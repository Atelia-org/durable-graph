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
    private sealed record BusinessIds(uint Root, uint Samples, uint Optional, uint Nested, uint Inner, uint ArrayOptional, uint Points,
        uint Scalars, uint Entries) {
        internal uint[] All => [Root, Samples, Optional, Nested, Inner, ArrayOptional, Points, Scalars, Entries];
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
                PersistenceAssembly = AssemblyEvidence(typeof(RepositoryStorageFacade).Assembly), RuntimeAssembly = AssemblyEvidence(typeof(DurableSchema).Assembly),
                HostAssembly = AssemblyEvidence(typeof(Host).Assembly), TypedBodyAbi = TypedBaseBodyCodec.FormatVersion,
            }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("MetaBootstrapSchema:" + lane + ":Passed");
        return 0;
    }

    private static void Seed(string database, string witnessPath) {
        using var repository = RepositoryStorageFacade.CreateNew(database);
        StateModelRegistry models = Host.Models();
        var session = new BranchSaveSession(repository, "main", models);
        Need(session.Catalog.Accepted is null && session.Catalog.DirtyCount == 0, "Seed must not manually register catalog entries.");
        using var pending = session.Prepare(Host.Create());
        CheckCatalog(pending.Catalog.View);
        Need(pending.Business.Write.Records.Count == 9 && pending.Business.Write.Records.All(row => row.Kind == ObjectVersionKind.Base), "First save must write Root, four Lists, two Arrays and two Dictionary Base rows.");
        RepositorySaveResult saved = pending.Commit();
        Need(saved.Business.ObjectHeads.Count == 9, "Inline/base/Nullable must not create rows; the shared inner List must create only one.");
        FrameAddress schema = repository.SharedSchemaRoot!.Value;
        Need(saved.Business.SchemaRoot == schema, "The business graph must pin its actual meta root.");
        BusinessIds ids = CheckRows(repository, saved.Business.Address, pending.Catalog.View, models);
        CheckFixedRowsWithoutCatalog(repository, saved.Business.Address, ids, 0);
        WriteWitness(witnessPath, new(Hex(saved.Business.Address), null, Hex(schema), ids,
            saved.Business.ObjectHeads.ToDictionary(pair => pair.Key, pair => Hex(pair.Value))));
    }

    private static void Read(string database, string witnessPath, bool continued) {
        Witness witness = ReadWitness(witnessPath);
        using var repository = RepositoryStorageFacade.OpenReadOnlyExisting(database);
        FrameAddress schema = Address(witness.Schema), s0 = Address(witness.State0);
        Need(repository.Store.ReadGraph(s0).SchemaRoot == schema, "The seed State must retain its exact Schema pin.");
        // This is intentionally before the first registration of any business model in this process.
        SchemaCatalogView meta = SchemaCatalogView.Load(repository.Store, schema);
        CheckCatalog(meta);
        CheckFixedRowsWithoutCatalog(repository, s0, witness.Ids, 0);
        FrameAddress? s1 = continued ? Address(witness.State1 ?? throw new InvalidDataException("Missing continued State.")) : null;
        if (s1 is { } current) { CheckFixedRowsWithoutCatalog(repository, current, witness.Ids, 1); }
        Need(repository.Store.AppendedFrames == 0, "Reading meta must not append.");
        StateModelRegistry models = Host.Models();
        Need(CheckRows(repository, s0, meta, models) == witness.Ids, "Cold seed rows must retain every object ID.");
        Host.Check(BusinessPersistenceAdapter.ReadRoot(repository.Store, s0, models), 0);
        if (s1 is { } accepted) {
            Need(repository.GetHead("main") == accepted && repository.GetStateHead("main") == accepted, "Fresh published roots must select the continued State.");
            StoredGraphRevision graph = repository.Store.ReadGraph(accepted);
            Need(graph.SourceGraph == s0 && graph.SchemaRoot == schema, "The Delta State must retain its exact content source and Schema pin.");
            Need(CheckRows(repository, accepted, meta, models) == witness.Ids, "Cold continued rows must retain every object ID.");
            Host.Check(BusinessPersistenceAdapter.ReadRoot(repository.Store, accepted, models), 1);
            foreach (uint id in witness.Ids.All) {
                ObjectVersionChain chain = repository.Store.ReadObjectChain(accepted, id);
                Need(chain.Entries.Count == 2 && chain.Entries[^1].Record.Kind == ObjectVersionKind.Delta, "Every changed object must persist Base plus one real Delta.");
                Need(chain.Entries[^1].Record.PriorAddress == Address(witness.Heads[id]), "Fresh Delta prior must equal its exact seed head.");
                Need(graph.ObjectHeads[id] != Address(witness.Heads[id]), "A changed object's accepted head must advance.");
            }
        } else { Need(repository.GetHead("main") == s0, "Fresh roots must select the seed State."); }
        Need(repository.SharedSchemaRoot == schema && repository.Store.AppendedFrames == 0, "Read-only lanes must keep the exact shared root.");
    }

    private static void Continue(string database, string witnessPath) {
        Witness witness = ReadWitness(witnessPath);
        FrameAddress s0 = Address(witness.State0), schema = Address(witness.Schema);
        using var repository = RepositoryStorageFacade.OpenExisting(database);
        Need(repository.Store.ReadGraph(s0).SchemaRoot == schema, "Cold continuation must read the pinned seed Schema.");
        SchemaCatalogView meta = SchemaCatalogView.Load(repository.Store, schema); // No business registration yet.
        CheckCatalog(meta);
        CheckFixedRowsWithoutCatalog(repository, s0, witness.Ids, 0);
        var session = new BranchSaveSession(repository, "main", Host.Models());
        IDurableObject restored = session.State ?? throw new InvalidDataException("Missing restored State.");
        Host.Check(restored, 0);
        object[] instances = Host.Objects(restored);
        Host.Change(restored);
        using var pending = session.Prepare(restored, parameters: new(1_000_000, 1));
        Need(pending.Catalog.Write is null && pending.Catalog.AddedDescriptors == 0 && pending.Catalog.EncodedIds.Count == 0, "Same-shape continuation must reuse the catalog.");
        Need(pending.Business.Write.SourceGraph == s0 && pending.Business.Write.RemovedIds.Count == 0, "Continuation must preserve the exact content source and live set.");
        Need(pending.Business.Write.Records.Count == 9 && pending.Business.Write.Records.Select(row => row.ObjectId).Order().SequenceEqual(witness.Ids.All.Order()), "Sparse changes must write exactly the nine existing objects.");
        foreach (ObjectVersionRecord row in pending.Business.Write.Records) {
            Need(row.Kind == ObjectVersionKind.Delta, "Every stored change must be a real Delta.");
            Need(row.PriorAddress == session.StateSource!.ObjectHeads[row.ObjectId] && row.PriorAddress == Address(witness.Heads[row.ObjectId]), "Delta priors must match the restored seed heads.");
        }
        RepositorySaveResult saved = pending.Commit();
        Need(repository.SharedSchemaRoot == schema && saved.Business.SchemaRoot == schema, "Continuation must retain its accurate Schema pin.");
        object[] installed = Host.Objects(session.State!);
        for (int i = 0; i < instances.Length; i++) {
            Need(ReferenceEquals(instances[i], installed[i]), "Successful installation must retain every frozen candidate live identity.");
        }
        CheckFixedRowsWithoutCatalog(repository, saved.Business.Address, witness.Ids, 1);
        Host.Check(BusinessPersistenceAdapter.ReadRoot(repository.Store, s0, Host.Models()), 0);
        Host.Check(BusinessPersistenceAdapter.ReadRoot(repository.Store, saved.Business.Address, Host.Models()), 1);
        WriteWitness(witnessPath, witness with { State1 = Hex(saved.Business.Address) });
    }

    private static BusinessIds CheckRows(RepositoryStorageFacade repository, FrameAddress graph, SchemaCatalogView meta,
        StateModelRegistry models) {
        var expected = Expected();
        ModelExecutionEnvironment snapshot = models.CreateEnvironment(schemaLookup: key => meta.Schemas.GetValueOrDefault(key));
        DecodedGraphRows decoded = StoredGraphDecoder.Read(repository.Store, graph, snapshot, meta);
        Need(decoded.Rows.Count == 9, "Business graph must contain exactly Root, four Lists, two Arrays and two Dictionaries.");
        uint Find(ObjectLayout layout) => decoded.Rows.Values.Single(row => row.Layout.Equals(layout)).Id.Value;
        BusinessIds ids = new(Find(ObjectLayout.ForDurable(expected.Root)), Find(ObjectLayout.ForList(expected.List)),
            Find(ObjectLayout.ForList(expected.Optional)), Find(ObjectLayout.ForList(expected.Nested)), Find(ObjectLayout.ForList(expected.Inner)),
            Find(ObjectLayout.ForArray(expected.ArrayOptional)), Find(ObjectLayout.ForArray(expected.Points)),
            Find(ObjectLayout.ForDictionary(expected.Scalars)), Find(ObjectLayout.ForDictionary(expected.Entries)));
        Need(decoded.Snapshot.RootId.Value == ids.Root && expected.Root.Equals(decoded.Rows[new(ids.Root)].Schema), "Business rows must use the recovered complete Root Schema.");
        Need(((IFrozenArrayState)decoded.Rows[new(ids.ArrayOptional)].Content).Shape.Equals(new ArrayShape(128)) &&
            ((IFrozenArrayState)decoded.Rows[new(ids.Points)].Content).Shape.Equals(new ArrayShape(16, 8)), "Decoded arrays must retain their exact persisted shapes.");
        Need(((IFrozenDictionaryState)decoded.Rows[new(ids.Scalars)].Content).Count == 128 &&
            ((IFrozenDictionaryState)decoded.Rows[new(ids.Scalars)].Content).ComparerKind == DictionaryComparerKind.ScalarDefault &&
            ((IFrozenDictionaryState)decoded.Rows[new(ids.Entries)].Content).Count == 128 &&
            ((IFrozenDictionaryState)decoded.Rows[new(ids.Entries)].Content).ComparerKind == DictionaryComparerKind.CurrentDefault,
            "Dictionary contents must retain their exact count and stored comparison policy.");
        foreach (uint id in new[] { ids.Root, ids.Samples, ids.Points, ids.Entries }) {
            ObjectVersionChain chain = repository.Store.ReadObjectChain(graph, id);
            BareValueReader header = new(chain.Entries[0].Record.Body);
            Need(header.ReadByte() == TypedBaseBodyCodec.FormatVersion && header.ReadByte() == 1, "User Root/List/Array/Dictionary must use the current typed ABI's dynamic route.");
            ObjectId descriptor = new(header.ReadVarUInt32());
            Need(descriptor == meta.DescriptorId(decoded.Rows[new(id)].Layout), "Base descriptor ObjectId must belong to the selected exact catalog.");
            var body = TypedBaseBodyCodec.Decode(chain.Entries[0].Record.Body, meta);
            Need(body.Layout.Equals(decoded.Rows[new(id)].Layout), "Dynamic Base interpretation must use the selected exact catalog.");
            bool requiresCatalog = false;
            try { _ = TypedBaseBodyCodec.Decode(chain.Entries[0].Record.Body, catalog: null); }
            catch (InvalidDataException) { requiresCatalog = true; }
            Need(requiresCatalog, "User Root/List/Array/Dictionary must require its source catalog.");
        }
        return ids;
    }

    private static void CheckFixedRowsWithoutCatalog(RepositoryStorageFacade repository, FrameAddress graph, BusinessIds ids, int stage) {
        var expected = Expected();
        // Neither header interpretation nor Base/Delta body reading receives a Schema view or user definitions.
        ModelExecutionEnvironment empty = new StateModelRegistry().CreateEnvironment();
        ObjectStateRecord Read(uint id, ListLayout layout) {
            ObjectVersionChain chain = repository.Store.ReadObjectChain(graph, id);
            var decoded = TypedBaseBodyCodec.Decode(chain.Entries[0].Record.Body, catalog: null);
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
        ObjectVersionChain arrayChain = repository.Store.ReadObjectChain(graph, ids.ArrayOptional);
        var arrayBody = TypedBaseBodyCodec.Decode(arrayChain.Entries[0].Record.Body, catalog: null);
        Need(arrayBody.Layout.Equals(ObjectLayout.ForArray(expected.ArrayOptional)), "Fixed Array header must recover the exact vector layout.");
        ObjectStateRecord arrayRow = empty.ResolveObjectReader(arrayBody.Layout).Read(new(ids.ArrayOptional), new Bodies(arrayChain, arrayBody.Body));
        Need(arrayRow.Id == new ObjectId(ids.ArrayOptional), "Fixed Array reader must retain the object identity.");
        FrozenArrayState<NullableState<int>> array = arrayRow.GetArrayState<NullableState<int>>();
        Need(array.Shape.Equals(new ArrayShape(128)), "Fixed Array body must retain its exact persisted shape.");
        for (int i = 0; i < array.Shape.Count; i++) {
            int? value = Host.ExpectedArrayOptional(i, stage);
            Need(array[i].HasValue == value.HasValue && (!value.HasValue || array[i].Value == value.Value), "Fixed Nullable Int32 array decoding mismatch.");
        }
        ObjectVersionChain mappingChain = repository.Store.ReadObjectChain(graph, ids.Scalars);
        var mappingBody = TypedBaseBodyCodec.Decode(mappingChain.Entries[0].Record.Body, catalog: null);
        Need(mappingBody.Layout.Equals(ObjectLayout.ForDictionary(expected.Scalars)), "Fixed Dictionary header must recover both exact slots and codec.");
        DictionaryStateReader mappingReader = (DictionaryStateReader)empty.ResolveObjectReader(mappingBody.Layout);
        ObjectStateRecord mappingRow = mappingReader.Read(new(ids.Scalars), new Bodies(mappingChain, mappingBody.Body));
        mappingReader.ValidateLookupKeys(mappingRow, static _ => throw new InvalidDataException("Builtin scalar keys must not resolve object rows."));
        FrozenDictionaryState<int, NullableState<long>> mapping = mappingRow.GetDictionaryState<int, NullableState<long>>();
        Need(mappingRow.Id == new ObjectId(ids.Scalars) && mapping.Count == 128 && mapping.ComparerKind == DictionaryComparerKind.ScalarDefault,
            "Fixed Dictionary body must retain its ID, count and comparison policy.");
        Dictionary<int, long?> actual = new();
        foreach (var pair in mapping.Entries) { actual.Add(pair.Key, pair.Value.HasValue ? pair.Value.Value : null); }
        foreach (int key in Host.DictionaryKeys(stage)) {
            Need(actual.TryGetValue(key, out long? value) && value == Host.ExpectedScalarValue(key, stage), "Fixed Dictionary must decode every complete key and Nullable Int64 value.");
        }
        Need(actual.ContainsKey(5) == (stage == 0), "Fixed Dictionary must apply the actual key removal.");
    }

    private sealed class Bodies(ObjectVersionChain chain, byte[] first) : IStateBodySource {
        public int Count => chain.Entries.Count;
        public ReadOnlySpan<byte> GetBody(int index) => index == 0 ? first : chain.Entries[index].Record.Body;
    }

    private static void CheckCatalog(SchemaCatalogView meta) {
        var expected = Expected();
        Need(meta.Schemas.Count == 4 && meta.DescriptorIds.Count == 7, "Expected Root, Base, Point, Key and the three user-container descriptors.");
        foreach (ObjectLayout builtin in new[] { ObjectLayout.ForList(expected.Optional), ObjectLayout.ForList(expected.Nested),
            ObjectLayout.ForList(expected.Inner), ObjectLayout.ForArray(expected.ArrayOptional), ObjectLayout.ForDictionary(expected.Scalars) }) {
            bool rejectedDescriptor = false;
            try { _ = meta.DescriptorId(builtin); }
            catch (InvalidDataException) { rejectedDescriptor = true; }
            Need(rejectedDescriptor, "Builtin-only List/Array/Dictionary layouts must not acquire catalog descriptors.");
        }
        foreach (DurableSchema schema in new[] { expected.Root, expected.Base, expected.Point, expected.Key }) {
            Need(meta.Schemas.TryGetValue(new(schema.Type, schema.Version), out DurableSchema? stored) && schema.Equals(stored), "Cold employee projection must preserve the complete Schema.");
        }
        ObjectId rootId = meta.DescriptorId(ObjectLayout.ForDurable(expected.Root));
        ObjectId baseId = meta.DescriptorId(ObjectLayout.ForDurable(expected.Base));
        ObjectId listId = meta.DescriptorId(ObjectLayout.ForList(expected.List));
        ObjectId arrayId = meta.DescriptorId(ObjectLayout.ForArray(expected.Points));
        ObjectId mappingId = meta.DescriptorId(ObjectLayout.ForDictionary(expected.Entries));
        _ = meta.ResolveLayout(rootId);
        _ = meta.ResolveLayout(baseId);
        _ = meta.ResolveLayout(listId);
        Need(meta.ResolveLayout(arrayId).Equals(ObjectLayout.ForArray(expected.Points)), "Catalog must recover the full rank-two exact inline Array layout.");
        Need(meta.ResolveLayout(mappingId).Equals(ObjectLayout.ForDictionary(expected.Entries)), "Catalog must recover the complete inline key/value Dictionary layout.");
        ObjectId[] inlineIds = meta.DescriptorIds.Where(id => id != rootId && id != baseId && id != listId && id != arrayId && id != mappingId).ToArray();
        Need(inlineIds.Length == 2, "Point and Key must have exactly two inline descriptors.");
        // Inline Schemas describe values and must never become object-row layouts.
        foreach (ObjectId inlineId in inlineIds) {
            bool rejectedInline = false;
            try { _ = meta.ResolveLayout(inlineId); }
            catch (InvalidDataException) { rejectedInline = true; }
            Need(rejectedInline, "The inline descriptor must reject object-row use.");
        }
    }

    private static (DurableSchema Root, DurableSchema Base, DurableSchema Point, ListLayout List,
        ListLayout Optional, ListLayout Nested, ListLayout Inner, ArrayLayout ArrayOptional, ArrayLayout Points,
        DurableSchema Key, DictionaryLayout Scalars, DictionaryLayout Entries) Expected() {
        DurableSchema point = new("p1.closure.point", 1, SchemaKind.InlineValue,
            new DurableFieldInfo(1, TypeTag.Int32), DurableFieldInfo.Nullable(2, new(1, TypeTag.Int32)));
        DurableSchema key = new("p1.closure.key", 1, SchemaKind.InlineValue,
            new DurableFieldInfo(1, TypeTag.Int32), DurableFieldInfo.Nullable(2, new(1, TypeTag.Int32)));
        DurableSchema @base = new("p1.closure.base", 1, [
            new(1, TypeTag.Int32), new(2, TypeTag.Double), new(3, TypeTag.Double), new(4, TypeTag.Double), new(5, TypeTag.Double),
        ]);
        ListLayout list = new(DurableFieldInfo.Nullable(1, new(1, TypeTag.InlineValue, inlineSchema: point)));
        ListLayout optional = new(DurableFieldInfo.Nullable(1, new(1, TypeTag.Int64)));
        ListLayout inner = new(new(1, TypeTag.Int32));
        ListLayout nested = new(DurableFieldInfo.Reference(1, inner.Type));
        ArrayLayout arrayOptional = new(TypeExprKind.VectorArray, DurableFieldInfo.Nullable(1, new(1, TypeTag.Int32)), codecVersion: 1);
        ArrayLayout points = new(TypeExprKind.Rank2Array, DurableFieldInfo.Nullable(1, new(1, TypeTag.InlineValue, inlineSchema: point)), codecVersion: 1);
        DictionaryLayout scalars = new(new(1, TypeTag.Int32), DurableFieldInfo.Nullable(2, new(1, TypeTag.Int64)), codecVersion: 1);
        DictionaryLayout entries = new(new(1, TypeTag.InlineValue, inlineSchema: key),
            DurableFieldInfo.Nullable(2, new(1, TypeTag.InlineValue, inlineSchema: point)), codecVersion: 1);
        DurableSchema root = new("p1.closure.root", 1, [
            new(1, TypeTag.InlineValue, inlineSchema: point),
            DurableFieldInfo.Nullable(2, new(1, TypeTag.InlineValue, inlineSchema: point)),
            DurableFieldInfo.Nullable(3, new(1, TypeTag.Int32)), DurableFieldInfo.Reference(4, list.Type),
            DurableFieldInfo.Reference(5, optional.Type), DurableFieldInfo.Reference(6, nested.Type),
            DurableFieldInfo.Reference(7, arrayOptional.Type), DurableFieldInfo.Reference(8, points.Type),
            DurableFieldInfo.Reference(9, scalars.Type), DurableFieldInfo.Reference(10, entries.Type),
        ], @base);
        return (root, @base, point, list, optional, nested, inner, arrayOptional, points, key, scalars, entries);
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
