using System.Buffers;
using Atelia.Binary;
using Atelia.DurableGraph.Schema;

namespace Atelia.DurableGraph.Runtime;

/// <summary>Writes the complete canonical bytes of one reference-free private value.</summary>
public delegate void DeclaredPrivateWrite<TValue>(ref BareValueWriter writer, TValue value);

/// <summary>Validates and reads one bounded private value, consuming the entire input.</summary>
public delegate TValue DeclaredPrivateRead<TValue>(ref BareValueReader reader);

/// <summary>An author's format for private data with no graph references. The framework freezes
/// its bytes at capture, frames their length and replaces the whole member in a Delta when changed.
/// The reader validates a bounded body and produces a fresh value for each materialized graph.</summary>
public sealed class DeclaredPrivateFormat<TValue> {
    private readonly DeclaredPrivateWrite<TValue> _write;
    private readonly DeclaredPrivateRead<TValue> _read;

    /// <summary>Defines a private body format and freezes the default used when a migration adds this member.</summary>
    /// <param name="formatId">A stable, nonblank Unicode name identifying the format at this member position across versions.</param>
    /// <param name="defaultValue">The value assigned when an accepted old layout has fewer trailing slots.</param>
    /// <param name="write">Encodes a complete value deterministically. Its output must be accepted by the reader.</param>
    /// <param name="read">Validates the entire body and produces a value independent of other materializations, or throws.</param>
    public DeclaredPrivateFormat(string formatId, TValue defaultValue, DeclaredPrivateWrite<TValue> write, DeclaredPrivateRead<TValue> read) {
        var shape = new DeclaredSlotShape(DeclaredSlotKind.Private, formatId: formatId);
        _write = write ?? throw new ArgumentNullException(nameof(write));
        _read = read ?? throw new ArgumentNullException(nameof(read));
        byte[] initial = Encode(defaultValue);
        Slot = new(shape, body => Decode(body), initial);
    }

    internal DeclaredSlot Slot { get; }

    internal byte[] Encode(TValue value) {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new BareValueWriter(buffer);
        _write(ref writer, value);
        byte[] bytes = buffer.WrittenSpan.ToArray();
        Decode(bytes);
        return bytes;
    }

    internal TValue Decode(ReadOnlySpan<byte> body) {
        var reader = new BareValueReader(body);
        TValue value = _read(ref reader);
        reader.EnsureFullyConsumed();
        return value;
    }
}
