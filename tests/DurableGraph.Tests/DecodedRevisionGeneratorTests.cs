using Atelia.DurableGraph.Schema;
using System.Reflection;
using Atelia.DurableGraph.Build;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Fact]
    public void DecodedRevisionColdReopenDispatchesMixedHistoricalModelsAndValidatesTheWholeTargetView() {
        using AncestryHistoryDirectory history = new();
        SchemaHistoryTool publisher = new();
        GeneratorTestRun initial = RunGenerator(FusedDeltaPreamble + """
            [DurableType("decoded.base", 1)]
            public abstract partial class OldBase : IDurableObject {
                [DurableField(9)] private string? _name;
            }
            [DurableType("decoded.leaf", 1)]
            public sealed partial class Leaf : OldBase {
                [DurableField(3)] private int _number;
                [DurableField(8)] private string? _alias;
            }
            """);
        AssertSchemaOnlyCompiles(initial);
        publisher.Publish(history.WriteManifest(initial), history.History);
        GeneratorTestRun current = RunGenerator(DecodedRevisionCurrentSource, history.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(current);
        Assembly assembly = EmitAndLoad(current.OutputCompilation);
        Assert.Null(assembly.GetType("FusedDelta.OldBase"));
        Type leaf = assembly.GetType("FusedDelta.Leaf")!;
        DurableSchema oldSchema = ReadSchemaOnly(leaf, 1);
        DurableSchema currentSchema = ReadSchemaOnly(leaf, 2);
        DurableSchema otherSchema = ReadSchemaOnly(assembly.GetType("FusedDelta.Other")!, 1);
        Assert.Equal(TypeTag.String, Assert.Single(oldSchema.BaseSchema!.Fields).TypeTag);
        Assert.Equal(TypeTag.Byte, Assert.Single(currentSchema.BaseSchema!.Fields).TypeTag);
        Type host = assembly.GetType("FusedDelta.Host")!;
        var register = host.GetMethod("Register")!.CreateDelegate<Func<bool, StateReaderRegistry>>();
        var check = host.GetMethod("Check")!.CreateDelegate<Action<DecodedRevision, int>>();
        var prepare = host.GetMethod("Prepare1")!.CreateDelegate<Func<byte[], byte[], PreparedDeltaBody>>();

        // Independent raw values seed the stored bodies; generated typed code prepares Deltas.
        // Stored IDs are fixture data only. The read API receives neither IDs nor per-object readers.
        byte[] original = [10, 2, 11]; // Old ancestor string ID 10; int 1; alias ID 11.
        PreparedDeltaBody firstDelta = prepare(original, [10, 4, 11]);
        PreparedDeltaBody secondDelta = prepare([10, 4, 11], [10, 4, 10]);
        Assert.Equal<byte>([2, 4], firstDelta.Body.ToArray());
        Assert.Equal<byte>([4, 10], secondDelta.Body.ToArray());
        static ObjectVersionRecord Durable(SchemaStore schemas, uint id, DurableSchema schema, byte[] body) =>
            ObjectVersionRecord.CreateBase(id, BaseObjectBodyCodec.Encode(schemas.RegisterRepresentations([ObjectLayout.ForDurable(schema)])[0], new(body)).Body);
        static ObjectVersionRecord Text(uint id, string value) =>
            ObjectVersionRecord.CreateBase(id, BaseObjectBodyCodec.EncodeString(StringPayloadCodec.PrepareBase(value)).Body);

        using RawBaseDirectory directory = new();
        const long rotationThreshold = 128;
        FrameAddress first, second, third, newBase, missingString, wrongKind, malformed;
        using (GraphResources segments = GraphResources.CreateLegacyNew(directory.Path, rotationThreshold)) {
            SchemaStore schemas = segments.Schemas;
            schemas.RegisterBatch([oldSchema, currentSchema, otherSchema]);
            using StateRevisionStore store = new(segments.Data);
            first = store.Append(StateRevision.CreateObjectHeadMapBase(null, [
                Durable(schemas, 1, oldSchema, original),
                Durable(schemas, 2, currentSchema, [7, 8, 12]),
                Text(10, "same"), Text(11, "same"), Text(12, ""), Text(13, ""),
                Durable(schemas, 99, otherSchema, [11, 18, 13]),
            ], []));
            second = store.Append(StateRevision.CreateObjectHeadMapDelta(first,
                [ObjectVersionRecord.CreateDelta(1, first, firstDelta.Body)], []));
            third = store.Append(StateRevision.CreateObjectHeadMapDelta(second,
                [ObjectVersionRecord.CreateDelta(1, second, secondDelta.Body)], []));
            newBase = store.Append(StateRevision.CreateObjectHeadMapDelta(third,
                [Durable(schemas, 1, oldSchema, [11, 6, 12])], []));

            // Only the removed CLR ancestor's slot refers to ID 10 in this parent.
            // Checking only current or leaf-declared slots would incorrectly accept these views.
            missingString = store.Append(StateRevision.CreateObjectHeadMapDelta(second, [], [10]));
            wrongKind = store.Append(StateRevision.CreateObjectHeadMapDelta(second,
                [Durable(schemas, 10, currentSchema, [7, 8, 12])], []));
            // ID 99 is last: all earlier objects have decoded before this trailing-byte error.
            malformed = store.Append(StateRevision.CreateObjectHeadMapDelta(third,
                [Durable(schemas, 99, otherSchema, [11, 18, 13, 0])], []));
            segments.Data.ConfirmDurable();
        }
        Assert.NotEqual(first, third);
        Array.Clear(original);
        firstDelta = null!;
        secondDelta = null!;
        Dictionary<string, byte[]> diskBefore = Directory.EnumerateFiles(directory.Path, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes);

        DecodedRevision[] retained;
        using (GraphResources reopened = GraphResources.OpenLegacyReadOnlyExisting(directory.Path)) {
            SchemaStore schemas = reopened.Schemas;
            using StateRevisionStore store = new(reopened.Data);
            StateReaderRegistry readers = register(true);
            retained = new[] { first, second, third, newBase }
                .Select(address => RevisionDecoder.Read(store, schemas, address, readers)).ToArray();
            for (int stage = 0; stage < retained.Length; stage++) {
                check(retained[stage], stage);
                Assert.Equal(new[] { first, second, third, newBase }[stage], retained[stage].RevisionAddress);
                Assert.Equal<uint>([1, 2, 10, 11, 12, 13, 99], retained[stage].Objects.Select(row => row.Id).Select(id => id.Value));
            }
            Assert.Equal(3, store.ReadObjectVersionChain(third, 1).Records.Count);
            Assert.Single(store.ReadObjectVersionChain(newBase, 1).Records);

            foreach (FrameAddress bad in new[] { missingString, wrongKind, malformed }) {
                DecodedRevision? delivered = null;
                Assert.Throws<InvalidDataException>(() => delivered = RevisionDecoder.Read(store, schemas, bad, readers));
                Assert.Null(delivered);
                check(retained[2], 2);
            }
            // The accepted SchemaSnapshot includes Other, but intentionally omit that code capability.
            // Missing ID 99's reader cannot fall back to the same-shaped historical Leaf DTO.
            StateReaderRegistry missingReader = register(false);
            DecodedRevision? partial = null;
            Assert.Throws<InvalidDataException>(() => partial = RevisionDecoder.Read(store, schemas, third, missingReader));
            Assert.Null(partial);
            check(RevisionDecoder.Read(store, schemas, first, readers), 0);
            check(retained[2], 2);
        }
        // Decoded DTOs and the identity table own their content beyond file/store lifetimes.
        for (int stage = 0; stage < retained.Length; stage++) check(retained[stage], stage);
        foreach ((string path, byte[] contents) in diskBefore) Assert.Equal(contents, File.ReadAllBytes(path));
        Assert.Equal(diskBefore.Count, Directory.EnumerateFiles(directory.Path, "*", SearchOption.AllDirectories).Count());
    }

    private static readonly string DecodedRevisionCurrentSource = FusedDeltaPreamble + """
        [DurableType("decoded.base", 2)]
        public abstract partial class NewBase : IDurableObject {
            [DurableField(2)] private byte _small;
        }
        [DurableType("decoded.leaf", 2)]
        public sealed partial class Leaf : NewBase {
            [DurableField(1)] private uint _number;
            [DurableField(8)] private string? _alias;
        }
        [DurableType("decoded.other", 1)]
        public sealed partial class Other : IDurableObject {
            [DurableField(1)] private string? _name;
            [DurableField(2)] private int _number;
            [DurableField(3)] private string? _empty;
        }
        public static class Host {
        """ + FusedDeltaHostMethods("Leaf", 1) + """
            public static Atelia.DurableGraph.Persistence.StateReaderRegistry Register(bool includeOther) {
                var readers = new Atelia.DurableGraph.Persistence.StateReaderRegistry();
                Leaf.__DurableState.RegisterReaders(readers);
                Leaf.__DurableState.RegisterReaders(readers); // Stable generated bindings are idempotent.
                if (includeOther) Other.__DurableState.RegisterReaders(readers);
                return readers;
            }
            public static void Check(Atelia.DurableGraph.Persistence.DecodedRevision view, int stage) {
                var old = view.GetRequired(new ObjectId(1)).GetState<Leaf.__DurableState.V1>();
                var current = view.GetRequired(new ObjectId(2)).GetState<Leaf.__DurableState.V2>();
                var other = view.GetRequired(new ObjectId(99)).GetState<Other.__DurableState.V1>();
                if (old.Segment0Field9.Value != (stage == 3 ? 11u : 10u) ||
                    old.Segment1Field3 != (stage == 0 ? 1 : stage == 3 ? 3 : 2) ||
                    old.Segment1Field8.Value != (stage < 2 ? 11u : stage == 2 ? 10u : 12u) ||
                    current.Segment0Field2 != 7 || current.Segment1Field1 != 8 || current.Segment1Field8.Value != 12 ||
                    other.Segment0Field1.Value != 11 || other.Segment0Field2 != 9 || other.Segment0Field3.Value != 13)
                    throw new Exception("Stored exact DTO values or dispatch differ.");
                if (!view.GetRequired(new ObjectId(1)).Schema.Equals(Leaf.__DurableState.V1.Schema) ||
                    !view.GetRequired(new ObjectId(2)).Schema.Equals(Leaf.__DurableState.V2.Schema) ||
                    !view.GetRequired(new ObjectId(99)).Schema.Equals(Other.__DurableState.V1.Schema))
                    throw new Exception("The stored exact Schema was replaced.");
                string shared = view.Strings.ResolveString(new ObjectId(10));
                string equal = view.Strings.ResolveString(new ObjectId(11));
                if (shared != "same" || equal != "same" || ReferenceEquals(shared, equal) ||
                    !ReferenceEquals(equal, view.Strings.ResolveString(other.Segment0Field1)) ||
                    !ReferenceEquals(view.Strings.ResolveString(new ObjectId(12)), string.Empty) ||
                    !ReferenceEquals(view.Strings.ResolveString(new ObjectId(13)), string.Empty) || view.Strings.ResolveString(new ObjectId(0)) != null)
                    throw new Exception("String value or identity differs.");
                foreach (uint id in new uint[] { 10, 11, 12, 13 })
                    if (!ReferenceEquals(view.GetRequired(new ObjectId(id)).StringContent, view.Strings.ResolveString(new ObjectId(id))))
                        throw new Exception("Rows and the reference table decoded different instances.");
                old = default; // GetState returns a copy; changing the local cannot alter the retained row.
                if (view.GetRequired(new ObjectId(1)).GetState<Leaf.__DurableState.V1>().Segment0Field9.IsNull)
                    throw new Exception("A returned DTO copy changed the stored state.");
            }
        }
        """;
}
