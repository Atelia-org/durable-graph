using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Buffers;
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
    public void PersistedDeltaChainColdReopenReconstructsFrozenCaptureAndValidatesTargetReferences() {
        GeneratorTestRun run = RunGenerator(PersistedDeltaCaptureSource);
        AssertSchemaOnlyCompiles(run);
        Type host = EmitAndLoad(run.OutputCompilation).GetType("PersistedCapture.Host")!;
        var capture = host.GetMethod("Capture")!.CreateDelegate<PersistedCapture>();
        var models = host.GetMethod("Models")!.CreateDelegate<Func<StateModelRegistry>>();
        var check = host.GetMethod("Check")!.CreateDelegate<Func<DecodedRevision, byte[]>>();
        var input = capture(); // Capture and generated body codecs own values after every domain mutation.
        DurableSchema schema = input.Schema;
        uint ownerId = input.Owner.Value, nameId = input.Name.Value, aliasId = input.Alias.Value, emptyId = input.Empty.Value;
        byte[][] expected = [ExpectedCapturedBody(nameId, nameId, emptyId, 1),
            ExpectedCapturedBody(nameId, aliasId, emptyId, 2), ExpectedCapturedBody(nameId, aliasId, emptyId, 3)];
        Assert.Equal(expected[0], input.Base);
        Assert.True(input.First.HasChanges);
        Assert.True(input.Second.HasChanges);
        Assert.Equal<byte>([0x0A, checked((byte)aliasId), 4], input.First.Body.ToArray());
        Assert.Equal<byte>([0x08, 6], input.Second.Body.ToArray());

        using RawBaseDirectory directory = new();
        FrameAddress first, second, third, repeated, missingString, wrongKind, malformed, selectedSchema;
        using (RepositoryStorageFacade repository = RepositoryStorageFacade.CreateNew(directory.Path)) {
            SchemaCatalogWorkspace catalog = new(repository.EnsureEditingAllowed);
            catalog.Set(schema);
            PreparedSchemaCatalogWrite prepared = catalog.Prepare();
            ObjectVersionRecord Text((ObjectId Id, byte[] Body) row) => ObjectVersionRecord.CreateBase(row.Id.Value,
                TypedBaseBodyCodec.Encode(ObjectLayout.String, new(row.Body)));
            ObjectVersionRecord Owner(byte[] body) => ObjectVersionRecord.CreateBase(ownerId,
                TypedBaseBodyCodec.Encode(ObjectLayout.ForDurable(schema), new(body), prepared.View));
            using (PendingRepositorySave pending = repository.Prepare("main", StoredGraphKind.State,
                new(null, input.Owner, input.Strings.Where(row => row.Id.Value != aliasId).Select(Text).Append(Owner(input.Base))), prepared.Write)) {
                first = repository.Commit(pending, prepared.Install, prepareMeta: prepared.PrepareInstall).Business.Address;
            }
            selectedSchema = repository.SharedSchemaRoot!.Value;
            StoredGraphRevision initial = repository.Store.ReadGraph(first);
            ObjectVersionRecord[] edits = [ObjectVersionRecord.CreateDelta(ownerId, initial.ObjectHeads[ownerId], input.First.Body),
                Text(input.Strings.Single(row => row.Id.Value == aliasId))];
            using (PendingRepositorySave pending = repository.Prepare("main", StoredGraphKind.State, new(first, input.Owner, edits))) {
                second = repository.Commit(pending).Business.Address;
            }
            StoredGraphRevision middle = repository.Store.ReadGraph(second);
            using (PendingRepositorySave pending = repository.Prepare("main", StoredGraphKind.State,
                new(second, input.Owner, [ObjectVersionRecord.CreateDelta(ownerId, middle.ObjectHeads[ownerId], input.Second.Body)]))) {
                third = repository.Commit(pending).Business.Address;
            }
            Assert.Equal(third, repository.GetHead("main"));
            Assert.Equal(third, repository.GetStateHead("main"));
            Assert.Equal(selectedSchema, repository.Store.ReadGraph(third).SchemaRoot);

            // Canonical, completed experimental siblings use the real writer; none is published as a branch.
            repeated = repository.Store.Append(StoredGraphKind.State, first, selectedSchema, new(first, input.Owner, edits)).Address;
            Assert.Equal(input.First.Body.ToArray(), repository.Store.ReadObjectChain(repeated, ownerId).Entries[^1].Record.Body.ToArray());
            missingString = repository.Store.Append(StoredGraphKind.State, third, selectedSchema, new(third, input.Owner, [], [nameId])).Address;
            wrongKind = repository.Store.Append(StoredGraphKind.State, third, selectedSchema,
                new(third, input.Owner, [ObjectVersionRecord.CreateBase(nameId,
                    TypedBaseBodyCodec.Encode(ObjectLayout.ForDurable(schema), new(expected[2]), prepared.View))])).Address;
            malformed = repository.Store.Append(StoredGraphKind.State, third, selectedSchema,
                new(third, input.Owner, [ObjectVersionRecord.CreateDelta(ownerId,
                    repository.Store.ReadGraph(third).ObjectHeads[ownerId], new byte[] { 8, 8, 0 })])).Address;
            Assert.Equal(third, repository.GetHead("main"));
            repository.Data.ConfirmDurable();
        }
        Array.Clear(input.Base);
        Dictionary<string, byte[]> bytes = Directory.EnumerateFiles(directory.Path, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(directory.Path, path), File.ReadAllBytes);
        DecodedRevision[] owned;
        using (RepositoryStorageFacade reopened = RepositoryStorageFacade.OpenReadOnlyExisting(directory.Path)) {
            Assert.Equal(third, reopened.GetHead("main"));
            Assert.Equal(selectedSchema, reopened.SharedSchemaRoot);
            owned = new[] { first, second, third, repeated }.Select(address => DecodedRevision.ReadStoredGraph(reopened.Store, address, models())).ToArray();
            for (int i = 0; i < owned.Length; i++) Assert.Equal(expected[i == 3 ? 1 : i], check(owned[i]));
            ObjectVersionChain chain = reopened.Store.ReadObjectChain(third, ownerId);
            Assert.Equal(new[] { first, second, third }.Select(address => reopened.Store.ReadGraph(address).OwnedBatch!.Value),
                chain.Entries.Select(entry => entry.ContainingBatch));
            Assert.Equal(chain.Entries.Sum(entry => (long)entry.PayloadBytes), chain.ReconstructionPayloadBytes);
            Assert.Equal(reopened.Store.ReadGraph(third).ObjectHeads[ownerId], chain.Head);
            Assert.Equal(reopened.Store.ReadGraph(second).ObjectHeads[ownerId], chain.Entries[2].Record.PriorAddress);
            Assert.True(chain.ReconstructionPayloadBytes > reopened.Store.ReadObjectChain(first, ownerId).ReconstructionPayloadBytes);
            foreach (FrameAddress invalid in new[] { missingString, wrongKind, malformed }) {
                DecodedRevision? delivered = null;
                Assert.Throws<InvalidDataException>(() => delivered = DecodedRevision.ReadStoredGraph(reopened.Store, invalid, models()));
                Assert.Null(delivered);
                Assert.Equal(expected[2], check(owned[2]));
            }
            Assert.Throws<InvalidDataException>(() => DecodedRevision.ReadStoredGraph(reopened.Store, third, new StateModelRegistry()));
            Assert.Equal(expected[0], check(DecodedRevision.ReadStoredGraph(reopened.Store, first, models())));
        }
        Assert.Equal(expected[2], check(owned[2])); // DTOs and decoded string identity survive all backing lifetimes.
        Assert.Equal(bytes.Keys.Order(), Directory.EnumerateFiles(directory.Path, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(directory.Path, path)).Order());
        foreach ((string path, byte[] before) in bytes) Assert.Equal(before, File.ReadAllBytes(Path.Combine(directory.Path, path)));

        // Same owner key/version but a different complete ancestor layout cannot borrow the generated reader.
        using RawBaseDirectory alternate = new();
        DurableSchema wrongAncestor = new(schema.BaseSchema!.Type, schema.BaseSchema.Version, []);
        DurableSchema wrongSchema = new(schema.Type, schema.Version, schema.Fields.ToArray(), wrongAncestor);
        using RepositoryStorageFacade wrongRepository = RepositoryStorageFacade.CreateNew(alternate.Path);
        SchemaCatalogWorkspace wrongCatalog = new(wrongRepository.EnsureEditingAllowed);
        wrongCatalog.Set(wrongSchema);
        PreparedSchemaCatalogWrite wrong = wrongCatalog.Prepare();
        ObjectVersionRecord[] alternateRows = input.Strings.Select(row => ObjectVersionRecord.CreateBase(row.Id.Value,
            TypedBaseBodyCodec.Encode(ObjectLayout.String, new(row.Body)))).Append(ObjectVersionRecord.CreateBase(ownerId,
                TypedBaseBodyCodec.Encode(ObjectLayout.ForDurable(wrongSchema), new(expected[2]), wrong.View))).ToArray();
        using PendingRepositorySave wrongPending = wrongRepository.Prepare("main", StoredGraphKind.State, new(null, input.Owner, alternateRows), wrong.Write);
        FrameAddress wrongGraph = wrongRepository.Commit(wrongPending, wrong.Install, prepareMeta: wrong.PrepareInstall).Business.Address;
        Assert.Throws<InvalidDataException>(() => DecodedRevision.ReadStoredGraph(wrongRepository.Store, wrongGraph, models()));
    }

    [Fact]
    public void PersistedHistoricalDeltaChainUsesRemovedAncestorLayoutAndPersistedExactSchema() {
        using AncestryHistoryDirectory files = new();
        SchemaHistoryTool publisher = new();
        GeneratorTestRun initial = RunGenerator(FusedDeltaPreamble + """
            [DurableType("persisted.base", 1)]
            public abstract partial class OldBase : IDurableObject { [DurableField(99)] private int _number; }
            [DurableType("persisted.leaf", 1)]
            public sealed partial class Leaf : OldBase { [DurableField(99)] private bool _flag; }
            """);
        AssertSchemaOnlyCompiles(initial);
        publisher.Publish(files.WriteManifest(initial), files.History);
        string source = FusedDeltaPreamble + """
            [DurableType("persisted.base", 2)]
            public abstract partial class NewBase : IDurableObject { [DurableField(2)] private byte _small; }
            [DurableType("persisted.leaf", 2)]
            public sealed partial class Leaf : NewBase {
                [DurableField(1)] private uint _number;
                internal static void UpgradeStateV1ToV2(in __DurableState.V1 old, out __DurableState.V2 next) =>
                    next = new(checked((byte)old.Segment0Field99), old.Segment1Field99 ? 1u : 0u);
            }
            public static class Host {
            """ + FusedDeltaHostMethods("Leaf", 1) + FusedDeltaHostMethods("Leaf", 2) + """
                public static Atelia.DurableGraph.Persistence.StateModelRegistry Models() {
                    Atelia.DurableGraph.Persistence.StateModelRegistry models = new();
                    Leaf.__DurableState.RegisterModel(models);
                    return models;
                }
            }
            """;
        GeneratorTestRun current = RunGenerator(source, files.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(current);
        Assembly assembly = EmitAndLoad(current.OutputCompilation);
        Assert.Null(assembly.GetType("FusedDelta.OldBase"));
        Type leaf = assembly.GetType("FusedDelta.Leaf")!;
        DurableSchema oldSchema = ReadSchemaOnly(leaf, 1), newSchema = ReadSchemaOnly(leaf, 2);
        Assert.Equal(TypeTag.Int32, Assert.Single(oldSchema.BaseSchema!.Fields).TypeTag);
        Assert.Equal(TypeTag.Byte, Assert.Single(newSchema.BaseSchema!.Fields).TypeTag);
        Type host = assembly.GetType("FusedDelta.Host")!;
        var prepare = host.GetMethod("Prepare1")!.CreateDelegate<Func<byte[], byte[], PreparedDeltaBody>>();
        var models = host.GetMethod("Models")!.CreateDelegate<Func<StateModelRegistry>>();
        byte[] body = [2, 0];
        PreparedDeltaBody delta1 = prepare(body, [2, 1]), delta2 = prepare([2, 1], [4, 1]);
        Assert.Equal<byte>([2, 1], delta1.Body.ToArray());
        Assert.Equal<byte>([1, 4], delta2.Body.ToArray());
        using RawBaseDirectory directory = new();
        FrameAddress first, second, third, schemaRoot;
        using (RepositoryStorageFacade repository = RepositoryStorageFacade.CreateNew(directory.Path)) {
            SchemaCatalogWorkspace catalog = new(repository.EnsureEditingAllowed);
            catalog.Set(oldSchema);
            catalog.Set(newSchema);
            PreparedSchemaCatalogWrite prepared = catalog.Prepare();
            using (PendingRepositorySave pending = repository.Prepare("main", StoredGraphKind.State,
                new(null, new(1), [ObjectVersionRecord.CreateBase(1,
                    TypedBaseBodyCodec.Encode(ObjectLayout.ForDurable(oldSchema), new(body), prepared.View))]), prepared.Write)) {
                first = repository.Commit(pending, prepared.Install, prepareMeta: prepared.PrepareInstall).Business.Address;
            }
            schemaRoot = repository.SharedSchemaRoot!.Value;
            using (PendingRepositorySave pending = repository.Prepare("main", StoredGraphKind.State,
                new(first, new(1), [ObjectVersionRecord.CreateDelta(1, repository.Store.ReadGraph(first).ObjectHeads[1], delta1.Body)]))) {
                second = repository.Commit(pending).Business.Address;
            }
            using (PendingRepositorySave pending = repository.Prepare("main", StoredGraphKind.State,
                new(second, new(1), [ObjectVersionRecord.CreateDelta(1, repository.Store.ReadGraph(second).ObjectHeads[1], delta2.Body)]))) {
                third = repository.Commit(pending).Business.Address;
            }
            Assert.Equal(third, repository.GetStateHead("main"));
        }
        Array.Clear(body);
        Dictionary<string, byte[]> bytes = Directory.EnumerateFiles(directory.Path, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(directory.Path, path), File.ReadAllBytes);
        DecodedRevision retained;
        using (RepositoryStorageFacade reopened = RepositoryStorageFacade.OpenReadOnlyExisting(directory.Path)) {
            SchemaCatalogView catalog = SchemaCatalogView.Load(reopened.Store, schemaRoot);
            Assert.Equal(oldSchema, catalog.Schemas[new(oldSchema.Type, oldSchema.Version)]);
            DecodedRevision middle = DecodedRevision.ReadStoredGraph(reopened.Store, second, models());
            retained = DecodedRevision.ReadStoredGraph(reopened.Store, third, models());
            Assert.Equal(oldSchema, retained.GetRequired(new(1)).Schema);
            Assert.Equal(TypeTag.Int32, Assert.Single(retained.GetRequired(new(1)).Schema!.BaseSchema!.Fields).TypeTag);
            Assert.Equal(1, ReadStoredInt(middle));
            Assert.Equal(2, ReadStoredInt(retained));
            Assert.Equal(3, reopened.Store.ReadObjectChain(third, 1).Entries.Count);
            NormalizedGraphState normalized = NormalizedGraphState.Create(retained, retained.SourceModels!);
            Assert.True(normalized.Objects[new(1)].RequiresRewrite);
            Assert.Equal(newSchema, normalized.Objects[new(1)].Current.Schema);
        }
        Assert.Equal(2, ReadStoredInt(retained));
        foreach ((string path, byte[] before) in bytes) Assert.Equal(before, File.ReadAllBytes(Path.Combine(directory.Path, path)));
        Assert.Equal(bytes.Keys.Order(), Directory.EnumerateFiles(directory.Path, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(directory.Path, path)).Order());

        FrameAddress rewritten;
        using (RepositoryStorageFacade reopened = RepositoryStorageFacade.OpenExisting(directory.Path)) {
            BranchSaveSession session = new(reopened, "main", models());
            Assert.Equal(third, session.StateSource!.Address);
            Assert.True(session.Baseline!.Objects[new(1)].RequiresRewrite);
            using PreparedBranchSave save = session.Prepare(session.State!);
            ObjectVersionRecord record = Assert.Single(save.Business.Write.Records);
            Assert.Equal(ObjectVersionKind.Base, record.Kind);
            rewritten = save.Commit().Business.Address;
            Assert.False(session.Baseline!.Objects[new(1)].RequiresRewrite);
            Assert.Equal(schemaRoot, reopened.SharedSchemaRoot);
        }
        using RepositoryStorageFacade fresh = RepositoryStorageFacade.OpenReadOnlyExisting(directory.Path);
        DecodedRevision old = DecodedRevision.ReadStoredGraph(fresh.Store, third, models());
        DecodedRevision updated = DecodedRevision.ReadStoredGraph(fresh.Store, rewritten, models());
        Assert.Equal(oldSchema, old.GetRequired(new(1)).Schema);
        Assert.Equal(newSchema, updated.GetRequired(new(1)).Schema);
        object upgraded = updated.GetRequired(new(1)).Content;
        Assert.Equal((byte)2, (byte)upgraded.GetType().GetField("Segment0Field2", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(upgraded)!);
        Assert.Equal(1u, (uint)upgraded.GetType().GetField("Segment1Field1", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(upgraded)!);
        Assert.Equal(third, fresh.Store.ReadGraph(rewritten).SourceGraph);
        Assert.Single(fresh.Store.ReadObjectChain(rewritten, 1).Entries);
        Assert.Equal(3, fresh.Store.ReadObjectChain(third, 1).Entries.Count);

        static int ReadStoredInt(DecodedRevision view) {
            object value = view.GetRequired(new(1)).Content;
            return (int)value.GetType().GetField("Segment0Field99", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(value)!;
        }
    }

    private static byte[] ExpectedCapturedBody(uint name, uint alias, uint empty, int score) {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new BareValueWriter(buffer);
        writer.WriteVarUInt32(name);
        writer.WriteVarUInt32(alias);
        writer.WriteVarUInt32(empty);
        writer.WriteVarInt32(score);
        return buffer.WrittenSpan.ToArray();
    }

    private delegate (ObjectId Owner, ObjectId Name, ObjectId Alias, ObjectId Empty, DurableSchema Schema,
        (ObjectId Id, byte[] Body)[] Strings, byte[] Base, PreparedDeltaBody First, PreparedDeltaBody Second) PersistedCapture();

    private const string PersistedDeltaCaptureSource = """
        using Atelia.Binary;
        using System;
        using System.Buffers;
        using System.Collections.Generic;
        using System.IO;
        using System.Linq;
        using Atelia.DurableGraph;
        using Atelia.DurableGraph.Schema;
        using Atelia.DurableGraph.Runtime;
        using Atelia.DurableGraph.Persistence;
        using Atelia.DurableGraph.Storage;
        using Atelia.FrameStore;
        using Atelia.DurableGraph.Serialization;
        namespace PersistedCapture;
        [DurableType("persisted.capture.base", 1)]
        public abstract partial class Base : IDurableObject {
            [DurableField(1)] private string _name;
            protected Base(string name) { _name = name; }
            public void Rename(string name) { _name = name; }
        }
        [DurableType("persisted.capture.leaf", 1)]
        public sealed partial class Leaf : Base {
            [DurableField(1)] private string _alias;
            [DurableField(9)] private string _empty = string.Empty;
            [DurableField(20)] private int _score = 1;
            public Leaf(string shared) : base(shared) { _alias = shared; }
            public void Change(string alias, int score) { _alias = alias; _score = score; }
        }
        public static class Host {
            private static byte[] Write(in Leaf.__DurableState.V1 state) {
                var buffer = new ArrayBufferWriter<byte>();
                var writer = new BareValueWriter(buffer);
                Leaf.__DurableState.WriteBaseBody(ref writer, in state);
                return buffer.WrittenSpan.ToArray();
            }
            public static (ObjectId Owner, ObjectId Name, ObjectId Alias, ObjectId Empty, DurableSchema Schema,
                (ObjectId Id, byte[] Body)[] Strings, byte[] Base, PreparedDeltaBody First, PreparedDeltaBody Second) Capture() {
                string shared = new string(new[] { 'x' });
                string equal = new string(new[] { 'x' });
                if (ReferenceEquals(shared, equal)) throw new Exception("Distinct fixture strings.");
                var owner = new Leaf(shared);
                var session = new CaptureSession();
                CapturedGraph Capture() {
                    var context = session.BeginCapture();
                    Leaf.__DurableState.AddRoot(context, owner);
                    return context.Seal();
                }
                var first = Capture();
                owner.Rename("mutation after Seal"); owner.Change("mutation", 90);
                var prior = first.Objects.Single(item => item.Kind == ObjectStateKind.Durable).GetState<Leaf.__DurableState.V1>();
                session.Accept(first); // Fixture's in-memory baseline only; not a durable publication.
                owner.Rename(shared); owner.Change(equal, 2);
                var second = Capture();
                owner.Rename("mutation after Seal"); owner.Change("mutation", 91);
                var middle = second.Objects.Single(item => item.Kind == ObjectStateKind.Durable).GetState<Leaf.__DurableState.V1>();
                var delta1 = Leaf.__DurableState.PrepareDeltaBody(in prior, in middle);
                session.Accept(second);
                owner.Rename(shared); owner.Change(equal, 3);
                var third = Capture();
                owner.Rename("mutation after Seal"); owner.Change("mutation", 92);
                var current = third.Objects.Single(item => item.Kind == ObjectStateKind.Durable).GetState<Leaf.__DurableState.V1>();
                var delta2 = Leaf.__DurableState.PrepareDeltaBody(in middle, in current);
                var strings = third.Objects.Where(item => item.Kind == ObjectStateKind.String).Select(item => {
                    var buffer = new ArrayBufferWriter<byte>();
                    var writer = new BareValueWriter(buffer);
                    writer.WriteString(item.StringContent);
                    return (item.Id, buffer.WrittenSpan.ToArray());
                }).ToArray();
                session.Discard(third);
                return (first.RootIds[0], prior.Segment0Field1, middle.Segment1Field1, prior.Segment1Field9,
                    Leaf.__DurableState.V1.Schema, strings, Write(in prior), delta1, delta2);
            }
            public static Atelia.DurableGraph.Persistence.StateModelRegistry Models() {
                Atelia.DurableGraph.Persistence.StateModelRegistry models = new();
                Leaf.__DurableState.RegisterModel(models);
                return models;
            }
            public static byte[] Check(DecodedRevision view) {
                var state = view.Objects.Single(row => row.Kind == ObjectStateKind.Durable).GetState<Leaf.__DurableState.V1>();
                string name = view.Strings.ResolveString(state.Segment0Field1)!;
                string alias = view.Strings.ResolveString(state.Segment1Field1)!;
                if (name != "x" || alias != "x" || ReferenceEquals(name, alias) != (state.Segment1Field20 == 1) ||
                    !ReferenceEquals(view.Strings.ResolveString(state.Segment1Field9), string.Empty))
                    throw new InvalidDataException("Restored identity or frozen values.");
                return Write(in state);
            }
        }
        """;
}
