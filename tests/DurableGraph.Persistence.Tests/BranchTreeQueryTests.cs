using System.Buffers;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.HeadStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class BranchTreeQueryTests : IDisposable {
    private const string Prefix = "dg-branch-tree-query-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
    private static readonly DurableSchema TreeSchema = new("TreeQueryRoot", 1, new DurableFieldInfo(1, TypeTag.Int32));

    [Fact]
    public void Forest_uses_actual_origin_branch_and_preserves_empty_forks_across_parent_child_progress_and_reopen() {
        BranchId a, b, c, d, e, fromInherited;
        CommitId aSeed, cSource;
        using (Repository repository = Repository.CreateStorageNew(_path, Models())) {
            a = repository.CreateRootBranchFromState("same", new DB087Widget { Value = 1 }).Id;
            b = repository.CreateRootBranchFromState("same", new DB087Widget { Value = 2 }).Id;
            aSeed = repository.ReadCurrentCommit(a).Commit.Id;
            c = repository.ForkBranch(aSeed, "same").Id;
            using (WorkingCopy work = repository.OpenWorkingCopy(c)) {
                ((DB087Widget)work.State!).Value = 3;
                cSource = work.CommitState().Id;
            }
            d = repository.ForkBranch(cSource, "same").Id; // Empty child of C, not A.
            e = repository.ForkBranch(repository.ReadCurrentCommit(b).Commit.Id, "same").Id;
            CommitId inherited = repository.ReadFrames(c).First().Id;
            Assert.Equal(aSeed, inherited);
            fromInherited = repository.ForkBranch(inherited, "same").Id;
            AssertForest(repository);
            using (WorkingCopy work = repository.OpenWorkingCopy(a)) {
                ((DB087Widget)work.State!).Value = 11;
                work.CommitState();
            }
            using (WorkingCopy work = repository.OpenWorkingCopy(c)) {
                ((DB087Widget)work.State!).Value = 33;
                work.CommitState();
            }
            AssertForest(repository);
            Assert.Equal(cSource, repository.ReadCurrentCommit(d).Commit.Id);
            Assert.Equal(aSeed, repository.ReadCurrentCommit(fromInherited).Commit.Id);
        }
        Dictionary<string, byte[]> before = SnapshotFiles();
        using (Repository repository = Repository.OpenStorageReadOnlyExisting(_path, new StateModelRegistry())) {
            AssertForest(repository); // Directory queries need no registered domain model.
            BranchInfo[] retained = repository.ListBranchSubtree(a);
            Assert.Equal(4, retained.Length);
            retained[0] = new(retained[0].Id, "caller replacement", null);
            BranchInfo[] roots = repository.ListRootBranches();
            roots[0] = new(roots[0].Id, "caller replacement", null);
            AssertForest(repository);
            Assert.Equal(0, repository.GraphStore.AppendedFrames);
        }
        AssertFilesEqual(before, SnapshotFiles());

        void AssertForest(Repository repository) {
            AssertIds(repository.ListRootBranches(), a, b);
            AssertIds(repository.ListBranchSubtree(a), a, c, d, fromInherited);
            AssertIds(repository.ListBranchSubtree(b), b, e);
            AssertIds(repository.ListBranchSubtree(c), c, d);
            AssertIds(repository.ListBranchSubtree(d), d);
            AssertIds(repository.ListBranchSubtree(e), e);
            AssertIds(repository.ListBranchSubtree(fromInherited), fromInherited);
            Assert.All(repository.ListBranches(), branch => Assert.Equal("same", branch.DisplayLabel));
            Assert.Equal(aSeed, repository.ReadBranchInfo(fromInherited).ForkedFromCommitId);
            Assert.Equal(cSource, repository.ReadBranchInfo(d).ForkedFromCommitId);
        }
    }

    [Fact]
    public void Tree_queries_do_not_capture_allocate_or_hydrate_business_models() {
        Counts counts = new();
        BranchId root, child;
        using (Repository repository = Repository.CreateStorageNew(_path, Models(counts))) {
            root = repository.CreateRootBranchFromState("root", new TreeRoot { Value = 7 }).Id;
            child = repository.ForkBranch(repository.ReadCurrentCommit(root).Commit.Id, "child").Id;
        }
        counts.Captures = counts.Allocations = counts.Hydrations = 0;
        counts.Forbidden = true;
        using Repository reopened = Repository.OpenStorageReadOnlyExisting(_path, Models(counts));
        AssertIds(reopened.ListRootBranches(), root);
        AssertIds(reopened.ListBranchSubtree(root), root, child);
        AssertIds(reopened.ListBranchSubtree(child), child);
        Assert.Equal(0, counts.Captures);
        Assert.Equal(0, counts.Allocations);
        Assert.Equal(0, counts.Hydrations);
    }

    [Fact]
    public void Empty_repository_has_no_roots_and_default_management_or_unregistered_starts_are_rejected() {
        BranchId root;
        HeadId orphan;
        HeadId[] controls;
        using (Repository repository = Repository.CreateStorageNew(_path, Models())) {
            Assert.Empty(repository.ListRootBranches());
            Assert.Throws<KeyNotFoundException>(() => repository.ListBranchSubtree(default));
            root = repository.CreateRootBranchFromState("root", new DB087Widget()).Id;
        }
        using (RepositoryResources resources = RepositoryResources.OpenStorageExisting(_path)) {
            orphan = resources.Heads.CreateHead(resources.Heads.ReadHead(root.Value).Unwrap().Payload.ToArray(), out _).Unwrap().HeadId;
            controls = [resources.Heads.ReadHead(1).Unwrap().Id.HeadId, resources.Heads.ReadHead(2).Unwrap().Id.HeadId,
                resources.Heads.ReadHead(3).Unwrap().Id.HeadId];
        }
        using Repository reopened = Repository.OpenStorageReadOnlyExisting(_path, new StateModelRegistry());
        AssertIds(reopened.ListRootBranches(), root);
        AssertIds(reopened.ListBranchSubtree(root), root);
        Assert.Throws<KeyNotFoundException>(() => reopened.ListBranchSubtree(default));
        foreach (HeadId head in controls.Append(orphan)) {
            Assert.Throws<KeyNotFoundException>(() => reopened.ListBranchSubtree(new(head)));
        }
        Assert.False(reopened.IsFaulted);
    }

    [Fact]
    public void A_real_multilevel_chain_returns_each_descendant_once_without_assuming_an_order() {
        const int depth = 24; // A structural witness, not a large-directory benchmark.
        List<BranchId> chain = [];
        using (Repository repository = Repository.CreateStorageNew(_path, Models())) {
            chain.Add(repository.CreateRootBranchFromState("root", new DB087Widget()).Id);
            CommitId source = repository.ReadCurrentCommit(chain[0]).Commit.Id;
            for (int level = 1; level < depth; level++) {
                BranchId child = repository.ForkBranch(source, "repeat").Id;
                chain.Add(child);
                // A real local save is essential: forking an inherited current would
                // otherwise attach every node to the same actual source branch.
                using WorkingCopy work = repository.OpenWorkingCopy(child);
                ((DB087Widget)work.State!).Value = level;
                source = work.CommitState().Id;
            }
        }
        using Repository reopened = Repository.OpenStorageReadOnlyExisting(_path, new StateModelRegistry());
        AssertIds(reopened.ListRootBranches(), chain[0]);
        AssertIds(reopened.ListBranchSubtree(chain[0]), chain.ToArray());
        AssertIds(reopened.ListBranchSubtree(chain[depth / 2]), chain.Skip(depth / 2).ToArray());
        AssertIds(reopened.ListBranchSubtree(chain[^1]), chain[^1]);
    }

    [Theory]
    [InlineData("fault")]
    [InlineData("dispose")]
    [InlineData("data-close")]
    [InlineData("heads-close")]
    public void Warm_tree_results_do_not_bypass_repository_or_backing_lifetime(string stop) {
        using Repository repository = Repository.CreateStorageNew(_path, Models());
        BranchId root = repository.CreateRootBranchFromState("root", new DB087Widget()).Id;
        BranchInfo[] retained = repository.ListRootBranches();
        AssertIds(repository.ListBranchSubtree(root), root);
        RepositoryResources resources = DB096TestAccess.Resources(repository);
        switch (stop) {
            case "fault": resources.MarkFaulted(); break;
            case "dispose": repository.Dispose(); break;
            case "data-close": resources.Data.Dispose(); break;
            case "heads-close": resources.Heads.Dispose(); break;
            default: throw new InvalidOperationException(stop);
        }
        Assert.ThrowsAny<InvalidOperationException>(() => repository.ListRootBranches());
        Assert.ThrowsAny<InvalidOperationException>(() => repository.ListBranchSubtree(root));
        Assert.Equal(root, Assert.Single(retained).Id);
    }

    [Fact]
    public void Both_queries_reject_reentry_and_remain_usable_after_the_outer_commit() {
        using Repository repository = Repository.CreateStorageNew(_path, Models());
        BranchId root = repository.CreateRootBranchFromState("root", new DB087Widget()).Id;
        using WorkingCopy work = repository.OpenWorkingCopy(root);
        int callbacks = 0;
        repository.SaveStageObserver = stage => {
            if (stage != RepositorySaveStage.BeforePublication) { return; }
            callbacks++;
            Assert.Throws<InvalidOperationException>(() => repository.ListRootBranches());
            Assert.Throws<InvalidOperationException>(() => repository.ListBranchSubtree(root));
        };
        work.CommitState();
        repository.SaveStageObserver = null;
        Assert.Equal(1, callbacks);
        Assert.False(repository.IsFaulted);
        AssertIds(repository.ListRootBranches(), root);
        AssertIds(repository.ListBranchSubtree(root), root);
    }

    private static void AssertIds(BranchInfo[] actual, params BranchId[] expected) {
        Assert.Equal(expected.Length, actual.Length);
        Assert.Equal(actual.Length, actual.Select(branch => branch.Id).Distinct().Count());
        Assert.True(expected.ToHashSet().SetEquals(actual.Select(branch => branch.Id)));
    }

    private static StateModelRegistry Models(Counts? counts = null) {
        StateModelRegistry models = new();
        models.Register(global::Atelia.DurableGraph.Generated.Family_5030576964676574.Definition);
        if (counts is not null) {
            models.Register(new StateModelBinding<TreeRoot, int>(new(TreeSchema, BaseInt,
                static (in int prior, in int current) => new(prior != current, BaseInt(in current).Body)),
                [new StateReaderBinding<int>(TreeSchema, static (ref BareValueReader reader) => reader.ReadVarInt32(),
                    static (ref BareValueReader reader, in int _) => reader.ReadVarInt32(), static (in int _, IStateReferenceVisitor _) => { })],
                static row => row.GetState<int>(), () => { counts.Allocations++; counts.Check(); return new(); },
                (TreeRoot root, in int state, ObjectReadTable _) => { counts.Hydrations++; counts.Check(); root.Value = state; },
                (root, _) => { counts.Captures++; counts.Check(); return root.Value; }, static (in int _, IStateReferenceVisitor _) => { }));
        }
        return models;
    }

    private static PreparedBaseBody BaseInt(in int value) {
        ArrayBufferWriter<byte> bytes = new();
        BareValueWriter writer = new(bytes);
        writer.WriteVarInt32(value);
        return new(bytes.WrittenSpan);
    }
    private sealed class TreeRoot : IDurableObject { internal int Value; }
    private sealed class Counts {
        internal int Captures, Allocations, Hydrations;
        internal bool Forbidden;
        internal void Check() { if (Forbidden) { throw new InvalidOperationException("Tree query must not materialize business objects."); } }
    }
    private Dictionary<string, byte[]> SnapshotFiles() => Directory.GetFiles(_path, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(_path, path), File.ReadAllBytes);
    private static void AssertFilesEqual(Dictionary<string, byte[]> expected, Dictionary<string, byte[]> actual) {
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach ((string path, byte[] bytes) in expected) { Assert.Equal(bytes, actual[path]); }
    }
    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}
