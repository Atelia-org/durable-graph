using Atelia.Binary;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class RegisteredLeafRegistrationTests {
    [Fact]
    public void LeafDeclarationRequiresAUsablePersistentIdentity() {
        Assert.Throws<ArgumentNullException>("id", () => new DurableLeafAttribute(null!));
        Assert.Throws<ArgumentException>("id", () => new DurableLeafAttribute(" "));
        Assert.Throws<ArgumentException>("id", () => new DurableLeafAttribute("leaf" + (char)0xd800));
        Assert.Throws<ArgumentOutOfRangeException>("version", () => new DurableLeafAttribute("例.🐈", 0));
        var valid = new DurableLeafAttribute("例.🐈");
        Assert.Equal("例.🐈", valid.Id);
        Assert.Equal(1, valid.Version);
    }

    [Fact]
    public void CatalogRejectsAReservedLeafAtRegistration() {
        StateModelRegistry registry = new();
        Assert.Throws<NotSupportedException>(() => registry.RegisterLeaf(new Codec<ReservedLeaf>()));
        Assert.False(registry.CreateEnvironment().TryGetCurrentObjectBinding(typeof(ReservedLeaf), out _));
    }

    [Fact]
    public void InvalidHistoricalVersionsLeaveNoPartiallyRegisteredReaders() {
        StateModelRegistry registry = new();
        Codec<RegistrationLeaf> codec = new();
        foreach (int[]? invalid in new int[]?[] { null, [1, 1], [1, 0], [1, 3], [1, 4] }) {
            codec.Versions = invalid;
            Assert.Throws<ArgumentException>("codec", () => registry.RegisterLeaf(codec));
            var empty = registry.CreateEnvironment();
            Assert.False(empty.TryGetCurrentObjectBinding(typeof(RegistrationLeaf), out _));
            Assert.Empty(empty.Readers);
        }

        int[] versions = [1];
        codec.Versions = versions;
        registry.RegisterLeaf(codec);
        versions[0] = 2;
        registry.RegisterLeaf(codec);
        Assert.Throws<InvalidOperationException>(() => registry.RegisterLeaf(new Codec<RegistrationLeaf>()));
        var models = registry.CreateEnvironment();
        Assert.Equal(1, models.ResolveReader(new DurableSchema("Registration.Leaf", 1)).Schema.Version);
        Assert.Throws<InvalidDataException>(() => models.ResolveReader(new DurableSchema("Registration.Leaf", 2)));
        Assert.Equal(3, models.ResolveReader(new DurableSchema("Registration.Leaf", 3)).Schema.Version);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARegisteredReferenceLeafCannotSupplyAnInlineOrNullableValue(bool nullable) {
        StateModelRegistry registry = new();
        registry.RegisterLeaf(new Codec<RegistrationLeaf>());
        var models = registry.CreateEnvironment();
        TypeExpr type = models.GetTypeExpr(typeof(RegistrationLeaf));
        Assert.Throws<InvalidDataException>(() => models.GetDomainType(TypeExpr.Nullable(type)));
        DurableSchema forged = new(type, 3, SchemaKind.InlineValue);
        DurableFieldInfo inline = new(1, TypeTag.InlineValue, inlineSchema: forged);
        DurableFieldInfo element = nullable ? DurableFieldInfo.Nullable(1, inline) : inline;
        Assert.Throws<InvalidDataException>(() => models.ResolveObjectReader(ObjectLayout.ForList(new(element))));
        Assert.Equal(TypeTag.ObjectReference, models.ResolveCurrentValue(typeof(RegistrationLeaf)).Slot.TypeTag);
    }

    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 1)]
    public void CurrentBindingAndHistoricalReaderRetainExactViewChecks(bool reader, int version) {
        StateModelRegistry registry = new();
        registry.RegisterLeaf(new Codec<RegistrationLeaf> { Versions = [1] });
        DurableSchema expected = new("Registration.Leaf", version);
        DurableSchema different = new(expected.SchemaId, version, new DurableFieldInfo(1, TypeTag.Byte));
        var good = registry.CreateEnvironment(key => key.Version == version ? expected : null);
        var bad = registry.CreateEnvironment(key => key.Version == version ? different : null);
        void Bind(ModelExecutionEnvironment models) {
            if (reader) { models.ResolveReader(expected); }
            else { Assert.True(models.TryGetCurrentObjectBinding(typeof(RegistrationLeaf), out _)); }
        }
        Bind(good);
        using var parent = good.BeginSchemaRequirementCollection();
        using (var child = good.BeginSchemaRequirementCollection()) {
            Bind(good);
            child.Complete();
        }
        var requirements = parent.Complete();
        Assert.Equal(1, requirements.Count);
        Assert.Throws<InvalidDataException>(() => requirements.Validate(bad));
        Assert.Throws<InvalidDataException>(() => Bind(bad));
    }

    private sealed class Codec<T> : DurableLeafCodec<T, byte> where T : class, new() {
        internal int[]? Versions = [];
        public override IReadOnlyList<int> HistoricalVersions => Versions!;
        public override byte Capture(T value, LeafAcceptance? acceptance) => 0;
        public override T Restore(byte state) => new();
        public override PreparedBaseBody PrepareBase(byte state) => new([state]);
        public override byte ReadBase(int version, ref BareValueReader reader) => reader.ReadByte();
    }
}

[DurableLeaf("Registration.Leaf", 3)]
public sealed class RegistrationLeaf { }

[DurableLeaf("P0C")]
public sealed class ReservedLeaf { }
