using System.Buffers;
using Atelia.Binary;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Storage;

/// <summary>
/// Encodes the provisional v4 StateRevision's object-version records and
/// ObjectHeadMap membership representation.
/// </summary>
internal static class StateRevisionWireWriter {
    internal static long Measure(StateRevision revision) {
        ArgumentNullException.ThrowIfNull(revision);
        ValidateCollectionCounts(revision);
        long bytes = 3 + (revision.ParentRevisionAddress.HasValue ? FrameAddress.EncodedSize : 0)
            + BareValueEncoding.MeasureVarUInt32((uint)revision.LocalObjects.Count);
        foreach (ObjectVersionRecord record in revision.LocalObjects) {
            bytes = checked(bytes + BareValueEncoding.MeasureVarUInt32(record.ObjectId)
                + (record.PriorAddress is { } prior
                    ? ObjectVersionPayloadSize.GetDeltaPayloadBytes(record.Body.Length, prior)
                    : ObjectVersionPayloadSize.GetBasePayloadBytes(record.Body.Length)));
        }
        if (revision.ObjectHeadMapKind == ObjectHeadMapKind.Base) {
            bytes = checked(bytes + BareValueEncoding.MeasureVarUInt32((uint)revision.ExternalObjectHeads.Count));
            foreach (uint objectId in revision.ExternalObjectHeads.Keys) {
                bytes = checked(bytes + BareValueEncoding.MeasureVarUInt32(objectId) + FrameAddress.EncodedSize);
            }
        } else {
            bytes = checked(bytes + BareValueEncoding.MeasureVarUInt32((uint)revision.RemovedObjectIds.Count));
            foreach (uint objectId in revision.RemovedObjectIds) {
                bytes = checked(bytes + BareValueEncoding.MeasureVarUInt32(objectId));
            }
        }
        return bytes;
    }

    internal static void Write(
        IBufferWriter<byte> destination,
        StateRevision revision) {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(revision);
        ValidateCollectionCounts(revision);

        BareValueWriter writer = new(destination);
        writer.WriteByte(StateRevisionWireFormat.Version);
        writer.WriteByte((byte)revision.ObjectHeadMapKind);
        if (revision.ParentRevisionAddress is { } parent) {
            writer.WriteByte(1);
            FrameAddressWireCodec.Write(ref writer, parent);
        } else {
            writer.WriteByte(0);
        }

        WriteCount(ref writer, revision.LocalObjects.Count);
        foreach (ObjectVersionRecord record in revision.LocalObjects) {
            writer.WriteVarUInt32(record.ObjectId);
            ObjectVersionPayloadCodec.Write(ref writer, record.Kind, record.PriorAddress, record.Body);
        }
        switch (revision.ObjectHeadMapKind) {
            case ObjectHeadMapKind.Base:
                WriteCount(ref writer, revision.ExternalObjectHeads.Count);
                foreach ((uint objectId, FrameAddress address) in
                    revision.ExternalObjectHeads) {
                    writer.WriteVarUInt32(objectId);
                    FrameAddressWireCodec.Write(ref writer, address);
                }

                break;
            case ObjectHeadMapKind.Delta:
                WriteObjectIds(ref writer, revision.RemovedObjectIds);
                break;
            default:
                throw new InvalidDataException(
                    $"Unknown ObjectHeadMap kind {revision.ObjectHeadMapKind}.");
        }
    }

    private static void WriteObjectIds(
        ref BareValueWriter writer,
        IReadOnlyCollection<uint> objectIds) {
        WriteCount(ref writer, objectIds.Count);
        foreach (uint objectId in objectIds) {
            writer.WriteVarUInt32(objectId);
        }
    }

    private static void WriteCount(ref BareValueWriter writer, int count) {
        ValidateCount(count, "collection");
        writer.WriteVarUInt32((uint)count);
    }

    private static void ValidateCollectionCounts(StateRevision revision) {
        ValidateCount(revision.LocalObjects.Count, nameof(revision.LocalObjects));
        if (revision.ObjectHeadMapKind == ObjectHeadMapKind.Base) {
            ValidateCount(
                revision.ExternalObjectHeads.Count,
                nameof(revision.ExternalObjectHeads));
        } else {
            ValidateCount(
                revision.RemovedObjectIds.Count,
                nameof(revision.RemovedObjectIds));
        }
    }

    private static void ValidateCount(int count, string role) {
        if ((uint)count > StateRevisionWireFormat.MaxCollectionCount) {
            throw new InvalidDataException(
                $"{role} count {count} exceeds the provisional limit " +
                $"{StateRevisionWireFormat.MaxCollectionCount}.");
        }
    }
}
