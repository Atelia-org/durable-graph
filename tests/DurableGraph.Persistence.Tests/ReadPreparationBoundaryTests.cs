using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Serialization;
using Node = Atelia.DurableGraph.Persistence.Tests.SharedReadModel.Node;
using State = Atelia.DurableGraph.Persistence.Tests.SharedReadModel.State;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class ReadPreparationBoundaryTests {
    [Fact]
    public void ReadingStateEventAndCheckpointNeverInvokesSaveBodyPreparers() {
        const string prefix = "dg-read-preparation-boundary-";
        string path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        try {
            CommitId state, message;
            using (Repository writer = Repository.CreateNew(path, SharedReadModel.Models())) {
                BranchInfo branch = writer.CreateRootBranchFromState("main", new Node { Value = 10 });
                using WorkingCopy work = writer.OpenWorkingCopy(branch.Id);
                state = work.BaseCommitId;
                message = work.CommitEvent(new Node { Value = 20 }).Id;
            }
            int baseCalls = 0, deltaCalls = 0;
            StateModelRegistry models = SharedReadModel.Models(
                prepareBase: (in State _) => { baseCalls++; throw new InvalidOperationException("A read invoked a Base preparer."); },
                prepareDelta: (in State _, in State _) => { deltaCalls++; throw new InvalidOperationException("A read invoked a Delta preparer."); });
            using Repository reader = Repository.OpenReadOnlyExisting(path, models);
            Assert.Equal((byte)10, Assert.IsType<Node>(reader.LoadState(state)).Value);
            Assert.Equal((byte)20, Assert.IsType<Node>(reader.LoadEvent(message)).Value);
            EventCheckpoint checkpoint = Assert.IsType<EventCheckpoint>(reader.ReadCheckpoint(message));
            Assert.Equal((byte)10, Assert.IsType<Node>(checkpoint.PreviousState).Value);
            Assert.Equal((byte)20, Assert.IsType<Node>(checkpoint.Event).Value);
            Assert.NotSame(checkpoint.PreviousState, checkpoint.Event);
            Assert.Equal(0, baseCalls);
            Assert.Equal(0, deltaCalls);
            Assert.Equal(0, reader.GraphStore.AppendedFrames);
        } finally {
            SharedReadModel.DeleteFixture(path, prefix);
        }
    }
}
