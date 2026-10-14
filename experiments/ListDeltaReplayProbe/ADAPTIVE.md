# Default bounded List Delta competition — DB-051

2026-09-09. Implements [DB-051](../../docs/design-branches/0051-bounded-list-delta-competition/README.md).
The earlier [fallback research](FALLBACK.md), [white-box study](WHITEBOX.md) and [first matrix](RESULTS.md)
retain their original writers, budgets and measured results. This is a new product-writer measurement.

## Result and scope

Adaptive is the default. It finishes and encodes the original Local incumbent, then uses a stall signal
to try independently bounded Myers. Only a complete strictly smaller body replaces Local; reaching its
length stops challenger encoding. A losing scratch buffer never becomes a PreparedDeltaBody.
The streaming encoder removes the previous whole-range patch buffer and retains codec 2 bytes.

Both representative seeds passed: **220 frozen pairs each; 4 smaller, 216 equal, 0 larger** than explicit
Local. Each has 2,640 measured observations (four writers, three repetitions). Every candidate was
Apply-checked, including policy-independent candidates and unchanged pairs; input Base fingerprints
remained unchanged. The guarantee concerns raw List Delta body bytes, not global optimality, whole-file
size, elapsed time or peak memory. Reader, Schema, RepresentationId and outer Base/Delta policy are unchanged.

## Reproducibility

Use the commands and measurement definitions in [README](README.md). Runs were serialized, with Release
binaries and `DOTNET_TieredCompilation=0` for whitebox and both adaptive matrices:

| Run | Parameters | Artifact directory under `obj/` |
|---|---|---|
| Ordinary persistence smoke | default arguments | `run-20260909111000-41904-c6dee44a` |
| Whitebox persistence | counts 4096,16384; repeats 3; DiffRepeats 31 | `run-20260909111040-24996-61c3acc8` |
| Adaptive seed 49001 | counts 32,512,4096; repeats 3; DiffRepeats 9 | `run-20260909111121-43088-df78a452` |
| Adaptive seed 49002 | same, seed 49002 | `run-20260909111134-33336-c2d65fe8` |
| Retained research smoke | fallback; count 32; repeats 1; DiffRepeats 1 | `run-20260909111149-17584-6094d2c6` |

Reports identify source as `245c26e` plus working-tree changes (the implementation was committed after
acceptance). Both adaptive matrices use these SHA-256 fingerprints:

- Probe: `DC2AB4668ADF11210C621CF6DE59738D347C2958D75E5EFABCDFB0C00E1A74B3`
- Runtime: `D5FF1263210D57C88282FC287B0202F8AB5DE7136DF576F3DDE923D906462470`

The ordinary run verified 20 independent measured repositories / 300 revisions, plus 80 warmup
revisions. Whitebox verified 120 measured repositories / 240 revisions, plus 40 warmup revisions.
Every historical revision and latest reopen were validated. The retained fallback smoke verified
84 pairs / 420 observations and its existing kernel/body checks, confirming the explicit planFactory
still bypasses Adaptive coordination.

## Adverse cases and actual writes

The following whitebox rows use X=8, Y=5. ObjectVersion payload bytes exclude ObjectId, membership and
shared frame overhead; body bytes exclude the object envelope. These are distinct measurements.

| Case | N | Local raw body | Adaptive raw body | Local actual List write | Adaptive actual List write |
|---|---:|---:|---:|---|---|
| Head insert 33 + tail change | 4096 | 16236 | 47 | Base 8169 B | Delta 54 B |
| Head insert 33 + tail change | 16384 | 73582 | 50 | Base 40938 B | Delta 58 B |
| Distributed insert/delete 65 each | 4096 | 770 | 770 | Delta 778 B | Delta 778 B |
| Distributed insert/delete 65 each | 16384 | 777 | 777 | Delta 786 B | Delta 786 B |

The first family wins Myers competition. The balanced family does not trigger it, so it keeps Local
without entering Myers's depth cliff. The additional frozen-pair wins are early insertion 3 followed
by insertion 33 and tail change: 1171→156 B at N=512 and 8339→156 B at N=4096.

Marker tests use genuine generated nested struct DTOs. They now stop the larger Myers body after a
few literal elements, while retaining the complete sparse Local body:

| Marker count | Local / selected B | Full Myers B | Challenger cutoff B | Encoded challenger literals |
|---|---:|---:|---:|---:|
| 34 | 207 | 1455 | 220 | 5 of 33 |
| 48 | 291 | 2071 | 308 | 7 of 47 |
| 65 | 393 | 2819 | 396 | 9 of 64 |

Cutoff can exceed the ceiling: one child codec invocation is atomic. This is an output checkpoint,
not an allocation cap. The separate equal-cost unit test has different complete plans of exactly
1161 B; reaching equality at the final Copy retains Local. Duplicate `0100→1001` still does not
trigger: Adaptive retains 13 B although explicit Myers produces 7 B. This accepted false negative
prevents claiming global byte optimality.

## Cost of the guarantee

For each of the 196 ordinary frozen pairs, first take the median across three repetitions of the
per-run nine-sample median; then sum those pair medians. These sums describe isolated Diff, not
wall-clock replay or whole Commit. Allocations are corresponding cumulative current-thread bytes.

| Seed | Local raw B | Adaptive raw B | Local Diff sum ms | Adaptive Diff sum ms | Local allocated B | Adaptive allocated B |
|---|---:|---:|---:|---:|---:|---:|
| 49001 | 279364 | 279364 | 8.8473 | 10.1223 | 20347864 | 21086792 |
| 49002 | 279337 | 279337 | 8.8235 | 9.7569 | 20350776 | 21089704 |

Both seeds have 145 NotTriggered, 31 NoChange, 3 SamePlan and 17 MyersIncomplete ordinary pairs.
Here the guarantee costs about 11–14% isolated Diff time and 3.6% cumulative allocation, with no
ordinary-byte saving. These are bounded synthetic observations, not general performance promises.

Finishing the incumbent deliberately retains Local's worst-case preparation cost. For N=4096 head
insert 33 + tail change, whitebox median isolated Diff is Local 0.5777 ms / 1,589,256 B, Adaptive
0.6239 ms / 1,595,048 B, and explicit Myers 0.0292 ms / 5,792 B. Adaptive fixes the selected payload;
it does not erase the work already spent preparing Local. Whole Commit includes Capture, Base,
planning and flushes; the report retains its noisy measurements separately.

Further pooling, child interruption, region reuse and trigger refinement require a measured consumer
problem. They remain in the [roadmap](../../docs/DurableGraph-research-roadmap.md#32-list-差分算法选型与设计),
not prerequisites for this byte guarantee.
