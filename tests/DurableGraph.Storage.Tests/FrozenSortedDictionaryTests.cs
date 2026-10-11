using System.Collections;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Storage.Tests;

public sealed class FrozenSortedDictionaryTests {
    [Fact]
    public void Empty_dictionary_has_no_values_and_rejects_indexer_miss() {
        FrozenSortedDictionary<uint, FrameAddress> dictionary = new([]);

        Assert.Empty(dictionary);
        Assert.Empty(dictionary.Keys);
        Assert.Empty(dictionary.Values);
        Assert.False(dictionary.ContainsKey(1));
        Assert.False(dictionary.TryGetValue(1, out FrameAddress missing));
        Assert.Equal(default, missing);
        Assert.Throws<KeyNotFoundException>(() => dictionary[1]);
    }

    [Fact]
    public void Single_item_supports_hit_and_misses_on_both_sides() {
        FrameAddress expected = Address(32);
        FrozenSortedDictionary<uint, FrameAddress> dictionary = new(new() { [42] = expected });

        Assert.Single(dictionary);
        Assert.True(dictionary.ContainsKey(42));
        Assert.True(dictionary.TryGetValue(42, out FrameAddress actual));
        Assert.Equal(expected, actual);
        Assert.Equal(expected, dictionary[42]);
        Assert.False(dictionary.ContainsKey(41));
        Assert.False(dictionary.TryGetValue(43, out _));
        Assert.Throws<KeyNotFoundException>(() => dictionary[43]);
    }

    [Fact]
    public void Sparse_uint_keys_preserve_sorted_key_value_correspondence() {
        SortedDictionary<uint, FrameAddress> builder = new() {
            [uint.MaxValue] = Address(32),
            [1] = Address(64),
            [4_000_000_000] = Address(96),
            [17] = Address(128),
            [65_537] = Address(160),
        };
        FrozenSortedDictionary<uint, FrameAddress> dictionary = new(builder);

        Assert.Equal(builder.Count, dictionary.Count);
        Assert.Equal(builder.Keys, dictionary.Keys);
        Assert.Equal(builder.Values, dictionary.Values);
        Assert.Equal(builder.ToArray(), dictionary.ToArray());
        Assert.Equal(builder.ToArray(), ((IEnumerable)dictionary).Cast<KeyValuePair<uint, FrameAddress>>().ToArray());
        foreach ((uint key, FrameAddress expected) in builder) {
            Assert.True(dictionary.ContainsKey(key));
            Assert.True(dictionary.TryGetValue(key, out FrameAddress actual));
            Assert.Equal(expected, actual);
            Assert.Equal(expected, dictionary[key]);
        }

        foreach (uint missing in new uint[] { 0, 2, 16, 18, 65_536, 65_538, 3_999_999_999, uint.MaxValue - 1 }) {
            Assert.False(dictionary.ContainsKey(missing));
            Assert.False(dictionary.TryGetValue(missing, out FrameAddress value));
            Assert.Equal(default, value);
            Assert.Throws<KeyNotFoundException>(() => dictionary[missing]);
        }
    }

    [Fact]
    public void Freeze_owns_arrays_and_read_projections_do_not_expose_them() {
        FrameAddress first = Address(32);
        FrameAddress second = Address(64);
        SortedDictionary<uint, FrameAddress> builder = new() { [1] = first, [9] = second };
        FrozenSortedDictionary<uint, FrameAddress> dictionary = new(builder);
        builder[1] = Address(96);
        builder.Remove(9);
        builder.Add(5, Address(128));
        builder.Clear();

        uint[] keysCopy = dictionary.Keys.ToArray();
        FrameAddress[] valuesCopy = dictionary.Values.ToArray();
        KeyValuePair<uint, FrameAddress>[] pairsCopy = dictionary.ToArray();
        keysCopy[0] = 99;
        valuesCopy[0] = default;
        pairsCopy[0] = default;

        Assert.Equal([1u, 9u], dictionary.Keys);
        Assert.Equal([first, second], dictionary.Values);
        Assert.Equal(first, dictionary[1]);
        Assert.Equal(second, dictionary[9]);
        Assert.False((object)dictionary is IDictionary<uint, FrameAddress>);
        Assert.False(dictionary.Keys is ICollection<uint>);
        Assert.False(dictionary.Values is ICollection<FrameAddress>);
        foreach (object projection in new object[] { dictionary, dictionary.Keys, dictionary.Values }) {
            Assert.False(projection is Array);
            Assert.False(projection is ICollection);
            Assert.False(projection is IDictionary);
        }
    }

    [Fact]
    public void Freeze_rejects_custom_sorting() {
        SortedDictionary<uint, FrameAddress> descending = new(Comparer<uint>.Create((left, right) => right.CompareTo(left))) {
            [1] = Address(32),
            [2] = Address(64),
        };

        Assert.Throws<ArgumentException>(() => new FrozenSortedDictionary<uint, FrameAddress>(descending));
    }

    private static FrameAddress Address(long offset) => StorageTestFrames.Address(1, offset);
}
