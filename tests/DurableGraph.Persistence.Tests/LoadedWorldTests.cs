using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Buffers;
using System.Runtime.CompilerServices;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using Atelia.RbfSegmentStore;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class LoadedWorldTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"durable-graph-loaded-world-{Guid.NewGuid():N}");
    private readonly IRbfFile _file;
    private readonly SegmentStore _segments;
    private readonly SchemaStore _schemas;
    private readonly StateRevisionStore _store;
    // A real stable scalar makes sparse field updates smaller than a complete Base.
    private const ulong InitialSequence = 0xFEDC_BA98_7654_3210UL;
    private static readonly DurableSchema Old = Schema("World", 1);
    private static readonly DurableSchema Current = Schema("World", 2);
    private static readonly ReadAmplificationBaseBudgetParameters NoRebase = new(1000000, 1);

    public LoadedWorldTests() {
        Directory.CreateDirectory(_root);
        _file = RbfFile.CreateNew(Path.Combine(_root, "schemas.rbf"));
        _schemas = new(_file);
        _segments = SegmentStore.CreateNew(Path.Combine(_root, "state"), new() { NewStoreLayout = RbfSegmentStoreLayout.Flat });
        _store = new(_segments);
    }

    [Fact]
    public void NormalizationFollowsCompleteDeltaChainAndRetainsSourceRowsWithoutWriting() {
        _schemas.Register(Old);
        FrameAddress first = _store.Append(StateRevision.CreateObjectHeadMapBase(null,
            [Durable(1, Old, new(2, 8, 0)), Text(8, "removed by upgrade")], []));
        FrameAddress second = _store.Append(StateRevision.CreateObjectHeadMapDelta(first,
            [ObjectVersionRecord.CreateDelta(1, first, Delta(new(2, 8, 0), new(3, 8, 0)).Body)], []));
        FrameAddress third = _store.Append(StateRevision.CreateObjectHeadMapDelta(second,
            [ObjectVersionRecord.CreateDelta(1, second, Delta(new(3, 8, 0), new(4, 8, 0, InitialSequence - 1)).Body)], []));
        StateModelRegistry models = Registry(Model(upgrade: state => state with { Value = (byte)(state.Value + 10), TextId = new ObjectId(0) }));
        StateModelSnapshot snapshot = models.Snapshot();
        DecodedRevision decoded = RevisionDecoder.ReadSnapshot(_store, _schemas, third, snapshot.Readers);
        long schemaTail = _file.TailOffset;
        long stateTail = Tail();
        NormalizedRevision normalized = NormalizedRevision.Create(decoded, snapshot);
        Assert.Equal(new State(4, 8, 0, InitialSequence - 1), decoded.GetRequired(new ObjectId(1)).GetState<State>());
        Assert.Equal(new State(14, 0, 0, InitialSequence - 1), normalized.Objects[new ObjectId(1)].Current.GetState<State>());
        Assert.True(normalized.Objects[new ObjectId(1)].RequiresRewrite);
        Assert.Equal(Old, normalized.Objects[new ObjectId(1)].SourceSchema);
        Assert.Equal(Current, normalized.Objects[new ObjectId(1)].Current.Schema);
        Assert.Contains(new ObjectId(8), normalized.Objects.Keys);
        Assert.Same(decoded.GetRequired(new ObjectId(8)).StringContent, normalized.Objects[new ObjectId(8)].Current.StringContent);
        Assert.Equal(schemaTail, _file.TailOffset);
        Assert.Equal(stateTail, Tail());
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(99u)]
    public void InvalidUpgradeReferenceFailsBeforeWorldDeliveryAndDoesNotWrite(uint badId) {
        FrameAddress address = Seed(Old, new(1, 0, 0));
        StateModelRegistry models = Registry(Model(upgrade: state => state with { TextId = new ObjectId(badId) }));
        long schemaTail = _file.TailOffset;
        long stateTail = Tail();
        LoadedWorld<World>? delivered = null;
        Assert.Throws<InvalidDataException>(() => delivered = LoadedWorld.Load<World>(_store, _schemas, address, new ObjectId(1), models));
        Assert.Null(delivered);
        Assert.Equal(schemaTail, _file.TailOffset);
        Assert.Equal(stateTail, Tail());
    }

    [Fact]
    public void CurrentRowsAreNotUpgradedAndLateUnknownSourceFamilyStillFails() {
        FrameAddress address = Seed(Current, new(7, 0, 0));
        StateModelRegistry models = Registry(Model(upgrade: _ => throw new Exception("must not run")));
        LoadedWorld<World> loaded = LoadedWorld.Load<World>(_store, _schemas, address, new ObjectId(1), models);
        Assert.Equal((byte)7, loaded.World.Value);
        Assert.Empty(loaded.Prepare(NoRebase).Revision.LocalObjects);
        DurableSchema unknown = Schema("Unknown", 1);
        _schemas.Register(unknown);
        FrameAddress bad = _store.Append(StateRevision.CreateObjectHeadMapDelta(address, [Durable(99, unknown, new(1, 0, 0))], []));
        Assert.Throws<InvalidDataException>(() => LoadedWorld.Load<World>(_store, _schemas, bad, new ObjectId(1), models));
    }

    [Fact]
    public void EmptyAliasKeepsOriginalBaselineSlotUntilRealDeltaAndRemoveArePersisted() {
        FrameAddress address = Seed(Current, new(7, 9, 0), Text(3, ""), Text(9, ""));
        StateModelRegistry models = Registry(Model());
        LoadedWorld<World> loaded = LoadedWorld.Load<World>(_store, _schemas, address, new ObjectId(1), models);
        Assert.Same(string.Empty, loaded.World.Text);
        PreparedWorldRevision plan = loaded.Prepare(NoRebase);
        ObjectVersionRecord write = Assert.Single(plan.Revision.LocalObjects);
        Assert.Equal(ObjectVersionKind.Delta, write.Kind);
        Assert.Equal(1u, write.ObjectId);
        Assert.Equal(new uint[] { 9 }, plan.Revision.RemovedObjectIds);
        FrameAddress next = _store.Append(plan.Revision);
        DecodedRevision decoded = RevisionDecoder.ReadSnapshot(_store, _schemas, next, models.Snapshot().Readers);
        Assert.Equal(new ObjectId(3), decoded.GetRequired(new ObjectId(1)).GetState<State>().TextId);
        Assert.Equal(new ObjectId(9), RevisionDecoder.ReadSnapshot(_store, _schemas, address, models.Snapshot().Readers).GetRequired(new ObjectId(1)).GetState<State>().TextId);
        Assert.Same(string.Empty, LoadedWorld.Load<World>(_store, _schemas, next, plan.WorldId, models).World.Text);
        Assert.Empty(LoadedWorld.Load<World>(_store, _schemas, next, new ObjectId(1), models).Prepare(NoRebase).Revision.LocalObjects);
    }

    [Fact]
    public void UpgradedUnchangedWorldForcesBaseAndNewLoadClearsRewriteObligation() {
        FrameAddress address = Seed(Old, new(5, 0, 0));
        StateModelRegistry models = Registry(Model());
        LoadedWorld<World> loaded = LoadedWorld.Load<World>(_store, _schemas, address, new ObjectId(1), models);
        PreparedWorldRevision plan = loaded.Prepare(NoRebase);
        Assert.Equal(ObjectVersionKind.Base, Assert.Single(plan.Revision.LocalObjects).Kind);
        Assert.Equal(address, plan.Revision.ParentRevisionAddress);
        loaded.World.Value = 99;
        FrameAddress next = _store.Append(plan.Revision);
        LoadedWorld<World> reloaded = LoadedWorld.Load<World>(_store, _schemas, next, plan.WorldId, models);
        Assert.Equal((byte)5, reloaded.World.Value); // The plan owns content from before the edit.
        Assert.Equal(InitialSequence, reloaded.World.Sequence);
        Assert.Empty(reloaded.Prepare(NoRebase).Revision.LocalObjects);
        reloaded.World.Value = 6;
        Assert.Equal(ObjectVersionKind.Delta, Assert.Single(reloaded.Prepare(NoRebase).Revision.LocalObjects).Kind);
        PreparedWorldRevision repeated = loaded.Prepare(NoRebase);
        Assert.Equal(address, repeated.Revision.ParentRevisionAddress);
        Assert.Equal(ObjectVersionKind.Base, Assert.Single(repeated.Revision.LocalObjects).Kind);
        Assert.Equal(address, loaded.ParentRevisionAddress);
    }

    [Fact]
    public void AllocationStartsAboveCompleteSourceMembershipEvenAfterUpgradeCutsTheLargestId() {
        FrameAddress address = Seed(Old, new(1, 100, 0), Text(100, "old"));
        LoadedWorld<World> loaded = LoadedWorld.Load<World>(_store, _schemas, address, new ObjectId(1),
            Registry(Model(upgrade: state => state with { TextId = new ObjectId(0) })));
        Assert.Null(loaded.World.Text);
        loaded.World.Text = new string('n', 1);
        PreparedWorldRevision first = loaded.Prepare(NoRebase);
        Assert.Equal(new uint[] { 1, 101 }, first.Revision.LocalObjectIds);
        Assert.Equal(new uint[] { 1, 101 }, _store.ReadLiveObjectHeadMap(_store.Append(first.Revision)).Keys);
        PreparedWorldRevision second = loaded.Prepare(NoRebase);
        Assert.Equal(new uint[] { 1, 102 }, second.Revision.LocalObjectIds); // Discard burns IDs.
        Assert.Equal(address, second.Revision.ParentRevisionAddress);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExhaustedCursorAllowsLoadAndExistingPrepareButRejectsNewStrings(bool upgrade) {
        FrameAddress address = Seed(upgrade ? Old : Current, new(1, uint.MaxValue, 0), Text(uint.MaxValue, "retained"));
        LoadedWorld<World> loaded = LoadedWorld.Load<World>(_store, _schemas, address, new ObjectId(1), Registry(Model()));
        string retained = loaded.World.Text!;
        PreparedWorldRevision valid = loaded.Prepare(NoRebase);
        Assert.Equal(upgrade ? 1 : 0, valid.Revision.LocalObjects.Count);
        loaded.World.Text = new string('x', 1);
        Assert.Throws<InvalidOperationException>(() => loaded.Prepare(NoRebase));
        loaded.World.Text = retained;
        Assert.Equal(upgrade ? 1 : 0, loaded.Prepare(NoRebase).Revision.LocalObjects.Count);
        Assert.Equal(address, loaded.ParentRevisionAddress);
    }

    [Fact]
    public void EncodingFailureReleasesCaptureAndRetainsUpgradeRewriteAndBaseline() {
        bool fail = true;
        FrameAddress address = Seed(Old, new(5, 0, 0));
        LoadedWorld<World> loaded = LoadedWorld.Load<World>(_store, _schemas, address, new ObjectId(1),
            Registry(Model(beforePrepare: () => { if (fail) throw new InvalidOperationException("test encoding failure"); })));
        Assert.Throws<InvalidOperationException>(() => loaded.Prepare(NoRebase));
        fail = false;
        PreparedWorldRevision retried = loaded.Prepare(NoRebase);
        Assert.Equal(address, retried.Revision.ParentRevisionAddress);
        Assert.Equal(ObjectVersionKind.Base, Assert.Single(retried.Revision.LocalObjects).Kind);
    }

    [Fact]
    public void CurrentSchemaConflictDoesNotAppendStateOrAdvanceLoadedParent() {
        FrameAddress address = Seed(Old, new(5, 0, 0));
        LoadedWorld<World> loaded = LoadedWorld.Load<World>(_store, _schemas, address, new ObjectId(1), Registry(Model()));
        _schemas.Register(new DurableSchema("World", 2, new DurableFieldInfo(1, TypeTag.Int64)));
        long tail = Tail();
        // The frozen model resolver detects the newly registered conflict before Schema registration.
        Assert.Throws<InvalidDataException>(() => loaded.Prepare(NoRebase));
        Assert.Equal(tail, Tail());
        Assert.Equal(address, loaded.ParentRevisionAddress);
        Assert.Throws<InvalidDataException>(() => loaded.Prepare(NoRebase));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(9u)]
    [InlineData(99u)]
    public void InvalidWorldIdDoesNotGuessARoot(uint id) {
        FrameAddress address = Seed(Current, new(1, 9, 0), Text(9, "text"));
        StateModelRegistry models = Registry(Model());
        if (id == 0) {
            Assert.Throws<ArgumentOutOfRangeException>(() => LoadedWorld.Load<World>(_store, _schemas, address, new ObjectId(id), models));
        } else {
            Assert.Throws<InvalidDataException>(() => LoadedWorld.Load<World>(_store, _schemas, address, new ObjectId(id), models));
        }
        Assert.Throws<InvalidDataException>(() => LoadedWorld.Load<OtherWorld>(_store, _schemas, address, new ObjectId(1), models));
    }

    [Fact]
    public void HydrationPreservesSharedAndEqualIndependentStringIdentityWithoutConstructorInitializers() {
        FrameAddress shared = Seed(Current, new(1, 8, 8), Text(8, "same"));
        StateModelRegistry models = Registry(Model());
        LoadedWorld<World> first = LoadedWorld.Load<World>(_store, _schemas, shared, new ObjectId(1), models);
        Assert.Same(first.World.Text, first.World.Alias);
        Assert.Equal(0, first.World.TransientMarker);
        FrameAddress distinct = _store.Append(StateRevision.CreateObjectHeadMapDelta(shared,
            [Durable(1, Current, new(1, 8, 9)), Text(9, "same")], []));
        LoadedWorld<World> second = LoadedWorld.Load<World>(_store, _schemas, distinct, new ObjectId(1), models);
        Assert.Equal(second.World.Text, second.World.Alias);
        Assert.NotSame(second.World.Text, second.World.Alias);
    }

    [Fact]
    public void UpgradeFailureAndLateRegistrationCannotPublishAPartialLoadedView() {
        FrameAddress address = Seed(Old, new(5, 0, 0));
        StateModelRegistry models = Registry(Model(upgrade: _ => throw new InvalidOperationException("upgrade failure")));
        LoadedWorld<World>? result = null;
        Assert.Throws<InvalidOperationException>(() => result = LoadedWorld.Load<World>(_store, _schemas, address, new ObjectId(1), models));
        Assert.Null(result);

        DurableSchema other = Schema("Other", 1);
        _schemas.Register(other);
        FrameAddress withOther = _store.Append(StateRevision.CreateObjectHeadMapDelta(address, [Durable(99, other, new(1, 0, 0))], []));
        StateModelBinding late = ModelCore<OtherWorld>(current: other, old: other);
        StateModelRegistry changing = new();
        changing.Register(Model(onRead: () => changing.Register(late)));
        Assert.Throws<InvalidDataException>(() => LoadedWorld.Load<World>(_store, _schemas, withOther, new ObjectId(1), changing));
        Assert.Equal((byte)5, LoadedWorld.Load<World>(_store, _schemas, withOther, new ObjectId(1), changing).World.Value);
    }

    [Fact]
    public void UnreachableSourceWithReaderButFailingUpgradeStillRejectsLoad() {
        FrameAddress address = Seed(Current, new(5, 0, 0));
        DurableSchema otherOld = Schema("Other", 1);
        DurableSchema otherCurrent = Schema("Other", 2);
        _schemas.Register(otherOld);
        FrameAddress withOther = _store.Append(StateRevision.CreateObjectHeadMapDelta(address, [Durable(99, otherOld, new(1, 0, 0))], []));
        StateModelRegistry models = Registry(Model());
        models.Register(ModelCore<OtherWorld>(upgrade: _ => throw new InvalidOperationException("unreachable upgrade fails"), current: otherCurrent, old: otherOld));
        LoadedWorld<World>? delivered = null;
        Assert.Throws<InvalidOperationException>(() => delivered = LoadedWorld.Load<World>(_store, _schemas, withOther, new ObjectId(1), models));
        Assert.Null(delivered);
    }

    [Fact]
    public void RecursivePrepareFailureLeavesOwnerReusable() {
        FrameAddress address = Seed(Old, new(5, 0, 0));
        bool reenter = true;
        LoadedWorld<World>? loaded = null;
        loaded = LoadedWorld.Load<World>(_store, _schemas, address, new ObjectId(1),
            Registry(Model(beforePrepare: () => { if (reenter) loaded!.Prepare(NoRebase); })));
        Assert.Throws<InvalidOperationException>(() => loaded.Prepare(NoRebase));
        reenter = false;
        PreparedWorldRevision retried = loaded.Prepare(NoRebase);
        Assert.Equal(address, retried.Revision.ParentRevisionAddress);
        Assert.Equal(ObjectVersionKind.Base, Assert.Single(retried.Revision.LocalObjects).Kind);
    }

    [Fact]
    public void RegistryRejectsReplacementModelWithoutChangingTheExistingSnapshot() {
        FrameAddress address = Seed(Current, new(5, 0, 0));
        StateModelBinding original = Model();
        StateModelRegistry models = Registry(original);
        models.Register(original);
        Assert.Throws<InvalidOperationException>(() => models.Register(Model()));
        Assert.Same(original, models.Snapshot().Models[TypeExpr.Named("World")]);
        Assert.Equal((byte)5, LoadedWorld.Load<World>(_store, _schemas, address, new ObjectId(1), models).World.Value);
    }

    [Fact]
    public void DuplicateClrTypeRegistrationIsAtomicAndDoesNotReserveTheRejectedFamilyOrReaders() {
        StateModelBinding original = Model();
        StateModelRegistry models = Registry(original);
        DurableSchema other = Schema("Other", 1);
        Assert.Throws<InvalidOperationException>(() => models.Register(Model(current: other, old: other)));
        StateModelSnapshot afterFailure = models.Snapshot();
        Assert.Same(original, Assert.Single(afterFailure.Models).Value);
        Assert.Same(original, Assert.Single(afterFailure.Types).Value);
        Assert.DoesNotContain(afterFailure.Readers.Keys, key => key.SchemaId == "Other");

        StateModelBinding accepted = ModelCore<OtherWorld>(current: other, old: other);
        models.Register(accepted);
        StateModelSnapshot afterSuccess = models.Snapshot();
        Assert.Same(accepted, afterSuccess.Models[TypeExpr.Named("Other")]);
        Assert.Same(accepted, afterSuccess.Types[typeof(OtherWorld)]);
        Assert.Same(accepted.Readers[0], afterSuccess.Readers[new("Other", 1)]);
        Assert.Single(afterFailure.Models);
        Assert.Single(afterFailure.Types);
    }

    [Fact]
    public void DefiniteAppendFailureDoesNotAdvanceTheLoadedBaseline() {
        FrameAddress address = Seed(Old, new(5, 0, 0));
        LoadedWorld<World> loaded = LoadedWorld.Load<World>(_store, _schemas, address, new ObjectId(1), Registry(Model()));
        PreparedWorldRevision plan = loaded.Prepare(NoRebase);
        using (RbfSegmentWriterLease occupied = _segments.OpenActiveWriter()) {
            long tail = occupied.File.TailOffset;
            // An occupied exclusive writer rejects Append before it can write a frame.
            Assert.Throws<InvalidOperationException>(() => _store.Append(plan.Revision));
            Assert.Equal(tail, occupied.File.TailOffset);
        }
        PreparedWorldRevision retry = loaded.Prepare(NoRebase);
        Assert.Equal(address, loaded.ParentRevisionAddress);
        Assert.Equal(address, retry.Revision.ParentRevisionAddress);
        Assert.Equal(ObjectVersionKind.Base, Assert.Single(retry.Revision.LocalObjects).Kind);
        Assert.NotEqual(address, _store.Append(retry.Revision));
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
        CapturedStatePreparation<State> preparation = new(currentSchema,
            (in State state) => { beforePrepare?.Invoke(); return Base(state); },
            static (in State prior, in State next) => Delta(prior, next));
        StateReaderBinding<State> Reader(DurableSchema schema) => new(schema,
            (ref BinaryPayloadReader reader) => { onRead?.Invoke(); return Read(ref reader); },
            static (ref BinaryPayloadReader reader, in State prior) => Apply(ref reader, prior), Validate);
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
    private FrameAddress Seed(DurableSchema schema, State state, params ObjectVersionRecord[] strings) {
        _schemas.Register(schema);
        return _store.Append(StateRevision.CreateObjectHeadMapBase(null, new[] { Durable(1, schema, state) }.Concat(strings), []));
    }
    private ObjectVersionRecord Durable(uint id, DurableSchema schema, State state) =>
        ObjectVersionRecord.CreateBase(id, BaseObjectBodyCodec.Encode(
            _schemas.RegisterRepresentations([ObjectLayout.ForDurable(schema)])[0], Base(state)).Body);
    private static ObjectVersionRecord Text(uint id, string value) =>
        ObjectVersionRecord.CreateBase(id, BaseObjectBodyCodec.EncodeString(StringPayloadCodec.PrepareBase(value)).Body);
    private static PreparedBaseBody Base(State state) {
        ArrayBufferWriter<byte> bytes = new();
        BinaryPayloadWriter writer = new(bytes);
        writer.WriteByte(state.Value);
        writer.WriteUInt32(state.TextId.Value);
        writer.WriteUInt32(state.AliasId.Value);
        writer.WriteUInt64(state.Sequence);
        return new(bytes.WrittenSpan);
    }
    private static State Read(ref BinaryPayloadReader reader) => new(reader.ReadByte(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt64());
    private static PreparedDeltaBody Delta(State prior, State next) {
        byte mask = (byte)((prior.Value != next.Value ? 1 : 0) | (prior.TextId != next.TextId ? 2 : 0) | (prior.AliasId != next.AliasId ? 4 : 0) | (prior.Sequence != next.Sequence ? 8 : 0));
        ArrayBufferWriter<byte> bytes = new();
        BinaryPayloadWriter writer = new(bytes);
        writer.WriteByte(mask);
        if ((mask & 1) != 0) writer.WriteByte(next.Value);
        if ((mask & 2) != 0) writer.WriteUInt32(next.TextId.Value);
        if ((mask & 4) != 0) writer.WriteUInt32(next.AliasId.Value);
        if ((mask & 8) != 0) writer.WriteUInt64(next.Sequence);
        return new(mask != 0, bytes.WrittenSpan);
    }
    private static State Apply(ref BinaryPayloadReader reader, State prior) {
        byte mask = reader.ReadByte();
        if (mask == 0 || mask > 15) throw new InvalidDataException("Invalid test bitmap.");
        return new((mask & 1) != 0 ? reader.ReadByte() : prior.Value,
            (mask & 2) != 0 ? new ObjectId(reader.ReadUInt32()) : prior.TextId,
            (mask & 4) != 0 ? new ObjectId(reader.ReadUInt32()) : prior.AliasId,
            (mask & 8) != 0 ? reader.ReadUInt64() : prior.Sequence);
    }
    private long Tail() {
        using RbfSegmentWriterLease writer = _segments.OpenActiveWriter();
        return writer.File.TailOffset;
    }
    public void Dispose() {
        _store.Dispose();
        _file.Dispose();
        _segments.Dispose();
        string resolved = Path.GetFullPath(_root);
        if (!string.Equals(Path.GetDirectoryName(resolved), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(resolved).StartsWith("durable-graph-loaded-world-", StringComparison.Ordinal)) {
            throw new InvalidOperationException("Refusing to delete outside this test fixture.");
        }
        Directory.Delete(resolved, recursive: true);
    }
}
