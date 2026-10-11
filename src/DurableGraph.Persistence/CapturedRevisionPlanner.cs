using Atelia.FrameStore;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;

namespace Atelia.DurableGraph.Persistence;

/// <summary>
/// Registers captured representations and plans typed Base contents against an explicit
/// Parent. The caller still owns the DTO baseline's correspondence to that Parent.
/// Representation registration is durable; State append, publication and Capture.Accept
/// remain outside this operation.
/// </summary>
internal static class CapturedRevisionPlanner {
    internal static PreparedObjectRevision PrepareRevision(
        StateRevisionStore store,
        SchemaStore schemas,
        FrameAddress? parentRevisionAddress,
        PreparedCapturedGraph input,
        ReadAmplificationBaseBudgetParameters parameters) {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(schemas);
        ArgumentNullException.ThrowIfNull(input);
        if ((parentRevisionAddress is null) != (input.Previous is null)) {
            throw new ArgumentException("Parent and the previous captured graph must either both exist or both be absent.", nameof(input));
        }

        IReadOnlyDictionary<ObjectId, FrameAddress> parentHeads = parentRevisionAddress is { } parent
            ? store.ReadLiveObjectHeadMap(parent).ToDictionary(static pair => new ObjectId(pair.Key), static pair => pair.Value)
            : new Dictionary<ObjectId, FrameAddress>();
        if (input.Previous is { } previous &&
            (previous.Objects.Count != parentHeads.Count || previous.Objects.Any(item => !parentHeads.ContainsKey(item.Id)))) {
            throw new ArgumentException("The previous graph must describe the exact Parent's complete live membership.", nameof(input));
        }

        // Preflight every surviving object's stored type before registering any
        // new schema, including NoChange and objects the policy might rebase.
        foreach (PreparedCapturedObject row in input.Objects) {
            bool exists = parentHeads.ContainsKey(row.Current.Id);
            if (row.Previous is null) {
                if (exists) {
                    throw new ArgumentException("A new captured object already exists in Parent.", nameof(input));
                }
                continue;
            }
            if (!exists) {
                throw new ArgumentException("An existing captured object is absent from Parent.", nameof(input));
            }

            // TODO(DB-031): Measure duplicate chain reads here and in the policy
            // planner before introducing an operation-scoped cache.
            ObjectVersionChain chain = store.ReadObjectVersionChain(parentRevisionAddress!.Value, row.Current.Id.Value);
            DecodedBaseObjectBody stored = BaseObjectBodyCodec.Decode(chain.Records[0].Record.Body, schemas);
            if (stored.Kind != row.Current.Kind) {
                throw new InvalidDataException($"Object {row.Current.Id} changed its stored type kind.");
            }
            if (stored.Kind == ObjectStateKind.String) {
                if (chain.Records.Count != 1) {
                    throw new InvalidDataException("String objects cannot have Delta records.");
                }
            }
            else {
                if (!stored.Layout.Equals(row.Current.Layout)) {
                    throw new InvalidDataException($"Object {row.Current.Id} cannot extend a different exact layout. A controlled migration must write a new Base.");
                }
            }
        }

        // Registration validates the complete Schema closure even for an existing
        // representation ID or unchanged object, before its first append.
        RepresentationId[] representations = schemas.RegisterRepresentations(
            input.Objects.Select(static row => row.Current.Layout).ToArray());

        PreparedObject[] rows = input.Objects.Select((row, index) => {
            EncodedBaseObjectBody encodedBaseBody = BaseObjectBodyCodec.Encode(representations[index], row.BaseBody);
            if (row.Previous is null) {
                return PreparedObject.New(row.Current.Id, encodedBaseBody);
            }
            FrameAddress prior = parentHeads[row.Current.Id];
            return row.DeltaBody is null
                ? PreparedObject.Unchanged(row.Current.Id, prior, encodedBaseBody)
                : PreparedObject.Compared(row.Current.Id, prior, encodedBaseBody, row.DeltaBody);
        }).ToArray();
        return ObjectRevisionPlanner.PrepareRevision(store, parentRevisionAddress, rows, parameters);
    }
}
