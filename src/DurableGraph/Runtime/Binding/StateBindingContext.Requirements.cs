using Atelia.DurableGraph.Schema;

namespace Atelia.DurableGraph.Runtime;

public abstract partial class StateBindingContext {
    private SchemaRequirementCollection? _schemaRequirementCollection;

    /// <summary>Collects standard exact-layout validations in this context until completion or disposal.</summary>
    internal SchemaRequirementCollection BeginSchemaRequirementCollection() {
        SchemaRequirementCollection scope = new(this, _schemaRequirementCollection);
        _schemaRequirementCollection = scope;
        return scope;
    }

    /// <summary>Checks every exact Schema embedded in an object's source or current layout.</summary>
    internal void CheckObjectLayout(ObjectLayout layout, string path) {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(path);
        ExactSchemaRequirementSet.Builder requirements = new();
        switch (layout.Kind) {
            case ObjectStateKind.Durable:
                requirements.Add(layout.Schema!, path);
                break;
            case ObjectStateKind.Array:
                AddSlot(layout.Array!.ElementSlot, $"{path}.element");
                break;
            case ObjectStateKind.List:
                AddSlot(layout.List!.ElementSlot, $"{path}.element");
                break;
            case ObjectStateKind.Dictionary:
                AddSlot(layout.Dictionary!.KeySlot, $"{path}.key");
                AddSlot(layout.Dictionary.ValueSlot, $"{path}.value");
                break;
            case ObjectStateKind.String:
                return;
            default:
                throw new InvalidDataException("Unknown object layout kind.");
        }
        requirements.Build().Validate(this);

        void AddSlot(DurableFieldInfo slot, string slotPath) {
            if (slot.ValueSchema is { } schema) { requirements.Add(schema, slotPath); }
        }
    }

    /// <summary>A synchronous lexical scope; only completed child scopes contribute to their parent.</summary>
    internal sealed class SchemaRequirementCollection : IDisposable {
        private readonly StateBindingContext _context;
        private readonly SchemaRequirementCollection? _parent;
        private readonly ExactSchemaRequirementSet.Builder _requirements = new();
        private bool _closed;

        internal SchemaRequirementCollection(StateBindingContext context, SchemaRequirementCollection? parent) {
            _context = context;
            _parent = parent;
        }

        internal void Add(ExactSchemaRequirementSet requirements) => _requirements.Add(requirements);

        internal ExactSchemaRequirementSet Complete() {
            RequireCurrent();
            ExactSchemaRequirementSet result = _requirements.Build();
            _parent?.Add(result);
            Dispose();
            return result;
        }

        public void Dispose() {
            if (_closed) { return; }
            RequireCurrent();
            _context._schemaRequirementCollection = _parent;
            _closed = true;
        }

        private void RequireCurrent() {
            if (_closed || !ReferenceEquals(_context._schemaRequirementCollection, this)) {
                throw new InvalidOperationException("Schema requirement scopes must complete or dispose in lexical order.");
            }
        }
    }
}
