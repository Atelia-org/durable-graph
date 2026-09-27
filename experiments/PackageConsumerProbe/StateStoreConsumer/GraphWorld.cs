using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using Atelia.RbfSegmentStore;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;

namespace StateStorePackageConsumerProbe;

[DurableType("package.graph-world", 1)]
public sealed partial class GraphWorld : IDurableObject {
    [DurableField(1)] private GraphEntity? _primary;
    [DurableField(2)] private GraphEntity? _alias;

    public GraphWorld(GraphCharacter character) {
        GraphConstruction.Count++;
        _primary = character;
        _alias = character;
    }

    private void Disconnect() { _primary = null; _alias = null; }

    internal static void Exercise(string directory) {
        RbfSegmentStoreOptions options = new() { NewStoreLayout = RbfSegmentStoreLayout.Flat };
        ReadAmplificationBaseBudgetParameters policy = new(int.MaxValue, 1);
        StateModelRegistry models = new();
        __DurableState.RegisterModel(models);
        GraphCharacter.__DurableState.RegisterModel(models);
        GraphItem.__DurableState.RegisterModel(models);
        ObjectId worldId;
        FrameAddress initialRevision, childRevision, removedRevision;
        string shared = new('G', 1);
        GraphCharacter character = new(shared, 7);
        GraphWorld world = new(character);
        GraphItem item = character.Item;
        int constructed = GraphConstruction.Count;
        using (var repository = EventHistoryRepository.CreateNew(directory, options))
        using (var session = repository.CreateBranch("main", world, models, policy)) {
            worldId = session.StateId;
            initialRevision = session.StateRevisionAddress;
            session.CommitDomainEvent(repository.ReadState<GraphWorld>(session.Head, models)._primary!, policy);
            character.Score = 8;
            childRevision = session.CommitDomainState(policy).RevisionAddress;
            Require(ReferenceEquals(session.State, world) && ReferenceEquals(world._primary, character) &&
                ReferenceEquals(character.Item, item) && ReferenceEquals(item.Owner, character) &&
                item.Cache == 17 && GraphConstruction.Count == constructed,
                "Continuous commits must preserve application instances, cycles and transient fields.");
        }
        using (var repository = EventHistoryRepository.OpenExisting(directory, options))
        using (var session = repository.Resume<GraphWorld>("main", models)) {
            Require(session.StateRevisionAddress == childRevision, "Resume selected an older State.");
            AssertGraph(session.State, 8, constructed);
            GraphWorld original = session.State;
            session.CommitDomainEvent(repository.ReadState<GraphWorld>(session.Head, models)._primary!, policy);
            original.Disconnect();
            removedRevision = session.CommitDomainState(policy).RevisionAddress;
            Require(ReferenceEquals(session.State, original), "Resumed commit replaced the application root.");
        }
        using (var repository = EventHistoryRepository.OpenReadOnlyExisting(directory, options)) {
            var frames = repository.ReadFrames("main").ToArray();
            GraphWorld removed = repository.ReadState<GraphWorld>(frames[^1], models);
            Require(removed._primary is null && removed._alias is null, "Removed graph was not restored.");
            AssertGraph(repository.ReadState<GraphWorld>(frames[0], models), 7, constructed);
            AssertGraph(repository.ReadState<GraphWorld>(frames[2], models), 8, constructed);
            var pair = repository.ReadPair<GraphCharacter, GraphWorld>(frames[1], frames[2], models);
            Require(pair.First.Score == 7 && ((GraphCharacter)pair.Second._primary!).Score == 8,
                "Pair must restore each selected version.");
        }
        using var schemaFile = RbfFile.OpenReadOnlyExisting(Path.Combine(directory, "schemas.rbf"));
        SchemaStore schemas = new(schemaFile, readOnly: true);
        using SegmentStore segments = SegmentStore.OpenReadOnlyExisting(Path.Combine(directory, "state"), options);
        using StateRevisionStore store = new(segments);
        StateRevision initial = store.Read(initialRevision);
        Require(initial.ParentRevisionAddress is null && initial.LocalObjects.Count == 4 &&
            initial.LocalObjects.All(record => record.Kind == ObjectVersionKind.Base) && schemas.Count == 4,
            "Initial graph must preserve all exact Schemas and four Base objects.");
        StateRevision changed = store.Read(childRevision);
        ObjectVersionRecord delta = changed.LocalObjects.Single();
        Require(delta.ObjectId != worldId.Value && delta.Kind == ObjectVersionKind.Delta &&
            changed.RemovedObjectIds.Count == 0 && changed.ParentRevisionAddress == initialRevision &&
            store.ReadLiveObjectHeadMap(childRevision)[worldId.Value] == initialRevision &&
            store.ReadObjectVersionChain(childRevision, delta.ObjectId).Records.Count == 2,
            "A child-only edit must extend only the child content chain against S0.");
        StateRevision removedRevisionData = store.Read(removedRevision);
        Require(removedRevisionData.LocalObjects.Single().ObjectId == worldId.Value &&
            store.ReadLiveObjectHeadMap(removedRevision).Keys.SequenceEqual(new[] { worldId.Value }),
            "Disconnecting the final World paths must remove the cyclic island and its string.");
        Console.WriteLine("EventHistoryContinuousCommit:True");
    }

    private static void AssertGraph(GraphWorld world, int score, int constructed) {
        Require(world._primary is GraphCharacter && ReferenceEquals(world._primary, world._alias),
            "A nominal base slot must restore the exact registered derived instance and sharing.");
        GraphCharacter character = (GraphCharacter)world._primary!;
        Require(character.Score == score && ReferenceEquals(character.Item.Owner, character) &&
            ReferenceEquals(character.Item.Self, character.Item), "Readonly mutual/self references lost object identity.");
        Require(character.CreatedAtTicks == 638_625_600_000_000_000, "A child edit changed the restored creation timestamp.");
        Require(ReferenceEquals(character.Name, character.Item.Label) && character.Name == "G",
            "The inherited readonly name and Item label must share one string instance.");
        Require(character.Item.Cache == 0 && GraphConstruction.Count == constructed,
            "Graph restoration must not run constructors or transient field initializers.");
    }

    private static void Require(bool condition, string message) {
        if (!condition) { throw new InvalidOperationException(message); }
    }
}

[DurableType("package.graph-entity", 1)]
public abstract partial class GraphEntity : IDurableObject {
    [DurableField(1)] private readonly string _name;
    protected GraphEntity(string name) { GraphConstruction.Count++; _name = name; }
    public string Name => _name;
}

[DurableType("package.graph-character", 1)]
public sealed partial class GraphCharacter : GraphEntity {
    [DurableField(1)] private int _score;
    [DurableField(2)] private readonly GraphItem _item;
    [DurableField(3)] private readonly ulong _createdAtTicks;
    public GraphCharacter(string name, int score) : base(name) {
        GraphConstruction.Count++;
        _score = score;
        _item = new GraphItem(this, name);
        _createdAtTicks = 638_625_600_000_000_000;
    }
    public int Score { get => _score; set => _score = value; }
    public GraphItem Item => _item;
    public ulong CreatedAtTicks => _createdAtTicks;
}

[DurableType("package.graph-item", 1)]
public sealed partial class GraphItem : IDurableObject {
    [DurableField(1)] private readonly GraphEntity _owner;
    [DurableField(2)] private readonly GraphItem _self;
    [DurableField(3)] private readonly string _label;
    [Transient] private int _cache = 17;
    public GraphItem(GraphEntity owner, string label) {
        GraphConstruction.Count++;
        _owner = owner; _self = this; _label = label;
    }
    public GraphEntity Owner => _owner;
    public GraphItem Self => _self;
    public string Label => _label;
    public int Cache => _cache;
}

internal static class GraphConstruction {
    internal static int Count;
}
