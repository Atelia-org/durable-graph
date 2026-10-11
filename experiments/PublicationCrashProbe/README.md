# Publication process-stop witness

DB-086 public FrameStore/VersionStore substrate experiment. Run `./Run-Probe.ps1`;
use `-NoBuild` only after building this project. It is outside the product solution.
Use the same source/package dependency configuration as the root build.

The controller starts a separate .NET writer and kills it at an explicit checkpoint
without running disposal. A fresh writable process opens FS, opens VS and reads only
the selected root and its required data frame. Checkpoints cover an empty root,
second data appended before confirmation, data confirmed before publication, and
VS publication confirmed. A tiny rotation threshold exercises multiple data files.
An injected incomplete control-file tail is recovered on VS's lazy ReadRef; the
previous complete head remains selected. This intentionally replaces the superseded
strict-tail rejection and whole-store reflush witness.

The byte ordinal and opaque address are a substrate witness, not DG's wire format.
The public VS API combines append and confirmation; this probe does not claim to
inject its internal failure windows. Product failure tests cover those boundaries.
Process kill and ordinary OS visibility do not establish behavior under OS crash,
power loss, storage-device failure or durable directory metadata. Artifacts remain
beneath ignored `obj/run-*`.
