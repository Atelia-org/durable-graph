using Atelia.Binary;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Runtime;

/// <summary>
/// Immutable string bindings for one loading view. Empty strings share string.Empty;
/// nonempty strings with different IDs retain distinct instances. This is not a graph loader.
/// </summary>
public sealed class StringReadTable {
    private readonly Dictionary<ObjectId, string> _strings;
    internal IEnumerable<KeyValuePair<ObjectId, string>> Entries => _strings;

    private StringReadTable(Dictionary<ObjectId, string> strings) {
        _strings = strings;
    }

    /// <summary>
    /// Decodes complete non-null string bodies. Keep inputs stable during this call;
    /// the returned table retains neither their buffers nor the input enumeration.
    /// IDs must be nonzero and unique. A failure returns no partially decoded table.
    /// The caller separately validates the full object directory and its exact schema bindings.
    /// </summary>
    public static StringReadTable Decode(IEnumerable<(ObjectId Id, ReadOnlyMemory<byte> Body)> records) {
        ArgumentNullException.ThrowIfNull(records);
        Dictionary<ObjectId, string> strings = [];
        HashSet<string> nonemptyInstances = new(ReferenceEqualityComparer.Instance);
        foreach ((ObjectId id, ReadOnlyMemory<byte> body) in records) {
            if (id.IsNull || strings.ContainsKey(id)) {
                throw new InvalidDataException("String object IDs must be nonzero and unique.");
            }

            BareValueReader reader = new(body.Span);
            string value = reader.ReadString();
            reader.EnsureFullyConsumed();
            AddDecoded(strings, nonemptyInstances, id, value);
        }
        return new(strings);
    }

    /// <summary>Copies completed decoded bindings without decoding or replacing nonempty instances.</summary>
    internal static StringReadTable FromDecoded(IEnumerable<(ObjectId Id, string Value)> records) {
        ArgumentNullException.ThrowIfNull(records);
        Dictionary<ObjectId, string> strings = [];
        HashSet<string> nonemptyInstances = new(ReferenceEqualityComparer.Instance);
        foreach ((ObjectId id, string value) in records) {
            AddDecoded(strings, nonemptyInstances, id, value);
        }
        return new(strings);
    }

    private static void AddDecoded(
        Dictionary<ObjectId, string> strings,
        HashSet<string> nonemptyInstances,
        ObjectId id,
        string value) {
        if (id.IsNull || strings.ContainsKey(id)) {
            throw new InvalidDataException("String object IDs must be nonzero and unique.");
        }
        if (value is null) {
            throw new InvalidDataException("A string object must contain a non-null string.");
        }
        if (value.Length == 0) {
            value = string.Empty;
        } else if (!nonemptyInstances.Add(value)) {
            throw new InvalidDataException("Different nonempty string IDs must have distinct instances.");
        }
        strings.Add(id, value);
    }

    /// <summary>Resolves a string ID in this view. Zero is null; any other absent ID is invalid.</summary>
    public string? ResolveString(ObjectId id) {
        if (id.IsNull) {
            return null;
        }
        return _strings.TryGetValue(id, out string? value)
            ? value
            : throw new InvalidDataException($"Object ID {id} is not a string in this loading view.");
    }
}
