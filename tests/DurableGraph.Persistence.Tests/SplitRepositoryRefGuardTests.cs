using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using Atelia.HeadStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class SplitRepositoryRefGuardTests : IDisposable {
    private const string Prefix = "dg-split-ref-guard-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));

    [Fact]
    public void PendingAndPreparingSavesRejectBranchAndTagInsertionWithoutConsumingTheSave() {
        Seed(_path);
        using RepositoryResources resources = RepositoryResources.OpenStorageExisting(_path);
        HeadId branch = Assert.Single(resources.ListBranches()).Key;
        HeadEntryId head = resources.ReadCurrent(branch).Id;
        PublicationSnapshot before = Snapshot(resources, branch);
        using (PendingRepositorySave pending = Prepare(resources, branch)) {
            Assert.True(resources.HasPendingSave);
            AssertBlocked(resources, head, "pending-tag");
            Assert.True(pending.IsPrepared);
            AssertUnchanged(resources, branch, before);
        }
        bool reached = false;
        using (PendingRepositorySave pending = resources.PrepareSaveFrom(branch, head, null, StoredGraphKind.State, default, () => {
            reached = true;
            AssertBlocked(resources, head, "preparing-tag");
            AssertUnchanged(resources, branch, before);
            return (Business(resources, head), null);
        })) {
            Assert.True(reached);
            Assert.True(pending.IsPrepared);
            AssertUnchanged(resources, branch, before);
        }
        Assert.False(resources.HasPendingSave);
        AssertTagMissing(resources, "pending-tag");
        AssertTagMissing(resources, "preparing-tag");
        HeadId fork = resources.ForkBranch(head, "allowed", out PublicationOutcome outcome);
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Assert.Equal(head, resources.ReadCurrent(fork).Id);
        resources.CreateStoredTag("allowed-tag", head, out outcome);
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Assert.Equal(head, resources.ResolveStoredTag("allowed-tag"));
        AssertSharedUnchanged(resources, before);
        Assert.True(resources.GraphStore.AppendedFrames > before.Frames); // D/T are real incremental management graphs.
        Assert.False(resources.IsFaulted);
    }

    [Fact]
    public void CommittingSaveRejectsNestedBranchAndTagInsertionAndConfirmsOnlyItsOwnPublication() {
        Seed(_path);
        using RepositoryResources resources = RepositoryResources.OpenStorageExisting(_path);
        HeadId branch = Assert.Single(resources.ListBranches()).Key;
        HeadEntryId head = resources.ReadCurrent(branch).Id;
        PublicationSnapshot before = Snapshot(resources, branch);
        HashSet<RepositorySaveStage> seen = [];
        resources.SaveStageObserver = point => {
            if (point is not (RepositorySaveStage.AfterBusinessAppend or RepositorySaveStage.BeforePublication or RepositorySaveStage.AfterPublication)) { return; }
            Assert.True(seen.Add(point));
            PublicationSnapshot atCheckpoint = Snapshot(resources, branch);
            AssertBlocked(resources, head, "committing-" + point);
            AssertUnchanged(resources, branch, atCheckpoint);
        };
        using PendingRepositorySave pending = Prepare(resources, branch);
        RepositorySaveResult saved = resources.CommitSave(pending);
        resources.SaveStageObserver = null;
        Assert.Equal(3, seen.Count);
        Assert.False(resources.HasPendingSave);
        Assert.False(resources.IsFaulted);
        Assert.Equal(saved.CommitId, resources.ReadCurrent(branch).Id);
        Assert.Single(resources.ListBranches());
        foreach (RepositorySaveStage point in seen) { AssertTagMissing(resources, "committing-" + point); }
        AssertSharedUnchanged(resources, before);
        Assert.Equal(before.DirectoryEntry, resources.Heads.ReadHead(1).Unwrap().Id); // Ordinary save never publishes D.
        HeadId fork = resources.ForkBranch(saved.CommitId, "after-commit", out PublicationOutcome outcome);
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Assert.Equal(saved.CommitId, resources.ReadCurrent(fork).Id);
        AssertSharedUnchanged(resources, before);
    }

    [Fact]
    public void ForeignOwnerAndConsumedSaveCandidatesRejectBeforePublicationAndLeaveBothOwnersHealthy() {
        Seed(_path);
        Seed(_path + "-foreign");
        using RepositoryResources resources = RepositoryResources.OpenStorageExisting(_path);
        using RepositoryResources foreign = RepositoryResources.OpenStorageExisting(_path + "-foreign");
        HeadId branch = Assert.Single(resources.ListBranches()).Key;
        HeadId foreignBranch = Assert.Single(foreign.ListBranches()).Key;
        PublicationSnapshot original = Snapshot(resources, branch), foreignBefore = Snapshot(foreign, foreignBranch);
        using PendingRepositorySave candidate = Prepare(resources, branch);
        Assert.Throws<InvalidOperationException>(() => foreign.CommitSave(candidate));
        AssertUnchanged(foreign, foreignBranch, foreignBefore);
        AssertUnchanged(resources, branch, original);
        Assert.False(foreign.IsFaulted);
        RepositorySaveResult accepted = resources.CommitSave(candidate);
        Assert.Equal(accepted.CommitId, resources.ReadCurrent(branch).Id);
        PublicationSnapshot afterAccepted = Snapshot(resources, branch);
        Assert.Throws<InvalidOperationException>(() => resources.CommitSave(candidate));
        Assert.Throws<InvalidOperationException>(() => resources.PrepareSaveFrom(branch, original.MainHead, null,
            StoredGraphKind.State, default, () => throw new Xunit.Sdk.XunitException("Stale expected Entry must reject before capture.")));
        AssertUnchanged(resources, branch, afterAccepted);
        Assert.False(resources.IsFaulted);
        using PendingRepositorySave renewed = Prepare(resources, branch);
        RepositorySaveResult next = resources.CommitSave(renewed);
        Assert.NotEqual(accepted.CommitId, next.CommitId);
        AssertSharedUnchanged(resources, original);
        AssertUnchanged(foreign, foreignBranch, foreignBefore);
    }

    private static void AssertBlocked(RepositoryResources resources, HeadEntryId source, string tag) {
        Assert.Throws<InvalidOperationException>(() => resources.ForkBranch(source, "blocked", out _));
        PublicationOutcome outcome = PublicationOutcome.Confirmed;
        Assert.Throws<InvalidOperationException>(() => resources.CreateStoredTag(tag, source, out outcome));
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Single(resources.ListBranches());
        Assert.False(resources.IsFaulted);
    }
    private static PendingRepositorySave Prepare(RepositoryResources resources, HeadId branch) {
        HeadEntryId head = resources.ReadCurrent(branch).Id;
        return resources.PrepareSaveFrom(branch, head, null, StoredGraphKind.State, default, () => (Business(resources, head), null));
    }
    private static PreparedGraphWrite Business(RepositoryResources resources, HeadEntryId head) {
        HistoryGraphRecord current = resources.ReadDescriptor(head);
        return new(current.Address, current.RootId, []); // Legal no-change State still gets a new Commit.
    }
    private static void Seed(string path) {
        using Repository repository = Repository.CreateStorageNew(path, SharedReadModel.Models());
        repository.CreateRootBranchFromState("main", new SharedReadModel.Node { Value = 7 });
    }
    private static PublicationSnapshot Snapshot(RepositoryResources resources, HeadId branch) {
        var directory = resources.Heads.ReadHead(1).Unwrap();
        var shared = resources.Heads.ReadHead(2).Unwrap();
        return new(directory.Id, directory.Payload.ToArray(), shared.Id, shared.Payload.ToArray(), resources.ReadCurrent(branch).Id,
            resources.SharedSchemaRoot!.Value, resources.GraphStore.AppendedFrames);
    }
    private static void AssertUnchanged(RepositoryResources resources, HeadId branch, PublicationSnapshot expected) {
        var directory = resources.Heads.ReadHead(1).Unwrap();
        Assert.Equal(expected.DirectoryEntry, directory.Id);
        Assert.Equal(expected.DirectoryPayload, directory.Payload.ToArray());
        Assert.Equal(expected.MainHead, resources.ReadCurrent(branch).Id);
        AssertSharedUnchanged(resources, expected);
        Assert.Equal(expected.Frames, resources.GraphStore.AppendedFrames);
    }
    private static void AssertSharedUnchanged(RepositoryResources resources, PublicationSnapshot expected) {
        var shared = resources.Heads.ReadHead(2).Unwrap();
        Assert.Equal(expected.SharedEntry, shared.Id);
        Assert.Equal(expected.SharedPayload, shared.Payload.ToArray());
        Assert.Equal(expected.SharedRoot, resources.SharedSchemaRoot);
    }
    private static void AssertTagMissing(RepositoryResources resources, string tag) =>
        Assert.Contains("[DurableGraph.TagNotFound]", Assert.Throws<InvalidOperationException>(() => resources.ResolveStoredTag(tag)).Message);
    private sealed record PublicationSnapshot(HeadEntryId DirectoryEntry, byte[] DirectoryPayload, HeadEntryId SharedEntry,
        byte[] SharedPayload, HeadEntryId MainHead, FrameAddress SharedRoot, long Frames);
    public void Dispose() {
        SharedReadModel.DeleteFixture(_path, Prefix);
        SharedReadModel.DeleteFixture(_path + "-foreign", Prefix);
    }
}
