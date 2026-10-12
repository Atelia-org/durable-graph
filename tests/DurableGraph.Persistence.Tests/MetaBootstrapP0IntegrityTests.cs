using Atelia.Data;
using Atelia.DurableGraph.Persistence.MetaBootstrapP0;
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
        P0GraphSnapshot initial = fixture.State(null, [B(1, [10])]);
        P0GraphSnapshot updated = fixture.State(initial.Address, [B(1, [20])]);
        P0GraphSnapshot sibling = fixture.State(initial.Address, [B(1, [30])]);
        FrameAddress? wrongSource = error switch {
            "stale" => initial.Address,
            "otherBranch" => sibling.Address,
            "missing" => null,
            _ => throw new ArgumentOutOfRangeException(nameof(error)),
        };
        FrameAddress batch = fixture.Batch(wrongSource, [D(1, updated.Objects[1], [40])]);
        FrameAddress directory = fixture.BaseDirectory(updated.Address, Map((1u, batch)));
        FrameAddress bad = fixture.Graph(P0GraphKind.State, updated.Address, 1, directory, batch);

        fixture.Cold(store => {
            Assert.Equal(updated.Objects[1], store.ReadGraph(updated.Address).Objects[1]);
            Assert.Throws<InvalidDataException>(() => store.ReadGraph(bad));
        });
    }

    [Theory]
    [InlineData("omittedFromBase")]
    [InlineData("removedByDelta")]
    [InlineData("externalHead")]
    public void Cold_read_rejects_owned_local_record_not_selected_by_directory(string error) {
        using Fixture fixture = CreateFixture();
        P0GraphSnapshot initial = fixture.State(null, [B(1, [10]), B(2, [20])]);
        FrameAddress batch = fixture.Batch(initial.Address, [B(1, [11]), B(2, [21])]);
        var heads = Map((1u, batch));
        if (error == "externalHead") { heads.Add(2, initial.Objects[2]); }
        FrameAddress directory = error == "removedByDelta"
            ? fixture.Directory(initial.Address, ObjectVersionKind.Delta, initial.DirectoryRoot,
                P0DirectoryCodec.PrepareDelta(initial.Objects, heads).Body)
            : fixture.BaseDirectory(initial.Address, heads);
        FrameAddress bad = fixture.Graph(P0GraphKind.State, initial.Address, 1, directory, batch);

        fixture.Cold(store => {
            Assert.Equal(heads.OrderBy(static pair => pair.Key).ToArray(),
                store.ReadDirectory(directory).OrderBy(static pair => pair.Key).ToArray());
            Assert.Throws<InvalidDataException>(() => store.ReadGraph(bad));
        });
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("otherBranch")]
    [InlineData("graphAddress")]
    public void Retained_bad_Delta_uses_its_own_batch_source_and_fails_cold(string error) {
        using Fixture fixture = CreateFixture();
        P0GraphSnapshot initial = fixture.State(null, [B(1, [10])]);
        P0GraphSnapshot updated = fixture.State(initial.Address, [D(1, initial.Objects[1], [20])]);
        P0GraphSnapshot sibling = fixture.State(initial.Address, [B(1, [30])]);
        FrameAddress wrongPrior = error switch {
            "stale" => initial.Objects[1],
            "otherBranch" => sibling.Objects[1],
            "graphAddress" => updated.Address,
            _ => throw new ArgumentOutOfRangeException(nameof(error)),
        };
        FrameAddress batch = fixture.Batch(updated.Address, [D(1, wrongPrior, [40])]);
        FrameAddress directory = fixture.BaseDirectory(updated.Address, Map((1u, batch)));
        // An external head remains a shallow declaration. The content-chain read
        // must still reject its stale edge, even after another graph retains it.
        FrameAddress external = fixture.Graph(P0GraphKind.State, updated.Address, 1, directory, null);
        FrameAddress retained = fixture.Graph(P0GraphKind.State, external, 1, directory, null);

        fixture.Cold(store => {
            Assert.Equal(batch, store.ReadGraph(external).Objects[1]);
            Assert.Equal(batch, store.ReadGraph(retained).Objects[1]);
            Assert.Throws<InvalidDataException>(() => store.ReadObjectChain(retained, 1));
        });
    }

    [Fact]
    public void Retained_valid_Delta_keeps_original_source_across_events_and_unchanged_State() {
        using Fixture fixture = CreateFixture();
        P0GraphSnapshot initial = fixture.State(null, [B(1, [10])]);
        P0GraphSnapshot updated = fixture.State(initial.Address, [D(1, initial.Objects[1], [20])]);
        P0GraphSnapshot firstEvent = fixture.Append(P0GraphKind.Event, updated.Address, 1, []);
        P0GraphSnapshot secondEvent = fixture.Append(P0GraphKind.Event, firstEvent.Address, 1, []);
        P0GraphSnapshot unchanged = fixture.State(secondEvent.Address, []);

        fixture.Cold(store => {
            P0GraphSnapshot restored = store.ReadGraph(unchanged.Address);
            Assert.Equal(secondEvent.Address, restored.Parent);
            Assert.Equal(updated.Address, restored.SourceGraph);
            Assert.Equal(updated.DirectoryRoot, restored.DirectoryRoot);
            Assert.Null(restored.OwnedBatch);
            P0ObjectChain chain = store.ReadObjectChain(unchanged.Address, 1);
            Assert.Equal(new[] { initial.OwnedBatch!.Value, updated.OwnedBatch!.Value },
                chain.Entries.Select(static entry => entry.ContainingBatch).ToArray());
            Assert.Null(chain.Entries[0].SourceGraph);
            Assert.Equal(initial.Address, chain.Entries[1].SourceGraph);
            Assert.Equal(new byte[] { 10 }, chain.Entries[0].Record.Body.ToArray());
            Assert.Equal(new byte[] { 20 }, chain.Entries[1].Record.Body.ToArray());
            Assert.Equal(ObjectVersionPayloadSize.GetBasePayloadBytes(1) +
                ObjectVersionPayloadSize.GetDeltaPayloadBytes(1, initial.Objects[1]),
                chain.ReconstructionPayloadBytes);
        });
    }

    [Theory]
    [InlineData("baseCreator")]
    [InlineData("deltaCreator")]
    [InlineData("deltaPrior")]
    public void Cold_read_rejects_directory_created_from_wrong_graph_or_prior(string error) {
        using Fixture fixture = CreateFixture();
        P0GraphSnapshot initial = fixture.State(null, [B(1, [10])]);
        P0GraphSnapshot updated = fixture.State(initial.Address, [B(1, [20])]);
        FrameAddress directory = error switch {
            "baseCreator" => fixture.BaseDirectory(initial.Address, updated.Objects),
            "deltaCreator" => fixture.Directory(initial.Address, ObjectVersionKind.Delta,
                updated.DirectoryRoot, P0DirectoryCodec.PrepareDelta(updated.Objects, updated.Objects).Body),
            "deltaPrior" => fixture.Directory(updated.Address, ObjectVersionKind.Delta,
                initial.DirectoryRoot, P0DirectoryCodec.PrepareDelta(updated.Objects, updated.Objects).Body),
            _ => throw new ArgumentOutOfRangeException(nameof(error)),
        };
        FrameAddress bad = fixture.Graph(P0GraphKind.State, updated.Address, 1, directory, null);

        fixture.Cold(store => Assert.Throws<InvalidDataException>(() => store.ReadGraph(bad)));
    }

    [Fact]
    public void Reused_directory_preserves_creator_and_allows_new_root_but_not_new_owned_batch() {
        using Fixture fixture = CreateFixture();
        P0GraphSnapshot initial = fixture.State(null, [B(1, [10]), B(2, [20])]);
        FrameAddress reused = fixture.Graph(P0GraphKind.State, initial.Address, 2, initial.DirectoryRoot, null);
        FrameAddress batch = fixture.Batch(initial.Address, [B(1, [30])]);
        FrameAddress bad = fixture.Graph(P0GraphKind.State, initial.Address, 1, initial.DirectoryRoot, batch);

        fixture.Cold(store => {
            P0GraphSnapshot restored = store.ReadGraph(reused);
            Assert.Equal(new ObjectId(2), restored.RootId);
            Assert.Equal(initial.Address, restored.SourceGraph);
            Assert.Equal(initial.DirectoryRoot, restored.DirectoryRoot);
            Assert.Null(restored.OwnedBatch);
            Assert.Null(store.ReadDirectoryVersion(restored.DirectoryRoot).SourceGraph);
            Assert.Equal(new byte[] { 20 }, Assert.Single(store.ReadObjectChain(reused, 2).Entries).Record.Body.ToArray());
            Assert.Throws<InvalidDataException>(() => store.ReadGraph(bad));
        });
    }

    [Theory]
    [InlineData("missingId")]
    [InlineData("graphRole")]
    [InlineData("directoryRole")]
    public void Completed_locator_still_requires_batch_role_and_same_local_id(string error) {
        using Fixture fixture = CreateFixture();
        P0GraphSnapshot initial = fixture.State(null, [B(1, [10])]);
        FrameAddress wrongHead = error switch {
            "missingId" => fixture.Batch(initial.Address, [B(2, [20])]),
            "graphRole" => initial.Address,
            "directoryRole" => initial.DirectoryRoot,
            _ => throw new ArgumentOutOfRangeException(nameof(error)),
        };
        FrameAddress directory = fixture.BaseDirectory(initial.Address, Map((1u, wrongHead)));
        FrameAddress bad = fixture.Graph(P0GraphKind.State, initial.Address, 1, directory, null);

        fixture.Cold(store => {
            Assert.Equal(wrongHead, store.ReadDirectory(directory)[1]);
            Assert.Throws<InvalidDataException>(() => store.ReadGraph(bad));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void New_object_and_directory_Base_stop_bad_old_content_without_losing_source_metadata(bool badDirectory) {
        using Fixture fixture = CreateFixture();
        P0GraphSnapshot initial = fixture.State(null, [B(1, [10])]);
        P0GraphSnapshot updated = fixture.State(initial.Address, [D(1, initial.Objects[1], [20])]);
        FrameAddress badBatch = fixture.Batch(updated.Address, [badDirectory
            ? B(1, [30]) : D(1, initial.Objects[1], [30])]);
        var badHeads = Map((1u, badBatch));
        FrameAddress badRoot = badDirectory
            ? fixture.Directory(updated.Address, ObjectVersionKind.Delta, initial.DirectoryRoot,
                P0DirectoryCodec.PrepareDelta(updated.Objects, badHeads).Body)
            : fixture.BaseDirectory(updated.Address, badHeads);
        FrameAddress bad = fixture.Graph(P0GraphKind.State, updated.Address, 1, badRoot, badBatch);

        FrameAddress resetBatch = fixture.Batch(bad, [B(1, [])]);
        FrameAddress resetDirectory = fixture.BaseDirectory(bad, Map((1u, resetBatch)));
        FrameAddress reset = fixture.Graph(P0GraphKind.State, bad, 1, resetDirectory, resetBatch);
        FrameAddress extendingBatch = fixture.Batch(bad, [D(1, badBatch, [40])]);
        FrameAddress extendingDirectory = fixture.BaseDirectory(bad, Map((1u, extendingBatch)));
        FrameAddress extending = fixture.Graph(P0GraphKind.State, bad, 1, extendingDirectory, extendingBatch);

        fixture.Cold(store => {
            Assert.Throws<InvalidDataException>(() => store.ReadGraph(bad));
            P0GraphSnapshot restored = store.ReadGraph(reset);
            Assert.Equal(bad, restored.Parent);
            Assert.Equal(bad, restored.SourceGraph);
            Assert.Equal(ObjectVersionKind.Base, store.ReadDirectoryVersion(resetDirectory).Kind);
            P0ObjectChain chain = store.ReadObjectChain(reset, 1);
            P0ChainEntry only = Assert.Single(chain.Entries);
            Assert.Equal(resetBatch, only.ContainingBatch);
            Assert.Equal(bad, only.SourceGraph);
            Assert.Empty(only.Record.Body.ToArray());
            Assert.Equal(ObjectVersionPayloadSize.GetBasePayloadBytes(0), chain.ReconstructionPayloadBytes);
            // The same older dependency must remain invalid when actually needed.
            Assert.Throws<InvalidDataException>(() => store.ReadObjectChain(extending, 1));
        });
    }

    [Fact]
    public void Removed_id_cannot_rejoin_old_head_via_Delta_but_new_Base_can_start_new_chain() {
        using Fixture fixture = CreateFixture();
        P0GraphSnapshot initial = fixture.State(null, [B(1, [10]), B(2, [20])]);
        P0GraphSnapshot removed = fixture.Append(P0GraphKind.State, initial.Address, 2, [], [1]);
        FrameAddress badBatch = fixture.Batch(removed.Address, [D(1, initial.Objects[1], [30])]);
        FrameAddress badDirectory = fixture.BaseDirectory(removed.Address,
            Map((1u, badBatch), (2u, removed.Objects[2])));
        FrameAddress bad = fixture.Graph(P0GraphKind.State, removed.Address, 1, badDirectory, badBatch);
        P0GraphSnapshot fresh = fixture.State(removed.Address, [B(1, [90])]);
        P0GraphSnapshot latest = fixture.State(fresh.Address, [D(1, fresh.Objects[1], [91])]);

        fixture.Cold(store => {
            Assert.False(store.ReadGraph(removed.Address).Objects.ContainsKey(1));
            Assert.Throws<InvalidDataException>(() => store.ReadObjectChain(bad, 1));
            P0ObjectChain chain = store.ReadObjectChain(latest.Address, 1);
            Assert.Equal(new[] { fresh.OwnedBatch!.Value, latest.OwnedBatch!.Value },
                chain.Entries.Select(static entry => entry.ContainingBatch).ToArray());
            Assert.Equal(new byte[] { 90 }, chain.Entries[0].Record.Body.ToArray());
            Assert.Equal(new byte[] { 10 }, Assert.Single(store.ReadObjectChain(initial.Address, 1).Entries).Record.Body.ToArray());
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

        internal Fixture(string path) {
            _path = path;
            _frames = Frames.Create(path);
            Store = new(_frames);
            // Only the graph role is relevant here. Typed catalog projection has
            // separate generated-model tests; this fixture uses opaque raw bodies.
            FrameAddress batch = _frames.Append(P0GraphWire.BatchTag,
                P0GraphWire.EncodeBatch(null, null, [B(1, [0])])).Unwrap();
            FrameAddress directory = BaseDirectory(null, Map((1u, batch)));
            SchemaRoot = _frames.Append(P0GraphWire.GraphTag,
                P0GraphWire.EncodeGraph(P0GraphKind.Meta, null, new(1), directory, null, batch)).Unwrap();
        }

        internal P0GraphStore Store { get; }
        internal FrameAddress SchemaRoot { get; }

        internal P0GraphSnapshot State(FrameAddress? parent, ObjectVersionRecord[] records) =>
            Append(P0GraphKind.State, parent, 1, records);

        internal P0GraphSnapshot Append(P0GraphKind kind, FrameAddress? parent, uint rootId,
            ObjectVersionRecord[] records, uint[]? removed = null) =>
            Store.Append(kind, parent, SchemaRoot,
                new(Store.ResolveContentSource(kind, parent), new(rootId), records, removed));

        internal FrameAddress Batch(FrameAddress? source, ObjectVersionRecord[] records) =>
            _frames.Append(P0GraphWire.BatchTag, P0GraphWire.EncodeBatch(source, SchemaRoot, records)).Unwrap();

        internal FrameAddress BaseDirectory(FrameAddress? source, IReadOnlyDictionary<uint, FrameAddress> heads) =>
            Directory(source, ObjectVersionKind.Base, null, P0DirectoryCodec.PrepareBase(heads).Body);

        internal FrameAddress Directory(FrameAddress? source, ObjectVersionKind kind,
            FrameAddress? prior, ReadOnlySpan<byte> body) =>
            _frames.Append(P0GraphWire.DirectoryTag, P0GraphWire.EncodeDirectory(source, kind, prior, body)).Unwrap();

        internal FrameAddress Graph(P0GraphKind kind, FrameAddress? parent, uint rootId,
            FrameAddress directory, FrameAddress? ownedBatch) =>
            _frames.Append(P0GraphWire.GraphTag,
                P0GraphWire.EncodeGraph(kind, parent, new(rootId), directory, SchemaRoot, ownedBatch)).Unwrap();

        internal void Cold(Action<P0GraphStore> verify) {
            _frames.ConfirmDurable();
            Dispose();
            using Frames reopened = Frames.OpenReadOnly(_path);
            using P0GraphStore reader = new(reopened);
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
