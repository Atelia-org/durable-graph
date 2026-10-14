using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Tests;

public sealed class DictionaryBodyTests {
    private static readonly DictionaryLayout Layout = new(new(1, TypeTag.Int32), new(2, TypeTag.Int32));
    private static FrozenDictionaryState<int, int> State(params DictionaryEntryState<int, int>[] entries) =>
        new(DictionaryComparerKind.ScalarDefault, entries);

    [Fact]
    public void GoldenBaseAndThreeDeltaGroupsRoundtripWithoutMutatingPrior() {
        FrozenDictionaryState<int, int> prior = State(new(1, 10), new(2, 20));
        FrozenDictionaryState<int, int> current = State(new(3, 30), new(1, 11));
        PreparedBaseBody body = DictionaryStateBody<int, int, Int32StateOps, Int32StateOps>.PrepareBase(prior, Layout);
        Assert.Equal(new byte[] { 0, 2, 2, 20, 4, 40 }, body.Body.ToArray());
        PreparedDeltaBody delta = DictionaryStateBody<int, int, Int32StateOps, Int32StateOps>.PrepareDelta(prior, current, Layout);
        Assert.Equal(new byte[] { 1, 4, 1, 2, 22, 1, 6, 60 }, delta.Body.ToArray());
        Assert.True(delta.HasChanges);
        FrozenDictionaryState<int, int> applied = Apply(delta.Body.ToArray(), prior);
        Assert.Equal(new[] { new DictionaryEntryState<int, int>(1, 11), new(3, 30) }, applied.Entries.ToArray());
        Assert.Equal(new[] { new DictionaryEntryState<int, int>(1, 10), new(2, 20) }, prior.Entries.ToArray());
        Assert.False(DictionaryStateBody<int, int, Int32StateOps, Int32StateOps>.PrepareDelta(current, applied, Layout).HasChanges);
    }

    [Fact]
    public void Redundant_key_codewords_are_reencoded_for_delta_addressing_and_duplicate_checks() {
        // Comparer 0, redundant count 1, redundant ZigZag key 1, value 10.
        FrozenDictionaryState<int, int> prior = Read([0, 0x81, 0, 0x82, 0, 20]);
        Assert.Equal(State(new DictionaryEntryState<int, int>(1, 10)).Entries.ToArray(), prior.Entries.ToArray());
        Assert.Equal(new byte[] { 0, 1, 2, 20 },
            DictionaryStateBody<int, int, Int32StateOps, Int32StateOps>.PrepareBase(prior, Layout).Body.ToArray());

        // A redundant key addresses the same persisted semantic key for a value patch.
        FrozenDictionaryState<int, int> changed = Apply([0, 1, 0x82, 0, 22, 0], prior);
        Assert.Equal(State(new DictionaryEntryState<int, int>(1, 11)).Entries.ToArray(), changed.Entries.ToArray());
        Assert.Equal(State(new DictionaryEntryState<int, int>(1, 10)).Entries.ToArray(), prior.Entries.ToArray());
        Assert.Throws<InvalidDataException>(() => Read([0, 2, 2, 20, 0x82, 0, 22]));
    }

    [Fact]
    public void ReorderingIsNoChangeAndMatchedValuesUseExactlyOneFusedCall() {
        CountingOps.Calls = 0;
        PreparedDeltaBody delta = DictionaryStateBody<int, int, Int32StateOps, CountingOps>.PrepareDelta(
            State(new(1, 10), new(2, 20)), State(new(2, 20), new(1, 10)), Layout);
        Assert.False(delta.HasChanges);
        Assert.Equal(2, CountingOps.Calls);
        Assert.Equal(new byte[] { 0, 0, 0 }, delta.Body.ToArray());
        CountingOps.Calls = 0;
        delta = DictionaryStateBody<int, int, Int32StateOps, CountingOps>.PrepareDelta(
            State(new(1, 10), new(2, 20)), State(new(2, 21), new(3, 30)), Layout);
        Assert.True(delta.HasChanges);
        Assert.Equal(1, CountingOps.Calls);
    }

    [Fact]
    public void PreparationMetadataValidationDoesNotEncodeKeys() {
        StateValueBinding key = new(new(1, TypeTag.Int32), typeof(int), typeof(CountingKeyOps),
            typeof(int), typeof(IdentityValueProjection<int>));
        Assert.True(BuiltinStateValues.TryBindCurrent(typeof(int), out StateValueBinding value));
        DictionaryObjectBinding binding = DictionaryObjectBinding.Create(typeof(Dictionary<int, int>), Layout, key, value);
        ObjectStateRecord captured = binding.Capture(new(1), new Dictionary<int, int> { [1] = 10 }, new CaptureSession().BeginCapture());
        CountingKeyOps.Writes = 0;
        IStatePreparationBinding preparation = (IStatePreparationBinding)binding;
        preparation.Validate(captured);
        Assert.Equal(0, CountingKeyOps.Writes);
        preparation.PrepareBase(captured);
        Assert.True(CountingKeyOps.Writes > 0);
    }

    [Fact]
    public void SuccessiveCommitsDoNotDependOnCaptureEnumerationOrder() {
        FrozenDictionaryState<int, int>[] captures = [
            State(new(1, 1), new(2, 2), new(3, 3)),
            State(new(4, 4), new(3, 3), new(1, 1)),
            State(new(1, 9), new(4, 4), new(5, 5)),
            State(new(5, 10), new(1, 9)),
        ];
        FrozenDictionaryState<int, int> disk = captures[0];
        for (int index = 1; index < captures.Length; index++) {
            PreparedDeltaBody delta = DictionaryStateBody<int, int, Int32StateOps, Int32StateOps>.PrepareDelta(captures[index - 1], captures[index], Layout);
            disk = Apply(delta.Body.ToArray(), disk);
            Assert.False(DictionaryStateBody<int, int, Int32StateOps, Int32StateOps>.PrepareDelta(captures[index], disk, Layout).HasChanges);
        }
    }

    [Theory]
    [InlineData(new byte[] { 1, 6, 0, 0 })] // Missing Remove key.
    [InlineData(new byte[] { 0, 1, 6, 42, 0 })] // Missing Patch key.
    [InlineData(new byte[] { 0, 0, 1, 2, 42 })] // Existing Add key.
    [InlineData(new byte[] { 1, 2, 0, 1, 2, 42 })] // Same key across Remove/Add.
    [InlineData(new byte[] { 0, 2, 2, 42, 2, 44, 0 })] // Repeated Patch.
    [InlineData(new byte[] { 0, 1, 2, 20, 0 })] // Unchanged Patch.
    [InlineData(new byte[] { 0, 0, 2, 6, 60, 6, 62 })] // Repeated Add.
    [InlineData(new byte[] { 2, 2, 2, 0, 0 })] // Repeated Remove.
    [InlineData(new byte[] { 3, 2, 4, 6, 0, 0 })] // Removal count exceeds prior.
    [InlineData(new byte[] { 0, 0, 0, 0 })] // Trailing byte.
    [InlineData(new byte[] { 0, 0, 255, 255, 255, 255, 15 })] // Excessive result allocation.
    public void InvalidDeltaRejectsWithoutMutatingPrior(byte[] bytes) {
        FrozenDictionaryState<int, int> prior = State(new(1, 10), new(2, 20));
        Assert.Throws<InvalidDataException>(() => Apply(bytes, prior));
        Assert.Equal(State(new(1, 10), new(2, 20)).Entries.ToArray(), prior.Entries.ToArray());
    }

    [Theory]
    [InlineData(new byte[] { 9, 0 })]
    [InlineData(new byte[] { 1, 0 })] // String policy with integer layout.
    [InlineData(new byte[] { 0, 2, 2, 20, 2, 40 })]
    [InlineData(new byte[] { 0, 255, 255, 255, 255, 15 })]
    [InlineData(new byte[] { 0, 1 })]
    [InlineData(new byte[] { 0, 0, 0 })]
    public void InvalidBaseRejects(byte[] bytes) => Assert.Throws<InvalidDataException>(() => Read(bytes));

    [Fact]
    public void EveryTruncationOfPreparedBodiesRejects() {
        FrozenDictionaryState<int, int> prior = State(new(1, 10), new(2, 20));
        byte[] body = DictionaryStateBody<int, int, Int32StateOps, Int32StateOps>.PrepareBase(prior, Layout).Body.ToArray();
        byte[] delta = DictionaryStateBody<int, int, Int32StateOps, Int32StateOps>.PrepareDelta(prior, State(new(1, 11), new(3, 30)), Layout).Body.ToArray();
        for (int length = 0; length < body.Length; length++) {
            byte[] prefix = body[..length];
            Exception? error = Record.Exception(() => Read(prefix));
            Assert.True(error is EndOfStreamException or InvalidDataException,
                $"Base prefix of length {length} should reject as truncated or structurally impossible, got {error?.GetType().Name ?? "no error"}.");
        }
        for (int length = 0; length < delta.Length; length++) {
            byte[] prefix = delta[..length];
            Exception? error = Record.Exception(() => Apply(prefix, prior));
            Assert.True(error is EndOfStreamException or InvalidDataException,
                $"Delta prefix of length {length} should reject as truncated or structurally impossible, got {error?.GetType().Name ?? "no error"}.");
        }
    }

    [Fact]
    public void FrozenConstructorCopiesEntriesAndSinglePatchSizeDoesNotGrowWithUntouchedEntries() {
        DictionaryEntryState<int, int>[] input = [new(1, 10)];
        FrozenDictionaryState<int, int> state = new(DictionaryComparerKind.ScalarDefault, input);
        input[0] = new(2, 20);
        Assert.Equal(new(1, 10), state[0]);
        var many = Enumerable.Range(1, 1000).Select(key => new DictionaryEntryState<int, int>(key, 10)).ToArray();
        FrozenDictionaryState<int, int> prior = State(many);
        many[0] = new(1, 11);
        PreparedDeltaBody large = DictionaryStateBody<int, int, Int32StateOps, Int32StateOps>.PrepareDelta(prior, State(many), Layout);
        PreparedDeltaBody small = DictionaryStateBody<int, int, Int32StateOps, Int32StateOps>.PrepareDelta(state, State(new DictionaryEntryState<int, int>(1, 11)), Layout);
        Assert.Equal(small.Body.ToArray(), large.Body.ToArray());
    }

    [Theory]
    [MemberData(nameof(ArrayBodyTests.ScalarArrays), MemberType = typeof(ArrayBodyTests))]
    public void EveryScalarKeyCapturesAndRestoresThroughHistoricalReader(Array values) {
        Type keyType = values.GetType().GetElementType()!;
        Assert.True(BuiltinStateValues.TryBindCurrent(keyType, out StateValueBinding key));
        Assert.True(BuiltinStateValues.TryBindCurrent(typeof(int), out StateValueBinding value));
        Type domainType = typeof(Dictionary<,>).MakeGenericType(keyType, typeof(int));
        System.Collections.IDictionary domain = (System.Collections.IDictionary)Activator.CreateInstance(domainType)!;
        foreach (object entry in values) { if (!domain.Contains(entry)) { domain.Add(entry, domain.Count); } }
        DictionaryLayout layout = new(key.Slot, value.Slot);
        DictionaryObjectBinding binding = DictionaryObjectBinding.Create(domainType, layout, key, value);
        ObjectStateRecord captured = binding.Capture(new(1), domain, new CaptureSession().BeginCapture());
        IStatePreparationBinding preparation = (IStatePreparationBinding)binding;
        byte[] body = preparation.PrepareBase(captured).Body.ToArray();
        domain.Clear();
        ObjectStateRecord decoded = DictionaryStateReader.Create(layout, new(key.Slot, key.StateType, key.StateOpsType), value)
            .Read(new(1), new Bodies(body));
        System.Collections.IDictionary restored = (System.Collections.IDictionary)binding.Allocate(decoded);
        binding.Hydrate(restored, decoded, new ObjectReadTable(new Dictionary<ObjectId, object>()));
        Assert.True(restored.Count > 0);
        Assert.False(preparation.PrepareDelta(captured, decoded).HasChanges);
        foreach (object entry in values) { Assert.True(restored.Contains(entry)); }
    }

    private static FrozenDictionaryState<int, int> Apply(byte[] bytes, FrozenDictionaryState<int, int> prior) {
        BareValueReader reader = new(bytes);
        var result = DictionaryStateBody<int, int, Int32StateOps, Int32StateOps>.ApplyDelta(ref reader, prior, Layout);
        reader.EnsureFullyConsumed();
        return result;
    }

    private static FrozenDictionaryState<int, int> Read(byte[] bytes) {
        BareValueReader reader = new(bytes);
        var result = DictionaryStateBody<int, int, Int32StateOps, Int32StateOps>.ReadBase(ref reader, Layout);
        reader.EnsureFullyConsumed();
        return result;
    }

    private sealed class Bodies(params byte[][] bodies) : IStateBodySource {
        public int Count => bodies.Length;
        public ReadOnlySpan<byte> GetBody(int index) => bodies[index];
    }

    private readonly struct CountingOps : IStateOps<int> {
        internal static int Calls;
        public static bool StateEquals(in int left, in int right, DurableFieldInfo slot) => throw new InvalidOperationException("Writer must use fused PrepareDelta.");
        public static void WriteBase(ref BareValueWriter writer, in int value, DurableFieldInfo slot) => Int32StateOps.WriteBase(ref writer, in value, slot);
        public static int ReadBase(ref BareValueReader reader, DurableFieldInfo slot) => Int32StateOps.ReadBase(ref reader, slot);
        public static PreparedDeltaBody PrepareDelta(in int prior, in int current, DurableFieldInfo slot) {
            Calls++;
            return Int32StateOps.PrepareDelta(in prior, in current, slot);
        }
        public static int ApplyDelta(ref BareValueReader reader, in int prior, DurableFieldInfo slot) => Int32StateOps.ApplyDelta(ref reader, in prior, slot);
        public static void VisitReferences(in int value, IStateReferenceVisitor visitor, DurableFieldInfo slot) { }
    }

    private readonly struct CountingKeyOps : IStateOps<int> {
        internal static int Writes;
        public static bool StateEquals(in int left, in int right, DurableFieldInfo slot) => left == right;
        public static void WriteBase(ref BareValueWriter writer, in int value, DurableFieldInfo slot) {
            Writes++;
            Int32StateOps.WriteBase(ref writer, in value, slot);
        }
        public static int ReadBase(ref BareValueReader reader, DurableFieldInfo slot) => Int32StateOps.ReadBase(ref reader, slot);
        public static PreparedDeltaBody PrepareDelta(in int prior, in int current, DurableFieldInfo slot) => Int32StateOps.PrepareDelta(in prior, in current, slot);
        public static int ApplyDelta(ref BareValueReader reader, in int prior, DurableFieldInfo slot) => Int32StateOps.ApplyDelta(ref reader, in prior, slot);
        public static void VisitReferences(in int value, IStateReferenceVisitor visitor, DurableFieldInfo slot) { }
    }
}
