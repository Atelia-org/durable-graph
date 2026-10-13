extern alias persistence;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Schema;
using Atelia.FrameStore;
using persistence::Atelia.DurableGraph;
using persistence::Atelia.DurableGraph.Persistence;
using DataStore = Atelia.FrameStore.FrameStore;
using PublicationStore = Atelia.VersionStore.VersionStore;

namespace DevelopmentSchemaPackage;

internal static class Program {
    private const string Protocol = "DG.DevelopmentSchemaConsumer.v1";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly string[] Assemblies = ["Atelia.Binary", "Atelia.Data", "Atelia.DurableGraph",
        "Atelia.DurableGraph.Persistence", "Atelia.DurableGraph.Serialization", "Atelia.DurableGraph.Storage",
        "Atelia.FrameStore", "Atelia.Primitives", "Atelia.Rbf", "Atelia.VersionStore"];
#if SHAPE_V1
    private static string Shape => "V1";
#elif SHAPE_A
    private static string Shape => "A";
#elif SHAPE_B
    private static string Shape => "B";
#else
#error A known ShapeStage is required.
#endif
    private sealed record RefImage(ulong Ticket, string Payload);
    private sealed record RefSnapshot(RefImage Branches, RefImage Shared);
    private sealed record FrameImage(string Address, uint Tag, string PayloadSha256, string TailMetaSha256);
    private sealed class Witness {
        public string BaselineState { get; set; } = "";
        public uint RootId { get; set; }
        public RefImage SharedV1 { get; set; } = null!;
        public RefImage? SharedFinal { get; set; }
        public string? DraftAState { get; set; }
        public string? DraftBState { get; set; }
        public string? FinalState { get; set; }
        public string? ContinuationState { get; set; }
        public FrameImage[] Frames { get; set; } = [];
    }

    private static int Main(string[] args) {
        try {
            Need(args.Length == 3, "Expected phase, database and witness path.");
            string phase = args[0], database = Path.GetFullPath(args[1]), witnessPath = Path.GetFullPath(args[2]);
            object observations = phase switch {
                "seed" when Shape == "V1" => Seed(database, witnessPath),
                "draft-a" when Shape == "A" => Draft(database, witnessPath, "dev-a"),
                "draft-b" when Shape == "B" => Draft(database, witnessPath, "dev-b"),
                "final" when Shape == "B" => Final(database, witnessPath),
                "verify" when Shape == "B" => Verify(database, witnessPath),
                _ => throw new InvalidDataException("Unknown phase or wrong compiled shape."),
            };
            Assembly[] loaded = Assemblies.Select(Assembly.Load).ToArray();
            WriteJson(Path.Combine(Path.GetDirectoryName(witnessPath)!, phase + ".evidence.json"), new {
                Protocol, Phase = phase, Shape, Passed = true, ProcessId = Environment.ProcessId, Observations = observations,
                ActualRefs = ReadRefs(database),
                HostAssembly = AssemblyEvidence(typeof(Program).Assembly),
                LoadedAssemblies = loaded.Select(AssemblyEvidence).ToArray(),
            });
            Console.WriteLine("DevelopmentSchemaConsumer:" + phase + ":Passed");
            return 0;
        } catch (Exception exception) {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static object Seed(string database, string witnessPath) {
        Witness witness = new();
        using (Repository repository = Repository.CreateNew(database, Models(), schemaWriteMode: SchemaWriteMode.Shared)) {
            List<int> values = Enumerable.Range(0, 128).ToList();
            Widget root = new() { Value = 17, Numbers = values };
            root.Child = new() { Value = 19, Numbers = values, Child = root };
            using BranchCheckout branch = repository.CreateBranch("main", root);
            repository.CreateTag("baseline", branch.Head);
            Check(root, 17, 19, 7);
            witness.BaselineState = Hex(branch.Head.RevisionAddress);
            witness.RootId = branch.Head.RootId.Value;
        }
        witness.SharedV1 = ReadShared(database);
        witness.Frames = Frames(database);
        WriteJson(witnessPath, witness);
        return new { FixedV1Baseline = true, RingAndAlias = true, RootId = witness.RootId, witness.BaselineState, witness.SharedV1 };
    }

    private static object Draft(string database, string witnessPath, string branchName) {
        Witness witness = ReadWitness(witnessPath);
        Need(ReadShared(database) == witness.SharedV1, "Drafts must start with the same unchanged shared V1 ref.");
        using (Repository repository = Repository.OpenExisting(database, Models())) {
            CheckpointAddress baseline = repository.ResolveTag("baseline");
            Need(Hex(baseline.RevisionAddress) == witness.BaselineState, "The fixed baseline tag moved.");
            CheckpointAddress fork = repository.CreateBranch(branchName, baseline);
            Need(fork.RevisionAddress == baseline.RevisionAddress, "Ref-only creation did not select the baseline.");
            Widget.UpgradeCalls = 0;
            using BranchCheckout branch = repository.CheckoutPrivate(branchName);
            Need(branch.StateRevisionAddress == baseline.RevisionAddress && branch.StateId?.Value == witness.RootId,
                "The private checkout did not import the original State address/root identity.");
            Check(branch.State, BaseRoot, BaseChild, 7);
            Need(Widget.UpgradeCalls == 2, "Off must retain V1 history and perform both real V1-to-V2 Upgrades.");
            ApplyDraft((Widget)branch.State!);
            CheckpointAddress saved = branch.CommitState();
            IReadOnlyList<CheckpointAddress> history = repository.ReadFrames(branchName);
            Need(saved.RootId.Value == witness.RootId && history[^1] == saved &&
                history[^2].RevisionAddress == baseline.RevisionAddress,
                "The draft lost the baseline root ID or exact predecessor.");
            if (Shape == "A") { witness.DraftAState = Hex(saved.RevisionAddress); }
            else { witness.DraftBState = Hex(saved.RevisionAddress); }
        }
        Need(ReadShared(database) == witness.SharedV1, "Private draft saving changed shared revision/payload.");
        CheckFrames(database, witness.Frames);
        using (Repository cold = Repository.OpenReadOnlyExisting(database, Models())) {
            Need(Hex(cold.GetHead("main")!.RevisionAddress) == witness.BaselineState, "Private saving changed main.");
            Check(cold.ReadState(cold.GetHead(branchName)!), BaseRoot + 10, BaseChild + 10, 7001);
            Check(cold.ReadState(cold.ResolveTag("baseline")), BaseRoot, BaseChild, 7);
        }
        witness.Frames = Frames(database);
        WriteJson(witnessPath, witness);
        return new { BaselineIdentityImported = true, V1UpgradeWithOff = true, PrivateColdValues = true,
            SharedV1Unchanged = true, CompleteFramesPreserved = true, RootId = witness.RootId,
            DraftState = Shape == "A" ? witness.DraftAState : witness.DraftBState };
    }

    private static object Final(string database, string witnessPath) {
        Witness witness = ReadWitness(witnessPath);
        Need(ReadShared(database) == witness.SharedV1, "Neither draft may have accepted V2 into shared.");
        using (Repository repository = Repository.OpenExisting(database, Models())) {
            CheckpointAddress baseline = repository.ResolveTag("baseline");
            repository.CreateBranch("final", baseline);
            Widget.UpgradeCalls = 0;
            using BranchCheckout branch = repository.CheckoutPrivate("final");
            Need(branch.StateRevisionAddress == baseline.RevisionAddress && branch.StateId?.Value == witness.RootId,
                "The final branch must also import the fixed V1 State and identity.");
            Check(branch.State, BaseRoot, BaseChild, 7);
            Need(Widget.UpgradeCalls == 2, "Finalization must Upgrade V1, not inherit a draft V2 State.");
            ApplyDraft((Widget)branch.State!);
            CheckpointAddress saved = branch.CommitStateToShared();
            IReadOnlyList<CheckpointAddress> history = repository.ReadFrames("final");
            Need(saved.RootId.Value == witness.RootId && history[^1] == saved &&
                history[^2].RevisionAddress == baseline.RevisionAddress,
                "Finalization changed the original identity or content predecessor.");
            witness.FinalState = Hex(saved.RevisionAddress);
        }
        witness.SharedFinal = ReadShared(database);
        Need(witness.SharedFinal != witness.SharedV1, "Final B was not accepted by a changed shared ref.");
        CheckFrames(database, witness.Frames);
        using (Repository cold = Repository.OpenExisting(database, Models(), schemaWriteMode: SchemaWriteMode.Shared)) {
            using BranchCheckout branch = cold.Checkout("final");
            Need(Hex(branch.StateRevisionAddress!.Value) == witness.FinalState && branch.StateId?.Value == witness.RootId,
                "Cold shared continuation lost the final State or root identity.");
            Widget root = Check(branch.State, BaseRoot + 10, BaseChild + 10, 7001);
            root.Numbers[7] = 7002;
            CheckpointAddress continued = branch.CommitState();
            IReadOnlyList<CheckpointAddress> history = cold.ReadFrames("final");
            Need(continued.RootId.Value == witness.RootId && history[^1] == continued &&
                Hex(history[^2].RevisionAddress) == witness.FinalState,
                "Cold continuation did not continue the actual finalized State.");
            witness.ContinuationState = Hex(continued.RevisionAddress);
        }
        Need(ReadShared(database) == witness.SharedFinal, "Ordinary same-shape continuation changed shared.");
        CheckFrames(database, witness.Frames);
        witness.Frames = Frames(database);
        WriteJson(witnessPath, witness);
        return new { WinnerBOnlyPromoted = true, BaselineIdentityImported = true, FinalFromV1 = true,
            ColdSharedContinuation = true, CompleteFramesPreserved = true, witness.FinalState, witness.ContinuationState,
            witness.SharedV1, witness.SharedFinal };
    }

    private static object Verify(string database, string witnessPath) {
        Witness witness = ReadWitness(witnessPath);
        Dictionary<string, string> before = FileImages(database);
        using (Repository repository = Repository.OpenReadOnlyExisting(database, Models())) {
            Need(Hex(repository.ResolveTag("baseline").RevisionAddress) == witness.BaselineState, "The original tag changed.");
            Need(Hex(repository.GetHead("main")!.RevisionAddress) == witness.BaselineState &&
                Hex(repository.GetHead("dev-a")!.RevisionAddress) == witness.DraftAState &&
                Hex(repository.GetHead("dev-b")!.RevisionAddress) == witness.DraftBState,
                "A fixed main or private draft head changed.");
            Check(repository.ReadState(repository.ResolveTag("baseline")), BaseRoot, BaseChild, 7);
            Check(repository.ReadState(repository.GetHead("dev-b")!), BaseRoot + 10, BaseChild + 10, 7001);
            CheckpointAddress final = repository.GetHead("final")!;
            Need(Hex(final.RevisionAddress) == witness.ContinuationState && final.RootId.Value == witness.RootId,
                "The cold final history or original identity changed.");
            Check(repository.ReadState(final), BaseRoot + 10, BaseChild + 10, 7002);
            bool rejected = false;
            try { repository.ReadState(repository.GetHead("dev-a")!); }
            catch (InvalidDataException) { rejected = true; }
            Need(rejected, "The incompatible V2A private data was interpreted as V2B.");
        }
        Need(ReadShared(database) == witness.SharedFinal, "Readonly verification changed shared.");
        CheckFrames(database, witness.Frames);
        Need(SameFiles(before, FileImages(database)), "Readonly verification changed repository files.");
        return new { FixedBaselinePreserved = true, WinnerBColdValues = true, IncompatibleDraftRejected = true,
            ReadOnlyFilesUnchanged = true, CompleteFramesPreserved = true, witness.RootId };
    }

#if SHAPE_A
    private const double BaseRoot = 117, BaseChild = 119;
#elif SHAPE_B
    private const double BaseRoot = 217.5, BaseChild = 219.5;
#else
    private const double BaseRoot = 17, BaseChild = 19;
#endif
    private static void ApplyDraft(Widget root) {
        root.Value += 10;
        root.Child!.Value += 10;
        root.Numbers[7] = 7001;
    }
    private static Widget Check(IDurableObject? value, double rootValue, double childValue, int atSeven) {
        Need(value is Widget, "Unexpected root CLR type.");
        Widget root = (Widget)value!;
        Need(root.Value == rootValue && root.Child is { } child && child.Value == childValue &&
            ReferenceEquals(root, child.Child) && ReferenceEquals(root.Numbers, child.Numbers) && root.Numbers.Count == 128,
            "Values, ring or List alias disagree.");
        for (int i = 0; i < 128; i++) { Need(root.Numbers[i] == (i == 7 ? atSeven : i), "List contents disagree."); }
        return root;
    }
    private static StateModelRegistry Models() {
        StateModelRegistry models = new();
        global::Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
        return models;
    }
    private static RefImage ReadShared(string database) => ReadRefs(database).Shared;
    private static RefSnapshot ReadRefs(string database) {
        using DataStore data = DataStore.OpenReadOnly(Path.Combine(database, "data"));
        using PublicationStore versions = PublicationStore.OpenReadOnly(Path.Combine(database, "versions"), data);
        var branches = versions.ReadRef(1).Unwrap();
        var shared = versions.ReadRef(2).Unwrap();
        return new(new(branches.Revision.Ticket.Packed, Convert.ToHexString(branches.Payload)),
            new(shared.Revision.Ticket.Packed, Convert.ToHexString(shared.Payload)));
    }
    private static FrameImage[] Frames(string database) {
        using DataStore data = DataStore.OpenReadOnly(Path.Combine(database, "data"));
        List<FrameInfo> inventory = [];
        data.Inventory(inventory.Add).Unwrap();
        return inventory.Select(info => {
            using FrameRead frame = data.ReadFrame(info.Address).Unwrap();
            Need(!frame.IsTombstone, "A completed user frame became a tombstone.");
            return new FrameImage(Hex(info.Address), frame.Tag, Hash(frame.Payload), Hash(frame.TailMeta));
        }).OrderBy(frame => frame.Address, StringComparer.Ordinal).ToArray();
    }
    private static void CheckFrames(string database, IEnumerable<FrameImage> prior) {
        Dictionary<string, FrameImage> actual = Frames(database).ToDictionary(frame => frame.Address);
        foreach (FrameImage frame in prior) { Need(actual.TryGetValue(frame.Address, out FrameImage? current) && current == frame,
            "A complete frame disappeared or changed at its original address."); }
    }
    private static Dictionary<string, string> FileImages(string database) => Directory.GetFiles(database, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(database, path), path => Hash(File.ReadAllBytes(path)));
    private static bool SameFiles(Dictionary<string, string> before, Dictionary<string, string> after) =>
        before.Count == after.Count && before.All(pair => after.TryGetValue(pair.Key, out string? hash) && pair.Value == hash);
    private static object AssemblyEvidence(Assembly assembly) => new {
        Name = assembly.GetName().Name, Path = assembly.Location, Sha256 = Hash(File.ReadAllBytes(assembly.Location)),
    };
    private static Witness ReadWitness(string path) => JsonSerializer.Deserialize<Witness>(File.ReadAllText(path), Json)
        ?? throw new InvalidDataException("Missing witness.");
    private static void WriteJson(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Json));
    private static string Hex(FrameAddress address) {
        Span<byte> bytes = stackalloc byte[FrameAddress.EncodedSize];
        Need(address.TryWrite(bytes), "Invalid address.");
        return Convert.ToHexString(bytes);
    }
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static void Need(bool condition, string message) { if (!condition) { throw new InvalidDataException(message); } }
}
