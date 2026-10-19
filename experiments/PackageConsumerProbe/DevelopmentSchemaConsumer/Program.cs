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
using PublicationStore = Atelia.HeadStore.HeadStore;

namespace DevelopmentSchemaPackage;

internal static class Program {
    private const string Protocol = "DG.DevelopmentSchemaConsumer.v2";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly string[] Assemblies = ["Atelia.Binary", "Atelia.Data", "Atelia.DurableGraph",
        "Atelia.DurableGraph.Persistence", "Atelia.DurableGraph.Serialization", "Atelia.DurableGraph.Storage",
        "Atelia.FrameStore", "Atelia.Primitives", "Atelia.Rbf", "Atelia.HeadStore"];
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
        using (Repository repository = Repository.CreateNew(database, Models())) {
            List<int> values = Enumerable.Range(0, 128).ToList();
            Widget root = new() { Value = 17, Numbers = values };
            root.Child = new() { Value = 19, Numbers = values, Child = root };
            using WorkingCopy branch = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", root).Id);
            repository.CreateTag("baseline", branch.BaseCommitId);
            Check(root, 17, 19, 7);
            witness.BaselineState = Hex(branch.BaseCommitId);
            witness.RootId = branch.StateId!.Value.Value;
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
            CommitId baseline = repository.ResolveTag("baseline");
            Need(Hex(baseline) == witness.BaselineState, "The fixed baseline tag moved.");
            BranchInfo fork = repository.ForkBranch(baseline, branchName);
            Need(repository.ReadCurrentCommit(fork.Id).Commit.Id == baseline, "Ref-only creation did not select the baseline.");
            Widget.UpgradeCalls = 0;
            using WorkingCopy branch = repository.OpenWorkingCopy(BranchNamed(repository, branchName));
            Need(branch.StateSourceCommitId == baseline && branch.StateId?.Value == witness.RootId,
                "The copied checkout did not retain the original State address/root identity.");
            Check(branch.State, BaseRoot, BaseChild, 7);
            Need(Widget.UpgradeCalls == 2, "Off must retain V1 history and perform both real V1-to-V2 Upgrades.");
            ApplyDraft((Widget)branch.State!);
            CommitId saved = branch.CommitState().Id;
            IReadOnlyList<CommitInfo> history = repository.ReadFrames(branch.BranchId);
            Need(branch.StateId!.Value.Value == witness.RootId && history[^1].Id == saved &&
                history[^2].Id == baseline,
                "The draft lost the baseline root ID or exact predecessor.");
            if (Shape == "A") { witness.DraftAState = Hex(saved); }
            else { witness.DraftBState = Hex(saved); }
        }
        Need(ReadShared(database) != witness.SharedV1, "The draft must accept its V2 into its own copied repository catalog.");
        CheckFrames(database, witness.Frames);
        using (Repository cold = Repository.OpenReadOnlyExisting(database, Models())) {
            Need(Hex(cold.ReadCurrentCommit(BranchNamed(cold, "main")).Commit.Id) == witness.BaselineState, "Draft saving changed the original branch.");
            Check(cold.LoadState(cold.ReadCurrentCommit(BranchNamed(cold, branchName)).Commit.Id), BaseRoot + 10, BaseChild + 10, 7001);
            Check(cold.LoadState(cold.ResolveTag("baseline")), BaseRoot, BaseChild, 7);
        }
        witness.Frames = Frames(database);
        WriteJson(witnessPath, witness);
        return new { BaselineIdentityImported = true, V1UpgradeWithOff = true, DraftColdValues = true,
            CopiedCatalogAdvanced = true, CompleteFramesPreserved = true, RootId = witness.RootId,
            DraftState = Shape == "A" ? witness.DraftAState : witness.DraftBState };
    }

    private static object Final(string database, string witnessPath) {
        Witness witness = ReadWitness(witnessPath);
        Need(ReadShared(database) == witness.SharedV1, "Release must start from the unchanged V1 repository copy.");
        using (Repository repository = Repository.OpenExisting(database, Models())) {
            CommitId baseline = repository.ResolveTag("baseline");
            repository.ForkBranch(baseline, "final");
            Widget.UpgradeCalls = 0;
            using WorkingCopy branch = repository.OpenWorkingCopy(BranchNamed(repository, "final"));
            Need(branch.StateSourceCommitId == baseline && branch.StateId?.Value == witness.RootId,
                "The final branch must also import the fixed V1 State and identity.");
            Check(branch.State, BaseRoot, BaseChild, 7);
            Need(Widget.UpgradeCalls == 2, "Finalization must Upgrade V1, not inherit a draft V2 State.");
            ApplyDraft((Widget)branch.State!);
            CommitId saved = branch.CommitState().Id;
            IReadOnlyList<CommitInfo> history = repository.ReadFrames(branch.BranchId);
            Need(branch.StateId!.Value.Value == witness.RootId && history[^1].Id == saved &&
                history[^2].Id == baseline,
                "Finalization changed the original identity or content predecessor.");
            witness.FinalState = Hex(saved);
        }
        witness.SharedFinal = ReadShared(database);
        Need(witness.SharedFinal != witness.SharedV1, "Final B was not accepted by a changed shared ref.");
        CheckFrames(database, witness.Frames);
        using (Repository cold = Repository.OpenExisting(database, Models())) {
            using WorkingCopy branch = cold.OpenWorkingCopy(BranchNamed(cold, "final"));
            Need(Hex(branch.StateSourceCommitId!.Value) == witness.FinalState && branch.StateId?.Value == witness.RootId,
                "Cold shared continuation lost the final State or root identity.");
            Widget root = Check(branch.State, BaseRoot + 10, BaseChild + 10, 7001);
            root.Numbers[7] = 7002;
            CommitId continued = branch.CommitState().Id;
            IReadOnlyList<CommitInfo> history = cold.ReadFrames(branch.BranchId);
            Need(branch.StateId!.Value.Value == witness.RootId && history[^1].Id == continued &&
                Hex(history[^2].Id) == witness.FinalState,
                "Cold continuation did not continue the actual finalized State.");
            witness.ContinuationState = Hex(continued);
        }
        Need(ReadShared(database) == witness.SharedFinal, "Ordinary same-shape continuation changed shared.");
        CheckFrames(database, witness.Frames);
        witness.Frames = Frames(database);
        WriteJson(witnessPath, witness);
        return new { WinnerBAcceptedFromCleanCopy = true, BaselineIdentityImported = true, FinalFromV1 = true,
            ColdSharedContinuation = true, CompleteFramesPreserved = true, witness.FinalState, witness.ContinuationState,
            witness.SharedV1, witness.SharedFinal };
    }

    private static object Verify(string database, string witnessPath) {
        Witness witness = ReadWitness(witnessPath);
        Dictionary<string, string> before = FileImages(database);
        using (Repository repository = Repository.OpenReadOnlyExisting(database, Models())) {
            Need(Hex(repository.ResolveTag("baseline")) == witness.BaselineState, "The original tag changed.");
            Need(Hex(repository.ReadCurrentCommit(BranchNamed(repository, "main")).Commit.Id) == witness.BaselineState,
                "The fixed original branch changed.");
            Check(repository.LoadState(repository.ResolveTag("baseline")), BaseRoot, BaseChild, 7);
            CommitId final = repository.ReadCurrentCommit(BranchNamed(repository, "final")).Commit.Id;
            Need(Hex(final) == witness.ContinuationState,
                "The cold final history or original identity changed.");
            Check(repository.LoadState(final), BaseRoot + 10, BaseChild + 10, 7002);
        }
        string draftA = Path.Combine(Path.GetDirectoryName(database)!, "database-a");
        Dictionary<string, string> draftBefore = FileImages(draftA);
        bool rejected = false;
        try {
            using Repository repository = Repository.OpenReadOnlyExisting(draftA, Models());
            repository.LoadState(repository.ReadCurrentCommit(BranchNamed(repository, "dev-a")).Commit.Id);
        } catch (InvalidDataException) { rejected = true; }
        Need(rejected, "The incompatible V2A copy was interpreted as V2B.");
        Need(SameFiles(draftBefore, FileImages(draftA)), "Rejected cross-model reading changed the draft copy.");
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
        var branches = versions.ReadHead(1).Unwrap();
        var shared = versions.ReadHead(2).Unwrap();
        return new(new(branches.Id.Ticket.Packed, Convert.ToHexString(branches.Payload)),
            new(shared.Id.Ticket.Packed, Convert.ToHexString(shared.Payload)));
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
    private static BranchId BranchNamed(Repository repository, string label) => repository.ListBranches().Single(branch => branch.DisplayLabel == label).Id;
    private static string Hex(CommitId id) {
        Span<byte> bytes = stackalloc byte[CommitId.EncodedSize];
        Need(id.TryWrite(bytes), "Invalid CommitId.");
        return Convert.ToHexString(bytes);
    }
    private static string Hex(FrameAddress address) {
        Span<byte> bytes = stackalloc byte[FrameAddress.EncodedSize];
        Need(address.TryWrite(bytes), "Invalid address.");
        return Convert.ToHexString(bytes);
    }
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static void Need(bool condition, string message) { if (!condition) { throw new InvalidDataException(message); } }
}
