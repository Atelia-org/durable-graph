using Atelia.FrameStore;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;
using Atelia.DurableGraph.Persistence.MetaBootstrapP0;

namespace Atelia.DurableGraph.Persistence;

/// <summary>One current DTO baseline, with complete source membership and migration provenance.</summary>
internal sealed class NormalizedRevision {
    private readonly Dictionary<ObjectId, NormalizedObject> _objects;
    private readonly bool _isCandidate;
    private readonly NormalizedRevision? _candidateSource;

    private NormalizedRevision(FrameAddress address, Dictionary<ObjectId, NormalizedObject> objects, StringReadTable strings,
        StateRevisionStore? sourceStore, SchemaStore? sourceSchemas,
        IReadOnlyDictionary<ObjectId, ObjectStateRecord>? currentDtos = null,
        P0GraphStore? splitStore = null, P0CatalogView? selectedSchemaView = null, StateModelSnapshot? sourceModels = null,
        bool isCandidate = false, NormalizedRevision? candidateSource = null) {
        RevisionAddress = address;
        _objects = objects;
        Strings = strings;
        CurrentDtos = currentDtos ?? objects.ToDictionary(static pair => pair.Key, static pair => pair.Value.Current);
        SourceStore = sourceStore;
        SourceSchemas = sourceSchemas;
        SplitStore = splitStore;
        SelectedSchemaView = selectedSchemaView;
        SourceModels = sourceModels;
        _isCandidate = isCandidate;
        _candidateSource = candidateSource;
    }

    internal FrameAddress RevisionAddress { get; }
    internal IReadOnlyDictionary<ObjectId, NormalizedObject> Objects => _objects;
    internal StringReadTable Strings { get; }
    internal IReadOnlyDictionary<ObjectId, ObjectStateRecord> CurrentDtos { get; }
    internal StateRevisionStore? SourceStore { get; }
    internal SchemaStore? SourceSchemas { get; }
    internal P0GraphStore? SplitStore { get; }
    internal P0CatalogView? SelectedSchemaView { get; }
    internal StateModelSnapshot? SourceModels { get; }

    // Candidate DTOs are prepared before append. Unwritten objects retain their committed
    // storage facts; actual writes complete a separate installation before publication.
    internal static NormalizedRevision FromCandidate(CapturedGraph candidate, StateModelSnapshot models,
        StateRevisionStore store, SchemaStore schemas, NormalizedRevision? baseline) {
        var (rows, strings) = CreateCandidate(candidate, models, baseline);
        return new(default, rows, strings, store, schemas, sourceModels: models, isCandidate: true, candidateSource: baseline);
    }

    internal static NormalizedRevision FromSplitCandidate(CapturedGraph candidate, StateModelSnapshot models,
        P0GraphStore store, NormalizedRevision? baseline) {
        baseline?.RequireSplitSource(store, models);
        var (rows, strings) = CreateCandidate(candidate, models, baseline);
        return new(default, rows, strings, null, null, splitStore: store,
            selectedSchemaView: baseline?.SelectedSchemaView, sourceModels: models, isCandidate: true, candidateSource: baseline);
    }

    private static (Dictionary<ObjectId, NormalizedObject> Rows, StringReadTable Strings) CreateCandidate(
        CapturedGraph candidate, StateModelSnapshot models, NormalizedRevision? baseline) {
        Dictionary<ObjectId, NormalizedObject> rows = [];
        foreach (ObjectStateRecord row in candidate.Objects) {
            ObjectBinding model = ResolveModel(models, row.Layout);
            ObjectStorageInfo? storage = baseline is not null && baseline.Objects.TryGetValue(row.Id, out NormalizedObject? prior)
                ? prior.Storage : null;
            rows.Add(row.Id, new(row, row.Layout, false, model, storage));
        }
        StringReadTable strings = StringReadTable.FromDecoded(candidate.Objects
            .Where(static row => row.Kind == ObjectStateKind.String)
            .Select(static row => (row.Id, row.StringContent)));
        return (rows, strings);
    }

    internal NormalizedRevision WithAddress(FrameAddress address, StateRevision revision) {
        if (!_isCandidate || RevisionAddress != default || SourceStore is null || SourceSchemas is null) {
            throw new InvalidOperationException("Only a private save candidate can prepare an installation.");
        }
        // Read this Store's actual appended frame/map. This does not reopen old content chains.
        StateRevision actual = SourceStore.Read(address);
        if (actual.ParentRevisionAddress != _candidateSource?.RevisionAddress || actual.ParentRevisionAddress != revision.ParentRevisionAddress) {
            throw new InvalidDataException("An installation must use its candidate's exact content Parent.");
        }
        RequireSameWrites(revision.LocalObjects, actual.LocalObjects);
        IReadOnlyDictionary<uint, FrameAddress> heads = SourceStore.ReadLiveObjectHeadMap(address);
        return CompleteInstallation(address, actual.LocalObjects, heads, SourceStore, SourceSchemas, null, null);
    }

    internal NormalizedRevision WithSplitGraph(P0GraphStore store, P0GraphSnapshot graph,
        IReadOnlyList<ObjectVersionRecord> expectedRecords, P0CatalogView selected) {
        if (!_isCandidate || RevisionAddress != default || !ReferenceEquals(SplitStore, store) ||
            !ReferenceEquals(graph.SourceOwner, store)) {
            throw new InvalidOperationException("Only this split Store's private candidate can prepare an installation.");
        }
        P0GraphSnapshot actual = store.ReadGraph(graph.Address);
        if (actual.Metadata != graph.Metadata || actual.SourceGraph != _candidateSource?.RevisionAddress ||
            !actual.Objects.SequenceEqual(graph.Objects) || selected.Snapshot is not { } schema ||
            !ReferenceEquals(schema.SourceOwner, store) || schema.Address != actual.SchemaRoot) {
            throw new InvalidDataException("A split installation must use its actual graph, content Parent and completed Schema view.");
        }
        IReadOnlyList<ObjectVersionRecord> records = actual.OwnedBatch is { } batch ? store.ReadBatch(batch).Records : [];
        RequireSameWrites(expectedRecords, records);
        foreach (NormalizedObject row in _objects.Values) { selected.Require(row.Current.Layout); }
        return CompleteInstallation(actual.Address, records, actual.Objects, null, null, store, selected);
    }

    // Actual maps reach this private calculation only through a concrete backend check above.
    private NormalizedRevision CompleteInstallation(FrameAddress address, IReadOnlyList<ObjectVersionRecord> records,
        IReadOnlyDictionary<uint, FrameAddress> heads, StateRevisionStore? sourceStore, SchemaStore? sourceSchemas,
        P0GraphStore? splitStore, P0CatalogView? selected) {
        if (heads.Count != _objects.Count || _objects.Keys.Any(id => !heads.ContainsKey(id.Value))) {
            throw new InvalidDataException("Installed membership must equal the complete captured candidate.");
        }
        Dictionary<ObjectId, NormalizedObject> installed = new(_objects);
        HashSet<ObjectId> written = [];
        HashSet<ObjectId> bases = [];
        foreach (ObjectVersionRecord record in records) {
            ObjectId id = new(record.ObjectId);
            if (!written.Add(id) || !installed.TryGetValue(id, out NormalizedObject? row)) {
                throw new InvalidDataException("Every installed record must identify one unique candidate member.");
            }
            long actualBytes = record.EncodedPayloadBytes
                ?? throw new InvalidDataException("Installation accounting requires actual read record bytes.");
            long payloadBytes;
            if (record.Kind == ObjectVersionKind.Base) {
                bases.Add(id);
                payloadBytes = actualBytes;
            } else {
                ObjectStorageInfo prior = row.Storage
                    ?? throw new InvalidDataException("An appended Delta requires committed storage information.");
                if (record.PriorAddress != prior.Head) {
                    throw new InvalidDataException("An appended Delta must extend the committed object head.");
                }
                payloadBytes = checked(prior.ReconstructionPayloadBytes + actualBytes);
            }
            installed[id] = row with { Storage = new ObjectStorageInfo(heads[id.Value], payloadBytes) };
        }
        foreach ((ObjectId id, NormalizedObject row) in installed) {
            if (row.Storage is not { } storage || storage.Head != heads[id.Value]) {
                throw new InvalidDataException("Every installed object requires its actual committed head and payload information.");
            }
            if (_candidateSource is not null && _candidateSource.Objects.TryGetValue(id, out NormalizedObject? prior) && prior.RequiresRewrite &&
                !bases.Contains(id)) {
                throw new InvalidDataException("A surviving rewritten object must install a new Base.");
            }
        }
        return new(address, installed, Strings, sourceStore, sourceSchemas, CurrentDtos, splitStore, selected, SourceModels);
    }

    private static void RequireSameWrites(IReadOnlyList<ObjectVersionRecord> expected, IReadOnlyList<ObjectVersionRecord> actual) {
        if (expected.Count != actual.Count) { throw new InvalidDataException("The appended records differ from the prepared candidate."); }
        for (int index = 0; index < expected.Count; index++) {
            ObjectVersionRecord left = expected[index], right = actual[index];
            if (left.ObjectId != right.ObjectId || left.Kind != right.Kind || left.PriorAddress != right.PriorAddress || !left.Body.SequenceEqual(right.Body)) {
                throw new InvalidDataException("The appended records differ from the prepared candidate.");
            }
        }
    }

    internal void RequireStorageSource(StateRevisionStore store, SchemaStore schemas) {
        if (!ReferenceEquals(SourceStore, store) || !ReferenceEquals(SourceSchemas, schemas)) {
            throw new InvalidDataException("The normalized baseline must originate in these exact Store lifetimes.");
        }
    }

    internal void RequireSplitSource(P0GraphStore store, StateModelSnapshot models) {
        if (_isCandidate || !ReferenceEquals(SplitStore, store) || !ReferenceEquals(SourceModels, models) || SelectedSchemaView is not { } selected) {
            throw new InvalidDataException("The normalized baseline must retain its exact split Store and frozen model environment.");
        }
        P0GraphSnapshot actual = store.ReadGraph(RevisionAddress);
        if (actual.Kind != P0GraphKind.State || selected.Snapshot is not { } schema || !ReferenceEquals(schema.SourceOwner, store) ||
            schema.Address != actual.SchemaRoot || actual.Objects.Count != _objects.Count || _objects.Keys.Any(id => !actual.Objects.ContainsKey(id.Value))) {
            throw new InvalidDataException("Normalized source membership and selected Schema view must match the exact State.");
        }
        foreach ((ObjectId id, NormalizedObject row) in _objects) {
            if (row.Storage is not { } storage || storage.Head != actual.Objects[id.Value] || row.SourceLayout.Kind != row.Current.Kind ||
                row.RequiresRewrite != !row.SourceLayout.Equals(row.Current.Layout) || !row.Model.CurrentLayout.Equals(row.Current.Layout) ||
                (row.Current.Kind == ObjectStateKind.String && (row.SourceSchema is not null || row.RequiresRewrite))) {
                throw new InvalidDataException("Every normalized source row must retain its exact head, layout and model, including removed members.");
            }
            selected.Require(row.SourceLayout);
        }
    }

    internal static NormalizedRevision Create(DecodedRevision source, StateModelSnapshot models,
        GraphReadStatistics? statistics = null) {
        if (source.SplitStore is not null && !ReferenceEquals(source.SourceModels, models)) {
            throw new InvalidDataException("A split decoded source must normalize in its issuing frozen model environment.");
        }
        Dictionary<ObjectId, NormalizedObject> normalized = [];
        foreach (ObjectStateRecord row in source.Objects) {
            ObjectBinding model = ResolveModel(models, row.Layout);
            if (statistics is not null) { statistics.NormalizedObjects++; }
            ObjectStateRecord current = model.Normalize(row);
            if (current.Id != row.Id || current.Kind != row.Kind ||
                !model.CurrentLayout.Equals(current.Layout)) {
                throw new InvalidDataException("Normalization must preserve object identity and produce the exact current layout.");
            }
            ObjectStorageInfo? storage = source.ObjectStorage.TryGetValue(row.Id, out ObjectStorageInfo info) ? info : null;
            normalized.Add(row.Id, new(current, row.Layout, !row.Layout.Equals(current.Layout), model, storage));
        }
        // The complete old chains were decoded first. Validate current references only after
        // every single-object Upgrade completed; source rows remain live even after an edge is cut.
        Dictionary<ObjectId, ObjectStateRecord> directory = normalized.ToDictionary(static pair => pair.Key, static pair => pair.Value.Current);
        StateReferenceValidator validator = new(directory);
        foreach (NormalizedObject row in normalized.Values) {
            if (statistics is not null) { statistics.CurrentReferenceValidationVisits++; }
            row.Model.VisitReferences(row.Current, validator);
        }
        // Validate all source members even if an Upgrade removed their last incoming edge.
        foreach (NormalizedObject row in normalized.Values) {
            if (row.Model is DictionaryObjectBinding dictionary) {
                if (statistics is not null) { statistics.DictionaryValidationCalls++; }
                dictionary.ValidateLookupKeys(row.Current, id => directory.TryGetValue(id, out ObjectStateRecord? target)
                    ? target : throw new InvalidDataException($"Dictionary key object {id.Value} is not live."));
            }
        }
        return new(source.RevisionAddress, normalized, source.Strings, source.SourceStore, source.SourceSchemas, directory,
            source.SplitStore, source.SelectedSchemaView, models);
    }

    private static ObjectBinding ResolveModel(StateModelSnapshot models, ObjectLayout layout) =>
        models.TryGetCurrentObjectBinding(models.GetDomainType(layout.Type), out ObjectBinding? binding) ? binding! :
        throw new InvalidDataException($"No current object binding exists for {layout.Type}.");
}

internal sealed record NormalizedObject(
    ObjectStateRecord Current,
    ObjectLayout SourceLayout,
    bool RequiresRewrite,
    ObjectBinding Model,
    ObjectStorageInfo? Storage = null) {
    internal DurableSchema? SourceSchema => SourceLayout.Schema;
}
