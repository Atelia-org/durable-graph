using System.Buffers;
using Atelia.DurableGraph.Persistence.MetaBootstrapP0;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class SplitRepositoryEnvironmentTests : IDisposable {
    private const string Prefix = "dg-split-repository-environment-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
    private static readonly DurableSchema Extra = new("SplitRepositoryExtra", 1, new DurableFieldInfo(1, TypeTag.Int32));
    private static readonly DurableSchema NextSchema = new("SplitRepositoryNext", 1, [
        new(1, TypeTag.Int32), DurableFieldInfo.Reference(2, TypeExpr.Named("P0Widget")),
    ]);
    private static readonly DurableSchema LateSchema = new("SplitRepositoryLate", 1, new DurableFieldInfo(1, TypeTag.Int32));

    [Fact]
    public void OpenFreezesLateRegistrationsUntilTheNextOpenWithoutEagerlyClosingUnusedFactories() {
        Seed();
        Counts counts = new();
        StateModelRegistry models = Models(counts);
        CheckpointAddress stale;
        using (Repository repository = Repository.OpenMetaBootstrapExisting(_path, models)) {
            Assert.Empty(counts.NextLookups);
            models.Register(LateModel());
            using BranchCheckout checkout = repository.Checkout("main");
            Assert.False(checkout.Models.TryGetCurrentModel(typeof(LateRoot), out _));
            stale = checkout.Head;
            FrameAddress? schema = repository.SharedSchemaRoot;
            long frames = repository.MetaBootstrapStore.AppendedFrames;
            Assert.Throws<ArgumentException>(() => checkout.CommitEvent(new LateRoot { Value = 4 }));
            Assert.Equal(stale, checkout.Head);
            Assert.Equal(schema, repository.SharedSchemaRoot);
            Assert.Equal(frames, repository.MetaBootstrapStore.AppendedFrames);
            Assert.False(repository.IsFaulted);
            Assert.Empty(counts.NextLookups);
            DB087Widget root = AssertWidget(checkout.State, 0);
            root.Numbers[7] = 1;
            checkout.CommitState(); // The rejected late type did not poison this checkout or leave a pending save.
        }
        using Repository reopened = Repository.OpenMetaBootstrapExisting(_path, models);
        using BranchCheckout restored = reopened.Checkout("main");
        Assert.True(restored.Models.TryGetCurrentModel(typeof(LateRoot), out _));
        CheckpointAddress accepted = restored.CommitEvent(new LateRoot { Value = 4 });
        Assert.Equal(4, Assert.IsType<LateRoot>(reopened.ReadEvent(accepted)).Value);
        AssertWidget(restored.State, 1);
        Assert.Throws<ArgumentException>(() => reopened.ReadState(stale));
        Assert.False(reopened.IsFaulted);
    }

    [Fact]
    public void CheckoutPinsItsExecutionSchemaAcrossDiscardAndEventsWhileColdStateUsesTheNewStoredSchema() {
        Seed(withSide: true);
        Counts counts = new();
        FrameAddress oldState, finalState, currentSchema;
        ObjectId oldRootId, listId;
        using (Repository repository = Repository.OpenMetaBootstrapExisting(_path, Models(counts))) {
            using BranchCheckout checkout = repository.Checkout("main");
            DB087Widget original = AssertWidget(checkout.State, 0);
            oldState = checkout.StateRevisionAddress!.Value;
            oldRootId = checkout.StateId!.Value;
            NormalizedRevision baseline0 = checkout.Baseline!;
            StateModelSnapshot models0 = checkout.Models;
            DecodedRevision.SplitBindingEnvironment environment0 = checkout.BindingEnvironment!;
            FrameAddress schema0 = environment0.SelectedSchemaView.Snapshot!.Address;
            Assert.False(models0.TryGetSchema(Extra.Type, Extra.Version, out _));
            Assert.Empty(counts.NextLookups);
            listId = Assert.Single(baseline0.Objects, pair => pair.Value.Current.Kind == ObjectStateKind.List).Key;

            checkout.Catalog.Set(Extra);
            CheckpointAddress head0 = checkout.Head;
            long frames = repository.MetaBootstrapStore.AppendedFrames;
            checkout.PrepareAndDiscard(original);
            Assert.Equal(frames, repository.MetaBootstrapStore.AppendedFrames);
            Assert.Equal(head0, checkout.Head);
            Assert.Equal(schema0, repository.SharedSchemaRoot);
            Assert.Same(baseline0, checkout.Baseline);
            Assert.Same(models0, checkout.Models);
            Assert.Same(environment0, checkout.BindingEnvironment);
            Assert.Equal(1, checkout.Catalog.DirtyCount);

            CheckpointAddress event1 = checkout.CommitEvent(original);
            FrameAddress schema1 = repository.SharedSchemaRoot!.Value;
            Assert.NotEqual(schema0, schema1);
            Assert.Same(original, checkout.State);
            Assert.Equal(oldState, checkout.StateRevisionAddress);
            Assert.Same(baseline0, checkout.Baseline);
            Assert.Same(models0, checkout.Models);
            Assert.Same(environment0, checkout.BindingEnvironment);
            Assert.Equal(0, checkout.Catalog.DirtyCount);
            Assert.Equal(schema1, checkout.Catalog.Accepted!.Snapshot!.Address);
            Assert.False(checkout.Models.TryGetSchema(Extra.Type, Extra.Version, out _));
            Assert.Equal(schema0, repository.MetaBootstrapStore.ReadGraph(oldState).SchemaRoot);
            Assert.Equal(schema1, repository.MetaBootstrapStore.ReadGraph(event1.RevisionAddress).SchemaRoot);

            // Give the side branch a real Event head pinning S1, while its nearest State still pins S0.
            CheckpointAddress sideEvent;
            using (BranchCheckout side = repository.Checkout("side")) {
                Assert.Equal(schema0, side.BindingEnvironment!.SelectedSchemaView.Snapshot!.Address);
                Assert.False(side.Models.TryGetSchema(Extra.Type, Extra.Version, out _));
                AssertWidget(side.State, 0);
                sideEvent = side.CommitEvent(side.State!);
                Assert.Equal(schema1, repository.MetaBootstrapStore.ReadGraph(sideEvent.RevisionAddress).SchemaRoot);
            }
            using (BranchCheckout side = repository.Checkout("side")) {
                Assert.Equal(sideEvent, side.Head);
                Assert.Equal(GraphFrameKind.Event, side.Head.Kind);
                Assert.Equal(schema0, side.BindingEnvironment!.SelectedSchemaView.Snapshot!.Address);
                Assert.False(side.Models.TryGetSchema(Extra.Type, Extra.Version, out _));
                AssertWidget(side.State, 0);
            }
            Assert.Empty(counts.NextLookups);

            NextRoot next = new() { Value = 9, Link = original };
            checkout.PrepareAndDiscard(next); // The first lazy closure may be retained; the candidate's identities may not.
            Assert.Equal(new[] { false }, counts.NextLookups);
            Assert.Same(baseline0, checkout.Baseline);
            CheckpointAddress event2 = checkout.CommitEvent(next);
            Assert.Equal(new[] { false }, counts.NextLookups);
            Assert.Same(baseline0, checkout.Baseline);
            Assert.Equal(oldRootId, checkout.StateId);
            CheckpointAddress state = checkout.CommitState(next);
            Assert.NotEqual(event2.RootId, state.RootId);
            Assert.Same(next, checkout.State);
            Assert.Same(original, next.Link);
            Assert.Same(models0, checkout.Models);
            Assert.Same(models0, checkout.Baseline!.SourceModels);
            Assert.Same(environment0, checkout.BindingEnvironment);
            currentSchema = repository.SharedSchemaRoot!.Value;
            Assert.Equal(currentSchema, checkout.Baseline.SelectedSchemaView!.Snapshot!.Address);
            Assert.NotEqual(schema0, currentSchema);
            foreach ((ObjectId id, NormalizedObject row) in baseline0.Objects) {
                Assert.Equal(row.Storage, checkout.Baseline.Objects[id].Storage);
            }
            Assert.Equal(new[] { false }, counts.NextLookups); // Same checkout/environment memo, including after Discard.

            P0GraphSnapshot before = repository.MetaBootstrapStore.ReadGraph(state.RevisionAddress);
            frames = repository.MetaBootstrapStore.AppendedFrames;
            CheckpointAddress unchanged = checkout.CommitState();
            P0GraphSnapshot saved = repository.MetaBootstrapStore.ReadGraph(unchanged.RevisionAddress);
            Assert.Null(saved.OwnedBatch);
            Assert.Equal(before.DirectoryRoot, saved.DirectoryRoot);
            Assert.Equal(frames + 1, repository.MetaBootstrapStore.AppendedFrames);
            finalState = unchanged.RevisionAddress;
            NextRoot independent = Assert.IsType<NextRoot>(repository.ReadState(unchanged));
            Assert.Equal(9, independent.Value);
            AssertWidget(independent.Link, 0);
            Assert.NotSame(next, independent);
            Assert.Equal(new[] { false, true }, counts.NextLookups);
            Assert.False(checkout.Models.TryGetSchema(Extra.Type, Extra.Version, out _));
        }

        Dictionary<string, byte[]> files = SnapshotFiles();
        Counts readOnlyCounts = new();
        using (Repository repository = Repository.OpenMetaBootstrapReadOnlyExisting(_path, Models(readOnlyCounts))) {
            Assert.Empty(readOnlyCounts.NextLookups);
            NextRoot root = Assert.IsType<NextRoot>(repository.ReadState(repository.GetHead("main")));
            Assert.Equal(9, root.Value);
            AssertWidget(root.Link, 0);
            Assert.Equal(new[] { true }, readOnlyCounts.NextLookups);
            Assert.Throws<InvalidOperationException>(() => repository.Checkout("main"));
            Assert.Throws<InvalidOperationException>(() => repository.CreateBranch("forbidden", Ring()));
            Assert.Equal(0, repository.MetaBootstrapStore.AppendedFrames);
        }
        AssertFilesEqual(files);

        Counts coldCounts = new();
        using (Repository repository = Repository.OpenMetaBootstrapExisting(_path, Models(coldCounts))) {
            using BranchCheckout checkout = repository.Checkout("main");
            Assert.Equal(finalState, checkout.StateRevisionAddress);
            Assert.Equal(currentSchema, checkout.BindingEnvironment!.SelectedSchemaView.Snapshot!.Address);
            Assert.Equal(new[] { true }, coldCounts.NextLookups);
            NextRoot root = Assert.IsType<NextRoot>(checkout.State);
            AssertWidget(root.Link, 0);
            IReadOnlyDictionary<ObjectId, NormalizedObject> previous = checkout.Baseline!.Objects;
            ObjectStorageInfo prior = previous[listId].Storage!.Value;
            root.Link!.Numbers[7] = 3;
            CheckpointAddress continued = checkout.CommitState();
            P0GraphSnapshot graph = repository.MetaBootstrapStore.ReadGraph(continued.RevisionAddress);
            ObjectVersionRecord delta = Assert.Single(repository.MetaBootstrapStore.ReadBatch(graph.OwnedBatch!.Value).Records);
            Assert.Equal(listId.Value, delta.ObjectId);
            Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
            Assert.Equal(prior.Head, delta.PriorAddress);
            Assert.Equal(prior.ReconstructionPayloadBytes + ObjectVersionPayloadSize.GetDeltaPayloadBytes(delta.Body.Length, prior.Head),
                checkout.Baseline!.Objects[listId].Storage!.Value.ReconstructionPayloadBytes);
            foreach ((ObjectId id, NormalizedObject row) in previous.Where(pair => pair.Key != listId)) {
                Assert.Equal(row.Storage, checkout.Baseline.Objects[id].Storage);
            }
            Assert.Equal(currentSchema, graph.SchemaRoot);
            AssertWidget(Assert.IsType<NextRoot>(repository.ReadState(continued)).Link, 3);
            AssertWidget(P0BusinessWorkspace.ReadRoot(repository.MetaBootstrapStore, oldState, Models(new())), 0);
            Assert.Equal(oldRootId, repository.MetaBootstrapStore.ReadGraph(oldState).RootId);
        }
    }

    [Fact]
    public void CheckoutFailuresReleaseOccupancyAndAnOldDisposeDoesNotReleaseItsSuccessor() {
        Seed();
        using (Repository inspector = Repository.OpenMetaBootstrapExisting(_path, new())) {
            Assert.Throws<KeyNotFoundException>(() => inspector.Checkout("missing"));
            Assert.Throws<KeyNotFoundException>(() => inspector.Checkout("missing"));
            Assert.Throws<InvalidDataException>(() => inspector.Checkout("main"));
            Assert.Throws<InvalidDataException>(() => inspector.Checkout("main")); // Not an occupancy failure left by the first attempt.
            Assert.False(inspector.IsFaulted);
        }
        CheckpointAddress stale;
        P0GraphStore retained;
        using (Repository repository = Repository.OpenMetaBootstrapExisting(_path, Models(new()))) {
            BranchCheckout old = repository.Checkout("main");
            Assert.Throws<InvalidOperationException>(() => repository.Checkout("main"));
            old.Dispose();
            using BranchCheckout successor = repository.Checkout("main");
            old.Dispose();
            Assert.Throws<InvalidOperationException>(() => repository.Checkout("main"));
            Assert.Throws<ObjectDisposedException>(() => old.CommitState());
            DB087Widget root = AssertWidget(successor.State, 0);
            root.Numbers[7] = 2;
            stale = successor.CommitState();
            retained = repository.MetaBootstrapStore;
        }
        Assert.ThrowsAny<InvalidOperationException>(() => retained.ReadGraph(stale.RevisionAddress));
        using Repository reopened = Repository.OpenMetaBootstrapExisting(_path, Models(new()));
        Assert.Throws<ArgumentException>(() => reopened.ReadState(stale));
        AssertWidget(reopened.ReadState(reopened.GetHead("main")), 2);
    }

    [Fact]
    public void CaptureCallbackReentryRejectionsPreserveTheOuterRepositoryCandidateAndBranchOccupancy() {
        Seed(withSide: true);
        Counts counts = new();
        using Repository repository = Repository.OpenMetaBootstrapExisting(_path, Models(counts));
        using BranchCheckout checkout = repository.Checkout("main");
        CheckpointAddress before = checkout.Head;
        DB087Widget original = AssertWidget(checkout.State, 0);
        bool reached = false;
        counts.OnCapture = () => {
            reached = true;
            Assert.Throws<InvalidOperationException>(() => repository.ReadState(before));
            Assert.Throws<InvalidOperationException>(() => repository.Checkout("side"));
            Assert.Throws<InvalidOperationException>(repository.Dispose);
            Assert.Throws<InvalidOperationException>(checkout.Dispose);
            Assert.Throws<InvalidOperationException>(() => checkout.Catalog.Set(Extra));
        };
        NextRoot candidate = new() { Value = 8, Link = original };
        CheckpointAddress installed = checkout.CommitState(candidate);
        counts.OnCapture = null;
        Assert.True(reached);
        Assert.Same(candidate, checkout.State);
        Assert.Equal(installed, repository.GetHead("main"));
        Assert.False(repository.IsFaulted);
        Assert.Throws<InvalidOperationException>(() => repository.Checkout("main"));
        using BranchCheckout side = repository.Checkout("side");
        AssertWidget(side.State, 0);
        CheckpointAddress unchanged = checkout.CommitState();
        Assert.Null(repository.MetaBootstrapStore.ReadGraph(unchanged.RevisionAddress).OwnedBatch);
    }

    [Fact]
    public void EventFirstCheckoutUsesCreationSharedSchemaAndUnsupportedOrWrongBackendEntriesCannotPublish() {
        Seed();
        Counts counts = new();
        using (Repository repository = Repository.OpenMetaBootstrapExisting(_path, Models(counts))) {
            using (BranchCheckout main = repository.Checkout("main")) {
                main.Catalog.Set(Extra);
                main.CommitEvent(main.State!);
            }
            FrameAddress atCreation = repository.SharedSchemaRoot!.Value;
            NextRoot first = new() { Value = 5, Link = Ring() };
            using BranchCheckout eventOnly = repository.CreateBranchFromEvent("event-only", first);
            Assert.Null(eventOnly.State);
            Assert.Null(eventOnly.StateId);
            Assert.Null(eventOnly.StateRevisionAddress);
            Assert.Null(eventOnly.Baseline);
            Assert.Equal(atCreation, eventOnly.BindingEnvironment!.SelectedSchemaView.Snapshot!.Address);
            Assert.True(eventOnly.Models.TryGetSchema(Extra.Type, Extra.Version, out _));
            Assert.Equal(new[] { true }, counts.NextLookups);
            CheckpointAddress before = eventOnly.Head;
            long frames = repository.MetaBootstrapStore.AppendedFrames;
            eventOnly.PrepareAndDiscard(first);
            Assert.Equal(before, eventOnly.Head);
            Assert.Equal(frames, repository.MetaBootstrapStore.AppendedFrames);
            Assert.Null(eventOnly.State);
            CheckpointAddress initialState = eventOnly.CommitState(first);
            Assert.NotEqual(before.RootId, initialState.RootId);
            Assert.Same(first, eventOnly.State);
            Assert.Equal(initialState.RootId, eventOnly.StateId);
            Assert.Equal(atCreation, eventOnly.BindingEnvironment.SelectedSchemaView.Snapshot!.Address);
            Assert.Equal(5, Assert.IsType<NextRoot>(repository.ReadPair(initialState, initialState).First).Value);
            Assert.Equal(initialState, repository.GetHead("event-only"));
        }
        Dictionary<string, byte[]> files = SnapshotFiles();
        using (Repository publicDefault = Repository.OpenReadOnlyExisting(_path, Models(new()))) {
            Assert.Equal(5, Assert.IsType<NextRoot>(publicDefault.ReadState(publicDefault.GetHead("event-only"))).Value);
        }
        AssertFilesEqual(files);
        string legacyPath = _path + "-legacy";
        using (GraphResources legacy = GraphResources.CreateLegacyNew(legacyPath)) { }
        Assert.Throws<InvalidDataException>(() => Repository.OpenMetaBootstrapReadOnlyExisting(legacyPath, Models(new())));
        using Repository positive = Repository.OpenMetaBootstrapReadOnlyExisting(_path, Models(new()));
        Assert.Equal(5, Assert.IsType<NextRoot>(positive.ReadState(positive.GetHead("event-only"))).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AdapterPreparationFailureIsRetryableAndConfirmedPublicationFailureRequiresAFreshOwner(bool published) {
        Seed();
        FrameAddress old, actual;
        using (Repository repository = Repository.OpenMetaBootstrapExisting(_path, Models(new()))) {
            using BranchCheckout checkout = repository.Checkout("main");
            CheckpointAddress before = checkout.Head;
            old = before.RevisionAddress;
            NormalizedRevision baseline = checkout.Baseline!;
            FrameAddress? schema = repository.SharedSchemaRoot;
            DB087Widget root = AssertWidget(checkout.State, 0);
            root.Numbers[7] = 1;
            long frames = repository.MetaBootstrapStore.AppendedFrames;
            IOException failure = new("split repository adapter boundary");
            if (published) {
                repository.MetaBootstrapCheckpoint = point => { if (point == P0SaveCheckpoint.AfterPublication) { throw failure; } };
                GraphCommitException error = Assert.Throws<GraphCommitException>(() => checkout.CommitState());
                Assert.Same(failure, error.InnerException);
                Assert.Equal(GraphCommitOutcome.Published, error.Outcome);
                Assert.True(repository.IsFaulted);
                Assert.Same(baseline, checkout.Baseline);
                Assert.Equal(before, checkout.Head);
                Assert.Throws<InvalidOperationException>(() => checkout.CommitState());
                actual = error.CandidateRevisionAddress!.Value;
            } else {
                repository.Checkpoint = point => { if (point == CommitCheckpoint.AfterPrepare) { throw failure; } };
                Assert.Same(failure, Assert.Throws<IOException>(() => checkout.CommitState()));
                Assert.False(repository.IsFaulted);
                Assert.Same(baseline, checkout.Baseline);
                Assert.Equal(before, checkout.Head);
                Assert.Equal(before, repository.GetHead("main"));
                Assert.Equal(schema, repository.SharedSchemaRoot);
                Assert.Equal(frames, repository.MetaBootstrapStore.AppendedFrames);
                repository.Checkpoint = null;
                actual = checkout.CommitState().RevisionAddress;
            }
        }
        using Repository reopened = Repository.OpenMetaBootstrapReadOnlyExisting(_path, Models(new()));
        Assert.Equal(actual, reopened.GetHead("main").RevisionAddress);
        AssertWidget(reopened.ReadState(reopened.GetHead("main")), 1);
        AssertWidget(P0BusinessWorkspace.ReadRoot(reopened.MetaBootstrapStore, old, Models(new())), 0);
    }

    private void Seed(bool withSide = false) {
        using Repository repository = Repository.CreateMetaBootstrapNew(_path, Models(new()));
        using BranchCheckout main = repository.CreateBranch("main", Ring());
        if (withSide) { using BranchCheckout side = repository.CreateBranch("side", Ring()); }
    }

    private static DB087Widget Ring() {
        List<int> numbers = Enumerable.Repeat(0, 128).ToList();
        DB087Widget root = new() { Value = 1, Numbers = numbers };
        root.Child = new() { Value = 2, Numbers = numbers, Child = root };
        return root;
    }
    private static DB087Widget AssertWidget(IDurableObject? value, int number) {
        DB087Widget root = Assert.IsType<DB087Widget>(value);
        Assert.Equal(1, root.Value);
        DB087Widget child = Assert.IsType<DB087Widget>(root.Child);
        Assert.Equal(2, child.Value);
        Assert.Same(root, child.Child);
        Assert.Same(root.Numbers, child.Numbers);
        Assert.Equal(128, root.Numbers.Count);
        for (int i = 0; i < root.Numbers.Count; i++) { Assert.Equal(i == 7 ? number : 0, root.Numbers[i]); }
        return root;
    }

    private static StateModelRegistry Models(Counts counts) {
        StateModelRegistry models = new();
        models.Register(global::Atelia.DurableGraph.Generated.Family_5030576964676574.Definition);
        models.Register(new StateDefinitionBinding(NextSchema.SchemaId, SchemaKind.ReferenceObject, 0, typeof(NextRoot),
            [new(NextSchema.SchemaId, 1, SchemaKind.ReferenceObject, 0,
                [new(1, TypeExpr.Builtin(TypeTag.Int32)), new(2, TypeExpr.Named("P0Widget"))])],
            currentModelFactory: (domainType, context) => {
                Assert.Equal(typeof(NextRoot), domainType);
                counts.NextLookups.Add(context.TryGetSchema(Extra.Type, Extra.Version, out _));
                return new StateModelBinding<NextRoot, NextState>(new(NextSchema, BaseNext,
                    static (in NextState prior, in NextState current) => new(prior != current, BaseNext(in current).Body)),
                    [context.ResolveReader(NextSchema)], static row => row.GetState<NextState>(), static () => new(),
                    static (NextRoot target, in NextState state, ObjectReadTable objects) => {
                        target.Value = state.Value;
                        target.Link = objects.ResolveObject<DB087Widget>(state.Link);
                    }, (domain, capture) => {
                        counts.OnCapture?.Invoke();
                        return new(domain.Value, capture.CaptureObject(domain.Link, TypeExpr.Named("P0Widget")));
                    }, VisitNext);
            }, historicalReaderFactory: static (schema, _) => new StateReaderBinding<NextState>(schema,
                static (ref BareValueReader reader) => new(reader.ReadVarInt32(), new(reader.ReadVarUInt32())),
                static (ref BareValueReader reader, in NextState _) => new(reader.ReadVarInt32(), new(reader.ReadVarUInt32())), VisitNext)));
        return models;
    }
    private static void VisitNext(in NextState state, IStateReferenceVisitor visitor) => visitor.VisitObject(state.Link, TypeExpr.Named("P0Widget"));
    private static PreparedBaseBody BaseNext(in NextState state) {
        ArrayBufferWriter<byte> bytes = new();
        BareValueWriter writer = new(bytes);
        writer.WriteVarInt32(state.Value);
        writer.WriteVarUInt32(state.Link.Value);
        return new(bytes.WrittenSpan);
    }
    private static StateModelBinding<LateRoot, int> LateModel() => new(new(LateSchema, BaseInt,
        static (in int prior, in int current) => new(prior != current, BaseInt(in current).Body)),
        [new StateReaderBinding<int>(LateSchema, static (ref BareValueReader r) => r.ReadVarInt32(),
            static (ref BareValueReader r, in int _) => r.ReadVarInt32(), static (in int _, IStateReferenceVisitor _) => { })],
        static row => row.GetState<int>(), static () => new(), static (LateRoot target, in int state, ObjectReadTable _) => target.Value = state,
        static (domain, _) => domain.Value, static (in int _, IStateReferenceVisitor _) => { });
    private static PreparedBaseBody BaseInt(in int value) {
        ArrayBufferWriter<byte> bytes = new();
        BareValueWriter writer = new(bytes);
        writer.WriteVarInt32(value);
        return new(bytes.WrittenSpan);
    }
    private sealed class NextRoot : IDurableObject { internal int Value; internal DB087Widget? Link; }
    private sealed class LateRoot : IDurableObject { internal int Value; }
    private readonly record struct NextState(int Value, ObjectId Link);
    private sealed class Counts { internal List<bool> NextLookups { get; } = []; internal Action? OnCapture; }

    private Dictionary<string, byte[]> SnapshotFiles() => Directory.GetFiles(_path, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(_path, path), File.ReadAllBytes);
    private void AssertFilesEqual(Dictionary<string, byte[]> expected) {
        Dictionary<string, byte[]> actual = SnapshotFiles();
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach ((string name, byte[] bytes) in expected) { Assert.Equal(bytes, actual[name]); }
    }
    public void Dispose() {
        SharedReadModel.DeleteFixture(_path, Prefix);
        SharedReadModel.DeleteFixture(_path + "-legacy", Prefix);
    }
}
