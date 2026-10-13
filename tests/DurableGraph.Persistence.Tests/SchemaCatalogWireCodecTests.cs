using Atelia.DurableGraph.Schema;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class SchemaKeyTests {
    [Fact]
    public void SchemaKeyRequiresANominalIdentityAndAPositiveVersion() {
        Assert.Throws<ArgumentException>(() => new SchemaKey(" ", 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SchemaKey("A", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SchemaKey("A", -1));
    }
}
