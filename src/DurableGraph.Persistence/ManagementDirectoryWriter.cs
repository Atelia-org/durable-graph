using System.Buffers.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.HeadStore;

namespace Atelia.DurableGraph.Persistence;

// Baselines must come from the narrow reader or this candidate readback; same-assembly
// fabricated rows/H are not an authenticated input. Only additions are supported. Frozen decoded rows remain the sole accepted baseline.
internal static class ManagementDirectoryWriter {
    internal static PreparedDirectoryWrite PrepareBranchAddition(GraphRecordStore store, DecodedGraphRows? accepted,
        HeadId branch, string label) {
        ArgumentNullException.ThrowIfNull(label);
        ManagementDirectoryReader.RequireText(label);
        Span<byte> bytes = stackalloc byte[HeadId.EncodedSize];
        if (!branch.TryWrite(bytes)) { throw new ArgumentException("A valid HeadId is required.", nameof(branch)); }
        uint key = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        var rows = Begin(store, accepted, ManagementDirectoryKind.Branches, out ObjectId root, out ulong next);
        var prior = accepted?.Rows[root].GetDictionaryState<uint, ObjectId>();
        var entries = prior?.Entries.ToArray().ToList() ?? [];
        if (entries.Any(item => item.Key == key)) { throw new ArgumentException("The branch is already registered.", nameof(branch)); }
        ObjectId text = Allocate(ref next);
        rows.Add(text, new(text, label));
        entries.Add(new(key, text));
        rows[root] = new(root, ManagementDirectoryReader.BranchesLayout,
            new FrozenDictionaryState<uint, ObjectId>(DictionaryComparerKind.ScalarDefault, entries.OrderBy(item => item.Key).ToArray()));
        return Finish(store, accepted, ManagementDirectoryKind.Branches, root, rows);
    }

    internal static PreparedDirectoryWrite PrepareBookmarkAddition(GraphRecordStore store, DecodedGraphRows? accepted,
        string name, HeadEntryId target) {
        ArgumentNullException.ThrowIfNull(name);
        ManagementDirectoryReader.RequireText(name);
        byte[] bytes = new byte[HeadEntryId.EncodedSize];
        if (!target.TryWrite(bytes)) { throw new ArgumentException("A valid HeadEntryId is required.", nameof(target)); }
        var rows = Begin(store, accepted, ManagementDirectoryKind.Bookmarks, out ObjectId root, out ulong next);
        var prior = accepted?.Rows[root].GetDictionaryState<ObjectId, ObjectId>();
        var entries = prior?.Entries.ToArray().ToList() ?? [];
        if (entries.Any(item => StringComparer.Ordinal.Equals(rows[item.Key].StringContent, name))) {
            throw new ArgumentException("The bookmark name already exists.", nameof(name));
        }
        ObjectId text = Allocate(ref next), value = Allocate(ref next);
        rows.Add(text, new(text, name));
        rows.Add(value, new(value, ManagementDirectoryReader.EntryBytesLayout,
            new FrozenArrayState<byte>(new(HeadEntryId.EncodedSize), bytes)));
        entries.Add(new(text, value));
        rows[root] = new(root, ManagementDirectoryReader.BookmarksLayout,
            new FrozenDictionaryState<ObjectId, ObjectId>(DictionaryComparerKind.StringOrdinal,
                entries.OrderBy(item => rows[item.Key].StringContent, StringComparer.Ordinal).ToArray()));
        return Finish(store, accepted, ManagementDirectoryKind.Bookmarks, root, rows);
    }

    private static Dictionary<ObjectId, ObjectStateRecord> Begin(GraphRecordStore store, DecodedGraphRows? accepted,
        ManagementDirectoryKind kind, out ObjectId root, out ulong next) {
        ArgumentNullException.ThrowIfNull(store);
        store.EnsureUsable();
        if (accepted is not null) {
            store.Revalidate(accepted.Snapshot);
            ManagementDirectoryReader.ValidateRows(accepted, kind);
        }
        root = accepted?.Snapshot.RootId ?? new(1);
        next = accepted is null ? 2 : (ulong)accepted.Rows.Keys.Max().Value + 1;
        return accepted?.Rows.ToDictionary() ?? [];
    }

    private static ObjectId Allocate(ref ulong next) => next <= uint.MaxValue ? new((uint)next++)
        : throw new InvalidOperationException("The management ObjectId domain is exhausted.");

    private static PreparedDirectoryWrite Finish(GraphRecordStore store, DecodedGraphRows? accepted,
        ManagementDirectoryKind kind, ObjectId root, Dictionary<ObjectId, ObjectStateRecord> rows) {
        var models = BootstrapBindings.CreateEnvironment();
        List<PreparedObject> objects = [];
        foreach (ObjectStateRecord source in rows.Values) {
            ObjectStateRecord row = source.Kind == ObjectStateKind.String ? source : BootstrapBindings.WithPreparation(source, models);
            PreparedBaseBody body = row.Kind == ObjectStateKind.String ? StringBodyPreparer.PrepareBase(row.StringContent)
                : row.Preparation!.PrepareBase(row);
            EncodedBaseObjectBody typed = new(TypedBaseBodyCodec.Encode(row.Layout, body));
            if (accepted is null || !accepted.Rows.TryGetValue(row.Id, out ObjectStateRecord? before)) {
                objects.Add(PreparedObject.New(row.Id, typed));
            } else if (row.Id == root) {
                objects.Add(PreparedObject.Compared(row.Id, accepted.Snapshot.ObjectHeads[row.Id.Value], typed,
                    row.Preparation!.PrepareDelta(before, row)));
            } else {
                objects.Add(PreparedObject.Unchanged(row.Id, accepted.Snapshot.ObjectHeads[row.Id.Value], typed));
            }
        }
        var plan = ObjectWritePlanner.PlanObjectWrites(objects,
            accepted?.Storage.ToDictionary(pair => pair.Key, pair => pair.Value.ReconstructionPayloadBytes) ?? [], new(5, 5));
        return new(store, kind, accepted, new(accepted?.Snapshot.Address, root, plan.Records));
    }
}

// Carries no HeadEntryId or publication authority. The caller installs only after
// successful publication, within its operation guard, and retains the actual control HeadEntryId.
internal sealed class PreparedDirectoryWrite(GraphRecordStore store, ManagementDirectoryKind kind,
    DecodedGraphRows? expectedBaseline, PreparedGraphWrite write) {
    private DecodedGraphRows? _installation;
    internal PreparedGraphWrite Write { get; } = write;
    internal DecodedGraphRows? ExpectedBaseline { get; } = expectedBaseline;

    internal void PrepareInstall(StoredGraphRevision graph) {
        if (_installation is not null) { throw new InvalidOperationException("Directory installation is already prepared."); }
        StoredGraphRevision actual = store.Revalidate(graph);
        if (actual.Kind != StoredGraphKind.Meta || actual.SchemaRoot is not null || actual.SourceGraph != Write.SourceGraph ||
            actual.Parent != Write.SourceGraph || actual.RootId != Write.RootId) {
            throw new InvalidDataException("Directory installation requires its exact Meta source and root.");
        }
        var records = actual.OwnedBatch is { } batch ? store.ReadBatch(batch).Records : [];
        if (records.Count != Write.Records.Count) { throw new InvalidDataException("Directory candidate records differ."); }
        for (int i = 0; i < records.Count; i++) {
            var a = records[i]; var b = Write.Records[i];
            if (a.ObjectId != b.ObjectId || a.Kind != b.Kind || a.PriorAddress != b.PriorAddress || !a.Body.SequenceEqual(b.Body)) {
                throw new InvalidDataException("Directory candidate records differ.");
            }
        }
        HashSet<uint> members = ExpectedBaseline?.Snapshot.ObjectHeads.Keys.ToHashSet() ?? [];
        members.UnionWith(Write.Records.Select(row => row.ObjectId));
        if (!members.SetEquals(actual.ObjectHeads.Keys)) { throw new InvalidDataException("Directory candidate membership differs."); }
        HashSet<uint> written = Write.Records.Select(row => row.ObjectId).ToHashSet();
        if (ExpectedBaseline is not null) {
            foreach (var pair in ExpectedBaseline.Snapshot.ObjectHeads) {
                if (!written.Contains(pair.Key) && actual.ObjectHeads[pair.Key] != pair.Value) {
                    throw new InvalidDataException("An unchanged directory object head differs.");
                }
            }
        }
        _installation = kind == ManagementDirectoryKind.Branches
            ? ManagementDirectoryReader.ReadBranches(store, actual.Address) : ManagementDirectoryReader.ReadBookmarks(store, actual.Address);
    }

    internal DecodedGraphRows GetInstallation(DecodedGraphRows? currentBaseline) {
        store.EnsureUsable();
        if (!ReferenceEquals(currentBaseline, ExpectedBaseline)) { throw new InvalidOperationException("The directory baseline has advanced."); }
        return _installation ?? throw new InvalidOperationException("Prepare the directory installation before publication.");
    }
}
