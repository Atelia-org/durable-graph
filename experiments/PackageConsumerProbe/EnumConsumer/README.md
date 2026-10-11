# Enum package consumer

DB-053 delivery witness. Run from the repository root:

```powershell
./experiments/PackageConsumerProbe/Run-EnumProbe.ps1
```

The runner creates an isolated ten-package feed and cache, publishes generated history and
then verifies it with the packaged build assets. Both consumer references are ordinary
PackageReference entries. Pass both `-PackageSource` and `-Version` to reuse an existing matching
feed. Artifacts remain under the unique ignored `obj/enum-*` run directory.

V1 declares a registered `LegacyMode : int` with Flags and an alias, while the saved values include
negative numbers, unnamed values, `int.MinValue` and `int.MaxValue`. A World contains a direct enum,
nullable enum, shared List, vector, nullable rank-four array, `Box<T>.T? where T : struct, Enum`, and
inline `Cell<T> where T : unmanaged, Enum`. The five reference objects survive continuous saves;
inline enum and Cell values have no object IDs. One List element edit produces an actual Delta,
unchanged saving writes no object versions, and saved Base/Delta bytes survive
clearing or overwriting every corresponding source value before reading them back.

V2 deletes the old CLR enum declaration and introduces `CurrentMode : long` with the same durable
identity and version 2. Its current constant table differs deliberately. Stored-exact decoding
still returns the original integer DTOs before any business callback. Explicit enum conversion
adds 1000; World, Box and Cell explicitly request the relevant tools, and configured List/array
owner rules use the same enum rule with opted-in Nullable lifting.

The expected 39 leaf conversions belong to five owners: World 3, shared List 32, vector 2,
rank-four array 1 and Box 1. An absent array element invokes no leaf conversion. The shared List
is converted once despite two incoming references. All five upgraded owners rewrite Base, then
save unchanged and resume ordinary List Delta. A cold reopen preserves the result without
repeating business callbacks. The earlier revision remains independently readable.

The runner requires immutable history hashes and exact counts **4 -> 8**, with canonical text
format v9 for newly published files. This slice reuses the existing catalog and body formats. Stage markers are
printed only after all executable assertions pass:

```text
EnumSeed:True:UnknownBits:True:GenericNullableAndArrays:True:SharedListDelta:True:FrozenPreparation:True
EnumUpgrade:True:HistoricalExact:True:DeletedDomainEnum:True:ExplicitLiftAndOwners:True:ForcedBaseThenDelta:True:ColdReopen:True
```

This is a package and functional witness, not a benchmark. The full scalar-width matrix,
invalid declarations, changed underlying type without version advancement, missing upgrade
capabilities and corrupted body/catalog cases belong to focused product tests. This consumer
does not claim that Schema history records or validates enum constant names and assignments.
See [DB-053](../../../docs/design-branches/0053-enum-inline-state-slice.md) for the contract and
recorded acceptance evidence.
