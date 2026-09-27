using System.Diagnostics;
using System.Reflection;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using Atelia.RbfSegmentStore;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;

namespace Atelia.ListDeltaReplayProbe;

internal sealed record StepResult(string Workload, int Count, int Repeat, string Algorithm, int Step, string Edit,
    double CommitMs, long CommitAllocatedBytes, int BaseWrites, int DeltaWrites, long BasePayloadBytes, long DeltaPayloadBytes,
    string ListWriteKind, int ListPayloadBytes, DiffResult? Diff, string StateFingerprint);
internal sealed record DiffResult(int BaseBodyBytes, int DeltaBodyBytes, bool HasChanges,
    double MillisecondsMedian, double MillisecondsMin, double MillisecondsMax, long AllocatedBytesMedian,
    ProductDiffDiagnostics Diagnostics);
internal sealed record RunResult(string Workload, int Count, int Repeat, string Algorithm, string Directory,
    double SetupMs, long SetupAllocatedBytes, double ReopenAndLoadMs, long ReopenAndLoadAllocatedBytes,
    long StateFileBytes, long SchemaFileBytes, long JournalFileBytes, StepResult[] Steps);

internal static class Replay {
    private static readonly RbfSegmentStoreOptions StoreOptions = new() { NewStoreLayout = RbfSegmentStoreLayout.Flat };
    private sealed record Saved(FrameAddress Address, string Fingerprint, double Ms, long Allocated, string Edit);
    private delegate DiffResult DiffRunner(ObjectStateRecord prior, ObjectStateRecord current, ListDeltaAlgorithm algorithm, int repeats);

    internal static RunResult Run<T>(Workload<T> workload, int count, int repeat, ListDeltaAlgorithm algorithm,
        string directory, Edit[] script, Settings settings, int? operationLimit = null, Action<World<T>, Edit>? applyEdit = null) {
        World<T> world = workload.Seed(count);
        StateModelRegistry models = Models(algorithm);
        List<Saved> saved = [];
        ObjectId worldId;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        Repository repository = Repository.CreateNew(directory, models, StoreOptions);
        BranchCheckout? session = null;
        double setupMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        long setupAllocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        using (repository) {
            try {
                Save("Initial");
                worldId = session!.StateId!.Value;
                foreach (Edit edit in script.Take(operationLimit ?? script.Length)) {
                    if (applyEdit is null) { workload.Apply(world, edit); }
                    else { applyEdit(world, edit); }
                    Save($"{edit.Round}:{edit.Kind}");
                    if (!ReferenceEquals(world, session!.State) || !ReferenceEquals(world.Items, ((World<T>)session!.State!).Alias)) {
                        throw new InvalidOperationException("Commit replaced a domain instance.");
                    }
                }
            }
            finally { session?.Dispose(); }
            void Save(string edit) {
                // Domain edits, expected-state construction and checks stay outside Commit timing.
                string fingerprint = workload.Fingerprint(world);
                long allocated = GC.GetAllocatedBytesForCurrentThread();
                long start = Stopwatch.GetTimestamp();
                ReadAmplificationBaseBudgetParameters parameters = new(settings.ReadAmplification, settings.BaseBudgetPercent);
                if (session is null) {
                    session = repository.CreateBranch("main", world, parameters);
                } else {
                    // The marker carries no World reference; E and S overhead are both timed.
                    session.CommitEvent(new ReplayEvent(), parameters);
                    session.CommitState(parameters);
                }
                FrameAddress address = session.StateRevisionAddress!.Value;
                double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
                saved.Add(new(address, fingerprint, elapsed, bytes, edit));
            }
        }

        // Validation uses fresh file handles after the writer closes. No original algorithm is
        // supplied to readers; historical materialization is deliberately outside Commit timing.
        List<StepResult> steps = [];
        using (IRbfFile schemaFile = RbfFile.OpenReadOnlyExisting(Path.Combine(directory, "schemas.rbf")))
        using (SegmentStore segments = SegmentStore.OpenReadOnlyExisting(Path.Combine(directory, "state"), StoreOptions)) {
            SchemaStore schemas = new(schemaFile, readOnly: true);
            using StateRevisionStore states = new(segments);
            StateModelSnapshot snapshot = models.Snapshot(schemas);
            if (!snapshot.TryGetCurrentObjectBinding(typeof(List<T>), out ObjectBinding? binding)) {
                throw new InvalidOperationException("No current List binding.");
            }
            ListObjectBinding listBinding = (ListObjectBinding)binding!;
            DiffRunner measure = typeof(Replay).GetMethod(nameof(Measure), BindingFlags.Static | BindingFlags.NonPublic)!
                .MakeGenericMethod(listBinding.ElementBinding.StateType, listBinding.ElementBinding.StateOpsType).CreateDelegate<DiffRunner>();
            ObjectStateRecord? previous = null;
            ObjectId listId = default;
            for (int index = 0; index < saved.Count; index++) {
                Saved entry = saved[index];
                LoadedWorld<World<T>> loaded = LoadedWorld.Load<World<T>>(states, schemas, entry.Address, worldId, Models());
                if (workload.Fingerprint(loaded.World) != entry.Fingerprint) { throw new InvalidOperationException("Historical domain graph differs from edit script."); }
                DecodedRevision decoded = RevisionDecoder.Read(states, schemas, entry.Address, Readers());
                ObjectStateRecord current = decoded.Objects.Single(row => row.Kind == ObjectStateKind.List && row.Layout.Equals(listBinding.CurrentLayout));
                if (index != 0 && current.Id != listId) { throw new InvalidOperationException("List identity changed across resize/edit."); }
                listId = current.Id;
                StateRevision revision = states.Read(entry.Address);
                int bases = 0, deltas = 0, listPayload = 0;
                long baseBytes = 0, deltaBytes = 0;
                string listKind = "NoChange";
                foreach (ObjectVersionRecord row in revision.LocalObjects) {
                    ObjectVersionChainEntry actual = states.ReadObjectVersionChain(entry.Address, row.ObjectId).Records[^1];
                    if (row.Kind == ObjectVersionKind.Base) { bases++; baseBytes += actual.ObjectVersionPayloadBytes; }
                    else { deltas++; deltaBytes += actual.ObjectVersionPayloadBytes; }
                    if (row.ObjectId == listId.Value) { listKind = row.Kind.ToString(); listPayload = actual.ObjectVersionPayloadBytes; }
                }
                DiffResult? diff = previous is null ? null : measure(previous, current, algorithm, settings.DiffRepeats);
                if (entry.Edit.EndsWith(":Undo", StringComparison.Ordinal) || entry.Edit.EndsWith(":ChildOnly", StringComparison.Ordinal)) {
                    // An unchanged container may still receive a policy-motivated Base rewrite.
                    if (diff!.HasChanges || listKind == "Delta") { throw new InvalidOperationException("Undo or child-only edit changed container slots."); }
                }
                steps.Add(new(workload.Name, count, repeat, algorithm.ToString(), index, entry.Edit, entry.Ms, entry.Allocated,
                    bases, deltas, baseBytes, deltaBytes, listKind, listPayload, diff, entry.Fingerprint));
                previous = current;
            }
        }

        allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        started = Stopwatch.GetTimestamp();
        using Repository reopened = Repository.OpenExisting(directory, Models(), StoreOptions);
        using BranchCheckout restored = reopened.Checkout("main");
        double coldMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        long coldAllocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        if (workload.Fingerprint(((World<T>)restored.State!)) != saved[^1].Fingerprint) { throw new InvalidOperationException("Cold latest graph differs."); }
        return new(workload.Name, count, repeat, algorithm.ToString(), directory, setupMs, setupAllocated, coldMs, coldAllocated,
            DirectorySize(Path.Combine(directory, "state")), new FileInfo(Path.Combine(directory, "schemas.rbf")).Length,
            DirectorySize(Path.Combine(directory, "journal")), steps.ToArray());
    }

    private static DiffResult Measure<TState, TOps>(ObjectStateRecord priorRecord, ObjectStateRecord currentRecord,
        ListDeltaAlgorithm algorithm, int repeats) where TState : unmanaged where TOps : IStateOps<TState> {
        FrozenListState<TState> prior = priorRecord.GetListState<TState>();
        FrozenListState<TState> current = currentRecord.GetListState<TState>();
        ListLayout layout = currentRecord.Layout.List!;
        byte[] priorBefore = ListStateBody<TState, TOps>.PrepareBase(prior, layout).Body.ToArray();
        byte[] currentBefore = ListStateBody<TState, TOps>.PrepareBase(current, layout).Body.ToArray();
        // Generic reflection, Base preparation, one warmup and all correctness checks are excluded.
        _ = ListStateBody<TState, TOps>.PrepareDelta(prior, current, layout, algorithm);
        double[] times = new double[repeats];
        long[] allocations = new long[repeats];
        PreparedDeltaBody? delta = null;
        for (int index = 0; index < repeats; index++) {
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            delta = ListStateBody<TState, TOps>.PrepareDelta(prior, current, layout, algorithm);
            times[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            allocations[index] = GC.GetAllocatedBytesForCurrentThread() - allocated;
        }
        ProductDiffDiagnostics diagnostics = ProductDiagnostics.Observe<TState, TOps>(prior, current, layout, algorithm, delta!);
        BinaryPayloadReader reader = new(delta!.Body);
        FrozenListState<TState> applied = ListStateBody<TState, TOps>.ApplyDelta(ref reader, prior, layout);
        reader.EnsureFullyConsumed();
        bool equal = prior.Count == current.Count;
        if (equal) {
            for (int index = 0; index < prior.Count; index++) {
                if (!TOps.StateEquals(in prior.OwnedElements[index], in current.OwnedElements[index], layout.ElementSlot)) { equal = false; break; }
            }
        }
        if (equal == delta.HasChanges || !ListStateBody<TState, TOps>.PrepareBase(applied, layout).Body.SequenceEqual(currentBefore) ||
            !ListStateBody<TState, TOps>.PrepareBase(prior, layout).Body.SequenceEqual(priorBefore) ||
            !ListStateBody<TState, TOps>.PrepareBase(current, layout).Body.SequenceEqual(currentBefore)) {
            throw new InvalidOperationException("Candidate Delta roundtrip, exact equality or frozen-input ownership failed.");
        }
        Array.Sort(times); Array.Sort(allocations);
        return new(currentBefore.Length, delta.Body.Length, delta.HasChanges,
            Statistics.Median(times), times[0], times[^1], allocations[allocations.Length / 2], diagnostics);
    }

    private static StateModelRegistry Models(ListDeltaAlgorithm? algorithm = null) {
        StateModelRegistry models = new();
        Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
        if (algorithm is { } selected) { models.UseListDeltaAlgorithm(selected); }
        return models;
    }

    private static StateReaderRegistry Readers() {
        StateReaderRegistry readers = new();
        Atelia.DurableGraph.Generated.DurableDefinitions.Register(readers);
        return readers;
    }

    private static long DirectorySize(string directory) => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
}
