using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

if (args is ["--help"]) {
    Console.WriteLine("ExplicitStorageClassificationProbe [--output <observations.json>]");
    return 0;
}
if (args.Length != 0 && (args.Length != 2 || args[0] != "--output")) {
    Console.Error.WriteLine("Use --help or --output <observations.json>.");
    return 2;
}

string output = args.Length == 0
    ? Path.Combine("obj", "ExplicitStorageClassificationProbe", "observations.json")
    : args[1];
string? platformAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
if (string.IsNullOrEmpty(platformAssemblies)) {
    Console.Error.WriteLine("This probe requires a normal .NET host exposing TRUSTED_PLATFORM_ASSEMBLIES.");
    return 2;
}
MetadataReference[] references = platformAssemblies.Split(Path.PathSeparator)
    .Concat([typeof(IDurableObject).Assembly.Location, typeof(DurableGraphGenerator).Assembly.Location])
    .DistinctBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
    .Select(path => MetadataReference.CreateFromFile(path)).ToArray();
CSharpParseOptions parse = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp14);
var fixtures = new (string Name, string Declaration)[] {
    ("ordinary_durable_auto", "public partial class Model : IDurableObject { [field:DurableField(1)] public int Value { get; set; } }"),
    ("ordinary_transient_auto", "public partial class Model : IDurableObject { [field:Transient] public int Value { get; set; } }"),
    ("ordinary_unmarked_auto", "public partial class Model : IDurableObject { public int Value { get; set; } }"),
    ("ordinary_field_backed", "public partial class Model : IDurableObject { [field:DurableField(1)] public int Value { get => field + 100; set => field = value; } }"),
    ("ordinary_get_only", "public partial class Model : IDurableObject { [field:DurableField(1)] public int Value { get; } }"),
    ("ordinary_computed_durable", "public partial class Model : IDurableObject { [field:DurableField(1)] public int Value => 42; }"),
    ("ordinary_computed_transient", "public partial class Model : IDurableObject { [field:Transient] public int Value => 42; }"),
    ("ordinary_event_durable", "public partial class Model : IDurableObject { [field:DurableField(1)] public event System.Action? Changed; }"),
    ("ordinary_event_transient", "public partial class Model : IDurableObject { [field:Transient] public event System.Action? Changed; }"),
    ("ordinary_primary_capture", "public partial class Model([field:DurableField(1)] int seed) : IDurableObject { public int Read() => seed; }"),
    ("ordinary_static_durable", "public partial class Model : IDurableObject { [field:DurableField(1)] public static int Value { get; set; } }"),
    ("ordinary_static_transient", "public partial class Model : IDurableObject { [field:Transient] public static int Value { get; set; } }"),
    ("struct_durable_auto", "public partial struct Model { [field:DurableField(1)] public int Value { get; set; } }"),
    ("record_durable_auto", "public partial record Model : IDurableObject { [field:DurableField(1)] public int Value { get; set; } }")
};
List<object> observations = [];
List<string> shapeFailures = [];
foreach (var fixture in fixtures) {
    string source = "using Atelia.DurableGraph; [DurableType(\"probe.\" + nameof(Model),1)] " + fixture.Declaration;
    SyntaxTree tree = CSharpSyntaxTree.ParseText(source, parse, path: fixture.Name + ".cs");
    CSharpCompilation compilation = CSharpCompilation.Create("Probe_" + fixture.Name, [tree], references,
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
    SemanticModel semantic = compilation.GetSemanticModel(tree);
    TypeDeclarationSyntax declaration = tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>().Single();
    INamedTypeSymbol type = semantic.GetDeclaredSymbol(declaration)!;
    IFieldSymbol[] fields = type.GetMembers().OfType<IFieldSymbol>().ToArray();
    var semanticFields = fields.Select(field => new {
        field.Name, field.MetadataName, field.IsImplicitlyDeclared, field.IsStatic, field.IsReadOnly,
        Associated = field.AssociatedSymbol?.Kind.ToString(),
        Attributes = AttributeNames(field.GetAttributes())
    }).ToArray();
    var semanticProperties = type.GetMembers().OfType<IPropertySymbol>().Select(property => new {
        property.Name, property.IsStatic,
        Getter = property.GetMethod?.Name, Setter = property.SetMethod?.Name,
        Attributes = AttributeNames(property.GetAttributes())
    }).ToArray();
    var semanticEvents = type.GetMembers().OfType<IEventSymbol>().Select(member => new {
        member.Name, member.IsStatic, Attributes = AttributeNames(member.GetAttributes())
    }).ToArray();

    // Source-only emission is independent of Generator rejection. Events expose their
    // attributed backing storage in PE metadata even when Roslyn GetMembers omits it.
    EmissionObservation sourceEmission = ObserveEmission(compilation);
    GeneratorDriver driver = CSharpGeneratorDriver.Create([new DurableGraphGenerator().AsSourceGenerator()], parseOptions: parse);
    driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation updated, out var diagnostics);
    GeneratorDriverRunResult run = driver.GetRunResult();
    var generated = run.Results.SelectMany(result => result.GeneratedSources).ToArray();
    bool generatorHasErrors = diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    bool generatorThrew = run.Results.Any(result => result.Exception is not null);
    EmissionObservation generatedEmission = ObserveEmission(updated);
    observations.Add(new {
        fixture.Name, Source = source,
        SemanticFields = semanticFields, SemanticProperties = semanticProperties, SemanticEvents = semanticEvents,
        InputCSharpDiagnostics = ObserveDiagnostics(compilation.GetDiagnostics()), SourceEmission = sourceEmission,
        Generator = new {
            HasErrors = generatorHasErrors, Diagnostics = ObserveDiagnostics(diagnostics),
            Exceptions = run.Results.Where(result => result.Exception is not null)
                .Select(result => result.Exception!.GetType().FullName).ToArray()
        },
        GeneratedCSharpDiagnostics = ObserveDiagnostics(updated.GetDiagnostics()), GeneratedEmission = generatedEmission,
        GeneratedFiles = generated.Select(result => result.HintName).ToArray(),
        CandidateManifest = generated.Where(result => result.HintName == "DurableGraphSchemaHistoryCandidates.g.cs")
            .Select(result => result.SourceText.ToString()).SingleOrDefault()
    });

    Check(sourceEmission.Succeeded, fixture.Name + ": source-only Emit failed");
    if (fixture.Name is "ordinary_durable_auto" or "record_durable_auto") {
        Check(fields.Any(field => field.IsImplicitlyDeclared && field.AssociatedSymbol is IPropertySymbol
            && AttributeNames(field.GetAttributes()).Contains("Atelia.DurableGraph.DurableFieldAttribute")),
            fixture.Name + ": expected an attributed property backing field in Roslyn symbols");
        Check(sourceEmission.Fields.Any(field => field.Name == "<Value>k__BackingField"
            && field.Attributes.Contains("DurableFieldAttribute")), fixture.Name + ": expected attributed backing in PE metadata");
    }
    if (fixture.Name is "ordinary_event_durable" or "ordinary_event_transient") {
        string attribute = fixture.Name == "ordinary_event_durable" ? "DurableFieldAttribute" : "TransientAttribute";
        Check(semanticEvents.Length == 1 && fields.Length == 0,
            fixture.Name + ": expected event symbols without source field symbols");
        Check(sourceEmission.Fields.Any(field => field.Name == "Changed" && field.Attributes.Contains(attribute)),
            fixture.Name + ": expected attributed event backing in PE metadata");
    }
    if (fixture.Name == "record_durable_auto") {
        Check(!generatorHasErrors && !generatorThrew && generatedEmission.Succeeded,
            "record_durable_auto: supported Generator/Emit positive control failed");
        Check(generated.Any(result => result.HintName == "DurableGraphSchemaHistoryCandidates.g.cs"
            && result.SourceText.ToString().Contains("// field:1|2", StringComparison.Ordinal)),
            "record_durable_auto: expected persistent Int32 slot in candidate manifest");
    }
    Console.WriteLine($"{fixture.Name}: SGErrors={generatorHasErrors}; SG=[{string.Join(',', diagnostics.Select(diagnostic => diagnostic.Id))}]; CSharp=[{string.Join(',', updated.GetDiagnostics().Where(diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning).Select(diagnostic => diagnostic.Id).Distinct())}]; Emit={generatedEmission.Succeeded}");
}

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
File.WriteAllText(output, JsonSerializer.Serialize(new {
    FormatVersion = 1,
    LanguageVersion = "CSharp14",
    RoslynVersion = typeof(CSharpCompilation).Assembly.GetName().Version?.ToString(),
    ShapeChecks = new { Passed = shapeFailures.Count == 0, Failures = shapeFailures.ToArray() },
    Cases = observations
}, new JsonSerializerOptions { WriteIndented = true }) + "\n");
foreach (string failure in shapeFailures) Console.Error.WriteLine(failure);
Console.WriteLine($"Observed {fixtures.Length} cases; shape checks {(shapeFailures.Count == 0 ? "passed" : "failed")}.");
return shapeFailures.Count == 0 ? 0 : 1;

void Check(bool condition, string failure) {
    if (!condition) shapeFailures.Add(failure);
}

static string[] AttributeNames(IEnumerable<AttributeData> attributes) =>
    attributes.Select(attribute => attribute.AttributeClass?.ToDisplayString() ?? "<unresolved>").ToArray();

static object[] ObserveDiagnostics(IEnumerable<Diagnostic> diagnostics) => diagnostics
    .Where(diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning)
    .Select(diagnostic => (object)new {
        diagnostic.Id, Severity = diagnostic.Severity.ToString(),
        Message = diagnostic.GetMessage(CultureInfo.InvariantCulture)
    }).ToArray();

static EmissionObservation ObserveEmission(Compilation compilation) {
    using MemoryStream image = new();
    var result = compilation.Emit(image);
    if (!result.Success) return new(false, ObserveDiagnostics(result.Diagnostics), [], []);
    image.Position = 0;
    using PEReader pe = new(image, PEStreamOptions.LeaveOpen);
    MetadataReader metadata = pe.GetMetadataReader();
    TypeDefinition model = metadata.TypeDefinitions.Select(metadata.GetTypeDefinition)
        .Single(definition => metadata.GetString(definition.Name) == "Model");
    MetadataFieldObservation[] fields = model.GetFields().Select(metadata.GetFieldDefinition).Select(field =>
        new MetadataFieldObservation(metadata.GetString(field.Name), field.Attributes.ToString(),
            field.GetCustomAttributes().Select(AttributeName).ToArray())).ToArray();
    MetadataPropertyObservation[] properties = model.GetProperties().Select(metadata.GetPropertyDefinition).Select(property => {
        PropertyAccessors accessors = property.GetAccessors();
        return new MetadataPropertyObservation(metadata.GetString(property.Name),
            MethodName(accessors.Getter), MethodName(accessors.Setter), property.GetCustomAttributes().Select(AttributeName).ToArray());
    }).ToArray();
    return new(true, ObserveDiagnostics(result.Diagnostics), fields, properties);

    string? MethodName(MethodDefinitionHandle handle) => handle.IsNil ? null : metadata.GetString(metadata.GetMethodDefinition(handle).Name);

    string AttributeName(CustomAttributeHandle handle) {
        EntityHandle constructor = metadata.GetCustomAttribute(handle).Constructor;
        EntityHandle parent = constructor.Kind switch {
            HandleKind.MemberReference => metadata.GetMemberReference((MemberReferenceHandle)constructor).Parent,
            HandleKind.MethodDefinition => metadata.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType(),
            _ => default
        };
        return parent.Kind switch {
            HandleKind.TypeReference => metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)parent).Name),
            HandleKind.TypeDefinition => metadata.GetString(metadata.GetTypeDefinition((TypeDefinitionHandle)parent).Name),
            _ => parent.Kind.ToString()
        };
    }
}

internal sealed record EmissionObservation(bool Succeeded, object[] Diagnostics,
    MetadataFieldObservation[] Fields, MetadataPropertyObservation[] Properties);
internal sealed record MetadataFieldObservation(string Name, string Flags, string[] Attributes);
internal sealed record MetadataPropertyObservation(string Name, string? Getter, string? Setter, string[] Attributes);
