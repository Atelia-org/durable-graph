# EventHistory recovery and application snapshots

This independent PackageReference consumer is the recommended small snapshot/recovery example
for [DB-065](../../../docs/design-branches/0065-event-history-consumer-contract-slice.md).
It does not use repository ProjectReferences, manual generator wiring, or private fault hooks.
The existing [EventHistoryConsumer](../EventHistoryConsumer/README.md) separately retains its
Upgrade and deliberately mutable-alias mechanism regressions.

Run from the repository root:

```powershell
./experiments/PackageConsumerProbe/Run-EventHistoryRecoveryProbe.ps1
# Or reuse an existing matching nine-package feed:
./experiments/PackageConsumerProbe/Run-EventHistoryRecoveryProbe.ps1 -PackageSource <feed> -Version <version>
```

The runner creates isolated packages/history/build/database directories beneath
`experiments/PackageConsumerProbe/obj/`, publishes four generated Schema histories and builds
again in Verify mode.

It also inspects the real Persistence nupkg and restored package directory: DLL/XML must be adjacent,
the XML must match the package, and the facade's Commit, Checkout, initial CreateBranch, status and
history-enumeration members must contain their summaries and remarks.
It invokes the built consumer in separate processes:

| Command | Behavior |
|---|---|
| `init-record <directory>` | Creates S0 at Hp 10, records E1, leaves application work unfinished, then exits. |
| `recover <directory>` | Checks out the existing branch, rebuilds Transient lookup, explicitly reads the application-selected Event and applies it, commits S, and exits. It never creates new work. |
| `hot <directory>` | Creates a fresh repository and completes E1/S1 without reopening. |
| `next <directory>` | Accepts a separate second command, records E2, and completes S2. |
| `verify <directory>` | Opens read-only and reads old E1 with only DamageEvent/ActorSnapshot registered. |

To run a mode manually after building, use
`dotnet <consumer-bin>/Debug/net10.0/Atelia.EventHistoryRecoveryConsumer.dll <command> <directory>`.
`init-record` and `hot` require new directories; the other commands require an existing one.
This is a fixed demonstration scenario, not a general command processor.

[Model.cs](Model.cs) owns the application model. ActorSnapshot has readonly scalars and a private
readonly array populated by `List<string>.ToArray()`; its public API only reads individual strings.
The copy isolates the mutable container, and immutable strings may be shared. A readonly array
field by itself does not freeze elements. Mutable element objects would require their own content
isolation. This application snapshot is an ordinary Durable domain object, distinct from generated
Versioned DTOs. It contains no Character/World reference.

[Program.cs](Program.cs) uses the business ActorId to locate the entity and applies damage in place.
ActorId is not an ObjectId or revision handle. All saves use the existing default policy.
Both hot and cold paths retain E1 Hp 10 and original observations while S1 reaches Hp 7;
source list insertion, deletion, and replacement cannot alter E1. The second command reaches Hp 4,
and old E1 remains independently readable. Event-only registration does not imply that repository
Open skips State metadata validation.

[PendingRecovery.cs](PendingRecovery.cs) is the same ordinary application control function linked
into internal fault regression tests. Restore does not run domain constructors or initializers;
the example explicitly rebuilds Transient lookup before applying an event. A second recovery when
the application protocol reports no unfinished work performs no business application and appends nothing. The runner compares
persisted bytes, and verifies read-only browsing changes neither bytes nor file timestamps.

On any exception the example logs the exception and, when available, GraphCommitException.Outcome
and repository.IsFaulted, then leaves the using scopes. It does not reuse old State/Event references,
retry automatically, repair files, or synthesize a replacement Event. An Open failure has no
repository health to report. This conservative policy does not imply every pre-write failure
faults a repository or rolls back domain mutations.

This application imposes its own strict S/E alternation: every Event is followed by the State
that incorporates it before another command can be recorded. Only within that application protocol
is an Event head its processing cursor. `PendingRecovery` explicitly calls `ReadEvent`; Repository
and BranchCheckout do not expose or infer PendingEvent. General E/E/S/S histories must record an
application sequence/cursor in State and choose the unprocessed Event range themselves.
Recovery assumes an existing branch and serialized host control without concurrent branch moves.
A State head cannot prove that an external request completed: publication may have failed before
its Event existed. External request identity and exactly-once external side effects are not supplied
by this example. The handler only mutates domain memory. Internal checkpoint tests, rather than
this public-package program, provide deterministic failure injection evidence.
