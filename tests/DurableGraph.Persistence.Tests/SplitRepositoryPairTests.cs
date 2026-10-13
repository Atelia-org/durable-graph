using System.Buffers;
using Atelia.DurableGraph.Persistence.MetaBootstrapP0;
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
        using (Repository seed = Repository.CreateMetaBootstrapNew(_path, GeneratedModels(), schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared)) {
            SplitPairEnvelope root = Envelope();
            using BranchCheckout main = seed.CreateBranch("main", root);
            CheckpointAddress s0 = main.Head;
            seed.CreateTag("original", s0);
            CheckpointAddress s1 = main.CommitState();
            seed.CreateTag("stable", s1);
            P0GraphSnapshot first = seed.MetaBootstrapStore.ReadGraph(s0.RevisionAddress);
            P0GraphSnapshot stable = seed.MetaBootstrapStore.ReadGraph(s1.RevisionAddress);
            Assert.Equal(7, first.Objects.Count); // Envelope, two Widgets, List, and three string IDs.
            Assert.Null(stable.OwnedBatch);
            Assert.Equal(first.DirectoryRoot, stable.DirectoryRoot);
            root.Widget!.Child!.Value = 20;
            CheckpointAddress s2 = main.CommitState();
            seed.CreateTag("changed", s2);
            P0GraphSnapshot changed = seed.MetaBootstrapStore.ReadGraph(s2.RevisionAddress);
            ObjectVersionRecord child = Assert.Single(seed.MetaBootstrapStore.ReadBatch(changed.OwnedBatch!.Value).Records);
            Assert.NotEqual(first.Objects[child.ObjectId], changed.Objects[child.ObjectId]);
            Assert.Equal(first.Objects[main.StateId!.Value.Value], changed.Objects[main.StateId.Value.Value]);
            foreach ((uint id, FrameAddress head) in first.Objects) {
                if (id != child.ObjectId) { Assert.Equal(head, changed.Objects[id]); }
            }
            Assert.Equal(first.SchemaRoot, changed.SchemaRoot);
        }
        Dictionary<string, byte[]> files = SnapshotFiles();
        using (Repository reader = Repository.OpenMetaBootstrapReadOnlyExisting(_path, GeneratedModels())) {
            CheckpointAddress original = reader.ResolveTag("original"), stable = reader.ResolveTag("stable"), changed = reader.ResolveTag("changed");
            SplitPairEnvelope independentlyDecodedFirst = AssertEnvelope(reader.ReadState(original), 2);
            SplitPairEnvelope independentlyDecodedSecond = AssertEnvelope(reader.ReadState(stable), 2);
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
            SplitPairEnvelope independent = AssertEnvelope(reader.ReadState(original), 2);
            Assert.NotSame(oldRoot, independent);
            Assert.NotSame(oldRoot.Widget.Numbers, independent.Widget!.Numbers);
            Assert.Equal(0, reader.MetaBootstrapStore.AppendedFrames);
        }
        AssertFilesEqual(files, SnapshotFiles());
    }

    [Fact]
    public void DifferentSchemaHydrationClosuresKeepEqualCurrentDtosIndependentAndMatchSingleReads() {
        using (Repository seed = Repository.CreateMetaBootstrapNew(_path, ProbeModels(1, new()), schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared)) {
            using BranchCheckout main = seed.CreateBranch("main", new ProbeRoot { Value = 7 });
            seed.CreateTag("before", main.Head);
            main.Catalog.Set(Extra);
            CheckpointAddress next = main.CommitEvent(main.State!);
            seed.CreateTag("after", next);
            P0GraphSnapshot first = seed.MetaBootstrapStore.ReadGraph(main.StateRevisionAddress!.Value);
            P0GraphSnapshot second = seed.MetaBootstrapStore.ReadGraph(next.RevisionAddress);
            Assert.NotEqual(first.SchemaRoot, second.SchemaRoot);
            Assert.Equal(first.Objects[first.RootId.Value], second.Objects[second.RootId.Value]);
            Assert.Null(second.OwnedBatch);
        }
        Dictionary<string, byte[]> files = SnapshotFiles();
        Counts counts = new() { HydrateFromSchema = true };
        StateModelRegistry registry = ProbeModels(1, counts);
        using (Repository reader = Repository.OpenMetaBootstrapReadOnlyExisting(_path, registry)) {
            CheckpointAddress first = reader.ResolveTag("before"), second = reader.ResolveTag("after");
            DecodedRevision oldDto = DecodedRevision.ReadSplit(reader.MetaBootstrapStore, first.RevisionAddress, registry);
            DecodedRevision newDto = DecodedRevision.ReadSplit(reader.MetaBootstrapStore, second.RevisionAddress, registry);
            Assert.Equal(7, oldDto.GetRequired(first.RootId).GetState<int>());
            Assert.Equal(oldDto.GetRequired(first.RootId).Schema, newDto.GetRequired(second.RootId).Schema);
            Assert.Equal(7, newDto.GetRequired(second.RootId).GetState<int>());
            counts.ResetObservations();
            var pair = reader.ReadPair(first, second);
            ProbeRoot oldRoot = Assert.IsType<ProbeRoot>(pair.First), newRoot = Assert.IsType<ProbeRoot>(pair.Second);
            Assert.Equal(7, oldRoot.Value);
            Assert.Equal(107, newRoot.Value);
            Assert.NotSame(oldRoot, newRoot);
            Assert.Equal(new[] { false, true }, counts.Lookups);
            Assert.Equal(2, counts.Hydrates);
            Assert.Equal(0, counts.Normalizes); // Both layouts are current; the difference exists only in Hydrate's environment.
            Assert.Equal(oldRoot.Value, Assert.IsType<ProbeRoot>(reader.ReadState(first)).Value);
            Assert.Equal(newRoot.Value, Assert.IsType<ProbeRoot>(reader.ReadEvent(second)).Value);
            var reverse = reader.ReadPair(second, first);
            Assert.Equal(107, Assert.IsType<ProbeRoot>(reverse.First).Value);
            Assert.Equal(7, Assert.IsType<ProbeRoot>(reverse.Second).Value);
            Assert.False(reader.IsFaulted);
            Assert.Equal(0, reader.MetaBootstrapStore.AppendedFrames);
        }
        AssertFilesEqual(files, SnapshotFiles());
    }

    [Fact]
    public void HistoricalRewriteIsNotSharedEvenWithinOneEnvironmentAndConfirmedBaseBecomesEligible() {
        using (Repository seed = Repository.CreateMetaBootstrapNew(_path, ProbeModels(1, new()), schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared)) {
            using BranchCheckout main = seed.CreateBranch("main", new ProbeRoot { Value = 7 });
            seed.CreateTag("old", main.Head);
        }
        Counts counts = new();
        StateModelRegistry registry = ProbeModels(2, counts);
        Dictionary<string, byte[]> files = SnapshotFiles();
        using (Repository reader = Repository.OpenMetaBootstrapReadOnlyExisting(_path, registry)) {
            CheckpointAddress old = reader.ResolveTag("old");
            DecodedRevision source = DecodedRevision.ReadSplit(reader.MetaBootstrapStore, old.RevisionAddress, registry);
            NormalizedRevision normalized = NormalizedRevision.Create(source, source.SourceModels!);
            Assert.Equal(ProbeV1, source.GetRequired(old.RootId).Schema);
            Assert.Equal(ProbeV2, normalized.Objects[old.RootId].Current.Schema);
            Assert.True(normalized.Objects[old.RootId].RequiresRewrite);
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
        using (Repository writer = Repository.OpenMetaBootstrapExisting(_path, registry, schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared)) {
            using BranchCheckout main = writer.Checkout("main");
            CheckpointAddress current = main.CommitState();
            P0GraphSnapshot graph = writer.MetaBootstrapStore.ReadGraph(current.RevisionAddress);
            ObjectVersionRecord rewritten = Assert.Single(writer.MetaBootstrapStore.ReadBatch(graph.OwnedBatch!.Value).Records);
            Assert.Equal(ObjectVersionKind.Base, rewritten.Kind);
            Assert.Equal(current.RootId.Value, rewritten.ObjectId);
            Assert.False(main.Baseline!.Objects[current.RootId].RequiresRewrite);
            var eligible = writer.ReadPair(current, current);
            Assert.Same(eligible.First, eligible.Second);
            Assert.Equal(7, Assert.IsType<ProbeRoot>(eligible.First).Value);
            CheckpointAddress old = writer.ResolveTag("old");
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
        using (Repository reader = Repository.OpenMetaBootstrapReadOnlyExisting(_path, ProbeModels(1, counts))) {
            CheckpointAddress first = reader.ResolveTag("first"), second = reader.ResolveTag("second");
            (IDurableObject First, IDurableObject Second)? delivered = null;
            Assert.Same(counts.Failure, Assert.Throws<InvalidDataException>(() => delivered = reader.ReadPair(first, second)));
            Assert.Null(delivered);
            Assert.Equal(2, counts.Allocations);
            Assert.Equal(2, counts.Hydrates); // The first callback can run; its object is still not delivered.
            Assert.False(reader.IsFaulted);
            Assert.Equal(second, reader.GetHead("main"));
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
        using (Repository reader = Repository.OpenMetaBootstrapReadOnlyExisting(_path, ProbeModels(1, counts))) {
            CheckpointAddress first = reader.ResolveTag("first"), second = reader.ResolveTag("second");
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
    public void PairChecksOwnerAndLifetimeAndRejectsCallbackReentryWithoutDamagingOuterRead() {
        CheckpointAddress stale = SeedProbePair();
        Dictionary<string, byte[]> files = SnapshotFiles();
        Counts counts = new();
        Repository reader = Repository.OpenMetaBootstrapReadOnlyExisting(_path, ProbeModels(1, counts));
        try {
            CheckpointAddress first = reader.ResolveTag("first"), second = reader.ResolveTag("second");
            Assert.Throws<ArgumentException>(() => reader.ReadPair(stale, second));
            Assert.Throws<ArgumentException>(() => reader.ReadPair(first, stale));
            Assert.Throws<ArgumentNullException>(() => reader.ReadPair(null!, second));
            Assert.Throws<ArgumentNullException>(() => reader.ReadPair(first, null!));
            Assert.Equal(0, counts.Allocations);
            counts.DuringHydrate = () => {
                Assert.Throws<InvalidOperationException>(() => reader.ReadPair(first, second));
                Assert.Throws<InvalidOperationException>(() => reader.ReadState(first));
                Assert.Throws<InvalidOperationException>(() => reader.Checkout("main"));
                Assert.Throws<InvalidOperationException>(() => reader.Dispose());
            };
            var pair = reader.ReadPair(first, second);
            Assert.Equal(7, Assert.IsType<ProbeRoot>(pair.First).Value);
            Assert.Equal(8, Assert.IsType<ProbeRoot>(pair.Second).Value);
            Assert.Equal(2, counts.Hydrates);
            counts.DuringHydrate = null;
            Assert.Equal(7, Assert.IsType<ProbeRoot>(reader.ReadState(first)).Value);
            Assert.False(reader.IsFaulted);
            reader.Dispose();
            Assert.Throws<ObjectDisposedException>(() => reader.ReadPair(first, second));
            Assert.Equal(7, Assert.IsType<ProbeRoot>(pair.First).Value);
            Assert.Equal(8, Assert.IsType<ProbeRoot>(pair.Second).Value); // Delivered objects outlive the owner.
        } finally { reader.Dispose(); }
        AssertFilesEqual(files, SnapshotFiles());
    }

    private CheckpointAddress SeedProbePair() {
        using Repository seed = Repository.CreateMetaBootstrapNew(_path, ProbeModels(1, new()), schemaWriteMode: global::Atelia.DurableGraph.SchemaWriteMode.Shared);
        using BranchCheckout main = seed.CreateBranch("main", new ProbeRoot { Value = 7 });
        CheckpointAddress first = main.Head;
        seed.CreateTag("first", first);
        Assert.IsType<ProbeRoot>(main.State).Value = 8;
        CheckpointAddress second = main.CommitState();
        seed.CreateTag("second", second);
        Assert.Equal(seed.MetaBootstrapStore.ReadGraph(first.RevisionAddress).SchemaRoot,
            seed.MetaBootstrapStore.ReadGraph(second.RevisionAddress).SchemaRoot);
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
                bool extra = context.TryGetSchema(Extra.Type, Extra.Version, out _);
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
