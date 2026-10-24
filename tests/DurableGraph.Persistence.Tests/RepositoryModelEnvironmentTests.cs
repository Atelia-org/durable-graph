using static Atelia.DurableGraph.Persistence.Tests.DB096TestAccess;
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
            BranchInfo branch = repository.CreateRootBranchFromState("main", Root(7));
            Assert.Equal(family ? 1 : 0, writes.Factories);
            Assert.Equal(1, writes.Comparers);
            using (var session = repository.OpenWorkingCopy(branch.Id)) {
                // Creation publishes without retaining the application's object/environment.
                // Opening restores a fresh execution environment, which subsequent saves reuse.
                int factories = writes.Factories, comparers = writes.Comparers;
                Assert.Equal(family ? 2 : 0, factories);
                Assert.Equal(2, comparers);
                session.CommitEvent(Root(9));
                session.CommitState();
                Assert.Equal(factories, writes.Factories);
                Assert.Equal(comparers, writes.Comparers);
            }
        }

        Counts reads = new();
        StateModelRegistry models = Models(family, reads);
        using (Repository repository = Repository.OpenExisting(_root, models)) {
            Assert.Equal(0, reads.Factories);
            Assert.Equal(0, reads.Comparers);
            CommitId head = repository.ReadCurrentCommit(BranchNamed(repository, "main")).Commit.Id;
            CommitId domainEvent = Assert.Single(repository.EnumerateEvents(repository.ReadCurrentCommit(BranchNamed(repository, "main")).Commit.Id, HistoryOrder.OldestFirst).ToArray()).Id;
            for (int iteration = 0; iteration < 2; iteration++) {
                Assert.Equal(7, ((RootModel)repository.LoadState(head)).Values!["key"]);
                Assert.Equal(9, ((RootModel)repository.LoadEvent(domainEvent)).Values!["key"]);
                EventCheckpoint checkpoint = Assert.IsType<EventCheckpoint>(repository.ReadCheckpoint(domainEvent));
                Assert.Equal(7, Assert.IsType<RootModel>(checkpoint.PreviousState).Values!["key"]);
                Assert.Equal(9, Assert.IsType<RootModel>(checkpoint.Event).Values!["key"]);
                using var session = repository.OpenWorkingCopy(BranchNamed(repository, "main"));
                Assert.Equal(7, ((RootModel)session.State!).Values!["key"]);
            }
            // Independent reads may close independent exact-S environments. The resident
            // State environment is reused by another checkout of the same State.
            int factories = reads.Factories, comparers = reads.Comparers;
            using var resumed = repository.OpenWorkingCopy(BranchNamed(repository, "main"));
            Assert.Equal(7, ((RootModel)resumed.State!).Values!["key"]);
            Assert.Equal(factories, reads.Factories);
            Assert.Equal(comparers, reads.Comparers);
            Assert.Equal(family, reads.Factories > 0);
            Assert.True(reads.Comparers > 0);
        }
        int previousFactories = reads.Factories, previousComparers = reads.Comparers;
        using (Repository repository = Repository.OpenReadOnlyExisting(_root, models)) {
            Assert.Equal(7, ((RootModel)repository.LoadState(repository.ReadCurrentCommit(BranchNamed(repository, "main")).Commit.Id)).Values!["key"]);
        }
        Assert.Equal(previousFactories + (family ? 1 : 0), reads.Factories);
        Assert.Equal(previousComparers + 1, reads.Comparers);
    }

    [Fact]
    public void BuilderRegistrationAfterOpenIsInvisibleUntilNextOpen() {
        StateModelRegistry models = Models();
        using (Repository repository = Create(models)) {
            models.Register(Model<EventModel>(EventSchema));
            using var session = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", Root(7)).Id);
            CommitId before = session.BaseCommitId;
            Assert.Throws<ArgumentException>(() => session.CommitEvent(new EventModel()).Id);
            Assert.Equal(before, session.BaseCommitId);
            Assert.False(repository.IsFaulted);
        }
        using (Repository repository = Repository.OpenExisting(_root, models)) {
            using var session = repository.OpenWorkingCopy(BranchNamed(repository, "main"));
            session.CommitEvent(new EventModel());
        }
        using Repository reader = Repository.OpenReadOnlyExisting(_root, models);
        Assert.IsType<EventModel>(((IDurableObject)reader.LoadEvent(reader.ReadCurrentCommit(BranchNamed(reader, "main")).Commit.Id)));
    }

    [Fact]
    public void EmptyAndEventOnlyCatalogsOpenLazilyAndReadOnlyInspectionDoesNotWrite() {
        StateModelRegistry all = Models();
        all.Register(Model<EventModel>(EventSchema));
        using (Repository repository = Create(all)) {
            using var session = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", Root(7)).Id);
            session.CommitEvent(new EventModel());
        }
        var before = SnapshotFiles();
        using (Repository repository = Repository.OpenReadOnlyExisting(_root, new())) {
            Assert.Equal("main", Assert.Single(repository.ListBranches()).DisplayLabel);
            Assert.Equal(2, repository.ReadFrames(BranchNamed(repository, "main")).Count);
            Assert.Throws<InvalidDataException>(() => ((IDurableObject)repository.LoadEvent(repository.ReadCurrentCommit(BranchNamed(repository, "main")).Commit.Id)));
        }
        StateModelRegistry events = new();
        events.Register(Model<EventModel>(EventSchema));
        using (Repository repository = Repository.OpenReadOnlyExisting(_root, events)) {
            CommitId head = repository.ReadCurrentCommit(BranchNamed(repository, "main")).Commit.Id;
            Assert.IsType<EventModel>(((IDurableObject)repository.LoadEvent(head)));
            Assert.Throws<InvalidDataException>(() => ((IDurableObject)repository.LoadState(Assert.IsType<CommitId>(repository.GetPreviousState(head)))));
            Assert.Throws<InvalidOperationException>(() => repository.OpenWorkingCopy(BranchNamed(repository, "main")));
        }
        AssertFiles(before);
        using (Repository repository = Repository.OpenExisting(_root, events)) {
            Assert.Throws<InvalidDataException>(() => repository.OpenWorkingCopy(BranchNamed(repository, "main")));
            Assert.False(repository.IsFaulted);
            Assert.IsType<EventModel>(((IDurableObject)repository.LoadEvent(repository.ReadCurrentCommit(BranchNamed(repository, "main")).Commit.Id)));
        }
        using Repository reopened = Repository.OpenExisting(_root, all);
        using var restored = reopened.OpenWorkingCopy(BranchNamed(reopened, "main"));
        Assert.Equal(7, ((RootModel)restored.State!).Values!["key"]);
    }

    [Fact]
    public void HistoricalReadKeepsExactSAndSaveRejectsLatestCatalogConflict() {
        using (Repository repository = Create(Models())) {
            using var session = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", Root(7)).Id);
        }
        DurableSchema current = new(RootSchema.SchemaId, 2, DurableFieldInfo.Reference(1, MapType));
        StateModelRegistry models = new();
        models.Register(Model<RootModel>(current, Reader(RootSchema), Reader(current)));
        models.Register(Model<EventModel>(EventSchema));
        models.UseDictionaryComparer<string, int>(StringComparer.Ordinal);
        using Repository reader = Repository.OpenExisting(_root, models);
        CommitId head = reader.ReadCurrentCommit(BranchNamed(reader, "main")).Commit.Id;
        Assert.Equal(7, ((RootModel)reader.LoadState(head)).Values!["key"]);
        // Publish the additional declaration jointly with an unrelated Event. Historical
        // reads retain S0; the next Save must still qualify its captured V2 against latest S.
        using WorkingCopy checkout = reader.OpenWorkingCopy(BranchNamed(reader, "main"));
        reader.CommitBranches([checkout.CreateEventCommitRequest(new EventModel())],
            [new DurableSchema(RootSchema.SchemaId, 2, new DurableFieldInfo(1, TypeTag.Int32))]);
        Assert.Equal(7, ((RootModel)reader.LoadState(head)).Values!["key"]);
        long frames = reader.GraphStore.AppendedFrames;
        Assert.Throws<SchemaConflictException>(() => checkout.CommitState().Id);
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
                0 => Repository.CreateNew(_root, null!),
                1 => Repository.OpenExisting(_root, null!),
                _ => Repository.OpenReadOnlyExisting(_root, null!),
            };
        });
        Assert.Equal("models", error.ParamName);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void FailedRootOpenReleasesPreviouslyAcquiredBackingResources() {
        using (Repository repository = Create(Models())) {
            using var session = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", Root(7)).Id);
        }
        string schema = Directory.GetFiles(Path.Combine(_root, "versions", "refs"), "*.rbf", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).First();
        string saved = Path.Combine(_root, "root.saved");
        File.Move(schema, saved);
        try {
            Assert.ThrowsAny<Exception>(() => Repository.OpenExisting(_root, Models()));
        } finally {
            File.Move(saved, schema);
        }
        using Repository repositoryAfterFailure = Repository.OpenExisting(_root, Models());
        using var resumed = repositoryAfterFailure.OpenWorkingCopy(BranchNamed(repositoryAfterFailure, "main"));
        Assert.Equal(7, ((RootModel)resumed.State!).Values!["key"]);
    }

    private Repository Create(StateModelRegistry models) => Repository.CreateNew(_root, models);
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
