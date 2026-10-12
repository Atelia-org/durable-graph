using Atelia.DurableGraph.Storage;
using Atelia.DurableGraph.Schema;
namespace Atelia.DurableGraph.Persistence;
internal static class RepositoryGraphValidator {
    internal static Atelia.EventJournal.EventAddress RequireBranchHead(Atelia.EventJournal.EventAddress? head) =>
        head ?? throw new InvalidDataException("EventHistory branches cannot have empty heads.");

    internal static void RequireBranchMembership(Atelia.EventJournal.EventAddress head, Func<Atelia.EventJournal.EventAddress, bool> contains) {
        if (!contains(head)) { throw new InvalidDataException("Branch head is absent from physical Journal history."); }
    }

    internal static HistoryGraphRecord? FindBaseline(HistoryGraphRecord record,
        IReadOnlyDictionary<Atelia.EventJournal.EventAddress, HistoryGraphRecord?> nearestStates) {
        if (record.Parent is not { } parent) { return null; }
        if (!nearestStates.TryGetValue(parent, out var baseline)) {
            throw new InvalidDataException("Journal Parent is missing from the preceding physical history.");
        }
        return baseline;
    }

    // Operation-local ancestry, deliberately independent of the full physical-history index.
    // The resolver retains its checked-read contract; this proves graph relationships, not
    // arbitrary tickets' membership in a continuous physical file scan.
    internal static void ValidateAncestry(StateRevisionStore states, SchemaStore schemas,
        HistoryGraphRecord selected, Func<Atelia.EventJournal.EventAddress, HistoryGraphRecord> read) {
        var pending = new Stack<HistoryGraphRecord>();
        HistoryGraphRecord current = selected;
        while (true) {
            pending.Push(current);
            if (current.Parent is not { } parent) { break; }
            if (parent.SegmentNumber > current.Address.SegmentNumber ||
                parent.SegmentNumber == current.Address.SegmentNumber && parent.Ticket.Offset >= current.Address.Ticket.Offset) {
                throw new InvalidDataException("A Journal parent must physically precede its child.");
            }
            try { current = read(parent); }
            catch (Exception error) when (error is InvalidDataException or IOException or ArgumentException or InvalidOperationException or AteliaException) {
                error.Data["DurableGraph.JournalAddress"] = parent.ToString();
                throw;
            }
        }
        HistoryGraphRecord? baseline = null;
        var checkedRevisions = new HashSet<FrameAddress>();
        while (pending.TryPop(out var record)) {
            try {
                ValidateStateClosure(states, schemas, record.RevisionAddress, checkedRevisions);
                ValidateGraph(states, schemas, record, baseline?.RevisionAddress);
            }
            catch (Exception error) when (error is InvalidDataException or IOException or ArgumentException or InvalidOperationException or AteliaException) {
                error.Data["DurableGraph.JournalAddress"] = record.Address.ToString();
                if (!error.Data.Contains("DurableGraph.RevisionAddress")) {
                    error.Data["DurableGraph.RevisionAddress"] = record.RevisionAddress.ToString();
                }
                throw;
            }
            if (record.Kind == GraphFrameKind.State) { baseline = record; }
        }
    }

    private static void ValidateStateClosure(StateRevisionStore states, SchemaStore schemas,
        FrameAddress root, HashSet<FrameAddress> checkedRevisions) {
        var pending = new Stack<FrameAddress>();
        var records = new List<FrameAddress>();
        pending.Push(root);
        while (pending.TryPop(out var address)) {
            if (!checkedRevisions.Add(address)) { continue; }
            try {
                ValidateRevisionEdges(states.Read(address), edge => {
                    RequireEarlierStateEdge(address, edge);
                    if (!checkedRevisions.Contains(edge)) { pending.Push(edge); }
                }, default);
                records.Add(address);
            }
            catch (Exception error) when (error is InvalidDataException or IOException or ArgumentException or InvalidOperationException or AteliaException) {
                error.Data["DurableGraph.RevisionAddress"] = address.ToString();
                throw;
            }
        }
        // First check each addressed frame itself, so a bad CRC is attributed to that address,
        // before map/object traversal can encounter the same frame indirectly from its owner.
        foreach (var address in records) {
            try {
                ValidateRevisionDependencies(states, schemas, address, edge => RequireEarlierStateEdge(address, edge));
            }
            catch (Exception error) when (error is InvalidDataException or IOException or ArgumentException or InvalidOperationException or AteliaException) {
                error.Data["DurableGraph.RevisionAddress"] = address.ToString();
                throw;
            }
        }
    }

    private static void RequireEarlierStateEdge(FrameAddress address, FrameAddress edge) {
        if (edge.FileNumber > address.FileNumber ||
            edge.FileNumber == address.FileNumber && edge.FrameTicket.Offset >= address.FrameTicket.Offset) {
            throw new InvalidDataException("State dependency must precede its containing Revision.");
        }
    }

    // Shared State record rules. Audit supplies physical membership; the controlled-source
    // candidate supplies earlier-edge validation and follows the dependency closure by address.
    internal static void ValidateRevisionDependencies(StateRevisionStore states, SchemaStore? schemas,
        FrameAddress address, Action<FrameAddress> requireEdge, CancellationToken cancellationToken = default) {
        var revision = states.Read(address);
        ValidateRevisionEdges(revision, requireEdge, cancellationToken);
        foreach (uint id in states.ReadLiveObjectHeadMap(address).Keys) {
            cancellationToken.ThrowIfCancellationRequested();
            var chain = states.ReadObjectVersionChain(address, id);
            if (schemas is not null) { ValidateObject(chain, schemas); }
        }
    }

    private static void ValidateRevisionEdges(StateRevision revision, Action<FrameAddress> requireEdge,
        CancellationToken cancellationToken) {
        if (revision.ParentRevisionAddress is { } parent) { requireEdge(parent); }
        foreach (var edge in revision.ExternalObjectHeads.Values) {
            cancellationToken.ThrowIfCancellationRequested();
            requireEdge(edge);
        }
        foreach (var local in revision.LocalObjects) {
            cancellationToken.ThrowIfCancellationRequested();
            if (local.PriorAddress is { } prior) { requireEdge(prior); }
        }
    }

    internal static void ValidateGraph(StateRevisionStore states, SchemaStore schemas, HistoryGraphRecord record, FrameAddress? expectedParent, CancellationToken cancellationToken = default) {
        StateRevision revision = states.Read(record.RevisionAddress);
        if (revision.ParentRevisionAddress != expectedParent) { throw new InvalidDataException("Journal and StateRevision Parent disagree."); }
        var objects = states.ReadLiveObjectHeadMap(record.RevisionAddress);
        if (!objects.ContainsKey(record.RootId.Value)) { throw new InvalidDataException("Graph root is absent from Revision membership."); }
        foreach (uint id in objects.Keys) {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectVersionChain chain = states.ReadObjectVersionChain(record.RevisionAddress, id);
            DecodedBaseObjectBody body = ValidateObject(chain, schemas);
            if (id == record.RootId.Value && body.Kind != ObjectStateKind.Durable) { throw new InvalidDataException("Graph root must be durable."); }
        }
    }

    internal static DecodedBaseObjectBody ValidateObject(ObjectVersionChain chain, SchemaStore schemas) {
        DecodedBaseObjectBody body = BaseObjectBodyCodec.Decode(chain.Records[0].Record.Body, schemas);
        if (body.Kind == ObjectStateKind.Durable && body.Layout.Schema!.Kind != SchemaKind.ReferenceObject) { throw new InvalidDataException("Object Base cannot select an inline Schema."); }
        if (body.Kind == ObjectStateKind.String && chain.Records.Count != 1) { throw new InvalidDataException("String cannot have a Delta chain."); }
        return body;
    }

}
