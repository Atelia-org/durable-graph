using Atelia.Binary;

using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using DescriptorDto = Atelia.DurableGraph.Generated.Family_503044.V1;
using FieldDto = Atelia.DurableGraph.Generated.Family_503046.V1;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class MetaBootstrapDictionaryTests : IDisposable {
    private const string Prefix = "dg-bootstrap-dictionary-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
    private static readonly DurableSchema OldRoot = new("Dictionary.LookupRoot", 1, new DurableFieldInfo(1, TypeTag.Byte));
    private static readonly DurableSchema CurrentRoot = new("Dictionary.LookupRoot", 2, new DurableFieldInfo(1, TypeTag.Byte));

    [Theory]
    [InlineData(DictionaryComparerKind.StringOrdinal, "same", "same")]
    [InlineData(DictionaryComparerKind.StringOrdinalIgnoreCase, "SAME", "same")]
    [InlineData(DictionaryComparerKind.ReferenceIdentity, "", "")]
    public void Stored_lookup_collisions_in_an_unreachable_Dictionary_fail_before_a_nontrivial_Root_normalization(
        DictionaryComparerKind comparer, string first, string second) {
        DictionaryLayout layout = new(new(1, TypeTag.String), new(2, TypeTag.Int32), codecVersion: 1);
        FrozenDictionaryState<ObjectId, int> state = new(comparer, [new(new(100), 10), new(new(101), 20)]);
        PreparedBaseBody raw = DictionaryStateBody<ObjectId, int, StringIdStateOps, Int32StateOps>.PrepareBase(state, layout);
        var standalone = (DictionaryStateReader)BootstrapBindings.Reader(ObjectLayout.ForDictionary(layout), NoSchemas());
        ObjectStateRecord dictionary = standalone.Read(new(10), new SingleBody(raw.Body.ToArray()));
        Dictionary<ObjectId, ObjectStateRecord> exact = new() {
            [new(10)] = dictionary, [new(100)] = new(new(100), first), [new(101)] = new(new(101), second),
        };
        standalone.VisitReferences(dictionary, new StateReferenceValidator(exact)); // Bytes and typed references are legal.
        Assert.Equal(2, dictionary.GetDictionaryState<ObjectId, int>().Count);
        Assert.Equal(TypeTag.Byte, Assert.Single(OldRoot.Fields).TypeTag); // No Root reference reaches the map.
        SchemaCatalogWorkspace catalog = new(static () => { });
        catalog.Set(OldRoot);
        PreparedSchemaCatalogWrite prepared = catalog.Prepare();
        FrameAddress good, bad;
        using (var repository = RepositoryStorageFacade.CreateNew(_path)) {
            good = Publish(repository, "good", LookupRows(prepared.View, layout, raw, "first", "second"), prepared).Address;
            bad = Publish(repository, "bad", LookupRows(prepared.View, layout, raw, first, second)).Address;
        }
        using var reader = RepositoryStorageFacade.OpenReadOnlyExisting(_path);
        Assert.Equal(good, MechanismFixture.State(reader, "good")?.Address);
        Assert.Equal(bad, MechanismFixture.State(reader, "bad")?.Address);
        Assert.Equal(4, MechanismFixture.Snapshot(reader, bad).ObjectHeads.Count);
        int normalized = 0;
        StateModelRegistry models = RootModels(() => normalized++);
        LookupRoot positive = Assert.IsType<LookupRoot>(BusinessPersistenceAdapter.ReadRoot(reader.Store, MechanismFixture.Snapshot(reader, good), models));
        Assert.Equal((byte)7, positive.Value);
        Assert.Equal(1, normalized); // V1 -> V2 actually invokes the delegate; this counter is not vacuous.
        normalized = 0;
        Assert.Throws<InvalidDataException>(() => BusinessPersistenceAdapter.ReadRoot(reader.Store, MechanismFixture.Snapshot(reader, bad), models));
        Assert.Equal(0, normalized);
        SchemaCatalogView selected = SchemaCatalogView.Load(reader.Store, reader.Store.ReadMetadata(bad).SchemaRoot!.Value);
        Assert.Throws<InvalidDataException>(() => StoredGraphDecoder.Read(reader.Store, MechanismFixture.Snapshot(reader, bad),
            models.CreateEnvironment(schemaLookup: key => selected.Schemas.GetValueOrDefault(key)), selected));
        Assert.Equal(0, reader.Store.AppendedFrames);
    }

    [Fact]
    public void Pure_builtin_Dictionary_Base_and_canonical_key_Delta_cold_read_through_real_refs_without_a_Schema_root() {
        DictionaryLayout layout = new(new(1, TypeTag.Int32), DurableFieldInfo.Nullable(2, new(1, TypeTag.Int64)), codecVersion: 1);
        FrozenDictionaryState<int, NullableState<long>> before = new(DictionaryComparerKind.ScalarDefault,
            [new(1, default), new(2, new(long.MinValue))]);
        FrozenDictionaryState<int, NullableState<long>> after = new(DictionaryComparerKind.ScalarDefault,
            [new(2, new(long.MaxValue)), new(3, new(5))]);
        PreparedBaseBody raw = DictionaryStateBody<int, NullableState<long>, Int32StateOps, NullableStateOps<long, Int64StateOps>>.PrepareBase(before, layout);
        PreparedDeltaBody delta = DictionaryStateBody<int, NullableState<long>, Int32StateOps, NullableStateOps<long, Int64StateOps>>.PrepareDelta(before, after, layout);
        Assert.True(delta.HasChanges);
        byte[] bytes = TypedBaseBodyCodec.Encode(ObjectLayout.ForDictionary(layout), raw);
        Assert.Equal((byte)5, bytes[0]);
        Assert.Equal((byte)2, bytes[1]);
        var decoded = TypedBaseBodyCodec.Decode(bytes, null);
        Assert.Equal(ObjectLayout.ForDictionary(layout), decoded.Layout);
        Assert.Equal(1u, decoded.Layout.Dictionary!.CodecVersion);
        ObjectStateRecord row = BootstrapBindings.Reader(decoded.Layout, NoSchemas()).Read(new(1), new SingleBody(decoded.Body));
        ObjectStateRecord prepared = BootstrapBindings.WithPreparation(row, NoSchemas());
        Assert.Equal(raw.Body, prepared.Preparation!.PrepareBase(prepared).Body);
        FrameAddress s0, s1;
        using (var repository = RepositoryStorageFacade.CreateNew(_path)) {
            StoredGraphRevision seed = Publish(repository, "main", [ObjectVersionRecord.CreateBase(1, bytes)]);
            s0 = seed.Address;
            using PendingRepositorySave pending = MechanismFixture.Prepare(repository, "main", StoredGraphKind.State,
                new(s0, new(1), [ObjectVersionRecord.CreateDelta(1, seed.ObjectHeads[1], delta.Body)]));
            s1 = repository.Commit(pending).Business.Address;
        }
        using var reader = RepositoryStorageFacade.OpenReadOnlyExisting(_path);
        Assert.Equal(s1, MechanismFixture.State(reader, "main")?.Address);
        Assert.Null(reader.SharedSchemaRoot);
        Assert.Null(reader.Store.ReadMetadata(s1).SchemaRoot);
        Assert.Equal(before.Entries.ToArray(), StoredGraphDecoder.Read(reader.Store, MechanismFixture.Snapshot(reader, s0), NoSchemas()).Rows[new(1)]
            .GetDictionaryState<int, NullableState<long>>().Entries.ToArray());
        Assert.Equal(after.Entries.ToArray(), StoredGraphDecoder.Read(reader.Store, MechanismFixture.Snapshot(reader, s1), NoSchemas()).Rows[new(1)]
            .GetDictionaryState<int, NullableState<long>>().Entries.ToArray());
        Assert.Equal(2, reader.Store.ReadObjectChain(MechanismFixture.Snapshot(reader, s1), 1).Entries.Count);
    }

    [Theory]
    [InlineData(DictionaryComparerKind.CurrentDefault)]
    [InlineData(DictionaryComparerKind.Application)]
    public void Stored_business_lookup_modes_preserve_distinct_signed_zero_keys_without_resolving_current_comparison(DictionaryComparerKind mode) {
        DictionaryLayout layout = new(new(1, TypeTag.Single), new(2, TypeTag.Int32), codecVersion: 1);
        float negativeZero = BitConverter.Int32BitsToSingle(unchecked((int)0x80000000));
        FrozenDictionaryState<float, int> state = new(mode, [new(0f, 1), new(negativeZero, 2)]);
        PreparedBaseBody raw = DictionaryStateBody<float, int, SingleStateOps, Int32StateOps>.PrepareBase(state, layout);
        FrameAddress graph;
        using (var repository = RepositoryStorageFacade.CreateNew(_path)) {
            graph = Publish(repository, "main", [ObjectVersionRecord.CreateBase(1,
                TypedBaseBodyCodec.Encode(ObjectLayout.ForDictionary(layout), raw))]).Address;
        }
        using var reader = RepositoryStorageFacade.OpenReadOnlyExisting(_path);
        int resolutions = 0;
        StateModelRegistry registry = new();
        registry.UseDictionaryComparerResolver(_ => { resolutions++; throw new InvalidOperationException("Stored lookup must not execute a current comparer."); });
        FrozenDictionaryState<float, int> decoded = StoredGraphDecoder.Read(reader.Store, MechanismFixture.Snapshot(reader, graph), registry.CreateEnvironment()).Rows[new(1)].GetDictionaryState<float, int>();
        Assert.Equal(mode, decoded.ComparerKind);
        Assert.Equal(0, BitConverter.SingleToInt32Bits(decoded[0].Key));
        Assert.Equal(unchecked((int)0x80000000), BitConverter.SingleToInt32Bits(decoded[1].Key));
        Assert.Equal(0, resolutions);
    }

    [Theory]
    [InlineData("count")]
    [InlineData("order")]
    [InlineData("duplicateFieldId")]
    [InlineData("typeMismatch")]
    [InlineData("codec")]
    [InlineData("inactiveElement")]
    public void Valid_employee_bodies_cannot_authorize_noncanonical_or_inconsistent_Dictionary_descriptors(string corruption) {
        ObjectLayout dictionary = UserDictionary(Key(TypeTag.Int32), Point(TypeTag.Int32));
        SchemaCatalogView original = Prepare(dictionary).View;
        Dictionary<ObjectId, ObjectStateRecord> rows = original.Rows.ToDictionary();
        ObjectId id = original.DescriptorId(dictionary);
        DescriptorDto d = rows[id].GetState<DescriptorDto>();
        ObjectStateRecord fields = rows[d.Segment0Field4];
        ObjectId[] slots = fields.GetListState<ObjectId>().Elements.ToArray();
        switch (corruption) {
            case "count":
                rows[fields.Id] = new(fields.Id, fields.Layout.List!, new FrozenListState<ObjectId>([slots[0]]));
                break;
            case "order":
                rows[fields.Id] = new(fields.Id, fields.Layout.List!, new FrozenListState<ObjectId>(slots.Reverse().ToArray()));
                break;
            case "duplicateFieldId":
                FieldDto value = rows[slots[1]].GetState<FieldDto>();
                Replace(rows, slots[1], new FieldDto(1, value.Segment0Field2, value.Segment0Field3, value.Segment0Field4, value.Segment0Field5));
                break;
            case "typeMismatch":
                // The helper list is referenced only by this Dictionary TypeExpr, so both slots remain intact.
                var type = rows[d.Segment0Field2].GetState<Atelia.DurableGraph.Generated.Family_503054.V1>();
                ObjectStateRecord arguments = rows[type.Segment0Field4];
                rows[arguments.Id] = new(arguments.Id, arguments.Layout.List!, new FrozenListState<ObjectId>(arguments.GetListState<ObjectId>().Elements.ToArray().Reverse().ToArray()));
                break;
            case "codec":
                Replace(rows, id, new DescriptorDto(5, d.Segment0Field2, 0, d.Segment0Field4, default, 2, default));
                break;
            default:
                Replace(rows, id, new DescriptorDto(5, d.Segment0Field2, 0, d.Segment0Field4, slots[0], 1, default));
                break;
        }
        FrameAddress meta = WriteMeta(original.RootId, rows);
        using var reader = RepositoryStorageFacade.OpenReadOnlyExisting(_path);
        Assert.Equal((byte)5, StoredGraphDecoder.ReadMeta(reader.Store, meta).Rows[id].GetState<DescriptorDto>().Segment0Field1);
        Assert.Throws<InvalidDataException>(() => SchemaCatalogView.Load(reader.Store, meta));
    }

    [Theory]
    [InlineData("key")]
    [InlineData("value")]
    public void Both_exact_Dictionary_dependencies_reject_accepted_same_key_shape_conflicts_before_append(string changed) {
        DurableSchema key = Key(TypeTag.Int32), point = Point(TypeTag.Int32);
        ObjectLayout first = UserDictionary(key, point);
        ObjectLayout incompatible = UserDictionary(changed == "key" ? Key(TypeTag.Int64) : key,
            changed == "value" ? Point(TypeTag.Int64) : point);
        DurableSchema root = new("Dictionary.Owner", 1, DurableFieldInfo.Reference(1, first.Type));
        SchemaCatalogWorkspace catalog = new(static () => { });
        catalog.Set(root);
        PreparedSchemaCatalogWrite prepared = catalog.PrepareOwned([first]);
        Assert.Equal(3, prepared.View.Schemas.Count);
        using var repository = RepositoryStorageFacade.CreateNew(_path);
        Publish(repository, "main", [ObjectVersionRecord.CreateBase(1,
            TypedBaseBodyCodec.Encode(ObjectLayout.ForDurable(root), new PreparedBaseBody([2]), prepared.View)),
            ObjectVersionRecord.CreateBase(2, TypedBaseBodyCodec.Encode(first, new PreparedBaseBody([4, 0]), prepared.View))], prepared);
        FrameAddress accepted = repository.SharedSchemaRoot!.Value;
        long frames = repository.Store.AppendedFrames;
        catalog.Set(root); // The owner has the same nominal Dictionary<Key,Point?> field.
        Assert.Throws<SchemaConflictException>(() => catalog.PrepareOwned([incompatible]));
        Assert.Equal(frames, repository.Store.AppendedFrames);
        Assert.Equal(accepted, repository.SharedSchemaRoot);
        Assert.Equal(key, catalog.Accepted!.Schemas[new(key.Type, 1)]);
        Assert.Equal(point, catalog.Accepted.Schemas[new(point.Type, 1)]);
        Assert.Equal(first, catalog.Accepted.ResolveLayout(catalog.Accepted.DescriptorId(first)));
    }

    [Fact]
    public void Discard_recomputes_both_captured_slots_without_erasing_an_explicit_root_still_requiring_the_old_key() {
        DurableSchema keyA = Key(TypeTag.Int32), keyB = Key(TypeTag.Int64);
        DurableSchema pointA = Point(TypeTag.Int32), pointB = Point(TypeTag.Int64);
        ObjectLayout first = UserDictionary(keyA, pointA), next = UserDictionary(keyB, pointB);
        SchemaCatalogWorkspace catalog = new(static () => { });
        PreparedSchemaCatalogWrite abandoned = catalog.PrepareOwned([first]);
        using var repository = RepositoryStorageFacade.CreateNew(_path);
        using (PendingRepositorySave pending = MechanismFixture.Prepare(repository, "discarded", StoredGraphKind.State,
            new(null, new(1), [ObjectVersionRecord.CreateBase(1,
                TypedBaseBodyCodec.Encode(first, new PreparedBaseBody([4, 0]), abandoned.View))]), abandoned.Write)) { }
        Assert.Equal(0, repository.Store.AppendedFrames);
        Assert.Null(MechanismFixture.FindCurrent(repository, "discarded")?.Address);
        Assert.Null(catalog.Accepted);
        Assert.Equal(0, catalog.DirtyCount);
        PreparedSchemaCatalogWrite corrected = catalog.PrepareOwned([next]);
        Assert.Equal(keyA, abandoned.View.Schemas[new(keyA.Type, 1)]);
        Assert.Equal(pointA, abandoned.View.Schemas[new(pointA.Type, 1)]);
        Assert.Equal(keyB, corrected.View.Schemas[new(keyB.Type, 1)]);
        Assert.Equal(pointB, corrected.View.Schemas[new(pointB.Type, 1)]);
        catalog.Set(new DurableSchema("Dictionary.Other", 1, new DurableFieldInfo(1, TypeTag.InlineValue, inlineSchema: keyA)));
        Assert.Throws<SchemaConflictException>(() => catalog.PrepareOwned([next]));
        Assert.Equal(1, catalog.DirtyCount);
        catalog.Set(new DurableSchema("Dictionary.Other", 1, new DurableFieldInfo(1, TypeTag.InlineValue, inlineSchema: keyB)));
        SchemaCatalogView final = catalog.PrepareOwned([next]).View;
        Assert.Equal(next, final.ResolveLayout(final.DescriptorId(next)));
    }

    [Fact]
    public void A_user_reference_key_remains_dynamic_without_registering_the_unknown_target_body() {
        ObjectLayout layout = ObjectLayout.ForDictionary(new(DurableFieldInfo.Reference(1, TypeExpr.Named("Dictionary.UnknownKey")),
            new(2, TypeTag.Int32), codecVersion: 1));
        Assert.Null(layout.Dictionary!.KeySlot.ValueSchema);
        Assert.Throws<InvalidDataException>(() => TypedBaseBodyCodec.Encode(layout, new PreparedBaseBody([3, 0])));
        PreparedSchemaCatalogWrite prepared = Prepare(layout);
        FrameAddress meta = WriteMeta(prepared.View.RootId, prepared.View.Rows);
        using var reader = RepositoryStorageFacade.OpenReadOnlyExisting(_path);
        SchemaCatalogView loaded = SchemaCatalogView.Load(reader.Store, meta);
        Assert.Empty(loaded.Schemas);
        Assert.Equal(layout, loaded.ResolveLayout(Assert.Single(loaded.DescriptorIds)));
        byte[] body = TypedBaseBodyCodec.Encode(layout, new PreparedBaseBody([3, 0]), loaded);
        Assert.Equal((byte)1, body[1]);
        Assert.Equal(layout, TypedBaseBodyCodec.Decode(body, loaded).Layout);
        Assert.Throws<InvalidDataException>(() => TypedBaseBodyCodec.Decode(body, null));
    }

    [Fact]
    public void A_builtin_Dictionary_cannot_gain_a_dynamic_descriptor_from_valid_employee_records() {
        ObjectLayout layout = ObjectLayout.ForDictionary(new(DurableFieldInfo.Reference(1, TypeExpr.Named("Dictionary.UnknownKey")),
            new(2, TypeTag.Int32), codecVersion: 1));
        SchemaCatalogView original = Prepare(layout).View;
        Dictionary<ObjectId, ObjectStateRecord> rows = original.Rows.ToDictionary();
        ObjectId id = original.DescriptorId(layout);
        DescriptorDto descriptor = rows[id].GetState<DescriptorDto>();
        var type = rows[descriptor.Segment0Field2].GetState<Atelia.DurableGraph.Generated.Family_503054.V1>();
        ObjectId keyType = rows[type.Segment0Field4].GetListState<ObjectId>().Elements[0];
        Replace(rows, keyType, new Atelia.DurableGraph.Generated.Family_503054.V1((byte)TypeExprKind.Builtin, (byte)TypeTag.Int32, default, default));
        ObjectId keyField = rows[descriptor.Segment0Field4].GetListState<ObjectId>().Elements[0];
        Replace(rows, keyField, new FieldDto(1, (byte)TypeTag.Int32, default, default, default));
        FrameAddress meta = WriteMeta(original.RootId, rows);
        using var reader = RepositoryStorageFacade.OpenReadOnlyExisting(_path);
        Assert.Equal((byte)5, StoredGraphDecoder.ReadMeta(reader.Store, meta).Rows[id].GetState<DescriptorDto>().Segment0Field1);
        Assert.Throws<InvalidDataException>(() => SchemaCatalogView.Load(reader.Store, meta));
    }

    [Fact]
    public void A_valid_structural_Dictionary_body_cannot_expand_the_meta_employee_whitelist() {
        PreparedSchemaCatalogWrite prepared = Prepare();
        ObjectLayout layout = ObjectLayout.ForDictionary(new(new(1, TypeTag.Int32), new(2, TypeTag.Int32), codecVersion: 1));
        byte[] body = TypedBaseBodyCodec.Encode(layout, new PreparedBaseBody([0, 0]));
        var decoded = TypedBaseBodyCodec.Decode(body, null);
        Assert.Equal(0, BootstrapBindings.Reader(decoded.Layout).Read(new(100), new SingleBody(decoded.Body)).GetDictionaryState<int, int>().Count);
        FrameAddress meta = WriteMeta(prepared.View.RootId, prepared.View.Rows, ObjectVersionRecord.CreateBase(100, body));
        using var reader = RepositoryStorageFacade.OpenReadOnlyExisting(_path);
        Assert.Throws<InvalidDataException>(() => SchemaCatalogView.Load(reader.Store, meta));
    }

    [Fact]
    public void A_nullable_key_can_be_described_but_the_existing_body_policy_rejects_it_even_when_empty() {
        DictionaryLayout layout = new(DurableFieldInfo.Nullable(1, new(1, TypeTag.Int32)), new(2, TypeTag.Int32), codecVersion: 1);
        byte[] bytes = TypedBaseBodyCodec.Encode(ObjectLayout.ForDictionary(layout), new PreparedBaseBody([0, 0]));
        var decoded = TypedBaseBodyCodec.Decode(bytes, null);
        Assert.Equal(ObjectLayout.ForDictionary(layout), decoded.Layout);
        Assert.Throws<InvalidDataException>(() => BootstrapBindings.Reader(decoded.Layout, NoSchemas()).Read(new(1), new SingleBody(decoded.Body)));
    }

    private static IEnumerable<ObjectVersionRecord> LookupRows(SchemaCatalogView view, DictionaryLayout layout, PreparedBaseBody raw, string first, string second) => [
        ObjectVersionRecord.CreateBase(1, TypedBaseBodyCodec.Encode(ObjectLayout.ForDurable(OldRoot), new PreparedBaseBody([7]), view)),
        ObjectVersionRecord.CreateBase(10, TypedBaseBodyCodec.Encode(ObjectLayout.ForDictionary(layout), raw)),
        ObjectVersionRecord.CreateBase(100, TypedBaseBodyCodec.Encode(ObjectLayout.String, StringBodyPreparer.PrepareBase(first))),
        ObjectVersionRecord.CreateBase(101, TypedBaseBodyCodec.Encode(ObjectLayout.String, StringBodyPreparer.PrepareBase(second))),
    ];
    private static StateModelRegistry RootModels(Action normalize) {
        StatePreparationBinding<byte> preparation = new(CurrentRoot,
            static (in byte value) => new([value]), static (in byte prior, in byte next) => new(prior != next, [next]));
        StateReaderBinding<byte> Reader(DurableSchema schema) => new(schema,
            static (ref BareValueReader reader) => reader.ReadByte(),
            static (ref BareValueReader reader, in byte prior) => reader.ReadByte(), Visit);
        StateModelBinding<LookupRoot, byte> model = new(preparation, [Reader(OldRoot), Reader(CurrentRoot)],
            row => { normalize(); return row.GetState<byte>(); }, static () => new(),
            static (LookupRoot root, in byte state, ObjectReadTable objects) => root.Value = state,
            static (root, context) => root.Value, Visit);
        StateModelRegistry registry = new();
        registry.Register(model);
        return registry;
    }
    private static void Visit(in byte state, IStateReferenceVisitor visitor) { }
    private sealed class LookupRoot : IDurableObject { internal byte Value; }
    private static DurableSchema Key(TypeTag tag) => new("Dictionary.Key", 1, SchemaKind.InlineValue, new DurableFieldInfo(1, tag));
    private static DurableSchema Point(TypeTag tag) => new("Dictionary.Point", 1, SchemaKind.InlineValue, new DurableFieldInfo(1, tag));
    private static ObjectLayout UserDictionary(DurableSchema key, DurableSchema point) => ObjectLayout.ForDictionary(new(
        new(1, TypeTag.InlineValue, inlineSchema: key), DurableFieldInfo.Nullable(2, new(1, TypeTag.InlineValue, inlineSchema: point)), codecVersion: 1));
    private static ModelExecutionEnvironment NoSchemas() => new StateModelRegistry().CreateEnvironment(
        schemaLookup: _ => throw new InvalidOperationException("Builtin Dictionary must not query Schema."));
    private static PreparedSchemaCatalogWrite Prepare(params ObjectLayout[] layouts) {
        SchemaCatalogWorkspace owner = new(static () => { });
        foreach (ObjectLayout layout in layouts) { owner.Set(layout); }
        return owner.Prepare();
    }
    private static void Replace<TState>(Dictionary<ObjectId, ObjectStateRecord> rows, ObjectId id, TState state) where TState : unmanaged =>
        rows[id] = new(id, rows[id].Schema!, state);
    private FrameAddress WriteMeta(ObjectId root, IReadOnlyDictionary<ObjectId, ObjectStateRecord> rows, ObjectVersionRecord? extra = null) {
        ModelExecutionEnvironment employees = BootstrapBindings.CreateEnvironment();
        IEnumerable<ObjectVersionRecord> records = rows.Values.Select(row => {
            ObjectStateRecord prepared = BootstrapBindings.WithPreparation(row, employees);
            PreparedBaseBody raw = prepared.Kind == ObjectStateKind.String ? StringBodyPreparer.PrepareBase(prepared.StringContent)
                : prepared.Preparation!.PrepareBase(prepared);
            return ObjectVersionRecord.CreateBase(prepared.Id.Value, TypedBaseBodyCodec.Encode(prepared.Layout, raw));
        });
        if (extra is not null) { records = records.Append(extra); }
        using var repository = RepositoryStorageFacade.CreateNew(_path);
        FrameAddress graph = repository.Store.AppendMeta(null, new(null, root, records)).Address;
        repository.Data.ConfirmDurable();
        return graph;
    }
    private static StoredGraphRevision Publish(RepositoryStorageFacade repository, string branch, IEnumerable<ObjectVersionRecord> records, PreparedSchemaCatalogWrite? catalog = null) {
        using PendingRepositorySave pending = MechanismFixture.Prepare(repository, branch, StoredGraphKind.State, new(null, new(1), records), catalog?.Write);
        return repository.Commit(pending, catalog is null ? null : catalog.Install,
            prepareMeta: catalog is null ? null : catalog.PrepareInstall).Business;
    }
    private sealed class SingleBody(byte[] body) : IStateBodySource {
        public int Count => 1;
        public ReadOnlySpan<byte> GetBody(int index) => index == 0 ? body : throw new ArgumentOutOfRangeException(nameof(index));
    }
    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}
