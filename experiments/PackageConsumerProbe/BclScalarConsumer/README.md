# BCL scalar package consumer

Run from the repository root:

```powershell
./experiments/PackageConsumerProbe/Run-BclScalarProbe.ps1
```

Use `-PackageSource <feed> -Version <version>` to reuse a matching ten-package feed.
The default runner packs the complete local dependency closure. The consumer references only
the Runtime and StateStore packages; it has no ProjectReference, manual analyzer, import or
AdditionalFiles wiring. Each application generation runs in its own process against one database.

Generation 1 captures Guid, decimal and TimeSpan directly, in a readonly positional record,
generic Box closures, nullable arrays, nullable Lists, and Dictionary keys/values. Scale-only
changes remain visible in owner, inline, List and nullable List state. An actual Remove/Add changes
the stored decimal key from `1.0m` to `1.00m`; independent raw Dictionary Delta reading requires
one old-key Remove, zero PatchValue entries and one new-key Add. Cold loading preserves scale,
signed zero, TimeSpan extremes, GUID identity values and shared List identity. Recapture is NoChange
and retains ScalarDefault for all three builtin key kinds.

Generation 2 deletes the old inline CLR declaration, changes its counter from int to long,
and explicitly advances the inline and World schemas. Its generated historical DTO still holds
Guid/decimal/TimeSpan values directly. Exact reading does not call business upgrades. The owner
requests an explicit Point conversion tool; the shared List uses the selected element rule.
The callbacks run once for the owner's inline value and once for each of the 32 List elements.
Only the owner and List rewrite Base; the next save is NoChange, the following scale edit produces
ordinary Delta, and another cold reopen does not repeat upgrades. Unchanged generic Box histories
and original scalar/Dictionary bodies remain readable without a conversion.

Required markers:

```text
BclScalarSeed:True:ThreeScalarCompositions:True:ExactDecimalScale:True:DecimalKeyRemoveAdd:True:ColdReopen:True
BclScalarUpgrade:True:DeletedInlineClr:True:ExplicitOwnerAndValueUpgrade:True:HistoricalExact:True:ForcedBaseThenDelta:True:ColdReopen:True
```

The runner checks history counts `3 -> 5`, requires v9 for new history, runs packaged Publish and
Verify, and checks accepted filenames, SHA256 hashes and bytes before and after each later build.
This two-generation lane does not replace the separate fixed-v7 fixture required by
[DB-057](../../../docs/design-branches/0057-bcl-scalar-value-slice.md).
It preserves List nominal element identity during Upgrade; it does not demonstrate changing a
container's nominal key/value type, implicit unit conversion, ValueTuple or boxed value support.
Artifacts stay under the ignored unique `experiments/PackageConsumerProbe/obj/bcl-scalar-*` directory.
