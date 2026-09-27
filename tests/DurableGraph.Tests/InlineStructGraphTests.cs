using Atelia.DurableGraph.Schema;
using System.Reflection;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Fact]
    public void InlineStructGraphSessionRetainsInstancesWritesNestedDeltaAndColdRestoresRemovedHistory() {
        InlineGraphFixture fixture = CompileInlineGraph();
        using RawBaseDirectory directory = new();
        FrameAddress first, unchanged, nested, childOnly, removed;
        ObjectId worldId;
        object originalWorld, originalChild;
        using (FixtureGraphRepository repository = FixtureGraphRepository.CreateNew(directory.Path, fixture.Models())) {
            using IDisposable session = (IDisposable)fixture.CreateSession(repository);
            originalWorld = fixture.World(session);
            originalChild = fixture.Child(originalWorld)!;
            first = fixture.Commit(session);
            worldId = fixture.WorldId(session);
            unchanged = fixture.Commit(session);
            fixture.ChangeNested(originalWorld, 8);
            nested = fixture.Commit(session);
            fixture.ChangeChild(originalWorld, 12);
            childOnly = fixture.Commit(session);
            Assert.Same(originalWorld, fixture.World(session));
            Assert.Same(originalChild, fixture.Child(originalWorld));
            fixture.Check(originalWorld, 8, 12, false);
            fixture.Detach(originalWorld);
            removed = fixture.Commit(session);
            Assert.Same(originalWorld, fixture.World(session));
            Assert.Equal(removed, repository.HeadRevisionAddress);
            fixture.Check(originalWorld, 8, -1, false);
        }

        using (SegmentStore segments = SegmentStore.OpenExisting(Path.Combine(directory.Path, "state")))
        using (var schemaFile = RbfFile.OpenExisting(Path.Combine(directory.Path, "schemas.rbf"))) {
            using StateRevisionStore store = new(segments);
            SchemaStore schemas = new(schemaFile);
            StateRevision initial = store.Read(first);
            Assert.Equal(4, initial.LocalObjects.Count); // World, Child, two distinct equal strings; no value rows.
            Assert.All(initial.LocalObjects, record => Assert.Equal(ObjectVersionKind.Base, record.Kind));
            uint childId = FindGraphObject(schemas, initial, "inline.graph.child");
            Assert.Equal(first, store.Read(unchanged).ParentRevisionAddress);
            Assert.Empty(store.Read(unchanged).LocalObjects);
            Assert.Empty(store.Read(unchanged).RemovedObjectIds);
            ObjectVersionRecord ownerDelta = Assert.Single(store.Read(nested).LocalObjects);
            Assert.Equal(worldId.Value, ownerDelta.ObjectId);
            Assert.Equal(ObjectVersionKind.Delta, ownerDelta.Kind);
            // Owner field 1 -> Envelope field 1 -> Leaf field 1 -> signed int 8.
            Assert.Equal<byte>([1, 1, 1, 16], ownerDelta.Body.ToArray());
            ObjectVersionRecord childDelta = Assert.Single(store.Read(childOnly).LocalObjects);
            Assert.Equal(childId, childDelta.ObjectId);
            Assert.Equal(ObjectVersionKind.Delta, childDelta.Kind);
            Assert.Equal(new[] { childId }, store.Read(removed).RemovedObjectIds);
            Assert.DoesNotContain(childId, store.ReadLiveObjectHeadMap(removed).Keys);
            Assert.Contains(childId, store.ReadLiveObjectHeadMap(first).Keys);
            Assert.Equal(4, store.ReadLiveObjectHeadMap(first).Count);
            fixture.Check(fixture.LoadOld(store, schemas, first, worldId), 7, 11, true);
            fixture.Check(fixture.LoadOld(store, schemas, childOnly, worldId), 8, 12, true);
            fixture.Check(fixture.LoadOld(store, schemas, removed, worldId), 8, -1, true);
        }

        FrameAddress reopenedNoChange;
        using (FixtureGraphRepository repository = FixtureGraphRepository.OpenExisting(directory.Path, fixture.Models())) {
            using IDisposable loaded = (IDisposable)fixture.LoadSession(repository);
            object world = fixture.World(loaded);
            Assert.NotSame(originalWorld, world);
            fixture.Check(world, 8, -1, true);
            reopenedNoChange = fixture.Commit(loaded);
            Assert.Same(world, fixture.World(loaded));
        }
        using (SegmentStore segments = SegmentStore.OpenExisting(Path.Combine(directory.Path, "state"))) {
            using StateRevisionStore store = new(segments);
            Assert.Equal(removed, store.Read(reopenedNoChange).ParentRevisionAddress);
            Assert.Empty(store.Read(reopenedNoChange).LocalObjects);
            Assert.Empty(store.Read(reopenedNoChange).RemovedObjectIds);
        }
    }

    [Fact]
    public void InlineStructUnregisteredNestedReferenceFailureDoesNotAdvanceSessionBaseline() {
        InlineGraphFixture fixture = CompileInlineGraph();
        using RawBaseDirectory directory = new();
        FrameAddress first, retry;
        using (FixtureGraphRepository repository = FixtureGraphRepository.CreateNew(directory.Path, fixture.Models())) {
            using IDisposable session = (IDisposable)fixture.CreateSession(repository);
            first = fixture.Commit(session);
            object world = fixture.World(session);
            object child = fixture.ReplaceUnknown(world);
            Assert.Throws<InvalidOperationException>(() => fixture.Commit(session));
            Assert.Equal(first, repository.HeadRevisionAddress);
            Assert.False(repository.IsFaulted);
            fixture.RestoreChild(world, child);
            retry = fixture.Commit(session);
            Assert.Same(world, fixture.World(session));
            Assert.Same(child, fixture.Child(world));
            fixture.Check(world, 7, 11, false);
        }
        using SegmentStore segments = SegmentStore.OpenExisting(Path.Combine(directory.Path, "state"));
        using StateRevisionStore store = new(segments);
        Assert.Equal(first, store.Read(retry).ParentRevisionAddress);
        Assert.Empty(store.Read(retry).LocalObjects);
        Assert.Empty(store.Read(retry).RemovedObjectIds);
    }

    [Fact]
    public void InlineStructPreparedCandidateDoesNotFollowLaterDomainSlotOrReferencedObjectMutations() {
        InlineGraphFixture fixture = CompileInlineGraph();
        using RawBaseDirectory directory = new();
        using RawBaseDirectory schemaDirectory = new();
        Directory.CreateDirectory(schemaDirectory.Path);
        string schemaPath = Path.Combine(schemaDirectory.Path, "schemas.rbf");
        FrameAddress first;
        ObjectId worldId;
        using (SegmentStore segments = SegmentStore.CreateNew(directory.Path))
        using (var schemaFile = RbfFile.CreateNew(schemaPath)) {
            using StateRevisionStore store = new(segments);
            SchemaStore schemas = new(schemaFile);
            object world = fixture.NewWorld();
            FixturePreparedWorldRevision frozen = fixture.PrepareNew(store, schemas, world);
            worldId = frozen.WorldId;
            fixture.ChangeNested(world, 999);
            fixture.ChangeChild(world, 888);
            fixture.Detach(world);
            first = store.Append(frozen.Revision);
        }
        using (SegmentStore segments = SegmentStore.OpenExisting(directory.Path))
        using (var schemaFile = RbfFile.OpenExisting(schemaPath)) {
            using StateRevisionStore store = new(segments);
            SchemaStore schemas = new(schemaFile);
            fixture.Check(fixture.LoadOld(store, schemas, first, worldId), 7, 11, true);
            Assert.Equal(4, store.ReadLiveObjectHeadMap(first).Count);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InlineStructStoredDeepReferenceValidationRejectsMissingOrWrongKindTarget(bool wrongKind) {
        InlineGraphFixture fixture = CompileInlineGraph();
        using RawBaseDirectory directory = new();
        using RawBaseDirectory schemaDirectory = new();
        Directory.CreateDirectory(schemaDirectory.Path);
        using SegmentStore segments = SegmentStore.CreateNew(directory.Path);
        using var schemaFile = RbfFile.CreateNew(Path.Combine(schemaDirectory.Path, "schemas.rbf"));
        using StateRevisionStore store = new(segments);
        SchemaStore schemas = new(schemaFile);
        FixturePreparedWorldRevision initial = fixture.PrepareNew(store, schemas, fixture.NewWorld());
        ObjectVersionRecord owner = Assert.Single(initial.Revision.LocalObjects,
            record => record.ObjectId == initial.WorldId.Value);
        DecodedBaseObjectBody envelope = BaseObjectBodyCodec.Decode(owner.Body, schemas);
        byte[] body = envelope.Body.ToArray();
        uint childId = FindGraphObject(schemas, initial.Revision, "inline.graph.child");
        Assert.Equal(14, body[0]); // Envelope.Inner.X = 7, followed by the nested Child ID.
        Assert.Equal(childId, (uint)body[1]);
        uint replacement = wrongKind
            ? initial.Revision.LocalObjects.First(record =>
                BaseObjectBodyCodec.Decode(record.Body, schemas).Kind == ObjectStateKind.String).ObjectId
            : initial.Revision.LocalObjectIds.Max() + 1;
        Assert.InRange(replacement, 1u, 127u);
        body[1] = (byte)replacement; // Canonical one-byte ID; no broken wire/framing to mask visitor failure.
        ObjectVersionRecord invalidOwner = ObjectVersionRecord.CreateBase(initial.WorldId.Value,
            BaseObjectBodyCodec.Encode(envelope.RepresentationId, new(body)).Body);
        FrameAddress address = store.Append(StateRevision.CreateObjectHeadMapBase(null,
            initial.Revision.LocalObjects.Select(record => record.ObjectId == initial.WorldId.Value ? invalidOwner : record), []));

        // Exact DTO reading itself must reject; rejection cannot be deferred until domain Hydrate.
        Assert.Throws<InvalidDataException>(() => fixture.ReadStored(store, schemas, address));
        Assert.Throws<InvalidDataException>(() => fixture.LoadOld(store, schemas, address, initial.WorldId));
    }

    private static InlineGraphFixture CompileInlineGraph() {
        GeneratorTestRun run = RunGenerator(InlineGraphSource);
        AssertSchemaOnlyCompiles(run);
        return new(EmitAndLoad(run.OutputCompilation).GetType("InlineGraph.Host")!);
    }

    private sealed class InlineGraphFixture(Type host) {
        private T Method<T>(string name) where T : Delegate => host.GetMethod(name)!.CreateDelegate<T>();
        public StateModelRegistry Models() => Method<Func<StateModelRegistry>>("Models")();
        public object CreateSession(FixtureGraphRepository repository) => Method<Func<FixtureGraphRepository, object>>("CreateSession")(repository);
        public object LoadSession(FixtureGraphRepository repository) => Method<Func<FixtureGraphRepository, object>>("LoadSession")(repository);
        public object NewWorld() => Method<Func<object>>("NewWorld")();
        public object World(object session) => Method<Func<object, object>>("World")(session);
        public object? Child(object world) => Method<Func<object, object?>>("Child")(world);
        public ObjectId WorldId(object session) => Method<Func<object, ObjectId>>("WorldId")(session);
        public FrameAddress Commit(object session) => Method<Func<object, FrameAddress>>("Commit")(session);
        public void ChangeNested(object world, int value) => Method<Action<object, int>>("ChangeNested")(world, value);
        public void ChangeChild(object world, int value) => Method<Action<object, int>>("ChangeChild")(world, value);
        public void Detach(object world) => Method<Action<object>>("Detach")(world);
        public object ReplaceUnknown(object world) => Method<Func<object, object>>("ReplaceUnknown")(world);
        public void RestoreChild(object world, object child) => Method<Action<object, object>>("RestoreChild")(world, child);
        public void Check(object world, int value, int childValue, bool cold)
            => Method<Action<object, int, int, bool>>("Check")(world, value, childValue, cold);
        public FixturePreparedWorldRevision PrepareNew(StateRevisionStore states, SchemaStore schemas, object world)
            => Method<Func<StateRevisionStore, SchemaStore, object, FixturePreparedWorldRevision>>("PrepareNew")(states, schemas, world);
        public object LoadOld(StateRevisionStore states, SchemaStore schemas, FrameAddress address, ObjectId worldId)
            => Method<Func<StateRevisionStore, SchemaStore, FrameAddress, ObjectId, object>>("LoadOld")(states, schemas, address, worldId);
        public DecodedRevision ReadStored(StateRevisionStore states, SchemaStore schemas, FrameAddress address)
            => Method<Func<StateRevisionStore, SchemaStore, FrameAddress, DecodedRevision>>("ReadStored")(states, schemas, address);
    }

    private const string InlineGraphSource = """
        using System;
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        using Atelia.DurableGraph.Persistence;
        using Atelia.DurableGraph.Storage;
        namespace InlineGraph;

        [DurableType("inline.graph.leaf", 1)]
        public readonly partial struct Leaf {
            [DurableField(1)] private readonly int _x;
            [DurableField(2)] private readonly Child? _child;
            [DurableField(3)] private readonly World? _owner;
            [DurableField(4)] private readonly string? _text;
            [Transient] private readonly int _cache = 17;
            public Leaf(int x, Child? child, World? owner, string? text) {
                _x = x; _child = child; _owner = owner; _text = text;
            }
            public int X => _x;
            public Child? Child => _child;
            public World? Owner => _owner;
            public string? Text => _text;
            public int Cache => _cache;
        }
        [DurableType("inline.graph.envelope", 1)]
        public readonly partial struct Envelope {
            [DurableField(1)] private readonly Leaf _inner;
            [DurableField(2)] private readonly string? _equalText;
            [Transient] private readonly int _cache = 17;
            public Envelope(Leaf inner, string? equalText) { _inner = inner; _equalText = equalText; }
            public Leaf Inner => _inner;
            public string? EqualText => _equalText;
            public int Cache => _cache;
        }
        [DurableType("inline.graph.world", 1)]
        public sealed partial class World : IDurableObject {
            [DurableField(1)] public Envelope Value;
            [DurableField(2)] public string? Alias;
            [DurableField(3)] public readonly long Padding;
            [DurableField(4)] public Leaf Second;
            [Transient] public int Cache = 17;
            public World(int x) {
                Alias = new string('s', 4);
                Child child = new(this, 11);
                Value = new(new(x, child, this, Alias), new string('s', 4));
                Second = new(29, child, this, Alias);
                Padding = long.MaxValue;
            }
            public void ChangeNested(int value) {
                Value = new(new(value, Value.Inner.Child, this, Value.Inner.Text), Value.EqualText);
            }
            public void SetChild(Child? child) {
                Value = new(new(Value.Inner.X, child, this, Value.Inner.Text), Value.EqualText);
                Second = new(Second.X, child, this, Second.Text);
            }
            public void Check(int value, int childValue, bool cold) {
                if (Value.Inner.X != value || Second.X != 29 || Padding != long.MaxValue)
                    throw new Exception("Nested value content changed");
                if (!ReferenceEquals(Value.Inner.Owner, this) || !ReferenceEquals(Second.Owner, this)
                    || !ReferenceEquals(Value.Inner.Child, Second.Child))
                    throw new Exception("Nested owner cycle or shared child identity changed");
                if (Alias != "ssss" || Value.EqualText != Alias || !ReferenceEquals(Alias, Value.Inner.Text)
                    || !ReferenceEquals(Alias, Second.Text) || ReferenceEquals(Alias, Value.EqualText))
                    throw new Exception("Nested shared/distinct nonempty string identity changed");
                if (childValue < 0) {
                    if (Value.Inner.Child != null) throw new Exception("Removed child returned");
                } else {
                    Child child = Value.Inner.Child ?? throw new Exception("Missing child");
                    if (child.Value != childValue || !ReferenceEquals(child.Owner, this)
                        || !ReferenceEquals(child.Self, child)) throw new Exception("Child cycle/content changed");
                }
                if (cold && (Cache != 0 || Value.Cache != 0 || Value.Inner.Cache != 0 || Second.Cache != 0))
                    throw new Exception("Struct constructor/initializer ran during cold restoration");
                if (!cold && Cache != 17) throw new Exception("Commit recreated original World");
            }
        }
        [DurableType("inline.graph.child", 1)]
        public partial class Child : IDurableObject {
            [DurableField(1)] public int Value;
            [DurableField(2)] public readonly World Owner;
            [DurableField(3)] public readonly Child Self;
            [DurableField(4)] public readonly long Padding;
            public Child(World owner, int value) { Owner = owner; Value = value; Self = this; Padding = long.MaxValue; }
        }
        public sealed class UnknownChild : Child { public UnknownChild(World owner) : base(owner, 99) { } }
        public static class Host {
            public static StateModelRegistry Models() {
                StateModelRegistry models = new();
                global::InlineGraph.World.__DurableState.RegisterModel(models);
                global::InlineGraph.Child.__DurableState.RegisterModel(models);
                return models;
            }
            public static object NewWorld() => new World(7);
            public static object CreateSession(FixtureGraphRepository repository) => repository.Create(new World(7));
            public static object LoadSession(FixtureGraphRepository repository) => repository.Load<World>();
            public static object World(object session) => ((FixtureGraphSession<World>)session).World;
            public static object? Child(object world) => ((World)world).Value.Inner.Child;
            public static ObjectId WorldId(object session) => ((FixtureGraphSession<World>)session).WorldId!.Value;
            public static FrameAddress Commit(object session) => ((FixtureGraphSession<World>)session).Commit(new(1000000, 1));
            public static void ChangeNested(object world, int value) => ((World)world).ChangeNested(value);
            public static void ChangeChild(object world, int value) => ((World)world).Value.Inner.Child!.Value = value;
            public static void Detach(object world) => ((World)world).SetChild(null);
            public static object ReplaceUnknown(object world) {
                World root = (World)world;
                Child previous = root.Value.Inner.Child!;
                root.SetChild(new UnknownChild(root));
                return previous;
            }
            public static void RestoreChild(object world, object child) => ((World)world).SetChild((Child)child);
            public static void Check(object world, int value, int childValue, bool cold)
                => ((World)world).Check(value, childValue, cold);
            public static FixturePreparedWorldRevision PrepareNew(StateRevisionStore states, SchemaStore schemas, object world)
                => FixtureLoadedWorld.PrepareNew(states, schemas, (World)world, Models(), new(1000000, 1));
            public static object LoadOld(StateRevisionStore states, SchemaStore schemas, FrameAddress address, ObjectId worldId)
                => FixtureLoadedWorld.Load<World>(states, schemas, address, worldId, Models()).World;
            public static DecodedRevision ReadStored(StateRevisionStore states, SchemaStore schemas, FrameAddress address) {
                StateReaderRegistry readers = new();
                global::InlineGraph.World.__DurableState.RegisterReaders(readers);
                global::InlineGraph.Child.__DurableState.RegisterReaders(readers);
                return RevisionDecoder.Read(states, schemas, address, readers);
            }
        }
        """;
}
