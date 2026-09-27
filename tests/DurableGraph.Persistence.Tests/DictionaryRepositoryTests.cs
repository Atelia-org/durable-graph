using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Buffers;
using System.Reflection;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using Atelia.RbfSegmentStore;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class DictionaryRepositoryTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"durable-dictionary-repository-{Guid.NewGuid():N}");
    private static readonly ReadAmplificationBaseBudgetParameters NoRebase = new(1000000, 1);
    private static readonly TypeExpr ValuesType = TypeExpr.Dictionary(TypeExpr.Builtin(TypeTag.String), TypeExpr.Builtin(TypeTag.Int32));
    private static readonly TypeExpr LinksType = TypeExpr.Dictionary(TypeExpr.Named("DictionaryWorld"), TypeExpr.Named("DictionaryWorld"));
    private static readonly DurableSchema Schema = new("DictionaryWorld", 1,
        DurableFieldInfo.Reference(1, ValuesType), DurableFieldInfo.Reference(2, ValuesType),
        DurableFieldInfo.Reference(3, LinksType), new DurableFieldInfo(4, TypeTag.String), new DurableFieldInfo(5, TypeTag.Byte));

    [Fact]
    public void SharedMapSurvivesPermutationAndThreeSuccessiveKeyedDeltaCommits() {
        Dictionary<string, int> values = Enumerable.Range(0, 100).ToDictionary(i => $"key-{i:D3}", i => 10000 + i);
        World world = new() { Values = values, Alias = values };
        List<(FrameAddress Address, Dictionary<string, int> Expected)> revisions = [];
        FrameAddress reordered;
        using (StateSaveHarness repository = CreateRepository()) {
            using StateSaveSession<World> session = repository.Create(world);
            revisions.Add((session.Commit(NoRebase), new(values)));
            KeyValuePair<string, int>[] reverse = values.Reverse().ToArray();
            values.Clear();
            foreach (var pair in reverse) { values.Add(pair.Key, pair.Value); }
            values.EnsureCapacity(500);
            reordered = session.Commit(NoRebase);
            values.Remove("key-030");
            values.Add("new-a", 777);
            values["key-090"] = 1234;
            revisions.Add((session.Commit(NoRebase), new(values)));
            values.Remove("key-050");
            values.Add("new-b", 888);
            values["key-070"] = 4321;
            revisions.Add((session.Commit(NoRebase), new(values)));
            values.Remove("new-a");
            values.Add("new-c", 999);
            values["key-090"] = 9876;
            revisions.Add((session.Commit(NoRebase), new(values)));
            Assert.Same(world, session.World);
            Assert.Same(values, session.World.Values);
            Assert.Same(values, session.World.Alias);
        }
        using (SegmentStore segments = OpenState())
        using (IRbfFile file = RbfFile.OpenExisting(Path.Combine(_root, "schemas.rbf"))) {
            using StateRevisionStore states = new(segments);
            SchemaStore schemas = new(file, readOnly: true);
            Assert.Empty(states.Read(reordered).LocalObjects);
            Assert.Empty(states.Read(reordered).RemovedObjectIds);
            StateModelSnapshot snapshot = Models().Snapshot(schemas);
            ObjectId mapId = Assert.Single(RevisionDecoder.ReadSnapshot(states, schemas, revisions[0].Address, snapshot).Objects,
                row => row.Kind == ObjectStateKind.Dictionary).Id;
            foreach ((FrameAddress address, Dictionary<string, int> expected) in revisions) {
                DecodedRevision exact = RevisionDecoder.ReadSnapshot(states, schemas, address, snapshot);
                FrozenDictionaryState<ObjectId, int> frozen = exact.GetRequired(mapId).GetDictionaryState<ObjectId, int>();
                Dictionary<string, int> actual = frozen.Entries.ToArray().ToDictionary(
                    entry => exact.GetRequired(entry.Key).StringContent, entry => entry.Value);
                Assert.Equal(expected.OrderBy(pair => pair.Key), actual.OrderBy(pair => pair.Key));
                if (address != revisions[0].Address) {
                    ObjectVersionRecord change = Assert.Single(states.Read(address).LocalObjects, row => row.ObjectId == mapId.Value);
                    Assert.Equal(ObjectVersionKind.Delta, change.Kind);
                    Assert.True(change.Body.Length < 80, $"A three-entry edit encoded {change.Body.Length} bytes.");
                }
            }
        }
        FrameAddress unchanged;
        using (StateSaveHarness repository = StateSaveHarness.OpenExisting(_root, Models())) {
            using StateSaveSession<World> session = repository.Load<World>();
            Assert.Equal(values.OrderBy(pair => pair.Key), session.World.Values!.OrderBy(pair => pair.Key));
            Assert.Same(session.World.Values, session.World.Alias);
            unchanged = session.Commit(NoRebase);
        }
        using (SegmentStore segments = OpenState()) {
            using StateRevisionStore states = new(segments);
            Assert.Empty(states.Read(unchanged).LocalObjects);
        }
    }

    [Fact]
    public void SameClosedMapTypeSupportsDifferentComparersAndReferenceKeyCycles() {
        World world = new() {
            Values = new(StringComparer.Ordinal) { ["A"] = 1 },
            Alias = new(StringComparer.OrdinalIgnoreCase) { ["B"] = 2 },
        };
        World child = new() { Value = 3 };
        Dictionary<World, World> links = new(ReferenceEqualityComparer.Instance) { [world] = child, [child] = world };
        world.Links = child.Links = links;
        FrameAddress childOnly, removed;
        using (StateSaveHarness repository = CreateRepository()) {
            using StateSaveSession<World> session = repository.Create(world);
            session.Commit(NoRebase);
            child.Value = 4;
            childOnly = session.Commit(NoRebase);
        }
        using (StateSaveHarness repository = StateSaveHarness.OpenExisting(_root, Models())) {
            using StateSaveSession<World> session = repository.Load<World>();
            World loaded = session.World;
            Assert.False(loaded.Values!.ContainsKey("a"));
            Assert.Equal(2, loaded.Alias!["b"]);
            World loadedChild = loaded.Links![loaded];
            Assert.Equal((byte)4, loadedChild.Value);
            Assert.Same(loaded, loaded.Links[loadedChild]);
            Assert.Same(loaded.Links, loadedChild.Links);
            loaded.Links = null;
            removed = session.Commit(NoRebase);
        }
        using SegmentStore segments = OpenState();
        using StateRevisionStore states = new(segments);
        Assert.Single(states.Read(childOnly).LocalObjects);
        Assert.Equal(2, states.Read(removed).RemovedObjectIds.Count);
    }

    [Fact]
    public void ReferenceStringKeysKeepSeparateInstancesAndDomainAliases() {
        string first = new("same".ToCharArray());
        string second = new("same".ToCharArray());
        Assert.NotSame(first, second);
        World world = new() { KeyAlias = first, Values = new(ReferenceEqualityComparer.Instance) { [first] = 1, [second] = 2 } };
        using (StateSaveHarness repository = CreateRepository()) {
            using StateSaveSession<World> session = repository.Create(world);
            session.Commit(NoRebase);
        }
        using StateSaveHarness cold = StateSaveHarness.OpenExisting(_root, Models());
        using StateSaveSession<World> loaded = cold.Load<World>();
        Dictionary<string, int> map = loaded.World.Values!;
        Assert.Equal(2, map.Count);
        Assert.Equal(1, map[loaded.World.KeyAlias!]);
        string other = Assert.Single(map.Keys, key => !ReferenceEquals(key, loaded.World.KeyAlias));
        Assert.Equal(other, loaded.World.KeyAlias);
        Assert.Equal(2, map[other]);
        Assert.False(map.ContainsKey(new string("same".ToCharArray())));
    }

    [Fact]
    public void UnsupportedComparerFailsBeforePublicationAndDoesNotReplaceBaseline() {
        Dictionary<string, int> original = new() { ["a"] = 1 };
        World world = new() { Values = original };
        using StateSaveHarness repository = CreateRepository();
        using StateSaveSession<World> session = repository.Create(world);
        FrameAddress prior = session.Commit(NoRebase);
        CountingComparer comparer = new();
        world.Values = new(comparer) { ["b"] = 2 };
        comparer.Calls = 0;
        Assert.Throws<InvalidDataException>(() => session.Commit(NoRebase));
        Assert.Equal(0, comparer.Calls);
        Assert.Equal(prior, repository.HeadRevisionAddress);
        Assert.False(repository.IsFaulted);
        world.Values = original;
        session.Commit(NoRebase);
        Assert.Same(original, session.World.Values);
    }

    [Theory]
    [InlineData(DictionaryComparerKind.StringOrdinal, "same", "same")]
    [InlineData(DictionaryComparerKind.StringOrdinalIgnoreCase, "SAME", "same")]
    [InlineData(DictionaryComparerKind.ReferenceIdentity, "", "")]
    public void ExactReadRejectsLookupKeyCollisionEvenInUnreachableDictionary(
        DictionaryComparerKind comparer, string first, string second) {
        Directory.CreateDirectory(_root);
        using SegmentStore segments = SegmentStore.CreateNew(Path.Combine(_root, "state"));
        using IRbfFile file = RbfFile.CreateNew(Path.Combine(_root, "schemas.rbf"));
        using StateRevisionStore states = new(segments);
        SchemaStore schemas = new(file);
        DictionaryLayout layout = new(new(1, TypeTag.String), new(2, TypeTag.Int32));
        RepresentationId mapRepresentation = schemas.RegisterRepresentations([ObjectLayout.ForDictionary(layout)])[0];
        RepresentationId worldRepresentation = schemas.RegisterRepresentations([ObjectLayout.ForDurable(Schema)])[0];
        ArrayBufferWriter<byte> bytes = new();
        BinaryPayloadWriter writer = new(bytes);
        writer.WriteByte((byte)comparer);
        writer.WriteUInt32(2);
        writer.WriteUInt32(3); writer.WriteInt32(10);
        writer.WriteUInt32(4); writer.WriteInt32(20);
        State empty = default;
        FrameAddress address = states.Append(StateRevision.CreateObjectHeadMapBase(null,
            [ObjectVersionRecord.CreateBase(1, BaseObjectBodyCodec.Encode(worldRepresentation, Base(in empty)).Body),
             ObjectVersionRecord.CreateBase(2, BaseObjectBodyCodec.Encode(mapRepresentation, new(bytes.WrittenSpan)).Body),
             Text(3, first), Text(4, second)], []));
        StateReaderRegistry readers = new();
        readers.Register(new StateReaderBinding<State>(Schema, Read, Apply, Visit));
        Assert.Throws<InvalidDataException>(() => RevisionDecoder.Read(states, schemas, address, readers));
        int normalizations = 0;
        Assert.Throws<InvalidDataException>(() => LoadedWorld.Load<World>(states, schemas, address, new(1),
            Models(() => normalizations++)));
        Assert.Equal(0, normalizations);
    }

    [Fact]
    public void CurrentValidationRejectsLookupCollisionInUnreachableNormalizedDictionary() {
        // Enter the internal current-validation boundary directly. Even an unreachable
        // unchanged-layout map must pass lookup equality before the directory is returned.
        byte[] stringBody = StringPayloadCodec.PrepareBase("same").Body.ToArray();
        StringReadTable strings = StringReadTable.Decode([(new ObjectId(3), stringBody.AsMemory()), (new ObjectId(4), stringBody.AsMemory())]);
        StateModelSnapshot snapshot = Models().Snapshot();
        Assert.True(snapshot.TryGetCurrentObjectBinding(typeof(Dictionary<string, int>), out ObjectBinding? binding));
        DictionaryLayout layout = ((DictionaryObjectBinding)binding!).DictionaryLayout;
        State detached = default;
        // Controlled fixture construction bypasses exact-reader rejection to exercise the
        // independent current boundary. Product visibility remains unchanged.
        ObjectStateRecord[] rows = [Row(new ObjectId(1), Schema, detached, null),
            Row(new ObjectId(2), layout, new FrozenDictionaryState<ObjectId, int>(DictionaryComparerKind.StringOrdinal,
                [new(new(3), 1), new(new(4), 2)]), null),
            Row(new ObjectId(3), strings.ResolveString(new(3))), Row(new ObjectId(4), strings.ResolveString(new(4)))];
        DecodedRevision source = new(default, rows, strings);
        Assert.Throws<InvalidDataException>(() => NormalizedRevision.Create(source, snapshot));

        static ObjectStateRecord Row(params object?[] arguments) =>
            (ObjectStateRecord)Activator.CreateInstance(typeof(ObjectStateRecord), BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null, args: arguments, culture: null)!;
    }

    private sealed class World : IDurableObject {
        public Dictionary<string, int>? Values;
        public Dictionary<string, int>? Alias;
        public Dictionary<World, World>? Links;
        public string? KeyAlias;
        public byte Value;
    }
    private readonly record struct State(ObjectId Values, ObjectId Alias, ObjectId Links, ObjectId KeyAlias, byte Value);

    private static StateModelRegistry Models(Action? onNormalize = null) {
        CapturedStatePreparation<State> prepare = new(Schema, Base, Delta);
        StateReaderBinding<State> reader = new(Schema, Read, Apply, Visit);
        StateModelBinding<World, State> model = new(prepare, [reader], row => { onNormalize?.Invoke(); return row.GetState<State>(); }, static () => new(),
            static (World world, in State state, ObjectReadTable objects) => {
                world.Values = objects.ResolveObject<Dictionary<string, int>>(state.Values);
                world.Alias = objects.ResolveObject<Dictionary<string, int>>(state.Alias);
                world.Links = objects.ResolveObject<Dictionary<World, World>>(state.Links);
                world.KeyAlias = objects.ResolveString(state.KeyAlias);
                world.Value = state.Value;
            }, static (world, context) => new(context.CaptureObject(world.Values, ValuesType),
                context.CaptureObject(world.Alias, ValuesType), context.CaptureObject(world.Links, LinksType),
                context.CaptureString(world.KeyAlias), world.Value), Visit);
        StateModelRegistry registry = new();
        registry.Register(model);
        return registry;
    }
    private static void Visit(in State state, IStateReferenceVisitor visitor) {
        visitor.VisitObject(state.Values, ValuesType);
        visitor.VisitObject(state.Alias, ValuesType);
        visitor.VisitObject(state.Links, LinksType);
        visitor.VisitString(state.KeyAlias);
    }
    private static State Read(ref BinaryPayloadReader reader) => new(new(reader.ReadUInt32()), new(reader.ReadUInt32()),
        new(reader.ReadUInt32()), new(reader.ReadUInt32()), reader.ReadByte());
    private static PreparedBaseBody Base(in State state) {
        ArrayBufferWriter<byte> bytes = new();
        BinaryPayloadWriter writer = new(bytes);
        writer.WriteUInt32(state.Values.Value); writer.WriteUInt32(state.Alias.Value);
        writer.WriteUInt32(state.Links.Value); writer.WriteUInt32(state.KeyAlias.Value);
        writer.WriteByte(state.Value);
        return new(bytes.WrittenSpan);
    }
    private static PreparedDeltaBody Delta(in State prior, in State next) {
        byte mask = (byte)((prior.Values != next.Values ? 1 : 0) | (prior.Alias != next.Alias ? 2 : 0) |
            (prior.Links != next.Links ? 4 : 0) | (prior.KeyAlias != next.KeyAlias ? 8 : 0) | (prior.Value != next.Value ? 16 : 0));
        ArrayBufferWriter<byte> bytes = new();
        BinaryPayloadWriter writer = new(bytes);
        writer.WriteByte(mask);
        if ((mask & 1) != 0) { writer.WriteUInt32(next.Values.Value); }
        if ((mask & 2) != 0) { writer.WriteUInt32(next.Alias.Value); }
        if ((mask & 4) != 0) { writer.WriteUInt32(next.Links.Value); }
        if ((mask & 8) != 0) { writer.WriteUInt32(next.KeyAlias.Value); }
        if ((mask & 16) != 0) { writer.WriteByte(next.Value); }
        return new(mask != 0, bytes.WrittenSpan);
    }
    private static State Apply(ref BinaryPayloadReader reader, in State prior) {
        byte mask = reader.ReadByte();
        if (mask is 0 or > 31) { throw new InvalidDataException("Invalid test state Delta."); }
        return new((mask & 1) != 0 ? new(reader.ReadUInt32()) : prior.Values,
            (mask & 2) != 0 ? new(reader.ReadUInt32()) : prior.Alias,
            (mask & 4) != 0 ? new(reader.ReadUInt32()) : prior.Links,
            (mask & 8) != 0 ? new(reader.ReadUInt32()) : prior.KeyAlias,
            (mask & 16) != 0 ? reader.ReadByte() : prior.Value);
    }
    private sealed class CountingComparer : IEqualityComparer<string> {
        internal int Calls;
        public bool Equals(string? left, string? right) { Calls++; return StringComparer.Ordinal.Equals(left, right); }
        public int GetHashCode(string value) { Calls++; return StringComparer.Ordinal.GetHashCode(value); }
    }
    private static ObjectVersionRecord Text(uint id, string value) =>
        ObjectVersionRecord.CreateBase(id, BaseObjectBodyCodec.EncodeString(StringPayloadCodec.PrepareBase(value)).Body);
    private StateSaveHarness CreateRepository(StateModelRegistry? models = null) => StateSaveHarness.CreateNew(_root, models ?? Models(), new() { NewStoreLayout = RbfSegmentStoreLayout.Flat });
    private SegmentStore OpenState() => SegmentStore.OpenExisting(Path.Combine(_root, "state"));
    public void Dispose() {
        string resolved = Path.GetFullPath(_root);
        if (Path.GetDirectoryName(resolved) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) ||
            !Path.GetFileName(resolved).StartsWith("durable-dictionary-repository-", StringComparison.Ordinal)) {
            throw new InvalidOperationException("Unexpected fixture directory.");
        }
        if (Directory.Exists(resolved)) { Directory.Delete(resolved, recursive: true); }
    }
}
