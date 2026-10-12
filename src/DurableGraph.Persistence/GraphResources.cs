using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using Atelia.RbfSegmentStore;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;

namespace Atelia.DurableGraph.Persistence;

/// <summary>Owns one matching Schema/State file set, without selecting or publishing a graph head.</summary>
/// <remarks>
/// Writable opens confirm complete existing data before the publisher can reuse it. Read-only
/// opens validate physical framing without flushing, recovering, truncating or creating files.
/// The outer host owns operation serialization and must mark uncertain State writes as faulted.
/// </remarks>
internal sealed class GraphResources : IDisposable {
    private readonly IRbfFile _schemaFile;
    private readonly SegmentStore _segments;
    private bool _faulted;
    private bool _disposed;

    private GraphResources(IRbfFile schemaFile, SegmentStore segments, SchemaStore schemas, bool readOnly) {
        _schemaFile = schemaFile;
        _segments = segments;
        Schemas = schemas;
        States = new(segments);
        IsReadOnly = readOnly;
    }

    internal SchemaStore Schemas { get; }
    internal StateRevisionStore States { get; }
    internal bool IsReadOnly { get; }
    internal bool IsFaulted => _faulted || Schemas.IsFaulted;

    internal static GraphResources CreateNew(string path, RbfSegmentStoreOptions? options = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string root = Path.GetFullPath(path);
        if (Directory.Exists(root) || File.Exists(root)) { throw new IOException("Repository path already exists."); }
        Directory.CreateDirectory(root);
        return CreateInExistingDirectory(root, options);
    }

    // The publication host acquires its repository lock before opening these resources.
    internal static GraphResources CreateInExistingDirectory(string path, RbfSegmentStoreOptions? options = null) =>
        Open(path, options, create: true, readOnly: false);

    internal static GraphResources OpenExisting(string path, RbfSegmentStoreOptions? options = null) =>
        Open(path, options, create: false, readOnly: false);

    internal static GraphResources OpenReadOnlyExisting(string path, RbfSegmentStoreOptions? options = null) =>
        Open(path, options, create: false, readOnly: true);

    internal static GraphResources OpenReadOnlyCandidate(string path, RbfSegmentStoreOptions? options = null) =>
        Open(path, options, create: false, readOnly: true, readOnlyCandidate: true);

    internal static GraphResources OpenRecoveryExisting(string path, Action<string, Action> confirmFile) =>
        Open(path, null, create: false, readOnly: false, confirmFile: confirmFile);

    private static GraphResources Open(string path, RbfSegmentStoreOptions? options, bool create, bool readOnly, bool readOnlyCandidate = false,
        Action<string, Action>? confirmFile = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string root = Path.GetFullPath(path);
        if (!Directory.Exists(root)) { throw new DirectoryNotFoundException(root); }
        IRbfFile? schemas = null;
        SegmentStore? segments = null;
        try {
            string schemaPath = Path.Combine(root, "schemas.rbf");
            schemas = create ? RbfFile.CreateNew(schemaPath) :
                readOnly ? RbfFile.OpenReadOnlyExisting(schemaPath) : RbfFile.OpenExisting(schemaPath);
            SchemaStore schemaStore = confirmFile is null ? new(schemas, readOnly) :
                new(schemas, readOnly, default, action => confirmFile(schemaPath, action));
            // Also confirm an empty header. Count excludes built-in container registrations.
            if (!readOnly && schemaStore.Count == 0) {
                if (confirmFile is null) { schemas.DurableFlush(); }
                else { confirmFile(schemaPath, () => schemas.DurableFlush()); }
            }
            string statePath = Path.Combine(root, "state");
            RbfSegmentStoreOptions strict = StrictOptions(options);
            if (create) {
                segments = SegmentStore.CreateNew(statePath, strict);
                using RbfSegmentWriterLease writer = segments.OpenActiveWriter();
                writer.File.DurableFlush();
            } else {
                // Check historical as well as active files before the store caches read handles.
                if (!Directory.Exists(statePath)) { throw new DirectoryNotFoundException(statePath); }
                foreach (string filePath in readOnlyCandidate ? Array.Empty<string>() : Directory.EnumerateFiles(statePath, "*.rbf", SearchOption.AllDirectories)) {
                    using IRbfFile file = readOnly ? RbfFile.OpenReadOnlyExisting(filePath) : RbfFile.OpenExisting(filePath);
                    ValidatePhysicalFile(file);
                    if (!readOnly) {
                        if (confirmFile is null) { file.DurableFlush(); }
                        else { confirmFile(filePath, () => file.DurableFlush()); }
                    }
                }
                segments = readOnly ? SegmentStore.OpenReadOnlyExisting(statePath, strict) : SegmentStore.OpenExisting(statePath, strict);
            }
            return new(schemas, segments, schemaStore, readOnly);
        } catch {
            try { segments?.Dispose(); } finally { schemas?.Dispose(); }
            // A failed creation remains for inspection, never automatically deleted.
            throw;
        }
    }

    private static RbfSegmentStoreOptions StrictOptions(RbfSegmentStoreOptions? supplied) {
        supplied ??= new();
        return new() {
            NewStoreLayout = supplied.NewStoreLayout,
            SegmentSizeThresholdBytes = supplied.SegmentSizeThresholdBytes,
            HistoricalReaderPoolCapacity = supplied.HistoricalReaderPoolCapacity,
            CacheMode = supplied.CacheMode,
        };
    }

    private static void ValidatePhysicalFile(IRbfFile file) {
        var frames = file.ScanForward(showTombstone: true).GetEnumerator();
        while (frames.MoveNext()) {
            using RbfPooledFrame frame = file.ReadPooledFrame(frames.Current.Ticket).Unwrap();
            if (frame.IsTombstone) { throw new InvalidDataException("Repository State files cannot contain tombstones."); }
        }
        if (frames.TerminationError is { } error) { throw new InvalidDataException($"Invalid State tail: {error.Message}"); }
    }

    internal void RequireAvailable() {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsFaulted) { throw new InvalidOperationException("Graph resources are faulted; dispose and reopen before further operations."); }
    }

    internal void RequireWritable() {
        RequireAvailable();
        if (IsReadOnly) { throw new InvalidOperationException("Graph resources are read-only."); }
    }

    internal void MarkFaulted() => _faulted = true;

    public void Dispose() {
        if (_disposed) { return; }
        _disposed = true;
        States.Dispose();
        try { _segments.Dispose(); } finally { _schemaFile.Dispose(); }
    }
}
