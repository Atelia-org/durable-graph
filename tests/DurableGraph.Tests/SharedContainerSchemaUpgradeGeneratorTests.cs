using Atelia.DurableGraph.Build;
using Atelia.DurableGraph.Persistence;

using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Fact]
    public void GeneratedSharedContainerUpgradeRejectsMissingListRulesAndColdForkPreservesIdentities() {
        // Reuse the generated Array/List/Dictionary + nullable inline history witness.
        // The additional qualification here is the public Shared checkout lifecycle.
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
        FrameAddress baseline, firstSaved;
        ObjectId root;
        uint[] ids;
        EvidenceFrameImage[] oldFrames;
        EvidenceRefImage shared;
        using (Repository repository = Repository.CreateNew(directory.Path, old.Models())) {
            BranchId mainId = repository.CreateRootBranchFromState("main", old.Create()).Id;
            using WorkingCopy main = repository.OpenWorkingCopy(mainId);
            // Consume the empty-session allocation range before fixing the user snapshot.
            main.CommitState(old.Create());
            baseline = EvidenceAddress(repository, main.BaseCommitId);
            root = main.StateId!.Value;
            ids = main.Baseline!.Objects.Keys.Select(id => id.Value).Order().ToArray();
            Assert.All(ids, id => Assert.True(id > 4));
            repository.CreateTag("baseline", main.BaseCommitId);
            oldFrames = EvidenceReadFrames(EvidenceResources(repository).Data);
            shared = EvidenceReadRef(repository, 2);
        }
        using (Repository repository = Repository.OpenExisting(directory.Path, missing.Models())) {
            CommitId checkpoint = repository.ResolveTag("baseline");
            repository.ForkBranch(checkpoint, "upgraded");
            EvidencePublicationImage before = EvidencePublication(repository);
            Assert.ThrowsAny<InvalidDataException>(() => repository.OpenWorkingCopy(EvidenceBranch(repository, "upgraded")));
            EvidenceAssertPublicationUnchanged(repository, before);
            Assert.Equal(checkpoint, repository.ReadCurrentCommit(EvidenceBranch(repository, "upgraded")).Commit.Id);
            // A second preflight must reach the same data error, rather than occupancy rejection.
            Assert.ThrowsAny<InvalidDataException>(() => repository.OpenWorkingCopy(EvidenceBranch(repository, "upgraded")));
            EvidenceAssertPublicationUnchanged(repository, before);
            Assert.False(repository.IsFaulted);
        }
        using (Repository repository = Repository.OpenExisting(directory.Path, current.Models())) {
            using WorkingCopy branch = repository.OpenWorkingCopy(EvidenceBranch(repository, "upgraded"));
            current.Check(branch.State!, 1);
            Assert.Equal(root, branch.StateId);
            Assert.Equal(ids, branch.Baseline!.Objects.Keys.Select(id => id.Value).Order().ToArray());
            Assert.Equal(3, branch.Baseline.Objects.Values.Count(row => row.RequiresRewrite));
            firstSaved = EvidenceAddress(repository, branch.CommitState().Id);
            GraphRecordStore store = ReferenceGraphStore(repository);
            StoredGraphRevision saved = PublishedGraph(EvidenceResources(repository), firstSaved);

            Assert.Equal(baseline, EvidenceResources(repository).ReadPrevious(branch.BaseCommitId.Value)!.Address);
            Assert.Equal(baseline, saved.SourceGraph);
            Assert.Equal(ids, saved.ObjectHeads.Keys.Order().ToArray());
            Assert.Equal(root, saved.RootId);
            Assert.Equal(3, DefaultRecords(store, PublishedGraph(EvidenceResources(repository), firstSaved)).Count);
            Assert.All(DefaultRecords(store, PublishedGraph(EvidenceResources(repository), firstSaved)), record => {
                Assert.Equal(ObjectVersionKind.Base, record.Kind);
                Assert.Null(record.PriorAddress);
            });
            Assert.Equal(PublishedGraph(EvidenceResources(repository), baseline).ObjectHeads[root.Value], saved.ObjectHeads[root.Value]);
            AssertBootstrapContainerUpgradeCatalog(branch.AcceptedCatalog!, ExpectedBootstrapContainerUpgrade(1), ExpectedBootstrapContainerUpgrade(2));
            Assert.Equal(saved.SchemaRoot, repository.SharedSchemaRoot);
            shared = EvidenceReadRef(repository, 2);
            EvidenceAssertFramesPreserved(EvidenceResources(repository).Data, oldFrames);
        }
        using (Repository repository = Repository.OpenExisting(directory.Path, current.Models())) {
            using WorkingCopy branch = repository.OpenWorkingCopy(EvidenceBranch(repository, "upgraded"));
            current.Check(branch.State!, 1);
            Assert.Equal(root, branch.StateId);
            Dictionary<ObjectId, ObjectStorageInfo> previous = branch.Baseline!.Objects
                .ToDictionary(row => row.Key, row => row.Value.Storage!.Value);
            current.Change(branch.State!);
            FrameAddress next = EvidenceAddress(repository, branch.CommitState().Id);
            GraphRecordStore store = ReferenceGraphStore(repository);
            Assert.Equal(firstSaved, PublishedGraph(EvidenceResources(repository), next).SourceGraph);
            Assert.Equal(ids, PublishedGraph(EvidenceResources(repository), next).ObjectHeads.Keys.Order().ToArray());
            Assert.Equal(3, DefaultRecords(store, PublishedGraph(EvidenceResources(repository), next)).Count);
            Assert.All(DefaultRecords(store, PublishedGraph(EvidenceResources(repository), next)), record => {
                Assert.Equal(ObjectVersionKind.Delta, record.Kind);
                Assert.Equal(previous[new(record.ObjectId)].Head, record.PriorAddress);
                ObjectVersionChain chain = store.ReadObjectChain(PublishedGraph(EvidenceResources(repository), next), record.ObjectId);
                Assert.Equal(previous[new(record.ObjectId)].ReconstructionPayloadBytes + chain.Entries[^1].PayloadBytes,
                    branch.Baseline!.Objects[new(record.ObjectId)].Storage!.Value.ReconstructionPayloadBytes);
            });
            current.Check(repository.LoadState(branch.BaseCommitId), 2);
            EvidenceAssertSameRef(shared, EvidenceReadRef(repository, 2));
            EvidenceAssertFramesPreserved(EvidenceResources(repository).Data, oldFrames);
        }
    }
}
