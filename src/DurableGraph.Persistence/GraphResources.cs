using System.Runtime.ExceptionServices;
using Atelia.DurableGraph.Persistence.MetaBootstrapP0;
using Atelia.VersionStore;
using DataStore = Atelia.FrameStore.FrameStore;
using PublicationStore = Atelia.VersionStore.VersionStore;

namespace Atelia.DurableGraph.Persistence;

/// <summary>Owns a matching data FrameStore, VersionStore and durable graph reads/publication.</summary>
/// <remarks>
/// Operations are serial. Facades and borrowed Data remain valid only while this owner is usable.
/// Normal backing-store recovery runs before DG caches are constructed. Complete frames are never
/// rewritten by DG. Data-only users can explicitly call Data.ConfirmDurable; Repository publication
/// uses VersionStore's data-first barrier. Dispose and reopen after an uncertain owned operation.
/// </remarks>
public sealed partial class GraphResources : IDisposable {
    internal const long DefaultRotationThresholdBytes = 64L * 1024 * 1024 * 1024;
    private readonly PublicationStore _versions;
    private bool _faulted;
    private bool _disposed;

    public DataStore Data { get; }
    public bool IsReadOnly { get; }
    public bool IsFaulted {
        get {
            if (_faulted) { return true; }
            try { Data.EnsureUsable(); _versions.EnsureUsable(); return false; }
            catch (InvalidOperationException) { return true; }
        }
    }
    internal PublicationStore Versions => _versions;

    public static GraphResources CreateNew(string path, long rotationThresholdBytes = DefaultRotationThresholdBytes,
        long readCacheBudgetBytes = P0GraphStore.DefaultReadCacheBudgetBytes) =>
        CreateMetaBootstrapNew(path, readCacheBudgetBytes, rotationThresholdBytes: rotationThresholdBytes);
    public static GraphResources OpenExisting(string path, long rotationThresholdBytes = DefaultRotationThresholdBytes,
        long readCacheBudgetBytes = P0GraphStore.DefaultReadCacheBudgetBytes) =>
        OpenMetaBootstrapExisting(path, readCacheBudgetBytes, rotationThresholdBytes);
    public static GraphResources OpenReadOnlyExisting(string path, long readCacheBudgetBytes = P0GraphStore.DefaultReadCacheBudgetBytes) =>
        OpenMetaBootstrapReadOnlyExisting(path, readCacheBudgetBytes);

    internal static void ValidateBranchName(string name) {
        if (name is null || name.Length is < 1 or > 128 || name.EndsWith(".", StringComparison.Ordinal) || name.EndsWith(".lock", StringComparison.Ordinal)) {
            throw new ArgumentException("Invalid branch name.", nameof(name));
        }
        for (int i = 0; i < name.Length; i++) {
            char c = name[i];
            bool valid = c is >= 'a' and <= 'z' || c is >= '0' and <= '9' || i > 0 && c is '.' or '_' or '-';
            if (!valid) { throw new ArgumentException("Invalid branch name.", nameof(name)); }
        }
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

    internal void RequireAvailable() => EnsureBackingUsable();

    internal void RequireWritable() {
        RequireAvailable();
        if (IsReadOnly) { throw new InvalidOperationException("Graph resources are read-only."); }
    }

    internal void MarkFaulted() {
        _faulted = true;
        // Data can remain healthy after a DG delivery/install failure. A retained owned facade
        // must still reject both warmed reads and new appends until this owner is reopened.
        _metaBootstrapStore.Dispose();
    }

    public void Dispose() {
        if (_disposed) { return; }
        if (_preparing) { throw new InvalidOperationException("An active metadata graph preparation cannot be disposed recursively."); }
        _disposed = true;
        _pending?.Finish();
        _pending = null;
        CloseResources(null, _metaBootstrapStore, _versions, Data);
    }

    // Keep failure evidence while attempting every owned resource. Backing owners aggregate their
    // own close failures; retain those aggregates instead of flattening or replacing their errors.
    private static void CloseResources(Exception? primary, IDisposable? states,
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
