using Atelia.DurableGraph.Schema;
using System.Reflection;
using Atelia.DurableGraph.Build;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Persistence.MetaBootstrapP0;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Fact]
    public void DecodedRevisionColdReopenDispatchesMixedHistoricalModelsAndValidatesTheWholeTargetView() {
        using AncestryHistoryDirectory history = new();
        SchemaHistoryTool publisher = new();
        GeneratorTestRun initial = RunGenerator(FusedDeltaPreamble + """
            [DurableType("decoded.base", 1)]
            public abstract partial class OldBase : IDurableObject {
                [DurableField(9)] private string? _name;
            }
            [DurableType("decoded.leaf", 1)]
            public sealed partial class Leaf : OldBase {
                [DurableField(3)] private int _number;
                [DurableField(8)] private string? _alias;
            }
            """);
        AssertSchemaOnlyCompiles(initial);
        publisher.Publish(history.WriteManifest(initial), history.History);
        GeneratorTestRun current = RunGenerator(DecodedRevisionCurrentSource, history.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(current);
        Assembly assembly = EmitAndLoad(current.OutputCompilation);
        Assert.Null(assembly.GetType("FusedDelta.OldBase"));
        Type leaf = assembly.GetType("FusedDelta.Leaf")!;
        DurableSchema oldSchema = ReadSchemaOnly(leaf, 1);
        DurableSchema currentSchema = ReadSchemaOnly(leaf, 2);
        DurableSchema otherSchema = ReadSchemaOnly(assembly.GetType("FusedDelta.Other")!, 1);
        Assert.Equal(TypeTag.String, Assert.Single(oldSchema.BaseSchema!.Fields).TypeTag);
        Assert.Equal(TypeTag.Byte, Assert.Single(currentSchema.BaseSchema!.Fields).TypeTag);
        Type host = assembly.GetType("FusedDelta.Host")!;
        var register = host.GetMethod("Register")!.CreateDelegate<Func<bool, StateModelRegistry>>();
        var check = host.GetMethod("Check")!.CreateDelegate<Action<DecodedRevision, int>>();
        var prepare = host.GetMethod("Prepare1")!.CreateDelegate<Func<byte[], byte[], PreparedDeltaBody>>();

        // Independent raw values seed the stored bodies; generated typed code prepares Deltas.
        // Stored IDs are fixture data only. The read API receives neither IDs nor per-object readers.
        byte[] original = [10, 2, 11]; // Old ancestor string ID 10; int 1; alias ID 11.
        PreparedDeltaBody firstDelta = prepare(original, [10, 4, 11]);
        PreparedDeltaBody secondDelta = prepare([10, 4, 11], [10, 4, 10]);
        Assert.Equal<byte>([2, 4], firstDelta.Body.ToArray());
        Assert.Equal<byte>([4, 10], secondDelta.Body.ToArray());
        static ObjectVersionRecord Durable(SchemaCatalogView catalog, uint id, DurableSchema schema, byte[] body) =>
            ObjectVersionRecord.CreateBase(id, TypedBaseBodyCodec.Encode(ObjectLayout.ForDurable(schema), new(body), catalog));
        static ObjectVersionRecord Text(uint id, string value) =>
            ObjectVersionRecord.CreateBase(id, TypedBaseBodyCodec.Encode(ObjectLayout.String, StringPayloadCodec.PrepareBase(value)));

        using RawBaseDirectory directory = new();
        const long rotationThreshold = 128;
        FrameAddress first, second, third, newBase, schemaRoot, missingString, wrongKind, malformed, mismatchedBase;
        using (GraphResources resources = GraphResources.CreateNew(directory.Path, rotationThreshold))
        using (RepositoryStorageFacade repository = new(resources)) {
            GraphRecordStore store = repository.Store;
            SchemaCatalogWorkspace catalog = new(repository.EnsureEditingAllowed);
            foreach (DurableSchema schema in new[] { oldSchema, currentSchema, otherSchema }) { catalog.Set(schema); }
            PreparedSchemaCatalogWrite preparedCatalog = catalog.Prepare();
            using (PendingRepositorySave pending = repository.Prepare("main", StoredGraphKind.State, new(null, new(1), [
                Durable(preparedCatalog.View, 1, oldSchema, original),
                Durable(preparedCatalog.View, 2, currentSchema, [7, 8, 12]),
                Text(10, "same"), Text(11, "same"), Text(12, ""), Text(13, ""),
                Durable(preparedCatalog.View, 99, otherSchema, [11, 18, 13]),
            ]), preparedCatalog.Write)) {
                RepositorySaveResult saved = repository.Commit(pending, installMeta: preparedCatalog.Install,
                    prepareMeta: preparedCatalog.PrepareInstall);
                first = saved.Business.Address;
                schemaRoot = saved.Meta!.Address;
            }
            Assert.Equal(first, repository.GetHead("main"));
            Assert.Equal(schemaRoot, repository.SharedSchemaRoot);
            Assert.Same(preparedCatalog.View, catalog.Accepted);
            check(DecodedRevision.ReadStoredGraph(store, first, register(true)), 0);

            FrameAddress Save(PreparedGraphWrite write) {
                using PendingRepositorySave pending = repository.Prepare("main", StoredGraphKind.State, write);
                RepositorySaveResult saved = repository.Commit(pending);
                Assert.Equal(saved.Business.Address, repository.GetHead("main"));
                Assert.Equal(schemaRoot, saved.Business.SchemaRoot);
                Assert.Equal(schemaRoot, repository.SharedSchemaRoot);
                return saved.Business.Address;
            }
            // Object priors identify their actual batch, independently of the business graph address.
            FrameAddress firstHead = store.ReadGraph(first).ObjectHeads[1];
            Assert.NotEqual(first, firstHead);
            second = Save(new(first, new(1), [ObjectVersionRecord.CreateDelta(1, firstHead, firstDelta.Body)]));
            FrameAddress secondHead = store.ReadGraph(second).ObjectHeads[1];
            Assert.NotEqual(second, secondHead);
            third = Save(new(second, new(1), [ObjectVersionRecord.CreateDelta(1, secondHead, secondDelta.Body)]));
            newBase = Save(new(third, new(1), [Durable(preparedCatalog.View, 1, oldSchema, [11, 6, 12])]));

            // These unpublished graphs deliberately bypass the public business writer's typed validation.
            // They are bounded internal corruption fixtures, not publishable business candidates.
            // Only the removed CLR ancestor's slot refers to ID 10 in this parent.
            // Checking only current or leaf-declared slots would incorrectly accept these views.
            missingString = store.Append(StoredGraphKind.State, second, schemaRoot, new(second, new(1), [], [10])).Address;
            wrongKind = store.Append(StoredGraphKind.State, second, schemaRoot,
                new(second, new(1), [Durable(preparedCatalog.View, 10, currentSchema, [7, 8, 12])])).Address;
            // ID 99 is last: all earlier objects have decoded before this trailing-byte error.
            malformed = store.Append(StoredGraphKind.State, third, schemaRoot,
                new(third, new(1), [Durable(preparedCatalog.View, 99, otherSchema, [11, 18, 13, 0])])).Address;

            // Equal leaf key and declared fields cannot hide a changed complete ancestor layout.
            DurableSchema oldAncestor = oldSchema.BaseSchema!;
            DurableSchema differentAncestor = new(oldAncestor.SchemaId, oldAncestor.Version,
                [new(9, TypeTag.UInt32)]);
            DurableSchema differentLeaf = new(oldSchema.Type, oldSchema.Version, oldSchema.Fields.ToArray(), differentAncestor);
            SchemaCatalogWorkspace incompatible = new(repository.EnsureEditingAllowed);
            foreach (DurableSchema schema in new[] { differentLeaf, currentSchema, otherSchema }) { incompatible.Set(schema); }
            PreparedSchemaCatalogWrite incompatibleCatalog = incompatible.Prepare();
            FrameAddress incompatibleRoot = store.Append(StoredGraphKind.Meta, null, null, incompatibleCatalog.Write!).Address;
            Assert.Equal(differentLeaf, SchemaCatalogView.Load(store, incompatibleRoot).Schemas[new(oldSchema.Type, oldSchema.Version)]);
            mismatchedBase = store.Append(StoredGraphKind.State, third, incompatibleRoot, new(third, new(1), [])).Address;
            Assert.Equal(schemaRoot, store.ReadObjectChain(mismatchedBase, 1).Entries[0].SchemaRoot);
            Assert.Equal(newBase, repository.GetHead("main"));
            Assert.Equal(schemaRoot, repository.SharedSchemaRoot);
            resources.Data.ConfirmDurable();
        }
        Assert.NotEqual(first, third);
        Array.Clear(original);
        firstDelta = null!;
        secondDelta = null!;
        Dictionary<string, byte[]> diskBefore = Directory.EnumerateFiles(directory.Path, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes);

        DecodedRevision[] retained;
        using (RepositoryStorageFacade reopened = RepositoryStorageFacade.OpenReadOnlyExisting(directory.Path)) {
            GraphRecordStore store = reopened.Store;
            Assert.Equal(newBase, reopened.GetHead("main"));
            Assert.Equal(schemaRoot, reopened.SharedSchemaRoot);
            StateModelRegistry readers = register(true);
            retained = new[] { first, second, third, newBase }
                .Select(address => DecodedRevision.ReadStoredGraph(store, address, readers)).ToArray();
            for (int stage = 0; stage < retained.Length; stage++) {
                check(retained[stage], stage);
                Assert.Equal(new[] { first, second, third, newBase }[stage], retained[stage].RevisionAddress);
                Assert.Equal<uint>([1, 2, 10, 11, 12, 13, 99], retained[stage].Objects.Select(row => row.Id).Select(id => id.Value));
            }
            Assert.Equal(3, store.ReadObjectChain(third, 1).Entries.Count);
            Assert.Single(store.ReadObjectChain(newBase, 1).Entries);

            foreach (FrameAddress bad in new[] { missingString, wrongKind, malformed, mismatchedBase }) {
                DecodedRevision? delivered = null;
                Assert.Throws<InvalidDataException>(() => delivered = DecodedRevision.ReadStoredGraph(store, bad, readers));
                Assert.Null(delivered);
                check(retained[2], 2);
            }
            // The selected meta graph includes Other, but intentionally omit that code capability.
            // Missing ID 99's reader cannot fall back to the same-shaped historical Leaf DTO.
            StateModelRegistry missingReader = register(false);
            DecodedRevision? partial = null;
            Assert.Throws<InvalidDataException>(() => partial = DecodedRevision.ReadStoredGraph(store, third, missingReader));
            Assert.Null(partial);
            check(DecodedRevision.ReadStoredGraph(store, first, readers), 0);
            check(retained[2], 2);
        }
        // Decoded DTOs and the identity table own their content beyond file/store lifetimes.
        for (int stage = 0; stage < retained.Length; stage++) check(retained[stage], stage);
        foreach ((string path, byte[] contents) in diskBefore) Assert.Equal(contents, File.ReadAllBytes(path));
        Assert.Equal(diskBefore.Count, Directory.EnumerateFiles(directory.Path, "*", SearchOption.AllDirectories).Count());
    }

    private static readonly string DecodedRevisionCurrentSource = FusedDeltaPreamble + """
        [DurableType("decoded.base", 2)]
        public abstract partial class NewBase : IDurableObject {
            [DurableField(2)] private byte _small;
        }
        [DurableType("decoded.leaf", 2)]
        public sealed partial class Leaf : NewBase {
            [DurableField(1)] private uint _number;
            [DurableField(8)] private string? _alias;
        }
        [DurableType("decoded.other", 1)]
        public sealed partial class Other : IDurableObject {
            [DurableField(1)] private string? _name;
            [DurableField(2)] private int _number;
            [DurableField(3)] private string? _empty;
        }
        public static class Host {
        """ + FusedDeltaHostMethods("Leaf", 1) + """
            public static Atelia.DurableGraph.Persistence.StateModelRegistry Register(bool includeOther) {
                var readers = new Atelia.DurableGraph.Persistence.StateModelRegistry();
                Leaf.__DurableState.RegisterModel(readers);
                Leaf.__DurableState.RegisterModel(readers); // Stable generated bindings are idempotent.
                if (includeOther) Other.__DurableState.RegisterModel(readers);
                return readers;
            }
            public static void Check(Atelia.DurableGraph.Persistence.DecodedRevision view, int stage) {
                var old = view.GetRequired(new ObjectId(1)).GetState<Leaf.__DurableState.V1>();
                var current = view.GetRequired(new ObjectId(2)).GetState<Leaf.__DurableState.V2>();
                var other = view.GetRequired(new ObjectId(99)).GetState<Other.__DurableState.V1>();
                if (old.Segment0Field9.Value != (stage == 3 ? 11u : 10u) ||
                    old.Segment1Field3 != (stage == 0 ? 1 : stage == 3 ? 3 : 2) ||
                    old.Segment1Field8.Value != (stage < 2 ? 11u : stage == 2 ? 10u : 12u) ||
                    current.Segment0Field2 != 7 || current.Segment1Field1 != 8 || current.Segment1Field8.Value != 12 ||
                    other.Segment0Field1.Value != 11 || other.Segment0Field2 != 9 || other.Segment0Field3.Value != 13)
                    throw new Exception("Stored exact DTO values or dispatch differ.");
                if (!view.GetRequired(new ObjectId(1)).Schema.Equals(Leaf.__DurableState.V1.Schema) ||
                    !view.GetRequired(new ObjectId(2)).Schema.Equals(Leaf.__DurableState.V2.Schema) ||
                    !view.GetRequired(new ObjectId(99)).Schema.Equals(Other.__DurableState.V1.Schema))
                    throw new Exception("The stored exact Schema was replaced.");
                string shared = view.Strings.ResolveString(new ObjectId(10));
                string equal = view.Strings.ResolveString(new ObjectId(11));
                if (shared != "same" || equal != "same" || ReferenceEquals(shared, equal) ||
                    !ReferenceEquals(equal, view.Strings.ResolveString(other.Segment0Field1)) ||
                    !ReferenceEquals(view.Strings.ResolveString(new ObjectId(12)), string.Empty) ||
                    !ReferenceEquals(view.Strings.ResolveString(new ObjectId(13)), string.Empty) || view.Strings.ResolveString(new ObjectId(0)) != null)
                    throw new Exception("String value or identity differs.");
                foreach (uint id in new uint[] { 10, 11, 12, 13 })
                    if (!ReferenceEquals(view.GetRequired(new ObjectId(id)).StringContent, view.Strings.ResolveString(new ObjectId(id))))
                        throw new Exception("Rows and the reference table decoded different instances.");
                old = default; // GetState returns a copy; changing the local cannot alter the retained row.
                if (view.GetRequired(new ObjectId(1)).GetState<Leaf.__DurableState.V1>().Segment0Field9.IsNull)
                    throw new Exception("A returned DTO copy changed the stored state.");
            }
        }
        """;
}
