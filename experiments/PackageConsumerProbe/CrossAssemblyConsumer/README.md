# Cross-assembly model consumer

This DB-059 regression uses three independently compiled assemblies:

`DomainLibrary nupkg → AppModel nupkg → Host`

All dependencies use `PackageReference`. Neither model project links the other's sources or
history, imports an analyzer manually, uses friend access, or suppresses generated-name diagnostics.
Both model projects publish their own history; the pure Host declares no durable types and owns no history.

Run from the repository root:

```powershell
./experiments/PackageConsumerProbe/Run-CrossAssemblyProbe.ps1
# Reuse a freshly packed runtime feed (aliases: FeedPath / PackageVersion):
./experiments/PackageConsumerProbe/Run-CrossAssemblyProbe.ps1 -PackageSource <feed> -Version <version>
```

The script creates isolated feeds, package caches, history and database under the probe's ignored `obj`.
It builds two DomainLibrary packages and one AppModel package. After V1 saves the graph, it replaces
only the library DLL with the V2 package's DLL in a separate runtime directory. It checks the library
assembly identity is unchanged, its bytes changed, and the AppModel and Host DLL hashes remain identical.
This tests compatible library replacement; it does not claim arbitrary CLR API removal is binary compatible.

## Minimal library and host pattern

Libraries with ordinary non-generic class/struct declarations can opt into complete generated definitions:

```xml
<DurableGraphGenerateDefinitions>true</DurableGraphGenerateDefinitions>
```

The V1 DomainLibrary has only those ordinary declarations, making the packaged compiler property
necessary. Each library wraps its internal generated aggregator in its own public catalog:

```csharp
public static void Register(IStateModelRegistration models) =>
    Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
```

Host registers both catalogs before opening a session:

```csharp
var models = new StateModelRegistry();
DomainCatalog.Register(models);
AppCatalog.Register(models);
using var repository = EventHistoryRepository.CreateNew(path, models, options);
using var session = repository.CreateBranch("main", World.Create(), policy);
// Later: EventHistoryRepository.OpenExisting(path, models, options), then repository.Resume<World>("main").
```

Each library can similarly expose `RegisterReaders(IStateReaderRegistration)` for stored-exact inspection.
The probe keeps generated DTO names inside the owning library; its test facades project only the values
needed by Host assertions. This is ordinary consumer code, not a new framework discovery protocol.

Default package builds publish each project's current `.dgschema`; the script follows with
`-p:DurableGraphSchemaHistoryMode=Verify`. All previously accepted filenames, SHA256 hashes and complete
bytes are checked before and after later publication. Domain history grows from two to four files;
AppModel retains exactly one World v1 file. No external history is copied into the app.

## What the two generations prove

- World v1 has two references to one external Node. The Node refers to itself. Cold reads preserve both identities.
- Changing only Node produces one Node Delta and leaves World unchanged; cold recapture produces NoChange.
- V2 keeps public `Node`, `Value`, `Next`, factory and catalog signatures, but removes the old private-layout
  inline CLR declaration. Retained Node v1 DTO reading remains possible, without executing Upgrade.
- An explicit Node v1→v2 Upgrade changes its inline count from int to long and adds 1000 as a business rule.
  Only Node rewrites Base. NoChange and ordinary Delta follow; another cold reopen does not repeat Upgrade.
- AppModel and Host are not recompiled for V2. Missing a public CLR type they actually use would be a different,
  unsupported binary compatibility change. The deleted old inline type never appears in their API signatures.

DB-059 also supports external nominal references in existing containers and dynamic generic parameters.
Direct fixed external struct fields, fixed external inline roots, and inheritance from external durable bases
remain rejected until a separate exact-template import contract is designed. The broader composition and
failure matrix is covered by the product tests; this package lane stays focused on delivery and history ownership.
