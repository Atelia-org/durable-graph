using Atelia.FrameStore;

// Opaque addresses are exported only through FrameAddress's public 12-byte codeword.
internal static class ProbeArtifacts {
    internal static string Format(FrameAddress address) {
        Span<byte> bytes = stackalloc byte[FrameAddress.EncodedSize];
        if (!address.TryWrite(bytes)) { throw new InvalidDataException("Cannot encode frame address."); }
        return Convert.ToHexString(bytes);
    }

    internal static FrameAddress Parse(string text) {
        byte[] bytes = Convert.FromHexString(text);
        if (bytes.Length != FrameAddress.EncodedSize || !FrameAddress.TryRead(bytes, out FrameAddress address)) {
            throw new InvalidDataException("Invalid frame address sidecar.");
        }
        return address;
    }

    internal static long DataLength(string directory) => Directory.EnumerateFiles(Path.Combine(directory, "data"), "*.rbf", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
}
