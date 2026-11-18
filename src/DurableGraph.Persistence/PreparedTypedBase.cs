using Atelia.DurableGraph.Runtime;

namespace Atelia.DurableGraph.Persistence;

/// <summary>Owns a complete typed Base or its checked header and the frozen candidate to encode.</summary>
internal sealed class PreparedTypedBase {
    private readonly byte[] _bytes;
    private readonly PreparedCapturedObject? _deferred;

    internal PreparedTypedBase(ReadOnlySpan<byte> encodedBody) {
        _bytes = encodedBody.ToArray();
        EstimatedBodyBytes = _bytes.Length;
    }

    internal PreparedTypedBase(ReadOnlySpan<byte> header, PreparedCapturedObject deferred) {
        _bytes = header.ToArray();
        _deferred = deferred;
        EstimatedBodyBytes = checked(header.Length + deferred.EstimateDeferredBaseSize());
    }

    internal long EstimatedBodyBytes { get; }

    // The planner invokes this once for each selected Base, before any append.
    internal ReadOnlySpan<byte> Encode() {
        if (_deferred is null) { return _bytes; }
        ReadOnlySpan<byte> raw = _deferred.BaseBody.Body;
        byte[] body = GC.AllocateUninitializedArray<byte>(checked(_bytes.Length + raw.Length));
        _bytes.CopyTo(body, 0);
        raw.CopyTo(body.AsSpan(_bytes.Length));
        return body;
    }
}
