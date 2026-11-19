using Atelia.DurableGraph;
using Atelia.DurableGraph.Runtime;
using System.Runtime.InteropServices;
using System.Text;

internal static class Probe {
    private readonly record struct SourceFreePiece(string Text, int Offset, int Length);
    private readonly record struct Block(string Backing, int Local, int Length, int Logical);
    private sealed record Run(int Source, int Length, string? Literal) {
        public int End => Source + Length;
    }
    private sealed record IntervalIndex(Block[] Begins, Block[] MaxEnd) {
        public static IntervalIndex Build(List<Block> blocks) {
            Block[] starts = blocks.OrderBy(b => b.Local).ToArray();
            Block[] max = new Block[starts.Length];
            for (int i = 0; i < starts.Length; i++) {
                max[i] = i != 0 && End(max[i - 1]) >= End(starts[i]) ? max[i - 1] : starts[i];
            }
            return new(starts, max);
        }
        public (int Source, int Length) Find(int position, int remaining) {
            int lo = 0, hi = Begins.Length;
            while (lo < hi) {
                int mid = lo + (hi - lo) / 2;
                if (Begins[mid].Local <= position) lo = mid + 1;
                else hi = mid;
            }
            if (lo > 0 && End(MaxEnd[lo - 1]) > position) {
                Block hit = MaxEnd[lo - 1];
                return (hit.Logical + position - hit.Local, Math.Min(remaining, End(hit) - position));
            }
            return (-1, lo == Begins.Length ? remaining : Math.Min(remaining, Begins[lo].Local - position));
        }
        private static int End(Block b) => b.Local + b.Length;
    }
    private static Block[] Chunks(TextSnapshot snapshot) {
        int logical = 0;
        return snapshot.GetChunks().Select(m => {
            if (!MemoryMarshal.TryGetString(m, out string? s, out int i, out int n) || s is null) throw new Exception("expected owned string slice");
            Block b = new(s, i, n, logical); logical += n; return b;
        }).ToArray();
    }
    private static List<Run> Plan(TextSnapshot source, TextSnapshot target) {
        Block[] old = Chunks(source), cur = Chunks(target);
        int prefix = Common(old, cur, Math.Min(source.Length, target.Length), false);
        int suffix = Common(old, cur, Math.Min(source.Length, target.Length) - prefix, true);
        List<Run> ops = [];
        Add(ops, 0, prefix, null);
        if (prefix + suffix < target.Length) {
            var groups = new Dictionary<string, List<Block>>(ReferenceEqualityComparer.Instance);
            foreach (var block in old) {
                if (!groups.TryGetValue(block.Backing, out var list)) groups[block.Backing] = list = [];
                list.Add(block);
            }
            var indexes = new Dictionary<string, IntervalIndex>(ReferenceEqualityComparer.Instance);
            foreach (var kv in groups) indexes[kv.Key] = IntervalIndex.Build(kv.Value);
            foreach (Block b in cur) {
                int pos = Math.Max(prefix, b.Logical), end = Math.Min(target.Length - suffix, b.Logical + b.Length);
                while (pos < end) {
                    int local = b.Local + pos - b.Logical;
                    (int src, int n) = indexes.TryGetValue(b.Backing, out var ix) ? ix.Find(local, end - pos) : (-1, end - pos);
                    Add(ops, src, n, src < 0 ? b.Backing.Substring(local, n) : null);
                    pos += n;
                }
            }
        }
        Add(ops, source.Length - suffix, suffix, null);
        string s = source.ToString(); StringBuilder replay = new();
        foreach (Run op in ops) {
            if (op.Source < 0) replay.Append(op.Literal);
            else replay.Append(s, op.Source, op.Length);
        }
        if (!string.Equals(replay.ToString(), target.ToString(), StringComparison.Ordinal)) throw new Exception("incorrect replay");
        return ops;
    }
    private static int Common(Block[] left, Block[] right, int limit, bool reverse) {
        int li = reverse ? left.Length - 1 : 0, ri = reverse ? right.Length - 1 : 0;
        int done = 0, la = 0, ra = 0;
        while (done < limit) {
            Block x = left[li], y = right[ri];
            int len = Math.Min(limit - done, Math.Min(x.Length - la, y.Length - ra));
            int lx = reverse ? x.Local + x.Length - 1 - la : x.Local + la;
            int ry = reverse ? y.Local + y.Length - 1 - ra : y.Local + ra;
            int dir = reverse ? -1 : 1;
            if (!ReferenceEquals(x.Backing, y.Backing) || lx != ry) {
                for (int i = 0; i < len; i++) if (x.Backing[lx + i * dir] != y.Backing[ry + i * dir]) return done + i;
            }
            done += len; la += len; ra += len;
            if (la == x.Length) { li += dir; la = 0; }
            if (ra == y.Length) { ri += dir; ra = 0; }
        }
        return done;
    }
    private static void Add(List<Run> ops, int src, int n, string? lit) {
        if (n == 0) return;
        if (ops.Count > 0 && ops[^1] is var last && ((src >= 0 && last.Source >= 0 && src == last.End) || (src < 0 && last.Source < 0))) {
            ops[^1] = new(last.Source, last.Length + n, src < 0 ? last.Literal + lit : null);
        } else ops.Add(new(src, n, lit));
    }
    private static int Var(int v) => v < 128 ? 1 : v < 16384 ? 2 : v < 2097152 ? 3 : v < 268435456 ? 4 : 5;
    private static void Show(string label, DurableTextState prior, DurableTextState next) {
        var ops = Plan(prior.Content, next.Content);
        int copy = ops.Where(x => x.Source >= 0).Sum(x => x.Length), literal = next.Content.Length - copy;
        int bytes = Var(next.Content.Length) + ops.Sum(o => 1 + Var(o.Length) + (o.Source >= 0 ? Var(o.Source) : 2 * o.Length));
        var actual = TextBodyCodec.PrepareDelta(prior, next);
        Console.WriteLine($"{label,-32} oldPieces={prior.Content.View.PieceCount,3} newPieces={next.Content.View.PieceCount,3} altCopy={copy,4} altNew={literal,4} altOps={ops.Count,3} altBytes={bytes,5} DGbytes={actual.Body.Length,5}");
    }
    private static DurableTextState Aliases(string backing, params (int start, int length)[] spans) {
        List<TextPiece> p = spans.Select(s => new TextPiece(backing, s.start, s.length, -1)).ToList();
        return new(new TextSnapshot(TextPieceView.FromOwnedPieces(p, p.Sum(x => x.Length))));
    }
    public static void Main() {
        Console.WriteLine($"inline descriptor bytes: current {System.Runtime.CompilerServices.Unsafe.SizeOf<TextPiece>()}, source-free {System.Runtime.CompilerServices.Unsafe.SizeOf<SourceFreePiece>()}");
        var editor = new DurableText();
        for (int i = 0; i < 32; i++) editor.Replace(editor.Length, 0, new string((char)('A' + i), 128));
        var frozen = editor.CaptureState();
        if (!editor.TryInstall(editor.PrepareAcceptance(frozen))) throw new Exception("install failed");
        var normalized = editor.CaptureState();
        Console.WriteLine($"saved source pieces {frozen.Content.View.PieceCount}; editor pieces {normalized.Content.View.PieceCount}; same token {ReferenceEquals(frozen.ContentToken, normalized.ContentToken)}");
        editor.Replace(1, 1, "!");
        var first = editor.CaptureState();
        Show("one-edit-captured", frozen, first);
        editor.Replace(editor.Length - 2, 1, "?");
        var second = editor.CaptureState();
        Show("two-edits-captured", frozen, second);
        Show("two-edits-normalized", normalized, second);
        var bytes = TextBodyCodec.PrepareBase(frozen).Body.ToArray();
        var cold = TextBodyCodec.ReadBase(bytes);
        var restored = DurableText.Restore(cold);
        restored.Replace(1, 1, "!"); restored.Replace(restored.Length - 2, 1, "?");
        Show("two-edits-shared-cold-restore", cold, restored.CaptureState());
        Show("two-edits-independent-cold-read", TextBodyCodec.ReadBase(bytes), restored.CaptureState());
        var graphEditor = new DurableText(new string('o', 2048));
        var a = graphEditor.CaptureState();
        graphEditor.TryInstall(graphEditor.PrepareAcceptance(a));
        graphEditor.Replace(100, 1, "A");
        var b = graphEditor.CaptureState();
        graphEditor.TryInstall(graphEditor.PrepareAcceptance(b));
        graphEditor.Replace(1900, 1, "B");
        Show("other-graph-A", a, graphEditor.CaptureState());
        Show("current-graph-B", b, graphEditor.CaptureState());
        var s = new string("abcdefghijklmnop".ToCharArray());
        Show("overlapping-repeated-gaps", Aliases(s, (0, 4), (8, 3), (1, 3), (0, 4)), Aliases(s, (7, 4), (0, 4), (4, 3), (0, 4)));
        var rand = new Random(104);
        for (int i = 0; i < 2000; i++) {
            var bas = Enumerable.Range(0, rand.Next(0, 25)).Select(_ => { int n = rand.Next(1, 8); return (rand.Next(17 - n), n); }).ToArray();
            var tgt = Enumerable.Range(0, rand.Next(0, 25)).Select(_ => { int n = rand.Next(1, 8); return (rand.Next(17 - n), n); }).ToArray();
            _ = Plan(Aliases(s, bas).Content, Aliases(s, tgt).Content);
        }
        Console.WriteLine("2000 generated overlapping/duplicate/gap one-backing pairs replayed correctly (seed 104).");
    }
}
