using Atelia.DurableGraph;

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
