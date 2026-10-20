using Atelia.DurableGraph.Runtime;
using Atelia.FrameStore;
using WidgetState = Atelia.DurableGraph.Generated.Family_5030576964676574.V1;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class SourceSnapshotSeamTests : IDisposable {
    private const string Prefix = "dg-source-snapshot-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ColdAndWarmBusinessDecodeQualifySelectedGraphOnceAndSaveAdmissionStillRechecks(bool selectEvent) {
        (FrameAddress state, FrameAddress domainEvent) = Seed();
        using RepositoryStorageFacade repository = RepositoryStorageFacade.OpenExisting(_path);
        GraphRecordStore store = repository.Store;
        FrameAddress selected = selectEvent ? domainEvent : state;
        StateModelRegistry.FrozenConfiguration configuration = Models().FreezeConfiguration();

        for (int attempt = 0; attempt < 2; attempt++) {
            long before = store.BusinessGraphQualificationCount;
            DecodedRevision decoded = DecodedRevision.ReadStoredGraph(store, store.ReadBusinessGraph(selected, null), configuration);
            // Count complete qualification, not physical decode/cache misses. Meta loading is excluded.
            Assert.Equal(before + 1, store.BusinessGraphQualificationCount);
            StoredGraphRevision snapshot = Assert.IsType<StoredGraphRevision>(decoded.SourceSnapshot);
            Assert.Equal(selected, snapshot.Address);
            Assert.Same(store, snapshot.SourceOwner);
            Assert.Equal(selectEvent ? StoredGraphKind.Event : StoredGraphKind.State, snapshot.Kind);
            Assert.Null(snapshot.SourceGraph);
            Assert.Equal(snapshot.SchemaRoot, decoded.SelectedSchemaView!.Snapshot!.Address);
            Assert.Equal(selectEvent ? 51 : 7, decoded.GetRequired(snapshot.RootId).GetState<WidgetState>().Segment0Field1);

            if (!selectEvent) {
                NormalizedGraphState normalized = NormalizedGraphState.Create(decoded, decoded.SourceModels!);
                Assert.Same(snapshot, normalized.SourceSnapshot);
                long beforeAdmission = store.BusinessGraphQualificationCount;
                StoredGraphRevision rechecked = normalized.RequireStoredSource(store, decoded.SourceModels!);
                Assert.Equal(beforeAdmission + 1, store.BusinessGraphQualificationCount);
                Assert.Equal(snapshot.Metadata, rechecked.Metadata);
                Assert.Same(snapshot, normalized.SourceSnapshot);
            }

            before = store.BusinessGraphQualificationCount;
            DB087Widget root = Assert.IsType<DB087Widget>(BusinessPersistenceAdapter.ReadRoot(store, store.ReadBusinessGraph(selected, null), Models()));
            Assert.Equal(before + 1, store.BusinessGraphQualificationCount);
            Assert.Equal(selectEvent ? 51 : 7, root.Value);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetainedSourceSnapshotDoesNotGrantSaveAuthorityAfterOwnerStopsButDtoRemainsReadable(bool fault) {
        (FrameAddress state, _) = Seed();
        using RepositoryResources resources = RepositoryResources.OpenStorageExisting(_path);
        using RepositoryStorageFacade repository = new(resources);
        BranchSaveSessionFixture session = new(repository, "main", Models());
        GraphRecordStore store = repository.Store;
        DecodedRevision decoded = DecodedRevision.ReadStoredGraph(store, store.ReadBusinessGraph(state, null), Models().FreezeConfiguration());
        StoredGraphRevision snapshot = Assert.IsType<StoredGraphRevision>(decoded.SourceSnapshot);
        NormalizedGraphState normalized = NormalizedGraphState.Create(decoded, decoded.SourceModels!);
        Assert.Same(snapshot, normalized.SourceSnapshot);
        normalized.RequireStoredSource(store, decoded.SourceModels!); // Positive control uses the actual reader-issued source.

        if (fault) { resources.MarkFaulted(); }
        else { resources.Dispose(); }

        Assert.ThrowsAny<InvalidOperationException>(() => normalized.RequireStoredSource(store, decoded.SourceModels!));
        Assert.ThrowsAny<InvalidOperationException>(() => DecodedRevision.ReadStoredGraph(store, snapshot, Models().FreezeConfiguration()));
        Assert.ThrowsAny<InvalidOperationException>(() => store.ReadObjectChains(snapshot).ToArray());
        Assert.ThrowsAny<InvalidOperationException>(() => session.Prepare(session.State!));
        WidgetState root = decoded.GetRequired(snapshot.RootId).GetState<WidgetState>();
        Assert.Equal(7, root.Segment0Field1);
        Assert.Equal(Enumerable.Range(0, 128), decoded.GetRequired(root.Segment0Field2).GetListState<int>().Elements.ToArray());
        Assert.Equal(state, snapshot.Address);
        Assert.Equal(decoded.Objects.Count, snapshot.ObjectHeads.Count);
    }

    private (FrameAddress State, FrameAddress Event) Seed() {
        using RepositoryStorageFacade repository = RepositoryStorageFacade.CreateNew(_path);
        BranchSaveSessionFixture session = new(repository, "main", Models());
        FrameAddress state;
        using (PreparedFixtureSave save = session.Prepare(new DB087Widget { Value = 7, Numbers = Enumerable.Range(0, 128).ToList() })) {
            state = save.Commit().Business.Address;
        }
        using PreparedFixtureSave domainEvent = session.Prepare(new DB087Widget { Value = 51 }, StoredGraphKind.Event);
        return (state, domainEvent.Commit().Business.Address);
    }

    private static StateModelRegistry Models() {
        StateModelRegistry registry = new();
        registry.Register(global::Atelia.DurableGraph.Generated.Family_5030576964676574.Definition);
        return registry;
    }

    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}
