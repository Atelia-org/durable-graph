# Fixed inline library consumer

> 当前 G 门禁暂停：此 lane 仍依赖 `RevisionDecoder` 与 SCB1/raw Store 诊断；`Schemas/States` 已收为 internal，默认 owner 不授旧模式。后继迁移范围见 [DB-087 §36](../../../docs/design-branches/0087-meta-bootstrap-refactoring-route/0087-meta-bootstrap-refactoring-route.md#36-p1d-第七片旧typed恢复束退出)。下文保留匹配历史包版本的验收记录，不能作为当前门禁。

Run from the repository root:

```powershell
./experiments/PackageConsumerProbe/Run-InlineLibraryProbe.ps1
# Reuse a matching feed containing the nine Runtime/StateStore dependency packages:
./experiments/PackageConsumerProbe/Run-InlineLibraryProbe.ps1 -PackageSource <feed> -Version <version>
```

This [DB-060](../../../docs/design-branches/0060-cross-assembly-inline-history-slice/README.md) consumer
uses four independently compiled projects with only package dependencies:
`Host -> AppModel -> LibraryA -> LibraryB`. Model packages include ordinary SDK reference and
implementation assemblies; the runner checks that compilation selects `ref/` and execution selects `lib/`.
The pack-content target only includes the SDK-produced reference DLL. It does not run the generator,
copy schema history, or add analyzer/build imports.

The domain model uses ordinary value fields. `World.Position` contains LibraryA's `Coordinate`,
which contains LibraryB's public Point. Point stores its value through a private field of an internal
Leaf type. World also has a nullable Point, a generic `Pair<int>` and a private readonly Guid.
Editing the coordinate produces a nested World Delta; none of these inline values gets an ObjectId.

Library authors keep their own `.dgschema` directory and retain the exported historical Family helpers.
LibraryB has only ordinary struct declarations, so it sets `DurableGraphGenerateDefinitions=true`.
LibraryA enters Family generation through its generic value; AppModel enters through fixed external
inline fields. Each library exposes a public `Register` / `RegisterReaders` catalog that calls its own
internal `Generated.DurableDefinitions`. The Host registers all three explicitly. Importing build
metadata does not replace runtime registration or choose business Upgrade functions.

Generation two renames/deletes both LibraryB historical CLR types, changes the leaf from int to long,
and increments Point, Coordinate and World versions. AppModel explicitly converts its entire old World
DTO to the current DTO, adding 1000 to both coordinates. Each affected consumer is recompiled normally;
this lane makes no claim that inline layout changes preserve consumer DLL compatibility.

The Host first cold-decodes the original exact DTO without running Upgrade, then loads the current
World, rewrites one required Base, performs NoChange and ordinary Delta commits, and cold-reopens
without repeating Upgrade. The old revision remains exact-readable after the current head advances.

The runner builds and publishes each generation in dependency order, cleans and verifies each model,
and checks independent history counts: LibraryB `2 -> 4`, LibraryA `2 -> 3`, AppModel `1 -> 2`.
Accepted filenames, SHA-256 hashes and bytes remain unchanged; each file must belong to that project's
owned IDs. LibraryA never republishes LibraryB history, and AppModel never republishes either library.
Artifacts, the database and the complete `independent-history.json` evidence remain in a unique ignored
`obj/inline-library-*` directory. No existing artifacts are deleted.

Expected final markers:

- `InlineLibrarySeed:True:TransitivePrivateInline:True:NullableAndGeneric:True:OwnerDelta:True:ColdNoChange:True`
- `InlineLibraryUpgrade:True:DeletedInlineClr:True:HistoricalExact:True:OwnerBase:True:NoChangeThenDelta:True:ColdReopen:True`
- `InlineLibraryPackaging:True:ReferenceAssemblies:True:IndependentHistoryUnchanged:True:NoImportedHistoryPublication:True`

Negative metadata and missing-history cases belong to product generator/Build tests. This package
lane exercises the public successful delivery path and does not add cross-assembly inheritance,
automatic upgrade-rule discovery, or an application wrapper API.

Packing may emit NU5131 because the generated nuspec has no legacy `<references>` group.
This example supports PackageReference only; the runner independently verifies compile `ref/`
and runtime `lib/` selection. It does not validate `packages.config` consumers. MSBuild pack does
not directly support that nuspec group; see [NuGet's assembly-selection guidance](https://learn.microsoft.com/en-us/nuget/create-packages/select-assemblies-referenced-by-projects).
