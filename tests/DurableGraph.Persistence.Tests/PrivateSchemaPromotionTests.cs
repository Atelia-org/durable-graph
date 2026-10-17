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

public sealed class PrivateSchemaPromotionTests : IDisposable {
    private const string Prefix = "dg-private-promotion-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
    private static readonly ReadAmplificationBaseBudgetParameters NoRebase = new(1_000_000, 1);
    private static readonly DurableSchema PrivateOnly = new("Promotion.PrivateOnly", 1, new DurableFieldInfo(1, TypeTag.Int32));
    private static readonly ObjectLayout UnreachableList = ObjectLayout.ForList(new(DurableFieldInfo.Reference(1, TypeExpr.Named("Promotion.AbsentElement"))));

    [Fact]
    public void Shared_checkout_promotion_merges_the_actual_complete_head_remaps_descriptor_numbers_and_cold_continues_Delta() {
        FrameAddress privateState, privateEvent, statePin, privatePin, sharedPin, oldEnvelopeHead, promoted, continued;
        FrameImage[] complete;
        ObjectId envelopeId;
        ObjectId privateDescriptor, sharedDescriptor;
        using (Repository repository = Repository.CreateNew(_path, Models())) {
            using BranchCheckout main = repository.CreateSharedBranch("main", Ring(), NoRebase);
            DB087Widget ring = Ring();
            repository.CreateBranch("dev", main.Head);
            using BranchCheckout dev = repository.CheckoutPrivate("dev");
            dev.CommitState(new DB087EnvelopeRoot { Link = ring, Numbers = ring.Numbers }, NoRebase);
            DurableSchema envelopeSchema = dev.Models.ResolveCurrentModel(typeof(DB087EnvelopeRoot)).CurrentSchema;
            privateDescriptor = dev.Catalog.Accepted!.SchemaNodeId(envelopeSchema);
            dev.Catalog.Set(PrivateOnly);
            privateState = dev.CommitState(NoRebase).RevisionAddress;
            statePin = dev.Catalog.Accepted.Snapshot!.Address;
            dev.Catalog.Set(UnreachableList);
            privateEvent = dev.CommitEvent(Ring(), NoRebase).RevisionAddress;
            privatePin = dev.Catalog.Accepted.Snapshot!.Address;
            Assert.NotEqual(statePin, privatePin);
            Assert.Equal(privateState, dev.StateRevisionAddress);
            envelopeId = dev.StateId!.Value;
            oldEnvelopeHead = dev.Baseline!.Objects[envelopeId].Storage!.Value.Head;
            DurableSchema other = new("0.Collision.Envelope", 1, envelopeSchema.Fields.ToArray());
            main.Catalog.Set(other);
            main.Catalog.Set(envelopeSchema);
            main.CommitState(NoRebase);
            SchemaCatalogView target = SchemaCatalogView.Load(repository.GraphStore, repository.SharedSchemaRoot!.Value);
            Assert.Equal(privateDescriptor, target.SchemaNodeId(other)); // Equal numbers denote different complete layouts.
            sharedDescriptor = target.SchemaNodeId(envelopeSchema);
            Assert.NotEqual(privateDescriptor, sharedDescriptor);
            sharedPin = target.Snapshot!.Address;
            complete = ReadFrames(ResourcesOf(repository).Data);
        }
        using (Repository repository = Repository.OpenExisting(_path, Models())) {
            using BranchCheckout ordinary = repository.CheckoutShared("dev");

            Assert.False(ordinary.UsesPrivateSchemas);
            Assert.Equal(sharedPin, ordinary.Catalog.Accepted!.Snapshot!.Address);
            Assert.False(ordinary.Catalog.Accepted.Schemas.ContainsKey(new(PrivateOnly.Type, 1)));
            Assert.Equal(privateEvent, ordinary.Head.RevisionAddress);
            Assert.Equal(privateState, ordinary.StateRevisionAddress);
            Assert.Equal(privatePin, repository.GraphStore.ReadMetadata(ordinary.Head.RevisionAddress).SchemaRoot);
            ModelExecutionEnvironment models = ordinary.Models;
            DecodedRevision.SchemaViewBindingEnvironment environment = ordinary.BindingEnvironment!;
            var storage = ordinary.Baseline!.Objects.Select(pair => (pair.Key, pair.Value.Storage)).ToArray();
            StoredGraphRevision previous = repository.GraphStore.ReadGraph(privateState);
            PublicationImage beforePromotion = Snapshot(repository);
            Assert.Throws<InvalidOperationException>(() => ordinary.CommitState(NoRebase));
            AssertUnchanged(repository, beforePromotion);
            promoted = ordinary.CommitStateToShared(NoRebase).RevisionAddress;
            StoredGraphRevision saved = repository.GraphStore.ReadGraph(promoted);
            SchemaCatalogView merged = SchemaCatalogView.Load(repository.GraphStore, saved.SchemaRoot!.Value);
            Assert.Equal(repository.SharedSchemaRoot, saved.SchemaRoot);
            Assert.Equal(sharedPin, merged.Snapshot!.SourceGraph);
            Assert.Equal(PrivateOnly, merged.Schemas[new(PrivateOnly.Type, 1)]);
            Assert.Equal(UnreachableList, merged.ResolveLayout(merged.DescriptorId(UnreachableList)));
            DurableSchema envelope = ordinary.Models.ResolveCurrentModel(typeof(DB087EnvelopeRoot)).CurrentSchema;
            Assert.Equal(sharedDescriptor, merged.SchemaNodeId(envelope));
            Assert.Equal(previous.DirectoryRoot, saved.DirectoryRoot);
            Assert.Equal(previous.ObjectHeads.ToArray(), saved.ObjectHeads.ToArray());
            Assert.Null(saved.OwnedBatch);
            Assert.Equal(privateEvent, saved.Parent);
            Assert.Equal(privateState, saved.SourceGraph);
            Assert.Equal(storage, ordinary.Baseline!.Objects.Select(pair => (pair.Key, pair.Value.Storage)).ToArray());
            ObjectVersionChain retained = repository.GraphStore.ReadObjectChain(promoted, envelopeId.Value);
            Assert.Equal(oldEnvelopeHead, retained.Head);
            Assert.Equal(statePin, environment.SelectedSchemaView.Snapshot!.Address);
            Assert.NotEqual(saved.SchemaRoot, retained.Entries[0].SchemaRoot);
            BareValueReader header = new(retained.Entries[0].Record.Body);
            Assert.Equal((byte)5, header.ReadByte());
            Assert.Equal((byte)1, header.ReadByte());
            Assert.Equal(privateDescriptor.Value, header.ReadVarUInt32());
            AssertEnvelope(repository.ReadState(repository.GetHead("dev")), 7);

            Assert.False(ordinary.UsesPrivateSchemas);
            Assert.Same(models, ordinary.Models);
            Assert.Same(environment, ordinary.BindingEnvironment);
            RefImage shared = ReadHead(repository, 2);
            FrameAddress reused = ordinary.CommitState(NoRebase).RevisionAddress;
            StoredGraphRevision unchanged = repository.GraphStore.ReadGraph(reused);
            Assert.Equal(saved.SchemaRoot, unchanged.SchemaRoot);
            Assert.Equal(saved.DirectoryRoot, unchanged.DirectoryRoot);
            Assert.Null(unchanged.OwnedBatch);
            AssertSameRef(shared, ReadHead(repository, 2)); // No meta write uses the single branch ref.
            DB087EnvelopeRoot root = AssertEnvelope(ordinary.State, 7);
            ObjectId list = ordinary.Baseline!.Objects.Single(pair => pair.Value.Current.Kind == ObjectStateKind.List).Key;
            ObjectStorageInfo prior = ordinary.Baseline.Objects[list].Storage!.Value;
            root.Numbers[7] = 401;
            continued = ordinary.CommitState(root, NoRebase).RevisionAddress;
            StoredGraphRevision deltaGraph = repository.GraphStore.ReadGraph(continued);
            Assert.Equal(reused, deltaGraph.SourceGraph);
            ObjectVersionBatch batch = repository.GraphStore.ReadBatch(deltaGraph.OwnedBatch!.Value);
            Assert.Equal(reused, batch.SourceGraph);
            Assert.Equal(saved.SchemaRoot, batch.SchemaRoot);
            ObjectVersionRecord delta = Assert.Single(batch.Records);
            Assert.Equal(list.Value, delta.ObjectId);
            Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
            Assert.Equal(prior.Head, delta.PriorAddress);
            Assert.Equal(prior.ReconstructionPayloadBytes + delta.EncodedPayloadBytes!.Value,
                ordinary.Baseline.Objects[list].Storage!.Value.ReconstructionPayloadBytes);
            AssertFramesPreserved(ResourcesOf(repository).Data, complete);
        }
        using Repository cold = Repository.OpenReadOnlyExisting(_path, Models());
        AssertEnvelope(cold.ReadState(cold.GetHead("dev")), 401);
        AssertEnvelope(cold.ReadState(cold.ReadFrames("dev").Single(frame => frame.RevisionAddress == privateState)), 7);
        AssertEnvelope(cold.ReadState(cold.ReadFrames("dev").Single(frame => frame.RevisionAddress == promoted)), 7);
        Assert.Equal(continued, cold.GetHead("dev").RevisionAddress);
        AssertFramesPreserved(ResourcesOf(cold).Data, complete);
    }

    [Fact]
    public void Private_session_after_promotion_keeps_promoted_pin_and_excludes_later_shared_additions_even_after_cold_open() {
        DurableSchema laterShared = new("Promotion.LaterShared", 1, new DurableFieldInfo(1, TypeTag.Byte));
        DurableSchema warmPrivate = new("Promotion.WarmPrivate", 1, new DurableFieldInfo(1, TypeTag.Int16));
        DurableSchema coldPrivate = new("Promotion.ColdPrivate", 1, new DurableFieldInfo(1, TypeTag.Int64));
        FrameAddress promotedPin, warmPin, warmState, sharedPin;
        RefImage shared;
        FrameImage[] complete;
        using (Repository repository = Repository.CreateNew(_path, Models())) {
            using BranchCheckout main = repository.CreateSharedBranch("main", Ring(), NoRebase);
            repository.CreateBranch("dev", main.Head);
            using BranchCheckout dev = repository.CheckoutPrivate("dev");
            ModelExecutionEnvironment models = dev.Models;
            DecodedRevision.SchemaViewBindingEnvironment execution = dev.BindingEnvironment!;
            dev.Catalog.Set(PrivateOnly);
            dev.CommitState(NoRebase);
            dev.CommitStateToShared(NoRebase);
            promotedPin = dev.Catalog.Accepted!.Snapshot!.Address;
            Assert.Equal(promotedPin, repository.SharedSchemaRoot);
            Assert.True(dev.UsesPrivateSchemas);
            Assert.Same(models, dev.Models);
            Assert.Same(execution, dev.BindingEnvironment);

            main.Catalog.Set(laterShared);
            main.CommitState(NoRebase);
            sharedPin = repository.SharedSchemaRoot!.Value;
            Assert.NotEqual(promotedPin, sharedPin);
            SchemaCatalogView latest = SchemaCatalogView.Load(repository.GraphStore, sharedPin);
            Assert.Equal(PrivateOnly, latest.Schemas[new(PrivateOnly.Type, 1)]);
            Assert.Equal(laterShared, latest.Schemas[new(laterShared.Type, 1)]);
            shared = ReadHead(repository, 2);
            Assert.Equal(promotedPin, dev.Catalog.Accepted.Snapshot.Address);
            Assert.False(dev.Catalog.Accepted.Schemas.ContainsKey(new(laterShared.Type, 1)));

            dev.Catalog.Set(warmPrivate);
            AssertRing(dev.State, 7).Numbers[7] = 501;
            using (PreparedBranchSave candidate = dev.SaveSession.Prepare(dev.State!, parameters: NoRebase)) {
                Assert.Equal(promotedPin, candidate.Pending.CatalogSource);
            }
            warmState = dev.CommitState(NoRebase).RevisionAddress;
            StoredGraphRevision saved = repository.GraphStore.ReadGraph(warmState);
            warmPin = saved.SchemaRoot!.Value;
            SchemaCatalogView view = SchemaCatalogView.Load(repository.GraphStore, warmPin);
            Assert.Equal(promotedPin, view.Snapshot!.SourceGraph);
            Assert.Equal(PrivateOnly, view.Schemas[new(PrivateOnly.Type, 1)]);
            Assert.Equal(warmPrivate, view.Schemas[new(warmPrivate.Type, 1)]);
            Assert.False(view.Schemas.ContainsKey(new(laterShared.Type, 1)));
            Assert.Equal(warmPin, repository.GraphStore.ReadMetadata(dev.Head.RevisionAddress).SchemaRoot);
            Assert.True(dev.UsesPrivateSchemas);
            Assert.Same(models, dev.Models);
            Assert.Same(execution, dev.BindingEnvironment);
            AssertSameRef(shared, ReadHead(repository, 2));
            complete = ReadFrames(ResourcesOf(repository).Data);
        }
        using (Repository repository = Repository.OpenExisting(_path, Models())) {
            using BranchCheckout dev = repository.CheckoutPrivate("dev");
            Assert.Equal(warmState, dev.Head.RevisionAddress);
            Assert.Equal(warmPin, dev.Catalog.Accepted!.Snapshot!.Address);
            Assert.Equal(PrivateOnly, dev.Catalog.Accepted.Schemas[new(PrivateOnly.Type, 1)]);
            Assert.Equal(warmPrivate, dev.Catalog.Accepted.Schemas[new(warmPrivate.Type, 1)]);
            Assert.False(dev.Catalog.Accepted.Schemas.ContainsKey(new(laterShared.Type, 1)));
            dev.Catalog.Set(coldPrivate);
            AssertRing(dev.State, 501).Numbers[7] = 502;
            using (PreparedBranchSave candidate = dev.SaveSession.Prepare(dev.State!, parameters: NoRebase)) {
                Assert.Equal(warmPin, candidate.Pending.CatalogSource);
            }
            CheckpointAddress next = dev.CommitState(NoRebase);
            StoredGraphRevision saved = repository.GraphStore.ReadGraph(next.RevisionAddress);
            Assert.Equal(warmState, saved.SourceGraph);
            Assert.Equal(saved.SchemaRoot, repository.GraphStore.ReadMetadata(dev.Head.RevisionAddress).SchemaRoot);
            SchemaCatalogView view = SchemaCatalogView.Load(repository.GraphStore, saved.SchemaRoot!.Value);
            Assert.Equal(warmPin, view.Snapshot!.SourceGraph);
            Assert.Equal(PrivateOnly, view.Schemas[new(PrivateOnly.Type, 1)]);
            Assert.Equal(warmPrivate, view.Schemas[new(warmPrivate.Type, 1)]);
            Assert.Equal(coldPrivate, view.Schemas[new(coldPrivate.Type, 1)]);
            Assert.False(view.Schemas.ContainsKey(new(laterShared.Type, 1)));
            Assert.Equal(sharedPin, repository.SharedSchemaRoot);
            AssertSameRef(shared, ReadHead(repository, 2));
            AssertFramesPreserved(ResourcesOf(repository).Data, complete);
        }
        using Repository reader = Repository.OpenReadOnlyExisting(_path, Models());
        AssertRing(reader.ReadState(reader.GetHead("dev")), 502);
        AssertSameRef(shared, ReadHead(reader, 2));
        AssertFramesPreserved(ResourcesOf(reader).Data, complete);
    }

    [Fact]
    public void Layout_only_promotion_adds_no_Schema_but_publishes_both_refs_and_reuses_the_business_directory() {
        using Repository repository = Repository.CreateNew(_path, Models());
        using BranchCheckout main = repository.CreateSharedBranch("main", Ring(), NoRebase);
        repository.CreateBranch("dev", main.Head);
        using BranchCheckout dev = repository.CheckoutPrivate("dev");
        ObjectLayout dynamicList = ObjectLayout.ForList(new(DurableFieldInfo.Reference(1, TypeExpr.Named("P0Widget"))));
        dev.Catalog.Set(dynamicList);
        StoredGraphRevision previous = repository.GraphStore.ReadGraph(dev.CommitState(NoRebase).RevisionAddress);
        SchemaCatalogView oldShared = SchemaCatalogView.Load(repository.GraphStore, repository.SharedSchemaRoot!.Value);
        Assert.Throws<InvalidDataException>(() => oldShared.DescriptorId(dynamicList));
        PublicationImage before = Snapshot(repository);
        StoredGraphRevision saved = repository.GraphStore.ReadGraph(dev.CommitStateToShared(NoRebase).RevisionAddress);
        SchemaCatalogView merged = SchemaCatalogView.Load(repository.GraphStore, saved.SchemaRoot!.Value);
        Assert.Equal(oldShared.Schemas.Count, merged.Schemas.Count);
        Assert.Equal(oldShared.ObjectLayouts.Count() + 1, merged.ObjectLayouts.Count());
        Assert.Equal(dynamicList, merged.ResolveLayout(merged.DescriptorId(dynamicList)));
        Assert.NotEqual(before.Branches.Revision, ReadHead(repository, 1).Revision);
        Assert.NotEqual(before.Shared.Revision, ReadHead(repository, 2).Revision);
        Assert.Equal(repository.SharedSchemaRoot, saved.SchemaRoot);
        Assert.Equal(previous.DirectoryRoot, saved.DirectoryRoot);
        Assert.Equal(previous.ObjectHeads.ToArray(), saved.ObjectHeads.ToArray());
        Assert.Null(saved.OwnedBatch);
        Assert.True(dev.UsesPrivateSchemas);
    }

    [Fact]
    public void Shared_Event_accepts_meta_without_consuming_the_State_heads_H_or_pending_layout_rewrite() {
        FrameAddress source, sourcePin, eventAddress;
        ObjectStorageInfo original;
        using (Repository repository = Repository.CreateNew(_path, CounterModels(1))) {
            using BranchCheckout main = repository.CreateSharedBranch("main", new Counter { Value = 7 }, NoRebase);
            repository.CreateBranch("dev", main.Head);
            using BranchCheckout dev = repository.CheckoutPrivate("dev");
            source = dev.Head.RevisionAddress;
            sourcePin = dev.Catalog.Accepted!.Snapshot!.Address;
            original = dev.Baseline!.Objects[dev.StateId!.Value].Storage!.Value;
            Assert.NotNull(repository.SharedSchemaRoot);
        }
        using Repository cold = Repository.OpenExisting(_path, CounterModels(2));
        using BranchCheckout checkout = cold.CheckoutPrivate("dev");
        NormalizedGraphState baseline = checkout.Baseline!;
        ObjectId id = checkout.StateId!.Value;
        Assert.True(baseline.Objects[id].RequiresRewrite);
        Assert.Equal(original, baseline.Objects[id].Storage);
        ModelExecutionEnvironment models = checkout.Models;
        DecodedRevision.SchemaViewBindingEnvironment environment = checkout.BindingEnvironment!;
        checkout.Catalog.Set(PrivateOnly);
        eventAddress = checkout.CommitEventToShared(new Counter { Value = 20 }, NoRebase).RevisionAddress;
        StoredGraphRevision ev = cold.GraphStore.ReadGraph(eventAddress);
        Assert.Equal(source, ev.Parent);
        Assert.Equal(source, ev.SourceGraph);
        Assert.Equal(cold.SharedSchemaRoot, ev.SchemaRoot);
        Assert.NotEqual(sourcePin, ev.SchemaRoot);
        Assert.Equal(PrivateOnly, SchemaCatalogView.Load(cold.GraphStore, ev.SchemaRoot!.Value).Schemas[new(PrivateOnly.Type, 1)]);
        Assert.Equal(0, checkout.Catalog.DirtyCount);
        Assert.Same(baseline, checkout.Baseline);
        Assert.True(checkout.Baseline!.Objects[id].RequiresRewrite);
        Assert.Equal(original, checkout.Baseline.Objects[id].Storage);
        Assert.Equal(source, checkout.StateRevisionAddress);
        Assert.Equal(id, checkout.StateId);
        Assert.Equal(7, Assert.IsType<Counter>(checkout.State).Value);
        Assert.Equal(20, Assert.IsType<Counter>(cold.ReadEvent(checkout.Head)).Value);
        Assert.True(checkout.UsesPrivateSchemas);

        Assert.Same(models, checkout.Models);
        Assert.Same(environment, checkout.BindingEnvironment);
        FrameAddress state = checkout.CommitStateToShared(NoRebase).RevisionAddress;
        StoredGraphRevision saved = cold.GraphStore.ReadGraph(state);
        Assert.Equal(eventAddress, saved.Parent);
        Assert.Equal(source, saved.SourceGraph);
        ObjectVersionRecord rewritten = Assert.Single(LocalRecords(cold.GraphStore, state));
        Assert.Equal(id.Value, rewritten.ObjectId);
        Assert.Equal(ObjectVersionKind.Base, rewritten.Kind);
        Assert.False(checkout.Baseline!.Objects[id].RequiresRewrite);
        Assert.NotEqual(original.Head, checkout.Baseline.Objects[id].Storage!.Value.Head);
        Assert.Equal(rewritten.EncodedPayloadBytes!.Value, checkout.Baseline.Objects[id].Storage!.Value.ReconstructionPayloadBytes);
    }

    [Fact]
    public void Discard_keeps_private_dirty_intent_and_a_later_conflict_is_corrected_before_shared_acceptance() {
        int captures = 0;
        using Repository repository = Repository.CreateNew(_path, CounterModels(1, () => captures++));
        using BranchCheckout main = repository.CreateSharedBranch("main", new Counter { Value = 1 }, NoRebase);
        repository.CreateBranch("dev", main.Head);
        using BranchCheckout dev = repository.CheckoutPrivate("dev");
        Assert.IsType<Counter>(dev.State).Value = 2;
        dev.Catalog.Set(PrivateOnly);
        dev.CommitState(NoRebase);
        DurableSchema wrong = new("Promotion.Corrected", 1, new DurableFieldInfo(1, TypeTag.Byte));
        DurableSchema correct = new(wrong.Type, 1, new DurableFieldInfo(1, TypeTag.Int64));
        SchemaCatalogView accepted = dev.Catalog.Accepted!;
        NormalizedGraphState baseline = dev.Baseline!;
        ModelExecutionEnvironment models = dev.Models;
        PublicationImage before = Snapshot(repository);
        dev.Catalog.Set(wrong);
        dev.PrepareAndDiscard(dev.State!, parameters: NoRebase, toShared: true);
        AssertUnchanged(repository, before);
        Assert.Same(accepted, dev.Catalog.Accepted);
        Assert.Same(baseline, dev.Baseline);
        Assert.True(dev.Catalog.DirtyCount > 0);
        Assert.True(dev.UsesPrivateSchemas);
        main.Catalog.Set(correct);
        main.CommitState(NoRebase);
        PublicationImage beforeConflict = Snapshot(repository);
        captures = 0;
        Assert.Throws<SchemaConflictException>(() => dev.CommitStateToShared(NoRebase));
        Assert.Equal(0, captures);
        AssertUnchanged(repository, beforeConflict);
        Assert.Same(accepted, dev.Catalog.Accepted);
        Assert.Same(baseline, dev.Baseline);
        dev.Catalog.Set(correct);
        dev.CommitStateToShared(NoRebase);
        Assert.Equal(1, captures);
        SchemaCatalogView merged = SchemaCatalogView.Load(repository.GraphStore, repository.SharedSchemaRoot!.Value);
        Assert.Equal(PrivateOnly, merged.Schemas[new(PrivateOnly.Type, 1)]);
        Assert.Equal(correct, merged.Schemas[new(correct.Type, 1)]);
        Assert.Equal(0, dev.Catalog.DirtyCount);
        Assert.True(dev.UsesPrivateSchemas);
        Assert.Same(models, dev.Models);
    }

    [Fact]
    public void An_unreachable_published_private_conflict_rejects_before_capture_and_leaves_private_saving_usable() {
        int captures = 0;
        DurableSchema a = new("Promotion.Conflict", 1, new DurableFieldInfo(1, TypeTag.Int32));
        DurableSchema b = new(a.Type, 1, new DurableFieldInfo(1, TypeTag.Int64));
        using Repository repository = Repository.CreateNew(_path, CounterModels(1, () => captures++));
        using BranchCheckout main = repository.CreateSharedBranch("main", new Counter { Value = 1 }, NoRebase);
        repository.CreateBranch("dev", main.Head);
        using BranchCheckout dev = repository.CheckoutPrivate("dev");
        Assert.IsType<Counter>(dev.State).Value = 2;
        dev.Catalog.Set(b);
        dev.CommitState(NoRebase);
        main.Catalog.Set(a);
        main.CommitState(NoRebase);
        PublicationImage before = Snapshot(repository);
        NormalizedGraphState baseline = dev.Baseline!;
        captures = 0;
        Assert.Throws<SchemaConflictException>(() => dev.CommitStateToShared(NoRebase));
        Assert.Equal(0, captures);
        AssertUnchanged(repository, before);
        Assert.Same(baseline, dev.Baseline);
        dev.CommitState(NoRebase);
        Assert.Equal(1, captures);
        Assert.Equal(b, dev.Catalog.Accepted!.Schemas[new(b.Type, 1)]);
        AssertSameRef(before.Shared, ReadHead(repository, 2));
    }

    [Fact]
    public void Synchronous_promotion_loads_each_independent_Base_catalog_once_and_merges_captured_layouts() {
        using Frames frames = Frames.Create(_path);
        DurableSchema sourceSchema = new("Promotion.Count.Source", 1, new DurableFieldInfo(1, TypeTag.Int32));
        DurableSchema targetSchema = new("Promotion.Count.Target", 1, new DurableFieldInfo(1, TypeTag.Byte));
        DurableSchema dirtySchema = new("Promotion.Count.Dirty", 1, new DurableFieldInfo(1, TypeTag.Boolean));
        DurableSchema capturedSchema = new("Promotion.Count.Captured", 1, new DurableFieldInfo(1, TypeTag.Int64));
        SchemaCatalogView source, target;
        using (GraphRecordStore writer = new(frames)) {
            source = AppendFreshCatalog(writer, sourceSchema, UnreachableList);
            target = AppendFreshCatalog(writer, targetSchema);
            Assert.NotEqual(source.Snapshot!.Address, target.Snapshot!.Address);
            Assert.Null(source.Snapshot!.SourceGraph);
            Assert.Null(target.Snapshot!.SourceGraph);
            Assert.All(writer.ReadBatch(source.Snapshot!.OwnedBatch!.Value).Records, row => Assert.Equal(ObjectVersionKind.Base, row.Kind));
            Assert.All(writer.ReadBatch(target.Snapshot!.OwnedBatch!.Value).Records, row => Assert.Equal(ObjectVersionKind.Base, row.Kind));
        }
        using GraphRecordStore reader = new(frames, readCacheBudgetBytes: 0);
        SchemaCatalogWorkspace workspace = new(static () => { }, source);
        workspace.Set(dirtySchema);
        int captures = 0;
        PreparedSchemaCatalogWrite prepared = workspace.PrepareToSharedOwned(reader, source.Snapshot!.Address, target.Snapshot!.Address, () => {
            captures++;
            Assert.Equal(2, reader.ReadCacheStatistics.GraphDecodes);
            Assert.Equal(2, reader.ReadCacheStatistics.DirectoryMaterializations);
            return [ObjectLayout.ForDurable(capturedSchema)];
        });

        Assert.Equal(1, captures);
        Assert.Equal(2, reader.ReadCacheStatistics.GraphDecodes);
        Assert.Equal(2, reader.ReadCacheStatistics.DirectoryMaterializations);
        Assert.Equal(0, reader.AppendedFrames);
        Assert.Equal(target.Snapshot!.Address, prepared.Write!.SourceGraph);
        foreach (DurableSchema schema in new[] { sourceSchema, targetSchema, dirtySchema, capturedSchema }) {
            Assert.Equal(schema, prepared.View.Schemas[new(schema.Type, schema.Version)]);
        }
        Assert.Equal(UnreachableList, prepared.View.ResolveLayout(prepared.View.DescriptorId(UnreachableList)));
        Assert.Same(source, workspace.Accepted);
        Assert.Equal(1, workspace.DirtyCount);
    }

    [Fact]
    public void Captured_same_key_conflict_rejects_after_one_callback_without_accepting_dirty_intent() {
        using Frames frames = Frames.Create(_path);
        DurableSchema sourceSchema = new("Promotion.Late.Source", 1, new DurableFieldInfo(1, TypeTag.Byte));
        DurableSchema registered = new("Promotion.Late.Target", 1, new DurableFieldInfo(1, TypeTag.Int32));
        DurableSchema captured = new(registered.Type, 1, new DurableFieldInfo(1, TypeTag.Int64));
        DurableSchema dirty = new("Promotion.Late.Dirty", 1, new DurableFieldInfo(1, TypeTag.Byte));
        SchemaCatalogView source, target;
        using (GraphRecordStore writer = new(frames)) {
            source = AppendFreshCatalog(writer, sourceSchema);
            target = AppendFreshCatalog(writer, registered);
        }
        using GraphRecordStore reader = new(frames, readCacheBudgetBytes: 0);
        SchemaCatalogWorkspace workspace = new(static () => { }, source);
        workspace.Set(dirty);
        int captures = 0;
        PreparedSchemaCatalogWrite? delivered = null;
        SchemaConflictException error = Assert.Throws<SchemaConflictException>(() => delivered = workspace.PrepareToSharedOwned(reader,
            source.Snapshot!.Address, target.Snapshot!.Address, () => {
                captures++;
                return [ObjectLayout.ForDurable(captured)];
            }));
        Assert.Equal(registered, error.RegisteredSchema);
        Assert.Equal(captured, error.ConflictingSchema);
        Assert.Equal(1, captures);
        Assert.Null(delivered);
        Assert.Same(source, workspace.Accepted);
        Assert.Equal(1, workspace.DirtyCount);
        Assert.Equal(0, reader.AppendedFrames);
        PreparedSchemaCatalogWrite corrected = workspace.PrepareToSharedOwned(reader, source.Snapshot!.Address,
            target.Snapshot!.Address, () => { captures++; return [ObjectLayout.ForDurable(registered)]; });
        Assert.Equal(2, captures);
        Assert.Equal(dirty, corrected.View.Schemas[new(dirty.Type, 1)]);
        Assert.Same(source, workspace.Accepted);
        Assert.Equal(1, workspace.DirtyCount);
    }

    [Fact]
    public void Promotion_imports_a_new_root_layout_first_encountered_by_business_Capture() {
        using Repository repository = Repository.CreateNew(_path, Models());
        using BranchCheckout main = repository.CreateSharedBranch("main", Ring(), NoRebase);
        repository.CreateBranch("dev", main.Head);
        using BranchCheckout dev = repository.CheckoutPrivate("dev");
        DurableSchema envelope = dev.Models.ResolveCurrentModel(typeof(DB087EnvelopeRoot)).CurrentSchema;
        SchemaCatalogView before = SchemaCatalogView.Load(repository.GraphStore, repository.SharedSchemaRoot!.Value);
        Assert.False(before.Schemas.ContainsKey(new(envelope.Type, envelope.Version)));
        DB087Widget ring = AssertRing(dev.State, 7);
        DB087EnvelopeRoot root = new() { Link = ring, Numbers = ring.Numbers };

        CheckpointAddress saved = dev.CommitStateToShared(root, NoRebase);
        SchemaCatalogView merged = SchemaCatalogView.Load(repository.GraphStore, repository.SharedSchemaRoot!.Value);
        Assert.Equal(envelope, merged.Schemas[new(envelope.Type, envelope.Version)]);
        Assert.Equal(before.Snapshot!.Address, merged.Snapshot!.SourceGraph);
        Assert.Same(root, dev.State);
        Assert.Equal(saved, dev.Head);
        AssertEnvelope(repository.ReadState(saved), 7);
        Assert.Equal(0, dev.Catalog.DirtyCount);
        Assert.True(dev.UsesPrivateSchemas);
    }

    [Fact]
    public void Late_shared_conflict_from_the_captured_new_root_keeps_private_saving_usable() {
        using Repository repository = Repository.CreateNew(_path, Models());
        using BranchCheckout main = repository.CreateSharedBranch("main", Ring(), NoRebase);
        repository.CreateBranch("dev", main.Head);
        using BranchCheckout dev = repository.CheckoutPrivate("dev");
        DurableSchema envelope = dev.Models.ResolveCurrentModel(typeof(DB087EnvelopeRoot)).CurrentSchema;
        DurableSchema wrong = new(envelope.Type, envelope.Version, new DurableFieldInfo(1, TypeTag.Int64));
        Assert.False(dev.Catalog.Accepted!.Schemas.ContainsKey(new(envelope.Type, envelope.Version)));
        main.Catalog.Set(wrong);
        main.CommitState(NoRebase);
        SchemaCatalogView accepted = dev.Catalog.Accepted!;
        NormalizedGraphState baseline = dev.Baseline!;
        PublicationImage before = Snapshot(repository);
        DB087Widget ring = AssertRing(dev.State, 7);
        DB087EnvelopeRoot root = new() { Link = ring, Numbers = ring.Numbers };
        CheckpointAddress? delivered = null;
        SchemaConflictException error = Assert.Throws<SchemaConflictException>(() => delivered = dev.CommitStateToShared(root, NoRebase));
        Assert.Equal(wrong, error.RegisteredSchema);
        Assert.Equal(envelope, error.ConflictingSchema);
        Assert.Null(delivered);
        Assert.Same(accepted, dev.Catalog.Accepted);
        Assert.Same(baseline, dev.Baseline);
        AssertUnchanged(repository, before);

        CheckpointAddress saved = dev.CommitState(root, NoRebase);
        Assert.Same(root, dev.State);
        AssertEnvelope(repository.ReadState(saved), 7);
        AssertSameRef(before.Shared, ReadHead(repository, 2));
    }

    [Fact]
    public void Promotion_Capture_keeps_public_and_resource_owner_reentry_guards_until_preparation_finishes() {
        Action? duringCapture = null;
        int captures = 0;
        using Repository repository = Repository.CreateNew(_path, CounterModels(1, () => { captures++; duringCapture?.Invoke(); }));
        using BranchCheckout checkout = repository.CreateSharedBranchFromEvent("main", new Counter { Value = 1 }, NoRebase);
        RepositoryResources resources = ResourcesOf(repository);
        using RepositoryStorageFacade facade = new(resources);
        bool nestedProducerCalled = false;
        captures = 0;
        duringCapture = () => {
            Assert.Throws<InvalidOperationException>(() => checkout.CommitEventToShared(new Counter { Value = 99 }, NoRebase));
            Assert.Throws<InvalidOperationException>(() => checkout.Catalog.Set(PrivateOnly));
            Assert.Throws<InvalidOperationException>(() => repository.Dispose());
            Assert.Throws<InvalidOperationException>(() => resources.Dispose());
            Assert.Throws<InvalidOperationException>(() => facade.Dispose());
            Assert.Throws<InvalidOperationException>(() => facade.PrepareFrom("other", StoredGraphKind.State,
                () => { nestedProducerCalled = true; throw new IOException("Nested producer must not be called."); }));
            Assert.False(nestedProducerCalled);
        };
        CheckpointAddress saved = checkout.CommitEventToShared(new Counter { Value = 2 }, NoRebase);
        duringCapture = null;
        Assert.Equal(1, captures);
        Assert.Equal(saved, checkout.Head);
        Assert.Null(checkout.State);
        Assert.Equal(2, Assert.IsType<Counter>(repository.ReadEvent(saved)).Value);
        Assert.False(resources.HasPendingSave);
        Assert.False(repository.IsFaulted);
    }

    [Theory]
    [InlineData("StoreDispose")]
    [InlineData("OwnerFault")]
    [InlineData("DataClose")]
    [InlineData("VersionsClose")]
    public void Event_only_promotion_rejects_stopped_Store_before_business_body_preparation_or_pending(string stop) {
        Action? duringCapture = null;
        int captures = 0, bodyPreparations = 0, afterPrepare = 0;
        using Repository repository = Repository.CreateNew(_path,
            CounterModels(1, () => { captures++; duringCapture?.Invoke(); }, () => bodyPreparations++));
        using BranchCheckout checkout = repository.CreateSharedBranchFromEvent("main", new Counter { Value = 1 }, NoRebase);
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
        CheckpointAddress? delivered = null;
        Assert.ThrowsAny<InvalidOperationException>(() => delivered = checkout.CommitEventToShared(new Counter { Value = 2 }, NoRebase));
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
        Assert.Equal(1, Assert.IsType<Counter>(cold.ReadEvent(cold.GetHead("main"))).Value);
    }

    [Fact]
    public void Shared_raw_pending_requires_latest_shared_meta_source_and_the_exact_business_State_source() {
        using Repository repository = Repository.CreateNew(_path, Models());
        using BranchCheckout main = repository.CreateSharedBranch("main", Ring(), NoRebase);
        repository.CreateBranch("dev", main.Head);
        using BranchCheckout dev = repository.CheckoutPrivate("dev");
        dev.Catalog.Set(PrivateOnly);
        dev.CommitState(NoRebase);
        FrameAddress headPin = dev.Catalog.Accepted!.Snapshot!.Address;
        Assert.NotEqual(repository.SharedSchemaRoot, headPin);
        using RepositoryStorageFacade facade = new(ResourcesOf(repository));
        SchemaCatalogWorkspace catalog = new(facade.EnsureEditingAllowed, SchemaCatalogView.Load(facade.Store, headPin));
        catalog.Set(new DurableSchema("Promotion.RawExtra", 1, new DurableFieldInfo(1, TypeTag.Byte)));
        PreparedSchemaCatalogWrite privateCandidate = catalog.Prepare();
        PreparedGraphWrite validBusiness = new(dev.StateRevisionAddress, dev.StateId!.Value, []);
        PublicationImage before = Snapshot(repository);
        Assert.Throws<InvalidDataException>(() => facade.Prepare("dev", StoredGraphKind.State, validBusiness, privateCandidate.Write));
        AssertUnchanged(repository, before);
        PreparedSchemaCatalogWrite target = catalog.PrepareToSharedOwned(facade.Store, headPin, facade.SharedSchemaRoot, static () => []);
        Assert.Equal(facade.SharedSchemaRoot, target.Write!.SourceGraph);
        Assert.NotEqual(main.StateRevisionAddress, dev.StateRevisionAddress);
        PreparedGraphWrite wrongBusiness = new(main.StateRevisionAddress, dev.StateId.Value, []);
        Assert.Throws<InvalidDataException>(() => facade.Prepare("dev", StoredGraphKind.State, wrongBusiness, target.Write));
        AssertUnchanged(repository, before);
        using (PendingRepositorySave valid = facade.Prepare("dev", StoredGraphKind.State, validBusiness, target.Write)) {
            Assert.Equal(facade.SharedSchemaRoot, valid.CatalogSource);
        }
        AssertUnchanged(repository, before);
    }

    [Theory]
    [InlineData((int)RepositorySaveStage.AfterMetaAppend, false)]
    [InlineData((int)RepositorySaveStage.BeforePublication, false)]
    [InlineData((int)RepositorySaveStage.AfterPublication, true)]
    public void Two_ref_promotion_failure_recovers_both_old_or_both_new_and_preserves_complete_frames(int stage, bool visible) {
        FrameAddress oldHead, oldShared, mainHead;
        FrameImage[] complete;
        RepositoryPublicationException error;
        RefImage shared, oldBranches;
        using (Repository repository = Repository.CreateNew(_path, Models())) {
            using BranchCheckout main = repository.CreateSharedBranch("main", Ring(), NoRebase);
            repository.CreateBranch("dev", main.Head);
            using BranchCheckout dev = repository.CheckoutPrivate("dev");
            dev.Catalog.Set(PrivateOnly);
            dev.CommitState(NoRebase);
            oldHead = dev.Head.RevisionAddress;
            oldShared = repository.SharedSchemaRoot!.Value;
            mainHead = main.Head.RevisionAddress;
            complete = ReadFrames(ResourcesOf(repository).Data);
            shared = ReadHead(repository, 2);
            oldBranches = ReadHead(repository, 1);
            NormalizedGraphState baseline = dev.Baseline!;
            DB087Widget root = AssertRing(dev.State, 7);
            root.Numbers[7] = 402;
            IOException injected = new("Promotion publication interrupted.");
            repository.SaveStageObserver = checkpoint => { if (checkpoint == (RepositorySaveStage)stage) { throw injected; } };
            CheckpointAddress? delivered = null;
            error = Assert.Throws<RepositoryPublicationException>(() => delivered = dev.CommitStateToShared(root, NoRebase));
            Assert.Same(injected, error.InnerException);
            Assert.Equal(visible ? RepositoryPublicationOutcome.Published : RepositoryPublicationOutcome.NotPublished, error.Outcome);
            Assert.True(repository.IsFaulted);
            Assert.Null(delivered);
            Assert.Equal(oldHead, dev.Head.RevisionAddress);
            Assert.Same(baseline, dev.Baseline);
            Assert.True(dev.UsesPrivateSchemas);
        }
        using Repository cold = Repository.OpenExisting(_path, Models());
        using BranchCheckout recovered = cold.CheckoutPrivate("dev");
        Assert.Equal(mainHead, cold.GetHead("main").RevisionAddress);
        Assert.Equal(visible ? error.CandidateRevisionAddress : oldHead, recovered.Head.RevisionAddress);
        Assert.Equal(visible, SchemaCatalogView.Load(cold.GraphStore, cold.SharedSchemaRoot!.Value).Schemas.ContainsKey(new(PrivateOnly.Type, 1)));
        Assert.Equal(cold.SharedSchemaRoot == oldShared, !visible);
        if (!visible) {
            AssertSameRef(oldBranches, ReadHead(cold, 1));
            AssertSameRef(shared, ReadHead(cold, 2));
        } else { Assert.Equal(cold.SharedSchemaRoot, cold.GraphStore.ReadMetadata(recovered.Head.RevisionAddress).SchemaRoot); }
        AssertRing(recovered.State, visible ? 402 : 7);
        AssertFramesPreserved(ResourcesOf(cold).Data, complete);
        if (stage == (int)RepositorySaveStage.AfterMetaAppend) { Assert.Null(error.CandidateRevisionAddress); }
        else { Assert.NotNull(error.CandidateRevisionAddress); }
    }

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
        StoredGraphRevision graph = store.Append(StoredGraphKind.Meta, null, null, prepared.Write!);
        prepared.PrepareInstall(graph);
        return prepared.View;
    }

    private sealed record RefImage(HeadEntryId Revision, byte[] Payload);
    private sealed record FrameImage(FrameAddress Address, uint Tag, int MetaLength, bool IsTombstone, byte[] Payload);
    private sealed record PublicationImage(RefImage Branches, RefImage Shared, long Appended, FrameImage[] Frames);
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
