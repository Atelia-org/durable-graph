using System.Reflection;
using Atelia.FrameStore;
using Atelia.DurableGraph.Schema;
using Atelia.HeadStore;

namespace Atelia.DurableGraph.Persistence.Tests;

// Mechanism fixtures retain their supplied objects while using the real prepare/install pipeline.
// Public API tests use CreateRootBranch/OpenWorkingCopy and therefore restore fresh objects.
internal sealed class BranchSaveSessionFixture {
    private readonly RepositoryStorageFacade _facade;
    private readonly string _label;
    private bool _bound;
    private readonly StateModelRegistry.FrozenConfiguration _configuration;
    internal BranchSaveSession Session { get; private set; }
    internal BranchSaveSessionFixture(RepositoryStorageFacade facade, string label, StateModelRegistry models, BusinessPersistenceAdapter? business = null) {
        _facade = facade;
        _label = label;
        _configuration = models.FreezeConfiguration();
        RepositoryResources resources = MechanismFixture.Resources(facade);
        var members = resources.ListBranches().Where(pair => pair.Value == label).ToArray();
        if (members.Length == 0) {
            Session = new(facade, null, null, _configuration);
        } else {
            HeadId branch = members.Single().Key;
            _bound = true;
            HistoryGraphRecord current = resources.ReadCurrent(branch);
            HistoryGraphRecord? state = resources.FindNearestState(current.Id, inclusive: true);
            BusinessPersistenceAdapter? restored = business ?? (state is null ? null : BusinessPersistenceAdapter.LoadState(
                facade.Store, MechanismFixture.Snapshot(facade, state.Address), _configuration));
            Session = new(facade, branch, current.Id, _configuration, restored);
        }
    }
    internal IDurableObject? State => Session.State;
    internal ObjectId RootId => Session.RootId;
    internal StoredGraphRevision? StateSource => Session.StateSource;
    internal SchemaCatalogView? AcceptedCatalog => Session.AcceptedCatalog;
    internal ModelExecutionEnvironment Models => Session.Models;
    internal NormalizedGraphState? Baseline => Session.Baseline;
    internal DecodedRevision.SchemaViewBindingEnvironment? BindingEnvironment => Session.BindingEnvironment;
    internal PreparedFixtureSave Prepare(IDurableObject root, StoredGraphKind kind = StoredGraphKind.State,
        ReadAmplificationBaseBudgetParameters? parameters = null, IEnumerable<DurableSchema>? declarations = null) =>
        new(this, Session.Prepare(root, kind, parameters, rootLabel: _bound ? null : _label, declarations: declarations));
    internal void Accept(RepositorySaveResult result) {
        if (_bound) { Session.Advance(result.CommitId); return; }
        _bound = true;
        // A real root session is deliberately single-use. Preserve its installed adapter while binding
        // the fixture's continuation to the actual branch and publication returned by HeadStore.
        BusinessPersistenceAdapter business = (BusinessPersistenceAdapter)typeof(BranchSaveSession)
            .GetField("_business", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Session)!;
        Session = new(_facade, result.BranchId, result.CommitId, _configuration,
            business.Source is null ? null : business);
    }
}

internal sealed class PreparedFixtureSave(BranchSaveSessionFixture owner, PreparedBranchSave actual) : IDisposable {
    internal PreparedBranchSave Actual => actual;
    internal PreparedSchemaCatalogWrite Catalog => actual.Catalog;
    internal PreparedBusinessWrite Business => actual.Business;
    internal PendingRepositorySave Pending => actual.Pending;
    internal RepositorySaveResult Commit(Action<StoredGraphRevision>? prepareBusinessDelivery = null,
        Action<RepositorySaveResult>? deliver = null) {
        RepositorySaveResult result = actual.Commit(prepareBusinessDelivery, deliver);
        owner.Accept(result);
        return result;
    }
    public void Dispose() => actual.Dispose();
}

internal static class MechanismFixture {
    internal static PendingRepositorySave Prepare(RepositoryStorageFacade facade, string label, StoredGraphKind kind,
        PreparedGraphWrite business, PreparedGraphWrite? catalog = null) =>
        PrepareFrom(facade, label, kind, () => (business, catalog));
    internal static PendingRepositorySave PrepareFrom(RepositoryStorageFacade facade, string label, StoredGraphKind kind,
        Func<(PreparedGraphWrite Business, PreparedGraphWrite? Catalog)> produce) {
        var members = Resources(facade).ListBranches().Where(pair => pair.Value == label).ToArray();
        if (members.Length == 0) { return facade.PrepareFrom(null, null, label, kind, default, produce); }
        HeadId branch = members.Single().Key;
        return facade.PrepareFrom(branch, facade.ReadCurrent(branch).Id, null, kind, default, produce);
    }
    internal static RepositoryResources Resources(RepositoryStorageFacade facade) {
        _ = facade.Store;
        return (RepositoryResources)typeof(RepositoryStorageFacade).GetField("_resources", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(facade)!;
    }
    internal static HeadId Branch(RepositoryStorageFacade facade, string label) =>
        Resources(facade).ListBranches().Single(pair => pair.Value == label).Key;
    internal static HistoryGraphRecord Current(RepositoryStorageFacade facade, string label) =>
        Resources(facade).ReadCurrent(Branch(facade, label));
    internal static HistoryGraphRecord? FindCurrent(RepositoryStorageFacade facade, string label) {
        var matches = Resources(facade).ListBranches().Where(pair => pair.Value == label).ToArray();
        return matches.Length == 0 ? null : facade.ReadCurrent(matches.Single().Key);
    }
    internal static HistoryGraphRecord? State(RepositoryStorageFacade facade, string label) {
        RepositoryResources resources = Resources(facade);
        return FindCurrent(facade, label) is { } current ? resources.FindNearestState(current.Id, inclusive: true) : null;
    }
    internal static StoredGraphRevision Snapshot(GraphRecordStore store, FrameAddress address) {
        store.EnsureUsable();
        var ensure = (Action?)typeof(GraphRecordStore).GetField("_ensureOwner", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store);
        if (ensure?.Target is not RepositoryResources owner) { throw new InvalidOperationException("Use an explicit expected source for standalone graph fixtures."); }
        return Snapshot(owner, address);
    }
    internal static StoredGraphRevision Snapshot(RepositoryStorageFacade facade, FrameAddress address) => Snapshot(Resources(facade), address);
    internal static StoredGraphRevision Snapshot(RepositoryResources resources, FrameAddress address) {
        foreach (HeadId branch in resources.ListBranches().Keys) {
            var found = resources.ReadHistory(resources.ReadCurrent(branch).Id).FirstOrDefault(row => row.Address == address);
            if (found is not null) {
                FrameAddress? source = resources.FindNearestState(found.Id, inclusive: false)?.Address;
                return resources.GraphStore.ReadBusinessGraph(address, source);
            }
        }
        throw new InvalidDataException("The fixture address is not a registered business publication.");
    }
}
