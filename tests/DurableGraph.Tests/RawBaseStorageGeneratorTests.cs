using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Persistence.MetaBootstrapP0;
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
        var validateDecoded = host.GetMethod("ValidateDecoded")!.CreateDelegate<Func<uint[], DecodedRevision, uint, bool>>();
        var models = host.GetMethod("Models")!.CreateDelegate<Func<StateModelRegistry>>()();
        var input = capture();

        // The graph persists the first root and exact type metadata. The capture's multi-root
        // list remains an explicit query fixture, without a multi-root product policy.
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
        FrameAddress schemaRoot;
        using (GraphResources resources = GraphResources.CreateNew(directory.Path, rotationThreshold))
        using (P0Repository repository = new(resources)) {
            P0GraphStore store = repository.Store;
            P0CatalogWorkspace catalog = new(repository.EnsureEditingAllowed);
            foreach (DurableSchema schema in metadata.Where(item => !item.IsString).Select(item => item.Schema!).Distinct()) {
                catalog.Set(schema);
            }
            P0PreparedCatalog preparedCatalog = catalog.Prepare();
            ObjectVersionRecord Encode((uint Id, bool IsString, DurableSchema? Schema, byte[] Body) record,
                byte[]? body = null) => ObjectVersionRecord.CreateBase(record.Id, P0TypedBodyCodec.Encode(
                    record.IsString ? ObjectLayout.String : ObjectLayout.ForDurable(record.Schema!),
                    new(body ?? record.Body), preparedCatalog.View));

            // These captured SG bodies explicitly select Base; this is not a planner-policy witness.
            using (P0PendingSave pending = repository.Prepare("main", P0GraphKind.State,
                new(null, new(firstOwner), input.First.Reverse().Select(record => Encode(record))), preparedCatalog.Write)) {
                P0SaveResult saved = repository.Commit(pending, installMeta: preparedCatalog.Install,
                    prepareMeta: preparedCatalog.PrepareInstall);
                first = saved.Business.Address;
                schemaRoot = saved.Meta!.Address;
                Assert.Equal(new ObjectId(firstOwner), saved.Business.RootId);
            }
            using (P0PendingSave pending = repository.Prepare("main", P0GraphKind.State,
                new(first, new(firstOwner), [Encode(input.Second.Single(record => record.Id == firstOwner))]))) {
                P0SaveResult saved = repository.Commit(pending);
                second = saved.Business.Address;
                Assert.Equal(new ObjectId(firstOwner), saved.Business.RootId);
                Assert.Equal(schemaRoot, saved.Business.SchemaRoot);
            }
            Assert.Equal(second, repository.GetHead("main"));
            Assert.Equal(schemaRoot, repository.SharedSchemaRoot);
            Assert.True(validateDecoded(input.Roots, DecodedRevision.ReadSplit(store, first, models), 7));
            Assert.True(validateDecoded(input.Roots, DecodedRevision.ReadSplit(store, second, models), 8));

            // Unpublished internal corruption fixtures preserve completed frames. Their raw append
            // bypasses the public typed writer; ReadSplit must validate every live row before delivery.
            byte[] missing = input.Second.Single(record => record.Id == secondOwner).Body.ToArray();
            Assert.InRange(missing[0], (byte)1, (byte)127); // The fixture's first reference is one byte.
            missing[0] = 127;
            missingString = store.Append(P0GraphKind.State, second, schemaRoot,
                new(second, new(firstOwner), [Encode(input.Second.Single(record => record.Id == secondOwner), missing)])).Address;
            byte[] nonString = input.Second.Single(record => record.Id == firstOwner).Body.ToArray();
            nonString[0] = checked((byte)secondOwner);
            wrongKind = store.Append(P0GraphKind.State, second, schemaRoot,
                new(second, new(firstOwner), [Encode(input.Second.Single(record => record.Id == firstOwner), nonString)])).Address;
            Assert.Equal(second, repository.GetHead("main"));
            Assert.Equal(schemaRoot, repository.SharedSchemaRoot);
            resources.Data.ConfirmDurable();
        }
        Assert.NotEqual(first, second);

        // Clear every supplied body before cold reopening. All content then comes from actual
        // selected object chains; CaptureSession and source domain objects are not loading inputs.
        foreach (var record in input.First.Concat(input.Second)) {
            Array.Clear(record.Body);
        }
        Dictionary<string, byte[]> diskBefore = Directory.EnumerateFiles(directory.Path, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes);
        Dictionary<uint, byte[]> oldBodies, newBodies;
        DecodedRevision oldView, newView;
        using (P0Repository reopened = P0Repository.OpenReadOnlyExisting(directory.Path)) {
            P0GraphStore cold = reopened.Store;
            Assert.Equal(second, reopened.GetHead("main"));
            Assert.Equal(schemaRoot, reopened.SharedSchemaRoot);
            P0GraphSnapshot firstGraph = cold.ReadGraph(first), secondGraph = cold.ReadGraph(second);
            Assert.Equal(new ObjectId(firstOwner), firstGraph.RootId);
            Assert.Equal(firstGraph.RootId, secondGraph.RootId);
            Assert.Equal(schemaRoot, firstGraph.SchemaRoot);
            Assert.Equal(schemaRoot, secondGraph.SchemaRoot);
            Assert.Equal(input.First.Select(record => record.Id).Order(), secondGraph.Objects.Keys);
            FrameAddress firstBatch = firstGraph.OwnedBatch!.Value, secondBatch = secondGraph.OwnedBatch!.Value;
            Assert.NotEqual(first, firstBatch);
            Assert.NotEqual(second, secondBatch);
            Assert.NotEqual(firstBatch, secondBatch);
            Assert.Equal(secondBatch, secondGraph.Objects[firstOwner]);
            Assert.Equal(firstBatch, secondGraph.Objects[secondOwner]);
            Dictionary<uint, byte[]> ReadBodies(FrameAddress graph) => cold.ReadGraph(graph).Objects.Keys.Reverse()
                .ToDictionary(id => id, id => {
                    P0ChainEntry entry = Assert.Single(cold.ReadObjectChain(graph, id).Entries);
                    P0CatalogView source = P0CatalogView.Load(cold, entry.SchemaRoot!.Value);
                    return P0TypedBodyCodec.Decode(entry.Record.Body, source).Body;
                });

            oldView = DecodedRevision.ReadSplit(cold, first, models);
            newView = DecodedRevision.ReadSplit(cold, second, models);
            Assert.True(validateDecoded(input.Roots, oldView, 7));
            Assert.True(validateDecoded(input.Roots, newView, 8));
            Assert.Equal(firstGraph.Objects.Select(pair => (new ObjectId(pair.Key), pair.Value)),
                oldView.ObjectHeads.OrderBy(pair => pair.Key).Select(pair => (pair.Key, pair.Value)));
            Assert.Equal(secondGraph.Objects.Select(pair => (new ObjectId(pair.Key), pair.Value)),
                newView.ObjectHeads.OrderBy(pair => pair.Key).Select(pair => (pair.Key, pair.Value)));
            oldBodies = ReadBodies(first);
            newBodies = ReadBodies(second);
            Assert.True(validate(input.Roots, metadata, oldBodies, 7));
            Assert.True(validate(input.Roots, metadata.Reverse().ToArray(), newBodies, 8));
            Assert.False(oldBodies[firstOwner].SequenceEqual(newBodies[firstOwner]));
            foreach (var item in metadata.Where(item => item.IsString)) {
                Assert.Equal(firstBatch, secondGraph.Objects[item.Id]);
                Assert.Equal(oldBodies[item.Id], newBodies[item.Id]);
            }

            foreach (FrameAddress bad in new[] { missingString, wrongKind }) {
                DecodedRevision? partial = null;
                Assert.Throws<InvalidDataException>(() => partial = DecodedRevision.ReadSplit(cold, bad, models));
                Assert.Null(partial);
                Assert.Throws<InvalidDataException>(() => validate(input.Roots, metadata, ReadBodies(bad), 8));
                Assert.True(validateDecoded(input.Roots, oldView, 7));
                Assert.True(validateDecoded(input.Roots, newView, 8));
            }
            DurableSchema originalSchema = metadata.Single(item => item.Id == firstOwner).Schema!;
            DurableSchema wrongVersion = new(originalSchema.SchemaId, originalSchema.Version + 1,
                originalSchema.Fields.ToArray(), originalSchema.BaseSchema);
            var mismatchedSchema = metadata.Select(item => item.Id == firstOwner
                ? (item.Id, item.IsString, (DurableSchema?)wrongVersion) : item).ToArray();
            Assert.Throws<InvalidDataException>(() => validate(input.Roots, mismatchedSchema, newBodies, 8));
            uint stringId = metadata.First(item => item.IsString).Id;
            Assert.Throws<InvalidDataException>(() => validate([stringId, secondOwner, firstOwner, 0], metadata, newBodies, 8));
        }
        // Owned raw bodies, exact DTOs and per-ID string identity remain available after close.
        Assert.True(validate(input.Roots, metadata, oldBodies, 7));
        Assert.True(validate(input.Roots, metadata, newBodies, 8));
        Assert.True(validateDecoded(input.Roots, oldView, 7));
        Assert.True(validateDecoded(input.Roots, newView, 8));
        foreach ((string path, byte[] contents) in diskBefore) Assert.Equal(contents, File.ReadAllBytes(path));
        Assert.Equal(diskBefore.Count, Directory.EnumerateFiles(directory.Path, "*", SearchOption.AllDirectories).Count());
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
            public static Atelia.DurableGraph.Persistence.StateModelRegistry Models() {
                var models = new Atelia.DurableGraph.Persistence.StateModelRegistry();
                Owner.__DurableState.RegisterModel(models);
                return models;
            }
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

            // This independent codec check receives unpacked actual Base bodies and explicit
            // fixture query metadata; ReadSplit separately validates the persisted complete view.
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
                return ValidateValues(roots, owners, strings, firstScore);
            }

            public static bool ValidateDecoded(uint[] roots,
                Atelia.DurableGraph.Persistence.DecodedRevision view, uint firstScore) {
                if (view.Objects.Count != 5) throw new InvalidDataException("The exact view must contain all five live rows.");
                var owners = new Dictionary<uint, Owner.__DurableState.V1>();
                foreach (var row in view.Objects) {
                    if (row.Kind == ObjectStateKind.String) {
                        if (!ReferenceEquals(row.StringContent, view.Strings.ResolveString(row.Id)))
                            throw new InvalidDataException("Decoded rows and string table must use the same per-ID instance.");
                    } else {
                        if (!Owner.__DurableState.V1.Schema.Equals(row.Schema))
                            throw new InvalidDataException("The persisted owner has a different complete Schema.");
                        owners.Add(row.Id.Value, row.GetState<Owner.__DurableState.V1>());
                    }
                }
                foreach (uint root in roots) {
                    if (root != 0 && !owners.ContainsKey(root))
                        throw new InvalidDataException("Unknown or non-durable fixture root.");
                }
                return ValidateValues(roots, owners, view.Strings, firstScore);
            }

            private static bool ValidateValues(uint[] roots, Dictionary<uint, Owner.__DurableState.V1> owners,
                StringReadTable strings, uint firstScore) {
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
