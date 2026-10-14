using Atelia.DurableGraph.Schema;

namespace Atelia.DurableGraph.Runtime;

/// <summary>
/// Registers typed roots and captures their state when sealed. Keep the domain view stable
/// from registration through Seal. Generated adapters pair the domain, schema and DTO types.
/// </summary>
public sealed class CaptureContext : IDisposable {
    private enum Phase { Registering, Capturing, Sealed, Resolved }

    private CaptureSession? _session;
    private Phase _phase;
    private Dictionary<object, ObjectId>? _bindings = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, RootCapture> _registrations = new(ReferenceEqualityComparer.Instance);
    private readonly IStateModelResolver _models;
    private readonly List<RootCapture> _queue = [];
    private readonly List<ObjectId> _rootIds = [];
    private readonly List<ObjectStateRecord> _objects = [];

    internal CaptureContext(CaptureSession session, IStateModelResolver models) {
        _session = session;
        _models = models;
    }

    internal CapturedGraph? Candidate { get; private set; }

    /// <summary>Registers an exact concrete root; null contributes a zero root ID.</summary>
    public ObjectId AddRoot<TDomain, TState>(
        TDomain? value,
        DurableSchema schema,
        Func<TDomain, CaptureContext, TState> capture)
        where TDomain : class, IDurableObject
        where TState : unmanaged => AddRootCore(value, schema, capture, preparation: null);

    /// <summary>Registers an exact root together with stable frozen-state preparation operations.</summary>
    public ObjectId AddRoot<TDomain, TState>(
        TDomain? value,
        DurableSchema schema,
        Func<TDomain, CaptureContext, TState> capture,
        StatePreparationBinding<TState> preparation)
        where TDomain : class, IDurableObject
        where TState : unmanaged {
        try {
            ArgumentNullException.ThrowIfNull(preparation);
            return AddRootCore(value, schema, capture, preparation);
        }
        catch {
            AbortBuild();
            throw;
        }
    }

    private ObjectId AddRootCore<TDomain, TState>(
        TDomain? value,
        DurableSchema schema,
        Func<TDomain, CaptureContext, TState> capture,
        StatePreparationBinding<TState>? preparation)
        where TDomain : class, IDurableObject
        where TState : unmanaged {
        try {
            RequirePhase(Phase.Registering);
            ArgumentNullException.ThrowIfNull(schema);
            schema.RequireReferenceObject();
            ArgumentNullException.ThrowIfNull(capture);
            if (preparation is not null && !schema.Equals(preparation.Schema)) {
                throw new ArgumentException("Preparation requires the registered root's exact Schema.", nameof(preparation));
            }
            if (value is null) {
                _rootIds.Add(default);
                return default;
            }
            if (value.GetType() != typeof(TDomain)) {
                throw new ArgumentException("Root capture requires the exact concrete domain type.", nameof(value));
            }
            if (_models.TryGetCurrentModel(typeof(TDomain), out StateModelBinding? model) &&
                !model!.MatchesCapture(schema, capture, preparation)) {
                throw new ArgumentException("Root capture must match its registered model binding.", nameof(capture));
            }
            if (_registrations.TryGetValue(value, out RootCapture? existing)) {
                if (existing is not RootCapture<TDomain, TState> typed ||
                    !typed.Schema.Equals(schema) || !typed.Capture.Equals(capture) ||
                    !ReferenceEquals(typed.Preparation, preparation)) {
                    throw new ArgumentException("A repeated root must use the same schema, DTO, capture and preparation binding.", nameof(capture));
                }
                _rootIds.Add(existing.Id);
                return existing.Id;
            }
            ObjectId id = _session!.GetOrAllocateId(value);
            RootCapture<TDomain, TState> root = new(id, value, schema, capture, preparation);
            _bindings!.Add(value, id);
            _registrations.Add(value, root);
            _queue.Add(root);
            _rootIds.Add(id);
            return id;
        }
        catch {
            AbortBuild();
            throw;
        }
    }

    /// <summary>Registers a durable reference by reference identity, validates its nominal constraint, and queues its capture.</summary>
    public ObjectId CaptureDurable(IDurableObject? value, string nominalSchemaId) {
        try { return CaptureDurable(value, TypeExpr.Named(nominalSchemaId)); }
        catch { AbortBuild(); throw; }
    }

    /// <summary>Captures a reference constrained by a complete constructed nominal type.</summary>
    public ObjectId CaptureDurable(IDurableObject? value, TypeExpr nominalType) => CaptureObject(value, nominalType);

    /// <summary>Validates every incoming reference and captures the actual supported CLR object by reference identity.</summary>
    public ObjectId CaptureObject(object? value, TypeExpr declaredType) {
        try {
            RequirePhase(Phase.Capturing);
            StateReferenceValidator.RequireReferenceType(declaredType);
            if (value is null) {
                return default;
            }
            if (value is string { Length: 0 }) { value = string.Empty; }
            if (!_models.TryGetCurrentObjectBinding(value.GetType(), out ObjectBinding? model)) {
                throw new InvalidOperationException($"No current object binding is registered for actual domain type {value.GetType()}.");
            }
            // Validate every edge before interning, including aliases of an already queued root or child.
            if (!StateReferenceValidator.Accepts(model!.CurrentLayout, declaredType)) {
                throw new InvalidOperationException($"The actual domain type does not satisfy declared reference type {declaredType}.");
            }
            if (_registrations.TryGetValue(value, out RootCapture? existing)) {
                if (!existing.Matches(model)) {
                    throw new InvalidOperationException("The existing object capture disagrees with its registered model binding.");
                }
                return existing.Id;
            }
            ObjectId id = _session!.GetOrAllocateId(value);
            ModelCapture registration = new(id, value, model);
            _bindings!.Add(value, id);
            _registrations.Add(value, registration);
            _queue.Add(registration);
            return id;
        }
        catch {
            AbortBuild();
            throw;
        }
    }

    /// <summary>Captures a string reference. Empty strings share one identity; nonempty strings use reference identity.</summary>
    public ObjectId CaptureString(string? value) => CaptureObject(value, TypeExpr.Builtin(TypeTag.String));

    /// <summary>Copies the reachable closure of all registered roots into a complete immutable graph.</summary>
    public CapturedGraph Seal() {
        try {
            RequirePhase(Phase.Registering);
            _phase = Phase.Capturing;
            for (int index = 0; index < _queue.Count; index++) {
                ObjectStateRecord item = _queue[index].Invoke(this);
                // A callback may catch an illegal reentrant call. Such a failure still aborts the build.
                RequirePhase(Phase.Capturing);
                _objects.Add(item);
            }
            // Key projection can intern references queued after the Dictionary itself.
            // Validate lookup equality only when all captured target contents are available.
            if (_objects.Any(static row => row.Preparation is DictionaryObjectBinding)) {
                Dictionary<ObjectId, ObjectStateRecord> directory = _objects.ToDictionary(static row => row.Id);
                StateReferenceValidator validator = new(directory);
                foreach (ObjectStateRecord row in _objects) {
                    if (row.Preparation is DictionaryObjectBinding dictionary) {
                        dictionary.VisitReferences(row, validator);
                    }
                }
                foreach (ObjectStateRecord row in _objects) {
                    if (row.Preparation is DictionaryObjectBinding dictionary) {
                        dictionary.ValidateLookupKeys(row, id => directory.TryGetValue(id, out ObjectStateRecord? target)
                            ? target : throw new InvalidDataException($"Dictionary key object {id.Value} was not captured."));
                    }
                }
            }
            Candidate = new CapturedGraph(_rootIds, _objects);
            _phase = Phase.Sealed;
            ClearBuildData();
            return Candidate;
        }
        catch {
            AbortBuild();
            throw;
        }
    }

    /// <summary>Abandons an unfinished or unresolved capture. Safe after accept/discard and repeated calls.</summary>
    public void Dispose() => Resolve();

    internal Dictionary<object, ObjectId> DetachBindings() {
        Dictionary<object, ObjectId> result = _bindings!;
        // Ownership transfer must not allocate after a durable publication. Resolve sees null
        // and therefore cannot clear the dictionary now owned by the session.
        _bindings = null;
        return result;
    }

    internal void Resolve() {
        _session?.RequireNotPreparing();
        _phase = Phase.Resolved;
        _bindings?.Clear();
        ClearBuildData();
        Candidate = null;
        CaptureSession? session = _session;
        _session = null;
        session?.Release(this);
    }

    private void AbortBuild() {
        if (_phase is Phase.Registering or Phase.Capturing) {
            Resolve();
        }
    }

    private void RequirePhase(Phase phase) {
        if (_phase != phase) {
            throw new InvalidOperationException("The operation is not valid in the current capture phase.");
        }
    }

    private void ClearBuildData() {
        _registrations.Clear();
        _queue.Clear();
        _rootIds.Clear();
        _objects.Clear();
    }

    private abstract class RootCapture(ObjectId id) {
        public ObjectId Id { get; } = id;
        public abstract ObjectStateRecord Invoke(CaptureContext context);
        public abstract bool Matches(ObjectBinding model);
    }

    private sealed class RootCapture<TDomain, TState>(
        ObjectId id, TDomain source, DurableSchema schema, Func<TDomain, CaptureContext, TState> capture,
        StatePreparationBinding<TState>? preparation)
        : RootCapture(id)
        where TDomain : class, IDurableObject
        where TState : unmanaged {
        public DurableSchema Schema { get; } = schema;
        public Func<TDomain, CaptureContext, TState> Capture { get; } = capture;
        public StatePreparationBinding<TState>? Preparation { get; } = preparation;

        public override ObjectStateRecord Invoke(CaptureContext context) =>
            new(Id, Schema, Capture(source, context), Preparation);

        public override bool Matches(ObjectBinding model) => model is StateModelBinding durable && durable.MatchesCapture(Schema, Capture, Preparation);
    }

    private sealed class ModelCapture(ObjectId id, object source, ObjectBinding model) : RootCapture(id) {
        public override ObjectStateRecord Invoke(CaptureContext context) => model.Capture(Id, source, context);
        public override bool Matches(ObjectBinding other) => ReferenceEquals(model, other);
    }
}
