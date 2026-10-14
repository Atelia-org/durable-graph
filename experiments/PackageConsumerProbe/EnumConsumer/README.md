# Enum package consumer

> 当前 G 门禁暂停：此 lane 仍依赖 `RevisionDecoder` 与 SCB1/raw Store 诊断；`Schemas/States` 已收为 internal，默认 owner 不授旧模式。后继迁移范围见 [DB-087 §36](../../../docs/design-branches/0087-meta-bootstrap-refactoring-route/0087-meta-bootstrap-refactoring-route.md#36-p1d-第七片旧typed恢复束退出)。下文保留匹配历史包版本的验收记录，不能作为当前门禁。

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

The 32-element List keeps its Base larger than a one-element Delta even with the new fixed
12-byte prior address. The probe retains its physical Delta assertions and uses a fresh new-format
repository; its accepted V1 model history remains unchanged when compiling V2.

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
See [DB-053](../../../docs/design-branches/0053-enum-inline-state-slice/README.md) for the contract and
recorded acceptance evidence.
