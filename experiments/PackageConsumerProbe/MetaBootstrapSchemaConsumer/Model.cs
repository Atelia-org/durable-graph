extern alias persistence;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Schema;
using persistence::Atelia.DurableGraph.Persistence;

namespace BootstrapSchemaClosure;

// Combines the Schema-closure story with the standalone List, Array and Dictionary protocols.
// This source is copied into an isolated package consumer and compiled by the packaged SG.
[DurableType("p1.closure.base", 1)]
public abstract partial class Base : IDurableObject {
    [DurableField(1)] public int Seed;
    [DurableField(2)] public double A;
    [DurableField(3)] public double B;
    [DurableField(4)] public double C;
    [DurableField(5)] public double D;
}

[DurableType("p1.closure.point", 1)]
public partial struct Point {
    [DurableField(1)] public int X;
    [DurableField(2)] public int? Rank;
}

[DurableType("p1.closure.key", 1)]
public partial struct Key : IEquatable<Key> {
    [DurableField(1)] public int X;
    [DurableField(2)] public int? Rank;
    public bool Equals(Key other) => X == other.X && Rank == other.Rank;
    public override bool Equals(object? value) => value is Key key && Equals(key);
    public override int GetHashCode() => HashCode.Combine(X, Rank);
}

[DurableType("p1.closure.root", 1)]
public sealed partial class Root : Base {
    [DurableField(1)] public Point Position;
    [DurableField(2)] public Point? OptionalPoint;
    [DurableField(3)] public int? Count;
    [DurableField(4)] public List<Point?> Samples = new();
    [DurableField(5)] public List<long?> Optional = new();
    [DurableField(6)] public List<List<int>> Nested = new();
    [DurableField(7)] public int?[] ArrayOptional = new int?[128];
    [DurableField(8)] public Point?[,] Points = new Point?[16, 8];
    [DurableField(9)] public Dictionary<int, long?> Scalars = new();
    [DurableField(10)] public Dictionary<Key, Point?> Entries = new();
}

public static class Host {
    public static StateModelRegistry Models() {
        StateModelRegistry models = new();
        Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
        return models;
    }

    public static IDurableObject Create() {
        Root root = new() { Seed = 17, A = 1, B = 2, C = 3, D = 4,
            Position = new() { X = 7, Rank = 11 }, OptionalPoint = new Point { X = 13, Rank = null } };
        for (int i = 0; i < 128; i++) {
            root.Samples.Add(i % 7 == 0 ? null : new Point { X = 1000 + i, Rank = i % 3 == 0 ? null : i });
        }
        for (int i = 0; i < 256; i++) { root.Optional.Add(ExpectedOptional(i, 0)); }
        List<int> inner = new();
        for (int i = 0; i < 128; i++) { inner.Add(1000 + i); }
        for (int i = 0; i < 64; i++) { root.Nested.Add(inner); }
        for (int i = 0; i < 128; i++) { root.ArrayOptional[i] = ExpectedArrayOptional(i, 0); }
        for (int row = 0; row < 16; row++) {
            for (int column = 0; column < 8; column++) { root.Points[row, column] = ExpectedArrayPoint(row, column, 0); }
        }
        foreach (int key in DictionaryKeys(0)) {
            root.Scalars.Add(key, ExpectedScalarValue(key, 0));
            root.Entries.Add(KeyFor(key), ExpectedEntryValue(key, 0));
        }
        return root;
    }

    public static object[] Objects(IDurableObject value) {
        Root root = (Root)value;
        return [root, root.Samples, root.Optional, root.Nested, root.Nested[0], root.ArrayOptional, root.Points, root.Scalars, root.Entries];
    }

    public static long? ExpectedOptional(int index, int stage) => stage == 1 && index == 30 ? long.MaxValue - 23
        : index % 5 == 0 || stage == 1 && index == 31 ? null : (long)int.MaxValue + 1000 + index;

    public static int? ExpectedArrayOptional(int index, int stage) => stage == 1 && index == 20 ? int.MinValue + 17
        : stage == 1 && index == 63 ? -12345 : index % 5 == 0 || stage == 1 && index == 21 ? null : 1000 + index;

    public static Point? ExpectedArrayPoint(int row, int column, int stage) {
        if (stage == 1) {
            if (row == 0 && column == 0) { return new Point { X = 9000, Rank = 100 }; }
            if (row == 7 && column == 3) { return new Point { X = 7003, Rank = null }; }
            if (row == 7 && column == 2) { return null; }
        }
        int flat = row * 8 + column;
        return flat % 9 == 0 ? null : new Point { X = 1000 + flat, Rank = flat % 4 == 0 ? null : flat };
    }

    public static Key KeyFor(int index) => new() { X = index, Rank = index % 3 == 0 ? null : 10000 + index };

    public static IEnumerable<int> DictionaryKeys(int stage) {
        for (int i = 0; i < 128; i++) { if (stage == 0 || i != 5) { yield return i; } }
        if (stage == 1) { yield return 1000; }
    }

    public static long? ExpectedScalarValue(int key, int stage) => stage == 1 ? key switch {
        6 => null, 7 => long.MaxValue - 23, 10 => long.MinValue + 17, 1000 => 77700000007,
        _ => key % 5 == 0 ? null : (long)int.MaxValue + 1000 + key,
    } : key % 5 == 0 ? null : (long)int.MaxValue + 1000 + key;

    public static Point? ExpectedEntryValue(int key, int stage) {
        if (stage == 1) {
            if (key == 6) { return null; }
            if (key == 7) { return new Point { X = 7007, Rank = null }; }
            if (key == 10) { return new Point { X = 9000, Rank = 100 }; }
            if (key == 1000) { return new Point { X = 8000, Rank = 1000 }; }
        }
        return key % 5 == 0 ? null : new Point { X = 1000 + key, Rank = key % 4 == 0 ? null : key };
    }

    public static void Change(IDurableObject value) {
        Root root = (Root)value;
        root.Seed = 18;
        root.Position = new() { X = 8, Rank = null };
        root.OptionalPoint = null;
        root.Count = 99;
        root.Samples[31] = new Point { X = 7031, Rank = null };
        root.Optional[30] = long.MaxValue - 23;
        root.Optional[31] = null;
        List<int> inner = root.Nested[0];
        inner[17] = 7777;
        root.Nested.Add(inner);
        root.ArrayOptional[20] = int.MinValue + 17;
        root.ArrayOptional[21] = null;
        root.ArrayOptional[63] = -12345;
        root.Points[0, 0] = new Point { X = 9000, Rank = 100 };
        root.Points[7, 3] = new Point { X = 7003, Rank = null };
        root.Points[7, 2] = null;
        root.Scalars.Remove(5);
        root.Scalars.Add(1000, 77700000007);
        root.Scalars[6] = null;
        root.Scalars[7] = long.MaxValue - 23;
        root.Scalars[10] = long.MinValue + 17;
        root.Entries.Remove(KeyFor(5));
        root.Entries.Add(KeyFor(1000), new Point { X = 8000, Rank = 1000 });
        root.Entries[KeyFor(6)] = null;
        root.Entries[KeyFor(7)] = new Point { X = 7007, Rank = null };
        root.Entries[KeyFor(10)] = new Point { X = 9000, Rank = 100 };
        var scalarPairs = root.Scalars.ToArray();
        root.Scalars.Clear();
        foreach (var pair in scalarPairs.Reverse()) { root.Scalars.Add(pair.Key, pair.Value); }
        var entryPairs = root.Entries.ToArray();
        root.Entries.Clear();
        foreach (var pair in entryPairs.Reverse()) { root.Entries.Add(pair.Key, pair.Value); }
    }

    public static void Check(IDurableObject value, int stage) {
        Root root = (Root)value;
        if (root.Seed != (stage == 0 ? 17 : 18) || root.A != 1 || root.B != 2 || root.C != 3 || root.D != 4 ||
            root.Position.X != (stage == 0 ? 7 : 8) || root.Position.Rank != (stage == 0 ? 11 : (int?)null) ||
            root.Count != (stage == 0 ? (int?)null : 99)) { throw new InvalidOperationException("Root/base/inline state mismatch."); }
        if (stage == 0 ? root.OptionalPoint is not { X: 13, Rank: null } : root.OptionalPoint is not null) {
            throw new InvalidOperationException("Nullable inline state mismatch.");
        }
        if (root.Samples.Count != 128) { throw new InvalidOperationException("List count mismatch."); }
        for (int i = 0; i < 128; i++) {
            Point? sample = root.Samples[i];
            if (i % 7 == 0) {
                if (sample is not null) { throw new InvalidOperationException("Null List element mismatch."); }
            } else {
                int x = stage == 1 && i == 31 ? 7031 : 1000 + i;
                int? rank = i % 3 == 0 || stage == 1 && i == 31 ? null : i;
                if (sample is not { } present || present.X != x || present.Rank != rank) {
                    throw new InvalidOperationException("Nullable inline List element mismatch.");
                }
            }
        }
        if (root.Optional.Count != 256 || root.Nested.Count != (stage == 0 ? 64 : 65)) {
            throw new InvalidOperationException("Builtin List count mismatch.");
        }
        for (int i = 0; i < 256; i++) {
            if (root.Optional[i] != ExpectedOptional(i, stage)) { throw new InvalidOperationException("Nullable Int64 value mismatch."); }
        }
        List<int> inner = root.Nested[0];
        if (inner.Count != 128) { throw new InvalidOperationException("Inner List count mismatch."); }
        foreach (List<int> candidate in root.Nested) {
            if (!ReferenceEquals(candidate, inner)) { throw new InvalidOperationException("Shared inner List identity mismatch."); }
        }
        for (int i = 0; i < 128; i++) {
            if (inner[i] != (stage == 1 && i == 17 ? 7777 : 1000 + i)) {
                throw new InvalidOperationException("Inner Int32 value mismatch.");
            }
        }
        if (!root.ArrayOptional.GetType().IsSZArray || root.ArrayOptional.Rank != 1 || root.ArrayOptional.Length != 128 ||
            root.ArrayOptional.GetLowerBound(0) != 0 || root.Points.GetType().IsSZArray || root.Points.Rank != 2 ||
            root.Points.GetLength(0) != 16 || root.Points.GetLength(1) != 8 || root.Points.GetLowerBound(0) != 0 || root.Points.GetLowerBound(1) != 0) {
            throw new InvalidOperationException("Array shape mismatch.");
        }
        for (int i = 0; i < 128; i++) {
            if (root.ArrayOptional[i] != ExpectedArrayOptional(i, stage)) { throw new InvalidOperationException("Nullable Int32 array mismatch."); }
        }
        for (int row = 0; row < 16; row++) {
            for (int column = 0; column < 8; column++) {
                Point? pointValue = root.Points[row, column], expected = ExpectedArrayPoint(row, column, stage);
                if (pointValue.HasValue != expected.HasValue || pointValue is { } actual && expected is { } present &&
                    (actual.X != present.X || actual.Rank != present.Rank)) {
                    throw new InvalidOperationException("Nullable Point array mismatch.");
                }
            }
        }
        if (root.Scalars.Count != 128 || root.Entries.Count != 128 || root.Scalars.ContainsKey(5) != (stage == 0) ||
            root.Entries.ContainsKey(KeyFor(5)) != (stage == 0)) { throw new InvalidOperationException("Dictionary membership mismatch."); }
        foreach (int key in DictionaryKeys(stage)) {
            if (!root.Scalars.TryGetValue(key, out long? scalar) || scalar != ExpectedScalarValue(key, stage)) {
                throw new InvalidOperationException("Scalar Dictionary value mismatch.");
            }
            Key fullKey = KeyFor(key), alteredKey = new() { X = fullKey.X, Rank = fullKey.Rank.HasValue ? null : -1 };
            if (root.Entries.ContainsKey(alteredKey) || root.Entries.Comparer.Equals(fullKey, alteredKey) ||
                !root.Entries.Comparer.Equals(fullKey, KeyFor(key)) || !root.Entries.TryGetValue(fullKey, out Point? entry)) {
                throw new InvalidOperationException("Dictionary complete key/comparer mismatch.");
            }
            Point? expected = ExpectedEntryValue(key, stage);
            if (entry.HasValue != expected.HasValue || entry is { } actual && expected is { } present &&
                (actual.X != present.X || actual.Rank != present.Rank)) { throw new InvalidOperationException("Inline Dictionary value mismatch."); }
        }
    }
}
