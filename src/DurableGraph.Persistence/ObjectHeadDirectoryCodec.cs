
using System.Buffers.Binary;
using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using Locator = Atelia.DurableGraph.Generated.Family_503048.V1;
using LocatorOps = Atelia.DurableGraph.Generated.Family_503048.BodyV1;
using DirectoryBody = Atelia.DurableGraph.Runtime.DictionaryStateBody<uint, Atelia.DurableGraph.Generated.Family_503048.V1,
    Atelia.DurableGraph.Runtime.UInt32StateOps, Atelia.DurableGraph.Generated.Family_503048.BodyV1>;

namespace Atelia.DurableGraph.Persistence;

/// <summary>The ordinary Dictionary body, with a real generated inline locator value.</summary>
internal static class ObjectHeadDirectoryCodec {
    internal static DictionaryLayout Layout { get; } = CreateLayout();

    private static DictionaryLayout CreateLayout() {
        ModelExecutionEnvironment employees = BootstrapBindings.CreateEnvironment();
        return new(new(1, TypeTag.UInt32), employees.ResolveCurrentValue(typeof(ObjectHeadLocator)).Slot);
    }

    internal static PreparedBaseBody PrepareBase(IReadOnlyDictionary<uint, FrameAddress> current) =>
        DirectoryBody.PrepareBase(Freeze(current), Layout);

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
        return DirectoryBody.PrepareDeltaFromOperations(
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
        return DirectoryBody.PrepareDeltaFromOperations(
            Layout, removedIds.ToArray(), patched.ToArray(), added.ToArray());
    }

    internal static FrozenSortedDictionary<uint, FrameAddress> ReadBase(ReadOnlySpan<byte> body) {
        BareValueReader reader = new(body);
        FrozenDictionaryState<uint, Locator> state =
            DirectoryBody.ReadBase(ref reader, Layout);
        reader.EnsureFullyConsumed();
        return Project(state);
    }

    // Only validated directory Base/prefix entries enter this builder. The caller owns
    // it until every body is consumed, including the generated locator's slot validation.
    // For a uint key the value itself identifies both its canonical encoding and its lookup key.
    internal static void ApplyOwnedDelta(SortedDictionary<uint, FrameAddress> heads, HashSet<uint> touched,
        ReadOnlySpan<byte> body) {
        touched.Clear();
        BareValueReader reader = new(body);
        int removed = DirectoryBody.ReadCount(ref reader);
        if (removed > heads.Count) { throw new InvalidDataException("Directory removals exceed prior count."); }
        DirectoryBody.RequireTailFits(removed, ref reader, Layout, values: false);
        for (int i = 0; i < removed; i++) {
            uint id = ReadOperationKey(ref reader, touched);
            if (!heads.Remove(id)) { throw new InvalidDataException("Directory Remove requires an existing key."); }
        }

        int patched = DirectoryBody.ReadCount(ref reader);
        if (patched > heads.Count) { throw new InvalidDataException("Directory patches exceed remaining prior count."); }
        DirectoryBody.RequireTailFits(patched, ref reader, Layout, values: false);
        for (int i = 0; i < patched; i++) {
            uint id = ReadOperationKey(ref reader, touched);
            if (!heads.TryGetValue(id, out FrameAddress before)) { throw new InvalidDataException("Directory Patch requires an existing key."); }
            Locator prior = ToState(before);
            Locator next = LocatorOps.ApplyDelta(ref reader, in prior, Layout.ValueSlot);
            if (LocatorOps.StateEquals(in prior, in next, Layout.ValueSlot)) { throw new InvalidDataException("Directory Patch must change its value."); }
            heads[id] = FromState(in next);
        }

        int added = DirectoryBody.ReadCount(ref reader);
        long total = (long)heads.Count + added;
        if (total > Array.MaxLength) { throw new InvalidDataException("Directory result exceeds supported length."); }
        DictionaryKeyPolicy.RequireKeyCount((int)total, Layout.KeySlot);
        DirectoryBody.RequireTailFits(added, ref reader, Layout, values: true);
        for (int i = 0; i < added; i++) {
            uint id = ReadOperationKey(ref reader, touched);
            if (heads.ContainsKey(id)) { throw new InvalidDataException("Directory Add requires an absent key."); }
            Locator next = LocatorOps.ReadBase(ref reader, Layout.ValueSlot);
            heads.Add(id, FromState(in next));
        }
        reader.EnsureFullyConsumed();
    }

    private static uint ReadOperationKey(ref BareValueReader reader, HashSet<uint> touched) {
        uint id = UInt32StateOps.ReadBase(ref reader, Layout.KeySlot);
        if (id == 0 || !touched.Add(id)) { throw new InvalidDataException("Directory operation IDs must be nonzero and unique per body."); }
        return id;
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

    private static FrozenSortedDictionary<uint, FrameAddress> Project(FrozenDictionaryState<uint, Locator> state) {
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
