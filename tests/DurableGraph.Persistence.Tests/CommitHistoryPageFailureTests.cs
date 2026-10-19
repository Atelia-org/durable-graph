using System.Buffers.Binary;
using System.Reflection;
using Atelia.Data;
using Atelia.HeadStore;
using Atelia.Rbf;
using static Atelia.DurableGraph.Persistence.Tests.DB096TestAccess;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class CommitHistoryPageFailureTests : IDisposable {
    private const string Prefix = "dg-history-page-failure-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Invalid_boundaries_and_limits_reject_without_reading_and_release_the_guard() {
        var seed = Seed();
        using Repository repository = Repository.OpenStorageReadOnlyExisting(_path, new());
        int visited = 0;
        repository.HistoryPageItemObserver = _ => visited++;
        Assert.Throws<ArgumentException>(() => repository.ReadHistoryPage(default, new(1, 1000)));
        Assert.Throws<ArgumentException>(() => repository.ReadHistoryPage(HistoryStart.Before(default), new(1, 1000)));
        foreach (HistoryPageLimits limits in new[] { new HistoryPageLimits(0, 1000), new(-1, 1000), new(1, 0), new(1, -1) }) {
            Assert.Throws<ArgumentOutOfRangeException>(() => repository.ReadHistoryPage(HistoryStart.AtOrBefore(seed.Tip), limits));
        }
        Assert.Equal(0, visited);
        Assert.Single(repository.ReadHistoryPage(HistoryStart.AtOrBefore(seed.Tip), new(1, 1000)).Items);
        Assert.False(repository.IsFaulted);
    }

    [Fact]
    public void Work_exhaustion_before_anchor_and_after_a_checked_prefix_delivers_no_page_and_is_retryable() {
        var seed = Seed();
        var files = Files();
        using (Repository repository = Repository.OpenStorageReadOnlyExisting(_path, new())) {
            int visited = 0;
            repository.HistoryPageItemObserver = _ => visited++;
            CommitHistoryPage? delivered = null;
            BudgetFailure(() => delivered = repository.ReadHistoryPage(HistoryStart.AtOrBefore(seed.Tip), new(2, 1)));
            Assert.Null(delivered);
            Assert.Equal(0, visited);
            long oneItemWork = MinimumWork(repository, HistoryStart.AtOrBefore(seed.Tip));
            visited = 0;
            BudgetFailure(() => delivered = repository.ReadHistoryPage(HistoryStart.AtOrBefore(seed.Tip), new(2, oneItemWork)));
            Assert.Null(delivered);
            Assert.Equal(1, visited); // The failure really follows a complete checked descriptor.
            Assert.False(repository.IsFaulted);
            repository.HistoryPageItemObserver = null;
            var recovered = repository.ReadHistoryPage(HistoryStart.AtOrBefore(seed.Tip), new(2, 1000));
            Assert.Equal(new[] { seed.Tip, seed.Commits[^2] }, recovered.Items.Select(item => item.Id));
        }
        AssertFiles(files);
    }

    [Fact]
    public void Before_revalidates_and_charges_the_repeated_anchor_before_returning_older_items() {
        var seed = Seed();
        using Repository repository = Repository.OpenStorageReadOnlyExisting(_path, new());
        long inclusiveWork = MinimumWork(repository, HistoryStart.AtOrBefore(seed.Tip));
        long exclusiveWork = MinimumWork(repository, HistoryStart.Before(seed.Tip));
        Assert.True(exclusiveWork > inclusiveWork);
        List<CommitId> visited = [];
        repository.HistoryPageItemObserver = visited.Add;
        CommitHistoryPage? delivered = null;
        BudgetFailure(() => delivered = repository.ReadHistoryPage(HistoryStart.Before(seed.Tip), new(1, inclusiveWork)));
        Assert.Null(delivered);
        Assert.Equal(new[] { seed.Tip }, visited);
        visited.Clear();
        var page = repository.ReadHistoryPage(HistoryStart.Before(seed.Tip), new(1, exclusiveWork));
        Assert.Equal(seed.Commits[^2], Assert.Single(page.Items).Id);
        Assert.Equal(new[] { seed.Tip, seed.Commits[^2] }, visited);
        Assert.False(repository.IsFaulted);
    }

    [Fact]
    public void Precancellation_does_not_start_scanning_and_does_not_poison_a_retry() {
        var seed = Seed();
        using Repository repository = Repository.OpenStorageReadOnlyExisting(_path, new());
        using CancellationTokenSource cancel = new();
        cancel.Cancel();
        int visited = 0;
        repository.HistoryPageItemObserver = _ => visited++;
        CommitHistoryPage? delivered = null;
        OperationCanceledException error = Assert.ThrowsAny<OperationCanceledException>(() =>
            delivered = repository.ReadHistoryPage(HistoryStart.AtOrBefore(seed.Tip), new(2, 1000), cancel.Token));
        Assert.Equal(cancel.Token, error.CancellationToken);
        Assert.Null(delivered);
        Assert.Equal(0, visited);
        Assert.False(repository.IsFaulted);
        Assert.Single(repository.ReadHistoryPage(HistoryStart.AtOrBefore(seed.Tip), new(1, 1000)).Items);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Cancellation_during_checked_descriptors_discards_the_entire_prefix(int cancelAt) {
        var seed = Seed();
        using Repository repository = Repository.OpenStorageReadOnlyExisting(_path, new());
        using CancellationTokenSource cancel = new();
        int visited = 0;
        repository.HistoryPageItemObserver = _ => { if (++visited == cancelAt) { cancel.Cancel(); } };
        CommitHistoryPage? delivered = null;
        Assert.ThrowsAny<OperationCanceledException>(() => delivered = repository.ReadHistoryPage(
            HistoryStart.AtOrBefore(seed.Tip), new(2, 1000), cancel.Token));
        Assert.Null(delivered);
        Assert.Equal(cancelAt, visited);
        Assert.False(repository.IsFaulted);
        repository.HistoryPageItemObserver = null;
        Assert.Equal(2, repository.ReadHistoryPage(HistoryStart.AtOrBefore(seed.Tip), new(2, 1000)).Items.Count);
    }

    [Fact]
    public void Final_item_cleanup_cancellation_still_prevents_delivery() {
        var seed = Seed();
        using Repository repository = Repository.OpenStorageReadOnlyExisting(_path, new());
        using CancellationTokenSource cancel = new();
        ClosingFile? wrapped = null;
        repository.HistoryPageItemObserver = _ => wrapped = WrapTemporary(Resources(repository).Heads, cancel.Cancel);
        CommitHistoryPage? delivered = null;
        Assert.ThrowsAny<OperationCanceledException>(() => delivered = repository.ReadHistoryPage(
            HistoryStart.AtOrBefore(seed.Tip), new(1, 1000), cancel.Token));
        Assert.Null(delivered);
        Assert.NotNull(wrapped);
        Assert.Equal(1, wrapped.DisposeCalls);
        Assert.True(wrapped.InnerClosed); // Cancellation occurs after the full page's visitor returned and its file closed.
        Assert.False(repository.IsFaulted);
        repository.HistoryPageItemObserver = null;
        Assert.Single(repository.ReadHistoryPage(HistoryStart.AtOrBefore(seed.Tip), new(1, 1000)).Items);
    }

    [Fact]
    public void Reentry_and_busy_disposal_reject_without_damaging_the_outer_page() {
        var seed = Seed();
        using Repository repository = Repository.OpenStorageReadOnlyExisting(_path, new());
        int visited = 0;
        repository.HistoryPageItemObserver = _ => {
            visited++;
            Assert.Throws<InvalidOperationException>(() => repository.ReadHistoryPage(HistoryStart.AtOrBefore(seed.Tip), new(1, 1000)));
            Assert.Throws<InvalidOperationException>(() => repository.ReadCurrentCommit(seed.Branch));
            Assert.Throws<InvalidOperationException>(repository.Dispose);
        };
        Assert.Equal(2, repository.ReadHistoryPage(HistoryStart.AtOrBefore(seed.Tip), new(2, 1000)).Items.Count);
        Assert.Equal(2, visited);
        Assert.False(repository.IsFaulted);
        repository.HistoryPageItemObserver = null;
        repository.Dispose();
        Assert.Throws<ObjectDisposedException>(() => repository.ReadHistoryPage(HistoryStart.AtOrBefore(seed.Tip), new(1, 1000)));
    }

    [Fact]
    public void Stopped_graph_store_after_a_checked_item_cannot_deliver_a_page() {
        var seed = Seed();
        using Repository repository = Repository.OpenStorageReadOnlyExisting(_path, new());
        GraphRecordStore store = repository.GraphStore;
        repository.HistoryPageItemObserver = _ => store.Dispose();
        CommitHistoryPage? delivered = null;
        Assert.Throws<ObjectDisposedException>(() => delivered = repository.ReadHistoryPage(HistoryStart.AtOrBefore(seed.Tip), new(1, 1000)));
        Assert.Null(delivered);
        repository.HistoryPageItemObserver = null;
        Assert.Throws<ObjectDisposedException>(() => repository.ReadHistoryPage(HistoryStart.AtOrBefore(seed.Tip), new(1, 1000)));
    }

    [Fact]
    public void Temporary_head_cleanup_failure_prevents_full_page_delivery_and_requires_a_new_owner() {
        var seed = Seed();
        IOException failure = new("injected history temporary cleanup");
        ClosingFile? wrapped = null;
        using (Repository repository = Repository.OpenStorageReadOnlyExisting(_path, new())) {
            // Do not pre-read the target Head: the first query must really own a temporary file.
            repository.HistoryPageItemObserver = _ => wrapped = WrapTemporary(Resources(repository).Heads, () => throw failure);
            CommitHistoryPage? delivered = null;
            Assert.Same(failure, Assert.Throws<IOException>(() => delivered = repository.ReadHistoryPage(HistoryStart.AtOrBefore(seed.Tip), new(1, 1000))));
            Assert.Null(delivered);
            Assert.NotNull(wrapped);
            Assert.True(wrapped.InnerClosed);
            Assert.Equal(1, wrapped.DisposeCalls);
            Assert.True(repository.IsFaulted);
            Assert.Throws<InvalidOperationException>(() => repository.ReadHistoryPage(HistoryStart.AtOrBefore(seed.Tip), new(1, 1000)));
        }
        using Repository reopened = Repository.OpenStorageReadOnlyExisting(_path, new());
        Assert.Equal(seed.Tip, Assert.Single(reopened.ReadHistoryPage(HistoryStart.AtOrBefore(seed.Tip), new(1, 1000)).Items).Id);
    }

    [Fact]
    public void Dg_owner_fault_during_successful_history_cleanup_prevents_page_delivery() {
        var seed = Seed();
        using (Repository repository = Repository.OpenStorageReadOnlyExisting(_path, new())) {
            RepositoryResources resources = Resources(repository);
            Atelia.HeadStore.HeadStore heads = resources.Heads;
            ClosingFile? wrapped = null;
            repository.HistoryPageItemObserver = _ => wrapped = WrapTemporary(heads, resources.MarkFaulted);
            CommitHistoryPage? delivered = null;
            Assert.Throws<InvalidOperationException>(() => delivered = repository.ReadHistoryPage(
                HistoryStart.AtOrBefore(seed.Tip), new(1, 1000)));
            Assert.Null(delivered);
            Assert.NotNull(wrapped);
            Assert.Equal(1, wrapped.DisposeCalls);
            Assert.True(wrapped.InnerClosed);
            heads.EnsureUsable(); // HeadStore cleanup succeeded; DG's final owner check is independently necessary.
            Assert.True(repository.IsFaulted);
        }
        using Repository reopened = Repository.OpenStorageReadOnlyExisting(_path, new());
        Assert.Equal(seed.Tip, Assert.Single(reopened.ReadHistoryPage(HistoryStart.AtOrBefore(seed.Tip), new(1, 1000)).Items).Id);
    }

    [Fact]
    public void Management_unregistered_and_inexact_entry_anchors_do_not_become_business_history() {
        var seed = Seed();
        CommitId unregistered;
        CommitId[] controls;
        using (RepositoryResources offline = RepositoryResources.OpenStorageExisting(_path)) {
            byte[] valid = offline.Heads.ReadHead(seed.Branch.Value).Unwrap().Payload.ToArray();
            unregistered = new(offline.Heads.CreateHead(valid, out _).Unwrap());
            controls = new uint[] { 1, 2, 3 }.Select(id => new CommitId(offline.Heads.ReadHead(id).Unwrap().Id)).ToArray();
        }
        using Repository repository = Repository.OpenStorageReadOnlyExisting(_path, new());
        foreach (CommitId id in controls.Append(unregistered)) {
            Assert.Throws<KeyNotFoundException>(() => repository.ReadHistoryPage(HistoryStart.AtOrBefore(id), new(1, 1000)));
        }
        Span<byte> bytes = stackalloc byte[CommitId.EncodedSize];
        Assert.True(seed.Tip.TryWrite(bytes));
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[4..], SizedPtr.Create(4, 4).Packed); // Valid value codec, inside header rather than an Entry.
        Assert.True(CommitId.TryRead(bytes, out CommitId insideHeader));
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => repository.ReadHistoryPage(HistoryStart.AtOrBefore(insideHeader), new(1, 1000)));
        Assert.Contains("[HeadStore.InvalidHeadRecord]", error.Message);
        Assert.False(repository.IsFaulted);
        Assert.Equal(seed.Tip, Assert.Single(repository.ReadHistoryPage(HistoryStart.AtOrBefore(seed.Tip), new(1, 1000)).Items).Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Excluded_anchor_and_bad_middle_still_require_business_role(bool metaRole) {
        var seed = Seed();
        CommitId bad, current;
        // All public owners/caches are closed. Append a deliberately invalid semantic history using a separate raw owner.
        using (RepositoryResources offline = RepositoryResources.OpenStorageExisting(_path)) {
            byte[] valid = offline.Heads.ReadHead(seed.Branch.Value).Unwrap().Payload.ToArray();
            byte[] invalid = metaRole ? BusinessEntryPayloadCodec.Encode(offline.SharedSchemaRoot!.Value, []) : "invalid descriptor"u8.ToArray();
            bad = new(offline.Heads.PublishHead(seed.Branch.Value, invalid, out _).Unwrap());
            current = new(offline.Heads.PublishHead(seed.Branch.Value, valid, out _).Unwrap());
        }
        using Repository repository = Repository.OpenStorageReadOnlyExisting(_path, new());
        int visited = 0;
        repository.HistoryPageItemObserver = _ => visited++;
        Assert.Throws<InvalidDataException>(() => repository.ReadHistoryPage(HistoryStart.Before(bad), new(1, 1000)));
        Assert.Equal(0, visited); // Before must validate the excluded descriptor, not merely skip its Entry.
        CommitHistoryPage? delivered = null;
        Assert.Throws<InvalidDataException>(() => delivered = repository.ReadHistoryPage(HistoryStart.AtOrBefore(current), new(2, 1000)));
        Assert.Null(delivered);
        Assert.Equal(1, visited);
        Assert.False(repository.IsFaulted);
        repository.HistoryPageItemObserver = null;
        Assert.Equal(current, Assert.Single(repository.ReadHistoryPage(HistoryStart.AtOrBefore(current), new(1, 1000)).Items).Id);
    }

    [Fact]
    public void Inherited_unregistered_member_is_rejected_after_the_valid_local_prefix() {
        var seed = Seed();
        CommitId local;
        using (RepositoryResources offline = RepositoryResources.OpenStorageExisting(_path)) {
            byte[] valid = offline.Heads.ReadHead(seed.Branch.Value).Unwrap().Payload.ToArray();
            HeadEntryId foreign = offline.Heads.CreateHead(valid, out _).Unwrap();
            HeadId child = offline.Heads.ForkHead(foreign, out _).Unwrap();
            local = new(offline.Heads.PublishHead(child, valid, out _).Unwrap());
            RegisterBranch(offline, child);
        }
        using Repository repository = Repository.OpenStorageReadOnlyExisting(_path, new());
        int visited = 0;
        repository.HistoryPageItemObserver = _ => visited++;
        CommitHistoryPage? delivered = null;
        Assert.Throws<KeyNotFoundException>(() => delivered = repository.ReadHistoryPage(HistoryStart.AtOrBefore(local), new(2, 1000)));
        Assert.Null(delivered);
        Assert.Equal(1, visited);
        Assert.False(repository.IsFaulted);
        repository.HistoryPageItemObserver = null;
        Assert.Equal(local, Assert.Single(repository.ReadHistoryPage(HistoryStart.AtOrBefore(local), new(1, 1000)).Items).Id);
    }

    private (BranchId Branch, CommitId[] Commits, CommitId Tip) Seed() {
        using Repository repository = Repository.CreateStorageNew(_path, SharedReadModel.Models());
        BranchId branch = repository.CreateRootBranchFromState("main", new SharedReadModel.Node { Value = 1 }).Id;
        using WorkingCopy work = repository.OpenWorkingCopy(branch);
        List<CommitId> commits = [work.BaseCommitId];
        commits.Add(work.CommitEvent(new SharedReadModel.Node { Value = 2 }).Id);
        commits.Add(work.CommitState(new SharedReadModel.Node { Value = 3 }).Id);
        commits.Add(work.CommitEvent(new SharedReadModel.Node { Value = 4 }).Id);
        return (branch, commits.ToArray(), commits[^1]);
    }
    private static void BudgetFailure(Action read) => Assert.Contains("[HeadStore.HistoryBudgetExceeded]", Assert.Throws<InvalidOperationException>(read).Message);
    private static long MinimumWork(Repository repository, HistoryStart start) {
        for (long budget = 1; budget <= 256; budget++) {
            try { _ = repository.ReadHistoryPage(start, new(1, budget)); return budget; }
            catch (InvalidOperationException error) when (error.Message.Contains("[HeadStore.HistoryBudgetExceeded]", StringComparison.Ordinal)) { }
        }
        throw new Xunit.Sdk.XunitException("The finite four-Commit fixture did not complete one item within 256 work steps.");
    }
    private static void RegisterBranch(RepositoryResources owner, HeadId branch) {
        HeadEntry current = owner.Heads.ReadHead(1).Unwrap();
        FrameAddress? address = ManagementDirectoryPointer.Decode(ManagementDirectoryKind.Branches, current.Payload);
        DecodedGraphRows? accepted = address is { } selected ? ManagementDirectoryReader.ReadBranches(owner.GraphStore, selected) : null;
        PreparedDirectoryWrite candidate = ManagementDirectoryWriter.PrepareBranchAddition(owner.GraphStore, accepted, branch, "child");
        StoredGraphRevision graph = owner.GraphStore.AppendMeta(candidate.Write.SourceGraph, candidate.Write);
        owner.Heads.PublishHead(current.Id.HeadId, ManagementDirectoryPointer.Encode(ManagementDirectoryKind.Branches, graph.Address), out _).Unwrap();
        // Close immediately: this raw publication intentionally does not install the owner's cached D.
    }
    private static ClosingFile WrapTemporary(Atelia.HeadStore.HeadStore owner, Action afterClose) {
        FieldInfo field = typeof(Atelia.HeadStore.HeadStore).GetField("_historyHead", BindingFlags.Instance | BindingFlags.NonPublic)!;
        IRbfFile actual = Assert.IsAssignableFrom<IRbfFile>(field.GetValue(owner));
        ClosingFile wrapped = new(actual, afterClose);
        field.SetValue(owner, wrapped);
        return wrapped;
    }
    // This bounded injection wraps the actual acquired temporary. It always closes the real file
    // before reporting cancellation/failure; it neither fabricates history records nor repairs files.
    private sealed class ClosingFile(IRbfFile inner, Action afterClose) : IRbfFile {
        internal int DisposeCalls;
        internal bool InnerClosed;
        public void Dispose() { DisposeCalls++; inner.Dispose(); InnerClosed = true; afterClose(); }
        public RbfFormat Format => inner.Format;
        public long TailOffset => inner.TailOffset;
        public AteliaResult<SizedPtr> Append(uint tag, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> tailMeta = default) => inner.Append(tag, payload, tailMeta);
        public RbfFrameBuilder BeginAppend() => inner.BeginAppend();
        public RbfFrameBuilder BeginAppend(int payloadLength, int tailMetaLength, out SizedPtr ticket) => inner.BeginAppend(payloadLength, tailMetaLength, out ticket);
        public AteliaResult<RbfPooledFrame> ReadPooledFrame(SizedPtr ticket) => inner.ReadPooledFrame(ticket);
        public AteliaResult<RbfFrame> ReadFrame(SizedPtr ticket, Span<byte> buffer) => inner.ReadFrame(ticket, buffer);
        public RbfReverseSequence ScanReverse(bool showTombstone = false) => inner.ScanReverse(showTombstone);
        public AteliaResult<RbfReverseSequence> ScanReverse(RbfScanBoundary boundary, bool showTombstone = false) => inner.ScanReverse(boundary, showTombstone);
        public RbfForwardSequence ScanForward(bool showTombstone = false) => inner.ScanForward(showTombstone);
        public AteliaResult<RbfForwardSequence> ScanForward(RbfScanBoundary boundary, bool showTombstone = false) => inner.ScanForward(boundary, showTombstone);
        public AteliaResult<RbfScanBoundary> GetScanBoundaryAfter(SizedPtr ticket) => inner.GetScanBoundaryAfter(ticket);
        public long GetPhysicalOffsetImmediatelyAfter(SizedPtr ticket) => inner.GetPhysicalOffsetImmediatelyAfter(ticket);
        public AteliaResult<OptionalRbfFrameInfo> ReadFrameInfoImmediatelyAfter(SizedPtr ticket) => inner.ReadFrameInfoImmediatelyAfter(ticket);
        public AteliaResult<RbfFrameInfo> ReadFrameInfo(SizedPtr ticket) => inner.ReadFrameInfo(ticket);
        public AteliaResult<RbfTailMeta> ReadTailMeta(SizedPtr ticket, Span<byte> buffer) => inner.ReadTailMeta(ticket, buffer);
        public AteliaResult<RbfPooledTailMeta> ReadPooledTailMeta(SizedPtr ticket) => inner.ReadPooledTailMeta(ticket);
        public void DurableFlush() => inner.DurableFlush();
        public void SetupReadLog(string? path) => inner.SetupReadLog(path);
    }
    private Dictionary<string, byte[]> Files() => Directory.GetFiles(_path, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
    private void AssertFiles(Dictionary<string, byte[]> expected) {
        Assert.Equal(expected.Keys.Order(), Directory.GetFiles(_path, "*", SearchOption.AllDirectories).Order());
        foreach (var pair in expected) { Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key)); }
    }
    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}
