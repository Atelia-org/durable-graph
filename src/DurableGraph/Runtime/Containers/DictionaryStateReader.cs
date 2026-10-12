using Atelia.Binary;
using Atelia.DurableGraph.Schema;
using System.Buffers;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Runtime;

/// <summary>Reads exact mapping contents independently of historical domain key/value CLR types.</summary>
public abstract class DictionaryStateReader : ObjectReaderBinding {
    private protected DictionaryStateReader(DictionaryLayout layout) : base(ObjectLayout.ForDictionary(layout)) { }

    public static DictionaryStateReader Create(DictionaryLayout layout, StateValueBinding key, StateValueBinding value) {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        if (layout.KeySlot != StateBindingContext.WithFieldId(key.Slot, 1) ||
            layout.ValueSlot != StateBindingContext.WithFieldId(value.Slot, 2)) {
            throw new ArgumentException("The key and value bindings must match the complete Dictionary layout.");
        }
        return (DictionaryStateReader)Activator.CreateInstance(typeof(DictionaryStateReader<,,,>)
            .MakeGenericType(key.StateType, value.StateType, key.StateOpsType, value.StateOpsType), layout)!;
    }

    internal abstract void ValidateLookupKeys(ObjectStateRecord state, Func<ObjectId, ObjectStateRecord> resolve);
}

internal sealed class DictionaryStateReader<K, V, KOps, VOps> : DictionaryStateReader
    where K : unmanaged where V : unmanaged where KOps : IStateOps<K> where VOps : IStateOps<V> {
    public DictionaryStateReader(DictionaryLayout layout) : base(layout) { }
    public override Type StateType => typeof(FrozenDictionaryState<K, V>);

    internal override ObjectStateRecord Read(ObjectId id, IStateBodySource source) {
        ArgumentNullException.ThrowIfNull(source);
        if (id.IsNull || source.Count <= 0) { throw new InvalidDataException("A Dictionary requires a nonzero ID and a Base body."); }
        BareValueReader reader = new(source.GetBody(0));
        FrozenDictionaryState<K, V> state = DictionaryStateBody<K, V, KOps, VOps>.ReadBase(ref reader, Layout.Dictionary!);
        reader.EnsureFullyConsumed();
        for (int index = 1; index < source.Count; index++) {
            reader = new(source.GetBody(index));
            state = DictionaryStateBody<K, V, KOps, VOps>.ApplyDelta(ref reader, state, Layout.Dictionary!);
            reader.EnsureFullyConsumed();
        }
        return new(id, Layout.Dictionary!, state);
    }

    internal override void VisitReferences(ObjectStateRecord item, IStateReferenceVisitor visitor) {
        Require(item);
        DictionaryStateBody<K, V, KOps, VOps>.VisitReferences(item.GetDictionaryState<K, V>(), visitor, Layout.Dictionary!);
    }

    internal override void ValidateLookupKeys(ObjectStateRecord state, Func<ObjectId, ObjectStateRecord> resolve) {
        Require(state);
        DictionaryStateBody<K, V, KOps, VOps>.ValidateLookupKeys(state.GetDictionaryState<K, V>(), Layout.Dictionary!, resolve);
    }

    private void Require(ObjectStateRecord row) {
        if (!Layout.Equals(row.Layout)) { throw new InvalidDataException("The Dictionary reader requires its exact layout."); }
    }
}

internal readonly record struct DictionaryValueChange<K, V>(K Key, V Prior, V Current)
    where K : unmanaged where V : unmanaged;

internal static class DictionaryStateBody<K, V, KOps, VOps>
    where K : unmanaged where V : unmanaged where KOps : IStateOps<K> where VOps : IStateOps<V> {
    internal static PreparedBaseBody PrepareBase(FrozenDictionaryState<K, V> state, DictionaryLayout layout) {
        KeyIndex keys = Index(state, layout);
        ValidateLocalKeys(state, layout, keys);
        ArrayBufferWriter<byte> buffer = new();
        BareValueWriter writer = new(buffer);
        writer.WriteByte((byte)state.ComparerKind);
        writer.WriteVarUInt32((uint)state.Count);
        for (int index = 0; index < state.Count; index++) {
            writer.WriteRawBytes(keys.Bytes[index]);
            V value = state.Entries[index].Value;
            VOps.WriteBase(ref writer, in value, layout.ValueSlot);
        }
        return new(buffer.WrittenSpan);
    }

    internal static FrozenDictionaryState<K, V> ReadBase(ref BareValueReader reader, DictionaryLayout layout) {
        DictionaryComparerKind kind = (DictionaryComparerKind)reader.ReadByte();
        DictionaryKeyPolicy.RequireStoredPolicy(kind, layout.KeySlot);
        int count = ReadCount(ref reader);
        DictionaryKeyPolicy.RequireKeyCount(count, layout.KeySlot);
        RequireTailFits(count, ref reader, layout, values: true);
        DictionaryEntryState<K, V>[] entries = new DictionaryEntryState<K, V>[count];
        for (int index = 0; index < count; index++) {
            K key = KOps.ReadBase(ref reader, layout.KeySlot);
            V value = VOps.ReadBase(ref reader, layout.ValueSlot);
            entries[index] = new(key, value);
        }
        FrozenDictionaryState<K, V> state = new(kind, entries, takeOwnership: true);
        ValidateLocal(state, layout);
        return state;
    }

    internal static PreparedDeltaBody PrepareDelta(FrozenDictionaryState<K, V> prior, FrozenDictionaryState<K, V> current,
        DictionaryLayout layout) {
        if (prior.ComparerKind != current.ComparerKind) { throw new InvalidDataException("A Dictionary Delta cannot change comparison policy."); }
        KeyIndex oldKeys = Index(prior, layout);
        KeyIndex newKeys = Index(current, layout);
        ValidateLocalKeys(prior, layout, oldKeys);
        ValidateLocalKeys(current, layout, newKeys);
        // TODO(DB-054): measure key-buffer reuse before adding persistent caches or StateHash.
        ArrayBufferWriter<byte> removals = new(), patches = new(), additions = new();
        BareValueWriter removeWriter = new(removals), patchWriter = new(patches), addWriter = new(additions);
        uint removeCount = 0, patchCount = 0, addCount = 0;
        for (int index = 0; index < prior.Count; index++) {
            if (!newKeys.Offsets.ContainsKey(oldKeys.Bytes[index])) {
                WriteRemoval(ref removeWriter, oldKeys.Bytes[index]);
                removeCount++;
            }
        }
        for (int index = 0; index < current.Count; index++) {
            byte[] key = newKeys.Bytes[index];
            V value = current.Entries[index].Value;
            if (oldKeys.Offsets.TryGetValue(key, out int previous)) {
                V oldValue = prior.Entries[previous].Value;
                if (WritePatch(ref patchWriter, key, in oldValue, in value, layout)) { patchCount++; }
            } else {
                WriteAddition(ref addWriter, key, in value, layout);
                addCount++;
            }
        }
        return CompleteDelta(removals, removeCount, patches, patchCount, additions, addCount);
    }

    /// <summary>Encodes owner-verified, reduced operations using the ordinary Dictionary Delta wire.</summary>
    /// <remarks>
    /// The owner proves exact prior values, Remove/Patch membership, Add absence and an unchanged comparison policy.
    /// This emitter encodes only supplied keys and values; it does not compare complete endpoints or authenticate their provenance.
    /// </remarks>
    internal static PreparedDeltaBody PrepareDeltaFromOperations(DictionaryLayout layout, ReadOnlySpan<K> removed,
        ReadOnlySpan<DictionaryValueChange<K, V>> patched, ReadOnlySpan<DictionaryEntryState<K, V>> added) {
        ArgumentNullException.ThrowIfNull(layout);
        HashSet<byte[]> operations = new(DictionaryKeyBytesComparer.Instance);
        ArrayBufferWriter<byte> removals = new(), patches = new(), additions = new();
        BareValueWriter removeWriter = new(removals), patchWriter = new(patches), addWriter = new(additions);
        uint removeCount = 0, patchCount = 0, addCount = 0;
        foreach (ref readonly K key in removed) {
            byte[] encoded = EncodeKey(in key, layout);
            RequireOperation(operations, encoded);
            WriteRemoval(ref removeWriter, encoded);
            removeCount++;
        }
        foreach (ref readonly DictionaryValueChange<K, V> change in patched) {
            K key = change.Key;
            byte[] encoded = EncodeKey(in key, layout);
            // Even a no-op Patch is an input operation and must not hide a repeated key.
            RequireOperation(operations, encoded);
            V prior = change.Prior, current = change.Current;
            if (WritePatch(ref patchWriter, encoded, in prior, in current, layout)) { patchCount++; }
        }
        foreach (ref readonly DictionaryEntryState<K, V> entry in added) {
            K key = entry.Key;
            byte[] encoded = EncodeKey(in key, layout);
            RequireOperation(operations, encoded);
            V value = entry.Value;
            WriteAddition(ref addWriter, encoded, in value, layout);
            addCount++;
        }
        return CompleteDelta(removals, removeCount, patches, patchCount, additions, addCount);
    }

    private static void WriteRemoval(ref BareValueWriter writer, ReadOnlySpan<byte> key) => writer.WriteRawBytes(key);

    private static bool WritePatch(ref BareValueWriter writer, ReadOnlySpan<byte> key, in V prior, in V current,
        DictionaryLayout layout) {
        PreparedDeltaBody delta = VOps.PrepareDelta(in prior, in current, layout.ValueSlot);
        if (!delta.HasChanges) { return false; }
        writer.WriteRawBytes(key);
        writer.WriteRawBytes(delta.Body);
        return true;
    }

    private static void WriteAddition(ref BareValueWriter writer, ReadOnlySpan<byte> key, in V value,
        DictionaryLayout layout) {
        writer.WriteRawBytes(key);
        VOps.WriteBase(ref writer, in value, layout.ValueSlot);
    }

    private static PreparedDeltaBody CompleteDelta(ArrayBufferWriter<byte> removals, uint removeCount,
        ArrayBufferWriter<byte> patches, uint patchCount, ArrayBufferWriter<byte> additions, uint addCount) {
        ArrayBufferWriter<byte> buffer = new();
        BareValueWriter writer = new(buffer);
        writer.WriteVarUInt32(removeCount);
        writer.WriteRawBytes(removals.WrittenSpan);
        writer.WriteVarUInt32(patchCount);
        writer.WriteRawBytes(patches.WrittenSpan);
        writer.WriteVarUInt32(addCount);
        writer.WriteRawBytes(additions.WrittenSpan);
        return new(removeCount != 0 || patchCount != 0 || addCount != 0, buffer.WrittenSpan);
    }

    internal static FrozenDictionaryState<K, V> ApplyDelta(ref BareValueReader reader, FrozenDictionaryState<K, V> prior,
        DictionaryLayout layout) {
        KeyIndex oldKeys = Index(prior, layout);
        ValidateLocalKeys(prior, layout, oldKeys);
        HashSet<byte[]> operations = new(DictionaryKeyBytesComparer.Instance);
        HashSet<int> removed = [];
        Dictionary<int, V> patches = [];
        int removeCount = ReadCount(ref reader);
        if (removeCount > prior.Count) { throw new InvalidDataException("Dictionary removals exceed prior count."); }
        RequireTailFits(removeCount, ref reader, layout, values: false);
        for (int index = 0; index < removeCount; index++) {
            byte[] encoded = ReadKey(ref reader, layout, out _);
            RequireOperation(operations, encoded);
            if (!oldKeys.Offsets.TryGetValue(encoded, out int previous)) { throw new InvalidDataException("Dictionary Remove requires an existing prior key."); }
            removed.Add(previous);
        }
        int patchCount = ReadCount(ref reader);
        if (patchCount > prior.Count - removeCount) { throw new InvalidDataException("Dictionary patches exceed remaining prior count."); }
        RequireTailFits(patchCount, ref reader, layout, values: false);
        for (int index = 0; index < patchCount; index++) {
            byte[] encoded = ReadKey(ref reader, layout, out _);
            RequireOperation(operations, encoded);
            if (!oldKeys.Offsets.TryGetValue(encoded, out int previous)) { throw new InvalidDataException("Dictionary Patch requires an existing prior key."); }
            V oldValue = prior.Entries[previous].Value;
            V value = VOps.ApplyDelta(ref reader, in oldValue, layout.ValueSlot);
            if (VOps.StateEquals(in oldValue, in value, layout.ValueSlot)) { throw new InvalidDataException("Dictionary value Patch must change the value."); }
            patches.Add(previous, value);
        }
        int addCount = ReadCount(ref reader);
        long resultCount = (long)prior.Count - removeCount + addCount;
        if (resultCount > Array.MaxLength) { throw new InvalidDataException("Dictionary result count exceeds supported buffer length."); }
        DictionaryKeyPolicy.RequireKeyCount((int)resultCount, layout.KeySlot);
        RequireTailFits(addCount, ref reader, layout, values: true);
        DictionaryEntryState<K, V>[] entries = new DictionaryEntryState<K, V>[(int)resultCount];
        int output = 0;
        for (int index = 0; index < prior.Count; index++) {
            if (removed.Contains(index)) { continue; }
            DictionaryEntryState<K, V> entry = prior.Entries[index];
            entries[output++] = patches.TryGetValue(index, out V value) ? new(entry.Key, value) : entry;
        }
        for (int index = 0; index < addCount; index++) {
            byte[] encoded = ReadKey(ref reader, layout, out K key);
            RequireOperation(operations, encoded);
            if (oldKeys.Offsets.ContainsKey(encoded)) { throw new InvalidDataException("Dictionary Add requires a key absent from prior."); }
            entries[output++] = new(key, VOps.ReadBase(ref reader, layout.ValueSlot));
        }
        FrozenDictionaryState<K, V> state = new(prior.ComparerKind, entries, takeOwnership: true);
        ValidateLocal(state, layout);
        return state;
    }

    internal static void VisitReferences(FrozenDictionaryState<K, V> state, IStateReferenceVisitor visitor, DictionaryLayout layout) {
        foreach (ref readonly DictionaryEntryState<K, V> entry in state.Entries) {
            K key = entry.Key;
            V value = entry.Value;
            KOps.VisitReferences(in key, visitor, layout.KeySlot);
            VOps.VisitReferences(in value, visitor, layout.ValueSlot);
        }
    }

    internal static void ValidateLocal(FrozenDictionaryState<K, V> state, DictionaryLayout layout) =>
        ValidateLocalKeys(state, layout, Index(state, layout));

    internal static void ValidateLookupKeys(FrozenDictionaryState<K, V> state, DictionaryLayout layout,
        Func<ObjectId, ObjectStateRecord> resolve) {
        ArgumentNullException.ThrowIfNull(resolve);
        KeyIndex keys = Index(state, layout);
        ValidateLocalKeys(state, layout, keys);
        if (state.ComparerKind is DictionaryComparerKind.ScalarDefault or DictionaryComparerKind.CurrentDefault or DictionaryComparerKind.Application) { return; }
        HashSet<string>? strings = state.ComparerKind switch {
            DictionaryComparerKind.StringOrdinal => new(StringComparer.Ordinal),
            DictionaryComparerKind.StringOrdinalIgnoreCase => new(StringComparer.OrdinalIgnoreCase),
            _ => null,
        };
        HashSet<ObjectId> identities = [];
        foreach (byte[] encoded in keys.Bytes) {
            ObjectId id = DictionaryKeyPolicy.ReadReference(encoded);
            ObjectStateRecord row = resolve(id) ?? throw new InvalidDataException("Dictionary key target is missing.");
            if (row.Id != id || (layout.KeySlot.TypeTag == TypeTag.String && row.Kind != ObjectStateKind.String)) {
                throw new InvalidDataException("Dictionary key resolves to an incompatible target.");
            }
            if (strings is not null) {
                if (row.Kind != ObjectStateKind.String || !strings.Add(row.StringContent)) {
                    throw new InvalidDataException("Dictionary contains duplicate lookup keys under its string comparison policy.");
                }
            } else {
                // Zero is not a legal key ID, so it can represent the one canonical Empty identity.
                ObjectId identity = row.Kind == ObjectStateKind.String && row.StringContent.Length == 0 ? default : id;
                if (!identities.Add(identity)) { throw new InvalidDataException("Dictionary keys collide after Empty canonicalization."); }
            }
        }
    }

    private static void ValidateLocalKeys(FrozenDictionaryState<K, V> state, DictionaryLayout layout, KeyIndex keys) {
        DictionaryKeyPolicy.RequireStoredPolicy(state.ComparerKind, layout.KeySlot);
        if (state.ComparerKind == DictionaryComparerKind.ScalarDefault) {
            HashSet<object> lookup = [];
            foreach (byte[] encoded in keys.Bytes) {
                if (!lookup.Add(DictionaryKeyPolicy.ReadScalar(encoded, layout.KeySlot))) {
                    throw new InvalidDataException("Dictionary contains duplicate scalar lookup keys.");
                }
            }
        } else if (layout.KeySlot.TypeTag is TypeTag.String or TypeTag.ObjectReference) {
            foreach (byte[] encoded in keys.Bytes) { DictionaryKeyPolicy.ReadReference(encoded); }
        }
    }

    private static KeyIndex Index(FrozenDictionaryState<K, V> state, DictionaryLayout layout) {
        DictionaryKeyPolicy.RequireStoredPolicy(state.ComparerKind, layout.KeySlot);
        DictionaryKeyPolicy.RequireKeyCount(state.Count, layout.KeySlot);
        byte[][] bytes = new byte[state.Count][];
        Dictionary<byte[], int> offsets = new(DictionaryKeyBytesComparer.Instance);
        for (int index = 0; index < state.Count; index++) {
            K key = state.Entries[index].Key;
            bytes[index] = EncodeKey(in key, layout);
            if (!offsets.TryAdd(bytes[index], index)) { throw new InvalidDataException("Dictionary contains duplicate persistent keys."); }
        }
        return new(bytes, offsets);
    }

    private static byte[] EncodeKey(in K key, DictionaryLayout layout) {
        ArrayBufferWriter<byte> buffer = new();
        BareValueWriter writer = new(buffer);
        KOps.WriteBase(ref writer, in key, layout.KeySlot);
        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] ReadKey(ref BareValueReader reader, DictionaryLayout layout, out K key) {
        key = KOps.ReadBase(ref reader, layout.KeySlot);
        return EncodeKey(in key, layout);
    }

    private static void RequireOperation(HashSet<byte[]> operations, byte[] encoded) {
        if (!operations.Add(encoded)) { throw new InvalidDataException("Dictionary Delta repeats a persistent key within or across operation groups."); }
    }

    private static int ReadCount(ref BareValueReader reader) {
        uint count = reader.ReadVarUInt32();
        if (count > Array.MaxLength) { throw new InvalidDataException("Dictionary count exceeds supported buffer length."); }
        return (int)count;
    }

    private static void RequireTailFits(int count, ref BareValueReader reader, DictionaryLayout layout, bool values) {
        long minimum = StateBodySize.MinimumBaseBytes(layout.KeySlot);
        if (values) { minimum += StateBodySize.MinimumBaseBytes(layout.ValueSlot); }
        if (minimum > 0 && count > reader.RemainingCount / minimum) {
            throw new InvalidDataException("Dictionary entries cannot fit in the remaining payload.");
        }
    }

    private sealed record KeyIndex(byte[][] Bytes, Dictionary<byte[], int> Offsets);
}

internal sealed class DictionaryKeyBytesComparer : IEqualityComparer<byte[]> {
    internal static DictionaryKeyBytesComparer Instance { get; } = new();
    public bool Equals(byte[]? left, byte[]? right) => ReferenceEquals(left, right) ||
        (left is not null && right is not null && left.AsSpan().SequenceEqual(right));
    public int GetHashCode(byte[] value) {
        HashCode hash = new();
        hash.AddBytes(value);
        return hash.ToHashCode();
    }
}
