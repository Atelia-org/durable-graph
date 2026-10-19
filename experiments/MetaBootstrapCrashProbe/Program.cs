extern alias persistence;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using Atelia.HeadStore;
using MetaBootstrapCrash;
using persistence::Atelia.DurableGraph;
using persistence::Atelia.DurableGraph.Persistence;

using DataStore = Atelia.FrameStore.FrameStore;
using PublicationStore = Atelia.HeadStore.HeadStore;

// READY is diagnostic evidence, never publication authority. Only a fresh owner
// reading the target business Head and shared Schema decides the selected pair after external termination.
internal static class Program {
    private const string Protocol = "DG.MetaBootstrapCrashProbe.v2";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly DurableSchema Extra = new("CrashExtra", 1, new DurableFieldInfo(1, TypeTag.Int64));
    private static readonly string[] Windows = ["AfterMetaAppend", "AfterBusinessAppend", "AfterFirstCuEnd",
        "AfterAllCuEnd", "AfterPublicationConfirmed", "BeforeInstall"];

    private sealed record ObjectEvidence(uint Id, string Head, long H);
    private sealed record FrameEvidence(string Address, uint Tag, string PayloadSha256, string TailMetaSha256);
    private sealed record ReadyEvidence(string Protocol, string Window, int ProcessId, string OldState, string OldSchema,
        uint RootId, uint ListId, ObjectEvidence[] Objects, string? CandidateMeta, string? CandidateBusiness,
        FrameEvidence[] OldFrames, FrameEvidence[] CapturedFrames, int CuEndsSeen, int BeforeCuFlushCalls, string PublicationStage,
        string TargetBranchId, string ExpectedBaseCommitId, string OldBranchDirectoryEntry, string OldBookmarkDirectoryEntry);
    private sealed record FileImage(string Path, string Sha256);
    private sealed record InitReadyEvidence(string Protocol, string Window, int ProcessId, uint CompletedRefId,
        uint[] CompletedRefs, FileImage[] FormalRefImages, int DeclaredUserDataFrames);

    private static int Main(string[] args) {
        if (args.Length < 1) { throw new ArgumentException("Expected seed, writer, verify, init-writer or init-verify."); }
        Dictionary<string, string> options = Parse(args);
        string repositoryPath = Path.GetFullPath(Required(options, "repo"));
        switch (args[0]) {
            case "seed": Seed(repositoryPath, Path.GetFullPath(Required(options, "result"))); break;
            case "writer": Writer(repositoryPath, Window(options), Path.GetFullPath(Required(options, "ready"))); break;
            case "verify": Verify(repositoryPath, Window(options), Path.GetFullPath(Required(options, "ready")),
                Path.GetFullPath(Required(options, "result"))); break;
            case "init-writer": InitWriter(repositoryPath, InitWindow(options), Path.GetFullPath(Required(options, "ready"))); break;
            case "init-verify": InitVerify(repositoryPath, InitWindow(options), Path.GetFullPath(Required(options, "ready")),
                Path.GetFullPath(Required(options, "result"))); break;
            default: throw new ArgumentException("Expected seed, writer, verify, init-writer or init-verify.");
        }
        Console.WriteLine("MetaBootstrapCrash:" + args[0] + ":Passed");
        return 0;
    }

    private static void InitWriter(string repositoryPath, string window, string readyPath) {
        uint target = window == "AfterFirstCreateHead" ? 1u : window == "AfterSecondCreateHead" ? 2u : 3u;
        List<uint> completed = [];
        using Repository unexpected = Repository.CreateStorageNew(repositoryPath, HostModel.Models(), afterCreateHead: localId => {
            completed.Add(localId);
            Need(localId == (uint)completed.Count, "The factory must complete Heads 1/2/3 in order.");
            if (localId != target) { return; }
            // CreateHead has returned: its private RBF was flushed, closed and renamed.
            // Only those closed formal control files are hashed here; the borrowed
            // FrameStore remains exclusive and no Repository has been delivered.
            string refsPath = Path.Combine(repositoryPath, "versions", "refs");
            FileImage[] files = Directory.GetFiles(refsPath, "*.rbf").Order(StringComparer.Ordinal)
                .Select(path => new FileImage(Path.GetRelativePath(repositoryPath, path), Hash(File.ReadAllBytes(path)))).ToArray();
            Need(files.Length == completed.Count, "Unexpected formal ref set at the factory checkpoint.");
            Need(Directory.GetFiles(Path.Combine(repositoryPath, "versions", "creating")).Length == 0,
                "A successful CreateHead must not leave private pending output.");
            // This is a source-informed declaration, not an active FS inventory.
            // The fresh verifier independently measures the actual user-frame count.
            Wait(new InitReadyEvidence(Protocol, window, Environment.ProcessId, localId,
                completed.ToArray(), files, DeclaredUserDataFrames: 0), readyPath);
        });
        throw new InvalidOperationException("The factory checkpoint was not reached; initialization writer must be killed externally.");
    }

    private static void InitVerify(string repositoryPath, string window, string readyPath, string resultPath) {
        InitReadyEvidence ready = JsonSerializer.Deserialize<InitReadyEvidence>(File.ReadAllText(readyPath), Json)
            ?? throw new InvalidDataException("Missing initialization READY evidence.");
        bool complete = window == "AfterThirdCreateHead";
        uint[] refs = complete ? [1, 2, 3] : window == "AfterSecondCreateHead" ? [1, 2] : [1];
        Need(ready.Protocol == Protocol && ready.Window == window && ready.ProcessId != Environment.ProcessId &&
            ready.CompletedRefId == refs[^1] && ready.CompletedRefs.SequenceEqual(refs) && ready.DeclaredUserDataFrames == 0,
            "Initialization READY must describe the exact successful factory returns in another process.");
        Dictionary<string, string> initialFiles = SnapshotFiles(repositoryPath);
        Need(ready.FormalRefImages.Length == refs.Length, "Wrong formal ref image count.");
        foreach (FileImage image in ready.FormalRefImages) {
            Need(initialFiles.TryGetValue(image.Path, out string? hash) && hash == image.Sha256,
                "A successful factory's formal ref file changed after external Kill.");
        }
        if (complete) {
            using (Repository reader = Repository.OpenStorageReadOnlyExisting(repositoryPath, HostModel.Models())) {
                RepositoryResources resources = Resources(reader);
                Need(reader.IsReadOnly && reader.ListBranches().Length == 0 && reader.SharedSchemaRoot is null,
                    "Three completed factory Heads must open as an empty readonly DG repository.");
                CheckEmptyInitializationRoots(resources.Heads, 3);
                Need(Inventory(resources).Length == 0, "The initialization factory must not contain user data frames.");
            }
        } else { RejectIncompleteFactory(repositoryPath, readOnly: true); }
        Need(SameFiles(initialFiles, SnapshotFiles(repositoryPath)), "Readonly factory verification changed files.");

        if (!complete) {
            RejectIncompleteFactory(repositoryPath, readOnly: false);
            Need(SameFiles(initialFiles, SnapshotFiles(repositoryPath)), "Writable DG Open must not complete or delete an initialized formal root.");
        }
        // Both fresh exclusive acquisitions must succeed after the failed DG opens
        // (or successful readonly close). This also independently validates actual
        // formal roots and the zero user-frame declaration without a DG cache.
        using (DataStore data = DataStore.Open(Path.Combine(repositoryPath, "data")))
        using (PublicationStore versions = PublicationStore.Open(Path.Combine(repositoryPath, "versions"), data)) {
            Need(versions.ReservedHeadIdUpperBound == 3 && versions.ListHeads().Unwrap().Select(id => versions.GetLocalHeadId(id).Unwrap()).Order().SequenceEqual(refs),
                "Fresh exclusive backing must expose precisely the completed formal refs.");
            CheckEmptyInitializationRoots(versions, refs.Length);
            int userFrames = 0;
            data.Inventory(_ => userFrames++).Unwrap();
            Need(userFrames == 0, "Actual initialized data has unexpected user frames.");
            Need(data.RecoveryReports.Values.All(report => report.Action == Atelia.Rbf.RbfTailRecoveryAction.None) && versions.RecoveryReports.Count == 0,
                "After-successful-CreateHead Kill must not require backing tail repair.");
        }
        Need(SameFiles(initialFiles, SnapshotFiles(repositoryPath)), "Initialization verification changed completed data or formal ref bytes.");
        string? seedState = null, schema = null, continuationState = null;
        ObjectEvidence[] objects = [];
        if (complete) {
            (FrameAddress state, FrameAddress selectedSchema, FrameAddress next, ObjectEvidence[] storage) =
                SeedEmptyInitializationThenColdContinue(repositoryPath);
            seedState = Encode(state);
            schema = Encode(selectedSchema);
            continuationState = Encode(next);
            objects = storage;
        }
        WriteJson(resultPath, new { Protocol, Lane = "init-verify", Passed = true, ProcessId = Environment.ProcessId,
            WriterProcessId = ready.ProcessId, Window = window,
            ObservedInitialization = complete ? "CompleteEmptyRoots" : refs.Length == 1 ? "MissingSecondHead" : "MissingThirdHead",
            InitialUserDataFrames = 0, FileBytesPreserved = true, ReadOnlyFilesUnchanged = true, LocksReleased = true,
            SingleDelta = complete, ExactPrior = complete, IdsPreserved = complete, HVerified = complete,
            SeedState = seedState, Schema = schema, ContinuationState = continuationState, Objects = objects,
            TypedBodyAbi = TypedBaseBodyCodec.FormatVersion });
    }

    private static void RejectIncompleteFactory(string repositoryPath, bool readOnly) {
        try {
            using Repository unexpected = readOnly ? Repository.OpenStorageReadOnlyExisting(repositoryPath, HostModel.Models())
                : Repository.OpenStorageExisting(repositoryPath, HostModel.Models());
        } catch (InvalidOperationException error) {
            Need(error.Message.Contains("[HeadStore.HeadNotFound]", StringComparison.Ordinal), "Fresh DG rejected for a cause other than missing ref2.");
            return;
        }
        throw new InvalidDataException("DG must refuse incomplete initialization rather than fill or ignore ref2.");
    }

    private static void CheckEmptyInitializationRoots(PublicationStore versions, int completed) {
        for (uint id = 1; id <= 3; id++) {
            var read = versions.ReadHead(id);
            if (id <= completed) {
                ReadOnlySpan<byte> payload = read.Unwrap().Payload;
                bool empty = id == 2 ? DecodeRoot(payload, "P0S1"u8).Count == 0
                    : ManagementDirectoryPointer.Decode(id == 1 ? ManagementDirectoryKind.Branches : ManagementDirectoryKind.Bookmarks, payload) is null;
                Need(empty, "Initialized control directory must be empty.");
            } else {
                Need(read.IsFailure && read.Error!.ErrorCode == "HeadStore.HeadNotFound", "Missing Head must remain absent.");
            }
        }
    }

    private static (FrameAddress State, FrameAddress Schema, FrameAddress Next, ObjectEvidence[] Objects)
        SeedEmptyInitializationThenColdContinue(string repositoryPath) {
        FrameAddress state, schema;
        ObjectEvidence[] initial;
        FrameEvidence[] seedFrames;
        uint listId;
        using (Repository seed = Repository.OpenStorageExisting(repositoryPath, HostModel.Models())) {
            seed.PreparedStateReuseEnabled = false;
            Need(seed.ListBranches().Length == 0 && seed.SharedSchemaRoot is null, "Writable DG must see the complete empty initialization.");
            using WorkingCopy main = seed.OpenWorkingCopy(seed.CreateRootBranchFromState("main", HostModel.Ring()).Id);
            seed.CreateTag("initial", main.BaseCommitId);
            state = Describe(seed, main.BaseCommitId).Address;
            schema = seed.SharedSchemaRoot!.Value;
            initial = Objects(main);
            listId = ListId(main);
            Need(initial.Length == 3, "Seed must contain the Widget ring and shared List.");
            StoredGraphRevision graph = ReadPublishedGraph(seed, main.BaseCommitId);
            Need(RequiredSchema(graph) == schema, "Seed storage pin must match its actual shared root.");
            CheckCatalog(SchemaCatalogView.Load(seed.GraphStore, schema), extra: false);
            CheckStorage(seed, graph, initial);
            Need(graph.ObjectHeads[listId] != state, "The List head must be its actual batch rather than the graph address.");
            RepositoryResources resources = Resources(seed);
            seedFrames = Inventory(resources).Select(frame => CaptureFrame(resources, frame)).ToArray();
        }
        FrameAddress next;
        ObjectEvidence[] updated;
        using (Repository reopened = Repository.OpenStorageExisting(repositoryPath, HostModel.Models())) {
            reopened.PreparedStateReuseEnabled = false;
            using WorkingCopy cold = reopened.OpenWorkingCopy(BranchNamed(reopened));
            Widget root = HostModel.Check(cold.State, 7);
            Need(cold.StateRevisionAddress == state && Objects(cold).SequenceEqual(initial), "Cold checkout must preserve complete seed IDs, actual heads and H.");
            ObjectEvidence list = initial.Single(row => row.Id == listId);
            root.Numbers[7] = 201;
            CommitId nextCommit = cold.CommitState().Id;
            next = Describe(reopened, nextCommit).Address;
            StoredGraphRevision graph = ReadPublishedGraph(reopened, nextCommit);
            Need(graph.SourceGraph == state && RequiredSchema(graph) == schema && graph.OwnedBatch is not null,
                "Cold continuation must preserve the selected content source and Schema pin.");
            ObjectVersionBatch batch = reopened.GraphStore.ReadBatch(graph.OwnedBatch!.Value);
            Need(batch.SourceGraph == state && batch.SchemaRoot == schema && batch.Records.Count == 1, "Cold continuation must write exactly one actual record.");
            ObjectVersionRecord delta = batch.Records[0];
            Need(delta.ObjectId == listId && delta.Kind == ObjectVersionKind.Delta && delta.PriorAddress == Decode(list.Head),
                "Cold continuation must be a List Delta with exact prior.");
            updated = Objects(cold);
            Need(updated.Select(row => row.Id).SequenceEqual(initial.Select(row => row.Id)), "Cold continuation changed complete IDs.");
            ObjectEvidence nextList = updated.Single(row => row.Id == listId);
            Need(Decode(nextList.Head) == graph.OwnedBatch && Decode(nextList.Head) != next &&
                delta.EncodedPayloadBytes is { } bytes && nextList.H == checked(list.H + bytes), "Actual batch head or incremented H disagrees.");
            foreach (ObjectEvidence row in initial.Where(row => row.Id != listId)) {
                Need(updated.Single(candidate => candidate.Id == row.Id) == row, "Cold continuation changed retained heads/H.");
            }
            CheckStorage(reopened, graph, updated);
            CheckFrames(Resources(reopened), seedFrames);
            HostModel.Check(reopened.LoadState(reopened.ReadCurrentCommit(BranchNamed(reopened)).Commit.Id), 201);
            HostModel.Check(reopened.LoadState(reopened.ResolveTag("initial")), 7);
        }
        using (Repository final = Repository.OpenStorageReadOnlyExisting(repositoryPath, HostModel.Models())) {
            Need(Describe(final, final.ReadCurrentCommit(BranchNamed(final)).Commit.Id).Address == next && final.SharedSchemaRoot == schema, "Fresh continuation roots disagree.");
            HostModel.Check(final.LoadState(final.ReadCurrentCommit(BranchNamed(final)).Commit.Id), 201);
            HostModel.Check(final.LoadState(final.ResolveTag("initial")), 7);
            StoredGraphRevision graph = ReadPublishedGraph(final, final.ReadCurrentCommit(BranchNamed(final)).Commit.Id);
            CheckStorage(final, graph, updated);
            CheckFrames(Resources(final), seedFrames);
        }
        return (state, schema, next, updated);
    }

    private static void Seed(string repositoryPath, string resultPath) {
        object evidence;
        using (Repository repository = Repository.CreateStorageNew(repositoryPath, HostModel.Models())) {
            repository.PreparedStateReuseEnabled = false;
            using WorkingCopy main = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", HostModel.Ring()).Id);
            repository.CreateTag("initial", main.BaseCommitId);
            StoredGraphRevision state = ReadPublishedGraph(repository, main.BaseCommitId);
            FrameAddress schema = RequiredSchema(state);
            Need(schema == repository.SharedSchemaRoot, "Seed State and shared Schema pins disagree.");
            SchemaCatalogView catalog = SchemaCatalogView.Load(repository.GraphStore, schema);
            CheckCatalog(catalog, extra: false);
            HostModel.Check(main.State, 7);
            ObjectEvidence[] objects = Objects(main);
            Need(objects.Length == 3, "Seed must contain two Widgets and a shared List.");
            CheckStorage(repository, state, objects);
            evidence = new { Protocol, Lane = "seed", Passed = true, ProcessId = Environment.ProcessId,
                State = Encode(state.Address), Schema = Encode(schema), RootId = state.RootId.Value,
                ListId = ListId(main), Objects = objects, TypedBodyAbi = TypedBaseBodyCodec.FormatVersion,
                TargetBranchId = Encode(main.BranchId), ExpectedBaseCommitId = Encode(main.BaseCommitId),
                OldBranchDirectoryEntry = Encode(Resources(repository).Heads.ReadHead(1).Unwrap().Id),
                OldBookmarkDirectoryEntry = Encode(Resources(repository).Heads.ReadHead(3).Unwrap().Id) };
        }
        WriteJson(resultPath, evidence);
    }

    private static void Writer(string repositoryPath, string window, string readyPath) {
        // These scopes intentionally never complete: the controller must kill this
        // process after READY. There is no exception/finally path used as a crash.
        using Repository repository = Repository.OpenStorageExisting(repositoryPath, HostModel.Models());
        repository.PreparedStateReuseEnabled = false;
        using WorkingCopy main = repository.OpenWorkingCopy(BranchNamed(repository));
        Widget root = HostModel.Check(main.State, 7);
        RepositoryResources resources = Resources(repository);
        GraphRecordStore store = resources.GraphStore;
        string targetBranch = Encode(main.BranchId), expectedBase = Encode(main.BaseCommitId);
        string directoryEntry = Encode(resources.Heads.ReadHead(1).Unwrap().Id), bookmarkEntry = Encode(resources.Heads.ReadHead(3).Unwrap().Id);
        StoredGraphRevision old = ReadPublishedGraph(repository, main.BaseCommitId);
        FrameAddress oldSchema = RequiredSchema(old);
        Need(oldSchema == repository.SharedSchemaRoot, "Writer starts from a mixed root pair.");
        CheckCatalog(SchemaCatalogView.Load(repository.GraphStore, oldSchema), extra: false);
        ObjectEvidence[] objects = Objects(main);
        CheckStorage(repository, old, objects);
        uint listId = ListId(main);
        FrameInfo[] oldFrameInfo = Inventory(resources);
        HashSet<FrameAddress> previousFrames = oldFrameInfo.Select(frame => frame.Address).ToHashSet();
        FrameEvidence[] oldFrames = oldFrameInfo.Select(frame => CaptureFrame(resources, frame)).ToArray();
        ReadyEvidence? prepared = null;
        int cuEnds = 0, beforeCuFlushCalls = 0;
        repository.SaveStageObserver = checkpoint => {
            if (checkpoint == RepositorySaveStage.AfterMetaAppend && window == "AfterMetaAppend") {
                Wait(Capture("DG.AfterMetaAppend"), readyPath);
            }
            if (checkpoint == RepositorySaveStage.AfterBusinessAppend) {
                // All data reads occur after Append returned. VS callbacks only use
                // this prepared evidence and never inspect DG/FS with a CU lease live.
                prepared = Capture("DG.AfterBusinessAppend");
                if (window == "AfterBusinessAppend") { Wait(prepared, readyPath); }
            }
            if (checkpoint == RepositorySaveStage.BeforeInstall && window == "BeforeInstall") {
                Wait(Prepared("DG.BeforeInstall"), readyPath);
            }
        };
        InstallHooks(resources.Heads, () => {
            cuEnds++;
            if (window == "AfterFirstCuEnd" && cuEnds == 1) { Wait(Prepared("VS.AfterHeadRecordAppended.1"), readyPath); }
            if (window == "AfterAllCuEnd" && cuEnds == 2) { Wait(Prepared("VS.AfterHeadRecordAppended.2"), readyPath); }
        }, () => beforeCuFlushCalls++, () => {
            Need(cuEnds == 2 && beforeCuFlushCalls == 2, "The mutation must publish two CU members.");
            if (window == "AfterPublicationConfirmed") { Wait(Prepared("VS.AfterPublicationConfirmed"), readyPath); }
        });
        root.Numbers[7] = 201;
        repository.CommitBranches([main.CreateStateCommitRequest()], [Extra]); // One declaration forces a real business/Schema CU.
        throw new InvalidOperationException("The configured window was not reached; writer must not exit normally.");

        ReadyEvidence Prepared(string stage) {
            Need(prepared is not null, "A VS window requires data candidates prepared by DG first.");
            return prepared! with { CuEndsSeen = cuEnds, BeforeCuFlushCalls = beforeCuFlushCalls, PublicationStage = stage };
        }
        ReadyEvidence Capture(string stage) {
            // Inventory's callback only collects info. ReadFrame starts after the
            // scan has returned, so these diagnostics do not reenter its visitor.
            FrameInfo[] added = Inventory(resources).Where(frame => !previousFrames.Contains(frame.Address)).ToArray();
            FrameEvidence[] frames = added.Select(frame => CaptureFrame(resources, frame)).ToArray();
            string? meta = null, business = null;
            foreach (FrameInfo frame in added.Where(frame => frame.Tag == GraphFrameCodec.GraphTag)) {
                GraphEnvelope graph = store.ReadMetadata(frame.Address);
                if (graph.Kind == StoredGraphKind.Meta) { Need(meta is null, "More than one candidate meta graph."); meta = Encode(frame.Address); }
                else if (graph.Kind == StoredGraphKind.State) {
                    Need(business is null, "More than one candidate business graph."); business = Encode(frame.Address);
                } else { throw new InvalidDataException("Unexpected Event in this State crash mutation."); }
            }
            Need(meta is not null, "The new Extra definition must produce a completed meta candidate.");
            if (stage == "DG.AfterBusinessAppend") { Need(business is not null, "Missing completed business candidate."); }
            return new(Protocol, window, Environment.ProcessId, Encode(old.Address), Encode(oldSchema), old.RootId.Value,
                listId, objects, meta, business, oldFrames, frames, cuEnds, beforeCuFlushCalls, stage,
                targetBranch, expectedBase, directoryEntry, bookmarkEntry);
        }
    }

    private static void Verify(string repositoryPath, string window, string readyPath, string resultPath) {
        ReadyEvidence ready = JsonSerializer.Deserialize<ReadyEvidence>(File.ReadAllText(readyPath), Json)
            ?? throw new InvalidDataException("Missing READY evidence.");
        Need(ready.Protocol == Protocol && ready.Window == window && ready.ProcessId != Environment.ProcessId,
            "READY must describe this window in a distinct writer process.");
        int expectedEnds = window is "AfterMetaAppend" or "AfterBusinessAppend" ? 0 : window == "AfterFirstCuEnd" ? 1 : 2;
        int expectedFlushCalls = window is "AfterPublicationConfirmed" or "BeforeInstall" ? 2 : 0;
        Need(ready.CuEndsSeen == expectedEnds && ready.BeforeCuFlushCalls == expectedFlushCalls,
            "READY hook counts disagree with the actual requested publication boundary.");
        bool expectNew = window is "AfterAllCuEnd" or "AfterPublicationConfirmed" or "BeforeInstall";
        BranchId target = DecodeBranch(ready.TargetBranchId);
        CommitId expectedBase = DecodeCommit(ready.ExpectedBaseCommitId);
        HeadEntryId oldDirectory = DecodeEntry(ready.OldBranchDirectoryEntry), oldBookmarks = DecodeEntry(ready.OldBookmarkDirectoryEntry);
        Need(expectedBase.Value.HeadId == target.Value, "Seed Commit must belong to the target root Branch.");
        CommitId selectedCommit;
        FrameAddress oldState = Decode(ready.OldState), oldSchema = Decode(ready.OldSchema);
        FrameAddress candidateMeta = Decode(ready.CandidateMeta ?? throw new InvalidDataException("Missing candidate meta address."));
        FrameAddress? candidateBusiness = ready.CandidateBusiness is { } encoded ? Decode(encoded) : null;
        FrameAddress expectedState = expectNew ? candidateBusiness ?? throw new InvalidDataException("Missing new business address.") : oldState;
        FrameAddress expectedSchema = expectNew ? candidateMeta : oldSchema;
        FrameAddress continued;
        ObjectEvidence[] selectedObjects;
        Dictionary<string, string> beforeReadOnly = SnapshotFiles(repositoryPath);
        using (Repository repository = Repository.OpenStorageReadOnlyExisting(repositoryPath, HostModel.Models())) {
            repository.PreparedStateReuseEnabled = false;
            RepositoryResources resources = Resources(repository);
            // Formal D membership and the actual B/S entries decide visibility; READY is only expected evidence.
            Need(repository.ListBranches() is [var branch] && branch.Id == target && branch.DisplayLabel == "main",
                "The selected target is not the fixture's one registered business Branch.");
            CheckControlEntries(resources, oldDirectory, oldBookmarks);
            HeadEntry businessHead = resources.Heads.ReadHead(target.Value).Unwrap();
            HeadEntry shared = resources.Heads.ReadHead(2).Unwrap();
            var businessPayload = BusinessEntryPayloadCodec.Decode(businessHead.Payload);
            IReadOnlyDictionary<string, FrameAddress> sharedRoots = DecodeRoot(shared.Payload, "P0S1"u8);
            Need(businessPayload.Graph == expectedState, "Actual business Head must select the exact expected graph.");
            Need(sharedRoots.Count == 1 && sharedRoots.TryGetValue("schema", out FrameAddress schemaHead) && schemaHead == expectedSchema,
                "Actual S must select the matching Schema graph; a mixed pair is rejected.");
            selectedCommit = repository.ReadCurrentCommit(target).Commit.Id;
            Need(selectedCommit.Value == businessHead.Id && repository.SharedSchemaRoot == expectedSchema,
                "DG owner and actual B/S entries disagree.");
            Need(Describe(repository, expectedBase).Address == oldState, "The seed Commit no longer names the old State.");
            Need(expectNew ? selectedCommit != expectedBase : selectedCommit == expectedBase, "Actual Commit identity disagrees with publication visibility.");
            if (expectNew) {
                Need(resources.ReadPrevious(selectedCommit.Value)?.Id == expectedBase.Value,
                    "The new selected Commit must immediately follow the original seed Commit.");
            }
            StoredGraphRevision selected = ReadPublishedGraph(repository, selectedCommit);
            Need(selected.Kind == StoredGraphKind.State && RequiredSchema(selected) == expectedSchema && selected.RootId.Value == ready.RootId,
                "Selected business role, root or exact Schema pin disagrees.");
            CheckCatalog(SchemaCatalogView.Load(repository.GraphStore, expectedSchema), expectNew);
            // Known completed candidate addresses are content evidence, not a startup
            // audit. Fresh Open may recover incomplete tails, but never these frames.
            CheckFrames(resources, ready.OldFrames);
            CheckFrames(resources, ready.CapturedFrames);
            StoredGraphRevision metaCandidate = repository.GraphStore.ReadMetaGraph(candidateMeta);
            Need(metaCandidate.Kind == StoredGraphKind.Meta && metaCandidate.SourceGraph == oldSchema,
                "Candidate meta must derive from the original exact shared catalog.");
            SchemaCatalogView candidateCatalog = SchemaCatalogView.Load(repository.GraphStore, candidateMeta);
            CheckCatalog(candidateCatalog, extra: true);
            SchemaCatalogView originalCatalog = SchemaCatalogView.Load(repository.GraphStore, oldSchema);
            DurableSchema widgetSchema = originalCatalog.Schemas[new(TypeExpr.Named("CrashWidget"), 1)];
            Need(originalCatalog.SchemaNodeId(widgetSchema) == candidateCatalog.SchemaNodeId(widgetSchema),
                "Retained Widget descriptor identity changed in the new catalog.");
            if (candidateBusiness is { } business) {
                StoredGraphRevision candidate = repository.GraphStore.ReadBusinessGraph(business, oldState);
                Need(candidate.SourceGraph == oldState && RequiredSchema(candidate) == candidateMeta,
                    "Candidate business must preserve its actual content source and new Schema pin.");
            }
            if (!expectNew) {
                Need(expectedSchema != candidateMeta && (candidateBusiness is null || expectedState != candidateBusiness),
                    "Completed unselected candidates must not be adopted.");
            }
            HostModel.Check(repository.LoadState(repository.ResolveTag("initial")), 7);
            HostModel.Check(repository.LoadState(repository.ReadCurrentCommit(BranchNamed(repository)).Commit.Id), expectNew ? 201 : 7);
            Need(resources.Data.RecoveryReports.Count == 0 && resources.Heads.RecoveryReports.Count == 0,
                "Readonly verification must not execute physical recovery.");
        }
        Dictionary<string, string> afterReadOnly = SnapshotFiles(repositoryPath);
        Need(beforeReadOnly.Count == afterReadOnly.Count && beforeReadOnly.All(pair => afterReadOnly.TryGetValue(pair.Key, out string? hash) && hash == pair.Value),
            "Readonly root, typed and completed-frame verification changed repository files.");

        using (Repository repository = Repository.OpenStorageExisting(repositoryPath, HostModel.Models())) {
            repository.PreparedStateReuseEnabled = false;
            RepositoryResources resources = Resources(repository);
            Need(repository.ReadCurrentCommit(target).Commit.Id == selectedCommit && Describe(repository, selectedCommit).Address == expectedState && repository.SharedSchemaRoot == expectedSchema,
                "Writable cold Open changed the selected pair.");
            StoredGraphRevision selected = ReadPublishedGraph(repository, selectedCommit);
            using WorkingCopy checkout = repository.OpenWorkingCopy(target);
            Need(checkout.BranchId == target && checkout.BaseCommitId == selectedCommit, "Cold working copy changed target or selected Commit.");
            CheckControlEntries(resources, oldDirectory, oldBookmarks);
            Widget root = HostModel.Check(checkout.State, expectNew ? 201 : 7);
            selectedObjects = Objects(checkout);
            Need(selectedObjects.Select(row => row.Id).SequenceEqual(ready.Objects.Select(row => row.Id)), "Cold complete IDs changed.");
            Need(ListId(checkout) == ready.ListId && checkout.StateRevisionAddress == expectedState, "Cold State source or List ID changed.");
            CheckStorage(repository, selected, selectedObjects);
            foreach (ObjectEvidence old in ready.Objects) {
                ObjectEvidence actual = selectedObjects.Single(row => row.Id == old.Id);
                if (!expectNew || old.Id != ready.ListId) { Need(actual == old, "Retained head/H or selected old head/H changed."); }
            }
            ObjectEvidence list = selectedObjects.Single(row => row.Id == ready.ListId);
            root.Numbers[8] = 202;
            CommitId continuation = checkout.CommitState().Id;
            continued = Describe(repository, continuation).Address;
            StoredGraphRevision next = ReadPublishedGraph(repository, continuation);
            Need(next.SourceGraph == expectedState && RequiredSchema(next) == expectedSchema && next.OwnedBatch is not null,
                "Continuation must derive from the actual selected State and exact Schema.");
            ObjectVersionBatch batch = repository.GraphStore.ReadBatch(next.OwnedBatch!.Value);
            Need(batch.SourceGraph == expectedState && batch.SchemaRoot == expectedSchema && batch.Records.Count == 1,
                "Continuation must write one actual object record with the correct batch context.");
            ObjectVersionRecord delta = batch.Records[0];
            Need(delta.ObjectId == ready.ListId && delta.Kind == ObjectVersionKind.Delta && delta.PriorAddress == Decode(list.Head),
                "Continuation must be the single List Delta against its exact selected prior.");
            Need(next.ObjectHeads[ready.ListId] == next.OwnedBatch && next.ObjectHeads[ready.ListId] != continued,
                "Object head must be the actual batch address, not the graph location.");
            ObjectEvidence[] updated = Objects(checkout);
            Need(updated.Select(row => row.Id).SequenceEqual(selectedObjects.Select(row => row.Id)), "Continuation changed complete IDs.");
            foreach (ObjectEvidence row in selectedObjects.Where(row => row.Id != ready.ListId)) {
                Need(updated.Single(candidate => candidate.Id == row.Id) == row, "Continuation changed retained storage.");
            }
            ObjectEvidence updatedList = updated.Single(row => row.Id == ready.ListId);
            Need(delta.EncodedPayloadBytes is { } bytes && updatedList.H == checked(list.H + bytes), "Continuation H is not exact prior H plus actual payload.");
            CheckStorage(repository, next, updated);
            CheckFrames(resources, ready.OldFrames);
            CheckFrames(resources, ready.CapturedFrames);
            HostModel.Check(repository.LoadState(repository.ReadCurrentCommit(BranchNamed(repository)).Commit.Id), expectNew ? 201 : 7, 202);
            HostModel.Check(repository.LoadState(repository.ResolveTag("initial")), 7);
            Need(resources.Data.RecoveryReports.Values.All(report => report.Action == Atelia.Rbf.RbfTailRecoveryAction.None),
                "A READY-complete data prefix must not require tail repair.");
            Need(resources.Heads.RecoveryReports.All(report => report.Recovery.Action == Atelia.Rbf.RbfTailRecoveryAction.None),
                "Completed CU members must not require control-file tail repair.");
        }
        // A second independent owner verifies that the accepted continuation, selected
        // exact catalog and completed candidates survive ordinary resource closure too.
        using (Repository final = Repository.OpenStorageReadOnlyExisting(repositoryPath, HostModel.Models())) {
            Need(Describe(final, final.ReadCurrentCommit(BranchNamed(final)).Commit.Id).Address == continued && final.SharedSchemaRoot == expectedSchema,
                "Fresh continuation roots changed.");
            HostModel.Check(final.LoadState(final.ReadCurrentCommit(BranchNamed(final)).Commit.Id), expectNew ? 201 : 7, 202);
            HostModel.Check(final.LoadState(final.ResolveTag("initial")), 7);
            Need(BranchNamed(final) == target, "Cold continuation changed the target Branch.");
            CheckControlEntries(Resources(final), oldDirectory, oldBookmarks);
            CheckFrames(Resources(final), ready.OldFrames);
            CheckFrames(Resources(final), ready.CapturedFrames);
        }
        WriteJson(resultPath, new { Protocol, Lane = "verify", Passed = true, ProcessId = Environment.ProcessId, Window = window,
            WriterProcessId = ready.ProcessId, ObservedOldOrNew = expectNew ? "New" : "Old", SelectedState = Encode(expectedState),
            SelectedSchema = Encode(expectedSchema), BusinessState = Encode(expectedState), SharedSchema = Encode(expectedSchema),
            ready.TargetBranchId, ready.ExpectedBaseCommitId, ready.OldBranchDirectoryEntry, ready.OldBookmarkDirectoryEntry,
            SelectedCommitId = Encode(selectedCommit),
            ContinuationState = Encode(continued), SingleDelta = true, ExactPrior = true, IdsPreserved = true, HVerified = true,
            CompletedFramesPreserved = true, ReadOnlyFilesUnchanged = true, NoNonNoneRecovery = true,
            Objects = selectedObjects, OrphanMeta = !expectNew ? ready.CandidateMeta : null,
            OrphanBusiness = !expectNew ? ready.CandidateBusiness : null, TypedBodyAbi = TypedBaseBodyCodec.FormatVersion });
    }

    private static void CheckCatalog(SchemaCatalogView catalog, bool extra) {
        Need(catalog.Schemas.TryGetValue(new(Extra.Type, Extra.Version), out DurableSchema? schema) == extra,
            "Exact cold meta catalog Extra presence disagrees.");
        if (extra) { Need(Extra.Equals(schema), "Extra's complete definition disagrees."); }
        Need(catalog.Schemas.ContainsKey(new(TypeExpr.Named("CrashWidget"), 1)), "The cold catalog lacks Widget's definition.");
        Need(catalog.DescriptorIds.Count == (extra ? 2 : 1), "Unexpected root-declared descriptor count.");
    }
    private static BranchId BranchNamed(Repository repository) => repository.ListBranches().Single(branch => branch.DisplayLabel == "main").Id;
    private static HistoryGraphRecord Describe(Repository repository, CommitId commit) => Resources(repository).ReadDescriptor(commit.Value);
    private static StoredGraphRevision ReadPublishedGraph(Repository repository, CommitId commit) {
        RepositoryResources resources = Resources(repository);
        HistoryGraphRecord selected = resources.ReadDescriptor(commit.Value);
        return repository.GraphStore.ReadBusinessGraph(selected.Address, resources.FindNearestState(commit.Value, inclusive: false)?.Address);
    }
    private static void CheckControlEntries(RepositoryResources resources, HeadEntryId directory, HeadEntryId bookmarks) {
        Need(resources.Heads.ReadHead(1).Unwrap().Id == directory && resources.Heads.ReadHead(3).Unwrap().Id == bookmarks,
            "An ordinary existing-Branch save must not change D or T.");
    }
    private static string Encode(BranchId id) {
        Span<byte> bytes = stackalloc byte[BranchId.EncodedSize];
        Need(id.TryWrite(bytes), "Invalid BranchId.");
        return Convert.ToHexString(bytes);
    }
    private static string Encode(CommitId id) {
        Span<byte> bytes = stackalloc byte[CommitId.EncodedSize];
        Need(id.TryWrite(bytes), "Invalid CommitId.");
        return Convert.ToHexString(bytes);
    }
    private static string Encode(HeadEntryId id) {
        Span<byte> bytes = stackalloc byte[HeadEntryId.EncodedSize];
        Need(id.TryWrite(bytes), "Invalid HeadEntryId.");
        return Convert.ToHexString(bytes);
    }
    private static BranchId DecodeBranch(string text) {
        Need(BranchId.TryRead(Convert.FromHexString(text), out BranchId id), "Invalid READY BranchId.");
        return id;
    }
    private static CommitId DecodeCommit(string text) {
        Need(CommitId.TryRead(Convert.FromHexString(text), out CommitId id), "Invalid READY CommitId.");
        return id;
    }
    private static HeadEntryId DecodeEntry(string text) {
        Need(HeadEntryId.TryRead(Convert.FromHexString(text), out HeadEntryId id), "Invalid READY HeadEntryId.");
        return id;
    }

    private static ObjectEvidence[] Objects(WorkingCopy checkout) =>
        (checkout.Baseline ?? throw new InvalidDataException("Missing complete saving baseline.")).Objects.OrderBy(pair => pair.Key)
            .Select(pair => new ObjectEvidence(pair.Key.Value, Encode(pair.Value.Storage!.Value.Head), pair.Value.Storage.Value.ReconstructionPayloadBytes)).ToArray();
    private static uint ListId(WorkingCopy checkout) => checkout.Baseline!.Objects.Single(pair => pair.Value.Current.Kind == ObjectStateKind.List).Key.Value;
    private static void CheckStorage(Repository repository, StoredGraphRevision graph, ObjectEvidence[] objects) {
        Need(graph.ObjectHeads.Keys.Order().SequenceEqual(objects.Select(row => row.Id)), "Complete directory membership changed.");
        foreach (ObjectEvidence row in objects) {
            FrameAddress head = Decode(row.Head);
            Need(graph.ObjectHeads[row.Id] == head && repository.GraphStore.ReadObjectChain(graph, row.Id).ReconstructionPayloadBytes == row.H,
                "Cold owned head/H disagrees with the actual selected chain.");
        }
    }
    private static FrameAddress RequiredSchema(StoredGraphRevision graph) => graph.SchemaRoot ?? throw new InvalidDataException("Business graph needs an exact Schema pin.");
    private static RepositoryResources Resources(Repository repository) =>
        (RepositoryResources)typeof(Repository).GetField("_resources", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(repository)!;
    private static FrameInfo[] Inventory(RepositoryResources resources) {
        List<FrameInfo> frames = [];
        resources.Data.Inventory(frames.Add).Unwrap();
        return frames.ToArray();
    }
    private static FrameEvidence CaptureFrame(RepositoryResources resources, FrameInfo info) {
        using FrameRead frame = resources.Data.ReadFrame(info.Address).Unwrap();
        Need(!frame.IsTombstone && frame.Tag == info.Tag, "Candidate frame role changed during capture.");
        return new(Encode(info.Address), frame.Tag, Hash(frame.Payload), Hash(frame.TailMeta));
    }
    private static void CheckFrames(RepositoryResources resources, IEnumerable<FrameEvidence> frames) {
        foreach (FrameEvidence evidence in frames) {
            using FrameRead frame = resources.Data.ReadFrame(Decode(evidence.Address)).Unwrap();
            Need(!frame.IsTombstone && frame.Tag == evidence.Tag && Hash(frame.Payload) == evidence.PayloadSha256 && Hash(frame.TailMeta) == evidence.TailMetaSha256,
                "A READY-verified complete candidate frame changed or disappeared.");
        }
    }
    private static void InstallHooks(PublicationStore versions, Action afterCuEnd, Action beforeCuFlush, Action afterConfirmed) {
        FieldInfo field = typeof(PublicationStore).GetField("_testHooks", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object hooks = Activator.CreateInstance(field.FieldType, nonPublic: true)!;
        field.FieldType.GetField("AfterHeadRecordAppended", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(hooks, (Action<Atelia.Data.SizedPtr>)(_ => afterCuEnd()));
        field.FieldType.GetField("AfterPublicationConfirmed", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(hooks, afterConfirmed);
        field.FieldType.GetField("BeforeConditionalUpdateFlush", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(hooks, (Action<uint>)(_ => beforeCuFlush()));
        field.SetValue(versions, hooks);
    }
    private static void Wait(object evidence, string path) {
        WriteJson(path, evidence);
        Console.WriteLine("READY");
        Console.Out.Flush();
        Thread.Sleep(Timeout.Infinite);
        throw new InvalidOperationException("The writer must be killed externally.");
    }
    private static void WriteJson(string path, object value) {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".pending";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json));
        File.Move(temporary, path, overwrite: false);
    }
    private static string Encode(FrameAddress address) {
        Span<byte> bytes = stackalloc byte[FrameAddress.EncodedSize];
        Need(address.TryWrite(bytes), "Cannot encode address.");
        return Convert.ToHexString(bytes);
    }
    private static FrameAddress Decode(string value) {
        Need(FrameAddress.TryRead(Convert.FromHexString(value), out FrameAddress address), "Invalid evidence address.");
        return address;
    }
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static IReadOnlyDictionary<string, FrameAddress> DecodeRoot(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> magic) {
        Need(payload.StartsWith(magic), "Incorrect actual root payload role.");
        return NamedFrameMapCodec.Decode(payload[magic.Length..]).Unwrap();
    }
    private static Dictionary<string, string> SnapshotFiles(string repositoryPath) =>
        Directory.GetFiles(repositoryPath, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(repositoryPath, path), path => Hash(File.ReadAllBytes(path)));
    private static bool SameFiles(Dictionary<string, string> expected, Dictionary<string, string> actual) =>
        expected.Count == actual.Count && expected.All(pair => actual.TryGetValue(pair.Key, out string? hash) && hash == pair.Value);
    private static Dictionary<string, string> Parse(string[] args) {
        if (args.Length % 2 != 1) { throw new ArgumentException("Options must be --name value pairs."); }
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        for (int i = 1; i < args.Length; i += 2) {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || !result.TryAdd(args[i][2..], args[i + 1])) {
                throw new ArgumentException("Invalid or duplicate option.");
            }
        }
        return result;
    }
    private static string Required(Dictionary<string, string> options, string name) =>
        options.TryGetValue(name, out string? value) ? value : throw new ArgumentException("Missing --" + name);
    private static string Window(Dictionary<string, string> options) {
        string window = Required(options, "window");
        Need(Windows.Contains(window, StringComparer.Ordinal), "Unknown process-crash window.");
        return window;
    }
    private static string InitWindow(Dictionary<string, string> options) {
        string window = Required(options, "window");
        Need(window is "AfterFirstCreateHead" or "AfterSecondCreateHead" or "AfterThirdCreateHead", "Unknown direct factory initialization window.");
        return window;
    }
    private static void Need(bool condition, string message) {
        if (!condition) { throw new InvalidDataException(message); }
    }
}
