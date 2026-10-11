using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Fact]
    public void GeneratedSchemasRemainDurableAfterDefiniteStateAppendFailure() {
        GeneratorTestRun run = RunGenerator(PreparedRevisionSource);
        AssertSchemaOnlyCompiles(run);
        Type host = EmitAndLoad(run.OutputCompilation).GetType("PreparedRevisionWitness.Host")!;
        var capture = host.GetMethod("Capture")!.CreateDelegate<PreparedRevisionCapture>();
        var session = (CaptureSession)host.GetField("Session")!.GetValue(null)!;
        CapturedGraph candidate = capture(1);
        PreparedCapturedGraph input = session.Prepare(candidate);

        using RawBaseDirectory directory = new();
        using (GraphResources segments = GraphResources.CreateNew(directory.Path))
        {
            SchemaStore schemas = segments.Schemas;
            using StateRevisionStore store = new(segments.Data);
            var stateTail = CountRevisionFrames(segments);
            long emptySchemaTail = CountDataFrames(segments);
            var plan = CapturedRevisionPlanner.PrepareRevision(store, schemas, null, input, new(100, 100));
            Assert.Equal(3, schemas.Count); // Owner, Tag and Tag's exact ancestor.
            Assert.True(CountDataFrames(segments) > emptySchemaTail);
            store.Dispose(); // Schema publication has succeeded; the State facade now rejects append definitely.
            Assert.Throws<ObjectDisposedException>(() => store.Append(plan.Revision));
            Assert.Equal(stateTail, CountRevisionFrames(segments));
            Assert.Null(session.Current);
            Assert.Throws<InvalidOperationException>(() => session.BeginCapture());
            session.Discard(candidate);
        }
        using GraphResources reopenedResources = GraphResources.OpenReadOnlyExisting(directory.Path);
        SchemaStore reopened = reopenedResources.Schemas;
        Assert.Equal(3, reopened.Count);
        foreach (var row in input.Objects.Where(item => item.Current.Kind == ObjectStateKind.Durable)) {
            DurableSchema expected = row.Current.Schema!;
            Assert.Equal(expected, reopened.GetRequired(expected.SchemaId, expected.Version));
            if (expected.BaseSchema is { } ancestor) {
                Assert.Equal(ancestor, reopened.GetRequired(ancestor.SchemaId, ancestor.Version));
            }
        }
        Assert.Null(session.Current);
    }

    [Fact]
    public void GeneratedSchemaConflictRejectsEntireBatchBeforeStateAppendOrCandidateInstall() {
        GeneratorTestRun run = RunGenerator(PreparedRevisionSource);
        AssertSchemaOnlyCompiles(run);
        Type host = EmitAndLoad(run.OutputCompilation).GetType("PreparedRevisionWitness.Host")!;
        var capture = host.GetMethod("Capture")!.CreateDelegate<PreparedRevisionCapture>();
        var session = (CaptureSession)host.GetField("Session")!.GetValue(null)!;
        CapturedGraph candidate = capture(1); // Two concrete types, one with an exact ancestor.
        PreparedCapturedGraph input = session.Prepare(candidate);
        DurableSchema tag = input.Objects.Single(item => item.Current.Id == candidate.RootIds[3]).Current.Schema!;

        using RawBaseDirectory directory = new();
        using GraphResources segments = GraphResources.CreateNew(directory.Path);
        SchemaStore schemas = segments.Schemas;
        schemas.RegisterBatch([new DurableSchema(tag.SchemaId, tag.Version, [], tag.BaseSchema)]);
        long schemaTail = CountDataFrames(segments);
        int registered = schemas.Count;
        using StateRevisionStore store = new(segments.Data);
        var before = CountRevisionFrames(segments);

        Assert.Throws<SchemaConflictException>(() => {
            var plan = CapturedRevisionPlanner.PrepareRevision(store, schemas, null, input, new(100, 100));
            store.Append(plan.Revision);
            session.Accept(candidate);
        });
        Assert.Equal(before, CountRevisionFrames(segments));
        Assert.Equal(schemaTail, CountDataFrames(segments));
        Assert.Equal(registered, schemas.Count);
        Assert.Null(session.Current);
        Assert.Throws<InvalidOperationException>(() => session.BeginCapture());
        Assert.Throws<SchemaNotFoundException>(() => schemas.GetRequired("prepared-revision.owner", 1));
        // Rejection leaves the pending frozen candidate usable and explicitly discardable.
        Assert.Same(candidate, session.Prepare(candidate).Candidate);
        session.Discard(candidate);
    }

    [Theory]
    [InlineData("new-with-parent")]
    [InlineData("existing-without-parent")]
    [InlineData("membership")]
    [InlineData("kind")]
    [InlineData("schema")]
    public void GeneratedPreparationRejectsWrongParentBeforeRegistrationAndPreservesAcceptedBaseline(string mismatch) {
        GeneratorTestRun run = RunGenerator(PreparedRevisionSource);
        AssertSchemaOnlyCompiles(run);
        Type host = EmitAndLoad(run.OutputCompilation).GetType("PreparedRevisionWitness.Host")!;
        var capture = host.GetMethod("Capture")!.CreateDelegate<PreparedRevisionCapture>();
        var session = (CaptureSession)host.GetField("Session")!.GetValue(null)!;
        CapturedGraph first = capture(0);
        PreparedCapturedGraph initial = session.Prepare(first);
        ObjectId ownerId = first.RootIds[0];

        using RawBaseDirectory directory = new();
        using GraphResources segments = GraphResources.CreateNew(directory.Path);
        SchemaStore schemas = segments.Schemas;
        using StateRevisionStore store = new(segments.Data);
        var firstPlan = CapturedRevisionPlanner.PrepareRevision(store, schemas, null, initial, new(100, 100));
        FrameAddress parent = store.Append(firstPlan.Revision);
        session.Accept(first);
        CapturedGraph candidate = capture(0);
        PreparedCapturedGraph input = session.Prepare(candidate);
        Assert.False(input.Objects.Single(item => item.Current.Id == ownerId).DeltaBody!.HasChanges);

        FrameAddress? selectedParent = parent;
        switch (mismatch) {
            case "new-with-parent":
                input = initial;
                break;
            case "existing-without-parent":
                selectedParent = null;
                break;
            case "membership":
                selectedParent = store.Append(StateRevision.CreateObjectHeadMapDelta(parent, [],
                    [first.Objects.First(item => item.Kind == ObjectStateKind.String).Id.Value]));
                break;
            case "kind":
                var text = BaseObjectBodyCodec.EncodeString(StringPayloadCodec.PrepareBase("x"));
                selectedParent = store.Append(StateRevision.CreateObjectHeadMapDelta(parent,
                    [ObjectVersionRecord.CreateBase(ownerId.Value, text.Body)], []));
                break;
            case "schema":
                var owner = initial.Objects.Single(item => item.Current.Id == ownerId);
                var original = owner.Current.Schema!;
                var versionTwo = new DurableSchema(original.SchemaId, 2, original.Fields.ToArray(), original.BaseSchema);
                schemas.RegisterBatch([versionTwo]);
                var differentType = BaseObjectBodyCodec.Encode(schemas.RegisterRepresentations([ObjectLayout.ForDurable(versionTwo)])[0], owner.BaseBody);
                selectedParent = store.Append(StateRevision.CreateObjectHeadMapDelta(parent,
                    [ObjectVersionRecord.CreateBase(ownerId.Value, differentType.Body)], []));
                break;
        }
        long schemaTail = CountDataFrames(segments);
        var stateTail = CountRevisionFrames(segments);
        Exception error = Assert.ThrowsAny<Exception>(() => {
            var plan = CapturedRevisionPlanner.PrepareRevision(store, schemas, selectedParent, input, new(1, 100));
            store.Append(plan.Revision);
            session.Accept(candidate);
        });
        if (mismatch is "kind" or "schema") Assert.IsType<InvalidDataException>(error);
        else Assert.IsType<ArgumentException>(error);
        Assert.Equal(stateTail, CountRevisionFrames(segments));
        Assert.Equal(schemaTail, CountDataFrames(segments));
        Assert.Same(first, session.Current);
        Assert.Throws<InvalidOperationException>(() => session.BeginCapture());
        Assert.Same(candidate, session.Prepare(candidate).Candidate);

        // A valid retry against the exact original parent has no writes, including
        // no redundant registration, and still does not install the candidate.
        var retry = CapturedRevisionPlanner.PrepareRevision(store, schemas, parent, session.Prepare(candidate), new(100, 100));
        Assert.Empty(retry.Revision.LocalObjects);
        Assert.Equal(schemaTail, CountDataFrames(segments));
        Assert.Same(first, session.Current);
        session.Discard(candidate);
    }

    private static long CountRevisionFrames(GraphResources resources) {
        long revisionFrames = 0;
        resources.Data.Inventory(info => {
            if (info.Tag == StateRevisionStore.FrameTag) { revisionFrames++; }
        }).Unwrap();
        return revisionFrames;
    }

    private static long CountDataFrames(GraphResources resources) => resources.Data.Inventory(_ => { }).Unwrap();
}
