namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class ManagementDirectoryPointerTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Empty_and_selected_graph_have_exact_role_headers_and_upstream_address_bytes(bool bookmarks) {
        ManagementDirectoryKind kind = Kind(bookmarks);
        byte[] magic = bookmarks ? "P0T2"u8.ToArray() : "P0R4"u8.ToArray();
        byte[] empty = ManagementDirectoryPointer.Encode(kind, null);
        Assert.Equal(new byte[] { magic[0], magic[1], magic[2], magic[3], 0 }, empty);
        Assert.Null(ManagementDirectoryPointer.Decode(kind, empty));

        // Numeric codec validity does not assert this address belongs to a store
        // or points at a management graph; the graph reader supplies that check.
        FrameAddress address = TestAddress(file: 73, offset: 4096, length: 512);
        byte[] expectedAddress = new byte[FrameAddress.EncodedSize];
        Assert.True(address.TryWrite(expectedAddress));
        byte[] selected = ManagementDirectoryPointer.Encode(kind, address);
        Assert.Equal(17, selected.Length);
        Assert.Equal(magic, selected[..4]);
        Assert.Equal(1, selected[4]);
        Assert.Equal(expectedAddress, selected[5..]);
        Assert.Equal(address, ManagementDirectoryPointer.Decode(kind, selected));
        Assert.Equal(selected, ManagementDirectoryPointer.Encode(kind,
            ManagementDirectoryPointer.Decode(kind, selected)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Malformed_pointers_never_turn_into_an_empty_directory(bool bookmarks) {
        ManagementDirectoryKind kind = Kind(bookmarks);
        byte[] selected = ManagementDirectoryPointer.Encode(kind, TestAddress());
        byte[] empty = ManagementDirectoryPointer.Encode(kind, null);
        for (int length = 0; length < selected.Length; length++) {
            byte[] truncated = selected[..length];
            Assert.Throws<InvalidDataException>(() => ManagementDirectoryPointer.Decode(kind, truncated));
        }
        Assert.Throws<InvalidDataException>(() => ManagementDirectoryPointer.Decode(kind, [.. selected, 0]));
        Assert.Throws<InvalidDataException>(() => ManagementDirectoryPointer.Decode(kind, [.. empty, 0]));

        foreach (byte flag in new byte[] { 0, 2, 255 }) {
            byte[] invalidFlag = (byte[])selected.Clone();
            invalidFlag[4] = flag;
            Assert.Throws<InvalidDataException>(() => ManagementDirectoryPointer.Decode(kind, invalidFlag));
        }
        foreach (byte flag in new byte[] { 1, 2, 255 }) {
            byte[] invalidFlag = (byte[])empty.Clone();
            invalidFlag[4] = flag;
            Assert.Throws<InvalidDataException>(() => ManagementDirectoryPointer.Decode(kind, invalidFlag));
        }

        byte[] invalidFile = (byte[])selected.Clone();
        invalidFile.AsSpan(5, sizeof(uint)).Clear();
        Assert.Throws<InvalidDataException>(() => ManagementDirectoryPointer.Decode(kind, invalidFile));
        byte[] invalidTicket = (byte[])selected.Clone();
        invalidTicket.AsSpan(5 + sizeof(uint)).Clear();
        Assert.Throws<InvalidDataException>(() => ManagementDirectoryPointer.Decode(kind, invalidTicket));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Other_roles_legacy_headers_and_default_addresses_are_rejected(bool bookmarks) {
        ManagementDirectoryKind kind = Kind(bookmarks);
        byte[] otherRole = ManagementDirectoryPointer.Encode(Kind(!bookmarks), null);
        Assert.Throws<InvalidDataException>(() => ManagementDirectoryPointer.Decode(kind, otherRole));
        foreach (byte[] magic in new[] { "P0R1"u8.ToArray(), "P0R2"u8.ToArray(),
            "P0T1"u8.ToArray(), "P0S1"u8.ToArray(), "BAD!"u8.ToArray() }) {
            Assert.Throws<InvalidDataException>(() => ManagementDirectoryPointer.Decode(kind, [.. magic, 0]));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ManagementDirectoryPointer.Encode(kind, default(FrameAddress)));
    }

    [Fact]
    public void Unknown_role_is_a_caller_error() {
        var unknown = (ManagementDirectoryKind)99;
        Assert.Throws<ArgumentOutOfRangeException>(() => ManagementDirectoryPointer.Encode(unknown, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => ManagementDirectoryPointer.Decode(unknown, []));
    }

    private static ManagementDirectoryKind Kind(bool bookmarks) => bookmarks
        ? ManagementDirectoryKind.Bookmarks : ManagementDirectoryKind.Branches;
}
