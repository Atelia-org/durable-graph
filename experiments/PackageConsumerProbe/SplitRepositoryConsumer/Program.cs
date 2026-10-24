extern alias persistence;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Atelia.Binary;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using Atelia.HeadStore;
using SplitRepositoryPackage;
using persistence::Atelia.DurableGraph;
using persistence::Atelia.DurableGraph.Persistence;
using S = global::Atelia.DurableGraph.Generated.Family_7061636B6167652E73706C69742E776964676574;

// Witnesses are diagnostic. Physical checks independently read public backing
// stores after every Repository and checkout in that operation has closed.
internal static class Program {
    private const string Protocol = "DG.SplitRepositoryConsumer.v2";
    private const string WidgetName = "package.split.widget";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private sealed record StorageEvidence(uint Id, string Head, long H);
    private sealed record FrameImage(string Address, uint Tag, string PayloadSha256, string TailMetaSha256);
    private sealed record Witness(string SeedState, string SeedSchema, uint RootId, uint ChildId, uint ListId,
        uint WidgetDescriptorId, StorageEvidence[] SeedObjects, FrameImage[] SeedFrames,
        string? MainState = null, string? RewriteEvent = null, string? RewriteState = null, string? RewriteSchema = null,
        uint? CurrentWidgetDescriptorId = null, StorageEvidence[]? RewriteObjects = null, FrameImage[]? RewriteFrames = null,
        string? ContinuationState = null, StorageEvidence[]? ContinuationObjects = null, FrameImage[]? ContinuationFrames = null,
        string? CatalogSeedState = null, string? CatalogState = null, string? CatalogSchema = null, string? CatalogAcceptanceEvent = null,
        uint? CatalogRootId = null, uint? CatalogListId = null, uint? CatalogEventDescriptorId = null,
        StorageEvidence[]? CatalogSeedObjects = null, StorageEvidence[]? CatalogObjects = null, FrameImage[]? CatalogFrames = null,
        string? FollowupEvent = null, string? FollowupSchema = null, uint? FollowupEventDescriptorId = null,
        StorageEvidence[]? FollowupObjects = null, FrameImage[]? FollowupFrames = null,
        string? SharedContinuationState = null, StorageEvidence[]? SharedContinuationObjects = null, FrameImage[]? SharedContinuationFrames = null);
    private sealed record RefImage(HeadEntryId Revision, byte[] Payload);

    private static int Main(string[] args) {
        if (args.Length != 3) { throw new ArgumentException("Expected seed|upgrade|rewrite|continue|verify, database and witness path."); }
        string lane = args[0], database = Path.GetFullPath(args[1]), witnessPath = Path.GetFullPath(args[2]);
        object observations;
#if HISTORY_V1
        if (lane != "seed") { throw new ArgumentException("The independently compiled V1 host only seeds."); }
        observations = Seed(database, witnessPath);
#elif HISTORY_V2
        observations = lane switch {
            "upgrade" => Upgrade(database, witnessPath),
            "rewrite" => Rewrite(database, witnessPath),
            "continue" => Continue(database, witnessPath),
            "verify" => Verify(database, witnessPath),
            _ => throw new ArgumentException("The accepted-history V2 host expects upgrade|rewrite|continue|verify."),
        };
#else
#error HistoryVersion must be 1 or 2.
#endif
        // The observed ABI comes from real Base records, after all product owners close.
        WireInspector.Snapshot actual = WireInspector.Read(database);
        WriteJson(Path.Combine(Path.GetDirectoryName(witnessPath)!, lane + ".evidence.json"), new {
            Protocol, Lane = lane, Passed = true, ProcessId = Environment.ProcessId, actual.TypedBodyAbi,
            RuntimeAssembly = AssemblyEvidence(typeof(DurableSchema).Assembly),
            PersistenceAssembly = AssemblyEvidence(typeof(Repository).Assembly),
            StorageAssembly = AssemblyEvidence(typeof(ObjectVersionRecord).Assembly),
            HostAssembly = AssemblyEvidence(typeof(Widget).Assembly),
            LoadedAssemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => assembly.GetName().Name?.StartsWith("Atelia.", StringComparison.Ordinal) == true)
                .OrderBy(assembly => assembly.GetName().Name, StringComparer.Ordinal).Select(AssemblyEvidence).ToArray(),
            Observations = observations,
        });
        Console.WriteLine("SplitRepositoryConsumer:" + lane + ":Passed");
        return 0;
    }

#if HISTORY_V1
    private static object Seed(string database, string witnessPath) {
        FrameAddress state;
        CommitId stateCommit;
        uint rootId;
        using (Repository repository = Repository.CreateNew(database, Models())) {
            using WorkingCopy main = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", Ring()).Id);
            repository.CreateTag("initial", main.BaseCommitId);
            stateCommit = main.BaseCommitId;
            rootId = main.StateId!.Value.Value;
            Check(main.State, upgraded: false);
        }
        WireInspector.Snapshot actual = WireInspector.Read(database);
        state = actual.GraphAddress(stateCommit);
        WireInspector.Graph graph = actual.Graph(state);
        FrameAddress schema = RequiredSchema(graph);
        CheckRefs(actual, schema, ("main", state));
        Need(graph.Kind == WireInspector.GraphKind.State && graph.RootId == rootId && graph.Heads.Count == 3,
            "Seed must have complete three-object State membership.");
        uint descriptor = CheckCatalog(actual, schema, current: false);
        uint list = graph.Heads.Keys.Single(id => WireInspector.Header(actual.ObjectChain(state, id).Entries[0].Record.Body).Route == 2);
        uint child = graph.Heads.Keys.Single(id => id != rootId && id != list);
        CheckWidgetBase(actual, state, rootId, 1, descriptor);
        CheckWidgetBase(actual, state, child, 1, descriptor);
        StorageEvidence[] objects = Storage(actual, graph);
        Witness witness = new(Hex(state), Hex(schema), rootId, child, list, descriptor, objects, Frames(actual));
        CheckStorage(actual, graph, objects);
        WriteJson(witnessPath, witness);
        return new { witness.SeedState, witness.SeedSchema, Objects = objects, WidgetDescriptorId = descriptor,
            RingAndSharedList = true, ActualRefsVerified = true, CompleteStorageVerified = true };
    }
#endif

#if HISTORY_V2
    private static object Upgrade(string database, string witnessPath) {
        Witness witness = ReadWitness(witnessPath);
        Dictionary<string, string> before = FileImages(database);
        WireInspector.Snapshot original = WireInspector.Read(database);
        FrameAddress seed = Address(witness.SeedState), schema = Address(witness.SeedSchema);
        CheckRefs(original, schema, ("main", seed));
        Need(CheckCatalog(original, schema, current: false) == witness.WidgetDescriptorId, "The persisted V1 descriptor ID changed.");
        S.V1 root = ReadHistoricalWidget(original, seed, witness.RootId, witness.WidgetDescriptorId);
        S.V1 child = ReadHistoricalWidget(original, seed, witness.ChildId, witness.WidgetDescriptorId);
        Need(Widget.UpgradeCalls == 0 && root.Segment0Field1 == 1 && child.Segment0Field1 == 2 &&
            root.Segment0Field2.Value == witness.ListId && child.Segment0Field2 == root.Segment0Field2 &&
            root.Segment0Field3.Value == witness.ChildId && child.Segment0Field3.Value == witness.RootId,
            "The generated accepted V1 body reader must preserve exact historical values/IDs without Upgrade.");
        int upgrades;
        using (Repository repository = Repository.OpenReadOnlyExisting(database, Models())) {
            CommitId initial = repository.ResolveTag("initial");
            Need(initial == original.CommitAt(seed), "The initial tag changed.");
            Check(repository.LoadState(initial), upgraded: true);
            Need(Widget.UpgradeCalls == 2, "Readonly materialization must Upgrade both historical Widget rows.");
            Widget first = Check(repository.LoadState(initial), upgraded: true);
            Widget second = Check(repository.LoadState(initial), upgraded: true);
            Need(!ReferenceEquals(first, second) && !ReferenceEquals(first.Child, second.Child) &&
                !ReferenceEquals(first.Numbers, second.Numbers),
                "Independent historical State reads must preserve each graph's cycle and aliases without sharing mutable instances.");
            upgrades = Widget.UpgradeCalls;
        }
        WireInspector.Snapshot after = WireInspector.Read(database);
        CheckRefs(after, schema, ("main", seed));
        CheckStorage(after, after.Graph(seed), witness.SeedObjects);
        CheckFrames(after, witness.SeedFrames);
        Need(SameFiles(before, FileImages(database)), "Readonly accepted-history Upgrade changed repository files.");
        return new { ReadOnlyFilesUnchanged = true, ExactHistoricalDto = true, ReadOnlyUpgrade = true, UpgradeCalls = upgrades,
            HistoricalStateReadsIndependent = true, ActualRefsVerified = true, CompleteFramesPreserved = true };
    }

    private static object Rewrite(string database, string witnessPath) {
        Witness witness = ReadWitness(witnessPath);
        FrameAddress seed = Address(witness.SeedState), oldSchema = Address(witness.SeedSchema), mainState;
        CommitId mainCommit;
        WireInspector.Snapshot original = WireInspector.Read(database);
        CheckRefs(original, oldSchema, ("main", seed));
        CheckStorage(original, original.Graph(seed), witness.SeedObjects);
        using (Repository repository = Repository.OpenExisting(database, Models())) {
            using WorkingCopy main = repository.OpenWorkingCopy(BranchNamed(repository, "main"));
            Check(main.State, upgraded: true);
            Need(main.StateSourceCommitId == original.CommitAt(seed) && main.StateId!.Value.Value == witness.RootId,
                "Cold main must select the exact historical State and root ID.");
            mainCommit = main.CommitState().Id;
        }
        WireInspector.Snapshot latest = WireInspector.Read(database);
        mainState = latest.GraphAddress(mainCommit);
        FrameAddress schema = RequiredSchema(latest.Graph(mainState));
        Need(schema != oldSchema, "The automatically captured V2 closure must advance the shared catalog.");
        uint descriptor = CheckCatalog(latest, schema, current: true);
        CheckRefs(latest, schema, ("main", mainState));
        CheckRewrittenState(latest, mainState, seed, witness, schema, descriptor);

        // Closing after Fork makes its ref-only nature observable independently.
        int beforeFork = Widget.UpgradeCalls;
        using (Repository repository = Repository.OpenExisting(database, Models())) {
            using WorkingCopy fork = repository.OpenWorkingCopy(repository.ForkBranch(repository.ResolveTag("initial"), "fork").Id);
            Need(fork.BaseCommitId == original.CommitAt(seed) && fork.StateSourceCommitId == original.CommitAt(seed) && fork.StateId!.Value.Value == witness.RootId,
                "Historical Fork must restore the tag's nearest State, independently of latest shared S.");
            Check(fork.State, upgraded: true);
            Need(Widget.UpgradeCalls == beforeFork + 2, "Historical Fork must execute accepted V1 history for both Widgets.");
        }
        WireInspector.Snapshot forked = WireInspector.Read(database);
        CheckRefs(forked, schema, ("main", mainState), ("fork", seed));
        CheckFrames(forked, Frames(latest)); // Fork appends D metadata but no business Commit.
        Need(forked.CommitAt(seed) == original.CommitAt(seed), "Fork fabricated a new inherited Commit.");
        CheckStorage(forked, forked.Graph(seed), witness.SeedObjects);

        FrameAddress eventAddress, rewritten;
        CommitId eventCommit, rewrittenCommit;
        using (Repository repository = Repository.OpenExisting(database, Models())) {
            using WorkingCopy fork = repository.OpenWorkingCopy(BranchNamed(repository, "fork"));
            Widget root = Check(fork.State, upgraded: true);
            ObjectId rootId = fork.StateId!.Value;
            eventCommit = fork.CommitEvent(root).Id;
            Need(fork.BaseCommitId == eventCommit && fork.StateSourceCommitId == original.CommitAt(seed) && fork.StateId == rootId &&
                ReferenceEquals(root, fork.State), "Independent Event must retain the original State instance/ID/source and pending rewrite.");
            Check(repository.LoadEvent(fork.BaseCommitId), upgraded: true);
            Widget firstEvent = Check(repository.LoadEvent(eventCommit), upgraded: true);
            Widget secondEvent = Check(repository.LoadEvent(eventCommit), upgraded: true);
            Need(!ReferenceEquals(firstEvent, secondEvent) && !ReferenceEquals(firstEvent.Child, secondEvent.Child) &&
                !ReferenceEquals(firstEvent.Numbers, secondEvent.Numbers),
                "Two reads of the same Event must preserve each graph's aliases without sharing mutable instances across graphs.");
            Widget stateSide = Check(repository.LoadState(repository.ResolveTag("initial")), upgraded: true);
            Widget eventSide = Check(repository.LoadEvent(eventCommit), upgraded: true);
            Need(!ReferenceEquals(stateSide, eventSide) && !ReferenceEquals(stateSide.Numbers, eventSide.Numbers) &&
                stateSide.Numbers.SequenceEqual(eventSide.Numbers), "Independent State and Event reads must materialize equal reachable content.");
            rewrittenCommit = fork.CommitState().Id;
            Need(fork.StateSourceCommitId == rewrittenCommit && fork.StateId == rootId && ReferenceEquals(root, fork.State),
                "The following State must accept the original upgraded instances and IDs.");
            Widget sameFirst = Check(repository.LoadState(fork.BaseCommitId), upgraded: true);
            Widget sameSecond = Check(repository.LoadState(fork.BaseCommitId), upgraded: true);
            Need(!ReferenceEquals(sameFirst, sameSecond) && !ReferenceEquals(sameFirst.Numbers, sameSecond.Numbers),
                "Repeated current State reads must restore independent mutable graphs.");
            Widget oldRoot = Check(repository.LoadState(repository.ResolveTag("initial")), upgraded: true);
            Widget newRoot = Check(repository.LoadState(fork.BaseCommitId), upgraded: true);
            Need(!ReferenceEquals(oldRoot, newRoot) && !ReferenceEquals(oldRoot.Numbers, newRoot.Numbers) &&
                oldRoot.Value == newRoot.Value && oldRoot.Child!.Value == newRoot.Child!.Value && oldRoot.Numbers.SequenceEqual(newRoot.Numbers),
                "Independent State reads under different exact schemas must preserve equal contents and separate mutable instances.");
        }
        WireInspector.Snapshot actual = WireInspector.Read(database);
        eventAddress = actual.GraphAddress(eventCommit);
        rewritten = actual.GraphAddress(rewrittenCommit);
        CheckRefs(actual, schema, ("main", mainState), ("fork", rewritten));
        WireInspector.Graph domainEvent = actual.Graph(eventAddress);
        Need(domainEvent.Kind == WireInspector.GraphKind.Event && domainEvent.Parent == seed && domainEvent.SourceGraph is null &&
            RequiredSchema(domainEvent) == schema, "Event must separately pin S1 without a State content source.");
        CheckIndependentEvent(actual, domainEvent, 3);
        CheckRewrittenState(actual, rewritten, eventAddress, witness, schema, descriptor);
        Need(actual.ObjectChain(seed, witness.ListId).Head == actual.ObjectChain(rewritten, witness.ListId).Head &&
            RequiredSchema(actual.Graph(seed)) != RequiredSchema(actual.Graph(rewritten)),
            "The schema rewrite must retain the unchanged List ID/head and current contents.");
        Need(CheckCatalog(actual, schema, current: true, version: 1) == witness.WidgetDescriptorId,
            "The accepted V1 descriptor must reuse its original metaObjectId.");
        CheckFrames(actual, witness.SeedFrames);
        HashSet<string> previous = witness.SeedFrames.Select(frame => frame.Address).ToHashSet(StringComparer.Ordinal);
        Witness next = witness with { MainState = Hex(mainState), RewriteEvent = Hex(eventAddress), RewriteState = Hex(rewritten),
            RewriteSchema = Hex(schema), CurrentWidgetDescriptorId = descriptor, RewriteObjects = Storage(actual, actual.Graph(rewritten)),
            RewriteFrames = Frames(actual).Where(frame => !previous.Contains(frame.Address)).ToArray() };
        WriteJson(witnessPath, next);
        return new { next.MainState, next.RewriteEvent, next.RewriteState, next.RewriteSchema, Objects = next.RewriteObjects,
            HistoricalForkUsesStateS = true, LatestCatalogRemainsIndependent = true, IndependentEventKeepsStateRewrite = true,
            RepeatedEventReadsIndependent = true, StateEventReadsIndependent = true,
            RewrittenLiveWidgetBases = 2, RetainedListHeadAndH = true, RepeatedStateReadsIndependent = true, CrossSchemaStateReadsIndependent = true,
            ActualRefsVerified = true, CompleteFramesPreserved = true, RetainedDescriptorId = witness.WidgetDescriptorId,
            CurrentDescriptorId = descriptor };
    }

    private static object Continue(string database, string witnessPath) {
        Witness witness = ReadWitness(witnessPath);
        FrameAddress rewritten = Address(witness.RewriteState!), schema = Address(witness.RewriteSchema!);
        WireInspector.Snapshot previous = WireInspector.Read(database);
        CheckRefs(previous, schema, ("main", Address(witness.MainState!)), ("fork", rewritten));
        StorageEvidence[] priorRows = Storage(previous, previous.Graph(rewritten));
        Need(priorRows.SequenceEqual(witness.RewriteObjects!), "Actual cold source storage differs from the last confirmed observation.");
        FrameAddress continued;
        CommitId continuedCommit;
        using (Repository repository = Repository.OpenExisting(database, Models())) {
            using WorkingCopy fork = repository.OpenWorkingCopy(BranchNamed(repository, "fork"));
            Widget root = Check(fork.State, upgraded: true);
            Need(Widget.UpgradeCalls == 0 && fork.StateSourceCommitId == previous.CommitAt(rewritten) && fork.StateId!.Value.Value == witness.RootId,
                "The fresh current State must retain root identity without another historical Upgrade.");
            root.Numbers[7] = 201;
            continuedCommit = fork.CommitState().Id;
            Check(repository.LoadState(repository.ResolveTag("initial")), upgraded: true);
            Check(repository.LoadState(repository.ReadCurrentCommit(BranchNamed(repository, "fork")).Commit.Id), upgraded: true, atSeven: 201);
        }
        WireInspector.Snapshot actual = WireInspector.Read(database);
        continued = actual.GraphAddress(continuedCommit);
        WireInspector.Graph graph = actual.Graph(continued);
        Need(graph.Parent == rewritten && graph.SourceGraph == rewritten && RequiredSchema(graph) == schema && graph.OwnedBatch is not null,
            "Cold Delta must select the exact current content Parent and Schema.");
        uint[] written = graph.Heads.Where(pair => pair.Value == graph.OwnedBatch).Select(pair => pair.Key).ToArray();
        Need(written.SequenceEqual(new[] { witness.ListId }), "Only the List may have a newly written local object record.");
        var chain = actual.ObjectChain(continued, witness.ListId);
        var delta = chain.Entries[^1];
        var prior = previous.ObjectChain(rewritten, witness.ListId);
        Need(delta.Record.Kind == ObjectVersionKind.Delta && delta.Record.PriorAddress == prior.Head &&
            delta.ContainingBatch == graph.OwnedBatch && delta.SourceGraph == rewritten && delta.SchemaRoot == schema,
            "The actual List Delta must reference the selected object's exact head/source/S.");
        StorageEvidence[] updated = Storage(actual, graph);
        Need(updated.Select(row => row.Id).SequenceEqual(priorRows.Select(row => row.Id)) && chain.Head == graph.OwnedBatch &&
            chain.Head != continued && chain.H == checked(prior.H + delta.Record.ActualPayloadBytes),
            "Complete IDs and wire H must use the actual batch, not the graph address or a re-encoded estimate.");
        foreach (StorageEvidence row in priorRows.Where(row => row.Id != witness.ListId)) {
            Need(updated.Single(candidate => candidate.Id == row.Id) == row, "Delta continuation changed an unchanged Widget head/H.");
        }
        CheckStorage(actual, graph, updated);
        CheckRefs(actual, schema, ("main", Address(witness.MainState!)), ("fork", continued));
        CheckFrames(actual, witness.SeedFrames.Concat(witness.RewriteFrames!));
        HashSet<string> previousFrames = witness.SeedFrames.Concat(witness.RewriteFrames!).Select(frame => frame.Address).ToHashSet(StringComparer.Ordinal);
        Witness next = witness with { ContinuationState = Hex(continued), ContinuationObjects = updated,
            ContinuationFrames = Frames(actual).Where(frame => !previousFrames.Contains(frame.Address)).ToArray() };
        next = CatalogStory(database, next, actual);
        next = FollowupStory(database, next);
        WriteJson(witnessPath, next);
        return new { next.ContinuationState, Objects = updated, SingleListDelta = true, ExactPrior = true, IdsPreserved = true,
            ActualWireH = true, RetainedWidgetHeads = true, ActualRefsVerified = true, CompleteFramesPreserved = true,
            next.CatalogSeedState, next.CatalogState, next.CatalogSchema,
            CatalogObjects = next.CatalogObjects, CatalogEventDescriptor = next.CatalogEventDescriptorId,
            CatalogBaselineIdentities = true, CatalogEventAcceptsMetaOnly = true, CatalogRetainedDeclarations = true, CatalogColdListDelta = true, CatalogSharedRootAdvanced = true,
            CatalogHotStateRetained = true, CatalogIndependentRead = true, CatalogSameOwnerCheckout = true,
            next.FollowupEvent, next.FollowupSchema, next.SharedContinuationState, next.FollowupEventDescriptorId,
            FollowupCatalogUnchanged = true, FollowupBranchAdvanced = true, FollowupEventRetainsState = true,
            SharedColdOwnerListDelta = true, FollowupRetainedOrigins = true };
    }

    private static Witness CatalogStory(string database, Witness witness, WireInspector.Snapshot shared) {
        RefImage sharedRef = ReadSharedRef(database), branchRef = ReadRefImage(database, 1);
        FrameAddress initial = Address(witness.ContinuationState!), catalogEvent;
        CommitId catalogEventCommit;
        uint rootId = witness.RootId, listId = witness.ListId;
        using (Repository repository = Repository.OpenExisting(database, Models())) {
            // Fork the real baseline; registration alone must not persist the Event type.
            repository.ForkBranch(repository.ReadCurrentCommit(BranchNamed(repository, "fork")).Commit.Id, "catalog");
            using WorkingCopy created = repository.OpenWorkingCopy(BranchNamed(repository, "catalog"));
            Widget root = Check(created.State, upgraded: true, atSeven: 201);
            Need(created.BaseCommitId == shared.CommitAt(initial) && created.StateSourceCommitId == shared.CommitAt(initial) &&
                created.StateId!.Value.Value == rootId, "Catalog fork must restore the actual baseline identities.");
            catalogEventCommit = created.CommitEvent(new CatalogEvent { Value = 41 }).Id;
            Need(created.BaseCommitId == catalogEventCommit && created.StateSourceCommitId == shared.CommitAt(initial) &&
                created.StateId!.Value.Value == rootId && ReferenceEquals(created.State, root),
                "Catalog Event must accept meta without advancing the State baseline, identity or instance.");
        }
        WireInspector.Snapshot seeded = WireInspector.Read(database);
        catalogEvent = seeded.GraphAddress(catalogEventCommit);
        WireInspector.Graph seedGraph = seeded.Graph(initial), eventGraph = seeded.Graph(catalogEvent);
        FrameAddress catalogSchema = RequiredSchema(eventGraph);
        Need(catalogSchema != Address(witness.RewriteSchema!) &&
            RequiredSchema(seedGraph) == Address(witness.RewriteSchema!) &&
            eventGraph.Kind == WireInspector.GraphKind.Event && eventGraph.Parent == initial &&
            eventGraph.SourceGraph is null && seedGraph.RootId == rootId,
            "Catalog Event must pin its newly accepted directory independently of the older State S.");
        CheckIndependentEvent(seeded, eventGraph, 1);
        Need(CheckCatalog(shared, RequiredSchema(seedGraph), current: true) == witness.CurrentWidgetDescriptorId,
            "The baseline catalog must not already contain the Event declaration.");
        uint unused = CheckEventCatalog(seeded, catalogSchema, witness);
        StorageEvidence[] initialRows = Storage(seeded, seedGraph);
        Need(initialRows.SequenceEqual(witness.ContinuationObjects!), "Fork changed source heads, H or IDs.");
        Need(seeded.SharedSchemaRoot == catalogSchema && !SameSharedRef(sharedRef, ReadSharedRef(database)) &&
            !SameRefImage(branchRef, ReadRefImage(database, 1)),
            "Fork registration must advance D, and the Event must publish the new declaration plus its business Head.");
        RefImage acceptedSharedRef = ReadSharedRef(database);

        FrameAddress next;
        CommitId nextCommit;
        using (Repository repository = Repository.OpenExisting(database, Models())) {
            using WorkingCopy current = repository.OpenWorkingCopy(BranchNamed(repository, "catalog"));
            Widget root = Check(current.State, upgraded: true, atSeven: 201);
            Need(current.BaseCommitId == catalogEventCommit && current.StateSourceCommitId == shared.CommitAt(initial) && current.StateId!.Value.Value == rootId,
                "Cold Shared checkout must import its own exact root identity and source.");
            root.Numbers[7] = 202;
            CommitId saved = current.CommitState().Id;
            nextCommit = saved;
            Need(ReferenceEquals(root, Check(current.State, upgraded: true, atSeven: 202)) &&
                current.StateId!.Value.Value == rootId && current.StateSourceCommitId == nextCommit,
                "Confirmed State must accept the original edited instances and identity.");

            Widget independent = Check(repository.LoadState(saved), upgraded: true, atSeven: 202);
            Need(!ReferenceEquals(root, independent) && !ReferenceEquals(root.Child, independent.Child) &&
                !ReferenceEquals(root.Numbers, independent.Numbers),
                "Same-owner independent State read must restore equal values in independent mutable instances.");
            current.Dispose();
            using WorkingCopy signedAgain = repository.OpenWorkingCopy(BranchNamed(repository, "catalog"));
            Widget restored = Check(signedAgain.State, upgraded: true, atSeven: 202);
            Need(signedAgain.BaseCommitId == nextCommit && signedAgain.StateSourceCommitId == nextCommit &&
                signedAgain.StateId!.Value.Value == rootId && repository.ReadCurrentCommit(BranchNamed(repository, "catalog")).Commit.Id == nextCommit &&
                !ReferenceEquals(root, restored) && !ReferenceEquals(root.Child, restored.Child) &&
                !ReferenceEquals(root.Numbers, restored.Numbers) && !ReferenceEquals(independent, restored) &&
                !ReferenceEquals(independent.Child, restored.Child) && !ReferenceEquals(independent.Numbers, restored.Numbers),
                "Same-owner recheckout must retain the saved State and identity while restoring an independent graph.");
        }
        WireInspector.Snapshot actual = WireInspector.Read(database);
        next = actual.GraphAddress(nextCommit);
        WireInspector.Graph graph = actual.Graph(next);
        Need(graph.Parent == catalogEvent && graph.SourceGraph == initial && graph.RootId == rootId &&
            RequiredSchema(graph) == catalogSchema && graph.OwnedBatch is not null &&
            graph.Heads.Where(pair => pair.Value == graph.OwnedBatch).Select(pair => pair.Key).SequenceEqual(new[] { listId }),
            "Catalog cold continuation must write only the changed List against its exact State and accepted S.");
        var priorList = seeded.ObjectChain(initial, listId);
        var currentList = actual.ObjectChain(next, listId);
        var delta = currentList.Entries[^1];
        Need(delta.Record.Kind == ObjectVersionKind.Delta && delta.Record.PriorAddress == priorList.Head &&
            delta.ContainingBatch == graph.OwnedBatch && delta.SourceGraph == initial && delta.SchemaRoot == catalogSchema &&
            currentList.H == checked(priorList.H + delta.Record.ActualPayloadBytes),
            "Catalog List Delta must preserve its exact prior and add actual encoded payload bytes to H.");
        StorageEvidence[] rows = Storage(actual, graph);
        Need(rows.Select(row => row.Id).SequenceEqual(initialRows.Select(row => row.Id)), "Catalog continuation changed complete membership IDs.");
        foreach (StorageEvidence row in initialRows.Where(row => row.Id != listId)) {
            Need(rows.Single(candidate => candidate.Id == row.Id) == row, "Catalog continuation changed a retained Widget head/H.");
        }
        Need(CheckEventCatalog(actual, catalogSchema, witness) == unused, "Catalog accepted declarations or IDs changed after cold saving.");
        CheckRefs(actual, catalogSchema, ("main", Address(witness.MainState!)),
            ("fork", Address(witness.ContinuationState!)), ("catalog", next));
        CheckStorage(actual, actual.Graph(Address(witness.ContinuationState!)), witness.ContinuationObjects!);
        CheckFrames(actual, Frames(shared));
        Need(SameSharedRef(acceptedSharedRef, ReadSharedRef(database)), "Saving known layouts must retain the accepted shared ref.");
        Dictionary<string, string> beforeRead = FileImages(database);
        using (Repository reader = Repository.OpenReadOnlyExisting(database, Models())) {
            Check(reader.LoadState(reader.ReadCurrentCommit(BranchNamed(reader, "main")).Commit.Id), upgraded: true);
            Check(reader.LoadState(reader.ReadCurrentCommit(BranchNamed(reader, "fork")).Commit.Id), upgraded: true, atSeven: 201);
            Check(reader.LoadState(reader.ReadCurrentCommit(BranchNamed(reader, "catalog")).Commit.Id), upgraded: true, atSeven: 202);
            Need(reader.LoadEvent(catalogEventCommit) is CatalogEvent { Value: 41 },
                "Readonly must recover the generated Event through its own accepted historical S.");
        }
        Need(SameFiles(beforeRead, FileImages(database)), "Readonly cold recovery changed repository files.");
        HashSet<string> oldFrames = Frames(shared).Select(frame => frame.Address).ToHashSet(StringComparer.Ordinal);
        return witness with { CatalogSeedState = Hex(initial), CatalogState = Hex(next), CatalogSchema = Hex(catalogSchema), CatalogAcceptanceEvent = Hex(catalogEvent),
            CatalogRootId = rootId, CatalogListId = listId, CatalogEventDescriptorId = unused,
            CatalogSeedObjects = initialRows, CatalogObjects = rows,
            CatalogFrames = Frames(actual).Where(frame => !oldFrames.Contains(frame.Address)).ToArray() };
    }

    private static uint CheckEventCatalog(WireInspector.Snapshot actual, FrameAddress schema, Witness witness) {
        var catalog = actual.Catalogs[schema];
        Need(catalog.DeclaredIds.Count == 3 && catalog.WidgetDescriptors.Count == 2 &&
            catalog.WidgetDescriptors[1] == witness.WidgetDescriptorId && catalog.WidgetDescriptors[2] == witness.CurrentWidgetDescriptorId &&
            catalog.CatalogEventDescriptor is { } unused && catalog.DeclaredIds.Contains(unused) &&
            unused != witness.WidgetDescriptorId && unused != witness.CurrentWidgetDescriptorId,
            "The accepted catalog must retain unreachable V1/current V2 IDs and the State-unreachable SG Event schema.");
        return catalog.CatalogEventDescriptor!.Value;
    }

    private static Witness FollowupStory(string database, Witness witness) {
        FrameAddress catalogState = Address(witness.CatalogState!);
        WireInspector.Snapshot before = WireInspector.Read(database);
        RefImage oldBranchRef = ReadBusinessRef(database, before.CommitAt(catalogState)), oldSharedRef = ReadRefImage(database, 2);
        RefImage directoryBefore = ReadRefImage(database, 1);
        FrameAddress followupEvent;
        CommitId followupCommit;
        using (Repository repository = Repository.OpenExisting(database, Models())) {
            using WorkingCopy current = repository.OpenWorkingCopy(BranchNamed(repository, "catalog"));
            Widget root = Check(current.State, upgraded: true, atSeven: 202);
            ObjectId? rootId = current.StateId;
            followupCommit = current.CommitEvent(root).Id;
            Need(current.BaseCommitId == followupCommit && current.StateSourceCommitId == before.CommitAt(catalogState) &&
                current.StateId == rootId && ReferenceEquals(current.State, root),
                "Ordinary Event must accept Head without accepting a new State baseline or instance.");
        }
        WireInspector.Snapshot followup = WireInspector.Read(database);
        followupEvent = followup.GraphAddress(followupCommit);
        FrameAddress sharedSchema = followup.SharedSchemaRoot ?? throw new InvalidDataException("Followup did not publish shared S.");
        uint unused = CheckEventCatalog(followup, sharedSchema, witness);
        RefImage followupBranchRef = ReadBusinessRef(database, followupCommit), followupSharedRef = ReadRefImage(database, 2);
        Need(sharedSchema == Address(witness.CatalogSchema!) &&
            oldBranchRef.Revision != followupBranchRef.Revision && !oldBranchRef.Payload.AsSpan().SequenceEqual(followupBranchRef.Payload) &&
            SameRefImage(oldSharedRef, followupSharedRef) && SameRefImage(directoryBefore, ReadRefImage(database, 1)),
            "A known-layout Event must advance its business Head while retaining D and the accepted shared Catalog.");
        WireInspector.Graph domainEvent = followup.Graph(followupEvent), previous = followup.Graph(catalogState);
        Need(domainEvent.Kind == WireInspector.GraphKind.Event && domainEvent.Parent == catalogState && domainEvent.SourceGraph is null &&
            RequiredSchema(domainEvent) == sharedSchema && domainEvent.DirectoryRoot != previous.DirectoryRoot &&
            domainEvent.OwnedBatch is not null,
            "The no-change Event must own a fresh complete graph while preserving the accepted shared view.");
        CheckIndependentEvent(followup, domainEvent, 3);
        CheckStorage(followup, previous, witness.CatalogObjects!);
        CheckRefs(followup, sharedSchema, ("main", Address(witness.MainState!)),
            ("fork", Address(witness.ContinuationState!)), ("catalog", followupEvent));
        CheckFrames(followup, Frames(before));

        // This is a fresh owner in the Continue PID. The final Verify PID stays
        // wholly read-only; no claim of a second writer process is made here.
        FrameAddress continued;
        CommitId continuedCommit;
        using (Repository repository = Repository.OpenExisting(database, Models())) {
            using WorkingCopy current = repository.OpenWorkingCopy(BranchNamed(repository, "catalog"));
            Widget root = Check(current.State, upgraded: true, atSeven: 202);
            Need(current.BaseCommitId == followupCommit && current.StateSourceCommitId == before.CommitAt(catalogState) &&
                current.StateId!.Value.Value == witness.CatalogRootId,
                "Cold Shared checkout must restore the previous State while preserving the newer Event head.");
            root.Numbers[7] = 203;
            continuedCommit = current.CommitState().Id;
        }
        WireInspector.Snapshot actual = WireInspector.Read(database);
        continued = actual.GraphAddress(continuedCommit);
        WireInspector.Graph graph = actual.Graph(continued);
        uint listId = witness.CatalogListId!.Value;
        Need(graph.Kind == WireInspector.GraphKind.State && graph.Parent == followupEvent && graph.SourceGraph == catalogState &&
            RequiredSchema(graph) == sharedSchema && graph.RootId == witness.CatalogRootId && graph.OwnedBatch is not null &&
            graph.Heads.Where(pair => pair.Value == graph.OwnedBatch).Select(pair => pair.Key).SequenceEqual(new[] { listId }),
            "Cold Shared State must use the Event Parent, original State content source, and only a new List record.");
        var prior = before.ObjectChain(catalogState, listId);
        var chain = actual.ObjectChain(continued, listId);
        var delta = chain.Entries[^1];
        Need(delta.Record.Kind == ObjectVersionKind.Delta && delta.Record.PriorAddress == prior.Head && delta.SourceGraph == catalogState &&
            delta.SchemaRoot == sharedSchema && delta.ContainingBatch == graph.OwnedBatch &&
            chain.H == checked(prior.H + delta.Record.ActualPayloadBytes),
            "Shared continuation Delta must extend the exact State head and its actual wire H.");
        StorageEvidence[] rows = Storage(actual, graph);
        Need(rows.Select(row => row.Id).SequenceEqual(witness.CatalogObjects!.Select(row => row.Id)), "Followup changed complete live IDs.");
        foreach (StorageEvidence row in witness.CatalogObjects!.Where(row => row.Id != listId)) {
            Need(rows.Single(candidate => candidate.Id == row.Id) == row, "Shared continuation rewrote a compatible retained Widget head/H.");
        }
        foreach (uint id in graph.Heads.Keys) {
            Need(actual.ObjectChain(continued, id).Entries[0].ContainingBatch == before.ObjectChain(catalogState, id).Entries[0].ContainingBatch &&
                actual.ObjectChain(continued, id).Entries[0].SchemaRoot == before.ObjectChain(catalogState, id).Entries[0].SchemaRoot,
                "Retained Base interpretation must remain tied to its exact historical origin S.");
        }
        Need(CheckEventCatalog(actual, sharedSchema, witness) == unused && actual.SharedSchemaRoot == sharedSchema,
            "Cold Shared saving lost the complete accepted catalog or changed its accepted descriptor identity.");
        CheckRefs(actual, sharedSchema, ("main", Address(witness.MainState!)),
            ("fork", Address(witness.ContinuationState!)), ("catalog", continued));
        CheckStorage(actual, graph, rows);
        CheckFrames(actual, Frames(followup));
        HashSet<string> beforeFrames = Frames(before).Select(frame => frame.Address).ToHashSet(StringComparer.Ordinal);
        HashSet<string> followupFrames = Frames(followup).Select(frame => frame.Address).ToHashSet(StringComparer.Ordinal);
        return witness with { FollowupEvent = Hex(followupEvent), FollowupSchema = Hex(sharedSchema), FollowupEventDescriptorId = unused,
            FollowupObjects = Storage(followup, domainEvent),
            FollowupFrames = Frames(followup).Where(frame => !beforeFrames.Contains(frame.Address)).ToArray(),
            SharedContinuationState = Hex(continued), SharedContinuationObjects = rows,
            SharedContinuationFrames = Frames(actual).Where(frame => !followupFrames.Contains(frame.Address)).ToArray() };
    }

    private static RefImage ReadBusinessRef(string database, CommitId commit) {
        Span<byte> bytes = stackalloc byte[CommitId.EncodedSize];
        Need(commit.TryWrite(bytes) && HeadEntryId.TryRead(bytes, out _), "Invalid CommitId codec.");
        HeadEntryId.TryRead(bytes, out HeadEntryId id);
        using var data = Atelia.FrameStore.FrameStore.OpenReadOnly(Path.Combine(database, "data"));
        using var heads = Atelia.HeadStore.HeadStore.OpenReadOnly(Path.Combine(database, "versions"), data);
        HeadEntry current = heads.ReadHead(id.HeadId).Unwrap();
        return new(current.Id, current.Payload.ToArray());
    }

    private static RefImage ReadSharedRef(string database) => ReadRefImage(database, 2);

    private static RefImage ReadRefImage(string database, uint refId) {
        using var data = Atelia.FrameStore.FrameStore.OpenReadOnly(Path.Combine(database, "data"));
        using var versions = Atelia.HeadStore.HeadStore.OpenReadOnly(Path.Combine(database, "versions"), data);
        HeadEntry snapshot = versions.ReadHead(refId).Unwrap();
        return new(snapshot.Id, snapshot.Payload.ToArray());
    }

    private static bool SameSharedRef(RefImage before, RefImage after) => SameRefImage(before, after);

    private static bool SameRefImage(RefImage before, RefImage after) =>
        before.Revision.Equals(after.Revision) && before.Payload.AsSpan().SequenceEqual(after.Payload);

    private static object Verify(string database, string witnessPath) {
        Witness witness = ReadWitness(witnessPath);
        Dictionary<string, string> before = FileImages(database);
        WireInspector.Snapshot prior = WireInspector.Read(database);
        using (Repository repository = Repository.OpenReadOnlyExisting(database, Models())) {
            CommitId initial = repository.ResolveTag("initial");
            Need(initial == prior.CommitAt(Address(witness.SeedState)), "The immutable initial tag moved.");
            Check(repository.LoadState(initial), upgraded: true);
            Check(repository.LoadState(repository.ReadCurrentCommit(BranchNamed(repository, "main")).Commit.Id), upgraded: true);
            Check(repository.LoadState(repository.ReadCurrentCommit(BranchNamed(repository, "fork")).Commit.Id), upgraded: true, atSeven: 201);
            Need(repository.ReadCurrentCommit(BranchNamed(repository, "catalog")).Commit.Id == prior.CommitAt(Address(witness.SharedContinuationState!)), "The catalog branch's published head changed.");
            Check(repository.LoadState(repository.ReadCurrentCommit(BranchNamed(repository, "catalog")).Commit.Id), upgraded: true, atSeven: 203);
            var catalogHistory = repository.ReadFrames(BranchNamed(repository, "catalog"));
            Check(repository.LoadState(catalogHistory.Single(commit => commit.Id == prior.CommitAt(Address(witness.CatalogState!))).Id), upgraded: true, atSeven: 202);
            Check(repository.LoadEvent(catalogHistory.Single(commit => commit.Id == prior.CommitAt(Address(witness.FollowupEvent!))).Id), upgraded: true, atSeven: 202);
            Need(repository.LoadEvent(catalogHistory.Single(commit => commit.Id == prior.CommitAt(Address(witness.CatalogAcceptanceEvent!))).Id) is CatalogEvent { Value: 41 },
                "The original generated Event must remain readable through its own historical origin S.");
            Check(repository.LoadEvent(repository.ReadFrames(BranchNamed(repository, "fork")).Single(commit => commit.Id == prior.CommitAt(Address(witness.RewriteEvent!))).Id), upgraded: true);
            Widget old = Check(repository.LoadState(initial), upgraded: true);
            Widget current = Check(repository.LoadState(repository.ReadCurrentCommit(BranchNamed(repository, "fork")).Commit.Id), upgraded: true, atSeven: 201);
            Need(!ReferenceEquals(old, current) && !ReferenceEquals(old.Numbers, current.Numbers),
                "Final independent reads under different exact schemas must retain separate mutable instances.");
        }
        WireInspector.Snapshot actual = WireInspector.Read(database);
        FrameAddress schema = Address(witness.RewriteSchema!), next = Address(witness.ContinuationState!);
        FrameAddress followupSchema = Address(witness.FollowupSchema!), sharedHead = Address(witness.SharedContinuationState!);
        CheckRefs(actual, followupSchema, ("main", Address(witness.MainState!)), ("fork", next), ("catalog", sharedHead));
        Need(CheckCatalog(actual, Address(witness.SeedSchema), current: false) == witness.WidgetDescriptorId &&
            CheckCatalog(actual, schema, current: true, version: 1) == witness.WidgetDescriptorId &&
            CheckCatalog(actual, schema, current: true) == witness.CurrentWidgetDescriptorId,
            "Fresh catalogs must preserve declared exact old/new descriptor identities.");
        Need(RequiredSchema(actual.Graph(Address(witness.SeedState))) == Address(witness.SeedSchema) &&
            RequiredSchema(actual.Graph(Address(witness.MainState!))) == schema &&
            RequiredSchema(actual.Graph(Address(witness.RewriteEvent!))) == schema &&
            RequiredSchema(actual.Graph(Address(witness.RewriteState!))) == schema && RequiredSchema(actual.Graph(next)) == schema,
            "All old/new/Event graph pins must remain exact after every owner closes.");
        CheckStorage(actual, actual.Graph(Address(witness.SeedState)), witness.SeedObjects);
        CheckStorage(actual, actual.Graph(Address(witness.RewriteState!)), witness.RewriteObjects!);
        CheckStorage(actual, actual.Graph(next), witness.ContinuationObjects!);
        FrameAddress catalogSeed = Address(witness.CatalogSeedState!), catalogHead = Address(witness.CatalogState!);
        Need(RequiredSchema(actual.Graph(catalogSeed)) == Address(witness.RewriteSchema!) &&
            RequiredSchema(actual.Graph(Address(witness.CatalogAcceptanceEvent!))) == Address(witness.CatalogSchema!) &&
            RequiredSchema(actual.Graph(catalogHead)) == Address(witness.CatalogSchema!) &&
            CheckEventCatalog(actual, Address(witness.CatalogSchema!), witness) == witness.CatalogEventDescriptorId,
            "Final catalog-branch graphs must preserve their exact full accepted catalog and declaration IDs.");
        CheckStorage(actual, actual.Graph(catalogSeed), witness.CatalogSeedObjects!);
        CheckStorage(actual, actual.Graph(catalogHead), witness.CatalogObjects!);
        FrameAddress followupEvent = Address(witness.FollowupEvent!);
        Need(RequiredSchema(actual.Graph(followupEvent)) == followupSchema && RequiredSchema(actual.Graph(sharedHead)) == followupSchema &&
            CheckEventCatalog(actual, followupSchema, witness) == witness.FollowupEventDescriptorId,
            "Final shared catalog must contain the complete State-unreachable declaration and exact followup graph pins.");
        CheckStorage(actual, actual.Graph(followupEvent), witness.FollowupObjects!);
        CheckStorage(actual, actual.Graph(sharedHead), witness.SharedContinuationObjects!);
        CheckFrames(actual, witness.SeedFrames.Concat(witness.RewriteFrames!).Concat(witness.ContinuationFrames!).Concat(witness.CatalogFrames!)
            .Concat(witness.FollowupFrames!).Concat(witness.SharedContinuationFrames!));
        Need(SameFiles(before, FileImages(database)), "Final readonly verification changed repository files.");
        return new { ReadOnlyFilesUnchanged = true, OldTagPreserved = true, MainAndForkValues = true, AccurateSchemaPins = true,
            ActualRefsVerified = true, CompleteStorageVerified = true, CompleteFramesPreserved = true, CrossSchemaStateReadsIndependent = true,
            CatalogColdValues = true, FollowupSharedColdValues = true };
    }

    private static S.V1 ReadHistoricalWidget(WireInspector.Snapshot actual, FrameAddress graph, uint id, uint descriptor) {
        CheckWidgetBase(actual, graph, id, 1, descriptor);
        var chain = actual.ObjectChain(graph, id);
        Need(chain.Entries.Count == 1, "The seed Widget must have one complete Base.");
        var header = WireInspector.Header(chain.Entries[0].Record.Body);
        BareValueReader reader = new(header.Body);
        S.V1 value = S.BodyV1.Read(ref reader, Schema(1));
        reader.EnsureFullyConsumed();
        return value;
    }

    private static void CheckRewrittenState(WireInspector.Snapshot actual, FrameAddress address, FrameAddress businessParent,
        Witness witness, FrameAddress schema, uint descriptor) {
        WireInspector.Graph graph = actual.Graph(address);
        Need(graph.Kind == WireInspector.GraphKind.State && graph.Parent == businessParent && graph.SourceGraph == Address(witness.SeedState) &&
            RequiredSchema(graph) == schema && graph.OwnedBatch is not null && graph.RootId == witness.RootId,
            "The first State must retain its exact V1 content source, business Parent, root ID and new Schema pin.");
        uint[] written = graph.Heads.Where(pair => pair.Value == graph.OwnedBatch).Select(pair => pair.Key).Order().ToArray();
        Need(written.SequenceEqual(new[] { witness.RootId, witness.ChildId }.Order()),
            "Every rewritten-live Widget must write Base; the untouched List must remain retained.");
        foreach (uint id in written) {
            var chain = actual.ObjectChain(address, id);
            Need(chain.Entries.Count == 1 && chain.Head == graph.OwnedBatch && chain.Entries[0].Record.Kind == ObjectVersionKind.Base &&
                chain.Entries[0].Record.PriorAddress is null && chain.Entries[0].SourceGraph == Address(witness.SeedState) &&
                chain.Entries[0].SchemaRoot == schema, "Rewritten Widget must have an actual new Base and exact source context.");
            CheckWidgetBase(actual, address, id, 2, descriptor);
        }
        StorageEvidence priorList = witness.SeedObjects.Single(row => row.Id == witness.ListId);
        Need(Storage(actual, graph).Single(row => row.Id == witness.ListId) == priorList &&
            actual.ObjectChain(address, witness.ListId).Entries[0].SchemaRoot == Address(witness.SeedSchema),
            "Retained List must keep old head/H and Base origin S0 despite selected new S1.");
    }
#endif

    private static uint CheckCatalog(WireInspector.Snapshot actual, FrameAddress schema, bool current, int? version = null) {
        var catalog = actual.Catalogs[schema];
        Need(catalog.WidgetDescriptors.ContainsKey(1) && catalog.WidgetDescriptors.ContainsKey(2) == current &&
            catalog.WidgetDescriptors.Count == (current ? 2 : 1) && catalog.DeclaredIds.Count == (current ? 2 : 1),
            "Actual catalog must declare exactly the stored Widget versions, independently of available CLR models.");
        if (current) { Need(catalog.WidgetDescriptors[1] != catalog.WidgetDescriptors[2], "Distinct Widget versions need distinct descriptor IDs."); }
        uint id = catalog.WidgetDescriptors[version ?? (current ? 2 : 1)];
        Need(catalog.DeclaredIds.Contains(id), "Widget descriptor must be declared by the selected catalog root.");
        return id;
    }

    private static void CheckWidgetBase(WireInspector.Snapshot actual, FrameAddress graph, uint id, int version, uint descriptor) {
        var origin = actual.ObjectChain(graph, id).Entries[0];
        Need(origin.Record.Kind == ObjectVersionKind.Base && origin.Record.PriorAddress is null && origin.SchemaRoot is not null,
            "Widget chain must start at a complete Base with its own source S.");
        var header = WireInspector.Header(origin.Record.Body);
        var catalog = actual.Catalogs[origin.SchemaRoot!.Value];
        Need(header.FormatVersion == actual.TypedBodyAbi && header.FormatVersion == 5 && header.Route == 1 && header.DescriptorId == descriptor &&
            catalog.WidgetDescriptors[version] == descriptor && catalog.DeclaredIds.Contains(descriptor),
            "Actual Base header must resolve its declared dynamic metaObjectId through the exact origin catalog.");
    }

    private static void CheckIndependentEvent(WireInspector.Snapshot actual, WireInspector.Graph graph, int objectCount) {
        Need(graph.Kind == WireInspector.GraphKind.Event && graph.SourceGraph is null && graph.OwnedBatch is not null &&
            graph.Heads.Count == objectCount && graph.Heads.Values.All(head => head == graph.OwnedBatch),
            "Event must own every reachable object independently of State.");
        foreach (uint id in graph.Heads.Keys) {
            var chain = actual.ObjectChain(graph.Address, id);
            Need(chain.Entries.Count == 1 && chain.Head == graph.OwnedBatch &&
                chain.Entries[0].Record.Kind == ObjectVersionKind.Base && chain.Entries[0].Record.PriorAddress is null &&
                chain.Entries[0].SourceGraph is null && chain.Entries[0].SchemaRoot == graph.SchemaRoot,
                "Every Event object must have one local Base with no prior content source and the Event's exact Schema pin.");
        }
    }

    private static void CheckStorage(WireInspector.Snapshot actual, WireInspector.Graph graph, StorageEvidence[] storage) {
        Need(graph.Heads.Keys.Order().SequenceEqual(storage.Select(row => row.Id).Order()), "Actual complete directory membership disagrees.");
        foreach (StorageEvidence row in storage) {
            var chain = actual.ObjectChain(graph.Address, row.Id);
            long measured = chain.Entries.Aggregate(0L, (sum, entry) => checked(sum + entry.Record.ActualPayloadBytes));
            Need(chain.Head == Address(row.Head) && graph.Heads[row.Id] == chain.Head && chain.H == row.H && measured == row.H,
                "Actual object head or decoded wire reconstruction payload H disagrees.");
        }
    }

    private static void CheckRefs(WireInspector.Snapshot actual, FrameAddress schema, params (string Branch, FrameAddress Address)[] branches) {
        Need(actual.BranchHeads.Count == branches.Length && actual.SharedSchemaRoot == schema,
            "Actual two formal refs must select the expected branch directory and shared Schema authority.");
        foreach (var branch in branches) { Need(actual.BranchHeads[branch.Branch] == branch.Address, "Actual formal branch root disagrees."); }
    }

    private static StateModelRegistry Models() {
        StateModelRegistry registry = new();
        registry.Register(S.Definition);
#if HISTORY_V2
        registry.Register(global::Atelia.DurableGraph.Generated.Family_70726F62652E707269766174652E756E726561636861626C65.Definition);
#endif
        return registry;
    }

    private static DurableSchema Schema(int version) => new(WidgetName, version,
        new DurableFieldInfo(1, version == 1 ? TypeTag.Int32 : TypeTag.Int64),
        DurableFieldInfo.Reference(2, TypeExpr.List(TypeExpr.Builtin(TypeTag.Int32))),
        DurableFieldInfo.Reference(3, TypeExpr.Named(WidgetName)));

    private static Widget Ring() {
        List<int> values = Enumerable.Range(0, 128).ToList();
        Widget root = new() { Value = 1, Numbers = values };
        root.Child = new() { Value = 2, Numbers = values, Child = root };
        return root;
    }

    private static Widget Check(IDurableObject? value, bool upgraded, int atSeven = 7) {
        Need(value is Widget, "Unexpected root CLR type.");
        Widget root = (Widget)value!;
        Need(root.Value == (upgraded ? 101 : 1) && root.Child is { } child && child.Value == (upgraded ? 102 : 2) &&
            ReferenceEquals(root, child.Child) && ReferenceEquals(root.Numbers, child.Numbers) && root.Numbers.Count == 128,
            "Widget ring, values or shared List disagree.");
        for (int i = 0; i < 128; i++) { Need(root.Numbers[i] == (i == 7 ? atSeven : i), "List contents disagree."); }
        return root;
    }

    private static StorageEvidence[] Storage(WireInspector.Snapshot actual, WireInspector.Graph graph) => graph.Heads.Keys.Order()
        .Select(id => { var chain = actual.ObjectChain(graph.Address, id); return new StorageEvidence(id, Hex(chain.Head), chain.H); }).ToArray();
    private static FrameImage[] Frames(WireInspector.Snapshot actual) => actual.Frames.OrderBy(frame => Hex(frame.Address), StringComparer.Ordinal)
        .Select(frame => new FrameImage(Hex(frame.Address), frame.Tag, frame.PayloadSha256, frame.TailMetaSha256)).ToArray();
    private static void CheckFrames(WireInspector.Snapshot actual, IEnumerable<FrameImage> images) {
        Dictionary<string, FrameImage> frames = Frames(actual).ToDictionary(frame => frame.Address, StringComparer.Ordinal);
        foreach (FrameImage image in images) {
            Need(frames.TryGetValue(image.Address, out FrameImage? current) && current == image,
                "A previously completed frame's address/tag/payload/tail metadata changed or disappeared.");
        }
    }

    private static FrameAddress RequiredSchema(WireInspector.Graph graph) => graph.SchemaRoot ?? throw new InvalidDataException("Missing exact Schema root.");
    private static Dictionary<string, string> FileImages(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(root, path), path => Hash(File.ReadAllBytes(path)));
    private static bool SameFiles(Dictionary<string, string> before, Dictionary<string, string> after) =>
        before.Count == after.Count && before.All(pair => after.TryGetValue(pair.Key, out string? value) && value == pair.Value);
    private static object AssemblyEvidence(Assembly assembly) => new { Name = assembly.GetName().Name, assembly.FullName,
        Path = assembly.Location, Sha256 = Hash(File.ReadAllBytes(assembly.Location)) };
    private static Witness ReadWitness(string path) => JsonSerializer.Deserialize<Witness>(File.ReadAllText(path), Json)
        ?? throw new InvalidDataException("Missing witness.");
    private static void WriteJson(string path, object value) {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, Json));
    }
    private static BranchId BranchNamed(Repository repository, string label) => repository.ListBranches().Single(branch => branch.DisplayLabel == label).Id;
    private static string Hex(FrameAddress address) {
        Span<byte> bytes = stackalloc byte[FrameAddress.EncodedSize];
        Need(address.TryWrite(bytes), "Invalid address.");
        return Convert.ToHexString(bytes);
    }
    private static FrameAddress Address(string encoded) {
        Need(FrameAddress.TryRead(Convert.FromHexString(encoded), out FrameAddress address), "Invalid witness address.");
        return address;
    }
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static void Need(bool condition, string message) {
        if (!condition) { throw new InvalidDataException(message); }
    }
}
