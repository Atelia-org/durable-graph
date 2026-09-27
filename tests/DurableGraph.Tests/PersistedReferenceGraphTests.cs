using System.Reflection;
using Atelia.DurableGraph.Build;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using Atelia.RbfSegmentStore;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Fact]
    public void PersistedReferenceGraphNewFreezeColdRestoreChildDeltaAndCyclicIslandRemoval() {
        GeneratorTestRun generated = RunGenerator(ReferenceGraphSource);
        AssertSchemaOnlyCompiles(generated);
        ReferenceGraphFixture fixture = new(EmitAndLoad(generated.OutputCompilation));
        using RawBaseDirectory directory = new();
        using RawBaseDirectory schemaDirectory = new();
        Directory.CreateDirectory(schemaDirectory.Path);
        string schemaPath = Path.Combine(schemaDirectory.Path, "schemas.rbf");
        RbfSegmentStoreOptions options = new() { NewStoreLayout = RbfSegmentStoreLayout.Flat, SegmentSizeThresholdBytes = 8 };
        FrameAddress first, changed, detached;
        ObjectId worldId;
        uint characterId;
        uint[] allIds;
        using (var schemaFile = RbfFile.CreateNew(schemaPath))
        using (SegmentStore segments = SegmentStore.CreateNew(directory.Path, options)) {
            SchemaStore schemas = new(schemaFile);
            using StateRevisionStore store = new(segments);
            object world = fixture.Create();
            FixturePreparedWorldRevision initial = fixture.PrepareNew(store, schemas, world);
            Assert.Null(initial.Revision.ParentRevisionAddress);
            Assert.All(initial.Revision.LocalObjects, row => Assert.Equal(ObjectVersionKind.Base, row.Kind));
            worldId = initial.WorldId;
            characterId = FindGraphObject(schemas, initial.Revision, "reference.character");
            allIds = initial.Revision.LocalObjectIds.ToArray();
            Assert.Equal(5, allIds.Length); // World, Character, Item, shared nonempty string, Empty.
            fixture.ChangeNew(world, 999);
            first = store.Append(initial.Revision);
        }

        using (var schemaFile = RbfFile.OpenExisting(schemaPath))
        using (SegmentStore segments = SegmentStore.OpenExisting(directory.Path, options)) {
            SchemaStore schemas = new(schemaFile);
            using StateRevisionStore store = new(segments);
            object loaded = fixture.Load(store, schemas, first, worldId);
            fixture.Check(loaded, 7);
            Assert.Empty(fixture.Prepare(loaded).Revision.LocalObjects);
            fixture.Change(loaded, 8);
            FixturePreparedWorldRevision update = fixture.Prepare(loaded);
            Assert.Equal(first, update.Revision.ParentRevisionAddress);
            ObjectVersionRecord childDelta = Assert.Single(update.Revision.LocalObjects);
            Assert.Equal(characterId, childDelta.ObjectId);
            Assert.Equal(ObjectVersionKind.Delta, childDelta.Kind);
            Assert.Empty(update.Revision.RemovedObjectIds);
            changed = store.Append(update.Revision);
            FixturePreparedWorldRevision repeated = fixture.Prepare(loaded);
            Assert.Equal(first, repeated.Revision.ParentRevisionAddress);
            Assert.Equal(childDelta.Body.ToArray(), Assert.Single(repeated.Revision.LocalObjects).Body.ToArray());
        }

        using (var schemaFile = RbfFile.OpenExisting(schemaPath))
        using (SegmentStore segments = SegmentStore.OpenExisting(directory.Path, options)) {
            SchemaStore schemas = new(schemaFile);
            using StateRevisionStore store = new(segments);
            object loaded = fixture.Load(store, schemas, changed, worldId);
            fixture.Check(loaded, 8);
            fixture.Detach(loaded);
            FixturePreparedWorldRevision removal = fixture.Prepare(loaded);
            Assert.Equal(changed, removal.Revision.ParentRevisionAddress);
            Assert.Equal(ObjectHeadMapKind.Base, removal.Revision.ObjectHeadMapKind);
            Assert.Empty(removal.Revision.ExternalObjectHeads);
            ObjectVersionRecord worldChange = Assert.Single(removal.Revision.LocalObjects);
            Assert.Equal(worldId.Value, worldChange.ObjectId);
            detached = store.Append(removal.Revision);
            Assert.Equal<uint>([worldId.Value], store.ReadLiveObjectHeadMap(detached).Keys);
        }

        using (var schemaFile = RbfFile.OpenExisting(schemaPath))
        using (SegmentStore segments = SegmentStore.OpenExisting(directory.Path, options)) {
            SchemaStore schemas = new(schemaFile);
            using StateRevisionStore store = new(segments);
            fixture.Check(fixture.Load(store, schemas, detached, worldId), -1);
            fixture.Check(fixture.Load(store, schemas, first, worldId), 7);
            fixture.Check(fixture.Load(store, schemas, changed, worldId), 8);
            Assert.Equal(allIds.Order(), store.ReadLiveObjectHeadMap(first).Keys.Order());
            Assert.Equal(2, store.ReadObjectVersionChain(changed, characterId).Records.Count);
            Assert.Single(store.ReadObjectVersionChain(changed, worldId.Value).Records);
            Assert.NotEqual(first.FileNumber, detached.FileNumber);
        }
    }

    [Fact]
    public void PersistedReferenceGraphUnregisteredReplacementFailsWithoutAdvancingLoadedParent() {
        GeneratorTestRun generated = RunGenerator(ReferenceGraphSource);
        AssertSchemaOnlyCompiles(generated);
        Assembly assembly = EmitAndLoad(generated.OutputCompilation);
        ReferenceGraphFixture fixture = new(assembly);
        var replaceUnknown = assembly.GetType("FusedDelta.Host")!.GetMethod("ReplaceUnknown")!
            .CreateDelegate<Func<object, object>>();
        var restoreChild = assembly.GetType("FusedDelta.Host")!.GetMethod("RestoreChild")!
            .CreateDelegate<Action<object, object>>();
        using RawBaseDirectory directory = new();
        using RawBaseDirectory schemaDirectory = new();
        Directory.CreateDirectory(schemaDirectory.Path);
        using var schemaFile = RbfFile.CreateNew(Path.Combine(schemaDirectory.Path, "schemas.rbf"));
        using SegmentStore segments = SegmentStore.CreateNew(directory.Path);
        SchemaStore schemas = new(schemaFile);
        using StateRevisionStore store = new(segments);
        FixturePreparedWorldRevision initial = fixture.PrepareNew(store, schemas, fixture.Create());
        FrameAddress first = store.Append(initial.Revision);
        object loaded = fixture.Load(store, schemas, first, initial.WorldId);
        object previous = replaceUnknown(loaded);
        Assert.Throws<InvalidOperationException>(() => fixture.Prepare(loaded));
        restoreChild(loaded, previous);
        FixturePreparedWorldRevision recovered = fixture.Prepare(loaded);
        Assert.Equal(first, recovered.Revision.ParentRevisionAddress);
        Assert.Empty(recovered.Revision.LocalObjects);
        Assert.Empty(recovered.Revision.RemovedObjectIds);
        fixture.Check(loaded, 7);
    }

    [Fact]
    public void PersistedReferenceGraphRetargetUsesNewBaseObjectsAndRemovesPreviousIsland() {
        GeneratorTestRun generated = RunGenerator(ReferenceGraphSource);
        AssertSchemaOnlyCompiles(generated);
        Assembly assembly = EmitAndLoad(generated.OutputCompilation);
        ReferenceGraphFixture fixture = new(assembly);
        var replaceKnown = assembly.GetType("FusedDelta.Host")!.GetMethod("ReplaceKnown")!
            .CreateDelegate<Action<object, int>>();
        using RawBaseDirectory directory = new();
        using RawBaseDirectory schemaDirectory = new();
        Directory.CreateDirectory(schemaDirectory.Path);
        using var schemaFile = RbfFile.CreateNew(Path.Combine(schemaDirectory.Path, "schemas.rbf"));
        using SegmentStore segments = SegmentStore.CreateNew(directory.Path);
        SchemaStore schemas = new(schemaFile);
        using StateRevisionStore store = new(segments);
        FixturePreparedWorldRevision initial = fixture.PrepareNew(store, schemas, fixture.Create());
        uint previousCharacter = FindGraphObject(schemas, initial.Revision, "reference.character");
        uint previousItem = FindGraphObject(schemas, initial.Revision, "reference.item");
        uint sourceMaximum = initial.Revision.LocalObjectIds.Max();
        FrameAddress first = store.Append(initial.Revision);
        object loaded = fixture.Load(store, schemas, first, initial.WorldId);
        replaceKnown(loaded, 42);
        FixturePreparedWorldRevision replacement = fixture.Prepare(loaded);
        Assert.Equal(first, replacement.Revision.ParentRevisionAddress);
        Assert.Contains(previousCharacter, replacement.Revision.RemovedObjectIds);
        Assert.Contains(previousItem, replacement.Revision.RemovedObjectIds);
        Assert.Equal(3, replacement.Revision.RemovedObjectIds.Count); // Old Character, Item and distinct name string.
        Assert.Single(replacement.Revision.LocalObjects, row => row.ObjectId == initial.WorldId.Value);
        ObjectVersionRecord[] newRows = replacement.Revision.LocalObjects.Where(row => row.ObjectId != initial.WorldId.Value).ToArray();
        Assert.Equal(3, newRows.Length);
        Assert.All(newRows, row => {
            Assert.True(row.ObjectId > sourceMaximum);
            Assert.Equal(ObjectVersionKind.Base, row.Kind);
        });
        FrameAddress changed = store.Append(replacement.Revision);
        fixture.Check(fixture.Load(store, schemas, changed, initial.WorldId), 42);
        fixture.Check(fixture.Load(store, schemas, first, initial.WorldId), 7);
        Assert.Equal(5, store.ReadLiveObjectHeadMap(changed).Count);
    }

    [Fact]
    public void PersistedReferenceGraphHistoricalMultiDeltaUpgradeKeepsIdsAndForcesOnlyChangedSchemaBase() {
        using AncestryHistoryDirectory history = new();
        GeneratorTestRun oldRun = RunGenerator(ReferenceHistorySource(1));
        AssertSchemaOnlyCompiles(oldRun);
        new SchemaHistoryTool().Publish(history.WriteManifest(oldRun), history.History);
        ReferenceGraphFixture old = new(EmitAndLoad(oldRun.OutputCompilation));
        GeneratorTestRun currentRun = RunGenerator(ReferenceHistorySource(2), history.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(currentRun);
        Assembly currentAssembly = EmitAndLoad(currentRun.OutputCompilation);
        ReferenceGraphFixture current = new(currentAssembly);
        var upgradeCalls = currentAssembly.GetType("FusedDelta.Host")!.GetMethod("UpgradeCalls")!.CreateDelegate<Func<int>>();
        using RawBaseDirectory directory = new();
        using RawBaseDirectory schemaDirectory = new();
        Directory.CreateDirectory(schemaDirectory.Path);
        string schemaPath = Path.Combine(schemaDirectory.Path, "schemas.rbf");
        FrameAddress first, second, third, rewritten;
        ObjectId worldId;
        uint nodeId;
        uint[] ids;
        using (var schemaFile = RbfFile.CreateNew(schemaPath))
        using (SegmentStore segments = SegmentStore.CreateNew(directory.Path)) {
            SchemaStore schemas = new(schemaFile);
            using StateRevisionStore store = new(segments);
            FixturePreparedWorldRevision initial = old.PrepareNew(store, schemas, old.Create());
            worldId = initial.WorldId;
            nodeId = FindGraphObject(schemas, initial.Revision, "reference.history.node");
            ids = initial.Revision.LocalObjectIds.ToArray();
            first = store.Append(initial.Revision);
            object loaded = old.Load(store, schemas, first, worldId);
            old.Change(loaded, 8);
            FixturePreparedWorldRevision change1 = old.Prepare(loaded);
            Assert.Equal(ObjectVersionKind.Delta, Assert.Single(change1.Revision.LocalObjects).Kind);
            second = store.Append(change1.Revision);
            loaded = old.Load(store, schemas, second, worldId);
            old.Change(loaded, 9);
            FixturePreparedWorldRevision change2 = old.Prepare(loaded);
            Assert.Equal(ObjectVersionKind.Delta, Assert.Single(change2.Revision.LocalObjects).Kind);
            third = store.Append(change2.Revision);
        }
        using (var schemaFile = RbfFile.OpenExisting(schemaPath))
        using (SegmentStore segments = SegmentStore.OpenExisting(directory.Path)) {
            SchemaStore schemas = new(schemaFile);
            using StateRevisionStore store = new(segments);
            Assert.Equal(3, store.ReadObjectVersionChain(third, nodeId).Records.Count);
            object loaded = current.Load(store, schemas, third, worldId);
            current.Check(loaded, 9);
            Assert.Equal(1, upgradeCalls());
            FixturePreparedWorldRevision rewrite = current.Prepare(loaded);
            Assert.Equal(third, rewrite.Revision.ParentRevisionAddress);
            Assert.Empty(rewrite.Revision.RemovedObjectIds);
            ObjectVersionRecord record = Assert.Single(rewrite.Revision.LocalObjects);
            Assert.Equal(nodeId, record.ObjectId);
            Assert.Equal(ObjectVersionKind.Base, record.Kind);
            Assert.Equal(2, BaseObjectBodyCodec.Decode(record.Body, schemas).Layout.Schema!.Version);
            rewritten = store.Append(rewrite.Revision);
            Assert.Equal(ids.Order(), store.ReadLiveObjectHeadMap(rewritten).Keys.Order());
            Assert.Single(store.ReadObjectVersionChain(rewritten, nodeId).Records);
        }
        using (var schemaFile = RbfFile.OpenExisting(schemaPath))
        using (SegmentStore segments = SegmentStore.OpenExisting(directory.Path)) {
            SchemaStore schemas = new(schemaFile);
            using StateRevisionStore store = new(segments);
            int previousUpgrades = upgradeCalls();
            object loaded = current.Load(store, schemas, rewritten, worldId);
            current.Check(loaded, 9);
            Assert.Equal(previousUpgrades, upgradeCalls());
            Assert.Empty(current.Prepare(loaded).Revision.LocalObjects);
            current.Check(current.Load(store, schemas, first, worldId), 7);
            current.Check(current.Load(store, schemas, third, worldId), 9);
            old.Check(old.Load(store, schemas, third, worldId), 9);
        }
    }

    private static uint FindGraphObject(SchemaStore schemas, StateRevision revision, string schemaId) => Assert.Single(
        revision.LocalObjects, row => BaseObjectBodyCodec.Decode(row.Body, schemas).Layout.Schema?.SchemaId == schemaId).ObjectId;

    private sealed class ReferenceGraphFixture(Assembly assembly) {
        private readonly Type _host = assembly.GetType("FusedDelta.Host")!;
        public object Create() => _host.GetMethod("Create")!.CreateDelegate<Func<object>>()();
        public FixturePreparedWorldRevision PrepareNew(StateRevisionStore store, SchemaStore schemas, object world) =>
            _host.GetMethod("PrepareNew")!.CreateDelegate<Func<StateRevisionStore, SchemaStore, object, FixturePreparedWorldRevision>>()(store, schemas, world);
        public object Load(StateRevisionStore store, SchemaStore schemas, FrameAddress address, ObjectId worldId) =>
            _host.GetMethod("Load")!.CreateDelegate<Func<StateRevisionStore, SchemaStore, FrameAddress, ObjectId, object>>()(store, schemas, address, worldId);
        public FixturePreparedWorldRevision Prepare(object loaded) => _host.GetMethod("Prepare")!.CreateDelegate<Func<object, FixturePreparedWorldRevision>>()(loaded);
        public void Check(object loaded, int expected) => _host.GetMethod("Check")!.CreateDelegate<Action<object, int>>()(loaded, expected);
        public void Change(object loaded, int value) => _host.GetMethod("Change")!.CreateDelegate<Action<object, int>>()(loaded, value);
        public void ChangeNew(object world, int value) => _host.GetMethod("ChangeNew")!.CreateDelegate<Action<object, int>>()(world, value);
        public void Detach(object loaded) => _host.GetMethod("Detach")!.CreateDelegate<Action<object>>()(loaded);
    }

    private const string ReferenceGraphHost = """
        public static object Create() => new World();
        public static Atelia.DurableGraph.Tests.FixturePreparedWorldRevision PrepareNew(
            Atelia.DurableGraph.Storage.StateRevisionStore store,
            Atelia.DurableGraph.Persistence.SchemaStore schemas, object world) =>
            Atelia.DurableGraph.Tests.FixtureLoadedWorld.PrepareNew(store, schemas, (World)world, Models(), new(1000000, 1));
        public static object Load(Atelia.DurableGraph.Storage.StateRevisionStore store,
            Atelia.DurableGraph.Persistence.SchemaStore schemas, Atelia.DurableGraph.Storage.FrameAddress address, ObjectId worldId) {
            World.ConstructorCalls = 0;
            return Atelia.DurableGraph.Tests.FixtureLoadedWorld.Load<World>(store, schemas, address, worldId, Models());
        }
        public static Atelia.DurableGraph.Tests.FixturePreparedWorldRevision Prepare(object loaded) =>
            ((Atelia.DurableGraph.Tests.FixtureLoadedWorld<World>)loaded).Prepare(new(1000000, 1));
        public static void Check(object loaded, int expected) => ((Atelia.DurableGraph.Tests.FixtureLoadedWorld<World>)loaded).World.Check(expected);
        public static void Change(object loaded, int value) => ((Atelia.DurableGraph.Tests.FixtureLoadedWorld<World>)loaded).World.Change(value);
        public static void ChangeNew(object world, int value) => ((World)world).Change(value);
        public static void Detach(object loaded) => ((Atelia.DurableGraph.Tests.FixtureLoadedWorld<World>)loaded).World.Detach();
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
            private static Atelia.DurableGraph.Persistence.StateModelRegistry Models() {
                var models = new Atelia.DurableGraph.Persistence.StateModelRegistry();
                World.__DurableState.RegisterModel(models); Entity.__DurableState.RegisterModel(models);
                Character.__DurableState.RegisterModel(models); Item.__DurableState.RegisterModel(models);
                return models;
            }
            public static object ReplaceUnknown(object loaded) => ((Atelia.DurableGraph.Tests.FixtureLoadedWorld<World>)loaded).World.ReplaceUnknown();
            public static void ReplaceKnown(object loaded, int value) => ((Atelia.DurableGraph.Tests.FixtureLoadedWorld<World>)loaded).World.ReplaceKnown(value);
            public static void RestoreChild(object loaded, object saved) {
                ((Atelia.DurableGraph.Tests.FixtureLoadedWorld<World>)loaded).World.RestoreChild(saved);
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
            private static Atelia.DurableGraph.Persistence.StateModelRegistry Models() {
                var models = new Atelia.DurableGraph.Persistence.StateModelRegistry();
                World.__DurableState.RegisterModel(models); Node.__DurableState.RegisterModel(models);
                return models;
            }
        """ + ReferenceGraphHost + "\n}";
}
