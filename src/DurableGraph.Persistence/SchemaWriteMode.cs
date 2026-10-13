namespace Atelia.DurableGraph;

/// <summary>The explicitly selected destination for Schemas captured by business saves.</summary>
public enum SchemaWriteMode {
    /// <summary>Use the repository's shared Schema catalog and jointly publish additions with the business graph.</summary>
    Shared = 1,
}
