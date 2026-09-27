# Atelia.DurableGraph

DurableGraph is an exploratory .NET 10 prototype for versioned durable object graphs.
Its public API, Schema-history format, and build workflow are not stable yet.

A direct package reference supplies the runtime library, Source Generator, and Schema-history
build integration. Each successful local `CoreCompile` publishes Schema definition templates under
`DurableGraphSchemaHistory/`; `ContinuousIntegrationBuild=true` verifies that the current records
already exist without writing them. Configure this with `DurableGraphSchemaHistoryDirectory` and
`DurableGraphSchemaHistoryMode` (`Publish`, `Verify`, or `Off`). Keep `.dgschema` files under source
control. `Off` disables Publish/Verify but does not make legacy history valid. The removed
`DurableGraphSnapshotHistoryDirectory` property, the old default `DurableGraphSnapshots/` directory,
and legacy `.dgsnapshot` files are rejected rather than silently ignored or consumed.

Domain attributes, `IDurableObject`, `ObjectId`, upgrade context and registration interfaces stay in
`Atelia.DurableGraph`. Schema/layout descriptors use `Atelia.DurableGraph.Schema`; bindings, capture,
state operations and container readers use `Atelia.DurableGraph.Runtime`, in the same assembly.
Persistence sessions come from `Atelia.DurableGraph.Persistence`; shared bytes and raw revisions use
`Atelia.DurableGraph.Serialization` and `Atelia.DurableGraph.Storage`. Recompile the dependency closure
when migrating the former StateStore package names. Keep model IDs, versions and accepted history.

## Generated Schema and state

Annotate each durable partial class or struct with `[DurableType("example.character", 1)]`; a
durable class implements `IDurableObject`, directly or through its domain base, while structs have no object identity.
Top-level generic classes and structs are supported, including readonly structs, generic base
classes, and private/readonly fields. The Generator emits exact Schema/history,
readonly versioned state DTOs, raw Base/Delta body operations,
Capture, reader/model registration, Normalize, Allocate, and Hydrate. Unsupported type shapes or
field types are diagnostics rather than a metadata-only fallback.

`TypeExpr.Named(definitionId, arguments)` identifies a closed durable family; `Box<int>` and
`Box<Point>` are distinct families sharing a definition ID. A Schema key combines this closed
identity with the declaration's explicit version. Complete base/inline layouts remain part of
`DurableSchema`, and equal keys require equal complete definitions in the destination SchemaStore.
The `.dgschema` templates preserve declaration arity, scoped parameters and fixed dependency
versions; they do not enumerate all closed combinations. Missing a generic owner version bump can
therefore produce different first layouts in independent empty repositories. Existing conflicting
definitions are rejected; a key alone is not a cross-repository equivalence guarantee.

Supported scalars include bool, byte/sbyte, short/ushort, int/uint, long/ulong, char, Half, float,
double, Guid, decimal, TimeSpan, DateOnly, TimeOnly and DateTimeOffset. Supported compositions include
string and durable class references, inline durable structs/record structs/enums, Nullable values,
zero-based SZ/rank 2–4 arrays, exact BCL List and Dictionary content objects. DateTime, ValueTuple,
boxed identity, CLR nested types, arbitrary external bases, ref structs, array covariance and NativeAOT
guarantees remain outside the supported contract. CLR generic constraints
are retained and enforced; `allows ref struct` is rejected, and a constraint does not make an
otherwise unsupported closed value serializable.

Record classes support positional and body fields, generic declarations and durable record inheritance.
Use `[field: DurableField(id)]` or `[field: Transient]` on actual property backing storage; inherited
positional properties are stored by their declaring base only. Hydration bypasses constructors,
initializers and accessors. C# equality and `with` retain their normal behavior; neither supplies
deep immutability or collection-content equality. Persistence continues to distinguish reference identity.

`DurableBase` has been removed. When upgrading, change direct model bases to `IDurableObject`,
change `where T : DurableBase` to `where T : class, IDurableObject`, and update erased API variables
and callbacks. Recompile affected model libraries and hosts together. The marker adds no Schema
or data; removing the old empty base alone requires no Schema version bump. Every domain ancestor
still needs its durable declaration/history; arbitrary third-party bases are not admitted.

### Definition registration and generic state hosts

A compilation containing generic durable declarations/history, explicit `[DurableUpgrade]`
registrations, record structs/enums, Nullable compositions, or value-upgrade declarations uses the
generated Family surface for all its durable declarations. A model library can also explicitly
request this surface with `<DurableGraphGenerateDefinitions>true</DurableGraphGenerateDefinitions>`.
Unset or false retains automatic selection; false does not disable an otherwise required Family path.
Register the
definition factories once; the repository's frozen model environment closes actual supported types as needed:

```csharp
var models = new StateModelRegistry();
Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);

using var repository = Repository.CreateNew(repositoryPath, models);
using var session = repository.CreateBranch(
    "main", world, new ReadAmplificationBaseBudgetParameters(3, 5));
var initialStateFrame = session.Head; // S0 is already published; world retains its identity.
using var child = repository.Fork("candidate", initialStateFrame);
// session stays open; child owns a separately restored mutable State and saving baseline.
```

These storage APIs require `Atelia.DurableGraph.Persistence` in addition to the runtime package.
The [repository quickstart](../../README.md) provides the complete Event/State commit, reopen and
application recovery example. Event and succeeding State both compare against the preceding State;
committing an Event does not advance the State baseline.
For stored-exact decoding, register the same generated definitions into a `StateReaderRegistry`.
Repository Open freezes its definition/model directory; later registration affects future opens only.
Current bindings use actual closed CLR types, while historical readers bind retained templates
against the complete stored Schema. The framework does not discover models by assembly scanning.

Historical DTOs are named `Atelia.DurableGraph.Generated.Family_<UTF8HexDefinitionId>.Vn<TState…>`.
The host itself is non-generic and is independent of the current domain type. DTO type parameters
represent only unresolved state values: references use ObjectId, structs use exact nested DTOs,
and unused nominal parameters need not produce DTO parameters. The generated execution templates
separately use `IStateOps<TState>` and `IValueProjection<TDomain,TState>` static helpers. Their CLR
types are derived execution metadata, not persisted type identities; known fields still call their
operations directly. Resolve a closed Schema through its reader/model binding rather than assuming
a generic DTO CLR type identifies the entire Schema or slot semantics.

### Composing model libraries

Each model project references `Atelia.DurableGraph` directly to receive the generator and build
assets, owns its `.dgschema` history, and exposes a normal public registration facade:

```csharp
public static class DomainCatalog {
    public static void Register(IStateModelRegistration models) {
        Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
    }
    public static void RegisterReaders(IStateReaderRegistration readers) {
        Atelia.DurableGraph.Generated.DurableDefinitions.Register(readers);
    }
}
```

The generated `DurableDefinitions` aggregator is internal to that assembly. The application calls
each library's facade before creating or loading a session, rather than resolving generated type
names across assemblies. `IStateModelRegistration` also accepts definitions, so the public facade
signature can remain stable when a library switches from ordinary model registration to Family
generation. Existing ordinary class libraries can wrap their local `__DurableState.RegisterModel`
entry instead. Family/DTO/Definition types keep their existing visibility.
For public stored-exact decoding, register each library's definitions/readers through its
`RegisterReaders` facade into `StateReaderRegistry`; a reader's nominal evidence does not grant
the ability to decode another missing historical version.

An application can reference external durable classes or use external durable values through
arrays, List, Dictionary and generic representation parameters. For example, `LocalBox<RemotePoint>`,
`RemoteBox<LocalPoint>`, and a local `InlineBox<T>` closed over `RemotePoint` use registered factories
without importing the dependency's generated DTO names. A plain struct library can enable
`DurableGraphGenerateDefinitions` to export the required current/historical value factories.

Direct fixed external values such as `RemotePoint`, `RemotePoint?`, and `RemotePair<LocalPoint>`
use the providing library's generated inline-history exports. A library using the Family surface
exports its own retained declaration templates automatically; a plain struct library enables
`DurableGraphGenerateDefinitions` as shown above. The consumer selects the Family surface when it
needs these fixed dependencies, including dependencies retained only in old history.
Public values may encapsulate private fields of internal durable value types: the library's
projection handles those fields, while its public state helpers provide the historical layout.

The generator reads the required exports through compiler metadata, including transitive base/inline
dependencies. Each library continues to own and publish only its own `.dgschema` files. Do not copy
dependency history into the consumer's directory. The build targets pass a generated read-only
reference manifest to Publish/Verify; it is temporary build input, not another history directory.
Missing exports, required old versions or conflicting definition ownership fail explicitly.
Exports describe build capabilities; the host still registers each library through its facade.

Local durable classes may derive from an external public top-level durable base, including abstract
and generic bases. The providing library uses the Family surface; fixed external base dependencies
automatically select that surface in the consumer. Each class projects its own fields and delegates
its complete base state to a bound `StateBaseProjection<TBase,TState>`. Private/readonly fields and
internal value implementations remain inside the defining library; consumers only combine public DTOs.
The complete leaf DTO and Base/Delta body keep their existing flattened declaration segments.

Base projection does not allocate or register another object and does not run a base Upgrade.
The actual object's leaf owner still converts the complete historical DTO; base layout changes require
affected derived versions and explicit upgrades, even when their own fields did not change.
Handwritten model bindings must opt in with `supportsBaseProjection: true` only when their Capture/Hydrate
callbacks support compatible derived instances. Whole-object entry points still require exact CLR types.
Generated inline exports use execution contract 1; reference-object exports use contract 2, independently
of the canonical v9 template format. No dependency history is copied into the consumer.

Existing CLR shapes, generic constraints, reference and comparer restrictions still apply. Model identity is the durable
definition ID, not an assembly name; two libraries cannot independently claim the same identity.

Each referenced object retains its own Schema version. Updating its library does not change a
nominal-only owner Schema, but preserving an already compiled consumer also requires compatible
public CLR types and methods. Dynamic inline dependencies continue to require explicit owner
version changes and Upgrade when their complete layout changes. Missing registration or retained
history fails explicitly; the current implementation does not substitute for a historical reader.
Changing generation mode may require updating library-internal DTO/helper references; it does not
change persistent Schema identity, history or body encoding.

For a fixed external inline field, changing the value's exact layout requires bumping the owner
version and writing its explicit Upgrade, just as for a local inline value. Rebuild affected owners;
the nominal-only DLL replacement guarantee does not extend to arbitrary inline layout changes.
Old inline CLR declarations may be removed while the providing library retains their generated
history DTO/body and the application retains the necessary upgrade path.

See the [cross-assembly consumer](../../experiments/PackageConsumerProbe/CrossAssemblyConsumer/README.md)
for independent model packages, public facades, two-generation history and an unchanged consumer DLL.
The [inline library consumer](../../experiments/PackageConsumerProbe/InlineLibraryConsumer/README.md)
demonstrates direct values across three model libraries, independent history and explicit upgrades.
The [inheritance library consumer](../../experiments/PackageConsumerProbe/InheritanceLibraryConsumer/README.md)
demonstrates two external base edges, hidden field implementations, deleted historical CLR names and leaf-only upgrades.

Use a normal C# alias for readable historical types. For a declaration with ID `Box`, whose V2 adds
an integer field after the retained value, a generic adjacent conversion is:

```csharp
using BoxStates = Atelia.DurableGraph.Generated.Family_426F78;

internal static class Upgrades {
    [DurableUpgrade(typeof(Box<>), 1)]
    internal static void Upgrade<TState>(
        in BoxStates.V1<TState> oldValue,
        out BoxStates.V2<TState> newValue,
        UpgradeContext context) where TState : unmanaged {
        newValue = new(oldValue.Segment0Field1, 0);
    }
}
```

The method must be public or internal on a top-level non-generic static CLR host. The attribute selects the owner and the
adjacent edge (`1` means V1 to V2); a closed owner such as `typeof(Box<Point>)` can register an
explicit business conversion between different old/new state types. A selected closed provider has
priority and does not fall back on failure. All required adjacent steps for that object bind before
its first business callback. Missing or ambiguous intermediate exact layouts require explicit
historical endpoints; the current value version is not substituted for an unknown old one.

`UpgradeContext` provides the current ObjectId and exact SourceObjectSchema/TargetObjectSchema for
that adjacent edge, plus any declared value tools described below. Do not retain it or its tools
after the synchronous call. It exposes no object-graph reads or ID allocation. Business
code explicitly converts inline values. When such a dependency advances, the owner definition must
also advance, including closures such as `Box<int>` whose physical fields did not change.

For a complete package consumer, see the
[generic history probe](../../experiments/PackageConsumerProbe/GenericConsumer/README.md).
The implementation record is [DB-038 §12](../../docs/design-branches/0038-generic-schema-state-and-binding-design.md#12-产品施工跟踪).

### Composable value upgrades

An owner can reuse a value conversion across its closed generic families. For the same `Box` history
above, replace the pass-through method with an explicit dependency and a two-state-parameter method:

```csharp
using BoxStates = Atelia.DurableGraph.Generated.Family_426F78;
using PointStates = Atelia.DurableGraph.Generated.Family_506F696E74;

[ValueUpgradeRuleSet(AllowKeepExact = true)]
internal sealed class Coordinates { }

internal static class Upgrades {
    [DurableUpgrade(typeof(Box<>), 1)]
    [UpgradeDependency("value", typeof(Coordinates), "Box", 1, "Box", 1)]
    internal static void UpgradeBox<A, B>(
        in BoxStates.V1<A> oldValue, out BoxStates.V2<B> newValue,
        UpgradeContext context) where A : unmanaged where B : unmanaged {
        var convert = context.GetValueUpgrade<A, B>("value");
        newValue = new(convert(in oldValue.Segment0Field1), 0);
    }

    [DurableValueUpgrade(typeof(Coordinates), "Point", 1, 2)]
    internal static void UpgradePoint(
        in PointStates.V1 oldValue, out PointStates.V2 newValue,
        UpgradeContext context) {
        newValue = new(oldValue.Segment0Field1 * 1000L);
    }
}
```

This example assumes `Point` V1 has one int field and V2 has one long field. The rule-set marker is
an accessible top-level non-generic class in the same compilation. Value methods use the same accessible top-level static
host and three-parameter shape as explicit owner methods. The string definition ID selects retained
inline history, so a value provider can continue referring to historical state DTOs after the old
domain struct is deleted, even when no current durable declarations remain. The Generator supports same-inline-family endpoints, including an open
generic `Pair` provider that declares and calls its own element dependency. It does not choose a
business rule or automatically walk inline fields.

Each dependency names a source and target **declaration ID plus FieldId**. This selects the correct
inheritance segment even when base and derived declarations both use FieldId 1. Keys are ordinal,
case-sensitive and local to the provider: a Pair tool cannot obtain a Box dependency by using the
same name. The Generator emits `Generated.UpgradeSlots_<UTF8HostHex>_<UTF8MethodHex>` string constants
and `Generated.ValueUpgradeRules_<UTF8MarkerHex>.Rules`; host and marker encodings include their
namespace. Normal using aliases can shorten these generated names. Attribute keys must be C#
identifiers (keywords are escaped); the string lookup API accepts the same key directly.
`Generated.DurableDefinitions.Register(models)` registers the generated rules together with the
definitions. Runtime-only registration can instead supply immutable `StateValueUpgradeRuleSet`
and `StateValueUpgradeProvider` metadata explicitly; this lower-level API also represents builtin,
reference and closed nominal patterns with full expected slot semantics. Those broader patterns
are not additional `DurableValueUpgrade` attribute forms.

`AllowKeepExact` is opt-in: it permits `Box<int>` to preserve its value only when there are no
explicit candidates and the complete source/target slot semantics agree. It cannot conflate a
numeric uint, string ID and durable-object ID, or hide an invalid explicit candidate. Multiple
matching candidates are an error. All declared tools for every adjacent owner step bind before
that object's first business callback, including tools unused by a particular data branch.
`GetValueUpgrade` only retrieves those bindings; an unknown key or wrong requested state type fails
when the call executes. Framework prebinding does not inspect arbitrary C# method bodies.

A nested value call receives its own dependency table and the same owner ObjectId and exact
adjacent object Schemas. Cached plans contain no invocation Context or captured per-object tools.
These synchronous tools do not search version paths, repair missing historical layouts, read
other objects, allocate IDs, or alter persistence formats. Legacy two-parameter owner methods must
add `UpgradeContext` before declaring dependencies. The construction contract and acceptance
evidence are recorded in [DB-039 §8](../../docs/design-branches/0039-composable-value-upgrade-design.md#8-产品施工合同与验收映射).

### Retained non-generic generated helpers

Compilations that neither select Family automatically nor request `DurableGraphGenerateDefinitions`
retain the established generated API below. Introducing the Family path above changes generated names for the whole
compilation; update direct DTO/helper references to Family aliases and definition registration.
Existing non-generic two-parameter Upgrade methods can still be adapted to the common invocation
contract, while new generic and explicitly registered providers use the three-parameter form.

The generated `Schema` property is the current exact Schema. `GetSchema(int version)` returns cached
metadata for versions 1 through the current version, including each version's exact historical base
chain; other versions throw `ArgumentOutOfRangeException`. Each declaration owns its own FieldId
space. `Schema.Fields` describes that declaration while `Schema.BaseSchema` binds its exact immutable
ancestor layout.

The assembly-internal nested `__DurableState` contains readonly structs `V1` through the current
version. Each DTO's static `Schema` property is the same cached exact definition returned by
`GetSchema(n)`. DTOs are regenerated from accepted `.dgschema` history and the current definition;
there is no separate DTO source history to maintain. Current and historical layouts support bool,
byte/sbyte, short/ushort, int/uint, long/ulong, char, Half, float, double, string, and supported
durable references. Reference fields become `ObjectId` slots (UInt32 on the wire) while their Schema type retains
String or its nominal durable target.

Each DTO physically flattens the exact ancestor chain into fields such as `Segment0Field1`: base
declarations first, then each declaration's fields in FieldId order. Schema metadata remains
segmented. Historical DTOs do not depend on old CLR base definitions remaining in source.

Inline structs are explicitly marked with their own `[DurableType("example.position", 1)]` on a
top-level `partial struct` (including `readonly partial struct`). They do not need
IDurableObject; implementing that marker does not grant them object identity. Each has Schema/history even if no class currently uses it, and every instance
field remains explicitly durable or transient. Supported scalar, string, durable-reference and
nested struct fields are recursively projected into unmanaged readonly DTOs; references become IDs.
The struct has no separate ObjectId, object row, root registration or StateModelBinding.

`SchemaKind.InlineValue` distinguishes these layouts from `ReferenceObject` definitions.
`DurableFieldInfo.InlineSchema` binds the exact nested value layout. A value version change requires
explicit owner version increases along inline/base dependencies; nominal target versions do not
propagate. A Schema family cannot switch kind. Shared generated value DTO/body helpers use exact
Schema identity independently of the current domain struct declaration. Removing an old struct
does not remove a retained owner's historical inline layout or require a value migration shell.
Owner upgrades explicitly construct nested DTOs, including target-typed `new`; the framework does
not run a second automatic struct upgrade chain.

Base and fused Delta bodies compose through static nested calls. A changed composite Delta slot
contains a nonempty child Delta, without another Schema header or length. Struct ref Hydrate starts
with a default temporary and fills private/readonly fields without running constructors or
initializers; Transient fields remain default. Field and array-element slots can use this same
helper; supported arrays also use independent content-object bindings. Current Capture/Hydrate bridges keep
domain and state types separate; the Family path uses that separation for generic composition.

Build-time Schema history and runtime SchemaStore batches have separate formats. Already accepted
history files are not rewritten merely because the writer advances. Object Base headers reference a
repository-local integer RepresentationId, resolved through SchemaStore to the complete exact layout;
same-Schema Deltas still use the Base's type information.

For a scalar-only layout:

```csharp
var state = Character.__DurableState.Capture(character);
var prepared = Character.__DurableState.PrepareBaseBody(in state);

var reader = new BinaryPayloadReader(prepared.Body);
var restoredState = Character.__DurableState.ReadBaseBodyV1(ref reader);
reader.EnsureFullyConsumed();
```

`WriteBaseBody(ref writer, in Vn)` writes a raw Base body. `PrepareBaseBody(in Vn)` owns those bytes
for reuse. `PrepareDeltaBody(in prior, in current)` owns its change decision and raw Delta body;
`ApplyDeltaBodyVn` applies it to the exact same-version prior DTO. The public containers are
`PreparedBaseBody` and `PreparedDeltaBody`, and expose immutable bytes as `Body`. They contain
neither the Persistence Base type header nor the Storage ObjectVersion envelope.

The Serialization library is a transitive dependency. Its Reader/Writer constructors, scalar
operations, non-null string content codec, and boundary checks are public for generated-code
consumers. Char preserves a UTF-16 code unit; Half/float/double preserve their fixed-width bit
patterns. Failed reads return no partial DTO but may leave the Reader advanced; failed writes do
not roll back earlier output.

When a model advances, user code supplies any required adjacent single-object conversion:

```csharp
private static void UpgradeStateV1ToV2(
    in __DurableState.V1 oldValue,
    out __DurableState.V2 newValue) {
    newValue = new(oldValue.Segment0Field1, default);
}
```

Historical readers do not require an upgrade path, but loading an old DTO into the current editable
model does. Loading itself does not rewrite State; a surviving upgraded object is emitted as a new
Base by a later explicit Prepare.

## Capture and object-state rows

Layouts containing references capture through a `CaptureContext`. The retained non-generic
generated `AddRoot` supplies the exact model binding:

```csharp
var session = new CaptureSession();
using var capture = session.BeginCapture();
ObjectId rootId = Character.__DurableState.AddRoot(capture, character);
var candidate = capture.Seal();
session.Accept(candidate); // Or session.Discard(candidate).
```

Keep the domain graph stable from registration through Seal. Capture uses CLR reference identity,
allocates nonzero UInt32 ObjectIds, freezes DTO values, and lists the complete candidate rows in
ascending ID order. `ObjectStateRecord` is the common immutable carrier for a durable exact DTO or
string content; its enclosing `CapturedGraph`, `DecodedRevision`, or normalized view supplies the
stage meaning. Empty strings normalize to `string.Empty`; distinct nonempty string instances retain
distinct identity.

`CaptureSession.Accept` installs only an in-memory candidate and its bindings. It is not State
append, Commit, or publication. Discard, failure, and disposing an unresolved context preserve the
accepted graph, although allocated numeric IDs can remain consumed.

## Typed persistence path

Register generated models or definition factories with `StateModelRegistry`, then supply it when
creating or opening a `Repository`. That open instance freezes the model configuration.
`CreateBranch(name, initialState)` commits S0 and returns a non-generic `BranchCheckout` retaining
the supplied domain instances. `CreateBranchFromEvent(name, initialEvent)` commits E0 and returns
a checkout with no State. `CommitEvent` records an independent Event snapshot; `CommitState`
installs the frozen State candidate after publication. Either role may be committed consecutively.
`CommitState(nextState)` accepts any registered nonnull root, including a different CLR type;
the parameterless overload requires an existing State. The checkout owns the nearest State's
Revision, DTO baseline and instance-ID bindings; callers do not pass these separately.

All these save calls have an optional policy; an override applies to that call only, not to later
checkout defaults. The Persistence package includes XML documentation for the Repository facade.
The [snapshot/recovery example](../../experiments/PackageConsumerProbe/EventHistoryRecoveryConsumer/README.md)
uses an explicit application protocol to distinguish new commands from completing recorded work.
The library does not infer pending work or completion from E/S order. Publication outcome and
IsFaulted are independent; failed attempts do not undo caller mutations. Reopen faulted repositories
and inspect their actual history before deciding whether to retry.

`Checkout` retains the exact branch head and restores only its nearest State, or no graph for a
pure Event prefix. It does not materialize Events or replay business handlers. `ReadState` and
`ReadEvent` independently materialize a selected repository-issued `CheckpointAddress`.
`ReadCheckpoint(address)` eagerly returns an `EventCheckpoint` or `StateCheckpoint`, containing
the selected root and the nearest strict ancestor of the opposite role. PreviousState/PreviousEvent
and its address are both null only when no such ancestor exists. At most two graphs are restored;
they have independent mutable instances, and repeated root getters retain the same objects.
Changing a read result neither writes history nor advances a checkout. PreviousEvent is navigation,
not evidence of causality or application progress.

Experimental `ReadPair(first, second)` returns two
`IDurableObject` roots with their actual types preserved, in input order after both reads succeed.
Selections may have either State/Event kind and need not be adjacent. Use each input frame's `Kind`
for its role and pattern matching for its domain type.
Within one operation it reuses stored-exact decoding and may
share domain instances whose complete current reference closure is safe to share. It makes no
cross-graph sharing or separation guarantee: do not use cross-graph `ReferenceEquals` to infer
business identity or version, or to choose program behavior. Both complete graphs must remain
read-only, including observable Transient mutation on every reachable object; the library does not
freeze ordinary CLR instances. Keep each view's owner/context/index/cache outside the paired graphs,
not on potentially shared nodes. A global Actor-to-context table is insufficient when one Actor belongs
to both views. See the executable [external view example](../../experiments/PackageConsumerProbe/EventHistoryConsumer/README.md#per-view-transient-context).
For per-view in-place Transient initialization, use independent `ReadState`/`ReadEvent` calls. These work
without a writer; persistent members remain historical snapshots and no saving baseline is installed.
Use `Checkout` to continue editing and committing. Sharing comparison uses persistent-state equality
without preparing object Base/Delta payloads; ordinary read validation may still encode canonical
Dictionary keys. Independent restoration can reuse frozen
DTOs and strings but allocates separate mutable graphs. Upgrade and validation still
run independently for each view. See [DB-064](../../docs/design-branches/0064-shared-revision-decoding-design.md)
for the internal sharing boundary; there is no public cache configuration or new wire format.
`Fork(name, address)` publishes a named durable ref and returns an independent editable checkout,
restoring only the selected position's nearest State, or no graph for a pure Event prefix. The source
checkout may remain open; its uncommitted edits are excluded. Fork adds no new history checkpoint.
Each branch permits one active checkout; different branches may remain open together, but all
repository operations must be serialized. Explicitly Dispose a checkout before checking out that
branch again or moving its ref with an expected head. Other active branches do not block those calls.
The ref-only `CreateBranch(name, address)` does not restore a graph or occupy an editing slot.
Two children of the same Event do not claim work or guarantee exactly-once external effects;
application state and external protocols determine processing progress. A failed Fork may already
have published its ref: inspect publication outcome and IsFaulted, and reopen when required.
`EnumerateEvents(endInclusive, order, afterExclusive)` returns only Event addresses in a fixed
logical history, even if a branch later advances or moves. Default `HistoryOrder.NewestFirst`
supports early stopping without preparing the full chain. `OldestFirst` may buffer addresses;
an optional lower bound must be an ancestor or the same point and is validated before the first item.
Each MoveNext checks repository availability and releases its operation guard before returning, so
the loop body can call ReadEvent. No traversed State graph is materialized. `ReadFrames(branch)`
still returns the full oldest-first metadata chain; slicing that list does not reduce its cost.
Strict Open performs separate physical-history validation. CheckpointAddress values are valid only
in their issuing open repository, and their diagnostic addresses are not persistent bookmarks.

Graph materialization decodes the complete stored-exact directory, validates and upgrades its rows,
then allocates all reachable objects before hydrating references. Durable classes use
`RuntimeHelpers.GetUninitializedObject`; constructors, field initializers and Transient hooks do
not run. User code rebuilds Transient state after delivery on Checkout or independent reads; paired
views keep their context outside the graphs as described above. For explicit stored-exact DTO inspection,
register retained readers/definitions with `StateReaderRegistry` and use `RevisionDecoder`.
`LoadedWorld` and its prepare/load surface are internal mechanism helpers, not application APIs.

Journal branch refs are the sole publication authority. Schema/State data becomes durable before
the Journal graph frame, and the ref is published last; unreferenced appends do not advance a branch.
Read-only opening does not create, flush or repair files. Reopening is strict, without automatic tail
recovery or transparent retries after uncertain publication. See the
[quickstart and failure boundaries](../../README.md) for runnable calls and operational limits.

The Base type-header codec is internal to Persistence. Public generated bodies are raw;
`EncodedBaseObjectBody` brands the internal `[type header | raw Base body]` result so the typed
planner cannot omit or apply that header twice. Storage continues to accept opaque body bytes and
does not interpret this brand. Neither raw body access nor `StateRevisionStore.Append` constitutes
Commit or publication.

Changing an exact base binding requires an explicit version increase in the derived class and every
affected descendant. Accepted `.dgschema` history retains the old binding. Generator and publisher
validate complete, conflict-free ancestry. The Schema-history syntax is a build-time metadata format,
not the object-graph payload or runtime SchemaStore wire format.
