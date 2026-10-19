using System.Buffers.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using Atelia.HeadStore;
using DataStore = Atelia.FrameStore.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class ManagementDirectoryReaderTests : IDisposable {
    private readonly string _path = Path.Combine(Path.GetTempPath(), "dg-management-reader-" + Guid.NewGuid().ToString("N"));
    private static DictionaryLayout Branches => ManagementDirectoryReader.BranchesLayout;
    private static DictionaryLayout Bookmarks => ManagementDirectoryReader.BookmarksLayout;
    private static ArrayLayout Bytes => ManagementDirectoryReader.EntryBytesLayout;

    [Fact]
    public void Cold_branch_delta_preserves_real_snapshot_heads_H_and_duplicate_labels_without_a_HeadStore() {
        FrameAddress current, prior, labelHead;
        using (DataStore data = DataStore.Create(_path))
        using (GraphRecordStore graphs = new(data)) {
            FrozenDictionaryState<uint, ObjectId> first = BranchMap((42, 2));
            StoredGraphRevision initial = graphs.AppendMeta(null,
                new(null, new(1), [BranchRow(1, first), Text(2, "世界线😀")]));
            prior = initial.Address;
            labelHead = initial.ObjectHeads[2];
            PreparedDeltaBody delta = DictionaryStateBody<uint, ObjectId, UInt32StateOps, StringIdStateOps>
                .PrepareDelta(first, BranchMap((42, 2), (43, 3)), Branches);
            current = graphs.AppendMeta(prior, new(prior, new(1), [
                ObjectVersionRecord.CreateDelta(1, initial.ObjectHeads[1], delta.Body), Text(3, "世界线😀"),
                Text(4, "") // An unreferenced leaf is not a label.
            ])).Address;
        }
        using (DataStore data = DataStore.Open(_path))
        using (GraphRecordStore graphs = new(data)) {
            DecodedGraphRows decoded = ManagementDirectoryReader.ReadBranches(graphs, current);
            Assert.Equal(current, decoded.Snapshot.Address);
            Assert.Same(graphs, decoded.Snapshot.SourceOwner);
            Assert.Equal(prior, decoded.Snapshot.SourceGraph);
            Assert.Null(decoded.Snapshot.SchemaRoot);
            Assert.Equal(labelHead, decoded.Snapshot.ObjectHeads[2]);
            Assert.Equal(2, decoded.Rows[new(1)].GetDictionaryState<uint, ObjectId>().Count);
            Assert.Equal(decoded.Rows[new(2)].StringContent, decoded.Rows[new(3)].StringContent);
            ObjectVersionChain chain = graphs.ReadObjectChain(graphs.ReadMetaGraph(current), 1);
            Assert.Equal(2, chain.Entries.Count);
            Assert.Equal(ObjectVersionKind.Delta, chain.Entries[1].Record.Kind);
            Assert.Equal(chain.ReconstructionPayloadBytes, decoded.Storage[new(1)].ReconstructionPayloadBytes);
            Assert.Equal(chain.Head, decoded.Storage[new(1)].Head);
            Assert.Throws<InvalidDataException>(() => StoredGraphDecoder.Read(graphs, graphs.ReadMetaGraph(current)));
        }
    }

    [Fact]
    public void Cold_bookmark_delta_keeps_Ordinal_names_and_exact_owned_entry_bytes() {
        FrameAddress current;
        byte[] entry = EntryBytes();
        using (DataStore data = DataStore.Create(_path))
        using (GraphRecordStore graphs = new(data)) {
            FrozenDictionaryState<ObjectId, ObjectId> first = BookmarkMap((2, 3));
            StoredGraphRevision initial = graphs.AppendMeta(null,
                new(null, new(1), [BookmarkRow(1, first), Text(2, "Save"), ByteRow(3, entry)]));
            PreparedDeltaBody delta = DictionaryStateBody<ObjectId, ObjectId, StringIdStateOps, ObjectIdStateOps>
                .PrepareDelta(first, BookmarkMap((2, 3), (4, 5)), Bookmarks);
            current = graphs.AppendMeta(initial.Address, new(initial.Address, new(1), [
                ObjectVersionRecord.CreateDelta(1, initial.ObjectHeads[1], delta.Body), Text(4, "save"), ByteRow(5, entry),
                Text(6, ""), ByteRow(7, []) // Unreferenced builtin leaves need no bookmark role.
            ])).Address;
        }
        using (DataStore data = DataStore.Open(_path))
        using (GraphRecordStore graphs = new(data)) {
            DecodedGraphRows decoded = ManagementDirectoryReader.ReadBookmarks(graphs, current);
            FrozenDictionaryState<ObjectId, ObjectId> map = decoded.Rows[new(1)].GetDictionaryState<ObjectId, ObjectId>();
            Assert.Equal(DictionaryComparerKind.StringOrdinal, map.ComparerKind);
            Assert.Equal(2, map.Count);
            Assert.Equal(entry, decoded.Rows[new(3)].GetArrayState<byte>().Elements.ToArray());
            Assert.Equal(entry, decoded.Rows[new(5)].GetArrayState<byte>().Elements.ToArray());
            Assert.True(HeadEntryId.TryRead(decoded.Rows[new(3)].GetArrayState<byte>().Elements, out _));
            entry[0] ^= 0xFF;
            Assert.NotEqual(entry, decoded.Rows[new(3)].GetArrayState<byte>().Elements.ToArray());
            Assert.Throws<InvalidDataException>(() => StoredGraphDecoder.Read(graphs, graphs.ReadMetaGraph(current)));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Management_requires_Meta_and_exact_dictionary_root(bool bookmark) {
        using DataStore data = DataStore.Create(_path);
        using GraphRecordStore graphs = new(data);
        ObjectVersionRecord map = bookmark ? BookmarkRow(1, BookmarkMap()) : BranchRow(1, BranchMap());
        StoredGraphRevision business = graphs.AppendBusiness(StoredGraphKind.State, null, null, new(null, new(1), [map]));
        Assert.Contains("Meta", Assert.Throws<InvalidDataException>(() => Read(graphs, business.Address, bookmark)).Message);
        StoredGraphRevision wrongRoot = graphs.AppendMeta(null, new(null, new(2), [map, Text(2, "text root")]));
        Assert.Contains("exact Dictionary root", Assert.Throws<InvalidDataException>(() => Read(graphs, wrongRoot.Address, bookmark)).Message);
        byte[] malformed = GraphFrameCodec.EncodeGraph(StoredGraphKind.State,
            null, new(1), wrongRoot.DirectoryRoot, business.Address, wrongRoot.OwnedBatch);
        // Meta has an extra optional PreviousMeta field; preserve the P0G2 wire shape before changing role.
        malformed = [.. malformed.AsSpan(0, 5), (byte)0, .. malformed.AsSpan(5)];
        malformed[4] = (byte)StoredGraphKind.Meta; // Bypass only the writer's Meta/Schema guard, retaining valid frame CRC.
        FrameAddress withSchema = data.Append(GraphFrameCodec.GraphTag, malformed).Unwrap();
        Assert.Throws<InvalidDataException>(() => Read(graphs, withSchema, bookmark));
    }

    [Fact]
    public void Role_text_accepts_length_boundaries_and_preserves_whitespace_and_canonical_distinctions() {
        string[] texts = [" ", new string('中', 1022) + "😀", "é", "e\u0301"];
        using DataStore data = DataStore.Create(_path);
        using GraphRecordStore graphs = new(data);
        List<ObjectVersionRecord> dRows = [], tRows = [];
        for (int i = 0; i < texts.Length; i++) {
            dRows.Add(Text((uint)i + 2, texts[i]));
            tRows.Add(Text((uint)i + 2, texts[i]));
        }
        dRows.Add(BranchRow(1, BranchMap((1, 2), (2, 3), (3, 4), (4, 5))));
        tRows.Add(BookmarkRow(1, BookmarkMap((2, 6), (3, 6), (4, 6), (5, 6))));
        tRows.Add(ByteRow(6, EntryBytes()));
        StoredGraphRevision d = graphs.AppendMeta(null, new(null, new(1), dRows));
        StoredGraphRevision t = graphs.AppendMeta(null, new(null, new(1), tRows));
        DecodedGraphRows decodedD = ManagementDirectoryReader.ReadBranches(graphs, d.Address);
        DecodedGraphRows decodedT = ManagementDirectoryReader.ReadBookmarks(graphs, t.Address);
        for (uint i = 0; i < texts.Length; i++) {
            Assert.Equal(texts[i], decodedD.Rows[new(i + 2)].StringContent);
            Assert.Equal(texts[i], decodedT.Rows[new(i + 2)].StringContent);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Dynamic_schema_in_retained_Base_or_Delta_is_rejected_before_its_content_is_read(bool bookmark, bool deltaSchema) {
        using DataStore data = DataStore.Create(_path);
        using GraphRecordStore graphs = new(data);
        ObjectVersionRecord empty = bookmark ? BookmarkRow(1, BookmarkMap()) : BranchRow(1, BranchMap());
        FrameAddress poison = data.Append(GraphFrameCodec.DirectoryTag, new byte[] { 0xFF }).Unwrap();
        FrameAddress batch = data.Append(GraphFrameCodec.BatchTag,
            GraphFrameCodec.EncodeBatch(null, deltaSchema ? null : poison, [empty])).Unwrap();
        FrameAddress directory = RawDirectory(data, null, batch);
        FrameAddress previous = data.Append(GraphFrameCodec.GraphTag,
            GraphFrameCodec.EncodeGraph(StoredGraphKind.Meta, null, new(1), directory, null, null)).Unwrap();
        if (deltaSchema) {
            // Ordinary Dictionary no-op Delta: zero removals, patches and additions.
            batch = data.Append(GraphFrameCodec.BatchTag, GraphFrameCodec.EncodeBatch(previous, poison,
                [ObjectVersionRecord.CreateDelta(1, batch, new byte[] { 0, 0, 0 })])).Unwrap();
            directory = RawDirectory(data, previous, batch);
            previous = data.Append(GraphFrameCodec.GraphTag,
                GraphFrameCodec.EncodeGraph(StoredGraphKind.Meta, previous, new(1), directory, null, null)).Unwrap();
        }
        FrameAddress selected = data.Append(GraphFrameCodec.GraphTag,
            GraphFrameCodec.EncodeGraph(StoredGraphKind.Meta, previous, new(1), directory, null, null)).Unwrap();

        // Physical relations are valid and selected Schema is null. Preload only the
        // actual chain, so a SchemaCatalogView.Load(poison) would add a GraphMiss.
        Assert.Null(graphs.ReadMetaGraph(selected).SchemaRoot);
        ObjectVersionChain chain = graphs.ReadObjectChain(graphs.ReadMetaGraph(selected), 1);
        Assert.Equal(poison, chain.Entries[deltaSchema ? 1 : 0].SchemaRoot);
        long graphMisses = graphs.ReadCacheStatistics.GraphMisses;
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => Read(graphs, selected, bookmark));
        Assert.Equal("A management object chain cannot use a dynamic Schema root.", error.Message);
        Assert.Equal(graphMisses, graphs.ReadCacheStatistics.GraphMisses);
        if (!deltaSchema) {
            // The formerly proposed builtin-only environment really attempts that read.
            Assert.Throws<InvalidDataException>(() => StoredGraphDecoder.Read(graphs, graphs.ReadMetaGraph(selected), new StateModelRegistry().CreateEnvironment()));
            Assert.Equal(graphMisses + 1, graphs.ReadCacheStatistics.GraphMisses);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void All_live_layouts_are_checked_before_their_body_reader(bool bookmark) {
        using DataStore data = DataStore.Create(_path);
        using GraphRecordStore graphs = new(data);
        ObjectVersionRecord root = bookmark ? BookmarkRow(1, BookmarkMap()) : BranchRow(1, BranchMap());
        // A valid typed layout header and deliberately invalid object body. The closure
        // diagnostic proves rejection precedes executing that employee/container reader.
        ObjectLayout employee = BootstrapBindings.Layout(typeof(SchemaCatalogRecord));
        foreach (ObjectLayout outside in new[] { employee, ObjectLayout.ForArray(new(TypeExprKind.VectorArray, new(1, TypeTag.Int32))) }) {
            StoredGraphRevision graph = graphs.AppendMeta(null, new(null, new(1), [root,
                ObjectVersionRecord.CreateBase(2, TypedBaseBodyCodec.Encode(outside, new(new byte[] { 0xFF })))
            ]));
            Assert.Contains("fixed closure", Assert.Throws<InvalidDataException>(() => Read(graphs, graph.Address, bookmark)).Message);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unreachable_dictionary_must_obey_role_comparer(bool bookmark) {
        using DataStore data = DataStore.Create(_path);
        using GraphRecordStore graphs = new(data);
        ObjectVersionRecord root = bookmark ? BookmarkRow(1, BookmarkMap()) : BranchRow(1, BranchMap());
        ObjectVersionRecord bad = bookmark ? BookmarkRow(2, new(DictionaryComparerKind.StringOrdinalIgnoreCase, []))
            : BranchRow(2, new(DictionaryComparerKind.Application, []));
        StoredGraphRevision graph = graphs.AppendMeta(null, new(null, new(1), [root, bad]));
        Assert.Contains(bookmark ? "StringOrdinal" : "ScalarDefault",
            Assert.Throws<InvalidDataException>(() => Read(graphs, graph.Address, bookmark)).Message);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("long")]
    [InlineData("high")]
    [InlineData("low")]
    [InlineData("badPair")]
    public void Text_role_validates_Unicode_and_length_even_in_unreachable_dictionary(string error) {
        string text = error switch { "empty" => "", "long" => new('中', 1025), "high" => "\uD800", "low" => "\uDC00", _ => "\uD800x" };
        using DataStore data = DataStore.Create(_path);
        using GraphRecordStore graphs = new(data);
        StoredGraphRevision d = graphs.AppendMeta(null, new(null, new(1), [
            BranchRow(1, BranchMap()), BranchRow(2, BranchMap((42, 3))), Text(3, text)
        ]));
        Assert.Throws<InvalidDataException>(() => ManagementDirectoryReader.ReadBranches(graphs, d.Address));
        StoredGraphRevision t = graphs.AppendMeta(null, new(null, new(1), [
            BookmarkRow(1, BookmarkMap()), BookmarkRow(2, BookmarkMap((3, 4))), Text(3, text), ByteRow(4, EntryBytes())
        ]));
        Assert.Throws<InvalidDataException>(() => ManagementDirectoryReader.ReadBookmarks(graphs, t.Address));
    }

    [Theory]
    [InlineData("headId")]
    [InlineData("nullLabel")]
    [InlineData("missingLabel")]
    [InlineData("wrongLabelLayout")]
    public void Unreachable_branch_dictionary_rejects_invalid_ID_and_label_reference(string error) {
        using DataStore data = DataStore.Create(_path);
        using GraphRecordStore graphs = new(data);
        uint label = error == "nullLabel" ? 0u : error == "missingLabel" ? 99u : 3u;
        ObjectVersionRecord target = error == "wrongLabelLayout" ? BranchRow(3, BranchMap()) : Text(3, "有效");
        StoredGraphRevision graph = graphs.AppendMeta(null, new(null, new(1), [
            BranchRow(1, BranchMap()), BranchRow(2, BranchMap((error == "headId" ? 0u : 42u, label))), target
        ]));
        Assert.Throws<InvalidDataException>(() => ManagementDirectoryReader.ReadBranches(graphs, graph.Address));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("zeroHead")]
    [InlineData("zeroTicket")]
    [InlineData("nullValue")]
    [InlineData("duplicateNames")]
    public void Unreachable_bookmark_dictionary_rejects_invalid_entries_and_duplicate_lookup_names(string error) {
        byte[] bytes = EntryBytes();
        if (error == "short") { bytes = bytes[..^1]; }
        if (error == "long") { bytes = [.. bytes, 0]; }
        if (error == "zeroHead") { bytes.AsSpan(0, 4).Clear(); }
        if (error == "zeroTicket") { bytes.AsSpan(4).Clear(); }
        using DataStore data = DataStore.Create(_path);
        using GraphRecordStore graphs = new(data);
        var entries = error == "duplicateNames" ? BookmarkMap((3, 4), (5, 4)) : BookmarkMap((3, error == "nullValue" ? 0u : 4u));
        StoredGraphRevision graph = graphs.AppendMeta(null, new(null, new(1), [
            BookmarkRow(1, BookmarkMap()), BookmarkRow(2, entries), Text(3, "same"), ByteRow(4, bytes), Text(5, "same")
        ]));
        Assert.Throws<InvalidDataException>(() => ManagementDirectoryReader.ReadBookmarks(graphs, graph.Address));
    }

    private static DecodedGraphRows Read(GraphRecordStore store, FrameAddress graph, bool bookmark) =>
        bookmark ? ManagementDirectoryReader.ReadBookmarks(store, graph) : ManagementDirectoryReader.ReadBranches(store, graph);
    private static FrozenDictionaryState<uint, ObjectId> BranchMap(params (uint Key, uint Value)[] entries) =>
        new(DictionaryComparerKind.ScalarDefault, entries.Select(pair => new DictionaryEntryState<uint, ObjectId>(pair.Key, new(pair.Value))).ToArray());
    private static FrozenDictionaryState<ObjectId, ObjectId> BookmarkMap(params (uint Key, uint Value)[] entries) =>
        new(DictionaryComparerKind.StringOrdinal, entries.Select(pair => new DictionaryEntryState<ObjectId, ObjectId>(new(pair.Key), new(pair.Value))).ToArray());
    private static ObjectVersionRecord BranchRow(uint id, FrozenDictionaryState<uint, ObjectId> value) => ObjectVersionRecord.CreateBase(id,
        TypedBaseBodyCodec.Encode(ObjectLayout.ForDictionary(Branches), DictionaryStateBody<uint, ObjectId, UInt32StateOps, StringIdStateOps>.PrepareBase(value, Branches)));
    private static ObjectVersionRecord BookmarkRow(uint id, FrozenDictionaryState<ObjectId, ObjectId> value) => ObjectVersionRecord.CreateBase(id,
        TypedBaseBodyCodec.Encode(ObjectLayout.ForDictionary(Bookmarks), DictionaryStateBody<ObjectId, ObjectId, StringIdStateOps, ObjectIdStateOps>.PrepareBase(value, Bookmarks)));
    private static ObjectVersionRecord Text(uint id, string text) => ObjectVersionRecord.CreateBase(id,
        TypedBaseBodyCodec.Encode(ObjectLayout.String, StringBodyPreparer.PrepareBase(text)));
    private static ObjectVersionRecord ByteRow(uint id, byte[] value) => ObjectVersionRecord.CreateBase(id,
        TypedBaseBodyCodec.Encode(ObjectLayout.ForArray(Bytes), ArrayStateBody<byte, ByteStateOps>.PrepareBase(new(new(value.Length), value), Bytes)));
    private static byte[] EntryBytes() {
        // A codec-valid handle deliberately not resolved in any HeadStore. Directory
        // parsing does not establish published membership or exact Entry qualification.
        byte[] bytes = new byte[HeadEntryId.EncodedSize];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 42);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(4), 1234UL << 26 | 20);
        Assert.True(HeadEntryId.TryRead(bytes, out _));
        return bytes;
    }
    private static FrameAddress RawDirectory(DataStore data, FrameAddress? source, FrameAddress batch) =>
        data.Append(GraphFrameCodec.DirectoryTag, GraphFrameCodec.EncodeDirectory(source, ObjectVersionKind.Base, null,
            ObjectHeadDirectoryCodec.PrepareBase(new Dictionary<uint, FrameAddress> { [1] = batch }).Body)).Unwrap();

    public void Dispose() { if (Directory.Exists(_path)) { Directory.Delete(_path, recursive: true); } }
}
