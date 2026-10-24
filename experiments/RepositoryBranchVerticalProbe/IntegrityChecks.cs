using System.Buffers.Binary;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;
using Atelia.FrameStore;

namespace RepositoryBranchVerticalProbe;

// These fixtures deliberately publish through raw HeadStore only after the public
// Repository has closed. No live DG cache is reused after external mutation.
internal static class IntegrityChecks {
    private static int _checks;

    internal static void Run() {
        WrongNearestSource();
        PublishedDeliveryFailure();
        UnpublishedRegistration();
        Console.WriteLine($"PASS: {_checks} integrity checks; malformed descriptor, wrong historical source, publication failure.");
    }

    private static void UnpublishedRegistration() {
        string path = NewPath("registration");
        BranchId original;
        FrameAddress? schemaBefore;
        using (var repo = Repository.CreateNew(path, World.Models())) {
            original = repo.CreateRootBranchFromState("正式成员", new World()).Id;
            schemaBefore = repo.SharedSchemaRoot;
        }
        Atelia.HeadStore.HeadEntryId directoryBefore;
        using (var resources = RepositoryResources.OpenReadOnlyExisting(path)) {
            directoryBefore = resources.Heads.ReadHead(1).Unwrap().Id;
        }
        using (var repo = Repository.OpenExisting(path, World.Models())) {
            using var work = repo.OpenWorkingCopy(original);
            ((World)work.State!).Progress = 1;
            work.CommitState();
        }
        using (var resources = RepositoryResources.OpenReadOnlyExisting(path)) {
            Check(resources.Heads.ReadHead(1).Unwrap().Id == directoryBefore, "ordinary commit does not publish the member directory");
        }
        BranchId rejected = default;
        CommitId physicalCommit = default;
        FrameAddress? orphan = null;
        using (var repo = Repository.OpenExisting(path, World.Models())) {
            repo.SaveStageObserver = stage => {
                if (stage == RepositorySaveStage.BeforePublication) { throw new IOException("stop before D/S registration"); }
            };
            try {
                // This model adds new catalog definitions. Its physical business Head
                // already exists when the final D/S registration is interrupted.
                repo.CreateRootBranchFromEvent("未登记", new ActionRecord { Number = 9 });
                throw new InvalidOperationException("Expected registration failure.");
            } catch (RepositoryPublicationException error) {
                Check(error.Outcome == RepositoryPublicationOutcome.NotPublished, "physical Head success alone is not DG registration");
                Check(error.TargetBranchId is not null && error.CandidateCommitId is not null,
                    "failed registration retains actual known physical identities");
                Check(repo.IsFaulted, "interrupted append/install owner is retired");
                rejected = error.TargetBranchId!.Value;
                physicalCommit = error.CandidateCommitId!.Value;
                orphan = error.CandidateRevisionAddress;
            }
        }
        using (var repo = Repository.OpenExisting(path, World.Models())) {
            Check(repo.ListBranches().Length == 1 && repo.SharedSchemaRoot == schemaBefore,
                "fresh owner sees neither failed member nor its new Catalog");
            Reject<KeyNotFoundException>(() => repo.ReadBranchInfo(rejected), "unregistered physical Head is not a Branch");
            Reject<KeyNotFoundException>(() => repo.ReadCommitInfo(physicalCommit), "unregistered physical Entry is not a Commit");
            Check(((World)repo.LoadState(repo.ReadCurrentCommit(original).Commit.Id)).Progress == 1,
                "previous registered State remains usable");
        }
        using (var resources = RepositoryResources.OpenReadOnlyExisting(path)) {
            Check(resources.Heads.ReadEntry(physicalCommit.Value).Unwrap().Id == physicalCommit.Value,
                "unregistered physical Entry is retained without claiming it");
            Check(resources.GraphStore.ReadBusinessGraph(orphan!.Value, null).Kind == StoredGraphKind.Event,
                "completed unpublished content is retained append-only");
            Check(resources.Heads.ReadHead(1).Unwrap().Id == directoryBefore,
                "failed registration did not replace the directory entry");
        }
    }

    private static void WrongNearestSource() {
        string path = NewPath("wrong-source");
        BranchId branch;
        CommitId s0, s1, forged;
        using (var repo = Repository.CreateNew(path, World.Models())) {
            branch = repo.CreateRootBranchFromState("错源", new World()).Id;
            s0 = repo.ReadCurrentCommit(branch).Commit.Id;
            using var work = repo.OpenWorkingCopy(branch);
            ((World)work.State!).Inventory[0] = 42;
            s1 = work.CommitState().Id;
        }
        using (var resources = RepositoryResources.OpenExisting(path)) {
            GraphRecordStore store = resources.GraphStore;
            var first = resources.ReadDescriptor(s0.Value);
            var second = resources.ReadDescriptor(s1.Value);
            StoredGraphRevision baseline = store.ReadBusinessGraph(first.Address, null);
            StoredGraphRevision latest = store.ReadBusinessGraph(second.Address, first.Address);
            Check(latest.SourceGraph == first.Address, "real second State has its strict previous State source");
            var localRecords = store.ReadBatch(latest.OwnedBatch!.Value).Records;
            Check(localRecords.Any(r => r.Kind == Atelia.DurableGraph.Storage.ObjectVersionKind.Delta),
                "ordinary State save writes an actual object Delta");
            Check(store.ReadDirectoryVersion(latest.DirectoryRoot).Kind == Atelia.DurableGraph.Storage.ObjectVersionKind.Delta,
                "ordinary State save writes an actual object-directory Delta");
            DescriptorBoundaries(first.Address);

            // Internally consistent State: directory/source both use S0. Its actual
            // Head history will be S0,S1,S2, so S1 is the only correct source.
            var graph = store.AppendBusiness(StoredGraphKind.State, latest.SchemaRoot,
                new PreparedGraphWrite(first.Address, baseline.RootId, []));
            byte[] payload = BusinessEntryPayloadCodec.Encode(graph.Address, [9]);
            var id = resources.Heads.PublishHead(branch.Value, payload, out _).Unwrap();
            forged = new CommitId(id);
            Check(store.ReadBusinessGraph(graph.Address, first.Address).SourceGraph == first.Address,
                "wrong historical source is nevertheless physically self-consistent");
        }
        using (var repo = Repository.OpenExisting(path, World.Models())) {
            Check(repo.ReadCommitInfo(forged).Kind == CommitKind.State, "description does not claim complete content qualification");
            Reject<InvalidDataException>(() => repo.LoadState(forged), "exact State load rejects physically consistent but historically wrong source");
            Reject<InvalidDataException>(() => repo.OpenWorkingCopy(branch), "cold working-copy open checks tip State source");
            Check(((World)repo.LoadState(s1)).Inventory[0] == 42, "an unrelated bad tip does not invalidate the preceding State");
        }
    }

    private static void PublishedDeliveryFailure() {
        string path = NewPath("published-failure");
        BranchId branch;
        CommitId before;
        using (var repo = Repository.CreateNew(path, World.Models())) {
            branch = repo.CreateRootBranchFromState("发布之后", new World()).Id;
            using var work = repo.OpenWorkingCopy(branch);
            before = work.BaseCommitId;
            ((World)work.State!).Progress = 27;
            repo.SaveStageObserver = stage => {
                if (stage == RepositorySaveStage.AfterPublication) { throw new IOException("deliberate delivery failure"); }
            };
            try {
                work.CommitState();
                throw new InvalidOperationException("Expected publication failure.");
            } catch (RepositoryPublicationException error) {
                Check(error.Outcome == RepositoryPublicationOutcome.Published, "post-publication failure reports Published");
                Check(repo.IsFaulted, "post-publication failure invalidates owner");
                Check(error.TargetBranchId == branch && error.ExpectedBaseCommitId == before,
                    "failure retains target and expected baseline");
            }
        }
        using (var repo = Repository.OpenExisting(path, World.Models())) {
            var current = repo.ReadCurrentCommit(branch).Commit;
            Check(current.Id != before && ((World)repo.LoadState(current.Id)).Progress == 27,
                "fresh owner observes the already-published save");
        }
    }

    private static void DescriptorBoundaries(FrameAddress graph) {
        byte[] source = [1, 2, 3];
        byte[] payload = BusinessEntryPayloadCodec.Encode(graph, source);
        source[0] = 7;
        var decoded = BusinessEntryPayloadCodec.Decode(payload);
        Check(decoded.Graph == graph && decoded.Summary.SequenceEqual(new byte[] { 1, 2, 3 }), "descriptor copies summary");
        decoded.Summary[0] = 8;
        Check(BusinessEntryPayloadCodec.Decode(payload).Summary[0] == 1, "decoded summary does not alias payload");
        for (int length = 0; length < payload.Length; length++) {
            byte[] truncated = payload[..length];
            Reject<InvalidDataException>(() => BusinessEntryPayloadCodec.Decode(truncated), "truncated descriptor");
        }
        Reject<InvalidDataException>(() => BusinessEntryPayloadCodec.Decode([.. payload, 0]), "trailing descriptor byte");
        byte[] hugeLength = (byte[])payload.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(hugeLength.AsSpan(16), uint.MaxValue);
        Reject<InvalidDataException>(() => BusinessEntryPayloadCodec.Decode(hugeLength), "oversized declared summary");
        byte[] badAddress = (byte[])payload.Clone();
        badAddress.AsSpan(4, 12).Clear();
        Reject<InvalidDataException>(() => BusinessEntryPayloadCodec.Decode(badAddress), "default graph address");
        byte[] maximum = BusinessEntryPayloadCodec.Encode(graph, new byte[BusinessEntryPayloadCodec.MaxSummaryLength]);
        Check(maximum.Length == Atelia.HeadStore.HeadStore.MaxHeadEntryPayloadLength &&
            BusinessEntryPayloadCodec.Decode(maximum).Summary.Length == BusinessEntryPayloadCodec.MaxSummaryLength,
            "exact payload maximum is legal");
        Reject<ArgumentOutOfRangeException>(() => BusinessEntryPayloadCodec.Encode(graph,
            new byte[BusinessEntryPayloadCodec.MaxSummaryLength + 1]), "one byte beyond maximum");
        Check(BusinessEntryPayloadCodec.Decode(BusinessEntryPayloadCodec.Encode(graph, [])).Summary.Length == 0, "empty summary");
    }

    private static string NewPath(string role) {
        string path = Path.GetFullPath(Path.Combine(".artifacts", "db096-branch-vertical", role + "-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }
    private static void Check(bool condition, string message) {
        if (!condition) { throw new InvalidOperationException("FAIL: " + message); }
        _checks++;
    }
    private static void Reject<T>(Action action, string message) where T : Exception {
        try { action(); }
        catch (T) { _checks++; return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + ": " + message);
    }
}
