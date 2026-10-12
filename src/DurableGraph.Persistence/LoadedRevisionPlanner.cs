using Atelia.FrameStore;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;

namespace Atelia.DurableGraph.Persistence;

/// <summary>Consumes only the normalized baseline produced by controlled Load.</summary>
internal static class LoadedRevisionPlanner {
    internal static PreparedObjectRevision Prepare(StateRevisionStore store, SchemaStore schemas,
        NormalizedRevision source, IReadOnlyList<PreparedCapturedObject> contents,
        ReadAmplificationBaseBudgetParameters parameters, bool independentSnapshot = false) {
        source.RequireStorageSource(store, schemas);
        IReadOnlyDictionary<ObjectId, FrameAddress> heads = store.ReadLiveObjectHeadMap(source.RevisionAddress).ToDictionary(static pair => new ObjectId(pair.Key), static pair => pair.Value);
        if (heads.Count != source.Objects.Count || source.Objects.Keys.Any(id => !heads.ContainsKey(id))) {
            throw new InvalidDataException("Loaded source membership no longer matches the exact Parent.");
        }
        // Append-only history was verified while establishing this baseline. Recheck its
        // exact Parent and every head, including source rows no longer reachable from World.
        // Current Schema registration below still validates the live catalog closure.
        foreach ((ObjectId id, NormalizedObject row) in source.Objects) {
            if (row.Storage is not { } storage || storage.Head != heads[id]) {
                throw new InvalidDataException($"Loaded object {id} no longer matches its exact source head.");
            }
            ObjectLayout layout = row.SourceLayout;
            if (layout.Kind != row.Current.Kind) {
                throw new InvalidDataException($"Loaded object {id} no longer has its source kind.");
            }
            if (layout.Kind == ObjectStateKind.String) {
                if (row.SourceSchema is not null || row.RequiresRewrite) {
                    throw new InvalidDataException("String source provenance is invalid.");
                }
            }
            if (row.RequiresRewrite != !layout.Equals(row.Current.Layout) ||
                !row.Model.CurrentLayout.Equals(row.Current.Layout)) {
                throw new InvalidDataException($"Loaded object {id} no longer matches its exact source layout and normalized model.");
            }
        }
        foreach (PreparedCapturedObject row in contents) {
            bool existed = source.Objects.TryGetValue(row.Current.Id, out NormalizedObject? prior);
            if (existed != (row.Previous is not null) ||
                (existed && (!ReferenceEquals(row.Previous, prior!.Current) ||
                    row.Current.Kind != prior.Current.Kind ||
                    !row.Current.Layout.Equals(prior.Current.Layout)))) {
                throw new InvalidDataException("Prepared contents do not match the controlled normalized baseline.");
            }
        }
        RepresentationId[] representations = schemas.RegisterRepresentations(
            contents.Select(static row => row.Current.Layout).ToArray());
        List<PreparedObject> rows = [];
        for (int index = 0; index < contents.Count; index++) {
            PreparedCapturedObject row = contents[index];
            EncodedBaseObjectBody encodedBaseBody = BaseObjectBodyCodec.Encode(representations[index], row.BaseBody);
            rows.Add(ObjectRevisionPlanner.ClassifyCaptured(row, encodedBaseBody,
                row.Previous is null ? null : heads[row.Current.Id],
                row.Previous is not null && source.Objects[row.Current.Id].RequiresRewrite));
        }
        return ObjectRevisionPlanner.PrepareLoadedRevision(store, source, rows, parameters, independentSnapshot);
    }
}
