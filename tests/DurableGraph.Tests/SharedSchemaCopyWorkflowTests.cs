using System.Security.Cryptography;
using Atelia.DurableGraph.Build;
using Atelia.DurableGraph.Persistence;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    // DB-096 replacement for in-repository private development: copied, closed
    // repositories accept competing V2 drafts through the ordinary Shared path.
    // This uses real SG assemblies, not packaged MSBuild targets or OS snapshots.
    [Fact]
    public void SharedSchemaRepositoryCopiesIsolateDraftsAndUpgradeCleanReleaseBaseline() {
        using AncestryHistoryDirectory history = new();
        SchemaHistoryTool publisher = new();
        GeneratorTestRun v1 = RunGenerator(DevelopmentWorkflowSource(1, wideMarker: false), [], null, "true");
        AssertSchemaOnlyCompiles(v1);
        publisher.Publish(history.WriteManifest(v1), history.History);
        Dictionary<string, string> acceptedV1 = history.ReadContents();
        Assert.Single(acceptedV1);
        DevelopmentWorkflowHost old = DevelopmentWorkflowHost.Load(v1);

        // Both compilation inputs contain only V1. Draft outputs are distinct
        // assemblies and are never published to the accepted compiler history.
        GeneratorTestRun draftA = RunGenerator(DevelopmentWorkflowSource(2, wideMarker: false), history.ReadAdditionalTexts(), null, "true");
        GeneratorTestRun draftB = RunGenerator(DevelopmentWorkflowSource(2, wideMarker: true), history.ReadAdditionalTexts(), null, "true");
        AssertSchemaOnlyCompiles(draftA);
        AssertSchemaOnlyCompiles(draftB);
        DevelopmentWorkflowHost a = DevelopmentWorkflowHost.Load(draftA), b = DevelopmentWorkflowHost.Load(draftB);
        Assert.Equal(a.Schema().Type, b.Schema().Type);
        Assert.Equal(2, a.Schema().Version);
        Assert.Equal(2, b.Schema().Version);
        Assert.NotEqual(a.Schema(), b.Schema());

        using RawBaseDirectory template = new(), experimentA = new(), experimentB = new(), release = new();
        FrameAddress baseline, schemaV1;
        ObjectId rootId;
        Dictionary<ObjectId, (long Value, ObjectId List, ObjectId Child)> originalEdges;
        Dictionary<ObjectId, ObjectStorageInfo> originalStorage;
        EvidenceFrameImage[] originalFrames;
        using (Repository repository = Repository.CreateNew(template.Path, old.Models())) {
            using BranchCheckout main = repository.CreateSharedBranch("main", old.Create());
            uint firstMaximum = main.Baseline!.Objects.Keys.Max(id => id.Value);
            main.CommitState(old.Create());
            IDurableObject child = old.Child(main.State!);
            baseline = main.CommitState(child).RevisionAddress;
            Assert.Same(child, main.State);
            rootId = main.StateId!.Value;
            Assert.All(main.Baseline!.Objects.Keys, id => Assert.True(id.Value > firstMaximum));
            originalEdges = DevelopmentEdges(main, old);
            originalStorage = DevelopmentStorage(main);
            Assert.Equal(3, originalStorage.Count);
            old.Check(main.State!, 2, 1, 7);
            repository.CreateTag("user-v1", main.Head);
            schemaV1 = repository.SharedSchemaRoot!.Value;
            originalFrames = EvidenceReadFrames(EvidenceResources(repository).Data);
        }

        // All source owners are closed. Copy every file and directory; never
        // replace a live store or select only its data frames/control files.
        Dictionary<string, string> templateHashes = SharedCopyFileHashes(template.Path);
        CopyClosedSchemaRepository(template.Path, experimentA.Path, templateHashes);
        CopyClosedSchemaRepository(template.Path, experimentB.Path, templateHashes);
        CopyClosedSchemaRepository(template.Path, release.Path, templateHashes);
        foreach ((string path, DevelopmentWorkflowHost host, int seven) in new[] {
            (experimentA.Path, a, 7001), (experimentB.Path, b, 7002)
        }) {
            FrameAddress upgraded, schema;
            using (Repository repository = Repository.OpenExisting(path, host.Models())) {
                using BranchCheckout main = repository.CheckoutShared("main");
                Assert.Equal(baseline, main.StateRevisionAddress);
                Assert.Equal(schemaV1, repository.SharedSchemaRoot);
                DevelopmentAssertIdentity(main, host, rootId, originalEdges);
                Assert.Equal(2, main.Baseline!.Objects.Values.Count(row => row.RequiresRewrite));
                upgraded = main.CommitState().RevisionAddress;
                schema = repository.SharedSchemaRoot!.Value;
                Assert.NotEqual(schemaV1, schema);
                DevelopmentAssertUpgrade(ReferenceGraphStore(repository), main, host, baseline, baseline,
                    schema, rootId, originalEdges, originalStorage);
                SchemaCatalogView catalog = SchemaCatalogView.Load(ReferenceGraphStore(repository), schema);
                Assert.Equal(2, catalog.Schemas.Count);
                Assert.Equal(old.Schema(), catalog.Schemas[new(old.Schema().Type, 1)]);
                Assert.Equal(host.Schema(), catalog.Schemas[new(host.Schema().Type, 2)]);
            }
            ContinueSharedSchemaCopy(path, host, upgraded, schema, rootId, originalEdges, seven);
            using (Repository repository = Repository.OpenReadOnlyExisting(path, host.Models())) {
                host.Check(repository.ReadState(repository.GetHead("main")), 2 + host.Offset, 1 + host.Offset, seven);
                Assert.Equal(baseline, repository.ResolveTag("user-v1").RevisionAddress);
                Assert.Equal(schemaV1, ReferenceGraphStore(repository).ReadGraph(baseline).SchemaRoot);
                EvidenceAssertFramesPreserved(EvidenceResources(repository).Data, originalFrames);
            }
        }
        AssertSharedCopyHashes(templateHashes, SharedCopyFileHashes(template.Path));
        AssertSharedCopyHashes(templateHashes, SharedCopyFileHashes(release.Path));
        DevelopmentAssertHistoryUnchanged(history, acceptedV1);

        foreach ((string path, DevelopmentWorkflowHost wrongHost) in new[] {
            (experimentA.Path, b), (experimentB.Path, a)
        }) {
            Dictionary<string, string> before = SharedCopyFileHashes(path);
            using (Repository repository = Repository.OpenReadOnlyExisting(path, wrongHost.Models())) {
                Assert.ThrowsAny<InvalidDataException>(() => repository.ReadState(repository.GetHead("main")));
                Assert.False(repository.IsFaulted);
            }
            AssertSharedCopyHashes(before, SharedCopyFileHashes(path));
        }

        // Only the winning definition enters accepted compiler history. Release
        // starts from untouched V1 bytes, with no promotion/import of either draft.
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
        FrameAddress finalState, finalSchema;
        using (Repository repository = Repository.OpenExisting(release.Path, winner.Models())) {
            using BranchCheckout main = repository.CheckoutShared("main");
            Assert.Equal(baseline, main.StateRevisionAddress);
            finalState = main.CommitState().RevisionAddress;
            finalSchema = repository.SharedSchemaRoot!.Value;
            GraphRecordStore store = ReferenceGraphStore(repository);
            DevelopmentAssertUpgrade(store, main, winner, baseline, baseline, finalSchema, rootId, originalEdges, originalStorage);
            SchemaCatalogView catalog = SchemaCatalogView.Load(store, finalSchema);
            Assert.Equal(2, catalog.Schemas.Count);
            Assert.Equal(old.Schema(), catalog.Schemas[new(old.Schema().Type, 1)]);
            Assert.Equal(winner.Schema(), catalog.Schemas[new(winner.Schema().Type, 2)]);
            Assert.DoesNotContain(catalog.Schemas.Values, schema => schema.Equals(a.Schema()));
        }
        ContinueSharedSchemaCopy(release.Path, winner, finalState, finalSchema, rootId, originalEdges, 9000);
        using (Repository repository = Repository.OpenReadOnlyExisting(release.Path, winner.Models())) {
            winner.Check(repository.ReadState(repository.GetHead("main")), 202, 201, 9000);
            winner.Check(repository.ReadState(repository.ResolveTag("user-v1")), 202, 201, 7);
            EvidenceAssertFramesPreserved(EvidenceResources(repository).Data, originalFrames);
        }
        // The repository's current catalog now contains V2, but an old host can
        // still read the bookmarked V1 with its own exact historical Schema.
        using (Repository repository = Repository.OpenReadOnlyExisting(release.Path, old.Models())) {
            Assert.Equal(finalSchema, repository.SharedSchemaRoot);
            old.Check(repository.ReadState(repository.ResolveTag("user-v1")), 2, 1, 7);
        }
        using (Repository repository = Repository.OpenReadOnlyExisting(template.Path, old.Models())) {
            Assert.Equal(baseline, repository.GetHead("main").RevisionAddress);
            Assert.Equal(schemaV1, repository.SharedSchemaRoot);
            old.Check(repository.ReadState(repository.GetHead("main")), 2, 1, 7);
        }
        AssertSharedCopyHashes(templateHashes, SharedCopyFileHashes(template.Path));
    }

    private static void ContinueSharedSchemaCopy(string path, DevelopmentWorkflowHost host,
        FrameAddress source, FrameAddress schema, ObjectId rootId,
        Dictionary<ObjectId, (long Value, ObjectId List, ObjectId Child)> edges, int seven) {
        using Repository repository = Repository.OpenExisting(path, host.Models());
        using BranchCheckout main = repository.CheckoutShared("main");
        Assert.Equal(source, main.StateRevisionAddress);
        DevelopmentAssertIdentity(main, host, rootId, edges);
        Dictionary<ObjectId, ObjectStorageInfo> prior = DevelopmentStorage(main);
        ObjectId listId = edges[rootId].List;
        host.ChangeList(main.State!, seven);
        FrameAddress saved = main.CommitState(new(int.MaxValue, 1)).RevisionAddress;
        DevelopmentAssertDelta(ReferenceGraphStore(repository), main, source, saved, schema, listId, prior[listId]);
        foreach ((ObjectId id, ObjectStorageInfo storage) in prior.Where(pair => pair.Key != listId)) {
            Assert.Equal(storage, main.Baseline!.Objects[id].Storage);
        }
        Assert.Equal(schema, repository.SharedSchemaRoot);
    }

    private static void CopyClosedSchemaRepository(string source, string destination, Dictionary<string, string> expected) {
        Assert.False(Directory.Exists(destination));
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories)) {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)) {
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
        }
        AssertSharedCopyHashes(expected, SharedCopyFileHashes(destination));
    }

    private static Dictionary<string, string> SharedCopyFileHashes(string path) =>
        Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).ToDictionary(
            file => Path.GetRelativePath(path, file),
            file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))), StringComparer.Ordinal);

    private static void AssertSharedCopyHashes(Dictionary<string, string> expected, Dictionary<string, string> actual) {
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach ((string file, string hash) in expected) { Assert.Equal(hash, actual[file]); }
    }
}
