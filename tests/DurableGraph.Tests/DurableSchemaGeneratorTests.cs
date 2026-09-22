using System.Reflection;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    private static readonly CSharpParseOptions ParseOptions =
        CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp14);

    [Theory]
    [MemberData(nameof(InvalidSources))]
    public void InvalidDurableDeclarationsFailWithPreciseDiagnostic(
        string expectedDiagnosticId,
        string source) {
        GeneratorTestRun run = RunGenerator(source);

        Diagnostic diagnostic = Assert.Single(
            run.GeneratorDiagnostics,
            candidate => candidate.Id == expectedDiagnosticId);

        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Empty(run.GeneratedSources);
    }

    [Fact]
    public void GeneratedOutputIsIndependentOfTypeDeclarationOrder() {
        const string firstOrder = """
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;

            namespace Samples {
                [DurableType("samples.second", 1)]
                public sealed partial class Second : IDurableObject {
                    [DurableField(1)] private long _value;
                }

                [DurableType("samples.first", 1)]
                public sealed partial class First : IDurableObject {
                    [DurableField(1)] private bool _value;
                }
            }
            """;
        const string secondOrder = """
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;

            namespace Samples {
                [DurableType("samples.first", 1)]
                public sealed partial class First : IDurableObject {
                    [DurableField(1)] private bool _value;
                }

                [DurableType("samples.second", 1)]
                public sealed partial class Second : IDurableObject {
                    [DurableField(1)] private long _value;
                }
            }
            """;

        GeneratorTestRun first = RunGenerator(firstOrder);
        GeneratorTestRun second = RunGenerator(secondOrder);

        foreach (string hintName in new[] {
            "DurableSchemas.g.cs",
            "DurableGraphSchemaHistoryCandidates.g.cs",
            "DurableStates.g.cs",
        }) {
            string firstGenerated = GeneratedSource(first, hintName);
            string secondGenerated = GeneratedSource(second, hintName);
            Assert.Equal(firstGenerated, secondGenerated);
            Assert.DoesNotContain('\r', firstGenerated);
        }
    }

    [Fact]
    public void GeneratedOutputIsIndependentOfFieldDeclarationOrder() {
        string firstOrder = DurableTypeSource(
            "[DurableField(9)] private string _name = string.Empty;\n" +
            "[DurableField(3)] private int _age;");
        string secondOrder = DurableTypeSource(
            "[DurableField(3)] private int _age;\n" +
            "[DurableField(9)] private string _name = string.Empty;");

        GeneratorTestRun first = RunGenerator(firstOrder);
        GeneratorTestRun second = RunGenerator(secondOrder);

        foreach (string hintName in new[] {
            "DurableSchemas.g.cs",
            "DurableGraphSchemaHistoryCandidates.g.cs",
            "DurableStates.g.cs",
        }) {
            string firstGenerated = GeneratedSource(first, hintName);
            string secondGenerated = GeneratedSource(second, hintName);
            Assert.Equal(firstGenerated, secondGenerated);
        }
    }


    [Fact]
    public void CompilationWithoutDurableTypesEmitsAnEmptyCandidateManifest() {
        GeneratorTestRun run = RunGenerator(
            "namespace Samples; internal sealed class PlainType { }");

        Assert.DoesNotContain(run.GeneratorDiagnostics, IsError);
        Assert.Equal(
            "// durable-graph-schema-history-manifest:9\n",
            GeneratedSource(run, "DurableGraphSchemaHistoryCandidates.g.cs"));
        Assert.DoesNotContain(
            run.GeneratedSources,
            source => source.HintName == "DurableSnapshots.g.cs");
    }


    [Fact]
    public void MissingPredecessorHistoryFailsClosed() {
        GeneratorTestRun run = RunGenerator(
            DurableTypeSource(
                "[DurableField(1)] private int _value;",
                durableTypeArguments: "\"samples.example\", 3"),
            SchemaHistory("example-v1.dgschema", "samples.example", 1, (1, 2)));

        Diagnostic diagnostic = Assert.Single(
            run.GeneratorDiagnostics,
            candidate => candidate.Id == "DG0014");
        Assert.Contains("version 2", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.DoesNotContain(
            run.GeneratedSources,
            source => source.HintName == "DurableSnapshots.g.cs");
    }

    [Fact]
    public void MalformedSchemaHistoryFailsClosed() {
        GeneratorTestRun run = RunGenerator(
            DurableTypeSource("[DurableField(1)] private int _value;"),
            new InMemoryAdditionalText(
                "broken.dgschema",
                "// durable-graph-schema-history:1\n// not-schema-history\n"));

        Diagnostic diagnostic = Assert.Single(
            run.GeneratorDiagnostics,
            candidate => candidate.Id == "DG0012");
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("broken.dgschema", diagnostic.Location.GetLineSpan().Path);
    }

    [Fact]
    public void CandidateManifestCannotMasqueradeAsAcceptedHistory() {
        GeneratorTestRun run = RunGenerator(
            DurableTypeSource("[DurableField(1)] private int _value;"),
            new InMemoryAdditionalText(
                "candidate.dgschema",
                "// durable-graph-schema-history-manifest:1\n" +
                "// schema-begin\n" +
                "// schema-id-base64:c2FtcGxlcy5leGFtcGxl\n" +
                "// version:1\n" +
                "// field:1|2\n" +
                "// schema-end\n"));

        Diagnostic diagnostic = Assert.Single(
            run.GeneratorDiagnostics,
            candidate => candidate.Id == "DG0012");
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Theory]
    [InlineData(
        "// durable-graph-snapshot:1\n// schema-begin\n// schema-id-base64:c2FtcGxlcy5leGFtcGxl\n// version:1\n// field:1|2\n// schema-end\n")]
    [InlineData(
        "// durable-graph-schema-history:1\n// snapshot-begin\n// schema-id-base64:c2FtcGxlcy5leGFtcGxl\n// version:1\n// field:1|2\n// snapshot-end\n")]
    public void DgschemaRejectsLegacyHeaderOrRecordMarkers(string legacyContent) {
        GeneratorTestRun run = RunGenerator(
            DurableTypeSource("[DurableField(1)] private int _value;"),
            new InMemoryAdditionalText("legacy.dgschema", legacyContent));

        Diagnostic diagnostic = Assert.Single(
            run.GeneratorDiagnostics,
            candidate => candidate.Id == "DG0012");
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("legacy.dgschema", diagnostic.Location.GetLineSpan().Path);
    }

    [Fact]
    public void ConflictingHistoryForSameSchemaVersionFailsClosed() {
        GeneratorTestRun run = RunGenerator(
            DurableTypeSource(
                "[DurableField(1)] private int _value;",
                durableTypeArguments: "\"samples.example\", 2"),
            SchemaHistory("first.dgschema", "samples.example", 1, (1, 2)),
            SchemaHistory("second.dgschema", "samples.example", 1, (1, 3)));

        Assert.Contains(
            run.GeneratorDiagnostics,
            candidate => candidate.Id == "DG0013");
        Assert.DoesNotContain(
            run.GeneratedSources,
            source => source.HintName == "DurableSnapshots.g.cs");
    }

    [Fact]
    public void CurrentHistoryMustExactlyMatchCurrentFields() {
        GeneratorTestRun run = RunGenerator(
            DurableTypeSource("[DurableField(1)] private int _value;"),
            SchemaHistory("current.dgschema", "samples.example", 1, (1, 3)));

        Diagnostic diagnostic = Assert.Single(
            run.GeneratorDiagnostics,
            candidate => candidate.Id == "DG0015");
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.DoesNotContain(
            run.GeneratedSources,
            source => source.HintName == "DurableSnapshots.g.cs");
    }


    public static TheoryData<string, string> InvalidSources() {
        return new TheoryData<string, string> {
            {
                "DG0001",
                DurableTypeSource(
                    "[Transient] private int _value;",
                    typeDeclaration: "public sealed class Example : IDurableObject")
            },
            {
                "DG0001",
                """
                using Atelia.DurableGraph;
                using Atelia.DurableGraph.Schema;
                using Atelia.DurableGraph.Runtime;

                namespace Atelia.DurableGraph {
                    public static class Lookalikes {
                        public interface IDurableObject { }
                    }
                }

                namespace Samples {
                    [DurableType("samples.example", 1)]
                    public sealed partial class Example
                        : Atelia.DurableGraph.Lookalikes.IDurableObject {
                        [Transient] private int _value;
                    }
                }
                """
            },
            {
                "DG0002",
                DurableTypeSource(
                    "[Transient] private int _value;",
                    durableTypeArguments: "\"   \", 1")
            },
            {
                "DG0002",
                DurableTypeSource(
                    "[Transient] private int _value;",
                    durableTypeArguments: "\"\\uD800\", 1")
            },
            {
                "DG0003",
                DurableTypeSource("private int _value;")
            },
            {
                "DG0003",
                """
                using System;
                using Atelia.DurableGraph;
                using Atelia.DurableGraph.Schema;
                using Atelia.DurableGraph.Runtime;

                namespace Atelia.DurableGraph {
                    public static class Lookalikes {
                        [AttributeUsage(AttributeTargets.Field)]
                        public sealed class DurableFieldAttribute : Attribute {
                            public DurableFieldAttribute(int id) { }
                        }
                    }
                }

                namespace Samples {
                    [DurableType("samples.example", 1)]
                    public sealed partial class Example : IDurableObject {
                        [Atelia.DurableGraph.Lookalikes.DurableField(1)]
                        private int _value;
                    }
                }
                """
            },
            {
                "DG0004",
                DurableTypeSource("[DurableField(1), Transient] private int _value;")
            },
            {
                "DG0005",
                DurableTypeSource("[DurableField(0)] private int _value;")
            },
            {
                "DG0006",
                DurableTypeSource(
                    "[DurableField(1)] private int _first;\n" +
                    "[DurableField(1)] private string _second = string.Empty;")
            },
            {
                "DG0007",
                DurableTypeSource("[DurableField(1)] private System.DateTime _value;")
            },
            {
                "DG0008",
                DurableTypeSource(
                    "[Transient] private int _value;\n" +
                    "public static DurableSchema Schema => throw null!;")
            },
            {
                "DG0008",
                """
                using Atelia.DurableGraph;
                using Atelia.DurableGraph.Schema;
                using Atelia.DurableGraph.Runtime;

                namespace Samples;

                [DurableType("samples.schema", 1)]
                public sealed partial class Schema : IDurableObject {
                    [Transient] private int _value;
                }
                """
            },
            {
                "DG0009",
                DurableTypeSource("[DurableField(1)] private static int _value;")
            },
        };
    }

    private static string DurableTypeSource(
        string members,
        string durableTypeArguments = "\"samples.example\", 1",
        string typeDeclaration = "public sealed partial class Example : IDurableObject") {
        return $$"""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;

            namespace Samples;

            [DurableType({{durableTypeArguments}})]
            {{typeDeclaration}} {
                {{members}}
            }
            """;
    }

    // Only graph persistence witnesses need the test bridge. Schema-only compilations must
    // retain their original reference/import environment (including historical-name probes).
    private static bool NeedsFixtureBridge(string source) =>
        source.Contains("FixtureGraphRepository", StringComparison.Ordinal) ||
        source.Contains("FixtureGraphSession", StringComparison.Ordinal) ||
        source.Contains("FixtureLoadedWorld", StringComparison.Ordinal) ||
        source.Contains("FixturePreparedWorldRevision", StringComparison.Ordinal);

    private static string WithFixtureBridgeImport(string source) => NeedsFixtureBridge(source)
        ? "using Atelia.DurableGraph.Tests;\n" + source : source;

    private static IEnumerable<MetadataReference> FixtureBridgeReferences(string source) =>
        NeedsFixtureBridge(source)
            ? [MetadataReference.CreateFromFile(typeof(FixtureGraphRepository).Assembly.Location)]
            : [];

    private static GeneratorTestRun RunGenerator(
        string source,
        params AdditionalText[] additionalTexts) =>
        RunGenerator(source, additionalTexts, extraGenerators: null, forceDefinitions: null);

    private static GeneratorTestRun RunGenerator(
        string source,
        AdditionalText[] additionalTexts,
        ISourceGenerator[]? extraGenerators,
        string? forceDefinitions) {
        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(WithFixtureBridgeImport(source), ParseOptions);
        CSharpCompilation compilation = CSharpCompilation.Create(
            assemblyName: $"GeneratorTests_{Guid.NewGuid():N}",
            syntaxTrees: [syntaxTree],
            references: PlatformReferences().Concat(FixtureBridgeReferences(source)).Append(
                MetadataReference.CreateFromFile(typeof(IDurableObject).Assembly.Location)).Append(
                MetadataReference.CreateFromFile(typeof(Atelia.DurableGraph.Serialization.BinaryPayloadReader).Assembly.Location)).Append(
                MetadataReference.CreateFromFile(typeof(Atelia.DurableGraph.Persistence.SchemaStore).Assembly.Location)).Append(
                MetadataReference.CreateFromFile(typeof(Atelia.DurableGraph.Storage.ObjectVersionChain).Assembly.Location)),
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));
        ISourceGenerator durableGenerator = new DurableSchemaGenerator().AsSourceGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: extraGenerators is null ? [durableGenerator] : [durableGenerator, .. extraGenerators],
            additionalTexts: additionalTexts,
            parseOptions: ParseOptions,
            optionsProvider: new CrossAssemblyOptions(forceDefinitions));

        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out Compilation outputCompilation,
            out _);
        GeneratorDriverRunResult runResult = driver.GetRunResult();

        return new GeneratorTestRun(
            (CSharpCompilation)outputCompilation,
            runResult.Diagnostics,
            runResult.Results.SelectMany(result => result.GeneratedSources).ToArray());
    }

    private static string GeneratedSource(
        GeneratorTestRun run,
        string hintName) {
        return Assert.Single(
            run.GeneratedSources,
            source => source.HintName == hintName).SourceText.ToString();
    }

    private static AdditionalText SchemaHistory(
        string path,
        string schemaId,
        int version,
        params (int FieldId, int TypeTag)[] fields) {
        string content =
            "// durable-graph-schema-history:1\n" +
            "// schema-begin\n" +
            "// schema-id-base64:" +
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(schemaId)) +
            "\n// version:" + version.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            "\n";

        foreach ((int fieldId, int typeTag) in fields) {
            content += "// field:" +
                fieldId.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                "|" +
                typeTag.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                "\n";
        }

        content += "// schema-end\n";
        return new InMemoryAdditionalText(path, content);
    }

    private static IEnumerable<MetadataReference> PlatformReferences() {
        string trustedAssemblies = Assert.IsType<string>(
            AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"));

        return trustedAssemblies
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
    }

    private static Assembly EmitAndLoad(CSharpCompilation compilation) {
        using MemoryStream assemblyStream = new();
        EmitResult result = compilation.Emit(assemblyStream);

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics));

        return Assembly.Load(assemblyStream.ToArray());
    }

    private static int CountOccurrences(string text, string value) {
        int count = 0;
        int startIndex = 0;

        while ((startIndex = text.IndexOf(
            value,
            startIndex,
            StringComparison.Ordinal)) >= 0) {
            count++;
            startIndex += value.Length;
        }

        return count;
    }

    private static bool IsError(Diagnostic diagnostic) {
        return diagnostic.Severity == DiagnosticSeverity.Error;
    }

    private sealed record GeneratorTestRun(
        CSharpCompilation OutputCompilation,
        IEnumerable<Diagnostic> GeneratorDiagnostics,
        GeneratedSourceResult[] GeneratedSources);

    private sealed class InMemoryAdditionalText : AdditionalText {
        private readonly SourceText _text;

        public InMemoryAdditionalText(string path, string content) {
            Path = path;
            _text = SourceText.From(content);
        }

        public override string Path { get; }

        public override SourceText GetText(
            CancellationToken cancellationToken = default) {
            return _text;
        }
    }
}
