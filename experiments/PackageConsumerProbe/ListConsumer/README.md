# List package consumer

> 当前 G 门禁暂停：此 lane 仍依赖 `RevisionDecoder` 与 SCB1/raw Store 诊断；`Schemas/States` 已收为 internal，默认 owner 不授旧模式。后继迁移范围见 [DB-087 §36](../../../docs/design-branches/0087-meta-bootstrap-refactoring-route/0087-meta-bootstrap-refactoring-route.md#36-p1d-第七片旧typed恢复束退出)。下文保留匹配历史包版本的验收记录，不能作为当前门禁。

Run `./experiments/PackageConsumerProbe/Run-ListProbe.ps1` from the repository root.
The optional `-PackageSource <feed> -Version <version>` pair reuses an already packed dependency feed.

Each run creates a new shared FrameStore/VersionStore repository. The 32-entry container
keeps enough unchanged content for the sparse edits to remain real Deltas with the fixed 12B
prior address. The lane retains exact model-schema history and its immutable file/hash checks.

This DB-047 witness builds two application versions against real Runtime and Storage packages,
without project references or manual generator/build wiring. V1 saves a shared `List<LegacyPoint>`,
`List<List<int>>`, arrays of lists, lists of multidimensional arrays of generic structs,
`Box<List<int>>` with both `T` and `List<T>` members, inline structs containing lists, and a World/list cycle.
Consecutive commits cover element replacement, append, middle insert/remove and tail removal while
retaining the same list identity. Increasing Capacity writes no object changes. All intermediate
revisions decode their respective frozen contents; a separate saved-content witness clears
the domain list after saving both Base and Delta and then restores the saved data.

V2 deletes the LegacyPoint CLR declaration and supplies CurrentPoint with the same Schema ID at V2.
Retained history generates the old exact state DTO and reader. An explicit list element rule set
upgrades each element once for the shared list, receives its List layout/count owner facts, and
forces only that list to Base. An unchanged commit writes no objects; a subsequent element edit
uses Delta on the same domain instances. Cold reopening does not repeat Upgrade, and the original
revision still decodes `FrozenListState<Point.V1>` independently of the removed domain declaration.

Every object's Base v4 representation ID resolves through the persisted SchemaCatalog. Reopening
and reverse-order registration preserve IDs without metadata writes. The upgraded list receives
a new representation ID while its referring World's ID stays stable; subsequent Delta inherits it.

Required stage markers are:

- V1: `ListSeed`, `CompositionsAndCycles`, `ResizeAndCapacity`, `FrozenDelta`, `RepresentationIds`, `ReorderedRegistration`.
- V2: `ListUpgrade`, `SharedOwnerOnce`, `ForcedBaseThenDelta`, `HistoricalExact`, `DeletedDomainStruct`, `ColdReopen`, `IndependentRepresentationUpgrade`, `DeltaInheritsRepresentation`.

Each marker is followed by `True`. The runner publishes and verifies canonical history v9, checks
counts `4 -> 5`, and preserves all accepted file names and hashes. Feed/cache/history/databases
remain in a unique ignored `obj` run directory. The lane verifies package delivery and cross-process
history continuity; malformed bodies, empty-list Upgrade preflight and failure injection belong to
product tests. The current consumer uses DB-049's List range codec 2; writer comparisons and performance
evidence live in [ListDeltaReplayProbe](../../ListDeltaReplayProbe/README.md), not in these package markers.
