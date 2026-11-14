using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Storage;
using Atelia.HeadStore;
using static Atelia.DurableGraph.Persistence.Tests.DB096TestAccess;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class GraphHeapTests : IDisposable {
    private const string Prefix = "dg-graph-heap-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
    private readonly string _otherPath = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));

    [Fact]
    public void GeneratedGraphsInterleaveForkKeepAliasesAndContinueAfterColdReopen() {
        GraphRef a0, b0, a1, a2, a3;
        using (Repository repository = Repository.CreateNew(_path, Models())) {
            Assert.Empty(repository.ListBranches());
            HeapNode root = Ring();
            using GraphBuilder a = repository.CreateGraph(root);
            using GraphBuilder b = repository.CreateGraph(new HeapOther { Value = 90 });
            a0 = repository.SaveGraph(a);
            StoredGraphRevision first = Snapshot(repository, a0);
            b0 = repository.SaveGraph(b);
            root.Next!.Value = 21;
            root.Numbers[64] = 1000;
            a1 = repository.SaveGraph(a);
            StoredGraphRevision next = Snapshot(repository, a1);
            Assert.Equal(first.ObjectHeads[first.RootId.Value], next.ObjectHeads[next.RootId.Value]);
            Assert.Contains(repository.GraphStore.ReadBatch(next.OwnedBatch!.Value).Records,
                row => row.Kind == ObjectVersionKind.Delta);

            using GraphBuilder fork = repository.OpenGraph(a0);
            HeapNode forkRoot = Assert.IsType<HeapNode>(fork.Root);
            Assert.NotSame(root, forkRoot);
            Assert.NotSame(root.Next, forkRoot.Next);
            forkRoot.Next!.Value = 22;
            a2 = repository.SaveGraph(fork);
            Assert.Equal(21, root.Next.Value);
            a3 = repository.SaveGraph(fork);
            Assert.NotEqual(a2, a3);
            Assert.Null(Snapshot(repository, a3).OwnedBatch);
            Assert.Equal(Snapshot(repository, a2).DirectoryRoot, Snapshot(repository, a3).DirectoryRoot);
            Assert.Empty(repository.ListBranches());
            Assert.Throws<KeyNotFoundException>(() => repository.ReadCommitInfo(a1.CommitId));
            Assert.Throws<KeyNotFoundException>(() => repository.CreateTag("heap", a1.CommitId));
        }

        using Repository reopened = Repository.OpenExisting(_path, Models());
        foreach ((GraphRef reference, int childValue) in new[] { (a0, 20), (a1, 21), (a2, 22), (a3, 22) }) {
            using GraphBuilder loaded = reopened.OpenGraph(reference);
            HeapNode root = Assert.IsType<HeapNode>(loaded.Root);
            Assert.Equal(childValue, root.Next!.Value);
            Assert.Same(root.Next, root.Alias);
            Assert.Same(root, root.Next.Next);
            Assert.Same(root.Numbers, root.Next.Numbers);
        }
        using GraphBuilder old = reopened.OpenGraph(a0);
        using GraphBuilder other = reopened.OpenGraph(b0);
        Assert.Equal(90, Assert.IsType<HeapOther>(other.Root).Value);
        HeapNode edited = Assert.IsType<HeapNode>(old.Root);
        edited.Next!.Value = 23;
        edited.Numbers[65] = 1001;
        GraphRef continued = reopened.SaveGraph(old);
        using GraphBuilder verify = reopened.OpenGraph(continued);
        Assert.Equal(23, Assert.IsType<HeapNode>(verify.Root).Next!.Value);
        using GraphBuilder unchanged = reopened.OpenGraph(a0);
        Assert.Equal(20, Assert.IsType<HeapNode>(unchanged.Root).Next!.Value);
    }

    [Fact]
    public void RootReplacementDeletesUnreachableObjectsAndCanChangeTheRootType() {
        GraphRef original, replacement;
        using (Repository repository = Repository.CreateNew(_path, Models())) {
            using GraphBuilder builder = repository.CreateGraph(Ring());
            original = repository.SaveGraph(builder);
            HeapNode oldRoot = (HeapNode)builder.Root;
            HeapNode child = oldRoot.Next!;
            child.Next = child.Alias = null;
            builder.Root = child;
            replacement = repository.SaveGraph(builder);
            Assert.Same(child, builder.Root);
            Assert.NotEqual(Snapshot(repository, original).RootId, Snapshot(repository, replacement).RootId);
            Assert.False(Snapshot(repository, replacement).ObjectHeads.ContainsKey(Snapshot(repository, original).RootId.Value));
            HeapOther different = new() { Value = 123 };
            builder.Root = different;
            GraphRef changedType = repository.SaveGraph(builder);
            Assert.Single(Snapshot(repository, changedType).ObjectHeads);
            using GraphBuilder loaded = repository.OpenGraph(changedType);
            Assert.Equal(123, Assert.IsType<HeapOther>(loaded.Root).Value);
        }
        using Repository reader = Repository.OpenReadOnlyExisting(_path, Models());
        using GraphBuilder before = reader.OpenGraph(original);
        using GraphBuilder after = reader.OpenGraph(replacement);
        Assert.Same(((HeapNode)before.Root).Next, ((HeapNode)before.Root).Alias);
        Assert.Null(((HeapNode)after.Root).Next);
    }

    [Fact]
    public void EmptyHeapReopensAndRejectsDefaultEmptyAndBusinessReferences() {
        using (Repository empty = Repository.CreateNew(_path, Models())) { Assert.Empty(empty.ListBranches()); }
        using Repository repository = Repository.OpenExisting(_path, Models());
        Assert.Empty(repository.ListBranches());
        Assert.Throws<InvalidOperationException>(() => repository.OpenGraph(default));
        GraphRef emptyRef = new(new CommitId(Resources(repository).ReadHeapTip()));
        Assert.Throws<InvalidDataException>(() => repository.OpenGraph(emptyRef));
        BranchInfo branch = repository.CreateRootBranchFromState("world", new HeapOther());
        GraphRef businessRef = new(repository.ReadCurrentCommit(branch.Id).Commit.Id);
        Assert.Throws<ArgumentException>(() => repository.OpenGraph(businessRef));
        using GraphBuilder graph = repository.CreateGraph(new HeapOther { Value = 7 });
        GraphRef saved = repository.SaveGraph(graph);
        Assert.Equal(branch.Id, Assert.Single(repository.ListBranches()).Id);
        Assert.False(repository.IsFaulted);
        using GraphBuilder opened = repository.OpenGraph(saved);
        Assert.Equal(7, ((HeapOther)opened.Root).Value);
    }

    [Fact]
    public void HeapTargetKeepsExpectedTipAndStateRoleChecks() {
        using Repository repository = Repository.CreateNew(_path, Models());
        RepositoryResources resources = Resources(repository);
        HeadEntryId empty = resources.ReadHeapTip();
        using GraphBuilder builder = repository.CreateGraph(new HeapOther());
        GraphRef first = repository.SaveGraph(builder);
        StoredGraphRevision graph = Snapshot(repository, first);
        bool produced = false;
        Assert.Throws<InvalidOperationException>(() => resources.PrepareSaveFrom(empty.HeadId, empty, null,
            StoredGraphKind.State, default, () => {
                produced = true;
                return (new PreparedGraphWrite(graph.Address, graph.RootId, []), null);
            }));
        Assert.False(produced);
        HeadEntryId current = resources.ReadHeapTip();
        Assert.Throws<InvalidOperationException>(() => resources.PrepareSaveFrom(current.HeadId, current, null,
            StoredGraphKind.Event, default, () => (new PreparedGraphWrite(null, graph.RootId, []), null)));
        Assert.Equal(current, resources.ReadHeapTip());
        Assert.False(repository.IsFaulted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WrongHeapControlPayloadOrEventRoleRejectsColdOpen(bool eventRole) {
        byte[] payload;
        using (Repository writer = Repository.CreateNew(_path, Models())) {
            BranchInfo branch = writer.CreateRootBranchFromEvent("events", new HeapOther());
            payload = eventRole
                ? Resources(writer).Heads.ReadHead(branch.Id.Value).Unwrap().Payload.ToArray()
                : new byte[] { 0, 1, 2, 3 };
        }
        // Explicit offline corruption with all DG owners closed; no cache survives it.
        using (var data = Atelia.FrameStore.FrameStore.Open(Path.Combine(_path, "data")))
        using (var heads = Atelia.HeadStore.HeadStore.Open(Path.Combine(_path, "versions"), data)) {
            HeadEntry heap = heads.ReadHead(4).Unwrap();
            heads.PublishHead(heap.Id.HeadId, payload, out _).Unwrap();
        }
        Assert.Throws<InvalidDataException>(() => Repository.OpenReadOnlyExisting(_path, Models()));
        Assert.Throws<InvalidDataException>(() => Repository.OpenExisting(_path, Models()));
    }

    [Fact]
    public void ReadOnlyOwnerAllowsMemoryEditsButCannotSaveAndBuildersCheckLifetime() {
        GraphRef reference;
        using (Repository writer = Repository.CreateNew(_path, Models())) {
            using GraphBuilder builder = writer.CreateGraph(new HeapOther { Value = 7 });
            reference = writer.SaveGraph(builder);
        }
        GraphBuilder retained;
        using (Repository reader = Repository.OpenReadOnlyExisting(_path, Models())) {
            retained = reader.OpenGraph(reference);
            retained.Root = new HeapOther { Value = 8 };
            Assert.Throws<InvalidOperationException>(() => reader.SaveGraph(retained));
            using GraphBuilder independent = reader.OpenGraph(reference);
            Assert.Equal(7, ((HeapOther)independent.Root).Value);
        }
        Assert.Throws<ObjectDisposedException>(() => retained.Root);
        retained.Dispose();
        retained.Dispose();
        using Repository writerAgain = Repository.OpenExisting(_path, Models());
        using Repository other = Repository.CreateNew(_otherPath, Models());
        using GraphBuilder live = writerAgain.OpenGraph(reference);
        Assert.Throws<ArgumentException>(() => other.SaveGraph(live));
        live.Dispose();
        Assert.Throws<ObjectDisposedException>(() => writerAgain.SaveGraph(live));
        Assert.False(writerAgain.IsFaulted);
        Assert.False(other.IsFaulted);
    }

    [Fact]
    public void PreparationFailureLeavesTheHeadAndAcceptedBaselineUnchangedAndCanRetry() {
        bool fail = false;
        StateModelRegistry models = SharedReadModel.Models(prepareBase: (in SharedReadModel.State state) => {
            if (fail) { throw new InvalidOperationException("capture preparation failed"); }
            return SharedReadModel.Base(in state);
        });
        using Repository repository = Repository.CreateNew(_path, models);
        SharedReadModel.Node root = new() { Value = 1 };
        using GraphBuilder builder = repository.CreateGraph(root);
        GraphRef first = repository.SaveGraph(builder);
        var tip = Resources(repository).ReadHeapTip();
        long frames = repository.GraphStore.AppendedFrames;
        root.Value = 2;
        fail = true;
        Assert.Throws<InvalidOperationException>(() => repository.SaveGraph(builder));
        Assert.Equal(tip, Resources(repository).ReadHeapTip());
        Assert.Equal(frames, repository.GraphStore.AppendedFrames);
        Assert.False(repository.IsFaulted);
        fail = false;
        GraphRef second = repository.SaveGraph(builder);
        using GraphBuilder old = repository.OpenGraph(first);
        using GraphBuilder current = repository.OpenGraph(second);
        Assert.Equal((byte)1, ((SharedReadModel.Node)old.Root).Value);
        Assert.Equal((byte)2, ((SharedReadModel.Node)current.Root).Value);
    }

    [Fact]
    public void OperationReentryAndBuilderDisposalAreRejectedWithoutConsumingTheCandidate() {
        using Repository repository = Repository.CreateNew(_path, Models());
        using GraphBuilder builder = repository.CreateGraph(new HeapOther());
        repository.OperationStageObserver = stage => {
            if (stage != RepositoryOperationStage.AfterPrepare) { return; }
            Assert.Throws<InvalidOperationException>(() => repository.SaveGraph(builder));
            Assert.Throws<InvalidOperationException>(() => builder.Root = new HeapOther());
            Assert.Throws<InvalidOperationException>(() => builder.Dispose());
        };
        GraphRef saved = repository.SaveGraph(builder);
        Assert.True(saved.IsBound);
        Assert.False(repository.IsFaulted);
        repository.OperationStageObserver = null;
        using GraphBuilder read = repository.OpenGraph(saved);
        Assert.IsType<HeapOther>(read.Root);
    }

    [Theory]
    [InlineData((int)RepositorySaveStage.BeforePublication, RepositoryPublicationOutcome.NotPublished)]
    [InlineData((int)RepositorySaveStage.AfterPublication, RepositoryPublicationOutcome.Published)]
    public void PublicationFailureKeepsActualOutcomeAndFaultsAllBuilders(int stage, RepositoryPublicationOutcome outcome) {
        GraphRef first;
        HeadEntryId tip;
        CommitId? candidate;
        using (Repository writer = Repository.CreateNew(_path, Models())) {
            using GraphBuilder a = writer.CreateGraph(Ring());
            first = writer.SaveGraph(a);
            tip = Resources(writer).ReadHeapTip();
            using GraphBuilder b = writer.CreateGraph(new HeapOther { Value = 99 }); // New Schema joins this save.
            writer.SaveStageObserver = current => { if ((int)current == stage) { throw new IOException("injected"); } };
            RepositoryPublicationException error = Assert.Throws<RepositoryPublicationException>(() => writer.SaveGraph(b));
            Assert.Equal(outcome, error.Outcome);
            Assert.Null(error.TargetBranchId);
            Assert.Equal(new CommitId(tip), error.ExpectedBaseCommitId);
            candidate = error.CandidateCommitId;
            Assert.True(writer.IsFaulted);
            Assert.Throws<InvalidOperationException>(() => writer.SaveGraph(a));
            Assert.Throws<InvalidOperationException>(() => writer.OpenGraph(first));
        }
        using Repository reopened = Repository.OpenExisting(_path, Models());
        HeadEntryId actual = Resources(reopened).ReadHeapTip();
        Assert.Equal(outcome == RepositoryPublicationOutcome.NotPublished, actual == tip);
        Assert.Throws<KeyNotFoundException>(() => reopened.ReadCommitInfo(new CommitId(tip)));
        if (candidate is { } diagnostic) {
            Assert.Throws<KeyNotFoundException>(() => reopened.ReadCommitInfo(diagnostic));
            Assert.Throws<KeyNotFoundException>(() => reopened.CreateTag("diagnostic", diagnostic));
        }
        if (outcome == RepositoryPublicationOutcome.Published) {
            using GraphBuilder published = reopened.OpenGraph(new GraphRef(new CommitId(actual)));
            Assert.Equal(99, ((HeapOther)published.Root).Value);
        }
        using GraphBuilder unchanged = reopened.OpenGraph(first);
        Assert.Equal(10, ((HeapNode)unchanged.Root).Value);
        Assert.Empty(reopened.ListBranches());
    }

    [Theory]
    [InlineData((int)RepositorySaveStage.BeforePublication, false)]
    [InlineData((int)RepositorySaveStage.AfterPublication, true)]
    public void FirstGraphAndItsFirstSchemaPublishTogether(int stage, bool published) {
        HeadEntryId empty;
        using (Repository writer = Repository.CreateNew(_path, Models())) {
            empty = Resources(writer).ReadHeapTip();
            using GraphBuilder builder = writer.CreateGraph(new HeapOther { Value = 17 });
            writer.SaveStageObserver = current => { if ((int)current == stage) { throw new IOException("first save"); } };
            RepositoryPublicationException error = Assert.Throws<RepositoryPublicationException>(() => writer.SaveGraph(builder));
            Assert.Equal(published ? RepositoryPublicationOutcome.Published : RepositoryPublicationOutcome.NotPublished, error.Outcome);
            Assert.Null(builder.Business.Source);
        }
        using Repository reopened = Repository.OpenExisting(_path, Models());
        HeadEntryId current = Resources(reopened).ReadHeapTip();
        Assert.Equal(published, current != empty);
        Assert.Equal(published, reopened.SharedSchemaRoot is not null);
        if (published) {
            using GraphBuilder graph = reopened.OpenGraph(new GraphRef(new CommitId(current)));
            Assert.Equal(17, ((HeapOther)graph.Root).Value);
        } else {
            Assert.Throws<InvalidDataException>(() => reopened.OpenGraph(new GraphRef(new CommitId(current))));
        }
        Assert.Empty(reopened.ListBranches());
    }

    [Fact]
    public void UpgradedGraphSavesRequiredBaseThroughTheSamePublicPipeline() {
        GraphRef first;
        using (Repository writer = Repository.CreateNew(_path, SharedReadModel.Models())) {
            using GraphBuilder builder = writer.CreateGraph(new SharedReadModel.Node { Value = 7 });
            first = writer.SaveGraph(builder);
        }
        using Repository upgraded = Repository.OpenExisting(_path, SharedReadModel.Models(currentVersion: 2));
        using GraphBuilder loaded = upgraded.OpenGraph(first);
        GraphRef next = upgraded.SaveGraph(loaded);
        StoredGraphRevision snapshot = Snapshot(upgraded, next);
        Assert.All(upgraded.GraphStore.ReadBatch(snapshot.OwnedBatch!.Value).Records,
            row => Assert.Equal(ObjectVersionKind.Base, row.Kind));
        using GraphBuilder restored = upgraded.OpenGraph(next);
        Assert.Equal((byte)7, ((SharedReadModel.Node)restored.Root).Value);
    }

    [Fact]
    public void PublishedWorldDirectoriesSelectOnlyTheirOwnGraphVersionsAcrossColdForks() {
        CommitId worldA, worldB;
        using (Repository repository = Repository.CreateNew(_path, Models())) {
            using GraphBuilder entry = repository.CreateGraph(new HeapNode { Text = "red apple" });
            GraphRef a = repository.SaveGraph(entry);
            BranchInfo branch = repository.CreateRootBranchFromState("A", new HeapWorld { Entries = new() { [1] = a } });
            worldA = repository.ReadCurrentCommit(branch.Id).Commit.Id;
            BranchInfo fork = repository.ForkBranch(worldA, "B");
            using WorkingCopy work = repository.OpenWorkingCopy(fork.Id);
            ((HeapNode)entry.Root).Text = "blue berry";
            GraphRef b = repository.SaveGraph(entry);
            ((HeapWorld)work.State!).Entries[1] = b;
            worldB = work.CommitState().Id;
            ((HeapNode)entry.Root).Text = "secret orphan";
            _ = repository.SaveGraph(entry);
            Assert.Equal(2, repository.ListBranches().Length);
        }
        using Repository reader = Repository.OpenReadOnlyExisting(_path, Models());
        Assert.Equal(new[] { "red apple" }, Search(reader, worldA, "apple"));
        Assert.Empty(Search(reader, worldA, "berry"));
        Assert.Equal(new[] { "blue berry" }, Search(reader, worldB, "berry"));
        Assert.Empty(Search(reader, worldB, "orphan"));
    }

    private static string[] Search(Repository repository, CommitId world, string term) {
        HeapWorld directory = (HeapWorld)repository.LoadState(world);
        List<string> matches = [];
        foreach (GraphRef reference in directory.Entries.Values) {
            using GraphBuilder builder = repository.OpenGraph(reference);
            string text = ((HeapNode)builder.Root).Text!;
            if (text.Contains(term, StringComparison.Ordinal)) { matches.Add(text); }
        }
        return matches.ToArray();
    }
    private static StoredGraphRevision Snapshot(Repository repository, GraphRef reference) {
        HistoryGraphRecord record = Resources(repository).ReadHeapDescriptor(reference.CommitId.Value);
        return repository.GraphStore.ReadBusinessGraph(record.Address);
    }
    private static HeapNode Ring() {
        List<int> numbers = Enumerable.Range(0, 128).ToList();
        HeapNode a = new() { Value = 10, Numbers = numbers }, b = new() { Value = 20, Numbers = numbers };
        a.Next = a.Alias = b;
        b.Next = b.Alias = a;
        return a;
    }
    private static StateModelRegistry Models() {
        StateModelRegistry models = new();
        models.Register(global::Atelia.DurableGraph.Generated.Family_486561704E6F6465.Definition);
        models.Register(global::Atelia.DurableGraph.Generated.Family_486561704F74686572.Definition);
        models.Register(global::Atelia.DurableGraph.Generated.Family_48656170576F726C64.Definition);
        models.Register(GraphRef.Definition);
        return models;
    }
    public void Dispose() {
        SharedReadModel.DeleteFixture(_path, Prefix);
        SharedReadModel.DeleteFixture(_otherPath, Prefix);
    }
}

[DurableType("HeapNode", 1)]
public sealed partial class HeapNode : IDurableObject {
    [DurableField(1)] public int Value;
    [DurableField(2)] public HeapNode? Next;
    [DurableField(3)] public HeapNode? Alias;
    [DurableField(4)] public List<int> Numbers = [];
    [DurableField(5)] public string? Text;
}

[DurableType("HeapOther", 1)]
public sealed partial class HeapOther : IDurableObject {
    [DurableField(1)] public int Value;
}

[DurableType("HeapWorld", 1)]
public sealed partial class HeapWorld : IDurableObject {
    [DurableField(1)] public Dictionary<int, GraphRef> Entries = [];
}
