# Package consumer probe

> Active product regression. Run when changing generated output, runtime public APIs,
> history/build integration, or package wiring. Root-solution tests do not exercise this
> delivery boundary. Product progress lives in [src/PROJECT-STATE.md](../../src/PROJECT-STATE.md).

Storage dependencies now have their own version S, pinned in
[StorageDependency.props](../../eng/StorageDependency.props). The current self-contained runners share
[PackageProbeSupport.ps1](PackageProbeSupport.ps1): its Prepare helper downloads the five pinned public nupkg files
from nuget.org and returns their feed; it does not clone or rebuild storage sources. The runner then packs
only the four DG packages at its independent version G. `S != G` is required.
Normal repository build/test restores directly from nuget.org without Prepare. Source development and unique
local dev packages with an explicit NuGet.Config are described in [the dependency guide](../../docs/storage-dependency.md).
An explicit `-PackageSource <feed> -Version <G>` still consumes the supplied complete feed without repacking it.
The public consumer projects keep their minimal DG PackageReferences; storage arrives transitively.
Isolated restore uses a task config for the supplied complete feed rather than the repository's normal public sources.

This experiment verifies the reusable delivery boundary rather than project-to-project wiring.
The consumer project has one `PackageReference` to `Atelia.DurableGraph`; it contains no manual
analyzer reference, `AdditionalFiles`, build hook, or `Import`.

[Organization migration](Run-OrganizationMigrationProbe.ps1) compares two distinct real runtime and generator
package versions through isolated legacy/current lanes. It preserves identical model/history bytes, opens old
E/S frames, resumes a pending event, continues Delta/NoChange, and cold-opens in another process. It checks
every old complete frame at its original byte address and verifies read-only browsing leaves files unchanged.
Use mandatory `-LegacyPackageSource/-LegacyVersion/-PackageSource/-Version`; `-Stage Seed` then `-Stage Complete`
with the same `-WorkRoot` freezes legacy evidence before changing product sources. The separate
[API inventory](OrganizationApiInventory/README.md) compares compiled type/member shapes under the approved mapping.
This complements the older DurableBase migration witness; its old lane still uses its original packages and marker.
The organization migration keeps the pre-DB-077 facade calls in `Program.Legacy.cs`; its current lane
uses repository-open model configuration. The Storage extraction witness intentionally retains its
frozen older DG API in both lanes because only the Storage packages differ there.

The [cross-assembly consumer](CrossAssemblyConsumer/README.md) runs through `Run-CrossAssemblyProbe.ps1`:
independent DomainLibrary and AppModel packages expose public registration catalogs, and a pure Host saves
a shared cyclic graph. A compatible V2 library replacement leaves AppModel/Host DLL hashes and World history
unchanged, restores deleted-inline-type historical DTOs, upgrades only the target, and resumes Base/Delta saves.

The [fixed inline library consumer](InlineLibraryConsumer/README.md) runs through `Run-InlineLibraryProbe.ps1`:
Host, AppModel and two value libraries compile from real package reference assemblies. Direct fields compose
three layers of inline history, including private internal implementation values. Two generations retain
independent library history, explicitly upgrade affected owners and resume Base/NoChange/Delta saves.

The [inheritance library consumer](InheritanceLibraryConsumer/README.md) runs through `Run-InheritanceLibraryProbe.ps1`:
BaseLibrary, MiddleLibrary and AppModel form two cross-library inheritance edges through real ref/lib packages.
Private readonly hidden generic/Nullable base state, polymorphic aliases and cycles round-trip without constructors.
Two generations remove old base/inline CLR names, retain independent history and run only each leaf's explicit Upgrade
before required Base, NoChange and Delta saves.

Consumers use the runtime's non-generic `ObjectId` for captured/decoded identities, reference DTO
slots and UpgradeContext tracing. Storage rows and probe-owned address sidecars retain numeric
`uint` IDs; those boundaries use `.Value` or `new ObjectId(...)` explicitly. Ordinary numeric
`uint` fields and the independently specified reference-body golden bytes remain unchanged.

The [Dictionary consumer](DictionaryConsumer/README.md) exercises experimental BCL Dictionary support through
`Run-DictionaryProbe.ps1`: supported comparer policies, shared key/value references, unordered key-addressed Delta,
and independent historical enum-key/struct-value upgrades after deleting their old CLR declarations.

The [composite Dictionary consumer](CompositeDictionaryConsumer/README.md) runs through
`Run-CompositeDictionaryProbe.ps1`: generic struct keys whose IEquatable ignores a persistent Timestamp,
typed/resolver Application configuration, preserved restoration modes, and retained nested key/value history.
A separate same-Schema comparison change leaves exact DTO reading available while current TryAdd rejects a collision.

The [record consumer](RecordConsumer/README.md) runs through `Run-RecordProbe.ps1`: readonly positional
generic record keys and nested record values, explicit retained DTO upgrades after deleting old CLR names,
and current Default/Application dictionaries progressing from upgraded Base through NoChange to Delta.
Record-bearing compilations use the existing Family generated API; backing fields use `field:` attributes.

The [Enum consumer](EnumConsumer/README.md) adds explicit durable enum coverage through
`Run-EnumProbe.ps1`: unknown integer values, generic/Nullable/array/List composition,
retained history after deleting the old CLR enum, and explicit upgrades followed by Base/Delta resaves.
The current package writer emits history v9; accepted earlier history remains unchanged. Catalog v2 is retained.
The InlineStruct consumer keeps the ordinary generated API in V1/V2; after deleting the
inline CLR declarations in V3, it uses Family DTO aliases and unified definition registration.
This exercises the retained orphan-inline-history selection rule without changing its saved history.

Run from the repository root:

```powershell
./experiments/PackageConsumerProbe/Run-Probe.ps1
```

The probe packs a unique local package, uses an isolated package cache and Schema-history
directory under this experiment's ignored `obj` directory, and exercises local publish plus
CI-style read-only verification. It also proves that the packaged build target rejects legacy
`.dgsnapshot` files instead of silently ignoring them. Its V1/V2 lane proves that bare `[DurableType]` emits exact
Schema history and versioned State DTOs, including a compile-checked adjacent upgrade shape.

The feed also contains the runtime's Serialization dependency. A final consumer captures private
base/derived fields into a readonly versioned DTO through generated `__DurableState`,
then mutates the domain instance. Static DTO body calls verify the original golden bytes,
DTO/Schema pairing and domain isolation. It still has just one PackageReference;
Serialization is supplied transitively, with no friend access or manual analyzer wiring.

The same consumer also exercises every added scalar kind through generated Capture/DTO bodies,
including an isolated surrogate, negative zero and NaN payloads against fixed golden bytes.
This validates the public primitive API and publication of the extended Schema tags.

The same consumer prepares a Delta between frozen base/derived DTOs after mutating
their source objects. It checks independent bitmap/value golden bytes, the actual Delta length,
and a no-change result whose zero bitmap is nonempty. The public `PreparedDeltaBody` comes from the
transitive Serialization package; its owned `Body` is reused directly for repeated generated
Apply calls, with complete body consumption and golden Base reconstruction. An invalid padding
bit must fail. The script requires the `PreparedDeltaBody:True` marker in addition to the existing
markers. This covers the same-Schema body API, not persisted Delta records or prior-chain validation.

The consumer also calls generated `PrepareBaseBody(in V1)` on the frozen inherited DTO and the prebuilt
`StringPayloadCodec.PrepareBase(string)` for nonempty and empty string content. The public
`PreparedBaseBody` and string helper arrive through the same transitive Serialization package.
Golden bytes, later domain mutation and changes to external body copies verify reusable owned
body content. The script additionally requires `PreparedBaseBody:True`; this is body preparation,
not Storage envelope sizing or a complete Save operation.

Generated AddRoot also supplies a stable preparation binding. The same single-package consumer
uses `CaptureSession.Prepare(candidate)` for heterogeneous roots, private inheritance segments and
string objects without selecting DTO types or calling their body helpers. Independent Base and Delta
goldens verify frozen content, repeated preparation, a nonempty no-change bitmap, equal-but-distinct
string replacement, retirement, and content surviving Discard. The script requires the additional
`CapturePreparation:True` marker. This prepares in-memory candidates; it does not establish a
persistent Parent baseline, append a StateRevision, or publish a branch head.

Generated `RegisterReaders` also accepts a small consumer-owned `IStateReaderRegistration` sink
from Runtime alone. Two calls supply the same stable reader binding and its exact generated Schema;
the script requires `GeneratedReaders:True`. This verifies the generator's registration seam
without adding a StateStore package dependency or assigning registry policy to the sink.

The Runtime-only consumer also registers the stable generated `Model` through a consumer-owned
`IStateModelRegistration` sink, normalizes a captured row to the current-version DTO, then calls generated `Allocate`
and `Hydrate`. Its private readonly base fields survive; neither the leaf/base constructors nor
the transient field initializer executes. The script additionally requires `GeneratedModel:True`
and `ReadonlyRestore:True`. This verifies generated restoration helpers without adding StateStore
to the Runtime package or implementing a consumer-owned persistence coordinator.

The final consumer also captures two concrete roots with shared strings, distinct equal strings,
null/empty/surrogate content, and private base fields through generated AddRoot adapters. It checks
the ID-closed candidate DTO directory, static ID-body golden bytes, mutation isolation, accept/discard, stable IDs for
instances retained across accepted candidates, discarded-number consumption, and fresh IDs after retirement. These use public runtime seams
from the single package reference; generated helpers and DTOs stay internal to the consumer.
This remains an in-memory candidate witness, not StateStore Save.

The string decoding witness then encodes the frozen DTOs and string objects into independent
owned bytes. Its typed loader accepts only bytes, roots and explicit metadata, preflights the
complete directory for unique nonzero IDs and exact Schema/body bindings, decodes all bodies with
full consumption, and validates every owner's generated string reference slots before returning.
It checks reference sharing across owners/base segments, equal but distinct strings, null/empty/
surrogate content, mutation after Seal but before encoding, and reversed object ordering. Missing
or wrong-kind references, cross-kind duplicate IDs, mismatched Schema, invalid roots and malformed
bodies must fail. The generated helpers call the public string APIs through the same single
PackageReference, with no friend access. This typed byte bundle is not a persistent format,
StateRevision, generic loader, or restored domain object graph.

Empty strings are the explicit exception to reference preservation: Capture normalizes all empty
instances to `string.Empty` and one ID per capture view; loading different IDs with empty bodies
returns that same singleton, while duplicate IDs remain invalid and ID zero remains null. The
consumer proves this through generated Capture with independently allocated empty test inputs and
then byte-only loading. Public `Replace` calls supply those test inputs with explicit identity
assertions; product behavior has no dependency on that allocation behavior or private runtime hooks.

## EventHistory publication and retained StateStore regressions

The [record class consumer](RecordClassConsumer/README.md) exercises DB-068 through three real
package builds: an external generic ordinary-class hierarchy becomes a same-Schema record hierarchy,
then explicitly upgrades a changed field. It covers pending Event recovery, event-only registration,
record identity and `with`, retained immutable history, Base rewrite and subsequent Delta.
Run `./experiments/PackageConsumerProbe/Run-RecordClassProbe.ps1`, optionally with a matching
`-PackageSource <feed> -Version <version>`.

For application onboarding, start with the [snapshot/recovery consumer](EventHistoryRecoveryConsumer/README.md):
default policy calls, private readonly snapshot contents, event-only reading, and the same application-selected Event
control function exercised by the internal failure regressions. Its runner also checks the packaged and
restored StateStore XML documentation. The root README itself has a separate extraction-and-run witness:

```powershell
./experiments/PackageConsumerProbe/Run-EventHistoryRecoveryProbe.ps1
./experiments/PackageConsumerProbe/Run-ReadmeQuickStartProbe.ps1 -PackageSource <matching-feed> -Version <version>
```

The README witness extracts the actual project, models, program, browsing, Upgrade and policy-override blocks.
It runs two V1 processes, then the documented V2 edits, checks retained history, executes the explicit
save-policy example against the resumed world, and builds again in Verify mode.
Use the matching nine-package feed printed by the recovery runner, or another fresh feed from the same source.

```powershell
./experiments/PackageConsumerProbe/Run-EventHistoryProbe.ps1
./experiments/PackageConsumerProbe/Run-StateStoreProbe.ps1
```

The [EventHistory consumer](EventHistoryConsumer/README.md) is DB-063's two-process, two-generation
public facade witness. It publishes S0/Event/State/pending Event, independently browses an Event
with no World/Bob model registrations, reads a pair of selected graphs, checks out State and explicitly reads the Event,
upgrades the old State, and continues through required Base, NoChange and ordinary Delta saves.
Root replacement is checked separately. Readonly browsing preserves every file's bytes and mtime.
DB-077 fixes the model registry at each Open; Event-only and complete catalogs use separate opens.
The consumer forces Family definitions (`DurableGraphGenerateDefinitions=true`) and registers the
generated Family definitions explicitly before opening, so package acceptance covers that path too.
DB-078-B extends this lane with independent Event/State Checkpoints, stable edited getters,
Event-first predecessor nullability and fixed event queries across States, commits and branch moves.
Recovery and RecordClass use EnumerateEvents with explicit oldest-first ordering; the recovery runner
also checks the new Checkpoint/query XML documentation in the actual restored package.

StateStore and all model/history consumer lanes now use `Repository` and
`BranchCheckout`. The existing model probes use a deliberate alternating application fixture; the facade also supports Event-first and free E/E/S/S histories. `CreateBranch` publishes S0 and preserves the original instances;
subsequent model-regression steps explicitly publish an Event snapshot followed by State.
Those regression Events may use the World itself as the snapshot root; the focused EventHistory
lane instead uses `Observed(Alice)` to verify independent event membership and capabilities.
They intentionally mutate caller-created aliases after capture to test frozen persistence; these are
mechanism tests, not examples of keeping a hot Event's reachable CLR contents read-only.
Journal refs provide the sole publication point; no `publication.rbf` is created.

The StateStore runner retains the inherited Character's exact DTO/Base/Delta golden checks,
Schema registration idempotence/conflict checks, and shared strings. Its V1/V2 World lane retains
real generated history, explicit Upgrade, constructor-free private readonly hydration, required
Base rewrite followed by NoChange and Delta, and cold historical reading. The graph lane retains
shared derived targets, cycles, child-only Delta, unreachable-cycle removal, continuous instance
identity and historical snapshots. Raw `RevisionDecoder` and Storage reads remain diagnostic
assertions; consumers no longer own the save pipeline or call internal preparation APIs.

The local feed contains **nine** packages, including the unmodified sibling EventJournal substrate.
Each consumer references `Atelia.DurableGraph` for generator/build assets and
`Atelia.DurableGraph.Persistence` for the facade, without manual analyzer/import/project wiring.
Most runners accept `-PackageSource <feed> -Version <version>` to reuse a matching feed. Accepted
history hashes remain immutable across consumer builds. Frozen-content probes now save through
the facade and then mutate/clear their source objects before reading the saved graph; staging and
publication-failure isolation remain the responsibility of product integration tests.

## Guid, decimal and TimeSpan values

Run `./experiments/PackageConsumerProbe/Run-BclScalarProbe.ps1` for the
[BCL scalar consumer](BclScalarConsumer/README.md). Its two package builds cover direct and generic
scalar state, Nullable and containers, exact decimal scale/signed zero, actual decimal key Remove/Add,
deleted inline CLR history, explicit owner/value Upgrade and Base-to-Delta continuation. New history
uses v9; accepted names, hashes and bytes remain immutable. The runner also accepts a matching feed
through `-PackageSource <feed> -Version <version>`.

## DateOnly, TimeOnly and DateTimeOffset values

Run `./experiments/PackageConsumerProbe/Run-TemporalScalarProbe.ps1` for the
[temporal scalar consumer](TemporalScalarConsumer/README.md). Two package builds verify scalar
composition, exact offset changes at the same instant, Dictionary key Remove/Add, deleted inline
CLR history and explicit owner/shared List value upgrades. Upgraded objects rewrite Base, then
resume NoChange and ordinary Delta with no repeated business calls after cold reopen. New history
uses v9 while accepted filenames, hashes and bytes remain unchanged. A matching nine-package
feed can be reused with `-PackageSource <feed> -Version <version>`.

## Historical capability retention

```powershell
./experiments/PackageConsumerProbe/Run-HistoryCapabilityProbe.ps1
```

This independent script builds four real package consumers: V1 writes a World and a cyclic Legacy
object with Base/Delta history; V2 without Upgrade methods proves exact DTO decoding works while
editable Load fails; V2 with explicit upgrades removes the World edge and retains an abstract
migration shell, validates even the orphan's references, then writes a required World Base and
Legacy Remove. V3 removes the Legacy CLR class entirely: the migrated Revision loads, the old
Revision fails for lack of an exact reader despite its persisted Schema and retained history files.
Existing history hashes must remain unchanged. The witness freezes no automatic retired-type API.

An application that still promises to load older Revisions must retain their reader and normalization
capabilities. Removing a family from a newer Revision does not make its older versions independently
recoverable from metadata. `-PackageSource <feed> -Version <version>` can reuse an existing nine-package
feed; the default invocation packs its own isolated dependency closure.


## Inline struct history and retained value layouts

```powershell
./experiments/PackageConsumerProbe/Run-InlineStructProbe.ps1
```

This DB-037 regression builds four actual package consumers using the same feed and immutable
history directory. V1 saves an inherited World with two levels of readonly inline values, shared
and distinct-equal string references, a shared child and a World self-reference. A nested leaf edit
produces one owner Delta; the inline values never acquire object rows. Reopening restores private
readonly fields and defaults transient state without running value constructors.

V2 changes the inner value's numeric layout and explicitly advances the outer value, base owner
and derived World Schemas. The owner Upgrade constructs nested historical DTOs through target-typed
constructors. BranchCheckout rewrites upgraded objects as Base, then compares unchanged while retaining
the same domain instances. A separate rebuild advances only the nominal child's version: owner and
inline Schema versions remain unchanged, and the next save writes only a required child Base.

V3 removes both inline domain struct declarations and their containing persistent field. Its retained
V1-to-V2-to-V3 owner Upgrade chain still compiles, exact-reads the original Base/Delta revision, and
loads that old revision into the current model. The published current head removes the now-unreachable
child and strings and reopens normally. Shared historical value DTO/body helpers survive independently
of the deleted domain CLR names; this does not provide readers for deleted reference-object families.

The script requires all stage markers and history counts `5 -> 10 -> 11 -> 13`, rejects deletion or
modification of previously published `.dgschema` files, and checks that new history uses text format v9.
The unchanged score uses a five-byte integer value so the nested leaf change remains a real Delta
candidate after Base v4 shortened the type header; the ordinary B/D policy is unchanged.
It packs the same nine-package dependency closure by default; `-PackageSource <feed> -Version <version>`
reuses an existing feed. Generated artifacts and probe-owned address sidecars remain under the unique
ignored `obj` run directory.

## Generic definitions and owner upgrades

```powershell
./experiments/PackageConsumerProbe/Run-GenericProbe.ps1
```

The [generic consumer](GenericConsumer/README.md) exercises the DB-038 product path through real
runtime and StateStore packages. It builds successive application versions in separate processes,
registers `Generated.DurableDefinitions` before Open, and lets the repository's model snapshot close the encountered
`Box<int>`, `Box<Point>`, and inline `Pair<LegacyPoint>` bindings. Historical state DTOs live in
generated Family hosts, independently of current domain CLR declarations.

The stages cover actual Base/Delta persistence, stored-exact decoding, generic pass-through and
closed business upgrades, forced Base after Upgrade, stable resave, and deletion of an old inline
domain struct. A missing closed conversion leaves exact decoding available while editable loading
fails before that object's first business callback. The consumer also checks that UpgradeContext
identifies the actual object and each adjacent owner edge; it does not exercise DB-039 value tools.

The runner preserves accepted history hashes and requires v9 for new history. It packs an isolated
dependency feed by default, or reuses one through `-PackageSource <feed> -Version <version>`.
Run instructions, stage details and limitations live in the consumer README; the implementation
record is [DB-038 §12](../../docs/design-branches/0038-generic-schema-state-and-binding-design.md#12-产品施工跟踪).

## Composable value upgrades

```powershell
./experiments/PackageConsumerProbe/Run-ValueUpgradeProbe.ps1
```

The separate [value-upgrade consumer](ValueUpgradeConsumer/README.md) exercises the DB-039
Context tools through actual runtime and StateStore packages. One open Box owner requests an
explicitly declared value dependency: Box<int> uses the opted-in KeepExact rule, Box<Point> invokes
the Point conversion, and Box<Pair<Point>> composes the open Pair provider with that same leaf rule.
Box and Pair deliberately reuse the local key `value` to check their independent Context scopes.
Nested callbacks check the current ObjectId and complete adjacent owner Schema endpoints.

The runner builds three application versions plus a V2 missing-rule variant. That negative build
retains exact decoding but rejects the affected owner's editable Load before its first business
callback, and the runner checks that repository files and hashes remain unchanged. V3 removes an
old inline domain struct while retaining its generated history DTO and value provider. The fixture
checks multi-step loading from V1, forced Base, unchanged resave, ordinary Delta, and cold reopening.
It preserves accepted history hashes across builds; it does not change any persistent format.

The runner packs an isolated nine-package feed by default; use `-PackageSource <feed> -Version <version>`
to reuse a matching feed. Exact stage markers and boundaries live in the consumer README;
construction and acceptance evidence are recorded in
[DB-039 §8](../../docs/design-branches/0039-composable-value-upgrade-design.md#8-产品施工合同与验收映射).

## Composable arrays and array-owned upgrades

```powershell
./experiments/PackageConsumerProbe/Run-ArrayProbe.ps1
```

The [array consumer](ArrayConsumer/README.md) uses two real package builds to exercise all four
supported ranks, jagged sharing, generic array operands and generic struct elements, and a cycle
through a World array. A historical inline element Upgrade runs once per shared array, forces one
Base rewrite, then resumes ordinary Delta saving on the same domain instances. The old Revision
still decodes its exact old element DTO. The runner publishes and verifies immutable history v9.

The array consumer also covers DB-045's repository-local representation IDs: new Base v4 headers
contain only the ID, reopening preserves its layout, and array element Upgrade changes the array's
representation while the referencing owner's representation remains stable. The descriptor and its
exact Schema dependencies live in `schemas.rbf`; generated DTO and body contracts are unchanged.

## BCL List content and list-owned upgrades

```powershell
./experiments/PackageConsumerProbe/Run-ListProbe.ps1
```

The [list consumer](ListConsumer/README.md) exercises the DB-047 content object through two real
package builds. Nested lists, arrays in lists and lists in arrays, generic class/struct operands,
shared identity and World/list cycles survive continuous saves and cold reopening. Element edits,
append and middle/tail deletion preserve the List ObjectId; Capacity-only changes write no objects.
Saved Base/Delta content survives clearing the source lists before reading it back.

The next build removes the old inline domain CLR declaration while retaining its versioned state
history. Explicit list-owned value Upgrade runs once for the shared list, forces Base and then
resumes ordinary Delta. Representation IDs remain persistent, with a new ID only for the upgraded
list layout. The runner requires stage markers, immutable history hashes and new history v9;
it accepts `-PackageSource <feed> -Version <version>` to reuse the existing nine-package feed.
The consumer uses DB-049's List range codec 2 and DB-051's default Adaptive writer. Algorithm comparisons
and same-history measurements live in [ListDeltaReplayProbe](../ListDeltaReplayProbe/README.md); this lane verifies package delivery.

## Nullable values and lifted value upgrades

Run `./experiments/PackageConsumerProbe/Run-NullableProbe.ps1` for the
[Nullable consumer](NullableConsumer/README.md). The two-stage witness uses nullable struct fields,
a shared List, vector and rank-four array, retaining reference sharing and cycles inside present values.
The second build deletes the old struct CLR declaration while retaining exact history, explicitly lifts
the child conversion, rewrites upgraded owners as Base and resumes Delta. Clearing nullable values
removes the unreachable cyclic island. History v9 hashes and counts are checked in packaged Publish
and Verify modes; the runner also accepts an existing matching nine-package feed.

## Immutable leaf proof capability

Run `./experiments/PackageConsumerProbe/Run-ImmutableLeafProbe.ps1` for the
[immutable leaf consumer](ImmutableLeafConsumer/README.md). Its two package builds verify the DB-075
structural proof through real package delivery. A plain consumer forces Family definitions and requires
its non-generic readonly leaf binding to resolve with internal `IsImmutableLeaf=true` (reflection only)
before round-tripping the leaf value through `Repository`; a second consumer compiles the
identical model with a local sibling source generator wired as an analyzer in the same compiler pass,
and the same model must then resolve with `IsImmutableLeaf=false` because the sibling's added mutable
state is visible to the structural proof but not to durable classification. Pass
`-PackageSource <feed> -Version <version>` to reuse a matching nine-package feed.
