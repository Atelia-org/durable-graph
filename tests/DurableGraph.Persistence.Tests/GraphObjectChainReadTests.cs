using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using Frames = Atelia.FrameStore.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class GraphObjectChainReadTests : IDisposable {
    private const string Prefix = "dg-object-chain-read-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(0L)]
    [InlineData(GraphRecordStore.DefaultReadCacheBudgetBytes)]
    public void DecoderReadsTheCompleteBaseGraphOnceAndPreservesEveryRowHeadAndActualH(long cacheBudget) {
        using Frames frames = Frames.Create(_path);
        ObjectVersionRecord[] records = Enumerable.Range(1, 32)
            .Select(id => StringRecord((uint)id, $"value-{id}"))
            .ToArray();
        StoredGraphRevision written;
        using (GraphRecordStore writer = new(frames)) {
            written = writer.Append(StoredGraphKind.Meta, null, null, new(null, new(1), records));
        }
        using GraphRecordStore reader = new(frames, readCacheBudgetBytes: cacheBudget);
        DecodedGraphRows decoded = StoredGraphDecoder.Read(reader, written.Address);
        AssertComplete(decoded);
        GraphPhysicalReadStatistics first = reader.ReadCacheStatistics;
        Assert.Equal(1, first.GraphDecodes);
        Assert.Equal(1, first.DirectoryMaterializations);
        Assert.Equal(0, reader.AppendedFrames);

        AssertComplete(StoredGraphDecoder.Read(reader, written.Address));
        GraphPhysicalReadStatistics second = reader.ReadCacheStatistics;
        if (cacheBudget == 0) {
            Assert.Equal(first.GraphDecodes + 1, second.GraphDecodes);
            Assert.Equal(first.DirectoryMaterializations + 1, second.DirectoryMaterializations);
        } else {
            Assert.Equal(first.GraphDecodes, second.GraphDecodes);
            Assert.Equal(first.DirectoryMaterializations, second.DirectoryMaterializations);
            Assert.Equal(first.GraphHits + 1, second.GraphHits);
            Assert.Equal(first.DirectoryHits + 1, second.DirectoryHits);
        }
        Assert.Equal(0, reader.AppendedFrames);

        void AssertComplete(DecodedGraphRows actual) {
            Assert.Equal(written.Address, actual.Snapshot.Address);
            Assert.Equal(written.RootId, actual.Snapshot.RootId);
            Assert.Same(reader, actual.Snapshot.SourceOwner);
            Assert.Equal(records.Length, actual.Rows.Count);
            Assert.Equal(records.Length, actual.Storage.Count);
            Assert.Equal(written.ObjectHeads.ToArray(), actual.Snapshot.ObjectHeads.ToArray());
            foreach (ObjectVersionRecord record in records) {
                ObjectId id = new(record.ObjectId);
                Assert.Equal($"value-{record.ObjectId}", actual.Rows[id].StringContent);
                Assert.Equal(written.ObjectHeads[record.ObjectId], actual.Storage[id].Head);
                Assert.Equal(ObjectVersionPayloadSize.GetBasePayloadBytes(record.Body.Length),
                    actual.Storage[id].ReconstructionPayloadBytes);
            }
        }
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(GraphRecordStore.DefaultReadCacheBudgetBytes)]
    public void BulkReadRejectsAnUnreachableWrongRoleHeadBeforeReturningTheIterator(long cacheBudget) {
        using Frames frames = Frames.Create(_path);
        using GraphRecordStore writer = new(frames);
        StoredGraphRevision first = writer.Append(StoredGraphKind.State, null, null,
            new(null, new(1), [StringRecord(1, "root"), StringRecord(2, "unreachable")]));
        var heads = new Dictionary<uint, FrameAddress> {
            [1] = first.ObjectHeads[1],
            [2] = first.DirectoryRoot,
        };
        FrameAddress directory = frames.Append(GraphFrameCodec.DirectoryTag,
            GraphFrameCodec.EncodeDirectory(first.Address, ObjectVersionKind.Base, null,
                ObjectHeadDirectoryCodec.PrepareBase(heads).Body)).Unwrap();
        FrameAddress bad = frames.Append(GraphFrameCodec.GraphTag,
            GraphFrameCodec.EncodeGraph(StoredGraphKind.State, first.Address, new(1), directory, null, null)).Unwrap();
        using GraphRecordStore reader = new(frames, readCacheBudgetBytes: cacheBudget);
        reader.ReadGraph(first.Address);
        reader.ReadMetadata(bad);
        reader.ReadDirectory(directory);

        IEnumerable<ObjectVersionChain>? delivered = null;
        Assert.Throws<InvalidDataException>(() => delivered = reader.ReadObjectChains(bad, out _));
        Assert.Null(delivered);
        DecodedGraphRows? decoded = null;
        Assert.Throws<InvalidDataException>(() => decoded = StoredGraphDecoder.Read(reader, bad));
        Assert.Null(decoded);
        Assert.Equal(0, reader.AppendedFrames);
        Assert.Equal("root", StoredGraphDecoder.Read(reader, first.Address, new StateModelRegistry().CreateEnvironment())
            .Rows[new(1)].StringContent);
    }

    private static ObjectVersionRecord StringRecord(uint id, string value) => ObjectVersionRecord.CreateBase(id,
        TypedBaseBodyCodec.Encode(ObjectLayout.String, StringBodyPreparer.PrepareBase(value)));

    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}
