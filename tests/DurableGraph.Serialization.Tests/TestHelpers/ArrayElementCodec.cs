using Atelia.Binary;
namespace Atelia.DurableGraph.Serialization.Tests;

/// <summary>
/// Encodes only the elements of an existing array, without dimensions or an object header.
/// A failed read or write may leave a partial result; the caller owns publication and payload boundaries.
/// </summary>
internal abstract class ArrayElementCodec {
    internal abstract Type ArrayType { get; }
    internal abstract void ReadElements(ref BareValueReader reader, Array target);
    internal abstract void WriteElements(ref BareValueWriter writer, Array source);

    protected void ValidateArray(Array array, string parameterName) {
        ArgumentNullException.ThrowIfNull(array, parameterName);
        if (array.GetType() != ArrayType) {
            throw new ArgumentException($"Expected exact array type '{ArrayType}', but received '{array.GetType()}'.", parameterName);
        }
    }
}

internal sealed class VectorElementCodec<T>(ValueSlotCodec<T> slot) : ArrayElementCodec where T : struct {
    internal override Type ArrayType => typeof(T[]);

    internal override void ReadElements(ref BareValueReader reader, Array target) {
        ValidateArray(target, nameof(target));
        var values = (T[])target;
        for (int index = 0; index < values.Length; index++) {
            slot.Read(ref reader, ref values[index]);
        }
    }

    internal override void WriteElements(ref BareValueWriter writer, Array source) {
        ValidateArray(source, nameof(source));
        var values = (T[])source;
        for (int index = 0; index < values.Length; index++) {
            slot.Write(ref writer, ref values[index]);
        }
    }
}

internal sealed class Array2ElementCodec<T>(ValueSlotCodec<T> slot) : ArrayElementCodec where T : struct {
    internal override Type ArrayType => typeof(T[,]);

    internal override void ReadElements(ref BareValueReader reader, Array target) {
        ValidateArray(target, nameof(target));
        var values = (T[,])target;
        int length0 = values.GetLength(0);
        int length1 = values.GetLength(1);
        if (length0 == 0 || length1 == 0) {
            return;
        }

        int lower0 = values.GetLowerBound(0);
        int lower1 = values.GetLowerBound(1);
        for (int offset0 = 0; offset0 < length0; offset0++) {
            for (int offset1 = 0; offset1 < length1; offset1++) {
                slot.Read(ref reader, ref values[lower0 + offset0, lower1 + offset1]);
            }
        }
    }

    internal override void WriteElements(ref BareValueWriter writer, Array source) {
        ValidateArray(source, nameof(source));
        var values = (T[,])source;
        int length0 = values.GetLength(0);
        int length1 = values.GetLength(1);
        if (length0 == 0 || length1 == 0) {
            return;
        }

        int lower0 = values.GetLowerBound(0);
        int lower1 = values.GetLowerBound(1);
        for (int offset0 = 0; offset0 < length0; offset0++) {
            for (int offset1 = 0; offset1 < length1; offset1++) {
                slot.Write(ref writer, ref values[lower0 + offset0, lower1 + offset1]);
            }
        }
    }
}
