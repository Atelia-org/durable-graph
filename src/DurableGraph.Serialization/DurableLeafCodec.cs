using Atelia.Binary;
using System.Text;

namespace Atelia.DurableGraph.Serialization;

/// <summary>Declares the persistent identity and current body format of a codec-owned graph leaf.</summary>
/// <remarks>
/// Apply to a public, top-level, sealed, non-generic class that does not implement <c>IDurableObject</c>.
/// Domain objects and containers can reference the leaf by identity. Its contents have no outgoing graph references.
/// Reserve the ID across ordinary models, leaf codecs and all versions of the application.
/// A store identifies the family by its nominal ID. An unrelated codec reusing an ID and version
/// can interpret another family's bytes when the declared layouts agree.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class DurableLeafAttribute : Attribute {
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Declares an ID and the version emitted by the current writer.</summary>
    /// <param name="id">A stable nonblank Unicode identifier, unique in the persistent model catalog.</param>
    /// <param name="version">A positive version. Advance it when changing the interpretation of stored bytes.</param>
    /// <exception cref="ArgumentNullException">The identifier is null.</exception>
    /// <exception cref="ArgumentException">The identifier is blank or contains an unpaired surrogate.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The version is zero or negative.</exception>
    public DurableLeafAttribute(string id, int version = 1) {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);
        try {
            StrictUtf8.GetByteCount(id);
        } catch (EncoderFallbackException error) {
            throw new ArgumentException("A leaf identifier must be valid Unicode.", nameof(id), error);
        }
        Id = id;
        Version = version;
    }

    /// <summary>Gets the ordinal, stable identity of this leaf family.</summary>
    public string Id { get; }

    /// <summary>Gets the version used for newly written Base and Delta bodies.</summary>
    public int Version { get; }
}

/// <summary>A receipt for one advancing State capture, shared by its leaves.</summary>
/// <remarks>The receipt can outlive the capture. Retaining it does not retain the graph or repository.</remarks>
public abstract class LeafAcceptance {
    /// <summary>Gets whether this exact frozen capture has been installed as an accepted State.</summary>
    /// <remarks>
    /// Reading the flag has no side effects. It starts false and can become true once.
    /// A discarded or failed installation leaves it false, even if the State reached storage.
    /// A codec using it to cache a predecessor must still verify that its cached version
    /// matches the frozen predecessor supplied for each Delta.
    /// </remarks>
    public abstract bool IsAccepted { get; }
}

/// <summary>Captures, encodes and restores the contents of one nominal graph leaf.</summary>
/// <typeparam name="TDomain">The exact class marked with <see cref="DurableLeafAttribute"/>.</typeparam>
/// <typeparam name="TFrozen">An immutable representation of the full value. It may own managed storage.</typeparam>
/// <remarks>
/// The repository retains the registered codec. Its operations must have stable semantics across saves,
/// discarded candidates, independent reads and shared graph histories. Frozen values may be retained
/// and reused after a callback returns. Keep all previously supplied values intact.
/// Reject malformed or unsupported bytes during replay. Unconsumed bytes, a null frozen value,
/// a null restored object or a null Base body fail the operation, as do exceptions from a codec.
/// </remarks>
public abstract class DurableLeafCodec<TDomain, TFrozen> where TDomain : class where TFrozen : notnull {
    /// <summary>Gets the earlier body versions this codec can read in addition to the current version.</summary>
    /// <remarks>
    /// Registration snapshots this list. Supply distinct positive versions strictly below the current
    /// version. Each listed version must support its historical Base and every Delta that could follow it.
    /// The default is empty.
    /// </remarks>
    public virtual IReadOnlyList<int> HistoricalVersions => [];

    /// <summary>Freezes the full current value before the repository prepares any body.</summary>
    /// <param name="value">The live domain object. Later edits must leave the returned state intact.</param>
    /// <param name="acceptance">The receipt for an advancing State, or null for an independent Event or capture without State installation.</param>
    /// <returns>An owned immutable value, which may share immutable storage with the domain object.</returns>
    public abstract TFrozen Capture(TDomain value, LeafAcceptance? acceptance);

    /// <summary>Reconstructs a fresh, fully initialized domain object for one materialized graph.</summary>
    /// <param name="state">The completed frozen value, which may also back other graphs and future saves.</param>
    /// <returns>A fresh object of the exact domain type. The graph restores aliases to the same object ID.</returns>
    public abstract TDomain Restore(TFrozen state);

    /// <summary>Encodes a complete value in the current body version.</summary>
    /// <returns>An owned raw Base body. The framework supplies type and storage headers.</returns>
    public abstract PreparedBaseBody PrepareBase(TFrozen state);

    /// <summary>Compares the current value with the exact frozen predecessor for this object in this graph.</summary>
    /// <returns>
    /// Null requires a full Base for this pair. A <see cref="PreparedDeltaBody"/> with
    /// <see cref="PreparedDeltaBody.HasChanges"/> false certifies equal persistent content.
    /// A changed result supplies the raw Delta the planner may select in place of a Base.
    /// </returns>
    /// <remarks>The default requests a Base on every save of an existing leaf.</remarks>
    public virtual PreparedDeltaBody? PrepareDelta(TFrozen previous, TFrozen current) => null;

    /// <summary>Reads the first body of one stored chain into the current frozen representation.</summary>
    /// <param name="version">The exact stored body version selected by the graph's schema.</param>
    /// <param name="reader">A bounded reader over the entire raw Base. Consume it completely.</param>
    public abstract TFrozen ReadBase(int version, ref BareValueReader reader);

    /// <summary>Applies one stored Delta to its decoded predecessor, preserving that predecessor.</summary>
    /// <param name="version">The same stored body version as the Base of this chain.</param>
    /// <param name="reader">A bounded reader over the entire raw Delta. Consume it completely.</param>
    /// <param name="previous">The frozen value produced by the preceding Base or Delta.</param>
    /// <remarks>The default rejects every Delta with <see cref="InvalidDataException"/>.</remarks>
    public virtual TFrozen ApplyDelta(int version, ref BareValueReader reader, TFrozen previous) =>
        throw new InvalidDataException("This leaf codec does not accept Delta bodies.");

    /// <summary>Completes a decoded chain after its Base and all Deltas, including a Base with no Deltas.</summary>
    /// <returns>An equivalent frozen value suitable for restoration and for use as a future predecessor.</returns>
    /// <remarks>Override to compact replay-only storage. The default returns the input.</remarks>
    public virtual TFrozen FinishReplay(TFrozen state) => state;

    /// <summary>Estimates the raw Base byte length so the planner can defer encoding an existing leaf.</summary>
    /// <returns>A nonnegative estimated byte count, excluding headers, or null to encode the Base eagerly.</returns>
    /// <remarks>The planner may choose a Base even for unchanged content. Storage accounting uses the actual encoded length.</remarks>
    public virtual long? EstimateBaseSize(TFrozen state) => null;
}

/// <summary>Accepts leaf codecs when an application configures the model catalog for its repositories.</summary>
public interface IDurableLeafRegistration {
    /// <summary>Registers one codec with its current writer and all its declared exact readers.</summary>
    /// <typeparam name="TDomain">The public leaf class declared by the codec package.</typeparam>
    /// <typeparam name="TFrozen">The codec's frozen representation, which can be private to the package.</typeparam>
    /// <param name="codec">The stable instance retained by this registration. Registering it again is idempotent.</param>
    /// <remarks>
    /// Configure all required codecs before opening a repository that will read or write their leaves.
    /// Each CLR leaf type and nominal ID has one owner in the catalog.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The codec is null.</exception>
    /// <exception cref="ArgumentException">The CLR leaf declaration or list of historical versions is invalid.</exception>
    /// <exception cref="InvalidOperationException">A different codec or model owns the CLR type, nominal ID or reader.</exception>
    /// <exception cref="NotSupportedException">The nominal ID is reserved by the repository.</exception>
    void RegisterLeaf<TDomain, TFrozen>(DurableLeafCodec<TDomain, TFrozen> codec) where TDomain : class where TFrozen : notnull;
}
