using static Atelia.DurableGraph.Persistence.Tests.DB096TestAccess;
using System.Buffers;
using Atelia.Binary;

using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using Atelia.HeadStore;
using Frames = Atelia.FrameStore.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

// Shared Catalog evolution preserves exact historical views and independent State baselines.
public sealed class SharedSchemaRepositoryTests : IDisposable {
    private const string Prefix = "dg-shared-schema-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
    private static readonly ReadAmplificationBaseBudgetParameters NoRebase = new(1_000_000, 1);
    private static readonly DurableSchema BranchExtra = new("Shared.Schema.BranchExtra", 1, new DurableFieldInfo(1, TypeTag.Int64));
    private static readonly DurableSchema Unreachable = new("Shared.Schema.Unreachable", 1, new DurableFieldInfo(1, TypeTag.Int32));
    private static readonly DurableSchema AddedByEvent = new("Shared.Schema.EventOnly", 1, new DurableFieldInfo(1, TypeTag.Int16));

    [Fact]
    public void Shared_baseline_fork_cold_continues_with_no_additional_catalog_write() {
        FrameAddress initial;
        RefImage shared;
        ObjectId[] identities;
        using (Repository repository = CreateShared()) {
            using WorkingCopy main = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", Ring(), parameters: NoRebase).Id);
            // Replace the original graph to make identity retention distinguishable from fresh allocation.
            main.CommitState(Ring(), parameters: NoRebase);
            identities = main.Baseline!.Objects.Keys.ToArray();
            Assert.All(identities, id => Assert.True(id.Value > 3));
            repository.ForkBranch(main.BaseCommitId, "dev");
            using WorkingCopy branch = repository.OpenWorkingCopy(BranchNamed(repository, "dev"));
            Assert.Equal(identities, branch.Baseline!.Objects.Keys.ToArray());
            initial = Describe(repository, repository.CommitBranches([
                branch.CreateStateCommitRequest(branch.State!, parameters: NoRebase)
            ], [BranchExtra])[branch.BranchId].Id).Address;
            shared = ReadHead(repository, 2);
            Assert.Equal(BranchExtra, Catalog(repository, initial).Schemas[new(BranchExtra.Type, 1)]);
            AssertRing(branch.State, 7);
        }
        using Repository reopened = Repository.OpenExisting(_path, Models());
        using WorkingCopy cold = reopened.OpenWorkingCopy(BranchNamed(reopened, "dev"));
        DB087Widget root = AssertRing(cold.State, 7);
        Assert.Equal(identities, cold.Baseline!.Objects.Keys.ToArray());

        AssertSameRef(shared, ReadHead(reopened, 2));
        root.Numbers[7] = 301;
        CommitId next = cold.CommitState(parameters: NoRebase).Id;
        StoredGraphRevision continued = DB096TestAccess.Snapshot(reopened, next);
        Assert.Equal(initial, continued.SourceGraph);
        Assert.Contains(LocalRecords(reopened.GraphStore, continued), row => row.Kind == ObjectVersionKind.Delta);
        AssertRing(reopened.LoadState(next), 301);
        AssertSameRef(shared, ReadHead(reopened, 2));
    }


    [Fact]
    public void Shared_Event_accepts_meta_but_cold_execution_and_State_identity_stay_on_the_saved_State() {
        FrameAddress stateAddress, stateSchema, eventAddress, eventSchema;
        RefImage sharedRef;
        using (Repository repository = CreateShared()) {
            using WorkingCopy main = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", Ring(), parameters: NoRebase).Id);
            repository.ForkBranch(main.BaseCommitId, "dev");
            using WorkingCopy dev = repository.OpenWorkingCopy(BranchNamed(repository, "dev"));
            stateAddress = Describe(repository, dev.BaseCommitId).Address;
            stateSchema = Catalog(repository, stateAddress).Snapshot!.Address;
            sharedRef = ReadHead(repository, 2);
            IDurableObject oldState = dev.State!;
            ObjectId? oldId = dev.StateId;
            NormalizedGraphState baseline = dev.Baseline!;
            var storage = baseline.Objects.Select(pair => (pair.Key, pair.Value.Storage)).ToArray();
            CommitId eventCommit = repository.CommitBranches([
                dev.CreateEventCommitRequest(Ring(100), parameters: NoRebase)
            ], [AddedByEvent])[dev.BranchId].Id;
            eventAddress = Describe(repository, eventCommit).Address;
            StoredGraphRevision graph = DB096TestAccess.Snapshot(repository, eventCommit);
            eventSchema = graph.SchemaRoot!.Value;
            Assert.NotEqual(stateSchema, eventSchema);
            Assert.Equal(stateSchema, Catalog(repository, eventAddress).Snapshot!.SourceGraph);
            Assert.Equal(AddedByEvent, dev.AcceptedCatalog!.Schemas[new(AddedByEvent.Type, 1)]);
            Assert.Equal(eventSchema, dev.AcceptedCatalog.Snapshot!.Address);
            Assert.Equal(stateAddress, Describe(repository, Previous(repository, eventCommit)!.Value).Address);
            Assert.Equal(stateAddress, graph.SourceGraph);
            ObjectVersionBatch batch = repository.GraphStore.ReadBatch(graph.OwnedBatch!.Value);
            Assert.Equal(stateAddress, batch.SourceGraph);
            Assert.Equal(eventSchema, batch.SchemaRoot);
            Assert.Same(oldState, dev.State);
            Assert.Equal(oldId, dev.StateId);
            Assert.Equal(stateAddress, dev.StateRevisionAddress);
            Assert.Same(baseline, dev.Baseline);
            Assert.Equal(storage, dev.Baseline!.Objects.Select(pair => (pair.Key, pair.Value.Storage)).ToArray());
            Assert.NotEqual(sharedRef.Revision, ReadHead(repository, 2).Revision);
            sharedRef = ReadHead(repository, 2);
        }
        using Repository cold = Repository.OpenExisting(_path, Models());
        using WorkingCopy checkout = cold.OpenWorkingCopy(BranchNamed(cold, "dev"));
        DB087Widget state = AssertRing(checkout.State, 7);
        Assert.Equal(eventAddress, Describe(cold, checkout.BaseCommitId).Address);
        Assert.Equal(stateAddress, checkout.StateRevisionAddress);
        Assert.Equal(eventSchema, checkout.AcceptedCatalog!.Snapshot!.Address);
        Assert.Equal(stateSchema, checkout.BindingEnvironment!.SelectedSchemaView.Snapshot!.Address);
        AssertRing(cold.LoadEvent(checkout.BaseCommitId), 7, value: 100);
        state.Numbers[7] = 302;
        CommitId next = checkout.CommitState(parameters: NoRebase).Id;
        StoredGraphRevision graphNext = DB096TestAccess.Snapshot(cold, next);
        Assert.Equal(eventAddress, Describe(cold, Previous(cold, next)!.Value).Address);
        Assert.Equal(stateAddress, graphNext.SourceGraph);
        Assert.Equal(eventSchema, graphNext.SchemaRoot);
        AssertRing(cold.LoadState(next), 302);
        AssertSameRef(sharedRef, ReadHead(cold, 2));
    }

    [Fact]
    public void Discarded_prepared_declarations_do_not_leak_into_a_later_commit() {
        using Repository repository = CreateShared();
        using WorkingCopy main = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", Ring(), parameters: NoRebase).Id);
        repository.ForkBranch(main.BaseCommitId, "dev");
        using WorkingCopy dev = repository.OpenWorkingCopy(BranchNamed(repository, "dev"));
        PublicationImage before = Snapshot(repository);
        NormalizedGraphState baseline = dev.Baseline!;
        CommitId oldHead = dev.BaseCommitId;
        FrameAddress oldSchema = dev.AcceptedCatalog!.Snapshot!.Address;
        DurableSchema wrong = new("Shared.Schema.Corrected", 1, new DurableFieldInfo(1, TypeTag.Byte));
        DurableSchema corrected = new("Shared.Schema.Corrected", 1, new DurableFieldInfo(1, TypeTag.Int64));
        AssertRing(dev.State, 7).Numbers[7] = 303;
        using (PreparedBranchSave discarded = dev.SaveSession.Prepare(dev.State!, parameters: NoRebase, declarations: [wrong])) {
            Assert.NotNull(discarded.Catalog.Write);
            Assert.Equal(wrong, discarded.Catalog.View.Schemas[new(wrong.Type, 1)]);
        }
        AssertUnchanged(repository, before);
        Assert.Same(baseline, dev.Baseline);
        Assert.Equal(oldHead, dev.BaseCommitId);
        Assert.Equal(oldSchema, dev.AcceptedCatalog.Snapshot!.Address);
        Assert.False(dev.AcceptedCatalog.Schemas.ContainsKey(new(corrected.Type, 1)));
        CommitId undeclared = dev.CommitState(parameters: NoRebase).Id;
        Assert.False(Catalog(repository, Describe(repository, undeclared).Address).Schemas.ContainsKey(new(wrong.Type, 1)));
        Assert.Equal(oldSchema, dev.AcceptedCatalog!.Snapshot!.Address);
        AssertRing(repository.LoadState(undeclared), 303);
        CommitId saved = repository.CommitBranches([
            dev.CreateStateCommitRequest(dev.State!, parameters: NoRebase)
        ], [corrected])[dev.BranchId].Id;
        Assert.Equal(corrected, Catalog(repository, Describe(repository, saved).Address).Schemas[new(corrected.Type, 1)]);
        Assert.Equal(corrected, dev.AcceptedCatalog!.Schemas[new(corrected.Type, 1)]);
        AssertRing(repository.LoadState(saved), 303);
        Assert.NotNull(repository.SharedSchemaRoot);
        Assert.NotEqual(before.Shared.Revision, ReadHead(repository, 2).Revision);
    }

    [Theory]
    [InlineData("state-meta")]
    [InlineData("business")]
    public void Shared_raw_preparation_cannot_substitute_a_real_but_wrong_meta_or_business_source(string wrongSource) {
        using Repository repository = CreateShared();
        using WorkingCopy main = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", Ring(), parameters: NoRebase).Id);
        repository.ForkBranch(main.BaseCommitId, "dev");
        using WorkingCopy dev = repository.OpenWorkingCopy(BranchNamed(repository, "dev"));
        repository.CommitBranches([dev.CreateStateCommitRequest(dev.State!, parameters: NoRebase)], [BranchExtra]);
        FrameAddress state = dev.StateRevisionAddress!.Value;
        Assert.NotEqual(main.StateRevisionAddress, dev.StateRevisionAddress);
        FrameAddress stateSchema = Catalog(repository, state).Snapshot!.Address;
        repository.CommitBranches([dev.CreateEventCommitRequest(Ring(100), parameters: NoRebase)], [AddedByEvent]);
        FrameAddress latestCatalog = dev.AcceptedCatalog!.Snapshot!.Address;
        Assert.NotEqual(stateSchema, latestCatalog);
        using RepositoryStorageFacade facade = new(ResourcesOf(repository));
        FrameAddress catalogSource = wrongSource switch {
            "state-meta" => stateSchema,
            _ => latestCatalog,
        };
        SchemaCatalogWorkspace catalog = new(facade.EnsureEditingAllowed, SchemaCatalogView.Load(facade.Store, catalogSource));
        catalog.Set(new DurableSchema("Shared.Schema.RawCandidate", 1, new DurableFieldInfo(1, TypeTag.Byte)));
        PreparedGraphWrite write = catalog.Prepare().Write!;
        FrameAddress businessSource = wrongSource == "business" ? main.StateRevisionAddress!.Value : state;
        PreparedGraphWrite business = new(businessSource, dev.StateId!.Value, []);
        PublicationImage before = Snapshot(repository);
        Assert.Throws<InvalidDataException>(() => facade.PrepareFrom(dev.BranchId.Value, dev.BaseCommitId.Value, null, StoredGraphKind.State, default, () => (business, write)));
        AssertUnchanged(repository, before);
        Assert.False(facade.HasPending);
        Assert.Equal(latestCatalog, dev.AcceptedCatalog.Snapshot!.Address);
    }

    [Fact]
    public void Shared_checkout_rejects_unregistered_occupied_and_readonly_before_output() {
        using (Repository repository = CreateShared()) {
            using WorkingCopy main = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", Ring(), parameters: NoRebase).Id);
            repository.ForkBranch(main.BaseCommitId, "dev");
            using WorkingCopy dev = repository.OpenWorkingCopy(BranchNamed(repository, "dev"));
            PublicationImage before = Snapshot(repository);
            Assert.Throws<ArgumentException>(() => repository.OpenWorkingCopy(default));
            BranchId managementHead = new(ReadHead(repository, 1).Revision.HeadId);
            Assert.Throws<KeyNotFoundException>(() => repository.OpenWorkingCopy(managementHead));
            Assert.Throws<InvalidOperationException>(() => repository.OpenWorkingCopy(BranchNamed(repository, "dev")));
            AssertUnchanged(repository, before);
        }
        using Repository readOnly = Repository.OpenReadOnlyExisting(_path, Models());
        PublicationImage readOnlyBefore = Snapshot(readOnly);
        Assert.Throws<InvalidOperationException>(() => readOnly.OpenWorkingCopy(BranchNamed(readOnly, "dev")));
        AssertUnchanged(readOnly, readOnlyBefore);
    }


    [Fact]
    public void Event_only_cold_checkout_uses_the_latest_catalog_and_its_first_State_has_no_content_source() {
        FrameAddress firstEvent, oldPin, latestShared, secondEvent, eventPin;
        RefImage shared;
        using (Repository repository = CreateShared()) {
            using WorkingCopy events = repository.OpenWorkingCopy(repository.CreateRootBranchFromEvent("events", Ring(100), parameters: NoRebase).Id);
            firstEvent = Describe(repository, events.BaseCommitId).Address;
            oldPin = Catalog(repository, firstEvent).Snapshot!.Address;
            Assert.Null(events.State);
            using WorkingCopy main = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", Ring(), parameters: NoRebase).Id);
            repository.CommitBranches([main.CreateStateCommitRequest(main.State!, parameters: NoRebase)], [Unreachable]);
            latestShared = repository.SharedSchemaRoot!.Value;
            shared = ReadHead(repository, 2);
            Assert.NotEqual(oldPin, latestShared);
        }
        using (Repository repository = Repository.OpenExisting(_path, Models())) {
            using WorkingCopy events = repository.OpenWorkingCopy(BranchNamed(repository, "events"));

            Assert.Equal(firstEvent, Describe(repository, events.BaseCommitId).Address);
            Assert.Null(events.State);
            Assert.Null(events.StateId);
            Assert.Null(events.StateRevisionAddress);
            Assert.Null(events.Baseline);
            Assert.Equal(latestShared, events.AcceptedCatalog!.Snapshot!.Address);
            Assert.Equal(latestShared, events.BindingEnvironment!.SelectedSchemaView.Snapshot!.Address);
            Assert.Same(events.Models, events.BindingEnvironment.Models);
            Assert.True(events.AcceptedCatalog.Schemas.ContainsKey(new(Unreachable.Type, 1)));
            CommitId secondEventCommit = repository.CommitBranches([
                events.CreateEventCommitRequest(Ring(200), parameters: NoRebase)
            ], [AddedByEvent])[events.BranchId].Id;
            secondEvent = Describe(repository, secondEventCommit).Address;
            StoredGraphRevision saved = DB096TestAccess.Snapshot(repository, secondEventCommit);
            eventPin = saved.SchemaRoot!.Value;
            Assert.NotEqual(oldPin, eventPin);
            Assert.Equal(firstEvent, Describe(repository, Previous(repository, secondEventCommit)!.Value).Address);
            Assert.Null(saved.SourceGraph);
            Assert.Equal(latestShared, events.AcceptedCatalog!.Snapshot!.SourceGraph);
            Assert.Equal(eventPin, events.AcceptedCatalog.Snapshot.Address);
            Assert.Null(repository.GraphStore.ReadBatch(saved.OwnedBatch!.Value).SourceGraph);
            Assert.Null(events.State);
            Assert.Null(events.Baseline);
            Assert.Null(events.StateRevisionAddress);
            Assert.NotEqual(shared.Revision, ReadHead(repository, 2).Revision);
            shared = ReadHead(repository, 2);
        }
        using Repository cold = Repository.OpenExisting(_path, Models());
        using WorkingCopy empty = cold.OpenWorkingCopy(BranchNamed(cold, "events"));
        Assert.Equal(secondEvent, Describe(cold, empty.BaseCommitId).Address);
        Assert.Null(empty.State);
        Assert.Null(empty.Baseline);
        Assert.Null(empty.StateRevisionAddress);
        Assert.Equal(eventPin, empty.AcceptedCatalog!.Snapshot!.Address);
        Assert.Equal(eventPin, empty.BindingEnvironment!.SelectedSchemaView.Snapshot!.Address);
        CommitId firstState = empty.CommitState(Ring(), parameters: NoRebase).Id;
        StoredGraphRevision graph = DB096TestAccess.Snapshot(cold, firstState);
        Assert.Equal(secondEvent, Describe(cold, Previous(cold, firstState)!.Value).Address);
        Assert.Null(graph.SourceGraph);
        Assert.Equal(eventPin, graph.SchemaRoot);
        IReadOnlyList<ObjectVersionRecord> records = LocalRecords(cold.GraphStore, graph);
        Assert.Equal(3, records.Count);
        Assert.All(records, row => Assert.Equal(ObjectVersionKind.Base, row.Kind));
        AssertRing(cold.LoadState(firstState), 7);
        Assert.Equal(eventPin, cold.SharedSchemaRoot);
        AssertSameRef(shared, ReadHead(cold, 2));
    }



    private static IReadOnlyList<ObjectVersionRecord> LocalRecords(GraphRecordStore store, StoredGraphRevision graph) =>
        graph.OwnedBatch is { } batch ? store.ReadBatch(batch).Records : [];

    private Repository CreateShared() => Repository.CreateNew(_path, Models());
    private static StateModelRegistry Models() {
        StateModelRegistry models = new();
        models.Register(global::Atelia.DurableGraph.Generated.Family_5030576964676574.Definition);
        return models;
    }
    private static DB087Widget Ring(int value = 1) {
        List<int> numbers = Enumerable.Range(0, 128).ToList();
        DB087Widget root = new() { Value = value, Numbers = numbers };
        root.Child = new() { Value = value + 1, Numbers = numbers, Child = root };
        return root;
    }
    private static DB087Widget AssertRing(IDurableObject? instance, int atSeven, int value = 1) {
        DB087Widget root = Assert.IsType<DB087Widget>(instance), child = Assert.IsType<DB087Widget>(root.Child);
        Assert.Equal(value, root.Value);
        Assert.Equal(value + 1, child.Value);
        Assert.Same(root, child.Child);
        Assert.Same(root.Numbers, child.Numbers);
        Assert.Equal(128, root.Numbers.Count);
        for (int i = 0; i < 128; i++) { Assert.Equal(i == 7 ? atSeven : i, root.Numbers[i]); }
        return root;
    }
    private static SchemaCatalogView Catalog(Repository repository, FrameAddress business) =>
        SchemaCatalogView.Load(repository.GraphStore, repository.GraphStore.ReadMetadata(business).SchemaRoot!.Value);
    private sealed record FrameImage(FrameAddress Address, uint Tag, int MetaLength, bool IsTombstone, byte[] Payload);
    private static FrameImage[] ReadFrames(Frames data) {
        List<FrameInfo> frames = [];
        data.Inventory(frames.Add).Unwrap();
        return frames.Select(info => {
            using FrameRead read = data.ReadFrame(info.Address).Unwrap();
            return new FrameImage(info.Address, read.Tag, read.TailMetaLength, read.IsTombstone, read.PayloadAndMeta.ToArray());
        }).ToArray();
    }
    private static void AssertFramesPreserved(Frames data, IEnumerable<FrameImage> before) {
        FrameImage[] after = ReadFrames(data);
        foreach (FrameImage frame in before) {
            FrameImage retained = after.Single(item => item.Address == frame.Address);
            Assert.Equal(frame.Tag, retained.Tag);
            Assert.Equal(frame.MetaLength, retained.MetaLength);
            Assert.Equal(frame.IsTombstone, retained.IsTombstone);
            Assert.Equal(frame.Payload, retained.Payload);
        }
    }
    private sealed record RefImage(HeadEntryId Revision, byte[] Payload);
    private sealed record PublicationImage(RefImage Branches, RefImage Shared, IReadOnlyDictionary<BranchId, CommitId> Business, long Bytes, long Appended);
    private static RefImage ReadHead(Repository repository, uint id) {
        HeadEntry read = ResourcesOf(repository).Heads.ReadHead(id).Unwrap();
        return new(read.Id, read.Payload.ToArray());
    }
    private static PublicationImage Snapshot(Repository repository) => new(ReadHead(repository, 1), ReadHead(repository, 2),
        repository.ListBranches().ToDictionary(branch => branch.Id, branch => repository.ReadCurrentCommit(branch.Id).Commit.Id),
        DataBytes(ResourcesOf(repository).Data), repository.GraphStore.AppendedFrames);
    private static void AssertSameRef(RefImage before, RefImage after) {
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.Payload, after.Payload);
    }
    private static void AssertUnchanged(Repository repository, PublicationImage before) {
        Assert.Equal(before.Business.Count, repository.ListBranches().Length);
        foreach (var branch in before.Business) { Assert.Equal(branch.Value, repository.ReadCurrentCommit(branch.Key).Commit.Id); }
        AssertSameRef(before.Branches, ReadHead(repository, 1));
        AssertSameRef(before.Shared, ReadHead(repository, 2));
        Assert.Equal(before.Bytes, DataBytes(ResourcesOf(repository).Data));
        Assert.Equal(before.Appended, repository.GraphStore.AppendedFrames);
        Assert.False(ResourcesOf(repository).HasPendingSave);
        Assert.False(repository.IsFaulted);
    }

    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}
