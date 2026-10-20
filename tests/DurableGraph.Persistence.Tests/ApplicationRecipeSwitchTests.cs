using System.Buffers;
using ApplicationRecipes;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class ApplicationRecipeSwitchTests : IDisposable {
    private const string Prefix = "dg-application-selection-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
    private static readonly DurableSchema RootSchema = new("ApplicationSelectionRoot", 1,
        new DurableFieldInfo(1, TypeTag.Int32));

    [Fact]
    public void Failed_open_retains_old_edits_and_retries_the_same_created_branch_after_old_line_advances() {
        RestoreControl control = new();
        using Repository repository = Repository.CreateNew(_path, Models(control));
        BranchId main = repository.CreateRootBranchFromState("main", new Root { Value = 1 }).Id;
        using BranchSelection selection = new(repository, main);
        WorkingCopy previous = selection.Current;
        CommitId source = previous.BaseCommitId;
        ((Root)previous.State!).Value = 9; // This has not been saved and is not part of the fork.
        BranchInfo created = selection.Fork(source, "alternate");
        Assert.Equal(created.Id, selection.PendingBranchId);
        Assert.Equal(2, repository.ListBranches().Length);

        control.FailAllocate = true;
        InvalidDataException failure = Assert.Throws<InvalidDataException>(() => selection.OpenPending());
        Assert.Equal("Temporary application allocator failure.", failure.Message);
        Assert.Same(previous, selection.Current);
        Assert.Equal(9, Assert.IsType<Root>(selection.Current.State).Value);
        Assert.Equal(created.Id, selection.PendingBranchId);
        Assert.False(repository.IsFaulted);
        Assert.Throws<InvalidOperationException>(() => selection.Fork(source, "duplicate attempt"));
        Assert.Equal(2, repository.ListBranches().Length);

        CommitId parentProgress = previous.CommitState().Id;
        Assert.NotEqual(source, parentProgress);
        Assert.Equal(source, repository.ReadCurrentCommit(created.Id).Commit.Id);
        Assert.Equal(created.Id, selection.PendingBranchId);

        control.FailAllocate = false;
        WorkingCopy opened = selection.OpenPending();
        Assert.Same(opened, selection.Current);
        Assert.Null(selection.PendingBranchId);
        Assert.Equal(created.Id, opened.BranchId);
        Assert.Equal(source, opened.BaseCommitId);
        Assert.Equal(1, Assert.IsType<Root>(opened.State).Value);
        Assert.Throws<ObjectDisposedException>(() => previous.CommitState());
        Assert.Throws<InvalidOperationException>(() => selection.OpenPending());
        Assert.Equal(2, repository.ListBranches().Length);

        ((Root)opened.State!).Value = 2;
        CommitId childProgress = opened.CommitState().Id;
        Assert.Equal(created.Id, repository.ReadCommitInfo(childProgress).CreatedOnBranchId);
        Assert.Equal(parentProgress, repository.ReadCurrentCommit(main).Commit.Id);
        using WorkingCopy mainAgain = repository.OpenWorkingCopy(main);
        Assert.Equal(9, Assert.IsType<Root>(mainAgain.State).Value);
    }

    [Fact]
    public void Faulted_owner_preserves_selection_evidence_but_neither_old_copy_nor_helper_can_continue() {
        using Repository repository = Repository.CreateNew(_path, Models(new()));
        BranchId main = repository.CreateRootBranchFromState("main", new Root { Value = 1 }).Id;
        using BranchSelection selection = new(repository, main);
        WorkingCopy previous = selection.Current;
        CommitId source = previous.BaseCommitId;
        BranchInfo created = selection.Fork(source, "alternate");
        DB096TestAccess.Resources(repository).MarkFaulted();

        Assert.ThrowsAny<InvalidOperationException>(() => previous.CommitState());
        Assert.ThrowsAny<InvalidOperationException>(() => selection.OpenPending());
        Assert.Throws<InvalidOperationException>(() => selection.Fork(source, "duplicate attempt"));
        Assert.True(repository.IsFaulted);
        Assert.Same(previous, selection.Current);
        Assert.Equal(created.Id, selection.PendingBranchId);

        selection.Dispose();
        repository.Dispose();
        using Repository reopened = Repository.OpenExisting(_path, Models(new()));
        Assert.Equal(2, reopened.ListBranches().Length);
        using BranchSelection resumed = new(reopened, created.Id);
        Assert.Equal(source, resumed.Current.BaseCommitId);
        Assert.Equal(1, Assert.IsType<Root>(resumed.Current.State).Value);
    }

    [Fact]
    public void Reentrant_disposal_during_open_does_not_release_old_occupancy_or_corrupt_the_outer_switch() {
        RestoreControl control = new();
        using Repository repository = Repository.CreateNew(_path, Models(control));
        BranchId main = repository.CreateRootBranchFromState("main", new Root { Value = 1 }).Id;
        using BranchSelection selection = new(repository, main);
        WorkingCopy previous = selection.Current;
        BranchInfo created = selection.Fork(previous.BaseCommitId, "alternate");
        int calls = 0;
        control.OnAllocate = () => {
            calls++;
            Assert.Throws<InvalidOperationException>(selection.Dispose);
            Assert.Throws<InvalidOperationException>(() => selection.OpenPending());
            Assert.Same(previous, selection.Current);
            Assert.Equal(created.Id, selection.PendingBranchId);
        };
        WorkingCopy next = selection.OpenPending();
        Assert.Equal(1, calls);
        control.OnAllocate = null;
        Assert.Equal(created.Id, next.BranchId);
        Assert.Null(selection.PendingBranchId);
        Assert.False(repository.IsFaulted);
        Assert.Throws<ObjectDisposedException>(() => previous.CommitState());
        using WorkingCopy released = repository.OpenWorkingCopy(main);
        next.CommitState();
        released.CommitState();
    }

    private static StateModelRegistry Models(RestoreControl control) {
        StateModelRegistry models = new();
        models.Register(new StateModelBinding<Root, int>(new(RootSchema, BaseInt,
            static (in int prior, in int current) => new(prior != current, BaseInt(in current).Body)),
            [new StateReaderBinding<int>(RootSchema,
                static (ref BareValueReader reader) => reader.ReadVarInt32(),
                static (ref BareValueReader reader, in int _) => reader.ReadVarInt32(),
                static (in int _, IStateReferenceVisitor _) => { })],
            static row => row.GetState<int>(),
            () => {
                control.OnAllocate?.Invoke();
                if (control.FailAllocate) { throw new InvalidDataException("Temporary application allocator failure."); }
                return new();
            },
            static (Root root, in int state, ObjectReadTable _) => root.Value = state,
            static (root, _) => root.Value, static (in int _, IStateReferenceVisitor _) => { }));
        return models;
    }

    private static PreparedBaseBody BaseInt(in int value) {
        ArrayBufferWriter<byte> bytes = new();
        BareValueWriter writer = new(bytes);
        writer.WriteVarInt32(value);
        return new(bytes.WrittenSpan);
    }

    private sealed class Root : IDurableObject { internal int Value; }
    private sealed class RestoreControl {
        internal bool FailAllocate;
        internal Action? OnAllocate;
    }

    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}
