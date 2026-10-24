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
Build projects sharing a history directory serially. After a Publish build completes, Verify builds
may run in parallel with separate outputs while history stays unchanged. Publish/Verify operations
coordinate locally to protect history from overlapping publishers; compilation itself does not acquire
that coordination and may reject a temporarily incomplete history set.
Domain declarations remain in `Atelia.DurableGraph`; layouts use `.Schema` and binding/capture/state
operations use `.Runtime`. Repository operations require `Atelia.DurableGraph.Persistence`.

State-based applications can create a branch with State and call CommitState repeatedly; no preceding
Event or Event model is required. CommitEvent is optional and publishes separately from CommitState.
Each Event captures a complete graph with local identities, its own Base records and no State content source.
Events neither consume nor advance the State saving baseline and do not accept incremental saving policy parameters.
Register models before Open; save independent Event/State graphs, query checkpoints, resolve immutable
tags and fork branches with nonunique display labels. Each branch has at most one active WorkingCopy;
repository operations are serialized. Open selects resource access; CreateRootBranchFromState,
CreateRootBranchFromEvent and ForkBranch return durable BranchInfo. OpenWorkingCopy separately acquires
an editable working copy by BranchId. All saves share one repository Schema Catalog; accepted
definitions cannot change shape under the same key. Develop conflicting drafts in separate complete,
closed repository copies, then upgrade a clean baseline with the final model. Private/ToShared APIs
have been removed. The P0R4 branch-directory pointer and P0G2 business graphs reject older layouts rather than
providing a compatibility path.
Restoration bypasses constructors and field initializers. Rebuild Transient state after
delivery. LoadState and LoadEvent restore independent graphs; ReadCheckpoint restores at most two
independent graphs and rejects mutable factory-instance collisions. ReadPair and its cross-graph sharing
optimization have been removed; graph-local aliases and cycles remain supported.

FrameStore/HeadStore own backing storage; meta Schema objects and business saves share publication.
Completed frames remain append-only. Normal backing recovery handles incomplete tails; read-only
opening does not repair them. Publication failures expose outcome and fault state: close and reopen
when required, inspect durable names, and do not transparently retry. No old raw Store or storage-format
compatibility path is provided.

See the [repository guide](https://github.com/Atelia-org/durable-graph/blob/main/README.md) for complete
public examples, current type boundaries, schema development, dependency setup and failure handling.
The [package probes](https://github.com/Atelia-org/durable-graph/tree/main/experiments/PackageConsumerProbe)
distinguish executable delivery gates from model/history compilation fixtures.
