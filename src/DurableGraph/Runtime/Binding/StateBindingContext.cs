using Atelia.DurableGraph.Schema;
using System.Collections.Immutable;

namespace Atelia.DurableGraph.Runtime;

/// <summary>Resolves current models inside one frozen model catalog.</summary>
public interface IStateModelResolver {
    bool TryGetCurrentModel(Type domainType, out StateModelBinding? model);
    bool TryGetCurrentObjectBinding(Type domainType, out ObjectBinding? binding) {
        ArgumentNullException.ThrowIfNull(domainType);
        if (domainType == typeof(string)) { binding = StringObjectBinding.Instance; return true; }
        bool found = TryGetCurrentModel(domainType, out StateModelBinding? model);
        binding = model;
        return found;
    }
}

/// <summary>Cold-path closure services for generated factories in one immutable code catalog.</summary>
/// <remarks>Memoized bindings belong to this snapshot. No method registers application capabilities or allocates object IDs.</remarks>
public abstract partial class StateBindingContext : IStateModelResolver {
    private readonly Dictionary<(TypeExpr Type, int Version), StateSchemaBinding> _schemaBindings = [];

    public abstract bool TryGetCurrentModel(Type domainType, out StateModelBinding? model);
    public virtual bool TryGetCurrentObjectBinding(Type domainType, out ObjectBinding? binding) {
        ArgumentNullException.ThrowIfNull(domainType);
        if (domainType == typeof(string)) { binding = StringObjectBinding.Instance; return true; }
        bool found = TryGetCurrentModel(domainType, out StateModelBinding? model);
        binding = model;
        return found;
    }
    public abstract StateValueBinding ResolveCurrentValue(Type domainType);
    public abstract StateValueBinding ResolveStoredValue(DurableFieldInfo slot);
    public abstract StateReaderBinding ResolveReader(DurableSchema schema);
    public virtual ObjectReaderBinding ResolveObjectReader(ObjectLayout layout) => layout.Kind switch {
        ObjectStateKind.String => StringObjectReader.Instance,
        ObjectStateKind.Durable => ResolveReader(layout.Schema!),
        _ => throw new InvalidDataException("No historical object reader is available for this layout."),
    };
    public abstract TypeExpr GetTypeExpr(Type domainType);
    public abstract Type GetDomainType(TypeExpr type);
    public abstract StateDefinitionBinding GetDefinition(string definitionId);
    public abstract bool TryGetSchema(TypeExpr type, int version, out DurableSchema? schema);

    /// <summary>Checks a closed named identity without binding the referenced object's body.</summary>
    /// <remarks>
    /// A catalog may supply reference-only evidence from an already registered exact nominal model or reader
    /// when no Definition exists. This does not provide retained templates, readers or upgrades.
    /// </remarks>
    protected virtual SchemaKind GetNamedDeclarationKind(TypeExpr nominal) {
        ArgumentNullException.ThrowIfNull(nominal);
        if (nominal.Kind != TypeExprKind.Named || !nominal.IsClosed) {
            throw new InvalidDataException("A named value requires a closed nominal identity.");
        }
        StateDefinitionBinding definition = GetDefinition(nominal.DefinitionId!);
        if (definition.Arity != nominal.Arguments.Length) {
            throw new InvalidDataException("A named value has the wrong generic arity.");
        }
        return definition.Kind;
    }

    public StateModelBinding ResolveCurrentModel(Type domainType) =>
        TryGetCurrentModel(domainType, out StateModelBinding? model) ? model! :
        throw new InvalidDataException($"No current model is registered for {domainType}.");

    public StateModelBinding ResolveCurrentModel(TypeExpr type) => ResolveCurrentModel(GetDomainType(type));

    public StateSchemaTemplate GetTemplate(string definitionId, int version) =>
        GetDefinition(definitionId).Templates.FirstOrDefault(template => template.Version == version) ??
        throw new InvalidDataException($"No retained Schema template exists for {definitionId} v{version}.");

    /// <summary>Checks stored layout against declaration patterns and derives exact free-value operands.</summary>
    public StateSchemaBinding BindSchema(DurableSchema schema) {
        ArgumentNullException.ThrowIfNull(schema);
        var key = (schema.Type, schema.Version);
        if (_schemaBindings.TryGetValue(key, out StateSchemaBinding? prior)) {
            if (!prior.Schema.Equals(schema)) { throw new InvalidDataException("The snapshot encountered conflicting complete Schemas for one key."); }
            CheckRegistered(schema);
            return prior;
        }
        CheckRegistered(schema);
        Dictionary<(TypeExpr Expression, int? Version), DurableFieldInfo> values = [];
        List<DurableFieldInfo> fields = [];
        TypeExpr[] parameters = Enumerable.Range(0, schema.Type.Arguments.Length).Select(TypeExpr.Parameter).ToArray();
        Match(schema, parameters, schema.Type.Arguments, values, fields, new(), 1);
        StateSchemaBinding result = new(this, schema, values, fields);
        _schemaBindings.Add(key, result);
        return result;
    }

    private void Match(DurableSchema actual, IReadOnlyList<TypeExpr> symbolicArguments,
        IReadOnlyList<TypeExpr> rootArguments,
        Dictionary<(TypeExpr Expression, int? Version), DurableFieldInfo> values,
        List<DurableFieldInfo>? flattened, HashSet<(DurableSchema Schema, TypeExpr Scope)> matched, int depth) {
        if (depth > 256) { throw new InvalidDataException("Exact Schema template traversal exceeded its depth bound."); }
        // Repeated fields can share a complete inline DAG. Revisit only when the
        // declaration variables have a different meaning in the owner's scope.
        if (!matched.Add((actual, TypeExpr.Named(actual.SchemaId, symbolicArguments.ToArray())))) { return; }
        StateSchemaTemplate template = GetTemplate(actual.SchemaId, actual.Version);
        if (template.Kind != actual.Kind || template.Arity != actual.Type.Arguments.Length ||
            template.Arity != symbolicArguments.Count || template.Fields.Length != actual.Fields.Length) {
            throw new InvalidDataException("Stored Schema kind, arity or declared fields do not match retained history.");
        }
        if ((template.BaseSchema is null) != (actual.BaseSchema is null)) {
            throw new InvalidDataException("Stored Schema ancestry does not match retained history.");
        }
        if (template.BaseSchema is { } basePattern) {
            TypeExpr symbolicBase = Substitute(basePattern.Type, symbolicArguments);
            DurableSchema actualBase = actual.BaseSchema!;
            if (actualBase.Type != Substitute(symbolicBase, rootArguments) || actualBase.Version != basePattern.Version) {
                throw new InvalidDataException("Stored exact base does not match its retained declaration pattern.");
            }
            Match(actualBase, symbolicBase.Arguments, rootArguments, values, flattened, matched, depth + 1);
        }
        for (int index = 0; index < template.Fields.Length; index++) {
            StateFieldTemplate pattern = template.Fields[index];
            DurableFieldInfo slot = actual.Fields[index];
            TypeExpr expression = Substitute(pattern.ValueType, symbolicArguments);
            TypeExpr nominal = Substitute(expression, rootArguments);
            if (slot.FieldId != pattern.FieldId || NominalType(slot) != nominal) {
                throw new InvalidDataException("A stored field does not match its retained type pattern.");
            }
            TypeExpr declaredValue = pattern.ValueType.IsNullable ? pattern.ValueType.ElementType! : pattern.ValueType;
            MatchValue(slot, expression, pattern.InlineVersion, declaredValue.Kind != TypeExprKind.Parameter);
            flattened?.Add(slot);
        }

        void MatchValue(DurableFieldInfo slot, TypeExpr expression, int? inlineVersion, bool requiresFixedVersion) {
            TypeExpr nominal = Substitute(expression, rootArguments);
            if (nominal.Kind == TypeExprKind.Named) {
                SchemaKind valueKind = GetNamedDeclarationKind(nominal);
                if ((valueKind == SchemaKind.InlineValue) != (slot.TypeTag == TypeTag.InlineValue)) {
                    throw new InvalidDataException("A parameter or named field has the wrong declaration kind or generic arity.");
                }
            }
            if (inlineVersion is { } exactVersion) {
                if (slot.ValueSchema is not { } inline || inline.Version != exactVersion) {
                    throw new InvalidDataException("A stored inline dependency has the wrong exact version.");
                }
            } else if (requiresFixedVersion && slot.TypeTag == TypeTag.InlineValue) {
                throw new InvalidDataException("A named inline field requires a fixed historical version.");
            }
            // A parameter means the entire exact slot. Named dependent values also propagate
            // their inner variables through the callee's declaration scope.
            int? bindingVersion = expression.Kind == TypeExprKind.Parameter ? null : inlineVersion;
            var valueKey = (expression, bindingVersion);
            DurableFieldInfo canonical = WithFieldId(slot, 1);
            if (values.TryGetValue(valueKey, out DurableFieldInfo seen) && seen != canonical) {
                throw new InvalidDataException("Repeated use of one value expression has inconsistent exact slot semantics.");
            }
            values[valueKey] = canonical;
            if (expression.IsNullable) {
                MatchValue(slot.NullableLayout!.ElementSlot, expression.ElementType!, inlineVersion, requiresFixedVersion);
            } else if (slot.ValueSchema is { } nested) {
                if (expression.Kind == TypeExprKind.Named) {
                    Match(nested, expression.Arguments, rootArguments, values, null, matched, depth + 1);
                } else {
                    // A single T can itself be a generic struct. Its internal declaration
                    // variables belong to that value, not to the enclosing owner's ordinals.
                    BindSchema(nested);
                }
            }
        }
    }

    internal void CheckRegistered(DurableSchema schema) {
        ExactSchemaRequirementSet.Create((schema, "schema root")).Validate(this);
    }

    /// <summary>A plan-local certificate of every authoritative exact layout it depends on.</summary>
    private sealed class ExactSchemaRequirementSet {
        private readonly Requirement[] _requirements;

        private ExactSchemaRequirementSet(Requirement[] requirements) {
            _requirements = requirements;
        }

        internal static ExactSchemaRequirementSet Create(params (DurableSchema Schema, string Path)[] roots) {
            Builder builder = new();
            foreach ((DurableSchema schema, string path) in roots) { builder.Add(schema, path); }
            return builder.Build();
        }

        internal void Validate(StateBindingContext context) {
            foreach (Requirement requirement in _requirements) {
                DurableSchema expected = requirement.Schema;
                if (context.TryGetSchema(expected.Type, expected.Version, out DurableSchema? registered) &&
                    !expected.Equals(registered)) {
                    throw new InvalidDataException(
                        $"Schema requirement '{requirement.Path}' expects {expected.Type} v{expected.Version}, " +
                        "but the repository registered a different exact definition.",
                        new SchemaConflictException(registered!, expected));
                }
            }
        }

        internal sealed class Builder {
            private readonly Dictionary<(TypeExpr Type, int Version), Entry> _requirements = [];
            private readonly List<Entry> _ordered = [];

            internal void Add(DurableSchema schema, string path) => Visit(schema, path, 1);

            internal ExactSchemaRequirementSet Build() => new(_ordered
                .Select(static entry => new Requirement(entry.Schema, entry.Path)).ToArray());

            private int Visit(DurableSchema schema, string path, int depth) {
                if (depth > 256) { throw new InvalidDataException($"Exact Schema layout at '{path}' exceeded its depth bound."); }
                var key = (schema.Type, schema.Version);
                if (_requirements.TryGetValue(key, out Entry? known)) {
                    if (!known.Schema.Equals(schema)) {
                        throw new InvalidDataException(
                            $"Exact Schema {schema.Type} v{schema.Version} has conflicting layouts at " +
                            $"'{known.Path}' and '{path}'.");
                    }
                    if (depth + known.Height - 1 > 256) {
                        throw new InvalidDataException($"Exact Schema layout at '{path}' exceeded its depth bound.");
                    }
                    return known.Height;
                }

                // Reserve the key before following children so every later occurrence is
                // compared with the first stable path. Public immutable Schemas cannot form cycles.
                Entry pending = new(schema, path);
                _requirements.Add(key, pending);
                _ordered.Add(pending);
                int height = 1;
                if (schema.BaseSchema is { } ancestor) {
                    height = Math.Max(height, Visit(ancestor, $"{path}.base", depth + 1) + 1);
                }
                foreach (DurableFieldInfo field in schema.Fields) {
                    if (field.ValueSchema is not { } inline) { continue; }
                    height = Math.Max(height,
                        Visit(inline, $"{path}.field[{field.FieldId}].inline", depth + 1) + 1);
                }
                pending.Height = height;
                return height;
            }
        }

        private sealed record Requirement(DurableSchema Schema, string Path);

        private sealed class Entry(DurableSchema schema, string path) {
            internal DurableSchema Schema { get; } = schema;
            internal string Path { get; } = path;
            internal int Height { get; set; } = 1;
        }
    }

    public static TypeExpr Substitute(TypeExpr expression, IReadOnlyList<TypeExpr> arguments) {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(arguments);
        if (expression.Kind == TypeExprKind.Parameter) {
            if (expression.ParameterOrdinal >= arguments.Count) { throw new InvalidDataException("A parameter is outside its declaration scope."); }
            return arguments[expression.ParameterOrdinal];
        }
        if (expression.Kind == TypeExprKind.Builtin || expression.Arguments.IsEmpty) { return expression; }
        if (expression.IsList) { return TypeExpr.List(Substitute(expression.ElementType!, arguments)); }
        if (expression.IsDictionary) {
            return TypeExpr.Dictionary(Substitute(expression.KeyType!, arguments), Substitute(expression.ValueType!, arguments));
        }
        if (expression.IsNullable) { return TypeExpr.Nullable(Substitute(expression.ElementType!, arguments)); }
        if (expression.IsArray) {
            TypeExpr element = Substitute(expression.ElementType!, arguments);
            return expression.ArrayRank == 1 ? TypeExpr.VectorArray(element) : TypeExpr.MultiDimArray(element, expression.ArrayRank);
        }
        return TypeExpr.Named(expression.DefinitionId!, expression.Arguments.Select(argument => Substitute(argument, arguments)).ToArray());
    }

    public static DurableFieldInfo WithFieldId(DurableFieldInfo slot, int fieldId) => slot.TypeTag switch {
        TypeTag.ObjectReference => DurableFieldInfo.Reference(fieldId, slot.TargetType!),
        TypeTag.InlineValue => new(fieldId, TypeTag.InlineValue, inlineSchema: slot.InlineSchema),
        TypeTag.Nullable => DurableFieldInfo.Nullable(fieldId, slot.NullableLayout!.ElementSlot),
        _ => new(fieldId, slot.TypeTag),
    };

    public static TypeExpr NominalType(DurableFieldInfo slot) => slot.TypeTag switch {
        TypeTag.ObjectReference => slot.TargetType!,
        TypeTag.InlineValue => slot.InlineSchema!.Type,
        TypeTag.Nullable => TypeExpr.Nullable(NominalType(slot.NullableLayout!.ElementSlot)),
        _ => TypeExpr.Builtin(slot.TypeTag),
    };
}

/// <summary>Validated exact layout and its declaration-scoped free-value bindings.</summary>
public sealed class StateSchemaBinding {
    private readonly StateBindingContext _context;
    private readonly IReadOnlyDictionary<(TypeExpr Expression, int? Version), DurableFieldInfo> _values;

    internal StateSchemaBinding(StateBindingContext context, DurableSchema schema,
        Dictionary<(TypeExpr Expression, int? Version), DurableFieldInfo> values, IEnumerable<DurableFieldInfo> fields) {
        _context = context;
        Schema = schema;
        _values = values;
        FieldSlots = fields.ToImmutableArray();
    }

    public DurableSchema Schema { get; }
    public ImmutableArray<DurableFieldInfo> FieldSlots { get; }
    public StateBindingContext Context => _context;
    internal IReadOnlyDictionary<(TypeExpr Expression, int? Version), DurableFieldInfo> ValueSlots => _values;

    public StateValueBinding GetValue(TypeExpr expression, int? inlineVersion = null) {
        ArgumentNullException.ThrowIfNull(expression);
        if (_values.TryGetValue((expression, inlineVersion), out DurableFieldInfo slot)) {
            return _context.ResolveStoredValue(slot);
        }
        if (!inlineVersion.HasValue) {
            DurableFieldInfo[] candidates = _values.Where(pair => pair.Key.Expression == expression).Select(static pair => pair.Value).Distinct().ToArray();
            if (candidates.Length == 1) { return _context.ResolveStoredValue(candidates[0]); }
        }
        throw new InvalidDataException("The exact Schema does not uniquely bind this value expression.");
    }

    internal bool TryGetSlot(TypeExpr expression, int? version, out DurableFieldInfo slot) =>
        _values.TryGetValue((expression, version), out slot);
}
