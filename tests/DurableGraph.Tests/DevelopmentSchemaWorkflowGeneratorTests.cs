using System.Reflection;
using Atelia.DurableGraph.Build;
using Atelia.DurableGraph.Persistence;

using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    // Real SG and storage: Event publication may accept V2 while the loaded
    // State still requires a rewrite. Discard and Event must not consume it.
    [Fact]
    public void SharedUpgradeDiscardAndEventPreserveStateRewriteUntilStateCommit() {
        using AncestryHistoryDirectory history = new();
        SchemaHistoryTool publisher = new();
        GeneratorTestRun v1 = RunGenerator(DevelopmentWorkflowSource(1, wideMarker: false), [], null, "true");
        AssertSchemaOnlyCompiles(v1);
        publisher.Publish(history.WriteManifest(v1), history.History);
        GeneratorTestRun v2 = RunGenerator(DevelopmentWorkflowSource(2, wideMarker: false), history.ReadAdditionalTexts(), null, "true");
        AssertSchemaOnlyCompiles(v2);
        DevelopmentWorkflowHost old = DevelopmentWorkflowHost.Load(v1), current = DevelopmentWorkflowHost.Load(v2);
        using RawBaseDirectory directory = new();
        FrameAddress source, schemaV1, schemaV2, eventAddress, saved, continued;
        ObjectId rootId;
        Dictionary<ObjectId, (long Value, ObjectId List, ObjectId Child)> edges;
        Dictionary<ObjectId, ObjectStorageInfo> storage;
        EvidenceFrameImage[] originalFrames;
        using (Repository repository = Repository.CreateNew(directory.Path, old.Models())) {
            BranchId mainId = repository.CreateRootBranchFromState("main", old.Create()).Id;
            using WorkingCopy branch = repository.OpenWorkingCopy(mainId);
            branch.CommitState(old.Create());
            source = EvidenceAddress(repository, branch.CommitState(old.Child(branch.State!)).Id);
            rootId = branch.StateId!.Value;
            edges = DevelopmentEdges(branch, old);
            storage = DevelopmentStorage(branch);
            repository.CreateTag("v1", branch.BaseCommitId);
            schemaV1 = repository.SharedSchemaRoot!.Value;
            originalFrames = EvidenceReadFrames(EvidenceResources(repository).Data);
        }
        using (Repository repository = Repository.OpenExisting(directory.Path, current.Models())) {
            using WorkingCopy branch = repository.OpenWorkingCopy(EvidenceBranch(repository, "main"));
            DevelopmentAssertIdentity(branch, current, rootId, edges);
            Assert.Equal(2, branch.Baseline!.Objects.Values.Count(row => row.RequiresRewrite));
            Assert.Equal(schemaV1, branch.BindingEnvironment!.SelectedSchemaView.Snapshot!.Address);
            NormalizedGraphState baseline = branch.Baseline;
            IDurableObject state = branch.State!;
            EvidencePublicationImage before = EvidencePublication(repository);
            branch.PrepareAndDiscard(state);
            EvidenceAssertPublicationUnchanged(repository, before);
            Assert.Same(baseline, branch.Baseline);
            Assert.Same(state, branch.State);
            Assert.Equal(2, branch.Baseline.Objects.Values.Count(row => row.RequiresRewrite));

            eventAddress = EvidenceAddress(repository, branch.CommitEvent(state).Id);
            GraphRecordStore store = ReferenceGraphStore(repository);
            StoredGraphRevision domainEvent = PublishedGraph(EvidenceResources(repository), eventAddress);
            schemaV2 = repository.SharedSchemaRoot!.Value;
            Assert.NotEqual(schemaV1, schemaV2);
            Assert.Equal(schemaV2, domainEvent.SchemaRoot);

            Assert.Equal(source, domainEvent.SourceGraph);
            Assert.Equal(StoredGraphKind.Event, domainEvent.Kind);
            Assert.Same(baseline, branch.Baseline);
            Assert.Same(state, branch.State);
            Assert.Equal(source, branch.StateRevisionAddress);
            Assert.Equal(rootId, branch.StateId);
            Assert.Equal(schemaV1, branch.BindingEnvironment.SelectedSchemaView.Snapshot!.Address);
            Assert.Equal(2, branch.Baseline.Objects.Values.Count(row => row.RequiresRewrite));
            Assert.Equal(old.Schema(), branch.Catalog.Accepted!.Schemas[new(old.Schema().Type, 1)]);
            Assert.Equal(current.Schema(), branch.Catalog.Accepted.Schemas[new(current.Schema().Type, 2)]);
            Assert.Equal(2, DefaultRecords(store, PublishedGraph(EvidenceResources(repository), eventAddress)).Count);
            Assert.All(DefaultRecords(store, PublishedGraph(EvidenceResources(repository), eventAddress)), row => Assert.Equal(ObjectVersionKind.Base, row.Kind));
            before = EvidencePublication(repository);
            branch.PrepareAndDiscard(state);
            EvidenceAssertPublicationUnchanged(repository, before);
            Assert.Same(baseline, branch.Baseline);
            Assert.Equal(2, branch.Baseline.Objects.Values.Count(row => row.RequiresRewrite));
        }
        // Reopen at the Event: execution still normalizes the old State using its
        // exact V1 pin, while publication uses the already accepted V2 Catalog.
        using (Repository repository = Repository.OpenExisting(directory.Path, current.Models())) {
            using WorkingCopy branch = repository.OpenWorkingCopy(EvidenceBranch(repository, "main"));
            Assert.Equal(eventAddress, EvidenceAddress(repository, branch.BaseCommitId));
            Assert.Equal(source, branch.StateRevisionAddress);
            Assert.Equal(schemaV1, branch.BindingEnvironment!.SelectedSchemaView.Snapshot!.Address);
            Assert.Equal(2, branch.Baseline!.Objects.Values.Count(row => row.RequiresRewrite));
            EvidenceRefImage shared = EvidenceReadRef(repository, 2);
            saved = EvidenceAddress(repository, branch.CommitState().Id);
            DevelopmentAssertUpgrade(repository, branch, current, source, eventAddress,
                schemaV2, rootId, edges, storage);
            EvidenceAssertSameRef(shared, EvidenceReadRef(repository, 2));
            EvidenceAssertFramesPreserved(EvidenceResources(repository).Data, originalFrames);
        }
        using (Repository repository = Repository.OpenExisting(directory.Path, current.Models())) {
            using WorkingCopy branch = repository.OpenWorkingCopy(EvidenceBranch(repository, "main"));
            DevelopmentAssertIdentity(branch, current, rootId, edges);
            Assert.All(branch.Baseline!.Objects.Values, row => Assert.False(row.RequiresRewrite));
            ObjectId list = edges[rootId].List;
            ObjectStorageInfo prior = branch.Baseline.Objects[list].Storage!.Value;
            current.ChangeList(branch.State!, 9000);
            continued = EvidenceAddress(repository, branch.CommitState(parameters: new(int.MaxValue, 1)).Id);
            DevelopmentAssertDelta(ReferenceGraphStore(repository), branch, saved, continued, schemaV2, list, prior);
        }
        Dictionary<string, byte[]> files = EvidenceFiles(directory.Path);
        using (Repository repository = Repository.OpenReadOnlyExisting(directory.Path, old.Models())) {
            old.Check(repository.LoadState(repository.ResolveTag("v1")), 2, 1, 7);
        }
        using (Repository repository = Repository.OpenReadOnlyExisting(directory.Path, current.Models())) {
            current.Check(repository.LoadState(repository.ReadCurrentCommit(EvidenceBranch(repository, "main")).Commit.Id), 102, 101, 9000);
            current.Check(repository.LoadEvent(Assert.Single(repository.ReadFrames(EvidenceBranch(repository, "main")), row => EvidenceAddress(repository, row.Id) == eventAddress).Id), 102, 101, 7);
            Assert.Equal(continued, EvidenceAddress(repository, repository.ReadCurrentCommit(EvidenceBranch(repository, "main")).Commit.Id));
            Assert.Throws<InvalidOperationException>(() => repository.OpenWorkingCopy(EvidenceBranch(repository, "main")));
            EvidenceAssertFramesPreserved(EvidenceResources(repository).Data, originalFrames);
        }
        EvidenceAssertFilesUnchanged(files, EvidenceFiles(directory.Path));
    }

    private static void DevelopmentAssertUpgrade(Repository repository, WorkingCopy branch, DevelopmentWorkflowHost host,
        FrameAddress source, FrameAddress parent, FrameAddress schema, ObjectId rootId,
        Dictionary<ObjectId, (long Value, ObjectId List, ObjectId Child)> edges, Dictionary<ObjectId, ObjectStorageInfo> storage) {
        GraphRecordStore store = repository.GraphStore;
        Assert.Equal(parent, EvidenceResources(repository).ReadPrevious(branch.BaseCommitId.Value)!.Address);
        StoredGraphRevision graph = store.ReadBusinessGraph(branch.StateRevisionAddress!.Value, source);
        Assert.Equal(source, graph.SourceGraph);

        Assert.Equal(schema, graph.SchemaRoot);
        Assert.Equal(rootId, graph.RootId);
        Assert.Equal(storage.Keys.Select(id => id.Value).Order(), graph.ObjectHeads.Keys.Order());
        ObjectVersionRecord[] records = DefaultRecords(store, graph).ToArray();
        Assert.Equal(edges.Keys.Select(id => id.Value).Order(), records.Select(row => row.ObjectId).Order());
        Assert.Equal(2, records.Length);
        foreach (ObjectVersionRecord record in records) {
            Assert.Equal(ObjectVersionKind.Base, record.Kind);
            Assert.Null(record.PriorAddress);
            ObjectVersionChain chain = store.ReadObjectChain(graph, record.ObjectId);
            ObjectVersionChainEntry entry = Assert.Single(chain.Entries);
            Assert.Equal(source, entry.SourceGraph);
            Assert.Equal(schema, entry.SchemaRoot);
            Assert.Equal(graph.OwnedBatch, chain.Head);
            Assert.Equal(host.Schema(), DefaultLayout(store, graph, record.ObjectId).Schema);
            Assert.Equal((long)entry.PayloadBytes, chain.ReconstructionPayloadBytes);
            Assert.Equal(chain.ReconstructionPayloadBytes, branch.Baseline!.Objects[new(record.ObjectId)].Storage!.Value.ReconstructionPayloadBytes);
        }
        ObjectId list = edges[rootId].List;
        Assert.Equal(storage[list], branch.Baseline!.Objects[list].Storage);
        Assert.Equal(storage[list].Head, graph.ObjectHeads[list.Value]);
        Assert.All(branch.Baseline!.Objects.Values, row => Assert.False(row.RequiresRewrite));
        DevelopmentAssertIdentity(branch, host, rootId, edges);
    }

    private static void DevelopmentAssertDelta(GraphRecordStore store, WorkingCopy branch, FrameAddress source,
        FrameAddress saved, FrameAddress schema, ObjectId list, ObjectStorageInfo prior) {
        StoredGraphRevision graph = store.ReadBusinessGraph(saved, source);

        Assert.Equal(source, graph.SourceGraph);
        Assert.Equal(schema, graph.SchemaRoot);
        ObjectVersionRecord delta = Assert.Single(DefaultRecords(store, graph));
        Assert.Equal(list.Value, delta.ObjectId);
        Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
        Assert.Equal(prior.Head, delta.PriorAddress);
        ObjectVersionChain chain = store.ReadObjectChain(graph, list.Value);
        ObjectVersionChainEntry entry = chain.Entries[^1];
        Assert.Equal(source, entry.SourceGraph);
        Assert.Equal(schema, entry.SchemaRoot);
        Assert.Equal(prior.Head, entry.Record.PriorAddress);
        Assert.Equal(graph.OwnedBatch, chain.Head);
        Assert.NotEqual(saved, chain.Head);
        Assert.Equal(prior.ReconstructionPayloadBytes + entry.PayloadBytes, chain.ReconstructionPayloadBytes);
        Assert.Equal(chain.ReconstructionPayloadBytes, branch.Baseline!.Objects[list].Storage!.Value.ReconstructionPayloadBytes);
    }

    private static void DevelopmentAssertIdentity(WorkingCopy branch, DevelopmentWorkflowHost host, ObjectId rootId,
        Dictionary<ObjectId, (long Value, ObjectId List, ObjectId Child)> original) {
        Assert.Equal(rootId, branch.StateId);
        var edges = DevelopmentEdges(branch, host);
        Assert.Equal(original.Keys.Order(), edges.Keys.Order());
        foreach (var (id, old) in original) {
            Assert.Equal(old.List, edges[id].List);
            Assert.Equal(old.Child, edges[id].Child);
            Assert.Equal(old.Value + host.Offset, edges[id].Value);
        }
        Assert.Equal(original.Keys.Append(original[rootId].List).Order(), branch.Baseline!.Objects.Keys.Order());
        Assert.Equal(128, branch.Baseline!.Objects[original[rootId].List].Current.GetListState<int>().Count);
        host.Check(branch.State!, 2 + host.Offset, 1 + host.Offset,
            branch.Baseline!.Objects[original[rootId].List].Current.GetListState<int>()[7]);
    }
    private static Dictionary<ObjectId, (long Value, ObjectId List, ObjectId Child)> DevelopmentEdges(WorkingCopy branch,
        DevelopmentWorkflowHost host) => branch.Baseline!.Objects.Where(pair => pair.Value.Current.Kind == ObjectStateKind.Durable)
        .ToDictionary(pair => pair.Key, pair => host.Edges(pair.Value.Current));
    private static Dictionary<ObjectId, ObjectStorageInfo> DevelopmentStorage(WorkingCopy branch) =>
        branch.Baseline!.Objects.ToDictionary(pair => pair.Key, pair => pair.Value.Storage!.Value);
    private static void DevelopmentAssertHistoryUnchanged(AncestryHistoryDirectory history, Dictionary<string, string> expected) {
        Dictionary<string, string> actual = history.ReadContents();
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach ((string path, string contents) in expected) { Assert.Equal(contents, actual[path]); }
    }

    private sealed class DevelopmentWorkflowHost(Type host, int offset) {
        internal int Offset => offset;
        internal Func<StateModelRegistry> Models { get; } = host.GetMethod("Models")!.CreateDelegate<Func<StateModelRegistry>>();
        internal Func<IDurableObject> Create { get; } = host.GetMethod("Create")!.CreateDelegate<Func<IDurableObject>>();
        internal Func<IDurableObject, IDurableObject> Child { get; } = host.GetMethod("Child")!.CreateDelegate<Func<IDurableObject, IDurableObject>>();
        internal Action<IDurableObject, int> ChangeList { get; } = host.GetMethod("ChangeList")!.CreateDelegate<Action<IDurableObject, int>>();
        internal Action<IDurableObject, long, long, int> Check { get; } = host.GetMethod("Check")!.CreateDelegate<Action<IDurableObject, long, long, int>>();
        internal Func<ObjectStateRecord, (long Value, ObjectId List, ObjectId Child)> Edges { get; } =
            host.GetMethod("Edges")!.CreateDelegate<Func<ObjectStateRecord, (long, ObjectId, ObjectId)>>();
        internal DurableSchema Schema() => Models().CreateEnvironment().ResolveCurrentModel(host.Assembly.GetType("DevelopmentWorkflow.Widget")!).CurrentSchema;
        internal static DevelopmentWorkflowHost Load(GeneratorTestRun run) {
            Type host = EmitAndLoad(run.OutputCompilation).GetType("DevelopmentWorkflow.Host")!;
            return new(host, (int)host.GetField("Offset")!.GetRawConstantValue()!);
        }
    }

    private static string DevelopmentWorkflowSource(int version, bool wideMarker) {
        string family = Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes("workflow.widget"));
        int offset = version == 1 ? 0 : wideMarker ? 200 : 100;
        string marker = version == 1 ? "" : $"[DurableField(8)] public {(wideMarker ? "long" : "byte")} Marker;";
        string upgrade = version == 1 ? "" : $$"""
            private static void UpgradeStateV1ToV2(in W.V1 prior, out W.V2 next) {
                next = new((long)prior.Segment0Field1 + {{offset}}, prior.Segment0Field2, prior.Segment0Field3,
                    prior.Segment0Field4, prior.Segment0Field5, prior.Segment0Field6, prior.Segment0Field7, {{(wideMarker ? "900L" : "(byte)9")}});
            }
            """;
        return $$"""
            using System;
            using System.Collections.Generic;
            using System.Linq;
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Persistence;
            using Atelia.DurableGraph.Runtime;
            using Atelia.DurableGraph.Schema;
            using W = global::Atelia.DurableGraph.Generated.Family_{{family}};
            namespace DevelopmentWorkflow;
            [DurableType("workflow.widget", {{version}})]
            public sealed partial class Widget : IDurableObject {
                [DurableField(1)] public {{(version == 1 ? "int" : "long")}} Value;
                [DurableField(2)] public List<int> Numbers = new();
                [DurableField(3)] public Widget? Child;
                [DurableField(4)] public double A;
                [DurableField(5)] public double B;
                [DurableField(6)] public double C;
                [DurableField(7)] public double D;
                {{marker}}
                {{upgrade}}
            }
            public static class Host {
                public const int Offset = {{offset}};
                public static StateModelRegistry Models() {
                    var registry = new StateModelRegistry();
                    Atelia.DurableGraph.Generated.DurableDefinitions.Register(registry);
                    return registry;
                }
                public static IDurableObject Create() {
                    var numbers = Enumerable.Range(0, 128).ToList();
                    var root = new Widget { Value = 1, Numbers = numbers, A = .25, B = .5, C = .75, D = 1 };
                    root.Child = new Widget { Value = 2, Numbers = numbers, Child = root, A = .25, B = .5, C = .75, D = 1 };
                    return root;
                }
                public static IDurableObject Child(IDurableObject value) => ((Widget)value).Child!;
                public static void ChangeList(IDurableObject value, int seven) => ((Widget)value).Numbers[7] = seven;
                public static (long Value, ObjectId List, ObjectId Child) Edges(ObjectStateRecord record) {
                    var dto = record.GetState<W.V{{version}}>();
                    return (dto.Segment0Field1, dto.Segment0Field2, dto.Segment0Field3);
                }
                public static void Check(IDurableObject value, long expected, long childExpected, int seven) {
                    var root = (Widget)value;
                    if (root.Value != expected || root.Child is not { } child || child.Value != childExpected ||
                        ReferenceEquals(root, child) || !ReferenceEquals(child.Child, root) || !ReferenceEquals(root.Numbers, child.Numbers)) {
                        throw new InvalidOperationException("Widget values, ring or List alias differ.");
                    }
                    foreach (Widget item in new[] { root, child }) {
                        if (item.A != .25 || item.B != .5 || item.C != .75 || item.D != 1{{(version == 1 ? "" : wideMarker ? " || item.Marker != 900L" : " || item.Marker != 9")}}) {
                            throw new InvalidOperationException("Complete upgraded Widget fields differ.");
                        }
                    }
                    if (root.Numbers.Count != 128) { throw new InvalidOperationException("List count differs."); }
                    for (int i = 0; i < 128; i++) {
                        if (root.Numbers[i] != (i == 7 ? seven : i)) { throw new InvalidOperationException("List values differ."); }
                    }
                }
            }
            """;
    }
}
