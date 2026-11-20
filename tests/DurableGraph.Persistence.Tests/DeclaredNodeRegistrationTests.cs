using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class DeclaredNodeRegistrationTests {
    [Fact]
    public void RepeatedRegistrationSharesOneDeclarationAndAnEarlierSnapshotKeepsItsOwnRegistry() {
        var registry = new StateModelRegistry();
        var before = registry.FreezeConfiguration();
        var node = new DeclaredNode<GraphMember>(static () => new(), []);
        registry.RegisterNode(node);
        node.Register(registry);
        Assert.Throws<InvalidOperationException>(() => registry.RegisterNode(new DeclaredNode<GraphMember>(static () => new(), [])));

        Assert.False(before.CreateEnvironment().TryGetCurrentObjectBinding(typeof(GraphMember), out _));
        var after = registry.CreateEnvironment();
        Assert.True(after.TryGetCurrentObjectBinding(typeof(GraphMember), out var binding));
        Assert.True(binding!.CurrentLayout.Schema!.IsDeclaredNode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LeafAndNodeCannotClaimTheSameFamilyInEitherOrder(bool nodeFirst) {
        var registry = new StateModelRegistry();
        var node = new DeclaredNode<GraphMember>(static () => new(), []);
        var leaf = new ConflictingLeafCodec();
        if (nodeFirst) {
            registry.RegisterNode(node);
            Assert.Throws<InvalidOperationException>(() => registry.RegisterLeaf(leaf));
        } else {
            registry.RegisterLeaf(leaf);
            Assert.Throws<InvalidOperationException>(() => registry.RegisterNode(node));
        }
        var models = registry.CreateEnvironment();
        Assert.Equal(nodeFirst, models.TryGetCurrentObjectBinding(typeof(GraphMember), out _));
        Assert.Equal(!nodeFirst, models.TryGetCurrentObjectBinding(typeof(ConflictingGraphMemberLeaf), out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AGeneratedDefinitionAndANodeCannotClaimTheSameFamilyInEitherOrder(bool nodeFirst) {
        var registry = new StateModelRegistry();
        var node = new DeclaredNode<NodeWithGeneratedFamily>(static () => new(), []);
        var definition = global::Atelia.DurableGraph.Generated.Family_546578745769746E657373.Definition;
        if (nodeFirst) {
            registry.RegisterNode(node);
            Assert.Throws<InvalidOperationException>(() => registry.Register(definition));
        } else {
            registry.Register(definition);
            Assert.Throws<InvalidOperationException>(() => registry.RegisterNode(node));
        }
        Assert.Equal(nodeFirst, registry.CreateEnvironment().TryGetCurrentObjectBinding(typeof(NodeWithGeneratedFamily), out _));
    }

    [Fact]
    public void AcceptedVersionsMustBePositiveDistinctAndOlderThanCurrent() {
        Assert.Throws<ArgumentNullException>("acceptedVersions", () => new DeclaredNode<AppendedSlotsV2>(static () => new(), [], null!));
        foreach (int[] invalid in new int[][] { [-1], [0], [1, 1], [2], [3] }) {
            Assert.Throws<ArgumentException>("acceptedVersions", () => new DeclaredNode<AppendedSlotsV2>(static () => new(), [], invalid));
        }
    }

    [Fact]
    public void ASealedRegisteredAccessorCannotHoldAnUnrelatedNominalTarget() {
        var registry = new StateModelRegistry();
        new DeclaredNode<GraphAccessorSlots>(static () => new(), [
            DeclaredMember<GraphAccessorSlots>.Reference<GraphMember>(TypeExpr.Named("Test.DeclaredAccessorSlots"),
                _ => null, (_, _) => { }),
        ]).Register(registry);
        new DeclaredNode<GraphMember>(static () => new(), []).Register(registry);
        var models = registry.CreateEnvironment();
        Assert.Throws<InvalidOperationException>(() => models.TryGetCurrentObjectBinding(typeof(GraphAccessorSlots), out _));
    }

    private sealed class ConflictingLeafCodec : DurableLeafCodec<ConflictingGraphMemberLeaf, byte> {
        public override byte Capture(ConflictingGraphMemberLeaf value, LeafAcceptance? acceptance) => 0;
        public override ConflictingGraphMemberLeaf Restore(byte state) => new();
        public override PreparedBaseBody PrepareBase(byte state) => new([state]);
        public override byte ReadBase(int version, ref BareValueReader reader) => reader.ReadByte();
    }
}

// Explicitly conflicting families for host registration tests. The ordinary definitions live in separate type packages in practice.
[DurableLeaf(GraphMember.SchemaId)]
public sealed class ConflictingGraphMemberLeaf { }

[DurableNode("TextWitness")]
public sealed class NodeWithGeneratedFamily { }
