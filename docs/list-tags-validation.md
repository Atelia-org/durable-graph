# ListTags consumer slice

2026-10-26. The MUD consumer needs to discover named saves without maintaining a second tag index.

`Repository.ListTags(): TagInfo[]` reads the existing bookmark directory and returns detached
`Name` / `CommitId` records, sorted with `StringComparer.Ordinal`. It validates every target's
commit metadata without restoring domain objects, works with read-only repositories and an
empty model registry, and rejects the entire query if any target is invalid. Existing operation,
fault and disposal guards apply. Storage dependencies and persisted formats are unchanged.

Source validation uses the root Release build and the complete Persistence test project in
explicit storage source mode at the pinned revision. Tests cover empty repositories, Unicode
and case-sensitive ordering, independent snapshots, State/Event targets, cold read-only opens,
no file changes, a large bookmark directory, invalid target metadata, and fault/disposal guards.

Package validation uses a newly versioned candidate from committed source, then the existing
TagConsumer's four separate processes with ordinary PackageReferences and isolated cache,
build output and schema history. Its metadata lane now checks the ListTags API and exact results.
Source results: root Release build passed with zero warnings/errors; all 1,207 Persistence
tests passed with zero skips. Commands used `-p:UseStorageSources=true` and the pinned storage
checkout at `70b406cd8d1117733eee81a33e7f978399aa98b2`. Local logs are
`.artifacts/list-tags-build.log` and `.artifacts/list-tags-tests.log`.

Delivery passed from clean source commit `5a705711311a7dbd54c0dab354ac55054b3083c0`:

- G: `0.0.0-mudtags.20261026.g5a70571`; S remains `0.1.3-dev.20261019.g70b406c`.
- Portable eleven-package candidate: `.artifacts/candidates/0.0.0-mudtags.20261026.g5a70571/`,
  including `feed/`, `packages.sha256`, complete consumer configuration and source provenance.
- `Prepare-DurableGraphCandidate.ps1` verified the six pinned storage packages and built G in
  package mode. The tag probe reused the ten Atelia packages from the producer's `pack-feed`.
- `Run-TagProbe.ps1` passed Publish/Verify builds with zero warnings/errors and all four cold
  processes (`seed`, `metadata`, `continue`, `verify`). Its isolated artifacts are under
  `experiments/PackageConsumerProbe/obj/tags-20261026001304-24416-c2f8c3a5/`.
- Consumer assets select exactly the four G and six S versions above. The Persistence DLL in
  the candidate package and consumer output has the same SHA256:
  `4992200E7CF9F1FB500D2A5CFAB6F449BB67E473000DF653DB6AF862D5B45F40`.

This is source regression plus the ordinary-package tag workflow, not a rerun of every package
consumer or the relocated QuickStart qualification. No remote publication occurred. The MUD
repository owns its separate vendoring and game integration acceptance.
