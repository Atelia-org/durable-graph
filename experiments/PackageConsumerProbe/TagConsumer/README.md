# Immutable tag package consumer

```powershell
./experiments/PackageConsumerProbe/Run-TagProbe.ps1
# Or use the complete isolated feed packed from the current source revision:
./experiments/PackageConsumerProbe/Run-TagProbe.ps1 -PackageSource <feed> -Version <DurableGraph-version>
```

The project uses only the two public PackageReferences. Packaged generator/build
assets publish and verify two schema histories, with distinct State and Event
root types. The runner restores the complete ten-package feed into a private
cache. Storage component versions come from dependency metadata and the shared
feed helper; the runner does not force all storage packages to one version.

Four separate processes exercise immutable tags and persistent commit locations:

1. `seed` creates an Event-first root branch, tags that CommitId and a later State,
   then advances the source branch.
2. `metadata` resolves both tags and reads their CommitInfo in a read-only repository
   with an empty model registry.
3. `continue` round-trips a CommitId through its public codec and retains it across
   closing and reopening the same repository. It creates another tag from that ID,
   rejects a default ID and wrong-role load, then uses `ForkBranch` and
   `OpenWorkingCopy` to continue two State forks and two Event-only forks.
   Each Event-only fork starts without a State and saves its first State explicitly.
   Finally it advances the original branch to 99 and creates an unedited fork at
   the old Event; the original branch keeps its progress and the tags remain fixed.
4. `verify` reopens read-only and checks historical values, State self-cycles,
   all four edited children, the unedited old-point fork, and the original branch.

Creation returns BranchInfo and does not retain editing occupancy. State edits use
`WorkingCopy.State`; a successful replacement State commit installs its supplied
instance. This fixture assigns distinct display labels to discover its branches;
labels are not library-enforced keys. Applications can persist BranchId directly.
A CommitId is interpreted in the repository chosen by the caller. Its compact
encoding supports development-time misuse detection, not globally unique identity.

The successful markers are `TagsSeed`, `TagsMetadata`, `TagsContinue`, and
`TagsVerify`, each with the detailed assertions printed by the runner. Artifacts
remain under `../obj/tags-*`, including the private package cache and generated
schema histories. Failure injection and append-only byte audits belong to the
product tests; this probe witnesses public package delivery across processes.
