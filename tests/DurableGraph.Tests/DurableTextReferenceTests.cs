using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;

namespace Atelia.DurableGraph.Tests;

public sealed class DurableTextReferenceTests {
    [Fact]
    public void TextReferencesRequireTheirExactTargetAtEveryEdge() {
        TypeExpr text = TypeExpr.Text;
        Assert.Equal(TypeTag.ObjectReference, DurableFieldInfo.Reference(1, text).TypeTag);
        Assert.Same(text, DurableFieldInfo.NominalType(DurableFieldInfo.Reference(1, text)));
        Assert.Throws<ArgumentException>(() => TypeExpr.Nullable(text));

        ObjectId id = new(7);
        StateReferenceValidator validator = new(new Dictionary<ObjectId, ObjectStateRecord> {
            [id] = new(id, "plain string")
        });
        validator.VisitString(id);
        validator.VisitObject(default, text);
        Assert.Throws<InvalidDataException>(() => validator.VisitObject(id, text));
        Assert.True(StateReferenceValidator.Accepts(ObjectLayout.Text, text));
        Assert.False(StateReferenceValidator.Accepts(ObjectLayout.Text, TypeExpr.Builtin(TypeTag.String)));
        Assert.False(StateReferenceValidator.Accepts(ObjectLayout.Text, TypeExpr.Named("DurableText")));
    }

    [Fact]
    public void AVisitorWithoutTextSupportRejectsTheNewFamily() {
        IStateReferenceVisitor visitor = new StringAndNamedVisitor();
        Assert.Throws<NotSupportedException>(() => visitor.VisitObject(new(1), TypeExpr.Text));
    }

    private sealed class StringAndNamedVisitor : IStateReferenceVisitor {
        public void VisitString(ObjectId id) => throw new InvalidOperationException("A text reference was routed to string.");
        public void VisitDurable(ObjectId id, string nominalSchemaId) => throw new InvalidOperationException("A text reference was routed to a named family.");
    }
}
