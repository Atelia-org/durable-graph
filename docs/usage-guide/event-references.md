# 正文单次保存与 EventRef

当一个事实已经保存为 Event，而当前 State 只需要选择或使用它时，可以让 State 保存 `EventRef`，避免把长正文再次纳入 State 图。模型输出、工具结果、用户输入和长配置是典型例子。设计及限定验收统一见 [DB-099](../design-branches/0099-event-references/README.md)。

## 接入方式

`EventRef` 位于 Persistence 程序集、`Atelia.DurableGraph` 命名空间，是普通 durable inline 值。它包含准确 Event commit 的位置，不包含目标对象、泛型类型参数或 Repository。

```csharp
// 在打开 Repository 前登记应用模型以及外部 inline 定义。
var models = new StateModelRegistry();
Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
models.Register(EventRef.Definition);

// 应用先校验业务转换，正常发布 Event 后取得引用。
CommitInfo receipt = working.CommitEvent(fact);
state.Fact = EventRef.FromCommit(receipt);
working.CommitState();

// 显式读取；每次调用独立物化，泛型只检查返回根类型。
var restored = repository.LoadEvent<MyFact>(state.Fact);
```

其中应用领域类可以直接声明 `[DurableField(1)] public EventRef Fact;`，也可在 nullable、数组、List 中使用它。登记引用定义不等于登记目标 Event 的模型；要读取旧 Event，仍需保留相应 reader/current model/升级规则。

正常返回的 Event 提交回执可用于 `FromCommit`。已有外部 `CommitId` 先通过 `repository.ReadCommitInfo(id)` 查询描述，再签发引用；位置编码或描述符都不证明正文健康。不得猜测未来 ID，也不得在提交抛出异常后拿候选 ID 继续更新 State。应用 E/S 两次提交如何恢复见[提交、事件与恢复](events-and-recovery.md)。

`default(EventRef)` 是可持久保存的未绑定值，`IsBound` 只检查位置编码。读取未绑定值会拒绝；哪些业务阶段必须绑定由应用负责。`FromCommit` 拒绝 State 角色，实际 `LoadEvent` 再校验准确位置、Event 角色、目标准确历史 Schema 与完整图。引用不自动跳到最新版本。

## State 保存什么，应用缓存什么

State 保存引用、选择和进度。例如工具批次保存响应引用、调用序号与结果引用；它不再重复保存响应 JSON 中已有的长调用参数。序号是应用对该准确响应的选择规则，未来升级也须维护此语义。

Capture、差分和 Base 重写仅处理引用的标量字段，不读取目标。State 本身可以冷恢复，但其内容依赖尚未证明可用；模型/工具启动前应解析并校验本次操作需要的事实。结果提交还需要的设置和调用事实，应随操作快照保留到提交完成，避免外部副作用发生后再因缓存逐出触发依赖读取失败。

缓存由应用 owner 管理。建议按准确 `EventRef` 缓存完整、固定、不可变的事实投影，使用时再选择子项。若缓存局部结果，键还必须包含投影种类和选择项。命中也须检查业务种类、操作关联与索引；不能只在首次读取时验证。

缓存须有驻留预算、可逐出和清空，并在使用前检查 owner 的关闭、失败和重入状态。不得把 `CommitEvent` 的任意可变原参数当成已冻结的事实。对外返回独立快照，或严格保证投影不可变；读取缓存对象并修改它不会修改历史 Event。缓存预算不代表请求或进程的总内存上限。

这些策略目前属于应用经验，不是 DG 的通用缓存框架。库不提供透明 `.Value`、恢复时自动注入 resolver、Hydrate 内读库或跨图对象共享。没有缓存时仍可正常使用显式读取。

## 保留与适用边界

- 引用只在原 Repository 上解释；同库 fork 保留祖先引用。现有跨库误用检测是概率诊断，不能当作强全局身份。
- State-only 导出不再自足。备份整个 Repository；未来裁剪、迁移或压缩工具必须保留目标及其 Schema/依赖，或显式重映射引用。当前没有外部引用 GC 或自动 pin。
- 本机制消除的是 Event 与引用方 State 的正文重复，不消除不同 Event 之间的相同字符串，也不消除同一 Event 内的冗余表示。
- 完整 prompt 第一次构造仍需读取全部所选事实。引用不降低模型 token，也不代替上下文选择和摘要。
- 只有独立事实才能自然获得此收益。频繁编辑的大块当前状态、没有自然 Event 所有者的内容，应先比较直接 State 保存的成本，不必为了使用引用强造事件体系。
