using Atelia.DurableGraph.Schema;
using System.Collections.Immutable;

namespace Atelia.DurableGraph.Runtime;

/// <summary>One fixed-version exact dependency in a declaration template.</summary>
public sealed record StateSchemaReference(TypeExpr Type, int Version);

/// <summary>One declared field type pattern; a named inline value or nullable named child has a fixed inline version.</summary>
public sealed record StateFieldTemplate(int FieldId, TypeExpr ValueType, int? InlineVersion = null);

/// <summary>One generated DTO type parameter and its declaration-scoped value expression.</summary>
public sealed record StateParameterTemplate(TypeExpr Expression, int? InlineVersion = null);

/// <summary>Retained schema declaration patterns, independent of current domain CLR types.</summary>
public sealed class StateSchemaTemplate {
    public StateSchemaTemplate(string definitionId, int version, SchemaKind kind, int arity,
        IEnumerable<StateFieldTemplate> fields, StateSchemaReference? baseSchema = null,
        Type? stateTypeDefinition = null, IEnumerable<StateParameterTemplate>? stateParameters = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);
        if (arity is < 0 or > TypeExpr.MaximumArity) { throw new ArgumentOutOfRangeException(nameof(arity)); }
        if (!Enum.IsDefined(kind)) { throw new ArgumentOutOfRangeException(nameof(kind)); }
        ArgumentNullException.ThrowIfNull(fields);
        DefinitionId = definitionId;
        Version = version;
        Kind = kind;
        Arity = arity;
        Fields = fields.OrderBy(static field => field.FieldId).ToImmutableArray();
        if (Fields.Any(static field => field.FieldId <= 0) ||
            Fields.Select(static field => field.FieldId).Distinct().Count() != Fields.Length) {
            throw new ArgumentException("Template field IDs must be positive and unique.", nameof(fields));
        }
        foreach (StateFieldTemplate field in Fields) {
            ValidatePattern(field.ValueType, arity);
            TypeExpr versionedType = field.ValueType.IsNullable ? field.ValueType.ElementType! : field.ValueType;
            if (field.InlineVersion is <= 0 || (field.InlineVersion.HasValue && versionedType.Kind != TypeExprKind.Named)) {
                throw new ArgumentException("A fixed inline dependency requires a named type and positive version.", nameof(fields));
            }
        }
        if (baseSchema is not null) {
            ValidatePattern(baseSchema.Type, arity);
            if (kind != SchemaKind.ReferenceObject || baseSchema.Type.Kind != TypeExprKind.Named || baseSchema.Version <= 0) {
                throw new ArgumentException("A base dependency requires a named reference type and positive version.", nameof(baseSchema));
            }
        }
        BaseSchema = baseSchema;
        StateTypeDefinition = stateTypeDefinition;
        StateParameters = stateParameters?.ToImmutableArray() ?? ImmutableArray<StateParameterTemplate>.Empty;
        foreach (StateParameterTemplate parameter in StateParameters) {
            ValidatePattern(parameter.Expression, arity);
            if (parameter.InlineVersion is <= 0) { throw new ArgumentException("A fixed state operand version must be positive.", nameof(stateParameters)); }
        }
        if (stateTypeDefinition is not null &&
            (StateParameters.Length == 0 ? stateTypeDefinition.ContainsGenericParameters :
                !stateTypeDefinition.IsGenericTypeDefinition || stateTypeDefinition.GetGenericArguments().Length != StateParameters.Length)) {
            throw new ArgumentException("The DTO definition must match its ordered representation parameters.", nameof(stateTypeDefinition));
        }
    }

    public string DefinitionId { get; }
    public int Version { get; }
    public SchemaKind Kind { get; }
    public int Arity { get; }
    public ImmutableArray<StateFieldTemplate> Fields { get; }
    public StateSchemaReference? BaseSchema { get; }
    public Type? StateTypeDefinition { get; }
    public ImmutableArray<StateParameterTemplate> StateParameters { get; }

    private static void ValidatePattern(TypeExpr pattern, int arity) {
        ArgumentNullException.ThrowIfNull(pattern);
        if (pattern.Kind == TypeExprKind.Parameter && pattern.ParameterOrdinal >= arity) {
            throw new ArgumentException("A type parameter ordinal is outside its declaration scope.");
        }
        foreach (TypeExpr argument in pattern.Arguments) { ValidatePattern(argument, arity); }
    }
}

/// <summary>Stable generated factories and retained history for one explicitly registered definition.</summary>
/// <remarks>
/// Factories close capabilities from declarations, exact layouts and the supplied context's resolution services.
/// Their callbacks must preserve stable persistent results for equal inputs, fixed code and explicit layouts
/// when required dependencies are valid; unrelated Schema presence or memo/call counts must not select business values.
/// Capture provides canonical current DTOs; same-layout restoration round-trips persistent fields and references.
/// Historical field changes belong to explicit upgrades. Do not modify input DTOs; application initialization
/// and per-view Transient state belong after complete graph delivery, outside Allocate/Hydrate.
/// Freezing the registration catalog does not copy delegate captures; their behavior must remain stable.
/// These caller obligations do not prove that Capture or Commit executed every custom reader validation.
/// </remarks>
public sealed class StateDefinitionBinding {
    public StateDefinitionBinding(string definitionId, SchemaKind kind, int arity, Type? domainTypeDefinition,
        IEnumerable<StateSchemaTemplate> templates,
        Func<Type, StateBindingContext, StateValueBinding>? currentValueFactory = null,
        Func<DurableSchema, StateBindingContext, StateValueBinding>? historicalValueFactory = null,
        Func<Type, StateBindingContext, StateModelBinding>? currentModelFactory = null,
        Func<DurableSchema, StateBindingContext, StateReaderBinding>? historicalReaderFactory = null,
        IEnumerable<StateUpgradeProvider>? upgrades = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionId);
        ArgumentNullException.ThrowIfNull(templates);
        if (arity is < 0 or > TypeExpr.MaximumArity) { throw new ArgumentOutOfRangeException(nameof(arity)); }
        if (!Enum.IsDefined(kind)) { throw new ArgumentOutOfRangeException(nameof(kind)); }
        if (domainTypeDefinition is not null &&
            ((arity == 0 && domainTypeDefinition.ContainsGenericParameters) ||
            (arity != 0 && (!domainTypeDefinition.IsGenericTypeDefinition || domainTypeDefinition.GetGenericArguments().Length != arity)))) {
            throw new ArgumentException("The CLR declaration must have the registered generic arity.", nameof(domainTypeDefinition));
        }
        if (domainTypeDefinition is not null && (domainTypeDefinition.IsByRefLike ||
            BuiltinStateValues.TryBindCurrent(domainTypeDefinition, out _) ||
            (kind == SchemaKind.InlineValue ? !domainTypeDefinition.IsValueType :
                (!domainTypeDefinition.IsClass || !typeof(IDurableObject).IsAssignableFrom(domainTypeDefinition))))) {
            throw new ArgumentException("The CLR declaration must match the supported durable class, inline struct or enum kind.", nameof(domainTypeDefinition));
        }
        DefinitionId = definitionId;
        Kind = kind;
        Arity = arity;
        DomainTypeDefinition = domainTypeDefinition;
        Templates = templates.OrderBy(static template => template.Version).ToImmutableArray();
        if (Templates.IsEmpty || Templates.Any(template => template.DefinitionId != definitionId || template.Arity != arity || template.Kind != kind) ||
            Templates.Select(static template => template.Version).Distinct().Count() != Templates.Length) {
            throw new ArgumentException("Definition history requires unique versions of the same declaration.", nameof(templates));
        }
        if (domainTypeDefinition?.IsEnum == true) {
            // This is a current CLR projection constraint, not an enum marker in
            // retained history. Earlier layouts may use another integer width
            // or come from an equivalent inline struct declaration.
            TypeTag underlyingTag = Type.GetTypeCode(Enum.GetUnderlyingType(domainTypeDefinition)) switch {
                TypeCode.SByte => TypeTag.SByte,
                TypeCode.Byte => TypeTag.Byte,
                TypeCode.Int16 => TypeTag.Int16,
                TypeCode.UInt16 => TypeTag.UInt16,
                TypeCode.Int32 => TypeTag.Int32,
                TypeCode.UInt32 => TypeTag.UInt32,
                TypeCode.Int64 => TypeTag.Int64,
                TypeCode.UInt64 => TypeTag.UInt64,
                _ => TypeTag.Invalid,
            };
            StateSchemaTemplate current = Templates[^1];
            if (underlyingTag == TypeTag.Invalid || current.Fields.Length != 1 ||
                current.Fields[0].FieldId != 1 || current.Fields[0].ValueType != TypeExpr.Builtin(underlyingTag) ||
                !current.StateParameters.IsEmpty) {
                throw new ArgumentException("The current enum template requires only field 1 with its CLR underlying integer type and no state parameters.", nameof(templates));
            }
        }
        CurrentValueFactory = currentValueFactory;
        HistoricalValueFactory = historicalValueFactory;
        CurrentModelFactory = currentModelFactory;
        HistoricalReaderFactory = historicalReaderFactory;
        Upgrades = upgrades?.ToImmutableArray() ?? ImmutableArray<StateUpgradeProvider>.Empty;
        if (Upgrades.Any(provider => provider.DefinitionId != definitionId)) {
            throw new ArgumentException("Upgrade providers must belong to the registered definition.", nameof(upgrades));
        }
        if (!Upgrades.IsEmpty && kind != SchemaKind.ReferenceObject) {
            throw new ArgumentException("Only identity-bearing owner definitions register object upgrade providers.", nameof(upgrades));
        }
        HashSet<int> versions = Templates.Select(static template => template.Version).ToHashSet();
        Dictionary<(TypeExpr? Owner, int From), StateUpgradeProvider> edges = [];
        foreach (StateUpgradeProvider provider in Upgrades) {
            if ((provider.ClosedOwner is { } owner && owner.Arguments.Length != arity) ||
                (provider.IsLegacyTwoParameter && arity != 0)) {
                throw new ArgumentException("Owner upgrade arity or legacy invocation shape disagrees with its declaration.", nameof(upgrades));
            }
            if (!versions.Contains(provider.FromVersion) || !versions.Contains(provider.ToVersion)) {
                throw new ArgumentException("An owner upgrade requires both retained adjacent Schema templates.", nameof(upgrades));
            }
            // A zero-arity declaration has only one owner. Its nominally "closed"
            // spelling cannot introduce a second entry beside the ordinary edge.
            var key = (arity == 0 ? null : provider.ClosedOwner, provider.FromVersion);
            if (edges.TryGetValue(key, out StateUpgradeProvider? prior) && !prior.IsSameCapability(provider)) {
                throw new ArgumentException("Conflicting owner upgrade capabilities claim the same adjacent edge.", nameof(upgrades));
            }
            edges.TryAdd(key, provider);
        }
    }

    public string DefinitionId { get; }
    public SchemaKind Kind { get; }
    public int Arity { get; }
    public Type? DomainTypeDefinition { get; }
    public ImmutableArray<StateSchemaTemplate> Templates { get; }
    public int CurrentVersion => Templates[^1].Version;
    public Func<Type, StateBindingContext, StateValueBinding>? CurrentValueFactory { get; }
    public Func<DurableSchema, StateBindingContext, StateValueBinding>? HistoricalValueFactory { get; }
    public Func<Type, StateBindingContext, StateModelBinding>? CurrentModelFactory { get; }
    public Func<DurableSchema, StateBindingContext, StateReaderBinding>? HistoricalReaderFactory { get; }
    public ImmutableArray<StateUpgradeProvider> Upgrades { get; }
}
