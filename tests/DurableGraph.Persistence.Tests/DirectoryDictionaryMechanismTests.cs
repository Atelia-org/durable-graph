using System.Buffers.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using Atelia.HeadStore;
using DataStore = Atelia.FrameStore.FrameStore;
using PublicationStore = Atelia.HeadStore.HeadStore;

namespace Atelia.DurableGraph.Persistence.Tests;

// DB-096 mechanism witness, not a new Repository layout. D and S are independent
// control Heads in a fixture store. Both reference real Meta graphs; D uses an
// explicit builtin reader instead of the Schema employee whitelist.
public sealed class DirectoryDictionaryMechanismTests : IDisposable {
    private const string Prefix = "dg-directory-dictionary-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
    private static readonly DictionaryLayout Layout = new(new(1, TypeTag.UInt32), new(2, TypeTag.String), codecVersion: 1);
    private static readonly DurableSchema SeedSchema = new("DirectoryWitness.Seed", 1, new DurableFieldInfo(1, TypeTag.Byte));
    private static readonly DurableSchema AddedSchema = new("DirectoryWitness.Added", 1, new DurableFieldInfo(1, TypeTag.Int32));
    private static readonly DurableSchema UnpublishedSchema = new("DirectoryWitness.Unpublished", 1, new DurableFieldInfo(1, TypeTag.Int64));

    [Fact]
    public void Builtin_directory_cold_deltas_reuse_labels_and_publish_with_real_catalog_without_publishing_abandoned_candidates() {
        FrameAddress d0, d1, d2, s0, s1, abandonedDirectory, abandonedSchema;
        HeadId addedBranch, unpublishedBranch;
        HeadEntryId publishedDirectory, publishedSchema;
        Dictionary<uint, string> expected = [];
        Dictionary<uint, FrameAddress> originalLabels;
        using (Fixture store = Fixture.Create(_path)) {
            Dictionary<uint, ObjectId> entries = [];
            List<ObjectVersionRecord> rows = [];
            for (uint i = 2; i < 18; i++) {
                HeadId branch = store.Heads.CreateHead("business fixture"u8.ToArray(), out _).Unwrap().HeadId;
                uint encoded = Encode(branch);
                entries.Add(encoded, new(i));
                string label = i <= 3 ? "废灵根/遗迹捡到小绿瓶" : $"世界线/开局#{i}";
                expected.Add(encoded, label);
                rows.Add(Label(i, label));
            }
            PreparedBaseBody body = DictionaryStateBody<uint, ObjectId, UInt32StateOps, StringIdStateOps>
                .PrepareBase(Freeze(entries), Layout);
            rows.Add(ObjectVersionRecord.CreateBase(1, TypedBaseBodyCodec.Encode(ObjectLayout.ForDictionary(Layout), body)));
            StoredGraphRevision directory = store.Graphs.Append(StoredGraphKind.Meta, null, null, new(null, new(1), rows));
            d0 = directory.Address;
            originalLabels = directory.ObjectHeads.Where(pair => pair.Key != 1).ToDictionary();
            Assert.Equal(ObjectVersionKind.Base, Assert.Single(store.Graphs.ReadObjectChain(d0, 1).Entries).Record.Kind);

            SchemaCatalogWorkspace catalog = new(static () => { });
            catalog.Set(SeedSchema);
            PreparedSchemaCatalogWrite prepared = catalog.Prepare();
            StoredGraphRevision schema = store.Graphs.Append(StoredGraphKind.Meta, null, null, prepared.Write!);
            s0 = schema.Address;
            store.Heads.CreateHead(1, Pointer(d0), out _).Unwrap();
            store.Heads.CreateHead(2, Pointer(s0), out _).Unwrap();
            Assert.Throws<InvalidDataException>(() => StoredGraphDecoder.Read(store.Graphs, d0));
            AssertDirectory(store, d0, expected);
        }

        // Restore both graphs after every owner/cache has been closed. No application
        // model registration, Schema lookup, or old Repository facade participates.
        using (Fixture store = Fixture.Open(_path)) {
            Assert.Equal(d0, ReadPointer(store.Heads.ReadHead(1).Unwrap()));
            Assert.Equal(s0, ReadPointer(store.Heads.ReadHead(2).Unwrap()));
            DecodedGraphRows before = AssertDirectory(store, d0, expected);
            SchemaCatalogView accepted = SchemaCatalogView.Load(store.Graphs, s0);
            Assert.Equal(SeedSchema, accepted.Schemas[new(SeedSchema.Type, 1)]);
            addedBranch = store.Heads.CreateHead("business fixture"u8.ToArray(), out _).Unwrap().HeadId;
            expected.Add(Encode(addedBranch), "加入魔教");
            StoredGraphRevision directory = Add(store, before, Encode(addedBranch), new(18), expected[Encode(addedBranch)]);
            d1 = directory.Address;
            AssertExactDelta(store, d1, d0, before.Snapshot.ObjectHeads[1], count: 2);
            AssertLabelsReused(directory, originalLabels);

            SchemaCatalogWorkspace catalog = new(static () => { }, accepted);
            catalog.Set(AddedSchema);
            PreparedSchemaCatalogWrite prepared = catalog.Prepare();
            StoredGraphRevision schema = store.Graphs.Append(StoredGraphKind.Meta, s0, null, prepared.Write!);
            s1 = schema.Address;
            prepared.PrepareInstall(schema);
            HeadEntry oldD = store.Heads.ReadHead(1).Unwrap(), oldS = store.Heads.ReadHead(2).Unwrap();
            IReadOnlyDictionary<HeadId, HeadEntryId> published = store.Heads.PublishHeads(new Dictionary<HeadId, ReadOnlyMemory<byte>> {
                [oldD.Id.HeadId] = Pointer(d1), [oldS.Id.HeadId] = Pointer(s1)
            }, out PublicationOutcome outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.Equal(2, published.Count);
            publishedDirectory = published[oldD.Id.HeadId];
            publishedSchema = published[oldS.Id.HeadId];
            Assert.NotEqual(oldD.Id, publishedDirectory);
            Assert.NotEqual(oldS.Id, publishedSchema);
            prepared.Install(schema);
            Assert.Same(prepared.View, catalog.Accepted);
            Assert.Equal(d1, ReadPointer(store.Heads.ReadHead(1).Unwrap()));
            Assert.Equal(s1, ReadPointer(store.Heads.ReadHead(2).Unwrap()));
        }

        using (Fixture store = Fixture.Open(_path)) {
            Assert.Equal(publishedDirectory, store.Heads.ReadHead(1).Unwrap().Id);
            Assert.Equal(publishedSchema, store.Heads.ReadHead(2).Unwrap().Id);
            DecodedGraphRows before = AssertDirectory(store, ReadPointer(store.Heads.ReadHead(1).Unwrap()), expected);
            SchemaCatalogView catalog = SchemaCatalogView.Load(store.Graphs, ReadPointer(store.Heads.ReadHead(2).Unwrap()));
            Assert.Equal(AddedSchema, catalog.Schemas[new(AddedSchema.Type, 1)]);
            AssertExactDelta(store, d1, d0, store.Graphs.ReadGraph(d0).ObjectHeads[1], count: 2);
            HeadId branch = store.Heads.CreateHead("business fixture"u8.ToArray(), out _).Unwrap().HeadId;
            expected.Add(Encode(branch), "背叛魔教");
            StoredGraphRevision directory = Add(store, before, Encode(branch), new(19), expected[Encode(branch)]);
            d2 = directory.Address;
            AssertExactDelta(store, d2, d1, before.Snapshot.ObjectHeads[1], count: 3);
            AssertLabelsReused(directory, originalLabels);
            store.Heads.PublishHeads(new Dictionary<HeadId, ReadOnlyMemory<byte>> {
                [publishedDirectory.HeadId] = Pointer(d2)
            }, out PublicationOutcome outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.Equal(publishedSchema, store.Heads.ReadHead(2).Unwrap().Id);

            // These complete frames survive closure, but no control Head points to
            // either candidate. This witnesses publication visibility, not a crash.
            unpublishedBranch = store.Heads.CreateHead("business fixture"u8.ToArray(), out _).Unwrap().HeadId;
            DecodedGraphRows latest = AssertDirectory(store, d2, expected);
            abandonedDirectory = Add(store, latest, Encode(unpublishedBranch), new(20), "未登记世界线").Address;
            SchemaCatalogWorkspace pendingCatalog = new(static () => { }, catalog);
            pendingCatalog.Set(UnpublishedSchema);
            PreparedSchemaCatalogWrite pending = pendingCatalog.Prepare();
            abandonedSchema = store.Graphs.Append(StoredGraphKind.Meta, s1, null, pending.Write!).Address;
            store.Data.ConfirmDurable();
            Assert.Equal(d2, ReadPointer(store.Heads.ReadHead(1).Unwrap()));
            Assert.Equal(s1, ReadPointer(store.Heads.ReadHead(2).Unwrap()));
        }

        using (Fixture store = Fixture.Open(_path)) {
            Assert.Equal(d2, ReadPointer(store.Heads.ReadHead(1).Unwrap()));
            Assert.Equal(s1, ReadPointer(store.Heads.ReadHead(2).Unwrap()));
            DecodedGraphRows directory = AssertDirectory(store, d2, expected);
            AssertLabelsReused(directory.Snapshot, originalLabels);
            Assert.DoesNotContain(Encode(unpublishedBranch), directory.Rows[new(1)].GetDictionaryState<uint, ObjectId>().Entries.ToArray().Select(pair => pair.Key));
            Assert.Equal(unpublishedBranch, store.Heads.ReadHead(unpublishedBranch).Unwrap().Id.HeadId);
            SchemaCatalogView catalog = SchemaCatalogView.Load(store.Graphs, s1);
            Assert.DoesNotContain(new SchemaKey(UnpublishedSchema.Type, 1), catalog.Schemas.Keys);
            DecodedGraphRows abandoned = ReadDirectory(store, abandonedDirectory);
            Assert.Equal(expected.Count + 1, abandoned.Rows[new(1)].GetDictionaryState<uint, ObjectId>().Count);
            Assert.Contains(new SchemaKey(UnpublishedSchema.Type, 1), SchemaCatalogView.Load(store.Graphs, abandonedSchema).Schemas.Keys);
        }
    }

    [Fact]
    public void Builtin_bookmark_dictionary_cold_reads_exact_entry_handle_from_byte_array_without_application_schema() {
        string dataPath = Path.Combine(_path, "data"), headsPath = Path.Combine(_path, "heads");
        Directory.CreateDirectory(_path);
        ArrayLayout bytesLayout = new(TypeExprKind.VectorArray, new(1, TypeTag.Byte), codecVersion: 1);
        DictionaryLayout bookmarks = new(new(1, TypeTag.String), DurableFieldInfo.Reference(2, bytesLayout.Type), codecVersion: 1);
        HeadEntryId target;
        FrameAddress t0;
        using (DataStore data = DataStore.Create(dataPath))
        using (PublicationStore heads = PublicationStore.Create(headsPath, data, reservedHeadIdUpperBound: 1))
        using (GraphRecordStore graphs = new(data)) {
            target = heads.CreateHead("bookmark target"u8.ToArray(), out _).Unwrap();
            byte[] encoded = new byte[HeadEntryId.EncodedSize];
            Assert.True(target.TryWrite(encoded));
            FrozenDictionaryState<ObjectId, ObjectId> map = new(DictionaryComparerKind.StringOrdinal, [new(new(2), new(3))]);
            PreparedBaseBody mapBody = DictionaryStateBody<ObjectId, ObjectId, StringIdStateOps, ObjectIdStateOps>.PrepareBase(map, bookmarks);
            PreparedBaseBody bytesBody = ArrayStateBody<byte, ByteStateOps>.PrepareBase(new(new(HeadEntryId.EncodedSize), encoded), bytesLayout);
            t0 = graphs.Append(StoredGraphKind.Meta, null, null, new(null, new(1), [
                ObjectVersionRecord.CreateBase(1, TypedBaseBodyCodec.Encode(ObjectLayout.ForDictionary(bookmarks), mapBody)),
                Label(2, "拾取英雄之剑之前"),
                ObjectVersionRecord.CreateBase(3, TypedBaseBodyCodec.Encode(ObjectLayout.ForArray(bytesLayout), bytesBody))
            ])).Address;
            heads.CreateHead(1, Pointer(t0), out _).Unwrap();
        }
        using (DataStore data = DataStore.Open(dataPath))
        using (PublicationStore heads = PublicationStore.Open(headsPath, data)) {
            Assert.Equal(t0, ReadPointer(heads.ReadHead(1).Unwrap()));
            using GraphRecordStore graphs = new(data);
            Assert.Throws<InvalidDataException>(() => StoredGraphDecoder.Read(graphs, t0));
            DecodedGraphRows decoded = StoredGraphDecoder.Read(graphs, t0, InternalModels());
            Assert.Null(decoded.Snapshot.SchemaRoot);
            DictionaryEntryState<ObjectId, ObjectId> entry = Assert.Single(decoded.Rows[new(1)].GetDictionaryState<ObjectId, ObjectId>().Entries.ToArray());
            Assert.Equal("拾取英雄之剑之前", decoded.Rows[entry.Key].StringContent);
            FrozenArrayState<byte> bytes = decoded.Rows[entry.Value].GetArrayState<byte>();
            Assert.Equal(new ArrayShape(HeadEntryId.EncodedSize), bytes.Shape);
            Assert.True(HeadEntryId.TryRead(bytes.Elements, out HeadEntryId restored));
            Assert.Equal(target, restored);
            Assert.Equal("bookmark target"u8.ToArray(), heads.ReadEntry(restored).Unwrap().Payload.ToArray());
        }
    }

    private static StoredGraphRevision Add(Fixture store, DecodedGraphRows before, uint key, ObjectId labelId, string label) {
        FrozenDictionaryState<uint, ObjectId> prior = before.Rows[new(1)].GetDictionaryState<uint, ObjectId>();
        Dictionary<uint, ObjectId> entries = prior.Entries.ToArray().ToDictionary(static pair => pair.Key, static pair => pair.Value);
        entries.Add(key, labelId);
        PreparedDeltaBody delta = DictionaryStateBody<uint, ObjectId, UInt32StateOps, StringIdStateOps>.PrepareDelta(prior, Freeze(entries), Layout);
        Assert.True(delta.HasChanges);
        PreparedBaseBody full = DictionaryStateBody<uint, ObjectId, UInt32StateOps, StringIdStateOps>.PrepareBase(Freeze(entries), Layout);
        List<PreparedObject> objects = [
            PreparedObject.Compared(new(1), before.Snapshot.ObjectHeads[1],
                new(TypedBaseBodyCodec.Encode(ObjectLayout.ForDictionary(Layout), full)), delta),
            PreparedObject.New(labelId, new(Label(labelId.Value, label).Body))
        ];
        foreach (ObjectStateRecord row in before.Rows.Values.Where(static row => row.Kind == ObjectStateKind.String)) {
            objects.Add(PreparedObject.Unchanged(row.Id, before.Snapshot.ObjectHeads[row.Id.Value],
                new(TypedBaseBodyCodec.Encode(ObjectLayout.String, StringBodyPreparer.PrepareBase(row.StringContent)))));
        }
        var planned = ObjectWritePlanner.PlanObjectWrites(objects,
            before.Storage.ToDictionary(static pair => pair.Key, static pair => pair.Value.ReconstructionPayloadBytes), new(5, 5));
        Assert.Equal(before.Rows.Count + 1, planned.Estimates.Length); // Complete post-live set, including unchanged labels.
        Assert.Equal(2, planned.Records.Length);
        Assert.Equal(ObjectVersionKind.Delta, Assert.Single(planned.Records, static row => row.ObjectId == 1).Kind);
        Assert.Equal(ObjectVersionKind.Base, Assert.Single(planned.Records, row => row.ObjectId == labelId.Value).Kind);
        PreparedGraphWrite write = new(before.Snapshot.Address, new(1), planned.Records);
        return store.Graphs.Append(StoredGraphKind.Meta, before.Snapshot.Address, null, write);
    }

    private static void AssertExactDelta(Fixture store, FrameAddress address, FrameAddress source, FrameAddress prior, int count) {
        StoredGraphRevision graph = store.Graphs.ReadGraph(address);
        Assert.Equal(StoredGraphKind.Meta, graph.Kind);
        Assert.Null(graph.SchemaRoot);
        Assert.Equal(source, graph.Parent);
        Assert.Equal(source, graph.SourceGraph);
        ObjectVersionBatch batch = store.Graphs.ReadBatch(graph.OwnedBatch!.Value);
        Assert.Equal(source, batch.SourceGraph);
        Assert.Null(batch.SchemaRoot);
        ObjectVersionRecord row = Assert.Single(batch.Records, static row => row.ObjectId == 1);
        Assert.Equal(ObjectVersionKind.Delta, row.Kind);
        Assert.Equal(prior, row.PriorAddress);
        ObjectVersionChain chain = store.Graphs.ReadObjectChain(address, 1);
        Assert.Equal(count, chain.Entries.Count);
        Assert.Equal(source, chain.Entries[^1].SourceGraph);
        Assert.Equal(prior, chain.Entries[^1].Record.PriorAddress);
    }

    private static DecodedGraphRows AssertDirectory(Fixture store, FrameAddress address, Dictionary<uint, string> expected) {
        DecodedGraphRows decoded = ReadDirectory(store, address);
        Assert.Equal(StoredGraphKind.Meta, decoded.Snapshot.Kind);
        Assert.Null(decoded.Snapshot.SchemaRoot);
        FrozenDictionaryState<uint, ObjectId> entries = decoded.Rows[new(1)].GetDictionaryState<uint, ObjectId>();
        Assert.Equal(expected.Count, entries.Count);
        Span<byte> bytes = stackalloc byte[HeadId.EncodedSize];
        foreach ((uint key, ObjectId id) in entries.Entries) {
            Assert.Equal(expected[key], decoded.Rows[id].StringContent);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, key);
            Assert.True(HeadId.TryRead(bytes, out HeadId branch));
            Assert.Equal(branch, store.Heads.ReadHead(branch).Unwrap().Id.HeadId);
        }
        return decoded;
    }

    private static DecodedGraphRows ReadDirectory(Fixture store, FrameAddress address) => StoredGraphDecoder.Read(store.Graphs, address, InternalModels());
    private static ModelExecutionEnvironment InternalModels() => new StateModelRegistry().CreateEnvironment(
        schemaLookup: _ => throw new InvalidOperationException("Internal builtin directory must not query application Schema."));
    private static void AssertLabelsReused(StoredGraphRevision graph, Dictionary<uint, FrameAddress> original) {
        foreach ((uint id, FrameAddress prior) in original) { Assert.Equal(prior, graph.ObjectHeads[id]); }
    }
    private static FrozenDictionaryState<uint, ObjectId> Freeze(Dictionary<uint, ObjectId> values) =>
        new(DictionaryComparerKind.ScalarDefault, values.OrderBy(static pair => pair.Key)
            .Select(static pair => new DictionaryEntryState<uint, ObjectId>(pair.Key, pair.Value)).ToArray());
    private static ObjectVersionRecord Label(uint id, string text) => ObjectVersionRecord.CreateBase(id,
        TypedBaseBodyCodec.Encode(ObjectLayout.String, StringBodyPreparer.PrepareBase(text)));
    private static uint Encode(HeadId id) {
        Span<byte> encoded = stackalloc byte[HeadId.EncodedSize];
        Assert.True(id.TryWrite(encoded));
        return BinaryPrimitives.ReadUInt32LittleEndian(encoded);
    }
    private static byte[] Pointer(FrameAddress address) {
        byte[] bytes = new byte[FrameAddress.EncodedSize];
        Assert.True(address.TryWrite(bytes));
        return bytes;
    }
    private static FrameAddress ReadPointer(HeadEntry entry) {
        Assert.Equal(FrameAddress.EncodedSize, entry.Payload.Length);
        Assert.True(FrameAddress.TryRead(entry.Payload, out FrameAddress address));
        return address;
    }

    private sealed class Fixture : IDisposable {
        internal DataStore Data { get; }
        internal PublicationStore Heads { get; }
        internal GraphRecordStore Graphs { get; }
        private Fixture(string path, bool create) {
            Directory.CreateDirectory(path);
            Data = create ? DataStore.Create(Path.Combine(path, "data")) : DataStore.Open(Path.Combine(path, "data"));
            Heads = create ? PublicationStore.Create(Path.Combine(path, "heads"), Data, reservedHeadIdUpperBound: 2)
                : PublicationStore.Open(Path.Combine(path, "heads"), Data);
            if (!create) {
                _ = ReadPointer(Heads.ReadHead(1).Unwrap());
                _ = ReadPointer(Heads.ReadHead(2).Unwrap());
            }
            Graphs = new(Data);
        }
        internal static Fixture Create(string path) => new(path, true);
        internal static Fixture Open(string path) => new(path, false);
        public void Dispose() { Graphs.Dispose(); Heads.Dispose(); Data.Dispose(); }
    }
    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}
