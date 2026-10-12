extern alias persistence;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Atelia.Binary;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using BootstrapSchemaClosure;
using persistence::Atelia.DurableGraph.Persistence;
using persistence::Atelia.DurableGraph.Persistence.MetaBootstrapP0;

internal static class Program {
    private sealed record BusinessIds(uint Root, uint Samples, uint Optional, uint Nested, uint Inner) {
        internal uint[] All => [Root, Samples, Optional, Nested, Inner];
    }
    private sealed record Witness(string State0, string? State1, string Schema, BusinessIds Ids, Dictionary<uint, string> Heads);

    private static int Main(string[] args) {
        if (args.Length != 3) { throw new ArgumentException("Expected lane, database and witness path."); }
        string lane = args[0], database = Path.GetFullPath(args[1]), witnessPath = Path.GetFullPath(args[2]);
        switch (lane) {
            case "seed": Seed(database, witnessPath); break;
            case "read": Read(database, witnessPath, continued: false); break;
            case "continue": Continue(database, witnessPath); break;
            case "verify": Read(database, witnessPath, continued: true); break;
            default: throw new ArgumentException("Unknown consumer lane.");
        }
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(witnessPath)!, lane + ".evidence.json"),
            JsonSerializer.Serialize(new {
                Lane = lane, ProcessId = Environment.ProcessId, EmployeeAssembly = AssemblyEvidence(typeof(P0CatalogRecord).Assembly),
                PersistenceAssembly = AssemblyEvidence(typeof(P0Repository).Assembly), RuntimeAssembly = AssemblyEvidence(typeof(DurableSchema).Assembly),
                HostAssembly = AssemblyEvidence(typeof(Host).Assembly), TypedBodyAbi = P0TypedBodyCodec.FormatVersion,
            }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("MetaBootstrapSchema:" + lane + ":Passed");
        return 0;
    }

    private static void Seed(string database, string witnessPath) {
        using var repository = P0Repository.CreateNew(database);
        StateModelRegistry models = Host.Models();
        var session = new P0Session(repository, "main", models);
        Need(session.Catalog.Accepted is null && session.Catalog.DirtyCount == 0, "Seed must not manually register catalog entries.");
        using var pending = session.Prepare(Host.Create());
        CheckCatalog(pending.Catalog.View);
        Need(pending.Business.Write.Records.Count == 5 && pending.Business.Write.Records.All(row => row.Kind == ObjectVersionKind.Base), "First save must write Root and four List Base rows.");
        P0SaveResult saved = pending.Commit();
        Need(saved.Business.Objects.Count == 5, "Inline/base/Nullable must not create rows; the shared inner List must create only one.");
        FrameAddress schema = repository.SharedSchemaRoot!.Value;
        Need(saved.Business.SchemaRoot == schema, "The business graph must pin its actual meta root.");
        BusinessIds ids = CheckRows(repository, saved.Business.Address, pending.Catalog.View, models);
        CheckFixedRowsWithoutCatalog(repository, saved.Business.Address, ids, 0);
        WriteWitness(witnessPath, new(Hex(saved.Business.Address), null, Hex(schema), ids,
            saved.Business.Objects.ToDictionary(pair => pair.Key, pair => Hex(pair.Value))));
    }

    private static void Read(string database, string witnessPath, bool continued) {
        Witness witness = ReadWitness(witnessPath);
        using var repository = P0Repository.OpenReadOnlyExisting(database);
        FrameAddress schema = Address(witness.Schema), s0 = Address(witness.State0);
        Need(repository.Store.ReadGraph(s0).SchemaRoot == schema, "The seed State must retain its exact Schema pin.");
        // This is intentionally before the first registration of any business model in this process.
        P0CatalogView meta = P0CatalogView.Load(repository.Store, schema);
        CheckCatalog(meta);
        CheckFixedRowsWithoutCatalog(repository, s0, witness.Ids, 0);
        FrameAddress? s1 = continued ? Address(witness.State1 ?? throw new InvalidDataException("Missing continued State.")) : null;
        if (s1 is { } current) { CheckFixedRowsWithoutCatalog(repository, current, witness.Ids, 1); }
        Need(repository.Store.AppendedFrames == 0, "Reading meta must not append.");
        StateModelRegistry models = Host.Models();
        Need(CheckRows(repository, s0, meta, models) == witness.Ids, "Cold seed rows must retain every object ID.");
        Host.Check(P0BusinessWorkspace.ReadRoot(repository.Store, s0, models), 0);
        if (s1 is { } accepted) {
            Need(repository.GetHead("main") == accepted && repository.GetStateHead("main") == accepted, "Fresh published roots must select the continued State.");
            P0GraphSnapshot graph = repository.Store.ReadGraph(accepted);
            Need(graph.SourceGraph == s0 && graph.SchemaRoot == schema, "The Delta State must retain its exact content source and Schema pin.");
            Need(CheckRows(repository, accepted, meta, models) == witness.Ids, "Cold continued rows must retain every object ID.");
            Host.Check(P0BusinessWorkspace.ReadRoot(repository.Store, accepted, models), 1);
            foreach (uint id in witness.Ids.All) {
                P0ObjectChain chain = repository.Store.ReadObjectChain(accepted, id);
                Need(chain.Entries.Count == 2 && chain.Entries[^1].Record.Kind == ObjectVersionKind.Delta, "Every changed object must persist Base plus one real Delta.");
                Need(chain.Entries[^1].Record.PriorAddress == Address(witness.Heads[id]), "Fresh Delta prior must equal its exact seed head.");
                Need(graph.Objects[id] != Address(witness.Heads[id]), "A changed object's accepted head must advance.");
            }
        } else { Need(repository.GetHead("main") == s0, "Fresh roots must select the seed State."); }
        Need(repository.SharedSchemaRoot == schema && repository.Store.AppendedFrames == 0, "Read-only lanes must keep the exact shared root.");
    }

    private static void Continue(string database, string witnessPath) {
        Witness witness = ReadWitness(witnessPath);
        FrameAddress s0 = Address(witness.State0), schema = Address(witness.Schema);
        using var repository = P0Repository.OpenExisting(database);
        Need(repository.Store.ReadGraph(s0).SchemaRoot == schema, "Cold continuation must read the pinned seed Schema.");
        P0CatalogView meta = P0CatalogView.Load(repository.Store, schema); // No business registration yet.
        CheckCatalog(meta);
        CheckFixedRowsWithoutCatalog(repository, s0, witness.Ids, 0);
        var session = new P0Session(repository, "main", Host.Models());
        IDurableObject restored = session.State ?? throw new InvalidDataException("Missing restored State.");
        Host.Check(restored, 0);
        object[] instances = Host.Objects(restored);
        Host.Change(restored);
        using var pending = session.Prepare(restored, parameters: new(1_000_000, 1));
        Need(pending.Catalog.Write is null && pending.Catalog.AddedDescriptors == 0 && pending.Catalog.EncodedIds.Count == 0, "Same-shape continuation must reuse the catalog.");
        Need(pending.Business.Write.SourceGraph == s0 && pending.Business.Write.RemovedIds.Count == 0, "Continuation must preserve the exact content source and live set.");
        Need(pending.Business.Write.Records.Count == 5 && pending.Business.Write.Records.Select(row => row.ObjectId).Order().SequenceEqual(witness.Ids.All.Order()), "Sparse changes must write exactly the five existing objects.");
        foreach (ObjectVersionRecord row in pending.Business.Write.Records) {
            Need(row.Kind == ObjectVersionKind.Delta, "Every stored change must be a real Delta.");
            Need(row.PriorAddress == session.StateSource!.Objects[row.ObjectId] && row.PriorAddress == Address(witness.Heads[row.ObjectId]), "Delta priors must match the restored seed heads.");
        }
        P0SaveResult saved = pending.Commit();
        Need(repository.SharedSchemaRoot == schema && saved.Business.SchemaRoot == schema, "Continuation must retain its accurate Schema pin.");
        object[] installed = Host.Objects(session.State!);
        for (int i = 0; i < instances.Length; i++) {
            Need(ReferenceEquals(instances[i], installed[i]), "Successful installation must retain every frozen candidate live identity.");
        }
        CheckFixedRowsWithoutCatalog(repository, saved.Business.Address, witness.Ids, 1);
        Host.Check(P0BusinessWorkspace.ReadRoot(repository.Store, s0, Host.Models()), 0);
        Host.Check(P0BusinessWorkspace.ReadRoot(repository.Store, saved.Business.Address, Host.Models()), 1);
        WriteWitness(witnessPath, witness with { State1 = Hex(saved.Business.Address) });
    }

    private static BusinessIds CheckRows(P0Repository repository, FrameAddress graph, P0CatalogView meta,
        StateModelRegistry models) {
        var expected = Expected();
        StateModelSnapshot snapshot = models.Snapshot(schemaLookup: key => meta.Schemas.GetValueOrDefault(key));
        P0DecodedRows decoded = P0TypedReader.Read(repository.Store, graph, snapshot, meta);
        Need(decoded.Rows.Count == 5, "Business graph must contain exactly Root and four Lists.");
        uint Find(ObjectLayout layout) => decoded.Rows.Values.Single(row => row.Layout.Equals(layout)).Id.Value;
        BusinessIds ids = new(Find(ObjectLayout.ForDurable(expected.Root)), Find(ObjectLayout.ForList(expected.List)),
            Find(ObjectLayout.ForList(expected.Optional)), Find(ObjectLayout.ForList(expected.Nested)), Find(ObjectLayout.ForList(expected.Inner)));
        Need(decoded.Snapshot.RootId.Value == ids.Root && expected.Root.Equals(decoded.Rows[new(ids.Root)].Schema), "Business rows must use the recovered complete Root Schema.");
        foreach (uint id in new[] { ids.Root, ids.Samples }) {
            P0ObjectChain chain = repository.Store.ReadObjectChain(graph, id);
            BareValueReader header = new(chain.Entries[0].Record.Body);
            Need(header.ReadByte() == P0TypedBodyCodec.FormatVersion && header.ReadByte() == 1, "Root/composite List must use the current typed ABI's dynamic route.");
            ObjectId descriptor = new(header.ReadVarUInt32());
            Need(descriptor == meta.DescriptorId(decoded.Rows[new(id)].Layout), "Base descriptor ObjectId must belong to the selected exact catalog.");
            var body = P0TypedBodyCodec.Decode(chain.Entries[0].Record.Body, meta);
            Need(body.Layout.Equals(decoded.Rows[new(id)].Layout), "Dynamic Base interpretation must use the selected exact catalog.");
            bool requiresCatalog = false;
            try { _ = P0TypedBodyCodec.Decode(chain.Entries[0].Record.Body, catalog: null); }
            catch (InvalidDataException) { requiresCatalog = true; }
            Need(requiresCatalog, "User Root/composite List must require its source catalog.");
        }
        return ids;
    }

    private static void CheckFixedRowsWithoutCatalog(P0Repository repository, FrameAddress graph, BusinessIds ids, int stage) {
        var expected = Expected();
        // Neither header interpretation nor Base/Delta body reading receives a Schema view or user definitions.
        StateModelSnapshot empty = new StateModelRegistry().Snapshot();
        ObjectStateRecord Read(uint id, ListLayout layout) {
            P0ObjectChain chain = repository.Store.ReadObjectChain(graph, id);
            var decoded = P0TypedBodyCodec.Decode(chain.Entries[0].Record.Body, catalog: null);
            Need(decoded.Layout.Equals(ObjectLayout.ForList(layout)), "Fixed structural header must recover the exact closed List layout.");
            ObjectStateRecord row = empty.ResolveObjectReader(decoded.Layout).Read(new(id), new Bodies(chain, decoded.Body));
            Need(row.Id == new ObjectId(id) && row.Layout.Equals(decoded.Layout), "Fixed reader must decode the actual persisted Base/Delta chain.");
            return row;
        }
        FrozenListState<NullableState<long>> optional = Read(ids.Optional, expected.Optional).GetListState<NullableState<long>>();
        Need(optional.Count == 256, "Fixed Nullable List count mismatch.");
        for (int i = 0; i < optional.Count; i++) {
            long? value = Host.ExpectedOptional(i, stage);
            Need(optional[i].HasValue == value.HasValue && (!value.HasValue || optional[i].Value == value.Value), "Fixed Nullable Int64 decoding mismatch.");
        }
        FrozenListState<ObjectId> nested = Read(ids.Nested, expected.Nested).GetListState<ObjectId>();
        Need(nested.Count == (stage == 0 ? 64 : 65), "Fixed nested List count mismatch.");
        foreach (ObjectId reference in nested.Elements) { Need(reference == new ObjectId(ids.Inner), "Fixed nested List must retain the single shared inner ID."); }
        FrozenListState<int> inner = Read(ids.Inner, expected.Inner).GetListState<int>();
        Need(inner.Count == 128, "Fixed inner List count mismatch.");
        for (int i = 0; i < inner.Count; i++) { Need(inner[i] == (stage == 1 && i == 17 ? 7777 : 1000 + i), "Fixed inner Int32 decoding mismatch."); }
    }

    private sealed class Bodies(P0ObjectChain chain, byte[] first) : IStateBodySource {
        public int Count => chain.Entries.Count;
        public ReadOnlySpan<byte> GetBody(int index) => index == 0 ? first : chain.Entries[index].Record.Body;
    }

    private static void CheckCatalog(P0CatalogView meta) {
        var expected = Expected();
        Need(meta.Schemas.Count == 3 && meta.DescriptorIds.Count == 4, "Expected exactly Root, Base, Point and composite List descriptors.");
        foreach (ListLayout builtin in new[] { expected.Optional, expected.Nested, expected.Inner }) {
            bool rejectedDescriptor = false;
            try { _ = meta.DescriptorId(ObjectLayout.ForList(builtin)); }
            catch (InvalidDataException) { rejectedDescriptor = true; }
            Need(rejectedDescriptor, "Builtin-only Lists must not acquire catalog descriptors.");
        }
        foreach (DurableSchema schema in new[] { expected.Root, expected.Base, expected.Point }) {
            Need(meta.Schemas.TryGetValue(new(schema.Type, schema.Version), out DurableSchema? stored) && schema.Equals(stored), "Cold employee projection must preserve the complete Schema.");
        }
        ObjectId rootId = meta.DescriptorId(ObjectLayout.ForDurable(expected.Root));
        ObjectId baseId = meta.DescriptorId(ObjectLayout.ForDurable(expected.Base));
        ObjectId listId = meta.DescriptorId(ObjectLayout.ForList(expected.List));
        _ = meta.ResolveLayout(rootId);
        _ = meta.ResolveLayout(baseId);
        _ = meta.ResolveLayout(listId);
        ObjectId inlineId = meta.DescriptorIds.Single(id => id != rootId && id != baseId && id != listId);
        // Inline Schemas describe values and must never become object-row layouts.
        bool rejectedInline = false;
        try { _ = meta.ResolveLayout(inlineId); }
        catch (InvalidDataException) { rejectedInline = true; }
        Need(rejectedInline, "The inline descriptor must reject object-row use.");
    }

    private static (DurableSchema Root, DurableSchema Base, DurableSchema Point, ListLayout List,
        ListLayout Optional, ListLayout Nested, ListLayout Inner) Expected() {
        DurableSchema point = new("p1.closure.point", 1, SchemaKind.InlineValue,
            new DurableFieldInfo(1, TypeTag.Int32), DurableFieldInfo.Nullable(2, new(1, TypeTag.Int32)));
        DurableSchema @base = new("p1.closure.base", 1, [
            new(1, TypeTag.Int32), new(2, TypeTag.Double), new(3, TypeTag.Double), new(4, TypeTag.Double), new(5, TypeTag.Double),
        ]);
        ListLayout list = new(DurableFieldInfo.Nullable(1, new(1, TypeTag.InlineValue, inlineSchema: point)));
        ListLayout optional = new(DurableFieldInfo.Nullable(1, new(1, TypeTag.Int64)));
        ListLayout inner = new(new(1, TypeTag.Int32));
        ListLayout nested = new(DurableFieldInfo.Reference(1, inner.Type));
        DurableSchema root = new("p1.closure.root", 1, [
            new(1, TypeTag.InlineValue, inlineSchema: point),
            DurableFieldInfo.Nullable(2, new(1, TypeTag.InlineValue, inlineSchema: point)),
            DurableFieldInfo.Nullable(3, new(1, TypeTag.Int32)), DurableFieldInfo.Reference(4, list.Type),
            DurableFieldInfo.Reference(5, optional.Type), DurableFieldInfo.Reference(6, nested.Type),
        ], @base);
        return (root, @base, point, list, optional, nested, inner);
    }

    private static object AssemblyEvidence(Assembly assembly) => new {
        Name = assembly.GetName().Name, Path = assembly.Location,
        Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))),
    };
    private static Witness ReadWitness(string path) => JsonSerializer.Deserialize<Witness>(File.ReadAllText(path)) ?? throw new InvalidDataException("Missing witness.");
    private static void WriteWitness(string path, Witness witness) => File.WriteAllText(path, JsonSerializer.Serialize(witness));
    private static string Hex(FrameAddress address) {
        byte[] encoded = new byte[FrameAddress.EncodedSize];
        Need(address.TryWrite(encoded), "Invalid fixture address.");
        return Convert.ToHexString(encoded);
    }
    private static FrameAddress Address(string encoded) {
        Need(FrameAddress.TryRead(Convert.FromHexString(encoded), out FrameAddress address), "Invalid persisted witness address.");
        return address;
    }
    private static void Need(bool condition, string message) {
        if (!condition) { throw new InvalidDataException(message); }
    }
}
