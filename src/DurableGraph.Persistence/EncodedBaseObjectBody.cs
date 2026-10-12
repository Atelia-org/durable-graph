namespace Atelia.DurableGraph.Persistence;

/// <summary>An owned Base object body with its producer's actual type header already encoded.</summary>
internal sealed class EncodedBaseObjectBody {
    private readonly byte[] _body;

    internal EncodedBaseObjectBody(ReadOnlySpan<byte> body) {
        _body = body.ToArray();
    }

    internal ReadOnlySpan<byte> Body => _body;
}
