# 提交、事件与恢复

[使用指南入口](README.md) · 前置：[应用建模](application-design.md)

## 从 State-only 开始选择

如果只需要保存、重开、浏览和分叉世界，连续 CommitState 已经足够：

```text
S0 → S1 → S2 → S3
```

只有需要保存“发生了什么”、分析输入输出，或在中断后补做已接收输入时，再加入 Event。
DG 允许任意 E/S 排列。Event 是独立保存的领域对象图，不自动执行处理器、不安装 State。

本文后续给出一种可选应用协议，不是 DG 对所有历史的解释规则：

```text
S0 → E1 → S1 → E2 → S2
     已接收  已应用
```

它使用两次独立提交，允许“输入已保存、结果尚未保存”这个状态存在。

## 原位处理协议

先规定以下应用不变量：

- 当前分支由单个处理者串行消费事件；每个事件有业务 Sequence 或其他明确关联标识。
- State 保存最后成功应用的进度，进度与业务结果一起提交。
- 上一个事件的 State 保存成功前，不接收下一个需处理的事件。
- Apply 只做内存领域更新；不执行网络、文件或进程副作用。
- 提交期间 State/Event 图保持稳定；应用失败后不继续使用可能已改坏的工作图。

在这些约束下，流程可以写成如下伪代码。`Validate`、`ApplyInPlace`、
进度字段和故障处理属于应用；这里不定义新的 DG API。

```text
Validate(state, event)             // 不修改；拒绝常规非法输入
working.CommitEvent(event)        // 输入先保存
try:
    ApplyInPlace(state, event)    // 原位更新
    state.LastApplied = event.Sequence
    working.CommitState()        // 结果和进度一起保存
catch:
    停止当前处理
    废弃本次可变图和受影响资源，重开后按保存结果判断
    不透明重试当前操作
```

CommitEvent 本身失败也走停止、检查发布结果和重开的路径。
预检可以减少非法事件进入历史，但不能证明后续更新绝不会抛异常。
若希望记录被拒绝的输入，可以另存审计事件；不要把它自动放入“必将应用”的序列。

这种写法不需要先复制整份 State 来做预演。
如果应用选择先计算候选再保存，也应明确它的复制成本、事件含义与失败边界。

## 恢复依据是应用进度

OpenWorkingCopy 恢复最近已保存 State，即使当前 BaseCommitId 指向 Event。
StateSourceCommitId 表示这个 State 来自哪里；DG 不会自动重放尾部 Event。

在上述严格交替协议中，正常运行最多留下一个尚未应用的尾部事件。
恢复可以固定 end 与 StateSourceCommitId，枚举该范围中的 Event，
校验业务序号，只更新 State 并 CommitState，不再次 CommitEvent：

```text
加载最近 State
固定当前历史终点和 State 来源
读取范围内 Event
验证事件类型、序号和业务阶段
应用未处理事件，连同进度保存 State
然后才继续业务 I/O
```

当前入口为 `EnumerateEvents(end, HistoryOrder.OldestFirst, afterExclusive: source)`、
`LoadEvent(commitId)` 和 `CommitState()`。
先固定范围，再追加恢复提交，避免查询边界随恢复过程移动。
完整 API 用法与可运行消费者见
[根 README](../../README.md#事件快照与失败恢复)和
[EventHistoryRecoveryConsumer](../../experiments/PackageConsumerProbe/EventHistoryRecoveryConsumer/README.md)。

**此范围算法依赖上述严格协议。** 若允许先积压多个 Event，再分批保存 State，
新的 State 可能只处理了其历史位置之前的一部分事件。
此时只扫描 StateSourceCommitId 之后会漏掉未处理项；需要以应用进度保存的事件位置或队列为依据。
不要把通用 `… E E S …` 邻接关系解释成“前面的事件全都处理完了”。

若 Apply 每次都在同一事件上失败，重启不应无限循环重做。
应停止并报告未处理事件，或者进入应用明确定义、可审计的拒绝/补偿流程。
Schema 升级也不替应用决定如何跳过输入。

## 分清发布结果、资源健康与内存状态

| 要判断的事 | 判断依据 |
| --- | --- |
| 发布是否成立 | 正常返回，或 RepositoryPublicationException.Outcome 的 NotPublished / Unknown / Published |
| 当前 Repository 是否可继续使用 | IsFaulted 与资源生命周期 |
| 内存图是否仍满足业务不变量 | 应用更新过程；DG 不回滚领域修改 |
| 请求在业务上是否完成 | 重开后的 State 进度、关联 ID 与所需历史 |

NotPublished 不保证 Repository 健康，也不证明领域图没改过。
Published 可以伴随安装或结果交付失败；没有收到成功返回不等于保存没有生效。
更早的失败还可能直接传播原始异常。
准确合同见 [RepositoryPublicationException](../../src/DurableGraph.Persistence/RepositoryPublicationException.cs)。

一个小应用可以保守地在更新/保存异常后停用整个会话，关闭并重开。
仅业务更新失败且 Repository 健康时，也可设计更窄的丢弃工作副本流程，
但必须重新取得领域图、重建派生状态，并让所有持有旧图的组件放弃旧引用。
Dispose 不保存，也不撤销原对象上的修改。

打开失败时报告错误；不要自动覆盖存储目录、退回空 State 或静默换旧版本。
这些行为会把真实故障变成看似正常的新会话。

## Event 的快照与身份

CommitEvent 独立保存它当时可达的完整对象图；它不保存无关的 State 修改，
也不推进 State 的比较基线。
Event 可以引用 State 中的对象，但当前实现会将其内容独立保存，不能据此获得跨图共享身份。

热执行中的原 Event 对象仍可能引用可变角色。Commit 后继续修改角色，会影响这个内存 Event
所观察到的值，虽然磁盘上的 Event 已经保存了旧值。
为了让热处理与冷恢复有相同输入，推荐 Event 使用稳定的业务 ID、标量事实和必要的自有快照。
只读字段不能冻结其引用的 List、数组或可变元素。

Event 引用整个世界，会完整保存那个可达世界。小型自有 Event 更容易控制成本；
不要把全 State 塞进每个事件来代替 State 提交。
独立 Event 的含义、成本与测试证据见
[DB-097](../design-branches/0097-independent-event-graphs/README.md)。

## 外部 I/O 由已保存阶段驱动

Agent 或其他工作流可以把阶段设计成：

```text
Ready → 保存 Started/相应 State → 外部 I/O
      → 保存 Result/相应 State → 下一步
```

只在必要的状态提交成功后开始下一项副作用。
I/O 成功但结果尚未保存时崩溃，会留下结果未知的操作；
需要应用决定查询外部结果、使用幂等标识重试、暂停人工处理，或明确接受重复执行。
保存了结果的操作应能直接继续后续阶段，而不重复执行。

将 operation ID 与本次 attempt ID 分开，可能有助于描述重试；
是否需要两个标识取决于真实恢复策略，不必预先引入复杂执行框架。
无论采用哪种标识，DG 都不自动赋予 exactly-once 保证。
取消等待也不一定表示远端请求或本地进程已经停止。

## 演化与恢复一起设计

已保存 State 的恢复不重新运行旧业务处理器，但仍要保留所需 reader/Upgrade。
已保存、尚未应用的 Event 则需要能够被当前处理程序正确解释。
升级应用时应同时决定：旧 State 如何转换、旧事件如何读、未完成操作采用什么语义。
若要求严格复现实验轨迹，还应固定相关规则、模型、配置和运行环境。

Event-first 是有效的 DG 用法，首次 State 前 WorkingCopy.State 可以为 null。
应用要定义初始化协议；不要为了规避这个状态制造无意义的占位世界。
本篇严格交替例子为了简化说明从 S0 开始，不限制 DG 本身。
