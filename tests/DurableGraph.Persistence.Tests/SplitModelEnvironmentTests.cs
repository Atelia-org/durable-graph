using System.Buffers;
using Atelia.DurableGraph.Persistence.MetaBootstrapP0;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class SplitModelEnvironmentTests : IDisposable {
    private const string Prefix = "dg-split-model-environment-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
    private static readonly DurableSchema StoredRoot = new("SplitEnvironmentRoot", 1, new DurableFieldInfo(1, TypeTag.Int32));
    private static readonly DurableSchema CurrentRoot = new("SplitEnvironmentRoot", 2, new DurableFieldInfo(1, TypeTag.Int32));
    private static readonly DurableSchema Extra = new("SplitEnvironmentExtra", 1, new DurableFieldInfo(1, TypeTag.Int32));
    private static readonly TypeExpr MapType = TypeExpr.Dictionary(TypeExpr.Builtin(TypeTag.String), TypeExpr.Builtin(TypeTag.Int32));
    private static readonly DurableSchema MapRootSchema = new("SplitEnvironmentMapRoot", 1, DurableFieldInfo.Reference(1, MapType));
    private static readonly DurableSchema LateSchema = new("SplitEnvironmentLate", 1, new DurableFieldInfo(1, TypeTag.Int32));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FactoryConstructionAndDelayedNormalizeObserveTheirExactSelectedSchema(bool delayedLookup) {
        using GraphResources resources = GraphResources.CreateMetaBootstrapNew(_path);
        using P0Repository repository = new(resources);
        StateModelRegistry writes = new();
        writes.Register(Model(StoredRoot, [Reader(StoredRoot)], static row => row.GetState<int>()));
        P0Session writer = new(repository, "main", writes);
        ProbeRoot root = new() { Value = 7 };
        P0GraphSnapshot s0, s1;
        using (P0SessionSave save = writer.Prepare(root)) { s0 = save.Commit().Business; }
        writer.Catalog.Set(Extra);
        using (P0SessionSave save = writer.Prepare(root)) {
            Assert.Empty(save.Business.Write.Records);
            s1 = save.Commit().Business;
        }
        Assert.NotEqual(s0.SchemaRoot, s1.SchemaRoot);
        Assert.Equal(s0.RootId, s1.RootId);
        Assert.Equal(s0.Objects[s0.RootId.Value], s1.Objects[s1.RootId.Value]);
        Assert.False(P0CatalogView.Load(repository.Store, s0.SchemaRoot!.Value).Schemas.ContainsKey(new(Extra.Type, Extra.Version)));
        Assert.Equal(Extra, P0CatalogView.Load(repository.Store, s1.SchemaRoot!.Value).Schemas[new(Extra.Type, Extra.Version)]);

        long frames = repository.Store.AppendedFrames;
        Counts counts = new();
        StateModelRegistry reads = ProbeModels(delayedLookup, counts);
        StateModelRegistry.FrozenConfiguration frozen = reads.FreezeConfiguration();
        // Both sources are issued by the concrete reader from real graphs and their own pinned S.
        DecodedRevision source0 = DecodedRevision.ReadSplit(repository.Store, s0.Address, frozen);
        DecodedRevision source1 = DecodedRevision.ReadSplit(repository.Store, s1.Address, frozen);
        Assert.Same(repository.Store, source0.SplitStore);
        Assert.Same(repository.Store, source1.SplitStore);
        Assert.Equal(s0.SchemaRoot, source0.SelectedSchemaView!.Snapshot!.Address);
        Assert.Equal(s1.SchemaRoot, source1.SelectedSchemaView!.Snapshot!.Address);
        Assert.Equal(StoredRoot, Assert.Single(source0.Objects).Schema);
        Assert.Equal(StoredRoot, Assert.Single(source1.Objects).Schema);
        Assert.Equal(7, source0.GetRequired(s0.RootId).GetState<int>());
        Assert.Equal(7, source1.GetRequired(s1.RootId).GetState<int>());
        Assert.False(source0.SelectedSchemaView!.Schemas.ContainsKey(new(Extra.Type, Extra.Version)));
        Assert.True(source1.SelectedSchemaView!.Schemas.TryGetValue(new(Extra.Type, Extra.Version), out DurableSchema? selectedExtra));
        Assert.Equal(Extra, selectedExtra);
        // Missing target V2 is permitted for derived current code; neither source persists it.
        Assert.False(source0.SelectedSchemaView.Schemas.ContainsKey(new(CurrentRoot.Type, CurrentRoot.Version)));
        Assert.False(source1.SelectedSchemaView.Schemas.ContainsKey(new(CurrentRoot.Type, CurrentRoot.Version)));

        StateModelBinding binding0 = source0.SourceModels!.ResolveCurrentModel(typeof(ProbeRoot));
        StateModelBinding binding1 = source1.SourceModels!.ResolveCurrentModel(typeof(ProbeRoot));
        Assert.Equal(2, counts.Factories);
        Assert.Equal(delayedLookup ? [] : new[] { false, true }, counts.FactoryLookups);
        Assert.Throws<InvalidDataException>(() => NormalizedRevision.Create(source1, source0.SourceModels));
        Assert.Throws<InvalidDataException>(() => NormalizedRevision.Create(source0, source1.SourceModels));
        Assert.Throws<InvalidDataException>(() => DecodedRevision.ReadSplit(repository.Store, s1.Address, frozen, source0.BindingEnvironment));
        Assert.Throws<InvalidDataException>(() => DecodedRevision.ReadSplit(repository.Store, s0.Address, frozen, source1.BindingEnvironment));
        Assert.Equal(0, counts.Normalizes); // Product rejects misissued models before invoking either delegate.
        ProbeRoot restored0 = GraphReader.ReadRoot<ProbeRoot>(source0, s0.RootId, new(source0.SourceModels));
        ProbeRoot restored1 = GraphReader.ReadRoot<ProbeRoot>(source1, s1.RootId, new(source1.SourceModels));
        Assert.Equal(7, restored0.Value);
        Assert.Equal(107, restored1.Value);
        Assert.Equal(delayedLookup ? new[] { false, true } : [], counts.NormalizeLookups);
        Assert.Equal(2, counts.Normalizes); // Stored V1 -> current V2 really invokes historical Normalize.

        // This deliberately misuses a first-view binding as the rejected reuse proposal.
        // It neither signs a source nor installs a synthetic baseline into the product.
        ObjectStateRecord staleAtS1 = binding0.Normalize(source1.GetRequired(s1.RootId));
        Assert.Equal(CurrentRoot, staleAtS1.Schema);
        Assert.Equal(7, staleAtS1.GetState<int>());
        Assert.NotEqual(restored1.Value, staleAtS1.GetState<int>());
        ObjectStateRecord latestAtOldS0 = binding1.Normalize(source0.GetRequired(s0.RootId));
        Assert.Equal(107, latestAtOldS0.GetState<int>());
        Assert.NotEqual(restored0.Value, latestAtOldS0.GetState<int>());

        // Fresh per-S closure is the positive control for both query timings.
        foreach ((P0GraphSnapshot graph, int expected) in new[] { (s0, 7), (s1, 107) }) {
            Counts fresh = new();
            DecodedRevision source = DecodedRevision.ReadSplit(repository.Store, graph.Address, ProbeModels(delayedLookup, fresh));
            ProbeRoot restored = GraphReader.ReadRoot<ProbeRoot>(source, graph.RootId, new(source.SourceModels!));
            Assert.Equal(expected, restored.Value);
            Assert.Equal(1, fresh.Factories);
            Assert.Equal(1, fresh.Normalizes);
            Assert.Equal(expected == 107, Assert.Single(delayedLookup ? fresh.NormalizeLookups : fresh.FactoryLookups));
        }
        Assert.Equal(frames, repository.Store.AppendedFrames);
        Assert.Equal(s1.Address, repository.GetHead("main"));
        Assert.Equal(s1.Address, repository.GetStateHead("main"));
        Assert.Equal(s1.SchemaRoot, repository.SharedSchemaRoot);
        Assert.Equal(7, source0.GetRequired(s0.RootId).GetState<int>());
        Assert.Equal(7, source1.GetRequired(s1.RootId).GetState<int>());
    }

    [Fact]
    public void FrozenConfigurationCopiesRegistrationsAndPoliciesWhileExactSchemaReuseRemainsOwnerLocal() {
        FrameAddress first, second, schema;
        ObjectId rootId;
        using (GraphResources resources = GraphResources.CreateMetaBootstrapNew(_path)) {
            using P0Repository repository = new(resources);
            StateModelRegistry models = new();
            models.Register(MapModel(ReaderId(MapRootSchema)));
            models.UseDictionaryComparerResolver(static _ => StringComparer.Ordinal);
            P0Session writer = new(repository, "main", models);
            MapRoot root = new() { Values = new(new StringProxy()) { ["key"] = 7 } };
            using (P0SessionSave save = writer.Prepare(root)) {
                P0GraphSnapshot graph = save.Commit().Business;
                first = graph.Address;
                schema = graph.SchemaRoot!.Value;
                rootId = graph.RootId;
            }
            root.Values["key"] = 8;
            using (P0SessionSave save = writer.Prepare(root)) {
                Assert.Null(save.Catalog.Write);
                P0GraphSnapshot graph = save.Commit().Business;
                second = graph.Address;
                Assert.Equal(schema, graph.SchemaRoot);
            }
        }

        Counts counts = new();
        StateModelRegistry reads = MapModels(counts);
        reads.UseListDeltaAlgorithm(ListDeltaAlgorithm.Position);
        StateModelRegistry.FrozenConfiguration frozen = reads.FreezeConfiguration();
        Assert.Equal(0, counts.Factories);
        Assert.Equal(0, counts.Comparers);
        // Each choice changes after freezing, before any lazy closure has run.
        reads.Register(LateModel());
        reads.UseListDeltaAlgorithm(ListDeltaAlgorithm.LocalResync);
        reads.UseDictionaryComparer<string, int>(StringComparer.OrdinalIgnoreCase);
        StateModelRegistry.FrozenConfiguration changed = reads.FreezeConfiguration();
        Dictionary<string, byte[]> before = SnapshotFiles(_path);
        using (GraphResources resources = GraphResources.OpenMetaBootstrapReadOnlyExisting(_path)) {
            P0GraphStore store = resources.MetaBootstrapStore;
            DecodedRevision source1 = DecodedRevision.ReadSplit(store, first, frozen);
            Assert.NotNull(source1.BindingEnvironment);
            Assert.Equal(0, counts.Factories);
            Assert.Equal(0, counts.Comparers);
            Assert.False(source1.SourceModels!.TryGetCurrentModel(typeof(LateRoot), out _));
            MapRoot one = GraphReader.ReadRoot<MapRoot>(source1, rootId, new(source1.SourceModels));
            Assert.Equal(7, one.Values!["key"]);
            Assert.False(one.Values.ContainsKey("KEY")); // Frozen resolver, before a later exact comparer was added.
            Assert.Equal(ListDeltaAlgorithm.Position, BindList(source1.SourceModels).DeltaAlgorithm);
            Assert.Equal(1, counts.Factories);
            Assert.Equal(1, counts.Comparers);

            DecodedRevision source2 = DecodedRevision.ReadSplit(store, second, frozen, source1.BindingEnvironment);
            Assert.Same(source1.BindingEnvironment, source2.BindingEnvironment);
            Assert.Same(source1.SourceModels, source2.SourceModels);
            Assert.Same(source1.SelectedSchemaView, source2.SelectedSchemaView);
            MapRoot two = GraphReader.ReadRoot<MapRoot>(source2, rootId, new(source2.SourceModels!));
            Assert.Equal(8, two.Values!["key"]);
            Assert.False(two.Values.ContainsKey("KEY"));
            Assert.NotSame(one, two);
            Assert.NotSame(one.Values, two.Values);
            Assert.Equal(1, counts.Factories);
            Assert.Equal(1, counts.Comparers); // Successful lazy factory/comparer closures belong to this exact-S environment.

            DecodedRevision fresh = DecodedRevision.ReadSplit(store, first, frozen);
            Assert.NotSame(source1.BindingEnvironment, fresh.BindingEnvironment);
            Assert.NotSame(source1.SourceModels, fresh.SourceModels);
            Assert.Equal(7, GraphReader.ReadRoot<MapRoot>(fresh, rootId, new(fresh.SourceModels!)).Values!["key"]);
            Assert.Equal(2, counts.Factories);
            Assert.Equal(2, counts.Comparers); // No implicit all-history or owner-wide model cache.

            Assert.Throws<InvalidDataException>(() => DecodedRevision.ReadSplit(store, first, changed, source1.BindingEnvironment));
            Assert.Equal(2, counts.Factories);
            Assert.Equal(2, counts.Comparers);
            DecodedRevision newConfiguration = DecodedRevision.ReadSplit(store, first, changed);
            Assert.True(newConfiguration.SourceModels!.TryGetCurrentModel(typeof(LateRoot), out _));
            MapRoot newChoice = GraphReader.ReadRoot<MapRoot>(newConfiguration, rootId, new(newConfiguration.SourceModels));
            Assert.Equal(7, newChoice.Values!["KEY"]); // The new freeze really observes the exact new comparer.
            Assert.Equal(ListDeltaAlgorithm.LocalResync, BindList(newConfiguration.SourceModels).DeltaAlgorithm);
            Assert.Equal(3, counts.Factories);
            Assert.Equal(2, counts.Comparers); // An exact comparer supersedes the frozen fallback resolver.
            Assert.Equal(second, resources.GetMetaBootstrapHead("main"));
            Assert.Equal(second, resources.GetMetaBootstrapStateHead("main"));
            Assert.Equal(schema, resources.SharedMetaBootstrapRoot);

            resources.Dispose();
            Assert.ThrowsAny<InvalidOperationException>(() => DecodedRevision.ReadSplit(store, second, frozen, source1.BindingEnvironment));
            Assert.Equal(3, counts.Factories);
            Assert.Equal(2, counts.Comparers);
        }
        AssertFilesEqual(before, SnapshotFiles(_path));

        // Clone only while all resources are closed. Equal actual addresses cannot transfer environment ownership.
        string otherPath = _path + "-other";
        CopyClosedRepository(_path, otherPath);
        using GraphResources firstOwner = GraphResources.OpenMetaBootstrapReadOnlyExisting(_path);
        using GraphResources otherOwner = GraphResources.OpenMetaBootstrapReadOnlyExisting(otherPath);
        DecodedRevision owned = DecodedRevision.ReadSplit(firstOwner.MetaBootstrapStore, first, frozen);
        Assert.Equal(7, GraphReader.ReadRoot<MapRoot>(owned, rootId, new(owned.SourceModels!)).Values!["key"]);
        int factories = counts.Factories, comparers = counts.Comparers;
        Assert.Equal(schema, otherOwner.MetaBootstrapStore.ReadGraph(first).SchemaRoot);
        Assert.Throws<InvalidDataException>(() => DecodedRevision.ReadSplit(otherOwner.MetaBootstrapStore, first, frozen, owned.BindingEnvironment));
        Assert.Equal(factories, counts.Factories);
        Assert.Equal(comparers, counts.Comparers);
        DecodedRevision independent = DecodedRevision.ReadSplit(otherOwner.MetaBootstrapStore, first, frozen);
        MapRoot other = GraphReader.ReadRoot<MapRoot>(independent, rootId, new(independent.SourceModels!));
        Assert.Equal(7, other.Values!["key"]);
        Assert.False(other.Values.ContainsKey("KEY"));
        Assert.NotSame(owned.SourceModels, independent.SourceModels);
        Assert.Equal(factories + 1, counts.Factories);
        Assert.Equal(comparers + 1, counts.Comparers);
    }

    [Fact]
    public void ExactSelectedTargetConflictRejectsBeforeHistoricalNormalizeWhileTheOldSchemaStillReads() {
        using GraphResources resources = GraphResources.CreateMetaBootstrapNew(_path);
        using P0Repository repository = new(resources);
        StateModelRegistry writes = new();
        writes.Register(Model(StoredRoot, [Reader(StoredRoot)], static row => row.GetState<int>()));
        P0Session writer = new(repository, "main", writes);
        ProbeRoot root = new() { Value = 7 };
        P0GraphSnapshot good, bad;
        using (P0SessionSave save = writer.Prepare(root)) { good = save.Commit().Business; }
        writer.Catalog.Set(new DurableSchema(CurrentRoot.Type, CurrentRoot.Version, new DurableFieldInfo(1, TypeTag.Int64)));
        using (P0SessionSave save = writer.Prepare(root)) { bad = save.Commit().Business; }
        Counts counts = new();
        StateModelRegistry.FrozenConfiguration frozen = ProbeModels(delayedLookup: true, counts).FreezeConfiguration();
        DecodedRevision old = DecodedRevision.ReadSplit(repository.Store, good.Address, frozen);
        Assert.Equal(7, GraphReader.ReadRoot<ProbeRoot>(old, good.RootId, new(old.SourceModels!)).Value);
        int normalizes = counts.Normalizes;
        DecodedRevision conflict = DecodedRevision.ReadSplit(repository.Store, bad.Address, frozen);
        Assert.Throws<InvalidDataException>(() => GraphReader.ReadRoot<ProbeRoot>(conflict, bad.RootId, new(conflict.SourceModels!)));
        Assert.Equal(normalizes, counts.Normalizes);
        // A newer positive lookup cannot make an older graph's missing target borrow the latest S.
        Assert.False(old.SelectedSchemaView!.Schemas.ContainsKey(new(CurrentRoot.Type, CurrentRoot.Version)));
        Assert.Equal(7, GraphReader.ReadRoot<ProbeRoot>(old, good.RootId, new(old.SourceModels!)).Value);
        Assert.Equal(good.Objects[good.RootId.Value], bad.Objects[bad.RootId.Value]);
        Assert.Equal(StoredRoot, Assert.Single(conflict.Objects).Schema);
    }

    private static StateModelRegistry ProbeModels(bool delayedLookup, Counts counts) {
        StateModelRegistry models = new();
        models.Register(new StateDefinitionBinding(StoredRoot.SchemaId, SchemaKind.ReferenceObject, 0, typeof(ProbeRoot),
            [Template(1), Template(2)],
            currentModelFactory: (_, context) => {
                counts.Factories++;
                bool constructedWithExtra = !delayedLookup && LookupExtra(context, counts.FactoryLookups);
                return Model(CurrentRoot, [context.ResolveReader(StoredRoot), context.ResolveReader(CurrentRoot)], row => {
                    counts.Normalizes++;
                    bool hasExtra = delayedLookup ? LookupExtra(context, counts.NormalizeLookups) : constructedWithExtra;
                    return row.GetState<int>() + (hasExtra ? 100 : 0);
                });
            },
            historicalReaderFactory: static (schema, _) => Reader(schema)));
        return models;
    }

    private static bool LookupExtra(StateBindingContext context, List<bool> observations) {
        bool present = SchemaLookupCounterexampleProbe.TryGetSchema(context, Extra.Type, Extra.Version, out DurableSchema? schema);
        if (present) { Assert.Equal(Extra, schema); }
        observations.Add(present);
        return present;
    }

    private static StateSchemaTemplate Template(int version) => new(StoredRoot.SchemaId, version,
        SchemaKind.ReferenceObject, 0, [new(1, TypeExpr.Builtin(TypeTag.Int32))]);

    private static StateModelBinding<ProbeRoot, int> Model(DurableSchema current, StateReaderBinding[] readers,
        Func<ObjectStateRecord, int> normalize) => new(new(current, Base,
            static (in int prior, in int now) => new(prior != now, Base(in now).Body)), readers, normalize,
            static () => new(), static (ProbeRoot target, in int state, ObjectReadTable _) => target.Value = state,
            static (domain, _) => domain.Value, static (in int _, IStateReferenceVisitor _) => { });

    private static StateReaderBinding<int> Reader(DurableSchema schema) => new(schema,
        static (ref BareValueReader reader) => reader.ReadVarInt32(),
        static (ref BareValueReader reader, in int _) => reader.ReadVarInt32(),
        static (in int _, IStateReferenceVisitor _) => { });

    private static PreparedBaseBody Base(in int value) {
        ArrayBufferWriter<byte> bytes = new();
        BareValueWriter writer = new(bytes);
        writer.WriteVarInt32(value);
        return new(bytes.WrittenSpan);
    }

    private sealed class ProbeRoot : IDurableObject { internal int Value; }
    private sealed class LateRoot : IDurableObject { internal int Value; }
    private sealed class MapRoot : IDurableObject { internal Dictionary<string, int>? Values; }
    private sealed class StringProxy : IEqualityComparer<string> {
        public bool Equals(string? left, string? right) => StringComparer.Ordinal.Equals(left, right);
        public int GetHashCode(string value) => StringComparer.Ordinal.GetHashCode(value);
    }
    private sealed class Counts {
        internal int Factories;
        internal int Normalizes;
        internal int Comparers;
        internal List<bool> FactoryLookups { get; } = [];
        internal List<bool> NormalizeLookups { get; } = [];
    }

    private static StateModelRegistry MapModels(Counts counts) {
        StateModelRegistry models = new();
        models.Register(new StateDefinitionBinding(MapRootSchema.SchemaId, SchemaKind.ReferenceObject, 0, typeof(MapRoot),
            [new(MapRootSchema.SchemaId, 1, SchemaKind.ReferenceObject, 0, [new(1, MapType)])],
            currentModelFactory: (_, context) => {
                counts.Factories++;
                return MapModel(context.ResolveReader(MapRootSchema));
            }, historicalReaderFactory: static (schema, _) => ReaderId(schema)));
        models.UseDictionaryComparerResolver(type => {
            Assert.Equal(typeof(Dictionary<string, int>), type);
            counts.Comparers++;
            return StringComparer.Ordinal;
        });
        return models;
    }

    private static StateModelBinding<MapRoot, ObjectId> MapModel(StateReaderBinding reader) => new(new(MapRootSchema, BaseId,
        static (in ObjectId prior, in ObjectId now) => new(prior != now, BaseId(in now).Body)), [reader],
        static row => row.GetState<ObjectId>(), static () => new(),
        static (MapRoot target, in ObjectId state, ObjectReadTable objects) => target.Values = objects.ResolveObject<Dictionary<string, int>>(state),
        static (domain, context) => context.CaptureObject(domain.Values, MapType), VisitId);

    private static StateReaderBinding<ObjectId> ReaderId(DurableSchema schema) => new(schema,
        static (ref BareValueReader reader) => new(reader.ReadVarUInt32()),
        static (ref BareValueReader reader, in ObjectId _) => new(reader.ReadVarUInt32()), VisitId);
    private static void VisitId(in ObjectId value, IStateReferenceVisitor visitor) => visitor.VisitObject(value, MapType);
    private static PreparedBaseBody BaseId(in ObjectId value) {
        ArrayBufferWriter<byte> bytes = new();
        BareValueWriter writer = new(bytes);
        writer.WriteVarUInt32(value.Value);
        return new(bytes.WrittenSpan);
    }

    private static StateModelBinding<LateRoot, int> LateModel() => new(new(LateSchema, Base,
        static (in int prior, in int now) => new(prior != now, Base(in now).Body)), [Reader(LateSchema)],
        static row => row.GetState<int>(), static () => new(),
        static (LateRoot target, in int value, ObjectReadTable _) => target.Value = value,
        static (domain, _) => domain.Value, static (in int _, IStateReferenceVisitor _) => { });

    private static ListObjectBinding BindList(StateModelSnapshot models) {
        Assert.True(models.TryGetCurrentObjectBinding(typeof(List<int>), out ObjectBinding? binding));
        return Assert.IsAssignableFrom<ListObjectBinding>(binding);
    }

    private static Dictionary<string, byte[]> SnapshotFiles(string path) => Directory.GetFiles(path, "*", SearchOption.AllDirectories)
        .ToDictionary(name => Path.GetRelativePath(path, name), File.ReadAllBytes);
    private static void AssertFilesEqual(Dictionary<string, byte[]> expected, Dictionary<string, byte[]> actual) {
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach ((string name, byte[] bytes) in expected) { Assert.Equal(bytes, actual[name]); }
    }
    private static void CopyClosedRepository(string from, string to) {
        Directory.CreateDirectory(to);
        foreach (string directory in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories)) {
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, directory)));
        }
        foreach (string name in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories)) {
            File.Copy(name, Path.Combine(to, Path.GetRelativePath(from, name)));
        }
    }

    public void Dispose() {
        SharedReadModel.DeleteFixture(_path, Prefix);
        SharedReadModel.DeleteFixture(_path + "-other", Prefix);
    }
}
