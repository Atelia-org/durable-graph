using Atelia.Data;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using Atelia.RbfSegmentStore;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;

namespace Atelia.DurableGraph.Persistence.Tests;

/// <summary>Deliberately narrower views test confinement, not permission to discard complete facts.</summary>
public sealed class DB085S0BReaderBoundaryTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), "durablegraph-db085-s0b-reader-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FreshStateReaderCannotReadCompleteHiddenRevisionDirectlyOrIndirectly(bool warmOldCache) {
        FrameAddress first, second;
        using (var segments = SegmentStore.CreateNew(_root, new() { NewStoreLayout = RbfSegmentStoreLayout.Flat })) {
            using var states = new StateRevisionStore(segments);
            first = states.AppendDurably(StateRevision.CreateObjectHeadMapBase(null,
                [ObjectVersionRecord.CreateBase(1, [4, 1, 0])], []));
            second = states.AppendDurably(StateRevision.CreateObjectHeadMapDelta(first,
                [ObjectVersionRecord.CreateDelta(1, first, [42])], []));
            if (warmOldCache) {
                Assert.Equal(2, states.ReadObjectVersionChain(second, 1).Records.Count);
                Assert.Single(states.ReadLiveObjectHeadMap(second));
                Assert.Equal(first, states.Read(second).ParentRevisionAddress);
            }
        }
        string path = Directory.GetFiles(_root, "*.rbf", SearchOption.AllDirectories).Single();
        byte[] before = File.ReadAllBytes(path);
        using (var full = RbfFile.OpenReadOnlyExisting(path)) {
            using var frame = full.ReadPooledFrame(second.FrameTicket).Unwrap();
            Assert.Equal(StateRevisionStore.RbfTag, frame.Tag);
        }
        long end = first.FrameTicket.Offset + first.FrameTicket.Length + 4;
        long bytesRequested = 0;
        var bounded = RbfFile.OpenReadOnlyCandidate(path, end, beforeRead: count => bytesRequested += count);
        using (var segments = SegmentStore.OpenReadOnlyCandidate(_root, RbfSegmentStoreLayout.Flat, bounded)) {
            using var states = new StateRevisionStore(segments); // This is a fresh cache, deliberately enabled.
            Assert.Single(states.ReadLiveObjectHeadMap(first));
            Assert.Single(states.ReadObjectVersionChain(first, 1).Records);
            Assert.Single(states.ReadLiveObjectHeadMap(first)); // Own cache hit remains confined.
            long readsBeforeRejection = bytesRequested;
            Assert.ThrowsAny<Exception>(() => states.Read(second));
            Assert.ThrowsAny<Exception>(() => states.ReadLiveObjectHeadMap(second));
            Assert.ThrowsAny<Exception>(() => states.ReadObjectVersionChain(second, 1));
            Assert.Equal(readsBeforeRejection, bytesRequested);
            Assert.Throws<InvalidOperationException>(() => segments.OpenActiveWriter());
        }
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void FixedViewRefusesMissingFenceEvenWhenTicketBodyIsEntirelyVisible() {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "fence.rbf");
        SizedPtr ticket;
        using (var writer = RbfFile.CreateNew(path)) {
            ticket = writer.Append(1, [1, 2, 3, 4]).Unwrap();
            writer.DurableFlush();
        }
        byte[] before = File.ReadAllBytes(path);
        // Factory may reject this non-boundary immediately; if it permits a byte view,
        // every frame entry must still reject the ticket before renting its payload.
        IRbfFile? view = null;
        try {
            view = RbfFile.OpenReadOnlyCandidate(path, ticket.Offset + ticket.Length);
            Assert.True(view.ReadPooledFrame(ticket).IsFailure);
            Assert.True(view.ReadFrameInfo(ticket).IsFailure);
        }
        catch (InvalidDataException) { }
        finally { view?.Dispose(); }
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    public void Dispose() {
        if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); }
    }
}
