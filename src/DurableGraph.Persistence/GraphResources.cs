using System.Runtime.ExceptionServices;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using Atelia.VersionStore;
using DataStore = Atelia.FrameStore.FrameStore;
using PublicationStore = Atelia.VersionStore.VersionStore;

namespace Atelia.DurableGraph.Persistence;

/// <summary>Owns a matching data FrameStore, VersionStore and their DG Schema/State facades.</summary>
/// <remarks>
/// Operations are serial. Facades and borrowed Data remain valid only while this owner is usable.
/// Normal backing-store recovery runs before DG caches are constructed. Complete frames are never
/// rewritten by DG. Data-only users can explicitly call Data.ConfirmDurable; Repository publication
/// uses VersionStore's data-first barrier. Dispose and reopen after an uncertain owned operation.
/// </remarks>
public sealed class GraphResources : IDisposable {
    private readonly PublicationStore _versions;
    private Dictionary<string, FrameAddress> _root;
    private RefRevision _rootRevision;
    private bool _faulted;
    private bool _disposed;

    private GraphResources(DataStore data, PublicationStore versions, RefBytesSnapshot root) {
        Data = data;
        _versions = versions;
        _root = RepositoryRootCodec.Decode(root.Payload);
        _rootRevision = root.Revision;
        IsReadOnly = versions.IsReadOnly;
        Schemas = new(this, _root.TryGetValue(RepositoryRootCodec.SchemaKey, out var schema) ? schema : null);
        States = new(data);
    }

    public DataStore Data { get; }
    public SchemaStore Schemas { get; }
    public StateRevisionStore States { get; }
    public bool IsReadOnly { get; }
    public bool IsFaulted {
        get {
            if (_faulted || Schemas.IsFaulted) { return true; }
            try { Data.EnsureUsable(); _versions.EnsureUsable(); return false; }
            catch (InvalidOperationException) { return true; }
        }
    }
    internal PublicationStore Versions => _versions;
    internal RefRevision RootRevision => _rootRevision;

    public static GraphResources CreateNew(string path, long rotationThresholdBytes = 64L * 1024 * 1024 * 1024) =>
        Open(path, rotationThresholdBytes, create: true, readOnly: false);
    public static GraphResources OpenExisting(string path, long rotationThresholdBytes = 64L * 1024 * 1024 * 1024) =>
        Open(path, rotationThresholdBytes, create: false, readOnly: false);
    public static GraphResources OpenReadOnlyExisting(string path) =>
        Open(path, 64L * 1024 * 1024 * 1024, create: false, readOnly: true);

    private static GraphResources Open(string path, long rotationThresholdBytes, bool create, bool readOnly) {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string root = Path.GetFullPath(path);
        if (create) {
            if (Directory.Exists(root) || File.Exists(root)) { throw new IOException("Repository path already exists."); }
            Directory.CreateDirectory(root);
        } else {
            if (!Directory.Exists(root)) { throw new DirectoryNotFoundException(root); }
            // Reject old DG layouts before invoking the writable backing-store factories.
            if (!Directory.Exists(Path.Combine(root, "data")) || !Directory.Exists(Path.Combine(root, "versions"))) {
                throw new InvalidDataException("This directory is not a new-format DurableGraph repository.");
            }
        }
        DataStore? data = null;
        PublicationStore? versions = null;
        try {
            string dataPath = Path.Combine(root, "data");
            data = create ? DataStore.Create(dataPath, rotationThresholdBytes) : readOnly
                ? DataStore.OpenReadOnly(dataPath) : DataStore.Open(dataPath, rotationThresholdBytes);
            string versionsPath = Path.Combine(root, "versions");
            versions = create ? PublicationStore.Create(versionsPath, data, reservedRefIdUpperBound: 1) : readOnly
                ? PublicationStore.OpenReadOnly(versionsPath, data) : PublicationStore.Open(versionsPath, data);
            if (create) {
                versions.CreateRef(1, RepositoryRootCodec.Encode(new Dictionary<string, FrameAddress>(StringComparer.Ordinal)), out _).Unwrap();
            }
            RefBytesSnapshot snapshot = versions.ReadRef(1).Unwrap();
            return new(data, versions, snapshot);
        } catch (Exception primary) {
            CloseResources(primary, null, versions, data);
            throw;
        }
    }

    internal IReadOnlyList<string> ListBranches() {
        RequireAvailable();
        return _root.Keys.Where(static key => key.StartsWith(RepositoryRootCodec.BranchPrefix, StringComparison.Ordinal))
            .Select(static key => key[RepositoryRootCodec.BranchPrefix.Length..]).Order(StringComparer.Ordinal).ToArray();
    }

    internal bool HasBranch(string name) {
        RequireAvailable();
        RepositoryRootCodec.ValidateBranchName(name);
        return _root.ContainsKey(RepositoryRootCodec.BranchPrefix + name);
    }

    internal FrameAddress GetHead(string name) {
        RequireAvailable();
        RepositoryRootCodec.ValidateBranchName(name);
        return _root.TryGetValue(RepositoryRootCodec.BranchPrefix + name, out var head)
            ? head : throw new KeyNotFoundException($"Branch '{name}' was not found.");
    }

    internal sealed record RootUpdate(Dictionary<string, FrameAddress> Roots, byte[] Payload, RefRevision ExpectedRevision);

    internal RootUpdate PrepareBranchRoot(string name, FrameAddress head) {
        RequireWritable();
        RepositoryRootCodec.ValidateBranchName(name);
        return PrepareRoot(new Dictionary<string, FrameAddress>(_root, StringComparer.Ordinal) {
            [RepositoryRootCodec.BranchPrefix + name] = head
        });
    }

    internal RootUpdate PrepareSchemaRoot(FrameAddress address) {
        RequireWritable();
        return PrepareRoot(new Dictionary<string, FrameAddress>(_root, StringComparer.Ordinal) {
            [RepositoryRootCodec.SchemaKey] = address
        });
    }

    private RootUpdate PrepareRoot(Dictionary<string, FrameAddress> candidate) =>
        new(candidate, RepositoryRootCodec.Encode(candidate), _rootRevision);

    internal void PublishBranch(string name, FrameAddress head, out PublicationOutcome outcome) {
        outcome = PublicationOutcome.NotAttempted;
        PublishRoot(PrepareBranchRoot(name, head), out outcome);
    }

    internal void PublishSchema(FrameAddress address) => PublishRoot(PrepareSchemaRoot(address), out _);

    internal void PublishRoot(RootUpdate candidate, out PublicationOutcome outcome) {
        outcome = PublicationOutcome.NotAttempted;
        RequireWritable();
        if (candidate.ExpectedRevision != _rootRevision) {
            throw new InvalidOperationException("Repository publication must use the latest root.");
        }
        AteliaResult<RefRevision> result;
        try { result = _versions.PublishRef(_rootRevision.RefId, candidate.Payload, out outcome); }
        catch { MarkFaulted(); throw; }
        RefRevision published = result.Unwrap();
        _root = candidate.Roots;
        _rootRevision = published;
    }

    internal HistoryGraphRecord ReadHistory(FrameAddress address) {
        RequireAvailable();
        using FrameRead frame = Data.ReadFrame(address).Unwrap();
        if (frame.IsTombstone || frame.Tag != StateRevisionStore.FrameTag) {
            throw new InvalidDataException("A graph checkpoint must select a complete StateRevision frame.");
        }
        var metadata = GraphMetadataCodec.Decode(frame.TailMeta);
        return new(address, metadata.Parent, metadata.Kind, metadata.RootId);
    }

    internal void EnsureBackingUsable() {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_faulted) { throw new InvalidOperationException("Graph resources are faulted; dispose and reopen before further operations."); }
        try {
            Data.EnsureUsable();
            _versions.EnsureUsable();
        } catch {
            MarkFaulted();
            throw;
        }
    }

    internal void RequireAvailable() {
        EnsureBackingUsable();
        if (Schemas.IsFaulted) { throw new InvalidOperationException("Schema resources are faulted; dispose and reopen before further operations."); }
    }
    internal void RequireWritable() {
        RequireAvailable();
        if (IsReadOnly) { throw new InvalidOperationException("Graph resources are read-only."); }
    }
    internal void MarkFaulted() {
        _faulted = true;
        // Data can remain healthy after a DG delivery/install failure. A retained owned facade
        // must still reject both warmed reads and new appends until this owner is reopened.
        States?.Dispose();
    }

    public void Dispose() {
        if (_disposed) { return; }
        _disposed = true;
        CloseResources(null, States, _versions, Data);
    }

    // Keep failure evidence while attempting every owned resource. Backing owners aggregate their
    // own close failures; retain those aggregates instead of flattening or replacing their errors.
    private static void CloseResources(Exception? primary, StateRevisionStore? states,
        PublicationStore? versions, DataStore? data) {
        Exception? stateError = null;
        Exception? versionError = null;
        Exception? dataError = null;
        try { states?.Dispose(); } catch (Exception error) { stateError = error; }
        try { versions?.Dispose(); } catch (Exception error) { versionError = error; }
        try { data?.Dispose(); } catch (Exception error) { dataError = error; }

        Exception? first = primary ?? stateError ?? versionError ?? dataError;
        if (first is null) { return; }
        int count = (primary is null ? 0 : 1) + (stateError is null ? 0 : 1)
            + (versionError is null ? 0 : 1) + (dataError is null ? 0 : 1);
        if (count == 1) { ExceptionDispatchInfo.Capture(first).Throw(); }
        var errors = new List<Exception>(count);
        if (primary is not null) { errors.Add(primary); }
        if (stateError is not null) { errors.Add(stateError); }
        if (versionError is not null) { errors.Add(versionError); }
        if (dataError is not null) { errors.Add(dataError); }
        throw new AggregateException("Repository failure and resource cleanup errors.", errors);
    }
}
