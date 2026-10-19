using static Atelia.DurableGraph.Persistence.Tests.DB096TestAccess;
using System.Buffers;
using Atelia.Binary;

using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using Atelia.HeadStore;
using Frames = Atelia.FrameStore.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class SchemaWriteAdmissionTests : IDisposable {
    private const string Prefix = "dg-schema-write-admission-";
    private readonly List<string> _paths = [];
    private static readonly DurableSchema Extra = new("SchemaWriteAdmission.Extra", 1, new DurableFieldInfo(1, TypeTag.Byte));

    [Fact]
    public void WritableCreationNeedsNoOpenModeAndExplicitSharedCreatesStateAndEventBranches() {
        string path = NewPath();
        using (Repository repository = Repository.CreateNew(path, Models())) {
            Assert.False(repository.IsReadOnly);
            PublicationSnapshot before = Snapshot(repository);
            Assert.Empty(before.Frames);
            Assert.Null(ManagementDirectoryPointer.Decode(ManagementDirectoryKind.Branches, before.Branches.Payload));
            Assert.Empty(NamedFrameMapCodec.Decode(before.Shared.Payload[4..]).Unwrap());
            using WorkingCopy main = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", Ring()).Id);
            using WorkingCopy eventOnly = repository.OpenWorkingCopy(repository.CreateRootBranchFromEvent("event-only", Ring()).Id);
            AssertRing(main.State, 7);
            Assert.Null(eventOnly.State);
            Assert.Equal(CommitKind.Event, WorkTestAccess.Kind(eventOnly));
        }
        using Repository reader = Repository.OpenReadOnlyExisting(path, Models());
        AssertRing(reader.LoadState(reader.ReadCurrentCommit(BranchNamed(reader, "main")).Commit.Id), 7);
        AssertRing(reader.LoadEvent(reader.ReadCurrentCommit(BranchNamed(reader, "event-only")).Commit.Id), 7);
    }

    [Fact]
    public void ColdSharedCheckoutSavesDeltaWithoutOpenModeAndPreservesIdentityAndCompletedFrames() {
        string path = NewPath();
        FrameAddress seed, schema;
        ObjectId rootId, listId;
        ObjectStorageInfo originalList;
        FrameImage[] completed;
        using (Repository repository = Repository.CreateNew(path, Models())) {
            using WorkingCopy main = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("main", Ring()).Id);
            repository.CreateTag("initial", main.BaseCommitId);
            seed = Describe(repository, main.BaseCommitId).Address;
            schema = repository.SharedSchemaRoot!.Value;
            rootId = main.StateId!.Value;
            listId = Assert.Single(main.Baseline!.Objects, pair => pair.Value.Current.Kind == ObjectStateKind.List).Key;
            originalList = main.Baseline.Objects[listId].Storage!.Value;
            completed = ReadFrames(ResourcesOf(repository).Data);
        }

        FrameAddress continued;
        using (Repository repository = Repository.OpenExisting(path, Models())) {
            using WorkingCopy main = repository.OpenWorkingCopy(BranchNamed(repository, "main"));
            DB087Widget root = AssertRing(main.State, 7);
            Assert.Equal(rootId, main.StateId);
            Assert.Equal(originalList, main.Baseline!.Objects[listId].Storage);
            HeadEntry sharedBefore = ResourcesOf(repository).Heads.ReadHead(2).Unwrap();
            root.Numbers[7] = 201;
            CommitInfo saved = main.CommitState();
            continued = Describe(repository, saved.Id).Address;
            StoredGraphRevision graph = DB096TestAccess.Snapshot(repository, saved.Id);
            Assert.Equal(seed, ResourcesOf(repository).ReadPrevious(saved.Id.Value)?.Address);
            Assert.Equal(seed, graph.SourceGraph);
            Assert.Equal(schema, graph.SchemaRoot);
            Assert.Equal(rootId, graph.RootId);
            Assert.Equal(3, graph.ObjectHeads.Count);
            ObjectVersionRecord delta = Assert.Single(repository.GraphStore.ReadBatch(graph.OwnedBatch!.Value).Records);
            Assert.Equal(listId.Value, delta.ObjectId);
            Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
            Assert.Equal(originalList.Head, delta.PriorAddress);
            Assert.Equal(originalList.ReconstructionPayloadBytes + delta.EncodedPayloadBytes!.Value,
                main.Baseline!.Objects[listId].Storage!.Value.ReconstructionPayloadBytes);
            AssertSameRef(sharedBefore, ResourcesOf(repository).Heads.ReadHead(2).Unwrap());
            AssertFramesPreserved(ResourcesOf(repository).Data, completed);
        }
        using Repository reader = Repository.OpenReadOnlyExisting(path, Models());
        AssertRing(reader.LoadState(reader.ResolveTag("initial")), 7);
        AssertRing(reader.LoadState(reader.ReadCurrentCommit(BranchNamed(reader, "main")).Commit.Id), 201);
        Assert.Equal(continued, Describe(reader, reader.ReadCurrentCommit(BranchNamed(reader, "main")).Commit.Id).Address);
        AssertFramesPreserved(ResourcesOf(reader).Data, completed);
    }

    [Fact]
    public void ExplicitSharedSessionSupportsOrdinarySaveFormsAndManualIntentWithoutOpenMode() {
        string path = NewPath();
        int captures = 0;
        using (Repository seed = Repository.CreateNew(path, CaptureModels(() => captures++))) {
            using WorkingCopy main = seed.OpenWorkingCopy(seed.CreateRootBranchFromState("main", new CaptureRoot { Value = 17 }).Id);
        }
        Assert.Equal(1, captures);
        captures = 0;
        using Repository repository = Repository.OpenExisting(path, CaptureModels(() => captures++));
        using WorkingCopy checkout = repository.OpenWorkingCopy(BranchNamed(repository, "main"));
        CaptureRoot root = Assert.IsType<CaptureRoot>(checkout.State);
        Assert.Equal(17, root.Value);
        Assert.Equal(0, captures);
        checkout.CommitState(); // An unchanged ordinary save also has a definite destination.
        Assert.Equal(1, captures);
        root.Value = 18;
        checkout.CommitState();
        Assert.Equal(2, captures);
        checkout.CommitState(new CaptureRoot { Value = 19 });
        Assert.Equal(3, captures);
        root = Assert.IsType<CaptureRoot>(checkout.State);
        checkout.CommitEvent(root);
        Assert.Equal(4, captures);
        checkout.PrepareAndDiscard(root);
        Assert.Equal(5, captures);
        using WorkingCopy newState = repository.OpenWorkingCopy(repository.CreateRootBranchFromState("new-state", root).Id);
        Assert.Equal(6, captures);
        using WorkingCopy newEvent = repository.OpenWorkingCopy(repository.CreateRootBranchFromEvent("new-event", root).Id);
        Assert.Equal(7, captures);
        checkout.Catalog.Set(Extra);
        checkout.Catalog.Set(ObjectLayout.ForDurable(Extra));
        Assert.Equal(1, checkout.Catalog.DirtyCount);
        Assert.False(repository.IsFaulted);
    }

    [Fact]
    public void WritableOwnerForkAndTagOperationsPreserveBusinessAndSharedSchema() {
        string path = NewPath();
        FrameAddress initial, current, schema;
        using (Repository seed = Repository.CreateNew(path, Models())) {
            using WorkingCopy main = seed.OpenWorkingCopy(seed.CreateRootBranchFromState("main", Ring()).Id);
            initial = Describe(seed, main.BaseCommitId).Address;
            seed.CreateTag("initial", main.BaseCommitId);
            AssertRing(main.State, 7).Numbers[7] = 201;
            current = Describe(seed, main.CommitState().Id).Address;
            schema = seed.SharedSchemaRoot!.Value;
        }
        using (Repository repository = Repository.OpenExisting(path, Models())) {

            CommitId old = repository.ResolveTag("initial"), latest = repository.ReadCurrentCommit(BranchNamed(repository, "main")).Commit.Id;
            Assert.Equal(initial, Describe(repository, old).Address);
            Assert.Equal(current, Describe(repository, latest).Address);
            PublicationSnapshot before = Snapshot(repository);
            using (WorkingCopy main = repository.OpenWorkingCopy(BranchNamed(repository, "main"))) {
                AssertRing(main.State, 201);
                BranchInfo plain = repository.ForkBranch(old, "plain");
                Assert.Equal(old, plain.ForkedFromCommitId);
                Assert.Equal(old, repository.ReadCurrentCommit(plain.Id).Commit.Id);
                using WorkingCopy fork = repository.OpenWorkingCopy(repository.ForkBranch(old, "fork").Id);
                AssertRing(fork.State, 7);
                Assert.Equal(old, fork.BaseCommitId);
                Assert.NotSame(main.State, fork.State);
                Assert.Equal(current, main.StateRevisionAddress);
            }
            repository.CreateTag("bookmark", latest);
            Assert.Equal(latest, repository.ReadCurrentCommit(BranchNamed(repository, "main")).Commit.Id);
            Assert.Equal(old, repository.ReadCurrentCommit(BranchNamed(repository, "plain")).Commit.Id);
            Assert.Equal(old, repository.ReadCurrentCommit(BranchNamed(repository, "fork")).Commit.Id);
            Assert.Equal(latest, repository.ResolveTag("bookmark"));
            Assert.Equal(schema, repository.SharedSchemaRoot);
            AssertSameRef(before.Shared, ResourcesOf(repository).Heads.ReadHead(2).Unwrap());
            AssertFramesPreserved(ResourcesOf(repository).Data, before.Frames);
            FrameImage[] added = ReadFrames(ResourcesOf(repository).Data)
                .Where(frame => !before.Frames.Any(old => old.Address == frame.Address)).ToArray();
            Assert.NotEmpty(added);
            FrameImage[] addedGraphs = added.Where(frame => frame.Tag == GraphFrameCodec.GraphTag).ToArray();
            Assert.NotEmpty(addedGraphs);
            Assert.All(addedGraphs, frame =>
                Assert.Equal(StoredGraphKind.Meta, repository.GraphStore.ReadMetadata(frame.Address).Kind));
            Assert.True(repository.GraphStore.AppendedFrames > before.Appended);
            Assert.False(repository.IsFaulted);
            Assert.False(ResourcesOf(repository).HasPendingSave);
        }
        using Repository reader = Repository.OpenReadOnlyExisting(path, Models());
        Assert.Equal(current, Describe(reader, reader.ResolveTag("bookmark")).Address);
        AssertRing(reader.LoadState(reader.ReadCurrentCommit(BranchNamed(reader, "plain")).Commit.Id), 7);
        AssertRing(reader.LoadState(reader.ReadCurrentCommit(BranchNamed(reader, "fork")).Commit.Id), 7);
    }

    [Fact]
    public void ReadonlyGuardStillRejectsAllGraphRefAndTagWritesWithoutMutation() {
        string path = NewPath();
        using (Repository seed = Repository.CreateNew(path, Models())) {
            using WorkingCopy main = seed.OpenWorkingCopy(seed.CreateRootBranchFromState("main", Ring()).Id);
        }
        Dictionary<string, byte[]> files = ReadFiles(path);
        using (Repository reader = Repository.OpenReadOnlyExisting(path, Models())) {
            Assert.True(reader.IsReadOnly);

            CommitId head = reader.ReadCurrentCommit(BranchNamed(reader, "main")).Commit.Id;
            AssertRing(reader.LoadState(head), 7);
            PublicationSnapshot before = Snapshot(reader);
            Action[] writes = [
                () => reader.OpenWorkingCopy(BranchNamed(reader, "main")),
                () => reader.OpenWorkingCopy(reader.CreateRootBranchFromState("state", Ring()).Id),
                () => reader.OpenWorkingCopy(reader.CreateRootBranchFromEvent("event", Ring()).Id),
                () => reader.ForkBranch(head, "plain"),
                () => reader.OpenWorkingCopy(reader.ForkBranch(head, "fork").Id),
                () => reader.CreateTag("tag", head),
            ];
            foreach (Action write in writes) {
                Assert.Throws<InvalidOperationException>(write);
                AssertUnchanged(reader, before);
            }
        }
        AssertFilesUnchanged(files, ReadFiles(path));
    }

    private static StateModelRegistry Models() {
        StateModelRegistry models = new();
        models.Register(global::Atelia.DurableGraph.Generated.Family_5030576964676574.Definition);
        models.Register(global::Atelia.DurableGraph.Generated.Family_5030456E76656C6F7065.Definition);
        return models;
    }
    private static DB087Widget Ring() {
        List<int> numbers = Enumerable.Range(0, 128).ToList();
        DB087Widget root = new() { Value = 1, Numbers = numbers };
        root.Child = new() { Value = 2, Numbers = numbers, Child = root };
        return root;
    }
    private static DB087Widget AssertRing(IDurableObject? value, int atSeven) {
        DB087Widget root = Assert.IsType<DB087Widget>(value), child = Assert.IsType<DB087Widget>(root.Child);
        Assert.Equal(1, root.Value);
        Assert.Equal(2, child.Value);
        Assert.Same(root, child.Child);
        Assert.Same(root.Numbers, child.Numbers);
        Assert.Equal(128, root.Numbers.Count);
        for (int index = 0; index < 128; index++) { Assert.Equal(index == 7 ? atSeven : index, root.Numbers[index]); }
        return root;
    }

    private sealed class CaptureRoot : IDurableObject { internal int Value; }
    private static readonly DurableSchema CaptureSchema = new("SchemaWriteAdmission.Capture", 1, new DurableFieldInfo(1, TypeTag.Int32));
    private static StateModelRegistry CaptureModels(Action capture) {
        static void Visit(in int value, IStateReferenceVisitor visitor) { }
        static PreparedBaseBody Base(in int value) {
            ArrayBufferWriter<byte> buffer = new();
            BareValueWriter writer = new(buffer);
            writer.WriteVarInt32(value);
            return new(buffer.WrittenSpan);
        }
        StateModelRegistry models = new();
        models.Register(new StateModelBinding<CaptureRoot, int>(new(CaptureSchema, Base,
            static (in int prior, in int next) => new(prior != next, Base(next).Body)),
            [new StateReaderBinding<int>(CaptureSchema, static (ref BareValueReader input) => input.ReadVarInt32(),
                static (ref BareValueReader input, in int prior) => input.ReadVarInt32(), Visit)],
            static row => row.GetState<int>(), static () => new(),
            static (CaptureRoot domain, in int value, ObjectReadTable objects) => domain.Value = value,
            (domain, context) => { capture(); return domain.Value; }, Visit));
        return models;
    }

    private sealed record FrameImage(FrameAddress Address, uint Tag, int TailMetaLength, bool IsTombstone, byte[] PayloadAndMeta);
    private sealed record PublicationSnapshot(HeadEntry Branches, HeadEntry Shared, FrameImage[] Frames, long Appended);
    private static PublicationSnapshot Snapshot(Repository repository) {
        RepositoryResources resources = ResourcesOf(repository);
        return new(resources.Heads.ReadHead(1).Unwrap(), resources.Heads.ReadHead(2).Unwrap(),
            ReadFrames(resources.Data), repository.GraphStore.AppendedFrames);
    }
    private static void AssertUnchanged(Repository repository, PublicationSnapshot before) {
        RepositoryResources resources = ResourcesOf(repository);
        AssertSameRef(before.Branches, resources.Heads.ReadHead(1).Unwrap());
        AssertSameRef(before.Shared, resources.Heads.ReadHead(2).Unwrap());
        AssertExactFrames(before.Frames, ReadFrames(resources.Data));
        Assert.Equal(before.Appended, repository.GraphStore.AppendedFrames);
        Assert.False(resources.HasPendingSave);
        Assert.False(repository.IsFaulted);
    }
    private static void AssertSameRef(HeadEntry before, HeadEntry after) {
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.Payload.ToArray(), after.Payload.ToArray());
    }
    private static FrameImage[] ReadFrames(Frames data) {
        List<FrameInfo> frames = [];
        data.Inventory(frames.Add).Unwrap();
        return frames.Select(info => {
            using FrameRead read = data.ReadFrame(info.Address).Unwrap();
            return new FrameImage(info.Address, read.Tag, read.TailMetaLength, read.IsTombstone, read.PayloadAndMeta.ToArray());
        }).ToArray();
    }
    private static void AssertExactFrames(FrameImage[] before, FrameImage[] after) {
        Assert.Equal(before.Select(frame => frame.Address), after.Select(frame => frame.Address));
        foreach (FrameImage image in before) { AssertFrame(image, after.Single(frame => frame.Address == image.Address)); }
    }
    private static void AssertFramesPreserved(Frames data, IEnumerable<FrameImage> before) {
        FrameImage[] after = ReadFrames(data);
        foreach (FrameImage image in before) { AssertFrame(image, after.Single(frame => frame.Address == image.Address)); }
    }
    private static void AssertFrame(FrameImage before, FrameImage after) {
        Assert.Equal(before.Tag, after.Tag);
        Assert.Equal(before.TailMetaLength, after.TailMetaLength);
        Assert.Equal(before.IsTombstone, after.IsTombstone);
        Assert.Equal(before.PayloadAndMeta, after.PayloadAndMeta);
    }
    private static Dictionary<string, byte[]> ReadFiles(string path) => Directory.GetFiles(path, "*", SearchOption.AllDirectories)
        .ToDictionary(file => file, File.ReadAllBytes);
    private static void AssertFilesUnchanged(Dictionary<string, byte[]> before, Dictionary<string, byte[]> after) {
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach ((string file, byte[] bytes) in before) { Assert.Equal(bytes, after[file]); }
    }
    private string NewPath() {
        string path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
        _paths.Add(path);
        return path;
    }
    public void Dispose() {
        foreach (string path in _paths) { SharedReadModel.DeleteFixture(path, Prefix); }
    }
}
