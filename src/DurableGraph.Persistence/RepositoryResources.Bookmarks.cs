using Atelia.HeadStore;

namespace Atelia.DurableGraph.Persistence;

public sealed partial class RepositoryResources {
    internal const int MaxBookmarkNameCodeUnits = 1024;
    private DecodedGraphRows? _bookmarks;
    private HeadEntryId _bookmarkEntry;

    internal void CreateStoredTag(string name, HeadEntryId target, out PublicationOutcome outcome) {
        outcome = PublicationOutcome.NotAttempted;
        EnsureEditingAllowed();
        ValidateBookmarkName(name);
        if (FindBookmark(name) is not null) { throw new InvalidOperationException("[DurableGraph.DuplicateTagName] The bookmark name already exists."); }
        _ = ReadDescriptor(target);
        PreparedDirectoryWrite candidate = ManagementDirectoryWriter.PrepareBookmarkAddition(GraphStore, _bookmarks, name, target);
        GraphRecordStore store = GraphStore;
        long before = store.AppendedFrames;
        bool publishing = false, rejected = false;
        _committing = true;
        try {
            var graph = store.AppendMeta(candidate.Write.SourceGraph, candidate.Write);
            candidate.PrepareInstall(graph);
            DecodedGraphRows next = candidate.GetInstallation(_bookmarks);
            byte[] payload = ManagementDirectoryPointer.Encode(ManagementDirectoryKind.Bookmarks, graph.Address);
            SaveStageObserver?.Invoke(RepositorySaveStage.BeforePublication);
            RequireStorageAvailable();
            _ = candidate.GetInstallation(_bookmarks);
            publishing = true;
            var published = _heads.PublishHead(_bookmarkEntry.HeadId, payload, out outcome);
            rejected = published.IsFailure;
            HeadEntryId entry = published.Unwrap();
            SaveStageObserver?.Invoke(RepositorySaveStage.AfterPublication);
            RequireStorageAvailable();
            SaveStageObserver?.Invoke(RepositorySaveStage.BeforeInstall);
            RequireStorageAvailable();
            _bookmarks = next;
            _bookmarkEntry = entry;
        } catch (Exception error) {
            if (outcome != PublicationOutcome.NotAttempted || !StorageBackingUsable() ||
                (publishing ? !rejected : store.AppendedFrames != before)) { MarkFaulted(); }
            throw PublicationFailure(outcome, null, error, null, null, null);
        } finally { _committing = false; }
    }

    internal HeadEntryId ResolveStoredTag(string name) {
        RequireStorageAvailable();
        ValidateBookmarkName(name);
        HeadEntryId id = FindBookmark(name) ?? throw new InvalidOperationException("[DurableGraph.TagNotFound] The bookmark name does not exist.");
        _ = ReadDescriptor(id);
        return id;
    }

    internal TagInfo[] ListStoredTags() {
        RequireStorageAvailable();
        if (_bookmarks is not { } directory) { return []; }
        List<TagInfo> result = [];
        foreach (var pair in directory.Rows[directory.Snapshot.RootId].GetDictionaryState<ObjectId, ObjectId>().Entries) {
            HeadEntryId id = ReadBookmarkTarget(directory, pair.Value);
            _ = ReadDescriptor(id);
            result.Add(new(directory.Rows[pair.Key].StringContent, new(id)));
        }
        result.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
        return result.ToArray();
    }

    private HeadEntryId? FindBookmark(string name) {
        if (_bookmarks is not { } directory) { return null; }
        foreach (var pair in directory.Rows[directory.Snapshot.RootId].GetDictionaryState<ObjectId, ObjectId>().Entries) {
            if (!StringComparer.Ordinal.Equals(directory.Rows[pair.Key].StringContent, name)) { continue; }
            return ReadBookmarkTarget(directory, pair.Value);
        }
        return null;
    }
    private static HeadEntryId ReadBookmarkTarget(DecodedGraphRows directory, ObjectId value) {
        if (!HeadEntryId.TryRead(directory.Rows[value].GetArrayState<byte>().Elements, out HeadEntryId id)) {
            throw new InvalidDataException("Invalid bookmark CommitId.");
        }
        return id;
    }
    private static void ValidateBookmarkName(string name) {
        try { ManagementDirectoryReader.RequireText(name ?? throw InvalidBookmarkName()); }
        catch (InvalidDataException) { throw InvalidBookmarkName(); }
    }
    private static InvalidOperationException InvalidBookmarkName() =>
        new("[DurableGraph.InvalidTagName] A bookmark name requires 1..1024 UTF-16 code units with valid Unicode.");
}
