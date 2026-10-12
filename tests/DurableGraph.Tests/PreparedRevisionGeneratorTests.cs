using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Fact]
    public void CapturedGraphPreparationOwnsHeterogeneousFrozenBodiesAndRepeatedDeltas() {
        GeneratorTestRun run = RunGenerator(PreparedRevisionSource);
        AssertSchemaOnlyCompiles(run);
        Type host = EmitAndLoad(run.OutputCompilation).GetType("PreparedRevisionWitness.Host")!;
        var capture = host.GetMethod("Capture")!.CreateDelegate<PreparedRevisionCapture>();
        var session = (CaptureSession)host.GetField("Session")!.GetValue(null)!;
        byte[][] expected = new byte[5][];
        uint[][] roots = new uint[5][];
        uint ownerId = 0, tagId = 0, originalStringId = 0, equalStringId = 0, emptyId = 0;
        // Capture owns frozen values and comparison baselines, without store/source authority.
        for (int stage = 0; stage < expected.Length; stage++) {
            CapturedGraph? accepted = session.Current;
            CapturedGraph graph = capture(stage);
            PreparedCapturedGraph input = session.Prepare(graph);
            PreparedCapturedGraph repeated = session.Prepare(graph);
            Assert.Same(accepted, input.Previous);
            Assert.Same(graph, input.Candidate);
            Assert.Equal(input.Objects.Select(row => row.Current.Id), repeated.Objects.Select(row => row.Current.Id));
            foreach (var row in input.Objects) {
                var again = repeated.Objects.Single(item => item.Current.Id == row.Current.Id);
                Assert.Equal(row.BaseBody.Body.ToArray(), again.BaseBody.Body.ToArray());
                Assert.Equal(row.DeltaBody?.HasChanges, again.DeltaBody?.HasChanges);
                ObjectStateRecord? previous = accepted?.Objects.SingleOrDefault(item => item.Id == row.Current.Id);
                Assert.Same(previous, row.Previous);
                if (previous is null || row.Current.Kind == ObjectStateKind.String)
                    Assert.Null(row.DeltaBody);
                else Assert.NotNull(row.DeltaBody);
                if (row.DeltaBody is not null)
                    Assert.Equal(row.DeltaBody.Body.ToArray(), again.DeltaBody!.Body.ToArray());
            }
            Assert.Same(accepted, session.Current);
            Assert.Throws<InvalidOperationException>(() => session.BeginCapture());
            roots[stage] = graph.RootIds.Select(id => id.Value).ToArray();
            if (stage is 1 or 2) {
                tagId = graph.RootIds[3].Value;
                Assert.Equal(new[] { graph.RootIds[0].Value, graph.RootIds[0].Value, 0u, tagId, tagId, 0u }, roots[stage]);
            }
            else Assert.Equal(new[] { graph.RootIds[0].Value, graph.RootIds[0].Value, 0u }, roots[stage]);
            var owner = Assert.Single(input.Objects, row => row.Current.Id == graph.RootIds[0]);
            if (stage == 0) {
                ownerId = owner.Current.Id.Value;
                originalStringId = Assert.Single(graph.Objects,
                    item => item.Kind == ObjectStateKind.String && item.StringContent == "x").Id.Value;
                emptyId = Assert.Single(graph.Objects,
                    item => item.Kind == ObjectStateKind.String && item.StringContent == string.Empty).Id.Value;
                Assert.NotEqual(0u, originalStringId);
                Assert.NotEqual(0u, emptyId);
            }
            if (stage == 1) {
                equalStringId = Assert.Single(graph.Objects,
                    item => item.Kind == ObjectStateKind.String && item.StringContent == "x" && item.Id.Value != originalStringId).Id.Value;
                Assert.NotEqual(originalStringId, equalStringId);
            }
            uint name = stage < 2 ? originalStringId : equalStringId;
            uint alias = stage == 0 ? originalStringId : equalStringId;
            byte score = (byte)(Math.Min(stage, 2) + 1);
            // Four one-byte references/scalar values followed by eight fixed-width zero doubles.
            expected[stage] = new byte[68];
            expected[stage][0] = checked((byte)name);
            expected[stage][1] = checked((byte)alias);
            expected[stage][2] = checked((byte)emptyId);
            expected[stage][3] = score;
            Assert.Equal(expected[stage], owner.BaseBody.Body.ToArray());
            if (stage is 1 or 2) {
                Assert.True(owner.DeltaBody!.HasChanges);
                Assert.Equal<byte>([stage == 1 ? (byte)0x0A : (byte)0x09, 0, checked((byte)equalStringId), score],
                    owner.DeltaBody.Body.ToArray());
            }
            if (stage >= 3) {
                Assert.False(owner.DeltaBody!.HasChanges);
                Assert.Equal<byte>([0, 0], owner.DeltaBody.Body.ToArray());
            }

            if (stage is 1 or 2) {
                var tag = Assert.Single(input.Objects, row => row.Current.Id.Value == tagId);
                Assert.NotNull(tag.Current.Schema!.BaseSchema);
                Assert.Equal<byte>([checked((byte)equalStringId), 7], tag.BaseBody.Body.ToArray());
                if (stage == 1) Assert.Null(tag.Previous);
                else {
                    Assert.False(tag.DeltaBody!.HasChanges);
                    Assert.Equal<byte>([0], tag.DeltaBody.Body.ToArray());
                }
            }

            Assert.Equal(ownerId, owner.Current.Id.Value);
            if (stage >= 2) Assert.DoesNotContain(graph.Objects, row => row.Id.Value == originalStringId);
            if (stage >= 3) Assert.DoesNotContain(graph.Objects, row => row.Id.Value == tagId);
            if (stage < expected.Length - 1) session.Accept(graph);
            else session.Discard(graph);
        }
        Assert.NotNull(session.Current);
        Assert.Contains(session.Current!.Objects, row => row.Id.Value == equalStringId);
        Assert.Contains(session.Current.Objects, row => row.Id.Value == emptyId);
    }

    private delegate CapturedGraph PreparedRevisionCapture(int stage);

    private const string PreparedRevisionSource = """
        using System;
        using System.Collections.Generic;
        using System.IO;
        using System.Linq;
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        using Atelia.DurableGraph.Persistence;
        using Atelia.DurableGraph.Storage;
        using Atelia.FrameStore;
        using Atelia.DurableGraph.Serialization;
        namespace PreparedRevisionWitness;
        [DurableType("prepared-revision.owner", 1)]
        public sealed partial class Owner : IDurableObject {
            [DurableField(1)] private string _name;
            [DurableField(2)] private string _alias;
            [DurableField(3)] private string _empty = string.Empty;
            [DurableField(4)] private uint _score;
            [DurableField(10)] private double _a;
            [DurableField(11)] private double _b;
            [DurableField(12)] private double _c;
            [DurableField(13)] private double _d;
            [DurableField(14)] private double _e;
            [DurableField(15)] private double _f;
            [DurableField(16)] private double _g;
            [DurableField(17)] private double _h;
            public Owner(string value) { _name = value; _alias = value; }
            public void Change(string name, string alias, uint score) { _name = name; _alias = alias; _score = score; }
        }
        [DurableType("prepared-revision.tag-base", 1)]
        public abstract partial class TagBase : IDurableObject {
            [DurableField(1)] private string _label;
            protected TagBase(string value) { _label = value; }
            protected void ChangeLabel(string value) { _label = value; }
        }
        [DurableType("prepared-revision.tag", 1)]
        public sealed partial class Tag : TagBase {
            [DurableField(1)] private byte _marker;
            public Tag(string value) : base(value) { }
            public void Change(string label, byte marker) { ChangeLabel(label); _marker = marker; }
        }
        public static class Host {
            public static readonly CaptureSession Session = new();
            private static readonly string Shared = new string(new[] { 'x' });
            private static readonly string Equal = new string(new[] { 'x' });
            private static readonly Owner Domain = new(Shared);
            private static readonly Tag Secondary = new(Equal);
            public static CapturedGraph Capture(int stage) {
                if (ReferenceEquals(Shared, Equal)) throw new Exception("Fixture requires distinct equal strings.");
                Domain.Change(stage < 2 ? Shared : Equal, stage == 0 ? Shared : Equal, (uint)(Math.Min(stage, 2) + 1));
                Secondary.Change(Equal, 7);
                var context = Session.BeginCapture();
                Owner.__DurableState.AddRoot(context, Domain);
                Owner.__DurableState.AddRoot(context, Domain);
                Owner.__DurableState.AddRoot(context, null);
                if (stage is 1 or 2) {
                    Tag.__DurableState.AddRoot(context, Secondary);
                    Tag.__DurableState.AddRoot(context, Secondary);
                    Tag.__DurableState.AddRoot(context, null);
                }
                var graph = context.Seal();
                Domain.Change("mutated after Seal", "never captured", 99);
                Secondary.Change("mutated inherited field", 99);
                return graph;
            }
            public static StateModelRegistry Models() {
                StateModelRegistry models = new();
                Owner.__DurableState.RegisterModel(models);
                Tag.__DurableState.RegisterModel(models);
                return models;
            }
            public static object NewOwner() => new Owner(new string('x', 1));
            public static object NewTag() => new Tag(string.Empty);
        }
        """;
}
