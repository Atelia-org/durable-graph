using Atelia.Binary;
using System.Buffers;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Serialization;

namespace PackageConsumerProbe;

[DurableType("package.body-base", 1)]
public abstract partial class BinaryBase : IDurableObject {
    [DurableField(7)] private readonly int _count;
    [DurableField(1)] private readonly bool _enabled;
    internal static int BaseConstructorCalls;

    protected BinaryBase(bool enabled, int count) {
        BaseConstructorCalls++;
        _enabled = enabled;
        _count = count;
    }

    protected bool HasExpectedBase => _enabled && _count == -17;
}

[DurableType("package.body-leaf", 1)]
public sealed partial class Character : BinaryBase {
    [DurableField(1)] private long _total;
    [Transient] private int _sentinel = 41;
    private static int _constructorCalls;

    private Character(bool enabled, int count, long total, int sentinel) : base(enabled, count) {
        _constructorCalls++;
        _total = total;
        _sentinel = sentinel;
    }

    public static string ExerciseGeneratedState() {
        ScalarValues.Exercise();
        ReferenceCaptureExercise.Run();
        StringDecodingExercise.Run();
        ExercisePreparedDelta();
        ExerciseCapturePreparation();
        ReaderSink registered = new();
        __DurableState.RegisterReaders(registered);
        __DurableState.RegisterReaders(registered);
        if (registered.Readers.Count != 2 ||
            !ReferenceEquals(registered.Readers[0], registered.Readers[1]) ||
            !ReferenceEquals(registered.Readers[0].Schema, __DurableState.V1.Schema) ||
            !registered.Readers[0].Schema.Equals(Schema)) {
            throw new InvalidOperationException("Generated registration lost its stable exact Schema binding.");
        }
        Character source = new(true, -17, 42, 8);
        var captured = __DurableState.Capture(source);
        ExerciseGeneratedModel(source);
        source._total = 999;
        ArrayBufferWriter<byte> buffer = new();
        BareValueWriter writer = new(buffer);
        __DurableState.WriteBaseBody(ref writer, in captured);
        if (!buffer.WrittenSpan.SequenceEqual(new byte[] { 0x01, 0x21, 0x54 })) {
            throw new InvalidOperationException("Unexpected base-first binary body.");
        }

        // The result and prebuilt string helper arrive through the single runtime package reference.
        PreparedBaseBody prepared = __DurableState.PrepareBaseBody(in captured);
        byte[] externalCopy = prepared.Body.ToArray();
        Array.Clear(externalCopy);
        source._total = 1001;
        _ = __DurableState.PrepareBaseBody(in captured);
        if (!prepared.Body.SequenceEqual(new byte[] { 0x01, 0x21, 0x54 }) ||
            !StringPayloadCodec.PrepareBase("A").Body.SequenceEqual(new byte[] { 0x03, 0x41 }) ||
            !StringPayloadCodec.PrepareBase(string.Empty).Body.SequenceEqual(new byte[] { 0x00 })) {
            throw new InvalidOperationException("Packaged Base preparation lost canonical bytes or frozen content.");
        }

        BareValueReader reader = new(buffer.WrittenSpan);
        var restored = __DurableState.ReadBaseBodyV1(ref reader);
        reader.EnsureFullyConsumed();
        bool valid = restored.Segment0Field1 && restored.Segment0Field7 == -17 &&
            restored.Segment1Field1 == 42 && source._total == 1001 && source._sentinel == 8 &&
            source.HasExpectedBase && ReferenceEquals(__DurableState.V1.Schema, Schema);
        return $"DurableState:{Convert.ToHexString(buffer.WrittenSpan)}:{valid}:ReferenceCapture:True:StringDecoding:True:PreparedDeltaBody:True:PreparedBaseBody:True:CapturePreparation:True:GeneratedReaders:True:GeneratedModel:True:ReadonlyRestore:True";
    }

    private static void ExerciseGeneratedModel(Character source) {
        ModelSink sink = new();
        __DurableState.RegisterModel(sink);
        __DurableState.RegisterModel(sink);
        if (sink.Models.Count != 2 || !ReferenceEquals(sink.Models[0], sink.Models[1]) ||
            !ReferenceEquals(sink.Models[0], __DurableState.Model) ||
            sink.Models[0].DomainType != typeof(Character) || !sink.Models[0].CurrentSchema.Equals(Schema)) {
            throw new InvalidOperationException("Runtime-only model registration lost its stable binding.");
        }
        CaptureSession session = new();
        CaptureContext context = session.BeginCapture();
        ObjectId id = __DurableState.AddRoot(context, source);
        CapturedGraph graph = context.Seal();
        var state = __DurableState.Normalize(graph.Objects.Single(row => row.Id == id));
        int constructors = _constructorCalls, baseConstructors = BaseConstructorCalls;
        Character restored = __DurableState.Allocate();
        __DurableState.Hydrate(restored, in state, new ObjectReadTable(StringReadTable.Decode([]), new Dictionary<ObjectId, IDurableObject>()));
        if (ReferenceEquals(source, restored) || !restored.HasExpectedBase || restored._total != 42 ||
            restored._sentinel != 0 || _constructorCalls != constructors || BaseConstructorCalls != baseConstructors) {
            throw new InvalidOperationException("Generated allocation/hydration ran constructors or lost private readonly base fields.");
        }
        session.Discard(graph);
    }

    private sealed class ModelSink : IStateModelRegistration {
        internal List<StateModelBinding> Models { get; } = [];
        public void Register(StateModelBinding model) => Models.Add(model);
    }

    // This sink needs only the Runtime package. Persistence owns registry policy and persistence.
    private sealed class ReaderSink : IStateReaderRegistration {
        internal List<StateReaderBinding> Readers { get; } = [];
        public void Register(StateReaderBinding reader) => Readers.Add(reader);
    }
}

[DurableType("package.scalar-values", 1)]
public sealed partial class ScalarValues : IDurableObject {
    [DurableField(1)] private byte _byte = byte.MaxValue;
    [DurableField(2)] private sbyte _sbyte = sbyte.MinValue;
    [DurableField(3)] private short _short = -1;
    [DurableField(4)] private ushort _ushort = 128;
    [DurableField(5)] private uint _uint = 16384;
    [DurableField(6)] private ulong _ulong = ulong.MaxValue;
    [DurableField(7)] private char _char = '\uD800';
    [DurableField(8)] private Half _half = BitConverter.UInt16BitsToHalf(0x8000);
    [DurableField(9)] private float _single = BitConverter.UInt32BitsToSingle(0x7FC12345);
    [DurableField(10)] private double _double = BitConverter.UInt64BitsToDouble(0xFFF8123456789ABC);

    internal static void Exercise() {
        ScalarValues source = new();
        var captured = __DurableState.Capture(source);
        source._half = (Half)1;
        ArrayBufferWriter<byte> buffer = new();
        BareValueWriter writer = new(buffer);
        __DurableState.WriteBaseBody(ref writer, in captured);
        const string golden = "FF80018001808001FFFFFFFFFFFFFFFFFF0100D800804523C17FBC9A78563412F8FF";
        if (Convert.ToHexString(buffer.WrittenSpan) != golden) {
            throw new InvalidOperationException("Unexpected scalar DTO bytes.");
        }

        BareValueReader reader = new(buffer.WrittenSpan);
        var restored = __DurableState.ReadBaseBodyV1(ref reader);
        reader.EnsureFullyConsumed();
        if (restored.Segment0Field1 != byte.MaxValue || restored.Segment0Field2 != sbyte.MinValue ||
            restored.Segment0Field3 != -1 || restored.Segment0Field4 != 128 ||
            restored.Segment0Field5 != 16384 || restored.Segment0Field6 != ulong.MaxValue ||
            restored.Segment0Field7 != '\uD800' ||
            BitConverter.HalfToUInt16Bits(restored.Segment0Field8) != 0x8000 ||
            BitConverter.SingleToUInt32Bits(restored.Segment0Field9) != 0x7FC12345 ||
            BitConverter.DoubleToUInt64Bits(restored.Segment0Field10) != 0xFFF8123456789ABC ||
            !ReferenceEquals(__DurableState.V1.Schema, Schema)) {
            throw new InvalidOperationException("Packaged scalar DTO did not preserve field values and floating-point bits.");
        }
    }
}
