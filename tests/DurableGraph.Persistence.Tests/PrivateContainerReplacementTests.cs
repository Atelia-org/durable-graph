using System.Buffers;
using Atelia.DurableGraph.Persistence.MetaBootstrapP0;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.VersionStore;
using Frames = Atelia.FrameStore.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

// Actual generated consumers exercise a removed dynamic layout after Capture.
[DurableType("Replacement.ListRoot", 1)]
internal sealed partial class DB087ReplacementListRoot : IDurableObject {
    [DurableField(1)] internal List<DB087Widget> Items = [];
}

public sealed class PrivateContainerReplacementTests : IDisposable {
    private const string Prefix = "dg-private-container-replace-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
    private static readonly ReadAmplificationBaseBudgetParameters NoRebase = new(1_000_000, 1);
    private static readonly DurableSchema PointA = new("Replacement.Point", 1, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.Int32));
    private static readonly DurableSchema PointB = new(PointA.Type, 1, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.Int64));
    private static readonly ObjectLayout ListA = List(PointA);
    private static readonly ObjectLayout ArrayA = Array(PointA);
    private static readonly ObjectLayout DictionaryA = Dictionary(PointA);
    private static readonly ObjectLayout FixedList = ObjectLayout.ForList(new(new(1, TypeTag.Int32)));

    [Fact]
    public void Noop_reuses_the_pin_and_natural_set_replacement_reuses_destinations_converges_and_removes_dynamic_declarations() {
        FrameAddress sourcePin, sourceHead;
        ObjectId listId, arrayId, dictionaryId, pointId;
        RefImage shared;
        FrameImage[] complete;
        DurableSchema newPoint = new("Replacement.NewPoint", 1, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.Byte));
        ObjectLayout newList = List(newPoint);
        using (Repository repository = Repository.CreateNew(_path, CounterModels(), schemaWriteMode: SchemaWriteMode.Shared)) {
            using BranchCheckout main = Seed(repository);
            P0CatalogView source = main.Catalog.Accepted!;
            sourcePin = source.Snapshot!.Address;
            sourceHead = main.Head.RevisionAddress;
            listId = source.DescriptorId(ListA);
            arrayId = source.DescriptorId(ArrayA);
            dictionaryId = source.DescriptorId(DictionaryA);
            pointId = source.SchemaNodeId(PointA);
            shared = ReadRef(repository, 2);
            complete = ReadFrames(ResourcesOf(repository).Data);
            using BranchCheckout noop = repository.CreatePrivateBranch("noop", new Counter { Value = 2 }, [], NoRebase,
                containerReplacements: [(ListA, ListA)]);
            Assert.Equal(sourcePin, noop.Catalog.Accepted!.Snapshot!.Address);
            Assert.Equal(source.DescriptorIds.ToArray(), noop.Catalog.Accepted.DescriptorIds.ToArray());
            AssertFirstBase(repository, noop);
            using BranchCheckout merged = repository.CreatePrivateBranch("merged", new Counter { Value = 2 }, [], NoRebase,
                containerReplacements: [(ArrayA, ListA), (DictionaryA, ListA)]);
            P0CatalogView merge = merged.Catalog.Accepted!;
            Assert.Equal(sourcePin, merge.Snapshot!.SourceGraph);
            Assert.Equal(listId, merge.DescriptorId(ListA));
            Assert.Equal(pointId, merge.SchemaNodeId(PointA));
            Assert.Equal([ListA], Containers(merge));
            AssertRemoved(merge, ArrayA, arrayId);
            AssertRemoved(merge, DictionaryA, dictionaryId);
            AssertRetainedEmployeeHeads(source, merge);
            AssertFirstBase(repository, merged);
            using BranchCheckout fixedBranch = repository.CreatePrivateBranch("fixed", new Counter { Value = 2 }, [], NoRebase,
                containerReplacements: [(ListA, FixedList), (ArrayA, FixedList), (DictionaryA, FixedList)]);
            P0CatalogView fixedView = fixedBranch.Catalog.Accepted!;
            Assert.Empty(Containers(fixedView));
            fixedView.Require(FixedList); // Fixed ABI layouts do not acquire a dynamic descriptor.
            Assert.Throws<InvalidDataException>(() => fixedView.DescriptorId(FixedList));
            AssertRemoved(fixedView, ListA, listId);
            AssertRemoved(fixedView, ArrayA, arrayId);
            AssertRemoved(fixedView, DictionaryA, dictionaryId);
            Assert.Equal(pointId, fixedView.SchemaNodeId(PointA));
            AssertRetainedEmployeeHeads(source, fixedView);
            AssertFirstBase(repository, fixedBranch);
            using BranchCheckout added = repository.CreatePrivateBranch("new-key", new Counter { Value = 2 }, [], NoRebase,
                containerReplacements: [(ListA, newList)]);
            Assert.Equal(newPoint, added.Catalog.Accepted!.Schemas[new(newPoint.Type, 1)]);
            Assert.Equal(PointA, added.Catalog.Accepted.Schemas[new(PointA.Type, 1)]);
            Assert.Equal(newList, added.Catalog.Accepted.ResolveLayout(added.Catalog.Accepted.DescriptorId(newList)));
            AssertRemoved(added.Catalog.Accepted, ListA, listId);
            Assert.Equal(arrayId, added.Catalog.Accepted.DescriptorId(ArrayA));
            Assert.Equal(dictionaryId, added.Catalog.Accepted.DescriptorId(DictionaryA));
            AssertFirstBase(repository, added);
            AssertSameRef(shared, ReadRef(repository, 2));
            Assert.Equal(sourceHead, main.Head.RevisionAddress);
            Assert.Equal(sourcePin, repository.SharedSchemaRoot);
        }
        using Repository cold = Repository.OpenReadOnlyExisting(_path, CounterModels());
        AssertSameRef(shared, ReadRef(cold, 2));
        P0CatalogView original = P0CatalogView.Load(cold.MetaBootstrapStore, sourcePin);
        Assert.Equal(listId, original.DescriptorId(ListA));
        Assert.Equal(arrayId, original.DescriptorId(ArrayA));
        Assert.Equal(dictionaryId, original.DescriptorId(DictionaryA));
        Assert.Equal(sourceHead, cold.GetHead("main").RevisionAddress);
        foreach (string name in new[] { "noop", "merged", "fixed", "new-key" }) {
            Assert.Equal(2, Assert.IsType<Counter>(cold.ReadState(cold.GetHead(name))).Value);
        }
        Assert.Equal(sourcePin, Catalog(cold, "noop").Snapshot!.Address);
        Assert.Equal([ListA], Containers(Catalog(cold, "merged")));
        Assert.Empty(Containers(Catalog(cold, "fixed")));
        Assert.Equal(newPoint, Catalog(cold, "new-key").Schemas[new(newPoint.Type, 1)]);
        AssertFramesPreserved(ResourcesOf(cold).Data, complete);
    }

    [Fact]
    public void Changed_existing_inline_dependencies_require_an_override_and_every_untouched_container_must_still_close_before_capture() {
        int captures = 0;
        using Repository repository = Repository.CreateNew(_path, CounterModels(() => captures++), schemaWriteMode: SchemaWriteMode.Shared);
        using BranchCheckout main = Seed(repository);
        P0CatalogView old = main.Catalog.Accepted!;
        PublicationImage before = Snapshot(repository);
        captures = 0;
        var complete = new[] { (ListA, List(PointB)), (ArrayA, Array(PointB)), (DictionaryA, Dictionary(PointB)) };
        Assert.Throws<SchemaConflictException>(() => repository.CreatePrivateBranch("trial", new Counter { Value = 2 }, [], NoRebase,
            containerReplacements: complete)); // New layouts alone do not authorize changing an existing Schema key.
        Assert.Equal(0, captures);
        AssertUnchanged(repository, before);
        Assert.Throws<SchemaConflictException>(() => repository.CreatePrivateBranch("trial", new Counter { Value = 2 }, [PointB], NoRebase,
            containerReplacements: [(ListA, List(PointB))])); // Retained Array/Dictionary still refer to the complete old inline definition.
        Assert.Equal(0, captures);
        AssertUnchanged(repository, before);
        Assert.DoesNotContain("trial", repository.ListBranches());
        using BranchCheckout corrected = repository.CreatePrivateBranch("trial", new Counter { Value = 2 }, [PointB], NoRebase,
            containerReplacements: complete);
        Assert.Equal(1, captures);
        P0CatalogView view = corrected.Catalog.Accepted!;
        Assert.Equal(PointB, view.Schemas[new(PointB.Type, 1)]);
        Assert.NotEqual(old.SchemaNodeId(PointA), view.SchemaNodeId(PointB));
        Assert.Throws<InvalidDataException>(() => view.SchemaNodeId(PointA));
        foreach (var (oldLayout, newLayout) in complete) {
            AssertRemoved(view, oldLayout, old.DescriptorId(oldLayout));
            Assert.Equal(newLayout, view.ResolveLayout(view.DescriptorId(newLayout)));
            Assert.NotEqual(old.DescriptorId(oldLayout), view.DescriptorId(newLayout));
        }
        AssertFirstBase(repository, corrected);
        AssertSameRef(before.Shared, ReadRef(repository, 2));
        P0CatalogView shared = P0CatalogView.Load(repository.MetaBootstrapStore, repository.SharedSchemaRoot!.Value);
        Assert.Equal(PointA, shared.Schemas[new(PointA.Type, 1)]);
        Assert.Equal(old.DescriptorIds.ToArray(), shared.DescriptorIds.ToArray());
    }

    [Theory]
    [InlineData("same-type-wrong-complete-old")]
    [InlineData("duplicate-old")]
    [InlineData("fixed-old")]
    [InlineData("null-old")]
    [InlineData("null-new")]
    [InlineData("noncontainer-new")]
    [InlineData("employee-container-new")]
    public void Invalid_endpoints_reject_before_capture_without_occupying_the_name_or_appending(string shape) {
        int captures = 0;
        using Repository repository = Repository.CreateNew(_path, CounterModels(() => captures++), schemaWriteMode: SchemaWriteMode.Shared);
        using BranchCheckout main = Seed(repository);
        DurableSchema otherPoint = new(PointA.Type, 1, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.Byte));
        ObjectLayout otherList = List(otherPoint);
        Assert.Equal(ListA.Type, otherList.Type);
        Assert.NotEqual(ListA, otherList);
        (ObjectLayout, ObjectLayout)[] requested = shape switch {
            "same-type-wrong-complete-old" => [(otherList, ListA)],
            "duplicate-old" => [(ListA, ListA), (ListA, FixedList)],
            "fixed-old" => [(FixedList, ListA)],
            "null-old" => [(null!, ListA)],
            "null-new" => [(ListA, null!)],
            "noncontainer-new" => [(ListA, ObjectLayout.String)],
            "employee-container-new" => [(ListA, P0FixedBindings.Layout(typeof(List<P0FieldRecord>)))],
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
        PublicationImage before = Snapshot(repository);
        captures = 0;
        Exception? error = Record.Exception(() => repository.CreatePrivateBranch("trial", new Counter { Value = 2 }, [], NoRebase,
            containerReplacements: requested));
        if (shape is "same-type-wrong-complete-old" or "fixed-old") { Assert.IsType<InvalidDataException>(error); }
        else if (shape is "null-old" or "null-new") { Assert.IsType<ArgumentNullException>(error); }
        else { Assert.IsType<ArgumentException>(error); }
        Assert.Equal(0, captures);
        AssertUnchanged(repository, before);
        Assert.DoesNotContain("trial", repository.ListBranches());
        using BranchCheckout retry = repository.CreatePrivateBranch("trial", new Counter { Value = 2 }, [], NoRebase,
            containerReplacements: [(ListA, ListA)]);
        Assert.Equal(1, captures);
        AssertFirstBase(repository, retry);
        AssertSameRef(before.Shared, ReadRef(repository, 2));
    }

    [Fact]
    public void Replacement_enumeration_runs_inside_the_owner_guard_and_an_interrupted_enumeration_can_retry_the_same_name() {
        int captures = 0;
        using Repository repository = Repository.CreateNew(_path, CounterModels(() => captures++), schemaWriteMode: SchemaWriteMode.Shared);
        using BranchCheckout main = Seed(repository);
        PublicationImage before = Snapshot(repository);
        Assert.Equal(main.Head.RevisionAddress, repository.GetHead("main").RevisionAddress);
        captures = 0;
        bool enumerated = false;
        IOException injected = new("Replacement enumeration interrupted.");
        IEnumerable<(ObjectLayout, ObjectLayout)> Enumerate() {
            enumerated = true;
            Assert.Throws<InvalidOperationException>(() => repository.GetHead("main"));
            yield return (ListA, ListA);
            throw injected;
        }
        Assert.Same(injected, Assert.Throws<IOException>(() => repository.CreatePrivateBranch("trial", new Counter { Value = 2 }, [], NoRebase,
            containerReplacements: Enumerate())));
        Assert.True(enumerated);
        Assert.Equal(0, captures);
        AssertUnchanged(repository, before);
        Assert.Equal(main.Head.RevisionAddress, repository.GetHead("main").RevisionAddress);
        Assert.DoesNotContain("trial", repository.ListBranches());
        using BranchCheckout retry = repository.CreatePrivateBranch("trial", new Counter { Value = 2 }, [], NoRebase,
            containerReplacements: [(ArrayA, ListA)]);
        Assert.Equal(1, captures);
        AssertRemoved(retry.Catalog.Accepted!, ArrayA, main.Catalog.Accepted!.DescriptorId(ArrayA));
        AssertFirstBase(repository, retry);
    }

    [Fact]
    public void A_real_generated_business_List_cannot_redeclare_a_removed_layout_during_capture_and_can_retry_with_a_compatible_root() {
        FrameAddress source;
        FrameImage[] complete;
        using (Repository repository = Repository.CreateNew(_path, GeneratedModels(), schemaWriteMode: SchemaWriteMode.Shared)) {
            DB087Widget widget = new() { Value = 7, Numbers = [1, 2, 3] };
            DB087ReplacementListRoot root = new() { Items = [widget] };
            using BranchCheckout main = repository.CreateBranch("main", root, NoRebase);
            source = main.Head.RevisionAddress;
            ObjectLayout old = main.Baseline!.Objects.Values.Single(row => row.Current.Layout.List?.ElementSlot.TargetType == TypeExpr.Named("P0Widget")).Current.Layout;
            ObjectId oldId = main.Catalog.Accepted!.DescriptorId(old);
            PublicationImage before = Snapshot(repository);
            complete = before.Frames;
            InvalidDataException error = Assert.Throws<InvalidDataException>(() => repository.CreatePrivateBranch("trial", root, [], NoRebase,
                containerReplacements: [(old, FixedList)]));
            Assert.Equal("The selected catalog lacks this complete object layout.", error.Message);
            AssertUnchanged(repository, before); // Capture happened, but the actual typed writer cannot encode an undeclared old layout.
            Assert.DoesNotContain("trial", repository.ListBranches());
            Assert.Same(widget, Assert.Single(root.Items));
            using BranchCheckout corrected = repository.CreatePrivateBranch("trial", widget, [], NoRebase,
                containerReplacements: [(old, FixedList)]);
            AssertRemoved(corrected.Catalog.Accepted!, old, oldId);
            AssertFirstBase(repository, corrected, expectedRecords: 2);
            AssertSameRef(before.Shared, ReadRef(repository, 2));
        }
        using Repository cold = Repository.OpenExisting(_path, GeneratedModels());
        using BranchCheckout privateBranch = cold.CheckoutPrivate("trial");
        DB087Widget saved = Assert.IsType<DB087Widget>(privateBranch.State);
        Assert.Equal(7, saved.Value);
        saved.Numbers[1] = 30;
        privateBranch.CommitState(NoRebase);
        Assert.Equal(30, Assert.IsType<DB087Widget>(cold.ReadState(privateBranch.Head)).Numbers[1]);
        Assert.Equal(source, cold.GetHead("main").RevisionAddress);
        DB087ReplacementListRoot original = Assert.IsType<DB087ReplacementListRoot>(cold.ReadState(cold.GetHead("main")));
        Assert.Equal([1, 2, 3], Assert.Single(original.Items).Numbers);
        AssertFramesPreserved(ResourcesOf(cold).Data, complete);
    }

    [Theory]
    [InlineData((int)P0SaveCheckpoint.AfterMetaAppend, false, false)]
    [InlineData((int)P0SaveCheckpoint.BeforePublication, false, true)]
    [InlineData((int)P0SaveCheckpoint.AfterPublication, true, true)]
    public void Private_replacement_failure_is_decided_by_the_actual_branch_ref_after_close_and_keeps_shared_and_completed_frames(int stage, bool visible, bool changesSchema) {
        FrameAddress sourceHead, sourcePin;
        RefImage branches, shared;
        FrameImage[] complete;
        GraphCommitException error;
        using (Repository repository = Repository.CreateNew(_path, CounterModels(), schemaWriteMode: SchemaWriteMode.Shared)) {
            using BranchCheckout main = Seed(repository);
            sourceHead = main.Head.RevisionAddress;
            sourcePin = repository.SharedSchemaRoot!.Value;
            branches = ReadRef(repository, 1);
            shared = ReadRef(repository, 2);
            complete = ReadFrames(ResourcesOf(repository).Data);
            var replacements = changesSchema
                ? new[] { (ListA, List(PointB)), (ArrayA, Array(PointB)), (DictionaryA, Dictionary(PointB)) }
                : new[] { (ListA, FixedList), (ArrayA, FixedList), (DictionaryA, FixedList) };
            IOException injected = new("Private container publication interrupted.");
            repository.MetaBootstrapCheckpoint = checkpoint => { if (checkpoint == (P0SaveCheckpoint)stage) { throw injected; } };
            BranchCheckout? delivered = null;
            error = Assert.Throws<GraphCommitException>(() => delivered = repository.CreatePrivateBranch("trial", new Counter { Value = 2 },
                changesSchema ? [PointB] : [], NoRebase, containerReplacements: replacements));
            Assert.Same(injected, error.InnerException);
            Assert.Equal(visible ? GraphCommitOutcome.Published : GraphCommitOutcome.NotPublished, error.Outcome);
            Assert.True(repository.IsFaulted);
            Assert.Null(delivered);
        }
        using Repository cold = Repository.OpenExisting(_path, CounterModels());
        AssertSameRef(shared, ReadRef(cold, 2)); // Private publication never changes the shared ref, even after publication succeeds.
        Assert.Equal(sourcePin, cold.SharedSchemaRoot);
        Assert.Equal(sourceHead, cold.GetHead("main").RevisionAddress);
        P0CatalogView sourceView = P0CatalogView.Load(cold.MetaBootstrapStore, sourcePin);
        Assert.Equal(PointA, sourceView.Schemas[new(PointA.Type, 1)]);
        Assert.Equal(3, Containers(sourceView).Length);
        if (visible) {
            Assert.NotEqual(branches.Revision, ReadRef(cold, 1).Revision);
            using BranchCheckout recovered = cold.CheckoutPrivate("trial");
            Assert.Equal(error.CandidateRevisionAddress, recovered.Head.RevisionAddress);
            Assert.Equal(2, Assert.IsType<Counter>(recovered.State).Value);
            Assert.Equal(PointB, recovered.Catalog.Accepted!.Schemas[new(PointB.Type, 1)]);
            foreach (ObjectLayout layout in new[] { ListA, ArrayA, DictionaryA }) {
                AssertRemoved(recovered.Catalog.Accepted, layout, sourceView.DescriptorId(layout));
            }
            AssertFirstBase(cold, recovered);
        } else {
            AssertSameRef(branches, ReadRef(cold, 1));
            Assert.DoesNotContain("trial", cold.ListBranches());
        }
        if (stage == (int)P0SaveCheckpoint.AfterMetaAppend) { Assert.Null(error.CandidateRevisionAddress); }
        else { Assert.NotNull(error.CandidateRevisionAddress); }
        AssertFramesPreserved(ResourcesOf(cold).Data, complete);
    }

    private static ObjectLayout List(DurableSchema schema) => ObjectLayout.ForList(new(new(1, TypeTag.InlineValue, inlineSchema: schema)));
    private static ObjectLayout Array(DurableSchema schema) => ObjectLayout.ForArray(new(TypeExprKind.VectorArray, new(1, TypeTag.InlineValue, inlineSchema: schema)));
    private static ObjectLayout Dictionary(DurableSchema schema) => ObjectLayout.ForDictionary(new(new(1, TypeTag.InlineValue, inlineSchema: schema), new(2, TypeTag.Int32)));
    private static ObjectLayout[] Containers(P0CatalogView view) => view.ObjectLayouts.Where(static layout => layout.Schema is null).ToArray();
    private static P0CatalogView Catalog(Repository repository, string name) => P0CatalogView.Load(repository.MetaBootstrapStore,
        repository.MetaBootstrapStore.ReadMetadata(repository.GetHead(name).RevisionAddress).SchemaRoot!.Value);
    private static BranchCheckout Seed(Repository repository) {
        BranchCheckout main = repository.CreateBranch("main", new Counter { Value = 1 }, NoRebase);
        main.Catalog.Set(ListA);
        main.Catalog.Set(ArrayA);
        main.Catalog.Set(DictionaryA);
        main.CommitState(NoRebase);
        return main;
    }
    private static void AssertRemoved(P0CatalogView view, ObjectLayout old, ObjectId oldId) {
        Assert.Throws<InvalidDataException>(() => view.DescriptorId(old));
        Assert.Throws<InvalidDataException>(() => view.ResolveLayout(oldId));
        Assert.True(view.Rows.ContainsKey(oldId)); // Old completed employee nodes remain live; declaration membership controls lookup.
    }
    private static void AssertRetainedEmployeeHeads(P0CatalogView old, P0CatalogView next) {
        foreach (var (id, head) in old.Snapshot!.Objects) {
            if (id != old.DescriptorListId.Value) { Assert.Equal(head, next.Snapshot!.Objects[id]); }
        }
    }
    private static void AssertFirstBase(Repository repository, BranchCheckout branch, int expectedRecords = 1) {
        P0GraphSnapshot graph = repository.MetaBootstrapStore.ReadGraph(branch.Head.RevisionAddress);
        Assert.Null(graph.Parent);
        Assert.Null(graph.SourceGraph);
        P0Batch batch = repository.MetaBootstrapStore.ReadBatch(graph.OwnedBatch!.Value);
        Assert.Null(batch.SourceGraph);
        Assert.Equal(graph.SchemaRoot, batch.SchemaRoot);
        Assert.Equal(expectedRecords, batch.Records.Count);
        Assert.All(batch.Records, row => Assert.Equal(ObjectVersionKind.Base, row.Kind));
    }
    private sealed class Counter : IDurableObject { internal int Value; }
    private static readonly DurableSchema CounterSchema = new("Replacement.Counter", 1, new DurableFieldInfo(1, TypeTag.Int32));
    private static StateModelRegistry CounterModels(Action? capture = null) {
        static void Visit(in int value, IStateReferenceVisitor visitor) { }
        static PreparedBaseBody Base(in int value) {
            ArrayBufferWriter<byte> bytes = new();
            BareValueWriter writer = new(bytes);
            writer.WriteVarInt32(value);
            return new(bytes.WrittenSpan);
        }
        StateModelRegistry models = new();
        models.Register(new StateModelBinding<Counter, int>(new(CounterSchema, Base,
            static (in int prior, in int next) => new(prior != next, Base(next).Body)),
            [new StateReaderBinding<int>(CounterSchema, static (ref BareValueReader reader) => reader.ReadVarInt32(),
                static (ref BareValueReader reader, in int prior) => reader.ReadVarInt32(), Visit)],
            static row => row.GetState<int>(), static () => new(),
            static (Counter domain, in int value, ObjectReadTable objects) => domain.Value = value,
            (domain, context) => { capture?.Invoke(); return domain.Value; }, Visit));
        return models;
    }
    private static StateModelRegistry GeneratedModels() {
        StateModelRegistry models = new();
        models.Register(global::Atelia.DurableGraph.Generated.Family_5030576964676574.Definition);
        models.Register(global::Atelia.DurableGraph.Generated.Family_5265706C6163656D656E742E4C697374526F6F74.Definition);
        return models;
    }
    private sealed record RefImage(RefRevision Revision, byte[] Payload);
    private sealed record FrameImage(FrameAddress Address, uint Tag, int MetaLength, bool IsTombstone, byte[] Payload);
    private sealed record PublicationImage(RefImage Branches, RefImage Shared, long Appended, FrameImage[] Frames);
    private static RefImage ReadRef(Repository repository, uint id) {
        RefBytesSnapshot read = ResourcesOf(repository).Versions.ReadRef(id).Unwrap();
        return new(read.Revision, read.Payload.ToArray());
    }
    private static FrameImage[] ReadFrames(Frames data) {
        List<FrameInfo> frames = [];
        data.Inventory(frames.Add).Unwrap();
        return frames.Select(info => {
            using FrameRead read = data.ReadFrame(info.Address).Unwrap();
            return new FrameImage(info.Address, read.Tag, read.TailMetaLength, read.IsTombstone, read.PayloadAndMeta.ToArray());
        }).ToArray();
    }
    private static PublicationImage Snapshot(Repository repository) => new(ReadRef(repository, 1), ReadRef(repository, 2),
        repository.MetaBootstrapStore.AppendedFrames, ReadFrames(ResourcesOf(repository).Data));
    private static void AssertSameRef(RefImage before, RefImage after) {
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.Payload, after.Payload);
    }
    private static void AssertFrame(FrameImage before, FrameImage after) {
        Assert.Equal(before.Tag, after.Tag);
        Assert.Equal(before.MetaLength, after.MetaLength);
        Assert.Equal(before.IsTombstone, after.IsTombstone);
        Assert.Equal(before.Payload, after.Payload);
    }
    private static void AssertFramesPreserved(Frames data, IEnumerable<FrameImage> before) {
        FrameImage[] after = ReadFrames(data);
        foreach (FrameImage frame in before) { AssertFrame(frame, after.Single(item => item.Address == frame.Address)); }
    }
    private static void AssertUnchanged(Repository repository, PublicationImage before) {
        AssertSameRef(before.Branches, ReadRef(repository, 1));
        AssertSameRef(before.Shared, ReadRef(repository, 2));
        Assert.Equal(before.Appended, repository.MetaBootstrapStore.AppendedFrames);
        FrameImage[] after = ReadFrames(ResourcesOf(repository).Data);
        Assert.Equal(before.Frames.Select(frame => frame.Address), after.Select(frame => frame.Address));
        foreach (FrameImage frame in before.Frames) { AssertFrame(frame, after.Single(item => item.Address == frame.Address)); }
        Assert.False(ResourcesOf(repository).HasMetaBootstrapPending);
        Assert.False(repository.IsFaulted);
    }
    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}
