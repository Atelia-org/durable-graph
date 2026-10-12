using System.Buffers.Binary;
using Atelia.Data;
using Atelia.Rbf;
using Atelia.Rbf.Internal;

namespace Atelia.UnifiedRootProbe;

/// <summary>Executable fixture checks. Deliberate byte replacements below are corruption models, not crash tests.</summary>
internal static class ProbeSelfTests {
    private const uint DataTag = 0x5544_4154;
    private const uint RootTag = 0x5552_4F54;
    private static readonly string[] DataFiles = ["a.rbf", "b.rbf", "roots.rbf"];

    internal static object Run(string scratchParent) {
        scratchParent = Path.GetFullPath(scratchParent);
        if (!string.Equals(Path.GetPathRoot(scratchParent), @"C:\", StringComparison.OrdinalIgnoreCase)) {
            throw new ArgumentException("Self-test fixtures must be on C:.", nameof(scratchParent));
        }
        Directory.CreateDirectory(scratchParent);
        string suite = Path.Combine(scratchParent, "unified-root-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(suite);
        var passed = new List<string>();
        void Case(string name, Action test) {
            try { test(); passed.Add(name); }
            catch (Exception ex) { throw new InvalidOperationException($"Self-test {name} failed; fixtures: {suite}", ex); }
        }

        Case("joint-publication-and-old-view", () => {
            string root = New(suite, "joint");
            RootView old = UnifiedRootStore.Seed(root, 11, 12);
            using (var reader = UnifiedRootStore.Open(root)) { Equal(old, reader.Read(), "seed reader"); }
            RootView next = UnifiedRootStore.Commit(root, 21, 22);
            Check(next.Generation == 1 && next.AValue == 21 && next.BValue == 22, "joint values");
            Check(next.AIdentity == old.AIdentity && next.BIdentity == old.BIdentity, "stable file identities");
            Equal(next, UnifiedRootStore.Inspect(root), "latest view");
            Equal(old, UnifiedRootStore.Inspect(root, 0), "old immutable view");
            Check(next.ATicketOffset > old.ATicketOffset && next.BTicketOffset > old.BTicketOffset, "fresh tickets");
        });

        Case("complete-unpublished-orphans-remain-invisible-and-immutable", () => {
            string root = New(suite, "orphans");
            RootView old = UnifiedRootStore.Seed(root, 1, 2);
            ExpectStop(() => UnifiedRootStore.Commit(root, 3, 4, point => {
                if (point == "AfterFlushB") { throw new SimulatedStopException(); }
            }));
            Equal(old, UnifiedRootStore.Inspect(root), "orphan remains invisible");
            InventoryFile[] before = UnifiedRootStore.Inventory(root);
            Check(before.Single(x => x.Path == "a.rbf").Offsets.Length == 3, "A complete orphan retained");
            Check(before.Single(x => x.Path == "b.rbf").Offsets.Length == 3, "B complete orphan retained");
            RootView next = UnifiedRootStore.Commit(root, 5, 6);
            Check(next.Generation == 1 && next.ATicketOffset > old.ATicketOffset + 60 &&
                next.BTicketOffset > old.BTicketOffset + 60, "orphan addresses not reused");
            foreach (InventoryFile file in before) {
                InventoryFile after = UnifiedRootStore.Inventory(root).Single(x => x.Path == file.Path);
                Check(after.Offsets.Take(file.Offsets.Length).SequenceEqual(file.Offsets), "offsets retained: " + file.Path);
                Check(HashPrefix(Path.Combine(root, file.Path), file.CompleteLength) == file.Sha256,
                    "complete prefix unchanged: " + file.Path);
            }
            Equal(old, UnifiedRootStore.Inspect(root, 0), "old generation after orphan continuation");
            // The RBF candidate itself must reject a physical orphan beyond the old view's EOF.
            using var oldCandidate = RbfFile.OpenReadOnlyCandidate(Path.Combine(root, "a.rbf"), old.AVisibleEof);
            Check(!oldCandidate.ReadPooledFrame(SizedPtr.Create(old.AVisibleEof, UnifiedRootStore.DataTicketLength)).IsSuccess,
                "old candidate must not read the next valid frame");
        });

        Case("complete-bad-selected-root-and-dependency-never-fallback", () => {
            foreach (string target in new[] { "roots.rbf", "a.rbf" }) {
                string root = New(suite, "bad-selected-" + target);
                UnifiedRootStore.Seed(root, 1, 2);
                RootView latest = UnifiedRootStore.Commit(root, 3, 4);
                string path = Path.Combine(root, target);
                long offset = target == "roots.rbf" ? 4 + UnifiedRootStore.RootPhysicalLength + 8 : latest.ATicketOffset + 8;
                FlipByte(path, offset); // explicit corruption model, complete frame still occupies its slot
                byte[][] before = Image(root);
                RejectOpen(root);
                Refused(UnifiedRootStore.Recover(root), target + " selected corruption");
                SameImage(root, before);
            }
        });

        Case("crc-valid-unknown-format-mixed-generation-and-out-of-eof", () => {
            foreach (string variant in new[] { "unknown-data", "unknown-root", "mixed", "past-eof" }) {
                string root = New(suite, variant);
                RootView view = UnifiedRootStore.Seed(root, 7, 8);
                if (variant == "unknown-data") {
                    ReplacePayload(suite, root, "a.rbf", view.ATicketOffset, 32, DataTag, p => p[0] ^= 0x20);
                }
                else if (variant == "unknown-root") {
                    ReplacePayload(suite, root, "roots.rbf", 4, 80, RootTag, p => p[0] ^= 0x20);
                }
                else if (variant == "mixed") {
                    ReplacePayload(suite, root, "b.rbf", view.BTicketOffset, 32, DataTag,
                        p => BinaryPrimitives.WriteInt64LittleEndian(p.AsSpan(8), 1));
                }
                else {
                    ReplacePayload(suite, root, "roots.rbf", 4, 80, RootTag,
                        p => BinaryPrimitives.WriteInt64LittleEndian(p.AsSpan(40), view.AVisibleEof + 60));
                }
                byte[][] before = Image(root);
                RejectOpen(root);
                Refused(UnifiedRootStore.Recover(root), variant);
                SameImage(root, before);
            }
        });

        Case("bad-complete-orphan-is-normal-invisible-but-recovery-refuses", () => {
            string root = New(suite, "bad-orphan");
            RootView old = UnifiedRootStore.Seed(root, 1, 2);
            ExpectStop(() => UnifiedRootStore.Commit(root, 3, 4, point => {
                if (point == "AfterFlushB") { throw new SimulatedStopException(); }
            }));
            FlipByte(Path.Combine(root, "b.rbf"), old.BVisibleEof + 8);
            Equal(old, UnifiedRootStore.Inspect(root), "normal read need not scan orphan");
            byte[][] before = Image(root);
            Refused(UnifiedRootStore.Recover(root), "bad complete orphan");
            SameImage(root, before);
        });

        Case("actual-partial-writes-are-rescued-once", () => {
            foreach (string target in new[] { "a.rbf", "roots.rbf" }) {
                string root = New(suite, "partial-" + target);
                RootView old = UnifiedRootStore.Seed(root, 1, 2);
                string path = Path.Combine(root, target);
                RbfWriteHooks? previous = RbfWriteInstrumentation.Current;
                try {
                    RbfWriteInstrumentation.Current = new RbfWriteHooks {
                        BeforeWrite = request => SamePath(request.Path, path) && request.Offset > 4 ? 2 : request.RequestedBytes
                    };
                    ExpectIOException(() => UnifiedRootStore.Commit(root, 3, 4));
                }
                finally { RbfWriteInstrumentation.Current = previous; }
                Equal(old, UnifiedRootStore.Inspect(root), "short-tail normal view");
                RecoveryResult result = UnifiedRootStore.Recover(root);
                Check(result.Status == "Succeeded" && result.TruncatedFile == target && result.ActionAttempted &&
                    result.LengthChanged && result.BarrierCompleted, "single short tail recovery flags: " + result);
                Equal(old, UnifiedRootStore.Inspect(root), "old pair after rescue");
                RootView next = UnifiedRootStore.Commit(root, 5, 6);
                Check(next.Generation == 1 && next.AValue == 5 && next.BValue == 6, "continuation after rescue");
            }
        });

        Case("inconsistent-short-tail-cannot-be-discarded", () => {
            foreach (string variant in new[] { "wrong-head-length", "wrong-partial-magic", "two-short-tails" }) {
                string root = New(suite, variant);
                UnifiedRootStore.Seed(root, 1, 2);
                byte[] head = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(head, variant == "wrong-head-length" ? 60 : 56);
                AppendBytes(Path.Combine(root, "a.rbf"), variant == "wrong-partial-magic" ?
                    [.. head, (byte)'X'] : head);
                if (variant == "two-short-tails") { AppendBytes(Path.Combine(root, "b.rbf"), head); }
                byte[][] before = Image(root);
                Refused(UnifiedRootStore.Recover(root), variant);
                SameImage(root, before);
            }
        });

        Case("real-writer-root-without-fence-is-recoverable", () => {
            string root = New(suite, "root-no-fence");
            RootView old = UnifiedRootStore.Seed(root, 1, 2);
            string rootPath = Path.Combine(root, "roots.rbf");
            RbfWriteHooks? previous = RbfWriteInstrumentation.Current;
            try {
                RbfWriteInstrumentation.Current = new RbfWriteHooks {
                    BeforeWrite = request => SamePath(request.Path, rootPath) && request.Offset ==
                        4 + UnifiedRootStore.RootPhysicalLength ? request.RequestedBytes - 4 : request.RequestedBytes
                };
                ExpectIOException(() => UnifiedRootStore.Commit(root, 3, 4));
            }
            finally { RbfWriteInstrumentation.Current = previous; }
            Equal(old, UnifiedRootStore.Inspect(root), "valid root suffix still selects predecessor");
            RecoveryResult result = UnifiedRootStore.Recover(root);
            Check(result.Status == "Succeeded" && result.TruncatedFile == "roots.rbf" &&
                result.ActionAttempted && result.LengthChanged && result.BarrierCompleted,
                "valid root suffix rescue: " + result);
            Equal(old, result.View!, "old pair after root suffix rescue");
        });

        Case("crc-valid-root-references-or-descriptor-without-fence-refuse", () => {
            foreach (string variant in new[] { "wrong-refs", "wrong-descriptor" }) {
                string root = New(suite, variant);
                RootView old = UnifiedRootStore.Seed(root, 1, 2);
                string path = Path.Combine(root, "roots.rbf");
                RbfWriteHooks? previous = RbfWriteInstrumentation.Current;
                try {
                    RbfWriteInstrumentation.Current = new RbfWriteHooks {
                        BeforeWrite = request => SamePath(request.Path, path) && request.Offset ==
                            4 + UnifiedRootStore.RootPhysicalLength ? 104 : request.RequestedBytes
                    };
                    ExpectIOException(() => UnifiedRootStore.Commit(root, 3, 4));
                }
                finally { RbfWriteInstrumentation.Current = previous; }
                long offset = 4 + UnifiedRootStore.RootPhysicalLength;
                byte[] payload = new byte[80];
                using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                    source.Position = offset + 4;
                    source.ReadExactly(payload);
                }
                int tailMetaLength = 0;
                if (variant == "wrong-refs") {
                    byte[] originalRoot = File.ReadAllBytes(path);
                    originalRoot.AsSpan(4 + 4 + 32, 8).CopyTo(payload.AsSpan(32)); // old A ticket, new A EOF
                }
                else { tailMetaLength = 4; } // valid RBF trailer descriptor, wrong fixed root shape
                byte[] frame = BuildFrame(suite, payload, RootTag, tailMetaLength);
                Check(frame.Length == 108, "root model frame shape");
                WriteBytesAt(path, offset, frame.AsSpan(0, 104)); // explicit corruption model, no Fence
                RejectOpen(root);
                byte[][] before = Image(root);
                Refused(UnifiedRootStore.Recover(root), variant + " before Fence");
                SameImage(root, before);
                _ = old;
            }
        });

        Case("short-root-cannot-discard-fabricated-publication-before-new-dependencies", () => {
            string root = New(suite, "root-before-dependencies");
            UnifiedRootStore.Seed(root, 1, 2);
            string path = Path.Combine(root, "roots.rbf");
            // Model a root HeadLen and full next-generation payload while both A/B remain at
            // generation zero. The old refs are structurally valid but cannot be the new pair.
            byte[] rootZero = File.ReadAllBytes(path);
            byte[] prefix = rootZero.AsSpan(4, 84).ToArray();
            BinaryPrimitives.WriteInt64LittleEndian(prefix.AsSpan(12), 1);
            AppendBytes(path, prefix);
            RejectOpen(root); // Normal also rejects a short root impossible under the writer order.
            byte[][] before = Image(root);
            Refused(UnifiedRootStore.Recover(root), "short root without new-generation dependencies");
            SameImage(root, before);
        });

        Case("trailer-fragments-1-through-11-remain-unresolved", () => {
            for (int size = 1; size <= 11; size++) {
                string root = New(suite, "trailer-" + size);
                UnifiedRootStore.Seed(root, 1, 2);
                string path = Path.Combine(root, "roots.rbf");
                RbfWriteHooks? previous = RbfWriteInstrumentation.Current;
                try {
                    int written = 88 + size; // HeadLen + payload + PayloadCRC + 1..11 Trailer bytes.
                    RbfWriteInstrumentation.Current = new RbfWriteHooks {
                        BeforeWrite = request => SamePath(request.Path, path) && request.Offset ==
                            4 + UnifiedRootStore.RootPhysicalLength ? written : request.RequestedBytes
                    };
                    ExpectIOException(() => UnifiedRootStore.Commit(root, 3, 4));
                }
                finally { RbfWriteInstrumentation.Current = previous; }
                byte[][] before = Image(root);
                RecoveryResult result = UnifiedRootStore.Recover(root);
                Refused(result, "trailer fragment " + size);
                Check(result.Detail.Contains("Unresolved", StringComparison.OrdinalIgnoreCase),
                    "trailer fragment must remain unresolved: " + size + " / " + result.Detail);
                SameImage(root, before);
            }
        });

        Case("lock-io-cancellation-and-budget-before-action-preserve-image", () => {
            string root = New(suite, "preaction");
            UnifiedRootStore.Seed(root, 1, 2);
            byte[][] baseline = Image(root);
            using (var held = new FileStream(Path.Combine(root, "repository.lock"), FileMode.Open,
                FileAccess.ReadWrite, FileShare.None)) {
                ExpectIOException(() => UnifiedRootStore.Inspect(root));
                RecoveryResult locked = UnifiedRootStore.Recover(root);
                Check(locked.Status == "IoOrActionFailure" && !locked.ActionAttempted, "lock refusal");
            }
            SameImage(root, baseline);
            RecoveryResult budget = UnifiedRootStore.Recover(root, readBudget: 0);
            Check(budget.Status == "BudgetExceeded" && !budget.ActionAttempted, "zero budget");
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            RecoveryResult cancelled = UnifiedRootStore.Recover(root, cancellationToken: cts.Token);
            Check(cancelled.Status == "Cancelled" && !cancelled.ActionAttempted, "pre-cancel");
            SameImage(root, baseline);
            string aPath = Path.Combine(root, "a.rbf");
            RbfWriteHooks? previous = RbfWriteInstrumentation.Current;
            try {
                RbfWriteInstrumentation.Current = new RbfWriteHooks {
                    BeforeWrite = request => SamePath(request.Path, aPath)
                        ? throw new IOException("Injected write I/O failure") : request.RequestedBytes
                };
                ExpectIOException(() => UnifiedRootStore.Commit(root, 3, 4));
            }
            finally { RbfWriteInstrumentation.Current = previous; }
            SameImage(root, baseline);
            Equal(UnifiedRootStore.Inspect(root), UnifiedRootStore.Recover(root).View!, "retry after no-write failure");
        });

        Case("post-action-cancel-and-failure-report-progress-and-retry", () => {
            foreach (string mode in new[] { "cancel", "throw", "confirm-flush" }) {
                string root = New(suite, "postaction-" + mode);
                RootView old = UnifiedRootStore.Seed(root, 1, 2);
                AppendBytes(Path.Combine(root, "a.rbf"), [0x38, 0, 0, 0, (byte)'U']);
                using var cts = new CancellationTokenSource();
                RbfWriteHooks? previous = RbfWriteInstrumentation.Current;
                try {
                    if (mode == "confirm-flush") {
                        string aPath = Path.Combine(root, "a.rbf");
                        RbfWriteInstrumentation.Current = new RbfWriteHooks {
                            BeforeFlush = request => {
                                if (SamePath(request.Path, aPath)) { throw new IOException("Injected confirmation I/O failure"); }
                            }
                        };
                    }
                    RecoveryResult result = UnifiedRootStore.Recover(root, checkpoint: point => {
                        if (point == "AfterSetLengthBeforeFlush" && mode == "cancel") { cts.Cancel(); }
                        if (point == "AfterSetLengthBeforeFlush" && mode == "throw") {
                            throw new IOException("Injected after-action I/O failure");
                        }
                    }, cancellationToken: cts.Token);
                    Check(result.Status == (mode == "cancel" ? "Cancelled" : "IoOrActionFailure"),
                        "postaction status: " + result);
                    Check(result.ActionAttempted && result.LengthChanged, "postaction flags: " + result);
                    Check(result.BarrierCompleted == (mode == "confirm-flush"), "barrier flag: " + result);
                }
                finally { RbfWriteInstrumentation.Current = previous; }
                RecoveryResult retry = UnifiedRootStore.Recover(root);
                Check(retry.Status == "Succeeded" && !retry.ActionAttempted, "fresh retry: " + retry);
                Equal(old, retry.View!, "old pair after retry");
                RootView next = UnifiedRootStore.Commit(root, 3, 4);
                Check(next.Generation == 1 && next.AValue == 3 && next.BValue == 4, "postaction continuation");
            }
        });

        Case("f2-missing-name-and-damaged-old-extent-refuse", () => {
            string missing = New(suite, "f2-missing-name");
            UnifiedRootStore.Seed(missing, 1, 2);
            UnifiedRootStore.Commit(missing, 3, 4);
            File.Move(Path.Combine(missing, "b.rbf"), Path.Combine(missing, "b.name-lost"));
            // Explicit directory-entry-loss model, not a Windows power-loss claim.
            RejectOpen(missing);
            Refused(UnifiedRootStore.Recover(missing), "missing selected dependency name");

            string lost = New(suite, "f2-root-present-dependency-lost");
            RootView prior = UnifiedRootStore.Seed(lost, 1, 2);
            UnifiedRootStore.Commit(lost, 3, 4);
            using (var b = new FileStream(Path.Combine(lost, "b.rbf"), FileMode.Open,
                FileAccess.ReadWrite, FileShare.None)) {
                b.SetLength(prior.BVisibleEof); // explicit image model of a failed B durability barrier
                b.Flush(flushToDisk: true);
            }
            byte[][] lostImage = Image(lost);
            RejectOpen(lost);
            Refused(UnifiedRootStore.Recover(lost), "root present but selected dependency lost");
            SameImage(lost, lostImage);

            string damaged = New(suite, "f2-damaged-old-extent");
            RootView first = UnifiedRootStore.Seed(damaged, 1, 2);
            UnifiedRootStore.Commit(damaged, 3, 4);
            FlipByte(Path.Combine(damaged, "a.rbf"), first.ATicketOffset + 8);
            byte[][] before = Image(damaged);
            RejectOpenGeneration(damaged, 0);
            Refused(UnifiedRootStore.Recover(damaged), "complete old extent corruption");
            SameImage(damaged, before);
        });

        Case("f2-dependency-flush-failure-and-readable-unflushed-root", () => {
            string failed = New(suite, "f2-dependency-flush-failure");
            RootView old = UnifiedRootStore.Seed(failed, 1, 2);
            string bPath = Path.Combine(failed, "b.rbf");
            int bFlushes = 0;
            RbfWriteHooks? previous = RbfWriteInstrumentation.Current;
            try {
                RbfWriteInstrumentation.Current = new RbfWriteHooks {
                    BeforeFlush = request => {
                        if (SamePath(request.Path, bPath) && ++bFlushes == 2) {
                            throw new IOException("Injected B dependency barrier failure");
                        }
                    }
                };
                ExpectIOException(() => UnifiedRootStore.Commit(failed, 3, 4));
            }
            finally { RbfWriteInstrumentation.Current = previous; }
            Check(bFlushes == 2, "failure must occur at post-append dependency flush");
            Equal(old, UnifiedRootStore.Inspect(failed), "flush failure did not publish root");
            Check(UnifiedRootStore.Recover(failed).Status == "Succeeded", "complete orphan can be re-confirmed");

            string readable = New(suite, "f2-readable-unflushed-root");
            RootView stable = UnifiedRootStore.Seed(readable, 1, 2);
            string rootPath = Path.Combine(readable, "roots.rbf");
            byte[] stableRootImage = File.ReadAllBytes(rootPath);
            var trace = new List<string>();
            previous = RbfWriteInstrumentation.Current;
            try {
                RbfWriteInstrumentation.Current = new RbfWriteHooks {
                    AfterWrite = observation => {
                        if (observation.Path is string path) { trace.Add("write-" + Path.GetFileName(path)); }
                    },
                    AfterFlush = request => {
                        if (request.Path is string path) { trace.Add("flush-" + Path.GetFileName(path)); }
                    }
                };
                ExpectStop(() => UnifiedRootStore.Commit(readable, 3, 4, point => {
                    if (point == "AfterAppendRoot") { throw new SimulatedStopException(); }
                }));
            }
            finally { RbfWriteInstrumentation.Current = previous; }
            int writeA = trace.LastIndexOf("write-a.rbf"), writeB = trace.LastIndexOf("write-b.rbf");
            int writeRoot = trace.LastIndexOf("write-roots.rbf");
            Check(writeA >= 0 && writeB > writeA && writeRoot > writeB,
                "dependency/root append trace exists");
            Check(trace.FindIndex(writeA + 1, writeRoot - writeA - 1, x => x == "flush-a.rbf") > writeA &&
                trace.FindIndex(writeB + 1, writeRoot - writeB - 1, x => x == "flush-b.rbf") > writeB,
                "each dependency flushed after its write and before root write");
            Check(!trace.Skip(writeRoot + 1).Contains("flush-roots.rbf"),
                "stopped complete root has no post-append root barrier");
            RootView observed = UnifiedRootStore.Inspect(readable);
            Check(observed.Generation == 1 && observed.AValue == 3 && observed.BValue == 4,
                "complete root is readable before its explicit durable flush");
            // Image model: a readable, unflushed root disappears at a later power failure. This
            // does not assert that Windows would actually lose these particular bytes.
            using (var image = new FileStream(rootPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
                image.SetLength(stableRootImage.Length);
                image.Position = 0;
                image.Write(stableRootImage);
                image.Flush(flushToDisk: true);
            }
            Equal(stable, UnifiedRootStore.Inspect(readable), "volatile root lost in image model");
            Check(UnifiedRootStore.Recover(readable).Status == "Succeeded", "orphan data remains after root loss model");

            string sameFile = New(suite, "f2-following-same-file-flush");
            UnifiedRootStore.Seed(sameFile, 1, 2);
            ExpectStop(() => UnifiedRootStore.Commit(sameFile, 3, 4, point => {
                if (point == "AfterAppendRoot") { throw new SimulatedStopException(); }
            }));
            string sameRootPath = Path.Combine(sameFile, "roots.rbf");
            var subsequentTrace = new List<string>();
            previous = RbfWriteInstrumentation.Current;
            try {
                RbfWriteInstrumentation.Current = new RbfWriteHooks {
                    AfterWrite = observation => {
                        if (SamePath(observation.Path, sameRootPath)) { subsequentTrace.Add("write-root2"); }
                    },
                    AfterFlush = request => {
                        if (SamePath(request.Path, sameRootPath)) { subsequentTrace.Add("flush-root"); }
                    }
                };
                Check(UnifiedRootStore.Commit(sameFile, 5, 6).Generation == 2, "same-file continuation");
            }
            finally { RbfWriteInstrumentation.Current = previous; }
            Check(subsequentTrace.Count(x => x == "flush-root") >= 2 &&
                subsequentTrace.IndexOf("flush-root") < subsequentTrace.IndexOf("write-root2"),
                "following commit confirms old root file before appending next root");
        });

        return new { Status = "Passed", Count = passed.Count, Cases = passed, FixtureRoot = suite };
    }

    private static string New(string suite, string name) => Path.Combine(suite, name + "-" + Guid.NewGuid().ToString("N"));
    private static bool SamePath(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static void Check(bool condition, string message) { if (!condition) { throw new Exception(message); } }
    private static void Equal(RootView expected, RootView actual, string context) =>
        Check(expected == actual, context + ": expected " + expected + ", actual " + actual);
    private static void Refused(RecoveryResult result, string context) =>
        Check(result.Status == "Refused" && !result.ActionAttempted && !result.LengthChanged &&
            !result.BarrierCompleted, context + ": " + result);
    private static void ExpectStop(Action action) {
        try { action(); }
        catch (SimulatedStopException) { return; }
        throw new Exception("Expected injected checkpoint stop.");
    }
    private static void ExpectIOException(Action action) {
        try { action(); }
        catch (IOException) { return; }
        throw new Exception("Expected I/O failure.");
    }
    private static void RejectOpen(string root) {
        try { UnifiedRootStore.Inspect(root); }
        catch (InvalidDataException) { return; }
        throw new Exception("Normal read accepted a complete invalid selected record.");
    }
    private static void RejectOpenGeneration(string root, long generation) {
        try { UnifiedRootStore.Inspect(root, generation); }
        catch (InvalidDataException) { return; }
        throw new Exception("Historical read accepted a complete invalid selected record.");
    }
    private static byte[][] Image(string root) => DataFiles.Select(name => File.ReadAllBytes(Path.Combine(root, name))).ToArray();
    private static void SameImage(string root, byte[][] before) {
        byte[][] after = Image(root);
        for (int i = 0; i < DataFiles.Length; i++) {
            Check(before[i].AsSpan().SequenceEqual(after[i]), "unexpected image change: " + DataFiles[i]);
        }
    }
    private static string HashPrefix(string path, long length) {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] bytes = new byte[checked((int)length)];
        stream.ReadExactly(bytes);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
    }
    private static void AppendBytes(string path, byte[] bytes) {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        stream.Position = stream.Length;
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }
    private static void WriteBytesAt(string path, long offset, ReadOnlySpan<byte> bytes) {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        stream.Position = offset;
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }
    private static void FlipByte(string path, long offset) {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        stream.Position = offset;
        int value = stream.ReadByte();
        Check(value >= 0, "corruption offset outside file");
        stream.Position = offset;
        stream.WriteByte((byte)(value ^ 0x80));
        stream.Flush(flushToDisk: true);
    }
    private static void ReplacePayload(string suite, string root, string name, long offset,
        int payloadLength, uint tag, Action<byte[]> mutate) {
        // A fresh, real RBF Append computes both CRCs. Replacing a complete frame is an explicit
        // semantic-corruption model and never an alleged crash or normal Store operation.
        string path = Path.Combine(root, name);
        byte[] payload = new byte[payloadLength];
        using (var original = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) {
            original.Position = offset + 4;
            original.ReadExactly(payload);
        }
        mutate(payload);
        byte[] frame = BuildFrame(suite, payload, tag);
        Check(frame.Length == payloadLength + 28, "replacement frame physical shape");
        WriteBytesAt(path, offset, frame);
    }
    private static byte[] BuildFrame(string suite, byte[] payload, uint tag, int tailMetaLength = 0) {
        string scratch = Path.Combine(suite, "frame-" + Guid.NewGuid().ToString("N") + ".rbf");
        try {
            using (var builder = RbfFile.CreateNew(scratch, RbfCacheMode.Off)) {
                builder.Append(tag, payload.AsSpan(0, payload.Length - tailMetaLength),
                    payload.AsSpan(payload.Length - tailMetaLength)).Unwrap();
                builder.DurableFlush();
            }
            return File.ReadAllBytes(scratch)[4..];
        }
        finally { if (File.Exists(scratch)) { File.Delete(scratch); } }
    }
    private sealed class SimulatedStopException : Exception { }
}
