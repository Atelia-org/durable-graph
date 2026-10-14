using Atelia.DurableGraph.Persistence;
using Atelia.FrameStore;
using Atelia.VersionStore;

namespace Atelia.DurableGraph;

public sealed partial class Repository {
    // Metadata navigation selects business history without reading live directories or typed bodies.
    private HistoryGraphRecord ReadHistoryCore(FrameAddress address) => _resources.ReadHistory(address);

    // Callers own the complete operation's busy guard. Each selected graph gets its exact S environment.
    private IDurableObject ReadGraphRootCore(CheckpointAddress frame) {
        DecodedRevision source = DecodedRevision.ReadStoredGraph(_storageFacade!.Store, frame.Address, _modelConfiguration!);
        return GraphReader.ReadRoot<IDurableObject>(source, frame.RootId, new(source.SourceModels!));
    }

    private Checkpoint ReadHistoryCheckpointCore(CheckpointAddress address) {
        CheckpointKind previousKind = address.Kind == CheckpointKind.Event ? CheckpointKind.State : CheckpointKind.Event;
        CheckpointAddress? previousAddress = PreviousCore(address, previousKind);
        IDurableObject root;
        IDurableObject? previous;
        if (previousAddress is null) {
            root = ReadGraphRootCore(address);
            previous = null;
        } else {
            DecodedRevision first = DecodedRevision.ReadStoredGraph(_storageFacade!.Store, address.Address, _modelConfiguration!);
            DecodedRevision second = DecodedRevision.ReadStoredGraph(_storageFacade.Store, previousAddress.Address, _modelConfiguration!);
            (root, previous) = GraphReader.ReadIndependent(first, new(first.SourceModels!), address.RootId,
                second, new(second.SourceModels!), previousAddress.RootId);
        }
        return address.Kind == CheckpointKind.Event
            ? new EventCheckpoint(address, root, previous, previousAddress)
            : new StateCheckpoint(address, root, previous, previousAddress);
    }

    private (IDurableObject First, IDurableObject Second) ReadGraphPairCore(
        CheckpointAddress firstAddress, CheckpointAddress secondAddress) {
        var store = _storageFacade!.Store;
        DecodedRevision first = DecodedRevision.ReadStoredGraph(store, firstAddress.Address, _modelConfiguration!);
        DecodedRevision.SchemaViewBindingEnvironment? environment = first.BindingEnvironment;
        if (store.ReadMetadata(secondAddress.Address).SchemaRoot != environment?.SelectedSchemaView.Snapshot?.Address) {
            environment = null;
        }
        // ReadStoredGraph verifies the actual Store, frozen configuration and graph's exact S on reuse.
        DecodedRevision second = DecodedRevision.ReadStoredGraph(store, secondAddress.Address, _modelConfiguration!, environment);
        return GraphReader.ReadPair(first, new(first.SourceModels!), firstAddress.RootId,
            second, new(second.SourceModels!), secondAddress.RootId);
    }

    private void MutateBranchCore(string branchName, FrameAddress head) {
        // Complete the candidate before user publication hooks and any VS output.
        RepositoryResources.PreparedBranchRootUpdate update = _resources.PrepareBranchRoot(branchName, head);
        MutateRefCore((out PublicationOutcome outcome) => _resources.PublishBranchRoot(update, out outcome));
    }

    private FrameAddress ResolveTagCore(string name) => _resources.ResolveStoredTag(name);

    private void CreateTagCore(string name, FrameAddress address) {
        _resources.EnsureEditingAllowed();
        PublicationOutcome outcome = PublicationOutcome.NotAttempted;
        try { OperationStageObserver?.Invoke(RepositoryOperationStage.BeforePublication); }
        catch (Exception error) { throw new RepositoryPublicationException(RepositoryPublicationOutcome.NotPublished, null, error); }
        try { _resources.CreateStoredTag(name, address, out outcome); }
        catch (Exception error) {
            // Pre-I/O validation and an ordinary VS rejection retain their original error.
            if (outcome == PublicationOutcome.NotAttempted && !_resources.IsFaulted) { throw; }
            throw new RepositoryPublicationException(MapOutcome(outcome), null, error);
        }
        try { OperationStageObserver?.Invoke(RepositoryOperationStage.AfterPublication); }
        catch (Exception error) {
            _resources.MarkFaulted();
            throw new RepositoryPublicationException(RepositoryPublicationOutcome.Published, null, error);
        }
    }
}
