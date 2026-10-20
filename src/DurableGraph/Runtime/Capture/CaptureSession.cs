using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Runtime;

/// <summary>
/// Owns one in-memory accepted graph and a monotonic ID allocator. Single-threaded;
/// only one capture may be in flight, including a sealed but unresolved candidate.
/// </summary>
public sealed class CaptureSession {
    private ulong _nextObjectId;
    private Dictionary<object, ObjectId> _bindings = new(ReferenceEqualityComparer.Instance);
    private CaptureContext? _pending;
    private bool _preparing;
    private bool _beginningCapture;

    public CaptureSession() : this(1) { }

    // Allows the finite ID domain to be tested without allocating billions of objects.
    internal CaptureSession(uint firstObjectId) {
        ArgumentOutOfRangeException.ThrowIfZero(firstObjectId);
        _nextObjectId = firstObjectId;
    }

    // Identity-only import for the controlled loader. Does not fabricate Capture provenance.
    internal CaptureSession(ulong firstObjectId, IReadOnlyDictionary<object, ObjectId> bindings) {
        ArgumentNullException.ThrowIfNull(bindings);
        if (firstObjectId == 0 || firstObjectId > (ulong)uint.MaxValue + 1) {
            throw new ArgumentOutOfRangeException(nameof(firstObjectId));
        }
        HashSet<ObjectId> ids = [];
        foreach ((object instance, ObjectId id) in bindings) {
            if (id.IsNull || id.Value >= firstObjectId || !ids.Add(id)) {
                throw new ArgumentException("Imported IDs must be unique, nonzero and below the allocation cursor.", nameof(bindings));
            }
            _bindings.Add(instance, id);
        }
        _nextObjectId = firstObjectId;
    }

    public CapturedGraph? Current { get; private set; }

    /// <summary>
    /// Prepares all candidate bodies against Current without resolving the candidate or allocating IDs.
    /// The result identifies in-memory sources only; it does not certify a persistent Parent Revision.
    /// </summary>
    public PreparedCapturedGraph Prepare(CapturedGraph candidate) {
        RequireCandidate(candidate);
        _preparing = true;
        try {
            CapturedGraph? previous = Current;
            Dictionary<ObjectId, ObjectStateRecord> priorObjects = previous?.Objects.ToDictionary(static item => item.Id) ?? [];

            return new PreparedCapturedGraph(previous, candidate, PrepareObjects(candidate, priorObjects));
        }
        finally {
            _preparing = false;
        }
    }

    // Loaded baselines describe source-live rows, not a previously captured graph.
    internal IReadOnlyList<PreparedCapturedObject> PrepareAgainst(
        CapturedGraph candidate, IReadOnlyDictionary<ObjectId, ObjectStateRecord> previous) {
        RequireCandidate(candidate);
        ArgumentNullException.ThrowIfNull(previous);
        _preparing = true;
        try {
            return PrepareObjects(candidate, previous);
        }
        finally {
            _preparing = false;
        }
    }

    private static List<PreparedCapturedObject> PrepareObjects(
        CapturedGraph candidate, IReadOnlyDictionary<ObjectId, ObjectStateRecord> previous) {
        // Validate the entire comparison set before invoking any user body operation.
        foreach (ObjectStateRecord current in candidate.Objects) {
            previous.TryGetValue(current.Id, out ObjectStateRecord? prior);
            ValidatePreparation(current, prior);
        }
        List<PreparedCapturedObject> objects = new(candidate.Objects.Count);
        foreach (ObjectStateRecord current in candidate.Objects) {
            previous.TryGetValue(current.Id, out ObjectStateRecord? prior);
            PreparedBaseBody body = current.Kind == ObjectStateKind.String
                ? StringBodyPreparer.PrepareBase(current.StringContent)
                : current.Preparation!.PrepareBase(current);
            PreparedDeltaBody? delta = prior is not null && current.Kind != ObjectStateKind.String
                ? current.Preparation!.PrepareDelta(prior, current)
                : null;
            objects.Add(new PreparedCapturedObject(current, prior, body, delta));
        }
        return objects;
    }

    public CaptureContext BeginCapture() => BeginCapture([]);

    /// <summary>Begins a capture with an explicit, frozen directory of current model bindings.</summary>
    public CaptureContext BeginCapture(IEnumerable<StateModelBinding> models) {
        RequireNotPreparing();
        if (_pending is not null || _beginningCapture) {
            throw new InvalidOperationException("Resolve the current capture before beginning another.");
        }
        ArgumentNullException.ThrowIfNull(models);
        _beginningCapture = true;
        try {
            Dictionary<Type, StateModelBinding> types = [];
            Dictionary<TypeExpr, StateModelBinding> families = [];
            foreach (StateModelBinding model in models) {
                ArgumentNullException.ThrowIfNull(model);
                if ((types.TryGetValue(model.DomainType, out StateModelBinding? byType) && !ReferenceEquals(byType, model)) ||
                    (families.TryGetValue(model.CurrentSchema.Type, out StateModelBinding? byFamily) && !ReferenceEquals(byFamily, model))) {
                    throw new ArgumentException("Each exact domain type and Schema family requires one stable model binding.", nameof(models));
                }
                types.TryAdd(model.DomainType, model);
                families.TryAdd(model.CurrentSchema.Type, model);
            }
            _pending = new CaptureContext(this, new FixedModels(types));
            return _pending;
        } finally {
            _beginningCapture = false;
        }
    }

    // Repository workspaces already own a frozen catalog. Its lazy closures stay in that
    // snapshot, allowing newly encountered generic object types without registry mutation.
    internal CaptureContext BeginCapture(IStateModelResolver models) {
        RequireNotPreparing();
        ArgumentNullException.ThrowIfNull(models);
        if (_pending is not null || _beginningCapture) {
            throw new InvalidOperationException("Resolve the current capture before beginning another.");
        }
        _pending = new CaptureContext(this, models);
        return _pending;
    }

    private sealed class FixedModels(IReadOnlyDictionary<Type, StateModelBinding> models) : IStateModelResolver {
        public bool TryGetCurrentModel(Type domainType, out StateModelBinding? model) => models.TryGetValue(domainType, out model);
    }

    /// <summary>Installs the actual candidate and its live bindings without recapturing domain data.</summary>
    public void Accept(CapturedGraph candidate) {
        CaptureContext context = RequireCandidate(candidate);
        Dictionary<object, ObjectId> bindings = context.DetachBindings();
        _bindings = bindings;
        Current = candidate;
        context.Resolve();
    }

    /// <summary>Discards candidate bindings. IDs already allocated remain consumed.</summary>
    public void Discard(CapturedGraph candidate) {
        RequireCandidate(candidate).Resolve();
    }

    internal ObjectId GetOrAllocateId(object source) {
        if (_bindings.TryGetValue(source, out ObjectId id)) {
            return id;
        }
        if (_nextObjectId > uint.MaxValue) {
            throw new InvalidOperationException("The session's object ID space is exhausted.");
        }
        return new ObjectId((uint)_nextObjectId++);
    }

    internal void Release(CaptureContext context) {
        if (ReferenceEquals(_pending, context)) {
            _pending = null;
        }
    }

    internal void RequireNotPreparing() {
        if (_preparing) {
            throw new InvalidOperationException("The capture session cannot be changed or prepared recursively during preparation.");
        }
    }

    private static void ValidatePreparation(ObjectStateRecord current, ObjectStateRecord? prior) {
        if (prior is not null && !current.Layout.Equals(prior.Layout)) {
            throw new InvalidOperationException($"An existing captured ID changed its exact object layout. Current: {DescribePreparationRecord(current)}; prior: {DescribePreparationRecord(prior)}.");
        }
        if (prior is not null && current.Kind == ObjectStateKind.Array &&
            !((IFrozenArrayState)current.Content).Shape.Equals(((IFrozenArrayState)prior.Content).Shape)) {
            throw new InvalidOperationException($"An existing captured array ID changed its shape. {DescribePreparationRecord(current)}; " +
                $"prior dimensions=[{string.Join(",", ((IFrozenArrayState)prior.Content).Shape.Lengths.ToArray())}]; " +
                $"current dimensions=[{string.Join(",", ((IFrozenArrayState)current.Content).Shape.Lengths.ToArray())}].");
        }
        if (current.Kind == ObjectStateKind.String) {
            if (prior is not null && !ReferenceEquals(current.StringContent, prior.StringContent)) {
                throw new InvalidOperationException($"An existing string ID changed reference identity. {DescribePreparationRecord(current)}.");
            }
            return;
        }
        IStatePreparationBinding preparation = current.Preparation
            ?? throw new InvalidOperationException($"The captured object has no preparation binding. {DescribePreparationRecord(current)}.");
        preparation.Validate(current);
        if (prior is not null) {
            if (!ReferenceEquals(preparation, prior.Preparation)) {
                throw new InvalidOperationException($"An existing object changed preparation binding. {DescribePreparationRecord(current)}.");
            }
            preparation.Validate(prior);
        }
    }

    private static string DescribePreparationRecord(ObjectStateRecord record) => FormattableString.Invariant(
        $"ObjectId={record.Id.Value}, Kind={record.Kind}, Type={record.Layout.Type}, SchemaVersion={record.Schema?.Version.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}");

    private CaptureContext RequireCandidate(CapturedGraph candidate) {
        RequireNotPreparing();
        ArgumentNullException.ThrowIfNull(candidate);
        if (_pending is null || !ReferenceEquals(_pending.Candidate, candidate)) {
            throw new InvalidOperationException("The candidate is not the session's pending unresolved candidate.");
        }
        return _pending;
    }
}
