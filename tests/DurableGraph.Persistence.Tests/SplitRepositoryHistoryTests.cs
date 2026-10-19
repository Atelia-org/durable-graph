using static Atelia.DurableGraph.Persistence.Tests.DB096TestAccess;
using System.Buffers;

using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using Atelia.HeadStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class SplitRepositoryHistoryTests : IDisposable {
    private const string Prefix = "dg-split-repository-history-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
    private static readonly DurableSchema Extra = new("SplitHistoryExtra", 1, new DurableFieldInfo(1, TypeTag.Int32));
    private static readonly DurableSchema ProbeV1 = new("SplitHistoryProbe", 1, new DurableFieldInfo(1, TypeTag.Int32));
    private static readonly DurableSchema ProbeV2 = new("SplitHistoryProbe", 2, new DurableFieldInfo(1, TypeTag.Int32));

    [Fact]
    public void MixedHistoryTagsHistoricalForksPreserveSourcesAndColdContinueDelta() {
        FrameAddress selected, forkSaved, shared;
        using (Repository repository = Repository.CreateStorageNew(_path, Models())) {
            DB087Widget root = Ring();
            using WorkingCopy main = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", root).Id);
            root = Assert.IsType<DB087Widget>(main.State);
            CommitId s0 = main.BaseCommitId;
            main.Catalog.Set(Extra);
            root.Numbers[7] = 11;
            CommitId e1 = main.CommitEvent(root).Id;
            CommitId e2 = main.CommitEvent(Ring(12)).Id;
            root.Numbers[7] = 2;
            CommitId s1 = main.CommitState().Id;
            root.Numbers[7] = 3;
            CommitId s2 = main.CommitState().Id;
            selected = Describe(repository, e1).Address;
            shared = repository.SharedSchemaRoot!.Value;
            Assert.Equal(new[] { s0, e1, e2, s1, s2 }, repository.ReadFrames(BranchNamed(repository, "main")).Select(commit => commit.Id));
            Assert.Equal(new[] { e1, e2 }, repository.EnumerateEvents(s2, HistoryOrder.OldestFirst).Select(commit => commit.Id));
            Assert.Equal(new[] { e2, e1 }, repository.EnumerateEvents(s2).Select(commit => commit.Id));
            Assert.Equal(new[] { e2 }, repository.EnumerateEvents(s2, afterExclusive: e1).Select(commit => commit.Id));
            Assert.Equal(s0, repository.GetPreviousState(e1));
            Assert.Equal(s0, repository.GetPreviousState(e2));

            long frames = repository.GraphStore.AppendedFrames;
            repository.CreateTag("selected", e1);
            Assert.Equal(e1, repository.ResolveTag("selected"));
            BranchInfo referenceBranch = repository.ForkBranch(e1, "reference");
            Assert.Equal(e1, repository.ReadCurrentCommit(referenceBranch.Id).Commit.Id);
            using WorkingCopy reference = repository.OpenWorkingCopy(BranchNamed(repository, "reference"));
            using WorkingCopy fork = repository.OpenWorkingCopy(repository.ForkBranch(e1, "fork").Id);
            Assert.Equal(e1, fork.BaseCommitId); // The source branch has advanced to S2; this is the selected historical head.
            Assert.Equal(Describe(repository, s0).Address, fork.StateRevisionAddress);
            Assert.Equal(reference.StateRevisionAddress, fork.StateRevisionAddress);
            DB087Widget forkRoot = AssertRing(fork.State, 0);
            DB087Widget referenceRoot = AssertRing(reference.State, 0);
            Assert.NotSame(root, forkRoot);
            Assert.NotSame(forkRoot, referenceRoot);
            Assert.NotSame(forkRoot.Numbers, referenceRoot.Numbers);
            Assert.True(repository.GraphStore.AppendedFrames > frames); // D/T metadata is persisted; no business Commit is fabricated.
            Assert.Equal(shared, repository.SharedSchemaRoot); // A historical branch does not roll back shared Schema.

            ObjectId listId = Assert.Single(fork.Baseline!.Objects, pair => pair.Value.Current.Kind == ObjectStateKind.List).Key;
            ObjectStorageInfo prior = fork.Baseline.Objects[listId].Storage!.Value;
            forkRoot.Numbers[7] = 4;
            CommitId saved = fork.CommitState().Id;
            forkSaved = Describe(repository, saved).Address;
            StoredGraphRevision graph = Snapshot(repository, saved);
            Assert.Equal(e1.Value, ResourcesOf(repository).ReadPrevious(saved.Value)!.Id);
            Assert.Equal(Describe(repository, s0).Address, graph.SourceGraph);
            ObjectVersionRecord delta = Assert.Single(repository.GraphStore.ReadBatch(graph.OwnedBatch!.Value).Records);
            Assert.Equal(listId.Value, delta.ObjectId);
            Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
            Assert.Equal(prior.Head, delta.PriorAddress);
            AssertRing(reference.State, 0);
            AssertRing(main.State, 3);
            Assert.Equal(s2, main.BaseCommitId);
            reference.Dispose();
            frames = repository.GraphStore.AppendedFrames;
            BranchInfo atLatest = repository.ForkBranch(s2, "reference-latest");
            Assert.Equal(s2, repository.ReadCurrentCommit(atLatest.Id).Commit.Id);
            Assert.Equal(e1, repository.ReadCurrentCommit(referenceBranch.Id).Commit.Id);
            Assert.True(repository.GraphStore.AppendedFrames > frames);
            Assert.Equal(shared, repository.SharedSchemaRoot);
            Assert.Equal(e1, repository.ResolveTag("selected"));
        }
        using (Repository repository = Repository.OpenStorageExisting(_path, Models())) {
            CommitId restoredTag = repository.ResolveTag("selected");
            Assert.Equal(selected, Describe(repository, restoredTag).Address);
            AssertRing(repository.LoadEvent(restoredTag), 11);
            Assert.Equal(shared, repository.SharedSchemaRoot);
            using WorkingCopy fork = repository.OpenWorkingCopy(BranchNamed(repository, "fork"));
            DB087Widget root = AssertRing(fork.State, 4);
            Assert.Equal(forkSaved, fork.StateRevisionAddress);
            ObjectId listId = Assert.Single(fork.Baseline!.Objects, pair => pair.Value.Current.Kind == ObjectStateKind.List).Key;
            ObjectStorageInfo prior = fork.Baseline.Objects[listId].Storage!.Value;
            root.Numbers[8] = 5;
            CommitId saved = fork.CommitState().Id;
            StoredGraphRevision graph = Snapshot(repository, saved);
            ObjectVersionRecord delta = Assert.Single(repository.GraphStore.ReadBatch(graph.OwnedBatch!.Value).Records);
            Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
            Assert.Equal(prior.Head, delta.PriorAddress);
            Assert.Equal(prior.ReconstructionPayloadBytes + ObjectVersionPayloadSize.GetDeltaPayloadBytes(delta.Body.Length, prior.Head),
                fork.Baseline!.Objects[listId].Storage!.Value.ReconstructionPayloadBytes);
            Assert.Equal(forkSaved, graph.SourceGraph);
            DB087Widget latest = AssertRing(repository.LoadState(saved), 4);
            Assert.Equal(5, latest.Numbers[8]);
            AssertRing(repository.LoadState(repository.ReadCurrentCommit(BranchNamed(repository, "main")).Commit.Id), 3);
            AssertRing(repository.LoadEvent(repository.ResolveTag("selected")), 11);
        }
    }

    [Fact]
    public void MetadataNavigationDoesNotDecodeBusinessOrFollowAnInvalidDirectoryRole() {
        using (Repository seed = Repository.CreateStorageNew(_path, Models())) {
            using WorkingCopy main = seed.OpenWorkingCopy(seed.CreateRootBranchFromState("main", Ring()).Id);
            seed.CreateTag("valid", main.BaseCommitId);
        }
        CommitId valid, selected;
        FrameAddress bad;
        using (RepositoryResources resources = RepositoryResources.OpenExisting(_path)) {
            HeadId branch = Assert.Single(resources.ListBranches()).Key;
            HistoryGraphRecord state = resources.ReadCurrent(branch);
            valid = new(state.Id);
            bad = resources.Data.Append(GraphFrameCodec.GraphTag,
                GraphFrameCodec.EncodeGraph(StoredGraphKind.Event, null, state.RootId,
                    state.Address, resources.SharedSchemaRoot, null)).Unwrap();
            selected = new(resources.Heads.PublishHead(branch, BusinessEntryPayloadCodec.Encode(bad, []), out _).Unwrap());
        }
        Counts counts = new() { ThrowBusinessCallbacks = true };
        using Repository repository = Repository.OpenStorageExisting(_path, Models(counts));
        repository.CreateTag("bad-directory", selected);
        Assert.Equal(selected, repository.ResolveTag("bad-directory"));
        BranchInfo metadata = repository.ForkBranch(selected, "metadata");
        Assert.Equal(new[] { valid, selected }, repository.ReadFrames(metadata.Id).Select(commit => commit.Id));
        Assert.Equal(new[] { selected }, repository.EnumerateEvents(selected).Select(commit => commit.Id));
        Assert.Equal(valid, repository.GetPreviousState(selected));
        Assert.Equal(new[] { "main", "metadata" }, repository.ListBranches().Select(branch => branch.DisplayLabel).Order());
        Assert.Equal(0, counts.Factories);
        Assert.Equal(0, counts.Readers);
        Assert.Throws<InvalidDataException>(() => Snapshot(repository, selected));
        Assert.Throws<InvalidDataException>(() => repository.LoadEvent(selected));
        Assert.Equal(0, counts.Factories);
        Assert.Equal(0, counts.Readers);
        counts.ThrowBusinessCallbacks = false;
        AssertRing(repository.LoadState(valid), 0); // Same registered callbacks really run during typed reading.
        Assert.True(counts.Readers > 0);
        Assert.True(counts.Factories > 0);
        Assert.False(repository.IsFaulted);
    }

    [Fact]
    public void ReadCheckpointUsesEachGraphsAccurateSchemaAndReturnsIndependentMutableRoots() {
        using (Repository seed = Repository.CreateStorageNew(_path, ProbeModels(1, new()))) {
            using WorkingCopy main = seed.OpenWorkingCopy(seed.CreateRootBranchFromState("main", new ProbeRoot { Value = 7 }).Id);
            main.Catalog.Set(Extra);
            main.CommitEvent(main.State!);
        }
        Dictionary<string, byte[]> files = SnapshotFiles(_path);
        Counts counts = new();
        using (Repository reader = Repository.OpenStorageReadOnlyExisting(_path, ProbeModels(2, counts))) {
            CommitId head = reader.ReadCurrentCommit(BranchNamed(reader, "main")).Commit.Id;
            CommitId previous = reader.GetPreviousState(head)!.Value;
            Assert.NotEqual(reader.GraphStore.ReadMetadata(Describe(reader, head).Address).SchemaRoot,
                reader.GraphStore.ReadMetadata(Describe(reader, previous).Address).SchemaRoot);
            EventCheckpoint result = Assert.IsType<EventCheckpoint>(reader.ReadCheckpoint(head));
            Assert.Equal(previous, result.PreviousStateCommit?.Id);
            ProbeRoot domainEvent = Assert.IsType<ProbeRoot>(result.Event);
            ProbeRoot state = Assert.IsType<ProbeRoot>(result.PreviousState);
            Assert.Equal(107, domainEvent.Value);
            Assert.Equal(7, state.Value);
            Assert.NotSame(domainEvent, state);
            Assert.Equal(new[] { true, false }, counts.Lookups);
            Assert.Equal(2, counts.Normalizes); // Both actual stored V1 rows normalize to V2 in their own view.
            domainEvent.Value = 900;
            Assert.Equal(7, state.Value);
            Assert.Equal(head, reader.ReadCurrentCommit(BranchNamed(reader, "main")).Commit.Id);
        }
        AssertFilesEqual(files, SnapshotFiles(_path));
    }

    [Fact]
    public void EventOnlyHistoricalForkNeedsNoEventModelAndFirstStateHasNoContentSource() {
        FrameAddress selected, shared;
        using (Repository seed = Repository.CreateStorageNew(_path, Models())) {
            using WorkingCopy main = seed.OpenWorkingCopy(seed.CreateRootBranchFromEvent("main", Ring(1)).Id);
            selected = Describe(seed, main.BaseCommitId).Address;
            seed.CreateTag("prefix", main.BaseCommitId);
            main.Catalog.Set(Extra);
            main.CommitEvent(Ring(2));
            shared = seed.SharedSchemaRoot!.Value;
        }
        using (Repository noModels = Repository.OpenStorageExisting(_path, new())) {
            CommitId prefix = noModels.ResolveTag("prefix");
            long frames = noModels.GraphStore.AppendedFrames;
            using WorkingCopy fork = noModels.OpenWorkingCopy(noModels.ForkBranch(prefix, "empty").Id);
            Assert.Null(fork.State);
            Assert.Null(fork.StateRevisionAddress);
            Assert.Null(fork.Baseline);
            Assert.Equal(prefix, fork.BaseCommitId);
            Assert.Equal(shared, fork.BindingEnvironment!.SelectedSchemaView.Snapshot!.Address);
            Assert.True(noModels.GraphStore.AppendedFrames > frames);
            Assert.Null(noModels.GetPreviousState(prefix));
        }
        using Repository repository = Repository.OpenStorageExisting(_path, Models());
        using WorkingCopy resumed = repository.OpenWorkingCopy(BranchNamed(repository, "empty"));
        Assert.Null(resumed.State);
        CommitId first = resumed.CommitState(Ring(3)).Id;
        StoredGraphRevision graph = Snapshot(repository, first);
        Assert.Equal(selected, ResourcesOf(repository).ReadPrevious(first.Value)!.Address);
        Assert.Null(graph.SourceGraph);
        Assert.All(repository.GraphStore.ReadBatch(graph.OwnedBatch!.Value).Records,
            row => Assert.Equal(ObjectVersionKind.Base, row.Kind));
        AssertRing(resumed.State, 3);
        Assert.Equal(shared, repository.SharedSchemaRoot);
    }

    [Fact]
    public void HistoricalForkRestoreFailureKeepsTheCreatedBranchAndReleasesWorkingCopyOccupancy() {
        using (Repository seed = Repository.CreateStorageNew(_path, ProbeModels(1, new()))) {
            using WorkingCopy main = seed.OpenWorkingCopy(seed.CreateRootBranchFromState("main", new ProbeRoot { Value = 7 }).Id);
            main.CommitEvent(new ProbeRoot { Value = 8 });
        }
        Counts counts = new() { FailAllocate = true };
        using Repository repository = Repository.OpenStorageExisting(_path, ProbeModels(1, counts));
        CommitId source = repository.ReadCurrentCommit(BranchNamed(repository, "main")).Commit.Id;
        BranchInfo created = repository.ForkBranch(source, "retry");
        var directory = ResourcesOf(repository).Heads.ReadHead(1).Unwrap();
        long frames = repository.GraphStore.AppendedFrames;
        Assert.Throws<InvalidDataException>(() => repository.OpenWorkingCopy(created.Id));
        Assert.False(repository.IsFaulted);
        Assert.Equal(source, repository.ReadCurrentCommit(created.Id).Commit.Id);
        Assert.Equal(directory.Id, ResourcesOf(repository).Heads.ReadHead(1).Unwrap().Id);
        Assert.Equal(frames, repository.GraphStore.AppendedFrames);
        counts.FailAllocate = false;
        using WorkingCopy retry = repository.OpenWorkingCopy(created.Id);
        Assert.Equal(7, Assert.IsType<ProbeRoot>(retry.State).Value);
        Assert.Equal(source, retry.BaseCommitId);
        Assert.Equal(frames, repository.GraphStore.AppendedFrames);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForkRegistrationFailurePreservesSharedSchemaAndHasExplicitColdRecovery(bool published) {
        CommitId source;
        HeadEntryId sharedRevision;
        byte[] sharedPayload;
        BranchId? candidate;
        using (Repository seed = Repository.CreateStorageNew(_path, Models())) {
            BranchInfo main = seed.CreateRootBranchFromState("main", Ring());
            source = seed.ReadCurrentCommit(main.Id).Commit.Id;
        }
        using (Repository repository = Repository.OpenStorageExisting(_path, Models())) {
            var shared = ResourcesOf(repository).Heads.ReadHead(2).Unwrap();
            sharedRevision = shared.Id;
            sharedPayload = shared.Payload.ToArray();
            IOException injected = new("split fork registration window");
            repository.SaveStageObserver = point => {
                if (point == (published ? RepositorySaveStage.AfterPublication : RepositorySaveStage.BeforePublication)) { throw injected; }
            };
            RepositoryPublicationException error = Assert.Throws<RepositoryPublicationException>(() => repository.ForkBranch(source, "candidate"));
            Assert.Same(injected, error.InnerException);
            Assert.Equal(published ? RepositoryPublicationOutcome.Published : RepositoryPublicationOutcome.NotPublished, error.Outcome);
            Assert.True(repository.IsFaulted); // A physical Head and complete D candidate already exist.
            candidate = error.TargetBranchId;
            Assert.NotNull(candidate);
        }
        using Repository reopened = Repository.OpenStorageReadOnlyExisting(_path, Models());
        var selected = ResourcesOf(reopened).Heads.ReadHead(2).Unwrap();
        Assert.Equal(sharedRevision, selected.Id);
        Assert.Equal(sharedPayload, selected.Payload.ToArray());
        Assert.Equal(published ? 2 : 1, reopened.ListBranches().Length);
        if (published) {
            Assert.Equal(source, reopened.ReadCurrentCommit(candidate!.Value).Commit.Id);
            AssertRing(reopened.LoadState(source), 0);
        } else {
            Assert.Throws<KeyNotFoundException>(() => reopened.ReadCurrentCommit(candidate!.Value));
        }
    }

    [Fact]
    public void ReadCheckpointRejectsAnAllocatorSharingOneMutableInstanceAcrossBothGraphsBeforeHydration() {
        using (Repository seed = Repository.CreateStorageNew(_path, ProbeModels(1, new()))) {
            using WorkingCopy main = seed.OpenWorkingCopy(seed.CreateRootBranchFromState("main", new ProbeRoot { Value = 7 }).Id);
            main.CommitEvent(new ProbeRoot { Value = 8 });
        }
        Counts counts = new() { Singleton = new() { Value = 100 } };
        using Repository repository = Repository.OpenStorageReadOnlyExisting(_path, ProbeModels(1, counts));
        Checkpoint? delivered = null;
        Assert.Throws<InvalidDataException>(() => delivered = repository.ReadCheckpoint(repository.ReadCurrentCommit(BranchNamed(repository, "main")).Commit.Id));
        Assert.Null(delivered);
        Assert.Equal(0, counts.Hydrates);
        Assert.Equal(100, counts.Singleton!.Value);
        Assert.False(repository.IsFaulted);
        counts.Singleton = null;
        EventCheckpoint positive = Assert.IsType<EventCheckpoint>(repository.ReadCheckpoint(repository.ReadCurrentCommit(BranchNamed(repository, "main")).Commit.Id));
        Assert.Equal(8, Assert.IsType<ProbeRoot>(positive.Event).Value);
        Assert.Equal(7, Assert.IsType<ProbeRoot>(positive.PreviousState).Value);
        Assert.NotSame(positive.Event, positive.PreviousState);
        Assert.Equal(2, counts.Hydrates);
    }

    [Fact]
    public void HistoryHandlesSurviveReopenWhileOccupancyReadonlyAndIteratorLifetimeRemainChecked() {
        CommitId old;
        using (Repository seed = Repository.CreateStorageNew(_path, Models())) {
            using WorkingCopy main = seed.OpenWorkingCopy(seed.CreateRootBranchFromState("main", Ring()).Id);
            old = main.CommitEvent(Ring(1)).Id;
            seed.CreateTag("event", old);
        }
        using (Repository repository = Repository.OpenStorageExisting(_path, Models())) {
            CommitId current = repository.ResolveTag("event");
            Assert.Equal(current, old);
            BranchInfo fromOld = repository.ForkBranch(old, "same-label");
            using WorkingCopy inherited = repository.OpenWorkingCopy(fromOld.Id);
            Assert.Equal(old, inherited.BaseCommitId);
            repository.CreateTag("survives-owner", old);
            Assert.IsType<EventCheckpoint>(repository.ReadCheckpoint(old));
            Assert.Throws<ArgumentException>(() => repository.ReadCheckpoint(default));
            Assert.Empty(repository.EnumerateEvents(current, afterExclusive: old));
            using WorkingCopy active = repository.OpenWorkingCopy(BranchNamed(repository, "main"));
            Assert.Throws<InvalidOperationException>(() => repository.OpenWorkingCopy(active.BranchId));
            BranchInfo duplicateLabel = repository.ForkBranch(current, "main");
            Assert.NotEqual(active.BranchId, duplicateLabel.Id);
            Assert.Throws<InvalidOperationException>(() => repository.CreateTag("event", current));
            active.Dispose();
            Assert.Equal(current, repository.ReadCurrentCommit(active.BranchId).Commit.Id);
            Assert.False(repository.IsFaulted);
            repository.ForkBranch(current, "positive");
            Assert.Equal(current, repository.ReadCurrentCommit(BranchNamed(repository, "positive")).Commit.Id);
        }
        Dictionary<string, byte[]> files = SnapshotFiles(_path);
        using (Repository reader = Repository.OpenStorageReadOnlyExisting(_path, Models())) {
            CommitId current = reader.ResolveTag("event");
            Assert.Throws<InvalidOperationException>(() => reader.ForkBranch(current, "blocked"));
            Assert.Throws<InvalidOperationException>(() => reader.OpenWorkingCopy(reader.ForkBranch(current, "blocked").Id));
            Assert.Throws<InvalidOperationException>(() => reader.OpenWorkingCopy(reader.ListBranches()[0].Id));
            Assert.Throws<InvalidOperationException>(() => reader.CreateTag("blocked", current));
            Assert.IsType<EventCheckpoint>(reader.ReadCheckpoint(current));
            using IEnumerator<CommitInfo> events = reader.EnumerateEvents(current).GetEnumerator();
            Assert.True(events.MoveNext());
            Assert.False(events.MoveNext());
            reader.Dispose();
            Assert.Throws<ObjectDisposedException>(() => events.MoveNext()); // Also after exhaustion.
        }
        AssertFilesEqual(files, SnapshotFiles(_path));
    }

    private static StateModelRegistry Models(Counts? counts = null) {
        StateModelRegistry models = new();
        StateDefinitionBinding original = global::Atelia.DurableGraph.Generated.Family_5030576964676574.Definition;
        if (counts is null) { models.Register(original); }
        else {
            models.Register(new StateDefinitionBinding(original.DefinitionId, original.Kind, original.Arity,
                original.DomainTypeDefinition, original.Templates,
                currentModelFactory: (type, context) => {
                    counts.Factories++;
                    if (counts.ThrowBusinessCallbacks) { throw new InvalidDataException("Metadata invoked a business factory."); }
                    return original.CurrentModelFactory!(type, context);
                }, historicalReaderFactory: (schema, context) => {
                    counts.Readers++;
                    if (counts.ThrowBusinessCallbacks) { throw new InvalidDataException("Metadata invoked a business reader factory."); }
                    return original.HistoricalReaderFactory!(schema, context);
                }, upgrades: original.Upgrades));
        }
        return models;
    }
    private static DB087Widget Ring(int number = 0) {
        List<int> numbers = Enumerable.Repeat(0, 128).ToList();
        numbers[7] = number;
        DB087Widget root = new() { Value = 1, Numbers = numbers };
        root.Child = new() { Value = 2, Numbers = numbers, Child = root };
        return root;
    }
    private static DB087Widget AssertRing(IDurableObject? value, int number) {
        DB087Widget root = Assert.IsType<DB087Widget>(value);
        Assert.Equal(1, root.Value);
        DB087Widget child = Assert.IsType<DB087Widget>(root.Child);
        Assert.Equal(2, child.Value);
        Assert.Same(root, child.Child);
        Assert.Same(root.Numbers, child.Numbers);
        Assert.Equal(128, root.Numbers.Count);
        Assert.Equal(number, root.Numbers[7]);
        for (int i = 0; i < root.Numbers.Count; i++) { if (i is not 7 and not 8) { Assert.Equal(0, root.Numbers[i]); } }
        return root;
    }

    private static StateModelRegistry ProbeModels(int currentVersion, Counts counts) {
        StateModelRegistry models = new();
        models.Register(new StateDefinitionBinding(ProbeV1.SchemaId, SchemaKind.ReferenceObject, 0, typeof(ProbeRoot),
            new StateSchemaTemplate[] {
                new(ProbeV1.SchemaId, 1, SchemaKind.ReferenceObject, 0, [new(1, TypeExpr.Builtin(TypeTag.Int32))]),
                new(ProbeV1.SchemaId, 2, SchemaKind.ReferenceObject, 0, [new(1, TypeExpr.Builtin(TypeTag.Int32))]),
            }.Take(currentVersion),
            currentModelFactory: (type, context) => {
                Assert.Equal(typeof(ProbeRoot), type);
                bool extra = SchemaLookupCounterexampleProbe.TryGetSchema(context, Extra.Type, Extra.Version, out _);
                counts.Lookups.Add(extra);
                DurableSchema current = currentVersion == 1 ? ProbeV1 : ProbeV2;
                return new StateModelBinding<ProbeRoot, int>(new(current, BaseInt,
                    static (in int prior, in int now) => new(prior != now, BaseInt(in now).Body)),
                    Enumerable.Range(1, currentVersion).Select(version => context.ResolveReader(version == 1 ? ProbeV1 : ProbeV2)),
                    row => { counts.Normalizes++; return row.GetState<int>() + (extra ? 100 : 0); },
                    () => {
                        if (counts.FailAllocate) { throw new InvalidDataException("Fork allocation failed."); }
                        return counts.Singleton ?? new ProbeRoot();
                    }, (ProbeRoot target, in int state, ObjectReadTable _) => { counts.Hydrates++; target.Value = state; },
                    static (domain, _) => domain.Value, static (in int _, IStateReferenceVisitor _) => { });
            }, historicalReaderFactory: static (schema, _) => new StateReaderBinding<int>(schema,
                static (ref BareValueReader reader) => reader.ReadVarInt32(),
                static (ref BareValueReader reader, in int _) => reader.ReadVarInt32(),
                static (in int _, IStateReferenceVisitor _) => { })));
        return models;
    }
    private static PreparedBaseBody BaseInt(in int value) {
        ArrayBufferWriter<byte> buffer = new();
        BareValueWriter writer = new(buffer);
        writer.WriteVarInt32(value);
        return new(buffer.WrittenSpan);
    }
    private sealed class ProbeRoot : IDurableObject { internal int Value; }
    private sealed class Counts {
        internal int Factories, Readers, Normalizes, Hydrates;
        internal bool ThrowBusinessCallbacks, FailAllocate;
        internal ProbeRoot? Singleton;
        internal List<bool> Lookups { get; } = [];
    }
    private static Dictionary<string, byte[]> SnapshotFiles(string path) => Directory.GetFiles(path, "*", SearchOption.AllDirectories)
        .ToDictionary(name => Path.GetRelativePath(path, name), File.ReadAllBytes);
    private static void AssertFilesEqual(Dictionary<string, byte[]> expected, Dictionary<string, byte[]> actual) {
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach ((string name, byte[] bytes) in expected) { Assert.Equal(bytes, actual[name]); }
    }
    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}
