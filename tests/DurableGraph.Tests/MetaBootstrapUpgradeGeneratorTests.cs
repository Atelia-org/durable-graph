using Atelia.Binary;
using Atelia.DurableGraph.Build;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Persistence.MetaBootstrapP0;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    // Both writers are independently compiled real Generator consumers. Build history is
    // accepted before runtime publication; neither old bodies nor historical DTOs are hand-built.
    [Fact]
    public void MetaBootstrapAcceptedHistoryUpgradeSeparatesReadOnlyEventAndStateAcceptance() {
        using AncestryHistoryDirectory history = new();
        SchemaHistoryTool publisher = new();
        GeneratorTestRun v1 = RunGenerator(BootstrapUpgradeSource(1), [], null, "true");
        AssertSchemaOnlyCompiles(v1);
        BootstrapUpgradeHost oldHost = BootstrapUpgradeHost.Load(v1);
        publisher.Publish(history.WriteManifest(v1), history.History);
        Dictionary<string, string> acceptedV1 = history.ReadContents();

        GeneratorTestRun v2 = RunGenerator(BootstrapUpgradeSource(2), history.ReadAdditionalTexts(), null, "true");
        AssertSchemaOnlyCompiles(v2);
        BootstrapUpgradeHost currentHost = BootstrapUpgradeHost.Load(v2);
        publisher.Publish(history.WriteManifest(v2), history.History);
        publisher.Verify(history.WriteManifest(v2), history.History);
        foreach ((string name, string contents) in acceptedV1) {
            Assert.Equal(contents, File.ReadAllText(Path.Combine(history.History, name)));
        }
        GeneratorTestRun withoutUpgrade = RunGenerator(BootstrapUpgradeSource(2, includeUpgrade: false),
            history.ReadAdditionalTexts(), null, "true");
        AssertSchemaOnlyCompiles(withoutUpgrade);
        BootstrapUpgradeHost missingHost = BootstrapUpgradeHost.Load(withoutUpgrade);
        Assert.NotSame(oldHost.ClrType, currentHost.ClrType);
        DurableSchema oldSchema = oldHost.Schema();
        DurableSchema currentSchema = currentHost.Schema();
        SchemaKey oldKey = new(oldSchema.Type, 1), currentKey = new(currentSchema.Type, 2);

        using RawBaseDirectory files = new();
        FrameAddress s0, meta0, e1, e2, s1, s2, meta1;
        uint rootId;
        using (P0Repository repository = P0Repository.CreateNew(files.Path)) {
            StateModelRegistry oldModels = oldHost.Models();
            P0Session session = new(repository, "main", oldModels);
            session.Catalog.Set(oldSchema); // The initial writer knows only actual V1.
            IDurableObject oldRoot = oldHost.Create();
            Assert.Equal(oldHost.ClrType, oldRoot.GetType());
            using P0SessionSave save = session.Prepare(oldRoot);
            P0SaveResult result = save.Commit();
            s0 = result.Business.Address;
            meta0 = result.Meta!.Address;
            rootId = result.Business.RootId.Value;
            Assert.Equal(oldSchema, session.Catalog.Accepted!.Schemas[oldKey]);
            Assert.False(session.Catalog.Accepted.Schemas.ContainsKey(currentKey));
            AssertBootstrapStoredSchema(repository, result.Business, oldSchema, ObjectVersionKind.Base);
        }

        // All data/version resources have closed; the new assembly reads exact S0+meta0.
        using (P0Repository repository = P0Repository.OpenReadOnlyExisting(files.Path)) {
            InvalidDataException missing = Assert.Throws<InvalidDataException>(() =>
                P0BusinessWorkspace.ReadRoot(repository.Store, s0, missingHost.Models()));
            Assert.Contains("upgrade capability is missing", missing.Message);
            Assert.False(repository.IsFaulted);
            IDurableObject upgraded = P0BusinessWorkspace.ReadRoot(repository.Store, s0, currentHost.Models());
            currentHost.Check(upgraded, 107);
            Assert.True(currentHost.UpgradeCalls() > 0);
            Assert.Equal(currentHost.ClrType, upgraded.GetType());
            Assert.Equal(s0, repository.GetHead("main"));
            Assert.Equal(meta0, repository.SharedSchemaRoot);
            Assert.False(P0CatalogView.Load(repository.Store, meta0).Schemas.ContainsKey(currentKey));
            Assert.Equal(0, repository.Store.AppendedFrames);
            P0Session readonlySession = new(repository, "main", currentHost.Models());
            Assert.Throws<InvalidOperationException>(() => readonlySession.Prepare(readonlySession.State!));
            Assert.Equal(0, repository.Store.AppendedFrames);
        }

        using (P0Repository repository = P0Repository.OpenExisting(files.Path)) {
            StateModelRegistry currentModels = currentHost.Models();
            P0Session session = new(repository, "main", currentModels);
            IDurableObject root = session.State!;
            currentHost.Check(root, 107);
            Assert.Equal(s0, session.StateSource!.Address);
            Assert.False(session.Catalog.Accepted!.Schemas.ContainsKey(currentKey));
            Assert.Equal(0, session.Catalog.DirtyCount);

            // Capture registers current V2 automatically. Discard installs neither authority.
            long frames = repository.Store.AppendedFrames;
            using (P0SessionSave discarded = session.Prepare(root, P0GraphKind.Event)) {
                Assert.NotNull(discarded.Catalog.Write);
                Assert.Equal(currentSchema, discarded.Catalog.View.Schemas[currentKey]);
                Assert.Equal(ObjectVersionKind.Base, Assert.Single(discarded.Business.Write.Records).Kind);
            }
            Assert.Equal(frames, repository.Store.AppendedFrames);
            Assert.Equal(s0, repository.GetHead("main"));
            Assert.Equal(meta0, repository.SharedSchemaRoot);
            Assert.False(session.Catalog.Accepted.Schemas.ContainsKey(currentKey));
            Assert.True(session.Catalog.DirtyCount > 0);
            Assert.Same(root, session.State);
            Assert.Equal(s0, session.StateSource.Address);

            using (P0SessionSave eventSave = session.Prepare(root, P0GraphKind.Event)) {
                Assert.Equal(s0, eventSave.Business.Write.SourceGraph);
                Assert.Equal(ObjectVersionKind.Base, Assert.Single(eventSave.Business.Write.Records).Kind);
                P0SaveResult result = eventSave.Commit();
                e1 = result.Business.Address;
                meta1 = result.Meta!.Address;
                Assert.NotEqual(meta0, meta1);
                Assert.Equal(meta1, result.Business.SchemaRoot);
                AssertBootstrapStoredSchema(repository, result.Business, currentSchema, ObjectVersionKind.Base);
            }
            Assert.Equal(currentSchema, session.Catalog.Accepted.Schemas[currentKey]);
            Assert.Equal(0, session.Catalog.DirtyCount);
            Assert.Equal(s0, session.StateSource.Address);
            Assert.Same(root, session.State);

            // Identical E2 still needs V2 Base relative to S0: E1 cannot consume rewrite.
            using (P0SessionSave eventSave = session.Prepare(root, P0GraphKind.Event)) {
                Assert.Null(eventSave.Catalog.Write);
                Assert.Equal(s0, eventSave.Business.Write.SourceGraph);
                Assert.Equal(ObjectVersionKind.Base, Assert.Single(eventSave.Business.Write.Records).Kind);
                P0SaveResult result = eventSave.Commit();
                e2 = result.Business.Address;
                Assert.Equal(e1, result.Business.Parent);
                Assert.Equal(s0, result.Business.SourceGraph);
                AssertBootstrapStoredSchema(repository, result.Business, currentSchema, ObjectVersionKind.Base);
                currentHost.Check(P0BusinessWorkspace.ReadRoot(repository.Store, e2, currentModels), 107);
            }
            Assert.Equal(s0, session.StateSource.Address);
            Assert.Same(root, session.State);

            currentHost.Change(root, 108);
            using (P0SessionSave stateSave = session.Prepare(root)) {
                Assert.Null(stateSave.Catalog.Write);
                Assert.Equal(s0, stateSave.Business.Write.SourceGraph);
                Assert.Equal(ObjectVersionKind.Base, Assert.Single(stateSave.Business.Write.Records).Kind);
                P0SaveResult result = stateSave.Commit();
                s1 = result.Business.Address;
                Assert.Equal(e2, result.Business.Parent);
                AssertBootstrapStoredSchema(repository, result.Business, currentSchema, ObjectVersionKind.Base);
            }
            Assert.Equal(s1, session.StateSource.Address);

            currentHost.Change(root, 109);
            using (P0SessionSave stateSave = session.Prepare(root)) {
                Assert.Null(stateSave.Catalog.Write);
                Assert.Equal(s1, stateSave.Business.Write.SourceGraph);
                ObjectVersionRecord delta = Assert.Single(stateSave.Business.Write.Records);
                Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
                Assert.Equal(session.StateSource.Objects[rootId], delta.PriorAddress);
                P0SaveResult result = stateSave.Commit();
                s2 = result.Business.Address;
                AssertBootstrapStoredSchema(repository, result.Business, currentSchema, ObjectVersionKind.Delta);
            }
            Assert.Equal(s2, session.StateSource.Address);
            Assert.Equal(meta1, repository.SharedSchemaRoot);
            Assert.Equal(meta0, repository.Store.ReadGraph(s0).SchemaRoot);
            Assert.False(P0CatalogView.Load(repository.Store, meta0).Schemas.ContainsKey(currentKey));
            currentHost.Check(P0BusinessWorkspace.ReadRoot(repository.Store, s0, currentModels), 107);
            currentHost.Check(P0BusinessWorkspace.ReadRoot(repository.Store, s2, currentModels), 109);
        }

        using (P0Repository repository = P0Repository.OpenReadOnlyExisting(files.Path)) {
            Assert.Equal(s2, repository.GetStateHead("main"));
            Assert.Equal(meta1, repository.SharedSchemaRoot);
            currentHost.Check(P0BusinessWorkspace.ReadRoot(repository.Store, s2, currentHost.Models()), 109);
            currentHost.Check(P0BusinessWorkspace.ReadRoot(repository.Store, s0, currentHost.Models()), 107);
            AssertBootstrapStoredSchema(repository, repository.Store.ReadGraph(s0), oldSchema, ObjectVersionKind.Base);
            Assert.Equal(0, repository.Store.AppendedFrames);
        }
    }

    private static void AssertBootstrapStoredSchema(P0Repository repository, P0GraphSnapshot graph,
        DurableSchema expected, ObjectVersionKind latestKind) {
        P0ObjectChain chain = repository.Store.ReadObjectChain(graph.Address, graph.RootId.Value);
        Assert.Equal(latestKind, chain.Entries[^1].Record.Kind);
        P0ChainEntry first = chain.Entries[0];
        BareValueReader header = new(first.Record.Body);
        Assert.Equal((byte)1, header.ReadByte());
        Assert.Equal((byte)1, header.ReadByte());
        ObjectId descriptor = new(header.ReadVarUInt32());
        P0CatalogView catalog = P0CatalogView.Load(repository.Store, first.SchemaRoot!.Value);
        Assert.Equal(expected, catalog.ResolveLayout(descriptor).Schema);
        Assert.Equal(catalog.DescriptorId(ObjectLayout.ForDurable(expected)), descriptor);
    }

    private sealed class BootstrapUpgradeHost(Type host) {
        public Type ClrType => host.Assembly.GetType("BootstrapUpgrade.Root")!;
        public Func<StateModelRegistry> Models { get; } = host.GetMethod("Models")!.CreateDelegate<Func<StateModelRegistry>>();
        public DurableSchema Schema() => Models().Snapshot().ResolveCurrentModel(ClrType).CurrentSchema;
        public Func<IDurableObject> Create { get; } = host.GetMethod("Create")!.CreateDelegate<Func<IDurableObject>>();
        public Action<IDurableObject, int> Change { get; } = host.GetMethod("Change")!.CreateDelegate<Action<IDurableObject, int>>();
        public Action<IDurableObject, int> Check { get; } = host.GetMethod("Check")!.CreateDelegate<Action<IDurableObject, int>>();
        public Func<int> UpgradeCalls { get; } = host.GetMethod("UpgradeCalls")!.CreateDelegate<Func<int>>();

        public static BootstrapUpgradeHost Load(GeneratorTestRun run) =>
            new(EmitAndLoad(run.OutputCompilation).GetType("BootstrapUpgrade.Host")!);
    }

    private static string BootstrapUpgradeSource(int version, bool includeUpgrade = true) {
        string upgrade = version == 2 && includeUpgrade ? """
                private static void UpgradeStateV1ToV2(in S.V1 prior, out S.V2 next) {
                    Upgrades++;
                    next = new(prior.Segment0Field1 + 100, (byte)7,
                        prior.Segment0Field3, prior.Segment0Field4, prior.Segment0Field5, prior.Segment0Field6);
                }
            """ : "";
        return $$"""
            using System;
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            using Atelia.DurableGraph.Persistence;
            using S = global::Atelia.DurableGraph.Generated.Family_70312E75706772616465;
            namespace BootstrapUpgrade;
            [DurableType("p1.upgrade", {{version}})]
            public sealed partial class Root : IDurableObject {
                [DurableField(1)] public int Value;
                {{(version == 2 ? "[DurableField(2)] public byte Marker;" : "")}}
                [DurableField(3)] public double A;
                [DurableField(4)] public double B;
                [DurableField(5)] public double C;
                [DurableField(6)] public double D;
                public static int Upgrades;
                {{upgrade}}
            }
            public static class Host {
                public static StateModelRegistry Models() {
                    StateModelRegistry models = new();
                    Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
                    return models;
                }
                public static IDurableObject Create() => new Root {
                    Value = 7, A = 1, B = 2, C = 3, D = 4{{(version == 2 ? ", Marker = 7" : "")}}
                };
                public static void Change(IDurableObject value, int next) => ((Root)value).Value = next;
                public static void Check(IDurableObject value, int expected) {
                    Root root = (Root)value;
                    if (root.Value != expected || root.A != 1 || root.B != 2 || root.C != 3 || root.D != 4
                        {{(version == 2 ? "|| root.Marker != 7" : "")}}) throw new InvalidOperationException("Incorrect restored state.");
                }
                public static int UpgradeCalls() => Root.Upgrades;
            }
            """;
    }
}
