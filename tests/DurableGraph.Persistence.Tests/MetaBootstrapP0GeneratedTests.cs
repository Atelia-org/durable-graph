using Atelia.Binary;
using Atelia.DurableGraph.Persistence.MetaBootstrapP0;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

// Uses actual generated business bodies and closes/reopens the real FS/VS resources.
// These are fresh-resource witnesses in one process, not process/page-cache cold measurements.
public sealed class MetaBootstrapP0GeneratedTests : IDisposable {
    private const string Prefix = "dg-bootstrap-generated-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));

    [Fact]
    public void GeneratedWidgetSharedListAndCycleColdReadAndContinueAgainstTheExactSavedState() {
        DurableSchema unknown = new("Unknown.Business.Owner", 1, [
            new(1, TypeTag.Int32),
            DurableFieldInfo.Reference(2, TypeExpr.Named("Unknown.Business.Target")),
            DurableFieldInfo.Reference(3, TypeExpr.List(TypeExpr.Builtin(TypeTag.Int32))),
        ]);
        DB087Widget original = Ring(1);
        FrameAddress state, schema;
        uint listId;
        using (var repository = RepositoryStorageFacade.CreateNew(_path)) {
            StateModelRegistry models = Models();
            var session = new BranchSaveSession(repository, "main", models);
            SetWidgetLayouts(session, models);
            session.Catalog.Set(unknown); // Neither unknown name has a registered business CLR model.
            using var save = session.Prepare(original);
            RepositorySaveResult saved = save.Commit();
            state = saved.Business.Address;
            schema = saved.Meta!.Address;
            Assert.Equal(3, saved.Business.ObjectHeads.Count); // Two Widgets and one shared List.
            Assert.Same(original, session.State);
            Assert.Equal(0, session.Catalog.DirtyCount);
            listId = LiveListId(repository, state, models);
            ObjectVersionChain rootChain = repository.Store.ReadObjectChain(state, saved.Business.RootId.Value);
            BareValueReader header = new(rootChain.Entries[0].Record.Body);
            Assert.Equal((byte)5, header.ReadByte());
            Assert.Equal((byte)1, header.ReadByte()); // The dynamic route points directly to a meta descriptor ObjectId.
            ObjectId descriptor = new(header.ReadVarUInt32());
            ObjectLayout widgetLayout = models.CreateEnvironment().ResolveCurrentModel(typeof(DB087Widget)).CurrentLayout;
            Assert.Equal(save.Catalog.View.DescriptorId(widgetLayout), descriptor);
            ObjectStateRecord descriptorRow = save.Catalog.View.Rows[descriptor];
            Assert.Equal(P0FixedBindings.CreateEnvironment().ResolveCurrentModel(typeof(P0DescriptorRecord)).CurrentLayout, descriptorRow.Layout);
            var locator = ObjectHeadDirectoryCodec.ToState(rootChain.Entries[0].ContainingBatch);
            Assert.Equal(rootChain.Entries[0].ContainingBatch, ObjectHeadDirectoryCodec.FromState(in locator));
        }

        using var reopened = RepositoryStorageFacade.OpenExisting(_path);
        SchemaCatalogView coldMeta = SchemaCatalogView.Load(reopened.Store, schema);
        Assert.Equal(unknown, coldMeta.Schemas[new(unknown.Type, unknown.Version)]);
        Assert.NotSame(unknown, coldMeta.Schemas[new(unknown.Type, unknown.Version)]);
        StateModelRegistry reopenedModels = Models();
        DB087Widget independent = AssertRing(BusinessPersistenceAdapter.ReadRoot(reopened.Store, state, reopenedModels), 1, 1);
        Assert.NotSame(original, independent);
        var continued = new BranchSaveSession(reopened, "main", reopenedModels);
        DB087Widget live = AssertRing(continued.State, 1, 1);
        Assert.NotSame(independent, live);
        Assert.NotSame(independent.Numbers, live.Numbers);
        live.Value = 2;
        live.Numbers[7] = 10008;
        using var next = continued.Prepare(live);
        Assert.Null(next.Catalog.Write);
        Assert.Equal(state, next.Business.Write.SourceGraph);
        RepositorySaveResult result = next.Commit();
        Assert.Equal(schema, reopened.SharedSchemaRoot);
        Assert.Equal(state, result.Business.SourceGraph);
        Assert.Equal(ObjectVersionKind.Delta, reopened.Store.ReadObjectChain(result.Business.Address, listId).Entries.Last().Record.Kind);
        DB087Widget latest = AssertRing(BusinessPersistenceAdapter.ReadRoot(reopened.Store, result.Business.Address, reopenedModels), 2, 1);
        Assert.Equal(10008, latest.Numbers[7]);
        DB087Widget old = AssertRing(BusinessPersistenceAdapter.ReadRoot(reopened.Store, state, reopenedModels), 1, 1);
        Assert.Equal(8, old.Numbers[7]);
    }

    [Fact]
    public void CatalogAppendWritesAnActualDescriptorListDeltaWithoutReencodingAcceptedDescriptors() {
        FrameAddress firstMeta, secondMeta;
        ObjectId listId;
        ObjectId[] oldDescriptors;
        using (var repository = RepositoryStorageFacade.CreateNew(_path)) {
            StateModelRegistry models = Models();
            var session = new BranchSaveSession(repository, "main", models);
            SetWidgetLayouts(session, models);
            for (int i = 0; i < 40; i++) { session.Catalog.Set(IntSchema($"Unknown.Seed{i:00}")); }
            DB087Widget root = Ring(1);
            using (var initial = session.Prepare(root)) { firstMeta = initial.Commit().Meta!.Address; }
            SchemaCatalogView baseline = session.Catalog.Accepted!;
            listId = baseline.DescriptorListId;
            oldDescriptors = baseline.DescriptorIds.ToArray();
            session.Catalog.Set(IntSchema("Unknown.Added"));
            using var update = session.Prepare(root);
            Assert.Equal(1, update.Catalog.AddedDescriptors);
            Assert.Equal(0, update.Catalog.EncodedAcceptedDescriptors);
            Assert.Empty(update.Catalog.EncodedIds.Intersect(oldDescriptors));
            Assert.Contains(listId, update.Catalog.EncodedIds);
            ObjectVersionRecord listWrite = Assert.Single(update.Catalog.Write!.Records, record => record.ObjectId == listId.Value);
            Assert.Equal(ObjectVersionKind.Delta, listWrite.Kind);
            secondMeta = update.Commit().Meta!.Address;
            ObjectVersionChain stored = repository.Store.ReadObjectChain(secondMeta, listId.Value);
            Assert.Equal(ObjectVersionKind.Delta, stored.Entries.Last().Record.Kind);
            Assert.Equal(baseline.Snapshot!.ObjectHeads[listId.Value], stored.Entries.Last().Record.PriorAddress);
        }

        using var reopened = RepositoryStorageFacade.OpenExisting(_path);
        SchemaCatalogView old = SchemaCatalogView.Load(reopened.Store, firstMeta);
        SchemaCatalogView current = SchemaCatalogView.Load(reopened.Store, secondMeta);
        Assert.Equal(oldDescriptors, old.DescriptorIds.ToArray());
        Assert.Equal(oldDescriptors.Length + 1, current.DescriptorIds.Count);
        Assert.Equal(IntSchema("Unknown.Added"), current.Schemas[new("Unknown.Added", 1)]);
        Assert.False(old.Schemas.ContainsKey(new("Unknown.Added", 1)));
        Assert.Equal(ObjectVersionKind.Delta, reopened.Store.ReadObjectChain(secondMeta, listId.Value).Entries.Last().Record.Kind);
    }

    [Fact]
    public void EventsAcceptMetaWhileTheirBusinessComparisonAndIdentityStayOnTheLastState() {
        FrameAddress state, event1, event2, lastState;
        using (var repository = RepositoryStorageFacade.CreateNew(_path)) {
            StateModelRegistry models = Models();
            var session = new BranchSaveSession(repository, "main", models);
            SetWidgetLayouts(session, models);
            DB087Widget root = Ring(1);
            using (var initial = session.Prepare(root)) { state = initial.Commit().Business.Address; }
            FrameAddress meta0 = repository.SharedSchemaRoot!.Value;

            session.Catalog.Set(IntSchema("Unknown.Event1"));
            root.Value = 2;
            root.Numbers[0] = 200;
            using (var save = session.Prepare(root, StoredGraphKind.Event)) {
                Assert.Equal(state, save.Business.Write.SourceGraph);
                RepositorySaveResult saved = save.Commit();
                event1 = saved.Business.Address;
                Assert.Equal(state, saved.Business.Parent);
                Assert.Equal(state, saved.Business.SourceGraph);
                Assert.NotEqual(meta0, repository.SharedSchemaRoot);
                Assert.Equal(saved.Meta!.Address, session.Catalog.Accepted!.Snapshot!.Address);
            }
            Assert.Equal(0, session.Catalog.DirtyCount);
            Assert.Equal(state, session.StateSource!.Address);
            Assert.Same(root, session.State);
            Assert.Equal(state, repository.GetStateHead("main"));

            FrameAddress meta1 = repository.SharedSchemaRoot!.Value;
            session.Catalog.Set(IntSchema("Unknown.Event2"));
            // Repeating E1's content must still compare against S0, not the preceding Event DTOs.
            using (var save = session.Prepare(root, StoredGraphKind.Event)) {
                Assert.Equal(state, save.Business.Write.SourceGraph);
                Assert.NotEmpty(save.Business.Write.Records);
                RepositorySaveResult saved = save.Commit();
                event2 = saved.Business.Address;
                Assert.Equal(event1, saved.Business.Parent);
                Assert.Equal(state, saved.Business.SourceGraph);
                Assert.NotEqual(meta1, repository.SharedSchemaRoot);
                Assert.Equal(saved.Meta!.Address, session.Catalog.Accepted!.Snapshot!.Address);
            }
            Assert.Equal(0, session.Catalog.DirtyCount);
            Assert.Equal(state, session.StateSource!.Address);
            Assert.Same(root, session.State);
            AssertRing(BusinessPersistenceAdapter.ReadRoot(repository.Store, event1, models), 2, 200);
            AssertRing(BusinessPersistenceAdapter.ReadRoot(repository.Store, event2, models), 2, 200);
            AssertRing(BusinessPersistenceAdapter.ReadRoot(repository.Store, state, models), 1, 1);

            root.Value = 3;
            root.Numbers[0] = 300;
            using var final = session.Prepare(root);
            Assert.Null(final.Catalog.Write);
            Assert.Equal(state, final.Business.Write.SourceGraph);
            RepositorySaveResult finalResult = final.Commit();
            lastState = finalResult.Business.Address;
            Assert.Equal(event2, finalResult.Business.Parent);
            Assert.Equal(state, finalResult.Business.SourceGraph);
            Assert.Equal(lastState, session.StateSource!.Address);
            Assert.Equal(lastState, repository.GetStateHead("main"));
        }

        using var reopened = RepositoryStorageFacade.OpenExisting(_path);
        Assert.Equal(lastState, reopened.GetStateHead("main"));
        var cold = new BranchSaveSession(reopened, "main", Models());
        AssertRing(cold.State, 3, 300);
        Assert.True(cold.Catalog.Accepted!.Schemas.ContainsKey(new("Unknown.Event1", 1)));
        Assert.True(cold.Catalog.Accepted.Schemas.ContainsKey(new("Unknown.Event2", 1)));
    }

    [Fact]
    public void DiscardRetainsLogicalDirtyKeysAndSameShapeNoChangeConsumesThemOnlyAfterCommit() {
        using var repository = RepositoryStorageFacade.CreateNew(_path);
        StateModelRegistry models = Models();
        var session = new BranchSaveSession(repository, "main", models);
        SetWidgetLayouts(session, models);
        DB087Widget root = Ring(1);
        using (var initial = session.Prepare(root)) { initial.Commit(); }
        FrameAddress baseline = repository.SharedSchemaRoot!.Value;
        long frames = repository.Store.AppendedFrames;
        DurableSchema wrong = new("Unknown.Repair", 1, new DurableFieldInfo(1, TypeTag.String));
        session.Catalog.Set(wrong);
        Dictionary<uint, byte[]> discardedBodies;
        using (var discarded = session.Prepare(root)) {
            Assert.Throws<InvalidOperationException>(() => session.Catalog.Set(IntSchema("Unknown.Blocked")));
            discardedBodies = discarded.Catalog.Write!.Records.ToDictionary(record => record.ObjectId, record => record.Body.ToArray());
            Assert.Equal(wrong, discarded.Catalog.View.Schemas[new(wrong.Type, wrong.Version)]);
        }
        Assert.False(repository.HasPending);
        Assert.Equal(frames, repository.Store.AppendedFrames);
        Assert.Equal(baseline, repository.SharedSchemaRoot);
        Assert.Equal(1, session.Catalog.DirtyCount);
        Assert.False(session.Catalog.Accepted!.Schemas.ContainsKey(new(wrong.Type, wrong.Version)));

        DurableSchema corrected = IntSchema("Unknown.Repair");
        session.Catalog.Set(corrected);
        using (var retry = session.Prepare(root)) {
            Assert.Equal(corrected, retry.Catalog.View.Schemas[new(corrected.Type, corrected.Version)]);
            Assert.Contains(retry.Catalog.Write!.Records, record => discardedBodies.TryGetValue(record.ObjectId, out byte[]? old)
                && !old.AsSpan().SequenceEqual(record.Body));
            retry.Commit();
        }
        Assert.Equal(0, session.Catalog.DirtyCount);
        FrameAddress accepted = repository.SharedSchemaRoot!.Value;
        session.Catalog.Set(IntSchema("Unknown.Repair"));
        Assert.Equal(1, session.Catalog.DirtyCount);
        FrameAddress directory = session.StateSource!.DirectoryRoot;
        long beforeNoChange = repository.Store.AppendedFrames;
        using (var sameShape = session.Prepare(root)) {
            Assert.Null(sameShape.Catalog.Write);
            Assert.Empty(sameShape.Catalog.EncodedIds);
            Assert.Empty(sameShape.Business.Write.Records);
            Assert.Equal(1, session.Catalog.DirtyCount);
            RepositorySaveResult saved = sameShape.Commit();
            Assert.Equal(accepted, saved.Meta!.Address);
            Assert.Equal(directory, saved.Business.DirectoryRoot);
            Assert.Null(saved.Business.OwnedBatch);
            Assert.Equal(beforeNoChange + 1, repository.Store.AppendedFrames);
        }
        Assert.Equal(0, session.Catalog.DirtyCount);
        Assert.Equal(accepted, repository.SharedSchemaRoot);
    }

    [Fact]
    public void DiscardedBranchCandidateRebasesSharedMetaAndReallocatesItsProvisionalDescriptorIds() {
        FrameAddress main, dev;
        using (var repository = RepositoryStorageFacade.CreateNew(_path)) {
            StateModelRegistry models = Models();
            var a = new BranchSaveSession(repository, "main", models);
            SetWidgetLayouts(a, models);
            DB087Widget aRoot = Ring(1), bRoot = Ring(90);
            using (var initial = a.Prepare(aRoot)) { initial.Commit(); }
            var b = new BranchSaveSession(repository, "dev", models);
            DurableSchema bSchema = IntSchema("Unknown.BranchB");
            b.Catalog.Set(bSchema);
            ObjectId provisional;
            using (var discarded = b.Prepare(bRoot)) {
                provisional = discarded.Catalog.View.DescriptorId(ObjectLayout.ForDurable(bSchema));
            }
            Assert.Null(repository.GetHead("dev"));
            Assert.Equal(1, b.Catalog.DirtyCount);

            DurableSchema aSchema = IntSchema("Unknown.BranchA");
            a.Catalog.Set(aSchema);
            ObjectId publishedA;
            FrameAddress metaA;
            using (var savedA = a.Prepare(aRoot)) {
                publishedA = savedA.Catalog.View.DescriptorId(ObjectLayout.ForDurable(aSchema));
                Assert.Equal(provisional, publishedA); // The discarded ID was provisional in the old exact view.
                RepositorySaveResult saved = savedA.Commit();
                main = saved.Business.Address;
                metaA = saved.Meta!.Address;
            }
            using (var rebased = b.Prepare(bRoot)) {
                Assert.Equal(metaA, rebased.Catalog.Write!.SourceGraph);
                ObjectId actualB = rebased.Catalog.View.DescriptorId(ObjectLayout.ForDurable(bSchema));
                Assert.NotEqual(provisional, actualB);
                Assert.Equal(publishedA, rebased.Catalog.View.DescriptorId(ObjectLayout.ForDurable(aSchema)));
                Assert.DoesNotContain(publishedA, rebased.Catalog.EncodedIds);
                Assert.Equal(0, rebased.Catalog.EncodedAcceptedDescriptors);
                dev = rebased.Commit().Business.Address;
            }
            Assert.Equal(0, b.Catalog.DirtyCount);
            Assert.Equal(main, repository.GetHead("main"));
            Assert.Equal(metaA, repository.Store.ReadGraph(main).SchemaRoot);
            Assert.Equal(repository.SharedSchemaRoot, repository.Store.ReadGraph(dev).SchemaRoot);
        }

        using var reopened = RepositoryStorageFacade.OpenExisting(_path);
        SchemaCatalogView shared = SchemaCatalogView.Load(reopened.Store, reopened.SharedSchemaRoot!.Value);
        Assert.Equal(IntSchema("Unknown.BranchA"), shared.Schemas[new("Unknown.BranchA", 1)]);
        Assert.Equal(IntSchema("Unknown.BranchB"), shared.Schemas[new("Unknown.BranchB", 1)]);
        AssertRing(BusinessPersistenceAdapter.ReadRoot(reopened.Store, main, Models()), 1, 1);
        AssertRing(BusinessPersistenceAdapter.ReadRoot(reopened.Store, dev, Models()), 90, 1);
    }

    [Fact]
    public void EventCaptureRemovesDetachedChildWithoutInstallingStateMembershipOrIdentity() {
        using var repository = RepositoryStorageFacade.CreateNew(_path);
        StateModelRegistry models = Models();
        var session = new BranchSaveSession(repository, "main", models);
        SetWidgetLayouts(session, models);
        DB087Widget root = Ring(1), child = root.Child!;
        StoredGraphRevision initial;
        using (var save = session.Prepare(root)) { initial = save.Commit().Business; }
        uint listId = LiveListId(repository, initial.Address, models);
        uint childId = Assert.Single(initial.ObjectHeads.Keys, id => id != initial.RootId.Value && id != listId);
        root.Child = null;
        StoredGraphRevision removed;
        using (var deletion = session.Prepare(root, StoredGraphKind.Event)) {
            Assert.Equal(initial.Address, deletion.Business.Write.SourceGraph);
            Assert.Equal(childId, Assert.Single(deletion.Business.Write.RemovedIds));
            removed = deletion.Commit().Business;
        }
        Assert.False(removed.ObjectHeads.ContainsKey(childId));
        Assert.Equal(initial.Address, session.StateSource!.Address);
        DB087Widget coldEvent = Assert.IsType<DB087Widget>(BusinessPersistenceAdapter.ReadRoot(repository.Store, removed.Address, models));
        Assert.Null(coldEvent.Child);
        Assert.Equal(1, coldEvent.Value);
        Assert.Equal(Enumerable.Range(1, 96).ToArray(), coldEvent.Numbers.ToArray());

        root.Child = child; // The same CLR child remains identified by the unadvanced S0 baseline.
        using var restored = session.Prepare(root);
        Assert.Equal(initial.Address, restored.Business.Write.SourceGraph);
        Assert.Empty(restored.Business.Write.RemovedIds);
        Assert.Empty(restored.Business.Write.Records);
        StoredGraphRevision saved = restored.Commit().Business;
        Assert.Equal(removed.Address, saved.Parent);
        Assert.Equal(initial.Address, saved.SourceGraph);
        Assert.Equal(initial.DirectoryRoot, saved.DirectoryRoot);
        Assert.Null(saved.OwnedBatch);
        Assert.Equal(initial.ObjectHeads[childId], saved.ObjectHeads[childId]);
        AssertRing(BusinessPersistenceAdapter.ReadRoot(repository.Store, saved.Address, models), 1, 1);
        AssertRing(BusinessPersistenceAdapter.ReadRoot(repository.Store, initial.Address, models), 1, 1);
    }

    private static StateModelRegistry Models() {
        StateModelRegistry models = new();
        models.Register(global::Atelia.DurableGraph.Generated.Family_5030576964676574.Definition);
        return models;
    }

    private static void SetWidgetLayouts(BranchSaveSession session, StateModelRegistry models) {
        ModelExecutionEnvironment snapshot = models.CreateEnvironment();
        session.Catalog.Set(snapshot.ResolveCurrentModel(typeof(DB087Widget)).CurrentSchema);
        Assert.True(snapshot.TryGetCurrentObjectBinding(typeof(List<int>), out ObjectBinding? list));
        session.Catalog.Set(list!.CurrentLayout);
    }

    private static DurableSchema IntSchema(string name) => new(name, 1, new DurableFieldInfo(1, TypeTag.Int32));

    private static DB087Widget Ring(int value) {
        List<int> numbers = Enumerable.Range(1, 96).ToList();
        DB087Widget root = new() { Value = value, Numbers = numbers };
        DB087Widget child = new() { Value = value + 10, Numbers = numbers, Child = root };
        root.Child = child;
        return root;
    }

    private static DB087Widget AssertRing(IDurableObject? instance, int value, int firstNumber) {
        DB087Widget root = Assert.IsType<DB087Widget>(instance);
        Assert.Equal(value, root.Value);
        Assert.NotNull(root.Child);
        Assert.Same(root, root.Child!.Child);
        Assert.Same(root.Numbers, root.Child.Numbers);
        Assert.Equal(96, root.Numbers.Count);
        Assert.Equal(firstNumber, root.Numbers[0]);
        return root;
    }

    private static uint LiveListId(RepositoryStorageFacade repository, FrameAddress graph, StateModelRegistry models) {
        StoredGraphRevision saved = repository.Store.ReadGraph(graph);
        SchemaCatalogView catalog = SchemaCatalogView.Load(repository.Store, saved.SchemaRoot!.Value);
        DecodedGraphRows decoded = StoredGraphDecoder.Read(repository.Store, graph, models.CreateEnvironment(), catalog);
        return Assert.Single(decoded.Rows.Values, row => row.Kind == ObjectStateKind.List).Id.Value;
    }

    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}
