# Atelia.DurableGraph

DurableGraph is an exploratory .NET 10 prototype for versioned durable object graphs.
Its public API and persistent formats are not frozen.

A runtime PackageReference supplies the Source Generator and Schema-history build integration.
Durable classes implement `IDurableObject`; explicitly marked partial classes/structs generate versioned
readonly DTOs, Base/Delta bodies, Capture, model/reader registration, Normalize, Allocate and Hydrate.
Supported compositions include references, inline values, generics, records, enums, Nullable, arrays,
List and experimental Dictionary. Unsupported shapes produce diagnostics.
Explicit storage classifications must have a valid field target. Ordinary class/struct property
backing cannot be persisted with `field: DurableField` (DG0024); use an explicit field. Real property
backing or ordinary field-like event storage can be excluded with `field: Transient`. Records retain
their stricter storage rules. Capture and restoration do not invoke property accessors.

Schema history uses `DurableGraphSchemaHistoryDirectory` and `DurableGraphSchemaHistoryMode`
(`Publish`, `Verify`, `Off`). Commit accepted `.dgschema` files with model source; they retain old DTO/reader
definitions for explicit Upgrade. `Off` does not ignore accepted same-version conflicts.
Domain declarations remain in `Atelia.DurableGraph`; layouts use `.Schema` and binding/capture/state
operations use `.Runtime`. Repository operations require `Atelia.DurableGraph.Persistence`.

Register models before Open; save independent Event/State graphs, query checkpoints, resolve immutable
tags and fork named branches. Each branch has at most one active checkout; repository operations are
serialized. Restoration bypasses constructors and field initializers. Rebuild Transient state after
delivery; ReadPair may share read-only nodes, so view-specific context belongs outside those graphs.

FrameStore/VersionStore own backing storage; meta Schema objects and business saves share publication.
Completed frames remain append-only. Normal backing recovery handles incomplete tails; read-only
opening does not repair them. Publication failures expose outcome and fault state: close and reopen
when required, inspect durable names, and do not transparently retry. No old raw Store or storage-format
compatibility path is provided.

See the [repository guide](https://github.com/Atelia-org/durable-graph/blob/main/README.md) for complete
public examples, current type boundaries, schema development, dependency setup and failure handling.
The [package probes](https://github.com/Atelia-org/durable-graph/tree/main/experiments/PackageConsumerProbe)
distinguish executable delivery gates from model/history compilation fixtures.
