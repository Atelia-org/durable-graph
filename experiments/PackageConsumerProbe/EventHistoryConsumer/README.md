# EventHistory package consumer

Run from the repository root:

```powershell
./experiments/PackageConsumerProbe/Run-EventHistoryProbe.ps1
# Or reuse an isolated feed from this source revision:
./experiments/PackageConsumerProbe/Run-EventHistoryProbe.ps1 -PackageSource <feed> -Version <version>
```

This DB-063/064 delivery witness has only the two public PackageReferences, no project/analyzer/import
wiring. The runner packs nine dependencies, publishes V1 Schema history, runs that process to
completion, then compiles and runs V2 with the same immutable history and repository. Both builds
also run the packaged history Verify target.
DB-077 model configuration is supplied once at each repository Open. This project forces Family
definitions and registers `Family_*.Definition` before Open; subsequent CreateBranch, Resume and
Read operations compile without registry arguments against the real packages.

V1 writes `S0 -> E1 -> S1 -> E2`, leaving E2 pending. World contains Alice and Bob; Observed events
contain Alice twice, Alice has a self-cycle and shared string labels. E1 records score 2, S1 score 3,
and E2 score 4. Hot saves preserve the caller's World and transient cache.

This fixture deliberately mutates Alice through a shared hot alias after recording an Event, to
prove that persisted DTOs remain frozen. That also changes the original hot PendingEvent object;
it is not the recommended read-only Event modeling pattern. For application usage, see the
[snapshot/recovery consumer](../EventHistoryRecoveryConsumer/README.md), which isolates the observed content.

V2 changes Alice's scalar representation and adds World.Generation through explicit adjacent
upgrades, preserving Alice's stable creation timestamp. That meaningful stable field also makes
the later one-field edit economically worth writing as Delta. From a fresh readonly open, a catalog containing only Observed and Alice reads E2;
World and Bob are not registered and World's Upgrade remains uncalled. A separate readonly open
with the complete catalog performs ReadPair, which
returns E2 and S1 with their correct values and graph-local aliases. File content and modification
times remain unchanged by readonly operations; the API does not promise cross-view instance sharing.

Cold Resume(E2) returns preceding State and pending Event independently, without mutable Alice
aliases between them. Completing State forces World/Alice Base, then subsequent E/S steps verify
NoChange and ordinary Delta. Another pair validates old E2 after later State changes, and a final
State commit replaces the root while retaining its child instances.

`metrics.txt` records total RBF bytes and the selected State revisions' actual local Base/Delta
write counts. These observations are not physical read-I/O measurements or performance thresholds.
Fault injection, cross-store durability and branch movement are product-suite responsibilities.

The separate `database-shared-read` repository adds five unchanged schemas in both builds;
it does not change the Alice/World Upgrade fixture. V1 writes `S0 -> E1 -> S1 -> E2`.
E1 references S0's entire World unchanged. S1 changes only a child scalar, and E2 records
another value while leaving State pending. V2 opens this repository in a different process
and checks the real persisted head maps: E1 reuses every S0 object head, while S1 retains the
root/container heads and changes exactly one child head.

The shared-read fixture covers a stable two-node cycle, a changing self-cycle, shared and distinct
equal nonempty strings, arrays, lists, string-key dictionaries, reference-identity dictionary keys,
and present inline/Nullable structs carrying references. Two generic holder objects have only
an inline or Nullable reference to the changed child, so missing these edges cannot be hidden by
another direct field on the same owner. Every selected graph must retain its own
values and graph-local aliases. The non-generic ReadPair overload is exercised with State/Event,
Event/State, Event/Event and State/State inputs, checking actual root types and positional values.
It also reads the same selection twice and checks that readonly
operations leave file content and modification times unchanged. After cold Resume, mutating both
stable/changed State nodes and containers must leave the pending Event unchanged.

`database-shared-read/shared-read-metrics.txt` records actual retained reference-instance counts,
current-thread managed allocated bytes and elapsed milliseconds for independent versus paired
S0/E1 and S0/S1 reads. Retained counts include reachable domain classes, containers and
strings; inline structs are not separate instances. Cross-view `ReferenceEquals` results are
observations only, never acceptance assertions or public sharing guarantees. Each timing is a
single synchronous sample including binding/JIT effects, not a benchmark; allocation is not peak
memory, and neither metric measures physical read I/O. There are no performance thresholds.

## Per-view Transient context

The same packaged shared-read fixture also exercises the DB-066 view contract, through
`SharedReadProbe.CheckExternalViews` and its small `WorldView` class. `SharedRoot` represents a World;
its stable `SharedNode` cycle represents two unchanged actors. S0 and S1 have different world context
values (10 and 20), while both actors retain their persisted values (1 and 2).

Each `WorldView` keeps `Snapshot`, `ActorsById` and `QueryContext` outside the durable graph. Querying
either actor returns that view's own world context together with the actor value. These assertions
run on the actual restored pair, without requiring cross-graph `ReferenceEquals` or a sharing hit.
For example, the caller uses the two independently constructed views:

```csharp
WorldView earlier = new(pair.First);
WorldView later = new(pair.Second);
ActorObservation before = earlier.ObserveActor(1); // WorldValue 10, ActorValue 1
ActorObservation after = later.ObserveActor(1);   // WorldValue 20, ActorValue 1
```

Do not set each actor's `Transient.OwnerWorld` while visiting a ReadPair result: the second view could
overwrite the first view's context on a shared actor. Transient means unpersisted, not unobservable.
A global Actor-to-context table has the same ambiguity. The fixture verifies that its paired actor
nodes retain uninitialized `OwnerWorld` fields while both external view queries work correctly.

If an application must initialize Transient fields on the nodes themselves, use separate
`ReadState`/`ReadEvent` calls. The probe also does this under `OpenReadOnlyExisting`, sets the two
independent actors' `OwnerWorld` fields, and verifies that each retains its correct world context.
No writer or Resume is required for that historical browsing. Persistent members remain snapshots;
these reads do not install a saving baseline. Use Resume when continuing edits and commits.
