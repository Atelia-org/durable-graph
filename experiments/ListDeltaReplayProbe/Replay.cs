using Atelia.Binary;
using System.Diagnostics;
using System.Reflection;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Persistence.MetaBootstrapP0;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

namespace Atelia.ListDeltaReplayProbe;

internal sealed record StepResult(string Workload, int Count, int Repeat, string Algorithm, int Step, string Edit,
    double CommitMs, long CommitAllocatedBytes, int BaseWrites, int DeltaWrites, long BasePayloadBytes, long DeltaPayloadBytes,
    string ListWriteKind, int ListPayloadBytes, DiffResult? Diff, string StateFingerprint);
internal sealed record DiffResult(int BaseBodyBytes, int DeltaBodyBytes, bool HasChanges,
    double MillisecondsMedian, double MillisecondsMin, double MillisecondsMax, long AllocatedBytesMedian,
    ProductDiffDiagnostics Diagnostics);
internal sealed record RunResult(string Workload, int Count, int Repeat, string Algorithm, string Directory,
    double SetupMs, long SetupAllocatedBytes, double ReopenAndLoadMs, long ReopenAndLoadAllocatedBytes,
    long DataFileBytes, long ControlFileBytes, StepResult[] Steps);

internal static class Replay {
    private const long StoreOptions = 1024 * 1024;
    private sealed record Saved(FrameAddress Address, ObjectId RootId, IReadOnlyDictionary<ObjectId, ObjectStorageInfo> Storage,
        string Fingerprint, double Ms, long Allocated, string Edit);
    private delegate DiffResult DiffRunner(ObjectStateRecord prior, ObjectStateRecord current, ListDeltaAlgorithm algorithm, int repeats);

    internal static RunResult Run<T>(Workload<T> workload, int count, int repeat, ListDeltaAlgorithm algorithm,
        string directory, Edit[] script, Settings settings, int? operationLimit = null, Action<World<T>, Edit>? applyEdit = null) {
        World<T> world = workload.Seed(count);
        StateModelRegistry models = Models(algorithm);
        List<Saved> saved = [];
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        Repository repository = Repository.CreateNew(directory, models, StoreOptions, schemaWriteMode: SchemaWriteMode.Shared);
        BranchCheckout? session = null;
        double setupMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        long setupAllocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        using (repository) {
            try {
                Save("Initial");
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
                double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
                // Keep installed identity/head/H diagnostics outside both save measurements.
                FrameAddress address = session.StateRevisionAddress!.Value;
                ObjectId rootId = session.StateId!.Value;
                if (session.Head.Kind != GraphFrameKind.State || session.Head.RevisionAddress != address || session.Head.RootId != rootId) {
                    throw new InvalidOperationException("Committed State and checkout baseline differ.");
                }
                Dictionary<ObjectId, ObjectStorageInfo> storage = session.Baseline!.Objects.ToDictionary(
                    pair => pair.Key, pair => pair.Value.Storage ?? throw new InvalidOperationException("A saved object lacks head/H."));
                saved.Add(new(address, rootId, storage, fingerprint, elapsed, bytes, edit));
            }
        }

        // Validation uses fresh file handles after the writer closes. No original algorithm is
        // supplied to readers; historical materialization is deliberately outside Commit timing.
        List<StepResult> steps = [];
        using (Repository reader = Repository.OpenReadOnlyExisting(directory, Models())) {
            GraphRecordStore store = reader.GraphStore;
            // Reissue handles from the reopened owner's published history; Saved addresses are diagnostics.
            IReadOnlyList<CheckpointAddress> history = reader.ReadFrames("main");
            if (history.Count != saved.Count * 2 - 1) { throw new InvalidOperationException("Published E/S history length differs from the trace."); }
            DiffRunner? measure = null;
            ObjectStateRecord? previous = null;
            ObjectId listId = default;
            for (int index = 0; index < saved.Count; index++) {
                Saved entry = saved[index];
                CheckpointAddress checkpoint = history[index * 2];
                if (checkpoint.Kind != GraphFrameKind.State || checkpoint.RevisionAddress != entry.Address ||
                    checkpoint.RootId != entry.RootId || entry.RootId != saved[0].RootId) {
                    throw new InvalidOperationException("Published State address or root identity differs from the saved trace.");
                }
                if (index != 0) {
                    CheckpointAddress marker = history[index * 2 - 1];
                    if (marker.Kind != GraphFrameKind.Event || reader.ReadEvent(marker) is not ReplayEvent) {
                        throw new InvalidOperationException("An edit's independent marker Event is missing.");
                    }
                }
                World<T> loaded = (World<T>)reader.ReadState(checkpoint);
                if (workload.Fingerprint(loaded) != entry.Fingerprint) { throw new InvalidOperationException("Historical domain graph differs from edit script."); }
                // Direct Diff consumes stored-exact states decoded against this graph's actual selected S.
                DecodedRevision decoded = DecodedRevision.ReadStoredGraph(store, checkpoint.RevisionAddress, Models());
                if (!decoded.SourceModels!.TryGetCurrentObjectBinding(typeof(List<T>), out ObjectBinding? binding)) {
                    throw new InvalidOperationException("No current List binding.");
                }
                ListObjectBinding listBinding = (ListObjectBinding)binding!;
                measure ??= typeof(Replay).GetMethod(nameof(Measure), BindingFlags.Static | BindingFlags.NonPublic)!
                    .MakeGenericMethod(listBinding.ElementBinding.StateType, listBinding.ElementBinding.StateOpsType).CreateDelegate<DiffRunner>();
                ObjectStateRecord current = decoded.Objects.Single(row => row.Kind == ObjectStateKind.List && row.Layout.Equals(listBinding.CurrentLayout));
                if (index != 0 && current.Id != listId) { throw new InvalidOperationException("List identity changed across resize/edit."); }
                listId = current.Id;
                StoredGraphRevision graph = store.ReadGraph(entry.Address);
                FrameAddress? expectedParent = index == 0 ? null : history[index * 2 - 1].RevisionAddress;
                FrameAddress? expectedSource = index == 0 ? null : saved[index - 1].Address;
                if (graph.Kind != StoredGraphKind.State || graph.RootId != entry.RootId ||
                    graph.Parent != expectedParent || graph.SourceGraph != expectedSource ||
                    !graph.ObjectHeads.Keys.Order().SequenceEqual(entry.Storage.Keys.Select(id => id.Value).Order()) ||
                    !decoded.Objects.Select(row => row.Id).SequenceEqual(entry.Storage.Keys.Order())) {
                    throw new InvalidOperationException("Stored State roots, parents or complete live IDs differ from the installed baseline.");
                }
                foreach ((ObjectId id, ObjectStorageInfo expected) in entry.Storage) {
                    ObjectVersionChain chain = store.ReadObjectChain(entry.Address, id.Value);
                    if (graph.ObjectHeads[id.Value] != expected.Head || decoded.ObjectHeads[id] != expected.Head ||
                        decoded.ObjectStorage[id] != expected || chain.Head != expected.Head ||
                        chain.ReconstructionPayloadBytes != expected.ReconstructionPayloadBytes) {
                        throw new InvalidOperationException("Stored object head/H differs from the installed baseline.");
                    }
                }
                int bases = 0, deltas = 0, listPayload = 0;
                long baseBytes = 0, deltaBytes = 0;
                string listKind = "NoChange";
                IReadOnlyList<ObjectVersionRecord> records = graph.OwnedBatch is { } batch ? store.ReadBatch(batch).Records : [];
                foreach (ObjectVersionRecord row in records) {
                    ObjectVersionChainEntry actual = store.ReadObjectChain(entry.Address, row.ObjectId).Entries[^1];
                    if (actual.ContainingBatch != graph.OwnedBatch || actual.Record.Kind != row.Kind || actual.Record.PriorAddress != row.PriorAddress) {
                        throw new InvalidOperationException("State payload accounting selected a different object version.");
                    }
                    if (row.Kind == ObjectVersionKind.Base) { bases++; baseBytes += actual.PayloadBytes; }
                    else { deltas++; deltaBytes += actual.PayloadBytes; }
                    if (row.ObjectId == listId.Value) { listKind = row.Kind.ToString(); listPayload = actual.PayloadBytes; }
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
        using Repository reopened = Repository.OpenExisting(directory, Models(), StoreOptions, schemaWriteMode: SchemaWriteMode.Shared);
        using BranchCheckout restored = reopened.Checkout("main");
        double coldMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        long coldAllocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        if (workload.Fingerprint(((World<T>)restored.State!)) != saved[^1].Fingerprint) { throw new InvalidOperationException("Cold latest graph differs."); }
        Saved latest = saved[^1];
        if (restored.StateId != latest.RootId || restored.StateRevisionAddress != latest.Address ||
            restored.Baseline!.Objects.Count != latest.Storage.Count || restored.Baseline.Objects.Any(pair =>
                !latest.Storage.TryGetValue(pair.Key, out ObjectStorageInfo expected) || pair.Value.Storage != expected)) {
            throw new InvalidOperationException("Cold latest root, live IDs or head/H differ from the saved baseline.");
        }
        return new(workload.Name, count, repeat, algorithm.ToString(), directory, setupMs, setupAllocated, coldMs, coldAllocated,
            DirectorySize(Path.Combine(directory, "data")),
            DirectorySize(Path.Combine(directory, "versions")), steps.ToArray());
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
        BareValueReader reader = new(delta!.Body);
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
#pragma warning disable CS0436 // Intentionally register this probe's local SG definitions, visible alongside Persistence's through IVT.
        Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
#pragma warning restore CS0436
        if (algorithm is { } selected) { models.UseListDeltaAlgorithm(selected); }
        return models;
    }

    private static long DirectorySize(string directory) => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
}
