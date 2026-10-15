using Atelia.DurableGraph;

namespace ExplicitStoragePackage;

[DurableType("probe.explicit.subject", 1)]
public sealed partial class Subject : IDurableObject {
    [DurableField(1)] public int Value;
    [DurableField(2)] public int Control;
    public static int AccessorCalls;

    [field: Transient]
    public int Scratch {
        get { AccessorCalls++; return field; }
        set { AccessorCalls++; field = value; }
    }
}
