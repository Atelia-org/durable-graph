using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Atelia.DurableGraph.SchemaHistory;

namespace Atelia.DurableGraph.Build;

internal sealed partial class SchemaHistoryTool {
    private const string SchemaHistoryExtension = ".dgschema";

    public SchemaHistoryResult Publish(
        string manifestPath,
        string schemaHistoryDirectory,
        string? referenceManifestPath = null) {
        IReadOnlyList<SchemaHistoryRecord> candidates = SchemaHistoryTextCodec.ParseManifest(manifestPath);

        Dictionary<SchemaHistoryKey, ExistingSchemaHistoryRecord> existing = Directory.Exists(schemaHistoryDirectory)
            ? LoadHistory(schemaHistoryDirectory)
            : new Dictionary<SchemaHistoryKey, ExistingSchemaHistoryRecord>();
        Dictionary<SchemaHistoryKey, SchemaHistoryRecord> available = LoadAvailable(
            manifestPath, referenceManifestPath, candidates, existing);
        if (candidates.Count == 0 && !Directory.Exists(schemaHistoryDirectory)) {
            return new SchemaHistoryResult("published 0 schema-history record(s); 0 already exact");
        }

        foreach (SchemaHistoryRecord candidate in candidates) {
            if (available.TryGetValue(candidate.Key, out SchemaHistoryRecord? historical) &&
                !candidate.ShapeEquals(historical)) {
                throw new SchemaHistoryException(
                    $"history conflicts with manifest for schema '{candidate.SchemaId}' version {candidate.Version}");
            }

            available[candidate.Key] = candidate;
        }

        ValidateClosure(available);
        List<PendingSchemaHistoryRecord> pending = new();
        int unchangedCount = 0;

        foreach (SchemaHistoryRecord candidate in candidates) {
            SchemaHistoryKey key = candidate.Key;

            if (existing.ContainsKey(key)) {
                unchangedCount++;
                continue;
            }

            string canonicalContent = SchemaHistoryTextCodec.RenderHistory(candidate);
            string fileName = SchemaHistoryTextCodec.GetHistoryFileName(candidate, canonicalContent);
            string destinationPath = Path.Combine(schemaHistoryDirectory, fileName);

            if (File.Exists(destinationPath)) {
                throw new SchemaHistoryException(
                    $"history destination '{fileName}' already exists but was not a valid indexed record");
            }

            pending.Add(new PendingSchemaHistoryRecord(destinationPath, canonicalContent));
        }

        Directory.CreateDirectory(schemaHistoryDirectory);

        foreach (PendingSchemaHistoryRecord record in pending) {
            PublishCreateOnly(record);
        }

        return new SchemaHistoryResult(
            $"published {pending.Count} schema-history record(s); {unchangedCount} already exact");
    }

    public SchemaHistoryResult Verify(
        string manifestPath,
        string schemaHistoryDirectory,
        string? referenceManifestPath = null) {
        IReadOnlyList<SchemaHistoryRecord> candidates = SchemaHistoryTextCodec.ParseManifest(manifestPath);
        Dictionary<SchemaHistoryKey, ExistingSchemaHistoryRecord> existing = Directory.Exists(schemaHistoryDirectory)
            ? LoadHistory(schemaHistoryDirectory)
            : new Dictionary<SchemaHistoryKey, ExistingSchemaHistoryRecord>();
        LoadAvailable(manifestPath, referenceManifestPath, candidates, existing);

        foreach (SchemaHistoryRecord candidate in candidates) {
            if (!existing.TryGetValue(candidate.Key, out ExistingSchemaHistoryRecord? historical)) {
                throw new SchemaHistoryException(
                    $"history is missing schema '{candidate.SchemaId}' version {candidate.Version}");
            }

            if (!candidate.ShapeEquals(historical.Record)) {
                throw new SchemaHistoryException(
                    $"history conflicts with manifest for schema '{candidate.SchemaId}' version {candidate.Version}");
            }
        }

        return new SchemaHistoryResult(
            $"verified {candidates.Count} current manifest candidate(s) against {existing.Count} schema-history record(s)");
    }

    private static Dictionary<SchemaHistoryKey, ExistingSchemaHistoryRecord> LoadHistory(
        string schemaHistoryDirectory) {
        if (Directory.EnumerateFiles(schemaHistoryDirectory, "*.dgsnapshot", SearchOption.TopDirectoryOnly).Any()) {
            throw new SchemaHistoryException(
                "DurableGraph Schema-history directory contains legacy .dgsnapshot files; " +
                "regenerate them as .dgschema because legacy history is not accepted.");
        }

        Dictionary<SchemaHistoryKey, ExistingSchemaHistoryRecord> records = new();
        string[] paths = Directory.GetFiles(
            schemaHistoryDirectory,
            $"*{SchemaHistoryExtension}",
            SearchOption.TopDirectoryOnly);
        Array.Sort(paths, StringComparer.Ordinal);

        foreach (string path in paths) {
            SchemaHistoryRecord record = SchemaHistoryTextCodec.ParseHistory(path);
            string canonicalContent = SchemaHistoryTextCodec.RenderHistory(record, record.SourceFormatVersion);
            string expectedFileName = SchemaHistoryTextCodec.GetHistoryFileName(record, canonicalContent);
            string actualFileName = Path.GetFileName(path);

            if (!StringComparer.Ordinal.Equals(actualFileName, expectedFileName)) {
                throw new SchemaHistoryException(
                    $"history file '{actualFileName}' must be named '{expectedFileName}'");
            }

            if (records.TryGetValue(record.Key, out ExistingSchemaHistoryRecord? duplicate)) {
                string conflict = record.ShapeEquals(duplicate.Record)
                    ? "duplicates"
                    : "conflicts with";
                throw new SchemaHistoryException(
                    $"history file '{actualFileName}' {conflict} '{duplicate.FileName}' for schema '{record.SchemaId}' version {record.Version}");
            }

            records.Add(
                record.Key,
                new ExistingSchemaHistoryRecord(actualFileName, record));
        }

        return records;
    }

    private static void ValidateClosure(IReadOnlyDictionary<SchemaHistoryKey, SchemaHistoryRecord> records) {
        const int maximumDepth = 256;
        Dictionary<string, int> nominalArities = new(StringComparer.Ordinal);
        Dictionary<string, int> nominalKinds = new(StringComparer.Ordinal);
        foreach (SchemaHistoryRecord record in records.Values) {
            RequireArity(record.SchemaId, record.Arity);
            RequireKind(record.SchemaId, record.Kind);
            if (record.BaseType is not null) RequirePattern(record.BaseType, 1);
            foreach (SchemaHistoryField field in record.Fields) {
                RequirePattern(field.ValuePattern, field.TypeTag == 15 ? 1 : field.TypeTag == 16 ? 2 : 0);
            }
        }

        void RequireArity(string id, int arity) {
            if (nominalArities.TryGetValue(id, out int previous) && previous != arity) {
                throw new SchemaHistoryException($"schema family '{id}' changes arity or has conflicting nominal arity");
            }
            nominalArities[id] = arity;
        }

        void RequireKind(string id, int kind) {
            if (nominalKinds.TryGetValue(id, out int previous) && previous != kind) {
                throw new SchemaHistoryException($"schema family '{id}' changes kind or has conflicting reference kind");
            }
            nominalKinds[id] = kind;
        }

        void RequirePattern(TypePattern pattern, int rootKind) {
            if (rootKind != 0 && pattern.Kind == PatternKind.Named) RequireKind(pattern.DefinitionId!, rootKind);
            foreach (TypePattern named in pattern.NamedNodes()) RequireArity(named.DefinitionId!, named.Arguments.Count);
            RequireNullableKinds(pattern);
        }

        void RequireNullableKinds(TypePattern pattern) {
            if (pattern.IsNullable && pattern.ElementType!.Kind == PatternKind.Named) {
                RequireKind(pattern.ElementType.DefinitionId!, 2);
            }
            foreach (TypePattern argument in pattern.Arguments) { RequireNullableKinds(argument); }
        }

        Dictionary<string, int> kinds = new(StringComparer.Ordinal);
        foreach (SchemaHistoryRecord record in records.Values) {
            if (kinds.TryGetValue(record.SchemaId, out int kind) && kind != record.Kind * 64 + record.Arity) {
                throw new SchemaHistoryException($"schema family '{record.SchemaId}' changes kind or arity across versions");
            }
            kinds[record.SchemaId] = record.Kind * 64 + record.Arity;
            if (record.Kind == 2 && record.BaseSchema is not null) {
                throw new SchemaHistoryException($"inline schema '{record.SchemaId}' cannot have a base schema");
            }
            List<TypePattern> patterns = record.Fields.Select(field => field.ValuePattern).ToList();
            if (record.BaseType is not null) patterns.Add(record.BaseType);
            foreach (TypePattern pattern in patterns) {
                if (!pattern.ParametersFit(record.Arity)) {
                    throw new SchemaHistoryException($"schema '{record.SchemaId}' has an unbound type parameter");
                }
                foreach (TypePattern named in pattern.NamedNodes()) {
                    foreach (SchemaHistoryRecord target in records.Values) {
                        if (target.SchemaId == named.DefinitionId && target.Arity != named.Arguments.Count) {
                            throw new SchemaHistoryException($"schema '{record.SchemaId}' has a wrong-arity type pattern for '{target.SchemaId}'");
                        }
                    }
                }
            }

            // Retain the stronger CLR ancestry rule: a base chain cannot repeat a family,
            // even through a different exact version. Nominal references are not dependencies.
            HashSet<string> ancestors = new(StringComparer.Ordinal) { record.SchemaId };
            SchemaHistoryRecord current = record;
            while (current.BaseSchema is SchemaHistoryKey baseKey) {
                if (!ancestors.Add(baseKey.SchemaId)) {
                    throw new SchemaHistoryException(
                        $"schema '{record.SchemaId}' version {record.Version} repeats ancestor schema '{baseKey.SchemaId}'");
                }
                if (ancestors.Count > maximumDepth) {
                    throw new SchemaHistoryException($"exact schema dependency depth exceeds {maximumDepth}");
                }
                current = Resolve(current, baseKey, "base", 1, current.BaseType!);
            }
        }

        Dictionary<SchemaHistoryKey, int> heights = new();
        HashSet<SchemaHistoryKey> visiting = new();
        foreach (SchemaHistoryRecord record in records.Values) {
            Visit(record, 1);
        }

        SchemaHistoryRecord Resolve(SchemaHistoryRecord owner, SchemaHistoryKey key, string edge, int expectedKind, TypePattern pattern) {
            if (!records.TryGetValue(key, out SchemaHistoryRecord? dependency)) {
                throw new SchemaHistoryException(
                    $"schema '{owner.SchemaId}' version {owner.Version} is missing {edge} schema '{key.SchemaId}' version {key.Version}");
            }
            if (dependency.Kind != expectedKind) {
                throw new SchemaHistoryException(
                    $"schema '{owner.SchemaId}' has {edge} schema '{key.SchemaId}' with incompatible kind {dependency.Kind}");
            }
            if (dependency.Arity != pattern.Arguments.Count) {
                throw new SchemaHistoryException($"schema '{owner.SchemaId}' has wrong arity for {edge} schema '{key.SchemaId}'");
            }
            return dependency;
        }

        int Visit(SchemaHistoryRecord record, int depth) {
            if (depth > maximumDepth) {
                throw new SchemaHistoryException($"exact schema dependency depth exceeds {maximumDepth}");
            }
            if (heights.TryGetValue(record.Key, out int cached)) {
                if (depth + cached - 1 > maximumDepth) {
                    throw new SchemaHistoryException($"exact schema dependency depth exceeds {maximumDepth}");
                }
                return cached;
            }
            if (!visiting.Add(record.Key)) {
                throw new SchemaHistoryException($"exact schema dependency cycle at '{record.SchemaId}' version {record.Version}");
            }
            int height = 1;
            if (record.BaseSchema is SchemaHistoryKey baseKey) {
                height = Math.Max(height, 1 + Visit(Resolve(record, baseKey, "base", 1, record.BaseType!), depth + 1));
            }
            foreach (SchemaHistoryField field in record.Fields) {
                if (field.InlineSchema is SchemaHistoryKey inlineKey) {
                    TypePattern inlinePattern = field.ValuePattern.IsNullable ? field.ValuePattern.ElementType! : field.ValuePattern;
                    height = Math.Max(height, 1 + Visit(Resolve(record, inlineKey, "inline", 2, inlinePattern), depth + 1));
                }
            }
            visiting.Remove(record.Key);
            heights.Add(record.Key, height);
            return height;
        }
    }

    private static void PublishCreateOnly(PendingSchemaHistoryRecord record) {
        string directory = Path.GetDirectoryName(record.DestinationPath)
            ?? throw new SchemaHistoryException("history destination has no directory");
        string fileName = Path.GetFileName(record.DestinationPath);
        string temporaryPath = Path.Combine(
            directory,
            $".{fileName}.{Guid.NewGuid():N}.tmp");

        try {
            byte[] bytes = SchemaHistoryTextCodec.Utf8NoBom.GetBytes(record.Content);

            using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None)) {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, record.DestinationPath, overwrite: false);
        } finally {
            if (File.Exists(temporaryPath)) {
                File.Delete(temporaryPath);
            }
        }
    }

    private sealed record ExistingSchemaHistoryRecord(string FileName, SchemaHistoryRecord Record);

    private sealed record PendingSchemaHistoryRecord(string DestinationPath, string Content);
}

internal static class SchemaHistoryTextCodec {
    private const string ManifestHeader = "// durable-graph-schema-history-manifest:";
    private const string HistoryHeader = "// durable-graph-schema-history:";
    private const string SchemaBegin = "// schema-begin";
    private const string SchemaEnd = "// schema-end";
    private const string SchemaIdPrefix = "// schema-id-base64:";
    private const string VersionPrefix = "// version:";
    private const string KindPrefix = "// kind:";
    private const string BasePrefix = "// base:";
    private const string FieldPrefix = "// field:";

    internal static readonly UTF8Encoding Utf8NoBom = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static IReadOnlyList<SchemaHistoryRecord> ParseManifest(string path) {
        return ParseManifestText(path, ReadUtf8(path, allowByteOrderMark: true));
    }

    internal static IReadOnlyList<SchemaHistoryRecord> ParseManifestText(string path, string text) {
        text = StripReferenceHash(path, text, out _);
        IReadOnlyList<SchemaHistoryRecord> records = Parse(
            path,
            text,
            ManifestHeader,
            requireExactlyOneRecord: false);
        Dictionary<SchemaHistoryKey, SchemaHistoryRecord> distinct = new();

        foreach (SchemaHistoryRecord record in records) {
            if (distinct.TryGetValue(record.Key, out SchemaHistoryRecord? duplicate)) {
                string reason = record.ShapeEquals(duplicate)
                    ? "duplicates a record"
                    : "has a conflicting shape";
                throw Invalid(
                    path,
                    $"schema '{record.SchemaId}' version {record.Version} {reason}");
            }

            distinct.Add(record.Key, record);
        }

        return records;
    }

    internal static string StripReferenceHash(string path, string text, out string? hash) {
        const string prefix = "// references-sha256:";
        hash = null;
        int marker = text.IndexOf(prefix, StringComparison.Ordinal);
        if (marker < 0) return text;
        if (marker == 0 || text[marker - 1] != '\n') throw Invalid(path, "invalid reference hash position");
        string suffix = text.Substring(marker + prefix.Length);
        if (suffix.EndsWith("\r\n", StringComparison.Ordinal)) suffix = suffix.Substring(0, suffix.Length - 2);
        else if (suffix.EndsWith("\n", StringComparison.Ordinal)) suffix = suffix.Substring(0, suffix.Length - 1);
        if (suffix.Length != 64 || suffix.Any(character => !(character >= '0' && character <= '9' || character >= 'a' && character <= 'f'))) {
            throw Invalid(path, "reference hash must be one terminal lowercase SHA256 line");
        }
        hash = suffix;
        return text.Substring(0, marker);
    }

    internal static string ReadManifestText(string path) => ReadUtf8(path, allowByteOrderMark: true);

    internal static SchemaHistoryRecord ParseReference(SchemaHistoryReferenceEntry entry) {
        string path = entry.Owner + ":" + entry.SchemaId;
        IReadOnlyList<SchemaHistoryRecord> records = Parse(path, entry.Manifest, ManifestHeader, requireExactlyOneRecord: true);
        SchemaHistoryRecord record = records[0];
        string canonical = RenderHistory(record).Replace(HistoryHeader, ManifestHeader, StringComparison.Ordinal);
        int expectedKind = entry.ContractVersion switch { 1 => 2, 2 => 1, _ => 0 };
        if (record.SourceFormatVersion != 9 || record.Kind != expectedKind || record.SchemaId != entry.SchemaId || record.Version != entry.SchemaVersion ||
            !StringComparer.Ordinal.Equals(canonical, entry.Manifest)) {
            throw Invalid(path, "reference must be a canonical v9 manifest matching its exported ID/version and execution contract (1: inline, 2: class)");
        }
        return record;
    }

    public static SchemaHistoryRecord ParseHistory(string path) {
        byte[] bytes;

        try {
            bytes = File.ReadAllBytes(path);
        } catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException) {
            throw Invalid(path, exception.Message);
        }

        string text = DecodeUtf8(path, bytes, allowByteOrderMark: false);
        SchemaHistoryRecord record = Parse(
            path,
            text,
            HistoryHeader,
            requireExactlyOneRecord: true)[0];
        byte[] canonicalBytes = Utf8NoBom.GetBytes(RenderHistory(record, record.SourceFormatVersion));

        if (!bytes.AsSpan().SequenceEqual(canonicalBytes)) {
            throw Invalid(path, "content is not canonical UTF-8 with LF line endings");
        }

        return record;
    }

    public static string RenderHistory(SchemaHistoryRecord record, int formatVersion = 9) {
        StringBuilder builder = new();
        builder.Append(HistoryHeader).AppendLine(formatVersion.ToString(CultureInfo.InvariantCulture));
        AppendSchemaRecord(builder, record, formatVersion);
        return builder.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    public static string GetHistoryFileName(
        SchemaHistoryRecord record,
        string canonicalContent) {
        string schemaHash = ToLowerHex(SHA256.HashData(Utf8NoBom.GetBytes(record.SchemaId)));
        string contentHash = ToLowerHex(SHA256.HashData(Utf8NoBom.GetBytes(canonicalContent)));
        return $"schema.{schemaHash}.V{record.Version.ToString(CultureInfo.InvariantCulture)}.{contentHash}.dgschema";
    }

    private static IReadOnlyList<SchemaHistoryRecord> Parse(
        string path,
        string text,
        string expectedHeader,
        bool requireExactlyOneRecord) {
        string[] lines = SplitLines(path, text);

        if (lines.Length == 0 ||
            (!StringComparer.Ordinal.Equals(lines[0], expectedHeader + "1") &&
             !StringComparer.Ordinal.Equals(lines[0], expectedHeader + "2") &&
             !StringComparer.Ordinal.Equals(lines[0], expectedHeader + "3") &&
             !StringComparer.Ordinal.Equals(lines[0], expectedHeader + "4") &&
             !StringComparer.Ordinal.Equals(lines[0], expectedHeader + "5") &&
             !StringComparer.Ordinal.Equals(lines[0], expectedHeader + "6") &&
             !StringComparer.Ordinal.Equals(lines[0], expectedHeader + "7") &&
             !StringComparer.Ordinal.Equals(lines[0], expectedHeader + "8") &&
             !StringComparer.Ordinal.Equals(lines[0], expectedHeader + "9"))) {
            throw Invalid(path, $"expected header '{expectedHeader}1', '{expectedHeader}2', '{expectedHeader}3', '{expectedHeader}4', '{expectedHeader}5', '{expectedHeader}6', '{expectedHeader}7', '{expectedHeader}8', or '{expectedHeader}9'");
        }
        int formatVersion = lines[0][lines[0].Length - 1] - '0';

        List<SchemaHistoryRecord> records = new();
        int index = 1;

        while (index < lines.Length) {
            if (!StringComparer.Ordinal.Equals(lines[index], SchemaBegin)) {
                throw Invalid(path, $"line {index + 1} must be '{SchemaBegin}'");
            }

            index++;
            string schemaIdBase64 = ReadPrefixedLine(
                path,
                lines,
                ref index,
                SchemaIdPrefix);
            string schemaId = DecodeSchemaId(path, schemaIdBase64);
            string versionText = ReadPrefixedLine(
                path,
                lines,
                ref index,
                VersionPrefix);
            int version = ParsePositiveCanonicalInt(path, versionText, "version");
            int kind = formatVersion == 1 ? 1 : ParsePositiveCanonicalInt(
                path, ReadPrefixedLine(path, lines, ref index, KindPrefix), "kind");
            if (kind is not (1 or 2)) {
                throw Invalid(path, $"unsupported schema kind {kind}");
            }
            int arity = formatVersion >= 3 ? ParseArity(path, ReadPrefixedLine(path, lines, ref index, "// arity:")) : 0;
            SchemaHistoryKey? baseSchema = null;
            TypePattern? baseType = null;

            if (index < lines.Length && lines[index].StartsWith(BasePrefix, StringComparison.Ordinal)) {
                string baseText = lines[index].Substring(BasePrefix.Length);
                int separator = baseText.IndexOf('|');

                if (separator <= 0 || separator != baseText.LastIndexOf('|')) {
                    throw Invalid(path, $"line {index + 1} has an invalid base entry");
                }

                baseType = formatVersion >= 3 ? ParsePattern(path, baseText.Substring(0, separator), arity, PatternKind.Named, formatVersion) :
                    TypePattern.Named(DecodeSchemaId(path, baseText.Substring(0, separator)));
                baseSchema = new SchemaHistoryKey(
                    baseType.DefinitionId!,
                    ParsePositiveCanonicalInt(path, baseText.Substring(separator + 1), "base version"));
                index++;
            }

            List<SchemaHistoryField> fields = new();
            int previousFieldId = 0;

            while (index < lines.Length &&
                lines[index].StartsWith(FieldPrefix, StringComparison.Ordinal)) {
                string fieldText = lines[index].Substring(FieldPrefix.Length);
                string[] parts = fieldText.Split('|');

                if (parts.Length is < 2 or > 4) {
                    throw Invalid(path, $"line {index + 1} has an invalid field entry");
                }

                int fieldId = ParsePositiveCanonicalInt(
                    path,
                    parts[0],
                    $"field ID on line {index + 1}");
                int typeTag = ParsePositiveCanonicalInt(
                    path,
                    parts[1],
                    $"TypeTag on line {index + 1}");

                if (fieldId <= previousFieldId) {
                    throw Invalid(
                        path,
                        $"field IDs must be unique and sorted; line {index + 1} has {fieldId} after {previousFieldId}");
                }

                if (typeTag < 1 || typeTag > (formatVersion == 1 ? 15 : formatVersion == 2 ? 16 : formatVersion < 6 ? 17 : formatVersion < 8 ? 18 : formatVersion < 9 ? 21 : 24)) {
                    throw Invalid(path, $"line {index + 1} has unsupported TypeTag {typeTag}");
                }

                if (typeTag == 18 ? parts.Length is < 3 or > 4 : parts.Length != (typeTag == 16 ? 4 : typeTag is 15 or 17 ? 3 : 2)) {
                    throw Invalid(path, $"line {index + 1} has an invalid field operand");
                }
                TypePattern pattern = TypePattern.IsBuiltinTag(typeTag) ? TypePattern.Builtin(typeTag) :
                    formatVersion >= 3 ? ParsePattern(path, parts[2], arity, typeTag == 18 ? PatternKind.Nullable : typeTag == 17 ? PatternKind.Parameter : PatternKind.Named, formatVersion, typeTag == 15) :
                    TypePattern.Named(DecodeSchemaId(path, parts[2]));
                string? targetSchemaId = typeTag == 15 ? pattern.DefinitionId : null;
                SchemaHistoryKey? inlineSchema = typeTag == 16 ? new SchemaHistoryKey(
                    pattern.DefinitionId!,
                    ParsePositiveCanonicalInt(path, parts[3], "inline version")) : null;
                if (typeTag == 18) {
                    TypePattern child = pattern.ElementType!;
                    bool hasInline = child.Kind == PatternKind.Named;
                    if (parts.Length != (hasInline ? 4 : 3)) { throw Invalid(path, "a nullable named child requires exactly one inline version; other nullable children have none"); }
                    if (hasInline) {
                        inlineSchema = new SchemaHistoryKey(child.DefinitionId!, ParsePositiveCanonicalInt(path, parts[3], "nullable child inline version"));
                    }
                }
                fields.Add(new SchemaHistoryField(fieldId, typeTag, targetSchemaId, inlineSchema, pattern));
                previousFieldId = fieldId;
                index++;
            }

            if (index >= lines.Length || !StringComparer.Ordinal.Equals(lines[index], SchemaEnd)) {
                throw Invalid(path, $"record for schema '{schemaId}' version {version} has no '{SchemaEnd}'");
            }

            index++;
            if (kind == 2 && baseSchema is not null) {
                throw Invalid(path, "inline schema cannot have a base schema");
            }
            records.Add(new SchemaHistoryRecord(schemaId, schemaIdBase64, version, fields, baseSchema, kind, arity, baseType) {
                SourceFormatVersion = formatVersion,
            });
        }

        if (requireExactlyOneRecord && records.Count != 1) {
            throw Invalid(path, $"history must contain exactly one record block, found {records.Count}");
        }

        return records;
    }

    private static string[] SplitLines(string path, string text) {
        if (text.IndexOf('\r') >= 0) {
            text = text.Replace("\r\n", "\n", StringComparison.Ordinal);

            if (text.IndexOf('\r') >= 0) {
                throw Invalid(path, "contains an unsupported carriage return");
            }
        }

        string[] lines = text.Split('\n');

        if (lines.Length > 0 && lines[^1].Length == 0) {
            Array.Resize(ref lines, lines.Length - 1);
        }

        return lines;
    }

    private static string ReadPrefixedLine(
        string path,
        string[] lines,
        ref int index,
        string prefix) {
        if (index >= lines.Length || !lines[index].StartsWith(prefix, StringComparison.Ordinal)) {
            throw Invalid(path, $"line {index + 1} must start with '{prefix}'");
        }

        string value = lines[index].Substring(prefix.Length);
        index++;
        return value;
    }

    private static string DecodeSchemaId(string path, string value) {
        byte[] bytes;

        try {
            bytes = Convert.FromBase64String(value);
        } catch (FormatException) {
            throw Invalid(path, "schema-id-base64 is not valid Base64");
        }

        if (!StringComparer.Ordinal.Equals(Convert.ToBase64String(bytes), value)) {
            throw Invalid(path, "schema-id-base64 is not canonical Base64");
        }

        string schemaId;

        try {
            schemaId = Utf8NoBom.GetString(bytes);
        } catch (DecoderFallbackException) {
            throw Invalid(path, "schema-id-base64 does not encode valid UTF-8");
        }

        if (string.IsNullOrWhiteSpace(schemaId)) {
            throw Invalid(path, "schema ID must not be empty or whitespace");
        }

        return schemaId;
    }

    private static int ParsePositiveCanonicalInt(
        string path,
        string value,
        string description) {
        if (!int.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int result) ||
            result <= 0 ||
            !StringComparer.Ordinal.Equals(
                result.ToString(CultureInfo.InvariantCulture),
                value)) {
            throw Invalid(path, $"{description} must be a canonical positive Int32");
        }

        return result;
    }

    private static string ReadUtf8(string path, bool allowByteOrderMark) {
        byte[] bytes;

        try {
            bytes = File.ReadAllBytes(path);
        } catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException) {
            throw Invalid(path, exception.Message);
        }

        return DecodeUtf8(path, bytes, allowByteOrderMark);
    }

    private static string DecodeUtf8(
        string path,
        byte[] bytes,
        bool allowByteOrderMark) {
        ReadOnlySpan<byte> content = bytes;

        const int utf8ByteOrderMarkLength = 3;
        bool hasUtf8ByteOrderMark =
            content.Length >= utf8ByteOrderMarkLength &&
            content[0] == 0xef &&
            content[1] == 0xbb &&
            content[2] == 0xbf;

        if (hasUtf8ByteOrderMark) {
            if (!allowByteOrderMark) {
                throw Invalid(path, "UTF-8 byte-order marks are not canonical history content");
            }

            content = content.Slice(utf8ByteOrderMarkLength);
        }

        try {
            return Utf8NoBom.GetString(content);
        } catch (DecoderFallbackException) {
            throw Invalid(path, "is not valid UTF-8");
        }
    }

    private static void AppendSchemaRecord(
        StringBuilder builder,
        SchemaHistoryRecord record,
        int formatVersion) {
        if (formatVersion < 9 && (record.BaseType?.ContainsTemporalScalars == true || record.Fields.Any(field => field.ValuePattern.ContainsTemporalScalars))) {
            throw new SchemaHistoryException("DateOnly, TimeOnly and DateTimeOffset require history format v9.");
        }
        if (formatVersion < 8 && (record.BaseType?.ContainsBclScalars == true || record.Fields.Any(field => field.ValuePattern.ContainsBclScalars))) {
            throw new SchemaHistoryException("Guid, decimal and TimeSpan require history format v8.");
        }
        if (formatVersion < 7 && (record.BaseType?.ContainsDictionary == true || record.Fields.Any(field => field.ValuePattern.ContainsDictionary))) {
            throw new SchemaHistoryException("Dictionary requires history format v7.");
        }
        builder.AppendLine(SchemaBegin);
        builder.Append(SchemaIdPrefix).AppendLine(record.SchemaIdBase64);
        builder.Append(VersionPrefix)
            .AppendLine(record.Version.ToString(CultureInfo.InvariantCulture));

        if (formatVersion >= 2) {
            builder.Append(KindPrefix).AppendLine(record.Kind.ToString(CultureInfo.InvariantCulture));
        }
        if (formatVersion >= 3) {
            builder.Append("// arity:").AppendLine(record.Arity.ToString(CultureInfo.InvariantCulture));
        }
        if (record.BaseSchema is SchemaHistoryKey baseSchema) {
            builder.Append(BasePrefix)
                .Append(formatVersion >= 3 ? record.BaseType!.ToString() : Convert.ToBase64String(Utf8NoBom.GetBytes(baseSchema.SchemaId)))
                .Append('|')
                .AppendLine(baseSchema.Version.ToString(CultureInfo.InvariantCulture));
        }

        foreach (SchemaHistoryField field in record.Fields) {
            builder.Append(FieldPrefix)
                .Append(field.FieldId.ToString(CultureInfo.InvariantCulture))
                .Append('|')
                .Append(field.TypeTag.ToString(CultureInfo.InvariantCulture));
            if (field.TypeTag == 18) {
                if (formatVersion < 6) { throw new SchemaHistoryException("Nullable requires history format v6."); }
                builder.Append('|').Append(field.ValuePattern.ToString());
                if (field.InlineSchema is SchemaHistoryKey childSchema) {
                    builder.Append('|').Append(childSchema.Version.ToString(CultureInfo.InvariantCulture));
                }
            } else if (field.TypeTag is 15 or 17) {
                builder.Append('|').Append(formatVersion >= 3 ? field.ValuePattern.ToString() : Convert.ToBase64String(Utf8NoBom.GetBytes(field.TargetSchemaId!)));
            }
            if (field.TypeTag != 18 && field.InlineSchema is SchemaHistoryKey inlineSchema) {
                builder.Append('|').Append(formatVersion >= 3 ? field.ValuePattern.ToString() : Convert.ToBase64String(Utf8NoBom.GetBytes(inlineSchema.SchemaId)))
                    .Append('|').Append(inlineSchema.Version.ToString(CultureInfo.InvariantCulture));
            }
            builder.AppendLine();
        }

        builder.AppendLine(SchemaEnd);
    }

    private static string ToLowerHex(byte[] bytes) {
        return Convert.ToHexStringLower(bytes);
    }

    private static int ParseArity(string path, string text) {
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int arity) || arity < 0 || arity > 32 ||
            text != arity.ToString(CultureInfo.InvariantCulture)) throw Invalid(path, "arity must be canonical and between 0 and 32");
        return arity;
    }

    private static TypePattern ParsePattern(string path, string text, int arity, PatternKind expectedKind, int formatVersion, bool arrayReference = false) {
        if (!TypePattern.TryParse(text, arity, out TypePattern? pattern, formatVersion >= 4, formatVersion >= 5, formatVersion >= 6, formatVersion >= 7, formatVersion >= 8, formatVersion >= 9) ||
            (pattern!.Kind != expectedKind && !(arrayReference && (pattern.IsArray || pattern.IsList || pattern.IsDictionary)))) {
            throw Invalid(path, "invalid or unbound canonical type pattern");
        }
        return pattern;
    }

    private static SchemaHistoryException Invalid(string path, string reason) {
        return new SchemaHistoryException(
            $"record file '{Path.GetFileName(path)}' is invalid: {reason}");
    }
}

internal sealed class SchemaHistoryRecord {
    public SchemaHistoryRecord(
        string schemaId,
        string schemaIdBase64,
        int version,
        IReadOnlyList<SchemaHistoryField> fields,
        SchemaHistoryKey? baseSchema = null,
        int kind = 1,
        int arity = 0,
        TypePattern? baseType = null) {
        SchemaId = schemaId;
        SchemaIdBase64 = schemaIdBase64;
        Version = version;
        Fields = fields;
        BaseSchema = baseSchema;
        Kind = kind;
        Arity = arity;
        BaseType = baseType ?? (baseSchema is SchemaHistoryKey key ? TypePattern.Named(key.SchemaId) : null);
    }

    public string SchemaId { get; }

    public string SchemaIdBase64 { get; }

    public int Version { get; }

    public IReadOnlyList<SchemaHistoryField> Fields { get; }

    public SchemaHistoryKey? BaseSchema { get; }

    public int Kind { get; }
    public int Arity { get; }
    public TypePattern? BaseType { get; }

    internal int SourceFormatVersion { get; init; } = 9;

    public SchemaHistoryKey Key => new(SchemaId, Version);

    public bool ShapeEquals(SchemaHistoryRecord other) {
        return Kind == other.Kind && Arity == other.Arity && BaseSchema == other.BaseSchema && Equals(BaseType, other.BaseType) && Fields.SequenceEqual(other.Fields);
    }
}

internal readonly record struct SchemaHistoryKey(string SchemaId, int Version);

internal readonly record struct SchemaHistoryField {
    public SchemaHistoryField(int FieldId, int TypeTag, string? TargetSchemaId = null, SchemaHistoryKey? InlineSchema = null, TypePattern? ValuePattern = null) {
        this.FieldId = FieldId; this.TypeTag = TypeTag; this.TargetSchemaId = TargetSchemaId; this.InlineSchema = InlineSchema;
        this.ValuePattern = ValuePattern ?? (InlineSchema is SchemaHistoryKey key ? TypePattern.Named(key.SchemaId) :
            TargetSchemaId is not null ? TypePattern.Named(TargetSchemaId) : TypePattern.Builtin(TypeTag));
    }
    public int FieldId { get; }
    public int TypeTag { get; }
    public string? TargetSchemaId { get; }
    public SchemaHistoryKey? InlineSchema { get; }
    public TypePattern ValuePattern { get; }
}

internal readonly record struct SchemaHistoryResult(string Message);

internal sealed class SchemaHistoryException : Exception {
    public SchemaHistoryException(string message)
        : base(message) {
    }
}
