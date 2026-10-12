using Atelia.DurableGraph.Persistence.MetaBootstrapP0;
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
            using P0GraphStore store = new(frames) { DirectoryReadAmplificationThreshold = 100 };
            schemaRoot = store.Append(P0GraphKind.Meta, null, null,
                new(null, new(1), [B(1, [0])])).Address;
            P0GraphSnapshot first = store.Append(P0GraphKind.State, null, schemaRoot,
                new(null, new(1), Enumerable.Range(1, 64).Select(i => B((uint)i, [(byte)i]))));
            P0GraphSnapshot second = store.Append(P0GraphKind.State, first.Address, schemaRoot,
                new(first.Address, new(1), [D(1, first.Objects[1], [65, 66])]));
            P0DirectoryVersion initialDirectory = store.ReadDirectoryVersion(first.DirectoryRoot);
            P0DirectoryVersion changedDirectory = store.ReadDirectoryVersion(second.DirectoryRoot);
            Assert.Equal(ObjectVersionKind.Base, initialDirectory.Kind);
            Assert.Equal(ObjectVersionKind.Delta, changedDirectory.Kind);
            Assert.Equal(first.Address, changedDirectory.SourceGraph);
            Assert.Equal(first.DirectoryRoot, changedDirectory.Prior);
            Assert.Equal((long)initialDirectory.PayloadBytes + changedDirectory.PayloadBytes,
                store.ReadDirectoryPayloadBytes(second.DirectoryRoot));

            long appended = store.AppendedFrames;
            P0GraphSnapshot reused = store.Append(P0GraphKind.State, second.Address, schemaRoot,
                new(second.Address, new(1), []));
            Assert.Equal(appended + 1, store.AppendedFrames); // The new graph envelope is a real revision.
            Assert.Equal(second.DirectoryRoot, reused.DirectoryRoot);
            Assert.Null(reused.OwnedBatch);
            Assert.Equal(second.Address, reused.SourceGraph);
            Assert.Equal(second.Objects[1], reused.Objects[1]);
            Assert.Equal(first.Objects[2], reused.Objects[2]);

            firstAddress = first.Address;
            secondAddress = second.Address;
            reusedAddress = reused.Address;
            Verify(store);
            frames.ConfirmDurable();
        }

        using Frames reopened = Frames.OpenReadOnly(_path);
        using P0GraphStore reader = new(reopened);
        Verify(reader);

        void Verify(P0GraphStore store) {
            P0GraphSnapshot first = store.ReadGraph(firstAddress);
            P0GraphSnapshot second = store.ReadGraph(secondAddress);
            P0GraphSnapshot reused = store.ReadGraph(reusedAddress);
            Assert.Equal(schemaRoot, reused.SchemaRoot);
            Assert.Equal(second.Address, reused.Parent);
            Assert.Equal(second.DirectoryRoot, reused.DirectoryRoot);
            Assert.NotEqual(first.DirectoryRoot, second.DirectoryRoot);
            Assert.Equal(64, reused.Objects.Count);
            P0ObjectChain chain = store.ReadObjectChain(reused.Address, 1);
            Assert.Equal(2, chain.Entries.Count);
            Assert.Equal(first.OwnedBatch, chain.Entries[0].ContainingBatch);
            Assert.Equal(second.OwnedBatch, chain.Entries[1].ContainingBatch);
            Assert.Equal(first.Address, chain.Entries[1].SourceGraph);
            Assert.Equal(schemaRoot, chain.Entries[0].SchemaRoot);
            Assert.Equal(new byte[] { 1 }, chain.Entries[0].Record.Body.ToArray());
            Assert.Equal(new byte[] { 65, 66 }, chain.Entries[1].Record.Body.ToArray());
            Assert.Equal(ObjectVersionPayloadSize.GetBasePayloadBytes(1) +
                ObjectVersionPayloadSize.GetDeltaPayloadBytes(2, first.Objects[1]), chain.ReconstructionPayloadBytes);
            Assert.Single(store.ReadObjectChain(reused.Address, 2).Entries);
        }
    }

    [Fact]
    public void Directory_Base_choice_and_explicit_removes_are_independent_of_object_Delta_choice() {
        using Frames frames = Frames.Create(_path);
        using P0GraphStore store = new(frames) { DirectoryReadAmplificationThreshold = 1 };
        P0GraphSnapshot first = store.Append(P0GraphKind.State, null, null,
            new(null, new(1), Enumerable.Range(1, 64).Select(i => B((uint)i, [(byte)i]))));
        P0GraphSnapshot second = store.Append(P0GraphKind.State, first.Address, null,
            new(first.Address, new(1), [D(1, first.Objects[1], [65])]));
        P0DirectoryVersion rebased = store.ReadDirectoryVersion(second.DirectoryRoot);
        Assert.Equal(ObjectVersionKind.Base, rebased.Kind);
        Assert.Null(rebased.Prior);
        Assert.Equal(first.Address, rebased.SourceGraph);
        Assert.Equal(ObjectVersionKind.Delta, store.ReadBatch(second.OwnedBatch!.Value).Records[0].Kind);

        P0GraphSnapshot third = store.Append(P0GraphKind.State, second.Address, null,
            new(second.Address, new(1), [B(65, [66])], Enumerable.Range(2, 63).Select(i => (uint)i)));
        Assert.Equal(new uint[] { 1, 65 }, third.Objects.Keys.Order().ToArray());
        Assert.Equal(second.Objects[1], third.Objects[1]);
        Assert.Equal(third.OwnedBatch, third.Objects[65]);
        Assert.Equal(64, store.ReadGraph(first.Address).Objects.Count);
        Assert.Equal(64, store.ReadGraph(second.Address).Objects.Count);
        Assert.Equal(2, store.ReadObjectChain(third.Address, 1).Entries.Count);
        Assert.Single(store.ReadObjectChain(third.Address, 65).Entries);
    }

    private static ObjectVersionRecord B(uint id, byte[] body) => ObjectVersionRecord.CreateBase(id, body);
    private static ObjectVersionRecord D(uint id, FrameAddress prior, byte[] body) => ObjectVersionRecord.CreateDelta(id, prior, body);
    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}
