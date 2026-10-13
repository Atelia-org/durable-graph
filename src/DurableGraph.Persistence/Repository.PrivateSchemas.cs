namespace Atelia.DurableGraph;

public sealed partial class Repository {
    /// <summary>Restores an existing branch and explicitly continues saving into its private Schema view.</summary>
    /// <remarks>
    /// Execution models use the nearest State's exact Schema pin. The private catalog uses the current
    /// business head's pin, including meta accepted by a later Event; it never falls back to latest shared.
    /// Private saving publishes only the branch directory. Reopening requires this explicit entry again:
    /// branch names and addresses do not persist or infer the application's write destination.
    /// </remarks>
    public BranchCheckout CheckoutPrivate(string branchName) => CheckoutMetaBootstrap(branchName, privateCatalog: true);
}
