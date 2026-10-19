using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using Atelia.HeadStore;
using DataStore = Atelia.FrameStore.FrameStore;
using Heads = Atelia.HeadStore.HeadStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class ManagementDirectoryCandidateTests : IDisposable {
    private const string Prefix = "dg-directory-candidate-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Additions_preserve_baseline_and_cold_identity_and_use_real_planner(bool bookmarks) {
        FrameAddress last = default;
        HeadEntryId target;
        int deltas = 0, laterBases = 0;
        using (var data = DataStore.Create(_path))
        using (var heads = Heads.Create(_path + "-heads", data))
        using (var store = new GraphRecordStore(data)) {
            target = heads.CreateHead("target"u8.ToArray(), out _).Unwrap();
            DecodedGraphRows? accepted = null;
            for (int i = 0; i < 80; i++) {
                HeadEntryId id = heads.CreateHead("target"u8.ToArray(), out _).Unwrap();
                var candidate = bookmarks ? ManagementDirectoryWriter.PrepareBookmarkAddition(store, accepted, $"name-{i:D3}", target)
                    : ManagementDirectoryWriter.PrepareBranchAddition(store, accepted, id.HeadId, "重复标签");
                Assert.Throws<InvalidOperationException>(() => candidate.GetInstallation(accepted));
                var before = accepted;
                var graph = store.Append(StoredGraphKind.Meta, candidate.Write.SourceGraph, null, candidate.Write);
                candidate.PrepareInstall(graph);
                var installed = candidate.GetInstallation(accepted);
                Assert.Same(before, accepted);
                if (accepted is not null) {
                    Assert.Equal(accepted.Snapshot.Address, graph.SourceGraph);
                    foreach (var pair in accepted.Snapshot.ObjectHeads.Where(pair => pair.Key != graph.RootId.Value)) {
                        Assert.Equal(pair.Value, graph.ObjectHeads[pair.Key]);
                    }
                    var root = Assert.Single(candidate.Write.Records, row => row.ObjectId == graph.RootId.Value);
                    if (root.Kind == ObjectVersionKind.Delta) { deltas++; } else { laterBases++; }
                    Assert.Throws<InvalidOperationException>(() => candidate.GetInstallation(installed));
                }
                accepted = installed;
                last = graph.Address;
                if (bookmarks) {
                    Assert.Throws<ArgumentException>(() => ManagementDirectoryWriter.PrepareBookmarkAddition(store, accepted, $"name-{i:D3}", target));
                } else {
                    Assert.Throws<ArgumentException>(() => ManagementDirectoryWriter.PrepareBranchAddition(store, accepted, id.HeadId, "different"));
                }
            }
            data.ConfirmDurable();
        }
        Assert.True(deltas > 0);
        Assert.True(laterBases > 0);
        using (var data = DataStore.Open(_path))
        using (var heads = Heads.Open(_path + "-heads", data))
        using (var store = new GraphRecordStore(data)) {
            var before = bookmarks ? ManagementDirectoryReader.ReadBookmarks(store, last) : ManagementDirectoryReader.ReadBranches(store, last);
            var id = heads.CreateHead("cold"u8.ToArray(), out _).Unwrap();
            var candidate = bookmarks ? ManagementDirectoryWriter.PrepareBookmarkAddition(store, before, "cold", target)
                : ManagementDirectoryWriter.PrepareBranchAddition(store, before, id.HeadId, "冷续写");
            var graph = store.Append(StoredGraphKind.Meta, last, null, candidate.Write);
            candidate.PrepareInstall(graph);
            var after = candidate.GetInstallation(before);
            Assert.Equal(last, after.Snapshot.SourceGraph);
            Assert.Equal(before.Rows.Count + (bookmarks ? 2 : 1), after.Rows.Count);
            last = graph.Address;
            data.ConfirmDurable();
        }
        using (var data = DataStore.Open(_path))
        using (var store = new GraphRecordStore(data)) {
            var restored = bookmarks ? ManagementDirectoryReader.ReadBookmarks(store, last) : ManagementDirectoryReader.ReadBranches(store, last);
            var root = restored.Snapshot.RootId;
            int count = bookmarks ? restored.Rows[root].GetDictionaryState<ObjectId, ObjectId>().Count
                : restored.Rows[root].GetDictionaryState<uint, ObjectId>().Count;
            Assert.Equal(81, count);
            Assert.Equal(last, restored.Snapshot.Address);
            Assert.Equal(store.ReadObjectChain(last, root.Value).Entries.Sum(entry => (long)entry.PayloadBytes),
                restored.Storage[root].ReconstructionPayloadBytes);
        }
    }

    [Fact]
    public void Install_rejects_wrong_owner_source_root_records_and_missing_members_without_poisoning_candidate() {
        using var data = DataStore.Create(_path);
        using var heads = Heads.Create(_path + "-heads", data);
        using var store = new GraphRecordStore(data);
        var id = heads.CreateHead("a"u8.ToArray(), out _).Unwrap();
        var first = ManagementDirectoryWriter.PrepareBranchAddition(store, null, id.HeadId, "a");
        var g0 = store.Append(StoredGraphKind.Meta, null, null, first.Write);
        first.PrepareInstall(g0);
        var accepted = first.GetInstallation(null);
        var id2 = heads.CreateHead("b"u8.ToArray(), out _).Unwrap();
        var next = ManagementDirectoryWriter.PrepareBranchAddition(store, accepted, id2.HeadId, "b");
        using (var other = new GraphRecordStore(data)) {
            Assert.Throws<InvalidDataException>(() => next.PrepareInstall(other.ReadGraph(g0.Address)));
        }
        Assert.Throws<InvalidDataException>(() => next.PrepareInstall(g0));
        var wrongText = ManagementDirectoryWriter.PrepareBranchAddition(store, accepted, id2.HeadId, "wrong");
        var badRecords = store.Append(StoredGraphKind.Meta, g0.Address, null, wrongText.Write);
        Assert.Throws<InvalidDataException>(() => next.PrepareInstall(badRecords));
        var wrongRoot = store.Append(StoredGraphKind.Meta, g0.Address, null,
            new(g0.Address, new(2), next.Write.Records));
        Assert.Throws<InvalidDataException>(() => next.PrepareInstall(wrongRoot));
        var missing = store.Append(StoredGraphKind.Meta, g0.Address, null,
            new(g0.Address, next.Write.RootId, next.Write.Records, [2]));
        Assert.Throws<InvalidDataException>(() => next.PrepareInstall(missing));
        var correct = store.Append(StoredGraphKind.Meta, g0.Address, null, next.Write);
        next.PrepareInstall(correct);
        Assert.Equal(correct.Address, next.GetInstallation(accepted).Snapshot.Address);
        Assert.Equal(2, accepted.Rows.Count);
        store.Dispose();
        Assert.Throws<ObjectDisposedException>(() => next.GetInstallation(accepted));
    }

    [Fact]
    public void Inputs_and_role_are_checked_before_preparation() {
        using var data = DataStore.Create(_path);
        using var heads = Heads.Create(_path + "-heads", data);
        using var store = new GraphRecordStore(data);
        var id = heads.CreateHead("a"u8.ToArray(), out _).Unwrap();
        Assert.Throws<ArgumentException>(() => ManagementDirectoryWriter.PrepareBranchAddition(store, null, default, "a"));
        Assert.Throws<ArgumentException>(() => ManagementDirectoryWriter.PrepareBookmarkAddition(store, null, "a", default));
        Assert.Throws<InvalidDataException>(() => ManagementDirectoryWriter.PrepareBranchAddition(store, null, id.HeadId, "\ud800"));
        var first = ManagementDirectoryWriter.PrepareBookmarkAddition(store, null, "a", id);
        var graph = store.Append(StoredGraphKind.Meta, null, null, first.Write);
        first.PrepareInstall(graph);
        var accepted = first.GetInstallation(null);
        Assert.Throws<InvalidDataException>(() => ManagementDirectoryWriter.PrepareBranchAddition(store, accepted, id.HeadId, "a"));
        var row = accepted.Rows.Values.Single(row => row.Layout.Array is not null).GetArrayState<byte>();
        byte[] copy = row.Elements.ToArray();
        Array.Clear(copy);
        Assert.True(HeadEntryId.TryRead(row.Elements, out var decoded));
        Assert.Equal(id, decoded);
    }

    [Fact]
    public void Unreachable_live_rows_survive_and_allocation_checks_uint_exhaustion() {
        using var data = DataStore.Create(_path);
        using var heads = Heads.Create(_path + "-heads", data);
        using var store = new GraphRecordStore(data);
        var id = heads.CreateHead("a"u8.ToArray(), out _).Unwrap();
        var first = ManagementDirectoryWriter.PrepareBranchAddition(store, null, id.HeadId, "a");
        var leaf = ObjectVersionRecord.CreateBase(100, TypedBaseBodyCodec.Encode(
            Atelia.DurableGraph.Schema.ObjectLayout.String, Atelia.DurableGraph.Serialization.StringBodyPreparer.PrepareBase("unreachable")));
        var initial = store.Append(StoredGraphKind.Meta, null, null, new(null, new(1), first.Write.Records.Append(leaf)));
        var accepted = ManagementDirectoryReader.ReadBranches(store, initial.Address);
        var id2 = heads.CreateHead("b"u8.ToArray(), out _).Unwrap();
        var next = ManagementDirectoryWriter.PrepareBranchAddition(store, accepted, id2.HeadId, "b");
        Assert.Contains(next.Write.Records, record => record.ObjectId == 101);
        var actual = store.Append(StoredGraphKind.Meta, initial.Address, null, next.Write);
        next.PrepareInstall(actual);
        Assert.Equal(initial.ObjectHeads[100], next.GetInstallation(accepted).Snapshot.ObjectHeads[100]);
        var exhausted = ObjectVersionRecord.CreateBase(uint.MaxValue, leaf.Body);
        var full = store.Append(StoredGraphKind.Meta, initial.Address, null, new(initial.Address, new(1), [exhausted]));
        var maxBaseline = ManagementDirectoryReader.ReadBranches(store, full.Address);
        Assert.Throws<InvalidOperationException>(() => ManagementDirectoryWriter.PrepareBranchAddition(store, maxBaseline, id2.HeadId, "b"));
    }
    public void Dispose() {
        SharedReadModel.DeleteFixture(_path, Prefix);
        SharedReadModel.DeleteFixture(_path + "-heads", Prefix);
    }
}
