using static Atelia.DurableGraph.Persistence.Tests.DB096TestAccess;
using System.Buffers;
using System.Reflection;
using System.Runtime.CompilerServices;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;

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
        using WorkingCopy main = repository.OpenWorkingCopy(repository.CreateRootBranchFromEvent("main", Graph()).Id);
        main.CommitState(Graph());
        PreparedStateRestoration? previous = null;
        if (backfill) {
            using WorkingCopy disabled = repository.OpenWorkingCopy(repository.ForkBranch(main.BaseCommitId, "disabled").Id);
            previous = repository.PreparedStateEntry;
            Assert.NotNull(previous);
            Assert.Equal(0, previous.ImmutableLeafCount);
            repository.ImmutableLeafReuseEnabled = true;
        }
        control.FailTail = true;
        int hydrated = control.LeafHydrates;
        WorkingCopy? delivered = null;
        Assert.Same(control.Failure, Assert.Throws<InvalidDataException>(() => delivered = repository.OpenWorkingCopy(repository.ForkBranch(main.BaseCommitId, "retry").Id)));
        Assert.Null(delivered);
        Assert.Equal(hydrated + 1, control.LeafHydrates); // The leaf actually completed before the tail failed.
        Assert.Same(previous, repository.PreparedStateEntry);
        Assert.Contains(repository.ListBranches(), branch => branch.DisplayLabel == "retry");
        Assert.False(repository.IsFaulted);

        control.FailTail = false;
        using WorkingCopy successful = repository.OpenWorkingCopy(BranchNamed(repository, "retry"));
        Assert.Equal(hydrated + 2, control.LeafHydrates);
        PreparedStateRestoration installed = Assert.IsType<PreparedStateRestoration>(repository.PreparedStateEntry);
        Assert.Equal(1, installed.ImmutableLeafCount);
        Leaf leaf = Assert.IsType<Owner>(successful.State).First!;
        control.FailTail = true;
        int allocations = control.LeafAllocations;
        hydrated = control.LeafHydrates;
        repository.RestorationStatistics = new();
        Assert.Throws<InvalidDataException>(() => repository.OpenWorkingCopy(repository.ForkBranch(main.BaseCommitId, "failed-hit").Id));
        Assert.Equal(allocations, control.LeafAllocations);
        Assert.Equal(hydrated, control.LeafHydrates);
        Assert.Equal(1, repository.RestorationStatistics.ReusedImmutableLeaves);
        Assert.Equal((byte)7, leaf.Value);
        Assert.Same(installed, repository.PreparedStateEntry);
        control.FailTail = false;
        using WorkingCopy afterFailure = repository.OpenWorkingCopy(repository.ForkBranch(main.BaseCommitId, "after-failure").Id);
        Assert.Same(leaf, Assert.IsType<Owner>(afterFailure.State).First);
        Assert.Equal(hydrated, control.LeafHydrates);
    }

    [Fact]
    public void AllCachedInstancesAreRegisteredBeforeAnEarlierObjectIdAllocates() {
        Controls control = new();
        using Repository repository = Create(control);
        using WorkingCopy main = repository.OpenWorkingCopy(repository.CreateRootBranchFromEvent("main", new Owner { First = new(7), Second = new(8) }).Id);
        main.CommitState(new Owner { First = new(7), Second = new(8) });
        using WorkingCopy warm = repository.OpenWorkingCopy(repository.ForkBranch(main.BaseCommitId, "warm").Id);
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
        using WorkingCopy main = repository.OpenWorkingCopy(repository.CreateRootBranchFromEvent("main", Graph()).Id);
        main.CommitState(Graph());
        CommitId original = main.BaseCommitId;
        CommitId eventAddress;
        using (WorkingCopy events = repository.OpenWorkingCopy(repository.CreateRootBranchFromEvent("events", Graph(9)).Id)) { eventAddress = events.BaseCommitId; }
        if (backfill) {
            repository.ImmutableLeafReuseEnabled = false;
            using WorkingCopy disabled = repository.OpenWorkingCopy(repository.ForkBranch(original, "disabled").Id);
            Assert.Equal(0, repository.PreparedStateEntry!.ImmutableLeafCount);
        }
        repository.ImmutableLeafReuseEnabled = true;
        using WorkingCopy warm = repository.OpenWorkingCopy(repository.ForkBranch(original, "warm").Id);
        PreparedStateRestoration entry = Assert.IsType<PreparedStateRestoration>(repository.PreparedStateEntry);
        Assert.Equal(1, entry.ImmutableLeafCount);
        Assert.True(control.DependencyChecks > 0);
        int allocations = control.LeafAllocations, hydrates = control.LeafHydrates;
        repository.CommitBranches([main.CreateEventCommitRequest(main.State!)],
            [new DurableSchema(Dependency.SchemaId, 1, SchemaKind.InlineValue,
                new DurableFieldInfo(1, TypeTag.Int64))]);
        CommitId catalogEvent = main.BaseCommitId;
        var configuration = Field<StateModelRegistry.FrozenConfiguration>(repository, "_modelConfiguration");
        var wrongEnvironment = DecodedRevision.SchemaViewBindingEnvironment.Load(repository.GraphStore,
            configuration, repository.GraphStore.ReadMetadata(Describe(repository, catalogEvent).Address).SchemaRoot!.Value);
        InvalidDataException conflict = Assert.Throws<InvalidDataException>(() => entry.Requirements.Validate(wrongEnvironment.Models));
        Assert.Contains(Dependency.SchemaId, conflict.Message);
        Assert.Equal(allocations, control.LeafAllocations);
        Assert.Equal(hydrates, control.LeafHydrates);
        repository.RestorationStatistics = new();
        // Event-only requests still bypass the State certificate and leaf table.
        using (WorkingCopy events = repository.OpenWorkingCopy(BranchNamed(repository, "events"))) {
            Assert.Null(events.State);
            Assert.Equal(eventAddress, events.BaseCommitId);
        }
        using (WorkingCopy eventFork = repository.OpenWorkingCopy(repository.ForkBranch(eventAddress, "event-only").Id)) {
            Assert.Null(eventFork.State);
            Assert.Equal(eventAddress, eventFork.BaseCommitId);
        }
        Assert.Same(entry, repository.PreparedStateEntry);
        Assert.Equal(1, entry.ImmutableLeafCount);
        Assert.Equal(0, repository.RestorationStatistics.PreparedStateHits);
        Assert.Equal(0, repository.RestorationStatistics.PreparedStateMisses);
        Assert.Equal(0, repository.RestorationStatistics.AllocatedObjects);
        Assert.Equal(0, repository.RestorationStatistics.HydratedObjects);
        using WorkingCopy oldHit = repository.OpenWorkingCopy(repository.ForkBranch(original, "old-hit").Id);
        Assert.Same(Assert.IsType<Owner>(warm.State).First, Assert.IsType<Owner>(oldHit.State).First);
        Assert.Equal(1, repository.RestorationStatistics.ReusedImmutableLeaves);
        Assert.Equal(0, repository.RestorationStatistics.DecodedObjects);
        Assert.Equal(allocations, control.LeafAllocations);
        Assert.Equal(hydrates, control.LeafHydrates);
        Assert.Same(entry, repository.PreparedStateEntry);

        // The actual new State selects S1. Its first complete materialization
        // must check the callback dependency and must not install a partial table.
        CommitId newer = main.CommitState().Id;
        int reused = repository.RestorationStatistics.ReusedImmutableLeaves;
        Assert.Throws<InvalidDataException>(() => repository.OpenWorkingCopy(repository.ForkBranch(newer, "rejected").Id));
        Assert.Equal(reused, repository.RestorationStatistics.ReusedImmutableLeaves);
        Assert.Equal(allocations + 1, control.LeafAllocations);
        Assert.Equal(hydrates + (duringAllocate ? 0 : 1), control.LeafHydrates);
        Assert.Same(entry, repository.PreparedStateEntry);
        Assert.Contains(repository.ListBranches(), branch => branch.DisplayLabel == "rejected");
        Assert.False(repository.IsFaulted);
    }

    [Theory]
    [InlineData("before")]
    [InlineData("after")]
    [InlineData("unknown")]
    public void PublicationFailureNeverInstallsCandidateLeafTable(string phase) {
        Controls control = new();
        using Repository repository = Create(control);
        using WorkingCopy main = repository.OpenWorkingCopy(repository.CreateRootBranchFromEvent("main", Graph()).Id);
        main.CommitState(Graph());
        using WorkingCopy warm = repository.OpenWorkingCopy(repository.ForkBranch(main.BaseCommitId, "warm").Id);
        PreparedStateRestoration previous = repository.PreparedStateEntry!;
        Leaf deliveredLeaf = Assert.IsType<Owner>(warm.State).First!;
        CommitId newer = main.CommitState(Graph(9)).Id;
        int hydrates = control.LeafHydrates;
        var oldDirectory = ResourcesOf(repository).Heads.ReadHead(1).Unwrap().Id;
        var oldSchema = ResourcesOf(repository).Heads.ReadHead(2).Unwrap().Id;
        var oldBookmarks = ResourcesOf(repository).Heads.ReadHead(3).Unwrap().Id;
        BranchId? failedTarget = null;
        if (phase == "unknown") {
            InstallPublicationFault(ResourcesOf(repository).Heads, "AfterAppend", () => throw new IOException("Unknown after actual root append"));
        }
        repository.OperationStageObserver = point => {
            if (phase != "unknown" && point == (phase == "before" ? RepositoryOperationStage.BeforePublication : RepositoryOperationStage.AfterPublication)) {
                throw new IOException("Publication interruption");
            }
        };
        if (phase == "before") {
            Assert.Throws<IOException>(() => repository.ForkBranch(newer, "candidate"));
        } else {
            RepositoryPublicationException error = Assert.Throws<RepositoryPublicationException>(() => repository.ForkBranch(newer, "candidate"));
            Assert.Equal(phase == "after" ? RepositoryPublicationOutcome.Published : RepositoryPublicationOutcome.Unknown, error.Outcome);
            failedTarget = error.TargetBranchId;
        }
        Assert.Equal(hydrates, control.LeafHydrates); // Fork publishes before any restoration.
        Assert.Equal((byte)7, deliveredLeaf.Value);
        repository.OperationStageObserver = null;
        if (phase == "before") {
            Assert.False(repository.IsFaulted);
            Assert.Same(previous, repository.PreparedStateEntry);
            Assert.DoesNotContain(repository.ListBranches(), branch => branch.DisplayLabel == "candidate");
            using WorkingCopy retry = repository.OpenWorkingCopy(repository.ForkBranch(newer, "candidate").Id);
            Assert.Equal(hydrates + 1, control.LeafHydrates);
            Assert.Equal(1, repository.PreparedStateEntry!.ImmutableLeafCount);
            Assert.NotSame(deliveredLeaf, Assert.IsType<Owner>(retry.State).First);
        } else {
            Assert.True(repository.IsFaulted);
            Assert.Null(repository.PreparedStateEntry);
            Assert.Throws<InvalidOperationException>(() => repository.OpenWorkingCopy(repository.ForkBranch(newer, "blocked").Id));
        }
        if (phase == "unknown") {
            Assert.NotNull(failedTarget);
            // Close every old facade before checking what the actual single-D append published.
            warm.Dispose();
            main.Dispose();
            repository.Dispose();
            using Repository reopened = Repository.OpenExisting(_path, Models(new()));
            var resources = ResourcesOf(reopened);
            Assert.NotEqual(oldDirectory, resources.Heads.ReadHead(1).Unwrap().Id);
            Assert.Equal(oldSchema, resources.Heads.ReadHead(2).Unwrap().Id);
            Assert.Equal(oldBookmarks, resources.Heads.ReadHead(3).Unwrap().Id);
            BranchInfo created = reopened.ReadBranchInfo(failedTarget.Value);
            Assert.Equal("candidate", created.DisplayLabel);
            Assert.Equal(newer, created.ForkedFromCommitId);
            Assert.Equal(newer, reopened.ReadCurrentCommit(created.Id).Commit.Id); // No clone c0.
            Assert.Equal(newer, reopened.ReadCurrentCommit(main.BranchId).Commit.Id);
            Assert.Equal((byte)9, Assert.IsType<Owner>(reopened.LoadState(newer)).First!.Value);
            using WorkingCopy continued = reopened.OpenWorkingCopy(created.Id);
            CommitId saved = continued.CommitState().Id;
            Assert.Equal(created.Id, new BranchId(saved.Value.HeadId));
            Assert.Equal(newer, new CommitId(resources.ReadPrevious(saved.Value)!.Id));
            Assert.Equal(newer, reopened.ReadCurrentCommit(main.BranchId).Commit.Id);
        }
    }

    [Theory]
    [InlineData("evict")]
    [InlineData("dispose")]
    [InlineData("fault")]
    public void SlotLifetimeReleasesItsOnlyStrongLeafReference(string finish) {
        Controls control = new();
        using Repository repository = Create(control);
        using WorkingCopy main = repository.OpenWorkingCopy(repository.CreateRootBranchFromEvent("main", Graph()).Id);
        main.CommitState(Graph());
        WeakReference<Leaf> leaf = WarmWeak(repository, main.BaseCommitId);
        Assert.True(IsAlive(leaf));
        if (finish == "evict") {
            CommitId newer = main.CommitState(Graph(9)).Id;
            using WorkingCopy replacement = repository.OpenWorkingCopy(repository.ForkBranch(newer, "replacement").Id);
        } else if (finish == "dispose") {
            repository.Dispose();
        } else {
            repository.OperationStageObserver = point => {
                if (point == RepositoryOperationStage.AfterPublication) { throw new IOException("fault"); }
            };
            Assert.Throws<RepositoryPublicationException>(() => repository.OpenWorkingCopy(repository.ForkBranch(main.BaseCommitId, "fault").Id));
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
        using WorkingCopy main = repository.OpenWorkingCopy(repository.CreateRootBranchFromEvent("main", Graph()).Id);
        main.CommitState(Graph());
        CommitId original = main.BaseCommitId;
        var retained = WarmLive(repository, original);
        using WorkingCopy live = retained.Checkout;
        Owner root = Assert.IsType<Owner>(live.State);
        Leaf leaf = root.First!;
        CommitId newer = main.CommitState(Graph(9)).Id;
        using WorkingCopy replacement = repository.OpenWorkingCopy(repository.ForkBranch(newer, "replacement").Id);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(EntryIsAlive(retained.Entry));
        Assert.Equal((byte)7, leaf.Value);
        Assert.Same(leaf, root.First);

        root.Second = new Leaf(11);
        CommitId saved = live.CommitState().Id;
        Assert.Equal(original, Previous(repository, saved));
        Assert.Equal(Describe(repository, original).RootId, Describe(repository, saved).RootId);
        Owner restored = Assert.IsType<Owner>(repository.LoadState(saved));
        Assert.Equal((byte)7, restored.First!.Value);
        Assert.Equal((byte)11, restored.Second!.Value);
        Assert.Same(leaf, root.First);
        GC.KeepAlive(live);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WorkingCopy Checkout, WeakReference<PreparedStateRestoration> Entry) WarmLive(
        Repository repository, CommitId address) {
        WorkingCopy checkout = repository.OpenWorkingCopy(repository.ForkBranch(address, "live").Id);
        return (checkout, new(repository.PreparedStateEntry!));
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool EntryIsAlive(WeakReference<PreparedStateRestoration> weak) => weak.TryGetTarget(out _);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<Leaf> WarmWeak(Repository repository, CommitId address) {
        using WorkingCopy fork = repository.OpenWorkingCopy(repository.ForkBranch(address, "warm").Id);
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
