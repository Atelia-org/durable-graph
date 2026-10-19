using Atelia.HeadStore;
using static Atelia.DurableGraph.Persistence.Tests.DB096TestAccess;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class BranchPhysicalCreationFailureTests : IDisposable {
    private const string Prefix = "dg-physical-branch-failure-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false, "BeforeNewHeadRename", false)]
    [InlineData(false, "AfterConfirmed", true)]
    [InlineData(true, "BeforeNewHeadRename", false)]
    [InlineData(true, "AfterConfirmed", true)]
    public void Physical_creation_failure_never_registers_or_delivers_a_branch(bool fork, string stage, bool physicalExists) {
        BranchId main;
        CommitId source, current;
        HeadEntryId[] controls;
        Dictionary<FrameAddress, (uint Tag, int TailLength, byte[] Bytes)> frames;
        using (Repository seed = Repository.CreateNew(_path, Models())) {
            main = seed.CreateRootBranchFromState("main", new DB087Widget { Value = 17 }).Id;
            source = seed.ReadCurrentCommit(main).Commit.Id;
            using WorkingCopy work = seed.OpenWorkingCopy(main);
            ((DB087Widget)work.State!).Value = 18;
            current = work.CommitState().Id;
            seed.CreateTag("old-source", source);
            controls = Controls(Resources(seed));
            frames = Frames(Resources(seed).Data);
        }
        Dictionary<string, byte[]> oldHeads = PhysicalHeads();
        using (Repository repository = Repository.OpenExisting(_path, Models())) {
            RepositoryResources resources = Resources(repository);
            int hookCalls = 0, registrationCalls = 0;
            IOException injected = new("physical " + stage);
            repository.SaveStageObserver = point => {
                if (point == RepositorySaveStage.BeforePublication) { registrationCalls++; }
            };
            // Install before the API call: these are the physical Head publication hooks,
            // not the later D (or D/S) registration publication.
            InstallPublicationFault(resources.Heads, stage, () => { hookCalls++; throw injected; });
            BranchInfo? delivered = null;
            RepositoryPublicationException error = Assert.Throws<RepositoryPublicationException>(() => {
                delivered = fork ? repository.ForkBranch(source, "candidate")
                    : repository.CreateRootBranchFromState("candidate", new DB087Widget { Value = 99 });
            });
            Assert.Equal(1, hookCalls);
            Assert.Equal(0, registrationCalls);
            Assert.Null(delivered);
            Assert.Same(injected, error.InnerException);
            Assert.Equal(RepositoryPublicationOutcome.NotPublished, error.Outcome);
            Assert.Null(error.TargetBranchId);
            Assert.Null(error.CandidateCommitId);
            Assert.Null(error.ExpectedBaseCommitId);
            RepositoryPublicationMember member = Assert.Single(error.Members);
            Assert.Null(member.TargetBranchId);
            Assert.Null(member.CandidateCommitId);
            if (fork) { Assert.Null(error.CandidateRevisionAddress); }
            else { Assert.NotNull(error.CandidateRevisionAddress); }
            Assert.True(repository.IsFaulted);
            Assert.Throws<InvalidOperationException>(() => repository.ListBranches());
            Assert.Throws<InvalidOperationException>(() => repository.ReadCommitInfo(source));
            Assert.Throws<InvalidOperationException>(() => repository.OpenWorkingCopy(main));
        }

        // Inspect actual formal files after all owners close. Do not fabricate a BranchId
        // or CommitId for the physical creation whose result was never returned.
        Dictionary<string, byte[]> after = PhysicalHeads();
        Assert.Equal(oldHeads.Count + (physicalExists ? 1 : 0), after.Count);
        foreach (var old in oldHeads) { Assert.Equal(old.Value, after[old.Key]); }
        if (physicalExists) {
            string added = Assert.Single(after.Keys.Except(oldHeads.Keys));
            Assert.NotEmpty(after[added]);
        }

        using (Repository cold = Repository.OpenReadOnlyExisting(_path, Models())) {
            Assert.Equal(controls, Controls(Resources(cold)));
            Assert.Equal(main, Assert.Single(cold.ListBranches()).Id);
            Assert.Equal(main, Assert.Single(cold.ListRootBranches()).Id);
            Assert.Equal(main, Assert.Single(cold.ListBranchSubtree(main)).Id);
            Assert.Equal(current, cold.ReadCurrentCommit(main).Commit.Id);
            Assert.Equal(source, cold.ResolveTag("old-source"));
            Assert.Equal(source, cold.ReadCommitInfo(source).Id);
            Assert.Equal(17, Assert.IsType<DB087Widget>(cold.LoadState(source)).Value);
            Assert.Equal(18, Assert.IsType<DB087Widget>(cold.LoadState(current)).Value);
            AssertFrames(Resources(cold).Data, frames);
            Assert.False(cold.IsFaulted);
        }

        CommitId continued;
        using (Repository writable = Repository.OpenExisting(_path, Models())) {
            using WorkingCopy work = writable.OpenWorkingCopy(main);
            Assert.Equal(current, work.BaseCommitId);
            ((DB087Widget)work.State!).Value = 19;
            continued = work.CommitState().Id;
            Assert.NotEqual(current, continued);
            Assert.Equal(controls, Controls(Resources(writable)));
            Assert.Equal(main, Assert.Single(writable.ListBranches()).Id);
            AssertFrames(Resources(writable).Data, frames);
            Assert.False(writable.IsFaulted);
        }
        using (Repository final = Repository.OpenReadOnlyExisting(_path, Models())) {
            Assert.Equal(continued, final.ReadCurrentCommit(main).Commit.Id);
            Assert.Equal(19, Assert.IsType<DB087Widget>(final.LoadState(continued)).Value);
            Assert.Equal(17, Assert.IsType<DB087Widget>(final.LoadState(source)).Value);
            Assert.Equal(source, final.ResolveTag("old-source"));
            Assert.Equal(controls, Controls(Resources(final)));
            Assert.Equal(main, Assert.Single(final.ListBranches()).Id);
            AssertFrames(Resources(final).Data, frames);
        }
    }

    private Dictionary<string, byte[]> PhysicalHeads() => Directory.GetFiles(Path.Combine(_path, "versions", "refs"), "*.rbf")
        .ToDictionary(path => Path.GetFileName(path), File.ReadAllBytes, StringComparer.Ordinal);

    private static HeadEntryId[] Controls(RepositoryResources resources) =>
        [resources.Heads.ReadHead(1).Unwrap().Id, resources.Heads.ReadHead(2).Unwrap().Id, resources.Heads.ReadHead(3).Unwrap().Id];

    private static Dictionary<FrameAddress, (uint Tag, int TailLength, byte[] Bytes)> Frames(Atelia.FrameStore.FrameStore data) {
        List<FrameAddress> addresses = [];
        data.Inventory(item => addresses.Add(item.Address)).Unwrap();
        Dictionary<FrameAddress, (uint Tag, int TailLength, byte[] Bytes)> result = [];
        foreach (FrameAddress address in addresses) {
            using FrameRead frame = data.ReadFrame(address).Unwrap();
            result.Add(address, (frame.Tag, frame.TailMetaLength, frame.PayloadAndMeta.ToArray()));
        }
        Assert.NotEmpty(result);
        return result;
    }

    private static void AssertFrames(Atelia.FrameStore.FrameStore data,
        Dictionary<FrameAddress, (uint Tag, int TailLength, byte[] Bytes)> expected) {
        foreach (var pair in expected) {
            using FrameRead frame = data.ReadFrame(pair.Key).Unwrap();
            Assert.Equal(pair.Value.Tag, frame.Tag);
            Assert.Equal(pair.Value.TailLength, frame.TailMetaLength);
            Assert.Equal(pair.Value.Bytes, frame.PayloadAndMeta.ToArray());
        }
    }

    private static StateModelRegistry Models() {
        StateModelRegistry models = new();
        models.Register(global::Atelia.DurableGraph.Generated.Family_5030576964676574.Definition);
        return models;
    }

    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}
