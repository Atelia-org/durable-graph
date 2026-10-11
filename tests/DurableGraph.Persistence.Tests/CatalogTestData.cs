using Atelia.DurableGraph.Schema;
using System.Buffers;
using Atelia.Binary;

namespace Atelia.DurableGraph.Persistence.Tests;

/// <summary>Unchecked fixture encoding for malformed complete catalog snapshots; never use the validating product writer to manufacture a negative case.</summary>
internal static class CatalogTestData {
    internal static SchemaCatalogEntry[] Schemas(IEnumerable<DurableSchema> schemas, uint firstId = 2) =>
        schemas.Select(schema => SchemaCatalogEntry.ForSchema(new(firstId++), schema)).ToArray();

    internal static byte[] Encode(IReadOnlyList<SchemaCatalogEntry> entries) {
        Dictionary<SchemaKey, RepresentationId> ids = new();
        foreach (SchemaCatalogEntry entry in entries) {
            if (entry.Schema is { } schema) { ids[new(schema.Type, schema.Version)] = entry.Id; }
        }
        ArrayBufferWriter<byte> buffer = new();
        BareValueWriter writer = new(buffer);
        writer.WriteByte(2);
        writer.WriteVarUInt32((uint)entries.Count);
        foreach (SchemaCatalogEntry entry in entries) {
            writer.WriteVarUInt32(entry.Id.Value);
            if (entry.Schema is { } schema) {
                writer.WriteByte((byte)schema.Kind);
                TypeExprWireCodec.Write(ref writer, schema.Type);
                writer.WriteVarUInt32((uint)schema.Version);
                writer.WriteVarUInt32(schema.BaseSchema is { } ancestor ? ids[new(ancestor.Type, ancestor.Version)].Value : 0);
                writer.WriteVarUInt32((uint)schema.Fields.Length);
                foreach (DurableFieldInfo field in schema.Fields) {
                    writer.WriteVarUInt32((uint)field.FieldId);
                    WriteSlot(ref writer, field, ids);
                }
            }
            else if (entry.Array is { } array) {
                writer.WriteByte(3);
                writer.WriteVarUInt32(array.CodecVersion);
                writer.WriteByte((byte)array.Constructor);
                WriteSlot(ref writer, array.ElementSlot, ids);
            }
            else if (entry.List is { } list) {
                writer.WriteByte(4);
                writer.WriteVarUInt32(list.CodecVersion);
                WriteSlot(ref writer, list.ElementSlot, ids);
            }
            else {
                writer.WriteByte(5);
                writer.WriteVarUInt32(entry.Dictionary!.CodecVersion);
                WriteSlot(ref writer, entry.Dictionary.KeySlot, ids);
                WriteSlot(ref writer, entry.Dictionary.ValueSlot, ids);
            }
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteSlot(ref BareValueWriter writer, DurableFieldInfo slot, Dictionary<SchemaKey, RepresentationId> ids) {
        writer.WriteByte((byte)slot.TypeTag);
        if (slot.TypeTag == TypeTag.Nullable) { WriteSlot(ref writer, slot.NullableLayout!.ElementSlot, ids); }
        else if (slot.TypeTag == TypeTag.ObjectReference) { TypeExprWireCodec.Write(ref writer, slot.TargetType!); }
        else if (slot.TypeTag == TypeTag.InlineValue) {
            writer.WriteVarUInt32(ids[new(slot.InlineSchema!.Type, slot.InlineSchema.Version)].Value);
        }
    }
}
