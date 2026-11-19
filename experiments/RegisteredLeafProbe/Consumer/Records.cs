using System.Reflection;
using System.Security.Cryptography;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Probe.Consumer;

// Post-timing, read-only inspection of actual committed frames. The leaf and host model use public APIs exclusively.
internal static class Records {
    internal sealed record Witness(string Kind, int BodyBytes, string BodySha256, bool ExactPredecessor);

    internal static Witness[] Inspect(Repository repository, List<GraphRef> refs) {
        var reads = refs.Select(r => Read(repository, r)).ToArray();
        // Only the text changes after the initial snapshot. Its real row ID comes from the first edit.
        uint leafId = reads[1].Records.Single().ObjectId;
        var result = new List<Witness>();
        for (int i = 0; i < reads.Length; i++) {
            var record = reads[i].Records.Single(r => r.ObjectId == leafId);
            bool correct = i == 0 ? record.Kind == ObjectVersionKind.Base && record.PriorAddress is null :
                record.Kind == ObjectVersionKind.Delta && record.PriorAddress == reads[i - 1].Batch;
            Program.Need(correct, $"unexpected {record.Kind} or incorrect predecessor for changed row at save {i}");
            result.Add(new(record.Kind.ToString(), record.Body.Length, Convert.ToHexString(SHA256.HashData(record.Body)), correct));
        }
        return result.ToArray();
    }

    internal static object InspectMixedEdit(Repository repository, GraphRef original, GraphRef next) {
        var first = Read(repository, original);
        var hot = Read(repository, next);
        Program.Need(hot.Records.Count == 2, "expected exactly the changed text and bitmap rows");
        foreach (var row in hot.Records) {
            Program.Need(row.Kind == ObjectVersionKind.Delta && row.PriorAddress == first.Batch &&
                first.Records.Any(r => r.ObjectId == row.ObjectId && r.Kind == ObjectVersionKind.Base),
                "mixed graph delta must point to the same object's base");
        }
        // Probe-defined encoding: change byte zero from 0x01 to 0x03.
        var bitmap = hot.Records.Single(r => r.Body.SequenceEqual(new byte[] { 0, 1, 0, 3 }));
        var text = hot.Records.Single(r => r.ObjectId != bitmap.ObjectId);
        return new { textDeltaBytes = text.Body.Length, bitmapDelta = Convert.ToHexString(bitmap.Body), exactPredecessors = true };
    }

    private static (FrameAddress Batch, IReadOnlyList<ObjectVersionRecord> Records) Read(Repository repository, GraphRef reference) {
        const BindingFlags access = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        static object Property(object obj, string name) => obj.GetType().GetProperty(name, access)!.GetValue(obj)!;
        static object Invoke(object obj, string name, object arg) => obj.GetType().GetMethod(name, access,
            binder: null, types: [arg.GetType()], modifiers: null)!.Invoke(obj, [arg])!;
        var resources = typeof(Repository).GetField("_resources", access)!.GetValue(repository)!;
        var store = Property(repository, "GraphStore");
        var commit = Property(reference, "CommitId");
        var descriptor = Invoke(resources, "ReadHeapDescriptor", Property(commit, "Value"));
        var revision = Invoke(store, "ReadBusinessGraph", Property(descriptor, "Address"));
        var batch = (FrameAddress)Property(revision, "OwnedBatch");
        return (batch, (IReadOnlyList<ObjectVersionRecord>)Property(Invoke(store, "ReadBatch", batch), "Records"));
    }
}
