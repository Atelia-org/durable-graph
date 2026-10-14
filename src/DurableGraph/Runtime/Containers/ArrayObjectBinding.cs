using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Runtime;

/// <summary>Current array projection assembled once from closed element capabilities.</summary>
public abstract class ArrayObjectBinding : ObjectBinding {
    private protected ArrayObjectBinding(Type domainType, ArrayLayout layout, StateValueBinding element,
        Func<ArrayObjectBinding, ObjectStateRecord, ObjectStateRecord>? normalize)
        : base(domainType, ObjectLayout.ForArray(layout)) {
        ArrayLayout = layout;
        ElementBinding = element;
        NormalizeState = normalize;
    }

    public ArrayLayout ArrayLayout { get; }
    public StateValueBinding ElementBinding { get; }
    private protected Func<ArrayObjectBinding, ObjectStateRecord, ObjectStateRecord>? NormalizeState { get; }

    public static ArrayObjectBinding Create(Type domainArrayType, ArrayLayout layout, StateValueBinding element,
        Func<ArrayObjectBinding, ObjectStateRecord, ObjectStateRecord>? normalize = null) {
        ArgumentNullException.ThrowIfNull(domainArrayType);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(element);
        if (!domainArrayType.IsArray || domainArrayType.GetArrayRank() != layout.Rank ||
            domainArrayType.IsSZArray != layout.IsVector ||
            domainArrayType.GetElementType() != element.DomainType || element.ProjectionType is null ||
            layout.ElementSlot != StateBindingContext.WithFieldId(element.Slot, 1)) {
            throw new ArgumentException("The current array type and element binding must match the complete array layout.");
        }
        return (ArrayObjectBinding)Activator.CreateInstance(
            typeof(ArrayObjectBinding<,,,>).MakeGenericType(element.DomainType!, element.StateType,
                element.ProjectionType, element.StateOpsType), domainArrayType, layout, element, normalize)!;
    }

    internal abstract ObjectStateRecord CreateStateRecord(ObjectId id, object frozen);
}

internal sealed class ArrayObjectBinding<TDomain, TState, TProjection, TOps> : ArrayObjectBinding, IStatePreparationBinding
    where TState : unmanaged where TProjection : IValueProjection<TDomain, TState> where TOps : IStateOps<TState> {
    public ArrayObjectBinding(Type domainType, ArrayLayout layout, StateValueBinding element,
        Func<ArrayObjectBinding, ObjectStateRecord, ObjectStateRecord>? normalize)
        : base(domainType, layout, element, normalize) { }

    internal override ObjectStateRecord Capture(ObjectId id, object domain, CaptureContext context) {
        RequireDomain(domain);
        ArrayShape shape = ArrayShape.FromArray((Array)domain);
        TState[] elements = new TState[shape.Count];
        if (shape.Count == 0) {
            return CreateStateRecord(id, new FrozenArrayState<TState>(shape, elements, takeOwnership: true));
        }
        int index = 0;
        switch (shape.Rank) {
            case 1: {
                TDomain[] array = (TDomain[])domain;
                for (int i0 = 0; i0 < shape[0]; i0++) {
                    elements[index++] = TProjection.Capture(in array[i0], context, ArrayLayout.ElementSlot);
                }
                break;
            }
            case 2: {
                TDomain[,] array = (TDomain[,])domain;
                for (int i0 = 0; i0 < shape[0]; i0++) {
                    for (int i1 = 0; i1 < shape[1]; i1++) {
                        elements[index++] = TProjection.Capture(in array[i0, i1], context, ArrayLayout.ElementSlot);
                    }
                }
                break;
            }
            case 3: {
                TDomain[,,] array = (TDomain[,,])domain;
                for (int i0 = 0; i0 < shape[0]; i0++) {
                    for (int i1 = 0; i1 < shape[1]; i1++) {
                        for (int i2 = 0; i2 < shape[2]; i2++) {
                            elements[index++] = TProjection.Capture(in array[i0, i1, i2], context, ArrayLayout.ElementSlot);
                        }
                    }
                }
                break;
            }
            case 4: {
                TDomain[,,,] array = (TDomain[,,,])domain;
                for (int i0 = 0; i0 < shape[0]; i0++) {
                    for (int i1 = 0; i1 < shape[1]; i1++) {
                        for (int i2 = 0; i2 < shape[2]; i2++) {
                            for (int i3 = 0; i3 < shape[3]; i3++) {
                                elements[index++] = TProjection.Capture(in array[i0, i1, i2, i3], context, ArrayLayout.ElementSlot);
                            }
                        }
                    }
                }
                break;
            }
        }
        return CreateStateRecord(id, new FrozenArrayState<TState>(shape, elements, takeOwnership: true));
    }

    internal override ObjectStateRecord Normalize(ObjectStateRecord source) {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Kind != ObjectStateKind.Array || source.Layout.Type != CurrentLayout.Type) {
            throw new InvalidDataException("An array can only normalize within its nominal array type.");
        }
        if (NormalizeState is null && source.Layout.Equals(CurrentLayout)) {
            return CreateStateRecord(source.Id, source.GetArrayState<TState>());
        }
        if (NormalizeState is null) {
            throw new InvalidDataException("The historical array requires an explicit element upgrade capability.");
        }
        // Even an unchanged layout revalidates the snapshot's exact dependencies.
        ObjectStateRecord result = NormalizeState(this, source);
        ((IStatePreparationBinding)this).Validate(result);
        if (result.Id != source.Id || !result.GetArrayState<TState>().Shape.Equals(
                ((IFrozenArrayState)source.Content).Shape)) {
            throw new InvalidDataException("Array normalization must preserve object identity and shape.");
        }
        return result;
    }

    internal override ObjectStateRecord CreateStateRecord(ObjectId id, object frozen) {
        if (id.IsNull || frozen is not FrozenArrayState<TState> state || state.Shape.Rank != ArrayLayout.Rank) {
            throw new InvalidDataException("The array state does not match its target element representation and shape.");
        }
        return new(id, ArrayLayout, state, this);
    }

    internal override void VisitReferences(ObjectStateRecord current, IStateReferenceVisitor visitor) {
        ((IStatePreparationBinding)this).Validate(current);
        ArrayStateBody<TState, TOps>.VisitReferences(current.GetArrayState<TState>(), visitor, ArrayLayout);
    }

    internal override object Allocate(ObjectStateRecord current) {
        ((IStatePreparationBinding)this).Validate(current);
        ArrayShape shape = current.GetArrayState<TState>().Shape;
        return shape.Rank switch {
            1 => new TDomain[shape[0]],
            2 => new TDomain[shape[0], shape[1]],
            3 => new TDomain[shape[0], shape[1], shape[2]],
            4 => new TDomain[shape[0], shape[1], shape[2], shape[3]],
            _ => throw new InvalidDataException("Unsupported array rank."),
        };
    }

    internal override void Hydrate(object domain, ObjectStateRecord current, ObjectReadTable objects) {
        RequireDomain(domain);
        ((IStatePreparationBinding)this).Validate(current);
        FrozenArrayState<TState> state = current.GetArrayState<TState>();
        ArrayShape shape = state.Shape;
        if (!shape.Equals(ArrayShape.FromArray((Array)domain))) {
            throw new InvalidDataException("The allocated array does not match the frozen shape.");
        }
        if (shape.Count == 0) { return; }
        TState[] elements = state.OwnedElements;
        int index = 0;
        switch (shape.Rank) {
            case 1: {
                TDomain[] array = (TDomain[])domain;
                for (int i0 = 0; i0 < shape[0]; i0++) {
                    TProjection.Hydrate(ref array[i0], in elements[index++], objects, ArrayLayout.ElementSlot);
                }
                break;
            }
            case 2: {
                TDomain[,] array = (TDomain[,])domain;
                for (int i0 = 0; i0 < shape[0]; i0++) {
                    for (int i1 = 0; i1 < shape[1]; i1++) {
                        TProjection.Hydrate(ref array[i0, i1], in elements[index++], objects, ArrayLayout.ElementSlot);
                    }
                }
                break;
            }
            case 3: {
                TDomain[,,] array = (TDomain[,,])domain;
                for (int i0 = 0; i0 < shape[0]; i0++) {
                    for (int i1 = 0; i1 < shape[1]; i1++) {
                        for (int i2 = 0; i2 < shape[2]; i2++) {
                            TProjection.Hydrate(ref array[i0, i1, i2], in elements[index++], objects, ArrayLayout.ElementSlot);
                        }
                    }
                }
                break;
            }
            case 4: {
                TDomain[,,,] array = (TDomain[,,,])domain;
                for (int i0 = 0; i0 < shape[0]; i0++) {
                    for (int i1 = 0; i1 < shape[1]; i1++) {
                        for (int i2 = 0; i2 < shape[2]; i2++) {
                            for (int i3 = 0; i3 < shape[3]; i3++) {
                                TProjection.Hydrate(ref array[i0, i1, i2, i3], in elements[index++], objects, ArrayLayout.ElementSlot);
                            }
                        }
                    }
                }
                break;
            }
        }
    }

    void IStatePreparationBinding.Validate(ObjectStateRecord item) {
        if (!CurrentLayout.Equals(item.Layout) || item.GetArrayState<TState>().Shape.Rank != ArrayLayout.Rank) {
            throw new InvalidOperationException("The preparation binding requires its exact array layout and state.");
        }
    }

    bool IStatePreparationBinding.ProvesSameState(ObjectStateRecord left, ObjectStateRecord right) {
        ((IStatePreparationBinding)this).Validate(left);
        ((IStatePreparationBinding)this).Validate(right);
        FrozenArrayState<TState> leftState = left.GetArrayState<TState>();
        FrozenArrayState<TState> rightState = right.GetArrayState<TState>();
        if (!leftState.Shape.Equals(rightState.Shape)) { return false; }
        for (int index = 0; index < leftState.Shape.Count; index++) {
            if (!TOps.StateEquals(in leftState.Elements[index], in rightState.Elements[index], ArrayLayout.ElementSlot)) { return false; }
        }
        return true;
    }

    PreparedBaseBody IStatePreparationBinding.PrepareBase(ObjectStateRecord current) {
        ((IStatePreparationBinding)this).Validate(current);
        return ArrayStateBody<TState, TOps>.PrepareBase(current.GetArrayState<TState>(), ArrayLayout);
    }

    PreparedDeltaBody IStatePreparationBinding.PrepareDelta(ObjectStateRecord previous, ObjectStateRecord current) {
        ((IStatePreparationBinding)this).Validate(previous);
        ((IStatePreparationBinding)this).Validate(current);
        return ArrayStateBody<TState, TOps>.PrepareDelta(previous.GetArrayState<TState>(), current.GetArrayState<TState>(), ArrayLayout);
    }

    private void RequireDomain(object domain) {
        if (domain is null || domain.GetType() != DomainType) {
            throw new InvalidDataException("Array projection requires the exact registered CLR array type.");
        }
    }
}
