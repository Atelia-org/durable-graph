
using Atelia.DurableGraph.Schema;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class NominalReferenceSchemaWireTests {
    [Fact]
    public void RuntimeOperandIsExclusiveAndRequiredAndSelfReferenceIsNotAnExactDependency() {
        Assert.ThrowsAny<ArgumentException>(() => new DurableFieldInfo(1, TypeTag.ObjectReference));
        Assert.ThrowsAny<ArgumentException>(() => new DurableFieldInfo(1, TypeTag.ObjectReference, " "));
        Assert.ThrowsAny<ArgumentException>(() => new DurableFieldInfo(1, TypeTag.UInt32, "A"));
        DurableSchema self = new("A", 1, new DurableFieldInfo(1, TypeTag.ObjectReference, "A"));
        Assert.Null(self.BaseSchema);
        SchemaCatalogWorkspace workspace = new(static () => { });
        workspace.Set(self);
        SchemaCatalogView prepared = workspace.Prepare().View;
        Assert.Equal(self, Assert.Single(prepared.Schemas).Value);
        Assert.Null(prepared.Schemas[new("A", 1)].BaseSchema);
    }
}
