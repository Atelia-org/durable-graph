using System.Buffers.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.FrameStore;
using Atelia.HeadStore;

namespace Atelia.DurableGraph.Persistence;

internal enum ManagementDirectoryKind { Branches, Bookmarks }

// D/T use ordinary builtin container bodies, but accept a smaller closed graph than
// a business reader. This entrance neither opens business Heads nor registers Schema.
internal static class ManagementDirectoryReader {
    internal static readonly DictionaryLayout BranchesLayout = new(new(1, TypeTag.UInt32), new(2, TypeTag.String));
    internal static readonly ArrayLayout EntryBytesLayout = new(TypeExprKind.VectorArray, new(1, TypeTag.Byte));
    internal static readonly DictionaryLayout BookmarksLayout = new(new(1, TypeTag.String),
        DurableFieldInfo.Reference(2, EntryBytesLayout.Type));
    private static readonly ObjectLayout BranchesRootLayout = ObjectLayout.ForDictionary(BranchesLayout);
    private static readonly ObjectLayout BookmarksRootLayout = ObjectLayout.ForDictionary(BookmarksLayout);
    private static readonly ObjectLayout EntryBytesObjectLayout = ObjectLayout.ForArray(EntryBytesLayout);

    internal static DecodedGraphRows ReadBranches(GraphRecordStore store, FrameAddress graph) =>
        StoredGraphDecoder.ReadManagementDirectory(store, graph, ManagementDirectoryKind.Branches);

    internal static DecodedGraphRows ReadBookmarks(GraphRecordStore store, FrameAddress graph) =>
        StoredGraphDecoder.ReadManagementDirectory(store, graph, ManagementDirectoryKind.Bookmarks);

    internal static void RequireLayout(ObjectLayout layout, ManagementDirectoryKind kind) {
        if (layout.Equals(ObjectLayout.String) || layout.Equals(RootLayout(kind)) ||
            (kind == ManagementDirectoryKind.Bookmarks && layout.Equals(EntryBytesObjectLayout))) { return; }
        throw new InvalidDataException("An object layout is outside the management directory's fixed closure.");
    }

    internal static void ValidateRows(DecodedGraphRows decoded, ManagementDirectoryKind kind) {
        if (!decoded.Rows.TryGetValue(decoded.Snapshot.RootId, out ObjectStateRecord? root) ||
            !root.Layout.Equals(RootLayout(kind))) {
            throw new InvalidDataException("A management directory requires its exact Dictionary root.");
        }
        Span<byte> headBytes = stackalloc byte[HeadId.EncodedSize];
        // Every dictionary has the role, even if unreachable. An unreferenced leaf
        // has only its builtin layout contract until a directory entry assigns its role.
        foreach (ObjectStateRecord row in decoded.Rows.Values) {
            if (row.Kind != ObjectStateKind.Dictionary) { continue; }
            if (kind == ManagementDirectoryKind.Branches) {
                FrozenDictionaryState<uint, ObjectId> entries = row.GetDictionaryState<uint, ObjectId>();
                if (entries.ComparerKind != DictionaryComparerKind.ScalarDefault) {
                    throw new InvalidDataException("The branch directory requires ScalarDefault comparison.");
                }
                foreach (var entry in entries.Entries) {
                    BinaryPrimitives.WriteUInt32LittleEndian(headBytes, entry.Key);
                    if (!HeadId.TryRead(headBytes, out _)) { throw new InvalidDataException("Invalid branch HeadId encoding."); }
                    RequireText(RequireTarget(decoded, entry.Value, ObjectLayout.String).StringContent);
                }
            } else {
                FrozenDictionaryState<ObjectId, ObjectId> entries = row.GetDictionaryState<ObjectId, ObjectId>();
                if (entries.ComparerKind != DictionaryComparerKind.StringOrdinal) {
                    throw new InvalidDataException("The bookmark directory requires StringOrdinal comparison.");
                }
                foreach (var entry in entries.Entries) {
                    RequireText(RequireTarget(decoded, entry.Key, ObjectLayout.String).StringContent);
                    FrozenArrayState<byte> bytes = RequireTarget(decoded, entry.Value,
                        EntryBytesObjectLayout).GetArrayState<byte>();
                    if (bytes.Shape.Rank != 1 || !HeadEntryId.TryRead(bytes.Elements, out _)) {
                        throw new InvalidDataException("A bookmark value requires exactly one encoded HeadEntryId.");
                    }
                }
            }
        }
    }

    private static ObjectLayout RootLayout(ManagementDirectoryKind kind) => kind switch {
        ManagementDirectoryKind.Branches => BranchesRootLayout,
        ManagementDirectoryKind.Bookmarks => BookmarksRootLayout,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static ObjectStateRecord RequireTarget(DecodedGraphRows decoded, ObjectId id, ObjectLayout layout) {
        if (!decoded.Rows.TryGetValue(id, out ObjectStateRecord? target) || !target.Layout.Equals(layout)) {
            throw new InvalidDataException("A management directory entry requires a non-null target of its exact layout.");
        }
        return target;
    }

    internal static void RequireText(string text) {
        if (text.Length is < 1 or > 1024) { throw new InvalidDataException("Directory text requires 1..1024 UTF-16 code units."); }
        for (int i = 0; i < text.Length; i++) {
            if (char.IsHighSurrogate(text[i])) {
                if (++i == text.Length || !char.IsLowSurrogate(text[i])) { throw new InvalidDataException("Directory text requires valid Unicode."); }
            } else if (char.IsLowSurrogate(text[i])) { throw new InvalidDataException("Directory text requires valid Unicode."); }
        }
    }
}
