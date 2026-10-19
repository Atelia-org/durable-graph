
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

/// <summary>
/// Test-only adapter for codec/history mechanism fixtures that describe successive State saves.
/// First save publishes S0; later saves publish a trivial Event then the next State through
/// the real Repository save owner. This mechanism fixture retains supplied CLR instances;
/// public creation tests separately cover CreateRootBranch followed by a restoring OpenWorkingCopy.
/// The marker is a fixture convention; product history permits E/E and S/S.
/// </summary>
internal sealed class StateSaveHarness : IDisposable {
    private readonly Repository _repository;
    private readonly RepositoryStorageFacade _facade;
    private FrameAddress? _stateHead;
    private ObjectId? _worldId;
    private StateSaveHarness(Repository repository, bool hasHead) {
        _repository = repository;
        _facade = new(StorageTestData.ResourcesOf(repository));
        if (hasHead) {
            HistoryGraphRecord? state = MechanismFixture.State(_facade, "main");
            _stateHead = state?.Address;
            _worldId = state?.RootId;
        }
    }
    internal static StateSaveHarness CreateNew(string path, StateModelRegistry models, long rotationThresholdBytes = 64L * 1024 * 1024 * 1024) {
        models.Register(MarkerModel);
        return new(Repository.CreateNew(path, models, rotationThresholdBytes), false) { Models = models };
    }
    internal static StateSaveHarness OpenExisting(string path, StateModelRegistry models, long rotationThresholdBytes = 64L * 1024 * 1024 * 1024) {
        models.Register(MarkerModel);
        return new(Repository.OpenExisting(path, models, rotationThresholdBytes), true) { Models = models };
    }
    private StateModelRegistry Models { get; init; } = null!;
    internal FrameAddress? HeadRevisionAddress => _stateHead;
    internal ObjectId? WorldId => _worldId;
    internal bool IsFaulted => _repository.IsFaulted;
    internal Action<RepositoryOperationStage>? OperationStageObserver { get; set; }
    internal Action<RepositorySaveStage>? SaveStageObserver {
        get => _repository.SaveStageObserver;
        set => _repository.SaveStageObserver = value;
    }
    internal StateSaveSession<T> Create<T>(T world) where T : class, IDurableObject =>
        new(this, world, new(_facade, "main", Models));
    internal StateSaveSession<T> Load<T>() where T : class, IDurableObject {
        BranchSaveSessionFixture session = new(_facade, "main", Models);
        return new(this, (T)session.State!, session);
    }
    internal void Installed(RepositorySaveResult result) {
        _stateHead = result.Business.Address;
        _worldId = result.Business.RootId;
    }
    internal static IDurableObject NewMarker() => new Marker();
    public void Dispose() { _facade.Dispose(); _repository.Dispose(); }

    private sealed class Marker : IDurableObject { }
    private static readonly DurableSchema MarkerSchema = new("StateSaveHarness.Marker", 1);
    private static readonly StateModelBinding MarkerModel = new StateModelBinding<Marker, byte>(
        new(MarkerSchema, static (in byte state) => new PreparedBaseBody([]),
            static (in byte prior, in byte next) => new PreparedDeltaBody(false, [])),
        [new StateReaderBinding<byte>(MarkerSchema, static (ref BareValueReader input) => 0,
            static (ref BareValueReader input, in byte prior) => prior, Visit)],
        static row => row.GetState<byte>(), static () => new Marker(),
        static (Marker domain, in byte state, ObjectReadTable objects) => { },
        static (domain, context) => 0, Visit);
    private static void Visit(in byte state, IStateReferenceVisitor visitor) { }
}

internal sealed class StateSaveSession<T>(StateSaveHarness repository, T initial,
    BranchSaveSessionFixture session) : IDisposable where T : class, IDurableObject {
    internal bool IsFaulted => repository.IsFaulted;
    internal T World => (T)(session.State ?? initial);
    internal ObjectId? WorldId => session.State is null ? null : session.RootId;
    internal FrameAddress? ParentRevisionAddress => session.StateSource?.Address;
    internal FrameAddress Commit(ReadAmplificationBaseBudgetParameters parameters) {
        if (session.StateSource is not null) {
            Action<RepositorySaveStage>? checkpoint = repository.SaveStageObserver;
            repository.SaveStageObserver = null;
            try {
                using PreparedFixtureSave marker = session.Prepare(StateSaveHarness.NewMarker(), StoredGraphKind.Event, parameters);
                marker.Commit();
            } finally { repository.SaveStageObserver = checkpoint; }
        }
        using PreparedFixtureSave pending = session.Prepare(World, parameters: parameters);
        repository.OperationStageObserver?.Invoke(RepositoryOperationStage.AfterPrepare);
        RepositorySaveResult result = pending.Commit();
        repository.Installed(result);
        return result.Business.Address;
    }
    public void Dispose() { }
}

internal static class WorkTestAccess {
    internal static Repository Owner(WorkingCopy work) =>
        (Repository)typeof(WorkingCopy).GetField("_repository", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(work)!;
    internal static CommitKind Kind(WorkingCopy work) => Owner(work).ReadCommitInfo(work.BaseCommitId).Kind;
    internal static FrameAddress Address(WorkingCopy work, CommitId id) => DB096TestAccess.Describe(Owner(work), id).Address;
}
