using System.Text;

namespace Atelia.DurableGraph.Serialization;

/// <summary>Declares the persistent identity and current layout version of a node with registered members.</summary>
/// <remarks>
/// Apply to a public, top-level, sealed, non-generic class with no other durable type declaration and
/// no <c>IDurableObject</c> implementation. The application registers the ordered members before
/// opening a repository. A generated model can reference the node by its nominal ID.
/// Reserve the ID across ordinary models, registered leaves, declared nodes and all versions of the application.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class DurableNodeAttribute : Attribute {
    /// <summary>Declares the ID and version of the current member layout.</summary>
    /// <param name="id">A stable nonblank Unicode identifier, unique in the persistent model catalog.</param>
    /// <param name="version">A positive version. Advance it when changing the persistent layout or its meaning.</param>
    /// <exception cref="ArgumentNullException">The identifier is null.</exception>
    /// <exception cref="ArgumentException">The identifier is blank or contains an unpaired surrogate.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The version is zero or negative.</exception>
    public DurableNodeAttribute(string id, int version = 1) {
        RegisteredObjectIdentity.Validate(id, version);
        Id = id;
        Version = version;
    }

    /// <summary>Gets the ordinal, stable identity of this node family.</summary>
    public string Id { get; }

    /// <summary>Gets the version of the member layout used for newly written nodes.</summary>
    public int Version { get; }
}

internal static class RegisteredObjectIdentity {
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static void Validate(string id, int version) {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);
        try {
            StrictUtf8.GetByteCount(id);
        } catch (EncoderFallbackException error) {
            throw new ArgumentException("A persistent identifier must be valid Unicode.", nameof(id), error);
        }
    }
}
