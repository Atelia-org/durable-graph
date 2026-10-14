using System.Text;
using Atelia.DurableGraph.Build;

namespace Atelia.DurableGraph.Tests;

public sealed class NullableTemplateHistoryTests {
    [Fact]
    public void VersionSixGoldenRetainsNullableChildVersionAndParameters() {
        using Fixture fixture = new();
        string text = History("Holder", "// field:1|18|q(b2)\n// field:2|18|q(p0)\n// field:3|18|q(nUG9pbnQ=())|2\n", arity: 1);
        SchemaHistoryRecord record = fixture.Parse(text);
        Assert.Equal(text, SchemaHistoryTextCodec.RenderHistory(record, 6));
        Assert.Equal(new SchemaHistoryKey("Point", 2), record.Fields[2].InlineSchema);
        Assert.Equal("q(b2)", record.Fields[1].ValuePattern.Substitute([record.Fields[0].ValuePattern.ElementType!]).ToString());
    }

    [Theory]
    [InlineData("1|18|q(b4)")]
    [InlineData("1|18|q(q(b2))")]
    [InlineData("1|18|q(a1(b2))")]
    [InlineData("1|18|q(l(b2))")]
    [InlineData("1|18|q(b2)|1")]
    [InlineData("1|18|q(nUG9pbnQ=())")]
    [InlineData("1|18|q(nUG9pbnQ=())|0")]
    [InlineData("1|18|q(nUG9pbnQ=())|01")]
    [InlineData("1|18|q(p0)|1")]
    [InlineData("1|18|q(p1)")]
    [InlineData("1|18|q(b2,b2)")]
    [InlineData("1|18|q()")]
    [InlineData("1|17|q(p0)")]
    public void InvalidNullablePatternsAndOperandsAreRejected(string field) {
        using Fixture fixture = new();
        Assert.Throws<SchemaHistoryException>(() => fixture.Parse(History("Bad", "// field:" + field + "\n", arity: 1)));
    }

    [Theory]
    [InlineData("1|18|q(b2)")]
    [InlineData("1|15|l(q(b2))")]
    [InlineData("1|15|nQm94(q(b2))")]
    public void OldHistoryRejectsNullableAtEveryDepth(string field) {
        using Fixture fixture = new();
        for (int version = 3; version <= 5; version++) {
            Assert.Throws<SchemaHistoryException>(() => fixture.Parse(
                History("Old", "// field:" + field + "\n").Replace("history:6", "history:" + version)));
        }
    }

    [Fact]
    public void NullableInlineClosureIsRequiredBeforeAnyPublication() {
        using Fixture fixture = new();
        string owner = History("World", "// field:1|18|q(nUG9pbnQ=())|1\n");
        Assert.Throws<SchemaHistoryException>(() => fixture.Publish(owner));
        Assert.Empty(fixture.Accepted());
        string point = History("Point", "// field:1|2\n", kind: 2);
        fixture.Publish(owner, point);
        string[] accepted = fixture.Accepted();
        fixture.Publish(owner, point);
        Assert.Equal(accepted, fixture.Accepted());
        Assert.Throws<SchemaHistoryException>(() => fixture.Publish(History("Point", "// field:1|3\n", kind: 2)));
        Assert.Equal(accepted, fixture.Accepted());
    }

    [Fact]
    public void NullableChildCannotBeReferenceFamilyEvenUnderAReferenceConstructor() {
        using Fixture fixture = new();
        string node = History("Node", "// field:1|2\n");
        string owner = History("World", "// field:1|15|l(q(nTm9kZQ==()))\n");
        Assert.Throws<SchemaHistoryException>(() => fixture.Publish(owner, node));
        Assert.Empty(fixture.Accepted());
    }

    [Fact]
    public void VersionSixPublicationPreservesAcceptedVersionFiveBytesAndHash() {
        using Fixture fixture = new();
        string old = History("Old", "// field:1|2\n").Replace("history:6", "history:5");
        fixture.Accept(old);
        string[] before = fixture.Accepted();
        fixture.Publish(History("New", "// field:1|18|q(b2)\n"));
        Assert.Contains(before[0], fixture.Accepted());
        Assert.Equal(2, fixture.Accepted().Length);
    }

    private static string History(string id, string fields, int arity = 0, int kind = 1) =>
        GenericTemplateHistoryTests.History(id, arity: arity, kind: kind, body: fields).Replace("history:3", "history:6");

    private sealed class Fixture : IDisposable {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "durable-nullable-history-" + Guid.NewGuid().ToString("N"));
        private string HistoryPath => Path.Combine(_root, "accepted");
        internal Fixture() => Directory.CreateDirectory(_root);
        internal SchemaHistoryRecord Parse(string text) {
            string path = Path.Combine(_root, "input.dgschema");
            File.WriteAllText(path, text, new UTF8Encoding(false));
            return SchemaHistoryTextCodec.ParseHistory(path);
        }
        internal void Accept(string text) {
            SchemaHistoryRecord record = Parse(text);
            Directory.CreateDirectory(HistoryPath);
            File.WriteAllText(Path.Combine(HistoryPath, SchemaHistoryTextCodec.GetHistoryFileName(record, text)), text, new UTF8Encoding(false));
        }
        internal void Publish(params string[] histories) {
            string path = Path.Combine(_root, "manifest.g.cs");
            File.WriteAllText(path, "// durable-graph-schema-history-manifest:6\n" +
                string.Concat(histories.Select(text => text.Replace("// durable-graph-schema-history:6\n", ""))), new UTF8Encoding(false));
            new SchemaHistoryTool().Publish(path, HistoryPath);
            new SchemaHistoryTool().Verify(path, HistoryPath);
        }
        internal string[] Accepted() => Directory.Exists(HistoryPath)
            ? Directory.GetFiles(HistoryPath).Order(StringComparer.Ordinal).Select(path => Path.GetFileName(path) + File.ReadAllText(path)).ToArray()
            : [];
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
