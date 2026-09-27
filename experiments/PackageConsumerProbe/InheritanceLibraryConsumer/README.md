# Cross-assembly inheritance package consumer

Run from the repository root:

```powershell
./experiments/PackageConsumerProbe/Run-InheritanceLibraryProbe.ps1
# Or reuse a matching feed with the nine Runtime/StateStore dependency packages:
./experiments/PackageConsumerProbe/Run-InheritanceLibraryProbe.ps1 -PackageSource <feed> -Version <version>
```

This [DB-061](../../../docs/design-branches/0061-cross-assembly-inheritance-slice.md) consumer
uses four independent projects: `Host -> AppModel -> MiddleLibrary -> BaseLibrary`.
Only PackageReference connects them. The three model packages include SDK-produced `ref/`
compile assets and `lib/` runtime assets; the runner verifies NuGet's selected asset paths.
The pack-content target includes the reference DLL and does not copy history, import build
scripts, or add generator/analyzer references. No friend assemblies or ProjectReference are used.

`Leaf : Middle<int> : LegacyBase<int>` crosses two library boundaries. Base is abstract and
stores private readonly `InternalPair<T>`, `InternalPoint?`, and Guid fields. Middle adds its
own private readonly fields. The leaf cannot name those internal implementation types.
Each layer reuses FieldId 1; the generated complete leaf DTO and body preserve declaration
segments. The Host constructs two leaves with no parameterless constructor, then creates
base-declared cyclic references, aliases and a polymorphic `List<Base<int>>` containing both.
Only the two leaves and the List have object rows; base and inline states do not acquire IDs.

The seed process independently changes an ancestor field, a leaf field and a child object.
Each save must write exactly the changed leaf as Delta. Cold loading preserves private readonly
state and identity, runs no constructors, and permits an empty NoChange commit.

Generation two deletes `LegacyBase<T>`, `InternalPair<T>` and `InternalPoint`, replacing them
with new CLR names under the same Schema IDs and incremented versions. Pair's stamp and Point's
value widen from int to long. Base, Middle and Leaf explicitly advance their owner versions.
All affected consumers are rebuilt; this example does not claim binary compatibility after a
base CLR type is removed. Nominal identity and stored reference IDs remain unchanged.

The leaf's explicit Upgrade converts its entire flattened DTO, adding 1000 once to the hidden
stamp and optional point. Base and Middle each register their own owner rule, but neither runs
for a Leaf object. Historical exact decoding runs no Upgrade; editable loading invokes precisely
two leaf edges. Both upgraded leaves require Base, while their unchanged polymorphic List does
not. NoChange, ordinary Delta and another cold reopen follow without repeating upgrades or
running constructors. The old revision remains exact-readable after the current head advances.

Model catalogs expose stable handwritten facades:

```csharp
var models = new StateModelRegistry();
AppCatalog.Register(models);
MiddleCatalog.Register(models);
BaseCatalog.Register(models);
using var repository = EventHistoryRepository.OpenExisting(directory, models);
using var session = repository.Resume<Leaf>("main");
session.State.Ancestor++;
session.CommitDomainEvent(session.State);
session.CommitDomainState(new ReadAmplificationBaseBudgetParameters(8, 10));
```

Each library calls its own internal `Generated.DurableDefinitions`; `RegisterReaders` provides
retained exact decoding separately. Host registration is explicit and completes before opening
the graph. Compile-time imported templates do not automatically register runtime factories or
business rules. BaseLibrary forces Family generation with `DurableGraphGenerateDefinitions=true`;
external inheritance automatically selects Family for downstream models.

The runner performs Publish, Clean, Verify and Pack in dependency order for each generation.
History counts are BaseLibrary `3 -> 6`, MiddleLibrary `1 -> 2`, AppModel `1 -> 2`; the pure Host
owns no history. It compares accepted filenames, SHA-256 hashes and full bytes, and rejects any
history file with a foreign declared Schema ID. Artifacts and `independent-history.json` remain
in a unique ignored `obj/inheritance-library-*` directory; no prior artifacts are deleted.

Expected markers:

- `InheritanceLibrarySeed:True:TwoExternalBaseEdges:True:HiddenReadonlyGenericNullable:True:PolymorphicCycles:True:AncestorAndLeafDelta:True:ColdNoChange:True`
- `InheritanceLibraryUpgrade:True:DeletedBaseAndInlineClr:True:HistoricalExact:True:LeafOnlyUpgrade:True:RequiredBases:True:NoChangeThenDelta:True:ColdReopen:True`
- `InheritanceLibraryPackaging:True:ReferenceAssemblies:True:TwoExternalBaseEdges:True:IndependentHistoryUnchanged:True:NoImportedHistoryPublication:True`

Failure cases such as missing exports, wrong exact ancestors, ungranted base projections and
missing owner Upgrade edges belong to the product test suite. This lane verifies successful
real package delivery, rather than defining alternate recovery or rule-discovery behavior.

Packing may emit NU5131 because the generated nuspec has no legacy `<references>` group.
This example supports PackageReference only; the runner independently verifies compile `ref/`
and runtime `lib/` selection. It does not validate `packages.config` consumers. MSBuild pack does
not directly support that nuspec group; see [NuGet's assembly-selection guidance](https://learn.microsoft.com/en-us/nuget/create-packages/select-assemblies-referenced-by-projects).
