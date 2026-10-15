namespace Atelia.DurableGraph;

/// <summary>
/// Marks a field as intentionally excluded from durable state.
/// Use field: for real property backing storage or ordinary field-like event storage.
/// The target must exist; computed properties and abstract, extern, partial, or custom
/// events do not supply it. Records retain their separate event-storage restrictions.
/// This does not change record equality, and restoration leaves excluded storage at
/// its default value without invoking accessors.
/// </summary>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class TransientAttribute : Attribute {
}
