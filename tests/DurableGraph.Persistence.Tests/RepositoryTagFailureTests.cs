using System.Reflection;
using Atelia.Data;
using Atelia.VersionStore;
using Atelia.Rbf;
using Atelia.FrameStore;

using Node = Atelia.DurableGraph.Persistence.Tests.SharedReadModel.Node;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class RepositoryTagFailureTests : IDisposable {
    private const string Prefix = "durable-tag-failures-";
    private readonly string _root = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));


    [Fact]
    public void ClosedUpstreamBeforeTagOutputIsNotPublishedAndRequiresReopen() {
        Atelia.FrameStore.FrameAddress revision;
        using (Repository repository = Create()) {
            using BranchCheckout main = repository.CreateBranch("main", new Node { Value = 17 });
            CheckpointAddress address = main.Head;
            revision = address.RevisionAddress;
            repository.CreateTag("stable", address);
            using BranchCheckout warm = repository.Fork("warm", address);
            Assert.NotNull(repository.PreparedStateEntry);
            repository.OperationStageObserver = point => {
                if (point == RepositoryOperationStage.BeforePublication) { ResourcesOf(repository).Versions.Dispose(); }
            };
            // The real subsequent upstream CreateTag rejects its disposed owner before output;
            // VersionStore supplies NotAttempted despite the exception.
            GraphCommitException error = Assert.Throws<GraphCommitException>(() => repository.CreateTag("candidate", address));
            Assert.Equal(GraphCommitOutcome.NotPublished, error.Outcome);
            Assert.Null(error.CandidateRevisionAddress);
            Assert.IsType<ObjectDisposedException>(error.InnerException);
            Assert.True(repository.IsFaulted);
            Assert.Null(repository.PreparedStateEntry);
            repository.OperationStageObserver = null;
            AssertFaultedOperations(repository, main, address);
        }
        using Repository reopened = Repository.OpenReadOnlyExisting(_root, new StateModelRegistry());
        Assert.Equal(revision, reopened.ResolveTag("stable").RevisionAddress);
        Assert.Equal(reopened.GetHead("main"), reopened.ResolveTag("stable"));
        InvalidOperationException missing = Assert.Throws<InvalidOperationException>(() => reopened.ResolveTag("candidate"));
        Assert.Contains("[VersionStore.TagNotFound]", missing.Message);
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepositoryPublicationGatePreservesNotPublishedVersusPublished(bool afterPublication) {
        Atelia.FrameStore.FrameAddress revision;
        using (Repository repository = Create()) {
            using BranchCheckout main = repository.CreateBranch("main", new Node { Value = 17 });
            CheckpointAddress address = main.Head;
            revision = address.RevisionAddress;
            using BranchCheckout warm = repository.Fork("warm", address);
            PreparedStateRestoration entry = repository.PreparedStateEntry!;
            IOException injected = new("DG publication checkpoint");
            repository.OperationStageObserver = point => {
                if (point == (afterPublication ? RepositoryOperationStage.AfterPublication : RepositoryOperationStage.BeforePublication)) {
                    throw injected;
                }
            };
            GraphCommitException error = Assert.Throws<GraphCommitException>(() => repository.CreateTag("candidate", address));
            Assert.Equal(afterPublication ? GraphCommitOutcome.Published : GraphCommitOutcome.NotPublished, error.Outcome);
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
                Assert.Contains("[VersionStore.TagNotFound]", missing.Message);
                repository.CreateTag("candidate", address);
                Assert.Equal(address, repository.ResolveTag("candidate"));
                Assert.Same(entry, repository.PreparedStateEntry);
            }
        }
        using Repository reopened = Repository.OpenReadOnlyExisting(_root, new StateModelRegistry());
        Assert.Equal(revision, reopened.ResolveTag("candidate").RevisionAddress);
        Assert.Equal(reopened.GetHead("main"), reopened.ResolveTag("candidate"));
    }

    [Fact]
    public void TagPublicationRejectsReentryAndBusyDisposalWithoutDamagingOuterOperation() {
        using Repository repository = Create();
        using BranchCheckout main = repository.CreateBranch("main", new Node { Value = 17 });
        CheckpointAddress address = main.Head;
        repository.CreateTag("stable", address);
        using BranchCheckout warm = repository.Fork("warm", address);
        PreparedStateRestoration entry = repository.PreparedStateEntry!;
        int checks = 0;
        InstallPublicationFault(ResourcesOf(repository).Versions, "AfterAppend", () => {
            checks++;
            Assert.Throws<InvalidOperationException>(() => repository.CreateTag("nested", address));
            Assert.Throws<InvalidOperationException>(() => repository.ResolveTag("stable"));
            Assert.Throws<InvalidOperationException>(() => repository.GetHead("main"));
            Assert.Throws<InvalidOperationException>(() => repository.Fork("nested-fork", address));
            Assert.Throws<InvalidOperationException>(() => main.CommitState());
            Assert.Throws<InvalidOperationException>(() => main.Dispose());
            Assert.Throws<InvalidOperationException>(() => repository.Dispose());
        }, tag: true);
        repository.CreateTag("outer", address);
        ClearPublicationFault(ResourcesOf(repository).Versions);
        Assert.Equal(1, checks);
        Assert.Equal(address, repository.ResolveTag("outer"));
        Assert.False(repository.IsFaulted);
        Assert.Same(entry, repository.PreparedStateEntry);
        Assert.DoesNotContain("nested-fork", repository.ListBranches());
        Assert.Contains("[VersionStore.TagNotFound]", Assert.Throws<InvalidOperationException>(() => repository.ResolveTag("nested")).Message);
        // Rejected disposal did not release this branch's editing occupancy.
        Assert.Throws<InvalidOperationException>(() => repository.Checkout("main"));
        main.CommitState();
        Assert.Equal(address, repository.ResolveTag("outer"));
    }

    [Fact]
    public void TagOperationsRejectReentryFromDomainMaterialization() {
        Repository? active = null;
        CheckpointAddress? target = null;
        int checks = 0;
        using Repository repository = Repository.CreateNew(_root, SharedReadModel.Models(hydrate: _ => {
            checks++;
            Assert.Throws<InvalidOperationException>(() => active!.ResolveTag("stable"));
            Assert.Throws<InvalidOperationException>(() => active!.CreateTag("nested", target!));
        }),
        schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared);
        active = repository;
        using BranchCheckout main = repository.CreateBranch("main", new Node { Value = 17 });
        target = main.Head;
        repository.CreateTag("stable", main.Head);
        using BranchCheckout fork = repository.Fork("fork", main.Head);
        Assert.Equal(1, checks);
        Assert.Equal(main.Head, repository.ResolveTag("stable"));
        Assert.False(repository.IsFaulted);
    }

    [Fact]
    public void ReadonlyResolvesWithoutWritesAndDisposedInstanceRejectsBothOperations() {
        CheckpointAddress oldAddress;
        using (Repository writer = Create()) {
            using BranchCheckout main = writer.CreateBranch("main", new Node { Value = 17 });
            oldAddress = main.Head;
            writer.CreateTag("stable", oldAddress);
            using BranchCheckout warm = writer.Fork("warm", oldAddress);
            Assert.NotNull(writer.PreparedStateEntry);
            writer.Dispose();
            Assert.Null(writer.PreparedStateEntry);
            Assert.Throws<ObjectDisposedException>(() => writer.CreateTag("disposed", oldAddress));
            Assert.Throws<ObjectDisposedException>(() => writer.ResolveTag("stable"));
            Assert.Throws<ObjectDisposedException>(() => main.CommitState());
        }
        Dictionary<string, byte[]> before = Snapshot();
        using (Repository reader = Repository.OpenReadOnlyExisting(_root, new StateModelRegistry())) {
            CheckpointAddress address = reader.ResolveTag("stable");
            Assert.Equal(oldAddress.RevisionAddress, address.RevisionAddress);
            Assert.Equal(oldAddress.RootId, address.RootId);
            Assert.NotEqual(oldAddress, address);
            Assert.Throws<InvalidOperationException>(() => reader.CreateTag("readonly", address));
            Assert.False(reader.IsFaulted);
            Assert.Equal(address, reader.ResolveTag("stable"));
        }
        AssertSnapshot(before);
    }

    private Repository Create() => Repository.CreateNew(_root, SharedReadModel.Models(), schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared);
    private static T Field<T>(object instance, string name) =>
        (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static void AssertFaultedOperations(Repository repository, BranchCheckout main, CheckpointAddress address) {
        Assert.True(main.IsFaulted);
        Assert.Throws<InvalidOperationException>(() => repository.CreateTag("blocked", address));
        Assert.Throws<InvalidOperationException>(() => repository.ResolveTag("stable"));
        Assert.Throws<InvalidOperationException>(() => repository.GetHead("main"));
        Assert.Throws<InvalidOperationException>(() => repository.ListBranches());
        Assert.Throws<InvalidOperationException>(() => repository.ReadCheckpoint(address));
        Assert.Throws<InvalidOperationException>(() => repository.Checkout("main"));
        Assert.Throws<InvalidOperationException>(() => repository.Fork("blocked", address));
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
