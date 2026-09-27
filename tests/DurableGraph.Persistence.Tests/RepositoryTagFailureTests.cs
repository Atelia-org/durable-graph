using System.Reflection;
using Atelia.Data;
using Atelia.EventJournal;
using Atelia.Rbf;
using Atelia.RbfSegmentStore;
using Journal = Atelia.EventJournal.EventJournal;
using Node = Atelia.DurableGraph.Persistence.Tests.SharedReadModel.Node;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class RepositoryTagFailureTests : IDisposable {
    private const string Prefix = "durable-tag-failures-";
    private readonly string _root = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("BeforeTargetFlush", TagPublicationOutcome.NotAttempted, GraphCommitOutcome.NotPublished, false)]
    [InlineData("BeforeAppend", TagPublicationOutcome.NotAttempted, GraphCommitOutcome.NotPublished, false)]
    [InlineData("AfterAppend", TagPublicationOutcome.Unknown, GraphCommitOutcome.Unknown, true)]
    [InlineData("AfterDurableFlush", TagPublicationOutcome.Confirmed, GraphCommitOutcome.Published, true)]
    public void ActualUpstreamStageDeterminesOutcomeAndFaultUntilStrictReopen(string stage,
        TagPublicationOutcome upstreamOutcome, GraphCommitOutcome outcome, bool visibleAfterReopen) {
        Atelia.DurableGraph.Storage.FrameAddress revision;
        ObjectId root;
        using (Repository repository = Create()) {
            using BranchCheckout main = repository.CreateBranch("main", new Node { Value = 17 });
            CheckpointAddress address = main.Head;
            revision = address.RevisionAddress;
            root = address.RootId;
            repository.CreateTag("stable", address);
            using BranchCheckout warm = repository.Fork("warm", address);
            Assert.NotNull(repository.PreparedStateEntry);
            IOException injected = new($"Real tag publication stage: {stage}");
            List<string> reached = [];
            InstallProbe(JournalOf(repository), observed => {
                reached.Add(observed);
                if (observed == stage) { throw injected; }
            });

            GraphCommitException error = Assert.Throws<GraphCommitException>(() => repository.CreateTag("candidate", address));
            Assert.Equal(outcome, error.Outcome);
            Assert.Null(error.CandidateRevisionAddress);
            TagPublicationException upstream = Assert.IsType<TagPublicationException>(error.InnerException);
            Assert.Equal(upstreamOutcome, upstream.Outcome);
            Assert.Equal("candidate", upstream.TagName);
            Assert.Same(injected, upstream.InnerException);
            Assert.Equal(stage, reached[^1]);
            Assert.True(repository.IsFaulted);
            Assert.Null(repository.PreparedStateEntry);
            Assert.Equal(address, main.Head);
            Assert.Equal((byte)17, Assert.IsType<Node>(warm.State).Value);
            AssertFaultedOperations(repository, main, address);
        }
        // Unknown is inspected after closing the old driver, never retried against it.
        // This injected AfterAppend leaves a complete record; this is not a power-loss claim.
        using Repository reopened = Repository.OpenReadOnlyExisting(_root, new StateModelRegistry());
        CheckpointAddress original = reopened.ResolveTag("stable");
        Assert.Equal(revision, original.RevisionAddress);
        Assert.Equal(root, original.RootId);
        Assert.Equal(original, reopened.GetHead("main"));
        if (visibleAfterReopen) {
            Assert.Equal(original, reopened.ResolveTag("candidate"));
        } else {
            InvalidOperationException missing = Assert.Throws<InvalidOperationException>(() => reopened.ResolveTag("candidate"));
            Assert.Contains("[EventJournal.TagNotFound]", missing.Message);
        }
    }

    [Fact]
    public void UntypedUpstreamThrowAfterPublicationAttemptRemainsUnknownUntilReopen() {
        Atelia.DurableGraph.Storage.FrameAddress revision;
        using (Repository repository = Create()) {
            using BranchCheckout main = repository.CreateBranch("main", new Node { Value = 17 });
            CheckpointAddress address = main.Head;
            revision = address.RevisionAddress;
            repository.CreateTag("stable", address);
            using BranchCheckout warm = repository.Fork("warm", address);
            Assert.NotNull(repository.PreparedStateEntry);
            repository.Checkpoint = point => {
                if (point == CommitCheckpoint.BeforePublication) { JournalOf(repository).Dispose(); }
            };
            // The hook returns normally. The real subsequent upstream CreateTag throws
            // ObjectDisposedException, carrying no typed evidence about publication.
            GraphCommitException error = Assert.Throws<GraphCommitException>(() => repository.CreateTag("candidate", address));
            Assert.Equal(GraphCommitOutcome.Unknown, error.Outcome);
            Assert.Null(error.CandidateRevisionAddress);
            Assert.IsType<ObjectDisposedException>(error.InnerException);
            Assert.True(repository.IsFaulted);
            Assert.Null(repository.PreparedStateEntry);
            repository.Checkpoint = null;
            AssertFaultedOperations(repository, main, address);
        }
        using Repository reopened = Repository.OpenReadOnlyExisting(_root, new StateModelRegistry());
        Assert.Equal(revision, reopened.ResolveTag("stable").RevisionAddress);
        Assert.Equal(reopened.GetHead("main"), reopened.ResolveTag("stable"));
        InvalidOperationException missing = Assert.Throws<InvalidOperationException>(() => reopened.ResolveTag("candidate"));
        Assert.Contains("[EventJournal.TagNotFound]", missing.Message);
    }

    [Fact]
    public void ActualRbfPreIoResultFailureKeepsOriginalErrorAndHealthySlot() {
        using (Repository seed = Create()) {
            using BranchCheckout main = seed.CreateBranch("main", new Node { Value = 17 });
            using BranchCheckout warm = seed.Fork("warm", main.Head);
        }
        Dictionary<string, byte[]> before = Snapshot();
        using (Repository repository = Repository.OpenExisting(_root, SharedReadModel.Models())) {
            using BranchCheckout main = repository.Checkout("main");
            CheckpointAddress address = main.Head;
            using BranchCheckout warm = repository.Checkout("warm");
            PreparedStateRestoration entry = repository.PreparedStateEntry!;
            Journal journal = JournalOf(repository);
            IRbfFile log = Field<IRbfFile>(journal, "_refOpLog");
            FieldInfo tail = log.GetType().GetField("_tailOffset", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.NotNull(tail);
            long originalTail = (long)tail.GetValue(log)!;
            List<string> reached = [];
            // Same real RBF pre-I/O offset-overflow rejection used by the pinned upstream
            // ImmutableTagTests; no fake TagPublicationException or replacement publisher.
            InstallProbe(journal, stage => {
                reached.Add(stage);
                if (stage == "BeforeAppend") { tail.SetValue(log, SizedPtr.MaxOffset + SizedPtr.Alignment); }
            });
            try {
                InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(() => repository.CreateTag("retry", address));
                Assert.Contains("[Rbf.ArgumentError]", refusal.Message);
                Assert.Contains("exceeds SizedPtr.MaxOffset", refusal.Message);
                Assert.Equal(new[] { "BeforeTargetFlush", "BeforeAppend" }, reached);
            } finally {
                tail.SetValue(log, originalTail);
                InstallProbe(journal, null);
            }
            Assert.False(repository.IsFaulted);
            Assert.False(main.IsFaulted);
            Assert.Same(entry, repository.PreparedStateEntry);
            Assert.Equal(address, repository.GetHead("main"));
            InvalidOperationException missing = Assert.Throws<InvalidOperationException>(() => repository.ResolveTag("retry"));
            Assert.Contains("[EventJournal.TagNotFound]", missing.Message);
        }
        // All handles are closed so the assertion includes the exclusively held Schema
        // file. Compare before the successful retry is allowed to append the tag.
        AssertSnapshot(before);
        using Repository retry = Repository.OpenExisting(_root, SharedReadModel.Models());
        using BranchCheckout resumed = retry.Checkout("main");
        CheckpointAddress retryAddress = resumed.Head;
        PreparedStateRestoration retryEntry = retry.PreparedStateEntry!;
        retry.CreateTag("retry", retryAddress);
        Assert.Equal(retryAddress, retry.ResolveTag("retry"));
        Assert.Same(retryEntry, retry.PreparedStateEntry);
        resumed.CommitEvent(new Node { Value = 29 });
        Assert.Equal(retryAddress, retry.ResolveTag("retry"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepositoryPublicationGatePreservesNotPublishedVersusPublished(bool afterPublication) {
        Atelia.DurableGraph.Storage.FrameAddress revision;
        using (Repository repository = Create()) {
            using BranchCheckout main = repository.CreateBranch("main", new Node { Value = 17 });
            CheckpointAddress address = main.Head;
            revision = address.RevisionAddress;
            using BranchCheckout warm = repository.Fork("warm", address);
            PreparedStateRestoration entry = repository.PreparedStateEntry!;
            IOException injected = new("DG publication checkpoint");
            repository.Checkpoint = point => {
                if (point == (afterPublication ? CommitCheckpoint.AfterPublication : CommitCheckpoint.BeforePublication)) {
                    throw injected;
                }
            };
            GraphCommitException error = Assert.Throws<GraphCommitException>(() => repository.CreateTag("candidate", address));
            Assert.Equal(afterPublication ? GraphCommitOutcome.Published : GraphCommitOutcome.NotPublished, error.Outcome);
            Assert.Null(error.CandidateRevisionAddress);
            Assert.Same(injected, error.InnerException);
            repository.Checkpoint = null;
            if (afterPublication) {
                Assert.True(repository.IsFaulted);
                Assert.Null(repository.PreparedStateEntry);
                AssertFaultedOperations(repository, main, address);
            } else {
                Assert.False(repository.IsFaulted);
                Assert.Same(entry, repository.PreparedStateEntry);
                InvalidOperationException missing = Assert.Throws<InvalidOperationException>(() => repository.ResolveTag("candidate"));
                Assert.Contains("[EventJournal.TagNotFound]", missing.Message);
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
        InstallProbe(JournalOf(repository), stage => {
            if (stage != "BeforeAppend") { return; }
            checks++;
            Assert.Throws<InvalidOperationException>(() => repository.CreateTag("nested", address));
            Assert.Throws<InvalidOperationException>(() => repository.ResolveTag("stable"));
            Assert.Throws<InvalidOperationException>(() => repository.GetHead("main"));
            Assert.Throws<InvalidOperationException>(() => repository.Fork("nested-fork", address));
            Assert.Throws<InvalidOperationException>(() => main.CommitState());
            Assert.Throws<InvalidOperationException>(() => main.Dispose());
            Assert.Throws<InvalidOperationException>(() => repository.Dispose());
        });
        repository.CreateTag("outer", address);
        InstallProbe(JournalOf(repository), null);
        Assert.Equal(1, checks);
        Assert.Equal(address, repository.ResolveTag("outer"));
        Assert.False(repository.IsFaulted);
        Assert.Same(entry, repository.PreparedStateEntry);
        Assert.DoesNotContain("nested-fork", repository.ListBranches());
        Assert.Contains("[EventJournal.TagNotFound]", Assert.Throws<InvalidOperationException>(() => repository.ResolveTag("nested")).Message);
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
        }), new() { NewStoreLayout = RbfSegmentStoreLayout.Flat });
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

    private Repository Create() => Repository.CreateNew(_root, SharedReadModel.Models(),
        new() { NewStoreLayout = RbfSegmentStoreLayout.Flat });
    private static T Field<T>(object instance, string name) =>
        (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static Journal JournalOf(Repository repository) => Field<HistoryJournal>(repository, "_history").Journal;
    private static void InstallProbe(Journal journal, Action<string>? callback) {
        PropertyInfo property = typeof(Journal).GetProperty("TagPublicationProbe", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.NotNull(property);
        if (callback is null) { property.SetValue(journal, null); return; }
        Type stage = property.PropertyType.GetGenericArguments()[0];
        MethodInfo factory = typeof(RepositoryTagFailureTests).GetMethod(nameof(Probe), BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(stage);
        property.SetValue(journal, factory.CreateDelegate<Func<Action<string>, Delegate>>()(callback));
    }
    private static Delegate Probe<TStage>(Action<string> callback) where TStage : struct, Enum =>
        new Action<TStage>(stage => callback(stage.ToString()));
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
