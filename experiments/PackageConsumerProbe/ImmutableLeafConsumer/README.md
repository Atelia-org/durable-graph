# Immutable leaf package consumer

DB-075 delivery witness for the immutable-leaf structural proof through real package delivery.
Run from the repository root:

```powershell
./experiments/PackageConsumerProbe/Run-ImmutableLeafProbe.ps1
```

Pass both `-PackageSource` and `-Version` to reuse an existing matching nine-package feed.
Artifacts remain under the unique ignored `obj/immutable-leaf-*` run directory.

Both consumers share one original model through a linked source file: a non-generic readonly
scalar leaf (`[DurableType("probe.leaf", 1)] public partial class Leaf` with one `readonly int`
field).

The **Family consumer** is an ordinary PackageReference consumer (one `Atelia.DurableGraph`
reference plus the Persistence facade; no manual analyzer, import or AdditionalFiles wiring) that
sets `DurableGraphGenerateDefinitions=true`. It resolves the leaf's current binding through the
generated `DurableDefinitions` registration catalog and requires the internal `IsImmutableLeaf`
capability flag to be `true`. The flag is read through reflection only; the probe adds no public
query API and uses no friend access. The consumer then round-trips the leaf value:
`Repository.CreateNew` publishes the branch, and a fresh `OpenExisting` plus `Resume`
restores the persisted `Value` without running constructors.

The **sibling consumer** compiles the identical model together with a local sibling source
generator ([SiblingGenerator](SiblingGenerator/SiblingGenerator.csproj)) wired as an `<Analyzer>`
DLL. The sibling emits `public partial class Leaf { public int Counter { get; set; } }` in the
same compiler pass, so the durable generator classifies the candidate without seeing the added
mutable state. The binding must then resolve with `IsImmutableLeaf=false`; the structural proof
rejects the unsafe claim at binding creation instead of trusting compile-time classification
alone. The sibling output is never merged into the consumer sources before compilation.

Stage markers are printed only after all executable assertions pass:

```text
ImmutableLeafFamily:Flag:True:Roundtrip:True
ImmutableLeafSibling:Flag:False:True
```

This probe verifies package delivery of the DB-075 capability; it does not replace the focused
generator and runtime tests for classification rules, helper shape checks or sibling scenarios,
and it makes no DB-073 read-cache claim.
