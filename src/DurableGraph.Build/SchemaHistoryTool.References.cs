using System.Text;
using Atelia.DurableGraph.SchemaHistory;

namespace Atelia.DurableGraph.Build;

internal sealed partial class SchemaHistoryTool {
    private static Dictionary<SchemaHistoryKey, SchemaHistoryRecord> LoadAvailable(
        string? expectedHash,
        string? referenceManifestPath,
        IReadOnlyList<SchemaHistoryRecord> candidates,
        IReadOnlyDictionary<SchemaHistoryKey, ExistingSchemaHistoryRecord> existing) {
        IReadOnlyList<SchemaHistoryReferenceEntry> references = Array.Empty<SchemaHistoryReferenceEntry>();
        if (referenceManifestPath is null) {
            if (expectedHash is not null) throw new SchemaHistoryException("manifest requires its matching --reference-manifest input");
        } else {
            try {
                byte[] bytes = File.ReadAllBytes(referenceManifestPath);
                string text = SchemaHistoryTextCodec.Utf8NoBom.GetString(bytes);
                references = SchemaHistoryReferenceProtocol.Parse(text);
                if (expectedHash is not null && expectedHash != SchemaHistoryReferenceProtocol.ComputeHash(text)) {
                    throw new SchemaHistoryException("reference manifest SHA256 does not match the owned manifest");
                }
                if (expectedHash is null && references.Count != 0) {
                    throw new SchemaHistoryException("nonempty reference manifest requires an owned manifest reference hash");
                }
            } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException or DecoderFallbackException or EncoderFallbackException) {
                throw new SchemaHistoryException($"reference manifest '{referenceManifestPath}' is invalid: {exception.Message}");
            }
        }

        HashSet<string> ownedIds = existing.Keys.Select(key => key.SchemaId).Concat(candidates.Select(record => record.SchemaId)).ToHashSet(StringComparer.Ordinal);
        Dictionary<SchemaHistoryKey, SchemaHistoryRecord> available = existing.ToDictionary(pair => pair.Key, pair => pair.Value.Record);
        Dictionary<SchemaHistoryKey, SchemaHistoryRecord> imported = new();
        foreach (SchemaHistoryReferenceEntry reference in references) {
            if (ownedIds.Contains(reference.SchemaId)) {
                throw new SchemaHistoryException($"reference schema '{reference.SchemaId}' belongs to an owned definition; imports cannot supply owned history");
            }
            SchemaHistoryRecord record = SchemaHistoryTextCodec.ParseReference(reference);
            imported.Add(record.Key, record);
            available.Add(record.Key, record);
        }
        // A dependency library cannot borrow the consumer's definition to fill a missing external export.
        ValidateClosure(imported);
        // Accepted owned history may depend on read-only imported history, but never on current candidates.
        ValidateClosure(available);
        return available;
    }
}
