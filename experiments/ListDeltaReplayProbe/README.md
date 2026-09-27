# List Delta domain-history replay

The product experiment compares Position, LocalResync, BoundedMyers and the default Adaptive writer
under one List codec. Adaptive preserves a complete Local incumbent and permits only a strictly
smaller, complete Myers challenger. It references product projects and the real source generator,
and copies no matcher or codec. The construction contract is
[DB-051](../../docs/design-branches/0051-bounded-list-delta-competition.md).
Acceptance and measured costs are recorded in [ADAPTIVE.md](ADAPTIVE.md).
The first three-writer matrix and its historical default decision are recorded in [RESULTS.md](RESULTS.md).
Two white-box adverse families and boundary controls are described in [WHITEBOX.md](WHITEBOX.md).
Research-only complementary fallback coordinators and byte-cost counterexamples are in [FALLBACK.md](FALLBACK.md).
The executable uses Runtime/StateStore test friendship only for obtaining closed element operations
and directly measuring their List body implementation. This is not a package delivery test.

Run from the repository root, with no competing builds or benchmark processes:

```powershell
# Small complete correctness smoke: 5 workloads, all algorithms, 14 edits plus initial commit.
./experiments/ListDeltaReplayProbe/Run-Probe.ps1

# Representative three-scale run, including repeated fresh repositories and interleaved writers.
./experiments/ListDeltaReplayProbe/Run-Probe.ps1 -Counts '32,512,4096' -Repeats 3 -DiffRepeats 5

# A larger targeted repetition; avoid an unrestricted largest-width/count Cartesian product.
./experiments/ListDeltaReplayProbe/Run-Probe.ps1 -Counts '8192' -Repeats 3 -Rounds 2 -DiffRepeats 5

# Two white-box families: local window and global edit-depth boundaries, with controls.
./experiments/ListDeltaReplayProbe/Run-Probe.ps1 -Suite whitebox -Counts '4096,16384' -Repeats 3 -DiffRepeats 31

# Research on identical captured frozen pairs; raw payloads, no repository publication.
./experiments/ListDeltaReplayProbe/Run-Probe.ps1 -Suite fallback -Counts '32,512,4096' -Repeats 3 -DiffRepeats 9

# DB-051: four product writers on those same fixture families, including Marker and duplicates.
# Set DOTNET_TieredCompilation=0 for a representative measurement and record it in the output.
./experiments/ListDeltaReplayProbe/Run-Probe.ps1 -Suite adaptive -Counts '32,512,4096' -Repeats 3 -DiffRepeats 9 -Seed 49001
./experiments/ListDeltaReplayProbe/Run-Probe.ps1 -Suite adaptive -Counts '32,512,4096' -Repeats 3 -DiffRepeats 9 -Seed 49002 -NoBuild
```

The runner builds Release unless `-NoBuild` is supplied. `-Output` must name a new directory;
existing repositories are never adopted, deleted or reused. Defaults retain artifacts below ignored
`obj/`. The representative ordinary run executes 2,520 measured save steps and 80 warmup save steps;
budget several minutes depending on flush latency and historical-chain verification. The smoke
executes 300 measured plus 80 warmup save steps. No timeout changes the algorithms or pass criteria.
Count accepts 8–50,000, Repeats 1–20, Rounds 1–20, DiffRepeats 1–100. `-Seed`,
`-ReadAmplification` (X, default 8) and `-BaseBudgetPercent` (Y, default 5) are recorded unchanged
for every algorithm. The widest inline workload caps count at 512 and deduplicates capped sizes.

`-Suite ordinary` is the default. `-Suite whitebox` defaults to Count 4096, requires Count >= 512
and Rounds = 1, and uses deterministic single-edit histories without Seed. It runs five cases
(two adverse cases and three controls), each in its own repository, plus 20 warmup repositories.
It reuses the same Commit, historical Load, candidate Apply and frozen-input checks. In addition,
`whitebox.json` records instrumented comparison counts, known optimal unchanged-pair counts,
independent relaxed-bound diagnostics and isolated matcher time/allocation for the three standalone
matchers. Adaptive is measured as a complete writer, including body/persistence bytes and competition
diagnostics; it is never passed to `ListDeltaMatcher.Plan`. `whitebox.md` summarizes both views.
Counters are excluded from timing. Saved data always uses the unmodified product budgets.
See WHITEBOX for the run with tiered compilation disabled to reduce JIT phase interference.

`-Suite fallback` retains three baseline writers and compares two experiment-only coordinators.
It uses the same generated captured DTO pairs, internal shared matching kernels and product body
writer/decoder. `fallback.json` / `fallback.md` report raw bytes, comparisons, diagnostics, time and
current-thread allocation; no repositories are created and X/Y are unused. The output directory
must still be fresh. Existing Counts/Repeats/Rounds/DiffRepeats limits apply; integer adverse cases
use at least 512 elements and wide-value cases at most 512, while the fixed Marker cases have 34/48/65
elements. This suite does not add public algorithms or change the default. See FALLBACK for
acceptance, exact measurement boundaries, formal runs and the remaining byte-cost decision.

`-Suite adaptive` reuses exactly those generated captured pairs and adds the product Adaptive writer
alongside the three explicit product writers. It calls the actual product entry without a plan factory
or the research coordinators; no standalone matcher metric is invented for Adaptive. `adaptive.json`
and `adaptive.md` report complete Diff time/allocation, raw body size, complete incumbent size,
competition outcome, actual challenger bytes at completion/cutoff, and independent search budget B
per matcher. Untimed wrappers count whole-writer StateEquals calls and direct element WriteBase /
PrepareDelta calls; they do not count recursively expanded generated struct fields. Eight warmups
precede each timing series. Every candidate Apply-validates and leaves both input encodings unchanged;
every Adaptive body must be no longer than explicit Local, with identical HasChanges. Same fixture,
repeat and algorithm order rules apply. No time/allocation nonregression requirement is imposed.
This mode produces no repositories and does not model outer Base/Delta policy choices. An atomic
child call can pass the byte ceiling before the next checkpoint; reported cutoff bytes are actual
written bytes, not an estimated upper bound or a peak-memory guarantee. Historical DB-050 results
remain frozen and describe its original shared-budget research coordinators.

Workloads and script:

- Unique integers and alternating integers.
- Repeated `Node` references and null slots; a pool preserves all nodes, including a reference
  cycle. A child-only edit changes node content while List ObjectId slots remain unchanged.
- Generic `Cell<int>` with a sparse field change.
- Larger nested `Wide<Cell<int>>` with eight stable long fields; a micro-change touches its nested
  value, including the difficult case where every element changes a little before an insertion.

Every round includes head/middle insertion/deletion, tail append/removal, scattered insertion/deletion,
sparse updates, rotation, reverse, micro-change-plus-insert, an undone edit and child-only update.
The script is generated before runs with a fixed seed and domain test IDs/positions. It never uses
repository-assigned ObjectIds to address edits. Reference cases intentionally contain duplicates;
their deletions use deterministic positions rather than pretending references have unique keys.

Each workload/scale/repetition/algorithm gets a fresh independent repository and domain graph.
One BranchCheckout performs its complete trace; no reopen happens between measured edit steps.
All first binding/JIT/publication warmups are separate. Algorithm order rotates between workloads,
scales and repetitions. Edits and fingerprints are outside timing; the complete synchronous Commit
includes a minimal independent marker Event followed by State, both captures, graph writes,
and both Journal ref publication barriers. The marker has no World/List reference. The initial
step measures CreateBranch (S0); repository creation is Setup. Per-step payload counters and
isolated Diff samples remain State-only. Report Measurement.Version is 2: whole-save timings
and repository bytes are not directly comparable with the earlier publication.rbf reports.

After closing the writer, fresh readers materialize every historical Revision and verify complete
content/order/count, shared List identity, repeated children and cycles. An additional Repository
reopen/load verifies the latest graph without selecting the old writer algorithm. This is cold
handles/bindings, not a cleared OS page cache. Its lower-priority metric includes repository integrity
checks and writable-open flushes; do not describe it as raw decoder throughput.

The exact frozen states read from consecutive revisions also enter direct product PrepareDelta.
Reflection closes the generic test entry outside timing; one untimed call precedes repeated samples.
Every candidate is Apply-checked even when the storage policy selected Base, and both inputs are
checked against their original Base encodings. StateEquals, HasChanges and reconstructed bytes must
agree. This local measurement excludes Capture/Base preparation by design and supplements rather
than replaces the whole-Commit measurements. A separate untimed instrumented call verifies identical
bytes and records the same competition/element-call diagnostics used by the adaptive suite; Adaptive
is also checked against explicit Local on every consecutive state pair.

Reports:

- `report.json`: environment, baseline and compiled assembly hashes, policy, matcher budgets,
  generated script, warmup/setup/initial publication, individual observations and median/min/max
  across fresh runs. Allocation is current-thread managed allocation, not peak memory or all-thread
  allocation. Matcher limits are read from product constants; the current comparison-budget formula
  is documented alongside the binary fingerprints. Per-step Diff diagnostics include whole-writer
  equality/child-call counts and the Adaptive competition outcome and cutoff bytes.
- `steps.csv`: per-step Commit and isolated Diff samples, candidate sizes, actual Base/Delta counts
  and actual decoded ObjectVersion payload bytes, plus List-specific write kind/bytes.
- `summary.md`: concise comparison. Correctness is the only automatic gate; no performance winner
  or maximum cold-read latency is required.

Object payload bytes exclude ObjectId, membership and shared frame overhead. State, Schema and
Journal directory sizes (`JournalFileBytes`) are separately reported in each RunResult. State
file totals include marker Event revisions; State per-step payload counts exclude them. Historical
RESULTS/WHITEBOX reports retain their original measurement boundary. Candidate Delta sizes include
NoChange bodies as preparation observations. Undo and child-only edits must leave List slot state
unchanged and cannot produce a List Delta; the policy may still choose an ordinary Base rewrite
to reduce read amplification, and that write remains included in the actual-byte report.
Flush variability and full-graph work can obscure matcher differences; preserve that observation.
This is a bounded synthetic study, not a universal workload ranking or a peak-memory profiler.

Current research context lives in [PROJECT-STATE.md](PROJECT-STATE.md); product authority and acceptance
remain in [DB-049](../../docs/design-branches/0049-list-range-delta-and-matcher-trial-slice.md) and
[DB-051](../../docs/design-branches/0051-bounded-list-delta-competition.md).
