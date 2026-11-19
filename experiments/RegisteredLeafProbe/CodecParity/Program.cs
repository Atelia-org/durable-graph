using System.Security.Cryptography;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Runtime;
using Atelia.Binary;
using System.Buffers.Binary;

var rng = new Random(104);
using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
int cases = 0;
void Check(DurableTextState previous, DurableText editor, string expected) {
    var frozen = editor.CaptureState();
    var delta = TextBodyCodec.PrepareDelta(previous, frozen);
    var reader = new BareValueReader(delta.Body);
    var decoded = TextBodyCodec.FinishReplay(TextBodyCodec.ApplyDelta(ref reader, previous));
    reader.EnsureFullyConsumed();
    if (decoded.Content.ToString() != expected || editor.ToString() != expected)
        throw new InvalidOperationException($"wrong roundtrip for {cases}");
    Span<byte> size = stackalloc byte[5];
    BinaryPrimitives.WriteInt32LittleEndian(size, delta.Body.Length);
    size[4] = delta.HasChanges ? (byte)1 : (byte)0;
    hash.AppendData(size);
    hash.AppendData(delta.Body);
    cases++;
}
for (int sample = 0; sample < 600; sample++) {
    var editor = new DurableText();
    var text = "";
    for (int part = 0; part < rng.Next(1, 30); part++) {
        var suffix = new string((char)rng.Next(0x10000), rng.Next(1,25));
        editor.Replace(text.Length,0,suffix);
        text += suffix;
    }
    var prior = editor.CaptureState();
    editor.TryInstall(editor.PrepareAcceptance(prior));
    Check(prior, editor, text);
    for (int edit = 0; edit < 12; edit++) {
        int pos = rng.Next(text.Length + 1);
        int remove = rng.Next(1 + Math.Min(8, text.Length-pos));
        var literal = new string((char)rng.Next(0x10000), rng.Next(12));
        editor.Replace(pos,remove,literal);
        text = text.Remove(pos,remove).Insert(pos,literal);
        Check(prior,editor,text);
    }
    var foreign = new DurableText(new string('b', text.Length)).CaptureState();
    Check(foreign,editor,text);
}
Console.WriteLine($"replays={cases}; sha256={Convert.ToHexString(hash.GetHashAndReset())}");
