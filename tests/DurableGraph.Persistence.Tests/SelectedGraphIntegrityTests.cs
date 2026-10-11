using Atelia.DurableGraph.Storage;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed partial class EventHistoryRepositoryTests {
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RequiredGraphReadChecksRevisionParentEvenForCompleteBase(bool state, bool orphan) {
        FrameAddress first;
        ObjectId root;
        using (Repository seed = CreateRepository()) {
            using BranchCheckout main = seed.CreateBranch("main", new Node { Value = 1 }, NoRebase);
            first = main.Head.RevisionAddress;
            root = main.Head.RootId;
        }
        using (GraphResources resources = GraphResources.OpenExisting(_root)) {
            FrameAddress invalid = resources.States.Append(StateRevision.CreateObjectHeadMapBase(null,
                resources.States.Read(first).LocalObjects, []),
                GraphMetadataCodec.Encode(state ? GraphFrameKind.State : GraphFrameKind.Event, first, root));
            if (!orphan) { resources.PublishBranch("main", invalid, out _); }
            else { resources.Data.ConfirmDurable(); }
        }
        var before = SnapshotFiles();
        using (Repository reader = Repository.OpenReadOnlyExisting(_root, Models())) {
            var head = reader.GetHead("main");
            if (orphan) { Assert.Equal(first, head.RevisionAddress); Assert.NotNull(reader.ReadState(head)); }
            else {
                Assert.Throws<InvalidDataException>(() => reader.ReadCheckpoint(head));
                Assert.Throws<InvalidDataException>(() => reader.ReadPair(head, head));
                if (state) { Assert.Throws<InvalidDataException>(() => reader.ReadState(head)); }
                else { Assert.Throws<InvalidDataException>(() => reader.ReadEvent(head)); }
                Assert.False(reader.IsFaulted);
            }
        }
        AssertFiles(before);
        if (!orphan) {
            using Repository writer = Repository.OpenExisting(_root, Models());
            var head = writer.GetHead("main");
            Assert.Throws<InvalidDataException>(() => writer.Checkout("main"));
            Assert.Throws<InvalidDataException>(() => writer.Fork("invalid-fork", head));
            Assert.False(writer.IsFaulted);
            Assert.DoesNotContain("invalid-fork", writer.ListBranches());
        }
    }

    [Fact]
    public void EventOnlyHistoryDoesNotPermitEventRevisionAsStateParent() {
        FrameAddress first;
        ObjectId root;
        using (Repository seed = CreateRepository()) {
            using BranchCheckout main = seed.CreateBranchFromEvent("main", new Node { Value = 1 }, NoRebase);
            first = main.Head.RevisionAddress;
            root = main.Head.RootId;
        }
        using (GraphResources resources = GraphResources.OpenExisting(_root)) {
            FrameAddress invalid = resources.States.Append(StateRevision.CreateObjectHeadMapDelta(first, [], []),
                GraphMetadataCodec.Encode(GraphFrameKind.State, first, root));
            resources.PublishBranch("main", invalid, out _);
        }
        using Repository reader = Repository.OpenReadOnlyExisting(_root, Models());
        Assert.Throws<InvalidDataException>(() => reader.ReadState(reader.GetHead("main")));
        Assert.False(reader.IsFaulted);
    }
}
