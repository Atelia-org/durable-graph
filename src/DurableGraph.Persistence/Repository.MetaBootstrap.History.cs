using Atelia.DurableGraph.Persistence;
using Atelia.HeadStore;

namespace Atelia.DurableGraph;

public sealed partial class Repository {
    // History chooses the strict preceding State; the graph layer checks that exact source.
    private StoredGraphRevision QualifyBusiness(HistoryGraphRecord record) =>
        _storageFacade.Store.ReadBusinessGraph(record.Address,
            _resources.FindNearestState(record.Id, inclusive: false)?.Address);

    private IDurableObject ReadGraphRootCore(HistoryGraphRecord record) {
        var source = DecodedRevision.ReadStoredGraph(_storageFacade.Store, QualifyBusiness(record), _modelConfiguration);
        return GraphReader.ReadRoot<IDurableObject>(source, record.RootId, new(source.SourceModels!));
    }

    private Checkpoint ReadHistoryCheckpointCore(HistoryGraphRecord record) {
        var previousKind = record.Kind == CommitKind.Event ? CommitKind.State : CommitKind.Event;
        var previousRecord = _resources.FindPreviousKind(record.Id, previousKind);
        IDurableObject root;
        IDurableObject? previous;
        if (previousRecord is null) {
            root = ReadGraphRootCore(record);
            previous = null;
        } else {
            // Each side has its own strict State source. The opposite-role context is not
            // necessarily the source of the selected State (S0,E1,S1,S2 is the counterexample).
            var first = DecodedRevision.ReadStoredGraph(_storageFacade.Store, QualifyBusiness(record), _modelConfiguration);
            var second = DecodedRevision.ReadStoredGraph(_storageFacade.Store, QualifyBusiness(previousRecord), _modelConfiguration);
            (root, previous) = GraphReader.ReadIndependent(first, new(first.SourceModels!), record.RootId,
                second, new(second.SourceModels!), previousRecord.RootId);
        }
        return record.Kind == CommitKind.Event
            ? new EventCheckpoint(Issue(record), root, previous, previousRecord is null ? null : Issue(previousRecord))
            : new StateCheckpoint(Issue(record), root, previous, previousRecord is null ? null : Issue(previousRecord));
    }

    private (IDurableObject First, IDurableObject Second) ReadGraphPairCore(
        HistoryGraphRecord firstRecord, HistoryGraphRecord secondRecord) {
        var store = _storageFacade.Store;
        var firstSnapshot = QualifyBusiness(firstRecord);
        var secondSnapshot = QualifyBusiness(secondRecord);
        var first = DecodedRevision.ReadStoredGraph(store, firstSnapshot, _modelConfiguration);
        var environment = first.BindingEnvironment;
        if (secondSnapshot.SchemaRoot != environment?.SelectedSchemaView.Snapshot?.Address) { environment = null; }
        var second = DecodedRevision.ReadStoredGraph(store, secondSnapshot, _modelConfiguration, environment);
        return GraphReader.ReadPair(first, new(first.SourceModels!), firstRecord.RootId,
            second, new(second.SourceModels!), secondRecord.RootId);
    }

    private void CreateTagCore(string name, HeadEntryId id) {
        _resources.EnsureEditingAllowed();
        PublicationOutcome outcome = PublicationOutcome.NotAttempted;
        try { OperationStageObserver?.Invoke(RepositoryOperationStage.BeforePublication); }
        catch (Exception error) { throw new RepositoryPublicationException(RepositoryPublicationOutcome.NotPublished, null, error); }
        try { _resources.CreateStoredTag(name, id, out outcome); }
        catch (Exception error) {
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
