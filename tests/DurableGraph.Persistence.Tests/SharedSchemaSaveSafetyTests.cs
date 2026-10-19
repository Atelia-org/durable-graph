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

public sealed class SharedSchemaSaveSafetyTests : IDisposable {
    private const string Prefix = "dg-shared-save-safety-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
    private static readonly ReadAmplificationBaseBudgetParameters NoRebase = new(1_000_000, 1);
    private static readonly DurableSchema ExtraDeclaration = new("Shared.ExtraDeclaration", 1, new DurableFieldInfo(1, TypeTag.Int32));

    [Fact]
    public void Layout_only_catalog_growth_publishes_business_and_Schema_while_preserving_branch_directory() {
        using Repository repository = Repository.CreateNew(_path, Models());
        using WorkingCopy main = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", Ring(), parameters: NoRebase).Id);
        repository.ForkBranch(main.BaseCommitId, "dev");
        using WorkingCopy dev = repository.OpenWorkingCopy(BranchNamed(repository, "dev"));
        ObjectLayout dynamicList = ObjectLayout.ForList(new(DurableFieldInfo.Reference(1, TypeExpr.Named("P0Widget"))));
        CommitId previousCommit = dev.BaseCommitId;
        StoredGraphRevision previous = DB096TestAccess.Snapshot(repository, previousCommit);
        dev.Catalog.Set(dynamicList);
        SchemaCatalogView oldShared = SchemaCatalogView.Load(repository.GraphStore, repository.SharedSchemaRoot!.Value);
        Assert.Throws<InvalidDataException>(() => oldShared.DescriptorId(dynamicList));
        PublicationImage before = Snapshot(repository);
        StoredGraphRevision saved = DB096TestAccess.Snapshot(repository, dev.CommitState(parameters: NoRebase).Id);
        SchemaCatalogView merged = SchemaCatalogView.Load(repository.GraphStore, saved.SchemaRoot!.Value);
        Assert.Equal(oldShared.Schemas.Count, merged.Schemas.Count);
        Assert.Equal(oldShared.ObjectLayouts.Count() + 1, merged.ObjectLayouts.Count());
        Assert.Equal(dynamicList, merged.ResolveLayout(merged.DescriptorId(dynamicList)));
        AssertSameRef(before.Branches, ReadHead(repository, 1));
        Assert.NotEqual(previousCommit, dev.BaseCommitId);
        Assert.Equal(dev.BaseCommitId, repository.ReadCurrentCommit(dev.BranchId).Commit.Id);
        Assert.NotEqual(before.Shared.Revision, ReadHead(repository, 2).Revision);
        Assert.Equal(repository.SharedSchemaRoot, saved.SchemaRoot);
        Assert.Equal(previous.DirectoryRoot, saved.DirectoryRoot);
        Assert.Equal(previous.ObjectHeads.ToArray(), saved.ObjectHeads.ToArray());
        Assert.Null(saved.OwnedBatch);
    }

    [Fact]
    public void Shared_Event_accepts_meta_without_consuming_the_State_heads_H_or_pending_layout_rewrite() {
        FrameAddress source, sourcePin, eventAddress;
        ObjectStorageInfo original;
        using (Repository repository = Repository.CreateNew(_path, CounterModels(1))) {
            using WorkingCopy main = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", new Counter { Value = 7 }, parameters: NoRebase).Id);
            repository.ForkBranch(main.BaseCommitId, "dev");
            using WorkingCopy dev = repository.OpenWorkingCopy(BranchNamed(repository, "dev"));
            source = Describe(repository, dev.BaseCommitId).Address;
            sourcePin = dev.Catalog.Accepted!.Snapshot!.Address;
            original = dev.Baseline!.Objects[dev.StateId!.Value].Storage!.Value;
            Assert.NotNull(repository.SharedSchemaRoot);
        }
        using Repository cold = Repository.OpenExisting(_path, CounterModels(2));
        using WorkingCopy checkout = cold.OpenWorkingCopy(BranchNamed(cold, "dev"));
        NormalizedGraphState baseline = checkout.Baseline!;
        ObjectId id = checkout.StateId!.Value;
        Assert.True(baseline.Objects[id].RequiresRewrite);
        Assert.Equal(original, baseline.Objects[id].Storage);
        ModelExecutionEnvironment models = checkout.Models;
        DecodedRevision.SchemaViewBindingEnvironment environment = checkout.BindingEnvironment!;
        checkout.Catalog.Set(ExtraDeclaration);
        PublicationImage beforeDiscard = Snapshot(cold);
        checkout.PrepareAndDiscard(checkout.State!, parameters: NoRebase);
        AssertUnchanged(cold, beforeDiscard);
        Assert.Same(baseline, checkout.Baseline);
        Assert.True(checkout.Baseline!.Objects[id].RequiresRewrite);
        Assert.Equal(original, checkout.Baseline.Objects[id].Storage);
        Assert.Equal(source, checkout.StateRevisionAddress);
        Assert.True(checkout.Catalog.DirtyCount > 0);
        CommitId eventCommit = checkout.CommitEvent(new Counter { Value = 20 }, parameters: NoRebase).Id;
        eventAddress = Describe(cold, eventCommit).Address;
        StoredGraphRevision ev = DB096TestAccess.Snapshot(cold, eventCommit);
        Assert.Equal(source, Describe(cold, Previous(cold, eventCommit)!.Value).Address);
        Assert.Equal(source, ev.SourceGraph);
        Assert.Equal(cold.SharedSchemaRoot, ev.SchemaRoot);
        Assert.NotEqual(sourcePin, ev.SchemaRoot);
        Assert.Equal(ExtraDeclaration, SchemaCatalogView.Load(cold.GraphStore, ev.SchemaRoot!.Value).Schemas[new(ExtraDeclaration.Type, 1)]);
        Assert.Equal(0, checkout.Catalog.DirtyCount);
        Assert.Same(baseline, checkout.Baseline);
        Assert.True(checkout.Baseline!.Objects[id].RequiresRewrite);
        Assert.Equal(original, checkout.Baseline.Objects[id].Storage);
        Assert.Equal(source, checkout.StateRevisionAddress);
        Assert.Equal(id, checkout.StateId);
        Assert.Equal(7, Assert.IsType<Counter>(checkout.State).Value);
        Assert.Equal(20, Assert.IsType<Counter>(cold.LoadEvent(checkout.BaseCommitId)).Value);

        Assert.Same(models, checkout.Models);
        Assert.Same(environment, checkout.BindingEnvironment);
        CommitId stateCommit = checkout.CommitState(parameters: NoRebase).Id;
        FrameAddress state = Describe(cold, stateCommit).Address;
        StoredGraphRevision saved = DB096TestAccess.Snapshot(cold, stateCommit);
        Assert.Equal(eventAddress, Describe(cold, Previous(cold, stateCommit)!.Value).Address);
        Assert.Equal(source, saved.SourceGraph);
        ObjectVersionRecord rewritten = Assert.Single(LocalRecords(cold.GraphStore, saved));
        Assert.Equal(id.Value, rewritten.ObjectId);
        Assert.Equal(ObjectVersionKind.Base, rewritten.Kind);
        Assert.False(checkout.Baseline!.Objects[id].RequiresRewrite);
        Assert.NotEqual(original.Head, checkout.Baseline.Objects[id].Storage!.Value.Head);
        Assert.Equal(rewritten.EncodedPayloadBytes!.Value, checkout.Baseline.Objects[id].Storage!.Value.ReconstructionPayloadBytes);
    }

    [Fact]
    public void Discard_keeps_explicit_dirty_intent_and_a_later_conflict_is_corrected_before_shared_acceptance() {
        int captures = 0;
        using Repository repository = Repository.CreateNew(_path, CounterModels(1, () => captures++));
        using WorkingCopy main = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", new Counter { Value = 1 }, parameters: NoRebase).Id);
        repository.ForkBranch(main.BaseCommitId, "dev");
        using WorkingCopy dev = repository.OpenWorkingCopy(BranchNamed(repository, "dev"));
        Assert.IsType<Counter>(dev.State).Value = 2;
        dev.Catalog.Set(ExtraDeclaration);
        dev.CommitState(parameters: NoRebase);
        DurableSchema wrong = new("Promotion.Corrected", 1, new DurableFieldInfo(1, TypeTag.Byte));
        DurableSchema correct = new(wrong.Type, 1, new DurableFieldInfo(1, TypeTag.Int64));
        SchemaCatalogView accepted = dev.Catalog.Accepted!;
        NormalizedGraphState baseline = dev.Baseline!;
        ModelExecutionEnvironment models = dev.Models;
        PublicationImage before = Snapshot(repository);
        dev.Catalog.Set(wrong);
        dev.PrepareAndDiscard(dev.State!, parameters: NoRebase);
        AssertUnchanged(repository, before);
        Assert.Same(accepted, dev.Catalog.Accepted);
        Assert.Same(baseline, dev.Baseline);
        Assert.True(dev.Catalog.DirtyCount > 0);
        main.Catalog.Set(correct);
        main.CommitState(parameters: NoRebase);
        PublicationImage beforeConflict = Snapshot(repository);
        captures = 0;
        Assert.Throws<SchemaConflictException>(() => dev.CommitState(parameters: NoRebase).Id);
        Assert.Equal(0, captures);
        AssertUnchanged(repository, beforeConflict);
        Assert.Same(accepted, dev.Catalog.Accepted);
        Assert.Same(baseline, dev.Baseline);
        dev.Catalog.Set(correct);
        dev.CommitState(parameters: NoRebase);
        Assert.Equal(1, captures);
        SchemaCatalogView merged = SchemaCatalogView.Load(repository.GraphStore, repository.SharedSchemaRoot!.Value);
        Assert.Equal(ExtraDeclaration, merged.Schemas[new(ExtraDeclaration.Type, 1)]);
        Assert.Equal(correct, merged.Schemas[new(correct.Type, 1)]);
        Assert.Equal(0, dev.Catalog.DirtyCount);
        Assert.Same(models, dev.Models);
    }

    [Fact]
    public void Captured_same_key_conflict_does_not_accept_dirty_intent_or_append() {
        using Frames frames = Frames.Create(_path);
        DurableSchema registered = new("Shared.Captured", 1, new DurableFieldInfo(1, TypeTag.Int32));
        DurableSchema captured = new(registered.Type, 1, new DurableFieldInfo(1, TypeTag.Int64));
        DurableSchema dirty = new("Shared.Dirty", 1, new DurableFieldInfo(1, TypeTag.Byte));
        using GraphRecordStore store = new(frames);
        SchemaCatalogView accepted = AppendFreshCatalog(store, registered);
        SchemaCatalogWorkspace workspace = new(static () => { }, accepted);
        workspace.Set(dirty);
        long appended = store.AppendedFrames;
        SchemaConflictException error = Assert.Throws<SchemaConflictException>(() =>
            workspace.PrepareOwned([ObjectLayout.ForDurable(captured)]));
        Assert.Equal(registered, error.RegisteredSchema);
        Assert.Equal(captured, error.ConflictingSchema);
        Assert.Same(accepted, workspace.Accepted);
        Assert.Equal(1, workspace.DirtyCount);
        Assert.Equal(appended, store.AppendedFrames);
        PreparedSchemaCatalogWrite corrected = workspace.PrepareOwned([ObjectLayout.ForDurable(registered)]);
        Assert.Equal(dirty, corrected.View.Schemas[new(dirty.Type, 1)]);
        Assert.Same(accepted, workspace.Accepted);
        Assert.Equal(1, workspace.DirtyCount);
    }

    [Fact]
    public void Save_accepts_a_new_root_layout_first_encountered_by_business_Capture() {
        using Repository repository = Repository.CreateNew(_path, Models());
        using WorkingCopy main = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", Ring(), parameters: NoRebase).Id);
        repository.ForkBranch(main.BaseCommitId, "dev");
        using WorkingCopy dev = repository.OpenWorkingCopy(BranchNamed(repository, "dev"));
        DurableSchema envelope = dev.Models.ResolveCurrentModel(typeof(DB087EnvelopeRoot)).CurrentSchema;
        SchemaCatalogView before = SchemaCatalogView.Load(repository.GraphStore, repository.SharedSchemaRoot!.Value);
        Assert.False(before.Schemas.ContainsKey(new(envelope.Type, envelope.Version)));
        DB087Widget ring = AssertRing(dev.State, 7);
        DB087EnvelopeRoot root = new() { Link = ring, Numbers = ring.Numbers };

        CommitId saved = dev.CommitState(root, parameters: NoRebase).Id;
        SchemaCatalogView merged = SchemaCatalogView.Load(repository.GraphStore, repository.SharedSchemaRoot!.Value);
        Assert.Equal(envelope, merged.Schemas[new(envelope.Type, envelope.Version)]);
        Assert.Equal(before.Snapshot!.Address, merged.Snapshot!.SourceGraph);
        Assert.Same(root, dev.State);
        Assert.Equal(saved, dev.BaseCommitId);
        AssertEnvelope(repository.LoadState(saved), 7);
        Assert.Equal(0, dev.Catalog.DirtyCount);
    }

    [Fact]
    public void Late_shared_conflict_from_the_captured_new_root_keeps_existing_root_saveable() {
        using Repository repository = Repository.CreateNew(_path, Models());
        using WorkingCopy main = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", Ring(), parameters: NoRebase).Id);
        repository.ForkBranch(main.BaseCommitId, "dev");
        using WorkingCopy dev = repository.OpenWorkingCopy(BranchNamed(repository, "dev"));
        DurableSchema envelope = dev.Models.ResolveCurrentModel(typeof(DB087EnvelopeRoot)).CurrentSchema;
        DurableSchema wrong = new(envelope.Type, envelope.Version, new DurableFieldInfo(1, TypeTag.Int64));
        Assert.False(dev.Catalog.Accepted!.Schemas.ContainsKey(new(envelope.Type, envelope.Version)));
        main.Catalog.Set(wrong);
        main.CommitState(parameters: NoRebase);
        SchemaCatalogView accepted = dev.Catalog.Accepted!;
        NormalizedGraphState baseline = dev.Baseline!;
        PublicationImage before = Snapshot(repository);
        DB087Widget ring = AssertRing(dev.State, 7);
        DB087EnvelopeRoot root = new() { Link = ring, Numbers = ring.Numbers };
        CommitId? delivered = null;
        SchemaConflictException error = Assert.Throws<SchemaConflictException>(() => delivered = dev.CommitState(root, parameters: NoRebase).Id);
        Assert.Equal(wrong, error.RegisteredSchema);
        Assert.Equal(envelope, error.ConflictingSchema);
        Assert.Null(delivered);
        Assert.Equal(repository.SharedSchemaRoot, dev.Catalog.Accepted!.Snapshot!.Address);
        Assert.Same(baseline, dev.Baseline);
        AssertUnchanged(repository, before);

        CommitId saved = dev.CommitState(parameters: NoRebase).Id;
        Assert.Same(ring, dev.State);
        AssertRing(repository.LoadState(saved), 7);
        AssertSameRef(before.Shared, ReadHead(repository, 2));
    }

    [Fact]
    public void Save_Capture_keeps_public_and_resource_owner_reentry_guards_until_preparation_finishes() {
        Action? duringCapture = null;
        int captures = 0;
        using Repository repository = Repository.CreateNew(_path, CounterModels(1, () => { captures++; duringCapture?.Invoke(); }));
        using WorkingCopy checkout = repository.OpenWorkingCopy(repository.CreateRootBranchFromEvent("main", new Counter { Value = 1 }, parameters: NoRebase).Id);
        RepositoryResources resources = ResourcesOf(repository);
        using RepositoryStorageFacade facade = new(resources);
        bool nestedProducerCalled = false;
        captures = 0;
        duringCapture = () => {
            Assert.Throws<InvalidOperationException>(() => checkout.CommitEvent(new Counter { Value = 99 }, parameters: NoRebase).Id);
            Assert.Throws<InvalidOperationException>(() => checkout.Catalog.Set(ExtraDeclaration));
            Assert.Throws<InvalidOperationException>(() => repository.Dispose());
            Assert.Throws<InvalidOperationException>(() => resources.Dispose());
            Assert.Throws<InvalidOperationException>(() => facade.Dispose());
            Assert.Throws<InvalidOperationException>(() => facade.PrepareFrom(checkout.BranchId.Value, checkout.BaseCommitId.Value, null, StoredGraphKind.State, default,
                () => { nestedProducerCalled = true; throw new IOException("Nested producer must not be called."); }));
            Assert.False(nestedProducerCalled);
        };
        CommitId saved = checkout.CommitEvent(new Counter { Value = 2 }, parameters: NoRebase).Id;
        duringCapture = null;
        Assert.Equal(1, captures);
        Assert.Equal(saved, checkout.BaseCommitId);
        Assert.Null(checkout.State);
        Assert.Equal(2, Assert.IsType<Counter>(repository.LoadEvent(saved)).Value);
        Assert.False(resources.HasPendingSave);
        Assert.False(repository.IsFaulted);
    }

    [Theory]
    [InlineData("StoreDispose")]
    [InlineData("OwnerFault")]
    [InlineData("DataClose")]
    [InlineData("VersionsClose")]
    public void Event_only_save_rejects_stopped_Store_before_business_body_preparation_or_pending(string stop) {
        Action? duringCapture = null;
        int captures = 0, bodyPreparations = 0, afterPrepare = 0;
        using Repository repository = Repository.CreateNew(_path,
            CounterModels(1, () => { captures++; duringCapture?.Invoke(); }, () => bodyPreparations++));
        using WorkingCopy checkout = repository.OpenWorkingCopy(repository.CreateRootBranchFromEvent("main", new Counter { Value = 1 }, parameters: NoRebase).Id);
        RepositoryResources resources = ResourcesOf(repository);
        GraphRecordStore retained = repository.GraphStore;
        PublicationImage before = Snapshot(repository);
        SchemaCatalogView accepted = checkout.Catalog.Accepted!;
        Assert.Null(checkout.State);
        Assert.Null(checkout.Baseline);
        captures = bodyPreparations = 0;
        repository.OperationStageObserver = _ => afterPrepare++;
        duringCapture = () => {
            switch (stop) {
                case "StoreDispose": retained.Dispose(); break;
                case "OwnerFault": resources.MarkFaulted(); break;
                case "DataClose": resources.Data.Dispose(); break;
                case "VersionsClose": resources.Heads.Dispose(); break;
                default: throw new ArgumentOutOfRangeException(nameof(stop));
            }
        };
        CommitId? delivered = null;
        Assert.ThrowsAny<InvalidOperationException>(() => delivered = checkout.CommitEvent(new Counter { Value = 2 }, parameters: NoRebase).Id);
        Assert.Null(delivered);
        Assert.Equal(1, captures);
        Assert.Equal(0, bodyPreparations);
        Assert.Equal(0, afterPrepare);
        Assert.Same(accepted, checkout.Catalog.Accepted);
        Assert.Equal(before.Appended, retained.AppendedFrames);
        Assert.Null(checkout.State);
        if (stop == "StoreDispose") { Assert.False(resources.HasPendingSave); }
        checkout.Dispose();
        repository.Dispose();

        using Repository cold = Repository.OpenReadOnlyExisting(_path, CounterModels(1));
        AssertSameRef(before.Branches, ReadHead(cold, 1));
        AssertSameRef(before.Shared, ReadHead(cold, 2));
        Assert.Equal(before.Frames.Select(frame => frame.Address), ReadFrames(ResourcesOf(cold).Data).Select(frame => frame.Address));
        AssertFramesPreserved(ResourcesOf(cold).Data, before.Frames);
        Assert.Equal(1, Assert.IsType<Counter>(cold.LoadEvent(cold.ReadCurrentCommit(BranchNamed(cold, "main")).Commit.Id)).Value);
    }

    [Fact]
    public void Shared_raw_pending_requires_latest_shared_meta_source_and_the_exact_business_State_source() {
        using Repository repository = Repository.CreateNew(_path, Models());
        using WorkingCopy main = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", Ring(), parameters: NoRebase).Id);
        repository.ForkBranch(main.BaseCommitId, "dev");
        using WorkingCopy dev = repository.OpenWorkingCopy(BranchNamed(repository, "dev"));
        dev.Catalog.Set(ExtraDeclaration);
        dev.CommitState(parameters: NoRebase);
        FrameAddress headPin = dev.Catalog.Accepted!.Snapshot!.Address;
        main.Catalog.Set(new DurableSchema("Shared.Later", 1, new DurableFieldInfo(1, TypeTag.Byte)));
        main.CommitState(parameters: NoRebase);
        Assert.NotEqual(repository.SharedSchemaRoot, headPin);
        using RepositoryStorageFacade facade = new(ResourcesOf(repository));
        SchemaCatalogWorkspace catalog = new(facade.EnsureEditingAllowed, SchemaCatalogView.Load(facade.Store, headPin));
        catalog.Set(new DurableSchema("Promotion.RawExtra", 1, new DurableFieldInfo(1, TypeTag.Byte)));
        PreparedSchemaCatalogWrite staleCandidate = catalog.Prepare();
        PreparedGraphWrite validBusiness = new(dev.StateRevisionAddress, dev.StateId!.Value, []);
        PublicationImage before = Snapshot(repository);
        Assert.Throws<InvalidDataException>(() => facade.PrepareFrom(dev.BranchId.Value, dev.BaseCommitId.Value, null, StoredGraphKind.State, default, () => (validBusiness, staleCandidate.Write)));
        AssertUnchanged(repository, before);
        catalog.RebaseOwned(SchemaCatalogView.Load(facade.Store, facade.SharedSchemaRoot!.Value));
        PreparedSchemaCatalogWrite target = catalog.PrepareOwned([]);
        Assert.Equal(facade.SharedSchemaRoot, target.Write!.SourceGraph);
        Assert.NotEqual(main.StateRevisionAddress, dev.StateRevisionAddress);
        PreparedGraphWrite wrongBusiness = new(main.StateRevisionAddress, dev.StateId.Value, []);
        Assert.Throws<InvalidDataException>(() => facade.PrepareFrom(dev.BranchId.Value, dev.BaseCommitId.Value, null, StoredGraphKind.State, default, () => (wrongBusiness, target.Write)));
        AssertUnchanged(repository, before);
        using (PendingRepositorySave valid = facade.PrepareFrom(dev.BranchId.Value, dev.BaseCommitId.Value, null, StoredGraphKind.State, default, () => (validBusiness, target.Write))) {
            Assert.Equal(facade.SharedSchemaRoot, valid.CatalogSource);
        }
        AssertUnchanged(repository, before);
    }

    [Theory]
    [InlineData((int)RepositorySaveStage.AfterMetaAppend, false)]
    [InlineData((int)RepositorySaveStage.BeforePublication, false)]
    [InlineData((int)RepositorySaveStage.AfterPublication, true)]
    public void Two_ref_schema_growth_failure_recovers_both_old_or_both_new_and_preserves_complete_frames(int stage, bool visible) {
        FrameAddress oldHead, oldShared, mainHead;
        FrameImage[] complete;
        RepositoryPublicationException error;
        RefImage shared, oldBranches;
        using (Repository repository = Repository.CreateNew(_path, Models())) {
            using WorkingCopy main = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", Ring(), parameters: NoRebase).Id);
            repository.ForkBranch(main.BaseCommitId, "dev");
            using WorkingCopy dev = repository.OpenWorkingCopy(BranchNamed(repository, "dev"));
            dev.CommitState(parameters: NoRebase);
            dev.Catalog.Set(ExtraDeclaration);
            CommitId oldCommit = dev.BaseCommitId;
            oldHead = Describe(repository, oldCommit).Address;
            oldShared = repository.SharedSchemaRoot!.Value;
            mainHead = Describe(repository, main.BaseCommitId).Address;
            complete = ReadFrames(ResourcesOf(repository).Data);
            shared = ReadHead(repository, 2);
            oldBranches = ReadHead(repository, 1);
            NormalizedGraphState baseline = dev.Baseline!;
            DB087Widget root = AssertRing(dev.State, 7);
            root.Numbers[7] = 402;
            IOException injected = new("Shared Catalog publication interrupted.");
            repository.SaveStageObserver = checkpoint => { if (checkpoint == (RepositorySaveStage)stage) { throw injected; } };
            CommitId? delivered = null;
            error = Assert.Throws<RepositoryPublicationException>(() => delivered = dev.CommitState(root, parameters: NoRebase).Id);
            Assert.Same(injected, error.InnerException);
            Assert.Equal(visible ? RepositoryPublicationOutcome.Published : RepositoryPublicationOutcome.NotPublished, error.Outcome);
            Assert.True(repository.IsFaulted);
            Assert.Null(delivered);
            Assert.Equal(oldCommit, dev.BaseCommitId);
            Assert.Same(baseline, dev.Baseline);
        }
        using Repository cold = Repository.OpenExisting(_path, Models());
        using WorkingCopy recovered = cold.OpenWorkingCopy(BranchNamed(cold, "dev"));
        Assert.Equal(mainHead, Describe(cold, cold.ReadCurrentCommit(BranchNamed(cold, "main")).Commit.Id).Address);
        Assert.Equal(visible ? error.CandidateRevisionAddress : oldHead, Describe(cold, recovered.BaseCommitId).Address);
        Assert.Equal(visible, SchemaCatalogView.Load(cold.GraphStore, cold.SharedSchemaRoot!.Value).Schemas.ContainsKey(new(ExtraDeclaration.Type, 1)));
        Assert.Equal(cold.SharedSchemaRoot == oldShared, !visible);
        AssertSameRef(oldBranches, ReadHead(cold, 1));
        if (!visible) {
            AssertSameRef(shared, ReadHead(cold, 2));
        } else { Assert.Equal(cold.SharedSchemaRoot, cold.GraphStore.ReadMetadata(Describe(cold, recovered.BaseCommitId).Address).SchemaRoot); }
        AssertRing(recovered.State, visible ? 402 : 7);
        AssertFramesPreserved(ResourcesOf(cold).Data, complete);
        if (stage == (int)RepositorySaveStage.AfterMetaAppend) { Assert.Null(error.CandidateRevisionAddress); }
        else { Assert.NotNull(error.CandidateRevisionAddress); }
    }

    private static IReadOnlyList<ObjectVersionRecord> LocalRecords(GraphRecordStore store, StoredGraphRevision graph) =>
        graph.OwnedBatch is { } batch ? store.ReadBatch(batch).Records : [];

    private static StateModelRegistry Models() {
        StateModelRegistry models = new();
        models.Register(global::Atelia.DurableGraph.Generated.Family_5030576964676574.Definition);
        models.Register(global::Atelia.DurableGraph.Generated.Family_5030456E76656C6F7065.Definition);
        return models;
    }
    private static DB087Widget Ring() {
        List<int> numbers = Enumerable.Range(0, 128).ToList();
        DB087Widget root = new() { Value = 1, Numbers = numbers };
        root.Child = new() { Value = 2, Numbers = numbers, Child = root };
        return root;
    }
    private static DB087Widget AssertRing(IDurableObject? instance, int atSeven) {
        DB087Widget root = Assert.IsType<DB087Widget>(instance), child = Assert.IsType<DB087Widget>(root.Child);
        Assert.Equal(1, root.Value);
        Assert.Equal(2, child.Value);
        Assert.Same(root, child.Child);
        Assert.Same(root.Numbers, child.Numbers);
        Assert.Equal(128, root.Numbers.Count);
        for (int i = 0; i < 128; i++) { Assert.Equal(i == 7 ? atSeven : i, root.Numbers[i]); }
        return root;
    }
    private static DB087EnvelopeRoot AssertEnvelope(IDurableObject? instance, int atSeven) {
        DB087EnvelopeRoot root = Assert.IsType<DB087EnvelopeRoot>(instance);
        Assert.Same(root.Numbers, AssertRing(root.Link, atSeven).Numbers);
        return root;
    }
    private sealed class Counter : IDurableObject { internal int Value; }
    private static DurableSchema CounterSchema(int version) => new("Promotion.Counter", version, new DurableFieldInfo(1, TypeTag.Int32));
    // A real retained reader and current normalizer isolate RequiresRewrite;
    // this is not a claim about source-generator Upgrade history.
    private static StateModelRegistry CounterModels(int version, Action? capture = null, Action? prepareBody = null) {
        static void Visit(in int value, IStateReferenceVisitor visitor) { }
        PreparedBaseBody Base(in int value) {
            prepareBody?.Invoke();
            ArrayBufferWriter<byte> bytes = new();
            BareValueWriter writer = new(bytes);
            writer.WriteVarInt32(value);
            return new(bytes.WrittenSpan);
        }
        StateReaderBinding<int> Reader(int v) => new(CounterSchema(v), static (ref BareValueReader reader) => reader.ReadVarInt32(),
            static (ref BareValueReader reader, in int prior) => reader.ReadVarInt32(), Visit);
        StateModelRegistry models = new();
        models.Register(new StateModelBinding<Counter, int>(new(CounterSchema(version), Base,
            (in int prior, in int next) => new(prior != next, Base(next).Body)),
            version == 1 ? [Reader(1)] : [Reader(1), Reader(2)],
            static row => row.GetState<int>(), static () => new(),
            static (Counter domain, in int value, ObjectReadTable objects) => domain.Value = value,
            (domain, context) => { capture?.Invoke(); return domain.Value; }, Visit));
        return models;
    }
    private static SchemaCatalogView AppendFreshCatalog(GraphRecordStore store, DurableSchema schema, ObjectLayout? extraLayout = null) {
        SchemaCatalogWorkspace workspace = new(static () => { });
        workspace.Set(schema);
        if (extraLayout is not null) { workspace.Set(extraLayout); }
        PreparedSchemaCatalogWrite prepared = workspace.Prepare();
        StoredGraphRevision graph = store.AppendMeta(null, prepared.Write!);
        prepared.PrepareInstall(graph);
        return prepared.View;
    }

    private sealed record RefImage(HeadEntryId Revision, byte[] Payload);
    private sealed record FrameImage(FrameAddress Address, uint Tag, int MetaLength, bool IsTombstone, byte[] Payload);
    private sealed record PublicationImage(RefImage Branches, RefImage Shared, IReadOnlyDictionary<BranchId, CommitId> Business, long Appended, FrameImage[] Frames);
    private static RefImage ReadHead(Repository repository, uint id) {
        HeadEntry read = ResourcesOf(repository).Heads.ReadHead(id).Unwrap();
        return new(read.Id, read.Payload.ToArray());
    }
    private static FrameImage[] ReadFrames(Frames data) {
        List<FrameInfo> frames = [];
        data.Inventory(frames.Add).Unwrap();
        return frames.Select(info => {
            using FrameRead read = data.ReadFrame(info.Address).Unwrap();
            return new FrameImage(info.Address, read.Tag, read.TailMetaLength, read.IsTombstone, read.PayloadAndMeta.ToArray());
        }).ToArray();
    }
    private static PublicationImage Snapshot(Repository repository) => new(ReadHead(repository, 1), ReadHead(repository, 2),
        repository.ListBranches().ToDictionary(branch => branch.Id, branch => repository.ReadCurrentCommit(branch.Id).Commit.Id),
        repository.GraphStore.AppendedFrames, ReadFrames(ResourcesOf(repository).Data));
    private static void AssertSameRef(RefImage before, RefImage after) {
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.Payload, after.Payload);
    }
    private static void AssertFrame(FrameImage before, FrameImage after) {
        Assert.Equal(before.Tag, after.Tag);
        Assert.Equal(before.MetaLength, after.MetaLength);
        Assert.Equal(before.IsTombstone, after.IsTombstone);
        Assert.Equal(before.Payload, after.Payload);
    }
    private static void AssertFramesPreserved(Frames data, IEnumerable<FrameImage> before) {
        FrameImage[] after = ReadFrames(data);
        foreach (FrameImage frame in before) { AssertFrame(frame, after.Single(item => item.Address == frame.Address)); }
    }
    private static void AssertUnchanged(Repository repository, PublicationImage before) {
        Assert.Equal(before.Business.Count, repository.ListBranches().Length);
        foreach (var branch in before.Business) { Assert.Equal(branch.Value, repository.ReadCurrentCommit(branch.Key).Commit.Id); }
        AssertSameRef(before.Branches, ReadHead(repository, 1));
        AssertSameRef(before.Shared, ReadHead(repository, 2));
        Assert.Equal(before.Appended, repository.GraphStore.AppendedFrames);
        FrameImage[] after = ReadFrames(ResourcesOf(repository).Data);
        Assert.Equal(before.Frames.Select(frame => frame.Address), after.Select(frame => frame.Address));
        foreach (FrameImage frame in before.Frames) { AssertFrame(frame, after.Single(item => item.Address == frame.Address)); }
        Assert.False(ResourcesOf(repository).HasPendingSave);
        Assert.False(repository.IsFaulted);
    }
    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}
