using System.Reflection;
using Atelia.DurableGraph.Build;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Persistence.MetaBootstrapP0;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Fact]
    public void GeneratedPrivateInlineContainerReplacementRequiresEveryOldLayoutAndColdMatchingEnvironmentsContinue() {
        var (a, b) = CompilePrivateContainerFixtures();
        ObjectLayout[] oldLayouts = PublicPrivateContainerLayouts(wide: false), newLayouts = PublicPrivateContainerLayouts(wide: true);
        Assert.Equal(oldLayouts, a.Layouts());
        Assert.Equal(newLayouts, b.Layouts());
        Assert.Equal(a.Schema("Point"), PublicPrivatePoint(wide: false));
        Assert.Equal(b.Schema("Point"), PublicPrivatePoint(wide: true));
        Assert.Equal(a.Schema("Root"), b.Schema("Root")); // Nominal container references do not carry the inline shape.
        var replacements = oldLayouts.Zip(newLayouts, (old, next) => (OldLayout: old, NewLayout: next)).ToArray();
        for (int i = 0; i < oldLayouts.Length; i++) {
            Assert.Equal(oldLayouts[i].Type, newLayouts[i].Type);
            Assert.NotEqual(oldLayouts[i], newLayouts[i]);
        }
        using RawBaseDirectory directory = new();
        FrameAddress oldState, sharedSchema, privateState, privateSchema;
        PrivateFrameImage[] originalFrames;
        PrivateRefImage shared;
        using (Repository repository = Repository.CreateNew(directory.Path, a.Models(), schemaWriteMode: SchemaWriteMode.Shared)) {
            using BranchCheckout main = repository.CreateBranch("main", a.CreateRoot());
            oldState = main.Head.RevisionAddress;
            repository.CreateTag("a-initial", main.Head);
            sharedSchema = repository.SharedSchemaRoot!.Value;
            P0CatalogView catalog = P0CatalogView.Load(ReferenceGraphStore(repository), sharedSchema);
            foreach (ObjectLayout layout in oldLayouts) { Assert.Equal(layout, catalog.ResolveLayout(catalog.DescriptorId(layout))); }
            AssertPrivateContainerSources(repository, oldState, oldLayouts, ObjectVersionKind.Base);
            originalFrames = PrivateReadFrames(PrivateResources(repository).Data);
            shared = PrivateReadRef(repository, 2);
        }

        using (Repository repository = Repository.OpenExisting(directory.Path, b.Models())) {
            PrivatePublicationImage before = PrivatePublication(repository);
            // The same Root and TypeExpr cannot authorize an unchanged old exact
            // Array, List or Dictionary dependency on Point's former shape.
            for (int omitted = 0; omitted < replacements.Length; omitted++) {
                var partial = replacements.Where((_, index) => index != omitted).ToArray();
                Assert.Throws<SchemaConflictException>(() => repository.CreatePrivateBranch("private", b.CreateRoot(),
                    [PublicPrivatePoint(wide: true)], containerReplacements: partial));
                Assert.DoesNotContain("private", repository.ListBranches());
                PrivateAssertPublicationUnchanged(repository, before);
            }
            using BranchCheckout branch = repository.CreatePrivateBranch("private", b.CreateRoot(),
                [PublicPrivatePoint(wide: true)], containerReplacements: replacements);
            b.Check(branch.State!, changed: false);
            privateState = branch.Head.RevisionAddress;
            P0GraphStore store = ReferenceGraphStore(repository);
            P0GraphSnapshot graph = store.ReadGraph(privateState);
            privateSchema = graph.SchemaRoot!.Value;
            Assert.Equal(4, graph.Objects.Count);
            Assert.Equal(4, DefaultRecords(store, privateState).Count);
            Assert.All(DefaultRecords(store, privateState), row => {
                Assert.Equal(ObjectVersionKind.Base, row.Kind);
                Assert.Null(row.PriorAddress);
                Assert.Equal(graph.OwnedBatch, graph.Objects[row.ObjectId]);
                Assert.Single(store.ReadObjectChain(privateState, row.ObjectId).Entries);
            });
            Assert.Null(graph.Parent);
            Assert.Null(graph.SourceGraph);
            P0CatalogView original = P0CatalogView.Load(store, sharedSchema), forked = P0CatalogView.Load(store, privateSchema);
            Assert.Equal(PublicPrivatePoint(wide: false), original.Schemas[new(PublicPrivatePoint(false).Type, 1)]);
            Assert.Equal(PublicPrivatePoint(wide: true), forked.Schemas[new(PublicPrivatePoint(true).Type, 1)]);
            Assert.NotEqual(original.SchemaNodeId(PublicPrivatePoint(false)), forked.SchemaNodeId(PublicPrivatePoint(true)));
            Assert.Equal(original.SchemaNodeId(a.Schema("Root")), forked.SchemaNodeId(b.Schema("Root")));
            for (int i = 0; i < oldLayouts.Length; i++) {
                Assert.Equal(newLayouts[i], forked.ResolveLayout(forked.DescriptorId(newLayouts[i])));
                Assert.NotEqual(original.DescriptorId(oldLayouts[i]), forked.DescriptorId(newLayouts[i]));
                Assert.Throws<InvalidDataException>(() => forked.DescriptorId(oldLayouts[i]));
            }
            AssertPrivateContainerSources(repository, privateState, newLayouts, ObjectVersionKind.Base);
            PrivateAssertSameRef(shared, PrivateReadRef(repository, 2));
            Assert.Equal(sharedSchema, repository.SharedSchemaRoot);
            PrivateAssertFramesPreserved(PrivateResources(repository).Data, originalFrames);
        }

        // New resources and matching A code continue the old exact view.
        using (Repository repository = Repository.OpenExisting(directory.Path, a.Models(), schemaWriteMode: SchemaWriteMode.Shared)) {
            using BranchCheckout main = repository.Checkout("main");
            a.Check(main.State!, changed: false);
            a.Change(main.State!);
            FrameAddress saved = main.CommitState().RevisionAddress;
            AssertPrivateContainerDeltas(repository, saved, oldState, oldLayouts, main);
            a.Check(repository.ReadState(main.Head), changed: true);
            PrivateAssertFramesPreserved(PrivateResources(repository).Data, originalFrames);
        }
        using (Repository repository = Repository.OpenExisting(directory.Path, b.Models())) {
            using BranchCheckout branch = repository.CheckoutPrivate("private");
            b.Check(branch.State!, changed: false);
            Assert.Equal(privateState, branch.StateRevisionAddress);
            Assert.Equal(privateSchema, branch.Catalog.Accepted!.Snapshot!.Address);
            b.Change(branch.State!);
            FrameAddress saved = branch.CommitState().RevisionAddress;
            AssertPrivateContainerDeltas(repository, saved, privateState, newLayouts, branch);
            b.Check(repository.ReadState(branch.Head), changed: true);
            PrivateAssertSameRef(shared, PrivateReadRef(repository, 2));
            Assert.Equal(sharedSchema, repository.SharedSchemaRoot);
            PrivateAssertFramesPreserved(PrivateResources(repository).Data, originalFrames);
        }

        Dictionary<string, byte[]> files = PrivateFiles(directory.Path);
        using (Repository reader = Repository.OpenReadOnlyExisting(directory.Path, a.Models())) {
            a.Check(reader.ReadState(reader.GetHead("main")), changed: true);
            a.Check(reader.ReadState(reader.ResolveTag("a-initial")), changed: false);
            Assert.ThrowsAny<InvalidDataException>(() => reader.ReadState(reader.GetHead("private")));
            Assert.False(reader.IsFaulted);
        }
        using (Repository reader = Repository.OpenReadOnlyExisting(directory.Path, b.Models())) {
            b.Check(reader.ReadState(reader.GetHead("private")), changed: true);
            Assert.ThrowsAny<InvalidDataException>(() => reader.ReadState(reader.GetHead("main")));
            Assert.False(reader.IsFaulted);
            PrivateAssertFramesPreserved(PrivateResources(reader).Data, originalFrames);
        }
        PrivateAssertFilesUnchanged(files, PrivateFiles(directory.Path));
    }

    [Fact]
    public void GeneratedPrivateContainerReplacementCannotSilentlyRebindUntouchedDeepInlineDeclarations() {
        var (a, b) = CompilePrivateContainerFixtures();
        ObjectLayout[] oldLayouts = PublicPrivateContainerLayouts(false), newLayouts = PublicPrivateContainerLayouts(true);
        var replacements = oldLayouts.Zip(newLayouts, (old, next) => (OldLayout: old, NewLayout: next)).ToArray();
        Assert.Equal(oldLayouts, a.Layouts());
        Assert.Equal(newLayouts, b.Layouts());
        using RawBaseDirectory directory = new();
        FrameAddress sharedSchema;
        PrivateFrameImage[] completed;
        PrivateRefImage shared;
        using (Repository repository = Repository.CreateNew(directory.Path, a.Models(), schemaWriteMode: SchemaWriteMode.Shared)) {
            using (BranchCheckout main = repository.CreateBranch("main", a.CreateRoot())) { }
            // These real accepted definitions are not reachable from the new B root.
            using BranchCheckout unused = repository.CreateBranch("unused", a.CreateUnused());
            sharedSchema = repository.SharedSchemaRoot!.Value;
            P0CatalogView catalog = P0CatalogView.Load(ReferenceGraphStore(repository), sharedSchema);
            Assert.Equal(a.Schema("Deep"), catalog.Schemas[new(a.Schema("Deep").Type, 1)]);
            Assert.Equal(a.Schema("Unused"), catalog.Schemas[new(a.Schema("Unused").Type, 1)]);
            shared = PrivateReadRef(repository, 2);
            completed = PrivateReadFrames(PrivateResources(repository).Data);
        }
        using (Repository repository = Repository.OpenExisting(directory.Path, b.Models())) {
            PrivatePublicationImage before = PrivatePublication(repository);
            Assert.Throws<SchemaConflictException>(() => repository.CreatePrivateBranch("private", b.CreateRoot(),
                [b.Schema("Point")], containerReplacements: replacements));
            PrivateAssertPublicationUnchanged(repository, before);
            Assert.Throws<SchemaConflictException>(() => repository.CreatePrivateBranch("private", b.CreateRoot(),
                [b.Schema("Point"), b.Schema("Deep")], containerReplacements: replacements));
            PrivateAssertPublicationUnchanged(repository, before);
            Assert.DoesNotContain("private", repository.ListBranches());
            using BranchCheckout branch = repository.CreatePrivateBranch("private", b.CreateRoot(),
                [b.Schema("Point"), b.Schema("Deep"), b.Schema("Unused")], containerReplacements: replacements);
            b.Check(branch.State!, changed: false);
            P0GraphSnapshot graph = ReferenceGraphStore(repository).ReadGraph(branch.Head.RevisionAddress);
            Assert.Equal(4, graph.Objects.Count);
            Assert.All(DefaultRecords(ReferenceGraphStore(repository), graph.Address), row => Assert.Equal(ObjectVersionKind.Base, row.Kind));
            P0CatalogView view = P0CatalogView.Load(ReferenceGraphStore(repository), graph.SchemaRoot!.Value);
            Assert.Equal(b.Schema("Deep"), view.Schemas[new(b.Schema("Deep").Type, 1)]);
            Assert.Equal(b.Schema("Unused"), view.Schemas[new(b.Schema("Unused").Type, 1)]);
            AssertPrivateContainerSources(repository, graph.Address, newLayouts, ObjectVersionKind.Base);
            PrivateAssertSameRef(shared, PrivateReadRef(repository, 2));
            Assert.Equal(sharedSchema, repository.SharedSchemaRoot);
            PrivateAssertFramesPreserved(PrivateResources(repository).Data, completed);
        }
        Dictionary<string, byte[]> files = PrivateFiles(directory.Path);
        using (Repository reader = Repository.OpenReadOnlyExisting(directory.Path, a.Models())) {
            a.CheckUnused(reader.ReadState(reader.GetHead("unused")));
            a.Check(reader.ReadState(reader.GetHead("main")), changed: false);
        }
        using (Repository reader = Repository.OpenReadOnlyExisting(directory.Path, b.Models())) {
            b.Check(reader.ReadState(reader.GetHead("private")), changed: false);
            PrivateAssertFramesPreserved(PrivateResources(reader).Data, completed);
        }
        PrivateAssertFilesUnchanged(files, PrivateFiles(directory.Path));
    }

    // Public values are logical replacement requests, not signed storage sources.
    // Equality with actual generated bindings and the owner-selected descriptors
    // demonstrates that callers need no new public catalog-query API.
    private static DurableSchema PublicPrivatePoint(bool wide) => new("PCR.Point", 1, SchemaKind.InlineValue,
        new DurableFieldInfo(1, wide ? TypeTag.Int64 : TypeTag.Int32));
    private static ObjectLayout[] PublicPrivateContainerLayouts(bool wide) {
        DurableFieldInfo element = new(1, TypeTag.InlineValue, inlineSchema: PublicPrivatePoint(wide));
        return [
            ObjectLayout.ForArray(new ArrayLayout(TypeExprKind.VectorArray, element)),
            ObjectLayout.ForList(new ListLayout(element)),
            ObjectLayout.ForDictionary(new DictionaryLayout(new(1, TypeTag.Int32),
                new(2, TypeTag.InlineValue, inlineSchema: PublicPrivatePoint(wide)))),
        ];
    }
    private static void AssertPrivateContainerSources(Repository repository, FrameAddress address, ObjectLayout[] layouts, ObjectVersionKind kind) {
        P0GraphStore store = ReferenceGraphStore(repository);
        P0GraphSnapshot graph = store.ReadGraph(address);
        foreach (ObjectLayout layout in layouts) {
            uint id = Assert.Single(graph.Objects.Keys, id => DefaultLayout(store, address, id).Equals(layout));
            P0ObjectChain chain = store.ReadObjectChain(address, id);
            Assert.Equal(kind, chain.Entries[^1].Record.Kind);
            P0CatalogView origin = P0CatalogView.Load(store, chain.Entries[0].SchemaRoot!.Value);
            Assert.Equal(layout, P0TypedBodyCodec.Decode(chain.Entries[0].Record.Body, origin).Layout);
            Assert.Equal(layout, origin.ResolveLayout(origin.DescriptorId(layout)));
            Assert.Throws<InvalidDataException>(() => P0TypedBodyCodec.Decode(chain.Entries[0].Record.Body, catalog: null));
        }
    }
    private static void AssertPrivateContainerDeltas(Repository repository, FrameAddress address, FrameAddress source,
        ObjectLayout[] layouts, BranchCheckout branch) {
        P0GraphStore store = ReferenceGraphStore(repository);
        P0GraphSnapshot graph = store.ReadGraph(address), previous = store.ReadGraph(source);
        Assert.Equal(source, graph.Parent);
        Assert.Equal(source, graph.SourceGraph);
        Assert.Equal(previous.SchemaRoot, graph.SchemaRoot);
        Assert.Equal(previous.RootId, graph.RootId);
        Assert.Equal(previous.Objects.Keys, graph.Objects.Keys);
        ObjectVersionRecord[] deltas = DefaultRecords(store, address).ToArray();
        Assert.Equal(3, deltas.Length);
        Assert.All(deltas, delta => {
            Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
            Assert.Equal(previous.Objects[delta.ObjectId], delta.PriorAddress);
            P0ObjectChain chain = store.ReadObjectChain(address, delta.ObjectId);
            Assert.Equal(2, chain.Entries.Count);
            Assert.Equal(source, chain.Entries[^1].SourceGraph);
            Assert.Equal(graph.SchemaRoot, chain.Entries[^1].SchemaRoot);
            Assert.Equal(store.ReadObjectChain(source, delta.ObjectId).ReconstructionPayloadBytes + chain.Entries[^1].PayloadBytes,
                chain.ReconstructionPayloadBytes);
            Assert.Equal(chain.ReconstructionPayloadBytes, branch.Baseline!.Objects[new(delta.ObjectId)].Storage!.Value.ReconstructionPayloadBytes);
        });
        Assert.Equal(previous.Objects[graph.RootId.Value], graph.Objects[graph.RootId.Value]);
        AssertPrivateContainerSources(repository, address, layouts, ObjectVersionKind.Delta);
    }
    private static (PrivateContainerFixture A, PrivateContainerFixture B) CompilePrivateContainerFixtures() {
        using AncestryHistoryDirectory historyA = new(), historyB = new();
        SchemaHistoryTool tool = new();
        GeneratorTestRun runA = RunGenerator(PrivateContainerSource(wide: false), [], null, "true");
        GeneratorTestRun runB = RunGenerator(PrivateContainerSource(wide: true), [], null, "true");
        AssertSchemaOnlyCompiles(runA);
        AssertSchemaOnlyCompiles(runB);
        tool.Publish(historyA.WriteManifest(runA), historyA.History);
        tool.Verify(historyA.WriteManifest(runA), historyA.History);
        tool.Publish(historyB.WriteManifest(runB), historyB.History);
        tool.Verify(historyB.WriteManifest(runB), historyB.History);
        return (new(EmitAndLoad(runA.OutputCompilation)), new(EmitAndLoad(runB.OutputCompilation)));
    }
    private sealed class PrivateContainerFixture(Assembly assembly) {
        private readonly Type _host = assembly.GetType("PrivateContainerStory.Host")!;
        internal StateModelRegistry Models() => _host.GetMethod("Models")!.CreateDelegate<Func<StateModelRegistry>>()();
        internal DurableSchema Schema(string name) => _host.GetMethod("Schema")!
            .CreateDelegate<Func<StateBindingContext, string, DurableSchema>>()(Models().Snapshot(), name);
        internal ObjectLayout[] Layouts() => _host.GetMethod("Layouts")!.CreateDelegate<Func<StateBindingContext, ObjectLayout[]>>()(Models().Snapshot());
        internal IDurableObject CreateRoot() => _host.GetMethod("CreateRoot")!.CreateDelegate<Func<IDurableObject>>()();
        internal IDurableObject CreateUnused() => _host.GetMethod("CreateUnused")!.CreateDelegate<Func<IDurableObject>>()();
        internal void Change(object root) => _host.GetMethod("Change")!.CreateDelegate<Action<object>>()(root);
        internal void Check(object root, bool changed) => _host.GetMethod("Check")!.CreateDelegate<Action<object, bool>>()(root, changed);
        internal void CheckUnused(object root) => _host.GetMethod("CheckUnused")!.CreateDelegate<Action<object>>()(root);
    }
    private static string PrivateContainerSource(bool wide) => $$"""
        using System;
        using System.Collections.Generic;
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Persistence;
        using Atelia.DurableGraph.Runtime;
        using Atelia.DurableGraph.Schema;
        namespace PrivateContainerStory;
        [DurableType("PCR.Point", 1)] public partial struct Point { [DurableField(1)] public {{(wide ? "long" : "int")}} X; }
        [DurableType("PCR.Deep", 1)] public partial struct Deep { [DurableField(1)] public Point Point; }
        [DurableType("PCR.Unused", 1)] public sealed partial class Unused : IDurableObject { [DurableField(1)] public Deep Deep; }
        [DurableType("PCR.Root", 1)] public sealed partial class Root : IDurableObject {
            [DurableField(1)] public Point[] Array = new Point[128];
            [DurableField(2)] public List<Point> List = new();
            [DurableField(3)] public Dictionary<int, Point> Dictionary = new();
            [DurableField(4)] public Point[] ArrayAlias = null!;
            [DurableField(5)] public List<Point> ListAlias = null!;
            [DurableField(6)] public Dictionary<int, Point> DictionaryAlias = null!;
            [DurableField(7)] public Root Self = null!;
        }
        public static class Host {
            public static StateModelRegistry Models() {
                var models = new StateModelRegistry();
                Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
                return models;
            }
            public static DurableSchema Schema(StateBindingContext context, string name) => name switch {
                "Root" => context.ResolveCurrentModel(typeof(Root)).CurrentSchema,
                "Point" => context.ResolveCurrentValue(typeof(Point)).Slot.ValueSchema!,
                "Deep" => context.ResolveCurrentValue(typeof(Deep)).Slot.ValueSchema!,
                "Unused" => context.ResolveCurrentModel(typeof(Unused)).CurrentSchema,
                _ => throw new InvalidOperationException("Unknown fixture Schema."),
            };
            public static ObjectLayout[] Layouts(StateBindingContext context) {
                ObjectLayout Get(Type type) => context.TryGetCurrentObjectBinding(type, out ObjectBinding? binding)
                    ? binding!.CurrentLayout : throw new InvalidOperationException("Missing generated container binding.");
                return new[] { Get(typeof(Point[])), Get(typeof(List<Point>)), Get(typeof(Dictionary<int, Point>)) };
            }
            private static {{(wide ? "long" : "int")}} X(int index, bool changed) => ({{(wide ? "1L << 40" : "1")}}) + index + (changed && index == 7 ? 1000 : 0);
            public static IDurableObject CreateRoot() {
                var root = new Root();
                for (int i = 0; i < 128; i++) {
                    var point = new Point { X = X(i, false) };
                    root.Array[i] = point; root.List.Add(point); root.Dictionary.Add(i, point);
                }
                root.ArrayAlias = root.Array; root.ListAlias = root.List; root.DictionaryAlias = root.Dictionary; root.Self = root;
                return root;
            }
            public static IDurableObject CreateUnused() => new Unused { Deep = new Deep { Point = new Point { X = X(4, false) } } };
            public static void Change(object value) {
                var root = (Root)value;
                var point = new Point { X = X(7, true) };
                root.Array[7] = point; root.List[7] = point; root.Dictionary[7] = point;
            }
            public static void CheckUnused(object value) {
                if (((Unused)value).Deep.Point.X != X(4, false)) { throw new InvalidOperationException("Untouched deep A value changed."); }
            }
            public static void Check(object value, bool changed) {
                var root = (Root)value;
                if (!ReferenceEquals(root, root.Self) || !ReferenceEquals(root.Array, root.ArrayAlias) ||
                    !ReferenceEquals(root.List, root.ListAlias) || !ReferenceEquals(root.Dictionary, root.DictionaryAlias) ||
                    root.Array.Length != 128 || root.List.Count != 128 || root.Dictionary.Count != 128) {
                    throw new InvalidOperationException("Root cycle, container aliases or counts differ.");
                }
                for (int i = 0; i < 128; i++) {
                    if (root.Array[i].X != X(i, changed) || root.List[i].X != X(i, changed) || root.Dictionary[i].X != X(i, changed)) {
                        throw new InvalidOperationException("Actual inline container values differ.");
                    }
                }
            }
        }
        """;
}
