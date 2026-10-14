using Atelia.DurableGraph.Persistence.MetaBootstrapP0;
using System.Buffers.Binary;
using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using Locator = Atelia.DurableGraph.Generated.Family_503048.V1;
using LocatorOps = Atelia.DurableGraph.Generated.Family_503048.BodyV1;

namespace Atelia.DurableGraph.Persistence;

/// <summary>The ordinary Dictionary body, with a real generated inline locator value.</summary>
internal static class ObjectHeadDirectoryCodec {
    internal static DictionaryLayout Layout { get; } = CreateLayout();

    private static DictionaryLayout CreateLayout() {
        ModelExecutionEnvironment employees = P0FixedBindings.CreateEnvironment();
        return new(new(1, TypeTag.UInt32), employees.ResolveCurrentValue(typeof(P0HeadLocator)).Slot);
    }

    internal static PreparedBaseBody PrepareBase(IReadOnlyDictionary<uint, FrameAddress> current) =>
        DictionaryStateBody<uint, Locator, UInt32StateOps, LocatorOps>.PrepareBase(Freeze(current), Layout);

    internal static PreparedDeltaBody PrepareDelta(IReadOnlyDictionary<uint, FrameAddress> prior,
        IReadOnlyDictionary<uint, FrameAddress> current) {
        Validate(prior);
        Validate(current);
        uint[] removed = prior.Keys.Where(id => !current.ContainsKey(id)).Order().ToArray();
        DictionaryValueChange<uint, Locator>[] patched = current
            .Where(pair => prior.TryGetValue(pair.Key, out FrameAddress before) && before != pair.Value)
            .OrderBy(static pair => pair.Key)
            .Select(pair => new DictionaryValueChange<uint, Locator>(pair.Key, ToState(prior[pair.Key]), ToState(pair.Value)))
            .ToArray();
        DictionaryEntryState<uint, Locator>[] added = current.Where(pair => !prior.ContainsKey(pair.Key))
            .OrderBy(static pair => pair.Key)
            .Select(pair => new DictionaryEntryState<uint, Locator>(pair.Key, ToState(pair.Value))).ToArray();
        return DictionaryStateBody<uint, Locator, UInt32StateOps, LocatorOps>.PrepareDeltaFromOperations(
            Layout, removed, patched, added);
    }

    // The writer already owns these normalized membership changes and completed heads.
    internal static PreparedDeltaBody PrepareDelta(IReadOnlyDictionary<uint, FrameAddress> prior,
        IReadOnlyList<uint> removedIds, IReadOnlyDictionary<uint, FrameAddress> writtenHeads) {
        ArgumentNullException.ThrowIfNull(prior);
        ArgumentNullException.ThrowIfNull(removedIds);
        Validate(writtenHeads);
        foreach (uint id in removedIds) {
            if (id == 0 || !prior.ContainsKey(id)) { throw new InvalidDataException("Directory Remove requires a live source ID."); }
        }
        List<DictionaryValueChange<uint, Locator>> patched = [];
        List<DictionaryEntryState<uint, Locator>> added = [];
        foreach ((uint id, FrameAddress head) in writtenHeads.OrderBy(static pair => pair.Key)) {
            if (prior.TryGetValue(id, out FrameAddress before)) {
                patched.Add(new(id, ToState(before), ToState(head)));
            } else { added.Add(new(id, ToState(head))); }
        }
        return DictionaryStateBody<uint, Locator, UInt32StateOps, LocatorOps>.PrepareDeltaFromOperations(
            Layout, removedIds.ToArray(), patched.ToArray(), added.ToArray());
    }

    internal static IReadOnlyDictionary<uint, FrameAddress> ReadBase(ReadOnlySpan<byte> body) {
        BareValueReader reader = new(body);
        FrozenDictionaryState<uint, Locator> state =
            DictionaryStateBody<uint, Locator, UInt32StateOps, LocatorOps>.ReadBase(ref reader, Layout);
        reader.EnsureFullyConsumed();
        return Project(state);
    }

    internal static IReadOnlyDictionary<uint, FrameAddress> ApplyDelta(IReadOnlyDictionary<uint, FrameAddress> prior,
        ReadOnlySpan<byte> body) {
        BareValueReader reader = new(body);
        FrozenDictionaryState<uint, Locator> state =
            DictionaryStateBody<uint, Locator, UInt32StateOps, LocatorOps>.ApplyDelta(ref reader, Freeze(prior), Layout);
        reader.EnsureFullyConsumed();
        return Project(state);
    }

    internal static Locator ToState(FrameAddress address) {
        Span<byte> bytes = stackalloc byte[FrameAddress.EncodedSize];
        if (!address.TryWrite(bytes)) { throw new InvalidDataException("A directory locator requires a valid address."); }
        return new(BinaryPrimitives.ReadUInt32LittleEndian(bytes),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]), BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]));
    }

    internal static FrameAddress FromState(in Locator state) {
        Span<byte> bytes = stackalloc byte[FrameAddress.EncodedSize];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, state.Segment0Field1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[4..], state.Segment0Field2);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[8..], state.Segment0Field3);
        if (!FrameAddress.TryRead(bytes, out FrameAddress address)) { throw new InvalidDataException("Invalid directory locator bits."); }
        return address;
    }

    private static FrozenDictionaryState<uint, Locator> Freeze(IReadOnlyDictionary<uint, FrameAddress> map) {
        Validate(map);
        DictionaryEntryState<uint, Locator>[] entries = map.OrderBy(static pair => pair.Key)
            .Select(pair => new DictionaryEntryState<uint, Locator>(pair.Key, ToState(pair.Value))).ToArray();
        return new(DictionaryComparerKind.ScalarDefault, entries);
    }

    private static IReadOnlyDictionary<uint, FrameAddress> Project(FrozenDictionaryState<uint, Locator> state) {
        if (state.ComparerKind != DictionaryComparerKind.ScalarDefault) {
            throw new InvalidDataException("The live directory requires ScalarDefault comparison.");
        }
        Dictionary<uint, FrameAddress> map = [];
        foreach (ref readonly DictionaryEntryState<uint, Locator> entry in state.Entries) {
            Locator value = entry.Value;
            if (entry.Key == 0 || !map.TryAdd(entry.Key, FromState(in value))) {
                throw new InvalidDataException("Directory IDs must be nonzero and unique.");
            }
        }
        return new FrozenSortedDictionary<uint, FrameAddress>(new(map));
    }

    private static void Validate(IReadOnlyDictionary<uint, FrameAddress> map) {
        ArgumentNullException.ThrowIfNull(map);
        foreach ((uint id, FrameAddress head) in map) {
            if (id == 0 || head == default) { throw new InvalidDataException("Directory IDs and locators must be nonzero."); }
        }
    }
}
