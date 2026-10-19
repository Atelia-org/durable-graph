using Atelia.HeadStore;
using Atelia.DurableGraph.Storage;
using static Atelia.DurableGraph.Persistence.Tests.DB096TestAccess;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class BranchRegistrationFailureTests : IDisposable {
    private const string Prefix = "dg-registration-failure-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("root", false, "BeforeDataConfirm", RepositoryPublicationOutcome.NotPublished, false)]
    [InlineData("root", false, "AfterAppend", RepositoryPublicationOutcome.Unknown, true)]
    [InlineData("root", true, "SecondAppend", RepositoryPublicationOutcome.Unknown, true)]
    [InlineData("fork", false, "BeforeDataConfirm", RepositoryPublicationOutcome.NotPublished, false)]
    [InlineData("fork", false, "BeforeInstall", RepositoryPublicationOutcome.Published, true)]
    [InlineData("tag", false, "BeforeInstall", RepositoryPublicationOutcome.Published, true)]
    [InlineData("root", true, "BeforeMemberInstall", RepositoryPublicationOutcome.Published, true)]
    public void Registration_windows_preserve_exact_control_entries_and_cold_creation_facts(
        string operation, bool growsSchema, string stage, RepositoryPublicationOutcome outcome, bool visible) {
        BranchId original;
        CommitId source;
        HeadEntryId oldD, oldS, oldT;
        BranchId? candidateBranch;
        CommitId? candidateCommit;
        FrameAddress? candidateGraph;
        Dictionary<FrameAddress, FrameImage> completed = [];
        using (Repository repository = Repository.CreateStorageNew(_path, Models())) {
            original = repository.CreateRootBranchFromState("original", new DB087Widget { Value = 17, Numbers = [1, 2] }).Id;
            source = repository.ReadCurrentCommit(original).Commit.Id;
            repository.CreateTag("stable", source);
            RepositoryResources resources = Resources(repository);
            oldD = resources.Heads.ReadHead(1).Unwrap().Id;
            oldS = resources.Heads.ReadHead(2).Unwrap().Id;
            oldT = resources.Heads.ReadHead(3).Unwrap().Id;
            RememberFrames(resources.Data, completed);
            IOException injected = new("registration " + operation + " " + stage);
            int registrationBoundaries = 0, faultCalls = 0;
            repository.SaveStageObserver = point => {
                if (point == RepositorySaveStage.BeforePublication) {
                    registrationBoundaries++;
                    RememberFrames(resources.Data, completed);
                    if (stage is "BeforeDataConfirm" or "AfterAppend" or "SecondAppend") {
                        // Physical CreateHead/ForkHead has completed. These HS hooks
                        // observe the final D(/S)/T publication, never physical creation.
                        InstallPublicationFault(resources.Heads, stage == "SecondAppend" ? "AfterAppend" : stage, () => {
                            faultCalls++;
                            if (stage != "SecondAppend" || faultCalls == 2) { throw injected; }
                        });
                    }
                }
                if ((stage == "BeforeInstall" && point == RepositorySaveStage.BeforeInstall) ||
                    (stage == "BeforeMemberInstall" && point == RepositorySaveStage.BeforeMemberInstall)) {
                    faultCalls++;
                    throw injected;
                }
            };
            BranchInfo? delivered = null;
            bool tagReturned = false;
            RepositoryPublicationException error = Assert.Throws<RepositoryPublicationException>(() => {
                switch (operation) {
                    case "root":
                        IDurableObject root = growsSchema ? new DB087EnvelopeRoot { Numbers = [31, 32] }
                            : new DB087Widget { Value = 29, Numbers = [3, 4] };
                        delivered = repository.CreateRootBranchFromState("candidate", root);
                        break;
                    case "fork": delivered = repository.ForkBranch(source, "candidate"); break;
                    case "tag": repository.CreateTag("candidate", source); tagReturned = true; break;
                    default: throw new InvalidOperationException(operation);
                }
            });
            Assert.Equal(1, registrationBoundaries);
            Assert.Equal(stage == "SecondAppend" ? 2 : 1, faultCalls);
            Assert.Same(injected, error.InnerException);
            Assert.Equal(outcome, error.Outcome);
            Assert.Null(delivered);
            Assert.False(tagReturned);
            Assert.True(repository.IsFaulted);
            Assert.Single(error.Members);
            Assert.Null(error.ExpectedBaseCommitId);
            candidateBranch = error.TargetBranchId;
            candidateCommit = error.CandidateCommitId;
            candidateGraph = error.CandidateRevisionAddress;
            if (operation == "root") {
                Assert.NotNull(candidateBranch);
                Assert.NotNull(candidateCommit); // Already returned by physical CreateHead, even when D is unpublished.
                Assert.NotNull(candidateGraph);
                Assert.Equal(candidateBranch, new BranchId(candidateCommit!.Value.Value.HeadId));
            } else {
                Assert.Null(candidateCommit); // Fork has no clone Entry; a tag creates no business Commit.
                Assert.Null(candidateGraph);
                if (operation == "fork") { Assert.NotNull(candidateBranch); }
                else { Assert.Null(candidateBranch); }
            }
            Assert.ThrowsAny<InvalidOperationException>(() => repository.ListBranches());
            Assert.ThrowsAny<InvalidOperationException>(() => repository.CreateTag("blocked", source));
        }

        using Repository reopened = Repository.OpenStorageExisting(_path, Models());
        RepositoryResources cold = Resources(reopened);
        Assert.Equal(source, reopened.ReadCurrentCommit(original).Commit.Id);
        Assert.Equal(source, reopened.ResolveTag("stable"));
        Assert.Equal(17, Assert.IsType<DB087Widget>(reopened.LoadState(source)).Value);
        Assert.Equal(visible && operation != "tag", cold.Heads.ReadHead(1).Unwrap().Id != oldD);
        Assert.Equal(visible && growsSchema, cold.Heads.ReadHead(2).Unwrap().Id != oldS);
        Assert.Equal(visible && operation == "tag", cold.Heads.ReadHead(3).Unwrap().Id != oldT);
        Assert.Equal(1 + (visible && operation != "tag" ? 1 : 0), reopened.ListBranches().Length);
        foreach (var (address, image) in completed) {
            using FrameRead actual = cold.Data.ReadFrame(address).Unwrap();
            Assert.Equal(image.Tag, actual.Tag);
            Assert.Equal(image.TailMetaLength, actual.TailMetaLength);
            Assert.Equal(image.IsTombstone, actual.IsTombstone);
            Assert.Equal(image.PayloadAndMeta, actual.PayloadAndMeta.ToArray());
        }
        if (operation == "tag") {
            Assert.Equal(source, reopened.ResolveTag("candidate"));
            return;
        }
        if (!visible) {
            Assert.Throws<KeyNotFoundException>(() => reopened.ReadBranchInfo(candidateBranch!.Value));
            Assert.Throws<KeyNotFoundException>(() => reopened.ReadCurrentCommit(candidateBranch!.Value));
            // The physical output is retained, but it has not acquired DG membership.
            HeadEntry physical = cold.Heads.ReadHead(candidateBranch!.Value.Value).Unwrap();
            if (operation == "root") {
                Assert.Equal(candidateCommit!.Value.Value, physical.Id);
                Assert.Throws<KeyNotFoundException>(() => reopened.ReadCommitInfo(candidateCommit.Value));
                Assert.Equal(candidateGraph, BusinessEntryPayloadCodec.Decode(physical.Payload).Graph);
                Assert.Equal(StoredGraphKind.State, cold.GraphStore.ReadBusinessGraph(candidateGraph!.Value, null).Kind);
            } else { Assert.Equal(source.Value, physical.Id); }
            return;
        }
        BranchInfo branch = reopened.ReadBranchInfo(candidateBranch!.Value);
        Assert.Equal("candidate", branch.DisplayLabel);
        CommitId current = reopened.ReadCurrentCommit(branch.Id).Commit.Id;
        if (operation == "fork") {
            Assert.Equal(source, branch.ForkedFromCommitId);
            Assert.Equal(source, current); // Header-only fork has not manufactured c0.
            Assert.Equal(17, Assert.IsType<DB087Widget>(reopened.LoadState(current)).Value);
        } else {
            Assert.Null(branch.ForkedFromCommitId);
            Assert.Equal(candidateCommit, current);
            Assert.Equal(candidateGraph, Describe(reopened, current).Address);
            Assert.Equal(reopened.SharedSchemaRoot, Snapshot(reopened, current).SchemaRoot);
            if (growsSchema) { Assert.Equal(new[] { 31, 32 }, Assert.IsType<DB087EnvelopeRoot>(reopened.LoadState(current)).Numbers); }
            else { Assert.Equal(29, Assert.IsType<DB087Widget>(reopened.LoadState(current)).Value); }
        }
        // Continue once through a fresh working copy; exact history must follow the selected result.
        using WorkingCopy work = reopened.OpenWorkingCopy(branch.Id);
        if (work.State is DB087EnvelopeRoot envelope) { envelope.Numbers[0]++; }
        else { ((DB087Widget)work.State!).Value++; }
        CommitId next = work.CommitState().Id;
        Assert.Equal(branch.Id, new BranchId(next.Value.HeadId));
        Assert.Equal(current, new CommitId(cold.ReadPrevious(next.Value)!.Id));
        Assert.Equal(source, reopened.ReadCurrentCommit(original).Commit.Id);
    }

    private static void RememberFrames(Atelia.FrameStore.FrameStore data, Dictionary<FrameAddress, FrameImage> completed) {
        List<FrameInfo> frames = [];
        data.Inventory(frames.Add).Unwrap();
        foreach (FrameInfo info in frames) {
            if (completed.ContainsKey(info.Address)) { continue; }
            using FrameRead frame = data.ReadFrame(info.Address).Unwrap();
            completed.Add(info.Address, new(frame.Tag, frame.TailMetaLength, frame.IsTombstone, frame.PayloadAndMeta.ToArray()));
        }
    }
    private sealed record FrameImage(uint Tag, int TailMetaLength, bool IsTombstone, byte[] PayloadAndMeta);
    private static StateModelRegistry Models() {
        StateModelRegistry models = new();
        models.Register(global::Atelia.DurableGraph.Generated.Family_5030576964676574.Definition);
        models.Register(global::Atelia.DurableGraph.Generated.Family_5030456E76656C6F7065.Definition);
        return models;
    }
    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}
