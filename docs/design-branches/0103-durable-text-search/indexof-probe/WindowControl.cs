using System.Buffers;
using Atelia.DurableGraph;

// Diagnostic control on a frozen snapshot. Keep at least half of each full window as forward progress.
internal static class WindowControl {
    internal static int IndexOf(TextSnapshot snapshot, string needle, int start) {
        ArgumentNullException.ThrowIfNull(needle);
        if ((uint)start > (uint)snapshot.Length) { throw new ArgumentOutOfRangeException(nameof(start)); }
        if (needle.Length == 0) { return start; }
        int remaining = snapshot.Length - start;
        if (needle.Length > remaining) { return -1; }

        int capacity = (int)Math.Min(remaining, Math.Max(1_024L, 2L * needle.Length));
        char[] buffer = ArrayPool<char>.Shared.Rent(capacity);
        try {
            int position = start;
            while (true) {
                int length = Math.Min(capacity, snapshot.Length - position);
                var window = buffer.AsSpan(0, length);
                snapshot.CopyTo(position, window);
                int found = ((ReadOnlySpan<char>)window).IndexOf(needle, StringComparison.Ordinal);
                if (found >= 0) { return position + found; }
                if (length == snapshot.Length - position) { return -1; }
                // A match may start in the last needle.Length - 1 units and finish in the next window.
                position += length - needle.Length + 1;
            }
        } finally { ArrayPool<char>.Shared.Return(buffer); }
    }
}
