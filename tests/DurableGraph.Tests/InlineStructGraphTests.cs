using Atelia.DurableGraph.Schema;
using System.Reflection;
using Atelia.DurableGraph.Persistence;

using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

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

        using (RepositoryResources segments = RepositoryResources.OpenExisting(directory.Path))
        {
            var store = segments.GraphStore;
            var initial = store.ReadGraph(first);
            Assert.Equal(4, DefaultRecords(store, initial.Address).Count); // World, Child, two distinct equal strings; no value rows.
            Assert.All(DefaultRecords(store, initial.Address), record => Assert.Equal(ObjectVersionKind.Base, record.Kind));
            uint childId = Assert.Single(initial.ObjectHeads.Keys, id => DefaultLayout(store, first, id).Schema?.SchemaId == "inline.graph.child");
            Assert.Equal(first, store.ReadGraph(unchanged).SourceGraph);
            Assert.Empty(DefaultRecords(store, unchanged));
            Assert.Empty(DefaultRemoved(store, unchanged));
            ObjectVersionRecord ownerDelta = Assert.Single(DefaultRecords(store, nested));
            Assert.Equal(worldId.Value, ownerDelta.ObjectId);
            Assert.Equal(ObjectVersionKind.Delta, ownerDelta.Kind);
            // Owner field 1 -> Envelope field 1 -> Leaf field 1 -> signed int 8.
            Assert.Equal<byte>([1, 1, 1, 16], ownerDelta.Body.ToArray());
            ObjectVersionRecord childDelta = Assert.Single(DefaultRecords(store, childOnly));
            Assert.Equal(childId, childDelta.ObjectId);
            Assert.Equal(ObjectVersionKind.Delta, childDelta.Kind);
            Assert.Equal(new[] { childId }, DefaultRemoved(store, removed));
            Assert.DoesNotContain(childId, store.ReadGraph(removed).ObjectHeads.Keys);
            Assert.Contains(childId, store.ReadGraph(first).ObjectHeads.Keys);
            Assert.Equal(4, store.ReadGraph(first).ObjectHeads.Count);
            fixture.Check(BusinessPersistenceAdapter.ReadRoot(store, first, fixture.Models()), 7, 11, true);
            fixture.Check(BusinessPersistenceAdapter.ReadRoot(store, childOnly, fixture.Models()), 8, 12, true);
            fixture.Check(BusinessPersistenceAdapter.ReadRoot(store, removed, fixture.Models()), 8, -1, true);
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
        using (RepositoryResources segments = RepositoryResources.OpenExisting(directory.Path)) {
            var store = segments.GraphStore;
            Assert.Equal(removed, store.ReadGraph(reopenedNoChange).SourceGraph);
            Assert.Empty(DefaultRecords(store, reopenedNoChange));
            Assert.Empty(DefaultRemoved(store, reopenedNoChange));
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
        using RepositoryResources segments = RepositoryResources.OpenExisting(directory.Path);
        var store = segments.GraphStore;
        Assert.Equal(first, store.ReadGraph(retry).SourceGraph);
        Assert.Empty(DefaultRecords(store, retry));
        Assert.Empty(DefaultRemoved(store, retry));
    }

    [Fact]
    public void InlineStructPreparedCandidateDoesNotFollowLaterDomainSlotOrReferencedObjectMutations() {
        InlineGraphFixture fixture = CompileInlineGraph();
        using RawBaseDirectory directory = new();
        object world = fixture.NewWorld();
        bool mutated = false;
        using (Repository repository = Repository.CreateNew(directory.Path, fixture.Models(), schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared)) {
            repository.OperationStageObserver = checkpoint => {
                if (checkpoint != RepositoryOperationStage.AfterPrepare) { return; }
                fixture.ChangeNested(world, 999);
                fixture.ChangeChild(world, 888);
                fixture.Detach(world);
                mutated = true;
            };
            using BranchCheckout checkout = repository.CreateBranch("main", (IDurableObject)world);
            Assert.True(mutated);
            Assert.Same(world, checkout.State);
        }
        using Repository reopened = Repository.OpenReadOnlyExisting(directory.Path, fixture.Models());
        fixture.Check(reopened.ReadState(reopened.GetHead("main")), 7, 11, true);
        Assert.Equal(4, reopened.GraphStore.ReadGraph(reopened.GetHead("main").RevisionAddress).ObjectHeads.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InlineStructStoredDeepReferenceValidationRejectsMissingOrWrongKindTarget(bool wrongKind) {
        InlineGraphFixture fixture = CompileInlineGraph();
        using RawBaseDirectory directory = new();
        FrameAddress first;
        using (Repository repository = Repository.CreateNew(directory.Path, fixture.Models(), schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared)) {
            using BranchCheckout checkout = repository.CreateBranch("main", (IDurableObject)fixture.NewWorld());
            first = checkout.StateRevisionAddress!.Value;
        }
        using RepositoryResources resources = RepositoryResources.OpenExisting(directory.Path);
        GraphRecordStore store = resources.GraphStore;
        StoredGraphRevision initial = store.ReadGraph(first);
        SchemaCatalogView catalog = SchemaCatalogView.Load(store, initial.SchemaRoot!.Value);
        ObjectVersionRecord owner = Assert.Single(DefaultRecords(store, first),
            record => record.ObjectId == initial.RootId.Value);
        var envelope = TypedBaseBodyCodec.Decode(owner.Body, catalog);
        byte[] body = envelope.Body;
        uint childId = Assert.Single(initial.ObjectHeads.Keys, id =>
            DefaultLayout(store, first, id).Schema?.SchemaId == "inline.graph.child");
        Assert.Equal(14, body[0]); // Envelope.Inner.X = 7, followed by the nested Child ID.
        Assert.Equal(childId, (uint)body[1]);
        uint replacement = wrongKind
            ? initial.ObjectHeads.Keys.First(id => DefaultLayout(store, first, id).Kind == ObjectStateKind.String)
            : initial.ObjectHeads.Keys.Max() + 1;
        Assert.InRange(replacement, 1u, 127u);
        body[1] = (byte)replacement; // Canonical one-byte ID; framing remains valid.
        ObjectVersionRecord invalidOwner = ObjectVersionRecord.CreateBase(initial.RootId.Value,
            TypedBaseBodyCodec.Encode(envelope.Layout, new(body), catalog));
        StoredGraphRevision invalid = store.Append(StoredGraphKind.State, first, initial.SchemaRoot,
            new PreparedGraphWrite(first, initial.RootId, [invalidOwner]));
        Assert.Equal(initial.SchemaRoot, invalid.SchemaRoot);
        Assert.Equal(initial.ObjectHeads.Keys, invalid.ObjectHeads.Keys);

        // Exact DTO reading must reject before domain Hydrate; a valid sibling is the positive control.
        fixture.Check(BusinessPersistenceAdapter.ReadRoot(store, first, fixture.Models()), 7, 11, true);
        Assert.Throws<InvalidDataException>(() => DecodedRevision.ReadStoredGraph(store, invalid.Address, fixture.Models()));
        Assert.Throws<InvalidDataException>(() => BusinessPersistenceAdapter.ReadRoot(store, invalid.Address, fixture.Models()));
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

    }

    private const string InlineGraphSource = """
        using System;
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        using Atelia.DurableGraph.Persistence;
        using Atelia.DurableGraph.Storage;
        using Atelia.FrameStore;
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

        }
        """;
}
