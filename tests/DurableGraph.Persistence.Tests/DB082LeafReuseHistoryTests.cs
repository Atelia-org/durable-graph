using System.Buffers;
using System.Reflection;
using System.Runtime.CompilerServices;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.RbfSegmentStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class DB082LeafReuseHistoryTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"durable-db082-history-{Guid.NewGuid():N}");
    private static readonly ReadAmplificationBaseBudgetParameters NoRebase = new(1000000, 1);
    private const string OwnerId = "DB082MutableOwner";
    private const string LeafId = "DB082HistoricalLeaf";
    private const string IntermediateId = "DB082MiddleOnly";
    private const string MaterializationId = "DB082LeafHydration";
    private static readonly DurableSchema OwnerSchema = new(OwnerId, 1,
        new DurableFieldInfo(1, TypeTag.Int32), new DurableFieldInfo(2, TypeTag.ObjectReference, LeafId));
    private static readonly DurableSchema OldLeaf = new(LeafId, 1, new DurableFieldInfo(1, TypeTag.Int32));
    private static readonly DurableSchema CurrentLeaf = new(LeafId, 3, new DurableFieldInfo(1, TypeTag.Int32));
    private static readonly DurableSchema HydrationDependency = new(MaterializationId, 1, SchemaKind.InlineValue,
        new DurableFieldInfo(1, TypeTag.Int32));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HistoricalLeafReusePreservesFirstSaveRewriteIdentityAndStorageAcrossReopen(bool reuse) {
        Seed();
        ObjectId rootId, leafId;
        CheckpointAddress saved;
        ObjectStorageInfo savedLeafStorage;
        using (Repository repository = Repository.OpenExisting(_root, Models(current: true, new()))) {
            repository.ImmutableLeafReuseEnabled = reuse;
            GraphReadStatistics statistics = new();
            repository.RestorationStatistics = statistics;
            using BranchCheckout first = repository.Checkout("main");
            CheckpointAddress original = first.Head;
            using BranchCheckout hit = repository.Fork("saved", original);
            Root firstRoot = Assert.IsType<Root>(first.State), hitRoot = Assert.IsType<Root>(hit.State);
            Assert.NotSame(firstRoot, hitRoot);
            Assert.Equal(reuse, ReferenceEquals(firstRoot.Leaf, hitRoot.Leaf));
            Assert.Equal(reuse ? 1 : 0, repository.PreparedStateEntry!.ImmutableLeafCount);
            Assert.Equal(reuse ? 1 : 0, statistics.ReusedImmutableLeaves);
            rootId = hit.StateId!.Value;
            leafId = LeafObjectId(hit);
            Assert.Equal(first.StateId, hit.StateId);
            Assert.Equal(LeafObjectId(first), leafId);
            Assert.True(Baseline(hit).Objects[leafId].RequiresRewrite);
            ObjectStorageInfo oldLeafStorage = Baseline(hit).Objects[leafId].Storage!.Value;
            AssertStorage(repository, hit);

            hitRoot.Value = 29;
            saved = hit.CommitState(NoRebase);
            StateRevision revision = Resources(repository).States.Read(saved.RevisionAddress);
            Assert.Equal(original.RevisionAddress, revision.ParentRevisionAddress);
            Assert.Equal(ObjectVersionKind.Base, revision.LocalObjects.Single(row => row.ObjectId == leafId.Value).Kind);
            // This fixture's Delta contains a complete DTO; the planner may choose Base
            // when its framing is smaller. The changed owner must still be persisted.
            Assert.Contains(revision.LocalObjects, row => row.ObjectId == rootId.Value);
            Assert.False(Baseline(hit).Objects[leafId].RequiresRewrite);
            savedLeafStorage = Baseline(hit).Objects[leafId].Storage!.Value;
            Assert.NotEqual(oldLeafStorage.Head, savedLeafStorage.Head);
            AssertStorage(repository, hit);
            Assert.Equal(17, firstRoot.Value);
            Assert.True(Baseline(first).Objects[leafId].RequiresRewrite);

            CheckpointAddress unchanged = hit.CommitState(NoRebase);
            Assert.Empty(Resources(repository).States.Read(unchanged.RevisionAddress).LocalObjects);
            Assert.Equal(saved.RevisionAddress, Resources(repository).States.Read(unchanged.RevisionAddress).ParentRevisionAddress);
            Assert.Equal(savedLeafStorage, Baseline(hit).Objects[leafId].Storage);
            Assert.Equal(rootId, hit.StateId);
            Assert.Equal(leafId, LeafObjectId(hit));
            saved = unchanged;
        }
        using Repository reopened = Repository.OpenExisting(_root, Models(current: true, new()));
        reopened.ImmutableLeafReuseEnabled = reuse;
        Assert.Null(reopened.PreparedStateEntry);
        using BranchCheckout restored = reopened.Checkout("saved");
        Root restoredRoot = Assert.IsType<Root>(restored.State);
        Assert.Equal(saved.RevisionAddress, restored.Head.RevisionAddress);
        Assert.Equal(saved.RootId, restored.Head.RootId);
        Assert.Equal(reopened.GetHead("saved"), restored.Head);
        Assert.Equal(29, restoredRoot.Value);
        Assert.Equal(41, restoredRoot.Leaf!.Value);
        Assert.Equal(rootId, restored.StateId);
        Assert.Equal(leafId, LeafObjectId(restored));
        Assert.False(Baseline(restored).Objects[leafId].RequiresRewrite);
        Assert.Equal(savedLeafStorage, Baseline(restored).Objects[leafId].Storage);
        AssertStorage(reopened, restored);
    }

    [Fact]
    public void SuccessorRevisionWithUnchangedLeafHeadBuildsItsOwnLeafTable() {
        using Repository repository = Repository.CreateNew(_root, Models(current: true, new()),
            new() { NewStoreLayout = RbfSegmentStoreLayout.Flat });
        repository.ImmutableLeafReuseEnabled = true;
        Leaf live = new(41);
        using BranchCheckout seed = repository.CreateBranch("main", new Root { Value = 17, Leaf = live }, NoRebase);
        using BranchCheckout first = repository.Fork("first", seed.Head);
        Root firstRoot = Assert.IsType<Root>(first.State);
        Assert.NotSame(live, firstRoot.Leaf);
        PreparedStateRestoration oldEntry = repository.PreparedStateEntry!;
        ObjectId leafId = LeafObjectId(first);
        ObjectStorageInfo storage = Baseline(first).Objects[leafId].Storage!.Value;
        firstRoot.Value++;
        CheckpointAddress next = first.CommitState(NoRebase);
        Assert.Equal(storage, Baseline(first).Objects[leafId].Storage);
        Assert.Same(oldEntry, repository.PreparedStateEntry);
        using BranchCheckout nextCold = repository.Fork("next-cold", next);
        Root nextRoot = Assert.IsType<Root>(nextCold.State);
        Assert.NotSame(oldEntry, repository.PreparedStateEntry);
        Assert.NotSame(firstRoot.Leaf, nextRoot.Leaf);
        Assert.Equal(leafId, LeafObjectId(nextCold));
        Assert.Equal(storage, Baseline(nextCold).Objects[leafId].Storage);
        AssertStorage(repository, nextCold);
        using BranchCheckout nextHit = repository.Fork("next-hit", next);
        Assert.Same(nextRoot.Leaf, Assert.IsType<Root>(nextHit.State).Leaf);
        Assert.Equal(1, repository.PreparedStateEntry!.ImmutableLeafCount);
    }

    [Fact]
    public void NewRevisionBuildsNewLeafTableAndDropsOnlyObsoleteHistoricalRequirements() {
        Seed();
        Counters counts = new();
        using Repository repository = Repository.OpenExisting(_root, Models(current: true, counts));
        repository.ImmutableLeafReuseEnabled = true;
        GraphReadStatistics statistics = new();
        repository.RestorationStatistics = statistics;
        using BranchCheckout upgraded = repository.Checkout("main");
        CheckpointAddress oldAddress = upgraded.Head;
        PreparedStateRestoration oldEntry = repository.PreparedStateEntry!;
        Leaf oldLeaf = Assert.IsType<Root>(upgraded.State).Leaf!;
        Assert.Equal(1, oldEntry.ImmutableLeafCount);
        Assert.Equal(1, counts.Normalizes);
        using (BranchCheckout oldHit = repository.Fork("old-hit", oldAddress)) {
            Assert.Same(oldLeaf, Assert.IsType<Root>(oldHit.State).Leaf);
            Assert.Equal(1, counts.LeafAllocations);
            Assert.Equal(1, counts.LeafHydrations);
        }

        CheckpointAddress currentAddress = upgraded.CommitState(NoRebase);
        Assert.Same(oldEntry, repository.PreparedStateEntry); // DB-081 did not admit hot candidates.
        int misses = statistics.PreparedStateMisses, reused = statistics.ReusedImmutableLeaves;
        using BranchCheckout currentCold = repository.Fork("current-cold", currentAddress);
        PreparedStateRestoration currentEntry = repository.PreparedStateEntry!;
        Leaf currentLeaf = Assert.IsType<Root>(currentCold.State).Leaf!;
        Assert.NotSame(oldEntry, currentEntry);
        Assert.NotSame(oldLeaf, currentLeaf);
        Assert.Equal(1, currentEntry.ImmutableLeafCount);
        Assert.Equal(currentAddress, currentEntry.StateCheckpoint);
        Assert.Equal(misses + 1, statistics.PreparedStateMisses);
        Assert.Equal(reused, statistics.ReusedImmutableLeaves);
        Assert.Equal(1, counts.Normalizes);
        Assert.Equal(2, counts.LeafAllocations);
        Assert.Equal(2, counts.LeafHydrations);
        Assert.Equal(LeafObjectId(upgraded), LeafObjectId(currentCold));
        AssertStorage(repository, currentCold);

        Resources(repository).Schemas.Register(new DurableSchema(IntermediateId, 5, SchemaKind.InlineValue,
            new DurableFieldInfo(1, TypeTag.Int64)));
        using (BranchCheckout currentHit = repository.Fork("current-hit", currentAddress)) {
            Assert.Same(currentLeaf, Assert.IsType<Root>(currentHit.State).Leaf);
            Assert.Equal(reused + 1, statistics.ReusedImmutableLeaves);
            Assert.Equal(2, counts.LeafAllocations);
            Assert.Equal(2, counts.LeafHydrations);
        }
        int allocated = statistics.AllocatedObjects;
        InvalidDataException oldConflict = Assert.Throws<InvalidDataException>(() => repository.Fork("old-rejected", oldAddress));
        Assert.Contains(IntermediateId, oldConflict.Message);
        Assert.Equal(allocated, statistics.AllocatedObjects);
        Assert.Same(currentEntry, repository.PreparedStateEntry);
        Assert.DoesNotContain("old-rejected", repository.ListBranches());

        Resources(repository).Schemas.Register(new DurableSchema(MaterializationId, 1, SchemaKind.InlineValue,
            new DurableFieldInfo(1, TypeTag.Int64)));
        int normalized = counts.Normalizes;
        reused = statistics.ReusedImmutableLeaves;
        InvalidDataException currentConflict = Assert.Throws<InvalidDataException>(() => repository.Fork("current-rejected", currentAddress));
        Assert.Contains(MaterializationId, currentConflict.Message);
        Assert.Equal(normalized, counts.Normalizes);
        Assert.Equal(reused, statistics.ReusedImmutableLeaves);
        Assert.Equal(allocated, statistics.AllocatedObjects);
        Assert.Equal(2, counts.LeafHydrations);
        Assert.Same(currentEntry, repository.PreparedStateEntry);
        Assert.DoesNotContain("current-rejected", repository.ListBranches());
        Assert.False(repository.IsFaulted);
    }

    private void Seed() {
        using Repository repository = Repository.CreateNew(_root, Models(current: false, new()),
            new() { NewStoreLayout = RbfSegmentStoreLayout.Flat });
        using BranchCheckout branch = repository.CreateBranch("main", new Root { Value = 17, Leaf = new(41) }, NoRebase);
    }

    private static StateModelRegistry Models(bool current, Counters counts) {
        StateModelRegistry registry = new();
        registry.Register(new StateModelBinding<Root, RootState>(new(OwnerSchema, OwnerBody,
            static (in RootState prior, in RootState next) => new(prior != next, OwnerBody(next).Body)),
            [new StateReaderBinding<RootState>(OwnerSchema, ReadOwner,
                static (ref BinaryPayloadReader reader, in RootState _) => ReadOwner(ref reader), VisitOwner)],
            static row => row.GetState<RootState>(), static () => new(),
            static (Root root, in RootState state, ObjectReadTable objects) => {
                root.Value = state.Value;
                root.Leaf = objects.ResolveDurable<Leaf>(state.Leaf);
            }, static (root, context) => new(root.Value, context.CaptureDurable(root.Leaf, LeafId)), VisitOwner));
        registry.Register(new StateDefinitionBinding(IntermediateId, SchemaKind.InlineValue, 0, null,
            [new(IntermediateId, 5, SchemaKind.InlineValue, 0, [new(1, TypeExpr.Builtin(TypeTag.Int32))], stateTypeDefinition: typeof(int))],
            historicalValueFactory: static (schema, _) => new(new(1, TypeTag.InlineValue, inlineSchema: schema), typeof(int), typeof(Int32StateOps))));
        registry.Register(new StateDefinitionBinding(MaterializationId, SchemaKind.InlineValue, 0, null,
            [new(MaterializationId, 1, SchemaKind.InlineValue, 0, [new(1, TypeExpr.Builtin(TypeTag.Int32))], stateTypeDefinition: typeof(int))]));
        StateSchemaTemplate[] history = [
            new(LeafId, 1, SchemaKind.ReferenceObject, 0, [new(1, TypeExpr.Builtin(TypeTag.Int32))], stateTypeDefinition: typeof(int)),
            new(LeafId, 2, SchemaKind.ReferenceObject, 0, [new(1, TypeExpr.Named(IntermediateId), 5)], stateTypeDefinition: typeof(int)),
            new(LeafId, 3, SchemaKind.ReferenceObject, 0, [new(1, TypeExpr.Builtin(TypeTag.Int32))], stateTypeDefinition: typeof(int)),
        ];
        registry.Register(new StateDefinitionBinding(LeafId, SchemaKind.ReferenceObject, 0, typeof(Leaf),
            current ? history : [history[0]], currentModelFactory: (_, context) => {
                DurableSchema schema = current ? CurrentLeaf : OldLeaf;
                return new StateModelBinding<Leaf, int>(new(schema, LeafBody,
                    static (in int prior, in int next) => new(prior != next, LeafBody(next).Body)),
                    [context.ResolveReader(schema)], row => { counts.Normalizes++; return context.Normalize<int>(row, schema); },
                    () => { counts.LeafAllocations++; return new(0); },
                    (Leaf leaf, in int state, ObjectReadTable _) => {
                        context.BindSchema(HydrationDependency);
                        counts.LeafHydrations++;
                        LeafValue(leaf) = state;
                    }, static (leaf, _) => leaf.Value, static (in int _, IStateReferenceVisitor _) => { },
                    sourceReaderResolver: context.ResolveReader, isImmutableLeaf: true);
            }, historicalReaderFactory: static (schema, context) => {
                if (schema.Version == 2) { context.ResolveStoredValue(schema.Fields[0]); }
                return new StateReaderBinding<int>(schema, static (ref BinaryPayloadReader reader) => reader.ReadInt32(),
                    static (ref BinaryPayloadReader reader, in int _) => reader.ReadInt32(),
                    static (in int _, IStateReferenceVisitor _) => { });
            }, upgrades: current ? [new(LeafId, 1, Method(nameof(Keep))), new(LeafId, 2, Method(nameof(Keep)))] : []));
        return registry;
    }

    private static RootState ReadOwner(ref BinaryPayloadReader reader) => new(reader.ReadInt32(), new(reader.ReadUInt32()));
    private static void VisitOwner(in RootState state, IStateReferenceVisitor visitor) => visitor.VisitDurable(state.Leaf, LeafId);
    private static PreparedBaseBody OwnerBody(in RootState state) {
        ArrayBufferWriter<byte> bytes = new();
        BinaryPayloadWriter writer = new(bytes);
        writer.WriteInt32(state.Value);
        writer.WriteUInt32(state.Leaf.Value);
        return new(bytes.WrittenSpan);
    }
    private static PreparedBaseBody LeafBody(in int state) {
        ArrayBufferWriter<byte> bytes = new();
        BinaryPayloadWriter writer = new(bytes);
        writer.WriteInt32(state);
        return new(bytes.WrittenSpan);
    }
    private static void Keep(in int prior, out int next, UpgradeContext _) => next = prior;
    private static MethodInfo Method(string name) => typeof(DB082LeafReuseHistoryTests).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = nameof(Leaf.Value))]
    private static extern ref int LeafValue(Leaf leaf);
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;
    private static GraphResources Resources(Repository repository) => Field<GraphResources>(repository, "_resources");
    private static NormalizedRevision Baseline(BranchCheckout branch) => Field<NormalizedRevision>(branch.Workspace, "_baseline");
    private static ObjectId LeafObjectId(BranchCheckout branch) => Baseline(branch).Objects.Single(row => row.Value.Current.Schema?.SchemaId == LeafId).Key;
    private static void AssertStorage(Repository repository, BranchCheckout branch) {
        foreach ((ObjectId id, NormalizedObject row) in Baseline(branch).Objects) {
            ObjectVersionChain chain = Resources(repository).States.ReadObjectVersionChain(branch.StateRevisionAddress!.Value, id.Value);
            Assert.Equal(new ObjectStorageInfo(chain.ObjectHeadAddress, chain.ReconstructionPayloadBytes), row.Storage);
        }
    }
    private sealed class Root : IDurableObject { internal int Value; internal Leaf? Leaf; }
    private sealed class Leaf(int value) : IDurableObject { internal readonly int Value = value; }
    private readonly record struct RootState(int Value, ObjectId Leaf);
    private sealed class Counters { internal int Normalizes; internal int LeafAllocations; internal int LeafHydrations; }
    public void Dispose() => SharedReadModel.DeleteFixture(_root, "durable-db082-history-");
}
