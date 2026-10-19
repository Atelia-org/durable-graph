using System.Buffers.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.FrameStore;
using Atelia.HeadStore;
using DataStore = Atelia.FrameStore.FrameStore;
using PublicationStore = Atelia.HeadStore.HeadStore;

namespace Atelia.DurableGraph.Persistence.Tests;

// Integration witness for the new internal pieces. This fixture is not a second
// Repository implementation and does not qualify its public operations or failures.
public sealed class ManagementDirectoryPublicationTests : IDisposable {
    private const string Prefix = "dg-directory-publication-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Actual_directory_candidates_publish_with_schema_then_reopen_without_accepting_abandoned_graphs() {
        DurableSchema[] definitions = [new("DirectoryPublication.Seed", 1, new DurableFieldInfo(1, TypeTag.Byte)),
            new("DirectoryPublication.Added", 1, new DurableFieldInfo(1, TypeTag.Int64))];
        List<HeadId> registered = [];
        FrameAddress d = default, s = default, abandonedD = default, abandonedS = default, abandonedT = default;
        HeadEntryId publishedD = default, publishedS = default, publishedT = default, bookmarkTarget = default;
        for (int pass = 0; pass < 2; pass++) {
            using Fixture store = new(_path, create: pass == 0);
            DecodedGraphRows? acceptedD = store.ReadDirectory(ManagementDirectoryKind.Branches);
            DecodedGraphRows? acceptedT = store.ReadDirectory(ManagementDirectoryKind.Bookmarks);
            SchemaCatalogView? acceptedS = store.ReadSchema();
            if (pass == 0) {
                Assert.Null(acceptedD);
                Assert.Null(acceptedT);
                Assert.Null(acceptedS);
                Assert.Equal(0, store.Graphs.AppendedFrames);
                Assert.Equal(5, store.Heads.ReadHead(1).Unwrap().Payload.Length);
                Assert.Equal(5, store.Heads.ReadHead(3).Unwrap().Payload.Length);
            } else {
                Assert.Equal(d, acceptedD!.Snapshot.Address);
                Assert.Equal(s, acceptedS!.Snapshot!.Address);
                Assert.Equal(publishedD, store.Heads.ReadHead(1).Unwrap().Id);
                Assert.Equal(publishedS, store.Heads.ReadHead(2).Unwrap().Id);
                Assert.Equal(publishedT, store.Heads.ReadHead(3).Unwrap().Id);
                Assert.Throws<ArgumentException>(() => ManagementDirectoryWriter.PrepareBookmarkAddition(
                    store.Graphs, acceptedT, "固定存档", bookmarkTarget));
            }

            HeadId branch = store.Heads.CreateHead("business fixture"u8.ToArray(), out _).Unwrap().HeadId;
            registered.Add(branch);
            PreparedDirectoryWrite directory = ManagementDirectoryWriter.PrepareBranchAddition(store.Graphs, acceptedD, branch, "遗迹/捡到小绿瓶");
            SchemaCatalogWorkspace catalog = new(static () => { }, acceptedS);
            catalog.Set(definitions[pass]);
            PreparedSchemaCatalogWrite schema = catalog.Prepare();
            StoredGraphRevision actualD = store.Graphs.AppendMeta(acceptedD?.Snapshot.Address, directory.Write);
            StoredGraphRevision actualS = store.Graphs.AppendMeta(acceptedS?.Snapshot?.Address, schema.Write!);
            directory.PrepareInstall(actualD);
            schema.PrepareInstall(actualS);
            Assert.Same(acceptedS, catalog.Accepted);
            Assert.Equal(acceptedD?.Snapshot.Address, store.ReadDirectoryAddress(ManagementDirectoryKind.Branches));
            Assert.Equal(acceptedS?.Snapshot?.Address, store.ReadSchemaAddress());

            HeadEntry oldD = store.Heads.ReadHead(1).Unwrap(), oldS = store.Heads.ReadHead(2).Unwrap();
            IReadOnlyDictionary<HeadId, HeadEntryId> entries = store.Heads.PublishHeads(new Dictionary<HeadId, ReadOnlyMemory<byte>> {
                [oldD.Id.HeadId] = ManagementDirectoryPointer.Encode(ManagementDirectoryKind.Branches, actualD.Address),
                [oldS.Id.HeadId] = SchemaPointer(actualS.Address)
            }, out PublicationOutcome outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.Equal(2, entries.Count);
            publishedD = entries[oldD.Id.HeadId];
            publishedS = entries[oldS.Id.HeadId];
            acceptedD = directory.GetInstallation(acceptedD);
            schema.Install(actualS);
            Assert.Same(schema.View, catalog.Accepted);
            AssertBranches(acceptedD, registered);
            d = actualD.Address;
            s = actualS.Address;

            if (pass == 0) {
                bookmarkTarget = store.Heads.ReadHead(branch).Unwrap().Id;
                PreparedDirectoryWrite tag = ManagementDirectoryWriter.PrepareBookmarkAddition(store.Graphs, acceptedT, "固定存档", bookmarkTarget);
                StoredGraphRevision actualT = store.Graphs.AppendMeta(null, tag.Write);
                tag.PrepareInstall(actualT);
                HeadEntry oldT = store.Heads.ReadHead(3).Unwrap();
                publishedT = store.Heads.PublishHead(oldT.Id.HeadId,
                    ManagementDirectoryPointer.Encode(ManagementDirectoryKind.Bookmarks, actualT.Address), out _).Unwrap();
                acceptedT = tag.GetInstallation(acceptedT);
                Assert.Single(acceptedT.Rows[acceptedT.Snapshot.RootId].GetDictionaryState<ObjectId, ObjectId>().Entries.ToArray());
            } else {
                // Fully prepare all three graphs, but publish none of their pointers.
                HeadId unregistered = store.Heads.CreateHead("unregistered fixture"u8.ToArray(), out _).Unwrap().HeadId;
                PreparedDirectoryWrite pendingD = ManagementDirectoryWriter.PrepareBranchAddition(store.Graphs, acceptedD, unregistered, "未发布");
                StoredGraphRevision nextD = store.Graphs.AppendMeta(d, pendingD.Write);
                pendingD.PrepareInstall(nextD);
                abandonedD = nextD.Address;
                catalog.Set(new DurableSchema("DirectoryPublication.Abandoned", 1, new DurableFieldInfo(1, TypeTag.Int32)));
                PreparedSchemaCatalogWrite pendingS = catalog.Prepare();
                StoredGraphRevision nextS = store.Graphs.AppendMeta(s, pendingS.Write!);
                pendingS.PrepareInstall(nextS);
                abandonedS = nextS.Address;
                PreparedDirectoryWrite pendingT = ManagementDirectoryWriter.PrepareBookmarkAddition(store.Graphs, acceptedT, "未发布书签", bookmarkTarget);
                StoredGraphRevision nextT = store.Graphs.AppendMeta(acceptedT!.Snapshot.Address, pendingT.Write);
                pendingT.PrepareInstall(nextT);
                abandonedT = nextT.Address;
                store.Data.ConfirmDurable();
                AssertBranches(acceptedD, registered);
                Assert.Same(schema.View, catalog.Accepted);
                Assert.Equal(publishedT, store.Heads.ReadHead(3).Unwrap().Id);
            }
        }

        using (Fixture store = new(_path, create: false)) {
            Assert.Equal(publishedD, store.Heads.ReadHead(1).Unwrap().Id);
            Assert.Equal(publishedS, store.Heads.ReadHead(2).Unwrap().Id);
            Assert.Equal(publishedT, store.Heads.ReadHead(3).Unwrap().Id);
            AssertBranches(store.ReadDirectory(ManagementDirectoryKind.Branches)!, registered);
            Assert.Equal(d, store.ReadDirectoryAddress(ManagementDirectoryKind.Branches));
            Assert.Equal(s, store.ReadSchemaAddress());
            Assert.Equal(2, store.ReadSchema()!.Schemas.Count);
            DecodedGraphRows bookmarks = store.ReadDirectory(ManagementDirectoryKind.Bookmarks)!;
            var bookmark = Assert.Single(bookmarks.Rows[bookmarks.Snapshot.RootId].GetDictionaryState<ObjectId, ObjectId>().Entries.ToArray());
            Assert.Equal("固定存档", bookmarks.Rows[bookmark.Key].StringContent);
            Assert.True(HeadEntryId.TryRead(bookmarks.Rows[bookmark.Value].GetArrayState<byte>().Elements, out HeadEntryId target));
            Assert.Equal(bookmarkTarget, target);
            Assert.Equal("business fixture"u8.ToArray(), store.Heads.ReadEntry(target).Unwrap().Payload.ToArray());
            Assert.Equal(abandonedD, ManagementDirectoryReader.ReadBranches(store.Graphs, abandonedD).Snapshot.Address);
            Assert.Equal(abandonedS, SchemaCatalogView.Load(store.Graphs, abandonedS).Snapshot!.Address);
            Assert.Equal(abandonedT, ManagementDirectoryReader.ReadBookmarks(store.Graphs, abandonedT).Snapshot.Address);
        }
    }

    private static void AssertBranches(DecodedGraphRows directory, List<HeadId> expected) {
        var entries = directory.Rows[directory.Snapshot.RootId].GetDictionaryState<uint, ObjectId>();
        Assert.Equal(expected.Count, entries.Count);
        Span<byte> bytes = stackalloc byte[HeadId.EncodedSize];
        foreach (var entry in entries.Entries) {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, entry.Key);
            Assert.True(HeadId.TryRead(bytes, out HeadId head));
            Assert.Contains(head, expected);
            Assert.Equal("遗迹/捡到小绿瓶", directory.Rows[entry.Value].StringContent);
        }
    }

    // S retains the existing P0S1 NamedFrameMap format; only D/T use the new pointer.
    private static byte[] SchemaPointer(FrameAddress? address) => [.. "P0S1"u8,
        .. NamedFrameMapCodec.Encode(address is { } value ? new Dictionary<string, FrameAddress> { ["schema"] = value } : []).Unwrap()];
    private sealed class Fixture : IDisposable {
        internal DataStore Data { get; }
        internal PublicationStore Heads { get; }
        internal GraphRecordStore Graphs { get; }
        internal Fixture(string path, bool create) {
            Directory.CreateDirectory(path);
            Data = create ? DataStore.Create(Path.Combine(path, "data")) : DataStore.Open(Path.Combine(path, "data"));
            Heads = create ? PublicationStore.Create(Path.Combine(path, "heads"), Data, reservedHeadIdUpperBound: 3)
                : PublicationStore.Open(Path.Combine(path, "heads"), Data);
            if (create) {
                Heads.CreateHead(1, ManagementDirectoryPointer.Encode(ManagementDirectoryKind.Branches, null), out _).Unwrap();
                Heads.CreateHead(2, SchemaPointer(null), out _).Unwrap();
                Heads.CreateHead(3, ManagementDirectoryPointer.Encode(ManagementDirectoryKind.Bookmarks, null), out _).Unwrap();
            }
            _ = ReadDirectoryAddress(ManagementDirectoryKind.Branches);
            _ = ReadSchemaAddress();
            _ = ReadDirectoryAddress(ManagementDirectoryKind.Bookmarks);
            Graphs = new(Data);
        }
        internal FrameAddress? ReadDirectoryAddress(ManagementDirectoryKind kind) => ManagementDirectoryPointer.Decode(kind,
            Heads.ReadHead(kind == ManagementDirectoryKind.Branches ? 1u : 3u).Unwrap().Payload);
        internal DecodedGraphRows? ReadDirectory(ManagementDirectoryKind kind) => ReadDirectoryAddress(kind) is { } graph
            ? kind == ManagementDirectoryKind.Branches ? ManagementDirectoryReader.ReadBranches(Graphs, graph)
                : ManagementDirectoryReader.ReadBookmarks(Graphs, graph) : null;
        internal FrameAddress? ReadSchemaAddress() {
            byte[] payload = Heads.ReadHead(2).Unwrap().Payload.ToArray();
            Assert.True(payload.AsSpan().StartsWith("P0S1"u8));
            return NamedFrameMapCodec.Decode(payload.AsSpan(4)).Unwrap().TryGetValue("schema", out FrameAddress schema) ? schema : null;
        }
        internal SchemaCatalogView? ReadSchema() => ReadSchemaAddress() is { } graph ? SchemaCatalogView.Load(Graphs, graph) : null;
        public void Dispose() { Graphs.Dispose(); Heads.Dispose(); Data.Dispose(); }
    }
    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}
