using System.Text;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;

namespace Atelia.Text.Tests;

public sealed class TextToolTests {
    private sealed class Host(params TextToolTarget[] targets) : ITextToolHost {
        public TextToolTarget[] Visible { get; set; } = targets;
        public bool MayEdit { get; set; } = true;
        public IEnumerable<TextToolTarget> GetSearchTargets() => Visible;
        public TextToolTarget? Resolve(string target, bool forEdit) =>
            forEdit && !MayEdit ? null : Visible.SingleOrDefault(field => field.Address == target);
    }

    private static ToolSession Session(ITextToolHost host) =>
        new ToolRegistry(TextEditorTools.Create(host)).CreateSession();

    private static async Task<ToolExecuteResult> Invoke(ToolSession session, string name, object input, CancellationToken cancellation = default) =>
        (await session.ExecuteAsync(new RawToolCall(name, "test", JsonSerializer.Serialize(input)), cancellation)).ExecuteResult;

    private static JsonElement Read(ToolExecuteResult receipt, ToolExecutionStatus expected = ToolExecutionStatus.Success) {
        Assert.Equal(expected, receipt.Status);
        return JsonSerializer.Deserialize<JsonElement>(receipt.GetFlattenedText());
    }

    [Fact]
    public async Task SessionResolvesScopeAtEachCallAndReturnsBoundedVerbatimContext() {
        string source = new string('x', 8000) + "一号北门，二号北门" + new string('y', 9000);
        var old = new TextToolTarget("memo:A/body", "A", "body", new TextEditor("题目\r\n" + source));
        var current = old with { Editor = new TextEditor(old.Editor.ToString()) };
        var host = new Host(current);
        var session = Session(host);
        Assert.Equal(new[] { "search", "replace_unique" }, session.VisibleDefinitions.Select(t => t.Name));
        var search = Read(await Invoke(session, "search", new { pattern = "北门" }));
        var group = Assert.Single(search.GetProperty("results").EnumerateArray());
        Assert.Equal(current.Address, group.GetProperty("target").GetString());
        Assert.Equal("A", group.GetProperty("entry").GetString());
        Assert.Equal("body", group.GetProperty("field").GetString());
        var matches = group.GetProperty("matches");
        Assert.Equal(2, matches.GetArrayLength());
        foreach (var match in matches.EnumerateArray()) {
            Assert.Equal(2, match.GetProperty("line").GetInt32());
            Assert.True(match.GetProperty("truncated_before").GetBoolean());
            Assert.True(match.GetProperty("truncated_after").GetBoolean());
            string excerpt = match.GetProperty("excerpt").GetString()!;
            Assert.InRange(excerpt.Length, 1, 120);
            Assert.Equal(excerpt, current.Editor.ToString().Substring(match.GetProperty("excerpt_start").GetInt32(), excerpt.Length));
        }
        var unclear = Read(await Invoke(session, "replace_unique", new { target = current.Address, old_text = "北门", new_text = "南门" }), ToolExecutionStatus.Failed);
        Assert.Equal("ambiguous", unclear.GetProperty("status").GetString());
        Assert.Equal(2, unclear.GetProperty("candidates").GetArrayLength());
        Assert.Equal(old.Editor.ToString(), current.Editor.ToString());

        Assert.Equal("replaced", Read(await Invoke(session, "replace_unique",
            new { target = current.Address, old_text = "二号北门", new_text = "二号南门" })).GetProperty("status").GetString());
        var now = Read(await Invoke(session, "search", new { pattern = "[二三]号南门", regex = true, target = current.Address }));
        Assert.Single(now.GetProperty("results")[0].GetProperty("matches").EnumerateArray());
        host.Visible = [old];
        var previous = Read(await Invoke(session, "search", new { pattern = "北门" }));
        Assert.Equal(2, previous.GetProperty("results")[0].GetProperty("matches").GetArrayLength());
        Assert.Empty(Read(await Invoke(session, "search", new { pattern = "南门" })).GetProperty("results").EnumerateArray());
    }

    [Fact]
    public async Task AmbiguousLongLiteralsKeepTheNeighborsNeededToEditOneOccurrence() {
        string needle = new('文', 200);
        var field = new TextToolTarget("memo:a", "a", "body", new TextEditor($"甲{needle}钥匙。乙{needle}车票。"));
        var session = Session(new Host(field));
        var response = Read(await Invoke(session, "replace_unique", new { target = field.Address, old_text = needle, new_text = "新" }), ToolExecutionStatus.Failed);
        Assert.Equal("ambiguous", response.GetProperty("status").GetString());
        var second = response.GetProperty("candidates")[1];
        string prefix = second.GetProperty("before").GetString()!;
        string suffix = second.GetProperty("after").GetString()!;
        Assert.InRange(prefix.Length + suffix.Length, 1, 120);
        Assert.EndsWith("乙", prefix);
        Assert.StartsWith("车票", suffix);
        Read(await Invoke(session, "replace_unique", new { target = field.Address, old_text = needle + suffix, new_text = "新" + suffix }));
        Assert.Equal($"甲{needle}钥匙。乙新车票。", field.Editor.ToString());
    }

    [Fact]
    public async Task TargetResolutionCoversHostOwnedFilesAndDoesNotRevealHiddenTargets() {
        string path = Path.GetTempFileName();
        try {
            await File.WriteAllTextAsync(path, "刪掉這行\n留下", Encoding.UTF8);
            var file = new TextToolTarget(new Uri(path).AbsoluteUri, Path.GetFileName(path), "contents", new TextEditor(await File.ReadAllTextAsync(path)));
            var host = new Host(file) { MayEdit = false };
            var session = Session(host);
            var remove = new { target = file.Address, old_text = "刪掉這行\n", new_text = "" };
            var search = Read(await Invoke(session, "search", new { target = file.Address, pattern = "這行" }));
            Assert.Equal(file.Address, search.GetProperty("results")[0].GetProperty("target").GetString());
            var denied = Read(await Invoke(session, "replace_unique", remove), ToolExecutionStatus.Failed);
            var absent = Read(await Invoke(session, "replace_unique", new { target = "file:///hidden", old_text = "a", new_text = "" }), ToolExecutionStatus.Failed);
            Assert.Equal("target_unavailable", denied.GetProperty("status").GetString());
            Assert.Equal(denied.GetRawText(), absent.GetRawText());
            Assert.Equal("刪掉這行\n留下", file.Editor.ToString());
            host.Visible = [];
            Assert.Empty(Read(await Invoke(session, "search", new { pattern = "這行" })).GetProperty("results").EnumerateArray());
            Assert.Equal(denied.GetRawText(), Read(await Invoke(session, "search", new { target = file.Address, pattern = "這行" }), ToolExecutionStatus.Failed).GetRawText());
            host.Visible = [file];
            host.MayEdit = true;
            Read(await Invoke(session, "replace_unique", remove));
            Assert.Equal("刪掉這行\n留下", await File.ReadAllTextAsync(path));
            await File.WriteAllTextAsync(path, file.Editor.ToString(), Encoding.UTF8);
            Assert.Equal("留下", await File.ReadAllTextAsync(path));
        }
        finally {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SearchLimitsApplyAcrossFieldsAndReportOnlyConfirmedOmissions() {
        var fields = Enumerable.Range(0, 40).Select(i =>
            new TextToolTarget($"memo:{i}", $"entry {i}", "text", new TextEditor(new string('a', 1000) + " gate " + new string('b', 2000)))).ToArray();
        var host = new Host(fields);
        var session = Session(host);
        var defaultResult = Read(await Invoke(session, "search", new { pattern = "gate" }));
        Assert.Equal(8, defaultResult.GetProperty("results").GetArrayLength());
        Assert.Equal("hits", defaultResult.GetProperty("limited_by").GetString());
        var sized = Read(await Invoke(session, "search", new { pattern = "gate", limit = 32, snippet_length = 512 }));
        Assert.True(sized.GetProperty("has_more").GetBoolean());
        Assert.Equal("text_budget", sized.GetProperty("limited_by").GetString());
        Assert.InRange(sized.GetProperty("results").GetArrayLength(), 1, 15);
        host.Visible = [fields[0], fields[1] with { Editor = new TextEditor("unrelated") }];
        var exact = Read(await Invoke(session, "search", new { pattern = "gate", limit = 1 }));
        Assert.False(exact.GetProperty("has_more").GetBoolean());

        host.Visible = [fields[0] with { Address = new string('X', 100_000) }];
        var oversized = Read(await Invoke(session, "search", new { pattern = "gate" }));
        Assert.Equal("text_budget", oversized.GetProperty("limited_by").GetString());
        Assert.True(oversized.GetRawText().Length < 256);
    }

    [Theory]
    [InlineData("😀😁", false, 2, "😀", 0)]
    [InlineData("😀😁", true, 2, "😀", 0)]
    [InlineData("😀😁", false, 1, "", 2)]
    [InlineData("A😀B", false, 1, "A", 0)]
    [InlineData("😀😁B", false, 1, "B", 4)]
    [InlineData("A😀😁😃😄B", false, 1, "", 5)]
    public async Task TightExcerptsReturnAnIntactCodePointWhenItFits(string body, bool regex, int budget, string excerpt, int excerptStart) {
        var session = Session(new Host(new TextToolTarget("memo:a", "A", "text", new(body))));
        var receipt = Read(await Invoke(session, "search", new { pattern = body, regex, snippet_length = budget }));
        var match = receipt.GetProperty("results")[0].GetProperty("matches")[0];
        Assert.Equal(0, match.GetProperty("start").GetInt32());
        Assert.Equal(body.Length, match.GetProperty("length").GetInt32());
        Assert.Equal(excerptStart, match.GetProperty("excerpt_start").GetInt32());
        Assert.Equal(excerpt, match.GetProperty("excerpt").GetString());
        Assert.Equal(excerptStart > 0, match.GetProperty("truncated_before").GetBoolean());
        Assert.Equal(excerptStart + excerpt.Length < body.Length, match.GetProperty("truncated_after").GetBoolean());
    }

    [Theory]
    [InlineData(8191, true)]  // Labels alone exceed the total budget.
    [InlineData(8189, true)]  // Labels fit, but leave no room for the first excerpt.
    [InlineData(8187, false)] // The field fits an empty page but not the remaining budget.
    public async Task OversizedFieldDoesNotHideReportableLaterTargets(int longLabel, bool expectLast) {
        var editor = new TextEditor("北门");
        TextToolTarget first = new("m:1", "", "", editor);
        TextToolTarget large = new("m:2", new string('L', longLabel), "", editor);
        TextToolTarget last = new("m:3", "", "", editor);
        var session = Session(new Host(first, large, last));
        var receipt = Read(await Invoke(session, "search", new { pattern = "北门", snippet_length = 2 }));
        Assert.Equal(expectLast ? new[] { "m:1", "m:3" } : new[] { "m:1" },
            receipt.GetProperty("results").EnumerateArray().Select(group => group.GetProperty("target").GetString()));
        Assert.True(receipt.GetProperty("has_more").GetBoolean());
        Assert.Equal("text_budget", receipt.GetProperty("limited_by").GetString());

        session = Session(new Host(large, last));
        var fromStart = Read(await Invoke(session, "search", new { pattern = "北门", snippet_length = 2 }));
        var only = Assert.Single(fromStart.GetProperty("results").EnumerateArray());
        Assert.Equal(expectLast ? "m:3" : "m:2", only.GetProperty("target").GetString());
        Assert.Equal(expectLast ? "" : large.Entry, only.GetProperty("entry").GetString());
        Assert.True(fromStart.GetProperty("has_more").GetBoolean());
        Assert.Equal("text_budget", fromStart.GetProperty("limited_by").GetString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OrderedIndependentEditsReadTheCurrentTextWithinAndAcrossTurns(bool oneTurn) {
        var field = new TextToolTarget("memo:a", "a", "body", new TextEditor("aaa"));
        var session = Session(new Host(field));
        (string Old, string New, string Status)[] calls = [("aa", "x", "ambiguous"), ("aaa", "b", "replaced"), ("aaa", "?", "not_found"), ("b", "c", "replaced")];
        var blocks = calls.Select((edit, i) => new ActionBlock.ToolCall(new RawToolCall("replace_unique", $"edit-{i}",
            JsonSerializer.Serialize(new { target = field.Address, old_text = edit.Old, new_text = edit.New }))));
        int executed = 0;
        foreach (var turn in blocks.Chunk(oneTurn ? calls.Length : 1).Select(chunk => new ActionMessage(chunk))) {
            foreach (var call in turn.ToolCalls) {
                var execution = await session.ExecuteAsync(call, CancellationToken.None);
                Assert.Equal($"edit-{executed}", execution.ToolCallId);
                string status = calls[executed++].Status;
                var response = Read(execution.ExecuteResult, status == "replaced" ? ToolExecutionStatus.Success : ToolExecutionStatus.Failed);
                Assert.Equal(status, response.GetProperty("status").GetString());
                if (status == "ambiguous") {
                    Assert.Equal(new[] { 0, 1 }, response.GetProperty("candidates").EnumerateArray().Select(m => m.GetProperty("start").GetInt32()));
                }
            }
        }
        Assert.Equal(calls.Length, executed);
        Assert.Equal("c", field.Editor.ToString());
    }

    [Fact]
    public async Task UnpairedSurrogatesNeverBecomeAnAddressOrAnchorForADifferentMatch() {
        var field = new TextToolTarget("memo:a", "a", "body", new TextEditor("\uD800门 分界 \uFFFD门"));
        var host = new Host(field);
        var session = Session(host);
        var search = new { pattern = "门", snippet_length = 3 };
        var expected = Read(await Invoke(session, "search", search), ToolExecutionStatus.Failed);
        Assert.Equal("unrepresentable_text", expected.GetProperty("status").GetString());
        Assert.False(expected.TryGetProperty("results", out _));

        var edit = new { target = field.Address, old_text = "门", new_text = "桥" };
        foreach (string body in new[] { "\uD800门 分界 \uFFFD门", "门\uDC00 分界 门\uFFFD" }) {
            var candidate = new TextEditor(body);
            host.Visible = [field with { Editor = candidate }];
            Assert.Equal(expected.GetRawText(), Read(await Invoke(session, "replace_unique", edit), ToolExecutionStatus.Failed).GetRawText());
            Assert.Equal(body, candidate.ToString());
        }
        var safe = field with { Editor = new TextEditor("门") };
        foreach (var target in new[] { safe with { Address = "memo:\uD800" }, safe with { Entry = "\uDC00" }, safe with { Field = "\uD800" } }) {
            host.Visible = [target];
            Assert.Equal(expected.GetRawText(), Read(await Invoke(session, "search", search), ToolExecutionStatus.Failed).GetRawText());
        }
        host.Visible = [safe with { Editor = new TextEditor("门" + new string('x', 1000) + "\uD800") }];
        var local = Read(await Invoke(session, "search", search));
        Assert.Equal("门xx", local.GetProperty("results")[0].GetProperty("matches")[0].GetProperty("excerpt").GetString());
    }

    [Fact]
    public async Task RegexTimeoutDoesNotReturnEarlierFieldsAsACompleteSearch() {
        var first = new TextToolTarget("memo:1", "1", "body", new TextEditor("aaaa"));
        var slow = new TextToolTarget("memo:2", "2", "body", new TextEditor(new string('a', 4096) + "!"));
        var session = Session(new Host(first, slow));
        var failed = Read(await Invoke(session, "search", new { pattern = "^(a+)+$", regex = true }), ToolExecutionStatus.Failed);
        Assert.Equal("regex_timeout", failed.GetProperty("status").GetString());
        Assert.False(failed.TryGetProperty("results", out _));
    }

    [Fact]
    public async Task InvalidPatternsAndCancelledEditsProduceNoWrites() {
        var field = new TextToolTarget("memo:a", "a", "body", new TextEditor(" "));
        var session = Session(new Host(field));
        Assert.Equal("invalid_regex", Read(await Invoke(session, "search", new { regex = true, pattern = "(" }), ToolExecutionStatus.Failed).GetProperty("status").GetString());
        Assert.Equal("empty_literal", Read(await Invoke(session, "search", new { pattern = "" }), ToolExecutionStatus.Failed).GetProperty("status").GetString());
        Assert.Equal(ToolExecutionStatus.Failed, (await Invoke(session, "search", new { pattern = " ", limit = 33 })).Status);
        Assert.Equal("empty_literal", Read(await Invoke(session, "replace_unique",
            new { target = field.Address, old_text = "", new_text = "x" }), ToolExecutionStatus.Failed).GetProperty("status").GetString());
        var edit = new { target = field.Address, old_text = " ", new_text = "x" };
        Assert.Equal(ToolExecutionStatus.Skipped, (await Invoke(session, "replace_unique", edit, new CancellationToken(true))).Status);
        Assert.Equal(" ", field.Editor.ToString());
        Read(await Invoke(session, "replace_unique", edit));
        Assert.Equal("x", field.Editor.ToString());
    }
}
