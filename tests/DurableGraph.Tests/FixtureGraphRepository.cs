using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.RbfSegmentStore;

namespace Atelia.DurableGraph.Tests;

// Test-only adapter for existing generated model/history witnesses. Each old Save operation
// maps to initial S0 or explicit marker E + S. It preserves the graph assertions without
// retaining a product compatibility facade. Public API acceptance uses EventHistory directly.
public sealed class FixtureGraphRepository : IDisposable {
    private readonly EventHistoryRepository _repository;
    private FrameAddress? _stateAddress;
    private FixtureGraphRepository(EventHistoryRepository repository, bool existing) {
        _repository = repository;
        if (existing) { _stateAddress = repository.GetHead("main").RevisionAddress; }
    }
    public static FixtureGraphRepository CreateNew(string path, StateModelRegistry models, RbfSegmentStoreOptions? options = null) {
        FixtureMarker.Register(models);
        return new(EventHistoryRepository.CreateNew(path, models, options), false);
    }
    public static FixtureGraphRepository OpenExisting(string path, StateModelRegistry models, RbfSegmentStoreOptions? options = null) {
        FixtureMarker.Register(models);
        return new(EventHistoryRepository.OpenExisting(path, models, options), true);
    }
    public FrameAddress? HeadRevisionAddress => _stateAddress;
    public bool IsFaulted => _repository.IsFaulted;
    public FixtureGraphSession<T> Create<T>(T state) where T : class, IDurableObject {
        return new(this, state);
    }
    public FixtureGraphSession<T> Load<T>() where T : class, IDurableObject {
        return new(this, _repository.Resume<T>("main"));
    }
    internal EventHistorySession<T> Initialize<T>(T state,
        ReadAmplificationBaseBudgetParameters parameters) where T : class, IDurableObject =>
        _repository.CreateBranch("main", state, parameters);
    internal void Accept(FrameAddress address) => _stateAddress = address;
    public void Dispose() => _repository.Dispose();
}

public sealed class FixtureGraphSession<T> : IDisposable where T : class, IDurableObject {
    private readonly FixtureGraphRepository _repository;
    private readonly T _initial;
    private EventHistorySession<T>? _session;
    internal FixtureGraphSession(FixtureGraphRepository repository, T initial) {
        _repository = repository; _initial = initial;
    }
    internal FixtureGraphSession(FixtureGraphRepository repository, EventHistorySession<T> session) {
        _repository = repository; _initial = session.State; _session = session;
    }
    public T World => _session is null ? _initial : _session.State;
    public ObjectId? WorldId => _session?.StateId;
    public FrameAddress? ParentRevisionAddress => _session?.StateRevisionAddress;
    public FrameAddress Commit(ReadAmplificationBaseBudgetParameters parameters) {
        if (_session is null) {
            _session = _repository.Initialize(_initial, parameters);
        } else {
            if (_session.PendingEvent is null) { _session.CommitDomainEvent(new FixtureMarker(), parameters); }
            _session.CommitDomainState(parameters);
        }
        FrameAddress address = _session.StateRevisionAddress;
        _repository.Accept(address);
        return address;
    }
    public void Dispose() => _session?.Dispose();
}

internal sealed class FixtureMarker : IDurableObject {
    private static readonly DurableSchema Schema = new("DurableGraph.Tests.FixtureMarker", 1,
        new DurableFieldInfo(1, TypeTag.Byte));
    private static readonly StateModelBinding Model = new StateModelBinding<FixtureMarker, byte>(
        new CapturedStatePreparation<byte>(Schema,
            static (in byte value) => new PreparedBaseBody(new byte[] { value }),
            static (in byte prior, in byte next) => new PreparedDeltaBody(prior != next, new byte[] { next })),
        [new StateReaderBinding<byte>(Schema,
            static (ref BinaryPayloadReader reader) => reader.ReadByte(),
            static (ref BinaryPayloadReader reader, in byte prior) => reader.ReadByte(), Visit)],
        static row => row.GetState<byte>(),
        static () => new FixtureMarker(),
        static (FixtureMarker domain, in byte value, ObjectReadTable objects) => { },
        static (domain, context) => 0,
        Visit);
    private static void Visit(in byte state, IStateReferenceVisitor visitor) { }
    internal static void Register(StateModelRegistry models) => models.Register(Model);
}
