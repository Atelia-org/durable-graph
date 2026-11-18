using Atelia.Binary;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Runtime;

internal sealed class TextObjectBinding : ObjectBinding, IStatePreparationBinding {
    internal static TextObjectBinding Instance { get; } = new();
    private TextObjectBinding() : base(typeof(DurableText), ObjectLayout.Text) { }

    internal override ObjectStateRecord Capture(ObjectId id, object domain, CaptureContext context) {
        DurableText text = (DurableText)domain;
        DurableTextState state = text.CaptureState();
        context.RegisterTextCapture(text, state);
        return new(id, state, this);
    }

    internal override ObjectStateRecord Normalize(ObjectStateRecord source) {
        Validate(source);
        return new(source.Id, source.TextState, this);
    }

    internal override void VisitReferences(ObjectStateRecord current, IStateReferenceVisitor visitor) => Validate(current);
    internal override object Allocate(ObjectStateRecord current) {
        Validate(current);
        return DurableText.Restore(current.TextState);
    }

    internal override void Hydrate(object domain, ObjectStateRecord current, ObjectReadTable objects) {
        Validate(current);
        if (domain is not DurableText text || !ReferenceEquals(text.CaptureState().ContentToken, current.TextState.ContentToken)) {
            throw new InvalidDataException("Text hydration requires its allocated frozen content identity.");
        }
    }

    public void Validate(ObjectStateRecord item) {
        if (item.Kind != ObjectStateKind.Text || item.Content is not DurableTextState) {
            throw new InvalidOperationException("The Text binding requires frozen Text content.");
        }
    }

    public PreparedBaseBody PrepareBase(ObjectStateRecord current) {
        Validate(current);
        return TextBodyCodec.PrepareBase(current.TextState);
    }

    public PreparedDeltaBody PrepareDelta(ObjectStateRecord previous, ObjectStateRecord current) {
        Validate(previous);
        Validate(current);
        return TextBodyCodec.PrepareDelta(previous.TextState, current.TextState);
    }
}

internal sealed class TextObjectReader : ObjectReaderBinding {
    internal static TextObjectReader Instance { get; } = new();
    private TextObjectReader() : base(ObjectLayout.Text) { }
    public override Type StateType => typeof(DurableTextState);

    internal override ObjectStateRecord Read(ObjectId objectId, IStateBodySource source) {
        ArgumentNullException.ThrowIfNull(source);
        if (objectId.IsNull || source.Count <= 0) { throw new InvalidDataException("Text requires a nonzero ID and a Base body."); }
        BareValueReader reader = new(source.GetBody(0));
        DurableTextState state = TextBodyCodec.ReadBase(ref reader);
        for (int index = 1; index < source.Count; index++) {
            reader = new(source.GetBody(index));
            state = TextBodyCodec.ApplyDelta(ref reader, state);
        }
        return new(objectId, TextBodyCodec.FinishReplay(state));
    }

    internal override void VisitReferences(ObjectStateRecord item, IStateReferenceVisitor visitor) => TextObjectBinding.Instance.Validate(item);
}
