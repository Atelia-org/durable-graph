using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class DeclaredNodeAppendTests : IDisposable {
    private const string Prefix = "dg-node-appended-slots-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
    private static readonly TypeExpr StringType = TypeExpr.Builtin(TypeTag.String);

    [Fact]
    public void OnlyTheRetainedPrefixComesFromTheOldStoreAndEveryAppendedMemberGetsItsDeclaredDefault() {
        GraphRef saved = SaveOld("kept");
        byte[] initial = [7, 8];
        var bytes = new DeclaredPrivateFormat<byte[]>("Test.Bytes", initial,
            static (ref BareValueWriter writer, byte[] data) => writer.WriteRawBytes(data),
            static (ref BareValueReader reader) => reader.ReadRawBytes(reader.RemainingCount).ToArray());
        DeclaredMember<AppendedSlotsV2>[] members = [
            DeclaredMember<AppendedSlotsV2>.Reference<string>(StringType, n => n.Caption, (n, v) => n.Caption = v),
            DeclaredMember<AppendedSlotsV2>.Int32(n => n.Score, (n, v) => n.Score = v),
            DeclaredMember<AppendedSlotsV2>.Reference<string>(StringType, n => n.Optional, (n, v) => n.Optional = v),
            DeclaredMember<AppendedSlotsV2>.References<string>(StringType, n => n.Tags, (n, v) => n.Tags = v),
            DeclaredMember<AppendedSlotsV2>.Private(bytes, n => n.Bytes, (n, v) => n.Bytes = v),
        ];
        int[] accepted = [1];
        var current = new DeclaredNode<AppendedSlotsV2>(static () => new(), members, accepted);
        initial[0] = 99;
        members[0] = null!;
        accepted[0] = 0;
        using var reader = Repository.OpenReadOnlyExisting(_path, Models(current));
        using var left = reader.OpenGraph(saved);
        using var right = reader.OpenGraph(saved);
        var one = Assert.IsType<TextWitnessBox<AppendedSlotsV2>>(left.Root).Value;
        var two = Assert.IsType<TextWitnessBox<AppendedSlotsV2>>(right.Root).Value;
        Assert.Equal("kept", one.Caption);
        Assert.Equal(0, one.Score);
        Assert.Null(one.Optional);
        Assert.Empty(one.Tags);
        Assert.Equal(new byte[] { 7, 8 }, one.Bytes);
        one.Bytes[0] = 23;
        Assert.Equal(new byte[] { 7, 8 }, two.Bytes);
    }

    [Fact]
    public void AcceptingAnOldVersionDoesNotChangeThePersistentTargetOfAnExistingSlotEvenWhenItHoldsNull() {
        GraphRef saved = SaveOld(null);
        int allocations = 0;
        var current = new DeclaredNode<AppendedSlotsV2>(() => { allocations++; return new(); }, [
            DeclaredMember<AppendedSlotsV2>.Reference<List<string>>(TypeExpr.List(StringType), static _ => null, static (_, _) => { }),
        ], 1);
        using var reader = Repository.OpenReadOnlyExisting(_path, Models(current));
        Assert.Throws<InvalidDataException>(() => reader.OpenGraph(saved));
        Assert.Equal(0, allocations);
    }

    private GraphRef SaveOld(string? caption) {
        var old = new DeclaredNode<AppendedSlotsV1>(static () => new(), [
            DeclaredMember<AppendedSlotsV1>.Reference<string>(StringType, n => n.Caption, (n, v) => n.Caption = v),
        ]);
        using var repo = Repository.CreateNew(_path, Models(old));
        using var graph = repo.CreateGraph(new TextWitnessBox<AppendedSlotsV1> { Value = new() { Caption = caption } });
        return repo.SaveGraph(graph);
    }

    private static StateModelRegistry Models<T>(DeclaredNode<T> node) where T : class {
        var registry = new StateModelRegistry();
        registry.Register(global::Atelia.DurableGraph.Generated.Family_546578745769746E657373426F78.Definition);
        node.Register(registry);
        return registry;
    }

    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}

[DurableNode("Test.AppendedSlots", 1)]
public sealed class AppendedSlotsV1 {
    public string? Caption;
}

[DurableNode("Test.AppendedSlots", 2)]
public sealed class AppendedSlotsV2 {
    public string? Caption;
    public int Score = -1;
    public string? Optional = "constructor";
    public string?[] Tags = ["constructor"];
    public byte[] Bytes = [99];
}
