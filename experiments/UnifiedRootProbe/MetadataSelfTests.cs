using System.Buffers.Binary;
using Atelia.Data;
using Atelia.EventJournal;
using Atelia.Rbf;
using Atelia.Rbf.Internal;

namespace Atelia.UnifiedRootProbe;

/// <summary>
/// DB-085 metadata/rotation counterexamples. Byte edits below are image models, never
/// claimed to be observations of a process kill or of a power failure.
/// </summary>
internal static class MetadataSelfTests {
    internal static object Run(string scratchParent) {
        scratchParent = Path.GetFullPath(scratchParent);
        string allowed = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Temp", "db085-metadata-20260930"));
        if (!scratchParent.Equals(allowed, StringComparison.OrdinalIgnoreCase) &&
            !scratchParent.StartsWith(allowed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) {
            throw new ArgumentException("Metadata self-tests require the DB-085 task temp directory.", nameof(scratchParent));
        }
        Directory.CreateDirectory(scratchParent);
        string suite = Path.Combine(scratchParent, "metadata-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(suite);
        var passed = new List<string>();
        var observations = new List<string>();
        void Case(string name, Action body) {
            try {
                body();
                passed.Add(name);
            }
            catch (Exception error) {
                throw new InvalidOperationException($"Metadata self-test {name} failed; fixture: {suite}", error);
            }
        }

        Case("directory-two-branches-old-root-and-rotation", () => {
            string root = New(suite, "branches");
            MetadataView seed = MetadataStore.Seed(root);
            Check(seed.Generation == 0 && seed.ActiveSegment == 1, "seed root generation/segment");
            MetadataBranchView a0 = Read(root, "a", 0);
            MetadataStore.PopulateOrphans(root, 1);
            long retainedSegment1Eof = new FileInfo(Segment(root, 1)).Length;
            using (var oldSegment = RbfFile.OpenReadOnlyCandidate(Segment(root, 1), seed.Segment1Eof)) {
                SizedPtr orphan = SizedPtr.Create(seed.Segment1Eof,
                    32 + EventFrameHeaderCodec.FixedLength + 24);
                Check(!oldSegment.ReadPooledFrame(orphan).IsSuccess,
                    "old root's segment EOF must hide a complete later EventFrame");
            }
            MetadataView created = MetadataStore.CreateB(root);
            Check(created.Generation == 1 && created.ActiveSegment == 2, "B publication must rotate to the real second segment");
            MetadataBranchView a1 = Read(root, "a");
            MetadataBranchView b1 = Read(root, "b");
            Check(a1 == a0, "A identity, head, payload and address stay fixed while B is created");
            Check(b1.Name == "b" && b1.RefId != a1.RefId && b1.Head.SegmentNumber == 2,
                "B has a distinct real RefId and a segment-2 event head");
            Check(created.Segment2Eof > 4 && created.Segment1Eof == retainedSegment1Eof,
                "second segment contains an actual event; old segment's complete orphan remains at its address");
            ExpectMissingBranch(root, "b", 0);
            Check(Read(root, "a", 0) == a0, "old root freezes A");
            MetadataView advanced = MetadataStore.AdvanceB(root);
            MetadataBranchView b2 = Read(root, "b");
            Check(advanced.Generation == 2 && b2.RefId == b1.RefId && b2.Head != b1.Head &&
                b2.Sequence > b1.Sequence, "B advance preserves identity and moves its head");
            Check(Read(root, "b", 1) == b1 && Read(root, "a") == a0,
                "historical B and unchanged A remain address-stable");
        });

        Case("new-file-and-complete-orphans-retain-addresses", () => {
            foreach (string point in new[] { "AfterEventFlush", "AfterAllocationFlush", "AfterDirectoryFlush" }) {
                string root = New(suite, "orphan-" + point);
                MetadataStore.Seed(root);
                Snapshot before = Snapshot.Take(root);
                ExpectStop(() => MetadataStore.CreateB(root, current => {
                    if (current == point) { throw new SimulatedStopException(); }
                }));
                Check(Read(root, "a").Name == "a", "old A remains selected after " + point);
                ExpectMissingBranch(root, "b");
                Snapshot orphan = Snapshot.Take(root);
                Check(orphan.TotalBytes > before.TotalBytes, "stop must have left physical facts: " + point);
                MetadataRecoveryResult recovered = MetadataStore.Recover(root);
                Check(recovered.Status == "Succeeded", "complete orphan recovery: " + point + " / " + recovered);
                MetadataView next = MetadataStore.CreateB(root);
                Check(next.Generation == 1 && Read(root, "b").Name == "b", "B retry after " + point);
                orphan.AssertPrefixRetained(root);
            }
        });

        Case("full-root-publication-before-ack-selects-B", () => {
            string root = New(suite, "published-no-ack");
            MetadataStore.Seed(root);
            ExpectStop(() => MetadataStore.CreateB(root, point => {
                if (point == "AfterRootFlush") { throw new SimulatedStopException(); }
            }));
            Check(Read(root, "b").Name == "b", "complete root selects B without an acknowledgement");
            MetadataRecoveryResult report = MetadataStore.Recover(root);
            Check(report.Status == "Succeeded", "published root survives recovery");
        });

        Case("qualification-budget-and-cancellation-do-not-mutate", () => {
            string root = New(suite, "preaction");
            MetadataStore.Seed(root);
            Snapshot initial = Snapshot.Take(root);
            MetadataRecoveryResult budget = MetadataStore.Recover(root, readBudget: 0);
            Check(budget.Status == "BudgetExceeded" && !budget.ActionAttempted, "budget exhaustion is not qualification");
            initial.AssertSame(root);
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            MetadataRecoveryResult cancelled = MetadataStore.Recover(root, cancellationToken: cts.Token);
            Check(cancelled.Status == "Cancelled" && !cancelled.ActionAttempted, "cancellation is not qualification");
            initial.AssertSame(root);
            using (var held = new FileStream(Path.Combine(root, "repository.lock"), FileMode.Open,
                FileAccess.ReadWrite, FileShare.None)) {
                MetadataRecoveryResult unavailable = MetadataStore.Recover(root);
                Check(unavailable.Status == "IoOrActionFailure" && !unavailable.ActionAttempted &&
                    !unavailable.LengthChanged && !unavailable.BarrierCompleted,
                    "lock I/O failure cannot grant action: " + unavailable);
            }
            initial.AssertSame(root);
        });

        Case("complete-bad-selected-root-and-event-refuse-without-fallback", () => {
            foreach (string target in new[] { "root", "event" }) {
                string root = New(suite, "bad-selected-" + target);
                MetadataStore.Seed(root);
                MetadataStore.CreateB(root);
                MetadataBranchView b = Read(root, "b");
                string path = target == "root" ? Path.Combine(root, "roots.rbf") : Segment(root, 2);
                long offset = target == "root" ? LastOffset(root, "roots.rbf") : b.Head.Ticket.Offset;
                FlipByte(path, offset + 8); // Explicit complete-frame corruption model.
                Snapshot damaged = Snapshot.Take(root);
                RejectRead(root, "b", "selected " + target);
                Refused(MetadataStore.Recover(root), "selected " + target);
                damaged.AssertSame(root);
            }
        });

        Case("complete-bad-orphan-invisible-to-normal-but-blocks-recovery", () => {
            string root = New(suite, "bad-orphan");
            MetadataView seed = MetadataStore.Seed(root);
            MetadataBranchView a = Read(root, "a");
            MetadataStore.PopulateOrphans(root, 1);
            FlipByte(Segment(root, 1), seed.Segment1Eof + 8);
            Check(Read(root, "a") == a, "normal read does not cross selected EOF");
            Snapshot damaged = Snapshot.Take(root);
            Refused(MetadataStore.Recover(root), "bad complete orphan");
            damaged.AssertSame(root);
        });

        Case("crc-valid-root-directory-alias-and-parent-contradictions-refuse", () => {
            string wrongRoot = New(suite, "wrong-root-eof");
            MetadataStore.Seed(wrongRoot);
            MetadataStore.CreateB(wrongRoot);
            ReplacePayload(suite, wrongRoot, "roots.rbf", LastOffset(wrongRoot, "roots.rbf"),
                0x3152_5255, payload => BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(24),
                    new FileInfo(Path.Combine(wrongRoot, "catalog.rbf")).Length + 1000));
            RefuseAndKeepImage(wrongRoot, "b", "CRC-valid root EOF outside catalog");

            string alias = New(suite, "directory-alias");
            MetadataStore.Seed(alias);
            MetadataStore.CreateB(alias);
            ReplacePayload(suite, alias, "catalog.rbf", LastOffset(alias, "catalog.rbf"),
                0x3144_5255, payload => {
                    int first = DirectoryEntry(payload, "a"), second = DirectoryEntry(payload, "b");
                    payload.AsSpan(first, 8).CopyTo(payload.AsSpan(second, 8));
                });
            RefuseAndKeepImage(alias, "b", "CRC-valid directory RefId alias");

            string parent = New(suite, "event-parent-cycle");
            MetadataStore.Seed(parent);
            MetadataStore.CreateB(parent);
            MetadataBranchView b = Read(parent, "b");
            ReplaceEvent(suite, parent, b.Head, metadata => metadata with { Parent = b.Head });
            RefuseAndKeepImage(parent, "b", "CRC-valid event parent cycle");
        });

        Case("crc-valid-nested-directory-is-not-a-physical-frame-member", () => {
            string root = New(suite, "nested-directory");
            MetadataView selected = MetadataStore.Seed(root, unrelatedBranches: 8);
            string catalog = Path.Combine(root, "catalog.rbf");
            long outerOffset = LastOffset(root, "catalog.rbf");
            byte[] outerPayload = ReadPayload(catalog, outerOffset).Payload;
            int firstEntryEnd = DirectoryEntry(outerPayload, "a") + 24;
            byte[] nestedPayload = outerPayload[..firstEntryEnd];
            BinaryPrimitives.WriteInt32LittleEndian(nestedPayload.AsSpan(20), 1);
            byte[] nested = BuildFrame(suite, nestedPayload, 0x3144_5255);
            const int insertion = 100; // 4B-aligned offset in the large outer directory body.
            Check(insertion + nested.Length <= outerPayload.Length,
                "nested frame fits inside outer directory payload");
            SizedPtr fakeTicket = SizedPtr.Create(outerOffset + 4 + insertion, nested.Length - 4);
            ReplacePayload(suite, root, "catalog.rbf", outerOffset, 0x3144_5255,
                payload => nested.AsSpan().CopyTo(payload.AsSpan(insertion)));
            ReplacePayload(suite, root, "roots.rbf", LastOffset(root, "roots.rbf"), 0x3152_5255,
                payload => {
                    BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(16), fakeTicket.Packed);
                    BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(24),
                        fakeTicket.Offset + fakeTicket.Length + 4);
                });
            Snapshot forged = Snapshot.Take(root);
            // Normal is a bounded read under the controlled-writer source premise. An
            // adversarial image can satisfy its local CRC/EOF checks; this observation
            // is recorded, never treated as a proof of physical membership.
            try {
                Check(Read(root, "a").Name == "a", "synthetic A name if Normal accepts forged source");
                observations.Add("nested-catalog-normal=accepted-under-controlled-source-boundary");
            }
            catch (InvalidDataException) {
                observations.Add("nested-catalog-normal=rejected");
            }
            Refused(MetadataStore.Recover(root), "nested directory is not an RBF prefix member");
            forged.AssertSame(root);
            _ = selected;
        });

        Case("unknown-file-refuses-recovery-without-action", () => {
            string root = New(suite, "unknown-file");
            MetadataStore.Seed(root);
            File.WriteAllBytes(Path.Combine(root, "unrecognized.rbf"), [1, 2, 3]);
            Snapshot before = Snapshot.Take(root);
            Refused(MetadataStore.Recover(root), "unknown repository entry");
            before.AssertSame(root);
        });

        Case("new-segment-matching-header-prefix-can-finish-once", () => {
            for (int length = 0; length <= 3; length++) {
                string root = New(suite, "header-prefix-" + length);
                MetadataStore.Seed(root);
                File.WriteAllBytes(Segment(root, 2), "RBF1"u8[..length].ToArray());
                MetadataRecoveryResult report = MetadataStore.Recover(root);
                Check(report.Status == "Succeeded" && report.Action == "CompleteSegment2Header" &&
                    report.ActionAttempted && report.LengthChanged && report.BarrierCompleted,
                    "matching new segment header completion: " + length + " / " + report);
                Check(File.ReadAllBytes(Segment(root, 2)).AsSpan().SequenceEqual("RBF1"u8),
                    "completed segment has exact RBF header");
                MetadataRecoveryResult retry = MetadataStore.Recover(root);
                Check(retry.Status == "Succeeded" && !retry.ActionAttempted,
                    "header completion is idempotent: " + retry);
                MetadataView created = MetadataStore.CreateB(root);
                Check(created.ActiveSegment == 2 && Read(root, "b").Name == "b",
                    "completed segment is used for rotation");
            }
        });

        Case("new-segment-header-contradiction-and-root-reference-refuse", () => {
            string wrong = New(suite, "wrong-header");
            MetadataStore.Seed(wrong);
            File.WriteAllBytes(Segment(wrong, 2), [(byte)'X', (byte)'B']);
            Snapshot wrongImage = Snapshot.Take(wrong);
            Refused(MetadataStore.Recover(wrong), "nonmatching new segment header");
            wrongImage.AssertSame(wrong);

            string referenced = New(suite, "referenced-header");
            MetadataStore.Seed(referenced);
            MetadataStore.CreateB(referenced);
            File.WriteAllBytes(Segment(referenced, 2), "RB"u8.ToArray());
            Snapshot referencedImage = Snapshot.Take(referenced);
            RejectRead(referenced, "b", "root-referenced segment with short header");
            Refused(MetadataStore.Recover(referenced), "root-referenced segment cannot be completed");
            referencedImage.AssertSame(referenced);
        });

        Case("impossible-orphan-directory-generation-blocks-segment-header-action", () => {
            string root = New(suite, "orphan-directory-generation-99-plus-header0");
            MetadataStore.Seed(root);
            MetadataBranchView oldA = Read(root, "a", 0);
            string catalog = Path.Combine(root, "catalog.rbf");
            byte[] orphanDirectory = ReadPayload(catalog, 4).Payload;
            Check(BinaryPrimitives.ReadInt64LittleEndian(orphanDirectory.AsSpan(8)) == 0,
                "model starts with the valid seed directory");
            BinaryPrimitives.WriteInt64LittleEndian(orphanDirectory.AsSpan(8), 99);
            AppendBytes(catalog, BuildFrame(suite, orphanDirectory, 0x3144_5255));
            File.WriteAllBytes(Segment(root, 2), []); // 0B matching new-segment header candidate.
            Snapshot forged = Snapshot.Take(root);
            Check(Read(root, "a", 0) == oldA,
                "Normal root0 remains readable while complete orphan directory is invisible");
            MetadataRecoveryResult report = MetadataStore.Recover(root);
            Refused(report, "generation-99 complete orphan must block header completion preaction");
            Check(report.Action is null,
                "qualification rejects impossible directory chronology before choosing header action: " + report);
            forged.AssertSame(root);
        });

        Case("orphan-directory-head-rewind-blocks-two-byte-root-tail-action", () => {
            string root = New(suite, "orphan-g3-B-rewind-plus-root-head2");
            MetadataStore.Seed(root);
            MetadataStore.CreateB(root);
            MetadataBranchView bAtRoot1 = Read(root, "b", 1);
            MetadataStore.AdvanceB(root);
            MetadataBranchView bAtRoot2 = Read(root, "b", 2);
            Check(bAtRoot1.RefId == bAtRoot2.RefId && bAtRoot1.Head != bAtRoot2.Head,
                "model begins with same B identity advanced to a distinct head");
            string catalog = Path.Combine(root, "catalog.rbf");
            byte[] orphanDirectory = ReadPayload(catalog, LastOffset(root, "catalog.rbf")).Payload;
            BinaryPrimitives.WriteInt64LittleEndian(orphanDirectory.AsSpan(8), 3);
            int bEntry = DirectoryEntry(orphanDirectory, "b");
            EventAddressCodec.Encode(bAtRoot1.Head, orphanDirectory.AsSpan(bEntry + 8, 16));
            AppendBytes(catalog, BuildFrame(suite, orphanDirectory, 0x3144_5255));
            string roots = Path.Combine(root, "roots.rbf");
            int rootTicketLength = ReadPayload(roots, 4).TicketLength;
            byte[] nextHead = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(nextHead, rootTicketLength);
            AppendBytes(roots, nextHead[..2]); // Real RBF HeadLen prefix, not a random corrupt tail.
            Snapshot forged = Snapshot.Take(root);
            Check(Read(root, "b") == bAtRoot2,
                "Normal root2 still selects advanced B, not the complete orphan directory");
            MetadataRecoveryResult report = MetadataStore.Recover(root);
            Refused(report, "generation-3 orphan directory rewinds B before any root-tail action");
            Check(report.Action is null, "recovery chose no tail action before rejecting head rewind: " + report);
            forged.AssertSame(root);
        });

        Case("trailer-fragments-1-through-11-are-unresolved", () => {
            for (int size = 1; size <= 11; size++) {
                string root = New(suite, "trailer-" + size);
                MetadataStore.Seed(root);
                string rootPath = Path.Combine(root, "roots.rbf");
                long nextOffset = new FileInfo(rootPath).Length;
                RbfWriteHooks? previous = RbfWriteInstrumentation.Current;
                try {
                    RbfWriteInstrumentation.Current = new RbfWriteHooks {
                        BeforeWrite = request => SamePath(request.Path, rootPath) && request.Offset == nextOffset
                            ? 56 + size : request.RequestedBytes
                    };
                    ExpectIOException(() => MetadataStore.CreateB(root));
                }
                finally { RbfWriteInstrumentation.Current = previous; }
                Snapshot before = Snapshot.Take(root);
                MetadataRecoveryResult report = MetadataStore.Recover(root);
                Check(report.Status == "Unresolved" && !report.ActionAttempted && !report.LengthChanged &&
                    !report.BarrierCompleted, "trailer fragment " + size + ": " + report);
                before.AssertSame(root);
            }
        });

        Case("root-allocation-eof-inside-Create-plus-event-short-tail-refuses-preaction", () => {
            string root = New(suite, "allocation-eof-mid-create");
            MetadataStore.Seed(root);
            MetadataStore.CreateB(root);
            string eventPath = Segment(root, 2);
            long nextEvent = new FileInfo(eventPath).Length;
            RbfWriteHooks? previous = RbfWriteInstrumentation.Current;
            try {
                RbfWriteInstrumentation.Current = new RbfWriteHooks {
                    BeforeWrite = request => SamePath(request.Path, eventPath) && request.Offset == nextEvent
                        ? 5 : request.RequestedBytes
                };
                ExpectIOException(() => MetadataStore.AdvanceB(root));
            }
            finally { RbfWriteInstrumentation.Current = previous; }
            long bCreate = LastOffset(root, "allocation.rbf");
            ReplacePayload(suite, root, "roots.rbf", LastOffset(root, "roots.rbf"),
                0x3152_5255, payload => BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(32),
                    bCreate + 8));
            Snapshot before = Snapshot.Take(root);
            MetadataRecoveryResult report = MetadataStore.Recover(root);
            Refused(report, "root allocation frontier cuts a complete Create while event has short tail");
            before.AssertSame(root);
        });

        Case("directory-parent-outside-segment-view-refuses-selected-and-orphan", () => {
            foreach (bool published in new[] { true, false }) {
                string root = New(suite, published ? "selected-parent-outside" : "orphan-parent-outside");
                MetadataView oldest = MetadataStore.Seed(root);
                MetadataStore.PopulateOrphans(root, 1);
                EventAddress orphanParent = new(SizedPtr.Create(oldest.Segment1Eof,
                    32 + EventFrameHeaderCodec.FixedLength + 24), 1, AddressHint.None);
                if (published) { MetadataStore.CreateB(root); }
                else {
                    ExpectStop(() => MetadataStore.CreateB(root, point => {
                        if (point == "AfterDirectoryFlush") { throw new SimulatedStopException(); }
                    }));
                }
                EventAddress bHead = published ? Read(root, "b").Head :
                    new(SizedPtr.Create(4, 32 + EventFrameHeaderCodec.FixedLength + 24), 2, AddressHint.None);
                ReplaceEvent(suite, root, bHead, metadata => metadata with { Parent = orphanParent });
                ReplacePayload(suite, root, "catalog.rbf", LastOffset(root, "catalog.rbf"),
                    0x3144_5255, payload => BinaryPrimitives.WriteInt64LittleEndian(
                        payload.AsSpan(24), oldest.Segment1Eof));
                Snapshot before = Snapshot.Take(root);
                if (published) { RejectRead(root, "b", "selected B Parent outside directory segment-1 EOF"); }
                else { Check(Read(root, "a").Name == "a", "old root remains readable despite bad orphan directory"); }
                Refused(MetadataStore.Recover(root),
                    published ? "selected directory omits ancestor" : "orphan directory omits ancestor");
                before.AssertSame(root);
            }
        });

        Case("full-ticket-without-Fence-can-rescue-but-wrong-trailer-tag-cannot", () => {
            string good = New(suite, "root-no-fence");
            MetadataStore.Seed(good);
            string goodRoot = Path.Combine(good, "roots.rbf");
            long nextRoot = new FileInfo(goodRoot).Length;
            RbfWriteHooks? previous = RbfWriteInstrumentation.Current;
            try {
                RbfWriteInstrumentation.Current = new RbfWriteHooks {
                    BeforeWrite = request => SamePath(request.Path, goodRoot) && request.Offset == nextRoot
                        ? request.RequestedBytes - 4 : request.RequestedBytes
                };
                ExpectIOException(() => MetadataStore.CreateB(good));
            }
            finally { RbfWriteInstrumentation.Current = previous; }
            MetadataRecoveryResult rescued = MetadataStore.Recover(good);
            Check(rescued.Status == "Succeeded" && rescued.ActionAttempted && rescued.LengthChanged &&
                rescued.BarrierCompleted, "valid full root ticket without Fence is a supported short tail: " + rescued);
            Check(Read(good, "a").Name == "a", "old root remains after no-Fence rescue");

            string wrongTag = New(suite, "no-fence-wrong-tag");
            MetadataStore.Seed(wrongTag);
            ExpectStop(() => MetadataStore.CreateB(wrongTag, point => {
                if (point == "AfterDirectoryFlush") { throw new SimulatedStopException(); }
            }));
            byte[] payload = ReadPayload(Path.Combine(wrongTag, "roots.rbf"), 4).Payload;
            BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(8), 1);
            string catalog = Path.Combine(wrongTag, "catalog.rbf");
            long directoryOffset = LastOffset(wrongTag, "catalog.rbf");
            int directoryLength = ReadPayload(catalog, directoryOffset).TicketLength;
            BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(16),
                SizedPtr.Create(directoryOffset, directoryLength).Packed);
            BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(24), new FileInfo(catalog).Length);
            BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(32),
                new FileInfo(Path.Combine(wrongTag, "allocation.rbf")).Length);
            byte[] fake = BuildFrame(suite, payload, 0xDEAD_0851);
            AppendBytes(Path.Combine(wrongTag, "roots.rbf"), fake[..^4]); // complete ticket, absent Fence
            Snapshot fakeImage = Snapshot.Take(wrongTag);
            Refused(MetadataStore.Recover(wrongTag), "CRC-valid next root with wrong trailer tag");
            fakeImage.AssertSame(wrongTag);

            string wrongHead = New(suite, "no-fence-wrong-headlen");
            MetadataStore.Seed(wrongHead);
            ExpectStop(() => MetadataStore.CreateB(wrongHead, point => {
                if (point == "AfterDirectoryFlush") { throw new SimulatedStopException(); }
            }));
            byte[] wrongPayload = ReadPayload(Path.Combine(wrongHead, "roots.rbf"), 4).Payload;
            BinaryPrimitives.WriteInt64LittleEndian(wrongPayload.AsSpan(8), 1);
            string wrongCatalog = Path.Combine(wrongHead, "catalog.rbf");
            long wrongDirectoryOffset = LastOffset(wrongHead, "catalog.rbf");
            int wrongDirectoryLength = ReadPayload(wrongCatalog, wrongDirectoryOffset).TicketLength;
            BinaryPrimitives.WriteUInt64LittleEndian(wrongPayload.AsSpan(16),
                SizedPtr.Create(wrongDirectoryOffset, wrongDirectoryLength).Packed);
            BinaryPrimitives.WriteInt64LittleEndian(wrongPayload.AsSpan(24), new FileInfo(wrongCatalog).Length);
            BinaryPrimitives.WriteInt64LittleEndian(wrongPayload.AsSpan(32),
                new FileInfo(Path.Combine(wrongHead, "allocation.rbf")).Length);
            byte[] impossible = BuildFrame(suite, wrongPayload, 0x3152_5255);
            int headLength = BinaryPrimitives.ReadInt32LittleEndian(impossible);
            BinaryPrimitives.WriteInt32LittleEndian(impossible, headLength + 4);
            AppendBytes(Path.Combine(wrongHead, "roots.rbf"), impossible[..^4]);
            Snapshot wrongHeadImage = Snapshot.Take(wrongHead);
            Refused(MetadataStore.Recover(wrongHead), "next root short tail contradicts fixed HeadLen");
            wrongHeadImage.AssertSame(wrongHead);
        });

        Case("variable-Create-and-catalog-frames-keep-real-four-byte-padding", () => {
            string root = New(suite, "variable-padding");
            MetadataStore.Seed(root);
            MetadataStore.CreateB(root);
            foreach ((string name, long offset) in new[] {
                ("allocation.rbf", LastOffset(root, "allocation.rbf")),
                ("catalog.rbf", 4L),
                ("catalog.rbf", LastOffset(root, "catalog.rbf"))
            }) {
                string path = Path.Combine(root, name);
                (byte[] payload, int ticketLength) = ReadPayload(path, offset);
                int expected = (payload.Length + 27) & ~3;
                Check(ticketLength == expected, "real RBF ticket rounds variable body to 4B: " + name);
                byte[] raw = File.ReadAllBytes(path);
                int padStart = checked((int)offset + 4 + payload.Length);
                int padLength = ticketLength - 24 - payload.Length;
                Check(padLength is >= 0 and <= 3 &&
                    raw.AsSpan(padStart, padLength).IndexOfAnyExcept((byte)0) < 0,
                    "real RBF padding bytes are zero: " + name);
            }
        });

        Case("F1-real-writer-hook-orders-dependent-flushes-before-root", () => {
            string root = New(suite, "f1-order");
            MetadataStore.Seed(root);
            var trace = new List<string>();
            RbfWriteHooks? previous = RbfWriteInstrumentation.Current;
            try {
                RbfWriteInstrumentation.Current = new RbfWriteHooks {
                    AfterWrite = observation => {
                        if (observation.Path is string path) {
                            trace.Add("write:" + Path.GetFileName(path) + "@" +
                                observation.Offset.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        }
                    },
                    AfterFlush = request => {
                        if (request.Path is string path) { trace.Add("flush:" + Path.GetFileName(path)); }
                    }
                };
                MetadataStore.CreateB(root);
            }
            finally { RbfWriteInstrumentation.Current = previous; }
            int WriteIndex(string file, long minimumOffset) {
                string prefix = "write:" + file + "@";
                for (int i = 0; i < trace.Count; i++) {
                    if (trace[i].StartsWith(prefix, StringComparison.Ordinal) &&
                        long.TryParse(trace[i].AsSpan(prefix.Length), out long offset) &&
                        offset >= minimumOffset) { return i; }
                }
                return -1;
            }
            int eventWrite = WriteIndex("00000002.rbf", 4); // offset 0 is only the RBF Header.
            int allocationWrite = WriteIndex("allocation.rbf", 4);
            int directoryWrite = WriteIndex("catalog.rbf", 4);
            int rootWrite = WriteIndex("roots.rbf", 4);
            Check(eventWrite >= 0 && allocationWrite > eventWrite && directoryWrite > allocationWrite &&
                rootWrite > directoryWrite, "F1 append order: " + string.Join(",", trace));
            Check(HasFlushBetween(trace, "00000002.rbf", eventWrite, allocationWrite) &&
                HasFlushBetween(trace, "allocation.rbf", allocationWrite, directoryWrite) &&
                HasFlushBetween(trace, "catalog.rbf", directoryWrite, rootWrite) &&
                HasFlushAfter(trace, "roots.rbf", rootWrite),
                "F1 dependency and publication DurableFlush order: " + string.Join(",", trace));
        });

        Case("F1-created-segment-and-root-before-ack-windows", () => {
            string created = New(suite, "f1-segment-created");
            MetadataStore.Seed(created);
            ExpectStop(() => MetadataStore.CreateB(created, point => {
                if (point == "AfterSegmentCreated") { throw new SimulatedStopException(); }
            }));
            Check(File.Exists(Segment(created, 2)) && Read(created, "a").Name == "a",
                "new segment name is process-visible before B publication");
            ExpectMissingBranch(created, "b");
            Check(MetadataStore.Recover(created).Status == "Succeeded", "created segment can be requalified");
            Check(MetadataStore.CreateB(created).ActiveSegment == 2, "retry adopts existing second segment");

            string root = New(suite, "f1-root-before-ack");
            MetadataStore.Seed(root);
            ExpectStop(() => MetadataStore.CreateB(root, point => {
                if (point == "BeforeAck") { throw new SimulatedStopException(); }
            }));
            Check(Read(root, "b").Name == "b", "durably flushed complete root selects B before reply");
        });

        Case("power-loss-mode-and-missing-name-barrier-refuse-before-mutation", () => {
            string root = New(suite, "power-loss-required");
            MetadataStore.Seed(root);
            Snapshot before = Snapshot.Take(root);
            try {
                MetadataStore.CreateB(root, durability: MetadataDurabilityMode.PowerLossRequired);
                throw new Exception("Power-loss mode unexpectedly committed B.");
            }
            catch (NotSupportedException error) {
                Check(error.Message.Contains("NamesBarrierNotProvided", StringComparison.Ordinal),
                    "writer refusal identifies missing names barrier");
            }
            before.AssertSame(root);
            MetadataRecoveryResult report = MetadataStore.Recover(root,
                durability: MetadataDurabilityMode.PowerLossRequired);
            Refused(report, "PowerLossRequired recovery without directory names barrier");
            Check(report.Detail.Contains("NamesBarrierNotProvided", StringComparison.Ordinal) &&
                report.Detail.Contains("PowerLossRequired", StringComparison.Ordinal),
                "recovery refusal gives both exact missing preconditions: " + report.Detail);
            before.AssertSame(root);
        });

        Case("F2-byte-model-missing-name-dependency-and-old-range-refuse", () => {
            string missing = New(suite, "f2-name-lost");
            MetadataStore.Seed(missing);
            MetadataStore.CreateB(missing);
            File.Move(Segment(missing, 2), Segment(missing, 2) + ".name-lost");
            Snapshot missingImage = Snapshot.Take(missing);
            RejectRead(missing, "b", "F2 missing second-segment name model");
            Refused(MetadataStore.Recover(missing), "F2 missing segment name");
            missingImage.AssertSame(missing);

            string lost = New(suite, "f2-dependency-lost");
            MetadataStore.Seed(lost);
            MetadataStore.CreateB(lost);
            File.WriteAllBytes(Segment(lost, 2), "RBF1"u8.ToArray());
            Snapshot lostImage = Snapshot.Take(lost);
            RejectRead(lost, "b", "F2 root present but B dependency absent model");
            Refused(MetadataStore.Recover(lost), "F2 root-referenced event lost");
            lostImage.AssertSame(lost);

            string changed = New(suite, "f2-old-range-changed");
            MetadataStore.Seed(changed);
            MetadataStore.CreateB(changed);
            FlipByte(Segment(changed, 1), Read(changed, "a", 0).Head.Ticket.Offset + 8);
            Snapshot changedImage = Snapshot.Take(changed);
            RejectRead(changed, "a", "F2 older complete event extent changed model");
            Refused(MetadataStore.Recover(changed), "F2 old-range corruption");
            changedImage.AssertSame(changed);
        });

        Case("F2-unflushed-readable-root-loss-is-image-model", () => {
            string root = New(suite, "f2-root-own-unflush");
            MetadataStore.Seed(root);
            string rootPath = Path.Combine(root, "roots.rbf");
            byte[] oldRoots = File.ReadAllBytes(rootPath);
            var trace = new List<string>();
            RbfWriteHooks? previous = RbfWriteInstrumentation.Current;
            try {
                RbfWriteInstrumentation.Current = new RbfWriteHooks {
                    AfterWrite = observation => {
                        if (observation.Path is string path) { trace.Add("write:" + Path.GetFileName(path)); }
                    },
                    AfterFlush = request => {
                        if (request.Path is string path) { trace.Add("flush:" + Path.GetFileName(path)); }
                    }
                };
                ExpectStop(() => MetadataStore.CreateB(root, point => {
                    if (point == "AfterRootAppend") { throw new SimulatedStopException(); }
                }));
            }
            finally { RbfWriteInstrumentation.Current = previous; }
            int rootWrite = trace.LastIndexOf("write:roots.rbf");
            Check(rootWrite >= 0 && !HasFlushAfter(trace, "roots.rbf", rootWrite),
                "F1 hook observes readable root before own DurableFlush");
            Check(Read(root, "b").Name == "b", "unflushed complete root is readable in the process image");
            // F2 image model only: replace the root file with its earlier bytes as if the
            // unconfirmed append was lost. This is not a Windows power-loss observation.
            File.WriteAllBytes(rootPath, oldRoots);
            ExpectMissingBranch(root, "b");
            MetadataRecoveryResult result = MetadataStore.Recover(root);
            Check(result.Status == "Succeeded", "old root plus complete orphan dependencies requalify");
            Check(MetadataStore.CreateB(root).ActiveSegment == 2, "retry after root-loss model adopts seg2");
        });

        Case("real-exclusive-lock-IoFailure-is-not-normal-fallback-or-recovery-qualification", () => {
            string root = New(suite, "exclusive-lock");
            MetadataStore.Seed(root);
            Snapshot initial = Snapshot.Take(root);
            using (var held = new FileStream(Path.Combine(root, "repository.lock"), FileMode.Open,
                FileAccess.ReadWrite, FileShare.None)) {
                ExpectIOException(() => {
                    using MetadataStore.MetadataReader reader = MetadataStore.Open(root);
                    _ = reader.Read("a");
                });
                MetadataRecoveryResult report = MetadataStore.Recover(root);
                Check(report.Status == "IoOrActionFailure" && !report.ActionAttempted &&
                    !report.LengthChanged && !report.BarrierCompleted,
                    "real exclusive-lock I/O failure must not authorize recovery: " + report);
            }
            initial.AssertSame(root);
            Check(Read(root, "a").Name == "a", "fresh Open succeeds after lock release");
            Check(MetadataStore.Recover(root).Status == "Succeeded", "fresh recovery confirms healthy fixture");
        });

        Case("cancel-after-header-write-or-tail-truncate-reports-action-and-retries", () => {
            string headerRoot = New(suite, "cancel-after-header-write");
            MetadataStore.Seed(headerRoot);
            File.WriteAllBytes(Segment(headerRoot, 2), "RB"u8.ToArray());
            using (var cancel = new CancellationTokenSource()) {
                MetadataRecoveryResult interrupted = MetadataStore.Recover(headerRoot,
                    checkpoint: point => {
                        if (point == "AfterHeaderWriteBeforeFlush") { cancel.Cancel(); }
                    }, cancellationToken: cancel.Token);
                Check(interrupted.Status == "Cancelled" && interrupted.Action == "CompleteSegment2Header" &&
                    interrupted.ActionAttempted && interrupted.LengthChanged && !interrupted.BarrierCompleted,
                    "cancel after real header write records action but no completed barrier: " + interrupted);
            }
            Check(File.ReadAllBytes(Segment(headerRoot, 2)).AsSpan().SequenceEqual("RBF1"u8),
                "header bytes remain after cancellation before flush");
            MetadataRecoveryResult headerRetry = MetadataStore.Recover(headerRoot);
            Check(headerRetry.Status == "Succeeded" && !headerRetry.ActionAttempted,
                "fresh retry confirms completed header: " + headerRetry);

            string tailRoot = New(suite, "cancel-after-tail-truncate");
            MetadataStore.Seed(tailRoot);
            string rootPath = Path.Combine(tailRoot, "roots.rbf");
            byte[] oldCompleteRoot = File.ReadAllBytes(rootPath);
            long nextRoot = oldCompleteRoot.LongLength;
            RbfWriteHooks? previous = RbfWriteInstrumentation.Current;
            try {
                RbfWriteInstrumentation.Current = new RbfWriteHooks {
                    BeforeWrite = request => SamePath(request.Path, rootPath) && request.Offset == nextRoot
                        ? request.RequestedBytes - 4 : request.RequestedBytes
                };
                ExpectIOException(() => MetadataStore.CreateB(tailRoot));
            }
            finally { RbfWriteInstrumentation.Current = previous; }
            using (var cancel = new CancellationTokenSource()) {
                MetadataRecoveryResult interrupted = MetadataStore.Recover(tailRoot,
                    checkpoint: point => {
                        if (point == "AfterSetLengthBeforeFlush") { cancel.Cancel(); }
                    }, cancellationToken: cancel.Token);
                Check(interrupted.Status == "Cancelled" && interrupted.Action == "TruncateTail" &&
                    interrupted.ActionAttempted && interrupted.LengthChanged && !interrupted.BarrierCompleted,
                    "cancel after real SetLength records action but no completed barrier: " + interrupted);
            }
            Check(File.ReadAllBytes(rootPath).AsSpan().SequenceEqual(oldCompleteRoot),
                "truncation retained exact old complete root bytes");
            MetadataRecoveryResult tailRetry = MetadataStore.Recover(tailRoot);
            Check(tailRetry.Status == "Succeeded" && !tailRetry.ActionAttempted,
                "fresh retry confirms truncated file and complete orphans: " + tailRetry);
            ExpectMissingBranch(tailRoot, "b");
        });

        return new { Status = "Passed", Count = passed.Count, Cases = passed,
            Observations = observations, FixtureRoot = suite };
    }

    private static MetadataBranchView Read(string root, string branch, long? generation = null) {
        using MetadataStore.MetadataReader reader = MetadataStore.Open(root, generation);
        return reader.Read(branch);
    }

    private static MetadataView ReadView(string root, long? generation = null) {
        using MetadataStore.MetadataReader reader = MetadataStore.Open(root, generation);
        return reader.ReadAll();
    }

    private static void ExpectMissingBranch(string root, string branch, long? generation = null) {
        try { _ = Read(root, branch, generation); }
        catch (KeyNotFoundException) { return; }
        throw new Exception($"Branch {branch} unexpectedly exists in generation {generation?.ToString() ?? "latest"}.");
    }

    private static void ExpectStop(Action body) {
        try { body(); }
        catch (SimulatedStopException) { return; }
        throw new Exception("Required checkpoint was not reached.");
    }

    private static void ExpectIOException(Action body) {
        try { body(); }
        catch (IOException) { return; }
        throw new Exception("Expected actual RBF short-write I/O failure.");
    }

    private static bool SamePath(string? a, string b) => a is not null &&
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static bool HasFlushBetween(List<string> trace, string file, int after, int before) {
        for (int i = after + 1; i < before; i++) {
            if (trace[i] == "flush:" + file) { return true; }
        }
        return false;
    }

    private static bool HasFlushAfter(List<string> trace, string file, int after) =>
        trace.Skip(after + 1).Contains("flush:" + file);

    private static void RejectRead(string root, string branch, string context) {
        try { _ = Read(root, branch); }
        catch (InvalidDataException) { return; }
        throw new Exception("Normal read accepted invalid " + context);
    }

    private static void Refused(MetadataRecoveryResult result, string context) {
        Check(result.Status == "Refused" && !result.ActionAttempted && !result.LengthChanged &&
            !result.BarrierCompleted, context + ": " + result);
    }

    private static void RefuseAndKeepImage(string root, string branch, string context) {
        Snapshot before = Snapshot.Take(root);
        RejectRead(root, branch, context);
        Refused(MetadataStore.Recover(root), context);
        before.AssertSame(root);
    }

    private static void Check(bool condition, string message) {
        if (!condition) { throw new Exception(message); }
    }

    private static string New(string suite, string label) =>
        Path.Combine(suite, label + "-" + Guid.NewGuid().ToString("N"));

    private static string Segment(string root, uint number) => Path.Combine(root, "events", "segments",
        number.ToString("D8", System.Globalization.CultureInfo.InvariantCulture) + ".rbf");

    private static long LastOffset(string root, string name) => MetadataStore.Inventory(root)
        .Single(file => file.Path == name).Offsets.Last();

    private static int DirectoryEntry(byte[] payload, string name) {
        int count = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(20));
        int pos = 40;
        for (int i = 0; i < count; i++) {
            int length = payload[pos++];
            string found = System.Text.Encoding.UTF8.GetString(payload, pos, length);
            pos += length;
            int refId = pos;
            pos += 24;
            if (found == name) { return refId; }
        }
        throw new Exception("Directory branch entry missing: " + name);
    }

    private static void FlipByte(string path, long offset) {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        stream.Position = offset;
        int value = stream.ReadByte();
        Check(value >= 0, "corruption offset outside file: " + path);
        stream.Position = offset;
        stream.WriteByte((byte)(value ^ 0x80));
        stream.Flush(flushToDisk: true);
    }

    private static void ReplacePayload(string suite, string root, string name, long offset,
        uint tag, Action<byte[]> mutate) {
        string path = Path.Combine(root, name);
        (byte[] payload, int ticketLength) = ReadPayload(path, offset);
        mutate(payload);
        byte[] replacement = BuildFrame(suite, payload, tag);
        Check(replacement.Length == ticketLength + 4, "replacement frame length changed");
        WriteAt(path, offset, replacement);
    }

    private static (byte[] Payload, int TicketLength) ReadPayload(string path, long offset) {
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        source.Position = offset;
        Span<byte> head = stackalloc byte[4];
        source.ReadExactly(head);
        int ticketLength = BinaryPrimitives.ReadInt32LittleEndian(head);
        using var file = RbfFile.OpenReadOnlyCandidate(path, source.Length);
        using var frame = file.ReadPooledFrame(SizedPtr.Create(offset, ticketLength)).Unwrap();
        Check(frame.TailMetaLength == 0, "replacement model expects payload-only frame");
        return (frame.PayloadAndMeta.ToArray(), ticketLength);
    }

    private static void ReplaceEvent(string suite, string root, EventAddress address,
        Func<EventFrameHeader, EventFrameHeader> mutate) {
        string path = Segment(root, address.SegmentNumber);
        byte[] payload;
        byte[] meta;
        using (var file = RbfFile.OpenReadOnlyCandidate(path, new FileInfo(path).Length)) {
            using var frame = file.ReadPooledFrame(address.Ticket).Unwrap();
            payload = frame.PayloadAndMeta[..32].ToArray();
            meta = new byte[EventFrameHeaderCodec.FixedLength];
            EventFrameHeader header = EventFrameHeaderCodec.Decode(
                frame.PayloadAndMeta[32..]).Unwrap();
            EventFrameHeader revised = mutate(header);
            EventFrameHeaderCodec.Encode(in revised, meta);
        }
        byte[] replacement = BuildFrame(suite, payload, Atelia.EventJournal.EventJournal.EventFrameTag, meta);
        Check(replacement.Length == address.Ticket.Length + 4, "replacement EventFrame length changed");
        WriteAt(path, address.Ticket.Offset, replacement);
    }

    private static byte[] BuildFrame(string suite, byte[] payload, uint tag, byte[]? tailMeta = null) {
        string scratch = Path.Combine(suite, "frame-" + Guid.NewGuid().ToString("N") + ".rbf");
        try {
            using (var file = RbfFile.CreateNew(scratch, RbfCacheMode.Off)) {
                file.Append(tag, payload, tailMeta ?? []).Unwrap();
                file.DurableFlush();
            }
            return File.ReadAllBytes(scratch)[4..];
        }
        finally { if (File.Exists(scratch)) { File.Delete(scratch); } }
    }

    private static void WriteAt(string path, long offset, byte[] bytes) {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        stream.Position = offset;
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void AppendBytes(string path, byte[] bytes) {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        stream.Position = stream.Length;
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private sealed class SimulatedStopException : Exception { }

    private sealed record Snapshot(Dictionary<string, byte[]> Files) {
        internal long TotalBytes => Files.Values.Sum(file => file.LongLength);

        internal static Snapshot Take(string root) => new(Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(root, path), File.ReadAllBytes,
                StringComparer.OrdinalIgnoreCase));

        internal void AssertSame(string root) {
            Snapshot actual = Take(root);
            Check(Files.Keys.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(
                actual.Files.Keys.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase),
                "file-name inventory changed");
            foreach ((string name, byte[] bytes) in Files) {
                Check(bytes.AsSpan().SequenceEqual(actual.Files[name]), "file image changed: " + name);
            }
        }

        internal void AssertPrefixRetained(string root) {
            foreach ((string name, byte[] bytes) in Files) {
                string path = Path.Combine(root, name);
                Check(File.Exists(path), "orphan file removed: " + name);
                byte[] actual = File.ReadAllBytes(path);
                Check(actual.Length >= bytes.Length && actual.AsSpan(0, bytes.Length).SequenceEqual(bytes),
                    "orphan complete prefix moved or changed: " + name);
            }
        }
    }
}
