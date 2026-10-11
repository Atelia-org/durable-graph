using System.Reflection;
using Atelia.Data;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed partial class EventHistoryRepositoryTests {


    [Theory]
    [InlineData("Upper")]
    [InlineData("bad/name")]
    [InlineData("main.lock")]
    [InlineData("trailing.")]
    public void InvalidBranchNameIsRejectedBeforeCaptureOrWrites(string name) {
        using (Repository empty = CreateRepository()) { }
        var before = SnapshotFiles();
        int captures = 0;
        using (Repository repository = Repository.OpenExisting(_root, Models(_ => captures++))) {
            Assert.ThrowsAny<Exception>(() => repository.CreateBranch(name, new Node(), NoRebase));
            Assert.Equal(0, captures);
            Assert.False(repository.IsFaulted);
            Assert.Empty(repository.ListBranches());
        }
        AssertFiles(before);
        using Repository retry = Repository.OpenExisting(_root, Models());
        using var valid = retry.CreateBranch("main", new Node(), NoRebase);
    }

    [Theory]
    [InlineData((int)CommitCheckpoint.AfterPublication)]
    [InlineData((int)CommitCheckpoint.BeforeInstall)]
    public void PublishedEventFailureReopensAsPendingWithoutAdvancingStateBaseline(int point) {
        FrameAddress state;
        using (Repository repository = CreateRepository()) {
            using var session = repository.CreateBranch("main", new Node { Value = 1 }, NoRebase);
            state = session.StateRevisionAddress!.Value;
            repository.Checkpoint = checkpoint => {
                if (checkpoint == (CommitCheckpoint)point) throw new IOException("Event published but completion interrupted");
            };
            var failure = Assert.Throws<GraphCommitException>(() => session.CommitEvent(new Node { Value = 8 }, NoRebase));
            Assert.Equal(GraphCommitOutcome.Published, failure.Outcome);
            Assert.Equal(state, session.StateRevisionAddress!.Value);
            Assert.True(repository.IsFaulted);
        }
        using Repository reopened = Repository.OpenExisting(_root, Models());
        using var resumed = reopened.Checkout("main");
        Assert.Equal(state, resumed.StateRevisionAddress!.Value);
        Assert.Equal((byte)1, ((Node)resumed.State!).Value);
        Assert.Equal((byte)8, ((Node)reopened.ReadEvent(resumed.Head)).Value);
        resumed.CommitState(NoRebase);
        Assert.Equal(GraphFrameKind.State, resumed.Head.Kind);
    }

}
