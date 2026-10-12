using Atelia.FrameStore;
using Atelia.DurableGraph.Storage;

namespace Atelia.DurableGraph.Persistence;

/// <summary>One private, single-use save candidate retained until publication is resolved.</summary>
internal sealed class PreparedWorldSave : IDisposable {
    private readonly BusinessSaveCandidate _candidate;

    internal PreparedWorldSave(BusinessSaveCandidate candidate, StateRevision revision) {
        _candidate = candidate;
        Revision = revision;
    }

    internal ObjectId RootId => _candidate.RootId;
    internal bool IsIndependentSnapshot => _candidate.IsIndependentSnapshot;
    internal StateRevision Revision { get; }

    /// <summary>Completes storage accounting and allocation at the actual address before publishing the head.</summary>
    internal void PrepareInstall(FrameAddress address) {
        _candidate.RequireReady();
        if (IsIndependentSnapshot) {
            throw new InvalidOperationException("Independent snapshots cannot advance the State workspace; dispose after publication resolves.");
        }
        if (address == default) {
            throw new ArgumentException("Installation requires the appended Revision address.", nameof(address));
        }
        _candidate.PrepareInstallation(_candidate.NextBaseline.WithAddress(address, Revision));
    }

    /// <summary>Called only after confirmed publication; no field capture, callbacks or allocation.</summary>
    internal void Install() => _candidate.Install();

    public void Dispose() => _candidate.Dispose();
}
