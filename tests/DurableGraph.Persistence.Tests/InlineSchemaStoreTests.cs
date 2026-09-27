using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using Atelia.RbfSegmentStore;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class InlineSchemaStoreTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"durable-inline-schema-{Guid.NewGuid():N}");
    private int _sequence;
    private static readonly IReadOnlyDictionary<RepresentationId, SchemaCatalogEntry> Empty = CatalogTestData.Empty;

    public InlineSchemaStoreTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void IndependentGoldenBindsExactPrecedingInlineSchemaByIntegerId() {
        DurableSchema value = new("Z", 2, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.Int32));
        DurableSchema owner = new("A", 1, new DurableFieldInfo(1, TypeTag.InlineValue, inlineSchema: value));
        byte[] golden = Convert.FromHexString("0202020202035A000200010102030102034100010001011002");
        Assert.Equal(golden, SchemaCatalogWireCodec.Write(CatalogTestData.Schemas([value, owner]), Empty));
        var read = SchemaCatalogWireCodec.Read(golden, Empty);
        Assert.Equal(owner, read[1].Schema);
        Assert.Equal(SchemaKind.ReferenceObject, read[1].Schema!.Kind);
        Assert.Equal(SchemaKind.InlineValue, read[0].Schema!.Kind);
        Assert.Null(read[0].Layout);
        Assert.Same(read[0].Schema, read[1].Schema!.Fields[0].InlineSchema);
        for (int length = 0; length < golden.Length; length++) {
            byte[] prefix = golden[..length];
            Exception? error = Record.Exception(() => SchemaCatalogWireCodec.Read(prefix, Empty));
            Assert.True(error is InvalidDataException or EndOfStreamException, $"Cut {length}: {error}");
        }
    }

    [Theory]
    [InlineData(0x31424753U, "010203410101035A020201010809035A0200010304")]
    [InlineData(0x31424753U, "02020341010100010110035A02035A020200010102")]
    [InlineData(0x31424753U, "04020203410001010001011002035A000202035A00020200010102")]
    [InlineData(0x31425052U, "01010203010402")]
    public void RetiredSchemaAndRepresentationLogsAreRejectedWithoutRewriting(uint tag, string hex) {
        string path = NextPath();
        byte[] legacy = Convert.FromHexString(hex);
        using (IRbfFile file = RbfFile.CreateNew(path)) {
            file.Append(tag, legacy).Unwrap();
            file.DurableFlush();
        }
        byte[] original = File.ReadAllBytes(path);
        using (IRbfFile file = RbfFile.OpenExisting(path)) {
            Assert.Throws<InvalidDataException>(() => new SchemaStore(file));
        }
        Assert.Equal(original, File.ReadAllBytes(path));
        using IRbfFile reopened = RbfFile.OpenReadOnlyExisting(path);
        Assert.Throws<InvalidDataException>(() => new SchemaStore(reopened, readOnly: true));
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
        using (IRbfFile file = RbfFile.CreateNew(path)) {
            SchemaStore store = new(file);
            store.Register(owner);
            Assert.Equal(4, store.Count);
            long tail = file.TailOffset;
            store.Register(owner);
            Assert.Equal(tail, file.TailOffset);
            DurableSchema changedPoint = new("Point", 1, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.Int32));
            DurableSchema changedOwner = new("Fresh", 1, new DurableFieldInfo(1, TypeTag.InlineValue, inlineSchema: changedPoint));
            Assert.Throws<SchemaConflictException>(() => store.RegisterBatch([new("Independent", 1), changedOwner]));
            Assert.Equal(tail, file.TailOffset);
            Assert.Equal(4, store.Count);
            Assert.False(store.IsFaulted);
        }
        using IRbfFile reopened = RbfFile.OpenReadOnlyExisting(path);
        SchemaStore restored = new(reopened, readOnly: true);
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
        using IRbfFile file = RbfFile.CreateNew(NextPath());
        SchemaStore store = new(file);
        store.Register(owner1);
        long tail = file.TailOffset;
        Assert.Throws<SchemaConflictException>(() => store.Register(wrong));
        Assert.Equal(tail, file.TailOffset);
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
        using IRbfFile file = RbfFile.CreateNew(NextPath());
        SchemaStore store = new(file);
        if (!sameBatch) { store.Register(reference); }
        long tail = file.TailOffset;
        Assert.Throws<ArgumentException>(() => store.RegisterBatch(sameBatch ? [reference, inline] : [inline]));
        Assert.Equal(tail, file.TailOffset);
        Assert.Equal(sameBatch ? 0 : 1, store.Count);
        Assert.False(store.IsFaulted);
        var registered = sameBatch ? Empty : CatalogTestData.Index(CatalogTestData.Schemas([reference]));
        byte[] batch = CatalogTestData.Encode(CatalogTestData.Schemas(sameBatch ? [reference, inline] : [inline], sameBatch ? 2U : 3U));
        Assert.Throws<InvalidDataException>(() => SchemaCatalogWireCodec.Read(batch, registered));
        Assert.Equal(sameBatch ? 0 : 1, registered.Count);
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
        var registered = CatalogTestData.Index(CatalogTestData.Schemas([new("Existing", 1)]));
        Assert.Throws<InvalidDataException>(() => SchemaCatalogWireCodec.Read(Convert.FromHexString(hex), registered));
        Assert.Single(registered);
    }

    [Fact]
    public void InlineDepthLimitIncludesCachedAndPreviouslyRegisteredDependencies() {
        DurableSchema[] chain = InlineChain(257);
        var atLimit = chain[..256];
        var registered = CatalogTestData.Index(SchemaCatalogWireCodec.Read(
            SchemaCatalogWireCodec.Write(CatalogTestData.Schemas(atLimit), Empty), Empty));
        Assert.Equal(256, registered.Count);
        Assert.Throws<InvalidDataException>(() => SchemaCatalogWireCodec.Read(CatalogTestData.Encode(CatalogTestData.Schemas(chain)), Empty));
        Assert.Throws<InvalidDataException>(() => SchemaCatalogWireCodec.Read(
            CatalogTestData.Encode(CatalogTestData.Schemas([chain[^1]], 258), registered), registered));
        using IRbfFile file = RbfFile.CreateNew(NextPath());
        SchemaStore store = new(file);
        store.Register(chain[255]);
        long tail = file.TailOffset;
        Assert.Throws<ArgumentException>(() => store.Register(chain[256]));
        Assert.Equal(tail, file.TailOffset);
        Assert.Equal(256, store.Count);
        Assert.False(store.IsFaulted);
    }

    [Fact]
    public void InlineSchemaCannotIdentifyObjectBaseOrBeReadAsAnObject() {
        DurableSchema inline = new("A", 1, SchemaKind.InlineValue);
        Assert.Throws<ArgumentException>(() => ObjectLayout.ForDurable(inline));
        using IRbfFile file = RbfFile.CreateNew(NextPath());
        SchemaStore schemas = new(file);
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
        using IRbfFile file = RbfFile.CreateNew(NextPath());
        SchemaStore schemas = new(file);
        schemas.Register(owner);
        using SegmentStore segments = SegmentStore.CreateNew(NextPath(), new() { NewStoreLayout = RbfSegmentStoreLayout.Flat });
        using StateRevisionStore states = new(segments);
        FrameAddress address = states.Append(StateRevision.CreateObjectHeadMapBase(null,
            [ObjectVersionRecord.CreateBase(1, BaseObjectBodyCodec.Encode(schemas.RegisterRepresentations([ObjectLayout.ForDurable(owner)])[0], new([1])).Body)], []));
        ObjectVersionChain chain = states.ReadObjectVersionChain(address, 1);
        int calls = 0;
        byte Read(ref BinaryPayloadReader reader) { calls++; return reader.ReadByte(); }
        byte Apply(ref BinaryPayloadReader reader, in byte previous) { calls++; return previous; }
        Assert.Throws<InvalidDataException>(() => TypedObjectVersionReader.ReadDurable<byte>(chain, schemas, wrong, Read, Apply));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void PublishedBasePointingAtInlineSchemaRefusesRepositoryOpen() {
        string repositoryPath = NextPath();
        using (EventHistoryRepository repository = EventHistoryRepository.CreateNew(repositoryPath, new StateModelRegistry())) { }
        using (IRbfFile schemasFile = RbfFile.OpenExisting(Path.Combine(repositoryPath, "schemas.rbf"))) {
            new SchemaStore(schemasFile).Register(new("A", 1, SchemaKind.InlineValue));
        }
        FrameAddress address;
        using (SegmentStore segments = SegmentStore.OpenExisting(Path.Combine(repositoryPath, "state"))) {
            using StateRevisionStore states = new(segments);
            // Valid current Base envelope syntax, but its catalog ID identifies an inline value.
            address = states.AppendDurably(StateRevision.CreateObjectHeadMapBase(null,
                [ObjectVersionRecord.CreateBase(1, new byte[] { 4, 2 })], []));
        }
        using (HistoryJournal history = HistoryJournal.Open(repositoryPath, readOnly: false)) {
            history.ConfirmDurable();
            var frame = history.Append(GraphFrameKind.State, address, new ObjectId(1), null);
            history.Journal.CreateBranch("main", frame).Unwrap();
        }
        Dictionary<string, byte[]> before = Directory.GetFiles(repositoryPath, "*", SearchOption.AllDirectories)
            .ToDictionary(static path => path, File.ReadAllBytes);
        Assert.Throws<InvalidDataException>(() => EventHistoryRepository.OpenExisting(repositoryPath, new StateModelRegistry()));
        foreach ((string path, byte[] bytes) in before) { Assert.Equal(bytes, File.ReadAllBytes(path)); }
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
