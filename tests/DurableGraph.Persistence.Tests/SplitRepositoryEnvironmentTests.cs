using static Atelia.DurableGraph.Persistence.Tests.DB096TestAccess;
using System.Buffers;

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
        CommitId stale;
        using (Repository repository = Repository.OpenStorageExisting(_path, models)) {
            Assert.Empty(counts.NextLookups);
            models.Register(LateModel());
            using WorkingCopy checkout = repository.OpenWorkingCopy(BranchNamed(repository, "main"));
            Assert.False(checkout.Models.TryGetCurrentModel(typeof(LateRoot), out _));
            stale = checkout.BaseCommitId;
            FrameAddress? schema = repository.SharedSchemaRoot;
            long frames = repository.GraphStore.AppendedFrames;
            Assert.Throws<ArgumentException>(() => checkout.CommitEvent(new LateRoot { Value = 4 }));
            Assert.Equal(stale, checkout.BaseCommitId);
            Assert.Equal(schema, repository.SharedSchemaRoot);
            Assert.Equal(frames, repository.GraphStore.AppendedFrames);
            Assert.False(repository.IsFaulted);
            Assert.Empty(counts.NextLookups);
            DB087Widget root = AssertWidget(checkout.State, 0);
            root.Numbers[7] = 1;
            checkout.CommitState(); // The rejected late type did not poison this checkout or leave a pending save.
        }
        using Repository reopened = Repository.OpenStorageExisting(_path, models);
        using WorkingCopy restored = reopened.OpenWorkingCopy(BranchNamed(reopened, "main"));
        Assert.True(restored.Models.TryGetCurrentModel(typeof(LateRoot), out _));
        CommitId accepted = restored.CommitEvent(new LateRoot { Value = 4 }).Id;
        Assert.Equal(4, Assert.IsType<LateRoot>(reopened.LoadEvent(accepted)).Value);
        AssertWidget(restored.State, 1);
        AssertWidget(reopened.LoadState(stale), 0);
        Assert.False(reopened.IsFaulted);
    }

    [Fact]
    public void CheckoutPinsItsExecutionSchemaAcrossDiscardAndEventsWhileColdStateUsesTheNewStoredSchema() {
        Seed(withSide: true);
        Counts counts = new();
        FrameAddress oldState, finalState, currentSchema;
        ObjectId oldRootId, listId;
        using (Repository repository = Repository.OpenStorageExisting(_path, Models(counts))) {
            using WorkingCopy checkout = repository.OpenWorkingCopy(BranchNamed(repository, "main"));
            DB087Widget original = AssertWidget(checkout.State, 0);
            oldState = checkout.StateRevisionAddress!.Value;
            oldRootId = checkout.StateId!.Value;
            NormalizedGraphState baseline0 = checkout.Baseline!;
            ModelExecutionEnvironment models0 = checkout.Models;
            DecodedRevision.SchemaViewBindingEnvironment environment0 = checkout.BindingEnvironment!;
            FrameAddress schema0 = environment0.SelectedSchemaView.Snapshot!.Address;
            Assert.False(environment0.SelectedSchemaView.Schemas.ContainsKey(new(Extra.Type, Extra.Version)));
            Assert.Empty(counts.NextLookups);
            listId = Assert.Single(baseline0.Objects, pair => pair.Value.Current.Kind == ObjectStateKind.List).Key;

            CommitId head0 = checkout.BaseCommitId;
            long frames = repository.GraphStore.AppendedFrames;
            checkout.PrepareAndDiscard(original);
            Assert.Equal(frames, repository.GraphStore.AppendedFrames);
            Assert.Equal(head0, checkout.BaseCommitId);
            Assert.Equal(schema0, repository.SharedSchemaRoot);
            Assert.Same(baseline0, checkout.Baseline);
            Assert.Same(models0, checkout.Models);
            Assert.Same(environment0, checkout.BindingEnvironment);
            Assert.False(checkout.AcceptedCatalog!.Schemas.ContainsKey(new(Extra.Type, Extra.Version)));

            CommitId event1 = repository.CommitBranches([checkout.CreateEventCommitRequest(original)], [Extra])[checkout.BranchId].Id;
            FrameAddress schema1 = repository.SharedSchemaRoot!.Value;
            Assert.NotEqual(schema0, schema1);
            Assert.Same(original, checkout.State);
            Assert.Equal(oldState, checkout.StateRevisionAddress);
            Assert.Same(baseline0, checkout.Baseline);
            Assert.Same(models0, checkout.Models);
            Assert.Same(environment0, checkout.BindingEnvironment);

            Assert.Equal(schema1, checkout.AcceptedCatalog!.Snapshot!.Address);
            Assert.False(checkout.BindingEnvironment!.SelectedSchemaView.Schemas.ContainsKey(new(Extra.Type, Extra.Version)));
            Assert.Equal(schema0, repository.GraphStore.ReadBusinessGraph(oldState, null).SchemaRoot);
            Assert.Equal(schema1, Snapshot(repository, event1).SchemaRoot);

            // Give the side branch a real Event head pinning S1, while its nearest State still pins S0.
            CommitId sideEvent;
            using (WorkingCopy side = repository.OpenWorkingCopy(BranchNamed(repository, "side"))) {
                Assert.Equal(schema0, side.BindingEnvironment!.SelectedSchemaView.Snapshot!.Address);
                Assert.False(side.BindingEnvironment.SelectedSchemaView.Schemas.ContainsKey(new(Extra.Type, Extra.Version)));
                AssertWidget(side.State, 0);
                sideEvent = side.CommitEvent(side.State!).Id;
                Assert.Equal(schema1, Snapshot(repository, sideEvent).SchemaRoot);
            }
            using (WorkingCopy side = repository.OpenWorkingCopy(BranchNamed(repository, "side"))) {
                Assert.Equal(sideEvent, side.BaseCommitId);
                Assert.Equal(CommitKind.Event, repository.ReadCommitInfo(side.BaseCommitId).Kind);
                Assert.Equal(schema0, side.BindingEnvironment!.SelectedSchemaView.Snapshot!.Address);
                Assert.False(side.BindingEnvironment.SelectedSchemaView.Schemas.ContainsKey(new(Extra.Type, Extra.Version)));
                AssertWidget(side.State, 0);
            }
            Assert.Empty(counts.NextLookups);

            NextRoot next = new() { Value = 9, Link = original };
            checkout.PrepareAndDiscard(next); // The first lazy closure may be retained; the candidate's identities may not.
            Assert.Equal(new[] { false }, counts.NextLookups);
            Assert.Same(baseline0, checkout.Baseline);
            CommitId event2 = checkout.CommitEvent(next).Id;
            Assert.Equal(new[] { false }, counts.NextLookups);
            Assert.Same(baseline0, checkout.Baseline);
            Assert.Equal(oldRootId, checkout.StateId);
            CommitId state = checkout.CommitState(next).Id;
            Assert.NotEqual(Describe(repository, event2).RootId, Describe(repository, state).RootId);
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

            StoredGraphRevision before = Snapshot(repository, state);
            frames = repository.GraphStore.AppendedFrames;
            CommitId unchanged = checkout.CommitState().Id;
            StoredGraphRevision saved = Snapshot(repository, unchanged);
            Assert.Null(saved.OwnedBatch);
            Assert.Equal(before.DirectoryRoot, saved.DirectoryRoot);
            Assert.Equal(frames + 1, repository.GraphStore.AppendedFrames);
            finalState = Describe(repository, unchanged).Address;
            NextRoot independent = Assert.IsType<NextRoot>(repository.LoadState(unchanged));
            Assert.Equal(9, independent.Value);
            AssertWidget(independent.Link, 0);
            Assert.NotSame(next, independent);
            Assert.Equal(new[] { false, true }, counts.NextLookups);
            Assert.False(checkout.BindingEnvironment!.SelectedSchemaView.Schemas.ContainsKey(new(Extra.Type, Extra.Version)));
        }

        Dictionary<string, byte[]> files = SnapshotFiles();
        Counts readOnlyCounts = new();
        using (Repository repository = Repository.OpenStorageReadOnlyExisting(_path, Models(readOnlyCounts))) {
            Assert.Empty(readOnlyCounts.NextLookups);
            NextRoot root = Assert.IsType<NextRoot>(repository.LoadState(repository.ReadCurrentCommit(BranchNamed(repository, "main")).Commit.Id));
            Assert.Equal(9, root.Value);
            AssertWidget(root.Link, 0);
            Assert.Equal(new[] { true }, readOnlyCounts.NextLookups);
            Assert.Throws<InvalidOperationException>(() => repository.OpenWorkingCopy(BranchNamed(repository, "main")));
            Assert.Throws<InvalidOperationException>(() => repository.OpenWorkingCopy(repository.CreateRootBranchFromState("forbidden", Ring()).Id));
            Assert.Equal(0, repository.GraphStore.AppendedFrames);
        }
        AssertFilesEqual(files);

        Counts coldCounts = new();
        using (Repository repository = Repository.OpenStorageExisting(_path, Models(coldCounts))) {
            using WorkingCopy checkout = repository.OpenWorkingCopy(BranchNamed(repository, "main"));
            Assert.Equal(finalState, checkout.StateRevisionAddress);
            Assert.Equal(currentSchema, checkout.BindingEnvironment!.SelectedSchemaView.Snapshot!.Address);
            Assert.Equal(new[] { true }, coldCounts.NextLookups);
            NextRoot root = Assert.IsType<NextRoot>(checkout.State);
            AssertWidget(root.Link, 0);
            IReadOnlyDictionary<ObjectId, NormalizedObject> previous = checkout.Baseline!.Objects;
            ObjectStorageInfo prior = previous[listId].Storage!.Value;
            root.Link!.Numbers[7] = 3;
            CommitId continued = checkout.CommitState().Id;
            StoredGraphRevision graph = Snapshot(repository, continued);
            ObjectVersionRecord delta = Assert.Single(repository.GraphStore.ReadBatch(graph.OwnedBatch!.Value).Records);
            Assert.Equal(listId.Value, delta.ObjectId);
            Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
            Assert.Equal(prior.Head, delta.PriorAddress);
            Assert.Equal(prior.ReconstructionPayloadBytes + ObjectVersionPayloadSize.GetDeltaPayloadBytes(delta.Body.Length, prior.Head),
                checkout.Baseline!.Objects[listId].Storage!.Value.ReconstructionPayloadBytes);
            foreach ((ObjectId id, NormalizedObject row) in previous.Where(pair => pair.Key != listId)) {
                Assert.Equal(row.Storage, checkout.Baseline.Objects[id].Storage);
            }
            Assert.Equal(currentSchema, graph.SchemaRoot);
            AssertWidget(Assert.IsType<NextRoot>(repository.LoadState(continued)).Link, 3);
            AssertWidget(BusinessPersistenceAdapter.ReadRoot(repository.GraphStore, repository.GraphStore.ReadBusinessGraph(oldState, null), Models(new())), 0);
            Assert.Equal(oldRootId, repository.GraphStore.ReadBusinessGraph(oldState, null).RootId);
        }
    }

    [Fact]
    public void CheckoutFailuresReleaseOccupancyAndAnOldDisposeDoesNotReleaseItsSuccessor() {
        Seed();
        using (Repository inspector = Repository.OpenStorageExisting(_path, new())) {
            Assert.Throws<ArgumentException>(() => inspector.OpenWorkingCopy(default));
            Assert.Throws<ArgumentException>(() => inspector.OpenWorkingCopy(default));
            Assert.Throws<InvalidDataException>(() => inspector.OpenWorkingCopy(BranchNamed(inspector, "main")));
            Assert.Throws<InvalidDataException>(() => inspector.OpenWorkingCopy(BranchNamed(inspector, "main"))); // Not an occupancy failure left by the first attempt.
            Assert.False(inspector.IsFaulted);
        }
        CommitId stale;
        GraphRecordStore retained;
        FrameAddress staleAddress;
        using (Repository repository = Repository.OpenStorageExisting(_path, Models(new()))) {
            WorkingCopy old = repository.OpenWorkingCopy(BranchNamed(repository, "main"));
            Assert.Throws<InvalidOperationException>(() => repository.OpenWorkingCopy(BranchNamed(repository, "main")));
            old.Dispose();
            using WorkingCopy successor = repository.OpenWorkingCopy(BranchNamed(repository, "main"));
            old.Dispose();
            Assert.Throws<InvalidOperationException>(() => repository.OpenWorkingCopy(BranchNamed(repository, "main")));
            Assert.Throws<ObjectDisposedException>(() => old.CommitState());
            DB087Widget root = AssertWidget(successor.State, 0);
            root.Numbers[7] = 2;
            stale = successor.CommitState().Id;
            retained = repository.GraphStore;
            staleAddress = Describe(repository, stale).Address;
        }
        Assert.ThrowsAny<InvalidOperationException>(() => retained.ReadBusinessGraph(staleAddress, null));
        using Repository reopened = Repository.OpenStorageExisting(_path, Models(new()));
        AssertWidget(reopened.LoadState(stale), 2);
        AssertWidget(reopened.LoadState(reopened.ReadCurrentCommit(BranchNamed(reopened, "main")).Commit.Id), 2);
    }

    [Fact]
    public void CaptureCallbackReentryRejectionsPreserveTheOuterRepositoryCandidateAndBranchOccupancy() {
        Seed(withSide: true);
        Counts counts = new();
        using Repository repository = Repository.OpenStorageExisting(_path, Models(counts));
        using WorkingCopy checkout = repository.OpenWorkingCopy(BranchNamed(repository, "main"));
        CommitId before = checkout.BaseCommitId;
        DB087Widget original = AssertWidget(checkout.State, 0);
        bool reached = false;
        counts.OnCapture = () => {
            reached = true;
            Assert.Throws<InvalidOperationException>(() => repository.LoadState(before));
            Assert.Throws<InvalidOperationException>(() => repository.OpenWorkingCopy(BranchNamed(repository, "side")));
            Assert.Throws<InvalidOperationException>(repository.Dispose);
            Assert.Throws<InvalidOperationException>(checkout.Dispose);
            Assert.Throws<InvalidOperationException>(() => repository.CommitBranches([], [Extra]));
        };
        NextRoot candidate = new() { Value = 8, Link = original };
        CommitId installed = checkout.CommitState(candidate).Id;
        counts.OnCapture = null;
        Assert.True(reached);
        Assert.Same(candidate, checkout.State);
        Assert.Equal(installed, repository.ReadCurrentCommit(BranchNamed(repository, "main")).Commit.Id);
        Assert.False(repository.IsFaulted);
        Assert.Throws<InvalidOperationException>(() => repository.OpenWorkingCopy(BranchNamed(repository, "main")));
        using WorkingCopy side = repository.OpenWorkingCopy(BranchNamed(repository, "side"));
        AssertWidget(side.State, 0);
        CommitId unchanged = checkout.CommitState().Id;
        Assert.Null(Snapshot(repository, unchanged).OwnedBatch);
    }

    [Fact]
    public void EventFirstCheckoutUsesCreationSharedSchemaAndUnsupportedOrWrongBackendEntriesCannotPublish() {
        Seed();
        Counts counts = new();
        using (Repository repository = Repository.OpenStorageExisting(_path, Models(counts))) {
            using (WorkingCopy main = repository.OpenWorkingCopy(BranchNamed(repository, "main"))) {
                repository.CommitBranches([main.CreateEventCommitRequest(main.State!)], [Extra]);
            }
            NextRoot first = new() { Value = 5, Link = Ring() };
            using WorkingCopy eventOnly = repository.OpenWorkingCopy(repository.CreateRootBranchFromEvent("event-only", first).Id);
            FrameAddress atCreation = repository.SharedSchemaRoot!.Value;
            Assert.Null(eventOnly.State);
            Assert.Null(eventOnly.StateId);
            Assert.Null(eventOnly.StateRevisionAddress);
            Assert.Null(eventOnly.Baseline);
            Assert.Equal(atCreation, eventOnly.BindingEnvironment!.SelectedSchemaView.Snapshot!.Address);
            Assert.True(eventOnly.BindingEnvironment.SelectedSchemaView.Schemas.ContainsKey(new(Extra.Type, Extra.Version)));
            Assert.Equal(new[] { true }, counts.NextLookups);
            CommitId before = eventOnly.BaseCommitId;
            long frames = repository.GraphStore.AppendedFrames;
            eventOnly.PrepareAndDiscard(first);
            Assert.Equal(before, eventOnly.BaseCommitId);
            Assert.Equal(frames, repository.GraphStore.AppendedFrames);
            Assert.Null(eventOnly.State);
            CommitId initialState = eventOnly.CommitState(first).Id;
            Assert.NotEqual(Describe(repository, before).RootId, Describe(repository, initialState).RootId);
            Assert.Same(first, eventOnly.State);
            Assert.Equal(Describe(repository, initialState).RootId, eventOnly.StateId);
            Assert.Equal(atCreation, eventOnly.BindingEnvironment.SelectedSchemaView.Snapshot!.Address);
            Assert.Equal(5, Assert.IsType<NextRoot>(repository.ReadPair(initialState, initialState).First).Value);
            Assert.Equal(initialState, repository.ReadCurrentCommit(BranchNamed(repository, "event-only")).Commit.Id);
        }
        Dictionary<string, byte[]> files = SnapshotFiles();
        using (Repository publicDefault = Repository.OpenReadOnlyExisting(_path, Models(new()))) {
            Assert.Equal(5, Assert.IsType<NextRoot>(publicDefault.LoadState(publicDefault.ReadCurrentCommit(BranchNamed(publicDefault, "event-only")).Commit.Id)).Value);
        }
        AssertFilesEqual(files);
        string legacyPath = _path + "-legacy";
        Directory.CreateDirectory(legacyPath);
        using (var data = Atelia.FrameStore.FrameStore.Create(Path.Combine(legacyPath, "data"))) {
            using var versions = Atelia.HeadStore.HeadStore.Create(Path.Combine(legacyPath, "versions"), data,
                reservedHeadIdUpperBound: 1);
            versions.CreateHead(1, [0], out _).Unwrap(); // Valid opaque VS payload, incompatible required ref layout.
        }
        var incompatibleFiles = Directory.GetFiles(legacyPath, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
        Assert.Throws<InvalidDataException>(() => Repository.OpenStorageReadOnlyExisting(legacyPath, Models(new())));
        Assert.Equal(incompatibleFiles.Keys.Order(), Directory.GetFiles(legacyPath, "*", SearchOption.AllDirectories).Order());
        foreach ((string path, byte[] bytes) in incompatibleFiles) { Assert.Equal(bytes, File.ReadAllBytes(path)); }
        using Repository positive = Repository.OpenReadOnlyExisting(_path, Models(new()));
        Assert.Equal(5, Assert.IsType<NextRoot>(positive.LoadState(positive.ReadCurrentCommit(BranchNamed(positive, "event-only")).Commit.Id)).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AdapterPreparationFailureIsRetryableAndConfirmedPublicationFailureRequiresAFreshOwner(bool published) {
        Seed();
        FrameAddress old, actual;
        using (Repository repository = Repository.OpenStorageExisting(_path, Models(new()))) {
            using WorkingCopy checkout = repository.OpenWorkingCopy(BranchNamed(repository, "main"));
            CommitId before = checkout.BaseCommitId;
            old = Describe(repository, before).Address;
            NormalizedGraphState baseline = checkout.Baseline!;
            FrameAddress? schema = repository.SharedSchemaRoot;
            DB087Widget root = AssertWidget(checkout.State, 0);
            root.Numbers[7] = 1;
            long frames = repository.GraphStore.AppendedFrames;
            IOException failure = new("split repository adapter boundary");
            if (published) {
                repository.SaveStageObserver = point => { if (point == RepositorySaveStage.AfterPublication) { throw failure; } };
                RepositoryPublicationException error = Assert.Throws<RepositoryPublicationException>(() => checkout.CommitState());
                Assert.Same(failure, error.InnerException);
                Assert.Equal(RepositoryPublicationOutcome.Published, error.Outcome);
                Assert.True(repository.IsFaulted);
                Assert.Same(baseline, checkout.Baseline);
                Assert.Equal(before, checkout.BaseCommitId);
                Assert.Throws<InvalidOperationException>(() => checkout.CommitState());
                actual = error.CandidateRevisionAddress!.Value;
            } else {
                repository.OperationStageObserver = point => { if (point == RepositoryOperationStage.AfterPrepare) { throw failure; } };
                Assert.Same(failure, Assert.Throws<IOException>(() => checkout.CommitState()));
                Assert.False(repository.IsFaulted);
                Assert.Same(baseline, checkout.Baseline);
                Assert.Equal(before, checkout.BaseCommitId);
                Assert.Equal(before, repository.ReadCurrentCommit(BranchNamed(repository, "main")).Commit.Id);
                Assert.Equal(schema, repository.SharedSchemaRoot);
                Assert.Equal(frames, repository.GraphStore.AppendedFrames);
                repository.OperationStageObserver = null;
                actual = Describe(repository, checkout.CommitState().Id).Address;
            }
        }
        using Repository reopened = Repository.OpenStorageReadOnlyExisting(_path, Models(new()));
        Assert.Equal(actual, Describe(reopened, reopened.ReadCurrentCommit(BranchNamed(reopened, "main")).Commit.Id).Address);
        AssertWidget(reopened.LoadState(reopened.ReadCurrentCommit(BranchNamed(reopened, "main")).Commit.Id), 1);
        AssertWidget(BusinessPersistenceAdapter.ReadRoot(reopened.GraphStore, reopened.GraphStore.ReadBusinessGraph(old, null), Models(new())), 0);
    }

    private void Seed(bool withSide = false) {
        using Repository repository = Repository.CreateStorageNew(_path, Models(new()));
        using WorkingCopy main = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", Ring()).Id);
        if (withSide) { using WorkingCopy side = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("side", Ring()).Id); }
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
                counts.NextLookups.Add(SchemaLookupCounterexampleProbe.TryGetSchema(context, Extra.Type, Extra.Version, out _));
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
