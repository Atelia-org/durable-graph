using Atelia.DurableGraph.Persistence.MetaBootstrapP0;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using Atelia.VersionStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class SplitRepositoryRefGuardTests : IDisposable {
    private const string Prefix = "dg-split-ref-guard-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));

    [Fact]
    public void PendingAndPreparingSavesRejectBranchAndTagInsertionWithoutConsumingTheirCandidates() {
        using GraphResources resources = GraphResources.CreateMetaBootstrapNew(_path);
        Seed(resources);
        FrameAddress head = resources.GetMetaBootstrapHead("main")!.Value;
        GraphResources.MetaBootstrapBranchUpdate branch = resources.PrepareMetaBootstrapBranchRoot("allowed", head);
        PublicationSnapshot before = Snapshot(resources);

        using (P0PendingSave pending = resources.PrepareMetaBootstrap("main", P0GraphKind.State, Business(resources, 2))) {
            Assert.True(resources.HasMetaBootstrapPending);
            AssertBlocked(resources, branch, head, "pending-tag");
            Assert.True(pending.IsPrepared);
            AssertUnchanged(resources, before);
        }
        Assert.False(resources.HasMetaBootstrapPending);

        bool reached = false;
        using (P0PendingSave pending = resources.PrepareMetaBootstrapFrom("main", P0GraphKind.State, () => {
            reached = true;
            AssertBlocked(resources, branch, head, "preparing-tag");
            AssertUnchanged(resources, before);
            return (Business(resources, 2), null);
        })) {
            Assert.True(reached);
            Assert.True(pending.IsPrepared);
            Assert.True(resources.HasMetaBootstrapPending);
            AssertUnchanged(resources, before);
        }
        Assert.False(resources.HasMetaBootstrapPending);
        AssertUnchanged(resources, before);
        AssertTagMissing(resources, "pending-tag");
        AssertTagMissing(resources, "preparing-tag");

        // The rejected publication never consumed this genuine owner-issued candidate.
        resources.PublishMetaBootstrapBranchRoot(branch, out PublicationOutcome branchOutcome);
        Assert.Equal(PublicationOutcome.Confirmed, branchOutcome);
        Assert.Equal(head, resources.GetMetaBootstrapHead("allowed"));
        resources.CreateMetaBootstrapTag("allowed-tag", head, out PublicationOutcome tagOutcome);
        Assert.Equal(PublicationOutcome.Confirmed, tagOutcome);
        Assert.Equal(head, resources.ResolveMetaBootstrapTag("allowed-tag"));
        AssertSharedAndFramesUnchanged(resources, before);
        Assert.False(resources.IsFaulted);
    }

    [Fact]
    public void CommittingSaveRejectsNestedBranchAndTagInsertionAndConfirmsOnlyItsOwnPublication() {
        using GraphResources resources = GraphResources.CreateMetaBootstrapNew(_path);
        Seed(resources);
        FrameAddress head = resources.GetMetaBootstrapHead("main")!.Value;
        GraphResources.MetaBootstrapBranchUpdate branch = resources.PrepareMetaBootstrapBranchRoot("nested-branch", head);
        PublicationSnapshot before = Snapshot(resources);
        HashSet<P0SaveCheckpoint> seen = [];
        resources.MetaBootstrapCheckpoint = point => {
            if (point is not (P0SaveCheckpoint.AfterBusinessAppend or P0SaveCheckpoint.BeforePublication or P0SaveCheckpoint.AfterPublication)) {
                return;
            }
            Assert.True(seen.Add(point));
            PublicationSnapshot atCheckpoint = Snapshot(resources);
            AssertBlocked(resources, branch, head, "committing-" + point);
            AssertUnchanged(resources, atCheckpoint);
        };

        P0SaveResult saved;
        using (P0PendingSave pending = resources.PrepareMetaBootstrap("main", P0GraphKind.State, Business(resources, 2))) {
            saved = resources.CommitMetaBootstrap(pending);
        }
        resources.MetaBootstrapCheckpoint = null;
        Assert.Equal(3, seen.Count);
        Assert.False(resources.HasMetaBootstrapPending);
        Assert.False(resources.IsFaulted);
        Assert.Equal(saved.Business.Address, resources.GetMetaBootstrapHead("main"));
        Assert.Null(resources.GetMetaBootstrapHead("nested-branch"));
        foreach (P0SaveCheckpoint point in seen) { AssertTagMissing(resources, "committing-" + point); }
        AssertSharedUnchanged(resources, before);
        Assert.True(resources.MetaBootstrapStore.AppendedFrames > before.Frames);

        long frames = resources.MetaBootstrapStore.AppendedFrames;
        GraphResources.MetaBootstrapBranchUpdate fresh = resources.PrepareMetaBootstrapBranchRoot("after-commit", saved.Business.Address);
        resources.PublishMetaBootstrapBranchRoot(fresh, out PublicationOutcome outcome);
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Assert.Equal(saved.Business.Address, resources.GetMetaBootstrapHead("after-commit"));
        Assert.Equal(frames, resources.MetaBootstrapStore.AppendedFrames);
        AssertSharedUnchanged(resources, before);
    }

    [Fact]
    public void ForeignOwnerAndStaleBaselineCandidatesRejectBeforePublicationAndLeaveBothOwnersHealthy() {
        using GraphResources resources = GraphResources.CreateMetaBootstrapNew(_path);
        using GraphResources foreign = GraphResources.CreateMetaBootstrapNew(_path + "-foreign");
        Seed(resources);
        Seed(foreign);
        FrameAddress head = resources.GetMetaBootstrapHead("main")!.Value;
        GraphResources.MetaBootstrapBranchUpdate candidate = resources.PrepareMetaBootstrapBranchRoot("from-owner", head);
        GraphResources.MetaBootstrapBranchUpdate stale = resources.PrepareMetaBootstrapBranchRoot("stale", head);
        PublicationSnapshot original = Snapshot(resources);
        PublicationSnapshot foreignBefore = Snapshot(foreign);

        PublicationOutcome foreignOutcome = PublicationOutcome.Confirmed;
        Assert.Throws<InvalidOperationException>(() => foreign.PublishMetaBootstrapBranchRoot(candidate, out foreignOutcome));
        Assert.Equal(PublicationOutcome.NotAttempted, foreignOutcome);
        AssertUnchanged(foreign, foreignBefore);
        AssertUnchanged(resources, original);
        Assert.False(foreign.IsFaulted);
        Assert.False(resources.IsFaulted);

        resources.PublishMetaBootstrapBranchRoot(candidate, out PublicationOutcome accepted);
        Assert.Equal(PublicationOutcome.Confirmed, accepted);
        Assert.Equal(head, resources.GetMetaBootstrapHead("from-owner"));
        PublicationSnapshot afterAccepted = Snapshot(resources);
        PublicationOutcome staleOutcome = PublicationOutcome.Confirmed;
        Assert.Throws<InvalidOperationException>(() => resources.PublishMetaBootstrapBranchRoot(stale, out staleOutcome));
        Assert.Equal(PublicationOutcome.NotAttempted, staleOutcome);
        AssertUnchanged(resources, afterAccepted);
        Assert.Null(resources.GetMetaBootstrapHead("stale"));
        Assert.False(resources.IsFaulted);

        GraphResources.MetaBootstrapBranchUpdate renewed = resources.PrepareMetaBootstrapBranchRoot("stale", head);
        resources.PublishMetaBootstrapBranchRoot(renewed, out PublicationOutcome renewedOutcome);
        Assert.Equal(PublicationOutcome.Confirmed, renewedOutcome);
        Assert.Equal(head, resources.GetMetaBootstrapHead("stale"));
        AssertSharedAndFramesUnchanged(resources, original);
        AssertUnchanged(foreign, foreignBefore);
        Assert.False(resources.IsFaulted);
    }

    private static void AssertBlocked(GraphResources resources, GraphResources.MetaBootstrapBranchUpdate candidate,
        FrameAddress head, string tag) {
        Assert.Throws<InvalidOperationException>(() => resources.PrepareMetaBootstrapBranchRoot("blocked-prepare", head));
        PublicationOutcome branchOutcome = PublicationOutcome.Confirmed;
        Assert.Throws<InvalidOperationException>(() => resources.PublishMetaBootstrapBranchRoot(candidate, out branchOutcome));
        Assert.Equal(PublicationOutcome.NotAttempted, branchOutcome);
        PublicationOutcome tagOutcome = PublicationOutcome.Confirmed;
        Assert.Throws<InvalidOperationException>(() => resources.CreateMetaBootstrapTag(tag, head, out tagOutcome));
        Assert.Equal(PublicationOutcome.NotAttempted, tagOutcome);
        Assert.Null(resources.GetMetaBootstrapHead("blocked-prepare"));
        Assert.False(resources.IsFaulted);
    }

    // Opaque records isolate these concrete publication guards from model callbacks.
    private static void Seed(GraphResources resources) {
        using P0PendingSave pending = resources.PrepareMetaBootstrap("main", P0GraphKind.State,
            Business(resources, 1), new(null, new(1), [ObjectVersionRecord.CreateBase(1, [1])]));
        resources.CommitMetaBootstrap(pending);
    }
    private static P0PreparedGraphWrite Business(GraphResources resources, byte value) =>
        new(resources.GetMetaBootstrapStateHead("main"), new(1), [ObjectVersionRecord.CreateBase(1, [value])]);

    private static PublicationSnapshot Snapshot(GraphResources resources) {
        var branches = resources.Versions.ReadRef(1).Unwrap();
        var shared = resources.Versions.ReadRef(2).Unwrap();
        return new(branches.Revision, branches.Payload.ToArray(), shared.Revision, shared.Payload.ToArray(),
            resources.GetMetaBootstrapHead("main")!.Value, resources.SharedMetaBootstrapRoot!.Value,
            resources.MetaBootstrapStore.AppendedFrames);
    }
    private static void AssertUnchanged(GraphResources resources, PublicationSnapshot expected) {
        var branches = resources.Versions.ReadRef(1).Unwrap();
        Assert.Equal(expected.BranchRevision, branches.Revision);
        Assert.Equal(expected.BranchPayload, branches.Payload.ToArray());
        Assert.Equal(expected.MainHead, resources.GetMetaBootstrapHead("main"));
        AssertSharedAndFramesUnchanged(resources, expected);
    }
    private static void AssertSharedAndFramesUnchanged(GraphResources resources, PublicationSnapshot expected) {
        AssertSharedUnchanged(resources, expected);
        Assert.Equal(expected.Frames, resources.MetaBootstrapStore.AppendedFrames);
    }
    private static void AssertSharedUnchanged(GraphResources resources, PublicationSnapshot expected) {
        var shared = resources.Versions.ReadRef(2).Unwrap();
        Assert.Equal(expected.SharedRevision, shared.Revision);
        Assert.Equal(expected.SharedPayload, shared.Payload.ToArray());
        Assert.Equal(expected.SharedRoot, resources.SharedMetaBootstrapRoot);
    }
    private static void AssertTagMissing(GraphResources resources, string tag) =>
        Assert.Contains("[VersionStore.TagNotFound]", Assert.Throws<InvalidOperationException>(() => resources.ResolveMetaBootstrapTag(tag)).Message);

    private sealed record PublicationSnapshot(RefRevision BranchRevision, byte[] BranchPayload,
        RefRevision SharedRevision, byte[] SharedPayload, FrameAddress MainHead, FrameAddress SharedRoot, long Frames);

    public void Dispose() {
        SharedReadModel.DeleteFixture(_path, Prefix);
        SharedReadModel.DeleteFixture(_path + "-foreign", Prefix);
    }
}
