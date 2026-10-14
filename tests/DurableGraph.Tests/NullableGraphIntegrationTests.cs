using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Persistence;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Fact]
    public void NullableCompositionsFreezeCapturedValuesAndDiscardDoesNotRetireReferencedIdentity() {
        GeneratorTestRun run = RunGenerator(NullableGraphSource);
        AssertSchemaOnlyCompiles(run);
        Type host = EmitAndLoad(run.OutputCompilation).GetType("NullableGraph.Host")!;
        object[] fixture = (object[])host.GetMethod("Create")!.Invoke(null, null)!;
        var models = (StateModelRegistry)fixture[0];
        IDurableObject world = Assert.IsAssignableFrom<IDurableObject>(fixture[1]);
        Action edit = (Action)fixture[2], clear = (Action)fixture[3], restore = (Action)fixture[4];
        ModelExecutionEnvironment snapshot = models.CreateEnvironment();
        Assert.True(snapshot.TryGetCurrentModel(world.GetType(), out StateModelBinding? binding));
        Assert.NotNull(binding);
        var session = new CaptureSession();
        CapturedGraph Capture() {
            // Accept/Discard resolves this pending context; disposing here would discard
            // the sealed candidate before its caller can prepare or accept it.
            CaptureContext context = session.BeginCapture(snapshot);
            binding.AddRoot(context, world);
            return context.Seal();
        }

        CapturedGraph first = Capture();
        ObjectStateRecord node = Assert.Single(first.Objects, row => row.Layout.Schema?.SchemaId == "nullable.node");
        // World, Node, two Boxes and the List are objects; both nullable layers are inline.
        Assert.Equal(5, first.Objects.Count);
        session.Accept(first);
        edit();
        CapturedGraph changed = Capture();
        PreparedCapturedGraph beforeMutation = session.Prepare(changed);
        Assert.Contains(beforeMutation.Objects, row => row.DeltaBody?.HasChanges == true);
        clear();
        PreparedCapturedGraph afterMutation = session.Prepare(changed);
        Assert.Equal(beforeMutation.Objects.Count, afterMutation.Objects.Count);
        for (int i = 0; i < beforeMutation.Objects.Count; i++) {
            Assert.Equal(beforeMutation.Objects[i].BaseBody.Body.ToArray(), afterMutation.Objects[i].BaseBody.Body.ToArray());
            Assert.Equal(beforeMutation.Objects[i].DeltaBody?.Body.ToArray(), afterMutation.Objects[i].DeltaBody?.Body.ToArray());
        }
        session.Accept(changed);

        CapturedGraph absent = Capture();
        Assert.DoesNotContain(absent.Objects, row => row.Id == node.Id);
        Assert.Equal(4, absent.Objects.Count);
        session.Discard(absent);
        restore();
        CapturedGraph restored = Capture();
        Assert.Contains(restored.Objects, row => row.Id == node.Id);
        session.Accept(restored);
        CapturedGraph unchanged = Capture();
        Assert.All(session.Prepare(unchanged).Objects.Where(row => row.DeltaBody is not null),
            row => Assert.False(row.DeltaBody!.HasChanges));
        session.Discard(unchanged);
    }

    private const string NullableGraphSource = """
        using System;
        using System.Collections.Generic;
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        using Atelia.DurableGraph.Persistence;
        namespace NullableGraph;
        [DurableType("nullable.node", 1)] public partial class Node : IDurableObject {
            [DurableField(1)] public Node Self;
            [DurableField(2)] public int Value;
        }
        [DurableType("nullable.point", 1)] public readonly partial struct Point {
            [DurableField(1)] public readonly int X;
            [DurableField(2)] public readonly Node Link;
            public Point(int x, Node link) { X=x; Link=link; }
        }
        [DurableType("nullable.envelope", 1)] public partial struct Envelope<T> {
            [DurableField(1)] public T Value;
        }
        [DurableType("nullable.box", 1)] public partial class Box<T> : IDurableObject {
            [DurableField(1)] public T Value;
        }
        [DurableType("nullable.maybe", 1)] public partial class Maybe<T> : IDurableObject where T:struct {
            [DurableField(1)] public T? Value;
        }
        [DurableType("nullable.world", 1)] public partial class World : IDurableObject {
            [DurableField(1)] public Envelope<Point?>? Value;
            [DurableField(2)] public List<Point?> Items = new();
            [DurableField(3)] public Box<int?> Scalar = new();
            [DurableField(4)] public Maybe<int> Constrained = new();
        }
        public static class Host {
            public static object[] Create() {
                var models = new StateModelRegistry();
                Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
                var node = new Node {Value=1}; node.Self=node;
                var world = new World { Scalar=new Box<int?> {Value=17}, Constrained=new Maybe<int> {Value=19} };
                Action restore = () => { world.Value=new Envelope<Point?> {Value=new Point(41,node)};
                    world.Items.Clear(); world.Items.Add(null); world.Items.Add(new Point(43,node)); };
                Action clear = () => { world.Value=null; world.Items.Clear(); world.Items.Add(null); };
                Action edit = () => { node.Value++; world.Value=new Envelope<Point?> {Value=new Point(51,node)};
                    world.Items[1]=new Point(53,node); world.Scalar.Value=null; world.Constrained.Value=23; };
                restore();
                return new object[] {models,world,edit,clear,restore};
            }
        }
        """;
}
