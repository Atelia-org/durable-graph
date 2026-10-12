using System.Buffers;
using Atelia.DurableGraph.Persistence.MetaBootstrapP0;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class RepresentationIntegrationTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"durable-representation-integration-{Guid.NewGuid():N}");
    private static readonly DurableSchema WorldSchema = new("W", 1,
        new DurableFieldInfo(1, TypeTag.String), new DurableFieldInfo(2, TypeTag.Byte));

    [Fact]
    public void DeltaInheritsDescriptorAcrossColdReopenAndRebaseStartsNewChain() {
        ObjectLayout layout = ObjectLayout.ForDurable(WorldSchema);
        FrameAddress first, changed, rewritten, schemaRoot;
        ObjectId descriptor;
        using (P0Repository repository = P0Repository.CreateNew(_root)) {
            P0CatalogWorkspace catalog = new(repository.EnsureEditingAllowed);
            catalog.Set(layout);
            P0PreparedCatalog prepared = catalog.Prepare();
            descriptor = prepared.View.DescriptorId(layout);
            P0GraphSnapshot initial = Save(repository, null, new(1), [
                ObjectVersionRecord.CreateBase(1, P0TypedBodyCodec.Encode(layout, new PreparedBaseBody([2, 7]), prepared.View)),
                ObjectVersionRecord.CreateBase(2, P0TypedBodyCodec.Encode(ObjectLayout.String, StringPayloadCodec.PrepareBase("old"), prepared.View)),
            ], prepared.Write);
            first = initial.Address;
            schemaRoot = initial.SchemaRoot!.Value;
            P0GraphSnapshot delta = Save(repository, first, new(1),
                [ObjectVersionRecord.CreateDelta(1, initial.Objects[1], [9])]);
            changed = delta.Address;
            rewritten = Save(repository, changed, new(1), [
                ObjectVersionRecord.CreateBase(1, P0TypedBodyCodec.Encode(layout, new PreparedBaseBody([2, 11]), prepared.View)),
            ]).Address;
        }

        using P0Repository reopened = P0Repository.OpenReadOnlyExisting(_root);
        long tail = DataBytes(reopened.Data);
        StateModelRegistry readers = WorldReaders();
        Assert.Equal((byte)7, DecodedRevision.ReadSplit(reopened.Store, first, readers).GetRequired(new(1)).GetState<WorldState>().Value);
        DecodedRevision deltaView = DecodedRevision.ReadSplit(reopened.Store, changed, readers);
        Assert.Equal(new WorldState(new(2), 9), deltaView.GetRequired(new(1)).GetState<WorldState>());
        Assert.Equal("old", deltaView.GetRequired(new(2)).StringContent);
        DecodedRevision baseView = DecodedRevision.ReadSplit(reopened.Store, rewritten, readers);
        Assert.Equal((byte)11, baseView.GetRequired(new(1)).GetState<WorldState>().Value);

        P0CatalogView selected = P0CatalogView.Load(reopened.Store, schemaRoot);
        Assert.Equal(descriptor, selected.DescriptorId(layout));
        Assert.Equal(layout, selected.ResolveLayout(descriptor));
        P0ObjectChain oldChain = reopened.Store.ReadObjectChain(changed, 1);
        Assert.Equal(2, oldChain.Entries.Count);
        Assert.Equal(reopened.Store.ReadGraph(first).Objects[1], oldChain.Entries[1].Record.PriorAddress);
        Assert.Equal(new byte[] { 9 }, oldChain.Entries[1].Record.Body.ToArray());
        Assert.Equal(oldChain.Head, deltaView.ObjectHeads[new(1)]);
        Assert.Equal(reopened.Store.ReadGraph(first).Objects[2], deltaView.ObjectHeads[new(2)]);
        Assert.NotEqual(changed, deltaView.ObjectHeads[new(1)]);
        Assert.Equal(oldChain.Entries.Sum(static entry => (long)entry.PayloadBytes), deltaView.ObjectStorage[new(1)].ReconstructionPayloadBytes);
        P0ObjectChain newChain = reopened.Store.ReadObjectChain(rewritten, 1);
        Assert.Single(newChain.Entries);
        Assert.Equal(newChain.Head, baseView.ObjectHeads[new(1)]);
        Assert.Equal((long)newChain.Entries[0].PayloadBytes, baseView.ObjectStorage[new(1)].ReconstructionPayloadBytes);
        foreach (P0ChainEntry entry in new[] { oldChain.Entries[0], newChain.Entries[0] }) {
            Assert.Equal(schemaRoot, entry.SchemaRoot);
            BareValueReader header = new(entry.Record.Body);
            Assert.Equal(P0TypedBodyCodec.FormatVersion, header.ReadByte());
            Assert.Equal((byte)1, header.ReadByte());
            Assert.Equal(descriptor.Value, header.ReadVarUInt32());
            Assert.Equal(layout, P0TypedBodyCodec.Decode(entry.Record.Body, selected).Layout);
        }
        Assert.Equal(tail, DataBytes(reopened.Data));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void ColdReadRejectsUnknownOrInlineMetaIdInOtherwiseValidBusinessFrames(bool invalidRoot, bool inlineDescriptor) {
        FrameAddress revision;
        using (P0Repository repository = P0Repository.CreateNew(_root)) {
            P0CatalogWorkspace catalog = new(repository.EnsureEditingAllowed);
            catalog.Set(WorldSchema);
            DurableSchema inline = new("Value", 1, SchemaKind.InlineValue);
            if (inlineDescriptor) { catalog.Set(inline); }
            P0PreparedCatalog prepared = catalog.Prepare();
            ObjectId invalid = inlineDescriptor ? prepared.View.SchemaNodeId(inline) : new(prepared.View.Rows.Keys.Max().Value + 1);
            ArrayBufferWriter<byte> buffer = new();
            BareValueWriter writer = new(buffer);
            writer.WriteByte(P0TypedBodyCodec.FormatVersion);
            writer.WriteByte(1);
            writer.WriteVarUInt32(invalid.Value);
            writer.WriteRawBytes([0, 1]);
            byte[] good = P0TypedBodyCodec.Encode(ObjectLayout.ForDurable(WorldSchema), new PreparedBaseBody([0, 1]), prepared.View);
            ObjectVersionRecord[] rows = invalidRoot
                ? [ObjectVersionRecord.CreateBase(1, buffer.WrittenSpan)]
                : [ObjectVersionRecord.CreateBase(1, good), ObjectVersionRecord.CreateBase(2, buffer.WrittenSpan)];
            revision = Save(repository, null, new(1), rows, prepared.Write).Address;
        }

        var before = Directory.GetFiles(_root, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
        using (P0Repository reopened = P0Repository.OpenReadOnlyExisting(_root)) {
            Assert.Throws<InvalidDataException>(() => DecodedRevision.ReadSplit(reopened.Store, revision, WorldReaders()));
        }
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Order());
        foreach ((string path, byte[] bytes) in before) { Assert.Equal(bytes, File.ReadAllBytes(path)); }
    }

    [Fact]
    public void PersistedArrayDescriptorCanBindRetainedValueCodeWithoutDomainDeclarationButCannotSupplyMissingCode() {
        DurableSchema point = new("RetiredPoint", 1, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.Int32));
        ObjectLayout layout = ObjectLayout.ForArray(new(TypeExprKind.VectorArray,
            new DurableFieldInfo(1, TypeTag.InlineValue, inlineSchema: point)));
        ObjectId descriptor;
        FrameAddress revision, schemaRoot;
        using (P0Repository repository = P0Repository.CreateNew(_root)) {
            P0CatalogWorkspace catalog = new(repository.EnsureEditingAllowed);
            catalog.Set(layout);
            P0PreparedCatalog prepared = catalog.Prepare();
            descriptor = prepared.View.DescriptorId(layout);
            ArrayBufferWriter<byte> buffer = new();
            BareValueWriter writer = new(buffer);
            writer.WriteVarUInt32(2);
            writer.WriteVarInt32(3);
            writer.WriteVarInt32(7);
            byte[] body = P0TypedBodyCodec.Encode(layout, new PreparedBaseBody(buffer.WrittenSpan), prepared.View);
            P0GraphSnapshot saved = Save(repository, null, new(12), [ObjectVersionRecord.CreateBase(12, body)], prepared.Write);
            revision = saved.Address;
            schemaRoot = saved.SchemaRoot!.Value;
        }

        using P0Repository reopened = P0Repository.OpenReadOnlyExisting(_root);
        long tail = DataBytes(reopened.Data);
        DecodedRevision retained = DecodedRevision.ReadSplit(reopened.Store, revision, RetiredPointReaders(includeCode: true));
        Assert.Equal(layout, retained.GetRequired(new(12)).Layout);
        Assert.Throws<InvalidDataException>(() => retained.SourceModels!.GetDomainType(layout.Type));
        FrozenArrayState<int> decoded = retained.GetRequired(new(12)).GetArrayState<int>();
        Assert.Equal(2, decoded.Shape.Count);
        Assert.Equal(3, decoded[0]);
        Assert.Equal(7, decoded[1]);

        // A fresh metadata-only configuration must fail even after a different
        // configuration successfully bound this descriptor on the same Store.
        P0CatalogView selected = P0CatalogView.Load(reopened.Store, schemaRoot);
        Assert.Equal(layout, selected.ResolveLayout(descriptor));
        Assert.Throws<InvalidDataException>(() => DecodedRevision.ReadSplit(reopened.Store, revision, RetiredPointReaders(includeCode: false)));
        Assert.Equal(tail, DataBytes(reopened.Data));
    }

    private readonly record struct WorldState(ObjectId Text, byte Value);

    private static StateModelRegistry WorldReaders() {
        StateModelRegistry readers = new();
        readers.Register(new StateDefinitionBinding("W", SchemaKind.ReferenceObject, 0, null,
            [new("W", 1, SchemaKind.ReferenceObject, 0, [new(1, TypeExpr.Builtin(TypeTag.String)), new(2, TypeExpr.Builtin(TypeTag.Byte))])],
            historicalReaderFactory: static (schema, _) => new StateReaderBinding<WorldState>(schema,
                static (ref BareValueReader input) => new(new ObjectId(input.ReadVarUInt32()), input.ReadByte()),
                static (ref BareValueReader input, in WorldState prior) => prior with { Value = input.ReadByte() },
                static (in WorldState state, IStateReferenceVisitor visitor) => visitor.VisitString(state.Text))));
        return readers;
    }

    private static StateModelRegistry RetiredPointReaders(bool includeCode) {
        StateModelRegistry readers = new();
        readers.Register(new StateDefinitionBinding("RetiredPoint", SchemaKind.InlineValue, 0, null,
            [new("RetiredPoint", 1, SchemaKind.InlineValue, 0, [new(1, TypeExpr.Builtin(TypeTag.Int32))])],
            historicalValueFactory: includeCode ? static (schema, _) => new(
                new(1, TypeTag.InlineValue, inlineSchema: schema), typeof(int), typeof(Int32StateOps)) : null));
        return readers;
    }

    private static P0GraphSnapshot Save(P0Repository repository, FrameAddress? source,
        ObjectId root, ObjectVersionRecord[] records, P0PreparedGraphWrite? catalog = null) {
        using P0PendingSave pending = repository.Prepare("main", P0GraphKind.State, new(source, root, records), catalog);
        return repository.Commit(pending).Business;
    }

    public void Dispose() {
        string resolved = Path.GetFullPath(_root);
        if (!string.Equals(Path.GetDirectoryName(resolved), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(resolved).StartsWith("durable-representation-integration-", StringComparison.Ordinal)) {
            throw new InvalidOperationException("Refusing to delete outside this test fixture.");
        }
        if (Directory.Exists(resolved)) { Directory.Delete(resolved, recursive: true); }
    }
}
