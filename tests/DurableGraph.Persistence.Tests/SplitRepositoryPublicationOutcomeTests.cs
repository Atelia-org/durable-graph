using Atelia.DurableGraph.Persistence.MetaBootstrapP0;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;
using Atelia.VersionStore;
using DataStore = Atelia.FrameStore.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

// Returned exceptions from the actual typed Repository, separate from the
// process-Kill witnesses and the opaque lower P0 publication suite.
public sealed class SplitRepositoryPublicationOutcomeTests : IDisposable {
    private const string Prefix = "dg-split-publication-outcome-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
    private static readonly DurableSchema Extra = new("SplitPublicationExtra", 1, new DurableFieldInfo(1, TypeTag.Int64));

    [Theory]
    [InlineData("BeforeDataConfirm", 0, GraphCommitOutcome.NotPublished, false)]
    [InlineData("AfterAppend", 1, GraphCommitOutcome.Unknown, false)]
    [InlineData("AfterAppend", 2, GraphCommitOutcome.Unknown, true)]
    [InlineData("AfterConfirmed", 0, GraphCommitOutcome.Published, true)]
    public void ActualTwoRefPublicationReturnsOutcomeAndFaultsWithoutDeliveringOrInstalling(
        string stage, int failOnAppend, GraphCommitOutcome outcome, bool visible) =>
        AssertFaultedMutation(stage, failOnAppend, outcome, visible);

    [Fact]
    public void ConfirmedBeforeInstallInterruptionPublishesBothRootsButKeepsTheUndeliveredCheckoutBaseline() =>
        AssertFaultedMutation("BeforeInstall", 0, GraphCommitOutcome.Published, visible: true);

    [Fact]
    public void BeforeOutputNotPublishedKeepsTheOwnerHealthyAndRequiresAnExplicitRetry() {
        SeedResult seed = Seed();
        using (Repository repository = Repository.OpenMetaBootstrapExisting(_path, Models(), schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared)) {
            using BranchCheckout main = repository.Checkout("main");
            Assert.NotNull(repository.PreparedStateEntry);
            GraphResources resources = ResourcesOf(repository);
            P0GraphStore store = repository.MetaBootstrapStore;
            NormalizedRevision baseline = main.Baseline!;
            CheckpointAddress oldHead = main.Head;
            P0CatalogView accepted = main.Catalog.Accepted!;
            RefBytesSnapshot oldBranches = resources.Versions.ReadRef(1).Unwrap();
            RefBytesSnapshot oldShared = resources.Versions.ReadRef(2).Unwrap();
            FrameImage[] oldFrames = ReadFrames(resources.Data);
            long appended = store.AppendedFrames;
            DB087Widget root = AssertRing(main.State, 7);
            main.Catalog.Set(Extra);
            root.Numbers[7] = 201;
            IOException failure = new("Before any output");
            int attempts = 0;
            repository.MetaBootstrapCheckpoint = point => {
                if (point == P0SaveCheckpoint.BeforeAppend) { attempts++; throw failure; }
            };
            CheckpointAddress? delivered = null;
            GraphCommitException error = Assert.Throws<GraphCommitException>(() => delivered = main.CommitState());
            Assert.Equal(GraphCommitOutcome.NotPublished, error.Outcome);
            Assert.Same(failure, error.InnerException);
            Assert.Null(error.CandidateRevisionAddress);
            Assert.Null(delivered);
            Assert.Equal(1, attempts);
            Assert.False(repository.IsFaulted);
            Assert.Same(baseline, main.Baseline);
            Assert.Equal(oldHead, main.Head);
            Assert.Same(accepted, main.Catalog.Accepted);
            Assert.Equal(1, main.Catalog.DirtyCount);
            Assert.Equal(7, baseline.Objects[seed.ListId].Current.GetListState<int>().Elements[7]);
            Assert.Equal(appended, store.AppendedFrames);
            Assert.Equal(oldFrames.Length, ReadFrames(resources.Data).Length);
            AssertFramesPreserved(resources.Data, oldFrames);
            AssertSameRef(oldBranches, resources.Versions.ReadRef(1).Unwrap());
            AssertSameRef(oldShared, resources.Versions.ReadRef(2).Unwrap());
            Assert.Equal(seed.State, repository.GetHead("main").RevisionAddress);
            Assert.Equal(seed.Schema, repository.SharedSchemaRoot);
            Assert.NotNull(repository.PreparedStateEntry);
            Assert.True(store.ReadCacheStatistics.ResidentChargeBytes > 0);

            // No automatic retry occurred. Removing the injection is an explicit
            // second save of the preserved application mutation and manual intent.
            repository.MetaBootstrapCheckpoint = null;
            CheckpointAddress saved = main.CommitState();
            P0GraphSnapshot graph = store.ReadGraph(saved.RevisionAddress);
            Assert.Equal(seed.State, graph.SourceGraph);
            Assert.NotEqual(seed.Schema, graph.SchemaRoot);
            Assert.Equal(repository.SharedSchemaRoot, graph.SchemaRoot);
            Assert.Equal(0, main.Catalog.DirtyCount);
            AssertRing(repository.ReadState(saved), 201);
        }
        VerifySelectedAndContinue(seed, visible: true, candidate: null, capturedFrames: []);
    }

    private void AssertFaultedMutation(string stage, int failOnAppend, GraphCommitOutcome outcome, bool visible) {
        SeedResult seed = Seed();
        FrameAddress candidate = default;
        FrameImage[] captured = [];
        using (Repository repository = Repository.OpenMetaBootstrapExisting(_path, Models(), schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared)) {
            using BranchCheckout main = repository.Checkout("main");
            Assert.NotNull(repository.PreparedStateEntry); // Actual cold checkout populated the State slot.
            GraphResources resources = ResourcesOf(repository);
            P0GraphStore store = repository.MetaBootstrapStore; // Borrow before Repository becomes busy.
            store.ReadGraph(seed.State);
            Assert.True(store.ReadCacheStatistics.ResidentChargeBytes > 0);
            Assert.True(store.ReadCacheStatistics.EntryCount > 0);
            NormalizedRevision baseline = main.Baseline!;
            CheckpointAddress oldHead = main.Head;
            P0CatalogView accepted = main.Catalog.Accepted!;
            HashSet<FrameAddress> oldAddresses = seed.Frames.Select(frame => frame.Address).ToHashSet();
            IOException failure = new("Actual split publication " + stage);
            int hookCalls = 0;
            repository.MetaBootstrapCheckpoint = point => {
                if (point == P0SaveCheckpoint.AfterBusinessAppend) {
                    // The actual DG append has returned. No FS reads take place
                    // inside a VS hook while a CU builder remains on its stack.
                    captured = ReadFrames(resources.Data).Where(frame => !oldAddresses.Contains(frame.Address)).ToArray();
                    candidate = captured.Single(frame => frame.Tag == P0GraphWire.GraphTag &&
                        store.ReadMetadata(frame.Address).Kind == P0GraphKind.State).Address;
                }
                if (stage == "BeforeInstall" && point == P0SaveCheckpoint.BeforeInstall) { hookCalls++; throw failure; }
            };
            if (stage != "BeforeInstall") {
                InstallPublicationFault(resources.Versions, stage, () => {
                    if (stage != "AfterAppend" || ++hookCalls == failOnAppend) {
                        if (stage != "AfterAppend") { hookCalls++; }
                        throw failure;
                    }
                });
            }
            DB087Widget root = AssertRing(main.State, 7);
            main.Catalog.Set(Extra); // Ensures the save updates meta and the business ref together.
            root.Numbers[7] = 201;
            CheckpointAddress? delivered = null;
            GraphCommitException error = Assert.Throws<GraphCommitException>(() => delivered = main.CommitState());
            Assert.Equal(outcome, error.Outcome);
            Assert.Same(failure, error.InnerException);
            Assert.NotEqual(default, candidate);
            Assert.Equal(candidate, error.CandidateRevisionAddress);
            Assert.Null(delivered);
            Assert.Equal(stage == "AfterAppend" ? failOnAppend : 1, hookCalls);
            Assert.NotEmpty(captured);
            Assert.True(repository.IsFaulted); // NotPublished and health are independent.
            Assert.Equal(oldHead, main.Head);
            Assert.Equal(seed.State, main.StateRevisionAddress);
            Assert.Same(baseline, main.Baseline);
            Assert.Equal(7, baseline.Objects[seed.ListId].Current.GetListState<int>().Elements[7]);
            Assert.Same(accepted, main.Catalog.Accepted);
            Assert.Equal(1, main.Catalog.DirtyCount);
            Assert.Null(repository.PreparedStateEntry);
            Assert.Equal(0, store.ReadCacheStatistics.EntryCount);
            Assert.Equal(0, store.ReadCacheStatistics.ResidentChargeBytes);
            long completedOutput = store.AppendedFrames;
            Assert.Throws<InvalidOperationException>(() => repository.GetHead("main"));
            Assert.Throws<InvalidOperationException>(() => main.CommitState());
            Assert.Throws<ObjectDisposedException>(() => store.ReadGraph(seed.State));
            Assert.Throws<ObjectDisposedException>(() => store.ReadDirectory(seed.Directory));
            Assert.Throws<ObjectDisposedException>(() => store.Append(P0GraphKind.State, seed.State, seed.Schema,
                new(seed.State, seed.RootId, [])));
            Assert.Equal(completedOutput, store.AppendedFrames); // Fault rejection neither retries nor writes.
        }
        VerifySelectedAndContinue(seed, visible, candidate, captured);
    }

    private void VerifySelectedAndContinue(SeedResult seed, bool visible, FrameAddress? candidate, FrameImage[] capturedFrames) {
        FrameAddress continued;
        using (Repository reopened = Repository.OpenMetaBootstrapExisting(_path, Models(), schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared)) {
            GraphResources resources = ResourcesOf(reopened);
            P0GraphStore store = reopened.MetaBootstrapStore;
            IReadOnlyDictionary<string, FrameAddress> branches = ReadRoot(resources.Versions.ReadRef(1).Unwrap(), "P0R1"u8);
            IReadOnlyDictionary<string, FrameAddress> shared = ReadRoot(resources.Versions.ReadRef(2).Unwrap(), "P0S1"u8);
            FrameAddress selectedState = branches["branch/main"], selectedSchema = shared["schema"];
            Assert.Single(branches);
            Assert.Single(shared);
            Assert.Equal(selectedState, reopened.GetHead("main").RevisionAddress);
            Assert.Equal(selectedSchema, reopened.SharedSchemaRoot);
            if (visible) {
                if (candidate is { } actualCandidate) { Assert.Equal(actualCandidate, selectedState); }
                Assert.NotEqual(seed.State, selectedState);
                Assert.NotEqual(seed.Schema, selectedSchema);
            } else {
                Assert.Equal(seed.State, selectedState);
                Assert.Equal(seed.Schema, selectedSchema);
            }
            P0GraphSnapshot selected = store.ReadGraph(selectedState);
            Assert.Equal(selectedSchema, selected.SchemaRoot);
            P0CatalogView original = P0CatalogView.Load(store, seed.Schema);
            P0CatalogView catalog = P0CatalogView.Load(store, selectedSchema);
            Assert.False(original.Schemas.ContainsKey(new(Extra.Type, Extra.Version)));
            Assert.Equal(visible, catalog.Schemas.ContainsKey(new(Extra.Type, Extra.Version)));
            if (visible) { Assert.Equal(Extra, catalog.Schemas[new(Extra.Type, Extra.Version)]); }
            DurableSchema widget = original.Schemas[new(TypeExpr.Named("P0Widget"), 1)];
            Assert.Equal(original.SchemaNodeId(widget), catalog.SchemaNodeId(widget));
            AssertFramesPreserved(resources.Data, seed.Frames);
            AssertFramesPreserved(resources.Data, capturedFrames); // Complete candidates remain even when unselected.
            if (candidate is { } completedCandidate) {
                P0GraphSnapshot completed = store.ReadGraph(completedCandidate);
                Assert.Equal(seed.State, completed.SourceGraph);
                Assert.NotEqual(seed.Schema, completed.SchemaRoot);
                P0GraphSnapshot meta = store.ReadGraph(completed.SchemaRoot!.Value);
                Assert.Equal(P0GraphKind.Meta, meta.Kind);
                Assert.Equal(seed.Schema, meta.SourceGraph);
                Assert.Equal(Extra, P0CatalogView.Load(store, meta.Address).Schemas[new(Extra.Type, Extra.Version)]);
                if (!visible) { Assert.NotEqual(selectedState, completedCandidate); }
            }
            AssertRing(reopened.ReadState(reopened.ResolveTag("initial")), 7);
            using BranchCheckout cold = reopened.Checkout("main");
            DB087Widget root = AssertRing(cold.State, visible ? 201 : 7);
            Assert.Equal(seed.RootId, cold.StateId);
            Assert.Equal(selectedState, cold.StateRevisionAddress);
            Assert.Equal(seed.Storage.Keys.OrderBy(id => id.Value), cold.Baseline!.Objects.Keys.OrderBy(id => id.Value));
            foreach ((ObjectId id, ObjectStorageInfo oldStorage) in seed.Storage) {
                ObjectStorageInfo actual = cold.Baseline.Objects[id].Storage!.Value;
                Assert.Equal(actual.Head, selected.Objects[id.Value]);
                Assert.Equal(actual.ReconstructionPayloadBytes, store.ReadObjectChain(selectedState, id.Value).ReconstructionPayloadBytes);
                if (!visible || id != seed.ListId) { Assert.Equal(oldStorage, actual); }
            }
            ObjectStorageInfo prior = cold.Baseline.Objects[seed.ListId].Storage!.Value;
            root.Numbers[8] = 202;
            CheckpointAddress saved = cold.CommitState();
            continued = saved.RevisionAddress;
            P0GraphSnapshot next = store.ReadGraph(continued);
            Assert.Equal(selectedState, next.SourceGraph);
            Assert.Equal(selectedSchema, next.SchemaRoot);
            P0Batch batch = store.ReadBatch(next.OwnedBatch!.Value);
            Assert.Equal(selectedState, batch.SourceGraph);
            Assert.Equal(selectedSchema, batch.SchemaRoot);
            ObjectVersionRecord delta = Assert.Single(batch.Records);
            Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
            Assert.Equal(seed.ListId.Value, delta.ObjectId);
            Assert.Equal(prior.Head, delta.PriorAddress);
            ObjectStorageInfo accepted = cold.Baseline!.Objects[seed.ListId].Storage!.Value;
            Assert.Equal(next.OwnedBatch.Value, accepted.Head);
            Assert.NotEqual(continued, accepted.Head);
            Assert.Equal(prior.ReconstructionPayloadBytes + delta.EncodedPayloadBytes!.Value, accepted.ReconstructionPayloadBytes);
            Assert.Equal(accepted.ReconstructionPayloadBytes, store.ReadObjectChain(continued, seed.ListId.Value).ReconstructionPayloadBytes);
            AssertFramesPreserved(resources.Data, seed.Frames);
            AssertFramesPreserved(resources.Data, capturedFrames);
        }
        using Repository final = Repository.OpenMetaBootstrapReadOnlyExisting(_path, Models());
        Assert.Equal(continued, final.GetHead("main").RevisionAddress);
        AssertRing(final.ReadState(final.GetHead("main")), visible ? 201 : 7, 202);
        AssertRing(final.ReadState(final.ResolveTag("initial")), 7);
        AssertFramesPreserved(ResourcesOf(final).Data, seed.Frames);
        AssertFramesPreserved(ResourcesOf(final).Data, capturedFrames);
    }

    private SeedResult Seed() {
        using Repository repository = Repository.CreateMetaBootstrapNew(_path, Models(), schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared);
        using BranchCheckout main = repository.CreateBranch("main", Ring());
        repository.CreateTag("initial", main.Head);
        P0GraphSnapshot graph = repository.MetaBootstrapStore.ReadGraph(main.Head.RevisionAddress);
        Dictionary<ObjectId, ObjectStorageInfo> storage = main.Baseline!.Objects.ToDictionary(pair => pair.Key, pair => pair.Value.Storage!.Value);
        Assert.Equal(3, storage.Count);
        Assert.Equal(repository.SharedSchemaRoot, graph.SchemaRoot);
        ObjectId list = Assert.Single(main.Baseline.Objects, pair => pair.Value.Current.Kind == ObjectStateKind.List).Key;
        return new(graph.Address, graph.SchemaRoot!.Value, graph.DirectoryRoot, graph.RootId, list, storage, ReadFrames(ResourcesOf(repository).Data));
    }

    private static IReadOnlyDictionary<string, FrameAddress> ReadRoot(RefBytesSnapshot snapshot, ReadOnlySpan<byte> magic) {
        Assert.True(snapshot.Payload.StartsWith(magic));
        return RootMapCodec.Decode(snapshot.Payload[magic.Length..]).Unwrap();
    }
    private static void AssertSameRef(RefBytesSnapshot expected, RefBytesSnapshot actual) {
        Assert.Equal(expected.Revision, actual.Revision);
        Assert.Equal(expected.Payload.ToArray(), actual.Payload.ToArray());
    }
    private static FrameImage[] ReadFrames(DataStore data) {
        List<FrameInfo> frames = [];
        data.Inventory(frames.Add).Unwrap();
        return frames.Select(frame => {
            using FrameRead read = data.ReadFrame(frame.Address).Unwrap();
            return new FrameImage(frame.Address, read.Tag, read.TailMetaLength, read.IsTombstone, read.PayloadAndMeta.ToArray());
        }).ToArray();
    }
    private static void AssertFramesPreserved(DataStore data, IEnumerable<FrameImage> expected) {
        foreach (FrameImage image in expected) {
            using FrameRead actual = data.ReadFrame(image.Address).Unwrap();
            Assert.Equal(image.Tag, actual.Tag);
            Assert.Equal(image.TailMetaLength, actual.TailMetaLength);
            Assert.Equal(image.IsTombstone, actual.IsTombstone);
            Assert.Equal(image.PayloadAndMeta, actual.PayloadAndMeta.ToArray());
        }
    }
    private static StateModelRegistry Models() {
        StateModelRegistry models = new();
        models.Register(global::Atelia.DurableGraph.Generated.Family_5030576964676574.Definition);
        return models;
    }
    private static DB087Widget Ring() {
        List<int> numbers = Enumerable.Range(0, 128).ToList();
        DB087Widget root = new() { Value = 1, Numbers = numbers };
        root.Child = new() { Value = 2, Numbers = numbers, Child = root };
        return root;
    }
    private static DB087Widget AssertRing(IDurableObject? value, int atSeven, int atEight = 8) {
        DB087Widget root = Assert.IsType<DB087Widget>(value);
        DB087Widget child = Assert.IsType<DB087Widget>(root.Child);
        Assert.Equal(1, root.Value);
        Assert.Equal(2, child.Value);
        Assert.Same(root, child.Child);
        Assert.Same(root.Numbers, child.Numbers);
        Assert.Equal(128, root.Numbers.Count);
        for (int i = 0; i < 128; i++) { Assert.Equal(i == 7 ? atSeven : i == 8 ? atEight : i, root.Numbers[i]); }
        return root;
    }
    private sealed record FrameImage(FrameAddress Address, uint Tag, int TailMetaLength, bool IsTombstone, byte[] PayloadAndMeta);
    private sealed record SeedResult(FrameAddress State, FrameAddress Schema, FrameAddress Directory, ObjectId RootId,
        ObjectId ListId, Dictionary<ObjectId, ObjectStorageInfo> Storage, FrameImage[] Frames);
    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}
