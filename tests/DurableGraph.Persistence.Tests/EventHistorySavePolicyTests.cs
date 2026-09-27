using System.Reflection;
using Atelia.DurableGraph.Storage;
using Atelia.RbfSegmentStore;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;

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

    private ObjectVersionKind[][] SavePolicyTrace(string name, bool omitPolicy, bool includeOverrides,
        ReadAmplificationBaseBudgetParameters currentDefault) {
        string path = Path.Combine(_root, name);
        List<FrameAddress> addresses = [];
        using (var repository = Repository.CreateNew(path, Models(), new() { NewStoreLayout = RbfSegmentStoreLayout.Flat })) {
            Node state = new();
            using var session = includeOverrides
                ? repository.CreateBranch("main", state, NoRebase)
                : omitPolicy ? repository.CreateBranch("main", state)
                : repository.CreateBranch("main", state, currentDefault);
            addresses.Add(session.StateRevisionAddress!.Value);
            // Long enough to exercise both Base and Delta for the proposed 3x/5x/10x
            // defaults; a materially different policy may need a different workload.
            for (int i = 1; i <= 40; i++) {
                if (i % 4 != 0) { state.Value++; }
                CheckpointAddress domainEvent = includeOverrides && i == 1
                    ? session.CommitEvent(state, new(1, 100))
                    : omitPolicy ? session.CommitEvent(state)
                    : session.CommitEvent(state, currentDefault);
                addresses.Add(domainEvent.RevisionAddress);
                CheckpointAddress saved = includeOverrides && i == 2
                    ? session.CommitState(NoRebase)
                    : omitPolicy ? session.CommitState()
                    : session.CommitState(currentDefault);
                addresses.Add(saved.RevisionAddress);
            }
        }
        using (var repository = Repository.OpenReadOnlyExisting(path, Models())) {
            Assert.Equal((byte)30, ((Node)repository.ReadState(repository.GetHead("main"))).Value);
        }
        using SegmentStore segments = SegmentStore.OpenExisting(Path.Combine(path, "state"));
        using StateRevisionStore store = new(segments);
        return addresses.Select(address => store.Read(address).LocalObjects.Select(row => row.Kind).ToArray()).ToArray();
    }
}
