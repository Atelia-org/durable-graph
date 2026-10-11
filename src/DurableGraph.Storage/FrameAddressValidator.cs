using Atelia.FrameStore;

namespace Atelia.DurableGraph.Storage;

internal static class FrameAddressValidator {
    internal static void ValidateRequired(FrameAddress address, string parameterName) {
        if (address == default) {
            throw new ArgumentOutOfRangeException(parameterName, address, "A required FrameAddress cannot be default.");
        }
    }
}
