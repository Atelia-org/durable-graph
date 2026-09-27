using System.Buffers;
using System.Reflection;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.RbfSegmentStore;
using Node = Atelia.DurableGraph.Persistence.Tests.SharedReadModel.Node;
using State = Atelia.DurableGraph.Persistence.Tests.SharedReadModel.State;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class DB080PreparedStateHistoryTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"durable-db080-history-{Guid.NewGuid():N}");
    private static readonly ReadAmplificationBaseBudgetParameters NoRebase = new(1000000, 1);
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
        using (Repository seed = Repository.CreateNew(_root, HistoryModels(current: false, new()),
            new() { NewStoreLayout = RbfSegmentStoreLayout.Flat })) {
            using BranchCheckout state = seed.CreateBranch("main", new HistoricalRoot { Value = 17 }, NoRebase);
            using BranchCheckout events = seed.CreateBranchFromEvent("events", new HistoricalRoot { Value = 29 }, NoRebase);
        }
        HistoryCounters counters = new();
        using Repository repository = Repository.OpenExisting(_root, HistoryModels(current: true, counters, extraFactory));
        CheckpointAddress address = repository.GetHead("main");
        CheckpointAddress eventAddress = repository.GetHead("events");
        // A normal readonly operation precloses the actual repository model, readers and
        // v1 -> v2 -> v3 UpgradePlan before the first preparation collection begins.
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
            Assert.Equal(address.RevisionAddress, Resources(repository).States.Read(saved.RevisionAddress).ParentRevisionAddress);
            Assert.Equal(ObjectVersionKind.Base, Assert.Single(Resources(repository).States.Read(saved.RevisionAddress).LocalObjects).Kind);
        }

        PreparedStateRestoration entry = Assert.IsType<PreparedStateRestoration>(repository.PreparedStateEntry);
        // This key belongs only to historical v2 or a factory's standard Resolve.
        // Neither source/current layouts nor persisted v1 contain it; registration is late.
        string conflictingId = extraFactory is null ? IntermediateId : FactoryDependencyId;
        Resources(repository).Schemas.Register(new DurableSchema(conflictingId, 5, SchemaKind.InlineValue,
            new DurableFieldInfo(1, TypeTag.Int64)));
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

        BranchCheckout? delivered = null;
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => delivered = repository.Fork("rejected", address));
        Assert.Contains(conflictingId, error.Message);
        Assert.Contains("v5", error.Message);
        Assert.Null(delivered);
        Assert.Same(entry, repository.PreparedStateEntry);
        Assert.Equal(normalized, counters.Normalizes);
        Assert.Equal(allocated, counters.Allocations);
        Assert.Equal(before.Decoded, statistics.DecodedObjects);
        Assert.Equal(before.Normalized, statistics.NormalizedObjects);
        Assert.Equal(before.Prepared, statistics.PreparedGraphs);
        Assert.False(repository.IsFaulted);
        Assert.DoesNotContain("rejected", repository.ListBranches());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UpgradedSourceRetainsUnreachableRowsHighIdsEmptyIdentityAndRewrite(bool enableReuse) {
        SeedCompleteHistoricalSource();
        using Repository repository = Repository.OpenExisting(_root, ProvenanceModels());
        repository.PreparedStateReuseEnabled = enableReuse;
        repository.RestorationStatistics = new();
        CheckpointAddress address = repository.GetHead("main");
        using BranchCheckout first = repository.Checkout("main");
        using BranchCheckout second = repository.Fork("second", address);
        Assert.NotSame(first.State, second.State);
        Assert.Equal(enableReuse ? 1 : 0, repository.RestorationStatistics.PreparedStateHits);
        foreach (BranchCheckout branch in new[] { first, second }) {
            NormalizedRevision source = Baseline(branch.Workspace);
            Assert.Equal(new uint[] { 1, 3, 9, 100 }, source.Objects.Keys.Select(id => id.Value).Order());
            Assert.Same(Resources(repository).States, source.SourceStore);
            Assert.Same(Resources(repository).Schemas, source.SourceSchemas);
            foreach ((ObjectId id, NormalizedObject row) in source.Objects) {
                ObjectVersionChain chain = Resources(repository).States.ReadObjectVersionChain(source.RevisionAddress, id.Value);
                Assert.Equal(new ObjectStorageInfo(chain.ObjectHeadAddress, chain.ReconstructionPayloadBytes), row.Storage);
            }
            Assert.True(source.Objects[new(1)].RequiresRewrite);
            Assert.Equal(1, source.Objects[new(1)].SourceSchema!.Version);
            Node root = Assert.IsType<Node>(branch.State);
            Assert.Null(root.Next);
            Assert.Same(string.Empty, root.Text);
            // An Event never installs a State baseline or clears pending rewrite.
            branch.CommitEvent(root, NoRebase);
            Assert.Same(source, Baseline(branch.Workspace));
            FrameAddress rewritten = branch.CommitState(NoRebase).RevisionAddress;
            StateRevision revision = Resources(repository).States.Read(rewritten);
            Assert.Equal(address.RevisionAddress, revision.ParentRevisionAddress);
            Assert.Equal(ObjectVersionKind.Base, Assert.Single(revision.LocalObjects).Kind);
            Assert.Equal(new uint[] { 9, 100 }, revision.RemovedObjectIds.Order());
            Assert.Equal(new uint[] { 1, 3 }, Resources(repository).States.ReadLiveObjectHeadMap(rewritten).Keys.Order());
            Assert.False(Baseline(branch.Workspace).Objects[new(1)].RequiresRewrite);
            root.Value++;
            root.Text = new string('n', 6);
            CheckpointAddress changedAddress = branch.CommitState(NoRebase);
            StateRevision changed = Resources(repository).States.Read(changedAddress.RevisionAddress);
            Assert.Equal(rewritten, changed.ParentRevisionAddress);
            // Tiny varint bodies can legitimately choose Base even for a sparse Delta.
            // Saving semantics and cleared rewrite are independent of that cost decision.
            Assert.Contains(changed.LocalObjects, row => row.ObjectId == 1);
            Assert.False(Baseline(branch.Workspace).Objects[new(1)].RequiresRewrite);
            Assert.Equal(ObjectVersionKind.Base, changed.LocalObjects.Single(row => row.ObjectId == 101).Kind);
            Assert.Equal(new uint[] { 1, 101 }, Resources(repository).States.ReadLiveObjectHeadMap(changedAddress.RevisionAddress).Keys.Order());
            Node restored = Assert.IsType<Node>(repository.ReadState(changedAddress));
            Assert.Equal(root.Value, restored.Value);
            Assert.Equal(root.Text, restored.Text);
        }
    }

    private void SeedCompleteHistoricalSource() {
        using Repository repository = Repository.CreateNew(_root, SharedReadModel.Models(),
            new() { NewStoreLayout = RbfSegmentStoreLayout.Flat });
        using BranchCheckout branch = repository.CreateBranch("main", new Node(), NoRebase);
        GraphResources resources = Resources(repository);
        RepresentationId representation = resources.Schemas.RegisterRepresentations([ObjectLayout.ForDurable(SharedReadModel.Schema)])[0];
        ObjectVersionRecord Durable(uint id, State state) => ObjectVersionRecord.CreateBase(id,
            BaseObjectBodyCodec.Encode(representation, SharedReadModel.Base(state)).Body);
        ObjectVersionRecord Empty(uint id) => ObjectVersionRecord.CreateBase(id,
            BaseObjectBodyCodec.EncodeString(StringPayloadCodec.PrepareBase(string.Empty)).Body);
        // Persist a legitimate complete graph with two historical Empty identities and
        // a high reachable ID which becomes unreachable only after normalization.
        FrameAddress revision = resources.States.AppendDurably(StateRevision.CreateObjectHeadMapBase(branch.StateRevisionAddress,
            [Durable(1, new State(100, 100, 9, 7)), Empty(3), Empty(9), Durable(100, new State(0, 0, 0, 8))], []));
        HistoryJournal history = Field<HistoryJournal>(repository, "_history");
        var reference = history.Journal.OpenBranch("main").Unwrap();
        var prior = history.Journal.GetHead(reference);
        var head = history.Append(GraphFrameKind.State, revision, new(1), prior);
        history.Journal.AdvanceRef(reference, prior, head).Unwrap();
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
        State Read(ref BinaryPayloadReader reader) => new(new(reader.ReadUInt32()), new(reader.ReadUInt32()),
            new(reader.ReadUInt32()), reader.ReadByte(), new(reader.ReadUInt32()));
        void Visit(in State state, IStateReferenceVisitor visitor) {
            visitor.VisitDurable(state.Next, schema.SchemaId);
            visitor.VisitDurable(state.Alias, schema.SchemaId);
            visitor.VisitString(state.Text);
            visitor.VisitObject(state.Links, SharedReadModel.LinksType);
        }
        StateReaderBinding old = new StateReaderBinding<State>(SharedReadModel.Schema, Read,
            (ref BinaryPayloadReader reader, in State prior) => Read(ref reader), Visit);
        StateReaderBinding current = new StateReaderBinding<State>(schema, Read,
            static (ref BinaryPayloadReader reader, in State prior) => prior with {
                Text = new(reader.ReadUInt32()), Value = reader.ReadByte(),
            }, Visit);
        StateModelRegistry registry = new();
        registry.Register(new StateModelBinding<Node, State>(new(schema, SharedReadModel.Base,
            static (in State prior, in State next) => {
                if (prior == next) { return new(false, []); }
                Assert.Equal(prior.Next, next.Next);
                Assert.Equal(prior.Alias, next.Alias);
                Assert.Equal(prior.Links, next.Links);
                ArrayBufferWriter<byte> bytes = new();
                BinaryPayloadWriter writer = new(bytes);
                writer.WriteUInt32(next.Text.Value);
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
        static (HistoricalRoot root, in CurrentState state, ObjectReadTable _) => root.Value = state.Value,
        static (root, _) => new(root.Value), static (in CurrentState _, IStateReferenceVisitor _) => { },
        sourceReaderResolver: context.ResolveReader);

    private static StateReaderBinding Reader<T>(DurableSchema schema, Func<int, T> create) where T : unmanaged => new StateReaderBinding<T>(schema,
        (ref BinaryPayloadReader reader) => create(reader.ReadInt32()),
        (ref BinaryPayloadReader reader, in T prior) => create(reader.ReadInt32()), static (in T _, IStateReferenceVisitor _) => { });
    private static PreparedBaseBody Body(int value) {
        ArrayBufferWriter<byte> bytes = new();
        BinaryPayloadWriter writer = new(bytes);
        writer.WriteInt32(value);
        return new(bytes.WrittenSpan);
    }
    private static void FirstUpgrade(in OldState prior, out MiddleState next, UpgradeContext _) => next = new(new(prior.Value));
    private static void LastUpgrade(in MiddleState prior, out CurrentState next, UpgradeContext _) => next = new(prior.Value.Value);
    private static MethodInfo Method(string name) => typeof(DB080PreparedStateHistoryTests).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;
    private static GraphResources Resources(Repository repository) => Field<GraphResources>(repository, "_resources");
    private static NormalizedRevision Baseline(WorldWorkspace workspace) => Field<NormalizedRevision>(workspace, "_baseline");
    private static (int Decoded, int Normalized, int Prepared, int Allocated, int Hydrated, int Hits, int Misses) Counts(GraphReadStatistics stats) =>
        (stats.DecodedObjects, stats.NormalizedObjects, stats.PreparedGraphs, stats.AllocatedObjects, stats.HydratedObjects, stats.PreparedStateHits, stats.PreparedStateMisses);
    private sealed class HistoryCounters { internal int Normalizes; internal int Allocations; internal int ExtraValidations; }
    private sealed class HistoricalRoot : IDurableObject { internal int Value; }
    private readonly record struct OldState(int Value);
    private readonly record struct Inline5(int Value);
    private readonly record struct MiddleState(Inline5 Value);
    private readonly record struct CurrentState(int Value);
    private sealed class Inline5Operations;
    public void Dispose() => SharedReadModel.DeleteFixture(_root, "durable-db080-history-");
}
