using Atelia.FrameStore;
using Atelia.VersionStore;
using PublicationStore = Atelia.VersionStore.VersionStore;

namespace Atelia.DurableGraph.Persistence;

internal static class RepositoryRootCodec {
    private static ReadOnlySpan<byte> Header => "DGR1\x01"u8;
    internal const string SchemaKey = "schema";
    internal const string BranchPrefix = "branch/";

    internal static byte[] Encode(IReadOnlyDictionary<string, FrameAddress> roots) {
        byte[] body = RootMapCodec.Encode(roots).Unwrap();
        int length = checked(Header.Length + body.Length);
        if (length > PublicationStore.MaxRootPayloadLength) {
            throw new InvalidOperationException("The repository root exceeds VersionStore's maximum payload length.");
        }
        byte[] bytes = new byte[length];
        Header.CopyTo(bytes);
        body.CopyTo(bytes, Header.Length);
        return bytes;
    }

    internal static Dictionary<string, FrameAddress> Decode(ReadOnlySpan<byte> payload) {
        if (!payload.StartsWith(Header)) { throw new InvalidDataException("Unknown DurableGraph repository root format."); }
        var roots = new Dictionary<string, FrameAddress>(RootMapCodec.Decode(payload[Header.Length..]).Unwrap(), StringComparer.Ordinal);
        foreach (string key in roots.Keys) {
            if (key == SchemaKey) { continue; }
            if (!key.StartsWith(BranchPrefix, StringComparison.Ordinal)) { throw new InvalidDataException("Unknown repository root key."); }
            try { ValidateBranchName(key[BranchPrefix.Length..]); }
            catch (ArgumentException error) { throw new InvalidDataException("Invalid branch name in repository root.", error); }
        }
        return roots;
    }

    internal static void ValidateBranchName(string name) {
        if (name is null || name.Length is < 1 or > 128 || name.EndsWith(".", StringComparison.Ordinal) || name.EndsWith(".lock", StringComparison.Ordinal)) {
            throw new ArgumentException("Invalid branch name.", nameof(name));
        }
        for (int i = 0; i < name.Length; i++) {
            char c = name[i];
            bool valid = c is >= 'a' and <= 'z' || c is >= '0' and <= '9' || i > 0 && c is '.' or '_' or '-';
            if (!valid) { throw new ArgumentException("Invalid branch name.", nameof(name)); }
        }
    }
}
