using System.Buffers;
using Atelia.Binary;

using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

// Hand-written bindings isolate restoration and publication boundaries. Generated
// V1/V2 body/history qualification is covered by the separate vertical witnesses.
public sealed class MetaBootstrapUpgradeBoundaryTests : IDisposable {
    private const string Prefix = "dg-bootstrap-upgrade-boundary-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
    private static readonly DurableSchema Old = Schema(1);
    private static readonly DurableSchema Current = Schema(2);

    [Fact]
    public async Task Independent_repositories_can_encode_read_and_upgrade_meta_in_parallel_without_sharing_closure_state() {
        const int workers = 4, iterations = 4;
        Directory.CreateDirectory(_path);
        using Barrier start = new(workers);
        await Task.WhenAll(Enumerable.Range(0, workers).Select(worker => Task.Run(() => {
            // Each task owns its resources and performs every operation synchronously.
            // Concurrency is between repositories, never within one publication owner.
            for (int iteration = 0; iteration < iterations; iteration++) {
                string path = Path.Combine(_path, $"worker-{worker}-iteration-{iteration}");
                string name = $"DB087.Worker{worker}.Iteration{iteration}";
                DurableSchema seedOnly = new(name + ".Seed", 1, new DurableFieldInfo(1, TypeTag.Int32));
                FrameAddress state, schema;
                using (var repository = RepositoryStorageFacade.CreateNew(path)) {
                    if (iteration == 0) { Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(20))); }
                    var source = AppendSeed(repository, textId: 100, additionalSchemas: [seedOnly]);
                    state = source.State;
                    schema = source.Schema;
                }
                using (var reader = RepositoryStorageFacade.OpenReadOnlyExisting(path)) {
                    SchemaCatalogView meta = SchemaCatalogView.Load(reader.Store, schema);
                    Assert.Equal(seedOnly, meta.Schemas[new(seedOnly.Type, 1)]);
                    Assert.Equal(2, meta.Schemas.Count);
                    Assert.False(meta.Schemas.ContainsKey(new(Current.Type, 2)));
                    BoundaryRoot root = Assert.IsType<BoundaryRoot>(BusinessPersistenceAdapter.ReadRoot(reader.Store,
                        MechanismFixture.Snapshot(reader, state), Registry(Model(Current, Old, cutText: true))));
                    Assert.Equal((byte)1, root.Generation);
                    Assert.Null(root.Text);
                    Assert.Equal(schema, reader.SharedSchemaRoot);
                }
                FrameAddress current;
                using (var repository = RepositoryStorageFacade.OpenExisting(path)) {
                    var session = new BranchSaveSessionFixture(repository, "main", Registry(Model(Current, Old, cutText: true)));
                    BoundaryRoot root = Assert.IsType<BoundaryRoot>(session.State);
                    for (int mutation = 0; mutation < 2; mutation++) {
                        root.Value++;
                        using var save = session.Prepare(root, declarations: [
                            new DurableSchema(name + $".Added{mutation}", 1, new DurableFieldInfo(1, TypeTag.Byte)),
                        ]);
                        current = save.Commit().Business.Address;
                        Assert.Equal(current, session.StateSource!.Address);
                        SchemaCatalogView accepted = SchemaCatalogView.Load(repository.Store, repository.SharedSchemaRoot!.Value);
                        Assert.Equal(4 + mutation, accepted.Schemas.Count); // V1, V2, seed and this owner's additions.
                        Assert.True(accepted.Schemas.ContainsKey(new(name + $".Added{mutation}", 1)));
                        Assert.Equal(schema, repository.Store.ReadMetadata(state).SchemaRoot);
                    }
                    current = MechanismFixture.Current(repository, "main").Address;
                }
                using (var reader = RepositoryStorageFacade.OpenReadOnlyExisting(path)) {
                    StateModelRegistry models = Registry(Model(Current, Old, cutText: true));
                    BoundaryRoot latest = Assert.IsType<BoundaryRoot>(BusinessPersistenceAdapter.ReadRoot(reader.Store, MechanismFixture.Snapshot(reader, current), models));
                    BoundaryRoot historical = Assert.IsType<BoundaryRoot>(BusinessPersistenceAdapter.ReadRoot(reader.Store, MechanismFixture.Snapshot(reader, state), models));
                    Assert.Equal((byte)7, latest.Value);
                    Assert.Equal((byte)5, historical.Value);
                    Assert.Equal((byte)1, latest.Generation);
                    Assert.Equal((byte)1, historical.Generation);
                    Assert.Equal(5, SchemaCatalogView.Load(reader.Store, reader.SharedSchemaRoot!.Value).Schemas.Count);
                    Assert.Equal(2, SchemaCatalogView.Load(reader.Store, schema).Schemas.Count);
                }
            }
        })));
    }

    [Fact]
    public void Capture_cannot_reenter_the_same_publication_owner_through_another_session() {
        using var repository = RepositoryStorageFacade.CreateNew(_path);
        BranchSaveSessionFixture? inner = null;
        bool attemptReentry = true;
        int rejected = 0;
        StateModelRegistry models = Registry(Model(Old, Old, onCapture: () => {
            if (!attemptReentry) { return; }
            Assert.Throws<InvalidOperationException>(() => {
                using var nested = inner!.Prepare(new BoundaryRoot { Value = 90 });
                nested.Commit();
            });
            rejected++;
        }));
        var outer = new BranchSaveSessionFixture(repository, "main", models);
        inner = new BranchSaveSessionFixture(repository, "other", models);

        BoundaryRoot root = new() { Value = 7 };
        using (var save = outer.Prepare(root)) {
            Assert.Equal(1, rejected);
            Assert.Null(MechanismFixture.FindCurrent(repository, "main")?.Address);
            Assert.Null(MechanismFixture.FindCurrent(repository, "other")?.Address);
            RepositorySaveResult result = save.Commit();
            Assert.Equal(result.Business.Address, MechanismFixture.FindCurrent(repository, "main")?.Address);
            Assert.Null(MechanismFixture.FindCurrent(repository, "other")?.Address);
            Assert.Same(root, outer.State);
        }
        Assert.False(repository.HasPending);
        Assert.False(repository.IsFaulted);

        attemptReentry = false;
        using var later = inner.Prepare(new BoundaryRoot { Value = 11 });
        Assert.Equal(later.Commit().Business.Address, MechanismFixture.FindCurrent(repository, "other")?.Address);
        Assert.Equal((byte)7, Assert.IsType<BoundaryRoot>(BusinessPersistenceAdapter.ReadRoot(repository.Store,
            MechanismFixture.Snapshot(repository, MechanismFixture.Current(repository, "main").Address), models)).Value);
    }

    [Fact]
    public void A_source_schema_with_the_same_key_but_another_shape_fails_before_body_or_upgrade_callbacks() {
        FrameAddress badGraph;
        DurableSchema wrongSource = Schema(1, wideValue: true);
        using (var repository = RepositoryStorageFacade.CreateNew(_path)) {
            AppendSeed(repository);
            SchemaCatalogWorkspace wrong = new(static () => { });
            wrong.Set(wrongSource);
            PreparedSchemaCatalogWrite meta = wrong.Prepare();
            StoredGraphRevision schema = repository.Store.AppendMeta(null, meta.Write!);
            byte[] body = TypedBaseBodyCodec.Encode(ObjectLayout.ForDurable(wrongSource), Base(new(5, default, 0)), meta.View);
            badGraph = repository.Store.AppendBusiness(StoredGraphKind.State, null, schema.Address,
                new(null, new(1), [ObjectVersionRecord.CreateBase(1, body)])).Address;
            repository.Data.ConfirmDurable();
        }

        int reads = 0, upgrades = 0;
        Dictionary<string, byte[]> before = Files();
        using (var reader = RepositoryStorageFacade.OpenReadOnlyExisting(_path)) {
            StateModelRegistry models = Registry(Model(Current, Old,
                onRead: () => reads++, onUpgrade: () => upgrades++));
            Assert.Throws<InvalidDataException>(() => BusinessPersistenceAdapter.ReadRoot(reader.Store, reader.Store.ReadBusinessGraph(badGraph, null), models));
            Assert.Equal(0, reads);
            Assert.Equal(0, upgrades);
            Assert.Equal(Old, SchemaCatalogView.Load(reader.Store, reader.SharedSchemaRoot!.Value).Schemas[new(Old.Type, 1)]);
        }
        AssertFilesUnchanged(before);
    }

    [Fact]
    public void A_registered_target_endpoint_with_another_shape_fails_before_upgrade_callbacks() {
        var source = Seed(additionalSchemas: [Schema(2, wideValue: true)]);
        int upgrades = 0;
        Dictionary<string, byte[]> before = Files();
        using (var reader = RepositoryStorageFacade.OpenReadOnlyExisting(_path)) {
            StateModelRegistry models = Registry(Model(Current, Old, onUpgrade: () => upgrades++));
            Assert.Throws<InvalidDataException>(() => BusinessPersistenceAdapter.ReadRoot(reader.Store, MechanismFixture.Snapshot(reader, source.State), models));
            Assert.Equal(0, upgrades);
            Assert.Equal(source.Schema, reader.SharedSchemaRoot);
            Assert.Equal(source.State, MechanismFixture.FindCurrent(reader, "main")?.Address);
        }
        AssertFilesUnchanged(before);
    }

    [Fact]
    public void A_registered_inline_dependency_conflict_is_rejected_even_when_the_target_owner_is_absent() {
        DurableSchema point = new("DB087.BoundaryPoint", 1, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.Byte));
        DurableSchema target = new(Current.Type, 2, [
            new(1, TypeTag.Byte), new(2, TypeTag.String), new(3, TypeTag.InlineValue, inlineSchema: point),
        ]);
        // The persisted catalog is valid by itself. Its nominal key conflicts with
        // an exact inline requirement introduced only by the current model.
        DurableSchema conflictingPoint = new(point.Type, 1, new DurableFieldInfo(1, TypeTag.UInt32));
        var source = Seed(additionalSchemas: [conflictingPoint]);
        int upgrades = 0;
        using var reader = RepositoryStorageFacade.OpenReadOnlyExisting(_path);
        SchemaCatalogView stored = SchemaCatalogView.Load(reader.Store, source.Schema);
        Assert.False(stored.Schemas.ContainsKey(new(target.Type, target.Version)));
        Assert.Equal(SchemaKind.ReferenceObject, stored.Schemas[new(point.Type, 1)].Kind);
        StateModelRegistry models = Registry(Model(target, Old, onUpgrade: () => upgrades++));
        Assert.Throws<InvalidDataException>(() => BusinessPersistenceAdapter.ReadRoot(reader.Store, MechanismFixture.Snapshot(reader, source.State), models));
        Assert.Equal(0, upgrades);
        Assert.Equal(source.Schema, reader.SharedSchemaRoot);
    }

    [Fact]
    public void Read_only_upgrade_without_a_persisted_target_keeps_every_file_and_both_ref_revisions_unchanged() {
        var source = Seed();
        Dictionary<string, byte[]> before = Files();
        int upgrades = 0;
        using (var reader = RepositoryStorageFacade.OpenReadOnlyExisting(_path)) {
            var branches = reader.Heads.ReadHead(1).Unwrap();
            var shared = reader.Heads.ReadHead(2).Unwrap();
            StateModelRegistry models = Registry(Model(Current, Old, onUpgrade: () => upgrades++));
            BoundaryRoot restored = Assert.IsType<BoundaryRoot>(BusinessPersistenceAdapter.ReadRoot(reader.Store, MechanismFixture.Snapshot(reader, source.State), models));
            Assert.Equal((byte)5, restored.Value);
            Assert.Equal((byte)1, restored.Generation);
            Assert.Equal(1, upgrades);
            SchemaCatalogView stillStored = SchemaCatalogView.Load(reader.Store, source.Schema);
            Assert.Single(stillStored.Schemas);
            Assert.False(stillStored.Schemas.ContainsKey(new(Current.Type, Current.Version)));
            Assert.Equal(branches.Id, reader.Heads.ReadHead(1).Unwrap().Id);
            Assert.Equal(shared.Id, reader.Heads.ReadHead(2).Unwrap().Id);
            Assert.Equal(branches.Payload.ToArray(), reader.Heads.ReadHead(1).Unwrap().Payload.ToArray());
            Assert.Equal(shared.Payload.ToArray(), reader.Heads.ReadHead(2).Unwrap().Payload.ToArray());
            Assert.Equal(source.Schema, reader.SharedSchemaRoot);
            Assert.Equal(source.State, MechanismFixture.FindCurrent(reader, "main")?.Address);
        }
        AssertFilesUnchanged(before);
    }

    [Fact]
    public void Missing_source_descriptor_cannot_be_supplied_by_the_latest_shared_catalog() {
        FrameAddress malformed;
        FrameAddress acceptedSchema;
        using (var repository = RepositoryStorageFacade.CreateNew(_path)) {
            var source = AppendSeed(repository);
            acceptedSchema = source.Schema;
            SchemaCatalogView accepted = SchemaCatalogView.Load(repository.Store, acceptedSchema);
            PreparedSchemaCatalogWrite empty = new SchemaCatalogWorkspace(static () => { }).Prepare();
            StoredGraphRevision missing = repository.Store.AppendMeta(null, empty.Write!);
            byte[] body = TypedBaseBodyCodec.Encode(ObjectLayout.ForDurable(Old), Base(new(5, default, 0)), accepted);
            malformed = repository.Store.AppendBusiness(StoredGraphKind.State, null, missing.Address,
                new(null, new(1), [ObjectVersionRecord.CreateBase(1, body)])).Address;
            repository.Data.ConfirmDurable();
        }
        int reads = 0, upgrades = 0;
        using var reader = RepositoryStorageFacade.OpenReadOnlyExisting(_path);
        Assert.Equal(acceptedSchema, reader.SharedSchemaRoot);
        Assert.Equal(Old, SchemaCatalogView.Load(reader.Store, acceptedSchema).Schemas[new(Old.Type, 1)]);
        StateModelRegistry models = Registry(Model(Current, Old,
            onRead: () => reads++, onUpgrade: () => upgrades++));
        Assert.Throws<InvalidDataException>(() => BusinessPersistenceAdapter.ReadRoot(reader.Store, reader.Store.ReadBusinessGraph(malformed, null), models));
        Assert.Equal(0, reads);
        Assert.Equal(0, upgrades);
    }

    [Fact]
    public void Cold_upgrade_rewrite_and_full_source_id_cursor_survive_event_and_discard_until_state_is_confirmed() {
        var source = Seed(textId: 100);
        using var repository = RepositoryStorageFacade.OpenExisting(_path);
        var session = new BranchSaveSessionFixture(repository, "main", Registry(Model(Current, Old, cutText: true)));
        BoundaryRoot root = Assert.IsType<BoundaryRoot>(session.State);
        Assert.Null(root.Text);
        Assert.Equal((byte)1, root.Generation);
        Assert.Equal(source.State, session.StateSource!.Address);

        RepositorySaveResult eventResult;
        using (var saveEvent = session.Prepare(root, StoredGraphKind.Event)) {
            AssertRewrite(saveEvent, source.State);
            Assert.NotNull(saveEvent.Catalog.Write); // V2 was not published by opening or upgrading.
            eventResult = saveEvent.Commit();
        }
        Assert.Equal(eventResult.Business.Address, MechanismFixture.FindCurrent(repository, "main")?.Address);
        Assert.Equal(source.State, session.StateSource!.Address);
        Assert.Equal(eventResult.Business.SchemaRoot, session.AcceptedCatalog!.Snapshot!.Address);
        Assert.True(session.AcceptedCatalog.Schemas.ContainsKey(new(Current.Type, 2)));
        Assert.Same(root, session.State);

        using (var discarded = session.Prepare(root)) {
            AssertRewrite(discarded, source.State);
            Assert.Null(discarded.Catalog.Write); // Confirmed Event accepted only the meta candidate.
        }
        Assert.Equal(eventResult.Business.Address, MechanismFixture.FindCurrent(repository, "main")?.Address);
        Assert.Equal(source.State, session.StateSource!.Address);
        Assert.False(repository.HasPending);

        RepositorySaveResult state;
        using (var retry = session.Prepare(root)) {
            AssertRewrite(retry, source.State);
            state = retry.Commit();
        }
        Assert.Equal(state.Business.Address, session.StateSource!.Address);
        Assert.Equal(new uint[] { 1 }, state.Business.ObjectHeads.Keys.ToArray());
        using (var unchanged = session.Prepare(root)) {
            Assert.Empty(unchanged.Business.Write.Records);
            Assert.Empty(unchanged.Business.Write.RemovedIds);
        }
        root.Text = "new text after the upgrade cut its old incoming edge";
        using var newObject = session.Prepare(root);
        uint newId = Assert.Single(newObject.Business.Write.Records, record => record.ObjectId != 1).ObjectId;
        Assert.True(newId > 100); // Complete source membership, not current reachability, owns the cursor.
        Assert.Equal(source.Schema, repository.Store.ReadMetadata(source.State).SchemaRoot);
        Assert.Contains(100u, MechanismFixture.Snapshot(repository, source.State).ObjectHeads.Keys);
    }

    [Fact]
    public void A_cold_workspace_rechecks_latest_shared_target_conflicts_before_any_save_append() {
        var source = Seed();
        using var repository = RepositoryStorageFacade.OpenExisting(_path);
        var first = new BranchSaveSessionFixture(repository, "main", Registry(Model(Current, Old)));
        BoundaryRoot upgraded = Assert.IsType<BoundaryRoot>(first.State);
        var other = new BranchSaveSessionFixture(repository, "other", Registry(Model(Old, Old)));
        using (var publishConflict = other.Prepare(new BoundaryRoot { Value = 9 }, declarations: [Schema(2, wideValue: true)])) {
            publishConflict.Commit();
        }
        FrameAddress shared = repository.SharedSchemaRoot!.Value;
        long appended = repository.Store.AppendedFrames;
        Assert.Throws<SchemaConflictException>(() => first.Prepare(upgraded));
        Assert.Equal(appended, repository.Store.AppendedFrames);
        Assert.Equal(source.State, MechanismFixture.FindCurrent(repository, "main")?.Address);
        Assert.Equal(source.State, first.StateSource!.Address);
        Assert.Equal(shared, repository.SharedSchemaRoot);
        Assert.False(repository.HasPending);
        Assert.False(repository.IsFaulted);
    }

    private static void AssertRewrite(PreparedFixtureSave save, FrameAddress source) {
        Assert.Equal(source, save.Business.Write.SourceGraph);
        ObjectVersionRecord record = Assert.Single(save.Business.Write.Records);
        Assert.Equal(1u, record.ObjectId);
        Assert.Equal(ObjectVersionKind.Base, record.Kind);
        Assert.Equal(new uint[] { 100 }, save.Business.Write.RemovedIds.ToArray());
    }

    private (FrameAddress State, FrameAddress Schema) Seed(uint textId = 0, DurableSchema[]? additionalSchemas = null) {
        using var repository = RepositoryStorageFacade.CreateNew(_path);
        return AppendSeed(repository, textId, additionalSchemas);
    }

    private static (FrameAddress State, FrameAddress Schema) AppendSeed(RepositoryStorageFacade repository,
        uint textId = 0, DurableSchema[]? additionalSchemas = null) {
        SchemaCatalogWorkspace catalog = new(static () => { });
        catalog.Set(Old);
        foreach (DurableSchema schema in additionalSchemas ?? []) { catalog.Set(schema); }
        PreparedSchemaCatalogWrite meta = catalog.Prepare();
        List<ObjectVersionRecord> records = [ObjectVersionRecord.CreateBase(1,
            TypedBaseBodyCodec.Encode(ObjectLayout.ForDurable(Old), Base(new(5, new(textId), 0)), meta.View))];
        if (textId != 0) {
            records.Add(ObjectVersionRecord.CreateBase(textId,
                TypedBaseBodyCodec.Encode(ObjectLayout.String, StringBodyPreparer.PrepareBase("old source string"))));
        }
        using PendingRepositorySave pending = MechanismFixture.Prepare(repository, "main", StoredGraphKind.State,
            new(null, new(1), records), meta.Write);
        RepositorySaveResult saved = repository.Commit(pending);
        return (saved.Business.Address, saved.Meta!.Address);
    }

    private static DurableSchema Schema(int version, bool wideValue = false) => new("DB087.UpgradeBoundary", version,
        new DurableFieldInfo(1, wideValue ? TypeTag.UInt32 : TypeTag.Byte), new DurableFieldInfo(2, TypeTag.String),
        new DurableFieldInfo(3, TypeTag.Byte));

    private static StateModelRegistry Registry(StateModelBinding model) {
        StateModelRegistry registry = new();
        registry.Register(model);
        return registry;
    }

    private static StateModelBinding Model(DurableSchema current, DurableSchema old, Action? onRead = null,
        Action? onUpgrade = null, Action? onCapture = null, bool cutText = false) {
        StatePreparationBinding<BoundaryState> preparation = new(current,
            static (in BoundaryState state) => Base(state),
            static (in BoundaryState prior, in BoundaryState next) => new(prior != next, Base(next).Body));
        StateReaderBinding<BoundaryState> Reader(DurableSchema schema) => new(schema,
            (ref BareValueReader reader) => { onRead?.Invoke(); return Read(ref reader); },
            static (ref BareValueReader reader, in BoundaryState prior) => Read(ref reader), Visit);
        StateReaderBinding[] readers = old.Equals(current) ? [Reader(current)] : [Reader(old), Reader(current)];
        return new StateModelBinding<BoundaryRoot, BoundaryState>(preparation, readers,
            row => {
                onUpgrade?.Invoke();
                BoundaryState state = row.GetState<BoundaryState>();
                return state with { Generation = checked((byte)(state.Generation + 1)), TextId = cutText ? default : state.TextId };
            },
            static () => new BoundaryRoot(),
            static (BoundaryRoot root, in BoundaryState state, ObjectReadTable objects) => {
                root.Value = state.Value;
                root.Generation = state.Generation;
                root.Text = objects.ResolveString(state.TextId);
            },
            (root, context) => { onCapture?.Invoke(); return new(root.Value, context.CaptureString(root.Text), root.Generation); }, Visit);
    }

    private static PreparedBaseBody Base(BoundaryState state) {
        ArrayBufferWriter<byte> buffer = new();
        BareValueWriter writer = new(buffer);
        writer.WriteByte(state.Value);
        writer.WriteVarUInt32(state.TextId.Value);
        writer.WriteByte(state.Generation);
        return new(buffer.WrittenSpan);
    }

    private static BoundaryState Read(ref BareValueReader reader) =>
        new(reader.ReadByte(), new(reader.ReadVarUInt32()), reader.ReadByte());

    private static void Visit(in BoundaryState state, IStateReferenceVisitor visitor) => visitor.VisitString(state.TextId);

    private Dictionary<string, byte[]> Files() => Directory.EnumerateFiles(_path, "*", SearchOption.AllDirectories)
        .ToDictionary(file => Path.GetRelativePath(_path, file), File.ReadAllBytes, StringComparer.Ordinal);

    private void AssertFilesUnchanged(IReadOnlyDictionary<string, byte[]> before) {
        Dictionary<string, byte[]> after = Files();
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        foreach (var file in before) { Assert.Equal(file.Value, after[file.Key]); }
    }

    private sealed class BoundaryRoot : IDurableObject {
        internal byte Value;
        internal byte Generation;
        internal string? Text;
    }

    private readonly record struct BoundaryState(byte Value, ObjectId TextId, byte Generation);

    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}
