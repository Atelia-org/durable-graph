using Atelia.DurableGraph.Persistence.Tests;
using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Tests;

// Generator mechanisms use the real internal capture/install pipeline so their initial
// CLR identity remains stable. Public creation/restoration has separate API tests.
public sealed class FixtureGraphRepository : IDisposable {
    private readonly RepositoryStorageFacade _facade;
    private readonly StateModelRegistry _models;
    private FrameAddress? _stateAddress;
    private FixtureGraphRepository(RepositoryStorageFacade facade, StateModelRegistry models, bool existing) {
        _facade = facade;
        _models = models;
        if (existing) { _stateAddress = MechanismFixture.State(facade, "main")?.Address; }
    }
    public static FixtureGraphRepository CreateNew(string path, StateModelRegistry models, long rotationThresholdBytes = 64L * 1024 * 1024 * 1024) {
        FixtureMarker.Register(models);
        return new(new RepositoryStorageFacade(RepositoryResources.CreateNew(path, rotationThresholdBytes)), models, false);
    }
    public static FixtureGraphRepository OpenExisting(string path, StateModelRegistry models, long rotationThresholdBytes = 64L * 1024 * 1024 * 1024) {
        FixtureMarker.Register(models);
        return new(new RepositoryStorageFacade(RepositoryResources.OpenExisting(path, rotationThresholdBytes)), models, true);
    }
    public FrameAddress? HeadRevisionAddress => _stateAddress;
    public bool IsFaulted => _facade.IsFaulted;
    public FixtureGraphSession<T> Create<T>(T state) where T : class, IDurableObject => new(this, NewSession(), state);
    public FixtureGraphSession<T> Load<T>() where T : class, IDurableObject {
        BranchSaveSessionFixture session = NewSession();
        return new(this, session, (T)session.State!);
    }
    private BranchSaveSessionFixture NewSession() => new(_facade, "main", _models);
    internal void Accept(FrameAddress address) => _stateAddress = address;
    public void Dispose() {
        RepositoryResources resources = MechanismFixture.Resources(_facade);
        _facade.Dispose();
        resources.Dispose();
    }
}

public sealed class FixtureGraphSession<T> : IDisposable where T : class, IDurableObject {
    private readonly FixtureGraphRepository _repository;
    private readonly BranchSaveSessionFixture _session;
    private readonly T _initial;
    internal FixtureGraphSession(FixtureGraphRepository repository, BranchSaveSessionFixture session, T initial) {
        _repository = repository; _session = session; _initial = initial;
    }
    public T World => (T?)_session.State ?? _initial;
    public ObjectId? WorldId => _session.State is null ? null : _session.RootId;
    public FrameAddress? ParentRevisionAddress => _session.StateSource?.Address;
    public FrameAddress Commit(ReadAmplificationBaseBudgetParameters parameters) {
        if (_session.State is not null) {
            using var marker = _session.Prepare(new FixtureMarker(), StoredGraphKind.Event, parameters);
            marker.Commit();
        }
        using var pending = _session.Prepare(World, StoredGraphKind.State, parameters);
        FrameAddress address = pending.Commit().Business.Address;
        _repository.Accept(address);
        return address;
    }
    public void Dispose() { }
}

internal sealed class FixtureMarker : IDurableObject {
    private static readonly DurableSchema Schema = new("DurableGraph.Tests.FixtureMarker", 1,
        new DurableFieldInfo(1, TypeTag.Byte));
    private static readonly StateModelBinding Model = new StateModelBinding<FixtureMarker, byte>(
        new StatePreparationBinding<byte>(Schema,
            static (in byte value) => new PreparedBaseBody(new byte[] { value }),
            static (in byte prior, in byte next) => new PreparedDeltaBody(prior != next, new byte[] { next })),
        [new StateReaderBinding<byte>(Schema,
            static (ref BareValueReader reader) => reader.ReadByte(),
            static (ref BareValueReader reader, in byte prior) => reader.ReadByte(), Visit)],
        static row => row.GetState<byte>(),
        static () => new FixtureMarker(),
        static (FixtureMarker domain, in byte value, ObjectReadTable objects) => { },
        static (domain, context) => 0,
        Visit);
    private static void Visit(in byte state, IStateReferenceVisitor visitor) { }
    internal static void Register(StateModelRegistry models) => models.Register(Model);
}
