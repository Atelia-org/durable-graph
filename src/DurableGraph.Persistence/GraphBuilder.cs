using Atelia.DurableGraph.Persistence;

namespace Atelia.DurableGraph;

/// <summary>An independent complete graph being edited against its last saved snapshot.</summary>
/// <remarks>
/// A builder belongs to one repository but occupies no business branch. Different builders can
/// be saved in any serial order. OpenGraph creates independent mutable instances; sharing CLR
/// instances supplied to CreateGraph remains the caller's responsibility. Keep the graph stable
/// while saving. Dispose neither saves nor rolls back domain changes.
/// </remarks>
public sealed class GraphBuilder : IDisposable {
    private readonly Repository _repository;
    private BusinessPersistenceAdapter? _business;
    private IDurableObject? _root;

    internal GraphBuilder(Repository repository, BusinessPersistenceAdapter business, IDurableObject root) {
        _repository = repository;
        _business = business;
        _root = root;
    }

    /// <summary>The editable graph root. It may be replaced with another registered durable object.</summary>
    /// <remarks>Replacing the root does not save. Existing reachable objects retain their identities on SaveGraph.</remarks>
    public IDurableObject Root {
        get { RequireLive(_repository); _repository.RequireGraphAccess(); return _root!; }
        set {
            RequireLive(_repository);
            _repository.RequireGraphAccess();
            ArgumentNullException.ThrowIfNull(value);
            _root = value;
        }
    }

    internal BusinessPersistenceAdapter Business => _business!;
    internal IDurableObject RootForSave => _root!;
    internal void RequireLive(Repository repository) {
        ObjectDisposedException.ThrowIf(_business is null, this);
        if (!ReferenceEquals(repository, _repository)) {
            throw new ArgumentException("A graph builder must be saved by its owning repository.");
        }
    }

    /// <summary>Releases this builder's graph and comparison baseline without saving.</summary>
    /// <remarks>Idempotent. Disposal during a repository operation is rejected.</remarks>
    public void Dispose() {
        if (_business is null) { return; }
        _repository.RequireGraphRelease();
        _business = null;
        _root = null;
    }
}
