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
    public void GeneratedPrivateHistoryPromotionPreservesFullDeclarationsOriginsAndPendingStateRewrite() {
        var (v1, v2) = CompilePrivatePromotionFixtures();
        using RawBaseDirectory directory = new();
        FrameAddress privateState, privateSchema, firstSharedState, firstSharedSchema, promotedEvent, promotedState, promotedSchema, continued;
        ObjectId rootId, listId, privateDescriptor;
        ObjectStorageInfo oldList;
        ObjectLayout unreachableList;
        IReadOnlyDictionary<uint, FrameAddress> privateHeads;
        PrivateFrameImage[] initialFrames;

        using (Repository repository = Repository.CreateNew(directory.Path, v1.Models(), schemaWriteMode: SchemaWriteMode.Shared)) {
            using BranchCheckout origin = repository.CreateBranch("origin", v1.CreateSharedOrigin());
            repository.CreateTag("baseline", origin.Head);
        }
        using (Repository repository = Repository.OpenExisting(directory.Path, v1.Models())) {
            // The Holder and its dynamic List are real live objects in the first
            // save. Replacing the business root leaves their declarations in S.
            repository.CreateBranch("private", repository.ResolveTag("baseline"));
            using BranchCheckout branch = repository.CheckoutPrivate("private");
            branch.CommitState(v1.CreateHolder());
            FrameAddress holderState = branch.Head.RevisionAddress;
            P0GraphStore store = ReferenceGraphStore(repository);
            unreachableList = Assert.Single(store.ReadGraph(holderState).Objects.Keys
                .Select(id => DefaultLayout(store, holderState, id)), layout =>
                layout.List is { } list && list.ElementSlot.TypeTag == TypeTag.ObjectReference);
            IDurableObject widget = v1.WidgetFromHolder(branch.State!);
            privateState = branch.CommitState(widget).RevisionAddress;
            v1.Check(branch.State!, 1, 7);
            Assert.Equal(3, store.ReadGraph(privateState).Objects.Count);
            Assert.Equal(2, DefaultRemoved(store, privateState).Count);
            Assert.Empty(DefaultRecords(store, privateState));
            privateSchema = store.ReadGraph(privateState).SchemaRoot!.Value;
            P0CatalogView catalog = P0CatalogView.Load(store, privateSchema);
            Assert.Equal(v1.Schema("Holder"), catalog.Schemas[new(v1.Schema("Holder").Type, 1)]);
            Assert.Equal(unreachableList, catalog.ResolveLayout(catalog.DescriptorId(unreachableList)));
            privateDescriptor = catalog.SchemaNodeId(v1.Schema("Widget"));
            rootId = branch.StateId!.Value;
            listId = Assert.Single(branch.Baseline!.Objects, row => row.Value.Current.Kind == ObjectStateKind.List).Key;
            oldList = branch.Baseline!.Objects[listId].Storage!.Value;
            privateHeads = store.ReadGraph(privateState).Objects;
            repository.CreateTag("private-v1", branch.Head);
            initialFrames = PrivateReadFrames(PrivateResources(repository).Data);
        }

        // Both independent successors start from the same shared origin. Late
        // uses Widget's three-field shape but another nominal identity, so these
        // real catalogs deliberately reuse a number for different descriptors.
        using (Repository repository = Repository.OpenExisting(directory.Path, v1.Models(), schemaWriteMode: SchemaWriteMode.Shared)) {
            using BranchCheckout late = repository.CreateBranch("late", v1.CreateLate());
            P0CatalogView latest = P0CatalogView.Load(ReferenceGraphStore(repository), repository.SharedSchemaRoot!.Value);
            Assert.Equal(privateDescriptor, latest.SchemaNodeId(v1.Schema("Late")));
            Assert.False(latest.Schemas.ContainsKey(new(v1.Schema("Widget").Type, 1)));
        }

        using (Repository repository = Repository.OpenExisting(directory.Path, v1.Models())) {
            using BranchCheckout branch = repository.CheckoutPrivate("private");
            Assert.Equal(0, PromotionRewriteCount(branch));
            firstSharedState = branch.CommitStateToShared().RevisionAddress;
            P0GraphStore store = ReferenceGraphStore(repository);
            P0GraphSnapshot graph = store.ReadGraph(firstSharedState);
            firstSharedSchema = graph.SchemaRoot!.Value;
            Assert.Equal(repository.SharedSchemaRoot, firstSharedSchema);
            Assert.Equal(privateState, graph.SourceGraph);
            Assert.Null(graph.OwnedBatch);
            Assert.Empty(DefaultRecords(store, firstSharedState));
            Assert.Equal(privateHeads.Keys, graph.Objects.Keys);
            foreach (var row in privateHeads) { Assert.Equal(row.Value, graph.Objects[row.Key]); }
            P0CatalogView imported = P0CatalogView.Load(store, firstSharedSchema);
            Assert.NotEqual(privateDescriptor, imported.SchemaNodeId(v1.Schema("Widget")));
            Assert.Equal(v1.Schema("Holder"), imported.Schemas[new(v1.Schema("Holder").Type, 1)]);
            Assert.Equal(unreachableList, imported.ResolveLayout(imported.DescriptorId(unreachableList)));
            Assert.Equal(v1.Schema("Late"), imported.Schemas[new(v1.Schema("Late").Type, 1)]);
            Assert.Equal(privateSchema, store.ReadObjectChain(firstSharedState, rootId.Value).Entries[0].SchemaRoot);
            Assert.Equal(v1.Schema("Widget"), DefaultLayout(store, firstSharedState, rootId.Value).Schema);
            Assert.Equal(oldList, branch.Baseline!.Objects[listId].Storage);
            v1.Check(repository.ReadState(branch.Head), 1, 7);
            PrivateAssertFramesPreserved(PrivateResources(repository).Data, initialFrames);
        }

        v2.ResetUpgrades();
        using (Repository repository = Repository.OpenExisting(directory.Path, v2.Models())) {
            using BranchCheckout branch = repository.CheckoutPrivate("private");
            v2.Check(branch.State!, 101, 7);
            Assert.Equal(2, v2.Upgrades());
            Assert.Equal(2, PromotionRewriteCount(branch));
            NormalizedRevision baseline = branch.Baseline!;
            IDurableObject state = branch.State!;
            Assert.Equal(firstSharedState, branch.StateRevisionAddress);
            Assert.Equal(rootId, branch.StateId);
            Assert.Equal(firstSharedSchema, branch.BindingEnvironment!.SelectedSchemaView.Snapshot!.Address);
            promotedEvent = branch.CommitEventToShared(state).RevisionAddress;
            P0GraphStore store = ReferenceGraphStore(repository);
            P0GraphSnapshot domainEvent = store.ReadGraph(promotedEvent);
            promotedSchema = domainEvent.SchemaRoot!.Value;
            Assert.NotEqual(firstSharedSchema, promotedSchema);
            Assert.Equal(P0GraphKind.Event, domainEvent.Kind);
            Assert.Equal(firstSharedState, domainEvent.Parent);
            Assert.Equal(firstSharedState, domainEvent.SourceGraph);
            Assert.Equal(repository.SharedSchemaRoot, promotedSchema);
            Assert.Same(baseline, branch.Baseline);
            Assert.Same(state, branch.State);
            Assert.Equal(firstSharedState, branch.StateRevisionAddress);
            Assert.Equal(rootId, branch.StateId);
            Assert.Equal(2, PromotionRewriteCount(branch));
            Assert.Equal(firstSharedSchema, branch.BindingEnvironment!.SelectedSchemaView.Snapshot!.Address);
            Assert.Equal(promotedSchema, branch.Catalog.Accepted!.Snapshot!.Address);
            P0CatalogView complete = P0CatalogView.Load(store, promotedSchema);
            Assert.Equal(v1.Schema("Widget"), complete.Schemas[new(v1.Schema("Widget").Type, 1)]);
            Assert.Equal(v2.Schema("Widget"), complete.Schemas[new(v2.Schema("Widget").Type, 2)]);
            Assert.Equal(v1.Schema("Holder"), complete.Schemas[new(v1.Schema("Holder").Type, 1)]);
            Assert.Equal(unreachableList, complete.ResolveLayout(complete.DescriptorId(unreachableList)));
            Assert.All(DefaultRecords(store, promotedEvent), row => Assert.Equal(ObjectVersionKind.Base, row.Kind));
            Assert.Equal(2, DefaultRecords(store, promotedEvent).Count);
            Assert.Equal(oldList.Head, domainEvent.Objects[listId.Value]);

            PrivatePublicationImage beforeDiscard = PrivatePublication(repository);
            branch.PrepareAndDiscard(state, toShared: true);
            Assert.Same(baseline, branch.Baseline);
            Assert.Same(state, branch.State);
            Assert.Equal(promotedEvent, branch.Head.RevisionAddress);
            Assert.Equal(2, PromotionRewriteCount(branch));
            PrivateAssertPublicationUnchanged(repository, beforeDiscard);

            PrivateRefImage sharedBeforeState = PrivateReadRef(repository, 2);
            promotedState = branch.CommitStateToShared().RevisionAddress;
            P0GraphSnapshot saved = store.ReadGraph(promotedState);
            Assert.Equal(promotedEvent, saved.Parent);
            Assert.Equal(firstSharedState, saved.SourceGraph);
            Assert.Equal(promotedSchema, saved.SchemaRoot);
            Assert.Equal(rootId, saved.RootId);
            Assert.Equal(privateHeads.Keys, saved.Objects.Keys);
            ObjectVersionRecord[] rewritten = DefaultRecords(store, promotedState).ToArray();
            Assert.Equal(2, rewritten.Length);
            Assert.All(rewritten, row => {
                Assert.Equal(ObjectVersionKind.Base, row.Kind);
                Assert.Null(row.PriorAddress);
                P0ObjectChain chain = store.ReadObjectChain(promotedState, row.ObjectId);
                Assert.Single(chain.Entries);
                Assert.Equal(v2.Schema("Widget"), DefaultLayout(store, promotedState, row.ObjectId).Schema);
                Assert.Equal(chain.ReconstructionPayloadBytes, branch.Baseline!.Objects[new(row.ObjectId)].Storage!.Value.ReconstructionPayloadBytes);
            });
            Assert.Equal(oldList, branch.Baseline!.Objects[listId].Storage);
            Assert.Equal(oldList.Head, saved.Objects[listId.Value]);
            Assert.Equal(privateSchema, store.ReadObjectChain(promotedState, listId.Value).Entries[0].SchemaRoot);
            Assert.Equal(0, PromotionRewriteCount(branch));
            Assert.Equal(firstSharedSchema, branch.BindingEnvironment!.SelectedSchemaView.Snapshot!.Address);
            PrivateAssertSameRef(sharedBeforeState, PrivateReadRef(repository, 2));
            v2.Check(repository.ReadState(branch.Head), 101, 7);
            PrivateAssertFramesPreserved(PrivateResources(repository).Data, initialFrames);

            // The explicit shared operation did not change this checkout's
            // default destination or frozen execution models. New ordinary
            // private intent advances only its graph pin, not shared ref2.
            branch.Catalog.Set(v2.Schema("PrivateOnly"));
            branch.CommitState();
            Assert.NotEqual(promotedSchema, branch.Catalog.Accepted!.Snapshot!.Address);
            Assert.Equal(promotedSchema, repository.SharedSchemaRoot);
            PrivateAssertSameRef(sharedBeforeState, PrivateReadRef(repository, 2));
        }

        v2.ResetUpgrades();
        using (Repository repository = Repository.OpenExisting(directory.Path, v2.Models())) {
            using BranchCheckout branch = repository.CheckoutPrivate("private");
            v2.Check(branch.State!, 101, 7);
            Assert.Equal(0, v2.Upgrades());
            Assert.Equal(0, PromotionRewriteCount(branch));
            Assert.Equal(v2.Schema("PrivateOnly"), branch.Catalog.Accepted!.Schemas[new(v2.Schema("PrivateOnly").Type, 1)]);
            PrivateRefImage sharedBefore = PrivateReadRef(repository, 2);
            ObjectStorageInfo prior = branch.Baseline!.Objects[listId].Storage!.Value;
            IReadOnlyDictionary<uint, FrameAddress> previousHeads = ReferenceGraphStore(repository).ReadGraph(branch.StateRevisionAddress!.Value).Objects;
            v2.ChangeList(branch.State!, 7, 201);
            continued = branch.CommitState().RevisionAddress;
            P0GraphStore store = ReferenceGraphStore(repository);
            P0GraphSnapshot graph = store.ReadGraph(continued);
            ObjectVersionRecord delta = Assert.Single(DefaultRecords(store, continued));
            Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
            Assert.Equal(listId.Value, delta.ObjectId);
            Assert.Equal(prior.Head, delta.PriorAddress);
            P0ObjectChain chain = store.ReadObjectChain(continued, listId.Value);
            Assert.Equal(prior.ReconstructionPayloadBytes + chain.Entries[^1].PayloadBytes, chain.ReconstructionPayloadBytes);
            Assert.Equal(chain.ReconstructionPayloadBytes, branch.Baseline!.Objects[listId].Storage!.Value.ReconstructionPayloadBytes);
            foreach (var row in previousHeads.Where(row => row.Key != listId.Value)) { Assert.Equal(row.Value, graph.Objects[row.Key]); }
            Assert.Equal(promotedSchema, repository.SharedSchemaRoot);
            PrivateAssertSameRef(sharedBefore, PrivateReadRef(repository, 2));
            v2.Check(repository.ReadState(branch.Head), 101, 201);
        }

        Dictionary<string, byte[]> files = PrivateFiles(directory.Path);
        using (Repository reader = Repository.OpenReadOnlyExisting(directory.Path, v1.Models())) {
            v1.Check(reader.ReadState(reader.ResolveTag("private-v1")), 1, 7);
            v1.Check(reader.ReadState(reader.GetHead("late")), 91, 7, late: true);
        }
        using (Repository reader = Repository.OpenReadOnlyExisting(directory.Path, v2.Models())) {
            v2.Check(reader.ReadState(reader.GetHead("private")), 101, 201);
            v2.Check(reader.ReadEvent(Assert.Single(reader.ReadFrames("private"), frame => frame.RevisionAddress == promotedEvent)), 101, 7);
            Assert.Equal(continued, reader.GetHead("private").RevisionAddress);
            Assert.Equal(promotedSchema, reader.SharedSchemaRoot);
            P0CatalogView shared = P0CatalogView.Load(ReferenceGraphStore(reader), promotedSchema);
            Assert.Equal(unreachableList, shared.ResolveLayout(shared.DescriptorId(unreachableList)));
            Assert.Equal(v1.Schema("Holder"), shared.Schemas[new(v1.Schema("Holder").Type, 1)]);
            Assert.False(shared.Schemas.ContainsKey(new(v2.Schema("PrivateOnly").Type, 1)));
            PrivateAssertFramesPreserved(PrivateResources(reader).Data, initialFrames);
        }
        PrivateAssertFilesUnchanged(files, PrivateFiles(directory.Path));
    }

    private static int PromotionRewriteCount(BranchCheckout branch) => branch.Baseline!.Objects.Values.Count(row => row.RequiresRewrite);

    private static (PrivatePromotionFixture V1, PrivatePromotionFixture V2) CompilePrivatePromotionFixtures() {
        using AncestryHistoryDirectory history = new();
        SchemaHistoryTool tool = new();
        GeneratorTestRun v1 = RunGenerator(PrivatePromotionSource(1), [], null, "true");
        AssertSchemaOnlyCompiles(v1);
        tool.Publish(history.WriteManifest(v1), history.History);
        Dictionary<string, string> accepted = history.ReadContents();
        GeneratorTestRun v2 = RunGenerator(PrivatePromotionSource(2), history.ReadAdditionalTexts(), null, "true");
        AssertSchemaOnlyCompiles(v2);
        tool.Publish(history.WriteManifest(v2), history.History);
        tool.Verify(history.WriteManifest(v2), history.History);
        foreach ((string name, string contents) in accepted) { Assert.Equal(contents, File.ReadAllText(Path.Combine(history.History, name))); }
        return (new(EmitAndLoad(v1.OutputCompilation)), new(EmitAndLoad(v2.OutputCompilation)));
    }

    private sealed class PrivatePromotionFixture(Assembly assembly) {
        private readonly Type _host = assembly.GetType("PrivatePromotionStory.Host")!;
        internal StateModelRegistry Models() => _host.GetMethod("Models")!.CreateDelegate<Func<StateModelRegistry>>()();
        internal IDurableObject CreateSharedOrigin() => _host.GetMethod("CreateSharedOrigin")!.CreateDelegate<Func<IDurableObject>>()();
        internal IDurableObject CreateHolder() => _host.GetMethod("CreateHolder")!.CreateDelegate<Func<IDurableObject>>()();
        internal IDurableObject CreateLate() => _host.GetMethod("CreateLate")!.CreateDelegate<Func<IDurableObject>>()();
        internal IDurableObject WidgetFromHolder(object root) => _host.GetMethod("WidgetFromHolder")!.CreateDelegate<Func<object, IDurableObject>>()(root);
        internal DurableSchema Schema(string name) => _host.GetMethod("Schema")!
            .CreateDelegate<Func<StateBindingContext, string, DurableSchema>>()(Models().Snapshot(), name);
        internal void Check(object root, long value, int seven, bool late = false) => _host.GetMethod("Check")!
            .CreateDelegate<Action<object, long, int, bool>>()(root, value, seven, late);
        internal void ChangeList(object root, int index, int value) => _host.GetMethod("ChangeList")!.CreateDelegate<Action<object, int, int>>()(root, index, value);
        internal void ResetUpgrades() => _host.GetMethod("ResetUpgrades")!.CreateDelegate<Action>()();
        internal int Upgrades() => _host.GetMethod("Upgrades")!.CreateDelegate<Func<int>>()();
    }

    private static string PrivatePromotionSource(int version) {
        string upgrade = version == 2 ? """
            private static void UpgradeStateV1ToV2(in W.V1 prior, out W.V2 next) {
                UpgradeCalls++;
                next = new((long)prior.Segment0Field1 + 100, prior.Segment0Field2, prior.Segment0Field3);
            }
            """ : "";
        return $$"""
            using System;
            using System.Collections.Generic;
            using System.Linq;
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Persistence;
            using Atelia.DurableGraph.Runtime;
            using Atelia.DurableGraph.Schema;
            using W = global::Atelia.DurableGraph.Generated.Family_70726F6D6F74696F6E2E776964676574;
            namespace PrivatePromotionStory;
            [DurableType("promotion.widget", {{version}})]
            public sealed partial class Widget : IDurableObject {
                [DurableField(1)] public {{(version == 1 ? "int" : "long")}} Value;
                [DurableField(2)] public List<int> Numbers = new();
                [DurableField(3)] public Widget? Child;
                public static int UpgradeCalls;
                {{upgrade}}
            }
            [DurableType("promotion.zzz-holder", 1)]
            public sealed partial class Holder : IDurableObject { [DurableField(1)] public List<Widget> Items = new(); }
            [DurableType("promotion.shared", 1)]
            public sealed partial class SharedOrigin : IDurableObject { [DurableField(1)] public byte Value; }
            [DurableType("promotion.late", 1)]
            public sealed partial class Late : IDurableObject {
                [DurableField(1)] public int Value;
                [DurableField(2)] public List<int> Numbers = new();
                [DurableField(3)] public Late? Child;
            }
            [DurableType("promotion.private-only", 1)]
            public sealed partial class PrivateOnly : IDurableObject { [DurableField(1)] public byte Value; }
            public static class Host {
                public static StateModelRegistry Models() {
                    var models = new StateModelRegistry();
                    Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
                    return models;
                }
                public static DurableSchema Schema(StateBindingContext context, string name) => context.ResolveCurrentModel(name switch {
                    "Widget" => typeof(Widget), "Holder" => typeof(Holder), "Late" => typeof(Late), "PrivateOnly" => typeof(PrivateOnly),
                    _ => throw new InvalidOperationException("Unknown fixture Schema."),
                }).CurrentSchema;
                public static IDurableObject CreateSharedOrigin() => new SharedOrigin { Value = 9 };
                public static IDurableObject CreateHolder() {
                    var numbers = Enumerable.Range(0, 128).ToList();
                    var root = new Widget { Value = 1, Numbers = numbers };
                    root.Child = new Widget { Value = 2, Numbers = numbers, Child = root };
                    return new Holder { Items = new List<Widget> { root } };
                }
                public static IDurableObject CreateLate() => new Late { Value = 91, Numbers = Enumerable.Range(0, 128).ToList() };
                public static IDurableObject WidgetFromHolder(object root) => ((Holder)root).Items[0];
                public static void ChangeList(object root, int index, int value) => ((Widget)root).Numbers[index] = value;
                public static void ResetUpgrades() => Widget.UpgradeCalls = 0;
                public static int Upgrades() => Widget.UpgradeCalls;
                public static void Check(object value, long expected, int seven, bool late) {
                    List<int> numbers;
                    if (late) {
                        var root = (Late)value;
                        if (root.Value != expected || root.Child is not null) { throw new InvalidOperationException("Late values differ."); }
                        numbers = root.Numbers;
                    } else {
                        var root = (Widget)value;
                        if (root.Value != expected || root.Child is not { } child || child.Value != expected + 1 ||
                            !ReferenceEquals(root, child.Child) || !ReferenceEquals(root.Numbers, child.Numbers)) {
                            throw new InvalidOperationException("Widget upgrade, ring or shared List differs.");
                        }
                        numbers = root.Numbers;
                    }
                    if (numbers.Count != 128) { throw new InvalidOperationException("List count differs."); }
                    for (int i = 0; i < 128; i++) {
                        if (numbers[i] != (i == 7 ? seven : i)) { throw new InvalidOperationException("List contents differ."); }
                    }
                }
            }
            """;
    }
}
