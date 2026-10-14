using System.Reflection;
using Atelia.DurableGraph.Build;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Persistence.MetaBootstrapP0;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Fact]
    public void LoadedWorldGeneratedHistoricalChainUpgradesRestoresAndResavesAcrossColdReopens() {
        LoadedWorldFixture fixture = CompileLoadedWorldFixture();
        using RawBaseDirectory directory = new();
        const long rotationThreshold = 128;
        FrameAddress first, second, third, rewritten, unchanged, changed;
        ObjectId rootId;
        uint retiredId;
        uint[] sourceIds;
        using (Repository repository = Repository.CreateNew(directory.Path, fixture.OldModels(), rotationThreshold, schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared)) {
            object root = fixture.CreateOld();
            using BranchCheckout branch = repository.CreateBranch("main", (IDurableObject)root, ReferenceGraphNoRebase);
            first = branch.Head.Address;
            rootId = branch.StateId!.Value;
            repository.CreateTag("initial", branch.Head);
            fixture.ChangeOld(root, 2);
            second = branch.CommitState(ReferenceGraphNoRebase).Address;
            fixture.ChangeOld(root, 3);
            third = branch.CommitState(ReferenceGraphNoRebase).Address;
            repository.CreateTag("old-latest", branch.Head);
            GraphRecordStore store = ReferenceGraphStore(repository);
            Assert.Equal(ObjectVersionKind.Delta, Assert.Single(DefaultRecords(store, second)).Kind);
            Assert.Equal(ObjectVersionKind.Delta, Assert.Single(DefaultRecords(store, third)).Kind);
            Assert.Equal(3, store.ReadObjectChain(third, rootId.Value).Entries.Count);
            DecodedRevision stored = DecodedRevision.ReadStoredGraph(store, third, fixture.CurrentModels());
            retiredId = Assert.Single(stored.Objects, row => row.Kind == ObjectStateKind.String && row.StringContent == "retired ancestor field").Id.Value;
            sourceIds = store.ReadGraph(third).ObjectHeads.Keys.ToArray();
            Assert.Equal(5, sourceIds.Length); // Root, retired string, two distinct equal strings and Empty.
            Assert.Equal(3, fixture.ReadStoredNumber(stored.GetRequired(rootId)));
            Assert.Equal(0, fixture.UpgradeCalls()); // Stored-exact reads do not Upgrade.
            fixture.ChangeOld(root, 99); // Later CLR mutation cannot change completed frames.
        }
        using (Repository repository = Repository.OpenExisting(directory.Path, fixture.CurrentModels(), rotationThreshold, schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared)) {
            fixture.ResetConstructors();
            using BranchCheckout loaded = repository.Checkout("main");
            fixture.Check(loaded.State!, 3);
            Assert.Equal(1, fixture.UpgradeCalls()); // Upgrade runs after both historical Deltas.
            Assert.Equal(rootId, loaded.StateId);
            GraphRecordStore store = ReferenceGraphStore(repository);
            rewritten = loaded.CommitState(ReferenceGraphNoRebase).Address;
            Assert.Equal(third, store.ReadGraph(rewritten).SourceGraph);
            Assert.Equal<uint>([retiredId], DefaultRemoved(store, rewritten));
            ObjectVersionRecord rewrite = Assert.Single(DefaultRecords(store, rewritten));
            Assert.Equal(ObjectVersionKind.Base, rewrite.Kind);
            Assert.Equal(rootId.Value, rewrite.ObjectId);
            Assert.Equal(2, DefaultLayout(store, rewritten, rootId.Value).Schema!.Version);
            Assert.Single(store.ReadObjectChain(rewritten, rootId.Value).Entries);
            Assert.Equal(sourceIds.Where(id => id != retiredId).Order(), store.ReadGraph(rewritten).ObjectHeads.Keys.Order());
            fixture.Change(loaded.State!, 99);
            fixture.Check(repository.ReadState(loaded.Head), 3);
        }
        using (Repository repository = Repository.OpenExisting(directory.Path, fixture.CurrentModels(), rotationThreshold, schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared)) {
            fixture.ResetConstructors();
            int upgrades = fixture.UpgradeCalls();
            using BranchCheckout loaded = repository.Checkout("main");
            fixture.Check(loaded.State!, 3);
            Assert.Equal(upgrades, fixture.UpgradeCalls());
            Assert.Equal(rootId, loaded.StateId);
            GraphRecordStore store = ReferenceGraphStore(repository);
            unchanged = loaded.CommitState(ReferenceGraphNoRebase).Address;
            Assert.Empty(DefaultRecords(store, unchanged));
            Assert.Empty(DefaultRemoved(store, unchanged));
            Assert.Equal(rewritten, store.ReadGraph(unchanged).SourceGraph);
            fixture.Change(loaded.State!, 4);
            changed = loaded.CommitState(ReferenceGraphNoRebase).Address;
            Assert.Equal(unchanged, store.ReadGraph(changed).SourceGraph);
            Assert.Equal(ObjectVersionKind.Delta, Assert.Single(DefaultRecords(store, changed)).Kind);
        }
        var bytesBefore = Directory.EnumerateFiles(directory.Path, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes);
        using (Repository repository = Repository.OpenReadOnlyExisting(directory.Path, fixture.CurrentModels())) {
            fixture.ResetConstructors();
            fixture.Check(repository.ReadState(repository.GetHead("main")), 4);
            fixture.Check(repository.ReadState(repository.ResolveTag("initial")), 1);
            fixture.Check(repository.ReadState(repository.ResolveTag("old-latest")), 3);
            GraphRecordStore store = ReferenceGraphStore(repository);
            Assert.Equal(new[] { store.ReadGraph(rewritten).OwnedBatch, store.ReadGraph(changed).OwnedBatch },
                store.ReadObjectChain(changed, rootId.Value).Entries.Select(row => (FrameAddress?)row.ContainingBatch));
            Assert.Contains(retiredId, store.ReadGraph(third).ObjectHeads.Keys);
            DecodedRevision stored = DecodedRevision.ReadStoredGraph(store, third, fixture.CurrentModels());
            Assert.Equal(3, fixture.ReadStoredNumber(stored.GetRequired(rootId)));
            Assert.Throws<InvalidOperationException>(() => repository.Checkout("main"));
        }
        Assert.Equal(bytesBefore.Keys.Order(), Directory.EnumerateFiles(directory.Path, "*", SearchOption.AllDirectories).Order());
        foreach ((string path, byte[] contents) in bytesBefore) { Assert.Equal(contents, File.ReadAllBytes(path)); }
    }

    [Fact]
    public void GeneratedDefaultRepositoryEmptyAliasesAndDistinctNamesSurviveColdDeltaAndRemove() {
        LoadedWorldFixture fixture = CompileLoadedWorldFixture();
        using RawBaseDirectory directory = new();
        FrameAddress original, merged;
        uint rootId, nameId, aliasId, emptyId;
        using (Repository repository = Repository.CreateNew(directory.Path, fixture.CurrentModels(), schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared)) {
            using BranchCheckout seed = repository.CreateBranch("main", (IDurableObject)fixture.CreateCurrent(), ReferenceGraphNoRebase);
            original = seed.Head.Address;
            rootId = seed.StateId!.Value.Value;
            repository.CreateTag("original", seed.Head);
            GraphRecordStore store = ReferenceGraphStore(repository);
            DecodedRevision source = DecodedRevision.ReadStoredGraph(store, original, fixture.CurrentModels());
            ObjectStateRecord[] names = source.Objects.Where(row => row.Kind == ObjectStateKind.String && row.StringContent == "same").ToArray();
            Assert.Equal(2, names.Length);
            nameId = names.Min(row => row.Id.Value);
            aliasId = names.Max(row => row.Id.Value);
            emptyId = Assert.Single(source.Objects, row => row.Kind == ObjectStateKind.String && row.StringContent.Length == 0).Id.Value;
            Assert.Equal(4, source.Objects.Count); // Ordinary capture already canonicalizes both Empty references.
        }
        using (Repository repository = Repository.OpenExisting(directory.Path, fixture.CurrentModels(), schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared)) {
            fixture.ResetConstructors();
            using BranchCheckout loaded = repository.Checkout("main");
            fixture.CheckCurrent(loaded.State!, 3);
            Assert.Equal(0, fixture.UpgradeCalls());
            GraphRecordStore store = ReferenceGraphStore(repository);
            CheckpointAddress unchanged = loaded.CommitState(ReferenceGraphNoRebase);
            Assert.Empty(DefaultRecords(store, unchanged.Address));
            Assert.Empty(DefaultRemoved(store, unchanged.Address));
            fixture.MergeNames(loaded.State!);
            merged = loaded.CommitState(ReferenceGraphNoRebase).Address;
            Assert.Equal<uint>([aliasId], DefaultRemoved(store, merged));
            ObjectVersionRecord delta = Assert.Single(DefaultRecords(store, merged));
            Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
            Assert.Equal(rootId, delta.ObjectId);
            Assert.Equal(new[] { rootId, nameId, emptyId }.Order(), store.ReadGraph(merged).ObjectHeads.Keys.Order());
            Assert.Equal(store.ReadGraph(original).ObjectHeads[nameId], store.ReadGraph(merged).ObjectHeads[nameId]);
            Assert.Equal(store.ReadGraph(original).ObjectHeads[emptyId], store.ReadGraph(merged).ObjectHeads[emptyId]);
        }
        using (Repository repository = Repository.OpenExisting(directory.Path, fixture.CurrentModels(), schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared)) {
            fixture.ResetConstructors();
            fixture.CheckCurrent(repository.ReadState(repository.ResolveTag("original")), 3);
            using BranchCheckout loaded = repository.Checkout("main");
            fixture.CheckMerged(loaded.State!, 3);
            GraphRecordStore store = ReferenceGraphStore(repository);
            Assert.Equal(2, store.ReadObjectChain(merged, rootId).Entries.Count);
            CheckpointAddress unchanged = loaded.CommitState(ReferenceGraphNoRebase);
            Assert.Empty(DefaultRecords(store, unchanged.Address));
            Assert.Empty(DefaultRemoved(store, unchanged.Address));
        }
    }

    private static LoadedWorldFixture CompileLoadedWorldFixture() {
        using AncestryHistoryDirectory history = new();
        SchemaHistoryTool tool = new();
        GeneratorTestRun initial = RunGenerator(LoadedWorldOldSource);
        AssertSchemaOnlyCompiles(initial);
        tool.Publish(history.WriteManifest(initial), history.History);
        Assembly oldAssembly = EmitAndLoad(initial.OutputCompilation);
        GeneratorTestRun current = RunGenerator(LoadedWorldCurrentSource, history.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(current);
        tool.Publish(history.WriteManifest(current), history.History);
        tool.Verify(history.WriteManifest(current), history.History);
        Assembly assembly = EmitAndLoad(current.OutputCompilation);
        Assert.Null(assembly.GetType("FusedDelta.OldBase"));
        return new(oldAssembly.GetType("FusedDelta.Host")!, assembly.GetType("FusedDelta.Host")!);
    }

    private sealed class LoadedWorldFixture(Type oldHost, Type host) {
        public StateModelRegistry OldModels() => oldHost.GetMethod("Models")!.CreateDelegate<Func<StateModelRegistry>>()();
        public StateModelRegistry CurrentModels() => host.GetMethod("Models")!.CreateDelegate<Func<StateModelRegistry>>()();
        public object CreateOld() => oldHost.GetMethod("Create")!.CreateDelegate<Func<object>>()();
        public object CreateCurrent() => host.GetMethod("Create")!.CreateDelegate<Func<object>>()();
        public void ResetConstructors() => host.Assembly.GetType("FusedDelta.NewBase")!.GetField("ConstructorCalls")!.SetValue(null, 0);
        public void ChangeOld(object root, int value) => oldHost.GetMethod("Change")!.CreateDelegate<Action<object, int>>()(root, value);
        public void Change(object root, int value) => host.GetMethod("Change")!.CreateDelegate<Action<object, int>>()(root, value);
        public void Check(object root, int value) => host.GetMethod("Check")!.CreateDelegate<Action<object, int>>()(root, value);
        public void CheckCurrent(object root, int value) => host.GetMethod("CheckCurrent")!.CreateDelegate<Action<object, int>>()(root, value);
        public void CheckMerged(object root, int value) => host.GetMethod("CheckMerged")!.CreateDelegate<Action<object, int>>()(root, value);
        public void MergeNames(object root) => host.GetMethod("MergeNames")!.CreateDelegate<Action<object>>()(root);
        public int UpgradeCalls() => host.GetMethod("UpgradeCalls")!.CreateDelegate<Func<int>>()();
        public int ReadStoredNumber(ObjectStateRecord row) => host.GetMethod("ReadStoredNumber")!.CreateDelegate<Func<ObjectStateRecord, int>>()(row);
    }

    private static readonly string LoadedWorldOldSource = FusedDeltaPreamble + """
        [DurableType("loaded.base", 1)]
        public abstract partial class OldBase : IDurableObject {
            [DurableField(9)] private string? _retired;
            protected OldBase() { _retired = "retired ancestor field"; }
        }
        [DurableType("loaded.leaf", 1)]
        public sealed partial class Leaf : OldBase {
            [DurableField(1)] private int _number;
            [DurableField(2)] private string? _name;
            [DurableField(3)] private string? _alias;
            [DurableField(4)] private string? _shared;
            [DurableField(5)] private string? _empty;
            [DurableField(6)] private string? _emptyAgain;
            // Stable real fields let the normal planner choose both historical Deltas.
            [DurableField(7)] private double _stableA;
            [DurableField(8)] private double _stableB;
            public Leaf() {
                _number = 1;
                _name = new string(new[] { 's', 'a', 'm', 'e' });
                _alias = new string(new[] { 's', 'a', 'm', 'e' });
                _shared = _name;
                _empty = _emptyAgain = string.Empty;
            }
            public void Change(int value) { _number = value; }
        }
        public static class Host {
            public static Atelia.DurableGraph.Persistence.StateModelRegistry Models() {
                var models = new Atelia.DurableGraph.Persistence.StateModelRegistry();
                Leaf.__DurableState.RegisterModel(models);
                return models;
            }
            public static object Create() => new Leaf();
            public static void Change(object root, int value) => ((Leaf)root).Change(value);
        }
        """;

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
            [DurableField(7)] private double _stableA;
            [DurableField(8)] private double _stableB;
            [Transient] private int _cache = 123;
            public Leaf(string name) : base(name) {
                ConstructorCalls++; _number = 3; _name = name;
                _alias = new string(new[] { 's', 'a', 'm', 'e' });
                _shared = _name; _empty = _emptyAgain = string.Empty;
            }
            public void Change(int number) { _number = number; }
            public void MergeNames() { _alias = _name; }
            public void Check(int number, byte marker = 7, bool separateNames = true) {
                if (_number != number || Marker != marker || ConstructorCalls != 0 || _cache != 0 || _stableA != 0 || _stableB != 0)
                    throw new Exception("Wrong persisted values or constructor/initializer ran.");
                if (_name != "same" || _alias != "same" || ReferenceEquals(_name, _alias) == separateNames ||
                    !ReferenceEquals(_name, _shared) || !ReferenceEquals(_name, BaseName) ||
                    !ReferenceEquals(_empty, string.Empty) || !ReferenceEquals(_emptyAgain, string.Empty))
                    throw new Exception("Shared/distinct string identity or Empty normalization changed.");
            }
            private static void UpgradeStateV1ToV2(in __DurableState.V1 old, out __DurableState.V2 next) {
                Upgrades++;
                next = new __DurableState.V2(7, old.Segment1Field2,
                    old.Segment1Field1, old.Segment1Field2, old.Segment1Field3,
                    old.Segment1Field4, old.Segment1Field5, old.Segment1Field6,
                    old.Segment1Field7, old.Segment1Field8);
            }
        }
        public static class Host {
            public static Atelia.DurableGraph.Persistence.StateModelRegistry Models() {
                var models = new Atelia.DurableGraph.Persistence.StateModelRegistry();
                Leaf.__DurableState.RegisterModel(models);
                return models;
            }
            public static object Create() => new Leaf(new string(new[] { 's', 'a', 'm', 'e' }));
            public static void Change(object root, int value) => ((Leaf)root).Change(value);
            public static void Check(object root, int value) => ((Leaf)root).Check(value);
            public static void CheckCurrent(object root, int value) => ((Leaf)root).Check(value, 99);
            public static void CheckMerged(object root, int value) => ((Leaf)root).Check(value, 99, false);
            public static void MergeNames(object root) => ((Leaf)root).MergeNames();
            public static int UpgradeCalls() => Leaf.Upgrades;
            public static int ReadStoredNumber(ObjectStateRecord row) => row.GetState<Leaf.__DurableState.V1>().Segment1Field1;
        }
        """;
}
