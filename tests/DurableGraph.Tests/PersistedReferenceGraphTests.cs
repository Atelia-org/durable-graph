using System.Reflection;
using Atelia.DurableGraph.Build;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Persistence.MetaBootstrapP0;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Fact]
    public void PersistedReferenceGraphNewFreezeColdRestoreChildDeltaAndCyclicIslandRemoval() {
        GeneratorTestRun generated = RunGenerator(ReferenceGraphSource);
        AssertSchemaOnlyCompiles(generated);
        ReferenceGraphFixture fixture = new(EmitAndLoad(generated.OutputCompilation));
        using RawBaseDirectory directory = new();
        const long rotationThresholdBytes = 128;
        FrameAddress first, changed, detached;
        ObjectId worldId;
        uint characterId;
        uint[] allIds;
        using (Repository repository = Repository.CreateNew(directory.Path, fixture.Models(), rotationThresholdBytes)) {
            object world = fixture.Create();
            using BranchCheckout branch = repository.CreateBranch("main", (IDurableObject)world, ReferenceGraphNoRebase);
            first = branch.Head.RevisionAddress;
            worldId = branch.StateId!.Value;
            P0GraphStore store = ReferenceGraphStore(repository);
            Assert.Null(store.ReadGraph(first).SourceGraph);
            Assert.All(DefaultRecords(store, first), row => Assert.Equal(ObjectVersionKind.Base, row.Kind));
            characterId = FindGraphObject(store, first, "reference.character");
            allIds = store.ReadGraph(first).Objects.Keys.ToArray();
            Assert.Equal(5, allIds.Length); // World, Character, Item, shared nonempty string, Empty.
            repository.CreateTag("initial", branch.Head);
            fixture.Change(world, 999); // The actual committed graph owns its prior values.
            fixture.ResetConstructors();
            fixture.Check(repository.ReadState(branch.Head), 7);
        }
        using (Repository repository = Repository.OpenExisting(directory.Path, fixture.Models(), rotationThresholdBytes)) {
            fixture.ResetConstructors();
            using BranchCheckout loaded = repository.Checkout("main");
            fixture.Check(loaded.State!, 7);
            CheckpointAddress unchanged = loaded.CommitState(ReferenceGraphNoRebase);
            P0GraphStore store = ReferenceGraphStore(repository);
            Assert.Empty(DefaultRecords(store, unchanged.Address));
            Assert.Empty(DefaultRemoved(store, unchanged.Address));
            fixture.Change(loaded.State!, 8);
            changed = loaded.CommitState(ReferenceGraphNoRebase).Address;
            Assert.Equal(unchanged.Address, store.ReadGraph(changed).SourceGraph);
            ObjectVersionRecord childDelta = Assert.Single(DefaultRecords(store, changed));
            Assert.Equal(characterId, childDelta.ObjectId);
            Assert.Equal(ObjectVersionKind.Delta, childDelta.Kind);
            Assert.Empty(DefaultRemoved(store, changed));
            Assert.Equal(2, store.ReadObjectChain(changed, characterId).Entries.Count);
            CheckpointAddress repeated = loaded.CommitState(ReferenceGraphNoRebase);
            Assert.Empty(DefaultRecords(store, repeated.Address));
            Assert.Equal(changed, store.ReadGraph(repeated.Address).SourceGraph);
            repository.CreateTag("changed", loaded.Head);
        }
        using (Repository repository = Repository.OpenExisting(directory.Path, fixture.Models(), rotationThresholdBytes)) {
            fixture.ResetConstructors();
            using BranchCheckout loaded = repository.Checkout("main");
            fixture.Check(loaded.State!, 8);
            FrameAddress parent = loaded.StateRevisionAddress!.Value;
            fixture.Detach(loaded.State!);
            detached = loaded.CommitState(ReferenceGraphNoRebase).Address;
            P0GraphStore store = ReferenceGraphStore(repository);
            Assert.Equal(parent, store.ReadGraph(detached).SourceGraph);
            ObjectVersionRecord worldChange = Assert.Single(DefaultRecords(store, detached));
            Assert.Equal(worldId.Value, worldChange.ObjectId);
            Assert.Equal(allIds.Where(id => id != worldId.Value).Order(), DefaultRemoved(store, detached));
            Assert.Equal<uint>([worldId.Value], store.ReadGraph(detached).Objects.Keys);
        }
        using (Repository repository = Repository.OpenReadOnlyExisting(directory.Path, fixture.Models())) {
            fixture.ResetConstructors();
            fixture.Check(repository.ReadState(repository.GetHead("main")), -1);
            fixture.Check(repository.ReadState(repository.ResolveTag("initial")), 7);
            fixture.Check(repository.ReadState(repository.ResolveTag("changed")), 8);
            P0GraphStore store = ReferenceGraphStore(repository);
            Assert.Equal(allIds.Order(), store.ReadGraph(first).Objects.Keys.Order());
            Assert.Equal(2, store.ReadObjectChain(changed, characterId).Entries.Count);
            Assert.Single(store.ReadObjectChain(changed, worldId.Value).Entries);
            Assert.NotEqual(first, detached);
        }
    }

    [Fact]
    public void PersistedReferenceGraphUnregisteredReplacementFailsWithoutAdvancingLoadedParent() {
        GeneratorTestRun generated = RunGenerator(ReferenceGraphSource);
        AssertSchemaOnlyCompiles(generated);
        Assembly assembly = EmitAndLoad(generated.OutputCompilation);
        ReferenceGraphFixture fixture = new(assembly);
        var replaceUnknown = assembly.GetType("FusedDelta.Host")!.GetMethod("ReplaceUnknown")!.CreateDelegate<Func<object, object>>();
        var restoreChild = assembly.GetType("FusedDelta.Host")!.GetMethod("RestoreChild")!.CreateDelegate<Action<object, object>>();
        using RawBaseDirectory directory = new();
        using Repository repository = Repository.CreateNew(directory.Path, fixture.Models());
        using BranchCheckout main = repository.CreateBranch("main", (IDurableObject)fixture.Create(), ReferenceGraphNoRebase);
        CheckpointAddress first = main.Head;
        fixture.ResetConstructors();
        using BranchCheckout loaded = repository.Fork("loaded", first);
        P0GraphStore store = ReferenceGraphStore(repository);
        long frames = store.AppendedFrames;
        object previous = replaceUnknown(loaded.State!);
        Assert.Throws<InvalidOperationException>(() => loaded.CommitState(ReferenceGraphNoRebase));
        Assert.Equal(first, loaded.Head);
        Assert.Equal(first, repository.GetHead("loaded"));
        Assert.Equal(first.Address, loaded.StateRevisionAddress);
        Assert.Equal(frames, store.AppendedFrames);
        Assert.False(repository.IsFaulted);
        restoreChild(loaded.State!, previous);
        CheckpointAddress recovered = loaded.CommitState(ReferenceGraphNoRebase);
        Assert.Equal(first.Address, store.ReadGraph(recovered.Address).SourceGraph);
        Assert.Empty(DefaultRecords(store, recovered.Address));
        Assert.Empty(DefaultRemoved(store, recovered.Address));
        fixture.Check(loaded.State!, 7);
    }

    [Fact]
    public void PersistedReferenceGraphRetargetUsesNewBaseObjectsAndRemovesPreviousIsland() {
        GeneratorTestRun generated = RunGenerator(ReferenceGraphSource);
        AssertSchemaOnlyCompiles(generated);
        Assembly assembly = EmitAndLoad(generated.OutputCompilation);
        ReferenceGraphFixture fixture = new(assembly);
        var replaceKnown = assembly.GetType("FusedDelta.Host")!.GetMethod("ReplaceKnown")!.CreateDelegate<Action<object, int>>();
        using RawBaseDirectory directory = new();
        using Repository repository = Repository.CreateNew(directory.Path, fixture.Models());
        using BranchCheckout main = repository.CreateBranch("main", (IDurableObject)fixture.Create(), ReferenceGraphNoRebase);
        CheckpointAddress first = main.Head;
        repository.CreateTag("initial", first);
        P0GraphStore store = ReferenceGraphStore(repository);
        uint previousCharacter = FindGraphObject(store, first.Address, "reference.character");
        uint previousItem = FindGraphObject(store, first.Address, "reference.item");
        uint sourceMaximum = store.ReadGraph(first.Address).Objects.Keys.Max();
        fixture.ResetConstructors();
        using BranchCheckout loaded = repository.Fork("loaded", first);
        replaceKnown(loaded.State!, 42);
        CheckpointAddress changed = loaded.CommitState(ReferenceGraphNoRebase);
        Assert.Equal(first.Address, store.ReadGraph(changed.Address).SourceGraph);
        Assert.Contains(previousCharacter, DefaultRemoved(store, changed.Address));
        Assert.Contains(previousItem, DefaultRemoved(store, changed.Address));
        Assert.Equal(3, DefaultRemoved(store, changed.Address).Count); // Old Character, Item and distinct name string.
        Assert.Single(DefaultRecords(store, changed.Address), row => row.ObjectId == first.RootId.Value);
        ObjectVersionRecord[] newRows = DefaultRecords(store, changed.Address).Where(row => row.ObjectId != first.RootId.Value).ToArray();
        Assert.Equal(3, newRows.Length);
        Assert.All(newRows, row => {
            Assert.True(row.ObjectId > sourceMaximum);
            Assert.Equal(ObjectVersionKind.Base, row.Kind);
        });
        fixture.ResetConstructors();
        fixture.Check(repository.ReadState(changed), 42);
        fixture.Check(repository.ReadState(repository.ResolveTag("initial")), 7);
        Assert.Equal(5, store.ReadGraph(changed.Address).Objects.Count);
    }

    [Fact]
    public void PersistedReferenceGraphHistoricalMultiDeltaUpgradeKeepsIdsAndForcesOnlyChangedSchemaBase() {
        using AncestryHistoryDirectory history = new();
        SchemaHistoryTool tool = new();
        GeneratorTestRun oldRun = RunGenerator(ReferenceHistorySource(1));
        AssertSchemaOnlyCompiles(oldRun);
        tool.Publish(history.WriteManifest(oldRun), history.History);
        ReferenceGraphFixture old = new(EmitAndLoad(oldRun.OutputCompilation));
        GeneratorTestRun currentRun = RunGenerator(ReferenceHistorySource(2), history.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(currentRun);
        tool.Publish(history.WriteManifest(currentRun), history.History);
        tool.Verify(history.WriteManifest(currentRun), history.History);
        Assembly currentAssembly = EmitAndLoad(currentRun.OutputCompilation);
        ReferenceGraphFixture current = new(currentAssembly);
        var upgradeCalls = currentAssembly.GetType("FusedDelta.Host")!.GetMethod("UpgradeCalls")!.CreateDelegate<Func<int>>();
        using RawBaseDirectory directory = new();
        FrameAddress first, second, third, rewritten;
        ObjectId worldId;
        uint nodeId;
        uint[] ids;
        using (Repository repository = Repository.CreateNew(directory.Path, old.Models())) {
            using BranchCheckout main = repository.CreateBranch("main", (IDurableObject)old.Create(), ReferenceGraphNoRebase);
            first = main.Head.Address;
            worldId = main.StateId!.Value;
            P0GraphStore store = ReferenceGraphStore(repository);
            nodeId = FindGraphObject(store, first, "reference.history.node");
            ids = store.ReadGraph(first).Objects.Keys.ToArray();
            repository.CreateTag("initial", main.Head);
            old.Change(main.State!, 8);
            second = main.CommitState(ReferenceGraphNoRebase).Address;
            Assert.Equal(ObjectVersionKind.Delta, Assert.Single(DefaultRecords(store, second)).Kind);
            old.Change(main.State!, 9);
            third = main.CommitState(ReferenceGraphNoRebase).Address;
            Assert.Equal(ObjectVersionKind.Delta, Assert.Single(DefaultRecords(store, third)).Kind);
            repository.CreateTag("old-latest", main.Head);
        }
        using (Repository repository = Repository.OpenExisting(directory.Path, current.Models())) {
            current.ResetConstructors();
            P0GraphStore store = ReferenceGraphStore(repository);
            Assert.Equal(3, store.ReadObjectChain(third, nodeId).Entries.Count);
            using BranchCheckout loaded = repository.Checkout("main");
            current.Check(loaded.State!, 9);
            Assert.Equal(1, upgradeCalls());
            Assert.Equal(worldId, loaded.StateId);
            rewritten = loaded.CommitState(ReferenceGraphNoRebase).Address;
            Assert.Equal(third, store.ReadGraph(rewritten).SourceGraph);
            Assert.Empty(DefaultRemoved(store, rewritten));
            ObjectVersionRecord record = Assert.Single(DefaultRecords(store, rewritten));
            Assert.Equal(nodeId, record.ObjectId);
            Assert.Equal(ObjectVersionKind.Base, record.Kind);
            Assert.Equal(2, DefaultLayout(store, rewritten, nodeId).Schema!.Version);
            Assert.Equal(ids.Order(), store.ReadGraph(rewritten).Objects.Keys.Order());
            Assert.Single(store.ReadObjectChain(rewritten, nodeId).Entries);
        }
        using (Repository repository = Repository.OpenExisting(directory.Path, current.Models())) {
            current.ResetConstructors();
            int previousUpgrades = upgradeCalls();
            using BranchCheckout loaded = repository.Checkout("main");
            current.Check(loaded.State!, 9);
            Assert.Equal(previousUpgrades, upgradeCalls());
            Assert.Equal(worldId, loaded.StateId);
            CheckpointAddress unchanged = loaded.CommitState(ReferenceGraphNoRebase);
            Assert.Empty(DefaultRecords(ReferenceGraphStore(repository), unchanged.Address));
            current.Check(repository.ReadState(repository.ResolveTag("initial")), 7);
            current.Check(repository.ReadState(repository.ResolveTag("old-latest")), 9);
        }
        using (Repository repository = Repository.OpenReadOnlyExisting(directory.Path, old.Models())) {
            old.ResetConstructors();
            old.Check(repository.ReadState(repository.ResolveTag("old-latest")), 9);
        }
    }

    private static readonly ReadAmplificationBaseBudgetParameters ReferenceGraphNoRebase = new(1000000, 1);
    private static uint FindGraphObject(P0GraphStore store, FrameAddress graph, string schemaId) => Assert.Single(
        store.ReadGraph(graph).Objects.Keys, id => DefaultLayout(store, graph, id).Schema?.SchemaId == schemaId);
    private static P0GraphStore ReferenceGraphStore(Repository repository) =>
        ((GraphResources)typeof(Repository).GetField("_resources", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(repository)!).MetaBootstrapStore;

    private sealed class ReferenceGraphFixture(Assembly assembly) {
        private readonly Type _host = assembly.GetType("FusedDelta.Host")!;
        public object Create() => _host.GetMethod("Create")!.CreateDelegate<Func<object>>()();
        public StateModelRegistry Models() => _host.GetMethod("Models")!.CreateDelegate<Func<StateModelRegistry>>()();
        public void ResetConstructors() => assembly.GetType("FusedDelta.World")!.GetField("ConstructorCalls")!.SetValue(null, 0);
        public void Check(object world, int expected) => _host.GetMethod("Check")!.CreateDelegate<Action<object, int>>()(world, expected);
        public void Change(object world, int value) => _host.GetMethod("Change")!.CreateDelegate<Action<object, int>>()(world, value);
        public void Detach(object world) => _host.GetMethod("Detach")!.CreateDelegate<Action<object>>()(world);
    }

    private const string ReferenceGraphHost = """
        public static object Create() => new World();
        public static void Check(object world, int expected) => ((World)world).Check(expected);
        public static void Change(object world, int value) => ((World)world).Change(value);
        public static void Detach(object world) => ((World)world).Detach();
        """;

    private static readonly string ReferenceGraphSource = FusedDeltaPreamble + """
        [DurableType("reference.world", 1)]
        public sealed partial class World : IDurableObject {
            public static int ConstructorCalls;
            [DurableField(1)] private Entity? _child;
            [DurableField(2)] private Entity? _alias;
            [DurableField(3)] private readonly World _self;
            [Transient] private int _cache = 123;
            public World() { ConstructorCalls++; _self = this; _child = _alias = new Character(this, 7); }
            public void Change(int value) { ((Character)_child!).Change(value); }
            public void Detach() { _child = _alias = null; }
            public void ReplaceKnown(int value) { _child = _alias = new Character(this, value); }
            public object ReplaceUnknown() { var saved = _child!; _child = new Unknown(this); return saved; }
            public void RestoreChild(object saved) { _child = (Entity)saved; }
            public void Check(int expected) {
                if (ConstructorCalls != 0 || _cache != 0 || !ReferenceEquals(_self, this)) throw new Exception("World restoration ran constructor or lost self identity.");
                if (expected < 0) { if (_child != null || _alias != null) throw new Exception("Detached island returned."); return; }
                if (!ReferenceEquals(_child, _alias) || _child is not Character character) throw new Exception("Shared derived identity changed.");
                character.Check(this, expected);
            }
        }
        [DurableType("reference.entity", 1)]
        public abstract partial class Entity : IDurableObject {
            [DurableField(1)] private readonly World _world;
            protected Entity(World world) { World.ConstructorCalls++; _world = world; }
            public World Owner => _world;
        }
        [DurableType("reference.character", 1)]
        public sealed partial class Character : Entity {
            [DurableField(1)] private int _number;
            [DurableField(2)] private readonly Item _item;
            [DurableField(3)] private readonly string _name;
            [DurableField(4)] private readonly string _empty;
            [DurableField(5)] private readonly long _padding;
            public Character(World world, int number) : base(world) {
                World.ConstructorCalls++; _number = number; _name = new string('n', 4); _empty = string.Empty;
                _item = new Item(this, _name); _padding = long.MaxValue;
            }
            public void Change(int value) { _number = value; }
            public void Check(World world, int expected) {
                if (_number != expected || !ReferenceEquals(Owner, world) || !ReferenceEquals(_item.Owner, this) ||
                    !ReferenceEquals(_name, _item.Name) || !ReferenceEquals(_empty, string.Empty) || _padding != long.MaxValue)
                    throw new Exception("Readonly inherited reference, mutual cycle, content or string identity changed.");
            }
        }
        [DurableType("reference.item", 1)]
        public sealed partial class Item : IDurableObject {
            [DurableField(1)] private readonly Character _owner;
            [DurableField(2)] private readonly string _name;
            public Item(Character owner, string name) { World.ConstructorCalls++; _owner = owner; _name = name; }
            public Character Owner => _owner;
            public string Name => _name;
        }
        public sealed class Unknown : Entity { public Unknown(World world) : base(world) { } }
        public static class Host {
            public static Atelia.DurableGraph.Persistence.StateModelRegistry Models() {
                var models = new Atelia.DurableGraph.Persistence.StateModelRegistry();
                World.__DurableState.RegisterModel(models); Entity.__DurableState.RegisterModel(models);
                Character.__DurableState.RegisterModel(models); Item.__DurableState.RegisterModel(models);
                return models;
            }
            public static object ReplaceUnknown(object loaded) => ((World)loaded).ReplaceUnknown();
            public static void ReplaceKnown(object loaded, int value) => ((World)loaded).ReplaceKnown(value);
            public static void RestoreChild(object loaded, object saved) {
                ((World)loaded).RestoreChild(saved);
                World.ConstructorCalls = 0;
            }
        """ + ReferenceGraphHost + "\n}";

    private static string ReferenceHistorySource(int version) => FusedDeltaPreamble + """
        [DurableType("reference.history.world", 1)]
        public sealed partial class World : IDurableObject {
            public static int ConstructorCalls;
            [DurableField(1)] private Node? _child;
            [DurableField(2)] private readonly Node _alias;
            public World() { ConstructorCalls++; _child = _alias = new Node(this); }
            public void Change(int value) { _child!.Change(value); }
            public void Detach() { _child = null; }
            public void Check(int expected) {
                if (ConstructorCalls != 0 || !ReferenceEquals(_child, _alias)) throw new Exception("Owner identity changed or constructor ran.");
                _child!.Check(this, expected);
            }
        }
        """ + $$"""
        [DurableType("reference.history.node", {{version}})]
        public sealed partial class Node : IDurableObject {
            public static int Upgrades;
            [DurableField(1)] private int _number;
            [DurableField(2)] private readonly World _world;
            [DurableField(3)] private readonly long _padding;
            [DurableField(4)] private readonly string _name;
            public Node(World world) { World.ConstructorCalls++; _number = 7; _world = world; _padding = long.MaxValue; _name = "node"; }
            public void Change(int value) { _number = value; }
            public void Check(World world, int expected) {
                if (_number != expected || !ReferenceEquals(_world, world) || _padding != long.MaxValue || _name != "node") throw new Exception("Historical graph changed.");
                {{(version == 2 ? "if (_marker != 42) throw new Exception(\"Upgrade marker missing.\");" : "")}}
            }
            {{(version == 2 ? """
                [DurableField(5)] private readonly byte _marker;
                private static void UpgradeStateV1ToV2(in __DurableState.V1 old, out __DurableState.V2 next) {
                    Upgrades++;
                    next = new __DurableState.V2(old.Segment0Field1, old.Segment0Field2, old.Segment0Field3, old.Segment0Field4, 42);
                }
                """ : "")}}
        }
        public static class Host {
            public static int UpgradeCalls() => Node.Upgrades;
            public static Atelia.DurableGraph.Persistence.StateModelRegistry Models() {
                var models = new Atelia.DurableGraph.Persistence.StateModelRegistry();
                World.__DurableState.RegisterModel(models); Node.__DurableState.RegisterModel(models);
                return models;
            }
        """ + ReferenceGraphHost + "\n}";
}
