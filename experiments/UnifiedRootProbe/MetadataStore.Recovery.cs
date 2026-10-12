using System.Diagnostics;
using System.Security.Cryptography;
using Atelia.Data;
using Atelia.Rbf;
using Atelia.Rbf.Internal;

namespace Atelia.UnifiedRootProbe;

internal static partial class MetadataStore {
    internal static MetadataRecoveryResult Recover(string root, long readBudget = long.MaxValue,
        Action<string>? checkpoint = null, CancellationToken cancellationToken = default,
        MetadataDurabilityMode durability = MetadataDurabilityMode.ProcessCrashOnly) {
        ArgumentOutOfRangeException.ThrowIfNegative(readBudget);
        if (durability == MetadataDurabilityMode.PowerLossRequired) {
            return new("Refused", "NamesBarrierNotProvided: PowerLossRequired is unsupported.",
                null, null, false, false, false);
        }
        RequireProcessCrashOnly(durability);
        root = Path.GetFullPath(root);
        bool attempted = false, lengthChanged = false, barrier = false;
        string? action = null;
        void Point(string point) {
            cancellationToken.ThrowIfCancellationRequested();
            checkpoint?.Invoke(point);
            cancellationToken.ThrowIfCancellationRequested();
        }
        try {
            using var held = Lock(root);
            MetadataQualification q = Qualify(root, readBudget, cancellationToken);
            Point("CandidateResourcesDisposed");
            Point("BeforeAction");
            if (q.ShortFile is { } shortName) {
                // Same exclusive lock, but all candidate readers are disposed. Re-derive
                // physical and semantic eligibility instead of acting from a prior report.
                MetadataQualification again = Qualify(root, long.MaxValue, cancellationToken);
                if (again.ShortFile != shortName || again.HeaderAction != q.HeaderAction ||
                    !q.Lengths.OrderBy(p => p.Key).SequenceEqual(again.Lengths.OrderBy(p => p.Key)) ||
                    !q.Boundaries.OrderBy(p => p.Key).SequenceEqual(again.Boundaries.OrderBy(p => p.Key))) {
                    throw new IOException("Candidate changed before action.");
                }
                string path = PathFor(root, shortName);
                if (q.HeaderAction) {
                    action = "CompleteSegment2Header";
                    Point("BeforeHeaderWrite");
                    using var target = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1);
                    if (target.Length != q.Lengths[shortName] || target.Length is < 0 or > 3) {
                        throw new IOException("Segment header changed.");
                    }
                    int from = checked((int)target.Length);
                    byte[] existing = new byte[from];
                    target.Position = 0;
                    ReadExactMeasured(target, existing);
                    if (!"RBF1"u8[..from].SequenceEqual(existing)) {
                        throw new InvalidDataException("Segment header changed before action.");
                    }
                    target.Position = from;
                    attempted = true;
                    target.Write("RBF1"u8[from..]);
                    lengthChanged = target.Length != from;
                    ProbeMeasurements.Current?.RecordWrite(4 - from);
                    Point("AfterHeaderWriteBeforeFlush");
                    long start = Stopwatch.GetTimestamp();
                    target.Flush(flushToDisk: true);
                    ProbeMeasurements.Current?.RecordFlush(Stopwatch.GetTimestamp() - start);
                    barrier = true;
                }
                else {
                    action = "TruncateTail";
                    Point("BeforeSetLength");
                    using var target = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1);
                    var physical = RbfPrefixInspector.Inspect(target, long.MaxValue, cancellationToken);
                    ProbeMeasurements.Current?.RecordReads(physical.ReadRequests, physical.RequestedBytes, physical.ReturnedBytes);
                    if (physical.Status == RbfPrefixStatus.Cancelled) {
                        throw new OperationCanceledException(cancellationToken);
                    }
                    if (physical.Status != RbfPrefixStatus.IncompleteSuffix ||
                        physical.OriginalLength != q.Lengths[shortName] ||
                        physical.LastCompleteBoundary != q.Boundaries[shortName]) {
                        throw new IOException("Short suffix changed before truncate.");
                    }
                    attempted = true;
                    target.SetLength(physical.LastCompleteBoundary);
                    lengthChanged = true;
                    Point("AfterSetLengthBeforeFlush");
                    long start = Stopwatch.GetTimestamp();
                    target.Flush(flushToDisk: true);
                    ProbeMeasurements.Current?.RecordFlush(Stopwatch.GetTimestamp() - start);
                    barrier = true;
                }
            }
            Point("AfterAction");
            Point("BeforeFreshOpen");
            MetadataQualification fresh = Qualify(root, long.MaxValue, cancellationToken);
            if (fresh.ShortFile is not null || fresh.HeaderAction) {
                throw new InvalidDataException("Fresh qualification still has an incomplete file.");
            }
            foreach (string name in new[] { "00000001.rbf", "00000002.rbf", "allocation.rbf", "catalog.rbf", "roots.rbf" }) {
                if (!fresh.Boundaries.TryGetValue(name, out long eof)) { continue; }
                using var file = RbfFile.OpenExisting(PathFor(root, name), RbfCacheMode.Off);
                if (file.TailOffset != eof) { throw new IOException("Fresh boundary changed: " + name); }
                Flush(file);
                Point(name switch {
                    "00000001.rbf" => "AfterConfirmSegment1",
                    "00000002.rbf" => "AfterConfirmSegment2",
                    "allocation.rbf" => "AfterConfirmAllocation",
                    "catalog.rbf" => "AfterConfirmDirectory",
                    _ => "AfterConfirmRoot"
                });
            }
            Point("BeforeDelivery");
            RequireInventory(root);
            foreach (var (name, eof) in fresh.Boundaries) {
                if (new FileInfo(PathFor(root, name)).Length != eof) {
                    throw new IOException("Boundary changed before delivery: " + name);
                }
            }
            MetadataDirectory selected = fresh.Directories[fresh.LatestRoot.DirectoryTicket];
            MetadataView view = ViewFromQualified(selected, fresh);
            return new("Succeeded", "Fresh full qualification and confirmation completed.",
                action, view, attempted, lengthChanged, barrier);
        }
        catch (OperationCanceledException ex) {
            return new("Cancelled", ex.Message, action, null, attempted, lengthChanged, barrier);
        }
        catch (MetadataBudgetExceededException ex) {
            return new("BudgetExceeded", ex.Message, action, null, attempted, lengthChanged, barrier);
        }
        catch (MetadataUnresolvedException ex) {
            return new("Unresolved", ex.Message, action, null, attempted, lengthChanged, barrier);
        }
        catch (InvalidDataException ex) {
            return new("Refused", ex.Message, action, null, attempted, lengthChanged, barrier);
        }
        catch (IOException ex) {
            return new("IoOrActionFailure", ex.Message, action, null, attempted, lengthChanged, barrier);
        }
        catch (UnauthorizedAccessException ex) {
            return new("IoOrActionFailure", ex.Message, action, null, attempted, lengthChanged, barrier);
        }
    }

    private static MetadataView ViewFromQualified(MetadataDirectory directory, MetadataQualification q) {
        var branches = directory.Branches.Select(entry =>
            BranchView(entry.Key, entry.Value, q.Events[entry.Value.Head])).ToArray();
        return new(directory.Generation, directory.ActiveSegment, directory.Segment1Eof,
            directory.Segment2Eof, q.LatestRoot.AllocationEof, branches);
    }

    internal static InventoryFile[] Inventory(string root) {
        root = Path.GetFullPath(root);
        using var held = Lock(root);
        RequireInventory(root);
        var result = new List<InventoryFile>();
        foreach (string name in PresentFiles(root)) {
            string path = PathFor(root, name);
            var physical = RbfPrefixInspector.Inspect(path);
            ProbeMeasurements.Current?.RecordReads(physical.ReadRequests, physical.RequestedBytes, physical.ReturnedBytes);
            if (physical.Status is not (RbfPrefixStatus.Complete or RbfPrefixStatus.IncompleteSuffix or RbfPrefixStatus.IncompleteHeader)) {
                throw new InvalidDataException(name + ": " + physical.Status);
            }
            long boundary = physical.LastCompleteBoundary;
            var offsets = new List<long>();
            if (boundary >= Header) {
                foreach (SizedPtr ticket in FrameTickets(path, boundary, _ => { }, default)) { offsets.Add(ticket.Offset); }
            }
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[64 * 1024];
            long left = boundary;
            while (left > 0) {
                int requested = (int)Math.Min(left, buffer.Length);
                int got = stream.Read(buffer, 0, requested);
                ProbeMeasurements.Current?.RecordRead(requested, got);
                if (got == 0) { throw new IOException("Short inventory hash read."); }
                hash.AppendData(buffer.AsSpan(0, got));
                left -= got;
            }
            string relative = name.EndsWith(".rbf", StringComparison.Ordinal) && name.Length == 12
                ? "events/segments/" + name : name;
            result.Add(new(relative, physical.OriginalLength, boundary,
                Convert.ToHexString(hash.GetHashAndReset()), offsets.ToArray()));
        }
        return result.ToArray();
    }
}
