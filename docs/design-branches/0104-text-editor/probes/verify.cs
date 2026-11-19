using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.Text;
using System.Buffers;
int passed = 0;
void Check(bool value) { if (!value) throw new Exception("Check " + (passed + 1)); passed++; }
FrozenText Cold(FrozenText text) => TextBodyCodec.ReadBase(TextBodyCodec.PrepareBase(text).Body);
FrozenText Replay(FrozenText previous, ReadOnlySpan<byte> bytes) {
    BareValueReader reader = new(bytes);
    return TextBodyCodec.ApplyDelta(ref reader, previous);
}
FrozenText prior = Cold(new TextEditor("ab").CaptureState());
var editor = TextEditor.Restore(prior);
var snap = editor.Snapshot();
editor.Replace(1, 0, "X"); editor.Replace(2, 0, "Y");
var changed = editor.CaptureState();
var bytes = TextBodyCodec.PrepareDelta(prior, changed).Body.ToArray();
Check(bytes.SequenceEqual(new byte[] { 4, 1, 0, 1, 2, 2, 88, 0, 89, 0, 1, 1, 1 })); // old golden
Check(Replay(prior, bytes).Content.ToString() == "aXYb");
Check(!editor.TryApplyEdits(snap, [], out _));
var current = editor.Snapshot();
Check(editor.TryInstall(editor.PrepareAcceptance(changed)));
Check(editor.TryApplyEdits(current, [], out _));
var other = Cold(new TextEditor("ZZ").CaptureState());
var foreign = TextBodyCodec.PrepareDelta(other, changed).Body.ToArray();
Check(foreign.SequenceEqual(new byte[] { 4, 2, 4, 97, 0, 88, 0, 89, 0, 98, 0 }));
Check(Replay(other, foreign).Content.ToString() == "aXYb");
var stale = editor.PrepareAcceptance(editor.CaptureState());
editor.Replace(0, 1, "A");
Check(!editor.TryInstall(stale));
var after = editor.CaptureState();
var now = TextBodyCodec.PrepareDelta(changed, after).Body.ToArray();
Check(Replay(changed, now).Content.ToString() == "AXYb");
var split = new TextEditor("\ud83d"); split.Replace(1, 0, "\ude00");
Check(TextBodyCodec.PrepareBase(split.CaptureState()).Body.SequenceEqual(new byte[] { 2, 2, 0x3d, 0xd8, 2, 0, 0xde }));
var many = new TextEditor("x");
for (int i = 1; i < 4100; i++) many.Replace(i, 0, i % 2 == 0 ? "x" : "y");
var fragmented = many.CaptureState(); Check(fragmented.ChunkCount == 4100);
var output = new ArrayBufferWriter<byte>(); BareValueWriter writer = new(output);
writer.WriteVarUInt32(4100); writer.WriteByte(1); writer.WriteVarUInt32(0); writer.WriteVarUInt32(4100);
var flat = Replay(fragmented, output.WrittenSpan);
Check(flat.ChunkCount == 1 && flat.Content.ToString() == many.ToString());
var large = Cold(new TextEditor(new string('z', 9000)).CaptureState());
var single = Replay(large, new byte[] { 1, 1, 2, 1 });
Check(single.Chunk(0).Length == 1 && single.Content.ToString() == "z");
Check(!ReferenceEquals(single, TextBodyCodec.FinishReplay(single)));
var random = new Random(915623);
for (int i = 0; i < 80; i++) {
    string before = new string((char)('a' + i % 10), random.Next(2, 40));
    var p = Cold(new TextEditor(before).CaptureState());
    var e = TextEditor.Restore(p); var expected = before;
    for (int j = 0; j < 7; j++) { int off = random.Next(expected.Length + 1); int take = random.Next(expected.Length - off + 1); string insert = new string((char)random.Next(65536), random.Next(4)); e.Replace(off, take, insert); expected = expected.Remove(off, take).Insert(off, insert); }
    var target = e.CaptureState();
    Check(Replay(p, TextBodyCodec.PrepareDelta(p, target).Body).Content.ToString() == expected);
}
var builder = new ReplayText(2);
var alias = builder;
builder.AppendLiteral("ab");
var sealedText = builder.Finish(2);
try { alias.AppendLiteral("changed"); Check(false); } catch (InvalidOperationException) { Check(true); }
try { alias.Finish(2); Check(false); } catch (InvalidOperationException) { Check(true); }
Check(sealedText.ChunkCount == 1 && sealedText.Content.ToString() == "ab");
var over = new ReplayText(2);
var partialAlias = over;
Check(!over.TryCopy(fragmented, 0, 3));
try { partialAlias.Finish(2); Check(false); } catch (InvalidOperationException) { Check(true); }
var full = new ReplayText(1);
full.AppendLiteral("X");
try { full.AppendLiteral("Y"); Check(false); } catch (InvalidOperationException) { Check(true); }
try { full.Finish(1); Check(false); } catch (InvalidOperationException) { Check(true); }
Console.WriteLine(passed + " assertions passed; independent Core+Adapter");
