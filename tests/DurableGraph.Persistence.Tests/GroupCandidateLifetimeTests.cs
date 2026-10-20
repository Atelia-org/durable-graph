using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;

namespace Atelia.DurableGraph.Persistence.Tests;

// DB-096 cleanup witness using real candidates. No group publication or public request API.
public sealed partial class SplitBusinessWorkspaceTests {
    [Fact]
    public void GroupCleanupAfterSecondCaptureFailureReleasesFirstStagingCandidate() {
        bool fail = true;
        BusinessWorkspace first = new(Registry(Model()).CreateEnvironment());
        BusinessWorkspace second = new(Registry(GroupCaptureModel(() => {
            if (fail) { throw new InvalidOperationException("second capture failed"); }
        })).CreateEnvironment());
        World firstRoot = new() { Value = 7 };
        World secondRoot = new() { Value = 9 };
        long frames = Store.AppendedFrames;
        BusinessSaveCandidate firstCapture = first.Capture(firstRoot, independentSnapshot: false);
        try {
            var error = Assert.Throws<InvalidOperationException>(() => second.Capture(secondRoot, independentSnapshot: false));
            Assert.Equal("second capture failed", error.Message);
            // A group cannot simply Dispose every candidate: the first is still staging.
            Assert.Throws<InvalidOperationException>(() => firstCapture.Dispose());
        } finally {
            firstCapture.AbortPreparation();
        }
        Assert.Null(first.Baseline);
        Assert.Null(second.Baseline);
        Assert.Equal(frames, Store.AppendedFrames);
        fail = false;
        BusinessSaveCandidate retryFirst = first.Capture(firstRoot, independentSnapshot: false);
        try {
            BusinessSaveCandidate retrySecond = second.Capture(secondRoot, independentSnapshot: false);
            try {
                Assert.Equal((byte)7, retryFirst.Graph.Objects.Single(row => row.Id == retryFirst.RootId).GetState<State>().Value);
                Assert.Equal((byte)9, retrySecond.Graph.Objects.Single(row => row.Id == retrySecond.RootId).GetState<State>().Value);
            } finally { retrySecond.AbortPreparation(); }
        } finally { retryFirst.AbortPreparation(); }
    }

    [Fact]
    public void GroupCleanupHandlesReadyEventAndStagingStateWithoutInstallingEventIdentity() {
        StateModelRegistry models = Registry(Model());
        World acceptedRoot = new() { Value = 10 };
        BranchSaveSessionFixture seed = new(_repository, "main", models);
        using (PreparedFixtureSave save = seed.Prepare(acceptedRoot, parameters: NoRebase)) { save.Commit(); }
        BusinessPersistenceAdapter eventMember = BusinessPersistenceAdapter.LoadState(Store, seed.StateSource!, models.FreezeConfiguration());
        BusinessPersistenceAdapter stateMember = new(Store, models);
        IDurableObject acceptedInstance = eventMember.State!;
        NormalizedGraphState baseline = eventMember.Baseline!;
        StoredGraphRevision source = eventMember.Source!;
        SchemaCatalogView catalog = SchemaCatalogView.Load(Store, _repository.SharedSchemaRoot!.Value);
        World eventRoot = new() { Value = 20 };
        World stateRoot = new() { Value = 30 };
        long frames = Store.AppendedFrames;

        BusinessSaveCandidate readyEvent = eventMember.CaptureOwned(eventRoot, StoredGraphKind.Event);
        BusinessSaveCandidate? stagingState = null;
        ObjectId abandonedEventId = readyEvent.RootId;
        try {
            using PreparedBusinessWrite eventWrite = eventMember.PrepareOwned(readyEvent, catalog, NoRebase);
            stagingState = stateMember.CaptureOwned(stateRoot, StoredGraphKind.State);
            readyEvent.RequireReady();
            Assert.Throws<InvalidOperationException>(() => stagingState.RequireReady());
            // This is the proposed group's common exit path, before any publication.
            readyEvent.AbortPreparation();
            stagingState.AbortPreparation();
        } finally {
            readyEvent.AbortPreparation();
            stagingState?.AbortPreparation();
        }

        Assert.Same(acceptedInstance, eventMember.State);
        Assert.Same(baseline, eventMember.Baseline);
        Assert.Same(source, eventMember.Source);
        Assert.Null(stateMember.Baseline);
        Assert.Equal(frames, Store.AppendedFrames);
        BusinessSaveCandidate retryEventAsState = eventMember.CaptureOwned(eventRoot, StoredGraphKind.State);
        try {
            // Aborted Event bindings were never accepted; the same CLR root gets a new ID.
            Assert.True(retryEventAsState.RootId.Value > abandonedEventId.Value);
            using PreparedBusinessWrite retryWrite = eventMember.PrepareOwned(retryEventAsState, catalog, NoRebase);
            Assert.Equal(source.Address, retryWrite.Write.SourceGraph);
            BusinessSaveCandidate retryOther = stateMember.CaptureOwned(stateRoot, StoredGraphKind.State);
            retryOther.AbortPreparation();
        } finally { retryEventAsState.AbortPreparation(); }
    }

    [Fact]
    public void GroupCleanupAfterSuccessfulInstallationDoesNotUndoAcceptedCandidate() {
        StateModelRegistry models = Registry(Model());
        World root = new() { Value = 42 };
        BusinessPersistenceAdapter member = new(Store, models);
        SchemaCatalogWorkspace catalogOwner = new(_repository.EnsureEditingAllowed);
        catalogOwner.Set(Current);
        PreparedSchemaCatalogWrite catalog = catalogOwner.Prepare();
        StoredGraphRevision meta = Store.AppendMeta(null, catalog.Write!);
        catalog.PrepareInstall(meta);
        catalog.Install(meta);
        BusinessSaveCandidate capture = member.CaptureOwned(root, StoredGraphKind.State);
        using PreparedBusinessWrite write = member.PrepareOwned(capture, catalog.View, NoRebase);
        StoredGraphRevision graph = Store.AppendBusiness(StoredGraphKind.State, meta.Address, write.Write);
        write.PrepareInstall(graph);
        write.Install(graph); // Internal installation primitive only; no publication claim.
        NormalizedGraphState baseline = member.Baseline!;
        ObjectId acceptedId = capture.RootId;
        capture.AbortPreparation();
        capture.AbortPreparation();
        Assert.Same(root, member.State);
        Assert.Same(baseline, member.Baseline);
        Assert.Same(graph, member.Source);
        BusinessSaveCandidate next = member.CaptureOwned(root, StoredGraphKind.State);
        try {
            Assert.Equal(acceptedId, next.RootId);
            using PreparedBusinessWrite unchanged = member.PrepareOwned(next, catalog.View, NoRebase);
            Assert.Empty(unchanged.Write.Records);
        } finally { next.AbortPreparation(); }
    }

    private static StateModelBinding GroupCaptureModel(Action onCapture) => new StateModelBinding<World, State>(
        new StatePreparationBinding<State>(Current,
            static (in State state) => Base(state), static (in State prior, in State next) => Delta(prior, next)),
        [new StateReaderBinding<State>(Current,
            static (ref BareValueReader reader) => Read(ref reader),
            static (ref BareValueReader reader, in State prior) => Apply(ref reader, prior), Validate)],
        static row => row.GetState<State>(), static () => new World(),
        static (World world, in State state, ObjectReadTable strings) => {
            world.Value = state.Value;
            world.Text = strings.ResolveString(state.TextId);
            world.Alias = strings.ResolveString(state.AliasId);
            world.Sequence = state.Sequence;
        },
        (world, context) => {
            onCapture();
            return new(world.Value, context.CaptureString(world.Text), context.CaptureString(world.Alias), world.Sequence);
        }, Validate);
}
