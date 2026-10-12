# Record struct package consumer

> 当前 G 门禁暂停：此 lane 仍依赖 `RevisionDecoder` 与 SCB1/raw Store 诊断；`Schemas/States` 已收为 internal，默认 owner 不授旧模式。后继迁移范围见 [DB-087 §36](../../../docs/design-branches/0087-meta-bootstrap-refactoring-route.md#36-p1d-第七片旧typed恢复束退出)。下文保留匹配历史包版本的验收记录，不能作为当前门禁。

DB-056 package delivery and retained-history witness:

```powershell
./experiments/PackageConsumerProbe/Run-RecordProbe.ps1
```

The runner builds two executables with ordinary Runtime/StateStore PackageReferences and the packaged
Publish/Verify workflow. Supply both `-PackageSource` and `-Version` to reuse a matching ten-package
feed; otherwise it packs an isolated feed. Artifacts remain in the ignored `obj/record-*` directory.
No ProjectReference, manual analyzer, import or hand-built historical DTO substitutes for delivery.

V1 defines readonly positional `LegacyKey<T>`, `LegacyPart` and `LegacyValue` records. The generic key
has nested Part, Scope and Timestamp storage, explicitly marked with `[field: DurableField]`; its
Scratch auto-property uses `[field: Transient]`. One dictionary uses current Default, another a generic
Application comparer. Shared aliases and a List reference the same maps. A typed string Application
choice also demonstrates that returning a standard comparer preserves mode 5 after restoration.

The key deliberately implements equality over Part and Scope. This is business code: record's synthesized
Equals would include Timestamp and Scratch, and DurableGraph attributes do not modify that behavior.
Neither current comparison choice changes the full key DTO used for persistence. Replacing a key's
Timestamp must produce a real Delta; changing only Scratch must produce NoChange. Value edits also
produce actual Dictionary Delta. Cold Load restores readonly backing storage and defaults Scratch.

V2 removes all three old CLR declarations while keeping their durable identities and immutable history.
Part and Value change int to long; Key advances its version for its changed nested inline layout.
An open Key value upgrade explicitly obtains the nested Part tool from UpgradeContext. Independently
selected Dictionary key and value rules run 192 callbacks: Part, Key and Value for 32 entries in each
of two shared Dictionary owners. The two upgraded maps rewrite Base, the next save is NoChange,
subsequent value edits resume Delta, and another cold opening does not repeat Upgrade. Stored-exact
reading of the original revision still returns the historical nested record DTOs.

Record-bearing compilations use `Generated.Family_<UTF8HexId>.Vn` DTOs and
`Generated.DurableDefinitions.Register`, including their ordinary World class. The consumer therefore
uses Family aliases in both generations; it does not depend on the older `__DurableState` API.
Persistence describes FieldId and slot layout, not the record keyword, property name or synthesized
methods. No record-specific persistent format is added.

The runner requires history v9 and counts **4 -> 7**, and verifies that previously accepted filenames
and SHA256 hashes are unchanged. It prints these markers only after executable assertions pass:

```text
RecordSeed:True:ReadonlyPositionalGeneric:True:PersistentKeyReplacement:True:StableModes:True
RecordUpgrade:True:DeletedGenericAndNestedTypes:True:ExplicitDoubleSlotUpgrade:True:ForcedBaseThenDelta:True:ColdReopen:True
```

Focused product tests cover invalid/missing field annotations, accessor side effects, complete dependency
prechecks and empty-container upgrade capability checks. This lane does not support record class,
ValueTuple, cross-assembly definitions or historical business-comparer preservation.
See [DB-056](../../../docs/design-branches/0056-record-struct-state-slice.md) for the complete contract.
