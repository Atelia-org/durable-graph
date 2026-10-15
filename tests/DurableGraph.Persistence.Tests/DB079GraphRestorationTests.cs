
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using Atelia.FrameStore;
using Node = Atelia.DurableGraph.Persistence.Tests.SharedReadModel.Node;
using State = Atelia.DurableGraph.Persistence.Tests.SharedReadModel.State;

namespace Atelia.DurableGraph.Persistence.Tests;

/// <summary>Ordering and identity witnesses at the common graph-restoration boundary.</summary>
public sealed class DB079GraphRestorationTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"durable-db079-restoration-{Guid.NewGuid():N}");
    private int _seedIndex;
    private static readonly ReadAmplificationBaseBudgetParameters NoRebase = new(1000000, 1);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PairCompletesBothAllocationTablesBeforeAnyHydration(bool failLastAllocation) {
        using RepositoryStorageFacade repository = RepositoryStorageFacade.CreateNew(_root);
        FrameAddress first = Seed(repository, (1, new(2, 2, 0, 10)), (2, new(1, 1, 0, 20)));
        FrameAddress second = Seed(repository, (1, new(2, 2, 0, 30)), (2, new(1, 1, 0, 40)));
        int allocations = 0, hydrations = 0;
        InvalidDataException failure = new("Last allocation of the second graph failed.");
        StateModelRegistry models = SharedReadModel.Models(allocate: () => {
            Assert.Equal(0, hydrations);
            if (++allocations == 4 && failLastAllocation) { throw failure; }
            return new Node();
        }, hydrate: _ => {
            Assert.Equal(4, allocations);
            hydrations++;
        });
        long tail = DataBytes(repository.Data);
        (Node First, Node Second)? delivered = null;

        if (failLastAllocation) {
            Assert.Same(failure, Assert.Throws<InvalidDataException>(() => delivered = ReadPair(repository, first, new(1), second, new(1), models)));
            Assert.Null(delivered);
            Assert.Equal(0, hydrations);
        } else {
            delivered = ReadPair(repository, first, new(1), second, new(1), models);
            Assert.Equal(4, hydrations);
            Assert.Equal((byte)10, delivered.Value.First.Value);
            Assert.Equal((byte)30, delivered.Value.Second.Value);
            AssertCycle(delivered.Value.First);
            AssertCycle(delivered.Value.Second);
            Assert.NotSame(delivered.Value.First, delivered.Value.Second);
            Assert.NotSame(delivered.Value.First.Next, delivered.Value.Second.Next);
        }
        Assert.Equal(4, allocations);
        Assert.Equal(tail, DataBytes(repository.Data));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EqualExactHeadWithoutEqualityProofDoesNotAuthorizeSingletonReuse(bool registerUnequalProof) {
        using RepositoryStorageFacade repository = RepositoryStorageFacade.CreateNew(_root);
        FrameAddress address = Seed(repository, (1, new(0, 0, 0, 10)));
        Node singleton = new();
        int allocations = 0, hydrations = 0;
        StateModelRegistry models = SharedReadModel.Models(
            includeEquality: registerUnequalProof, equality: static (in State _, in State _) => false,
            allocate: () => { allocations++; return singleton; }, hydrate: _ => hydrations++);
        (Node First, Node Second)? delivered = null;

        Assert.Throws<InvalidDataException>(() => delivered = ReadPair(repository, address, new(1), address, new(1), models));

        Assert.Null(delivered);
        Assert.Equal(2, allocations);
        Assert.Equal(0, hydrations);
        Assert.Equal((byte)0, singleton.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CheckpointCompletesBothAllocationTablesBeforeAnyHydration(bool failLastAllocation) {
        int allocations = 0, hydrations = 0;
        InvalidDataException failure = new("Last allocation of the previous graph failed.");
        bool fail = failLastAllocation;
        using Repository repository = Repository.CreateNew(_root, SharedReadModel.Models(allocate: () => {
            Assert.Equal(0, hydrations);
            if (++allocations == 4 && fail) { throw failure; }
            return new Node();
        }, hydrate: _ => {
            Assert.Equal(4, allocations);
            hydrations++;
        }));
        Node state = Cycle(10, 20), message = Cycle(30, 40);
        using BranchCheckout branch = repository.CreateSharedBranch("main", state, NoRebase);
        CheckpointAddress stateAddress = branch.Head;
        CheckpointAddress address = branch.CommitEvent(message, NoRebase);
        Checkpoint? delivered = null;

        if (failLastAllocation) {
            Assert.Same(failure, Assert.Throws<InvalidDataException>(() => delivered = repository.ReadCheckpoint(address)));
            Assert.Null(delivered);
            Assert.Equal(4, allocations);
            Assert.Equal(0, hydrations);
            Assert.Equal(address, repository.GetHead("main"));
            Assert.False(repository.IsFaulted);
            fail = false;
            allocations = 0;
        }
        EventCheckpoint checkpoint = Assert.IsType<EventCheckpoint>(repository.ReadCheckpoint(address));
        Node restoredEvent = Assert.IsType<Node>(checkpoint.Event);
        Node restoredState = Assert.IsType<Node>(checkpoint.PreviousState);
        Assert.Equal(4, allocations);
        Assert.Equal(4, hydrations);
        Assert.Equal(stateAddress, checkpoint.PreviousStateAddress);
        Assert.Equal((byte)10, restoredState.Value);
        Assert.Equal((byte)30, restoredEvent.Value);
        AssertCycle(restoredState);
        AssertCycle(restoredEvent);
        Assert.NotSame(state, restoredState);
        Assert.NotSame(message, restoredEvent);
        Assert.NotSame(restoredEvent.Next, restoredState.Next);
    }

    [Fact]
    public void CheckpointSingletonCollisionRejectsBeforeHydratingEitherGraph() {
        Node singleton = new();
        int allocations = 0, hydrations = 0;
        using Repository repository = Repository.CreateNew(_root, SharedReadModel.Models(
            allocate: () => { allocations++; return singleton; }, hydrate: _ => hydrations++));
        using BranchCheckout branch = repository.CreateSharedBranch("main", new Node { Value = 10 }, NoRebase);
        CheckpointAddress address = branch.CommitEvent(new Node { Value = 20 }, NoRebase);
        Checkpoint? delivered = null;

        Assert.Throws<InvalidDataException>(() => delivered = repository.ReadCheckpoint(address));

        Assert.Null(delivered);
        Assert.Equal(2, allocations);
        Assert.Equal(0, hydrations);
        Assert.Equal((byte)0, singleton.Value);
        Assert.False(repository.IsFaulted);
        Assert.Equal(address, repository.GetHead("main"));
    }

    [Fact]
    public void CheckpointPreviousGraphPreparationFailsBeforeEitherGraphAllocates() {
        using (Repository seed = Repository.CreateNew(_root, SharedReadModel.Models())) {
            using BranchCheckout branch = seed.CreateSharedBranch("main", new Node { Value = 10 }, NoRebase);
            branch.CommitEvent(new Node { Value = 30 }, NoRebase);
        }
        int allocations = 0, hydrations = 0;
        bool failPrevious = true;
        List<byte> normalized = [];
        InvalidDataException failure = new("Previous State upgrade failed.");
        // Use a real stored-v1/current-v2 upgrade: exact-current reads bypass Normalize.
        using Repository repository = Repository.OpenExisting(_root, SharedReadModel.Models(currentVersion: 2,
            normalize: row => {
                State state = row.GetState<State>();
                normalized.Add(state.Value);
                if (state.Value == 10 && failPrevious) { throw failure; }
                return state;
            }, allocate: () => { allocations++; return new Node(); }, hydrate: _ => hydrations++));
        CheckpointAddress address = repository.GetHead("main");
        Checkpoint? delivered = null;

        Assert.Same(failure, Assert.Throws<InvalidDataException>(() => delivered = repository.ReadCheckpoint(address)));

        Assert.Null(delivered);
        Assert.Equal(new byte[] { 30, 10 }, normalized);
        Assert.Equal(0, allocations);
        Assert.Equal(0, hydrations);
        Assert.False(repository.IsFaulted);
        Assert.Equal(address, repository.GetHead("main"));
        // The current Event remains readable without preparing its PreviousState.
        Assert.Equal((byte)30, Assert.IsType<Node>(repository.ReadEvent(address)).Value);
        Assert.Equal(1, allocations);
        Assert.Equal(1, hydrations);
        failPrevious = false;
        EventCheckpoint checkpoint = Assert.IsType<EventCheckpoint>(repository.ReadCheckpoint(address));
        Assert.Equal((byte)10, Assert.IsType<Node>(checkpoint.PreviousState).Value);
        Assert.Equal((byte)30, Assert.IsType<Node>(checkpoint.Event).Value);
        Assert.Equal(3, allocations);
        Assert.Equal(3, hydrations);
    }

    private static Node Cycle(byte rootValue, byte childValue) {
        Node root = new() { Value = rootValue };
        root.Next = root.Alias = new Node { Value = childValue, Next = root, Alias = root };
        return root;
    }

    private static void AssertCycle(Node root) {
        Assert.Same(root.Next, root.Alias);
        Assert.Same(root, root.Next!.Next);
        Assert.Same(root, root.Next.Alias);
    }

    private FrameAddress Seed(RepositoryStorageFacade repository, params (uint Id, State State)[] rows) {
        SchemaCatalogView? accepted = repository.SharedSchemaRoot is { } schema ? SchemaCatalogView.Load(repository.Store, schema) : null;
        SchemaCatalogWorkspace workspace = new(repository.EnsureEditingAllowed, accepted);
        workspace.Set(SharedReadModel.Schema);
        PreparedSchemaCatalogWrite catalog = workspace.Prepare();
        string branch = $"source-{_seedIndex++}";
        ObjectVersionRecord[] records = rows.Select(row => ObjectVersionRecord.CreateBase(row.Id,
            TypedBaseBodyCodec.Encode(ObjectLayout.ForDurable(SharedReadModel.Schema), SharedReadModel.Base(row.State), catalog.View))).ToArray();
        using PendingRepositorySave pending = repository.Prepare(branch, StoredGraphKind.State,
            new(null, new(1), records), catalog.Write);
        return repository.Commit(pending, catalog.Install, prepareMeta: catalog.PrepareInstall).Business.Address;
    }
    private static (Node First, Node Second) ReadPair(RepositoryStorageFacade repository,
        FrameAddress first, ObjectId firstRoot, FrameAddress second, ObjectId secondRoot, StateModelRegistry models) {
        StateModelRegistry.FrozenConfiguration configuration = models.FreezeConfiguration();
        DecodedRevision a = DecodedRevision.ReadStoredGraph(repository.Store, first, configuration);
        DecodedRevision b = DecodedRevision.ReadStoredGraph(repository.Store, second, configuration,
            repository.Store.ReadMetadata(first).SchemaRoot == repository.Store.ReadMetadata(second).SchemaRoot ? a.BindingEnvironment : null);
        var pair = GraphReader.ReadPair(a, new(a.SourceModels!), firstRoot, b, new(b.SourceModels!), secondRoot);
        return ((Node)pair.First, (Node)pair.Second);
    }

    public void Dispose() => SharedReadModel.DeleteFixture(_root, "durable-db079-restoration-");
}
