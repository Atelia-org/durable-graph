using Atelia.FrameStore;
using Atelia.DurableGraph.Storage;

namespace Atelia.DurableGraph.Persistence;

/// <summary>Verified head and actual reconstruction payload for one stored object.</summary>
internal readonly record struct ObjectStorageInfo(FrameAddress Head, long ReconstructionPayloadBytes);
