# DB-007：自适应双腿轮转与 Rebase/Deltify 策略

> 历史迁移（2026-10-14）：原路径 `docs/design-branches/0007-adaptive-two-leg-rotation-policy.md`；来源修订 `8ef9fca`，正文保留原时期语义。
> 适用范围：冻结的 TwoLeg 控制与评价研究，路线已暂停；产品候选转向 DB-014，旧 progress floor 表述按原实验日期阅读。 今日事实从 [PROJECT-STATE](../../../src/PROJECT-STATE.md) 续接。

> 状态：Deferred
>
> 创建日期：2026-08-28
>
> 更新日期：2026-09-01
>
> 冻结结论：可执行 workload 已观察到 budget-compatible 低放大 A-debt 连续 64 个自然 Save 保持 Stay；
> 在相同 strategy view 持续重复的条件下，pure selector 会 indefinitely stall，这是源码推导而非无限期测量。
> 未实现的后续控制分成 ReadMotive、ReadinessProgress 与 ShouldRotate。候选公式、边界与判别实验见
> [`ADAPTIVE-ROTATION-CONTROL-CANDIDATE.md`（归档）](../../../experiments/ARCHIVE.md#two-leg "原路径：experiments/TwoLegRotationProbe/ADAPTIVE-ROTATION-CONTROL-CANDIDATE.md")。
> 2026-09-02 起 TwoLeg 路线暂停；产品候选转向 [`DB-014`](../0014-multi-segment-backward-file-distance/README.md)。

> Authority boundary：本文保存 2026-08-28 至 2026-08-30 的候选分支与历史探针证据，不是当前
> benchmark baseline 的算法规格。文中 `progress floor`、NoChange-first 与“当前 executable baseline”
> 等表述应按其原实验日期阅读；2026-09-01 修复后的 `ReadAmplificationBaseBudgetPolicy` 已取消无条件
> progress floor，当前事实以源码、测试、`TwoLegRotationProbe/PROJECT-STATE.md` 与策略说明为准。

## 问题

在 current Revision 的 reconstruction closure 至多引用两个相邻文件的硬约束下，能否用同一组
无权重事实量决定：

- ObjectVersion 写 Base 还是 Delta；
- 普通 Save 是否顺带迁移 cold objects；
- 何时 A/B → B/C 迈腿；
- 如何平衡 read、write、storage、lineage 与单次 pause。

当前没有证据表明 `TotalPersistBytes`、Previous-file ratio 或任一单标量是充分统计量；允许最终
不存在唯一 winner。

2026-09-01 的当前裁决只冻结概念分层与 test-local 路线，不宣称新的 Adaptive 控制器已实现。
特别地，大型不可分割冷对象的单层复制下界已拆到 [`DB-013`](../0013-tiered-state-segments/README.md)；
DB-007 只继续研究单个 segment 内预算可分解的轮转进度和时机。

## 已选择的硬约束

1. 新 ObjectVersion 只写入 CurrentFile。
2. published current Revision 的 reconstruction closure 只涉及 Current/Previous。
3. A/B → B/C 时，terminating Base 位于 A 的 live objects 必须在 C 写完整 Base。
4. C Revision 的 shared prior-snapshot anchor 落在 B；Base 不保存 per-record parent，Delta 仍指
   exact parent。
5. OVD 内存使用 absolute address；输出时相对目标 Revision 编码。
6. 约 512 GiB frame-start、RBF one-frame 与 TailMeta 上限是格式 gate，不是调优参数。
7. 当前选择 `CanPrepareAndRotate` 作为成功 Save 的 liveness admission invariant：必须保留有限、
   容量合法的 B Base migration + C evacuation 完成路径。它不是既有格式是否可读的格式定律。
8. 文件被物理删除后不可访问不属于格式需要抵抗的故障模型。

## 待比较策略

- **对象局部成本**：依据 Base/Delta bytes 与当前 reconstruction cost 一步选择；简单但看不到
  shared-frame 与 rotation debt。
- **Previous/Current 压力**：根据 unique reconstruction frames/bytes 判断后腿负担；直观但
  不直接证明 C evacuation 可编码。
- **渐进 cold migration**：普通 Save 在 B 提前写少量 Base，平滑正式轮转写峰；必须证明
  消耗 B 空间后仍收敛。
- **统一边际规划**：比较 Base/Delta/Stay/Rotate 的事实性变化；不得把隐藏权重伪装成算法事实。

## 当前 workload 分区与两阶段规划方向

策略先把 parent snapshot 与本次变更归一化为 `Insert / Update / Remove / NoChange`：Remove 先从
PostLive 删除，之后没有 ObjectVersion placement；NoChange 不属于 foreground workload，但仍参与 A debt、
cold migration、C evacuation 与 Meta ObjectMap/OVD。

首版分别在“假设目标文件可容纳”下生成一个 PreferredStayB 与 PreferredRotateC，再用唯一
whole-candidate estimator 检查地址、Frame/File capacity、closure 与 completion certificate。容量不进入
首版 action 组合优化；两个偏好 candidate 都失败时保守 fail closed，不搜索同一 target 的次优修补，
也不把拒绝表述成一般无解。具体分叉、风险和重访触发见
[`DB-011`](../0011-two-phase-save-planning-and-capacity/README.md)。

## 无权重观测量

- File/Frame：A/B/C tail、candidate start、payload/TailMeta/padding/fence、full-frame reads、cache hit；
- Object：head/Base address、chain depth/bytes/unique frames、Base/Delta bytes、shared-anchor lookup、
  距离上次领域修改/Base 的 Saves；
- Revision：live/changed/unreachable/evacuation/migration counts、OVD/index bytes、B/C capacity、
  completion plan、materialized logical graph equality。

比例与 score 都从原始量离线派生，不能反过来污染 trace 或持久状态。

## Correctness oracle

```text
Materialize(candidate) == expected logical graph
ReconstructionFiles(candidate) ⊆ {Current, Previous}
CanPrepareAndRotate(successful post-state) == true
all frame starts/tickets/layouts are representable
failed plan leaves published state unchanged
```

`CanPrepareAndRotate == true` 必须由实际有限 plan 见证，而不只是检查 C 当前是否放得下
EvacuationSet。caller-selected B migrations 现在可以与 final C rotation 组合成具体 witness，但不是
决策过程；未选中某条完成路径或 bounded explorer 返回 `NotFoundWithinBounds`，都不能解释为一般无解。

未来 reference explorer 必须显式标注边界，并在捕获的小状态上 canonical 枚举 B migration
batches 及 terminal C action，再用同一 runtime OVD/reconstruction/layout oracle 复验。便宜
heuristic 不能自行宣称 completeness，但 explorer 不再作为首个连续策略循环的前置依赖。

## 冻结时的 executable evidence

- deterministic Field/List workload generation 与 replay；Create/Update/Remove 尺寸态连续且禁止
  ObjectId reuse；
- one Revision/one in-memory RBF Frame、exact v0.40 envelope、runtime OVD authority、absolute StateMap、
  symbolic Delta apply 与 current/lineage 分层；
- `ObjectPayloadOnly` 与 size-only `ProvisionalRevisionV0` 两种 accounting scopes；后者计入 domain
  headers、OVD、TailMeta directory、relative VarUInt 与 exact envelope，但不是 byte codec；
- `AlwaysBase`、`AlwaysDeltaWhenLegal`、`ObjectPayloadReadAmplification3` 三条基线；最后一条保留
  StateJournal ratio=3 的局部形状，但不移植 one-object-per-frame overhead；
- relay-free immediate A/B→B/C 已完成 plan、in-memory first-frame registration、`MaterializeLive(C)`、
  reconstruction 与 lineage；DB-010 已选择 Revision shared prior-snapshot anchor；
- explicit caller-selected nonempty A-debt B migration 已完成 plan/append：B 写 same-state、
  same-logical-ordinal Base，OVD Delta 指向 source PublishedRevision；plan 与 append 前都复验全部 live
  reconstruction 和 A/B closure，lineage 不作为 current-state gate；
- `3 x 140,000,000` payload witness 证明 immediate C 与合并两对象 B batch 均失败；两个单对象 B
  batches 后 C 成功，一批后仍失败。`head@B / Base@A` 也已覆盖；
- terminal C exact-sizing discriminator 使用合法最大 Previous ticket（10-byte VarUInt）：mandatory
  payload `268,435,390` 时 External total 恰为 `268,435,428`，加一后溢出；同一加一候选改用
  zero-payload same-state Base + Self 后 total 为 `268,435,427`，padding 后 frame 恰达上限，address
  tokens `21 -> 12`。
- realized step/observed epoch/run 的 test-local reduction 已明确 source/result scope、debt Base payload、
  live-object reconstruction Previous Frames、Current tail/next-start slack 与 append 分项；counterfactual
  final-C 不进入 realized 聚合。两 epoch witness 已验证 `A/B -> B/C -> C/D` 连续性。
- changed A-debt 固定对照在同一三 Update + 一 Create trace 上只切换 Stay-B Update 的 Delta/Base：
  Delta control append `48/48/48/668` B，并在 Rotate 集中写 608 B maintenance records；Base treatment
  append `144/244/344/60` B，把旧 A debt 在前三步清零。三个对象共用 A Frame，故 debt bytes 下降不使
  Previous-frame bytes 同步下降；换腿后 Base@B 又形成新 B/C scope 的 600 B / 3 Frames debt。
- role-disjoint 2x2 将 migration-only `{1,2,3}` 与 changed `{10,20,30}` 等尺寸配对并共置一个 1276 B
  A Frame；两个单轴 treatment 到第三次 Stay 各退休互不重叠的 600 B old-A debt，组合格退休两者并集并
  清零。单轴仍依赖共享 A Frame，组合格才释放；换腿后 Delta+paced、Base+none、组合格分别形成
  600/600/1200 B new-scope Previous debt，且组合格六对象只占前三个 realized B candidate Frames。
- source payload-Frame partition discriminator 用同构的 metadata-only full-OVD anchor 控制 latest A
  authority，并让 split source 保持唯一 accepted chain。相同 logical debt 下，第三次 Stay 的 shared/split
  required Previous-Frame bytes 分别为：Delta+none `1276/1308`、Delta+paced `1276/656`、Base+none
  `1276/652`、Base+paced `0/0`。这证明 object debt 不是 exact coarse Frame pressure 的充分统计量；不把
  OVD lookup、累计 IO 或产品策略输入一并宣称为已解决。

上述 terminal C exact-sizing discriminator 只证明当前 provisional v0 grammar 下 External 不支配 optional same-state
relocation；未实现 runtime optional action，不证明扩大 `CanPrepareAndRotate` 可达集、planner
completeness 或未来 wire format。尺寸裁决始终以 whole-candidate estimator 为唯一 authority，不从
单对象 token 差推导 additive savings。

当前 provisional matrix（`modeled file / final full-frame read`）：

| Workload | AlwaysBase | AlwaysDelta | Local ratio=3 |
|---|---:|---:|---:|
| hot-one/cold-eight | 1864 / 1132 | 1428 / 1396 | 1500 / 1132 |
| fixed-seed mixed | 516 / 176 | 460 / 444 | 460 / 296 |

这些数字只证明 write/read tradeoff 可观测，不选择 winner。

## 冻结时未闭合事项

1. 构造等 Base payload、固定单对象迁移数量的选择冲突：ObjectId-first 选中仍与其他 debt 共帧的对象，
   frame-release-aware 选中独占另一 Frame 的对象；比较即时 exact Frame release、append 与换腿后 debt；
2. 扩充 payload skew、stable hot/cold、burst、size-distribution 与 longer traces，再接入少量明确命名的
   pressure-aware treatments，保留原始事实而不预设总分；
3. 捕获具体 `RejectedUnproven`、`RejectedCapacityUnsearched` 或疑似 heuristic false-negative 后，再建立 small-state bounded/canonical
   explorer；找到的 witness 可证明 true，`NotFoundWithinBounds` 不冒充一般无解；
4. 只有策略结论确实依赖 byte-level 差异时，再做 provisional writer/parser；one-frame 真实容量频繁
   撞墙时才引入 Extent。

尚无真实 workload/SLO 时，不要求用户预填 read/write/pause 权重；当多个不可支配策略必须选择
产品默认值时再请求裁决。

## 最小 workload 矩阵

- 大量 cold + 少量 hot、单 hot 高频小 Delta；
- 持续增长/缩小/振荡、突发批量修改后稳定；
- 大量小 records 共享 Frame、少量大 records 造成 co-read；
- head@B/Base@A 与 head@A/Base@A；
- 接近 frame-start、Payload/TailMeta 边界；
- 连续多次文件轮转。

## 历史证据

旧 direct/Relay/relay+OVD-Self/relay-free discriminator 由 annotated tag
`research/relay-vs-relay-free-20260829` 与 DB-009 保存；阶段性实验细节留在实验簿，不在本 live
branch 重复累积。

## 重访触发条件

- reference oracle 发现 hard gate 不可满足或 heuristic 稳定 false-negative；
- 策略出现明显振荡或稳定 read/write/pause 劣势；
- 真实 RBF read log 提供 cache/locality 数据；
- lazy/partial Load 产生 chain-summary 消费者；
- one-frame Revision 出现真实容量失败。
