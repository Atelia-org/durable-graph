using Atelia.DurableGraph.Storage;
using Atelia.HeadStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class BranchGroupIntegrityTests : IDisposable {
    private const string Prefix = "dg-branch-group-integrity-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-head")]
    [InlineData("extra")]
    public void Invalid_confirmed_publication_result_faults_before_install_but_cold_heads_are_both_new(string defect) {
        BranchId leftId, rightId;
        CommitId leftBefore, rightBefore;
        Dictionary<HeadId, HeadEntryId>? actual = null;
        HeadEntryId directoryBefore, schemaBefore;
        using (Repository repository = Repository.CreateStorageNew(_path, Models())) {
            leftId = repository.CreateRootBranchFromState("left", new DB087Widget { Value = 1 }).Id;
            rightId = repository.CreateRootBranchFromState("right", new DB087Widget { Value = 2 }).Id;
            using WorkingCopy left = repository.OpenWorkingCopy(leftId);
            using WorkingCopy right = repository.OpenWorkingCopy(rightId);
            leftBefore = left.BaseCommitId;
            rightBefore = right.BaseCommitId;
            IDurableObject leftState = left.State!, rightState = right.State!;
            NormalizedGraphState leftBaseline = left.Baseline!, rightBaseline = right.Baseline!;
            RepositoryResources resources = DB096TestAccess.Resources(repository);
            directoryBefore = resources.Heads.ReadHead(1).Unwrap().Id;
            schemaBefore = resources.Heads.ReadHead(2).Unwrap().Id;
            int overrides = 0, installs = 0;
            resources.PublicationResultsOverride = entries => {
                overrides++;
                actual = entries.ToDictionary();
                Assert.Equal(2, actual.Count); // No Schema growth in this witness.
                var malformed = entries.ToDictionary();
                switch (defect) {
                    case "missing": Assert.True(malformed.Remove(rightId.Value)); break;
                    case "wrong-head": malformed[leftId.Value] = actual[rightId.Value]; break;
                    case "extra": malformed.Add(directoryBefore.HeadId, directoryBefore); break;
                    default: throw new InvalidOperationException(defect);
                }
                return malformed;
            };
            repository.SaveStageObserver = stage => {
                if (stage == RepositorySaveStage.BeforeMemberInstall) { installs++; }
            };
            CommitRequest[] requests = [
                left.CreateStateCommitRequest(new DB087Widget { Value = 10 }),
                right.CreateStateCommitRequest(new DB087Widget { Value = 20 })
            ];
            IReadOnlyDictionary<BranchId, CommitInfo>? delivered = null;
            RepositoryPublicationException error = Assert.Throws<RepositoryPublicationException>(
                () => delivered = repository.CommitBranches(requests));
            Assert.Equal(RepositoryPublicationOutcome.Published, error.Outcome);
            Assert.IsType<InvalidDataException>(error.InnerException);
            Assert.Equal(1, overrides);
            Assert.Equal(0, installs);
            Assert.Null(delivered);
            Assert.True(repository.IsFaulted);
            Assert.True(left.IsFaulted);
            Assert.True(right.IsFaulted);
            Assert.Same(leftState, left.State);
            Assert.Same(rightState, right.State);
            Assert.Same(leftBaseline, left.Baseline);
            Assert.Same(rightBaseline, right.Baseline);
            Assert.Equal(leftBefore, left.BaseCommitId);
            Assert.Equal(rightBefore, right.BaseCommitId);
            Assert.Equal(leftBefore, left.StateSourceCommitId);
            Assert.Equal(rightBefore, right.StateSourceCommitId);
            var members = error.Members.ToDictionary(member => member.TargetBranchId!.Value);
            Assert.Equal(2, members.Count);
            Assert.Equal(leftBefore, members[leftId].ExpectedBaseCommitId);
            Assert.Equal(rightBefore, members[rightId].ExpectedBaseCommitId);
            Assert.All(error.Members, member => Assert.NotNull(member.CandidateRevisionAddress));
            Assert.Throws<InvalidOperationException>(() => repository.CommitBranches(requests));
        }
        using Repository reopened = Repository.OpenStorageReadOnlyExisting(_path, Models());
        CommitId leftAfter = reopened.ReadCurrentCommit(leftId).Commit.Id;
        CommitId rightAfter = reopened.ReadCurrentCommit(rightId).Commit.Id;
        Assert.NotEqual(leftBefore, leftAfter);
        Assert.NotEqual(rightBefore, rightAfter);
        Assert.Equal(actual![leftId.Value], leftAfter.Value);
        Assert.Equal(actual![rightId.Value], rightAfter.Value);
        Assert.Equal(10, Assert.IsType<DB087Widget>(reopened.LoadState(leftAfter)).Value);
        Assert.Equal(20, Assert.IsType<DB087Widget>(reopened.LoadState(rightAfter)).Value);
        Assert.Equal(directoryBefore, DB096TestAccess.Resources(reopened).Heads.ReadHead(1).Unwrap().Id);
        Assert.Equal(schemaBefore, DB096TestAccess.Resources(reopened).Heads.ReadHead(2).Unwrap().Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cleanup_failure_is_attempted_once_preserves_primary_outcome_and_faults_the_real_group_owner(bool failBeforeAppend) {
        HeadEntryId leftBefore, rightBefore;
        using (RepositoryStorageFacade facade = RepositoryStorageFacade.CreateNew(_path)) {
            leftBefore = SeedOpaque(facade, "left", 1).CommitId;
            rightBefore = SeedOpaque(facade, "right", 2).CommitId;
            RepositoryResources resources = MechanismFixture.Resources(facade);
            HistoryGraphRecord left = resources.ReadCurrent(leftBefore.HeadId);
            HistoryGraphRecord right = resources.ReadCurrent(rightBefore.HeadId);
            // The existing raw mechanism fixture isolates publication from typed body decoding.
            using PendingRepositorySave pending = resources.PrepareSaveGroup(() => new([
                new(leftBefore.HeadId, leftBefore, null, StoredGraphKind.State, default,
                    new(left.Address, new(1), [ObjectVersionRecord.CreateBase(1, [10])])),
                new(rightBefore.HeadId, rightBefore, null, StoredGraphKind.State, default,
                    new(right.Address, new(1), [ObjectVersionRecord.CreateBase(1, [20])]))
            ], null));
            GraphRecordStore store = facade.Store;
            long appended = store.AppendedFrames;
            IOException primary = new("before group append"), cleanup = new("group candidate cleanup");
            if (failBeforeAppend) {
                resources.SaveStageObserver = stage => {
                    if (stage == RepositorySaveStage.BeforeAppend) { throw primary; }
                };
            }
            int cleanupCalls = 0, installed = 0, deliveryCalls = 0;
            IReadOnlyList<RepositorySaveResult>? delivered = null;
            RepositoryPublicationException error = Assert.Throws<RepositoryPublicationException>(() =>
                delivered = resources.CommitSaveGroup(pending,
                    installState: (_, _) => installed++,
                    deliver: _ => deliveryCalls++,
                    cleanup: () => { cleanupCalls++; throw cleanup; }));
            Assert.Null(delivered);
            Assert.Equal(failBeforeAppend ? RepositoryPublicationOutcome.NotPublished : RepositoryPublicationOutcome.Published, error.Outcome);
            if (failBeforeAppend) {
                AggregateException aggregate = Assert.IsType<AggregateException>(error.InnerException);
                Assert.Equal(new Exception[] { primary, cleanup }, aggregate.InnerExceptions);
                Assert.Equal(appended, store.AppendedFrames);
            } else {
                Assert.Same(cleanup, error.InnerException);
                Assert.True(store.AppendedFrames > appended);
            }
            Assert.Equal(failBeforeAppend ? 0 : 2, installed);
            Assert.Equal(failBeforeAppend ? 0 : 1, deliveryCalls);
            Assert.Equal(1, cleanupCalls);
            Assert.Equal(2, error.Members.Count);
            Assert.True(resources.IsFaulted);
            Assert.False(pending.IsPrepared);
            pending.Dispose();
            Assert.Equal(1, cleanupCalls);
            Assert.Throws<InvalidOperationException>(() => resources.ListBranches());
            foreach (RepositoryPublicationMember member in error.Members) {
                if (failBeforeAppend) {
                    Assert.Null(member.CandidateRevisionAddress);
                    Assert.Null(member.CandidateCommitId);
                } else {
                    Assert.NotNull(member.CandidateRevisionAddress);
                    Assert.NotNull(member.CandidateCommitId);
                }
            }
        }
        using RepositoryStorageFacade cold = RepositoryStorageFacade.OpenReadOnlyExisting(_path);
        Assert.Equal(!failBeforeAppend, MechanismFixture.Current(cold, "left").Id != leftBefore);
        Assert.Equal(!failBeforeAppend, MechanismFixture.Current(cold, "right").Id != rightBefore);
        Assert.Equal((byte)(failBeforeAppend ? 1 : 10), ReadOpaque(cold, "left"));
        Assert.Equal((byte)(failBeforeAppend ? 2 : 20), ReadOpaque(cold, "right"));
    }

    private static RepositorySaveResult SeedOpaque(RepositoryStorageFacade facade, string label, byte value) {
        PreparedGraphWrite? catalog = facade.SharedSchemaRoot is null
            ? new(null, new(1), [ObjectVersionRecord.CreateBase(1, [0x7F])]) : null;
        using PendingRepositorySave pending = MechanismFixture.Prepare(facade, label, StoredGraphKind.State,
            new(null, new(1), [ObjectVersionRecord.CreateBase(1, [value])]), catalog);
        return facade.Commit(pending);
    }

    private static byte ReadOpaque(RepositoryStorageFacade facade, string label) {
        StoredGraphRevision graph = MechanismFixture.Snapshot(facade, MechanismFixture.Current(facade, label).Address);
        return facade.Store.ReadObjectChain(graph, 1).Entries.Last().Record.Body[0];
    }

    private static StateModelRegistry Models() {
        StateModelRegistry models = new();
        models.Register(global::Atelia.DurableGraph.Generated.Family_5030576964676574.Definition);
        return models;
    }

    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}
