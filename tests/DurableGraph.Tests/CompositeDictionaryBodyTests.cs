using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Tests;

public sealed class CompositeDictionaryBodyTests {
    private static readonly DurableSchema KeySchema = new("CompositeKey", 1, SchemaKind.InlineValue,
        new DurableFieldInfo(1, TypeTag.Int32), new DurableFieldInfo(2, TypeTag.Int32));
    private static readonly DictionaryLayout Layout = new(new(1, TypeTag.InlineValue, inlineSchema: KeySchema), new(2, TypeTag.Int32));

    [Theory]
    [InlineData(DictionaryComparerKind.CurrentDefault)]
    [InlineData(DictionaryComparerKind.Application)]
    public void CompositeBaseAndDeltaUseEveryPersistentKeyFieldWithoutBusinessEquality(DictionaryComparerKind mode) {
        FrozenDictionaryState<KeyState, int> prior = State(mode, new(new(1, 100), 10), new(new(2, 200), 20));
        FrozenDictionaryState<KeyState, int> current = State(mode, new(new(2, 200), 21), new(new(1, 101), 10));
        var body = DictionaryStateBody<KeyState, int, KeyOps, Int32StateOps>.PrepareBase(prior, Layout);
        Assert.Equal((byte)mode, body.Body[0]);
        BareValueReader reader = new(body.Body);
        var decoded = DictionaryStateBody<KeyState, int, KeyOps, Int32StateOps>.ReadBase(ref reader, Layout);
        reader.EnsureFullyConsumed();
        Assert.Equal(prior.Entries.ToArray(), decoded.Entries.ToArray());
        var delta = DictionaryStateBody<KeyState, int, KeyOps, Int32StateOps>.PrepareDelta(prior, current, Layout);
        // The changed Timestamp belongs to the key: Remove, one retained-value Patch, Add.
        Assert.Equal(new byte[] { 1, 2, 200, 1, 1, 4, 144, 3, 42, 1, 2, 202, 1, 20 }, delta.Body.ToArray());
        reader = new(delta.Body);
        var applied = DictionaryStateBody<KeyState, int, KeyOps, Int32StateOps>.ApplyDelta(ref reader, decoded, Layout);
        reader.EnsureFullyConsumed();
        Assert.Equal(mode, applied.ComparerKind);
        Assert.False(DictionaryStateBody<KeyState, int, KeyOps, Int32StateOps>.PrepareDelta(current, applied, Layout).HasChanges);
        Assert.Equal(100, prior[0].Key.Timestamp);
    }

    [Theory]
    [InlineData(DictionaryComparerKind.CurrentDefault)]
    [InlineData(DictionaryComparerKind.Application)]
    public void NewModesRetainCanonicalDuplicateValidationButDoNotResolveBusinessLookup(DictionaryComparerKind mode) {
        FrozenDictionaryState<KeyState, int> businessCollision = State(mode, new(new(1, 100), 10), new(new(1, 101), 20));
        DictionaryStateBody<KeyState, int, KeyOps, Int32StateOps>.ValidateLookupKeys(businessCollision, Layout,
            _ => throw new InvalidOperationException("No target lookup for a composite business key."));
        FrozenDictionaryState<KeyState, int> persistentCollision = State(mode, new(new(1, 100), 10), new(new(1, 100), 20));
        Assert.Throws<InvalidDataException>(() => DictionaryStateBody<KeyState, int, KeyOps, Int32StateOps>.ValidateLocal(persistentCollision, Layout));
    }

    [Fact]
    public void ApplicationFloatBodyPreservesSignedZerosWithoutStandardLookupCheck() {
        DictionaryLayout layout = new(new(1, TypeTag.Single), new(2, TypeTag.Int32));
        FrozenDictionaryState<float, int> state = new(DictionaryComparerKind.Application,
            new DictionaryEntryState<float, int>[] { new(0f, 1), new(BitConverter.Int32BitsToSingle(unchecked((int)0x80000000)), 2) });
        var body = DictionaryStateBody<float, int, SingleStateOps, Int32StateOps>.PrepareBase(state, layout);
        BareValueReader reader = new(body.Body);
        var decoded = DictionaryStateBody<float, int, SingleStateOps, Int32StateOps>.ReadBase(ref reader, layout);
        reader.EnsureFullyConsumed();
        Assert.Equal(0, BitConverter.SingleToInt32Bits(decoded[0].Key));
        Assert.Equal(unchecked((int)0x80000000), BitConverter.SingleToInt32Bits(decoded[1].Key));
        DictionaryStateBody<float, int, SingleStateOps, Int32StateOps>.ValidateLookupKeys(decoded, layout,
            _ => throw new InvalidOperationException("Application behavior is not consulted by the DTO layer."));
    }

    [Theory]
    [InlineData(DictionaryComparerKind.CurrentDefault)]
    [InlineData(DictionaryComparerKind.Application)]
    public void NewModesStillRejectRootNullAndNullableSlots(DictionaryComparerKind mode) {
        DictionaryLayout references = new(new(1, TypeTag.String), new(2, TypeTag.Int32));
        FrozenDictionaryState<ObjectId, int> nullRoot = new(mode,
            new DictionaryEntryState<ObjectId, int>[] { new(default, 1) });
        Assert.Throws<InvalidDataException>(() => DictionaryStateBody<ObjectId, int, StringIdStateOps, Int32StateOps>.ValidateLocal(nullRoot, references));
        Assert.Throws<InvalidDataException>(() => DictionaryKeyPolicy.RequireStoredPolicy(mode, DurableFieldInfo.Nullable(1, new(1, TypeTag.Int32))));
        Assert.Throws<ArgumentException>(() => DictionaryKeyPolicy.RequireCurrentKey(typeof(int?), DurableFieldInfo.Nullable(1, new(1, TypeTag.Int32))));
    }

    [Theory]
    [InlineData(DictionaryComparerKind.CurrentDefault)]
    [InlineData(DictionaryComparerKind.Application)]
    public void CompositeNullMemberIsNotMistakenForNullRoot(DictionaryComparerKind mode) {
        DurableSchema schema = new("ReferenceKey", 1, SchemaKind.InlineValue,
            new DurableFieldInfo(1, TypeTag.String), new DurableFieldInfo(2, TypeTag.Int32));
        DictionaryLayout layout = new(new(1, TypeTag.InlineValue, inlineSchema: schema), new(2, TypeTag.Int32));
        FrozenDictionaryState<ReferenceKeyState, int> state = new(mode,
            new DictionaryEntryState<ReferenceKeyState, int>[] { new(new(default, 1), 10) });
        var body = DictionaryStateBody<ReferenceKeyState, int, ReferenceKeyOps, Int32StateOps>.PrepareBase(state, layout);
        Assert.Equal(new byte[] { (byte)mode, 1, 0, 2, 20 }, body.Body.ToArray());
        DictionaryStateBody<ReferenceKeyState, int, ReferenceKeyOps, Int32StateOps>.ValidateLookupKeys(state, layout,
            _ => throw new InvalidOperationException("Only the separate reference visitor interprets component IDs."));
    }

    [Theory]
    [InlineData(DictionaryComparerKind.CurrentDefault)]
    [InlineData(DictionaryComparerKind.Application)]
    public void RecursiveZeroWidthKeyRejectsBaseAndDeltaCountsBeforeReadingEntries(DictionaryComparerKind mode) {
        DurableSchema empty = new("Empty", 1, SchemaKind.InlineValue);
        DurableSchema nested = new("NestedEmpty", 1, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.InlineValue, inlineSchema: empty));
        DictionaryLayout layout = new(new(1, TypeTag.InlineValue, inlineSchema: nested), new(2, TypeTag.InlineValue, inlineSchema: empty));
        EmptyOps.Reads = 0;
        InvalidDataException baseError = Assert.Throws<InvalidDataException>(() => ReadEmpty(new byte[] { (byte)mode, 2 }, layout));
        Assert.Contains("at most one", baseError.Message);
        Assert.Equal(0, EmptyOps.Reads);
        InvalidDataException deltaError = Assert.Throws<InvalidDataException>(() => ApplyEmpty(new byte[] { 0, 0, 2 }, new(mode, []), layout));
        Assert.Contains("at most one", deltaError.Message);
        Assert.Equal(0, EmptyOps.Reads);
        var one = ReadEmpty(new byte[] { (byte)mode, 1 }, layout);
        Assert.Equal(1, one.Count);
        var cleared = ApplyEmpty(new byte[] { 1, 0, 0 }, one, layout);
        Assert.Empty(cleared.Entries.ToArray());
        var added = ApplyEmpty(new byte[] { 0, 0, 1 }, cleared, layout);
        Assert.Equal(1, added.Count);
    }

    [Fact]
    public void DefaultCompositeCaptureHydrationAndRecaptureRetainModeAndFullState() {
        DictionaryObjectBinding binding = CreateBinding(() => throw new InvalidOperationException("Default must not resolve Application."));
        Dictionary<DomainKey, int> domain = new() { [new(1, 100)] = 10 };
        ObjectStateRecord row = binding.Capture(new(42), domain, new CaptureSession().BeginCapture());
        Assert.Equal(DictionaryComparerKind.CurrentDefault, row.GetDictionaryState<KeyState, int>().ComparerKind);
        var restored = (Dictionary<DomainKey, int>)binding.Allocate(row);
        binding.Hydrate(restored, row, new ObjectReadTable(new Dictionary<ObjectId, object>()));
        Assert.Equal(10, restored[new(1, 999)]);
        ObjectStateRecord recaptured = binding.Capture(new(42), restored, new CaptureSession().BeginCapture());
        Assert.Equal(DictionaryComparerKind.CurrentDefault, recaptured.GetDictionaryState<KeyState, int>().ComparerKind);
        Assert.False(((IStatePreparationBinding)binding).PrepareDelta(row, recaptured).HasChanges);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ApplicationPreflightRunsBeforeProjectionEvenForEmptyDictionary(bool hasEntry) {
        DictionaryObjectBinding binding = CreateBinding();
        Dictionary<DomainKey, int> domain = new(new DomainComparer());
        if (hasEntry) { domain.Add(new(1, 100), 10); }
        DomainProjection.Captures = 0;
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => binding.Capture(new(42), domain, new CaptureSession().BeginCapture()));
        Assert.Contains("42", error.Message);
        Assert.Contains(typeof(Dictionary<DomainKey, int>).ToString(), error.Message);
        Assert.Contains("Application", error.Message);
        Assert.Equal(0, DomainProjection.Captures);
    }

    [Fact]
    public void ApplicationConfigurationErrorsDoNotFallBackAndContainObjectContext() {
        foreach (Func<object?> factory in new Func<object?>[] {
            () => null, () => StringComparer.Ordinal, () => throw new ArgumentException("resolver marker")
        }) {
            DictionaryObjectBinding binding = CreateBinding(factory);
            ObjectStateRecord row = binding.CreateStateRecord(new(81), State(DictionaryComparerKind.Application));
            InvalidDataException error = Assert.Throws<InvalidDataException>(() => binding.Allocate(row));
            Assert.Contains("81", error.Message);
            Assert.Contains(typeof(Dictionary<DomainKey, int>).ToString(), error.Message);
        }
    }

    [Fact]
    public void ApplicationReturningDefaultRetainsModeThroughHydrationAndCapture() {
        int resolutions = 0;
        DictionaryObjectBinding binding = CreateBinding(() => { resolutions++; return EqualityComparer<DomainKey>.Default; });
        ObjectStateRecord source = binding.CreateStateRecord(new(9), State(DictionaryComparerKind.Application, new DictionaryEntryState<KeyState, int>(new(1, 100), 10)));
        // Neither DTO creation, normalization nor whole-graph validation calls current behavior.
        binding.Normalize(source);
        binding.ValidateLookupKeys(source, _ => throw new InvalidOperationException());
        Assert.Equal(0, resolutions);
        var domain = (Dictionary<DomainKey, int>)binding.Allocate(source);
        Assert.Equal(1, resolutions);
        binding.Hydrate(domain, source, new ObjectReadTable(new Dictionary<ObjectId, object>()));
        Assert.Equal(10, domain[new(1, 0)]);
        ObjectStateRecord captured = binding.Capture(new(9), domain, new CaptureSession().BeginCapture());
        Assert.Equal(DictionaryComparerKind.Application, captured.GetDictionaryState<KeyState, int>().ComparerKind);
        Assert.False(((IStatePreparationBinding)binding).PrepareDelta(source, captured).HasChanges);
    }

    [Fact]
    public void ApplicationReturningOrdinalIsNotReclassifiedAsStandardStringMode() {
        Assert.True(BuiltinStateValues.TryBindCurrent(typeof(string), out StateValueBinding key));
        Assert.True(BuiltinStateValues.TryBindCurrent(typeof(int), out StateValueBinding value));
        DictionaryObjectBinding binding = DictionaryObjectBinding.Create(typeof(Dictionary<string, int>), new(key.Slot, value.Slot), key, value,
            getApplicationComparer: () => StringComparer.Ordinal);
        FrozenDictionaryState<ObjectId, int> state = new(DictionaryComparerKind.Application, new DictionaryEntryState<ObjectId, int>[] { new(new(1), 10) });
        ObjectStateRecord source = binding.CreateStateRecord(new(9), state);
        var domain = (Dictionary<string, int>)binding.Allocate(source);
        binding.Hydrate(domain, source, new ObjectReadTable(new Dictionary<ObjectId, object> { [new(1)] = "one" }));
        using CaptureContext context = new CaptureSession().BeginCapture(new SingleDictionaryResolver(binding));
        DurableSchema rootSchema = new("DictionaryBodyRoot", 1, DurableFieldInfo.Reference(1, binding.CurrentLayout.Type));
        context.AddRoot<DictionaryRoot, ObjectId>(new(domain), rootSchema,
            (root, capture) => capture.CaptureObject(root.Items, binding.CurrentLayout.Type));
        ObjectStateRecord captured = Assert.Single(context.Seal().Objects, row => row.Kind == ObjectStateKind.Dictionary);
        Assert.Equal(DictionaryComparerKind.Application, captured.GetDictionaryState<ObjectId, int>().ComparerKind);
    }

    [Fact]
    public void BusinessCollisionIsRejectedAtHydrationWithoutOverwritingFirstEntry() {
        DictionaryObjectBinding binding = CreateBinding();
        ObjectStateRecord row = binding.CreateStateRecord(new(9), State(DictionaryComparerKind.CurrentDefault, new(new(1, 100), 10), new(new(1, 101), 20)));
        binding.Normalize(row);
        var allocated = (Dictionary<DomainKey, int>)binding.Allocate(row);
        Assert.Throws<InvalidDataException>(() => binding.Hydrate(allocated, row, new ObjectReadTable(new Dictionary<ObjectId, object>())));
        Assert.Single(allocated);
        Assert.Equal(10, allocated[new(1, 0)]);
    }

    private static FrozenDictionaryState<KeyState, int> State(DictionaryComparerKind mode, params DictionaryEntryState<KeyState, int>[] entries) => new(mode, entries);
    private static DictionaryObjectBinding CreateBinding(Func<object?>? comparer = null) {
        StateValueBinding key = new(Layout.KeySlot, typeof(KeyState), typeof(KeyOps), typeof(DomainKey), typeof(DomainProjection));
        Assert.True(BuiltinStateValues.TryBindCurrent(typeof(int), out StateValueBinding value));
        return DictionaryObjectBinding.Create(typeof(Dictionary<DomainKey, int>), Layout, key, value, getApplicationComparer: comparer);
    }
    private static FrozenDictionaryState<EmptyState, EmptyState> ReadEmpty(byte[] bytes, DictionaryLayout layout) {
        BareValueReader reader = new(bytes);
        var state = DictionaryStateBody<EmptyState, EmptyState, EmptyOps, EmptyOps>.ReadBase(ref reader, layout);
        reader.EnsureFullyConsumed();
        return state;
    }
    private static FrozenDictionaryState<EmptyState, EmptyState> ApplyEmpty(byte[] bytes, FrozenDictionaryState<EmptyState, EmptyState> prior, DictionaryLayout layout) {
        BareValueReader reader = new(bytes);
        var state = DictionaryStateBody<EmptyState, EmptyState, EmptyOps, EmptyOps>.ApplyDelta(ref reader, prior, layout);
        reader.EnsureFullyConsumed();
        return state;
    }

    private readonly record struct KeyState(int Tenant, int Timestamp);
    private sealed class DictionaryRoot(Dictionary<string, int> items) : IDurableObject {
        internal Dictionary<string, int> Items { get; } = items;
    }
    private sealed class SingleDictionaryResolver(DictionaryObjectBinding dictionary) : IStateModelResolver {
        public bool TryGetCurrentModel(Type domainType, out StateModelBinding? model) { model = null; return false; }
        public bool TryGetCurrentObjectBinding(Type domainType, out ObjectBinding? binding) {
            binding = domainType == dictionary.DomainType ? dictionary : domainType == typeof(string) ? StringObjectBinding.Instance : null;
            return binding is not null;
        }
    }
    private readonly struct DomainKey(int tenant, int timestamp) : IEquatable<DomainKey> {
        internal int Tenant { get; } = tenant;
        internal int Timestamp { get; } = timestamp;
        public bool Equals(DomainKey other) => Tenant == other.Tenant;
        public override bool Equals(object? other) => other is DomainKey key && Equals(key);
        public override int GetHashCode() => Tenant;
    }
    private sealed class DomainComparer : IEqualityComparer<DomainKey> {
        public bool Equals(DomainKey x, DomainKey y) => x.Equals(y);
        public int GetHashCode(DomainKey obj) => obj.GetHashCode();
    }
    private readonly struct DomainProjection : IValueProjection<DomainKey, KeyState> {
        internal static int Captures;
        public static KeyState Capture(in DomainKey value, CaptureContext context, DurableFieldInfo slot) { Captures++; return new(value.Tenant, value.Timestamp); }
        public static void Hydrate(ref DomainKey target, in KeyState state, ObjectReadTable objects, DurableFieldInfo slot) => target = new(state.Tenant, state.Timestamp);
    }
    private readonly struct KeyOps : IStateOps<KeyState> {
        public static bool StateEquals(in KeyState left, in KeyState right, DurableFieldInfo slot) => left == right;
        public static void WriteBase(ref BareValueWriter writer, in KeyState value, DurableFieldInfo slot) { writer.WriteVarInt32(value.Tenant); writer.WriteVarInt32(value.Timestamp); }
        public static KeyState ReadBase(ref BareValueReader reader, DurableFieldInfo slot) => new(reader.ReadVarInt32(), reader.ReadVarInt32());
        public static PreparedDeltaBody PrepareDelta(in KeyState prior, in KeyState current, DurableFieldInfo slot) => throw new InvalidOperationException("Dictionary keys do not have nested patches.");
        public static KeyState ApplyDelta(ref BareValueReader reader, in KeyState prior, DurableFieldInfo slot) => throw new InvalidOperationException("Dictionary keys do not have nested patches.");
        public static void VisitReferences(in KeyState state, IStateReferenceVisitor visitor, DurableFieldInfo slot) { }
    }
    private readonly record struct ReferenceKeyState(ObjectId Reference, int Tenant);
    private readonly struct ReferenceKeyOps : IStateOps<ReferenceKeyState> {
        public static bool StateEquals(in ReferenceKeyState left, in ReferenceKeyState right, DurableFieldInfo slot) => left == right;
        public static void WriteBase(ref BareValueWriter writer, in ReferenceKeyState state, DurableFieldInfo slot) { writer.WriteVarUInt32(state.Reference.Value); writer.WriteVarInt32(state.Tenant); }
        public static ReferenceKeyState ReadBase(ref BareValueReader reader, DurableFieldInfo slot) => new(new(reader.ReadVarUInt32()), reader.ReadVarInt32());
        public static PreparedDeltaBody PrepareDelta(in ReferenceKeyState prior, in ReferenceKeyState current, DurableFieldInfo slot) => throw new InvalidOperationException();
        public static ReferenceKeyState ApplyDelta(ref BareValueReader reader, in ReferenceKeyState prior, DurableFieldInfo slot) => throw new InvalidOperationException();
        public static void VisitReferences(in ReferenceKeyState state, IStateReferenceVisitor visitor, DurableFieldInfo slot) => visitor.VisitString(state.Reference);
    }
    private readonly struct EmptyState { }
    private readonly struct EmptyOps : IStateOps<EmptyState> {
        internal static int Reads;
        public static bool StateEquals(in EmptyState left, in EmptyState right, DurableFieldInfo slot) => true;
        public static void WriteBase(ref BareValueWriter writer, in EmptyState value, DurableFieldInfo slot) { }
        public static EmptyState ReadBase(ref BareValueReader reader, DurableFieldInfo slot) { Reads++; return default; }
        public static PreparedDeltaBody PrepareDelta(in EmptyState prior, in EmptyState current, DurableFieldInfo slot) => new(false, []);
        public static EmptyState ApplyDelta(ref BareValueReader reader, in EmptyState prior, DurableFieldInfo slot) => throw new InvalidDataException("Empty states cannot change.");
        public static void VisitReferences(in EmptyState state, IStateReferenceVisitor visitor, DurableFieldInfo slot) { }
    }
}
