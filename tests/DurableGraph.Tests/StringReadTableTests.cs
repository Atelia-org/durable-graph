using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Buffers;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Tests;

public sealed class StringReadTableTests {
    public static TheoryData<string, string> GoldenBodies => new() {
        { "Ada", "07416461" },
        { "é", "02E900" }, // UTF-8 and UTF-16 tie: UTF-16 wins.
        { "中", "022D4E" },
        { string.Empty, "00" },
    };

    [Theory]
    [MemberData(nameof(GoldenBodies))]
    public void StringReadTableUsesCanonicalIndependentStringBodies(string value, string hex) {
        AssertCanonicalIndependentBody(value, hex);
    }

    [Fact]
    public void StringReadTablePreservesUnpairedSurrogateCodeUnitsInCanonicalIndependentBody() {
        // Construct inside execution: discovery transports string theory arguments through text encodings.
        string value = new(['\uD800']);
        AssertCanonicalIndependentBody(value, "0200D8");
        StringReadTable table = StringReadTable.Decode([(new ObjectId(1u), new byte[] { 0x02, 0x00, 0xD8 })]);
        string decoded = Assert.IsType<string>(table.ResolveString(new ObjectId(1)));
        Assert.Equal(1, decoded.Length);
        Assert.Equal('\uD800', decoded[0]);
    }

    private static void AssertCanonicalIndependentBody(string value, string hex) {
        ArrayBufferWriter<byte> bytes = new();
        BareValueWriter writer = new(bytes);
        writer.WriteString(value);
        Assert.Equal(hex, Convert.ToHexString(bytes.WrittenSpan));

        // Decoding receives literal bytes, not the encoder's output or a source instance.
        StringReadTable table = StringReadTable.Decode([(new ObjectId(uint.MaxValue), Convert.FromHexString(hex))]);
        Assert.Equal(value, table.ResolveString(new ObjectId(uint.MaxValue)));
        Assert.Same(table.ResolveString(new ObjectId(uint.MaxValue)), table.ResolveString(new ObjectId(uint.MaxValue)));
        if (value.Length == 0) {
            Assert.Same(string.Empty, table.ResolveString(new ObjectId(uint.MaxValue)));
        }
        Assert.Null(table.ResolveString(new ObjectId(0)));
    }

    [Theory]
    [InlineData("07416461")]
    [InlineData("02E900")]
    [InlineData("0200D8")]
    public void StringReadTablePreservesDifferentIdsForEqualNonemptyContents(string hex) {
        byte[] body = Convert.FromHexString(hex);
        StringReadTable table = StringReadTable.Decode([(new ObjectId(7u), body), (new ObjectId(3u), body), (new ObjectId(uint.MaxValue), body)]);
        string first = Assert.IsType<string>(table.ResolveString(new ObjectId(7)));
        string second = Assert.IsType<string>(table.ResolveString(new ObjectId(3)));
        string third = Assert.IsType<string>(table.ResolveString(new ObjectId(uint.MaxValue)));
        Assert.Equal(first, second);
        Assert.Equal(first, third);
        Assert.NotSame(first, second);
        Assert.NotSame(first, third);
        Assert.NotSame(second, third);
        Assert.Same(second, table.ResolveString(new ObjectId(3)));
    }

    [Fact]
    public void StringReadTableNormalizesAllEmptyContentsButStillRequiresUniqueNonzeroIds() {
        byte[] body = [0x00];
        StringReadTable table = StringReadTable.Decode([(new ObjectId(7u), body), (new ObjectId(3u), body), (new ObjectId(uint.MaxValue), body)]);
        Assert.Same(string.Empty, table.ResolveString(new ObjectId(7)));
        Assert.Same(string.Empty, table.ResolveString(new ObjectId(3)));
        Assert.Same(string.Empty, table.ResolveString(new ObjectId(uint.MaxValue)));
        Assert.Null(table.ResolveString(new ObjectId(0)));
        Assert.Throws<InvalidDataException>(() => StringReadTable.Decode([(new ObjectId(3u), body), (new ObjectId(3u), body)]));
        Assert.Throws<InvalidDataException>(() => StringReadTable.Decode([(new ObjectId(0u), body)]));
    }

    [Fact]
    public void StringReadTableAllowsEmptyInputButOnlyNullCanResolve() {
        StringReadTable table = StringReadTable.Decode([]);
        Assert.Null(table.ResolveString(new ObjectId(0)));
        Assert.Throws<InvalidDataException>(() => table.ResolveString(new ObjectId(1)));
        Assert.Throws<InvalidDataException>(() => table.ResolveString(new ObjectId(uint.MaxValue)));
        Assert.Throws<ArgumentNullException>(() => StringReadTable.Decode(null!));
    }

    [Fact]
    public void StringReadTableRejectsZeroAndDuplicateEntryIdsEvenForIdenticalContents() {
        byte[] body = [0x03, 0x41];
        Assert.Throws<InvalidDataException>(() => StringReadTable.Decode([(new ObjectId(0u), body)]));
        Assert.Throws<InvalidDataException>(() => StringReadTable.Decode([(new ObjectId(1u), body), (new ObjectId(1u), body)]));
        Assert.Throws<InvalidDataException>(() => StringReadTable.Decode([
            (new ObjectId(1u), body), (new ObjectId(1u), new byte[] { 0x03, 0x42 })]));
        Assert.Throws<InvalidDataException>(() => StringReadTable.Decode([
            (new ObjectId(uint.MaxValue), body), (new ObjectId(uint.MaxValue), body)]));
    }

    [Theory]
    [InlineData("05C080")] // Invalid UTF-8.
    [InlineData("FEFFFFFF0F")] // Length exceeds the supported Int32 range.
    [InlineData("0041")] // A valid empty body followed by unrelated bytes.
    public void StringReadTableRejectsMalformedAndTrailingBodies(string hex) {
        Assert.Throws<InvalidDataException>(() =>
            StringReadTable.Decode([(new ObjectId(1u), Convert.FromHexString(hex))]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("80")]
    [InlineData("0741")]
    [InlineData("0200")]
    public void StringReadTablePreservesReaderFailureForMissingOrTruncatedHeader(string hex) {
        Assert.Throws<EndOfStreamException>(() =>
            StringReadTable.Decode([(new ObjectId(1u), Convert.FromHexString(hex))]));
    }

    [Fact]
    public void StringReadTableDoesNotRetainInputBuffersOrDeferEnumeration() {
        byte[] first = [0x03, 0x41];
        byte[] second = [0x02, 0xE9, 0x00];
        List<(ObjectId Id, ReadOnlyMemory<byte> Body)> records = [(new ObjectId(1), first), (new ObjectId(2), second)];
        StringReadTable table = StringReadTable.Decode(records);
        first.AsSpan().Fill(0xFF);
        second.AsSpan().Clear();
        records.Clear();
        Assert.Equal("A", table.ResolveString(new ObjectId(1)));
        Assert.Equal("é", table.ResolveString(new ObjectId(2)));
        Assert.Same(table.ResolveString(new ObjectId(1)), table.ResolveString(new ObjectId(1)));
    }

    [Fact]
    public void StringReadTableRecordOrderDoesNotAffectBindingsAndViewsDoNotShareAnIdNamespace() {
        (ObjectId Id, ReadOnlyMemory<byte> Body)[] records = [
            (new ObjectId(1), new byte[] { 0x03, 0x41 }),
            (new ObjectId(2), new byte[] { 0x03, 0x41 }),
            (new ObjectId(9), new byte[] { 0x03, 0x42 }),
        ];
        StringReadTable forward = StringReadTable.Decode(records);
        StringReadTable reverse = StringReadTable.Decode(records.Reverse());
        foreach (ObjectId id in new ObjectId[] { new(1), new(2), new(9) }) {
            Assert.Equal(forward.ResolveString(id), reverse.ResolveString(id));
        }
        Assert.NotSame(forward.ResolveString(new ObjectId(1)), forward.ResolveString(new ObjectId(2)));
        Assert.NotSame(reverse.ResolveString(new ObjectId(1)), reverse.ResolveString(new ObjectId(2)));
        StringReadTable otherRevision = StringReadTable.Decode([(new ObjectId(1u), new byte[] { 0x03, 0x43 })]);
        Assert.Equal("C", otherRevision.ResolveString(new ObjectId(1)));
        Assert.Equal("A", forward.ResolveString(new ObjectId(1)));
        Assert.Throws<InvalidDataException>(() => otherRevision.ResolveString(new ObjectId(9)));
    }

    [Fact]
    public void StringReadTableIteratorFailureCannotPublishAPartialTableOrChangeAnExistingView() {
        StringReadTable existing = StringReadTable.Decode([(new ObjectId(1u), new byte[] { 0x03, 0x41 })]);
        string original = Assert.IsType<string>(existing.ResolveString(new ObjectId(1)));
        StringReadTable? failed = null;
        bool disposed = false;
        IEnumerable<(ObjectId Id, ReadOnlyMemory<byte> Body)> FailingRecords() {
            try {
                yield return (new ObjectId(1), new byte[] { 0x03, 0x42 });
                throw new IOException("Input enumeration failed after a valid record.");
            } finally {
                disposed = true;
            }
        }

        Assert.Throws<IOException>(() => failed = StringReadTable.Decode(FailingRecords()));
        Assert.True(disposed);
        Assert.Null(failed);
        Assert.Same(original, existing.ResolveString(new ObjectId(1)));
        Assert.Equal("A", existing.ResolveString(new ObjectId(1)));
    }

    [Fact]
    public void StringReadTableDecodeFailureLeavesAcceptedParentAndPendingCaptureUsable() {
        DurableSchema schema = new("string.read.isolation", 1, new DurableFieldInfo(1, TypeTag.String));
        CaptureSession session = new();
        Domain source = new() { Text = "parent" };
        using CaptureContext first = session.BeginCapture();
        first.AddRoot<Domain, ObjectId>(source, schema, static (value, context) => context.CaptureString(value.Text));
        CapturedGraph parent = first.Seal();
        session.Accept(parent);

        source.Text = "pending";
        using CaptureContext next = session.BeginCapture();
        next.AddRoot<Domain, ObjectId>(source, schema, static (value, context) => context.CaptureString(value.Text));
        CapturedGraph candidate = next.Seal();
        StringReadTable? failed = null;
        Assert.Throws<InvalidDataException>(() => failed = StringReadTable.Decode([
            (new ObjectId(2u), new byte[] { 0x03, 0x41 }), (new ObjectId(3u), new byte[] { 0x00, 0x41 })]));
        Assert.Null(failed);
        Assert.Same(parent, session.Current);
        Assert.Equal("parent", parent.Objects.Single(item => item.Kind == ObjectStateKind.String).StringContent);
        session.Accept(candidate);
        Assert.Same(candidate, session.Current);
        Assert.Equal(parent.RootIds[0], candidate.RootIds[0]);
        Assert.Equal("pending", candidate.Objects.Single(item => item.Kind == ObjectStateKind.String).StringContent);
    }

    private sealed class Domain : IDurableObject {
        internal string? Text;
    }
}
