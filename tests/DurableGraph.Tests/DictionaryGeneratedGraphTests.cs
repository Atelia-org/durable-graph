using System.Reflection;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Theory]
    [InlineData("bool", "false", "true")]
    [InlineData("byte", "byte.MinValue", "byte.MaxValue")]
    [InlineData("sbyte", "sbyte.MinValue", "sbyte.MaxValue")]
    [InlineData("short", "short.MinValue", "short.MaxValue")]
    [InlineData("ushort", "ushort.MinValue", "ushort.MaxValue")]
    [InlineData("int", "int.MinValue", "int.MaxValue")]
    [InlineData("uint", "uint.MinValue", "uint.MaxValue")]
    [InlineData("long", "long.MinValue", "long.MaxValue")]
    [InlineData("ulong", "ulong.MinValue", "ulong.MaxValue")]
    [InlineData("char", "'\\0'", "'\\uffff'")]
    [InlineData("System.Half", "System.Half.NaN", "System.Half.PositiveInfinity")]
    [InlineData("float", "float.NaN", "float.PositiveInfinity")]
    [InlineData("double", "double.NaN", "double.PositiveInfinity")]
    public void DictionaryGeneratedScalarKeysSurviveReorderedCommitBaselines(string type, string first, string second) {
        RunDictionaryKeyGraph(type, first, second, "");
    }

    [Theory]
    [InlineData("byte", "byte.MinValue", "byte.MaxValue")]
    [InlineData("sbyte", "sbyte.MinValue", "sbyte.MaxValue")]
    [InlineData("short", "short.MinValue", "short.MaxValue")]
    [InlineData("ushort", "ushort.MinValue", "ushort.MaxValue")]
    [InlineData("int", "int.MinValue", "int.MaxValue")]
    [InlineData("uint", "uint.MinValue", "uint.MaxValue")]
    [InlineData("long", "long.MinValue", "long.MaxValue")]
    [InlineData("ulong", "ulong.MinValue", "ulong.MaxValue")]
    public void DictionaryGeneratedEnumKeysKeepUnknownUnderlyingValues(string underlying, string first, string second) {
        RunDictionaryKeyGraph("Mode", $"(Mode)({first})", $"(Mode)({second})",
            $"[DurableType(\"Mode\", 1)] public enum Mode : {underlying} {{ Unused = 1 }}");
    }

    private static void RunDictionaryKeyGraph(string type, string first, string second, string declaration) {
        GeneratorTestRun run = RunGenerator($$"""
            using System;
            using System.Collections.Generic;
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            using Atelia.DurableGraph.Generated;
            using Atelia.DurableGraph.Persistence;
            {{declaration}}
            [DurableType("KeyWorld", 1)] public partial class KeyWorld<TKey> : IDurableObject where TKey : notnull {
                [DurableField(1)] public Dictionary<TKey, string> Values = new();
            }
            public static class Host {
                public static bool Probe(string path) => Run<{{type}}>(path, {{first}}, {{second}});
                private static bool Run<TKey>(string path, TKey first, TKey second) where TKey : notnull {
                    var models = new StateModelRegistry(); DurableDefinitions.Register(models);
                    var world = new KeyWorld<TKey>();
                    world.Values.Add(first, "a"); world.Values.Add(second, "b");
                    var original = world.Values;
                    using (var repo = FixtureGraphRepository.CreateNew(path, models)) {
                        using var session = repo.Create(world);
                        session.Commit(new(1000000, 1));
                        world.Values.Clear();
                        world.Values.Add(second, "b"); world.Values.Add(first, "c");
                        session.Commit(new(1000000, 1));
                        world.Values[second] = "d";
                        session.Commit(new(1000000, 1));
                        if (!ReferenceEquals(original, session.World.Values)) return false;
                    }
                    using (var repo = FixtureGraphRepository.OpenExisting(path, models)) {
                        using var session = repo.Load<KeyWorld<TKey>>();
                        if (session.World.Values.Count != 2 || session.World.Values[first] != "c" || session.World.Values[second] != "d") return false;
                        session.Commit(new(1000000, 1));
                        return true;
                    }
                }
            }
            """);
        AssertSchemaOnlyCompiles(run);
        Assembly assembly = EmitAndLoad(run.OutputCompilation);
        using RawBaseDirectory directory = new();
        Assert.True(assembly.GetType("Host")!.GetMethod("Probe")!.CreateDelegate<Func<string, bool>>()(directory.Path));
    }

    [Fact]
    public void DictionaryGeneratedGraphComposesValuesAndIdentityKeysWithoutCallingDomainEquality() {
        GeneratorTestRun run = RunGenerator("""
            using System;
            using System.Collections.Generic;
            using System.Linq;
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            using Atelia.DurableGraph.Generated;
            using Atelia.DurableGraph.Persistence;
            [DurableType("Point", 1)] public partial struct Point {
                [DurableField(1)] public int X;
                [DurableField(2)] public Node? Link;
            }
            [DurableType("Box", 1)] public partial class Box<T> : IDurableObject {
                [DurableField(1)] public T Value = default!;
            }
            [DurableType("Node", 1)] public partial class Node : IDurableObject {
                [DurableField(1)] public World? Owner;
                public override bool Equals(object? other) => throw new InvalidOperationException("Domain equality must not run");
                public override int GetHashCode() => throw new InvalidOperationException("Domain hash must not run");
            }
            [DurableType("World", 1)] public partial class World : IDurableObject {
                [DurableField(1)] public Dictionary<string, Point?> Points = new(StringComparer.OrdinalIgnoreCase);
                [DurableField(2)] public Dictionary<string, Point?>? Alias;
                [DurableField(3)] public Dictionary<Node, World> Identities = new(ReferenceEqualityComparer.Instance);
                [DurableField(4)] public Dictionary<string, int> StringIdentities = new(ReferenceEqualityComparer.Instance);
                [DurableField(5)] public string Key = "";
                [DurableField(6)] public Node? Node;
                [DurableField(7)] public Dictionary<string, List<Point?[,]>[]> Nested = new();
                [DurableField(8)] public List<Dictionary<string, Point?>>[] Views = [];
                [DurableField(9)] public Box<Dictionary<string, int>> Box = new();
            }
            public static class Host {
                public static bool Probe(string path) {
                    var models = new StateModelRegistry(); DurableDefinitions.Register(models);
                    string key = new string(new[] { 'K', 'e', 'y' });
                    string a = new string(new[] { 'x' }), b = new string(new[] { 'x' });
                    var world = new World { Key = key };
                    var node = new Node { Owner = world }; world.Node = node;
                    world.Points.Add(key, new Point { X = 1, Link = node }); world.Alias = world.Points;
                    world.Identities.Add(node, world);
                    world.StringIdentities.Add(a, 10); world.StringIdentities.Add(b, 20);
                    world.Nested.Add("grid", new[] { new List<Point?[,]> { new Point?[,] { { new Point { X = 7, Link = node }, null } } } });
                    world.Views = new[] { new List<Dictionary<string, Point?>> { world.Points } };
                    world.Box.Value = new() { ["n"] = 9 };
                    using (var repo = FixtureGraphRepository.CreateNew(path, models)) {
                        using var session = repo.Create(world);
                        session.Commit(new(1000000, 1));
                        world.Points[key] = new Point { X = 2, Link = node };
                        world.Points.Add("absent", null);
                        session.Commit(new(1000000, 1));
                        world.Points.Remove("absent"); world.Box.Value["n"] = 12;
                        session.Commit(new(1000000, 1));
                    }
                    using (var repo = FixtureGraphRepository.OpenExisting(path, models)) {
                        using var session = repo.Load<World>(); var w = session.World;
                        if (!ReferenceEquals(w.Points, w.Alias) || !ReferenceEquals(w.Points, w.Views[0][0])) return false;
                        if (!ReferenceEquals(w.Key, w.Points.Keys.Single()) || w.Points["KEY"]!.Value.X != 2) return false;
                        if (!ReferenceEquals(w.Node, w.Points["KEY"]!.Value.Link) || !ReferenceEquals(w, w.Identities[w.Node!])) return false;
                        if (!ReferenceEquals(w.Node!.Owner, w) || w.Box.Value["n"] != 12) return false;
                        if (w.Nested["grid"][0][0][0,0]!.Value.X != 7 || w.Nested["grid"][0][0][0,1].HasValue) return false;
                        var keys = w.StringIdentities.Keys.ToArray();
                        if (keys.Length != 2 || keys[0] != keys[1] || ReferenceEquals(keys[0], keys[1])) return false;
                        if (w.StringIdentities[keys[0]] + w.StringIdentities[keys[1]] != 30) return false;
                        session.Commit(new(1000000, 1));
                        return true;
                    }
                }
            }
            """);
        AssertSchemaOnlyCompiles(run);
        Assembly assembly = EmitAndLoad(run.OutputCompilation);
        using RawBaseDirectory directory = new();
        Assert.True(assembly.GetType("Host")!.GetMethod("Probe")!.CreateDelegate<Func<string, bool>>()(directory.Path));
    }
}
