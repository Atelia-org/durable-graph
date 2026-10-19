extern alias persistence;
using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using Atelia.Binary;
using CommitId = persistence::Atelia.DurableGraph.CommitId;
using Atelia.DurableGraph.Storage;
using Atelia.DurableGraph.Schema;
using persistence::Atelia.DurableGraph.Persistence;
using Atelia.FrameStore;
using Atelia.HeadStore;
using DataStore = Atelia.FrameStore.FrameStore;
using PublicationStore = Atelia.HeadStore.HeadStore;

// A test-only observer of this probe's current wire, not a repository reader or
// a format contract. Run only after all Repository/Checkout owners have closed.
// No friend access, reflection, generated employee DTOs or product codecs are used.
internal static class WireInspector {
    private const uint GraphTag = 0x37474250, BatchTag = 0x37424250, DirectoryTag = 0x37444250;
    private const int Limit = 1_000_000;
    private const string RootName = "pc.root", PointName = "pc.point", KeeperName = "pc.keeper";

    internal enum GraphKind : byte { Event = 1, State = 2, Meta = 3 }
    internal sealed record FrameImage(FrameAddress Address, uint Tag, string PayloadSha256, string TailMetaSha256);
    internal sealed record BaseHeader(byte FormatVersion, byte Route, uint? DescriptorId, uint? FixedCode, byte[] Body);
    internal sealed record Record(uint Id, ObjectVersionKind Kind, FrameAddress? PriorAddress, byte[] Body,
        int ActualPayloadBytes);
    internal sealed record Entry(Record Record, FrameAddress ContainingBatch, FrameAddress? SourceGraph,
        FrameAddress? SchemaRoot) {
        internal int PayloadBytes => Record.ActualPayloadBytes;
    }
    internal sealed record Chain(uint Id, FrameAddress Head, long H, IReadOnlyList<Entry> Entries) {
        internal IReadOnlyList<Entry> Records => Entries;
    }
    internal sealed record Batch(FrameAddress Address, FrameAddress? SourceGraph, FrameAddress? SchemaRoot,
        IReadOnlyDictionary<uint, Record> Records);
    // Parent is an observer fact: business predecessor comes only from HeadStore history;
    // Meta predecessor comes from P0G2 PreviousMeta. It is not a business wire field.
    internal sealed record Graph(FrameAddress Address, GraphKind Kind, FrameAddress? Parent, uint RootId,
        FrameAddress DirectoryRoot, FrameAddress? SchemaRoot, FrameAddress? OwnedBatch, FrameAddress? SourceGraph,
        IReadOnlyDictionary<uint, FrameAddress> Heads) {
        internal IReadOnlyDictionary<uint, FrameAddress> Objects => Heads;
    }
    internal sealed record RefImage(HeadEntryId Revision, byte[] Payload);
    internal sealed record Catalog(IReadOnlyList<uint> DeclaredIds, IReadOnlyDictionary<SchemaKey, DurableSchema> Schemas,
        IReadOnlyDictionary<SchemaKey, uint> SchemaIds, IReadOnlyDictionary<ObjectLayout, uint> DescriptorIds,
        IReadOnlyList<uint> LiveIds) {
        internal uint DescriptorId(ObjectLayout layout) => DescriptorIds.TryGetValue(layout, out uint id) ? id
            : throw new InvalidDataException("The actual catalog lacks this complete object layout.");
        internal uint SchemaNodeId(DurableSchema schema) {
            SchemaKey key = new(schema.Type, schema.Version);
            Require(Schemas.TryGetValue(key, out DurableSchema? actual) && actual.Equals(schema), "The actual catalog lacks this complete Schema.");
            return SchemaIds[key];
        }
        internal ObjectLayout ResolveLayout(uint id) => DescriptorIds.SingleOrDefault(pair => pair.Value == id).Key
            ?? throw new InvalidDataException("This ID is not a declared object descriptor.");
    }

    internal sealed class Snapshot {
        internal Snapshot(IReadOnlyDictionary<string, FrameAddress> branchHeads, FrameAddress? sharedSchemaRoot,
            IReadOnlyDictionary<FrameAddress, Graph> graphs, IReadOnlyDictionary<FrameAddress, Batch> batches,
            IReadOnlyDictionary<(FrameAddress Graph, uint Id), Chain> chains,
            IReadOnlyDictionary<FrameAddress, Catalog> catalogs, IReadOnlyList<FrameImage> frames, byte abi,
            IReadOnlyDictionary<uint, RefImage> refs) {
            BranchHeads = branchHeads;
            SharedSchemaRoot = sharedSchemaRoot;
            Graphs = graphs;
            Batches = batches;
            _chains = chains;
            Catalogs = catalogs;
            Frames = frames;
            TypedBodyAbi = abi;
            Refs = refs;
        }
        private readonly IReadOnlyDictionary<(FrameAddress Graph, uint Id), Chain> _chains;
        internal IReadOnlyDictionary<HeadEntryId, FrameAddress> CommitGraphs { get; set; } = new Dictionary<HeadEntryId, FrameAddress>();
        internal FrameAddress GraphAddress(CommitId commit) {
            Span<byte> bytes = stackalloc byte[CommitId.EncodedSize];
            Require(commit.TryWrite(bytes) && HeadEntryId.TryRead(bytes, out _), "Invalid commit handle.");
            HeadEntryId.TryRead(bytes, out HeadEntryId entry);
            return CommitGraphs.TryGetValue(entry, out var graph) ? graph
                : throw new InvalidDataException("Commit is absent from the registered branch histories.");
        }
        internal CommitId CommitAt(FrameAddress graph) {
            HeadEntryId entry = CommitGraphs.Single(pair => pair.Value == graph).Key;
            Span<byte> bytes = stackalloc byte[HeadEntryId.EncodedSize];
            Require(entry.TryWrite(bytes) && CommitId.TryRead(bytes, out _), "Invalid physical commit identity.");
            CommitId.TryRead(bytes, out CommitId commit);
            return commit;
        }
        internal IReadOnlyDictionary<string, FrameAddress> BranchHeads { get; }
        internal FrameAddress? SharedSchemaRoot { get; }
        internal IReadOnlyDictionary<FrameAddress, Graph> Graphs { get; }
        internal IReadOnlyDictionary<FrameAddress, Batch> Batches { get; }
        internal IReadOnlyDictionary<FrameAddress, Catalog> Catalogs { get; }
        internal IReadOnlyList<FrameImage> Frames { get; }
        internal byte TypedBodyAbi { get; }
        internal IReadOnlyDictionary<uint, RefImage> Refs { get; }
        internal Graph Graph(FrameAddress address) => Graphs.TryGetValue(address, out Graph? graph) ? graph
            : throw new InvalidDataException("The requested graph is absent from the completed inventory.");
        internal Chain ObjectChain(FrameAddress graph, uint id) => _chains.TryGetValue((graph, id), out Chain? chain) ? chain
            : throw new InvalidDataException("The requested object is not in that graph's directory.");
    }

    internal static Snapshot Read(string database) {
        using DataStore data = DataStore.OpenReadOnly(Path.Combine(database, "data"));
        using PublicationStore versions = PublicationStore.OpenReadOnly(Path.Combine(database, "versions"), data);
        Dictionary<uint, RefImage> refs = [];
        foreach (uint id in new uint[] { 1, 2, 3 }) {
            HeadEntry value = versions.ReadHead(id).Unwrap();
            refs.Add(id, new(value.Id, value.Payload.ToArray()));
        }
        FrameAddress? directory = Pointer(versions.ReadHead(1).Unwrap().Payload, "P0R3"u8);
        FrameAddress? bookmarks = Pointer(versions.ReadHead(3).Unwrap().Payload, "P0T2"u8);
        IReadOnlyDictionary<string, FrameAddress> shared = Root(versions.ReadHead(2).Unwrap().Payload, "P0S1"u8);
        Require(shared.Count == 0 || shared.Count == 1 && shared.ContainsKey("schema"), "Unknown shared root key.");
        FrameAddress? schema = shared.TryGetValue("schema", out FrameAddress selected) ? selected : null;
        List<FrameInfo> inventory = [];
        data.Inventory(inventory.Add).Unwrap();
        Require(inventory.Count <= Limit, "This finite probe inventory is too large.");
        Dictionary<FrameAddress, RawFrame> raw = [];
        List<FrameImage> images = [];
        foreach (FrameInfo info in inventory) {
            using FrameRead frame = data.ReadFrame(info.Address).Unwrap();
            Require(!frame.IsTombstone && frame.TailMetaLength == 0 &&
                frame.Tag is GraphTag or BatchTag or DirectoryTag, "The probe encountered an unsupported user frame.");
            images.Add(new(info.Address, frame.Tag, Hash(frame.Payload), Hash(frame.TailMeta)));
            raw.Add(info.Address, new(frame.Tag, frame.Payload.ToArray()));
        }
        return new Parser(raw).Read(versions, directory, bookmarks, schema, images, Freeze(refs));
    }

    // A Base contains the typed header; Delta bodies do not. This fixture's
    // business Root/Keeper/List heads are dynamic; meta uses finite employees.
    internal static BaseHeader Header(ReadOnlySpan<byte> bytes) {
        BareValueReader reader = new(bytes);
        byte abi = reader.ReadByte(), route = reader.ReadByte();
        Require(abi == 5, "Unknown typed ABI; this probe supports ABI 5 only.");
        uint? descriptor = null, fixedCode = null;
        switch (route) {
            case 0:
                fixedCode = reader.ReadVarUInt32();
                Require(fixedCode is >= 1 and <= 8, "Unknown finite employee code.");
                break;
            case 1:
                descriptor = reader.ReadVarUInt32();
                Require(descriptor != 0, "A dynamic descriptor must be nonzero.");
                break;
            case 2:
                ReadStructuralType(ref reader);
                break;
            default: throw new InvalidDataException("Unknown typed route.");
        }
        return new(abi, route, descriptor, fixedCode, bytes[reader.ConsumedCount..].ToArray());
    }

    private sealed record RawFrame(uint Tag, byte[] Payload);
    private sealed record Metadata(GraphKind Kind, FrameAddress? Parent, uint RootId, FrameAddress DirectoryRoot,
        FrameAddress? SchemaRoot, FrameAddress? OwnedBatch);
    private sealed record DirectoryVersion(FrameAddress? SourceGraph, Record Record);

    private sealed class Parser(Dictionary<FrameAddress, RawFrame> raw) {
        private readonly Dictionary<FrameAddress, Metadata> _metadata = [];
        private readonly Dictionary<FrameAddress, FrameAddress?> _businessPrevious = [];
        private readonly Dictionary<HeadEntryId, FrameAddress> _commits = [];
        private readonly Dictionary<FrameAddress, Batch> _batches = [];
        private readonly Dictionary<FrameAddress, DirectoryVersion> _directories = [];
        private readonly Dictionary<FrameAddress, IReadOnlyDictionary<uint, FrameAddress>> _heads = [];
        private readonly HashSet<FrameAddress> _readingDirectory = [];
        private readonly Dictionary<(FrameAddress, uint), Chain> _chains = [];

        internal Snapshot Read(PublicationStore versions, FrameAddress? branchDirectory, FrameAddress? bookmarks, FrameAddress? shared, List<FrameImage> images,
            IReadOnlyDictionary<uint, RefImage> refs) {
            foreach (var pair in raw.Where(static pair => pair.Value.Tag == GraphTag)) { _ = MetadataAt(pair.Key); }
            Dictionary<string, FrameAddress> branches = ReadBranches(versions, branchDirectory);
            if (bookmarks is { } tagGraph) { Require(MetadataAt(tagGraph).Kind == GraphKind.Meta, "T is not Meta."); }
            Dictionary<FrameAddress, Graph> graphs = [];
            foreach (var pair in _metadata) {
                // Unpublished business frames retain their inventory/hash evidence, but have no
                // authoritative history position from which an expected content source can be proved.
                if (pair.Value.Kind != GraphKind.Meta && !_businessPrevious.ContainsKey(pair.Key)) { continue; }

                Metadata m = pair.Value;
                FrameAddress? parent = m.Kind == GraphKind.Meta ? m.Parent : _businessPrevious[pair.Key];
                FrameAddress? source = Source(m.Kind, parent, pair.Key);
                var heads = Heads(m.DirectoryRoot);
                Require(heads.ContainsKey(m.RootId), "Graph root is absent from its complete directory.");
                FrameAddress? sourceDirectory = source is { } previous ? MetadataAt(previous).DirectoryRoot : null;
                DirectoryVersion directory = DirectoryAt(m.DirectoryRoot);
                if (sourceDirectory == m.DirectoryRoot) {
                    Require(m.OwnedBatch is null, "A reused directory cannot own a batch.");
                } else {
                    Require(directory.SourceGraph == source, "Directory creator differs from the content source.");
                    Require(directory.Record.Kind == ObjectVersionKind.Base || directory.Record.PriorAddress == sourceDirectory,
                        "Directory Delta does not extend the source directory.");
                }
                if (m.SchemaRoot is { } s) { Require(MetadataAt(s).Kind == GraphKind.Meta, "S is not a meta graph."); }
                if (m.OwnedBatch is { } owned) {
                    Batch batch = BatchAt(owned);
                    Require(batch.SourceGraph == source && batch.SchemaRoot == m.SchemaRoot, "Owned batch source/S mismatch.");
                    foreach (Record record in batch.Records.Values) {
                        Require(heads.TryGetValue(record.Id, out FrameAddress head) && head == owned,
                            "An owned object record is not selected by the complete directory.");
                    }
                }
                graphs.Add(pair.Key, new(pair.Key, m.Kind, parent, m.RootId, m.DirectoryRoot, m.SchemaRoot,
                    m.OwnedBatch, source, heads));
                foreach (var row in heads) { _chains.Add((pair.Key, row.Key), ChainAt(row.Key, row.Value)); }
            }
            foreach (FrameAddress address in branches.Values) { Require(graphs.ContainsKey(address), "Published branch graph is absent."); }
            if (shared is { } sroot) { Require(graphs[sroot].Kind == GraphKind.Meta, "Published shared S is not meta."); }
            Dictionary<FrameAddress, Catalog> catalogs = [];
            HashSet<FrameAddress> catalogRoots = [];
            FrameAddress? nextCatalog = shared;
            while (nextCatalog is { } address) {
                Require(catalogRoots.Add(address), "Shared Schema history cycle.");
                Graph graph = graphs[address];
                Require(graph.Kind == GraphKind.Meta, "Shared Schema history contains business data.");
                catalogs.Add(address, CatalogAt(graph));
                nextCatalog = graph.Parent;
            }
            // Check every Base selected by the registered histories or inventoried Meta graphs,
            // including retained object origins. Unpublished business frames remain inventory-only.
            byte[] abis = _chains.Values.Select(chain => Header(chain.Entries[0].Record.Body).FormatVersion).Distinct().ToArray();
            Require(abis.Length == 1, "Expected one actual typed ABI in the nonempty probe.");
            foreach (var pair in _chains) {
                Chain chain = pair.Value;
                Entry origin = chain.Entries[0];
                BaseHeader header = Header(origin.Record.Body);
                Graph selected = graphs[pair.Key.Item1];
                if (selected.Kind == GraphKind.Meta) {
                    Require(header.Route is 0 or 2 && origin.SchemaRoot is null, "Meta must use fixed or builtin structural interpretation.");
                } else {
                    Require(header.Route == 1 && origin.SchemaRoot is { } originS && catalogs.ContainsKey(originS),
                        "Business Base lacks its exact dynamic origin S.");
                    ObjectLayout stored = catalogs[origin.SchemaRoot!.Value].ResolveLayout(header.DescriptorId!.Value);
                    Require(selected.SchemaRoot is { } selectedS && catalogs.ContainsKey(selectedS), "Business graph lacks its selected S.");
                    _ = catalogs[selected.SchemaRoot!.Value].DescriptorId(stored);
                }
            }
            return new Snapshot(Freeze(branches), shared, Freeze(graphs), Freeze(_batches), Freeze(_chains), Freeze(catalogs),
                Array.AsReadOnly(images.ToArray()), abis[0], refs) { CommitGraphs = Freeze(_commits) };
        }

        private Dictionary<string, FrameAddress> ReadBranches(PublicationStore versions, FrameAddress? directory) {
            Dictionary<string, FrameAddress> branches = new(StringComparer.Ordinal);
            if (directory is not { } address) { return branches; }
            Metadata meta = MetadataAt(address);
            Require(meta.Kind == GraphKind.Meta && meta.SchemaRoot is null, "D requires a fixed Meta graph.");
            var heads = Heads(meta.DirectoryRoot);
            Require(heads.TryGetValue(meta.RootId, out _), "D root is absent from its directory.");
            Chain root = ChainAt(meta.RootId, heads[meta.RootId]);
            ReadOnlySpan<byte> encoded = root.Entries[0].Record.Body;
            Require(encoded.StartsWith(new byte[] { 5, 2, 10, 1, 9, 1, 4 }), "D requires Dictionary<UInt32,String>.");
            BareValueReader reader = new(Header(encoded).Body);
            Require(reader.ReadByte() == 0, "D requires ScalarDefault comparison.");
            Dictionary<uint, uint> rows = [];
            int count = Count(ref reader);
            for (int i = 0; i < count; i++) { rows.Add(Nonzero(ref reader), Nonzero(ref reader)); }
            reader.EnsureFullyConsumed();
            foreach (Entry delta in root.Entries.Skip(1)) {
                reader = new(delta.Record.Body);
                HashSet<uint> changed = [];
                int removed = Count(ref reader);
                for (int i = 0; i < removed; i++) {
                    uint key = Nonzero(ref reader);
                    Require(changed.Add(key) && rows.Remove(key), "Invalid D removal.");
                }
                int patched = Count(ref reader);
                for (int i = 0; i < patched; i++) {
                    uint key = Nonzero(ref reader), value = Nonzero(ref reader);
                    Require(changed.Add(key) && rows.TryGetValue(key, out uint old) && old != value, "Invalid D patch.");
                    rows[key] = value;
                }
                int added = Count(ref reader);
                for (int i = 0; i < added; i++) {
                    uint key = Nonzero(ref reader), value = Nonzero(ref reader);
                    Require(changed.Add(key) && rows.TryAdd(key, value), "Invalid D addition.");
                }
                reader.EnsureFullyConsumed();
            }
            HashSet<HeadId> members = [];
            foreach (uint key in rows.Keys) { members.Add(DecodeHead(key)); }
            foreach (var row in rows) {
                Require(heads.TryGetValue(row.Value, out _), "D label is missing.");
                Chain label = ChainAt(row.Value, heads[row.Value]);
                Require(label.Entries.Count == 1, "D string must be one immutable Base.");
                BaseHeader header = Header(label.Entries[0].Record.Body);
                Require(header.Route == 0 && header.FixedCode == 1, "D label must be a String.");
                BareValueReader text = new(header.Body);
                string name = text.ReadString();
                text.EnsureFullyConsumed();
                Require(name.Length is >= 1 and <= 1024, "Invalid D label length.");
                HeadId head = DecodeHead(row.Key);
                HeadEntry current = versions.ReadHead(head).Unwrap();
                List<HeadEntry> history = [];
                var end = versions.ReadEntryHistory(current.Id, new(Limit, long.MaxValue), entry => {
                    history.Add(entry);
                    return true;
                }).Unwrap();
                Require(end == HeadHistoryEnd.Complete && history.Count != 0, "Expected complete checked branch history.");
                FrameAddress? previous = null;
                for (int i = history.Count - 1; i >= 0; i--) {
                    HeadEntry entry = history[i];
                    Require(members.Contains(entry.Id.HeadId), "History entry owner is not registered in D.");
                    FrameAddress graph = BusinessGraph(entry.Payload);
                    Require(MetadataAt(graph).Kind is GraphKind.Event or GraphKind.State, "Business entry selects Meta.");
                    if (_commits.TryGetValue(entry.Id, out var known)) { Require(known == graph, "Commit graph changed."); }
                    else { _commits.Add(entry.Id, graph); }
                    if (_businessPrevious.TryGetValue(graph, out var prior)) { Require(prior == previous, "Graph history position is ambiguous."); }
                    else { _businessPrevious.Add(graph, previous); }
                    previous = graph;
                }
                Require(branches.TryAdd(name, BusinessGraph(current.Payload)), "This finite fixture expects distinct display labels.");
            }
            return branches;
        }

        private Metadata MetadataAt(FrameAddress address) {
            if (_metadata.TryGetValue(address, out Metadata? found)) { return found; }
            BareValueReader reader = new(Payload(address, GraphTag));
            Magic(ref reader, "P0G2"u8);
            GraphKind kind = (GraphKind)reader.ReadByte();
            Require(kind is GraphKind.Event or GraphKind.State or GraphKind.Meta, "Unknown graph role.");
            FrameAddress? parent = kind == GraphKind.Meta ? Optional(ref reader) : null;
            uint root = Nonzero(ref reader);
            FrameAddress directory = Address(ref reader);
            FrameAddress? schema = Optional(ref reader), batch = Optional(ref reader);
            Require(kind != GraphKind.Meta || schema is null, "Meta cannot borrow S.");
            reader.EnsureFullyConsumed();
            Metadata result = new(kind, parent, root, directory, schema, batch);
            _metadata.Add(address, result);
            return result;
        }

        private FrameAddress? Source(GraphKind kind, FrameAddress? parent, FrameAddress current) {
            HashSet<FrameAddress> visited = [current];
            while (parent is { } address) {
                Require(visited.Add(address), "Graph ancestry contains a cycle.");
                Metadata prior = MetadataAt(address);
                if (kind == GraphKind.Meta) {
                    Require(prior.Kind == GraphKind.Meta, "Meta Parent is not meta.");
                    return address;
                }
                Require(prior.Kind != GraphKind.Meta, "Business ancestry cannot contain meta.");
                if (prior.Kind == GraphKind.State) { return address; }
                parent = _businessPrevious[address];
            }
            return null;
        }

        private Batch BatchAt(FrameAddress address) {
            if (_batches.TryGetValue(address, out Batch? found)) { return found; }
            BareValueReader reader = new(Payload(address, BatchTag));
            Magic(ref reader, "P0B1"u8);
            FrameAddress? source = Optional(ref reader), schema = Optional(ref reader);
            int count = Count(ref reader);
            Require(count != 0 && count <= reader.RemainingCount / 3, "Invalid batch count.");
            Dictionary<uint, Record> records = [];
            uint priorId = 0;
            for (int i = 0; i < count; i++) {
                uint id = Nonzero(ref reader);
                Require(id > priorId, "Batch IDs must be ascending and unique.");
                records.Add(id, Version(ref reader, id));
                priorId = id;
            }
            reader.EnsureFullyConsumed();
            Batch result = new(address, source, schema, Freeze(records));
            _batches.Add(address, result);
            return result;
        }

        private DirectoryVersion DirectoryAt(FrameAddress address) {
            if (_directories.TryGetValue(address, out DirectoryVersion? found)) { return found; }
            BareValueReader reader = new(Payload(address, DirectoryTag));
            Magic(ref reader, "P0D1"u8);
            FrameAddress? source = Optional(ref reader);
            DirectoryVersion result = new(source, Version(ref reader, 0));
            reader.EnsureFullyConsumed();
            _directories.Add(address, result);
            return result;
        }

        private IReadOnlyDictionary<uint, FrameAddress> Heads(FrameAddress address) {
            if (_heads.TryGetValue(address, out var found)) { return found; }
            Require(_readingDirectory.Add(address), "Directory prior cycle.");
            try {
                DirectoryVersion version = DirectoryAt(address);
                BareValueReader reader = new(version.Record.Body);
                Dictionary<uint, FrameAddress> result;
                if (version.Record.Kind == ObjectVersionKind.Base) {
                    Require(reader.ReadByte() == 0, "Directory requires ScalarDefault Dictionary mode.");
                    int count = Count(ref reader);
                    Require(count <= reader.RemainingCount / 4, "Directory count cannot fit.");
                    result = [];
                    for (int i = 0; i < count; i++) { result.Add(Nonzero(ref reader), Locator(ref reader)); }
                } else {
                    FrameAddress source = version.SourceGraph ?? throw new InvalidDataException("Directory Delta lacks source.");
                    Metadata graph = MetadataAt(source);
                    Require(graph.Kind is GraphKind.State or GraphKind.Meta && graph.DirectoryRoot == version.Record.PriorAddress,
                        "Directory prior differs from its source graph.");
                    result = new(Heads(version.Record.PriorAddress!.Value));
                    HashSet<uint> operations = [];
                    int removed = Count(ref reader);
                    for (int i = 0; i < removed; i++) {
                        uint id = Nonzero(ref reader);
                        Require(operations.Add(id) && result.Remove(id), "Invalid repeated/missing directory Remove.");
                    }
                    int patched = Count(ref reader);
                    for (int i = 0; i < patched; i++) {
                        uint id = Nonzero(ref reader);
                        Require(operations.Add(id) && result.ContainsKey(id), "Invalid repeated/missing directory Patch.");
                        FrameAddress prior = result[id], current = PatchLocator(ref reader, prior);
                        Require(current != prior, "Directory Patch must change its locator.");
                        result[id] = current;
                    }
                    int added = Count(ref reader);
                    for (int i = 0; i < added; i++) {
                        uint id = Nonzero(ref reader);
                        Require(operations.Add(id) && !result.ContainsKey(id), "Invalid repeated/existing directory Add.");
                        result.Add(id, Locator(ref reader));
                    }
                }
                reader.EnsureFullyConsumed();
                var frozen = Freeze(result);
                _heads.Add(address, frozen);
                return frozen;
            } finally { _readingDirectory.Remove(address); }
        }

        private Chain ChainAt(uint id, FrameAddress head) {
            List<Entry> reverse = [];
            HashSet<FrameAddress> visited = [];
            FrameAddress address = head;
            long h = 0;
            while (true) {
                Require(visited.Add(address), "Object prior cycle.");
                Batch batch = BatchAt(address);
                Require(batch.Records.TryGetValue(id, out _), "Selected batch has no record for this ID.");
                Record record = batch.Records[id];
                h = checked(h + record.ActualPayloadBytes);
                reverse.Add(new(record, address, batch.SourceGraph, batch.SchemaRoot));
                if (record.Kind == ObjectVersionKind.Base) { break; }
                FrameAddress source = batch.SourceGraph ?? throw new InvalidDataException("Delta lacks source graph.");
                Metadata sourceGraph = MetadataAt(source);
                Require(sourceGraph.Kind is GraphKind.State or GraphKind.Meta, "Delta source is not State/meta.");
                var sourceHeads = Heads(sourceGraph.DirectoryRoot);
                Require(sourceHeads.TryGetValue(id, out FrameAddress prior) && prior == record.PriorAddress,
                    "Delta prior is not this ID's actual source head.");
                address = record.PriorAddress!.Value;
            }
            reverse.Reverse();
            return new(id, head, h, Array.AsReadOnly(reverse.ToArray()));
        }

        private Catalog CatalogAt(Graph graph) {
            BareValueReader root = Employee(graph, graph.RootId, 2);
            uint listId = Nonzero(ref root);
            root.EnsureFullyConsumed();
            uint[] declared = ReferenceList(graph, listId, 5);
            HashSet<uint> declarations = declared.ToHashSet();
            Require(declarations.Count == declared.Length, "Repeated catalog declaration.");
            Dictionary<uint, (byte Kind, uint Type, int Version, uint Fields, uint Element, uint Codec, uint Base)> descriptors = [];
            Dictionary<uint, DurableSchema> schemasById = [];
            Dictionary<uint, TypeExpr> expressions = [];
            Dictionary<uint, DurableFieldInfo> slots = [];
            HashSet<(byte Kind, uint Id)> active = [];
            Dictionary<SchemaKey, DurableSchema> schemas = [];
            Dictionary<SchemaKey, uint> schemaIds = [];
            Dictionary<ObjectLayout, uint> layouts = [];
            foreach (uint id in declared) {
                var node = Descriptor(id);
                ObjectLayout? layout;
                if (node.Kind is 1 or 3) {
                    DurableSchema schema = Schema(id);
                    SchemaKey key = new(schema.Type, schema.Version);
                    Require(schemas.TryAdd(key, schema) && schemaIds.TryAdd(key, id), "Repeated complete Schema key.");
                    layout = schema.Kind == SchemaKind.ReferenceObject ? ObjectLayout.ForDurable(schema) : null;
                } else {
                    Require(node.Kind == 2 && node.Type == 0 && node.Version == 0 && node.Fields == 0 &&
                        node.Element != 0 && node.Codec == 2 && node.Base == 0, "Unsupported List descriptor variant.");
                    DurableFieldInfo element = Field(node.Element);
                    Require(element.FieldId == 1 && element.TypeTag == TypeTag.Nullable &&
                        element.NullableLayout!.ElementSlot.InlineSchema is { } point &&
                        point.SchemaId == PointName && point.Version is 1 or 2, "List must contain a complete nullable inline Point slot.");
                    layout = ObjectLayout.ForList(new ListLayout(element, node.Codec));
                }
                if (layout is not null) { Require(layouts.TryAdd(layout, id), "Repeated complete object layout."); }
            }
            return new(Array.AsReadOnly(declared), Freeze(schemas), Freeze(schemaIds), Freeze(layouts),
                Array.AsReadOnly(graph.Heads.Keys.Order().ToArray()));

            (byte Kind, uint Type, int Version, uint Fields, uint Element, uint Codec, uint Base) Descriptor(uint id) {
                Require(declarations.Contains(id), "An exact descriptor dependency must be declared.");
                if (descriptors.TryGetValue(id, out var prior)) { return prior; }
                BareValueReader reader = Employee(graph, id, 3);
                var node = (Kind: reader.ReadByte(), Type: reader.ReadVarUInt32(), Version: reader.ReadVarInt32(),
                    Fields: reader.ReadVarUInt32(), Element: reader.ReadVarUInt32(), Codec: reader.ReadVarUInt32(), Base: reader.ReadVarUInt32());
                reader.EnsureFullyConsumed();
                descriptors.Add(id, node);
                return node;
            }

            DurableSchema Schema(uint id, int depth = 0) {
                Require(depth < 8, "Unexpected exact Schema depth.");
                if (schemasById.TryGetValue(id, out DurableSchema? prior)) { return prior; }
                Enter(1, id);
                try {
                    var node = Descriptor(id);
                    Require(node.Kind is 1 or 3 && node.Type != 0 && node.Version is 1 or 2 && node.Fields != 0 &&
                        node.Element == 0 && node.Codec == 0 && node.Base == 0, "Unsupported exact Schema variant.");
                    TypeExpr type = Expression(node.Type);
                    Require(type.Kind == TypeExprKind.Named && type.Arguments.Length == 0, "Expected a closed nongeneric probe Schema.");
                    DurableFieldInfo[] fields = ReferenceList(graph, node.Fields, 6).Select(member => Field(member, depth + 1)).ToArray();
                    switch (type.DefinitionId) {
                        case RootName:
                            Require(node.Kind == 1 && node.Version == 1 && fields.SequenceEqual(new[] {
                                DurableFieldInfo.Reference(1, TypeExpr.List(TypeExpr.Nullable(TypeExpr.Named(PointName)))),
                                DurableFieldInfo.Reference(2, TypeExpr.Named(RootName)),
                            }), "Root must have its two exact nominal reference fields.");
                            break;
                        case PointName:
                            Require(node.Kind == 3 && fields.Length == 4 && fields[0].FieldId == 1 &&
                                ((node.Version == 1 && fields[0].TypeTag == TypeTag.Int32) || (node.Version == 2 && fields[0].TypeTag == TypeTag.Int64)) &&
                                fields.Skip(1).SequenceEqual(new[] { new DurableFieldInfo(2, TypeTag.Double),
                                    new DurableFieldInfo(3, TypeTag.Double), new DurableFieldInfo(4, TypeTag.Double) }),
                                "Point must contain X and three exact Double padding fields.");
                            break;
                        case KeeperName:
                            Require(node.Kind == 1 && node.Version == 1 && fields.SequenceEqual(new[] { new DurableFieldInfo(1, TypeTag.Int64) }),
                                "Keeper must have its exact Int64 value field.");
                            break;
                        default: throw new InvalidDataException("Unknown Schema outside the finite probe.");
                    }
                    DurableSchema result = new(type, node.Version, fields, null,
                        node.Kind == 3 ? SchemaKind.InlineValue : SchemaKind.ReferenceObject);
                    schemasById.Add(id, result);
                    return result;
                } finally { active.Remove((1, id)); }
            }

            DurableFieldInfo Field(uint id, int depth = 0) {
                Require(depth < 8, "Unexpected field dependency depth.");
                if (slots.TryGetValue(id, out DurableFieldInfo prior)) { return prior; }
                Enter(2, id);
                try {
                    BareValueReader reader = Employee(graph, id, 4);
                    int fieldId = reader.ReadVarInt32();
                    TypeTag tag = (TypeTag)reader.ReadByte();
                    uint target = reader.ReadVarUInt32(), inline = reader.ReadVarUInt32(), nullable = reader.ReadVarUInt32();
                    reader.EnsureFullyConsumed();
                    DurableFieldInfo result;
                    if (tag is TypeTag.Int32 or TypeTag.Int64 or TypeTag.Double) {
                        Require(target == 0 && inline == 0 && nullable == 0, "Scalar slot contains inactive operands.");
                        result = new(fieldId, tag);
                    } else if (tag == TypeTag.ObjectReference) {
                        Require(target != 0 && inline == 0 && nullable == 0, "Reference slot has wrong operands.");
                        result = DurableFieldInfo.Reference(fieldId, Expression(target));
                    } else if (tag == TypeTag.InlineValue) {
                        Require(target == 0 && inline != 0 && nullable == 0, "Inline slot has wrong operands.");
                        DurableSchema point = Schema(inline, depth + 1);
                        Require(point.SchemaId == PointName && point.Kind == SchemaKind.InlineValue, "Only inline Point is supported.");
                        result = new(fieldId, tag, inlineSchema: point);
                    } else {
                        Require(tag == TypeTag.Nullable && target == 0 && inline == 0 && nullable != 0, "Unsupported field variant.");
                        DurableFieldInfo child = Field(nullable, depth + 1);
                        Require(child.FieldId == 1 && child.TypeTag == TypeTag.InlineValue, "Nullable must contain canonical inline Point.");
                        result = DurableFieldInfo.Nullable(fieldId, child);
                    }
                    slots.Add(id, result);
                    return result;
                } finally { active.Remove((2, id)); }
            }

            TypeExpr Expression(uint id, int depth = 0) {
                Require(depth < 8, "Unexpected nominal expression depth.");
                if (expressions.TryGetValue(id, out TypeExpr? prior)) { return prior; }
                Enter(3, id);
                try {
                    BareValueReader reader = Employee(graph, id, 7);
                    TypeExprKind kind = (TypeExprKind)reader.ReadByte();
                    byte builtin = reader.ReadByte();
                    uint name = reader.ReadVarUInt32(), arguments = reader.ReadVarUInt32();
                    reader.EnsureFullyConsumed();
                    Require(builtin == 0, "This probe's nominal expressions contain no builtin leaf.");
                    TypeExpr result;
                    if (kind == TypeExprKind.Named) {
                        Require(name != 0 && arguments == 0, "Expected a nongeneric named expression.");
                        BareValueReader text = Employee(graph, name, 1);
                        string value = text.ReadString();
                        text.EnsureFullyConsumed();
                        Require(value is RootName or PointName or KeeperName, "Unknown nominal name.");
                        result = TypeExpr.Named(value);
                    } else {
                        Require(kind is TypeExprKind.List or TypeExprKind.Nullable && name == 0 && arguments != 0, "Unsupported expression variant.");
                        uint[] args = ReferenceList(graph, arguments, 8);
                        Require(args.Length == 1, "List/Nullable requires one operand.");
                        TypeExpr child = Expression(args[0], depth + 1);
                        result = kind == TypeExprKind.List ? TypeExpr.List(child) : TypeExpr.Nullable(child);
                    }
                    expressions.Add(id, result);
                    return result;
                } finally { active.Remove((3, id)); }
            }

            void Enter(byte kind, uint id) => Require(id != 0 && active.Add((kind, id)), "Employee construction dependency cycle.");
        }

        private BareValueReader Employee(Graph graph, uint id, uint expectedCode) {
            Chain chain = _chains[(graph.Address, id)];
            Require(chain.Entries.Count == 1 && chain.Entries[0].SchemaRoot is null,
                "The finite immutable employee row must have one fixed Base.");
            BaseHeader header = Header(chain.Entries[0].Record.Body);
            Require(header.Route == 0 && header.FixedCode == expectedCode, "Employee body has the wrong finite code.");
            return new(header.Body);
        }

        private uint[] ReferenceList(Graph graph, uint id, uint code) {
            Chain chain = _chains[(graph.Address, id)];
            Require(chain.Entries.All(static entry => entry.SchemaRoot is null) &&
                (code == 5 || chain.Entries.Count == 1), "Only the declaration List may have employee Delta history.");
            BaseHeader header = Header(chain.Entries[0].Record.Body);
            Require(header.Route == 0 && header.FixedCode == code, "Wrong employee reference List type.");
            BareValueReader reader = new(header.Body);
            int count = Count(ref reader);
            Require(count <= reader.RemainingCount, "Reference List count cannot fit.");
            uint[] values = new uint[count];
            for (int i = 0; i < count; i++) { values[i] = Nonzero(ref reader); }
            reader.EnsureFullyConsumed();
            foreach (Entry delta in chain.Entries.Skip(1)) {
                reader = new(delta.Record.Body);
                int nextCount = Count(ref reader), output = 0;
                uint[] next = new uint[nextCount];
                while (output < nextCount) {
                    byte operation = reader.ReadByte();
                    int start = operation is 1 or 3 ? Count(ref reader) : 0;
                    int length = Count(ref reader);
                    Require(length > 0 && length <= nextCount - output, "Bad employee List range.");
                    if (operation is 1 or 3) {
                        Require(start <= values.Length && length <= values.Length - start, "Employee List copy exceeds prior.");
                        values.AsSpan(start, length).CopyTo(next.AsSpan(output, length));
                        if (operation == 3) {
                            uint last = 0;
                            while (true) {
                                uint position = reader.ReadVarUInt32();
                                if (position == 0) { break; }
                                Require(position > last && position <= length, "Employee List patch positions must be unique and ordered.");
                                int index = output + (int)position - 1;
                                uint value = Nonzero(ref reader);
                                Require(value != next[index], "An employee reference patch must change its value.");
                                next[index] = value;
                                last = position;
                            }
                            Require(last != 0, "A CopyAndPatch range requires a changed element.");
                        }
                    } else {
                        Require(operation == 2 && length <= reader.RemainingCount, "Unknown employee reference List range.");
                        for (int i = 0; i < length; i++) { next[output + i] = Nonzero(ref reader); }
                    }
                    output += length;
                }
                reader.EnsureFullyConsumed();
                values = next;
            }
            return values;
        }

        private ReadOnlySpan<byte> Payload(FrameAddress address, uint tag) {
            Require(raw.TryGetValue(address, out RawFrame? frame) && frame.Tag == tag, "Wrong/missing completed frame role.");
            return raw[address].Payload;
        }
    }

    private static HeadId DecodeHead(uint encoded) {
        Span<byte> bytes = stackalloc byte[HeadId.EncodedSize];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, encoded);
        Require(HeadId.TryRead(bytes, out HeadId head), "Invalid D HeadId.");
        return head;
    }

    private static FrameAddress BusinessGraph(ReadOnlySpan<byte> payload) {
        const int overhead = 4 + FrameAddress.EncodedSize + sizeof(uint);
        Require(payload.Length >= overhead && payload.Length <= PublicationStore.MaxHeadEntryPayloadLength &&
            payload[..4].SequenceEqual("P0E1"u8), "Invalid business entry descriptor.");
        Require(FrameAddress.TryRead(payload.Slice(4, FrameAddress.EncodedSize), out FrameAddress graph), "Invalid business graph address.");
        Require(BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4 + FrameAddress.EncodedSize)) == payload.Length - overhead,
            "Business summary length differs from its owned bytes.");
        return graph;
    }

    private static FrameAddress? Pointer(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> magic) {
        BareValueReader reader = new(payload);
        Magic(ref reader, magic);
        FrameAddress? graph = Optional(ref reader);
        reader.EnsureFullyConsumed();
        return graph;
    }

    private static void ReadStructuralType(ref BareValueReader reader) {
        // Closed finite support: business List<Int32>, D Dictionary<UInt32,String>,
        // T Dictionary<String,Byte[]> and its Byte[] leaves. No product codec is invoked.
        byte type = reader.ReadByte();
        if (type == 8) {
            Require(reader.ReadByte() == 1 && reader.ReadByte() == 2, "Expected List<Int32>.");
        } else if (type == 4) {
            Require(reader.ReadByte() == 1 && reader.ReadByte() == 5, "Expected Byte[].");
        } else {
            Require(type == 10 && reader.ReadByte() == 1, "Unknown finite structural type.");
            byte key = reader.ReadByte();
            if (key == 9) {
                Require(reader.ReadByte() == 1 && reader.ReadByte() == 4, "Expected Dictionary<UInt32,String>.");
            } else {
                Require(key == 4 && reader.ReadByte() == 4 && reader.ReadByte() == 1 && reader.ReadByte() == 5,
                    "Expected Dictionary<String,Byte[]>.");
            }
        }
    }

    private static IReadOnlyDictionary<string, FrameAddress> Root(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> magic) {
        Require(bytes.StartsWith(magic), "Unknown publication root format.");
        return NamedFrameMapCodec.Decode(bytes[magic.Length..]).Unwrap();
    }
    private static Record Version(ref BareValueReader reader, uint id) {
        int start = reader.ConsumedCount;
        ObjectVersionKind kind = (ObjectVersionKind)reader.ReadByte();
        Require(kind is ObjectVersionKind.Base or ObjectVersionKind.Delta, "Unknown object version kind.");
        FrameAddress? prior = kind == ObjectVersionKind.Delta ? Address(ref reader) : null;
        byte[] body = reader.ReadBytes().ToArray();
        return new(id, kind, prior, body, checked(reader.ConsumedCount - start));
    }
    private static FrameAddress Address(ref BareValueReader reader) {
        Require(FrameAddress.TryRead(reader.ReadRawBytes(FrameAddress.EncodedSize), out FrameAddress address), "Invalid fixed address.");
        return address;
    }
    private static FrameAddress? Optional(ref BareValueReader reader) => reader.ReadBoolean() ? Address(ref reader) : null;
    private static FrameAddress Locator(ref BareValueReader reader) => Locator(reader.ReadVarUInt32(), reader.ReadVarUInt32(), reader.ReadVarUInt32());
    private static FrameAddress Locator(uint word0, uint word1, uint word2) {
        Span<byte> bytes = stackalloc byte[FrameAddress.EncodedSize];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, word0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[4..], word1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[8..], word2);
        Require(FrameAddress.TryRead(bytes, out FrameAddress address), "Invalid directory locator bits.");
        return address;
    }
    private static FrameAddress PatchLocator(ref BareValueReader reader, FrameAddress prior) {
        Span<byte> bytes = stackalloc byte[FrameAddress.EncodedSize];
        Require(prior.TryWrite(bytes), "Invalid prior locator.");
        uint word0 = BinaryPrimitives.ReadUInt32LittleEndian(bytes), word1 = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]),
            word2 = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        byte mask = reader.ReadByte();
        Require(mask is >= 1 and <= 7, "Locator patch requires a nonempty three-field bitmap.");
        if ((mask & 1) != 0) { word0 = reader.ReadVarUInt32(); }
        if ((mask & 2) != 0) { word1 = reader.ReadVarUInt32(); }
        if ((mask & 4) != 0) { word2 = reader.ReadVarUInt32(); }
        return Locator(word0, word1, word2);
    }
    private static int Count(ref BareValueReader reader) {
        uint count = reader.ReadVarUInt32();
        Require(count <= Limit, "This finite probe count is too large.");
        return (int)count;
    }
    private static uint Nonzero(ref BareValueReader reader) {
        uint value = reader.ReadVarUInt32();
        Require(value != 0, "A referenced ObjectId must be nonzero.");
        return value;
    }
    private static void Magic(ref BareValueReader reader, ReadOnlySpan<byte> magic) =>
        Require(reader.ReadRawBytes(magic.Length).SequenceEqual(magic), "Unknown probe frame wire version.");
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static IReadOnlyDictionary<K, V> Freeze<K, V>(Dictionary<K, V> values) where K : notnull =>
        new ReadOnlyDictionary<K, V>(values);
    private static void Require(bool condition, string message) {
        if (!condition) { throw new InvalidDataException(message); }
    }
}
