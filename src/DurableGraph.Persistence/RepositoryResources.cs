using System.Runtime.ExceptionServices;

using Atelia.HeadStore;
using DataStore = Atelia.FrameStore.FrameStore;
using PublicationStore = Atelia.HeadStore.HeadStore;

namespace Atelia.DurableGraph.Persistence;

/// <summary>Owns one repository's matching data FrameStore, HeadStore, publication roots and save lifecycle.</summary>
/// <remarks>
/// Operations are serial. Facades and borrowed Data remain valid only while this owner is usable.
/// Normal backing-store recovery runs before DG caches are constructed. Complete frames are never
/// rewritten by DG. Data-only users can explicitly call Data.ConfirmDurable; Repository publication
/// uses HeadStore's data-first barrier. Dispose and reopen after an uncertain owned operation.
/// </remarks>
public sealed partial class RepositoryResources : IDisposable {
    internal const long DefaultRotationThresholdBytes = 64L * 1024 * 1024 * 1024;
    private readonly PublicationStore _heads;
    private bool _faulted;
    private bool _disposed;

    public DataStore Data { get; }
    public bool IsReadOnly { get; }
    public bool IsFaulted {
        get {
            if (_faulted) { return true; }
            try { Data.EnsureUsable(); _heads.EnsureUsable(); return false; }
            catch (InvalidOperationException) { return true; }
        }
    }
    internal PublicationStore Heads => _heads;

    public static RepositoryResources CreateNew(string path, long rotationThresholdBytes = DefaultRotationThresholdBytes,
        long readCacheBudgetBytes = GraphRecordStore.DefaultReadCacheBudgetBytes) =>
        CreateStorageNew(path, readCacheBudgetBytes, rotationThresholdBytes: rotationThresholdBytes);
    public static RepositoryResources OpenExisting(string path, long rotationThresholdBytes = DefaultRotationThresholdBytes,
        long readCacheBudgetBytes = GraphRecordStore.DefaultReadCacheBudgetBytes) =>
        OpenStorageExisting(path, readCacheBudgetBytes, rotationThresholdBytes);
    public static RepositoryResources OpenReadOnlyExisting(string path, long readCacheBudgetBytes = GraphRecordStore.DefaultReadCacheBudgetBytes) =>
        OpenStorageReadOnlyExisting(path, readCacheBudgetBytes);

    internal static void ValidateDisplayLabel(string label) {
        ArgumentNullException.ThrowIfNull(label);
        try { ManagementDirectoryReader.RequireText(label); }
        catch (InvalidDataException error) { throw new ArgumentException("A branch label requires 1..1024 UTF-16 code units with valid Unicode.", nameof(label), error); }
    }

    internal void EnsureBackingUsable() {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_faulted) { throw new InvalidOperationException("Repository resources are faulted; dispose and reopen before further operations."); }
        try {
            Data.EnsureUsable();
            _heads.EnsureUsable();
        } catch {
            MarkFaulted();
            throw;
        }
    }

    internal void RequireAvailable() => EnsureBackingUsable();

    internal void RequireWritable() {
        RequireAvailable();
        if (IsReadOnly) { throw new InvalidOperationException("Repository resources are read-only."); }
    }

    internal void MarkFaulted() {
        _faulted = true;
        // Data can remain healthy after a DG delivery/install failure. A retained owned facade
        // must still reject both warmed reads and new appends until this owner is reopened.
        _graphStore.Dispose();
    }

    public void Dispose() {
        if (_disposed) { return; }
        if (_preparing) { throw new InvalidOperationException("An active metadata graph preparation cannot be disposed recursively."); }
        _disposed = true;
        _pending?.Finish();
        _pending = null;
        CloseResources(null, _graphStore, _heads, Data);
    }

    // Keep failure evidence while attempting every owned resource. Backing owners aggregate their
    // own close failures; retain those aggregates instead of flattening or replacing their errors.
    private static void CloseResources(Exception? primary, IDisposable? states,
        PublicationStore? heads, DataStore? data) {
        Exception? stateError = null;
        Exception? headError = null;
        Exception? dataError = null;
        try { states?.Dispose(); } catch (Exception error) { stateError = error; }
        try { heads?.Dispose(); } catch (Exception error) { headError = error; }
        try { data?.Dispose(); } catch (Exception error) { dataError = error; }

        Exception? first = primary ?? stateError ?? headError ?? dataError;
        if (first is null) { return; }
        int count = (primary is null ? 0 : 1) + (stateError is null ? 0 : 1)
            + (headError is null ? 0 : 1) + (dataError is null ? 0 : 1);
        if (count == 1) { ExceptionDispatchInfo.Capture(first).Throw(); }
        var errors = new List<Exception>(count);
        if (primary is not null) { errors.Add(primary); }
        if (stateError is not null) { errors.Add(stateError); }
        if (headError is not null) { errors.Add(headError); }
        if (dataError is not null) { errors.Add(dataError); }
        throw new AggregateException("Repository failure and resource cleanup errors.", errors);
    }
}
