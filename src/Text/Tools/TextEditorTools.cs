using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;

namespace Atelia.Text;

/// <summary>Creates the <c>search</c> and <c>replace_unique</c> tools for a host's live text.</summary>
public static class TextEditorTools {
    private const int DefaultLimit = 8;
    private const int DefaultSnippetLength = 120;
    // Charge each returned field's labels once and each excerpt in full, including control characters.
    private const int TextBudget = 8192;
    private static readonly JsonSerializerOptions ReceiptJson = new() {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new ExactStringWriter() }
    };

    /// <summary>Returns tools for use in a <see cref="ToolRegistry"/> bound to this host.</summary>
    /// <remarks>Dispatch calls in model declaration order, including multiple calls in one message. Each call sees completed edits.</remarks>
    public static IReadOnlyList<ITool> Create(ITextToolHost host) {
        ArgumentNullException.ThrowIfNull(host);
        var implementation = new Handlers(host);
        return [
            MethodToolWrapper.FromDelegate<SearchInput>(implementation.Search),
            MethodToolWrapper.FromDelegate<ReplaceInput>(implementation.ReplaceUnique)
        ];
    }

    private sealed record SearchInput(
        [property: JsonPropertyName("pattern"), Description("Literal text (Ordinal, case-sensitive), or a .NET pattern with regex=true.")]
        string Pattern,
        [property: JsonPropertyName("target"), Description("One exact host address. Omit to search the fields visible in the current world and role.")]
        string? Target,
        [property: JsonPropertyName("regex"), Description("Set true for a .NET regular expression. Defaults to false.")]
        bool? Regex,
        [property: JsonPropertyName("limit"), Range(1, 32), Description("Maximum matches across all fields. Defaults to 8.")]
        int? Limit,
        [property: JsonPropertyName("snippet_length"), Range(1, 512), Description("Maximum UTF-16 units of nearby verbatim text per match. Defaults to 120.")]
        int? SnippetLength);

    private sealed record ReplaceInput(
        [property: JsonPropertyName("target"), Description("Exact host address of the field to edit, as returned by search.")]
        string Target,
        [property: JsonPropertyName("old_text"), Description("Nonempty exact literal. Include surrounding text when needed to identify one occurrence.")]
        string OldText,
        [property: JsonPropertyName("new_text"), Description("Literal replacement for the whole old_text, including any context to retain. An empty string deletes it.")]
        string NewText);

    private sealed record FieldHits(string Target, string Entry, string Field, List<TextMatch> Matches);

    private sealed class Handlers(ITextToolHost host) {
        [Tool("search", "Search the current visible text or one target. Results include addresses, 1-based lines and exact excerpts around matches. Truncation flags mark omitted text. Offsets count UTF-16 units.")]
        public ValueTask<ToolExecuteResult> Search(SearchInput input, ToolExecutionContext context, CancellationToken cancellationToken) {
            CheckCancellation(cancellationToken);
            Regex? regex = null;
            if (input.Regex == true) {
                try {
                    regex = new Regex(input.Pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
                }
                catch (RegexParseException ex) {
                    return Receipt(new { status = "invalid_regex", error = ex.Error.ToString(), offset = ex.Offset }, ToolExecutionStatus.Failed);
                }
            }
            else if (input.Pattern.Length == 0) {
                return Receipt(new { status = "empty_literal" }, ToolExecutionStatus.Failed);
            }

            IEnumerable<TextToolTarget> targets;
            if (input.Target is null) {
                targets = host.GetSearchTargets();
            }
            else if (host.Resolve(input.Target, forEdit: false) is { } target) {
                targets = [target];
            }
            else {
                return TargetUnavailable();
            }

            var results = new List<FieldHits>();
            int remainingHits = input.Limit ?? DefaultLimit;
            int excerptLength = input.SnippetLength ?? DefaultSnippetLength;
            int remainingText = TextBudget;
            string? limitedBy = null;
            foreach (var field in targets) {
                CheckCancellation(cancellationToken);
                TextSearchResult found;
                try {
                    found = regex is null
                        ? field.Editor.Search(input.Pattern, remainingHits, excerptLength)
                        : field.Editor.Search(regex, remainingHits, excerptLength);
                }
                catch (RegexMatchTimeoutException) {
                    return Receipt(new { status = "regex_timeout", hint = "Narrow the pattern or select a single target." }, ToolExecutionStatus.Failed);
                }

                if (found.Matches.Count != 0) {
                    long labelLength = (long)field.Address.Length + field.Entry.Length + field.Field.Length;
                    long firstHitCost = labelLength + found.Matches[0].Excerpt.Length;
                    if (firstHitCost > remainingText) {
                        limitedBy = "text_budget";
                        // This field cannot return even its first hit on an empty page.
                        if (firstHitCost > TextBudget) { continue; }
                        break;
                    }
                    remainingText -= (int)labelLength;
                    List<TextMatch> matches = [];
                    foreach (var match in found.Matches) {
                        if (match.Excerpt.Length > remainingText) {
                            break;
                        }
                        matches.Add(match);
                        remainingText -= match.Excerpt.Length;
                        remainingHits--;
                    }
                    results.Add(new(field.Address, field.Entry, field.Field, matches));
                    if (matches.Count < found.Matches.Count) {
                        limitedBy = "text_budget";
                        break;
                    }
                }
                if (found.HasMore) {
                    limitedBy ??= "hits";
                    break;
                }
            }
            return Receipt(new { status = "ok", results, hasMore = limitedBy is not null, limitedBy });
        }

        [Tool("replace_unique", "Replace one exact literal in the addressed field's current text. Ambiguous and missing matches leave it untouched. For ambiguity, extend old_text with nearby words from a candidate and retain those words in new_text. Each call executes independently in order.")]
        public ValueTask<ToolExecuteResult> ReplaceUnique(ReplaceInput input, ToolExecutionContext context, CancellationToken cancellationToken) {
            CheckCancellation(cancellationToken);
            if (input.OldText.Length == 0) {
                return Receipt(new { status = "empty_literal" }, ToolExecutionStatus.Failed);
            }
            var field = host.Resolve(input.Target, forEdit: true);
            if (field is null) { return TargetUnavailable(); }

            CheckCancellation(cancellationToken);
            var outcome = field.Editor.ReplaceUnique(input.OldText, input.NewText);
            switch (outcome.Status) {
                case ReplaceUniqueStatus.NotFound:
                    return Receipt(new { status = "not_found" }, ToolExecutionStatus.Failed);
                case ReplaceUniqueStatus.Ambiguous:
                    return Receipt(new {
                        status = "ambiguous",
                        candidates = outcome.Candidates, outcome.HasMore,
                        hint = "Extend old_text with a suffix of before or a prefix of after from the intended candidate. Retain those words in new_text."
                    }, ToolExecutionStatus.Failed);
                case ReplaceUniqueStatus.Replaced:
                    var before = outcome.Candidates[0];
                    return Receipt(new {
                        status = "replaced", before.Line, before.Start,
                        removedLength = before.Length, insertedLength = input.NewText.Length
                    });
                default:
                    throw new InvalidOperationException("Unrecognized replacement outcome.");
            }
        }
    }

    private static ValueTask<ToolExecuteResult> TargetUnavailable() =>
        Receipt(new { status = "target_unavailable" }, ToolExecutionStatus.Failed);

    private static ValueTask<ToolExecuteResult> Receipt<T>(T body, ToolExecutionStatus status = ToolExecutionStatus.Success) {
        try {
            return ValueTask.FromResult(ToolExecuteResult.FromText(status, JsonSerializer.Serialize(body, ReceiptJson)));
        }
        catch (EncoderFallbackException) {
            return ValueTask.FromResult(ToolExecuteResult.FromText(ToolExecutionStatus.Failed,
                """{"status":"unrepresentable_text","hint":"A result contains an isolated UTF-16 surrogate. Use a narrower search or access the exact code units through TextEditor."}"""));
        }
    }

    // System.Text.Json normally writes a replacement character for an unpaired surrogate.
    // In an editing anchor or address that character could identify a different occurrence or field.
    private sealed class ExactStringWriter : JsonConverter<string> {
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) {
            _ = StrictUtf8.GetByteCount(value);
            writer.WriteStringValue(value);
        }

        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException();
    }

    private static void CheckCancellation(CancellationToken cancellationToken) {
        if (cancellationToken.IsCancellationRequested) { throw new ToolExecutionCancelledBeforeMutationException(cancellationToken); }
    }
}
