using static Atelia.DurableGraph.Persistence.Tests.DB096TestAccess;
using System.Reflection;
using Atelia.Data;
using Atelia.HeadStore;
using Atelia.Rbf;
using Atelia.FrameStore;

using Node = Atelia.DurableGraph.Persistence.Tests.SharedReadModel.Node;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class RepositoryTagFailureTests : IDisposable {
    private const string Prefix = "durable-tag-failures-";
    private readonly string _root = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));


    [Fact]
    public void ClosedUpstreamBeforeTagOutputIsNotPublishedAndRequiresReopen() {
        CommitId revision;
        using (Repository repository = Create()) {
            using WorkingCopy main = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", new Node { Value = 17 }).Id);
            CommitId address = main.BaseCommitId;
            revision = address;
            repository.CreateTag("stable", address);
            using WorkingCopy warm = repository.OpenWorkingCopy(repository.ForkBranch(address, "warm").Id);
            Assert.NotNull(repository.PreparedStateEntry);
            repository.OperationStageObserver = point => {
                if (point == RepositoryOperationStage.BeforePublication) { ResourcesOf(repository).Heads.Dispose(); }
            };
            // The real subsequent upstream PublishHead rejects its disposed owner before output;
            // HeadStore supplies NotAttempted despite the exception.
            RepositoryPublicationException error = Assert.Throws<RepositoryPublicationException>(() => repository.CreateTag("candidate", address));
            Assert.Equal(RepositoryPublicationOutcome.NotPublished, error.Outcome);
            Assert.Null(error.CandidateRevisionAddress);
            Assert.IsType<ObjectDisposedException>(error.InnerException);
            Assert.True(repository.IsFaulted);
            Assert.Null(repository.PreparedStateEntry);
            repository.OperationStageObserver = null;
            AssertFaultedOperations(repository, main, address);
        }
        using Repository reopened = Repository.OpenReadOnlyExisting(_root, new StateModelRegistry());
        Assert.Equal(revision, reopened.ResolveTag("stable"));
        Assert.Equal(reopened.ReadCurrentCommit(BranchNamed(reopened, "main")).Commit.Id, reopened.ResolveTag("stable"));
        InvalidOperationException missing = Assert.Throws<InvalidOperationException>(() => reopened.ResolveTag("candidate"));
        Assert.Contains("[DurableGraph.TagNotFound]", missing.Message);
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepositoryPublicationGatePreservesNotPublishedVersusPublished(bool afterPublication) {
        CommitId revision;
        using (Repository repository = Create()) {
            using WorkingCopy main = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", new Node { Value = 17 }).Id);
            CommitId address = main.BaseCommitId;
            revision = address;
            using WorkingCopy warm = repository.OpenWorkingCopy(repository.ForkBranch(address, "warm").Id);
            PreparedStateRestoration entry = repository.PreparedStateEntry!;
            IOException injected = new("DG publication checkpoint");
            repository.OperationStageObserver = point => {
                if (point == (afterPublication ? RepositoryOperationStage.AfterPublication : RepositoryOperationStage.BeforePublication)) {
                    throw injected;
                }
            };
            RepositoryPublicationException error = Assert.Throws<RepositoryPublicationException>(() => repository.CreateTag("candidate", address));
            Assert.Equal(afterPublication ? RepositoryPublicationOutcome.Published : RepositoryPublicationOutcome.NotPublished, error.Outcome);
            Assert.Null(error.CandidateRevisionAddress);
            Assert.Same(injected, error.InnerException);
            repository.OperationStageObserver = null;
            if (afterPublication) {
                Assert.True(repository.IsFaulted);
                Assert.Null(repository.PreparedStateEntry);
                AssertFaultedOperations(repository, main, address);
            } else {
                Assert.False(repository.IsFaulted);
                Assert.Same(entry, repository.PreparedStateEntry);
                InvalidOperationException missing = Assert.Throws<InvalidOperationException>(() => repository.ResolveTag("candidate"));
                Assert.Contains("[DurableGraph.TagNotFound]", missing.Message);
                repository.CreateTag("candidate", address);
                Assert.Equal(address, repository.ResolveTag("candidate"));
                Assert.Same(entry, repository.PreparedStateEntry);
            }
        }
        using Repository reopened = Repository.OpenReadOnlyExisting(_root, new StateModelRegistry());
        Assert.Equal(revision, reopened.ResolveTag("candidate"));
        Assert.Equal(reopened.ReadCurrentCommit(BranchNamed(reopened, "main")).Commit.Id, reopened.ResolveTag("candidate"));
    }

    [Fact]
    public void TagPublicationRejectsReentryAndBusyDisposalWithoutDamagingOuterOperation() {
        using Repository repository = Create();
        using WorkingCopy main = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", new Node { Value = 17 }).Id);
        CommitId address = main.BaseCommitId;
        repository.CreateTag("stable", address);
        using WorkingCopy warm = repository.OpenWorkingCopy(repository.ForkBranch(address, "warm").Id);
        PreparedStateRestoration entry = repository.PreparedStateEntry!;
        int checks = 0;
        InstallPublicationFault(ResourcesOf(repository).Heads, "AfterAppend", () => {
            checks++;
            Assert.Throws<InvalidOperationException>(() => repository.CreateTag("nested", address));
            Assert.Throws<InvalidOperationException>(() => repository.ResolveTag("stable"));
            Assert.Throws<InvalidOperationException>(() => repository.ReadCurrentCommit(BranchNamed(repository, "main")).Commit.Id);
            Assert.Throws<InvalidOperationException>(() => repository.OpenWorkingCopy(repository.ForkBranch(address, "nested-fork").Id));
            Assert.Throws<InvalidOperationException>(() => main.CommitState());
            Assert.Throws<InvalidOperationException>(() => main.Dispose());
            Assert.Throws<InvalidOperationException>(() => repository.Dispose());
        });
        repository.CreateTag("outer", address);
        ClearPublicationFault(ResourcesOf(repository).Heads);
        Assert.Equal(1, checks);
        Assert.Equal(address, repository.ResolveTag("outer"));
        Assert.False(repository.IsFaulted);
        Assert.Same(entry, repository.PreparedStateEntry);
        Assert.DoesNotContain(repository.ListBranches(), branch => branch.DisplayLabel == "nested-fork");
        Assert.Contains("[DurableGraph.TagNotFound]", Assert.Throws<InvalidOperationException>(() => repository.ResolveTag("nested")).Message);
        // Rejected disposal did not release this branch's editing occupancy.
        Assert.Throws<InvalidOperationException>(() => repository.OpenWorkingCopy(BranchNamed(repository, "main")));
        main.CommitState();
        Assert.Equal(address, repository.ResolveTag("outer"));
    }

    [Fact]
    public void TagOperationsRejectReentryFromDomainMaterialization() {
        Repository? active = null;
        CommitId? target = null;
        int checks = 0;
        using Repository repository = Repository.CreateNew(_root, SharedReadModel.Models(hydrate: _ => {
            if (target is null) { return; }
            checks++;
            Assert.Throws<InvalidOperationException>(() => active!.ResolveTag("stable"));
            Assert.Throws<InvalidOperationException>(() => active!.CreateTag("nested", target!.Value));
        }));
        active = repository;
        using WorkingCopy main = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", new Node { Value = 17 }).Id);
        target = main.BaseCommitId;
        repository.CreateTag("stable", main.BaseCommitId);
        using WorkingCopy fork = repository.OpenWorkingCopy(repository.ForkBranch(main.BaseCommitId, "fork").Id);
        Assert.Equal(1, checks);
        Assert.Equal(main.BaseCommitId, repository.ResolveTag("stable"));
        Assert.False(repository.IsFaulted);
    }

    [Fact]
    public void ReadonlyResolvesWithoutWritesAndDisposedInstanceRejectsBothOperations() {
        CommitId oldAddress;
        using (Repository writer = Create()) {
            using WorkingCopy main = writer.OpenWorkingCopy(writer.CreateRootBranchFromState("main", new Node { Value = 17 }).Id);
            oldAddress = main.BaseCommitId;
            writer.CreateTag("stable", oldAddress);
            using WorkingCopy warm = writer.OpenWorkingCopy(writer.ForkBranch(oldAddress, "warm").Id);
            Assert.NotNull(writer.PreparedStateEntry);
            writer.Dispose();
            Assert.Null(writer.PreparedStateEntry);
            Assert.Throws<ObjectDisposedException>(() => writer.CreateTag("disposed", oldAddress));
            Assert.Throws<ObjectDisposedException>(() => writer.ResolveTag("stable"));
            Assert.Throws<ObjectDisposedException>(() => main.CommitState());
        }
        Dictionary<string, byte[]> before = Snapshot();
        using (Repository reader = Repository.OpenReadOnlyExisting(_root, new StateModelRegistry())) {
            CommitId address = reader.ResolveTag("stable");
            Assert.Equal(oldAddress, address);
            Assert.Throws<InvalidOperationException>(() => reader.CreateTag("readonly", address));
            Assert.False(reader.IsFaulted);
            Assert.Equal(address, reader.ResolveTag("stable"));
        }
        AssertSnapshot(before);
    }

    private Repository Create() => Repository.CreateNew(_root, SharedReadModel.Models());
    private static T Field<T>(object instance, string name) =>
        (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static void AssertFaultedOperations(Repository repository, WorkingCopy main, CommitId address) {
        Assert.True(main.IsFaulted);
        Assert.Throws<InvalidOperationException>(() => repository.CreateTag("blocked", address));
        Assert.Throws<InvalidOperationException>(() => repository.ResolveTag("stable"));
        Assert.Throws<InvalidOperationException>(() => repository.ListTags());
        Assert.Throws<InvalidOperationException>(() => repository.ReadCurrentCommit(BranchNamed(repository, "main")).Commit.Id);
        Assert.Throws<InvalidOperationException>(() => repository.ListBranches());
        Assert.Throws<InvalidOperationException>(() => repository.ReadCheckpoint(address));
        Assert.Throws<InvalidOperationException>(() => repository.OpenWorkingCopy(BranchNamed(repository, "main")));
        Assert.Throws<InvalidOperationException>(() => repository.OpenWorkingCopy(repository.ForkBranch(address, "blocked").Id));
        Assert.Throws<InvalidOperationException>(() => main.CommitState());
        Assert.Throws<InvalidOperationException>(() => main.CommitEvent(new Node()));
    }
    private Dictionary<string, byte[]> Snapshot() => Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(_root, path), File.ReadAllBytes, StringComparer.Ordinal);
    private void AssertSnapshot(Dictionary<string, byte[]> expected) {
        Dictionary<string, byte[]> actual = Snapshot();
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach ((string path, byte[] bytes) in expected) { Assert.Equal(bytes, actual[path]); }
    }
    public void Dispose() => SharedReadModel.DeleteFixture(_root, Prefix);
}
