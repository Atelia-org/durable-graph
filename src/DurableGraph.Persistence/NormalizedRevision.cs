using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;

namespace Atelia.DurableGraph.Persistence;

/// <summary>One current DTO baseline, with complete source membership and migration provenance.</summary>
internal sealed class NormalizedRevision {
    private readonly Dictionary<ObjectId, NormalizedObject> _objects;

    private NormalizedRevision(FrameAddress address, Dictionary<ObjectId, NormalizedObject> objects, StringReadTable strings,
        StateRevisionStore? sourceStore, SchemaStore? sourceSchemas,
        IReadOnlyDictionary<ObjectId, ObjectStateRecord>? currentDtos = null) {
        RevisionAddress = address;
        _objects = objects;
        Strings = strings;
        CurrentDtos = currentDtos ?? objects.ToDictionary(static pair => pair.Key, static pair => pair.Value.Current);
        SourceStore = sourceStore;
        SourceSchemas = sourceSchemas;
    }

    internal FrameAddress RevisionAddress { get; }
    internal IReadOnlyDictionary<ObjectId, NormalizedObject> Objects => _objects;
    internal StringReadTable Strings { get; }
    internal IReadOnlyDictionary<ObjectId, ObjectStateRecord> CurrentDtos { get; }
    internal StateRevisionStore? SourceStore { get; }
    internal SchemaStore? SourceSchemas { get; }

    // Candidate DTOs are prepared before append. Unwritten objects retain their committed
    // storage facts; actual writes complete a separate installation before publication.
    internal static NormalizedRevision FromCandidate(CapturedGraph candidate, StateModelSnapshot models,
        StateRevisionStore store, SchemaStore schemas, NormalizedRevision? baseline) {
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
        return new(default, rows, strings, store, schemas);
    }

    internal NormalizedRevision WithAddress(FrameAddress address, StateRevision revision) {
        if (RevisionAddress != default || SourceStore is null || SourceSchemas is null) {
            throw new InvalidOperationException("Only a private save candidate can prepare an installation.");
        }
        Dictionary<ObjectId, NormalizedObject> installed = new(_objects);
        FileScope scope = new(address.FileNumber);
        foreach (ObjectVersionRecord record in revision.LocalObjects) {
            ObjectId id = new(record.ObjectId);
            NormalizedObject row = installed[id];
            long payloadBytes;
            if (record.Kind == ObjectVersionKind.Base) {
                payloadBytes = ObjectVersionPayloadSize.GetBasePayloadBytes(record.Body.Length);
            } else {
                ObjectStorageInfo prior = row.Storage
                    ?? throw new InvalidDataException("An appended Delta requires committed storage information.");
                if (record.PriorAddress != prior.Head) {
                    throw new InvalidDataException("An appended Delta must extend the committed object head.");
                }
                payloadBytes = checked(prior.ReconstructionPayloadBytes +
                    ObjectVersionPayloadSize.GetDeltaPayloadBytes(record.Body.Length, prior.Head, scope));
            }
            installed[id] = row with { Storage = new ObjectStorageInfo(address, payloadBytes) };
        }
        if (installed.Values.Any(static row => row.Storage is null)) {
            throw new InvalidDataException("Every installed object requires complete storage information.");
        }
        return new(address, installed, Strings, SourceStore, SourceSchemas, CurrentDtos);
    }

    internal void RequireStorageSource(StateRevisionStore store, SchemaStore schemas) {
        if (!ReferenceEquals(SourceStore, store) || !ReferenceEquals(SourceSchemas, schemas)) {
            throw new InvalidDataException("The normalized baseline must originate in these exact Store lifetimes.");
        }
    }

    internal static NormalizedRevision Create(DecodedRevision source, StateModelSnapshot models,
        GraphReadStatistics? statistics = null) {
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
        return new(source.RevisionAddress, normalized, source.Strings, source.SourceStore, source.SourceSchemas, directory);
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
