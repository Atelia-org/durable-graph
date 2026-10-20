extern alias persistence;
using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using Atelia.Binary;
using CommitId = persistence::Atelia.DurableGraph.CommitId;
using Atelia.DurableGraph.Storage;
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
    private const string WidgetName = "package.split.widget";
    private const string CatalogEventName = "probe.private.unreachable";

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
    internal sealed record Catalog(IReadOnlyList<uint> DeclaredIds, IReadOnlyDictionary<int, uint> WidgetDescriptors,
        IReadOnlyDictionary<int, IReadOnlyList<int>> WidgetFields, uint? CatalogEventDescriptor = null);

    internal sealed class Snapshot {
        internal Snapshot(IReadOnlyDictionary<string, FrameAddress> branchHeads, FrameAddress? sharedSchemaRoot,
            IReadOnlyDictionary<FrameAddress, Graph> graphs, IReadOnlyDictionary<FrameAddress, Batch> batches,
            IReadOnlyDictionary<(FrameAddress Graph, uint Id), Chain> chains,
            IReadOnlyDictionary<FrameAddress, Catalog> catalogs, IReadOnlyList<FrameImage> frames, byte abi) {
            BranchHeads = branchHeads;
            SharedSchemaRoot = sharedSchemaRoot;
            Graphs = graphs;
            Batches = batches;
            _chains = chains;
            Catalogs = catalogs;
            Frames = frames;
            TypedBodyAbi = abi;
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
        internal Graph Graph(FrameAddress address) => Graphs.TryGetValue(address, out Graph? graph) ? graph
            : throw new InvalidDataException("The requested graph is absent from the completed inventory.");
        internal Chain ObjectChain(FrameAddress graph, uint id) => _chains.TryGetValue((graph, id), out Chain? chain) ? chain
            : throw new InvalidDataException("The requested object is not in that graph's directory.");
    }

    internal static Snapshot Read(string database) {
        using DataStore data = DataStore.OpenReadOnly(Path.Combine(database, "data"));
        using PublicationStore versions = PublicationStore.OpenReadOnly(Path.Combine(database, "versions"), data);
        FrameAddress? directory = Pointer(versions.ReadHead(1).Unwrap().Payload, "P0R4"u8);
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
        return new Parser(raw).Read(versions, directory, bookmarks, schema, images);
    }

    // A Base contains the typed header; Delta bodies do not. Structural support
    // deliberately stops at this fixture's List<int>, rather than cloning TypeExpr.
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

        internal Snapshot Read(PublicationStore versions, FrameAddress? branchDirectory, FrameAddress? bookmarks, FrameAddress? shared, List<FrameImage> images) {
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
                if (m.Kind == GraphKind.Event) {
                    Require(source is null && m.OwnedBatch is not null &&
                        directory.SourceGraph is null && directory.Record.Kind == ObjectVersionKind.Base &&
                        directory.Record.PriorAddress is null, "Event requires an independent Base directory and owned batch.");
                }
                if (m.OwnedBatch is { } owned) {
                    Batch batch = BatchAt(owned);
                    Require(batch.SourceGraph == source && batch.SchemaRoot == m.SchemaRoot, "Owned batch source/S mismatch.");
                    if (m.Kind == GraphKind.Event) {
                        Require(batch.Records.Count > 0 && batch.Records.Count == heads.Count &&
                            heads.Values.All(head => head == owned) &&
                            batch.Records.Values.All(record => record.Kind == ObjectVersionKind.Base && record.PriorAddress is null),
                            "Event must own every live object as a complete Base without retained heads or Deltas.");
                    }
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
            foreach (var chain in _chains.Values) {
                Entry origin = chain.Entries[0];
                BaseHeader header = Header(origin.Record.Body);
                if (header.Route == 1) {
                    Require(origin.SchemaRoot is { } originS && catalogs.TryGetValue(originS, out Catalog? catalog) &&
                        catalog.DeclaredIds.Contains(header.DescriptorId!.Value), "Dynamic Base lacks its origin-S declaration.");
                }
            }
            return new Snapshot(Freeze(branches), shared, Freeze(graphs), Freeze(_batches), Freeze(_chains), Freeze(catalogs),
                Array.AsReadOnly(images.ToArray()), abis[0]) { CommitGraphs = Freeze(_commits) };
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
            if (kind == GraphKind.Event) { return null; }
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
            Require(declared.Distinct().Count() == declared.Length, "Repeated catalog declaration.");
            Dictionary<int, uint> widgets = [];
            Dictionary<int, IReadOnlyList<int>> fieldsByVersion = [];
            uint? catalogEvent = null;
            foreach (uint id in declared) {
                BareValueReader descriptor = Employee(graph, id, 3);
                byte kind = descriptor.ReadByte();
                uint type = Nonzero(ref descriptor);
                int version = descriptor.ReadVarInt32();
                uint fields = Nonzero(ref descriptor);
                uint element = descriptor.ReadVarUInt32(), codec = descriptor.ReadVarUInt32(), ancestor = descriptor.ReadVarUInt32();
                descriptor.EnsureFullyConsumed();
                string name = TypeName(graph, type);
                Require(kind == 1 && element == 0 && codec == 0 && ancestor == 0,
                    "The finite probe requires a reference descriptor without container or base slots.");
                uint[] members = ReferenceList(graph, fields, 6);
                if (name == CatalogEventName) {
                    Require(version == 1 && catalogEvent is null && members.Length == 1,
                        "The State-unreachable Event declaration must be unique V1 with one field.");
                    BareValueReader unused = Employee(graph, members[0], 4);
                    Require(unused.ReadVarInt32() == 1 && unused.ReadByte() == 2 && unused.ReadVarUInt32() == 0 &&
                        unused.ReadVarUInt32() == 0 && unused.ReadVarUInt32() == 0,
                        "The State-unreachable Event declaration requires exactly Int32 field1 with no composed slots.");
                    unused.EnsureFullyConsumed();
                    catalogEvent = id;
                    continue;
                }
                Require(name == WidgetName && version is 1 or 2, "Unknown declaration outside the finite probe.");
                List<int> fieldIds = [];
                foreach (uint member in members) {
                    BareValueReader field = Employee(graph, member, 4);
                    int fieldId = field.ReadVarInt32();
                    byte tag = field.ReadByte();
                    uint target = field.ReadVarUInt32(), inline = field.ReadVarUInt32(), nullable = field.ReadVarUInt32();
                    field.EnsureFullyConsumed();
                    Require(inline == 0 && nullable == 0, "Unexpected composed Widget field.");
                    Require(fieldId switch {
                        1 => tag == (version == 1 ? 2 : 3) && target == 0,
                        2 => tag == 15 && target != 0 && TypeName(graph, target) == "List<Int32>",
                        3 => tag == 15 && target != 0 && TypeName(graph, target) == WidgetName,
                        _ => false,
                    }, "Unexpected complete Widget member shape.");
                    fieldIds.Add(fieldId);
                }
                Require(fieldIds.SequenceEqual(new[] { 1, 2, 3 }), "Widget must declare its three ordered members.");
                widgets.Add(version, id);
                fieldsByVersion.Add(version, fieldIds.AsReadOnly());
            }
            return new(Array.AsReadOnly(declared), Freeze(widgets), Freeze(fieldsByVersion), catalogEvent);
        }

        private string TypeName(Graph graph, uint id, int depth = 0) {
            Require(depth < 8, "Probe type declaration is unexpectedly deep.");
            BareValueReader type = Employee(graph, id, 7);
            byte kind = type.ReadByte(), builtin = type.ReadByte();
            uint name = type.ReadVarUInt32(), arguments = type.ReadVarUInt32();
            type.EnsureFullyConsumed();
            if (kind == 1) {
                Require(builtin == 2 && name == 0 && arguments == 0, "Only the Int32 builtin type is needed here.");
                return "Int32";
            }
            if (kind == 2) {
                Require(builtin == 0 && name != 0 && arguments == 0, "Expected a nongeneric named probe type.");
                BareValueReader text = Employee(graph, name, 1);
                string value = text.ReadString();
                text.EnsureFullyConsumed();
                return value;
            }
            Require(kind == 8 && builtin == 0 && name == 0 && arguments != 0, "Unsupported probe nominal type.");
            uint[] args = ReferenceList(graph, arguments, 8);
            Require(args.Length == 1, "List needs exactly one argument.");
            return "List<" + TypeName(graph, args[0], depth + 1) + ">";
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
                    int start = operation == 1 ? Count(ref reader) : 0;
                    int length = Count(ref reader);
                    Require(length > 0 && length <= nextCount - output, "Bad employee List range.");
                    if (operation == 1) {
                        Require(start <= values.Length && length <= values.Length - start, "Employee List copy exceeds prior.");
                        values.AsSpan(start, length).CopyTo(next.AsSpan(output, length));
                    } else {
                        Require(operation == 2 && length <= reader.RemainingCount, "Only employee List Copy/New is supported.");
                        for (int i = 0; i < length; i++) { next[output + i] = Nonzero(ref reader); }
                    }
                    output += length;
                }
                reader.EnsureFullyConsumed();
                Require(next.Length >= values.Length && next.AsSpan(0, values.Length).SequenceEqual(values),
                    "Shared catalog declaration history must append an unchanged prefix.");
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
