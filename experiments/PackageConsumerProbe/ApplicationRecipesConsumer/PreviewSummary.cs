using System.Text;
using System.Text.Json;

namespace ApplicationRecipes;

public sealed record GardenPreview(int Day, int Flowers, bool Won);

public enum SummaryStatus { None, Unsupported, Invalid, Available }

public sealed record SummaryReadResult(SummaryStatus Status, GardenPreview? Preview);

/// <summary>An application preview protocol, independent of DurableSchema.</summary>
public static class SummaryCodec {
    public const int MaxBytes = 4096;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    // Finish domain edits first, then project and commit without further graph changes.
    // This copies three values; it does not freeze the source graph or acquire a lock.
    public static GardenPreview FromState(GardenState state) {
        ArgumentNullException.ThrowIfNull(state);
        var preview = new GardenPreview(state.Day, state.Flowers, state.Won);
        if (!IsValid(preview)) { throw new ArgumentOutOfRangeException(nameof(state)); }
        return preview;
    }

    public static byte[] Encode(GardenPreview preview) {
        ArgumentNullException.ThrowIfNull(preview);
        if (!IsValid(preview)) { throw new ArgumentOutOfRangeException(nameof(preview)); }
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new {
            schemaVersion = 1,
            day = preview.Day,
            flowers = preview.Flowers,
            won = preview.Won,
        });
        if (bytes.Length > MaxBytes) { throw new ArgumentOutOfRangeException(nameof(preview)); }
        return bytes;
    }

    // Supply bytes only AFTER repository reading succeeds. Storage/read failures must
    // propagate to the caller; this decoder only classifies application preview data.
    public static SummaryReadResult Read(ReadOnlyMemory<byte> bytes) {
        if (bytes.IsEmpty) { return new(SummaryStatus.None, null); }
        if (bytes.Length > MaxBytes) { return new(SummaryStatus.Invalid, null); }
        try {
            // Validate even strings in unknown properties, which JsonDocument can leave undecoded.
            _ = StrictUtf8.GetCharCount(bytes.Span);
            using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { return new(SummaryStatus.Invalid, null); }
            int versionFields = 0;
            foreach (JsonProperty property in root.EnumerateObject()) {
                if (property.NameEquals("schemaVersion")) { versionFields++; }
            }
            if (versionFields != 1 || !TryInt(root, "schemaVersion", out int version) || version <= 0) {
                return new(SummaryStatus.Invalid, null);
            }
            // A future version may have completely different fields. Do not validate it as V1.
            if (version != 1) { return new(SummaryStatus.Unsupported, null); }
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in root.EnumerateObject()) {
                if (!names.Add(property.Name)) { return new(SummaryStatus.Invalid, null); }
            }
            if (!TryInt(root, "day", out int day) || !TryInt(root, "flowers", out int flowers)
                || !root.TryGetProperty("won", out JsonElement won)
                || won.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) {
                return new(SummaryStatus.Invalid, null);
            }
            var preview = new GardenPreview(day, flowers, won.GetBoolean());
            return IsValid(preview)
                ? new(SummaryStatus.Available, preview)
                : new(SummaryStatus.Invalid, null);
        } catch (JsonException) {
            return new(SummaryStatus.Invalid, null);
        } catch (DecoderFallbackException) {
            return new(SummaryStatus.Invalid, null);
        }
    }

    private static bool TryInt(JsonElement root, string name, out int value) {
        value = 0;
        return root.TryGetProperty(name, out JsonElement field)
            && field.ValueKind == JsonValueKind.Number && field.TryGetInt32(out value);
    }

    private static bool IsValid(GardenPreview preview) =>
        preview.Day is >= 0 and <= 7 && preview.Flowers is >= 0 and <= 3
        && (!preview.Won || (preview.Day == 7 && preview.Flowers == 3));
}
