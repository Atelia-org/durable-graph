using System.Reflection;

using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using Atelia.HeadStore;
using WidgetState = Atelia.DurableGraph.Generated.Family_5030576964676574.V1;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed partial class P0BusinessPlanningAccountingTests {
    [Fact]
    public void P0NormalizedBaselineInstallsMixedNewAndRetainedObjectHeadsAndColdContinuesDelta() {
        FrameAddress initial, changed;
        ObjectId rootId, oldChildId, rootListId, childListId, newChildId;
        using (RepositoryStorageFacade repository = RepositoryStorageFacade.CreateNew(_path)) {
            BranchSaveSessionFixture session = new(repository, "main", Models());
            DB087Widget root = TwoListRing();
            using (PreparedFixtureSave save = session.Prepare(root)) { initial = save.Commit().Business.Address; }
            NormalizedGraphState source = Baseline(session);
            AssertAccounting(repository, session);
            Assert.Equal(4, source.Objects.Count);
            rootId = session.StateSource!.RootId;
            WidgetState rootDto = source.Objects[rootId].Current.GetState<WidgetState>();
            oldChildId = rootDto.Segment0Field3;
            rootListId = rootDto.Segment0Field2;
            childListId = source.Objects[oldChildId].Current.GetState<WidgetState>().Segment0Field2;
            Assert.NotEqual(rootListId, childListId);
            ObjectStorageInfo retained = source.Objects[rootListId].Storage!.Value;
            ObjectStorageInfo priorList = source.Objects[childListId].Storage!.Value;
            List<int> childNumbers = root.Child!.Numbers;
            root.Child = new() { Value = 3, Numbers = childNumbers, Child = root };
            childNumbers[7] = 9;
            using (PreparedFixtureSave save = session.Prepare(root)) {
                Assert.Equal(oldChildId.Value, Assert.Single(save.Business.Write.RemovedIds));
                Assert.Equal(3, save.Business.Write.Records.Count);
                ObjectVersionRecord delta = Assert.Single(save.Business.Write.Records, row => row.ObjectId == childListId.Value);
                Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
                Assert.Equal(priorList.Head, delta.PriorAddress);
                ObjectVersionRecord inserted = Assert.Single(save.Business.Write.Records,
                    row => !source.Objects.ContainsKey(new(row.ObjectId)));
                newChildId = new(inserted.ObjectId);
                Assert.Equal(ObjectVersionKind.Base, inserted.Kind);
                Assert.DoesNotContain(save.Business.Write.Records, row => row.ObjectId == rootListId.Value);
                RepositorySaveResult result = save.Commit();
                changed = result.Business.Address;
                NormalizedGraphState accepted = Baseline(session);
                Assert.Equal(changed, accepted.RevisionAddress);
                Assert.DoesNotContain(oldChildId, accepted.Objects.Keys);
                Assert.Equal(4, accepted.Objects.Count);
                Assert.Equal(retained, accepted.Objects[rootListId].Storage);
                Assert.Equal(priorList.ReconstructionPayloadBytes + ObjectVersionPayloadSize.GetDeltaPayloadBytes(delta.Body.Length, priorList.Head),
                    accepted.Objects[childListId].Storage!.Value.ReconstructionPayloadBytes);
                Assert.Equal(ObjectVersionPayloadSize.GetBasePayloadBytes(inserted.Body.Length),
                    accepted.Objects[newChildId].Storage!.Value.ReconstructionPayloadBytes);
                foreach (ObjectVersionRecord record in save.Business.Write.Records) {
                    Assert.Equal(result.Business.OwnedBatch, accepted.Objects[new(record.ObjectId)].Storage!.Value.Head);
                    Assert.NotEqual(changed, accepted.Objects[new(record.ObjectId)].Storage!.Value.Head);
                }
                AssertAccounting(repository, session);
            }
            CheckTwoListRing(Assert.IsType<DB087Widget>(BusinessPersistenceAdapter.ReadRoot(repository.Store, MechanismFixture.Snapshot(repository, initial), Models())), 2, 0);
            CheckTwoListRing(root, 3, 9);
        }

        using (RepositoryStorageFacade repository = RepositoryStorageFacade.OpenExisting(_path)) {
            BranchSaveSessionFixture session = new(repository, "main", Models());
            Assert.Equal(changed, Baseline(session).RevisionAddress);
            AssertAccounting(repository, session);
            DB087Widget root = Assert.IsType<DB087Widget>(session.State);
            CheckTwoListRing(root, 3, 9);
            ObjectStorageInfo prior = Baseline(session).Objects[childListId].Storage!.Value;
            root.Child!.Numbers[8] = 9;
            using PreparedFixtureSave save = session.Prepare(root);
            ObjectVersionRecord delta = Assert.Single(save.Business.Write.Records);
            Assert.Equal(childListId.Value, delta.ObjectId);
            Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
            Assert.Equal(prior.Head, delta.PriorAddress);
            RepositorySaveResult result = save.Commit();
            Assert.Equal(newChildId, Baseline(session).Objects[rootId].Current.GetState<WidgetState>().Segment0Field3);
            Assert.Equal(prior.ReconstructionPayloadBytes + ObjectVersionPayloadSize.GetDeltaPayloadBytes(delta.Body.Length, prior.Head),
                Baseline(session).Objects[childListId].Storage!.Value.ReconstructionPayloadBytes);
            AssertAccounting(repository, session);
            CheckTwoListRing(Assert.IsType<DB087Widget>(BusinessPersistenceAdapter.ReadRoot(repository.Store, MechanismFixture.Snapshot(repository, changed), Models())), 3, 9);
            Assert.Equal(9, Assert.IsType<DB087Widget>(BusinessPersistenceAdapter.ReadRoot(repository.Store, MechanismFixture.Snapshot(repository, result.Business.Address), Models())).Child!.Numbers[8]);
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("graphHead")]
    public void P0NormalizedBaselineChecksStorageOfSourceMembersThatWillBeRemoved(string corruption) {
        using RepositoryStorageFacade repository = RepositoryStorageFacade.CreateNew(_path);
        BranchSaveSessionFixture session = new(repository, "main", Models());
        DB087Widget root = TwoListRing();
        using (PreparedFixtureSave save = session.Prepare(root)) { save.Commit(); }
        NormalizedGraphState baseline = Baseline(session);
        ObjectId childId = baseline.Objects[session.StateSource!.RootId].Current.GetState<WidgetState>().Segment0Field3;
        Dictionary<ObjectId, NormalizedObject> rows = MutableRows(baseline);
        NormalizedObject child = rows[childId];
        root.Child = null; // This source member is absent from the next reachable closure.
        rows[childId] = child with { Storage = corruption == "missing" ? null : child.Storage!.Value with { Head = baseline.RevisionAddress } };
        long frames = repository.Store.AppendedFrames;
        Assert.Throws<InvalidDataException>(() => session.Prepare(root));
        Assert.Equal(frames, repository.Store.AppendedFrames);
        Assert.False(repository.HasPending);
        Assert.False(repository.IsFaulted);
        Assert.Equal(baseline.RevisionAddress, MechanismFixture.FindCurrent(repository, "main")?.Address);
        rows[childId] = child;
        using PreparedFixtureSave valid = session.Prepare(root); // Positive control still removes the complete old members.
        Assert.Contains(childId.Value, valid.Business.Write.RemovedIds);
        valid.Commit();
        Assert.DoesNotContain(childId, Baseline(session).Objects.Keys);
        AssertAccounting(repository, session);
    }

    [Fact]
    public void P0NormalizedBaselineCannotBeBorrowedFromAnotherOwnerWithTheSameRealAddresses() {
        using (RepositoryStorageFacade repository = RepositoryStorageFacade.CreateNew(_path)) {
            BranchSaveSessionFixture session = new(repository, "main", Models());
            using PreparedFixtureSave seed = session.Prepare(TwoListRing());
            seed.Commit();
        }
        string copy = _path + "-copy";
        try {
            Directory.CreateDirectory(copy);
            foreach (string directory in Directory.EnumerateDirectories(_path, "*", SearchOption.AllDirectories)) {
                Directory.CreateDirectory(Path.Combine(copy, Path.GetRelativePath(_path, directory)));
            }
            foreach (string file in Directory.EnumerateFiles(_path, "*", SearchOption.AllDirectories)) {
                string target = Path.Combine(copy, Path.GetRelativePath(_path, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
            using RepositoryStorageFacade first = RepositoryStorageFacade.OpenExisting(_path);
            using RepositoryStorageFacade second = RepositoryStorageFacade.OpenExisting(copy);
            BranchSaveSessionFixture firstSession = new(first, "main", Models()), secondSession = new(second, "main", Models());
            NormalizedGraphState foreign = Baseline(firstSession), owned = Baseline(secondSession);
            Assert.Same(first.Store, foreign.SourceSnapshot!.SourceOwner);
            Assert.Same(second.Store, owned.SourceSnapshot!.SourceOwner);
            Assert.Equal(foreign.RevisionAddress, owned.RevisionAddress);
            foreach (ObjectId id in foreign.Objects.Keys) { Assert.Equal(foreign.Objects[id].Storage, owned.Objects[id].Storage); }
            foreign.RequireStoredSource(first.Store, foreign.SourceModels!); // Hold the real model/view constant: isolate owner lifetime.
            Assert.Throws<InvalidDataException>(() => foreign.RequireStoredSource(second.Store, foreign.SourceModels!));
            Assert.Throws<InvalidDataException>(() => DecodedRevision.ReadStoredGraph(
                second.Store, foreign.SourceSnapshot!, Models().FreezeConfiguration()));
            Assert.Throws<InvalidDataException>(() => second.Store.ReadObjectChains(foreign.SourceSnapshot!).ToArray());
            SchemaCatalogView catalog = SchemaCatalogView.Load(first.Store, first.SharedSchemaRoot!.Value);
            DecodedGraphRows typed = StoredGraphDecoder.Read(first.Store, MechanismFixture.Snapshot(first, foreign.RevisionAddress), foreign.SourceModels!, catalog);
            DecodedRevision syntheticDecoded = typed.ToDecodedRevision();
            Assert.Null(syntheticDecoded.SourceSnapshot);
            NormalizedGraphState synthetic = NormalizedGraphState.Create(syntheticDecoded, foreign.SourceModels!);
            Assert.Null(synthetic.SourceSnapshot);
            Assert.Equal(foreign.RevisionAddress, synthetic.RevisionAddress);
            Assert.Equal(foreign.Objects.Keys.Order(), synthetic.Objects.Keys.Order());
            foreach (ObjectId id in foreign.Objects.Keys) {
                Assert.Equal(foreign.Objects[id].Current.Layout, synthetic.Objects[id].Current.Layout);
                Assert.Equal(foreign.Objects[id].Storage, synthetic.Objects[id].Storage);
                if (foreign.Objects[id].Current.Kind == ObjectStateKind.Durable) {
                    Assert.Equal(foreign.Objects[id].Current.GetState<WidgetState>(), synthetic.Objects[id].Current.GetState<WidgetState>());
                } else {
                    Assert.Equal(foreign.Objects[id].Current.GetListState<int>().Elements.ToArray(), synthetic.Objects[id].Current.GetListState<int>().Elements.ToArray());
                }
            }
            Assert.Throws<InvalidDataException>(() => synthetic.RequireStoredSource(first.Store, foreign.SourceModels!));
            FieldInfo field = typeof(BusinessWorkspace).GetField("_baseline", BindingFlags.Instance | BindingFlags.NonPublic)!;
            field.SetValue(Business(secondSession), foreign); // Both views came from actual readers, not synthetic locators.
            long frames = second.Store.AppendedFrames;
            Assert.Throws<InvalidDataException>(() => secondSession.Prepare(secondSession.State!));
            Assert.Equal(frames, second.Store.AppendedFrames);
            Assert.False(second.IsFaulted);
            Assert.False(second.HasPending);
            Assert.Equal(owned.RevisionAddress, MechanismFixture.FindCurrent(second, "main")?.Address);
            field.SetValue(Business(secondSession), owned);
            using PreparedFixtureSave valid = secondSession.Prepare(secondSession.State!);
            Assert.Empty(valid.Business.Write.Records);
        }
        finally { SharedReadModel.DeleteFixture(copy, Prefix); }
    }

    [Fact]
    public void P0InstallationPreparationFailureKeepsRefsAndBaselineAndPreservesCompleteOrphans() {
        FrameAddress oldState, oldMeta, orphan;
        HeadEntryId branchRevision, sharedRevision;
        HeadId branchId;
        byte[] branchPayload, sharedPayload;
        long before;
        using (RepositoryStorageFacade repository = RepositoryStorageFacade.CreateNew(_path)) {
            BranchSaveSessionFixture session = new(repository, "main", Models());
            DB087Widget root = TwoListRing();
            using (PreparedFixtureSave seed = session.Prepare(root)) { oldState = seed.Commit().Business.Address; }
            oldMeta = repository.SharedSchemaRoot!.Value;
            NormalizedGraphState source = Baseline(session);
            SchemaCatalogWorkspace catalogOwner = repository.CatalogForPreparation;
            SchemaCatalogView accepted = catalogOwner.Accepted!;
            ObjectId childId = source.Objects[session.StateSource!.RootId].Current.GetState<WidgetState>().Segment0Field3;
            branchId = MechanismFixture.Branch(repository, "main");
            var branches = repository.Heads.ReadHead(branchId).Unwrap();
            var shared = repository.Heads.ReadHead(2).Unwrap();
            branchRevision = branches.Id; sharedRevision = shared.Id;
            branchPayload = branches.Payload.ToArray(); sharedPayload = shared.Payload.ToArray();
            before = DataBytes(repository.Data);
            root.Numbers[7] = 9;
            using PreparedFixtureSave save = session.Prepare(root, declarations:
                [new DurableSchema("Unpublished.InstallationFailure", 1, new DurableFieldInfo(1, TypeTag.Int32))]);
            bool reached = false, publicationReached = false;
            repository.SaveStageObserver = point => {
                if (point == RepositorySaveStage.BeforePublication) { publicationReached = true; }
                if (point != RepositorySaveStage.AfterBusinessAppend) { return; }
                reached = true;
                BusinessSaveCandidate sharedCandidate = Assert.IsType<BusinessSaveCandidate>(typeof(PreparedBusinessWrite)
                    .GetField("_candidate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(save.Business));
                NormalizedGraphState candidate = Assert.IsType<NormalizedGraphState>(typeof(BusinessSaveCandidate)
                    .GetField("_next", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sharedCandidate));
                Assert.DoesNotContain(save.Business.Write.Records, row => row.ObjectId == childId.Value);
                Assert.True(MutableRows(candidate).Remove(childId)); // Fail the actual complete-membership installation check.
            };
            RepositoryPublicationException error = Assert.Throws<RepositoryPublicationException>(() => save.Commit());
            Assert.True(reached);
            Assert.False(publicationReached);
            Assert.Equal(RepositoryPublicationOutcome.NotPublished, error.Outcome);
            Assert.IsType<InvalidDataException>(error.InnerException);
            orphan = error.CandidateRevisionAddress!.Value;
            Assert.True(repository.IsFaulted);
            Assert.Same(source, Baseline(session));
            Assert.Same(accepted, catalogOwner.Accepted);
        }
        using RepositoryStorageFacade reopened = RepositoryStorageFacade.OpenExisting(_path);
        var reopenedBranches = reopened.Heads.ReadHead(branchId).Unwrap();
        var reopenedShared = reopened.Heads.ReadHead(2).Unwrap();
        Assert.Equal(branchRevision, reopenedBranches.Id);
        Assert.Equal(sharedRevision, reopenedShared.Id);
        Assert.Equal(branchPayload, reopenedBranches.Payload.ToArray());
        Assert.Equal(sharedPayload, reopenedShared.Payload.ToArray());
        Assert.Equal(oldState, MechanismFixture.FindCurrent(reopened, "main")?.Address);
        Assert.Equal(oldMeta, reopened.SharedSchemaRoot);
        Assert.True(DataBytes(reopened.Data) > before);
        StoredGraphRevision completed = reopened.Store.ReadBusinessGraph(orphan, oldState);
        Assert.Equal(4, completed.ObjectHeads.Count);
        Assert.NotEqual(oldMeta, completed.SchemaRoot);
        SchemaCatalogView orphanMeta = SchemaCatalogView.Load(reopened.Store, completed.SchemaRoot!.Value);
        Assert.Contains(new SchemaKey("Unpublished.InstallationFailure", 1), orphanMeta.Schemas.Keys);
        Assert.Equal(9, Assert.IsType<DB087Widget>(BusinessPersistenceAdapter.ReadRoot(reopened.Store, reopened.Store.ReadBusinessGraph(orphan, oldState), Models())).Numbers[7]);
        Assert.Equal(0, Assert.IsType<DB087Widget>(BusinessPersistenceAdapter.ReadRoot(reopened.Store, MechanismFixture.Snapshot(reopened, oldState), Models())).Numbers[7]);
        BranchSaveSessionFixture continuation = new(reopened, "main", Models());
        DB087Widget rootAgain = Assert.IsType<DB087Widget>(continuation.State);
        rootAgain.Numbers[8] = 9;
        using PreparedFixtureSave next = continuation.Prepare(rootAgain);
        next.Commit();
        AssertAccounting(reopened, continuation);
    }

    [Theory]
    [InlineData((int)RepositorySaveStage.BeforePublication)]
    [InlineData((int)RepositorySaveStage.BeforeInstall)]
    public void RejectedTypedCommitAndDisposeReentryDoesNotReleaseTheOuterCapture(int checkpoint) {
        using RepositoryStorageFacade repository = RepositoryStorageFacade.CreateNew(_path);
        BranchSaveSessionFixture session = new(repository, "main", Models());
        DB087Widget root = TwoListRing();
        using (PreparedFixtureSave seed = session.Prepare(root)) { seed.Commit(); }
        root.Numbers[7] = 9;
        using PreparedFixtureSave save = session.Prepare(root);
        bool reached = false;
        repository.SaveStageObserver = point => {
            if ((int)point != checkpoint) { return; }
            reached = true;
            Assert.Throws<InvalidOperationException>(() => save.Commit());
            Assert.Throws<InvalidOperationException>(() => save.Dispose());
            Assert.Throws<InvalidOperationException>(() => session.Prepare(root));
        };
        RepositorySaveResult result = save.Commit();
        Assert.True(reached);
        Assert.False(repository.IsFaulted);
        Assert.Equal(result.Business.Address, MechanismFixture.FindCurrent(repository, "main")?.Address);
        Assert.Equal(result.Business.Address, Baseline(session).RevisionAddress);
        AssertAccounting(repository, session);
        repository.SaveStageObserver = null;
        using PreparedFixtureSave unchanged = session.Prepare(root);
        Assert.Empty(unchanged.Business.Write.Records);
        Assert.Equal(9, Assert.IsType<DB087Widget>(BusinessPersistenceAdapter.ReadRoot(repository.Store, MechanismFixture.Snapshot(repository, result.Business.Address), Models())).Numbers[7]);
    }

    private static Dictionary<ObjectId, NormalizedObject> MutableRows(NormalizedGraphState baseline) =>
        Assert.IsType<Dictionary<ObjectId, NormalizedObject>>(typeof(NormalizedGraphState)
            .GetField("_objects", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(baseline));

    private static DB087Widget TwoListRing() {
        DB087Widget root = new() { Value = 1, Numbers = Enumerable.Repeat(0, 128).ToList() };
        root.Child = new() { Value = 2, Numbers = Enumerable.Repeat(0, 128).ToList(), Child = root };
        return root;
    }

    private static void CheckTwoListRing(DB087Widget root, int childValue, int seventh) {
        Assert.Equal(1, root.Value);
        Assert.NotNull(root.Child);
        Assert.Equal(childValue, root.Child!.Value);
        Assert.Same(root, root.Child.Child);
        Assert.NotSame(root.Numbers, root.Child.Numbers);
        Assert.Equal(128, root.Numbers.Count);
        Assert.Equal(128, root.Child.Numbers.Count);
        Assert.All(root.Numbers, number => Assert.Equal(0, number));
        for (int i = 0; i < 128; i++) { Assert.Equal(i == 7 ? seventh : 0, root.Child.Numbers[i]); }
    }
}
