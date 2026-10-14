using System.Reflection;
using Atelia.DurableGraph.Build;
using Atelia.DurableGraph.Persistence;

using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using Atelia.VersionStore;
using Frames = Atelia.FrameStore.FrameStore;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    private const long PrivateWidgetValue = 1L << 40;
    private static readonly DurableSchema PrivateExtraSchema = new("private.event-extra", 1, new DurableFieldInfo(1, TypeTag.Byte));

    [Fact]
    public void GeneratedPrivateUpgradeForksCompleteBaselineViewAndColdEventStateContinuesWithoutLatestFallback() {
        var (a, b) = CompilePrivateSchemaFixtures();
        using RawBaseDirectory directory = new();
        FrameAddress aState, sourceShared, bState, bSchema, latestShared, eventAddress, eventSchema, continued;
        ObjectId unusedDescriptor, bRoot, bList;
        ObjectStorageInfo bListStorage;
        PrivateFrameImage[] originalFrames;
        PrivateRefImage sharedBeforePrivate, sharedAfterAdvance;

        using (Repository repository = Repository.CreateNew(directory.Path, a.Models(), schemaWriteMode: SchemaWriteMode.Shared)) {
            // This accepted declaration is never reachable from either Widget business graph.
            using (BranchCheckout unused = repository.CreateBranch("unused", a.CreateUnused())) { }
            using BranchCheckout main = repository.CreateBranch("main", a.Create());
            a.Check(main.State!, 1, 7, 8);
            aState = main.Head.RevisionAddress;
            repository.CreateTag("a-initial", main.Head);
            sourceShared = repository.SharedSchemaRoot!.Value;
            SchemaCatalogView catalog = SchemaCatalogView.Load(ReferenceGraphStore(repository), sourceShared);
            Assert.Equal(2, catalog.Schemas.Count);
            unusedDescriptor = catalog.SchemaNodeId(a.UnusedSchema());
            Assert.Equal(a.WidgetSchema(), catalog.Schemas[new(a.WidgetSchema().Type, 1)]);
            sharedBeforePrivate = PrivateReadRef(repository, 2);
            originalFrames = PrivateReadFrames(PrivateResources(repository).Data);
        }

        using (Repository repository = Repository.OpenExisting(directory.Path, b.Models())) {
            Assert.Null(repository.SchemaWriteMode);
            repository.CreateBranch("private", repository.ResolveTag("a-initial"));
            using BranchCheckout branch = repository.CheckoutPrivate("private");
            branch.CommitState();
            b.Check(branch.State!, PrivateWidgetValue, 7, 8);
            bState = branch.Head.RevisionAddress;
            bRoot = branch.StateId!.Value;
            repository.CreateTag("b-initial", branch.Head);
            GraphRecordStore store = ReferenceGraphStore(repository);
            StoredGraphRevision graph = store.ReadGraph(bState);
            bSchema = graph.SchemaRoot!.Value;
            Assert.NotEqual(sourceShared, bSchema);
            Assert.Equal(aState, graph.Parent);
            Assert.Equal(aState, graph.SourceGraph);
            Assert.Equal(store.ReadGraph(aState).RootId, graph.RootId);
            Assert.Equal(3, graph.ObjectHeads.Count);
            Assert.Equal(2, DefaultRecords(store, bState).Count);
            Assert.All(DefaultRecords(store, bState), record => {
                Assert.Equal(ObjectVersionKind.Base, record.Kind);
                Assert.Null(record.PriorAddress);
                Assert.Equal(graph.OwnedBatch, graph.ObjectHeads[record.ObjectId]);
                Assert.Single(store.ReadObjectChain(bState, record.ObjectId).Entries);
            });
            HashSet<FrameAddress> oldBusinessHeads = store.ReadGraph(aState).ObjectHeads.Values.ToHashSet();
            Assert.Single(graph.ObjectHeads, row => oldBusinessHeads.Contains(row.Value));
            Assert.Equal(2, graph.ObjectHeads.Keys.Count(id => DefaultLayout(store, bState, id).Schema?.Type == b.WidgetSchema().Type));
            SchemaCatalogView privateView = SchemaCatalogView.Load(store, bSchema);
            SchemaCatalogView originalView = SchemaCatalogView.Load(store, sourceShared);
            Assert.Equal(3, privateView.Schemas.Count);
            Assert.Equal(b.WidgetSchema(), privateView.Schemas[new(b.WidgetSchema().Type, 2)]);
            Assert.Equal(a.WidgetSchema(), originalView.Schemas[new(a.WidgetSchema().Type, 1)]);
            Assert.NotEqual(originalView.SchemaNodeId(a.WidgetSchema()), privateView.SchemaNodeId(b.WidgetSchema()));
            Assert.Equal(a.UnusedSchema(), privateView.Schemas[new(a.UnusedSchema().Type, 1)]);
            Assert.Equal(unusedDescriptor, privateView.SchemaNodeId(a.UnusedSchema()));
            StoredGraphRevision meta = store.ReadGraph(bSchema);
            Assert.Equal(sourceShared, meta.Parent);
            Assert.Equal(sourceShared, meta.SourceGraph);
            bList = Assert.Single(branch.Baseline!.Objects, row => row.Value.Current.Kind == ObjectStateKind.List).Key;
            bListStorage = branch.Baseline!.Objects[bList].Storage!.Value;
            Assert.Equal(sourceShared, repository.SharedSchemaRoot);
            PrivateAssertSameRef(sharedBeforePrivate, PrivateReadRef(repository, 2));
            PrivateAssertFramesPreserved(PrivateResources(repository).Data, originalFrames);
        }

        using (Repository repository = Repository.OpenExisting(directory.Path, a.Models(), schemaWriteMode: SchemaWriteMode.Shared)) {
            using (BranchCheckout later = repository.CreateBranch("later", a.CreateLater())) { }
            latestShared = repository.SharedSchemaRoot!.Value;
            Assert.NotEqual(sourceShared, latestShared);
            using BranchCheckout main = repository.Checkout("main");
            a.Check(main.State!, 1, 7, 8);
            a.ChangeList(main.State!, 8, 202);
            FrameAddress saved = main.CommitState().RevisionAddress;
            ObjectVersionRecord delta = Assert.Single(DefaultRecords(ReferenceGraphStore(repository), saved));
            Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
            a.Check(repository.ReadState(repository.GetHead("main")), 1, 7, 202);
            a.Check(repository.ReadState(repository.ResolveTag("a-initial")), 1, 7, 8);
            Assert.Equal(bState, repository.GetHead("private").RevisionAddress);
            sharedAfterAdvance = PrivateReadRef(repository, 2);
        }

        using (Repository repository = Repository.OpenExisting(directory.Path, b.Models())) {
            using BranchCheckout branch = repository.CheckoutPrivate("private");
            b.Check(branch.State!, PrivateWidgetValue, 7, 8);
            Assert.Equal(bState, branch.StateRevisionAddress);
            Assert.Equal(bRoot, branch.StateId);
            Assert.Equal(bSchema, branch.Catalog.Accepted!.Snapshot!.Address);
            Assert.Equal(bSchema, branch.BindingEnvironment!.SelectedSchemaView.Snapshot!.Address);
            Assert.Equal(bListStorage, branch.Baseline!.Objects[bList].Storage);
            Assert.False(branch.Catalog.Accepted!.Schemas.ContainsKey(new(a.LaterSchema().Type, 1)));
            Assert.True(SchemaCatalogView.Load(ReferenceGraphStore(repository), latestShared).Schemas.ContainsKey(new(a.LaterSchema().Type, 1)));
            NormalizedGraphState baseline = branch.Baseline!;
            IDurableObject state = branch.State!;
            SchemaCatalogView accepted = branch.Catalog.Accepted!;
            CheckpointAddress head = branch.Head;
            PrivatePublicationImage beforeDiscard = PrivatePublication(repository);
            branch.Catalog.Set(PrivateExtraSchema);
            branch.PrepareAndDiscard(state);
            Assert.Same(baseline, branch.Baseline);
            Assert.Same(accepted, branch.Catalog.Accepted);
            Assert.Same(state, branch.State);
            Assert.Equal(head, branch.Head);
            Assert.Equal(1, branch.Catalog.DirtyCount);
            PrivateAssertPublicationUnchanged(repository, beforeDiscard);

            eventAddress = branch.CommitEvent(state).RevisionAddress;
            StoredGraphRevision domainEvent = ReferenceGraphStore(repository).ReadGraph(eventAddress);
            eventSchema = domainEvent.SchemaRoot!.Value;
            Assert.NotEqual(bSchema, eventSchema);
            Assert.Equal(StoredGraphKind.Event, domainEvent.Kind);
            Assert.Equal(bState, domainEvent.Parent);
            Assert.Equal(bState, domainEvent.SourceGraph);
            Assert.Equal(eventSchema, branch.Catalog.Accepted!.Snapshot!.Address);
            Assert.Equal(PrivateExtraSchema, branch.Catalog.Accepted!.Schemas[new(PrivateExtraSchema.Type, 1)]);
            Assert.Equal(0, branch.Catalog.DirtyCount);
            Assert.Same(baseline, branch.Baseline);
            Assert.Same(state, branch.State);
            Assert.Equal(bRoot, branch.StateId);
            Assert.Equal(bState, branch.StateRevisionAddress);
            Assert.Equal(bSchema, branch.BindingEnvironment!.SelectedSchemaView.Snapshot!.Address);
            PrivateAssertSameRef(sharedAfterAdvance, PrivateReadRef(repository, 2));
            Assert.Equal(latestShared, repository.SharedSchemaRoot);
            PrivateAssertFramesPreserved(PrivateResources(repository).Data, originalFrames);
        }

        // All Event resources are closed. Its meta view is the next private write
        // source, while execution and incremental State content still belong to S0.
        using (Repository repository = Repository.OpenExisting(directory.Path, b.Models())) {
            using BranchCheckout branch = repository.CheckoutPrivate("private");
            b.Check(branch.State!, PrivateWidgetValue, 7, 8);
            Assert.Equal(eventAddress, branch.Head.RevisionAddress);
            Assert.Equal(bState, branch.StateRevisionAddress);
            Assert.Equal(eventSchema, branch.Catalog.Accepted!.Snapshot!.Address);
            Assert.Equal(bSchema, branch.BindingEnvironment!.SelectedSchemaView.Snapshot!.Address);
            Assert.False(branch.Catalog.Accepted!.Schemas.ContainsKey(new(a.LaterSchema().Type, 1)));
            Assert.Equal(PrivateExtraSchema, branch.Catalog.Accepted!.Schemas[new(PrivateExtraSchema.Type, 1)]);
            Assert.Equal(unusedDescriptor, branch.Catalog.Accepted!.SchemaNodeId(a.UnusedSchema()));
            b.ChangeList(branch.State!, 7, 201);
            continued = branch.CommitState().RevisionAddress;
            GraphRecordStore store = ReferenceGraphStore(repository);
            StoredGraphRevision graph = store.ReadGraph(continued);
            Assert.Equal(eventAddress, graph.Parent);
            Assert.Equal(bState, graph.SourceGraph);
            Assert.Equal(eventSchema, graph.SchemaRoot);
            Assert.Equal(bRoot, graph.RootId);
            ObjectVersionRecord delta = Assert.Single(DefaultRecords(store, continued));
            Assert.Equal(bList.Value, delta.ObjectId);
            Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
            Assert.Equal(bListStorage.Head, delta.PriorAddress);
            ObjectVersionChain chain = store.ReadObjectChain(continued, bList.Value);
            Assert.Equal(bListStorage.ReconstructionPayloadBytes + chain.Entries[^1].PayloadBytes,
                branch.Baseline!.Objects[bList].Storage!.Value.ReconstructionPayloadBytes);
            Assert.Equal(chain.ReconstructionPayloadBytes, branch.Baseline!.Objects[bList].Storage!.Value.ReconstructionPayloadBytes);
            Assert.Equal(store.ReadGraph(bState).ObjectHeads.Keys, graph.ObjectHeads.Keys);
            foreach (uint id in graph.ObjectHeads.Keys.Where(id => id != bList.Value)) {
                Assert.Equal(store.ReadGraph(bState).ObjectHeads[id], graph.ObjectHeads[id]);
            }
            Assert.Equal(eventSchema, branch.Catalog.Accepted!.Snapshot!.Address);
            Assert.Equal(latestShared, repository.SharedSchemaRoot);
            PrivateAssertSameRef(sharedAfterAdvance, PrivateReadRef(repository, 2));
            b.Check(repository.ReadState(repository.GetHead("private")), PrivateWidgetValue, 201, 8);
            b.Check(repository.ReadState(repository.ResolveTag("b-initial")), PrivateWidgetValue, 7, 8);
            PrivateAssertFramesPreserved(PrivateResources(repository).Data, originalFrames);
        }

        Dictionary<string, byte[]> files = PrivateFiles(directory.Path);
        using (Repository reader = Repository.OpenReadOnlyExisting(directory.Path, b.Models())) {
            b.Check(reader.ReadState(reader.GetHead("private")), PrivateWidgetValue, 201, 8);
            b.Check(reader.ReadState(reader.ResolveTag("b-initial")), PrivateWidgetValue, 7, 8);
            CheckpointAddress selectedEvent = Assert.Single(reader.ReadFrames("private"), frame => frame.RevisionAddress == eventAddress);
            b.Check(reader.ReadEvent(selectedEvent), PrivateWidgetValue, 7, 8);
            Assert.Throws<InvalidOperationException>(() => reader.CheckoutPrivate("private"));
            PrivateAssertSameRef(sharedAfterAdvance, PrivateReadRef(reader, 2));
            Assert.Equal(continued, reader.GetHead("private").RevisionAddress);
        }
        using (Repository reader = Repository.OpenReadOnlyExisting(directory.Path, a.Models())) {
            a.Check(reader.ReadState(reader.GetHead("main")), 1, 7, 202);
            a.Check(reader.ReadState(reader.ResolveTag("a-initial")), 1, 7, 8);
            Assert.Equal(a.WidgetSchema(), DefaultLayout(ReferenceGraphStore(reader), aState,
                ReferenceGraphStore(reader).ReadGraph(aState).RootId.Value).Schema);
            PrivateAssertFramesPreserved(PrivateResources(reader).Data, originalFrames);
        }
        PrivateAssertFilesUnchanged(files, PrivateFiles(directory.Path));
    }

    private static (PrivateSchemaFixture A, PrivateSchemaFixture B) CompilePrivateSchemaFixtures() {
        using AncestryHistoryDirectory history = new();
        SchemaHistoryTool tool = new();
        GeneratorTestRun runA = RunGenerator(PrivateSchemaSource(wide: false), [], null, "true");
        AssertSchemaOnlyCompiles(runA);
        tool.Publish(history.WriteManifest(runA), history.History);
        GeneratorTestRun runB = RunGenerator(PrivateSchemaSource(wide: true), history.ReadAdditionalTexts(), null, "true");
        AssertSchemaOnlyCompiles(runB);
        PrivateSchemaFixture a = new(EmitAndLoad(runA.OutputCompilation)), b = new(EmitAndLoad(runB.OutputCompilation));
        Assert.Equal(1, a.WidgetSchema().Version);
        Assert.Equal(2, b.WidgetSchema().Version);
        return (a, b);
    }

    private sealed class PrivateSchemaFixture(Assembly assembly) {
        private readonly Type _host = assembly.GetType("PrivateSchemaStory.Host")!;
        internal StateModelRegistry Models() => _host.GetMethod("Models")!.CreateDelegate<Func<StateModelRegistry>>()();
        internal IDurableObject Create() => _host.GetMethod("Create")!.CreateDelegate<Func<IDurableObject>>()();
        internal IDurableObject CreateUnused() => _host.GetMethod("CreateUnused")!.CreateDelegate<Func<IDurableObject>>()();
        internal IDurableObject CreateLater() => _host.GetMethod("CreateLater")!.CreateDelegate<Func<IDurableObject>>()();
        internal DurableSchema[] CurrentSchemas() => _host.GetMethod("ExportSchemas")!
            .CreateDelegate<Func<StateBindingContext, DurableSchema[]>>()(Models().CreateEnvironment());
        internal DurableSchema WidgetSchema() => Assert.Single(CurrentSchemas());
        internal DurableSchema UnusedSchema() => _host.GetMethod("UnusedSchema")!
            .CreateDelegate<Func<StateBindingContext, DurableSchema>>()(Models().CreateEnvironment());
        internal DurableSchema LaterSchema() => _host.GetMethod("LaterSchema")!
            .CreateDelegate<Func<StateBindingContext, DurableSchema>>()(Models().CreateEnvironment());
        internal void Check(object root, long value, int seven, int eight) =>
            _host.GetMethod("Check")!.CreateDelegate<Action<object, long, int, int>>()(root, value, seven, eight);
        internal void ChangeList(object root, int index, int value) =>
            _host.GetMethod("ChangeList")!.CreateDelegate<Action<object, int, int>>()(root, index, value);
    }

    private static string PrivateSchemaSource(bool wide) => $$"""
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Persistence;
        using Atelia.DurableGraph.Runtime;
        using Atelia.DurableGraph.Schema;
        using W = global::Atelia.DurableGraph.Generated.Family_707269766174652E776964676574;
        using U = global::Atelia.DurableGraph.Generated.Family_707269766174652E756E75736564;
        using L = global::Atelia.DurableGraph.Generated.Family_707269766174652E6C61746572;
        namespace PrivateSchemaStory;
        [DurableType("private.widget", {{(wide ? "2" : "1")}})]
        public sealed partial class Widget : IDurableObject {
            [DurableField(1)] public {{(wide ? "long" : "int")}} Value;
            [DurableField(2)] public List<int> Numbers = new();
            [DurableField(3)] public Widget? Child;
            {{(wide ? "private static void UpgradeStateV1ToV2(in W.V1 prior, out W.V2 next) => next = new((long)prior.Segment0Field1 + (1L << 40) - 1, prior.Segment0Field2, prior.Segment0Field3);" : "")}}
        }
        [DurableType("private.unused", 1)]
        public sealed partial class Unused : IDurableObject { [DurableField(1)] public byte Value; }
        [DurableType("private.later", 1)]
        public sealed partial class Later : IDurableObject { [DurableField(1)] public byte Value; }
        public static class Host {
            public static StateModelRegistry Models() {
                var models = new StateModelRegistry();
                models.Register(W.Definition);
                {{(wide ? "" : "models.Register(U.Definition); models.Register(L.Definition);")}}
                return models;
            }
            public static DurableSchema[] ExportSchemas(StateBindingContext context) => new[] { context.ResolveCurrentModel(typeof(Widget)).CurrentSchema };
            public static DurableSchema UnusedSchema(StateBindingContext context) => context.ResolveCurrentModel(typeof(Unused)).CurrentSchema;
            public static DurableSchema LaterSchema(StateBindingContext context) => context.ResolveCurrentModel(typeof(Later)).CurrentSchema;
            public static IDurableObject CreateUnused() => new Unused { Value = 9 };
            public static IDurableObject CreateLater() => new Later { Value = 10 };
            public static IDurableObject Create() {
                var numbers = Enumerable.Range(0, 128).ToList();
                var root = new Widget { Value = {{(wide ? "1L << 40" : "1")}}, Numbers = numbers };
                root.Child = new Widget { Value = root.Value + 1, Numbers = numbers, Child = root };
                return root;
            }
            public static void ChangeList(object value, int index, int next) => ((Widget)value).Numbers[index] = next;
            public static void Check(object value, long expected, int seven, int eight) {
                var root = (Widget)value;
                if (root.Value != expected || root.Child is not { } child || child.Value != expected + 1 ||
                    !ReferenceEquals(root, child.Child) || !ReferenceEquals(root.Numbers, child.Numbers) || root.Numbers.Count != 128) {
                    throw new InvalidOperationException("The generated Widget values, cycle or shared List differ.");
                }
                for (int i = 0; i < 128; i++) {
                    if (root.Numbers[i] != (i == 7 ? seven : i == 8 ? eight : i)) {
                        throw new InvalidOperationException("The generated List contents differ.");
                    }
                }
            }
        }
        """;

    private sealed record PrivateFrameImage(FrameAddress Address, uint Tag, int TailMetaLength, bool Tombstone, byte[] Bytes);
    private sealed record PrivateRefImage(RefRevision Revision, byte[] Bytes);
    private sealed record PrivatePublicationImage(PrivateRefImage Branches, PrivateRefImage Shared, PrivateFrameImage[] Frames, long Appended);
    private static RepositoryResources PrivateResources(Repository repository) =>
        (RepositoryResources)typeof(Repository).GetField("_resources", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(repository)!;
    private static PrivateRefImage PrivateReadRef(Repository repository, uint id) {
        RefBytesSnapshot actual = PrivateResources(repository).Versions.ReadRef(id).Unwrap();
        return new(actual.Revision, actual.Payload.ToArray());
    }
    private static void PrivateAssertSameRef(PrivateRefImage expected, PrivateRefImage actual) {
        Assert.Equal(expected.Revision, actual.Revision);
        Assert.Equal(expected.Bytes, actual.Bytes);
    }
    private static PrivatePublicationImage PrivatePublication(Repository repository) =>
        new(PrivateReadRef(repository, 1), PrivateReadRef(repository, 2), PrivateReadFrames(PrivateResources(repository).Data),
            ReferenceGraphStore(repository).AppendedFrames);
    private static void PrivateAssertPublicationUnchanged(Repository repository, PrivatePublicationImage before) {
        PrivateAssertSameRef(before.Branches, PrivateReadRef(repository, 1));
        PrivateAssertSameRef(before.Shared, PrivateReadRef(repository, 2));
        PrivateFrameImage[] after = PrivateReadFrames(PrivateResources(repository).Data);
        Assert.Equal(before.Frames.Select(frame => frame.Address), after.Select(frame => frame.Address));
        PrivateAssertFrameImages(before.Frames, after);
        Assert.Equal(before.Appended, ReferenceGraphStore(repository).AppendedFrames);
        Assert.False(PrivateResources(repository).HasPendingSave);
        Assert.False(repository.IsFaulted);
    }
    private static PrivateFrameImage[] PrivateReadFrames(Frames data) {
        List<FrameInfo> inventory = [];
        data.Inventory(inventory.Add).Unwrap();
        return inventory.Select(info => {
            using FrameRead read = data.ReadFrame(info.Address).Unwrap();
            return new PrivateFrameImage(info.Address, read.Tag, read.TailMetaLength, read.IsTombstone, read.PayloadAndMeta.ToArray());
        }).ToArray();
    }
    private static void PrivateAssertFramesPreserved(Frames data, PrivateFrameImage[] expected) =>
        PrivateAssertFrameImages(expected, PrivateReadFrames(data));
    private static void PrivateAssertFrameImages(PrivateFrameImage[] expected, PrivateFrameImage[] actual) {
        foreach (PrivateFrameImage old in expected) {
            PrivateFrameImage current = Assert.Single(actual, frame => frame.Address == old.Address);
            Assert.Equal(old.Tag, current.Tag);
            Assert.Equal(old.TailMetaLength, current.TailMetaLength);
            Assert.Equal(old.Tombstone, current.Tombstone);
            Assert.Equal(old.Bytes, current.Bytes);
        }
    }
    private static Dictionary<string, byte[]> PrivateFiles(string path) => Directory.GetFiles(path, "*", SearchOption.AllDirectories)
        .ToDictionary(file => file, File.ReadAllBytes);
    private static void PrivateAssertFilesUnchanged(Dictionary<string, byte[]> expected, Dictionary<string, byte[]> actual) {
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach ((string file, byte[] bytes) in expected) { Assert.Equal(bytes, actual[file]); }
    }
}
