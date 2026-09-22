using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Runtime;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    // DB-075 §2.1/§7: another source generator running in the same driver pass can add
    // instance state that the durable generator never sees as input. These witnesses keep
    // the sibling output out of DG's input (same-pass execution, never merged into the
    // source beforehand) and assert the delivered binding capability, not generated text.

    private const string BinaryLeafSource = """
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Runtime;
        [DurableType("sibling.binary.leaf", 1)]
        public partial class SiblingLeaf : IDurableObject {
            [DurableField(1)] public readonly int Value;
        }
        public static class Host {
            public static StateModelBinding Model => SiblingLeaf.__DurableState.Model;
        }
        """;

    private const string FamilyLeafSource = """
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Persistence;
        using Atelia.DurableGraph.Runtime;
        [DurableType("sibling.family.leaf", 1)]
        public partial class SiblingLeaf : IDurableObject {
            [DurableField(1)] public readonly int Value;
        }
        public static class Host {
            public static StateModelRegistry Models() {
                var models = new StateModelRegistry();
                global::Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
                return models;
            }
        }
        """;

    private const string BinaryChainSource = """
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Runtime;
        [DurableType("sibling.chain.base", 1)]
        public partial class SiblingChainBase : IDurableObject {
            [DurableField(1)] public readonly int BaseValue;
        }
        [DurableType("sibling.chain.derived", 1)]
        public partial class SiblingChainDerived : SiblingChainBase {
            [DurableField(2)] public readonly int Value;
        }
        public static class Host {
            public static StateModelBinding Base => SiblingChainBase.__DurableState.Model;
            public static StateModelBinding Derived => SiblingChainDerived.__DurableState.Model;
        }
        """;

    private const string BinaryInlineSource = """
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Runtime;
        [DurableType("sibling.inline.value", 1)]
        public readonly partial struct SiblingValue {
            [DurableField(1)] public readonly int Number;
        }
        [DurableType("sibling.inline.world", 1)]
        public partial class SiblingWorld : IDurableObject {
            [DurableField(1)] public readonly SiblingValue Value;
        }
        public static class Host {
            public static StateModelBinding Model => SiblingWorld.__DurableState.Model;
        }
        """;

    private const string LocalShadowSource = """
        using System;
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Runtime;
        namespace LocalShadow {
            [DurableType("sibling.shadow.local", 1)]
            public partial class LocalShadowLeaf : IDurableObject {
                [DurableField(1)] public readonly Guid Value;
            }
            public static class Host {
                public static StateModelBinding Model => LocalShadowLeaf.__DurableState.Model;
            }
        }
        """;

    private const string LocalShadowSiblingSource = """
        namespace LocalShadow {
            public struct Guid {
                public int[] Payload;
                public static implicit operator global::System.Guid(Guid value) => default;
                public static implicit operator Guid(global::System.Guid value) => default;
            }
        }
        """;

    private const string SystemShadowSource = """
        using System;
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Runtime;
        [DurableType("sibling.shadow.system", 1)]
        public partial class SystemShadowLeaf : IDurableObject {
            [DurableField(1)] public readonly Guid Value;
        }
        public static class Host {
            public static StateModelBinding Model => SystemShadowLeaf.__DurableState.Model;
        }
        """;

    private const string SystemShadowSiblingSource = """
        namespace System {
            public struct Guid {
                public int[] Payload;
            }
        }
        """;

    private const string EmptyLeafSource = """
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Runtime;
        [DurableType("sibling.empty", 1)]
        public partial class EmptySiblingLeaf : IDurableObject {
        }
        public static class Host {
            public static StateModelBinding Model => EmptySiblingLeaf.__DurableState.Model;
        }
        """;

    [Fact]
    public void BinaryLeafWithoutSiblingKeepsImmutableLeaf() {
        Assert.True(RunBinaryBindingWithSibling(BinaryLeafSource, null).IsImmutableLeaf);
    }

    [Fact]
    public void BinarySiblingMutableStateLosesImmutableLeaf() {
        Assert.False(RunBinaryBindingWithSibling(
            BinaryLeafSource, "public partial class SiblingLeaf { public int Counter { get; set; } }").IsImmutableLeaf);
    }

    [Fact]
    public void BinarySiblingReadonlyFieldLosesImmutableLeaf() {
        // Even non-mutable state added outside the audited pipeline removes the capability:
        // it never entered the generated capture path for this version.
        Assert.False(RunBinaryBindingWithSibling(
            BinaryLeafSource, "public partial class SiblingLeaf { public readonly int SiblingExtra; }").IsImmutableLeaf);
    }

    [Fact]
    public void BinarySiblingStaticMembersKeepImmutableLeaf() {
        Assert.True(RunBinaryBindingWithSibling(
            BinaryLeafSource, "public partial class SiblingLeaf { public static int Cache; public static int Compute() => Cache; }").IsImmutableLeaf);
    }

    [Fact]
    public void ForcedFamilyLeafWithoutSiblingClaimsImmutableLeaf() {
        Assert.True(RunFamilyBindingWithSibling(FamilyLeafSource, null, "SiblingLeaf").IsImmutableLeaf);
    }

    [Fact]
    public void ForcedFamilySiblingMutableStateLosesImmutableLeaf() {
        Assert.False(RunFamilyBindingWithSibling(
            FamilyLeafSource, "public partial class SiblingLeaf { public int Counter { get; set; } }", "SiblingLeaf").IsImmutableLeaf);
    }

    [Fact]
    public void BinarySiblingAncestorStateLosesImmutableLeafForWholeChain() {
        GeneratorTestRun run = RunGenerator(
            BinaryChainSource, [],
            [new SiblingSourceGenerator("public partial class SiblingChainBase { public int SiblingState; }").AsSourceGenerator()], null);
        Assert.DoesNotContain(run.GeneratorDiagnostics, IsError);
        Type host = EmitAndLoad(run.OutputCompilation).GetType("Host")!;
        Assert.False(((StateModelBinding)host.GetProperty("Base")!.GetValue(null)!).IsImmutableLeaf);
        Assert.False(((StateModelBinding)host.GetProperty("Derived")!.GetValue(null)!).IsImmutableLeaf);
    }

    [Fact]
    public void BinarySiblingInlineStructStateLosesImmutableLeaf() {
        Assert.False(RunBinaryBindingWithSibling(
            BinaryInlineSource, "public readonly partial struct SiblingValue { public readonly int Extra; }").IsImmutableLeaf);
    }

    [Fact]
    public void BinarySiblingNewBaseClassLosesImmutableLeaf() {
        Assert.False(RunBinaryBindingWithSibling(BinaryLeafSource.Replace("SiblingLeaf", "SiblingRoot"), """
            using Atelia.DurableGraph;
            public class SiblingIntermediate : IDurableObject {
                public int SiblingState;
            }
            public partial class SiblingRoot : SiblingIntermediate { }
            """).IsImmutableLeaf);
    }

    [Fact]
    public void LocalTypeShadowLosesImmutableLeaf() {
        // The audited builtin was the real corelib Guid; the compiled field rebinds to the
        // sibling's local Guid with the same field count and base, so only the trusted type
        // identity check rejects it.
        Assert.False(RunBinaryBindingWithSibling(LocalShadowSource, LocalShadowSiblingSource).IsImmutableLeaf);
    }

    [Fact]
    public void SystemTypeShadowFailsAtCompilationBeforeAnyBinding() {
        // DB-075 C4: a full-name System.Guid shadow rebinds every global::System.Guid in the
        // generated DTO as well. In this fixture the shadow name itself is reachable through
        // global::System.Guid, so the real corelib Guid can no longer be named in source and
        // no conversion between the two same-named types can be declared; the reader/writer
        // APIs still return the corelib type, so the model assembly is rejected at
        // compilation (rebound DTO/reader type mismatches plus the unmanaged state
        // constraint). This is a compile-stage failure, not a runtime guard result; the
        // guard's rejection of a rebound builtin field type (kept emit-legal via implicit
        // conversions) is witnessed by LocalTypeShadowLosesImmutableLeaf.
        GeneratorTestRun run = RunGenerator(SystemShadowSource, [],
            [new SiblingSourceGenerator(SystemShadowSiblingSource).AsSourceGenerator()], null);
        Assert.DoesNotContain(run.GeneratorDiagnostics, IsError);
        EmitResult result = run.OutputCompilation.Emit(Stream.Null);
        Assert.False(result.Success);
        // CS0436 alone only proves the shadow exists; pin the expected rejection to the
        // unmanaged-state constraint and the rebound DTO/reader type mismatches.
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "CS0436");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "CS8377");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id is "CS1503" or "CS0029" or "CS0019");
    }

    [Fact]
    public void EmptyLeafWithSiblingStateLosesImmutableLeaf() {
        Assert.False(RunBinaryBindingWithSibling(
            EmptyLeafSource, "public partial class EmptySiblingLeaf { public int State; }").IsImmutableLeaf);
    }

    private static StateModelBinding RunBinaryBindingWithSibling(string source, string? siblingSource) {
        GeneratorTestRun run = siblingSource is null
            ? RunGenerator(source)
            : RunGenerator(source, [], [new SiblingSourceGenerator(siblingSource).AsSourceGenerator()], null);
        Assert.DoesNotContain(run.GeneratorDiagnostics, IsError);
        return ResolveSingleBinaryReferenceModelBinding(EmitAndLoad(run.OutputCompilation));
    }

    private static StateModelBinding RunFamilyBindingWithSibling(string source, string? siblingSource, string typeName) {
        GeneratorTestRun run = siblingSource is null
            ? RunGenerator(source, [], null, "true")
            : RunGenerator(source, [], [new SiblingSourceGenerator(siblingSource).AsSourceGenerator()], "true");
        Assert.DoesNotContain(run.GeneratorDiagnostics, IsError);
        Assembly assembly = EmitAndLoad(run.OutputCompilation);
        StateModelRegistry models = assembly.GetType("Host")!
            .GetMethod("Models")!.CreateDelegate<Func<StateModelRegistry>>()();
        return models.Snapshot().ResolveCurrentModel(assembly.GetType(typeName)!);
    }

    internal static StateModelBinding ResolveSingleBinaryReferenceModelBinding(Assembly assembly) =>
        Assert.Single(ResolveBinaryReferenceModelBindings(assembly));

    private static List<StateModelBinding> ResolveBinaryReferenceModelBindings(Assembly assembly) {
        List<StateModelBinding> models = new();
        foreach (Type type in assembly.GetTypes()) {
            Type? state = type.GetNestedType("__DurableState", BindingFlags.Public | BindingFlags.NonPublic);
            FieldInfo? field = state?.GetField("Model", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (field?.GetValue(null) is StateModelBinding binding) models.Add(binding);
        }
        return models;
    }

    // IIncrementalGenerator with RegisterSourceOutput(CompilationProvider) keeps the sibling
    // output in the same compiler pass (invisible to DG's input); AsSourceGenerator adapts it
    // to the driver overload used by the harness, so no RS1042 suppression is needed.
    // RegisterPostInitializationOutput is deliberately avoided: it would change when the
    // output becomes visible to other generators.
    private sealed class SiblingSourceGenerator(string source) : IIncrementalGenerator {
        public void Initialize(IncrementalGeneratorInitializationContext context) =>
            context.RegisterSourceOutput(context.CompilationProvider, (productionContext, _) =>
                productionContext.AddSource("SiblingOutput.g.cs", SourceText.From(source, Encoding.UTF8)));
    }

}
