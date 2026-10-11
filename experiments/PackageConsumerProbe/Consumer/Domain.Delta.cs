using Atelia.Binary;
using System.Buffers;
using Atelia.DurableGraph.Serialization;

namespace PackageConsumerProbe;

public sealed partial class Character {
    private static void ExercisePreparedDelta() {
        Character priorSource = new(true, -17, 42, 8);
        Character currentSource = new(true, -18, 43, 9);
        var prior = __DurableState.Capture(priorSource);
        var current = __DurableState.Capture(currentSource);
        priorSource._total = 1000;
        currentSource._total = 2000;

        // One package reference supplies this public result type transitively.
        PreparedDeltaBody delta = __DurableState.PrepareDeltaBody(in prior, in current);
        PreparedDeltaBody unchanged = __DurableState.PrepareDeltaBody(in prior, in prior);
        // Base-first layout is bool, int, long; the latter two change.
        // ZigZag(-18) = 35 (0x23), ZigZag(43) = 86 (0x56).
        byte[] deltaGolden = [0x06, 0x23, 0x56];
        if (!delta.HasChanges || delta.Body.Length != 3 ||
            !delta.Body.SequenceEqual(deltaGolden) || unchanged.HasChanges ||
            !unchanged.Body.SequenceEqual(new byte[] { 0x00 })) {
            throw new InvalidOperationException("Packaged fused Delta result disagrees with its golden bytes.");
        }

        byte[] externalCopy = delta.Body.ToArray();
        externalCopy[0] = 0xFF;
        // Reuse the prepared bytes directly, including after another Prepare and an external mutation.
        for (int attempt = 0; attempt < 2; attempt++) {
            BareValueReader reader = new(delta.Body);
            var applied = __DurableState.ApplyDeltaBodyV1(ref reader, in prior);
            reader.EnsureFullyConsumed();
            ArrayBufferWriter<byte> buffer = new();
            BareValueWriter writer = new(buffer);
            __DurableState.WriteBaseBody(ref writer, in applied);
            if (!buffer.WrittenSpan.SequenceEqual(new byte[] { 0x01, 0x23, 0x56 }) ||
                __DurableState.PrepareDeltaBody(in applied, in current).HasChanges) {
                throw new InvalidOperationException("Packaged Delta did not reconstruct the frozen current DTO.");
            }
        }

        BareValueReader noChangeReader = new(unchanged.Body);
        var retained = __DurableState.ApplyDeltaBodyV1(ref noChangeReader, in prior);
        noChangeReader.EnsureFullyConsumed();
        if (__DurableState.PrepareDeltaBody(in retained, in prior).HasChanges ||
            prior.Segment1Field1 != 42 || current.Segment1Field1 != 43 ||
            !delta.Body.SequenceEqual(deltaGolden)) {
            throw new InvalidOperationException("Packaged Delta mutated a frozen DTO or owned result.");
        }

        try {
            // Three slots cannot use bitmap bit 3.
            BareValueReader malformed = new(new byte[] { 0x08 });
            _ = __DurableState.ApplyDeltaBodyV1(ref malformed, in prior);
        }
        catch (InvalidDataException) {
            return;
        }
        throw new InvalidOperationException("Packaged Delta accepted out-of-layout bitmap bits.");
    }
}
