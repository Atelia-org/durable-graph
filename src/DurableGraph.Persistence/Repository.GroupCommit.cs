using Atelia.DurableGraph.Schema;
using System.Collections.ObjectModel;
using Atelia.DurableGraph.Persistence;

namespace Atelia.DurableGraph;

public sealed partial class Repository {
    internal CommitRequest CreateCommitRequest(WorkingCopy work, IDurableObject root, StoredGraphKind kind,
        ReadOnlyMemory<byte> summary, ReadAmplificationBaseBudgetParameters? parameters) {
        RequireWriter();
        _busy = true;
        try {
            RequireCheckoutSource(work);
            ArgumentNullException.ThrowIfNull(root);
            return new(work, root, kind, summary, parameters);
        } finally { EndOperation(); }
    }

    /// <summary>Atomically publishes one State or Event for each requested existing branch in this repository.</summary>
    /// <remarks>
    /// Requests bind their original live working copies and exact baselines. The collection must be nonempty
    /// and contain each branch at most once. All graphs are captured during this call using a common Schema
    /// catalog candidate; keep them stable throughout the call. Normal return contains every requested branch.
    /// Complete unpublished frames may remain on failure. Unknown or Published failures must not be retried
    /// on the old owner; inspect RepositoryPublicationException and reopen a faulted repository. No domain
    /// mutation or external effect is rolled back. Single-branch immediate commits use this same kernel.
    /// </remarks>
    public IReadOnlyDictionary<BranchId, CommitInfo> CommitBranches(IEnumerable<CommitRequest> requests) =>
        CommitBranches(requests, []);

    // Explicit declarations are operation-local mechanism inputs, never a per-working-copy dirty catalog.
    internal IReadOnlyDictionary<BranchId, CommitInfo> CommitBranches(IEnumerable<CommitRequest> requests,
        IEnumerable<DurableSchema> declarations) {
        RequireWriter();
        _busy = true;
        CommitRequest[] inputs = [];
        BusinessSaveCandidate?[] captures = [];
        PreparedBusinessWrite[] writes = [];
        PreparedSchemaCatalogWrite? catalog = null;
        PendingRepositorySave? pending = null;
        bool resolved = false;
        void AbortAll() {
            if (resolved) { return; }
            resolved = true;
            List<Exception>? errors = null;
            foreach (BusinessSaveCandidate? capture in captures) {
                try { capture?.AbortPreparation(); }
                catch (Exception error) { (errors ??= []).Add(error); }
            }
            if (errors is not null) { throw new AggregateException("Candidate cleanup failed.", errors); }
        }
        try {
            Dictionary<BranchId, CommitInfo>? results = null;
            IReadOnlyDictionary<BranchId, CommitInfo>? view = null;
            pending = _storageFacade.PrepareGroup(() => {
                ArgumentNullException.ThrowIfNull(requests);
                ArgumentNullException.ThrowIfNull(declarations);
                inputs = _storageFacade.MaterializeInputs(requests);
                DurableSchema[] explicitSchemas = _storageFacade.MaterializeInputs(declarations);
                if (inputs.Length == 0) { throw new ArgumentException("A commit group must not be empty.", nameof(requests)); }
                HashSet<BranchId> branches = [];
                foreach (CommitRequest request in inputs) {
                    ArgumentNullException.ThrowIfNull(request);
                    if (!branches.Add(request.BranchId)) { throw new ArgumentException("A branch may occur only once in a commit group.", nameof(requests)); }
                    RequireCheckoutSource(request.Work);
                    if (request.Work.BaseCommitId != request.ExpectedBaseCommitId) {
                        throw new InvalidOperationException("The commit request's original working-copy baseline has changed.");
                    }
                    if (request.Kind == StoredGraphKind.State) {
                        ReadAmplificationBaseBudgetPolicy.ValidateParameters(request.Parameters ?? DefaultPolicy);
                    }
                    BusinessEntryPayloadCodec.ValidateSummaryLength(request.Summary.Length);
                }
                _storageFacade.EnsureSaveUsable();
                captures = new BusinessSaveCandidate?[inputs.Length];
                writes = new PreparedBusinessWrite[inputs.Length];
                var members = new PendingBusinessSave[inputs.Length];
                results = new(inputs.Length);
                view = new ReadOnlyDictionary<BranchId, CommitInfo>(results);
                for (int i = 0; i < inputs.Length; i++) {
                    CommitRequest request = inputs[i];
                    captures[i] = request.Work.SaveSession.CaptureOwned(request.Root, request.Kind);
                    _storageFacade.EnsureSaveUsable();
                }
                catalog = _storageFacade.CatalogForPreparation.PrepareOwned(
                    captures.SelectMany(static capture => capture!.Graph.Objects.Select(static row => row.Layout)), explicitSchemas);
                _storageFacade.EnsureSaveUsable();
                for (int i = 0; i < inputs.Length; i++) {
                    CommitRequest request = inputs[i];
                    writes[i] = request.Work.SaveSession.PrepareOwned(captures[i]!, catalog.View, request.Parameters ?? DefaultPolicy);
                    _storageFacade.EnsureSaveUsable();
                    members[i] = new(request.BranchId.Value, request.ExpectedBaseCommitId.Value, null, request.Kind,
                        request.Summary, writes[i].Write);
                }
                return new(members, catalog.Write);
            }, AbortAll);
            OperationStageObserver?.Invoke(RepositoryOperationStage.AfterPrepare);
            _storageFacade.EnsureSaveUsable();
            PreparedSchemaCatalogWrite commonCatalog = catalog!;
            _storageFacade.CommitGroup(pending, commonCatalog.Install, commonCatalog.PrepareInstall,
                (index, graph) => writes[index].PrepareInstall(graph),
                (index, graph) => writes[index].Install(graph), delivered => {
                    // Construct all descriptions before advancing any working-copy history position.
                    for (int i = 0; i < inputs.Length; i++) {
                        results!.Add(inputs[i].BranchId, Issue(_resources.ReadDescriptor(delivered[i].CommitId)));
                    }
                    for (int i = 0; i < inputs.Length; i++) {
                        WorkingCopy work = inputs[i].Work;
                        CommitInfo info = results![work.BranchId];
                        work.SaveSession.Advance(delivered[i].CommitId);
                        work.BaseCommitId = info.Id;
                        if (inputs[i].Kind == StoredGraphKind.State) { work.StateSourceCommitId = info.Id; }
                    }
                }, AbortAll);
            return view!;
        } catch (Exception primary) {
            try { AbortAll(); }
            catch (Exception cleanup) {
                _resources.MarkFaulted();
                throw new AggregateException("Save operation and candidate cleanup failed.", primary, cleanup);
            }
            throw;
        } finally {
            try { if (pending?.IsPrepared == true) { pending.Dispose(); } }
            finally { EndOperation(); }
        }
    }

}
