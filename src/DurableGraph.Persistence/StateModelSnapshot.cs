using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Atelia.DurableGraph.Persistence;

/// <summary>One frozen application code catalog with successful closures memoized locally.</summary>
/// <remarks>
/// Models, readers, definitions and Upgrade providers are frozen. The optional SchemaStore remains
/// a live, repository-wide monotonic authority, so cached closures recheck definitions registered later.
/// </remarks>
internal sealed partial class StateModelSnapshot : StateBindingContext {
    private readonly Dictionary<TypeExpr, StateModelBinding> _models;
    private readonly Dictionary<Type, StateModelBinding> _types;
    private readonly Dictionary<SchemaKey, StateReaderBinding> _readers;
    private readonly Dictionary<string, StateDefinitionBinding> _definitions;
    private readonly Dictionary<Type, StateValueUpgradeRuleSet> _valueUpgradeRules;
    private readonly Dictionary<Type, StateDefinitionBinding> _domainDefinitions = [];
    private readonly Dictionary<Type, StateValueBinding> _currentValues = [];
    private readonly Dictionary<SchemaKey, StateValueBinding> _storedValues = [];
    private readonly Dictionary<DurableFieldInfo, StateValueBinding> _storedNullableValues = [];
    // A closure may use standard resolution services for dependencies outside its own layout.
    private readonly Dictionary<object, ExactSchemaRequirementSet> _closureRequirements = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<(string Kind, object Key)> _closing = [];
    private readonly HashSet<Type> _validatedDomainTypes = [];
    private readonly Dictionary<Type, bool> _managedValueTypes = [];
    private readonly SchemaStore? _schemas;

    internal StateModelSnapshot(Dictionary<TypeExpr, StateModelBinding> models,
        Dictionary<Type, StateModelBinding> types, Dictionary<SchemaKey, StateReaderBinding> readers,
        Dictionary<string, StateDefinitionBinding>? definitions = null, SchemaStore? schemas = null,
        Dictionary<Type, StateValueUpgradeRuleSet>? valueUpgradeRules = null,
        Type? arrayElementUpgradeRuleSet = null, Type? listElementUpgradeRuleSet = null,
        ListDeltaAlgorithm listDeltaAlgorithm = ListDeltaAlgorithm.Adaptive,
        Type? dictionaryKeyUpgradeRuleSet = null, Type? dictionaryValueUpgradeRuleSet = null,
        Dictionary<Type, object>? dictionaryComparers = null, Func<Type, object?>? dictionaryComparerResolver = null) {
        _models = models;
        _types = types;
        _readers = readers;
        _definitions = definitions ?? new(StringComparer.Ordinal);
        _valueUpgradeRules = valueUpgradeRules ?? [];
        ArrayElementUpgradeRuleSet = arrayElementUpgradeRuleSet;
        ListElementUpgradeRuleSet = listElementUpgradeRuleSet;
        DictionaryKeyUpgradeRuleSet = dictionaryKeyUpgradeRuleSet;
        DictionaryValueUpgradeRuleSet = dictionaryValueUpgradeRuleSet;
        _dictionaryComparers = dictionaryComparers ?? [];
        _dictionaryComparerResolver = dictionaryComparerResolver;
        if (!Enum.IsDefined(listDeltaAlgorithm)) { throw new ArgumentOutOfRangeException(nameof(listDeltaAlgorithm)); }
        _listDeltaAlgorithm = listDeltaAlgorithm;
        _schemas = schemas;
        foreach (StateDefinitionBinding definition in _definitions.Values) {
            if (definition.DomainTypeDefinition is { } domain && !_domainDefinitions.TryAdd(domain, definition)) {
                throw new InvalidOperationException("A domain declaration is registered under more than one definition.");
            }
        }
    }

    internal IReadOnlyDictionary<TypeExpr, StateModelBinding> Models => _models;
    internal IReadOnlyDictionary<Type, StateModelBinding> Types => _types;
    internal IReadOnlyDictionary<SchemaKey, StateReaderBinding> Readers => _readers;
    public override Type? ArrayElementUpgradeRuleSet { get; }
    public override Type? ListElementUpgradeRuleSet { get; }
    public override Type? DictionaryKeyUpgradeRuleSet { get; }
    public override Type? DictionaryValueUpgradeRuleSet { get; }

    internal static void RegisterValueUpgradeRuleSet(Dictionary<Type, StateValueUpgradeRuleSet> rules,
        StateValueUpgradeRuleSet ruleSet) {
        ArgumentNullException.ThrowIfNull(ruleSet);
        if (rules.TryGetValue(ruleSet.RuleSet, out StateValueUpgradeRuleSet? prior)) {
            if (!ReferenceEquals(prior, ruleSet)) {
                throw new InvalidOperationException($"A different value upgrade ruleset is already registered for {ruleSet.RuleSet}.");
            }
            return;
        }
        rules.Add(ruleSet.RuleSet, ruleSet);
    }

    public override StateValueUpgradeRuleSet GetValueUpgradeRuleSet(Type ruleSet) =>
        _valueUpgradeRules.TryGetValue(ruleSet, out StateValueUpgradeRuleSet? rules) ? rules :
        throw new InvalidDataException($"No value upgrade ruleset is registered for {ruleSet}.");

    internal static void RegisterDefinition(Dictionary<string, StateDefinitionBinding> definitions, StateDefinitionBinding definition) {
        ArgumentNullException.ThrowIfNull(definition);
        if (definitions.TryGetValue(definition.DefinitionId, out StateDefinitionBinding? existing)) {
            if (!ReferenceEquals(existing, definition)) {
                throw new InvalidOperationException($"A different definition is already registered for {definition.DefinitionId}.");
            }
            return;
        }
        if (definition.DomainTypeDefinition is { } domain && definitions.Values.Any(item => item.DomainTypeDefinition == domain)) {
            throw new InvalidOperationException("A different definition already owns this CLR declaration.");
        }
        definitions.Add(definition.DefinitionId, definition);
    }

    public override StateDefinitionBinding GetDefinition(string definitionId) =>
        _definitions.TryGetValue(definitionId, out StateDefinitionBinding? definition) ? definition :
        throw new InvalidDataException($"No declaration factory is registered for {definitionId}.");

    protected override SchemaKind GetNamedDeclarationKind(TypeExpr nominal) {
        // Definition errors are authoritative; an existing concrete model must not mask them.
        // Only an exact closed nominal match can replace missing reference declaration metadata.
        // Explicit readers supply the same evidence for a read-only catalog without current models.
        // Do not close current factories here: recursive reference graphs stop at this boundary.
        if (nominal.Kind == TypeExprKind.Named && nominal.IsClosed &&
            !_definitions.ContainsKey(nominal.DefinitionId!) &&
            (_models.ContainsKey(nominal) || _readers.Keys.Any(key => key.Type == nominal))) {
            return SchemaKind.ReferenceObject;
        }
        return base.GetNamedDeclarationKind(nominal);
    }

    public override bool TryGetSchema(TypeExpr type, int version, out DurableSchema? schema) {
        if (_schemas is not null && _schemas.TryGet(new SchemaKey(type, version), out schema)) { return true; }
        schema = null;
        return false;
    }

    public override bool TryGetCurrentModel(Type domainType, out StateModelBinding? model) {
        RequireClosed(domainType);
        if (_types.TryGetValue(domainType, out model)) {
            CheckClosureRequirements(model);
            if (_definitions.ContainsKey(model.CurrentSchema.SchemaId)) { BindSchema(model.CurrentSchema); }
            else { CheckRegistered(model.CurrentSchema); }
            return true;
        }
        if (!TryDefinition(domainType, out StateDefinitionBinding? definition) || definition!.CurrentModelFactory is null) {
            model = null;
            return false;
        }
        var active = ("model", (object)domainType);
        Begin(active);
        try {
            using var collection = BeginSchemaRequirementCollection();
            model = definition.CurrentModelFactory(domainType, this);
            if (model.DomainType != domainType || model.CurrentSchema.Type != GetTypeExpr(domainType) ||
                model.CurrentSchema.Kind != definition.Kind || model.CurrentSchema.Version != definition.CurrentVersion) {
                throw new InvalidDataException("A current model factory returned the wrong domain or exact current Schema.");
            }
            BindSchema(model.CurrentSchema);
            if (_models.TryGetValue(model.CurrentSchema.Type, out StateModelBinding? byFamily) && !ReferenceEquals(byFamily, model)) {
                throw new InvalidDataException("A second current model was produced for the same constructed family.");
            }
            foreach (StateReaderBinding reader in model.Readers) {
                SchemaKey key = new(reader.Schema.Type, reader.Schema.Version);
                if (_readers.TryGetValue(key, out StateReaderBinding? prior) && !ReferenceEquals(prior, reader)) {
                    throw new InvalidDataException("The model did not reuse its snapshot's exact reader binding.");
                }
            }
            ExactSchemaRequirementSet requirements = collection.Complete();
            _closureRequirements.Add(model, requirements);
            foreach (StateReaderBinding reader in model.Readers) {
                if (_readers.TryAdd(new(reader.Schema.Type, reader.Schema.Version), reader)) {
                    _closureRequirements.TryAdd(reader, requirements);
                }
            }
            _models.Add(model.CurrentSchema.Type, model);
            _types.Add(domainType, model);
            return true;
        } finally { _closing.Remove(active); }
    }

    public override StateReaderBinding ResolveReader(DurableSchema schema) {
        ArgumentNullException.ThrowIfNull(schema);
        schema.RequireReferenceObject();
        SchemaKey key = new(schema.Type, schema.Version);
        if (_readers.TryGetValue(key, out StateReaderBinding? prior)) {
            if (!prior.Schema.Equals(schema)) { throw new InvalidDataException("A reader key was reused with a different complete Schema."); }
            CheckClosureRequirements(prior);
            if (_definitions.ContainsKey(schema.SchemaId)) { BindSchema(schema); }
            else { CheckRegistered(schema); }
            return prior;
        }
        StateDefinitionBinding definition = GetDefinition(schema.SchemaId);
        var factory = definition.HistoricalReaderFactory ?? throw new InvalidDataException("The definition has no historical object reader factory.");
        var active = ("reader", (object)key);
        Begin(active);
        try {
            using var collection = BeginSchemaRequirementCollection();
            BindSchema(schema);
            StateReaderBinding result = factory(schema, this);
            if (!schema.Equals(result.Schema)) { throw new InvalidDataException("A historical reader factory returned another exact Schema."); }
            RememberClosureRequirements(result, collection);
            _readers.Add(key, result);
            return result;
        } finally { _closing.Remove(active); }
    }

    public override StateValueBinding ResolveCurrentValue(Type domainType) {
        RequireClosed(domainType);
        if (_currentValues.TryGetValue(domainType, out StateValueBinding? prior)) {
            CheckClosureRequirements(prior);
            if (prior.Slot.ValueSchema is { } inline) { CheckRegistered(inline); }
            return prior;
        }
        if (BuiltinStateValues.TryBindCurrent(domainType, out StateValueBinding builtin)) {
            _currentValues.Add(domainType, builtin);
            return builtin;
        }
        if (Nullable.GetUnderlyingType(domainType) is { } underlying) {
            using var collection = BeginSchemaRequirementCollection();
            StateValueBinding child = ResolveCurrentValue(underlying);
            StateValueBinding result = new(DurableFieldInfo.Nullable(1, child.Slot),
                typeof(NullableState<>).MakeGenericType(child.StateType),
                typeof(NullableStateOps<,>).MakeGenericType(child.StateType, child.StateOpsType),
                domainType, typeof(NullableValueProjection<,,>).MakeGenericType(underlying, child.StateType, child.ProjectionType!));
            RememberClosureRequirements(result, collection);
            _currentValues.Add(domainType, result);
            return result;
        }
        TypeExpr nominal = GetTypeExpr(domainType);
        // Reference metadata must not recursively close the referenced object's body.
        if (domainType.IsArray || IsListType(domainType) || IsDictionaryType(domainType) ||
            (domainType.IsClass && typeof(IDurableObject).IsAssignableFrom(domainType))) {
            StateValueBinding reference = new(DurableFieldInfo.Reference(1, nominal), typeof(ObjectId), typeof(ObjectIdStateOps),
                domainType, typeof(ObjectValueProjection<>).MakeGenericType(domainType));
            _currentValues.Add(domainType, reference);
            return reference;
        }
        StateDefinitionBinding definition = GetDefinition(nominal.DefinitionId!);
        var factory = definition.CurrentValueFactory ?? throw new InvalidDataException("No current inline value projection is registered.");
        var active = ("current value", (object)domainType);
        Begin(active);
        try {
            using var collection = BeginSchemaRequirementCollection();
            StateValueBinding result = factory(domainType, this);
            if (result.DomainType != domainType || result.ProjectionType is null || result.Slot.InlineSchema is not { } schema ||
                schema.Type != nominal || schema.Version != definition.CurrentVersion) {
                throw new InvalidDataException("The current value factory returned the wrong exact projection.");
            }
            BindSchema(schema);
            RememberClosureRequirements(result, collection);
            _currentValues.Add(domainType, result);
            return result;
        } finally { _closing.Remove(active); }
    }

    public override StateValueBinding ResolveStoredValue(DurableFieldInfo slot) {
        if (BuiltinStateValues.TryBindStored(slot, out StateValueBinding builtin)) { return builtin; }
        if (slot.TypeTag == TypeTag.Nullable) {
            DurableFieldInfo nullableKey = WithFieldId(slot, 1);
            if (slot.ValueSchema is { } dependency) { CheckRegistered(dependency); }
            if (_storedNullableValues.TryGetValue(nullableKey, out StateValueBinding? cached)) {
                CheckClosureRequirements(cached);
                return cached.WithFieldId(slot.FieldId);
            }
            using var collection = BeginSchemaRequirementCollection();
            StateValueBinding child = ResolveStoredValue(slot.NullableLayout!.ElementSlot);
            StateValueBinding result = new(nullableKey, typeof(NullableState<>).MakeGenericType(child.StateType),
                typeof(NullableStateOps<,>).MakeGenericType(child.StateType, child.StateOpsType));
            RememberClosureRequirements(result, collection);
            _storedNullableValues.Add(nullableKey, result);
            return result.WithFieldId(slot.FieldId);
        }
        DurableSchema schema = slot.InlineSchema ?? throw new InvalidDataException("Unsupported stored value slot.");
        SchemaKey key = new(schema.Type, schema.Version);
        if (_storedValues.TryGetValue(key, out StateValueBinding? prior)) {
            if (!schema.Equals(prior.Slot.InlineSchema)) { throw new InvalidDataException("An inline value key was reused with a different exact Schema."); }
            CheckClosureRequirements(prior);
            CheckRegistered(schema);
            return prior.WithFieldId(slot.FieldId);
        }
        var factory = GetDefinition(schema.SchemaId).HistoricalValueFactory ??
            throw new InvalidDataException("No retained inline body factory is registered.");
        var active = ("stored value", (object)key);
        Begin(active);
        try {
            using var collection = BeginSchemaRequirementCollection();
            BindSchema(schema);
            StateValueBinding result = factory(schema, this);
            if (!schema.Equals(result.Slot.InlineSchema)) { throw new InvalidDataException("An inline body factory returned another exact Schema."); }
            StateValueBinding canonical = result.WithFieldId(1);
            RememberClosureRequirements(canonical, collection);
            _storedValues.Add(key, canonical);
            return result.WithFieldId(slot.FieldId);
        } finally { _closing.Remove(active); }
    }

    public override TypeExpr GetTypeExpr(Type domainType) {
        RequireClosed(domainType);
        if (BuiltinStateValues.TryBindCurrent(domainType, out StateValueBinding builtin)) { return NominalType(builtin.Slot); }
        if (Nullable.GetUnderlyingType(domainType) is { } underlying) { return TypeExpr.Nullable(GetTypeExpr(underlying)); }
        if (domainType.IsArray) {
            TypeExpr element = GetTypeExpr(domainType.GetElementType()!);
            return domainType.IsSZArray ? TypeExpr.VectorArray(element) : TypeExpr.MultiDimArray(element, domainType.GetArrayRank());
        }
        if (IsListType(domainType)) { return TypeExpr.List(GetTypeExpr(domainType.GetGenericArguments()[0])); }
        if (IsDictionaryType(domainType)) {
            Type[] arguments = domainType.GetGenericArguments();
            return TypeExpr.Dictionary(GetTypeExpr(arguments[0]), GetTypeExpr(arguments[1]));
        }
        if (_types.TryGetValue(domainType, out StateModelBinding? model)) { return model.CurrentSchema.Type; }
        if (!TryDefinition(domainType, out StateDefinitionBinding? definition)) {
            throw new InvalidDataException($"No supported declaration is registered for {domainType}.");
        }
        return TypeExpr.Named(definition!.DefinitionId, domainType.IsGenericType
            ? domainType.GetGenericArguments().Select(GetTypeExpr).ToArray() : []);
    }

    public override Type GetDomainType(TypeExpr type) {
        ArgumentNullException.ThrowIfNull(type);
        if (!type.IsClosed) { throw new InvalidDataException("A current domain lookup requires a closed nominal type."); }
        if (type.IsNullable) {
            Type child = GetDomainType(type.ElementType!);
            if (!child.IsValueType || Nullable.GetUnderlyingType(child) is not null) {
                throw new InvalidDataException("A Nullable operand must be a supported non-nullable value type.");
            }
            Type nullable = typeof(Nullable<>).MakeGenericType(child);
            RequireClosed(nullable);
            return nullable;
        }
        if (type.IsDictionary) {
            Type dictionary = typeof(Dictionary<,>).MakeGenericType(GetDomainType(type.KeyType!), GetDomainType(type.ValueType!));
            RequireClosed(dictionary);
            return dictionary;
        }
        if (type.IsList) {
            Type list = typeof(List<>).MakeGenericType(GetDomainType(type.ElementType!));
            RequireClosed(list);
            return list;
        }
        if (type.IsArray) {
            Type element = GetDomainType(type.ElementType!);
            Type array = type.Kind == TypeExprKind.VectorArray ? element.MakeArrayType() : element.MakeArrayType(type.ArrayRank);
            RequireClosed(array);
            return array;
        }
        if (type.Kind == TypeExprKind.Builtin) {
            return type.BuiltinTag switch {
                TypeTag.Boolean => typeof(bool), TypeTag.Byte => typeof(byte), TypeTag.SByte => typeof(sbyte),
                TypeTag.Int16 => typeof(short), TypeTag.UInt16 => typeof(ushort), TypeTag.Int32 => typeof(int),
                TypeTag.UInt32 => typeof(uint), TypeTag.Int64 => typeof(long), TypeTag.UInt64 => typeof(ulong),
                TypeTag.Char => typeof(char), TypeTag.Half => typeof(Half), TypeTag.Single => typeof(float),
                TypeTag.Double => typeof(double), TypeTag.String => typeof(string),
                TypeTag.Guid => typeof(Guid), TypeTag.Decimal => typeof(decimal), TypeTag.TimeSpan => typeof(TimeSpan),
                TypeTag.DateOnly => typeof(DateOnly), TypeTag.TimeOnly => typeof(TimeOnly), TypeTag.DateTimeOffset => typeof(DateTimeOffset),
                _ => throw new InvalidDataException("Unsupported builtin domain type."),
            };
        }
        if (_models.TryGetValue(type, out StateModelBinding? model)) {
            RequireClosed(model.DomainType);
            return model.DomainType;
        }
        StateDefinitionBinding definition = GetDefinition(type.DefinitionId!);
        if (definition.Arity != type.Arguments.Length || definition.DomainTypeDefinition is not { } domain) {
            throw new InvalidDataException("This historical family has no matching current CLR declaration.");
        }
        if (definition.Arity == 0) { RequireClosed(domain); return domain; }
        try {
            Type result = domain.MakeGenericType(type.Arguments.Select(GetDomainType).ToArray());
            RequireClosed(result);
            return result;
        }
        catch (ArgumentException error) { throw new InvalidDataException("The closed domain type violates its generic constraints.", error); }
    }

    private bool TryDefinition(Type type, out StateDefinitionBinding? definition) =>
        _domainDefinitions.TryGetValue(type.IsGenericType ? type.GetGenericTypeDefinition() : type, out definition);

    private static bool IsListType(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>);
    private static bool IsDictionaryType(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>);

    private void Begin((string Kind, object Key) active) {
        if (!_closing.Add(active)) { throw new InvalidDataException($"Recursive exact {active.Kind} binding is unsupported."); }
    }

    private void CheckClosureRequirements(object binding) {
        if (_closureRequirements.TryGetValue(binding, out ExactSchemaRequirementSet? requirements)) { requirements.Validate(this); }
    }

    private void RememberClosureRequirements(object binding, SchemaRequirementCollection collection) {
        ExactSchemaRequirementSet requirements = collection.Complete();
        if (_closureRequirements.TryGetValue(binding, out ExactSchemaRequirementSet? prior)) {
            ExactSchemaRequirementSet.Builder merged = new();
            merged.Add(prior);
            merged.Add(requirements);
            requirements = merged.Build();
        }
        _closureRequirements[binding] = requirements;
    }

    private void RequireClosed(Type type) {
        ArgumentNullException.ThrowIfNull(type);
        if (_validatedDomainTypes.Contains(type)) { return; }
        if (type.ContainsGenericParameters || type.IsByRefLike || type.IsPointer || type.IsByRef) {
            throw new InvalidDataException("The domain type is not a supported closed value or durable declaration.");
        }
        if (type.IsArray) {
            int rank = type.GetArrayRank();
            if (!type.IsSZArray && rank is < 2 or > 4) {
                throw new InvalidDataException("Only SZ arrays and rank 2 through 4 multidimensional arrays are supported.");
            }
            RequireClosed(type.GetElementType()!);
        }
        if (type.IsGenericType) {
            Type[] arguments = type.GetGenericArguments();
            Type[] parameters = type.GetGenericTypeDefinition().GetGenericArguments();
            for (int index = 0; index < arguments.Length; index++) {
                RequireClosed(arguments[index]);
                // The CLR enforces the ordinary struct/class/new()/type constraints, but
                // MakeGenericType does not enforce C#'s additional unmanaged requirement.
                if (parameters[index].IsDefined(typeof(IsUnmanagedAttribute), inherit: false) && ContainsManagedReferences(arguments[index])) {
                    throw new InvalidDataException("A closed domain argument violates its unmanaged generic constraint.");
                }
            }
        }
        _validatedDomainTypes.Add(type);
    }

    private bool ContainsManagedReferences(Type type) {
        if (_managedValueTypes.TryGetValue(type, out bool result)) { return result; }
        MethodInfo method = typeof(StateModelSnapshot).GetMethod(nameof(ContainsManagedReferencesTyped), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(type);
        result = method.CreateDelegate<Func<bool>>()();
        _managedValueTypes.Add(type, result);
        return result;
    }

    private static bool ContainsManagedReferencesTyped<T>() => RuntimeHelpers.IsReferenceOrContainsReferences<T>();
}
