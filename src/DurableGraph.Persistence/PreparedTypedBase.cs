using Atelia.DurableGraph.Runtime;

namespace Atelia.DurableGraph.Persistence;

/// <summary>Owns a complete typed Base or its checked header and the frozen candidate to encode.</summary>
internal sealed class PreparedTypedBase {
    private readonly byte[] _bytes;
    private readonly PreparedCapturedObject? _candidate;

    internal PreparedTypedBase(ReadOnlySpan<byte> encodedBody) {
        _bytes = encodedBody.ToArray();
        EstimatedBodyBytes = _bytes.Length;
    }

    // The State preparation owns the header. Neither it nor a prepared row mutates those bytes.
    internal PreparedTypedBase(byte[] header, PreparedCapturedObject candidate) {
        _bytes = header;
        _candidate = candidate;
        EstimatedBodyBytes = candidate.EagerBaseBody is { } eager
            ? EncodedLength(header.Length, eager.Body.Length)
            : checked(header.Length + candidate.EstimateDeferredBaseSize());
    }

    internal static int EncodedLength(int headerLength, int bodyLength) =>
        (long)headerLength + bodyLength <= Array.MaxLength ? headerLength + bodyLength
            : throw new InvalidDataException("The typed Base exceeds the supported CLR buffer length.");

    internal long EstimatedBodyBytes { get; }

    // The planner invokes this once for each selected Base, before any append.
    internal ReadOnlySpan<byte> Encode() {
        if (_candidate is null) { return _bytes; }
        ReadOnlySpan<byte> raw = _candidate.BaseBody.Body;
        byte[] body = GC.AllocateUninitializedArray<byte>(EncodedLength(_bytes.Length, raw.Length));
        _bytes.CopyTo(body, 0);
        raw.CopyTo(body.AsSpan(_bytes.Length));
        return body;
    }
}
