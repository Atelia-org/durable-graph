# DurableGraph 质量体检：Runtime 模型、Capture 与容器

日期：2026-10-20。检查基线：`1beca7cf2477d369010b3577b7a795c41dbc45f1`。主要读者：接续调查或施工的 Coding Agent。

## 结论与范围

本包没有确认当前内建类型／SG 常规路径上的高优先级正确性缺陷。以下列出一项有具体执行依赖的合同文档缺口、两项有限维护债；不把它们等同于已复现的数据丢失或默认路径错误。优先处理 R1 的合同明确化；R2 可随诊断小修完成；R3 只在实际修改容器升级时考虑，不建议单独发动重构。

已读根 AGENTS.md、完整 `src/PROJECT-STATE.md`，检查以下接缝：

- `Schema` 的 immutable exact 布局、Nullable child 与 TypeExpr 有限展开。
- `StateBindingContext` 的 exact requirement 闭包、缓存命中复核、升级计划与 invocation context 分离。
- `CaptureSession`／`CaptureContext` 的候选阶段、ObjectId、重入及失败隔离。
- 数组、List、Dictionary 的 owned state、Delta reader/writer、当前投影与 comparer 恢复。
- 对应 runtime/container 测试，以及 `ModelExecutionEnvironment`／registry 的相关绑定入口。

未穷举 List matcher 的全部搜索状态；未独立审阅 SG 发射细节、持久恢复／共享缓存、Repository 发布协议。本包未执行 dotnet，根 build/test 由主线程统一运行；本报告的判定来自源码控制流及现有测试内容，不能称为本轮已经运行通过。

## R1：IStateOps 的关键子 Delta 合同没有写在公共接口上

**类别：已确认的合同文档缺口；附带有据扩展风险。优先级：P2（有限文档／合同工作，无默认数据缺陷证明）。**

### 事实与证据

- [IStateOps 接口](../../src/DurableGraph/Runtime/Binding/StateValueBinding.cs#L14) 对 `ApplyDelta` 没有 XML 合同，对 `PrepareDelta` 也没有说明与 `StateEquals` 的一致性关系。
- [ArrayStateBody.ApplyDelta](../../src/DurableGraph/Runtime/Containers/ArrayStateReader.cs#L93) 校验索引后直接接受子 `TOps.ApplyDelta` 返回值，没有再比较 prior/current。
- [ListStateBody.ApplyDelta](../../src/DurableGraph/Runtime/Containers/ListStateReader.cs#L203) 拒绝没有 patch index 的 CopyAndPatch，但 [实际子 patch](../../src/DurableGraph/Runtime/Containers/ListStateReader.cs#L242) 同样依赖 `TOps` 自己拒绝不变值。
- 对比 [Dictionary patch](../../src/DurableGraph/Runtime/Containers/DictionaryStateReader.cs#L220) 与 [Nullable patch](../../src/DurableGraph/Runtime/State/NullableStateValues.cs#L84)：二者在调用子 ops 后显式比较，拒绝未变化结果。调用者承担多少校验责任目前不一致。
- 内建 ops 实际拒绝 no-op，例见 [Int32StateOps.ApplyDelta](../../src/DurableGraph/Runtime/State/BuiltinStateValues.cs#L245)。[ArrayBodyTests](../../tests/DurableGraph.Tests/ArrayBodyTests.cs#L110) 将 unchanged element 列为坏输入，但该用例使用内建 Int32 ops，因此并未独立证明容器层能检查弱 child ops。
- [DB-049 的 reader 合同](../../docs/design-branches/0049-list-range-delta-and-matcher-trial-slice/README.md#L99) 明确每个 patch 必须实际改变。这里借合同解释现有代码的责任边界，不将历史文档整体当作当前实现事实。

### 具体触发与影响

手写 `IStateOps<int>` 可以实现 `ApplyDelta => reader.ReadVarInt32()`，并对读取到相同 prior 的情况直接返回；接口当前文字没有提示这是违反约定。若此 ops 用于 Array/List，形如“patch index 指向已有元素，子 payload 又编码 prior 值”的 body 会被容器接受；同样的 ops 置于 Nullable／Dictionary 中却被 owner 拒绝。这个控制流结论不需要假定 SG 会发出坏 ops；当前 builtin/SG 路径没有因此被证明有漏洞。

此外，List writer 先用 `StateEquals` 选择 Copy/Patch；如实现者把业务 equality 当成完整状态相等，writer 会直接 Copy，而不会调用 `PrepareDelta` 发现差异。接口只说 compares persistent values，未把与融合 delta 的一致性写明。

### 最小改进与验收

本轮已按主线程协调补 `IStateOps` 的 XML：完整 exact slot 内的持久表示比较；`StateEquals(a,b) == !PrepareDelta(a,b).HasChanges`；`ApplyDelta` 必须拒绝不改变 prior 的子 patch，且不能修改 prior；回调不能依赖调用次数改变结果。并说明 no-change 准备结果不是传给 ApplyDelta 的 child patch。明确这是能力实现者的合同，避免仅给 Array/List 每元素再加比较，增加默认读成本和重复权威。公开签名、持久格式和运行行为均未改变。

如果未来决定统一改为 owner 防御性验证，则应作为单独行为切片：先用上述弱 ops 给 Array/List 写反例，确定所有 owner 统一负责还是所有 child 负责，再改行为；不应本轮顺手扩大。

验收：文档更新只核集成 diff 与签名／实现保持；涉及行为时运行 `ArrayBodyTests`、`ListBodyTests`、`NullableStateBodyTests`、`DictionaryBodyTests` 和新增弱 child 反例，并保留位级浮点／decimal／DateTimeOffset 表示测试。

## R2：Capture 准备失败诊断省略了可直接取得的对象身份

**类别：已确认的诊断维护债。优先级：P3。**

### 事实、触发与影响

[CaptureSession.ValidatePreparation](../../src/DurableGraph/Runtime/Capture/CaptureSession.cs#L177) 已持有完整 `current`／`prior`，但布局变化、数组 shape 变化、string 身份变化、缺 preparation、binding 不稳定等异常均只给通用文字，没有 ObjectId、对象 kind 或 exact 布局信息。

例如替换了模型 preparation 实例后继续准备同一已接受对象，会在“An existing object changed preparation binding”处失败。包含大量对象时，仅有异常栈不能区分是哪一个对象或哪一类模型；调用方需要重建每对象比较目录或调试内部循环。该检查本身正确，问题是已知诊断上下文被丢弃。

### 最小改进与验收

只在这些异常消息中补 `current.Id`、kind，以及有关的 source/target 类型／版本；shape 错误补两个 shape。不要为此加入日志系统、异常层级或 public inspection API，也不要包装／吞掉用户 body 异常。

验收：沿用 [CapturedGraphPreparationTests](../../tests/DurableGraph.Tests/CapturedGraphPreparationTests.cs) 的布局、binding、失败后重试与重入测试；若加断言只检查稳定的 ObjectId／类型字段，避免整段英文消息锁死实现。本轮未修改这些错误路径。

## R3：Array/List 的升级预绑定流程是同一正确性规则的两份实现

**类别：已确认的局部重复；未来维护风险，非现存功能错误。优先级：P3／有触发再做。**

### 事实、影响与边界

对比 [PrepareArrayUpgrade](../../src/DurableGraph/Runtime/Binding/StateBindingContext.ArrayUpgrade.cs#L33) 与 [PrepareListUpgrade](../../src/DurableGraph/Runtime/Binding/StateBindingContext.ListUpgrade.cs#L33)：收集两端 inline requirements、BindSchema、解析两端 StateType、exact 不同则选规则并 PrepareValueUpgrade、完整 requirement 预检与 scope 收集、反射闭合 typed factory，几乎逐句对应。两份代码当前都保留缓存命中 requirements 复核和空容器工具预绑定，不能把重复直接等同于遗漏。

如果下一片修改规则选择、Nullable lifting 或 exact requirement 传播，维护者必须同步修改两处高风险顺序；Dictionary 的局部 `PrepareSlot` 又有同类判断。真正容易漂移的是“回调前已经验证什么”，而不是容器 Allocate/Hydrate 的形状差别。

### 最小改进与验收

仅在下一次实际触碰这一规则时考虑提取一个 private 的“绑定两个完整槽及可选规则”的 helper，返回现有 ValueUpgradePlan／StateType 并向当前 requirements builder 收集证据。Array/List/Dictionary 继续各自拥有 plan、shape/count/comparer 和 invocation context，不建立通用容器 owner 框架、不新增接口或程序集。

验收必须保留 [ArrayUpgradeTests](../../tests/DurableGraph.Tests/ArrayUpgradeTests.cs)、[ListUpgradeTests](../../tests/DurableGraph.Tests/ListUpgradeTests.cs)、[DictionaryUpgradeTests](../../tests/DurableGraph.Tests/DictionaryUpgradeTests.cs)、[CompositeDictionaryUpgradeTests](../../tests/DurableGraph.Tests/CompositeDictionaryUpgradeTests.cs) 的空容器预检、错误依赖阻止首个 callback、缓存命中复核、共享容器一次 Normalize 和失败 prior 不变等故事。建议先用这些故事形成跨三种 owner 的核对清单，再做小范围 helper 提取；没有实际修改需求则保留当前代码。

## 已核实的关键保护与不应重开的限制

- [CaptureContext.Seal](../../src/DurableGraph/Runtime/Capture/CaptureContext.cs#L157) 在每次 callback 后再检查阶段；callback 捕获非法重入异常也不能继续交付候选。AbortBuild 释放 pending，而 session ID cursor 保持单调。Prepare guard 则在 finally 恢复，准备失败不安装候选。
- [ExactSchemaRequirementSet](../../src/DurableGraph/Runtime/Binding/StateBindingContext.cs#L191) 遍历 base/inline／Nullable inline，按完整 exact key 去重并拒绝冲突；[requirement scope](../../src/DurableGraph/Runtime/Binding/StateBindingContext.Requirements.cs#L45) 只把完成的 child scope 交给 parent。缓存命中仍复核，不能为了缩短代码删除这些步骤。
- Array/List/Dictionary upgrade 各自先绑定工具，空容器也不绕过；typed invocation context 不驻留 ObjectId 到 snapshot。保留有限类型与直接 source→target 容器升级，无需自动寻路。
- Dictionary 按 canonical key bytes 寻址，lookup comparer 不承担 DTO 配对；[DictionaryRestoreComparer](../../src/DurableGraph/Runtime/Containers/DictionaryRestoreComparer.cs) 保留模式 4/5。当前 application comparer 由冻结配置选择、可达恢复时碰撞拒绝、不得依赖尚未 hydrate 的引用内容，均为明确边界，不报作新缺陷。
- DTO equality、List matcher 的额外探测与所有 live Base 的提前准备存在可测成本，但 active context 已明确其来源与重访条件。本包没有新的成本测量，不重复提出缓存或大框架方案。

## 验证及改动记录

- 已完成静态交叉核对；没有编译／测试／独立可执行反例结果，不声称新发现通过运行验证。
- 主线程可用既有 source-mode 配置统一运行 `dotnet build DurableGraph.slnx`，以及上述测试；定向过滤可使用 `FullyQualifiedName~ArrayBodyTests|FullyQualifiedName~ListBodyTests|FullyQualifiedName~CapturedGraphPreparationTests|FullyQualifiedName~ArrayUpgradeTests|FullyQualifiedName~ListUpgradeTests|FullyQualifiedName~DictionaryUpgradeTests|FullyQualifiedName~CompositeDictionaryUpgradeTests`。
- 本包写入本报告，并在主线程分配的路径上仅补 `src/DurableGraph/Runtime/Binding/StateValueBinding.cs` 的接口 XML。公开签名、持久格式和运行行为均未改变；未改 active context。
