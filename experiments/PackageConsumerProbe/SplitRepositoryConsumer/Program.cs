extern alias persistence;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Atelia.Binary;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using Atelia.VersionStore;
using SplitRepositoryPackage;
using persistence::Atelia.DurableGraph;
using persistence::Atelia.DurableGraph.Persistence;
using S = global::Atelia.DurableGraph.Generated.Family_7061636B6167652E73706C69742E776964676574;

// Witnesses are diagnostic. Physical checks independently read public backing
// stores after every Repository and checkout in that operation has closed.
internal static class Program {
    private const string Protocol = "DG.SplitRepositoryConsumer.v1";
    private const string WidgetName = "package.split.widget";
    private static readonly DurableSchema PrivateUnused = new("probe.private.unreachable", 1, new DurableFieldInfo(1, TypeTag.Int32));
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private sealed record StorageEvidence(uint Id, string Head, long H);
    private sealed record FrameImage(string Address, uint Tag, string PayloadSha256, string TailMetaSha256);
    private sealed record Witness(string SeedState, string SeedSchema, uint RootId, uint ChildId, uint ListId,
        uint WidgetDescriptorId, StorageEvidence[] SeedObjects, FrameImage[] SeedFrames,
        string? MainState = null, string? RewriteEvent = null, string? RewriteState = null, string? RewriteSchema = null,
        uint? CurrentWidgetDescriptorId = null, StorageEvidence[]? RewriteObjects = null, FrameImage[]? RewriteFrames = null,
        string? ContinuationState = null, StorageEvidence[]? ContinuationObjects = null, FrameImage[]? ContinuationFrames = null,
        string? PrivateSeedState = null, string? PrivateState = null, string? PrivateSchema = null,
        uint? PrivateRootId = null, uint? PrivateListId = null, uint? PrivateUnusedDescriptorId = null,
        StorageEvidence[]? PrivateSeedObjects = null, StorageEvidence[]? PrivateObjects = null, FrameImage[]? PrivateFrames = null);
    private sealed record SharedRefImage(RefRevision Revision, byte[] Payload);

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
        uint rootId;
        using (Repository repository = Repository.CreateNew(database, Models(), schemaWriteMode: SchemaWriteMode.Shared)) {
            using BranchCheckout main = repository.CreateBranch("main", Ring());
            repository.CreateTag("initial", main.Head);
            state = main.Head.RevisionAddress;
            rootId = main.StateId!.Value.Value;
            Check(main.State, upgraded: false);
        }
        WireInspector.Snapshot actual = WireInspector.Read(database);
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
            CheckpointAddress initial = repository.ResolveTag("initial");
            Need(initial.RevisionAddress == seed, "The initial tag changed.");
            Check(repository.ReadState(initial), upgraded: true);
            Need(Widget.UpgradeCalls == 2, "Readonly materialization must Upgrade both historical Widget rows.");
            var pair = repository.ReadPair(initial, initial);
            Widget first = Check(pair.First, upgraded: true), second = Check(pair.Second, upgraded: true);
            Need(!ReferenceEquals(first, second) && ReferenceEquals(first.Numbers, second.Numbers),
                "Historical rewrite excludes Widgets; the unchanged same-S List remains shareable in this fixture.");
            upgrades = Widget.UpgradeCalls;
        }
        WireInspector.Snapshot after = WireInspector.Read(database);
        CheckRefs(after, schema, ("main", seed));
        CheckStorage(after, after.Graph(seed), witness.SeedObjects);
        CheckFrames(after, witness.SeedFrames);
        Need(SameFiles(before, FileImages(database)), "Readonly accepted-history Upgrade changed repository files.");
        return new { ReadOnlyFilesUnchanged = true, ExactHistoricalDto = true, ReadOnlyUpgrade = true, UpgradeCalls = upgrades,
            SameSUnchangedListShared = true, RewriteWidgetsIndependent = true, ActualRefsVerified = true, CompleteFramesPreserved = true };
    }

    private static object Rewrite(string database, string witnessPath) {
        Witness witness = ReadWitness(witnessPath);
        FrameAddress seed = Address(witness.SeedState), oldSchema = Address(witness.SeedSchema), mainState;
        WireInspector.Snapshot original = WireInspector.Read(database);
        CheckRefs(original, oldSchema, ("main", seed));
        CheckStorage(original, original.Graph(seed), witness.SeedObjects);
        using (Repository repository = Repository.OpenExisting(database, Models(), schemaWriteMode: SchemaWriteMode.Shared)) {
            using BranchCheckout main = repository.Checkout("main");
            Check(main.State, upgraded: true);
            Need(main.StateRevisionAddress == seed && main.StateId!.Value.Value == witness.RootId,
                "Cold main must select the exact historical State and root ID.");
            mainState = main.CommitState().RevisionAddress;
        }
        WireInspector.Snapshot latest = WireInspector.Read(database);
        FrameAddress schema = RequiredSchema(latest.Graph(mainState));
        Need(schema != oldSchema, "The automatically captured V2 closure must advance the shared catalog.");
        uint descriptor = CheckCatalog(latest, schema, current: true);
        CheckRefs(latest, schema, ("main", mainState));
        CheckRewrittenState(latest, mainState, seed, witness, schema, descriptor);

        // Closing after Fork makes its ref-only nature observable independently.
        int beforeFork = Widget.UpgradeCalls;
        using (Repository repository = Repository.OpenExisting(database, Models(), schemaWriteMode: SchemaWriteMode.Shared)) {
            using BranchCheckout fork = repository.Fork("fork", repository.ResolveTag("initial"));
            Need(fork.Head.RevisionAddress == seed && fork.StateRevisionAddress == seed && fork.StateId!.Value.Value == witness.RootId,
                "Historical Fork must restore the tag's nearest State, independently of latest shared S.");
            Check(fork.State, upgraded: true);
            Need(Widget.UpgradeCalls == beforeFork + 2, "Historical Fork must execute accepted V1 history for both Widgets.");
        }
        WireInspector.Snapshot forked = WireInspector.Read(database);
        CheckRefs(forked, schema, ("main", mainState), ("fork", seed));
        Need(Frames(forked).SequenceEqual(Frames(latest)), "Historical ref-only Fork appended or changed completed frames.");
        CheckStorage(forked, forked.Graph(seed), witness.SeedObjects);

        FrameAddress eventAddress, rewritten;
        using (Repository repository = Repository.OpenExisting(database, Models(), schemaWriteMode: SchemaWriteMode.Shared)) {
            using BranchCheckout fork = repository.Checkout("fork");
            Widget root = Check(fork.State, upgraded: true);
            ObjectId rootId = fork.StateId!.Value;
            eventAddress = fork.CommitEvent(root).RevisionAddress;
            Need(fork.Head.RevisionAddress == eventAddress && fork.StateRevisionAddress == seed && fork.StateId == rootId &&
                ReferenceEquals(root, fork.State), "Confirmed Event must discard its State candidate and retain the original State instance/ID/source.");
            Check(repository.ReadEvent(fork.Head), upgraded: true);
            rewritten = fork.CommitState().RevisionAddress;
            Need(fork.StateRevisionAddress == rewritten && fork.StateId == rootId && ReferenceEquals(root, fork.State),
                "The following State must accept the original upgraded instances and IDs.");
            var same = repository.ReadPair(fork.Head, fork.Head);
            Need(ReferenceEquals(Check(same.First, upgraded: true), Check(same.Second, upgraded: true)),
                "Same-S exact no-rewrite cycle must remain Pair-shareable in this fixture.");
            var mixed = repository.ReadPair(repository.ResolveTag("initial"), fork.Head);
            Widget oldRoot = Check(mixed.First, upgraded: true), newRoot = Check(mixed.Second, upgraded: true);
            Need(!ReferenceEquals(oldRoot, newRoot) && !ReferenceEquals(oldRoot.Numbers, newRoot.Numbers) &&
                oldRoot.Value == newRoot.Value && oldRoot.Child!.Value == newRoot.Child!.Value && oldRoot.Numbers.SequenceEqual(newRoot.Numbers),
                "Different exact-S Pair environments must preserve equal contents without sharing this fixture's mutable instances.");
        }
        WireInspector.Snapshot actual = WireInspector.Read(database);
        CheckRefs(actual, schema, ("main", mainState), ("fork", rewritten));
        WireInspector.Graph domainEvent = actual.Graph(eventAddress);
        Need(domainEvent.Kind == WireInspector.GraphKind.Event && domainEvent.Parent == seed && domainEvent.SourceGraph == seed &&
            RequiredSchema(domainEvent) == schema, "Event must separately pin S1 while retaining State0 as its content source.");
        CheckRewrittenState(actual, rewritten, eventAddress, witness, schema, descriptor);
        Need(actual.ObjectChain(seed, witness.ListId).Head == actual.ObjectChain(rewritten, witness.ListId).Head &&
            RequiredSchema(actual.Graph(seed)) != RequiredSchema(actual.Graph(rewritten)),
            "The cross-S Pair witness must have the same retained List ID/head and current contents.");
        Need(CheckCatalog(actual, schema, current: true, version: 1) == witness.WidgetDescriptorId,
            "The accepted V1 descriptor must reuse its original metaObjectId.");
        CheckFrames(actual, witness.SeedFrames);
        HashSet<string> previous = witness.SeedFrames.Select(frame => frame.Address).ToHashSet(StringComparer.Ordinal);
        Witness next = witness with { MainState = Hex(mainState), RewriteEvent = Hex(eventAddress), RewriteState = Hex(rewritten),
            RewriteSchema = Hex(schema), CurrentWidgetDescriptorId = descriptor, RewriteObjects = Storage(actual, actual.Graph(rewritten)),
            RewriteFrames = Frames(actual).Where(frame => !previous.Contains(frame.Address)).ToArray() };
        WriteJson(witnessPath, next);
        return new { next.MainState, next.RewriteEvent, next.RewriteState, next.RewriteSchema, Objects = next.RewriteObjects,
            HistoricalForkUsesStateS = true, LatestCatalogRemainsIndependent = true, EventCandidateDiscardKeepsStateRewrite = true,
            RewrittenLiveWidgetBases = 2, RetainedListHeadAndH = true, SameSPairShared = true, CrossSPairIndependent = true,
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
        Dictionary<string, string> beforeUnconfigured = FileImages(database);
        using (Repository unconfigured = Repository.OpenExisting(database, Models())) {
            Need(!unconfigured.IsReadOnly && unconfigured.SchemaWriteMode is null,
                "Missing Schema write configuration must retain writable backing resources without admitting business saves.");
            using BranchCheckout main = unconfigured.Checkout("main");
            Widget root = Check(main.State, upgraded: true);
            FrameAddress head = main.Head.RevisionAddress;
            FrameAddress? state = main.StateRevisionAddress;
            ObjectId? rootId = main.StateId;
            Check(unconfigured.ReadState(unconfigured.GetHead("main")), upgraded: true);
            foreach (Action save in new Action[] { () => main.CommitState(), () => main.CommitEvent(root) }) {
                bool rejected = false;
                try { save(); }
                catch (InvalidOperationException) { rejected = true; }
                Need(rejected && !unconfigured.IsFaulted && main.Head.RevisionAddress == head &&
                    unconfigured.GetHead("main").RevisionAddress == head && main.StateRevisionAddress == state &&
                    main.StateId == rootId && ReferenceEquals(main.State, root),
                    "An unconfigured State/Event save must be rejected without advancing or faulting the loaded checkout.");
            }
        }
        Need(SameFiles(beforeUnconfigured, FileImages(database)), "Unconfigured business-save rejection changed repository files.");
        FrameAddress continued;
        using (Repository repository = Repository.OpenExisting(database, Models(), schemaWriteMode: SchemaWriteMode.Shared)) {
            using BranchCheckout fork = repository.Checkout("fork");
            Widget root = Check(fork.State, upgraded: true);
            Need(Widget.UpgradeCalls == 0 && fork.StateRevisionAddress == rewritten && fork.StateId!.Value.Value == witness.RootId,
                "The fresh current State must retain root identity without another historical Upgrade.");
            root.Numbers[7] = 201;
            continued = fork.CommitState().RevisionAddress;
            Check(repository.ReadState(repository.ResolveTag("initial")), upgraded: true);
            Check(repository.ReadState(repository.GetHead("fork")), upgraded: true, atSeven: 201);
        }
        WireInspector.Snapshot actual = WireInspector.Read(database);
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
        next = PrivateStory(database, next, actual);
        WriteJson(witnessPath, next);
        return new { next.ContinuationState, Objects = updated, SingleListDelta = true, ExactPrior = true, IdsPreserved = true,
            ActualWireH = true, RetainedWidgetHeads = true, ActualRefsVerified = true, CompleteFramesPreserved = true,
            UnconfiguredSaveRejected = true, next.PrivateSeedState, next.PrivateState, next.PrivateSchema,
            PrivateObjects = next.PrivateObjects, PrivateUnusedDescriptor = next.PrivateUnusedDescriptorId,
            PrivateFrozenCatalog = true, PrivateColdListDelta = true, PrivateSharedRootUnchanged = true, PrivateSharedSaveRejected = true };
    }

    private static Witness PrivateStory(string database, Witness witness, WireInspector.Snapshot shared) {
        SharedRefImage sharedRef = ReadSharedRef(database);
        FrameAddress initial;
        uint rootId;
        using (Repository repository = Repository.OpenExisting(database, Models())) {
            Widget root = Ring();
            root.Value += 100;
            root.Child!.Value += 100;
            root.Numbers[7] = 201;
            using BranchCheckout created = repository.CreatePrivateBranch("private", root, [Schema(2), PrivateUnused]);
            initial = created.Head.RevisionAddress;
            rootId = created.StateId!.Value.Value;
            Need(created.StateRevisionAddress == initial && ReferenceEquals(created.State, root),
                "The explicit private entry must create and accept the supplied fresh State without an Open write mode.");
            Check(created.State, upgraded: true, atSeven: 201);
        }
        WireInspector.Snapshot seeded = WireInspector.Read(database);
        WireInspector.Graph seedGraph = seeded.Graph(initial);
        FrameAddress privateSchema = RequiredSchema(seedGraph);
        Need(privateSchema != Address(witness.RewriteSchema!) && seedGraph.Kind == WireInspector.GraphKind.State &&
            seedGraph.Parent is null && seedGraph.SourceGraph is null && seedGraph.RootId == rootId &&
            seedGraph.Heads.Count == 3 && seedGraph.OwnedBatch is not null,
            "The private new root must use its distinct full view and no business source.");
        uint listId = seedGraph.Heads.Keys.Single(id => WireInspector.Header(seeded.ObjectChain(initial, id).Entries[0].Record.Body).Route == 2);
        foreach (uint id in seedGraph.Heads.Keys) {
            var chain = seeded.ObjectChain(initial, id);
            Need(chain.Head == seedGraph.OwnedBatch && chain.Entries.Count == 1 && chain.Entries[0].Record.Kind == ObjectVersionKind.Base &&
                chain.Entries[0].Record.PriorAddress is null && chain.Entries[0].SourceGraph is null &&
                chain.Entries[0].SchemaRoot == privateSchema && chain.H == chain.Entries[0].Record.ActualPayloadBytes,
                "Every new private live object must start at its own actual Base and reset wire H.");
            if (id != listId) { CheckWidgetBase(seeded, initial, id, 2, witness.CurrentWidgetDescriptorId!.Value); }
        }
        uint unused = CheckPrivateCatalog(seeded, privateSchema, witness);
        StorageEvidence[] initialRows = Storage(seeded, seedGraph);
        RejectOrdinaryPrivateSaves(database, configuredShared: false, initial);
        RejectOrdinaryPrivateSaves(database, configuredShared: true, initial);

        FrameAddress next;
        using (Repository repository = Repository.OpenExisting(database, Models())) {
            using BranchCheckout current = repository.CheckoutPrivate("private");
            Widget root = Check(current.State, upgraded: true, atSeven: 201);
            Need(current.Head.RevisionAddress == initial && current.StateRevisionAddress == initial && current.StateId!.Value.Value == rootId,
                "Cold explicit private checkout must import its own exact root identity and source.");
            root.Numbers[7] = 202;
            next = current.CommitState().RevisionAddress;
            Need(ReferenceEquals(root, current.State) && current.StateId!.Value.Value == rootId,
                "Confirmed private State must accept the original edited instances and identity.");
        }
        WireInspector.Snapshot actual = WireInspector.Read(database);
        WireInspector.Graph graph = actual.Graph(next);
        Need(graph.Parent == initial && graph.SourceGraph == initial && graph.RootId == rootId &&
            RequiredSchema(graph) == privateSchema && graph.OwnedBatch is not null &&
            graph.Heads.Where(pair => pair.Value == graph.OwnedBatch).Select(pair => pair.Key).SequenceEqual(new[] { listId }),
            "Private cold continuation must write only the changed List against its exact State and frozen S.");
        var priorList = seeded.ObjectChain(initial, listId);
        var currentList = actual.ObjectChain(next, listId);
        var delta = currentList.Entries[^1];
        Need(delta.Record.Kind == ObjectVersionKind.Delta && delta.Record.PriorAddress == priorList.Head &&
            delta.ContainingBatch == graph.OwnedBatch && delta.SourceGraph == initial && delta.SchemaRoot == privateSchema &&
            currentList.H == checked(priorList.H + delta.Record.ActualPayloadBytes),
            "Private List Delta must preserve its exact prior and add actual encoded payload bytes to H.");
        StorageEvidence[] rows = Storage(actual, graph);
        Need(rows.Select(row => row.Id).SequenceEqual(initialRows.Select(row => row.Id)), "Private continuation changed complete membership IDs.");
        foreach (StorageEvidence row in initialRows.Where(row => row.Id != listId)) {
            Need(rows.Single(candidate => candidate.Id == row.Id) == row, "Private continuation changed a retained Widget head/H.");
        }
        Need(CheckPrivateCatalog(actual, privateSchema, witness) == unused, "Private frozen declarations or IDs changed after cold saving.");
        CheckRefs(actual, Address(witness.RewriteSchema!), ("main", Address(witness.MainState!)),
            ("fork", Address(witness.ContinuationState!)), ("private", next));
        CheckStorage(actual, actual.Graph(Address(witness.ContinuationState!)), witness.ContinuationObjects!);
        CheckFrames(actual, Frames(shared));
        Need(SameSharedRef(sharedRef, ReadSharedRef(database)), "Private saves changed the actual shared ref revision or payload.");
        Dictionary<string, string> beforeRead = FileImages(database);
        using (Repository reader = Repository.OpenReadOnlyExisting(database, Models())) {
            Check(reader.ReadState(reader.GetHead("main")), upgraded: true);
            Check(reader.ReadState(reader.GetHead("fork")), upgraded: true, atSeven: 201);
            Check(reader.ReadState(reader.GetHead("private")), upgraded: true, atSeven: 202);
        }
        Need(SameFiles(beforeRead, FileImages(database)), "Readonly shared/private cold recovery changed repository files.");
        HashSet<string> oldFrames = Frames(shared).Select(frame => frame.Address).ToHashSet(StringComparer.Ordinal);
        return witness with { PrivateSeedState = Hex(initial), PrivateState = Hex(next), PrivateSchema = Hex(privateSchema),
            PrivateRootId = rootId, PrivateListId = listId, PrivateUnusedDescriptorId = unused,
            PrivateSeedObjects = initialRows, PrivateObjects = rows,
            PrivateFrames = Frames(actual).Where(frame => !oldFrames.Contains(frame.Address)).ToArray() };
    }

    private static void RejectOrdinaryPrivateSaves(string database, bool configuredShared, FrameAddress initial) {
        Dictionary<string, string> before = FileImages(database);
        using (Repository repository = Repository.OpenExisting(database, Models(),
            schemaWriteMode: configuredShared ? SchemaWriteMode.Shared : null)) {
            using BranchCheckout ordinary = repository.Checkout("private");
            Widget root = Check(ordinary.State, upgraded: true, atSeven: 201);
            ObjectId? rootId = ordinary.StateId;
            foreach (Action save in new Action[] { () => ordinary.CommitState(), () => ordinary.CommitEvent(root) }) {
                bool rejected = false;
                try { save(); }
                catch (InvalidOperationException) { rejected = true; }
                Need(rejected && !repository.IsFaulted && ordinary.Head.RevisionAddress == initial &&
                    ordinary.StateRevisionAddress == initial && ordinary.StateId == rootId && ReferenceEquals(ordinary.State, root) &&
                    repository.GetHead("private").RevisionAddress == initial,
                    "Ordinary private-head save must reject missing mode or the complete unmatched declaration without advancing or faulting.");
            }
        }
        Need(SameFiles(before, FileImages(database)), "Rejected ordinary private-head saves changed repository files.");
    }

    private static uint CheckPrivateCatalog(WireInspector.Snapshot actual, FrameAddress schema, Witness witness) {
        var catalog = actual.Catalogs[schema];
        Need(catalog.DeclaredIds.Count == 3 && catalog.WidgetDescriptors.Count == 2 &&
            catalog.WidgetDescriptors[1] == witness.WidgetDescriptorId && catalog.WidgetDescriptors[2] == witness.CurrentWidgetDescriptorId &&
            catalog.PrivateUnusedDescriptor is { } unused && catalog.DeclaredIds.Contains(unused) &&
            unused != witness.WidgetDescriptorId && unused != witness.CurrentWidgetDescriptorId,
            "Private full catalog must retain unreachable V1/current V2 IDs and declare the exact unused Int32 schema.");
        return catalog.PrivateUnusedDescriptor!.Value;
    }

    private static SharedRefImage ReadSharedRef(string database) {
        using var data = Atelia.FrameStore.FrameStore.OpenReadOnly(Path.Combine(database, "data"));
        using var versions = Atelia.VersionStore.VersionStore.OpenReadOnly(Path.Combine(database, "versions"), data);
        RefBytesSnapshot snapshot = versions.ReadRef(2).Unwrap();
        return new(snapshot.Revision, snapshot.Payload.ToArray());
    }

    private static bool SameSharedRef(SharedRefImage before, SharedRefImage after) =>
        before.Revision.Equals(after.Revision) && before.Payload.AsSpan().SequenceEqual(after.Payload);

    private static object Verify(string database, string witnessPath) {
        Witness witness = ReadWitness(witnessPath);
        Dictionary<string, string> before = FileImages(database);
        using (Repository repository = Repository.OpenReadOnlyExisting(database, Models())) {
            CheckpointAddress initial = repository.ResolveTag("initial");
            Need(initial.RevisionAddress == Address(witness.SeedState), "The immutable initial tag moved.");
            Check(repository.ReadState(initial), upgraded: true);
            Check(repository.ReadState(repository.GetHead("main")), upgraded: true);
            Check(repository.ReadState(repository.GetHead("fork")), upgraded: true, atSeven: 201);
            Need(repository.GetHead("private").RevisionAddress == Address(witness.PrivateState!), "The private published head changed.");
            Check(repository.ReadState(repository.GetHead("private")), upgraded: true, atSeven: 202);
            Check(repository.ReadEvent(repository.ReadFrames("fork").Single(frame => frame.RevisionAddress == Address(witness.RewriteEvent!))), upgraded: true);
            var pair = repository.ReadPair(initial, repository.GetHead("fork"));
            Widget old = Check(pair.First, upgraded: true), current = Check(pair.Second, upgraded: true, atSeven: 201);
            Need(!ReferenceEquals(old, current) && !ReferenceEquals(old.Numbers, current.Numbers),
                "Final cross-S Pair must retain independent mutable instances in this fixture.");
        }
        WireInspector.Snapshot actual = WireInspector.Read(database);
        FrameAddress schema = Address(witness.RewriteSchema!), next = Address(witness.ContinuationState!);
        CheckRefs(actual, schema, ("main", Address(witness.MainState!)), ("fork", next), ("private", Address(witness.PrivateState!)));
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
        FrameAddress privateSeed = Address(witness.PrivateSeedState!), privateHead = Address(witness.PrivateState!);
        Need(RequiredSchema(actual.Graph(privateSeed)) == Address(witness.PrivateSchema!) &&
            RequiredSchema(actual.Graph(privateHead)) == Address(witness.PrivateSchema!) &&
            CheckPrivateCatalog(actual, Address(witness.PrivateSchema!), witness) == witness.PrivateUnusedDescriptorId,
            "Final private graphs must preserve their exact full frozen catalog and declaration IDs.");
        CheckStorage(actual, actual.Graph(privateSeed), witness.PrivateSeedObjects!);
        CheckStorage(actual, actual.Graph(privateHead), witness.PrivateObjects!);
        CheckFrames(actual, witness.SeedFrames.Concat(witness.RewriteFrames!).Concat(witness.ContinuationFrames!).Concat(witness.PrivateFrames!));
        Need(SameFiles(before, FileImages(database)), "Final readonly verification changed repository files.");
        return new { ReadOnlyFilesUnchanged = true, OldTagPreserved = true, MainAndForkValues = true, AccurateSchemaPins = true,
            ActualRefsVerified = true, CompleteStorageVerified = true, CompleteFramesPreserved = true, CrossSPairIndependent = true,
            PrivateColdValues = true };
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
