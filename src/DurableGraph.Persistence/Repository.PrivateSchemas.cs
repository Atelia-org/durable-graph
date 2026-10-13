using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Persistence.MetaBootstrapP0;
using Atelia.DurableGraph.Schema;

namespace Atelia.DurableGraph;

public sealed partial class Repository {
    /// <summary>Creates a new State branch using a complete frozen fork of the shared Schema view.</summary>
    /// <remarks>
    /// The supplied overrides replace complete exact definitions for this private view. Unchanged declarations
    /// remain present, and inconsistent dependencies are rejected. Capture uses the resulting view with this
    /// open's frozen model configuration; no old business graph is materialized. The new graph writes all live
    /// objects as Base and publishes only the branch directory. Shared Schema roots are unchanged.
    /// This explicit entry permits private saving on a writable open even without an Open-level write mode.
    /// Optional container replacements identify both sides by complete layouts, including exact slots and codec.
    /// Each old layout must be an actual declared dynamic container in the frozen shared view; untouched
    /// declarations remain. These replacements do not authorize conflicting Schema definitions: use schemaOverrides.
    /// Previously accepted nodes, frames and views remain immutable.
    /// </remarks>
    public BranchCheckout CreatePrivateBranch(string branchName, IDurableObject initialState,
        IEnumerable<DurableSchema> schemaOverrides, ReadAmplificationBaseBudgetParameters? parameters = null,
        IEnumerable<(ObjectLayout OldLayout, ObjectLayout NewLayout)>? containerReplacements = null) {
        RequireWriter();
        _busy = true;
        bool acquired = false, delivered = false;
        try {
            ArgumentNullException.ThrowIfNull(initialState);
            ArgumentNullException.ThrowIfNull(schemaOverrides);
            ValidateNewName(branchName);
            acquired = _activeBranches.Add(branchName);
            if (!acquired) { throw new InvalidOperationException("Branch already has an active checkout."); }
            BranchCheckout? checkout = null;
            // Initial shared loading, override enumeration and model closure run
            // inside the existing owner preparation guard, before any append.
            P0Session session = new(_splitRepository, branchName, _splitConfiguration, schemaOverrides,
                () => EnsureMetaBootstrapEditingAllowed(checkout!), containerReplacements);
            checkout = new(this, branchName, session, null, usesPrivateSchemas: true);
            PublishMetaBootstrap(checkout, initialState, P0GraphKind.State, parameters ?? DefaultPolicy);
            delivered = true;
            return checkout;
        } finally {
            if (acquired && !delivered) { _activeBranches.Remove(branchName); }
            EndOperation();
        }
    }

    /// <summary>Restores an existing branch and explicitly continues saving into its private Schema view.</summary>
    /// <remarks>
    /// Execution models use the nearest State's exact Schema pin. The private catalog uses the current
    /// business head's pin, including meta accepted by a later Event; it never falls back to latest shared.
    /// Private saving publishes only the branch directory. Reopening requires this explicit entry again:
    /// branch names and addresses do not persist or infer the application's write destination.
    /// </remarks>
    public BranchCheckout CheckoutPrivate(string branchName) => CheckoutMetaBootstrap(branchName, privateCatalog: true);
}
