using System.Reflection;
using Atelia.DurableGraph.Build;
using Atelia.DurableGraph.Persistence;

using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    // This Fact exercises real SG assemblies and real repository publication.
    // The separate MSBuild probe owns Off/Publish target execution evidence.
    [Fact]
    public void GeneratedDevelopmentForksKeepUserIdentitiesAndPublishOnlyCleanWinningVersion() {
        using AncestryHistoryDirectory history = new();
        SchemaHistoryTool publisher = new();
        GeneratorTestRun v1 = RunGenerator(DevelopmentWorkflowSource(1, wideMarker: false), [], null, "true");
        AssertSchemaOnlyCompiles(v1);
        publisher.Publish(history.WriteManifest(v1), history.History);
        Dictionary<string, string> acceptedV1 = history.ReadContents();
        Assert.Single(acceptedV1);
        DevelopmentWorkflowHost old = DevelopmentWorkflowHost.Load(v1);

        // Both drafts compile against only the same accepted V1 template. Their
        // generated V2 candidates are not accepted into compiler history here.
        GeneratorTestRun draftA = RunGenerator(DevelopmentWorkflowSource(2, wideMarker: false), history.ReadAdditionalTexts(), null, "true");
        GeneratorTestRun draftB = RunGenerator(DevelopmentWorkflowSource(2, wideMarker: true), history.ReadAdditionalTexts(), null, "true");
        AssertSchemaOnlyCompiles(draftA);
        AssertSchemaOnlyCompiles(draftB);
        DevelopmentWorkflowHost a = DevelopmentWorkflowHost.Load(draftA), b = DevelopmentWorkflowHost.Load(draftB);
        Assert.Equal(a.Schema().Type, b.Schema().Type);
        Assert.Equal(2, a.Schema().Version);
        Assert.Equal(2, b.Schema().Version);
        Assert.NotEqual(a.Schema(), b.Schema());
        DevelopmentAssertHistoryUnchanged(history, acceptedV1);

        using RawBaseDirectory directory = new();
        FrameAddress baseline, sharedV1, aState, aSchema, bState, bSchema, finalState, finalSchema, continued;
        ObjectId rootId, listId;
        Dictionary<ObjectId, (long Value, ObjectId List, ObjectId Child)> originalEdges;
        Dictionary<ObjectId, ObjectStorageInfo> originalStorage;
        PrivateRefImage originalShared;
        PrivateFrameImage[] seedFrames, draftFrames;
        using (Repository repository = Repository.CreateNew(directory.Path, old.Models(), schemaWriteMode: SchemaWriteMode.Shared)) {
            using BranchCheckout main = repository.CreateBranch("main", old.Create());
            ObjectId firstRoot = main.StateId!.Value;
            ObjectId[] firstIds = main.Baseline!.Objects.Keys.ToArray();
            // A fresh ring consumes a new block of IDs; promoting its existing
            // child then proves root identity is not accidental 1/2/3 allocation.
            main.CommitState(old.Create());
            IDurableObject child = old.Child(main.State!);
            baseline = main.CommitState(child).RevisionAddress;
            Assert.Same(child, main.State);
            rootId = main.StateId!.Value;
            Assert.NotEqual(firstRoot, rootId);
            Assert.All(main.Baseline!.Objects.Keys, id => Assert.True(id.Value > firstIds.Max(id => id.Value)));
            old.Check(main.State!, 2, 1, 7);
            originalEdges = DevelopmentEdges(main, old);
            originalStorage = DevelopmentStorage(main);
            Assert.Equal(3, originalStorage.Count);
            listId = Assert.Single(main.Baseline!.Objects, row => row.Value.Current.Kind == ObjectStateKind.List).Key;
            Assert.Equal(listId, originalEdges[rootId].List);
            Assert.Equal(rootId, originalEdges[originalEdges[rootId].Child].Child);
            repository.CreateTag("user-v1", main.Head);
            sharedV1 = repository.SharedSchemaRoot!.Value;
            SchemaCatalogView catalog = SchemaCatalogView.Load(ReferenceGraphStore(repository), sharedV1);
            Assert.Equal(old.Schema(), Assert.Single(catalog.Schemas).Value);
            originalShared = PrivateReadRef(repository, 2);
            seedFrames = PrivateReadFrames(PrivateResources(repository).Data);
        }

        aState = DevelopmentSaveDraft(directory.Path, "dev-a", a, baseline, rootId, originalEdges,
            originalStorage, originalShared, out aSchema);
        bState = DevelopmentSaveDraft(directory.Path, "dev-b", b, baseline, rootId, originalEdges,
            originalStorage, originalShared, out bSchema);
        Assert.NotEqual(aSchema, bSchema);
        DevelopmentAssertHistoryUnchanged(history, acceptedV1);

        aState = DevelopmentContinue(directory.Path, "dev-a", a, aState, aSchema, rootId, originalEdges, 7001,
            originalShared);
        bState = DevelopmentContinue(directory.Path, "dev-b", b, bState, bSchema, rootId, originalEdges, 7002,
            originalShared);

        // Each real V2 host must refuse the other branch's accepted same-key
        // shape. Closing the readonly owners leaves the complete file set intact.
        Dictionary<string, byte[]> beforeWrong = PrivateFiles(directory.Path);
        using (Repository repository = Repository.OpenReadOnlyExisting(directory.Path, a.Models())) {
            a.Check(repository.ReadState(repository.GetHead("dev-a")), 102, 101, 7001);
            Assert.ThrowsAny<InvalidDataException>(() => repository.ReadState(repository.GetHead("dev-b")));
            Assert.False(repository.IsFaulted);
        }
        using (Repository repository = Repository.OpenReadOnlyExisting(directory.Path, b.Models())) {
            b.Check(repository.ReadState(repository.GetHead("dev-b")), 202, 201, 7002);
            Assert.ThrowsAny<InvalidDataException>(() => repository.ReadState(repository.GetHead("dev-a")));
            Assert.False(repository.IsFaulted);
        }
        PrivateAssertFilesUnchanged(beforeWrong, PrivateFiles(directory.Path));

        // Compiler history accepts only the winning V2. This tool call is not
        // claimed as execution of the packaged MSBuild Publish/Verify targets.
        publisher.Publish(history.WriteManifest(draftB), history.History);
        publisher.Verify(history.WriteManifest(draftB), history.History);
        Assert.Equal(2, history.ReadContents().Count);
        foreach ((string file, string contents) in acceptedV1) {
            Assert.Equal(contents, File.ReadAllText(Path.Combine(history.History, file)));
        }
        GeneratorTestRun finalRun = RunGenerator(DevelopmentWorkflowSource(2, wideMarker: true), history.ReadAdditionalTexts(), null, "true");
        AssertSchemaOnlyCompiles(finalRun);
        DevelopmentWorkflowHost winner = DevelopmentWorkflowHost.Load(finalRun);
        Assert.Equal(b.Schema(), winner.Schema());

        using (Repository repository = Repository.OpenExisting(directory.Path, winner.Models())) {
            GraphRecordStore store = ReferenceGraphStore(repository);
            draftFrames = PrivateReadFrames(PrivateResources(repository).Data);
            FrameAddress fixedSource = repository.ResolveTag("user-v1").RevisionAddress;
            Assert.Equal(baseline, fixedSource);
            long frames = store.AppendedFrames;
            repository.CreateBranch("final", repository.ResolveTag("user-v1"));
            Assert.Equal(frames, store.AppendedFrames); // A ref-only operation, not a new root.
            using BranchCheckout final = repository.CheckoutPrivate("final");
            Assert.Equal(baseline, final.StateRevisionAddress);
            DevelopmentAssertIdentity(final, winner, rootId, originalEdges);
            Assert.Equal(2, final.Baseline!.Objects.Values.Count(row => row.RequiresRewrite));
            finalState = final.CommitStateToShared().RevisionAddress;
            finalSchema = repository.SharedSchemaRoot!.Value;
            Assert.NotEqual(sharedV1, finalSchema);
            DevelopmentAssertUpgrade(store, final, winner, baseline, baseline, finalSchema, rootId, originalEdges, originalStorage);
            SchemaCatalogView published = SchemaCatalogView.Load(store, finalSchema);
            Assert.Equal(2, published.Schemas.Count);
            Assert.Equal(old.Schema(), published.Schemas[new(old.Schema().Type, 1)]);
            Assert.Equal(winner.Schema(), published.Schemas[new(winner.Schema().Type, 2)]);
            Assert.DoesNotContain(published.Schemas.Values, schema => schema.Equals(a.Schema()));
            Assert.Equal(aState, repository.GetHead("dev-a").RevisionAddress);
            Assert.Equal(bState, repository.GetHead("dev-b").RevisionAddress);
            Assert.Equal(baseline, repository.GetHead("main").RevisionAddress);
            Assert.Equal(sharedV1, store.ReadGraph(baseline).SchemaRoot);
            Assert.Equal(aSchema, store.ReadGraph(aState).SchemaRoot);
            Assert.Equal(bSchema, store.ReadGraph(bState).SchemaRoot);
            PrivateAssertFramesPreserved(PrivateResources(repository).Data, seedFrames);
            PrivateAssertFramesPreserved(PrivateResources(repository).Data, draftFrames);
        }

        // A fresh winner owner writes a real Delta with actual prior/head/H.
        using (Repository repository = Repository.OpenExisting(directory.Path, winner.Models(), schemaWriteMode: SchemaWriteMode.Shared)) {
            PrivateRefImage sharedBefore = PrivateReadRef(repository, 2);
            Assert.Equal(finalSchema, repository.SharedSchemaRoot);
            using BranchCheckout final = repository.Checkout("final");
            DevelopmentAssertIdentity(final, winner, rootId, originalEdges);
            ObjectStorageInfo prior = final.Baseline!.Objects[listId].Storage!.Value;
            winner.ChangeList(final.State!, 9000);
            continued = final.CommitState(new(int.MaxValue, 1)).RevisionAddress;
            DevelopmentAssertDelta(ReferenceGraphStore(repository), final, finalState, continued, finalSchema, listId, prior);
            PrivateAssertSameRef(sharedBefore, PrivateReadRef(repository, 2));
            PrivateAssertFramesPreserved(PrivateResources(repository).Data, draftFrames);
        }
        Dictionary<string, byte[]> beforeFinalRead = PrivateFiles(directory.Path);
        using (Repository repository = Repository.OpenReadOnlyExisting(directory.Path, old.Models())) {
            Assert.Equal(baseline, repository.ResolveTag("user-v1").RevisionAddress);
            old.Check(repository.ReadState(repository.ResolveTag("user-v1")), 2, 1, 7);
        }
        using (Repository repository = Repository.OpenReadOnlyExisting(directory.Path, winner.Models())) {
            winner.Check(repository.ReadState(repository.ResolveTag("user-v1")), 202, 201, 7);
            winner.Check(repository.ReadState(repository.GetHead("final")), 202, 201, 9000);
            winner.Check(repository.ReadState(repository.GetHead("dev-b")), 202, 201, 7002);
            Assert.Equal(continued, repository.GetHead("final").RevisionAddress);
            Assert.Equal(finalSchema, repository.SharedSchemaRoot);
            PrivateAssertFramesPreserved(PrivateResources(repository).Data, seedFrames);
            PrivateAssertFramesPreserved(PrivateResources(repository).Data, draftFrames);
        }
        PrivateAssertFilesUnchanged(beforeFinalRead, PrivateFiles(directory.Path));
    }

    private static FrameAddress DevelopmentSaveDraft(string path, string name, DevelopmentWorkflowHost host,
        FrameAddress baseline, ObjectId rootId, Dictionary<ObjectId, (long Value, ObjectId List, ObjectId Child)> edges,
        Dictionary<ObjectId, ObjectStorageInfo> sourceStorage, PrivateRefImage shared, out FrameAddress schema) {
        using Repository repository = Repository.OpenExisting(path, host.Models());
        GraphRecordStore store = ReferenceGraphStore(repository);
        Assert.Equal(baseline, repository.ResolveTag("user-v1").RevisionAddress);
        long appended = store.AppendedFrames;
        repository.CreateBranch(name, repository.ResolveTag("user-v1"));
        Assert.Equal(appended, store.AppendedFrames);
        PrivateAssertSameRef(shared, PrivateReadRef(repository, 2));
        using BranchCheckout branch = repository.CheckoutPrivate(name);
        Assert.Equal(baseline, branch.StateRevisionAddress);
        DevelopmentAssertIdentity(branch, host, rootId, edges);
        Assert.Equal(sourceStorage.Keys.Order(), branch.Baseline!.Objects.Keys.Order());
        foreach ((ObjectId id, ObjectStorageInfo info) in sourceStorage) { Assert.Equal(info, branch.Baseline!.Objects[id].Storage); }
        Assert.Equal(2, branch.Baseline!.Objects.Values.Count(row => row.RequiresRewrite));
        NormalizedGraphState before = branch.Baseline!;
        branch.PrepareAndDiscard(branch.State!);
        Assert.Same(before, branch.Baseline);
        FrameAddress eventAddress = branch.CommitEvent(branch.State!).RevisionAddress;
        Assert.Same(before, branch.Baseline);
        Assert.Equal(baseline, branch.StateRevisionAddress);
        Assert.Equal(2, branch.Baseline!.Objects.Values.Count(row => row.RequiresRewrite));
        StoredGraphRevision eventGraph = store.ReadGraph(eventAddress);
        Assert.Equal(baseline, eventGraph.Parent);
        Assert.Equal(baseline, eventGraph.SourceGraph);
        schema = eventGraph.SchemaRoot!.Value;
        Assert.NotEqual(repository.SharedSchemaRoot, schema);
        FrameAddress saved = branch.CommitState().RevisionAddress;
        DevelopmentAssertUpgrade(store, branch, host, baseline, eventAddress, schema, rootId, edges, sourceStorage);
        PrivateAssertSameRef(shared, PrivateReadRef(repository, 2));
        return saved;
    }

    private static FrameAddress DevelopmentContinue(string path, string name, DevelopmentWorkflowHost host,
        FrameAddress source, FrameAddress schema, ObjectId rootId, Dictionary<ObjectId, (long Value, ObjectId List, ObjectId Child)> edges,
        int seven, PrivateRefImage sharedRef) {
        using Repository repository = Repository.OpenExisting(path, host.Models());
        using BranchCheckout branch = repository.CheckoutPrivate(name);
        Assert.Equal(source, branch.StateRevisionAddress);
        DevelopmentAssertIdentity(branch, host, rootId, edges);
        ObjectId listId = edges[rootId].List;
        Dictionary<ObjectId, ObjectStorageInfo> priorStorage = DevelopmentStorage(branch);
        host.ChangeList(branch.State!, seven);
        FrameAddress saved = branch.CommitState(new(int.MaxValue, 1)).RevisionAddress;
        DevelopmentAssertDelta(ReferenceGraphStore(repository), branch, source, saved, schema, listId, priorStorage[listId]);
        foreach ((ObjectId id, ObjectStorageInfo info) in priorStorage.Where(pair => pair.Key != listId)) {
            Assert.Equal(info, branch.Baseline!.Objects[id].Storage);
        }
        PrivateAssertSameRef(sharedRef, PrivateReadRef(repository, 2));
        host.Check(branch.State!, 2 + host.Offset, 1 + host.Offset, seven);
        return saved;
    }

    private static void DevelopmentAssertUpgrade(GraphRecordStore store, BranchCheckout branch, DevelopmentWorkflowHost host,
        FrameAddress source, FrameAddress parent, FrameAddress schema, ObjectId rootId,
        Dictionary<ObjectId, (long Value, ObjectId List, ObjectId Child)> edges, Dictionary<ObjectId, ObjectStorageInfo> storage) {
        StoredGraphRevision graph = store.ReadGraph(branch.Head.RevisionAddress);
        Assert.Equal(source, graph.SourceGraph);
        Assert.Equal(parent, graph.Parent);
        Assert.Equal(schema, graph.SchemaRoot);
        Assert.Equal(rootId, graph.RootId);
        Assert.Equal(storage.Keys.Select(id => id.Value).Order(), graph.ObjectHeads.Keys.Order());
        ObjectVersionRecord[] records = DefaultRecords(store, graph.Address).ToArray();
        Assert.Equal(edges.Keys.Select(id => id.Value).Order(), records.Select(row => row.ObjectId).Order());
        Assert.Equal(2, records.Length);
        foreach (ObjectVersionRecord record in records) {
            Assert.Equal(ObjectVersionKind.Base, record.Kind);
            Assert.Null(record.PriorAddress);
            ObjectVersionChain chain = store.ReadObjectChain(graph.Address, record.ObjectId);
            ObjectVersionChainEntry entry = Assert.Single(chain.Entries);
            Assert.Equal(source, entry.SourceGraph);
            Assert.Equal(schema, entry.SchemaRoot);
            Assert.Equal(graph.OwnedBatch, chain.Head);
            Assert.Equal(host.Schema(), DefaultLayout(store, graph.Address, record.ObjectId).Schema);
            Assert.Equal((long)entry.PayloadBytes, chain.ReconstructionPayloadBytes);
            Assert.Equal(chain.ReconstructionPayloadBytes, branch.Baseline!.Objects[new(record.ObjectId)].Storage!.Value.ReconstructionPayloadBytes);
        }
        ObjectId list = edges[rootId].List;
        Assert.Equal(storage[list], branch.Baseline!.Objects[list].Storage);
        Assert.Equal(storage[list].Head, graph.ObjectHeads[list.Value]);
        Assert.All(branch.Baseline!.Objects.Values, row => Assert.False(row.RequiresRewrite));
        DevelopmentAssertIdentity(branch, host, rootId, edges);
    }

    private static void DevelopmentAssertDelta(GraphRecordStore store, BranchCheckout branch, FrameAddress source,
        FrameAddress saved, FrameAddress schema, ObjectId list, ObjectStorageInfo prior) {
        StoredGraphRevision graph = store.ReadGraph(saved);
        Assert.Equal(source, graph.Parent);
        Assert.Equal(source, graph.SourceGraph);
        Assert.Equal(schema, graph.SchemaRoot);
        ObjectVersionRecord delta = Assert.Single(DefaultRecords(store, saved));
        Assert.Equal(list.Value, delta.ObjectId);
        Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
        Assert.Equal(prior.Head, delta.PriorAddress);
        ObjectVersionChain chain = store.ReadObjectChain(saved, list.Value);
        ObjectVersionChainEntry entry = chain.Entries[^1];
        Assert.Equal(source, entry.SourceGraph);
        Assert.Equal(schema, entry.SchemaRoot);
        Assert.Equal(prior.Head, entry.Record.PriorAddress);
        Assert.Equal(graph.OwnedBatch, chain.Head);
        Assert.NotEqual(saved, chain.Head);
        Assert.Equal(prior.ReconstructionPayloadBytes + entry.PayloadBytes, chain.ReconstructionPayloadBytes);
        Assert.Equal(chain.ReconstructionPayloadBytes, branch.Baseline!.Objects[list].Storage!.Value.ReconstructionPayloadBytes);
    }

    private static void DevelopmentAssertIdentity(BranchCheckout branch, DevelopmentWorkflowHost host, ObjectId rootId,
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
    private static Dictionary<ObjectId, (long Value, ObjectId List, ObjectId Child)> DevelopmentEdges(BranchCheckout branch,
        DevelopmentWorkflowHost host) => branch.Baseline!.Objects.Where(pair => pair.Value.Current.Kind == ObjectStateKind.Durable)
        .ToDictionary(pair => pair.Key, pair => host.Edges(pair.Value.Current));
    private static Dictionary<ObjectId, ObjectStorageInfo> DevelopmentStorage(BranchCheckout branch) =>
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
