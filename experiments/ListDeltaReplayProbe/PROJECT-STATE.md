# ListDeltaReplayProbe active context

Purpose: replay the same ordinary domain edits into independent DurableGraph repositories and
compare codec-2 List writers under the same policy, with correctness ahead of performance ranking.

Current focus: [DB-051](../../docs/design-branches/0051-bounded-list-delta-competition.md) is implemented
and accepted: full Local incumbent plus byte-limited Myers competition, with independent search budgets
and Adaptive as product default. [ADAPTIVE.md](ADAPTIVE.md) records both seed matrices, real whitebox
writes, exact cutoff observations and the cost of completing Local first. Further work requires a measured
consumer problem; no next algorithm optimization is automatically scheduled.
The existing fallback candidates remain research evidence in [FALLBACK.md](FALLBACK.md); prior causal examples
and real repository replay remain in [WHITEBOX.md](WHITEBOX.md) and [RESULTS.md](RESULTS.md).
Commands, measurement boundaries and workload inventory live in [README.md](README.md).

Selected invariants: exact same script and initial content per case; repository-local ObjectIds
never address cross-run edits; one Session per trace; all persisted revisions and all prepared
candidate Deltas validate; no writer selector needed by readers; algorithm order interleaved and
warmup/initial binding recorded separately. Under DB-063, an initial step publishes S0, and each
edit step publishes an independent marker Event followed by State through BranchCheckout.
Whole-step timing includes both publications; State payload counters and isolated Diff remain
State-only. Journal directory bytes replace the old publication-file metric. Measurement version 2
is intentionally not whole-save-comparable with frozen pre-DB-063 reports.

Output is ignored per-run JSON/CSV/Markdown plus independent repositories. Root product context is
[src/PROJECT-STATE.md](../../src/PROJECT-STATE.md); design/acceptance authority is
[DB-051](../../docs/design-branches/0051-bounded-list-delta-competition.md).

Fallback mode instead uses captured pairs and raw body validation, with no publication or cumulative
policy simulation. It shares kernels/body with the product, and the experimental candidates remain here.

Four product writers are measured through the common body codec; only the three standalone matchers
enter pure matching statistics. The adaptive suite reuses the frozen fixture matrix and verifies
body bytes never exceed explicit Local. Historical three/five-strategy evidence stays frozen.
Pooling, element-level interruption, trigger refinement and region-byte reuse remain profile-driven work.
