using Atelia.Binary;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Storage;

/// <summary>
/// Decodes the provisional v4 StateRevision's object-version records and
/// ObjectHeadMap membership representation, and immediately normalizes every
/// persisted reference to an absolute <see cref="FrameAddress"/>.
/// </summary>
internal static class StateRevisionWireReader {
    internal static StateRevision Read(
        ReadOnlySpan<byte> source) {
        BareValueReader reader = new(source);
        byte version = reader.ReadByte();
        if (version != StateRevisionWireFormat.Version) {
            throw new InvalidDataException(
                $"Unsupported State Revision wire version {version}.");
        }

        ObjectHeadMapKind kind = reader.ReadByte() switch {
            (byte)ObjectHeadMapKind.Base => ObjectHeadMapKind.Base,
            (byte)ObjectHeadMapKind.Delta => ObjectHeadMapKind.Delta,
            byte value => throw new InvalidDataException(
                $"Unknown ObjectHeadMap kind {value} in State Revision wire data."),
        };
        FrameAddress? parent = reader.ReadByte() switch {
            0 => null,
            1 => FrameAddressWireCodec.Read(ref reader),
            byte value => throw new InvalidDataException(
                $"Invalid parent-presence marker {value}."),
        };
        if (kind == ObjectHeadMapKind.Delta && parent is null) {
            throw new InvalidDataException(
                "An ObjectHeadMap Delta has no parent Revision address.");
        }

        ObjectVersionRecord[] localObjects = ReadLocalObjects(ref reader);
        StateRevision revision;
        try {
            revision = kind switch {
                ObjectHeadMapKind.Base => StateRevision.CreateObjectHeadMapBase(
                    parent,
                    localObjects,
                    ReadExternalObjectHeads(ref reader)),
                ObjectHeadMapKind.Delta when parent is { } parentAddress =>
                    StateRevision.CreateObjectHeadMapDelta(
                        parentAddress,
                        localObjects,
                        ReadObjectIds(ref reader)),
                _ => throw new InvalidDataException(
                    $"Unknown ObjectHeadMap kind {kind}."),
            };
        } catch (ArgumentException exception) {
            throw new InvalidDataException(
                "State Revision wire data violates the semantic model.",
                exception);
        }

        reader.EnsureFullyConsumed();

        return revision;
    }

    private static KeyValuePair<uint, FrameAddress>[] ReadExternalObjectHeads(
        ref BareValueReader reader) {
        int count = ReadCount(ref reader, minimumEntryBytes: 1 + FrameAddress.EncodedSize);
        KeyValuePair<uint, FrameAddress>[] entries = new KeyValuePair<uint, FrameAddress>[count];
        uint previous = 0;
        for (int index = 0; index < count; index++) {
            uint objectId = ReadNextObjectId(ref reader, previous);
            previous = objectId;
            FrameAddress address = FrameAddressWireCodec.Read(ref reader);
            entries[index] = new(objectId, address);
        }

        return entries;
    }

    private static uint[] ReadObjectIds(ref BareValueReader reader) {
        int count = ReadCount(ref reader, minimumEntryBytes: 1);
        uint[] objectIds = new uint[count];
        uint previous = 0;
        for (int index = 0; index < count; index++) {
            uint objectId = ReadNextObjectId(ref reader, previous);
            objectIds[index] = objectId;
            previous = objectId;
        }

        return objectIds;
    }

    private static ObjectVersionRecord[] ReadLocalObjects(ref BareValueReader reader) {
        int count = ReadCount(ref reader, minimumEntryBytes: 3);
        ObjectVersionRecord[] records = new ObjectVersionRecord[count];
        uint previous = 0;
        for (int index = 0; index < count; index++) {
            uint objectId = ReadNextObjectId(ref reader, previous);
            int payloadStart = reader.ConsumedCount;
            ObjectVersionKind kind = reader.ReadByte() switch {
                (byte)ObjectVersionKind.Base => ObjectVersionKind.Base,
                (byte)ObjectVersionKind.Delta => ObjectVersionKind.Delta,
                byte value => throw new InvalidDataException($"Unknown ObjectVersion kind {value}."),
            };
            FrameAddress? prior = kind == ObjectVersionKind.Delta
                ? FrameAddressWireCodec.Read(ref reader)
                : null;
            // ReadBytes validates bounded UInt32 length and remaining bounds before copying.
            ReadOnlySpan<byte> body = reader.ReadBytes();
            int encodedPayloadBytes = reader.ConsumedCount - payloadStart;
            records[index] = ObjectVersionRecord.FromDecoded(objectId, kind, prior, body, encodedPayloadBytes);
            previous = objectId;
        }

        return records;
    }

    private static int ReadCount(ref BareValueReader reader, int minimumEntryBytes) {
        uint encoded = reader.ReadVarUInt32();
        if (encoded > StateRevisionWireFormat.MaxCollectionCount) {
            throw new InvalidDataException(
                $"Collection count {encoded} exceeds the provisional limit " +
                $"{StateRevisionWireFormat.MaxCollectionCount}.");
        }

        if (encoded > (uint)(reader.RemainingCount / minimumEntryBytes)) {
            throw new InvalidDataException(
                $"Collection count {encoded} exceeds the remaining payload bounds.");
        }

        return checked((int)encoded);
    }

    private static uint ReadNextObjectId(
        ref BareValueReader reader,
        uint previous) {
        uint objectId = reader.ReadVarUInt32();
        if (objectId == 0) {
            throw new InvalidDataException("ObjectId 0 is reserved.");
        }

        if (objectId <= previous) {
            throw new InvalidDataException(
                "ObjectIds must be encoded in strictly ascending order.");
        }

        return objectId;
    }
}
