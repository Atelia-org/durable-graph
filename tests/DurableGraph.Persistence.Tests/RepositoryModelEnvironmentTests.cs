using System.Buffers;
using System.Reflection;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.RbfSegmentStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class RepositoryModelEnvironmentTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"durable-model-environment-{Guid.NewGuid():N}");
    private static readonly TypeExpr MapType = TypeExpr.Dictionary(TypeExpr.Builtin(TypeTag.String), TypeExpr.Builtin(TypeTag.Int32));
    private static readonly DurableSchema RootSchema = Schema("EnvironmentRoot");
    private static readonly DurableSchema EventSchema = Schema("EnvironmentEvent");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadPairReadAndResumeReuseSuccessfulClosuresWithinOneOpen(bool family) {
        Counts writes = new();
        StateModelRegistry writerModels = Models(family, writes);
        using (EventHistoryRepository repository = Create(writerModels)) {
            using (var session = repository.CreateBranch("main", Root(7))) {
                session.CommitDomainEvent(Root(9));
                session.CommitDomainState();
            }
            Assert.Equal(family ? 1 : 0, writes.Factories);
            Assert.Equal(1, writes.Comparers);
        }

        Counts reads = new();
        StateModelRegistry models = Models(family, reads);
        using (EventHistoryRepository repository = EventHistoryRepository.OpenExisting(_root, models)) {
            Assert.Equal(0, reads.Factories);
            Assert.Equal(0, reads.Comparers);
            GraphFrame head = repository.GetHead("main");
            GraphFrame domainEvent = Assert.Single(repository.ReadEvents("main"));
            for (int iteration = 0; iteration < 2; iteration++) {
                Assert.Equal(7, repository.ReadState<RootModel>(head).Values!["key"]);
                Assert.Equal(9, repository.ReadEvent<RootModel>(domainEvent).Values!["key"]);
                var pair = repository.ReadPair<RootModel, RootModel>(head, domainEvent);
                Assert.Equal(7, pair.First.Values!["key"]);
                Assert.Equal(9, pair.Second.Values!["key"]);
                using var session = repository.Resume<RootModel>("main");
                Assert.Equal(7, session.State.Values!["key"]);
            }
            Assert.Equal(family ? 1 : 0, reads.Factories);
            Assert.Equal(1, reads.Comparers);
        }
        using (EventHistoryRepository repository = EventHistoryRepository.OpenReadOnlyExisting(_root, models)) {
            Assert.Equal(7, repository.ReadState<RootModel>(repository.GetHead("main")).Values!["key"]);
        }
        Assert.Equal(family ? 2 : 0, reads.Factories);
        Assert.Equal(2, reads.Comparers);
    }

    [Fact]
    public void BuilderRegistrationAfterOpenIsInvisibleUntilNextOpen() {
        StateModelRegistry models = Models();
        using (EventHistoryRepository repository = Create(models)) {
            models.Register(Model<EventModel>(EventSchema));
            using var session = repository.CreateBranch("main", Root(7));
            GraphFrame before = session.Head;
            Assert.Throws<ArgumentException>(() => session.CommitDomainEvent(new EventModel()));
            Assert.Equal(before, session.Head);
            Assert.False(repository.IsFaulted);
        }
        using (EventHistoryRepository repository = EventHistoryRepository.OpenExisting(_root, models)) {
            using var session = repository.Resume<RootModel>("main");
            session.CommitDomainEvent(new EventModel());
        }
        using EventHistoryRepository reader = EventHistoryRepository.OpenReadOnlyExisting(_root, models);
        Assert.IsType<EventModel>(reader.ReadEvent<IDurableObject>(reader.GetHead("main")));
    }

    [Fact]
    public void EmptyAndEventOnlyCatalogsOpenLazilyAndReadOnlyInspectionDoesNotWrite() {
        StateModelRegistry all = Models();
        all.Register(Model<EventModel>(EventSchema));
        using (EventHistoryRepository repository = Create(all)) {
            using var session = repository.CreateBranch("main", Root(7));
            session.CommitDomainEvent(new EventModel());
        }
        var before = SnapshotFiles();
        using (EventHistoryRepository repository = EventHistoryRepository.OpenReadOnlyExisting(_root, new())) {
            Assert.Equal("main", Assert.Single(repository.ListBranches()));
            Assert.Equal(2, repository.ReadFrames("main").Count);
            Assert.Throws<InvalidDataException>(() => repository.ReadEvent<IDurableObject>(repository.GetHead("main")));
        }
        StateModelRegistry events = new();
        events.Register(Model<EventModel>(EventSchema));
        using (EventHistoryRepository repository = EventHistoryRepository.OpenReadOnlyExisting(_root, events)) {
            GraphFrame head = repository.GetHead("main");
            Assert.IsType<EventModel>(repository.ReadEvent<IDurableObject>(head));
            Assert.Throws<InvalidDataException>(() => repository.ReadState<IDurableObject>(repository.GetPreviousState(head)));
            Assert.Throws<InvalidOperationException>(() => repository.Resume<RootModel>("main"));
        }
        AssertFiles(before);
        using (EventHistoryRepository repository = EventHistoryRepository.OpenExisting(_root, events)) {
            Assert.Throws<InvalidDataException>(() => repository.Resume<RootModel>("main"));
            Assert.False(repository.IsFaulted);
            Assert.IsType<EventModel>(repository.ReadEvent<IDurableObject>(repository.GetHead("main")));
        }
        using EventHistoryRepository reopened = EventHistoryRepository.OpenExisting(_root, all);
        using var restored = reopened.Resume<RootModel>("main");
        Assert.Equal(7, restored.State.Values!["key"]);
    }

    [Fact]
    public void CachedModelStillRejectsConflictingSchemaPublishedAfterSuccessfulRead() {
        using (EventHistoryRepository repository = Create(Models())) {
            using var session = repository.CreateBranch("main", Root(7));
        }
        DurableSchema current = new(RootSchema.SchemaId, 2, DurableFieldInfo.Reference(1, MapType));
        StateModelRegistry models = new();
        models.Register(Model<RootModel>(current, Reader(RootSchema), Reader(current)));
        models.UseDictionaryComparer<string, int>(StringComparer.Ordinal);
        using EventHistoryRepository reader = EventHistoryRepository.OpenExisting(_root, models);
        GraphFrame head = reader.GetHead("main");
        Assert.Equal(7, reader.ReadState<RootModel>(head).Values!["key"]);
        // Reading v1 into the current v2 model closes v2 without persisting it. Make a
        // conflicting v2 authoritative afterwards; cache identity cannot waive revalidation.
        GraphResources resources = (GraphResources)typeof(EventHistoryRepository)
            .GetField("_resources", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(reader)!;
        resources.Schemas.Register(new DurableSchema(RootSchema.SchemaId, 2, new DurableFieldInfo(1, TypeTag.Int32)));
        Assert.Throws<InvalidDataException>(() => reader.ReadState<RootModel>(head));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void NullModelsAreRejectedBeforeAnyDirectoryOrResourceAcquisition(int entry) {
        ArgumentNullException error = Assert.Throws<ArgumentNullException>(() => {
            using EventHistoryRepository repository = entry switch {
                0 => EventHistoryRepository.CreateNew(_root, null!),
                1 => EventHistoryRepository.OpenExisting(_root, null!),
                _ => EventHistoryRepository.OpenReadOnlyExisting(_root, null!),
            };
        });
        Assert.Equal("models", error.ParamName);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void FailedOpenReleasesPreviouslyAcquiredJournalResources() {
        using (EventHistoryRepository repository = Create(Models())) {
            using var session = repository.CreateBranch("main", Root(7));
        }
        string schema = Path.Combine(_root, "schemas.rbf");
        string saved = Path.Combine(_root, "schemas.saved");
        File.Move(schema, saved);
        try {
            Assert.ThrowsAny<Exception>(() => EventHistoryRepository.OpenExisting(_root, Models()));
        } finally {
            File.Move(saved, schema);
        }
        using EventHistoryRepository repositoryAfterFailure = EventHistoryRepository.OpenExisting(_root, Models());
        using var resumed = repositoryAfterFailure.Resume<RootModel>("main");
        Assert.Equal(7, resumed.State.Values!["key"]);
    }

    private EventHistoryRepository Create(StateModelRegistry models) => EventHistoryRepository.CreateNew(_root, models,
        new() { NewStoreLayout = RbfSegmentStoreLayout.Flat });
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
        static (ref BinaryPayloadReader reader) => new(reader.ReadUInt32()),
        static (ref BinaryPayloadReader reader, in ObjectId prior) => new(reader.ReadUInt32()), Visit);
    private static void Visit(in ObjectId state, IStateReferenceVisitor visitor) => visitor.VisitObject(state, MapType);
    private static PreparedBaseBody WriteBase(in ObjectId state) {
        ArrayBufferWriter<byte> bytes = new();
        BinaryPayloadWriter writer = new(bytes);
        writer.WriteUInt32(state.Value);
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
