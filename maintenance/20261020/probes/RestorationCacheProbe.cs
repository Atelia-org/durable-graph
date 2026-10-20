using Atelia.Data;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using Xunit.Abstractions;
using Frames = Atelia.FrameStore.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

// Temporary audit witness. Copy into this test assembly, run, then remove the copy.
// Assertions describe the inspected implementation's costs, not the desired optimized cost.
public sealed class Audit03RestorationCacheProbe(ITestOutputHelper output) {
    [Theory]
    [InlineData(16, 0L)]
    [InlineData(32, 0L)]
    [InlineData(16, 1024L)]
    [InlineData(32, 1024L)]
    [InlineData(16, GraphRecordStore.DefaultReadCacheBudgetBytes)]
    [InlineData(32, GraphRecordStore.DefaultReadCacheBudgetBytes)]
    public void OneBatchQualificationAndChainEnumerationCountPhysicalProductions(int count, long budget) {
        const string prefix = "dg-audit03-cache-probe-";
        string path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        try {
            using Frames frames = Frames.Create(path);
            ObjectVersionRecord[] records = Enumerable.Range(1, count)
                .Select(id => ObjectVersionRecord.CreateBase((uint)id, new byte[] { 10 })).ToArray();
            FrameAddress batch = frames.Append(GraphFrameCodec.BatchTag,
                GraphFrameCodec.EncodeBatch(null, null, records)).Unwrap();
            SortedDictionary<uint, FrameAddress> heads = new(records.ToDictionary(row => row.ObjectId, _ => batch));
            FrameAddress directory = frames.Append(GraphFrameCodec.DirectoryTag,
                GraphFrameCodec.EncodeDirectory(null, ObjectVersionKind.Base, null,
                    ObjectHeadDirectoryCodec.PrepareBase(heads).Body)).Unwrap();
            FrameAddress graph = frames.Append(GraphFrameCodec.GraphTag,
                GraphFrameCodec.EncodeGraph(StoredGraphKind.State, null, new(1), directory, null, batch)).Unwrap();
            using GraphRecordStore reader = new(frames, readCacheBudgetBytes: budget);
            StoredGraphRevision snapshot = reader.ReadBusinessGraph(graph, null);
            GraphPhysicalReadStatistics qualified = reader.ReadCacheStatistics;
            int chains = 0;
            foreach (ObjectVersionChain chain in reader.ReadObjectChains(snapshot)) {
                Assert.Single(chain.Entries);
                chains++;
            }
            GraphPhysicalReadStatistics enumerated = reader.ReadCacheStatistics;
            Assert.Equal(count, chains);
            bool admitted = budget == GraphRecordStore.DefaultReadCacheBudgetBytes;
            long expectedQualified = admitted ? 1L : count + 1L;
            long expectedEnumerated = admitted ? 1L : 2L * count + 1L;
            output.WriteLine($"N={count}; budget={budget}; qualification BatchDecodes={qualified.BatchDecodes}; " +
                $"after chains BatchDecodes={enumerated.BatchDecodes}; bypasses={enumerated.AdmissionBypasses}");
            Assert.Equal(expectedQualified, qualified.BatchDecodes);
            Assert.Equal(expectedEnumerated, enumerated.BatchDecodes);
            Assert.Equal(0, reader.AppendedFrames);
        } finally {
            SharedReadModel.DeleteFixture(path, prefix);
        }
    }
}
