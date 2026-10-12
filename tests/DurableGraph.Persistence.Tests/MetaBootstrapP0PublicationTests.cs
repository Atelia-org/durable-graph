using Atelia.DurableGraph.Persistence.MetaBootstrapP0;
using Atelia.DurableGraph.Storage;

namespace Atelia.DurableGraph.Persistence.Tests;

// These bodies are deliberately opaque: this suite isolates real FS/VS
// publication. The generated Widget suite qualifies typed bootstrap separately.
public sealed class MetaBootstrapP0PublicationTests : IDisposable {
    private readonly string _path = Path.Combine(Path.GetTempPath(), "dg-bootstrap-publication-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("BeforeDataConfirm", 0, GraphCommitOutcome.NotPublished, false)]
    [InlineData("AfterAppend", 1, GraphCommitOutcome.Unknown, false)]
    [InlineData("AfterAppend", 2, GraphCommitOutcome.Unknown, true)]
    [InlineData("AfterConfirmed", 0, GraphCommitOutcome.Published, true)]
    public void Real_conditional_publication_cold_open_observes_both_old_or_both_new(
        string stage, int failOnAppend, GraphCommitOutcome expected, bool visible) {
        FrameAddress oldBusiness, oldMeta;
        long before;
        FrameAddress? candidate = null;
        using (var repository = P0Repository.CreateNew(_path)) {
            var first = Save(repository, 1);
            oldBusiness = first.Business.Address;
            oldMeta = repository.SharedSchemaRoot!.Value;
            before = DataBytes(repository.Data);
            using var pending = Prepare(repository, 2);
            int appends = 0;
            InstallPublicationFault(repository.Versions, stage, () => {
                if (stage != "AfterAppend" || ++appends == failOnAppend) { throw new IOException("P0 real publication window"); }
            });
            int installed = 0;
            var error = Assert.Throws<GraphCommitException>(() => repository.Commit(pending,
                _ => installed++, _ => installed++));
            Assert.Equal(expected, error.Outcome);
            Assert.True(repository.IsFaulted);
            Assert.Equal(0, installed);
            candidate = error.CandidateRevisionAddress;
            Assert.NotNull(candidate);
            Assert.ThrowsAny<Exception>(() => repository.GetHead("main"));
        }

        using var reopened = P0Repository.OpenExisting(_path);
        Assert.True(DataBytes(reopened.Data) > before); // Completed candidate data survives either publication result.
        FrameAddress business = reopened.GetHead("main")!.Value;
        FrameAddress meta = reopened.SharedSchemaRoot!.Value;
        Assert.Equal(visible ? candidate!.Value : oldBusiness, business);
        Assert.Equal(visible, meta != oldMeta);
        Assert.Equal(business, reopened.GetStateHead("main"));
        // Follow the actual newly opened graph/directory, not saved candidate bytes.
        Assert.Equal((byte)(visible ? 2 : 1), reopened.Store.ReadObjectChain(business, 1).Entries.Last().Record.Body[0]);
        Assert.Equal((byte)(visible ? 2 : 1), reopened.Store.ReadObjectChain(meta, 1).Entries.Last().Record.Body[0]);
        Assert.Equal((byte)1, reopened.Store.ReadObjectChain(oldBusiness, 1).Entries.Last().Record.Body[0]);
        Assert.Equal((byte)1, reopened.Store.ReadObjectChain(oldMeta, 1).Entries.Last().Record.Body[0]);
        var continued = Save(reopened, 3);
        Assert.Equal(continued.Business.Address, reopened.GetHead("main"));
    }

    [Fact]
    public void Confirmed_install_failure_keeps_both_roots_and_invalidates_old_owner() {
        FrameAddress oldBusiness, oldMeta, candidate;
        using (var repository = P0Repository.CreateNew(_path)) {
            oldBusiness = Save(repository, 1).Business.Address;
            oldMeta = repository.SharedSchemaRoot!.Value;
            using var pending = Prepare(repository, 2);
            int metaInstalls = 0;
            var error = Assert.Throws<GraphCommitException>(() => repository.Commit(pending,
                _ => metaInstalls++, _ => throw new IOException("State install failed after meta install")));
            Assert.Equal(GraphCommitOutcome.Published, error.Outcome);
            Assert.Equal(1, metaInstalls);
            Assert.True(repository.IsFaulted);
            candidate = error.CandidateRevisionAddress!.Value;
            Assert.ThrowsAny<Exception>(() => repository.GetHead("main"));
        }
        using var reopened = P0Repository.OpenExisting(_path);
        Assert.NotEqual(oldBusiness, candidate);
        Assert.Equal(candidate, reopened.GetHead("main"));
        Assert.NotEqual(oldMeta, reopened.SharedSchemaRoot);
        Assert.Equal((byte)2, reopened.Store.ReadObjectChain(reopened.SharedSchemaRoot!.Value, 1).Entries.Last().Record.Body[0]);
    }

    [Theory]
    [InlineData((int)P0SaveCheckpoint.AfterMetaAppend)]
    [InlineData((int)P0SaveCheckpoint.AfterBusinessAppend)]
    public void Completed_unpublished_meta_or_business_does_not_advance_either_ref(int checkpoint) {
        FrameAddress oldBusiness, oldMeta;
        long before;
        using (var repository = P0Repository.CreateNew(_path)) {
            oldBusiness = Save(repository, 1).Business.Address;
            oldMeta = repository.SharedSchemaRoot!.Value;
            before = DataBytes(repository.Data);
            repository.Checkpoint = point => {
                if ((int)point == checkpoint) { throw new IOException("Stop before publication"); }
            };
            using var pending = Prepare(repository, 2);
            var error = Assert.Throws<GraphCommitException>(() => repository.Commit(pending));
            Assert.Equal(GraphCommitOutcome.NotPublished, error.Outcome);
        }
        using var reopened = P0Repository.OpenExisting(_path);
        Assert.Equal(oldBusiness, reopened.GetHead("main"));
        Assert.Equal(oldMeta, reopened.SharedSchemaRoot);
        Assert.True(DataBytes(reopened.Data) > before);
        Assert.Equal((byte)1, reopened.Store.ReadObjectChain(oldMeta, 1).Entries.Last().Record.Body[0]);
        Assert.NotEqual(oldBusiness, Save(reopened, 3).Business.Address);
    }

    [Fact]
    public void Event_installs_meta_and_branch_head_but_preserves_state_baseline() {
        FrameAddress state;
        using (var repository = P0Repository.CreateNew(_path)) {
            state = Save(repository, 1).Business.Address;
            int metaInstalls = 0, stateInstalls = 0;
            using var pending = Prepare(repository, 2, P0GraphKind.Event);
            var result = repository.Commit(pending, _ => metaInstalls++, _ => stateInstalls++);
            Assert.Equal(1, metaInstalls);
            Assert.Equal(0, stateInstalls);
            Assert.Equal(result.Business.Address, repository.GetHead("main"));
            Assert.Equal(state, repository.GetStateHead("main"));
            Assert.Equal(result.Meta!.Address, repository.SharedSchemaRoot);
        }
        using var reopened = P0Repository.OpenExisting(_path);
        Assert.Equal(state, reopened.GetStateHead("main"));
        Assert.NotEqual(state, reopened.GetHead("main"));
        Assert.Equal((byte)2, reopened.Store.ReadObjectChain(reopened.SharedSchemaRoot!.Value, 1).Entries.Last().Record.Body[0]);
    }

    [Fact]
    public void Discard_is_pre_io_and_pending_is_single_use_and_owner_bound() {
        using var repository = P0Repository.CreateNew(_path);
        Save(repository, 1);
        long bytes = DataBytes(repository.Data);
        var discarded = Prepare(repository, 2);
        Assert.Throws<InvalidOperationException>(() => Prepare(repository, 3));
        discarded.Dispose();
        Assert.Equal(bytes, DataBytes(repository.Data));
        Assert.Throws<InvalidOperationException>(() => repository.Commit(discarded));
        using var corrected = Prepare(repository, 3);
        using (var other = P0Repository.CreateNew(Path.Combine(_path, "other"))) {
            Assert.Throws<InvalidOperationException>(() => other.Commit(corrected));
            Assert.False(other.IsFaulted);
        }
        var result = repository.Commit(corrected);
        Assert.Throws<InvalidOperationException>(() => repository.Commit(corrected));
        Assert.Equal((byte)3, repository.Store.ReadObjectChain(result.Business.Address, 1).Entries.Last().Record.Body[0]);
    }

    private static P0SaveResult Save(P0Repository repository, byte value) {
        using var pending = Prepare(repository, value);
        return repository.Commit(pending);
    }

    [Theory]
    [InlineData((int)P0SaveCheckpoint.BeforePublication)]
    [InlineData((int)P0SaveCheckpoint.BeforeInstall)]
    public void Pending_stays_exclusive_through_publication_and_install(int checkpoint) {
        using var repository = P0Repository.CreateNew(_path);
        using var pending = Prepare(repository, 1);
        bool reached = false;
        repository.Checkpoint = point => {
            if ((int)point != checkpoint) { return; }
            reached = true;
            Assert.Throws<InvalidOperationException>(() => repository.EnsureEditingAllowed());
            Assert.Throws<InvalidOperationException>(() => repository.Commit(pending));
            Assert.Throws<InvalidOperationException>(() => repository.Discard(pending));
            Assert.Throws<InvalidOperationException>(() => pending.Dispose());
        };
        var saved = repository.Commit(pending);
        Assert.True(reached);
        Assert.False(repository.IsFaulted);
        Assert.Equal(saved.Business.Address, repository.GetHead("main"));
        Assert.False(repository.HasPending);
    }

    private static P0PendingSave Prepare(P0Repository repository, byte value, P0GraphKind kind = P0GraphKind.State) =>
        repository.Prepare("main", kind,
            new P0PreparedGraphWrite(repository.GetStateHead("main"), new(1), [ObjectVersionRecord.CreateBase(1, [value])], []),
            new P0PreparedGraphWrite(repository.SharedSchemaRoot, new(1), [ObjectVersionRecord.CreateBase(1, [value])], []));

    public void Dispose() => SharedReadModel.DeleteFixture(_path, "dg-bootstrap-publication-");
}
