# DB-008：Revision 内的 contextual self address

> 历史迁移（2026-10-14）：原路径 `docs/design-branches/0008-revision-contextual-self-address.md`；来源修订 `8ef9fca`，正文保留原时期语义。
> 适用范围：字段局部 Self 的研究证据可复用；same/previous external binding grammar 属于旧 TwoLeg，后继地址方向见 DB-014。 今日事实从 [PROJECT-STATE](../../../src/PROJECT-STATE.md) 续接。

> 状态：Chosen（当前 one-Revision/one-RBF-Frame Working Design）
>
> 创建日期：2026-08-29
>
> 当前方向：`TwoLegRotationProbe` 已用 `ProvisionalRevisionV0` 选择字段级 `Self`；不把临时 record/opcode/layout 提升为正式 wire format。
>
> 2026-09-02：字段局部 `BindSelf` 仍与 [`DB-014`](../0014-multi-segment-backward-file-distance/README.md)
> 相容；本文其余 same/previous external binding grammar 只作为 TwoLeg executable evidence，产品 external
> reference 改由 BackwardFileDistance 表达。

## 问题

one-Revision/one-RBF-Frame 形状中，ObjectVersionDict 会把本 Revision 新写出的
ObjectVersion 安装为 latest binding。此前 Working Design 把这些 binding 编码为完整的
`RelativeFrameTicket`，并在 TailMeta 中再次保存当前 OVD 的同帧 ticket。最终
`SizedPtr.Length` 因而反过来影响自身 VarUInt 宽度，需要 writer 在 append 前求 fixed
point。

检查未来实际使用的 RBF v0.40 接口后，发现这条环并非底层格式强加的约束：

- `Append` / `EndAppend` 返回当前帧的 `SizedPtr`；
- `IRbfFrame.Ticket` 与 `IRbfTailMeta.Ticket` 在读取时提供承载帧的 `SizedPtr`；
- StateStore 还掌握承载帧的 `FileNumber`；
- TailMeta directory 可以用 frame 内 offset 定位 OVD 与各 ObjectVersion record。

因此，当前帧 ticket 已经是 encode/decode context。把它逐项重复写入帧内不是恢复完整
绝对地址所必需的。

## 候选方案

### A. Literal self-ticket + fixed point

所有 OVD value 统一使用 required `RelativeFrameTicket`，TailMeta 也保存当前 OVD 的
self-ticket。

优点是只有一种地址 token。代价是重复信息、更多字节和 fixed-point canonicality。
而且稳定解不必唯一。例如在 frame start 为 4、其余 Payload+TailMeta 为 98 bytes、
只有一个 self token 时，下列两个结果都可能自洽：

```text
assumed width 2 -> frame length 124 -> actual width 2
assumed width 3 -> frame length 128 -> actual width 3
```

所以“迭代到稳定”为 writer 选出一个结果还不够；格式还得规定 least fixed point，并让
reader 验证全局 canonicality。当前没有消费者证明这些复杂度有价值。

### B. OVD 字段级 Contextual Self

保持通用 `RelativeFrameTicket` 语法不变：

```text
0     = None，仅供 optional parent
1     = invalid
>= 2  = required same/previous RelativeFrameTicket
```

只在 OVD binding 字段内使用局部 grammar：

```text
0     = invalid live binding
1     = BindSelf，指向 containing Revision Frame
>= 2  = BindRelative，沿用 required RelativeFrameTicket
```

`BindSelf` decode 后立即归一化成
`AbsoluteFrameAddress(originFileNumber, containingFrameTicket)`；它不进入通用地址类型，也
不在内存 StateMap 中形成第二种 authority representation。`BindRelative` 若解析回
containing frame，必须拒绝，避免同一 binding 有两种表示。

TailMeta 不再保存 OVD self-ticket，只保存 OVD 的 frame 内 offset 和 domain
ObjectVersion directory。TailMeta preview 仍只有 RBF L2 信任，只能作为读取提示；完整
StateMap 必须在整帧 L3 校验成功后安装。

### C. Directory 隐式安装所有同帧 records

把 TailMeta directory 中的每个 domain record 自动解释为 OVD upsert，OVD 只记录删除
和外部 binding。

该方案最紧凑，但会把物理定位目录提升为成员映射 authority，并妨碍 helper、relay 或
未安装 records。当前没有必要付出这项耦合，先不进入原型。

## 当前实验选择

`ProvisionalRevisionV0` 选择候选 B。这个选择只证明下一轮尺寸和轮转实验无需携带
self-ticket fixed point；它不冻结以下细节：

- RBF numeric Tag；
- OVD 的持久 ObjectId 或是否需要保留 ObjectId；
- Base/Delta/Remove/BindSelf/BindRelative 的 numeric opcode；
- record length、排序和 TailMeta directory 的最终字节布局；
- Extent 出现后 contextual self 的作用域。

## 可执行判据

- `BindSelf` 在不同 containing tickets 下解析为各自准确绝对地址；
- Same/Previous external binding 仍按现有 1-bit horizon 解析；
- 显式 external token 指回 containing frame 时 fail closed；
- literal-self 反例证明 fixed point 可能不唯一；
- 同一冻结 workload 在 `ObjectPayloadOnly` 与 `ProvisionalRevisionV0` 中分别形成自洽的
  physical address space，且 logical materialization 相同；
- TailMeta、Payload+TailMeta、frame start 和算术边界在 append 前 fail closed。

这些判据由 `ProvisionalRevisionV0CodecTests`、
`ProvisionalRevisionV0IntegrationTests` 与 V0 policy matrix 覆盖；旧
`ObjectPayloadOnly` golden 保持不变。当前选择只裁决 single-frame contextual self，
没有把 V0 grammar 提升为产品 codec。

## 重访触发条件

- 一个逻辑 Revision 扩展为多个 RBF Frames；
- OVD 与 domain ObjectVersion 不再共享 containing frame；
- 出现必须脱离 RBF ticket context 单独搬运裸 OVD bytes 的消费者；
- 实际 codec 需要独立解析 TailMeta，而不能先获得 containing ticket；
- contextual opcode 的复杂度或尺寸被真实 profile 证明劣于 canonical least-fixed-point。
