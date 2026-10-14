using System.Buffers;

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
        using (RepositoryStorageFacade repository = RepositoryStorageFacade.CreateNew(_root)) {
            SchemaCatalogWorkspace catalog = new(repository.EnsureEditingAllowed);
            catalog.Set(layout);
            PreparedSchemaCatalogWrite prepared = catalog.Prepare();
            descriptor = prepared.View.DescriptorId(layout);
            StoredGraphRevision initial = Save(repository, null, new(1), [
                ObjectVersionRecord.CreateBase(1, TypedBaseBodyCodec.Encode(layout, new PreparedBaseBody([2, 7]), prepared.View)),
                ObjectVersionRecord.CreateBase(2, TypedBaseBodyCodec.Encode(ObjectLayout.String, StringPayloadCodec.PrepareBase("old"), prepared.View)),
            ], prepared.Write);
            first = initial.Address;
            schemaRoot = initial.SchemaRoot!.Value;
            StoredGraphRevision delta = Save(repository, first, new(1),
                [ObjectVersionRecord.CreateDelta(1, initial.ObjectHeads[1], [9])]);
            changed = delta.Address;
            rewritten = Save(repository, changed, new(1), [
                ObjectVersionRecord.CreateBase(1, TypedBaseBodyCodec.Encode(layout, new PreparedBaseBody([2, 11]), prepared.View)),
            ]).Address;
        }

        using RepositoryStorageFacade reopened = RepositoryStorageFacade.OpenReadOnlyExisting(_root);
        long tail = DataBytes(reopened.Data);
        StateModelRegistry readers = WorldReaders();
        Assert.Equal((byte)7, DecodedRevision.ReadStoredGraph(reopened.Store, first, readers).GetRequired(new(1)).GetState<WorldState>().Value);
        DecodedRevision deltaView = DecodedRevision.ReadStoredGraph(reopened.Store, changed, readers);
        Assert.Equal(new WorldState(new(2), 9), deltaView.GetRequired(new(1)).GetState<WorldState>());
        Assert.Equal("old", deltaView.GetRequired(new(2)).StringContent);
        DecodedRevision baseView = DecodedRevision.ReadStoredGraph(reopened.Store, rewritten, readers);
        Assert.Equal((byte)11, baseView.GetRequired(new(1)).GetState<WorldState>().Value);

        SchemaCatalogView selected = SchemaCatalogView.Load(reopened.Store, schemaRoot);
        Assert.Equal(descriptor, selected.DescriptorId(layout));
        Assert.Equal(layout, selected.ResolveLayout(descriptor));
        ObjectVersionChain oldChain = reopened.Store.ReadObjectChain(changed, 1);
        Assert.Equal(2, oldChain.Entries.Count);
        Assert.Equal(reopened.Store.ReadGraph(first).ObjectHeads[1], oldChain.Entries[1].Record.PriorAddress);
        Assert.Equal(new byte[] { 9 }, oldChain.Entries[1].Record.Body.ToArray());
        Assert.Equal(oldChain.Head, deltaView.ObjectHeads[new(1)]);
        Assert.Equal(reopened.Store.ReadGraph(first).ObjectHeads[2], deltaView.ObjectHeads[new(2)]);
        Assert.NotEqual(changed, deltaView.ObjectHeads[new(1)]);
        Assert.Equal(oldChain.Entries.Sum(static entry => (long)entry.PayloadBytes), deltaView.ObjectStorage[new(1)].ReconstructionPayloadBytes);
        ObjectVersionChain newChain = reopened.Store.ReadObjectChain(rewritten, 1);
        Assert.Single(newChain.Entries);
        Assert.Equal(newChain.Head, baseView.ObjectHeads[new(1)]);
        Assert.Equal((long)newChain.Entries[0].PayloadBytes, baseView.ObjectStorage[new(1)].ReconstructionPayloadBytes);
        foreach (ObjectVersionChainEntry entry in new[] { oldChain.Entries[0], newChain.Entries[0] }) {
            Assert.Equal(schemaRoot, entry.SchemaRoot);
            BareValueReader header = new(entry.Record.Body);
            Assert.Equal(TypedBaseBodyCodec.FormatVersion, header.ReadByte());
            Assert.Equal((byte)1, header.ReadByte());
            Assert.Equal(descriptor.Value, header.ReadVarUInt32());
            Assert.Equal(layout, TypedBaseBodyCodec.Decode(entry.Record.Body, selected).Layout);
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
        using (RepositoryStorageFacade repository = RepositoryStorageFacade.CreateNew(_root)) {
            SchemaCatalogWorkspace catalog = new(repository.EnsureEditingAllowed);
            catalog.Set(WorldSchema);
            DurableSchema inline = new("Value", 1, SchemaKind.InlineValue);
            if (inlineDescriptor) { catalog.Set(inline); }
            PreparedSchemaCatalogWrite prepared = catalog.Prepare();
            ObjectId invalid = inlineDescriptor ? prepared.View.SchemaNodeId(inline) : new(prepared.View.Rows.Keys.Max().Value + 1);
            ArrayBufferWriter<byte> buffer = new();
            BareValueWriter writer = new(buffer);
            writer.WriteByte(TypedBaseBodyCodec.FormatVersion);
            writer.WriteByte(1);
            writer.WriteVarUInt32(invalid.Value);
            writer.WriteRawBytes([0, 1]);
            byte[] good = TypedBaseBodyCodec.Encode(ObjectLayout.ForDurable(WorldSchema), new PreparedBaseBody([0, 1]), prepared.View);
            ObjectVersionRecord[] rows = invalidRoot
                ? [ObjectVersionRecord.CreateBase(1, buffer.WrittenSpan)]
                : [ObjectVersionRecord.CreateBase(1, good), ObjectVersionRecord.CreateBase(2, buffer.WrittenSpan)];
            revision = Save(repository, null, new(1), rows, prepared.Write).Address;
        }

        var before = Directory.GetFiles(_root, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
        using (RepositoryStorageFacade reopened = RepositoryStorageFacade.OpenReadOnlyExisting(_root)) {
            Assert.Throws<InvalidDataException>(() => DecodedRevision.ReadStoredGraph(reopened.Store, revision, WorldReaders()));
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
        using (RepositoryStorageFacade repository = RepositoryStorageFacade.CreateNew(_root)) {
            SchemaCatalogWorkspace catalog = new(repository.EnsureEditingAllowed);
            catalog.Set(layout);
            PreparedSchemaCatalogWrite prepared = catalog.Prepare();
            descriptor = prepared.View.DescriptorId(layout);
            ArrayBufferWriter<byte> buffer = new();
            BareValueWriter writer = new(buffer);
            writer.WriteVarUInt32(2);
            writer.WriteVarInt32(3);
            writer.WriteVarInt32(7);
            byte[] body = TypedBaseBodyCodec.Encode(layout, new PreparedBaseBody(buffer.WrittenSpan), prepared.View);
            StoredGraphRevision saved = Save(repository, null, new(12), [ObjectVersionRecord.CreateBase(12, body)], prepared.Write);
            revision = saved.Address;
            schemaRoot = saved.SchemaRoot!.Value;
        }

        using RepositoryStorageFacade reopened = RepositoryStorageFacade.OpenReadOnlyExisting(_root);
        long tail = DataBytes(reopened.Data);
        DecodedRevision retained = DecodedRevision.ReadStoredGraph(reopened.Store, revision, RetiredPointReaders(includeCode: true));
        Assert.Equal(layout, retained.GetRequired(new(12)).Layout);
        Assert.Throws<InvalidDataException>(() => retained.SourceModels!.GetDomainType(layout.Type));
        FrozenArrayState<int> decoded = retained.GetRequired(new(12)).GetArrayState<int>();
        Assert.Equal(2, decoded.Shape.Count);
        Assert.Equal(3, decoded[0]);
        Assert.Equal(7, decoded[1]);

        // A fresh metadata-only configuration must fail even after a different
        // configuration successfully bound this descriptor on the same Store.
        SchemaCatalogView selected = SchemaCatalogView.Load(reopened.Store, schemaRoot);
        Assert.Equal(layout, selected.ResolveLayout(descriptor));
        Assert.Throws<InvalidDataException>(() => DecodedRevision.ReadStoredGraph(reopened.Store, revision, RetiredPointReaders(includeCode: false)));
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

    private static StoredGraphRevision Save(RepositoryStorageFacade repository, FrameAddress? source,
        ObjectId root, ObjectVersionRecord[] records, PreparedGraphWrite? catalog = null) {
        using PendingRepositorySave pending = repository.Prepare("main", StoredGraphKind.State, new(source, root, records), catalog);
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
