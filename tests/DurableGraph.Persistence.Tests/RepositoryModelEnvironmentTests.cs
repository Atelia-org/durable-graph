using System.Buffers;
using System.Reflection;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class RepositoryModelEnvironmentTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"durable-model-environment-{Guid.NewGuid():N}");
    private static readonly TypeExpr MapType = TypeExpr.Dictionary(TypeExpr.Builtin(TypeTag.String), TypeExpr.Builtin(TypeTag.Int32));
    private static readonly DurableSchema RootSchema = Schema("EnvironmentRoot");
    private static readonly DurableSchema EventSchema = Schema("EnvironmentEvent");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadsRemainLazyAndAResumedExecutionEnvironmentReusesSuccessfulClosures(bool family) {
        Counts writes = new();
        StateModelRegistry writerModels = Models(family, writes);
        using (Repository repository = Create(writerModels)) {
            using (var session = repository.CreateBranch("main", Root(7))) {
                session.CommitEvent(Root(9));
                session.CommitState();
            }
            Assert.Equal(family ? 1 : 0, writes.Factories);
            Assert.Equal(1, writes.Comparers);
        }

        Counts reads = new();
        StateModelRegistry models = Models(family, reads);
        using (Repository repository = Repository.OpenExisting(_root, models, schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared)) {
            Assert.Equal(0, reads.Factories);
            Assert.Equal(0, reads.Comparers);
            CheckpointAddress head = repository.GetHead("main");
            CheckpointAddress domainEvent = Assert.Single(repository.EnumerateEvents(repository.GetHead("main"), HistoryOrder.OldestFirst).ToArray());
            for (int iteration = 0; iteration < 2; iteration++) {
                Assert.Equal(7, ((RootModel)repository.ReadState(head)).Values!["key"]);
                Assert.Equal(9, ((RootModel)repository.ReadEvent(domainEvent)).Values!["key"]);
                var pair = (((RootModel First, RootModel Second))repository.ReadPair(head, domainEvent));
                Assert.Equal(7, pair.First.Values!["key"]);
                Assert.Equal(9, pair.Second.Values!["key"]);
                using var session = repository.Checkout("main");
                Assert.Equal(7, ((RootModel)session.State!).Values!["key"]);
            }
            // Independent reads may close independent exact-S environments. The resident
            // State environment is reused by another checkout of the same State.
            int factories = reads.Factories, comparers = reads.Comparers;
            using var resumed = repository.Checkout("main");
            Assert.Equal(7, ((RootModel)resumed.State!).Values!["key"]);
            Assert.Equal(factories, reads.Factories);
            Assert.Equal(comparers, reads.Comparers);
            Assert.Equal(family, reads.Factories > 0);
            Assert.True(reads.Comparers > 0);
        }
        int previousFactories = reads.Factories, previousComparers = reads.Comparers;
        using (Repository repository = Repository.OpenReadOnlyExisting(_root, models)) {
            Assert.Equal(7, ((RootModel)repository.ReadState(repository.GetHead("main"))).Values!["key"]);
        }
        Assert.Equal(previousFactories + (family ? 1 : 0), reads.Factories);
        Assert.Equal(previousComparers + 1, reads.Comparers);
    }

    [Fact]
    public void BuilderRegistrationAfterOpenIsInvisibleUntilNextOpen() {
        StateModelRegistry models = Models();
        using (Repository repository = Create(models)) {
            models.Register(Model<EventModel>(EventSchema));
            using var session = repository.CreateBranch("main", Root(7));
            CheckpointAddress before = session.Head;
            Assert.Throws<ArgumentException>(() => session.CommitEvent(new EventModel()));
            Assert.Equal(before, session.Head);
            Assert.False(repository.IsFaulted);
        }
        using (Repository repository = Repository.OpenExisting(_root, models, schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared)) {
            using var session = repository.Checkout("main");
            session.CommitEvent(new EventModel());
        }
        using Repository reader = Repository.OpenReadOnlyExisting(_root, models);
        Assert.IsType<EventModel>(((IDurableObject)reader.ReadEvent(reader.GetHead("main"))));
    }

    [Fact]
    public void EmptyAndEventOnlyCatalogsOpenLazilyAndReadOnlyInspectionDoesNotWrite() {
        StateModelRegistry all = Models();
        all.Register(Model<EventModel>(EventSchema));
        using (Repository repository = Create(all)) {
            using var session = repository.CreateBranch("main", Root(7));
            session.CommitEvent(new EventModel());
        }
        var before = SnapshotFiles();
        using (Repository repository = Repository.OpenReadOnlyExisting(_root, new())) {
            Assert.Equal("main", Assert.Single(repository.ListBranches()));
            Assert.Equal(2, repository.ReadFrames("main").Count);
            Assert.Throws<InvalidDataException>(() => ((IDurableObject)repository.ReadEvent(repository.GetHead("main"))));
        }
        StateModelRegistry events = new();
        events.Register(Model<EventModel>(EventSchema));
        using (Repository repository = Repository.OpenReadOnlyExisting(_root, events)) {
            CheckpointAddress head = repository.GetHead("main");
            Assert.IsType<EventModel>(((IDurableObject)repository.ReadEvent(head)));
            Assert.Throws<InvalidDataException>(() => ((IDurableObject)repository.ReadState(Assert.IsType<CheckpointAddress>(repository.GetPreviousState(head)))));
            Assert.Throws<InvalidOperationException>(() => repository.Checkout("main"));
        }
        AssertFiles(before);
        using (Repository repository = Repository.OpenExisting(_root, events, schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared)) {
            Assert.Throws<InvalidDataException>(() => repository.Checkout("main"));
            Assert.False(repository.IsFaulted);
            Assert.IsType<EventModel>(((IDurableObject)repository.ReadEvent(repository.GetHead("main"))));
        }
        using Repository reopened = Repository.OpenExisting(_root, all, schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared);
        using var restored = reopened.Checkout("main");
        Assert.Equal(7, ((RootModel)restored.State!).Values!["key"]);
    }

    [Fact]
    public void HistoricalReadKeepsExactSAndSaveRejectsLatestCatalogConflict() {
        using (Repository repository = Create(Models())) {
            using var session = repository.CreateBranch("main", Root(7));
        }
        DurableSchema current = new(RootSchema.SchemaId, 2, DurableFieldInfo.Reference(1, MapType));
        StateModelRegistry models = new();
        models.Register(Model<RootModel>(current, Reader(RootSchema), Reader(current)));
        models.Register(Model<EventModel>(EventSchema));
        models.UseDictionaryComparer<string, int>(StringComparer.Ordinal);
        using Repository reader = Repository.OpenExisting(_root, models, schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared);
        CheckpointAddress head = reader.GetHead("main");
        Assert.Equal(7, ((RootModel)reader.ReadState(head)).Values!["key"]);
        // Publish the additional declaration jointly with an unrelated Event. Historical
        // reads retain S0; the next Save must still qualify its captured V2 against latest S.
        using BranchCheckout checkout = reader.Checkout("main");
        checkout.Catalog.Set(new DurableSchema(RootSchema.SchemaId, 2, new DurableFieldInfo(1, TypeTag.Int32)));
        checkout.CommitEvent(new EventModel());
        Assert.Equal(7, ((RootModel)reader.ReadState(head)).Values!["key"]);
        long frames = reader.GraphStore.AppendedFrames;
        Assert.Throws<SchemaConflictException>(() => checkout.CommitState());
        Assert.Equal(frames, reader.GraphStore.AppendedFrames);
        Assert.False(reader.IsFaulted);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void NullModelsAreRejectedBeforeAnyDirectoryOrResourceAcquisition(int entry) {
        ArgumentNullException error = Assert.Throws<ArgumentNullException>(() => {
            using Repository repository = entry switch {
                0 => Repository.CreateNew(_root, null!, schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared),
                1 => Repository.OpenExisting(_root, null!, schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared),
                _ => Repository.OpenReadOnlyExisting(_root, null!),
            };
        });
        Assert.Equal("models", error.ParamName);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void FailedRootOpenReleasesPreviouslyAcquiredBackingResources() {
        using (Repository repository = Create(Models())) {
            using var session = repository.CreateBranch("main", Root(7));
        }
        string schema = Directory.GetFiles(Path.Combine(_root, "versions", "refs"), "*.rbf", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).First();
        string saved = Path.Combine(_root, "root.saved");
        File.Move(schema, saved);
        try {
            Assert.ThrowsAny<Exception>(() => Repository.OpenExisting(_root, Models(), schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared));
        } finally {
            File.Move(saved, schema);
        }
        using Repository repositoryAfterFailure = Repository.OpenExisting(_root, Models(), schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared);
        using var resumed = repositoryAfterFailure.Checkout("main");
        Assert.Equal(7, ((RootModel)resumed.State!).Values!["key"]);
    }

    private Repository Create(StateModelRegistry models) => Repository.CreateNew(_root, models, schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared);
    private static RootModel Root(int value) => new() { Values = new(new StringProxy()) { ["key"] = value } };
    private static DurableSchema Schema(string id) => new(id, 1, DurableFieldInfo.Reference(1, MapType));
    private static StateModelRegistry Models(bool family = false, Counts? counts = null) {
        counts ??= new();
        StateModelRegistry models = new();
        if (family) {
            // Register only the declaration: each opened environment must lazily close this model.
            models.Register(new StateDefinitionBinding(RootSchema.SchemaId, SchemaKind.ReferenceObject, 0, typeof(RootModel),
                [new(RootSchema.SchemaId, 1, SchemaKind.ReferenceObject, 0, [new(1, MapType)])],
                currentModelFactory: (_, context) => {
                    counts.Factories++;
                    return Model<RootModel>(RootSchema, context.ResolveReader(RootSchema));
                }, historicalReaderFactory: static (schema, _) => Reader(schema)));
        } else {
            models.Register(Model<RootModel>(RootSchema));
        }
        models.UseDictionaryComparerResolver(type => {
            Assert.Equal(typeof(Dictionary<string, int>), type);
            counts.Comparers++;
            return StringComparer.Ordinal;
        });
        return models;
    }

    private static StateModelBinding<T, ObjectId> Model<T>(DurableSchema schema, params StateReaderBinding[] readers)
        where T : class, IRootModel, new() => new(new(schema, WriteBase,
            static (in ObjectId prior, in ObjectId current) => new(prior != current, WriteBase(in current).Body)),
            readers.Length == 0 ? [Reader(schema)] : readers, static row => row.GetState<ObjectId>(), static () => new(),
            static (T target, in ObjectId state, ObjectReadTable objects) => target.Values = objects.ResolveObject<Dictionary<string, int>>(state),
            static (source, context) => context.CaptureObject(source.Values, MapType), Visit);
    private static StateReaderBinding<ObjectId> Reader(DurableSchema schema) => new(schema,
        static (ref BareValueReader reader) => new(reader.ReadVarUInt32()),
        static (ref BareValueReader reader, in ObjectId prior) => new(reader.ReadVarUInt32()), Visit);
    private static void Visit(in ObjectId state, IStateReferenceVisitor visitor) => visitor.VisitObject(state, MapType);
    private static PreparedBaseBody WriteBase(in ObjectId state) {
        ArrayBufferWriter<byte> bytes = new();
        BareValueWriter writer = new(bytes);
        writer.WriteVarUInt32(state.Value);
        return new(bytes.WrittenSpan);
    }
    private interface IRootModel : IDurableObject { Dictionary<string, int>? Values { get; set; } }
    private sealed class RootModel : IRootModel { public RootModel() { } public Dictionary<string, int>? Values { get; set; } }
    private sealed class EventModel : IRootModel { public EventModel() { } public Dictionary<string, int>? Values { get; set; } }
    private sealed class Counts { internal int Factories; internal int Comparers; }
    private sealed class StringProxy : IEqualityComparer<string> {
        public bool Equals(string? left, string? right) => StringComparer.Ordinal.Equals(left, right);
        public int GetHashCode(string value) => StringComparer.Ordinal.GetHashCode(value);
    }
    private Dictionary<string, (byte[] Bytes, DateTime Modified)> SnapshotFiles() => Directory.GetFiles(_root, "*", SearchOption.AllDirectories)
        .ToDictionary(path => path, path => (File.ReadAllBytes(path), File.GetLastWriteTimeUtc(path)));
    private void AssertFiles(Dictionary<string, (byte[] Bytes, DateTime Modified)> before) {
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Order());
        foreach (var (path, saved) in before) {
            Assert.Equal(saved.Bytes, File.ReadAllBytes(path));
            Assert.Equal(saved.Modified, File.GetLastWriteTimeUtc(path));
        }
    }
    public void Dispose() {
        string resolved = Path.GetFullPath(_root);
        if (!string.Equals(Path.GetDirectoryName(resolved), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(resolved).StartsWith("durable-model-environment-", StringComparison.Ordinal)) {
            throw new InvalidOperationException("Unexpected fixture directory.");
        }
        if (Directory.Exists(resolved)) { Directory.Delete(resolved, recursive: true); }
    }
}
