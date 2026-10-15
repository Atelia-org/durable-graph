using System.Buffers;
using Atelia.Binary;

using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using Atelia.VersionStore;
using Frames = Atelia.FrameStore.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class SchemaWriteAdmissionTests : IDisposable {
    private const string Prefix = "dg-schema-write-admission-";
    private readonly List<string> _paths = [];
    private static readonly DurableSchema Extra = new("SchemaWriteAdmission.Extra", 1, new DurableFieldInfo(1, TypeTag.Byte));

    [Fact]
    public void WritableCreationNeedsNoOpenModeAndExplicitSharedCreatesStateAndEventBranches() {
        string path = NewPath();
        using (Repository repository = Repository.CreateNew(path, Models())) {
            Assert.False(repository.IsReadOnly);
            PublicationSnapshot before = Snapshot(repository);
            Assert.Empty(before.Frames);
            Assert.Empty(RootMapCodec.Decode(before.Branches.Payload[4..]).Unwrap());
            Assert.Empty(RootMapCodec.Decode(before.Shared.Payload[4..]).Unwrap());
            using BranchCheckout main = repository.CreateSharedBranch("main", Ring());
            using BranchCheckout eventOnly = repository.CreateSharedBranchFromEvent("event-only", Ring());
            AssertRing(main.State, 7);
            Assert.Null(eventOnly.State);
            Assert.Equal(CheckpointKind.Event, eventOnly.Head.Kind);
            Assert.False(main.UsesPrivateSchemas);
            Assert.False(eventOnly.UsesPrivateSchemas);
        }
        using Repository reader = Repository.OpenReadOnlyExisting(path, Models());
        AssertRing(reader.ReadState(reader.GetHead("main")), 7);
        AssertRing(reader.ReadEvent(reader.GetHead("event-only")), 7);
    }

    [Fact]
    public void ColdSharedCheckoutSavesDeltaWithoutOpenModeAndPreservesIdentityAndCompletedFrames() {
        string path = NewPath();
        FrameAddress seed, schema;
        ObjectId rootId, listId;
        ObjectStorageInfo originalList;
        FrameImage[] completed;
        using (Repository repository = Repository.CreateNew(path, Models())) {
            using BranchCheckout main = repository.CreateSharedBranch("main", Ring());
            repository.CreateTag("initial", main.Head);
            seed = main.Head.RevisionAddress;
            schema = repository.SharedSchemaRoot!.Value;
            rootId = main.StateId!.Value;
            listId = Assert.Single(main.Baseline!.Objects, pair => pair.Value.Current.Kind == ObjectStateKind.List).Key;
            originalList = main.Baseline.Objects[listId].Storage!.Value;
            completed = ReadFrames(ResourcesOf(repository).Data);
        }

        FrameAddress continued;
        using (Repository repository = Repository.OpenExisting(path, Models())) {
            using BranchCheckout main = repository.CheckoutShared("main");
            DB087Widget root = AssertRing(main.State, 7);
            Assert.Equal(rootId, main.StateId);
            Assert.Equal(originalList, main.Baseline!.Objects[listId].Storage);
            RefBytesSnapshot sharedBefore = ResourcesOf(repository).Versions.ReadRef(2).Unwrap();
            root.Numbers[7] = 201;
            continued = main.CommitState().RevisionAddress;
            StoredGraphRevision graph = repository.GraphStore.ReadGraph(continued);
            Assert.Equal(seed, graph.Parent);
            Assert.Equal(seed, graph.SourceGraph);
            Assert.Equal(schema, graph.SchemaRoot);
            Assert.Equal(rootId, graph.RootId);
            Assert.Equal(3, graph.ObjectHeads.Count);
            ObjectVersionRecord delta = Assert.Single(repository.GraphStore.ReadBatch(graph.OwnedBatch!.Value).Records);
            Assert.Equal(listId.Value, delta.ObjectId);
            Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
            Assert.Equal(originalList.Head, delta.PriorAddress);
            Assert.Equal(originalList.ReconstructionPayloadBytes + delta.EncodedPayloadBytes!.Value,
                main.Baseline!.Objects[listId].Storage!.Value.ReconstructionPayloadBytes);
            AssertSameRef(sharedBefore, ResourcesOf(repository).Versions.ReadRef(2).Unwrap());
            AssertFramesPreserved(ResourcesOf(repository).Data, completed);
        }
        using Repository reader = Repository.OpenReadOnlyExisting(path, Models());
        AssertRing(reader.ReadState(reader.ResolveTag("initial")), 7);
        AssertRing(reader.ReadState(reader.GetHead("main")), 201);
        Assert.Equal(continued, reader.GetHead("main").RevisionAddress);
        AssertFramesPreserved(ResourcesOf(reader).Data, completed);
    }

    [Fact]
    public void ExplicitSharedSessionSupportsOrdinarySaveFormsAndManualIntentWithoutOpenMode() {
        string path = NewPath();
        int captures = 0;
        using (Repository seed = Repository.CreateNew(path, CaptureModels(() => captures++))) {
            using BranchCheckout main = seed.CreateSharedBranch("main", new CaptureRoot { Value = 17 });
        }
        Assert.Equal(1, captures);
        captures = 0;
        using Repository repository = Repository.OpenExisting(path, CaptureModels(() => captures++));
        using BranchCheckout checkout = repository.CheckoutShared("main");
        CaptureRoot root = Assert.IsType<CaptureRoot>(checkout.State);
        Assert.Equal(17, root.Value);
        Assert.Equal(0, captures);
        checkout.CommitState(); // An unchanged ordinary save also has a definite destination.
        Assert.Equal(1, captures);
        root.Value = 18;
        checkout.CommitState();
        Assert.Equal(2, captures);
        checkout.CommitState(new CaptureRoot { Value = 19 });
        Assert.Equal(3, captures);
        root = Assert.IsType<CaptureRoot>(checkout.State);
        checkout.CommitEvent(root);
        Assert.Equal(4, captures);
        checkout.PrepareAndDiscard(root);
        Assert.Equal(5, captures);
        using BranchCheckout newState = repository.CreateSharedBranch("new-state", root);
        Assert.Equal(6, captures);
        using BranchCheckout newEvent = repository.CreateSharedBranchFromEvent("new-event", root);
        Assert.Equal(7, captures);
        checkout.Catalog.Set(Extra);
        checkout.Catalog.Set(ObjectLayout.ForDurable(Extra));
        Assert.Equal(1, checkout.Catalog.DirtyCount);
        Assert.False(repository.IsFaulted);
    }

    [Fact]
    public void WritableOwnerRefOnlyOperationsDoNotAppendBusinessOrSchemaFrames() {
        string path = NewPath();
        FrameAddress initial, current, schema;
        using (Repository seed = Repository.CreateNew(path, Models())) {
            using BranchCheckout main = seed.CreateSharedBranch("main", Ring());
            initial = main.Head.RevisionAddress;
            seed.CreateTag("initial", main.Head);
            AssertRing(main.State, 7).Numbers[7] = 201;
            current = main.CommitState().RevisionAddress;
            schema = seed.SharedSchemaRoot!.Value;
        }
        using (Repository repository = Repository.OpenExisting(path, Models())) {

            CheckpointAddress old = repository.ResolveTag("initial"), latest = repository.GetHead("main");
            Assert.Equal(initial, old.RevisionAddress);
            Assert.Equal(current, latest.RevisionAddress);
            PublicationSnapshot before = Snapshot(repository);
            using (BranchCheckout main = repository.CheckoutShared("main")) {
                AssertRing(main.State, 201);
                Assert.Equal(old, repository.CreateBranch("plain", old));
                using BranchCheckout fork = repository.ForkShared("fork", old);
                AssertRing(fork.State, 7);
                Assert.Equal(old, fork.Head);
                Assert.NotSame(main.State, fork.State);
                Assert.Equal(current, main.StateRevisionAddress);
            }
            repository.MoveBranch("plain", old, latest);
            repository.CreateTag("bookmark", latest);
            Assert.Equal(latest, repository.GetHead("main"));
            Assert.Equal(latest, repository.GetHead("plain"));
            Assert.Equal(old, repository.GetHead("fork"));
            Assert.Equal(latest, repository.ResolveTag("bookmark"));
            Assert.Equal(schema, repository.SharedSchemaRoot);
            AssertSameRef(before.Shared, ResourcesOf(repository).Versions.ReadRef(2).Unwrap());
            AssertExactFrames(before.Frames, ReadFrames(ResourcesOf(repository).Data));
            Assert.Equal(before.Appended, repository.GraphStore.AppendedFrames);
            Assert.False(repository.IsFaulted);
            Assert.False(ResourcesOf(repository).HasPendingSave);
        }
        using Repository reader = Repository.OpenReadOnlyExisting(path, Models());
        Assert.Equal(current, reader.ResolveTag("bookmark").RevisionAddress);
        AssertRing(reader.ReadState(reader.GetHead("plain")), 201);
        AssertRing(reader.ReadState(reader.GetHead("fork")), 7);
    }

    [Fact]
    public void ReadonlyGuardStillRejectsAllGraphRefAndTagWritesWithoutMutation() {
        string path = NewPath();
        using (Repository seed = Repository.CreateNew(path, Models())) {
            using BranchCheckout main = seed.CreateSharedBranch("main", Ring());
        }
        Dictionary<string, byte[]> files = ReadFiles(path);
        using (Repository reader = Repository.OpenReadOnlyExisting(path, Models())) {
            Assert.True(reader.IsReadOnly);

            CheckpointAddress head = reader.GetHead("main");
            AssertRing(reader.ReadState(head), 7);
            PublicationSnapshot before = Snapshot(reader);
            Action[] writes = [
                () => reader.CheckoutShared("main"),
                () => reader.CheckoutPrivate("main"),
                () => reader.CreateSharedBranch("state", Ring()),
                () => reader.CreateSharedBranchFromEvent("event", Ring()),
                () => reader.CreateBranch("plain", head),
                () => reader.ForkShared("fork", head),
                () => reader.MoveBranch("main", head, head),
                () => reader.CreateTag("tag", head),
            ];
            foreach (Action write in writes) {
                Assert.Throws<InvalidOperationException>(write);
                AssertUnchanged(reader, before);
            }
        }
        AssertFilesUnchanged(files, ReadFiles(path));
    }

    private static StateModelRegistry Models() {
        StateModelRegistry models = new();
        models.Register(global::Atelia.DurableGraph.Generated.Family_5030576964676574.Definition);
        models.Register(global::Atelia.DurableGraph.Generated.Family_5030456E76656C6F7065.Definition);
        return models;
    }
    private static DB087Widget Ring() {
        List<int> numbers = Enumerable.Range(0, 128).ToList();
        DB087Widget root = new() { Value = 1, Numbers = numbers };
        root.Child = new() { Value = 2, Numbers = numbers, Child = root };
        return root;
    }
    private static DB087Widget AssertRing(IDurableObject? value, int atSeven) {
        DB087Widget root = Assert.IsType<DB087Widget>(value), child = Assert.IsType<DB087Widget>(root.Child);
        Assert.Equal(1, root.Value);
        Assert.Equal(2, child.Value);
        Assert.Same(root, child.Child);
        Assert.Same(root.Numbers, child.Numbers);
        Assert.Equal(128, root.Numbers.Count);
        for (int index = 0; index < 128; index++) { Assert.Equal(index == 7 ? atSeven : index, root.Numbers[index]); }
        return root;
    }

    private sealed class CaptureRoot : IDurableObject { internal int Value; }
    private static readonly DurableSchema CaptureSchema = new("SchemaWriteAdmission.Capture", 1, new DurableFieldInfo(1, TypeTag.Int32));
    private static StateModelRegistry CaptureModels(Action capture) {
        static void Visit(in int value, IStateReferenceVisitor visitor) { }
        static PreparedBaseBody Base(in int value) {
            ArrayBufferWriter<byte> buffer = new();
            BareValueWriter writer = new(buffer);
            writer.WriteVarInt32(value);
            return new(buffer.WrittenSpan);
        }
        StateModelRegistry models = new();
        models.Register(new StateModelBinding<CaptureRoot, int>(new(CaptureSchema, Base,
            static (in int prior, in int next) => new(prior != next, Base(next).Body)),
            [new StateReaderBinding<int>(CaptureSchema, static (ref BareValueReader input) => input.ReadVarInt32(),
                static (ref BareValueReader input, in int prior) => input.ReadVarInt32(), Visit)],
            static row => row.GetState<int>(), static () => new(),
            static (CaptureRoot domain, in int value, ObjectReadTable objects) => domain.Value = value,
            (domain, context) => { capture(); return domain.Value; }, Visit));
        return models;
    }

    private sealed record FrameImage(FrameAddress Address, uint Tag, int TailMetaLength, bool IsTombstone, byte[] PayloadAndMeta);
    private sealed record PublicationSnapshot(RefBytesSnapshot Branches, RefBytesSnapshot Shared, FrameImage[] Frames, long Appended);
    private static PublicationSnapshot Snapshot(Repository repository) {
        RepositoryResources resources = ResourcesOf(repository);
        return new(resources.Versions.ReadRef(1).Unwrap(), resources.Versions.ReadRef(2).Unwrap(),
            ReadFrames(resources.Data), repository.GraphStore.AppendedFrames);
    }
    private static void AssertUnchanged(Repository repository, PublicationSnapshot before) {
        RepositoryResources resources = ResourcesOf(repository);
        AssertSameRef(before.Branches, resources.Versions.ReadRef(1).Unwrap());
        AssertSameRef(before.Shared, resources.Versions.ReadRef(2).Unwrap());
        AssertExactFrames(before.Frames, ReadFrames(resources.Data));
        Assert.Equal(before.Appended, repository.GraphStore.AppendedFrames);
        Assert.False(resources.HasPendingSave);
        Assert.False(repository.IsFaulted);
    }
    private static void AssertSameRef(RefBytesSnapshot before, RefBytesSnapshot after) {
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.Payload.ToArray(), after.Payload.ToArray());
    }
    private static FrameImage[] ReadFrames(Frames data) {
        List<FrameInfo> frames = [];
        data.Inventory(frames.Add).Unwrap();
        return frames.Select(info => {
            using FrameRead read = data.ReadFrame(info.Address).Unwrap();
            return new FrameImage(info.Address, read.Tag, read.TailMetaLength, read.IsTombstone, read.PayloadAndMeta.ToArray());
        }).ToArray();
    }
    private static void AssertExactFrames(FrameImage[] before, FrameImage[] after) {
        Assert.Equal(before.Select(frame => frame.Address), after.Select(frame => frame.Address));
        foreach (FrameImage image in before) { AssertFrame(image, after.Single(frame => frame.Address == image.Address)); }
    }
    private static void AssertFramesPreserved(Frames data, IEnumerable<FrameImage> before) {
        FrameImage[] after = ReadFrames(data);
        foreach (FrameImage image in before) { AssertFrame(image, after.Single(frame => frame.Address == image.Address)); }
    }
    private static void AssertFrame(FrameImage before, FrameImage after) {
        Assert.Equal(before.Tag, after.Tag);
        Assert.Equal(before.TailMetaLength, after.TailMetaLength);
        Assert.Equal(before.IsTombstone, after.IsTombstone);
        Assert.Equal(before.PayloadAndMeta, after.PayloadAndMeta);
    }
    private static Dictionary<string, byte[]> ReadFiles(string path) => Directory.GetFiles(path, "*", SearchOption.AllDirectories)
        .ToDictionary(file => file, File.ReadAllBytes);
    private static void AssertFilesUnchanged(Dictionary<string, byte[]> before, Dictionary<string, byte[]> after) {
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach ((string file, byte[] bytes) in before) { Assert.Equal(bytes, after[file]); }
    }
    private string NewPath() {
        string path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
        _paths.Add(path);
        return path;
    }
    public void Dispose() {
        foreach (string path in _paths) { SharedReadModel.DeleteFixture(path, Prefix); }
    }
}
