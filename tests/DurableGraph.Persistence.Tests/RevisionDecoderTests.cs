using System.Buffers;
using Atelia.Binary;

using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

// Exact-reader contracts over real split frames and joint publication. The definitions
// intentionally have no current domain model, capture implementation or Upgrade.
public sealed class SplitExactDecodingTests : IDisposable {
    private const string Prefix = "dg-split-exact-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
    private static readonly DurableSchema NodeSchema = new("Exact.Node", 1,
        new DurableFieldInfo(1, TypeTag.Byte), new DurableFieldInfo(2, TypeTag.String));
    private static readonly DurableSchema NumberSchema = new("Exact.Number", 1, new DurableFieldInfo(1, TypeTag.Byte));

    [Fact]
    public void Cold_full_membership_reconstructs_Delta_and_owns_strings_after_resources_close() {
        FrameAddress first, changed;
        using (var writer = RepositoryStorageFacade.CreateNew(_path)) {
            PreparedSchemaCatalogWrite catalog = Catalog(NodeSchema);
            StoredGraphRevision initial = Publish(writer, new(null, new(1), [
                Node(catalog.View, 1, 4, 3), Node(catalog.View, 2, 5, 3),
                Text(3, "same"), Text(4, "same"), Text(5, ""), Text(6, ""),
            ]), catalog);
            first = initial.Address;
            changed = Publish(writer, new(first, new(1), [
                ObjectVersionRecord.CreateDelta(1, initial.ObjectHeads[1], new byte[] { 1, 9 }),
            ])).Address;
        }
        DecodedRevision decoded;
        using (var reader = RepositoryStorageFacade.OpenReadOnlyExisting(_path)) {
            long before = DataBytes(reader.Data);
            var configuration = Nodes().FreezeConfiguration();
            DecodedRevision original = DecodedRevision.ReadStoredGraph(reader.Store, first, configuration);
            decoded = DecodedRevision.ReadStoredGraph(reader.Store, changed, configuration, original.BindingEnvironment);
            Assert.Equal(new NodeState(4, new(3)), original.GetRequired(new(1)).GetState<NodeState>());
            ObjectVersionChain chain = reader.Store.ReadObjectChain(changed, 1);
            Assert.Equal(2, chain.Entries.Count);
            Assert.Equal(original.ObjectHeads[new(1)], chain.Entries[1].Record.PriorAddress);
            Assert.Equal(new byte[] { 1, 9 }, chain.Entries[1].Record.Body.ToArray());
            Assert.Equal(chain.ReconstructionPayloadBytes, decoded.ObjectStorage[new(1)].ReconstructionPayloadBytes);
            Assert.Equal(before, DataBytes(reader.Data));
            Assert.Equal(0, reader.Store.AppendedFrames);
        }
        Assert.Equal(changed, decoded.RevisionAddress);
        Assert.Equal(new uint[] { 1, 2, 3, 4, 5, 6 }, decoded.Objects.Select(static row => row.Id.Value));
        Assert.Equal(new NodeState(9, new(3)), decoded.GetRequired(new(1)).GetState<NodeState>());
        Assert.Equal(new NodeState(5, new(3)), decoded.GetRequired(new(2)).GetState<NodeState>());
        Assert.Same(decoded.GetRequired(new(3)).StringContent, decoded.Strings.ResolveString(new(3)));
        Assert.Same(decoded.Strings.ResolveString(new(3)), decoded.Strings.ResolveString(decoded.GetRequired(new(2)).GetState<NodeState>().TextId));
        Assert.Equal(decoded.Strings.ResolveString(new(3)), decoded.Strings.ResolveString(new(4)));
        Assert.NotSame(decoded.Strings.ResolveString(new(3)), decoded.Strings.ResolveString(new(4)));
        Assert.Same(string.Empty, decoded.Strings.ResolveString(new(5)));
        Assert.Same(string.Empty, decoded.GetRequired(new(6)).StringContent);
        Assert.Null(decoded.Strings.ResolveString(default));
        Assert.Throws<InvalidDataException>(() => decoded.GetRequired(default));
        Assert.Throws<InvalidDataException>(() => decoded.GetRequired(new(99)));
        Assert.False(decoded.Objects is System.Collections.ICollection);
        Assert.False(decoded.Objects is IList<ObjectStateRecord>);
        NodeState copy = decoded.GetRequired(new(1)).GetState<NodeState>() with { Value = 99 };
        Assert.Equal((byte)99, copy.Value);
        Assert.Equal((byte)9, decoded.GetRequired(new(1)).GetState<NodeState>().Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Retained_owner_revalidates_removed_or_wrong_kind_string_in_each_complete_view(bool wrongKind) {
        using var repository = RepositoryStorageFacade.CreateNew(_path);
        PreparedSchemaCatalogWrite catalog = Catalog(NodeSchema, NumberSchema);
        StoredGraphRevision initial = Publish(repository, new(null, new(1), [Node(catalog.View, 1, 4, 2), Text(2, "value")]), catalog);
        StoredGraphRevision invalid = Publish(repository, new(initial.Address, new(1),
            wrongKind ? [Durable(catalog.View, 2, NumberSchema, [7])] : [], wrongKind ? [] : [2]));
        var configuration = Nodes(NumberSchema).FreezeConfiguration();
        DecodedRevision previous = DecodedRevision.ReadStoredGraph(repository.Store, initial.Address, configuration);
        Assert.Equal(initial.ObjectHeads[1], invalid.ObjectHeads[1]);
        Assert.Equal("value", previous.Strings.ResolveString(new(2)));
        DecodedRevision? result = null;
        Assert.Throws<InvalidDataException>(() => result = DecodedRevision.ReadStoredGraph(repository.Store, invalid.Address, configuration, previous.BindingEnvironment));
        Assert.Null(result);
        Assert.Equal("value", DecodedRevision.ReadStoredGraph(repository.Store, initial.Address, configuration, previous.BindingEnvironment).Strings.ResolveString(new(2)));
        Assert.False(repository.IsFaulted);
    }

    [Fact]
    public void Retained_nominal_owner_rechecks_the_actual_target_ancestry_in_the_second_view() {
        DurableSchema target = new("Exact.Target", 1, new DurableFieldInfo(1, TypeTag.Byte));
        DurableSchema derived = new("Exact.Derived", 1, [], target);
        DurableSchema other = new("Exact.Other", 1, new DurableFieldInfo(1, TypeTag.Byte));
        DurableSchema owner = new("Exact.Owner", 1, DurableFieldInfo.Reference(1, target.Type));
        using var repository = RepositoryStorageFacade.CreateNew(_path);
        PreparedSchemaCatalogWrite catalog = Catalog(owner, derived, other);
        StoredGraphRevision initial = Publish(repository, new(null, new(1), [Durable(catalog.View, 1, owner, [2]), Durable(catalog.View, 2, derived, [7])]), catalog);
        StoredGraphRevision invalid = Publish(repository, new(initial.Address, new(1), [Durable(catalog.View, 2, other, [8])]));
        StateModelRegistry models = new();
        RegisterExact(models, owner, schema => new StateReaderBinding<ObjectId>(schema,
            static (ref BareValueReader reader) => new(reader.ReadVarUInt32()),
            static (ref BareValueReader reader, in ObjectId prior) => new(reader.ReadVarUInt32()),
            (in ObjectId state, IStateReferenceVisitor visitor) => visitor.VisitObject(state, target.Type)));
        RegisterExact(models, derived, schema => ByteReader(schema));
        RegisterExact(models, other, schema => ByteReader(schema));
        var configuration = models.FreezeConfiguration();
        DecodedRevision previous = DecodedRevision.ReadStoredGraph(repository.Store, initial.Address, configuration);
        Assert.Equal(new ObjectId(2), previous.GetRequired(new(1)).GetState<ObjectId>());
        Assert.Equal(initial.ObjectHeads[1], invalid.ObjectHeads[1]);
        DecodedRevision? decoded = null;
        Assert.Throws<InvalidDataException>(() => decoded = DecodedRevision.ReadStoredGraph(repository.Store, invalid.Address, configuration, previous.BindingEnvironment));
        Assert.Null(decoded);
        Assert.Equal((byte)7, previous.GetRequired(new(2)).GetState<byte>());
    }

    [Theory]
    [InlineData(0)] // Owner scalar.
    [InlineData(1)] // Base scalar, with identical base nominal and version.
    [InlineData(2)] // Base version.
    [InlineData(3)] // Inline scalar, with identical inline nominal and version.
    public void Complete_definition_mismatch_precedes_the_objects_body_callback(int mismatch) {
        DurableSchema ancestor = new("Exact.Ancestor", 1, new DurableFieldInfo(1, TypeTag.Byte));
        DurableSchema point = new("Exact.Point", 1, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.Byte));
        DurableSchema stored = mismatch == 3
            ? new("Exact.Leaf", 1, new DurableFieldInfo(1, TypeTag.InlineValue, inlineSchema: point))
            : new("Exact.Leaf", 1, [new(1, TypeTag.Byte)], ancestor);
        DurableSchema wrong = mismatch switch {
            0 => new("Exact.Leaf", 1, [new(1, TypeTag.SByte)], ancestor),
            1 => new("Exact.Leaf", 1, [new(1, TypeTag.Byte)], new("Exact.Ancestor", 1, new DurableFieldInfo(1, TypeTag.SByte))),
            2 => new("Exact.Leaf", 1, [new(1, TypeTag.Byte)], new("Exact.Ancestor", 2, new DurableFieldInfo(1, TypeTag.Byte))),
            _ => new("Exact.Leaf", 1, new DurableFieldInfo(1, TypeTag.InlineValue, inlineSchema:
                new("Exact.Point", 1, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.SByte)))),
        };
        using var repository = RepositoryStorageFacade.CreateNew(_path);
        PreparedSchemaCatalogWrite catalog = Catalog(stored);
        StoredGraphRevision graph = Publish(repository, new(null, new(1), [Durable(catalog.View, 1, stored, [7])]), catalog);
        int matchingCalls = 0;
        StateModelRegistry matching = new();
        RegisterExact(matching, stored, schema => ByteReader(schema, () => matchingCalls++));
        DecodedRevision positive = DecodedRevision.ReadStoredGraph(repository.Store, graph.Address, matching);
        Assert.Equal((byte)7, positive.GetRequired(new(1)).GetState<byte>());
        Assert.Equal(stored, positive.GetRequired(new(1)).Schema);
        Assert.Equal(1, matchingCalls);
        int calls = 0;
        StateModelRegistry models = new();
        RegisterExact(models, wrong, _ => ByteReader(wrong, () => calls++));
        DecodedRevision? decoded = null;
        Assert.Throws<InvalidDataException>(() => decoded = DecodedRevision.ReadStoredGraph(repository.Store, graph.Address, models));
        Assert.Null(decoded);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(0)] // No definition.
    [InlineData(1)] // Metadata without body code.
    [InlineData(2)] // Only an earlier version's reader.
    public void Missing_definition_or_body_capability_does_not_fall_back(int missing) {
        DurableSchema stored = missing == 2 ? new("Exact.Number", 2, new DurableFieldInfo(1, TypeTag.Byte)) : NumberSchema;
        using var repository = RepositoryStorageFacade.CreateNew(_path);
        PreparedSchemaCatalogWrite catalog = Catalog(stored);
        StoredGraphRevision graph = Publish(repository, new(null, new(1), [Durable(catalog.View, 1, stored, [7])]), catalog);
        int calls = 0;
        StateModelRegistry models = new();
        if (missing == 1) { models.Register(Definition(NumberSchema)); }
        if (missing == 2) { models.Register(Definition(NumberSchema, schema => ByteReader(schema, () => calls++))); }
        Assert.Throws<InvalidDataException>(() => DecodedRevision.ReadStoredGraph(repository.Store, graph.Address, models));
        Assert.Equal(0, calls);
        Assert.False(repository.IsFaulted);
    }

    [Fact]
    public void Late_missing_historical_reader_cannot_publish_rows_or_borrow_callback_registration() {
        DurableSchema other = new("Exact.Other", 1, new DurableFieldInfo(1, TypeTag.Byte));
        using var repository = RepositoryStorageFacade.CreateNew(_path);
        PreparedSchemaCatalogWrite catalog = Catalog(NumberSchema, other);
        StoredGraphRevision graph = Publish(repository, new(null, new(1), [Durable(catalog.View, 1, NumberSchema, [7]), Durable(catalog.View, 2, other, [8])]), catalog);
        StateModelRegistry registry = new();
        int calls = 0;
        StateDefinitionBinding late = Definition(other, _ => ByteReader(other));
        registry.Register(Definition(NumberSchema, _ => ByteReader(NumberSchema, () => {
            calls++;
            if (calls == 1) { registry.Register(late); }
        })));
        DecodedRevision? decoded = null;
        Assert.Throws<InvalidDataException>(() => decoded = DecodedRevision.ReadStoredGraph(repository.Store, graph.Address, registry));
        Assert.Null(decoded);
        Assert.Equal(1, calls); // Pure earlier callbacks may run, but code registration is frozen for this operation.
        DecodedRevision retry = DecodedRevision.ReadStoredGraph(repository.Store, graph.Address, registry);
        Assert.Equal((byte)8, retry.GetRequired(new(2)).GetState<byte>());
        Assert.Equal(2, calls);
        Assert.False(repository.IsFaulted);
    }

    [Theory]
    [InlineData(0)] // Trailing Base.
    [InlineData(1)] // Missing Delta value.
    [InlineData(2)] // Trailing Delta.
    [InlineData(3)] // String Delta.
    [InlineData(4)] // Trailing string payload.
    public void Late_malformed_body_or_string_Delta_never_delivers_a_partial_view(int malformed) {
        using var repository = RepositoryStorageFacade.CreateNew(_path);
        PreparedSchemaCatalogWrite catalog = Catalog(NodeSchema);
        StoredGraphRevision initial = Publish(repository, new(null, new(1), [Node(catalog.View, 1, 4, 0),
            malformed is 3 or 4 ? Text(2, "text") : Node(catalog.View, 2, 5, 0)]), catalog);
        ObjectVersionRecord bad = malformed switch {
            0 => Durable(catalog.View, 2, NodeSchema, [5, 0, 99]),
            1 => ObjectVersionRecord.CreateDelta(2, initial.ObjectHeads[2], new byte[] { 1 }),
            2 => ObjectVersionRecord.CreateDelta(2, initial.ObjectHeads[2], new byte[] { 1, 9, 99 }),
            3 => ObjectVersionRecord.CreateDelta(2, initial.ObjectHeads[2], Array.Empty<byte>()),
            _ => ObjectVersionRecord.CreateBase(2, TypedBaseBodyCodec.Encode(ObjectLayout.String, new([0, 1]))),
        };
        StoredGraphRevision invalid = Publish(repository, new(initial.Address, new(1), [bad]));
        int reads = 0;
        int applies = 0;
        StateModelRegistry models = Nodes(onRead: () => reads++, onApply: () => applies++);
        long before = DataBytes(repository.Data);
        for (int attempt = 0; attempt < 2; attempt++) {
            DecodedRevision? decoded = null;
            if (malformed == 1) {
                Assert.Throws<EndOfStreamException>(() => decoded = DecodedRevision.ReadStoredGraph(repository.Store, invalid.Address, models));
            } else {
                Assert.Throws<InvalidDataException>(() => decoded = DecodedRevision.ReadStoredGraph(repository.Store, invalid.Address, models));
            }
            Assert.Null(decoded);
        }
        Assert.True(reads >= 2); // Owned physical cache hits do not skip typed full-consumption checks.
        Assert.Equal(malformed is 1 or 2 ? 2 : 0, applies); // A trailing Base cannot reach ApplyDelta.
        Assert.Equal(before, DataBytes(repository.Data));
        Assert.False(repository.IsFaulted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Completed_dynamic_header_cannot_use_unknown_or_inline_descriptor_as_object_kind(bool inline) {
        DurableSchema value = new("Exact.Value", 1, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.Byte));
        using var repository = RepositoryStorageFacade.CreateNew(_path);
        PreparedSchemaCatalogWrite catalog = Catalog(NumberSchema, value);
        uint id = inline ? catalog.View.SchemaNodeId(value).Value : uint.MaxValue;
        ArrayBufferWriter<byte> buffer = new();
        BareValueWriter bytes = new(buffer);
        bytes.WriteByte(TypedBaseBodyCodec.FormatVersion);
        bytes.WriteByte(1);
        bytes.WriteVarUInt32(id);
        bytes.WriteByte(7);
        StoredGraphRevision graph = Publish(repository, new(null, new(1), [ObjectVersionRecord.CreateBase(1, buffer.WrittenSpan)]), catalog);
        int calls = 0;
        StateModelRegistry models = new();
        RegisterExact(models, NumberSchema, _ => ByteReader(NumberSchema, () => calls++));
        Assert.Throws<InvalidDataException>(() => DecodedRevision.ReadStoredGraph(repository.Store, graph.Address, models));
        Assert.Equal(0, calls);
        Assert.False(repository.IsFaulted);
    }

    [Theory]
    [InlineData(DictionaryComparerKind.StringOrdinal, "left", "left")]
    [InlineData(DictionaryComparerKind.StringOrdinalIgnoreCase, "LEFT", "left")]
    [InlineData(DictionaryComparerKind.ReferenceIdentity, "", "")]
    public void Retained_Dictionary_repeats_lookup_validation_after_referenced_string_content_changes(
        DictionaryComparerKind comparer, string firstKey, string replacement) {
        DictionaryLayout layout = new(new(1, TypeTag.String), new(2, TypeTag.Int32), codecVersion: 1);
        FrozenDictionaryState<ObjectId, int> state = new(comparer, [new(new(2), 10), new(new(3), 20)]);
        PreparedBaseBody body = DictionaryStateBody<ObjectId, int, StringIdStateOps, Int32StateOps>.PrepareBase(state, layout);
        using var repository = RepositoryStorageFacade.CreateNew(_path);
        PreparedSchemaCatalogWrite catalog = Catalog(NumberSchema);
        StoredGraphRevision initial = Publish(repository, new(null, new(1), [Durable(catalog.View, 1, NumberSchema, [7]),
            Text(2, firstKey), Text(3, "right"), ObjectVersionRecord.CreateBase(10, TypedBaseBodyCodec.Encode(ObjectLayout.ForDictionary(layout), body))]), catalog);
        StoredGraphRevision invalid = Publish(repository, new(initial.Address, new(1), [Text(3, replacement)]));
        Assert.Equal(initial.ObjectHeads[10], invalid.ObjectHeads[10]);
        Assert.Equal(initial.ObjectHeads[2], invalid.ObjectHeads[2]);
        Assert.NotEqual(initial.ObjectHeads[3], invalid.ObjectHeads[3]);
        StateModelRegistry models = new();
        RegisterExact(models, NumberSchema, _ => ByteReader(NumberSchema));
        var configuration = models.FreezeConfiguration();
        DecodedRevision previous = DecodedRevision.ReadStoredGraph(repository.Store, initial.Address, configuration);
        Assert.Equal(2, previous.GetRequired(new(10)).GetDictionaryState<ObjectId, int>().Count);
        DecodedRevision? decoded = null;
        Assert.Throws<InvalidDataException>(() => decoded = DecodedRevision.ReadStoredGraph(repository.Store, invalid.Address, configuration, previous.BindingEnvironment));
        Assert.Null(decoded);
        Assert.Equal("right", previous.Strings.ResolveString(new(3)));
        Assert.Equal("right", DecodedRevision.ReadStoredGraph(repository.Store, initial.Address, configuration, previous.BindingEnvironment).Strings.ResolveString(new(3)));
        Assert.False(repository.IsFaulted);
    }

    private readonly record struct NodeState(byte Value, ObjectId TextId);

    private static StateModelRegistry Nodes(DurableSchema? extra = null, Action? onRead = null, Action? onApply = null) {
        StateModelRegistry models = new();
        RegisterExact(models, NodeSchema, schema => new StateReaderBinding<NodeState>(schema,
            (ref BareValueReader reader) => { onRead?.Invoke(); return new(reader.ReadByte(), new(reader.ReadVarUInt32())); },
            (ref BareValueReader reader, in NodeState prior) => {
                onApply?.Invoke();
                byte bitmap = reader.ReadByte();
                if (bitmap == 0 || bitmap > 3) { throw new InvalidDataException("Invalid test DTO Delta bitmap."); }
                return new((bitmap & 1) != 0 ? reader.ReadByte() : prior.Value,
                    (bitmap & 2) != 0 ? new(reader.ReadVarUInt32()) : prior.TextId);
            }, static (in NodeState state, IStateReferenceVisitor visitor) => visitor.VisitString(state.TextId)));
        if (extra is not null) { RegisterExact(models, extra, schema => ByteReader(schema)); }
        return models;
    }

    private static StateReaderBinding<byte> ByteReader(DurableSchema schema, Action? onRead = null) => new(schema,
        (ref BareValueReader reader) => { onRead?.Invoke(); return reader.ReadByte(); },
        static (ref BareValueReader reader, in byte prior) => reader.ReadByte(),
        static (in byte state, IStateReferenceVisitor visitor) => { });

    private static StateDefinitionBinding Definition(DurableSchema schema, Func<DurableSchema, StateReaderBinding>? reader = null) =>
        new(schema.SchemaId, schema.Kind, 0, null, [new(schema.SchemaId, schema.Version, schema.Kind, 0,
            schema.Fields.Select(field => new StateFieldTemplate(field.FieldId, StateBindingContext.NominalType(field), field.ValueSchema?.Version)),
            schema.BaseSchema is { } ancestor ? new(ancestor.Type, ancestor.Version) : null)],
            historicalReaderFactory: reader is null ? null : (actual, _) => reader(actual));

    private static void RegisterExact(StateModelRegistry registry, DurableSchema schema, Func<DurableSchema, StateReaderBinding>? reader = null) {
        if (schema.BaseSchema is { } ancestor) { RegisterExact(registry, ancestor); }
        foreach (DurableFieldInfo field in schema.Fields) {
            if (field.ValueSchema is { } inline) { RegisterExact(registry, inline); }
        }
        registry.Register(Definition(schema, reader));
    }

    private static PreparedSchemaCatalogWrite Catalog(params DurableSchema[] schemas) {
        SchemaCatalogWorkspace workspace = new(static () => { });
        foreach (DurableSchema schema in schemas) { workspace.Set(schema); }
        return workspace.Prepare();
    }

    private static StoredGraphRevision Publish(RepositoryStorageFacade repository, PreparedGraphWrite write, PreparedSchemaCatalogWrite? catalog = null) {
        using PendingRepositorySave pending = repository.Prepare("main", StoredGraphKind.State, write, catalog?.Write);
        return repository.Commit(pending).Business;
    }

    private static ObjectVersionRecord Durable(SchemaCatalogView catalog, uint id, DurableSchema schema, ReadOnlySpan<byte> body) =>
        ObjectVersionRecord.CreateBase(id, TypedBaseBodyCodec.Encode(ObjectLayout.ForDurable(schema), new(body), catalog));
    private static ObjectVersionRecord Node(SchemaCatalogView catalog, uint id, byte value, byte text) => Durable(catalog, id, NodeSchema, [value, text]);
    private static ObjectVersionRecord Text(uint id, string value) => ObjectVersionRecord.CreateBase(id,
        TypedBaseBodyCodec.Encode(ObjectLayout.String, StringBodyPreparer.PrepareBase(value)));

    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}
