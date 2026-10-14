# DB-011：Save 策略规划与容量可行性是否分成两阶段

> 历史迁移（2026-10-14）：原路径 `docs/design-branches/0011-two-phase-save-planning-and-capacity.md`；来源修订 `8ef9fca`，正文保留原时期语义。
> 适用范围：保留旧 TwoLeg preference/admission 及 no-fallback 证据；产品 normal Save 的 target pair 已由 DB-014 方向取代。 今日事实从 [PROJECT-STATE](../../../src/PROJECT-STATE.md) 续接。

> 状态：Superseded（产品 normal Save）；保留为 TwoLeg planning/admission 证据
>
> 创建日期：2026-08-29
>
> 历史原型方向：先在“假设目标文件可容纳”的模型中分别生成一个 Stay-B 与 Rotate-C 偏好计划，
> 再用唯一 exact candidate builder 做地址、单帧、文件尾和 closure 可行性过滤。首版不在同一 target
> 内搜索次优容量修复；两个偏好计划都不可行时 fail closed，并记录为未搜索而非一般无解。
>
> 2026-09-02：[`DB-014`](../0014-multi-segment-backward-file-distance/README.md) 让文件 rollover 与
> Base/Deltify/OVD membership 解耦，产品 normal Save 不再产生 Stay-B/Rotate-C target pair、A-debt 或
> completion certificate。本文件只保存 preference/admission 分层与 no-fallback 的 TwoLeg 证据。

## 问题

统一 per-Save 策略同时面对两类复杂性：

1. **选择问题**：写 B 还是创建 C；Update 写 Base 还是 Delta；是否顺带迁移 unchanged A debt；
2. **物理可行性问题**：candidate 是否越过 RBF Payload/TailMeta、frame-start、RelativeFrameTicket、
   当前 B tail 或 future completion capacity 的硬边界。

若从第一版策略开始就把所有硬容量嵌入组合优化，会把在线 Base/Deltify/rotation 问题迅速扩张成
constraint-aware subset search。若完全忽略容量并直接发布，又可能产生不可编码或不可继续迈腿的状态。

本分叉比较：

- **A. 两阶段**：先求不受容量约束的偏好计划，再 exact-filter；容量修复留到真实拒绝出现后；
- **B. 统一 constraint-aware 求解**：规划过程中只保留当前与未来均有容量证明的候选。

## Workload 与 parent snapshot 的分区

先把 parent PublishedRevision 与本次变更归一化为：

```text
Insert    = parent 中不存在、post-Save 中存在
Update    = parent 与 post-Save 中都存在，领域状态改变
Remove    = parent 中存在、post-Save 中不存在
NoChange  = parent 与 post-Save 中都存在，领域状态不变

PostLive = Insert + Update + NoChange
```

Remove 没有 ObjectVersion 的 Base/Delta/placement 决策。它先作用于 post-Save live set；之后沿 candidate
bindings 和 reconstruction chain 统计时自然不可达。但它仍改变 live-set metadata、debt、full OVD/Map
形状和 read-frame union，不能从 candidate builder 的物理定尺中消失。

NoChange 不进入 foreground workload，却是策略必须从 parent snapshot 获得的显式事实集合：

- NoChange + Base@A：Stay-B 可继承或 maintenance Base；Rotate-C 必须 Base；
- NoChange + B-contained：Stay-B 继承；Rotate-C 可 External 或 optional Base+Self。

## ObjectMap / OVD 的层次

本地检查的 Atelia StateJournal commit `8020bc907fd1fa15611bcad5828b7b2023583e70` 提供了一个可借鉴
形状（`src/StateJournal/Revision.cs`、`Revision.Commit.cs`）：`Revision` 把
`DurableDict<uint, ulong> _objectMap` 固定在 pool slot 0；Commit 对无变化对象跳过写入，对 changed/new
object Upsert 新 head ticket，对不可达 committed keys Remove，随后 ObjectMap 自己通过
`VersionChain.Write` 增量保存。

DurableGraph 产品层可以把 ObjectMap 理解为一个普通增量 durable collection；TwoLegRotationProbe 当前
仍用 runtime OVD 作为 PublishedRevision 的 live-binding authority 和 provisional metadata grammar。
两者在策略建模上可统一为“一个覆盖 PostLive 的特殊 Meta 容器”，但当前不删除显式 OVD，也不把
caller map 升为第二 authority。

## 阶段 1：Unbounded preference planning

输入是 exact parent snapshot facts 与归一化 workload。规划器分别产生：

```text
PreferredStayB
    target = B
    Insert -> Base
    Update -> Base/Delta
    selected NoChange A-debt -> same-state Base
    remaining NoChange -> inherit

PreferredRotateC
    target = C
    Insert -> Base
    Update whose reconstruction touches A -> Base
    B-contained Update -> Base/Delta
    NoChange A-debt -> mandatory Base
    NoChange B-contained -> External/optional Base
```

该阶段计算 object actions、read-frame union、debt 与 write/read/continuation facts，也需要完整 candidate
的尺寸事实来比较偏好；只是暂不把以下边界作为组合搜索约束：

- B 的下一 frame start 是否仍可表示；
- single Revision 是否超过 Payload+TailMeta/TailMeta 上限；
- preferred candidate 是否为目标文件中最优的“可容纳”备选；
- 容量失败后应拆哪一批 B migration 或改哪些 Base/Delta actions。

“假设放得下”不等于把尺寸当零，也不允许第二套近似 sizing authority。理想 seam 是让同一套 grammar
arithmetic 先返回不施加 maxima 的 component measure，再由 envelope validator 产生 feasibility
disposition；这可以是一次分析调用中的两个结果，不要求两套实现。当前 estimator 尚未提供该分层时，
Plan 先产出 canonical actions，Phase 2 再取得 exact whole-candidate bytes 或结构化 overflow。

## 阶段 2：Exact feasibility filter

对两个偏好计划分别构造完整 runtime candidate，并执行：

```text
logical post-state equality
runtime OVD / binding legality
current reconstruction closure
relative address representability
exact Payload / TailMeta / padding / frame layout
target B tail or fresh C placement
concrete CanPrepareAndRotate continuation certificate
```

初始 prototype 的选择规则：

1. 丢弃 exact-filter 失败的 target candidate；
2. 两个都通过：对原始事实向量做 Pareto/明确策略选择，不预设隐藏权重；
3. 仅一个通过：选择该 candidate；
4. 两个都失败：Save fail closed，accepted head 不变。

如果某个 preferred target 失败，但同一 target 下存在未枚举的次优 action mask 可以通过，本阶段允许
保守拒绝。结果应称为 `RejectedCapacityUnsearched` 或等价诊断，不能称 `Impossible`。

## 两阶段方案的收益

- 先验证用户提出的核心分解：全局 target 二选一，再在固定 target 下选择 per-object actions；
- 让连续 Save/多轮转 runner 尽早出现，不被 capacity-aware DP/search 阻塞；
- 正常 workload 大多远离硬上限时，可以直接观察主干策略 tradeoff；
- 容量拒绝本身会提供后续 repair/search 的真实小状态，而不是预想 action space；
- exact filter 继续保证不可编码 candidate 不会被接受。

## 风险与竞争方案 B 的价值

两阶段首版会产生保守 false-negative：

- 最便宜的 Stay-B 超限，但把某个 Delta 改 Base 或减少 maintenance 后可以容纳；
- 最便宜的 Rotate-C 超限，但不同 External/Base+Self 组合可以容纳；
- preferred candidate 本身可写，却没有 future completion certificate；另一较贵 candidate 可以；
- B/C 两个偏好计划都失败，但多批 preparatory B migration 后仍可完成。

若这些拒绝在普通 workload 中频繁出现，或阻碍策略比较，constraint-aware planning 就有当前消费者。
届时可从捕获状态开始做 bounded alternative menu、capacity repair 或 monotone debt scheduling，而不是直接
升级为一般完备 solver。

## 当前不可约约束

- 一个 Revision 的新 ObjectVersions 只写入一个 target file；
- Remove 先改变 PostLive，NoChange 必须从 parent authority 派生；
- whole-candidate estimator 是唯一物理定尺 authority；
- capacity 可以不进入偏好优化，但不能绕过 acceptance preflight；
- failed candidate 不改变 accepted head；
- 没有具体 completion certificate 的 candidate 只能保守拒绝；
- `RejectedCapacityUnsearched`、`RejectedUnproven` 与一般不可行必须区分。

## 冻结时未执行的扩展

同 target capacity repair、bounded alternative menu 与 constraint-aware solver 均未实现。只有 TwoLeg
再次成为产品候选，且普通 workload 出现有价值的 capacity false-negative 时，才重启这些问题。

## 重访触发条件

- `RejectedCapacityUnsearched` 在普通而非极限 workload 中稳定出现；
- 两阶段策略经常选择可写但 continuation 无证书的计划；
- 次优可行 candidate 显著改善 Save availability 或轮转 pause；
- multi-frame Revision/Extent 成为实际需求，使当前单帧 gate 不再是最终边界；
- 正式 wire grammar 改变 candidate size 的非加性结构。
