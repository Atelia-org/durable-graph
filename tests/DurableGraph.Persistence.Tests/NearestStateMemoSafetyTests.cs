using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using Atelia.HeadStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class NearestStateMemoSafetyTests : IDisposable {
    private const string Prefix = "dg-nearest-state-memo-safety-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WarmExactAnchorStillRejectsRawOldStateOrFalseAbsence(bool wrongNull) {
        SeedHistory seed = Seed();
        using RepositoryResources resources = RepositoryResources.OpenStorageExisting(_path);
        Assert.Equal(seed.LatestState.CommitId, resources.FindNearestState(seed.Tip.CommitId, true)!.Id);
        long warmed = resources.HistoryScanVisitedEntries;
        Assert.Equal(seed.LatestState.CommitId, resources.FindNearestState(seed.Tip.CommitId, true)!.Id);
        Assert.Equal(warmed, resources.HistoryScanVisitedEntries);
        long frames = resources.GraphStore.AppendedFrames;
        HeadEntry before = resources.Heads.ReadHead(seed.Branch).Unwrap();
        FrameAddress? wrong = wrongNull ? null : seed.InitialState.Business.Address;
        int aborts = 0;
        PendingRepositorySave? rejected = null;
        Assert.Throws<InvalidDataException>(() => rejected = resources.PrepareSaveGroup(() => new(
            [new(seed.Branch, seed.Tip.CommitId, null, StoredGraphKind.State, default,
                new(wrong, new(1), [ObjectVersionRecord.CreateBase(1, [99])]))], null), () => aborts++));
        Assert.Null(rejected);
        Assert.Equal(1, aborts);
        Assert.Equal(frames, resources.GraphStore.AppendedFrames);
        Assert.Equal(before.Id, resources.Heads.ReadHead(seed.Branch).Unwrap().Id);
        Assert.Equal(before.Payload.ToArray(), resources.Heads.ReadHead(seed.Branch).Unwrap().Payload.ToArray());
        Assert.False(resources.HasPendingSave);
        Assert.False(resources.IsFaulted);

        RepositorySaveResult valid = Save(resources, seed.Branch, seed.LatestState.Business.Address, StoredGraphKind.State, 99);
        Assert.Equal(valid.CommitId, resources.FindNearestState(valid.CommitId, true)!.Id);
        Assert.False(resources.IsFaulted);
    }

    [Fact]
    public void InclusiveFalseDoesNotReturnTheWarmedAnchorStateOrLoseTheEarlierBoundary() {
        SeedHistory seed = Seed();
        using RepositoryResources resources = RepositoryResources.OpenStorageExisting(_path);
        RepositorySaveResult latest = Save(resources, seed.Branch, seed.LatestState.Business.Address, StoredGraphKind.State, 50);
        Assert.Equal(latest.CommitId, resources.FindNearestState(latest.CommitId, true)!.Id);
        long warmed = resources.HistoryScanVisitedEntries;
        Assert.Equal(latest.CommitId, resources.FindNearestState(latest.CommitId, true)!.Id);
        Assert.Equal(warmed, resources.HistoryScanVisitedEntries);
        long before = resources.HistoryScanVisitedEntries;
        Assert.Equal(seed.LatestState.CommitId, resources.FindNearestState(latest.CommitId, false)!.Id);
        Assert.True(resources.HistoryScanVisitedEntries > before);
        Assert.Equal(seed.InitialState.CommitId, resources.FindNearestState(seed.LatestState.CommitId, false)!.Id);
        Assert.Equal(seed.LatestState.CommitId, resources.FindNearestState(seed.Tip.CommitId, false)!.Id);
        Assert.Null(resources.FindNearestState(seed.InitialState.CommitId, false));
        Assert.Equal(latest.CommitId, resources.FindNearestState(latest.CommitId, true)!.Id);
    }

    [Fact]
    public void ProvenEventOnlyAbsenceRejectsAnotherBranchStateAndAdvancesAtTheFirstRealState() {
        using RepositoryResources resources = RepositoryResources.CreateStorageNew(_path);
        RepositorySaveResult foreign = Save(resources, null, null, StoredGraphKind.State, 10);
        RepositorySaveResult eventOnly;
        using (PendingRepositorySave root = resources.PrepareSaveFrom(null, null, "event-only", StoredGraphKind.Event,
            default, () => (new(null, new(1), [ObjectVersionRecord.CreateBase(1, [20])]), null))) {
            eventOnly = resources.CommitSave(root);
        }
        Assert.Null(resources.FindNearestState(eventOnly.CommitId, true));
        long warmed = resources.HistoryScanVisitedEntries;
        Assert.Null(resources.FindNearestState(eventOnly.CommitId, true));
        Assert.Equal(warmed, resources.HistoryScanVisitedEntries);
        long frames = resources.GraphStore.AppendedFrames;
        Assert.Throws<InvalidDataException>(() => resources.PrepareSaveGroup(() => new(
            [new(eventOnly.BranchId, eventOnly.CommitId, null, StoredGraphKind.State, default,
                new(foreign.Business.Address, new(1), []))], null)));
        Assert.Equal(frames, resources.GraphStore.AppendedFrames);
        Assert.False(resources.HasPendingSave);
        Assert.False(resources.IsFaulted);
        RepositorySaveResult nextEvent = Save(resources, eventOnly.BranchId, null, StoredGraphKind.Event, 30);
        Assert.Null(resources.FindNearestState(nextEvent.CommitId, true));
        RepositorySaveResult firstState = Save(resources, eventOnly.BranchId, null, StoredGraphKind.State, 40);
        Assert.Equal(firstState.CommitId, resources.FindNearestState(firstState.CommitId, true)!.Id);
        Assert.False(resources.IsFaulted);
    }

    [Theory]
    [InlineData("OwnerFault")]
    [InlineData("OwnerDispose")]
    [InlineData("HeadsClose")]
    public void ExactAnchorMemoCannotBypassStoppedOwnerOrBacking(string stop) {
        SeedHistory seed = Seed();
        using RepositoryResources resources = RepositoryResources.OpenStorageReadOnlyExisting(_path);
        Assert.Equal(seed.LatestState.CommitId, resources.FindNearestState(seed.Tip.CommitId, true)!.Id);
        long warmed = resources.HistoryScanVisitedEntries;
        Assert.Equal(seed.LatestState.CommitId, resources.FindNearestState(seed.Tip.CommitId, true)!.Id);
        Assert.Equal(warmed, resources.HistoryScanVisitedEntries);
        switch (stop) {
            case "OwnerFault": resources.MarkFaulted(); break;
            case "OwnerDispose": resources.Dispose(); break;
            case "HeadsClose": resources.Heads.Dispose(); break;
            default: throw new InvalidOperationException(stop);
        }
        HistoryGraphRecord? delivered = null;
        Assert.ThrowsAny<InvalidOperationException>(() => delivered = resources.FindNearestState(seed.Tip.CommitId, true));
        Assert.Null(delivered);
        Assert.Equal(warmed, resources.HistoryScanVisitedEntries);
    }

    [Fact]
    public void ColdOwnerRevalidatesHistoryAndReturnsTheExactStateDescriptor() {
        SeedHistory seed = Seed();
        using (RepositoryResources first = RepositoryResources.OpenStorageReadOnlyExisting(_path)) {
            Assert.Equal(seed.LatestState.CommitId, first.FindNearestState(seed.Tip.CommitId, true)!.Id);
        }
        using RepositoryResources reopened = RepositoryResources.OpenStorageReadOnlyExisting(_path);
        Assert.Equal(0, reopened.HistoryScanVisitedEntries);
        HistoryGraphRecord state = reopened.FindNearestState(seed.Tip.CommitId, true)!;
        Assert.Equal(seed.LatestState.CommitId, state.Id);
        Assert.Equal(seed.LatestState.Business.Address, state.Address);
        Assert.Equal(seed.LatestState.Business.RootId, state.RootId);
        Assert.Equal(CommitKind.State, state.Kind);
        Assert.Equal(new byte[] { 30 }, state.Summary.ToArray());
        Assert.True(reopened.HistoryScanVisitedEntries > 0);
        long warmed = reopened.HistoryScanVisitedEntries;
        HistoryGraphRecord repeated = reopened.FindNearestState(seed.Tip.CommitId, true)!;
        Assert.Equal(state.Id, repeated.Id);
        Assert.Equal(state.Summary.ToArray(), repeated.Summary.ToArray());
        Assert.Equal(warmed, reopened.HistoryScanVisitedEntries);
    }

    [Fact]
    public void FailedTraversalCannotMemoizeItsCheckedEventPrefixAsNoState() {
        SeedHistory seed = Seed();
        HeadEntryId badTip;
        using (RepositoryResources offline = RepositoryResources.OpenStorageExisting(_path)) {
            byte[] validEvent = offline.Heads.ReadHead(seed.Branch).Unwrap().Payload.ToArray();
            offline.Heads.PublishHead(seed.Branch, "invalid business descriptor"u8, out _).Unwrap();
            badTip = offline.Heads.PublishHead(seed.Branch, validEvent, out _).Unwrap();
            // Deliberate raw offline fixture: close now; do not continue using the DG
            // publication baseline after bypassing its commit owner.
        }
        using RepositoryResources reader = RepositoryResources.OpenStorageReadOnlyExisting(_path);
        for (int attempt = 0; attempt < 2; attempt++) {
            long before = reader.HistoryScanVisitedEntries;
            HistoryGraphRecord? delivered = null;
            Assert.Throws<InvalidDataException>(() => delivered = reader.FindNearestState(badTip, true));
            Assert.Null(delivered);
            Assert.True(reader.HistoryScanVisitedEntries >= before + 2); // Valid tip, then the malformed middle.
            Assert.False(reader.IsFaulted);
        }
        Assert.Equal(seed.LatestState.CommitId, reader.FindNearestState(seed.Tip.CommitId, true)!.Id);
        Assert.False(reader.IsFaulted);
    }

    [Fact]
    public void HealthyOldPrefixDoesNotRequireTheUnselectedCorruptLatestSuffix() {
        RepositorySaveResult initial, earlier, later;
        using (RepositoryResources resources = RepositoryResources.CreateStorageNew(_path)) {
            initial = Save(resources, null, null, StoredGraphKind.State, 10);
            earlier = Save(resources, initial.BranchId, initial.Business.Address, StoredGraphKind.Event, 20);
            later = Save(resources, initial.BranchId, initial.Business.Address, StoredGraphKind.Event, 30);
        }
        // Controlled offline corruption only, after every DG/backing owner has closed.
        // This fixture has three reserved control files and exactly one business head.
        // Match the pinned storage HistoryFixture.FlipPayload midpoint injection; do
        // not recompute the CRC or alter the earlier Entry and its following Fence.
        string[] controls = ["00000001.rbf", "00000002.rbf", "00000003.rbf"];
        string business = Assert.Single(Directory.GetFiles(Path.Combine(_path, "versions", "refs"), "*.rbf"),
            path => !controls.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase));
        using (FileStream stream = new(business, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
            stream.Position = later.CommitId.Ticket.Offset + later.CommitId.Ticket.Length / 2;
            int original = stream.ReadByte();
            Assert.True(original >= 0);
            stream.Position--;
            stream.WriteByte((byte)(original ^ 1));
        }

        using RepositoryResources reader = RepositoryResources.OpenStorageReadOnlyExisting(_path);
        HistoryGraphRecord selected = reader.FindNearestState(earlier.CommitId, true)!;
        Assert.Equal(initial.CommitId, selected.Id);
        Assert.Equal(initial.Business.Address, selected.Address);
        Assert.Equal(new byte[] { 10 }, selected.Summary.ToArray());
        Assert.True(reader.HistoryScanVisitedEntries > 0);
        var current = reader.Heads.ReadHead(initial.BranchId);
        Assert.True(current.IsFailure);
        Assert.StartsWith("Rbf.", current.Error!.ErrorCode);
        Assert.False(reader.IsFaulted);
        Assert.Equal(initial.CommitId, reader.FindNearestState(earlier.CommitId, true)!.Id);
    }

    // Opaque graph bodies isolate history/source qualification from typed restoration.
    private SeedHistory Seed() {
        using RepositoryResources resources = RepositoryResources.CreateStorageNew(_path);
        RepositorySaveResult initial = Save(resources, null, null, StoredGraphKind.State, 10);
        Save(resources, initial.BranchId, initial.Business.Address, StoredGraphKind.Event, 20);
        RepositorySaveResult latest = Save(resources, initial.BranchId, initial.Business.Address, StoredGraphKind.State, 30);
        RepositorySaveResult tip = Save(resources, initial.BranchId, latest.Business.Address, StoredGraphKind.Event, 40);
        return new(initial.BranchId, initial, latest, tip);
    }

    private static RepositorySaveResult Save(RepositoryResources resources, HeadId? branch,
        FrameAddress? stateSource, StoredGraphKind kind, byte value) {
        HeadEntryId? expected = branch is { } id ? resources.ReadCurrent(id).Id : null;
        using PendingRepositorySave pending = resources.PrepareSaveFrom(branch, expected, branch is null ? "main" : null,
            kind, new byte[] { value }, () => (
                new(kind == StoredGraphKind.Event ? null : stateSource, new(1), [ObjectVersionRecord.CreateBase(1, [value])]),
                resources.SharedSchemaRoot is null ? new(null, new(1), [ObjectVersionRecord.CreateBase(1, [0x7F])]) : null));
        return resources.CommitSave(pending);
    }

    private sealed record SeedHistory(HeadId Branch, RepositorySaveResult InitialState,
        RepositorySaveResult LatestState, RepositorySaveResult Tip);

    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}
