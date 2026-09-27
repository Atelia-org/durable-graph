using System.Reflection;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed partial class WorldWorkspaceTests {
    [Fact]
    public void HotInstallsMaintainExactHeadsAndPayloadWithoutReadingOldChains() {
        World world = new() { Text = new string('x', 16) };
        var workspace = WorldWorkspace.Create(_store, _schemas, world, Registry(Model()));
        using (var first = StageWithoutReadingChains(workspace, NoRebase)) { Install(first); }
        NormalizedRevision initial = AssertStoredBaseline(workspace);
        ObjectId textId = initial.Objects.Keys.Single(id => id != workspace.WorldId);
        for (int i = 1; i <= 5; i++) {
            ObjectStorageInfo prior = StorageOf(workspace, workspace.WorldId);
            world.Value++;
            if (i == 1) {
                NormalizedRevision source = Baseline(workspace);
                using (var abandoned = StageWithoutReadingChains(workspace, NoRebase)) {
                    Assert.Equal(ObjectVersionKind.Delta, Assert.Single(abandoned.Revision.LocalObjects).Kind);
                    abandoned.PrepareInstall(_store.Append(abandoned.Revision));
                    Assert.Same(source, Baseline(workspace));
                    Assert.Equal(prior, StorageOf(workspace, workspace.WorldId));
                }
                Assert.Same(source, Baseline(workspace));
                Assert.Equal(prior, StorageOf(workspace, workspace.WorldId));
            }
            using (var changed = StageWithoutReadingChains(workspace, NoRebase)) {
                Assert.Equal(ObjectVersionKind.Delta, Assert.Single(changed.Revision.LocalObjects).Kind);
                Install(changed);
            }
            AssertStoredBaseline(workspace);
            ObjectStorageInfo updated = StorageOf(workspace, workspace.WorldId);
            Assert.NotEqual(prior.Head, updated.Head);
            Assert.True(updated.ReconstructionPayloadBytes > prior.ReconstructionPayloadBytes);
            Assert.Equal(initial.Objects[textId].Storage, Baseline(workspace).Objects[textId].Storage);
            using (var unchanged = StageWithoutReadingChains(workspace, NoRebase)) {
                Assert.Empty(unchanged.Revision.LocalObjects);
                Install(unchanged);
            }
            Assert.Equal(updated, StorageOf(workspace, workspace.WorldId));
            AssertStoredBaseline(workspace);
        }
        world.Text = null;
        using (var removed = StageWithoutReadingChains(workspace, NoRebase)) {
            FrameAddress saved = Install(removed);
            Assert.DoesNotContain(textId.Value, _store.ReadLiveObjectHeadMap(saved).Keys);
        }
        Assert.DoesNotContain(textId, AssertStoredBaseline(workspace).Objects.Keys);
    }

    [Fact]
    public void SnapshotOptionalBaseDoesNotResetTheCommittedStatePayload() {
        World world = new();
        var workspace = WorldWorkspace.Create(_store, _schemas, world, Registry(Model()));
        using (var first = StageWithoutReadingChains(workspace, NoRebase)) { Install(first); }
        long basePayload = StorageOf(workspace, workspace.WorldId).ReconstructionPayloadBytes;
        world.Value++;
        using (var delta = StageWithoutReadingChains(workspace, NoRebase)) {
            Assert.Equal(ObjectVersionKind.Delta, Assert.Single(delta.Revision.LocalObjects).Kind);
            Install(delta);
        }
        NormalizedRevision source = AssertStoredBaseline(workspace);
        ObjectStorageInfo before = StorageOf(workspace, workspace.WorldId);
        Assert.True(before.ReconstructionPayloadBytes > basePayload);
        var counts = ObjectChainCounts();
        using (var snapshot = workspace.StageSnapshot(world, new(1, 100))) {
            Assert.Equal(counts, ObjectChainCounts());
            Assert.Equal(source.RevisionAddress, snapshot.Revision.ParentRevisionAddress);
            Assert.Equal(ObjectVersionKind.Base, Assert.Single(snapshot.Revision.LocalObjects).Kind);
            _store.Append(snapshot.Revision);
        }
        Assert.Same(source, Baseline(workspace));
        Assert.Equal(before, StorageOf(workspace, workspace.WorldId));
        using (var state = StageWithoutReadingChains(workspace, new(1, 100))) {
            Assert.Equal(source.RevisionAddress, state.Revision.ParentRevisionAddress);
            Assert.Equal(ObjectVersionKind.Base, Assert.Single(state.Revision.LocalObjects).Kind);
            Install(state);
        }
        AssertStoredBaseline(workspace);
        Assert.Equal(basePayload, StorageOf(workspace, workspace.WorldId).ReconstructionPayloadBytes);
        Assert.NotEqual(before.Head, StorageOf(workspace, workspace.WorldId).Head);
    }

    [Fact]
    public void DiscardAfterPreparingUpgradeInstallationPreservesSourceStorageAndRetry() {
        FrameAddress old = Seed(Old, new(5, 100, 0), Text(100, "removed by upgrade"));
        var models = Registry(Model(upgrade: state => state with { TextId = new ObjectId(0) }));
        var workspace = WorldWorkspace.Load(_store, _schemas, old, new ObjectId(1), models);
        NormalizedRevision source = AssertStoredBaseline(workspace);
        ObjectStorageInfo before = StorageOf(workspace, workspace.WorldId);
        Assert.Contains(new ObjectId(100), source.Objects.Keys);
        using (var abandoned = StageWithoutReadingChains(workspace, NoRebase)) {
            Assert.Equal(ObjectVersionKind.Base, Assert.Single(abandoned.Revision.LocalObjects).Kind);
            FrameAddress orphan = _store.Append(abandoned.Revision);
            abandoned.PrepareInstall(orphan);
            Assert.Same(source, Baseline(workspace));
            Assert.Equal(before, StorageOf(workspace, workspace.WorldId));
        }
        Assert.Same(source, Baseline(workspace));
        AssertStoredBaseline(workspace);
        using (var retry = StageWithoutReadingChains(workspace, NoRebase)) {
            Assert.Equal(old, retry.Revision.ParentRevisionAddress);
            Assert.Equal(ObjectVersionKind.Base, Assert.Single(retry.Revision.LocalObjects).Kind);
            FrameAddress saved = Install(retry);
            Assert.Equal(new uint[] { 1 }, _store.ReadLiveObjectHeadMap(saved).Keys);
        }
        NormalizedRevision installed = AssertStoredBaseline(workspace);
        Assert.Single(installed.Objects);
        Assert.False(installed.Objects[workspace.WorldId].RequiresRewrite);
        Assert.NotEqual(before.Head, StorageOf(workspace, workspace.WorldId).Head);
        ((World)workspace.World!).Value++;
        using (var delta = StageWithoutReadingChains(workspace, NoRebase)) {
            Assert.Equal(ObjectVersionKind.Delta, Assert.Single(delta.Revision.LocalObjects).Kind);
            Install(delta);
        }
        AssertStoredBaseline(workspace);
    }

    [Fact]
    public void CachedDecodedRowsCarryStorageIntoAnEditableLoad() {
        World world = new() { Text = new string('y', 8) };
        var models = Registry(Model());
        var workspace = WorldWorkspace.Create(_store, _schemas, world, models);
        using (var initial = StageWithoutReadingChains(workspace, NoRebase)) { Install(initial); }
        world.Value++;
        using (var delta = StageWithoutReadingChains(workspace, NoRebase)) { Install(delta); }
        FrameAddress parent = workspace.ParentRevisionAddress!.Value;
        RevisionReadSession reads = new(_store, _schemas, models.Snapshot(_schemas));
        DecodedRevision decoded = reads.Read(parent);
        Assert.Equal(decoded.Objects.Count, decoded.ObjectStorage.Count);
        var counts = ObjectChainCounts();
        var loaded = WorldWorkspace.LoadSnapshot(reads, parent, workspace.WorldId);
        Assert.Equal(counts, ObjectChainCounts());
        Assert.Equal(decoded.Objects.Count, reads.Statistics.CacheHits);
        Assert.NotSame(world, loaded.World);
        NormalizedRevision baseline = AssertStoredBaseline(loaded);
        foreach (var (id, info) in decoded.ObjectStorage) {
            Assert.Equal(info, baseline.Objects[id].Storage);
        }
        ((World)loaded.World!).Value++;
        using (var next = StageWithoutReadingChains(loaded, NoRebase)) {
            Assert.Equal(ObjectVersionKind.Delta, Assert.Single(next.Revision.LocalObjects).Kind);
            Install(next);
        }
        AssertStoredBaseline(loaded);
    }

    [Fact]
    public void LoadedPlanningRejectsMissingOrForeignStorageIncludingRowsBeingRemoved() {
        FrameAddress parent = Seed(Old, new(5, 100, 0), Text(100, "unreachable after normalization"));
        var models = Registry(Model(upgrade: state => state with { TextId = new ObjectId(0) })).Snapshot(_schemas);
        DecodedRevision decoded = new RevisionReadSession(_store, _schemas, models).Read(parent);
        NormalizedRevision valid = NormalizedRevision.Create(decoded, models);
        Assert.True(valid.Objects[new ObjectId(1)].Current.GetState<State>().TextId.IsNull);
        // Empty contents would remove every source row. Provenance must still be checked.
        var removed = LoadedRevisionPlanner.Prepare(_store, _schemas, valid, [], NoRebase);
        Assert.Empty(_store.ReadLiveObjectHeadMap(_store.Append(removed.Revision)));

        NormalizedRevision synthetic = NormalizedRevision.Create(new(parent, decoded.Objects, decoded.Strings), models);
        Assert.Throws<InvalidDataException>(() => LoadedRevisionPlanner.Prepare(_store, _schemas, synthetic, [], NoRebase));
        using (StateRevisionStore foreign = new(_segments)) {
            Assert.Throws<InvalidDataException>(() => LoadedRevisionPlanner.Prepare(foreign, _schemas, valid, [], NoRebase));
        }
        using (IRbfFile foreignFile = RbfFile.CreateNew(Path.Combine(_root, "foreign-schemas.rbf"))) {
            SchemaStore foreign = new(foreignFile);
            foreign.RegisterBatch([Old, Current]);
            Assert.Throws<InvalidDataException>(() => LoadedRevisionPlanner.Prepare(_store, foreign, valid, [], NoRebase));
        }
        Dictionary<ObjectId, ObjectStorageInfo> missing = decoded.ObjectStorage.ToDictionary(pair => pair.Key, pair => pair.Value);
        missing.Remove(new ObjectId(100));
        NormalizedRevision WithoutRemovedRowStorage() => NormalizedRevision.Create(
            new(parent, decoded.Objects, decoded.Strings, decoded.ObjectHeads, _store, _schemas, missing), models);
        Assert.Throws<InvalidDataException>(() => LoadedRevisionPlanner.Prepare(
            _store, _schemas, WithoutRemovedRowStorage(), [], NoRebase));

        FrameAddress unrelated = _store.Append(StateRevision.CreateObjectHeadMapDelta(parent, [], []));
        Dictionary<ObjectId, ObjectStorageInfo> wrongHead = decoded.ObjectStorage.ToDictionary(pair => pair.Key, pair => pair.Value);
        wrongHead[new ObjectId(100)] = wrongHead[new ObjectId(100)] with { Head = unrelated };
        NormalizedRevision WithWrongRemovedHead() => NormalizedRevision.Create(
            new(parent, decoded.Objects, decoded.Strings, decoded.ObjectHeads, _store, _schemas, wrongHead), models);
        Assert.Throws<InvalidDataException>(() => LoadedRevisionPlanner.Prepare(
            _store, _schemas, WithWrongRemovedHead(), [], NoRebase));
    }

    private PreparedWorldSave StageWithoutReadingChains(WorldWorkspace workspace,
        ReadAmplificationBaseBudgetParameters parameters) {
        var before = ObjectChainCounts();
        PreparedWorldSave pending = workspace.Stage(parameters);
        Assert.Equal(before, ObjectChainCounts());
        return pending;
    }

    private static NormalizedRevision Baseline(WorldWorkspace workspace) =>
        Assert.IsType<NormalizedRevision>(typeof(WorldWorkspace)
            .GetField("_baseline", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(workspace));

    private static ObjectStorageInfo StorageOf(WorldWorkspace workspace, ObjectId id) =>
        Assert.IsType<ObjectStorageInfo>(Baseline(workspace).Objects[id].Storage);

    private NormalizedRevision AssertStoredBaseline(WorldWorkspace workspace) {
        NormalizedRevision baseline = Baseline(workspace);
        Assert.Same(_store, baseline.SourceStore);
        Assert.Same(_schemas, baseline.SourceSchemas);
        // A separate facade validates wire H without warming the workspace Store's caches.
        using StateRevisionStore independent = new(_segments);
        Assert.Equal(baseline.Objects.Keys.Order(), independent.ReadLiveObjectHeadMap(baseline.RevisionAddress)
            .Keys.Select(id => new ObjectId(id)).Order());
        foreach (var (id, row) in baseline.Objects) {
            ObjectVersionChain chain = independent.ReadObjectVersionChain(baseline.RevisionAddress, id.Value);
            Assert.Equal(new ObjectStorageInfo(chain.ObjectHeadAddress, chain.ReconstructionPayloadBytes), row.Storage);
        }
        return baseline;
    }

    private (long Reads, long Entries) ObjectChainCounts() {
        object statistics = typeof(StateRevisionStore).GetProperty("TraversalStatistics",
            BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_store)!;
        long Get(string name) => (long)statistics.GetType().GetProperty(name)!.GetValue(statistics)!;
        return (Get("ObjectChainReads"), Get("ObjectChainEntries"));
    }
}
