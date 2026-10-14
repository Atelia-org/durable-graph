using System.Security.Cryptography;
using System.Text;
using Atelia.DurableGraph.Build;

namespace Atelia.DurableGraph.Tests;

public sealed class CrossAssemblyInheritanceBuildTests {
    private const string ReferenceHeader = "// durable-graph-schema-references:1\n";
    private const string ManifestHeader = "// durable-graph-schema-history-manifest:9\n";

    [Theory]
    [InlineData("p1,p0")]
    [InlineData("p0,p0")]
    [InlineData("b2,b2")]
    public void GenericBasePatternsRemainOpenWhileNominalReferencesNeedNoExactExport(string arguments) {
        using Files files = new();
        string baseTemplate = Class("base", 1, fields: "// field:1|17|p0\n// field:2|17|p1\n" +
            $"// field:3|15|n{B64("unregistered-target")}()\n", arity: 2);
        string references = References(Entry("A", "base", 1, baseTemplate));
        string owned = Class("world", 1, $"// base:n{B64("base")}({arguments})|1\n", arity: 2);
        string manifest = files.Write("candidate.cs", WithHash(owned, references));
        string refs = files.Write("refs.cs", references);
        SchemaHistoryTool tool = new();
        Assert.Contains("published 1", tool.Publish(manifest, files.History, refs).Message);
        Assert.Contains("against 1 schema-history", tool.Verify(manifest, files.History, refs).Message);
        SchemaHistoryRecord record = SchemaHistoryTextCodec.ParseHistory(Assert.Single(Directory.GetFiles(files.History)));
        Assert.Equal($"n{B64("base")}({arguments})", record.BaseType!.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedBaseAndInlineImportsStayReadOnlyAcrossOwnedHistory(bool verify) {
        using Files files = new();
        string references = References(
            Entry("A", "middle", 1, Class("middle", 1, Base("base", 1))),
            Entry("B", "base", 1, Class("base", 1, fields: InlineField("point", 1))),
            Entry("B", "base", 2, Class("base", 2, fields: InlineField("point", 1))),
            Entry("B", "point", 1, Inline("point", 1), contract: 1));
        string refs = files.Write("refs.cs", references);
        string manifest = files.Write("candidate.cs", WithHash(Class("world", 1, Base("middle", 1)), references));
        SchemaHistoryTool tool = new();
        Assert.Contains("published 1", tool.Publish(manifest, files.History, refs).Message);
        string accepted = Assert.Single(Directory.GetFiles(files.History));
        byte[] oldBytes = File.ReadAllBytes(accepted);

        manifest = files.Write("candidate.cs", WithHash(Class("world", 2, Base("base", 2)), references));
        Assert.Contains("published 1", tool.Publish(manifest, files.History, refs).Message);
        Run(verify, tool, manifest, files.History, refs);
        // The current candidate no longer uses middle/base v1, but its owned retained version does.
        string historyOnly = files.Write("history-only.cs", WithHash(ManifestHeader, references));
        Run(verify, tool, historyOnly, files.History, refs);
        Assert.Equal(oldBytes, File.ReadAllBytes(accepted));
        Assert.Equal(new[] { 1, 2 }, Directory.GetFiles(files.History)
            .Select(path => SchemaHistoryTextCodec.ParseHistory(path))
            .Select(record => { Assert.Equal("world", record.SchemaId); return record.Version; }).Order());
    }

    [Theory]
    [InlineData("unknown-contract")]
    [InlineData("inline-contract-on-class")]
    [InlineData("class-contract-on-inline")]
    [InlineData("wrong-id")]
    [InlineData("wrong-version")]
    [InlineData("two-owners")]
    [InlineData("wrong-hash")]
    [InlineData("missing-input")]
    public void ClassExportEnvelopeRejectsInvalidInputsBeforePublication(string variant) {
        using Files files = new();
        string entry = variant switch {
            "unknown-contract" => Entry("A", "base", 1, Class("base", 1), contract: 3),
            "inline-contract-on-class" => Entry("A", "base", 1, Class("base", 1), contract: 1),
            "class-contract-on-inline" => Entry("A", "base", 1, Inline("base", 1)),
            "wrong-id" => Entry("A", "base", 1, Class("other", 1)),
            "wrong-version" => Entry("A", "base", 1, Class("base", 2)),
            _ => Entry("A", "base", 1, Class("base", 1)),
        };
        string references = References(entry);
        if (variant == "two-owners") references += Entry("B", "base", 2, Class("base", 2));
        string manifest = files.Write("candidate.cs", WithHash(ManifestHeader, variant == "wrong-hash" ? ReferenceHeader : references));
        string? refs = variant == "missing-input" ? null : files.Write("refs.cs", references);
        foreach (bool verify in new[] { false, true }) {
            Assert.Throws<SchemaHistoryException>(() => Run(verify, new(), manifest, files.History, refs));
        }
        Assert.False(Directory.Exists(files.History));
    }

    [Theory]
    [InlineData("version", "missing base")]
    [InlineData("arity", "arity")]
    [InlineData("base-kind", "kind")]
    [InlineData("inline-kind", "kind")]
    [InlineData("cycle", "ancestor")]
    [InlineData("depth", "depth")]
    public void ImportedClassDependenciesRequireExactSelfContainedClosure(string variant, string error) {
        using Files files = new();
        string references = variant switch {
            "version" => References(Entry("A", "a", 1, Class("a", 1, Base("b", 2))), Entry("B", "b", 1, Class("b", 1))),
            "arity" => References(Entry("A", "a", 1, Class("a", 1, Base("b", 1))), Entry("B", "b", 1, Class("b", 1, arity: 1))),
            "base-kind" => References(Entry("A", "a", 1, Class("a", 1, Base("b", 1))), Entry("B", "b", 1, Inline("b", 1), contract: 1)),
            "inline-kind" => References(Entry("A", "a", 1, Class("a", 1, fields: InlineField("b", 1))), Entry("B", "b", 1, Class("b", 1))),
            "cycle" => References(Entry("A", "a", 1, Class("a", 1, Base("b", 1))), Entry("B", "b", 1, Class("b", 1, Base("a", 1)))),
            "depth" => References(Enumerable.Range(0, 257).Select(index => {
                string id = "a" + index.ToString("D3");
                return Entry("A", id, 1, Class(id, 1, index < 256 ? Base("a" + (index + 1).ToString("D3"), 1) : ""));
            }).ToArray()),
            _ => throw new InvalidOperationException(),
        };
        string manifest = files.Write("candidate.cs", WithHash(ManifestHeader, references));
        string refs = files.Write("refs.cs", references);
        foreach (bool verify in new[] { false, true }) {
            Assert.Contains(error, Assert.Throws<SchemaHistoryException>(() => Run(verify, new(), manifest, files.History, refs)).Message);
        }
        Assert.False(Directory.Exists(files.History));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurrentBaseCannotRepairAcceptedDerivedHistory(bool verify) {
        using Files files = new();
        string accepted = files.Accept(Class("world", 1, Base("local", 1)));
        byte[] before = File.ReadAllBytes(accepted);
        string manifest = files.Write("candidate.cs", Class("local", 1));
        Assert.Contains("missing base", Assert.Throws<SchemaHistoryException>(() => Run(verify, new(), manifest, files.History, null)).Message);
        Assert.Equal(before, File.ReadAllBytes(accepted));
        Assert.Single(Directory.GetFiles(files.History));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurrentImportedBaseCannotReplaceMissingHistoricalBase(bool verify) {
        using Files files = new();
        files.Accept(Class("world", 1, Base("base", 1)));
        string references = References(Entry("A", "base", 2, Class("base", 2)));
        string manifest = files.Write("candidate.cs", WithHash(Class("world", 2, Base("base", 2)), references));
        string refs = files.Write("refs.cs", references);
        Assert.Contains("missing base schema 'base' version 1", Assert.Throws<SchemaHistoryException>(() => Run(verify, new(), manifest, files.History, refs)).Message);
        Assert.Single(Directory.GetFiles(files.History));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ImportedBaseCannotBorrowOwnedAncestor(bool verify, bool ownedByHistory) {
        using Files files = new();
        if (ownedByHistory) files.Accept(Class("local", 1));
        string references = References(Entry("A", "base", 1, Class("base", 1, Base("local", 1))));
        string manifest = files.Write("candidate.cs", WithHash(ownedByHistory ? ManifestHeader : Class("local", 1), references));
        string refs = files.Write("refs.cs", references);
        Assert.Contains("missing base", Assert.Throws<SchemaHistoryException>(() => Run(verify, new(), manifest, files.History, refs)).Message);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ClassImportCannotSupplyOwnedDefinitionHistory(bool verify, bool ownedByHistory) {
        using Files files = new();
        if (ownedByHistory) files.Accept(Class("local", 2));
        string references = References(Entry("A", "local", 1, Class("local", 1)));
        string manifest = files.Write("candidate.cs", WithHash(ownedByHistory ? ManifestHeader : Class("local", 2), references));
        string refs = files.Write("refs.cs", references);
        Assert.Contains("owned definition", Assert.Throws<SchemaHistoryException>(() => Run(verify, new(), manifest, files.History, refs)).Message);
    }

    private static SchemaHistoryResult Run(bool verify, SchemaHistoryTool tool, string manifest, string history, string? refs) =>
        verify ? tool.Verify(manifest, history, refs) : tool.Publish(manifest, history, refs);
    private static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
    private static string WithHash(string owned, string references) => owned + "// references-sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(references))) + "\n";
    private static string References(params string[] entries) => ReferenceHeader + string.Concat(entries);
    private static string Entry(string owner, string id, int version, string manifest, int contract = 2) => $"// reference|{B64(owner)}|{B64(id)}|{version}|{contract}|{B64(manifest)}\n";
    private static string Class(string id, int version, string baseEntry = "", string fields = "", int arity = 0) => Record(id, version, 1, arity, baseEntry + fields);
    private static string Inline(string id, int version) => Record(id, version, 2, 0, "");
    private static string Record(string id, int version, int kind, int arity, string body) =>
        ManifestHeader + $"// schema-begin\n// schema-id-base64:{B64(id)}\n// version:{version}\n// kind:{kind}\n// arity:{arity}\n" + body + "// schema-end\n";
    private static string Base(string id, int version) => $"// base:n{B64(id)}()|{version}\n";
    private static string InlineField(string id, int version) => $"// field:1|16|n{B64(id)}()|{version}\n";

    private sealed class Files : IDisposable {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "DurableGraph.DB061.Build", Guid.NewGuid().ToString("N"));
        public string History => Path.Combine(Root, "history");
        public Files() => Directory.CreateDirectory(Root);
        public string Write(string name, string text) {
            string path = Path.Combine(Root, name);
            File.WriteAllText(path, text, new UTF8Encoding(false));
            return path;
        }
        public string Accept(string manifest) {
            SchemaHistoryRecord record = Assert.Single(SchemaHistoryTextCodec.ParseManifestText("accepted", manifest, out _));
            string content = SchemaHistoryTextCodec.RenderHistory(record);
            Directory.CreateDirectory(History);
            string path = Path.Combine(History, SchemaHistoryTextCodec.GetHistoryFileName(record, content));
            File.WriteAllText(path, content, new UTF8Encoding(false));
            return path;
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
