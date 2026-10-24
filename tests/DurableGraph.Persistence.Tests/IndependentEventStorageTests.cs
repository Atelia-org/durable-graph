using static Atelia.DurableGraph.Persistence.Tests.DB096TestAccess;
using Atelia.Data;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using Frames = Atelia.FrameStore.FrameStore;
using Node = Atelia.DurableGraph.Persistence.Tests.SharedReadModel.Node;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class IndependentEventStorageTests : IDisposable {
    private const string Prefix = "dg-independent-event-storage-";
    private readonly List<string> _paths = [];

    [Theory]
    [InlineData("source")]
    [InlineData("delta")]
    [InlineData("remove")]
    [InlineData("empty")]
    public void AppendRejectsNonIndependentEventBeforeWritingAnyFrames(string error) {
        using Frames frames = Frames.Create(NewPath());
        using GraphRecordStore store = new(frames);
        StoredGraphRevision state = store.AppendBusiness(StoredGraphKind.State, null, new(null, new(1), [B(1)]));
        PreparedGraphWrite write = error switch {
            "source" => new(state.Address, new(1), [B(1)]),
            "delta" => new(null, new(1), [ObjectVersionRecord.CreateDelta(1, state.ObjectHeads[1], [20])]),
            "remove" => new(null, new(1), [B(1)], [2]),
            "empty" => new(null, new(1), []),
            _ => throw new ArgumentOutOfRangeException(nameof(error)),
        };
        long before = store.AppendedFrames;
        Assert.Throws<InvalidDataException>(() => store.AppendBusiness(StoredGraphKind.Event, null, write));
        Assert.Equal(before, store.AppendedFrames);
        StoredGraphRevision valid = store.AppendBusiness(StoredGraphKind.Event, null, new(null, new(1), [B(1)]));
        Assert.Null(valid.SourceGraph);
        Assert.Equal(valid.OwnedBatch, valid.ObjectHeads[1]);
        Assert.Throws<InvalidDataException>(() => store.ReadBusinessGraph(valid.Address, state.Address));
    }

    [Theory]
    [InlineData("externalHead")]
    [InlineData("missingOwnedBatch")]
    [InlineData("delta")]
    [InlineData("batchSource")]
    [InlineData("directorySource")]
    [InlineData("directoryDelta")]
    [InlineData("ownedRecordOmitted")]
    [InlineData("headRecordMissing")]
    public void ColdAndWarmReadRejectForgedEventRelations(string error) {
        string path = NewPath();
        FrameAddress bad, good;
        using (Frames frames = Frames.Create(path)) {
            using GraphRecordStore writer = new(frames);
            StoredGraphRevision state = writer.AppendBusiness(StoredGraphKind.State, null, new(null, new(1), [B(1)]));
            ObjectVersionRecord[] records = error switch {
                "externalHead" => [B(2)],
                "delta" => [ObjectVersionRecord.CreateDelta(1, state.ObjectHeads[1], [20])],
                "ownedRecordOmitted" => [B(1), B(2)],
                _ => [B(1)],
            };
            FrameAddress owned = Batch(frames, error == "batchSource" ? state.Address : null, records);
            Dictionary<uint, FrameAddress> heads = new() { [1] = owned };
            if (error == "externalHead") { heads[1] = state.ObjectHeads[1]; heads.Add(2, owned); }
            if (error == "headRecordMissing") { heads.Add(2, owned); }
            FrameAddress directory;
            if (error == "directoryDelta") {
                directory = frames.Append(GraphFrameCodec.DirectoryTag,
                    GraphFrameCodec.EncodeDirectory(null, ObjectVersionKind.Delta, state.DirectoryRoot,
                        ObjectHeadDirectoryCodec.PrepareDelta(state.ObjectHeads, heads).Body)).Unwrap();
            } else {
                directory = Directory(frames, error == "directorySource" ? state.Address : null, heads);
            }
            bad = Graph(frames, directory, error == "missingOwnedBatch" ? null : owned);
            good = writer.AppendBusiness(StoredGraphKind.Event, null, new(null, new(1), [B(1)])).Address;
        }
        using Frames reopened = Frames.OpenReadOnly(path);
        using GraphRecordStore reader = new(reopened);
        for (int attempt = 0; attempt < 2; attempt++) {
            Assert.Throws<InvalidDataException>(() => reader.ReadBusinessGraph(bad, null));
            StoredGraphRevision accepted = reader.ReadBusinessGraph(good, null);
            Assert.Single(accepted.ObjectHeads);
            Assert.Equal(accepted.OwnedBatch, accepted.ObjectHeads[1]);
        }
        Assert.Equal(0, reader.AppendedFrames);
    }

    [Fact]
    public void MetadataQualificationDoesNotExpandIntoLiveMembershipQualification() {
        using Frames frames = Frames.Create(NewPath());
        FrameAddress external = Batch(frames, null, [B(1)]);
        FrameAddress owned = Batch(frames, null, [B(2)]);
        FrameAddress directory = Directory(frames, null, new Dictionary<uint, FrameAddress> { [1] = external, [2] = owned });
        FrameAddress graph = Graph(frames, directory, owned);
        using GraphRecordStore reader = new(frames);
        reader.ValidateBusinessMetadata(graph, null);
        Assert.Equal(0, reader.ReadCacheStatistics.DirectoryMaterializations);
        Assert.Equal(0, reader.BusinessGraphQualificationCount);
        Assert.Throws<InvalidDataException>(() => reader.ReadBusinessGraph(graph, null));
    }

    [Fact]
    public void AdmissionDerivesStateSourceFromHistoryAndRequiresEventSourceToBeEmpty() {
        using RepositoryResources resources = RepositoryResources.CreateStorageNew(NewPath());
        using PendingRepositorySave initial = resources.PrepareSaveFrom(null, null, "main", StoredGraphKind.State,
            default, () => (new PreparedGraphWrite(null, new(1), [B(1)]), null));
        RepositorySaveResult first = resources.CommitSave(initial);
        StoredGraphRevision unrelated = resources.GraphStore.AppendBusiness(StoredGraphKind.State, null,
            new(null, new(1), [B(1)]));
        long before = resources.GraphStore.AppendedFrames;
        Assert.Throws<InvalidDataException>(() => resources.PrepareSaveFrom(first.BranchId, first.CommitId, null,
            StoredGraphKind.State, default, () => (new PreparedGraphWrite(unrelated.Address, new(1), [B(1)]), null)));
        Assert.Throws<InvalidDataException>(() => resources.PrepareSaveFrom(first.BranchId, first.CommitId, null,
            StoredGraphKind.Event, default, () => (new PreparedGraphWrite(first.Business.Address, new(1), [B(1)]), null)));
        Assert.Equal(before, resources.GraphStore.AppendedFrames);
        Assert.Equal(first.CommitId, resources.ReadCurrent(first.BranchId).Id);
        using PendingRepositorySave independent = resources.PrepareSaveFrom(first.BranchId, first.CommitId, null,
            StoredGraphKind.Event, default, () => (new PreparedGraphWrite(null, new(1), [B(1)]), null));
        RepositorySaveResult saved = resources.CommitSave(independent);
        Assert.Null(saved.Business.SourceGraph);
        Assert.Equal(first.Business.Address, resources.FindNearestState(saved.CommitId, true)!.Address);
    }

    [Theory]
    [InlineData("stateEvent", "state", "event-one")]
    [InlineData("eventState", "event-one", "state")]
    [InlineData("eventEvent", "event-one", "event-two")]
    [InlineData("sameEvent", "event-one", "event-one")]
    public void IndependentReadsKeepLocalIdsStringsCyclesAndAliasesInsideEachGraph(string pairKind, string firstText, string secondText) {
        string path = NewPath();
        CommitId state, firstEvent, secondEvent;
        using (Repository writer = Repository.CreateNew(path, SharedReadModel.Models())) {
            BranchInfo branch = writer.CreateRootBranchFromState("main", Cyclic("state"));
            using WorkingCopy work = writer.OpenWorkingCopy(branch.Id);
            state = work.BaseCommitId;
            firstEvent = work.CommitEvent(Cyclic("event-one")).Id;
            secondEvent = work.CommitEvent(Cyclic("event-two")).Id;
        }
        using Repository reader = Repository.OpenReadOnlyExisting(path, SharedReadModel.Models());
        (CommitId first, CommitId second) = SelectPair(pairKind, state, firstEvent, secondEvent);
        StoredGraphRevision firstSnapshot = reader.GraphStore.ReadBusinessGraph(Describe(reader, first).Address, null);
        StoredGraphRevision secondSnapshot = reader.GraphStore.ReadBusinessGraph(Describe(reader, second).Address, null);
        Assert.Equal(firstSnapshot.RootId, secondSnapshot.RootId);
        Assert.Equal(firstSnapshot.ObjectHeads.Keys.Order(), secondSnapshot.ObjectHeads.Keys.Order());
        var pair = (First: first == state ? reader.LoadState(first) : reader.LoadEvent(first),
            Second: second == state ? reader.LoadState(second) : reader.LoadEvent(second));
        Node a = AssertCyclic(pair.First, firstText);
        Node b = AssertCyclic(pair.Second, secondText);
        Assert.NotSame(a, b);
        Assert.NotSame(a.Next, b.Next);
        Assert.NotSame(a.Links, b.Links);
        Assert.Equal(0, reader.GraphStore.AppendedFrames);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void PreviousRepositoryEpochIsRejectedByEveryOpenMode(bool readOnly, bool storageOnly) {
        string path = NewPath();
        using (RepositoryResources resources = RepositoryResources.CreateStorageNew(path)) {
            var branch = resources.Heads.ReadHead(1).Unwrap();
            Assert.True(branch.Payload.ToArray().AsSpan().StartsWith("P0R4"u8));
            resources.Heads.PublishHead(branch.Id.HeadId, "P0R3\0"u8.ToArray(), out _).Unwrap();
        }
        InvalidDataException failure = Assert.Throws<InvalidDataException>(() => {
            if (storageOnly) {
                using RepositoryResources unexpected = readOnly
                    ? RepositoryResources.OpenStorageReadOnlyExisting(path)
                    : RepositoryResources.OpenStorageExisting(path);
            } else {
                using Repository unexpected = readOnly
                    ? Repository.OpenReadOnlyExisting(path, SharedReadModel.Models())
                    : Repository.OpenExisting(path, SharedReadModel.Models());
            }
        });
        Assert.Equal("Unknown management directory pointer format.", failure.Message);
    }

    private static Node Cyclic(string text) {
        Node root = new() { Text = new string(text.AsSpan()), Value = 1 };
        Node child = new() { Text = root.Text, Next = root, Value = 2 };
        root.Next = root.Alias = child;
        root.Links = [child, child, root];
        return root;
    }

    private static Node AssertCyclic(IDurableObject value, string text) {
        Node root = Assert.IsType<Node>(value);
        Assert.Equal(text, root.Text);
        Assert.NotNull(root.Next);
        Assert.Same(root.Next, root.Alias);
        Assert.Same(root, root.Next.Next);
        Assert.Same(root.Text, root.Next.Text);
        Assert.NotNull(root.Links);
        Assert.Equal(3, root.Links.Count);
        Assert.Same(root.Next, root.Links[0]);
        Assert.Same(root.Next, root.Links[1]);
        Assert.Same(root, root.Links[2]);
        return root;
    }

    private static (CommitId First, CommitId Second) SelectPair(string kind, CommitId state, CommitId first, CommitId second) =>
        kind switch {
            "stateEvent" => (state, first),
            "eventState" => (first, state),
            "eventEvent" => (first, second),
            "sameEvent" => (first, first),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private static ObjectVersionRecord B(uint id) => ObjectVersionRecord.CreateBase(id, [10]);
    private static FrameAddress Batch(Frames frames, FrameAddress? source, ObjectVersionRecord[] records) =>
        frames.Append(GraphFrameCodec.BatchTag, GraphFrameCodec.EncodeBatch(source, null, records)).Unwrap();
    private static FrameAddress Directory(Frames frames, FrameAddress? source, IReadOnlyDictionary<uint, FrameAddress> heads) =>
        frames.Append(GraphFrameCodec.DirectoryTag,
            GraphFrameCodec.EncodeDirectory(source, ObjectVersionKind.Base, null, ObjectHeadDirectoryCodec.PrepareBase(heads).Body)).Unwrap();
    private static FrameAddress Graph(Frames frames, FrameAddress directory, FrameAddress? owned) =>
        frames.Append(GraphFrameCodec.GraphTag,
            GraphFrameCodec.EncodeGraph(StoredGraphKind.Event, null, new(1), directory, null, owned)).Unwrap();
    private string NewPath() {
        string path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
        _paths.Add(path);
        return path;
    }
    public void Dispose() {
        foreach (string path in _paths) { SharedReadModel.DeleteFixture(path, Prefix); }
    }
}
