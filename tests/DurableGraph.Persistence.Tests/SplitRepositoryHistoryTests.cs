using System.Buffers;
using Atelia.DurableGraph.Persistence.MetaBootstrapP0;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using Atelia.VersionStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class SplitRepositoryHistoryTests : IDisposable {
    private const string Prefix = "dg-split-repository-history-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
    private static readonly DurableSchema Extra = new("SplitHistoryExtra", 1, new DurableFieldInfo(1, TypeTag.Int32));
    private static readonly DurableSchema ProbeV1 = new("SplitHistoryProbe", 1, new DurableFieldInfo(1, TypeTag.Int32));
    private static readonly DurableSchema ProbeV2 = new("SplitHistoryProbe", 2, new DurableFieldInfo(1, TypeTag.Int32));

    [Fact]
    public void MixedHistoryTagsHistoricalBranchesForkAndMovePreserveSourcesAndColdContinueDelta() {
        FrameAddress selected, forkSaved, shared;
        using (Repository repository = Repository.CreateMetaBootstrapNew(_path, Models(), schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared)) {
            DB087Widget root = Ring();
            using BranchCheckout main = repository.CreateBranch("main", root);
            CheckpointAddress s0 = main.Head;
            main.Catalog.Set(Extra);
            root.Numbers[7] = 11;
            CheckpointAddress e1 = main.CommitEvent(root);
            CheckpointAddress e2 = main.CommitEvent(Ring(12));
            root.Numbers[7] = 2;
            CheckpointAddress s1 = main.CommitState();
            root.Numbers[7] = 3;
            CheckpointAddress s2 = main.CommitState();
            selected = e1.RevisionAddress;
            shared = repository.SharedSchemaRoot!.Value;
            Assert.Equal(new[] { s0, e1, e2, s1, s2 }, repository.ReadFrames("main"));
            Assert.Equal(new[] { e1, e2 }, repository.EnumerateEvents(s2, HistoryOrder.OldestFirst));
            Assert.Equal(new[] { e2, e1 }, repository.EnumerateEvents(s2));
            Assert.Equal(new[] { e2 }, repository.EnumerateEvents(s2, afterExclusive: e1));
            Assert.Equal(s0, repository.GetPreviousState(e1));
            Assert.Equal(s0, repository.GetPreviousState(e2));

            long frames = repository.MetaBootstrapStore.AppendedFrames;
            repository.CreateTag("selected", e1);
            Assert.Equal(e1, repository.ResolveTag("selected"));
            Assert.Equal(e1, repository.CreateBranch("reference", e1));
            using BranchCheckout reference = repository.Checkout("reference");
            using BranchCheckout fork = repository.Fork("fork", e1);
            Assert.Equal(e1, fork.Head); // The source branch has advanced to S2; this is the selected historical head.
            Assert.Equal(s0.RevisionAddress, fork.StateRevisionAddress);
            Assert.Equal(reference.StateRevisionAddress, fork.StateRevisionAddress);
            DB087Widget forkRoot = AssertRing(fork.State, 0);
            DB087Widget referenceRoot = AssertRing(reference.State, 0);
            Assert.NotSame(root, forkRoot);
            Assert.NotSame(forkRoot, referenceRoot);
            Assert.NotSame(forkRoot.Numbers, referenceRoot.Numbers);
            Assert.Equal(frames, repository.MetaBootstrapStore.AppendedFrames);
            Assert.Equal(shared, repository.SharedSchemaRoot); // A historical branch does not roll back shared Schema.

            ObjectId listId = Assert.Single(fork.Baseline!.Objects, pair => pair.Value.Current.Kind == ObjectStateKind.List).Key;
            ObjectStorageInfo prior = fork.Baseline.Objects[listId].Storage!.Value;
            forkRoot.Numbers[7] = 4;
            CheckpointAddress saved = fork.CommitState();
            forkSaved = saved.RevisionAddress;
            P0GraphSnapshot graph = repository.MetaBootstrapStore.ReadGraph(forkSaved);
            Assert.Equal(e1.RevisionAddress, graph.Parent);
            Assert.Equal(s0.RevisionAddress, graph.SourceGraph);
            ObjectVersionRecord delta = Assert.Single(repository.MetaBootstrapStore.ReadBatch(graph.OwnedBatch!.Value).Records);
            Assert.Equal(listId.Value, delta.ObjectId);
            Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
            Assert.Equal(prior.Head, delta.PriorAddress);
            AssertRing(reference.State, 0);
            AssertRing(main.State, 3);
            Assert.Equal(s2, main.Head);
            reference.Dispose();
            frames = repository.MetaBootstrapStore.AppendedFrames;
            repository.MoveBranch("reference", e1, s2);
            Assert.Equal(s2, repository.GetHead("reference"));
            repository.MoveBranch("reference", s2, e1);
            Assert.Equal(e1, repository.GetHead("reference"));
            Assert.Equal(frames, repository.MetaBootstrapStore.AppendedFrames);
            Assert.Equal(shared, repository.SharedSchemaRoot);
            Assert.Equal(e1, repository.ResolveTag("selected"));
        }
        using (Repository repository = Repository.OpenMetaBootstrapExisting(_path, Models(), schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared)) {
            CheckpointAddress restoredTag = repository.ResolveTag("selected");
            Assert.Equal(selected, restoredTag.RevisionAddress);
            AssertRing(repository.ReadEvent(restoredTag), 11);
            Assert.Equal(shared, repository.SharedSchemaRoot);
            using BranchCheckout fork = repository.Checkout("fork");
            DB087Widget root = AssertRing(fork.State, 4);
            Assert.Equal(forkSaved, fork.StateRevisionAddress);
            ObjectId listId = Assert.Single(fork.Baseline!.Objects, pair => pair.Value.Current.Kind == ObjectStateKind.List).Key;
            ObjectStorageInfo prior = fork.Baseline.Objects[listId].Storage!.Value;
            root.Numbers[8] = 5;
            CheckpointAddress saved = fork.CommitState();
            P0GraphSnapshot graph = repository.MetaBootstrapStore.ReadGraph(saved.RevisionAddress);
            ObjectVersionRecord delta = Assert.Single(repository.MetaBootstrapStore.ReadBatch(graph.OwnedBatch!.Value).Records);
            Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
            Assert.Equal(prior.Head, delta.PriorAddress);
            Assert.Equal(prior.ReconstructionPayloadBytes + ObjectVersionPayloadSize.GetDeltaPayloadBytes(delta.Body.Length, prior.Head),
                fork.Baseline!.Objects[listId].Storage!.Value.ReconstructionPayloadBytes);
            Assert.Equal(forkSaved, graph.SourceGraph);
            DB087Widget latest = AssertRing(repository.ReadState(saved), 4);
            Assert.Equal(5, latest.Numbers[8]);
            AssertRing(repository.ReadState(repository.GetHead("main")), 3);
            AssertRing(repository.ReadEvent(repository.ResolveTag("selected")), 11);
        }
    }

    [Fact]
    public void MetadataNavigationDoesNotDecodeBusinessOrFollowAnInvalidDirectoryRole() {
        using (Repository seed = Repository.CreateMetaBootstrapNew(_path, Models(), schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared)) {
            using BranchCheckout main = seed.CreateBranch("main", Ring());
            seed.CreateTag("valid", main.Head);
        }
        Counts counts = new() { ThrowBusinessCallbacks = true };
        using Repository repository = Repository.OpenMetaBootstrapExisting(_path, Models(counts), schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared);
        CheckpointAddress valid = repository.GetHead("main");
        GraphResources resources = ResourcesOf(repository);
        FrameAddress bad = resources.Data.Append(P0GraphWire.GraphTag,
            P0GraphWire.EncodeGraph(P0GraphKind.Event, valid.RevisionAddress, valid.RootId,
                valid.RevisionAddress, repository.SharedSchemaRoot, null)).Unwrap();
        // A complete experimental graph envelope points to a graph frame in the directory slot.
        // This never rewrites a completed frame. The tag is published through the real VS data-first barrier.
        byte[] payload = new byte[FrameAddress.EncodedSize];
        Assert.True(bad.TryWrite(payload));
        resources.Versions.CreateTag("bad-directory", payload, out _).Unwrap();
        CheckpointAddress selected = repository.ResolveTag("bad-directory");
        Assert.Equal(bad, selected.RevisionAddress);
        Assert.Equal(selected, repository.CreateBranch("metadata", selected));
        Assert.Equal(new[] { valid, selected }, repository.ReadFrames("metadata"));
        Assert.Equal(new[] { selected }, repository.EnumerateEvents(repository.GetHead("metadata")));
        Assert.Equal(valid, repository.GetPreviousState(selected));
        Assert.Equal(new[] { "main", "metadata" }, repository.ListBranches());
        Assert.Equal(0, counts.Factories);
        Assert.Equal(0, counts.Readers);
        Assert.Throws<InvalidDataException>(() => repository.MetaBootstrapStore.ReadGraph(bad));
        Assert.Throws<InvalidDataException>(() => repository.ReadEvent(selected));
        Assert.Equal(0, counts.Factories);
        Assert.Equal(0, counts.Readers);
        counts.ThrowBusinessCallbacks = false;
        AssertRing(repository.ReadState(valid), 0); // Same registered callbacks really run during typed reading.
        Assert.True(counts.Readers > 0);
        Assert.True(counts.Factories > 0);
        Assert.False(repository.IsFaulted);
    }

    [Fact]
    public void ReadCheckpointUsesEachGraphsAccurateSchemaAndReturnsIndependentMutableRoots() {
        using (Repository seed = Repository.CreateMetaBootstrapNew(_path, ProbeModels(1, new()), schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared)) {
            using BranchCheckout main = seed.CreateBranch("main", new ProbeRoot { Value = 7 });
            main.Catalog.Set(Extra);
            main.CommitEvent(main.State!);
        }
        Dictionary<string, byte[]> files = SnapshotFiles(_path);
        Counts counts = new();
        using (Repository reader = Repository.OpenMetaBootstrapReadOnlyExisting(_path, ProbeModels(2, counts))) {
            CheckpointAddress head = reader.GetHead("main");
            CheckpointAddress previous = reader.GetPreviousState(head)!;
            Assert.NotEqual(reader.MetaBootstrapStore.ReadMetadata(head.RevisionAddress).SchemaRoot,
                reader.MetaBootstrapStore.ReadMetadata(previous.RevisionAddress).SchemaRoot);
            EventCheckpoint result = Assert.IsType<EventCheckpoint>(reader.ReadCheckpoint(head));
            Assert.Equal(previous, result.PreviousStateAddress);
            ProbeRoot domainEvent = Assert.IsType<ProbeRoot>(result.Event);
            ProbeRoot state = Assert.IsType<ProbeRoot>(result.PreviousState);
            Assert.Equal(107, domainEvent.Value);
            Assert.Equal(7, state.Value);
            Assert.NotSame(domainEvent, state);
            Assert.Equal(new[] { true, false }, counts.Lookups);
            Assert.Equal(2, counts.Normalizes); // Both actual stored V1 rows normalize to V2 in their own view.
            domainEvent.Value = 900;
            Assert.Equal(7, state.Value);
            Assert.Equal(head, reader.GetHead("main"));
        }
        AssertFilesEqual(files, SnapshotFiles(_path));
    }

    [Fact]
    public void EventOnlyHistoricalForkNeedsNoEventModelAndFirstStateHasNoContentParent() {
        FrameAddress selected, shared;
        using (Repository seed = Repository.CreateMetaBootstrapNew(_path, Models(), schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared)) {
            using BranchCheckout main = seed.CreateBranchFromEvent("main", Ring(1));
            selected = main.Head.RevisionAddress;
            seed.CreateTag("prefix", main.Head);
            main.Catalog.Set(Extra);
            main.CommitEvent(Ring(2));
            shared = seed.SharedSchemaRoot!.Value;
        }
        using (Repository noModels = Repository.OpenMetaBootstrapExisting(_path, new(), schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared)) {
            CheckpointAddress prefix = noModels.ResolveTag("prefix");
            long frames = noModels.MetaBootstrapStore.AppendedFrames;
            using BranchCheckout fork = noModels.Fork("empty", prefix);
            Assert.Null(fork.State);
            Assert.Null(fork.StateRevisionAddress);
            Assert.Null(fork.Baseline);
            Assert.Equal(prefix, fork.Head);
            Assert.Equal(shared, fork.BindingEnvironment!.SelectedSchemaView.Snapshot!.Address);
            Assert.Equal(frames, noModels.MetaBootstrapStore.AppendedFrames);
            Assert.Null(noModels.GetPreviousState(prefix));
        }
        using Repository repository = Repository.OpenMetaBootstrapExisting(_path, Models(), schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared);
        using BranchCheckout resumed = repository.Checkout("empty");
        Assert.Null(resumed.State);
        CheckpointAddress first = resumed.CommitState(Ring(3));
        P0GraphSnapshot graph = repository.MetaBootstrapStore.ReadGraph(first.RevisionAddress);
        Assert.Equal(selected, graph.Parent);
        Assert.Null(graph.SourceGraph);
        Assert.All(repository.MetaBootstrapStore.ReadBatch(graph.OwnedBatch!.Value).Records,
            row => Assert.Equal(ObjectVersionKind.Base, row.Kind));
        AssertRing(resumed.State, 3);
        Assert.Equal(shared, repository.SharedSchemaRoot);
    }

    [Fact]
    public void HistoricalForkRestoreFailurePublishesNothingAndReleasesItsNameForRetry() {
        using (Repository seed = Repository.CreateMetaBootstrapNew(_path, ProbeModels(1, new()), schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared)) {
            using BranchCheckout main = seed.CreateBranch("main", new ProbeRoot { Value = 7 });
            main.CommitEvent(new ProbeRoot { Value = 8 });
        }
        Counts counts = new() { FailAllocate = true };
        using Repository repository = Repository.OpenMetaBootstrapExisting(_path, ProbeModels(1, counts), schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared);
        CheckpointAddress source = repository.GetHead("main");
        var branchRef = ResourcesOf(repository).Versions.ReadRef(1).Unwrap();
        var sharedRef = ResourcesOf(repository).Versions.ReadRef(2).Unwrap();
        long frames = repository.MetaBootstrapStore.AppendedFrames;
        BranchCheckout? delivered = null;
        Assert.Throws<InvalidDataException>(() => delivered = repository.Fork("retry", source));
        Assert.Null(delivered);
        Assert.False(repository.IsFaulted);
        Assert.Equal(new[] { "main" }, repository.ListBranches());
        Assert.Equal(branchRef.Revision, ResourcesOf(repository).Versions.ReadRef(1).Unwrap().Revision);
        Assert.Equal(sharedRef.Revision, ResourcesOf(repository).Versions.ReadRef(2).Unwrap().Revision);
        Assert.Equal(frames, repository.MetaBootstrapStore.AppendedFrames);
        counts.FailAllocate = false;
        using BranchCheckout retry = repository.Fork("retry", source);
        Assert.Equal(7, Assert.IsType<ProbeRoot>(retry.State).Value); // Historical Event's nearest State, not its Event root 8.
        Assert.Equal(source, retry.Head);
        Assert.Equal(0, repository.MetaBootstrapStore.AppendedFrames);
        Assert.Equal(new[] { "main", "retry" }, repository.ListBranches());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RefOnlyForkPublicationFailurePreservesDataAndSharedRefAndHasExplicitRecovery(bool published) {
        FrameAddress source;
        RefRevision sharedRevision;
        byte[] sharedPayload;
        using (Repository seed = Repository.CreateMetaBootstrapNew(_path, Models(), schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared)) {
            using BranchCheckout main = seed.CreateBranch("main", Ring());
            source = main.Head.RevisionAddress;
        }
        Dictionary<string, byte[]> data = SnapshotFiles(Path.Combine(_path, "data"));
        using (Repository repository = Repository.OpenMetaBootstrapExisting(_path, Models(), schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared)) {
            using BranchCheckout main = repository.Checkout("main");
            var branches = ResourcesOf(repository).Versions.ReadRef(1).Unwrap();
            RefRevision branchRevision = branches.Revision;
            byte[] branchPayload = branches.Payload.ToArray();
            var shared = ResourcesOf(repository).Versions.ReadRef(2).Unwrap();
            sharedRevision = shared.Revision;
            sharedPayload = shared.Payload.ToArray();
            IOException injected = new("split ref-only fork window");
            repository.Checkpoint = point => {
                if (point == (published ? CommitCheckpoint.AfterPublication : CommitCheckpoint.BeforePublication)) { throw injected; }
            };
            BranchCheckout? delivered = null;
            GraphCommitException error = Assert.Throws<GraphCommitException>(() => delivered = repository.Fork("candidate", main.Head));
            Assert.Null(delivered);
            Assert.Null(error.CandidateRevisionAddress);
            Assert.Same(injected, error.InnerException);
            Assert.Equal(published ? GraphCommitOutcome.Published : GraphCommitOutcome.NotPublished, error.Outcome);
            Assert.Equal(published, repository.IsFaulted);
            repository.Checkpoint = null;
            if (published) {
                Assert.Throws<InvalidOperationException>(() => main.CommitState());
            } else {
                Assert.Equal(new[] { "main" }, repository.ListBranches());
                var unchanged = ResourcesOf(repository).Versions.ReadRef(1).Unwrap();
                Assert.Equal(branchRevision, unchanged.Revision);
                Assert.Equal(branchPayload, unchanged.Payload.ToArray());
                using BranchCheckout retry = repository.Fork("candidate", main.Head);
                AssertRing(retry.State, 0);
                Assert.Equal(0, repository.MetaBootstrapStore.AppendedFrames);
            }
        }
        AssertFilesEqual(data, SnapshotFiles(Path.Combine(_path, "data")));
        using Repository reopened = Repository.OpenMetaBootstrapReadOnlyExisting(_path, Models());
        Assert.Equal(source, reopened.GetHead("candidate").RevisionAddress);
        var selected = ResourcesOf(reopened).Versions.ReadRef(2).Unwrap();
        Assert.Equal(sharedRevision, selected.Revision);
        Assert.Equal(sharedPayload, selected.Payload.ToArray());
        AssertRing(reopened.ReadState(reopened.GetHead("candidate")), 0);
    }

    [Fact]
    public void ReadCheckpointRejectsAnAllocatorSharingOneMutableInstanceAcrossBothGraphsBeforeHydration() {
        using (Repository seed = Repository.CreateMetaBootstrapNew(_path, ProbeModels(1, new()), schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared)) {
            using BranchCheckout main = seed.CreateBranch("main", new ProbeRoot { Value = 7 });
            main.CommitEvent(new ProbeRoot { Value = 8 });
        }
        Counts counts = new() { Singleton = new() { Value = 100 } };
        using Repository repository = Repository.OpenMetaBootstrapReadOnlyExisting(_path, ProbeModels(1, counts));
        Checkpoint? delivered = null;
        Assert.Throws<InvalidDataException>(() => delivered = repository.ReadCheckpoint(repository.GetHead("main")));
        Assert.Null(delivered);
        Assert.Equal(0, counts.Hydrates);
        Assert.Equal(100, counts.Singleton!.Value);
        Assert.False(repository.IsFaulted);
        counts.Singleton = null;
        EventCheckpoint positive = Assert.IsType<EventCheckpoint>(repository.ReadCheckpoint(repository.GetHead("main")));
        Assert.Equal(8, Assert.IsType<ProbeRoot>(positive.Event).Value);
        Assert.Equal(7, Assert.IsType<ProbeRoot>(positive.PreviousState).Value);
        Assert.NotSame(positive.Event, positive.PreviousState);
        Assert.Equal(2, counts.Hydrates);
    }

    [Fact]
    public void HistoryRefsRejectWrongOwnersOccupancyAndReadonlyWritesAndIteratorsCheckLifetime() {
        CheckpointAddress old;
        using (Repository seed = Repository.CreateMetaBootstrapNew(_path, Models(), schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared)) {
            using BranchCheckout main = seed.CreateBranch("main", Ring());
            old = main.CommitEvent(Ring(1));
            seed.CreateTag("event", old);
        }
        using (Repository repository = Repository.OpenMetaBootstrapExisting(_path, Models(), schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared)) {
            CheckpointAddress current = repository.ResolveTag("event");
            Assert.Throws<ArgumentException>(() => repository.CreateBranch("foreign", old));
            Assert.Throws<ArgumentException>(() => repository.Fork("foreign", old));
            Assert.Throws<ArgumentException>(() => repository.CreateTag("foreign", old));
            Assert.Throws<ArgumentException>(() => repository.ReadCheckpoint(old));
            Assert.Throws<ArgumentException>(() => repository.EnumerateEvents(current, afterExclusive: old).ToArray());
            using BranchCheckout active = repository.Checkout("main");
            Assert.Throws<InvalidOperationException>(() => repository.MoveBranch("main", current, current));
            Assert.Throws<InvalidOperationException>(() => repository.Fork("main", current));
            Assert.Throws<InvalidOperationException>(() => repository.CreateTag("event", current));
            active.Dispose();
            Assert.Throws<InvalidOperationException>(() => repository.MoveBranch("main", repository.GetPreviousState(current)!, current));
            Assert.False(repository.IsFaulted);
            repository.CreateBranch("positive", current);
            Assert.Equal(current, repository.GetHead("positive"));
        }
        Dictionary<string, byte[]> files = SnapshotFiles(_path);
        using (Repository reader = Repository.OpenMetaBootstrapReadOnlyExisting(_path, Models())) {
            CheckpointAddress current = reader.ResolveTag("event");
            Assert.Throws<InvalidOperationException>(() => reader.CreateBranch("blocked", current));
            Assert.Throws<InvalidOperationException>(() => reader.Fork("blocked", current));
            Assert.Throws<InvalidOperationException>(() => reader.MoveBranch("main", current, current));
            Assert.Throws<InvalidOperationException>(() => reader.CreateTag("blocked", current));
            Assert.IsType<EventCheckpoint>(reader.ReadCheckpoint(current));
            using IEnumerator<CheckpointAddress> events = reader.EnumerateEvents(current).GetEnumerator();
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
                bool extra = context.TryGetSchema(Extra.Type, Extra.Version, out _);
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
