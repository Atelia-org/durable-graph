using Atelia.DurableGraph.Persistence.MetaBootstrapP0;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using DescriptorDto = Atelia.DurableGraph.Generated.Family_503044.V1;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class MetaBootstrapP0DescriptorTests : IDisposable {
    private readonly string _path = Path.Combine(Path.GetTempPath(), "dg-bootstrap-descriptors-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Cold_meta_projection_rejects_reserved_business_schema_name() {
        P0PreparedCatalog prepared = Prepare();
        ObjectStateRecord name = prepared.View.Rows.Values.Single(static row => row.Kind == ObjectStateKind.String);
        byte[] badName = P0TypedBodyCodec.Encode(ObjectLayout.String, StringPayloadCodec.PrepareBase("P0C"));
        ObjectVersionRecord[] records = prepared.Write!.Records.Select(row => row.ObjectId == name.Id.Value
            ? ObjectVersionRecord.CreateBase(row.ObjectId, badName) : row).ToArray();
        FrameAddress graph;
        using (var repository = P0Repository.CreateNew(_path)) {
            graph = repository.Store.Append(P0GraphKind.Meta, null, null, new(null, prepared.View.RootId, records)).Address;
        }
        using var reopened = P0Repository.OpenReadOnlyExisting(_path);
        Assert.Throws<InvalidDataException>(() => P0CatalogView.Load(reopened.Store, graph));
    }

    [Fact]
    public void Cold_meta_projection_rejects_an_unknown_descriptor_category() {
        P0PreparedCatalog prepared = Prepare();
        ObjectId id = Assert.Single(prepared.View.DescriptorIds);
        ObjectStateRecord source = prepared.View.Rows[id];
        DescriptorDto dto = source.GetState<DescriptorDto>();
        ObjectStateRecord bad = P0FixedBindings.WithPreparation(new(id, source.Schema!,
            new DescriptorDto(3, dto.Segment0Field2, dto.Segment0Field3, dto.Segment0Field4, dto.Segment0Field5, dto.Segment0Field6)));
        byte[] body = P0TypedBodyCodec.Encode(bad.Layout, bad.Preparation!.PrepareBase(bad));
        ObjectVersionRecord[] records = prepared.Write!.Records.Select(row => row.ObjectId == id.Value
            ? ObjectVersionRecord.CreateBase(row.ObjectId, body) : row).ToArray();
        FrameAddress graph;
        using (var repository = P0Repository.CreateNew(_path)) {
            graph = repository.Store.Append(P0GraphKind.Meta, null, null, new(null, prepared.View.RootId, records)).Address;
        }
        using var reopened = P0Repository.OpenReadOnlyExisting(_path);
        Assert.Throws<InvalidDataException>(() => P0CatalogView.Load(reopened.Store, graph));
    }

    [Fact]
    public void Only_declared_object_descriptors_can_be_dynamic_type_identifiers() {
        P0PreparedCatalog prepared = Prepare();
        ObjectId field = prepared.View.Rows.Values.Single(static row => row.Schema?.SchemaId == "P0F").Id;
        Assert.Throws<InvalidDataException>(() => prepared.View.ResolveLayout(field));
        ObjectId undeclared = new(prepared.View.Rows.Keys.Max().Value + 1);
        ObjectStateRecord descriptor = prepared.View.Rows[Assert.Single(prepared.View.DescriptorIds)];
        Dictionary<ObjectId, ObjectStateRecord> withUndeclared = prepared.View.Rows.ToDictionary();
        withUndeclared.Add(undeclared, new(undeclared, descriptor.Schema!, descriptor.GetState<DescriptorDto>()));
        P0CatalogView view = new(prepared.View.RootId, withUndeclared);
        Assert.True(view.Rows.ContainsKey(undeclared));
        Assert.Throws<InvalidDataException>(() => view.ResolveLayout(undeclared));
        Dictionary<ObjectId, ObjectStateRecord> bad = prepared.View.Rows.ToDictionary();
        ObjectStateRecord list = bad[prepared.View.DescriptorListId];
        bad[list.Id] = new(list.Id, list.Layout.List!, new FrozenListState<ObjectId>([field]));
        Assert.Throws<InvalidDataException>(() => new P0CatalogView(prepared.View.RootId, bad));
    }

    [Fact]
    public void Unsupported_schema_shapes_are_rejected_before_a_candidate_is_prepared() {
        P0CatalogWorkspace owner = new(static () => { });
        Assert.Throws<NotSupportedException>(() => owner.Set(new DurableSchema("P0D", 1, [new(1, TypeTag.Int32)])));
        DurableSchema parent = new("Parent", 1, [new(1, TypeTag.Int32)]);
        DurableSchema inherited = new("Child", 1, [new(2, TypeTag.Int32)], baseSchema: parent);
        Assert.Throws<NotSupportedException>(() => owner.Set(inherited));
        Assert.Equal(0, owner.DirtyCount);
    }

    [Fact]
    public void Catalog_owns_its_input_and_does_not_expose_a_writable_projection_alias() {
        P0PreparedCatalog prepared = Prepare();
        Dictionary<ObjectId, ObjectStateRecord> input = prepared.View.Rows.ToDictionary();
        P0CatalogView view = new(prepared.View.RootId, input);
        int count = input.Count;
        input.Clear();
        Assert.Equal(count, view.Rows.Count);
        Assert.Single(view.Schemas);
        AssertReadOnlyThroughSyncRoot(view.Rows);
        AssertReadOnlyThroughSyncRoot(view.Schemas);
        Assert.Equal(count, view.Rows.Count);
        Assert.Single(view.Schemas);
    }

    private static void AssertReadOnlyThroughSyncRoot<TKey, TValue>(IReadOnlyDictionary<TKey, TValue> projection) where TKey : notnull {
        if (projection is System.Collections.ICollection collection && collection.SyncRoot is IDictionary<TKey, TValue> alias) {
            Assert.Throws<NotSupportedException>(() => alias.Clear());
        }
    }

    private static P0PreparedCatalog Prepare() {
        P0CatalogWorkspace owner = new(static () => { });
        owner.Set(new DurableSchema("A_name_with_no_business_CLR", 1, [new(1, TypeTag.Int32)]));
        return owner.Prepare();
    }

    public void Dispose() {
        if (Directory.Exists(_path)) { Directory.Delete(_path, recursive: true); }
    }
}
