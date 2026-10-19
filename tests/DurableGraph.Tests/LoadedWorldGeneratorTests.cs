using System.Reflection;
using Atelia.DurableGraph.Build;
using Atelia.DurableGraph.Persistence;

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
        using (Repository repository = Repository.CreateNew(directory.Path, fixture.OldModels(), rotationThreshold)) {
            object root = fixture.CreateOld();
            BranchId mainId = repository.CreateRootBranchFromState("main", (IDurableObject)root, parameters: ReferenceGraphNoRebase).Id;
            using WorkingCopy branch = repository.OpenWorkingCopy(mainId);
            first = EvidenceAddress(repository, branch.BaseCommitId);
            rootId = branch.StateId!.Value;
            repository.CreateTag("initial", branch.BaseCommitId);
            root = branch.State!;
            fixture.ChangeOld(root, 2);
            second = EvidenceAddress(repository, branch.CommitState(parameters: ReferenceGraphNoRebase).Id);
            fixture.ChangeOld(root, 3);
            third = EvidenceAddress(repository, branch.CommitState(parameters: ReferenceGraphNoRebase).Id);
            repository.CreateTag("old-latest", branch.BaseCommitId);
            GraphRecordStore store = ReferenceGraphStore(repository);
            Assert.Equal(ObjectVersionKind.Delta, Assert.Single(DefaultRecords(store, PublishedGraph(EvidenceResources(repository), second))).Kind);
            Assert.Equal(ObjectVersionKind.Delta, Assert.Single(DefaultRecords(store, PublishedGraph(EvidenceResources(repository), third))).Kind);
            Assert.Equal(3, store.ReadObjectChain(PublishedGraph(EvidenceResources(repository), third), rootId.Value).Entries.Count);
            DecodedRevision stored = DecodedRevision.ReadStoredGraph(store, PublishedGraph(EvidenceResources(repository), third), fixture.CurrentModels().FreezeConfiguration());
            retiredId = Assert.Single(stored.Objects, row => row.Kind == ObjectStateKind.String && row.StringContent == "retired ancestor field").Id.Value;
            sourceIds = PublishedGraph(EvidenceResources(repository), third).ObjectHeads.Keys.ToArray();
            Assert.Equal(5, sourceIds.Length); // Root, retired string, two distinct equal strings and Empty.
            Assert.Equal(3, fixture.ReadStoredNumber(stored.GetRequired(rootId)));
            Assert.Equal(0, fixture.UpgradeCalls()); // Stored-exact reads do not Upgrade.
            fixture.ChangeOld(root, 99); // Later CLR mutation cannot change completed frames.
        }
        using (Repository repository = Repository.OpenExisting(directory.Path, fixture.CurrentModels(), rotationThreshold)) {
            fixture.ResetConstructors();
            using WorkingCopy loaded = repository.OpenWorkingCopy(EvidenceBranch(repository, "main"));
            fixture.Check(loaded.State!, 3);
            Assert.Equal(1, fixture.UpgradeCalls()); // Upgrade runs after both historical Deltas.
            Assert.Equal(rootId, loaded.StateId);
            GraphRecordStore store = ReferenceGraphStore(repository);
            rewritten = EvidenceAddress(repository, loaded.CommitState(parameters: ReferenceGraphNoRebase).Id);
            Assert.Equal(third, PublishedGraph(EvidenceResources(repository), rewritten).SourceGraph);
            Assert.Equal<uint>([retiredId], PublishedRemoved(EvidenceResources(repository), rewritten));
            ObjectVersionRecord rewrite = Assert.Single(DefaultRecords(store, PublishedGraph(EvidenceResources(repository), rewritten)));
            Assert.Equal(ObjectVersionKind.Base, rewrite.Kind);
            Assert.Equal(rootId.Value, rewrite.ObjectId);
            Assert.Equal(2, DefaultLayout(store, PublishedGraph(EvidenceResources(repository), rewritten), rootId.Value).Schema!.Version);
            Assert.Single(store.ReadObjectChain(PublishedGraph(EvidenceResources(repository), rewritten), rootId.Value).Entries);
            Assert.Equal(sourceIds.Where(id => id != retiredId).Order(), PublishedGraph(EvidenceResources(repository), rewritten).ObjectHeads.Keys.Order());
            fixture.Change(loaded.State!, 99);
            fixture.Check(repository.LoadState(loaded.BaseCommitId), 3);
        }
        using (Repository repository = Repository.OpenExisting(directory.Path, fixture.CurrentModels(), rotationThreshold)) {
            fixture.ResetConstructors();
            int upgrades = fixture.UpgradeCalls();
            using WorkingCopy loaded = repository.OpenWorkingCopy(EvidenceBranch(repository, "main"));
            fixture.Check(loaded.State!, 3);
            Assert.Equal(upgrades, fixture.UpgradeCalls());
            Assert.Equal(rootId, loaded.StateId);
            GraphRecordStore store = ReferenceGraphStore(repository);
            unchanged = EvidenceAddress(repository, loaded.CommitState(parameters: ReferenceGraphNoRebase).Id);
            Assert.Empty(DefaultRecords(store, PublishedGraph(EvidenceResources(repository), unchanged)));
            Assert.Empty(PublishedRemoved(EvidenceResources(repository), unchanged));
            Assert.Equal(rewritten, PublishedGraph(EvidenceResources(repository), unchanged).SourceGraph);
            fixture.Change(loaded.State!, 4);
            changed = EvidenceAddress(repository, loaded.CommitState(parameters: ReferenceGraphNoRebase).Id);
            Assert.Equal(unchanged, PublishedGraph(EvidenceResources(repository), changed).SourceGraph);
            Assert.Equal(ObjectVersionKind.Delta, Assert.Single(DefaultRecords(store, PublishedGraph(EvidenceResources(repository), changed))).Kind);
        }
        var bytesBefore = Directory.EnumerateFiles(directory.Path, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes);
        using (Repository repository = Repository.OpenReadOnlyExisting(directory.Path, fixture.CurrentModels())) {
            fixture.ResetConstructors();
            fixture.Check(repository.LoadState(repository.ReadCurrentCommit(EvidenceBranch(repository, "main")).Commit.Id), 4);
            fixture.Check(repository.LoadState(repository.ResolveTag("initial")), 1);
            fixture.Check(repository.LoadState(repository.ResolveTag("old-latest")), 3);
            GraphRecordStore store = ReferenceGraphStore(repository);
            Assert.Equal(new[] { PublishedGraph(EvidenceResources(repository), rewritten).OwnedBatch, PublishedGraph(EvidenceResources(repository), changed).OwnedBatch },
                store.ReadObjectChain(PublishedGraph(EvidenceResources(repository), changed), rootId.Value).Entries.Select(row => (FrameAddress?)row.ContainingBatch));
            Assert.Contains(retiredId, PublishedGraph(EvidenceResources(repository), third).ObjectHeads.Keys);
            DecodedRevision stored = DecodedRevision.ReadStoredGraph(store, PublishedGraph(EvidenceResources(repository), third), fixture.CurrentModels().FreezeConfiguration());
            Assert.Equal(3, fixture.ReadStoredNumber(stored.GetRequired(rootId)));
            Assert.Throws<InvalidOperationException>(() => repository.OpenWorkingCopy(EvidenceBranch(repository, "main")));
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
        using (Repository repository = Repository.CreateNew(directory.Path, fixture.CurrentModels())) {
            BranchId mainId = repository.CreateRootBranchFromState("main", (IDurableObject)fixture.CreateCurrent(), parameters: ReferenceGraphNoRebase).Id;
            using WorkingCopy seed = repository.OpenWorkingCopy(mainId);
            original = EvidenceAddress(repository, seed.BaseCommitId);
            rootId = seed.StateId!.Value.Value;
            repository.CreateTag("original", seed.BaseCommitId);
            GraphRecordStore store = ReferenceGraphStore(repository);
            DecodedRevision source = DecodedRevision.ReadStoredGraph(store, PublishedGraph(EvidenceResources(repository), original), fixture.CurrentModels().FreezeConfiguration());
            ObjectStateRecord[] names = source.Objects.Where(row => row.Kind == ObjectStateKind.String && row.StringContent == "same").ToArray();
            Assert.Equal(2, names.Length);
            nameId = names.Min(row => row.Id.Value);
            aliasId = names.Max(row => row.Id.Value);
            emptyId = Assert.Single(source.Objects, row => row.Kind == ObjectStateKind.String && row.StringContent.Length == 0).Id.Value;
            Assert.Equal(4, source.Objects.Count); // Ordinary capture already canonicalizes both Empty references.
        }
        using (Repository repository = Repository.OpenExisting(directory.Path, fixture.CurrentModels())) {
            fixture.ResetConstructors();
            using WorkingCopy loaded = repository.OpenWorkingCopy(EvidenceBranch(repository, "main"));
            fixture.CheckCurrent(loaded.State!, 3);
            Assert.Equal(0, fixture.UpgradeCalls());
            GraphRecordStore store = ReferenceGraphStore(repository);
            CommitId unchanged = loaded.CommitState(parameters: ReferenceGraphNoRebase).Id;
            Assert.Empty(DefaultRecords(store, PublishedGraph(EvidenceResources(repository), EvidenceAddress(repository, unchanged))));
            Assert.Empty(PublishedRemoved(EvidenceResources(repository), EvidenceAddress(repository, unchanged)));
            fixture.MergeNames(loaded.State!);
            merged = EvidenceAddress(repository, loaded.CommitState(parameters: ReferenceGraphNoRebase).Id);
            Assert.Equal<uint>([aliasId], PublishedRemoved(EvidenceResources(repository), merged));
            ObjectVersionRecord delta = Assert.Single(DefaultRecords(store, PublishedGraph(EvidenceResources(repository), merged)));
            Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
            Assert.Equal(rootId, delta.ObjectId);
            Assert.Equal(new[] { rootId, nameId, emptyId }.Order(), PublishedGraph(EvidenceResources(repository), merged).ObjectHeads.Keys.Order());
            Assert.Equal(PublishedGraph(EvidenceResources(repository), original).ObjectHeads[nameId], PublishedGraph(EvidenceResources(repository), merged).ObjectHeads[nameId]);
            Assert.Equal(PublishedGraph(EvidenceResources(repository), original).ObjectHeads[emptyId], PublishedGraph(EvidenceResources(repository), merged).ObjectHeads[emptyId]);
        }
        using (Repository repository = Repository.OpenExisting(directory.Path, fixture.CurrentModels())) {
            fixture.ResetConstructors();
            fixture.CheckCurrent(repository.LoadState(repository.ResolveTag("original")), 3);
            using WorkingCopy loaded = repository.OpenWorkingCopy(EvidenceBranch(repository, "main"));
            fixture.CheckMerged(loaded.State!, 3);
            GraphRecordStore store = ReferenceGraphStore(repository);
            Assert.Equal(2, store.ReadObjectChain(PublishedGraph(EvidenceResources(repository), merged), rootId).Entries.Count);
            CommitId unchanged = loaded.CommitState(parameters: ReferenceGraphNoRebase).Id;
            Assert.Empty(DefaultRecords(store, PublishedGraph(EvidenceResources(repository), EvidenceAddress(repository, unchanged))));
            Assert.Empty(PublishedRemoved(EvidenceResources(repository), EvidenceAddress(repository, unchanged)));
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
