using System.Buffers.Binary;
using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Xunit.Abstractions;
using DescriptorDto = Atelia.DurableGraph.Generated.Family_503044.V1;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class RegisteredNodeHistoricalReferenceTests(ITestOutputHelper output) : IDisposable {
    private const string Prefix = "dg-node-history-reference-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
    private static readonly TypeExpr StringTarget = TypeExpr.Builtin(TypeTag.String);
    private static readonly ReadAmplificationBaseBudgetParameters NoRebase = new(1000000, 1);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HistoricalReferencesDroppedByTheCurrentVersionStillValidateBeforeMaterialization(bool changeLastElement) {
        History history = WriteHistory(changeLastElement);
        using (var historical = Repository.OpenReadOnlyExisting(_path, WriterModels())) {
            var old = Assert.IsType<NodeReviewRoot<NodeReviewV1>>(historical.LoadState(history.Valid));
            if (changeLastElement) {
                Assert.Equal("first target", old.Value.Link);
                Assert.Null(old.Value.Padding[0]);
                Assert.Same(old.Value.Padding[1], old.Value.Padding[2]);
                Assert.Equal("padding", old.Value.Padding[1]);
                Assert.Equal("second target", old.Value.Padding[^1]);
            } else {
                Assert.Equal("second target", old.Value.Link);
            }
            Assert.Equal(21, old.Value.Value);
        }
        var control = new ReadTrace();
        using (var current = Repository.OpenReadOnlyExisting(_path, ReaderModels(control))) {
            Assert.Equal(10, Assert.IsType<NodeReviewRoot<NodeReviewV2>>(current.LoadState(history.Base)).Value.Value);
            Assert.Equal(21, Assert.IsType<NodeReviewRoot<NodeReviewV2>>(current.LoadState(history.Valid)).Value.Value);
            Assert.Equal(2, control.RootAllocations);
            Assert.Equal(2, control.Allocations);
            Assert.Equal(2, control.Hydrations);
        }
        foreach (var bad in history.Damaged) {
            var trace = new ReadTrace();
            using var current = Repository.OpenReadOnlyExisting(_path, ReaderModels(trace));
            IDurableObject? delivered = null;
            var error = Assert.Throws<InvalidDataException>(() => delivered = current.LoadState(bad));
            Assert.Contains($"Object ID {history.Target}", error.Message);
            Assert.Contains($"reference type {StringTarget}", error.Message);
            Assert.Null(delivered);
            Assert.Equal(0, trace.RootAllocations);
            Assert.Equal(0, trace.Allocations);
            Assert.Equal(0, trace.Hydrations);
        }
    }

    [Fact]
    public void SavingAnUneditedUpgradeRewritesTheNodeAsACurrentBaseAndDropsItsRetiredTargets() {
        var history = WriteHistory();
        CommitId rewritten;
        using (var repo = Repository.OpenExisting(_path, ReaderModels(new()))) {
            var prior = DB096TestAccess.Snapshot(repo, history.Valid);
            var oldNode = Assert.Single(repo.GraphStore.ReadBatch(prior.OwnedBatch!.Value).Records,
                row => row.Kind == ObjectVersionKind.Delta);
            using var work = repo.OpenWorkingCopy(repo.ForkBranch(history.Valid, "upgrade").Id);
            Assert.Equal(21, Assert.IsType<NodeReviewRoot<NodeReviewV2>>(work.State).Value.Value);
            rewritten = work.CommitState().Id;
            var saved = DB096TestAccess.Snapshot(repo, rewritten);
            Assert.Equal(new[] { saved.RootId.Value, oldNode.ObjectId }.Order(), saved.ObjectHeads.Keys.Order());
            var entry = Assert.Single(repo.GraphStore.ReadObjectChain(saved, oldNode.ObjectId).Entries);
            Assert.Equal(ObjectVersionKind.Base, entry.Record.Kind);
            var catalog = SchemaCatalogView.Load(repo.GraphStore, entry.SchemaRoot!.Value);
            var decoded = TypedBaseBodyCodec.Decode(entry.Record.Body, catalog);
            Assert.Equal(2, decoded.Layout.Schema!.Version);
            Assert.Equal(DeclaredSlotKind.Int32, Assert.Single(decoded.Layout.Schema.DeclaredSlots).Kind);
            Assert.Equal(new byte[] { 42 }, decoded.Body); // ZigZag(21) with no retired reference bytes.
        }
        using (var current = Repository.OpenReadOnlyExisting(_path, ReaderModels(new(), []))) {
            Assert.Equal(21, Assert.IsType<NodeReviewRoot<NodeReviewV2>>(current.LoadState(rewritten)).Value.Value);
        }
        using var original = Repository.OpenReadOnlyExisting(_path, WriterModels());
        Assert.Equal("second target", Assert.IsType<NodeReviewRoot<NodeReviewV1>>(original.LoadState(history.Valid)).Value.Link);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SameVersionReadersCannotChangeThePublishedLayoutOrBypassItAsAnOpaqueLeaf(bool opaque) {
        History history = WriteHistory();
        var trace = new ReadTrace();
        var models = opaque ? RootModels<ReviewOpaqueV1>(trace) : RootModels<NodeReviewV1>(trace);
        if (opaque) {
            models.RegisterLeaf(new OpaqueCodec(trace));
        } else {
            new DeclaredNode<NodeReviewV1>(() => { trace.Allocations++; return new(); }, [
                DeclaredMember<NodeReviewV1>.Int32(n => n.Value, (n, value) => n.Value = value),
                // Int32 and the actual string reference both consume one varuint in Base and Delta.
                DeclaredMember<NodeReviewV1>.Int32(n => 99, (n, value) => trace.Hydrations++),
                DeclaredMember<NodeReviewV1>.References<string>(StringTarget, n => n.Padding, (n, items) => n.Padding = items),
            ]).Register(models);
        }
        using var reader = Repository.OpenReadOnlyExisting(_path, models);
        Assert.Throws<InvalidDataException>(() => reader.LoadState(history.Valid));
        Assert.Equal(0, trace.LeafReads);
        Assert.Equal(0, trace.RootAllocations);
        Assert.Equal(0, trace.Allocations);
        Assert.Equal(0, trace.Hydrations);
    }

    private sealed record History(CommitId Base, CommitId Valid, CommitId[] Damaged, ObjectId Target);

    private History WriteHistory(bool changeLastElement = false) {
        using var store = RepositoryStorageFacade.CreateNew(_path);
        BranchSaveSessionFixture session = new(store, "node", WriterModels());
        var padding = Enumerable.Repeat<string?>("padding", 192).ToArray();
        padding[0] = null;
        padding[^1] = "first vector target";
        NodeReviewV1 node = new() { Value = 10, Link = "first target", Padding = padding };
        NodeReviewRoot<NodeReviewV1> root = new() { Value = node };
        using var first = session.Prepare(root, parameters: NoRebase);
        RepositorySaveResult original = first.Commit();
        node.Value = 21;
        if (changeLastElement) { node.Padding[^1] = "second target"; }
        else { node.Link = "second target"; }
        using var next = session.Prepare(root, parameters: NoRebase);
        RepositorySaveResult valid = next.Commit();
        var graph = valid.Business;
        var changed = store.Store.ReadBatch(graph.OwnedBatch!.Value).Records;
        var delta = Assert.Single(changed, record => record.Kind == ObjectVersionKind.Delta);
        var target = Assert.Single(changed, record => record.Kind == ObjectVersionKind.Base);
        Assert.Equal(2, changed.Count);
        Assert.Equal(original.Business.ObjectHeads[delta.ObjectId], delta.PriorAddress);
        Assert.DoesNotContain(target.ObjectId, original.Business.ObjectHeads.Keys);
        // The Base still names this string, but the selected directory only needs the final Delta target.
        uint retiredId = Assert.Single(original.Business.ObjectHeads.Keys.Except(graph.ObjectHeads.Keys));
        var retired = Assert.Single(store.Store.ReadObjectChain(original.Business, retiredId).Entries).Record;
        string retiredText = changeLastElement ? "first vector target" : "first target";
        Assert.Equal(TypedBaseBodyCodec.Encode(ObjectLayout.String, StringBodyPreparer.PrepareBase(retiredText)), retired.Body.ToArray());
        CheckPublishedSlots(store.Store, original, valid, delta.ObjectId, changeLastElement);

        // Each bad directory retains the node's exact v1 Base and Delta.
        var rootBase = Assert.Single(store.Store.ReadObjectChain(graph, graph.RootId.Value).Entries).Record;
        // A copy of the actual root is a well-formed replacement row of the wrong nominal type.
        var wrongTarget = ObjectVersionRecord.CreateBase(target.ObjectId, rootBase.Body);
        var catalog = SchemaCatalogView.Load(store.Store, graph.SchemaRoot!.Value);
        var rootLayout = TypedBaseBodyCodec.Decode(rootBase.Body, catalog).Layout;
        // A null root field leaves the damaged node in the stored directory but unreachable even in v1.
        var detachedRoot = ObjectVersionRecord.CreateBase(graph.RootId.Value,
            TypedBaseBodyCodec.Encode(rootLayout, new PreparedBaseBody(new byte[sizeof(uint)]), catalog));
        CommitId Publish(PreparedGraphWrite write) {
            using var candidate = MechanismFixture.Prepare(store, "node", StoredGraphKind.State, write);
            var saved = store.Commit(candidate);
            Assert.Equal(graph.ObjectHeads[delta.ObjectId], saved.Business.ObjectHeads[delta.ObjectId]);
            return new(saved.CommitId);
        }
        CommitId[] damaged = [
            Publish(new(graph.Address, graph.RootId, [], [target.ObjectId])),
            Publish(new(graph.Address, graph.RootId, [wrongTarget])),
            Publish(new(graph.Address, graph.RootId, [detachedRoot], [target.ObjectId])),
        ];
        return new(new(original.CommitId), new(valid.CommitId), damaged, new(target.ObjectId));
    }

    private void CheckPublishedSlots(GraphRecordStore store, RepositorySaveResult first, RepositorySaveResult next,
        uint id, bool changeLastElement) {
        Assert.NotNull(first.Meta);
        Assert.Equal(first.Meta.Address, next.Meta?.Address);
        Assert.Equal(first.Meta.Address, next.Business.SchemaRoot);
        var chain = store.ReadObjectChain(next.Business, id);
        Assert.Equal(new[] { ObjectVersionKind.Base, ObjectVersionKind.Delta }, chain.Entries.Select(row => row.Record.Kind));
        var initial = chain.Entries[0];
        Assert.Equal(first.Meta.Address, initial.SchemaRoot);
        var catalog = SchemaCatalogView.Load(store, initial.SchemaRoot!.Value);
        var decoded = TypedBaseBodyCodec.Decode(initial.Record.Body, catalog);
        var schema = Assert.IsType<DurableSchema>(decoded.Layout.Schema);
        Assert.Equal("Probe.RetiredNodeEdge", schema.SchemaId);
        Assert.Equal(1, schema.Version);
        Assert.True(schema.IsDeclaredNode);
        Assert.Equal(new[] { DeclaredSlotKind.Int32, DeclaredSlotKind.Reference, DeclaredSlotKind.References },
            schema.DeclaredSlots.Select(slot => slot.Kind));
        Assert.Equal(new TypeExpr?[] { null, StringTarget, StringTarget }, schema.DeclaredSlots.Select(slot => slot.Target));
        Assert.All(schema.DeclaredSlots, slot => Assert.Null(slot.FormatId));
        BareValueReader header = new(initial.Record.Body);
        Assert.Equal(TypedBaseBodyCodec.FormatVersion, header.ReadByte());
        Assert.Equal((byte)1, header.ReadByte());
        var descriptor = new ObjectId(header.ReadVarUInt32());
        Assert.Equal(catalog.DescriptorId(decoded.Layout), descriptor);
        Assert.Equal((byte)6, catalog.Rows[descriptor].GetState<DescriptorDto>().Segment0Field1);
        output.WriteLine($"v1, updated {(changeLastElement ? "last repeated item" : "single reference")}: P0D={descriptor.Value}, kind=6, "
            + $"node={id}, Base={decoded.Body.Length} body bytes/{initial.PayloadBytes} payload bytes, "
            + $"Delta={chain.Entries[1].Record.Body.Length} body bytes/{chain.Entries[1].PayloadBytes} payload bytes");
    }

    internal static StateModelRegistry RootModels<T>(ReadTrace? trace = null, TypeExpr? target = null) where T : class {
        var nodeType = target ?? TypeExpr.Named("Probe.RetiredNodeEdge");
        DurableSchema schema = new(nodeType.DefinitionId + ".TestRoot", 1, DurableFieldInfo.Reference(1, nodeType));
        static PreparedBaseBody Write(in ObjectId id) {
            byte[] bytes = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, id.Value);
            return new(bytes);
        }
        static ObjectId Read(ref BareValueReader reader) => new(BinaryPrimitives.ReadUInt32LittleEndian(reader.ReadRawBytes(4)));
        void Visit(in ObjectId id, IStateReferenceVisitor visitor) => visitor.VisitObject(id, nodeType);
        var preparation = new StatePreparationBinding<ObjectId>(schema, Write,
            (in ObjectId prior, in ObjectId next) => new(prior != next, Write(next).Body));
        var reader = new StateReaderBinding<ObjectId>(schema, Read, (ref BareValueReader input, in ObjectId _) => Read(ref input), Visit);
        StateModelRegistry registry = new();
        registry.Register(new StateModelBinding<NodeReviewRoot<T>, ObjectId>(preparation, [reader], row => row.GetState<ObjectId>(),
            () => { if (trace is not null) trace.RootAllocations++; return new(); },
            (NodeReviewRoot<T> root, in ObjectId id, ObjectReadTable objects) => root.Value = objects.ResolveObject<T>(id)!,
            (root, context) => context.CaptureObject(root.Value, nodeType), Visit));
        return registry;
    }

    private static StateModelRegistry WriterModels() {
        var registry = RootModels<NodeReviewV1>();
        new DeclaredNode<NodeReviewV1>(() => new(), [
            DeclaredMember<NodeReviewV1>.Int32(n => n.Value, (n, value) => n.Value = value),
            DeclaredMember<NodeReviewV1>.Reference<string>(StringTarget, n => n.Link, (n, link) => n.Link = link),
            DeclaredMember<NodeReviewV1>.References<string>(StringTarget, n => n.Padding, (n, items) => n.Padding = items),
        ]).Register(registry);
        return registry;
    }

    internal static StateModelRegistry ReaderModels(ReadTrace trace, int[]? acceptedVersions = null) {
        var registry = RootModels<NodeReviewV2>(trace);
        new DeclaredNode<NodeReviewV2>(() => { trace.Allocations++; return new(); }, [
            DeclaredMember<NodeReviewV2>.Int32(n => n.Value, (n, value) => { trace.Hydrations++; n.Value = value; }),
        ], acceptedVersions ?? [1]).Register(registry);
        return registry;
    }

    // Consumes arbitrary bytes to ensure the layout check fires before author code could accept a declared-node record.
    private sealed class OpaqueCodec(ReadTrace trace) : DurableLeafCodec<ReviewOpaqueV1, int> {
        public override int Capture(ReviewOpaqueV1 value, LeafAcceptance? acceptance) => 0;
        public override PreparedBaseBody PrepareBase(int state) => new([]);
        public override int ReadBase(int version, ref BareValueReader reader) {
            trace.LeafReads++;
            reader.ReadRawBytes(reader.RemainingCount);
            return 0;
        }
        public override int ApplyDelta(int version, ref BareValueReader reader, int previous) => ReadBase(version, ref reader);
        public override ReviewOpaqueV1 Restore(int state) { trace.Allocations++; return new(); }
    }

    internal class ReadTrace {
        internal int RootAllocations, Allocations, Hydrations, LeafReads;
    }

    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}

internal sealed class NodeReviewRoot<T> : IDurableObject {
    internal T Value = default!;
}

[DurableNode("Probe.RetiredNodeEdge", 1)]
public sealed class NodeReviewV1 {
    public int Value;
    public string? Link;
    public string?[] Padding = [];
}

[DurableNode("Probe.RetiredNodeEdge", 2)]
public sealed class NodeReviewV2 {
    public int Value;
}

[DurableLeaf("Probe.RetiredNodeEdge", 1)]
public sealed class ReviewOpaqueV1 { }
