using System.Buffers;
using System.Reflection;
using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Xunit.Abstractions;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class BusinessSourceValidationCostTests(ITestOutputHelper output) : IDisposable {
    private const string Prefix = "dg-business-source-cost-";
    private readonly List<string> _paths = [];

    [Theory]
    [InlineData(1000, false)]
    [InlineData(10000, false)]
    [InlineData(1000, true)]
    [InlineData(10000, true)]
    public void PrepareOwnedQualifiesStateOnceAndIndependentEventNever(int liveCount, bool domainEvent) {
        Harness harness = new();
        using RepositoryStorageFacade repository = RepositoryStorageFacade.CreateNew(NewPath());
        BranchSaveSessionFixture session = new(repository, "main", harness.Models);
        // Exactly N nodes, each containing only int Value and a nominal Next ID.
        // No strings or containers create additional live objects.
        Node root = Chain(liveCount);
        using (PreparedFixtureSave seed = session.Prepare(root)) seed.Commit();
        Assert.Equal(liveCount, session.Baseline!.Objects.Count);
        root.Value++; // Fixed k=1 change, independent of N.
        GraphRecordStore store = repository.Store;
        NormalizedGraphState baseline = session.Baseline;
        StoredGraphRevision source = session.StateSource!;
        long appended = store.AppendedFrames;
        StoredGraphKind kind = domainEvent ? StoredGraphKind.Event : StoredGraphKind.State;

        // Both samples use the same source and warm physical cache. Capture and catalog
        // preparation are outside the counter/allocation window; only PrepareOwned is measured.
        for (int sample = 0; sample < 2; sample++) {
            BusinessSaveCandidate capture = session.Session.CaptureOwned(root, kind);
            PreparedSchemaCatalogWrite catalog = repository.CatalogForPreparation.PrepareOwned(
                capture.Graph.Objects.Select(static row => row.Layout));
            harness.BodyCalls = 0;
            long qualifications = store.BusinessGraphQualificationCount;
            long allocation = GC.GetAllocatedBytesForCurrentThread();
            using PreparedBusinessWrite write = session.Session.PrepareOwned(capture, catalog.View, new(5, 5));
            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocation;
            long completeChecks = store.BusinessGraphQualificationCount - qualifications;
            output.WriteLine($"live={liveCount}, kind={kind}, sample={sample}, qualifications={completeChecks}, " +
                $"PrepareOwned allocated={allocated}, body calls={harness.BodyCalls}");
            Assert.Equal(domainEvent ? 0 : 1, completeChecks);
            Assert.True(harness.BodyCalls >= liveCount);
            Assert.Equal(domainEvent ? liveCount : 1, write.Write.Records.Count);
            if (domainEvent) {
                Assert.Null(write.Write.SourceGraph);
                Assert.All(write.Write.Records, row => Assert.Equal(ObjectVersionKind.Base, row.Kind));
            }
            Assert.Empty(write.Write.RemovedIds);
            Assert.Same(baseline, session.Baseline);
            Assert.Same(source, session.StateSource);
            Assert.Equal(appended, store.AppendedFrames);
        }
        Assert.False(repository.HasPending);
        Assert.False(repository.IsFaulted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnlyStateValidatesAnUnrelatedRemovedBaselineMemberBeforeBodyCallbacks(bool domainEvent) {
        Harness harness = new();
        using RepositoryStorageFacade repository = RepositoryStorageFacade.CreateNew(NewPath());
        BranchSaveSessionFixture session = new(repository, "main", harness.Models);
        Node root = Chain(2);
        using (PreparedFixtureSave seed = session.Prepare(root)) seed.Commit();
        NormalizedGraphState baseline = session.Baseline!;
        ObjectId child = baseline.Objects[session.RootId].Current.GetState<NodeState>().Next;
        Dictionary<ObjectId, NormalizedObject> rows = MutableRows(baseline);
        NormalizedObject original = rows[child];
        rows[child] = original with { Storage = null };
        root.Next = null;
        harness.BodyCalls = 0;
        long appended = repository.Store.AppendedFrames;
        StoredGraphKind kind = domainEvent ? StoredGraphKind.Event : StoredGraphKind.State;
        try {
            if (domainEvent) {
                using PreparedFixtureSave independent = session.Prepare(root, kind);
                Assert.Null(independent.Business.Write.SourceGraph);
                Assert.Empty(independent.Business.Write.RemovedIds);
                Assert.Single(independent.Business.Write.Records);
                Assert.True(harness.BodyCalls > 0);
            } else {
                Assert.Throws<InvalidDataException>(() => session.Prepare(root, kind));
                Assert.Equal(0, harness.BodyCalls);
            }
            Assert.Equal(appended, repository.Store.AppendedFrames);
            Assert.Same(baseline, session.Baseline);
            Assert.False(repository.HasPending);
            Assert.False(repository.IsFaulted);
        } finally { rows[child] = original; }
        using PreparedFixtureSave retry = session.Prepare(root, kind);
        if (domainEvent) { Assert.Empty(retry.Business.Write.RemovedIds); }
        else { Assert.Contains(child.Value, retry.Business.Write.RemovedIds); }
        Assert.True(harness.BodyCalls > 0);
    }

    [Fact]
    public void BodyFailureLeavesTheOldBaselineAndReleasesTheActualProducerForRetry() {
        Harness harness = new();
        using RepositoryStorageFacade repository = RepositoryStorageFacade.CreateNew(NewPath());
        BranchSaveSessionFixture session = new(repository, "main", harness.Models);
        Node root = Chain(2);
        using (PreparedFixtureSave seed = session.Prepare(root)) seed.Commit();
        NormalizedGraphState baseline = session.Baseline!;
        long appended = repository.Store.AppendedFrames;
        IOException sentinel = new("body preparation failed");
        harness.BeforeBody = () => throw sentinel;
        root.Value++;
        Assert.Same(sentinel, Assert.Throws<IOException>(() => session.Prepare(root)));
        Assert.Same(baseline, session.Baseline);
        Assert.Equal(appended, repository.Store.AppendedFrames);
        Assert.False(repository.HasPending);
        Assert.False(repository.IsFaulted);
        harness.BeforeBody = null;
        using PreparedFixtureSave retry = session.Prepare(root);
        retry.Commit();
        Assert.NotSame(baseline, session.Baseline);
        Assert.Equal(root.Value, session.Baseline!.Objects[session.RootId].Current.GetState<NodeState>().Value);
    }

    [Fact]
    public void ABodyCallbackFaultingTheOwnerIsRejectedByTheRealProducerBeforeAnyAppend() {
        Harness harness = new();
        string path = NewPath();
        using (RepositoryStorageFacade repository = RepositoryStorageFacade.CreateNew(path)) {
            BranchSaveSessionFixture session = new(repository, "main", harness.Models);
            Node root = Chain(2);
            using (PreparedFixtureSave seed = session.Prepare(root)) seed.Commit();
            NormalizedGraphState baseline = session.Baseline!;
            GraphRecordStore store = repository.Store;
            long appended = store.AppendedFrames;
            RepositoryResources resources = MechanismFixture.Resources(repository);
            harness.BodyCalls = 0;
            harness.BeforeBody = () => {
                harness.BeforeBody = null;
                resources.MarkFaulted();
            };
            root.Value++;
            Assert.ThrowsAny<InvalidOperationException>(() => session.Prepare(root));
            Assert.True(harness.BodyCalls > 0);
            Assert.True(repository.IsFaulted);
            Assert.Same(baseline, session.Baseline);
            Assert.Equal(appended, store.AppendedFrames);
        }
        using RepositoryStorageFacade reopened = RepositoryStorageFacade.OpenReadOnlyExisting(path);
        BranchSaveSessionFixture fresh = new(reopened, "main", harness.Models);
        Assert.Equal(0, Assert.IsType<Node>(fresh.State).Value); // The failed in-memory edit was never published.
    }

    private static Dictionary<ObjectId, NormalizedObject> MutableRows(NormalizedGraphState baseline) =>
        Assert.IsType<Dictionary<ObjectId, NormalizedObject>>(typeof(NormalizedGraphState)
            .GetField("_objects", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(baseline));

    private static Node Chain(int count) {
        Node? next = null;
        for (int index = count - 1; index >= 0; index--) next = new Node { Value = index, Next = next };
        return next!;
    }

    private sealed class Node : IDurableObject {
        internal int Value;
        internal Node? Next;
    }

    private readonly record struct NodeState(int Value, ObjectId Next);

    private sealed class Harness {
        private const string SchemaId = "source-cost.node";
        private static readonly DurableSchema Schema = new(SchemaId, 1,
            new DurableFieldInfo(1, TypeTag.Int32), new DurableFieldInfo(2, TypeTag.ObjectReference, SchemaId));
        internal StateModelRegistry Models { get; } = new();
        internal int BodyCalls;
        internal Action? BeforeBody;

        internal Harness() {
            StatePreparationBinding<NodeState> preparation = new(Schema, PrepareBase, PrepareDelta,
                static (in NodeState left, in NodeState right) => left == right);
            StateReaderBinding<NodeState> reader = new(Schema, Read,
                static (ref BareValueReader input, in NodeState prior) => {
                    NodeState current = Read(ref input);
                    if (current == prior) throw new InvalidDataException("A child patch must change its prior.");
                    return current;
                }, Visit);
            Models.Register(new StateModelBinding<Node, NodeState>(preparation, [reader],
                static source => source.GetState<NodeState>(), static () => new Node(),
                static (Node target, in NodeState state, ObjectReadTable objects) => {
                    target.Value = state.Value;
                    target.Next = objects.ResolveDurable<Node>(state.Next);
                }, static (value, capture) => new(value.Value, capture.CaptureDurable(value.Next, SchemaId)), Visit));
        }

        private PreparedBaseBody PrepareBase(in NodeState value) {
            BodyCalls++;
            BeforeBody?.Invoke();
            return new(Encode(in value));
        }

        private PreparedDeltaBody PrepareDelta(in NodeState prior, in NodeState current) {
            BodyCalls++;
            BeforeBody?.Invoke();
            return prior == current ? new(false, []) : new(true, Encode(in current));
        }

        private static byte[] Encode(in NodeState state) {
            ArrayBufferWriter<byte> buffer = new();
            BareValueWriter writer = new(buffer);
            writer.WriteVarInt32(state.Value);
            writer.WriteVarUInt32(state.Next.Value);
            return buffer.WrittenSpan.ToArray();
        }

        private static NodeState Read(ref BareValueReader reader) => new(reader.ReadVarInt32(), new(reader.ReadVarUInt32()));
        private static void Visit(in NodeState state, IStateReferenceVisitor visitor) => visitor.VisitDurable(state.Next, SchemaId);
    }

    private string NewPath() {
        string path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
        _paths.Add(path);
        return path;
    }

    public void Dispose() {
        foreach (string path in _paths) SharedReadModel.DeleteFixture(path, Prefix);
    }
}
