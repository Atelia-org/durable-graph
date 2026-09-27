using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
namespace Atelia.DurableGraph.Persistence;

internal sealed partial class StateModelSnapshot {
    private readonly Dictionary<Type, ListObjectBinding> _currentLists = [];
    private readonly Dictionary<ListLayout, ObjectReaderBinding> _listReaders = [];
    private readonly ListDeltaAlgorithm _listDeltaAlgorithm;

    private bool TryGetCurrentListBinding(Type domainType, out ObjectBinding? binding) {
        if (_currentLists.TryGetValue(domainType, out ListObjectBinding? prior)) {
            CheckClosureRequirements(prior);
            CheckContainerElement(prior.ListLayout.ElementSlot);
            binding = prior;
            return true;
        }
        var active = ("List", (object)domainType);
        Begin(active);
        try {
            using var collection = BeginSchemaRequirementCollection();
            // Reference slots do not close their target body: List<Node> and
            // structs containing List<the same struct> therefore terminate here.
            StateValueBinding element = ResolveCurrentValue(domainType.GetGenericArguments()[0]);
            ListLayout layout = new(element.Slot);
            ListObjectBinding result = ListObjectBinding.Create(domainType, layout, element,
                (target, source) => NormalizeList(source, target), _listDeltaAlgorithm);
            if (result.DomainType != domainType || !result.ListLayout.Equals(layout)) {
                throw new InvalidDataException("A List factory returned another current type or exact layout.");
            }
            CheckContainerElement(layout.ElementSlot);
            RememberClosureRequirements(result, collection);
            _currentLists.Add(domainType, result);
            binding = result;
            return true;
        } finally { _closing.Remove(active); }
    }

    private ObjectReaderBinding ResolveListReader(ObjectLayout layout) {
        ListLayout list = layout.List!;
        if (_listReaders.TryGetValue(list, out ObjectReaderBinding? prior)) {
            CheckClosureRequirements(prior);
            CheckContainerElement(list.ElementSlot);
            return prior;
        }
        var active = ("List reader", (object)list);
        Begin(active);
        try {
            using var collection = BeginSchemaRequirementCollection();
            // Retained state operations suffice; no historical domain CLR struct
            // or current object model is required to decode exact stored content.
            CheckContainerElement(list.ElementSlot);
            StateValueBinding element = ResolveStoredValue(list.ElementSlot);
            ObjectReaderBinding result = ListStateReader.Create(list, element);
            if (!result.Layout.Equals(layout)) {
                throw new InvalidDataException("A List reader factory returned another exact layout.");
            }
            CheckContainerElement(list.ElementSlot);
            RememberClosureRequirements(result, collection);
            _listReaders.Add(list, result);
            return result;
        } finally { _closing.Remove(active); }
    }
}
