using Atelia.DurableGraph;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using Atelia.RbfSegmentStore;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;

namespace StateStorePackageConsumerProbe;

internal static class Program {
    private static void Main(string[] args) {
        if (args.Length != 1) { throw new ArgumentException("Pass one artifact directory."); }
        string artifactDirectory = Path.GetFullPath(args[0]);
#if RESTORE_V1
        World.Seed(Path.Combine(artifactDirectory, "restore"));
        Console.WriteLine("HistoricalWorldSeeded:True");
#else
        Character.Exercise(artifactDirectory);
        Console.WriteLine("PersistedSchema:True:InitialState:True:RawDelta:True:ColdTypedRead:True:SharedString:True:ConflictBeforeAppend:True:DecodedRevision:True");
#if RESTORE_V2
        World.Exercise(Path.Combine(artifactDirectory, "restore"));
        Console.WriteLine("HistoricalUpgrade:True:ConstructorFree:True:ReadonlyHydrate:True:ForcedBase:True:UnchangedResave:True:NormalDelta:True:ReopenedWorld:True");
        GraphWorld.Exercise(Path.Combine(artifactDirectory, "graph"));
        Console.WriteLine("InitialGraph:True:SharedDerived:True:ReadonlyCycles:True:ChildOnlyDelta:True:UnreachableCycleRemoved:True:HistoricalGraphPreserved:True");
#endif
#endif
    }
}

[DurableType("package.persisted-base", 1)]
public abstract partial class NamedObject : IDurableObject {
    [DurableField(1)] private string _name;
    protected NamedObject(string name) { _name = name; }
    protected string Name => _name;
}

[DurableType("package.persisted-character", 1)]
public sealed partial class Character : NamedObject {
    [DurableField(1)] private int _score;
    [DurableField(2)] private string _alias;
    [DurableField(3)] private readonly ulong _createdAtTicks;

    private Character(string name, int score) : base(name) { _score = score; _alias = name; _createdAtTicks = 638_625_600_000_000_000; }

    internal static void Exercise(string directory) {
        string repositoryPath = Path.Combine(directory, "character");
        string schemaPath = Path.Combine(repositoryPath, "schemas.rbf");
        string statePath = Path.Combine(repositoryPath, "state");
        FrameAddress firstRevision, secondRevision;
        ObjectId characterId, stringId;
        RbfSegmentStoreOptions options = new() { NewStoreLayout = RbfSegmentStoreLayout.Flat };
        ReadAmplificationBaseBudgetParameters policy = new(int.MaxValue, 1);
        StateModelRegistry models = new();
        __DurableState.RegisterModel(models);
        using (var repository = EventHistoryRepository.CreateNew(repositoryPath, models, options)) {
            string shared = new('A', 1);
            Character source = new(shared, 7);
            using var session = repository.CreateBranch("main", source, policy);
            firstRevision = session.StateRevisionAddress;
            characterId = session.StateId;
            session.CommitDomainEvent(new Character(shared, 7), policy);
            source._score = 8;
            secondRevision = session.CommitDomainState(policy).RevisionAddress;
            Require(ReferenceEquals(session.State, source), "Commit replaced application-held instances.");
        }
        using (var repository = EventHistoryRepository.OpenReadOnlyExisting(repositoryPath, models, options)) {
            var frames = repository.ReadFrames("main").ToArray();
            Character original = repository.ReadState<Character>(frames[0]);
            Require(original._score == 7 && original.Name == "A" &&
                original._createdAtTicks == 638_625_600_000_000_000 &&
                ReferenceEquals(original.Name, original._alias), "Historical State lost values or sharing.");
            var pair = repository.ReadPair<Character, Character>(frames[1], frames[2]);
            Require(pair.First._score == 7 && pair.Second._score == 8, "Event/State pair lost selected values.");
        }
        // Inspect exact payload and registration behavior after closing the facade's writer.
        using (var schemaFile = RbfFile.OpenExisting(schemaPath)) {
            SchemaStore schemas = new(schemaFile);
            Require(schemas.Count == 2 && schemas.GetRequired(Schema.SchemaId, 1).BaseSchema!.Equals(NamedObject.Schema),
                "Ancestor closure registration failed.");
            long registeredLength = new FileInfo(schemaPath).Length;
            schemas.RegisterBatch([Schema]);
            Require(new FileInfo(schemaPath).Length == registeredLength, "Idempotent registration appended bytes.");
            bool rejected = false;
            try { schemas.RegisterBatch([new DurableSchema("package.unpublished", 1, []), new DurableSchema(Schema.SchemaId, 1, [])]); }
            catch (SchemaConflictException) { rejected = true; }
            Require(rejected && schemas.Count == 2 && new FileInfo(schemaPath).Length == registeredLength,
                "Schema conflict must reject the complete batch before append.");
        }

        using var coldSchemaFile = RbfFile.OpenReadOnlyExisting(schemaPath);
        SchemaStore coldSchemas = new(coldSchemaFile, readOnly: true);
        using SegmentStore coldSegments = SegmentStore.OpenReadOnlyExisting(statePath, options);
        using StateRevisionStore cold = new(coldSegments);
        StateRevision first = cold.Read(firstRevision);
        stringId = new ObjectId(first.LocalObjectIds.Single(id => id != characterId.Value));
        Require(first.ParentRevisionAddress is null && first.LocalObjects.Count == 2 &&
            first.LocalObjects.All(record => record.Kind == ObjectVersionKind.Base), "Initial graph must contain two Bases.");
        StateRevision changed = cold.Read(secondRevision);
        Require(changed.ParentRevisionAddress == firstRevision && changed.LocalObjects.Count == 1 &&
            changed.RemovedObjectIds.Count == 0, "State must compare against S0, not the Event snapshot.");
        Require(coldSchemas.Count == 2 && coldSchemas.GetRequired(Schema.SchemaId, 1).BaseSchema!.Equals(NamedObject.Schema),
            "Cold Schema recovery lost its exact ancestor.");
        ObjectVersionChain characterChain = cold.ReadObjectVersionChain(secondRevision, characterId.Value);
        Require(characterChain.Records.Count == 2 && characterChain.Records[1].Record.Kind == ObjectVersionKind.Delta &&
            characterChain.Records[1].Record.Body.SequenceEqual(new byte[] { 2, 16 }),
            "Persisted Delta acquired a Base type header.");

        StateReaderRegistry readers = new();
        __DurableState.RegisterReaders(readers);
        __DurableState.RegisterReaders(readers); // Stable generated registration is idempotent.
        DecodedRevision current = RevisionDecoder.Read(cold, coldSchemas, secondRevision, readers);
        DecodedRevision previous = RevisionDecoder.Read(cold, coldSchemas, firstRevision, readers);
        Require(current.RevisionAddress == secondRevision && previous.RevisionAddress == firstRevision &&
            current.Objects.Count == 2 && previous.Objects.Count == 2 &&
            current.Objects.Select(row => row.Id).SequenceEqual(new[] { characterId, stringId }),
            "Revision decoding lost complete ordered live membership or its query address.");
        var currentState = current.GetRequired(characterId).GetState<__DurableState.V1>();
        var priorState = previous.GetRequired(characterId).GetState<__DurableState.V1>();
        Require(currentState.Segment1Field1 == 8 && priorState.Segment1Field1 == 7 &&
            currentState.Segment1Field3 == 638_625_600_000_000_000 && priorState.Segment1Field3 == 638_625_600_000_000_000,
            "Cold static body reconstruction ignored the selected revision.");
        Require(current.GetRequired(stringId).StringContent == "A", "Built-in string decoding failed.");
        StringReadTable strings = current.Strings;
        Require(ReferenceEquals(current.GetRequired(stringId).StringContent, strings.ResolveString(stringId)) &&
            ReferenceEquals(previous.GetRequired(stringId).StringContent, previous.Strings.ResolveString(stringId)) &&
            ReferenceEquals(strings.ResolveString(currentState.Segment0Field1), strings.ResolveString(currentState.Segment1Field2)),
            "Shared inherited and leaf string fields lost reference identity.");
    }

    private static void Require(bool condition, string message) {
        if (!condition) { throw new InvalidOperationException(message); }
    }
}
