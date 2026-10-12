using Atelia.DurableGraph.Schema;
using Atelia.Binary;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class InlineSchemaStoreTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"durable-inline-schema-{Guid.NewGuid():N}");
    private int _sequence;

    public InlineSchemaStoreTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void IndependentGoldenBindsExactPrecedingInlineSchemaByIntegerId() {
        DurableSchema value = new("Z", 2, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.Int32));
        DurableSchema owner = new("A", 1, new DurableFieldInfo(1, TypeTag.InlineValue, inlineSchema: value));
        byte[] golden = Convert.FromHexString("0202020202035A000200010102030102034100010001011002");
        Assert.Equal(golden, SchemaCatalogWireCodec.Write(CatalogTestData.Schemas([value, owner])));
        var read = SchemaCatalogWireCodec.Read(golden);
        Assert.Equal(owner, read[1].Schema);
        Assert.Equal(SchemaKind.ReferenceObject, read[1].Schema!.Kind);
        Assert.Equal(SchemaKind.InlineValue, read[0].Schema!.Kind);
        Assert.Null(read[0].Layout);
        Assert.Same(read[0].Schema, read[1].Schema!.Fields[0].InlineSchema);
        for (int length = 0; length < golden.Length; length++) {
            byte[] prefix = golden[..length];
            Exception? error = Record.Exception(() => SchemaCatalogWireCodec.Read(prefix));
            Assert.True(error is InvalidDataException or EndOfStreamException, $"Cut {length}: {error}");
        }
    }

    [Fact]
    public void BaseAndInlineClosureIsRegisteredAtomicallyAndSharedOnReopen() {
        DurableSchema point = new("Point", 1, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.String));
        DurableSchema pair = new("Pair", 1, SchemaKind.InlineValue,
            new DurableFieldInfo(1, TypeTag.InlineValue, inlineSchema: point),
            new DurableFieldInfo(2, TypeTag.InlineValue, inlineSchema: point),
            new DurableFieldInfo(3, TypeTag.ObjectReference, "Owner"));
        DurableSchema ancestor = new("Ancestor", 1, new DurableFieldInfo(1, TypeTag.InlineValue, inlineSchema: point));
        DurableSchema owner = new("Owner", 1, [new(1, TypeTag.InlineValue, inlineSchema: pair)], ancestor);
        string path = NextPath();
        using (GraphResources file = GraphResources.CreateLegacyNew(path)) {
            SchemaStore store = file.Schemas;
            store.Register(owner);
            Assert.Equal(4, store.Count);
            long tail = DataBytes(file.Data);
            store.Register(owner);
            Assert.Equal(tail, DataBytes(file.Data));
            DurableSchema changedPoint = new("Point", 1, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.Int32));
            DurableSchema changedOwner = new("Fresh", 1, new DurableFieldInfo(1, TypeTag.InlineValue, inlineSchema: changedPoint));
            Assert.Throws<SchemaConflictException>(() => store.RegisterBatch([new("Independent", 1), changedOwner]));
            Assert.Equal(tail, DataBytes(file.Data));
            Assert.Equal(4, store.Count);
            Assert.False(store.IsFaulted);
        }
        using GraphResources reopened = GraphResources.OpenLegacyReadOnlyExisting(path);
        SchemaStore restored = reopened.Schemas;
        Assert.Equal(owner, restored.GetRequired("Owner", 1));
        Assert.Same(restored.GetRequired("Point", 1), restored.GetRequired("Ancestor", 1).Fields[0].InlineSchema);
        Assert.Same(restored.GetRequired("Point", 1), restored.GetRequired("Pair", 1).Fields[0].InlineSchema);
        Assert.Same(restored.GetRequired("Point", 1), restored.GetRequired("Pair", 1).Fields[1].InlineSchema);
    }

    [Fact]
    public void InlineVersionChangeConflictsUnlessOwnerAlsoChangesVersion() {
        DurableSchema value1 = new("Value", 1, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.Int32));
        DurableSchema value2 = new("Value", 2, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.Int64));
        DurableSchema owner1 = new("Owner", 1, new DurableFieldInfo(1, TypeTag.InlineValue, inlineSchema: value1));
        DurableSchema wrong = new("Owner", 1, new DurableFieldInfo(1, TypeTag.InlineValue, inlineSchema: value2));
        DurableSchema owner2 = new("Owner", 2, new DurableFieldInfo(1, TypeTag.InlineValue, inlineSchema: value2));
        using GraphResources file = GraphResources.CreateLegacyNew(NextPath());
        SchemaStore store = file.Schemas;
        store.Register(owner1);
        long tail = DataBytes(file.Data);
        Assert.Throws<SchemaConflictException>(() => store.Register(wrong));
        Assert.Equal(tail, DataBytes(file.Data));
        Assert.Equal(2, store.Count);
        store.Register(owner2);
        Assert.Equal(4, store.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FamilyKindCannotChangeWithinOrAcrossRegistrationBatches(bool sameBatch) {
        DurableSchema reference = new("A", 1);
        DurableSchema inline = new("A", 2, SchemaKind.InlineValue);
        using GraphResources file = GraphResources.CreateLegacyNew(NextPath());
        SchemaStore store = file.Schemas;
        if (!sameBatch) { store.Register(reference); }
        long tail = DataBytes(file.Data);
        Assert.Throws<ArgumentException>(() => store.RegisterBatch(sameBatch ? [reference, inline] : [inline]));
        Assert.Equal(tail, DataBytes(file.Data));
        Assert.Equal(sameBatch ? 0 : 1, store.Count);
        Assert.False(store.IsFaulted);
        byte[] snapshot = CatalogTestData.Encode(CatalogTestData.Schemas([reference, inline]));
        Assert.Throws<InvalidDataException>(() => SchemaCatalogWireCodec.Read(snapshot));
    }

    [Theory]
    [InlineData("02010300000000")] // Kind zero.
    [InlineData("02010305000000")] // Unknown kind.
    [InlineData("0201030202034100010200")] // Inline Schema has a base.
    [InlineData("0202030202034100010000040102034200010300")] // Reference base names inline kind.
    [InlineData("0201030102034100010001011002")] // Inline slot names registered reference kind.
    [InlineData("0201030102034100010001011063")] // Missing exact inline dependency.
    [InlineData("0201030202034100010001011003")] // Inline self cycle.
    [InlineData("0202030202034100010001011004040202034200010001011003")] // Forward edge in two-node cycle.
    public void MalformedInlineLayoutsFailBeforeMerging(string hex) {
        byte[] fragment = Convert.FromHexString(hex);
        byte[] existing = CatalogTestData.Encode(CatalogTestData.Schemas([new("Existing", 1)]));
        byte[] complete = [2, (byte)(fragment[1] + 1), .. existing.AsSpan(2), .. fragment.AsSpan(2)];
        Assert.Throws<InvalidDataException>(() => SchemaCatalogWireCodec.Read(complete));
    }

    [Fact]
    public void InlineDepthLimitIncludesCachedAndPreviouslyRegisteredDependencies() {
        DurableSchema[] chain = InlineChain(257);
        var atLimit = chain[..256];
        Assert.Equal(256, SchemaCatalogWireCodec.Read(SchemaCatalogWireCodec.Write(CatalogTestData.Schemas(atLimit))).Length);
        Assert.Throws<InvalidDataException>(() => SchemaCatalogWireCodec.Read(CatalogTestData.Encode(CatalogTestData.Schemas(chain))));
        using GraphResources file = GraphResources.CreateLegacyNew(NextPath());
        SchemaStore store = file.Schemas;
        store.Register(chain[255]);
        long tail = DataBytes(file.Data);
        Assert.Throws<ArgumentException>(() => store.Register(chain[256]));
        Assert.Equal(tail, DataBytes(file.Data));
        Assert.Equal(256, store.Count);
        Assert.False(store.IsFaulted);
    }

    [Fact]
    public void InlineSchemaCannotIdentifyObjectBaseOrBeReadAsAnObject() {
        DurableSchema inline = new("A", 1, SchemaKind.InlineValue);
        Assert.Throws<ArgumentException>(() => ObjectLayout.ForDurable(inline));
        using GraphResources file = GraphResources.CreateLegacyNew(NextPath());
        SchemaStore schemas = file.Schemas;
        schemas.Register(inline);
        Assert.Throws<InvalidDataException>(() => schemas.GetRepresentation(new(2)));
        Assert.Throws<InvalidDataException>(() => schemas.ResolveReader(new(2), new StateReaderRegistry().Snapshot(schemas)));
        Assert.Throws<InvalidDataException>(() => BaseObjectBodyCodec.Decode([4, 2], schemas));
        Assert.Throws<InvalidDataException>(() => TypedObjectVersionReader.MatchSchema(schemas.GetRequired("A", 1), inline));
        Assert.Throws<InvalidDataException>(() => TypedObjectVersionReader.MatchSchema(schemas.GetRequired("A", 1), new("A", 1)));
    }

    [Fact]
    public void ChangedNestedDefinitionRejectsTypedReadBeforeBodyCallback() {
        DurableSchema value = new("Value", 1, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.Byte));
        DurableSchema changed = new("Value", 1, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.SByte));
        DurableSchema owner = new("Owner", 1, new DurableFieldInfo(1, TypeTag.InlineValue, inlineSchema: value));
        DurableSchema wrong = new("Owner", 1, new DurableFieldInfo(1, TypeTag.InlineValue, inlineSchema: changed));
        using GraphResources file = GraphResources.CreateLegacyNew(NextPath());
        SchemaStore schemas = file.Schemas;
        schemas.Register(owner);
        StateRevisionStore states = file.States;
        FrameAddress address = states.Append(StateRevision.CreateObjectHeadMapBase(null,
            [ObjectVersionRecord.CreateBase(1, BaseObjectBodyCodec.Encode(schemas.RegisterRepresentations([ObjectLayout.ForDurable(owner)])[0], new([1])).Body)], []));
        ObjectVersionChain chain = states.ReadObjectVersionChain(address, 1);
        int calls = 0;
        byte Read(ref BareValueReader reader) { calls++; return reader.ReadByte(); }
        byte Apply(ref BareValueReader reader, in byte previous) { calls++; return previous; }
        Assert.Throws<InvalidDataException>(() => TypedObjectVersionReader.ReadDurable<byte>(chain, schemas, wrong, Read, Apply));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void PublishedBasePointingAtInlineSchemaRefusesSelectedGraphRead() {
        string repositoryPath = NextPath();
        using (GraphResources resources = GraphResources.CreateLegacyNew(repositoryPath)) {
            resources.Schemas.Register(new("A", 1, SchemaKind.InlineValue));
            // Valid Base envelope grammar, but its selected ID identifies an inline value.
            FrameAddress address = resources.States.Append(StateRevision.CreateObjectHeadMapBase(null,
                [ObjectVersionRecord.CreateBase(1, new byte[] { 4, 2 })], []));
            resources.Data.ConfirmDurable();
            Assert.Throws<InvalidDataException>(() => RevisionDecoder.Read(resources.States, resources.Schemas,
                address, new StateReaderRegistry()));
        }
        using GraphResources reopened = GraphResources.OpenLegacyReadOnlyExisting(repositoryPath);
        Assert.Equal(1, reopened.Schemas.Count);
    }

    private static DurableSchema[] InlineChain(int length) {
        DurableSchema[] chain = new DurableSchema[length];
        for (int index = 0; index < length; index++) {
            chain[index] = new($"Value{index:D3}", 1, SchemaKind.InlineValue,
                index == 0 ? [] : [new(1, TypeTag.InlineValue, inlineSchema: chain[index - 1])]);
        }
        return chain;
    }

    private string NextPath() => Path.Combine(_root, (++_sequence).ToString(System.Globalization.CultureInfo.InvariantCulture));

    public void Dispose() {
        string resolved = Path.GetFullPath(_root);
        if (!string.Equals(Path.GetDirectoryName(resolved), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(resolved).StartsWith("durable-inline-schema-", StringComparison.Ordinal)) {
            throw new InvalidOperationException("Refusing to delete outside the fixture temporary directory.");
        }
        Directory.Delete(resolved, recursive: true);
    }
}
