using Atelia.DurableGraph;

namespace StateStorePackageConsumerProbe;

[DurableType("package.persisted-base", 1)]
public abstract partial class NamedObject : IDurableObject {
    [DurableField(1)] private string _name;
    protected NamedObject(string name) { _name = name; }
    protected string Name => _name;
}

#if CHARACTER_HISTORY_V1
[DurableType("package.persisted-character", 1)]
#else
[DurableType("package.persisted-character", 2)]
#endif
public sealed partial class Character : NamedObject {
    [DurableField(1)] private int _score;
    [DurableField(2)] private string _alias;
    [DurableField(3)] private readonly ulong _createdAtTicks;

#if !CHARACTER_HISTORY_V1
    // Stable fixed-width content makes a changed score genuinely cheaper as Delta with
    // the new 12-byte prior address. V1's accepted schema stays unchanged.
    [DurableField(4)] private readonly Guid _fixtureId;
    [DurableField(5)] private readonly Guid _fixtureGroup;

    private static void UpgradeStateV1ToV2(in __DurableState.V1 old, out __DurableState.V2 next) =>
        next = new(old.Segment0Field1, old.Segment1Field1, old.Segment1Field2,
            old.Segment1Field3, default, default);
#endif

    private Character(string name, int score) : base(name) {
        _score = score; _alias = name; _createdAtTicks = 638_625_600_000_000_000;
#if !CHARACTER_HISTORY_V1
        _fixtureId = default; _fixtureGroup = default;
#endif
    }

}
