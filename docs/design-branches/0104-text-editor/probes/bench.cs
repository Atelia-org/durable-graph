using System.Diagnostics;
using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
#if ORIGINAL
using Atelia.DurableGraph;
using FrozenText=Atelia.DurableGraph.DurableTextState;
using TextEditor=Atelia.DurableGraph.DurableText;
#else
using Atelia.Text;
#endif
var ed = new TextEditor(new string('a', 4096));
for (int i = 1; i <= 16; i++) ed.Replace(i * 96, 1, "X");
var fragments = ed.CaptureState();
var prior = TextBodyCodec.ReadBase(TextBodyCodec.PrepareBase(fragments).Body);
var edit = TextEditor.Restore(prior);
for (int i = 1; i <= 16; i++) edit.Replace(i * 80, 1, "Y");
var changed = edit.CaptureState();
var delta = TextBodyCodec.PrepareDelta(prior, changed).Body.ToArray();
var foreign = TextBodyCodec.ReadBase(TextBodyCodec.PrepareBase(new TextEditor(new string('f', 4096)).CaptureState()).Body);
var generated = Apply(prior, delta);
if (generated.Content.ToString() != edit.ToString()) throw new Exception();
static FrozenText Apply(FrozenText previous, byte[] bytes) {
    BareValueReader reader = new(bytes);
    return TextBodyCodec.ApplyDelta(ref reader, previous);
}
void Run(string name, Func<int> action) {
    int n = 3000, sum = 0;
    for (int i = 0; i < 900; i++) sum += action();
    long[] alloc = new long[5]; double[] ns = new double[5];
    for (int r = 0; r < alloc.Length; r++) {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long startAlloc = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < n; i++) sum += action();
        ns[r] = Stopwatch.GetElapsedTime(start).TotalNanoseconds / n;
        alloc[r] = (GC.GetAllocatedBytesForCurrentThread() - startAlloc) / n;
    }
    Array.Sort(ns); Array.Sort(alloc);
    Console.WriteLine($"{name},{alloc[2]},{ns[2]:F1},{sum}");
}
Run("estimate", () => checked((int)TextBodyCodec.EstimateBaseSize(changed)));
Run("base33", () => TextBodyCodec.PrepareBase(changed).Body.Length);
Run("same", () => TextBodyCodec.PrepareDelta(prior, prior).Body.Length);
Run("delta33", () => TextBodyCodec.PrepareDelta(prior, changed).Body.Length);
Run("foreign33", () => TextBodyCodec.PrepareDelta(foreign, changed).Body.Length);
Run("replay33", () => Apply(prior, delta).Content.Length);
