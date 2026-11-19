using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;

namespace Atelia.DurableGraph.Tests;

public sealed class NamedLeafReferenceTests {
    [Fact]
    public void ReferencesToFrozenLeavesUseTheRegisteredNominalIdentity() {
        TypeExpr nominal = TypeExpr.Named("Tests.NamedLeaf");
        DurableFieldInfo slot = DurableFieldInfo.Reference(1, nominal);
        Assert.Equal(TypeTag.ObjectReference, slot.TypeTag);
        Assert.Equal(nominal, DurableFieldInfo.NominalType(slot));

        ObjectId leafId = new(7), stringId = new(8);
        var layout = ObjectLayout.ForDurable(new DurableSchema(nominal, 1));
        var validator = new StateReferenceValidator(new Dictionary<ObjectId, ObjectStateRecord> {
            [leafId] = new(leafId, layout, "frozen"),
            [stringId] = new(stringId, "plain string"),
        });
        validator.VisitObject(default, nominal);
        validator.VisitObject(leafId, nominal);
        validator.VisitString(stringId);
        Assert.Throws<InvalidDataException>(() => validator.VisitObject(stringId, nominal));
        Assert.Throws<InvalidDataException>(() => validator.VisitObject(leafId, TypeExpr.Named("Tests.OtherLeaf")));
        Assert.Throws<InvalidDataException>(() => validator.VisitString(leafId));
    }
}
