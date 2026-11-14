
using Atelia.DurableGraph.Storage;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed partial class EventHistoryRepositoryTests {
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CompleteStateBasesDoNotRequireEditingParentWhileEventsRemainIndependent(bool state, bool orphan) {
        FrameAddress first;
        ObjectId root;
        using (Repository seed = CreateRepository()) {
            BranchInfo main = seed.CreateRootBranchFromState("main", new Node { Value = 1 }, parameters: NoRebase);
            HistoryGraphRecord current = DB096TestAccess.Describe(seed, seed.ReadCurrentCommit(main.Id).Commit.Id);
            first = current.Address;
            root = current.RootId;
        }
        using (RepositoryResources resources = RepositoryResources.OpenExisting(_root)) {
            // A complete State is self-contained; Event batch and directory must remain independent.
            GraphRecordStore store = resources.GraphStore;
            FrameAddress invalid = AppendWithCreator(resources, state ? null : first, root,
                state ? StoredGraphKind.State : StoredGraphKind.Event, StorageTestData.LocalRecords(store, first));
            if (!orphan) { resources.Heads.PublishHead(resources.ListBranches().Single().Key, BusinessEntryPayloadCodec.Encode(invalid, default), out _).Unwrap(); }
            else { resources.Data.ConfirmDurable(); }
        }
        var before = SnapshotFiles();
        using (Repository reader = Repository.OpenReadOnlyExisting(_root, Models())) {
            var head = reader.ReadCurrentCommit(DB096TestAccess.BranchNamed(reader, "main")).Commit;
            if (orphan) { Assert.Equal(first, DB096TestAccess.Describe(reader, head.Id).Address); Assert.NotNull(reader.LoadState(head.Id)); }
            else if (state) {
                Assert.NotNull(reader.ReadCheckpoint(head.Id));
                Assert.Equal(1, Assert.IsType<Node>(reader.LoadState(head.Id)).Value);
            } else {
                Assert.Throws<InvalidDataException>(() => reader.ReadCheckpoint(head.Id));
                Assert.Throws<InvalidDataException>(() => reader.LoadEvent(head.Id));
                Assert.False(reader.IsFaulted);
            }
        }
        AssertFiles(before);
        if (!orphan) {
            using Repository writer = Repository.OpenExisting(_root, Models());
            var head = writer.ReadCurrentCommit(DB096TestAccess.BranchNamed(writer, "main")).Commit;
            BranchInfo child = writer.ForkBranch(head.Id, "selected-fork");
            if (state) {
                using WorkingCopy main = writer.OpenWorkingCopy(DB096TestAccess.BranchNamed(writer, "main"));
                using WorkingCopy fork = writer.OpenWorkingCopy(child.Id);
                Assert.Equal(1, Assert.IsType<Node>(main.State).Value);
                Assert.Equal(1, Assert.IsType<Node>(fork.State).Value);
            } else {
                Assert.Throws<InvalidDataException>(() => writer.OpenWorkingCopy(DB096TestAccess.BranchNamed(writer, "main")));
                Assert.Throws<InvalidDataException>(() => writer.OpenWorkingCopy(child.Id));
            }
            Assert.False(writer.IsFaulted);
            Assert.Contains(writer.ListBranches(), branch => branch.Id == child.Id);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StateBaseCreatorIsNonAuthoritativeWhileEventSourcesRemainForbidden(bool state) {
        FrameAddress first;
        ObjectId root;
        using (Repository seed = CreateRepository()) {
            BranchInfo main = seed.CreateRootBranchFromEvent("main", new Node { Value = 1 });
            HistoryGraphRecord current = DB096TestAccess.Describe(seed, seed.ReadCurrentCommit(main.Id).Commit.Id);
            first = current.Address;
            root = current.RootId;
        }
        using (RepositoryResources resources = RepositoryResources.OpenExisting(_root)) {
            GraphRecordStore store = resources.GraphStore;
            FrameAddress invalid = AppendWithCreator(resources, first, root, state ? StoredGraphKind.State : StoredGraphKind.Event,
                StorageTestData.LocalRecords(store, first));
            resources.Heads.PublishHead(resources.ListBranches().Single().Key, BusinessEntryPayloadCodec.Encode(invalid, default), out _).Unwrap();
        }
        using (Repository reader = Repository.OpenReadOnlyExisting(_root, Models())) {
            CommitInfo head = reader.ReadCurrentCommit(DB096TestAccess.BranchNamed(reader, "main")).Commit; // Metadata navigation does not replay the directory.
            Assert.Equal(state ? CommitKind.State : CommitKind.Event, head.Kind);
            if (state) { Assert.Equal(1, Assert.IsType<Node>(reader.LoadState(head.Id)).Value); }
            else { Assert.Throws<InvalidDataException>(() => reader.LoadEvent(head.Id)); }
            Assert.False(reader.IsFaulted);
        }
        using Repository writer = Repository.OpenExisting(_root, Models());
        CommitInfo selected = writer.ReadCurrentCommit(DB096TestAccess.BranchNamed(writer, "main")).Commit;
        BranchInfo child = writer.ForkBranch(selected.Id, "selected-fork");
        if (state) {
            using WorkingCopy main = writer.OpenWorkingCopy(DB096TestAccess.BranchNamed(writer, "main"));
            using WorkingCopy fork = writer.OpenWorkingCopy(child.Id);
            Assert.Equal(1, Assert.IsType<Node>(main.State).Value);
            Assert.Equal(1, Assert.IsType<Node>(fork.State).Value);
        } else {
            Assert.Throws<InvalidDataException>(() => writer.OpenWorkingCopy(DB096TestAccess.BranchNamed(writer, "main")));
            Assert.Throws<InvalidDataException>(() => writer.OpenWorkingCopy(child.Id));
        }
        Assert.False(writer.IsFaulted);
        Assert.Equal(new[] { "main", "selected-fork" }, writer.ListBranches().Select(branch => branch.DisplayLabel).Order());
    }

    private static FrameAddress AppendWithCreator(RepositoryResources resources, FrameAddress? source, ObjectId root, StoredGraphKind kind, IReadOnlyList<ObjectVersionRecord> records) {
        FrameAddress? schema = resources.SharedSchemaRoot;
        FrameAddress batch = resources.Data.Append(GraphFrameCodec.BatchTag, GraphFrameCodec.EncodeBatch(source, schema, records)).Unwrap();
        var heads = records.ToDictionary(row => row.ObjectId, _ => batch);
        FrameAddress directory = resources.Data.Append(GraphFrameCodec.DirectoryTag,
            GraphFrameCodec.EncodeDirectory(source, ObjectVersionKind.Base, null, ObjectHeadDirectoryCodec.PrepareBase(heads).Body)).Unwrap();
        return resources.Data.Append(GraphFrameCodec.GraphTag, GraphFrameCodec.EncodeGraph(kind, null, root, directory, schema, batch)).Unwrap();
    }
}
