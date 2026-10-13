using Atelia.DurableGraph.Persistence.MetaBootstrapP0;
using System.Buffers;
using System.Reflection;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using Node = Atelia.DurableGraph.Persistence.Tests.SharedReadModel.Node;
using State = Atelia.DurableGraph.Persistence.Tests.SharedReadModel.State;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class DB080PreparedStateHistoryTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"durable-db080-history-{Guid.NewGuid():N}");
    private static readonly ReadAmplificationBaseBudgetParameters NoRebase = new(1000000, 1);
    [ThreadStatic] private static int _upgradeCallbacks; // Actual business delegates on the synchronous test thread.
    private const string OwnerId = "DB080HistoryOwner";
    private const string IntermediateId = "DB080Intermediate";
    private const string FactoryDependencyId = "DB080FactoryDependency";
    private static readonly DurableSchema Source = new(OwnerId, 1, new DurableFieldInfo(1, TypeTag.Int32));
    private static readonly DurableSchema Intermediate = new(IntermediateId, 5, SchemaKind.InlineValue,
        new DurableFieldInfo(1, TypeTag.Int32));
    private static readonly DurableSchema Current = new(OwnerId, 3, new DurableFieldInfo(1, TypeTag.Int32));
    private static readonly DurableSchema FactoryDependency = new(FactoryDependencyId, 5, SchemaKind.InlineValue,
        new DurableFieldInfo(1, TypeTag.Int32));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreclosedHistoricalPlanRetainsMiddleOnlyDependencyAcrossReuseAndEviction(bool evict) =>
        VerifyPreclosedDependency(evict, null);

    [Theory]
    [InlineData("model")]
    [InlineData("reader")]
    [InlineData("value")]
    public void PreclosedFactoryRetainsStandardResolveDependencyOutsideExactOwnerLayouts(string factory) =>
        VerifyPreclosedDependency(false, factory);

    private void VerifyPreclosedDependency(bool evict, string? extraFactory) {
        using (Repository seed = Repository.CreateNew(_root, HistoryModels(current: false, new()), schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared)) {
            using BranchCheckout state = seed.CreateBranch("main", new HistoricalRoot { Value = 17 }, NoRebase);
            using BranchCheckout events = seed.CreateBranchFromEvent("events", new HistoricalRoot { Value = 29 }, NoRebase);
        }
        HistoryCounters counters = new();
        using Repository repository = Repository.OpenExisting(_root, HistoryModels(current: true, counters, extraFactory), schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared);
        CheckpointAddress address = repository.GetHead("main");
        CheckpointAddress eventAddress = repository.GetHead("events");
        // A real readonly operation restores the historical root in its own exact
        // environment. It neither creates nor precloses the later editable State slot.
        StateCheckpoint preclosed = Assert.IsType<StateCheckpoint>(repository.ReadCheckpoint(address));
        Assert.Equal(17, Assert.IsType<HistoricalRoot>(preclosed.State).Value);
        Assert.Null(repository.PreparedStateEntry);
        Assert.Equal(1, counters.Normalizes);
        if (extraFactory is not null) { Assert.True(counters.ExtraValidations > 0); }
        counters.Normalizes = counters.Allocations = 0;
        GraphReadStatistics statistics = new();
        repository.RestorationStatistics = statistics;
        using BranchCheckout first = repository.Checkout("main");
        PreparedStateRestoration original = Assert.IsType<PreparedStateRestoration>(repository.PreparedStateEntry);
        Assert.Equal(1, counters.Normalizes);
        Assert.Equal(1, counters.Allocations);
        Assert.Equal(1, statistics.PreparedGraphs);
        Assert.True(original.Selection.Normalized.Objects[first.StateId!.Value].RequiresRewrite);

        if (evict) {
            using BranchCheckout replacement = repository.CreateBranch("replacement", new HistoricalRoot { Value = 44 }, NoRebase);
            using BranchCheckout replacementFork = repository.Fork("replacement-fork", replacement.Head);
            Assert.NotSame(original, repository.PreparedStateEntry);
            using BranchCheckout reloaded = repository.Fork("reloaded", address);
            Assert.Equal(17, Assert.IsType<HistoricalRoot>(reloaded.State).Value);
            Assert.Equal(2, counters.Normalizes);
            Assert.NotSame(original, repository.PreparedStateEntry);
            // Eviction must not invalidate a live workspace's complete saving source.
            CheckpointAddress saved = first.CommitState(NoRebase);
            Assert.Equal(address.RevisionAddress, Resources(repository).MetaBootstrapStore.ReadGraph(saved.RevisionAddress).SourceGraph);
            Assert.Equal(ObjectVersionKind.Base, Assert.Single(Records(Resources(repository).MetaBootstrapStore, saved.RevisionAddress)).Kind);
        }

        PreparedStateRestoration entry = Assert.IsType<PreparedStateRestoration>(repository.PreparedStateEntry);
        // The successfully reader-issued environment already has its models, readers
        // and UpgradePlan closed. Recollect on that same environment to exercise
        // cached dependency replay, without installing this diagnostic preparation.
        StateModelRegistry.FrozenConfiguration configuration =
            Field<StateModelRegistry.FrozenConfiguration>(repository, "_splitConfiguration");
        DecodedRevision.SplitBindingEnvironment environment = entry.BindingEnvironment!;
        int factoryValidations = counters.ExtraValidations;
        StateBindingContext.ExactSchemaRequirementSet recollected;
        using (var collection = environment.Models.BeginSchemaRequirementCollection()) {
            DecodedRevision exact = DecodedRevision.ReadSplit(Resources(repository).MetaBootstrapStore,
                entry.RevisionAddress, configuration, environment);
            PreparedGraphSelection replayed = GraphReader.PrepareState(exact, new(environment.Models), entry.RootId);
            Assert.Equal(entry.RevisionAddress, replayed.Normalized.RevisionAddress);
            recollected = collection.Complete();
        }
        Assert.Equal(factoryValidations, counters.ExtraValidations); // No factory rerun supplies the certificate.
        Assert.True(recollected.Count > 0);
        Assert.Same(entry, repository.PreparedStateEntry);
        // This key belongs only to historical v2 or a factory's standard Resolve.
        // Neither source/current layouts nor persisted v1 contain it; registration is late.
        string conflictingId = extraFactory is null ? IntermediateId : FactoryDependencyId;
        first.Catalog.Set(new DurableSchema(conflictingId, 5, SchemaKind.InlineValue,
            new DurableFieldInfo(1, TypeTag.Int64)));
        first.CommitEvent(new HistoricalRoot { Value = 55 }, NoRebase);
        // Controlled metadata acceptance advances latest shared S, but cannot reinterpret
        // the old State's exact S or poison its successful preparation certificate.
        int normalized = counters.Normalizes, allocated = counters.Allocations;
        var before = Counts(statistics);
        using (BranchCheckout eventFork = repository.Fork("event-fork", eventAddress)) {
            Assert.Null(eventFork.State);
            Assert.Equal(eventAddress, eventFork.Head);
        }
        Assert.Same(entry, repository.PreparedStateEntry);
        Assert.Equal(before, Counts(statistics));
        Assert.Equal(normalized, counters.Normalizes);
        Assert.Equal(allocated, counters.Allocations);

        using (BranchCheckout old = repository.Fork("unpolluted", address)) {
            Assert.Equal(17, Assert.IsType<HistoricalRoot>(old.State).Value);
            Assert.NotSame(first.State, old.State);
            Assert.Same(entry, repository.PreparedStateEntry);
            Assert.Equal(normalized, counters.Normalizes);
            Assert.Equal(allocated + 1, counters.Allocations);
            Assert.Equal(entry.BindingEnvironment!.SelectedSchemaView.Snapshot!.Address,
                old.Baseline!.SelectedSchemaView!.Snapshot!.Address);
        }
        allocated = counters.Allocations;
        int hydrated = counters.Hydrates, upgrades = _upgradeCallbacks;
        // Construct and publish a legitimate complete inherited State which explicitly
        // selects the new S. Only that selected view carries the dependency conflict.
        GraphResources resources = Resources(repository);
        FrameAddress conflicting = resources.MetaBootstrapStore.Append(P0GraphKind.State,
            address.RevisionAddress, resources.SharedMetaBootstrapRoot,
            new(address.RevisionAddress, address.RootId, [])).Address;
        resources.PublishMetaBootstrapBranchRoot(
            resources.PrepareMetaBootstrapBranchRoot("conflicting-source", conflicting), out _);
        DecodedRevision.SplitBindingEnvironment wrongEnvironment = DecodedRevision.SplitBindingEnvironment.Load(
            resources.MetaBootstrapStore, configuration, resources.SharedMetaBootstrapRoot!.Value);
        InvalidDataException certificateFailure = Assert.Throws<InvalidDataException>(() => recollected.Validate(wrongEnvironment.Models));
        Assert.Contains(conflictingId, certificateFailure.Message);
        Assert.Contains("v5", certificateFailure.Message);
        Assert.Equal(normalized, counters.Normalizes);
        Assert.Equal(allocated, counters.Allocations);
        Assert.Equal(hydrated, counters.Hydrates);
        Assert.Equal(upgrades, _upgradeCallbacks);
        before = Counts(statistics);
        BranchCheckout? delivered = null;
        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            delivered = repository.Fork("rejected", repository.GetHead("conflicting-source")));
        Assert.Contains(conflictingId, error.Message);
        Assert.Contains("v5", error.Message);
        Assert.Null(delivered);
        Assert.Same(entry, repository.PreparedStateEntry);
        // A new exact S has a fresh environment: the opaque Normalize wrapper may
        // begin before its standard Upgrade plan is closed. No business conversion
        // delegate, allocation or hydration may run after the dependency conflict.
        int wrappers = extraFactory is "model" or "reader" ? 0 : 1;
        Assert.Equal(normalized + wrappers, counters.Normalizes);
        Assert.Equal(allocated, counters.Allocations);
        Assert.Equal(hydrated, counters.Hydrates);
        Assert.Equal(upgrades, _upgradeCallbacks);
        Assert.Equal(before.Decoded, statistics.DecodedObjects); // Legacy decoder statistic is not incremented by split reads.
        Assert.Equal(before.Normalized + wrappers, statistics.NormalizedObjects);
        Assert.Equal(before.Prepared + (extraFactory == "reader" ? 0 : 1), statistics.PreparedGraphs);
        Assert.False(repository.IsFaulted);
        Assert.DoesNotContain("rejected", repository.ListBranches());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UpgradedSourceRetainsUnreachableRowsHighIdsEmptyIdentityAndRewrite(bool enableReuse) {
        SeedCompleteHistoricalSource();
        using Repository repository = Repository.OpenExisting(_root, ProvenanceModels(), schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared);
        repository.PreparedStateReuseEnabled = enableReuse;
        repository.RestorationStatistics = new();
        CheckpointAddress address = repository.GetHead("main");
        using BranchCheckout first = repository.Checkout("main");
        using BranchCheckout second = repository.Fork("second", address);
        Assert.NotSame(first.State, second.State);
        Assert.Equal(enableReuse ? 1 : 0, repository.RestorationStatistics.PreparedStateHits);
        foreach (BranchCheckout branch in new[] { first, second }) {
            NormalizedRevision source = branch.Baseline!;
            Assert.Equal(new uint[] { 1, 3, 9, 100 }, source.Objects.Keys.Select(id => id.Value).Order());
            Assert.Same(Resources(repository).MetaBootstrapStore, source.SplitStore);
            Assert.Same(branch.Models, source.SourceModels);
            Assert.Same(branch.BindingEnvironment!.SelectedSchemaView, source.SelectedSchemaView);
            foreach ((ObjectId id, NormalizedObject row) in source.Objects) {
                P0ObjectChain chain = Resources(repository).MetaBootstrapStore.ReadObjectChain(source.RevisionAddress, id.Value);
                Assert.Equal(new ObjectStorageInfo(chain.Head, chain.ReconstructionPayloadBytes), row.Storage);
            }
            Assert.True(source.Objects[new(1)].RequiresRewrite);
            Assert.Equal(1, source.Objects[new(1)].SourceSchema!.Version);
            Node root = Assert.IsType<Node>(branch.State);
            Assert.Null(root.Next);
            Assert.Same(string.Empty, root.Text);
            // An Event never installs a State baseline or clears pending rewrite.
            branch.CommitEvent(root, NoRebase);
            Assert.Same(source, branch.Baseline!);
            FrameAddress rewritten = branch.CommitState(NoRebase).RevisionAddress;
            P0GraphStore store = Resources(repository).MetaBootstrapStore;
            P0GraphSnapshot revision = store.ReadGraph(rewritten);
            Assert.Equal(address.RevisionAddress, revision.SourceGraph);
            Assert.Equal(ObjectVersionKind.Base, Assert.Single(Records(store, rewritten)).Kind);
            Assert.Equal(new uint[] { 9, 100 }, source.Objects.Keys.Select(id => id.Value).Except(revision.Objects.Keys).Order());
            Assert.Equal(new uint[] { 1, 3 }, revision.Objects.Keys.Order());
            Assert.False(branch.Baseline!.Objects[new(1)].RequiresRewrite);
            root.Value++;
            root.Text = new string('n', 6);
            CheckpointAddress changedAddress = branch.CommitState(NoRebase);
            P0GraphSnapshot changed = store.ReadGraph(changedAddress.RevisionAddress);
            Assert.Equal(rewritten, changed.SourceGraph);
            // Tiny varint bodies can legitimately choose Base even for a sparse Delta.
            // Saving semantics and cleared rewrite are independent of that cost decision.
            Assert.Contains(Records(store, changed.Address), row => row.ObjectId == 1);
            Assert.False(branch.Baseline!.Objects[new(1)].RequiresRewrite);
            Assert.Equal(ObjectVersionKind.Base, Records(store, changed.Address).Single(row => row.ObjectId == 101).Kind);
            Assert.Equal(new uint[] { 1, 101 }, changed.Objects.Keys.Order());
            Node restored = Assert.IsType<Node>(repository.ReadState(changedAddress));
            Assert.Equal(root.Value, restored.Value);
            Assert.Equal(root.Text, restored.Text);
        }
    }

    private void SeedCompleteHistoricalSource() {
        using Repository repository = Repository.CreateNew(_root, SharedReadModel.Models(), schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared);
        using BranchCheckout branch = repository.CreateBranch("main", new Node(), NoRebase);
        GraphResources resources = Resources(repository);
        P0GraphStore store = resources.MetaBootstrapStore;
        P0CatalogView catalog = P0CatalogView.Load(store, resources.SharedMetaBootstrapRoot!.Value);
        ObjectVersionRecord Durable(uint id, State state) => ObjectVersionRecord.CreateBase(id,
            P0TypedBodyCodec.Encode(ObjectLayout.ForDurable(SharedReadModel.Schema), SharedReadModel.Base(state), catalog));
        ObjectVersionRecord Empty(uint id) => ObjectVersionRecord.CreateBase(id,
            P0TypedBodyCodec.Encode(ObjectLayout.String, StringPayloadCodec.PrepareBase(string.Empty), catalog));
        // Persist a legitimate complete graph with two historical Empty identities and
        // a high reachable ID which becomes unreachable only after normalization.
        FrameAddress prior = branch.Head.RevisionAddress;
        FrameAddress revision = store.Append(P0GraphKind.State, prior, catalog.Snapshot!.Address,
            new(branch.StateRevisionAddress, new(1),
                [Durable(1, new State(100, 100, 9, 7)), Empty(3), Empty(9), Durable(100, new State(0, 0, 0, 8))])).Address;
        resources.PublishMetaBootstrapBranchRoot(resources.PrepareMetaBootstrapBranchRoot("main", revision), out _);
    }

    private static StateModelRegistry HistoryModels(bool current, HistoryCounters counts, string? extraFactory = null) {
        StateModelRegistry registry = new();
        void Extra(string factory, StateBindingContext context) {
            if (extraFactory == factory) {
                // Standard Resolve in the same repository context, never a private
                // context or a synthetic callback that only pretends to validate.
                context.ResolveStoredValue(new(1, TypeTag.InlineValue, inlineSchema: FactoryDependency));
                counts.ExtraValidations++;
            }
        }
        registry.Register(new StateDefinitionBinding(FactoryDependencyId, SchemaKind.InlineValue, 0, null,
            [new(FactoryDependencyId, 5, SchemaKind.InlineValue, 0, [new(1, TypeExpr.Builtin(TypeTag.Int32))], stateTypeDefinition: typeof(Inline5))],
            historicalValueFactory: static (schema, _) => new(new(1, TypeTag.InlineValue, inlineSchema: schema), typeof(Inline5), typeof(Inline5Operations))));
        registry.Register(new StateDefinitionBinding(IntermediateId, SchemaKind.InlineValue, 0, null,
            [new(IntermediateId, 5, SchemaKind.InlineValue, 0, [new(1, TypeExpr.Builtin(TypeTag.Int32))], stateTypeDefinition: typeof(Inline5))],
            historicalValueFactory: (schema, context) => {
                Extra("value", context);
                return new(new(1, TypeTag.InlineValue, inlineSchema: schema), typeof(Inline5), typeof(Inline5Operations));
            }));
        StateSchemaTemplate[] templates = [
            new(OwnerId, 1, SchemaKind.ReferenceObject, 0, [new(1, TypeExpr.Builtin(TypeTag.Int32))], stateTypeDefinition: typeof(OldState)),
            new(OwnerId, 2, SchemaKind.ReferenceObject, 0, [new(1, Intermediate.Type, 5)], stateTypeDefinition: typeof(MiddleState)),
            new(OwnerId, 3, SchemaKind.ReferenceObject, 0, [new(1, TypeExpr.Builtin(TypeTag.Int32))], stateTypeDefinition: typeof(CurrentState)),
        ];
        registry.Register(new StateDefinitionBinding(OwnerId, SchemaKind.ReferenceObject, 0, typeof(HistoricalRoot),
            current ? templates : [templates[0]],
            currentModelFactory: (_, context) => {
                Extra("model", context);
                return current ? CurrentModel(context, counts) : OldModel(context, counts);
            },
            historicalReaderFactory: (schema, context) => {
                Extra("reader", context);
                // A generated historical reader closes its field operations through
                // this same context; the handwritten equivalent must do so as well.
                if (schema.Version == 2) { context.ResolveStoredValue(schema.Fields[0]); }
                return schema.Version switch {
                1 => Reader<OldState>(schema, value => new(value)),
                2 => Reader<MiddleState>(schema, value => new(new(value))),
                3 => Reader<CurrentState>(schema, value => new(value)),
                _ => throw new InvalidDataException(),
                };
            }, upgrades: current ? [
                new(OwnerId, 1, Method(nameof(FirstUpgrade))), new(OwnerId, 2, Method(nameof(LastUpgrade))),
            ] : []));
        return registry;
    }

    private static StateModelRegistry ProvenanceModels() {
        DurableSchema schema = new(SharedReadModel.Schema.SchemaId, 2, SharedReadModel.Schema.Fields.ToArray());
        State Read(ref BareValueReader reader) => new(new(reader.ReadVarUInt32()), new(reader.ReadVarUInt32()),
            new(reader.ReadVarUInt32()), reader.ReadByte(), new(reader.ReadVarUInt32()));
        void Visit(in State state, IStateReferenceVisitor visitor) {
            visitor.VisitDurable(state.Next, schema.SchemaId);
            visitor.VisitDurable(state.Alias, schema.SchemaId);
            visitor.VisitString(state.Text);
            visitor.VisitObject(state.Links, SharedReadModel.LinksType);
        }
        StateReaderBinding old = new StateReaderBinding<State>(SharedReadModel.Schema, Read,
            (ref BareValueReader reader, in State prior) => Read(ref reader), Visit);
        StateReaderBinding current = new StateReaderBinding<State>(schema, Read,
            static (ref BareValueReader reader, in State prior) => prior with {
                Text = new(reader.ReadVarUInt32()), Value = reader.ReadByte(),
            }, Visit);
        StateModelRegistry registry = new();
        registry.Register(new StateModelBinding<Node, State>(new(schema, SharedReadModel.Base,
            static (in State prior, in State next) => {
                if (prior == next) { return new(false, []); }
                Assert.Equal(prior.Next, next.Next);
                Assert.Equal(prior.Alias, next.Alias);
                Assert.Equal(prior.Links, next.Links);
                ArrayBufferWriter<byte> bytes = new();
                BareValueWriter writer = new(bytes);
                writer.WriteVarUInt32(next.Text.Value);
                writer.WriteByte(next.Value);
                return new(true, bytes.WrittenSpan);
            }), [old, current], static row => row.GetState<State>() with { Next = default, Alias = default },
            static () => new(),
            static (Node root, in State state, ObjectReadTable objects) => {
                root.Next = objects.ResolveDurable<Node>(state.Next);
                root.Alias = objects.ResolveDurable<Node>(state.Alias);
                root.Text = objects.ResolveString(state.Text);
                root.Value = state.Value;
                root.Links = objects.ResolveObject<List<Node>>(state.Links);
            }, static (root, context) => new(context.CaptureDurable(root.Next, SharedReadModel.Schema.SchemaId),
                context.CaptureDurable(root.Alias, SharedReadModel.Schema.SchemaId), context.CaptureString(root.Text), root.Value,
                context.CaptureObject(root.Links, SharedReadModel.LinksType)), Visit));
        return registry;
    }

    private static StateModelBinding OldModel(StateBindingContext context, HistoryCounters counts) => new StateModelBinding<HistoricalRoot, OldState>(
        new(Source, static (in OldState state) => Body(state.Value), static (in OldState prior, in OldState next) => new(prior != next, Body(next.Value).Body)),
        [context.ResolveReader(Source)], static row => row.GetState<OldState>(),
        () => { counts.Allocations++; return new(); },
        static (HistoricalRoot root, in OldState state, ObjectReadTable _) => root.Value = state.Value,
        static (root, _) => new(root.Value), static (in OldState _, IStateReferenceVisitor _) => { });

    private static StateModelBinding CurrentModel(StateBindingContext context, HistoryCounters counts) => new StateModelBinding<HistoricalRoot, CurrentState>(
        new(Current, static (in CurrentState state) => Body(state.Value), static (in CurrentState prior, in CurrentState next) => new(prior != next, Body(next.Value).Body)),
        [context.ResolveReader(Current)], row => { counts.Normalizes++; return context.Normalize<CurrentState>(row, Current); },
        () => { counts.Allocations++; return new(); },
        (HistoricalRoot root, in CurrentState state, ObjectReadTable _) => { counts.Hydrates++; root.Value = state.Value; },
        static (root, _) => new(root.Value), static (in CurrentState _, IStateReferenceVisitor _) => { },
        sourceReaderResolver: context.ResolveReader);

    private static StateReaderBinding Reader<T>(DurableSchema schema, Func<int, T> create) where T : unmanaged => new StateReaderBinding<T>(schema,
        (ref BareValueReader reader) => create(reader.ReadVarInt32()),
        (ref BareValueReader reader, in T prior) => create(reader.ReadVarInt32()), static (in T _, IStateReferenceVisitor _) => { });
    private static PreparedBaseBody Body(int value) {
        ArrayBufferWriter<byte> bytes = new();
        BareValueWriter writer = new(bytes);
        writer.WriteVarInt32(value);
        return new(bytes.WrittenSpan);
    }
    private static void FirstUpgrade(in OldState prior, out MiddleState next, UpgradeContext _) {
        _upgradeCallbacks++;
        next = new(new(prior.Value));
    }
    private static void LastUpgrade(in MiddleState prior, out CurrentState next, UpgradeContext _) {
        _upgradeCallbacks++;
        next = new(prior.Value.Value);
    }
    private static MethodInfo Method(string name) => typeof(DB080PreparedStateHistoryTests).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;
    private static GraphResources Resources(Repository repository) => Field<GraphResources>(repository, "_resources");
    private static IReadOnlyList<ObjectVersionRecord> Records(P0GraphStore store, FrameAddress address) =>
        store.ReadGraph(address).OwnedBatch is { } batch ? store.ReadBatch(batch).Records : [];
    private static (int Decoded, int Normalized, int Prepared, int Allocated, int Hydrated, int Hits, int Misses) Counts(GraphReadStatistics stats) =>
        (stats.DecodedObjects, stats.NormalizedObjects, stats.PreparedGraphs, stats.AllocatedObjects, stats.HydratedObjects, stats.PreparedStateHits, stats.PreparedStateMisses);
    private sealed class HistoryCounters { internal int Normalizes; internal int Allocations; internal int Hydrates; internal int ExtraValidations; }
    private sealed class HistoricalRoot : IDurableObject { internal int Value; }
    private readonly record struct OldState(int Value);
    private readonly record struct Inline5(int Value);
    private readonly record struct MiddleState(Inline5 Value);
    private readonly record struct CurrentState(int Value);
    private sealed class Inline5Operations;
    public void Dispose() => SharedReadModel.DeleteFixture(_root, "durable-db080-history-");
}
