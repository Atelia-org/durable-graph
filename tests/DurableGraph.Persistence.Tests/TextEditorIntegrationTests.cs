using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Serialization;
using Atelia.Text;
using Atelia.DurableGraph.Storage;
using static Atelia.DurableGraph.Persistence.Tests.DB096TestAccess;

namespace Atelia.DurableGraph.Persistence.Tests;

// Real generated models and product capture/planning/publication/typed restoration.
// Editor and wire validation tests belong to their component owners.
public sealed class TextEditorIntegrationTests : IDisposable {
    private const string Prefix = "dg-text-integration-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));

    [Fact]
    public void CapturedMultiPieceBaseIsPersistedBeforeCompactionAndColdDeltaContinuationUsesActualBytes() {
        string[] segments = Enumerable.Range(0, 64).Select(i => new string((char)('a' + i % 26), 64)).ToArray();
        segments[1] = new string('中', 64);
        segments[31] = new string('x', 63) + "\ud83d";
        segments[32] = "\ude00\0\r\n" + new string('y', 60);
        string original = string.Concat(segments);
        TextEditor text = new();
        foreach (string segment in segments) { text.Replace(text.Length, 0, segment); }
        var frozen = text.Snapshot();
        GraphRef first, changed;
        ObjectId textId;
        string changedContent = original[..2050] + "Z" + original[2051..];
        using (Repository repository = Repository.CreateNew(_path, Models())) {
            using GraphBuilder builder = repository.CreateGraph(new TextWitness { Body = text, Alias = text });
            first = repository.SaveGraph(builder);
            StoredGraphRevision initial = HeapSnapshot(repository, first);
            textId = TextId(repository.GraphStore, initial, original.Length);
            ObjectVersionChain chain = repository.GraphStore.ReadObjectChain(initial, textId.Value);
            var initialRecord = Assert.Single(chain.Entries);
            Assert.Equal(ObjectVersionKind.Base, initialRecord.Record.Kind);
            var catalog = SchemaCatalogView.Load(repository.GraphStore, initialRecord.SchemaRoot!.Value);
            var typed = TypedBaseBodyCodec.Decode(initialRecord.Record.Body, catalog);
            Assert.Equal("Atelia.Text.TextEditor", typed.Layout.Schema!.SchemaId);
            Assert.Equal(1, typed.Layout.Schema.Version);
            Assert.Empty(typed.Layout.Schema.Fields);
            BareValueReader reader = new(typed.Body);
            Assert.Equal((uint)original.Length, reader.ReadVarUInt32());
            List<string> storedSegments = [];
            while (!reader.End) { storedSegments.Add(reader.ReadString()); }
            Assert.Equal(segments, storedSegments);
            Assert.Equal(original, frozen.ToString());
            Assert.Equal((long)initialRecord.PayloadBytes,
                builder.Business.Baseline!.Objects[textId].Storage!.Value.ReconstructionPayloadBytes);
            AssertAccounting(repository.GraphStore, initial);
            text.Replace(2050, 1, "Z"); // The initial Copy spans many former physical segments.
            changed = repository.SaveGraph(builder);
            StoredGraphRevision next = HeapSnapshot(repository, changed);
            AssertDelta(repository.GraphStore, next, textId, initial.ObjectHeads[textId.Value]);
            Assert.Equal(repository.GraphStore.ReadObjectChain(next, textId.Value).Entries.Sum(entry => (long)entry.PayloadBytes),
                builder.Business.Baseline!.Objects[textId].Storage!.Value.ReconstructionPayloadBytes);
        }
        using Repository reopened = Repository.OpenExisting(_path, Models());
        using GraphBuilder loaded = reopened.OpenGraph(changed);
        TextWitness root = Assert.IsType<TextWitness>(loaded.Root);
        Assert.Same(root.Body, root.Alias);
        Assert.Equal(changedContent, root.Body!.ToString());
        root.Body.Replace(20, 1, "Q");
        GraphRef continued = reopened.SaveGraph(loaded);
        StoredGraphRevision final = HeapSnapshot(reopened, continued);
        AssertDelta(reopened.GraphStore, final, textId, HeapSnapshot(reopened, changed).ObjectHeads[textId.Value]);
        AssertAccounting(reopened.GraphStore, final);
        using GraphBuilder verify = reopened.OpenGraph(continued), old = reopened.OpenGraph(first);
        Assert.Equal(changedContent[..20] + "Q" + changedContent[21..], ((TextWitness)verify.Root).Body!.ToString());
        Assert.Equal(original, ((TextWitness)old.Root).Body!.ToString());
    }

    [Fact]
    public void GraphAliasesNullEmptyAndIndependentColdForksKeepTextIdentity() {
        string original = "\uD800\0\r\n" + new string('a', 4096) + "\uDC00";
        GraphRef first, changed, forked;
        ObjectId bodyId;
        TextSnapshot held;
        using (Repository repository = Repository.CreateNew(_path, Models())) {
            TextEditor body = new(original);
            held = body.Snapshot();
            TextWitness root = new() { Body = body, Alias = body, Empty = new(), Gist = "one line" };
            using GraphBuilder builder = repository.CreateGraph(root);
            first = repository.SaveGraph(builder);
            bodyId = TextId(repository.GraphStore, HeapSnapshot(repository, first), original.Length);
            body.Replace(100, 1, "X");
            root.Gist = "still a string";
            changed = repository.SaveGraph(builder);
            StoredGraphRevision next = HeapSnapshot(repository, changed);
            Assert.Equal(bodyId, TextId(repository.GraphStore, next, original.Length));
            AssertDelta(repository.GraphStore, next, bodyId, HeapSnapshot(repository, first).ObjectHeads[bodyId.Value]);
            Assert.Equal(original, held.ToString());
            using GraphBuilder fork = repository.OpenGraph(first);
            TextWitness forkRoot = Assert.IsType<TextWitness>(fork.Root);
            Assert.NotSame(body, forkRoot.Body);
            Assert.Same(forkRoot.Body, forkRoot.Alias);
            Assert.NotNull(forkRoot.Empty);
            Assert.Equal(0, forkRoot.Empty.Length);
            Assert.Null(forkRoot.Optional);
            forkRoot.Body!.Replace(101, 1, "Y");
            forked = repository.SaveGraph(fork);
            Assert.Equal(original[..100] + "X" + original[101..], body.ToString());
        }
        Assert.Equal(original, held.ToString()); // No borrowed Store/reader lifetime.
        using Repository reopened = Repository.OpenExisting(_path, Models());
        foreach ((GraphRef reference, string expected, string gist) in new[] {
            (first, original, "one line"),
            (changed, original[..100] + "X" + original[101..], "still a string"),
            (forked, original[..101] + "Y" + original[102..], "one line"),
        }) {
            using GraphBuilder loaded = reopened.OpenGraph(reference);
            TextWitness root = Assert.IsType<TextWitness>(loaded.Root);
            Assert.Equal(expected, root.Body!.ToString());
            Assert.Same(root.Body, root.Alias);
            Assert.Equal(gist, root.Gist);
            Assert.Null(root.Optional);
            Assert.Equal(string.Empty, root.Empty!.ToString());
        }
        using GraphBuilder left = reopened.OpenGraph(changed), right = reopened.OpenGraph(changed);
        TextWitness leftRoot = (TextWitness)left.Root, rightRoot = (TextWitness)right.Root;
        Assert.NotSame(leftRoot.Body, rightRoot.Body);
        leftRoot.Body!.Replace(102, 1, "Z");
        GraphRef continued = reopened.SaveGraph(left);
        Assert.Equal(original[..100] + "X" + original[101..], rightRoot.Body!.ToString());
        AssertDelta(reopened.GraphStore, HeapSnapshot(reopened, continued), bodyId,
            HeapSnapshot(reopened, changed).ObjectHeads[bodyId.Value]);
        AssertAccounting(reopened.GraphStore, HeapSnapshot(reopened, continued));
    }

    [Fact]
    public void SharedClrTextAcrossBuildersUsesEachActualPriorAndHandlesNetZero() {
        string original = new('a', 4096);
        using Repository repository = Repository.CreateNew(_path, Models());
        TextEditor text = new(original);
        using GraphBuilder one = repository.CreateGraph(new TextWitness { Body = text, Gist = "one" });
        using GraphBuilder two = repository.CreateGraph(new TextWitness { Body = text, Gist = "two" });
        GraphRef one0 = repository.SaveGraph(one), two0 = repository.SaveGraph(two);
        ObjectId oneId = TextId(repository.GraphStore, HeapSnapshot(repository, one0), original.Length);
        text.Replace(1, 0, "X");
        GraphRef one1 = repository.SaveGraph(one);
        text.Replace(2, 0, "Y");
        GraphRef two1 = repository.SaveGraph(two); // Its prior has neither X nor Y.
        GraphRef one2 = repository.SaveGraph(one); // Its prior has X but no Y.
        foreach ((GraphRef reference, string expected) in new[] {
            (one0, original), (two0, original),
            (one1, "aX" + original[1..]),
            (two1, "aXY" + original[1..]), (one2, "aXY" + original[1..]),
        }) {
            using GraphBuilder loaded = repository.OpenGraph(reference);
            Assert.Equal(expected, ((TextWitness)loaded.Root).Body!.ToString());
            AssertAccounting(repository.GraphStore, HeapSnapshot(repository, reference));
        }
        Assert.Equal(oneId, TextId(repository.GraphStore, HeapSnapshot(repository, one2), original.Length + 2));
        // Source belongs to the other builder, but content equality still permits reuse.
        text.Replace(20, 1, "z"); text.Replace(20, 1, "a");
        GraphRef two2 = repository.SaveGraph(two);
        Assert.Null(HeapSnapshot(repository, two2).OwnedBatch);
        Assert.Equal(HeapSnapshot(repository, two1).DirectoryRoot, HeapSnapshot(repository, two2).DirectoryRoot);
        text.Replace(21, 1, "q");
        GraphRef two3 = repository.SaveGraph(two);
        using GraphBuilder verify = repository.OpenGraph(two3);
        Assert.Equal(text.ToString(), ((TextWitness)verify.Root).Body!.ToString());
    }

    [Fact]
    public void DeltaAndNoChangeDoNotEncodeBaseAndSelectedBaseFailureCanRetry() {
        int encodes = 0;
        bool failBase = false;
        StateModelRegistry models = Models(() => {
            encodes++;
            if (failBase) { throw new InvalidOperationException("injected text Base encode failure"); }
        });
        using Repository repository = Repository.CreateNew(_path, models);
        TextEditor body = new(new string('a', 4096)), other = new(new string('b', 4096));
        using GraphBuilder graph = repository.CreateGraph(new TextWitness { Body = body, Other = other });
        GraphRef first = repository.SaveGraph(graph);
        TextSnapshot held = body.Snapshot();
        body.Replace(17, 1, "x");
        Assert.Equal(2, encodes);
        GraphRef second = repository.SaveGraph(graph);
        Assert.Equal(2, encodes); // A genuine small delta, not a size-estimation encode.
        body.Replace(17, 1, "a"); body.Replace(17, 1, "x");
        GraphRef unchanged = repository.SaveGraph(graph);
        Assert.Null(HeapSnapshot(repository, unchanged).OwnedBatch);
        Assert.Equal(2, encodes);
        body.Replace(18, 1, "y");
        other.Replace(0, other.Length, new string('c', 4096)); // Selected complete Base.
        long frames = repository.GraphStore.AppendedFrames;
        var tip = Resources(repository).ReadHeapTip();
        failBase = true;
        Assert.Throws<InvalidOperationException>(() => repository.SaveGraph(graph));
        Assert.Equal(frames, repository.GraphStore.AppendedFrames);
        Assert.Equal(tip, Resources(repository).ReadHeapTip());
        Assert.False(repository.IsFaulted);
        Assert.Equal(new string('a', 4096), held.ToString());
        failBase = false;
        GraphRef retry = repository.SaveGraph(graph);
        using GraphBuilder loaded = repository.OpenGraph(retry);
        TextWitness restored = (TextWitness)loaded.Root;
        Assert.Equal(body.ToString(), restored.Body!.ToString());
        Assert.Equal(new string('c', 4096), restored.Other!.ToString());
        AssertAccounting(repository.GraphStore, HeapSnapshot(repository, retry));
        using GraphBuilder prior = repository.OpenGraph(second);
        Assert.Equal('a', ((TextWitness)prior.Root).Body!.ToString()[18]);
        using GraphBuilder oldest = repository.OpenGraph(first);
        Assert.Equal(new string('a', 4096), ((TextWitness)oldest.Root).Body!.ToString());
    }

    [Fact]
    public void DiscardAndCaptureThenEditDoNotInstallStaleTextState() {
        using RepositoryStorageFacade repository = RepositoryStorageFacade.CreateNew(_path);
        BranchSaveSessionFixture session = new(repository, "text", Models());
        TextWitness root = new() { Body = new(new string('a', 4096)) };
        using (PreparedFixtureSave first = session.Prepare(root)) { first.Commit(); }
        FrameAddress firstAddress = session.StateSource!.Address;
        root.Body.Replace(1, 1, "x");
        using (PreparedFixtureSave discard = session.Prepare(root)) {
            Assert.NotEmpty(discard.Business.Write.Records);
        }
        Assert.Equal(firstAddress, session.StateSource!.Address);
        root.Body.Replace(2, 1, "y");
        string captured = root.Body.ToString();
        TextSnapshot held = root.Body.Snapshot();
        using (PreparedFixtureSave save = session.Prepare(root)) {
            ObjectId textId = TextId(repository.Store, session.StateSource!, 4096);
            ObjectVersionRecord delta = Assert.Single(save.Business.Write.Records, row => row.ObjectId == textId.Value);
            Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
            Assert.Equal(session.StateSource!.ObjectHeads[textId.Value], delta.PriorAddress);
            // The low-level protocol accepts the sealed graph without recapturing.
            root.Body.Replace(3, 1, "z");
            save.Commit();
        }
        StoredGraphRevision sealedGraph = session.StateSource!;
        Assert.Equal(captured, ReadRoot(repository.Store, sealedGraph).Body!.ToString());
        Assert.Equal('z', root.Body.ToString()[3]);
        Assert.Equal(captured, held.ToString());
        using (PreparedFixtureSave next = session.Prepare(root)) { next.Commit(); }
        Assert.Equal(root.Body.ToString(), ReadRoot(repository.Store, session.StateSource!).Body!.ToString());
        Assert.Equal(captured, ReadRoot(repository.Store, sealedGraph).Body!.ToString());
        AssertAccounting(repository.Store, session.StateSource!);
    }

    [Fact]
    public void IndependentEventDoesNotResetSharedTextStateSourceOrAccounting() {
        using RepositoryStorageFacade repository = RepositoryStorageFacade.CreateNew(_path);
        BranchSaveSessionFixture session = new(repository, "text", Models());
        TextWitness root = new() { Body = new(new string('a', 4096)) };
        using (PreparedFixtureSave first = session.Prepare(root)) { first.Commit(); }
        StoredGraphRevision state = session.StateSource!;
        ObjectId textId = TextId(repository.Store, state, 4096);
        long priorH = session.Baseline!.Objects[textId].Storage!.Value.ReconstructionPayloadBytes;
        root.Body.Replace(1, 1, "x");
        StoredGraphRevision domainEvent;
        using (PreparedFixtureSave save = session.Prepare(root, StoredGraphKind.Event)) {
            Assert.Null(save.Business.Write.SourceGraph);
            Assert.All(save.Business.Write.Records, row => Assert.Equal(ObjectVersionKind.Base, row.Kind));
            domainEvent = save.Commit().Business;
        }
        Assert.Equal(state.Address, session.StateSource!.Address);
        Assert.Equal(priorH, session.Baseline!.Objects[textId].Storage!.Value.ReconstructionPayloadBytes);
        root.Body.Replace(2, 1, "y");
        using (PreparedFixtureSave save = session.Prepare(root)) {
            ObjectVersionRecord delta = Assert.Single(save.Business.Write.Records, row => row.ObjectId == textId.Value);
            Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
            Assert.Equal(state.ObjectHeads[textId.Value], delta.PriorAddress);
            save.Commit();
        }
        Assert.Equal('a', ReadRoot(repository.Store, state).Body!.ToString()[1]);
        Assert.Equal("axa", ReadRoot(repository.Store, domainEvent).Body!.ToString()[..3]);
        Assert.Equal("axy", ReadRoot(repository.Store, session.StateSource!).Body!.ToString()[..3]);
        AssertAccounting(repository.Store, session.StateSource!);
    }

    [Theory]
    [InlineData((int)RepositorySaveStage.BeforePublication, RepositoryPublicationOutcome.NotPublished)]
    [InlineData((int)RepositorySaveStage.BeforeInstall, RepositoryPublicationOutcome.Published)]
    public void PublicationAndInstallationFailuresKeepDomainEditsAndActualDurableOutcome(int stage, RepositoryPublicationOutcome outcome) {
        GraphRef first;
        TextEditor text = new(new string('a', 4096));
        TextSnapshot held = text.Snapshot();
        using (Repository repository = Repository.CreateNew(_path, Models())) {
            using GraphBuilder graph = repository.CreateGraph(new TextWitness { Body = text });
            first = repository.SaveGraph(graph);
            text.Replace(10, 1, "x");
            repository.SaveStageObserver = point => { if ((int)point == stage) { throw new IOException("injected text publication failure"); } };
            RepositoryPublicationException failure = Assert.Throws<RepositoryPublicationException>(() => repository.SaveGraph(graph));
            Assert.Equal(outcome, failure.Outcome);
            Assert.True(repository.IsFaulted);
            Assert.Equal('x', text.ToString()[10]);
            Assert.Equal(new string('a', 4096), held.ToString());
        }
        using Repository reopened = Repository.OpenExisting(_path, Models());
        var tip = Resources(reopened).ReadHeapTip();
        using GraphBuilder actual = reopened.OpenGraph(new GraphRef(new CommitId(tip)));
        Assert.Equal(outcome == RepositoryPublicationOutcome.Published ? 'x' : 'a', ((TextWitness)actual.Root).Body!.ToString()[10]);
        using GraphBuilder old = reopened.OpenGraph(first);
        Assert.Equal('a', ((TextWitness)old.Root).Body!.ToString()[10]);
    }

    [Fact]
    public void ClosedGenericListAndDictionarySlotsPreserveOneTextAlias() {
        GraphRef saved;
        using (Repository repository = Repository.CreateNew(_path, Models())) {
            TextEditor text = new("generic\0\uD800");
            TextWitnessBox<TextEditor> root = new() { Value = text, Items = [text], Named = new() { ["same"] = text } };
            using GraphBuilder graph = repository.CreateGraph(root);
            saved = repository.SaveGraph(graph);
        }
        using Repository reopened = Repository.OpenExisting(_path, Models());
        using GraphBuilder loaded = reopened.OpenGraph(saved);
        TextWitnessBox<TextEditor> restored = Assert.IsType<TextWitnessBox<TextEditor>>(loaded.Root);
        Assert.Equal("generic\0\uD800", restored.Value.ToString());
        Assert.Same(restored.Value, restored.Items[0]);
        Assert.Same(restored.Value, restored.Named["same"]);
        restored.Value.Replace(0, 1, "G");
        GraphRef next = reopened.SaveGraph(loaded);
        using GraphBuilder verify = reopened.OpenGraph(next);
        Assert.Equal("Generic\0\uD800", ((TextWitnessBox<TextEditor>)verify.Root).Items[0].ToString());
    }

    private static StoredGraphRevision HeapSnapshot(Repository repository, GraphRef reference) =>
        repository.GraphStore.ReadBusinessGraph(Resources(repository).ReadHeapDescriptor(reference.CommitId.Value).Address);
    private static TextWitness ReadRoot(GraphRecordStore store, StoredGraphRevision graph) =>
        Assert.IsType<TextWitness>(BusinessPersistenceAdapter.ReadRoot(store, graph, Models()));
    private static ObjectId TextId(GraphRecordStore store, StoredGraphRevision graph, int length) {
        var models = Models();
        Assert.True(models.CreateEnvironment().TryGetCurrentObjectBinding(typeof(TextEditor), out var binding));
        var rows = DecodedRevision.ReadStoredGraph(store, graph, models.FreezeConfiguration()).Objects;
        return Assert.Single(rows, row => row.Layout.Equals(binding!.CurrentLayout) &&
            ((TextEditor)binding.Allocate(row)).Length == length).Id;
    }
    private static void AssertDelta(GraphRecordStore store, StoredGraphRevision graph, ObjectId textId, FrameAddress prior) {
        ObjectVersionChain chain = store.ReadObjectChain(graph, textId.Value);
        Assert.Equal(ObjectVersionKind.Delta, chain.Entries[^1].Record.Kind);
        Assert.Equal(prior, chain.Entries[^1].Record.PriorAddress);
    }
    private static void AssertAccounting(GraphRecordStore store, StoredGraphRevision graph) {
        DecodedRevision decoded = DecodedRevision.ReadStoredGraph(store, graph, Models().FreezeConfiguration());
        foreach (ObjectStateRecord row in decoded.Objects.Where(row => row.Schema?.SchemaId == "Atelia.Text.TextEditor")) {
            ObjectVersionChain chain = store.ReadObjectChain(graph, row.Id.Value);
            Assert.Equal(chain.Head, decoded.ObjectHeads[row.Id]);
            Assert.Equal(chain.Entries.Sum(entry => (long)entry.PayloadBytes), decoded.ObjectStorage[row.Id].ReconstructionPayloadBytes);
        }
    }
    private static StateModelRegistry Models(Action? observeBase = null) {
        StateModelRegistry models = new();
        IDurableLeafRegistration registration = observeBase is null ? models : new ObservedRegistration(models, observeBase);
        TextEditorPersistence.Register(registration);
        models.Register(global::Atelia.DurableGraph.Generated.Family_546578745769746E657373.Definition);
        models.Register(global::Atelia.DurableGraph.Generated.Family_546578745769746E657373426F78.Definition);
        return models;
    }
    private sealed class ObservedRegistration(StateModelRegistry models, Action beforeBase) : IDurableLeafRegistration {
        public void RegisterLeaf<TDomain, TFrozen>(DurableLeafCodec<TDomain, TFrozen> codec) where TDomain : class where TFrozen : notnull =>
            models.RegisterLeaf(new ObservedCodec<TDomain, TFrozen>(codec, beforeBase));
    }

    private sealed class ObservedCodec<TDomain, TFrozen>(DurableLeafCodec<TDomain, TFrozen> codec, Action beforeBase)
        : DurableLeafCodec<TDomain, TFrozen> where TDomain : class where TFrozen : notnull {
        public override IReadOnlyList<int> HistoricalVersions => codec.HistoricalVersions;
        public override TFrozen Capture(TDomain value, LeafAcceptance? acceptance) => codec.Capture(value, acceptance);
        public override TDomain Restore(TFrozen state) => codec.Restore(state);
        public override long? EstimateBaseSize(TFrozen state) => codec.EstimateBaseSize(state);
        public override PreparedBaseBody PrepareBase(TFrozen state) {
            beforeBase();
            return codec.PrepareBase(state);
        }
        public override PreparedDeltaBody? PrepareDelta(TFrozen previous, TFrozen current) => codec.PrepareDelta(previous, current);
        public override TFrozen ReadBase(int version, ref BareValueReader reader) => codec.ReadBase(version, ref reader);
        public override TFrozen ApplyDelta(int version, ref BareValueReader reader, TFrozen previous) => codec.ApplyDelta(version, ref reader, previous);
        public override TFrozen FinishReplay(TFrozen state) => codec.FinishReplay(state);
    }

    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}

[DurableType("TextWitness", 1)]
public sealed partial class TextWitness : IDurableObject {
    [DurableField(1)] public TextEditor? Body;
    [DurableField(2)] public TextEditor? Alias;
    [DurableField(3)] public TextEditor? Empty;
    [DurableField(4)] public TextEditor? Optional;
    [DurableField(5)] public string? Gist;
    [DurableField(6)] public TextEditor? Other;
}

[DurableType("TextWitnessBox", 1)]
public sealed partial class TextWitnessBox<T> : IDurableObject {
    [DurableField(1)] public T Value = default!;
    [DurableField(2)] public List<T> Items = [];
    [DurableField(3)] public Dictionary<string, T> Named = [];
}
