using Atelia.Binary;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Runtime;

string original = new('a', 65536);
var baseline = new DurableText(original).CaptureState();
foreach (int[] positions in new int[][] { [32768], [1024, 64511], [1024, 1025], [] }) {
    var editor = DurableText.Restore(baseline);
    foreach (int at in positions) { editor.Replace(at, 1, "z"); }
    string current = editor.ToString();
    int left = 0;
    while (left < original.Length && original[left] == current[left]) { left++; }
    int end = original.Length;
    while (end > left && original[end - 1] == current[end - 1]) { end--; }
    var oneRange = DurableText.Restore(baseline);
    oneRange.Replace(left, end - left, current[left..end]);
    int Check(DurableText actual) {
        var delta = TextBodyCodec.PrepareDelta(baseline, actual.CaptureState());
        var reader = new BareValueReader(delta.Body);
        if (TextBodyCodec.ApplyDelta(ref reader, baseline).Content.ToString() != current ||
            delta.HasChanges != (original != current)) { throw new InvalidOperationException(); }
        return delta.Body.Length;
    }
    Console.WriteLine($"{string.Join(',', positions)} | {Check(editor)} | {Check(oneRange)} | {Check(new DurableText(current))}");
}
