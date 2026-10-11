using Atelia.FrameStore;
using Atelia.VersionStore;
using DataStore = Atelia.FrameStore.FrameStore;
using RootStore = Atelia.VersionStore.VersionStore;

// Tiny ordinal records witness the public FS/VS publication contract, not DG's wire.
const uint probeTag = 0x50524F42;
if (args.Length != 3) { throw new ArgumentException("Usage: child|verify directory checkpoint|expected"); }
string root = Path.GetFullPath(args[1]);
string dataPath = Path.Combine(root, "data");
string versionsPath = Path.Combine(root, "versions");
if (args[0] == "child") {
    Directory.CreateDirectory(root);
    using DataStore data = DataStore.Create(dataPath, rotationThresholdBytes: 128);
    using RootStore versions = RootStore.Create(versionsPath, data, reservedRefIdUpperBound: 1);
    // VS requires a nonempty application root; ordinal zero represents no selected data.
    RefId rootRef = versions.CreateRef(1, [0], out PublicationOutcome initial).Unwrap().RefId;
    RequireConfirmed(initial);
    if (args[2] == "empty") { WaitForKill(); }
    FrameAddress first = data.Append(probeTag, DataPayload(1)).Unwrap();
    versions.PublishRef(rootRef, Encode(1, first), out PublicationOutcome published).Unwrap();
    RequireConfirmed(published);
    FrameAddress second = data.Append(probeTag, DataPayload(2)).Unwrap();
    if (args[2] == "after-data-append") { WaitForKill(); }
    data.ConfirmDurable();
    if (args[2] == "before-publication") { WaitForKill(); }
    versions.PublishRef(rootRef, Encode(2, second), out published).Unwrap();
    RequireConfirmed(published);
    if (args[2] == "after-publication") { WaitForKill(); }
    throw new ArgumentException("Unknown child checkpoint.");
}
if (args[0] == "verify") {
    using DataStore data = DataStore.Open(dataPath, rotationThresholdBytes: 128);
    using RootStore versions = RootStore.Open(versionsPath, data);
    // ReadRef performs any lazy control-file tail recovery. Unselected data is not audited.
    RefBytesSnapshot current = versions.ReadRef(1).Unwrap();
    if (current.Payload.Length == 0) { throw new InvalidDataException("Empty publication payload."); }
    int head = current.Payload[0];
    if (head == 0 && current.Payload.Length != 1) { throw new InvalidDataException("Invalid empty-root marker."); }
    if (head != 0) {
        if (current.Payload.Length != 1 + FrameAddress.EncodedSize ||
            !FrameAddress.TryRead(current.Payload[1..], out FrameAddress address)) {
            throw new InvalidDataException("Invalid publication payload.");
        }
        using FrameRead frame = data.ReadFrame(address).Unwrap();
        if (frame.IsTombstone || frame.Tag != probeTag || frame.TailMetaLength != 0 ||
            frame.Payload.Length != 256 || frame.Payload[0] != head) {
            throw new InvalidDataException("Published data is absent or does not match.");
        }
    }
    if (!int.TryParse(args[2], out int expected) || head != expected) {
        throw new InvalidDataException($"Expected {args[2]}, observed head {head}.");
    }
    Console.WriteLine($"ObservedHead={head}");
    return;
}
throw new ArgumentException("Unknown mode.");

static byte[] DataPayload(byte ordinal) {
    byte[] payload = new byte[256];
    payload[0] = ordinal;
    return payload;
}

static byte[] Encode(byte ordinal, FrameAddress address) {
    byte[] payload = new byte[1 + FrameAddress.EncodedSize];
    payload[0] = ordinal;
    if (!address.TryWrite(payload.AsSpan(1))) { throw new InvalidDataException("Invalid frame address."); }
    return payload;
}

static void RequireConfirmed(PublicationOutcome outcome) {
    if (outcome != PublicationOutcome.Confirmed) { throw new InvalidOperationException("Publication was not confirmed."); }
}

static void WaitForKill() {
    Console.WriteLine("READY");
    Console.Out.Flush();
    Thread.Sleep(Timeout.Infinite);
    throw new InvalidOperationException("Writer must be killed externally.");
}
