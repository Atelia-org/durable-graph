# DB-013：按写入温度分片的双层 State segments

> 历史迁移（2026-10-14）：原路径 `docs/design-branches/0013-tiered-state-segments.md`；来源修订 `8ef9fca`，正文保留原时期语义。
> 适用范围：TwoLeg 单层结构性下界与未采纳的 placement 候选；只在 TwoLeg 重启或独立 Hot/Cold consumer 出现时重访。 今日事实从 [PROJECT-STATE](../../../src/PROJECT-STATE.md) 续接。

> 状态：Deferred
>
> 创建日期：2026-09-01
>
> 边界：本文记录单层 TwoLeg 的结构性下界与一个候选产品架构，不是当前 wire format、
> `TwoLegRotationProbe` 实现计划或已接受设计。
>
> 2026-09-02：产品路线已选择 [`DB-014`](../0014-multi-segment-backward-file-distance/README.md)。DB-013
> 只在 TwoLeg 明确重启或出现独立 Hot/Cold placement consumer 时重访。

## 问题

单个 TwoLeg segment 让 latest PublishedRevision 的 current-reconstruction closure 只涉及相邻的
Previous/Current 两个文件。设一个 live 冷对象的完整 Base 大小为 `M`，其 terminating Base 仍在 A；
若要把 A/B 轮转为 B/C，则 A 退休前必须把该对象以完整 Base 写入 B 或 C。因此每个共享的热轮转
epoch 至少承担一次 `M` 的冷数据复制：策略能改变写入时机与峰值，不能消除这项总成本。

这产生两类不同问题：

- 许多预算内容得下的小中型 A debt 长期没有进度，是单层 Rotate/Stay 策略的完整性问题；
- 不可分割的大冷对象同时要求 eventual rotation、单次 Base 写入小于 `M`，且不改变对象边界，三者
  不能兼得。这是单层范式的结构性边界，不应伪装成排序或阈值可以消除的策略缺陷。

## 候选最小模型

若真实 mutable-cold consumer 不能合理放入 ArtifactStore 或独立 Repository，可把一个逻辑
StateStore 按写入温度分成两个同构且都具 authority 的 segment：

```text
PublishedCommit
├─ HotSegmentHead  -> ordinary TwoLeg FileScope + ordinary OVD
└─ ColdSegmentHead -> ordinary TwoLeg FileScope + ordinary OVD

LiveObjectMap = DisjointUnion(HotOVD, ColdOVD)
HotOVD.Keys ∩ ColdOVD.Keys = Empty
```

它不是 cache hierarchy：Cold segment 保存唯一权威状态，不能丢弃后重建。两个 segment 只在轮转
频率与对象归属上不同，复用同一种 Frame、ObjectVersion、OVD、Base/Deltify 和 TwoLeg 算法。

普通 hot-only Commit 追加 Hot segment，并精确复用上一 `ColdSegmentHead`；hot Rotate 只 evacuation
HotOVD 内的 A debt。冷对象只在 Cold segment 自身发生 Save/maintenance/Rotate 时承担复制成本。

## 不可约约束

1. 只有外层 `PublishedCommit` 是 authority；两个 segment head 不得独立发布。
2. 一个成功 Commit 精确绑定一对 `{HotSegmentHead, ColdSegmentHead}`。两边 candidate 都完成 durability
   barrier 后，才能原子发布新 pair；失败或崩溃后只能选择 old pair 或 new pair。
3. 两个 OVD 是同一种类型的两个实例，不建立特殊 `ColdObjectMap` wire 或第二套 map 算法。
4. 一个 live ObjectId 恰好属于一个 segment；加载时 overlap、missing 或 malformed binding fail closed。
5. durable references 继续只保存稳定 ObjectId。加载时先物化两个 OVD 的互斥并集，再解析跨 segment
   shared reference 与 cycle；不把物理 segment 地址泄露给领域对象。
6. `RelativeFrameTicket` 只在所属 segment 的本地 FileScope 内解释，禁止跨 segment ticket。
7. Hot/Cold 各自使用局部连续的 file-number sequence 或显式 previous/current catalog，避免全局交错编号
   破坏 `PreviousFileNumber = CurrentFileNumber - 1`。
8. 外层 head 必须保存每个 segment 的 exact PublishedRevision locator；只有 current file number 不能区分
   同文件内 Revision、orphan candidate 或 authority frontier。

这与 target design 的单一 `CommitManifest` authority 一致。若一次 Commit 同时在 Cold 创建对象 Y，
并让 Hot 对象 X 引用 Y，独立发布 Hot/Cold head 会产生无法解析或 mixed snapshot，不能接受。

## 首个可能的 placement 语义

最小首版只考虑 static placement：

- Type/Schema 上的显式 storage-class Attribute 只为新 ObjectId 给出默认 segment；
- 已发布 Hot/Cold OVD membership 才是现存 ObjectId 的 placement authority；
- 一个 ObjectId 生命周期内不自动换层；改变 Attribute 不静默迁移历史对象；
- Attribute 是物理 placement hint，不自动成为领域 Schema equality 或 SchemaHash 的组成部分；该点需由
  首个真实 Generator/Schema consumer 再裁决。

如果“99% 冷、1% 热”位于同一个 ObjectVersion 内，对象级分片不能拆开它。领域模型必须拆成两个
稳定 ObjectId，或把大型不可变部分改为 exact `ArtifactRef`。把 Attribute 标在普通拥有字段上会遇到
共享引用与循环图的 ownership 歧义，首版更适合标在有独立 ObjectId 的 durable type 上。

## 暂缓的复杂性

- 自动冷热识别、promotion/demotion、residency hysteresis 与多级 cascade；
- 既有 ObjectId 的跨 segment 迁移、重复/缺失裁决与 rollback；
- 跨 segment Base lineage。当前 Base 从 containing Revision 的 prior OVD 查 predecessor，目的 segment
  的 prior OVD 不包含迁入对象；未来可能需要经 parent composite commit 的 map union 查找；
- 两个 append sequence 的正式 crash/reopen、retention、GC 与救援协议；
- hot/cold 不同 policy parameter、负载再平衡与 segment 数量自动变化；
- lazy materialization。物理分片本身只降低轮转写入，并不保证全图 cold load 少读数据。

级联多个层在概念上可由同一模型重复组合，但当前没有消费者证明两层以上值得支付额外 head、map、
publication 与运维复杂性。

## 与应用级 offload 的选择顺序

1. 大型且不可变、可 exact-address、适合 lazy load 的内容优先进入 ArtifactStore。
2. 大型、低频可变，但不要求与热状态原子 Commit 的实体优先进入独立 Repository/StateStore。
3. 只有大型、低频可变实体必须共享 DurableId 空间和原子对象图 Commit 时，才重启双层 State segments。

## 对单层 TwoLeg 研究的影响

若 `TwoLegRotationProbe` 未来重启，单个 segment 内的进度机制应只针对预算可分解的小中型 A debt；
不得声称它消除了大冷对象每个本层 epoch 的复制下界，也不得让不可分割对象无条件绕过写入峰值
envelope。当前没有活跃实现计划。

## 重访触发条件与最小探针

满足以下条件时再进入代码实验：

- 出现必须与热对象保持同一原子 StateGraph、又不适合 ArtifactStore/独立 Store 的 mutable-cold
  consumer；
- 单层策略在该 workload/SLO 上因 `Pworkload`、F 或总写入无法进入可接受区；
- 需要测量偶发 cold update、第二 OVD/materialization 与 composite publication 固定开销是否抵消收益。

最小探针只允许 static placement：两个现有 TwoLeg engine 加一个 in-memory composite head；一次 Save
按固定 ObjectId treatment 分区，未变化 segment 精确复用旧 head，结果 map 必须互斥并集验真。它不实现
Attribute、re-tier、正式 wire 或 durable publication。若测得的收益只与“跳过多少次 cold Revision”线性
等价，且没有跨越明确 guardrail，应停止实验而不继续扩建产品架构。
