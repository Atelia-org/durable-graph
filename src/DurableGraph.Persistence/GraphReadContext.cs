using Atelia.DurableGraph.Runtime;

namespace Atelia.DurableGraph.Persistence;

/// <summary>Source-independent state for one synchronous graph-restoration operation.</summary>
internal sealed class GraphReadContext {
    private readonly HashSet<object> _mutableInstances = new(ReferenceEqualityComparer.Instance);

    internal GraphReadContext(ModelExecutionEnvironment models, GraphReadStatistics? statistics = null) {
        ArgumentNullException.ThrowIfNull(models);
        Models = models;
        Statistics = statistics ?? new();
        MeasureMaterialization = statistics is not null;
    }

    internal ModelExecutionEnvironment Models { get; }
    internal GraphReadStatistics Statistics { get; }
    internal bool MeasureMaterialization { get; }

    internal void RequireUniqueMutableInstance(object instance) {
        if (!_mutableInstances.Add(instance)) {
            throw new InvalidDataException("Independent graphs must allocate distinct mutable instances.");
        }
    }
}
