using System.Reflection;
using System.Runtime.ExceptionServices;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;

namespace Atelia.DurableGraph.Persistence.Tests;

// Extra test-only access for deliberately environment-sensitive counterexamples.
// Ordinary view assertions inspect the issued Schema view; factories must not copy this bypass.
internal static class SchemaLookupCounterexampleProbe {
    private static readonly MethodInfo Lookup = typeof(StateBindingContext).GetMethod("TryGetSchema",
        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, binder: null,
        types: [typeof(TypeExpr), typeof(int), typeof(DurableSchema).MakeByRefType()], modifiers: null)
        ?? throw new InvalidOperationException("The protected Schema lookup counterexample seam was not found.");

    internal static bool TryGetSchema(StateBindingContext context, TypeExpr type, int version, out DurableSchema? schema) {
        object?[] arguments = [type, version, null];
        bool found;
        try {
            found = (bool)Lookup.Invoke(context, arguments)!;
        } catch (TargetInvocationException error) when (error.InnerException is not null) {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
        schema = (DurableSchema?)arguments[2];
        return found;
    }
}
