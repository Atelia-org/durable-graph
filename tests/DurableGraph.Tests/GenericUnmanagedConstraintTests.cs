using Atelia.DurableGraph.Schema;
using System.Reflection;
using Atelia.DurableGraph.Persistence;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Fact]
    public void ReflectedManagedArgumentsCannotBypassGeneratedUnmanagedDomainConstraints() {
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("Managed",1)] public partial struct Managed {
                [DurableField(1)] public string? Text;
            }
            [DurableType("Box",1)] public partial class Box<T> : IDurableObject where T:unmanaged {
                [DurableField(1)] public T Value;
            }
            [DurableType("Inline",1)] public partial struct Inline<T> where T:unmanaged {
                [DurableField(1)] public T Value;
            }
            [DurableType("ClassOnly",1)] public partial class ClassOnly<T> : IDurableObject where T:class { }
            [DurableType("NeedsCtor",1)] public partial class NeedsCtor<T> : IDurableObject where T:new() { }
            [DurableType("Related",1)] public partial class Related<T,U> : IDurableObject where U:T { }
            """);
        AssertSchemaOnlyCompiles(run);
        Assembly assembly = EmitAndLoad(run.OutputCompilation);
        StateModelRegistry registry = new();
        assembly.GetType("Atelia.DurableGraph.Generated.DurableDefinitions")!.GetMethod("Register")!
            .Invoke(null, [registry]);
        ModelExecutionEnvironment snapshot = registry.CreateEnvironment();
        Type managed = assembly.GetType("Managed")!;
        Type boxDefinition = assembly.GetType("Box`1")!;
        Type inlineDefinition = assembly.GetType("Inline`1")!;

        // Actual .NET 10 behavior: the CLR accepts these constructed types even though
        // C# source would reject the argument for an unmanaged parameter.
        Type invalidBox = boxDefinition.MakeGenericType(managed);
        Type invalidInline = inlineDefinition.MakeGenericType(managed);
        TypeExpr argument = TypeExpr.Named("Managed");
        Assert.Equal(argument, snapshot.GetTypeExpr(managed));
        foreach (Type invalid in new[] { invalidBox, invalidInline }) {
            Assert.Throws<InvalidDataException>(() => snapshot.GetTypeExpr(invalid));
            Assert.Throws<InvalidDataException>(() => snapshot.ResolveCurrentValue(invalid));
            Assert.Throws<InvalidDataException>(() => snapshot.ResolveCurrentModel(invalid));
        }
        Assert.Throws<InvalidDataException>(() => snapshot.GetDomainType(TypeExpr.Named("Box", argument)));
        Assert.Throws<InvalidDataException>(() => snapshot.GetDomainType(TypeExpr.Named("Inline", argument)));
        Assert.Empty(snapshot.Models);
        Assert.Empty(snapshot.Readers);

        // The guard also applies inside another type argument, before reference binding.
        Type nested = boxDefinition.MakeGenericType(invalidInline);
        Assert.Throws<InvalidDataException>(() => snapshot.ResolveCurrentValue(nested));

        Type validBox = boxDefinition.MakeGenericType(typeof(int));
        Type validInline = inlineDefinition.MakeGenericType(typeof(int));
        Assert.Equal(TypeExpr.Named("Box", TypeExpr.Builtin(TypeTag.Int32)), snapshot.ResolveCurrentModel(validBox).CurrentSchema.Type);
        Assert.Equal(TypeExpr.Named("Inline", TypeExpr.Builtin(TypeTag.Int32)), snapshot.ResolveCurrentValue(validInline).Slot.InlineSchema!.Type);

        // Ordinary CLR constraints continue to be enforced when nominal identities are
        // closed for current-domain restoration.
        Assert.Throws<InvalidDataException>(() => snapshot.GetDomainType(TypeExpr.Named("ClassOnly", TypeExpr.Builtin(TypeTag.Int32))));
        Assert.Throws<InvalidDataException>(() => snapshot.GetDomainType(TypeExpr.Named("NeedsCtor", TypeExpr.Builtin(TypeTag.String))));
        Assert.Throws<InvalidDataException>(() => snapshot.GetDomainType(TypeExpr.Named("Related", TypeExpr.Builtin(TypeTag.String), TypeExpr.Builtin(TypeTag.Int32))));
    }
}
