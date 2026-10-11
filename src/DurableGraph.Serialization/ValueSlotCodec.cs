using Atelia.Binary;
namespace Atelia.DurableGraph.Serialization;

internal delegate void ReadSlot<T>(ref BareValueReader reader, ref T value) where T : struct;
internal delegate void WriteSlot<T>(ref BareValueWriter writer, ref T value) where T : struct;

/// <summary>A caller-supplied value body and its typed array bindings.</summary>
internal abstract class ValueSlotCodec {
    internal abstract Type ValueType { get; }
    internal abstract ArrayElementCodec BindVector();
    internal abstract ArrayElementCodec BindArray2();
}

internal sealed class ValueSlotCodec<T> : ValueSlotCodec where T : struct {
    internal ValueSlotCodec(ReadSlot<T> read, WriteSlot<T> write) {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(write);
        Read = read;
        Write = write;
    }

    internal override Type ValueType => typeof(T);
    internal ReadSlot<T> Read { get; }
    internal WriteSlot<T> Write { get; }

    internal override ArrayElementCodec BindVector() => new VectorElementCodec<T>(this);
    internal override ArrayElementCodec BindArray2() => new Array2ElementCodec<T>(this);
}
