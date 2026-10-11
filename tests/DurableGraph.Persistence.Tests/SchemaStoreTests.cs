using Atelia.DurableGraph.Schema;
using Atelia.Data;
using Atelia.VersionStore;
using System.Reflection;
using Atelia.FrameStore;
using Atelia.Rbf;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class SchemaStoreTests : IDisposable {
    private readonly List<string> _paths = [];

    [Fact]
    public void CompleteClosureRegistersOnceAndColdReopensWithExactDefinitions() {
        string path = NewPath();
        DurableSchema ancestor = new("Base", 2, new DurableFieldInfo(1, TypeTag.Int64));
        DurableSchema leaf = new("Leaf", 4, [new(2, TypeTag.String)], ancestor);
        using (GraphResources resources = GraphResources.CreateNew(path)) {
            SchemaStore store = resources.Schemas;
            Assert.Same(leaf, store.Register(leaf));
            Assert.Equal(2, store.Count);
            Assert.Equal(1, CountFrames(resources));
            DurableSchema equal = new("Leaf", 4, [new(2, TypeTag.String)], new("Base", 2, new DurableFieldInfo(1, TypeTag.Int64)));
            Assert.Same(leaf, store.Register(equal));
            store.RegisterBatch([]);
            RepresentationId[] ids = store.RegisterRepresentations([ObjectLayout.ForDurable(leaf), ObjectLayout.ForDurable(ancestor)]);
            Assert.Equal(new RepresentationId[] { new(3), new(2) }, ids);
            Assert.Equal(ObjectLayout.ForDurable(leaf), store.GetRepresentation(ids[0]));
            Assert.Equal(1, CountFrames(resources));
        }
        using (GraphResources resources = GraphResources.OpenExisting(path)) {
            SchemaStore store = resources.Schemas;
            Assert.Equal(leaf, store.GetRequired("Leaf", 4));
            Assert.Same(store.GetRequired("Base", 2), store.GetRequired("Leaf", 4).BaseSchema);
            SchemaNotFoundException missing = Assert.Throws<SchemaNotFoundException>(() => store.GetRequired("Leaf", 3));
            Assert.Equal("Leaf", missing.SchemaId);
            Assert.Equal(3, missing.Version);
            DurableSchema conflicting = new("Leaf", 4, [], ancestor);
            SchemaConflictException conflict = Assert.Throws<SchemaConflictException>(() => store.Register(conflicting));
            Assert.Same(store.GetRequired("Leaf", 4), conflict.RegisteredSchema);
            Assert.Same(conflicting, conflict.ConflictingSchema);
            Assert.Equal("Leaf", conflict.SchemaId);
            Assert.Equal(4, conflict.Version);
            Assert.Throws<SchemaConflictException>(() => store.Register(new("Leaf", 4, [new(2, TypeTag.String)], new("Base", 3))));
            Assert.Equal(1, CountFrames(resources));
            Assert.False(store.IsFaulted);
        }
    }

    [Fact]
    public void EveryPublicationContainsTheCompleteAcceptedCatalogAndStableIds() {
        string path = NewPath();
        DurableSchema a = new("A", 1);
        DurableSchema b = new("B", 1);
        using (GraphResources resources = GraphResources.CreateNew(path)) {
            Assert.Equal(new RepresentationId(2), resources.Schemas.RegisterRepresentations([ObjectLayout.ForDurable(a)])[0]);
            Assert.Equal(new RepresentationId(3), resources.Schemas.RegisterRepresentations([ObjectLayout.ForDurable(b)])[0]);
            List<SchemaCatalogEntry[]> snapshots = [];
            resources.Data.Inventory(info => {
                if (info.Tag != SchemaCatalogWireCodec.RbfTag) { return; }
                using FrameRead frame = resources.Data.ReadFrame(info.Address).Unwrap();
                snapshots.Add(SchemaCatalogWireCodec.Read(frame.Payload));
            }).Unwrap();
            Assert.Equal(new[] { 1, 2 }, snapshots.Select(static rows => rows.Length).Order().ToArray());
            SchemaCatalogEntry[] current = Assert.Single(snapshots, static rows => rows.Length == 2);
            Assert.Equal(new uint[] { 2, 3 }, current.Select(static row => row.Id.Value));
            Assert.Equal(new[] { a, b }, current.Select(static row => row.Schema));
        }
        using GraphResources reopened = GraphResources.OpenExisting(path);
        Assert.Equal(2, reopened.Schemas.Count);
        Assert.Equal(new RepresentationId[] { new(2), new(3) },
            reopened.Schemas.RegisterRepresentations([ObjectLayout.ForDurable(a), ObjectLayout.ForDurable(b)]));
        Assert.Equal(2, CountFrames(reopened));
    }

    [Fact]
    public void FullInputAndAncestorConflictsAreRejectedBeforeAnyWrite() {
        using GraphResources resources = GraphResources.CreateNew(NewPath());
        SchemaStore store = resources.Schemas;
        DurableSchema old = new("Taken", 1, new DurableFieldInfo(1, TypeTag.Int32));
        store.Register(old);
        Assert.Throws<SchemaConflictException>(() => store.RegisterBatch([
            new("New", 1), new("Taken", 1, new DurableFieldInfo(1, TypeTag.String))]));
        Assert.Throws<SchemaConflictException>(() => store.RegisterBatch([
            new("X", 1), new("X", 1, new DurableFieldInfo(1, TypeTag.Int32))]));
        Assert.Throws<SchemaConflictException>(() => store.RegisterBatch([
            new("Leaf1", 1, [], new("Ancestor", 1)),
            new("Leaf2", 1, [], new("Ancestor", 1, new DurableFieldInfo(1, TypeTag.Int32)))]));
        Assert.Equal(1, store.Count);
        Assert.Equal(1, CountFrames(resources));
        Assert.False(store.IsFaulted);
    }

    [Fact]
    public void EnumerationNullAndDepthFailuresLeaveStoreUsableAndDoNotConsumeIds() {
        using GraphResources resources = GraphResources.CreateNew(NewPath());
        SchemaStore store = resources.Schemas;
        Assert.Throws<IOException>(() => store.RegisterBatch(FailingEnumeration()));
        Assert.Throws<ArgumentNullException>(() => store.RegisterBatch([new("A", 1), null!]));
        Assert.Throws<ArgumentException>(() => store.Register(SchemaCatalogWireCodecTests.Chain(257)[^1]));
        Assert.Equal(0, CountFrames(resources));
        Assert.Equal(0, store.Count);
        Assert.False(store.IsFaulted);
        Assert.Equal(new RepresentationId(2), store.RegisterRepresentations([ObjectLayout.ForDurable(new("A", 1))])[0]);
    }

    [Fact]
    public void EnumerationCannotReenterAndCaughtReentryDoesNotPoison() {
        using GraphResources resources = GraphResources.CreateNew(NewPath());
        SchemaStore store = resources.Schemas;
        store.RegisterBatch(AttemptReentry());
        Assert.Equal(1, store.Count);
        Assert.False(store.IsFaulted);

        IEnumerable<DurableSchema> AttemptReentry() {
            Assert.Throws<InvalidOperationException>(() => store.Register(new("Inner", 1)));
            Assert.Throws<InvalidOperationException>(() => store.GetRequired("A", 1));
            Assert.Throws<InvalidOperationException>(() => store.Count);
            Assert.False(store.IsFaulted);
            yield return new("A", 1);
        }
    }

    [Fact]
    public void CompleteUnpublishedSnapshotsAndMalformedOrphansAreNotAdopted() {
        string path = NewPath();
        using (GraphResources resources = GraphResources.CreateNew(path)) {
            resources.Schemas.Register(new("A", 1));
            byte[] orphan = SchemaCatalogWireCodec.Write(CatalogTestData.Schemas([new("A", 1), new("Unpublished", 1)]));
            resources.Data.Append(SchemaCatalogWireCodec.RbfTag, orphan).Unwrap();
            resources.Data.Append(SchemaCatalogWireCodec.RbfTag, new byte[] { 99 }).Unwrap();
            resources.Data.ConfirmDurable();
        }
        using (GraphResources resources = GraphResources.OpenExisting(path)) {
            Assert.Equal(1, resources.Schemas.Count);
            Assert.Throws<SchemaNotFoundException>(() => resources.Schemas.GetRequired("Unpublished", 1));
            Assert.Equal(new RepresentationId(3),
                resources.Schemas.RegisterRepresentations([ObjectLayout.ForDurable(new("Accepted", 1))])[0]);
        }
        using GraphResources reopened = GraphResources.OpenExisting(path);
        Assert.Equal(2, reopened.Schemas.Count);
        Assert.Throws<SchemaNotFoundException>(() => reopened.Schemas.GetRequired("Unpublished", 1));
        Assert.Equal("Accepted", reopened.Schemas.GetRequired("Accepted", 1).SchemaId);
    }

    [Fact]
    public void OrdinaryControlCapacityRejectionKeepsSchemaOwnerHealthyAndDoesNotInstallCandidate() {
        string path = NewPath();
        using (GraphResources resources = GraphResources.CreateNew(path)) {
            var root = resources.Versions.ReadRef(1).Unwrap();
            // Reach the actual VS pre-I/O capacity check without creating a terabyte file.
            var refs = (System.Collections.IDictionary)resources.Versions.GetType()
                .GetField("_ownedRefs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(resources.Versions)!;
            object owned = refs[1U]!;
            IRbfFile file = (IRbfFile)owned.GetType().GetField("File", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owned)!;
            FieldInfo tail = file.GetType().GetField("_tailOffset", BindingFlags.Instance | BindingFlags.NonPublic)!;
            long originalTail = file.TailOffset;
            try {
                tail.SetValue(file, SizedPtr.MaxOffset + 4);
                var rejected = resources.Versions.PublishRef(root.Revision.RefId, root.Payload, out PublicationOutcome outcome);
                Assert.True(rejected.IsFailure);
                Assert.Equal(PublicationOutcome.NotAttempted, outcome);
                Assert.False(resources.IsFaulted);
                Assert.ThrowsAny<Exception>(() => resources.Schemas.Register(new("Rejected", 1)));
            }
            finally { tail.SetValue(file, originalTail); }
            Assert.False(resources.IsFaulted);
            Assert.False(resources.Schemas.IsFaulted);
            Assert.Equal(0, resources.Schemas.Count);
            Assert.Equal(root.Revision, resources.Versions.ReadRef(1).Unwrap().Revision);
            Assert.Equal(1, CountFrames(resources)); // The rejected snapshot remains a complete orphan.
            Assert.Equal(new RepresentationId(2), resources.Schemas.RegisterRepresentations([ObjectLayout.ForDurable(new("Accepted", 1))])[0]);
        }
        using GraphResources reopened = GraphResources.OpenExisting(path);
        Assert.Equal(1, reopened.Schemas.Count);
        Assert.Equal("Accepted", reopened.Schemas.GetRequired("Accepted", 1).SchemaId);
        Assert.Throws<SchemaNotFoundException>(() => reopened.Schemas.GetRequired("Rejected", 1));
    }

    [Theory]
    [InlineData("ConfirmDataDurable", false)]
    [InlineData("AfterRefRecordAppended", true)]
    [InlineData("AfterPublicationConfirmed", true)]
    public void SchemaPublicationFailureFaultsOwnerAndReopenUsesTheActualSelectedRoot(string hookName, bool rootHasSchema) {
        string path = NewPath();
        using (GraphResources resources = GraphResources.CreateNew(path)) {
            InjectPublicationFailure(resources, hookName);
            IOException error = Assert.Throws<IOException>(() => resources.Schemas.Register(new("A", 1)));
            Assert.Equal(hookName, error.Message);
            Assert.True(resources.Schemas.IsFaulted);
            Assert.True(resources.IsFaulted);
            Assert.Throws<InvalidOperationException>(() => resources.Schemas.GetRequired("A", 1));
            Assert.Throws<InvalidOperationException>(() => resources.Schemas.RegisterBatch([]));
        }
        // A close/reopen observes complete appended records, not the result of a power failure.
        using GraphResources reopened = GraphResources.OpenExisting(path);
        Assert.Equal(rootHasSchema ? 1 : 0, reopened.Schemas.Count);
        Assert.Equal(1, CountFrames(reopened)); // The complete snapshot is preserved even when unselected.
        Assert.Equal(new RepresentationId(2),
            reopened.Schemas.RegisterRepresentations([ObjectLayout.ForDurable(new("A", 1))])[0]);
        Assert.Equal(rootHasSchema ? 1 : 2, CountFrames(reopened));
    }

    [Fact]
    public void ReadOnlyStoreLoadsSelectedSnapshotWithoutWriting() {
        string path = NewPath();
        using (GraphResources resources = GraphResources.CreateNew(path)) { resources.Schemas.Register(new("A", 1)); }
        Dictionary<string, byte[]> before = SnapshotFiles(path);
        using (GraphResources resources = GraphResources.OpenReadOnlyExisting(path)) {
            Assert.Equal(1, resources.Schemas.Count);
            Assert.Throws<InvalidOperationException>(() => resources.Schemas.Register(new("B", 1)));
            Assert.Throws<InvalidOperationException>(() => resources.Schemas.RegisterBatch([]));
        }
        Dictionary<string, byte[]> after = SnapshotFiles(path);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach ((string file, byte[] bytes) in before) { Assert.Equal(bytes, after[file]); }
    }

    [Fact]
    public void BorrowedOwnerAndBackingLifetimeAreCheckedBeforeCachedSchemaReads() {
        GraphResources resources = GraphResources.CreateNew(NewPath());
        SchemaStore store = resources.Schemas;
        store.Register(new("A", 1));
        resources.Dispose();
        Assert.Throws<ObjectDisposedException>(() => store.GetRequired("A", 1));
        Assert.Throws<ObjectDisposedException>(() => store.Count);
        Assert.Throws<ObjectDisposedException>(() => store.RegisterBatch([]));

        using GraphResources other = GraphResources.CreateNew(NewPath());
        SchemaStore otherStore = other.Schemas;
        otherStore.Register(new("A", 1));
        other.Data.Dispose();
        Assert.ThrowsAny<Exception>(() => otherStore.GetRequired("A", 1));
        Assert.ThrowsAny<Exception>(() => otherStore.Count);
    }

    [Fact]
    public void ClosingOwnerDuringInputPreparationCannotAppendOrInstallCandidate() {
        string path = NewPath();
        using (GraphResources resources = GraphResources.CreateNew(path)) {
            SchemaStore store = resources.Schemas;
            Assert.Throws<ObjectDisposedException>(() => store.RegisterBatch(CloseDuringEnumeration()));
            IEnumerable<DurableSchema> CloseDuringEnumeration() {
                yield return new("Candidate", 1);
                resources.Dispose();
            }
        }
        using GraphResources reopened = GraphResources.OpenExisting(path);
        Assert.Equal(0, reopened.Schemas.Count);
        Assert.Equal(0, CountFrames(reopened));
    }

    private static void InjectPublicationFailure(GraphResources resources, string hookName) {
        // Use the backing store's existing narrow internal fault windows; product owners remain concrete.
        FieldInfo hooksField = resources.Versions.GetType().GetField("_testHooks", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object hooks = Activator.CreateInstance(hooksField.FieldType, nonPublic: true)!;
        FieldInfo callbackField = hooksField.FieldType.GetField(hookName, BindingFlags.Instance | BindingFlags.NonPublic)!;
        Delegate callback = hookName == "AfterRefRecordAppended"
            ? new Action<SizedPtr>(_ => throw new IOException(hookName))
            : new Action(() => throw new IOException(hookName));
        callbackField.SetValue(hooks, callback);
        hooksField.SetValue(resources.Versions, hooks);
    }

    private static long CountFrames(GraphResources resources) => resources.Data.Inventory(static _ => { }).Unwrap();

    private static Dictionary<string, byte[]> SnapshotFiles(string path) => Directory.GetFiles(path, "*", SearchOption.AllDirectories)
        .ToDictionary(static file => file, File.ReadAllBytes);

    private static IEnumerable<DurableSchema> FailingEnumeration() {
        yield return new("New", 1);
        throw new IOException("Injected enumeration failure.");
    }

    private string NewPath() {
        string path = Path.Combine(Path.GetTempPath(), $"durable-schema-{Guid.NewGuid():N}");
        _paths.Add(path);
        return path;
    }

    public void Dispose() {
        foreach (string path in _paths) { if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); } }
    }
}
