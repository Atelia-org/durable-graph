using System.Buffers.Binary;
using Atelia.DurableGraph.Runtime;

namespace Atelia.DurableGraph;

/// <summary>A durable value locating one exact Event root in the selected repository.</summary>
/// <remarks>
/// Capture and restoration of this value do not load or validate its target. Resolve it explicitly
/// through Repository.LoadEvent. The original repository must be selected; CommitId's probabilistic
/// cross-repository misuse detection also applies here. The default value is unbound and can be saved.
/// </remarks>
[DurableType("Atelia.DurableGraph.EventRef", 1)]
public readonly partial struct EventRef : IEquatable<EventRef> {
    [DurableField(1)] private readonly uint _word0;
    [DurableField(2)] private readonly uint _word1;
    [DurableField(3)] private readonly uint _word2;

    private EventRef(ReadOnlySpan<byte> encoded) {
        _word0 = BinaryPrimitives.ReadUInt32LittleEndian(encoded);
        _word1 = BinaryPrimitives.ReadUInt32LittleEndian(encoded[4..]);
        _word2 = BinaryPrimitives.ReadUInt32LittleEndian(encoded[8..]);
    }

    /// <summary>The generated definition to register alongside the application's own definitions.</summary>
    public static StateDefinitionBinding Definition =>
        global::Atelia.DurableGraph.Generated.Family_4174656C69612E44757261626C6547726170682E4576656E74526566.Definition;

    /// <summary>Creates a reference from an Event descriptor without reading its body.</summary>
    /// <remarks>The descriptor's role is checked; repository ownership and target content are not certified.</remarks>
    public static EventRef FromCommit(CommitInfo commit) {
        ArgumentNullException.ThrowIfNull(commit);
        if (commit.Kind != CommitKind.Event) {
            throw new ArgumentException("An Event reference requires an Event commit.", nameof(commit));
        }
        Span<byte> encoded = stackalloc byte[CommitId.EncodedSize];
        if (!commit.Id.TryWrite(encoded)) {
            throw new ArgumentException("An Event reference requires an encodable commit ID.", nameof(commit));
        }
        return new(encoded);
    }

    /// <summary>Whether this value contains a valid commit encoding; does not check target existence or role.</summary>
    /// <remarks>A bound encoding does not certify an Event or identify its owning repository.</remarks>
    public bool IsBound => TryGetCommitId(out _);

    /// <summary>Restores the exact commit location without accessing a repository.</summary>
    /// <remarks>The selected repository validates target existence and Event role when the reference is loaded.</remarks>
    /// <exception cref="InvalidOperationException">The reference is unbound or has an invalid encoding.</exception>
    public CommitId ToCommitId() => TryGetCommitId(out CommitId commit)
        ? commit : throw new InvalidOperationException("The Event reference is unbound or has an invalid commit encoding.");

    private bool TryGetCommitId(out CommitId commit) {
        Span<byte> encoded = stackalloc byte[CommitId.EncodedSize];
        BinaryPrimitives.WriteUInt32LittleEndian(encoded, _word0);
        BinaryPrimitives.WriteUInt32LittleEndian(encoded[4..], _word1);
        BinaryPrimitives.WriteUInt32LittleEndian(encoded[8..], _word2);
        return CommitId.TryRead(encoded, out commit);
    }

    public bool Equals(EventRef other) => _word0 == other._word0 && _word1 == other._word1 && _word2 == other._word2;
    public override bool Equals(object? obj) => obj is EventRef other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(_word0, _word1, _word2);
    public static bool operator ==(EventRef left, EventRef right) => left.Equals(right);
    public static bool operator !=(EventRef left, EventRef right) => !left.Equals(right);
}
