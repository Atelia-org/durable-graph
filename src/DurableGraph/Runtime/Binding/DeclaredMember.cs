using Atelia.DurableGraph.Schema;

namespace Atelia.DurableGraph.Runtime;

// Accessors, validators and defaults belong to the current registration. Only Shape is persisted.
internal sealed record DeclaredSlot(DeclaredSlotShape Shape, Action<ReadOnlySpan<byte>>? Validate = null, byte[]? Initial = null);

// A repeated cell owns the entire frozen ID vector. Copies of this value share immutable arrays.
internal readonly record struct DeclaredCell(uint Word, uint[]? Items = null, byte[]? Private = null) {
    internal DeclaredCell(uint[] items) : this(0, items) { }
    internal DeclaredCell(byte[] data) : this(0, null, data) { }
}

/// <summary>Associates a stored slot with typed accessors to a live domain member.</summary>
public abstract class DeclaredMember<T> where T : class {
    private DeclaredMember(DeclaredSlot slot) => Slot = slot;
    private DeclaredMember(DeclaredSlotKind kind, TypeExpr? target = null) =>
        Slot = new DeclaredSlot(new DeclaredSlotShape(kind, target));
    internal DeclaredSlot Slot { get; }
    internal abstract DeclaredCell Capture(T value, CaptureContext context);
    internal abstract void Hydrate(T value, DeclaredCell cell, ObjectReadTable context);

    // The stored target expression has already constrained the actual layout. A wider
    // CLR accessor can accept that object, including a covariant array such as string[].
    private static TTarget? ResolveTarget<TTarget>(ObjectReadTable objects, uint id) where TTarget : class {
        object? instance = objects.ResolveObject<object>(new(id));
        if (instance is null) { return null; }
        return instance as TTarget ??
            throw new InvalidDataException($"Node target {id} ({instance.GetType()}) cannot be assigned to a {typeof(TTarget)} member.");
    }

    /// <summary>Persists a signed 32-bit integer in the declared position.</summary>
    public static DeclaredMember<T> Int32(Func<T, int> get, Action<T, int> set) =>
        new IntMember(get ?? throw new ArgumentNullException(nameof(get)), set ?? throw new ArgumentNullException(nameof(set)));

    /// <summary>Persists a value using the supplied reference-free private format. Its entire encoded value is replaced when it changes.</summary>
    public static DeclaredMember<T> Private<TValue>(DeclaredPrivateFormat<TValue> format, Func<T, TValue> get, Action<T, TValue> set) =>
        new PrivateMember<TValue>(format ?? throw new ArgumentNullException(nameof(format)),
            get ?? throw new ArgumentNullException(nameof(get)), set ?? throw new ArgumentNullException(nameof(set)));

    /// <summary>Persists one optional graph reference whose target must satisfy the supplied closed type expression.</summary>
    /// <remarks>The caller pairs the persistent type expression with a CLR type that can represent its targets.
    /// Capture checks every non-null target against the expression, including already captured aliases.</remarks>
    public static DeclaredMember<T> Reference<TTarget>(TypeExpr target, Func<T, TTarget?> get, Action<T, TTarget?> set)
        where TTarget : class => new ReferenceMember<TTarget>(target,
            get ?? throw new ArgumentNullException(nameof(get)), set ?? throw new ArgumentNullException(nameof(set)));

    /// <summary>Stores a non-null vector of ordered references inline. Its elements may be null or refer to the same target.</summary>
    /// <remarks>The caller pairs the persistent element type with a compatible CLR element type. Capture checks every
    /// non-null element against the expression. Hydration creates a fresh array and restores each target's graph
    /// identity. Use a single array reference when the array itself must retain identity.</remarks>
    public static DeclaredMember<T> References<TTarget>(TypeExpr target, Func<T, TTarget?[]> get, Action<T, TTarget?[]> set)
        where TTarget : class => new RepeatedMember<TTarget>(target,
            get ?? throw new ArgumentNullException(nameof(get)), set ?? throw new ArgumentNullException(nameof(set)));

    private sealed class IntMember(Func<T, int> get, Action<T, int> set) : DeclaredMember<T>(DeclaredSlotKind.Int32) {
        internal override DeclaredCell Capture(T value, CaptureContext context) {
            int number = get(value);
            return new(unchecked((uint)((number << 1) ^ (number >> 31))));
        }
        internal override void Hydrate(T value, DeclaredCell cell, ObjectReadTable context) =>
            set(value, (int)(cell.Word >> 1) ^ -(int)(cell.Word & 1));
    }

    private sealed class PrivateMember<TValue>(DeclaredPrivateFormat<TValue> format, Func<T, TValue> get, Action<T, TValue> set)
        : DeclaredMember<T>(format.Slot) {
        internal override DeclaredCell Capture(T value, CaptureContext context) => new(format.Encode(get(value)));
        internal override void Hydrate(T value, DeclaredCell cell, ObjectReadTable context) => set(value, format.Decode(cell.Private!));
    }

    private sealed class ReferenceMember<TTarget>(TypeExpr target, Func<T, TTarget?> get, Action<T, TTarget?> set)
        : DeclaredMember<T>(DeclaredSlotKind.Reference, target) where TTarget : class {
        internal override DeclaredCell Capture(T value, CaptureContext context) => new(context.CaptureObject(get(value), Slot.Shape.Target!).Value);
        internal override void Hydrate(T value, DeclaredCell cell, ObjectReadTable context) => set(value, ResolveTarget<TTarget>(context, cell.Word));
    }

    private sealed class RepeatedMember<TTarget>(TypeExpr target, Func<T, TTarget?[]> get, Action<T, TTarget?[]> set)
        : DeclaredMember<T>(DeclaredSlotKind.References, target) where TTarget : class {
        internal override DeclaredCell Capture(T value, CaptureContext context) {
            TTarget?[] items = get(value) ?? throw new InvalidOperationException("A declared repeated slot requires a non-null vector.");
            if (items.Length == 0) { return new(Array.Empty<uint>()); }
            var ids = new uint[items.Length];
            for (int index = 0; index < ids.Length; index++) {
                if (items[index] is { } item) { ids[index] = context.CaptureObject(item, Slot.Shape.Target!).Value; }
            }
            return new(ids);
        }

        internal override void Hydrate(T value, DeclaredCell cell, ObjectReadTable context) {
            uint[] ids = cell.Items!;
            var items = new TTarget?[ids.Length];
            for (int index = 0; index < ids.Length; index++) {
                if (ids[index] != 0) { items[index] = ResolveTarget<TTarget>(context, ids[index]); }
            }
            set(value, items);
        }
    }
}
