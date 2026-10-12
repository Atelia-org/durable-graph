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
    private sealed record Witness(string State0, string? State1, string Schema, uint RootId, uint ListId, string ListHead);

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
                HostAssembly = AssemblyEvidence(typeof(Host).Assembly),
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
        Need(pending.Business.Write.Records.Count == 2 && pending.Business.Write.Records.All(row => row.Kind == ObjectVersionKind.Base), "First save must write exactly Root and List Base.");
        P0SaveResult saved = pending.Commit();
        uint rootId = saved.Business.RootId.Value;
        uint listId = saved.Business.Objects.Keys.Single(id => id != rootId);
        Need(saved.Business.Objects.Count == 2, "Inline/base/Nullable must not create business object rows.");
        FrameAddress schema = repository.SharedSchemaRoot!.Value;
        Need(saved.Business.SchemaRoot == schema, "The business graph must pin its actual meta root.");
        CheckRows(repository, saved.Business.Address, pending.Catalog.View, models, rootId, listId);
        WriteWitness(witnessPath, new(Hex(saved.Business.Address), null, Hex(schema), rootId, listId, Hex(saved.Business.Objects[listId])));
    }

    private static void Read(string database, string witnessPath, bool continued) {
        Witness witness = ReadWitness(witnessPath);
        using var repository = P0Repository.OpenReadOnlyExisting(database);
        FrameAddress schema = Address(witness.Schema), s0 = Address(witness.State0);
        // This is intentionally before the first registration of any business model in this process.
        P0CatalogView meta = P0CatalogView.Load(repository.Store, schema);
        CheckCatalog(meta);
        Need(repository.Store.AppendedFrames == 0, "Reading meta must not append.");
        StateModelRegistry models = Host.Models();
        CheckRows(repository, s0, meta, models, witness.RootId, witness.ListId);
        Host.Check(P0BusinessWorkspace.ReadRoot(repository.Store, s0, models), 0);
        if (continued) {
            FrameAddress s1 = Address(witness.State1 ?? throw new InvalidDataException("Missing continued State."));
            Need(repository.GetHead("main") == s1 && repository.GetStateHead("main") == s1, "Fresh published roots must select the continued State.");
            Need(repository.Store.ReadGraph(s1).SourceGraph == s0, "The Delta State must retain its exact content source.");
            Host.Check(P0BusinessWorkspace.ReadRoot(repository.Store, s1, models), 1);
            Need(repository.Store.ReadObjectChain(s1, witness.RootId).Entries[^1].Record.Kind == ObjectVersionKind.Delta, "Root must persist a real Delta.");
            Need(repository.Store.ReadObjectChain(s1, witness.ListId).Entries[^1].Record.Kind == ObjectVersionKind.Delta, "Composite List must persist a real Delta.");
        } else { Need(repository.GetHead("main") == s0, "Fresh roots must select the seed State."); }
        Need(repository.SharedSchemaRoot == schema && repository.Store.AppendedFrames == 0, "Read-only lanes must keep the exact shared root.");
    }

    private static void Continue(string database, string witnessPath) {
        Witness witness = ReadWitness(witnessPath);
        FrameAddress s0 = Address(witness.State0), schema = Address(witness.Schema);
        using var repository = P0Repository.OpenExisting(database);
        P0CatalogView meta = P0CatalogView.Load(repository.Store, schema); // No business registration yet.
        CheckCatalog(meta);
        var session = new P0Session(repository, "main", Host.Models());
        IDurableObject restored = session.State ?? throw new InvalidDataException("Missing restored State.");
        Host.Check(restored, 0);
        object list = Host.Samples(restored);
        Host.Change(restored);
        using var pending = session.Prepare(restored, parameters: new(1_000_000, 1));
        Need(pending.Catalog.Write is null && pending.Catalog.AddedDescriptors == 0 && pending.Catalog.EncodedIds.Count == 0, "Same-shape continuation must reuse the catalog.");
        Need(pending.Business.Write.SourceGraph == s0 && pending.Business.Write.RemovedIds.Count == 0, "Continuation must preserve the exact content source and live set.");
        Need(pending.Business.Write.Records.Count == 2, "Sparse changes must write Root and List only.");
        ObjectVersionRecord root = pending.Business.Write.Records.Single(row => row.ObjectId == witness.RootId);
        ObjectVersionRecord samples = pending.Business.Write.Records.Single(row => row.ObjectId == witness.ListId);
        Need(root.Kind == ObjectVersionKind.Delta && samples.Kind == ObjectVersionKind.Delta, "Both stored changes must be real Delta.");
        Need(root.PriorAddress == session.StateSource!.Objects[witness.RootId] && samples.PriorAddress == Address(witness.ListHead), "Delta priors must match the restored heads.");
        P0SaveResult saved = pending.Commit();
        Need(repository.SharedSchemaRoot == schema && saved.Business.SchemaRoot == schema, "Continuation must retain its accurate Schema pin.");
        Need(ReferenceEquals(restored, session.State) && ReferenceEquals(list, Host.Samples(session.State!)), "Successful installation must retain the frozen candidate's live identities.");
        Host.Check(P0BusinessWorkspace.ReadRoot(repository.Store, s0, Host.Models()), 0);
        Host.Check(P0BusinessWorkspace.ReadRoot(repository.Store, saved.Business.Address, Host.Models()), 1);
        WriteWitness(witnessPath, witness with { State1 = Hex(saved.Business.Address) });
    }

    private static void CheckRows(P0Repository repository, FrameAddress graph, P0CatalogView meta,
        StateModelRegistry models, uint rootId, uint listId) {
        var expected = Expected();
        StateModelSnapshot snapshot = models.Snapshot(schemaLookup: key => meta.Schemas.GetValueOrDefault(key));
        P0DecodedRows decoded = P0TypedReader.Read(repository.Store, graph, snapshot, meta);
        Need(decoded.Rows.Count == 2 && expected.Root.Equals(decoded.Rows[new(rootId)].Schema), "Business rows must use the recovered complete Root Schema.");
        Need(ObjectLayout.ForList(expected.List).Equals(decoded.Rows[new(listId)].Layout), "Composite List must use the recovered complete element layout.");
        foreach (uint id in new[] { rootId, listId }) {
            P0ObjectChain chain = repository.Store.ReadObjectChain(graph, id);
            BareValueReader body = new(chain.Entries[0].Record.Body);
            Need(body.ReadByte() == 2 && body.ReadByte() == 1, "Root/composite List must use the new employee wire's dynamic descriptor route.");
            ObjectId descriptor = new(body.ReadVarUInt32());
            Need(descriptor == meta.DescriptorId(decoded.Rows[new(id)].Layout), "Base descriptor ObjectId must belong to the selected exact catalog.");
        }
    }

    private static void CheckCatalog(P0CatalogView meta) {
        var expected = Expected();
        Need(meta.Schemas.Count == 3 && meta.DescriptorIds.Count == 4, "Expected exactly Root, Base, Point and composite List descriptors.");
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

    private static (DurableSchema Root, DurableSchema Base, DurableSchema Point, ListLayout List) Expected() {
        DurableSchema point = new("p1.closure.point", 1, SchemaKind.InlineValue,
            new DurableFieldInfo(1, TypeTag.Int32), DurableFieldInfo.Nullable(2, new(1, TypeTag.Int32)));
        DurableSchema @base = new("p1.closure.base", 1, [
            new(1, TypeTag.Int32), new(2, TypeTag.Double), new(3, TypeTag.Double), new(4, TypeTag.Double), new(5, TypeTag.Double),
        ]);
        ListLayout list = new(DurableFieldInfo.Nullable(1, new(1, TypeTag.InlineValue, inlineSchema: point)));
        DurableSchema root = new("p1.closure.root", 1, [
            new(1, TypeTag.InlineValue, inlineSchema: point),
            DurableFieldInfo.Nullable(2, new(1, TypeTag.InlineValue, inlineSchema: point)),
            DurableFieldInfo.Nullable(3, new(1, TypeTag.Int32)), DurableFieldInfo.Reference(4, list.Type),
        ], @base);
        return (root, @base, point, list);
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
