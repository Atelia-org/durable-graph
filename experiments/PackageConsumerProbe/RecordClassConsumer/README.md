# Record class package consumer

Run from the repository root:

```powershell
./experiments/PackageConsumerProbe/Run-RecordClassProbe.ps1
# Or reuse a matching feed containing all nine Runtime/StateStore packages:
./experiments/PackageConsumerProbe/Run-RecordClassProbe.ps1 -PackageSource <feed> -Version <version>
```

This [DB-068](../../../docs/design-branches/0068-record-class-model-slice.md) witness uses
`Host -> FactsLibrary -> BaseLibrary`, linked entirely by PackageReference. Both libraries
ship SDK-generated `ref/` and `lib/` assets. The runner checks the actual NuGet selection;
there are no ProjectReferences, manually installed analyzers, or reflection-based model binding.
Each model project owns and verifies only its own history. The sole reflection assertion checks
that the Runtime package no longer exports the old `DurableBase` type.

The current model is an ordinary `World : IDurableObject` holding positional records:

```csharp
[DurableType("RecordFact", 1)]
public abstract partial record Fact<T>([field: DurableField(1)] T Actor) : IDurableObject;

[DurableType("RecordDamage", 1)]
public sealed partial record Damage<T>(T Actor, [field: DurableField(1)] int Amount) : Fact<T>(Actor) {
    [DurableField(2)] public long Counter = 638_625_600_000_000_000;
    [DurableField(3)] public readonly long Stamp = 638_625_600_000_000_000;
}
```

`Actor` storage belongs to the external generic base. The derived parameter forwards it without
creating or classifying a second field. Each declaration segment can use FieldId 1 independently.
Libraries expose small handwritten registration facades over their generated catalogs; the Host
registers these explicitly, along with its own World catalog. Event-only reads intentionally omit World.
Each browsing process then opens a separate repository with the complete catalog for non-generic `ReadPair(E1, S0)`, pattern
matching the returned `IDurableObject` values as Damage and World and checking each root's historical
content. It makes no assumption about sharing domain instances across the two materialized graphs.

The runner builds three complete application generations in fresh intermediate directories:

1. Ordinary classes with explicit readonly fields and `IDurableObject` publish the V1 history,
   save S0, then E1 and exit with an Event awaiting application processing. Two equal-content objects and an
   alias are already present in S0.
2. The entire generic hierarchy changes to records **without changing Schema versions**.
   Exact history filenames and bytes stay unchanged. A separate process resumes E1, observes
   record equality plus distinct/shared reference identities, applies damage and publishes S1.
   Another process proves that completed work is not replayed and no persisted bytes change.
   A `with { }` copy is then saved alongside its still-reachable original; cold reopen preserves
   their distinct identities despite equal values. Read-only event-only browsing preserves files.
3. Damage.Amount widens from int to long and Damage advances to Schema v2. Its explicit generic
   Upgrade preserves each complete inherited DTO. The four retained Damage instances upgrade
   once each, require Base rewrites, then a mutable Counter edit produces a single Object Delta.
   A fresh process reopens the current state without repeating Upgrade. Historical E1 remains readable.

The ordinary World Schema and generic Fact Schema remain v1: the changed Damage layout belongs
to separate referenced objects. This does not permit skipping a required owner version when an
inline value or ancestor layout changes. The old framework-base-to-interface migration uses
the separate [old-package input witness](../Run-DurableBaseMigrationProbe.ps1); this runner starts with the new marker API.
Pass `-LegacyPackageSource`, `-LegacyVersion`, `-PackageSource` and `-Version` to that script.
It writes with an actual old DurableBase package, then restores and appends a Delta with the new marker
package while preserving Schema-history bytes. Both applications and their caches remain isolated.

These facts contain only scalar/string content. C# `with` is ordinarily a **shallow** copy, not
an immutable graph snapshot. The Counter field is deliberately mutable for the Delta regression;
application events should be treated as read-only after submission. A record containing a List or
array does not automatically compare its contents or freeze them. For collection-owning event
snapshots, use the existing [defensive-copy recovery example](../EventHistoryRecoveryConsumer/README.md):
copy input contents into privately owned storage and expose read-only access. Supply custom business
equality only when the domain requires it. The persistence layer uses reference identity and complete
persistent state independently of the record's business equality.

History Publish, Clean and Verify run at every generation. The accepted-history JSON, isolated feeds,
consumer binaries and database remain under a unique ignored `obj/record-class-*` directory.
Expected final markers are `RecordClass:Upgrade:BaseThenDelta:ColdReopen:True` and
`RecordClassPackaging:ThreeBuilds:ClassToRecord:HistoryUnchanged:ReferenceAssemblies:True`.
The runner's exits and assertions are the acceptance evidence; the source alone does not establish a pass.

SDK pack can emit NU5131 for model packages with `ref/` assemblies and no legacy nuspec
`references` group. This consumer supports PackageReference; selected compile and runtime paths
are explicitly checked. It does not test packages.config consumers.
