using System.Text;
using Atelia.DurableGraph.Build;

namespace Atelia.DurableGraph.Tests;

public sealed class InlineSchemaHistoryToolTests {
    [Fact]
    public void VersionTwoGoldenFreezesKindAndExactInlineOperand() {
        using Fixture fixture = new();
        SchemaHistoryRecord record = Record("World", fields: [new(7, 16, InlineSchema: new("Point", 2))]);
        const string golden = "// durable-graph-schema-history:2\n// schema-begin\n" +
            "// schema-id-base64:V29ybGQ=\n// version:1\n// kind:1\n" +
            "// field:7|16|UG9pbnQ=|2\n// schema-end\n";
        Assert.Equal(golden, SchemaHistoryTextCodec.RenderHistory(record, 2));
        SchemaHistoryRecord parsed = SchemaHistoryTextCodec.ParseHistory(fixture.Write("world.dgschema", golden));
        Assert.True(record.ShapeEquals(parsed));
        Assert.Null(Assert.Single(parsed.Fields).TargetSchemaId);
        Assert.Equal(new SchemaHistoryKey("Point", 2), Assert.Single(parsed.Fields).InlineSchema);
        Assert.Equal(2, SchemaHistoryTextCodec.ParseHistory(fixture.Write("point.dgschema",
            SchemaHistoryTextCodec.RenderHistory(Record("Point", 2, kind: 2)))).Kind);
    }

    [Fact]
    public void OldClassHistoryIsPreservedWhileAllNewWritesUseVersionNine() {
        using Fixture fixture = new();
        SchemaHistoryRecord old = Record("Base");
        const string oldContent = "// durable-graph-schema-history:1\n// schema-begin\n" +
            "// schema-id-base64:QmFzZQ==\n// version:1\n// schema-end\n";
        fixture.Accept(old, oldContent);
        string[] before = fixture.HistoryContents();
        fixture.Publish(old, Record("Point", kind: 2), Record("World", baseSchema: old.Key,
            fields: [new(1, 16, InlineSchema: new("Point", 1))]));
        Assert.Equal(3, fixture.HistoryContents().Length);
        Assert.Contains(before[0], fixture.HistoryContents());
        Assert.Equal(2, Directory.GetFiles(fixture.History).Count(path =>
            File.ReadAllText(path).StartsWith("// durable-graph-schema-history:9\n", StringComparison.Ordinal)));
        string[] accepted = fixture.HistoryContents();
        fixture.Verify(old, Record("Point", kind: 2));
        fixture.Publish(old);
        Assert.Equal(accepted, fixture.HistoryContents());
    }

    [Theory]
    [InlineData("// kind:0\n")]
    [InlineData("// kind:3\n")]
    [InlineData("// kind:01\n")]
    [InlineData("// kind:+1\n")]
    [InlineData("// kind:1\n// kind:1\n")]
    [InlineData("")]
    public void VersionTwoRequiresOneCanonicalKnownKind(string kind) {
        using Fixture fixture = new();
        string text = SchemaHistoryTextCodec.RenderHistory(Record("World"), 2).Replace("// kind:1\n", kind);
        Assert.Throws<SchemaHistoryException>(() => SchemaHistoryTextCodec.ParseHistory(fixture.Write("bad.dgschema", text)));
    }

    [Theory]
    [InlineData("1|16")]
    [InlineData("1|16|UG9pbnQ=")]
    [InlineData("1|16|UG9pbnQ=|0")]
    [InlineData("1|16|UG9pbnQ=|01")]
    [InlineData("1|16|UG9pbnQ=|+1")]
    [InlineData("1|16|UG9pbnQ=|2147483648")]
    [InlineData("1|16|UG9pbnR=|1")]
    [InlineData("1|16|IA==|1")]
    [InlineData("1|16|/w==|1")]
    [InlineData("1|16|UG9pbnQ=|1|1")]
    [InlineData("1|2|UG9pbnQ=|1")]
    [InlineData("1|15|UG9pbnQ=|1")]
    public void InlineOperandMustHaveCanonicalExactIdentity(string field) {
        using Fixture fixture = new();
        string text = SchemaHistoryTextCodec.RenderHistory(Record("World", fields: [new(1, 2)]), 2)
            .Replace("1|2", field);
        Assert.Throws<SchemaHistoryException>(() => SchemaHistoryTextCodec.ParseHistory(fixture.Write("bad.dgschema", text)));
    }

    [Fact]
    public void VersionOneCannotSmuggleInlineTagOrKind() {
        using Fixture fixture = new();
        string text = SchemaHistoryTextCodec.RenderHistory(Record("World", fields: [new(1, 16, InlineSchema: new("Point", 1))]), 2)
            .Replace("history:2", "history:1");
        Assert.Throws<SchemaHistoryException>(() => SchemaHistoryTextCodec.ParseHistory(fixture.Write("bad.dgschema", text)));
        Assert.Throws<SchemaHistoryException>(() => SchemaHistoryTextCodec.ParseHistory(fixture.Write("bad.dgschema", text.Replace("// kind:1\n", ""))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingAcceptedInlineCannotBeRepairedByCurrentCandidate(bool verify) {
        using Fixture fixture = new();
        SchemaHistoryRecord missing = Record("Point", kind: 2);
        SchemaHistoryRecord owner = Record("World", fields: [new(1, 16, InlineSchema: missing.Key)]);
        fixture.Accept(owner);
        string[] before = fixture.HistoryContents();
        Assert.Throws<SchemaHistoryException>(() => {
            if (verify) { fixture.Verify(missing, owner); }
            else { fixture.Publish(missing, owner); }
        });
        Assert.Equal(before, fixture.HistoryContents());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FamilyKindCannotChangeAcrossVersionsOrAcrossAcceptedAndCurrent(bool accepted) {
        using Fixture fixture = new();
        SchemaHistoryRecord original = Record("Shape");
        SchemaHistoryRecord changed = Record("Shape", 2, kind: 2);
        if (accepted) { fixture.Publish(original); }
        string[] before = fixture.HistoryContents();
        SchemaHistoryException exception = Assert.Throws<SchemaHistoryException>(() => fixture.Publish(
            Record("Independent"), original, changed));
        Assert.Contains("changes kind", exception.Message);
        Assert.Equal(before, fixture.HistoryContents());
    }

    [Fact]
    public void ExactEdgesRejectWrongKindAndInlineCannotHaveBase() {
        using Fixture fixture = new();
        SchemaHistoryRecord reference = Record("Reference");
        SchemaHistoryRecord value = Record("Value", kind: 2);
        Assert.Throws<SchemaHistoryException>(() => fixture.Publish(reference,
            Record("World", fields: [new(1, 16, InlineSchema: reference.Key)])));
        Assert.Throws<SchemaHistoryException>(() => fixture.Publish(value, Record("World", baseSchema: value.Key)));
        Assert.Throws<SchemaHistoryException>(() => fixture.Publish(reference,
            Record("Value", kind: 2, baseSchema: reference.Key)));
        Assert.Empty(fixture.HistoryContents());
    }

    [Fact]
    public void ExactCycleAndMissingCurrentDependencyFailBeforeAnyFilesAreCreated() {
        using Fixture fixture = new();
        Assert.Throws<SchemaHistoryException>(() => fixture.Publish(Record("Independent"),
            Record("World", fields: [new(1, 16, InlineSchema: new("Missing", 1))])));
        SchemaHistoryException cycle = Assert.Throws<SchemaHistoryException>(() => fixture.Publish(
            Record("A", kind: 2, fields: [new(1, 16, InlineSchema: new("B", 1))]),
            Record("B", kind: 2, fields: [new(1, 16, InlineSchema: new("A", 1))])));
        Assert.Contains("cycle", cycle.Message);
        Assert.False(Directory.Exists(fixture.History));
    }

    [Fact]
    public void SharedDagAndNominalBackEdgeAreAllowedWithoutNominalTargetClosure() {
        using Fixture fixture = new();
        // Each level shares both references to its predecessor: expanded as a tree this is exponential.
        List<SchemaHistoryRecord> records = [Record("V0", kind: 2, fields: [new(1, 15, "World"), new(2, 15, "Absent")])];
        for (int index = 1; index <= 40; index++) {
            records.Add(Record($"V{index}", kind: 2, fields: [
                new(1, 16, InlineSchema: records[^1].Key), new(2, 16, InlineSchema: records[^1].Key)]));
        }
        records.Add(Record("World", fields: [new(1, 16, InlineSchema: records[^1].Key)]));
        fixture.Publish(records.ToArray());
        fixture.Verify(records.ToArray());
        Assert.Equal(42, fixture.HistoryContents().Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedDepthBoundIncludesCachedInlineSubgraphs(bool dependencyFirst) {
        using Fixture fixture = new();
        List<SchemaHistoryRecord> records = [Record("V0", kind: 2)];
        for (int index = 1; index < 254; index++) {
            records.Add(Record($"V{index}", kind: 2, fields: [new(1, 16, InlineSchema: records[^1].Key)]));
        }
        records.Add(Record("Base", fields: [new(1, 16, InlineSchema: records[^1].Key)]));
        records.Add(Record("World", baseSchema: records[^1].Key)); // 256 nodes, accepted.
        SchemaHistoryRecord[] accepted = dependencyFirst ? records.ToArray() : records.AsEnumerable().Reverse().ToArray();
        fixture.Publish(accepted);
        string[] before = fixture.HistoryContents();
        Assert.Throws<SchemaHistoryException>(() => fixture.Publish(Record("Independent"), Record("TooDeep", baseSchema: new("World", 1))));
        Assert.Equal(before, fixture.HistoryContents());
    }

    [Fact]
    public void InlineVersionChangeRequiresOwnerVersionChangeAndConflictIsAtomic() {
        using Fixture fixture = new();
        SchemaHistoryRecord point1 = Record("Point", kind: 2, fields: [new(1, 2)]);
        SchemaHistoryRecord world1 = Record("World", fields: [new(1, 16, InlineSchema: point1.Key)]);
        fixture.Publish(world1, point1);
        string[] before = fixture.HistoryContents();
        SchemaHistoryRecord point2 = Record("Point", 2, kind: 2, fields: [new(1, 3)]);
        Assert.Throws<SchemaHistoryException>(() => fixture.Publish(point2,
            Record("World", fields: [new(1, 16, InlineSchema: point2.Key)])));
        Assert.Equal(before, fixture.HistoryContents());
        fixture.Publish(point2, Record("World", 2, fields: [new(1, 16, InlineSchema: point2.Key)]));
        Assert.Equal(4, fixture.HistoryContents().Length);
    }

    private static SchemaHistoryRecord Record(string id, int version = 1, int kind = 1,
        SchemaHistoryKey? baseSchema = null, SchemaHistoryField[]? fields = null) =>
        new(id, Convert.ToBase64String(Encoding.UTF8.GetBytes(id)), version, fields ?? [], baseSchema, kind);

    private sealed class Fixture : IDisposable {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "durable-inline-history-" + Guid.NewGuid().ToString("N"));
        private readonly SchemaHistoryTool _tool = new();
        public Fixture() => Directory.CreateDirectory(_root);
        public string History => Path.Combine(_root, "history");
        public string Write(string name, string text) {
            string path = Path.Combine(_root, name);
            File.WriteAllText(path, text, SchemaHistoryTextCodec.Utf8NoBom);
            return path;
        }
        private string Manifest(SchemaHistoryRecord[] records) => Write("manifest.g.cs",
            "// durable-graph-schema-history-manifest:9\n" + string.Concat(records.Select(record =>
                SchemaHistoryTextCodec.RenderHistory(record).Replace("// durable-graph-schema-history:9\n", ""))));
        public void Publish(params SchemaHistoryRecord[] records) => _tool.Publish(Manifest(records), History);
        public void Verify(params SchemaHistoryRecord[] records) => _tool.Verify(Manifest(records), History);
        public void Accept(SchemaHistoryRecord record, string? content = null) {
            Directory.CreateDirectory(History);
            content ??= SchemaHistoryTextCodec.RenderHistory(record);
            File.WriteAllText(Path.Combine(History, SchemaHistoryTextCodec.GetHistoryFileName(record, content)), content, SchemaHistoryTextCodec.Utf8NoBom);
        }
        public string[] HistoryContents() => Directory.Exists(History)
            ? Directory.GetFiles(History).Order(StringComparer.Ordinal).Select(path => Path.GetFileName(path) + "\n" + File.ReadAllText(path)).ToArray()
            : [];
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
