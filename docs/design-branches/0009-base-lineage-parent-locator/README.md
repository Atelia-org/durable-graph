# DB-009：Base lineage parent 是 direct ObjectVersion 还是 Revision locator

> 历史迁移（2026-10-14）：原路径 `docs/design-branches/0009-base-lineage-parent-locator.md`；来源修订 `8ef9fca`，正文保留原时期语义。
> 适用范围：旧 Probe 的 relay-free lineage 裁决；不复用 ObjectId 与相邻文件 topology 的假设不能直接套用产品。 今日事实从 [PROJECT-STATE](../../../src/PROJECT-STATE.md) 续接。

> 历史研究范围（2026-09-06 治理）：本文 Chosen 与实现事实属于所述 Probe，不是产品 lineage 完成声明。
> 下文“Remove 后不复用 ID”的约束已不适用于产品；产品允许 revision-local ObjectId 复用，新的占用者须从 Base 开始并截断旧 lineage。
> shared prior anchor 若进入产品仍须验证该边界；后续工作统一见[路线图](../../DurableGraph-research-roadmap.md)，旧研究正文保留。

> 状态：Chosen
>
> 创建日期：2026-08-29
>
> 裁决日期：2026-08-29
>
> 选择：Base lineage 使用 earlier Revision locator；不写 forwarding Relay。DB-010 随后把
> locator 收敛为 containing Revision 的 shared prior-snapshot anchor。
>
> 竞争方案归档：annotated tag `research/relay-vs-relay-free-20260829`
> 指向提交 `b84620b`。
>
> Scope：该裁决保留 Base lineage 经 prior Revision OVD 查找的语义；文中的 A/B/C bridge、preparatory
> B migration 与 adjacent-file locator 只属于冻结 TwoLeg topology。当前产品文件地址见
> [`DB-014`](../0014-multi-segment-backward-file-distance/README.md)。

## 裁决

选择 relay-free：

```text
C.Revision.OVD.Parent = B.PublishedRevision locator
C.RelocatedBase(Object X)
    -> containing Revision shared locator
    -> LookupLive(B.OVD, ObjectId X)
    -> exact prior ObjectVersion in A or B
```

Delta parent 仍直接指 exact previous ObjectVersion，因为它属于 current reconstruction 热路径。
Base 已含完整值，Revision locator 只在 lineage 导航时解析，不计入 current reconstruction
closure。

`RelativeFrameTicket` 没有跨越 C→A：C scope 只表达 C→B，B OVD 中的 ticket 再以 B scope
表达 B→A。文档中的 `C -> A` 是 locator 解析后的 exact ObjectVersion hops，不是单张跨两代
文件的票据。

## 被拒绝的两个 forwarding 形状

### A0：direct Relay

```text
B.RelayRevision:
    X = zero-payload same-version Delta -> A.X
    OVD Delta(parent = old B head) { }

C.Base(X) -> exact B.RelayRevision/X
```

### A1：Relay + OVD Self + locator

```text
B.RelayRevision:
    X = zero-payload same-version Delta -> A.X
    OVD Delta(parent = old B head) { X = Self }

C.Base(X) -> B.RelayRevision locator -> B.RelayRevision/X
```

这两种方案只为罕见的单对象 lineage 查询减少中间 OVD/Revision reads，却增加 O(N) B 写入、
TailMeta/容量债务、durable 顺序、orphan/reopen 与额外 planner 状态。当前 RBF TailMeta-only
preview 只有 L2 信任，最多作为 routing hint；权威 OVD/ObjectVersion 仍要求完整 Frame 的 L3
验证，所以本裁决不把“小 TailMeta I/O”当作已实现的性能捷径。即便 forwarding 能减少完整
历史 Frame reads，lineage 导航目前主要服务救援与离线分析，尚无 measured bottleneck 足以承担
这些主线复杂度。

## 当前实现事实

- runtime Revision Frame 携带 OVD Base/Delta 与 Self/External/Remove；
- `MaterializeLive(PublishedRevision)` replay OVD chain，产生唯一 live binding map；
- `WorkloadSimulator` 每次 Save 写 runtime OVD，`SimulationRun.StateMap` 只是 replay 后的派生快照；
- `ImmediateRotationPlanner.Create` 不接收 caller StateMap，只从 B PublishedRevision OVD 取 source；
- planner 只产生一个 immutable runtime C evacuation Frame 及其派生尺寸估值，不存在
  RelaySet、RelayRevision 或 B capacity debt；
- `ImmediateRotationAppender` 在所有 source/candidate preflight 后完成 in-memory 首帧 C file registration；
  `MaterializeLive(C)` 是 append 后唯一 StateMap 来源，本切片不发布 StateStore head；
- C OVD 以 B PublishedRevision 为 shared prior-snapshot anchor，evacuated Base 不再保存
  per-record parent；
- 唯一 `InspectObjectLineage` 对 Delta 走 exact parent、对 Base 走 Revision OVD locator；
- Delta 必须有正 payload 且 logical ordinal 为 `parent + 1`；same-version maintenance 只允许
  RelocatedBase；
- provisional domain grammar 只有 Base/Delta，不保留 Relay opcode、reserved value 或兼容层。

canonical AA/BA/BB 结果：

```text
AA: current reconstruction C; lineage C -> A; OVD reads B -> A
BA: current reconstruction C; lineage C -> B -> A; Base lookup reads B
BB: current head remains B; C full OVD binds External(B)
```

## 保留的相邻问题

- preparatory B Base migration 仍可能是 `CanPrepareAndRotate` 的 correctness path；本裁决只删除
  forwarding Relay，不删除分批降低 EvacuationSet 的能力；
- 当前只闭合 immediate one-C-Frame 路径；尚无一般 completion search、publication/reopen 或
  crash atomicity；
- 当前模型明确拒绝 Remove 后复用同一 DurableId；prior OVD chain 中可见的 Remove decisive。
  OVD Base checkpoint 后若要永久证明 ID 从未使用，需要未来独立 ID authority；
- DB-010 已选择把 Base per-record locator 合并到 Revision 的共同 prior-snapshot anchor；
  mixed-provenance import/rescue 是明确重访条件。

## 重访触发条件

只有出现真实 lineage consumer，并测得 OVD/TailMeta point lookup 形成稳定、重要的瓶颈时，才
重访 forwarding/checkpoint 优化。重访时优先比较 OVD-only checkpoint，不自动恢复 domain
no-op Delta。
