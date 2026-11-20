using Atelia.Binary;
using Atelia.DurableGraph.Schema;

namespace Atelia.DurableGraph.Runtime;

// Each interpreter holds an immutable layout bound to its source Schema. No domain object participates in replay.
internal sealed class DeclaredNodeReplay(DeclaredSlot[] slots) {
    internal void VisitReferences(DeclaredCell[] state, IStateReferenceVisitor visitor) {
        for (int index = 0; index < slots.Length; index++) {
            if (slots[index].Shape.Target is not { } target) { continue; }
            if (slots[index].Shape.Kind == DeclaredSlotKind.References) {
                foreach (uint id in state[index].Items!) {
                    if (id != 0) { visitor.VisitObject(new(id), target); }
                }
            } else if (state[index].Word != 0) {
                visitor.VisitObject(new(state[index].Word), target);
            }
        }
    }

    private static DeclaredCell ReadPrivate(ref BareValueReader reader, DeclaredSlot slot) {
        uint length = ReadWord(ref reader);
        if (length > Array.MaxLength || length > reader.RemainingCount) { throw new InvalidDataException("The private data exceeds the remaining node body."); }
        ReadOnlySpan<byte> bytes = reader.ReadRawBytes((int)length);
        slot.Validate!(bytes);
        return new(bytes.ToArray());
    }

    internal DeclaredCell[] ReadBase(ref BareValueReader reader) {
        // Each slot requires at least one byte, including empty private blocks and reference vectors.
        if (slots.Length > reader.RemainingCount) { throw new InvalidDataException("The declared slots cannot fit in the Base body."); }
        var state = new DeclaredCell[slots.Length];
        for (int index = 0; index < state.Length; index++) {
            DeclaredSlot slot = slots[index];
            if (slot.Shape.Kind == DeclaredSlotKind.Private) { state[index] = ReadPrivate(ref reader, slot); }
            else { state[index] = slot.Shape.Kind == DeclaredSlotKind.References ? ReadVector(ref reader) : new(ReadWord(ref reader)); }
        }
        return state;
    }

    private static DeclaredCell ReadVector(ref BareValueReader reader) {
        uint count = ReadWord(ref reader);
        if (count > Array.MaxLength || count > reader.RemainingCount) {
            throw new InvalidDataException("The repeated slot count exceeds the remaining body or CLR limit.");
        }
        var items = new uint[count];
        for (int index = 0; index < items.Length; index++) { items[index] = ReadWord(ref reader); }
        return new(items);
    }

    internal DeclaredCell[] ApplyDelta(ref BareValueReader reader, DeclaredCell[] previous) {
        if (reader.RemainingCount == 0) { throw new InvalidDataException("A stored node Delta needs at least one changed slot."); }
        var next = (DeclaredCell[])previous.Clone();
        int last = -1;
        while (reader.RemainingCount > 0) {
            uint index = ReadWord(ref reader);
            if (index >= slots.Length || (int)index <= last) { throw new InvalidDataException("Delta slot indexes must increase and fit the layout."); }
            last = (int)index;
            if (slots[index].Shape.Kind == DeclaredSlotKind.Private) {
                next[index] = ReadPrivate(ref reader, slots[index]);
                if (next[index].Private.AsSpan().SequenceEqual(previous[index].Private)) {
                    throw new InvalidDataException("A private Delta repeats its prior value.");
                }
                continue;
            }
            if (slots[index].Shape.Kind != DeclaredSlotKind.References) {
                uint word = ReadWord(ref reader);
                if (word == previous[index].Word) { throw new InvalidDataException("A Delta slot repeats its prior value."); }
                next[index] = new(word);
                continue;
            }
            uint[] prior = previous[index].Items!;
            switch (reader.ReadByte()) {
                case 0:
                    next[index] = ReadVector(ref reader);
                    if (next[index].Items!.Length == prior.Length) { throw new InvalidDataException("Replacing a vector requires a length change."); }
                    break;
                case 1:
                    next[index] = ApplyVectorPatch(ref reader, prior);
                    break;
                default: throw new InvalidDataException("Unknown repeated slot Delta.");
            }
        }
        return next;
    }

    private static DeclaredCell ApplyVectorPatch(ref BareValueReader reader, uint[] prior) {
        uint last = 0;
        uint[]? next = null;
        while (true) {
            uint entry = ReadWord(ref reader);
            if (entry == 0) { return next is not null ? new(next) : throw new InvalidDataException("An empty vector patch is invalid."); }
            if (entry <= last || entry > prior.Length) { throw new InvalidDataException("Vector patch positions must increase and fit the prior vector."); }
            last = entry;
            uint id = ReadWord(ref reader);
            if (id == prior[entry - 1]) { throw new InvalidDataException("A vector patch repeats its prior ID."); }
            next ??= (uint[])prior.Clone();
            next[entry - 1] = id;
        }
    }

    private static uint ReadWord(ref BareValueReader reader) {
        int start = reader.ConsumedCount;
        uint word = reader.ReadVarUInt32();
        if (reader.ConsumedCount - start != BareValueEncoding.MeasureVarUInt32(word)) {
            throw new InvalidDataException("Declared node integers require their shortest Base128 encoding.");
        }
        return word;
    }
}
