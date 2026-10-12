using System.Reflection;
using Atelia.DurableGraph.Persistence.MetaBootstrapP0;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using Atelia.VersionStore;
using WidgetState = Atelia.DurableGraph.Generated.Family_5030576964676574.V1;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed partial class P0BusinessPlanningAccountingTests {
    [Fact]
    public void P0NormalizedBaselineInstallsMixedNewAndRetainedObjectHeadsAndColdContinuesDelta() {
        FrameAddress initial, changed;
        ObjectId rootId, oldChildId, rootListId, childListId, newChildId;
        using (P0Repository repository = P0Repository.CreateNew(_path)) {
            P0Session session = new(repository, "main", Models());
            DB087Widget root = TwoListRing();
            using (P0SessionSave save = session.Prepare(root)) { initial = save.Commit().Business.Address; }
            NormalizedRevision source = Baseline(session);
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
            using (P0SessionSave save = session.Prepare(root)) {
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
                P0SaveResult result = save.Commit();
                changed = result.Business.Address;
                NormalizedRevision accepted = Baseline(session);
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
            CheckTwoListRing(Assert.IsType<DB087Widget>(P0BusinessWorkspace.ReadRoot(repository.Store, initial, Models())), 2, 0);
            CheckTwoListRing(root, 3, 9);
        }

        using (P0Repository repository = P0Repository.OpenExisting(_path)) {
            P0Session session = new(repository, "main", Models());
            Assert.Equal(changed, Baseline(session).RevisionAddress);
            AssertAccounting(repository, session);
            DB087Widget root = Assert.IsType<DB087Widget>(session.State);
            CheckTwoListRing(root, 3, 9);
            ObjectStorageInfo prior = Baseline(session).Objects[childListId].Storage!.Value;
            root.Child!.Numbers[8] = 9;
            using P0SessionSave save = session.Prepare(root);
            ObjectVersionRecord delta = Assert.Single(save.Business.Write.Records);
            Assert.Equal(childListId.Value, delta.ObjectId);
            Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
            Assert.Equal(prior.Head, delta.PriorAddress);
            P0SaveResult result = save.Commit();
            Assert.Equal(newChildId, Baseline(session).Objects[rootId].Current.GetState<WidgetState>().Segment0Field3);
            Assert.Equal(prior.ReconstructionPayloadBytes + ObjectVersionPayloadSize.GetDeltaPayloadBytes(delta.Body.Length, prior.Head),
                Baseline(session).Objects[childListId].Storage!.Value.ReconstructionPayloadBytes);
            AssertAccounting(repository, session);
            CheckTwoListRing(Assert.IsType<DB087Widget>(P0BusinessWorkspace.ReadRoot(repository.Store, changed, Models())), 3, 9);
            Assert.Equal(9, Assert.IsType<DB087Widget>(P0BusinessWorkspace.ReadRoot(repository.Store, result.Business.Address, Models())).Child!.Numbers[8]);
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("graphHead")]
    public void P0NormalizedBaselineChecksStorageOfSourceMembersThatWillBeRemoved(string corruption) {
        using P0Repository repository = P0Repository.CreateNew(_path);
        P0Session session = new(repository, "main", Models());
        DB087Widget root = TwoListRing();
        using (P0SessionSave save = session.Prepare(root)) { save.Commit(); }
        NormalizedRevision baseline = Baseline(session);
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
        Assert.Equal(baseline.RevisionAddress, repository.GetHead("main"));
        rows[childId] = child;
        using P0SessionSave valid = session.Prepare(root); // Positive control still removes the complete old members.
        Assert.Contains(childId.Value, valid.Business.Write.RemovedIds);
        valid.Commit();
        Assert.DoesNotContain(childId, Baseline(session).Objects.Keys);
        AssertAccounting(repository, session);
    }

    [Fact]
    public void P0NormalizedBaselineCannotBeBorrowedFromAnotherOwnerWithTheSameRealAddresses() {
        using (P0Repository repository = P0Repository.CreateNew(_path)) {
            P0Session session = new(repository, "main", Models());
            using P0SessionSave seed = session.Prepare(TwoListRing());
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
            using P0Repository first = P0Repository.OpenExisting(_path);
            using P0Repository second = P0Repository.OpenExisting(copy);
            P0Session firstSession = new(first, "main", Models()), secondSession = new(second, "main", Models());
            NormalizedRevision foreign = Baseline(firstSession), owned = Baseline(secondSession);
            Assert.Equal(foreign.RevisionAddress, owned.RevisionAddress);
            foreach (ObjectId id in foreign.Objects.Keys) { Assert.Equal(foreign.Objects[id].Storage, owned.Objects[id].Storage); }
            foreign.RequireSplitSource(first.Store, foreign.SourceModels!); // Hold the real model/view constant: isolate owner lifetime.
            Assert.Throws<InvalidDataException>(() => foreign.RequireSplitSource(second.Store, foreign.SourceModels!));
            P0CatalogView catalog = P0CatalogView.Load(first.Store, first.SharedSchemaRoot!.Value);
            P0DecodedRows typed = P0TypedReader.Read(first.Store, foreign.RevisionAddress, foreign.SourceModels!, catalog);
            NormalizedRevision synthetic = NormalizedRevision.Create(typed.ToDecodedRevision(), foreign.SourceModels!);
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
            Assert.Throws<InvalidDataException>(() => synthetic.RequireSplitSource(first.Store, foreign.SourceModels!));
            FieldInfo field = typeof(BusinessWorkspace).GetField("_baseline", BindingFlags.Instance | BindingFlags.NonPublic)!;
            field.SetValue(Business(secondSession), foreign); // Both views came from actual readers, not synthetic locators.
            long frames = second.Store.AppendedFrames;
            Assert.Throws<InvalidDataException>(() => secondSession.Prepare(secondSession.State!));
            Assert.Equal(frames, second.Store.AppendedFrames);
            Assert.False(second.IsFaulted);
            Assert.False(second.HasPending);
            Assert.Equal(owned.RevisionAddress, second.GetHead("main"));
            field.SetValue(Business(secondSession), owned);
            using P0SessionSave valid = secondSession.Prepare(secondSession.State!);
            Assert.Empty(valid.Business.Write.Records);
        }
        finally { SharedReadModel.DeleteFixture(copy, Prefix); }
    }

    [Fact]
    public void P0InstallationPreparationFailureKeepsRefsAndBaselineAndPreservesCompleteOrphans() {
        FrameAddress oldState, oldMeta, orphan;
        RefRevision branchRevision, sharedRevision;
        byte[] branchPayload, sharedPayload;
        long before;
        using (P0Repository repository = P0Repository.CreateNew(_path)) {
            P0Session session = new(repository, "main", Models());
            DB087Widget root = TwoListRing();
            using (P0SessionSave seed = session.Prepare(root)) { oldState = seed.Commit().Business.Address; }
            oldMeta = repository.SharedSchemaRoot!.Value;
            NormalizedRevision source = Baseline(session);
            P0CatalogView accepted = session.Catalog.Accepted!;
            ObjectId childId = source.Objects[session.StateSource!.RootId].Current.GetState<WidgetState>().Segment0Field3;
            var branches = repository.Versions.ReadRef(1).Unwrap();
            var shared = repository.Versions.ReadRef(2).Unwrap();
            branchRevision = branches.Revision; sharedRevision = shared.Revision;
            branchPayload = branches.Payload.ToArray(); sharedPayload = shared.Payload.ToArray();
            before = DataBytes(repository.Data);
            root.Numbers[7] = 9;
            session.Catalog.Set(new DurableSchema("Unpublished.InstallationFailure", 1, new DurableFieldInfo(1, TypeTag.Int32)));
            using P0SessionSave save = session.Prepare(root);
            bool reached = false, publicationReached = false;
            repository.Checkpoint = point => {
                if (point == P0SaveCheckpoint.BeforePublication) { publicationReached = true; }
                if (point != P0SaveCheckpoint.AfterBusinessAppend) { return; }
                reached = true;
                BusinessSaveCandidate sharedCandidate = Assert.IsType<BusinessSaveCandidate>(typeof(P0PreparedBusiness)
                    .GetField("_candidate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(save.Business));
                NormalizedRevision candidate = Assert.IsType<NormalizedRevision>(typeof(BusinessSaveCandidate)
                    .GetField("_next", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sharedCandidate));
                Assert.DoesNotContain(save.Business.Write.Records, row => row.ObjectId == childId.Value);
                Assert.True(MutableRows(candidate).Remove(childId)); // Fail the actual complete-membership installation check.
            };
            GraphCommitException error = Assert.Throws<GraphCommitException>(() => save.Commit());
            Assert.True(reached);
            Assert.False(publicationReached);
            Assert.Equal(GraphCommitOutcome.NotPublished, error.Outcome);
            Assert.IsType<InvalidDataException>(error.InnerException);
            orphan = error.CandidateRevisionAddress!.Value;
            Assert.True(repository.IsFaulted);
            Assert.Same(source, Baseline(session));
            Assert.Same(accepted, session.Catalog.Accepted);
            Assert.Equal(1, session.Catalog.DirtyCount);
        }
        using P0Repository reopened = P0Repository.OpenExisting(_path);
        var reopenedBranches = reopened.Versions.ReadRef(1).Unwrap();
        var reopenedShared = reopened.Versions.ReadRef(2).Unwrap();
        Assert.Equal(branchRevision, reopenedBranches.Revision);
        Assert.Equal(sharedRevision, reopenedShared.Revision);
        Assert.Equal(branchPayload, reopenedBranches.Payload.ToArray());
        Assert.Equal(sharedPayload, reopenedShared.Payload.ToArray());
        Assert.Equal(oldState, reopened.GetHead("main"));
        Assert.Equal(oldMeta, reopened.SharedSchemaRoot);
        Assert.True(DataBytes(reopened.Data) > before);
        P0GraphSnapshot completed = reopened.Store.ReadGraph(orphan);
        Assert.Equal(4, completed.Objects.Count);
        Assert.NotEqual(oldMeta, completed.SchemaRoot);
        P0CatalogView orphanMeta = P0CatalogView.Load(reopened.Store, completed.SchemaRoot!.Value);
        Assert.Contains(new SchemaKey("Unpublished.InstallationFailure", 1), orphanMeta.Schemas.Keys);
        Assert.Equal(9, Assert.IsType<DB087Widget>(P0BusinessWorkspace.ReadRoot(reopened.Store, orphan, Models())).Numbers[7]);
        Assert.Equal(0, Assert.IsType<DB087Widget>(P0BusinessWorkspace.ReadRoot(reopened.Store, oldState, Models())).Numbers[7]);
        P0Session continuation = new(reopened, "main", Models());
        DB087Widget rootAgain = Assert.IsType<DB087Widget>(continuation.State);
        rootAgain.Numbers[8] = 9;
        using P0SessionSave next = continuation.Prepare(rootAgain);
        next.Commit();
        AssertAccounting(reopened, continuation);
    }

    [Theory]
    [InlineData((int)P0SaveCheckpoint.BeforePublication)]
    [InlineData((int)P0SaveCheckpoint.BeforeInstall)]
    public void RejectedTypedCommitAndDisposeReentryDoesNotReleaseTheOuterCapture(int checkpoint) {
        using P0Repository repository = P0Repository.CreateNew(_path);
        P0Session session = new(repository, "main", Models());
        DB087Widget root = TwoListRing();
        using (P0SessionSave seed = session.Prepare(root)) { seed.Commit(); }
        root.Numbers[7] = 9;
        using P0SessionSave save = session.Prepare(root);
        bool reached = false;
        repository.Checkpoint = point => {
            if ((int)point != checkpoint) { return; }
            reached = true;
            Assert.Throws<InvalidOperationException>(() => save.Commit());
            Assert.Throws<InvalidOperationException>(() => save.Dispose());
            Assert.Throws<InvalidOperationException>(() => session.Prepare(root));
        };
        P0SaveResult result = save.Commit();
        Assert.True(reached);
        Assert.False(repository.IsFaulted);
        Assert.Equal(result.Business.Address, repository.GetHead("main"));
        Assert.Equal(result.Business.Address, Baseline(session).RevisionAddress);
        AssertAccounting(repository, session);
        repository.Checkpoint = null;
        using P0SessionSave unchanged = session.Prepare(root);
        Assert.Empty(unchanged.Business.Write.Records);
        Assert.Equal(9, Assert.IsType<DB087Widget>(P0BusinessWorkspace.ReadRoot(repository.Store, result.Business.Address, Models())).Numbers[7]);
    }

    private static Dictionary<ObjectId, NormalizedObject> MutableRows(NormalizedRevision baseline) =>
        Assert.IsType<Dictionary<ObjectId, NormalizedObject>>(typeof(NormalizedRevision)
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
