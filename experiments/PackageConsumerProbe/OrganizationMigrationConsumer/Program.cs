using Atelia.DurableGraph;
using __PERSISTENCE__;
using __STORAGE__;
using Atelia.Rbf;
using Atelia.RbfSegmentStore;
using System.Security.Cryptography;
using System.Text.Json;
using OrganizationMigrationConsumer;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;

if (args.Length != 2) throw new ArgumentException("Expected mode and directory.");
string mode = args[0], directory = Path.GetFullPath(args[1]);
bool legacy = __LEGACY__;
var options = new RbfSegmentStoreOptions { NewStoreLayout = RbfSegmentStoreLayout.Flat };
var models = new StateModelRegistry();
Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
foreach (var assembly in new[] { typeof(IDurableObject).Assembly, typeof(StateModelRegistry).Assembly,
    typeof(StateRevisionStore).Assembly, typeof(__SERIALIZATION__.BinaryPayloadReader).Assembly }) {
    Console.WriteLine($"Loaded:{assembly.FullName}:{assembly.Location}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location)))}");
}
Require(typeof(StateModelRegistry).Assembly.GetName().Name == (legacy ? "Atelia.DurableGraph.StateStore" : "Atelia.DurableGraph.Persistence"), "Incorrect persistence assembly.");
var budget = new ReadAmplificationBaseBudgetParameters(int.MaxValue, 1);
if (mode == "seed") {
    Require(legacy, "Seed requires the genuine legacy package.");
    using (var repository = Repository.CreateNew(directory, models, options)) {
        using (var session = repository.CreateBranch("complete", Make(100))) {
            session.CommitEvent(Make(3));
            ((World)session.State!).Value = 103;
            session.CommitState(budget);
        }
        repository.CreateBranch("pending", repository.ReadFrames("complete").Last());
        using var pending = repository.Checkout("pending");
        pending.CommitEvent(Make(7));
    }
    Check(false);
    Inventory(Path.Combine(directory, "..", "legacy-frames.json"));
} else if (mode == "read") {
    Require(!legacy, "Read requires migrated packages.");
    Check(false);
} else if (mode == "continue") {
    Require(!legacy, "Continuation requires migrated packages.");
    FrameAddress changed, unchanged;
    using (var repository = Repository.OpenExisting(directory, models, options)) {
        using var session = repository.Checkout("pending");
        Validate(((World)session.State!), 103);
        Require(repository.ReadEvent(session.Head) is World { Value: 7 }, "Old pending event missing.");
        Validate((World)repository.ReadEvent(session.Head)!, 7);
        ((World)session.State!).Value += ((World)repository.ReadEvent(session.Head)!).Value;
        changed = session.CommitState(budget).RevisionAddress;
        session.CommitEvent(Make(0));
        unchanged = session.CommitState(budget).RevisionAddress;
        Require(session.Head.Kind == GraphFrameKind.State, "Application completion did not publish State.");
    }
    using var segments = SegmentStore.OpenReadOnlyExisting(Path.Combine(directory, "state"), options);
    using var states = new StateRevisionStore(segments);
    Require(states.Read(changed).LocalObjects.Any(row => row.Kind == ObjectVersionKind.Delta), "Continuation did not produce Delta.");
    Require(states.Read(unchanged).LocalObjects.Count == 0, "Unchanged graph did not use NoChange.");
} else if (mode == "check") {
    Require(!legacy, "Cold final check requires migrated packages.");
    Check(true);
} else throw new ArgumentException("Unknown mode.");
Console.WriteLine($"OrganizationMigration:{mode}:Passed");

void Check(bool continued) {
    using var repository = Repository.OpenReadOnlyExisting(directory, models, options);
    var complete = repository.ReadFrames("complete");
    var pending = repository.ReadFrames("pending");
    Require(complete.Count == 3 && pending.Count == (continued ? 7 : 4), "Unexpected branch frame count.");
    Validate(((World)repository.ReadState(complete[0])), 100);
    Validate(((World)repository.ReadEvent(complete[1])), 3);
    Validate(((World)repository.ReadState(complete[2])), 103);
    Validate(((World)repository.ReadEvent(pending[3])), 7);
    if (continued) {
        Validate(((World)repository.ReadState(pending[4])), 110);
        Validate(((World)repository.ReadEvent(pending[5])), 0);
        Validate(((World)repository.ReadState(pending[6])), 110);
    }
    using var segments = SegmentStore.OpenReadOnlyExisting(Path.Combine(directory, "state"), options);
    using var states = new StateRevisionStore(segments);
    Require(states.Read(complete[0].RevisionAddress).LocalObjects.Any(row => row.Kind == ObjectVersionKind.Base), "Legacy Base missing.");
    Require(states.Read(complete[2].RevisionAddress).LocalObjects.Any(row => row.Kind == ObjectVersionKind.Delta), "Legacy Delta missing.");
}
void Inventory(string destination) {
    var inventory = new List<object>();
    foreach (string path in Directory.EnumerateFiles(directory, "*.rbf", SearchOption.AllDirectories).Order()) {
        byte[] bytes = File.ReadAllBytes(path);
        using var file = RbfFile.OpenReadOnlyExisting(path);
        var scanner = file.ScanForward(showTombstone: true).GetEnumerator();
        while (scanner.MoveNext()) {
            var frame = scanner.Current;
            int offset = checked((int)frame.Ticket.Offset), length = checked((int)frame.Ticket.Length);
            inventory.Add(new { Path = Path.GetRelativePath(directory, path), Offset = offset, Length = length,
                frame.Tag, SHA256 = Convert.ToHexString(SHA256.HashData(bytes.AsSpan(offset, length))) });
        }
        Require(scanner.TerminationError is null, $"Incomplete frame inventory: {path}: {scanner.TerminationError}");
    }
    Require(inventory.Count > 0, "No persisted frames found.");
    File.WriteAllText(destination, JsonSerializer.Serialize(inventory, new JsonSerializerOptions { WriteIndented = true }));
}
static World Make(long value) {
    var world = new World { Value = value, Position = new Point { X = 20, Y = 30 } };
    world.Self = world.Alias = world;
    world.Links.Add(world);
    world.Links.Add(world);
    world.Named.Add("self", world);
    return world;
}
static void Validate(World world, long value) {
    Require(world.Value == value && ReferenceEquals(world, world.Self) && ReferenceEquals(world, world.Alias), "Value/cycle/alias changed.");
    Require(world.Position.X == 20 && world.Position.Y == 30 && world.Numbers.SequenceEqual(new long[] { 1, 2, 3 }), "Inline or array changed.");
    Require(world.Links.Count == 2 && world.Links.All(item => ReferenceEquals(world, item)) && world.Named.Count == 1 && ReferenceEquals(world, world.Named["self"]), "Container aliases changed.");
    Require(world.A == 11 && world.B == 22 && world.C == 33 && world.D == 44, "Unchanged scalar fields changed.");
}
static void Require(bool condition, string message) {
    if (!condition) throw new InvalidOperationException(message);
}
