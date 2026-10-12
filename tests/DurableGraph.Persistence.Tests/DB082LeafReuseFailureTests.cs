using System.Buffers;
using System.Reflection;
using System.Runtime.CompilerServices;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Persistence.MetaBootstrapP0;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class DB082LeafReuseFailureTests : IDisposable {
    private const string Prefix = "durable-db082-failure-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
    private static readonly DurableSchema LeafSchema = new("DB082FailureLeaf", 1, new DurableFieldInfo(1, TypeTag.Byte));
    private static readonly DurableSchema TailSchema = new("DB082FailureTail", 1, new DurableFieldInfo(1, TypeTag.Byte));
    private static readonly DurableSchema OwnerSchema = new("DB082FailureOwner", 1,
        DurableFieldInfo.Reference(1, LeafSchema.Type), DurableFieldInfo.Reference(2, LeafSchema.Type),
        DurableFieldInfo.Reference(3, TailSchema.Type));
    private static readonly DurableSchema Dependency = new("DB082CallbackDependency", 1, SchemaKind.InlineValue,
        new DurableFieldInfo(1, TypeTag.Int32));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WholeGraphSuccessControlsColdInstallationAndDisabledToEnabledBackfill(bool backfill) {
        Controls control = new();
        using Repository repository = Create(control);
        repository.ImmutableLeafReuseEnabled = !backfill;
        using BranchCheckout main = repository.CreateBranch("main", Graph());
        PreparedStateRestoration? previous = null;
        if (backfill) {
            using BranchCheckout disabled = repository.Fork("disabled", main.Head);
            previous = repository.PreparedStateEntry;
            Assert.NotNull(previous);
            Assert.Equal(0, previous.ImmutableLeafCount);
            repository.ImmutableLeafReuseEnabled = true;
        }
        control.FailTail = true;
        int hydrated = control.LeafHydrates;
        BranchCheckout? delivered = null;
        Assert.Same(control.Failure, Assert.Throws<InvalidDataException>(() => delivered = repository.Fork("retry", main.Head)));
        Assert.Null(delivered);
        Assert.Equal(hydrated + 1, control.LeafHydrates); // The leaf actually completed before the tail failed.
        Assert.Same(previous, repository.PreparedStateEntry);
        Assert.DoesNotContain("retry", repository.ListBranches());
        Assert.False(repository.IsFaulted);

        control.FailTail = false;
        using BranchCheckout successful = repository.Fork("retry", main.Head);
        Assert.Equal(hydrated + 2, control.LeafHydrates);
        PreparedStateRestoration installed = Assert.IsType<PreparedStateRestoration>(repository.PreparedStateEntry);
        Assert.Equal(1, installed.ImmutableLeafCount);
        Leaf leaf = Assert.IsType<Owner>(successful.State).First!;
        control.FailTail = true;
        int allocations = control.LeafAllocations;
        hydrated = control.LeafHydrates;
        repository.RestorationStatistics = new();
        Assert.Throws<InvalidDataException>(() => repository.Fork("failed-hit", main.Head));
        Assert.Equal(allocations, control.LeafAllocations);
        Assert.Equal(hydrated, control.LeafHydrates);
        Assert.Equal(1, repository.RestorationStatistics.ReusedImmutableLeaves);
        Assert.Equal((byte)7, leaf.Value);
        Assert.Same(installed, repository.PreparedStateEntry);
        control.FailTail = false;
        using BranchCheckout afterFailure = repository.Fork("after-failure", main.Head);
        Assert.Same(leaf, Assert.IsType<Owner>(afterFailure.State).First);
        Assert.Equal(hydrated, control.LeafHydrates);
    }

    [Fact]
    public void AllCachedInstancesAreRegisteredBeforeAnEarlierObjectIdAllocates() {
        Controls control = new();
        using Repository repository = Create(control);
        using BranchCheckout main = repository.CreateBranch("main", new Owner { First = new(7), Second = new(8) });
        using BranchCheckout warm = repository.Fork("warm", main.Head);
        PreparedGraphSelection selection = repository.PreparedStateEntry!.Selection;
        ObjectId[] ids = selection.Reachable.Where(id => selection.Normalized.Objects[id].Model.DomainType == typeof(Leaf)).ToArray();
        Assert.Equal(2, ids.Length);
        Owner restored = Assert.IsType<Owner>(warm.State);
        Leaf cached = restored.Second!;
        Assert.Same(cached, repository.PreparedStateEntry.ImmutableLeaves![ids[1]]);
        control.ReturnSingleton = cached;
        GraphReadStatistics statistics = new();
        GraphReadContext session = new(selection.Models, statistics);
        int hydrates = control.LeafHydrates;
        // A partial trusted map isolates the ordering rule: id[0] will allocate,
        // while the later id[1] already owns the same exact-type instance.
        Assert.Throws<InvalidDataException>(() => GraphReader.RestorePreparedState(session, selection, out _,
            reusedLeaves: new Dictionary<ObjectId, object> { [ids[1]] = cached }));
        Assert.Equal(0, statistics.HydratedObjects);
        Assert.Equal(hydrates, control.LeafHydrates);
        Assert.Equal((byte)8, cached.Value);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void LeafCallbackRequirementsSurviveColdCollectionAndHitBackfill(bool duringAllocate, bool backfill) {
        Controls control = new() { DependencyDuringAllocate = duringAllocate, CheckDependency = true };
        using Repository repository = Create(control);
        using BranchCheckout main = repository.CreateBranch("main", Graph());
        CheckpointAddress original = main.Head;
        CheckpointAddress eventAddress;
        using (BranchCheckout events = repository.CreateBranchFromEvent("events", Graph(9))) { eventAddress = events.Head; }
        if (backfill) {
            repository.ImmutableLeafReuseEnabled = false;
            using BranchCheckout disabled = repository.Fork("disabled", original);
            Assert.Equal(0, repository.PreparedStateEntry!.ImmutableLeafCount);
        }
        repository.ImmutableLeafReuseEnabled = true;
        using BranchCheckout warm = repository.Fork("warm", original);
        PreparedStateRestoration entry = Assert.IsType<PreparedStateRestoration>(repository.PreparedStateEntry);
        Assert.Equal(1, entry.ImmutableLeafCount);
        Assert.True(control.DependencyChecks > 0);
        int allocations = control.LeafAllocations, hydrates = control.LeafHydrates;
        main.Catalog.Set(new DurableSchema(Dependency.SchemaId, 1, SchemaKind.InlineValue,
            new DurableFieldInfo(1, TypeTag.Int64)));
        CheckpointAddress catalogEvent = main.CommitEvent(main.State!);
        var configuration = Field<StateModelRegistry.FrozenConfiguration>(repository, "_splitConfiguration");
        var wrongEnvironment = DecodedRevision.SplitBindingEnvironment.Load(repository.MetaBootstrapStore,
            configuration, repository.MetaBootstrapStore.ReadMetadata(catalogEvent.Address).SchemaRoot!.Value);
        InvalidDataException conflict = Assert.Throws<InvalidDataException>(() => entry.Requirements.Validate(wrongEnvironment.Models));
        Assert.Contains(Dependency.SchemaId, conflict.Message);
        Assert.Equal(allocations, control.LeafAllocations);
        Assert.Equal(hydrates, control.LeafHydrates);
        repository.RestorationStatistics = new();
        // Event-only requests still bypass the State certificate and leaf table.
        using (BranchCheckout events = repository.Checkout("events")) {
            Assert.Null(events.State);
            Assert.Equal(eventAddress, events.Head);
        }
        using (BranchCheckout eventFork = repository.Fork("event-only", eventAddress)) {
            Assert.Null(eventFork.State);
            Assert.Equal(eventAddress, eventFork.Head);
        }
        Assert.Same(entry, repository.PreparedStateEntry);
        Assert.Equal(1, entry.ImmutableLeafCount);
        Assert.Equal(0, repository.RestorationStatistics.PreparedStateHits);
        Assert.Equal(0, repository.RestorationStatistics.PreparedStateMisses);
        Assert.Equal(0, repository.RestorationStatistics.AllocatedObjects);
        Assert.Equal(0, repository.RestorationStatistics.HydratedObjects);
        using BranchCheckout oldHit = repository.Fork("old-hit", original);
        Assert.Same(Assert.IsType<Owner>(warm.State).First, Assert.IsType<Owner>(oldHit.State).First);
        Assert.Equal(1, repository.RestorationStatistics.ReusedImmutableLeaves);
        Assert.Equal(0, repository.RestorationStatistics.DecodedObjects);
        Assert.Equal(allocations, control.LeafAllocations);
        Assert.Equal(hydrates, control.LeafHydrates);
        Assert.Same(entry, repository.PreparedStateEntry);

        // The actual new State selects S1. Its first complete materialization
        // must check the callback dependency and must not install a partial table.
        CheckpointAddress newer = main.CommitState();
        int reused = repository.RestorationStatistics.ReusedImmutableLeaves;
        Assert.Throws<InvalidDataException>(() => repository.Fork("rejected", newer));
        Assert.Equal(reused, repository.RestorationStatistics.ReusedImmutableLeaves);
        Assert.Equal(allocations + 1, control.LeafAllocations);
        Assert.Equal(hydrates + (duringAllocate ? 0 : 1), control.LeafHydrates);
        Assert.Same(entry, repository.PreparedStateEntry);
        Assert.DoesNotContain("rejected", repository.ListBranches());
        Assert.False(repository.IsFaulted);
    }

    [Theory]
    [InlineData("before")]
    [InlineData("after")]
    [InlineData("unknown")]
    public void PublicationFailureNeverInstallsCandidateLeafTable(string phase) {
        Controls control = new();
        using Repository repository = Create(control);
        using BranchCheckout main = repository.CreateBranch("main", Graph());
        using BranchCheckout warm = repository.Fork("warm", main.Head);
        PreparedStateRestoration previous = repository.PreparedStateEntry!;
        Leaf deliveredLeaf = Assert.IsType<Owner>(warm.State).First!;
        CheckpointAddress newer = main.CommitState(Graph(9));
        int hydrates = control.LeafHydrates;
        if (phase == "unknown") {
            InstallPublicationFault(ResourcesOf(repository).Versions, "AfterAppend", () => throw new IOException("Unknown after actual root append"));
        }
        repository.Checkpoint = point => {
            if (phase != "unknown" && point == (phase == "before" ? CommitCheckpoint.BeforePublication : CommitCheckpoint.AfterPublication)) {
                throw new IOException("Publication interruption");
            }
        };
        GraphCommitException error = Assert.Throws<GraphCommitException>(() => repository.Fork("candidate", newer));
        Assert.Equal(hydrates + 1, control.LeafHydrates);
        Assert.Equal(phase == "before" ? GraphCommitOutcome.NotPublished :
            phase == "after" ? GraphCommitOutcome.Published : GraphCommitOutcome.Unknown, error.Outcome);
        Assert.Equal((byte)7, deliveredLeaf.Value);
        repository.Checkpoint = null;
        if (phase == "before") {
            Assert.False(repository.IsFaulted);
            Assert.Same(previous, repository.PreparedStateEntry);
            Assert.DoesNotContain("candidate", repository.ListBranches());
            using BranchCheckout retry = repository.Fork("candidate", newer);
            Assert.Equal(hydrates + 2, control.LeafHydrates);
            Assert.Equal(1, repository.PreparedStateEntry!.ImmutableLeafCount);
            Assert.NotSame(deliveredLeaf, Assert.IsType<Owner>(retry.State).First);
        } else {
            Assert.True(repository.IsFaulted);
            Assert.Null(repository.PreparedStateEntry);
            Assert.Throws<InvalidOperationException>(() => repository.Fork("blocked", newer));
        }
    }

    [Theory]
    [InlineData("evict")]
    [InlineData("dispose")]
    [InlineData("fault")]
    public void SlotLifetimeReleasesItsOnlyStrongLeafReference(string finish) {
        Controls control = new();
        using Repository repository = Create(control);
        using BranchCheckout main = repository.CreateBranch("main", Graph());
        WeakReference<Leaf> leaf = WarmWeak(repository, main.Head);
        Assert.True(IsAlive(leaf));
        if (finish == "evict") {
            CheckpointAddress newer = main.CommitState(Graph(9));
            using BranchCheckout replacement = repository.Fork("replacement", newer);
        } else if (finish == "dispose") {
            repository.Dispose();
        } else {
            repository.Checkpoint = point => {
                if (point == CommitCheckpoint.AfterPublication) { throw new IOException("fault"); }
            };
            Assert.Throws<GraphCommitException>(() => repository.Fork("fault", main.Head));
            Assert.True(repository.IsFaulted);
        }
        if (finish != "evict") { Assert.Null(repository.PreparedStateEntry); }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(IsAlive(leaf));
        GC.KeepAlive(repository);
    }

    [Fact]
    public void LiveCheckoutRetainsLeafAndSavingSourceWithoutKeepingEvictedEntryAlive() {
        using Repository repository = Create(new());
        using BranchCheckout main = repository.CreateBranch("main", Graph());
        CheckpointAddress original = main.Head;
        var retained = WarmLive(repository, original);
        using BranchCheckout live = retained.Checkout;
        Owner root = Assert.IsType<Owner>(live.State);
        Leaf leaf = root.First!;
        CheckpointAddress newer = main.CommitState(Graph(9));
        using BranchCheckout replacement = repository.Fork("replacement", newer);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(EntryIsAlive(retained.Entry));
        Assert.Equal((byte)7, leaf.Value);
        Assert.Same(leaf, root.First);

        root.Second = new Leaf(11);
        CheckpointAddress saved = live.CommitState();
        Assert.Equal(original.Address, saved.Parent);
        Assert.Equal(original.RootId, saved.RootId);
        Owner restored = Assert.IsType<Owner>(repository.ReadState(saved));
        Assert.Equal((byte)7, restored.First!.Value);
        Assert.Equal((byte)11, restored.Second!.Value);
        Assert.Same(leaf, root.First);
        GC.KeepAlive(live);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (BranchCheckout Checkout, WeakReference<PreparedStateRestoration> Entry) WarmLive(
        Repository repository, CheckpointAddress address) {
        BranchCheckout checkout = repository.Fork("live", address);
        return (checkout, new(repository.PreparedStateEntry!));
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool EntryIsAlive(WeakReference<PreparedStateRestoration> weak) => weak.TryGetTarget(out _);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<Leaf> WarmWeak(Repository repository, CheckpointAddress address) {
        using BranchCheckout fork = repository.Fork("warm", address);
        return new(Assert.IsType<Owner>(fork.State).First!);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsAlive(WeakReference<Leaf> weak) => weak.TryGetTarget(out _);

    private Repository Create(Controls control) {
        Repository repository = Repository.CreateNew(_path, Models(control));
        repository.ImmutableLeafReuseEnabled = true;
        return repository;
    }
    private static Owner Graph(byte value = 7) => new() { First = new(value), Tail = new() };
    private static StateModelRegistry Models(Controls control) {
        StateModelRegistry registry = new();
        registry.Register(new StateDefinitionBinding(Dependency.SchemaId, SchemaKind.InlineValue, 0, null,
            [new(Dependency.SchemaId, 1, SchemaKind.InlineValue, 0, [new(1, TypeExpr.Builtin(TypeTag.Int32))])]));
        registry.Register(new StateDefinitionBinding(LeafSchema.SchemaId, SchemaKind.ReferenceObject, 0, typeof(Leaf),
            [new(LeafSchema.SchemaId, 1, SchemaKind.ReferenceObject, 0,
                [new(1, TypeExpr.Builtin(TypeTag.Byte))], stateTypeDefinition: typeof(byte))],
            currentModelFactory: (_, context) => {
                void Check(bool allocate) {
                    if (control.CheckDependency && control.DependencyDuringAllocate == allocate) {
                        context.BindSchema(Dependency);
                        control.DependencyChecks++;
                    }
                }
                return new StateModelBinding<Leaf, byte>(new(LeafSchema, ByteBody,
                    static (in byte prior, in byte next) => new(prior != next, new byte[] { next })),
                    [context.ResolveReader(LeafSchema)], static row => row.GetState<byte>(), () => {
                        control.LeafAllocations++;
                        Check(true);
                        return control.ReturnSingleton ?? new Leaf();
                    }, (Leaf target, in byte state, ObjectReadTable _) => {
                        control.LeafHydrates++;
                        Check(false);
                        LeafValue(target) = state;
                    }, static (leaf, _) => leaf.Value, static (in byte _, IStateReferenceVisitor _) => { }, isImmutableLeaf: true);
            }, historicalReaderFactory: (schema, _) => ByteReader(schema)));
        registry.Register(new StateModelBinding<Tail, byte>(new(TailSchema, ByteBody,
            static (in byte prior, in byte next) => new(prior != next, new byte[] { next })),
            [ByteReader(TailSchema)], static row => row.GetState<byte>(), static () => new(),
            (Tail _, in byte state, ObjectReadTable _) => { if (control.FailTail) { throw control.Failure; } },
            static (_, _) => 0, static (in byte _, IStateReferenceVisitor _) => { }));
        static OwnerState Read(ref BareValueReader input) => new(new(input.ReadVarUInt32()), new(input.ReadVarUInt32()), new(input.ReadVarUInt32()));
        static void Visit(in OwnerState state, IStateReferenceVisitor visitor) {
            visitor.VisitDurable(state.First, LeafSchema.Type);
            visitor.VisitDurable(state.Second, LeafSchema.Type);
            visitor.VisitDurable(state.Tail, TailSchema.Type);
        }
        registry.Register(new StateModelBinding<Owner, OwnerState>(new(OwnerSchema, OwnerBody,
            static (in OwnerState prior, in OwnerState next) => new(prior != next, OwnerBody(next).Body)),
            [new StateReaderBinding<OwnerState>(OwnerSchema, Read, static (ref BareValueReader input, in OwnerState _) => Read(ref input), Visit)],
            static row => row.GetState<OwnerState>(), static () => new(),
            static (Owner target, in OwnerState state, ObjectReadTable objects) => {
                target.First = objects.ResolveDurable<Leaf>(state.First);
                target.Second = objects.ResolveDurable<Leaf>(state.Second);
                target.Tail = objects.ResolveDurable<Tail>(state.Tail);
            }, static (owner, context) => new(context.CaptureDurable(owner.First, LeafSchema.Type),
                context.CaptureDurable(owner.Second, LeafSchema.Type), context.CaptureDurable(owner.Tail, TailSchema.Type)), Visit));
        return registry;
    }
    private static StateReaderBinding ByteReader(DurableSchema schema) => new StateReaderBinding<byte>(schema,
        static (ref BareValueReader input) => input.ReadByte(), static (ref BareValueReader input, in byte _) => input.ReadByte(),
        static (in byte _, IStateReferenceVisitor _) => { });
    private static PreparedBaseBody ByteBody(in byte value) => new(new byte[] { value });
    private static PreparedBaseBody OwnerBody(in OwnerState state) {
        ArrayBufferWriter<byte> bytes = new();
        BareValueWriter writer = new(bytes);
        writer.WriteVarUInt32(state.First.Value); writer.WriteVarUInt32(state.Second.Value); writer.WriteVarUInt32(state.Tail.Value);
        return new(bytes.WrittenSpan);
    }
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = nameof(Leaf.Value))]
    private static extern ref byte LeafValue(Leaf leaf);
    private sealed class Leaf(byte value = 0) : IDurableObject { internal readonly byte Value = value; }
    private sealed class Owner : IDurableObject { internal Leaf? First; internal Leaf? Second; internal Tail? Tail; }
    private sealed class Tail : IDurableObject;
    private readonly record struct OwnerState(ObjectId First, ObjectId Second, ObjectId Tail);
    private sealed class Controls {
        internal bool FailTail, CheckDependency, DependencyDuringAllocate;
        internal int LeafAllocations, LeafHydrates, DependencyChecks;
        internal Leaf? ReturnSingleton;
        internal readonly InvalidDataException Failure = new("Tail failed after the leaf hydrated.");
    }
    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}
