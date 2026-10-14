using System.Buffers;
using Atelia.Binary;

namespace Atelia.DurableGraph.Serialization;

/// <summary>Prepares owned string content using Atelia.Binary's default lossless encoding.</summary>
public static class StringBodyPreparer {
    public static PreparedBaseBody PrepareBase(string value) {
        ArgumentNullException.ThrowIfNull(value);
        ArrayBufferWriter<byte> buffer = new();
        BareValueWriter writer = new(buffer);
        writer.WriteString(value);
        return new PreparedBaseBody(buffer.WrittenSpan);
    }
}
