using System.Text.Json;
using Atelia.DurableGraph.Storage;
using Xunit.Abstractions;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class DB085S0CCostTests(ITestOutputHelper output) {
    [Fact]
    public void MeasureRecoveryStagesWithFreshSourcesAndIndependentAudit() {
        // Every sample is freshly built because Recover can change its input. All inputs
        // were just written: OS/device cache is uncontrolled, not cold. No timing threshold.
        foreach (var cell in new[] {
            new Cell("baseline", 0, 1, 0, 0),
            new Cell("shortTail", 0, 1, 0, 0, true),
            new Cell("history8", 8, 1, 0, 0),
            new Cell("history32", 32, 1, 0, 0),
            new Cell("live32", 0, 32, 0, 0),
            new Cell("orphans32", 0, 1, 32, 0),
            new Cell("largeFrame", 0, 1, 1, 1024 * 1024),
        }) {
            for (int sample = -1; sample < 3; sample++) {
                using var fixture = new DB085S0CFixture(cell.Commits, cell.LiveNodes);
                if (cell.Orphans > 0) {
                    using var segments = SegmentStore.OpenExisting(Path.Combine(fixture.Root, "state"));
                    using var states = new StateRevisionStore(segments);
                    byte[] body = new byte[2 + cell.BodyBytes]; body[0] = 4; body[1] = 1;
                    for (int i = 0; i < cell.Orphans; i++) {
                        states.AppendDurably(StateRevision.CreateObjectHeadMapBase(null,
                            [ObjectVersionRecord.CreateBase(1, body)], []));
                    }
                }
                Assert.True(RepositoryMaintenance.Audit(fixture.Root).IsHealthy); // Independent, outside recovery metrics.
                var complete = fixture.Snapshot();
                if (cell.ShortTail) { fixture.PartialWrite("state"); }
                var report = RepositoryRecovery.Recover(fixture.Root, SharedReadModel.Models());
                using var deliveredWriter = report.Repository;
                Assert.Equal("Succeeded", report.Status.ToString());
                Assert.Equal(cell.ShortTail, report.ActionAttempted);
                Assert.Equal(cell.ShortTail, report.LengthChanged);
                Assert.Equal(cell.ShortTail, report.BarrierCompleted);
                Assert.Equal(!cell.ShortTail, report.NoAction);
                var qualification = Assert.IsType<RecoveryQualificationReport>(report.Qualification);
                Assert.Equal(qualification.Total.Reads.RawRequestedBytes + report.Action.Reads.RawRequestedBytes + report.StrictOpen.Reads.RawRequestedBytes,
                    report.Total.Reads.RawRequestedBytes);
                Assert.True(report.Confirmation.Elapsed <= report.StrictOpen.Elapsed);
                Assert.True(report.Confirmation.ThreadAllocatedBytes <= report.StrictOpen.ThreadAllocatedBytes);
                if (sample >= 0) {
                    output.WriteLine("S0C_COST " + JsonSerializer.Serialize(new {
                        Cell = cell, Sample = sample, report.Status, report.Detail, report.SelectedFile,
                        report.ActionAttempted, report.LengthChanged, report.BarrierCompleted, report.SourceMayHaveChanged,
                        report.NoAction, Qualification = qualification, report.Action, report.StrictOpen,
                        report.Confirmation, report.Total, report.BudgetScope, report.MeasurementScope,
                    }));
                }
                // Report serialization excludes the delivered writer. Dispose before Audit.
                report.Repository!.Dispose();
                fixture.AssertLockReleased();
                fixture.AssertUnchanged(complete);
                Assert.True(RepositoryMaintenance.Audit(fixture.Root).IsHealthy);
                fixture.AssertUnchanged(complete);
            }
        }
    }
    private sealed record Cell(string Name, int Commits, int LiveNodes, int Orphans, int BodyBytes, bool ShortTail = false);
}
