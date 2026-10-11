using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Fact]
    public void CapturedGraphPreparationPersistsHeterogeneousFrozenBodiesAndRebasesUnchangedObjects() {
        GeneratorTestRun run = RunGenerator(PreparedRevisionSource);
        AssertSchemaOnlyCompiles(run);
        Type host = EmitAndLoad(run.OutputCompilation).GetType("PreparedRevisionWitness.Host")!;
        var capture = host.GetMethod("Capture")!.CreateDelegate<PreparedRevisionCapture>();
        var decode = host.GetMethod("Decode")!.CreateDelegate<PreparedRevisionDecode>();
        var session = (CaptureSession)host.GetField("Session")!.GetValue(null)!;

        // Root/query IDs and statically selected readers belong to this fixture.
        // Exact stored type definitions come only from the reopened SchemaStore.
        FrameAddress[] revisions = new FrameAddress[5];
        byte[][] expected = new byte[5][];
        uint[][] roots = new uint[5][];
        uint ownerId = 0, tagId = 0, originalStringId = 0, equalStringId = 0, emptyId = 0;
        long initialH = 0, accumulatedH = 0;
        using RawBaseDirectory directory = new();
        const long rotationThreshold = 128;
        using (GraphResources segments = GraphResources.CreateNew(directory.Path, rotationThreshold)) {
            SchemaStore schemas = segments.Schemas;
            using StateRevisionStore store = new(segments.Data);
            for (int stage = 0; stage < revisions.Length; stage++) {
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

                // This fixture owns the correspondence between accepted graph and exact Parent.
                // Neither Prepare nor the raw Storage planner certifies that correspondence.
                FrameAddress? parent = stage == 0 ? null : revisions[stage - 1];
                IReadOnlyDictionary<uint, FrameAddress> priorHeads = parent is null
                    ? new Dictionary<uint, FrameAddress>() : store.ReadLiveObjectHeadMap(parent.Value);
                var parameters = new ReadAmplificationBaseBudgetParameters(stage < 3 ? 100 : 1, 100);
                var prepared = CapturedRevisionPlanner.PrepareRevision(store, schemas, parent, input, parameters);
                Assert.Same(accepted, session.Current); // Planning never installs a candidate or baseline.
                Assert.Equal(parent, prepared.Revision.ParentRevisionAddress);
                // 68 raw bytes + v4 header (one version byte + one representation-ID byte)
                // + ObjectVersion kind and one-byte body length (70) = 72 payload bytes.
                Assert.Equal(72, Assert.Single(prepared.Estimates, item => item.ObjectId.Value == ownerId).BasePayloadBytes);
                if (stage == 0) {
                    Assert.Equal(input.Objects.Count, prepared.Revision.LocalObjects.Count);
                    Assert.All(prepared.Revision.LocalObjects, record => Assert.Equal(ObjectVersionKind.Base, record.Kind));
                }
                else if (stage is 1 or 2) {
                    ObjectVersionRecord record = Assert.Single(prepared.Revision.LocalObjects, item => item.ObjectId == ownerId);
                    Assert.Equal(ObjectVersionKind.Delta, record.Kind);
                    Assert.Equal(owner.DeltaBody!.Body.ToArray(), record.Body.ToArray());
                    Assert.Equal(priorHeads[ownerId], record.PriorAddress);
                    Assert.Equal(stage == 1 ? 3 : 1, prepared.Revision.LocalObjects.Count);
                    if (stage == 1) {
                        Assert.All(prepared.Revision.LocalObjects.Where(item => item.ObjectId != ownerId),
                            item => Assert.Equal(ObjectVersionKind.Base, item.Kind));
                    }
                    Assert.Equal(stage == 1 ? Array.Empty<uint>() : new[] { originalStringId }, prepared.Revision.RemovedObjectIds);
                }
                else if (stage == 3) {
                    ObjectVersionRecord record = Assert.Single(prepared.Revision.LocalObjects);
                    Assert.Equal(ownerId, record.ObjectId);
                    Assert.Equal(ObjectVersionKind.Base, record.Kind);
                    Assert.Equal(expected[stage], BaseObjectBodyCodec.Decode(record.Body, schemas).Body.ToArray());
                    Assert.Equal(new[] { tagId }, prepared.Revision.RemovedObjectIds);
                }
                else {
                    Assert.Empty(prepared.Revision.LocalObjects);
                    Assert.Empty(prepared.Revision.RemovedObjectIds);
                }
                revisions[stage] = store.Append(prepared.Revision);
                Assert.Same(accepted, session.Current); // Append produces an address, not a publication or Capture.Accept.
                var heads = store.ReadLiveObjectHeadMap(revisions[stage]);
                Assert.Equal(input.Objects.Select(row => row.Current.Id.Value).Order(), heads.Keys.Order());
                Assert.Equal(stage == 0 ? revisions[0] : priorHeads[emptyId], heads[emptyId]);
                if (stage == 1) Assert.Equal(revisions[0], heads[originalStringId]);
                if (stage >= 2) Assert.False(heads.ContainsKey(originalStringId));
                if (stage >= 2) Assert.Equal(revisions[1], heads[equalStringId]);
                if (stage is 1 or 2) Assert.Equal(revisions[1], heads[tagId]);
                if (stage >= 3) Assert.False(heads.ContainsKey(tagId));
                ObjectVersionChain chain = store.ReadObjectVersionChain(revisions[stage], ownerId);
                if (stage == 0) initialH = chain.ReconstructionPayloadBytes;
                if (stage == 2) {
                    accumulatedH = chain.ReconstructionPayloadBytes;
                    Assert.True(accumulatedH > initialH);
                    Assert.Equal(3, chain.Records.Count);
                }
                if (stage >= 3) {
                    Assert.Single(chain.Records);
                    Assert.Equal(initialH, chain.ReconstructionPayloadBytes);
                    Assert.Equal(revisions[3], chain.ObjectHeadAddress);
                }
                if (stage < revisions.Length - 1) session.Accept(graph); // Explicit fixture baseline choice only.
                else session.Discard(graph);
            }
            segments.Data.ConfirmDurable();
        }
        Assert.Equal(72, initialH); // 68 raw + 2 v4 representation-header + 2 ObjectVersion bytes.
        Assert.True(accumulatedH > initialH);
        Assert.Equal(revisions.Length, revisions.Distinct().Count());
        using GraphResources reopened = GraphResources.OpenReadOnlyExisting(directory.Path);

        SchemaStore coldSchemas = reopened.Schemas;
        using StateRevisionStore cold = new(reopened.Data);
        for (int stage = 0; stage < revisions.Length; stage++) {
            var heads = cold.ReadLiveObjectHeadMap(revisions[stage]);
            ObjectVersionChain chain = cold.ReadObjectVersionChain(revisions[stage], ownerId);
            Assert.All(roots[stage].Where(id => id != 0), id => Assert.True(id == ownerId || id == tagId));
            ObjectVersionChain? tagChain = null;
            if (stage is 1 or 2) {
                tagChain = cold.ReadObjectVersionChain(revisions[stage], tagId);
                Assert.Single(tagChain.Records);
                Assert.Equal<byte>([checked((byte)equalStringId), 7],
                    BaseObjectBodyCodec.Decode(tagChain.Records[0].Record.Body, coldSchemas).Body.ToArray());
            }
            Dictionary<uint, byte[]> strings = [];
            foreach ((uint id, FrameAddress address) in heads.Where(item => item.Key != ownerId && item.Key != tagId)) {
                ObjectVersionChain stringChain = cold.ReadObjectVersionChain(revisions[stage], id);
                Assert.Equal(id == emptyId ? string.Empty : "x", TypedObjectVersionReader.ReadString(stringChain));
                strings.Add(id, BaseObjectBodyCodec.Decode(stringChain.Records[0].Record.Body).Body.ToArray());
            }
            Assert.Equal(expected[stage], decode(chain, coldSchemas, strings, stage == 1, tagChain));
            // Even references unchanged by Delta must be validated against the target view.
            strings.Remove(emptyId);
            Assert.Throws<InvalidDataException>(() => decode(chain, coldSchemas, strings, stage == 1, tagChain));
        }
        Assert.True(cold.ReadLiveObjectHeadMap(revisions[0]).ContainsKey(originalStringId));
        Assert.False(cold.ReadLiveObjectHeadMap(revisions[2]).ContainsKey(originalStringId));
        Assert.Equal<byte>([3, (byte)'x'], BaseObjectBodyCodec.Decode(cold.ReadObjectBaseBody(revisions[0], originalStringId)).Body.ToArray());
    }

    private delegate CapturedGraph PreparedRevisionCapture(int stage);
    private delegate byte[] PreparedRevisionDecode(ObjectVersionChain chain, SchemaStore schemas, Dictionary<uint, byte[]> strings, bool expectDistinct, ObjectVersionChain? tagChain);

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
            public static byte[] Decode(ObjectVersionChain chain, SchemaStore schemas,
                Dictionary<uint, byte[]> stringBodies, bool expectDistinct, ObjectVersionChain? tagChain) {
                var state = TypedObjectVersionReader.ReadDurable(chain, schemas, Owner.__DurableState.V1.Schema,
                    Owner.__DurableState.ReadBaseBodyV1, Owner.__DurableState.ApplyDeltaBodyV1);
                var strings = StringReadTable.Decode(stringBodies.Select(item => (new ObjectId(item.Key), (ReadOnlyMemory<byte>)item.Value)));
                Owner.__DurableState.ValidateStringReferences(in state, strings);
                string name = strings.ResolveString(state.Segment0Field1)!;
                string alias = strings.ResolveString(state.Segment0Field2)!;
                if (name != "x" || alias != "x" || ReferenceEquals(name, alias) == expectDistinct ||
                    !ReferenceEquals(strings.ResolveString(state.Segment0Field3), string.Empty))
                    throw new InvalidDataException("String content, sharing or Empty identity failed.");
                if (tagChain is not null) {
                    var tag = TypedObjectVersionReader.ReadDurable(tagChain, schemas, Tag.__DurableState.V1.Schema,
                        Tag.__DurableState.ReadBaseBodyV1, Tag.__DurableState.ApplyDeltaBodyV1);
                    Tag.__DurableState.ValidateStringReferences(in tag, strings);
                    if (!ReferenceEquals(strings.ResolveString(tag.Segment0Field1), alias) || tag.Segment1Field1 != 7)
                        throw new InvalidDataException("Inherited frozen fields or cross-object string sharing failed.");
                }
                return Owner.__DurableState.PrepareBaseBody(in state).Body.ToArray();
            }
        }
        """;
}
