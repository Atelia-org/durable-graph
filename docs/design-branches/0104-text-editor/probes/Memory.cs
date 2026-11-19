using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Atelia.Binary;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Runtime;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
int rotation = int.Parse(args[0]);
Console.WriteLine("round,policy,stage,elapsed_ms,allocated_B,pieces_at_safe_point,pieces_after,logical_units,unique_backing_units,compactions");
string[] policies = ["none", "once", "boundary128"];
for (int r = -1; r < 3; r++) {
    for (int i = 0; i < policies.Length; i++) Scatter(policies[(i + r + 1 + rotation) % policies.Length], r);
}
Retention();
Provenance();
SparseMaintenance();
PendingCapture();
PreservingCompact.Check();

static void Scatter(string policy, int round) {
    const int length = 65536, writes = 2048;
    var original = new string('a', length);
    var editor = new DurableText(original);
    var old = editor.Snapshot();
    TextSnapshot? middle = null;
    char[] oracle = original.ToCharArray();
    string halfway = "";
    int flattened = 0;
    int preCheck = 0;
    long elapsed = 0, allocated = 0;
    for (int i = 0; i < writes; i++) {
        int at = 5 + (i * 997 % writes) * (length / writes);
        long before = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp();
        editor.Replace(at, 1, "b");
        preCheck = View(editor).PieceCount;
        if (policy == "boundary128" && (i + 1) % 128 == 0 && ShouldCompact(editor)) {
            CompactUnbound(editor);
            flattened++;
        }
        elapsed += Stopwatch.GetTimestamp() - start;
        allocated += GC.GetAllocatedBytesForCurrentThread() - before;
        oracle[at] = 'b';
        if (i + 1 == writes / 2) {
            middle = editor.Snapshot();
            halfway = new string(oracle);
        }
    }
    if (policy == "once") {
        long before = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp();
        CompactUnbound(editor);
        elapsed += Stopwatch.GetTimestamp() - start;
        allocated += GC.GetAllocatedBytesForCurrentThread() - before;
        flattened++;
    }
    if (round >= 0) Row(round, policy, "scatter2048", elapsed, allocated, preCheck, editor, flattened);

    // Simulate hot-spot operations following the safe point. Do not charge future edits to the first phase.
    int count = View(editor).PieceCount;
    long bytes = GC.GetAllocatedBytesForCurrentThread(), ticks = Stopwatch.GetTimestamp();
    for (int i = 0; i < 256; i++) editor.Replace(0, 1, i % 2 == 0 ? "c" : "d");
    long dt = Stopwatch.GetTimestamp() - ticks, db = GC.GetAllocatedBytesForCurrentThread() - bytes;
    oracle[0] = 'd';
    if (round >= 0) Row(round, policy, "follow256", dt, db, count, editor, 0);
    Need(editor.ToString() == new string(oracle) && middle!.ToString() == halfway && old.ToString() == original, "scatter");
}

static void Row(int r, string policy, string stage, long ticks, long bytes, int count, DurableText t, int flattened) =>
    Console.WriteLine($"{r},{policy},{stage},{ticks * 1000d / Stopwatch.Frequency:F4},{bytes},{count},{View(t).PieceCount},{t.Length},{UniqueUnits(t)},{flattened}");
static long UniqueUnits(DurableText editor) => View(editor).Pieces.Select(p => p.Text).Distinct<string>(ReferenceEqualityComparer.Instance).Sum(s => (long)s.Length);

static bool ShouldCompact(DurableText t) {
    var v = View(t);
    return v.PieceCount >= 2 && (long)v.PieceCount * v.PieceCount >= (v.Length + 15L) / 16 ||
           v.LargestBacking > 4L * Math.Max(1, v.Length) && v.LargestBacking - (long)v.Length >= 4096;
}
static void CompactUnbound(DurableText editor) {
    // This is only for editors without graph provenance. Preserve current editor, content token and prior immutable views.
    Need(SourceToken(editor) is null, "standalone prerequisite");
    InstallView(editor, TextPieceView.FromString(editor.ToString(), sourceOffset: -1, View(editor).ContentToken));
}
static void Retention() {
    foreach (var shape in new[] { "slice", "aggregate" }) {
        var (editor, donors) = BuildRetention(shape);
        string output = editor.ToString();
        int expected = shape == "slice" ? 1 : 8;
        if (shape == "slice") Need(output == new string('z', 32), "slice check");
        else Need(output == new string('a', 65536) + "bcdefghi", "aggregate check");
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        Need(Alive(donors) == expected, "before collection");
        Console.Error.WriteLine($"retention {shape} before logical={editor.Length} pieces={View(editor).PieceCount} unique={UniqueUnits(editor)} largest={View(editor).LargestBacking} heuristic={ShouldCompact(editor)} weak={Alive(donors)}");
        VerifyWhileHoldingSnapshot(editor, donors, output);
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        Need(Alive(donors) == 0 && editor.ToString() == output, "release after last snapshot");
        Console.Error.WriteLine($"retention {shape} after logical={editor.Length} pieces={View(editor).PieceCount} unique={UniqueUnits(editor)} weak={Alive(donors)}");
    }
}
[MethodImpl(MethodImplOptions.NoInlining)]
static (DurableText, WeakReference<string>[]) BuildRetention(string shape) {
    if (shape == "slice") {
        var backing = new string('z', 4 * 1024 * 1024);
        var t = new DurableText(backing);
        t.Replace(0, backing.Length - 32, "");
        return (t, [new(backing)]);
    }
    var editor = new DurableText(new string('a', 65536));
    var donors = new WeakReference<string>[8];
    for (int i = 0; i < donors.Length; i++) {
        var backing = new string((char)('b' + i), 65536);
        donors[i] = new(backing);
        int start = editor.Length;
        editor.Replace(start, 0, backing);
        editor.Replace(start, backing.Length - 1, "");
    }
    return (editor, donors);
}
[MethodImpl(MethodImplOptions.NoInlining)]
static void VerifyWhileHoldingSnapshot(DurableText t, WeakReference<string>[] donors, string expected) {
    var snapshot = t.Snapshot();
    CompactUnbound(t);
    GC.Collect(2, GCCollectionMode.Forced, true, true);
    Need(Alive(donors) == donors.Length && snapshot.ToString() == expected, "snapshot retains original backing");
    Need(t.TryApplyEdits(snapshot, [], out _), "same logical version");
    Console.Error.WriteLine($"retention held-snapshot pieces={View(t).PieceCount} current_unique={UniqueUnits(t)} live_old_donors={Alive(donors)} guard=accepted");
    GC.KeepAlive(snapshot);
}
[MethodImpl(MethodImplOptions.NoInlining)]
static int Alive(WeakReference<string>[] refs) => refs.Count(r => r.TryGetTarget(out _));

static void Provenance() {
    foreach (var mode in new[] { "preserve", "blind-zero", "all-new" }) {
        var baseline = new DurableText("abcdef").CaptureState();
        var e = DurableText.Restore(baseline);
        e.Replace(2, 1, "X");
        if (mode != "preserve") InstallView(e, TextPieceView.FromString(e.ToString(), mode == "blind-zero" ? 0 : -1, View(e).ContentToken));
        var current = e.CaptureState();
        var delta = TextBodyCodec.PrepareDelta(baseline, current);
        var reader = new BareValueReader(delta.Body);
        var actual = TextBodyCodec.ApplyDelta(ref reader, baseline).Content.ToString();
        var plan = string.Join("/", current.BuildSourcePlan().Select(p => p.IsCopy ? $"Copy({p.BaseOffset},{p.Length})" : $"New({p.Length})"));
        Need(actual == (mode == "blind-zero" ? "abcdef" : "abXdef"), "replay");
        Console.Error.WriteLine($"provenance {mode} plan={plan} delta_bytes={delta.Body.Length} replay={actual}");
    }
}
static void SparseMaintenance() {
    var t = new DurableText(new string('a', 1_048_576));
    t.Replace(524_288, 1, "b");
    Need(!ShouldCompact(t), "sparse estimate");
    int count = View(t).PieceCount;
    long before = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp();
    CompactUnbound(t);
    long ticks = Stopwatch.GetTimestamp() - start, bytes = GC.GetAllocatedBytesForCurrentThread() - before;
    Console.Error.WriteLine($"sparse forced compact logical={t.Length} before_pieces={count} after={View(t).PieceCount} allocated={bytes} ms={ticks * 1000d / Stopwatch.Frequency:F4}");
    Need(t.ToString()[524_288] == 'b' && View(t).PieceCount == 1, "sparse compact");
}
static void PendingCapture() {
    var e = new DurableText("abcdef");
    e.Replace(2, 1, "X");
    var snapshot = e.Snapshot();
    var capture = e.CaptureState();
    var pending = e.PrepareAcceptance(capture);
    CompactUnbound(e);
    Need(!e.TryInstall(pending), "old pending physical view must not install");
    Need(snapshot.ToString() == "abXdef" && e.TryApplyEdits(snapshot, [], out _) && SourceToken(e) is null, "same logical version");
    Console.Error.WriteLine("pending old-physical=False same-version=True source-bound=False");
}

static void Need(bool ok, string message) { if (!ok) throw new Exception(message); }

[UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_view")]
static extern ref TextPieceView View(DurableText t);
[UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_sourceToken")]
static extern ref object? SourceToken(DurableText t);
[UnsafeAccessor(UnsafeAccessorKind.Method, Name = "SetView")]
static extern void InstallView(DurableText t, TextPieceView next);
