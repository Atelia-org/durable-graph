using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.RbfSegmentStore;

namespace Atelia.DurableGraph.Persistence.Tests;

/// <summary>
/// Test-only adapter for codec/history mechanism fixtures that describe successive State saves.
/// First save publishes S0; later saves publish a trivial Event then the next State through
/// the real EventHistory facade. Production callers explicitly model their own E/S boundary.
/// </summary>
internal sealed class StateSaveHarness : IDisposable {
    private readonly EventHistoryRepository _repository;
    private GraphFrame? _stateHead;
    private StateSaveHarness(EventHistoryRepository repository, bool hasHead) {
        _repository = repository;
        if (hasHead) {
            GraphFrame head = repository.GetHead("main");
            _stateHead = head.Kind == GraphFrameKind.State ? head : repository.GetPreviousState(head);
        }
    }
    internal static StateSaveHarness CreateNew(string path, StateModelRegistry models, RbfSegmentStoreOptions? options = null) {
        models.Register(MarkerModel);
        return new(EventHistoryRepository.CreateNew(path, models, options), false);
    }
    internal static StateSaveHarness OpenExisting(string path, StateModelRegistry models, RbfSegmentStoreOptions? options = null) {
        models.Register(MarkerModel);
        return new(EventHistoryRepository.OpenExisting(path, models, options), true);
    }
    internal FrameAddress? HeadRevisionAddress => _stateHead?.RevisionAddress;
    internal ObjectId? WorldId => _stateHead?.RootId;
    internal bool IsFaulted => _repository.IsFaulted;
    internal Action<CommitCheckpoint>? Checkpoint { get => _repository.Checkpoint; set => _repository.Checkpoint = value; }
    internal StateSaveSession<T> Create<T>(T world) where T : class, IDurableObject {
        return new(this, world, null);
    }
    internal StateSaveSession<T> Load<T>() where T : class, IDurableObject {
        EventHistorySession<T> session = _repository.Resume<T>("main");
        return new(this, session.State, session);
    }
    internal EventHistorySession<T> Initialize<T>(T world,
        ReadAmplificationBaseBudgetParameters parameters) where T : class, IDurableObject {
        EventHistorySession<T> session = _repository.CreateBranch("main", world, parameters);
        _stateHead = session.Head;
        return session;
    }
    internal void Installed(GraphFrame frame) => _stateHead = frame;
    internal static IDurableObject NewMarker() => new Marker();
    public void Dispose() => _repository.Dispose();

    private sealed class Marker : IDurableObject { }
    private static readonly DurableSchema MarkerSchema = new("StateSaveHarness.Marker", 1);
    private static readonly StateModelBinding MarkerModel = new StateModelBinding<Marker, byte>(
        new(MarkerSchema, static (in byte state) => new PreparedBaseBody([]),
            static (in byte prior, in byte next) => new PreparedDeltaBody(false, [])),
        [new StateReaderBinding<byte>(MarkerSchema, static (ref BinaryPayloadReader input) => 0,
            static (ref BinaryPayloadReader input, in byte prior) => prior, Visit)],
        static row => row.GetState<byte>(), static () => new Marker(),
        static (Marker domain, in byte state, ObjectReadTable objects) => { },
        static (domain, context) => 0, Visit);
    private static void Visit(in byte state, IStateReferenceVisitor visitor) { }
}

internal sealed class StateSaveSession<T>(StateSaveHarness repository, T initial,
    EventHistorySession<T>? session) : IDisposable where T : class, IDurableObject {
    internal bool IsFaulted => repository.IsFaulted;
    internal T World => session is null ? initial : session.State;
    internal ObjectId? WorldId => session?.StateId;
    internal FrameAddress? ParentRevisionAddress => session?.StateRevisionAddress;
    internal FrameAddress Commit(ReadAmplificationBaseBudgetParameters parameters) {
        if (session is null) {
            session = repository.Initialize(initial, parameters);
            return session.StateRevisionAddress;
        }
        if (session.PendingEvent is null) {
            Action<CommitCheckpoint>? checkpoint = repository.Checkpoint;
            repository.Checkpoint = null;
            try { session.CommitDomainEvent(StateSaveHarness.NewMarker(), parameters); }
            finally { repository.Checkpoint = checkpoint; }
        }
        GraphFrame frame = session.CommitDomainState(parameters);
        repository.Installed(frame);
        return frame.RevisionAddress;
    }
    public void Dispose() => session?.Dispose();
}
