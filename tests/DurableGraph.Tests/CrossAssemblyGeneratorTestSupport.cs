using Atelia.Binary;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Atelia.DurableGraph.Generator;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    private static GeneratorTestRun RunCrossAssemblyGenerator(
        string source, IEnumerable<MetadataReference>? extraReferences = null,
        AdditionalText[]? history = null, string? forceDefinitions = null, string? assemblyName = null) {
        CSharpCompilation compilation = CSharpCompilation.Create(
            assemblyName ?? $"CrossAssembly_{Guid.NewGuid():N}",
            [CSharpSyntaxTree.ParseText(WithFixtureBridgeImport(source), ParseOptions)],
            PlatformReferences().Concat(new[] {
                typeof(IDurableObject).Assembly,
                typeof(global::Atelia.Binary.BareValueReader).Assembly,
                typeof(Atelia.FrameStore.FrameAddress).Assembly,
                typeof(AteliaResult<>).Assembly,
                typeof(Serialization.PreparedBaseBody).Assembly,
                typeof(global::Atelia.DurableGraph.Repository).Assembly,
                typeof(Storage.ObjectVersionChain).Assembly,
            }.Select(assembly => MetadataReference.CreateFromFile(assembly.Location)))
                .Concat(FixtureBridgeReferences(source))
                .Concat(extraReferences ?? []),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [new DurableSchemaGenerator().AsSourceGenerator()],
            additionalTexts: history ?? [], parseOptions: ParseOptions,
            optionsProvider: new CrossAssemblyOptions(forceDefinitions));
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation output, out _);
        GeneratorDriverRunResult result = driver.GetRunResult();
        return new GeneratorTestRun((CSharpCompilation)output, result.Diagnostics,
            result.Results.SelectMany(item => item.GeneratedSources).ToArray());
    }

    private static (MetadataReference Reference, byte[] Image) EmitCrossAssemblyReference(GeneratorTestRun run) {
        AssertSchemaOnlyCompiles(run);
        using MemoryStream stream = new();
        var result = run.OutputCompilation.Emit(stream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        byte[] image = stream.ToArray();
        return (MetadataReference.CreateFromImage(ImmutableArray.CreateRange(image)), image);
    }

    private sealed class CrossAssemblyOptions(string? value) : AnalyzerConfigOptionsProvider {
        private readonly AnalyzerConfigOptions global = new CrossAssemblyOptionValues(value);
        private readonly AnalyzerConfigOptions empty = new CrossAssemblyOptionValues(null);
        public override AnalyzerConfigOptions GlobalOptions => global;
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => empty;
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => empty;
    }

    private sealed class CrossAssemblyOptionValues(string? value) : AnalyzerConfigOptions {
        public override bool TryGetValue(string key, out string result) {
            result = value ?? string.Empty;
            return value is not null && key == "build_property.DurableGraphGenerateDefinitions";
        }
    }
}
