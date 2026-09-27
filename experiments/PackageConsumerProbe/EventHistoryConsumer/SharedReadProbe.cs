using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Storage;
using Atelia.RbfSegmentStore;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;

namespace EventHistoryPackageConsumerProbe;

internal static class SharedReadProbe {
    private static readonly RbfSegmentStoreOptions Options = new() { NewStoreLayout = RbfSegmentStoreLayout.Flat };
    private static readonly ReadAmplificationBaseBudgetParameters Policy = new(int.MaxValue, 1);

    private static StateModelRegistry Models() {
        StateModelRegistry models = new();
        Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
        return models;
    }

    internal static void Seed(string directory) {
        string stableText = new("stable".ToCharArray());
        SharedNode stable = new() { Value = 1, Text = stableText };
        SharedNode stableNext = new() { Value = 2, Text = stableText, Next = stable };
        stable.Next = stableNext;
        SharedNode changing = new() { Value = 10, Text = "changing" };
        changing.Next = changing;
        SharedRoot root = new() {
            Stable = stable,
            Changing = changing,
            StableArray = [stable, stableNext],
            StableList = [stable, stableNext],
            StableMap = new() { ["first"] = stable, ["second"] = stableNext },
            StableInline = new() { Target = stable },
            StableOptional = new SharedLinks { Target = stableNext },
            ChangingArray = [changing],
            ChangingList = [changing],
            ChangingMap = new() { ["child"] = changing },
            ChangingInline = new() { Target = changing },
            ChangingOptional = new SharedLinks { Target = changing },
            EqualOne = new string("equal".ToCharArray()),
            EqualTwo = new string("equal".ToCharArray()),
            ChangingKeyMap = new(ReferenceEqualityComparer.Instance) { [changing] = stable },
            InlineOwner = new() { Value = new SharedLinks { Target = changing } },
            OptionalOwner = new() { Value = new SharedLinks { Target = changing } },
        };
        using var repository = Repository.CreateNew(directory, Models(), Options);
        using var session = repository.CreateBranch("main", root, Policy);
        session.CommitEvent(new SharedEvent { View = root }, Policy);
        changing.Value = 20;
        session.CommitState(Policy);
        changing.Value = 30;
        session.CommitEvent(new SharedEvent { View = root }, Policy);
    }

    internal static void Verify(string directory) {
        string[] before = SnapshotFiles(directory);
        List<string> metrics = [];
        using (var repository = Repository.OpenReadOnlyExisting(directory, Models(), Options)) {
            CheckpointAddress[] frames = repository.ReadFrames("main").ToArray();
            Require(frames.Length == 4, "Shared-read fixture must contain S0 E1 S1 E2.");
            CheckActualHeads(directory, frames);

            var independent = Measure(() => (
                ((SharedRoot)repository.ReadState(frames[0])),
                ((SharedEvent)repository.ReadEvent(frames[1]))), out var independentObservation);
            CheckRoot(independent.First, 10);
            CheckRoot(independent.Second.View, 10);
            AddMetrics(metrics, "IndependentS0E1", independentObservation);

            var reused = Measure(() => { var pair = repository.ReadPair(frames[0], frames[1]); return ((SharedRoot)pair.First, (SharedEvent)pair.Second); },
                out var reusedObservation);
            CheckRoot(reused.First, 10);
            CheckRoot(reused.Second.View, 10);
            AddMetrics(metrics, "PairS0E1", reusedObservation);
            metrics.Add($"PairS0E1.RootShared={ReferenceEquals(reused.First, reused.Second.View)}");
            metrics.Add($"PairS0E1.StableCycleShared={ReferenceEquals(reused.First.Stable, reused.Second.View.Stable)}");
            metrics.Add($"PairS0E1.StringShared={ReferenceEquals(reused.First.EqualOne, reused.Second.View.EqualOne)}");

            var independentChanged = Measure(() => (
                ((SharedRoot)repository.ReadState(frames[0])),
                ((SharedRoot)repository.ReadState(frames[2]))), out var independentChangedObservation);
            CheckRoot(independentChanged.First, 10);
            CheckRoot(independentChanged.Second, 20);
            AddMetrics(metrics, "IndependentS0S1", independentChangedObservation);
            // Independent browsing needs no writer to initialize view-local Transient state.
            independentChanged.First.Stable.OwnerWorld = independentChanged.First;
            independentChanged.Second.Stable.OwnerWorld = independentChanged.Second;
            Require(independentChanged.First.Stable.OwnerWorld.Changing.Value == 10 &&
                independentChanged.Second.Stable.OwnerWorld.Changing.Value == 20,
                "Independent Transient initialization leaked between historical views.");

            var changed = Measure(() => { var pair = repository.ReadPair(frames[0], frames[2]); return ((SharedRoot)pair.First, (SharedRoot)pair.Second); },
                out var changedObservation);
            CheckRoot(changed.First, 10);
            CheckRoot(changed.Second, 20);
            CheckExternalViews(changed.First, changed.Second);
            AddMetrics(metrics, "PairS0S1", changedObservation);
            metrics.Add($"PairS0S1.RootShared={ReferenceEquals(changed.First, changed.Second)}");
            metrics.Add($"PairS0S1.StableCycleShared={ReferenceEquals(changed.First.Stable, changed.Second.Stable)}");
            metrics.Add($"PairS0S1.StableArrayShared={ReferenceEquals(changed.First.StableArray, changed.Second.StableArray)}");
            metrics.Add($"PairS0S1.StableListShared={ReferenceEquals(changed.First.StableList, changed.Second.StableList)}");
            metrics.Add($"PairS0S1.StableDictionaryShared={ReferenceEquals(changed.First.StableMap, changed.Second.StableMap)}");

            var identical = repository.ReadPair(frames[0], frames[0]);
            CheckRoot((SharedRoot)identical.First, 10);
            CheckRoot((SharedRoot)identical.Second, 10);

            // No type arguments or State/Event ordering assumption at the call site.
            int[] values = [10, 10, 20, 30];
            foreach (var (first, second) in new[] { (0, 1), (1, 0), (1, 3), (0, 2) }) {
                var pair = repository.ReadPair(frames[first], frames[second]);
                CheckSelection(frames[first], pair.First, values[first]);
                CheckSelection(frames[second], pair.Second, values[second]);
            }
        }
        Require(before.SequenceEqual(SnapshotFiles(directory)), "Readonly shared-read operations changed repository files.");

        using (var repository = Repository.OpenExisting(directory, Models(), Options)) {
            using var session = repository.Checkout("main");
            SharedEvent pending = (SharedEvent)repository.ReadEvent(session.Head);
            CheckRoot(((SharedRoot)session.State!), 20);
            CheckRoot(pending.View, 30);
            // Checkout plus independent ReadEvent must preserve editable isolation, unlike readonly ReadPair sharing.
            ((SharedRoot)session.State!).Stable.Value = 91;
            ((SharedRoot)session.State!).Changing.Value = 99;
            ((SharedRoot)session.State!).StableArray[0] = new SharedNode { Value = 100 };
            ((SharedRoot)session.State!).StableList.Clear();
            ((SharedRoot)session.State!).StableMap.Clear();
            ((SharedRoot)session.State!).ChangingArray[0] = new SharedNode { Value = 101 };
            ((SharedRoot)session.State!).ChangingList.Clear();
            ((SharedRoot)session.State!).ChangingMap.Clear();
            ((SharedRoot)session.State!).ChangingKeyMap.Clear();
            CheckRoot(pending.View, 30);
        }

        // All observations come from one synchronous read. They include binding/JIT effects,
        // measure managed allocation (not peak memory), and impose no performance threshold.
        File.WriteAllLines(Path.Combine(directory, "shared-read-metrics.txt"), metrics);
    }

    private static void CheckActualHeads(string directory, CheckpointAddress[] frames) {
        using SegmentStore segments = SegmentStore.OpenReadOnlyExisting(Path.Combine(directory, "state"), Options);
        using StateRevisionStore store = new(segments);
        var s0 = store.ReadLiveObjectHeadMap(frames[0].RevisionAddress);
        var e1 = store.ReadLiveObjectHeadMap(frames[1].RevisionAddress);
        var s1 = store.ReadLiveObjectHeadMap(frames[2].RevisionAddress);
        Require(s0.All(row => e1.TryGetValue(row.Key, out var head) && head == row.Value),
            "E1 must reuse S0's real ObjectVersion heads, not equal rewritten copies.");
        Require(frames[0].RootId == frames[2].RootId && s0[frames[0].RootId.Value] == s1[frames[2].RootId.Value],
            "The changed-child fixture must retain the owner's exact head.");
        Require(s0.Count == s1.Count && s0.Count(row => s1[row.Key] != row.Value) == 1,
            "S1 must change exactly the child's head, leaving arrays, lists, dictionaries and owners unchanged.");
    }

    private static void CheckExternalViews(SharedRoot earlierWorld, SharedRoot laterWorld) {
        // SharedRoot is the World; its stable SharedNodes play the role of unchanged actors.
        // Each view owns its index/context even if its actors happen to be shared by ReadPair.
        WorldView earlier = new(earlierWorld);
        WorldView later = new(laterWorld);
        Require(earlier.ObserveActor(1) == new ActorObservation(10, 1) &&
            later.ObserveActor(1) == new ActorObservation(20, 1) &&
            earlier.ObserveActor(2) == new ActorObservation(10, 2) &&
            later.ObserveActor(2) == new ActorObservation(20, 2),
            "An external actor index used another historical view's world context.");
        Require(earlierWorld.Stable.OwnerWorld is null && laterWorld.Stable.OwnerWorld is null,
            "ReadPair browsing must not initialize Transient owner fields on graph nodes.");
        // No cross-graph ReferenceEquals assertion: query correctness must not depend on sharing.
    }

    private sealed class WorldView {
        public SharedRoot Snapshot { get; }
        public IReadOnlyDictionary<int, SharedNode> ActorsById { get; }
        public int QueryContext { get; }

        public WorldView(SharedRoot snapshot) {
            Snapshot = snapshot;
            ActorsById = new Dictionary<int, SharedNode> {
                [snapshot.Stable.Value] = snapshot.Stable,
                [snapshot.Stable.Next!.Value] = snapshot.Stable.Next,
            };
            QueryContext = snapshot.Changing.Value;
        }

        public ActorObservation ObserveActor(int actorId) => new(QueryContext, ActorsById[actorId].Value);
    }

    private readonly record struct ActorObservation(int WorldValue, int ActorValue);

    private static void CheckSelection(CheckpointAddress frame, IDurableObject root, int expectedValue) {
        switch (root) {
            case SharedRoot state when frame.Kind == GraphFrameKind.State:
                CheckRoot(state, expectedValue);
                break;
            case SharedEvent domainEvent when frame.Kind == GraphFrameKind.Event:
                CheckRoot(domainEvent.View, expectedValue);
                break;
            default:
                throw new InvalidOperationException("Non-generic pair lost the selected root's actual type or input position.");
        }
    }

    private static void CheckRoot(SharedRoot root, int expectedChangingValue) {
        Require(root.Stable.Value == 1 && root.Stable.Next!.Value == 2 &&
            ReferenceEquals(root.Stable, root.Stable.Next.Next), "Stable cycle was not restored.");
        Require(root.Changing.Value == expectedChangingValue && ReferenceEquals(root.Changing, root.Changing.Next),
            "Changed child lost its selected Revision value or self-cycle.");
        Require(root.StableArray.Length == 2 && root.StableList.Count == 2 && root.StableMap.Count == 2 &&
            ReferenceEquals(root.StableArray[0], root.Stable) && ReferenceEquals(root.StableArray[1], root.Stable.Next) &&
            ReferenceEquals(root.StableList[0], root.Stable) && ReferenceEquals(root.StableList[1], root.Stable.Next) &&
            ReferenceEquals(root.StableMap["first"], root.Stable) && ReferenceEquals(root.StableMap["second"], root.Stable.Next) &&
            ReferenceEquals(root.StableInline.Target, root.Stable) && ReferenceEquals(root.StableOptional!.Value.Target, root.Stable.Next),
            "Stable container or inline/Nullable reference was not restored.");
        Require(root.ChangingArray.Length == 1 && root.ChangingList.Count == 1 && root.ChangingMap.Count == 1 &&
            ReferenceEquals(root.ChangingArray[0], root.Changing) && ReferenceEquals(root.ChangingList[0], root.Changing) &&
            ReferenceEquals(root.ChangingMap["child"], root.Changing) &&
            ReferenceEquals(root.ChangingInline.Target, root.Changing) && ReferenceEquals(root.ChangingOptional!.Value.Target, root.Changing),
            "Container or inline/Nullable reference resolved against the wrong view.");
        Require(ReferenceEquals(root.Stable.Text, root.Stable.Next!.Text) &&
            root.EqualOne == "equal" && root.EqualTwo == "equal" && !ReferenceEquals(root.EqualOne, root.EqualTwo),
            "Graph-local nonempty string identity was not preserved.");
        Require(root.ChangingKeyMap.Count == 1 && ReferenceEquals(root.ChangingKeyMap.Single().Key, root.Changing) &&
            root.ChangingKeyMap.TryGetValue(root.Changing, out SharedNode? keyMapValue) && ReferenceEquals(keyMapValue, root.Stable),
            "Reference-identity dictionary key resolved against the wrong view.");
        Require(ReferenceEquals(root.InlineOwner.Value.Target, root.Changing) &&
            ReferenceEquals(root.OptionalOwner.Value!.Value.Target, root.Changing),
            "An owner with only inline/Nullable references resolved against the wrong view.");
    }

    private static (TFirst First, TSecond Second) Measure<TFirst, TSecond>(Func<(TFirst First, TSecond Second)> read,
        out Observation observation) where TFirst : class, IDurableObject where TSecond : class, IDurableObject {
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        var pair = read();
        double elapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        observation = new(CountRetained(pair.First, pair.Second), allocatedBytes, elapsedMilliseconds);
        return pair;
    }

    private static int CountRetained(object first, object second) {
        HashSet<object> seen = new(ReferenceEqualityComparer.Instance);
        Visit(first);
        Visit(second);
        return seen.Count;

        void Visit(object? value) {
            if (value is null || !seen.Add(value)) return;
            switch (value) {
                case SharedRoot root:
                    Visit(root.Stable);
                    Visit(root.Changing);
                    Visit(root.StableArray);
                    Visit(root.StableList);
                    Visit(root.StableMap);
                    Visit(root.StableInline.Target);
                    Visit(root.StableOptional?.Target);
                    Visit(root.ChangingArray);
                    Visit(root.ChangingList);
                    Visit(root.ChangingMap);
                    Visit(root.ChangingInline.Target);
                    Visit(root.ChangingOptional?.Target);
                    Visit(root.EqualOne);
                    Visit(root.EqualTwo);
                    Visit(root.ChangingKeyMap);
                    Visit(root.InlineOwner);
                    Visit(root.OptionalOwner);
                    break;
                case SharedEvent e:
                    Visit(e.View);
                    break;
                case SharedNode node:
                    Visit(node.Next);
                    Visit(node.Text);
                    break;
                case SharedHolder<SharedLinks> inline:
                    Visit(inline.Value.Target);
                    break;
                case SharedHolder<SharedLinks?> optional:
                    Visit(optional.Value?.Target);
                    break;
                case SharedNode[] array:
                    foreach (SharedNode element in array) Visit(element);
                    break;
                case List<SharedNode> list:
                    foreach (SharedNode element in list) Visit(element);
                    break;
                case Dictionary<string, SharedNode> map:
                    foreach (var entry in map) { Visit(entry.Key); Visit(entry.Value); }
                    break;
                case Dictionary<SharedNode, SharedNode> identityMap:
                    foreach (var entry in identityMap) { Visit(entry.Key); Visit(entry.Value); }
                    break;
                case string:
                    break;
                default:
                    throw new InvalidOperationException($"Uncounted fixture type {value.GetType()}.");
            }
        }
    }

    private static void AddMetrics(List<string> metrics, string prefix, Observation observation) {
        metrics.Add($"{prefix}.RetainedInstances={observation.RetainedInstances}");
        metrics.Add($"{prefix}.AllocatedBytes={observation.AllocatedBytes}");
        metrics.Add($"{prefix}.ElapsedMilliseconds={observation.ElapsedMilliseconds.ToString("F3", CultureInfo.InvariantCulture)}");
    }

    private static string[] SnapshotFiles(string directory) => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path => path + ":" + File.GetLastWriteTimeUtc(path).Ticks + ":" +
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();

    private static void Require(bool condition, string message) {
        if (!condition) throw new InvalidOperationException(message);
    }

    private readonly record struct Observation(int RetainedInstances, long AllocatedBytes, double ElapsedMilliseconds);
}
