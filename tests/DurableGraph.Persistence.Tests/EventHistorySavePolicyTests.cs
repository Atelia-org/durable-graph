using static Atelia.DurableGraph.Persistence.Tests.DB096TestAccess;

using System.Reflection;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using SegmentStore = Atelia.FrameStore.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed partial class EventHistoryRepositoryTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OmittedPoliciesMatchCurrentDefaultWithoutInheritingPerCallOverrides(bool includeOverrides) {
        // White-box oracle only for the default API contract. Read the authority rather
        // than pinning its numeric value to the independent TestSavePolicies baseline.
        var currentDefault = Assert.IsType<ReadAmplificationBaseBudgetParameters>(typeof(Repository)
            .GetField("DefaultPolicy", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null));
        ObjectVersionKind[][] explicitWrites = SavePolicyTrace("explicit", omitPolicy: false, includeOverrides, currentDefault);
        ObjectVersionKind[][] omittedWrites = SavePolicyTrace("omitted", omitPolicy: true, includeOverrides, currentDefault);
        Assert.Equal(explicitWrites.Length, omittedWrites.Length);
        for (int i = 0; i < explicitWrites.Length; i++) {
            Assert.Equal(explicitWrites[i], omittedWrites[i]);
        }
        // Keep the witness sensitive to representation decisions, beyond successful saves.
        Assert.Contains(explicitWrites.Skip(1), writes => writes.Contains(ObjectVersionKind.Base));
        Assert.Contains(explicitWrites, writes => writes.Contains(ObjectVersionKind.Delta));
        Assert.Contains(explicitWrites, writes => writes.Length == 0);
    }

    [Fact]
    public void EventPublicEntryPointsExposeNoReconstructionPolicy() {
        foreach (var (type, name) in new[] {
            (typeof(WorkingCopy), nameof(WorkingCopy.CommitEvent)),
            (typeof(WorkingCopy), nameof(WorkingCopy.CreateEventCommitRequest)),
            (typeof(Repository), nameof(Repository.CreateRootBranchFromEvent))
        }) {
            MethodInfo method = Assert.Single(type.GetMethods(), method => method.Name == name);
            Assert.DoesNotContain(method.GetParameters(), parameter =>
                parameter.ParameterType == typeof(ReadAmplificationBaseBudgetParameters) ||
                parameter.ParameterType == typeof(ReadAmplificationBaseBudgetParameters?));
        }
    }

    private ObjectVersionKind[][] SavePolicyTrace(string name, bool omitPolicy, bool includeOverrides,
        ReadAmplificationBaseBudgetParameters currentDefault) {
        string path = Path.Combine(_root, name);
        List<FrameAddress> addresses = [];
        using (var repository = Repository.CreateNew(path, Models())) {
            Node state = new();
            using var session = includeOverrides
                ? repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", state, parameters: NoRebase).Id)
                : omitPolicy ? repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", state).Id)
                : repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", state, parameters: currentDefault).Id);
            state = (Node)session.State!;
            addresses.Add(session.StateRevisionAddress!.Value);
            // Long enough to exercise both Base and Delta for the proposed 3x/5x/10x
            // defaults; a materially different policy may need a different workload.
            for (int i = 1; i <= 40; i++) {
                if (i % 4 != 0) { state.Value++; }
                CommitId domainEvent = session.CommitEvent(state).Id;
                addresses.Add(Describe(repository, domainEvent).Address);
                CommitId saved = includeOverrides && i == 2
                    ? session.CommitState(parameters: NoRebase).Id
                    : omitPolicy ? session.CommitState().Id
                    : session.CommitState(parameters: currentDefault).Id;
                addresses.Add(Describe(repository, saved).Address);
            }
        }
        using (var repository = Repository.OpenReadOnlyExisting(path, Models())) {
            Assert.Equal((byte)30, ((Node)repository.LoadState(repository.ReadCurrentCommit(BranchNamed(repository, "main")).Commit.Id)).Value);
        }
        using RepositoryResources resources = RepositoryResources.OpenReadOnlyExisting(path);
        GraphRecordStore store = resources.GraphStore;
        return addresses.Select(address => StorageTestData.LocalRecords(store, address).Select(row => row.Kind).ToArray()).ToArray();
    }
}
