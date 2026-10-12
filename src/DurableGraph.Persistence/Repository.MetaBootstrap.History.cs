using Atelia.DurableGraph.Persistence;
using Atelia.FrameStore;
using Atelia.VersionStore;

namespace Atelia.DurableGraph;

public sealed partial class Repository {
    // Metadata navigation selects business history without reading live directories or typed bodies.
    private HistoryGraphRecord ReadHistoryCore(FrameAddress address) => UsesMetaBootstrap
        ? _resources.ReadMetaBootstrapHistory(address) : _resources.ReadHistory(address);

    // Callers own the complete operation's busy guard. Each selected graph gets its exact S environment.
    private IDurableObject ReadMetaBootstrapRootCore(CheckpointAddress frame) {
        DecodedRevision source = DecodedRevision.ReadSplit(_splitRepository!.Store, frame.Address, _splitConfiguration!);
        return GraphReader.ReadRoot<IDurableObject>(source, frame.RootId, new(source.SourceModels!));
    }

    private Checkpoint ReadMetaBootstrapCheckpointCore(CheckpointAddress address) {
        GraphFrameKind previousKind = address.Kind == GraphFrameKind.Event ? GraphFrameKind.State : GraphFrameKind.Event;
        CheckpointAddress? previousAddress = PreviousCore(address, previousKind);
        IDurableObject root;
        IDurableObject? previous;
        if (previousAddress is null) {
            root = ReadMetaBootstrapRootCore(address);
            previous = null;
        } else {
            DecodedRevision first = DecodedRevision.ReadSplit(_splitRepository!.Store, address.Address, _splitConfiguration!);
            DecodedRevision second = DecodedRevision.ReadSplit(_splitRepository.Store, previousAddress.Address, _splitConfiguration!);
            (root, previous) = GraphReader.ReadIndependent(first, new(first.SourceModels!), address.RootId,
                second, new(second.SourceModels!), previousAddress.RootId);
        }
        return address.Kind == GraphFrameKind.Event
            ? new EventCheckpoint(address, root, previous, previousAddress)
            : new StateCheckpoint(address, root, previous, previousAddress);
    }

    private (IDurableObject First, IDurableObject Second) ReadMetaBootstrapPairCore(
        CheckpointAddress firstAddress, CheckpointAddress secondAddress) {
        var store = _splitRepository!.Store;
        DecodedRevision first = DecodedRevision.ReadSplit(store, firstAddress.Address, _splitConfiguration!);
        DecodedRevision.SplitBindingEnvironment? environment = first.BindingEnvironment;
        if (store.ReadMetadata(secondAddress.Address).SchemaRoot != environment?.SelectedSchemaView.Snapshot?.Address) {
            environment = null;
        }
        // ReadSplit verifies the actual Store, frozen configuration and graph's exact S on reuse.
        DecodedRevision second = DecodedRevision.ReadSplit(store, secondAddress.Address, _splitConfiguration!, environment);
        return GraphReader.ReadPair(first, new(first.SourceModels!), firstAddress.RootId,
            second, new(second.SourceModels!), secondAddress.RootId);
    }

    private void MutateBranchCore(string branchName, FrameAddress head) {
        if (UsesMetaBootstrap) {
            // Complete the candidate before user publication hooks and any VS output.
            GraphResources.MetaBootstrapBranchUpdate update = _resources.PrepareMetaBootstrapBranchRoot(branchName, head);
            MutateRefCore((out PublicationOutcome outcome) => _resources.PublishMetaBootstrapBranchRoot(update, out outcome));
        } else {
            MutateRefCore((out PublicationOutcome outcome) => _resources.PublishBranch(branchName, head, out outcome));
        }
    }

    private FrameAddress ResolveTagCore(string name) {
        if (UsesMetaBootstrap) { return _resources.ResolveMetaBootstrapTag(name); }
        var payload = _resources.Versions.ResolveTag(name).Unwrap();
        if (!FrameAddress.TryRead(payload.Bytes, out FrameAddress address)) {
            throw new InvalidDataException("Invalid graph address in tag payload.");
        }
        return address;
    }

    private void CreateMetaBootstrapTagCore(string name, FrameAddress address) {
        _resources.EnsureMetaBootstrapEditingAllowed();
        PublicationOutcome outcome = PublicationOutcome.NotAttempted;
        try { Checkpoint?.Invoke(CommitCheckpoint.BeforePublication); }
        catch (Exception error) { throw new GraphCommitException(GraphCommitOutcome.NotPublished, null, error); }
        try { _resources.CreateMetaBootstrapTag(name, address, out outcome); }
        catch (Exception error) {
            // Pre-I/O validation and an ordinary VS rejection retain their original error.
            if (outcome == PublicationOutcome.NotAttempted && !_resources.IsFaulted) { throw; }
            throw new GraphCommitException(MapOutcome(outcome), null, error);
        }
        try { Checkpoint?.Invoke(CommitCheckpoint.AfterPublication); }
        catch (Exception error) {
            _resources.MarkFaulted();
            throw new GraphCommitException(GraphCommitOutcome.Published, null, error);
        }
    }
}
