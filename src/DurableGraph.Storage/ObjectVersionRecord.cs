using Atelia.FrameStore;

namespace Atelia.DurableGraph.Storage;

/// <summary>An immutable, owned opaque Base or Delta body for one local ObjectId.</summary>
public sealed class ObjectVersionRecord {
    private readonly byte[] _body;

    private ObjectVersionRecord(
        uint objectId,
        ObjectVersionKind kind,
        FrameAddress? priorAddress,
        ReadOnlySpan<byte> body,
        int? encodedPayloadBytes) {
        if (objectId == 0) {
            throw new ArgumentOutOfRangeException(nameof(objectId), objectId, "ObjectId 0 is reserved.");
        }

        ObjectId = objectId;
        Kind = kind;
        PriorAddress = priorAddress;
        _body = body.ToArray();
        EncodedPayloadBytes = encodedPayloadBytes;
    }

    public uint ObjectId { get; }
    public ObjectVersionKind Kind { get; }
    public FrameAddress? PriorAddress { get; }

    /// <summary>The complete representation body, including an explicitly supplied empty body.</summary>
    public ReadOnlySpan<byte> Body => _body;

    // Only decoded records have a measured payload cost in their original Frame.
    // The local ObjectId key and shared Revision/map framing are excluded.
    internal int? EncodedPayloadBytes { get; }

    /// <summary>Copies the Base body. The input must remain stable during this call.</summary>
    public static ObjectVersionRecord CreateBase(uint objectId, ReadOnlySpan<byte> body) =>
        new(objectId, ObjectVersionKind.Base, null, body, null);

    /// <summary>Copies the Delta body and declares its exact prior Frame for the same ObjectId.</summary>
    public static ObjectVersionRecord CreateDelta(uint objectId, FrameAddress priorAddress, ReadOnlySpan<byte> body) {
        FrameAddressValidator.ValidateRequired(priorAddress, nameof(priorAddress));
        return new(objectId, ObjectVersionKind.Delta, priorAddress, body, null);
    }

    internal static ObjectVersionRecord FromDecoded(
        uint objectId,
        ObjectVersionKind kind,
        FrameAddress? priorAddress,
        ReadOnlySpan<byte> body,
        int encodedPayloadBytes) {
        if (kind == ObjectVersionKind.Delta && priorAddress is { } prior) {
            FrameAddressValidator.ValidateRequired(prior, nameof(priorAddress));
        } else if (kind != ObjectVersionKind.Base || priorAddress is not null) {
            throw new ArgumentException("A Base has no prior; a Delta requires a prior.", nameof(priorAddress));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(encodedPayloadBytes);
        return new(objectId, kind, priorAddress, body, encodedPayloadBytes);
    }
}
