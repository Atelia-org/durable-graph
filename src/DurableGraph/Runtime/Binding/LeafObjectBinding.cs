using Atelia.Binary;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using System.Reflection;

namespace Atelia.DurableGraph.Runtime;

// The application owns the leaf CLR type and frozen representation. This adapter owns
// its layout, validation, identity boundary and exact Base/Delta replay.
internal sealed class LeafObjectBinding<TDomain, TFrozen> : RegisteredObjectBinding
    where TDomain : class where TFrozen : notnull {
    private readonly DurableLeafCodec<TDomain, TFrozen> _codec;
    private readonly StateReaderBinding[] _readers;

    internal LeafObjectBinding(DurableLeafCodec<TDomain, TFrozen> codec)
        : base(typeof(TDomain), LeafSchema()) {
        _codec = codec;
        DurableSchema current = CurrentLayout.Schema!;
        var readers = new List<StateReaderBinding> { new Reader(current, codec) };
        HashSet<int> versions = [current.Version];
        IReadOnlyList<int> historicalVersions = codec.HistoricalVersions ??
            throw new ArgumentException("A leaf codec must supply its historical versions or an empty list.", nameof(codec));
        foreach (int version in historicalVersions) {
            if (version <= 0 || version >= current.Version || !versions.Add(version)) {
                throw new ArgumentException("A historical leaf version must be distinct, positive and older than the current version.", nameof(codec));
            }
            readers.Add(new Reader(new DurableSchema(current.Type, version), codec));
        }
        _readers = readers.ToArray();
    }

    private static DurableSchema LeafSchema() {
        Type type = typeof(TDomain);
        if (!type.IsSealed || type.IsAbstract || type.IsGenericType || type.IsNested || !type.IsPublic ||
            typeof(IDurableObject).IsAssignableFrom(type) || type.IsDefined(typeof(DurableNodeAttribute), false) ||
            type.IsDefined(typeof(DurableTypeAttribute), false) || type.GetCustomAttribute<DurableLeafAttribute>() is not { } attribute) {
            throw new ArgumentException($"Leaf {type} must be a public top-level sealed, non-generic class with an exclusive DurableLeaf declaration.");
        }
        return new DurableSchema(attribute.Id, attribute.Version);
    }

    internal override object Registration => _codec;
    internal override IReadOnlyList<StateReaderBinding> Readers => _readers;

    private static TFrozen RequireValue(TFrozen value) => value is not null ? value :
        throw new InvalidDataException("A leaf codec returned null frozen content.");

    internal override ObjectStateRecord Capture(ObjectId id, object domain, CaptureContext context) =>
        new(id, CurrentLayout, RequireValue(_codec.Capture((TDomain)domain, context.LeafAcceptance)), this);

    internal override ObjectStateRecord Normalize(ObjectStateRecord source) {
        if (source.Content is TFrozen state) {
            foreach (StateReaderBinding reader in _readers) {
                if (reader.Layout.Equals(source.Layout)) { return new(source.Id, CurrentLayout, state, this); }
            }
        }
        throw new InvalidDataException($"Unsupported stored layout or frozen content for leaf {CurrentLayout.Type}.");
    }

    public override void Validate(ObjectStateRecord state) {
        if (!CurrentLayout.Equals(state.Layout) || state.Content is not TFrozen) {
            throw new InvalidDataException($"Expected current frozen content of leaf {CurrentLayout.Type}.");
        }
    }
    internal override void VisitReferences(ObjectStateRecord current, IStateReferenceVisitor visitor) => Validate(current);
    internal override object Allocate(ObjectStateRecord current) {
        Validate(current);
        return _codec.Restore((TFrozen)current.Content) ?? throw new InvalidDataException($"Leaf {CurrentLayout.Type} restored a null domain.");
    }
    internal override void Hydrate(object domain, ObjectStateRecord current, ObjectReadTable objects) { }
    internal override long? Estimate(ObjectStateRecord state) {
        long? estimate = _codec.EstimateBaseSize((TFrozen)state.Content);
        return estimate < 0 ? throw new InvalidDataException("A leaf Base estimate must be nonnegative.") : estimate;
    }
    public override PreparedBaseBody PrepareBase(ObjectStateRecord state) =>
        _codec.PrepareBase((TFrozen)state.Content) ?? throw new InvalidDataException("A leaf codec returned a null Base.");
    internal override PreparedDeltaBody? TryPrepareDelta(ObjectStateRecord previous, ObjectStateRecord current) =>
        _codec.PrepareDelta((TFrozen)previous.Content, (TFrozen)current.Content);

    private sealed class Reader(DurableSchema schema, DurableLeafCodec<TDomain, TFrozen> codec) : RegisteredReaderBinding(schema) {
        public override Type StateType => typeof(TFrozen);
        internal override ObjectStateRecord Read(ObjectId objectId, IStateBodySource source) {
            int count = source.Count;
            if (objectId.IsNull || count <= 0) { throw new InvalidDataException("A leaf needs a nonzero ID and a Base."); }
            BareValueReader input = new(source.GetBody(0));
            TFrozen state = RequireValue(codec.ReadBase(Schema.Version, ref input));
            input.EnsureFullyConsumed();
            for (int index = 1; index < count; index++) {
                input = new(source.GetBody(index));
                state = RequireValue(codec.ApplyDelta(Schema.Version, ref input, state));
                input.EnsureFullyConsumed();
            }
            return new(objectId, Layout, RequireValue(codec.FinishReplay(state)));
        }
        internal override void VisitReferences(ObjectStateRecord item, IStateReferenceVisitor visitor) {
            if (!Layout.Equals(item.Layout) || item.Content is not TFrozen) {
                throw new InvalidDataException("A frozen leaf record disagrees with its exact reader.");
            }
        }
    }
}
