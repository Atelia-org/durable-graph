using System.Buffers;
using Atelia.Binary;
using Atelia.DurableGraph.Persistence.MetaBootstrapP0;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using Atelia.VersionStore;
using Frames = Atelia.FrameStore.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

// Generated same-shape business graphs qualify the private save destination and
// failure boundaries. The Runtime suite separately qualifies generated independent A/B drafts.
public sealed class PrivateSchemaRepositoryTests : IDisposable {
    private const string Prefix = "dg-private-schema-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
    private static readonly ReadAmplificationBaseBudgetParameters NoRebase = new(1_000_000, 1);
    private static readonly DurableSchema SharedExtra = new("Private.Schema.Extra", 1, new DurableFieldInfo(1, TypeTag.Byte));
    private static readonly DurableSchema PrivateExtra = new("Private.Schema.PrivateExtra", 1, new DurableFieldInfo(1, TypeTag.Int64));
    private static readonly DurableSchema Unreachable = new("Private.Schema.Unreachable", 1, new DurableFieldInfo(1, TypeTag.Int32));
    private static readonly DurableSchema AddedByEvent = new("Private.Schema.EventOnly", 1, new DurableFieldInfo(1, TypeTag.Int16));

    [Fact]
    public void Private_baseline_fork_cold_continues_without_publishing_shared() {
        FrameAddress initial;
        RefImage shared;
        ObjectId[] identities;
        using (Repository repository = CreateShared()) {
            using BranchCheckout main = repository.CreateBranch("main", Ring(), NoRebase);
            // Replace the original graph to make identity retention distinguishable from fresh allocation.
            main.CommitState(Ring(), NoRebase);
            identities = main.Baseline!.Objects.Keys.ToArray();
            Assert.All(identities, id => Assert.True(id.Value > 3));
            repository.CreateBranch("dev", main.Head);
            using BranchCheckout branch = repository.CheckoutPrivate("dev");
            Assert.Equal(identities, branch.Baseline!.Objects.Keys.ToArray());
            branch.Catalog.Set(PrivateExtra);
            initial = branch.CommitState(NoRebase).RevisionAddress;
            shared = ReadRef(repository, 2);
            Assert.Equal(PrivateExtra, Catalog(repository, initial).Schemas[new(PrivateExtra.Type, 1)]);
            AssertRing(branch.State, 7);
        }
        using Repository reopened = Repository.OpenExisting(_path, Models());
        using BranchCheckout cold = reopened.CheckoutPrivate("dev");
        DB087Widget root = AssertRing(cold.State, 7);
        Assert.Equal(identities, cold.Baseline!.Objects.Keys.ToArray());
        Assert.Null(reopened.SchemaWriteMode);
        AssertSameRef(shared, ReadRef(reopened, 2));
        root.Numbers[7] = 301;
        CheckpointAddress next = cold.CommitState(NoRebase);
        P0GraphSnapshot continued = reopened.MetaBootstrapStore.ReadGraph(next.RevisionAddress);
        Assert.Equal(initial, continued.SourceGraph);
        Assert.Contains(LocalRecords(reopened.MetaBootstrapStore, continued.Address), row => row.Kind == ObjectVersionKind.Delta);
        AssertRing(reopened.ReadState(next), 301);
        AssertSameRef(shared, ReadRef(reopened, 2));
    }


    [Fact]
    public void Private_fork_retains_the_complete_baseline_view_without_consulting_latest_shared() {
        FrameAddress sharedRoot, privateRoot, sharedBusiness;
        RefImage sharedRef;
        using (Repository repository = CreateShared()) {
            using BranchCheckout main = repository.CreateBranch("main", Ring(), NoRebase);
            main.Catalog.Set(SharedExtra);
            main.Catalog.Set(Unreachable);
            sharedBusiness = main.CommitState(NoRebase).RevisionAddress;
            sharedRoot = repository.SharedSchemaRoot!.Value;
            sharedRef = ReadRef(repository, 2);
            P0CatalogView accepted = P0CatalogView.Load(repository.MetaBootstrapStore, sharedRoot);
            repository.CreateBranch("dev", main.Head);
            using BranchCheckout dev = repository.CheckoutPrivate("dev");
            Assert.Equal(sharedRoot, dev.Catalog.Accepted!.Snapshot!.Address);
            dev.Catalog.Set(PrivateExtra);
            privateRoot = dev.CommitState(NoRebase).RevisionAddress;
            P0CatalogView fork = Catalog(repository, privateRoot);
            Assert.NotEqual(sharedRoot, fork.Snapshot!.Address);
            Assert.Equal(sharedRoot, fork.Snapshot.SourceGraph);
            Assert.Equal(accepted.Schemas.Count + 1, fork.Schemas.Count);
            foreach ((SchemaKey key, DurableSchema schema) in accepted.Schemas) { Assert.Equal(schema, fork.Schemas[key]); }
            Assert.Equal(Unreachable, fork.Schemas[new(Unreachable.Type, 1)]);
            Assert.Equal(accepted.SchemaNodeId(SharedExtra), fork.SchemaNodeId(SharedExtra));
            AssertSameRef(sharedRef, ReadRef(repository, 2));
            Assert.Equal(sharedBusiness, repository.GetHead("main").RevisionAddress);
            Assert.Empty(LocalRecords(repository.MetaBootstrapStore, privateRoot));
        }
        using Repository cold = Repository.OpenReadOnlyExisting(_path, Models());
        Assert.Equal(sharedRoot, cold.SharedSchemaRoot);
        AssertSameRef(sharedRef, ReadRef(cold, 2));
        Assert.Equal(PrivateExtra, Catalog(cold, privateRoot).Schemas[new(PrivateExtra.Type, 1)]);
        Assert.Equal(Unreachable, Catalog(cold, privateRoot).Schemas[new(Unreachable.Type, 1)]);
        AssertRing(cold.ReadState(cold.GetHead("main")), 7);
        AssertRing(cold.ReadState(cold.GetHead("dev")), 7);
    }


    [Fact]
    public void Private_Event_accepts_meta_but_cold_execution_and_State_identity_stay_on_the_saved_State() {
        FrameAddress stateAddress, stateSchema, eventAddress, eventSchema;
        RefImage sharedRef;
        using (Repository repository = CreateShared()) {
            using BranchCheckout main = repository.CreateBranch("main", Ring(), NoRebase);
            repository.CreateBranch("dev", main.Head);
            using BranchCheckout dev = repository.CheckoutPrivate("dev");
            stateAddress = dev.Head.RevisionAddress;
            stateSchema = Catalog(repository, stateAddress).Snapshot!.Address;
            sharedRef = ReadRef(repository, 2);
            IDurableObject oldState = dev.State!;
            ObjectId? oldId = dev.StateId;
            NormalizedRevision baseline = dev.Baseline!;
            var storage = baseline.Objects.Select(pair => (pair.Key, pair.Value.Storage)).ToArray();
            dev.Catalog.Set(AddedByEvent);
            eventAddress = dev.CommitEvent(Ring(100), NoRebase).RevisionAddress;
            P0GraphSnapshot graph = repository.MetaBootstrapStore.ReadGraph(eventAddress);
            eventSchema = graph.SchemaRoot!.Value;
            Assert.NotEqual(stateSchema, eventSchema);
            Assert.Equal(stateSchema, Catalog(repository, eventAddress).Snapshot!.SourceGraph);
            Assert.Equal(AddedByEvent, dev.Catalog.Accepted!.Schemas[new(AddedByEvent.Type, 1)]);
            Assert.Equal(eventSchema, dev.Catalog.Accepted.Snapshot!.Address);
            Assert.Equal(stateAddress, graph.Parent);
            Assert.Equal(stateAddress, graph.SourceGraph);
            P0Batch batch = repository.MetaBootstrapStore.ReadBatch(graph.OwnedBatch!.Value);
            Assert.Equal(stateAddress, batch.SourceGraph);
            Assert.Equal(eventSchema, batch.SchemaRoot);
            Assert.Same(oldState, dev.State);
            Assert.Equal(oldId, dev.StateId);
            Assert.Equal(stateAddress, dev.StateRevisionAddress);
            Assert.Same(baseline, dev.Baseline);
            Assert.Equal(storage, dev.Baseline!.Objects.Select(pair => (pair.Key, pair.Value.Storage)).ToArray());
            AssertSameRef(sharedRef, ReadRef(repository, 2));
        }
        using Repository cold = Repository.OpenExisting(_path, Models());
        using BranchCheckout checkout = cold.CheckoutPrivate("dev");
        DB087Widget state = AssertRing(checkout.State, 7);
        Assert.Equal(eventAddress, checkout.Head.RevisionAddress);
        Assert.Equal(stateAddress, checkout.StateRevisionAddress);
        Assert.Equal(eventSchema, checkout.Catalog.Accepted!.Snapshot!.Address);
        Assert.Equal(stateSchema, checkout.BindingEnvironment!.SelectedSchemaView.Snapshot!.Address);
        AssertRing(cold.ReadEvent(checkout.Head), 7, value: 100);
        state.Numbers[7] = 302;
        CheckpointAddress next = checkout.CommitState(NoRebase);
        P0GraphSnapshot graphNext = cold.MetaBootstrapStore.ReadGraph(next.RevisionAddress);
        Assert.Equal(eventAddress, graphNext.Parent);
        Assert.Equal(stateAddress, graphNext.SourceGraph);
        Assert.Equal(eventSchema, graphNext.SchemaRoot);
        AssertRing(cold.ReadState(next), 302);
        AssertSameRef(sharedRef, ReadRef(cold, 2));
    }

    [Fact]
    public void Discarded_private_meta_can_be_corrected_without_appending_or_claiming_the_wrong_shape() {
        using Repository repository = CreateShared();
        using BranchCheckout main = repository.CreateBranch("main", Ring(), NoRebase);
        repository.CreateBranch("dev", main.Head);
        using BranchCheckout dev = repository.CheckoutPrivate("dev");
        PublicationImage before = Snapshot(repository);
        NormalizedRevision baseline = dev.Baseline!;
        CheckpointAddress oldHead = dev.Head;
        FrameAddress oldSchema = dev.Catalog.Accepted!.Snapshot!.Address;
        DurableSchema wrong = new("Private.Schema.Corrected", 1, new DurableFieldInfo(1, TypeTag.Byte));
        DurableSchema corrected = new("Private.Schema.Corrected", 1, new DurableFieldInfo(1, TypeTag.Int64));
        dev.Catalog.Set(wrong);
        AssertRing(dev.State, 7).Numbers[7] = 303;
        dev.PrepareAndDiscard(dev.State!, parameters: NoRebase);
        AssertUnchanged(repository, before);
        Assert.Same(baseline, dev.Baseline);
        Assert.Equal(oldHead, dev.Head);
        Assert.Equal(oldSchema, dev.Catalog.Accepted.Snapshot!.Address);
        Assert.False(dev.Catalog.Accepted.Schemas.ContainsKey(new(wrong.Type, 1)));
        Assert.True(dev.Catalog.DirtyCount > 0);
        dev.Catalog.Set(corrected);
        CheckpointAddress saved = dev.CommitState(NoRebase);
        Assert.Equal(corrected, Catalog(repository, saved.RevisionAddress).Schemas[new(corrected.Type, 1)]);
        Assert.Equal(0, dev.Catalog.DirtyCount);
        AssertRing(repository.ReadState(saved), 303);
        Assert.NotNull(repository.SharedSchemaRoot);
        AssertSameRef(before.Shared, ReadRef(repository, 2));
    }

    [Fact]
    public void Unchanged_private_save_reuses_meta_and_directory_and_never_rebases_to_latest_shared() {
        using Repository repository = CreateShared();
        using BranchCheckout main = repository.CreateBranch("main", Ring(), NoRebase);
        main.Catalog.Set(SharedExtra);
        main.CommitState(NoRebase);
        repository.CreateBranch("dev", main.Head);
        using BranchCheckout dev = repository.CheckoutPrivate("dev");
        dev.Catalog.Set(PrivateExtra);
        dev.CommitState(NoRebase);
        P0GraphSnapshot previous = repository.MetaBootstrapStore.ReadGraph(dev.Head.RevisionAddress);
        DurableSchema latest = new("Private.Schema.Later", 1, new DurableFieldInfo(1, TypeTag.Int32));
        main.Catalog.Set(latest);
        main.CommitState(NoRebase);
        RefImage sharedRef = ReadRef(repository, 2);
        Assert.NotEqual(previous.SchemaRoot, repository.SharedSchemaRoot);
        CheckpointAddress next = dev.CommitState(NoRebase);
        P0GraphSnapshot saved = repository.MetaBootstrapStore.ReadGraph(next.RevisionAddress);
        Assert.Equal(previous.SchemaRoot, saved.SchemaRoot);
        Assert.Equal(previous.DirectoryRoot, saved.DirectoryRoot);
        Assert.Equal(previous.Objects.ToArray(), saved.Objects.ToArray());
        Assert.Null(saved.OwnedBatch);
        Assert.Equal(previous.Address, saved.SourceGraph);
        Assert.False(dev.Catalog.Accepted!.Schemas.ContainsKey(new(latest.Type, 1)));
        Assert.Equal(PrivateExtra, dev.Catalog.Accepted.Schemas[new(PrivateExtra.Type, 1)]);
        AssertSameRef(sharedRef, ReadRef(repository, 2));
        DurableSchema privateLater = new(latest.Type, 1, new DurableFieldInfo(1, TypeTag.Byte));
        dev.Catalog.Set(privateLater); // Missing private declarations do not consult latest shared.
        next = dev.CommitState(NoRebase);
        Assert.Equal(privateLater, Catalog(repository, next.RevisionAddress).Schemas[new(latest.Type, 1)]);
        Assert.Equal(latest, P0CatalogView.Load(repository.MetaBootstrapStore, repository.SharedSchemaRoot!.Value).Schemas[new(latest.Type, 1)]);
        AssertSameRef(sharedRef, ReadRef(repository, 2));
    }

    [Fact]
    public void Shared_save_rejects_unreachable_private_declarations_before_capture_but_accepts_an_already_contained_view() {
        int captures = 0;
        using Repository repository = Repository.CreateNew(_path, CountingModels(() => captures++), schemaWriteMode: SchemaWriteMode.Shared);
        using BranchCheckout main = repository.CreateBranch("main", new CountingRoot { Value = 1 }, NoRebase);
        repository.CreateBranch("dev", main.Head);
        using (BranchCheckout dev = repository.CheckoutPrivate("dev")) {
            Assert.IsType<CountingRoot>(dev.State).Value = 2;
            dev.CommitState(NoRebase);
            dev.Catalog.Set(AddedByEvent); // No business field or reachable layout names this declaration.
            dev.CommitEvent(new CountingRoot { Value = 3 }, NoRebase);
        }
        using (BranchCheckout ordinary = repository.Checkout("dev")) {
            PublicationImage before = Snapshot(repository);
            NormalizedRevision baseline = ordinary.Baseline!;
            CheckpointAddress oldHead = ordinary.Head;
            captures = 0;
            Assert.Throws<InvalidOperationException>(() => ordinary.CommitState(NoRebase));
            Assert.Equal(0, captures);
            AssertUnchanged(repository, before);
            Assert.Same(baseline, ordinary.Baseline);
            Assert.Equal(oldHead, ordinary.Head);
        }
        using (BranchCheckout explicitlyPrivate = repository.CheckoutPrivate("dev")) {
            explicitlyPrivate.CommitState(NoRebase);
            Assert.Equal(AddedByEvent, explicitlyPrivate.Catalog.Accepted!.Schemas[new(AddedByEvent.Type, 1)]);
        }
        main.Catalog.Set(AddedByEvent);
        main.CommitState(NoRebase);
        RefImage shared = ReadRef(repository, 2);
        using BranchCheckout contained = repository.Checkout("dev");
        Assert.NotEqual(contained.Catalog.Accepted!.Snapshot!.Address,
            repository.MetaBootstrapStore.ReadMetadata(contained.Head.RevisionAddress).SchemaRoot);
        captures = 0;
        CheckpointAddress saved = contained.CommitState(NoRebase);
        Assert.Equal(1, captures);
        Assert.Equal(repository.SharedSchemaRoot, repository.MetaBootstrapStore.ReadMetadata(saved.RevisionAddress).SchemaRoot);
        AssertSameRef(shared, ReadRef(repository, 2));
        Assert.Equal(2, Assert.IsType<CountingRoot>(contained.State).Value);
    }

    [Theory]
    [InlineData((int)P0SaveCheckpoint.AfterMetaAppend, false)]
    [InlineData((int)P0SaveCheckpoint.BeforePublication, false)]
    [InlineData((int)P0SaveCheckpoint.AfterPublication, true)]
    public void Private_publication_failures_recover_from_ref1_and_never_advance_ref2(int stage, bool visible) {
        FrameAddress oldHead, mainHead, oldSchema;
        FrameImage[] complete;
        RefImage shared;
        GraphCommitException error;
        using (Repository repository = CreateShared()) {
            using BranchCheckout main = repository.CreateBranch("main", Ring(), NoRebase);
            repository.CreateBranch("dev", main.Head);
            using BranchCheckout dev = repository.CheckoutPrivate("dev");
            oldHead = dev.Head.RevisionAddress;
            oldSchema = dev.Catalog.Accepted!.Snapshot!.Address;
            mainHead = main.Head.RevisionAddress;
            shared = ReadRef(repository, 2);
            complete = ReadFrames(ResourcesOf(repository).Data);
            NormalizedRevision baseline = dev.Baseline!;
            dev.Catalog.Set(AddedByEvent);
            AssertRing(dev.State, 7).Numbers[7] = 304;
            IOException failure = new("Private publication interrupted.");
            P0GraphStore store = repository.MetaBootstrapStore;
            long before = store.AppendedFrames, outputAtFailure = before;
            repository.MetaBootstrapCheckpoint = checkpoint => {
                if (checkpoint == (P0SaveCheckpoint)stage) { outputAtFailure = store.AppendedFrames; throw failure; }
            };
            CheckpointAddress? delivered = null;
            error = Assert.Throws<GraphCommitException>(() => delivered = dev.CommitState(NoRebase));
            Assert.Equal(visible ? GraphCommitOutcome.Published : GraphCommitOutcome.NotPublished, error.Outcome);
            Assert.Same(failure, error.InnerException);
            Assert.True(outputAtFailure > before);
            Assert.True(repository.IsFaulted);
            Assert.Null(delivered);
            Assert.Equal(oldHead, dev.Head.RevisionAddress);
            Assert.Same(baseline, dev.Baseline);
            Assert.Throws<InvalidOperationException>(() => repository.GetHead("dev"));
        }
        using Repository cold = Repository.OpenExisting(_path, Models());
        using BranchCheckout recovered = cold.CheckoutPrivate("dev");
        AssertSameRef(shared, ReadRef(cold, 2));
        Assert.Equal(mainHead, cold.GetHead("main").RevisionAddress);
        Assert.Equal(visible ? error.CandidateRevisionAddress : oldHead, recovered.Head.RevisionAddress);
        AssertRing(recovered.State, visible ? 304 : 7);
        AssertFramesPreserved(ResourcesOf(cold).Data, complete);
        P0CatalogView accepted = recovered.Catalog.Accepted!;
        Assert.Equal(visible, accepted.Schemas.ContainsKey(new(AddedByEvent.Type, 1)));
        if (!visible) { Assert.Equal(oldSchema, accepted.Snapshot!.Address); }
        if (stage == (int)P0SaveCheckpoint.AfterMetaAppend) { Assert.Null(error.CandidateRevisionAddress); }
        else { Assert.NotNull(error.CandidateRevisionAddress); }
    }

    [Theory]
    [InlineData("state-meta")]
    [InlineData("shared-meta")]
    [InlineData("business")]
    public void Private_raw_preparation_cannot_substitute_a_real_but_wrong_meta_or_business_source(string wrongSource) {
        using Repository repository = CreateShared();
        using BranchCheckout main = repository.CreateBranch("main", Ring(), NoRebase);
        repository.CreateBranch("dev", main.Head);
        using BranchCheckout dev = repository.CheckoutPrivate("dev");
        dev.Catalog.Set(PrivateExtra);
        dev.CommitState(NoRebase);
        FrameAddress state = dev.StateRevisionAddress!.Value;
        Assert.NotEqual(main.StateRevisionAddress, dev.StateRevisionAddress);
        FrameAddress stateSchema = Catalog(repository, state).Snapshot!.Address;
        dev.Catalog.Set(AddedByEvent);
        dev.CommitEvent(Ring(100), NoRebase);
        FrameAddress latestPrivate = dev.Catalog.Accepted!.Snapshot!.Address;
        Assert.NotEqual(stateSchema, latestPrivate);
        using P0Repository facade = new(ResourcesOf(repository));
        FrameAddress catalogSource = wrongSource switch {
            "state-meta" => stateSchema,
            "shared-meta" => repository.SharedSchemaRoot!.Value,
            _ => latestPrivate,
        };
        P0CatalogWorkspace catalog = new(facade.EnsureEditingAllowed, P0CatalogView.Load(facade.Store, catalogSource));
        catalog.Set(new DurableSchema("Private.Schema.RawCandidate", 1, new DurableFieldInfo(1, TypeTag.Byte)));
        P0PreparedGraphWrite write = catalog.Prepare().Write!;
        FrameAddress businessSource = wrongSource == "business" ? main.StateRevisionAddress!.Value : state;
        P0PreparedGraphWrite business = new(businessSource, dev.StateId!.Value, []);
        PublicationImage before = Snapshot(repository);
        Assert.Throws<InvalidDataException>(() => facade.PreparePrivateFrom("dev", P0GraphKind.State, () => (business, write)));
        AssertUnchanged(repository, before);
        Assert.False(facade.HasPending);
        Assert.Equal(latestPrivate, dev.Catalog.Accepted.Snapshot!.Address);
    }

    [Fact]
    public void Private_checkout_rejects_missing_occupied_and_readonly_before_output() {
        using (Repository repository = CreateShared()) {
            using BranchCheckout main = repository.CreateBranch("main", Ring(), NoRebase);
            repository.CreateBranch("dev", main.Head);
            using BranchCheckout dev = repository.CheckoutPrivate("dev");
            PublicationImage before = Snapshot(repository);
            Assert.Throws<KeyNotFoundException>(() => repository.CheckoutPrivate("absent"));
            Assert.Throws<InvalidOperationException>(() => repository.CheckoutPrivate("dev"));
            AssertUnchanged(repository, before);
        }
        using Repository readOnly = Repository.OpenReadOnlyExisting(_path, Models());
        PublicationImage readOnlyBefore = Snapshot(readOnly);
        Assert.Throws<InvalidOperationException>(() => readOnly.CheckoutPrivate("dev"));
        AssertUnchanged(readOnly, readOnlyBefore);
    }


    [Fact]
    public void Event_only_private_cold_checkout_uses_the_head_pin_and_its_first_State_has_no_content_source() {
        FrameAddress firstEvent, oldPin, latestShared, privateEvent, privatePin;
        RefImage shared;
        using (Repository repository = CreateShared()) {
            using BranchCheckout events = repository.CreateBranchFromEvent("events", Ring(100), NoRebase);
            firstEvent = events.Head.RevisionAddress;
            oldPin = Catalog(repository, firstEvent).Snapshot!.Address;
            Assert.Null(events.State);
            using BranchCheckout main = repository.CreateBranch("main", Ring(), NoRebase);
            main.Catalog.Set(Unreachable);
            main.CommitState(NoRebase);
            latestShared = repository.SharedSchemaRoot!.Value;
            shared = ReadRef(repository, 2);
            Assert.NotEqual(oldPin, latestShared);
        }
        using (Repository repository = Repository.OpenExisting(_path, Models())) {
            using BranchCheckout events = repository.CheckoutPrivate("events");
            Assert.Null(repository.SchemaWriteMode);
            Assert.Equal(firstEvent, events.Head.RevisionAddress);
            Assert.Null(events.State);
            Assert.Null(events.StateId);
            Assert.Null(events.StateRevisionAddress);
            Assert.Null(events.Baseline);
            Assert.Equal(oldPin, events.Catalog.Accepted!.Snapshot!.Address);
            Assert.Equal(oldPin, events.BindingEnvironment!.SelectedSchemaView.Snapshot!.Address);
            Assert.Same(events.Models, events.BindingEnvironment.Models);
            Assert.False(events.Catalog.Accepted.Schemas.ContainsKey(new(Unreachable.Type, 1)));
            events.Catalog.Set(AddedByEvent);
            privateEvent = events.CommitEvent(Ring(200), NoRebase).RevisionAddress;
            P0GraphSnapshot saved = repository.MetaBootstrapStore.ReadGraph(privateEvent);
            privatePin = saved.SchemaRoot!.Value;
            Assert.NotEqual(oldPin, privatePin);
            Assert.Equal(firstEvent, saved.Parent);
            Assert.Null(saved.SourceGraph);
            Assert.Equal(oldPin, events.Catalog.Accepted!.Snapshot!.SourceGraph);
            Assert.Equal(privatePin, events.Catalog.Accepted.Snapshot.Address);
            Assert.Null(repository.MetaBootstrapStore.ReadBatch(saved.OwnedBatch!.Value).SourceGraph);
            Assert.Null(events.State);
            Assert.Null(events.Baseline);
            Assert.Null(events.StateRevisionAddress);
            AssertSameRef(shared, ReadRef(repository, 2));
        }
        using Repository cold = Repository.OpenExisting(_path, Models());
        using BranchCheckout empty = cold.CheckoutPrivate("events");
        Assert.Equal(privateEvent, empty.Head.RevisionAddress);
        Assert.Null(empty.State);
        Assert.Null(empty.Baseline);
        Assert.Null(empty.StateRevisionAddress);
        Assert.Equal(privatePin, empty.Catalog.Accepted!.Snapshot!.Address);
        Assert.Equal(privatePin, empty.BindingEnvironment!.SelectedSchemaView.Snapshot!.Address);
        CheckpointAddress firstState = empty.CommitState(Ring(), NoRebase);
        P0GraphSnapshot graph = cold.MetaBootstrapStore.ReadGraph(firstState.RevisionAddress);
        Assert.Equal(privateEvent, graph.Parent);
        Assert.Null(graph.SourceGraph);
        Assert.Equal(privatePin, graph.SchemaRoot);
        IReadOnlyList<ObjectVersionRecord> records = LocalRecords(cold.MetaBootstrapStore, graph.Address);
        Assert.Equal(3, records.Count);
        Assert.All(records, row => Assert.Equal(ObjectVersionKind.Base, row.Kind));
        AssertRing(cold.ReadState(firstState), 7);
        Assert.Equal(latestShared, cold.SharedSchemaRoot);
        AssertSameRef(shared, ReadRef(cold, 2));
    }



    private Repository CreateShared() => Repository.CreateNew(_path, Models(), schemaWriteMode: SchemaWriteMode.Shared);
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
    private static P0CatalogView Catalog(Repository repository, FrameAddress business) =>
        P0CatalogView.Load(repository.MetaBootstrapStore, repository.MetaBootstrapStore.ReadMetadata(business).SchemaRoot!.Value);
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
    private sealed record RefImage(RefRevision Revision, byte[] Payload);
    private sealed record PublicationImage(RefImage Branches, RefImage Shared, long Bytes, long Appended);
    private static RefImage ReadRef(Repository repository, uint id) {
        RefBytesSnapshot read = ResourcesOf(repository).Versions.ReadRef(id).Unwrap();
        return new(read.Revision, read.Payload.ToArray());
    }
    private static PublicationImage Snapshot(Repository repository) => new(ReadRef(repository, 1), ReadRef(repository, 2),
        DataBytes(ResourcesOf(repository).Data), repository.MetaBootstrapStore.AppendedFrames);
    private static void AssertSameRef(RefImage before, RefImage after) {
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.Payload, after.Payload);
    }
    private static void AssertUnchanged(Repository repository, PublicationImage before) {
        AssertSameRef(before.Branches, ReadRef(repository, 1));
        AssertSameRef(before.Shared, ReadRef(repository, 2));
        Assert.Equal(before.Bytes, DataBytes(ResourcesOf(repository).Data));
        Assert.Equal(before.Appended, repository.MetaBootstrapStore.AppendedFrames);
        Assert.False(ResourcesOf(repository).HasMetaBootstrapPending);
        Assert.False(repository.IsFaulted);
    }

    private sealed class CountingRoot : IDurableObject { internal int Value; }
    private static readonly DurableSchema CountingSchema = new("Private.Schema.Counting", 1, new DurableFieldInfo(1, TypeTag.Int32));
    private static StateModelRegistry CountingModels(Action capture) {
        static void Visit(in int value, IStateReferenceVisitor visitor) { }
        static PreparedBaseBody Base(in int value) {
            ArrayBufferWriter<byte> bytes = new();
            BareValueWriter writer = new(bytes);
            writer.WriteVarInt32(value);
            return new(bytes.WrittenSpan);
        }
        StateModelRegistry models = new();
        models.Register(new StateModelBinding<CountingRoot, int>(new(CountingSchema, Base,
            static (in int prior, in int next) => new(prior != next, Base(next).Body)),
            [new StateReaderBinding<int>(CountingSchema, static (ref BareValueReader reader) => reader.ReadVarInt32(),
                static (ref BareValueReader reader, in int prior) => reader.ReadVarInt32(), Visit)],
            static row => row.GetState<int>(), static () => new(),
            static (CountingRoot domain, in int value, ObjectReadTable objects) => domain.Value = value,
            (domain, context) => { capture(); return domain.Value; }, Visit));
        return models;
    }
    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}
