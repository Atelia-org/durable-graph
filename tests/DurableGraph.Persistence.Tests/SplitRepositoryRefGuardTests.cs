
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using Atelia.HeadStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class SplitRepositoryRefGuardTests : IDisposable {
    private const string Prefix = "dg-split-ref-guard-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));

    [Fact]
    public void PendingAndPreparingSavesRejectBranchAndTagInsertionWithoutConsumingTheirCandidates() {
        using RepositoryResources resources = RepositoryResources.CreateStorageNew(_path);
        Seed(resources);
        FrameAddress head = resources.GetBranchHead("main")!.Value;
        RepositoryResources.PreparedBranchRootUpdate branch = resources.PrepareBranchRoot("allowed", head);
        PublicationSnapshot before = Snapshot(resources);

        using (PendingRepositorySave pending = resources.PrepareSave("main", StoredGraphKind.State, Business(resources, 2))) {
            Assert.True(resources.HasPendingSave);
            AssertBlocked(resources, branch, head, "pending-tag");
            Assert.True(pending.IsPrepared);
            AssertUnchanged(resources, before);
        }
        Assert.False(resources.HasPendingSave);

        bool reached = false;
        using (PendingRepositorySave pending = resources.PrepareSaveFrom("main", StoredGraphKind.State, () => {
            reached = true;
            AssertBlocked(resources, branch, head, "preparing-tag");
            AssertUnchanged(resources, before);
            return (Business(resources, 2), null);
        })) {
            Assert.True(reached);
            Assert.True(pending.IsPrepared);
            Assert.True(resources.HasPendingSave);
            AssertUnchanged(resources, before);
        }
        Assert.False(resources.HasPendingSave);
        AssertUnchanged(resources, before);
        AssertTagMissing(resources, "pending-tag");
        AssertTagMissing(resources, "preparing-tag");

        // The rejected publication never consumed this genuine owner-issued candidate.
        resources.PublishBranchRoot(branch, out PublicationOutcome branchOutcome);
        Assert.Equal(PublicationOutcome.Confirmed, branchOutcome);
        Assert.Equal(head, resources.GetBranchHead("allowed"));
        resources.CreateStoredTag("allowed-tag", head, out PublicationOutcome tagOutcome);
        Assert.Equal(PublicationOutcome.Confirmed, tagOutcome);
        Assert.Equal(head, resources.ResolveStoredTag("allowed-tag"));
        AssertSharedAndFramesUnchanged(resources, before);
        Assert.False(resources.IsFaulted);
    }

    [Fact]
    public void CommittingSaveRejectsNestedBranchAndTagInsertionAndConfirmsOnlyItsOwnPublication() {
        using RepositoryResources resources = RepositoryResources.CreateStorageNew(_path);
        Seed(resources);
        FrameAddress head = resources.GetBranchHead("main")!.Value;
        RepositoryResources.PreparedBranchRootUpdate branch = resources.PrepareBranchRoot("nested-branch", head);
        PublicationSnapshot before = Snapshot(resources);
        HashSet<RepositorySaveStage> seen = [];
        resources.SaveStageObserver = point => {
            if (point is not (RepositorySaveStage.AfterBusinessAppend or RepositorySaveStage.BeforePublication or RepositorySaveStage.AfterPublication)) {
                return;
            }
            Assert.True(seen.Add(point));
            PublicationSnapshot atCheckpoint = Snapshot(resources);
            AssertBlocked(resources, branch, head, "committing-" + point);
            AssertUnchanged(resources, atCheckpoint);
        };

        RepositorySaveResult saved;
        using (PendingRepositorySave pending = resources.PrepareSave("main", StoredGraphKind.State, Business(resources, 2))) {
            saved = resources.CommitSave(pending);
        }
        resources.SaveStageObserver = null;
        Assert.Equal(3, seen.Count);
        Assert.False(resources.HasPendingSave);
        Assert.False(resources.IsFaulted);
        Assert.Equal(saved.Business.Address, resources.GetBranchHead("main"));
        Assert.Null(resources.GetBranchHead("nested-branch"));
        foreach (RepositorySaveStage point in seen) { AssertTagMissing(resources, "committing-" + point); }
        AssertSharedUnchanged(resources, before);
        Assert.True(resources.GraphStore.AppendedFrames > before.Frames);

        long frames = resources.GraphStore.AppendedFrames;
        RepositoryResources.PreparedBranchRootUpdate fresh = resources.PrepareBranchRoot("after-commit", saved.Business.Address);
        resources.PublishBranchRoot(fresh, out PublicationOutcome outcome);
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Assert.Equal(saved.Business.Address, resources.GetBranchHead("after-commit"));
        Assert.Equal(frames, resources.GraphStore.AppendedFrames);
        AssertSharedUnchanged(resources, before);
    }

    [Fact]
    public void ForeignOwnerAndStaleBaselineCandidatesRejectBeforePublicationAndLeaveBothOwnersHealthy() {
        using RepositoryResources resources = RepositoryResources.CreateStorageNew(_path);
        using RepositoryResources foreign = RepositoryResources.CreateStorageNew(_path + "-foreign");
        Seed(resources);
        Seed(foreign);
        FrameAddress head = resources.GetBranchHead("main")!.Value;
        RepositoryResources.PreparedBranchRootUpdate candidate = resources.PrepareBranchRoot("from-owner", head);
        RepositoryResources.PreparedBranchRootUpdate stale = resources.PrepareBranchRoot("stale", head);
        PublicationSnapshot original = Snapshot(resources);
        PublicationSnapshot foreignBefore = Snapshot(foreign);

        PublicationOutcome foreignOutcome = PublicationOutcome.Confirmed;
        Assert.Throws<InvalidOperationException>(() => foreign.PublishBranchRoot(candidate, out foreignOutcome));
        Assert.Equal(PublicationOutcome.NotAttempted, foreignOutcome);
        AssertUnchanged(foreign, foreignBefore);
        AssertUnchanged(resources, original);
        Assert.False(foreign.IsFaulted);
        Assert.False(resources.IsFaulted);

        resources.PublishBranchRoot(candidate, out PublicationOutcome accepted);
        Assert.Equal(PublicationOutcome.Confirmed, accepted);
        Assert.Equal(head, resources.GetBranchHead("from-owner"));
        PublicationSnapshot afterAccepted = Snapshot(resources);
        PublicationOutcome staleOutcome = PublicationOutcome.Confirmed;
        Assert.Throws<InvalidOperationException>(() => resources.PublishBranchRoot(stale, out staleOutcome));
        Assert.Equal(PublicationOutcome.NotAttempted, staleOutcome);
        AssertUnchanged(resources, afterAccepted);
        Assert.Null(resources.GetBranchHead("stale"));
        Assert.False(resources.IsFaulted);

        RepositoryResources.PreparedBranchRootUpdate renewed = resources.PrepareBranchRoot("stale", head);
        resources.PublishBranchRoot(renewed, out PublicationOutcome renewedOutcome);
        Assert.Equal(PublicationOutcome.Confirmed, renewedOutcome);
        Assert.Equal(head, resources.GetBranchHead("stale"));
        AssertSharedAndFramesUnchanged(resources, original);
        AssertUnchanged(foreign, foreignBefore);
        Assert.False(resources.IsFaulted);
    }

    private static void AssertBlocked(RepositoryResources resources, RepositoryResources.PreparedBranchRootUpdate candidate,
        FrameAddress head, string tag) {
        Assert.Throws<InvalidOperationException>(() => resources.PrepareBranchRoot("blocked-prepare", head));
        PublicationOutcome branchOutcome = PublicationOutcome.Confirmed;
        Assert.Throws<InvalidOperationException>(() => resources.PublishBranchRoot(candidate, out branchOutcome));
        Assert.Equal(PublicationOutcome.NotAttempted, branchOutcome);
        PublicationOutcome tagOutcome = PublicationOutcome.Confirmed;
        Assert.Throws<InvalidOperationException>(() => resources.CreateStoredTag(tag, head, out tagOutcome));
        Assert.Equal(PublicationOutcome.NotAttempted, tagOutcome);
        Assert.Null(resources.GetBranchHead("blocked-prepare"));
        Assert.False(resources.IsFaulted);
    }

    // Opaque records isolate these concrete publication guards from model callbacks.
    private static void Seed(RepositoryResources resources) {
        using PendingRepositorySave pending = resources.PrepareSave("main", StoredGraphKind.State,
            Business(resources, 1), new(null, new(1), [ObjectVersionRecord.CreateBase(1, [1])]));
        resources.CommitSave(pending);
    }
    private static PreparedGraphWrite Business(RepositoryResources resources, byte value) =>
        new(resources.GetBranchStateHead("main"), new(1), [ObjectVersionRecord.CreateBase(1, [value])]);

    private static PublicationSnapshot Snapshot(RepositoryResources resources) {
        var branches = resources.Heads.ReadHead(1).Unwrap();
        var shared = resources.Heads.ReadHead(2).Unwrap();
        return new(branches.Id, branches.Payload.ToArray(), shared.Id, shared.Payload.ToArray(),
            resources.GetBranchHead("main")!.Value, resources.SharedSchemaRoot!.Value,
            resources.GraphStore.AppendedFrames);
    }
    private static void AssertUnchanged(RepositoryResources resources, PublicationSnapshot expected) {
        var branches = resources.Heads.ReadHead(1).Unwrap();
        Assert.Equal(expected.BranchRevision, branches.Id);
        Assert.Equal(expected.BranchPayload, branches.Payload.ToArray());
        Assert.Equal(expected.MainHead, resources.GetBranchHead("main"));
        AssertSharedAndFramesUnchanged(resources, expected);
    }
    private static void AssertSharedAndFramesUnchanged(RepositoryResources resources, PublicationSnapshot expected) {
        AssertSharedUnchanged(resources, expected);
        Assert.Equal(expected.Frames, resources.GraphStore.AppendedFrames);
    }
    private static void AssertSharedUnchanged(RepositoryResources resources, PublicationSnapshot expected) {
        var shared = resources.Heads.ReadHead(2).Unwrap();
        Assert.Equal(expected.SharedRevision, shared.Id);
        Assert.Equal(expected.SharedPayload, shared.Payload.ToArray());
        Assert.Equal(expected.SharedRoot, resources.SharedSchemaRoot);
    }
    private static void AssertTagMissing(RepositoryResources resources, string tag) =>
        Assert.Contains("[DurableGraph.TagNotFound]", Assert.Throws<InvalidOperationException>(() => resources.ResolveStoredTag(tag)).Message);

    private sealed record PublicationSnapshot(HeadEntryId BranchRevision, byte[] BranchPayload,
        HeadEntryId SharedRevision, byte[] SharedPayload, FrameAddress MainHead, FrameAddress SharedRoot, long Frames);

    public void Dispose() {
        SharedReadModel.DeleteFixture(_path, Prefix);
        SharedReadModel.DeleteFixture(_path + "-foreign", Prefix);
    }
}
