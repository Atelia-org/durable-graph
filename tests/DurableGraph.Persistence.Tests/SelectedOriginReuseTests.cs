using System.Buffers;
using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using Xunit.Abstractions;
using Frames = Atelia.FrameStore.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class SelectedOriginReuseTests(ITestOutputHelper output) : IDisposable {
    private const string Prefix = "dg-selected-origin-";
    private readonly List<string> _paths = [];
    private static readonly DurableSchema World = new("W", 1, new DurableFieldInfo(1, TypeTag.Int32));
    private static readonly DurableSchema Other = new("A", 1, new DurableFieldInfo(1, TypeTag.Int32));
    private static readonly ObjectLayout WorldLayout = ObjectLayout.ForDurable(World);

    [Theory]
    [InlineData("same-S")]
    [InlineData("retained-only")]
    [InlineData("mixed")]
    public void ExactSelectedOriginSavesOneCatalogLoadOnlyWhenTheBusinessChainsUseIt(string mode) {
        using Frames frames = Frames.Create(NewPath());
        using GraphRecordStore store = new(frames, readCacheBudgetBytes: 0);
        SchemaCatalogView first = Catalog(store, [World]);
        StoredGraphRevision initial = store.AppendBusiness(StoredGraphKind.State, null, first.Snapshot!.Address,
            new(null, new(1), [Base(1, 17, first)]));
        SchemaCatalogView selected = first;
        StoredGraphRevision snapshot = initial;
        if (mode != "same-S") {
            // A separate valid catalog has a different descriptor assignment. It still
            // contains the complete W layout, so compatibility alone cannot identify origin.
            selected = Catalog(store, [Other, World]);
            Assert.NotEqual(first.DescriptorId(WorldLayout), selected.DescriptorId(WorldLayout));
            ObjectVersionRecord[] records = mode == "mixed" ? [Base(2, 29, selected)] : [];
            snapshot = store.AppendBusiness(StoredGraphKind.State, initial.Address, selected.Snapshot!.Address,
                new(initial.Address, new(1), records));
        }
        ModelExecutionEnvironment models = Models();
        SchemaCatalogView synthetic = new(selected.RootId, selected.Rows); // Same semantics, no persisted origin authority.
        var (_, loadCost) = Measure(store, () => SchemaCatalogView.Load(store, selected.Snapshot!.Address));
        Assert.True(loadCost.BatchDecodes > 0);
        var (actual, actualCost) = Measure(store, () => StoredGraphDecoder.Read(store, snapshot, models, selected));
        var (fallback, fallbackCost) = Measure(store, () => StoredGraphDecoder.Read(store, snapshot, models, synthetic));
        AssertRowsEqual(fallback, actual);
        Assert.Equal(17, actual.Rows[new(1)].GetState<int>());
        Assert.Equal(WorldLayout, actual.Rows[new(1)].Layout);
        if (mode == "mixed") Assert.Equal(29, actual.Rows[new(2)].GetState<int>());
        PhysicalCost expectedSaving = mode == "retained-only" ? default : loadCost;
        output.WriteLine($"{mode}: actual={actualCost}, fallback={fallbackCost}, standalone selected Load={loadCost}");
        Assert.Equal(expectedSaving, fallbackCost - actualCost);
        Assert.Equal(snapshot.ObjectHeads.Count, actual.Rows.Count);
    }

    [Fact]
    public void ForeignSelectedWithTheSameAddressAndCompleteLayoutsStillLoadsTheLocalOrigin() {
        using Frames frames = Frames.Create(NewPath());
        using Frames foreignFrames = Frames.Create(NewPath());
        using GraphRecordStore store = new(frames, readCacheBudgetBytes: 0);
        using GraphRecordStore foreignStore = new(foreignFrames, readCacheBudgetBytes: 0);
        SchemaCatalogView local = Catalog(store, [Other, World]);
        // Exchange equal-length persisted declaration names in an independent Store.
        // Both catalogs are genuine cold loads, contain A/W, and have the same address,
        // but equal descriptor numbers identify different complete layouts.
        SchemaCatalogView foreign = Catalog(foreignStore, [Other, World], exchangeNames: true);
        Assert.Equal(local.Snapshot!.Address, foreign.Snapshot!.Address);
        Assert.NotSame(store, foreign.Snapshot.SourceOwner);
        Assert.NotEqual(local.DescriptorId(WorldLayout), foreign.DescriptorId(WorldLayout));
        StoredGraphRevision snapshot = store.AppendBusiness(StoredGraphKind.State, null, local.Snapshot.Address,
            new(null, new(1), [Base(1, 41, local)]));
        ModelExecutionEnvironment models = Models();
        SchemaCatalogView synthetic = new(foreign.RootId, foreign.Rows);
        var (actual, actualCost) = Measure(store, () => StoredGraphDecoder.Read(store, snapshot, models, foreign));
        var (fallback, fallbackCost) = Measure(store, () => StoredGraphDecoder.Read(store, snapshot, models, synthetic));
        AssertRowsEqual(fallback, actual);
        Assert.Equal(WorldLayout, actual.Rows[new(1)].Layout);
        Assert.Equal(41, actual.Rows[new(1)].GetState<int>());
        Assert.Equal(fallbackCost, actualCost);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnknownDynamicDescriptorRemainsInvalidWithOrWithoutReusableSelected(bool syntheticSelected) {
        using Frames frames = Frames.Create(NewPath());
        using GraphRecordStore store = new(frames, readCacheBudgetBytes: 0);
        SchemaCatalogView selected = Catalog(store, [World]);
        ArrayBufferWriter<byte> buffer = new();
        BareValueWriter writer = new(buffer);
        writer.WriteByte(TypedBaseBodyCodec.FormatVersion);
        writer.WriteByte(1);
        writer.WriteVarUInt32(selected.Rows.Keys.Max(id => id.Value) + 1);
        writer.WriteVarInt32(17);
        StoredGraphRevision snapshot = store.AppendBusiness(StoredGraphKind.State, null, selected.Snapshot!.Address,
            new(null, new(1), [ObjectVersionRecord.CreateBase(1, buffer.WrittenSpan)]));
        if (syntheticSelected) selected = new(selected.RootId, selected.Rows);
        Assert.Throws<InvalidDataException>(() => StoredGraphDecoder.Read(store, snapshot, Models(), selected));
    }

    [Fact]
    public void RetainedOriginDoesNotMakeAMissingSelectedLayoutAcceptable() {
        using Frames frames = Frames.Create(NewPath());
        using GraphRecordStore store = new(frames, readCacheBudgetBytes: 0);
        SchemaCatalogView origin = Catalog(store, [World]);
        StoredGraphRevision initial = store.AppendBusiness(StoredGraphKind.State, null, origin.Snapshot!.Address,
            new(null, new(1), [Base(1, 17, origin)]));
        SchemaCatalogView selected = Catalog(store, [Other]);
        StoredGraphRevision retained = store.AppendBusiness(StoredGraphKind.State, initial.Address, selected.Snapshot!.Address,
            new(initial.Address, new(1), []));
        // A has the same descriptor number and body width as W. Substituting selected
        // for the true origin would incorrectly decode A and evade this rejection.
        Assert.Equal(origin.DescriptorId(WorldLayout), selected.DescriptorId(ObjectLayout.ForDurable(Other)));
        Assert.Throws<InvalidDataException>(() => StoredGraphDecoder.Read(store, retained, Models(), selected));
    }

    [Fact]
    public void PreviouslyLoadedSelectedDoesNotPermitReadingAfterItsStoreStops() {
        using Frames frames = Frames.Create(NewPath());
        using GraphRecordStore store = new(frames, readCacheBudgetBytes: 0);
        SchemaCatalogView selected = Catalog(store, [World]);
        StoredGraphRevision snapshot = store.AppendBusiness(StoredGraphKind.State, null, selected.Snapshot!.Address,
            new(null, new(1), [Base(1, 17, selected)]));
        store.Dispose();
        Assert.ThrowsAny<InvalidOperationException>(() => StoredGraphDecoder.Read(store, snapshot, Models(), selected));
    }

    private static SchemaCatalogView Catalog(GraphRecordStore store, DurableSchema[] schemas, bool exchangeNames = false) {
        SchemaCatalogWorkspace catalog = new(static () => { });
        foreach (DurableSchema schema in schemas) catalog.Set(schema);
        PreparedSchemaCatalogWrite prepared = catalog.Prepare();
        ObjectVersionRecord[] records = prepared.Write!.Records.ToArray();
        if (exchangeNames) {
            records = records.Select(record => {
                ObjectStateRecord row = prepared.View.Rows[new(record.ObjectId)];
                if (row.Kind != ObjectStateKind.String || row.StringContent is not ("A" or "W")) return record;
                string name = row.StringContent == "A" ? "W" : "A";
                return ObjectVersionRecord.CreateBase(record.ObjectId,
                    TypedBaseBodyCodec.Encode(ObjectLayout.String, StringBodyPreparer.PrepareBase(name)));
            }).ToArray();
        }
        StoredGraphRevision graph = store.AppendMeta(null, new(null, prepared.View.RootId, records));
        return SchemaCatalogView.Load(store, graph.Address);
    }

    private static ObjectVersionRecord Base(uint id, int value, SchemaCatalogView catalog) {
        ArrayBufferWriter<byte> buffer = new();
        BareValueWriter writer = new(buffer);
        writer.WriteVarInt32(value);
        return ObjectVersionRecord.CreateBase(id, TypedBaseBodyCodec.Encode(WorldLayout, new PreparedBaseBody(buffer.WrittenSpan), catalog));
    }

    private static ModelExecutionEnvironment Models() {
        StateReaderRegistry readers = new();
        foreach (DurableSchema schema in new[] { World, Other }) {
            readers.Register(new StateReaderBinding<int>(schema,
                static (ref BareValueReader reader) => reader.ReadVarInt32(),
                static (ref BareValueReader reader, in int prior) => Int32StateOps.ApplyDelta(ref reader, in prior, new(1, TypeTag.Int32)),
                static (in int value, IStateReferenceVisitor visitor) => { }));
        }
        return readers.CreateEnvironment(schemaLookup: null);
    }

    private static void AssertRowsEqual(DecodedGraphRows expected, DecodedGraphRows actual) {
        Assert.Equal(expected.Snapshot.Address, actual.Snapshot.Address);
        Assert.Equal(expected.Rows.Keys.Order(), actual.Rows.Keys.Order());
        foreach ((ObjectId id, ObjectStateRecord row) in expected.Rows) {
            Assert.Equal(row.Layout, actual.Rows[id].Layout);
            Assert.Equal(row.GetState<int>(), actual.Rows[id].GetState<int>());
            Assert.Equal(expected.Storage[id], actual.Storage[id]);
        }
    }

    private static (T Result, PhysicalCost Cost) Measure<T>(GraphRecordStore store, Func<T> read) {
        GraphPhysicalReadStatistics before = store.ReadCacheStatistics;
        T result = read();
        return (result, PhysicalCost.From(store.ReadCacheStatistics) - PhysicalCost.From(before));
    }

    private readonly record struct PhysicalCost(long GraphDecodes, long BatchDecodes, long DirectoryVersionDecodes,
        long DirectoryMaterializations, long DirectoryReplayFrames) {
        internal static PhysicalCost From(GraphPhysicalReadStatistics value) => new(value.GraphDecodes, value.BatchDecodes,
            value.DirectoryVersionDecodes, value.DirectoryMaterializations, value.DirectoryReplayFrames);
        public static PhysicalCost operator -(PhysicalCost left, PhysicalCost right) => new(left.GraphDecodes - right.GraphDecodes,
            left.BatchDecodes - right.BatchDecodes, left.DirectoryVersionDecodes - right.DirectoryVersionDecodes,
            left.DirectoryMaterializations - right.DirectoryMaterializations, left.DirectoryReplayFrames - right.DirectoryReplayFrames);
    }

    private string NewPath() {
        string path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
        _paths.Add(path);
        return path;
    }

    public void Dispose() {
        foreach (string path in _paths) SharedReadModel.DeleteFixture(path, Prefix);
    }
}
