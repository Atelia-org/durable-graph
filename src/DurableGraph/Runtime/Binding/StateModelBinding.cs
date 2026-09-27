using Atelia.DurableGraph.Schema;

namespace Atelia.DurableGraph.Runtime;

/// <summary>Fills an unpublished domain instance from a current frozen DTO.</summary>
public delegate void StateHydrator<TDomain, TState>(TDomain domain, in TState state, ObjectReadTable objects)
    where TDomain : class, IDurableObject where TState : unmanaged;

/// <summary>Code capabilities for one model family, independent of storage and publication.</summary>
/// <remarks>
/// Providers must have stable semantics within their model environment: explicit inputs determine
/// results, and callback counts must not determine domain state. Historical conversion must not
/// mutate input DTOs; capture must produce canonical current DTOs with complete values and identities.
/// Allocation and hydration must not publish partial objects, mutate shared DTOs or previously
/// delivered graphs. Successful binding closures may be reused.
/// </remarks>
public abstract class StateModelBinding : ObjectBinding {
    private readonly StateReaderBinding[] _readers;
    private readonly Func<DurableSchema, StateReaderBinding>? _sourceReaderResolver;

    private protected StateModelBinding(DurableSchema currentSchema, Type domainType, IEnumerable<StateReaderBinding> readers,
        Func<DurableSchema, StateReaderBinding>? sourceReaderResolver = null,
        bool isImmutableLeaf = false)
        : base(domainType, ObjectLayout.ForDurable(currentSchema)) {
        ArgumentNullException.ThrowIfNull(currentSchema);
        currentSchema.RequireReferenceObject();
        ArgumentNullException.ThrowIfNull(domainType);
        ArgumentNullException.ThrowIfNull(readers);
        CurrentSchema = currentSchema;
        _sourceReaderResolver = sourceReaderResolver;
        IsImmutableLeaf = isImmutableLeaf;
        _readers = readers.ToArray();
        HashSet<int> versions = [];
        foreach (StateReaderBinding reader in _readers) {
            if (reader is null || reader.Schema.Type != currentSchema.Type ||
                reader.Schema.Version > currentSchema.Version || !versions.Add(reader.Schema.Version)) {
                throw new ArgumentException("Model readers must have unique versions in the current Schema family.", nameof(readers));
            }
        }
        if (!_readers.Any(reader => reader.Schema.Equals(currentSchema))) {
            throw new ArgumentException("A model requires an exact current reader.", nameof(readers));
        }
        Readers = new ReaderList(_readers);
    }

    public DurableSchema CurrentSchema { get; }
    public IReadOnlyList<StateReaderBinding> Readers { get; }

    /// <summary>True when the exact model's instance state is proven to be readonly immutable value leaves.</summary>
    /// <remarks>
    /// Generated bindings verify the final compiled structure (exact base type, complete declared
    /// instance field set, and per-field identity, trusted type and init-only state) before passing
    /// true. A manual caller passing true asserts the same invariants for the exact runtime type
    /// itself, including any state introduced after the model source was written; the runtime does
    /// not re-verify manual assertions.
    /// </remarks>
    internal bool IsImmutableLeaf { get; }

    internal void RequireSource(ObjectStateRecord source) {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Kind != ObjectStateKind.Durable || source.Schema!.Type != CurrentSchema.Type ||
            (!_readers.Any(reader => reader.Schema.Equals(source.Schema)) &&
             !(_sourceReaderResolver?.Invoke(source.Schema).Schema.Equals(source.Schema) ?? false))) {
            throw new InvalidDataException("The source does not match an exact Schema in this model family.");
        }
    }

    internal abstract IDurableObject Allocate();
    internal override object Allocate(ObjectStateRecord current) {
        if (!CurrentLayout.Equals(current.Layout)) { throw new InvalidDataException("Allocation requires the current exact object layout."); }
        return Allocate();
    }
    internal abstract ObjectId AddRoot(CaptureContext context, IDurableObject domain);
    internal abstract bool MatchesCapture(DurableSchema schema, Delegate capture, ICapturedStatePreparation? preparation);

    private sealed class ReaderList(StateReaderBinding[] readers) : IReadOnlyList<StateReaderBinding> {
        public int Count => readers.Length;
        public StateReaderBinding this[int index] => readers[index];
        public IEnumerator<StateReaderBinding> GetEnumerator() => ((IEnumerable<StateReaderBinding>)readers).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

/// <summary>Strongly typed normalization, capture and restoration for one current model.</summary>
public sealed class StateModelBinding<TDomain, TState> : StateModelBinding
    where TDomain : class, IDurableObject where TState : unmanaged {
    private readonly CapturedStatePreparation<TState> _preparation;
    private readonly Func<ObjectStateRecord, TState> _normalize;
    private readonly Func<TDomain> _allocate;
    private readonly StateHydrator<TDomain, TState> _hydrate;
    private readonly Func<TDomain, CaptureContext, TState> _capture;
    private readonly StateReferenceVisitor<TState> _visitReferences;
    private readonly bool _supportsBaseProjection;

    /// <summary>Defines canonical current capture and restoration, and conversion from historical DTOs.</summary>
    /// <remarks>
    /// <paramref name="normalize"/> converts an accepted historical record to its canonical current DTO without modifying the
    /// input. Exact current-layout records bypass this delegate: the runtime preserves their exact
    /// DTO value and ObjectId and attaches this model's preparation. Business initialization and
    /// side effects belong after materialization, not in this historical conversion callback.
    /// </remarks>
    public StateModelBinding(
        CapturedStatePreparation<TState> preparation,
        IEnumerable<StateReaderBinding> readers,
        Func<ObjectStateRecord, TState> normalize,
        Func<TDomain> allocate,
        StateHydrator<TDomain, TState> hydrate,
        Func<TDomain, CaptureContext, TState> capture,
        StateReferenceVisitor<TState> visitReferences,
        Func<DurableSchema, StateReaderBinding>? sourceReaderResolver = null,
        bool supportsBaseProjection = false,
        bool isImmutableLeaf = false)
        : base((preparation ?? throw new ArgumentNullException(nameof(preparation))).Schema, typeof(TDomain), readers, sourceReaderResolver, isImmutableLeaf) {
        ArgumentNullException.ThrowIfNull(normalize);
        ArgumentNullException.ThrowIfNull(allocate);
        ArgumentNullException.ThrowIfNull(hydrate);
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(visitReferences);
        _preparation = preparation;
        _normalize = normalize;
        _allocate = allocate;
        _hydrate = hydrate;
        _capture = capture;
        _visitReferences = visitReferences;
        _supportsBaseProjection = supportsBaseProjection;
    }

    internal StateBaseProjection<TDomain, TState> CreateBaseProjection() {
        if (!_supportsBaseProjection) {
            throw new InvalidDataException("This model does not explicitly support projection onto derived instances.");
        }
        return new(_capture, _hydrate);
    }

    internal override ObjectStateRecord Normalize(ObjectStateRecord source) {
        RequireSource(source);
        TState state = CurrentLayout.Equals(source.Layout) ? source.GetState<TState>() : _normalize(source);
        return new ObjectStateRecord(source.Id, CurrentSchema, state, _preparation);
    }

    internal override void VisitReferences(ObjectStateRecord current, IStateReferenceVisitor visitor) {
        ArgumentNullException.ThrowIfNull(visitor);
        ((ICapturedStatePreparation)_preparation).Validate(current);
        TState state = current.GetState<TState>();
        _visitReferences(in state, visitor);
    }

    internal override IDurableObject Allocate() {
        TDomain domain = _allocate();
        RequireDomain(domain);
        return domain;
    }

    internal override void Hydrate(object domain, ObjectStateRecord current, ObjectReadTable objects) {
        RequireDomain(domain);
        ArgumentNullException.ThrowIfNull(objects);
        ((ICapturedStatePreparation)_preparation).Validate(current);
        TState state = current.GetState<TState>();
        _hydrate((TDomain)domain, in state, objects);
    }

    internal override ObjectId AddRoot(CaptureContext context, IDurableObject domain) {
        RequireDomain(domain);
        return context.AddRoot((TDomain)domain, CurrentSchema, _capture, _preparation);
    }

    internal override ObjectStateRecord Capture(ObjectId id, object domain, CaptureContext context) {
        RequireDomain(domain);
        return new ObjectStateRecord(id, CurrentSchema, _capture((TDomain)domain, context), _preparation);
    }

    internal override bool MatchesCapture(DurableSchema schema, Delegate capture, ICapturedStatePreparation? preparation) =>
        CurrentSchema.Equals(schema) && _capture.Equals(capture) && ReferenceEquals(_preparation, preparation);

    private static void RequireDomain(object? domain) {
        if (domain is null || domain.GetType() != typeof(TDomain)) {
            throw new InvalidDataException("Restoration requires a non-null exact domain type.");
        }
    }
}
