namespace Atelia.Text;

internal static class TextEditBatch {
    // On success the first count entries are ready for the piece table's forward pass.
    internal static TextEditFailure Prepare(TextEdit[] batch, int length, out int count, out int nextLength) {
        long total = length;
        bool ordered = true;
        int previousEnd = 0;
        count = 0;
        nextLength = length;
        for (int i = 0; i < batch.Length; i++) {
            TextEdit edit = batch[i];
            if (edit.Replacement is null) { return new(TextEditFailureKind.MissingReplacement, i); }
            if (edit.Start < 0 || edit.Start > length) { return new(TextEditFailureKind.InvalidStart, i); }
            if (edit.Length < 0 || edit.Length > length - edit.Start) { return new(TextEditFailureKind.InvalidLength, i); }
            if (IsEmpty(edit)) { continue; }
            if (edit.Start < previousEnd) { ordered = false; }
            previousEnd = edit.Start + edit.Length;
            total += (long)edit.Replacement.Length - edit.Length;
            count++;
        }

        if (!ordered) {
            var failure = OrderAndCheckConflicts(batch, count);
            if (failure.Kind != TextEditFailureKind.None) { return failure; }
        } else if (count != 0 && count != batch.Length) {
            int write = 0;
            foreach (var edit in batch) {
                if (!IsEmpty(edit)) { batch[write++] = edit; }
            }
        }
        if (total > int.MaxValue) { return new(TextEditFailureKind.ResultTooLong); }
        nextLength = (int)total;
        return default;
    }

    private static bool IsEmpty(TextEdit edit) => edit.Length == 0 && edit.Replacement.Length == 0;

    private static TextEditFailure OrderAndCheckConflicts(TextEdit[] batch, int count) {
        var indexed = new (TextEdit Edit, int Index)[count];
        int write = 0;
        for (int i = 0; i < batch.Length; i++) {
            if (!IsEmpty(batch[i])) { indexed[write++] = (batch[i], i); }
        }
        Array.Sort(indexed, static (a, b) => {
            int order = a.Edit.Start.CompareTo(b.Edit.Start);
            if (order != 0) { return order; }
            // Insertions come before the removal at a common start and retain input order.
            order = (a.Edit.Length != 0).CompareTo(b.Edit.Length != 0);
            return order != 0 ? order : a.Index.CompareTo(b.Index);
        });
        int previousEnd = 0;
        for (int i = 0; i < indexed.Length; i++) {
            var (edit, index) = indexed[i];
            if (edit.Start < previousEnd) {
                return new(TextEditFailureKind.ConflictingEdits, index, indexed[i - 1].Index);
            }
            previousEnd = edit.Start + edit.Length;
            batch[i] = edit;
        }
        return default;
    }
}
