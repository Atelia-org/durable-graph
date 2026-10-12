extern alias persistence;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Schema;
using persistence::Atelia.DurableGraph.Persistence;

namespace BootstrapSchemaClosure;

// Combines the Schema-closure story with MetaBootstrapBuiltinListGeneratorTests' List protocol.
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

[DurableType("p1.closure.root", 1)]
public sealed partial class Root : Base {
    [DurableField(1)] public Point Position;
    [DurableField(2)] public Point? OptionalPoint;
    [DurableField(3)] public int? Count;
    [DurableField(4)] public List<Point?> Samples = new();
    [DurableField(5)] public List<long?> Optional = new();
    [DurableField(6)] public List<List<int>> Nested = new();
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
        return root;
    }

    public static object[] Objects(IDurableObject value) {
        Root root = (Root)value;
        return [root, root.Samples, root.Optional, root.Nested, root.Nested[0]];
    }

    public static long? ExpectedOptional(int index, int stage) => stage == 1 && index == 30 ? long.MaxValue - 23
        : index % 5 == 0 || stage == 1 && index == 31 ? null : (long)int.MaxValue + 1000 + index;

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
    }
}
