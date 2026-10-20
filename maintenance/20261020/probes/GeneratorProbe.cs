using Microsoft.CodeAnalysis;

namespace Atelia.DurableGraph.Tests;

// Temporary observation probes. These assert the audit baseline's defects,
// not the desired post-fix contract. Copy into the test project only for the run.
public sealed partial class DurableSchemaGeneratorTests {
    [Fact]
    public void Maintenance20261020DomainParameterShadowProducesConstraintErrors() {
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            [DurableType("audit.shadow", 1)]
            public partial class Box<TState0> : IDurableObject where TState0 : class {
                [DurableField(1)] public TState0 Value = default!;
            }
            """);
        Assert.DoesNotContain(run.GeneratorDiagnostics, IsError);
        string generated = GeneratedSource(run, "DurableGenericStates.g.cs");
        Assert.Contains("__DurableCreateTyped<TState0, TOps0, TProjection0>", generated);
        Diagnostic[] errors = run.OutputCompilation.GetDiagnostics().Where(IsError).ToArray();
        Console.WriteLine(string.Join(Environment.NewLine, errors.Select(error => error.ToString())));
        Assert.Contains(errors, error => error.Id == "CS0452");
    }

    [Fact]
    public void Maintenance20261020OrdinaryFamilyHelperCollisionLacksDgDiagnostic() {
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            [DurableType("audit.helper", 1)]
            public partial class Model<T> : IDurableObject {
                public static int __DurableCapture => 0;
            }
            """);
        Assert.DoesNotContain(run.GeneratorDiagnostics, IsError);
        GeneratedSource(run, "DurableGenericStates.g.cs");
        Diagnostic[] errors = run.OutputCompilation.GetDiagnostics().Where(IsError).ToArray();
        Console.WriteLine(string.Join(Environment.NewLine, errors.Select(error => error.ToString())));
        Assert.Contains(errors, error => error.Id == "CS0102");
    }

    [Fact]
    public void Maintenance20261020FamilyMissingHistoryDiagnosticsScaleWithDeclaredVersion() {
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            [DurableType("audit.history", 32)]
            public partial class Box<T> : IDurableObject { }
            """);
        int missing = run.GeneratorDiagnostics.Count(diagnostic => diagnostic.Id == "DG0014");
        Console.WriteLine("Family missing-version diagnostic count: " + missing);
        Assert.Equal(31, missing);
        Assert.DoesNotContain(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "CS8785");
    }
}
