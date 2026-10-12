using Atelia.DurableGraph.Schema;
using System.Reflection;
using Atelia.DurableGraph.Build;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Fact]
    public void LoadedWorldGeneratedHistoricalChainUpgradesRestoresAndResavesAcrossColdReopens() {
        var fixture = CompileLoadedWorldFixture();
        var prepareDelta = fixture.Host.GetMethod("Prepare1")!.CreateDelegate<Func<byte[], byte[], PreparedDeltaBody>>();
        byte[] original = [20, 2, 10, 11, 10, 12, 12];
        byte[] middle = [20, 4, 10, 11, 10, 12, 12];
        byte[] latest = [20, 6, 10, 11, 10, 12, 12];
        PreparedDeltaBody delta1 = prepareDelta(original, middle);
        PreparedDeltaBody delta2 = prepareDelta(middle, latest);
        using RawBaseDirectory directory = new();
        const long rotationThreshold = 128;
        FrameAddress first, second, third, rewritten, unchanged, changed;
        using (GraphResources segments = GraphResources.CreateLegacyNew(directory.Path, rotationThreshold)) {
            SchemaStore schemas = segments.Schemas;
            schemas.RegisterBatch([fixture.OldSchema]);
            using StateRevisionStore store = new(segments.Data);
            first = store.Append(StateRevision.CreateObjectHeadMapBase(null, [
                LoadedDurable(schemas, 1, fixture.OldSchema, original), LoadedText(10, "same"),
                LoadedText(11, "same"), LoadedText(12, ""), LoadedText(20, "retired ancestor field"),
            ], []));
            second = store.Append(StateRevision.CreateObjectHeadMapDelta(first,
                [ObjectVersionRecord.CreateDelta(1, first, delta1.Body)], []));
            third = store.Append(StateRevision.CreateObjectHeadMapDelta(second,
                [ObjectVersionRecord.CreateDelta(1, second, delta2.Body)], []));
            segments.Data.ConfirmDurable();
        }
        Assert.NotEqual(first, third);
        Array.Clear(original);
        delta1 = null!;
        delta2 = null!;

        // The only source for the loaded baseline is the reopened files and generated model registry.
        using (GraphResources segments = GraphResources.OpenLegacyExisting(directory.Path, rotationThreshold)) {
            SchemaStore schemas = segments.Schemas;
            using StateRevisionStore store = new(segments.Data);
            Assert.Equal(3, store.ReadObjectVersionChain(third, 1).Records.Count);
            object loaded = fixture.Load(store, schemas, third);
            fixture.Check(loaded, 3);
            Assert.Equal(1, fixture.UpgradeCalls()); // One upgrade after both old Deltas have been applied.
            FixturePreparedWorldRevision plan = fixture.Prepare(loaded);
            Assert.Equal(new ObjectId(1), plan.WorldId);
            Assert.Equal(third, plan.Revision.ParentRevisionAddress);
            Assert.Equal<uint>([20], plan.Revision.RemovedObjectIds);
            ObjectVersionRecord rewrite = Assert.Single(plan.Revision.LocalObjects);
            Assert.Equal(ObjectVersionKind.Base, rewrite.Kind);
            Assert.Equal(1u, rewrite.ObjectId);
            DecodedBaseObjectBody envelope = BaseObjectBodyCodec.Decode(rewrite.Body, schemas);
            Assert.Equal(fixture.CurrentSchema.Version, envelope.Layout.Schema!.Version);
            Assert.Equal<byte>([7, 10, 6, 10, 11, 10, 12, 12, .. new byte[16]], envelope.Body.ToArray());
            fixture.Change(loaded, 99); // Frozen output cannot follow later domain mutation.
            rewritten = store.Append(plan.Revision);
            Assert.Single(store.ReadObjectVersionChain(rewritten, 1).Records);
            Assert.Equal<uint>([1, 10, 11, 12], store.ReadLiveObjectHeadMap(rewritten).Keys.Order());
            fixture.Change(loaded, 3);
            FixturePreparedWorldRevision repeated = fixture.Prepare(loaded);
            Assert.Equal(third, repeated.Revision.ParentRevisionAddress);
            Assert.Equal(ObjectVersionKind.Base, Assert.Single(repeated.Revision.LocalObjects).Kind);
            Assert.Equal(rewrite.Body.ToArray(), Assert.Single(repeated.Revision.LocalObjects).Body.ToArray());
            segments.Data.ConfirmDurable();
        }

        using (GraphResources segments = GraphResources.OpenLegacyExisting(directory.Path, rotationThreshold)) {
            SchemaStore schemas = segments.Schemas;
            using StateRevisionStore store = new(segments.Data);
            int upgrades = fixture.UpgradeCalls();
            object loaded = fixture.Load(store, schemas, rewritten);
            fixture.Check(loaded, 3);
            Assert.Equal(upgrades, fixture.UpgradeCalls());
            FixturePreparedWorldRevision noChange = fixture.Prepare(loaded);
            Assert.Empty(noChange.Revision.LocalObjects);
            Assert.Empty(noChange.Revision.RemovedObjectIds);
            unchanged = store.Append(noChange.Revision);
            object fresh = fixture.Load(store, schemas, unchanged);
            fixture.Change(fresh, 4);
            FixturePreparedWorldRevision update = fixture.Prepare(fresh);
            Assert.Equal(unchanged, update.Revision.ParentRevisionAddress);
            Assert.Equal(ObjectVersionKind.Delta, Assert.Single(update.Revision.LocalObjects).Kind);
            changed = store.Append(update.Revision);
            segments.Data.ConfirmDurable();
        }

        using (GraphResources segments = GraphResources.OpenLegacyReadOnlyExisting(directory.Path)) {
            SchemaStore schemas = segments.Schemas;
            using StateRevisionStore store = new(segments.Data);
            fixture.Check(fixture.Load(store, schemas, changed), 4);
            ObjectVersionChain chain = store.ReadObjectVersionChain(changed, 1);
            Assert.Equal(new[] { rewritten, changed }, chain.Records.Select(row => row.ContainingRevisionAddress));
            fixture.Check(fixture.Load(store, schemas, first), 1);
            fixture.Check(fixture.Load(store, schemas, third), 3);
            Assert.Equal<byte>(latest, fixture.ReadStored(store, schemas, third));
            Assert.Contains(20u, store.ReadLiveObjectHeadMap(third).Keys);

            var bytesBefore = Directory.EnumerateFiles(directory.Path, "*", SearchOption.AllDirectories)
                .ToDictionary(path => path, File.ReadAllBytes);
            Assert.Throws<InvalidOperationException>(() => store.Append(store.Read(third)));
            foreach ((string path, byte[] contents) in bytesBefore) Assert.Equal(contents, File.ReadAllBytes(path));
        }
        using (GraphResources segments = GraphResources.OpenLegacyExisting(directory.Path)) {
            using StateRevisionStore store = new(segments.Data);
            object oldOwner = fixture.Load(store, segments.Schemas, third);
            FixturePreparedWorldRevision beforeFailure = fixture.Prepare(oldOwner);
            using StateRevisionStore rejected = new(segments.Data);
            rejected.Dispose();
            Assert.Throws<ObjectDisposedException>(() => rejected.Append(beforeFailure.Revision));
            FixturePreparedWorldRevision afterFailure = fixture.Prepare(oldOwner);
            Assert.Equal(third, afterFailure.Revision.ParentRevisionAddress);
            Assert.Equal(ObjectVersionKind.Base, Assert.Single(afterFailure.Revision.LocalObjects).Kind);
            Assert.Equal(Assert.Single(beforeFailure.Revision.LocalObjects).Body.ToArray(),
                Assert.Single(afterFailure.Revision.LocalObjects).Body.ToArray());
            Assert.Equal<uint>([20], afterFailure.Revision.RemovedObjectIds);
        }
    }

    [Fact]
    public void LoadedWorldGeneratedEmptyAliasesProduceRealDeltaAndRemoveWithoutLosingNonemptyIdentity() {
        var fixture = CompileLoadedWorldFixture();
        using RawBaseDirectory directory = new();
        FrameAddress original, normalized;
        using (GraphResources segments = GraphResources.CreateLegacyNew(directory.Path)) {
            SchemaStore schemas = segments.Schemas;
            schemas.RegisterBatch([fixture.CurrentSchema]);
            using StateRevisionStore store = new(segments.Data);
            original = store.Append(StateRevision.CreateObjectHeadMapBase(null, [
                LoadedDurable(schemas, 1, fixture.CurrentSchema, [7, 10, 6, 10, 11, 10, 12, 13, .. new byte[16]]),
                LoadedText(10, "same"), LoadedText(11, "same"), LoadedText(12, ""), LoadedText(13, ""),
            ], []));
            segments.Data.ConfirmDurable();
        }
        using (GraphResources segments = GraphResources.OpenLegacyExisting(directory.Path)) {
            SchemaStore schemas = segments.Schemas;
            using StateRevisionStore store = new(segments.Data);
            object loaded = fixture.Load(store, schemas, original);
            fixture.Check(loaded, 3);
            Assert.Equal(0, fixture.UpgradeCalls());
            FixturePreparedWorldRevision plan = fixture.Prepare(loaded);
            Assert.Equal(original, plan.Revision.ParentRevisionAddress);
            Assert.Equal<uint>([13], plan.Revision.RemovedObjectIds);
            ObjectVersionRecord delta = Assert.Single(plan.Revision.LocalObjects);
            Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
            Assert.Equal(1u, delta.ObjectId);
            // Eighth body slot is the final Empty reference; two stable slots enlarge the mask to 2B.
            Assert.Equal<byte>([128, 0, 12], delta.Body.ToArray());
            normalized = store.Append(plan.Revision);
            segments.Data.ConfirmDurable();
        }
        using (GraphResources segments = GraphResources.OpenLegacyExisting(directory.Path)) {
            SchemaStore schemas = segments.Schemas;
            using StateRevisionStore store = new(segments.Data);
            fixture.Check(fixture.Load(store, schemas, original), 3);
            object loaded = fixture.Load(store, schemas, normalized);
            fixture.Check(loaded, 3);
            Assert.Equal<uint>([1, 10, 11, 12], store.ReadLiveObjectHeadMap(normalized).Keys.Order());
            Assert.Empty(fixture.Prepare(loaded).Revision.LocalObjects);
            Assert.Equal(2, store.ReadObjectVersionChain(normalized, 1).Records.Count);
        }
    }

    private static ObjectVersionRecord LoadedDurable(SchemaStore schemas, uint id, DurableSchema schema, byte[] body) =>
        ObjectVersionRecord.CreateBase(id, BaseObjectBodyCodec.Encode(schemas.RegisterRepresentations([ObjectLayout.ForDurable(schema)])[0], new(body)).Body);

    private static ObjectVersionRecord LoadedText(uint id, string text) =>
        ObjectVersionRecord.CreateBase(id, BaseObjectBodyCodec.EncodeString(StringPayloadCodec.PrepareBase(text)).Body);

    private static LoadedWorldFixture CompileLoadedWorldFixture() {
        using AncestryHistoryDirectory history = new();
        GeneratorTestRun initial = RunGenerator(FusedDeltaPreamble + """
            [DurableType("loaded.base", 1)]
            public abstract partial class OldBase : IDurableObject { [DurableField(9)] private string? _retired; }
            [DurableType("loaded.leaf", 1)]
            public sealed partial class Leaf : OldBase {
                [DurableField(1)] private int _number;
                [DurableField(2)] private string? _name;
                [DurableField(3)] private string? _alias;
                [DurableField(4)] private string? _shared;
                [DurableField(5)] private string? _empty;
                [DurableField(6)] private string? _emptyAgain;
            }
            """);
        AssertSchemaOnlyCompiles(initial);
        new SchemaHistoryTool().Publish(history.WriteManifest(initial), history.History);
        GeneratorTestRun current = RunGenerator(LoadedWorldCurrentSource, history.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(current);
        Assembly assembly = EmitAndLoad(current.OutputCompilation);
        Assert.Null(assembly.GetType("FusedDelta.OldBase"));
        Type leaf = assembly.GetType("FusedDelta.Leaf")!;
        return new(assembly.GetType("FusedDelta.Host")!, ReadSchemaOnly(leaf, 1), ReadSchemaOnly(leaf, 2));
    }

    private sealed class LoadedWorldFixture(Type host, DurableSchema oldSchema, DurableSchema currentSchema) {
        public Type Host { get; } = host;
        public DurableSchema OldSchema { get; } = oldSchema;
        public DurableSchema CurrentSchema { get; } = currentSchema;
        public Func<StateRevisionStore, SchemaStore, FrameAddress, object> Load { get; } =
            host.GetMethod("Load")!.CreateDelegate<Func<StateRevisionStore, SchemaStore, FrameAddress, object>>();
        public Func<object, FixturePreparedWorldRevision> Prepare { get; } =
            host.GetMethod("Prepare")!.CreateDelegate<Func<object, FixturePreparedWorldRevision>>();
        public Action<object, int> Check { get; } = host.GetMethod("Check")!.CreateDelegate<Action<object, int>>();
        public Action<object, int> Change { get; } = host.GetMethod("Change")!.CreateDelegate<Action<object, int>>();
        public Func<int> UpgradeCalls { get; } = host.GetMethod("UpgradeCalls")!.CreateDelegate<Func<int>>();
        public Func<StateRevisionStore, SchemaStore, FrameAddress, byte[]> ReadStored { get; } =
            host.GetMethod("ReadStored")!.CreateDelegate<Func<StateRevisionStore, SchemaStore, FrameAddress, byte[]>>();
    }

    private static readonly string LoadedWorldCurrentSource = FusedDeltaPreamble + """
        [DurableType("loaded.base", 2)]
        public abstract partial class NewBase : IDurableObject {
            public static int ConstructorCalls;
            [DurableField(2)] private readonly byte _marker;
            [DurableField(9)] private readonly string? _baseName;
            protected NewBase(string name) { ConstructorCalls++; _marker = 99; _baseName = name; }
            public byte Marker => _marker;
            public string? BaseName => _baseName;
        }
        [DurableType("loaded.leaf", 2)]
        public sealed partial class Leaf : NewBase {
            public static int Upgrades;
            [DurableField(1)] private int _number;
            [DurableField(2)] private string? _name;
            [DurableField(3)] private string? _alias;
            [DurableField(4)] private string? _shared;
            [DurableField(5)] private string? _empty;
            [DurableField(6)] private string? _emptyAgain;
            // Fixed address links cost 12B; real stable fields keep changed-object Delta economical.
            [DurableField(7)] private double _stableA;
            [DurableField(8)] private double _stableB;
            [Transient] private int _cache = 123;
            public Leaf(string name) : base(name) { ConstructorCalls++; _name = name; }
            public void Change(int number) { _number = number; }
            public void Check(int number) {
                if (_number != number || Marker != 7 || ConstructorCalls != 0 || _cache != 0 || _stableA != 0 || _stableB != 0)
                    throw new Exception("Wrong persisted values or constructor/initializer ran.");
                if (_name != "same" || _alias != "same" || ReferenceEquals(_name, _alias) ||
                    !ReferenceEquals(_name, _shared) || !ReferenceEquals(_name, BaseName) ||
                    !ReferenceEquals(_empty, string.Empty) || !ReferenceEquals(_emptyAgain, string.Empty))
                    throw new Exception("Shared/distinct string identity or Empty normalization changed.");
            }
            private static void UpgradeStateV1ToV2(in __DurableState.V1 old, out __DurableState.V2 next) {
                Upgrades++;
                next = new __DurableState.V2(7, old.Segment1Field2,
                    old.Segment1Field1, old.Segment1Field2, old.Segment1Field3,
                    old.Segment1Field4, old.Segment1Field5, old.Segment1Field6, 0, 0);
            }
        }
        public static class Host {
        """ + FusedDeltaHostMethods("Leaf", 1) + """
            public static object Load(Atelia.DurableGraph.Storage.StateRevisionStore store,
                Atelia.DurableGraph.Persistence.SchemaStore schemas, Atelia.FrameStore.FrameAddress revision) {
                var models = new Atelia.DurableGraph.Persistence.StateModelRegistry();
                Leaf.__DurableState.RegisterModel(models);
                return Atelia.DurableGraph.Tests.FixtureLoadedWorld.Load<Leaf>(store, schemas, revision, new(1), models);
            }
            public static Atelia.DurableGraph.Tests.FixturePreparedWorldRevision Prepare(object loaded) =>
                ((Atelia.DurableGraph.Tests.FixtureLoadedWorld<Leaf>)loaded).Prepare(new(1000000, 1));
            public static void Check(object loaded, int expected) =>
                ((Atelia.DurableGraph.Tests.FixtureLoadedWorld<Leaf>)loaded).World.Check(expected);
            public static void Change(object loaded, int value) =>
                ((Atelia.DurableGraph.Tests.FixtureLoadedWorld<Leaf>)loaded).World.Change(value);
            public static int UpgradeCalls() => Leaf.Upgrades;
            public static byte[] ReadStored(Atelia.DurableGraph.Storage.StateRevisionStore store,
                Atelia.DurableGraph.Persistence.SchemaStore schemas, Atelia.FrameStore.FrameAddress revision) {
                var readers = new Atelia.DurableGraph.Persistence.StateReaderRegistry();
                Leaf.__DurableState.RegisterReaders(readers);
                var decoded = Atelia.DurableGraph.Persistence.RevisionDecoder.Read(store, schemas, revision, readers);
                var old = decoded.GetRequired(new(1)).GetState<Leaf.__DurableState.V1>();
                return Leaf.__DurableState.PrepareBaseBody(in old).Body.ToArray();
            }
        }
        """;
}
