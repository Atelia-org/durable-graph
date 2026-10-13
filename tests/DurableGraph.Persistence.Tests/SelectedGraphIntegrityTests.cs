using Atelia.DurableGraph.Persistence.MetaBootstrapP0;
using Atelia.DurableGraph.Storage;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed partial class EventHistoryRepositoryTests {
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RequiredGraphReadChecksRevisionParentEvenForCompleteBase(bool state, bool orphan) {
        FrameAddress first;
        ObjectId root;
        using (Repository seed = CreateRepository()) {
            using BranchCheckout main = seed.CreateBranch("main", new Node { Value = 1 }, NoRebase);
            first = main.Head.RevisionAddress;
            root = main.Head.RootId;
        }
        using (GraphResources resources = GraphResources.OpenExisting(_root)) {
            // A complete Base graph with business parent S0 must still name S0 as its
            // content source. A new well-framed batch/directory falsely claims no source.
            P0GraphStore store = resources.MetaBootstrapStore;
            FrameAddress invalid = AppendInvalidSource(resources, first, null, root,
                state ? P0GraphKind.State : P0GraphKind.Event, StorageTestData.LocalRecords(store, first));
            if (!orphan) { resources.PublishMetaBootstrapBranchRoot(resources.PrepareMetaBootstrapBranchRoot("main", invalid), out _); }
            else { resources.Data.ConfirmDurable(); }
        }
        var before = SnapshotFiles();
        using (Repository reader = Repository.OpenReadOnlyExisting(_root, Models())) {
            var head = reader.GetHead("main");
            if (orphan) { Assert.Equal(first, head.RevisionAddress); Assert.NotNull(reader.ReadState(head)); }
            else {
                Assert.Throws<InvalidDataException>(() => reader.ReadCheckpoint(head));
                Assert.Throws<InvalidDataException>(() => reader.ReadPair(head, head));
                if (state) { Assert.Throws<InvalidDataException>(() => reader.ReadState(head)); }
                else { Assert.Throws<InvalidDataException>(() => reader.ReadEvent(head)); }
                Assert.False(reader.IsFaulted);
            }
        }
        AssertFiles(before);
        if (!orphan) {
            using Repository writer = Repository.OpenExisting(_root, Models(), schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared);
            var head = writer.GetHead("main");
            Assert.Throws<InvalidDataException>(() => writer.Checkout("main"));
            Assert.Throws<InvalidDataException>(() => writer.Fork("invalid-fork", head));
            Assert.False(writer.IsFaulted);
            Assert.DoesNotContain("invalid-fork", writer.ListBranches());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EventOnlyHistoryDoesNotPermitEventRevisionAsContentParent(bool state) {
        FrameAddress first;
        ObjectId root;
        using (Repository seed = CreateRepository()) {
            using BranchCheckout main = seed.CreateBranchFromEvent("main", new Node { Value = 1 }, NoRebase);
            first = main.Head.RevisionAddress;
            root = main.Head.RootId;
        }
        using (GraphResources resources = GraphResources.OpenExisting(_root)) {
            P0GraphStore store = resources.MetaBootstrapStore;
            FrameAddress invalid = AppendInvalidSource(resources, first, first, root, state ? P0GraphKind.State : P0GraphKind.Event,
                StorageTestData.LocalRecords(store, first));
            resources.PublishMetaBootstrapBranchRoot(resources.PrepareMetaBootstrapBranchRoot("main", invalid), out _);
        }
        using (Repository reader = Repository.OpenReadOnlyExisting(_root, Models())) {
            CheckpointAddress head = reader.GetHead("main"); // Metadata navigation does not replay the bad directory.
            Assert.Equal(state ? GraphFrameKind.State : GraphFrameKind.Event, head.Kind);
            if (state) { Assert.Throws<InvalidDataException>(() => reader.ReadState(head)); }
            else { Assert.Throws<InvalidDataException>(() => reader.ReadEvent(head)); }
            Assert.False(reader.IsFaulted);
        }
        using Repository writer = Repository.OpenExisting(_root, Models(), schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared);
        CheckpointAddress selected = writer.GetHead("main");
        Assert.Throws<InvalidDataException>(() => writer.Checkout("main"));
        Assert.Throws<InvalidDataException>(() => writer.Fork("invalid-fork", selected));
        Assert.False(writer.IsFaulted);
        Assert.Equal(new[] { "main" }, writer.ListBranches());
    }

    private static FrameAddress AppendInvalidSource(GraphResources resources, FrameAddress parent,
        FrameAddress? source, ObjectId root, P0GraphKind kind, IReadOnlyList<ObjectVersionRecord> records) {
        FrameAddress? schema = resources.SharedMetaBootstrapRoot;
        FrameAddress batch = resources.Data.Append(P0GraphWire.BatchTag, P0GraphWire.EncodeBatch(source, schema, records)).Unwrap();
        var heads = records.ToDictionary(row => row.ObjectId, _ => batch);
        FrameAddress directory = resources.Data.Append(P0GraphWire.DirectoryTag,
            P0GraphWire.EncodeDirectory(source, ObjectVersionKind.Base, null, P0DirectoryCodec.PrepareBase(heads).Body)).Unwrap();
        return resources.Data.Append(P0GraphWire.GraphTag, P0GraphWire.EncodeGraph(kind, parent, root, directory, schema, batch)).Unwrap();
    }
}
