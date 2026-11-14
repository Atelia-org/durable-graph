using System.Buffers.Binary;
using Atelia.DurableGraph.Runtime;

namespace Atelia.DurableGraph;

/// <summary>A durable value locating one exact saved graph in the selected repository.</summary>
/// <remarks>
/// Capture and restoration preserve only this value; they do not load or validate its target.
/// References are issued by Repository.SaveGraph and resolved explicitly through Repository.OpenGraph.
/// Select the original repository: CommitId's probabilistic cross-repository misuse detection also
/// applies here. The default value is unbound and can be saved.
/// </remarks>
[DurableType("Atelia.DurableGraph.GraphRef", 1)]
public readonly partial struct GraphRef : IEquatable<GraphRef> {
    [DurableField(1)] private readonly uint _word0;
    [DurableField(2)] private readonly uint _word1;
    [DurableField(3)] private readonly uint _word2;

    // Only the repository's successful graph-save path issues application references.
    // A valid location alone does not authenticate its owner, role or publication.
    internal GraphRef(CommitId commit) {
        Span<byte> encoded = stackalloc byte[CommitId.EncodedSize];
        if (!commit.TryWrite(encoded)) {
            throw new ArgumentException("A graph reference requires an encodable commit ID.", nameof(commit));
        }
        _word0 = BinaryPrimitives.ReadUInt32LittleEndian(encoded);
        _word1 = BinaryPrimitives.ReadUInt32LittleEndian(encoded[4..]);
        _word2 = BinaryPrimitives.ReadUInt32LittleEndian(encoded[8..]);
    }

    /// <summary>The generated definition to register alongside the application's own definitions.</summary>
    public static StateDefinitionBinding Definition =>
        global::Atelia.DurableGraph.Generated.Family_4174656C69612E44757261626C6547726170682E4772617068526566.Definition;

    /// <summary>Whether this value contains a valid commit encoding; does not check target existence or role.</summary>
    /// <remarks>A bound encoding does not certify a saved graph or identify its owning repository.</remarks>
    public bool IsBound => TryGetCommitId(out _);

    internal CommitId CommitId => TryGetCommitId(out CommitId commit)
        ? commit : throw new InvalidOperationException("The graph reference is unbound or has an invalid commit encoding.");

    private bool TryGetCommitId(out CommitId commit) {
        Span<byte> encoded = stackalloc byte[CommitId.EncodedSize];
        BinaryPrimitives.WriteUInt32LittleEndian(encoded, _word0);
        BinaryPrimitives.WriteUInt32LittleEndian(encoded[4..], _word1);
        BinaryPrimitives.WriteUInt32LittleEndian(encoded[8..], _word2);
        return CommitId.TryRead(encoded, out commit);
    }

    public bool Equals(GraphRef other) => _word0 == other._word0 && _word1 == other._word1 && _word2 == other._word2;
    public override bool Equals(object? obj) => obj is GraphRef other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(_word0, _word1, _word2);
    public static bool operator ==(GraphRef left, GraphRef right) => left.Equals(right);
    public static bool operator !=(GraphRef left, GraphRef right) => !left.Equals(right);
}
