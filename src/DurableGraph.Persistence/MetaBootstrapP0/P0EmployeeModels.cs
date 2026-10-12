namespace Atelia.DurableGraph.Persistence.MetaBootstrapP0;

// Real generator input for DB-087's employee table, selected by the outer typed ABI.
// The experiment reads these generated DTOs directly, without employee CLR shells.
[DurableType("P0C", 1)]
internal sealed partial class P0CatalogRecord : IDurableObject {
    [DurableField(1)] internal List<P0DescriptorRecord> Descriptors = [];
}

[DurableType("P0D", 1)]
internal sealed partial class P0DescriptorRecord : IDurableObject {
    [DurableField(1)] internal byte Kind = 0; // 1: reference Schema; 2: List layout; 3: inline Schema; 4: Array layout.
    [DurableField(2)] internal P0TypeExprRecord? Type = null;
    [DurableField(3)] internal int Version = 0;
    [DurableField(4)] internal List<P0FieldRecord>? Fields = null;
    [DurableField(5)] internal P0FieldRecord? Element = null;
    [DurableField(6)] internal uint CodecVersion = 0;
    [DurableField(7)] internal P0DescriptorRecord? Base = null;
}

[DurableType("P0F", 1)]
internal sealed partial class P0FieldRecord : IDurableObject {
    [DurableField(1)] internal int Id = 0;
    [DurableField(2)] internal byte Tag = 0;
    [DurableField(3)] internal P0TypeExprRecord? TargetType = null;
    [DurableField(4)] internal P0DescriptorRecord? Inline = null;
    [DurableField(5)] internal P0FieldRecord? NullableChild = null;
}

[DurableType("P0T", 1)]
internal sealed partial class P0TypeExprRecord : IDurableObject {
    [DurableField(1)] internal byte Kind = 0;
    [DurableField(2)] internal byte BuiltinTag = 0;
    [DurableField(3)] internal string? Name = null;
    [DurableField(4)] internal List<P0TypeExprRecord>? Arguments = null;
}

[DurableType("P0H", 1)]
internal readonly partial struct P0HeadLocator {
    [DurableField(1)] internal readonly uint Word0;
    [DurableField(2)] internal readonly uint Word1;
    [DurableField(3)] internal readonly uint Word2;

    internal P0HeadLocator(uint word0, uint word1, uint word2) {
        Word0 = word0;
        Word1 = word1;
        Word2 = word2;
    }
}

internal static class P0EmployeeDefinitions {
    internal static void Register(IStateDefinitionRegistration definitions) =>
        global::Atelia.DurableGraph.Generated.DurableDefinitions.Register(definitions);
}
