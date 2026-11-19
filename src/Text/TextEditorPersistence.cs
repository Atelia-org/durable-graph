using Atelia.Binary;
using Atelia.DurableGraph.Serialization;

namespace Atelia.Text;

/// <summary>Registers the text leaf codec for model capture and restoration.</summary>
public static class TextEditorPersistence {
    /// <summary>Adds the version 1 text editor binding to a model registry.</summary>
    public static void Register(IDurableLeafRegistration registry) {
        ArgumentNullException.ThrowIfNull(registry);
        registry.RegisterLeaf(TextCodec.Instance);
    }

    private sealed class TextCodec : DurableLeafCodec<TextEditor, FrozenText> {
        internal static readonly TextCodec Instance = new();

        public override FrozenText Capture(TextEditor value, LeafAcceptance? acceptance) =>
            value.CaptureState(acceptance);

        public override TextEditor Restore(FrozenText state) => TextEditor.Restore(state);

        public override long? EstimateBaseSize(FrozenText state) => TextBodyCodec.EstimateBaseSize(state);

        public override PreparedBaseBody PrepareBase(FrozenText state) => TextBodyCodec.PrepareBase(state);

        public override PreparedDeltaBody PrepareDelta(FrozenText previous, FrozenText current) =>
            TextBodyCodec.PrepareDelta(previous, current);

        public override FrozenText ReadBase(int version, ref BareValueReader reader) {
            RequireSupportedVersion(version);
            return TextBodyCodec.ReadBase(reader.ReadRawBytes(reader.RemainingCount));
        }

        public override FrozenText ApplyDelta(int version, ref BareValueReader reader, FrozenText previous) {
            RequireSupportedVersion(version);
            return TextBodyCodec.ApplyDelta(ref reader, previous);
        }

        public override FrozenText FinishReplay(FrozenText state) => TextBodyCodec.FinishReplay(state);

        private static void RequireSupportedVersion(int version) {
            if (version != 1) { throw new InvalidDataException($"Unsupported text leaf version {version}."); }
        }
    }
}
