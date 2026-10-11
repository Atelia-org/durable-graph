using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Reflection;
using Atelia.DurableGraph.Build;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Fact]
    public void StringReferenceValidationExecutesPrivateInheritedSlotsAfterFrozenStateByteRoundTrip() {
        GeneratorTestRun run = RunGenerator(StringReferenceValidationSource);
        AssertSchemaOnlyCompiles(run);
        Assembly assembly = EmitAndLoad(run.OutputCompilation);
        Assert.True(StringValidationDelegate<Func<bool>>(assembly, "RoundTrip")());

        string generated = GeneratedSource(run, "DurableStates.g.cs");
        Assert.Contains("table.ResolveString(state.Segment0Field1);", generated);
        Assert.Contains("table.ResolveString(state.Segment1Field2);", generated);
        Assert.DoesNotContain("table.ResolveString(state.Segment1Field1)", generated);
        AssertGeneratedBodiesRemainStaticallyBound(generated);
        foreach (string forbidden in new[] { "ValueSlotCodec", "PrimitiveSlotCodecs", "System.Reflection", "Dictionary<", "DynamicInvoke" }) {
            Assert.DoesNotContain(forbidden, generated);
        }
    }

    [Fact]
    public void StringReferenceValidationRejectsMissingAndNonStringTargetsButLeavesReadsAndNumericUInt32Independent() {
        GeneratorTestRun run = RunGenerator(StringReferenceValidationSource);
        AssertSchemaOnlyCompiles(run);
        Assembly assembly = EmitAndLoad(run.OutputCompilation);
        var validate = StringValidationDelegate<Func<uint, uint, uint, bool>>(assembly, "Validate");
        Assert.True(validate(0, 0, uint.MaxValue)); // Null references; the numeric slot needs no object.
        Assert.True(validate(3, 3, 99)); // Both declaration segments resolve a single target.
        Assert.False(validate(99, 3, 0)); // Missing inherited reference.
        Assert.False(validate(3, 99, 0)); // Missing leaf reference.
        Assert.False(validate(1, 3, 0)); // Durable root ID is absent from the string table.
        Assert.False(validate(3, 2, 0)); // Another Durable root, in a different declaration segment.
        Assert.True(StringValidationDelegate<Func<bool>>(assembly, "RejectNullTable")());
    }

    [Fact]
    public void StringReferenceValidationExistsAndChecksNullTableForEmptyAndScalarDtos() {
        GeneratorTestRun run = RunGenerator("""
            using System;
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            namespace StringValidation;
            [DurableType("validation.empty", 1)]
            public sealed partial class Empty : IDurableObject { }
            [DurableType("validation.scalar", 1)]
            public sealed partial class Scalar : IDurableObject {
                [DurableField(1)] private uint _number = uint.MaxValue;
            }
            public static class Host {
                public static bool Check() {
                    var table = StringReadTable.Decode(Array.Empty<(ObjectId, ReadOnlyMemory<byte>)>());
                    var empty = Empty.__DurableState.Capture(new Empty());
                    var scalar = Scalar.__DurableState.Capture(new Scalar());
                    Empty.__DurableState.ValidateStringReferences(in empty, table);
                    Scalar.__DurableState.ValidateStringReferences(in scalar, table);
                    int rejected = 0;
                    try { Empty.__DurableState.ValidateStringReferences(in empty, null!); }
                    catch (ArgumentNullException exception) { if (exception.ParamName == "table") rejected++; }
                    try { Scalar.__DurableState.ValidateStringReferences(in scalar, null!); }
                    catch (ArgumentNullException exception) { if (exception.ParamName == "table") rejected++; }
                    return rejected == 2 && scalar.Segment0Field1 == uint.MaxValue;
                }
            }
            """);
        AssertSchemaOnlyCompiles(run);
        Assert.True(StringValidationDelegate<Func<bool>>(EmitAndLoad(run.OutputCompilation), "Check")());
        Assert.DoesNotContain("table.ResolveString", GeneratedSource(run, "DurableStates.g.cs"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StringReferenceValidationUsesExactPublishedHistoryAfterStringBecomesUInt32AndClrAncestorDisappears(bool replaceChain) {
        using AncestryHistoryDirectory files = new();
        SchemaHistoryTool publisher = new();
        GeneratorTestRun initial = RunGenerator(ReferenceCaptureHistoryInitialSource);
        AssertSchemaOnlyCompiles(initial);
        byte[] original = EmitAndLoad(initial.OutputCompilation).GetType("ReferenceHistory.Host")!
            .GetMethod("Capture")!.CreateDelegate<Func<byte[]>>()();
        Assert.Equal<byte>([2, 2, 3], original);
        publisher.Publish(files.WriteManifest(initial), files.History);
        Dictionary<string, string> oldHistory = files.ReadContents();

        string source = ReferenceCaptureHistoryCurrentSource(replaceChain).Replace(
            "[DurableField(5)] private bool _flag;", "[DurableField(2)] private uint _other = uint.MaxValue;") + StringReferenceHistoryValidationHost;
        GeneratorTestRun current = RunGenerator(source, files.ReadAdditionalTexts().Reverse().ToArray());
        AssertSchemaOnlyCompiles(current);
        Assembly assembly = EmitAndLoad(current.OutputCompilation);
        Assert.Null(assembly.GetType("ReferenceHistory.OldBase"));
        var validateOld = assembly.GetType("ReferenceHistory.ValidationHost")!.GetMethod("ValidateOld")!
            .CreateDelegate<Func<byte[], uint, bool>>();
        Assert.True(validateOld(original, 0));
        Assert.False(validateOld(original, 2)); // Exact historical base and alias still carry strings.
        Assert.False(validateOld(original, 3)); // Same FieldId 2 is String only in V1.
        Assert.True(assembly.GetType("ReferenceHistory.ValidationHost")!.GetMethod("ValidateCurrent")!
            .CreateDelegate<Func<bool>>()());
        Type body = assembly.GetType("ReferenceHistory.Leaf")!.GetNestedType("__DurableState", BindingFlags.NonPublic)!;
        Assert.Equal(2, body.GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Count(method => method.Name == "ValidateStringReferences"));
        Assert.Equal(TypeTag.String, ReadSchemaOnly(assembly.GetType("ReferenceHistory.Leaf")!, 1).Fields[1].TypeTag);
        Assert.Equal(TypeTag.UInt32, Assert.Single(ReadSchemaOnly(assembly.GetType("ReferenceHistory.Leaf")!, 2).Fields).TypeTag);

        publisher.Publish(files.WriteManifest(current), files.History);
        publisher.Verify(files.WriteManifest(current), files.History);
        foreach ((string name, string content) in oldHistory) {
            Assert.Equal(content, File.ReadAllText(Path.Combine(files.History, name)));
        }
        GeneratorTestRun reloaded = RunGenerator(source, files.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(reloaded);
        Assert.Equal(GeneratedSource(current, "DurableStates.g.cs"), GeneratedSource(reloaded, "DurableStates.g.cs"));
    }

    private static T StringValidationDelegate<T>(Assembly assembly, string method) where T : Delegate =>
        assembly.GetType("StringValidation.Host")!.GetMethod(method)!.CreateDelegate<T>();

    private const string StringReferenceValidationSource = """
        using System;
        using System.Buffers;
        using System.IO;
        using System.Linq;
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        using Atelia.DurableGraph.Serialization;
        namespace StringValidation;
        [DurableType("validation.base", 1)]
        public abstract partial class Base : IDurableObject {
            [DurableField(1)] private string? _name;
            protected Base(string? name) { _name = name; }
            public void Change(string value) { _name = value; }
        }
        [DurableType("validation.leaf", 1)]
        public sealed partial class Leaf : Base {
            [DurableField(2)] private string? _alias;
            [DurableField(1)] private uint _number = uint.MaxValue;
            public Leaf(string? name, string? alias) : base(name) { _alias = alias; }
            public void Mutate(string value) { Change(value); _alias = value; _number = 7; }
        }
        public static class Host {
            private static byte[] Content(string value) {
                var buffer = new ArrayBufferWriter<byte>();
                var writer = new BareValueWriter(buffer);
                writer.WriteString(value);
                return buffer.WrittenSpan.ToArray();
            }
            private static Leaf.__DurableState.V1 Read(byte[] bytes) {
                var reader = new BareValueReader(bytes);
                var state = Leaf.__DurableState.ReadBaseBodyV1(ref reader);
                reader.EnsureFullyConsumed();
                return state;
            }
            public static bool RoundTrip() {
                string shared = new string(new[] { 'x' });
                string equal = new string(new[] { 'x' });
                var first = new Leaf(shared, shared);
                var second = new Leaf(equal, shared);
                var session = new CaptureSession();
                using var capture = session.BeginCapture();
                Leaf.__DurableState.AddRoot(capture, first);
                Leaf.__DurableState.AddRoot(capture, second);
                var graph = capture.Seal();
                first.Mutate("changed");
                second.Mutate("changed");
                byte[][] owners = graph.Objects.Where(entry => entry.Kind == ObjectStateKind.Durable).Select(entry => {
                    var state = entry.GetState<Leaf.__DurableState.V1>();
                    var buffer = new ArrayBufferWriter<byte>();
                    var writer = new BareValueWriter(buffer);
                    Leaf.__DurableState.WriteBaseBody(ref writer, in state);
                    return buffer.WrittenSpan.ToArray();
                }).ToArray();
                var records = graph.Objects.Where(entry => entry.Kind == ObjectStateKind.String)
                    .Select(entry => (entry.Id, (ReadOnlyMemory<byte>)Content(entry.StringContent))).Reverse().ToArray();
                var table = StringReadTable.Decode(records);
                var a = Read(owners[0]);
                var b = Read(owners[1]);
                Leaf.__DurableState.ValidateStringReferences(in a, table);
                Leaf.__DurableState.ValidateStringReferences(in b, table);
                return a.Segment1Field1 == uint.MaxValue && b.Segment1Field1 == uint.MaxValue &&
                    table.ResolveString(a.Segment0Field1) == "x" && table.ResolveString(b.Segment0Field1) == "x" &&
                    ReferenceEquals(table.ResolveString(a.Segment0Field1), table.ResolveString(a.Segment1Field2)) &&
                    ReferenceEquals(table.ResolveString(a.Segment1Field2), table.ResolveString(b.Segment1Field2)) &&
                    !ReferenceEquals(table.ResolveString(a.Segment0Field1), table.ResolveString(b.Segment0Field1)) &&
                    !ReferenceEquals(shared, table.ResolveString(a.Segment0Field1));
            }
            public static bool Validate(uint inherited, uint own, uint number) {
                var buffer = new ArrayBufferWriter<byte>();
                var writer = new BareValueWriter(buffer);
                writer.WriteVarUInt32(inherited);
                writer.WriteVarUInt32(number);
                writer.WriteVarUInt32(own);
                var state = Read(buffer.WrittenSpan.ToArray()); // Pure Read succeeds before resolution, even for invalid IDs.
                if (state.Segment0Field1.Value != inherited || state.Segment1Field1 != number || state.Segment1Field2.Value != own)
                    throw new Exception("Body read must remain ID-only.");
                var table = StringReadTable.Decode(new[] { (new ObjectId(3), (ReadOnlyMemory<byte>)Content("x")) });
                try { Leaf.__DurableState.ValidateStringReferences(in state, table); return true; }
                catch (InvalidDataException) { return false; }
            }
            public static bool RejectNullTable() {
                var state = Read(new byte[] { 0, 0, 0 });
                try { Leaf.__DurableState.ValidateStringReferences(in state, null!); }
                catch (ArgumentNullException exception) { return exception.ParamName == "table"; }
                return false;
            }
        }
        """;

    private const string StringReferenceHistoryValidationHost = """

        public static class ValidationHost {
            private static StringReadTable Table(uint omit) {
                var buffer = new ArrayBufferWriter<byte>();
                var writer = new BareValueWriter(buffer);
                writer.WriteString("x");
                var records = new System.Collections.Generic.List<(ObjectId, ReadOnlyMemory<byte>)>();
                foreach (uint id in new uint[] { 2, 3 }) {
                    if (id != omit) records.Add((new ObjectId(id), buffer.WrittenMemory));
                }
                return StringReadTable.Decode(records);
            }
            public static bool ValidateOld(byte[] bytes, uint omit) {
                var reader = new BareValueReader(bytes);
                var state = Leaf.__DurableState.ReadBaseBodyV1(ref reader);
                reader.EnsureFullyConsumed();
                try { Leaf.__DurableState.ValidateStringReferences(in state, Table(omit)); return true; }
                catch (System.IO.InvalidDataException) { return false; }
            }
            public static bool ValidateCurrent() {
                var state = Leaf.__DurableState.Capture(new Leaf());
                var buffer = new ArrayBufferWriter<byte>();
                var writer = new BareValueWriter(buffer);
                Leaf.__DurableState.WriteBaseBody(ref writer, in state);
                var reader = new BareValueReader(buffer.WrittenSpan);
                state = Leaf.__DurableState.ReadBaseBodyV2(ref reader);
                reader.EnsureFullyConsumed();
                var table = StringReadTable.Decode(Array.Empty<(ObjectId, ReadOnlyMemory<byte>)>());
                Leaf.__DurableState.ValidateStringReferences(in state, table);
                return state.Segment1Field2 == uint.MaxValue;
            }
        }
        """;
}
