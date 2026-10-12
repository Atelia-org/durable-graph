using System.Text.Json;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using Atelia.RbfSegmentStore;
using Xunit.Abstractions;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;
using Node = Atelia.DurableGraph.Persistence.Tests.SharedReadModel.Node;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class DB085S0BCostTests(ITestOutputHelper output) {
    [Fact]
    public void MeasureQualificationStagesAgainstIndependentAudit() {
        // All sources have just been written: OS/device cache is uncontrolled, not cold.
        // One warmup then three observations; timing is evidence, never a pass threshold.
        foreach (var cell in new[] {
            new Cell("baseline", 0, 1, 0, 0),
            new Cell("shortTail", 0, 1, 0, 0, true),
            new Cell("history8", 8, 1, 0, 0),
            new Cell("history32", 32, 1, 0, 0),
            new Cell("live32", 0, 32, 0, 0),
            new Cell("orphans32", 0, 1, 32, 0),
            new Cell("largeFrame", 0, 1, 1, 1024 * 1024),
        }) {
            string root = Path.Combine(Path.GetTempPath(), "durablegraph-db085-s0b-cost-" + Guid.NewGuid().ToString("N"));
            try {
                Node first = new() { Value = 1 };
                Node cursor = first;
                for (int i = 1; i < cell.LiveNodes; i++) { cursor.Next = new() { Value = (byte)i }; cursor = cursor.Next; }
                using (var repository = Repository.CreateNew(root, SharedReadModel.Models(),
                    new() { NewStoreLayout = RbfSegmentStoreLayout.Flat })) {
                    using var branch = repository.CreateBranch("main", first, new(1000000, 1));
                    for (int i = 0; i < cell.Commits; i++) {
                        first.Value++;
                        branch.CommitState(first);
                    }
                }
                if (cell.Orphans > 0) {
                    using var segments = SegmentStore.OpenExisting(Path.Combine(root, "state"));
                    using var states = new StateRevisionStore(segments);
                    byte[] body = new byte[2 + cell.BodyBytes];
                    body[0] = 4; body[1] = 1; // Opaque string Base; structural checks do not interpret its body.
                    for (int i = 0; i < cell.Orphans; i++) {
                        states.AppendDurably(StateRevision.CreateObjectHeadMapBase(null,
                            [ObjectVersionRecord.CreateBase(1, body)], []));
                    }
                }
                Assert.True(RepositoryMaintenance.Audit(root).IsHealthy); // Outside every timed candidate stage.
                if (cell.ShortTail) {
                    string path = Directory.GetFiles(Path.Combine(root, "state"), "*.rbf", SearchOption.AllDirectories).Single();
                    long end = new FileInfo(path).Length;
                    using (var writer = RbfFile.OpenExisting(path)) { writer.Append(StateRevisionStore.RbfTag, new byte[64]).Unwrap(); writer.DurableFlush(); }
                    byte[] bytes = File.ReadAllBytes(path);
                    File.WriteAllBytes(path, bytes[..checked((int)end + 2)]);
                    Assert.False(RepositoryMaintenance.Audit(root).IsHealthy);
                }
                var before = Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
                for (int sample = -1; sample < 3; sample++) {
                    var report = RepositoryRecoveryQualification.Qualify(root);
                    Assert.Equal("Qualified", report.Status.ToString());
                    if (sample >= 0) { output.WriteLine("S0B_COST " + JsonSerializer.Serialize(new { Cell = cell, Sample = sample, Report = report })); }
                }
                Assert.Equal(before.Keys.Order(), Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order());
                foreach (var pair in before) { Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key)); }
            }
            finally { if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
        }
    }

    private sealed record Cell(string Name, int Commits, int LiveNodes, int Orphans, int BodyBytes, bool ShortTail = false);
}
