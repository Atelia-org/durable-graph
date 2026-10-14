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
    public void GeneratedPrivateContainerUpgradeRejectsMissingListRulesAndColdForkPreservesIdentities() {
        // Reuse the generated Array/List/Dictionary + nullable inline history witness.
        // The additional qualification here is the PUBLIC private checkout lifecycle.
        using AncestryHistoryDirectory history = new();
        SchemaHistoryTool tool = new();
        GeneratorTestRun v1 = RunGenerator(BootstrapContainerUpgradeSource(1), [], null, "true");
        AssertSchemaOnlyCompiles(v1);
        BootstrapContainerUpgradeHost old = BootstrapContainerUpgradeHost.Load(v1);
        tool.Publish(history.WriteManifest(v1), history.History);
        GeneratorTestRun v2 = RunGenerator(BootstrapContainerUpgradeSource(2), history.ReadAdditionalTexts(), null, "true");
        AssertSchemaOnlyCompiles(v2);
        BootstrapContainerUpgradeHost current = BootstrapContainerUpgradeHost.Load(v2);
        string withoutListSelection = BootstrapContainerUpgradeSource(2)
            .Replace("models.UseListElementUpgrades(typeof(Rules));", "", StringComparison.Ordinal);
        Assert.NotEqual(BootstrapContainerUpgradeSource(2), withoutListSelection);
        GeneratorTestRun missingRun = RunGenerator(withoutListSelection, history.ReadAdditionalTexts(), null, "true");
        AssertSchemaOnlyCompiles(missingRun);
        BootstrapContainerUpgradeHost missing = BootstrapContainerUpgradeHost.Load(missingRun);

        using RawBaseDirectory directory = new();
        FrameAddress baseline, firstPrivate;
        ObjectId root;
        uint[] ids;
        PrivateFrameImage[] oldFrames;
        PrivateRefImage shared;
        using (Repository repository = Repository.CreateNew(directory.Path, old.Models(), schemaWriteMode: SchemaWriteMode.Shared)) {
            using BranchCheckout main = repository.CreateBranch("main", old.Create());
            // Consume the empty-session allocation range before fixing the user snapshot.
            main.CommitState(old.Create());
            baseline = main.Head.RevisionAddress;
            root = main.StateId!.Value;
            ids = main.Baseline!.Objects.Keys.Select(id => id.Value).Order().ToArray();
            Assert.All(ids, id => Assert.True(id > 4));
            repository.CreateTag("baseline", main.Head);
            oldFrames = PrivateReadFrames(PrivateResources(repository).Data);
            shared = PrivateReadRef(repository, 2);
        }
        using (Repository repository = Repository.OpenExisting(directory.Path, missing.Models())) {
            CheckpointAddress checkpoint = repository.ResolveTag("baseline");
            repository.CreateBranch("private", checkpoint);
            PrivatePublicationImage before = PrivatePublication(repository);
            Assert.ThrowsAny<InvalidDataException>(() => repository.CheckoutPrivate("private"));
            PrivateAssertPublicationUnchanged(repository, before);
            Assert.Equal(checkpoint, repository.GetHead("private"));
            // A second preflight must reach the same data error, rather than occupancy rejection.
            Assert.ThrowsAny<InvalidDataException>(() => repository.CheckoutPrivate("private"));
            PrivateAssertPublicationUnchanged(repository, before);
            Assert.False(repository.IsFaulted);
        }
        using (Repository repository = Repository.OpenExisting(directory.Path, current.Models())) {
            using BranchCheckout branch = repository.CheckoutPrivate("private");
            current.Check(branch.State!, 1);
            Assert.Equal(root, branch.StateId);
            Assert.Equal(ids, branch.Baseline!.Objects.Keys.Select(id => id.Value).Order().ToArray());
            Assert.Equal(3, branch.Baseline.Objects.Values.Count(row => row.RequiresRewrite));
            firstPrivate = branch.CommitState().RevisionAddress;
            GraphRecordStore store = ReferenceGraphStore(repository);
            StoredGraphRevision saved = store.ReadGraph(firstPrivate);
            Assert.Equal(baseline, saved.Parent);
            Assert.Equal(baseline, saved.SourceGraph);
            Assert.Equal(ids, saved.ObjectHeads.Keys.Order().ToArray());
            Assert.Equal(root, saved.RootId);
            Assert.Equal(3, DefaultRecords(store, firstPrivate).Count);
            Assert.All(DefaultRecords(store, firstPrivate), record => {
                Assert.Equal(ObjectVersionKind.Base, record.Kind);
                Assert.Null(record.PriorAddress);
            });
            Assert.Equal(store.ReadGraph(baseline).ObjectHeads[root.Value], saved.ObjectHeads[root.Value]);
            AssertBootstrapContainerUpgradeCatalog(branch.Catalog.Accepted!, ExpectedBootstrapContainerUpgrade(1), ExpectedBootstrapContainerUpgrade(2));
            PrivateAssertSameRef(shared, PrivateReadRef(repository, 2));
            PrivateAssertFramesPreserved(PrivateResources(repository).Data, oldFrames);
        }
        using (Repository repository = Repository.OpenExisting(directory.Path, current.Models())) {
            using BranchCheckout branch = repository.CheckoutPrivate("private");
            current.Check(branch.State!, 1);
            Assert.Equal(root, branch.StateId);
            Dictionary<ObjectId, ObjectStorageInfo> previous = branch.Baseline!.Objects
                .ToDictionary(row => row.Key, row => row.Value.Storage!.Value);
            current.Change(branch.State!);
            FrameAddress next = branch.CommitState().RevisionAddress;
            GraphRecordStore store = ReferenceGraphStore(repository);
            Assert.Equal(firstPrivate, store.ReadGraph(next).SourceGraph);
            Assert.Equal(ids, store.ReadGraph(next).ObjectHeads.Keys.Order().ToArray());
            Assert.Equal(3, DefaultRecords(store, next).Count);
            Assert.All(DefaultRecords(store, next), record => {
                Assert.Equal(ObjectVersionKind.Delta, record.Kind);
                Assert.Equal(previous[new(record.ObjectId)].Head, record.PriorAddress);
                ObjectVersionChain chain = store.ReadObjectChain(next, record.ObjectId);
                Assert.Equal(previous[new(record.ObjectId)].ReconstructionPayloadBytes + chain.Entries[^1].PayloadBytes,
                    branch.Baseline!.Objects[new(record.ObjectId)].Storage!.Value.ReconstructionPayloadBytes);
            });
            current.Check(repository.ReadState(branch.Head), 2);
            PrivateAssertSameRef(shared, PrivateReadRef(repository, 2));
            PrivateAssertFramesPreserved(PrivateResources(repository).Data, oldFrames);
        }
    }
}
