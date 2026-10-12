# Array package consumer

> 当前 G 门禁暂停：此 lane 仍依赖 `RevisionDecoder` 与 SCB1/raw Store 诊断；`Schemas/States` 已收为 internal，默认 owner 不授旧模式。后继迁移范围见 [DB-087 §36](../../../docs/design-branches/0087-meta-bootstrap-refactoring-route.md#36-p1d-第七片旧typed恢复束退出)。下文保留匹配历史包版本的验收记录，不能作为当前门禁。

Run `../Run-ArrayProbe.ps1` from this directory, or run
`./experiments/PackageConsumerProbe/Run-ArrayProbe.ps1` from the repository root.
The optional `-PackageSource <feed> -Version <version>` pair reuses an already packed dependency feed.

Each run creates a new shared FrameStore/VersionStore repository. The 32-entry container
keeps enough unchanged content for the sparse edits to remain real Deltas with the fixed 12B
prior address. The lane retains exact model-schema history and its immutable file/hash checks.

This DB-043 witness builds two application versions against actual Runtime and Storage packages,
without project references or manual generator/build wiring. The first writes a shared `Point[]`,
`int[][]`, `Pair<int,string>[,]`, rank-three and rank-four arrays, a `Box<int[]>` exercising both `T`
and `T[]` fields, and a World/array cycle. One in-place element edit persists one array Delta;
the original Revision remains frozen and reopening restores sharing and all four shapes.

The second changes only Point's inline Schema from an integer field to a long field. World, Box
and Pair retain their versions because the array reference is nominal. It selects one explicit
array element rule set. The shared array owns one conversion of each element, receives array
layout/shape facts, keeps its ObjectId and is the only object forced to Base on the next Commit.
An unchanged Commit writes no objects, and a later element edit uses Delta while preserving the
same domain instances. Cold reopening performs no further Upgrade. The consumer also reads the
original Revision as `FrozenArrayState<Point.V1>` after current Point has changed.

DB-045 additionally checks every persisted object Base through the public representation directory:
its v4 header contains only a canonical integer representation ID before the raw body. String uses
its reserved ID; generic classes and all array shapes resolve their complete exact layouts from
the persistent directory. Closing all handles and requesting the same layouts in reverse order on
a new writable SchemaStore must return the original IDs without appending metadata. User Schema
count is deliberately not treated as representation count, since built-in arrays also register.
The Point element Upgrade changes only the shared array's representation ID; World's and all other
objects' IDs remain unchanged. Subsequent array Delta chains inherit that new Base ID, and historical
reads still resolve the old one. Required markers include `RepresentationIds`, `ReorderedRegistration`,
`IndependentRepresentationUpgrade`, and `DeltaInheritsRepresentation`, each followed by `True`.

The runner publishes and verifies canonical history v9, checks counts `4 -> 5`, and preserves all
previous file names and hashes. It retains feed/cache/history/database artifacts in its unique
ignored `obj` directory. Broader malformed-input, unsupported-shape and upgrade-failure coverage
belongs to the product tests; this lane proves package delivery and two-process history continuity.
