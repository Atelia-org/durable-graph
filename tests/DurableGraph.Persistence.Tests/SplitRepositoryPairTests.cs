using static Atelia.DurableGraph.Persistence.Tests.DB096TestAccess;
using System.Buffers;

using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class SplitRepositoryPairTests : IDisposable {
    private const string Prefix = "dg-split-repository-pair-";
    private const string Text = "Pair preserves two equal but distinct nonempty strings.";
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
    private static readonly DurableSchema Extra = new("SplitPairExtra", 1, new DurableFieldInfo(1, TypeTag.Int32));
    private static readonly DurableSchema ProbeV1 = new("SplitPairProbe", 1, new DurableFieldInfo(1, TypeTag.Int32));
    private static readonly DurableSchema ProbeV2 = new("SplitPairProbe", 2, new DurableFieldInfo(1, TypeTag.Int32));

    // These identity assertions witness the current sharing optimization. The public Pair
    // contract continues to require read-only results without promising cross-view identity.
    [Fact]
    public void SameSchemaSharesStableGeneratedClosureAndChangedChildSplitsItsAncestors() {
        using (Repository seed = Repository.CreateStorageNew(_path, GeneratedModels())) {
            SplitPairEnvelope root = Envelope();
            using WorkingCopy main = seed.OpenWorkingCopy(seed.CreateRootBranchFromState("main", root).Id);
            root = Assert.IsType<SplitPairEnvelope>(main.State);
            CommitId s0 = main.BaseCommitId;
            seed.CreateTag("original", s0);
            CommitId s1 = main.CommitState().Id;
            seed.CreateTag("stable", s1);
            StoredGraphRevision first = Snapshot(seed, s0);
            StoredGraphRevision stable = Snapshot(seed, s1);
            Assert.Equal(7, first.ObjectHeads.Count); // Envelope, two Widgets, List, and three string IDs.
            Assert.Null(stable.OwnedBatch);
            Assert.Equal(first.DirectoryRoot, stable.DirectoryRoot);
            root.Widget!.Child!.Value = 20;
            CommitId s2 = main.CommitState().Id;
            seed.CreateTag("changed", s2);
            StoredGraphRevision changed = Snapshot(seed, s2);
            ObjectVersionRecord child = Assert.Single(seed.GraphStore.ReadBatch(changed.OwnedBatch!.Value).Records);
            Assert.NotEqual(first.ObjectHeads[child.ObjectId], changed.ObjectHeads[child.ObjectId]);
            Assert.Equal(first.ObjectHeads[main.StateId!.Value.Value], changed.ObjectHeads[main.StateId.Value.Value]);
            foreach ((uint id, FrameAddress head) in first.ObjectHeads) {
                if (id != child.ObjectId) { Assert.Equal(head, changed.ObjectHeads[id]); }
            }
            Assert.Equal(first.SchemaRoot, changed.SchemaRoot);
        }
        Dictionary<string, byte[]> files = SnapshotFiles();
        using (Repository reader = Repository.OpenStorageReadOnlyExisting(_path, GeneratedModels())) {
            CommitId original = reader.ResolveTag("original"), stable = reader.ResolveTag("stable"), changed = reader.ResolveTag("changed");
            SplitPairEnvelope independentlyDecodedFirst = AssertEnvelope(reader.LoadState(original), 2);
            SplitPairEnvelope independentlyDecodedSecond = AssertEnvelope(reader.LoadState(stable), 2);
            Assert.NotSame(independentlyDecodedFirst.Text, independentlyDecodedSecond.Text);
            Assert.NotSame(independentlyDecodedFirst.SameText, independentlyDecodedSecond.SameText);
            // Same physical heads yield distinct strings under separate ordinary reads;
            // Pair must prove same-ID sharing without interning the other equal string ID.
            var unchanged = reader.ReadPair(original, stable);
            SplitPairEnvelope first = AssertEnvelope(unchanged.First, 2);
            Assert.Same(first, AssertEnvelope(unchanged.Second, 2));
            Assert.NotSame(first.Text, first.SameText); // Equal bytes under different IDs must not intern.
            Assert.Same(string.Empty, first.Empty);
            var split = reader.ReadPair(original, changed);
            SplitPairEnvelope oldRoot = AssertEnvelope(split.First, 2), newRoot = AssertEnvelope(split.Second, 20);
            Assert.NotSame(oldRoot, newRoot);
            Assert.NotSame(oldRoot.Widget, newRoot.Widget);
            Assert.NotSame(oldRoot.Widget!.Child, newRoot.Widget!.Child);
            Assert.Same(oldRoot.Widget.Numbers, newRoot.Widget.Numbers); // This leaf has no dependency on the changed cycle.
            Assert.Same(oldRoot.Text, newRoot.Text);
            Assert.Same(oldRoot.SameText, newRoot.SameText);
            Assert.NotSame(oldRoot.Text, newRoot.SameText);
            Assert.Same(string.Empty, newRoot.Empty);
            var reverse = reader.ReadPair(changed, original);
            AssertEnvelope(reverse.First, 20);
            AssertEnvelope(reverse.Second, 2);
            SplitPairEnvelope independent = AssertEnvelope(reader.LoadState(original), 2);
            Assert.NotSame(oldRoot, independent);
            Assert.NotSame(oldRoot.Widget.Numbers, independent.Widget!.Numbers);
            Assert.Equal(0, reader.GraphStore.AppendedFrames);
        }
        AssertFilesEqual(files, SnapshotFiles());
    }

    [Fact]
    public void DifferentSchemaHydrationClosuresKeepEqualCurrentDtosIndependentAndMatchSingleReads() {
        using (Repository seed = Repository.CreateStorageNew(_path, ProbeModels(1, new()))) {
            using WorkingCopy main = seed.OpenWorkingCopy(seed.CreateRootBranchFromState("main", new ProbeRoot { Value = 7 }).Id);
            seed.CreateTag("before", main.BaseCommitId);
            CommitId next = seed.CommitBranches([main.CreateEventCommitRequest(main.State!)], [Extra])[main.BranchId].Id;
            seed.CreateTag("after", next);
            StoredGraphRevision first = Snapshot(seed, main.StateSourceCommitId!.Value);
            StoredGraphRevision second = Snapshot(seed, next);
            Assert.NotEqual(first.SchemaRoot, second.SchemaRoot);
            Assert.NotEqual(first.ObjectHeads[first.RootId.Value], second.ObjectHeads[second.RootId.Value]);
            Assert.NotNull(second.OwnedBatch);
            Assert.Null(second.SourceGraph);
        }
        Dictionary<string, byte[]> files = SnapshotFiles();
        Counts counts = new() { HydrateFromSchema = true };
        StateModelRegistry registry = ProbeModels(1, counts);
        using (Repository reader = Repository.OpenStorageReadOnlyExisting(_path, registry)) {
            CommitId first = reader.ResolveTag("before"), second = reader.ResolveTag("after");
            DecodedRevision oldDto = DecodedRevision.ReadStoredGraph(reader.GraphStore, Snapshot(reader, first), registry.FreezeConfiguration());
            DecodedRevision newDto = DecodedRevision.ReadStoredGraph(reader.GraphStore, Snapshot(reader, second), registry.FreezeConfiguration());
            Assert.Equal(7, oldDto.GetRequired(Describe(reader, first).RootId).GetState<int>());
            Assert.Equal(oldDto.GetRequired(Describe(reader, first).RootId).Schema, newDto.GetRequired(Describe(reader, second).RootId).Schema);
            Assert.Equal(7, newDto.GetRequired(Describe(reader, second).RootId).GetState<int>());
            counts.ResetObservations();
            var pair = reader.ReadPair(first, second);
            ProbeRoot oldRoot = Assert.IsType<ProbeRoot>(pair.First), newRoot = Assert.IsType<ProbeRoot>(pair.Second);
            Assert.Equal(7, oldRoot.Value);
            Assert.Equal(107, newRoot.Value);
            Assert.NotSame(oldRoot, newRoot);
            Assert.Equal(new[] { false, true }, counts.Lookups);
            Assert.Equal(2, counts.Hydrates);
            Assert.Equal(0, counts.Normalizes); // Both layouts are current; the difference exists only in Hydrate's environment.
            Assert.Equal(oldRoot.Value, Assert.IsType<ProbeRoot>(reader.LoadState(first)).Value);
            Assert.Equal(newRoot.Value, Assert.IsType<ProbeRoot>(reader.LoadEvent(second)).Value);
            var reverse = reader.ReadPair(second, first);
            Assert.Equal(107, Assert.IsType<ProbeRoot>(reverse.First).Value);
            Assert.Equal(7, Assert.IsType<ProbeRoot>(reverse.Second).Value);
            Assert.False(reader.IsFaulted);
            Assert.Equal(0, reader.GraphStore.AppendedFrames);
        }
        AssertFilesEqual(files, SnapshotFiles());
    }

    [Fact]
    public void HistoricalRewriteIsNotSharedEvenWithinOneEnvironmentAndConfirmedBaseBecomesEligible() {
        using (Repository seed = Repository.CreateStorageNew(_path, ProbeModels(1, new()))) {
            using WorkingCopy main = seed.OpenWorkingCopy(seed.CreateRootBranchFromState("main", new ProbeRoot { Value = 7 }).Id);
            seed.CreateTag("old", main.BaseCommitId);
        }
        Counts counts = new();
        StateModelRegistry registry = ProbeModels(2, counts);
        Dictionary<string, byte[]> files = SnapshotFiles();
        using (Repository reader = Repository.OpenStorageReadOnlyExisting(_path, registry)) {
            CommitId old = reader.ResolveTag("old");
            DecodedRevision source = DecodedRevision.ReadStoredGraph(reader.GraphStore, Snapshot(reader, old), registry.FreezeConfiguration());
            NormalizedGraphState normalized = NormalizedGraphState.Create(source, source.SourceModels!);
            Assert.Equal(ProbeV1, source.GetRequired(Describe(reader, old).RootId).Schema);
            Assert.Equal(ProbeV2, normalized.Objects[Describe(reader, old).RootId].Current.Schema);
            Assert.True(normalized.Objects[Describe(reader, old).RootId].RequiresRewrite);
            counts.ResetObservations();
            var pair = reader.ReadPair(old, old);
            Assert.Equal(7, Assert.IsType<ProbeRoot>(pair.First).Value);
            Assert.Equal(7, Assert.IsType<ProbeRoot>(pair.Second).Value);
            Assert.NotSame(pair.First, pair.Second);
            Assert.Single(counts.Lookups); // One same-S binding; rewrite still forbids sharing.
            Assert.Equal(2, counts.Normalizes);
            Assert.Equal(2, counts.Hydrates);
        }
        AssertFilesEqual(files, SnapshotFiles());
        using (Repository writer = Repository.OpenStorageExisting(_path, registry)) {
            using WorkingCopy main = writer.OpenWorkingCopy(BranchNamed(writer, "main"));
            CommitId current = main.CommitState().Id;
            StoredGraphRevision graph = Snapshot(writer, current);
            ObjectVersionRecord rewritten = Assert.Single(writer.GraphStore.ReadBatch(graph.OwnedBatch!.Value).Records);
            Assert.Equal(ObjectVersionKind.Base, rewritten.Kind);
            Assert.Equal(Describe(writer, current).RootId.Value, rewritten.ObjectId);
            Assert.False(main.Baseline!.Objects[Describe(writer, current).RootId].RequiresRewrite);
            var eligible = writer.ReadPair(current, current);
            Assert.Same(eligible.First, eligible.Second);
            Assert.Equal(7, Assert.IsType<ProbeRoot>(eligible.First).Value);
            CommitId old = writer.ResolveTag("old");
            var mixed = writer.ReadPair(old, current);
            Assert.NotSame(mixed.First, mixed.Second);
            Assert.Equal(7, Assert.IsType<ProbeRoot>(mixed.First).Value);
            Assert.Equal(7, Assert.IsType<ProbeRoot>(mixed.Second).Value);
        }
    }

    [Fact]
    public void SecondHydrationFailureDeliversNoPairAndOwnerRemainsUsable() {
        SeedProbePair();
        Dictionary<string, byte[]> files = SnapshotFiles();
        Counts counts = new() { FailValue = 8 };
        using (Repository reader = Repository.OpenStorageReadOnlyExisting(_path, ProbeModels(1, counts))) {
            CommitId first = reader.ResolveTag("first"), second = reader.ResolveTag("second");
            (IDurableObject First, IDurableObject Second)? delivered = null;
            Assert.Same(counts.Failure, Assert.Throws<InvalidDataException>(() => delivered = reader.ReadPair(first, second)));
            Assert.Null(delivered);
            Assert.Equal(2, counts.Allocations);
            Assert.Equal(2, counts.Hydrates); // The first callback can run; its object is still not delivered.
            Assert.False(reader.IsFaulted);
            Assert.Equal(second, reader.ReadCurrentCommit(BranchNamed(reader, "main")).Commit.Id);
            counts.FailValue = null;
            var success = reader.ReadPair(first, second);
            Assert.Equal(7, Assert.IsType<ProbeRoot>(success.First).Value);
            Assert.Equal(8, Assert.IsType<ProbeRoot>(success.Second).Value);
            Assert.NotSame(success.First, success.Second);
        }
        AssertFilesEqual(files, SnapshotFiles());
    }

    [Fact]
    public void CrossGraphRepeatedMutableAllocatorIsRejectedBeforeEitherHydration() {
        SeedProbePair();
        Dictionary<string, byte[]> files = SnapshotFiles();
        Counts counts = new() { Singleton = new ProbeRoot { Value = 100 } };
        using (Repository reader = Repository.OpenStorageReadOnlyExisting(_path, ProbeModels(1, counts))) {
            CommitId first = reader.ResolveTag("first"), second = reader.ResolveTag("second");
            (IDurableObject First, IDurableObject Second)? delivered = null;
            Assert.Throws<InvalidDataException>(() => delivered = reader.ReadPair(first, second));
            Assert.Null(delivered);
            Assert.Equal(2, counts.Allocations);
            Assert.Equal(0, counts.Hydrates);
            Assert.Equal(100, counts.Singleton!.Value);
            Assert.False(reader.IsFaulted);
            counts.Singleton = null;
            var success = reader.ReadPair(first, second);
            Assert.Equal(7, Assert.IsType<ProbeRoot>(success.First).Value);
            Assert.Equal(8, Assert.IsType<ProbeRoot>(success.Second).Value);
            Assert.NotSame(success.First, success.Second);
            Assert.Equal(2, counts.Hydrates);
        }
        AssertFilesEqual(files, SnapshotFiles());
    }

    [Fact]
    public void PairAcceptsReopenedHandlesAndChecksLifetimeAndCallbackReentry() {
        CommitId stale = SeedProbePair();
        Dictionary<string, byte[]> files = SnapshotFiles();
        Counts counts = new();
        Repository reader = Repository.OpenStorageReadOnlyExisting(_path, ProbeModels(1, counts));
        try {
            CommitId first = reader.ResolveTag("first"), second = reader.ResolveTag("second");
            Assert.Equal(7, Assert.IsType<ProbeRoot>(reader.ReadPair(stale, second).First).Value);
            Assert.Equal(7, Assert.IsType<ProbeRoot>(reader.ReadPair(first, stale).Second).Value);
            counts.ResetObservations();
            Assert.Throws<ArgumentException>(() => reader.ReadPair(default, second));
            Assert.Throws<ArgumentException>(() => reader.ReadPair(first, default));
            Assert.Equal(0, counts.Allocations);
            counts.DuringHydrate = () => {
                Assert.Throws<InvalidOperationException>(() => reader.ReadPair(first, second));
                Assert.Throws<InvalidOperationException>(() => reader.LoadState(first));
                Assert.Throws<InvalidOperationException>(() => reader.OpenWorkingCopy(BranchNamed(reader, "main")));
                Assert.Throws<InvalidOperationException>(() => reader.Dispose());
            };
            var pair = reader.ReadPair(first, second);
            Assert.Equal(7, Assert.IsType<ProbeRoot>(pair.First).Value);
            Assert.Equal(8, Assert.IsType<ProbeRoot>(pair.Second).Value);
            Assert.Equal(2, counts.Hydrates);
            counts.DuringHydrate = null;
            Assert.Equal(7, Assert.IsType<ProbeRoot>(reader.LoadState(first)).Value);
            Assert.False(reader.IsFaulted);
            reader.Dispose();
            Assert.Throws<ObjectDisposedException>(() => reader.ReadPair(first, second));
            Assert.Equal(7, Assert.IsType<ProbeRoot>(pair.First).Value);
            Assert.Equal(8, Assert.IsType<ProbeRoot>(pair.Second).Value); // Delivered objects outlive the owner.
        } finally { reader.Dispose(); }
        AssertFilesEqual(files, SnapshotFiles());
    }

    private CommitId SeedProbePair() {
        using Repository seed = Repository.CreateStorageNew(_path, ProbeModels(1, new()));
        using WorkingCopy main = seed.OpenWorkingCopy(seed.CreateRootBranchFromState("main", new ProbeRoot { Value = 7 }).Id);
        CommitId first = main.BaseCommitId;
        seed.CreateTag("first", first);
        Assert.IsType<ProbeRoot>(main.State).Value = 8;
        CommitId second = main.CommitState().Id;
        seed.CreateTag("second", second);
        Assert.Equal(Snapshot(seed, first).SchemaRoot,
            Snapshot(seed, second).SchemaRoot);
        return first;
    }

    private static StateModelRegistry GeneratedModels() {
        StateModelRegistry registry = new();
        registry.Register(global::Atelia.DurableGraph.Generated.Family_5030576964676574.Definition);
        registry.Register(global::Atelia.DurableGraph.Generated.Family_53706C697450616972456E76656C6F7065.Definition);
        return registry;
    }

    private static SplitPairEnvelope Envelope() {
        List<int> numbers = Enumerable.Range(0, 128).ToList();
        DB087Widget widget = new() { Value = 1, Numbers = numbers };
        widget.Child = new() { Value = 2, Numbers = numbers, Child = widget };
        return new() { Widget = widget, Text = new string(Text.ToCharArray()), SameText = new string(Text.ToCharArray()), Empty = string.Empty };
    }

    private static SplitPairEnvelope AssertEnvelope(IDurableObject value, int childValue) {
        SplitPairEnvelope root = Assert.IsType<SplitPairEnvelope>(value);
        DB087Widget widget = Assert.IsType<DB087Widget>(root.Widget);
        Assert.Equal(1, widget.Value);
        DB087Widget child = Assert.IsType<DB087Widget>(widget.Child);
        Assert.Equal(childValue, child.Value);
        Assert.Same(widget, child.Child);
        Assert.Same(widget.Numbers, child.Numbers);
        Assert.Equal(Enumerable.Range(0, 128), widget.Numbers);
        Assert.Equal(Text, root.Text);
        Assert.Equal(Text, root.SameText);
        Assert.NotSame(root.Text, root.SameText);
        Assert.Same(string.Empty, root.Empty);
        return root;
    }

    private static StateModelRegistry ProbeModels(int currentVersion, Counts counts) {
        StateModelRegistry registry = new();
        registry.Register(new StateDefinitionBinding(ProbeV1.SchemaId, SchemaKind.ReferenceObject, 0, typeof(ProbeRoot),
            new StateSchemaTemplate[] {
                new(ProbeV1.SchemaId, 1, SchemaKind.ReferenceObject, 0, [new(1, TypeExpr.Builtin(TypeTag.Int32))]),
                new(ProbeV1.SchemaId, 2, SchemaKind.ReferenceObject, 0, [new(1, TypeExpr.Builtin(TypeTag.Int32))]),
            }.Take(currentVersion),
            currentModelFactory: (type, context) => {
                Assert.Equal(typeof(ProbeRoot), type);
                bool extra = SchemaLookupCounterexampleProbe.TryGetSchema(context, Extra.Type, Extra.Version, out _);
                counts.Lookups.Add(extra);
                int offset = counts.HydrateFromSchema && extra ? 100 : 0;
                DurableSchema current = currentVersion == 1 ? ProbeV1 : ProbeV2;
                return new StateModelBinding<ProbeRoot, int>(new(current, BaseInt,
                    static (in int prior, in int now) => new(prior != now, BaseInt(in now).Body),
                    static (in int left, in int right) => left == right),
                    Enumerable.Range(1, currentVersion).Select(version => context.ResolveReader(version == 1 ? ProbeV1 : ProbeV2)),
                    row => { counts.Normalizes++; return row.GetState<int>(); },
                    () => { counts.Allocations++; return counts.Singleton ?? new ProbeRoot(); },
                    (ProbeRoot domain, in int state, ObjectReadTable _) => {
                        counts.Hydrates++;
                        counts.DuringHydrate?.Invoke();
                        if (counts.FailValue == state) { throw counts.Failure; }
                        domain.Value = state + offset;
                    }, (domain, _) => domain.Value - offset, static (in int _, IStateReferenceVisitor _) => { });
            }, historicalReaderFactory: static (schema, _) => new StateReaderBinding<int>(schema,
                static (ref BareValueReader input) => input.ReadVarInt32(),
                static (ref BareValueReader input, in int _) => input.ReadVarInt32(),
                static (in int _, IStateReferenceVisitor _) => { })));
        return registry;
    }

    private static PreparedBaseBody BaseInt(in int value) {
        ArrayBufferWriter<byte> bytes = new();
        BareValueWriter writer = new(bytes);
        writer.WriteVarInt32(value);
        return new(bytes.WrittenSpan);
    }
    private sealed class ProbeRoot : IDurableObject { internal int Value; }
    private sealed class Counts {
        internal int Normalizes, Allocations, Hydrates;
        internal bool HydrateFromSchema;
        internal int? FailValue;
        internal ProbeRoot? Singleton;
        internal Action? DuringHydrate;
        internal InvalidDataException Failure { get; } = new("Pair's second hydration failed.");
        internal List<bool> Lookups { get; } = [];
        internal void ResetObservations() { Normalizes = Allocations = Hydrates = 0; Lookups.Clear(); }
    }
    private Dictionary<string, byte[]> SnapshotFiles() => Directory.GetFiles(_path, "*", SearchOption.AllDirectories)
        .ToDictionary(name => Path.GetRelativePath(_path, name), File.ReadAllBytes);
    private static void AssertFilesEqual(Dictionary<string, byte[]> expected, Dictionary<string, byte[]> actual) {
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach ((string name, byte[] bytes) in expected) { Assert.Equal(bytes, actual[name]); }
    }
    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}

// Actual SG schema/body, kept separate from the existing Widget's persisted layout.
[DurableType("SplitPairEnvelope", 1)]
internal sealed partial class SplitPairEnvelope : IDurableObject {
    [DurableField(1)] internal DB087Widget? Widget = null;
    [DurableField(2)] internal string? Text = null;
    [DurableField(3)] internal string? SameText = null;
    [DurableField(4)] internal string? Empty = null;
}
