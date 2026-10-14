using System.Security.Cryptography;
using System.Text;
using Atelia.DurableGraph.Build;

namespace Atelia.DurableGraph.Tests;

public sealed class CrossAssemblyInlineBuildTests {
    private const string ReferenceHeader = "// durable-graph-schema-references:1\n";
    private const string ManifestHeader = "// durable-graph-schema-history-manifest:9\n";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReferenceClosureIsReadOnlyAndAcceptedHistoryBytesStayExact(bool verify) {
        using Files files = new();
        string referenced = References(
            Entry("A", "a", 1, Inline("a", 1, Field("b", 1))),
            Entry("B", "b", 1, Inline("b", 1)));
        string owned = Owner("world", 1, Field("a", 1));
        string manifest = files.Write("candidate.cs", WithHash(owned, referenced));
        string refs = files.Write("references.cs", referenced);
        SchemaHistoryTool tool = new();
        Assert.Contains("published 1", tool.Publish(manifest, files.History, refs).Message);
        string[] paths = Directory.GetFiles(files.History);
        Assert.Single(paths);
        byte[] before = File.ReadAllBytes(paths[0]);
        Assert.Equal("world", SchemaHistoryTextCodec.ParseHistory(paths[0]).SchemaId);
        SchemaHistoryResult result = Run(verify, tool, manifest, files.History, refs);
        Assert.Contains(verify ? "against 1 schema-history" : "0 schema-history", result.Message);
        Assert.Equal(before, File.ReadAllBytes(paths[0]));
        Assert.Single(Directory.GetFiles(files.History));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void OwnedManifestAllowsBomAndCrLfWithoutChangingReferenceHash(bool withBom, bool withCrLf) {
        using Files files = new();
        string referenced = References(Entry("A", "a", 1, Inline("a", 1)));
        string owned = WithHash(Owner("world", 1, Field("a", 1)), referenced);
        if (withCrLf) owned = owned.Replace("\n", "\r\n", StringComparison.Ordinal);
        string manifest = Path.Combine(files.Root, "candidate.cs");
        File.WriteAllText(manifest, owned, new UTF8Encoding(withBom));
        string refs = files.Write("references.cs", referenced);
        byte[] referenceBytes = File.ReadAllBytes(refs);

        IReadOnlyList<SchemaHistoryRecord> candidates = SchemaHistoryTextCodec.ParseManifest(manifest, out string? expectedHash);
        Assert.Equal("world", Assert.Single(candidates).SchemaId);
        Assert.Equal(Hash(referenced), expectedHash);

        SchemaHistoryTool tool = new();
        Assert.Equal("published 1 schema-history record(s); 0 already exact", tool.Publish(manifest, files.History, refs).Message);
        string history = Assert.Single(Directory.GetFiles(files.History));
        byte[] acceptedBytes = File.ReadAllBytes(history);
        Assert.Equal("world", SchemaHistoryTextCodec.ParseHistory(history).SchemaId);

        Assert.Equal("verified 1 current manifest candidate(s) against 1 schema-history record(s)", tool.Verify(manifest, files.History, refs).Message);
        Assert.Equal(acceptedBytes, File.ReadAllBytes(history));
        Assert.Equal(referenceBytes, File.ReadAllBytes(refs));
        Assert.Single(Directory.GetFiles(files.History));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoOwnedCandidatesStillValidateReferenceClosure(bool verify) {
        using Files files = new();
        string referenced = References(Entry("A", "a", 1, Inline("a", 1, Field("missing", 1))));
        string manifest = files.Write("candidate.cs", WithHash(ManifestHeader, referenced));
        string refs = files.Write("references.cs", referenced);
        Assert.Contains("missing inline", Assert.Throws<SchemaHistoryException>(() => Run(verify, new(), manifest, files.History, refs)).Message);
        Assert.False(Directory.Exists(files.History));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyReferenceInputKeepsLegacyDirectToolUsage(bool verify) {
        using Files files = new();
        string manifest = files.Write("empty.cs", ManifestHeader);
        string refs = files.Write("refs.cs", ReferenceHeader);
        Run(verify, new(), manifest, files.History, null);
        Run(verify, new(), manifest, files.History, refs);
        Assert.False(Directory.Exists(files.History));
    }

    [Theory]
    [InlineData("missing-file")]
    [InlineData("missing-argument")]
    [InlineData("wrong-hash")]
    [InlineData("missing-hash")]
    [InlineData("duplicate-hash")]
    [InlineData("uppercase-hash")]
    public void ReferenceInputMustMatchOwnedManifest(string variant) {
        using Files files = new();
        string referenced = References(Entry("A", "a", 1, Inline("a", 1)));
        string owned = WithHash(ManifestHeader, referenced);
        if (variant == "missing-hash") owned = ManifestHeader;
        if (variant == "wrong-hash") owned = WithHash(ManifestHeader, ReferenceHeader);
        if (variant == "duplicate-hash") owned += "// references-sha256:" + Hash(referenced) + "\n";
        if (variant == "uppercase-hash") owned = ManifestHeader + "// references-sha256:" + Hash(referenced).ToUpperInvariant() + "\n";
        string manifest = files.Write("candidate.cs", owned);
        string? refs = variant == "missing-argument" ? null : variant == "missing-file" ? Path.Combine(files.Root, "absent.cs") : files.Write("refs.cs", referenced);
        foreach (bool verify in new[] { false, true }) {
            Assert.Throws<SchemaHistoryException>(() => Run(verify, new(), manifest, files.History, refs));
        }
        Assert.False(Directory.Exists(files.History));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("two-owners")]
    [InlineData("unsorted")]
    [InlineData("truncated")]
    [InlineData("crlf")]
    [InlineData("bom")]
    [InlineData("unknown-protocol")]
    [InlineData("unknown-contract")]
    [InlineData("leading-zero")]
    [InlineData("invalid-utf8")]
    [InlineData("noncanonical-base64")]
    [InlineData("wrong-id")]
    [InlineData("wrong-version")]
    [InlineData("wrong-kind")]
    [InlineData("old-format")]
    [InlineData("multiple-records")]
    [InlineData("malformed-record")]
    [InlineData("record-crlf")]
    [InlineData("blank-owner")]
    public void MalformedReferencesFailBeforeEvenZeroCandidatePublication(string variant) {
        using Files files = new();
        string entry = Entry("A", "a", 1, Inline("a", 1));
        string references = variant switch {
            "duplicate" => References(entry, entry),
            "two-owners" => References(entry, Entry("B", "a", 2, Inline("a", 2))),
            "unsorted" => References(Entry("B", "b", 1, Inline("b", 1)), entry),
            "truncated" => References(entry)[..^1],
            "crlf" => References(entry).Replace("\n", "\r\n", StringComparison.Ordinal),
            "bom" => "\uFEFF" + References(entry),
            "unknown-protocol" => References(entry).Replace("references:1", "references:2", StringComparison.Ordinal),
            "unknown-contract" => ReferenceHeader + entry.Replace("|1|1|", "|1|3|", StringComparison.Ordinal),
            "leading-zero" => ReferenceHeader + entry.Replace("|1|1|", "|01|1|", StringComparison.Ordinal),
            "invalid-utf8" => ReferenceHeader + entry.Replace("|QQ==|", "|/w==|", StringComparison.Ordinal),
            "noncanonical-base64" => ReferenceHeader + entry.Replace("|QQ==|", "|QR==|", StringComparison.Ordinal),
            "wrong-id" => References(Entry("A", "a", 1, Inline("different", 1))),
            "wrong-version" => References(Entry("A", "a", 1, Inline("a", 2))),
            "wrong-kind" => References(Entry("A", "a", 1, Owner("a", 1))),
            "old-format" => References(Entry("A", "a", 1, Inline("a", 1).Replace("manifest:9", "manifest:8", StringComparison.Ordinal))),
            "multiple-records" => References(Entry("A", "a", 1, Inline("a", 1) + Inline("b", 1)[ManifestHeader.Length..])),
            "malformed-record" => References(Entry("A", "a", 1, Inline("a", 1).Replace("// schema-end\n", "", StringComparison.Ordinal))),
            "record-crlf" => References(Entry("A", "a", 1, Inline("a", 1).Replace("\n", "\r\n", StringComparison.Ordinal))),
            "blank-owner" => References(Entry(" ", "a", 1, Inline("a", 1))),
            _ => throw new InvalidOperationException(),
        };
        string manifest = files.Write("candidate.cs", WithHash(ManifestHeader, references));
        string refs = files.Write("refs.cs", references);
        foreach (bool verify in new[] { false, true }) {
            Assert.Throws<SchemaHistoryException>(() => Run(verify, new(), manifest, files.History, refs));
        }
        Assert.False(Directory.Exists(files.History));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("arity")]
    [InlineData("cycle")]
    [InlineData("depth")]
    public void ReferenceDependenciesUseExistingExactClosureRules(string variant) {
        using Files files = new();
        string references = variant switch {
            "version" => References(Entry("A", "a", 1, Inline("a", 1, Field("b", 2))), Entry("B", "b", 1, Inline("b", 1))),
            "arity" => References(Entry("A", "a", 1, Inline("a", 1, Field("b", 1))), Entry("B", "b", 1, Inline("b", 1, arity: 1))),
            "cycle" => References(Entry("A", "a", 1, Inline("a", 1, Field("b", 1))), Entry("B", "b", 1, Inline("b", 1, Field("a", 1)))),
            "depth" => References(Enumerable.Range(0, 257).Select(index => {
                string id = "a" + index.ToString("D3");
                return Entry("A", id, 1, Inline(id, 1, index < 256 ? Field("a" + (index + 1).ToString("D3"), 1) : ""));
            }).ToArray()),
            _ => throw new InvalidOperationException(),
        };
        string manifest = files.Write("candidate.cs", WithHash(ManifestHeader, references));
        string refs = files.Write("refs.cs", references);
        foreach (bool verify in new[] { false, true }) {
            SchemaHistoryException exception = Assert.Throws<SchemaHistoryException>(() => Run(verify, new(), manifest, files.History, refs));
            Assert.Contains(variant == "version" ? "missing inline" : variant, exception.Message);
        }
        Assert.False(Directory.Exists(files.History));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurrentCandidateCannotRepairAcceptedOwnedHistory(bool verify) {
        using Files files = new();
        string beforePath = files.Accept(Owner("world", 1, Field("local", 1)));
        byte[] before = File.ReadAllBytes(beforePath);
        string manifest = files.Write("candidate.cs", Inline("local", 1));
        SchemaHistoryException exception = Assert.Throws<SchemaHistoryException>(() => Run(verify, new(), manifest, files.History, null));
        Assert.Contains("missing inline schema 'local' version 1", exception.Message);
        Assert.Equal(before, File.ReadAllBytes(beforePath));
        Assert.Single(Directory.GetFiles(files.History));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ReferencesCannotSupplyAnyVersionOfOwnedDefinition(bool verify, bool ownedByHistory) {
        using Files files = new();
        if (ownedByHistory) files.Accept(Inline("local", 2));
        string references = References(Entry("Foreign", "local", 1, Inline("local", 1)));
        string owned = ownedByHistory ? ManifestHeader : Inline("local", 2);
        string manifest = files.Write("candidate.cs", WithHash(owned, references));
        string refs = files.Write("refs.cs", references);
        SchemaHistoryException exception = Assert.Throws<SchemaHistoryException>(() => Run(verify, new(), manifest, files.History, refs));
        Assert.Contains("owned definition", exception.Message);
        Assert.Equal(ownedByHistory ? 1 : 0, Directory.Exists(files.History) ? Directory.GetFiles(files.History).Length : 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurrentCandidateCannotRepairMissingImportedHistoryDependency(bool verify) {
        using Files files = new();
        files.Accept(Owner("world", 1, Field("a", 1)));
        string references = References(Entry("A", "a", 1, Inline("a", 1, Field("missing", 1))));
        string candidate = Inline("missing", 1);
        string manifest = files.Write("candidate.cs", WithHash(candidate, references));
        string refs = files.Write("refs.cs", references);
        Assert.Contains("missing inline", Assert.Throws<SchemaHistoryException>(() => Run(verify, new(), manifest, files.History, refs)).Message);
        Assert.Single(Directory.GetFiles(files.History));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImportedClosureCannotBorrowConsumerOwnedDefinition(bool verify) {
        using Files files = new();
        files.Accept(Inline("b", 1));
        string references = References(Entry("A", "a", 1, Inline("a", 1, Field("b", 1))));
        string manifest = files.Write("candidate.cs", WithHash(ManifestHeader, references));
        string refs = files.Write("refs.cs", references);
        Assert.Contains("missing inline", Assert.Throws<SchemaHistoryException>(() => Run(verify, new(), manifest, files.History, refs)).Message);
        Assert.Single(Directory.GetFiles(files.History));
    }

    private static SchemaHistoryResult Run(bool verify, SchemaHistoryTool tool, string manifest, string history, string? refs) =>
        verify ? tool.Verify(manifest, history, refs) : tool.Publish(manifest, history, refs);
    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
    private static string WithHash(string owned, string references) => owned + "// references-sha256:" + Hash(references) + "\n";
    private static string References(params string[] entries) => ReferenceHeader + string.Concat(entries);
    private static string Entry(string owner, string id, int version, string manifest) => $"// reference|{B64(owner)}|{B64(id)}|{version}|1|{B64(manifest)}\n";
    private static string Inline(string id, int version, string fields = "", int arity = 0) => Record(id, version, 2, arity, fields);
    private static string Owner(string id, int version, string fields = "") => Record(id, version, 1, 0, fields);
    private static string Record(string id, int version, int kind, int arity, string fields) =>
        ManifestHeader + $"// schema-begin\n// schema-id-base64:{B64(id)}\n// version:{version}\n// kind:{kind}\n// arity:{arity}\n" + fields + "// schema-end\n";
    private static string Field(string id, int version) => $"// field:1|16|n{B64(id)}()|{version}\n";

    private sealed class Files : IDisposable {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "DurableGraph.DB060.Build", Guid.NewGuid().ToString("N"));
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
