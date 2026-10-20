using System.Text.Json;
using Atelia.DurableGraph;

namespace ApplicationRecipes;

public static class NavigationChecks {
    // 无仓库操作：重开、继续子线及固定书签由主流程另行验证。
    public static void Run(BranchId branch, CommitId commit) {
        string branchHex = IdText.Encode(branch);
        string commitHex = IdText.Encode(commit);
        Need(branchHex.Length == 8 && commitHex.Length == 24, "Unexpected ID text lengths.");
        Need(branchHex == branchHex.ToUpperInvariant() && commitHex == commitHex.ToUpperInvariant(),
            "The writer must emit uppercase Hex.");
        Need(IdText.TryReadBranch(branchHex.ToLowerInvariant(), out var restoredBranch) && restoredBranch == branch,
            "Branch Hex did not round-trip.");
        Need(IdText.TryReadCommit(commitHex.ToLowerInvariant(), out var restoredCommit) && restoredCommit == commit,
            "Commit Hex did not round-trip.");
        Reject<ArgumentException>(() => IdText.Encode(default(BranchId)));
        Reject<ArgumentException>(() => IdText.Encode(default(CommitId)));
        foreach (string? text in new string?[] { null, "", " ", "0", "00000000", "GGGGGGGG", branchHex + "00", commitHex }) {
            Need(!IdText.TryReadBranch(text, out var rejected) && rejected == default, "Invalid branch text was accepted.");
        }
        foreach (string? text in new string?[] {
            null, "", " ", "0", new string('0', 24), new string('G', 24), commitHex + "00", branchHex,
            branchHex + new string('0', 16), new string('0', 8) + commitHex[8..]
        }) {
            Need(!IdText.TryReadCommit(text, out var rejected) && rejected == default, "Invalid commit text was accepted.");
        }

        Navigation saved = NavigationCodec.Decode(NavigationCodec.Encode("garden", branch, commit));
        Need(saved == new Navigation(1, "garden", branchHex, commitHex), "Navigation fields changed.");
        Need(NavigationCodec.Decode(NavigationCodec.Encode("garden", branch)).BookmarkCommit is null,
            "No bookmark must remain explicit absence.");
        string withoutBookmark = JsonSerializer.Serialize(new { Version = 1, RepositoryKey = "garden", LastUsedBranch = branchHex });
        Need(NavigationCodec.Decode(withoutBookmark).BookmarkCommit is null, "An omitted bookmark should be absent.");
        Navigation lower = NavigationCodec.Decode(JsonSerializer.Serialize(saved with {
            LastUsedBranch = branchHex.ToLowerInvariant(), BookmarkCommit = commitHex.ToLowerInvariant()
        }));
        Need(IdText.TryReadBranch(lower.LastUsedBranch, out var lowerBranch) && lowerBranch == branch,
            "Navigation should accept lowercase Hex.");

        Reject<ArgumentException>(() => NavigationCodec.Encode("", branch));
        Reject<ArgumentException>(() => NavigationCodec.Encode("garden", default));
        Reject<ArgumentException>(() => NavigationCodec.Encode("garden", branch, default(CommitId)));
        foreach (Navigation invalid in new[] {
            saved with { Version = 0 }, saved with { Version = 2 },
            saved with { RepositoryKey = "" }, saved with { RepositoryKey = null! },
            saved with { LastUsedBranch = null! }, saved with { LastUsedBranch = "00000000" },
            saved with { BookmarkCommit = "" }, saved with { BookmarkCommit = new string('0', 24) }
        }) {
            Reject<FormatException>(() => NavigationCodec.Decode(JsonSerializer.Serialize(invalid)));
        }
        foreach (string? invalid in new[] {
            null, "", "null", "{}", "{", "[]", """{"Version":"1"}""",
            JsonSerializer.Serialize(new { RepositoryKey = "garden", LastUsedBranch = branchHex }),
            JsonSerializer.Serialize(new { Version = 1, RepositoryKey = "garden" }),
            JsonSerializer.Serialize(new { Version = 1, LastUsedBranch = branchHex })
        }) {
            Reject<FormatException>(() => NavigationCodec.Decode(invalid!));
        }
    }

    private static void Need(bool condition, string message) {
        if (!condition) { throw new InvalidOperationException(message); }
    }

    private static void Reject<T>(Action action) where T : Exception {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
