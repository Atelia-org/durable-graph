# DB-015：对象 Base／Delta 的整数阈值与软预算

本问题保留仍在使用的对象表示选择合同。策略是一次保存中的纯函数：输入完整、冻结的 post-live 对象集合及 payload 计量，输出按 ObjectId 升序排列的稀疏写入计划；来源、可达性、成员删除与发布由调用方负责。

## 输入与计量

参数为整数 `α = ReadAmplificationThreshold ≥ 1`、`p = BaseBudgetPercent ∈ [1,100]`。每行有唯一非零 ObjectId 与非负 long 字节数：

| 分类 | B：当前完整 Base | D：当前 Delta | H：已提交重建链 |
|---|---|---|---|
| Insert | 必需 | 不适用 | 不适用 |
| BaseOnlyUpdate | 必需 | 不适用 | 不适用 |
| Update | 必需 | 必需 | 必需 |
| NoChange | 必需 | 不适用 | 必需 |

`null` 表示不适用，已知零值合法。B/D 包含对象 kind、prior、length、typed body，D 的 prior 固定 12B；排除 ObjectId、目录、共享帧及物理 I/O。H 是已选对象链实际消费的 payload 之和，不能用规范重编码长度替代合法冗余输入的实际长度。

`G = Σ B` 包含全部 post-live 行，含必需 Base 与无读动机的 NoChange；已 Remove 对象不参与。策略检查字段形状、重复 ID、负数及 `G`／`H+D` 的 long 溢出，但不能自行证明集合完整或计量来源真实。

## 选择规则

1. Insert、BaseOnlyUpdate，以及 `B ≤ D` 的 Update 必须写 Base，不消耗可选预算。BaseOnlyUpdate 表达没有合法 Delta 或必须重写的更新，不伪造 D/H。
2. 其余 Update 取 `N = H+D`，NoChange 取 `N = H`；仅 `N > αB` 有可选 Base 动机，等号不选。`0/0` 无动机，正数除以零按无穷大排序。
3. 候选按 `N/B` 降序、ObjectId 升序排列。不同候选的比率用 Int128 交叉乘积精确比较，不用浮点或 decimal 舍入。
4. 可选软预算为 `Q = floor(Gp/100)`。按上述顺序取最长前缀，每项花费完整 B；恰好装下可选，首个装不下时停止，不跳过它填充后续小项。
5. 唯一超支例外是排序后的第一个候选：若它独自超过 Q，仍写 Base，然后停止。此前已选的零 B 候选也占据序号，不能给后来的候选同样例外。
6. 被选候选写 Base；未选 Update 写 Delta；未选 NoChange 不写对象内容。最终计划按 ObjectId 升序交付。

阈值与预算乘积使用 Int128，输入、G、H+D 与输出字节数仍保 long 边界。每次调用重新计算 Q，没有余额、债务或跨保存策略状态。费用不是 `B-D`；必需写入、首候选例外及外围格式成本使 Q 不构成总写入硬上限，α 也不保证每个对象的放大率或无饥饿。

## 当前实现与验证入口

- [参数](../../../src/DurableGraph.Persistence/ReadAmplificationBaseBudgetParameters.cs)、[估算行与分类](../../../src/DurableGraph.Persistence/ObjectSaveEstimate.cs)、[纯选择器](../../../src/DurableGraph.Persistence/ReadAmplificationBaseBudgetPolicy.cs)。
- [整数、排序、前缀、首候选、强制 Base 与溢出回归](../../../tests/DurableGraph.Persistence.Tests/ReadAmplificationBaseBudgetPolicyTests.cs)。
- [真实对象记录的计量与分类](../0029-prepared-object-revision-planning-slice/README.md)；默认参数与使用方法只维护在[根 README](../../../README.md#调整保存策略)。

早期 DTO→plan 施工记录及旧 StateStore 接线退出工作树，历史正文可用 `git show 6dbaf0e:docs/design-branches/0015-statestore-object-representation-policy/README.md` 查阅；当时的测试计数、Probe 代理计量不作为当前 FS/VS 实现或性能资格。
