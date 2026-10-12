using System.Security.Cryptography;
using Atelia.DurableGraph.Build;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Persistence.MetaBootstrapP0;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using Atelia.VersionStore;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Fact]
    public void MetaBootstrapGenericHistorySelectsOpenAndClosedProvidersThenRewritesBaseAndDelta() {
        using AncestryHistoryDirectory history = new();
        var (oldHost, currentHost) = CompileBootstrapGenericUpgradeHosts(history);
        DurableSchema[] old = ExpectedBootstrapGenericUpgradeSchemas(1), current = ExpectedBootstrapGenericUpgradeSchemas(2);
        Assert.Equal(old[0], current[0]); // Root references are nominal; only the two Box representations change.

        using RawBaseDirectory files = new();
        FrameAddress s0, s1, s2, s3, meta0, meta1;
        uint[] ids;
        using (P0Repository repository = P0Repository.CreateNew(files.Path)) {
            P0Session session = new(repository, "main", oldHost.Models(0));
            IDurableObject root = oldHost.Create("root");
            oldHost.Check(root, 0);
            using P0SessionSave save = session.Prepare(root);
            AssertBootstrapGenericUpgradeCatalog(save.Catalog.View, old);
            Assert.Equal(3, save.Business.Write.Records.Count);
            Assert.All(save.Business.Write.Records, row => Assert.Equal(ObjectVersionKind.Base, row.Kind));
            P0SaveResult result = save.Commit();
            s0 = result.Business.Address;
            meta0 = result.Meta!.Address;
            P0DecodedRows decoded = ReadBootstrapGenericUpgradeRows(repository, s0, oldHost.Models(0));
            oldHost.CheckStored(decoded.Rows.Values.ToArray(), 1, 0);
            ids = old.Take(3).Select(schema => Assert.Single(decoded.Rows.Values, row => row.Schema!.Equals(schema)).Id.Value).ToArray();
            Assert.Equal(ids[0], result.Business.RootId.Value);
        }

        Dictionary<string, string> before = BootstrapGenericUpgradeFileHashes(files.Path);
        using (P0Repository repository = P0Repository.OpenReadOnlyExisting(files.Path)) {
            P0CatalogView catalog = P0CatalogView.Load(repository.Store, meta0); // No business registry is involved.
            AssertBootstrapGenericUpgradeCatalog(catalog, old);
            currentHost.ResetCalls();
            P0DecodedRows decoded = ReadBootstrapGenericUpgradeRows(repository, s0, currentHost.Models(0));
            currentHost.CheckStored(decoded.Rows.Values.ToArray(), 1, 0);
            Assert.Equal(0, currentHost.CallCount()); // Real historical DTOs from the current independent assembly.
            IDurableObject restored = P0BusinessWorkspace.ReadRoot(repository.Store, s0, currentHost.Models(0));
            currentHost.Check(restored, 1);
            currentHost.CheckCalls(ids.Skip(1).ToArray()); // Open int and closed Point must both run, once each.
            Assert.Equal(s0, repository.GetHead("main"));
            Assert.Equal(meta0, repository.SharedSchemaRoot);
            Assert.Equal(0, repository.Store.AppendedFrames);
        }
        AssertBootstrapGenericUpgradeFilesUnchanged(before, files.Path);

        using (P0Repository repository = P0Repository.OpenExisting(files.Path)) {
            StateModelRegistry models = currentHost.Models(0);
            P0Session session = new(repository, "main", models);
            IDurableObject root = session.State!;
            currentHost.Check(root, 1);
            object[] identities = currentHost.Objects(root);
            P0GraphSnapshot source = session.StateSource!;
            Assert.Equal(s0, source.Address);
            using (P0SessionSave save = session.Prepare(root)) {
                Assert.NotNull(save.Catalog.Write);
                AssertBootstrapGenericUpgradeCatalog(save.Catalog.View, old.Concat(current).Distinct().ToArray());
                Assert.Equal(s0, save.Business.Write.SourceGraph);
                Assert.Equal<uint>(ids.Skip(1).Order(), save.Business.Write.Records.Select(row => row.ObjectId));
                Assert.All(save.Business.Write.Records, row => Assert.Equal(ObjectVersionKind.Base, row.Kind));
                Assert.Empty(save.Business.Write.RemovedIds);
                P0SaveResult result = save.Commit();
                s1 = result.Business.Address;
                meta1 = result.Meta!.Address;
                Assert.NotEqual(meta0, meta1);
                Assert.Equal(source.Objects[ids[0]], result.Business.Objects[ids[0]]);
                foreach (uint id in ids.Skip(1)) { Assert.Single(repository.Store.ReadObjectChain(s1, id).Entries); }
            }
            Assert.Equal(s1, session.StateSource!.Address);
            Assert.Equal(meta1, repository.SharedSchemaRoot);
            P0CatalogView accepted = session.Catalog.Accepted!;
            Assert.NotEqual(accepted.DescriptorId(ObjectLayout.ForDurable(current[1])), accepted.DescriptorId(ObjectLayout.ForDurable(current[2])));
            foreach (int index in new[] { 1, 2 }) {
                Assert.NotEqual(accepted.DescriptorId(ObjectLayout.ForDurable(old[index])), accepted.DescriptorId(ObjectLayout.ForDurable(current[index])));
            }
            currentHost.CheckStored(ReadBootstrapGenericUpgradeRows(repository, s1, models).Rows.Values.ToArray(), 2, 1);

            currentHost.Change(root);
            currentHost.Check(root, 2);
            P0GraphSnapshot prior = session.StateSource!;
            using (P0SessionSave save = session.Prepare(root)) {
                Assert.Null(save.Catalog.Write);
                Assert.Empty(save.Catalog.EncodedIds);
                Assert.Equal(s1, save.Business.Write.SourceGraph);
                Assert.Equal<uint>(ids.Skip(1).Order(), save.Business.Write.Records.Select(row => row.ObjectId));
                foreach (ObjectVersionRecord row in save.Business.Write.Records) {
                    Assert.Equal(ObjectVersionKind.Delta, row.Kind);
                    Assert.Equal(prior.Objects[row.ObjectId], row.PriorAddress);
                }
                P0SaveResult result = save.Commit();
                s2 = result.Business.Address;
                Assert.Equal(meta1, result.Meta!.Address);
                Assert.Equal(source.Objects[ids[0]], result.Business.Objects[ids[0]]);
                foreach (uint id in ids.Skip(1)) {
                    P0ObjectChain chain = repository.Store.ReadObjectChain(s2, id);
                    Assert.Equal(2, chain.Entries.Count);
                    Assert.Equal(meta1, chain.Entries[0].SchemaRoot);
                    Assert.Equal(ObjectVersionKind.Delta, chain.Entries[^1].Record.Kind);
                }
            }
            object[] after = currentHost.Objects(session.State!);
            for (int index = 0; index < identities.Length; index++) { Assert.Same(identities[index], after[index]); }
            currentHost.CheckStored(ReadBootstrapGenericUpgradeRows(repository, s2, models).Rows.Values.ToArray(), 2, 2);
            prior = session.StateSource!;
            using (P0SessionSave save = session.Prepare(root)) {
                Assert.Null(save.Catalog.Write);
                Assert.Empty(save.Business.Write.Records);
                P0SaveResult result = save.Commit();
                s3 = result.Business.Address;
                Assert.Null(result.Business.OwnedBatch);
                Assert.Equal(prior.DirectoryRoot, result.Business.DirectoryRoot);
                Assert.Equal(source.Objects[ids[0]], result.Business.Objects[ids[0]]);
            }
        }

        before = BootstrapGenericUpgradeFileHashes(files.Path);
        using (P0Repository repository = P0Repository.OpenReadOnlyExisting(files.Path)) {
            Assert.Equal(s3, repository.GetStateHead("main"));
            Assert.Equal(meta1, repository.SharedSchemaRoot);
            AssertBootstrapGenericUpgradeCatalog(P0CatalogView.Load(repository.Store, meta0), old);
            AssertBootstrapGenericUpgradeCatalog(P0CatalogView.Load(repository.Store, meta1), old.Concat(current).Distinct().ToArray());
            oldHost.Check(P0BusinessWorkspace.ReadRoot(repository.Store, s0, oldHost.Models(0)), 0);
            currentHost.Check(P0BusinessWorkspace.ReadRoot(repository.Store, s0, currentHost.Models(0)), 1);
            currentHost.Check(P0BusinessWorkspace.ReadRoot(repository.Store, s1, currentHost.Models(0)), 1);
            currentHost.Check(P0BusinessWorkspace.ReadRoot(repository.Store, s2, currentHost.Models(0)), 2);
            currentHost.Check(P0BusinessWorkspace.ReadRoot(repository.Store, s3, currentHost.Models(0)), 2);
            Assert.Equal(meta0, repository.Store.ReadGraph(s0).SchemaRoot);
            Assert.Equal(0, repository.Store.AppendedFrames);
        }
        AssertBootstrapGenericUpgradeFilesUnchanged(before, files.Path);
    }

    [Theory]
    [InlineData(1, "int", "upgrade capability is missing")]
    [InlineData(2, "point", "cannot bind different DTO representations")]
    [InlineData(3, "point", "does not match its exact DTO endpoints")]
    public void MetaBootstrapGenericHistoryRejectsMissingOrWrongEndpointBeforeThatOwnerCallback(int mode, string rootKind, string message) {
        using AncestryHistoryDirectory history = new();
        var (oldHost, currentHost) = CompileBootstrapGenericUpgradeHosts(history);
        using RawBaseDirectory files = new();
        FrameAddress source, schema;
        uint ownerId;
        using (P0Repository repository = P0Repository.CreateNew(files.Path)) {
            P0Session session = new(repository, "main", oldHost.Models(0));
            using P0SessionSave save = session.Prepare(oldHost.Create(rootKind));
            P0SaveResult result = save.Commit();
            source = result.Business.Address;
            schema = result.Meta!.Address;
            ownerId = result.Business.RootId.Value;
            Assert.Single(result.Business.Objects); // No other owner can obscure the callback rejection stage.
        }
        Dictionary<string, string> before = BootstrapGenericUpgradeFileHashes(files.Path);
        using (P0Repository repository = P0Repository.OpenReadOnlyExisting(files.Path)) {
            var branch = repository.Versions.ReadRef(1).Unwrap();
            var shared = repository.Versions.ReadRef(2).Unwrap();
            currentHost.ResetCalls();
            currentHost.Check(P0BusinessWorkspace.ReadRoot(repository.Store, source, currentHost.Models(0)), 1);
            currentHost.CheckCalls([ownerId]); // The same physical source succeeds with complete capabilities.

            currentHost.ResetCalls();
            StateModelRegistry rejected = currentHost.Models(mode);
            P0DecodedRows decoded = ReadBootstrapGenericUpgradeRows(repository, source, rejected);
            currentHost.CheckStored(decoded.Rows.Values.ToArray(), 1, 0);
            Assert.Equal(0, currentHost.CallCount()); // Endpoint changes do not revoke the real historical reader.
            InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
                P0BusinessWorkspace.ReadRoot(repository.Store, source, rejected));
            Assert.Contains(message, error.Message);
            Assert.Equal(0, currentHost.CallCount());
            Assert.False(repository.IsFaulted);
            Assert.Equal(source, repository.GetHead("main"));
            Assert.Equal(schema, repository.SharedSchemaRoot);
            Assert.Equal(branch.Revision, repository.Versions.ReadRef(1).Unwrap().Revision);
            Assert.Equal(shared.Revision, repository.Versions.ReadRef(2).Unwrap().Revision);
            Assert.Equal(branch.Payload.ToArray(), repository.Versions.ReadRef(1).Unwrap().Payload.ToArray());
            Assert.Equal(shared.Payload.ToArray(), repository.Versions.ReadRef(2).Unwrap().Payload.ToArray());
            Assert.Equal(0, repository.Store.AppendedFrames);
        }
        AssertBootstrapGenericUpgradeFilesUnchanged(before, files.Path);
    }

    private static (BootstrapGenericUpgradeHost Old, BootstrapGenericUpgradeHost Current) CompileBootstrapGenericUpgradeHosts(AncestryHistoryDirectory history) {
        SchemaHistoryTool publisher = new();
        GeneratorTestRun old = RunGenerator(BootstrapGenericUpgradeSource(1), [], null, "true");
        AssertSchemaOnlyCompiles(old);
        BootstrapGenericUpgradeHost oldHost = BootstrapGenericUpgradeHost.Load(old);
        publisher.Publish(history.WriteManifest(old), history.History);
        Dictionary<string, string> previous = history.ReadContents();
        Assert.Equal(3, previous.Count); // Three open family definitions, not four runtime closed Schemas.
        GeneratorTestRun current = RunGenerator(BootstrapGenericUpgradeSource(2), history.ReadAdditionalTexts(), null, "true");
        AssertSchemaOnlyCompiles(current);
        BootstrapGenericUpgradeHost currentHost = BootstrapGenericUpgradeHost.Load(current);
        publisher.Publish(history.WriteManifest(current), history.History);
        publisher.Verify(history.WriteManifest(current), history.History);
        Assert.Equal(5, history.ReadContents().Count);
        foreach ((string name, string contents) in previous) { Assert.Equal(contents, File.ReadAllText(Path.Combine(history.History, name))); }
        Assert.NotSame(oldHost.ClrType, currentHost.ClrType);
        Assert.Equal(typeof(int), oldHost.ClrType.Assembly.GetType("BootstrapGenericUpgrade.Point")!.GetField("X")!.FieldType);
        Assert.Equal(typeof(long), currentHost.ClrType.Assembly.GetType("BootstrapGenericUpgrade.Point")!.GetField("X")!.FieldType);
        return (oldHost, currentHost);
    }

    private static P0DecodedRows ReadBootstrapGenericUpgradeRows(P0Repository repository, FrameAddress graph, StateModelRegistry models) {
        P0CatalogView selected = P0CatalogView.Load(repository.Store, repository.Store.ReadGraph(graph).SchemaRoot!.Value);
        return P0TypedReader.Read(repository.Store, graph, models.Snapshot(schemaLookup: key => selected.Schemas.GetValueOrDefault(key)), selected);
    }

    private static DurableSchema[] ExpectedBootstrapGenericUpgradeSchemas(int version) {
        TypeExpr numbers = TypeExpr.Named("GU.Box", TypeExpr.Builtin(TypeTag.Int32)), points = TypeExpr.Named("GU.Box", TypeExpr.Named("GU.Point"));
        DurableSchema point = new("GU.Point", version, SchemaKind.InlineValue, new DurableFieldInfo(1, version == 1 ? TypeTag.Int32 : TypeTag.Int64));
        DurableSchema Box(TypeExpr type, DurableFieldInfo value) {
            List<DurableFieldInfo> fields = [value];
            if (version == 2) { fields.Add(new(2, TypeTag.Int64)); }
            fields.Add(DurableFieldInfo.Reference(3, type));
            for (int field = 4; field <= 7; field++) { fields.Add(new(field, TypeTag.Double)); }
            return new(type, version, fields.ToArray());
        }
        DurableSchema root = new("GU.Root", 1, [DurableFieldInfo.Reference(1, numbers), DurableFieldInfo.Reference(2, points),
            DurableFieldInfo.Reference(3, numbers), DurableFieldInfo.Reference(4, points), DurableFieldInfo.Reference(5, TypeExpr.Named("GU.Root"))]);
        return [root, Box(numbers, new(1, TypeTag.Int32)), Box(points, new(1, TypeTag.InlineValue, inlineSchema: point)), point];
    }

    private static void AssertBootstrapGenericUpgradeCatalog(P0CatalogView catalog, DurableSchema[] expected) {
        Assert.Equal(expected.Length, catalog.Schemas.Count);
        Assert.Equal(expected.Length, catalog.DescriptorIds.Count);
        foreach (DurableSchema schema in expected) {
            Assert.Equal(schema, catalog.Schemas[new(schema.Type, schema.Version)]);
            if (schema.Kind == SchemaKind.ReferenceObject) { Assert.Equal(ObjectLayout.ForDurable(schema), catalog.ResolveLayout(catalog.SchemaNodeId(schema))); }
            else { Assert.Throws<InvalidDataException>(() => catalog.ResolveLayout(catalog.SchemaNodeId(schema))); }
        }
    }

    private static Dictionary<string, string> BootstrapGenericUpgradeFileHashes(string path) => Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
        .ToDictionary(file => Path.GetRelativePath(path, file), file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))), StringComparer.Ordinal);
    private static void AssertBootstrapGenericUpgradeFilesUnchanged(Dictionary<string, string> before, string path) {
        Dictionary<string, string> after = BootstrapGenericUpgradeFileHashes(path);
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        foreach ((string name, string contents) in before) { Assert.Equal(contents, after[name]); }
    }

    private sealed class BootstrapGenericUpgradeHost(Type host) {
        public Type ClrType => host.Assembly.GetType("BootstrapGenericUpgrade.Root")!;
        public Func<int, StateModelRegistry> Models { get; } = host.GetMethod("Models")!.CreateDelegate<Func<int, StateModelRegistry>>();
        public Func<string, IDurableObject> Create { get; } = host.GetMethod("Create")!.CreateDelegate<Func<string, IDurableObject>>();
        public Action<IDurableObject, int> Check { get; } = host.GetMethod("Check")!.CreateDelegate<Action<IDurableObject, int>>();
        public Action<ObjectStateRecord[], int, int> CheckStored { get; } = host.GetMethod("CheckStored")!.CreateDelegate<Action<ObjectStateRecord[], int, int>>();
        public Func<IDurableObject, object[]> Objects { get; } = host.GetMethod("Objects")!.CreateDelegate<Func<IDurableObject, object[]>>();
        public Action<IDurableObject> Change { get; } = host.GetMethod("Change")!.CreateDelegate<Action<IDurableObject>>();
        public Action ResetCalls { get; } = host.GetMethod("ResetCalls")!.CreateDelegate<Action>();
        public Func<int> CallCount { get; } = host.GetMethod("CallCount")!.CreateDelegate<Func<int>>();
        public Action<uint[]> CheckCalls { get; } = host.GetMethod("CheckCalls")!.CreateDelegate<Action<uint[]>>();
        public static BootstrapGenericUpgradeHost Load(GeneratorTestRun run) => new(EmitAndLoad(run.OutputCompilation).GetType("BootstrapGenericUpgrade.Host")!);
    }

    private static string BootstrapGenericUpgradeSource(int version) {
        string providers = version == 2 ? """
            public static class Upgrades {
                public static readonly List<(string Side, UpgradeContext Context)> Calls = new();
                [DurableUpgrade(typeof(Box<>), 1)]
                public static void Open<TState>(in B.V1<TState> prior, out B.V2<TState> next, UpgradeContext context) where TState : unmanaged {
                    Calls.Add(("open", context)); next = new(prior.Segment0Field1, 100, prior.Segment0Field3,
                        prior.Segment0Field4, prior.Segment0Field5, prior.Segment0Field6, prior.Segment0Field7);
                }
                [DurableUpgrade(typeof(Box<Point>), 1)]
                public static void Closed(in B.V1<P.V1> prior, out B.V2<P.V2> next, UpgradeContext context) {
                    Calls.Add(("closed", context)); next = new(new(prior.Segment0Field1.Segment0Field1 + 1000L), 200, prior.Segment0Field3,
                        prior.Segment0Field4, prior.Segment0Field5, prior.Segment0Field6, prior.Segment0Field7);
                }
                // This method is not generated registration: the rejection fixture changes only this selected capability.
                public static void Wrong(in B.V1<P.V2> prior, out B.V2<P.V2> next, UpgradeContext context) {
                    Calls.Add(("wrong", context)); next = new(prior.Segment0Field1, 999, prior.Segment0Field3,
                        prior.Segment0Field4, prior.Segment0Field5, prior.Segment0Field6, prior.Segment0Field7);
                }
            }
            """ : "";
        string selectProviders = version == 2 ? """
                    if (mode == 0) { Atelia.DurableGraph.Generated.DurableDefinitions.Register(models); return models; }
                    StateDefinitionBinding definition = B.Definition;
                    StateUpgradeProvider[] providers = mode switch {
                        1 => definition.Upgrades.Where(provider => provider.ClosedOwner is not null).ToArray(),
                        2 => definition.Upgrades.Where(provider => provider.ClosedOwner is null).ToArray(),
                        3 => definition.Upgrades.Select(provider => provider.ClosedOwner is null ? provider :
                            new StateUpgradeProvider(definition.DefinitionId, 1, typeof(Upgrades).GetMethod("Wrong")!, provider.ClosedOwner)).ToArray(),
                        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
                    };
                    models.Register(R.Definition); models.Register(P.Definition);
                    models.Register(new StateDefinitionBinding(definition.DefinitionId, definition.Kind, definition.Arity, definition.DomainTypeDefinition,
                        definition.Templates, definition.CurrentValueFactory, definition.HistoricalValueFactory,
                        definition.CurrentModelFactory, definition.HistoricalReaderFactory, providers));
            """ : "Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);";
        string storedCurrent = version == 2 ? """
                    if (storedVersion == 2) {
                        if (number is not null) { var state = number.GetState<B.V2<int>>(); CheckInt(state.Segment0Field1, phase); CheckStamp(state.Segment0Field2, 100, phase); CheckStateCommon(number.Id, state.Segment0Field3, state.Segment0Field4, state.Segment0Field5, state.Segment0Field6, state.Segment0Field7); }
                        if (point is not null) { var state = point.GetState<B.V2<P.V2>>(); CheckPoint(state.Segment0Field1.Segment0Field1, phase); CheckStamp(state.Segment0Field2, 200, phase); CheckStateCommon(point.Id, state.Segment0Field3, state.Segment0Field4, state.Segment0Field5, state.Segment0Field6, state.Segment0Field7); }
                        return;
                    }
            """ : "";
        string callFacts = version == 2 ? """
                    if (Upgrades.Calls.Count != ids.Length) throw new InvalidOperationException("Incorrect owner callback count.");
                    foreach (uint id in ids) {
                        var call = Upgrades.Calls.Single(call => call.Context.ObjectId.Value == id);
                        var owner = call.Context.SourceObjectSchema.Type;
                        bool point = owner.Arguments[0] == TypeExpr.Named("GU.Point");
                        if (call.Side != (point ? "closed" : "open") || call.Context.SourceObjectSchema.Version != 1 || call.Context.TargetObjectSchema.Version != 2 ||
                            owner != call.Context.TargetObjectSchema.Type) throw new InvalidOperationException("Wrong closed provider or exact endpoint.");
                        if (point && (call.Context.SourceObjectSchema.Fields[0].InlineSchema!.Version != 1 || call.Context.TargetObjectSchema.Fields[0].InlineSchema!.Version != 2))
                            throw new InvalidOperationException("Historical/current Point selection was lost.");
                    }
            """ : "";
        return $$"""
            using System;
            using System.Collections.Generic;
            using System.Linq;
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            using Atelia.DurableGraph.Persistence;
            using B = Atelia.DurableGraph.Generated.Family_47552E426F78;
            using P = Atelia.DurableGraph.Generated.Family_47552E506F696E74;
            using R = Atelia.DurableGraph.Generated.Family_47552E526F6F74;
            namespace BootstrapGenericUpgrade;
            [DurableType("GU.Point", {{version}})] public partial struct Point { [DurableField(1)] public {{(version == 1 ? "int" : "long")}} X; }
            [DurableType("GU.Box", {{version}})] public sealed partial class Box<T> : IDurableObject {
                [DurableField(1)] public T Value = default!;
                {{(version == 2 ? "[DurableField(2)] public long Stamp;" : "")}}
                [DurableField(3)] public Box<T> Next = null!;
                [DurableField(4)] public double A;
                [DurableField(5)] public double B;
                [DurableField(6)] public double C;
                [DurableField(7)] public double D;
            }
            [DurableType("GU.Root", 1)] public sealed partial class Root : IDurableObject {
                [DurableField(1)] public Box<int> Numbers = null!;
                [DurableField(2)] public Box<Point> Points = null!;
                [DurableField(3)] public Box<int> NumberAlias = null!;
                [DurableField(4)] public Box<Point> PointAlias = null!;
                [DurableField(5)] public Root Self = null!;
            }
            {{providers}}
            public static class Host {
                public static StateModelRegistry Models(int mode) {
                    StateModelRegistry models = new();
                    {{selectProviders}}
                    return models;
                }
                private static Box<T> CreateBox<T>(T value) {
                    Box<T> box = new() { Value = value, A = 1.25, B = 2.5, C = 3.75, D = 4.125 }; box.Next = box; return box;
                }
                public static IDurableObject Create(string kind) {
                    Box<int> number = CreateBox(17); Box<Point> point = CreateBox(new Point { X = 31 });
                    if (kind == "int") return number;
                    if (kind == "point") return point;
                    Root root = new() { Numbers = number, Points = point, NumberAlias = number, PointAlias = point }; root.Self = root; return root;
                }
                public static object[] Objects(IDurableObject value) { Root root = (Root)value; return new object[] { root, root.Numbers, root.Points }; }
                public static void Change(IDurableObject value) { Root root = (Root)value; root.Numbers.Value = 18; root.Points.Value = new Point { X = {{(version == 1 ? "32" : "90000000001L")}} }; }
                public static void Check(IDurableObject value, int phase) {
                    if (value is Root root) {
                        if (!ReferenceEquals(root, root.Self) || !ReferenceEquals(root.Numbers, root.NumberAlias) || !ReferenceEquals(root.Points, root.PointAlias))
                            throw new InvalidOperationException("Root nominal identity was lost.");
                        Check(root.Numbers, phase); Check(root.Points, phase); return;
                    }
                    if (value is Box<int> number) { CheckInt(number.Value, phase); CheckBox(number); {{(version == 2 ? "CheckStamp(number.Stamp, 100, phase);" : "")}} return; }
                    Box<Point> point = (Box<Point>)value; CheckPoint(point.Value.X, phase); CheckBox(point); {{(version == 2 ? "CheckStamp(point.Stamp, 200, phase);" : "")}}
                }
                private static void CheckInt(int value, int phase) { if (value != (phase == 2 ? 18 : 17)) throw new InvalidOperationException("Wrong generic scalar output."); }
                private static void CheckPoint(long value, int phase) { if (value != (phase == 0 ? 31 : phase == 1 ? 1031 : 90000000001L)) throw new InvalidOperationException("Wrong special inline output."); }
                private static void CheckStamp(long stamp, long expected, int phase) { if (stamp != (phase == 0 ? 0 : expected)) throw new InvalidOperationException("Generic provider selection was lost."); }
                private static void Pads(double a, double b, double c, double d) { if (a != 1.25 || b != 2.5 || c != 3.75 || d != 4.125) throw new InvalidOperationException("Stable DTO fields changed."); }
                private static void CheckBox<T>(Box<T> box) { if (!ReferenceEquals(box, box.Next)) throw new InvalidOperationException("Closed Box ring was lost."); Pads(box.A, box.B, box.C, box.D); }
                private static void CheckStateCommon(ObjectId owner, ObjectId next, double a, double b, double c, double d) { if (owner != next) throw new InvalidOperationException("Stored nominal ring was lost."); Pads(a, b, c, d); }
                public static void CheckStored(ObjectStateRecord[] rows, int storedVersion, int phase) {
                    ObjectStateRecord? number = rows.SingleOrDefault(row => row.Schema!.Type == TypeExpr.Named("GU.Box", TypeExpr.Builtin(TypeTag.Int32)));
                    ObjectStateRecord? point = rows.SingleOrDefault(row => row.Schema!.Type == TypeExpr.Named("GU.Box", TypeExpr.Named("GU.Point")));
                    ObjectStateRecord? root = rows.SingleOrDefault(row => row.Schema!.SchemaId == "GU.Root");
                    if (root is not null) { var state = root.GetState<R.V1>(); if (state.Segment0Field1 != number!.Id || state.Segment0Field2 != point!.Id || state.Segment0Field3 != number.Id || state.Segment0Field4 != point.Id || state.Segment0Field5 != root.Id) throw new InvalidOperationException("Historical Root DTO identity changed."); }
                    if (number is null && point is null || rows.Length != (root is null ? 1 : 3)) throw new InvalidOperationException("Unexpected live rows.");
                    if (typeof(B.V1<int>).Assembly != typeof(Root).Assembly || typeof(P.V1).Assembly != typeof(Root).Assembly) throw new InvalidOperationException("Historical DTOs must come from the independent selected assembly.");
                    {{storedCurrent}}
                    if (storedVersion != 1) throw new InvalidOperationException("Unexpected stored version.");
                    if (number is not null) { var state = number.GetState<B.V1<int>>(); CheckInt(state.Segment0Field1, phase); CheckStateCommon(number.Id, state.Segment0Field3, state.Segment0Field4, state.Segment0Field5, state.Segment0Field6, state.Segment0Field7); }
                    if (point is not null) { var state = point.GetState<B.V1<P.V1>>(); CheckPoint(state.Segment0Field1.Segment0Field1, phase); CheckStateCommon(point.Id, state.Segment0Field3, state.Segment0Field4, state.Segment0Field5, state.Segment0Field6, state.Segment0Field7); }
                }
                public static void ResetCalls() { {{(version == 2 ? "Upgrades.Calls.Clear();" : "")}} }
                public static int CallCount() => {{(version == 2 ? "Upgrades.Calls.Count" : "0")}};
                public static void CheckCalls(uint[] ids) { {{callFacts}} }
            }
            """;
    }
}
