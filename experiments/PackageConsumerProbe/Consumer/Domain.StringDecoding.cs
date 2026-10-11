using Atelia.Binary;
using System.Buffers;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;

namespace PackageConsumerProbe;

// A deliberately typed byte witness, not a persistent graph format or runtime registry.
internal static class StringDecodingExercise {
    private enum BodyKind { Hero, Item, String }
    private sealed record BodyRecord(ObjectId Id, BodyKind Kind, DurableSchema? Schema, byte[] Body);
    private sealed record BodyBundle(ObjectId[] Roots, BodyRecord[] Records);
    private sealed record Loaded(
        ObjectId[] Roots,
        Dictionary<ObjectId, CaptureHero.__DurableState.V1> Heroes,
        Dictionary<ObjectId, CaptureItem.__DurableState.V1> Items,
        StringReadTable Strings);

    internal static void Run() {
        AssertEmptyNormalization();
        BodyBundle bytes = CaptureAndEncode();
        Loaded loaded = Load(bytes);
        AssertTopology(loaded);
        AssertTopology(Load(bytes with { Records = bytes.Records.Reverse().ToArray() }));

        // The loader receives only owned bytes and explicit metadata, never a captured object.
        BodyRecord hero = bytes.Records.Single(record => record.Kind == BodyKind.Hero);
        BodyRecord item = bytes.Records.Single(record => record.Kind == BodyKind.Item);
        BodyRecord text = bytes.Records.First(record => record.Kind == BodyKind.String);
        Reject(bytes with { Records = Replace(bytes, hero with { Schema = CaptureItem.Schema }) });
        Reject(bytes with { Records = bytes.Records.Append(text with { Id = hero.Id }).ToArray() });
        Reject(bytes with { Records = bytes.Records.Append(text).ToArray() });
        Reject(bytes with { Records = bytes.Records.Select(record =>
            ReferenceEquals(record, text) ? text with { Id = default } : record).ToArray() });
        Reject(bytes with { Roots = [new ObjectId(127)] });
        Reject(bytes with { Roots = [text.Id] });
        Reject(bytes with { Records = Replace(bytes, hero with { Body = [127, 3, 14] }) });
        Reject(bytes with { Records = Replace(bytes, hero with { Body = [1, 3, 14] }) });
        // The first owner is valid; failure in the second must still prevent a loaded result.
        Reject(bytes with { Records = Replace(bytes, item with { Body = [3, 127, 0, 5, 6] }) });
        Reject(bytes with { Records = Replace(bytes, hero with { Body = [3, 3, 14, 0] }) });
        Reject(bytes with { Records = Replace(bytes, text with { Body = [0, 0] }) });
        Reject(bytes with { Records = Replace(bytes, text with { Body = [] }) });

        // Fresh tables interpret IDs independently; a failed witness cannot replace a prior result.
        StringReadTable other = StringReadTable.Decode(new[] { (text.Id, (ReadOnlyMemory<byte>)EncodeString("other")) });
        Require(other.ResolveString(text.Id) == "other" && loaded.Strings.ResolveString(text.Id) == "Ada",
            "A later decode must not replace another view's binding.");
        AssertTopology(loaded);
    }

    private static BodyBundle CaptureAndEncode() {
        string shared = new(['A', 'd', 'a']);
        string distinct = new(['A', 'd', 'a']);
        Require(!ReferenceEquals(shared, distinct), "Distinct equal source strings.");
        CaptureHero hero = new(shared);
        CaptureItem item = new(shared, distinct);
        CaptureSession session = new();
        using CaptureContext capture = session.BeginCapture();
        CaptureHero.__DurableState.AddRoot(capture, hero);
        CaptureItem.__DurableState.AddRoot(capture, item);
        CaptureHero.__DurableState.AddRoot(capture, hero);
        CaptureHero.__DurableState.AddRoot(capture, null);
        CapturedGraph candidate = capture.Seal();
        hero.ChangeAlias("changed after Seal, before encoding");
        return Encode(candidate);
    }

    private static BodyBundle Encode(CapturedGraph candidate) {
        List<BodyRecord> records = [];
        foreach (ObjectStateRecord entry in candidate.Objects) {
            ArrayBufferWriter<byte> buffer = new();
            BareValueWriter writer = new(buffer);
            BodyKind kind;
            if (entry.Kind == ObjectStateKind.String) {
                kind = BodyKind.String;
                writer.WriteString(entry.StringContent);
            } else if (CaptureHero.Schema.Equals(entry.Schema)) {
                kind = BodyKind.Hero;
                var state = entry.GetState<CaptureHero.__DurableState.V1>();
                CaptureHero.__DurableState.WriteBaseBody(ref writer, in state);
            } else if (CaptureItem.Schema.Equals(entry.Schema)) {
                kind = BodyKind.Item;
                var state = entry.GetState<CaptureItem.__DurableState.V1>();
                CaptureItem.__DurableState.WriteBaseBody(ref writer, in state);
            } else {
                throw new InvalidOperationException("Unexpected fixture schema.");
            }
            records.Add(new(entry.Id, kind, entry.Schema, buffer.WrittenSpan.ToArray()));
        }
        return new(candidate.RootIds.ToArray(), records.ToArray());
    }

    private static void AssertEmptyNormalization() {
        // Public API behavior supplies distinct test inputs on the tested runtime. The product
        // never depends on this allocation behavior: it canonicalizes every empty input.
        string firstEmpty = "A".Replace("A", "");
        string secondEmpty = "B".Replace("B", "");
        Require(firstEmpty.Length == 0 && secondEmpty.Length == 0 &&
            !ReferenceEquals(firstEmpty, secondEmpty) && !ReferenceEquals(firstEmpty, string.Empty) &&
            !ReferenceEquals(secondEmpty, string.Empty), "Fixture must contain independent empty instances.");
        BodyBundle bytes;
        CaptureSession session = new();
        using (CaptureContext capture = session.BeginCapture()) {
            CaptureHero.__DurableState.AddRoot(capture, new CaptureHero(firstEmpty));
            CaptureItem.__DurableState.AddRoot(capture, new CaptureItem(firstEmpty, secondEmpty));
            CapturedGraph candidate = capture.Seal();
            var hero = candidate.Objects.Single(record => record.Id.Value == 1)
                .GetState<CaptureHero.__DurableState.V1>();
            var item = candidate.Objects.Single(record => record.Id.Value == 2)
                .GetState<CaptureItem.__DurableState.V1>();
            ObjectId emptyId = hero.Segment0Field1;
            Require(!emptyId.IsNull && hero.Segment1Field1 == emptyId && item.Segment0Field1 == emptyId &&
                item.Segment0Field2 == emptyId && item.Segment0Field5 == emptyId,
                "Generated Capture must normalize distinct empty instances and string.Empty to one ID.");
            Require(candidate.Objects.Count == 4 && ReferenceEquals(
                candidate.Objects.Single(record => record.Id == emptyId).StringContent, string.Empty),
                "Capture retains one canonical empty object plus the surrogate object.");
            bytes = Encode(candidate);
        }
        Loaded loaded = Load(bytes);
        Require(ReferenceEquals(loaded.Strings.ResolveString(loaded.Heroes[new ObjectId(1)].Segment0Field1), string.Empty) &&
            ReferenceEquals(loaded.Strings.ResolveString(loaded.Items[new ObjectId(2)].Segment0Field2), string.Empty) &&
            loaded.Strings.ResolveString(loaded.Items[new ObjectId(2)].Segment0Field3) is null,
            "Byte-only loading preserves canonical empty and distinguishes null.");

        byte[] body = EncodeString(string.Empty);
        StringReadTable aliases = StringReadTable.Decode(new[] {
            (new ObjectId(10), (ReadOnlyMemory<byte>)body), (new ObjectId(11), (ReadOnlyMemory<byte>)body),
        });
        Require(ReferenceEquals(aliases.ResolveString(new ObjectId(10)), string.Empty) &&
            ReferenceEquals(aliases.ResolveString(new ObjectId(11)), string.Empty) && aliases.ResolveString(new ObjectId(0)) is null,
            "Distinct IDs with empty bodies intentionally resolve to the same canonical empty instance.");
        try {
            _ = StringReadTable.Decode(new[] {
                (new ObjectId(10), (ReadOnlyMemory<byte>)body), (new ObjectId(10), (ReadOnlyMemory<byte>)body),
            });
        }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Empty normalization must not permit duplicate object IDs.");
    }

    private static Loaded Load(BodyBundle bundle) {
        // Preflight the complete heterogeneous directory before decoding or exposing any result.
        Dictionary<ObjectId, BodyKind> directory = [];
        foreach (BodyRecord record in bundle.Records) {
            if (record.Id.IsNull || !directory.TryAdd(record.Id, record.Kind)) {
                throw new InvalidDataException("Object IDs must be nonzero and unique across every kind.");
            }
            bool exactSchema = record.Kind switch {
                BodyKind.Hero => CaptureHero.__DurableState.V1.Schema.Equals(record.Schema),
                BodyKind.Item => CaptureItem.__DurableState.V1.Schema.Equals(record.Schema),
                BodyKind.String => record.Schema is null,
                _ => false,
            };
            if (!exactSchema) { throw new InvalidDataException("Body binding requires its exact schema."); }
        }
        foreach (ObjectId root in bundle.Roots) {
            if (!root.IsNull && (!directory.TryGetValue(root, out BodyKind kind) || kind == BodyKind.String)) {
                throw new InvalidDataException("This typed witness requires a known durable root or null.");
            }
        }
        StringReadTable strings = StringReadTable.Decode(bundle.Records
            .Where(record => record.Kind == BodyKind.String)
            .Select(record => (record.Id, (ReadOnlyMemory<byte>)record.Body)));
        Dictionary<ObjectId, CaptureHero.__DurableState.V1> heroes = [];
        Dictionary<ObjectId, CaptureItem.__DurableState.V1> items = [];
        foreach (BodyRecord record in bundle.Records) {
            BareValueReader reader = new(record.Body);
            if (record.Kind == BodyKind.Hero) {
                var state = CaptureHero.__DurableState.ReadBaseBodyV1(ref reader);
                reader.EnsureFullyConsumed();
                CaptureHero.__DurableState.ValidateStringReferences(in state, strings);
                heroes.Add(record.Id, state);
            } else if (record.Kind == BodyKind.Item) {
                var state = CaptureItem.__DurableState.ReadBaseBodyV1(ref reader);
                reader.EnsureFullyConsumed();
                CaptureItem.__DurableState.ValidateStringReferences(in state, strings);
                items.Add(record.Id, state);
            }
        }
        return new(bundle.Roots.ToArray(), heroes, items, strings);
    }

    private static void AssertTopology(Loaded loaded) {
        Require(loaded.Roots.Select(id => id.Value).SequenceEqual(new uint[] { 1, 2, 1, 0 }), "Decoded root ordering.");
        var hero = loaded.Heroes[new ObjectId(1)];
        var item = loaded.Items[new ObjectId(2)];
        string? shared = loaded.Strings.ResolveString(hero.Segment0Field1);
        string? distinct = loaded.Strings.ResolveString(item.Segment0Field2);
        Require(shared == "Ada" && distinct == "Ada" && !ReferenceEquals(shared, distinct),
            "Different string IDs must not merge by value.");
        Require(ReferenceEquals(shared, loaded.Strings.ResolveString(hero.Segment1Field1)) &&
            ReferenceEquals(shared, loaded.Strings.ResolveString(item.Segment0Field1)),
            "Both owners and the base segment must share one resolved instance.");
        Require(hero.Segment1Field2 == 7, "Encoding must consume the sealed DTO, not changed domain state.");
        Require(loaded.Strings.ResolveString(item.Segment0Field3) is null &&
            loaded.Strings.ResolveString(item.Segment0Field4) == "\uD800" &&
            loaded.Strings.ResolveString(item.Segment0Field5) == "", "Null, surrogate and empty content.");
    }

    private static byte[] EncodeString(string value) {
        ArrayBufferWriter<byte> buffer = new();
        BareValueWriter writer = new(buffer);
        writer.WriteString(value);
        return buffer.WrittenSpan.ToArray();
    }

    private static BodyRecord[] Replace(BodyBundle bundle, BodyRecord replacement) =>
        bundle.Records.Select(record => record.Id == replacement.Id ? replacement : record).ToArray();

    private static void Reject(BodyBundle bundle) {
        try { _ = Load(bundle); }
        catch (InvalidDataException) { return; }
        catch (EndOfStreamException) { return; }
        throw new InvalidOperationException("An invalid byte bundle produced a loaded witness.");
    }

    private static void Require(bool condition, string message) {
        if (!condition) { throw new InvalidOperationException(message); }
    }
}
