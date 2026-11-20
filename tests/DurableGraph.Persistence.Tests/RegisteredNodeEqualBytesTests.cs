using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Xunit.Abstractions;
using static Atelia.DurableGraph.Persistence.Tests.RegisteredNodeHistoricalReferenceTests;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class RegisteredNodeEqualBytesTests(ITestOutputHelper output) : IDisposable {
    private const string Prefix = "dg-node-equal-bytes-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
    private static readonly TypeExpr StringTarget = TypeExpr.Builtin(TypeTag.String);
    private static readonly ReadAmplificationBaseBudgetParameters NoRebase = new(1000000, 1);

    [Fact]
    public void SameNodeBaseAndDeltaMeanScalarOrReferenceAccordingToEachStoresPublishedSchema() {
        string referencePath = Path.Combine(_path, "reference");
        string scalarPath = Path.Combine(_path, "scalar");
        History reference = WriteV1(referencePath, scalarWords: null);
        History scalar = WriteV1(scalarPath, reference.Words);

        Assert.Equal(reference.BaseBody, scalar.BaseBody);
        Assert.Equal(reference.DeltaBody, scalar.DeltaBody);
        Assert.Equal(reference.Schema.Type, scalar.Schema.Type);
        Assert.Equal(1, reference.Schema.Version);
        Assert.Equal(reference.Schema.Version, scalar.Schema.Version);
        Assert.Equal(DeclaredSlotKind.Reference, reference.Schema.DeclaredSlots[1].Kind);
        Assert.Equal(StringTarget, reference.Schema.DeclaredSlots[1].Target);
        Assert.Equal(DeclaredSlotKind.Int32, scalar.Schema.DeclaredSlots[1].Kind);
        output.WriteLine($"Both stores: raw Base={Convert.ToHexString(reference.BaseBody)}, Delta={Convert.ToHexString(reference.DeltaBody)}.");

        // The same v2 package and registry open both stores. There are no v1 accessors or slots here.
        var trace = new ReadTrace();
        var models = ReaderModels(trace);
        foreach (var (path, history) in new[] { (referencePath, reference), (scalarPath, scalar) }) {
            using var reader = Repository.OpenReadOnlyExisting(path, models);
            Assert.Equal(10, Assert.IsType<NodeReviewRoot<NodeReviewV2>>(reader.LoadState(history.Base)).Value.Value);
            Assert.Equal(21, Assert.IsType<NodeReviewRoot<NodeReviewV2>>(reader.LoadState(history.Valid)).Value.Value);
        }
        Assert.Equal((4, 4, 4), (trace.RootAllocations, trace.Allocations, trace.Hydrations));

        using var damaged = Repository.OpenReadOnlyExisting(referencePath, models);
        var error = Assert.Throws<InvalidDataException>(() => damaged.LoadState(reference.Missing!.Value));
        Assert.Contains($"Object ID {new ObjectId(reference.Words.Delta)}", error.Message);
        Assert.Contains($"reference type {StringTarget}", error.Message);
        Assert.Equal((4, 4, 4), (trace.RootAllocations, trace.Allocations, trace.Hydrations));
    }

    private sealed record History(CommitId Base, CommitId Valid, CommitId? Missing, DurableSchema Schema,
        byte[] BaseBody, byte[] DeltaBody, (uint Base, uint Delta) Words);

    private static History WriteV1(string path, (uint Base, uint Delta)? scalarWords) {
        var models = RootModels<EqualBytesNodeV1>();
        DeclaredMember<EqualBytesNodeV1> retired = scalarWords.HasValue
            ? DeclaredMember<EqualBytesNodeV1>.Int32(n => n.Number, (n, value) => n.Number = value)
            : DeclaredMember<EqualBytesNodeV1>.Reference<string>(StringTarget, n => n.Link, (n, value) => n.Link = value);
        new DeclaredNode<EqualBytesNodeV1>(static () => new(), [
            DeclaredMember<EqualBytesNodeV1>.Int32(n => n.Value, (n, value) => n.Value = value),
            retired,
            DeclaredMember<EqualBytesNodeV1>.References<string>(StringTarget, n => n.Padding, (n, value) => n.Padding = value),
        ]).Register(models);

        using var store = RepositoryStorageFacade.CreateNew(path);
        var session = new BranchSaveSessionFixture(store, "node", models);
        var node = new EqualBytesNodeV1 { Value = 10, Number = SignedValue(scalarWords?.Base ?? 0), Link = "first" };
        var root = new NodeReviewRoot<EqualBytesNodeV1> { Value = node };
        using var first = session.Prepare(root, parameters: NoRebase);
        var original = first.Commit();
        node.Value = 21;
        node.Number = SignedValue(scalarWords?.Delta ?? 0);
        node.Link = "second";
        using var next = session.Prepare(root, parameters: NoRebase);
        var changed = next.Commit();
        var graph = changed.Business;
        var delta = Assert.Single(store.Store.ReadBatch(graph.OwnedBatch!.Value).Records, row => row.Kind == ObjectVersionKind.Delta);
        Assert.Equal(original.Business.ObjectHeads[delta.ObjectId], delta.PriorAddress);
        var firstRow = Assert.Single(store.Store.ReadObjectChain(original.Business, delta.ObjectId).Entries).Record;
        Assert.Equal(ObjectVersionKind.Base, firstRow.Kind);
        var catalog = SchemaCatalogView.Load(store.Store, original.Business.SchemaRoot!.Value);
        var typed = TypedBaseBodyCodec.Decode(firstRow.Body, catalog);
        var schema = Assert.IsType<DurableSchema>(typed.Layout.Schema);

        // Read both retired words from the actual records. The scalar writer uses the inverse of
        // Int32's ZigZag so its varuint words equal the IDs chosen by the reference writer.
        BareValueReader baseReader = new(typed.Body);
        Assert.Equal(10, baseReader.ReadVarInt32());
        uint baseWord = baseReader.ReadVarUInt32();
        Assert.Equal((uint)node.Padding.Length, baseReader.ReadVarUInt32());
        Assert.Equal(new byte[node.Padding.Length], baseReader.ReadRawBytes(node.Padding.Length).ToArray());
        baseReader.EnsureFullyConsumed();
        BareValueReader deltaReader = new(delta.Body);
        Assert.Equal(0u, deltaReader.ReadVarUInt32());
        Assert.Equal(21, deltaReader.ReadVarInt32());
        Assert.Equal(1u, deltaReader.ReadVarUInt32());
        uint deltaWord = deltaReader.ReadVarUInt32();
        deltaReader.EnsureFullyConsumed();
        Assert.NotEqual(0u, baseWord);
        Assert.NotEqual(0u, deltaWord);
        Assert.NotEqual(baseWord, deltaWord);

        CommitId? missing = null;
        if (scalarWords.HasValue) {
            Assert.DoesNotContain(baseWord, original.Business.ObjectHeads.Keys);
            Assert.DoesNotContain(deltaWord, graph.ObjectHeads.Keys);
        } else {
            Assert.Contains(baseWord, original.Business.ObjectHeads.Keys);
            Assert.Contains(deltaWord, graph.ObjectHeads.Keys);
            using var remove = MechanismFixture.Prepare(store, "node", StoredGraphKind.State,
                new(graph.Address, graph.RootId, [], [deltaWord]));
            var bad = store.Commit(remove);
            Assert.DoesNotContain(deltaWord, bad.Business.ObjectHeads.Keys);
            Assert.Equal(graph.ObjectHeads[delta.ObjectId], bad.Business.ObjectHeads[delta.ObjectId]);
            missing = new CommitId(bad.CommitId);
        }
        return new(new(original.CommitId), new(changed.CommitId), missing, schema, typed.Body, delta.Body.ToArray(), (baseWord, deltaWord));
    }

    private static int SignedValue(uint zigZag) => (int)(zigZag >> 1) ^ -(int)(zigZag & 1);

    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}

[DurableNode("Probe.RetiredNodeEdge", 1)]
public sealed class EqualBytesNodeV1 {
    public int Value;
    public int Number;
    public string? Link;
    // Unchanged inline nulls make the real planner select a Delta for the two changed words.
    public string?[] Padding = new string?[32];
}
