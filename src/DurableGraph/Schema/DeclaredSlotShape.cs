using System.Text;

namespace Atelia.DurableGraph.Schema;

internal enum DeclaredSlotKind : byte {
    Int32 = 1,
    Reference = 2,
    References = 3,
    Private = 4,
}

// Persistent operands for one position in a declared node body. Live accessors,
// private format validators and migration policy belong to the application.
internal sealed record DeclaredSlotShape {
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal DeclaredSlotShape(DeclaredSlotKind kind, TypeExpr? target = null, string? formatId = null) {
        if (!Enum.IsDefined(kind)) { throw new ArgumentOutOfRangeException(nameof(kind)); }
        if (kind is DeclaredSlotKind.Reference or DeclaredSlotKind.References) {
            ArgumentNullException.ThrowIfNull(target);
            if (!target.IsClosed || !(target.IsObjectReferenceTarget || target.Kind == TypeExprKind.Builtin && target.BuiltinTag == TypeTag.String)) {
                throw new ArgumentException("A declared reference slot requires a closed object type or string.", nameof(target));
            }
        } else if (target is not null) {
            throw new ArgumentException("Only reference slots carry a target type.", nameof(target));
        }
        if (kind == DeclaredSlotKind.Private) {
            ArgumentException.ThrowIfNullOrWhiteSpace(formatId);
            try {
                StrictUtf8.GetByteCount(formatId);
            } catch (EncoderFallbackException error) {
                throw new ArgumentException("A private format identifier must be valid Unicode.", nameof(formatId), error);
            }
        } else if (formatId is not null) {
            throw new ArgumentException("Only private slots carry a format identifier.", nameof(formatId));
        }
        Kind = kind;
        Target = target;
        FormatId = formatId;
    }

    internal DeclaredSlotKind Kind { get; }
    internal TypeExpr? Target { get; }
    internal string? FormatId { get; }
}
