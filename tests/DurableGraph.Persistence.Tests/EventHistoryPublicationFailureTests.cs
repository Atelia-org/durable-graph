using static Atelia.DurableGraph.Persistence.Tests.DB096TestAccess;

using System.Reflection;
using Atelia.Data;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed partial class EventHistoryRepositoryTests {


    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void InvalidDisplayLabelIsRejectedBeforeCaptureOrWrites(int caseId) {
        string name = caseId switch { 0 => "", 1 => new string('x', 1025), 2 => new string((char)0xD800, 1), _ => new string((char)0xDC00, 1) };
        using (Repository empty = CreateRepository()) { }
        var before = SnapshotFiles();
        int captures = 0;
        using (Repository repository = Repository.OpenExisting(_root, Models(_ => captures++))) {
            Assert.ThrowsAny<Exception>(() => repository.OpenWorkingCopy(repository.CreateRootBranchFromState(name, new Node(), parameters: NoRebase).Id));
            Assert.Equal(0, captures);
            Assert.False(repository.IsFaulted);
            Assert.Empty(repository.ListBranches());
        }
        AssertFiles(before);
        using Repository retry = Repository.OpenExisting(_root, Models());
        using var valid = retry.OpenWorkingCopy(retry.CreateRootBranchFromState("main", new Node(), parameters: NoRebase).Id);
    }

    [Theory]
    [InlineData((int)RepositorySaveStage.AfterPublication)]
    [InlineData((int)RepositorySaveStage.BeforeInstall)]
    public void PublishedEventFailureReopensAsPendingWithoutAdvancingStateBaseline(int point) {
        FrameAddress state;
        using (Repository repository = CreateRepository()) {
            using var session = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", new Node { Value = 1 }, parameters: NoRebase).Id);
            state = session.StateRevisionAddress!.Value;
            repository.SaveStageObserver = checkpoint => {
                if (checkpoint == (RepositorySaveStage)point) throw new IOException("Event published but completion interrupted");
            };
            var failure = Assert.Throws<RepositoryPublicationException>(() => session.CommitEvent(new Node { Value = 8 }).Id);
            Assert.Equal(RepositoryPublicationOutcome.Published, failure.Outcome);
            Assert.Equal(state, session.StateRevisionAddress!.Value);
            Assert.True(repository.IsFaulted);
        }
        using Repository reopened = Repository.OpenExisting(_root, Models());
        using var resumed = reopened.OpenWorkingCopy(BranchNamed(reopened, "main"));
        Assert.Equal(state, resumed.StateRevisionAddress!.Value);
        Assert.Equal((byte)1, ((Node)resumed.State!).Value);
        Assert.Equal((byte)8, ((Node)reopened.LoadEvent(resumed.BaseCommitId)).Value);
        resumed.CommitState(parameters: NoRebase);
        Assert.Equal(CommitKind.State, WorkTestAccess.Kind(resumed));
    }

}
