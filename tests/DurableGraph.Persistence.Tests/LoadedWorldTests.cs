
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Buffers;
using System.Runtime.CompilerServices;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

// Restore/save invariants survive the retired LoadedWorld facade through the
// current split reader, shared GraphReader, and the actual repository save owner.
public sealed class LoadedWorldTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"durable-graph-loaded-world-{Guid.NewGuid():N}");
    private readonly RepositoryStorageFacade _repository;
    private GraphRecordStore Store => _repository.Store;
    // A real stable scalar makes sparse field updates smaller than a complete Base.
    private const ulong InitialSequence = 0xFEDC_BA98_7654_3210UL;
    private static readonly DurableSchema Old = Schema("World", 1);
    private static readonly DurableSchema Current = Schema("World", 2);
    private static readonly ReadAmplificationBaseBudgetParameters NoRebase = new(1000000, 1);

    public LoadedWorldTests() => _repository = RepositoryStorageFacade.CreateNew(_root);

    [Fact]
    public void NormalizationFollowsCompleteDeltaChainAndRetainsSourceRowsWithoutWriting() {
        FrameAddress first = Seed(Old, new(2, 8, 0), Text(8, "removed by upgrade"));
        FrameAddress firstHead = MechanismFixture.Snapshot(_repository, first).ObjectHeads[1];
        FrameAddress second = Append(first,
            _ => [ObjectVersionRecord.CreateDelta(1, firstHead, Delta(new(2, 8, 0), new(3, 8, 0)).Body)]);
        FrameAddress secondHead = MechanismFixture.Snapshot(_repository, second).ObjectHeads[1];
        FrameAddress third = Append(second,
            _ => [ObjectVersionRecord.CreateDelta(1, secondHead, Delta(new(3, 8, 0), new(4, 8, 0, InitialSequence - 1)).Body)]);
        StateModelRegistry models = Registry(Model(upgrade: state => state with { Value = (byte)(state.Value + 10), TextId = new ObjectId(0) }));
        DecodedRevision decoded = DecodedRevision.ReadStoredGraph(Store, MechanismFixture.Snapshot(_repository, third), (models).FreezeConfiguration());
        long tail = Tail();
        FrameAddress? shared = _repository.SharedSchemaRoot;
        NormalizedGraphState normalized = NormalizedGraphState.Create(decoded, decoded.SourceModels!);
        Assert.Equal(new State(4, 8, 0, InitialSequence - 1), decoded.GetRequired(new ObjectId(1)).GetState<State>());
        Assert.Equal(new State(14, 0, 0, InitialSequence - 1), normalized.Objects[new ObjectId(1)].Current.GetState<State>());
        Assert.True(normalized.Objects[new ObjectId(1)].RequiresRewrite);
        Assert.Equal(Old, normalized.Objects[new ObjectId(1)].SourceSchema);
        Assert.Equal(Current, normalized.Objects[new ObjectId(1)].Current.Schema);
        Assert.Contains(new ObjectId(8), normalized.Objects.Keys);
        Assert.Same(decoded.GetRequired(new ObjectId(8)).StringContent, normalized.Objects[new ObjectId(8)].Current.StringContent);
        Assert.Equal(3, Store.ReadObjectChain(MechanismFixture.Snapshot(_repository, third), 1).Entries.Count);
        Assert.Equal(shared, _repository.SharedSchemaRoot);
        Assert.Equal(tail, Tail());
    }

    [Fact]
    public void ReadOnlyUpgradeUsesThePinnedSourceWithoutAppendingOrRegisteringTheTarget() {
        FrameAddress address = Seed(Old, new(5, 8, 0), Text(8, "old"));
        FrameAddress schema = _repository.SharedSchemaRoot!.Value;
        _repository.Dispose();
        Dictionary<string, byte[]> before = Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(_root, path), File.ReadAllBytes, StringComparer.Ordinal);
        using (RepositoryStorageFacade reader = RepositoryStorageFacade.OpenReadOnlyExisting(_root)) {
            DecodedRevision decoded = DecodedRevision.ReadStoredGraph(reader.Store, MechanismFixture.Snapshot(reader, address), (Registry(Model(upgrade: state => state with { Value = 6, TextId = default }))).FreezeConfiguration());
            World world = GraphReader.ReadRoot<World>(decoded, new ObjectId(1), new(decoded.SourceModels!));
            Assert.Equal((byte)6, world.Value);
            Assert.Null(world.Text);
            Assert.Equal(schema, decoded.SelectedSchemaView!.Snapshot!.Address);
            Assert.DoesNotContain(new SchemaKey(Current.Type, Current.Version), decoded.SelectedSchemaView.Schemas.Keys);
            Assert.Equal(schema, reader.SharedSchemaRoot);
            Assert.Equal(address, MechanismFixture.FindCurrent(reader, "main")?.Address);
        }
        Dictionary<string, byte[]> after = Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(_root, path), File.ReadAllBytes, StringComparer.Ordinal);
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        foreach (var file in before) { Assert.Equal(file.Value, after[file.Key]); }
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(99u)]
    public void InvalidUpgradeReferenceFailsBeforeWorldDeliveryAndDoesNotWrite(uint badId) {
        FrameAddress address = Seed(Old, new(1, 0, 0));
        StateModelRegistry models = Registry(Model(upgrade: state => state with { TextId = new ObjectId(badId) }));
        long tail = Tail();
        FrameAddress? shared = _repository.SharedSchemaRoot;
        World? delivered = null;
        Assert.Throws<InvalidDataException>(() => delivered = ReadWorld(address, models));
        Assert.Null(delivered);
        Assert.Equal(shared, _repository.SharedSchemaRoot);
        Assert.Equal(tail, Tail());
    }

    [Fact]
    public void CurrentRowsAreNotUpgradedAndLateUnknownSourceFamilyStillFails() {
        FrameAddress address = Seed(Current, new(7, 0, 0));
        StateModelRegistry models = Registry(Model(upgrade: _ => throw new Exception("must not run")));
        BranchSaveSessionFixture session = new(_repository, "main", models);
        World world = Assert.IsType<World>(session.State);
        Assert.Equal((byte)7, world.Value);
        using (PreparedFixtureSave unchanged = session.Prepare(world, parameters: NoRebase)) {
            Assert.Empty(unchanged.Business.Write.Records);
        }
        DurableSchema unknown = Schema("Unknown", 1);
        FrameAddress bad = Append(address, catalog => [Durable(99, unknown, new(1, 0, 0), catalog)], unknown);
        Assert.Throws<InvalidDataException>(() => ReadWorld(bad, models));
    }

    [Fact]
    public void EmptyAliasKeepsOriginalBaselineSlotUntilRealDeltaAndRemoveArePersisted() {
        FrameAddress address = Seed(Current, new(7, 9, 0), Text(3, ""), Text(9, ""));
        StateModelRegistry models = Registry(Model());
        BranchSaveSessionFixture session = new(_repository, "main", models);
        World world = Assert.IsType<World>(session.State);
        Assert.Same(string.Empty, world.Text);
        FrameAddress next;
        using (PreparedFixtureSave save = session.Prepare(world, parameters: NoRebase)) {
            ObjectVersionRecord write = Assert.Single(save.Business.Write.Records);
            Assert.Equal(ObjectVersionKind.Delta, write.Kind);
            Assert.Equal(1u, write.ObjectId);
            Assert.Equal(new uint[] { 9 }, save.Business.Write.RemovedIds);
            next = save.Commit().Business.Address;
        }
        DecodedRevision decoded = DecodedRevision.ReadStoredGraph(Store, MechanismFixture.Snapshot(_repository, next), (models).FreezeConfiguration());
        Assert.Equal(new ObjectId(3), decoded.GetRequired(new ObjectId(1)).GetState<State>().TextId);
        Assert.Equal(new ObjectId(9), DecodedRevision.ReadStoredGraph(Store, MechanismFixture.Snapshot(_repository, address), (models).FreezeConfiguration()).GetRequired(new ObjectId(1)).GetState<State>().TextId);
        Assert.Same(string.Empty, ReadWorld(next, models).Text);
        BranchSaveSessionFixture reloaded = new(_repository, "main", models);
        using PreparedFixtureSave unchanged = reloaded.Prepare(reloaded.State!, parameters: NoRebase);
        Assert.Empty(unchanged.Business.Write.Records);
    }

    [Fact]
    public void UpgradedUnchangedWorldForcesBaseAndNewLoadClearsRewriteObligation() {
        FrameAddress address = Seed(Old, new(5, 0, 0));
        StateModelRegistry models = Registry(Model());
        BranchSaveSessionFixture session = new(_repository, "main", models);
        World world = Assert.IsType<World>(session.State);
        using (PreparedFixtureSave discarded = session.Prepare(world, parameters: NoRebase)) {
            Assert.Equal(ObjectVersionKind.Base, Assert.Single(discarded.Business.Write.Records).Kind);
            Assert.Equal(address, discarded.Business.Write.SourceGraph);
        }
        Assert.Equal(address, session.StateSource!.Address);
        FrameAddress next;
        using (PreparedFixtureSave frozen = session.Prepare(world, parameters: NoRebase)) {
            Assert.Equal(ObjectVersionKind.Base, Assert.Single(frozen.Business.Write.Records).Kind);
            world.Value = 99;
            next = frozen.Commit().Business.Address;
        }
        BranchSaveSessionFixture reloaded = new(_repository, "main", models);
        World restored = Assert.IsType<World>(reloaded.State);
        Assert.Equal((byte)5, restored.Value); // The plan owns content from before the edit.
        Assert.Equal(InitialSequence, restored.Sequence);
        Assert.False(reloaded.Baseline!.Objects[new ObjectId(1)].RequiresRewrite);
        using (PreparedFixtureSave unchanged = reloaded.Prepare(restored, parameters: NoRebase)) {
            Assert.Empty(unchanged.Business.Write.Records);
        }
        restored.Value = 6;
        using PreparedFixtureSave changed = reloaded.Prepare(restored, parameters: NoRebase);
        Assert.Equal(ObjectVersionKind.Delta, Assert.Single(changed.Business.Write.Records).Kind);
        Assert.Equal(next, changed.Business.Write.SourceGraph);
        Assert.Equal(next, session.StateSource!.Address);
    }

    [Fact]
    public void AllocationStartsAboveCompleteSourceMembershipEvenAfterUpgradeCutsTheLargestId() {
        FrameAddress address = Seed(Old, new(1, 100, 0), Text(100, "old"));
        BranchSaveSessionFixture session = new(_repository, "main", Registry(Model(upgrade: state => state with { TextId = new ObjectId(0) })));
        World world = Assert.IsType<World>(session.State);
        Assert.Null(world.Text);
        world.Text = new string('n', 1);
        using (PreparedFixtureSave first = session.Prepare(world, parameters: NoRebase)) {
            Assert.Equal(new uint[] { 1, 101 }, first.Business.Write.Records.Select(row => row.ObjectId));
        }
        Assert.Equal(address, session.StateSource!.Address);
        using PreparedFixtureSave second = session.Prepare(world, parameters: NoRebase);
        Assert.Equal(new uint[] { 1, 102 }, second.Business.Write.Records.Select(row => row.ObjectId)); // Discard burns IDs.
        Assert.Equal(address, second.Business.Write.SourceGraph);
        Assert.Equal(new uint[] { 1, 102 }, second.Commit().Business.ObjectHeads.Keys);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExhaustedCursorAllowsLoadAndExistingPrepareButRejectsNewStrings(bool upgrade) {
        FrameAddress address = Seed(upgrade ? Old : Current, new(1, uint.MaxValue, 0), Text(uint.MaxValue, "retained"));
        BranchSaveSessionFixture session = new(_repository, "main", Registry(Model()));
        World world = Assert.IsType<World>(session.State);
        string retained = world.Text!;
        using (PreparedFixtureSave valid = session.Prepare(world, parameters: NoRebase)) {
            Assert.Equal(upgrade ? 1 : 0, valid.Business.Write.Records.Count);
        }
        world.Text = new string('x', 1);
        Assert.Throws<InvalidOperationException>(() => session.Prepare(world, parameters: NoRebase));
        world.Text = retained;
        using PreparedFixtureSave retry = session.Prepare(world, parameters: NoRebase);
        Assert.Equal(upgrade ? 1 : 0, retry.Business.Write.Records.Count);
        Assert.Equal(address, session.StateSource!.Address);
    }

    [Fact]
    public void EncodingFailureReleasesCaptureAndRetainsUpgradeRewriteAndBaseline() {
        bool fail = true;
        FrameAddress address = Seed(Old, new(5, 0, 0));
        BranchSaveSessionFixture session = new(_repository, "main", Registry(Model(beforePrepare: () => {
            if (fail) { throw new InvalidOperationException("test encoding failure"); }
        })));
        World world = Assert.IsType<World>(session.State);
        long tail = Tail();
        Assert.Throws<InvalidOperationException>(() => session.Prepare(world, parameters: NoRebase));
        Assert.Equal(tail, Tail());
        Assert.False(_repository.HasPending);
        fail = false;
        using PreparedFixtureSave retry = session.Prepare(world, parameters: NoRebase);
        Assert.Equal(address, retry.Business.Write.SourceGraph);
        Assert.Equal(ObjectVersionKind.Base, Assert.Single(retry.Business.Write.Records).Kind);
    }

    [Fact]
    public void CurrentSchemaConflictDoesNotAppendStateOrAdvanceLoadedParent() {
        FrameAddress address = Seed(Old, new(5, 0, 0));
        BranchSaveSessionFixture session = new(_repository, "main", Registry(Model()));
        World world = Assert.IsType<World>(session.State);
        // A conflicting final intent meets the captured target in the same temporary
        // catalog; no global registration mutates the source's accurate old view.
        session.Catalog.Set(new DurableSchema("World", 2, new DurableFieldInfo(1, TypeTag.Int64)));
        long tail = Tail();
        Assert.Throws<SchemaConflictException>(() => session.Prepare(world, parameters: NoRebase));
        Assert.Equal(tail, Tail());
        Assert.Equal(address, session.StateSource!.Address);
        Assert.False(_repository.HasPending);
        session.Catalog.Set(Current);
        using PreparedFixtureSave retry = session.Prepare(world, parameters: NoRebase);
        Assert.Equal(address, retry.Business.Write.SourceGraph);
        Assert.Equal(ObjectVersionKind.Base, Assert.Single(retry.Business.Write.Records).Kind);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(9u)]
    [InlineData(99u)]
    public void InvalidWorldIdDoesNotGuessARoot(uint id) {
        FrameAddress address = Seed(Current, new(1, 9, 0), Text(9, "text"));
        DecodedRevision decoded = DecodedRevision.ReadStoredGraph(Store, MechanismFixture.Snapshot(_repository, address), (Registry(Model())).FreezeConfiguration());
        if (id == 0) {
            Assert.Throws<ArgumentOutOfRangeException>(() => GraphReader.ReadRoot<World>(decoded, new ObjectId(id), new(decoded.SourceModels!), true));
        } else {
            Assert.Throws<InvalidDataException>(() => GraphReader.ReadRoot<World>(decoded, new ObjectId(id), new(decoded.SourceModels!), true));
        }
        Assert.Throws<InvalidDataException>(() => GraphReader.ReadRoot<OtherWorld>(decoded, new ObjectId(1), new(decoded.SourceModels!), true));
    }

    [Fact]
    public void HydrationPreservesSharedAndEqualIndependentStringIdentityWithoutConstructorInitializers() {
        FrameAddress shared = Seed(Current, new(1, 8, 8), Text(8, "same"));
        StateModelRegistry models = Registry(Model());
        World first = ReadWorld(shared, models);
        Assert.Same(first.Text, first.Alias);
        Assert.Equal(0, first.TransientMarker);
        FrameAddress distinct = Append(shared,
            catalog => [Durable(1, Current, new(1, 8, 9), catalog), Text(9, "same")]);
        World second = ReadWorld(distinct, models);
        Assert.Equal(second.Text, second.Alias);
        Assert.NotSame(second.Text, second.Alias);
    }

    [Fact]
    public void UpgradeFailureAndLateRegistrationCannotPublishAPartialLoadedView() {
        FrameAddress address = Seed(Old, new(5, 0, 0));
        StateModelRegistry models = Registry(Model(upgrade: _ => throw new InvalidOperationException("upgrade failure")));
        World? result = null;
        Assert.Throws<InvalidOperationException>(() => result = ReadWorld(address, models));
        Assert.Null(result);

        DurableSchema other = Schema("Other", 1);
        FrameAddress withOther = Append(address, catalog => [Durable(99, other, new(1, 0, 0), catalog)], other);
        StateModelBinding late = ModelCore<OtherWorld>(current: other, old: other);
        StateModelRegistry changing = new();
        changing.Register(Model(onRead: () => changing.Register(late)));
        Assert.Throws<InvalidDataException>(() => ReadWorld(withOther, changing));
        Assert.Equal((byte)5, ReadWorld(withOther, changing).Value);
    }

    [Fact]
    public void UnreachableSourceWithReaderButFailingUpgradeStillRejectsLoad() {
        FrameAddress address = Seed(Current, new(5, 0, 0));
        DurableSchema otherOld = Schema("Other", 1);
        DurableSchema otherCurrent = Schema("Other", 2);
        FrameAddress withOther = Append(address, catalog => [Durable(99, otherOld, new(1, 0, 0), catalog)], otherOld);
        StateModelRegistry models = Registry(Model());
        models.Register(ModelCore<OtherWorld>(upgrade: _ => throw new InvalidOperationException("unreachable upgrade fails"), current: otherCurrent, old: otherOld));
        World? delivered = null;
        Assert.Throws<InvalidOperationException>(() => delivered = ReadWorld(withOther, models));
        Assert.Null(delivered);
    }

    [Fact]
    public void RecursivePrepareFailureLeavesOwnerReusable() {
        FrameAddress address = Seed(Old, new(5, 0, 0));
        bool reenter = true;
        BranchSaveSessionFixture? session = null;
        session = new(_repository, "main", Registry(Model(beforePrepare: () => {
            if (reenter) { session!.Prepare(session.State!, parameters: NoRebase); }
        })));
        Assert.Throws<InvalidOperationException>(() => session.Prepare(session.State!, parameters: NoRebase));
        reenter = false;
        using PreparedFixtureSave retry = session.Prepare(session.State!, parameters: NoRebase);
        Assert.Equal(address, retry.Business.Write.SourceGraph);
        Assert.Equal(ObjectVersionKind.Base, Assert.Single(retry.Business.Write.Records).Kind);
    }

    [Fact]
    public void RegistryRejectsReplacementModelWithoutChangingTheExistingSnapshot() {
        FrameAddress address = Seed(Current, new(5, 0, 0));
        StateModelBinding original = Model();
        StateModelRegistry models = Registry(original);
        models.Register(original);
        Assert.Throws<InvalidOperationException>(() => models.Register(Model()));
        Assert.Same(original, models.CreateEnvironment().Models[TypeExpr.Named("World")]);
        Assert.Equal((byte)5, ReadWorld(address, models).Value);
    }

    [Fact]
    public void DuplicateClrTypeRegistrationIsAtomicAndDoesNotReserveTheRejectedFamilyOrReaders() {
        StateModelBinding original = Model();
        StateModelRegistry models = Registry(original);
        DurableSchema other = Schema("Other", 1);
        Assert.Throws<InvalidOperationException>(() => models.Register(Model(current: other, old: other)));
        ModelExecutionEnvironment afterFailure = models.CreateEnvironment();
        Assert.Same(original, Assert.Single(afterFailure.Models).Value);
        Assert.Same(original, Assert.Single(afterFailure.Types).Value);
        Assert.DoesNotContain(afterFailure.Readers.Keys, key => key.SchemaId == "Other");

        StateModelBinding accepted = ModelCore<OtherWorld>(current: other, old: other);
        models.Register(accepted);
        ModelExecutionEnvironment afterSuccess = models.CreateEnvironment();
        Assert.Same(accepted, afterSuccess.Models[TypeExpr.Named("Other")]);
        Assert.Same(accepted, afterSuccess.Types[typeof(OtherWorld)]);
        Assert.Same(accepted.Readers[0], afterSuccess.Readers[new("Other", 1)]);
        Assert.Single(afterFailure.Models);
        Assert.Single(afterFailure.Types);
    }

    [Fact]
    public void DefiniteAppendFailureDoesNotAdvanceTheLoadedBaseline() {
        FrameAddress address = Seed(Old, new(5, 0, 0));
        BranchSaveSessionFixture session = new(_repository, "main", Registry(Model()));
        long tail = Tail();
        _repository.SaveStageObserver = checkpoint => {
            if (checkpoint == RepositorySaveStage.BeforeAppend) { throw new InvalidOperationException("definite pre-append failure"); }
        };
        using (PreparedFixtureSave failed = session.Prepare(session.State!, parameters: NoRebase)) {
            RepositoryPublicationException error = Assert.Throws<RepositoryPublicationException>(() => failed.Commit());
            Assert.Equal(RepositoryPublicationOutcome.NotPublished, error.Outcome);
            Assert.Null(error.CandidateRevisionAddress);
        }
        Assert.Equal(tail, Tail());
        Assert.False(_repository.HasPending);
        Assert.False(_repository.IsFaulted);
        Assert.Equal(address, MechanismFixture.FindCurrent(_repository, "main")?.Address);
        Assert.Equal(address, session.StateSource!.Address);
        Assert.True(session.Baseline!.Objects[new ObjectId(1)].RequiresRewrite);
        _repository.SaveStageObserver = null;
        using PreparedFixtureSave retry = session.Prepare(session.State!, parameters: NoRebase);
        Assert.Equal(address, retry.Business.Write.SourceGraph);
        Assert.Equal(ObjectVersionKind.Base, Assert.Single(retry.Business.Write.Records).Kind);
        Assert.NotEqual(address, retry.Commit().Business.Address);
    }

    private class World : IDurableObject {
        internal byte Value;
        internal string? Text;
        internal string? Alias;
        internal ulong Sequence = InitialSequence;
        internal int TransientMarker = 73;
        internal World(int unused) => throw new InvalidOperationException("Constructors must not run.");
    }
    private sealed class OtherWorld() : World(0) { }
    private readonly record struct State(byte Value, ObjectId TextId, ObjectId AliasId, ulong Sequence = InitialSequence) {
        internal State(byte value, uint textId, uint aliasId, ulong sequence = InitialSequence) : this(value, new ObjectId(textId), new ObjectId(aliasId), sequence) { }
    }

    private static StateModelBinding Model(Func<State, State>? upgrade = null, Action? beforePrepare = null,
        Action? onRead = null, DurableSchema? current = null, DurableSchema? old = null) =>
        ModelCore<World>(upgrade, beforePrepare, onRead, current, old);

    private static StateModelBinding ModelCore<TWorld>(Func<State, State>? upgrade = null, Action? beforePrepare = null,
        Action? onRead = null, DurableSchema? current = null, DurableSchema? old = null) where TWorld : World {
        DurableSchema currentSchema = current ?? Current;
        DurableSchema oldSchema = old ?? Old;
        StatePreparationBinding<State> preparation = new(currentSchema,
            (in State state) => { beforePrepare?.Invoke(); return Base(state); },
            static (in State prior, in State next) => Delta(prior, next));
        StateReaderBinding<State> Reader(DurableSchema schema) => new(schema,
            (ref BareValueReader reader) => { onRead?.Invoke(); return Read(ref reader); },
            static (ref BareValueReader reader, in State prior) => Apply(ref reader, prior), Validate);
        StateReaderBinding[] readers = currentSchema.Equals(oldSchema)
            ? [Reader(currentSchema)] : [Reader(oldSchema), Reader(currentSchema)];
        return new StateModelBinding<TWorld, State>(preparation, readers,
            row => row.Schema!.Equals(currentSchema) ? row.GetState<State>() : (upgrade ?? (static state => state))(row.GetState<State>()),
            static () => (TWorld)RuntimeHelpers.GetUninitializedObject(typeof(TWorld)),
            static (TWorld world, in State state, ObjectReadTable strings) => {
                world.Value = state.Value;
                world.Text = strings.ResolveString(state.TextId);
                world.Alias = strings.ResolveString(state.AliasId);
                world.Sequence = state.Sequence;
            },
            static (world, context) => new(world.Value, context.CaptureString(world.Text), context.CaptureString(world.Alias), world.Sequence),
            Validate);
    }

    private static void Validate(in State state, IStateReferenceVisitor visitor) {
        visitor.VisitString(state.TextId);
        visitor.VisitString(state.AliasId);
    }
    private static StateModelRegistry Registry(StateModelBinding model) {
        StateModelRegistry result = new();
        result.Register(model);
        return result;
    }
    private static DurableSchema Schema(string id, int version) => new(id, version,
        new DurableFieldInfo(1, TypeTag.Byte), new DurableFieldInfo(2, TypeTag.String), new DurableFieldInfo(3, TypeTag.String),
        new DurableFieldInfo(4, TypeTag.UInt64));
    private FrameAddress Seed(DurableSchema schema, State state, params ObjectVersionRecord[] strings) =>
        Append(null, catalog => new[] { Durable(1, schema, state, catalog) }.Concat(strings), schema);

    // Fixture construction uses the real typed writer and paired publication owner.
    // Hand-written bodies isolate this restore/save contract; they are not a second kernel.
    private FrameAddress Append(FrameAddress? source, Func<SchemaCatalogView, IEnumerable<ObjectVersionRecord>> records,
        params DurableSchema[] schemas) {
        SchemaCatalogView? accepted = _repository.SharedSchemaRoot is { } root ? SchemaCatalogView.Load(Store, root) : null;
        SchemaCatalogWorkspace catalog = new(_repository.EnsureEditingAllowed, accepted);
        foreach (DurableSchema schema in schemas) { catalog.Set(schema); }
        PreparedSchemaCatalogWrite meta = catalog.Prepare();
        using PendingRepositorySave pending = MechanismFixture.Prepare(_repository, "main", StoredGraphKind.State, new(source, new ObjectId(1), records(meta.View)), meta.Write);
        return _repository.Commit(pending, meta.Install, prepareMeta: meta.PrepareInstall).Business.Address;
    }

    private static ObjectVersionRecord Durable(uint id, DurableSchema schema, State state, SchemaCatalogView catalog) =>
        ObjectVersionRecord.CreateBase(id, TypedBaseBodyCodec.Encode(ObjectLayout.ForDurable(schema), Base(state), catalog));
    private static ObjectVersionRecord Text(uint id, string value) =>
        ObjectVersionRecord.CreateBase(id, TypedBaseBodyCodec.Encode(ObjectLayout.String, StringBodyPreparer.PrepareBase(value)));

    private World ReadWorld(FrameAddress address, StateModelRegistry models) {
        DecodedRevision source = DecodedRevision.ReadStoredGraph(Store, MechanismFixture.Snapshot(_repository, address), (models).FreezeConfiguration());
        return GraphReader.ReadRoot<World>(source, Store.ReadMetadata(address).RootId, new(source.SourceModels!), true);
    }

    private static PreparedBaseBody Base(State state) {
        ArrayBufferWriter<byte> bytes = new();
        BareValueWriter writer = new(bytes);
        writer.WriteByte(state.Value);
        writer.WriteVarUInt32(state.TextId.Value);
        writer.WriteVarUInt32(state.AliasId.Value);
        writer.WriteVarUInt64(state.Sequence);
        return new(bytes.WrittenSpan);
    }
    private static State Read(ref BareValueReader reader) => new(reader.ReadByte(), reader.ReadVarUInt32(), reader.ReadVarUInt32(), reader.ReadVarUInt64());
    private static PreparedDeltaBody Delta(State prior, State next) {
        byte mask = (byte)((prior.Value != next.Value ? 1 : 0) | (prior.TextId != next.TextId ? 2 : 0) | (prior.AliasId != next.AliasId ? 4 : 0) | (prior.Sequence != next.Sequence ? 8 : 0));
        ArrayBufferWriter<byte> bytes = new();
        BareValueWriter writer = new(bytes);
        writer.WriteByte(mask);
        if ((mask & 1) != 0) writer.WriteByte(next.Value);
        if ((mask & 2) != 0) writer.WriteVarUInt32(next.TextId.Value);
        if ((mask & 4) != 0) writer.WriteVarUInt32(next.AliasId.Value);
        if ((mask & 8) != 0) writer.WriteVarUInt64(next.Sequence);
        return new(mask != 0, bytes.WrittenSpan);
    }
    private static State Apply(ref BareValueReader reader, State prior) {
        byte mask = reader.ReadByte();
        if (mask == 0 || mask > 15) throw new InvalidDataException("Invalid test bitmap.");
        return new((mask & 1) != 0 ? reader.ReadByte() : prior.Value,
            (mask & 2) != 0 ? new ObjectId(reader.ReadVarUInt32()) : prior.TextId,
            (mask & 4) != 0 ? new ObjectId(reader.ReadVarUInt32()) : prior.AliasId,
            (mask & 8) != 0 ? reader.ReadVarUInt64() : prior.Sequence);
    }
    private long Tail() => DataBytes(_repository.Data);
    public void Dispose() {
        _repository.Dispose();
        string resolved = Path.GetFullPath(_root);
        if (!string.Equals(Path.GetDirectoryName(resolved), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(resolved).StartsWith("durable-graph-loaded-world-", StringComparison.Ordinal)) {
            throw new InvalidOperationException("Refusing to delete outside this test fixture.");
        }
        Directory.Delete(resolved, recursive: true);
    }
}
