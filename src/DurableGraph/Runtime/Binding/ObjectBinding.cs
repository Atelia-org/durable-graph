using Atelia.Binary;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Runtime;

/// <summary>Object-boundary operations for one current supported CLR reference type.</summary>
/// <remarks>Fields and array elements retain their statically bound value operations.</remarks>
public abstract class ObjectBinding {
    private protected ObjectBinding(Type domainType, ObjectLayout currentLayout) {
        ArgumentNullException.ThrowIfNull(domainType);
        ArgumentNullException.ThrowIfNull(currentLayout);
        if (!domainType.IsClass) { throw new ArgumentException("An object binding requires a reference type.", nameof(domainType)); }
        DomainType = domainType;
        CurrentLayout = currentLayout;
    }

    public Type DomainType { get; }
    public ObjectLayout CurrentLayout { get; }
    internal abstract ObjectStateRecord Capture(ObjectId id, object domain, CaptureContext context);
    internal abstract ObjectStateRecord Normalize(ObjectStateRecord source);
    internal abstract void VisitReferences(ObjectStateRecord current, IStateReferenceVisitor visitor);
    internal abstract object Allocate(ObjectStateRecord current);
    internal abstract void Hydrate(object domain, ObjectStateRecord current, ObjectReadTable objects);
}

/// <summary>Historical object body operations that do not require the current domain type.</summary>
public abstract class ObjectReaderBinding {
    private protected ObjectReaderBinding(ObjectLayout layout) {
        ArgumentNullException.ThrowIfNull(layout);
        Layout = layout;
    }
    public ObjectLayout Layout { get; }
    public abstract Type StateType { get; }
    internal abstract ObjectStateRecord Read(ObjectId objectId, IStateBodySource source);
    internal abstract void VisitReferences(ObjectStateRecord item, IStateReferenceVisitor visitor);
}

internal sealed class StringObjectBinding : ObjectBinding {
    internal static StringObjectBinding Instance { get; } = new();
    private StringObjectBinding() : base(typeof(string), ObjectLayout.String) { }
    internal override ObjectStateRecord Capture(ObjectId id, object domain, CaptureContext context) => new(id, (string)domain);
    internal override ObjectStateRecord Normalize(ObjectStateRecord source) {
        Require(source);
        return source;
    }
    internal override void VisitReferences(ObjectStateRecord current, IStateReferenceVisitor visitor) => Require(current);
    internal override object Allocate(ObjectStateRecord current) { Require(current); return current.StringContent; }
    internal override void Hydrate(object domain, ObjectStateRecord current, ObjectReadTable objects) {
        Require(current);
        if (!ReferenceEquals(domain, current.StringContent)) { throw new InvalidDataException("String restoration changed its decoded identity."); }
    }
    private static void Require(ObjectStateRecord state) {
        if (state.Kind != ObjectStateKind.String) { throw new InvalidDataException("Expected a string object state."); }
    }
}

internal sealed class StringObjectReader : ObjectReaderBinding {
    internal static StringObjectReader Instance { get; } = new();
    private StringObjectReader() : base(ObjectLayout.String) { }
    public override Type StateType => typeof(string);
    internal override ObjectStateRecord Read(ObjectId objectId, IStateBodySource source) {
        if (objectId.IsNull || source.Count != 1) { throw new InvalidDataException("A string requires a nonzero ID and exactly one Base body."); }
        BareValueReader reader = new(source.GetBody(0));
        string value = reader.ReadString();
        reader.EnsureFullyConsumed();
        return new(objectId, value);
    }
    internal override void VisitReferences(ObjectStateRecord item, IStateReferenceVisitor visitor) =>
        StringObjectBinding.Instance.VisitReferences(item, visitor);
}
