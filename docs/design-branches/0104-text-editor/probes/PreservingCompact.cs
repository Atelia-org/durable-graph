using System.Runtime.CompilerServices;
using Atelia.Binary;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Runtime;

// Diagnostic candidate only. Operates on current product sources in the same probe assembly.
internal static class PreservingCompact {
    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    private static extern TextPieceView NewView(List<TextPiece> pieces, int length, int largest, object contentToken);
    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "Add")]
    private static extern void Add(TextPieceView declaringType, List<TextPiece> pieces, TextPiece piece);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "SetView")]
    private static extern void Install(DurableText text, TextPieceView next);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_visitText")]
    private static extern ref string? Cache(DurableText text);

    // Keep each old source offset, but make all physical slices point into a single exact string.
    private static string Compact(DurableText editor, bool installCache) {
        TextPieceView old = editor.CaptureState().Content.View;
        string flat = editor.ToString(); // Reuse a warm immutable buffer where available.
        var packed = new List<TextPiece>();
        int at = 0;
        foreach (TextPiece piece in old.Pieces) {
            Add(old, packed, new TextPiece(flat, at, piece.Length, piece.BaseOffset));
            at += piece.Length;
        }
        var next = NewView(packed, old.Length, flat.Length, old.ContentToken);
        Install(editor, next);
        if (installCache && next.PieceCount > 1) Cache(editor) = flat;
        return flat;
    }

    internal static void Check() {
        foreach (bool bound in new[] { false, true }) {
            foreach (bool warm in new[] { false, true }) {
                var baseline = new DurableText("abcdef").CaptureState();
                var editor = bound ? DurableText.Restore(baseline) : new DurableText("abcdef");
                WeakReference<string> donor = GrowThenTrim(editor);
                RunWithSnapshot(editor, baseline, donor, bound, warm);
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                Need(!IsAlive(donor), "released donor");
                GC.KeepAlive(editor);
            }
        }
        var previous = new DurableText("abXYcd").CaptureState();
        var gap = DurableText.Restore(previous);
        gap.Replace(2, 2, "");
        byte[] before = Delta(previous, gap);
        string exact = Compact(gap, installCache: false);
        Need(gap.CaptureState().Content.View.PieceCount == 2 && Plan(gap) == "C0:2/C4:2", "logical gap");
        byte[] after = Delta(previous, gap);
        Need(before.SequenceEqual(after) && Replay(previous, after) == "abcd", "gap replay");
        // A cold multi-piece view does not automatically derive the query cache from its common backing.
        bool same = gap.VisitSpan(exact, static (s, str) => s.Overlaps(str.AsSpan(), out int o) && o == 0);
        Need(!same, "negative cache witness");
        Console.Error.WriteLine($"preserved-gap plan={Plan(gap)} delta={Convert.ToHexString(after)} replay=abcd cold_without_cache_used_original={same}");
        BaseSize();
        AlternatingSources();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunWithSnapshot(DurableText e, DurableTextState previous, WeakReference<string> donor,
            bool bound, bool warm) {
        if (warm) e.VisitSpan(0, static (s, _) => s.Length);
        var snapshot = e.Snapshot();
        string beforePlan = Plan(e);
        int beforePieces = snapshot.View.PieceCount;
        string? cached = Cache(e);
        byte[] before = Delta(previous, e);
        string flat = Compact(e, installCache: true);
        string afterPlan = Plan(e);
        var afterView = e.CaptureState().Content.View;
        int expectedCount = bound ? 3 : 1;
        string expectedPlan = bound ? "C0:2/N2/C4:2" : "N6";
        Need(afterView.PieceCount == expectedCount && afterPlan == expectedPlan, "origin map");
        Need(afterView.Pieces.All(p => ReferenceEquals(p.Text, flat)) && (!warm || ReferenceEquals(cached, flat)), "one backing");
        bool same = e.VisitSpan(flat, static (s, str) => s.Overlaps(str.AsSpan(), out int o) && o == 0);
        Need(same && snapshot.ToString() == "abXYef" && e.ToString() == "abXYef", "snapshot/cache");
        byte[] after = Delta(previous, e);
        Need(before.SequenceEqual(after) && Replay(previous, after) == "abXYef", "byte-equivalent delta");
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        Need(IsAlive(donor), "snapshot retains donor");
        Need(e.TryApplyEdits(snapshot, [new(4, 1, "Q")], out _), "guard must accept old physical view");
        var twin = bound ? DurableText.Restore(previous) : new DurableText("abcdef");
        twin.Replace(2, 2, "XY");
        twin.Replace(4, 1, "Q");
        byte[] follow = Delta(previous, e);
        Need(follow.SequenceEqual(Delta(previous, twin)) && Replay(previous, follow) == "abXYQf", "further editing");
        Console.Error.WriteLine($"preserved bound={bound} warm={warm} pieces={beforePieces}->{expectedCount} backingBeforeMax={snapshot.View.LargestBacking} afterMax={afterView.LargestBacking} beforePlan={beforePlan} afterPlan={afterPlan} beforeDelta={Convert.ToHexString(before)} afterDelta={Convert.ToHexString(after)} followDelta={Convert.ToHexString(follow)} cacheSame={same} guard=True");
        GC.KeepAlive(snapshot);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<string> GrowThenTrim(DurableText text) {
        var donor = new string('Y', 65536);
        text.Replace(2, 1, "X");
        text.Replace(3, 1, donor);
        text.Replace(3, donor.Length - 1, "");
        return new(donor);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsAlive(WeakReference<string> weak) => weak.TryGetTarget(out _);
    private static byte[] Delta(DurableTextState baseline, DurableText editor) =>
        TextBodyCodec.PrepareDelta(baseline, editor.CaptureState()).Body.ToArray();
    private static string Replay(DurableTextState baseline, byte[] bytes) {
        var reader = new BareValueReader(bytes);
        return TextBodyCodec.ApplyDelta(ref reader, baseline).Content.ToString();
    }
    private static string Plan(DurableText e) =>
        string.Join("/", e.CaptureState().BuildSourcePlan().Select(p => p.IsCopy ? $"C{p.BaseOffset}:{p.Length}" : $"N{p.Length}"));
    private static void Need(bool ok, string label) { if (!ok) throw new Exception(label); }

    private static void AlternatingSources() {
        const int size = 65536;
        var baseline = new DurableText(new string('a', size)).CaptureState();
        var edits = Enumerable.Range(0, 2048).Select(i => new TextEdit(5 + i * 32, 1, "b")).ToArray();
        byte[]? prior = null, follow = null;
        foreach (bool repack in new[] { false, true }) {
            var e = DurableText.Restore(baseline);
            Need(e.TryApplyEdits(e.Snapshot(), edits, out _), "arrange interleaved sources");
            if (repack) Compact(e, installCache: true);
            var view = e.CaptureState().Content.View;
            int n = view.PieceCount;
            long units = view.Pieces.Select(p => p.Text).Distinct<string>(ReferenceEqualityComparer.Instance).Sum(s => (long)s.Length);
            var d = Delta(baseline, e);
            Need(prior is null || prior.SequenceEqual(d), "alternating byte identity");
            prior = d;
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 256; i++) e.Replace(0, 1, i % 2 == 0 ? "c" : "d");
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            var next = Delta(baseline, e);
            Need(follow is null || follow.SequenceEqual(next), "edited alternating byte identity");
            follow = next;
            Need(Replay(baseline, next) == e.ToString(), "alternating replay");
            Console.Error.WriteLine($"alternating repack={repack} pieces={n} current_unique={units} follow256_bytes={allocated} delta_bytes={d.Length}");
        }
    }

    private static void BaseSize() {
        var editor = new DurableText(new string('汉', 1024));
        editor.Replace(editor.Length, 0, new string('a', 1024));
        var before = TextBodyCodec.PrepareBase(editor.CaptureState()).Body.ToArray();
        Compact(editor, installCache: true);
        var after = TextBodyCodec.PrepareBase(editor.CaptureState()).Body.ToArray();
        Need(TextBodyCodec.ReadBase(before).Content.ToString() == TextBodyCodec.ReadBase(after).Content.ToString(), "same Base text");
        Console.Error.WriteLine($"base segmentation bytes={before.Length}->{after.Length} both decoded equal");
    }
}
