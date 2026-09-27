using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Storage;

namespace Atelia.DurableGraph.Persistence;

/// <summary>One private, single-use save candidate retained until publication is resolved.</summary>
internal sealed class PreparedWorldSave : IDisposable {
    private WorldWorkspace? _owner;
    private readonly CaptureContext _context;
    private readonly CapturedGraph _candidate;
    private readonly NormalizedRevision? _next;
    private readonly IDurableObject? _nextState;
    private NormalizedRevision? _installation;

    internal PreparedWorldSave(WorldWorkspace owner, CaptureContext context,
        CapturedGraph candidate, ObjectId rootId, StateRevision revision, NormalizedRevision? next, IDurableObject? nextState) {
        _owner = owner;
        _context = context;
        _candidate = candidate;
        RootId = rootId;
        Revision = revision;
        _next = next;
        _nextState = nextState;
    }

    internal ObjectId RootId { get; }
    internal bool IsIndependentSnapshot => _nextState is null;
    internal StateRevision Revision { get; }

    /// <summary>Completes storage accounting and allocation at the actual address before publishing the head.</summary>
    internal void PrepareInstall(FrameAddress address) {
        RequireOwner();
        if (IsIndependentSnapshot) {
            throw new InvalidOperationException("Independent snapshots cannot advance the State workspace; dispose after publication resolves.");
        }
        if (_installation is not null) {
            throw new InvalidOperationException("This save already has its installation prepared.");
        }
        if (address.FileNumber == 0 || address.FrameTicket.Length == 0) {
            throw new ArgumentException("Installation requires the appended Revision address.", nameof(address));
        }
        _installation = _next!.WithAddress(address, Revision);
    }

    /// <summary>Called only after confirmed publication; no field capture, callbacks or allocation.</summary>
    internal void Install() {
        WorldWorkspace owner = RequireOwner();
        NormalizedRevision installation = _installation
            ?? throw new InvalidOperationException("Prepare the installation before publishing and installing it.");
        owner.Install(this, _candidate, installation, _nextState!);
        _owner = null;
    }

    public void Dispose() {
        if (_owner is { } owner) {
            owner.Discard(this, _context);
            _owner = null;
        }
    }

    private WorldWorkspace RequireOwner() => _owner
        ?? throw new InvalidOperationException("The save candidate has already been resolved.");
}
