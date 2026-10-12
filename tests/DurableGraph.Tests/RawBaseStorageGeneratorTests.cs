using Atelia.DurableGraph.Persistence;
using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Reflection;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Fact]
    public void RawBaseStorageColdReopenFeedsGeneratedDtoAndStringDecodersFromExactRevisions() {
        GeneratorTestRun run = RunGenerator(RawBaseStorageSource);
        AssertSchemaOnlyCompiles(run);
        Assembly assembly = EmitAndLoad(run.OutputCompilation);
        Type host = assembly.GetType("RawBaseWitness.Host")!;
        var capture = host.GetMethod("Capture")!.CreateDelegate<RawBaseCapture>();
        var validate = host.GetMethod("Validate")!.CreateDelegate<RawBaseValidate>();
        var input = capture();

        // Only these explicit fixture metadata survive alongside the selected addresses.
        // Neither the file nor this witness claims to contain a persistent type/root manifest.
        var metadata = input.First.Select(record => (record.Id, record.IsString, record.Schema)).ToArray();
        uint firstOwner = input.Roots[0];
        uint secondOwner = input.Roots[1];
        Assert.Equal(new[] { firstOwner, secondOwner, firstOwner, 0u }, input.Roots);
        Assert.Equal(5, input.First.Length); // Two owners, two distinct equal strings, one canonical empty.
        Assert.Equal(metadata, input.Second.Select(record => (record.Id, record.IsString, record.Schema)).ToArray());
        foreach (var original in input.First.Where(record => record.Id != firstOwner)) {
            Assert.Equal(original.Body, input.Second.Single(record => record.Id == original.Id).Body);
        }

        using RawBaseDirectory directory = new();
        const long rotationThreshold = 128;
        FrameAddress first;
        FrameAddress second;
        FrameAddress missingString;
        FrameAddress wrongKind;
        using (GraphResources segments = GraphResources.CreateLegacyNew(directory.Path, rotationThreshold)) {
            using StateRevisionStore store = new(segments.Data);
            first = store.Append(StateRevision.CreateObjectHeadMapBase(null,
                input.First.Reverse().Select(record => ObjectVersionRecord.CreateBase(record.Id, record.Body)), []));
            second = store.Append(StateRevision.CreateObjectHeadMapDelta(first,
                [ObjectVersionRecord.CreateBase(firstOwner, input.Second.Single(record => record.Id == firstOwner).Body)], []));

            // Storage deliberately accepts opaque bytes. Typed validation must reject these
            // persisted corrupt references, including a failure after the first owner was valid.
            byte[] missing = input.Second.Single(record => record.Id == secondOwner).Body.ToArray();
            Assert.InRange(missing[0], (byte)1, (byte)127); // The fixture's first reference is one byte.
            missing[0] = 127;
            missingString = store.Append(StateRevision.CreateObjectHeadMapDelta(second,
                [ObjectVersionRecord.CreateBase(secondOwner, missing)], []));
            byte[] nonString = input.Second.Single(record => record.Id == firstOwner).Body.ToArray();
            nonString[0] = checked((byte)secondOwner);
            wrongKind = store.Append(StateRevision.CreateObjectHeadMapDelta(second,
                [ObjectVersionRecord.CreateBase(firstOwner, nonString)], []));
            segments.Data.ConfirmDurable();
        }
        Assert.NotEqual(first, second);

        // Clear every supplied body before cold reopening. The decoder obtains content only by
        // ReadObjectBaseBody; neither CaptureSession nor any source object is an input to loading.
        foreach (var record in input.First.Concat(input.Second)) {
            Array.Clear(record.Body);
        }
        using GraphResources reopened = GraphResources.OpenLegacyReadOnlyExisting(directory.Path);
        using StateRevisionStore cold = new(reopened.Data);
        Dictionary<uint, byte[]> ReadBodies(FrameAddress revision) => cold.ReadLiveObjectHeadMap(revision)
            .Keys.Reverse().ToDictionary(id => id, id => cold.ReadObjectBaseBody(revision, id));

        Dictionary<uint, byte[]> oldBodies = ReadBodies(first);
        Dictionary<uint, byte[]> newBodies = ReadBodies(second);
        Assert.True(validate(input.Roots, metadata, oldBodies, 7));
        Assert.True(validate(input.Roots, metadata.Reverse().ToArray(), newBodies, 8));
        Assert.False(oldBodies[firstOwner].SequenceEqual(newBodies[firstOwner]));
        Assert.Equal(first, cold.ReadLiveObjectHeadMap(second)[secondOwner]);
        foreach (var item in metadata.Where(item => item.IsString)) {
            Assert.Equal(first, cold.ReadLiveObjectHeadMap(second)[item.Id]);
            Assert.Equal(oldBodies[item.Id], newBodies[item.Id]);
        }

        Assert.Throws<InvalidDataException>(() => validate(input.Roots, metadata, ReadBodies(missingString), 8));
        Assert.Throws<InvalidDataException>(() => validate(input.Roots, metadata, ReadBodies(wrongKind), 8));
        DurableSchema originalSchema = metadata.Single(item => item.Id == firstOwner).Schema!;
        DurableSchema wrongVersion = new(originalSchema.SchemaId, originalSchema.Version + 1,
            originalSchema.Fields.ToArray(), originalSchema.BaseSchema);
        var mismatchedSchema = metadata.Select(item => item.Id == firstOwner
            ? (item.Id, item.IsString, (DurableSchema?)wrongVersion) : item).ToArray();
        Assert.Throws<InvalidDataException>(() => validate(input.Roots, mismatchedSchema, newBodies, 8));
        uint stringId = metadata.First(item => item.IsString).Id;
        Assert.Throws<InvalidDataException>(() => validate([stringId, secondOwner, firstOwner, 0], metadata, newBodies, 8));
        Assert.True(validate(input.Roots, metadata, oldBodies, 7));
        Assert.True(validate(input.Roots, metadata, newBodies, 8));
    }

    private delegate (
        uint[] Roots,
        (uint Id, bool IsString, DurableSchema? Schema, byte[] Body)[] First,
        (uint Id, bool IsString, DurableSchema? Schema, byte[] Body)[] Second) RawBaseCapture();

    private delegate bool RawBaseValidate(
        uint[] roots,
        (uint Id, bool IsString, DurableSchema? Schema)[] metadata,
        Dictionary<uint, byte[]> bodies,
        uint firstScore);

    private sealed class RawBaseDirectory : IDisposable {
        private readonly string _temporaryRoot = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
        public string Path { get; }

        public RawBaseDirectory() {
            Path = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                _temporaryRoot, $"durable-graph-raw-base-generated-{Guid.NewGuid():N}"));
        }

        public void Dispose() {
            string parent = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!;
            if (!string.Equals(System.IO.Path.TrimEndingDirectorySeparator(_temporaryRoot), parent,
                StringComparison.OrdinalIgnoreCase) ||
                !System.IO.Path.GetFileName(Path).StartsWith("durable-graph-raw-base-generated-", StringComparison.Ordinal)) {
                throw new InvalidOperationException("Refusing to delete outside this fixture's temporary directory.");
            }
            if (Directory.Exists(Path)) {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private const string RawBaseStorageSource = """
        using Atelia.Binary;
        using System;
        using System.Buffers;
        using System.Collections.Generic;
        using System.IO;
        using System.Linq;
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        using Atelia.DurableGraph.Serialization;
        namespace RawBaseWitness;

        [DurableType("raw-base.owner", 1)]
        public sealed partial class Owner : IDurableObject {
            [DurableField(1)] private string? _name;
            [DurableField(2)] private string? _alias;
            [DurableField(3)] private string? _optional = null;
            [DurableField(4)] private string _empty = string.Empty;
            [DurableField(5)] private uint _score;
            public Owner(string name, string alias, uint score) {
                _name = name; _alias = alias; _score = score;
            }
            public void Change(string name, uint score) { _name = name; _score = score; }
        }

        public static class Host {
            public static (uint[] Roots,
                (uint Id, bool IsString, DurableSchema? Schema, byte[] Body)[] First,
                (uint Id, bool IsString, DurableSchema? Schema, byte[] Body)[] Second) Capture() {
                string shared = new string(new[] { 'A', 'd', 'a' });
                string equal = new string(new[] { 'A', 'd', 'a' });
                if (ReferenceEquals(shared, equal)) throw new InvalidOperationException("Fixture identity.");
                var first = new Owner(shared, shared, 7);
                var second = new Owner(equal, shared, 11);
                var session = new CaptureSession();
                CapturedGraph CaptureOwners() {
                    var context = session.BeginCapture();
                    Owner.__DurableState.AddRoot(context, first);
                    Owner.__DurableState.AddRoot(context, second);
                    Owner.__DurableState.AddRoot(context, first);
                    Owner.__DurableState.AddRoot(context, null);
                    return context.Seal();
                }
                var firstGraph = CaptureOwners();
                first.Change("changed after Seal, before encoding", 90);
                var firstBytes = Encode(firstGraph);
                session.Accept(firstGraph); // Fixture-only in-memory baseline, not durable publication.
                first.Change(shared, 8);
                var secondGraph = CaptureOwners();
                first.Change("changed again before encoding", 91);
                second.Change("second also changed after Seal", 92);
                var secondBytes = Encode(secondGraph);
                session.Discard(secondGraph);
                return (firstGraph.RootIds.Select(id => id.Value).ToArray(), firstBytes, secondBytes);
            }

            private static (uint Id, bool IsString, DurableSchema? Schema, byte[] Body)[] Encode(CapturedGraph graph) {
                return graph.Objects.Select(entry => {
                    var buffer = new ArrayBufferWriter<byte>();
                    var writer = new BareValueWriter(buffer);
                    bool isString = entry.Kind == ObjectStateKind.String;
                    if (isString) {
                        writer.WriteString(entry.StringContent);
                    } else {
                        var state = entry.GetState<Owner.__DurableState.V1>();
                        Owner.__DurableState.WriteBaseBody(ref writer, in state);
                    }
                    return (entry.Id.Value, isString, entry.Schema, buffer.WrittenSpan.ToArray());
                }).ToArray();
            }

            // Content arrives exclusively from the reopened Storage file. Metadata are explicit
            // test inputs: this typed witness does not introduce a persistent type registry.
            public static bool Validate(uint[] roots,
                (uint Id, bool IsString, DurableSchema? Schema)[] metadata,
                Dictionary<uint, byte[]> bodies, uint firstScore) {
                var directory = new Dictionary<uint, bool>();
                foreach (var entry in metadata) {
                    if (entry.Id == 0 || !directory.TryAdd(entry.Id, entry.IsString) || !bodies.ContainsKey(entry.Id))
                        throw new InvalidDataException("Invalid fixture directory.");
                    if (entry.IsString ? entry.Schema is not null : !Owner.__DurableState.V1.Schema.Equals(entry.Schema))
                        throw new InvalidDataException("Typed decoding requires exact Schema metadata.");
                }
                if (directory.Count != bodies.Count) throw new InvalidDataException("Directory must cover every body.");
                foreach (uint root in roots) {
                    if (root != 0 && (!directory.TryGetValue(root, out bool isString) || isString))
                        throw new InvalidDataException("Unknown or non-durable fixture root.");
                }
                var strings = StringReadTable.Decode(metadata.Where(entry => entry.IsString)
                    .Select(entry => (new ObjectId(entry.Id), (ReadOnlyMemory<byte>)bodies[entry.Id])));
                var owners = new Dictionary<uint, Owner.__DurableState.V1>();
                foreach (var entry in metadata.Where(entry => !entry.IsString)) {
                    var reader = new BareValueReader(bodies[entry.Id]);
                    var state = Owner.__DurableState.ReadBaseBodyV1(ref reader);
                    reader.EnsureFullyConsumed();
                    Owner.__DurableState.ValidateStringReferences(in state, strings);
                    owners.Add(entry.Id, state);
                }
                var first = owners[roots[0]];
                var second = owners[roots[1]];
                string? shared = strings.ResolveString(first.Segment0Field1);
                string? equal = strings.ResolveString(second.Segment0Field1);
                return roots.Length == 4 && roots[0] == roots[2] && roots[3] == 0 && owners.Count == 2 &&
                    first.Segment0Field5 == firstScore && second.Segment0Field5 == 11 &&
                    shared == "Ada" && equal == "Ada" && !ReferenceEquals(shared, equal) &&
                    ReferenceEquals(shared, strings.ResolveString(first.Segment0Field2)) &&
                    ReferenceEquals(shared, strings.ResolveString(second.Segment0Field2)) &&
                    strings.ResolveString(first.Segment0Field3) is null && strings.ResolveString(second.Segment0Field3) is null &&
                    !first.Segment0Field4.IsNull && first.Segment0Field4 == second.Segment0Field4 &&
                    ReferenceEquals(strings.ResolveString(first.Segment0Field4), string.Empty);
            }
        }
        """;
}
