using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Reflection;
using Atelia.DurableGraph.Build;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Fact]
    public void GeneratedCapturePreparationBindsHeterogeneousFrozenObjectsAndStringChanges() {
        GeneratorTestRun run = RunGenerator(FusedDeltaPreamble + """
            using System.Linq;
            [DurableType("prepare.base", 1)]
            public abstract partial class Base : IDurableObject {
                [DurableField(7)] private int _count = -17;
            }
            [DurableType("prepare.leaf", 1)]
            public sealed partial class Leaf : Base {
                [DurableField(1)] private uint _score = 42;
                [DurableField(2)] private string _name;
                public Leaf(string name) { _name = name; }
                public void Change(uint score, string name) { _score = score; _name = name; }
            }
            [DurableType("prepare.label", 1)]
            public sealed partial class Label : IDurableObject {
                [DurableField(1)] private string _name;
                [DurableField(2)] private string _empty = string.Empty;
                public Label(string name) { _name = name; }
            }
            public static class Host {
                public static PreparedCapturedGraph[] Run() {
                    var session = new CaptureSession();
                    string shared = new string(new[] { 'A' });
                    var leaf = new Leaf(shared);
                    var label = new Label(shared);
                    using var first = session.BeginCapture();
                    Leaf.__DurableState.AddRoot(first, leaf);
                    Label.__DurableState.AddRoot(first, label);
                    Leaf.__DurableState.AddRoot(first, leaf);
                    Label.__DurableState.AddRoot(first, null);
                    var graph = first.Seal();
                    leaf.Change(43, new string(new[] { 'A' }));
                    var initial = session.Prepare(graph);
                    var repeated = session.Prepare(graph);
                    session.Accept(graph);
                    using var second = session.BeginCapture();
                    Leaf.__DurableState.AddRoot(second, leaf);
                    Label.__DurableState.AddRoot(second, label);
                    var changedGraph = second.Seal();
                    var changed = session.Prepare(changedGraph);
                    session.Accept(changedGraph);
                    using var third = session.BeginCapture();
                    Leaf.__DurableState.AddRoot(third, leaf);
                    Label.__DurableState.AddRoot(third, label);
                    var unchanged = session.Prepare(third.Seal());
                    return new[] { initial, repeated, changed, unchanged };
                }
            }
            """);
        AssertSchemaOnlyCompiles(run);
        Assembly assembly = EmitAndLoad(run.OutputCompilation);
        PreparedCapturedGraph[] results = assembly.GetType("FusedDelta.Host")!.GetMethod("Run")!
            .CreateDelegate<Func<PreparedCapturedGraph[]>>()();
        Assert.Equal(4, results.Length);
        var initial = results[0];
        Assert.Null(initial.Previous);
        Assert.Equal<uint>([1, 2, 1, 0], initial.Candidate.RootIds.Select(id => id.Value));
        Assert.Equal<uint>([1, 2, 3, 4], initial.Objects.Select(row => row.Current.Id).Select(id => id.Value));
        Assert.Equal(new[] { "212A03", "0304", "0341", "00" },
            initial.Objects.Select(row => Convert.ToHexString(row.BaseBody.Body)));
        Assert.All(initial.Objects, row => { Assert.Null(row.Previous); Assert.Null(row.DeltaBody); });
        Assert.Same(initial.Candidate, results[1].Candidate);
        Assert.Equal(initial.Objects.Select(row => row.BaseBody.Body.ToArray()),
            results[1].Objects.Select(row => row.BaseBody.Body.ToArray()));

        var changed = results[2];
        Assert.Same(initial.Candidate, changed.Previous);
        Assert.Equal<uint>([1, 2, 3, 4, 5], changed.Objects.Select(row => row.Current.Id).Select(id => id.Value));
        Assert.Equal(new[] { "212B05", "0304", "0341", "00", "0341" },
            changed.Objects.Select(row => Convert.ToHexString(row.BaseBody.Body)));
        Assert.Equal<byte>([0x06, 0x2B, 0x05], changed.Objects[0].DeltaBody!.Body.ToArray());
        Assert.True(changed.Objects[0].DeltaBody!.HasChanges);
        Assert.False(changed.Objects[1].DeltaBody!.HasChanges);
        Assert.Equal<byte>([0], changed.Objects[1].DeltaBody!.Body.ToArray());
        Assert.Same(initial.Candidate.Objects[0], changed.Objects[0].Previous);
        Assert.Null(changed.Objects[4].Previous);
        Assert.Null(changed.Objects[4].DeltaBody);
        Assert.NotSame(changed.Objects[2].Current.StringContent, changed.Objects[4].Current.StringContent);
        Assert.Same(string.Empty, changed.Objects[3].Current.StringContent);
        Assert.All(results[3].Objects.Where(row => row.Current.Kind == ObjectStateKind.Durable), row => {
            Assert.False(row.DeltaBody!.HasChanges);
            Assert.Equal<byte>([0], row.DeltaBody.Body.ToArray());
        });
        Assert.All(results[3].Objects.Where(row => row.Current.Kind == ObjectStateKind.String), row => {
            Assert.NotNull(row.Previous);
            Assert.Null(row.DeltaBody);
        });

        string generated = GeneratedSource(run, "DurableStates.g.cs");
        Assert.Contains("CapturedStatePreparation<V1> Preparation = new(V1.Schema, PrepareBaseBody, PrepareDeltaBody, StateEquals);", generated);
        Assert.Contains(".Schema, CaptureDelegate, Preparation);", generated);
        foreach (string forbidden in new[] { "ValueSlotCodec", "PrimitiveSlotCodecs", "DynamicInvoke", "System.Reflection", "Dictionary<" }) {
            Assert.DoesNotContain(forbidden, generated);
        }
        Type baseBody = assembly.GetType("FusedDelta.Base")!.GetNestedType("__DurableState", BindingFlags.NonPublic)!;
        Assert.True(baseBody.GetField("Preparation", BindingFlags.NonPublic | BindingFlags.Static)!.IsInitOnly);
        Type leafBody = assembly.GetType("FusedDelta.Leaf")!.GetNestedType("__DurableState", BindingFlags.NonPublic)!;
        FieldInfo binding = leafBody.GetField("Preparation", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.True(binding.IsPrivate && binding.IsInitOnly);
        Assert.Same(binding.GetValue(null), binding.GetValue(null));
    }

    [Fact]
    public void GeneratedCapturePreparationBindsCurrentVersionWithoutChangingHistoricalBodyLayout() {
        using AncestryHistoryDirectory files = new();
        SchemaHistoryTool publisher = new();
        const string initialSource = """
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            namespace PreparationHistory;
            [DurableType("prepare.history", 1)]
            public sealed partial class Item : IDurableObject {
                [DurableField(1)] private int _value = -17;
            }
            """;
        GeneratorTestRun initial = RunGenerator(initialSource);
        AssertSchemaOnlyCompiles(initial);
        publisher.Publish(files.WriteManifest(initial), files.History);
        GeneratorTestRun current = RunGenerator("""
            using System;
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            using Atelia.DurableGraph.Serialization;
            namespace PreparationHistory;
            [DurableType("prepare.history", 2)]
            public sealed partial class Item : IDurableObject {
                [DurableField(1)] private int _value = -17;
                [DurableField(2)] private byte _added = 99;
                public void Change() { _added = 100; }
            }
            public static class Host {
                public static PreparedCapturedGraph[] Run() {
                    var session = new CaptureSession();
                    var item = new Item();
                    using var first = session.BeginCapture();
                    Item.__DurableState.AddRoot(first, item);
                    var graph = first.Seal();
                    var initial = session.Prepare(graph);
                    session.Accept(graph);
                    item.Change();
                    using var second = session.BeginCapture();
                    Item.__DurableState.AddRoot(second, item);
                    return new[] { initial, session.Prepare(second.Seal()) };
                }
                public static byte[] Historical() {
                    var reader = new BareValueReader(new byte[] { 0x21 });
                    var state = Item.__DurableState.ReadBaseBodyV1(ref reader);
                    reader.EnsureFullyConsumed();
                    return Item.__DurableState.PrepareBaseBody(in state).Body.ToArray();
                }
            }
            """, files.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(current);
        Assembly assembly = EmitAndLoad(current.OutputCompilation);
        Type host = assembly.GetType("PreparationHistory.Host")!;
        var results = host.GetMethod("Run")!.CreateDelegate<Func<PreparedCapturedGraph[]>>()();
        Assert.Equal<byte>([0x21, 0x63], Assert.Single(results[0].Objects).BaseBody.Body.ToArray());
        var updated = Assert.Single(results[1].Objects);
        Assert.Equal<byte>([0x21, 0x64], updated.BaseBody.Body.ToArray());
        Assert.Equal<byte>([0x02, 0x64], updated.DeltaBody!.Body.ToArray());
        Assert.Equal<byte>([0x21], host.GetMethod("Historical")!.CreateDelegate<Func<byte[]>>()());
        Type body = assembly.GetType("PreparationHistory.Item")!.GetNestedType("__DurableState", BindingFlags.NonPublic)!;
        FieldInfo binding = Assert.Single(body.GetFields(BindingFlags.Static | BindingFlags.NonPublic),
            field => field.Name == "Preparation");
        Assert.Equal(body.GetNestedType("V2", BindingFlags.NonPublic), Assert.Single(binding.FieldType.GenericTypeArguments));
        Assert.Single(body.GetNestedType("V1", BindingFlags.NonPublic)!.GetFields(BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.Equal(2, body.GetNestedType("V2", BindingFlags.NonPublic)!.GetFields(BindingFlags.Instance | BindingFlags.NonPublic).Length);
        Assert.Equal(2, body.GetMethods(BindingFlags.Static | BindingFlags.NonPublic).Count(method => method.Name == "PrepareBaseBody"));
        Assert.Equal(2, body.GetMethods(BindingFlags.Static | BindingFlags.NonPublic).Count(method => method.Name == "PrepareDeltaBody"));
    }
}
