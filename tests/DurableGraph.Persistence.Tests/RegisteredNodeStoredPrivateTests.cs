using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using static Atelia.DurableGraph.Persistence.Tests.RegisteredNodeHistoricalReferenceTests;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class RegisteredNodeStoredPrivateTests : IDisposable {
    private const string Prefix = "dg-stored-private-";
    private const string Format = "probe.rating-byte";
    private const byte Rejected = 241;
    private static readonly TypeExpr NodeType = TypeExpr.Named("Probe.StoredPrivate");
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));

    [Fact]
    public void AcceptedVersionReadsEachOldPrivateValueWithTheCurrentValidator() {
        GraphRef latest = SaveHistory(3, 5, 7);
        var trace = new Trace();
        using var repo = Repository.OpenReadOnlyExisting(_path, Models<StoredPrivateV2>(trace, [1]));
        using var graph = repo.OpenGraph(latest);
        var node = Assert.IsType<NodeReviewRoot<StoredPrivateV2>>(graph.Root).Value;
        Assert.Equal(7, node.Rating);
        Assert.Equal(128, node.Padding.Length);
        // The bounded reader validates Base + both Deltas; hydration decodes the last value once more.
        Assert.Equal(new byte[] { 3, 5, 7, 7 }, trace.Reads);
        Assert.Equal(1, trace.RootAllocations);
        Assert.Equal(1, trace.Allocations);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("probe.other-rating-format", true)]
    [InlineData(Format, false)]
    public void AdmissionRequiresAnAcceptedVersionAndAnAvailableSamePositionValidator(string? format, bool acceptsOld) {
        GraphRef latest = SaveHistory(3, 5, 7);
        var trace = new Trace();
        using var repo = Repository.OpenReadOnlyExisting(_path, Models<StoredPrivateV2>(trace, acceptsOld ? [1] : [], format));
        Assert.Throws<InvalidDataException>(() => repo.OpenGraph(latest));
        Assert.Empty(trace.Reads);
        Assert.Equal(0, trace.RootAllocations);
        Assert.Equal(0, trace.Allocations);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LaterValidDeltaCannotConcealRejectedPrivateBaseOrIntermediateDelta(bool invalidBase) {
        byte first = invalidBase ? Rejected : (byte)3;
        byte second = invalidBase ? (byte)5 : Rejected;
        GraphRef latest = SaveHistory(first, second, 7);
        // The earlier author accepts the byte. This fixture changes validation under the same format ID.
        using (var control = Repository.OpenReadOnlyExisting(_path, Models<StoredPrivateV1>(new(), []))) {
            using var graph = control.OpenGraph(latest);
            Assert.Equal(7, Assert.IsType<NodeReviewRoot<StoredPrivateV1>>(graph.Root).Value.Rating);
        }
        var trace = new Trace();
        using var repo = Repository.OpenReadOnlyExisting(_path, Models<StoredPrivateV2>(trace, [1], rejectValue: true));
        var error = Assert.Throws<InvalidDataException>(() => repo.OpenGraph(latest));
        Assert.Equal("Private rating 241 is forbidden.", error.Message);
        Assert.Equal(invalidBase ? new byte[] { Rejected } : new byte[] { 3, Rejected }, trace.Reads);
        Assert.Equal(0, trace.RootAllocations);
        Assert.Equal(0, trace.Allocations);
    }

    [Fact]
    public void AnAcceptedVersionStillRequiresAStoredSlotDescriptor() {
        GraphRef saved;
        var source = RootModels<StoredPrivateOpaqueV1>(target: NodeType);
        source.RegisterLeaf(new OpaqueV1());
        using (var writer = Repository.CreateNew(_path, source)) {
            using var graph = writer.CreateGraph(new NodeReviewRoot<StoredPrivateOpaqueV1> { Value = new() { Rating = 9 } });
            saved = writer.SaveGraph(graph);
            var revision = writer.GraphStore.ReadBusinessGraph(DB096TestAccess.Resources(writer).ReadHeapDescriptor(saved.CommitId.Value).Address);
            var node = Assert.Single(graph.Business.Baseline!.CurrentDtos.Values, row => row.Schema?.Type == NodeType);
            var record = Assert.Single(writer.GraphStore.ReadObjectChain(revision, node.Id.Value).Entries).Record;
            var catalog = SchemaCatalogView.Load(writer.GraphStore, revision.SchemaRoot!.Value);
            var stored = TypedBaseBodyCodec.Decode(record.Body, catalog);
            Assert.False(Assert.IsType<DurableSchema>(stored.Layout.Schema).IsDeclaredNode);
            Assert.Equal(new byte[] { 0, 1, 9 }, stored.Body);
        }
        using (var control = Repository.OpenReadOnlyExisting(_path, source)) {
            using var graph = control.OpenGraph(saved);
            Assert.Equal(9, Assert.IsType<NodeReviewRoot<StoredPrivateOpaqueV1>>(graph.Root).Value.Rating);
        }
        var trace = new Trace();
        using var reader = Repository.OpenReadOnlyExisting(_path, Models<StoredPrivateV2>(trace, [1]));
        Assert.Throws<InvalidDataException>(() => reader.OpenGraph(saved));
        Assert.Empty(trace.Reads);
        Assert.Equal(0, trace.RootAllocations);
        Assert.Equal(0, trace.Allocations);
    }

    private GraphRef SaveHistory(byte first, byte second, byte last) {
        using var repo = Repository.CreateNew(_path, Models<StoredPrivateV1>(new(), []));
        // An unchanged vector makes each private-only edit smaller than a fresh Base.
        var node = new StoredPrivateV1 { Rating = first, Padding = new string?[128] };
        using var graph = repo.CreateGraph(new NodeReviewRoot<StoredPrivateV1> { Value = node });
        repo.SaveGraph(graph);
        node.Rating = second;
        repo.SaveGraph(graph);
        node.Rating = last;
        GraphRef latest = repo.SaveGraph(graph);
        var saved = repo.GraphStore.ReadBusinessGraph(DB096TestAccess.Resources(repo).ReadHeapDescriptor(latest.CommitId.Value).Address);
        var id = Assert.Single(graph.Business.Baseline!.CurrentDtos.Values, row => row.Schema?.Type == NodeType).Id;
        var chain = repo.GraphStore.ReadObjectChain(saved, id.Value).Entries;
        Assert.Equal(new[] { ObjectVersionKind.Base, ObjectVersionKind.Delta, ObjectVersionKind.Delta }, chain.Select(row => row.Record.Kind));
        Assert.Equal(new byte[] { 1, 1, second }, chain[1].Record.Body);
        Assert.Equal(new byte[] { 1, 1, last }, chain[2].Record.Body);
        return latest;
    }

    private static StateModelRegistry Models<T>(Trace trace, int[] accepted, string? format = Format, bool rejectValue = false)
        where T : StoredPrivateValues, new() {
        var models = RootModels<T>(trace, NodeType);
        List<DeclaredMember<T>> members = [
            DeclaredMember<T>.References<string>(TypeExpr.Builtin(TypeTag.String), n => n.Padding, (n, a) => n.Padding = a),
        ];
        if (format is not null) {
            var body = new DeclaredPrivateFormat<byte>(format, 0, static (ref BareValueWriter w, byte v) => w.WriteByte(v),
                (ref BareValueReader r) => {
                    byte value = r.ReadByte();
                    trace.Reads.Add(value);
                    if (rejectValue && value == Rejected) { throw new InvalidDataException("Private rating 241 is forbidden."); }
                    return value;
                });
            members.Add(DeclaredMember<T>.Private(body, n => n.Rating, (n, rating) => n.Rating = rating));
        }
        new DeclaredNode<T>(() => { trace.Allocations++; return new(); }, members.ToArray(), accepted).Register(models);
        trace.Reads.Clear(); // Exclude the constructor's default-value validation.
        return models;
    }

    private sealed class Trace : ReadTrace {
        internal List<byte> Reads { get; } = [];
    }

    // Same raw bytes as a declared empty reference vector followed by the framed one-byte private slot.
    private sealed class OpaqueV1 : DurableLeafCodec<StoredPrivateOpaqueV1, byte> {
        public override byte Capture(StoredPrivateOpaqueV1 value, LeafAcceptance? acceptance) => value.Rating;
        public override StoredPrivateOpaqueV1 Restore(byte state) => new() { Rating = state };
        public override PreparedBaseBody PrepareBase(byte state) => new([0, 1, state]);
        public override byte ReadBase(int version, ref BareValueReader r) {
            if (r.ReadByte() != 0 || r.ReadByte() != 1) { throw new InvalidDataException(); }
            return r.ReadByte();
        }
    }

    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}

public abstract class StoredPrivateValues {
    public byte Rating;
    public string?[] Padding = [];
}

[DurableNode("Probe.StoredPrivate", 1)]
public sealed class StoredPrivateV1 : StoredPrivateValues { }

[DurableNode("Probe.StoredPrivate", 2)]
public sealed class StoredPrivateV2 : StoredPrivateValues { }

[DurableLeaf("Probe.StoredPrivate", 1)]
public sealed class StoredPrivateOpaqueV1 : StoredPrivateValues { }
