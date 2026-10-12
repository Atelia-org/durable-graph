using System.Reflection;
using Atelia.DurableGraph.Persistence.MetaBootstrapP0;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed partial class WorldWorkspaceTests {
    [Fact]
    public void LegacyAdapterActualTypedBaseEqualityKeepsNoChangeAtThresholdOne() {
        World world = new();
        WorldWorkspace workspace = WorldWorkspace.Create(_store, _schemas, world, Registry(Model()));
        using (PreparedWorldSave first = workspace.Stage(new(1, 100))) { Install(first); }
        ObjectVersionChain chain = _store.ReadObjectVersionChain(workspace.ParentRevisionAddress!.Value, workspace.WorldId.Value);
        ObjectVersionRecord record = Assert.Single(chain.Records).Record;
        Assert.Equal(ObjectVersionKind.Base, record.Kind);
        Assert.Equal((byte)4, record.Body[0]); // This adapter uses the actual legacy representation-ID head, not the P0 head.
        DecodedBaseObjectBody decoded = BaseObjectBodyCodec.Decode(record.Body, _schemas);
        Assert.True(record.Body.Length > decoded.Body.Length);
        long b = ObjectVersionPayloadSize.GetBasePayloadBytes(record.Body.Length);
        Assert.Equal(b, chain.ReconstructionPayloadBytes);
        Assert.Equal(b, StorageOf(workspace, workspace.WorldId).ReconstructionPayloadBytes);
        Assert.True(b > ObjectVersionPayloadSize.GetBasePayloadBytes(decoded.Body.Length));

        // At H == actual typed B there is no optional Base motive; omitting the head would create one.
        using PreparedWorldSave unchanged = StageWithoutReadingChains(workspace, new(1, 100));
        Assert.Empty(unchanged.Revision.LocalObjects);
        Assert.Empty(unchanged.Revision.RemovedObjectIds);
        Assert.Equal(b, StorageOf(workspace, workspace.WorldId).ReconstructionPayloadBytes);
    }
}

public sealed partial class P0BusinessPlanningAccountingTests : IDisposable {
    private const string Prefix = "dg-bootstrap-planning-accounting-";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));

    [Fact]
    public void P0AdapterActualTypedBaseEqualityAndHotColdAccountingUseObjectHeadsAndPreserveEventBaseline() {
        FrameAddress committed;
        uint listId;
        using (P0Repository repository = P0Repository.CreateNew(_path)) {
            StateModelRegistry models = Models();
            P0Session session = new(repository, "main", models);
            DB087Widget root = new() { Numbers = Enumerable.Repeat(0, 123).ToList() };
            using (P0SessionSave save = session.Prepare(root)) { save.Commit(); }
            P0GraphSnapshot source = session.StateSource!;
            P0CatalogView catalog = session.Catalog.Accepted!;
            P0DecodedRows rows = P0TypedReader.Read(repository.Store, source.Address, models.Snapshot(), catalog);
            listId = Assert.Single(rows.Rows.Values, row => row.Kind == ObjectStateKind.List).Id.Value;
            P0ChainEntry first = Assert.Single(repository.Store.ReadObjectChain(source.Address, listId).Entries);
            Assert.Equal(P0TypedBodyCodec.FormatVersion, first.Record.Body[0]);
            Assert.Equal((byte)2, first.Record.Body[1]); // P0 structural TypeExpr head differs from the legacy head.
            var decoded = P0TypedBodyCodec.Decode(first.Record.Body, catalog: null);
            Assert.Equal(124, decoded.Body.Length); // Count plus 123 one-byte Int32 zero values.
            Assert.True(first.Record.Body.Length >= 128);
            int headBytes = first.Record.Body.Length - decoded.Body.Length;
            long b = ObjectVersionPayloadSize.GetBasePayloadBytes(first.Record.Body.Length);
            Assert.Equal(ObjectVersionPayloadSize.GetBasePayloadBytes(decoded.Body.Length) + headBytes + 1, b);
            // The extra byte is the 127/128 body-length prefix boundary, in addition to all head bytes.
            AssertAccounting(repository, session);
            Assert.Equal(b, Accounting(session)[new(listId)]);
            using (P0SessionSave unchanged = session.Prepare(root, parameters: new(1, 100))) {
                Assert.Empty(unchanged.Business.Write.Records); // Every H equals its own actual typed B.
            }

            root.Numbers[7] = 1; // Same encoded scalar width keeps current B exactly equal to the initial B.
            long d;
            using (P0SessionSave save = session.Prepare(root)) {
                ObjectVersionRecord delta = Assert.Single(save.Business.Write.Records);
                Assert.Equal(listId, delta.ObjectId);
                Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
                Assert.Equal(source.Objects[listId], delta.PriorAddress);
                d = ObjectVersionPayloadSize.GetDeltaPayloadBytes(delta.Body.Length, delta.PriorAddress!.Value);
                save.Commit();
            }
            AssertAccounting(repository, session);
            Assert.Equal(b + d, Accounting(session)[new(listId)]);
            source = session.StateSource!;
            long frames = repository.Store.AppendedFrames;
            using (P0SessionSave discarded = session.Prepare(root, parameters: new(1, 100))) {
                Assert.Equal(ObjectVersionKind.Base, Assert.Single(discarded.Business.Write.Records).Kind);
            }
            Assert.Equal(frames, repository.Store.AppendedFrames);
            Assert.Equal(source.Address, session.StateSource!.Address);
            Assert.Equal(b + d, Accounting(session)[new(listId)]);

            FrameAddress eventGraph;
            using (P0SessionSave save = session.Prepare(root, P0GraphKind.Event, new(1, 100))) {
                ObjectVersionRecord optionalBase = Assert.Single(save.Business.Write.Records);
                Assert.Equal(listId, optionalBase.ObjectId);
                Assert.Equal(ObjectVersionKind.Base, optionalBase.Kind);
                Assert.Equal(b, ObjectVersionPayloadSize.GetBasePayloadBytes(optionalBase.Body.Length));
                eventGraph = save.Commit().Business.Address;
            }
            Assert.Equal(source.Address, session.StateSource!.Address);
            Assert.Equal(source.Address, repository.GetStateHead("main"));
            Assert.Equal(eventGraph, repository.GetHead("main"));
            Assert.Equal(b, repository.Store.ReadObjectChain(eventGraph, listId).ReconstructionPayloadBytes);
            Assert.Equal(b + d, Accounting(session)[new(listId)]); // Event Base cannot reset State H.
            AssertAccounting(repository, session);

            using (P0SessionSave save = session.Prepare(root, parameters: new(1, 100))) {
                ObjectVersionRecord optionalBase = Assert.Single(save.Business.Write.Records);
                Assert.Equal(listId, optionalBase.ObjectId);
                Assert.Equal(ObjectVersionKind.Base, optionalBase.Kind);
                committed = save.Commit().Business.Address;
            }
            AssertAccounting(repository, session);
            Assert.Equal(b, Accounting(session)[new(listId)]); // Only confirmed State resets reconstruction accounting.
            using (P0SessionSave unchanged = session.Prepare(root, parameters: new(1, 100))) {
                Assert.Empty(unchanged.Business.Write.Records);
            }
        }

        using (P0Repository repository = P0Repository.OpenExisting(_path)) {
            P0Session session = new(repository, "main", Models());
            Assert.Equal(committed, session.StateSource!.Address);
            AssertAccounting(repository, session); // Cold H comes from the actual chain, including this backend's head.
            DB087Widget root = Assert.IsType<DB087Widget>(session.State);
            Assert.Equal(1, root.Numbers[7]);
            long priorH = Accounting(session)[new(listId)];
            FrameAddress priorHead = session.StateSource.Objects[listId];
            root.Numbers[8] = 1;
            using (P0SessionSave save = session.Prepare(root)) {
                ObjectVersionRecord delta = Assert.Single(save.Business.Write.Records);
                Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
                Assert.Equal(priorHead, delta.PriorAddress);
                long d = ObjectVersionPayloadSize.GetDeltaPayloadBytes(delta.Body.Length, priorHead);
                save.Commit();
                Assert.Equal(priorH + d, Accounting(session)[new(listId)]);
            }
            AssertAccounting(repository, session);
        }
    }

    private static IReadOnlyDictionary<ObjectId, long> Accounting(P0Session session) {
        return Baseline(session).Objects.ToDictionary(static pair => pair.Key,
            static pair => pair.Value.Storage!.Value.ReconstructionPayloadBytes);
    }

    private static BusinessWorkspace Business(P0Session session) {
        P0BusinessWorkspace adapter = Assert.IsType<P0BusinessWorkspace>(typeof(P0Session)
            .GetField("_business", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session));
        return Assert.IsType<BusinessWorkspace>(typeof(P0BusinessWorkspace)
            .GetField("_workspace", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(adapter));
    }

    private static NormalizedRevision Baseline(P0Session session) => Assert.IsType<NormalizedRevision>(typeof(BusinessWorkspace)
        .GetField("_baseline", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Business(session)));

    private static void AssertAccounting(P0Repository repository, P0Session session) {
        P0GraphSnapshot source = session.StateSource!;
        IReadOnlyDictionary<ObjectId, long> h = Accounting(session);
        Assert.Equal(source.Objects.Keys.Order(), h.Keys.Select(id => id.Value).Order());
        foreach ((uint id, FrameAddress head) in source.Objects) {
            P0ObjectChain chain = repository.Store.ReadObjectChain(source.Address, id);
            Assert.Equal(head, chain.Head);
            Assert.Equal(head, chain.Entries[^1].ContainingBatch);
            Assert.NotEqual(source.Address, head); // P0 graph frames are never object batch heads.
            Assert.Equal(chain.ReconstructionPayloadBytes, h[new(id)]);
        }
    }

    private static StateModelRegistry Models() {
        StateModelRegistry models = new();
        models.Register(global::Atelia.DurableGraph.Generated.Family_5030576964676574.Definition);
        return models;
    }

    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}
