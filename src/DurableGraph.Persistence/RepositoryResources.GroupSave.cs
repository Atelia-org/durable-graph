using Atelia.FrameStore;
using Atelia.HeadStore;

namespace Atelia.DurableGraph.Persistence;

public sealed partial class RepositoryResources {
    private SchemaCatalogWorkspace? _catalog;
    // Internal failure injection after actual confirmation; never substitutes for HeadStore publication.
    internal Func<IReadOnlyDictionary<HeadId, HeadEntryId>, IReadOnlyDictionary<HeadId, HeadEntryId>>? PublicationResultsOverride { get; set; }

    internal SchemaCatalogWorkspace CatalogForPreparation {
        get {
            EnsureSaveUsable();
            if (_catalog is null || _catalog.Accepted?.Snapshot?.Address != _baseline.SharedRoot) {
                _catalog = new(EnsureEditingAllowed, _baseline.SharedRoot is { } root
                    ? SchemaCatalogView.Load(_graphStore, root) : null);
            }
            return _catalog;
        }
    }

    internal void EnsureSaveUsable() {
        RequireStorageAvailable();
        _graphStore.EnsureUsable();
    }

    private FrameAddress? ValidateSaveTarget(HeadId? branch, HeadEntryId? expected, string? rootLabel) {
        if (branch is { } target) {
            if (expected is null || rootLabel is not null || ReadCurrent(target).Id != expected.Value) {
                throw new InvalidOperationException("The branch's accepted Commit has changed.");
            }
            return FindNearestState(expected.Value, true)?.Address;
        }
        if (expected is not null) { throw new ArgumentException("A new root cannot have an accepted Commit."); }
        ValidateDisplayLabel(rootLabel!);
        return null;
    }

    // The producer includes input enumeration. No application callback escapes the owner's preparation guard.
    internal PendingRepositorySave PrepareSaveGroup(Func<PreparedSaveGroup> produce, Action? abort = null) {
        EnsureEditingAllowed();
        ArgumentNullException.ThrowIfNull(produce);
        _preparing = true;
        try {
            PreparedSaveGroup group = produce();
            EnsureSaveUsable();
            if (group.Members.Length == 0) { throw new ArgumentException("A save requires at least one member."); }
            HashSet<HeadId> targets = [];
            foreach (PendingBusinessSave member in group.Members) {
                if (member.Kind is not StoredGraphKind.State and not StoredGraphKind.Event) {
                    throw new ArgumentException("A business graph kind is required.");
                }
                if (member.Branch is { } branch ? !targets.Add(branch) : group.Members.Length != 1) {
                    throw new ArgumentException("Members must target distinct existing branches; a new root is a single save.");
                }
                BusinessEntryPayloadCodec.ValidateSummaryLength(member.Summary.Length);
                FrameAddress? source = ValidateSaveTarget(member.Branch, member.ExpectedEntry, member.RootLabel);
                if (member.StateSource != source || member.Business.SourceGraph != source) {
                    throw new InvalidDataException("Each business candidate must use its exact latest State selected by HeadStore history.");
                }
            }
            if (group.Catalog is not null && group.Catalog.SourceGraph != _baseline.SharedRoot) {
                throw new InvalidDataException("The catalog candidate must use the accepted catalog source.");
            }
            var pending = new PendingRepositorySave(this, _baseline, group.Members, group.Catalog, _baseline.SharedRoot);
            _pending = pending;
            return pending;
        } catch (Exception primary) {
            try { abort?.Invoke(); }
            catch (Exception cleanup) {
                MarkFaulted();
                throw new AggregateException("Save preparation and candidate cleanup failed.", primary, cleanup);
            }
            throw;
        } finally { _preparing = false; }
    }

    // Storage-only callers may omit hooks. Typed producers pair preparation and
    // installation for the same graph: prepare before publication, then install/deliver
    // only after validating the exact published Head set. State hooks never run for Events.
    // Cleanup is attempted at most once here; failures after publication fault the owner
    // without rolling back already published data or completed in-memory installations.
    internal IReadOnlyList<RepositorySaveResult> CommitSaveGroup(PendingRepositorySave pending,
        Action<StoredGraphRevision>? installMeta = null, Action<StoredGraphRevision>? prepareMeta = null,
        Action<int, StoredGraphRevision>? prepareState = null, Action<int, StoredGraphRevision>? installState = null,
        Action<int, StoredGraphRevision>? prepareDelivery = null,
        Action<IReadOnlyList<RepositorySaveResult>>? deliver = null, Action? cleanup = null) {
        EnsureSaveUsable();
        ArgumentNullException.ThrowIfNull(pending);
        if (_heads.IsReadOnly || _committing || !ReferenceEquals(pending.Owner, this) || !ReferenceEquals(_pending, pending) ||
            !pending.IsPrepared || !ReferenceEquals(pending.Baseline, _baseline)) {
            throw new InvalidOperationException("Commit requires the owner's latest unused pending save.");
        }
        foreach (PendingBusinessSave member in pending.Members) {
            if (member.Branch is { } target && ReadCurrent(target).Id != member.ExpectedEntry) {
                throw new InvalidOperationException("A branch's accepted Commit has changed.");
            }
        }
        var graphs = new StoredGraphRevision[pending.Members.Length];
        var results = new RepositorySaveResult[pending.Members.Length];
        var targets = pending.Members.Select(static member => member.Branch).ToArray();
        var commits = new HeadEntryId?[pending.Members.Length];
        pending.BeginCommit();
        _committing = true;
        PublicationOutcome outcome = PublicationOutcome.NotAttempted, physicalOutcome = PublicationOutcome.NotAttempted;
        GraphRecordStore store = GraphStore;
        long framesBefore = store.AppendedFrames;
        bool publishing = false, publicationRejected = false, cleanupFailed = false;
        void Cleanup() {
            Action? action = cleanup;
            cleanup = null;
            try { action?.Invoke(); }
            catch { cleanupFailed = true; throw; }
        }
        try {
            SaveStageObserver?.Invoke(RepositorySaveStage.BeforeAppend);
            EnsureSaveUsable();
            StoredGraphRevision? meta = pending.Catalog is not null ? store.AppendMeta(pending.CatalogSource, pending.Catalog)
                : pending.CatalogSource is { } existingMeta ? store.ReadMetaGraph(existingMeta) : null;
            if (pending.Catalog is not null) { SaveStageObserver?.Invoke(RepositorySaveStage.AfterMetaAppend); }
            EnsureSaveUsable();
            for (int i = 0; i < pending.Members.Length; i++) {
                PendingBusinessSave member = pending.Members[i];
                graphs[i] = store.AppendBusiness(member.Kind, member.StateSource, meta?.Address, member.Business);
                SaveStageObserver?.Invoke(RepositorySaveStage.AfterBusinessAppend);
                EnsureSaveUsable();
            }
            if (meta is not null) { prepareMeta?.Invoke(meta); }
            EnsureSaveUsable();
            for (int i = 0; i < pending.Members.Length; i++) {
                if (pending.Members[i].Kind == StoredGraphKind.State) { prepareState?.Invoke(i, graphs[i]); }
                EnsureSaveUsable();
                prepareDelivery?.Invoke(i, graphs[i]);
                EnsureSaveUsable();
            }
            PreparedDirectoryWrite? registration = null;
            DecodedGraphRows? nextDirectory = _baseline.Branches;
            var changes = new Dictionary<HeadId, ReadOnlyMemory<byte>>(pending.Members.Length + 1);
            for (int i = 0; i < pending.Members.Length; i++) {
                PendingBusinessSave member = pending.Members[i];
                byte[] payload = BusinessEntryPayloadCodec.Encode(graphs[i].Address, member.Summary.Span);
                if (member.Branch is null) {
                    var physical = _heads.CreateHead(payload, out physicalOutcome);
                    publicationRejected = physical.IsFailure;
                    commits[i] = physical.Unwrap();
                    targets[i] = commits[i]!.Value.HeadId;
                    registration = ManagementDirectoryWriter.PrepareBranchAddition(store, _baseline.Branches, targets[i]!.Value, member.RootLabel!);
                    StoredGraphRevision directory = store.AppendMeta(registration.Write.SourceGraph, registration.Write);
                    registration.PrepareInstall(directory);
                    nextDirectory = registration.GetInstallation(_baseline.Branches);
                    changes.Add(_branchEntry.HeadId, ManagementDirectoryPointer.Encode(ManagementDirectoryKind.Branches, directory.Address));
                } else { changes.Add(member.Branch.Value, payload); }
            }
            if (pending.Catalog is not null) { changes.Add(_sharedEntry.HeadId, EncodeShared(meta!.Address)); }
            SaveStageObserver?.Invoke(RepositorySaveStage.BeforePublication);
            EnsureSaveUsable();
            if (!ReferenceEquals(pending.Baseline, _baseline)) { throw new InvalidOperationException("The prepared save baseline changed."); }
            foreach (PendingBusinessSave member in pending.Members) {
                if (member.Branch is { } branch && ReadCurrent(branch).Id != member.ExpectedEntry) {
                    throw new InvalidOperationException("A prepared branch baseline changed.");
                }
            }
            if (registration is not null) { _ = registration.GetInstallation(_baseline.Branches); }
            publishing = true;
            var published = _heads.PublishHeads(changes, out outcome);
            publicationRejected = published.IsFailure;
            IReadOnlyDictionary<HeadId, HeadEntryId> entries = published.Unwrap();
            if (PublicationResultsOverride is { } transform) { entries = transform(entries); }
            EnsureSaveUsable();
            // Preserve every actual returned business identity even if another result is missing.
            for (int i = 0; i < pending.Members.Length; i++) {
                if (pending.Members[i].Branch is { } branch && entries.TryGetValue(branch, out HeadEntryId entry)) {
                    if (entry.HeadId != branch || entry == default) { throw new InvalidDataException("Publication returned a mismatched business identity."); }
                    commits[i] = entry;
                }
            }
            if (entries.Count != changes.Count || changes.Keys.Any(key => !entries.TryGetValue(key, out var id) || id.HeadId != key || id == default)) {
                throw new InvalidDataException("Publication did not return the exact requested Head set.");
            }
            SaveStageObserver?.Invoke(RepositorySaveStage.AfterPublication);
            EnsureSaveUsable();
            SaveStageObserver?.Invoke(RepositorySaveStage.BeforeInstall);
            EnsureSaveUsable();
            _baseline = new(nextDirectory, meta?.Address);
            if (registration is not null) { _branchEntry = entries[_branchEntry.HeadId]; }
            if (pending.Catalog is not null) { _sharedEntry = entries[_sharedEntry.HeadId]; }
            if (meta is not null) { installMeta?.Invoke(meta); }
            EnsureSaveUsable();
            for (int i = 0; i < pending.Members.Length; i++) {
                SaveStageObserver?.Invoke(RepositorySaveStage.BeforeMemberInstall);
                EnsureSaveUsable();
                if (pending.Members[i].Kind == StoredGraphKind.State) { installState?.Invoke(i, graphs[i]); }
                EnsureSaveUsable();
                results[i] = new(graphs[i], meta, meta?.Address, commits[i]!.Value, targets[i]!.Value);
            }
            deliver?.Invoke(results);
            EnsureSaveUsable();
            Cleanup();
            return results;
        } catch (Exception primary) {
            Exception error = primary;
            try { Cleanup(); }
            catch (Exception cleanupError) { error = new AggregateException("Save publication and candidate cleanup failed.", primary, cleanupError); }
            bool fault = cleanupFailed || outcome != PublicationOutcome.NotAttempted || physicalOutcome != PublicationOutcome.NotAttempted || !StorageBackingUsable() ||
                (publishing ? !publicationRejected : store.AppendedFrames != framesBefore);
            if (fault) { MarkFaulted(); }
            var members = new RepositoryPublicationMember[pending.Members.Length];
            for (int i = 0; i < members.Length; i++) {
                members[i] = new(targets[i], pending.Members[i].ExpectedEntry, graphs[i]?.Address, commits[i]);
            }
            throw new RepositoryPublicationException(outcome switch {
                PublicationOutcome.Confirmed => RepositoryPublicationOutcome.Published,
                PublicationOutcome.Unknown => RepositoryPublicationOutcome.Unknown,
                _ => RepositoryPublicationOutcome.NotPublished,
            }, error, members);
        } finally {
            pending.Finish();
            if (ReferenceEquals(_pending, pending)) { _pending = null; }
            _committing = false;
        }
    }
}
