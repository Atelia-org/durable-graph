using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Reflection;
using Atelia.DurableGraph.Build;
using Microsoft.CodeAnalysis;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Fact]
    public void ReferenceCaptureGeneratedRootsShareIdentityAcrossTypesAndPrivateInheritanceSegments() {
        GeneratorTestRun run = RunGenerator(ReferenceCaptureSource);
        AssertSchemaOnlyCompiles(run);
        Assembly assembly = EmitAndLoad(run.OutputCompilation);
        Assert.True(ReferenceCaptureDelegate<Func<bool>>(assembly, "IdentityAndLayout")());

        Type baseBody = assembly.GetType("ReferenceCaptureDomain.Base")!
            .GetNestedType("__DurableState", BindingFlags.NonPublic)!;
        Assert.Null(baseBody.GetMethod("AddRoot", BindingFlags.Static | BindingFlags.NonPublic));
        Type leafBody = assembly.GetType("ReferenceCaptureDomain.Leaf")!
            .GetNestedType("__DurableState", BindingFlags.NonPublic)!;
        Assert.Equal(2, leafBody.GetMethod("Capture", BindingFlags.Static | BindingFlags.NonPublic)!.GetParameters().Length);
        Assert.All(leafBody.GetNestedType("V1", BindingFlags.NonPublic)!
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic), field => Assert.True(field.IsInitOnly));
        string generated = GeneratedSource(run, "DurableStates.g.cs");
        Assert.DoesNotContain("value._cache", generated);
        foreach (string forbidden in new[] { "ValueSlotCodec", "PrimitiveSlotCodecs", "DynamicInvoke", "System.Reflection", "Dictionary<" }) {
            Assert.DoesNotContain(forbidden, generated);
        }
        Assert.Contains("writer.WriteVarUInt32(value.Segment0Field1.Value)", generated);
        Assert.Contains("reader.ReadVarUInt32()", generated);
    }

    [Fact]
    public void ReferenceCaptureGeneratedBodyUsesIdGoldenAndReadsNumbersWithoutResolvingObjects() {
        GeneratorTestRun run = RunGenerator(ReferenceCaptureSource);
        AssertSchemaOnlyCompiles(run);
        Assembly assembly = EmitAndLoad(run.OutputCompilation);
        // Both roots are registered before Seal: root IDs 1,2; then strings 3,4,5,6.
        Assert.Equal<byte>([3, 0, 1, 3, 4, 5, 6, 3],
            ReferenceCaptureDelegate<Func<byte[]>>(assembly, "WriteIds")());
        // These numbers deliberately have no graph/object table: ReadBaseBodyV1 is only a DTO body reader.
        Assert.Equal<byte>([0xFF, 0xFF, 0xFF, 0xFF, 0x0F, 0, 1, 0x80, 1, 1, 0, 0],
            ReferenceCaptureDelegate<Func<byte[]>>(assembly, "ReadAndWriteIds")());
    }

    [Fact]
    public void ReferenceCaptureGeneratedCandidatesFreezeValuesAndAcceptActualStateWithStableLiveIds() {
        GeneratorTestRun run = RunGenerator(ReferenceCaptureSource);
        AssertSchemaOnlyCompiles(run);
        Assert.True(ReferenceCaptureDelegate<Func<bool>>(EmitAndLoad(run.OutputCompilation), "Lifecycle")());
    }

    [Fact]
    public void ReferenceCaptureGeneratedRootRejectsDerivedSlicingWhileBaseSegmentCaptureStillWorks() {
        GeneratorTestRun run = RunGenerator(ReferenceCaptureSource);
        AssertSchemaOnlyCompiles(run);
        Assert.True(ReferenceCaptureDelegate<Func<bool>>(EmitAndLoad(run.OutputCompilation), "ExactRoot")());
    }

    [Fact]
    public void ReferenceCaptureStringOptInKeepsStringSchemaAndGeneratesOnlyIdDtoSlots() {
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("body.string", 1)]
            public sealed partial class Item : IDurableObject {
                [DurableField(1)] private string _text = string.Empty;
            }
            """);
        AssertSchemaOnlyCompiles(run);
        Type item = EmitAndLoad(run.OutputCompilation).GetType("Item")!;
        Assert.Equal(TypeTag.String, Assert.Single(ReadSchemaOnly(item, 1).Fields).TypeTag);
        Type body = item.GetNestedType("__DurableState", BindingFlags.NonPublic)!;
        Assert.Equal(typeof(ObjectId), body.GetNestedType("V1", BindingFlags.NonPublic)!
            .GetField("Segment0Field1", BindingFlags.Instance | BindingFlags.NonPublic)!.FieldType);
        Assert.Equal(2, body.GetMethod("Capture", BindingFlags.Static | BindingFlags.NonPublic)!.GetParameters().Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReferenceCaptureSharesContextOnlyWhereCurrentInheritanceNeedsIt(bool stringInBase) {
        string baseField = stringInBase ? "private string _base = null!;" : "private int _base = -1;";
        string leafField = stringInBase ? "private int _leaf = -1;" : "private string _leaf = null!;";
        GeneratorTestRun run = RunGenerator($$"""
            using System;
            using System.Buffers;
            using System.Linq;
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            using Atelia.DurableGraph.Serialization;
            namespace BinaryBodies;
            [DurableType("reference.mixed-base", 1)]
            public abstract partial class Base : IDurableObject { [DurableField(1)] {{baseField}} }
            [DurableType("reference.mixed-leaf", 1)]
            public sealed partial class Leaf : Base { [DurableField(1)] {{leafField}} }
            public static class Host {
                public static byte[] Capture() {
                    var session = new CaptureSession();
                    using var capture = session.BeginCapture();
                    Leaf.__DurableState.AddRoot(capture, new Leaf());
                    var graph = capture.Seal();
                    if (graph.Objects.Count != 1) throw new Exception("Null string must not create a record.");
                    var state = graph.Objects.Single().GetState<Leaf.__DurableState.V1>();
                    var buffer = new ArrayBufferWriter<byte>();
                    var writer = new BareValueWriter(buffer);
                    Leaf.__DurableState.WriteBaseBody(ref writer, in state);
                    return buffer.WrittenSpan.ToArray();
                }
            }
            """);
        AssertSchemaOnlyCompiles(run);
        Assembly assembly = EmitAndLoad(run.OutputCompilation);
        Assert.Equal(stringInBase ? new byte[] { 0, 1 } : [1, 0], GeneratedStateDelegate<Func<byte[]>>(assembly, "Capture")());
        Type baseBody = assembly.GetType("BinaryBodies.Base")!.GetNestedType("__DurableState", BindingFlags.NonPublic)!;
        Assert.Equal(stringInBase ? 2 : 1,
            baseBody.GetMethod("Capture", BindingFlags.Static | BindingFlags.NonPublic)!.GetParameters().Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReferenceCaptureStringHistorySurvivesPublisherAndDeletedClrAncestor(bool replaceChain) {
        using AncestryHistoryDirectory files = new();
        SchemaHistoryTool publisher = new();
        GeneratorTestRun initial = RunGenerator(ReferenceCaptureHistoryInitialSource);
        AssertSchemaOnlyCompiles(initial);
        byte[] original = EmitAndLoad(initial.OutputCompilation).GetType("ReferenceHistory.Host")!
            .GetMethod("Capture")!.CreateDelegate<Func<byte[]>>()();
        Assert.Equal<byte>([2, 2, 3], original);
        publisher.Publish(files.WriteManifest(initial), files.History);
        Dictionary<string, string> originalHistory = files.ReadContents();

        string source = ReferenceCaptureHistoryCurrentSource(replaceChain);
        AdditionalText[] accepted = files.ReadAdditionalTexts();
        GeneratorTestRun updated = RunGenerator(source, accepted.Reverse().ToArray());
        AssertSchemaOnlyCompiles(updated);
        string generated = GeneratedSource(updated, "DurableStates.g.cs");
        Assert.DoesNotContain("OldBase", generated);
        Assert.Equal(generated, GeneratedSource(RunGenerator(source, accepted), "DurableStates.g.cs"));
        Assembly assembly = EmitAndLoad(updated.OutputCompilation);
        Assert.Null(assembly.GetType("ReferenceHistory.OldBase"));
        Assert.Equal(original, assembly.GetType("ReferenceHistory.Host")!.GetMethod("RoundTripV1")!
            .CreateDelegate<Func<byte[], byte[]>>()(original));

        publisher.Publish(files.WriteManifest(updated), files.History);
        publisher.Verify(files.WriteManifest(updated), files.History);
        foreach ((string name, string content) in originalHistory) {
            Assert.Equal(content, File.ReadAllText(Path.Combine(files.History, name)));
        }
        GeneratorTestRun reloaded = RunGenerator(source, files.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(reloaded);
        Assert.Equal(generated, GeneratedSource(reloaded, "DurableStates.g.cs"));
        Assert.Equal(original, EmitAndLoad(reloaded.OutputCompilation).GetType("ReferenceHistory.Host")!
            .GetMethod("RoundTripV1")!.CreateDelegate<Func<byte[], byte[]>>()(original));
    }

    [Fact]
    public void ReferenceCaptureStringLayoutChangeWithoutVersionBumpIsRejectedAfterPublish() {
        using AncestryHistoryDirectory files = new();
        SchemaHistoryTool publisher = new();
        GeneratorTestRun initial = RunGenerator(ReferenceCaptureHistoryInitialSource);
        AssertSchemaOnlyCompiles(initial);
        publisher.Publish(files.WriteManifest(initial), files.History);
        // The current CLR field changes to UInt32, which happens to have identical wire bytes.
        // The declaration schema must nevertheless change; DTO representation is not schema identity.
        string changed = ReferenceCaptureHistoryInitialSource.Replace(
            "private string _other = new string(new[] { 'x' });", "private uint _other = 3;");
        GeneratorTestRun rejected = RunGenerator(changed, files.ReadAdditionalTexts());
        Assert.Contains(rejected.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0015" &&
            diagnostic.GetMessage().Contains("reference-history.leaf"));
        // The unchanged base remains valid and may still emit its own body. The rejected
        // leaf must not declare a body or fall back to the base layout through a root adapter.
        Assert.Empty(rejected.OutputCompilation.GetTypeByMetadataName("ReferenceHistory.Leaf")!
            .GetTypeMembers("__DurableState"));
        Assert.Single(rejected.OutputCompilation.GetTypeByMetadataName("ReferenceHistory.OldBase")!
            .GetTypeMembers("__DurableState"));
        Assert.Contains(rejected.OutputCompilation.GetDiagnostics(), IsError);

        GeneratorTestRun changedCandidate = RunGenerator(changed);
        AssertSchemaOnlyCompiles(changedCandidate);
        string changedManifest = files.WriteManifest(changedCandidate);
        Assert.Throws<SchemaHistoryException>(() => publisher.Publish(changedManifest, files.History));
        Assert.Throws<SchemaHistoryException>(() => publisher.Verify(changedManifest, files.History));
    }

    private static T ReferenceCaptureDelegate<T>(Assembly assembly, string method) where T : Delegate =>
        assembly.GetType("ReferenceCaptureDomain.Host")!.GetMethod(method)!.CreateDelegate<T>();

    private const string ReferenceCaptureSource = """
        using System;
        using System.Buffers;
        using System.Linq;
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        using Atelia.DurableGraph.Serialization;
        namespace ReferenceCaptureDomain;
        [DurableType("reference.base", 1)]
        public abstract partial class Base : IDurableObject {
            [DurableField(2)] private string? _optional;
            [DurableField(1)] private string _name;
            [Transient] private object _cache = new();
            protected Base(string name) { _name = name; }
            public void ChangeBase(string name) { _name = name; _optional = name; _cache = new(); }
        }
        [DurableType("reference.leaf", 1)]
        public sealed partial class Leaf : Base {
            [DurableField(6)] private string _surrogate = new string(new[] { '\uD800' });
            [DurableField(4)] private string _other;
            [DurableField(1)] private int _number = -1;
            [DurableField(5)] private string _empty = string.Empty;
            [DurableField(3)] private string _alias;
            public Leaf(string shared, string other) : base(shared) { _alias = shared; _other = other; }
            public void Mutate(string replacement) { ChangeBase(replacement); _alias = replacement; _other = replacement; _number = 99; }
        }
        [DurableType("reference.item", 1)]
        public sealed partial class Item : IDurableObject {
            [DurableField(1)] private string _text;
            public Item(string text) { _text = text; }
            public void Change(string text) { _text = text; }
        }
        [DurableType("reference.concrete", 1)]
        public partial class Concrete : IDurableObject { [DurableField(1)] private int _value = 7; }
        [DurableType("reference.derived", 1)]
        public sealed partial class Derived : Concrete { [DurableField(1)] private bool _flag = true; }
        public static class Host {
            private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
            private static ObjectStateRecord Find(CapturedGraph graph, uint id) => graph.Objects.Single(entry => entry.Id.Value == id);
            private static (CaptureSession Session, CapturedGraph Graph, Leaf Leaf, Item Item, string Shared, string Other) Fixture() {
                string shared = new string(new[] { 'x' });
                string other = new string(new[] { 'x' });
                Check(!ReferenceEquals(shared, other), "Fixture requires equal distinct instances.");
                var leaf = new Leaf(shared, other);
                var item = new Item(shared);
                var session = new CaptureSession();
                using var context = session.BeginCapture();
                Check(Leaf.__DurableState.AddRoot(context, leaf).Value == 1, "First root ID.");
                Check(Item.__DurableState.AddRoot(context, item).Value == 2, "Second root ID.");
                Check(Leaf.__DurableState.AddRoot(context, leaf).Value == 1, "Repeated root ID.");
                Check(Leaf.__DurableState.AddRoot(context, null).Value == 0, "Null root ID.");
                var graph = context.Seal();
                session.Accept(graph);
                return (session, graph, leaf, item, shared, other);
            }
            public static bool IdentityAndLayout() {
                var f = Fixture();
                var graph = f.Graph;
                Check(graph.RootIds.Select(id => id.Value).SequenceEqual(new uint[] { 1, 2, 1, 0 }), "Root input order and null/repeats.");
                Check(graph.Objects.Select(entry => entry.Id.Value).SequenceEqual(new uint[] { 1, 2, 3, 4, 5, 6 }), "Sorted object IDs.");
                var leaf = Find(graph, 1).GetState<Leaf.__DurableState.V1>();
                var item = Find(graph, 2).GetState<Item.__DurableState.V1>();
                Check(leaf.Segment0Field1.Value == 3 && leaf.Segment0Field2.Value == 0 && leaf.Segment1Field1 == -1, "Private base fields.");
                Check(leaf.Segment1Field3.Value == 3 && item.Segment0Field1.Value == 3 && leaf.Segment1Field4.Value == 4, "Shared vs equal string IDs.");
                Check(Find(graph, 3).Kind == ObjectStateKind.String && Find(graph, 3).Schema is null, "String record kind.");
                Check(ReferenceEquals(Find(graph, 3).StringContent, f.Shared), "Immutable content identity.");
                Check(ReferenceEquals(Find(graph, 4).StringContent, f.Other), "Distinct equal content retained.");
                Check(Find(graph, 5).StringContent.Length == 0 && leaf.Segment1Field5.Value == 5, "Empty is non-null.");
                Check(Find(graph, 6).StringContent.Length == 1 && Find(graph, 6).StringContent[0] == '\uD800', "Unpaired surrogate preserved.");
                Check(Find(graph, 1).Kind == ObjectStateKind.Durable && ReferenceEquals(Find(graph, 1).Schema, Leaf.Schema), "Exact root schema.");
                Check(Leaf.Schema.BaseSchema!.Fields.All(field => field.TypeTag == TypeTag.String), "Schema still String.");
                return true;
            }
            public static byte[] WriteIds() {
                var f = Fixture();
                var leaf = Find(f.Graph, 1).GetState<Leaf.__DurableState.V1>();
                var item = Find(f.Graph, 2).GetState<Item.__DurableState.V1>();
                var buffer = new ArrayBufferWriter<byte>();
                var writer = new BareValueWriter(buffer);
                Leaf.__DurableState.WriteBaseBody(ref writer, in leaf);
                Item.__DurableState.WriteBaseBody(ref writer, in item);
                return buffer.WrittenSpan.ToArray();
            }
            public static byte[] ReadAndWriteIds() {
                var reader = new BareValueReader(new byte[] { 255, 255, 255, 255, 15, 0, 1, 128, 1, 1, 0, 0 });
                var state = Leaf.__DurableState.ReadBaseBodyV1(ref reader);
                reader.EnsureFullyConsumed();
                Check(state.Segment0Field1.Value == uint.MaxValue && state.Segment0Field2.Value == 0 && state.Segment1Field1 == -1 &&
                    state.Segment1Field3.Value == 128 && state.Segment1Field4.Value == 1, "ID-only read layout.");
                var buffer = new ArrayBufferWriter<byte>();
                var writer = new BareValueWriter(buffer);
                Leaf.__DurableState.WriteBaseBody(ref writer, in state);
                return buffer.WrittenSpan.ToArray();
            }
            public static bool Lifecycle() {
                var f = Fixture();
                using (var capture = f.Session.BeginCapture()) {
                    Leaf.__DurableState.AddRoot(capture, f.Leaf);
                    Item.__DurableState.AddRoot(capture, f.Item);
                    var next = capture.Seal();
                    f.Leaf.Mutate(new string(new[] { 'y' }));
                    f.Item.Change(new string(new[] { 'z' }));
                    Check(Find(next, 1).GetState<Leaf.__DurableState.V1>().Segment1Field1 == -1, "Sealed scalar unchanged.");
                    Check(Find(next, 1).GetState<Leaf.__DurableState.V1>().Segment1Field3.Value == 3, "Sealed reference unchanged.");
                    f.Session.Accept(next);
                    Check(ReferenceEquals(f.Session.Current, next), "Accept installs exact candidate.");
                    Check(Find(f.Session.Current!, 2).GetState<Item.__DurableState.V1>().Segment0Field1.Value == 3, "Accept does not recapture.");
                }
                var parent = f.Session.Current;
                ObjectId burned;
                var fresh = new Item(new string(new[] { 'q' }));
                using (var capture = f.Session.BeginCapture()) {
                    burned = Item.__DurableState.AddRoot(capture, fresh);
                    var discarded = capture.Seal();
                    f.Session.Discard(discarded);
                    Check(ReferenceEquals(f.Session.Current, parent), "Discard leaves parent unchanged.");
                }
                using (var capture = f.Session.BeginCapture()) {
                    ObjectId retried = Item.__DurableState.AddRoot(capture, fresh);
                    Check(retried.Value > burned.Value + 1, "Discard burns both domain and string IDs.");
                    f.Session.Accept(capture.Seal());
                }
                using (var capture = f.Session.BeginCapture()) {
                    ObjectId returned = Item.__DurableState.AddRoot(capture, f.Item);
                    Check(returned.Value > burned.Value, "Retired root cannot recover old ID 2.");
                    f.Session.Accept(capture.Seal());
                }
                Check(Find(f.Graph, 1).GetState<Leaf.__DurableState.V1>().Segment1Field1 == -1, "Retained old graph stays frozen.");
                return true;
            }
            public static bool ExactRoot() {
                var value = new Derived();
                Check(Concrete.__DurableState.Capture(value).Segment0Field1 == 7, "Base segment permits derived value.");
                var session = new CaptureSession();
                bool rejected = false;
                using (var capture = session.BeginCapture()) {
                    try { Concrete.__DurableState.AddRoot(capture, value); }
                    catch (ArgumentException) { rejected = true; }
                }
                Check(rejected && session.Current is null, "Root binding rejects derived slicing.");
                using (var capture = session.BeginCapture()) {
                    ObjectId id = Derived.__DurableState.AddRoot(capture, value);
                    var graph = capture.Seal();
                    Check(Find(graph, id.Value).GetState<Derived.__DurableState.V1>().Segment1Field1, "Exact derived root succeeds.");
                    session.Accept(graph);
                }
                return true;
            }
        }
        """;

    private const string ReferenceCaptureHistoryInitialSource = """
        using System;
        using System.Buffers;
        using System.Linq;
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        using Atelia.DurableGraph.Serialization;
        namespace ReferenceHistory;
        [DurableType("reference-history.base", 1)]
        public abstract partial class OldBase : IDurableObject {
            [DurableField(1)] private string _name;
            protected OldBase(string name) { _name = name; }
        }
        [DurableType("reference-history.leaf", 1)]
        public sealed partial class Leaf : OldBase {
            [DurableField(1)] private string _alias;
            [DurableField(2)] private string _other = new string(new[] { 'x' });
            public Leaf(string shared) : base(shared) { _alias = shared; }
        }
        public static class Host {
            public static byte[] Capture() {
                var session = new CaptureSession();
                using var capture = session.BeginCapture();
                ObjectId id = Leaf.__DurableState.AddRoot(capture, new Leaf(new string(new[] { 'x' })));
                var graph = capture.Seal();
                var state = graph.Objects.Single(entry => entry.Id == id).GetState<Leaf.__DurableState.V1>();
                var buffer = new ArrayBufferWriter<byte>();
                var writer = new BareValueWriter(buffer);
                Leaf.__DurableState.WriteBaseBody(ref writer, in state);
                return buffer.WrittenSpan.ToArray();
            }
        }
        """;

    private static string ReferenceCaptureHistoryCurrentSource(bool replaceChain) => $$"""
        using System;
        using System.Buffers;
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        using Atelia.DurableGraph.Serialization;
        namespace ReferenceHistory;
        [DurableType("{{(replaceChain ? "reference-history.replacement" : "reference-history.base")}}", {{(replaceChain ? 1 : 2)}})]
        public abstract partial class CurrentBase : IDurableObject {
            [DurableField(1)] private int _number;
        }
        [DurableType("reference-history.leaf", 2)]
        public sealed partial class Leaf : CurrentBase {
            [DurableField(5)] private bool _flag;
        }
        public static class Host {
            public static byte[] RoundTripV1(byte[] bytes) {
                var reader = new BareValueReader(bytes);
                var state = Leaf.__DurableState.ReadBaseBodyV1(ref reader);
                reader.EnsureFullyConsumed();
                if (state.Segment0Field1.Value != 2u || state.Segment1Field1.Value != 2u || state.Segment1Field2.Value != 3u ||
                    !ReferenceEquals(Leaf.__DurableState.V1.Schema, Leaf.GetSchema(1)) ||
                    Leaf.__DurableState.V1.Schema.BaseSchema!.SchemaId != "reference-history.base" ||
                    Leaf.__DurableState.V1.Schema.BaseSchema!.Fields[0].TypeTag != TypeTag.String) {
                    throw new Exception("Historical IDs and exact removed ancestor schema must survive.");
                }
                // Only current schema closure controls Capture's context requirement.
                var current = Leaf.__DurableState.Capture(new Leaf());
                var buffer = new ArrayBufferWriter<byte>();
                var writer = new BareValueWriter(buffer);
                Leaf.__DurableState.WriteBaseBody(ref writer, in state);
                return buffer.WrittenSpan.ToArray();
            }
        }
        """;
}
