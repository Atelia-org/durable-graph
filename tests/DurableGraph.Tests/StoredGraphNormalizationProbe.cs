using System.Collections.Immutable;
using System.Collections.ObjectModel;

namespace Atelia.DurableGraph.Tests;

// Legacy test-only normalization witness. Its ProbeSnapshot vocabulary is not the current
// stored/current DTO view or the public Repository product contract.

internal enum ProbeStoredFieldKind {
    Invalid = 0,
    Int32 = 1,
    Reference = 2,
}

internal readonly record struct ProbeStoredFieldInfo(
    int FieldId,
    ProbeStoredFieldKind Kind);

internal sealed class ProbeStoredSchema : IEquatable<ProbeStoredSchema> {
    internal ProbeStoredSchema(
        string schemaId,
        int version,
        params ProbeStoredFieldInfo[] fields) {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaId);
        if (version <= 0) {
            throw new ArgumentOutOfRangeException(
                nameof(version),
                version,
                "Stored probe schema versions must be positive.");
        }

        ArgumentNullException.ThrowIfNull(fields);

        ProbeStoredFieldInfo[] canonicalFields =
            (ProbeStoredFieldInfo[])fields.Clone();
        Array.Sort(
            canonicalFields,
            static (left, right) => left.FieldId.CompareTo(right.FieldId));

        for (int index = 0; index < canonicalFields.Length; index++) {
            ProbeStoredFieldInfo field = canonicalFields[index];
            if (field.FieldId <= 0 ||
                field.Kind is ProbeStoredFieldKind.Invalid ||
                !Enum.IsDefined(field.Kind)) {
                throw new ArgumentException(
                    "Every stored probe field must have a positive identity and known kind.",
                    nameof(fields));
            }

            if (index > 0 && canonicalFields[index - 1].FieldId == field.FieldId) {
                throw new ArgumentException(
                    $"Stored probe field identity {field.FieldId} is duplicated.",
                    nameof(fields));
            }
        }

        SchemaId = schemaId;
        Version = version;
        Fields = ImmutableArray.CreateRange(canonicalFields);
    }

    internal string SchemaId { get; }

    internal int Version { get; }

    internal ImmutableArray<ProbeStoredFieldInfo> Fields { get; }

    public bool Equals(ProbeStoredSchema? other) {
        return other is not null &&
            StringComparer.Ordinal.Equals(SchemaId, other.SchemaId) &&
            Version == other.Version &&
            Fields.AsSpan().SequenceEqual(other.Fields.AsSpan());
    }

    public override bool Equals(object? obj) {
        return obj is ProbeStoredSchema other && Equals(other);
    }

    public override int GetHashCode() {
        HashCode hashCode = new();
        hashCode.Add(SchemaId, StringComparer.Ordinal);
        hashCode.Add(Version);

        foreach (ProbeStoredFieldInfo field in Fields) {
            hashCode.Add(field);
        }

        return hashCode.ToHashCode();
    }

    public static bool operator ==(
        ProbeStoredSchema? left,
        ProbeStoredSchema? right) {
        return Equals(left, right);
    }

    public static bool operator !=(
        ProbeStoredSchema? left,
        ProbeStoredSchema? right) {
        return !Equals(left, right);
    }
}

internal readonly record struct ProbeSnapshotV1(
    int Value,
    ProbeId? NextId);

internal delegate void ProbeUpgradeV1ToV2(
    in ProbeSnapshotV1 oldValue,
    out ProbeSnapshot newValue);

internal abstract class StoredProbeRecord {
    private readonly Action? _decodeHook;

    protected StoredProbeRecord(
        ProbeStoredSchema schema,
        Action? decodeHook) {
        ArgumentNullException.ThrowIfNull(schema);

        Schema = schema;
        _decodeHook = decodeHook;
    }

    internal ProbeStoredSchema Schema { get; }

    protected void OnDecode() {
        _decodeHook?.Invoke();
    }
}

internal sealed class StoredProbeRecordV1 : StoredProbeRecord {
    private readonly ProbeSnapshotV1 _payload;

    internal StoredProbeRecordV1(
        ProbeStoredSchema schema,
        ProbeSnapshotV1 payload,
        Action? decodeHook = null)
        : base(schema, decodeHook) {
        _payload = payload;
    }

    internal ProbeSnapshotV1 Decode() {
        OnDecode();
        return _payload;
    }
}

internal sealed class StoredProbeRecordV2 : StoredProbeRecord {
    private readonly ProbeSnapshot _payload;

    internal StoredProbeRecordV2(
        ProbeStoredSchema schema,
        ProbeSnapshot payload,
        Action? decodeHook = null)
        : base(schema, decodeHook) {
        _payload = payload;
    }

    internal ProbeSnapshot Decode() {
        OnDecode();
        return _payload;
    }
}

internal readonly record struct StoredGraphRecordEntry(
    ProbeId Id,
    StoredProbeRecord Record);

internal sealed class StoredGraphImage {
    internal StoredGraphImage(
        ProbeId rootId,
        IEnumerable<StoredGraphRecordEntry> records) {
        ArgumentNullException.ThrowIfNull(records);
        if (rootId.Value <= 0) {
            throw new InvalidStoredGraphImageException(
                "The stored graph root identity must be positive.");
        }

        Dictionary<ProbeId, StoredProbeRecord> copy = [];
        foreach (StoredGraphRecordEntry entry in records) {
            if (entry.Id.Value <= 0) {
                throw new InvalidStoredGraphImageException(
                    "Every stored graph record identity must be positive.");
            }

            if (entry.Record is null) {
                throw new InvalidStoredGraphImageException(
                    $"Stored graph record {entry.Id} is null.");
            }

            if (!copy.TryAdd(entry.Id, entry.Record)) {
                throw new InvalidStoredGraphImageException(
                    $"Stored graph record identity {entry.Id} is duplicated.");
            }
        }

        if (!copy.ContainsKey(rootId)) {
            throw new InvalidStoredGraphImageException(
                $"The stored graph does not contain root identity {rootId}.");
        }

        RootId = rootId;
        Records = new ReadOnlyDictionary<ProbeId, StoredProbeRecord>(copy);
    }

    internal ProbeId RootId { get; }

    internal IReadOnlyDictionary<ProbeId, StoredProbeRecord> Records { get; }
}

internal static class StoredGraphNormalizationProbe {
    internal const string SchemaId = "tests.graph-normalization-probe";

    internal static ProbeStoredSchema SchemaV1 { get; } = new(
        SchemaId,
        version: 1,
        new ProbeStoredFieldInfo(1, ProbeStoredFieldKind.Int32),
        new ProbeStoredFieldInfo(2, ProbeStoredFieldKind.Reference));

    internal static ProbeStoredSchema SchemaV2 { get; } = new(
        SchemaId,
        version: 2,
        new ProbeStoredFieldInfo(1, ProbeStoredFieldKind.Int32),
        new ProbeStoredFieldInfo(2, ProbeStoredFieldKind.Reference),
        new ProbeStoredFieldInfo(3, ProbeStoredFieldKind.Reference));

    internal static NormalizedBaselineGraph LoadNormalized(
        StoredGraphImage image,
        ProbeUpgradeV1ToV2? upgradeHandler) {
        ArgumentNullException.ThrowIfNull(image);

        KeyValuePair<ProbeId, StoredProbeRecord>[] orderedRecords = image.Records
            .OrderBy(static pair => pair.Key.Value)
            .ToArray();
        bool requiresUpgradeHandler = false;

        foreach ((ProbeId id, StoredProbeRecord record) in orderedRecords) {
            requiresUpgradeHandler |= PreflightRecord(id, record);
        }

        if (requiresUpgradeHandler && upgradeHandler is null) {
            throw new InvalidStoredGraphImageException(
                "The stored graph contains version 1 records, but no V1-to-V2 " +
                "upgrade handler was supplied.");
        }

        HashSet<ProbeId> sourceRecordIds = image.Records.Keys.ToHashSet();
        Dictionary<ProbeId, BaselineEntry> entries = [];

        foreach ((ProbeId id, StoredProbeRecord record) in orderedRecords) {
            ProbeSnapshot currentSnapshot;
            bool requiresRewrite;

            switch (record) {
                case StoredProbeRecordV1 version1Record:
                    ProbeSnapshotV1 version1Snapshot = Decode(id, version1Record);
                    currentSnapshot = Upgrade(
                        version1Snapshot,
                        upgradeHandler!);
                    requiresRewrite = true;
                    break;

                case StoredProbeRecordV2 version2Record:
                    currentSnapshot = Decode(id, version2Record);
                    requiresRewrite = false;
                    break;

                default:
                    throw new InvalidStoredGraphImageException(
                        $"Stored graph record {id} has an unsupported payload variant.");
            }

            ValidateReference(
                sourceRecordIds,
                id,
                fieldId: 2,
                currentSnapshot.NextId);
            ValidateReference(
                sourceRecordIds,
                id,
                fieldId: 3,
                currentSnapshot.AliasId);
            entries.Add(id, new BaselineEntry(currentSnapshot, requiresRewrite));
        }

        try {
            return new NormalizedBaselineGraph(image.RootId, entries);
        } catch (InvalidProbeGraphException exception) {
            throw new InvalidStoredGraphImageException(
                "The normalized stored graph is invalid.",
                exception);
        }
    }

    private static bool PreflightRecord(
        ProbeId id,
        StoredProbeRecord record) {
        ProbeStoredSchema storedSchema = record.Schema;
        if (!StringComparer.Ordinal.Equals(storedSchema.SchemaId, SchemaId)) {
            throw new InvalidStoredGraphImageException(
                $"Stored graph record {id} uses schema identity " +
                $"'{storedSchema.SchemaId}', expected '{SchemaId}'.");
        }

        switch (storedSchema.Version) {
            case 1:
                ValidateExactSchema(id, storedSchema, SchemaV1);
                if (record is not StoredProbeRecordV1) {
                    throw new InvalidStoredGraphImageException(
                        $"Stored graph record {id} declares schema version 1 " +
                        "but does not contain a V1 payload.");
                }

                return true;

            case 2:
                ValidateExactSchema(id, storedSchema, SchemaV2);
                if (record is not StoredProbeRecordV2) {
                    throw new InvalidStoredGraphImageException(
                        $"Stored graph record {id} declares schema version 2 " +
                        "but does not contain a V2 payload.");
                }

                return false;

            default:
                throw new UnsupportedSchemaVersionException(
                    storedSchema.SchemaId,
                    storedSchema.Version,
                    SchemaV2.Version);
        }
    }

    private static void ValidateExactSchema(
        ProbeId id,
        ProbeStoredSchema storedSchema,
        ProbeStoredSchema expectedSchema) {
        if (storedSchema != expectedSchema) {
            throw new InvalidStoredGraphImageException(
                $"Stored graph record {id} does not match the exact shape of " +
                $"schema '{SchemaId}' version {expectedSchema.Version}.");
        }
    }

    private static ProbeSnapshotV1 Decode(
        ProbeId id,
        StoredProbeRecordV1 record) {
        try {
            return record.Decode();
        } catch (Exception exception) {
            throw new InvalidStoredGraphImageException(
                $"Decoding stored graph record {id} as version 1 failed.",
                exception);
        }
    }

    private static ProbeSnapshot Decode(
        ProbeId id,
        StoredProbeRecordV2 record) {
        try {
            return record.Decode();
        } catch (Exception exception) {
            throw new InvalidStoredGraphImageException(
                $"Decoding stored graph record {id} as version 2 failed.",
                exception);
        }
    }

    private static ProbeSnapshot Upgrade(
        ProbeSnapshotV1 version1Snapshot,
        ProbeUpgradeV1ToV2 upgradeHandler) {
        try {
            upgradeHandler(in version1Snapshot, out ProbeSnapshot currentSnapshot);
            return currentSnapshot;
        } catch (Exception exception) {
            throw new DurableUpgradeException(
                SchemaId,
                fromVersion: 1,
                toVersion: 2,
                innerException: exception);
        }
    }

    private static void ValidateReference(
        IReadOnlySet<ProbeId> sourceRecordIds,
        ProbeId ownerId,
        int fieldId,
        ProbeId? referenceId) {
        if (referenceId is not ProbeId targetId) {
            return;
        }

        if (targetId.Value <= 0) {
            throw new InvalidStoredGraphImageException(
                $"Stored graph record {ownerId} has invalid reference {targetId} " +
                $"in field {fieldId}.");
        }

        if (!sourceRecordIds.Contains(targetId)) {
            throw new InvalidStoredGraphImageException(
                $"Stored graph record {ownerId} references identity {targetId} " +
                $"outside SourceRecordIds in field {fieldId}.");
        }
    }
}

internal sealed class InvalidStoredGraphImageException : InvalidOperationException {
    internal InvalidStoredGraphImageException(string message)
        : base(message) {
    }

    internal InvalidStoredGraphImageException(
        string message,
        Exception innerException)
        : base(message, innerException) {
    }
}
