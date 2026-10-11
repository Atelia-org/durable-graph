using Atelia.Binary;
using Atelia.DurableGraph.Build;
using Microsoft.CodeAnalysis;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StateDtoHistorySurvivesPublishRecompileAndRemovalOfOldClrAncestors(bool replaceChain) {
        using AncestryHistoryDirectory files = new();
        SchemaHistoryTool publisher = new();
        GeneratorTestRun initial = RunGenerator(InitialStateDtoHistorySource);
        AssertSchemaOnlyCompiles(initial);
        byte[] originalBytes = EmitAndLoad(initial.OutputCompilation).GetType("DtoHistory.Host")!
            .GetMethod("Capture")!.CreateDelegate<Func<byte[]>>()();
        Assert.Equal<byte>([0x21, 1, 0x54], originalBytes);
        publisher.Publish(files.WriteManifest(initial), files.History);
        Dictionary<string, string> originalHistory = files.ReadContents();
        Assert.Equal(3, originalHistory.Count);

        string source = CurrentStateDtoHistorySource(replaceChain);
        AdditionalText[] accepted = files.ReadAdditionalTexts();
        GeneratorTestRun updated = RunGenerator(source, accepted.Reverse().ToArray());
        AssertSchemaOnlyCompiles(updated);
        string generated = GeneratedSource(updated, "DurableStates.g.cs");
        Assert.DoesNotContain("OldBase", generated);
        Assert.DoesNotContain("OldMiddle", generated);
        Assert.Equal(generated, GeneratedSource(RunGenerator(source, accepted), "DurableStates.g.cs"));

        var assembly = EmitAndLoad(updated.OutputCompilation);
        Assert.Null(assembly.GetType("DtoHistory.OldBase"));
        Assert.Null(assembly.GetType("DtoHistory.OldMiddle"));
        Type host = assembly.GetType("DtoHistory.Host")!;
        byte[] historicalBytes = host.GetMethod("RoundTripV1")!.CreateDelegate<Func<byte[], byte[]>>()(originalBytes);
        Assert.Equal(originalBytes, historicalBytes);
        byte[] currentBytes = host.GetMethod("Capture")!.CreateDelegate<Func<byte[]>>()();
        Assert.Equal(replaceChain ? new byte[] { 0x80, 1, 0x0D } : [0x80, 1, 6, 0x0D], currentBytes);

        publisher.Publish(files.WriteManifest(updated), files.History);
        publisher.Verify(files.WriteManifest(updated), files.History);
        Assert.Equal(replaceChain ? 5 : 6, files.ReadContents().Count);
        foreach ((string name, string content) in originalHistory) {
            Assert.Equal(content, File.ReadAllText(Path.Combine(files.History, name)));
        }

        GeneratorTestRun reloaded = RunGenerator(source, files.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(reloaded);
        Assert.Equal(generated, GeneratedSource(reloaded, "DurableStates.g.cs"));
        Type reloadedHost = EmitAndLoad(reloaded.OutputCompilation).GetType("DtoHistory.Host")!;
        Assert.Equal(originalBytes, reloadedHost.GetMethod("RoundTripV1")!
            .CreateDelegate<Func<byte[], byte[]>>()(originalBytes));
    }

    private const string InitialStateDtoHistorySource = """
        using System.Buffers;
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        using Atelia.DurableGraph.Serialization;
        namespace DtoHistory;
        [DurableType("dto-history.base", 1)]
        public abstract partial class OldBase : IDurableObject {
            [DurableField(1)] private int _removedNumber = -17;
        }
        [DurableType("dto-history.middle", 1)]
        public abstract partial class OldMiddle : OldBase {
            [DurableField(1)] private bool _removedFlag = true;
        }
        [DurableType("dto-history.leaf", 1)]
        public sealed partial class Leaf : OldMiddle {
            [DurableField(1)] private long _removedAmount = 42;
        }
        public static class Host {
            public static byte[] Capture() {
                var state = Leaf.__DurableState.Capture(new Leaf());
                var buffer = new ArrayBufferWriter<byte>();
                var writer = new BareValueWriter(buffer);
                Leaf.__DurableState.WriteBaseBody(ref writer, in state);
                return buffer.WrittenSpan.ToArray();
            }
        }
        """;

    private static string CurrentStateDtoHistorySource(bool replaceChain) {
        string ancestors = replaceChain ? """
            [DurableType("dto-history.replacement", 1)]
            public abstract partial class Replacement : IDurableObject {
                [DurableField(1)] private long _newNumber = 64;
            }
            """ : """
            [DurableType("dto-history.base", 2)]
            public abstract partial class CurrentBase : IDurableObject {
                [DurableField(1)] private long _newNumber = 64;
            }
            [DurableType("dto-history.middle", 2)]
            public abstract partial class CurrentMiddle : CurrentBase {
                [DurableField(3)] private int _newCount = 3;
            }
            """;
        return $$"""
            using System;
            using System.Buffers;
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            using Atelia.DurableGraph.Serialization;
            namespace DtoHistory;
            {{ancestors}}
            [DurableType("dto-history.leaf", 2)]
            public sealed partial class Leaf : {{(replaceChain ? "Replacement" : "CurrentMiddle")}} {
                [DurableField(5)] private int _newLeaf = -7;
                public void Mutate() { _newLeaf = 999; }
            }
            public static class Host {
                public static byte[] Capture() {
                    var value = new Leaf();
                    var state = Leaf.__DurableState.Capture(value);
                    value.Mutate();
                    if (!ReferenceEquals(Leaf.__DurableState.V2.Schema, Leaf.Schema)) {
                        throw new Exception("Current DTO schema mismatch.");
                    }
                    var buffer = new ArrayBufferWriter<byte>();
                    var writer = new BareValueWriter(buffer);
                    Leaf.__DurableState.WriteBaseBody(ref writer, in state);
                    return buffer.WrittenSpan.ToArray();
                }
                public static byte[] RoundTripV1(byte[] bytes) {
                    var reader = new BareValueReader(bytes);
                    var state = Leaf.__DurableState.ReadBaseBodyV1(ref reader);
                    reader.EnsureFullyConsumed();
                    if (state.Segment0Field1 != -17 || !state.Segment1Field1 || state.Segment2Field1 != 42 ||
                        !ReferenceEquals(Leaf.__DurableState.V1.Schema, Leaf.GetSchema(1)) ||
                        Leaf.__DurableState.V1.Schema.BaseSchema!.BaseSchema!.SchemaId != "dto-history.base") {
                        throw new Exception("Historical DTO or exact ancestry mismatch.");
                    }
                    var buffer = new ArrayBufferWriter<byte>();
                    var writer = new BareValueWriter(buffer);
                    Leaf.__DurableState.WriteBaseBody(ref writer, in state);
                    return buffer.WrittenSpan.ToArray();
                }
            }
            """;
    }
}
