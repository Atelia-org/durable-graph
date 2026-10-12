using Atelia.DurableGraph.Schema;
using System.Buffers;
using Atelia.Binary;
using Atelia.Rbf;

namespace Atelia.DurableGraph.Persistence;

/// <summary>The sole persistent grammar for closed Schemas and object representations.</summary>
internal static class SchemaCatalogWireCodec {
    internal const uint RbfTag = 0x31424353; // SCB1 in little-endian byte order.
    internal const byte Version = 2;
    internal const int MaximumDepth = 256;

    internal static byte[] Write(IReadOnlyList<SchemaCatalogEntry> entries) {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0) { throw new ArgumentException("A catalog snapshot must not be empty.", nameof(entries)); }
        try {
            var state = new ValidationState();
            var buffer = new LimitedBufferWriter();
            var writer = new BareValueWriter(buffer);
            writer.WriteByte(Version);
            writer.WriteVarUInt32((uint)entries.Count);
            foreach (SchemaCatalogEntry entry in entries) {
                ArgumentNullException.ThrowIfNull(entry);
                writer.WriteVarUInt32(entry.Id.Value);
                if (entry.Schema is { } schema) {
                    writer.WriteByte((byte)schema.Kind);
                    TypeExprWireCodec.Write(ref writer, schema.Type);
                    writer.WriteVarUInt32((uint)schema.Version);
                    writer.WriteVarUInt32(schema.BaseSchema is { } ancestor
                        ? state.RequireSchema(ancestor, SchemaKind.ReferenceObject).Value : 0);
                    writer.WriteVarUInt32((uint)schema.Fields.Length);
                    foreach (DurableFieldInfo field in schema.Fields) {
                        writer.WriteVarUInt32((uint)field.FieldId);
                        WriteSlot(ref writer, field, state);
                    }
                }
                else if (entry.Array is { } array) {
                    writer.WriteByte(3);
                    writer.WriteVarUInt32(array.CodecVersion);
                    writer.WriteByte((byte)array.Constructor);
                    WriteSlot(ref writer, array.ElementSlot, state);
                }
                else if (entry.List is { } list) {
                    writer.WriteByte(4);
                    writer.WriteVarUInt32(list.CodecVersion);
                    WriteSlot(ref writer, list.ElementSlot, state);
                }
                else {
                    writer.WriteByte(5);
                    writer.WriteVarUInt32(entry.Dictionary!.CodecVersion);
                    WriteSlot(ref writer, entry.Dictionary.KeySlot, state);
                    WriteSlot(ref writer, entry.Dictionary.ValueSlot, state);
                }
                state.Add(entry);
            }
            return buffer.ToArray();
        }
        catch (InvalidDataException error) { throw new ArgumentException(error.Message, nameof(entries), error); }
    }

    internal static SchemaCatalogEntry[] Read(ReadOnlySpan<byte> payload) {
        var reader = new BareValueReader(payload);
        if (reader.ReadByte() != Version) { throw new InvalidDataException("Unknown Schema catalog version."); }
        uint count = reader.ReadVarUInt32();
        // The shortest row is a List: ID, kind, codec version, slot.
        if (count == 0 || count > (uint)reader.RemainingCount / 4) {
            throw new InvalidDataException("Invalid Schema catalog row count.");
        }
        var state = new ValidationState();
        var result = new SchemaCatalogEntry[(int)count];
        for (int i = 0; i < result.Length; i++) {
            var id = new RepresentationId(reader.ReadVarUInt32());
            byte kind = reader.ReadByte();
            SchemaCatalogEntry entry;
            try {
                if (kind is 1 or 2) {
                    TypeExpr type = TypeExprWireCodec.Read(ref reader);
                    uint version = reader.ReadVarUInt32();
                    if (type.Kind != TypeExprKind.Named || version == 0 || version > int.MaxValue) {
                        throw new InvalidDataException("A Schema requires a closed named type and a positive Int32 version.");
                    }
                    uint baseId = reader.ReadVarUInt32();
                    if (kind == 2 && baseId != 0) { throw new InvalidDataException("An inline Schema cannot have a base."); }
                    DurableSchema? ancestor = baseId == 0 ? null : state.ResolveSchema(new(baseId), SchemaKind.ReferenceObject);
                    uint fieldCount = reader.ReadVarUInt32();
                    if (fieldCount > (uint)reader.RemainingCount / 2) { throw new InvalidDataException("Invalid Schema field count."); }
                    var fields = new DurableFieldInfo[(int)fieldCount];
                    uint previousField = 0;
                    for (int j = 0; j < fields.Length; j++) {
                        uint fieldId = reader.ReadVarUInt32();
                        if (fieldId <= previousField || fieldId > int.MaxValue) {
                            throw new InvalidDataException("Schema field IDs must be positive, strictly increasing Int32 values.");
                        }
                        fields[j] = ReadSlot(ref reader, (int)fieldId, state);
                        previousField = fieldId;
                    }
                    entry = SchemaCatalogEntry.ForSchema(id, new(type, (int)version, fields, ancestor, (SchemaKind)kind));
                }
                else if (kind == 3) {
                    uint codecVersion = reader.ReadVarUInt32();
                    byte constructor = reader.ReadByte();
                    if (codecVersion != 1 || constructor is < 4 or > 7) {
                        throw new InvalidDataException("Unsupported array codec version or constructor.");
                    }
                    entry = SchemaCatalogEntry.ForArray(id, new((TypeExprKind)constructor, ReadSlot(ref reader, 1, state), codecVersion));
                }
                else if (kind == 4) {
                    uint codecVersion = reader.ReadVarUInt32();
                    if (codecVersion != 2) { throw new InvalidDataException("Unsupported List codec version."); }
                    entry = SchemaCatalogEntry.ForList(id, new(ReadSlot(ref reader, 1, state), codecVersion));
                }
                else if (kind == 5) {
                    uint codecVersion = reader.ReadVarUInt32();
                    if (codecVersion != 1) { throw new InvalidDataException("Unsupported Dictionary codec version."); }
                    DurableFieldInfo keySlot = ReadSlot(ref reader, 1, state);
                    DurableFieldInfo valueSlot = ReadSlot(ref reader, 2, state);
                    entry = SchemaCatalogEntry.ForDictionary(id, new(keySlot, valueSlot, codecVersion));
                }
                else { throw new InvalidDataException($"Unknown Schema catalog node kind {kind}."); }
            }
            catch (ArgumentException error) { throw new InvalidDataException("Invalid persisted catalog layout.", error); }
            state.Add(entry);
            result[i] = entry;
        }
        reader.EnsureFullyConsumed();
        return result;
    }

    internal static SchemaKey Key(DurableSchema schema) => new(schema.Type, schema.Version);

    private static void WriteSlot(ref BareValueWriter writer, DurableFieldInfo field, ValidationState state) {
        writer.WriteByte((byte)field.TypeTag);
        if (field.TypeTag == TypeTag.Nullable) { WriteSlot(ref writer, field.NullableLayout!.ElementSlot, state); }
        else if (field.TypeTag == TypeTag.ObjectReference) { TypeExprWireCodec.Write(ref writer, field.TargetType!); }
        else if (field.TypeTag == TypeTag.InlineValue) {
            writer.WriteVarUInt32(state.RequireSchema(field.InlineSchema!, SchemaKind.InlineValue).Value);
        }
    }

    private static DurableFieldInfo ReadSlot(ref BareValueReader reader, int fieldId, ValidationState state, bool nullableChild = false) {
        byte tag = reader.ReadByte();
        if (tag == 18) {
            if (nullableChild) { throw new InvalidDataException("A Nullable child cannot itself be Nullable."); }
            return DurableFieldInfo.Nullable(fieldId, ReadSlot(ref reader, 1, state, nullableChild: true));
        }
        if (TypeTagFacts.IsBuiltin((TypeTag)tag)) { return new(fieldId, (TypeTag)tag); }
        if (tag == 15) { return DurableFieldInfo.Reference(fieldId, TypeExprWireCodec.Read(ref reader)); }
        if (tag == 16) {
            return new(fieldId, TypeTag.InlineValue,
                inlineSchema: state.ResolveSchema(new(reader.ReadVarUInt32()), SchemaKind.InlineValue));
        }
        throw new InvalidDataException($"Unknown field slot tag {tag}.");
    }

    // Shared read/write validation keeps directory uniqueness, nominal declarations,
    // and exact dependency checks identical. Reference edges never require a target node.
    private sealed class ValidationState {
        private readonly Dictionary<RepresentationId, SchemaCatalogEntry> _nodes = new();
        private readonly Dictionary<SchemaKey, RepresentationId> _schemaIds = new();
        private readonly HashSet<ObjectLayout> _containers = new();
        private readonly Dictionary<RepresentationId, int> _heights = new();
        private readonly SchemaDeclarationValidator _declarations = new();
        private ulong _next = 2;

        internal DurableSchema ResolveSchema(RepresentationId id, SchemaKind kind) {
            if (!_nodes.TryGetValue(id, out SchemaCatalogEntry? entry) || entry.Schema is not { } schema || schema.Kind != kind) {
                throw new InvalidDataException($"Missing prior Schema node {id.Value}, or wrong dependency kind.");
            }
            return schema;
        }

        internal RepresentationId RequireSchema(DurableSchema schema, SchemaKind kind) {
            if (!_schemaIds.TryGetValue(Key(schema), out RepresentationId id) || !ResolveSchema(id, kind).Equals(schema)) {
                throw new InvalidDataException("An exact dependency must match a previously registered Schema node.");
            }
            return id;
        }

        internal void Add(SchemaCatalogEntry entry) {
            if (entry.Id.Value != _next) { throw new InvalidDataException("Catalog IDs must be contiguous, increasing values starting at two."); }
            int height = 1;
            if (entry.Schema is { } schema) {
                _declarations.ValidateDeclaration(schema.Type, schema.Kind);
                if (schema.BaseSchema is { } ancestor) { AddDependency(ancestor, SchemaKind.ReferenceObject); }
                foreach (DurableFieldInfo field in schema.Fields) { ValidateSlot(field); }
                if (!_schemaIds.TryAdd(Key(schema), entry.Id)) {
                    throw new InvalidDataException("A Schema key cannot be registered more than once, even under another ID.");
                }
            }
            else {
                if (entry.Dictionary is { } dictionary) {
                    ValidateContainerSlot(dictionary.KeySlot);
                    ValidateContainerSlot(dictionary.ValueSlot);
                }
                else { ValidateContainerSlot(entry.Array?.ElementSlot ?? entry.List!.ElementSlot); }
                if (!_containers.Add(entry.Layout!)) { throw new InvalidDataException("A container representation cannot have more than one ID."); }
            }
            _nodes.Add(entry.Id, entry);
            _heights.Add(entry.Id, height);
            _next++;

            void AddDependency(DurableSchema dependency, SchemaKind kind) {
                RepresentationId id = RequireSchema(dependency, kind);
                height = Math.Max(height, 1 + _heights[id]);
                if (height > MaximumDepth) { throw new InvalidDataException("Exact layout exceeds the maximum depth."); }
            }

            void ValidateSlot(DurableFieldInfo field) {
                if (field.TargetType is { } target) { _declarations.ValidateDeclaration(target, SchemaKind.ReferenceObject); }
                if (field.ValueSchema is { } inline) { AddDependency(inline, SchemaKind.InlineValue); }
            }

            void ValidateContainerSlot(DurableFieldInfo field) {
                if (field.TargetType is { } target) { _declarations.ValidateDeclaration(target, SchemaKind.ReferenceObject); }
                if (field.ValueSchema is { } inline) { RequireSchema(inline, SchemaKind.InlineValue); }
            }
        }

    }

    private sealed class LimitedBufferWriter : IBufferWriter<byte> {
        private readonly ArrayBufferWriter<byte> _buffer = new();
        public void Advance(int count) { Check(count); _buffer.Advance(count); }
        // A writer can request a maximum-width span and advance only its actual encoding.
        // Enforce the payload limit on Advance, not on that temporary capacity hint.
        public Memory<byte> GetMemory(int sizeHint = 0) => _buffer.GetMemory(sizeHint);
        public Span<byte> GetSpan(int sizeHint = 0) => _buffer.GetSpan(sizeHint);
        private void Check(int size) {
            if (size < 0 || size > RbfFile.MaxPayloadAndMetaLength - _buffer.WrittenCount) {
                throw new ArgumentException("Schema catalog snapshot exceeds the maximum FrameStore payload length.");
            }
        }
        internal byte[] ToArray() => _buffer.WrittenSpan.ToArray();
    }
}
