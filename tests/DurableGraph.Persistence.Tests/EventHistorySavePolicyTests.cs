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
        var currentDefault = Assert.IsType<ReadAmplificationBaseBudgetParameters>(typeof(EventHistoryRepository)
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
        using (var repository = EventHistoryRepository.CreateNew(path, Models(), new() { NewStoreLayout = RbfSegmentStoreLayout.Flat })) {
            Node state = new();
            using var session = includeOverrides
                ? repository.CreateBranch("main", state, NoRebase)
                : omitPolicy ? repository.CreateBranch("main", state)
                : repository.CreateBranch("main", state, currentDefault);
            addresses.Add(session.StateRevisionAddress);
            // Long enough to exercise both Base and Delta for the proposed 3x/5x/10x
            // defaults; a materially different policy may need a different workload.
            for (int i = 1; i <= 40; i++) {
                if (i % 4 != 0) { state.Value++; }
                GraphFrame domainEvent = includeOverrides && i == 1
                    ? session.CommitDomainEvent(state, new(1, 100))
                    : omitPolicy ? session.CommitDomainEvent(state)
                    : session.CommitDomainEvent(state, currentDefault);
                addresses.Add(domainEvent.RevisionAddress);
                GraphFrame saved = includeOverrides && i == 2
                    ? session.CommitDomainState(NoRebase)
                    : omitPolicy ? session.CommitDomainState()
                    : session.CommitDomainState(currentDefault);
                addresses.Add(saved.RevisionAddress);
            }
        }
        using (var repository = EventHistoryRepository.OpenReadOnlyExisting(path, Models())) {
            Assert.Equal((byte)30, repository.ReadState<Node>(repository.GetHead("main")).Value);
        }
        using SegmentStore segments = SegmentStore.OpenExisting(Path.Combine(path, "state"));
        using StateRevisionStore store = new(segments);
        return addresses.Select(address => store.Read(address).LocalObjects.Select(row => row.Kind).ToArray()).ToArray();
    }
}
