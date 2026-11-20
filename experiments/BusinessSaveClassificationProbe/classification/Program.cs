using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Atelia.Binary;
using Atelia.Data;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.FrameStore;
using Classification;

const int loops = 4096;
const int samples = 5;
using var process = Process.GetCurrentProcess();
if (OperatingSystem.IsWindows() && args.Length != 0 && args[0] != "--check") process.ProcessorAffinity = (nint)Convert.ToInt64(args[0],16);
var fixture = Fixture.Build();
var scratch = new PreparedObject[fixture.Length];
Classify(fixture, scratch, 1);
var kinds = scratch.GroupBy(r => r.ChangeKind).ToDictionary(g => g.Key.ToString(), g => g.Count());
if (kinds.GetValueOrDefault("Insert") != 1 || kinds.GetValueOrDefault("Update") != 33 ||
    kinds.GetValueOrDefault("NoChange") != 98 || kinds.GetValueOrDefault("BaseOnlyUpdate") != 65) throw new Exception(JsonSerializer.Serialize(kinds));
using var evidence = new MemoryStream();
using (var writer = new BinaryWriter(evidence, System.Text.Encoding.UTF8, true)) {
    for (int i = 0; i < scratch.Length; i++) {
        var result = scratch[i];
        var input = fixture[i];
        if (result.ObjectId != input.Row.Current.Id || result.PriorAddress != input.Prior ||
            !ReferenceEquals(result.TypedBase, input.Body) ||
            !ReferenceEquals(result.DeltaBody, input.Forced ? null : input.Row.DeltaBody)) throw new Exception("Result does not represent the actual input");
        writer.Write(result.ObjectId.Value);
        writer.Write((byte)result.ChangeKind);
        writer.Write(input.Forced);
        writer.Write(result.PriorAddress.HasValue);
        var body = result.TypedBase.Encode();
        writer.Write(body.Length);
        writer.Write(body);
        writer.Write(result.DeltaBody is not null);
        if (result.DeltaBody is { } delta) {
            writer.Write(delta.HasChanges);
            writer.Write(delta.Body.Length);
            writer.Write(delta.Body);
        }
    }
}
string fixtureHash = Convert.ToHexString(SHA256.HashData(evidence.ToArray()));
if (args.Contains("--check")) { Console.WriteLine(JsonSerializer.Serialize(new {fixtureHash,kinds})); return; }
ulong expected = scratch.Aggregate(0UL, (sum,r) => sum + ((uint)r.ChangeKind + 1) * r.ObjectId.Value);
if(Classify(fixture, scratch, 1024) != expected * 1024) throw new Exception("Warm-up");
var output = new List<object>();
for (int i = 0; i < samples; i++) {
    GC.Collect(2, GCCollectionMode.Forced, blocking: true);
    long startBytes = GC.GetAllocatedBytesForCurrentThread();
    long start = Stopwatch.GetTimestamp();
    ulong sum = Classify(fixture, scratch, loops);
    long ticks = Stopwatch.GetTimestamp() - start;
    long bytes = GC.GetAllocatedBytesForCurrentThread() - startBytes;
    if (sum != expected * loops) throw new Exception("Classifier checksum diverged");
    output.Add(new { ticks, bytes, sum });
}
object Identity(Type type) {
    var assembly = type.Assembly;
    return new { version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion,
        sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))) };
}
Console.WriteLine(JsonSerializer.Serialize(new { fixtureHash, rows=fixture.Length, loops, kinds, frequency=Stopwatch.Frequency, expected,
    framework = RuntimeInformation.FrameworkDescription, affinity = OperatingSystem.IsWindows() ? process.ProcessorAffinity.ToInt64().ToString("X") : null,
    tiered = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"), runtime=Identity(typeof(CaptureSession)),
    persistence=Identity(typeof(Repository)), harness=Identity(typeof(Fixture)), samples=output }));

[MethodImpl(MethodImplOptions.NoInlining)]
static ulong Classify(Input[] inputs, PreparedObject[] scratch, int loops) {
    ulong sum = 0;
    for (int round = 0; round < loops; round++) {
        for (int i = 0; i < inputs.Length; i++) {
            ref readonly var item = ref inputs[i];
            var row = ObjectWritePlanner.ClassifyCaptured(item.Row, item.Body, item.Prior, item.Forced);
            scratch[i] = row;
            sum += ((uint)row.ChangeKind + 1) * row.ObjectId.Value;
        }
    }
    return sum;
}

namespace Classification {
    internal readonly record struct Input(PreparedCapturedObject Row, PreparedTypedBase Body, FrameAddress? Prior, bool Forced);
    [DurableType("Classification.Root", 1)]
    public sealed partial class Domain : IDurableObject {
        [DurableField(1)] public List<string> Text = [];
        [DurableField(2)] public List<BaseLeaf> Leaves = [];
        [DurableField(3)] public List<List<int>> Values = [];
    }
    [DurableLeaf("Classification.BaseLeaf")]
    public sealed class BaseLeaf { public byte Value; }
    internal sealed class BaseLeafCodec : DurableLeafCodec<BaseLeaf, byte> {
        public override byte Capture(BaseLeaf value, LeafAcceptance? acceptance) => value.Value;
        public override BaseLeaf Restore(byte state) => new() { Value=state };
        public override PreparedBaseBody PrepareBase(byte state) => new([state]);
        public override byte ReadBase(int version, ref BareValueReader reader) => reader.ReadByte();
    }
    internal static class Fixture {
        internal static Input[] Build() {
            var registry = new StateModelRegistry();
            registry.Register(Atelia.DurableGraph.Generated.Family_436C617373696669636174696F6E2E526F6F74.Definition);
            registry.RegisterLeaf(new BaseLeafCodec());
            var models = registry.CreateEnvironment();
            if (!models.TryGetCurrentModel(typeof(Domain), out var root)) throw new Exception("Missing root");
            var live = new Domain();
            for(int i = 0; i < 64; i++) {
                live.Text.Add($"note-{i:D2}");
                live.Leaves.Add(new BaseLeaf { Value=(byte)i });
                live.Values.Add([i, 100, 33]);
            }
            var session = new CaptureSession();
            using var first = session.BeginCapture(models);
            root!.AddRoot(first, live);
            var before = first.Seal();
            session.Accept(before);
            for (int i=1; i<64; i+=2) live.Values[i][1] = 101;
            live.Text.Add("new string");
            using var second = session.BeginCapture(models);
            root.AddRoot(second, live);
            var candidate = second.Seal();
            var rows = session.PrepareAgainst(candidate, before.Objects.ToDictionary(o => o.Id));
            var catalog = new SchemaCatalogWorkspace(static () => { }).PrepareOwned(candidate.Objects.Select(o => o.Layout)).View;
            var header = new TypedBaseBodyCodec.StatePreparation(catalog);
            uint forced = rows.First(r=>r.Current.Layout.Type == TypeExpr.List(TypeExpr.Builtin(TypeTag.Int32)) && r.DeltaBody is { HasChanges:false }).Current.Id.Value;
            var inputs = rows.Select(r=>new Input(r,header.Prepare(r),r.Previous is not null ? Address(r.Current.Id.Value) : null,r.Current.Id.Value==forced)).ToArray();
            new Random(106).Shuffle(inputs);
            return inputs;
        }
        private static FrameAddress Address(uint id) {
            Span<byte> b = stackalloc byte[FrameAddress.EncodedSize];
            BinaryPrimitives.WriteUInt32LittleEndian(b, 1);
            BinaryPrimitives.WriteUInt64LittleEndian(b[4..], SizedPtr.Create(128 + 32L*id, 32).Packed);
            return FrameAddress.TryRead(b, out var a) ? a : throw new Exception("Invalid stand-in prior");
        }
    }
}
