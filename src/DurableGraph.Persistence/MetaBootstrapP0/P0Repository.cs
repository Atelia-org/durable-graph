using System.Runtime.ExceptionServices;
using Atelia.FrameStore;
using Atelia.VersionStore;
using DataStore = Atelia.FrameStore.FrameStore;
using PublicationStore = Atelia.VersionStore.VersionStore;

namespace Atelia.DurableGraph.Persistence.MetaBootstrapP0;

// Internal vertical witness, with a separate format and no production dual-write path.
internal sealed class P0Repository : IDisposable {
    private const uint BranchRef = 1, SharedRef = 2;
    private const string BranchPrefix = "branch/", SchemaKey = "schema";
    private static ReadOnlySpan<byte> BranchMagic => "P0R1"u8;
    private static ReadOnlySpan<byte> SharedMagic => "P0S1"u8;
    private readonly DataStore _data;
    private readonly PublicationStore _versions;
    private PublicationBaseline _baseline;
    private RefRevision _branchRevision, _sharedRevision;
    private P0PendingSave? _pending;
    private bool _preparing, _committing, _faulted, _disposed;

    // This reference is the operation's exact baseline; it is never changed in place.
    internal sealed class PublicationBaseline(Dictionary<string, FrameAddress> branches, FrameAddress? sharedRoot) {
        internal Dictionary<string, FrameAddress> Branches { get; } = branches;
        internal FrameAddress? SharedRoot { get; } = sharedRoot;
    }

    private P0Repository(DataStore data, PublicationStore versions,
        RefBytesSnapshot branches, RefBytesSnapshot shared) {
        _data = data;
        _versions = versions;
        _branchRevision = branches.Revision;
        _sharedRevision = shared.Revision;
        _baseline = new(DecodeBranches(branches.Payload), DecodeShared(shared.Payload));
        Store = new(data, EnsureAvailable);
        if (_baseline.SharedRoot is { } schema && Store.ReadMetadata(schema).Kind != P0GraphKind.Meta) {
            throw new InvalidDataException("The shared root must select a P0 meta graph.");
        }
    }

    internal DataStore Data => _data;
    internal PublicationStore Versions => _versions;
    internal P0GraphStore Store { get; }
    internal Action<P0SaveCheckpoint>? Checkpoint { get; set; }
    internal bool IsFaulted => _faulted || (!_disposed && !BackingUsable());
    internal bool HasPending { get { EnsureAvailable(); return _pending is not null; } }
    internal FrameAddress? SharedSchemaRoot { get { EnsureAvailable(); return _baseline.SharedRoot; } }

    internal static P0Repository CreateNew(string path) => Open(path, create: true, readOnly: false);
    internal static P0Repository OpenExisting(string path) => Open(path, create: false, readOnly: false);
    internal static P0Repository OpenReadOnlyExisting(string path) => Open(path, create: false, readOnly: true);

    private static P0Repository Open(string path, bool create, bool readOnly) {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string root = Path.GetFullPath(path);
        if (create) {
            if (Directory.Exists(root) || File.Exists(root)) { throw new IOException("The P0 repository path already exists."); }
            Directory.CreateDirectory(root);
        } else if (!Directory.Exists(root)) { throw new DirectoryNotFoundException(root); }

        DataStore? data = null;
        PublicationStore? versions = null;
        try {
            string dataPath = Path.Combine(root, "data"), versionsPath = Path.Combine(root, "versions");
            data = create ? DataStore.Create(dataPath) : readOnly ? DataStore.OpenReadOnly(dataPath) : DataStore.Open(dataPath);
            versions = create ? PublicationStore.Create(versionsPath, data, reservedRefIdUpperBound: SharedRef)
                : readOnly ? PublicationStore.OpenReadOnly(versionsPath, data) : PublicationStore.Open(versionsPath, data);
            if (versions.ReservedRefIdUpperBound != SharedRef) { throw new InvalidDataException("Not a P0 publication layout."); }
            if (create) {
                versions.CreateRef(BranchRef, EncodeBranches(new(StringComparer.Ordinal)), out _).Unwrap();
                versions.CreateRef(SharedRef, EncodeShared(null), out _).Unwrap();
            }
            // Missing either ref is an incomplete initialization, never a resumable create.
            RefBytesSnapshot branches = versions.ReadRef(BranchRef).Unwrap();
            RefBytesSnapshot shared = versions.ReadRef(SharedRef).Unwrap();
            return new(data, versions, branches, shared);
        }
        catch (Exception error) {
            CloseResources(error, null, versions, data);
            throw;
        }
    }

    internal FrameAddress? GetHead(string branch) {
        EnsureAvailable();
        RepositoryRootCodec.ValidateBranchName(branch);
        return _baseline.Branches.TryGetValue(branch, out FrameAddress head) ? head : null;
    }

    internal FrameAddress? GetStateHead(string branch) =>
        Store.ResolveContentSource(P0GraphKind.State, GetHead(branch));

    internal void EnsureEditingAllowed() {
        EnsureAvailable();
        if (_versions.IsReadOnly) { throw new InvalidOperationException("The P0 repository is read-only."); }
        if (_pending is not null || _preparing || _committing) { throw new InvalidOperationException("A P0 save is already being prepared or pending."); }
    }

    internal P0PendingSave Prepare(string branch, P0GraphKind kind,
        P0PreparedGraphWrite business, P0PreparedGraphWrite? catalog = null) {
        EnsureEditingAllowed();
        return PrepareCore(branch, kind, business, catalog);
    }

    internal P0PendingSave PrepareFrom(string branch, P0GraphKind kind,
        Func<(P0PreparedGraphWrite Business, P0PreparedGraphWrite? Catalog)> produce) {
        EnsureEditingAllowed();
        ArgumentNullException.ThrowIfNull(produce);
        RepositoryRootCodec.ValidateBranchName(branch);
        if (kind is not P0GraphKind.State and not P0GraphKind.Event) { throw new ArgumentException("Prepare requires a business graph kind.", nameof(kind)); }
        _preparing = true;
        try {
            var prepared = produce();
            return PrepareCore(branch, kind, prepared.Business, prepared.Catalog);
        } finally { _preparing = false; }
    }

    private P0PendingSave PrepareCore(string branch, P0GraphKind kind,
        P0PreparedGraphWrite business, P0PreparedGraphWrite? catalog) {
        EnsureAvailable();
        RepositoryRootCodec.ValidateBranchName(branch);
        ArgumentNullException.ThrowIfNull(business);
        if (kind is not P0GraphKind.State and not P0GraphKind.Event) { throw new ArgumentException("Prepare requires a business graph kind.", nameof(kind)); }
        FrameAddress? parent = GetHead(branch);
        if (business.SourceGraph != Store.ResolveContentSource(kind, parent)) {
            throw new InvalidDataException("The business candidate must use the branch's exact latest State source.");
        }
        if (catalog is not null && catalog.SourceGraph != _baseline.SharedRoot) {
            throw new InvalidDataException("The catalog candidate must use the latest shared meta source.");
        }
        var pending = new P0PendingSave(this, _baseline, branch, kind, business, catalog);
        _pending = pending;
        return pending;
    }

    internal P0SaveResult Commit(P0PendingSave pending,
        Action<P0GraphSnapshot>? installMeta = null, Action<P0GraphSnapshot>? installState = null) {
        EnsureAvailable();
        ArgumentNullException.ThrowIfNull(pending);
        if (_versions.IsReadOnly) { throw new InvalidOperationException("The P0 repository is read-only."); }
        if (_committing || !ReferenceEquals(pending.Owner, this) || !ReferenceEquals(_pending, pending) ||
            !pending.IsPrepared || !ReferenceEquals(pending.Baseline, _baseline)) {
            throw new InvalidOperationException("Commit requires this owner's latest, unused pending save.");
        }
        pending.BeginCommit();
        _committing = true;
        PublicationOutcome outcome = PublicationOutcome.NotAttempted;
        FrameAddress? candidate = null;
        long framesBefore = Store.AppendedFrames;
        bool publishing = false, publicationRejected = false;
        try {
            Checkpoint?.Invoke(P0SaveCheckpoint.BeforeAppend);
            EnsureAvailable();
            P0GraphSnapshot? selectedMeta = pending.Catalog is not null
                ? Store.Append(P0GraphKind.Meta, _baseline.SharedRoot, null, pending.Catalog)
                : _baseline.SharedRoot is { } currentMeta ? Store.ReadGraph(currentMeta) : null;
            if (pending.Catalog is not null) { Checkpoint?.Invoke(P0SaveCheckpoint.AfterMetaAppend); }
            EnsureAvailable();
            FrameAddress? schemaRoot = selectedMeta?.Address;
            FrameAddress? parent = _baseline.Branches.TryGetValue(pending.Branch, out FrameAddress oldHead) ? oldHead : null;
            P0GraphSnapshot business = Store.Append(pending.Kind, parent, schemaRoot, pending.Business);
            candidate = business.Address;
            Checkpoint?.Invoke(P0SaveCheckpoint.AfterBusinessAppend);
            EnsureAvailable();

            // Prepare all DG result/install material before crossing the publication boundary.
            var branches = new Dictionary<string, FrameAddress>(_baseline.Branches, StringComparer.Ordinal) {
                [pending.Branch] = business.Address
            };
            var nextBaseline = new PublicationBaseline(branches, schemaRoot);
            byte[] branchPayload = EncodeBranches(branches);
            byte[]? sharedPayload = pending.Catalog is null ? null : EncodeShared(schemaRoot);
            Dictionary<RefId, ReadOnlyMemory<byte>>? changes = sharedPayload is null ? null : new() {
                [_branchRevision.RefId] = branchPayload,
                [_sharedRevision.RefId] = sharedPayload
            };
            var saved = new P0SaveResult(business, selectedMeta, schemaRoot);
            Checkpoint?.Invoke(P0SaveCheckpoint.BeforePublication);
            EnsureAvailable();

            publishing = true;
            RefRevision nextBranch, nextShared = _sharedRevision;
            if (changes is not null) {
                var published = _versions.PublishRefs(changes, out outcome);
                publicationRejected = published.IsFailure;
                IReadOnlyDictionary<RefId, RefRevision> revisions = published.Unwrap();
                nextBranch = revisions[_branchRevision.RefId];
                nextShared = revisions[_sharedRevision.RefId];
            } else {
                var published = _versions.PublishRef(_branchRevision.RefId, branchPayload, out outcome);
                publicationRejected = published.IsFailure;
                nextBranch = published.Unwrap();
            }
            Checkpoint?.Invoke(P0SaveCheckpoint.AfterPublication);
            EnsureAvailable();
            Checkpoint?.Invoke(P0SaveCheckpoint.BeforeInstall);
            EnsureAvailable();
            _baseline = nextBaseline;
            _branchRevision = nextBranch;
            _sharedRevision = nextShared;
            // Even a reused exact meta graph can consume a producer's same-shape dirty set.
            if (selectedMeta is not null) { installMeta?.Invoke(selectedMeta); }
            EnsureAvailable();
            if (pending.Kind == P0GraphKind.State) { installState?.Invoke(business); }
            EnsureAvailable();
            return saved;
        }
        catch (Exception error) {
            // A normal VS rejection proves no publication and may leave healthy orphan data.
            // Other output exceptions, or interrupted DG installation, invalidate old facades.
            bool fault = outcome != PublicationOutcome.NotAttempted || !BackingUsable() ||
                (publishing ? !publicationRejected : Store.AppendedFrames != framesBefore);
            if (fault) { MarkFaulted(); }
            GraphCommitOutcome mapped = outcome switch {
                PublicationOutcome.Confirmed => GraphCommitOutcome.Published,
                PublicationOutcome.Unknown => GraphCommitOutcome.Unknown,
                _ => GraphCommitOutcome.NotPublished,
            };
            throw new GraphCommitException(mapped, candidate, error);
        }
        finally {
            pending.Finish();
            if (ReferenceEquals(_pending, pending)) { _pending = null; }
            _committing = false;
        }
    }

    internal void Discard(P0PendingSave pending) {
        if (_committing || !ReferenceEquals(pending.Owner, this) || !ReferenceEquals(_pending, pending) || !pending.IsPrepared) {
            throw new InvalidOperationException("Discard requires this owner's inactive, unused pending save.");
        }
        pending.Finish();
        _pending = null;
    }

    private void EnsureAvailable() {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_faulted) { throw new InvalidOperationException("The P0 repository is faulted; dispose and reopen it."); }
        _versions.EnsureUsable();
    }

    private bool BackingUsable() {
        try { _versions.EnsureUsable(); return true; }
        catch (InvalidOperationException) { return false; }
    }

    private void MarkFaulted() {
        _faulted = true;
        Store.Dispose();
    }

    private static byte[] EncodeBranches(Dictionary<string, FrameAddress> branches) => EncodeRoot(BranchMagic,
        branches.ToDictionary(static pair => BranchPrefix + pair.Key, static pair => pair.Value, StringComparer.Ordinal));

    private static byte[] EncodeShared(FrameAddress? schema) => EncodeRoot(SharedMagic,
        schema is { } address ? new Dictionary<string, FrameAddress>(StringComparer.Ordinal) { [SchemaKey] = address } : new(StringComparer.Ordinal));

    private static byte[] EncodeRoot(ReadOnlySpan<byte> magic, IReadOnlyDictionary<string, FrameAddress> roots) {
        byte[] body = RootMapCodec.Encode(roots).Unwrap();
        int length = checked(magic.Length + body.Length);
        if (length > PublicationStore.MaxRootPayloadLength) { throw new InvalidOperationException("P0 publication root exceeds the ref payload capacity."); }
        byte[] payload = new byte[length];
        magic.CopyTo(payload);
        body.CopyTo(payload, magic.Length);
        return payload;
    }

    private static Dictionary<string, FrameAddress> DecodeBranches(ReadOnlySpan<byte> payload) {
        var roots = DecodeRoot(BranchMagic, payload);
        var branches = new Dictionary<string, FrameAddress>(StringComparer.Ordinal);
        foreach ((string key, FrameAddress head) in roots) {
            if (!key.StartsWith(BranchPrefix, StringComparison.Ordinal)) { throw new InvalidDataException("Unknown P0 branch-root key."); }
            string name = key[BranchPrefix.Length..];
            try { RepositoryRootCodec.ValidateBranchName(name); }
            catch (ArgumentException error) { throw new InvalidDataException("Invalid P0 branch name.", error); }
            branches.Add(name, head);
        }
        return branches;
    }

    private static FrameAddress? DecodeShared(ReadOnlySpan<byte> payload) {
        var roots = DecodeRoot(SharedMagic, payload);
        if (roots.Count == 0) { return null; }
        if (roots.Count != 1 || !roots.TryGetValue(SchemaKey, out FrameAddress schema)) {
            throw new InvalidDataException("Unknown P0 shared-root key.");
        }
        return schema;
    }

    private static IReadOnlyDictionary<string, FrameAddress> DecodeRoot(ReadOnlySpan<byte> magic, ReadOnlySpan<byte> payload) {
        if (!payload.StartsWith(magic)) { throw new InvalidDataException("Unknown P0 publication-root format."); }
        return RootMapCodec.Decode(payload[magic.Length..]).Unwrap();
    }

    public void Dispose() {
        if (_disposed) { return; }
        if (_preparing) { throw new InvalidOperationException("An active P0 preparation cannot be disposed recursively."); }
        _disposed = true;
        _pending?.Finish();
        _pending = null;
        CloseResources(null, Store, _versions, _data);
    }

    private static void CloseResources(Exception? primary, P0GraphStore? store, PublicationStore? versions, DataStore? data) {
        var errors = new List<Exception>();
        if (primary is not null) { errors.Add(primary); }
        try { store?.Dispose(); } catch (Exception error) { errors.Add(error); }
        try { versions?.Dispose(); } catch (Exception error) { errors.Add(error); }
        try { data?.Dispose(); } catch (Exception error) { errors.Add(error); }
        if (errors.Count == 1) { ExceptionDispatchInfo.Capture(errors[0]).Throw(); }
        if (errors.Count > 1) { throw new AggregateException("P0 repository failure and resource cleanup errors.", errors); }
    }
}
