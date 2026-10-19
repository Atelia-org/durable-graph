extern alias persistence;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using PrivateContainerPackage;
using persistence::Atelia.DurableGraph;
using persistence::Atelia.DurableGraph.Persistence;

// This ordinary package consumer uses public product APIs. The witness is only
// diagnostic: after every product owner closes, the observer re-reads actual
// formal refs, complete catalogs and exact object-version chains independently.
internal static class Program {
    private const string Protocol = "DG.SchemaContainerConsumer.v4";
#if SHAPE_A
    private const string ShapeEnvironment = "A";
#elif SHAPE_B
    private const string ShapeEnvironment = "B";
#else
#error ShapeEnvironment must be A or B.
#endif
    private static bool Wide => ShapeEnvironment == "B";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private sealed record StorageEvidence(uint Id, string Head, long H);
    private sealed record FrameImage(string Address, uint Tag, string PayloadSha256, string TailMetaSha256);
    private sealed record Witness(string SeedState, string MainSchema, string SharedSchema, string KeeperState,
        uint ARootId, uint AChildId, uint AListId, StorageEvidence[] ASeedObjects,
        string AState, StorageEvidence[] AObjects, FrameImage[] CompleteFrames,
        string? BSeedState = null, string? BState = null, string? BSchema = null,
        uint? BRootId = null, uint? BChildId = null, uint? BListId = null,
        StorageEvidence[]? BSeedObjects = null, StorageEvidence[]? BObjects = null);

    private static int Main(string[] args) {
        if (args.Length != 3) { throw new ArgumentException("Expected phase, database and witness path."); }
        string phase = args[0], database = Path.GetFullPath(args[1]), witnessPath = Path.GetFullPath(args[2]);
        object observations = phase switch {
            "seed" when !Wide => Seed(database, witnessPath),
            "create" when Wide => Create(database, witnessPath),
            "continue-a" when !Wide => Continue(database, witnessPath),
            "continue-b" when Wide => Continue(database, witnessPath),
            "missing-upgrade" when Wide => MissingUpgrade(database, witnessPath),
            "verify-a" when !Wide => Verify(database, witnessPath),
            "verify-b" when Wide => Verify(database, witnessPath),
            _ => throw new ArgumentException("This phase does not belong to the compiled shape environment."),
        };
        // Read only after all Repository/Checkout resources for the phase close.
        WireInspector.Snapshot actual = WireInspector.Read(database);
        Need(actual.TypedBodyAbi == 5, "Actual Base records must use the current typed ABI.");
        WriteJson(Path.Combine(Path.GetDirectoryName(witnessPath)!, phase + ".evidence.json"), new {
            Protocol, Lane = phase, ShapeEnvironment, Passed = true, ProcessId = Environment.ProcessId,
            actual.TypedBodyAbi,
            RuntimeAssembly = AssemblyEvidence(typeof(DurableSchema).Assembly),
            PersistenceAssembly = AssemblyEvidence(typeof(Repository).Assembly),
            StorageAssembly = AssemblyEvidence(typeof(ObjectVersionRecord).Assembly),
            HostAssembly = AssemblyEvidence(typeof(Root).Assembly),
            LoadedAssemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => assembly.GetName().Name?.StartsWith("Atelia.", StringComparison.Ordinal) == true)
                .OrderBy(assembly => assembly.GetName().Name, StringComparer.Ordinal).Select(AssemblyEvidence).ToArray(),
            Observations = observations,
        });
        Console.WriteLine("SchemaContainerConsumer:" + phase + ":Passed");
        return 0;
    }

    private static object Seed(string database, string witnessPath) {
        FrameAddress state, keeperState;
        CommitId stateCommit, keeperCommit;
        uint rootId;
        using (Repository repository = Repository.CreateNew(database, Models())) {
            using (WorkingCopy keeper = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("keeper", new Keeper { Value = 1234567890123L }).Id)) {
                keeperCommit = keeper.BaseCommitId;
            }
            using (WorkingCopy main = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", Ring()).Id)) {
                Check(main.State, changed: false);
                repository.CreateTag("old", main.BaseCommitId);
                stateCommit = main.BaseCommitId;
                rootId = main.StateId!.Value.Value;
            }
        }
        WireInspector.Snapshot actual = WireInspector.Read(database);
        state = actual.GraphAddress(stateCommit);
        keeperState = actual.GraphAddress(keeperCommit);
        WireInspector.Graph graph = actual.Graph(state);
        FrameAddress mainSchema = RequiredSchema(graph), shared = actual.SharedSchemaRoot!.Value;
        CheckCatalog(actual, mainSchema, wide: false, keeper: true);
        CheckCatalog(actual, shared, wide: false, keeper: true);
        Need(shared == mainSchema, "The frozen main baseline must already contain the unreachable Keeper declaration.");
        Need(actual.BranchHeads.Count == 2 && actual.BranchHeads["main"] == state &&
            actual.BranchHeads["keeper"] == keeperState && graph.RootId == rootId, "Actual branch refs disagree with seed delivery.");
        uint list = ListId(actual, graph, wide: false), child = graph.Heads.Keys.Single(id => id != rootId && id != list);
        CheckAllBase(actual, graph, mainSchema);
        StorageEvidence[] rows = Storage(actual, graph);
        CheckStorage(actual, graph, rows);
        Witness witness = new(Hex(state), Hex(mainSchema), Hex(shared), Hex(keeperState), rootId, child, list,
            rows, Hex(state), rows, Frames(actual));
        WriteJson(witnessPath, witness);
        return new { RingAndSharedList = true, FullSharedCatalog = true, ActualRefs = true, CompleteFrames = true,
            witness.SeedState, witness.MainSchema, witness.SharedSchema, witness.KeeperState,
            RootId = rootId, ChildId = child, ListId = list, Objects = rows, Refs = RefFacts(actual),
            Catalog = CatalogFacts(actual, shared), Frames = witness.CompleteFrames };
    }

    private static object Create(string database, string witnessPath) {
        Witness witness = ReadWitness(witnessPath);
        WireInspector.Snapshot before = WireInspector.Read(database);
        CheckFrames(before, witness.CompleteFrames);
        Need(before.BranchHeads["main"] == Address(witness.AState) &&
            before.SharedSchemaRoot == Address(witness.SharedSchema), "B must fork the actual complete frozen shared view.");
        FrameAddress state;
        CommitId stateCommit;
        uint rootId;
        using (Repository repository = Repository.OpenExisting(database, Models())) {
            repository.ForkBranch(repository.ResolveTag("old"), "upgraded");
            using WorkingCopy branch = repository.OpenWorkingCopy(BranchNamed(repository, "upgraded"));
            Check(branch.State, changed: false);
            Need(branch.StateId!.Value.Value == witness.ARootId, "Upgrade must preserve the original root identity.");
            stateCommit = branch.CommitState().Id;
            rootId = branch.StateId!.Value.Value;
        }
        WireInspector.Snapshot actual = WireInspector.Read(database);
        state = actual.GraphAddress(stateCommit);
        WireInspector.Graph graph = actual.Graph(state);
        FrameAddress schema = RequiredSchema(graph);
        Need(schema != before.SharedSchemaRoot && actual.BranchHeads.Count == 3 && actual.BranchHeads["upgraded"] == state &&
            actual.BranchHeads["main"] == before.BranchHeads["main"] && actual.BranchHeads["keeper"] == before.BranchHeads["keeper"],
            "Upgrade must advance only its business branch and the common Catalog.");
        Need(graph.RootId == rootId && graph.Parent == Address(witness.SeedState) && graph.SourceGraph == Address(witness.SeedState) &&
            graph.Heads.Keys.Order().SequenceEqual(before.Graph(Address(witness.SeedState)).Heads.Keys.Order()),
            "The upgraded fork must retain baseline IDs and exact business source.");
        uint upgradedList = witness.AListId;
        WireInspector.Chain upgraded = actual.ObjectChain(state, upgradedList);
        Need(upgraded.Entries.Count == 1 && upgraded.Entries[0].Record.Kind == ObjectVersionKind.Base &&
            upgraded.Entries[0].SourceGraph == Address(witness.SeedState) && upgraded.Entries[0].SchemaRoot == schema,
            "The changed inline List must be rewritten as a genuine upgrade Base.");
        foreach (StorageEvidence row in witness.ASeedObjects.Where(row => row.Id != upgradedList)) {
            WireInspector.Chain retained = actual.ObjectChain(state, row.Id);
            Need(retained.Head == Address(row.Head) && retained.H == row.H, "Unchanged Root records must retain baseline heads/H.");
        }
        CheckCatalog(actual, schema, wide: true, keeper: true);
        WireInspector.Catalog oldCatalog = before.Catalogs[Address(witness.SharedSchema)], target = actual.Catalogs[schema];
        uint oldPoint = oldCatalog.SchemaNodeId(PointSchema(wide: false)), newPoint = target.SchemaNodeId(PointSchema(wide: true));
        uint oldList = oldCatalog.DescriptorId(ListLayout(wide: false)), newList = target.DescriptorId(ListLayout(wide: true));
        uint keeper = oldCatalog.DescriptorId(ObjectLayout.ForDurable(KeeperSchema()));
        Need(newPoint != oldPoint && newList != oldList && target.DeclaredIds.Contains(oldPoint) &&
            target.DeclaredIds.Contains(oldList) && target.LiveIds.Contains(oldPoint) && target.LiveIds.Contains(oldList),
            "Normal version growth must retain old and new Point/List declarations.");
        Need(target.DescriptorId(ObjectLayout.ForDurable(RootSchema())) == oldCatalog.DescriptorId(ObjectLayout.ForDurable(RootSchema())) &&
            target.DescriptorId(ObjectLayout.ForDurable(KeeperSchema())) == keeper,
            "Untouched Root and unreachable Keeper descriptors must be retained in the full common Catalog.");
        WireInspector.Graph meta = actual.Graph(schema);
        Need(meta.Kind == WireInspector.GraphKind.Meta && meta.Parent == before.SharedSchemaRoot &&
            meta.SourceGraph == before.SharedSchemaRoot, "Shared meta must derive from the actual complete frozen shared root.");
        Need(actual.SharedSchemaRoot == schema && !actual.Refs[2].Revision.Equals(before.Refs[2].Revision),
            "Ordinary Shared upgrade must publish the grown common Catalog.");
        CheckFrames(actual, witness.CompleteFrames);
        uint list = ListId(actual, graph, wide: true), child = graph.Heads.Keys.Single(id => id != rootId && id != list);
        StorageEvidence[] rows = Storage(actual, graph);
        CheckStorage(actual, graph, rows);
        Witness next = witness with { BSeedState = Hex(state), BState = Hex(state), BSchema = Hex(schema), SharedSchema = Hex(schema),
            BRootId = rootId, BChildId = child, BListId = list, BSeedObjects = rows, BObjects = rows, CompleteFrames = Frames(actual) };
        WriteJson(witnessPath, next);
        return new { BaselineIdentitiesPreserved = true, UpgradeListBase = true, ExactBusinessSource = true,
            FullCommonCatalog = true, KeeperPreserved = true, SharedCatalogAdvanced = true,
            State = Hex(state), Schema = Hex(schema), RootId = rootId, ChildId = child, ListId = list,
            OldPointId = oldPoint, NewPointId = newPoint, OldListId = oldList, NewListId = newList, KeeperId = keeper,
            Objects = rows, Refs = RefFacts(actual), Catalog = CatalogFacts(actual, schema), Graph = GraphFacts(actual, graph) };
    }

    private static object Continue(string database, string witnessPath) {
        Witness witness = ReadWitness(witnessPath);
        WireInspector.Snapshot before = WireInspector.Read(database);
        string branchName = Wide ? "upgraded" : "main";
        FrameAddress source = Address(Wide ? witness.BState! : witness.AState);
        uint list = Wide ? witness.BListId!.Value : witness.AListId;
        StorageEvidence[] priorRows = Wide ? witness.BObjects! : witness.AObjects;
        CheckStorage(before, before.Graph(source), priorRows);
        CheckFrames(before, witness.CompleteFrames);
        Need(before.BranchHeads[branchName] == source, "Cold continuation must use the actual prior branch State.");
        FrameAddress nextState;
        CommitId nextCommit;
        using (Repository repository = Repository.OpenExisting(database, Models())) {
            using WorkingCopy branch = repository.OpenWorkingCopy(BranchNamed(repository, branchName));
            Root root = Check(branch.State, changed: false);
            Need(branch.StateSourceCommitId == before.CommitAt(source) && branch.StateId!.Value.Value == (Wide ? witness.BRootId!.Value : witness.ARootId),
                "The cold editable baseline must retain its exact source and committed root ID.");
            root.Numbers[7] = PointAt(7, changed: true);
            // The fixture asks for the actual Delta candidate without optional Base spending.
            nextCommit = branch.CommitState(parameters: new ReadAmplificationBaseBudgetParameters(int.MaxValue, 1)).Id;
            Check(branch.State, changed: true);
            if (!Wide) { Check(repository.LoadState(repository.ResolveTag("old")), changed: false); }
        }
        WireInspector.Snapshot actual = WireInspector.Read(database);
        nextState = actual.GraphAddress(nextCommit);
        WireInspector.Graph graph = actual.Graph(nextState);
        FrameAddress schema = RequiredSchema(graph), expectedSchema = Address(witness.SharedSchema);
        Need(schema == expectedSchema && graph.Parent == source && graph.SourceGraph == source &&
            actual.BranchHeads[branchName] == nextState && graph.RootId == before.Graph(source).RootId,
            "The continued State must preserve exact business source/root and the selected Schema pin.");
        CheckCatalog(actual, schema, wide: true, keeper: true);
        CheckSingleDelta(before, actual, source, nextState, list, priorRows);
        CheckRef(before, actual, 2);
        Need(actual.SharedSchemaRoot == before.SharedSchemaRoot && actual.BranchHeads["keeper"] == before.BranchHeads["keeper"],
            "A cold compatible save must retain shared authority and the unrelated Keeper branch.");
        string other = Wide ? "main" : "upgraded";
        Need(actual.BranchHeads[other] == before.BranchHeads[other], "The other environment's branch was changed.");
        CheckFrames(actual, witness.CompleteFrames);
        StorageEvidence[] rows = Storage(actual, graph);
        Witness next = Wide ? witness with { BState = Hex(nextState), BObjects = rows, CompleteFrames = Frames(actual) }
            : witness with { AState = Hex(nextState), AObjects = rows, CompleteFrames = Frames(actual) };
        WriteJson(witnessPath, next);
        return new { SingleListDelta = true, ExactPrior = true, ActualWireH = true, IdsPreserved = true,
            RetainedRootHeads = true, OtherBranchUnchanged = true, SharedRefUnchanged = true,
            State = Hex(nextState), SourceState = Hex(source), Schema = Hex(schema), ListId = list, Objects = rows,
            Refs = RefFacts(actual), Graph = GraphFacts(actual, graph), CompleteFramesPreserved = true };
    }

    private static object MissingUpgrade(string database, string witnessPath) {
        Witness witness = ReadWitness(witnessPath);
        WireInspector.Snapshot before = WireInspector.Read(database);
        using (Repository repository = Repository.OpenExisting(database, Models(includeListRules: false))) {
            // Repeat the rejected checkout to verify it never occupies the branch.
            for (int attempt = 0; attempt < 2; attempt++) {
                bool rejected = false;
                try {
                    using WorkingCopy branch = repository.OpenWorkingCopy(BranchNamed(repository, "main"));
                } catch (InvalidDataException error) when (error.Message ==
                    "Changing a list element layout requires an explicitly selected list element upgrade rule set.") {
                    rejected = true;
                }
                Need(rejected && !repository.IsFaulted, "Missing List rules must reject checkout without faulting its owner.");
                Need(repository.ReadCurrentCommit(BranchNamed(repository, "main")).Commit.Id == before.CommitAt(Address(witness.AState)), "Rejected checkout moved main.");
            }
        }
        WireInspector.Snapshot actual = WireInspector.Read(database);
        CheckUnchanged(before, actual, bothRefs: true);
        return new { MissingListUpgradeRejected = true, NoBranchOccupancy = true, PublicationUnchanged = true,
            CompleteFramesPreserved = true };
    }

    private static object Verify(string database, string witnessPath) {
        Witness witness = ReadWitness(witnessPath);
        Dictionary<string, string> originalFiles = FileImages(database);
        WireInspector.Snapshot before = WireInspector.Read(database);
        CheckFrames(before, witness.CompleteFrames);
        string own = Wide ? "upgraded" : "main", foreign = Wide ? "main" : "upgraded";
        FrameAddress selected = Address(Wide ? witness.BState! : witness.AState);
        using (Repository reader = Repository.OpenReadOnlyExisting(database, Models())) {
            Need(reader.IsReadOnly && reader.ReadCurrentCommit(BranchNamed(reader, own)).Commit.Id == before.CommitAt(selected),
                "Readonly must select the actual branch root from the formal ref.");
            Check(reader.LoadState(reader.ReadCurrentCommit(BranchNamed(reader, own)).Commit.Id), changed: true);
            if (Wide) { Check(reader.LoadState(reader.ReadCurrentCommit(BranchNamed(reader, foreign)).Commit.Id), changed: true); }
            if (!Wide) {
                CommitId old = reader.ResolveTag("old");
                Need(old == before.CommitAt(Address(witness.SeedState)), "The old tag binding moved.");
                Check(reader.LoadState(old), changed: false);
            }
        }
        WireInspector.Snapshot actual = WireInspector.Read(database);
        CheckUnchanged(before, actual, bothRefs: true);
        Need(SameFiles(originalFiles, FileImages(database)), "Readonly changed the complete file set or bytes.");
        Need(actual.SharedSchemaRoot == Address(witness.SharedSchema) && actual.BranchHeads.Count == 3 &&
            actual.BranchHeads["main"] == Address(witness.AState) && actual.BranchHeads["upgraded"] == Address(witness.BState!) &&
            actual.BranchHeads["keeper"] == Address(witness.KeeperState), "Final actual root pair disagrees with the observed history.");
        CheckCatalog(actual, Address(witness.SharedSchema), wide: true, keeper: true);
        CheckCatalog(actual, Address(witness.BSchema!), wide: true, keeper: true);
        CheckStorage(actual, actual.Graph(Address(witness.SeedState)), witness.ASeedObjects);
        CheckStorage(actual, actual.Graph(Address(witness.AState)), witness.AObjects);
        CheckStorage(actual, actual.Graph(Address(witness.BSeedState!)), witness.BSeedObjects!);
        CheckStorage(actual, actual.Graph(Address(witness.BState!)), witness.BObjects!);
        CheckFrames(actual, witness.CompleteFrames);
        return new { ReadOnlyFilesUnchanged = true, OwnBranchRead = true, BaselineUpgradeRead = Wide,
            CompleteFramesPreserved = true, OldTagRead = !Wide, State = Hex(selected), Schema = Hex(RequiredSchema(actual.Graph(selected))),
            SharedSchema = witness.SharedSchema, UpgradedSchema = witness.BSchema, Refs = RefFacts(actual),
            AObjects = witness.AObjects, BObjects = witness.BObjects, Graph = GraphFacts(actual, actual.Graph(selected)) };
    }

    private static void CheckAllBase(WireInspector.Snapshot actual, WireInspector.Graph graph, FrameAddress schema) {
        Need(graph.Kind == WireInspector.GraphKind.State && graph.Heads.Count == 3 && graph.OwnedBatch is { } owned &&
            actual.Batches[owned].Records.Count == 3 && actual.Batches[owned].SourceGraph is null &&
            actual.Batches[owned].SchemaRoot == schema, "The new root must write all three live objects in an actual source-free batch.");
        foreach (uint id in graph.Heads.Keys) {
            WireInspector.Chain chain = actual.ObjectChain(graph.Address, id);
            WireInspector.Entry entry = chain.Entries.Single();
            Need(entry.Record.Kind == ObjectVersionKind.Base && entry.Record.PriorAddress is null &&
                entry.SourceGraph is null && entry.SchemaRoot == schema && chain.Head == graph.OwnedBatch,
                "Every new-root live object must be an actual Base with the selected Schema and no prior.");
        }
    }

    private static void CheckSingleDelta(WireInspector.Snapshot before, WireInspector.Snapshot actual,
        FrameAddress source, FrameAddress state, uint list, StorageEvidence[] priorRows) {
        WireInspector.Graph graph = actual.Graph(state);
        Need(graph.Heads.Keys.Order().SequenceEqual(before.Graph(source).Heads.Keys.Order()) && graph.OwnedBatch is not null,
            "Cold continuation must preserve complete object IDs and have an actual new owned batch.");
        WireInspector.Batch batch = actual.Batches[graph.OwnedBatch!.Value];
        Need(batch.Records.Count == 1 && batch.Records.ContainsKey(list) && batch.SourceGraph == source && batch.SchemaRoot == graph.SchemaRoot,
            "Only the changed List must be written, with the exact source and selected Schema context.");
        WireInspector.Chain prior = before.ObjectChain(source, list), chain = actual.ObjectChain(state, list);
        WireInspector.Entry delta = chain.Entries[^1];
        Need(delta.Record.Kind == ObjectVersionKind.Delta && delta.Record.PriorAddress == prior.Head &&
            delta.SourceGraph == source && delta.SchemaRoot == graph.SchemaRoot && chain.Head == batch.Address && chain.Head != graph.Address &&
            chain.Entries.Count == prior.Entries.Count + 1 && chain.H == checked(prior.H + delta.Record.ActualPayloadBytes),
            "Actual wire Delta must use the old List batch head and accumulate actual reconstruction payload H.");
        foreach (StorageEvidence row in priorRows.Where(row => row.Id != list)) {
            WireInspector.Chain retained = actual.ObjectChain(state, row.Id);
            Need(retained.Head == Address(row.Head) && retained.H == row.H, "Unchanged Root heads/H were not retained.");
        }
        CheckStorage(actual, graph, Storage(actual, graph));
    }

    private static uint ListId(WireInspector.Snapshot actual, WireInspector.Graph graph, bool wide) {

        return graph.Heads.Keys.Single(id => {
            WireInspector.Entry origin = actual.ObjectChain(graph.Address, id).Entries[0];
            WireInspector.BaseHeader header = WireInspector.Header(origin.Record.Body);
            return header.Route == 1 && origin.SchemaRoot is { } originSchema &&
                actual.Catalogs[originSchema].ResolveLayout(header.DescriptorId!.Value).Kind == ObjectStateKind.List;
        });
    }

    private static void CheckCatalog(WireInspector.Snapshot actual, FrameAddress schema, bool wide, bool keeper) {
        WireInspector.Catalog catalog = actual.Catalogs[schema];
        DurableSchema[] expected = wide ? [RootSchema(), PointSchema(false), PointSchema(true), KeeperSchema()] :
            keeper ? [RootSchema(), PointSchema(false), KeeperSchema()] : [RootSchema(), PointSchema(false)];
        Need(catalog.Schemas.Count == expected.Length && catalog.DeclaredIds.Count == expected.Length + (wide ? 2 : 1),
            "The actual full Schema and descriptor declaration set has the wrong membership.");
        foreach (DurableSchema definition in expected) {
            Need(catalog.Schemas.TryGetValue(new(definition.Type, definition.Version), out DurableSchema? stored) && stored.Equals(definition) &&
                catalog.DeclaredIds.Contains(catalog.SchemaNodeId(definition)), "Actual metadata differs from the full public Schema factory.");
        }
        Need(catalog.DescriptorIds.Count == (wide ? 4 : keeper ? 3 : 2) && catalog.DeclaredIds.Contains(catalog.DescriptorId(ListLayout(wide))),
            "The actual object-bearing layouts must include the complete exact Nullable inline List.");
    }

    private static void CheckStorage(WireInspector.Snapshot actual, WireInspector.Graph graph, StorageEvidence[] rows) {
        Need(graph.Heads.Keys.Order().SequenceEqual(rows.Select(row => row.Id).Order()), "Complete membership differs from the observed source.");
        foreach (StorageEvidence row in rows) {
            WireInspector.Chain chain = actual.ObjectChain(graph.Address, row.Id);
            long h = chain.Entries.Aggregate(0L, (sum, entry) => checked(sum + entry.Record.ActualPayloadBytes));
            Need(chain.Head == Address(row.Head) && graph.Heads[row.Id] == chain.Head && chain.H == row.H && h == row.H,
                "Actual full version chain head/H does not match the independent owned observation.");
        }
    }

    private static void CheckRef(WireInspector.Snapshot before, WireInspector.Snapshot after, uint id) {
        Need(before.Refs[id].Revision.Equals(after.Refs[id].Revision) && before.Refs[id].Payload.AsSpan().SequenceEqual(after.Refs[id].Payload),
            "A formal ref's actual revision or payload changed unexpectedly.");
    }
    private static void CheckUnchanged(WireInspector.Snapshot before, WireInspector.Snapshot after, bool bothRefs) {
        if (bothRefs) { CheckRef(before, after, 1); }
        CheckRef(before, after, 2);
        Need(before.Frames.Count == after.Frames.Count, "A rejected/readonly operation produced new complete frames.");
        CheckFrames(after, Frames(before));
    }
    private static void CheckFrames(WireInspector.Snapshot actual, IEnumerable<FrameImage> previous) {
        Dictionary<string, FrameImage> current = Frames(actual).ToDictionary(frame => frame.Address, StringComparer.Ordinal);
        foreach (FrameImage image in previous) {
            Need(current.TryGetValue(image.Address, out FrameImage? found) && image == found,
                "A complete frame address, tag, payload or tail metadata was changed or lost.");
        }
    }
    private static StateModelRegistry Models(bool includeListRules = true) {
        StateModelRegistry result = new();
        global::Atelia.DurableGraph.Generated.DurableDefinitions.Register(result);
#if SHAPE_B
        if (includeListRules) { result.UseListElementUpgrades(typeof(PointRules)); }
#endif
        return result;
    }
    private static DurableSchema RootSchema() => new("pc.root", 1,
        DurableFieldInfo.Reference(1, TypeExpr.List(TypeExpr.Nullable(TypeExpr.Named("pc.point")))),
        DurableFieldInfo.Reference(2, TypeExpr.Named("pc.root")));
    private static DurableSchema PointSchema(bool wide) => new("pc.point", wide ? 2 : 1, SchemaKind.InlineValue,
        new(1, wide ? TypeTag.Int64 : TypeTag.Int32), new(2, TypeTag.Double), new(3, TypeTag.Double), new(4, TypeTag.Double));
    private static DurableSchema KeeperSchema() => new("pc.keeper", 1, new DurableFieldInfo(1, TypeTag.Int64));
    private static ObjectLayout ListLayout(bool wide) => ObjectLayout.ForList(new ListLayout(
        DurableFieldInfo.Nullable(1, new DurableFieldInfo(1, TypeTag.InlineValue, inlineSchema: PointSchema(wide)))));
    private static Root Ring() {
        List<Point?> values = Enumerable.Range(0, 128).Select(i => i % 11 == 0 ? (Point?)null : PointAt(i, changed: false)).ToList();
        Root root = new() { Numbers = values };
        root.Child = new() { Numbers = values, Child = root };
        return root;
    }
    private static Point PointAt(int index, bool changed) {
        long value = (Wide ? 5_000_000_000L : 0) + index + (changed && index == 7 ? 777 : 0);
        return new Point {
#if SHAPE_A
            X = checked((int)value),
#else
            X = value,
#endif
            Padding0 = .25, Padding1 = .5, Padding2 = .75,
        };
    }
    private static Root Check(IDurableObject? value, bool changed) {
        Need(value is Root, "Unexpected domain root CLR type.");
        Root root = (Root)value!;
        Need(root.Child is { } child && !ReferenceEquals(root, child) && ReferenceEquals(root, child.Child) &&
            ReferenceEquals(root.Numbers, child.Numbers) && root.Numbers.Count == 128, "The two-root ring/shared List was not restored.");
        for (int i = 0; i < 128; i++) {
            Point? item = root.Numbers[i];
            if (i % 11 == 0) { Need(item is null, "An absent Nullable Point became present."); }
            else {
                Point expected = PointAt(i, changed);
                Need(item is { } present && present.X == expected.X && present.Padding0 == .25 && present.Padding1 == .5 && present.Padding2 == .75,
                    "A present Nullable Point's exact inline values changed.");
                if (Wide) { Need(item!.Value.X > int.MaxValue, "B must persist genuine wide values outside the A range."); }
            }
        }
        return root;
    }
    private static StorageEvidence[] Storage(WireInspector.Snapshot actual, WireInspector.Graph graph) => graph.Heads.Keys.Order()
        .Select(id => { WireInspector.Chain chain = actual.ObjectChain(graph.Address, id); return new StorageEvidence(id, Hex(chain.Head), chain.H); }).ToArray();
    private static FrameImage[] Frames(WireInspector.Snapshot actual) => actual.Frames.OrderBy(frame => Hex(frame.Address), StringComparer.Ordinal)
        .Select(frame => new FrameImage(Hex(frame.Address), frame.Tag, frame.PayloadSha256, frame.TailMetaSha256)).ToArray();
    private static object RefFacts(WireInspector.Snapshot actual) => actual.Refs.OrderBy(pair => pair.Key)
        .Select(pair => new { Id = pair.Key, Ticket = pair.Value.Revision.Ticket.Packed, PayloadSha256 = Hash(pair.Value.Payload),
            PayloadHex = Convert.ToHexString(pair.Value.Payload) }).ToArray();
    private static object CatalogFacts(WireInspector.Snapshot actual, FrameAddress address) {
        WireInspector.Catalog catalog = actual.Catalogs[address];
        return new { Address = Hex(address), catalog.DeclaredIds, catalog.LiveIds,
            RootId = catalog.SchemaNodeId(RootSchema()), KeeperId = catalog.SchemaNodeId(KeeperSchema()),
            PointIds = catalog.SchemaIds.Where(pair => pair.Key.Type.DefinitionId == "pc.point").Select(pair => new { pair.Key.Version, Id = pair.Value }).ToArray(),
            ListIds = catalog.DescriptorIds.Where(pair => pair.Key.List is not null).Select(pair => pair.Value).ToArray() };
    }
    private static object GraphFacts(WireInspector.Snapshot actual, WireInspector.Graph graph) => new {
        Address = Hex(graph.Address), Kind = graph.Kind.ToString(), Parent = HexOrNull(graph.Parent), SourceGraph = HexOrNull(graph.SourceGraph),
        graph.RootId, DirectoryRoot = Hex(graph.DirectoryRoot), SchemaRoot = HexOrNull(graph.SchemaRoot), OwnedBatch = HexOrNull(graph.OwnedBatch),
        Objects = graph.Heads.Keys.Order().Select(id => {
            WireInspector.Chain chain = actual.ObjectChain(graph.Address, id);
            return new { Id = id, Head = Hex(chain.Head), chain.H, Records = chain.Entries.Select(entry => new {
                Kind = entry.Record.Kind.ToString(), Prior = HexOrNull(entry.Record.PriorAddress), entry.Record.ActualPayloadBytes,
                Batch = Hex(entry.ContainingBatch), SourceGraph = HexOrNull(entry.SourceGraph), SchemaRoot = HexOrNull(entry.SchemaRoot),
                BaseDescriptor = entry.Record.Kind == ObjectVersionKind.Base ? WireInspector.Header(entry.Record.Body).DescriptorId : null,
            }).ToArray() };
        }).ToArray(),
    };
    private static FrameAddress RequiredSchema(WireInspector.Graph graph) => graph.SchemaRoot ?? throw new InvalidDataException("Missing exact Schema pin.");
    private static Dictionary<string, string> FileImages(string database) => Directory.GetFiles(database, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(database, path), path => Hash(File.ReadAllBytes(path)), StringComparer.Ordinal);
    private static bool SameFiles(Dictionary<string, string> before, Dictionary<string, string> after) =>
        before.Count == after.Count && before.All(pair => after.TryGetValue(pair.Key, out string? hash) && hash == pair.Value);
    private static Witness ReadWitness(string path) => JsonSerializer.Deserialize<Witness>(File.ReadAllText(path), Json)
        ?? throw new InvalidDataException("Missing diagnostic witness.");
    private static void WriteJson(string path, object value) {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, Json));
    }
    private static object AssemblyEvidence(Assembly assembly) => new { Name = assembly.GetName().Name, assembly.FullName,
        Path = assembly.Location, Sha256 = Hash(File.ReadAllBytes(assembly.Location)) };
    private static BranchId BranchNamed(Repository repository, string label) => repository.ListBranches().Single(branch => branch.DisplayLabel == label).Id;
    private static string Hex(FrameAddress address) {
        Span<byte> bytes = stackalloc byte[FrameAddress.EncodedSize];
        Need(address.TryWrite(bytes), "Invalid address.");
        return Convert.ToHexString(bytes);
    }
    private static string? HexOrNull(FrameAddress? address) => address is { } value ? Hex(value) : null;
    private static FrameAddress Address(string encoded) {
        Need(FrameAddress.TryRead(Convert.FromHexString(encoded), out FrameAddress value), "Invalid witness address.");
        return value;
    }
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static void Need(bool condition, string message) {
        if (!condition) { throw new InvalidDataException(message); }
    }
}
