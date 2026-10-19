using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Atelia.DurableGraph.Tests;

public sealed class RepositoryPublicApiTests {
    [Fact]
    public void ExplicitEditingEntrypointsCompileAgainstPublicAssemblies() {
        const string source = """
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Persistence;

            public static class Consumer {
                public static void Use(string path, StateModelRegistry models, IDurableObject state,
                    IDurableObject domainEvent, CheckpointAddress source) {
                    using var created = Repository.CreateNew(path, models,
                        rotationThresholdBytes: 4096, readCacheBudgetBytes: 0);
                    using var initialState = created.CreateSharedBranch("main", state);
                    using var initialEvent = created.CreateSharedBranchFromEvent("events", domainEvent);
                    using var shared = created.CheckoutShared("shared");
                    using var fork = created.ForkShared("fork", source);
                    CheckpointAddress reference = created.CreateBranch("reference", source);
                    shared.CommitState();
                    shared.CommitState(state);
                    shared.CommitEvent(domainEvent);
                    using var writable = Repository.OpenExisting(path, models,
                        rotationThresholdBytes: 4096, readCacheBudgetBytes: 0);
                    using var readOnly = Repository.OpenReadOnlyExisting(path, models, readCacheBudgetBytes: 0);
                }
            }
            """;

        using MemoryStream image = new();
        var result = Compile(source).Emit(image);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
    }

    [Theory]
    [InlineData("repository.[|Checkout|](\"main\");", "CS1061", "Checkout")]
    [InlineData("repository.[|Fork|](\"fork\", source);", "CS1061", "Fork")]
    [InlineData("repository.[|CreateBranchFromEvent|](\"events\", state);", "CS1061", "CreateBranchFromEvent")]
    [InlineData("_ = repository.[|SchemaWriteMode|];", "CS1061", "SchemaWriteMode")]
    [InlineData("_ = Repository.CreateNew(path, models, [|schemaWriteMode|]: default);", "CS1739", "schemaWriteMode")]
    [InlineData("_ = Repository.OpenExisting(path, models, [|schemaWriteMode|]: default);", "CS1739", "schemaWriteMode")]
    [InlineData("_ = typeof(global::Atelia.DurableGraph.[|SchemaWriteMode|]);", "CS0234", "SchemaWriteMode")]
    [InlineData("repository.CreateBranch(\"main\", [|state|]);", "CS1503", "CheckpointAddress")]
    [InlineData("repository.[|CheckoutPrivate|](\"main\");", "CS1061", "CheckoutPrivate")]
    [InlineData("repository.CheckoutShared(\"main\").[|CommitStateToShared|]();", "CS1061", "CommitStateToShared")]
    [InlineData("repository.CheckoutShared(\"main\").[|CommitStateToShared|](state);", "CS1061", "CommitStateToShared")]
    [InlineData("repository.CheckoutShared(\"main\").[|CommitEventToShared|](state);", "CS1061", "CommitEventToShared")]
    public void RemovedImplicitSelectionRequiresSpecificCompileError(
        string markedStatement, string expectedCode, string expectedTarget) {
        const string prefix = """
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Persistence;

            public static class Consumer {
                public static void Use(Repository repository, string path, StateModelRegistry models,
                    IDurableObject state, CheckpointAddress source) {

            """;
        const string suffix = "\n    }\n}\n";
        int markerStart = markedStatement.IndexOf("[|", StringComparison.Ordinal);
        int markerEnd = markedStatement.IndexOf("|]", StringComparison.Ordinal);
        Assert.True(markerStart >= 0 && markerEnd > markerStart);
        string statement = markedStatement.Remove(markerEnd, 2).Remove(markerStart, 2);
        string source = prefix + statement + suffix;
        TextSpan expectedSpan = new(prefix.Length + markerStart, markerEnd - markerStart - 2);

        Diagnostic error = Assert.Single(Compile(source).GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.Equal(expectedCode, error.Id);
        Assert.True(error.Location.IsInSource);
        Assert.Equal("Consumer.cs", error.Location.GetLineSpan().Path);
        Assert.Equal(expectedSpan, error.Location.SourceSpan);
        Assert.Equal(statement.Substring(markerStart, expectedSpan.Length),
            error.Location.SourceTree!.GetText().ToString(error.Location.SourceSpan));
        Assert.Contains(expectedTarget, error.GetMessage(CultureInfo.InvariantCulture));
    }

    private static CSharpCompilation Compile(string source) {
        string platform = Assert.IsType<string>(AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"));
        IEnumerable<MetadataReference> references = platform.Split(Path.PathSeparator)
            .Append(typeof(IDurableObject).Assembly.Location)
            .Append(typeof(Repository).Assembly.Location)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => MetadataReference.CreateFromFile(path));
        // This consumer has no friend access and runs no generator or test fixture bridge.
        return CSharpCompilation.Create($"RepositoryPublicApiConsumer_{Guid.NewGuid():N}",
            [CSharpSyntaxTree.ParseText(source, path: "Consumer.cs")], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));
    }
}
