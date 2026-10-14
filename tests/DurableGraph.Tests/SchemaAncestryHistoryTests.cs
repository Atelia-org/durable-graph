using System.Text;
using Atelia.DurableGraph.Build;

namespace Atelia.DurableGraph.Tests;

public sealed class SchemaAncestryHistoryTests {
    [Fact]
    public void BaseRecordHasCanonicalGoldenBytesAndRoundTrips() {
        using Fixture fixture = new();
        SchemaHistoryRecord record = Record("Leaf", 2, new("基类", 3));
        string expected = "// durable-graph-schema-history:2\n// schema-begin\n" +
            "// schema-id-base64:TGVhZg==\n// version:2\n// kind:1\n// base:5Z+657G7|3\n" +
            "// field:1|2\n// schema-end\n";

        Assert.Equal(expected, SchemaHistoryTextCodec.RenderHistory(record, 2));
        SchemaHistoryRecord parsed = SchemaHistoryTextCodec.ParseHistory(fixture.Write("record.dgschema", expected));
        Assert.Equal(record.Key, parsed.Key);
        Assert.True(record.ShapeEquals(parsed));
        Assert.Equal(new SchemaHistoryKey("基类", 3), parsed.BaseSchema);
    }

    [Fact]
    public void NewClassOnlyRecordUsesVersionNineWithArityZero() {
        Assert.Equal(
            "// durable-graph-schema-history:9\n// schema-begin\n// schema-id-base64:QmFzZQ==\n" +
            "// version:1\n// kind:1\n// arity:0\n// field:1|2\n// schema-end\n",
            SchemaHistoryTextCodec.RenderHistory(Record("Base", 1)));
    }

    [Fact]
    public void ThreeLevelsRequireExplicitVersionPropagationAndKeepHistoricalBindings() {
        using Fixture fixture = new();
        SchemaHistoryRecord base1 = Record("Base", 1);
        SchemaHistoryRecord middle1 = Record("Middle", 1, base1.Key);
        SchemaHistoryRecord leaf1 = Record("Leaf", 1, middle1.Key);
        fixture.Publish(leaf1, middle1, base1);
        string[] initial = fixture.HistoryContents();
        SchemaHistoryRecord base2 = Record("Base", 2, fieldType: 3);
        SchemaHistoryRecord middle2 = Record("Middle", 2, base2.Key);
        SchemaHistoryRecord leaf2 = Record("Leaf", 2, middle2.Key);

        Assert.Throws<SchemaHistoryException>(() => fixture.Publish(
            base2, Record("Middle", 1, base2.Key), leaf1));
        Assert.Equal(initial, fixture.HistoryContents());
        Assert.Throws<SchemaHistoryException>(() => fixture.Publish(
            base2, middle2, Record("Leaf", 1, middle2.Key)));
        Assert.Equal(initial, fixture.HistoryContents());

        Assert.Equal("published 3 schema-history record(s); 0 already exact", fixture.Publish(leaf2, middle2, base2).Message);
        Assert.Equal("published 0 schema-history record(s); 3 already exact", fixture.Publish(base2, middle2, leaf2).Message);
        Assert.Equal("verified 3 current manifest candidate(s) against 6 schema-history record(s)",
            fixture.Verify(leaf2, base2, middle2).Message);
        SchemaHistoryRecord[] accepted = Directory.GetFiles(fixture.History).Select(SchemaHistoryTextCodec.ParseHistory).ToArray();
        Assert.Equal(base1.Key, Assert.Single(accepted, row => row.Key == middle1.Key).BaseSchema);
        Assert.Equal(middle1.Key, Assert.Single(accepted, row => row.Key == leaf1.Key).BaseSchema);
    }

    [Fact]
    public void CandidateCanReferenceAcceptedBaseWithoutRepeatingItInManifest() {
        using Fixture fixture = new();
        SchemaHistoryRecord base1 = Record("Base", 1);
        fixture.Publish(base1);
        SchemaHistoryRecord leaf = Record("Leaf", 1, base1.Key);

        Assert.Equal("published 1 schema-history record(s); 0 already exact", fixture.Publish(leaf).Message);
        Assert.Equal("verified 1 current manifest candidate(s) against 2 schema-history record(s)", fixture.Verify(leaf).Message);
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData(1, null)]
    [InlineData(1, 2)]
    public void SameVersionCannotAddRemoveOrChangeBaseBinding(int? oldBaseVersion, int? newBaseVersion) {
        using Fixture fixture = new();
        fixture.Publish(Record("Base", 1), Record("Base", 2), Record("Leaf", 1,
            oldBaseVersion is int oldVersion ? new SchemaHistoryKey("Base", oldVersion) : null));
        SchemaHistoryRecord changed = Record("Leaf", 1,
            newBaseVersion is int newVersion ? new SchemaHistoryKey("Base", newVersion) : null);
        string[] before = fixture.HistoryContents();

        Assert.Throws<SchemaHistoryException>(() => fixture.Publish(Record("Independent", 1), changed));
        Assert.Throws<SchemaHistoryException>(() => fixture.Verify(changed));
        Assert.Equal(before, fixture.HistoryContents());
    }

    [Fact]
    public void MissingCandidateAncestorFailsBeforeAnyFilesAreCreated() {
        using Fixture fixture = new();
        SchemaHistoryException exception = Assert.Throws<SchemaHistoryException>(() => fixture.Publish(
            Record("Independent", 1), Record("Leaf", 1, new("Missing", 7))));

        Assert.Contains("missing base schema 'Missing' version 7", exception.Message);
        Assert.False(Directory.Exists(fixture.History));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CandidateCannotRepairIncompleteAcceptedHistory(bool verify) {
        using Fixture fixture = new();
        SchemaHistoryRecord missingBase = Record("Base", 1);
        SchemaHistoryRecord leaf = Record("Leaf", 1, missingBase.Key);
        fixture.WriteAccepted(leaf);
        string[] before = fixture.HistoryContents();

        SchemaHistoryException exception = Assert.Throws<SchemaHistoryException>(() => {
            if (verify) {
                fixture.Verify(missingBase, leaf);
            } else {
                fixture.Publish(missingBase, leaf);
            }
        });

        Assert.Contains("missing base schema 'Base' version 1", exception.Message);
        Assert.Equal(before, fixture.HistoryContents());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RepeatedAncestorIdentityIsRejectedEvenAcrossVersions(bool accepted, bool differentVersions) {
        using Fixture fixture = new();
        SchemaHistoryRecord first = Record("First", 1, new("Second", 1));
        SchemaHistoryRecord second = Record("Second", 1, new("First", differentVersions ? 2 : 1));
        SchemaHistoryRecord[] records = differentVersions ? [first, second, Record("First", 2)] : [first, second];

        if (accepted) {
            foreach (SchemaHistoryRecord record in records) {
                fixture.WriteAccepted(record);
            }
        }

        string[] before = fixture.HistoryContents();
        SchemaHistoryException exception = Assert.Throws<SchemaHistoryException>(() => fixture.Publish(records));
        Assert.Contains("repeats ancestor schema", exception.Message);
        Assert.Equal(before, fixture.HistoryContents());

        if (accepted) {
            Assert.Throws<SchemaHistoryException>(() => fixture.Verify(records));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("QmFzZQ==")]
    [InlineData("QmFzZQ==|1|2")]
    [InlineData("|1")]
    [InlineData("QmFzZQ==|0")]
    [InlineData("QmFzZQ==|01")]
    [InlineData("QmFzZQ==|+1")]
    [InlineData("QmFzZQ==|2147483648")]
    [InlineData("QmFzZQ==|")]
    [InlineData("QmFzZQ== |1")]
    [InlineData("QmFzZR==|1")]
    [InlineData("/w==|1")]
    [InlineData("IA==|1")]
    public void MalformedBaseEntryIsRejected(string entry) {
        using Fixture fixture = new();
        string text = Manifest(Record("Leaf", 1)).Replace(
            "// arity:0\n", $"// arity:0\n// base:{entry}\n", StringComparison.Ordinal);
        string path = fixture.Write("invalid.g.cs", text);

        Assert.Throws<SchemaHistoryException>(() => SchemaHistoryTextCodec.ParseManifest(path));
    }

    [Theory]
    [InlineData("// base:QmFzZQ==|1\n// base:QmFzZQ==|1\n// field:1|2\n")]
    [InlineData("// field:1|2\n// base:QmFzZQ==|1\n")]
    public void DuplicateOrMisplacedBaseEntryIsRejected(string body) {
        using Fixture fixture = new();
        string text = Manifest(Record("Leaf", 1)).Replace("// field:1|2\n", body, StringComparison.Ordinal);

        Assert.Throws<SchemaHistoryException>(() => SchemaHistoryTextCodec.ParseManifest(fixture.Write("invalid.g.cs", text)));
    }

    private static SchemaHistoryRecord Record(string id, int version, SchemaHistoryKey? baseSchema = null, int fieldType = 2) {
        return new SchemaHistoryRecord(id, Convert.ToBase64String(Encoding.UTF8.GetBytes(id)), version,
            [new SchemaHistoryField(1, fieldType)], baseSchema);
    }

    private static string Manifest(params SchemaHistoryRecord[] records) {
        return "// durable-graph-schema-history-manifest:9\n" + string.Concat(records.Select(record =>
            SchemaHistoryTextCodec.RenderHistory(record).Replace("// durable-graph-schema-history:9\n", "", StringComparison.Ordinal)));
    }

    private sealed class Fixture : IDisposable {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "Atelia.DurableGraph.Tests", Guid.NewGuid().ToString("N"));
        private readonly SchemaHistoryTool _tool = new();

        public Fixture() => Directory.CreateDirectory(_root);

        public string History => Path.Combine(_root, "history");

        public string Write(string name, string text) {
            string path = Path.Combine(_root, name);
            File.WriteAllText(path, text, SchemaHistoryTextCodec.Utf8NoBom);
            return path;
        }

        public SchemaHistoryResult Publish(params SchemaHistoryRecord[] records) => _tool.Publish(Write("manifest.g.cs", Manifest(records)), History);

        public SchemaHistoryResult Verify(params SchemaHistoryRecord[] records) => _tool.Verify(Write("manifest.g.cs", Manifest(records)), History);

        public void WriteAccepted(SchemaHistoryRecord record) {
            Directory.CreateDirectory(History);
            string content = SchemaHistoryTextCodec.RenderHistory(record);
            File.WriteAllText(Path.Combine(History, SchemaHistoryTextCodec.GetHistoryFileName(record, content)), content, SchemaHistoryTextCodec.Utf8NoBom);
        }

        public string[] HistoryContents() => Directory.Exists(History)
            ? Directory.GetFiles(History).Order(StringComparer.Ordinal).Select(path => Path.GetFileName(path) + "\n" + File.ReadAllText(path)).ToArray()
            : [];

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
