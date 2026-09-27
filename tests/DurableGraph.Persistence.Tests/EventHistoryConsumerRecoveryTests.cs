using EventHistoryRecovery;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed partial class EventHistoryRepositoryTests {
    // Intentionally use omitted policies here and in the linked PendingRecovery source:
    // these tests execute the consumer recovery contract with the public default API.
    [Theory]
    [InlineData((int)CommitCheckpoint.AfterStateDurable, false)]
    [InlineData((int)CommitCheckpoint.AfterPublication, true)]
    [InlineData((int)CommitCheckpoint.BeforeInstall, true)]
    public void ConsumerRecoveryUsesFreshPendingOrSkipsAlreadyPublishedState(int point, bool published) {
        Node oldState = new() { Value = 10 }, oldEvent = new() { Value = 3 };
        using (Repository repository = CreateRepository()) {
            using var session = repository.CreateBranch("main", oldState);
            session.CommitEvent(oldEvent);
            repository.Checkpoint = checkpoint => {
                if (checkpoint == (CommitCheckpoint)point) throw new IOException("State commit interrupted");
            };
            var error = Assert.Throws<GraphCommitException>(() => PendingRecovery.Complete<Node>(repository, session,
                _ => { }, (state, pending) => state.Value -= Assert.IsType<Node>(pending).Value));
            Assert.Equal(published ? GraphCommitOutcome.Published : GraphCommitOutcome.NotPublished, error.Outcome);
            Assert.True(repository.IsFaulted);
            Assert.Equal((byte)7, oldState.Value); // Failed delivery does not roll back application memory.
        }

        var beforeRecovery = SnapshotFiles();
        using (Repository repository = Repository.OpenExisting(_root, Models())) {
            using var session = repository.Checkout("main");
            Assert.NotSame(oldState, ((Node)session.State!));
            Assert.Equal(published ? (byte)7 : (byte)10, ((Node)session.State!).Value);
            if (published) Assert.Equal(GraphFrameKind.State, session.Head.Kind);
            else {
                Assert.NotSame(oldEvent, repository.ReadEvent(session.Head));
                Assert.Equal((byte)3, ((Node)repository.ReadEvent(session.Head)).Value);
            }
            var head = session.Head.RevisionAddress;
            int rebuilds = 0, applications = 0;
            // This application index belongs to this fresh attempt, never to the abandoned graph.
            Dictionary<string, Node> transientIndex = new();
            bool completed = PendingRecovery.Complete<Node>(repository, session, state => {
                rebuilds++;
                transientIndex.Add("target", state);
            }, (state, pending) => {
                applications++;
                Assert.Same(state, transientIndex["target"]);
                Assert.NotSame(oldState, state);
                Assert.NotSame(oldEvent, pending);
                state.Value -= Assert.IsType<Node>(pending).Value;
            });
            Assert.Equal(!published, completed);
            Assert.Equal(1, rebuilds); // Even a State head needs its application Transient rebuilt.
            Assert.Equal(published ? 0 : 1, applications);
            Assert.Equal((byte)7, ((Node)session.State!).Value);
            Assert.Equal(GraphFrameKind.State, session.Head.Kind);
            if (published) {
                Assert.Equal(head, session.Head.RevisionAddress);
            }
        }
        // Sample only while closed: an open writer owns repository.lock exclusively.
        if (published) AssertFiles(beforeRecovery); // No replacement Event, extra State or physical append.
        using Repository verification = Repository.OpenExisting(_root, Models());
        using var verified = verification.Checkout("main");
        Assert.Equal((byte)7, ((Node)verified.State!).Value);
        Assert.Equal(GraphFrameKind.State, verified.Head.Kind);
        // Writable browsing may persist a derived Journal forward-plan cache; keep it outside
        // the zero-write observation of Resume + the application's no-Pending recovery branch.
        Assert.Equal(3, verification.ReadFrames("main").Count());
        Assert.Single(verification.EnumerateEvents(verification.GetHead("main"), HistoryOrder.OldestFirst).ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConsumerRecoveryAbandonsModifiedGraphAfterUnwrappedFailure(bool failDuringPrepare) {
        Node oldState = new() { Value = 10 }, oldEvent = new() { Value = 3 };
        InvalidOperationException original = new("Application or preparation failure");
        using (Repository repository = CreateRepository()) {
            using var session = repository.CreateBranch("main", oldState);
            session.CommitEvent(oldEvent);
        }
        var beforeAttempt = SnapshotFiles();
        using (Repository repository = Repository.OpenExisting(_root, Models())) {
            using var session = repository.Checkout("main");
            oldState = ((Node)session.State!);
            oldEvent = ((Node)repository.ReadEvent(session.Head));
            if (failDuringPrepare) repository.Checkpoint = checkpoint => {
                if (checkpoint == CommitCheckpoint.AfterPrepare) throw original;
            };
            var thrown = Assert.Throws<InvalidOperationException>(() => PendingRecovery.Complete<Node>(repository, session,
                _ => { }, (state, pending) => {
                    state.Value -= Assert.IsType<Node>(pending).Value;
                    if (!failDuringPrepare) throw original;
                }));
            Assert.Same(original, thrown);
            Assert.False(repository.IsFaulted);
            Assert.Equal((byte)7, oldState.Value);
            Assert.Equal((byte)3, ((Node)repository.ReadEvent(session.Head)).Value);
            // End the attempt despite a healthy repository: blindly applying again here would yield 4.
        }
        AssertFiles(beforeAttempt);

        using (Repository repository = Repository.OpenExisting(_root, Models())) {
            using var session = repository.Checkout("main");
            Assert.NotSame(oldState, ((Node)session.State!));
            Assert.NotSame(oldEvent, repository.ReadEvent(session.Head));
            Assert.Equal((byte)10, ((Node)session.State!).Value);
            Node? rebuilt = null;
            int applications = 0;
            Assert.True(PendingRecovery.Complete<Node>(repository, session, state => rebuilt = state, (state, pending) => {
                Assert.Same(rebuilt, state);
                applications++;
                state.Value -= Assert.IsType<Node>(pending).Value;
            }));
            Assert.Equal(1, applications);
            Assert.Equal((byte)7, ((Node)session.State!).Value);
            Assert.Equal(GraphFrameKind.State, session.Head.Kind);
            Assert.Single(repository.EnumerateEvents(repository.GetHead("main"), HistoryOrder.OldestFirst).ToArray());
            Assert.Equal(3, repository.ReadFrames("main").Count());
        }
        using Repository verification = Repository.OpenExisting(_root, Models());
        using var verified = verification.Checkout("main");
        Assert.Equal((byte)7, ((Node)verified.State!).Value);
        Assert.Equal(GraphFrameKind.State, verified.Head.Kind);
    }
}
