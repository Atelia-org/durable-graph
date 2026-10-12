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
using persistence::Atelia.DurableGraph.Persistence.MetaBootstrapP0;
using S = global::Atelia.DurableGraph.Generated.Family_7061636B6167652E73706C69742E776964676574;

// The witness is diagnostic data. Every lane independently opens the actual
// Repository and reads its necessary refs, graph pins and object chains.
internal static class Program {
    private const string Protocol = "DG.SplitRepositoryConsumer.v1";
    private const string WidgetName = "package.split.widget";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly DurableSchema Extra = new("package.split.extra", 1, new DurableFieldInfo(1, TypeTag.Int64));
    private sealed record StorageEvidence(uint Id, string Head, long H);
    private sealed record FrameImage(string Address, uint Tag, int TailMetaLength, string PayloadAndMetaSha256);
    private sealed record Witness(string SeedState, string SeedSchema, uint RootId, uint ChildId, uint ListId,
        uint WidgetDescriptorId, StorageEvidence[] SeedObjects, FrameImage[] SeedFrames,
        string? MainState = null, string? RewriteState = null, string? RewriteSchema = null,
        uint? CurrentWidgetDescriptorId = null, StorageEvidence[]? RewriteObjects = null, FrameImage[]? RewriteFrames = null,
        string? ContinuationState = null, StorageEvidence[]? ContinuationObjects = null, FrameImage[]? ContinuationFrames = null);

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
        WriteJson(Path.Combine(Path.GetDirectoryName(witnessPath)!, lane + ".evidence.json"), new {
            Protocol, Lane = lane, Passed = true, ProcessId = Environment.ProcessId,
            TypedBodyAbi = P0TypedBodyCodec.FormatVersion,
            RuntimeAssembly = AssemblyEvidence(typeof(DurableSchema).Assembly),
            PersistenceAssembly = AssemblyEvidence(typeof(Repository).Assembly),
            StorageAssembly = AssemblyEvidence(typeof(ObjectVersionRecord).Assembly),
            HostAssembly = AssemblyEvidence(typeof(Widget).Assembly), Observations = observations,
        });
        Console.WriteLine("SplitRepositoryConsumer:" + lane + ":Passed");
        return 0;
    }

#if HISTORY_V1
    private static object Seed(string database, string witnessPath) {
        Witness witness;
        using (Repository repository = Repository.CreateMetaBootstrapNew(database, Models())) {
            using BranchCheckout main = repository.CreateBranch("main", Ring());
            repository.CreateTag("initial", main.Head);
            Widget root = Check(main.State, upgraded: false);
            P0GraphSnapshot graph = repository.MetaBootstrapStore.ReadGraph(main.Head.RevisionAddress);
            FrameAddress schema = RequiredSchema(graph);
            P0CatalogView catalog = CheckCatalog(repository, schema, current: false);
            StorageEvidence[] objects = Storage(main);
            uint list = main.Baseline!.Objects.Single(pair => pair.Value.Current.Kind == ObjectStateKind.List).Key.Value;
            uint child = main.Baseline.Objects.Single(pair => pair.Key.Value != graph.RootId.Value && pair.Key.Value != list).Key.Value;
            Need(objects.Length == 3 && graph.RootId.Value == main.StateId!.Value.Value, "Seed must have complete three-object membership.");
            CheckStorage(repository, graph, objects);
            CheckRefs(repository, schema, ("main", graph.Address));
            uint descriptor = catalog.SchemaNodeId(Schema(1)).Value;
            CheckWidgetBase(repository.MetaBootstrapStore, graph.Address, graph.RootId.Value, Schema(1), descriptor);
            CheckWidgetBase(repository.MetaBootstrapStore, graph.Address, child, Schema(1), descriptor);
            witness = new(Hex(graph.Address), Hex(schema), graph.RootId.Value, child, list, descriptor, objects, Frames(Resources(repository)));
            Need(root.Numbers[7] == 7, "Seed contents changed before closure.");
        }
        WriteJson(witnessPath, witness);
        return new { SeedState = witness.SeedState, SeedSchema = witness.SeedSchema, Objects = witness.SeedObjects,
            WidgetDescriptorId = witness.WidgetDescriptorId, RingAndSharedList = true, ActualRefsVerified = true, CompleteStorageVerified = true };
    }
#endif

#if HISTORY_V2
    private static object Upgrade(string database, string witnessPath) {
        Witness witness = ReadWitness(witnessPath);
        Dictionary<string, string> before = FileImages(database);
        int upgrades;
        using (Repository repository = Repository.OpenMetaBootstrapReadOnlyExisting(database, Models())) {
            CheckRefs(repository, Address(witness.SeedSchema), ("main", Address(witness.SeedState)));
            Need(CheckCatalog(repository, Address(witness.SeedSchema), current: false).SchemaNodeId(Schema(1)).Value == witness.WidgetDescriptorId,
                "The persisted V1 descriptor ID changed.");
            DecodedRevision decoded = DecodedRevision.ReadSplit(repository.MetaBootstrapStore, Address(witness.SeedState), Models());
            Need(Widget.UpgradeCalls == 0 && decoded.Objects.Count == 3, "Exact accepted V1 reading must not normalize.");
            S.V1 root = decoded.GetRequired(new(witness.RootId)).GetState<S.V1>();
            S.V1 child = decoded.GetRequired(new(witness.ChildId)).GetState<S.V1>();
            Need(root.Segment0Field1 == 1 && child.Segment0Field1 == 2 && root.Segment0Field2.Value == witness.ListId &&
                child.Segment0Field2 == root.Segment0Field2 && root.Segment0Field3.Value == witness.ChildId && child.Segment0Field3.Value == witness.RootId,
                "Exact historical DTO values or reference IDs disagree.");
            Check(repository.ReadState(repository.ResolveTag("initial")), upgraded: true);
            Need(Widget.UpgradeCalls == 2, "Readonly materialization must Upgrade both historical Widget rows.");
            var pair = repository.ReadPair(repository.ResolveTag("initial"), repository.ResolveTag("initial"));
            Widget first = Check(pair.First, upgraded: true), second = Check(pair.Second, upgraded: true);
            Need(!ReferenceEquals(first, second) && ReferenceEquals(first.Numbers, second.Numbers),
                "Historical rewrite excludes Widgets; the unchanged same-S List remains shareable.");
            P0GraphSnapshot graph = repository.MetaBootstrapStore.ReadGraph(Address(witness.SeedState));
            CheckStorage(repository, graph, witness.SeedObjects);
            CheckFrames(Resources(repository), witness.SeedFrames);
            Need(repository.MetaBootstrapStore.AppendedFrames == 0 && repository.SharedSchemaRoot == Address(witness.SeedSchema),
                "Readonly Upgrade must not register V2 or change storage roots.");
            upgrades = Widget.UpgradeCalls;
        }
        Need(SameFiles(before, FileImages(database)), "Readonly accepted-history Upgrade changed repository files.");
        return new { ReadOnlyFilesUnchanged = true, ExactHistoricalDto = true, ReadOnlyUpgrade = true, UpgradeCalls = upgrades,
            SameSUnchangedListShared = true, RewriteWidgetsIndependent = true, ActualRefsVerified = true, CompleteFramesPreserved = true };
    }

    private static object Rewrite(string database, string witnessPath) {
        Witness witness = ReadWitness(witnessPath);
        Witness nextWitness;
        using (Repository repository = Repository.OpenMetaBootstrapExisting(database, Models())) {
            CheckRefs(repository, Address(witness.SeedSchema), ("main", Address(witness.SeedState)));
            FrameAddress mainState, schema;
            using (BranchCheckout main = repository.Checkout("main")) {
                Check(main.State, upgraded: true);
                Need(Storage(main).SequenceEqual(witness.SeedObjects), "Cold source heads/H must come from the old State.");
                CheckRewrite(main, witness, expected: true);
                main.Catalog.Set(Extra); // No business CLR model exists for this metadata-only definition.
                mainState = main.CommitState().RevisionAddress;
                schema = repository.SharedSchemaRoot!.Value;
                Need(schema != Address(witness.SeedSchema), "The V2 closure and unknown Extra must advance the shared catalog.");
                CheckRewrittenState(repository, main, mainState, witness, schema);
            }
            // The latest shared authority now contains V2, but the selected historical
            // tag still restores through its own State.S0 execution environment.
            CheckpointAddress selected = repository.ResolveTag("initial");
            long framesBeforeFork = repository.MetaBootstrapStore.AppendedFrames;
            using BranchCheckout fork = repository.Fork("fork", selected);
            Need(repository.MetaBootstrapStore.AppendedFrames == framesBeforeFork && repository.SharedSchemaRoot == schema,
                "Historical ref-only Fork must not append a graph or rewind shared S.");
            Need(fork.Head.RevisionAddress == Address(witness.SeedState) && fork.StateRevisionAddress == Address(witness.SeedState) &&
                fork.BindingEnvironment!.SelectedSchemaView.Snapshot!.Address == Address(witness.SeedSchema) &&
                fork.Baseline!.SelectedSchemaView!.Snapshot!.Address == Address(witness.SeedSchema) &&
                fork.Catalog.Accepted!.Snapshot!.Address == schema,
                "Fork must separate selected State.S0 execution/source from latest S1 write authority.");
            Check(fork.State, upgraded: true);
            CheckRewrite(fork, witness, expected: true);
            Need(Storage(fork).SequenceEqual(witness.SeedObjects), "Fork must import complete old IDs and wire heads/H.");
            CheckRefs(repository, schema, ("main", mainState), ("fork", Address(witness.SeedState)));
            FrameAddress rewritten = fork.CommitState().RevisionAddress;
            Need(repository.SharedSchemaRoot == schema, "Fork must reuse the already-published exact V2 catalog.");
            CheckRewrittenState(repository, fork, rewritten, witness, schema);
            CheckRefs(repository, schema, ("main", mainState), ("fork", rewritten));
            CheckCatalog(repository, schema, current: true);
            var same = repository.ReadPair(fork.Head, fork.Head);
            Widget sameFirst = Check(same.First, upgraded: true), sameSecond = Check(same.Second, upgraded: true);
            Need(ReferenceEquals(sameFirst, sameSecond), "Same-S exact no-rewrite cycle must retain Pair sharing eligibility.");
            P0GraphSnapshot old = repository.MetaBootstrapStore.ReadGraph(Address(witness.SeedState));
            P0GraphSnapshot current = repository.MetaBootstrapStore.ReadGraph(rewritten);
            Need(old.SchemaRoot != current.SchemaRoot && old.Objects[witness.ListId] == current.Objects[witness.ListId],
                "Cross-S witness must retain the same List ID/head and current contents.");
            var mixed = repository.ReadPair(selected, fork.Head);
            Widget oldRoot = Check(mixed.First, upgraded: true), newRoot = Check(mixed.Second, upgraded: true);
            Need(!ReferenceEquals(oldRoot, newRoot) && !ReferenceEquals(oldRoot.Numbers, newRoot.Numbers),
                "Different exact-S environments must not share mutable instances despite equal V2 DTO values.");
            Need(oldRoot.Value == newRoot.Value && oldRoot.Child!.Value == newRoot.Child!.Value && oldRoot.Numbers.SequenceEqual(newRoot.Numbers),
                "Cross-S Pair positive contents must agree with independent current reads.");
            CheckFrames(Resources(repository), witness.SeedFrames);
            P0CatalogView accepted = CheckCatalog(repository, schema, current: true);
            Need(accepted.SchemaNodeId(Schema(1)).Value == witness.WidgetDescriptorId,
                "The retained V1 descriptor must reuse its original metaObjectId.");
            uint descriptor = accepted.SchemaNodeId(Schema(2)).Value;
            nextWitness = witness with { MainState = Hex(mainState), RewriteState = Hex(rewritten), RewriteSchema = Hex(schema),
                CurrentWidgetDescriptorId = descriptor, RewriteObjects = Storage(fork),
                RewriteFrames = Frames(Resources(repository)).Where(frame => !witness.SeedFrames.Any(oldFrame => oldFrame.Address == frame.Address)).ToArray() };
        }
        WriteJson(witnessPath, nextWitness);
        return new { MainState = nextWitness.MainState, RewriteState = nextWitness.RewriteState, RewriteSchema = nextWitness.RewriteSchema,
            Objects = nextWitness.RewriteObjects, HistoricalForkUsesStateS = true, LatestCatalogRemainsIndependent = true,
            RewrittenLiveWidgetBases = 2, RetainedListHeadAndH = true, SameSPairShared = true, CrossSPairIndependent = true,
            ActualRefsVerified = true, CompleteFramesPreserved = true, RetainedDescriptorId = witness.WidgetDescriptorId,
            CurrentDescriptorId = nextWitness.CurrentWidgetDescriptorId };
    }

    private static object Continue(string database, string witnessPath) {
        Witness witness = ReadWitness(witnessPath);
        FrameAddress rewritten = Address(witness.RewriteState!), schema = Address(witness.RewriteSchema!);
        StorageEvidence[] priorRows = witness.RewriteObjects ?? throw new InvalidDataException("Missing confirmed rewrite storage.");
        Witness nextWitness;
        using (Repository repository = Repository.OpenMetaBootstrapExisting(database, Models())) {
            CheckRefs(repository, schema, ("main", Address(witness.MainState!)), ("fork", rewritten));
            using BranchCheckout fork = repository.Checkout("fork");
            Widget root = Check(fork.State, upgraded: true);
            Need(Widget.UpgradeCalls == 0 && fork.BindingEnvironment!.SelectedSchemaView.Snapshot!.Address == schema,
                "Fresh V2 State uses its own S1 without another historical Upgrade.");
            Need(Storage(fork).SequenceEqual(priorRows), "Cold complete storage must equal previously observed actual heads/H.");
            CheckRewrite(fork, witness, expected: false);
            StorageEvidence prior = priorRows.Single(row => row.Id == witness.ListId);
            root.Numbers[7] = 201;
            FrameAddress continued = fork.CommitState().RevisionAddress;
            P0GraphSnapshot graph = repository.MetaBootstrapStore.ReadGraph(continued);
            Need(graph.Parent == rewritten && graph.SourceGraph == rewritten && RequiredSchema(graph) == schema && graph.OwnedBatch is not null,
                "Cold Delta must select the exact current content Parent and Schema.");
            P0Batch batch = repository.MetaBootstrapStore.ReadBatch(graph.OwnedBatch!.Value);
            Need(batch.SourceGraph == rewritten && batch.SchemaRoot == schema && batch.Records.Count == 1, "Only the List should write a new object record.");
            ObjectVersionRecord delta = batch.Records[0];
            Need(delta.Kind == ObjectVersionKind.Delta && delta.ObjectId == witness.ListId && delta.PriorAddress == Address(prior.Head),
                "The actual List Delta must reference the selected object's exact head.");
            StorageEvidence[] updated = Storage(fork);
            Need(updated.Select(row => row.Id).SequenceEqual(priorRows.Select(row => row.Id)), "Delta continuation changed complete IDs.");
            StorageEvidence list = updated.Single(row => row.Id == witness.ListId);
            Need(Address(list.Head) == graph.OwnedBatch && Address(list.Head) != continued && delta.EncodedPayloadBytes is { } bytes &&
                list.H == checked(prior.H + bytes), "Installed head/H must use the actual batch and decoded payload cost.");
            foreach (StorageEvidence row in priorRows.Where(row => row.Id != witness.ListId)) {
                Need(updated.Single(candidate => candidate.Id == row.Id) == row, "Delta continuation changed an unchanged Widget head/H.");
            }
            CheckStorage(repository, graph, updated);
            CheckRefs(repository, schema, ("main", Address(witness.MainState!)), ("fork", continued));
            CheckFrames(Resources(repository), witness.SeedFrames.Concat(witness.RewriteFrames!));
            Check(repository.ReadState(repository.ResolveTag("initial")), upgraded: true);
            Check(repository.ReadState(repository.GetHead("fork")), upgraded: true, atSeven: 201);
            HashSet<string> previous = witness.SeedFrames.Concat(witness.RewriteFrames!).Select(frame => frame.Address).ToHashSet();
            nextWitness = witness with { ContinuationState = Hex(continued), ContinuationObjects = updated,
                ContinuationFrames = Frames(Resources(repository)).Where(frame => !previous.Contains(frame.Address)).ToArray() };
        }
        WriteJson(witnessPath, nextWitness);
        return new { ContinuationState = nextWitness.ContinuationState, Objects = nextWitness.ContinuationObjects,
            SingleListDelta = true, ExactPrior = true, IdsPreserved = true, ActualWireH = true,
            RetainedWidgetHeads = true, ActualRefsVerified = true, CompleteFramesPreserved = true };
    }

    private static object Verify(string database, string witnessPath) {
        Witness witness = ReadWitness(witnessPath);
        Dictionary<string, string> before = FileImages(database);
        using (Repository repository = Repository.OpenMetaBootstrapReadOnlyExisting(database, Models())) {
            FrameAddress schema = Address(witness.RewriteSchema!), next = Address(witness.ContinuationState!);
            CheckRefs(repository, schema, ("main", Address(witness.MainState!)), ("fork", next));
            P0CatalogView original = CheckCatalog(repository, Address(witness.SeedSchema), current: false);
            P0CatalogView accepted = CheckCatalog(repository, schema, current: true);
            Need(original.SchemaNodeId(Schema(1)).Value == witness.WidgetDescriptorId &&
                accepted.SchemaNodeId(Schema(1)).Value == witness.WidgetDescriptorId &&
                accepted.SchemaNodeId(Schema(2)).Value == witness.CurrentWidgetDescriptorId,
                "Fresh catalogs must preserve the exact old/new descriptor identities.");
            CheckpointAddress initial = repository.ResolveTag("initial");
            Need(initial.RevisionAddress == Address(witness.SeedState), "The immutable initial tag moved.");
            Check(repository.ReadState(initial), upgraded: true);
            Check(repository.ReadState(repository.GetHead("main")), upgraded: true);
            Check(repository.ReadState(repository.GetHead("fork")), upgraded: true, atSeven: 201);
            var pair = repository.ReadPair(initial, repository.GetHead("fork"));
            Widget old = Check(pair.First, upgraded: true), current = Check(pair.Second, upgraded: true, atSeven: 201);
            Need(!ReferenceEquals(old, current) && !ReferenceEquals(old.Numbers, current.Numbers), "Final cross-S reads must remain independent.");
            P0GraphSnapshot seed = repository.MetaBootstrapStore.ReadGraph(Address(witness.SeedState));
            P0GraphSnapshot rewritten = repository.MetaBootstrapStore.ReadGraph(Address(witness.RewriteState!));
            P0GraphSnapshot continued = repository.MetaBootstrapStore.ReadGraph(next);
            Need(RequiredSchema(seed) == Address(witness.SeedSchema) && RequiredSchema(rewritten) == schema && RequiredSchema(continued) == schema,
                "Old and new graph pins must remain exact after all owners close.");
            CheckStorage(repository, seed, witness.SeedObjects);
            CheckStorage(repository, rewritten, witness.RewriteObjects!);
            CheckStorage(repository, continued, witness.ContinuationObjects!);
            CheckFrames(Resources(repository), witness.SeedFrames.Concat(witness.RewriteFrames!).Concat(witness.ContinuationFrames!));
            Need(repository.MetaBootstrapStore.AppendedFrames == 0, "Final readonly history reads must not append.");
        }
        Need(SameFiles(before, FileImages(database)), "Final readonly verification changed repository files.");
        return new { ReadOnlyFilesUnchanged = true, OldTagPreserved = true, MainAndForkValues = true, AccurateSchemaPins = true,
            ActualRefsVerified = true, CompleteStorageVerified = true, CompleteFramesPreserved = true, CrossSPairIndependent = true };
    }

    private static void CheckRewrite(BranchCheckout checkout, Witness witness, bool expected) {
        Need(checkout.Baseline!.Objects.Count == 3, "Loaded baseline must keep complete membership.");
        foreach (uint id in new[] { witness.RootId, witness.ChildId }) {
            var row = checkout.Baseline.Objects[new(id)];
            Need(row.RequiresRewrite == expected && row.Current.Schema!.Equals(Schema(2)), "Widget current Schema or rewrite qualification disagrees.");
            if (expected) { Need(row.SourceLayout.Schema!.Equals(Schema(1)), "Historical source must remain the exact stored V1 layout."); }
        }
        Need(!checkout.Baseline.Objects[new(witness.ListId)].RequiresRewrite, "The unchanged builtin List must not acquire a Widget rewrite.");
    }
    private static void CheckRewrittenState(Repository repository, BranchCheckout checkout, FrameAddress address,
        Witness witness, FrameAddress schema) {
        P0GraphSnapshot graph = repository.MetaBootstrapStore.ReadGraph(address);
        Need(graph.Parent == Address(witness.SeedState) && graph.SourceGraph == Address(witness.SeedState) && RequiredSchema(graph) == schema,
            "First State must derive from the exact V1 State and pin the accepted new Schema.");
        P0Batch batch = repository.MetaBootstrapStore.ReadBatch(graph.OwnedBatch!.Value);
        Need(batch.SourceGraph == Address(witness.SeedState) && batch.SchemaRoot == schema && batch.Records.Count == 2 &&
            batch.Records.Select(record => record.ObjectId).Order().SequenceEqual(new[] { witness.RootId, witness.ChildId }.Order()) &&
            batch.Records.All(record => record.Kind == ObjectVersionKind.Base && record.PriorAddress is null),
            "Every rewritten-live Widget must write Base; the untouched List must remain retained.");
        P0CatalogView catalog = CheckCatalog(repository, schema, current: true);
        uint descriptor = catalog.SchemaNodeId(Schema(2)).Value;
        foreach (uint id in new[] { witness.RootId, witness.ChildId }) {
            CheckWidgetBase(repository.MetaBootstrapStore, address, id, Schema(2), descriptor);
            P0ObjectChain chain = repository.MetaBootstrapStore.ReadObjectChain(address, id);
            Need(chain.Entries.Count == 1 && chain.Head == graph.OwnedBatch, "Rewritten Widget Base must truncate its reconstruction chain.");
        }
        StorageEvidence list = witness.SeedObjects.Single(row => row.Id == witness.ListId);
        Need(Storage(checkout).Single(row => row.Id == witness.ListId) == list, "Retained List head/H changed during Widget Upgrade.");
        Need(repository.MetaBootstrapStore.ReadObjectChain(address, witness.ListId).Entries[0].SchemaRoot == Address(witness.SeedSchema),
            "Retained List keeps its actual old Base origin, independently of the selected new S.");
        CheckRewrite(checkout, witness, expected: false);
        CheckStorage(repository, graph, Storage(checkout));
        Check(checkout.State, upgraded: true);
    }
#endif

    private static P0CatalogView CheckCatalog(Repository repository, FrameAddress schema, bool current) {
        P0CatalogView catalog = P0CatalogView.Load(repository.MetaBootstrapStore, schema);
        Need(catalog.Schemas[Key(1)].Equals(Schema(1)), "The stored exact V1 definition changed.");
        Need(catalog.Schemas.ContainsKey(Key(2)) == current && catalog.Schemas.ContainsKey(new(Extra.Type, Extra.Version)) == current,
            "V2 and unknown Extra must exist only in the selected new catalog.");
        if (current) {
            Need(catalog.Schemas[Key(2)].Equals(Schema(2)) && catalog.Schemas[new(Extra.Type, Extra.Version)].Equals(Extra),
                "The new complete definitions disagree.");
            Need(catalog.SchemaNodeId(Schema(1)) != catalog.SchemaNodeId(Schema(2)), "Distinct Widget versions must have distinct descriptor IDs.");
        }
        Need(catalog.DescriptorIds.Count == (current ? 3 : 1), "Unexpected root-declared descriptor count.");
        return catalog;
    }
    private static void CheckWidgetBase(P0GraphStore store, FrameAddress graph, uint id, DurableSchema expected, uint descriptor) {
        P0ChainEntry origin = store.ReadObjectChain(graph, id).Entries[0];
        P0CatalogView catalog = P0CatalogView.Load(store, origin.SchemaRoot!.Value);
        BareValueReader reader = new(origin.Record.Body);
        Need(reader.ReadByte() == P0TypedBodyCodec.FormatVersion && reader.ReadByte() == 1 && reader.ReadVarUInt32() == descriptor,
            "Widget Base must use the actual typed ABI and exact dynamic metaObjectId.");
        Need(catalog.DescriptorIds.Contains(new(descriptor)) && P0TypedBodyCodec.Decode(origin.Record.Body, catalog).Layout.Schema!.Equals(expected),
            "The Base descriptor must be declared by its accurate source catalog and reconstruct its full Schema.");
    }
    private static void CheckStorage(Repository repository, P0GraphSnapshot graph, StorageEvidence[] storage) {
        Need(graph.Objects.Keys.Order().SequenceEqual(storage.Select(row => row.Id).Order()), "Actual complete directory membership disagrees.");
        foreach (StorageEvidence row in storage) {
            P0ObjectChain chain = repository.MetaBootstrapStore.ReadObjectChain(graph.Address, row.Id);
            Need(chain.Head == Address(row.Head) && graph.Objects[row.Id] == chain.Head && chain.ReconstructionPayloadBytes == row.H,
                "Actual object head or wire reconstruction payload H disagrees.");
        }
    }
    private static void CheckRefs(Repository repository, FrameAddress schema, params (string Branch, FrameAddress Address)[] branches) {
        GraphResources resources = Resources(repository);
        RefBytesSnapshot actualBranches = resources.Versions.ReadRef(1).Unwrap(), actualShared = resources.Versions.ReadRef(2).Unwrap();
        Need(actualBranches.Payload.StartsWith("P0R1"u8) && actualShared.Payload.StartsWith("P0S1"u8), "Actual ref payload roles disagree.");
        IReadOnlyDictionary<string, FrameAddress> branchRoots = RootMapCodec.Decode(actualBranches.Payload[4..]).Unwrap();
        IReadOnlyDictionary<string, FrameAddress> shared = RootMapCodec.Decode(actualShared.Payload[4..]).Unwrap();
        Need(branchRoots.Count == branches.Length && shared.Count == 1 && shared["schema"] == schema && repository.SharedSchemaRoot == schema,
            "Actual ref2 must select the exact expected Schema authority.");
        foreach (var branch in branches) {
            Need(branchRoots["branch/" + branch.Branch] == branch.Address && repository.GetHead(branch.Branch).RevisionAddress == branch.Address,
                "Actual ref1 and the Repository branch head disagree.");
        }
    }
    private static StateModelRegistry Models() {
        StateModelRegistry registry = new();
        registry.Register(global::Atelia.DurableGraph.Generated.Family_7061636B6167652E73706C69742E776964676574.Definition);
        return registry;
    }
    private static DurableSchema Schema(int version) => new(WidgetName, version,
        new DurableFieldInfo(1, version == 1 ? TypeTag.Int32 : TypeTag.Int64),
        DurableFieldInfo.Reference(2, TypeExpr.List(TypeExpr.Builtin(TypeTag.Int32))),
        DurableFieldInfo.Reference(3, TypeExpr.Named(WidgetName)));
    private static SchemaKey Key(int version) => new(TypeExpr.Named(WidgetName), version);
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
    private static StorageEvidence[] Storage(BranchCheckout checkout) => checkout.Baseline!.Objects.OrderBy(pair => pair.Key)
        .Select(pair => new StorageEvidence(pair.Key.Value, Hex(pair.Value.Storage!.Value.Head), pair.Value.Storage.Value.ReconstructionPayloadBytes)).ToArray();
    private static GraphResources Resources(Repository repository) =>
        (GraphResources)typeof(Repository).GetField("_resources", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(repository)!;
    private static FrameImage[] Frames(GraphResources resources) {
        List<FrameInfo> info = [];
        resources.Data.Inventory(info.Add).Unwrap();
        return info.Select(frame => {
            using FrameRead read = resources.Data.ReadFrame(frame.Address).Unwrap();
            return new FrameImage(Hex(frame.Address), read.Tag, read.TailMetaLength, Hash(read.PayloadAndMeta));
        }).ToArray();
    }
    private static void CheckFrames(GraphResources resources, IEnumerable<FrameImage> images) {
        foreach (FrameImage image in images) {
            using FrameRead read = resources.Data.ReadFrame(Address(image.Address)).Unwrap();
            Need(read.Tag == image.Tag && read.TailMetaLength == image.TailMetaLength && Hash(read.PayloadAndMeta) == image.PayloadAndMetaSha256,
                "A previously completed frame changed or disappeared.");
        }
    }
    private static FrameAddress RequiredSchema(P0GraphSnapshot graph) => graph.SchemaRoot ?? throw new InvalidDataException("Missing exact Schema root.");
    private static Dictionary<string, string> FileImages(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(root, path), path => Hash(File.ReadAllBytes(path)));
    private static bool SameFiles(Dictionary<string, string> before, Dictionary<string, string> after) =>
        before.Count == after.Count && before.All(pair => after.TryGetValue(pair.Key, out string? value) && value == pair.Value);
    private static object AssemblyEvidence(Assembly assembly) => new { Name = assembly.GetName().Name, Path = assembly.Location,
        Sha256 = Hash(File.ReadAllBytes(assembly.Location)) };
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
