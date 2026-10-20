using System.Text.Json;
using Atelia.DurableGraph;

namespace ApplicationRecipes;

// 这是应用配置协议；RepositoryKey 由应用映射到仓库路径，不是 DG 的全局身份。
public sealed record Navigation(int Version, string RepositoryKey, string LastUsedBranch, string? BookmarkCommit);

public static class IdText {
    public static string Encode(BranchId id) {
        Span<byte> bytes = stackalloc byte[BranchId.EncodedSize];
        if (!id.TryWrite(bytes)) { throw new ArgumentException("Cannot encode a default branch ID.", nameof(id)); }
        return Convert.ToHexString(bytes);
    }

    public static string Encode(CommitId id) {
        Span<byte> bytes = stackalloc byte[CommitId.EncodedSize];
        if (!id.TryWrite(bytes)) { throw new ArgumentException("Cannot encode a default commit ID.", nameof(id)); }
        return Convert.ToHexString(bytes);
    }

    public static bool TryReadBranch(string? text, out BranchId id) {
        id = default;
        return TryReadHex(text, BranchId.EncodedSize, out byte[] bytes) && BranchId.TryRead(bytes, out id);
    }

    public static bool TryReadCommit(string? text, out CommitId id) {
        id = default;
        return TryReadHex(text, CommitId.EncodedSize, out byte[] bytes) && CommitId.TryRead(bytes, out id);
    }

    private static bool TryReadHex(string? text, int size, out byte[] bytes) {
        bytes = [];
        if (text is null || text.Length != size * 2) { return false; }
        try { bytes = Convert.FromHexString(text); return true; }
        catch (FormatException) { return false; }
    }
}

public static class NavigationCodec {
    public const int CurrentVersion = 1;

    public static string Encode(string repositoryKey, BranchId lastUsedBranch, CommitId? bookmarkCommit = null) {
        if (string.IsNullOrWhiteSpace(repositoryKey)) {
            throw new ArgumentException("An application repository key is required.", nameof(repositoryKey));
        }
        return JsonSerializer.Serialize(new Navigation(CurrentVersion, repositoryKey,
            IdText.Encode(lastUsedBranch), bookmarkCommit is { } bookmark ? IdText.Encode(bookmark) : null));
    }

    public static Navigation Decode(string json) {
        Navigation? value;
        if (json is null) { throw new FormatException("Navigation JSON is required."); }
        try { value = JsonSerializer.Deserialize<Navigation>(json); }
        catch (JsonException error) { throw new FormatException("Invalid navigation JSON.", error); }
        if (value is null || value.Version != CurrentVersion
            || string.IsNullOrWhiteSpace(value.RepositoryKey)
            || !IdText.TryReadBranch(value.LastUsedBranch, out _)
            || (value.BookmarkCommit is not null && !IdText.TryReadCommit(value.BookmarkCommit, out _))) {
            throw new FormatException("Unsupported or invalid navigation configuration.");
        }
        // 仅证明表示合法。选定原仓库后，由 Repository 操作检查成员和准确位置；
        // 不用列表首项、重复标签或 Commit 的来源分支替代 LastUsedBranch。
        return value;
    }
}
