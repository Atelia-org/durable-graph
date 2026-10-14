using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Reflection;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Fact]
    public void AdaptiveRejectsLargerMarkerCandidatesUsingRealGeneratedNestedStructDeltas() {
        GeneratorTestRun run = RunGenerator(AdaptiveWideSource);
        AssertSchemaOnlyCompiles(run);
        Type host = EmitAndLoad(run.OutputCompilation).GetType("AdaptiveWide.Host")!;
        var create = host.GetMethod("Marker")!.CreateDelegate<Func<int, object[]>>();
        foreach (int count in new[] { 34, 48, 65 }) {
            object[] fixture = create(count);
            StateModelRegistry registry = (StateModelRegistry)fixture[0];
            IDurableObject world = Assert.IsAssignableFrom<IDurableObject>(fixture[1]);
            ModelExecutionEnvironment snapshot = registry.CreateEnvironment();
            Assert.True(snapshot.TryGetCurrentModel(world.GetType(), out StateModelBinding? model));
            Assert.NotNull(model);
            Assert.True(snapshot.TryGetCurrentObjectBinding(fixture[2].GetType(), out ObjectBinding? binding));
            ListObjectBinding list = Assert.IsAssignableFrom<ListObjectBinding>(binding);
            CaptureSession session = new();
            ObjectStateRecord Capture() {
                using CaptureContext context = session.BeginCapture(snapshot);
                model.AddRoot(context, world);
                CapturedGraph candidate = context.Seal();
                ObjectStateRecord record = Assert.Single(candidate.Objects, row => row.Kind == ObjectStateKind.List);
                session.Accept(candidate);
                return record;
            }
            ObjectStateRecord prior = Capture();
            ((Action)fixture[3])();
            ObjectStateRecord current = Capture();
            // Erase live inputs after capture; all candidates must use their owned generated DTOs.
            ((System.Collections.IList)fixture[2]).Clear();
            StateValueBinding element = list.ElementBinding;
            MethodInfo verify = typeof(DurableSchemaGeneratorTests).GetMethod(nameof(VerifyAdaptiveGeneratedPair),
                BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(element.StateType, element.StateOpsType);
            verify.Invoke(null, [prior, current, count]);
        }
    }

    private static void VerifyAdaptiveGeneratedPair<TState, TOps>(ObjectStateRecord oldRecord, ObjectStateRecord newRecord,
        int count) where TState : unmanaged where TOps : IStateOps<TState> {
        Assert.Equal(oldRecord.Id, newRecord.Id);
        Assert.Equal(oldRecord.Layout, newRecord.Layout);
        FrozenListState<TState> prior = oldRecord.GetListState<TState>(), current = newRecord.GetListState<TState>();
        ListLayout layout = oldRecord.Layout.List!;
        var (result, observation) = ListAdaptiveDeltaTests.VerifyPair<TState, TOps>(
            prior.Elements.ToArray(), current.Elements.ToArray(), layout);
        PreparedDeltaBody myers = ListStateBody<TState, TOps>.PrepareDelta(prior, current, layout, ListDeltaAlgorithm.BoundedMyers);
        Assert.Equal(count switch { 34 => 207, 48 => 291, 65 => 393, _ => throw new InvalidOperationException() }, result.Body.Length);
        Assert.Equal(count switch { 34 => 1455, 48 => 2071, 65 => 2819, _ => throw new InvalidOperationException() }, myers.Body.Length);
        Assert.Equal(ListDeltaCompetitionOutcome.ByteLimitReached, observation.Outcome);
        Assert.Equal(result.Body.Length, observation.IncumbentBytes);
        Assert.InRange(observation.ChallengerWrittenBytes, observation.IncumbentBytes, myers.Body.Length - 1);
        // The frozen generated states also exercise actual nested bitmap/child bodies, not a cost surrogate.
        Assert.Equal(count, prior.Count);
        Assert.Equal(count, current.Count);
    }

    private const string AdaptiveWideSource = """
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        using Atelia.DurableGraph.Persistence;
        namespace AdaptiveWide;
        [DurableType("adaptive.cell",1)] public partial struct Cell<T> {
            [DurableField(1)] public int TestId;
            [DurableField(2)] public T Value;
        }
        [DurableType("adaptive.wide",1)] public partial struct Wide<T> {
            [DurableField(1)] public int TestId;
            [DurableField(2)] public T Value;
            [DurableField(3)] public long A;
            [DurableField(4)] public long B;
            [DurableField(5)] public long C;
            [DurableField(6)] public long D;
            [DurableField(7)] public long E;
            [DurableField(8)] public long F;
            [DurableField(9)] public long G;
            [DurableField(10)] public long H;
        }
        [DurableType("adaptive.world",1)] public partial class World:IDurableObject {
            [DurableField(1)] public List<Wide<Cell<int>>> Items = new();
        }
        public static class Host {
            public static object[] Marker(int count) {
                var registry = new StateModelRegistry();
                Atelia.DurableGraph.Generated.DurableDefinitions.Register(registry);
                var world = new World();
                for (int i=0;i<count;i++) world.Items.Add(new Wide<Cell<int>> {
                    Value = new Cell<int> {Value=i==0?-1_000_000:i*17},
                    A=1_000_000_000L,B=2_000_000_000L,C=3_000_000_000L,D=4_000_000_000L,
                    E=5_000_000_000L,F=6_000_000_000L,G=7_000_000_000L,H=8_000_000_000L
                });
                Action change = () => {
                    var marker = world.Items[0]; world.Items.RemoveAt(0);
                    for (int i=0;i<world.Items.Count;i++) {
                        var value=world.Items[i]; value.Value.Value++; world.Items[i]=value;
                    }
                    world.Items.Add(marker);
                };
                return new object[] {registry,world,world.Items,change};
            }
        }
        """;
}
