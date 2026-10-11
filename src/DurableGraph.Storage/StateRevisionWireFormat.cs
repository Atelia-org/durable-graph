namespace Atelia.DurableGraph.Storage;

internal static class StateRevisionWireFormat {
    internal const uint RbfTag = 0x52534744; // "DGSR" in little-endian byte order.
    internal const byte Version = 4;
    internal const int MaxCollectionCount = 1_000_000;
}
