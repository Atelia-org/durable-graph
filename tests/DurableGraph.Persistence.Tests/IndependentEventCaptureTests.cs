using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed partial class SplitBusinessWorkspaceTests {
    [Fact]
    public void IndependentEventPreparesEveryBaseWithoutStateQualificationDeltaOrPolicy() {
        int deltaCalls = 0;
        StateModelRegistry models = Registry(IndependentEventModel(() => deltaCalls++));
        BranchSaveSessionFixture session = new(_repository, "main", models);
        World world = new() { Value = 1, Text = new string('s', 12) };
        using (PreparedFixtureSave seed = session.Prepare(world)) seed.Commit();
        NormalizedGraphState baseline = session.Baseline!;
        StoredGraphRevision source = session.StateSource!;
        world.Value = 2;
        deltaCalls = 0;
        long qualifications = Store.BusinessGraphQualificationCount;

        // The internal shared shape still accepts State policy; Event must neither
        // validate this unusable policy nor reach the incremental planner.
        using (PreparedFixtureSave pending = session.Prepare(world, StoredGraphKind.Event, new(0, 0))) {
            Assert.Equal(qualifications, Store.BusinessGraphQualificationCount);
            Assert.Equal(0, deltaCalls);
            Assert.Null(pending.Business.Write.SourceGraph);
            Assert.Empty(pending.Business.Write.RemovedIds);
            Assert.Equal(2, pending.Business.Write.Records.Count);
            Assert.All(pending.Business.Write.Records, row => Assert.Equal(ObjectVersionKind.Base, row.Kind));
            StoredGraphRevision graph = pending.Commit().Business;
            Assert.Null(graph.SourceGraph);
            Assert.Same(baseline, session.Baseline);
            Assert.Same(source, session.StateSource);
            Assert.Equal((byte)1, baseline.CurrentDtos[session.RootId].GetState<State>().Value);
        }

        using PreparedFixtureSave state = session.Prepare(world, parameters: NoRebase);
        Assert.Equal(source.Address, state.Business.Write.SourceGraph);
        Assert.Equal(1, deltaCalls);
        Assert.Equal(ObjectVersionKind.Delta, Assert.Single(state.Business.Write.Records).Kind);
        state.Commit();
        Assert.Equal((byte)2, session.Baseline!.CurrentDtos[session.RootId].GetState<State>().Value);
    }

    [Fact]
    public void CommittedAndDiscardedEventsUseFreshLocalIdsWithoutConsumingStateIds() {
        BranchSaveSessionFixture session = new(_repository, "main", Registry(Model()));
        World state = new() { Value = 1 };
        using (PreparedFixtureSave seed = session.Prepare(state)) seed.Commit();
        Assert.Equal(new ObjectId(1), session.RootId);
        for (int index = 0; index < 3; index++) {
            World domainEvent = new() { Text = new string((char)('a' + index), 12) };
            using PreparedFixtureSave pending = session.Prepare(domainEvent, StoredGraphKind.Event);
            Assert.Equal(new ObjectId(1), pending.Business.Write.RootId);
            Assert.Equal(new uint[] { 1, 2 }, pending.Business.Write.Records.Select(row => row.ObjectId).ToArray());
            if (index != 1) pending.Commit();
            Assert.Same(state, session.State);
        }

        state.Text = new string('z', 12);
        using PreparedFixtureSave nextState = session.Prepare(state, parameters: NoRebase);
        Assert.Equal(new ObjectId(1), nextState.Business.Write.RootId);
        ObjectVersionRecord addedString = Assert.Single(nextState.Business.Write.Records, row => row.Kind == ObjectVersionKind.Base);
        Assert.Equal(2U, addedString.ObjectId);
        nextState.Commit();
        Assert.Equal(new ObjectId(2), session.Baseline!.CurrentDtos[session.RootId].GetState<State>().TextId);
    }

    [Fact]
    public void EventBodyFailurePreservesReentryGuardReleasesCandidateAndDoesNotBurnStateIds() {
        Action? beforeBase = null;
        BranchSaveSessionFixture session = new(_repository, "main",
            Registry(IndependentEventModel(static () => { }, () => beforeBase?.Invoke())));
        World state = new() { Value = 1 };
        using (PreparedFixtureSave seed = session.Prepare(state)) seed.Commit();
        NormalizedGraphState baseline = session.Baseline!;
        long appended = Store.AppendedFrames;
        IOException sentinel = new("independent Event body failure");
        beforeBase = () => {
            Assert.Throws<InvalidOperationException>(() => session.Prepare(new World(), StoredGraphKind.Event));
            throw sentinel;
        };
        World domainEvent = new() { Text = new string('e', 12) };
        Assert.Same(sentinel, Assert.Throws<IOException>(() => session.Prepare(domainEvent, StoredGraphKind.Event)));
        Assert.Same(baseline, session.Baseline);
        Assert.Equal(appended, Store.AppendedFrames);
        Assert.False(_repository.HasPending);
        Assert.False(_repository.IsFaulted);

        beforeBase = null;
        using (PreparedFixtureSave retry = session.Prepare(domainEvent, StoredGraphKind.Event)) {
            Assert.Equal(new uint[] { 1, 2 }, retry.Business.Write.Records.Select(row => row.ObjectId).ToArray());
            retry.Commit();
        }
        state.Text = new string('s', 12);
        using PreparedFixtureSave nextState = session.Prepare(state, parameters: NoRebase);
        Assert.Equal(2U, Assert.Single(nextState.Business.Write.Records, row => row.Kind == ObjectVersionKind.Base).ObjectId);
    }

    private static StateModelBinding IndependentEventModel(Action onDelta, Action? beforeBase = null) {
        StatePreparationBinding<State> preparation = new(Current,
            (in State state) => { beforeBase?.Invoke(); return Base(state); },
            (in State prior, in State next) => { onDelta(); return Delta(prior, next); });
        StateReaderBinding<State> reader = new(Current, static (ref BareValueReader input) => Read(ref input),
            static (ref BareValueReader input, in State prior) => Apply(ref input, prior), Validate);
        return new StateModelBinding<World, State>(preparation, [reader],
            static row => row.GetState<State>(), static () => new World(),
            static (World world, in State state, ObjectReadTable objects) => {
                world.Value = state.Value;
                world.Text = objects.ResolveString(state.TextId);
                world.Alias = objects.ResolveString(state.AliasId);
                world.Sequence = state.Sequence;
            },
            static (world, context) => new(world.Value, context.CaptureString(world.Text),
                context.CaptureString(world.Alias), world.Sequence), Validate);
    }
}
