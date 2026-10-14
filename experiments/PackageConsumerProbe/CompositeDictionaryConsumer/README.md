# Composite Dictionary package consumer

> 当前 G 门禁暂停：此 lane 仍依赖 `RevisionDecoder` 与 SCB1/raw Store 诊断；`Schemas/States` 已收为 internal，默认 owner 不授旧模式。后继迁移范围见 [DB-087 §36](../../../docs/design-branches/0087-meta-bootstrap-refactoring-route/0087-meta-bootstrap-refactoring-route.md#36-p1d-第七片旧typed恢复束退出)。下文保留匹配历史包版本的验收记录，不能作为当前门禁。

Each run creates a new shared FrameStore/VersionStore repository. The 32-entry container
keeps enough unchanged content for the sparse edits to remain real Deltas with the fixed 12B
prior address. The lane retains exact model-schema history and its immutable file/hash checks.

DB-055 package delivery and historical behavior witness:

```powershell
./experiments/PackageConsumerProbe/Run-CompositeDictionaryProbe.ps1
```

The runner builds two separate executables using ordinary Runtime/Storage PackageReferences,
an isolated package feed/cache and the packaged Publish/Verify history workflow. Pass both
`-PackageSource` and `-Version` to reuse an existing matching ten-package feed. Artifacts remain
under the unique ignored `obj/composite-dictionary-*` directory. No private product APIs or
ProjectReferences substitute for public delivery.

V1 has a generic `LegacyKey<LegacyPart>` with persistent Part, Scope and Timestamp fields.
Its IEquatable implementation ignores Timestamp; an additional Scratch field is Transient.
A plain `new Dictionary<Key, Value>()` needs no comparer configuration. A second Dictionary of
the same closed type uses an external generic comparer, resolved once per model snapshot.
Shared aliases and a containing List exercise the same binding through different incoming edges.
A typed string configuration returns the built-in OrdinalIgnoreCase comparer for an Application
dictionary created using a custom comparer. Thus the witness exercises both configuration paths
and preserves mode 5 even when its current inner comparer is a standard instance.

The first executable performs a value patch, actually replaces a key with a different Timestamp,
and changes only its Transient state. The first two changes must produce Delta; the last must
compare unchanged. Stored-exact reads retain the complete Timestamp. Cold Load and another
unchanged Commit must preserve CurrentDefault and Application modes, shared instances and lookup
behavior while Transient state returns to default.

V2 deletes the old generic Key, nested Part and Value CLR declarations. Their durable identities
advance to V2; Part/Value change int to long. An open Key conversion explicitly requests the
nested Part tool, while independent Dictionary key and value rules control conversion. The
expected 192 callbacks comprise key, nested part and value for 32 entries in each of two
Dictionaries. Only these two owners rewrite Base. Both subsequently compare unchanged, resume
value Delta and cold-reopen without repeating Upgrade.

A second repository isolates a different transition: CollisionKey and CollisionWorld remain at
Schema V1, and their persistent fields do not change. Only current Equals/GetHashCode changes
from Tenant+Number to Tenant. Historical exact DTOs remain readable without executing comparison
code, but current domain loading must reject two keys at TryAdd. No Upgrade runs, no resolver is
needed, and exact reading remains available after the failed Load. This distinguishes preserved
data from the ability of current business comparison rules to accept it. The package cannot call
the internal whole-revision Normalize API directly; focused product tests isolate that phase.

New history uses v9, with exact file counts **6 -> 9**. Previously accepted filenames and
SHA256 hashes must remain unchanged. The persistent formats are not version-bumped for this
fixture. Stage outputs are printed only after their executable assertions pass:

```text
CompositeDictionarySeed:True:DefaultIgnoresTimestamp:True:TypedAndGenericApplication:True:PersistentKeyReplacement:True:StableModes:True
CompositeDictionaryUpgrade:True:DeletedGenericAndNestedTypes:True:ExplicitDoubleSlotUpgrade:True:ForcedBaseThenDelta:True:ColdReopen:True:SameSchemaComparerCollision:True
```

Malformed payloads, absent/mismatched comparer configuration, zero-width keys and callback
failure boundaries are covered by focused product tests. This witness does not promise historical
business comparer preservation or comparison based on not-yet-hydrated reference contents.
See [DB-055](../../../docs/design-branches/0055-composite-dictionary-key-design/README.md) for the contract.
