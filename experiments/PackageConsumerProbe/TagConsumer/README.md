# Immutable tag package consumer

```powershell
./experiments/PackageConsumerProbe/Run-TagProbe.ps1
# Or use the complete isolated feed packed from the current source revision:
./experiments/PackageConsumerProbe/Run-TagProbe.ps1 -PackageSource <feed> -Version <DurableGraph-version>
```

The project uses only the two public PackageReferences. Packaged generator/build
assets publish and verify two schema histories, with distinct State and Event
root types. The runner restores the complete nine-package feed into a private
cache. Storage component versions come from dependency metadata and the shared
feed helper; the runner does not force all storage packages to one version.

Four separate processes exercise durable names without serializing handles:

1. `seed` tags an Event-first checkpoint and a later State, then advances the source branch.
2. `metadata` resolves both tags in a readonly repository with an empty model registry.
3. `continue` reads actual tagged roots, rejects a previous repository owner's
   address, and continues both tags through ref-only `CreateBranch` + `Checkout`
   and named `Fork`. Event-first continuations install their first States.
   Advancing and moving the source branch leaves both tags fixed.
4. `verify` reopens readonly and checks historical values, State self-cycles,
   all four child branches, and the moved source branch.

The successful markers are `TagsSeed`, `TagsMetadata`, `TagsContinue`, and
`TagsVerify`, each with the detailed assertions printed by the runner. Artifacts
remain under `../obj/tags-*`, including the private package cache and generated
schema histories. Failure injection and append-only byte audits belong to the
product tests; this probe witnesses public package delivery across processes.
