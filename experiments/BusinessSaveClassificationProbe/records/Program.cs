using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Atelia.Binary;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;
using Atelia.FrameStore;

var options = new JsonSerializerOptions { WriteIndented = true };
var codec = new ProbeCodec();
StateModelRegistry Models() {
    var models = new StateModelRegistry();
    models.Register(Atelia.DurableGraph.Generated.Family_44423130362E50726F62652E5375626A656374.Definition);
    models.RegisterLeaf(codec);
    return models;
}
void Require(bool ok, string reason) { if (!ok) throw new Exception(reason); }
string? Addr(FrameAddress? a) {
    if (a is not {} address) return null;
    Span<byte> raw = stackalloc byte[FrameAddress.EncodedSize];
    Require(address.TryWrite(raw), "bad address");
    return Convert.ToHexString(raw);
}
var command = args[0];
string path = args[1];
if (command == "seed") {
    using (Repository.CreateNew(path, Models())) {}
    return;
}
var output = args[2];
var refs = new List<GraphRef>();
var trajectory = new List<object>();
var cold = new List<object>();
string[] titles = ["seed title", "rewritten title"];
(byte A, byte B, int Value, int Name)[] expected = [(10,40,31,0),(10,40,31,0),(11,41,31,0),(11,41,31,0),(12,41,1001,0),(12,41,1001,1),(12,41,1001,1),(12,41,1001,1)];
var labels = new Dictionary<uint,string>();
string[] operations = ["initial", "unchanged", "changed-to-null-and-delta", "unchanged-pair-forces-base", "delta-and-sparse-list", "replace-string", "new-equal-string-instance", "final-unchanged"];
List<int> InitList() => Enumerable.Range(0, 128).ToList();
object Values(Subject s, int i) {
    var e = expected[i];
    var list = InitList();
    list[31] = e.Value;
    Require(s.A.Value == e.A && s.B.Value == e.B && s.Numbers.SequenceEqual(list) && s.Caption == titles[e.Name], $"bad values at {i}");
    return new { a = s.A.Value, b = s.B.Value, sparseValue = s.Numbers[31], listLength = s.Numbers.Count, text = s.Caption };
}
using (var repo = Repository.OpenExisting(path, Models())) {
    var subject = new Subject { A = new() {Value = 10}, B = new() { Value = 40 }, Numbers = InitList(), Caption = titles[0] };
    using var builder = repo.CreateGraph(subject);
    var resources = (RepositoryResources)typeof(Repository).GetField("_resources", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(repo)!;
    IReadOnlyDictionary<uint,FrameAddress> old = new Dictionary<uint,FrameAddress>();
    for (int i = 0; i < expected.Length; i++) {
        var e = expected[i];
        subject.A.Value = e.A;
        subject.B.Value = e.B;
        subject.Numbers[31] = e.Value;
        if (i <= 5) subject.Caption = titles[e.Name];
        if (i == 6) subject.Caption = new string(titles[e.Name].ToCharArray());
        codec.Pairs.Clear();
        GraphRef saved = repo.SaveGraph(builder);
        refs.Add(saved);
        var stored = repo.GraphStore.ReadBusinessGraph(resources.ReadHeapDescriptor(saved.CommitId.Value).Address);
        var batch = stored.OwnedBatch is {} own ? repo.GraphStore.ReadBatch(own) : null;
        IReadOnlyList<ObjectVersionRecord> records = batch?.Records ?? [];
        var rows = new List<object>();
        var map = builder.Business.Baseline!.Objects;
        foreach (var (id, head) in stored.ObjectHeads) {
            var normalized = map[new(id)];
            if (!labels.ContainsKey(id)) {
                var type = normalized.Model.DomainType;
                labels[id] = type == typeof(Cell) ? (records.Single(r => r.ObjectId == id).Body[^256] == 10 ? "leafA" : "leafB") :
                    type == typeof(List<int>) ? "list" : type == typeof(string) ? "string" : type == typeof(Subject) ? "root" : throw new Exception($"Unexpected type {type}");
            }
            var chain = repo.GraphStore.ReadObjectChain(stored, id);
            var installed = normalized.Storage!.Value;
            Require(chain.Head == installed.Head && head == chain.Head && chain.ReconstructionPayloadBytes == installed.ReconstructionPayloadBytes, "installed H/head differ from cold chain facts");
            rows.Add(new { id, role = labels[id], head = Addr(head), H = chain.ReconstructionPayloadBytes, chainLength = chain.Entries.Count });
        }
        var more = new List<object>();
        var writes = new List<string>();
        foreach (var r in records) {
            Require(r.Kind == ObjectVersionKind.Delta ? old.TryGetValue(r.ObjectId, out var prior) && prior == r.PriorAddress : r.PriorAddress is null, "incorrect prior");
            string role = labels[r.ObjectId];
            writes.Add(role + "/" + r.Kind);
            more.Add(new { id = r.ObjectId, role, kind = r.Kind.ToString(), body = Convert.ToHexString(r.Body), prior = Addr(r.PriorAddress), payloadBytes = r.EncodedPayloadBytes });
        }
        string[][] want = [
            ["root/Base","leafA/Base","leafB/Base","list/Base","string/Base"],[],
            ["leafA/Base","leafB/Delta"],["leafA/Base"],["leafA/Delta","list/Delta"],[],[],[]
        ];
        if (i is 5 or 6) Require(writes.Count == 2 && writes.Contains("string/Base") && writes.Any(k => k.StartsWith("root/")), "string replacement wrote wrong records: " + string.Join(",",writes));
        else Require(writes.Order().SequenceEqual(want[i].Order()), "wrong records for " + operations[i] + ": " + string.Join(",",writes));
        Require((records.Count == 0) == (stored.OwnedBatch is null), "wrong empty batch");
        trajectory.Add(new { stage = operations[i], source = Addr(batch?.SourceGraph), graph = Addr(stored.Address), batch = Addr(stored.OwnedBatch),
            pairs = codec.Pairs.ToArray(), rows, records = more, removedIds = old.Keys.Except(stored.ObjectHeads.Keys).Order().ToArray() });
        old = stored.ObjectHeads;
    }
}
using (var repo = Repository.OpenReadOnlyExisting(path, Models())) {
    for (int i = 0; i < refs.Count; i++) {
        using var restored = repo.OpenGraph(refs[i]);
        cold.Add(Values((Subject)restored.Root, i));
        var observed = JsonSerializer.SerializeToElement(trajectory[i]);
        var resources = (RepositoryResources)typeof(Repository).GetField("_resources", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(repo)!;
        var stored = repo.GraphStore.ReadBusinessGraph(resources.ReadHeapDescriptor(refs[i].CommitId.Value).Address);
        Require(Addr(stored.Address) == observed.GetProperty("graph").GetString(), "cold graph address mismatch");
        Require(Addr(stored.OwnedBatch) == observed.GetProperty("batch").GetString(), "cold batch address mismatch");
        var batch = stored.OwnedBatch is {} address ? repo.GraphStore.ReadBatch(address) : null;
        Require(Addr(batch?.SourceGraph) == observed.GetProperty("source").GetString(), "cold source address mismatch");
        IReadOnlyList<ObjectVersionRecord> records = batch?.Records ?? [];
        var expectedRecords = observed.GetProperty("records");
        Require(records.Count == expectedRecords.GetArrayLength(), "cold record count mismatch");
        for (int n = 0; n < records.Count; n++) {
            var fact = expectedRecords[n];
            var real = records[n];
            Require(real.ObjectId == fact.GetProperty("id").GetUInt32() && real.Kind.ToString() == fact.GetProperty("kind").GetString()
                && Convert.ToHexString(real.Body) == fact.GetProperty("body").GetString()
                && Addr(real.PriorAddress) == fact.GetProperty("prior").GetString()
                && real.EncodedPayloadBytes == fact.GetProperty("payloadBytes").GetInt32(), "cold record mismatch");
        }
        var listing = observed.GetProperty("rows");
        Require(stored.ObjectHeads.Count == listing.GetArrayLength(), "cold directory count mismatch");
        foreach (var row in listing.EnumerateArray()) {
            uint id = row.GetProperty("id").GetUInt32();
            var chain = repo.GraphStore.ReadObjectChain(stored, id);
            Require(Addr(chain.Head) == row.GetProperty("head").GetString()
                && chain.Head == stored.ObjectHeads[id] && chain.ReconstructionPayloadBytes == row.GetProperty("H").GetInt64()
                && chain.Entries.Count == row.GetProperty("chainLength").GetInt32(), "cold chain or directory mismatch");
        }
    }
}
var assemblies = AppDomain.CurrentDomain.GetAssemblies().Where(a => a != Assembly.GetExecutingAssembly() && !a.IsDynamic &&
    (a.GetName().Name!.StartsWith("Atelia.") || a.GetName().Name == "K4os.Compression.LZ4")).OrderBy(a=>a.GetName().Name).Select(a => new {
    name = a.GetName().Name, location = a.Location, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(a.Location))),
    revision = a.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion });
File.WriteAllText(Path.Combine(output,"loaded.json"), JsonSerializer.Serialize(assemblies, options));
File.WriteAllText(Path.Combine(output,"records.json"), JsonSerializer.Serialize(new { trajectory, cold }, options));

[DurableType("DB106.Probe.Subject",1)]
public sealed partial class Subject : IDurableObject {
    [DurableField(1)] public Cell A = new();
    [DurableField(2)] public Cell B = new();
    [DurableField(3)] public List<int> Numbers = [];
    [DurableField(4)] public string Caption = "";
}
[DurableLeaf("DB106.Probe.Cell",1)]
public sealed class Cell { public byte Value; }
public sealed class ProbeCodec : DurableLeafCodec<Cell,byte> {
    public List<string> Pairs = [];
    public override byte Capture(Cell value, LeafAcceptance? acceptance) => value.Value;
    public override Cell Restore(byte value) => new() {Value=value};
    public override long? EstimateBaseSize(byte value) => 256;
    public override PreparedBaseBody PrepareBase(byte value) {
        var bytes = new byte[256];
        bytes[0] = value;
        return new(bytes);
    }
    public override PreparedDeltaBody? PrepareDelta(byte previous, byte current) {
        bool mandatory = current == 11 && previous is 10 or 11;
        Pairs.Add($"{previous}->{current}:{(mandatory ? "base" : previous == current ? "unchanged" : "delta")}");
        return mandatory ? null : new(previous != current, [current, 0]);
    }
    public override byte ReadBase(int version, ref BareValueReader reader) {
        byte value = reader.ReadByte();
        if (reader.ReadRawBytes(255).IndexOfAnyExcept((byte)0) >= 0) throw new InvalidDataException("bad filler");
        return value;
    }
    public override byte ApplyDelta(int version, ref BareValueReader reader, byte previous) {
        byte value = reader.ReadByte();
        if(reader.ReadByte()!=0) throw new InvalidDataException("bad guard");
        return value;
    }
}
