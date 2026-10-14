namespace Atelia.DurableGraph.Persistence;

// Fixed bootstrap model input, selected by the outer typed ABI.
// Default meta decoding reads these generated DTOs directly, without employee CLR shells.
[DurableType("P0C", 1)]
internal sealed partial class SchemaCatalogRecord : IDurableObject {
    [DurableField(1)] internal List<LayoutDescriptorRecord> Descriptors = [];
}

[DurableType("P0D", 1)]
internal sealed partial class LayoutDescriptorRecord : IDurableObject {
    [DurableField(1)] internal byte Kind = 0; // 1: reference Schema; 2: List; 3: inline Schema; 4: Array; 5: Dictionary.
    [DurableField(2)] internal TypeExprRecord? Type = null;
    [DurableField(3)] internal int Version = 0;
    [DurableField(4)] internal List<LayoutSlotRecord>? Fields = null;
    [DurableField(5)] internal LayoutSlotRecord? Element = null;
    [DurableField(6)] internal uint CodecVersion = 0;
    [DurableField(7)] internal LayoutDescriptorRecord? Base = null;
}

[DurableType("P0F", 1)]
internal sealed partial class LayoutSlotRecord : IDurableObject {
    [DurableField(1)] internal int Id = 0;
    [DurableField(2)] internal byte Tag = 0;
    [DurableField(3)] internal TypeExprRecord? TargetType = null;
    [DurableField(4)] internal LayoutDescriptorRecord? Inline = null;
    [DurableField(5)] internal LayoutSlotRecord? NullableChild = null;
}

[DurableType("P0T", 1)]
internal sealed partial class TypeExprRecord : IDurableObject {
    [DurableField(1)] internal byte Kind = 0;
    [DurableField(2)] internal byte BuiltinTag = 0;
    [DurableField(3)] internal string? Name = null;
    [DurableField(4)] internal List<TypeExprRecord>? Arguments = null;
}

[DurableType("P0H", 1)]
internal readonly partial struct ObjectHeadLocator {
    [DurableField(1)] internal readonly uint Word0;
    [DurableField(2)] internal readonly uint Word1;
    [DurableField(3)] internal readonly uint Word2;

    internal ObjectHeadLocator(uint word0, uint word1, uint word2) {
        Word0 = word0;
        Word1 = word1;
        Word2 = word2;
    }
}

internal static class BootstrapDefinitions {
    internal static void Register(IStateDefinitionRegistration definitions) =>
        global::Atelia.DurableGraph.Generated.DurableDefinitions.Register(definitions);
}
