using System.Buffers.Binary;
using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Locator = Atelia.DurableGraph.Generated.Family_503048.V1;
using LocatorOps = Atelia.DurableGraph.Generated.Family_503048.BodyV1;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class DirectoryBulkReplayTests {
    private static FrameAddress Address(uint id) {
        Span<byte> bytes = stackalloc byte[FrameAddress.EncodedSize];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, id);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[4..], 1234UL << 26 | 20);
        Assert.True(FrameAddress.TryRead(bytes, out var result));
        return result;
    }

    [Fact]
    public void DirectoryDeltaAndMalformedBodiesMatchTheGenericDictionaryDecoder() {
        SortedDictionary<uint, FrameAddress> original = new() { [1] = Address(3), [2] = Address(4), [200] = Address(5) };
        SortedDictionary<uint, FrameAddress> changed = new(original) { [1] = Address(100), [5] = Address(9) };
        changed.Remove(2);
        byte[] valid = ObjectHeadDirectoryCodec.PrepareDelta(original, changed).Body.ToArray();
        Check(valid, expected: true);
        Check([0, 0, 0], expected: true);
        Check([.. valid, 0], expected: false);
        Check([2, 1, 1, 0, 0], expected: false); // repeated Remove
        var add = ObjectHeadDirectoryCodec.PrepareDelta(new Dictionary<uint, FrameAddress>(),
            new Dictionary<uint, FrameAddress> { [1] = Address(3) }).Body.ToArray();
        Check([1, 1, 0, .. add.AsSpan(2)], expected: false); // Remove and Add same prior key
        Check([0, 1, 1, 0, 0], expected: false); // empty inline value Patch
        Check([0, 0, 1, 3, 0, 0, 0], expected: false); // three zero locator words
        Check([.. add.AsSpan(0, 3), 0, .. add.AsSpan(4)], expected: false); // zero ID
        for (int i = 0; i < valid.Length; i++) {
            Check(valid[..i], expected: false);
            foreach (var mask in new byte[] { 1, 2, 4, 8, 16, 32, 64, 128, 255 }) {
                var altered = valid.ToArray();
                altered[i] ^= mask;
                Check(altered);
            }
        }

        void Check(byte[] body, bool? expected = null) {
            SortedDictionary<uint, FrameAddress> candidate = new(original);
            IReadOnlyDictionary<uint, FrameAddress>? reference = null;
            Exception? baseline = Record.Exception(() => { reference = ApplyReference(original, body); });
            Exception? actual = Record.Exception(() => ObjectHeadDirectoryCodec.ApplyOwnedDelta(candidate, [], body));
            if (expected is { } allowed) { Assert.Equal(allowed, baseline is null); }
            Assert.Equal(baseline is null, actual is null);
            if (actual is null) { Assert.Equal(reference!.ToArray(), candidate.ToArray()); }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(64)]
    public void RepeatedSparseAndDenseChangesOnlyRequireUniqueKeysWithinOneLayer(int count) {
        SortedDictionary<uint, FrameAddress> original = new();
        for (uint id = 1; id <= 64; id++) { original.Add(id, Address(id)); }
        SortedDictionary<uint, FrameAddress> candidate = new(original);
        HashSet<uint> touched = [];
        IReadOnlyDictionary<uint, FrameAddress> reference = original;
        for (uint layer = 1; layer <= 3; layer++) {
            SortedDictionary<uint, FrameAddress> expected = new(original);
            for (uint id = 1; id <= count; id++) { expected[id] = Address(id + 64 * layer); }
            byte[] delta = ObjectHeadDirectoryCodec.PrepareDelta(reference, expected).Body.ToArray();
            reference = ApplyReference(reference, delta);
            ObjectHeadDirectoryCodec.ApplyOwnedDelta(candidate, touched, delta);
            Assert.Equal(expected.ToArray(), reference.ToArray());
            Assert.Equal(reference.ToArray(), candidate.ToArray());
        }
    }

    // The original wire reader also projects the generic Dictionary result into sorted directory IDs.
    private static IReadOnlyDictionary<uint, FrameAddress> ApplyReference(IReadOnlyDictionary<uint, FrameAddress> prior,
        ReadOnlySpan<byte> body) {
        var entries = prior.Select(pair => new DictionaryEntryState<uint, Locator>(pair.Key, ObjectHeadDirectoryCodec.ToState(pair.Value))).ToArray();
        BareValueReader reader = new(body);
        var state = DictionaryStateBody<uint, Locator, UInt32StateOps, LocatorOps>.ApplyDelta(ref reader,
            new(DictionaryComparerKind.ScalarDefault, entries), ObjectHeadDirectoryCodec.Layout);
        reader.EnsureFullyConsumed();
        SortedDictionary<uint, FrameAddress> heads = new();
        foreach (ref readonly var entry in state.Entries) {
            if (entry.Key == 0 || !heads.TryAdd(entry.Key, ObjectHeadDirectoryCodec.FromState(entry.Value))) {
                throw new InvalidDataException("Invalid directory ID.");
            }
        }
        return heads;
    }
}
