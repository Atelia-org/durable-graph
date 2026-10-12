using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Buffers;
using System.Reflection;
using Atelia.DurableGraph.Build;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Fact]
    public void PersistedDeltaChainColdReopenReconstructsFrozenCaptureAndValidatesTargetReferences() {
        GeneratorTestRun run = RunGenerator(PersistedDeltaCaptureSource);
        AssertSchemaOnlyCompiles(run);
        Type host = EmitAndLoad(run.OutputCompilation).GetType("PersistedCapture.Host")!;
        var capture = host.GetMethod("Capture")!.CreateDelegate<PersistedCapture>();
        var decode = host.GetMethod("Decode")!.CreateDelegate<PersistedDecode>();
        var input = capture(); // All domain instances, candidate DTOs and CaptureSession stay inside this call.
        DurableSchema schema = input.Schema;
        uint ownerId = input.Owner.Value;
        uint nameId = input.Name.Value;
        uint aliasId = input.Alias.Value;
        uint emptyId = input.Empty.Value;
        byte[][] expected = [ExpectedCapturedBody(nameId, nameId, emptyId, 1),
            ExpectedCapturedBody(nameId, aliasId, emptyId, 2), ExpectedCapturedBody(nameId, aliasId, emptyId, 3)];
        Assert.Equal(expected[0], input.Base);
        Assert.True(input.First.HasChanges);
        Assert.True(input.Second.HasChanges);
        Assert.Equal<byte>([0x0A, checked((byte)aliasId), 4], input.First.Body.ToArray());
        Assert.Equal<byte>([0x08, 6], input.Second.Body.ToArray());

        using RawBaseDirectory directory = new();
        using RawBaseDirectory schemaDirectory = new();
        Directory.CreateDirectory(schemaDirectory.Path);
        const long rotationThreshold = 128;
        FrameAddress first, second, third, repeated, missingString, wrongKind, malformed;
        using (GraphResources segments = GraphResources.CreateLegacyNew(directory.Path, rotationThreshold)) {
            SchemaStore schemas = segments.Schemas;
            using StateRevisionStore store = new(segments.Data);
            schemas.RegisterBatch([schema]);
            first = store.Append(StateRevision.CreateObjectHeadMapBase(null,
                input.Strings.Where(item => item.Id.Value != aliasId).Select(item => ObjectVersionRecord.CreateBase(item.Id.Value, BaseObjectBodyCodec.EncodeString(new(item.Body)).Body))
                    .Append(ObjectVersionRecord.CreateBase(ownerId, BaseObjectBodyCodec.Encode(schemas.RegisterRepresentations([ObjectLayout.ForDurable(schema)])[0], new(input.Base)).Body)), []));
            second = store.Append(StateRevision.CreateObjectHeadMapDelta(first,
                [ObjectVersionRecord.CreateDelta(ownerId, first, input.First.Body),
                    ObjectVersionRecord.CreateBase(aliasId, BaseObjectBodyCodec.EncodeString(new(input.Strings.Single(item => item.Id.Value == aliasId).Body)).Body)], []));
            third = store.Append(StateRevision.CreateObjectHeadMapDelta(second,
                [ObjectVersionRecord.CreateDelta(ownerId, second, input.Second.Body)], []));

            // Reuse the same prepared bytes on another branch; no serialization is repeated.
            repeated = store.Append(StateRevision.CreateObjectHeadMapDelta(first,
                [ObjectVersionRecord.CreateDelta(ownerId, first, input.First.Body),
                    ObjectVersionRecord.CreateBase(aliasId, BaseObjectBodyCodec.EncodeString(new(input.Strings.Single(item => item.Id.Value == aliasId).Body)).Body)], []));
            Assert.Equal(input.First.Body.ToArray(), store.ReadObjectVersionChain(repeated, ownerId).Records[^1].Record.Body.ToArray());
            missingString = store.Append(StateRevision.CreateObjectHeadMapDelta(third, [], [nameId]));
            wrongKind = store.Append(StateRevision.CreateObjectHeadMapDelta(third,
                [ObjectVersionRecord.CreateBase(nameId, BaseObjectBodyCodec.Encode(schemas.RegisterRepresentations([ObjectLayout.ForDurable(schema)])[0], new(expected[2])).Body)], []));
            malformed = store.Append(StateRevision.CreateObjectHeadMapDelta(third,
                [ObjectVersionRecord.CreateDelta(ownerId, third, new byte[] { 8, 8, 0 })], []));
            segments.Data.ConfirmDurable();
        }
        Assert.NotEqual(first, second);
        Assert.NotEqual(second, third);
        Array.Clear(input.Base);
        foreach (var item in input.Strings) Array.Clear(item.Body);
        input = default; // Drop prepared bodies as well; only query IDs, static reader selection and independent expectations survive.
        using GraphResources reopened = GraphResources.OpenLegacyReadOnlyExisting(directory.Path);

        SchemaStore coldSchemas = reopened.Schemas;
        using StateRevisionStore cold = new(reopened.Data);
        byte[] Load(FrameAddress revision, SchemaStore? registry = null, DurableSchema? expectedSchema = null) {
            ObjectVersionChain chain = cold.ReadObjectVersionChain(revision, ownerId);
            Dictionary<uint, byte[]> strings = [];
            foreach ((uint id, FrameAddress address) in cold.ReadLiveObjectHeadMap(revision)) {
                var objectChain = cold.ReadObjectVersionChain(revision, id);
                var stored = BaseObjectBodyCodec.Decode(objectChain.Records[0].Record.Body, coldSchemas);
                if (stored.Kind == ObjectStateKind.String) {
                    TypedObjectVersionReader.ReadString(objectChain);
                    strings.Add(id, stored.Body.ToArray());
                }
            }
            return decode(chain, registry ?? coldSchemas, expectedSchema ?? schema, strings);
        }
        Assert.Equal(expected[0], Load(first));
        Assert.Equal(expected[1], Load(second));
        Assert.Equal(expected[2], Load(third));
        Assert.Equal(expected[1], Load(repeated));
        ObjectVersionChain oldest = cold.ReadObjectVersionChain(first, ownerId);
        ObjectVersionChain latest = cold.ReadObjectVersionChain(third, ownerId);
        Assert.Single(oldest.Records);
        Assert.Equal(new[] { first, second, third }, latest.Records.Select(entry => entry.ContainingRevisionAddress));
        Assert.Equal(third, latest.ObjectHeadAddress);
        Assert.Equal(ownerId, latest.ObjectId);
        Assert.Equal(latest.Records.Sum(entry => (long)entry.ObjectVersionPayloadBytes), latest.ReconstructionPayloadBytes);
        Assert.True(latest.ReconstructionPayloadBytes > oldest.ReconstructionPayloadBytes);

        // The Base ID selects one persistent exact representation for the whole chain.
        // Neither missing registration nor a mismatched reader may enter a body callback.
        using (GraphResources missingResources = GraphResources.CreateLegacyNew(Path.Combine(schemaDirectory.Path, "missing"))) {
            SchemaStore missing = missingResources.Schemas;
            int calls = (int)host.GetField("DecodeCalls")!.GetValue(null)!;
            Assert.Throws<InvalidDataException>(() => Load(third, missing));
            Assert.Equal(calls, (int)host.GetField("DecodeCalls")!.GetValue(null)!);
        }
        // An independent repository can hold the same logical keys with a different
        // ancestor definition. Its own registered Base must not authorize this reader.
        string alternateStatePath = Path.Combine(schemaDirectory.Path, "different-definition-state");
        DurableSchema ancestor = schema.BaseSchema!;
        var differentAncestor = new DurableSchema(ancestor.SchemaId, ancestor.Version, []);
        FrameAddress alternateRevision;
        using (GraphResources alternateSegments = GraphResources.CreateLegacyNew(alternateStatePath)) {
            SchemaStore alternate = alternateSegments.Schemas;
            DurableSchema differentSchema = new(schema.SchemaId, schema.Version, schema.Fields.ToArray(), differentAncestor);
            RepresentationId alternateId = alternate.RegisterRepresentations([ObjectLayout.ForDurable(differentSchema)])[0];
            using StateRevisionStore alternateStates = new(alternateSegments.Data);
            alternateRevision = alternateStates.Append(StateRevision.CreateObjectHeadMapBase(null,
                [ObjectVersionRecord.CreateBase(ownerId, BaseObjectBodyCodec.Encode(alternateId, new(expected[0])).Body)], []));
            alternateSegments.Data.ConfirmDurable();
        }
        using (GraphResources alternateSegments = GraphResources.OpenLegacyReadOnlyExisting(alternateStatePath)) {
            SchemaStore alternate = alternateSegments.Schemas;
            using StateRevisionStore alternateStates = new(alternateSegments.Data);
            ObjectVersionChain alternateChain = alternateStates.ReadObjectVersionChain(alternateRevision, ownerId);
            int calls = (int)host.GetField("DecodeCalls")!.GetValue(null)!;
            Assert.Throws<InvalidDataException>(() => decode(alternateChain, alternate, schema, []));
            Assert.Equal(calls, (int)host.GetField("DecodeCalls")!.GetValue(null)!);
        }
        var wrongSchema = new DurableSchema(schema.SchemaId, 2, schema.Fields.ToArray(), schema.BaseSchema);
        int beforeMismatch = (int)host.GetField("DecodeCalls")!.GetValue(null)!;
        Assert.Throws<InvalidDataException>(() => Load(third, expectedSchema: wrongSchema));
        Assert.Equal(beforeMismatch, (int)host.GetField("DecodeCalls")!.GetValue(null)!);
        foreach (FrameAddress bad in new[] { missingString, wrongKind, malformed }) {
            byte[]? delivered = null;
            Assert.Throws<InvalidDataException>(() => delivered = Load(bad));
            Assert.Null(delivered); // A late Apply/full-consumption/reference failure returns no partial result.
        }
        Assert.Equal(expected[0], Load(first));
        Assert.Equal(expected[2], Load(third));
    }

    [Fact]
    public void PersistedHistoricalDeltaChainUsesRemovedAncestorLayoutAndPersistedExactSchema() {
        using AncestryHistoryDirectory files = new();
        SchemaHistoryTool publisher = new();
        GeneratorTestRun initial = RunGenerator(FusedDeltaPreamble + """
            [DurableType("persisted.base", 1)]
            public abstract partial class OldBase : IDurableObject { [DurableField(99)] private int _number; }
            [DurableType("persisted.leaf", 1)]
            public sealed partial class Leaf : OldBase { [DurableField(99)] private bool _flag; }
            """);
        AssertSchemaOnlyCompiles(initial);
        publisher.Publish(files.WriteManifest(initial), files.History);
        string source = FusedDeltaPreamble + """
            [DurableType("persisted.base", 2)]
            public abstract partial class NewBase : IDurableObject { [DurableField(2)] private byte _small; }
            [DurableType("persisted.leaf", 2)]
            public sealed partial class Leaf : NewBase { [DurableField(1)] private uint _number; }
            public static class Host {
            """ + FusedDeltaHostMethods("Leaf", 1) + FusedDeltaHostMethods("Leaf", 2) + """
            public static int DecodeCalls;
            private static Leaf.__DurableState.V1 ReadOld(ref BareValueReader reader) {
                DecodeCalls++;
                return Leaf.__DurableState.ReadBaseBodyV1(ref reader);
            }
            public static byte[] DecodeStored(Atelia.DurableGraph.Storage.ObjectVersionChain chain,
                Atelia.DurableGraph.Persistence.SchemaStore schemas, DurableSchema expected) {
                var state = Atelia.DurableGraph.Persistence.TypedObjectVersionReader.ReadDurable(
                    chain, schemas, expected, ReadOld, Leaf.__DurableState.ApplyDeltaBodyV1);
                return Leaf.__DurableState.PrepareBaseBody(in state).Body.ToArray();
            }
            }
            """;
        GeneratorTestRun current = RunGenerator(source, files.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(current);
        Assembly assembly = EmitAndLoad(current.OutputCompilation);
        Assert.Null(assembly.GetType("FusedDelta.OldBase"));
        Type leaf = assembly.GetType("FusedDelta.Leaf")!;
        DurableSchema oldSchema = ReadSchemaOnly(leaf, 1);
        DurableSchema newSchema = ReadSchemaOnly(leaf, 2);
        Assert.Equal(TypeTag.Int32, Assert.Single(oldSchema.BaseSchema!.Fields).TypeTag);
        Assert.Equal(TypeTag.Byte, Assert.Single(newSchema.BaseSchema!.Fields).TypeTag);
        Type host = assembly.GetType("FusedDelta.Host")!;
        var prepare = host.GetMethod("Prepare1")!.CreateDelegate<Func<byte[], byte[], PreparedDeltaBody>>();
        var decode = host.GetMethod("DecodeStored")!.CreateDelegate<Func<ObjectVersionChain, SchemaStore, DurableSchema, byte[]>>();
        byte[] baseBytes = [2, 0]; // Old ancestor int=1, leaf bool=false.
        PreparedDeltaBody delta1 = prepare(baseBytes, [2, 1]);
        PreparedDeltaBody delta2 = prepare([2, 1], [4, 1]);
        Assert.Equal<byte>([2, 1], delta1.Body.ToArray());
        Assert.Equal<byte>([1, 4], delta2.Body.ToArray());
        using RawBaseDirectory directory = new();
        const long rotationThreshold = 128;
        FrameAddress first, second, third;
        using (GraphResources segments = GraphResources.CreateLegacyNew(directory.Path, rotationThreshold)) {
            SchemaStore schemas = segments.Schemas;
            using StateRevisionStore store = new(segments.Data);
            schemas.RegisterBatch([oldSchema, newSchema]);
            first = store.Append(StateRevision.CreateObjectHeadMapBase(null, [ObjectVersionRecord.CreateBase(1,
                BaseObjectBodyCodec.Encode(schemas.RegisterRepresentations([ObjectLayout.ForDurable(oldSchema)])[0], new(baseBytes)).Body)], []));
            second = store.Append(StateRevision.CreateObjectHeadMapDelta(first, [ObjectVersionRecord.CreateDelta(1, first, delta1.Body)], []));
            third = store.Append(StateRevision.CreateObjectHeadMapDelta(second, [ObjectVersionRecord.CreateDelta(1, second, delta2.Body)], []));
            segments.Data.ConfirmDurable();
        }
        Array.Clear(baseBytes);
        delta1 = null!;
        delta2 = null!;
        Assert.NotEqual(first, third);
        using GraphResources reopened = GraphResources.OpenLegacyReadOnlyExisting(directory.Path);
        SchemaStore coldSchemas = reopened.Schemas;
        using StateRevisionStore cold = new(reopened.Data);
        byte[] Load(FrameAddress revision) {
            ObjectVersionChain chain = cold.ReadObjectVersionChain(revision, 1);
            return decode(chain, coldSchemas, oldSchema);
        }
        Assert.Equal(oldSchema, coldSchemas.GetRequired(oldSchema.SchemaId, oldSchema.Version));
        Assert.Equal(TypeTag.Int32, Assert.Single(coldSchemas.GetRequired(oldSchema.SchemaId, 1).BaseSchema!.Fields).TypeTag);
        Assert.Equal<byte>([2, 1], Load(second));
        Assert.Equal<byte>([4, 1], Load(third));
        int calls = (int)host.GetField("DecodeCalls")!.GetValue(null)!;
        Assert.Throws<InvalidDataException>(() => decode(cold.ReadObjectVersionChain(third, 1), coldSchemas, newSchema));
        Assert.Equal(calls, (int)host.GetField("DecodeCalls")!.GetValue(null)!);
        Assert.Equal<byte>([4, 1], Load(third));
    }

    private static byte[] ExpectedCapturedBody(uint name, uint alias, uint empty, int score) {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new BareValueWriter(buffer);
        writer.WriteVarUInt32(name);
        writer.WriteVarUInt32(alias);
        writer.WriteVarUInt32(empty);
        writer.WriteVarInt32(score);
        return buffer.WrittenSpan.ToArray();
    }

    private delegate (ObjectId Owner, ObjectId Name, ObjectId Alias, ObjectId Empty, DurableSchema Schema,
        (ObjectId Id, byte[] Body)[] Strings, byte[] Base, PreparedDeltaBody First, PreparedDeltaBody Second) PersistedCapture();
    private delegate byte[] PersistedDecode(ObjectVersionChain chain, SchemaStore schemas, DurableSchema expected, Dictionary<uint, byte[]> strings);

    private const string PersistedDeltaCaptureSource = """
        using Atelia.Binary;
        using System;
        using System.Buffers;
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
        namespace PersistedCapture;
        [DurableType("persisted.capture.base", 1)]
        public abstract partial class Base : IDurableObject {
            [DurableField(1)] private string _name;
            protected Base(string name) { _name = name; }
            public void Rename(string name) { _name = name; }
        }
        [DurableType("persisted.capture.leaf", 1)]
        public sealed partial class Leaf : Base {
            [DurableField(1)] private string _alias;
            [DurableField(9)] private string _empty = string.Empty;
            [DurableField(20)] private int _score = 1;
            public Leaf(string shared) : base(shared) { _alias = shared; }
            public void Change(string alias, int score) { _alias = alias; _score = score; }
        }
        public static class Host {
            public static int DecodeCalls;
            private static byte[] Write(in Leaf.__DurableState.V1 state) {
                var buffer = new ArrayBufferWriter<byte>();
                var writer = new BareValueWriter(buffer);
                Leaf.__DurableState.WriteBaseBody(ref writer, in state);
                return buffer.WrittenSpan.ToArray();
            }
            public static (ObjectId Owner, ObjectId Name, ObjectId Alias, ObjectId Empty, DurableSchema Schema,
                (ObjectId Id, byte[] Body)[] Strings, byte[] Base, PreparedDeltaBody First, PreparedDeltaBody Second) Capture() {
                string shared = new string(new[] { 'x' });
                string equal = new string(new[] { 'x' });
                if (ReferenceEquals(shared, equal)) throw new Exception("Distinct fixture strings.");
                var owner = new Leaf(shared);
                var session = new CaptureSession();
                CapturedGraph Capture() {
                    var context = session.BeginCapture();
                    Leaf.__DurableState.AddRoot(context, owner);
                    return context.Seal();
                }
                var first = Capture();
                owner.Rename("mutation after Seal"); owner.Change("mutation", 90);
                var prior = first.Objects.Single(item => item.Kind == ObjectStateKind.Durable).GetState<Leaf.__DurableState.V1>();
                session.Accept(first); // Fixture's in-memory baseline only; not a durable publication.
                owner.Rename(shared); owner.Change(equal, 2);
                var second = Capture();
                owner.Rename("mutation after Seal"); owner.Change("mutation", 91);
                var middle = second.Objects.Single(item => item.Kind == ObjectStateKind.Durable).GetState<Leaf.__DurableState.V1>();
                var delta1 = Leaf.__DurableState.PrepareDeltaBody(in prior, in middle);
                session.Accept(second);
                owner.Rename(shared); owner.Change(equal, 3);
                var third = Capture();
                owner.Rename("mutation after Seal"); owner.Change("mutation", 92);
                var current = third.Objects.Single(item => item.Kind == ObjectStateKind.Durable).GetState<Leaf.__DurableState.V1>();
                var delta2 = Leaf.__DurableState.PrepareDeltaBody(in middle, in current);
                var strings = third.Objects.Where(item => item.Kind == ObjectStateKind.String).Select(item => {
                    var buffer = new ArrayBufferWriter<byte>();
                    var writer = new BareValueWriter(buffer);
                    writer.WriteString(item.StringContent);
                    return (item.Id, buffer.WrittenSpan.ToArray());
                }).ToArray();
                session.Discard(third);
                return (first.RootIds[0], prior.Segment0Field1, middle.Segment1Field1, prior.Segment1Field9,
                    Leaf.__DurableState.V1.Schema, strings, Write(in prior), delta1, delta2);
            }
            private static Leaf.__DurableState.V1 ReadBase(ref BareValueReader reader) {
                DecodeCalls++;
                return Leaf.__DurableState.ReadBaseBodyV1(ref reader);
            }
            public static byte[] Decode(ObjectVersionChain chain, SchemaStore schemas, DurableSchema expected,
                Dictionary<uint, byte[]> stringBodies) {
                var state = TypedObjectVersionReader.ReadDurable(chain, schemas, expected,
                    ReadBase, Leaf.__DurableState.ApplyDeltaBodyV1);
                var strings = StringReadTable.Decode(stringBodies.Select(item => (new ObjectId(item.Key), (ReadOnlyMemory<byte>)item.Value)));
                Leaf.__DurableState.ValidateStringReferences(in state, strings);
                string name = strings.ResolveString(state.Segment0Field1)!;
                string alias = strings.ResolveString(state.Segment1Field1)!;
                if (name != "x" || alias != "x" || ReferenceEquals(name, alias) != (state.Segment1Field20 == 1) ||
                    !ReferenceEquals(strings.ResolveString(state.Segment1Field9), string.Empty))
                    throw new InvalidDataException("Restored identity or frozen values.");
                return Write(in state); // No result escapes before every body and reference check succeeds.
            }
        }
        """;
}
