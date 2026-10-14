
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using Frames = Atelia.FrameStore.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

// Opaque bodies isolate the shared durable pipeline; generated-model tests
// separately qualify the catalog and business body interpretation.
public sealed class MetaBootstrapP0GraphStoreTests : IDisposable {
    private const string Prefix = "dg-bootstrap-graphstore-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Dictionary_delta_and_unchanged_root_preserve_exact_object_chains_after_cold_open() {
        FrameAddress firstAddress, secondAddress, reusedAddress, schemaRoot;
        using (Frames frames = Frames.Create(_path)) {
            using GraphRecordStore store = new(frames) { DirectoryReadAmplificationThreshold = 100 };
            schemaRoot = store.Append(StoredGraphKind.Meta, null, null,
                new(null, new(1), [B(1, [0])])).Address;
            StoredGraphRevision first = store.Append(StoredGraphKind.State, null, schemaRoot,
                new(null, new(1), Enumerable.Range(1, 64).Select(i => B((uint)i, [(byte)i]))));
            StoredGraphRevision second = store.Append(StoredGraphKind.State, first.Address, schemaRoot,
                new(first.Address, new(1), [D(1, first.ObjectHeads[1], [65, 66])]));
            ObjectHeadDirectoryVersion initialDirectory = store.ReadDirectoryVersion(first.DirectoryRoot);
            ObjectHeadDirectoryVersion changedDirectory = store.ReadDirectoryVersion(second.DirectoryRoot);
            Assert.Equal(ObjectVersionKind.Base, initialDirectory.Kind);
            Assert.Equal(ObjectVersionKind.Delta, changedDirectory.Kind);
            Assert.Equal(first.Address, changedDirectory.SourceGraph);
            Assert.Equal(first.DirectoryRoot, changedDirectory.Prior);
            Assert.Equal((long)initialDirectory.PayloadBytes + changedDirectory.PayloadBytes,
                store.ReadDirectoryPayloadBytes(second.DirectoryRoot));

            long appended = store.AppendedFrames;
            StoredGraphRevision reused = store.Append(StoredGraphKind.State, second.Address, schemaRoot,
                new(second.Address, new(1), []));
            Assert.Equal(appended + 1, store.AppendedFrames); // The new graph envelope is a real revision.
            Assert.Equal(second.DirectoryRoot, reused.DirectoryRoot);
            Assert.Null(reused.OwnedBatch);
            Assert.Equal(second.Address, reused.SourceGraph);
            Assert.Equal(second.ObjectHeads[1], reused.ObjectHeads[1]);
            Assert.Equal(first.ObjectHeads[2], reused.ObjectHeads[2]);

            firstAddress = first.Address;
            secondAddress = second.Address;
            reusedAddress = reused.Address;
            Verify(store);
            frames.ConfirmDurable();
        }

        using Frames reopened = Frames.OpenReadOnly(_path);
        using GraphRecordStore reader = new(reopened);
        Verify(reader);

        void Verify(GraphRecordStore store) {
            StoredGraphRevision first = store.ReadGraph(firstAddress);
            StoredGraphRevision second = store.ReadGraph(secondAddress);
            StoredGraphRevision reused = store.ReadGraph(reusedAddress);
            Assert.Equal(schemaRoot, reused.SchemaRoot);
            Assert.Equal(second.Address, reused.Parent);
            Assert.Equal(second.DirectoryRoot, reused.DirectoryRoot);
            Assert.NotEqual(first.DirectoryRoot, second.DirectoryRoot);
            Assert.Equal(64, reused.ObjectHeads.Count);
            ObjectVersionChain chain = store.ReadObjectChain(reused.Address, 1);
            Assert.Equal(2, chain.Entries.Count);
            Assert.Equal(first.OwnedBatch, chain.Entries[0].ContainingBatch);
            Assert.Equal(second.OwnedBatch, chain.Entries[1].ContainingBatch);
            Assert.Equal(first.Address, chain.Entries[1].SourceGraph);
            Assert.Equal(schemaRoot, chain.Entries[0].SchemaRoot);
            Assert.Equal(new byte[] { 1 }, chain.Entries[0].Record.Body.ToArray());
            Assert.Equal(new byte[] { 65, 66 }, chain.Entries[1].Record.Body.ToArray());
            Assert.Equal(ObjectVersionPayloadSize.GetBasePayloadBytes(1) +
                ObjectVersionPayloadSize.GetDeltaPayloadBytes(2, first.ObjectHeads[1]), chain.ReconstructionPayloadBytes);
            Assert.Single(store.ReadObjectChain(reused.Address, 2).Entries);
        }
    }

    [Fact]
    public void Directory_Base_choice_and_explicit_removes_are_independent_of_object_Delta_choice() {
        using Frames frames = Frames.Create(_path);
        using GraphRecordStore store = new(frames) { DirectoryReadAmplificationThreshold = 1 };
        StoredGraphRevision first = store.Append(StoredGraphKind.State, null, null,
            new(null, new(1), Enumerable.Range(1, 64).Select(i => B((uint)i, [(byte)i]))));
        StoredGraphRevision second = store.Append(StoredGraphKind.State, first.Address, null,
            new(first.Address, new(1), [D(1, first.ObjectHeads[1], [65])]));
        ObjectHeadDirectoryVersion rebased = store.ReadDirectoryVersion(second.DirectoryRoot);
        Assert.Equal(ObjectVersionKind.Base, rebased.Kind);
        Assert.Null(rebased.Prior);
        Assert.Equal(first.Address, rebased.SourceGraph);
        Assert.Equal(ObjectVersionKind.Delta, store.ReadBatch(second.OwnedBatch!.Value).Records[0].Kind);

        StoredGraphRevision third = store.Append(StoredGraphKind.State, second.Address, null,
            new(second.Address, new(1), [B(65, [66])], Enumerable.Range(2, 63).Select(i => (uint)i)));
        Assert.Equal(new uint[] { 1, 65 }, third.ObjectHeads.Keys.Order().ToArray());
        Assert.Equal(second.ObjectHeads[1], third.ObjectHeads[1]);
        Assert.Equal(third.OwnedBatch, third.ObjectHeads[65]);
        Assert.Equal(64, store.ReadGraph(first.Address).ObjectHeads.Count);
        Assert.Equal(64, store.ReadGraph(second.Address).ObjectHeads.Count);
        Assert.Equal(2, store.ReadObjectChain(third.Address, 1).Entries.Count);
        Assert.Single(store.ReadObjectChain(third.Address, 65).Entries);
    }

    private static ObjectVersionRecord B(uint id, byte[] body) => ObjectVersionRecord.CreateBase(id, body);
    private static ObjectVersionRecord D(uint id, FrameAddress prior, byte[] body) => ObjectVersionRecord.CreateDelta(id, prior, body);
    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}
