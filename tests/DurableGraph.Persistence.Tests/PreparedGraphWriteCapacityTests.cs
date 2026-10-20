using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using Atelia.HeadStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class PreparedGraphWriteCapacityTests : IDisposable {
    private const string Prefix = "dg-prepared-graph-capacity-";
    private readonly List<string> _paths = [];

    [Fact]
    public void RealRecordLimitRejectsBeforeAppendAndPreservesHealthyPreparationAndPendingSaves() {
        // Allocate one real, unique-ID input for every heavy boundary below. No million-row
        // graph is appended: this witness exercises preparation, not large-graph throughput.
        ObjectVersionRecord[] oversized = Enumerable.Range(1, GraphFrameCodec.MaxCollectionCount + 1)
            .Select(id => ObjectVersionRecord.CreateBase((uint)id, [])).ToArray();
        AssertConstructorAndEncoderBoundaries(oversized);
        AssertFailedGroupPreparation(oversized, oversizedCatalog: true);
        AssertFailedGroupPreparation(oversized, oversizedCatalog: false);
        AssertExistingPendingRemainsCommittable(oversized);
    }

    private static void AssertConstructorAndEncoderBoundaries(ObjectVersionRecord[] oversized) {
        ArgumentException error = Assert.Throws<ArgumentException>(() => new PreparedGraphWrite(null, new(1), oversized));
        Assert.Equal("records", error.ParamName);
        PreparedGraphWrite maximum = new(null, new(1), oversized.Take(GraphFrameCodec.MaxCollectionCount));
        Assert.Equal(GraphFrameCodec.MaxCollectionCount, maximum.Records.Count);
        Assert.Equal((uint)GraphFrameCodec.MaxCollectionCount, maximum.Records[^1].ObjectId);
        ObjectVersionRecord original = oversized[0];
        oversized[0] = ObjectVersionRecord.CreateBase(1, [99]);
        try {
            Assert.Same(original, maximum.Records[0]);
            Assert.Empty(maximum.Records[0].Body.ToArray());
            Assert.False(maximum.Records is IList<ObjectVersionRecord>);
        } finally { oversized[0] = original; }
        Assert.Empty(new PreparedGraphWrite(null, new(1), []).Records);
        // The wire writer retains its independent nonempty/upper-bound defenses.
        Assert.Equal("records", Assert.Throws<ArgumentException>(() => GraphFrameCodec.EncodeBatch(null, null, [])).ParamName);
        Assert.Equal("records", Assert.Throws<ArgumentException>(() => GraphFrameCodec.EncodeBatch(null, null, oversized)).ParamName);
    }

    private void AssertFailedGroupPreparation(ObjectVersionRecord[] oversized, bool oversizedCatalog) {
        using RepositoryResources resources = RepositoryResources.CreateStorageNew(NewPath());
        RepositorySaveResult leftSeed = Seed(resources, "left", 10);
        RepositorySaveResult rightSeed = Seed(resources, "right", 20);
        HistoryGraphRecord left = resources.ReadCurrent(leftSeed.BranchId);
        HistoryGraphRecord right = resources.ReadCurrent(rightSeed.BranchId);
        FrameAddress? schemaBefore = resources.SharedSchemaRoot;
        HeadEntry[] headsBefore = ReadHeads(resources, leftSeed.BranchId, rightSeed.BranchId);
        long framesBefore = resources.GraphStore.AppendedFrames;
        int aborts = 0, appendStages = 0;
        bool firstPrepared = false, catalogPrepared = false;
        resources.SaveStageObserver = _ => appendStages++;
        PendingRepositorySave? failed = null;
        ArgumentException error = Assert.Throws<ArgumentException>(() => failed = resources.PrepareSaveGroup(() => {
            PreparedGraphWrite catalog = new(schemaBefore, new(1), [ObjectVersionRecord.CreateBase(1, [0x7F])]);
            catalogPrepared = true;
            PendingBusinessSave first = Member(left, 11);
            firstPrepared = true;
            if (oversizedCatalog) { catalog = new(schemaBefore, new(1), oversized); }
            PreparedGraphWrite secondWrite = new(right.Address, new(1), oversized);
            return new([first, new(right.Id.HeadId, right.Id, null, StoredGraphKind.State,
                default, secondWrite, right.Address)], catalog);
        }, () => aborts++));
        Assert.Equal("records", error.ParamName);
        Assert.True(firstPrepared);
        Assert.True(catalogPrepared);
        Assert.Null(failed);
        Assert.Equal(1, aborts);
        Assert.Equal(0, appendStages);
        Assert.Equal(framesBefore, resources.GraphStore.AppendedFrames);
        Assert.Equal(schemaBefore, resources.SharedSchemaRoot);
        Assert.False(resources.HasPendingSave);
        Assert.False(resources.IsFaulted);
        AssertSameHeads(headsBefore, ReadHeads(resources, leftSeed.BranchId, rightSeed.BranchId));

        // A real retry proves the preparation guard and pending slot were released.
        using PendingRepositorySave retry = resources.PrepareSaveGroup(() => new(
            [Member(left, 11), Member(right, 21)], null));
        Assert.True(resources.HasPendingSave);
        IReadOnlyList<RepositorySaveResult> committed = resources.CommitSaveGroup(retry);
        Assert.Equal(2, committed.Count);
        Assert.NotEqual(left.Id, resources.ReadCurrent(leftSeed.BranchId).Id);
        Assert.NotEqual(right.Id, resources.ReadCurrent(rightSeed.BranchId).Id);
        Assert.False(resources.HasPendingSave);
        Assert.False(resources.IsFaulted);
        Assert.Equal(1, aborts);
    }

    private void AssertExistingPendingRemainsCommittable(ObjectVersionRecord[] oversized) {
        using RepositoryResources resources = RepositoryResources.CreateStorageNew(NewPath());
        RepositorySaveResult seed = Seed(resources, "main", 10);
        HistoryGraphRecord source = resources.ReadCurrent(seed.BranchId);
        // Zero records is a legitimate unchanged save and must reuse the source directory.
        using PendingRepositorySave pending = resources.PrepareSaveGroup(() => new(
            [new(source.Id.HeadId, source.Id, null, StoredGraphKind.State, default,
                new(source.Address, new(1), []), source.Address)], null));
        long before = resources.GraphStore.AppendedFrames;
        HeadEntry[] headsBefore = ReadHeads(resources, seed.BranchId);
        // Do not attempt a second PrepareSaveGroup here: its exclusivity guard would
        // reject before reaching the capacity check we intend to exercise.
        Assert.Equal("records", Assert.Throws<ArgumentException>(() => new PreparedGraphWrite(source.Address, new(1), oversized)).ParamName);
        Assert.True(pending.IsPrepared);
        Assert.True(resources.HasPendingSave);
        Assert.False(resources.IsFaulted);
        Assert.Equal(before, resources.GraphStore.AppendedFrames);
        AssertSameHeads(headsBefore, ReadHeads(resources, seed.BranchId));
        RepositorySaveResult result = resources.CommitSave(pending);
        Assert.Null(result.Business.OwnedBatch);
        Assert.Equal(seed.Business.DirectoryRoot, result.Business.DirectoryRoot);
        Assert.NotEqual(source.Id, resources.ReadCurrent(seed.BranchId).Id);
        Assert.False(pending.IsPrepared);
        Assert.False(resources.HasPendingSave);
        Assert.False(resources.IsFaulted);
    }

    private static PendingBusinessSave Member(HistoryGraphRecord source, byte value) =>
        new(source.Id.HeadId, source.Id, null, StoredGraphKind.State, default,
            new(source.Address, new(1), [ObjectVersionRecord.CreateBase(1, [value])]), source.Address);

    private static RepositorySaveResult Seed(RepositoryResources resources, string label, byte value) {
        using PendingRepositorySave pending = resources.PrepareSaveFrom(null, null, label,
            StoredGraphKind.State, default, () => (
                new(null, new(1), [ObjectVersionRecord.CreateBase(1, [value])]),
                resources.SharedSchemaRoot is null ? new(null, new(1), [ObjectVersionRecord.CreateBase(1, [0x7F])]) : null));
        return resources.CommitSave(pending);
    }

    private static HeadEntry[] ReadHeads(RepositoryResources resources, params HeadId[] branches) =>
        new uint[] { 1, 2, 3 }.Select(id => resources.Heads.ReadHead(id).Unwrap())
            .Concat(branches.Select(id => resources.Heads.ReadHead(id).Unwrap())).ToArray();

    private static void AssertSameHeads(HeadEntry[] expected, HeadEntry[] actual) {
        Assert.Equal(expected.Length, actual.Length);
        for (int index = 0; index < expected.Length; index++) {
            Assert.Equal(expected[index].Id, actual[index].Id);
            Assert.Equal(expected[index].Payload.ToArray(), actual[index].Payload.ToArray());
        }
    }

    private string NewPath() {
        string path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
        _paths.Add(path);
        return path;
    }

    public void Dispose() {
        foreach (string path in _paths) { SharedReadModel.DeleteFixture(path, Prefix); }
    }
}
