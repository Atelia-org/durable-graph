using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using System.Buffers;

namespace Atelia.DurableGraph.Persistence.Tests;

internal static class SharedReadModel {
    internal static readonly TypeExpr LinksType = TypeExpr.List(TypeExpr.Named("SharedReadNode"));
    internal static readonly DurableSchema Schema = new("SharedReadNode", 1,
        new DurableFieldInfo(1, TypeTag.ObjectReference, "SharedReadNode"),
        new DurableFieldInfo(2, TypeTag.ObjectReference, "SharedReadNode"),
        new DurableFieldInfo(3, TypeTag.String), new DurableFieldInfo(4, TypeTag.Byte),
        DurableFieldInfo.Reference(5, LinksType));

    internal sealed class Node : IDurableObject {
        internal Node? Next;
        internal Node? Alias;
        internal string? Text;
        internal byte Value;
        internal List<Node>? Links;
    }

    internal readonly record struct State(ObjectId Next, ObjectId Alias, ObjectId Text, byte Value, ObjectId Links = default) {
        internal State(uint next, uint alias, uint text, byte value)
            : this(new ObjectId(next), new ObjectId(alias), new ObjectId(text), value) { }
    }

    internal static StateModelRegistry Models(int currentVersion = 1, Func<ObjectStateRecord, State>? normalize = null,
        Func<Node>? allocate = null, Action<Node>? hydrate = null,
        StateBasePreparer<State>? prepareBase = null, StateDeltaPreparer<State>? prepareDelta = null) {
        DurableSchema current = currentVersion == 1 ? Schema : new(Schema.SchemaId, currentVersion, Schema.Fields.ToArray());
        StatePreparationBinding<State> preparation = new(current, prepareBase ?? Base,
            prepareDelta ?? (static (in State prior, in State next) => new(prior != next, Base(next).Body)));
        StateReaderBinding Reader(DurableSchema schema) => new StateReaderBinding<State>(schema, Read,
            static (ref BareValueReader input, in State prior) => Read(ref input), Visit);
        StateModelBinding<Node, State> model = new(preparation,
            currentVersion == 1 ? [Reader(Schema)] : [Reader(Schema), Reader(current)],
            normalize ?? (static row => row.GetState<State>()), allocate ?? (static () => new()),
            (Node domain, in State state, ObjectReadTable objects) => {
                domain.Next = objects.ResolveDurable<Node>(state.Next);
                domain.Alias = objects.ResolveDurable<Node>(state.Alias);
                domain.Text = objects.ResolveString(state.Text);
                domain.Value = state.Value;
                domain.Links = objects.ResolveObject<List<Node>>(state.Links);
                hydrate?.Invoke(domain);
            }, static (domain, context) => new(context.CaptureDurable(domain.Next, Schema.SchemaId),
                context.CaptureDurable(domain.Alias, Schema.SchemaId), context.CaptureString(domain.Text), domain.Value,
                context.CaptureObject(domain.Links, LinksType)), Visit);
        StateModelRegistry registry = new();
        registry.Register(model);
        return registry;
    }

    private static void Visit(in State state, IStateReferenceVisitor visitor) {
        visitor.VisitDurable(state.Next, Schema.SchemaId);
        visitor.VisitDurable(state.Alias, Schema.SchemaId);
        visitor.VisitString(state.Text);
        visitor.VisitObject(state.Links, LinksType);
    }

    private static State Read(ref BareValueReader reader) => new(new(reader.ReadVarUInt32()), new(reader.ReadVarUInt32()),
        new(reader.ReadVarUInt32()), reader.ReadByte(), new(reader.ReadVarUInt32()));

    internal static PreparedBaseBody Base(in State state) {
        ArrayBufferWriter<byte> bytes = new();
        BareValueWriter writer = new(bytes);
        writer.WriteVarUInt32(state.Next.Value);
        writer.WriteVarUInt32(state.Alias.Value);
        writer.WriteVarUInt32(state.Text.Value);
        writer.WriteByte(state.Value);
        writer.WriteVarUInt32(state.Links.Value);
        return new(bytes.WrittenSpan);
    }

    internal static void DeleteFixture(string path, string prefix) {
        string resolved = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(resolved), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(resolved).StartsWith(prefix, StringComparison.Ordinal)) {
            throw new InvalidOperationException("Refusing to delete outside this test fixture.");
        }
        if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
    }
}
