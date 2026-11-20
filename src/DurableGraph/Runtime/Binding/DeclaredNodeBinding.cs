using Atelia.Binary;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Runtime;

internal abstract class DeclaredNodeBinding(Type domain, DurableSchema schema) : RegisteredObjectBinding(domain, schema) {
    internal abstract Type? ReferenceAccessorType(int slot);
    internal abstract DeclaredNodeReader BindReader(DurableSchema source);
}

internal sealed class DeclaredNodeBinding<T>(DeclaredNode<T> node) : DeclaredNodeBinding(typeof(T), node.Schema) where T : class {
    internal override Type? ReferenceAccessorType(int slot) => node.ReferenceAccessorType(slot);
    internal override object Registration => node;
    internal override DeclaredNodeReader BindReader(DurableSchema source) => new(source, this, node.BindStored(source));

    internal override ObjectStateRecord Capture(ObjectId id, object domain, CaptureContext context) =>
        new(id, CurrentLayout, node.Capture((T)domain, context), this);

    internal override ObjectStateRecord Normalize(ObjectStateRecord source) {
        if (source.Schema is not { IsDeclaredNode: true } stored || source.Content is not DeclaredCell[] cells ||
            stored.DeclaredSlots.Length != cells.Length) {
            throw new InvalidDataException("The decoded node must match its stored slot layout.");
        }
        if (!CurrentLayout.Equals(source.Layout)) {
            node.ValidateStored(stored);
            cells = node.Normalize(cells);
        }
        return new(source.Id, CurrentLayout, cells, this);
    }

    private DeclaredCell[] CurrentCells(ObjectStateRecord state) =>
        CurrentLayout.Equals(state.Layout) && state.Content is DeclaredCell[] cells && cells.Length == node.Schema.DeclaredSlots.Length
            ? cells : throw new InvalidDataException($"Expected the current slots of node {CurrentLayout.Type}.");

    public override void Validate(ObjectStateRecord state) => CurrentCells(state);
    internal override void VisitReferences(ObjectStateRecord current, IStateReferenceVisitor visitor) =>
        node.VisitReferences(CurrentCells(current), visitor);
    internal override object Allocate(ObjectStateRecord current) {
        Validate(current);
        return node.Allocate();
    }
    internal override void Hydrate(object domain, ObjectStateRecord current, ObjectReadTable objects) =>
        node.Hydrate((T)domain, CurrentCells(current), objects);
    internal override long? Estimate(ObjectStateRecord state) => node.EstimateBaseSize(CurrentCells(state));
    public override PreparedBaseBody PrepareBase(ObjectStateRecord state) => node.PrepareBase(CurrentCells(state));
    internal override PreparedDeltaBody? TryPrepareDelta(ObjectStateRecord previous, ObjectStateRecord current) =>
        node.PrepareDelta(CurrentCells(previous), CurrentCells(current));
}

// One frozen model environment owns the reader of this exact persisted slot sequence.
internal sealed class DeclaredNodeReader(DurableSchema schema, DeclaredNodeBinding owner, DeclaredNodeReplay replay)
    : RegisteredReaderBinding(schema) {
    internal DeclaredNodeBinding Owner { get; } = owner;
    public override Type StateType => typeof(DeclaredCell[]);

    internal override ObjectStateRecord Read(ObjectId objectId, IStateBodySource source) {
        if (objectId.IsNull || source.Count <= 0) { throw new InvalidDataException("A declared node requires a nonzero ID and a Base."); }
        BareValueReader reader = new(source.GetBody(0));
        DeclaredCell[] cells = replay.ReadBase(ref reader);
        reader.EnsureFullyConsumed();
        for (int index = 1; index < source.Count; index++) {
            reader = new(source.GetBody(index));
            cells = replay.ApplyDelta(ref reader, cells);
            reader.EnsureFullyConsumed();
        }
        return new(objectId, Layout, cells);
    }

    internal override void VisitReferences(ObjectStateRecord item, IStateReferenceVisitor visitor) {
        if (!Layout.Equals(item.Layout) || item.Content is not DeclaredCell[] cells || cells.Length != Schema.DeclaredSlots.Length) {
            throw new InvalidDataException("The frozen node disagrees with its exact source layout.");
        }
        replay.VisitReferences(cells, visitor);
    }
}
