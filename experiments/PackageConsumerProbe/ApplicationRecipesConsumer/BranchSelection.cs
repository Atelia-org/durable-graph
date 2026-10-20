using Atelia.DurableGraph;

namespace ApplicationRecipes;

/// <summary>Application selection state; use serially, and let this object own its working copies.</summary>
/// <remarks>
/// The caller owns the Repository and keeps it open. Fork and open are deliberately separate:
/// an open failure does not undo creation. Publication exceptions from Fork still require the
/// caller to inspect outcome/owner health and reconcile after reopening, rather than blindly retry.
/// Persist PendingBranchId separately if this unfinished selection must survive an application exit.
/// </remarks>
public sealed class BranchSelection : IDisposable {
    private readonly Repository _repository;
    private bool _disposed;

    public BranchSelection(Repository repository, BranchId initialBranch) {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
        Current = repository.OpenWorkingCopy(initialBranch);
    }

    public WorkingCopy Current { get; private set; }
    public BranchId? PendingBranchId { get; private set; }

    /// <summary>Creates once and remembers the branch to open; uncommitted edits are not forked.</summary>
    public BranchInfo Fork(CommitId savedCommit, string displayLabel) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (PendingBranchId is not null) {
            throw new InvalidOperationException("A branch is already created. Retry OpenPending instead of forking again.");
        }
        BranchInfo created = _repository.ForkBranch(savedCommit, displayLabel);
        PendingBranchId = created.Id;
        return created;
    }

    /// <summary>Retries opening the same created branch, keeping Current until opening succeeds.</summary>
    public WorkingCopy OpenPending() {
        ObjectDisposedException.ThrowIf(_disposed, this);
        BranchId target = PendingBranchId
            ?? throw new InvalidOperationException("No created branch is waiting to be opened.");
        WorkingCopy next = _repository.OpenWorkingCopy(target);
        WorkingCopy previous = Current;
        Current = next;
        PendingBranchId = null;
        // The switch is complete before cleanup. Do not report a subsequent cleanup error
        // as a failed open or retry Fork. Under the serial-use contract this Dispose only
        // releases editing occupancy; it performs no storage I/O.
        previous.Dispose();
        return next;
    }

    public void Dispose() {
        if (_disposed) { return; }
        Current.Dispose();
        _disposed = true;
    }
}
