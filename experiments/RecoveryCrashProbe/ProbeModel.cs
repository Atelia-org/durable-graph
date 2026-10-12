using System.Buffers;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;

namespace Atelia.RecoveryCrashProbe;

// Handwritten bindings, adapted from SharedReadModel; no test framework or generator.
internal static class ProbeModel {
    internal abstract class Node : IDurableObject {
        internal Node? Next;
        internal Node? Alias;
        internal string? Text;
        internal byte Value;
    }
    internal sealed class OldNode : Node { }
    internal sealed class NewNode : Node { }
    private readonly record struct State(ObjectId Next, ObjectId Alias, ObjectId Text, byte Value);

    internal static StateModelRegistry Models() {
        var registry = new StateModelRegistry();
        Register<OldNode>(registry, "RecoveryProbeOld");
        Register<NewNode>(registry, "RecoveryProbeNew");
        return registry;
    }

    private static void Register<T>(StateModelRegistry registry, string id) where T : Node, new() {
        var schema = new DurableSchema(id, 1,
            new DurableFieldInfo(1, TypeTag.ObjectReference, id),
            new DurableFieldInfo(2, TypeTag.ObjectReference, id),
            new DurableFieldInfo(3, TypeTag.String), new DurableFieldInfo(4, TypeTag.Byte));
        void Visit(in State state, IStateReferenceVisitor visitor) {
            visitor.VisitDurable(state.Next, id);
            visitor.VisitDurable(state.Alias, id);
            visitor.VisitString(state.Text);
        }
        State Read(ref BinaryPayloadReader reader) => new(new(reader.ReadUInt32()), new(reader.ReadUInt32()),
            new(reader.ReadUInt32()), reader.ReadByte());
        PreparedBaseBody Base(in State state) {
            var bytes = new ArrayBufferWriter<byte>();
            var writer = new BinaryPayloadWriter(bytes);
            writer.WriteUInt32(state.Next.Value);
            writer.WriteUInt32(state.Alias.Value);
            writer.WriteUInt32(state.Text.Value);
            writer.WriteByte(state.Value);
            return new(bytes.WrittenSpan);
        }
        var preparation = new CapturedStatePreparation<State>(schema, Base,
            (in State prior, in State next) => new(prior != next, Base(next).Body),
            (in State left, in State right) => left == right);
        var reader = new StateReaderBinding<State>(schema, Read,
            (ref BinaryPayloadReader input, in State prior) => Read(ref input), Visit);
        registry.Register(new StateModelBinding<T, State>(preparation, [reader],
            row => row.GetState<State>(), () => new T(),
            (T domain, in State state, ObjectReadTable objects) => {
                domain.Next = objects.ResolveDurable<T>(state.Next);
                domain.Alias = objects.ResolveDurable<T>(state.Alias);
                domain.Text = objects.ResolveString(state.Text);
                domain.Value = state.Value;
            }, (domain, context) => new(context.CaptureDurable((T?)domain.Next, id),
                context.CaptureDurable((T?)domain.Alias, id), context.CaptureString(domain.Text), domain.Value), Visit));
    }

    internal static T Graph<T>(byte value) where T : Node, new() {
        var root = new T { Value = value, Text = "root:" + value };
        var child = new T { Value = (byte)(value + 1), Text = "child:" + value, Next = root };
        root.Next = root.Alias = child;
        child.Alias = child;
        return root;
    }

    internal static object Check(IDurableObject? graph) {
        if (graph is null) { return new { absent = true }; }
        var node = (Node)graph;
        if (node.Next is null || node.Next.Value != node.Value + 1 ||
            !ReferenceEquals(node.Next, node.Alias) || !ReferenceEquals(node.Next.Next, node) ||
            !ReferenceEquals(node.Next.Alias, node.Next) || node.Text != "root:" + node.Value ||
            node.Next.Text != "child:" + node.Value || node.GetType() != node.Next.GetType()) {
            throw new InvalidDataException("Graph content, type, aliases or cycle mixed across commits.");
        }
        return new { type = node.GetType().Name, value = node.Value, text = node.Text,
            childValue = node.Next.Value, aliases = true, cycle = true };
    }
}
