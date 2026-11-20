using System.Buffers;
using System.Reflection;
using Atelia.Binary;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Runtime;

/// <summary>Accepts declared node bindings when the application configures its graph model registry.</summary>
public interface IDeclaredNodeRegistration {
    /// <summary>Registers the current declaration and its accepted historical versions.
    /// Re-registering the same instance is idempotent.</summary>
    void RegisterNode<T>(DeclaredNode<T> node) where T : class;
}

/// <summary>Defines the ordered, persistent members of a class marked with <see cref="DurableNodeAttribute"/>.</summary>
/// <typeparam name="T">A public top-level, sealed, non-generic class with a single durable declaration.</typeparam>
/// <remarks>
/// Accept an older version only if each common slot position keeps its business meaning, kind, reference
/// target and private format. The repository reads the actual historical shape from the source Schema.
/// Trailing historical reference slots are validated before they are dropped. Every historical private
/// slot requires a current validator at the same position and with the same format ID. During migration,
/// added trailing slots receive zero, null, an empty vector or the current private format's encoded default.
/// </remarks>
public sealed class DeclaredNode<T> where T : class {
    private readonly Func<T> _allocate;
    private readonly DeclaredMember<T>[] _members;
    private readonly DeclaredSlot[] _current;
    private readonly DeclaredNodeReplay _currentReplay;
    private readonly HashSet<int> _versions = [];

    /// <summary>Captures a declaration for the current node type and the old versions it can read.</summary>
    /// <param name="allocate">Creates a fresh instance on each call. References are assigned after all reachable objects are allocated.</param>
    /// <param name="members">The current members in persistent order. The declaration copies this array.</param>
    /// <param name="acceptedVersions">Distinct, positive versions strictly below the current version.</param>
    public DeclaredNode(Func<T> allocate, DeclaredMember<T>[] members, params int[] acceptedVersions) {
        ArgumentNullException.ThrowIfNull(allocate);
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(acceptedVersions);
        Type type = typeof(T);
        if (!type.IsPublic || type.IsNested || !type.IsClass || !type.IsSealed || type.IsAbstract || type.IsGenericType ||
            typeof(IDurableObject).IsAssignableFrom(type) || type.IsDefined(typeof(DurableLeafAttribute), false) ||
            type.IsDefined(typeof(DurableTypeAttribute), false) || type.GetCustomAttribute<DurableNodeAttribute>() is not { } declaration) {
            throw new ArgumentException($"Declared node {type} must be a public top-level sealed non-generic class with its own exclusive DurableNode declaration.");
        }
        _allocate = allocate;
        _members = (DeclaredMember<T>[])members.Clone();
        _current = Array.ConvertAll(_members, member =>
            (member ?? throw new ArgumentException("A member declaration is null.", nameof(members))).Slot);
        Schema = DurableSchema.ForDeclaredNode(TypeExpr.Named(declaration.Id), declaration.Version, _current.Select(slot => slot.Shape));
        _currentReplay = new(_current);
        foreach (int version in acceptedVersions) {
            if (version <= 0 || version >= declaration.Version || !_versions.Add(version)) {
                throw new ArgumentException("Each accepted version must be distinct, positive and below the current version.", nameof(acceptedVersions));
            }
        }
    }

    /// <summary>Registers this declaration in the application's model registry.</summary>
    public void Register(IDeclaredNodeRegistration registry) {
        ArgumentNullException.ThrowIfNull(registry);
        registry.RegisterNode(this);
    }

    internal DurableSchema Schema { get; }

    internal void ValidateStored(DurableSchema source) {
        if (!source.IsDeclaredNode || source.Type != Schema.Type) {
            throw new InvalidDataException("The source must have the declared node grammar and nominal family.");
        }
        if (source.Version == Schema.Version) {
            if (!source.Equals(Schema)) { throw new InvalidDataException("The current node declaration disagrees with its stored Schema."); }
            return;
        }
        if (!_versions.Contains(source.Version)) { throw new InvalidDataException($"Node {Schema.Type} does not accept stored v{source.Version}."); }
        for (int index = 0; index < source.DeclaredSlots.Length; index++) {
            DeclaredSlotShape shape = source.DeclaredSlots[index];
            if (index < _current.Length) {
                if (_current[index].Shape != shape) {
                    throw new InvalidDataException($"Node {Schema.Type} slot {index} changed kind, target or private format since v{source.Version}.");
                }
            } else if (shape.Kind == DeclaredSlotKind.Private) {
                throw new InvalidDataException($"Node {Schema.Type} has no validator for retired private slot {index} ({shape.FormatId}).");
            }
        }
    }

    internal DeclaredNodeReplay BindStored(DurableSchema source) {
        ValidateStored(source);
        if (source.Version == Schema.Version) { return _currentReplay; }
        var slots = new DeclaredSlot[source.DeclaredSlots.Length];
        int common = Math.Min(slots.Length, _current.Length);
        Array.Copy(_current, slots, common);
        for (int index = common; index < slots.Length; index++) { slots[index] = new(source.DeclaredSlots[index]); }
        return new(slots);
    }

    internal void VisitReferences(DeclaredCell[] state, IStateReferenceVisitor visitor) =>
        _currentReplay.VisitReferences(state, visitor);

    internal DeclaredCell[] Capture(T value, CaptureContext context) {
        var state = new DeclaredCell[_members.Length];
        for (int index = 0; index < state.Length; index++) { state[index] = _members[index].Capture(value, context); }
        return state;
    }

    internal DeclaredCell[] Normalize(DeclaredCell[] state) {
        if (state.Length == _current.Length) { return state; }
        var current = new DeclaredCell[_current.Length];
        Array.Copy(state, current, Math.Min(state.Length, current.Length));
        for (int index = state.Length; index < current.Length; index++) {
            if (_current[index].Initial is { } initial) { current[index] = new(initial); }
            else if (_current[index].Shape.Kind == DeclaredSlotKind.References) { current[index] = new(Array.Empty<uint>()); }
        }
        return current;
    }

    internal T Allocate() => _allocate() ?? throw new InvalidDataException($"The allocation callback for {typeof(T)} returned null.");
    internal void Hydrate(T value, DeclaredCell[] state, ObjectReadTable context) {
        for (int index = 0; index < _members.Length; index++) { _members[index].Hydrate(value, state[index], context); }
    }

    internal long EstimateBaseSize(DeclaredCell[] state) {
        long size = 0;
        foreach (var cell in state) {
            if (cell.Private is { } bytes) {
                size += BareValueEncoding.MeasureVarUInt32((uint)bytes.Length) + (long)bytes.Length;
                continue;
            }
            if (cell.Items is not { } items) { size += BareValueEncoding.MeasureVarUInt32(cell.Word); continue; }
            size += BareValueEncoding.MeasureVarUInt32((uint)items.Length);
            foreach (uint id in items) { size += BareValueEncoding.MeasureVarUInt32(id); }
        }
        return size;
    }

    internal PreparedBaseBody PrepareBase(DeclaredCell[] state) {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new BareValueWriter(buffer);
        foreach (var cell in state) {
            if (cell.Private is { } bytes) { WritePrivate(ref writer, bytes); }
            else if (cell.Items is { } items) { WriteVector(ref writer, items); }
            else { writer.WriteVarUInt32(cell.Word); }
        }
        return new(buffer.WrittenSpan);
    }

    private static void WritePrivate(ref BareValueWriter writer, byte[] bytes) {
        writer.WriteVarUInt32((uint)bytes.Length);
        writer.WriteRawBytes(bytes);
    }

    private static void WriteVector(ref BareValueWriter writer, uint[] items) {
        writer.WriteVarUInt32((uint)items.Length);
        foreach (uint id in items) { writer.WriteVarUInt32(id); }
    }

    internal PreparedDeltaBody PrepareDelta(DeclaredCell[] previous, DeclaredCell[] current) {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new BareValueWriter(buffer);
        for (int index = 0; index < current.Length; index++) {
            if (current[index].Private is { } bytes) {
                if (!bytes.AsSpan().SequenceEqual(previous[index].Private)) {
                    writer.WriteVarUInt32((uint)index);
                    WritePrivate(ref writer, bytes);
                }
                continue;
            }
            if (current[index].Items is not { } items) {
                if (previous[index].Word != current[index].Word) {
                    writer.WriteVarUInt32((uint)index);
                    writer.WriteVarUInt32(current[index].Word);
                }
                continue;
            }
            uint[] prior = previous[index].Items!;
            if (prior.AsSpan().SequenceEqual(items)) { continue; }
            writer.WriteVarUInt32((uint)index);
            if (prior.Length != items.Length) {
                writer.WriteByte(0);
                WriteVector(ref writer, items);
                continue;
            }
            writer.WriteByte(1);
            for (int item = 0; item < items.Length; item++) {
                if (prior[item] == items[item]) { continue; }
                writer.WriteVarUInt32((uint)item + 1);
                writer.WriteVarUInt32(items[item]);
            }
            writer.WriteVarUInt32(0);
        }
        return new(buffer.WrittenCount != 0, buffer.WrittenSpan);
    }
}
