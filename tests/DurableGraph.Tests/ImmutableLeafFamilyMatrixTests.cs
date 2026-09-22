using System;
using System.Reflection;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Runtime;

namespace Atelia.DurableGraph.Tests;

// DB-075 §7 acceptance matrix for family-routed qualification, mixed compilations, and the
// implicitly-declared-state negatives that route through the family path. Every case asserts
// the delivered binding capability (actual StateModelBinding.IsImmutableLeaf); family routing
// is a generation path, never a model qualification.
public sealed partial class DurableSchemaGeneratorTests {
    private const string FamilyHostSource = """
        public static class Host {
            public static StateModelRegistry Models() {
                var models = new StateModelRegistry();
                global::Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
                return models;
            }
        }
        """;

    private static StateModelBinding RunFamilyBinding(string source, string typeName) {
        GeneratorTestRun run = RunGenerator(source);
        Assert.DoesNotContain(run.GeneratorDiagnostics, IsError);
        return ResolveFamilyBinding(EmitAndLoad(run.OutputCompilation), typeName);
    }

    private static StateModelBinding RunFamilyBindingForced(string source, string typeName) {
        GeneratorTestRun run = RunGenerator(source, [], null, "true");
        Assert.DoesNotContain(run.GeneratorDiagnostics, IsError);
        return ResolveFamilyBinding(EmitAndLoad(run.OutputCompilation), typeName);
    }

    private static StateModelBinding ResolveFamilyBinding(Assembly assembly, string typeName) {
        StateModelRegistry models = assembly.GetType("Host")!
            .GetMethod("Models")!.CreateDelegate<Func<StateModelRegistry>>()();
        return models.Snapshot().ResolveCurrentModel(assembly.GetType(typeName)!);
    }

    [Fact]
    public void FamilyEnumFieldClaimsImmutableLeaf() {
        // The enum routes this compilation through the family path; the runtime check is the
        // trusted field type identity (enums cannot gain partial state).
        Assert.True(RunFamilyBinding("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Persistence;
            using Atelia.DurableGraph.Runtime;
            [DurableType("family.enum.choice", 1)]
            public enum Choice : byte {
                One = 1
            }
            [DurableType("family.enum.leaf", 1)]
            public partial class EnumLeaf : IDurableObject {
                [DurableField(1)] public readonly Choice Choice;
            }
            """ + FamilyHostSource, "EnumLeaf").IsImmutableLeaf);
    }

    [Fact]
    public void FamilyNullableBuiltinFieldClaimsImmutableLeaf() {
        Assert.True(RunFamilyBindingForced("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Persistence;
            using Atelia.DurableGraph.Runtime;
            [DurableType("family.nullable.builtin.leaf", 1)]
            public partial class NullableBuiltinLeaf : IDurableObject {
                [DurableField(1)] public readonly int? Value;
            }
            """ + FamilyHostSource, "NullableBuiltinLeaf").IsImmutableLeaf);
    }

    [Fact]
    public void FamilyNullableInlineStructFieldClaimsImmutableLeaf() {
        Assert.True(RunFamilyBindingForced("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Persistence;
            using Atelia.DurableGraph.Runtime;
            [DurableType("family.nullable.inline.value", 1)]
            public readonly partial struct InlineValue {
                [DurableField(1)] public readonly int Number;
            }
            [DurableType("family.nullable.inline.leaf", 1)]
            public partial class NullableInlineLeaf : IDurableObject {
                [DurableField(1)] public readonly InlineValue? Value;
            }
            """ + FamilyHostSource, "NullableInlineLeaf").IsImmutableLeaf);
    }

    [Fact]
    public void FamilyNullableEnumFieldClaimsImmutableLeaf() {
        // The enum routes this compilation through the family path.
        Assert.True(RunFamilyBinding("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Persistence;
            using Atelia.DurableGraph.Runtime;
            [DurableType("family.nullable.enum.choice", 1)]
            public enum Choice : byte {
                One = 1
            }
            [DurableType("family.nullable.enum.leaf", 1)]
            public partial class NullableEnumLeaf : IDurableObject {
                [DurableField(1)] public readonly Choice? Maybe;
            }
            """ + FamilyHostSource, "NullableEnumLeaf").IsImmutableLeaf);
    }

    [Fact]
    public void FamilyRecursiveInlineStructFieldClaimsImmutableLeaf() {
        Assert.True(RunFamilyBindingForced("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Persistence;
            using Atelia.DurableGraph.Runtime;
            [DurableType("family.recursive.inner", 1)]
            public readonly partial struct InnerNode {
                [DurableField(1)] public readonly int Value;
            }
            [DurableType("family.recursive.outer", 1)]
            public readonly partial struct OuterNode {
                [DurableField(1)] public readonly InnerNode Inner;
                [DurableField(2)] public readonly int Sum;
            }
            [DurableType("family.recursive.leaf", 1)]
            public partial class RecursiveInlineLeaf : IDurableObject {
                [DurableField(1)] public readonly OuterNode Node;
            }
            """ + FamilyHostSource, "RecursiveInlineLeaf").IsImmutableLeaf);
    }

    [Fact]
    public void FamilyRecordExplicitReadonlyFieldClaimsImmutableLeaf() {
        // Record classes route through the family path naturally; only explicit readonly
        // fields qualify (DB-075 excludes every implicitly declared record backing).
        Assert.True(RunFamilyBinding("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Persistence;
            using Atelia.DurableGraph.Runtime;
            [DurableType("family.record.leaf", 1)]
            public partial record class RecordLeaf : IDurableObject {
                [DurableField(1)] public readonly int Value;
            }
            """ + FamilyHostSource, "RecordLeaf").IsImmutableLeaf);
    }

    [Fact]
    public void FamilyPrimaryConstructorFieldInitializerClaimsImmutableLeaf() {
        // The parameter is consumed only by the explicit readonly field initializer; the
        // family path must qualify the type by its audited fields like the binary path.
        Assert.True(RunFamilyBindingForced("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Persistence;
            using Atelia.DurableGraph.Runtime;
            [DurableType("family.primary.initializer.leaf", 1)]
            public partial class PrimaryInitializerLeaf(int value) : IDurableObject {
                [DurableField(1)] public readonly int Value = value;
            }
            """ + FamilyHostSource, "PrimaryInitializerLeaf").IsImmutableLeaf);
    }

    [Fact]
    public void MixedContainerCompilationClaimsOnlyScalarLeafImmutableLeaf() {
        // The List<int> container routes the whole compilation through the family path; the
        // scalar leaf still qualifies by its own shape while the container owner does not.
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Persistence;
            using Atelia.DurableGraph.Runtime;
            [DurableType("family.mixed.container", 1)]
            public partial class ContainerWorld : IDurableObject {
                [DurableField(1)] public readonly global::System.Collections.Generic.List<int> Items;
            }
            [DurableType("family.mixed.leaf", 1)]
            public partial class MixedScalarLeaf : IDurableObject {
                [DurableField(1)] public readonly int Value;
            }
            """ + FamilyHostSource);
        Assert.DoesNotContain(run.GeneratorDiagnostics, IsError);
        Assembly assembly = EmitAndLoad(run.OutputCompilation);
        StateModelRegistry models = assembly.GetType("Host")!
            .GetMethod("Models")!.CreateDelegate<Func<StateModelRegistry>>()();
        var snapshot = models.Snapshot();
        Assert.True(snapshot.ResolveCurrentModel(assembly.GetType("MixedScalarLeaf")!).IsImmutableLeaf);
        Assert.False(snapshot.ResolveCurrentModel(assembly.GetType("ContainerWorld")!).IsImmutableLeaf);
    }

    [Fact]
    public void PositionalRecordBackingExcludesImmutableLeaf() {
        // [field: DurableField] on a positional parameter targets the compiler-generated
        // backing storage; every implicitly declared backing is excluded, records included.
        Assert.False(RunFamilyBinding("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Persistence;
            using Atelia.DurableGraph.Runtime;
            [DurableType("family.neg.record.positional", 1)]
            public partial record class PositionalRecordLeaf([field: DurableField(1)] int Value) : IDurableObject;
            """ + FamilyHostSource, "PositionalRecordLeaf").IsImmutableLeaf);
    }

    [Fact]
    public void ClosedGenericBaseExcludesImmutableLeaf() {
        // Closed generic bases stay outside the audited closure (fail closed), so the derived
        // leaf never qualifies even though its own fields are readonly scalars.
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Persistence;
            using Atelia.DurableGraph.Runtime;
            [DurableType("family.neg.gbase.part", 1)]
            public partial struct Part<T> {
                [DurableField(1)] public T Item;
            }
            [DurableType("family.neg.gbase.base", 1)]
            public partial class GenericBase<T> : IDurableObject {
                [DurableField(1)] public Part<T> Part;
            }
            [DurableType("family.neg.gbase.derived", 1)]
            public partial class DerivedFromGeneric : GenericBase<int> {
                [DurableField(2)] public readonly int Value;
            }
            """ + FamilyHostSource);
        Assert.DoesNotContain(run.GeneratorDiagnostics, IsError);
        Assembly assembly = EmitAndLoad(run.OutputCompilation);
        StateModelRegistry models = assembly.GetType("Host")!
            .GetMethod("Models")!.CreateDelegate<Func<StateModelRegistry>>()();
        var snapshot = models.Snapshot();
        Assert.False(snapshot.ResolveCurrentModel(assembly.GetType("DerivedFromGeneric")!).IsImmutableLeaf);
        Assert.False(snapshot.ResolveCurrentModel(assembly.GetType("GenericBase`1")!.MakeGenericType(typeof(int))).IsImmutableLeaf);
    }
}
