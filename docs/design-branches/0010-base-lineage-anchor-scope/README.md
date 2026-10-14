# DB-010：Base lineage anchor 是 per-record 还是 Revision prior snapshot

> 历史迁移（2026-10-14）：原路径 `docs/design-branches/0010-base-lineage-anchor-scope.md`；来源修订 `8ef9fca`，正文保留原时期语义。
> 适用范围：旧 Probe shared anchor 的研究；Removed 与禁止 ID 复用前提不适用产品，不能据此宣称产品 lineage 已验证。 今日事实从 [PROJECT-STATE](../../../src/PROJECT-STATE.md) 续接。

> 历史研究范围（2026-09-06 治理）：下文 Removed → fail closed 与禁止 ID 复用是旧 Probe 的前提。
> 产品已选择 revision-local ObjectId、允许回收复用；不能直接照搬本页 lineage 判定。
> shared anchor 与新占用者 Base/旧链终止如何组合仍待产品分片验证，统一见[路线图](../../DurableGraph-research-roadmap.md)。

> 状态：Chosen
>
> 创建日期：2026-08-29
>
> 裁决日期：2026-08-29
>
> 选择：Base 不保存 direct parent；统一使用 containing Revision 的 OVD parent 作为
> prior-snapshot anchor。Delta 继续保存 exact per-record parent。

## 裁决

当前 single-writer 模型规定：一个 Revision 的全部 changes 都从同一个 accepted pre-save
snapshot 派生。因此每个 Base 的 prior 只能是：

```text
priorRevision = containingRevision.OVD.ParentRevisionFrameTicket
priorObject   = LookupLive(priorRevision, ObjectId)
```

每条 Base 再保存相同 Revision ticket 是重复 authority。当前实现删除这份 per-record ticket，
并将运行时属性明确命名为 `DeltaParentFrameTicket`：Base 必须为 null，Delta 必须存在。

OVD parent 的两个用途保持分层：

- OVD Delta：作为 live-map replay parent，同时也是该 Revision 的 prior snapshot；
- OVD Base：live-map materialization 在 Base 停止，不继承 parent；Base lineage 仍使用它查询
  prior snapshot。

## Base lineage 判定

- containing OVD 无 parent：仅 ordinal 1 Base 可成为 genesis root；
- prior lookup `Found`：只允许同 ordinal/同状态 relocation，或严格 `parent + 1` domain Base；
- prior lookup `AbsentAtBase`：仅 ordinal 1 可成为本 Revision 新建的 root；
- prior lookup `Removed`：fail closed，不把显式删除解释成新对象；
- OVD/anchor missing、non-earlier、cycle 或 malformed：current Base reconstruction 仍停止于
  自身，但 lineage diagnostic fail closed。

## 可执行证据

- 一个 shared anchor 同时覆盖 new Base、domain Base、relocated Base 与 exact-parent Delta；
- canonical AA/BA 通过同一个 B PublishedRevision 分别解析到 A/B exact prior heads；
- genesis、absent V1、absent V2、visible Remove 与 malformed anchor 均有边界测试；
- immediate runtime C 的 logical state、reconstruction 与 lineage 地址序列保持不变；
- model/grammar 拒绝 Base direct parent；provisional Base record 不再编码 `NoneToken` 占位，
  `DeltaParentTokenBytes == 0`；Delta token 规则不变；
- 完整 probe 211/211。

## 明确拒绝的当前能力

shared anchor 不表达一个 Revision 从多个历史 snapshots 拼装 Base。例如：

```text
R0: X1, Y1
R1: X2, Remove(Y)
R2: Base(X2 from R1), rescue Base(Y1 from R0)
```

当前不支持 stale-snapshot Save、branch/multiwriter merge、historical import/rescue restore、
mixed-provenance Base 或 Remove 后复用同一 DurableId。若这些成为真实消费者，再重访 record
shape；未发布原型不保留双语义 discriminant 或兼容层。

## DurableId 边界

`Removed` 只在 prior OVD chain 中仍可见时具有决定性。后续 OVD Base checkpoint 会省略 dead
ID，使更早 tombstone 退化为 `AbsentAtBase`；当前 `_seenObjectIds` 只在 workload producer
中禁止复用。若未来要求 reader 跨 reopen 永久证明 ID 从未复用，需要独立的 monotonic
allocator、epoch 或 retired-ID authority；per-record Base parent 同样不能自动解决该问题。

## 重访触发条件

- branch merge、historical import/rescue restore、stale-snapshot Save 或 mixed-provenance Revision；
- DurableId reuse/epoch 成为产品能力；
- Revision 不再严格派生自唯一 accepted prior snapshot。
