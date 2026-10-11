using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;

namespace Atelia.DurableGraph.Persistence;

/// <summary>Reconstructs one exact-version DTO using explicitly selected static body functions.</summary>
/// <remarks>
/// Storage validates prior addresses. The Base selects the Schema for every Delta; the caller
/// supplies a matching reader and owns repository correspondence. This does not discover CLR
/// types, upgrade DTOs, resolve reference slots, or restore a domain graph.
/// </remarks>
public static class TypedObjectVersionReader {
    public static TState ReadDurable<TState>(
        ObjectVersionChain chain,
        SchemaStore schemas,
        DurableSchema expectedSchema,
        StateBaseReader<TState> readBase,
        StateDeltaApplier<TState> applyDelta) where TState : unmanaged {
        ArgumentNullException.ThrowIfNull(schemas);
        ArgumentNullException.ThrowIfNull(expectedSchema);
        ArgumentNullException.ThrowIfNull(readBase);
        ArgumentNullException.ThrowIfNull(applyDelta);
        DecodedBaseObjectBody body = DecodeBase(chain, schemas);
        if (body.Kind != ObjectStateKind.Durable) {
            throw new InvalidDataException("A durable reader requires a durable Base type header.");
        }
        MatchSchema(body.Layout.Schema!, expectedSchema);
        return StateBodyDecoder.Read(CreateBodySource(chain, body), readBase, applyDelta);
    }

    /// <summary>
    /// Reads built-in string content without SchemaStore. Use a StringReadTable to retain
    /// reference identity across ObjectIds; each independent nonempty decode creates content.
    /// </summary>
    public static string ReadString(ObjectVersionChain chain) {
        DecodedBaseObjectBody body = DecodeBase(chain);
        return ReadString(chain, body);
    }

    internal static string ReadString(ObjectVersionChain chain, DecodedBaseObjectBody body) {
        if (chain.Records.Count != 1) {
            throw new InvalidDataException("An immutable string object cannot have Delta records.");
        }
        if (body.Kind != ObjectStateKind.String) {
            throw new InvalidDataException("A string reader requires a string Base type header.");
        }
        BareValueReader reader = new(body.Body);
        string value = reader.ReadString();
        reader.EnsureFullyConsumed();
        return value;
    }

    internal static DecodedBaseObjectBody DecodeBase(ObjectVersionChain chain, SchemaStore? schemas = null) {
        ValidateShape(chain);
        return BaseObjectBodyCodec.Decode(chain.Records[0].Record.Body, schemas);
    }

    internal static void MatchSchema(DurableSchema storedSchema, DurableSchema expectedSchema) {
        if (storedSchema.Kind != SchemaKind.ReferenceObject || expectedSchema.Kind != SchemaKind.ReferenceObject ||
            !storedSchema.Equals(expectedSchema)) {
            throw new InvalidDataException("The stored exact Schema definition does not match the selected body reader.");
        }
    }

    internal static IStateBodySource CreateBodySource(ObjectVersionChain chain, DecodedBaseObjectBody body) =>
        new ChainBodySource(chain, body);

    // Borrow the owned chain for this synchronous read; Delta bodies do not need another copy.
    private sealed class ChainBodySource(ObjectVersionChain chain, DecodedBaseObjectBody body) : IStateBodySource {
        public int Count => chain.Records.Count;
        public ReadOnlySpan<byte> GetBody(int index) => index == 0 ? body.Body : chain.Records[index].Record.Body;
    }

    private static void ValidateShape(ObjectVersionChain chain) {
        ArgumentNullException.ThrowIfNull(chain);
        if (chain.Records.Count == 0) {
            throw new InvalidDataException("An object version chain requires a Base.");
        }
        for (int index = 0; index < chain.Records.Count; index++) {
            ObjectVersionRecord record = chain.Records[index].Record;
            if (record.ObjectId != chain.ObjectId ||
                record.Kind != (index == 0 ? ObjectVersionKind.Base : ObjectVersionKind.Delta)) {
                throw new InvalidDataException("An object version chain must contain one Base followed by this object's Deltas.");
            }
        }
    }
}
