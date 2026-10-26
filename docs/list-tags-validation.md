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

The candidate and ordinary-package validation are the remaining delivery step.
