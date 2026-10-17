using Atelia.FrameStore;
using Atelia.HeadStore;

namespace Atelia.DurableGraph.Persistence;

public sealed partial class RepositoryResources {
    internal const int MaxBookmarkNameCodeUnits = 1024;
    private static ReadOnlySpan<byte> BookmarkMagic => "P0T1"u8;
    private IReadOnlyDictionary<string, FrameAddress> _bookmarks = null!;
    private HeadEntryId _bookmarkEntry;

    // DG tags select historical graph addresses, not current HeadSetTag members.
    internal void CreateStoredTag(string name, FrameAddress address, out PublicationOutcome outcome) {
        outcome = PublicationOutcome.NotAttempted;
        EnsureEditingAllowed();
        ValidateBookmarkName(name);
        if (_bookmarks.ContainsKey(name)) {
            throw new InvalidOperationException("[DurableGraph.DuplicateTagName] The bookmark name already exists.");
        }
        _ = ReadHistory(address);
        var next = new Dictionary<string, FrameAddress>(_bookmarks, StringComparer.Ordinal) { [name] = address };
        // Allocation and capacity validation happen before publication or fault ownership.
        byte[] payload = EncodeRoot(BookmarkMagic, next);
        bool publicationRejected = false;
        _committing = true;
        try {
            var published = _heads.PublishHead(_bookmarkEntry.HeadId, payload, out outcome);
            publicationRejected = published.IsFailure;
            HeadEntryId entry = published.Unwrap();
            _bookmarks = next;
            _bookmarkEntry = entry;
        } catch {
            if (!publicationRejected || outcome != PublicationOutcome.NotAttempted || !StorageBackingUsable()) { MarkFaulted(); }
            throw;
        } finally { _committing = false; }
    }

    internal FrameAddress ResolveStoredTag(string name) {
        RequireStorageAvailable();
        ValidateBookmarkName(name);
        if (!_bookmarks.TryGetValue(name, out FrameAddress address)) {
            throw new InvalidOperationException("[DurableGraph.TagNotFound] The bookmark name does not exist.");
        }
        _ = ReadHistory(address);
        return address;
    }

    private static IReadOnlyDictionary<string, FrameAddress> DecodeBookmarks(ReadOnlySpan<byte> payload) {
        var bookmarks = DecodeRoot(BookmarkMagic, payload);
        foreach (string name in bookmarks.Keys) {
            try { ValidateBookmarkName(name); }
            catch (InvalidOperationException error) { throw new InvalidDataException("Invalid bookmark directory name.", error); }
        }
        return bookmarks;
    }

    private static void ValidateBookmarkName(string name) {
        if (name is null || name.Length is < 1 or > MaxBookmarkNameCodeUnits) { throw InvalidBookmarkName(); }
        for (int i = 0; i < name.Length; i++) {
            char c = name[i];
            if (char.IsHighSurrogate(c)) {
                if (++i == name.Length || !char.IsLowSurrogate(name[i])) { throw InvalidBookmarkName(); }
            } else if (char.IsLowSurrogate(c)) { throw InvalidBookmarkName(); }
        }
    }

    private static InvalidOperationException InvalidBookmarkName() =>
        new("[DurableGraph.InvalidTagName] A bookmark name requires 1..1024 UTF-16 code units with valid Unicode.");
}
