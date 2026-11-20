using Atelia.DurableGraph.Schema;

namespace Atelia.DurableGraph.Tests;

public sealed class DeclaredSlotSchemaTests {
    private static readonly TypeExpr Node = TypeExpr.Named("Tests.DeclaredNode");
    private static readonly TypeExpr Text = TypeExpr.Builtin(TypeTag.String);
    private static readonly DeclaredSlotShape Value = new(DeclaredSlotKind.Int32);
    private static readonly DeclaredSlotShape Link = new(DeclaredSlotKind.Reference, Text);
    private static readonly DeclaredSlotShape Links = new(DeclaredSlotKind.References, Text);

    [Fact]
    public void ExactIdentityIncludesTheBodyGrammarEvenForEmptyNodes() {
        DurableSchema node = DurableSchema.ForDeclaredNode(Node, 1, []);
        DurableSchema ordinary = new(Node, 1);
        Assert.True(node.IsDeclaredNode);
        Assert.False(ordinary.IsDeclaredNode);
        Assert.Empty(ordinary.DeclaredSlots);
        Assert.Empty(node.DeclaredSlots);
        Assert.Equal(SchemaKind.ReferenceObject, node.Kind);
        Assert.Null(node.BaseSchema);
        Assert.Empty(node.Fields);
        Assert.NotEqual(ordinary, node);
        Assert.NotEqual(node, ordinary);
        Assert.Throws<ArgumentException>("baseSchema", () => new DurableSchema(TypeExpr.Named("Derived"), 1, [], node));
        Assert.Throws<ArgumentException>("inlineSchema", () => new DurableFieldInfo(1, TypeTag.InlineValue, inlineSchema: node));
    }

    [Fact]
    public void StructuralEqualityPreservesPositionMultiplicityTargetAndPrivateFormat() {
        DeclaredSlotShape[] slots = [Value, Link, Links, new(DeclaredSlotKind.Private, formatId: "app.雪")];
        DurableSchema original = DurableSchema.ForDeclaredNode(Node, 1, slots);
        DurableSchema equal = DurableSchema.ForDeclaredNode(TypeExpr.Named("Tests.DeclaredNode"), 1, [
            new(DeclaredSlotKind.Int32), new(DeclaredSlotKind.Reference, TypeExpr.Builtin(TypeTag.String)),
            new(DeclaredSlotKind.References, TypeExpr.Builtin(TypeTag.String)), new(DeclaredSlotKind.Private, formatId: "app.雪"),
        ]);
        slots[0] = Link;
        Assert.Equal(equal, original);
        Assert.Equal(equal.GetHashCode(), original.GetHashCode());
        Assert.Equal(Value, original.DeclaredSlots[0]);

        HashSet<DurableSchema> identities = [original];
        Assert.False(identities.Add(equal));
        Assert.True(identities.Add(DurableSchema.ForDeclaredNode(Node, 1, [.. original.DeclaredSlots.Reverse()])));
        Assert.True(identities.Add(DurableSchema.ForDeclaredNode(Node, 1, original.DeclaredSlots.SetItem(1, Links))));
        Assert.True(identities.Add(DurableSchema.ForDeclaredNode(Node, 1, original.DeclaredSlots.SetItem(1, new(DeclaredSlotKind.Reference, TypeExpr.VectorArray(Text))))));
        Assert.True(identities.Add(DurableSchema.ForDeclaredNode(Node, 1, original.DeclaredSlots.SetItem(3, new(DeclaredSlotKind.Private, formatId: "App.雪")))));
        Assert.True(identities.Add(DurableSchema.ForDeclaredNode(Node, 2, original.DeclaredSlots)));
    }

    [Fact]
    public void ShapeRejectsMissingExtraneousAndIllTypedOperands() {
        Assert.Throws<ArgumentOutOfRangeException>("kind", () => new DeclaredSlotShape((DeclaredSlotKind)99));
        Assert.Throws<ArgumentNullException>("target", () => new DeclaredSlotShape(DeclaredSlotKind.Reference));
        Assert.Throws<ArgumentException>("target", () => new DeclaredSlotShape(DeclaredSlotKind.Int32, Node));
        Assert.Throws<ArgumentException>("target", () => new DeclaredSlotShape(DeclaredSlotKind.References, TypeExpr.Builtin(TypeTag.Int32)));
        Assert.Throws<ArgumentException>("target", () => new DeclaredSlotShape(DeclaredSlotKind.Reference, TypeExpr.List(TypeExpr.Parameter(0))));
        Assert.Throws<ArgumentException>("target", () => new DeclaredSlotShape(DeclaredSlotKind.Reference, TypeExpr.Nullable(TypeExpr.Builtin(TypeTag.Int32))));
        Assert.Throws<ArgumentException>("formatId", () => new DeclaredSlotShape(DeclaredSlotKind.Reference, Node, "private"));
        Assert.Throws<ArgumentException>("target", () => new DeclaredSlotShape(DeclaredSlotKind.Private, Node, "private"));
        Assert.Throws<ArgumentException>("formatId", () => new DeclaredSlotShape(DeclaredSlotKind.Private, formatId: " "));
        Assert.Throws<ArgumentException>("formatId", () => new DeclaredSlotShape(DeclaredSlotKind.Private, formatId: new string((char)0xd800, 1)));
        Assert.Throws<ArgumentNullException>("slots", () => DurableSchema.ForDeclaredNode(Node, 1, null!));
        Assert.Throws<ArgumentException>("slots", () => DurableSchema.ForDeclaredNode(Node, 1, [null!]));
        Assert.Throws<ArgumentOutOfRangeException>("version", () => DurableSchema.ForDeclaredNode(Node, 0, []));
        Assert.Throws<ArgumentException>("type", () => DurableSchema.ForDeclaredNode(TypeExpr.List(Node), 1, []));
        Assert.Throws<ArgumentException>("type", () => DurableSchema.ForDeclaredNode(TypeExpr.Named("Generic", Text), 1, []));
    }
}
