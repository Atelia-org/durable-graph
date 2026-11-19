using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.Text;
using static Atelia.DurableGraph.Persistence.Tests.DB096TestAccess;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class RegisteredLeafLifecycleTests : IDisposable {
    private const string Prefix = "dg-leaf-lifecycle-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
    private static readonly string Original = new('a', 8192);

    [Fact]
    public void DiscardAndIndependentEventPreserveTheLastStateCopySource() {
        using RepositoryStorageFacade repo = RepositoryStorageFacade.CreateNew(_path);
        CanaryCodec codec = new();
        var models = Models(codec);
        BranchSaveSessionFixture session = new(repo, "leaf", models);
        LeafLifecycleHost host = new() { Body = new(Original), Canary = new() };
        using (var first = session.Prepare(host)) { first.Commit(); }
        Assert.True(codec.LastReceipt!.IsAccepted);
        var firstState = session.StateSource!;
        host.Body.Replace(17, 1, "X");
        using (var discarded = session.Prepare(host)) { Assert.NotEmpty(discarded.Business.Write.Records); }
        LeafAcceptance discardedReceipt = codec.LastReceipt!;
        Assert.False(discardedReceipt.IsAccepted);

        StoredGraphRevision domainEvent;
        using (var standalone = session.Prepare(host, StoredGraphKind.Event)) {
            Assert.Null(standalone.Business.Write.SourceGraph);
            Assert.All(standalone.Business.Write.Records, row => Assert.Equal(ObjectVersionKind.Base, row.Kind));
            Assert.Null(codec.LastReceipt);
            domainEvent = standalone.Commit().Business;
        }
        Assert.Same(firstState, session.StateSource);
        host.Body.Replace(18, 1, "Y");
        using (var next = session.Prepare(host)) {
            var text = Assert.Single(next.Business.Write.Records);
            AssertSingleReplacementDelta(text, firstState, 17, "XY");
            next.Commit();
        }
        Assert.False(discardedReceipt.IsAccepted);
        Assert.Equal(Original, Read(repo.Store, firstState, models).Body!.ToString());
        Assert.Equal(Original[..17] + "X" + Original[18..], Read(repo.Store, domainEvent, models).Body!.ToString());
        Assert.Equal(Original[..17] + "XY" + Original[19..], Read(repo.Store, session.StateSource!, models).Body!.ToString());
    }

    [Fact]
    public void AcceptingTheFrozenCaptureAfterAnEditCannotBindNewContentToTheOldPhysicalView() {
        using RepositoryStorageFacade repo = RepositoryStorageFacade.CreateNew(_path);
        var models = Models(new());
        BranchSaveSessionFixture session = new(repo, "stale", models);
        LeafLifecycleHost host = new() { Body = new(Original) };
        using (var first = session.Prepare(host)) { first.Commit(); }
        host.Body.Replace(11, 1, "X");
        string frozen = host.Body.ToString();
        using (var save = session.Prepare(host)) {
            AssertSingleReplacementDelta(Assert.Single(save.Business.Write.Records), session.StateSource!, 11, "X");
            host.Body.Replace(23, 0, "more");
            save.Commit();
        }
        var old = session.StateSource!;
        Assert.Equal(frozen, Read(repo.Store, old, models).Body!.ToString());
        Assert.Equal(frozen.Insert(23, "more"), host.Body.ToString());
        host.Body.Replace(61, 1, "Z"); // Forces consumption of any erroneously accepted old receipt.
        using (var later = session.Prepare(host)) { later.Commit(); }
        Assert.Equal(host.Body.ToString(), Read(repo.Store, session.StateSource!, models).Body!.ToString());
        Assert.Equal(frozen, Read(repo.Store, old, models).Body!.ToString());
    }

    [Fact]
    public void InterleavedGraphsSharingAnEditorUseTheirOwnPriorEvenWhenOffsetsAreInBounds() {
        var models = Models(new());
        using Repository repo = Repository.CreateNew(_path, models);
        TextEditor shared = new(Original);
        using var a = repo.CreateGraph(new LeafLifecycleHost { Body = shared });
        using var b = repo.CreateGraph(new LeafLifecycleHost { Body = shared });
        GraphRef a0 = repo.SaveGraph(a), b0 = repo.SaveGraph(b);
        shared.Replace(13, 1, "X");
        GraphRef a1 = repo.SaveGraph(a);
        shared.Replace(2048, 1, "Y");
        GraphRef b1 = repo.SaveGraph(b); // A's edited predecessor fits B's length but would copy the wrong prefix.
        GraphRef a2 = repo.SaveGraph(a);
        string withX = Original[..13] + "X" + Original[14..];
        string withBoth = withX[..2048] + "Y" + withX[2049..];
        foreach (var (graph, expected) in new[] { (a0, Original), (b0, Original), (a1, withX), (b1, withBoth), (a2, withBoth) }) {
            using var read = repo.OpenGraph(graph);
            Assert.Equal(expected, ((LeafLifecycleHost)read.Root).Body!.ToString());
        }
        var before = Heap(repo, b0);
        var after = Heap(repo, b1);
        uint textId = Assert.Single(after.ObjectHeads.Keys, id => id != after.RootId.Value);
        var leaf = repo.GraphStore.ReadObjectChain(after, textId).Entries[^1].Record;
        if (leaf.Kind == ObjectVersionKind.Delta) {
            Assert.Equal(before.ObjectHeads[textId], leaf.PriorAddress);
        } else { Assert.Equal(ObjectVersionKind.Base, leaf.Kind); }
    }

    [Fact]
    public void FailedCodecPreparationCannotReenterTheRepositoryOrAcceptItsCapture() {
        CanaryCodec codec = new();
        var models = Models(codec);
        using Repository repo = Repository.CreateNew(_path, models);
        LeafLifecycleHost host = new() { Body = new(Original), Canary = new() };
        using var builder = repo.CreateGraph(host);
        GraphRef prior = repo.SaveGraph(builder);
        host.Body.Replace(47, 1, "X");
        host.Canary.Value++;
        long frames = repo.GraphStore.AppendedFrames;
        var tip = Resources(repo).ReadHeapTip();
        var error = new FormatException("codec failed before publication");
        codec.BeforeDelta = () => {
            Assert.Throws<InvalidOperationException>(() => repo.SaveGraph(builder));
            throw error;
        };
        Assert.Same(error, Assert.Throws<FormatException>(() => repo.SaveGraph(builder)));
        var rejected = codec.LastReceipt!;
        Assert.False(rejected.IsAccepted);
        Assert.Equal(frames, repo.GraphStore.AppendedFrames);
        Assert.Equal(tip, Resources(repo).ReadHeapTip());
        Assert.False(repo.IsFaulted);
        codec.BeforeDelta = null;
        var updated = repo.SaveGraph(builder);
        Assert.True(codec.LastReceipt!.IsAccepted);
        Assert.False(rejected.IsAccepted);
        using var fresh = repo.OpenGraph(updated);
        Assert.Equal(Original[..47] + "X" + Original[48..], ((LeafLifecycleHost)fresh.Root).Body!.ToString());
        using var previous = repo.OpenGraph(prior);
        Assert.Equal(Original, ((LeafLifecycleHost)previous.Root).Body!.ToString());
    }

    [Theory]
    [InlineData((int)RepositorySaveStage.BeforePublication, RepositoryPublicationOutcome.NotPublished)]
    [InlineData((int)RepositorySaveStage.BeforeInstall, RepositoryPublicationOutcome.Published)]
    public void FailedPublicationStageLeavesReceiptUnacceptedAndColdReadReflectsActualTip(int stage, RepositoryPublicationOutcome outcome) {
        CanaryCodec codec = new();
        LeafLifecycleHost host = new() { Body = new(Original), Canary = new() };
        using (Repository repo = Repository.CreateNew(_path, Models(codec))) {
            using var graph = repo.CreateGraph(host);
            repo.SaveGraph(graph);
            host.Body.Replace(73, 1, "X");
            repo.SaveStageObserver = current => { if ((int)current == stage) throw new IOException("injected"); };
            var error = Assert.Throws<RepositoryPublicationException>(() => repo.SaveGraph(graph));
            Assert.Equal(outcome, error.Outcome);
            Assert.True(repo.IsFaulted);
            Assert.False(codec.LastReceipt!.IsAccepted);
            Assert.Equal('X', host.Body.ToString()[73]);
        }
        using var reopened = Repository.OpenExisting(_path, Models(new()));
        var tip = Resources(reopened).ReadHeapTip();
        using var actual = reopened.OpenGraph(new GraphRef(new CommitId(tip)));
        Assert.Equal(outcome == RepositoryPublicationOutcome.Published ? 'X' : 'a', ((LeafLifecycleHost)actual.Root).Body!.ToString()[73]);
    }

    [Fact]
    public void RequiredBaseWithoutADeltaDiffersFromAnUnchangedDelta() {
        CanaryCodec codec = new();
        using var repo = Repository.CreateNew(_path, Models(codec));
        LeafLifecycleHost host = new() { Canary = new() };
        using var graph = repo.CreateGraph(host);
        var first = Heap(repo, repo.SaveGraph(graph));
        uint leafId = Assert.Single(first.ObjectHeads.Keys, id => id != first.RootId.Value);
        var unchanged = Heap(repo, repo.SaveGraph(graph));
        Assert.Null(unchanged.OwnedBatch);
        codec.BaseOnly = true;
        host.Canary.Value = 99;
        var changed = repo.SaveGraph(graph);
        var records = repo.GraphStore.ReadBatch(Heap(repo, changed).OwnedBatch!.Value).Records;
        var persisted = Assert.Single(records);
        Assert.Equal(leafId, persisted.ObjectId);
        Assert.Equal(ObjectVersionKind.Base, persisted.Kind);
        using var read = repo.OpenGraph(changed);
        Assert.Equal((byte)99, ((LeafLifecycleHost)read.Root).Canary!.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ColdReadRejectsALeafReaderThatDoesNotConsumeTheWholeRecord(bool leaveDelta) {
        GraphRef first, second;
        using (var repo = Repository.CreateNew(_path, Models(new()))) {
            LeafLifecycleHost host = new() { Canary = new() };
            using var graph = repo.CreateGraph(host);
            first = repo.SaveGraph(graph);
            host.Canary.Value = 9;
            second = repo.SaveGraph(graph);
            var stored = Heap(repo, second);
            var leaf = Assert.Single(repo.GraphStore.ReadBatch(stored.OwnedBatch!.Value).Records);
            Assert.Equal(ObjectVersionKind.Delta, leaf.Kind);
        }
        CanaryCodec partial = new() { LeaveBaseUnread = !leaveDelta, LeaveDeltaUnread = leaveDelta };
        using (var repo = Repository.OpenExisting(_path, Models(partial))) {
            Assert.Throws<InvalidDataException>(() => repo.OpenGraph(leaveDelta ? second : first));
            Assert.True(partial.BaseReads > 0);
            if (leaveDelta) Assert.True(partial.DeltaReads > 0);
        }
        using var reopened = Repository.OpenExisting(_path, Models(new()));
        using var good = reopened.OpenGraph(second);
        Assert.Equal((byte)9, ((LeafLifecycleHost)good.Root).Canary!.Value);
    }

    [Fact]
    public void LeafReaderRequiresTheDeclaredNominalVersionAndCompleteShape() {
        var models = Models(new()).FreezeConfiguration().CreateEnvironment();
        const string id = "Probe.LifecycleCanary";
        var reader = models.ResolveReader(new DurableSchema(id, 1));
        Assert.Equal(1, reader.Schema.Version);
        Assert.Throws<InvalidDataException>(() => reader.Read(default, new UnreadBodies(1)));
        Assert.Throws<InvalidDataException>(() => reader.Read(new(1), new UnreadBodies(0)));
        Assert.Throws<InvalidDataException>(() => models.ResolveReader(new DurableSchema(id, 2)));
        Assert.Throws<InvalidDataException>(() => models.ResolveReader(new DurableSchema(id, 1, new DurableFieldInfo(1, TypeTag.Int32))));
    }

    [Fact]
    public void HistoricalBaseAndDeltaUseTheirOwnVersionBeforeCurrentBaseRewrite() {
        StateModelRegistry v1 = new();
        v1.RegisterLeaf(new CanaryCodec());
        var box = global::Atelia.DurableGraph.Generated.Family_546578745769746E657373426F78.Definition;
        v1.Register(box);
        GraphRef old, rewritten;
        uint leafId;
        using (var repo = Repository.CreateNew(_path, v1)) {
            var host = new TextWitnessBox<LifecycleCanary> { Value = new() { Value = 5 } };
            using var builder = repo.CreateGraph(host);
            repo.SaveGraph(builder);
            host.Value.Value = 9;
            old = repo.SaveGraph(builder);
            var record = Assert.Single(repo.GraphStore.ReadBatch(Heap(repo, old).OwnedBatch!.Value).Records);
            Assert.Equal(ObjectVersionKind.Delta, record.Kind);
            leafId = record.ObjectId;
        }

        var current = new CanaryV2Codec();
        StateModelRegistry v2 = new();
        v2.RegisterLeaf(current);
        v2.Register(box);
        using (var repo = Repository.OpenExisting(_path, v2)) {
            using var graph = repo.OpenGraph(old);
            var restored = Assert.IsType<TextWitnessBox<LifecycleCanaryV2>>(graph.Root);
            Assert.Equal(1009, restored.Value.Value);
            Assert.Contains(1, current.BaseVersions);
            Assert.Contains(1, current.DeltaVersions);
            rewritten = repo.SaveGraph(graph); // No domain edit. A current-format Base still has to be published.
            var records = repo.GraphStore.ReadBatch(Heap(repo, rewritten).OwnedBatch!.Value).Records;
            Assert.Equal(ObjectVersionKind.Base, Assert.Single(records, row => row.ObjectId == leafId).Kind);
        }
        current.BaseVersions.Clear();
        current.DeltaVersions.Clear();
        using (var repo = Repository.OpenExisting(_path, v2)) {
            using var graph = repo.OpenGraph(rewritten);
            Assert.Equal(1009, ((TextWitnessBox<LifecycleCanaryV2>)graph.Root).Value.Value);
            Assert.Equal(new[] { 2 }, current.BaseVersions);
            Assert.Empty(current.DeltaVersions);
        }
        using var oldRepo = Repository.OpenExisting(_path, v1);
        using var oldGraph = oldRepo.OpenGraph(old);
        Assert.Equal((byte)9, ((TextWitnessBox<LifecycleCanary>)oldGraph.Root).Value.Value);
    }

    private static void AssertSingleReplacementDelta(ObjectVersionRecord record, StoredGraphRevision prior, int start, string replacement) {
        Assert.Equal(ObjectVersionKind.Delta, record.Kind);
        Assert.Equal(prior.ObjectHeads[record.ObjectId], record.PriorAddress);
        BareValueReader reader = new(record.Body);
        Assert.Equal((uint)Original.Length, reader.ReadVarUInt32());
        Assert.Equal(1, reader.ReadByte());
        Assert.Equal(0u, reader.ReadVarUInt32());
        Assert.Equal((uint)start, reader.ReadVarUInt32());
        Assert.Equal(2, reader.ReadByte());
        Assert.Equal((uint)replacement.Length, reader.ReadVarUInt32());
        foreach (char c in replacement) Assert.Equal(c, reader.ReadCharLE());
        int end = start + replacement.Length;
        Assert.Equal(1, reader.ReadByte());
        Assert.Equal((uint)end, reader.ReadVarUInt32());
        Assert.Equal((uint)(Original.Length - end), reader.ReadVarUInt32());
        reader.EnsureFullyConsumed();
    }

    private static StateModelRegistry Models(CanaryCodec codec) {
        StateModelRegistry models = new();
        TextEditorPersistence.Register(models);
        models.RegisterLeaf(codec);
        models.Register(global::Atelia.DurableGraph.Generated.Family_4C6561664C6966656379636C65486F7374.Definition);
        return models;
    }
    private static LeafLifecycleHost Read(GraphRecordStore store, StoredGraphRevision state, StateModelRegistry models) =>
        Assert.IsType<LeafLifecycleHost>(BusinessPersistenceAdapter.ReadRoot(store, state, models));
    private static StoredGraphRevision Heap(Repository repo, GraphRef reference) =>
        repo.GraphStore.ReadBusinessGraph(Resources(repo).ReadHeapDescriptor(reference.CommitId.Value).Address);

    private sealed class UnreadBodies(int count) : IStateBodySource {
        public int Count => count;
        public ReadOnlySpan<byte> GetBody(int index) => throw new InvalidOperationException("The reader must reject before consuming a body.");
    }

    private sealed class CanaryCodec : DurableLeafCodec<LifecycleCanary, byte> {
        internal LeafAcceptance? LastReceipt;
        internal Action? BeforeDelta;
        internal bool BaseOnly, LeaveBaseUnread, LeaveDeltaUnread;
        internal int BaseReads, DeltaReads;
        public override byte Capture(LifecycleCanary value, LeafAcceptance? acceptance) {
            LastReceipt = acceptance;
            return value.Value;
        }
        public override LifecycleCanary Restore(byte state) => new() { Value = state };
        public override PreparedBaseBody PrepareBase(byte state) {
            byte[] bytes = new byte[256]; // Ensure the normal changed one-byte value selects Delta.
            bytes[0] = state;
            return new(bytes);
        }
        public override PreparedDeltaBody? PrepareDelta(byte previous, byte current) {
            BeforeDelta?.Invoke();
            return BaseOnly ? null : new(previous != current, [current, 0]);
        }
        public override byte ReadBase(int version, ref BareValueReader reader) {
            BaseReads++;
            byte value = reader.ReadByte();
            reader.ReadRawBytes(LeaveBaseUnread ? 254 : 255);
            return value;
        }
        public override byte ApplyDelta(int version, ref BareValueReader reader, byte previous) {
            DeltaReads++;
            byte next = reader.ReadByte();
            if (!LeaveDeltaUnread) reader.ReadByte();
            return next;
        }
    }

    private sealed class CanaryV2Codec : DurableLeafCodec<LifecycleCanaryV2, int> {
        public override IReadOnlyList<int> HistoricalVersions => [1];
        internal List<int> BaseVersions = [], DeltaVersions = [];
        public override int Capture(LifecycleCanaryV2 value, LeafAcceptance? reception) => value.Value;
        public override LifecycleCanaryV2 Restore(int state) => new() { Value = state };
        public override PreparedBaseBody PrepareBase(int state) {
            byte[] bytes = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes, state);
            return new(bytes);
        }
        public override int ReadBase(int version, ref BareValueReader reader) {
            BaseVersions.Add(version);
            if (version == 2) return reader.ReadInt32LE();
            Assert.Equal(1, version);
            int value = 1000 + reader.ReadByte();
            reader.ReadRawBytes(255);
            return value;
        }
        public override int ApplyDelta(int version, ref BareValueReader reader, int prior) {
            DeltaVersions.Add(version);
            Assert.Equal(1, version);
            Assert.Equal(1005, prior);
            int value = 1000 + reader.ReadByte();
            Assert.Equal(0, reader.ReadByte());
            return value;
        }
    }

    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}

[DurableType("LeafLifecycleHost", 1)]
public sealed partial class LeafLifecycleHost : IDurableObject {
    [DurableField(1)] public TextEditor? Body;
    [DurableField(2)] public LifecycleCanary? Canary;
}

[DurableLeaf("Probe.LifecycleCanary", 1)]
public sealed class LifecycleCanary {
    public byte Value;
}

[DurableLeaf("Probe.LifecycleCanary", 2)]
public sealed class LifecycleCanaryV2 {
    public int Value;
}
