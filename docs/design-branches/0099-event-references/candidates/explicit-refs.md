# 候选 A：显式 Event 根引用与应用 reader

> 独立候选，尚未选择或实施。依据：DurableGraph `66f2cf3`（产品 `a8f6ed1`）、console-agent `437a586`。本次只做源码与文档检查，没有运行构建或实验。

## 结论与现状

推荐先让 **Event 持有正文，State 持有指向完整 Event 根的普通 durable 值，AgentSession 显式解析引用**。首个消费者可以完全在应用内验证；如果验证成立，DG 提供同形的非泛型 `EventRef` 与薄 reader 封装即可。不恢复跨图对象身份，不给普通对象引用增加隐式外部含义。

现有 [WorkingCopy.CommitEvent](../../../../src/DurableGraph.Persistence/WorkingCopy.cs) 返回 `CommitInfo`，每次完整捕获独立 Event；[BusinessWorkspace.Capture](../../../../src/DurableGraph.Persistence/BusinessWorkspace.cs) 为其新建 CaptureSession；[GraphRecordStore](../../../../src/DurableGraph.Persistence/GraphRecordStore.cs) 要求所有对象为自有 Base。Event 局部 ObjectId 因此不适合直接成为跨图定位值。[DB-098](../../0098-readpair-retirement/README.md) 已保留独立 LoadEvent/LoadState 与 Checkpoint；引用 reader 可以直接调用这些路径。

消费端 [AgentSession.Dispatch](../../../../../atelia-console-agent/src/ConsoleAgent/AgentSession.cs) 的顺序已经是 Prepare → CommitEvent → Apply → CommitState。[AgentStateMachine](../../../../../atelia-console-agent/src/ConsoleAgent/AgentStateMachine.cs) 把输入放进 SavedObservation，把响应的 SavedAction 放进 Messages，还从响应重新构造 Calls；工具结果同时进入 Results/历史。[SavedMessages](../../../../../atelia-console-agent/src/ConsoleAgent/SavedMessages.cs) 显示正文是输入字符串、SavedAction.Json、工具结果文本及调用 RawInput。**只替换 Messages 会遗漏 Calls 和未完成的 Results。**

## 最小表示：根定位值，不暴露子对象地址

提议 `EventRef` 是 `[DurableType("…EventRef", 1)] readonly partial struct`，三个 `[DurableField] uint Word0/1/2`。它们仅是 `CommitId.TryWrite` 所得 12 字节的逐段小端装箱；复原后交给 `CommitId.TryRead`，不解释 HeadStore 的内部位布局。三个 uint 使用现有编码，不能据此承诺持久 body 恰为 12B；default 在解析时拒绝。

这并非新的机制假设：[BootstrapRecords.ObjectHeadLocator](../../../../src/DurableGraph.Persistence/BootstrapRecords.cs) 已采用相同的三个 UInt32 inline 字段；[RecordConsumer](../../../../experiments/PackageConsumerProbe/RecordConsumer/Model.cs) 有 readonly record struct 的跨进程模型先例。[BuiltinStateValues](../../../../src/DurableGraph/Runtime/State/BuiltinStateValues.cs) 的 UInt32 引用访问为空，因此该方案无需新 TypeTag、外部引用 visitor 或 Capture 特判，State 差分照常比较这些字段。不能复用保留的 `P0H` schema 身份。

reader 提议形状为 `Read<TEvent>(EventRef)`：复原 ID → `repo.LoadEvent(id)` → 检查结果可赋给 TEvent；应用再投影 `.Input`、`.Action`、`.SavedResult`。子载荷不需要 ObjectId、成员路径或任意 selector 协议。确有一根多个结果时，允许应用自己的结果序号；其含义随该 Event schema 管理，不成为 DG 通用寻址格式。

首轮使用非泛型 EventRef。持久 `EventRef<TEvent>` 虽可提供静态类型提示，仍不能证明实际根类型；[ModelExecutionEnvironment.GetTypeExpr](../../../../src/DurableGraph.Persistence/ModelExecutionEnvironment.cs) 会解析全部泛型实参，即使 T 不占正文也引入名义类型登记与闭合依赖。Event 类型改名/删除、泛型历史及 owner 升级还要处理这些依赖。先把类型约束留在 reader 方法与消息描述的具体变体中，避免为 phantom T 支付这笔成本。

若内建 DG，最小产品面是 Persistence 中同形的公开 EventRef、CommitId 转换及 LoadEvent 适配。复用生成器的跨程序集 schema export，并单独登记其 Definition；[DurableSchemaExportAttribute](../../../../src/DurableGraph/DurableSchemaExportAttribute.cs) 明确编译期导出不等于 runtime 登记。无需把内部 bootstrap 定义整体变成用户登记入口。这个包接线仍须普通包实验，本文没有证明它已经可用。

## 校验发生在哪里

EventRef 是定位值，不是不可伪造的能力或“源图已校验”证书。三层职责必须分开：

| 时点 | 做什么 | 不承担什么 |
|---|---|---|
| 本 Session 正常 CommitEvent 返回后 | 用刚返回的 Id 创建引用；本次调用已知发布成功且角色为 Event | 不凭调用方 CLR 对象建立通用可信内容缓存 |
| 导入外部/解码得到的 Id | 工厂可用 ReadCommitInfo 校验存在与 Event 角色 | 描述校验不证明根类型或完整对象图健康 |
| State 保存 | 普通 capture/差分，保存引用数值 | 不递归 Resolve 历史，不扫描全库，不额外发布依赖事务 |
| 首次使用正文 | LoadEvent 完整走现有源图、schema、引用、升级与物化检查，再做类型/业务校验 | 不以 receipt、相等 ID 或热缓存替代首次可信读取 |

任意从另一仓库取得的 CommitInfo 并不自动属于当前 reader；“本次正常返回”是受控调用链中的事实。公开转换只能声明编码/角色结果，不能伪装为仓库来源认证。构造器也不是持久值的校验门，因为恢复不保证执行它。

不让 LoadState 自动触发外部 I/O。冷开后 Session 在生成 prompt、执行工具所需正文之前解析相应引用；解析或业务配对失败就停止该操作。普通元数据查看可以不解析。若需要检查所有依赖，做显式应用校验操作，不塞进每次 CommitState。

schema 版本仍由所选 Event 自己的准确历史视图决定，不能拿当前 State 的视图解释它。[GraphReader.PrepareCore](../../../../src/DurableGraph.Persistence/GraphReader.cs) 既有 normalize/升级路径继续承担恢复；reader 不绕过坏源引用检查。State 改为引用式布局需要新的应用 schema；无旧库兼容负担时可以新建会话存储，不实现把任意旧正文反向匹配到历史 Event 的迁移器。未来 Event 升级由其常规 Upgrade 完成，reader 的业务投影再跟随当前类型调整。

## Agent 端到端与内存边界

State 保留 Version、Phase、计数和高水位等处理进度；Messages 换成输入/响应/工具批次的引用描述；Calls 换成活动响应引用加下一个调用序号；Results 换成结果引用列表。一个结果批次可以保存引用列表来固定分组，正文仍只在各自 Event。模型 JSON 只在 ModelResponseReceivedEvent，调用参数由该响应解析。

Dispatch 的 Prepare 继续在发布前做业务准入，但产出的待应用转换接收一个稍后提供的 EventRef；正常提交返回后 `Apply(ref)` 更新上述描述，再 CommitState。恢复使用枚举得到的原 CommitId 构造同一个 ref，LoadEvent 后运行同一转换。无持久仓库的内存模式可给 Session 自有事件表分配临时键，不要求制造假的持久 CommitId。

投影和 I/O 留在 Session/reader；StateMachine 接收已经解析的业务输入。当前 `AgentState.History`、PendingCalls 等无参数投影需要改到 Session 显式读取，这是真实的应用 API 改动。不能为了保留属性形状，让任意 getter 偷偷打开仓库或给 State 注入持久 reader。

reader/投影缓存由 Session 持有，按仓库实例、CommitId 和投影种类取键；随 owner 关闭、fault 后重开一起丢弃。冷读取按需解析，完整 prompt 首次可能要读取许多 Events；热路径复用已验证内容。缓存不进入 durable 字段，不要求跨 LoadState 共享领域实例。`CommitEvent` 不冻结调用方对象，因此通用缓存不能直接种入提交参数；Agent 可在受控路径缓存自有不可变正文副本，或先 LoadEvent 再缓存。缓存中的可变投影也不能任由调用方修改后成为后续读取结果。

有一个需明确选择的例外：PrepareInterrupt 现会合成失败工具结果，而 TurnInterruptedEvent 只有 Reason。**推荐保留小型合成描述**：结果描述引用原响应与中断 Event，加调用序号、提示种类；固定的应用提示由 reader 产生。此处没有外部观察得到的大正文，没必要为两句提示新增正文持久化。如果要求历史提示逐字不受程序更新影响，则把缺失结果的正文在提交前放进中断 Event；只存新合成部分，不能把此前收到的整批结果再次复制进去。两者都不需要 Event 之间解引用才能恢复一个 Event 根。

## 发布失败、fork 与保留

[RepositoryResources.CommitSaveGroup](../../../../src/DurableGraph.Persistence/RepositoryResources.GroupSave.cs) 在追加 graph 后才发布 Head，并取得最终 CommitId。方案接受这个顺序：不预留 ID，不在 Event 内引用自己，不把候选帧地址当已发布引用。

例如用户输入 E 已返回，进程在 Apply 前退出：重开恢复旧 State，枚举唯一待处理 E，使用 E 原 Id 添加输入引用并保存 State。若退出在 State 发布之后，重开取得新版 Version 与引用，不再重复应用。若 CommitEvent 抛出 Published/Unknown，即便异常有 CandidateCommitId，也不继续 Apply；按 [RepositoryPublicationException](../../../../src/DurableGraph.Persistence/RepositoryPublicationException.cs) 关闭并重开检查真实历史。若 State 发布失败，旧内存图与缓存一并丢弃，仍由实际 head 决定是否重放。该协议不改变外部副作用的不确定性。

同库 fork 继承的 State 中引用保持原 CommitId，reader 不要求 CreatedOnBranchId 等于当前分支，也不增加祖先遍历。首轮允许读取同库已发布 Event；业务是否只接受本分支祖先另由应用决定。跨库引用/导入不在范围：[CommitId](../../../../src/DurableGraph.Persistence/CommitId.cs) 现合同只有概率性误用检测，没有每引用 16B scope。本方案保持该限制，不声称 foreign ID 必然拒绝；路径选择由 Session 管理。若跨库复制成为实际产品需求，再比较一次每库 identity 与每引用 scope，不能悄悄扩张本片。

当前完整帧只追加、没有物理 GC，故第一片无需 pin 表。引用本身不会进入 DG 的对象引用闭包，**State 单独导出不再自足**；备份需保留整个仓库。以后任何裁剪、导出或 compaction 都必须识别这类依赖，否则不得删除被引用 Event。首轮业务 Event 仍自有完整载荷，不放 EventRef 并自动追溯另一 Event；不支持递归跨 Event 解析、循环检测或通用 blob 图。若将来增加这些能力，保留闭包须另立合同。

## 最小可证伪实验与最大弱点

1. 用真实 Agent 的输入、含文本工具调用的响应 JSON、分批工具结果跑新鲜存储；冷开比对 prompt、待执行调用与历史。检查 State 保存图没有这些正文字符串，而不仅比较总文件大小；同时量追加字节、capture 分配与耗时。
2. 对同一 EventRef 重复保存 State、清空热缓存并重开，验证引用值稳定、State 增量仍生效。加入无效/default/State 角色/错误 Event 类型/损坏源图反例；只声明当前合同能证明的跨库拒绝结果。
3. 在 Event 返回后、State 发布前后硬终止，以及注入 Published/Unknown；恢复后引用指向原 Event、Version 只推进一次。fork 后加载继承引用，保存新 State，确认不要求重写祖先引用。
4. 用长历史分别测冷开后完整 prompt、仅最新操作、连续热读取。若逐 Event 打开/升级/JSON 解析成本吞掉空间收益，根引用路线即被否定或需另一个有测量依据的批读切片。额外验证合成提示描述在中断和重开后配对完整。

最大弱点不是编码：它把 State 从自足快照变成逻辑索引，增加显式 reader、冷读次数、历史模型保留和导出约束；State 全图 capture 也不会因此成为 O(1)。没有“既省一份正文又免费冷恢复”的保证。它适合本消费者，因为输入/结果本就有独立 Event，提交顺序也已提供自然的 ID 边界。

可与其他方案融合的最小接缝是根引用值和显式 reader：先用应用 inline struct 证明收益，之后可把同形封装交给 DG；若未来改成专门持久引用槽，可保留应用的消息描述与业务投影；若数据证明整根加载过重，再以具体大 Event 为证据研究子载荷读取。不要同时引入内容寻址、通用懒代理和跨 Event 依赖图。
