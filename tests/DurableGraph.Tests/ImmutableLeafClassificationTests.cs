using System;
using System.Reflection;
using System.Linq;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Testing;
using Microsoft.CodeAnalysis;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Fact]
    public void EmptyReferenceModelClassifiesAsImmutableLeaf() {
        AssertLeafClassification("""
            using Atelia.DurableGraph;
            [DurableType("leaf.empty", 1)]
            public partial class EmptyLeaf : IDurableObject {
            }
            """, expectedLeaf: true);
    }

    [Fact]
    public void AllBuiltinScalarFieldsClassifyAsImmutableLeaf() {
        AssertLeafClassification("""
            using System;
            using Atelia.DurableGraph;
            [DurableType("leaf.scalars", 1)]
            public partial class ScalarLeaf : IDurableObject {
                [DurableField(1)] public readonly bool F01;
                [DurableField(2)] public readonly sbyte F02;
                [DurableField(3)] public readonly byte F03;
                [DurableField(4)] public readonly short F04;
                [DurableField(5)] public readonly ushort F05;
                [DurableField(6)] public readonly int F06;
                [DurableField(7)] public readonly uint F07;
                [DurableField(8)] public readonly long F08;
                [DurableField(9)] public readonly ulong F09;
                [DurableField(10)] public readonly char F10;
                [DurableField(11)] public readonly Half F11;
                [DurableField(12)] public readonly float F12;
                [DurableField(13)] public readonly double F13;
                [DurableField(14)] public readonly Guid F14;
                [DurableField(15)] public readonly decimal F15;
                [DurableField(16)] public readonly TimeSpan F16;
                [DurableField(17)] public readonly DateOnly F17;
                [DurableField(18)] public readonly TimeOnly F18;
                [DurableField(19)] public readonly DateTimeOffset F19;
            }
            """, expectedLeaf: true);
    }

    [Fact]
    public void RecursiveInlineStructFieldClassifiesAsImmutableLeaf() {
        AssertLeafClassification("""
            using Atelia.DurableGraph;
            [DurableType("leaf.node.inner", 1)]
            public readonly partial struct InnerNode {
                [DurableField(1)] public readonly int Value;
            }
            [DurableType("leaf.node.outer", 1)]
            public readonly partial struct OuterNode {
                [DurableField(1)] public readonly InnerNode Inner;
                [DurableField(2)] public readonly int Sum;
            }
            [DurableType("leaf.recursive.inline", 1)]
            public partial class RecursiveInlineLeaf : IDurableObject {
                [DurableField(1)] public readonly OuterNode Node;
            }
            """, expectedLeaf: true);
    }

    [Fact]
    public void ImmutableInheritanceChainClassifiesBothModels() {
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Runtime;
            [DurableType("leaf.chain.base", 1)]
            public partial class ChainBase : IDurableObject {
                [DurableField(1)] public readonly int BaseValue;
            }
            [DurableType("leaf.chain.derived", 1)]
            public partial class ChainDerived : ChainBase {
                [DurableField(2)] public readonly long DerivedValue;
            }
            public static class Host {
                public static StateModelBinding Base => ChainBase.__DurableState.Model;
                public static StateModelBinding Derived => ChainDerived.__DurableState.Model;
            }
            """);
        Assert.DoesNotContain(run.GeneratorDiagnostics, IsError);
        Type host = EmitAndLoad(run.OutputCompilation).GetType("Host")!;
        Assert.True(((StateModelBinding)host.GetProperty("Base")!.GetValue(null)!).IsImmutableLeaf);
        Assert.True(((StateModelBinding)host.GetProperty("Derived")!.GetValue(null)!).IsImmutableLeaf);
    }

    [Fact]
    public void MutableDerivedModelExcludesOnlyTheDerivedModel() {
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Runtime;
            [DurableType("leaf.negative.base", 1)]
            public partial class NegativeChainBase : IDurableObject {
                [DurableField(1)] public readonly int BaseValue;
            }
            [DurableType("leaf.negative.derived", 1)]
            public partial class NegativeChainDerived : NegativeChainBase {
                [DurableField(2)] public long DerivedValue;
            }
            public static class Host {
                public static StateModelBinding Base => NegativeChainBase.__DurableState.Model;
                public static StateModelBinding Derived => NegativeChainDerived.__DurableState.Model;
            }
            """);
        Assert.DoesNotContain(run.GeneratorDiagnostics, IsError);
        Type host = EmitAndLoad(run.OutputCompilation).GetType("Host")!;
        Assert.True(((StateModelBinding)host.GetProperty("Base")!.GetValue(null)!).IsImmutableLeaf);
        Assert.False(((StateModelBinding)host.GetProperty("Derived")!.GetValue(null)!).IsImmutableLeaf);
    }

    [Fact]
    public void MutableBaseReadonlyDerivedChainExcludesImmutableLeaf() {
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Runtime;
            [DurableType("leaf.neg.mutable.base", 1)]
            public partial class MutableChainBase : IDurableObject {
                [DurableField(1)] public int BaseValue;
            }
            [DurableType("leaf.neg.mutable.derived", 1)]
            public partial class MutableChainDerived : MutableChainBase {
                [DurableField(2)] public readonly int Value;
            }
            public static class Host {
                public static StateModelBinding Base => MutableChainBase.__DurableState.Model;
                public static StateModelBinding Derived => MutableChainDerived.__DurableState.Model;
            }
            """);
        Assert.DoesNotContain(run.GeneratorDiagnostics, IsError);
        Type host = EmitAndLoad(run.OutputCompilation).GetType("Host")!;
        Assert.False(((StateModelBinding)host.GetProperty("Base")!.GetValue(null)!).IsImmutableLeaf);
        Assert.False(((StateModelBinding)host.GetProperty("Derived")!.GetValue(null)!).IsImmutableLeaf);
    }

    [Fact]
    public void GetOnlyPropertyBackingFieldExcludesImmutableLeaf() {
        // A get-only auto-property is compiler-synthesized readonly storage that the audited
        // pipeline never sees as an explicit declaration (DB-075 excludes implicit backings).
        AssertLeafClassification("""
            using Atelia.DurableGraph;
            [DurableType("leaf.getonly.property", 1)]
            public partial class GetOnlyPropertyLeaf : IDurableObject {
                [DurableField(1)] public readonly int Value;
                public long ViewCount { get; }
            }
            """, expectedLeaf: false);
    }

    [Fact]
    public void InitOnlyPropertyBackingFieldExcludesImmutableLeaf() {
        AssertLeafClassification("""
            using Atelia.DurableGraph;
            [DurableType("leaf.initonly.property", 1)]
            public partial class InitOnlyPropertyLeaf : IDurableObject {
                [DurableField(1)] public readonly int Value;
                public int Points { get; init; }
            }
            """, expectedLeaf: false);
    }

    [Fact]
    public void PrimaryConstructorCaptureExcludesImmutableLeaf() {
        // A parameter captured by an instance member materializes an implicit mutable field;
        // the audited field set (and the runtime structure check) reject the type.
        AssertLeafClassification("""
            using Atelia.DurableGraph;
            [DurableType("leaf.primary.ctor", 1)]
            public partial class PrimaryCtorLeaf(int captured) : IDurableObject {
                [DurableField(1)] public readonly int Value;
                public int Next() => ++captured;
            }
            """, expectedLeaf: false);
    }

    [Fact]
    public void PrimaryConstructorFieldInitializerKeepsImmutableLeaf() {
        // The parameter is consumed only by the explicit readonly field initializer; it never
        // materializes into instance state, so qualification follows the audited fields.
        AssertLeafClassification("""
            using Atelia.DurableGraph;
            [DurableType("leaf.primary.initializer", 1)]
            public partial class PrimaryInitializerLeaf(int value) : IDurableObject {
                [DurableField(1)] public readonly int Value = value;
            }
            """, expectedLeaf: true);
    }

    [Fact]
    public void EmptyPrimaryConstructorKeepsImmutableLeaf() {
        AssertLeafClassification("""
            using Atelia.DurableGraph;
            [DurableType("leaf.primary.empty", 1)]
            public partial class EmptyPrimaryCtorLeaf() : IDurableObject {
            }
            """, expectedLeaf: true);
    }

    [Fact]
    public void MutableDurableFieldExcludesImmutableLeaf() {
        AssertLeafClassification("""
            using Atelia.DurableGraph;
            [DurableType("leaf.mutable.field", 1)]
            public partial class MutableFieldLeaf : IDurableObject {
                [DurableField(1)] public int Value;
            }
            """, expectedLeaf: false);
    }

    [Fact]
    public void StringFieldExcludesImmutableLeaf() {
        AssertLeafClassification("""
            using Atelia.DurableGraph;
            [DurableType("leaf.string", 1)]
            public partial class StringLeaf : IDurableObject {
                [DurableField(1)] public readonly string Name;
            }
            """, expectedLeaf: false);
    }

    [Fact]
    public void DurableObjectReferenceFieldExcludesImmutableLeaf() {
        AssertLeafClassification("""
            using Atelia.DurableGraph;
            [DurableType("leaf.reference", 1)]
            public partial class ReferenceLeaf : IDurableObject {
                [DurableField(1)] public readonly ReferenceLeaf? Peer;
            }
            """, expectedLeaf: false);
    }

    [Theory]
    [InlineData("int[]")]
    [InlineData("global::System.Collections.Generic.List<int>")]
    [InlineData("global::System.Collections.Generic.Dictionary<int, string>")]
    public void ContainerFieldExcludesImmutableLeaf(string fieldType) {
        // Container field patterns carry type arguments, so UsesGenericTemplates routes these
        // models through the family path; the owner binding stays false while qualified
        // non-generic leaves in the same compilation can still claim the capability (DB-075).
        GeneratorTestRun run = RunGenerator($$"""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Persistence;
            using Atelia.DurableGraph.Runtime;
            [DurableType("leaf.container", 1)]
            public partial class ContainerLeaf : IDurableObject {
                [DurableField(1)] public readonly {{fieldType}} Items;
            }
            """ + FamilyHostSource);
        Assert.DoesNotContain(run.GeneratorDiagnostics, IsError);
        Assembly assembly = EmitAndLoad(run.OutputCompilation);
        StateModelRegistry models = assembly.GetType("Host")!
            .GetMethod("Models")!.CreateDelegate<Func<StateModelRegistry>>()();
        Assert.False(models.Snapshot().ResolveCurrentModel(assembly.GetType("ContainerLeaf")!).IsImmutableLeaf);
    }

    [Fact]
    public void TransientFieldExcludesImmutableLeaf() {
        AssertLeafClassification("""
            using Atelia.DurableGraph;
            [DurableType("leaf.transient", 1)]
            public partial class TransientLeaf : IDurableObject {
                [DurableField(1)] public readonly int Value;
                [Transient] public readonly int Scratch;
            }
            """, expectedLeaf: false);
    }

    [Fact]
    public void AutoPropertyBackingFieldExcludesImmutableLeaf() {
        AssertLeafClassification("""
            using Atelia.DurableGraph;
            [DurableType("leaf.auto.property", 1)]
            public partial class AutoPropertyLeaf : IDurableObject {
                [DurableField(1)] public readonly int Value;
                public long ViewCount { get; set; }
            }
            """, expectedLeaf: false);
    }

    [Fact]
    public void FieldLikeEventBackingFieldExcludesImmutableLeaf() {
        AssertLeafClassification("""
            using Atelia.DurableGraph;
            [DurableType("leaf.event", 1)]
            public partial class EventLeaf : IDurableObject {
                [DurableField(1)] public readonly int Value;
                public event System.EventHandler? Changed;
            }
            """, expectedLeaf: false);
    }

    [Fact]
    public void MutableInlineStructMemberExcludesImmutableLeaf() {
        AssertLeafClassification("""
            using Atelia.DurableGraph;
            [DurableType("leaf.mutable.inline.value", 1)]
            public partial struct MutableValue {
                [DurableField(1)] public int Number;
            }
            [DurableType("leaf.mutable.inline.world", 1)]
            public partial class MutableInlineLeaf : IDurableObject {
                [DurableField(1)] public readonly MutableValue Value;
            }
            """, expectedLeaf: false);
    }

    [Fact]
    public void GenericFamilyDoesNotClaimImmutableLeaf() {
        // Closed generic constructions resolve through the family definition factory; the
        // generic root itself never qualifies (DB-075 candidate rules exclude generic symbols).
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Persistence;
            using Atelia.DurableGraph.Runtime;
            [DurableType("family.part", 1)]
            public partial struct Part<T> {
                [DurableField(1)] public T Item;
            }
            [DurableType("family.world", 1)]
            public partial class World<T> : IDurableObject {
                [DurableField(1)] public Part<T> Part;
            }
            """ + FamilyHostSource);
        Assert.DoesNotContain(run.GeneratorDiagnostics, IsError);
        Assembly assembly = EmitAndLoad(run.OutputCompilation);
        StateModelRegistry models = assembly.GetType("Host")!
            .GetMethod("Models")!.CreateDelegate<Func<StateModelRegistry>>()();
        Type closedWorld = assembly.GetType("World`1")!.MakeGenericType(typeof(int));
        Assert.False(models.Snapshot().ResolveCurrentModel(closedWorld).IsImmutableLeaf);
    }

    [Fact]
    public void CrossAssemblyInlineMemberExcludesImmutableLeaf() {
        // The consumer registry must register the referenced library's generated definitions
        // through reflection (no product IVT, no public query API) so the inline member's
        // value binding can resolve; the owner binding itself still stays false (DB-075
        // fail-closed rule for cross-assembly inline members).
        GeneratorTestRun libraryRun = RunCrossAssemblyGenerator(InlineLibrarySource, forceDefinitions: "true");
        var library = EmitCrossAssemblyReference(
            libraryRun);
        GeneratorTestRun app = RunCrossAssemblyGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Persistence;
            using Atelia.DurableGraph.Runtime;
            [DurableType("leaf.remote.world", 1)]
            public partial class RemoteLeaf : IDurableObject {
                [DurableField(1)] public readonly Remote.Point Position;
            }
            public static class Host {
                public static StateModelRegistry Models() {
                    var models = new StateModelRegistry();
                    global::Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
                    global::System.Reflection.Assembly referenced = typeof(global::Remote.Point).Assembly;
                    foreach (global::System.Type candidate in referenced.GetTypes()) {
                        if (candidate.FullName == "Atelia.DurableGraph.Generated.DurableDefinitions") {
                            candidate.GetMethod("Register", global::System.Reflection.BindingFlags.Public | global::System.Reflection.BindingFlags.Static)!.Invoke(null, new object?[] { models });
                        }
                    }
                    return models;
                }
            }
            """, [library.Reference]);
        AssertSchemaOnlyCompiles(app);
        using CrossAssemblyLoadScope scope = new(libraryRun, app);
        Assembly appAssembly = scope.Load(app);
        StateModelRegistry models = appAssembly.GetType("Host")!
            .GetMethod("Models")!.CreateDelegate<Func<StateModelRegistry>>()();
        Assert.False(models.Snapshot().ResolveCurrentModel(appAssembly.GetType("RemoteLeaf")!).IsImmutableLeaf);
    }

    [Fact]
    public void ImmutableLeafRegistrationWitnessCoversPositiveAndNegativeModels() {
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Persistence;
            using Atelia.DurableGraph.Runtime;
            [DurableType("leaf.witness.good", 1)]
            public partial class GoodLeaf : IDurableObject {
                [DurableField(1)] public readonly int Value;
            }
            [DurableType("leaf.witness.bad", 1)]
            public partial class BadLeaf : IDurableObject {
                [DurableField(1)] public int Value;
            }
            public static class Host {
                public static global::Atelia.DurableGraph.Runtime.StateModelBinding Good => GoodLeaf.__DurableState.Model;
                public static global::Atelia.DurableGraph.Runtime.StateModelBinding Bad => BadLeaf.__DurableState.Model;
            }
            """);
        Assert.DoesNotContain(run.GeneratorDiagnostics, IsError);
        Type host = EmitAndLoad(run.OutputCompilation).GetType("Host")!;
        var good = (StateModelBinding)host.GetProperty("Good")!.GetValue(null)!;
        var bad = (StateModelBinding)host.GetProperty("Bad")!.GetValue(null)!;
        Assert.True(good.IsImmutableLeaf);
        Assert.False(bad.IsImmutableLeaf);
    }

    [Fact]
    public void ImmutableLeafModelSharesReadPairWithoutRegression() {
        GeneratorTestRun run = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Persistence;
            using Atelia.DurableGraph.Runtime;
            [DurableType("leaf.pair.world", 1)]
            public partial class World : IDurableObject {
                [DurableField(1)] public readonly int Value;
            }
            public static class Host {
                public static StateModelRegistry Models() {
                    var models = new StateModelRegistry();
                    World.__DurableState.RegisterModel(models);
                    return models;
                }
                public static IDurableObject Create() => new World();
                public static void Save(string path, IDurableObject world, StateModelRegistry models, ReadAmplificationBaseBudgetParameters parameters) {
                    using var repository = EventHistoryRepository.CreateNew(path);
                    using var session = repository.CreateBranch("main", (World)world, models, parameters);
                }
            }
            """);
        Assert.DoesNotContain(run.GeneratorDiagnostics, IsError);
        Type host = EmitAndLoad(run.OutputCompilation).GetType("Host")!;
        StateModelRegistry models = host.GetMethod("Models")!.CreateDelegate<Func<StateModelRegistry>>()();
        IDurableObject world = host.GetMethod("Create")!.CreateDelegate<Func<IDurableObject>>()();
        using RawBaseDirectory directory = new();
        host.GetMethod("Save")!.CreateDelegate<Action<string, IDurableObject, StateModelRegistry, ReadAmplificationBaseBudgetParameters>>()(
            directory.Path, world, models, TestSavePolicies.Baseline);
        using (var repository = EventHistoryRepository.OpenReadOnlyExisting(directory.Path)) {
            GraphFrame frame = Assert.Single(repository.ReadFrames("main"));
            (IDurableObject first, IDurableObject second) = repository.ReadPair(frame, frame, models);
            Assert.Same(first, second);
        }
    }

    private static void AssertLeafClassification(string source, bool expectedLeaf) {
        // The source must contain exactly one non-inline reference model; this helper asserts
        // the delivered binding capability instead of generated text (DB-075 C2).
        GeneratorTestRun run = RunGenerator(source);
        Assert.DoesNotContain(run.GeneratorDiagnostics, IsError);
        StateModelBinding model = ResolveSingleBinaryReferenceModelBinding(EmitAndLoad(run.OutputCompilation));
        Assert.Equal(expectedLeaf, model.IsImmutableLeaf);
    }

}
