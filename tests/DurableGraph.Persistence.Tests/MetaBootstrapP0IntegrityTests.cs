using System.Buffers;
using Atelia.Data;

using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;
using Frames = Atelia.FrameStore.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

// These are storage-relation witnesses, not typed meta round-trip witnesses. Raw
// appends retain valid FrameStore framing/CRC while deliberately breaking DG edges.
public sealed class MetaBootstrapP0IntegrityTests : IDisposable {
    private const string RootPrefix = "DurableGraph-P0-Integrity-";
    private readonly string _temporaryRoot = Path.GetFullPath(Path.GetTempPath());
    private readonly List<string> _roots = [];

    [Theory]
    [InlineData("stale")]
    [InlineData("otherBranch")]
    [InlineData("missing")]
    public void Cold_read_rejects_owned_batch_with_wrong_content_source(string error) {
        using Fixture fixture = CreateFixture();
        StoredGraphRevision initial = fixture.State(null, [B(1, [10])]);
        StoredGraphRevision updated = fixture.State(initial.Address, [B(1, [20])]);
        StoredGraphRevision sibling = fixture.State(initial.Address, [B(1, [30])]);
        FrameAddress? wrongSource = error switch {
            "stale" => initial.Address,
            "otherBranch" => sibling.Address,
            "missing" => null,
            _ => throw new ArgumentOutOfRangeException(nameof(error)),
        };
        FrameAddress batch = fixture.Batch(wrongSource, [D(1, updated.ObjectHeads[1], [40])]);
        FrameAddress directory = fixture.BaseDirectory(updated.Address, Map((1u, batch)));
        FrameAddress bad = fixture.Graph(StoredGraphKind.State, updated.Address, 1, directory, batch);

        fixture.Cold(store => {
            Assert.Equal(updated.ObjectHeads[1], fixture.Read(store, updated.Address).ObjectHeads[1]);
            Assert.Throws<InvalidDataException>(() => fixture.Read(store, bad));
        });
    }

    [Theory]
    [InlineData("omittedFromBase")]
    [InlineData("removedByDelta")]
    [InlineData("externalHead")]
    public void Cold_read_rejects_owned_local_record_not_selected_by_directory(string error) {
        using Fixture fixture = CreateFixture();
        StoredGraphRevision initial = fixture.State(null, [B(1, [10]), B(2, [20])]);
        FrameAddress batch = fixture.Batch(initial.Address, [B(1, [11]), B(2, [21])]);
        var heads = Map((1u, batch));
        if (error == "externalHead") { heads.Add(2, initial.ObjectHeads[2]); }
        FrameAddress directory = error == "removedByDelta"
            ? fixture.Directory(initial.Address, ObjectVersionKind.Delta, initial.DirectoryRoot,
                ObjectHeadDirectoryCodec.PrepareDelta(initial.ObjectHeads, heads).Body)
            : fixture.BaseDirectory(initial.Address, heads);
        FrameAddress bad = fixture.Graph(StoredGraphKind.State, initial.Address, 1, directory, batch);

        fixture.Cold(store => {
            Assert.Equal(heads.OrderBy(static pair => pair.Key).ToArray(),
                store.ReadDirectory(directory).OrderBy(static pair => pair.Key).ToArray());
            Assert.Throws<InvalidDataException>(() => fixture.Read(store, bad));
        });
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("otherBranch")]
    [InlineData("graphAddress")]
    public void Retained_bad_Delta_uses_its_own_batch_source_and_fails_cold(string error) {
        using Fixture fixture = CreateFixture();
        StoredGraphRevision initial = fixture.State(null, [B(1, [10])]);
        StoredGraphRevision updated = fixture.State(initial.Address, [D(1, initial.ObjectHeads[1], [20])]);
        StoredGraphRevision sibling = fixture.State(initial.Address, [B(1, [30])]);
        FrameAddress wrongPrior = error switch {
            "stale" => initial.ObjectHeads[1],
            "otherBranch" => sibling.ObjectHeads[1],
            "graphAddress" => updated.Address,
            _ => throw new ArgumentOutOfRangeException(nameof(error)),
        };
        FrameAddress batch = fixture.Batch(updated.Address, [D(1, wrongPrior, [40])]);
        FrameAddress directory = fixture.BaseDirectory(updated.Address, Map((1u, batch)));
        // An external head remains a shallow declaration. The content-chain read
        // must still reject its stale edge, even after another graph retains it.
        FrameAddress external = fixture.Graph(StoredGraphKind.State, updated.Address, 1, directory, null);
        FrameAddress retained = fixture.Graph(StoredGraphKind.State, external, 1, directory, null);

        fixture.Cold(store => {
            Assert.Equal(batch, fixture.Read(store, external).ObjectHeads[1]);
            Assert.Equal(batch, fixture.Read(store, retained).ObjectHeads[1]);
            Assert.Throws<InvalidDataException>(() => store.ReadObjectChain(fixture.Read(store, retained), 1));
            for (int attempt = 0; attempt < 2; attempt++) {
                Assert.Throws<InvalidDataException>(() => store.ReadObjectChains(fixture.Read(store, retained)).ToArray());
            }
        });
    }

    [Fact]
    public void Bulk_chain_rejects_Event_as_retained_Delta_content_source_cold_and_warm() {
        using Fixture fixture = CreateFixture();
        StoredGraphRevision initial = fixture.State(null, [B(1, [10])]);
        StoredGraphRevision domainEvent = fixture.Append(StoredGraphKind.Event, null, 1, [B(1, [10])]);
        FrameAddress batch = fixture.Batch(domainEvent.Address, [D(1, initial.ObjectHeads[1], [20])]);
        FrameAddress directory = fixture.BaseDirectory(initial.Address, Map((1u, batch)));
        FrameAddress retained = fixture.Graph(StoredGraphKind.State, initial.Address, 1, directory, null);

        fixture.Cold(store => {
            for (int attempt = 0; attempt < 2; attempt++) {
                StoredGraphRevision snapshot = fixture.Read(store, retained);
                IEnumerable<ObjectVersionChain> chains = store.ReadObjectChains(snapshot);
                Assert.Equal(batch, snapshot.ObjectHeads[1]); // The external head itself is a valid shallow declaration.
                InvalidDataException error = Assert.Throws<InvalidDataException>(() => chains.ToArray());
                Assert.Contains("Content source", error.Message);
            }
            Assert.Equal(0, store.AppendedFrames);
        });
    }

    [Fact]
    public void Retained_valid_Delta_keeps_original_source_across_events_and_unchanged_State() {
        using Fixture fixture = CreateFixture();
        StoredGraphRevision initial = fixture.State(null, [B(1, [10])]);
        StoredGraphRevision updated = fixture.State(initial.Address, [D(1, initial.ObjectHeads[1], [20])]);
        StoredGraphRevision firstEvent = fixture.Append(StoredGraphKind.Event, null, 1, [B(1, [20])]);
        StoredGraphRevision secondEvent = fixture.Append(StoredGraphKind.Event, null, 1, [B(1, [20])]);
        StoredGraphRevision unchanged = fixture.State(updated.Address, []);

        fixture.Cold(store => {
            StoredGraphRevision restored = fixture.Read(store, unchanged.Address);
            Assert.Null(restored.PreviousMeta);
            Assert.Null(fixture.Read(store, firstEvent.Address).SourceGraph);
            Assert.Null(fixture.Read(store, secondEvent.Address).SourceGraph);
            Assert.Equal(updated.Address, restored.SourceGraph);
            Assert.Equal(updated.DirectoryRoot, restored.DirectoryRoot);
            Assert.Null(restored.OwnedBatch);
            foreach (ObjectVersionChain chain in new[] {
                store.ReadObjectChain(fixture.Read(store, unchanged.Address), 1), Assert.Single(store.ReadObjectChains(fixture.Read(store, unchanged.Address)))
            }) {
                Assert.Equal(new[] { initial.OwnedBatch!.Value, updated.OwnedBatch!.Value },
                    chain.Entries.Select(static entry => entry.ContainingBatch).ToArray());
                Assert.Null(chain.Entries[0].SourceGraph);
                Assert.Equal(initial.Address, chain.Entries[1].SourceGraph);
                Assert.Equal(new byte[] { 10 }, chain.Entries[0].Record.Body.ToArray());
                Assert.Equal(new byte[] { 20 }, chain.Entries[1].Record.Body.ToArray());
                Assert.Equal(ObjectVersionPayloadSize.GetBasePayloadBytes(1) +
                    ObjectVersionPayloadSize.GetDeltaPayloadBytes(1, initial.ObjectHeads[1]),
                    chain.ReconstructionPayloadBytes);
            }
        });
    }

    [Theory]
    [InlineData("baseCreator")]
    [InlineData("deltaCreator")]
    [InlineData("deltaPrior")]
    public void Cold_read_rejects_directory_created_from_wrong_graph_or_prior(string error) {
        using Fixture fixture = CreateFixture();
        StoredGraphRevision initial = fixture.State(null, [B(1, [10])]);
        StoredGraphRevision updated = fixture.State(initial.Address, [B(1, [20])]);
        FrameAddress directory = error switch {
            "baseCreator" => fixture.BaseDirectory(initial.Address, updated.ObjectHeads),
            "deltaCreator" => fixture.Directory(initial.Address, ObjectVersionKind.Delta,
                updated.DirectoryRoot, ObjectHeadDirectoryCodec.PrepareDelta(updated.ObjectHeads, updated.ObjectHeads).Body),
            "deltaPrior" => fixture.Directory(updated.Address, ObjectVersionKind.Delta,
                initial.DirectoryRoot, ObjectHeadDirectoryCodec.PrepareDelta(updated.ObjectHeads, updated.ObjectHeads).Body),
            _ => throw new ArgumentOutOfRangeException(nameof(error)),
        };
        FrameAddress bad = fixture.Graph(StoredGraphKind.State, updated.Address, 1, directory, null);

        fixture.Cold(store => Assert.Throws<InvalidDataException>(() => fixture.Read(store, bad)));
    }

    [Fact]
    public void Reused_directory_preserves_creator_and_allows_new_root_but_not_new_owned_batch() {
        using Fixture fixture = CreateFixture();
        StoredGraphRevision initial = fixture.State(null, [B(1, [10]), B(2, [20])]);
        FrameAddress reused = fixture.Graph(StoredGraphKind.State, initial.Address, 2, initial.DirectoryRoot, null);
        FrameAddress batch = fixture.Batch(initial.Address, [B(1, [30])]);
        FrameAddress bad = fixture.Graph(StoredGraphKind.State, initial.Address, 1, initial.DirectoryRoot, batch);

        fixture.Cold(store => {
            StoredGraphRevision restored = fixture.Read(store, reused);
            Assert.Equal(new ObjectId(2), restored.RootId);
            Assert.Equal(initial.Address, restored.SourceGraph);
            Assert.Equal(initial.DirectoryRoot, restored.DirectoryRoot);
            Assert.Null(restored.OwnedBatch);
            Assert.Null(store.ReadDirectoryVersion(restored.DirectoryRoot).SourceGraph);
            Assert.Equal(new byte[] { 20 }, Assert.Single(store.ReadObjectChain(fixture.Read(store, reused), 2).Entries).Record.Body.ToArray());
            Assert.Throws<InvalidDataException>(() => fixture.Read(store, bad));
        });
    }

    [Theory]
    [InlineData("missingId")]
    [InlineData("graphRole")]
    [InlineData("directoryRole")]
    public void Completed_locator_still_requires_batch_role_and_same_local_id(string error) {
        using Fixture fixture = CreateFixture();
        StoredGraphRevision initial = fixture.State(null, [B(1, [10])]);
        FrameAddress wrongHead = error switch {
            "missingId" => fixture.Batch(initial.Address, [B(2, [20])]),
            "graphRole" => initial.Address,
            "directoryRole" => initial.DirectoryRoot,
            _ => throw new ArgumentOutOfRangeException(nameof(error)),
        };
        FrameAddress directory = fixture.BaseDirectory(initial.Address, Map((1u, wrongHead)));
        FrameAddress bad = fixture.Graph(StoredGraphKind.State, initial.Address, 1, directory, null);

        fixture.Cold(store => {
            Assert.Equal(wrongHead, store.ReadDirectory(directory)[1]);
            Assert.Throws<InvalidDataException>(() => fixture.Read(store, bad));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void New_object_and_directory_Base_stop_bad_old_content_without_losing_source_metadata(bool badDirectory) {
        using Fixture fixture = CreateFixture();
        StoredGraphRevision initial = fixture.State(null, [B(1, [10])]);
        StoredGraphRevision updated = fixture.State(initial.Address, [D(1, initial.ObjectHeads[1], [20])]);
        FrameAddress badBatch = fixture.Batch(updated.Address, [badDirectory
            ? B(1, [30]) : D(1, initial.ObjectHeads[1], [30])]);
        var badHeads = Map((1u, badBatch));
        FrameAddress badRoot = badDirectory
            ? fixture.Directory(updated.Address, ObjectVersionKind.Delta, initial.DirectoryRoot,
                ObjectHeadDirectoryCodec.PrepareDelta(updated.ObjectHeads, badHeads).Body)
            : fixture.BaseDirectory(updated.Address, badHeads);
        FrameAddress bad = fixture.Graph(StoredGraphKind.State, updated.Address, 1, badRoot, badBatch);

        FrameAddress resetBatch = fixture.Batch(bad, [B(1, [])]);
        FrameAddress resetDirectory = fixture.BaseDirectory(bad, Map((1u, resetBatch)));
        FrameAddress reset = fixture.Graph(StoredGraphKind.State, bad, 1, resetDirectory, resetBatch);
        FrameAddress extendingBatch = fixture.Batch(bad, [D(1, badBatch, [40])]);
        FrameAddress extendingDirectory = fixture.BaseDirectory(bad, Map((1u, extendingBatch)));
        FrameAddress extending = fixture.Graph(StoredGraphKind.State, bad, 1, extendingDirectory, extendingBatch);

        fixture.Cold(store => {
            Assert.Throws<InvalidDataException>(() => fixture.Read(store, bad));
            StoredGraphRevision restored = fixture.Read(store, reset);
            Assert.Null(restored.PreviousMeta);
            Assert.Equal(bad, restored.SourceGraph);
            Assert.Equal(ObjectVersionKind.Base, store.ReadDirectoryVersion(resetDirectory).Kind);
            foreach (ObjectVersionChain chain in new[] {
                store.ReadObjectChain(fixture.Read(store, reset), 1), Assert.Single(store.ReadObjectChains(fixture.Read(store, reset)))
            }) {
                ObjectVersionChainEntry only = Assert.Single(chain.Entries);
                Assert.Equal(resetBatch, only.ContainingBatch);
                Assert.Equal(bad, only.SourceGraph);
                Assert.Empty(only.Record.Body.ToArray());
                Assert.Equal(ObjectVersionPayloadSize.GetBasePayloadBytes(0), chain.ReconstructionPayloadBytes);
            }
            // The same older dependency must remain invalid when actually needed.
            Assert.Throws<InvalidDataException>(() => store.ReadObjectChain(fixture.Read(store, extending), 1));
            Assert.Throws<InvalidDataException>(() => store.ReadObjectChains(fixture.Read(store, extending)).ToArray());
        });
    }

    [Fact]
    public void Removed_id_cannot_rejoin_old_head_via_Delta_but_new_Base_can_start_new_chain() {
        using Fixture fixture = CreateFixture();
        StoredGraphRevision initial = fixture.State(null, [B(1, [10]), B(2, [20])]);
        StoredGraphRevision removed = fixture.Append(StoredGraphKind.State, initial.Address, 2, [], [1]);
        FrameAddress badBatch = fixture.Batch(removed.Address, [D(1, initial.ObjectHeads[1], [30])]);
        FrameAddress badDirectory = fixture.BaseDirectory(removed.Address,
            Map((1u, badBatch), (2u, removed.ObjectHeads[2])));
        FrameAddress bad = fixture.Graph(StoredGraphKind.State, removed.Address, 1, badDirectory, badBatch);
        StoredGraphRevision fresh = fixture.State(removed.Address, [B(1, [90])]);
        StoredGraphRevision latest = fixture.State(fresh.Address, [D(1, fresh.ObjectHeads[1], [91])]);

        fixture.Cold(store => {
            Assert.False(fixture.Read(store, removed.Address).ObjectHeads.ContainsKey(1));
            Assert.Throws<InvalidDataException>(() => store.ReadObjectChain(fixture.Read(store, bad), 1));
            ObjectVersionChain chain = store.ReadObjectChain(fixture.Read(store, latest.Address), 1);
            Assert.Equal(new[] { fresh.OwnedBatch!.Value, latest.OwnedBatch!.Value },
                chain.Entries.Select(static entry => entry.ContainingBatch).ToArray());
            Assert.Equal(new byte[] { 90 }, chain.Entries[0].Record.Body.ToArray());
            Assert.Equal(new byte[] { 10 }, Assert.Single(store.ReadObjectChain(fixture.Read(store, initial.Address), 1).Entries).Record.Body.ToArray());
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Complete_two_frame_object_or_directory_prior_cycles_fail_cold_and_warm(bool objectPrior) {
        using Fixture fixture = CreateFixture();
        StoredGraphRevision positive = fixture.State(null, [B(1, [10])]);
        var cycle = fixture.AppendCycle(objectPrior);
        fixture.Cold(store => {
            if (objectPrior) {
                Assert.Equal(cycle.First, fixture.Read(store, cycle.Selection).ObjectHeads[1]);
                Assert.Equal(cycle.Second, Assert.Single(store.ReadBatch(cycle.First).Records).PriorAddress);
                Assert.Equal(cycle.First, Assert.Single(store.ReadBatch(cycle.Second).Records).PriorAddress);
            } else {
                Assert.Equal(cycle.Second, store.ReadDirectoryVersion(cycle.First).Prior);
                Assert.Equal(cycle.First, store.ReadDirectoryVersion(cycle.Second).Prior);
            }
            for (int attempt = 0; attempt < 2; attempt++) {
                InvalidDataException error = objectPrior
                    ? Assert.Throws<InvalidDataException>(() => store.ReadObjectChain(fixture.Read(store, cycle.Selection), 1))
                    : Assert.Throws<InvalidDataException>(() => store.ReadDirectory(cycle.First));
                Assert.Contains("cycle", error.Message, StringComparison.OrdinalIgnoreCase);
                if (objectPrior) {
                    InvalidDataException bulk = Assert.Throws<InvalidDataException>(() => store.ReadObjectChains(fixture.Read(store, cycle.Selection)).ToArray());
                    Assert.Contains("cycle", bulk.Message, StringComparison.OrdinalIgnoreCase);
                }
            }
            Assert.Equal(new byte[] { 10 }, Assert.Single(store.ReadObjectChain(fixture.Read(store, positive.Address), 1).Entries).Record.Body.ToArray());
            Assert.Equal(0, store.AppendedFrames);
        });
    }

    [Fact]
    public void Cold_and_warm_chain_H_keeps_actual_redundant_body_length_bytes() {
        using Fixture fixture = CreateFixture();
        ArrayBufferWriter<byte> payload = new();
        BareValueWriter writer = new(payload);
        writer.WriteRawBytes("P0B1"u8);
        writer.WriteBoolean(false);
        writer.WriteBoolean(true);
        WriteAddress(writer, fixture.SchemaRoot);
        writer.WriteVarUInt32(1);
        writer.WriteVarUInt32(1);
        writer.WriteByte((byte)ObjectVersionKind.Base);
        writer.WriteRawBytes([0x81, 0x00, 0x10]); // Legal redundant length=1, followed by one body byte.
        FrameAddress batch = fixture.RawBatch(payload.WrittenSpan);
        FrameAddress directory = fixture.BaseDirectory(null, Map((1u, batch)));
        FrameAddress graph = fixture.Graph(StoredGraphKind.State, null, 1, directory, batch);
        fixture.Cold(store => {
            for (int attempt = 0; attempt < 2; attempt++) {
                foreach (ObjectVersionChain chain in new[] {
                    store.ReadObjectChain(fixture.Read(store, graph), 1), Assert.Single(store.ReadObjectChains(fixture.Read(store, graph)))
                }) {
                    ObjectVersionChainEntry entry = Assert.Single(chain.Entries);
                    Assert.Equal(batch, chain.Head);
                    Assert.Equal(new byte[] { 0x10 }, entry.Record.Body.ToArray());
                    Assert.Equal(4, entry.Record.EncodedPayloadBytes); // kind + two length bytes + body.
                    Assert.Equal(4, entry.PayloadBytes);
                    Assert.Equal(4, chain.ReconstructionPayloadBytes);
                    Assert.Equal(3, ObjectVersionPayloadSize.GetBasePayloadBytes(1)); // Canonical re-encoding is shorter.
                }
            }
            Assert.True(store.ReadCacheStatistics.BatchHits > 0);
            Assert.Equal(0, store.AppendedFrames);
        });
    }

    [Theory]
    [InlineData(1_000_001u, false)]
    [InlineData(1_000_000u, false)]
    [InlineData(2u, true)]
    public void Completed_batch_count_cap_and_remaining_guard_reject_before_admission(uint count, bool oneMinimumEntry) {
        using Fixture fixture = CreateFixture();
        ArrayBufferWriter<byte> payload = new();
        BareValueWriter writer = new(payload);
        writer.WriteRawBytes("P0B1"u8);
        writer.WriteBoolean(false);
        writer.WriteBoolean(false);
        writer.WriteVarUInt32(count);
        if (oneMinimumEntry) { writer.WriteRawBytes([1, (byte)ObjectVersionKind.Base, 0]); }
        FrameAddress bad = fixture.RawBatch(payload.WrittenSpan);
        FrameAddress good = fixture.Batch(null, [B(1, [])]);
        fixture.Cold(store => {
            for (int attempt = 1; attempt <= 2; attempt++) {
                InvalidDataException error = Assert.Throws<InvalidDataException>(() => store.ReadBatch(bad));
                Assert.Contains("count", error.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(attempt, store.ReadCacheStatistics.BatchMisses);
                Assert.Equal(0, store.ReadCacheStatistics.BatchDecodes);
                Assert.Equal(0, store.ReadCacheStatistics.BatchHits);
                Assert.Equal(0, store.ReadCacheStatistics.EntryCount);
                Assert.Equal(0, store.ReadCacheStatistics.ResidentChargeBytes);
            }
            ObjectVersionBatch healthy = store.ReadBatch(good);
            Assert.Empty(Assert.Single(healthy.Records).Body.ToArray());
            Assert.Same(healthy, store.ReadBatch(good));
            Assert.Equal(0, store.AppendedFrames);
        });
    }

    private Fixture CreateFixture() {
        string root = Path.Combine(_temporaryRoot, RootPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _roots.Add(root);
        return new(Path.Combine(root, "data"));
    }

    private static Dictionary<uint, FrameAddress> Map(params (uint Id, FrameAddress Head)[] pairs) =>
        pairs.ToDictionary(static pair => pair.Id, static pair => pair.Head);

    private static ObjectVersionRecord B(uint id, byte[] body) => ObjectVersionRecord.CreateBase(id, body);
    private static ObjectVersionRecord D(uint id, FrameAddress prior, byte[] body) => ObjectVersionRecord.CreateDelta(id, prior, body);

    public void Dispose() {
        string prefix = Path.TrimEndingDirectorySeparator(_temporaryRoot) + Path.DirectorySeparatorChar;
        foreach (string root in _roots) {
            string target = Path.GetFullPath(root);
            if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(target).StartsWith(RootPrefix, StringComparison.Ordinal)) {
                throw new InvalidOperationException("Test cleanup must remain under its temporary root.");
            }
            StorageTestData.DeleteTestPath(target);
        }
    }

    private sealed class Fixture : IDisposable {
        private readonly string _path;
        private readonly Frames _frames;
        private bool _closed;
        // Explicit fixture inputs survive cold reopen; no payload infers the business source.
        private readonly Dictionary<FrameAddress, FrameAddress?> _expectedSources = [];

        internal Fixture(string path) {
            _path = path;
            _frames = Frames.Create(path);
            Store = new(_frames);
            // Only the graph role is relevant here. Typed catalog projection has
            // separate generated-model tests; this fixture uses opaque raw bodies.
            FrameAddress batch = _frames.Append(GraphFrameCodec.BatchTag,
                GraphFrameCodec.EncodeBatch(null, null, [B(1, [0])])).Unwrap();
            FrameAddress directory = BaseDirectory(null, Map((1u, batch)));
            SchemaRoot = _frames.Append(GraphFrameCodec.GraphTag,
                GraphFrameCodec.EncodeGraph(StoredGraphKind.Meta, null, new(1), directory, null, batch)).Unwrap();
        }

        internal GraphRecordStore Store { get; }
        internal FrameAddress SchemaRoot { get; }

        internal StoredGraphRevision State(FrameAddress? expectedSource, ObjectVersionRecord[] records) =>
            Append(StoredGraphKind.State, expectedSource, 1, records);

        internal StoredGraphRevision Append(StoredGraphKind kind, FrameAddress? expectedSource, uint rootId,
            ObjectVersionRecord[] records, uint[]? removed = null) {
            StoredGraphRevision result = Store.AppendBusiness(kind, SchemaRoot,
                new(expectedSource, new(rootId), records, removed));
            _expectedSources.Add(result.Address, expectedSource);
            return result;
        }

        internal StoredGraphRevision Read(GraphRecordStore store, FrameAddress address) =>
            store.ReadBusinessGraph(address, _expectedSources[address]);

        internal FrameAddress Batch(FrameAddress? source, ObjectVersionRecord[] records) =>
            _frames.Append(GraphFrameCodec.BatchTag, GraphFrameCodec.EncodeBatch(source, SchemaRoot, records)).Unwrap();

        internal FrameAddress RawBatch(ReadOnlySpan<byte> payload) => _frames.Append(GraphFrameCodec.BatchTag, payload).Unwrap();

        internal (FrameAddress First, FrameAddress Second, FrameAddress Selection) AppendCycle(bool objectPrior) {
            FrameAddress witness = TestAddress();
            byte[] graphTemplate = GraphFrameCodec.EncodeGraph(StoredGraphKind.State, null, new(1), witness, SchemaRoot, null);
            using FrameBuilder firstGraphBuilder = _frames.BeginAppend(graphTemplate.Length, 0, out FrameAddress firstGraph);
            using FrameBuilder secondGraphBuilder = _frames.BeginAppend(graphTemplate.Length, 0, out FrameAddress secondGraph);
            _expectedSources.Add(firstGraph, null);
            _expectedSources.Add(secondGraph, null);
            if (objectPrior) {
                int length = GraphFrameCodec.EncodeBatch(witness, SchemaRoot, [D(1, witness, [])]).Length;
                using FrameBuilder firstBuilder = _frames.BeginAppend(length, 0, out FrameAddress first);
                using FrameBuilder secondBuilder = _frames.BeginAppend(length, 0, out FrameAddress second);
                // No DG Store operation runs under these backing writer leases.
                // Each completed source graph selects the next exact batch head.
                FrameAddress firstDirectory = BaseDirectory(null, Map((1u, second)));
                FrameAddress secondDirectory = BaseDirectory(null, Map((1u, first)));
                BareValueWriter firstGraphWriter = new(firstGraphBuilder.PayloadAndMeta);
                firstGraphWriter.WriteRawBytes(GraphFrameCodec.EncodeGraph(StoredGraphKind.State, null, new(1), firstDirectory, SchemaRoot, null));
                BareValueWriter secondGraphWriter = new(secondGraphBuilder.PayloadAndMeta);
                secondGraphWriter.WriteRawBytes(GraphFrameCodec.EncodeGraph(StoredGraphKind.State, null, new(1), secondDirectory, SchemaRoot, null));
                BareValueWriter firstWriter = new(firstBuilder.PayloadAndMeta);
                firstWriter.WriteRawBytes(GraphFrameCodec.EncodeBatch(firstGraph, SchemaRoot, [D(1, second, [])]));
                BareValueWriter secondWriter = new(secondBuilder.PayloadAndMeta);
                secondWriter.WriteRawBytes(GraphFrameCodec.EncodeBatch(secondGraph, SchemaRoot, [D(1, first, [])]));
                Assert.Equal(firstGraph, firstGraphBuilder.EndAppend(GraphFrameCodec.GraphTag).Unwrap());
                Assert.Equal(secondGraph, secondGraphBuilder.EndAppend(GraphFrameCodec.GraphTag).Unwrap());
                Assert.Equal(first, firstBuilder.EndAppend(GraphFrameCodec.BatchTag).Unwrap());
                Assert.Equal(second, secondBuilder.EndAppend(GraphFrameCodec.BatchTag).Unwrap());
                return (first, second, secondGraph);
            } else {
                byte[] delta = ObjectHeadDirectoryCodec.PrepareDelta(Map((1u, witness)), Map((1u, witness))).Body.ToArray();
                int length = GraphFrameCodec.EncodeDirectory(witness, ObjectVersionKind.Delta, witness, delta).Length;
                using FrameBuilder firstBuilder = _frames.BeginAppend(length, 0, out FrameAddress first);
                using FrameBuilder secondBuilder = _frames.BeginAppend(length, 0, out FrameAddress second);
                BareValueWriter firstGraphWriter = new(firstGraphBuilder.PayloadAndMeta);
                firstGraphWriter.WriteRawBytes(GraphFrameCodec.EncodeGraph(StoredGraphKind.State, null, new(1), second, SchemaRoot, null));
                BareValueWriter secondGraphWriter = new(secondGraphBuilder.PayloadAndMeta);
                secondGraphWriter.WriteRawBytes(GraphFrameCodec.EncodeGraph(StoredGraphKind.State, null, new(1), first, SchemaRoot, null));
                BareValueWriter firstWriter = new(firstBuilder.PayloadAndMeta);
                firstWriter.WriteRawBytes(GraphFrameCodec.EncodeDirectory(firstGraph, ObjectVersionKind.Delta, second, delta));
                BareValueWriter secondWriter = new(secondBuilder.PayloadAndMeta);
                secondWriter.WriteRawBytes(GraphFrameCodec.EncodeDirectory(secondGraph, ObjectVersionKind.Delta, first, delta));
                Assert.Equal(firstGraph, firstGraphBuilder.EndAppend(GraphFrameCodec.GraphTag).Unwrap());
                Assert.Equal(secondGraph, secondGraphBuilder.EndAppend(GraphFrameCodec.GraphTag).Unwrap());
                Assert.Equal(first, firstBuilder.EndAppend(GraphFrameCodec.DirectoryTag).Unwrap());
                Assert.Equal(second, secondBuilder.EndAppend(GraphFrameCodec.DirectoryTag).Unwrap());
                return (first, second, secondGraph);
            }
        }

        internal FrameAddress BaseDirectory(FrameAddress? source, IReadOnlyDictionary<uint, FrameAddress> heads) =>
            Directory(source, ObjectVersionKind.Base, null, ObjectHeadDirectoryCodec.PrepareBase(heads).Body);

        internal FrameAddress Directory(FrameAddress? source, ObjectVersionKind kind,
            FrameAddress? prior, ReadOnlySpan<byte> body) =>
            _frames.Append(GraphFrameCodec.DirectoryTag, GraphFrameCodec.EncodeDirectory(source, kind, prior, body)).Unwrap();

        internal FrameAddress Graph(StoredGraphKind kind, FrameAddress? expectedSource, uint rootId,
            FrameAddress directory, FrameAddress? ownedBatch) {
            FrameAddress address = _frames.Append(GraphFrameCodec.GraphTag,
                GraphFrameCodec.EncodeGraph(kind, null, new(rootId), directory, SchemaRoot, ownedBatch)).Unwrap();
            _expectedSources.Add(address, expectedSource);
            return address;
        }

        internal void Cold(Action<GraphRecordStore> verify) {
            _frames.ConfirmDurable();
            Dispose();
            using Frames reopened = Frames.OpenReadOnly(_path);
            using GraphRecordStore reader = new(reopened);
            verify(reader);
        }

        public void Dispose() {
            if (_closed) { return; }
            _closed = true;
            Store.Dispose();
            _frames.Dispose();
        }
    }
}
