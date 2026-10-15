
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Buffers;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class CompositeDictionaryRepositoryTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"composite-dictionary-repository-{Guid.NewGuid():N}");
    private static readonly ReadAmplificationBaseBudgetParameters NoRebase = new(1000000, 1);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ApplicationRecipeCanReturnStandardComparerWithoutModeDriftAcrossReloadAndDelta(int recipe) {
        Dictionary<string, int> original = Numbers(new StringProxy(StringComparer.Ordinal));
        World<string> world = new() { First = original, Second = original };
        StateModelRegistry models = Models<string>();
        models.UseDictionaryComparer<string, int>(StringComparer.Ordinal);
        FrameAddress seed, unchanged, changed, final;
        using (StateSaveHarness repository = StateSaveHarness.CreateNew(_root, models)) {
            using StateSaveSession<World<string>> session = repository.Create(world);
            seed = session.Commit(NoRebase);
        }
        int calls = 0;
        StateModelRegistry current = Models<string>();
        current.UseDictionaryComparerResolver(type => {
            Assert.Equal(typeof(Dictionary<string, int>), type);
            calls++;
            return recipe switch {
                0 => EqualityComparer<string>.Default,
                1 => StringComparer.Ordinal,
                _ => new StringProxy(StringComparer.Ordinal),
            };
        });
        using (StateSaveHarness repository = StateSaveHarness.OpenExisting(_root, current)) {
            using StateSaveSession<World<string>> session = repository.Load<World<string>>();
            Assert.Equal(1, calls);
            Assert.Same(session.World.First, session.World.Second);
            unchanged = session.Commit(NoRebase);
            session.World.First!["key-070"] = 900;
            changed = session.Commit(NoRebase);
            Assert.Equal(1, calls);
        }
        using (StateSaveHarness repository = StateSaveHarness.OpenExisting(_root, current)) {
            using StateSaveSession<World<string>> session = repository.Load<World<string>>();
            Assert.Equal(2, calls); // A reopened repository resolves in its own fixed environment.
            Assert.Equal(900, session.World.First!["key-070"]);
            final = session.Commit(NoRebase);
        }
        using RepositoryResources file = OpenSchemas();
        GraphRecordStore states = file.GraphStore;
        foreach (FrameAddress address in new[] { seed, unchanged, changed, final }) {
            ObjectStateRecord map = Assert.Single(DecodedRevision.ReadStoredGraph(states, address, Models<string>()).Objects,
                row => row.Kind == ObjectStateKind.Dictionary);
            Assert.Equal(DictionaryComparerKind.Application, map.GetDictionaryState<ObjectId, int>().ComparerKind);
        }
        Assert.Empty(Records(states, unchanged));
        Assert.Equal(ObjectVersionKind.Delta, Assert.Single(Records(states, changed)).Kind);
        Assert.Empty(Records(states, final));
    }

    [Fact]
    public void DefaultCompositeKeyIgnoresTimestampForQueriesButPersistsItAndRetainsModeFour() {
        Dictionary<Key, int> values = Enumerable.Range(0, 100).ToDictionary(i => new Key(i, 1000 + i), i => 10000 + i);
        World<Key> world = new() { First = values };
        StateModelRegistry models = Models<Key>();
        models.UseDictionaryComparerResolver(_ => throw new InvalidOperationException("Default must not resolve Application."));
        FrameAddress seed, replaced, unchanged;
        using (StateSaveHarness repository = StateSaveHarness.CreateNew(_root, models)) {
            using StateSaveSession<World<Key>> session = repository.Create(world);
            seed = session.Commit(NoRebase);
            Assert.Equal(10007, values[new Key(7, -1)]);
            values.Remove(new Key(7, -1));
            values.Add(new Key(7, 7777), 10007);
            replaced = session.Commit(NoRebase);
        }
        using (StateSaveHarness repository = StateSaveHarness.OpenExisting(_root, models)) {
            using StateSaveSession<World<Key>> session = repository.Load<World<Key>>();
            Assert.Equal(10007, session.World.First![new Key(7, -1)]);
            Assert.Equal(7777, Assert.Single(session.World.First.Keys, key => key.Id == 7).Timestamp);
            unchanged = session.Commit(NoRebase);
        }
        using RepositoryResources file = OpenSchemas();
        GraphRecordStore states = file.GraphStore;
        foreach (FrameAddress address in new[] { seed, replaced, unchanged }) {
            ObjectStateRecord map = Assert.Single(DecodedRevision.ReadStoredGraph(states, address, Models<Key>()).Objects,
                row => row.Kind == ObjectStateKind.Dictionary);
            FrozenDictionaryState<KeyState, int> frozen = map.GetDictionaryState<KeyState, int>();
            Assert.Equal(DictionaryComparerKind.CurrentDefault, frozen.ComparerKind);
            Assert.Equal(address == seed ? 1007 : 7777, Assert.Single(frozen.Entries.ToArray(), entry => entry.Key.Id == 7).Key.Timestamp);
        }
        Assert.Equal(ObjectVersionKind.Delta, Assert.Single(Records(states, replaced)).Kind);
        Assert.Empty(Records(states, unchanged));
    }

    [Fact]
    public void TypedRegistrationWinsIsIdempotentAndDoesNotRequireSourceComparerIdentity() {
        StateModelRegistry models = Models<string>();
        IEqualityComparer<string> selected = StringComparer.OrdinalIgnoreCase;
        models.UseDictionaryComparer<string, int>(selected);
        models.UseDictionaryComparer<string, int>(selected);
        Assert.Throws<InvalidOperationException>(() => models.UseDictionaryComparer<string, int>(StringComparer.Ordinal));
        int resolverCalls = 0;
        Func<Type, object?> resolver = _ => { resolverCalls++; throw new InvalidOperationException("Exact selection must win."); };
        models.UseDictionaryComparerResolver(resolver);
        models.UseDictionaryComparerResolver(resolver);
        Assert.Throws<InvalidOperationException>(() => models.UseDictionaryComparerResolver(_ => null));
        World<string> world = new() {
            First = new(new StringProxy(StringComparer.Ordinal)) { ["MiXeD"] = 1 },
            Second = new(new StringProxy(StringComparer.OrdinalIgnoreCase)) { ["OTHER"] = 2 },
        };
        using (StateSaveHarness repository = StateSaveHarness.CreateNew(_root, models)) {
            using StateSaveSession<World<string>> session = repository.Create(world);
            session.Commit(NoRebase);
            Assert.False(world.First.ContainsKey("mixed")); // Save does not replace the source comparer.
        }
        using (StateSaveHarness repository = StateSaveHarness.OpenExisting(_root, models)) {
            using StateSaveSession<World<string>> session = repository.Load<World<string>>();
            Assert.Equal(1, session.World.First!["mixed"]);
            Assert.Equal(2, session.World.Second!["other"]);
        }
        Assert.Equal(0, resolverCalls);
    }

    [Fact]
    public void ResolverIsLazyAndCachedAcrossDistinctInstancesAndArrayListOccurrences() {
        StateModelRegistry models = Models<string>();
        int calls = 0;
        models.UseDictionaryComparerResolver(type => {
            Assert.Equal(typeof(Dictionary<string, int>), type);
            calls++;
            return StringComparer.Ordinal;
        });
        Dictionary<string, int> first = Numbers(new StringProxy(StringComparer.Ordinal));
        Dictionary<string, int> second = new(new StringProxy(StringComparer.Ordinal)) { ["other"] = 42 };
        World<string> world = new() { First = first, Second = second, Containers = [[first, second]] };
        using (StateSaveHarness repository = StateSaveHarness.CreateNew(_root, models)) {
            using StateSaveSession<World<string>> session = repository.Create(world);
            Assert.Equal(0, calls);
            session.Commit(NoRebase);
            session.Commit(NoRebase);
            first["key-070"]++;
            session.Commit(NoRebase);
            Assert.Equal(1, calls);
        }
        using (StateSaveHarness repository = StateSaveHarness.OpenExisting(_root, models)) {
            using StateSaveSession<World<string>> session = repository.Load<World<string>>();
            Assert.Equal(2, calls);
            Assert.Same(session.World.First, session.World.Containers![0][0]);
            Assert.Same(session.World.Second, session.World.Containers[0][1]);
            session.Commit(NoRebase);
            Assert.Equal(2, calls);
        }
    }

    [Fact]
    public void ConfigurationAddedAfterOpenCannotEnterItsFrozenEnvironmentUntilReopen() {
        StateModelRegistry models = Models<string>();
        World<string> world = new();
        using Repository repository = Repository.CreateNew(_root, models);
        FrameAddress seed;
        using (BranchCheckout session = repository.CreateSharedBranch("main", world, NoRebase)) {
            seed = session.StateRevisionAddress!.Value;
            session.CommitEvent(new World<string>(), NoRebase);
            models.UseDictionaryComparer<string, int>(StringComparer.Ordinal);
            world.First = new(new StringProxy(StringComparer.Ordinal));
            Assert.Throws<InvalidDataException>(() => session.CommitState(NoRebase));
            Assert.Equal(seed, session.StateRevisionAddress!.Value);
            Assert.False(repository.IsFaulted);
        }
        using (BranchCheckout session = repository.CheckoutShared("main")) {
            ((World<string>)session.State!).First = new(new StringProxy(StringComparer.Ordinal));
            Assert.Throws<InvalidDataException>(() => session.CommitState(NoRebase));
            Assert.Equal(seed, session.StateRevisionAddress!.Value);
        }
        repository.Dispose();
        using Repository reopened = Repository.OpenExisting(_root, models);
        using (BranchCheckout session = reopened.CheckoutShared("main")) {
            ((World<string>)session.State!).First = new(new StringProxy(StringComparer.Ordinal));
            session.CommitState(NoRebase);
            Assert.NotEqual(seed, session.StateRevisionAddress!.Value);
            Assert.Equal(CheckpointKind.State, session.Head.Kind);
        }
    }

    [Fact]
    public void StandardModesDoNotResolveApplicationEvenWhenConfigurationThrows() {
        StateModelRegistry models = Models<string>();
        models.UseDictionaryComparerResolver(_ => throw new InvalidOperationException("No Application object exists."));
        Dictionary<string, int> identity = new(ReferenceEqualityComparer.Instance) { [new string('a', 1)] = 3 };
        World<string> world = new() {
            First = new(StringComparer.Ordinal) { ["A"] = 1 },
            Second = new(StringComparer.OrdinalIgnoreCase) { ["B"] = 2 },
            Containers = [[identity]],
        };
        using (StateSaveHarness repository = StateSaveHarness.CreateNew(_root, models)) {
            using StateSaveSession<World<string>> session = repository.Create(world);
            session.Commit(NoRebase);
        }
        using StateSaveHarness cold = StateSaveHarness.OpenExisting(_root, models);
        using StateSaveSession<World<string>> loaded = cold.Load<World<string>>();
        Assert.False(loaded.World.First!.ContainsKey("a"));
        Assert.Equal(2, loaded.World.Second!["b"]);
        Assert.False(loaded.World.Containers![0][0].ContainsKey(new string('a', 1)));
        loaded.Commit(NoRebase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void InvalidResolverResultsCannotFallBackOrPublishEvenForEmptyMap(int failure) {
        StateModelRegistry models = Models<string>();
        models.UseDictionaryComparerResolver(_ => failure switch {
            0 => null,
            1 => EqualityComparer<int>.Default,
            _ => throw new ResolverFailure(),
        });
        World<string> world = new() { First = new(new StringProxy(StringComparer.Ordinal)) };
        using StateSaveHarness repository = StateSaveHarness.CreateNew(_root, models);
        using StateSaveSession<World<string>> session = repository.Create(world);
        Exception? error = Record.Exception(() => session.Commit(NoRebase));
        Assert.NotNull(error);
        Assert.Null(repository.HeadRevisionAddress);
        Assert.False(repository.IsFaulted);
        if (failure == 2) { Assert.Contains(nameof(ResolverFailure), error.ToString()); }
    }

    [Fact]
    public void EmptyApplicationRequiresRecipeAtAllocateButNotExactReadOrNormalization() {
        StateModelRegistry models = Models<string>();
        models.UseDictionaryComparer<string, int>(StringComparer.Ordinal);
        FrameAddress seed;
        ObjectId worldId;
        using (StateSaveHarness repository = StateSaveHarness.CreateNew(_root, models)) {
            using StateSaveSession<World<string>> session = repository.Create(new World<string> { First = new(new StringProxy(StringComparer.Ordinal)) });
            seed = session.Commit(NoRebase);
            worldId = repository.WorldId!.Value;
        }
        using RepositoryResources file = OpenSchemas();
        GraphRecordStore states = file.GraphStore;
        StateModelRegistry noRecipe = Models<string>();
        DecodedRevision exact = DecodedRevision.ReadStoredGraph(states, seed, noRecipe);
        Assert.Equal(2, exact.Objects.Count);
        Assert.Equal(0, Assert.Single(exact.Objects, row => row.Kind == ObjectStateKind.Dictionary).GetDictionaryState<ObjectId, int>().Count);
        NormalizedGraphState.Create(exact, exact.SourceModels!);
        Assert.Throws<InvalidDataException>(() => GraphReader.ReadRoot<World<string>>(exact, worldId, new(exact.SourceModels!)));
    }

    [Fact]
    public void ApplicationCanonicalDifferentKeysRemainReadableButCurrentLookupCollisionFailsLoad() {
        World<string> world = new() { First = new(new StringProxy(StringComparer.Ordinal)) { ["A"] = 1, ["a"] = 2 } };
        StateModelRegistry models = Models<string>();
        // Deliberately choose a different current recipe. Registration accepts this choice;
        // Save freezes the existing source entries without executing that recipe's equality.
        models.UseDictionaryComparer<string, int>(StringComparer.OrdinalIgnoreCase);
        FrameAddress seed;
        ObjectId worldId;
        using (StateSaveHarness repository = StateSaveHarness.CreateNew(_root, models)) {
            using StateSaveSession<World<string>> session = repository.Create(world);
            seed = session.Commit(NoRebase);
            worldId = repository.WorldId!.Value;
        }
        using RepositoryResources file = OpenSchemas();
        GraphRecordStore states = file.GraphStore;
        DecodedRevision exact = DecodedRevision.ReadStoredGraph(states, seed, models);
        Assert.Equal(2, Assert.Single(exact.Objects, row => row.Kind == ObjectStateKind.Dictionary).GetDictionaryState<ObjectId, int>().Count);
        NormalizedGraphState.Create(exact, exact.SourceModels!);
        Assert.Throws<InvalidDataException>(() => GraphReader.ReadRoot<World<string>>(exact, worldId, new(exact.SourceModels!)));
        Assert.Equal(4, DecodedRevision.ReadStoredGraph(states, seed, Models<string>()).Objects.Count);
    }

    [Fact]
    public void ApplicationFloatBitKeysPreserveSignedZeroAndDistinctNaNs() {
        double nan1 = BitConverter.Int64BitsToDouble(unchecked((long)0x7FF8000000000001UL));
        double nan2 = BitConverter.Int64BitsToDouble(unchecked((long)0x7FF8000000000002UL));
        BitComparer comparer = new();
        World<double> world = new() { First = new(comparer) { [0.0] = 1, [-0.0] = 2, [nan1] = 3, [nan2] = 4 } };
        StateModelRegistry models = Models<double>();
        models.UseDictionaryComparer<double, int>(comparer);
        using (StateSaveHarness repository = StateSaveHarness.CreateNew(_root, models)) {
            using StateSaveSession<World<double>> session = repository.Create(world);
            session.Commit(NoRebase);
        }
        using StateSaveHarness cold = StateSaveHarness.OpenExisting(_root, models);
        using StateSaveSession<World<double>> loaded = cold.Load<World<double>>();
        Assert.Equal(4, loaded.World.First!.Count);
        Assert.Equal(1, loaded.World.First[0.0]);
        Assert.Equal(2, loaded.World.First[-0.0]);
        Assert.Equal(3, loaded.World.First[nan1]);
        Assert.Equal(4, loaded.World.First[nan2]);
        loaded.Commit(NoRebase);
    }

    [Fact]
    public void ApplicationCannotHideCanonicalDuplicateKeysDistinguishedOnlyByTransientData() {
        TransientKeyComparer source = new();
        World<Key> world = new() { First = new(source) { [new Key(1, 2, 10)] = 1, [new Key(1, 2, 20)] = 2 } };
        StateModelRegistry models = Models<Key>();
        models.UseDictionaryComparer<Key, int>(EqualityComparer<Key>.Default);
        using StateSaveHarness repository = StateSaveHarness.CreateNew(_root, models);
        using StateSaveSession<World<Key>> session = repository.Create(world);
        Assert.Throws<InvalidDataException>(() => session.Commit(NoRebase));
        Assert.Null(repository.HeadRevisionAddress);
    }

    [Fact]
    public void ApplicationReferenceComparerCannotPreserveDistinctEmptyKeyInstances() {
        // Match the existing ReferenceCaptureSession fixture. If the runtime changes this
        // construction behavior, fail visibly rather than skipping the normalization witness.
        string first = "A".Replace("A", string.Empty);
        string second = "A".Replace("A", string.Empty);
        Assert.Equal(string.Empty, first);
        Assert.Equal(string.Empty, second);
        Assert.NotSame(string.Empty, first);
        Assert.NotSame(string.Empty, second);
        Assert.NotSame(first, second);
        Dictionary<string, int> values = new(new StringIdentityProxy()) { [first] = 1, [second] = 2 };
        Assert.Equal(2, values.Count);
        StateModelRegistry models = Models<string>();
        models.UseDictionaryComparer<string, int>(ReferenceEqualityComparer.Instance);
        using StateSaveHarness repository = StateSaveHarness.CreateNew(_root, models);
        using StateSaveSession<World<string>> session = repository.Create(new World<string> { First = values });
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => session.Commit(NoRebase));
        Assert.Contains("duplicate persistent keys", error.Message);
        Assert.Null(repository.HeadRevisionAddress);
        Assert.False(repository.IsFaulted);
        Assert.Equal(2, values.Count);
    }

    [Fact]
    public void UnreachableApplicationDoesNotResolveButItsCompleteReferencesStillValidate() {
        StateModelRegistry writerModels = Models<string>();
        writerModels.UseDictionaryComparer<string, int>(StringComparer.Ordinal);
        FrameAddress seed;
        ObjectId worldId;
        using (StateSaveHarness repository = StateSaveHarness.CreateNew(_root, writerModels)) {
            using StateSaveSession<World<string>> session = repository.Create(new World<string> {
                First = new(new StringProxy(StringComparer.Ordinal)) { ["key"] = 10 },
            });
            seed = session.Commit(NoRebase);
            worldId = repository.WorldId!.Value;
        }
        using RepositoryResources file = OpenSchemas();
        GraphRecordStore states = file.GraphStore;
        DecodedRevision initial = DecodedRevision.ReadStoredGraph(states, seed, Models<string>());
        ObjectStateRecord root = initial.GetRequired(worldId);
        ObjectId keyId = Assert.Single(initial.Objects, row => row.Kind == ObjectStateKind.String).Id;
        SchemaCatalogView selected = initial.SelectedSchemaView!;
        RootState detached = default;
        FrameAddress unreachable = states.Append(StoredGraphKind.State, seed, initial.SelectedSchemaView!.Snapshot!.Address,
            new(seed, worldId, [ObjectVersionRecord.CreateBase(worldId.Value,
                TypedBaseBodyCodec.Encode(root.Layout, RootBase(in detached), selected))])).Address;
        int calls = 0;
        StateModelRegistry readers = Models<string>();
        readers.UseDictionaryComparerResolver(_ => { calls++; throw new ResolverFailure(); });
        DecodedRevision exact = DecodedRevision.ReadStoredGraph(states, unreachable, readers);
        NormalizedGraphState.Create(exact, exact.SourceModels!);
        World<string> loaded = GraphReader.ReadRoot<World<string>>(exact, worldId, new(exact.SourceModels!));
        Assert.Null(loaded.First);
        Assert.Equal(0, calls);
        FrameAddress corrupt = states.Append(StoredGraphKind.State, unreachable, selected.Snapshot!.Address,
            new(unreachable, worldId, [], [keyId.Value])).Address;
        Assert.Throws<InvalidDataException>(() => DecodedRevision.ReadStoredGraph(states, corrupt, readers));
        Assert.Equal(0, calls);
    }

    private sealed class World<K> : IDurableObject where K : notnull {
        public Dictionary<K, int>? First;
        public Dictionary<K, int>? Second;
        public List<Dictionary<K, int>[]>? Containers;
    }
    private readonly record struct RootState(ObjectId First, ObjectId Second, ObjectId Containers);
    private static StateModelRegistry Models<K>() where K : notnull {
        StateModelRegistry models = new();
        if (typeof(K) == typeof(Key)) { models.Register(KeyDefinition()); }
        TypeExpr key = models.CreateEnvironment().GetTypeExpr(typeof(K));
        TypeExpr map = TypeExpr.Dictionary(key, TypeExpr.Builtin(TypeTag.Int32));
        TypeExpr containers = TypeExpr.List(TypeExpr.VectorArray(map));
        DurableSchema schema = new(TypeExpr.Named("CompositeConfigWorld", key), 1,
            DurableFieldInfo.Reference(1, map), DurableFieldInfo.Reference(2, map), DurableFieldInfo.Reference(3, containers));
        void Visit(in RootState state, IStateReferenceVisitor visitor) {
            visitor.VisitObject(state.First, map); visitor.VisitObject(state.Second, map); visitor.VisitObject(state.Containers, containers);
        }
        StatePreparationBinding<RootState> preparation = new(schema, RootBase, RootDelta);
        StateReaderBinding<RootState> reader = new(schema, ReadRoot, ApplyRoot, Visit);
        models.Register(new StateModelBinding<World<K>, RootState>(preparation, [reader], row => row.GetState<RootState>(),
            static () => new(), static (World<K> target, in RootState state, ObjectReadTable objects) => {
                target.First = objects.ResolveObject<Dictionary<K, int>>(state.First);
                target.Second = objects.ResolveObject<Dictionary<K, int>>(state.Second);
                target.Containers = objects.ResolveObject<List<Dictionary<K, int>[]>>(state.Containers);
            }, (value, context) => new(context.CaptureObject(value.First, map), context.CaptureObject(value.Second, map),
                context.CaptureObject(value.Containers, containers)), Visit));
        return models;
    }
    private static RootState ReadRoot(ref BareValueReader reader) => new(new(reader.ReadVarUInt32()), new(reader.ReadVarUInt32()), new(reader.ReadVarUInt32()));
    private static RootState ApplyRoot(ref BareValueReader reader, in RootState prior) => ReadRoot(ref reader);
    private static PreparedBaseBody RootBase(in RootState state) {
        ArrayBufferWriter<byte> bytes = new();
        BareValueWriter writer = new(bytes);
        writer.WriteVarUInt32(state.First.Value); writer.WriteVarUInt32(state.Second.Value); writer.WriteVarUInt32(state.Containers.Value);
        return new(bytes.WrittenSpan);
    }
    private static PreparedDeltaBody RootDelta(in RootState prior, in RootState current) => new(prior != current, RootBase(in current).Body);
    private static Dictionary<string, int> Numbers(IEqualityComparer<string> comparer) =>
        Enumerable.Range(0, 100).ToDictionary(index => $"key-{index:D3}", index => 10000 + index, comparer);

    private readonly struct Key(int id, int timestamp, int transient = 0) : IEquatable<Key> {
        public int Id { get; } = id;
        public int Timestamp { get; } = timestamp;
        public int Transient { get; } = transient;
        public bool Equals(Key other) => Id == other.Id;
        public override bool Equals(object? other) => other is Key key && Equals(key);
        public override int GetHashCode() => Id;
    }
    private readonly record struct KeyState(int Id, int Timestamp);
    private static readonly DurableSchema KeySchema = new("CompositeConfigKey", 1, SchemaKind.InlineValue,
        new DurableFieldInfo(1, TypeTag.Int32), new DurableFieldInfo(2, TypeTag.Int32));
    private static StateDefinitionBinding KeyDefinition() => new("CompositeConfigKey", SchemaKind.InlineValue, 0, typeof(Key),
        [new("CompositeConfigKey", 1, SchemaKind.InlineValue, 0, [new(1, TypeExpr.Builtin(TypeTag.Int32)), new(2, TypeExpr.Builtin(TypeTag.Int32))])],
        currentValueFactory: static (_, _) => new(new(1, TypeTag.InlineValue, inlineSchema: KeySchema), typeof(KeyState), typeof(KeyOps), typeof(Key), typeof(KeyProjection)),
        historicalValueFactory: static (schema, _) => new(new(1, TypeTag.InlineValue, inlineSchema: schema), typeof(KeyState), typeof(KeyOps)));
    private readonly struct KeyProjection : IValueProjection<Key, KeyState> {
        public static KeyState Capture(in Key value, CaptureContext context, DurableFieldInfo slot) => new(value.Id, value.Timestamp);
        public static void Hydrate(ref Key target, in KeyState state, ObjectReadTable objects, DurableFieldInfo slot) => target = new(state.Id, state.Timestamp);
    }
    private readonly struct KeyOps : IStateOps<KeyState> {
        public static bool StateEquals(in KeyState left, in KeyState right, DurableFieldInfo slot) => left == right;
        public static void WriteBase(ref BareValueWriter writer, in KeyState state, DurableFieldInfo slot) {
            writer.WriteVarInt32(state.Id); writer.WriteVarInt32(state.Timestamp);
        }
        public static KeyState ReadBase(ref BareValueReader reader, DurableFieldInfo slot) => new(reader.ReadVarInt32(), reader.ReadVarInt32());
        public static PreparedDeltaBody PrepareDelta(in KeyState prior, in KeyState current, DurableFieldInfo slot) {
            ArrayBufferWriter<byte> bytes = new();
            BareValueWriter writer = new(bytes);
            WriteBase(ref writer, in current, slot);
            return new(prior != current, bytes.WrittenSpan);
        }
        public static KeyState ApplyDelta(ref BareValueReader reader, in KeyState prior, DurableFieldInfo slot) => ReadBase(ref reader, slot);
        public static void VisitReferences(in KeyState state, IStateReferenceVisitor visitor, DurableFieldInfo slot) { }
    }
    private sealed class StringProxy(StringComparer inner) : IEqualityComparer<string> {
        public bool Equals(string? left, string? right) => inner.Equals(left, right);
        public int GetHashCode(string value) => inner.GetHashCode(value);
    }
    private sealed class StringIdentityProxy : IEqualityComparer<string> {
        public bool Equals(string? left, string? right) => ReferenceEquals(left, right);
        public int GetHashCode(string value) => ReferenceEqualityComparer.Instance.GetHashCode(value);
    }
    private sealed class BitComparer : IEqualityComparer<double> {
        public bool Equals(double left, double right) => BitConverter.DoubleToInt64Bits(left) == BitConverter.DoubleToInt64Bits(right);
        public int GetHashCode(double value) => BitConverter.DoubleToInt64Bits(value).GetHashCode();
    }
    private sealed class TransientKeyComparer : IEqualityComparer<Key> {
        public bool Equals(Key left, Key right) => left.Id == right.Id && left.Transient == right.Transient;
        public int GetHashCode(Key value) => HashCode.Combine(value.Id, value.Transient);
    }
    private sealed class ResolverFailure : Exception;
    private RepositoryResources OpenSchemas() => RepositoryResources.OpenExisting(_root);
    private static IReadOnlyList<ObjectVersionRecord> Records(GraphRecordStore store, FrameAddress address) =>
        store.ReadGraph(address).OwnedBatch is { } batch ? store.ReadBatch(batch).Records : [];

    public void Dispose() {
        string path = Path.GetFullPath(_root);
        if (Path.GetDirectoryName(path) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) ||
            !Path.GetFileName(path).StartsWith("composite-dictionary-repository-", StringComparison.Ordinal)) {
            throw new InvalidOperationException("Unexpected fixture directory.");
        }
        if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); }
    }
}
