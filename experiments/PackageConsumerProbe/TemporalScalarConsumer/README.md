# Temporal scalar package consumer

> 当前 G 门禁暂停：此 lane 仍依赖 `RevisionDecoder` 与 SCB1/raw Store 诊断；`Schemas/States` 已收为 internal，默认 owner 不授旧模式。后继迁移范围见 [DB-087 §36](../../../docs/design-branches/0087-meta-bootstrap-refactoring-route/0087-meta-bootstrap-refactoring-route.md#36-p1d-第七片旧typed恢复束退出)。下文保留匹配历史包版本的验收记录，不能作为当前门禁。

Run from the repository root:

```powershell
./experiments/PackageConsumerProbe/Run-TemporalScalarProbe.ps1
```

Or reuse a ten-package feed built from the same sources:

```powershell
./experiments/PackageConsumerProbe/Run-TemporalScalarProbe.ps1 -PackageSource <feed> -Version <version>
```

The runner packs an isolated dependency closure and uses an isolated package cache. The consumer
has only ordinary Runtime and StateStore PackageReferences; it has no ProjectReference, manual
Analyzer, Import or AdditionalFiles wiring. Artifacts stay in the unique ignored `obj` directory.

V1 saves DateOnly, TimeOnly and DateTimeOffset in direct fields, record struct state, generic
Box values, Nullable arrays, Lists and all three standard Dictionary key/value shapes. It checks
leap-day dates, midnight and final-day ticks, timestamp extrema, shared List identity and cold
NoChange. A change of offset at the same instant produces real owner/inline/List/Nullable List
Deltas; explicitly removing and adding the Dictionary key produces Remove+Add with the new
offset. Domain timestamp lookup still follows the standard same-instant comparison.

V2 deletes the old inline CLR declaration while retaining its exact generated state history.
The same nominal Point family changes its Count field from int to long with an explicit +1000
business conversion. An explicit owner dependency and shared List element upgrade invoke that
conversion once per value. Only the World and shared List rewrite Base; the next save is unchanged,
and later offset edits resume ordinary Delta. Cold reopen retains the exact offsets without
repeating upgrades. Historical exact reads preserve old values and do not call business code.

Required outputs:

```text
TemporalScalarSeed:True:ThreeScalarCompositions:True:ExactOffset:True:OffsetKeyRemoveAdd:True:ColdReopen:True
TemporalScalarUpgrade:True:DeletedInlineClr:True:ExplicitOwnerAndValueUpgrade:True:HistoricalExact:True:ForcedBaseThenDelta:True:ColdReopen:True
```

History counts are `3 -> 5`. New files use v9; accepted filenames, SHA256 hashes and complete
bytes are checked before and after Publish/Verify. This two-generation lane does not replace the
fixed-v8/new-v9 history compatibility tests. No container nominal type changes or implicit time
conversions are exercised; DateTime and local timezone/DST normalization remain outside scope.

The contract and acceptance record live in
[DB-058](../../../docs/design-branches/0058-temporal-scalar-value-slice/README.md).
