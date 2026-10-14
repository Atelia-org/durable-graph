using Atelia.DurableGraph.Runtime;
namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Theory]
    [InlineData("public partial record Value : IDurableObject;")]
    [InlineData("public sealed partial record class Value([field: DurableField(1)] int X) : IDurableObject;")]
    [InlineData("public abstract partial record Value([field: DurableField(1)] int X) : IDurableObject;")]
    [InlineData("internal partial record Value<T>([field: DurableField(1)] T X) : IDurableObject where T : struct;")]
    [InlineData("public partial record Value : IDurableObject { [DurableField(1)] private readonly int _x; }")]
    [InlineData("public partial record Value : IDurableObject { [field: DurableField(1)] public int X { get; init; } }")]
    [InlineData("public partial record Value : IDurableObject { [field: DurableField(1)] public int X { get => field; set => field = value; } }")]
    [InlineData("public partial record Value : IDurableObject { [field: Transient] public int Scratch { get; set; } public int Computed => throw new System.Exception(); }")]
    [InlineData("public partial record Value(int X) : IDurableObject { [DurableField(1)] public int X = X; }")]
    [InlineData("public partial record Value(int X) : IDurableObject { [field: DurableField(1)] public int X { get; init; } = X; }")]
    public void RecordClassSupportedStorageShapesCompile(string declaration) {
        GeneratorTestRun run = RunGenerator("using Atelia.DurableGraph; [DurableType(\"record.class\", 1)] " + declaration);
        AssertSchemaOnlyCompiles(run);
        _ = EmitAndLoad(run.OutputCompilation);
    }

    [Theory]
    [InlineData("public partial record Value(int X) : IDurableObject;", "DG0003")]
    [InlineData("public partial record Value : IDurableObject { public int X { get; init; } }", "DG0003")]
    [InlineData("public partial record Value([field: DurableField(1), Transient] int X) : IDurableObject;", "DG0004")]
    [InlineData("public partial record Value([field: DurableField(0)] int X) : IDurableObject;", "DG0005")]
    [InlineData("public partial record Value([field: DurableField(1)] int X, [field: DurableField(1)] int Y) : IDurableObject;", "DG0006")]
    [InlineData("public partial record Value : IDurableObject { [field: DurableField(1)] public static int X { get; set; } }", "DG0009")]
    [InlineData("public record Value : IDurableObject;", "DG0001")]
    [InlineData("public partial record Value;", "DG0001")]
    [InlineData("public partial record Value : IDurableObject { [DurableField(1)] public IDurableObject? Any; }", "DG0007")]
    public void RecordClassStorageAndQualificationFailuresAreDiagnosed(string declaration, string diagnosticId) {
        GeneratorTestRun run = RunGenerator("using Atelia.DurableGraph; [DurableType(\"record.class\", 1)] " + declaration);
        Assert.Contains(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == diagnosticId && IsError(diagnostic));
    }

    [Theory]
    [InlineData("[field: DurableField(1)] public int Computed => 42;")]
    [InlineData("[field: Transient] public int Computed => 42;")]
    [InlineData("public event System.Action? Changed;")]
    [InlineData("private static void __DurableReadonly_Family_78_1() { } [field: DurableField(1)] public int Number { get; init; }")]
    public void RecordClassCannotLoseIgnoredStorageOrCollideWithGeneratedAccessors(string member) {
        GeneratorTestRun run = RunGenerator("using Atelia.DurableGraph; [DurableType(\"x\",1)] public partial record X : IDurableObject { " + member + " }");
        Assert.Contains(run.GeneratorDiagnostics, IsError);
    }

    [Theory]
    [InlineData("[field: DurableField(1)] ")]
    [InlineData("[field: Transient] ")]
    public void RecordClassInheritedPositionalParameterCannotPretendToDeclareNewStorage(string ignoredAttribute) {
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("base",1)] public abstract partial record Base([field:DurableField(1)] int X) : IDurableObject;
            [DurableType("leaf",1)] public sealed partial record Leaf(
            """ + ignoredAttribute + "int X) : Base(X);");
        Assert.Contains(run.GeneratorDiagnostics, IsError);
    }

    [Fact]
    public void RecordClassCannotSkipUnmarkedIntermediateAncestor() {
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("base",1)] public abstract partial record Base([field:DurableField(1)] int X) : IDurableObject;
            public abstract record Middle(int X) : Base(X);
            [DurableType("leaf",1)] public sealed partial record Leaf(int X) : Middle(X);
            """);
        Assert.Contains(run.GeneratorDiagnostics, diagnostic => diagnostic.Id == "DG0019");
    }

    [Fact]
    public void RecordClassPropertyShadowingKeepsBothActualDeclarationLayers() {
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("base",1)] public abstract partial record Base([field:DurableField(1)] int Number) : IDurableObject;
            [DurableType("leaf",1)] public sealed partial record Leaf(int Number) : Base(Number) {
                [field:DurableField(1)] public new int Number { get; init; } = Number + 10;
            }
            """);
        AssertSchemaOnlyCompiles(run);
        var assembly = EmitAndLoad(run.OutputCompilation);
        var snapshot = EnumHistoryRegistry(assembly).CreateEnvironment();
        var schema = snapshot.ResolveCurrentModel(assembly.GetType("Leaf")!).CurrentSchema;
        Assert.Single(schema.Fields);
        Assert.Single(schema.BaseSchema!.Fields);
        Assert.Null(schema.BaseSchema.BaseSchema);
        IDurableObject value = (IDurableObject)Activator.CreateInstance(assembly.GetType("Leaf")!, 3)!;
        using var capture = new CaptureSession().BeginCapture(snapshot);
        snapshot.ResolveCurrentModel(value.GetType()).AddRoot(capture, value);
        ObjectStateRecord row = Assert.Single(capture.Seal().Objects);
        // Base.Number=3 and the independently declared Leaf.Number=13 both survive in declaration order.
        Assert.Equal(new byte[] { 6, 26 }, row.Preparation!.PrepareBase(row).Body.ToArray());
    }
}
