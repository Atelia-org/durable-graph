
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
        PreparedSchemaCatalogWrite prepared = Prepare();
        ObjectStateRecord name = prepared.View.Rows.Values.Single(static row => row.Kind == ObjectStateKind.String);
        byte[] badName = TypedBaseBodyCodec.Encode(ObjectLayout.String, StringBodyPreparer.PrepareBase("P0C"));
        ObjectVersionRecord[] records = prepared.Write!.Records.Select(row => row.ObjectId == name.Id.Value
            ? ObjectVersionRecord.CreateBase(row.ObjectId, badName) : row).ToArray();
        FrameAddress graph;
        using (var repository = RepositoryStorageFacade.CreateNew(_path)) {
            graph = repository.Store.Append(StoredGraphKind.Meta, null, null, new(null, prepared.View.RootId, records)).Address;
        }
        using var reopened = RepositoryStorageFacade.OpenReadOnlyExisting(_path);
        Assert.Throws<InvalidDataException>(() => SchemaCatalogView.Load(reopened.Store, graph));
    }

    [Fact]
    public void Cold_meta_projection_rejects_an_unknown_descriptor_category() {
        PreparedSchemaCatalogWrite prepared = Prepare();
        ObjectId id = Assert.Single(prepared.View.DescriptorIds);
        ObjectStateRecord source = prepared.View.Rows[id];
        DescriptorDto dto = source.GetState<DescriptorDto>();
        ObjectStateRecord bad = BootstrapBindings.WithPreparation(new(id, source.Schema!,
            new DescriptorDto(255, dto.Segment0Field2, dto.Segment0Field3, dto.Segment0Field4,
                dto.Segment0Field5, dto.Segment0Field6, dto.Segment0Field7)));
        byte[] body = TypedBaseBodyCodec.Encode(bad.Layout, bad.Preparation!.PrepareBase(bad));
        ObjectVersionRecord[] records = prepared.Write!.Records.Select(row => row.ObjectId == id.Value
            ? ObjectVersionRecord.CreateBase(row.ObjectId, body) : row).ToArray();
        FrameAddress graph;
        using (var repository = RepositoryStorageFacade.CreateNew(_path)) {
            graph = repository.Store.Append(StoredGraphKind.Meta, null, null, new(null, prepared.View.RootId, records)).Address;
        }
        using var reopened = RepositoryStorageFacade.OpenReadOnlyExisting(_path);
        Assert.Throws<InvalidDataException>(() => SchemaCatalogView.Load(reopened.Store, graph));
    }

    [Fact]
    public void Only_declared_object_descriptors_can_be_dynamic_type_identifiers() {
        PreparedSchemaCatalogWrite prepared = Prepare();
        ObjectId field = prepared.View.Rows.Values.Single(static row => row.Schema?.SchemaId == "P0F").Id;
        Assert.Throws<InvalidDataException>(() => prepared.View.ResolveLayout(field));
        ObjectId undeclared = new(prepared.View.Rows.Keys.Max().Value + 1);
        ObjectStateRecord descriptor = prepared.View.Rows[Assert.Single(prepared.View.DescriptorIds)];
        Dictionary<ObjectId, ObjectStateRecord> withUndeclared = prepared.View.Rows.ToDictionary();
        withUndeclared.Add(undeclared, new(undeclared, descriptor.Schema!, descriptor.GetState<DescriptorDto>()));
        SchemaCatalogView view = new(prepared.View.RootId, withUndeclared);
        Assert.True(view.Rows.ContainsKey(undeclared));
        Assert.Throws<InvalidDataException>(() => view.ResolveLayout(undeclared));
        Dictionary<ObjectId, ObjectStateRecord> bad = prepared.View.Rows.ToDictionary();
        ObjectStateRecord list = bad[prepared.View.DescriptorListId];
        bad[list.Id] = new(list.Id, list.Layout.List!, new FrozenListState<ObjectId>([field]));
        Assert.Throws<InvalidDataException>(() => new SchemaCatalogView(prepared.View.RootId, bad));
    }

    [Fact]
    public void Reserved_names_are_rejected_and_inherited_schema_closure_is_prepared() {
        SchemaCatalogWorkspace owner = new(static () => { });
        Assert.Throws<NotSupportedException>(() => owner.Set(new DurableSchema("P0D", 1, [new(1, TypeTag.Int32)])));
        Assert.Throws<NotSupportedException>(() => owner.Set(new DurableSchema("P0T", 2, [new(1, TypeTag.Int32)])));
        DurableSchema parent = new("Parent", 1, [new(1, TypeTag.Int32)]);
        DurableSchema inherited = new("Child", 1, [new(2, TypeTag.Int32)], baseSchema: parent);
        Assert.Equal(0, owner.DirtyCount);
        owner.Set(inherited);
        SchemaCatalogView prepared = owner.Prepare().View;
        Assert.Equal(parent, prepared.Schemas[new(parent.Type, parent.Version)]);
        Assert.Equal(inherited, prepared.Schemas[new(inherited.Type, inherited.Version)]);
    }

    [Fact]
    public void Catalog_owns_its_input_and_does_not_expose_a_writable_projection_alias() {
        PreparedSchemaCatalogWrite prepared = Prepare();
        Dictionary<ObjectId, ObjectStateRecord> input = prepared.View.Rows.ToDictionary();
        SchemaCatalogView view = new(prepared.View.RootId, input);
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

    private static PreparedSchemaCatalogWrite Prepare() {
        SchemaCatalogWorkspace owner = new(static () => { });
        owner.Set(new DurableSchema("A_name_with_no_business_CLR", 1, [new(1, TypeTag.Int32)]));
        return owner.Prepare();
    }

    public void Dispose() {
        if (Directory.Exists(_path)) { Directory.Delete(_path, recursive: true); }
    }
}
