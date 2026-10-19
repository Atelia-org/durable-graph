using Atelia.HeadStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class BranchDirectoryLookupTests : IDisposable {
    private const string Prefix = "dg-branch-directory-lookup-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(3)]
    [InlineData(9)]
    public void Accepted_directory_projects_once_and_repeated_queries_do_not_project_members_again(int count) {
        BranchId[] branches = new BranchId[count];
        CommitId first;
        using (Repository repository = Repository.CreateStorageNew(_path, Models())) {
            RepositoryResources resources = DB096TestAccess.Resources(repository);
            var empty = resources.ListBranches();
            Assert.Empty(empty);
            Assert.Same(empty, resources.ListBranches());
            Assert.Equal(0, resources.BranchDirectoryProjectedEntries);
            branches[0] = repository.CreateRootBranchFromState("重复标签", new DB087Widget()).Id;
            first = repository.ReadCurrentCommit(branches[0]).Commit.Id;
            for (int i = 1; i < count; i++) {
                branches[i] = repository.ForkBranch(first, "重复标签").Id;
            }
            // Creation delivers BranchInfo and projects each newly accepted D once.
            Assert.Equal((long)count * (count + 1) / 2, resources.BranchDirectoryProjectedEntries);
            Assert.Empty(empty); // A retained view is an immutable snapshot, not a live mutable map.
            AssertQueriesReuseProjection(repository, resources, branches, first);
        }
        using (Repository repository = Repository.OpenStorageReadOnlyExisting(_path, Models())) {
            RepositoryResources resources = DB096TestAccess.Resources(repository);
            Assert.Equal(0, resources.BranchDirectoryProjectedEntries);
            AssertQueriesReuseProjection(repository, resources, branches, first);
            Assert.Equal(count, resources.BranchDirectoryProjectedEntries);
        }
    }

    [Fact]
    public void Business_shared_schema_and_tag_publication_reuse_the_same_accepted_directory_view() {
        using Repository repository = Repository.CreateStorageNew(_path, Models());
        BranchId branch = repository.CreateRootBranchFromState("main", new DB087Widget()).Id;
        RepositoryResources resources = DB096TestAccess.Resources(repository);
        var view = resources.ListBranches();
        long projected = resources.BranchDirectoryProjectedEntries;
        HeadEntryId directory = resources.Heads.ReadHead(1).Unwrap().Id;
        HeadEntryId schema = resources.Heads.ReadHead(2).Unwrap().Id;
        using WorkingCopy work = repository.OpenWorkingCopy(branch);
        ((DB087Widget)work.State!).Value = 7;
        work.CommitState();
        Assert.Same(view, resources.ListBranches());
        Assert.Equal(schema, resources.Heads.ReadHead(2).Unwrap().Id);
        CommitId added = work.CommitEvent(new DirectoryLookupEvent { Value = 8 }).Id;
        Assert.NotEqual(schema, resources.Heads.ReadHead(2).Unwrap().Id);
        Assert.Same(view, resources.ListBranches());
        repository.CreateTag("bookmark", added);
        Assert.Equal(added, repository.ResolveTag("bookmark"));
        Assert.Same(view, resources.ListBranches());
        Assert.Equal(directory, resources.Heads.ReadHead(1).Unwrap().Id);
        Assert.Equal(projected, resources.BranchDirectoryProjectedEntries);
    }

    [Fact]
    public void Physical_unregistered_and_control_heads_are_not_promoted_by_cached_membership() {
        BranchId registered;
        using (Repository repository = Repository.CreateStorageNew(_path, Models())) {
            registered = repository.CreateRootBranchFromState("main", new DB087Widget()).Id;
        }
        HeadEntryId orphan;
        HeadEntryId[] controls;
        // External raw publication is performed with no live Repository/cache retained.
        using (RepositoryResources resources = RepositoryResources.OpenStorageExisting(_path)) {
            orphan = resources.Heads.CreateHead(resources.Heads.ReadHead(registered.Value).Unwrap().Payload.ToArray(), out _).Unwrap();
            controls = [resources.Heads.ReadHead(1).Unwrap().Id, resources.Heads.ReadHead(2).Unwrap().Id,
                resources.Heads.ReadHead(3).Unwrap().Id];
        }
        using Repository reopened = Repository.OpenStorageReadOnlyExisting(_path, Models());
        RepositoryResources owner = DB096TestAccess.Resources(reopened);
        Assert.Equal(registered, Assert.Single(reopened.ListBranches()).Id);
        var view = owner.ListBranches();
        Assert.Throws<KeyNotFoundException>(() => reopened.ReadBranchInfo(default));
        Assert.Throws<ArgumentException>(() => reopened.ReadCurrentCommit(default));
        foreach (HeadEntryId id in controls.Append(orphan)) {
            Assert.Throws<KeyNotFoundException>(() => reopened.ReadBranchInfo(new(id.HeadId)));
            Assert.Throws<KeyNotFoundException>(() => reopened.ReadCurrentCommit(new(id.HeadId)));
            Assert.Throws<KeyNotFoundException>(() => reopened.ReadCommitInfo(new(id)));
        }
        Assert.Same(view, owner.ListBranches());
        Assert.Equal(1, owner.BranchDirectoryProjectedEntries);
        Assert.False(reopened.IsFaulted);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Failed_registration_does_not_install_candidate_view_and_reopen_uses_published_D(bool fork, bool published) {
        BranchId candidate;
        using (Repository repository = Repository.CreateStorageNew(_path, Models())) {
            BranchId main = repository.CreateRootBranchFromState("main", new DB087Widget()).Id;
            CommitId source = repository.ReadCurrentCommit(main).Commit.Id;
            RepositoryResources resources = DB096TestAccess.Resources(repository);
            var accepted = resources.ListBranches();
            long projected = resources.BranchDirectoryProjectedEntries;
            IOException injected = new("stop registration before accepted-view installation");
            repository.SaveStageObserver = stage => {
                if (stage != (published ? RepositorySaveStage.AfterPublication : RepositorySaveStage.BeforePublication)) { return; }
                Assert.Same(accepted, resources.ListBranches());
                Assert.Single(accepted);
                Assert.Equal(projected, resources.BranchDirectoryProjectedEntries);
                throw injected;
            };
            RepositoryPublicationException error = Assert.Throws<RepositoryPublicationException>(() => {
                if (fork) { repository.ForkBranch(source, "candidate"); }
                else { repository.CreateRootBranchFromState("candidate", new DB087Widget()); }
            });
            Assert.Same(injected, error.InnerException);
            Assert.Equal(published ? RepositoryPublicationOutcome.Published : RepositoryPublicationOutcome.NotPublished, error.Outcome);
            Assert.True(repository.IsFaulted);
            candidate = error.TargetBranchId!.Value;
            Assert.ThrowsAny<InvalidOperationException>(() => resources.ListBranches());
            Assert.Equal(projected, resources.BranchDirectoryProjectedEntries);
        }
        using Repository reopened = Repository.OpenStorageReadOnlyExisting(_path, Models());
        Assert.Equal(published ? 2 : 1, reopened.ListBranches().Length);
        if (published) { Assert.Equal("candidate", reopened.ReadBranchInfo(candidate).DisplayLabel); }
        else { Assert.Throws<KeyNotFoundException>(() => reopened.ReadBranchInfo(candidate)); }
        Assert.Equal(published ? 2 : 1, DB096TestAccess.Resources(reopened).BranchDirectoryProjectedEntries);
    }

    [Theory]
    [InlineData("Fault")]
    [InlineData("Dispose")]
    [InlineData("DataClose")]
    [InlineData("HeadsClose")]
    public void A_warm_directory_cannot_bypass_owner_or_backing_lifetime(string stop) {
        using Repository repository = Repository.CreateStorageNew(_path, Models());
        BranchId branch = repository.CreateRootBranchFromState("main", new DB087Widget()).Id;
        RepositoryResources resources = DB096TestAccess.Resources(repository);
        var view = resources.ListBranches();
        long projected = resources.BranchDirectoryProjectedEntries;
        switch (stop) {
            case "Fault": resources.MarkFaulted(); break;
            case "Dispose": resources.Dispose(); break;
            case "DataClose": resources.Data.Dispose(); break;
            case "HeadsClose": resources.Heads.Dispose(); break;
            default: throw new InvalidOperationException(stop);
        }
        Assert.ThrowsAny<InvalidOperationException>(() => resources.ListBranches());
        Assert.ThrowsAny<InvalidOperationException>(() => repository.ReadBranchInfo(branch));
        Assert.ThrowsAny<InvalidOperationException>(() => repository.ReadCurrentCommit(branch));
        Assert.ThrowsAny<InvalidOperationException>(() => repository.ListBranches());
        Assert.Equal(projected, resources.BranchDirectoryProjectedEntries);
        Assert.Equal("main", view[branch.Value]); // Owned immutable result stays readable.
    }

    private static void AssertQueriesReuseProjection(Repository repository, RepositoryResources resources,
        BranchId[] branches, CommitId first) {
        var view = resources.ListBranches();
        Assert.Equal(branches.Length, view.Count);
        long projected = resources.BranchDirectoryProjectedEntries;
        var mutable = Assert.IsAssignableFrom<IDictionary<HeadId, string>>(view);
        Assert.Throws<NotSupportedException>(() => mutable[branches[0].Value] = "tampered");
        for (int repeat = 0; repeat < 3; repeat++) {
            foreach (BranchId branch in branches) {
                Assert.Equal("重复标签", repository.ReadBranchInfo(branch).DisplayLabel);
                Assert.Equal(first, repository.ReadCurrentCommit(branch).Commit.Id);
                Assert.Equal(first, repository.ReadCommitInfo(first).Id);
            }
            Assert.Equal(branches.Length, repository.ListBranches().Length);
            Assert.Same(view, resources.ListBranches());
        }
        Assert.Equal(projected, resources.BranchDirectoryProjectedEntries);
    }

    private static StateModelRegistry Models() {
        StateModelRegistry models = new();
        models.Register(global::Atelia.DurableGraph.Generated.Family_5030576964676574.Definition);
        models.Register(global::Atelia.DurableGraph.Generated.Family_4469726563746F72794C6F6F6B75704576656E74.Definition);
        return models;
    }

    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}

[DurableType("DirectoryLookupEvent", 1)]
internal sealed partial class DirectoryLookupEvent : IDurableObject {
    [DurableField(1)] internal int Value = 0;
}
