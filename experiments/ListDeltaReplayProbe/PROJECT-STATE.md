# ListDeltaReplayProbe active context

Purpose: replay the same ordinary domain edits into independent DurableGraph repositories and
compare codec-2 List writers under the same policy, with correctness ahead of performance ranking.

Current focus: remove the replay's old wire/read facade dependency while the product retires the old
save core. Saves and historical domain reads now use the public default Repository/BranchCheckout;
stored-exact DTOs and actual payload accounting use the real split store at each graph's selected S.
The minimum acceptance is the README Count 8 ordinary smoke: every published E/S position and State
fingerprint, root/live IDs/head/H, cold latest baseline and candidate Delta Apply/frozen inputs pass.
The integrating thread owns build and executable validation; no new performance result is asserted here.

[DB-051](../../docs/design-branches/0051-bounded-list-delta-competition.md) is implemented and accepted:
full Local incumbent plus byte-limited Myers competition, independent search budgets and Adaptive as
product default. [ADAPTIVE.md](ADAPTIVE.md) records the original seed matrices, real whitebox writes,
cutoffs and cost of completing Local first. Further optimization requires a measured consumer problem.
The existing fallback candidates remain research evidence in [FALLBACK.md](FALLBACK.md); prior causal examples
and real repository replay remain in [WHITEBOX.md](WHITEBOX.md) and [RESULTS.md](RESULTS.md).
Commands, measurement boundaries and workload inventory live in [README.md](README.md).

Selected invariants: exact same script and initial content per case; repository-local ObjectIds
never address cross-run edits; one checkout per trace; all published graphs and all prepared
candidate Deltas validate; no writer selector needed by readers; algorithm order interleaved and
warmup/initial binding recorded separately. Under DB-063, an initial step publishes S0, and each
edit step publishes an independent marker Event followed by State through BranchCheckout.
Whole-step timing includes both publications; installed root/live IDs/head/H snapshots and all
diagnostics stay outside timing. A fresh read-only Repository signs its own history handles and
ReadState verifies complete fingerprints/aliases/cycles. ReadSplit uses the graph's exact selected S;
actual State OwnedBatch records and object-chain payload/H replace the old raw revision wire.
No OwnedBatch means no local writes. Cold Checkout also preserves the complete saved baseline.
Measurement.Version = 4 identifies the split-store measurement route. Shared data
now contains meta graphs, directory versions, object batches and E/S graph envelopes; control contains
VS files. Version 4 is not cost/performance-comparable with version 3's DB-086 combined format
or older separate-store reports. Historical ADAPTIVE/RESULTS/WHITEBOX/FALLBACK stay frozen.

Output is ignored per-run JSON/CSV/Markdown plus independent repositories. Root product context is
[src/PROJECT-STATE.md](../../src/PROJECT-STATE.md); design/acceptance authority is
[DB-051](../../docs/design-branches/0051-bounded-list-delta-competition.md).

Fallback mode instead uses captured pairs and raw body validation, with no publication or cumulative
policy simulation. It shares kernels/body with the product, and the experimental candidates remain here.

Four product writers are measured through the common body codec; only the three standalone matchers
enter pure matching statistics. The adaptive suite reuses the frozen fixture matrix and verifies
body bytes never exceed explicit Local. Historical three/five-strategy evidence stays frozen.
Pooling, element-level interruption, trigger refinement and region-byte reuse remain profile-driven work.
