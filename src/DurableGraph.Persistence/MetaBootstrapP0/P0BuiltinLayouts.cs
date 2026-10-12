using System.Diagnostics.CodeAnalysis;
using Atelia.DurableGraph.Schema;

namespace Atelia.DurableGraph.Persistence.MetaBootstrapP0;

/// <summary>Pure structural layouts selected by typed ABI 3, without a Schema view or CLR model.</summary>
internal static class P0BuiltinLayouts {
    // ABI 3 explicitly selects this codec. A future reader/default must not reinterpret its bytes.
    internal const uint ListCodecVersion = 2;

    internal static bool IsBuiltin(ObjectLayout layout) => layout.Kind == ObjectStateKind.String ||
        TryResolve(layout.Type, out ObjectLayout? reconstructed) && reconstructed.Equals(layout);

    internal static ObjectLayout Resolve(TypeExpr type) => TryResolve(type, out ObjectLayout? layout) ? layout :
        throw new InvalidDataException("A structural object header requires a closed builtin-only List type.");

    private static bool TryResolve(TypeExpr type, [NotNullWhen(true)] out ObjectLayout? layout) {
        if (!type.IsList || !type.IsClosed || ContainsNamed(type)) { layout = null; return false; }
        // Nested container expressions constrain references only. Each referenced object's own
        // header selects its codec; this does not admit Array/Dictionary object heads.
        layout = ObjectLayout.ForList(new ListLayout(Slot(type.ElementType!), ListCodecVersion));
        return true;
    }

    private static bool ContainsNamed(TypeExpr type) => type.Kind == TypeExprKind.Named ||
        type.Arguments.Any(ContainsNamed);

    private static DurableFieldInfo Slot(TypeExpr type) => type.Kind switch {
        TypeExprKind.Builtin => new(1, type.BuiltinTag),
        TypeExprKind.Nullable => DurableFieldInfo.Nullable(1, Slot(type.ElementType!)),
        TypeExprKind.VectorArray or TypeExprKind.Rank2Array or TypeExprKind.Rank3Array or TypeExprKind.Rank4Array or
            TypeExprKind.List or TypeExprKind.Dictionary => DurableFieldInfo.Reference(1, type),
        _ => throw new InvalidDataException("Unsupported constructor in a builtin-only List slot."),
    };
}
