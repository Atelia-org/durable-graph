using Atelia.DurableGraph;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Persistence;

namespace Atelia.ListDeltaReplayProbe;

internal sealed record FallbackFixture(string Group, string Name, int Count, int Step,
    ObjectStateRecord Prior, ObjectStateRecord Current, StateValueBinding Element);

/// <summary>Capture each domain transition once; every contender receives the same frozen DTO pair.</summary>
internal static class FallbackFixtures {
    internal static IEnumerable<FallbackFixture> Create(Settings settings) {
        HashSet<(string Group, string Name, int Count, int Step)> keys = [];
        foreach (FallbackFixture fixture in Enumerate(settings)) {
            if (!keys.Add((fixture.Group, fixture.Name, fixture.Count, fixture.Step)) ||
                fixture.Prior.Id != fixture.Current.Id || !fixture.Prior.Layout.Equals(fixture.Current.Layout) ||
                !ReferenceEquals(fixture.Prior.Preparation, fixture.Current.Preparation)) {
                throw new InvalidOperationException("Fixture key, object identity, layout or binding changed unexpectedly.");
            }
            yield return fixture;
        }
    }

    private static IEnumerable<FallbackFixture> Enumerate(Settings settings) {
        Edit[] script = Scripts.Create(settings.Seed, settings.Rounds);
        foreach (FallbackFixture pair in Ordinary(Workloads.Integers, settings.Counts, script)) { yield return pair; }
        foreach (FallbackFixture pair in Ordinary(Workloads.Alternating, settings.Counts, script)) { yield return pair; }
        foreach (FallbackFixture pair in Ordinary(Workloads.References, settings.Counts, script)) { yield return pair; }
        foreach (FallbackFixture pair in Ordinary(Workloads.Small, settings.Counts, script)) { yield return pair; }
        int[] wideCounts = settings.Counts.Select(count => Math.Min(count, 512)).Distinct().Order().ToArray();
        foreach (FallbackFixture pair in Ordinary(Workloads.Large, wideCounts, script)) { yield return pair; }

        WhiteboxCase[] boundaries = [
            new("local-block32-tail-change", 32, true, false),
            new("local-block33-tail-change", 33, true, false),
            new("control-block33-intact-tail", 33, false, false),
            new("myers-balanced64", 64, false, true),
            new("myers-balanced65", 65, false, true),
        ];
        foreach (int count in settings.Counts.Select(count => Math.Max(512, count)).Distinct().Order()) {
            foreach (WhiteboxCase scenario in boundaries) {
                yield return Pair("whitebox", scenario.Name, Workloads.Integers, count,
                    world => scenario.Apply(world.Items));
            }

            // Beyond both current bounds: Local cannot see 129 ahead; Myers needs >128 edits.
            yield return Pair("falsification", "head129-tail-change", Workloads.Integers, count,
                world => new WhiteboxCase("head129", 129, true, false).Apply(world.Items));

            // Both failure families in one transition; no promise that switching once rescues it.
            yield return Pair("falsification", "head33-and-balanced65", Workloads.Integers, count, world => {
                new WhiteboxCase("balanced65", 65, false, true).Apply(world.Items);
                world.Items.InsertRange(0, Enumerable.Range(-2_000_033, 33));
            });

            // A successful early offset of 3 must not hide a later failed offset jump to 36.
            yield return Pair("falsification", "early3-later33-tail-change", Workloads.Integers, count, world => {
                world.Items.InsertRange(0, [-3, -2, -1]);
                world.Items.InsertRange(count / 2 + 3, Enumerable.Range(-2_000_033, 33));
                world.Items[^1] = -1_000_000;
            });

            // An unmatched block is genuine replacement at original positions, not a hidden shift.
            yield return Pair("falsification", "position-replacement-block-tail-change", Workloads.Integers, count, world => {
                for (int index = 0; index < count / 2; index++) { world.Items[index] = -index - 1; }
                world.Items[^1] = -1_000_000;
            });
        }

        // One tempting repeated-value anchor can produce a worse continuation than another tie.
        yield return Pair("falsification", "duplicate-false-anchor", Workloads.Integers, 4,
            world => Replace(world.Items, [1, 0, 0, 1]), world => Replace(world.Items, [0, 1, 0, 0]));

        foreach (int count in wideCounts) {
            // No equality matches, but each pair has a tiny generated inline Delta, not a full replacement.
            yield return Pair("falsification", "wide-all-tiny-field-changes", Workloads.Large, count, world => {
                for (int index = 0; index < world.Items.Count; index++) {
                    Wide<Cell<int>> value = world.Items[index];
                    value.Value.Value++;
                    world.Items[index] = value;
                }
            });
        }

        // Fixed sizes straddle rescue-budget boundaries; N=34 already exceeds Local's 32 window.
        // The sole exact common value is Marker. Following its LCS anchor emits N-1 wide literal
        // Bases; original-position pairs patch only the tiny nested value. Maximizing exact matches
        // need not minimize bytes, even when the alternative search successfully finishes.
        foreach (int count in new[] { 34, 48, 65 }) {
            yield return Pair("falsification", "wide-marker-byte-cost-trap", Workloads.Large, count, world => {
                Wide<Cell<int>> marker = world.Items[0];
                world.Items.RemoveAt(0);
                for (int index = 0; index < world.Items.Count; index++) {
                    Wide<Cell<int>> value = world.Items[index];
                    value.Value.Value++;
                    world.Items[index] = value;
                }
                world.Items.Add(marker);
                if (world.Items.Take(count - 1).Any(value => value.Value.Value % 17 != 1)) {
                    throw new InvalidOperationException("Marker trap created an unintended exact match.");
                }
            }, world => {
                Wide<Cell<int>> common = world.Items[0];
                common.TestId = 0;
                common.Value.TestId = 0;
                for (int index = 0; index < world.Items.Count; index++) {
                    Wide<Cell<int>> value = common;
                    value.Value.Value = index == 0 ? -1_000_000 : index * 17;
                    world.Items[index] = value;
                }
            });
        }
    }

    private static IEnumerable<FallbackFixture> Ordinary<T>(Workload<T> workload, int[] counts, Edit[] script) {
        CaptureBinding<T> binding = new();
        foreach (int count in counts) {
            World<T> world = workload.Seed(count);
            CaptureSession session = new();
            ObjectStateRecord previous = binding.Capture(session, world, workload);
            for (int index = 0; index < script.Length; index++) {
                Edit edit = script[index];
                workload.Apply(world, edit);
                ObjectStateRecord current = binding.Capture(session, world, workload);
                yield return new("ordinary", $"{workload.Name}/{edit.Round}:{edit.Kind}", count, index + 1,
                    previous, current, binding.Element);
                previous = current;
            }
        }
    }

    private static FallbackFixture Pair<T>(string group, string name, Workload<T> workload, int count,
        Action<World<T>> change, Action<World<T>>? initialize = null) {
        CaptureBinding<T> binding = new();
        CaptureSession session = new();
        World<T> world = workload.Seed(count);
        initialize?.Invoke(world);
        ObjectStateRecord prior = binding.Capture(session, world, workload);
        change(world);
        ObjectStateRecord current = binding.Capture(session, world, workload);
        return new(group, name, count, 1, prior, current, binding.Element);
    }

    private static void Replace<T>(List<T> target, IEnumerable<T> values) {
        target.Clear();
        target.AddRange(values);
    }

    private sealed class CaptureBinding<T> {
        private readonly ModelExecutionEnvironment _snapshot;
        private readonly StateModelBinding _model;
        private readonly ListObjectBinding _list;

        internal CaptureBinding() {
            StateModelRegistry registry = new();
#pragma warning disable CS0436 // Intentionally register this probe's local SG definitions, visible alongside Persistence's through IVT.
            Atelia.DurableGraph.Generated.DurableDefinitions.Register(registry);
#pragma warning restore CS0436
            _snapshot = registry.CreateEnvironment();
            if (!_snapshot.TryGetCurrentModel(typeof(World<T>), out StateModelBinding? model) || model is null ||
                !_snapshot.TryGetCurrentObjectBinding(typeof(List<T>), out ObjectBinding? list) || list is not ListObjectBinding listBinding) {
                throw new InvalidOperationException("Fixture requires generated World and List element bindings.");
            }
            _model = model;
            _list = listBinding;
        }

        internal StateValueBinding Element => _list.ElementBinding;

        internal ObjectStateRecord Capture(CaptureSession session, World<T> world, Workload<T> workload) {
            // Fingerprint also checks List aliases, repeated-reference pool identities and child cycles.
            string before = workload.Fingerprint(world);
            using CaptureContext context = session.BeginCapture(_snapshot);
            _model.AddRoot(context, world);
            CapturedGraph candidate = context.Seal();
            ObjectStateRecord list = candidate.Objects.Single(record =>
                record.Kind == ObjectStateKind.List && record.Layout.Equals(_list.CurrentLayout));
            session.Accept(candidate);
            if (before != workload.Fingerprint(world)) { throw new InvalidOperationException("Capture mutated the domain graph."); }
            return list;
        }
    }
}
