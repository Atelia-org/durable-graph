using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using Atelia.Data;
using Atelia.EventJournal;
using Atelia.Rbf;
using Atelia.Rbf.Internal;
using Atelia.RbfSegmentStore;
using Journal = Atelia.EventJournal.EventJournal;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;

namespace Atelia.UnifiedRootProbe;

internal sealed record MetadataBaselineWriteResult(
    string Type, bool Acknowledged, object Result, ProbeMeasurement Measurement,
    object View, ProbeMeasurement ViewReadback, string WriterAdmission,
    string MeasuredWrites, string UnmeasuredMetadataIo, string RecoveryCost);

internal sealed record MetadataBaselineInspectResult(
    string Type, object View, ProbeMeasurement Open, ProbeMeasurement FirstRead,
    string MeasurementScope, string RecoveryCost);

// The old-protocol side of the metadata comparison. This is deliberately a real
// EventJournal client: catalog, per-ref stores and active.segment remain its authority.
internal static class MetadataBaseline {
    private const int PayloadLength = 32;
    private static readonly FieldInfo SegmentsField = typeof(Journal).GetField("_segments", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(nameof(Journal), "_segments");

    internal static object Run(string mode, string path, string[] args, Action<string> checkpoint) {
        ArgumentNullException.ThrowIfNull(checkpoint);
        return mode switch {
            "baseline-seed" => Seed(path, Count(args, 2), Count(args, 3), Count(args, 4), Point(args, 5), checkpoint),
            "baseline-create-b" => CreateB(path, Point(args, 2), checkpoint),
            "baseline-advance" or "baseline-advance-b" => AdvanceB(path, Point(args, 2), checkpoint),
            "baseline-inspect" => Inspect(path),
            "baseline-inventory" => Inventory(path),
            _ => throw new ArgumentException("Unknown metadata baseline mode: " + mode)
        };
    }

    internal static MetadataBaselineWriteResult Seed(string root, int unrelated, int history, int residues,
        string? point = null, Action<string>? checkpoint = null) {
        ValidateCount(unrelated); ValidateCount(history); ValidateCount(residues);
        if (Path.Exists(root)) { throw new IOException("Baseline seed requires a new fixture path: " + root); }
        checkpoint ??= static _ => { };
        var result = MeasureWrite(() => {
            using Journal journal = Journal.CreateNew(root, Options());
            AttachProbes(journal, point, checkpoint);
            EventAddress head = journal.AppendEventFrame(null, Payload(0x41)).Unwrap();
            RefId a = journal.CreateBranch("a", head).Unwrap();
            for (int i = 0; i < history; i++) {
                var committed = journal.CommitToRef(a, head, Payload(0x48)).Unwrap();
                head = committed.EventAddress;
            }
            for (int i = 0; i < unrelated; i++) {
                journal.CreateBranch("u" + i.ToString("D6", CultureInfo.InvariantCulture), head).Unwrap();
            }
            for (int i = 0; i < residues; i++) {
                journal.AppendEventFrame(head, Payload(0x52)).Unwrap();
            }
            Maybe(point, "AfterSeed", checkpoint);
            return new { unrelatedBranches = unrelated, history, residues };
        }, "baseline-seed", "CreateNew, one A event and branch, then optional real ref history, unrelated refs and complete orphan events");
        // Open afresh for the business observation: the operation's measured interval
        // includes all its writes and barriers, but not a post-commit all-branch readback.
        return WithReadback(result, root);
    }

    internal static MetadataBaselineWriteResult CreateB(string root, string? point = null, Action<string>? checkpoint = null) {
        checkpoint ??= static _ => { };
        var result = MeasureWrite(() => {
            string first = RbfSegmentPath.GetSegmentPath(Path.Combine(root, "events"), RbfSegmentStoreLayout.Bucketed, 1);
            long threshold = new FileInfo(first).Length;
            if (threshold <= 4 || (threshold & 3) != 0) { throw new InvalidDataException("First event segment has no complete aligned append frontier."); }
            using Journal journal = Journal.OpenExisting(root, Options(threshold));
            if (journal.ActiveSegmentNumber != 1 || journal.ListBranches().Contains("b", StringComparer.Ordinal)) {
                throw new InvalidOperationException("Create B requires one active event segment and no B branch.");
            }
            AttachProbes(journal, point, checkpoint);
            RefId a = journal.OpenBranch("a").Unwrap();
            EventAddress parent = journal.GetHead(a) ?? throw new InvalidDataException("A has no head.");
            EventAddress next = journal.AppendEventFrame(parent, Payload(0x42)).Unwrap();
            if (next.SegmentNumber != 2) { throw new InvalidDataException("The B event did not trigger the required 1-to-2 rotation."); }
            Maybe(point, "AfterBEventDurable", checkpoint);
            RefId b = journal.CreateBranch("b", next).Unwrap();
            Maybe(point, "AfterBPublication", checkpoint);
            return new { bRefId = b.ToHexString(), firstSegmentThreshold = threshold };
        }, "baseline-create-b", "OpenExisting validates current catalog and active event tail; A ref endpoint is loaded on access, unrelated ref endpoints remain lazy. B event flush; Create allocation, per-ref Init, BindName.");
        return WithReadback(result, root);
    }

    internal static MetadataBaselineWriteResult AdvanceB(string root, string? point = null, Action<string>? checkpoint = null) {
        checkpoint ??= static _ => { };
        var result = MeasureWrite(() => {
            using Journal journal = Journal.OpenExisting(root, Options());
            if (journal.ActiveSegmentNumber != 2) { throw new InvalidOperationException("Advance B requires the already-created second segment."); }
            AttachProbes(journal, point, checkpoint);
            RefId b = journal.OpenBranch("b").Unwrap();
            EventAddress parent = journal.GetHead(b) ?? throw new InvalidDataException("B has no head.");
            EventAddress next = journal.AppendEventFrame(parent, Payload(0x43)).Unwrap();
            if (next.SegmentNumber != 2) { throw new InvalidDataException("B update unexpectedly rotated another segment."); }
            Maybe(point, "AfterAdvanceEventDurable", checkpoint);
            journal.AdvanceRef(b, parent, next).Unwrap();
            Maybe(point, "AfterAdvancePublication", checkpoint);
            return new { bRefId = b.ToHexString() };
        }, "baseline-advance", "OpenExisting validates current catalog and active event tail; B ref endpoint is loaded on access, unrelated ref endpoints remain lazy. B child event flush; ref move flush.");
        return WithReadback(result, root);
    }

    internal static MetadataBaselineInspectResult Inspect(string root) {
        var (journal, open) = ProbeMeasurements.Measure(() => Journal.OpenReadOnlyExisting(root, Options()));
        using (journal) {
            var (view, firstRead) = ProbeMeasurements.Measure(() => View(journal, checkedAncestors: true));
            return new("baseline-inspect", view, open, firstRead,
                "Open validates current catalog and locator; FirstRead resolves all current branch heads and checked-reads A/B ancestry. No historical root view exists.",
                "N/A: this comparison does not implement an old-protocol S1 recovery");
        }
    }

    private static MetadataBaselineWriteResult WithReadback((object Result, ProbeMeasurement Measurement, string Type, string Admission) measured,
        string root) {
        var (view, viewReadback) = ProbeMeasurements.Measure(() => {
            using Journal journal = Journal.OpenReadOnlyExisting(root, Options());
            return View(journal);
        });
        return new(measured.Type, true, measured.Result, measured.Measurement, view, viewReadback,
            measured.Admission,
            "Actual RBF RandomAccess.Write bytes and RBF FlushToDisk waits; requested/returned reads from RbfReadMetrics. Business measurement ends before the separate ViewReadback.",
            "SegmentLocator active.segment and EventJournal catalog.snapshot temp FileStream writes/flushes/moves are uncounted; elapsed time includes them. Directory-entry durability is not implemented.",
            "N/A: no old-protocol S1 recovery in this experiment");
    }

    private static object Inventory(string root) {
        var (files, measurement) = ProbeMeasurements.Measure(() => {
            var result = new List<object>();
            foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)) {
                string relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
                bool rbf = path.EndsWith(".rbf", StringComparison.Ordinal);
                RbfPrefixInspection? prefix = rbf ? RbfPrefixInspector.Inspect(path) : null;
                if (prefix is not null) {
                    ProbeMeasurements.Current?.RecordReads(prefix.ReadRequests, prefix.RequestedBytes, prefix.ReturnedBytes);
                    if (prefix.Status is RbfPrefixStatus.Cancelled or RbfPrefixStatus.BudgetExceeded or RbfPrefixStatus.IoFailure) {
                        throw new IOException("Inventory could not complete for " + relative + ": " + prefix.Detail);
                    }
                }
                byte[] bytes = File.ReadAllBytes(path);
                // File.ReadAllBytes is evidence collection, not a counted storage read-call sample.
                long complete = prefix?.LastCompleteBoundary ?? bytes.LongLength;
                long[] offsets = rbf && complete >= 4 ? FrameOffsets(path, complete) : [];
                result.Add(new {
                    path = relative, length = bytes.LongLength, completeLength = complete,
                    sha256 = Convert.ToHexString(SHA256.HashData(bytes.AsSpan(0, checked((int)complete)))),
                    offsets, status = prefix?.Status.ToString() ?? "Metadata"
                });
            }
            return result;
        });
        return new {
            type = "baseline-inventory", files, measurement,
            measurementScope = "Evidence inventory only. DirectIo counts exact RbfPrefixInspector reads; File.ReadAllBytes hashing and RBF offset enumeration are included in elapsed time but not counted as storage read calls. Exclude this measurement from workload performance comparison."
        };
    }

    private static long[] FrameOffsets(string path, long eof) {
        using IRbfFile file = RbfFile.OpenReadOnlyCandidate(path, eof, RbfCacheMode.Off);
        var offsets = new List<long>();
        var scan = file.ScanForward(showTombstone: true).GetEnumerator();
        while (scan.MoveNext()) { offsets.Add(scan.Current.Ticket.Offset); }
        if (scan.TerminationError is not null) { throw new InvalidDataException("Invalid complete inventory prefix: " + path); }
        return offsets.ToArray();
    }

    private static (object Result, ProbeMeasurement Measurement, string Type, string Admission)
        MeasureWrite<T>(Func<T> work, string type, string admission) {
        long flushStart = 0;
        RbfWriteHooks? previous = RbfWriteInstrumentation.Current;
        RbfWriteInstrumentation.Current = new RbfWriteHooks {
            AfterWrite = write => ProbeMeasurements.Current?.RecordWrite(write.WrittenBytes),
            BeforeFlush = _ => flushStart = Stopwatch.GetTimestamp(),
            AfterFlush = _ => ProbeMeasurements.Current?.RecordFlush(Stopwatch.GetTimestamp() - flushStart)
        };
        try {
            var (result, measurement) = ProbeMeasurements.Measure(work);
            return (result!, measurement, type, admission);
        }
        finally { RbfWriteInstrumentation.Current = previous; }
    }

    private static void AttachProbes(Journal journal, string? point, Action<string> checkpoint) {
        journal.OperationProbe = stage => Maybe(point, "Journal." + stage, checkpoint);
        var segments = (SegmentStore)(SegmentsField.GetValue(journal)
            ?? throw new InvalidOperationException("EventJournal has no segment store."));
        segments.OperationProbe = stage => Maybe(point, "Segment." + stage, checkpoint);
        // The real 4-byte RBF HeaderFence write is the earliest observable point
        // after CreateNew has created segment 2's directory entry and file name.
        RbfWriteHooks? counted = RbfWriteInstrumentation.Current;
        string second = RbfSegmentPath.GetSegmentPath(Path.Combine(journal.JournalPath, "events"), RbfSegmentStoreLayout.Bucketed, 2);
        bool IsSecond(string? candidate) => candidate is not null && string.Equals(Path.GetFullPath(candidate), second, StringComparison.OrdinalIgnoreCase);
        RbfWriteInstrumentation.Current = new RbfWriteHooks {
            BeforeWrite = request => {
                if (request.Offset == 0 && request.RequestedBytes == 4 && IsSecond(request.Path)) {
                    Maybe(point, "BeforeSegment2HeaderWrite", checkpoint);
                    if (point == "PartialSegment2Header0") { return 0; }
                    if (point == "PartialSegment2Header2") { return 2; }
                }
                return request.RequestedBytes;
            },
            AfterWrite = write => {
                counted?.AfterWrite?.Invoke(write);
                if (write.Offset == 0 && IsSecond(write.Path)) {
                    Maybe(point, write.WrittenBytes is 0 or 2 ? "PartialSegment2Header" + write.WrittenBytes : "AfterSegment2HeaderWrite", checkpoint);
                }
            },
            BeforeFlush = request => counted?.BeforeFlush?.Invoke(request),
            AfterFlush = request => counted?.AfterFlush?.Invoke(request)
        };
    }

    private static object View(Journal journal, bool checkedAncestors = false) {
        var branches = new List<object>();
        object? a = null, b = null;
        foreach (string name in journal.ListBranches()) {
            RefId id = journal.OpenBranch(name).Unwrap();
            EventAddress head = journal.GetHead(id) ?? throw new InvalidDataException("Branch has no head: " + name);
            using EventFrame frame = journal.ReadEvent(head).Unwrap();
            if (checkedAncestors && (name == "a" || name == "b")) { journal.ReadAncestorChain(head, checkedRead: true).Unwrap(); }
            object entry = new {
                name, refId = id.ToHexString(), headSegment = head.SegmentNumber,
                headTicket = head.Ticket.Packed, headSequence = frame.Header.SequenceNumber,
                parentSegment = frame.Header.Parent?.SegmentNumber, parentTicket = frame.Header.Parent?.Ticket.Packed,
                payloadHex = Convert.ToHexStringLower(frame.Payload)
            };
            branches.Add(entry);
            if (name == "a") { a = entry; }
            if (name == "b") { b = entry; }
        }
        return new { activeSegment = journal.ActiveSegmentNumber, branchCount = branches.Count, a, b, branches };
    }

    private static EventJournalOptions Options(long? threshold = null) => new() {
        EventSegmentStoreOptions = new() {
            NewStoreLayout = RbfSegmentStoreLayout.Bucketed,
            SegmentSizeThresholdBytes = threshold ?? 64L * 1024 * 1024 * 1024,
            CacheMode = RbfCacheMode.Off
        },
        RefSegmentStoreOptions = new() { NewStoreLayout = RbfSegmentStoreLayout.Flat, CacheMode = RbfCacheMode.Off },
        RefOpLogOptions = new() { CacheMode = RbfCacheMode.Off }
    };

    private static byte[] Payload(byte value) { byte[] payload = new byte[PayloadLength]; Array.Fill(payload, value); return payload; }
    private static int Count(string[] args, int index) {
        if (args.Length <= index) { return 0; }
        int value = int.Parse(args[index], CultureInfo.InvariantCulture);
        ValidateCount(value);
        return value;
    }
    private static void ValidateCount(int value) {
        if (value is < 0 or > 4096) { throw new ArgumentOutOfRangeException(nameof(value), "Axis count must be in [0, 4096]."); }
    }
    private static string? Point(string[] args, int index) => args.Length > index ? args[index] : null;
    private static void Maybe(string? selected, string point, Action<string> checkpoint) {
        if (selected == point) { checkpoint(point); }
    }
}
