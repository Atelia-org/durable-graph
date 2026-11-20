using System.Text;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;
using Atelia.Text;

namespace TextEditorConsumer;

// Interactive JSONL and replay share the same session and the same published tool definitions.
internal static class TextToolTrial {
    private const string Address = "memo:signal-watch/full";
    private const string Second = "潮汐班在北门收回灯具";
    private const string Summary = "交班地点及器材见正文";
    private const string PrivateNote = "北门私人备忘";

    private sealed class Host(Entry current, Entry archive) : ITextToolHost {
        public bool Archived { get; set; }
        public IEnumerable<TextToolTarget> GetSearchTargets() {
            var visible = Archived ? archive : current;
            yield return new(Address, "signal-watch", "FullText", visible.Body);
            yield return new("memo:signal-watch/summary", "signal-watch", "Summary", visible.Alias);
            // Bodies[0] belongs to another role and is never exposed to this session.
        }
        public TextToolTarget? Resolve(string target, bool forEdit) =>
            Archived && forEdit ? null : GetSearchTargets().FirstOrDefault(item => item.Address == target);
    }

    internal static async Task<int> RunAsync(string database, string? commands) {
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);
        var models = new StateModelRegistry();
        TextEditorPersistence.Register(models);
        models.Register(GraphRef.Definition);
        Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
        string original = "交班记录\r\n" + new string('·', 40_000) + "晨钟班在北门领取灯具，"
            + new string('·', 40_000) + Second + "。" + new string('·', 40_000);
        using var repo = Repository.CreateNew(database, models);
        var old = repo.CreateRootBranchFromState("archive", new Entry {
            Body = new(original), Alias = new(Summary), Bodies = [new(PrivateNote)]
        });
        using var archive = repo.OpenWorkingCopy(old.Id);
        using var current = repo.OpenWorkingCopy(repo.ForkBranch(archive.BaseCommitId, "current").Id);
        var editable = (Entry)current.State!;
        var archived = (Entry)archive.State!;
        var host = new Host(editable, archived);
        var session = new ToolRegistry(TextEditorTools.Create(host)).CreateSession();
        foreach (var tool in session.VisibleDefinitions) {
            var schema = (ToolSchema.Object)tool.InputSchema;
            Console.WriteLine(JsonSerializer.Serialize(new {
                tool = tool.Name, tool.Description, parameters = schema.Properties.Select(p => new {
                    p.Name, p.IsRequired, p.Schema.Description
                })
            }));
        }
        using var file = commands is null ? null : File.OpenText(commands);
        var input = file ?? Console.In;
        bool sawAmbiguity = false;
        while (await input.ReadLineAsync() is string line) {
            using var document = JsonDocument.Parse(line);
            var command = document.RootElement;
            if (command.TryGetProperty("world", out var world)) {
                host.Archived = world.GetString() switch {
                    "current" => false, "archive" => true, _ => throw new InvalidDataException("Unknown world.")
                };
                Console.WriteLine(world.GetRawText());
            } else if (command.TryGetProperty("verify", out _)) {
                var stored = (Entry)repo.LoadState(current.BaseCommitId);
                string revised = original.Replace(Second, Second.Replace("北门", "南门"));
                if (!sawAmbiguity || editable.Body.ToString() != revised
                    || editable.Alias.ToString() != Summary || editable.Bodies.Single().ToString() != PrivateNote
                    || archived.Body.ToString() != original || stored.Body.ToString() != original
                    || current.BaseCommitId != archive.BaseCommitId) {
                    throw new InvalidDataException("Trial failed the ambiguity, exact edit, visibility or unpublished-state check.");
                }
                host.Archived = false;
                await VerifySearch(session, revised, "北门", 1);
                await VerifySearch(session, revised, "南门", 1);
                host.Archived = true;
                await VerifySearch(session, original, "北门", 2);
                await VerifySearch(session, original, "南门", 0);
                Console.WriteLine("PASS: exact edit, current and archived search receipts, unsaved and role-private text.");
                return 0;
            } else {
                string name = command.GetProperty("tool").GetString()!;
                var call = new RawToolCall(name, $"trial-{session.LastIssuedExecutionSequence + 1}", command.GetProperty("input").GetRawText());
                var receipt = (await session.ExecuteAsync(call, CancellationToken.None)).ExecuteResult.GetFlattenedText();
                Console.WriteLine(receipt);
                if (name == "replace_unique" && receipt.StartsWith('{')) {
                    using var parsed = JsonDocument.Parse(receipt);
                    if (parsed.RootElement.TryGetProperty("status", out var status) && status.GetString() == "ambiguous") {
                        if (editable.Body.ToString() != original) throw new InvalidDataException("Ambiguous edit modified the draft.");
                        sawAmbiguity = true;
                    }
                }
            }
        }
        throw new InvalidDataException("End the trial with a verify command.");
    }

    private static async Task VerifySearch(ToolSession session, string source, string pattern, int expectedCount) {
        var call = new RawToolCall("search", $"check-{session.LastIssuedExecutionSequence + 1}", JsonSerializer.Serialize(new { pattern }));
        var receipt = (await session.ExecuteAsync(call, CancellationToken.None)).ExecuteResult;
        using var result = JsonDocument.Parse(receipt.GetFlattenedText());
        var root = result.RootElement;
        var groups = root.GetProperty("results");
        if (root.GetProperty("status").GetString() != "ok" || root.GetProperty("has_more").GetBoolean()
            || groups.GetArrayLength() != (expectedCount == 0 ? 0 : 1)) throw new InvalidDataException("Unexpected visible search results.");
        if (expectedCount == 0) return;
        var field = groups[0];
        var matches = field.GetProperty("matches");
        if (field.GetProperty("target").GetString() != Address || field.GetProperty("entry").GetString() != "signal-watch"
            || field.GetProperty("field").GetString() != "FullText" || matches.GetArrayLength() != expectedCount) {
            throw new InvalidDataException("Wrong field or hit count.");
        }
        int cursor = 0;
        foreach (var hit in matches.EnumerateArray()) {
            int expected = source.IndexOf(pattern, cursor, StringComparison.Ordinal);
            int offset = hit.GetProperty("excerpt_start").GetInt32();
            string excerpt = hit.GetProperty("excerpt").GetString()!;
            if (hit.GetProperty("line").GetInt32() != 2 || hit.GetProperty("end_line").GetInt32() != 2
                || hit.GetProperty("start").GetInt32() != expected || hit.GetProperty("length").GetInt32() != pattern.Length
                || excerpt.Length > 120 || offset > expected || offset + excerpt.Length < expected + pattern.Length
                || !source.AsSpan(offset, excerpt.Length).SequenceEqual(excerpt)
                || !hit.GetProperty("truncated_before").GetBoolean() || !hit.GetProperty("truncated_after").GetBoolean()) {
                throw new InvalidDataException("Wrong location, excerpt or clipping flags.");
            }
            cursor = expected + pattern.Length;
        }
    }
}
