using Atelia.Binary;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Runtime;

/// <summary>Supplies one unpacked Base body followed by raw Delta bodies in application order.</summary>
/// <remarks>The caller keeps the source and its buffers stable for the entire synchronous read.</remarks>
internal interface IStateBodySource {
    int Count { get; }
    ReadOnlySpan<byte> GetBody(int index);
}

public delegate TState StateBaseReader<TState>(ref BareValueReader reader) where TState : unmanaged;
public delegate TState StateDeltaApplier<TState>(ref BareValueReader reader, in TState prior) where TState : unmanaged;

/// <summary>One exact Schema and its typed body reader, erased only at the object boundary.</summary>
/// <remarks>Reading does not create a capture candidate or install a session baseline.</remarks>
public abstract class StateReaderBinding : ObjectReaderBinding {
    private protected StateReaderBinding(DurableSchema schema) : base(ObjectLayout.ForDurable(schema)) {
        ArgumentNullException.ThrowIfNull(schema);
        schema.RequireReferenceObject();
        Schema = schema;
    }

    public DurableSchema Schema { get; }
}

/// <summary>Reconstructs one exact-version unmanaged DTO before boxing the completed value once.</summary>
/// <remarks>Callbacks read bodies or validate references; they must not publish partial loading state.</remarks>
public sealed class StateReaderBinding<TState> : StateReaderBinding where TState : unmanaged {
    public override Type StateType => typeof(TState);
    private readonly StateBaseReader<TState> _readBase;
    private readonly StateDeltaApplier<TState> _applyDelta;
    private readonly StateReferenceVisitor<TState> _visitReferences;

    public StateReaderBinding(
        DurableSchema schema,
        StateBaseReader<TState> readBase,
        StateDeltaApplier<TState> applyDelta,
        StateReferenceVisitor<TState> visitReferences) : base(schema) {
        ArgumentNullException.ThrowIfNull(readBase);
        ArgumentNullException.ThrowIfNull(applyDelta);
        ArgumentNullException.ThrowIfNull(visitReferences);
        _readBase = readBase;
        _applyDelta = applyDelta;
        _visitReferences = visitReferences;
    }

    internal override ObjectStateRecord Read(ObjectId objectId, IStateBodySource source) {
        if (objectId.IsNull) {
            throw new InvalidDataException("A durable object ID must be nonzero.");
        }
        TState state = StateBodyDecoder.Read(source, _readBase, _applyDelta);
        return new ObjectStateRecord(objectId, Schema, state);
    }

    internal override void VisitReferences(ObjectStateRecord item, IStateReferenceVisitor visitor) {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(visitor);
        if (item.Kind != ObjectStateKind.Durable || !Schema.Equals(item.Schema)) {
            throw new InvalidDataException("The object does not match this reader's exact Schema.");
        }
        TState state;
        try {
            state = item.GetState<TState>();
        } catch (InvalidOperationException error) {
            throw new InvalidDataException("The object does not contain this reader's exact DTO type.", error);
        }
        _visitReferences(in state, visitor);
    }
}

internal static class StateBodyDecoder {
    internal static TState Read<TState>(
        IStateBodySource source,
        StateBaseReader<TState> readBase,
        StateDeltaApplier<TState> applyDelta) where TState : unmanaged {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(readBase);
        ArgumentNullException.ThrowIfNull(applyDelta);
        int count = source.Count;
        if (count <= 0) {
            throw new InvalidDataException("An object body source requires a Base.");
        }
        BareValueReader reader = new(source.GetBody(0));
        TState state = readBase(ref reader);
        reader.EnsureFullyConsumed();
        for (int index = 1; index < count; index++) {
            reader = new(source.GetBody(index));
            state = applyDelta(ref reader, in state);
            reader.EnsureFullyConsumed();
        }
        return state;
    }
}
