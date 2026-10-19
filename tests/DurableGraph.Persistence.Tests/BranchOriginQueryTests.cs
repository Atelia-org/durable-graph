using Atelia.HeadStore;
using Atelia.Data;
using Atelia.Rbf;
using System.Buffers.Binary;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class BranchOriginQueryTests : IDisposable {
    private const string Prefix = "dg-branch-origin-query-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Root_and_empty_fork_origins_survive_reopen_parent_advance_and_first_child_save() {
        BranchId parent, child;
        CommitId source, parentTip, childTip;
        using (Repository repository = Repository.CreateStorageNew(_path, Models())) {
            parent = repository.CreateRootBranchFromState("parent", new DB087Widget { Value = 1 }).Id;
            source = repository.ReadCurrentCommit(parent).Commit.Id;
            child = repository.ForkBranch(source, "child").Id;
            AssertOrigins(repository, parent, child, source);
            Assert.Equal(source, repository.ReadCurrentCommit(child).Commit.Id);
        }
        using (Repository repository = Repository.OpenStorageExisting(_path, Models())) {
            AssertOrigins(repository, parent, child, source);
            Assert.Equal(source, repository.ReadCurrentCommit(child).Commit.Id);
            using (WorkingCopy work = repository.OpenWorkingCopy(parent)) {
                ((DB087Widget)work.State!).Value = 2;
                parentTip = work.CommitState().Id;
            }
            Assert.NotEqual(source, parentTip);
            AssertOrigins(repository, parent, child, source);
            Assert.Equal(source, repository.ReadCurrentCommit(child).Commit.Id);
            using (WorkingCopy work = repository.OpenWorkingCopy(child)) {
                Assert.Equal(1, ((DB087Widget)work.State!).Value);
                ((DB087Widget)work.State!).Value = 3;
                childTip = work.CommitState().Id;
            }
            Assert.Equal(child, repository.ReadCommitInfo(childTip).CreatedOnBranchId);
            AssertOrigins(repository, parent, child, source);
        }
        using Repository reopened = Repository.OpenStorageReadOnlyExisting(_path, Models());
        AssertOrigins(reopened, parent, child, source);
        Assert.Equal(parentTip, reopened.ReadCurrentCommit(parent).Commit.Id);
        Assert.Equal(childTip, reopened.ReadCurrentCommit(child).Commit.Id);
    }

    [Fact]
    public void Unregistered_bad_header_does_not_block_registered_branch_or_directory_queries() {
        BranchId healthy;
        HeadId unrelated;
        uint localId;
        using (Repository repository = Repository.CreateStorageNew(_path, Models())) {
            healthy = repository.CreateRootBranchFromState("healthy", new DB087Widget()).Id;
        }
        using (RepositoryResources resources = RepositoryResources.OpenStorageExisting(_path)) {
            unrelated = resources.Heads.CreateHead("not a DG business entry"u8.ToArray(), out _).Unwrap().HeadId;
            localId = resources.Heads.GetLocalHeadId(unrelated).Unwrap();
        }
        string damagedPath = DamageClosedHeadHeader(localId);
        byte[] damaged = File.ReadAllBytes(damagedPath);
        using (Repository reopened = Repository.OpenStorageReadOnlyExisting(_path, Models())) {
            Assert.Null(reopened.ReadBranchInfo(healthy).ForkedFromCommitId);
            Assert.Equal(healthy, Assert.Single(reopened.ListBranches()).Id);
            Assert.Throws<KeyNotFoundException>(() => reopened.ReadBranchInfo(new(unrelated)));
            Assert.False(reopened.IsFaulted);
            Assert.Empty(DB096TestAccess.Resources(reopened).Heads.RecoveryReports);
        }
        Assert.Equal(damaged, File.ReadAllBytes(damagedPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Missing_or_bad_registered_head_is_not_reported_as_a_root_and_does_not_block_other_branch(bool missing) {
        BranchId healthy, damaged;
        uint localId;
        using (Repository repository = Repository.CreateStorageNew(_path, Models())) {
            healthy = repository.CreateRootBranchFromState("healthy", new DB087Widget()).Id;
            damaged = repository.CreateRootBranchFromState("damaged", new DB087Widget()).Id;
            localId = DB096TestAccess.Resources(repository).Heads.GetLocalHeadId(damaged.Value).Unwrap();
        }
        string path = HeadPath(localId);
        if (missing) { File.Move(path, path + ".offline-fixture"); }
        else { DamageClosedHeadHeader(localId); }
        using Repository reopened = Repository.OpenStorageReadOnlyExisting(_path, Models());
        Assert.Null(reopened.ReadBranchInfo(healthy).ForkedFromCommitId);
        if (missing) {
            Assert.Throws<InvalidOperationException>(() => reopened.ReadBranchInfo(damaged));
            Assert.Throws<InvalidOperationException>(() => reopened.ListBranches());
        } else {
            Assert.Contains("Unsupported HeaderFence", Assert.Throws<InvalidDataException>(
                () => reopened.ReadBranchInfo(damaged)).Message);
            Assert.Contains("Unsupported HeaderFence", Assert.Throws<InvalidDataException>(
                () => reopened.ListBranches()).Message);
        }
    }

    [Fact]
    public void Registered_child_with_local_save_cannot_hide_an_unregistered_source_branch() {
        BranchId healthy = SeedRoot();
        HeadId child;
        using (RepositoryResources resources = RepositoryResources.OpenStorageExisting(_path)) {
            byte[] valid = resources.Heads.ReadHead(healthy.Value).Unwrap().Payload.ToArray();
            HeadEntryId source = resources.Heads.CreateHead(valid, out _).Unwrap();
            child = resources.Heads.ForkHead(source, out _).Unwrap();
            resources.Heads.PublishHead(child, valid, out _).Unwrap();
            RegisterBranch(resources, child, "registered child of unregistered parent");
        }
        using Repository reopened = Repository.OpenStorageReadOnlyExisting(_path, Models());
        // The selected local commit is valid: rejecting the origin must not rely on
        // accidentally inspecting the inherited current of an empty fork.
        Assert.Equal(new BranchId(child), reopened.ReadCurrentCommit(new(child)).Commit.CreatedOnBranchId);
        Assert.Throws<KeyNotFoundException>(() => reopened.ReadBranchInfo(new(child)));
        Assert.Throws<KeyNotFoundException>(() => reopened.ListBranches());
        Assert.Null(reopened.ReadBranchInfo(healthy).ForkedFromCommitId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Registered_child_with_local_save_still_validates_exact_source_descriptor(bool metaGraph) {
        BranchId healthy = SeedRoot();
        HeadId child;
        using (RepositoryResources resources = RepositoryResources.OpenStorageExisting(_path)) {
            byte[] valid = resources.Heads.ReadHead(healthy.Value).Unwrap().Payload.ToArray();
            byte[] invalid = "not a DG descriptor"u8.ToArray();
            if (metaGraph) {
                var directory = ManagementDirectoryPointer.Decode(ManagementDirectoryKind.Branches,
                    resources.Heads.ReadHead(1).Unwrap().Payload);
                invalid = BusinessEntryPayloadCodec.Encode(directory!.Value, []);
            }
            HeadEntryId source = resources.Heads.CreateHead(invalid, out _).Unwrap();
            child = resources.Heads.ForkHead(source, out _).Unwrap();
            // Both current values now look valid. Only the exact fixed source is bad.
            resources.Heads.PublishHead(source.HeadId, valid, out _).Unwrap();
            resources.Heads.PublishHead(child, valid, out _).Unwrap();
            RegisterBranch(resources, source.HeadId, "source");
            RegisterBranch(resources, child, "child");
        }
        using Repository reopened = Repository.OpenStorageReadOnlyExisting(_path, Models());
        Assert.Equal(new BranchId(child), reopened.ReadCurrentCommit(new(child)).Commit.CreatedOnBranchId);
        Assert.Throws<InvalidDataException>(() => reopened.ReadBranchInfo(new(child)));
        Assert.Throws<InvalidDataException>(() => reopened.ListBranches());
        Assert.Null(reopened.ReadBranchInfo(healthy).ForkedFromCommitId);
    }

    [Fact]
    public void Directory_rejects_registered_origin_cycle_but_single_branch_query_does_not_audit_other_branches() {
        BranchId healthy, first, second;
        CommitId firstTip, secondTip;
        uint firstLocal, secondLocal;
        using (Repository repository = Repository.CreateStorageNew(_path, Models())) {
            healthy = repository.CreateRootBranchFromState("healthy", new DB087Widget()).Id;
            CommitId root = repository.ReadCurrentCommit(healthy).Commit.Id;
            first = repository.ForkBranch(root, "first").Id;
            second = repository.ForkBranch(root, "second").Id;
            using (WorkingCopy work = repository.OpenWorkingCopy(first)) { firstTip = work.CommitState().Id; }
            using (WorkingCopy work = repository.OpenWorkingCopy(second)) { secondTip = work.CommitState().Id; }
            firstLocal = DB096TestAccess.Resources(repository).Heads.GetLocalHeadId(first.Value).Unwrap();
            secondLocal = DB096TestAccess.Resources(repository).Heads.GetLocalHeadId(second.Value).Unwrap();
        }
        ReplaceClosedForkOrigin(firstLocal, secondLocal, secondTip.Value);
        ReplaceClosedForkOrigin(secondLocal, firstLocal, firstTip.Value);
        using Repository reopened = Repository.OpenStorageReadOnlyExisting(_path, Models());
        Assert.Null(reopened.ReadBranchInfo(healthy).ForkedFromCommitId);
        // Both direct sources are real, published, business-role entries. A failed
        // origin descriptor read cannot accidentally stand in for forest validation.
        Assert.Equal(secondTip, reopened.ReadBranchInfo(first).ForkedFromCommitId);
        Assert.Equal(firstTip, reopened.ReadBranchInfo(second).ForkedFromCommitId);
        Assert.Throws<InvalidDataException>(() => reopened.ListBranches());
        Assert.Null(reopened.ReadBranchInfo(healthy).ForkedFromCommitId);
    }

    private BranchId SeedRoot() {
        using Repository repository = Repository.CreateStorageNew(_path, Models());
        return repository.CreateRootBranchFromState("healthy", new DB087Widget()).Id;
    }

    // Raw publication is only used between closed public owners. It constructs real
    // invalid semantic relationships without changing any completed FrameStore frame.
    private static void RegisterBranch(RepositoryResources resources, HeadId branch, string label) {
        HeadEntry directoryHead = resources.Heads.ReadHead(1).Unwrap();
        var address = ManagementDirectoryPointer.Decode(ManagementDirectoryKind.Branches, directoryHead.Payload);
        DecodedGraphRows? accepted = address is { } value
            ? ManagementDirectoryReader.ReadBranches(resources.GraphStore, value) : null;
        PreparedDirectoryWrite candidate = ManagementDirectoryWriter.PrepareBranchAddition(resources.GraphStore, accepted, branch, label);
        StoredGraphRevision graph = resources.GraphStore.AppendMeta(candidate.Write.SourceGraph, candidate.Write);
        resources.Heads.PublishHead(directoryHead.Id.HeadId,
            ManagementDirectoryPointer.Encode(ManagementDirectoryKind.Branches, graph.Address), out _).Unwrap();
    }

    private static void AssertOrigins(Repository repository, BranchId parent, BranchId child, CommitId source) {
        Assert.Null(repository.ReadBranchInfo(parent).ForkedFromCommitId);
        Assert.Equal(source, repository.ReadBranchInfo(child).ForkedFromCommitId);
        BranchInfo[] listed = repository.ListBranches();
        Assert.Equal(2, listed.Length);
        Assert.Null(Assert.Single(listed, branch => branch.Id == parent).ForkedFromCommitId);
        Assert.Equal(source, Assert.Single(listed, branch => branch.Id == child).ForkedFromCommitId);
    }

    private string HeadPath(uint localId) => Path.Combine(_path, "versions", "refs", localId.ToString("x8") + ".rbf");

    private string DamageClosedHeadHeader(uint localId) {
        string path = HeadPath(localId);
        // All owners and caches were disposed before creating this offline bad image.
        // Reopen always uses a fresh read-only owner, so no repair is being tested.
        byte[] image = File.ReadAllBytes(path);
        Assert.NotEmpty(image);
        image[0] ^= 0xFF;
        File.WriteAllBytes(path, image);
        return path;
    }

    private void ReplaceClosedForkOrigin(uint localId, uint sourceLocalId, HeadEntryId source) {
        string path = HeadPath(localId);
        List<(SizedPtr Ticket, uint Tag, byte[] Payload, byte[] Metadata)> frames = [];
        using (IRbfFile original = RbfFile.OpenReadOnlyExisting(path, RbfCacheMode.Off)) {
            foreach (RbfFrameInfo info in original.ScanForward()) {
                using RbfPooledFrame frame = original.ReadPooledFrame(info.Ticket).Unwrap();
                int payloadLength = frame.PayloadAndMeta.Length - frame.TailMetaLength;
                frames.Add((info.Ticket, frame.Tag, frame.PayloadAndMeta[..payloadLength].ToArray(),
                    frame.PayloadAndMeta[payloadLength..].ToArray()));
            }
        }
        Assert.True(frames.Count >= 2);
        Assert.Equal(0u, frames[0].Tag);
        // Storage format 4's fork header stores the raw local Head number and
        // packed ticket, not the public fingerprint-encoded HeadEntryId codec.
        // Keep its size, every local Entry ticket and all Entry bytes.
        Assert.Equal(36, frames[0].Payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(frames[0].Payload.AsSpan(24), sourceLocalId);
        BinaryPrimitives.WriteUInt64LittleEndian(frames[0].Payload.AsSpan(28), source.Ticket.Packed);
        string mirror = path + ".offline-mirror";
        using (IRbfFile replacement = RbfFile.CreateNew(mirror, RbfCacheMode.Off)) {
            foreach (var frame in frames) {
                Assert.Equal(frame.Ticket, replacement.Append(frame.Tag, frame.Payload, frame.Metadata).Unwrap());
            }
            replacement.DurableFlush();
        }
        // This is a closed corruption fixture, never an in-process storage repair.
        File.Move(mirror, path, overwrite: true);
    }

    private static StateModelRegistry Models() {
        StateModelRegistry models = new();
        models.Register(global::Atelia.DurableGraph.Generated.Family_5030576964676574.Definition);
        return models;
    }

    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}
