using Atelia.Binary;
using System.Buffers;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;

namespace PackageConsumerProbe;

[DurableType("package.capture-base", 1)]
public abstract partial class CaptureBase : IDurableObject {
    [DurableField(1)] private string _name;

    protected CaptureBase(string name) { _name = name; }
}

[DurableType("package.capture-hero", 1)]
public sealed partial class CaptureHero : CaptureBase {
    [DurableField(1)] private string _alias;
    [DurableField(2)] private int _score = 7;

    public CaptureHero(string name) : base(name) { _alias = name; }
    public void ChangeAlias(string alias) { _alias = alias; _score = 90; }
}

[DurableType("package.capture-item", 1)]
public sealed partial class CaptureItem : IDurableObject {
    [DurableField(1)] private string _shared;
    [DurableField(2)] private string _equalButDistinct;
    [DurableField(3)] private string? _optional = null;
    [DurableField(4)] private string _surrogate = "\uD800";
    [DurableField(5)] private string _empty = string.Empty;

    public CaptureItem(string shared, string distinct) { _shared = shared; _equalButDistinct = distinct; }
}

internal static class ReferenceCaptureExercise {
    internal static void Run() {
        string shared = new(['A', 'd', 'a']);
        string equal = new(['A', 'd', 'a']);
        Require(!ReferenceEquals(shared, equal), "Fixture must have distinct equal strings.");
        CaptureHero hero = new(shared);
        CaptureItem item = new(shared, equal);
        CaptureSession session = new();
        CapturedGraph first;
        using (var capture = session.BeginCapture()) {
            Require(CaptureHero.__DurableState.AddRoot(capture, hero).Value == 1, "First root ID.");
            Require(CaptureItem.__DurableState.AddRoot(capture, item).Value == 2, "Second root ID.");
            CaptureHero.__DurableState.AddRoot(capture, hero);
            CaptureHero.__DurableState.AddRoot(capture, null);
            first = capture.Seal();
            Require(first.RootIds.Select(id => id.Value).SequenceEqual(new uint[] { 1, 2, 1, 0 }), "Root identity/order.");
            var heroState = first.Objects.Single(entry => entry.Id.Value == 1)
                .GetState<CaptureHero.__DurableState.V1>();
            var itemState = first.Objects.Single(entry => entry.Id.Value == 2)
                .GetState<CaptureItem.__DurableState.V1>();
            Require(heroState.Segment0Field1.Value == 3 && heroState.Segment1Field1.Value == 3 &&
                itemState.Segment0Field1.Value == 3 && itemState.Segment0Field2.Value == 4 &&
                itemState.Segment0Field3.Value == 0, "Shared and distinct string IDs.");
            Require(first.Objects.Count == 6 && first.Objects.Count(entry => entry.Kind == ObjectStateKind.String) == 4,
                "Closed object list.");
            Require(ReferenceEquals(first.Objects.Single(entry => entry.Id.Value == 3).StringContent, shared) &&
                ReferenceEquals(first.Objects.Single(entry => entry.Id.Value == 4).StringContent, equal) &&
                first.Objects.Single(entry => entry.Id.Value == 5).StringContent == "\uD800" &&
                first.Objects.Single(entry => entry.Id.Value == 6).StringContent.Length == 0, "String content identity.");
            Require(ReferenceEquals(first.Objects.Single(entry => entry.Id.Value == 1).Schema, CaptureHero.Schema),
                "Exact root schema.");

            ArrayBufferWriter<byte> bytes = new();
            BareValueWriter writer = new(bytes);
            CaptureHero.__DurableState.WriteBaseBody(ref writer, in heroState);
            Require(Convert.ToHexString(bytes.WrittenSpan) == "03030E", "Static reference ID golden.");
            BareValueReader reader = new(bytes.WrittenSpan);
            var decoded = CaptureHero.__DurableState.ReadBaseBodyV1(ref reader);
            reader.EnsureFullyConsumed();
            Require(decoded.Segment0Field1.Value == 3 && decoded.Segment1Field1.Value == 3 && decoded.Segment1Field2 == 7,
                "Typed ID body round trip.");

            hero.ChangeAlias(new string(['n', 'e', 'w']));
            session.Accept(first);
            Require(ReferenceEquals(first, session.Current) &&
                first.Objects.Single(entry => entry.Id.Value == 1).GetState<CaptureHero.__DurableState.V1>().Segment1Field2 == 7,
                "Accept must install the captured state.");
        }

        ObjectId discardedId;
        using (var capture = session.BeginCapture()) {
            CaptureHero.__DurableState.AddRoot(capture, hero);
            CaptureItem.__DurableState.AddRoot(capture, item);
            var candidate = capture.Seal();
            discardedId = candidate.Objects.Single(entry => entry.Id.Value == 1)
                .GetState<CaptureHero.__DurableState.V1>().Segment1Field1;
            Require(discardedId.Value > 6, "Changed alias needs a new ID.");
            session.Discard(candidate);
            Require(ReferenceEquals(first, session.Current), "Discard preserves parent.");
        }

        ObjectId retriedId;
        using (var capture = session.BeginCapture()) {
            Require(CaptureHero.__DurableState.AddRoot(capture, hero).Value == 1, "Live root ID remains stable.");
            Require(CaptureItem.__DurableState.AddRoot(capture, item).Value == 2, "Other live root remains stable.");
            var candidate = capture.Seal();
            var state = candidate.Objects.Single(entry => entry.Id.Value == 1).GetState<CaptureHero.__DurableState.V1>();
            retriedId = state.Segment1Field1;
            Require(retriedId.Value > discardedId.Value && state.Segment0Field1.Value == 3 && state.Segment1Field2 == 90,
                "Retry burns discarded numbers and retains live strings.");
            session.Accept(candidate);
        }

        using (var capture = session.BeginCapture()) { session.Accept(capture.Seal()); }
        using (var capture = session.BeginCapture()) {
            Require(CaptureHero.__DurableState.AddRoot(capture, hero).Value > retriedId.Value,
                "A retired CLR instance reenters with a fresh ID.");
            session.Discard(capture.Seal());
        }
        Require(session.Current!.Objects.Count == 0, "Discard keeps the empty accepted parent.");
    }

    private static void Require(bool condition, string message) {
        if (!condition) { throw new InvalidOperationException(message); }
    }
}
