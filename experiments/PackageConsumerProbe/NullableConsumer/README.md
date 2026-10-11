# Nullable package consumer

DB-052 product delivery witness. Run from the repository root:

```powershell
./experiments/PackageConsumerProbe/Run-NullableProbe.ps1
```

The runner packs ten local dependency packages into an isolated feed and cache. The consumer
references only the public DurableGraph and Persistence packages; generated code and history arrive
through packaged build assets. To reuse an existing matching feed, pass both `-PackageSource`
and `-Version`. All artifacts stay beneath the runner's unique ignored `obj/nullable-*` directory.

V1 writes a World with a `Point?` field, an `int?` field, a shared `List<Point?>`, a vector and a
rank-four array of nullable Points. Present Points retain the same Node, whose back-reference
and self-reference form a cycle. A List element edit creates an actual Delta; unchanged saving
creates no local object versions. Saved Base and Delta content survives clearing the
original domain values and collections before reading it back.

The 32-element List keeps its Base larger than a one-element Delta even with the new fixed
12-byte prior address. The probe retains its physical Delta assertions and uses a fresh new-format
repository; its accepted V1 model history remains unchanged when compiling V2.

V2 deletes the `LegacyPoint` CLR declaration and introduces `CurrentPoint` with the same durable
family at V2. World advances to V2 because its inline nullable child changed. An explicit World
Upgrade requests the nullable value tool; explicitly configured array/List element rule sets use
the same opted-in nullable lifting. The child conversion adds 1000 to each present Point and
preserves its reference ID. Absent values do not invoke business conversion. The expected 35
calls belong to four independent owners: one World field, 31 List elements, two vector elements
and one rank-four element. The shared List is upgraded once despite its two incoming references.

The consumer exact-decodes the old Base/Delta revision before business Upgrade, forces four
upgraded owners to Base, resumes ordinary Delta, and cold-reopens without repeating upgrades.
Clearing every nullable value removes the now-unreachable Node and its string while retaining
the World, shared List and arrays; the historical revision remains readable.

The runner checks immutable history hashes, publication followed by packaged Verify mode,
canonical history v9, and exact file counts **3 -> 5**. Required stage outputs are:

```text
NullableSeed:True:SharedCycles:True:VectorAndRank4:True:FrozenPreparation:True:HistoricalDelta:True
NullableUpgrade:True:LiftedOwnerAndElements:True:ForcedBaseThenDelta:True:HistoricalExact:True:DeletedDomainStruct:True:ColdReopen:True:ClearRemovesIsland:True
```

This is a functional and package-delivery witness, not a performance comparison. Product unit
tests separately cover malformed nullable bodies, explicit provider failures, missing historical
capabilities, schema conflicts and candidate Discard semantics. Implementation evidence belongs
in [DB-052](../../../docs/design-branches/0052-nullable-value-slot-slice.md).
