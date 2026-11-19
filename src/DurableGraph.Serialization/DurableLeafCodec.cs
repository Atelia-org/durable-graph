using Atelia.Binary;

namespace Atelia.DurableGraph.Serialization;

/// <summary>Declares the stable nominal identity and current body format of a reference-free leaf.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class DurableLeafAttribute(string id, int version = 1) : Attribute {
    public string Id { get; } = id;
    public int Version { get; } = version;
}

/// <summary>Issued to leaves frozen for one advancing graph. Only successful graph installation sets this flag.</summary>
public abstract class LeafAcceptance {
    public abstract bool IsAccepted { get; }
}

/// <summary>A codec for one exact CLR leaf. Frozen values must own or share immutable storage and hold no graph edges.</summary>
/// <remarks>All readers produce the current frozen representation. Restore returns a fresh domain instance.
/// Change the declared format version when stored body semantics change. Keep the old reader to open that history.</remarks>
public abstract class DurableLeafCodec<TDomain, TFrozen> where TDomain : class where TFrozen : notnull {
    public virtual IReadOnlyList<int> HistoricalVersions => [];
    public abstract TFrozen Capture(TDomain value, LeafAcceptance? acceptance);
    public abstract TDomain Restore(TFrozen state);
    public abstract PreparedBaseBody PrepareBase(TFrozen state);
    /// <summary>Returns a change decision with Delta bytes, or null to require a Base for this pair.</summary>
    public virtual PreparedDeltaBody? PrepareDelta(TFrozen previous, TFrozen current) => null;
    public abstract TFrozen ReadBase(int version, ref BareValueReader reader);
    public virtual TFrozen ApplyDelta(int version, ref BareValueReader reader, TFrozen previous) =>
        throw new InvalidDataException("This leaf codec does not accept Delta bodies.");
    public virtual TFrozen FinishReplay(TFrozen state) => state;
    public virtual long? EstimateBaseSize(TFrozen state) => null;
}

/// <summary>Allows a codec package to register a private frozen type with an application's model catalog.</summary>
public interface IDurableLeafRegistration {
    void RegisterLeaf<TDomain, TFrozen>(DurableLeafCodec<TDomain, TFrozen> codec) where TDomain : class where TFrozen : notnull;
}
