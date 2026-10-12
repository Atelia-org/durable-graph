namespace Atelia.DurableGraph.Persistence.MetaBootstrapP0;

// Real generator input for DB-087's deliberately finite P0 wire closure.
// The experiment reads these generated DTOs directly, without employee CLR shells.
[DurableType("P0C", 1)]
internal sealed partial class P0CatalogRecord : IDurableObject {
    [DurableField(1)] internal List<P0DescriptorRecord> Descriptors = [];
}

[DurableType("P0D", 1)]
internal sealed partial class P0DescriptorRecord : IDurableObject {
    [DurableField(1)] internal byte Kind = 0; // 1: reference Schema; 2: List<int>.
    [DurableField(2)] internal string? Name = null;
    [DurableField(3)] internal int Version = 0;
    [DurableField(4)] internal List<P0FieldRecord>? Fields = null;
    [DurableField(5)] internal P0FieldRecord? Element = null;
    [DurableField(6)] internal uint CodecVersion = 0;
}

[DurableType("P0F", 1)]
internal sealed partial class P0FieldRecord : IDurableObject {
    [DurableField(1)] internal int Id = 0;
    [DurableField(2)] internal byte Tag = 0;
    [DurableField(3)] internal byte TargetKind = 0; // 0: none; 1: named; 2: List<int>.
    [DurableField(4)] internal string? TargetName = null;
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
