using Atelia.Binary;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Storage;

/// <summary>Measures object-record bytes, excluding ObjectId and shared Revision/map framing.</summary>
public static class ObjectVersionPayloadSize {
    /// <summary>Returns the exact kind, body-length prefix, and Base body byte count.</summary>
    public static long GetBasePayloadBytes(int bodyLength) {
        ArgumentOutOfRangeException.ThrowIfNegative(bodyLength);
        return checked(1L + BareValueEncoding.MeasureVarUInt32((uint)bodyLength) + bodyLength);
    }

    // A size used for selection can exceed the length of a physically encodable
    // body. Keep the estimate wide: choosing Delta does not encode that Base.
    internal static long EstimateBasePayloadBytes(long bodyLength) {
        ArgumentOutOfRangeException.ThrowIfNegative(bodyLength);
        if (bodyLength <= int.MaxValue) { return GetBasePayloadBytes((int)bodyLength); }
        return checked(1L + BareValueEncoding.MeasureVarUInt32(uint.MaxValue) + bodyLength);
    }

    /// <summary>Returns the exact Delta payload size, including its fixed-size prior address.</summary>
    /// <remarks>This is a byte count; it does not prove that the address exists or the complete frame fits.</remarks>
    public static long GetDeltaPayloadBytes(int bodyLength, FrameAddress prior) {
        FrameAddressValidator.ValidateRequired(prior, nameof(prior));
        return checked(GetBasePayloadBytes(bodyLength) + FrameAddress.EncodedSize);
    }
}
