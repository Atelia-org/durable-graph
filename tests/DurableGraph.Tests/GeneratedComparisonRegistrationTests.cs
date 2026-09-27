using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Testing;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GeneratedCurrentPreparationProvesCompletePersistentState(bool family) {
        GeneratorTestRun run = RunGenerator(GeneratedEqualitySource(family));
        AssertSchemaOnlyCompiles(run);
        Assert.Equal(family, run.GeneratedSources.Any(source => source.HintName == "DurableGenericStates.g.cs"));
        Type host = EmitAndLoad(run.OutputCompilation).GetType("EqualityFixture.Host")!;
        StateModelRegistry registry = host.GetMethod("Models")!.CreateDelegate<Func<StateModelRegistry>>()();
        IDurableObject world = host.GetMethod("Create")!.CreateDelegate<Func<IDurableObject>>()();
        var set = host.GetMethod("Set")!.CreateDelegate<Action<IDurableObject, int>>();
        StateModelSnapshot models = registry.Snapshot();
        CaptureSession session = new();
        ObjectStateRecord Capture() {
            using var context = session.BeginCapture(models);
            ObjectId root = models.ResolveCurrentModel(world.GetType()).AddRoot(context, world);
            CapturedGraph graph = context.Seal();
            session.Accept(graph);
            return graph.Objects.Single(row => row.Id == root);
        }

        ObjectStateRecord baseline = Capture();
        ObjectStateRecord equal = Capture();
        Assert.NotSame(baseline, equal);
        Assert.Same(baseline.Preparation, equal.Preparation);
        // Exercise the registered erased capability, not a direct call to the generated body.
        Assert.True(baseline.Preparation!.ProvesSameState(baseline, equal));
        for (int mutation = 1; mutation <= (family ? 12 : 10); mutation++) {
            set(world, mutation);
            ObjectStateRecord different = Capture();
            Assert.False(baseline.Preparation.ProvesSameState(baseline, different), $"Missed persistent mutation {mutation} (family={family}).");
            Assert.False(baseline.Preparation.ProvesSameState(different, baseline));
            set(world, 0);
            ObjectStateRecord restored = Capture();
            Assert.True(baseline.Preparation.ProvesSameState(baseline, restored));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GeneratedCurrentModelsShareReadPairWithoutManualEqualityRegistration(bool family) {
        GeneratorTestRun run = RunGenerator(GeneratedEqualitySource(family));
        AssertSchemaOnlyCompiles(run);
        Type host = EmitAndLoad(run.OutputCompilation).GetType("EqualityFixture.Host")!;
        StateModelRegistry models = host.GetMethod("Models")!.CreateDelegate<Func<StateModelRegistry>>()();
        IDurableObject world = host.GetMethod("Create")!.CreateDelegate<Func<IDurableObject>>()();
        using RawBaseDirectory directory = new();
        host.GetMethod("Save")!.CreateDelegate<Action<string, IDurableObject, StateModelRegistry, ReadAmplificationBaseBudgetParameters>>()(
            directory.Path, world, models, TestSavePolicies.Baseline);
        using (var repository = EventHistoryRepository.OpenReadOnlyExisting(directory.Path, models)) {
            GraphFrame frame = Assert.Single(repository.ReadFrames("main"));
            (IDurableObject first, IDurableObject second) = repository.ReadPair(frame, frame);
            // White-box optimization witness: public consumers must not depend on this identity.
            Assert.Same(first, second);
            Assert.NotSame(world, first);
            Assert.Same(first, first.GetType().GetField("Peer")!.GetValue(first));
        }
    }

    private static string GeneratedEqualitySource(bool family) => $$"""
        using System;
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        using Atelia.DurableGraph.Persistence;
        namespace EqualityFixture;
        [DurableType("equality.ancestor", 1)]
        public partial class Ancestor : IDurableObject {
            [DurableField(1)] public int Inherited;
        }
        [DurableType("equality.part", 1)]
        public partial struct Part{{(family ? "<T>" : "")}} {
            [DurableField(1)] public int Number;
            [DurableField(2)] public {{(family ? "T" : "string")}} Link;
        }
        [DurableType("equality.world", 1)]
        public partial class World{{(family ? "<T>" : "")}} : Ancestor {
            [DurableField(1)] public Part{{(family ? "<T>" : "")}} Part;
            [DurableField(2)] public float Float;
            [DurableField(3)] public double Double;
            [DurableField(4)] public Half Half;
            [DurableField(5)] public decimal Decimal;
            [DurableField(6)] public DateTimeOffset Timestamp;
            [DurableField(7)] public World{{(family ? "<T>" : "")}} Peer;
            [DurableField(8)] public {{(family ? "T" : "string")}} Slot;
            {{(family ? "[DurableField(9)] public float? Optional;" : "")}}
        }
        public static class Host {
            private const string Text = "same content";
            public static StateModelRegistry Models() {
                var models = new StateModelRegistry();
                {{(family ? "Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);" : "Ancestor.__DurableState.RegisterModel(models); World.__DurableState.RegisterModel(models);")}}
                return models;
            }
            public static IDurableObject Create() {
                var world = new World{{(family ? "<string>" : "")}}();
                Set(world, 0);
                return world;
            }
            public static void Save(string path, IDurableObject world, StateModelRegistry models, ReadAmplificationBaseBudgetParameters parameters) {
                using var repository = EventHistoryRepository.CreateNew(path, models);
                using var session = repository.CreateBranch("main", (World{{(family ? "<string>" : "")}})world, parameters);
            }
            public static void Set(IDurableObject instance, int mutation) {
                var world = (World{{(family ? "<string>" : "")}})instance;
                world.Inherited = mutation == 1 ? 8 : 7;
                world.Part = new() { Number = mutation == 2 ? 10 : 9,
                    Link = mutation == 3 ? new string(Text.ToCharArray()) : Text };
                world.Float = BitConverter.Int32BitsToSingle(mutation == 4 ? 0x7FC00002 : 0x7FC00001);
                world.Double = mutation == 5 ? BitConverter.Int64BitsToDouble(long.MinValue) : 0d;
                world.Half = BitConverter.UInt16BitsToHalf(mutation == 6 ? (ushort)0x7E02 : (ushort)0x7E01);
                world.Decimal = mutation == 7 ? 1.00m : 1.0m;
                var instant = new DateTimeOffset(2026, 9, 11, 8, 0, 0, TimeSpan.FromHours(8));
                world.Timestamp = mutation == 8 ? instant.ToOffset(TimeSpan.Zero) : instant;
                world.Peer = mutation == 9 ? null : world;
                world.Slot = mutation == 10 ? new string(Text.ToCharArray()) : Text;
                {{(family ? "world.Optional = mutation == 11 ? null : mutation == 12 ? BitConverter.Int32BitsToSingle(int.MinValue) : 0f;" : "")}}
            }
        }
        """;
}
