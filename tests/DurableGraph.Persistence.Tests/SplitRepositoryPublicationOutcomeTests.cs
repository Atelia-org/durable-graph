using static Atelia.DurableGraph.Persistence.Tests.DB096TestAccess;

using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;
using Atelia.HeadStore;
using DataStore = Atelia.FrameStore.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

// Returned exceptions from the actual typed Repository, separate from the
// process-Kill witnesses and the opaque lower P0 publication suite.
public sealed class SplitRepositoryPublicationOutcomeTests : IDisposable {
    private const string Prefix = "dg-split-publication-outcome-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
    private static readonly DurableSchema Extra = new("SplitPublicationExtra", 1, new DurableFieldInfo(1, TypeTag.Int64));

    [Theory]
    [InlineData("BeforeDataConfirm", 0, RepositoryPublicationOutcome.NotPublished, false)]
    [InlineData("AfterAppend", 1, RepositoryPublicationOutcome.Unknown, false)]
    [InlineData("AfterAppend", 2, RepositoryPublicationOutcome.Unknown, true)]
    [InlineData("AfterConfirmed", 0, RepositoryPublicationOutcome.Published, true)]
    public void ActualTwoRefPublicationReturnsOutcomeAndFaultsWithoutDeliveringOrInstalling(
        string stage, int failOnAppend, RepositoryPublicationOutcome outcome, bool visible) =>
        AssertFaultedMutation(stage, failOnAppend, outcome, visible);

    [Fact]
    public void ConfirmedBeforeInstallInterruptionPublishesBothRootsButKeepsTheUndeliveredCheckoutBaseline() =>
        AssertFaultedMutation("BeforeInstall", 0, RepositoryPublicationOutcome.Published, visible: true);

    [Fact]
    public void BeforeOutputNotPublishedKeepsTheOwnerHealthyAndRequiresAnExplicitRetry() {
        SeedResult seed = Seed();
        using (Repository repository = Repository.OpenStorageExisting(_path, Models())) {
            using WorkingCopy main = repository.OpenWorkingCopy(BranchNamed(repository, "main"));
            Assert.NotNull(repository.PreparedStateEntry);
            RepositoryResources resources = ResourcesOf(repository);
            GraphRecordStore store = repository.GraphStore;
            NormalizedGraphState baseline = main.Baseline!;
            CommitId oldHead = main.BaseCommitId;
            SchemaCatalogWorkspace catalogOwner = resources.CatalogForPreparation;
            SchemaCatalogView accepted = catalogOwner.Accepted!;
            HeadEntry oldBranches = resources.Heads.ReadHead(1).Unwrap();
            HeadEntry oldShared = resources.Heads.ReadHead(2).Unwrap();
            FrameImage[] oldFrames = ReadFrames(resources.Data);
            long appended = store.AppendedFrames;
            DB087Widget root = AssertRing(main.State, 7);

            root.Numbers[7] = 201;
            IOException failure = new("Before any output");
            int attempts = 0;
            repository.SaveStageObserver = point => {
                if (point == RepositorySaveStage.BeforeAppend) { attempts++; throw failure; }
            };
            CommitId? delivered = null;
            RepositoryPublicationException error = Assert.Throws<RepositoryPublicationException>(() => delivered = repository.CommitBranches([main.CreateStateCommitRequest()], [Extra])[main.BranchId].Id);
            Assert.Equal(RepositoryPublicationOutcome.NotPublished, error.Outcome);
            Assert.Same(failure, error.InnerException);
            Assert.Null(error.CandidateRevisionAddress);
            Assert.Null(delivered);
            Assert.Equal(1, attempts);
            Assert.False(repository.IsFaulted);
            Assert.Same(baseline, main.Baseline);
            Assert.Equal(oldHead, main.BaseCommitId);
            Assert.Same(accepted, catalogOwner.Accepted);

            Assert.Equal(7, baseline.Objects[seed.ListId].Current.GetListState<int>().Elements[7]);
            Assert.Equal(appended, store.AppendedFrames);
            Assert.Equal(oldFrames.Length, ReadFrames(resources.Data).Length);
            AssertFramesPreserved(resources.Data, oldFrames);
            AssertSameRef(oldBranches, resources.Heads.ReadHead(1).Unwrap());
            AssertSameRef(oldShared, resources.Heads.ReadHead(2).Unwrap());
            Assert.Equal(seed.State, Describe(repository, repository.ReadCurrentCommit(BranchNamed(repository, "main")).Commit.Id).Address);
            Assert.Equal(seed.Schema, repository.SharedSchemaRoot);
            Assert.NotNull(repository.PreparedStateEntry);
            Assert.True(store.ReadCacheStatistics.ResidentChargeBytes > 0);

            // No automatic retry occurred. Removing the injection is an explicit
            // second save of the preserved application mutation and manual intent.
            repository.SaveStageObserver = null;
            CommitId saved = repository.CommitBranches([main.CreateStateCommitRequest()], [Extra])[main.BranchId].Id;
            StoredGraphRevision graph = Snapshot(repository, saved);
            Assert.Equal(seed.State, graph.SourceGraph);
            Assert.NotEqual(seed.Schema, graph.SchemaRoot);
            Assert.Equal(repository.SharedSchemaRoot, graph.SchemaRoot);

            AssertRing(repository.LoadState(saved), 201);
        }
        VerifySelectedAndContinue(seed, visible: true, candidate: null, capturedFrames: []);
    }

    private void AssertFaultedMutation(string stage, int failOnAppend, RepositoryPublicationOutcome outcome, bool visible) {
        SeedResult seed = Seed();
        FrameAddress candidate = default;
        FrameImage[] captured = [];
        using (Repository repository = Repository.OpenStorageExisting(_path, Models())) {
            using WorkingCopy main = repository.OpenWorkingCopy(BranchNamed(repository, "main"));
            Assert.NotNull(repository.PreparedStateEntry); // Actual cold checkout populated the State slot.
            RepositoryResources resources = ResourcesOf(repository);
            GraphRecordStore store = repository.GraphStore; // Borrow before Repository becomes busy.
            store.ReadBusinessGraph(seed.State, null);
            Assert.True(store.ReadCacheStatistics.ResidentChargeBytes > 0);
            Assert.True(store.ReadCacheStatistics.EntryCount > 0);
            NormalizedGraphState baseline = main.Baseline!;
            CommitId oldHead = main.BaseCommitId;
            SchemaCatalogWorkspace catalogOwner = resources.CatalogForPreparation;
            SchemaCatalogView accepted = catalogOwner.Accepted!;
            HashSet<FrameAddress> oldAddresses = seed.Frames.Select(frame => frame.Address).ToHashSet();
            IOException failure = new("Actual split publication " + stage);
            int hookCalls = 0;
            repository.SaveStageObserver = point => {
                if (point == RepositorySaveStage.AfterBusinessAppend) {
                    // The actual DG append has returned. No FS reads take place
                    // inside a VS hook while a CU builder remains on its stack.
                    captured = ReadFrames(resources.Data).Where(frame => !oldAddresses.Contains(frame.Address)).ToArray();
                    candidate = captured.Single(frame => frame.Tag == GraphFrameCodec.GraphTag &&
                        store.ReadMetadata(frame.Address).Kind == StoredGraphKind.State).Address;
                }
                if (stage == "BeforeInstall" && point == RepositorySaveStage.BeforeInstall) { hookCalls++; throw failure; }
            };
            if (stage != "BeforeInstall") {
                InstallPublicationFault(resources.Heads, stage, () => {
                    if (stage != "AfterAppend" || ++hookCalls == failOnAppend) {
                        if (stage != "AfterAppend") { hookCalls++; }
                        throw failure;
                    }
                });
            }
            DB087Widget root = AssertRing(main.State, 7);

            root.Numbers[7] = 201;
            CommitId? delivered = null;
            RepositoryPublicationException error = Assert.Throws<RepositoryPublicationException>(() => delivered = repository.CommitBranches([main.CreateStateCommitRequest()], [Extra])[main.BranchId].Id);
            Assert.Equal(outcome, error.Outcome);
            Assert.Same(failure, error.InnerException);
            Assert.NotEqual(default, candidate);
            Assert.Equal(candidate, error.CandidateRevisionAddress);
            Assert.Null(delivered);
            Assert.Equal(stage == "AfterAppend" ? failOnAppend : 1, hookCalls);
            Assert.NotEmpty(captured);
            Assert.True(repository.IsFaulted); // NotPublished and health are independent.
            Assert.Equal(oldHead, main.BaseCommitId);
            Assert.Equal(seed.State, main.StateRevisionAddress);
            Assert.Same(baseline, main.Baseline);
            Assert.Equal(7, baseline.Objects[seed.ListId].Current.GetListState<int>().Elements[7]);
            Assert.Same(accepted, catalogOwner.Accepted);

            Assert.Null(repository.PreparedStateEntry);
            Assert.Equal(0, store.ReadCacheStatistics.EntryCount);
            Assert.Equal(0, store.ReadCacheStatistics.ResidentChargeBytes);
            long completedOutput = store.AppendedFrames;
            Assert.Throws<InvalidOperationException>(() => repository.ReadCurrentCommit(BranchNamed(repository, "main")).Commit.Id);
            Assert.Throws<InvalidOperationException>(() => main.CommitState());
            Assert.Throws<ObjectDisposedException>(() => store.ReadBusinessGraph(seed.State, null));
            Assert.Throws<ObjectDisposedException>(() => store.ReadDirectory(seed.Directory));
            Assert.Throws<ObjectDisposedException>(() => store.AppendBusiness(StoredGraphKind.State, seed.State, seed.Schema,
                new(seed.State, seed.RootId, [])));
            Assert.Equal(completedOutput, store.AppendedFrames); // Fault rejection neither retries nor writes.
        }
        VerifySelectedAndContinue(seed, visible, candidate, captured);
    }

    private void VerifySelectedAndContinue(SeedResult seed, bool visible, FrameAddress? candidate, FrameImage[] capturedFrames) {
        FrameAddress continued;
        using (Repository reopened = Repository.OpenStorageExisting(_path, Models())) {
            RepositoryResources resources = ResourcesOf(reopened);
            GraphRecordStore store = reopened.GraphStore;
            var branches = resources.ListBranches();
            IReadOnlyDictionary<string, FrameAddress> shared = ReadRoot(resources.Heads.ReadHead(2).Unwrap(), "P0S1"u8);
            CommitId selectedId = reopened.ReadCurrentCommit(BranchNamed(reopened, "main")).Commit.Id;
            FrameAddress selectedState = Describe(reopened, selectedId).Address, selectedSchema = shared["schema"];
            Assert.Single(branches);
            Assert.Single(shared);
            Assert.Equal(selectedState, Describe(reopened, reopened.ReadCurrentCommit(BranchNamed(reopened, "main")).Commit.Id).Address);
            Assert.Equal(selectedSchema, reopened.SharedSchemaRoot);
            if (visible) {
                if (candidate is { } actualCandidate) { Assert.Equal(actualCandidate, selectedState); }
                Assert.NotEqual(seed.State, selectedState);
                Assert.NotEqual(seed.Schema, selectedSchema);
            } else {
                Assert.Equal(seed.State, selectedState);
                Assert.Equal(seed.Schema, selectedSchema);
            }
            StoredGraphRevision selected = Snapshot(reopened, selectedId);
            Assert.Equal(selectedSchema, selected.SchemaRoot);
            SchemaCatalogView original = SchemaCatalogView.Load(store, seed.Schema);
            SchemaCatalogView catalog = SchemaCatalogView.Load(store, selectedSchema);
            Assert.False(original.Schemas.ContainsKey(new(Extra.Type, Extra.Version)));
            Assert.Equal(visible, catalog.Schemas.ContainsKey(new(Extra.Type, Extra.Version)));
            if (visible) { Assert.Equal(Extra, catalog.Schemas[new(Extra.Type, Extra.Version)]); }
            DurableSchema widget = original.Schemas[new(TypeExpr.Named("P0Widget"), 1)];
            Assert.Equal(original.SchemaNodeId(widget), catalog.SchemaNodeId(widget));
            AssertFramesPreserved(resources.Data, seed.Frames);
            AssertFramesPreserved(resources.Data, capturedFrames); // Complete candidates remain even when unselected.
            if (candidate is { } completedCandidate) {
                StoredGraphRevision completed = store.ReadBusinessGraph(completedCandidate, seed.State);
                Assert.Equal(seed.State, completed.SourceGraph);
                Assert.NotEqual(seed.Schema, completed.SchemaRoot);
                StoredGraphRevision meta = store.ReadMetaGraph(completed.SchemaRoot!.Value);
                Assert.Equal(StoredGraphKind.Meta, meta.Kind);
                Assert.Equal(seed.Schema, meta.SourceGraph);
                Assert.Equal(Extra, SchemaCatalogView.Load(store, meta.Address).Schemas[new(Extra.Type, Extra.Version)]);
                if (!visible) { Assert.NotEqual(selectedState, completedCandidate); }
            }
            AssertRing(reopened.LoadState(reopened.ResolveTag("initial")), 7);
            using WorkingCopy cold = reopened.OpenWorkingCopy(BranchNamed(reopened, "main"));
            DB087Widget root = AssertRing(cold.State, visible ? 201 : 7);
            Assert.Equal(seed.RootId, cold.StateId);
            Assert.Equal(selectedState, cold.StateRevisionAddress);
            Assert.Equal(seed.Storage.Keys.OrderBy(id => id.Value), cold.Baseline!.Objects.Keys.OrderBy(id => id.Value));
            foreach ((ObjectId id, ObjectStorageInfo oldStorage) in seed.Storage) {
                ObjectStorageInfo actual = cold.Baseline.Objects[id].Storage!.Value;
                Assert.Equal(actual.Head, selected.ObjectHeads[id.Value]);
                Assert.Equal(actual.ReconstructionPayloadBytes, store.ReadObjectChain(selected, id.Value).ReconstructionPayloadBytes);
                if (!visible || id != seed.ListId) { Assert.Equal(oldStorage, actual); }
            }
            ObjectStorageInfo prior = cold.Baseline.Objects[seed.ListId].Storage!.Value;
            root.Numbers[8] = 202;
            CommitId saved = cold.CommitState().Id;
            continued = Describe(reopened, saved).Address;
            StoredGraphRevision next = Snapshot(reopened, saved);
            Assert.Equal(selectedState, next.SourceGraph);
            Assert.Equal(selectedSchema, next.SchemaRoot);
            ObjectVersionBatch batch = store.ReadBatch(next.OwnedBatch!.Value);
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
            Assert.Equal(accepted.ReconstructionPayloadBytes, store.ReadObjectChain(next, seed.ListId.Value).ReconstructionPayloadBytes);
            AssertFramesPreserved(resources.Data, seed.Frames);
            AssertFramesPreserved(resources.Data, capturedFrames);
        }
        using Repository final = Repository.OpenStorageReadOnlyExisting(_path, Models());
        Assert.Equal(continued, Describe(final, final.ReadCurrentCommit(BranchNamed(final, "main")).Commit.Id).Address);
        AssertRing(final.LoadState(final.ReadCurrentCommit(BranchNamed(final, "main")).Commit.Id), visible ? 201 : 7, 202);
        AssertRing(final.LoadState(final.ResolveTag("initial")), 7);
        AssertFramesPreserved(ResourcesOf(final).Data, seed.Frames);
        AssertFramesPreserved(ResourcesOf(final).Data, capturedFrames);
    }

    private SeedResult Seed() {
        using Repository repository = Repository.CreateStorageNew(_path, Models());
        using WorkingCopy main = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", Ring()).Id);
        repository.CreateTag("initial", main.BaseCommitId);
        StoredGraphRevision graph = Snapshot(repository, main.BaseCommitId);
        Dictionary<ObjectId, ObjectStorageInfo> storage = main.Baseline!.Objects.ToDictionary(pair => pair.Key, pair => pair.Value.Storage!.Value);
        Assert.Equal(3, storage.Count);
        Assert.Equal(repository.SharedSchemaRoot, graph.SchemaRoot);
        ObjectId list = Assert.Single(main.Baseline.Objects, pair => pair.Value.Current.Kind == ObjectStateKind.List).Key;
        return new(graph.Address, graph.SchemaRoot!.Value, graph.DirectoryRoot, graph.RootId, list, storage, ReadFrames(ResourcesOf(repository).Data));
    }

    private static IReadOnlyDictionary<string, FrameAddress> ReadRoot(HeadEntry snapshot, ReadOnlySpan<byte> magic) {
        Assert.True(snapshot.Payload.StartsWith(magic));
        return NamedFrameMapCodec.Decode(snapshot.Payload[magic.Length..]).Unwrap();
    }
    private static void AssertSameRef(HeadEntry expected, HeadEntry actual) {
        Assert.Equal(expected.Id, actual.Id);
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
