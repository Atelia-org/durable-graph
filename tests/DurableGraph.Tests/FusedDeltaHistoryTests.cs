using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Reflection;
using Atelia.DurableGraph.Build;
using Atelia.DurableGraph.Serialization;
using Microsoft.CodeAnalysis;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Fact]
    public void FusedDeltaHistoricalLayoutsKeepExactAncestorAndDoNotMixVersions() {
        using AncestryHistoryDirectory files = new();
        SchemaHistoryTool publisher = new();
        string initialSource = FusedDeltaPreamble + """
            [DurableType("fused.base", 1)]
            public abstract partial class OldBase : IDurableObject {
                [DurableField(99)] private int _number;
            }
            [DurableType("fused.leaf", 1)]
            public sealed partial class Leaf : OldBase {
                [DurableField(205)] private string? _name;
                [DurableField(99)] private bool _flag;
            }
            """;
        GeneratorTestRun initial = RunGenerator(initialSource);
        AssertSchemaOnlyCompiles(initial);
        publisher.Publish(files.WriteManifest(initial), files.History);
        Dictionary<string, string> frozenHistory = files.ReadContents();

        string currentSource = FusedDeltaPreamble + """
            [DurableType("fused.base", 2)]
            public abstract partial class NewBase : IDurableObject {
                [DurableField(2)] private byte _small;
            }
            [DurableType("fused.leaf", 2)]
            public sealed partial class Leaf : NewBase {
                [DurableField(1)] private uint _number;
            }
            public static class Host {
            """ + FusedDeltaHostMethods("Leaf", 1) + FusedDeltaHostMethods("Leaf", 2) + "\n}";
        GeneratorTestRun current = RunGenerator(currentSource, files.ReadAdditionalTexts().Reverse().ToArray());
        AssertSchemaOnlyCompiles(current);
        Assembly assembly = EmitAndLoad(current.OutputCompilation);
        Assert.Null(assembly.GetType("FusedDelta.OldBase"));
        Type leaf = assembly.GetType("FusedDelta.Leaf")!;
        Assert.Equal(1, ReadSchemaOnly(leaf, 1).BaseSchema!.Version);
        Assert.Equal(TypeTag.Int32, Assert.Single(ReadSchemaOnly(leaf, 1).BaseSchema!.Fields).TypeTag);
        Assert.Equal(2, ReadSchemaOnly(leaf, 2).BaseSchema!.Version);
        Assert.Equal(TypeTag.Byte, Assert.Single(ReadSchemaOnly(leaf, 2).BaseSchema!.Fields).TypeTag);

        Type host = assembly.GetType("FusedDelta.Host")!;
        foreach (var sample in new[] {
            (Version: 1, Prior: "020003", Current: "020104", Delta: "060104"),
            (Version: 1, Prior: "020003", Current: "040003", Delta: "0104"),
            (Version: 2, Prior: "0100", Current: "028001", Delta: "03028001"),
        }) {
            var prepare = host.GetMethod("Prepare" + sample.Version)!.CreateDelegate<Func<byte[], byte[], PreparedDeltaBody>>();
            var apply = host.GetMethod("Apply" + sample.Version)!.CreateDelegate<Func<byte[], byte[], byte[]>>();
            byte[] prior = Convert.FromHexString(sample.Prior);
            byte[] target = Convert.FromHexString(sample.Current);
            PreparedDeltaBody delta = prepare(prior, target);
            Assert.True(delta.HasChanges);
            Assert.Equal(Convert.FromHexString(sample.Delta), delta.Body.ToArray());
            Assert.Equal(target, apply(prior, delta.Body.ToArray()));
            Assert.False(prepare(target, target).HasChanges);
        }
        Type body = leaf.GetNestedType("__DurableState", BindingFlags.NonPublic)!;
        MethodInfo[] prepares = body.GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Where(method => method.Name == "PrepareDeltaBody").ToArray();
        Assert.Equal(2, prepares.Length);
        Assert.All(prepares, method => Assert.Equal(method.GetParameters()[0].ParameterType, method.GetParameters()[1].ParameterType));

        string mixed = currentSource + """

            public static class InvalidMixedVersion {
                public static void Try() {
                    var old = default(Leaf.__DurableState.V1);
                    var current = default(Leaf.__DurableState.V2);
                    Leaf.__DurableState.PrepareDeltaBody(in old, in current);
                }
            }
            """;
        GeneratorTestRun rejected = RunGenerator(mixed, files.ReadAdditionalTexts());
        Assert.Contains(rejected.OutputCompilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error && diagnostic.Id == "CS1503");
        publisher.Publish(files.WriteManifest(current), files.History);
        publisher.Verify(files.WriteManifest(current), files.History);
        foreach ((string name, string contents) in frozenHistory) {
            Assert.Equal(contents, File.ReadAllText(Path.Combine(files.History, name)));
        }
        GeneratorTestRun reloaded = RunGenerator(currentSource, files.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(reloaded);
        Assert.Equal(GeneratedSource(current, "DurableStates.g.cs"), GeneratedSource(reloaded, "DurableStates.g.cs"));
    }

    [Fact]
    public void FusedDeltaFrozenCaptureComparesReferenceIdsAndValidatesInheritedUnchangedReferences() {
        GeneratorTestRun run = RunGenerator(FusedDeltaCaptureSource);
        AssertSchemaOnlyCompiles(run);
        Assert.True(EmitAndLoad(run.OutputCompilation).GetType("FusedCapture.Host")!
            .GetMethod("Run")!.CreateDelegate<Func<bool>>()());
    }

    private const string FusedDeltaCaptureSource = """
        using System;
        using System.Buffers;
        using System.IO;
        using System.Linq;
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        using Atelia.DurableGraph.Serialization;
        namespace FusedCapture;
        [DurableType("fused.capture.base", 1)]
        public abstract partial class Base : IDurableObject {
            [DurableField(1)] private string? _name;
            protected Base(string name) { _name = name; }
            public void Rename(string name) { _name = name; }
        }
        [DurableType("fused.capture.leaf", 1)]
        public sealed partial class Leaf : Base {
            [DurableField(1)] private string? _alias;
            [DurableField(9)] private string? _optional;
            [DurableField(10)] private string _empty = string.Empty;
            [DurableField(20)] private int _score = 1;
            [Transient] public int TransientCounter;
            public Leaf(string shared) : base(shared) { _alias = shared; }
            public void Change(string? alias, string? optional, int score) {
                _alias = alias; _optional = optional; _score = score;
            }
        }
        public static class Host {
            private static byte[] Write(in Leaf.__DurableState.V1 value) {
                var buffer = new ArrayBufferWriter<byte>();
                var writer = new BareValueWriter(buffer);
                Leaf.__DurableState.WriteBaseBody(ref writer, in value);
                return buffer.WrittenSpan.ToArray();
            }
            private static byte[] Content(string text) {
                var buffer = new ArrayBufferWriter<byte>();
                var writer = new BareValueWriter(buffer);
                writer.WriteString(text);
                return buffer.WrittenSpan.ToArray();
            }
            private static Leaf.__DurableState.V1 Apply(in Leaf.__DurableState.V1 prior, PreparedDeltaBody delta) {
                var reader = new BareValueReader(delta.Body);
                var result = Leaf.__DurableState.ApplyDeltaBodyV1(ref reader, in prior);
                reader.EnsureFullyConsumed();
                return result;
            }
            private static bool Rejects(in Leaf.__DurableState.V1 state, StringReadTable table) {
                try { Leaf.__DurableState.ValidateStringReferences(in state, table); return false; }
                catch (InvalidDataException) { return true; }
            }
            public static bool Run() {
                string shared = new string(new[] { 'x' });
                string equal = new string(new[] { 'x' });
                if (ReferenceEquals(shared, equal)) throw new Exception("Distinct string fixture.");
                var owner = new Leaf(shared);
                var session = new CaptureSession();
                CapturedGraph Capture() {
                    var context = session.BeginCapture();
                    Leaf.__DurableState.AddRoot(context, owner);
                    return context.Seal();
                }
                var before = Capture();
                owner.Rename("mutation after first Seal");
                owner.Change("mutation", "mutation", 90);
                var prior = before.Objects.Single(entry => entry.Kind == ObjectStateKind.Durable)
                    .GetState<Leaf.__DurableState.V1>();
                session.Accept(before); // In-memory candidate baseline only, not durable commit.
                owner.Rename(shared);
                owner.Change(equal, string.Empty, 2);
                owner.TransientCounter = 42;
                var after = Capture();
                owner.Rename("mutation after second Seal");
                owner.Change("mutation", null, 91);
                var current = after.Objects.Single(entry => entry.Kind == ObjectStateKind.Durable)
                    .GetState<Leaf.__DurableState.V1>();
                var delta = Leaf.__DurableState.PrepareDeltaBody(in prior, in current);
                var same = Leaf.__DurableState.PrepareDeltaBody(in prior, in prior);
                if (same.HasChanges || same.Body[0] != 0 || !delta.HasChanges || delta.Body[0] != 0x16)
                    throw new Exception("Only alias, optional and score changed.");
                if (prior.Segment0Field1 != prior.Segment1Field1 || prior.Segment0Field1 != current.Segment0Field1 ||
                    current.Segment0Field1 == current.Segment1Field1 || !prior.Segment1Field9.IsNull ||
                    current.Segment1Field9.IsNull || current.Segment1Field9 != current.Segment1Field10 ||
                    prior.Segment1Field10 != current.Segment1Field10 || prior.Segment1Field20 != 1 || current.Segment1Field20 != 2)
                    throw new Exception("Frozen candidate identity or values.");
                var restored = Apply(in prior, delta);
                if (!Write(in current).AsSpan().SequenceEqual(Write(in restored))) throw new Exception("Reconstruction.");
                var records = after.Objects.Where(entry => entry.Kind == ObjectStateKind.String)
                    .Select(entry => (entry.Id, (ReadOnlyMemory<byte>)Content(entry.StringContent))).ToArray();
                var table = StringReadTable.Decode(records);
                Leaf.__DurableState.ValidateStringReferences(in restored, table);
                if (table.ResolveString(restored.Segment0Field1) != table.ResolveString(restored.Segment1Field1) ||
                    ReferenceEquals(table.ResolveString(restored.Segment0Field1), table.ResolveString(restored.Segment1Field1)) ||
                    !ReferenceEquals(string.Empty, table.ResolveString(restored.Segment1Field9)))
                    throw new Exception("Reference identity after decoding.");

                // The inherited base slot did not change, but must still resolve in the target table.
                ObjectId inheritedId = restored.Segment0Field1;
                var missing = StringReadTable.Decode(records.Where(record => record.Id != inheritedId));
                if (!Rejects(in restored, missing)) throw new Exception("Missing inherited target accepted.");

                // A durable owner's ID in that same unchanged slot is not a string target.
                ObjectId ownerId = after.RootIds[0];
                var wrongPrior = new Leaf.__DurableState.V1(ownerId, prior.Segment1Field1,
                    prior.Segment1Field9, prior.Segment1Field10, prior.Segment1Field20);
                var wrongCurrent = new Leaf.__DurableState.V1(ownerId, current.Segment1Field1,
                    current.Segment1Field9, current.Segment1Field10, current.Segment1Field20);
                var wrongDelta = Leaf.__DurableState.PrepareDeltaBody(in wrongPrior, in wrongCurrent);
                if (wrongDelta.Body[0] != 0x16) throw new Exception("Wrong-kind slot should remain unchanged in Delta.");
                var wrongRestored = Apply(in wrongPrior, wrongDelta);
                if (!Rejects(in wrongRestored, table)) throw new Exception("Non-string inherited target accepted.");
                session.Discard(after);
                var repeated = Apply(in prior, delta);
                Leaf.__DurableState.ValidateStringReferences(in repeated, table);
                return Write(in current).AsSpan().SequenceEqual(Write(in repeated));
            }
        }
        """;
}
