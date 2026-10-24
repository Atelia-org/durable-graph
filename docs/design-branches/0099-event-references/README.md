# DB-099：Event 引用与事实载荷单次保存

> 状态：2026-10-25 用户批准推荐方案，正在具体实施和验证；尚未完成验收。
> 基线：DG `66f2cf3`（产品变更 `a8f6ed1`），console-agent `437a586`；无兼容性包袱。Agent 固定包尚未切换。

## 要解决的问题与推荐方向

console-agent 将输入、模型响应和工具结果先存为独立 Event，又把正文纳入 State。这里需要解决的是同一事实载荷的重复持久化，而非减少所有内存表示。推荐让 **Event 拥有事实正文，State 保存 Event 根引用、选择和处理进度，应用按需加载并缓存操作所需的投影**。

库的首个增量是可持久化的非泛型 `EventRef` 值及薄读取入口，复用普通 inline/value schema；应用的首个增量是完整替换重复正文的所有权，包括设置、待执行参数和未完成工具批次。暂不增加 TypeTag、透明属性代理、跨图对象身份、通用内容 Store 或领域对象共享缓存。

这使 State 的恢复保持直接读取：加载 State 得到准确引用和进度，读取所需 Event 获得正文；不会为了得到 State 而重放完整事件历史。State 不再是业务内容自足的单图，引用目标成为其内容依赖。

阅读顺序：本稿是推荐方案的唯一入口；[征集约束](brief.md)说明独立任务与评审标准；[评选与复核](selection.md)记录取舍、反例和未解决事项。五份原始候选保留提案身份，不能与本稿混读成累加的实施要求。

实施切片包括库的公开 inline 值与显式读取、独立普通包消费者、Agent 的完整引用迁移及应用缓存、故障/正文排除回归和同工作负载成本对照。库不新增缓存策略、TypeTag 或外部引用遍历；没有旧存档兼容要求。下文尚未更新的“草稿/下一轮”表述保留原设计的实验顺序，完成后以实际验收记录收口。

## 1. 当前能力与本稿新增内容

| 已有事实 | 本稿提议 |
|---|---|
| [CommitId](../../../src/DurableGraph.Persistence/CommitId.cs) 是同库跨重开可复用的 12 字节位置；解码不证明存在或角色 | 一个可作为领域字段保存的 EventRef，封装这些位置字节 |
| [LoadEvent](../../../src/DurableGraph.Persistence/Repository.cs) 精确加载 Event；独立读取使用目标准确 Schema 环境 | 针对 EventRef 的薄入口，以及可选的泛型结果检查 |
| [ObjectHeadLocator](../../../src/DurableGraph.Persistence/BootstrapRecords.cs) 已用三个 UInt32 的 durable inline struct 存位置 | 复用这种值表达技术，使用独立的公开 nominal schema，绝不冒用 `P0H` |
| [Transient](../../../src/DurableGraph/TransientAttribute.cs) 不参与捕获；恢复不执行构造器或 accessor | 应用显式建立读取上下文与缓存，无自动注入或恢复 hook |
| [DB-098](../0098-readpair-retirement/README.md) 已退出 ReadPair 和其 Transient 共享限制 | 不恢复共享规划；缓存隔离根据消费者的实际可变性设计 |

本稿尚未证明公开 inline 值的跨程序集导出、模型登记和新包消费者接线；内部位置值先例不是本提案的包级验收。所有示意 API 名称均暂定。

## 2. EventRef 的最小语义

### 2.1 一个位置值，定位已提交的 Event 根

- 只指同一个 Repository 中准确的 Event commit 根，不随分支 tip 移动，不搜索“最近 Event”。
- 内部以 `CommitId.TryWrite/TryRead` 为唯一位置编码边界。三个 UInt32 按明确小端规则装箱公开的 12 字节，不解释 HeadStore 内部位布局。DG 的字段编码还有自身开销，不承诺总字段正文恰为 12 字节。
- 普通 readonly partial struct，独立 `[DurableType]` 与三个明确字段 ID；比较和 hash 只取位置值。没有 Repository、缓存、目标对象或 CLR 类型名字段。
- 使用非泛型持久类型。泛型读取 `LoadEvent<T>` 是返回类型检查，不把目标 T 嵌入引用字段的持久 Schema。异构 Agent 历史本就需要消息变体；首片没有证据需要为 phantom T 引入目标类型闭合依赖。
- `default(EventRef)` 表示未绑定，读取时受控拒绝。普通 inline 捕获可保存这一数值，不能依赖构造器替恢复做验证；业务模型中哪些位置必须绑定由应用准入校验。可选字段应选一种明确的缺省表示，不同时堆叠多层空值语义。

概念 API：

```csharp
// 提案示意，并非当前 API。EventRef 内部只有持久值。
EventRef reference = EventRef.FromCommit(eventCommitInfo);
ModelResponseReceivedEvent fact = repository.LoadEvent<ModelResponseReceivedEvent>(reference);
```

`FromCommit` 检查回执角色为 Event，转换准确 Id，不读取正文。本受控调用链刚完成的正常 CommitEvent 回执可直接使用；历史查询得到的 CommitInfo 也可转换。它不是防伪凭证，更不能证明回执属于随后选中的 Repository。需要从外部位置导入时，先走已有 `ReadCommitInfo` 检查准确位置和角色。首片不增加第二套发布回执或预留地址协议。

建议由 Persistence 程序集交付值类型及读取适配，继续保持当前程序集依赖方向。普通模型需要它的 schema export 和单独可登记 Definition；不把 bootstrap 员工定义整体开放给应用。不为此拆程序集或增加任意用户 codec 注册框架。确切登记入口由首个包消费者实验收敛。

### 2.2 捕获不解引用

`EventRef` 的字段只包含标量。Capture、diff、Base、Delta、容器编码都处理这些标量，不访问目标 Event，亦不访问缓存。State 历史有 N 个引用仍可能有 O(N) 的捕获／比较成本；本提案消除载荷重复，不承诺 O(1) 保存。

缓存是否加载、被清空、或同一事实被用于多个消息位置，不改变持久引用的字节语义。State Base 可以重写引用表及包装对象，不能把目标正文带回来。普通图内 `ObjectReference` 的现有完整目录校验保持，不能用 EventRef 替它绕过图内引用约束。

这种 ordinary inline 表示不会自动提供外部强引用完整性、pin 表或依赖枚举。库允许它像其他值一样出现在 State 或 Event 中，不新增按根角色扫描并禁止的机制；首次 Agent 接入只需要 State→Event。含 EventRef 的 Event 仍能独立恢复为“含位置的记录”，但其业务内容可能需要外部依赖。物理图自有完整与业务依赖闭包完整必须分开描述，库不自动递归展开引用。

## 3. 读取、验证与缓存

### 3.1 各阶段的证明范围

| 阶段 | 必须做的事 | 不作出的保证 |
|---|---|---|
| 引用签发／导入 | 受控正常提交回执，或明确查询准确位置与 Event 角色 | 不证明任意目标正文健康、T 可赋值、跨库强身份 |
| owner 的 Capture/保存/恢复 | 正常 inline/schema 编码与本图校验 | 不重读全部历史 Event，不认证所有外部目标 |
| 显式 LoadEvent(ref) | 复原 ID；复用现有准确位置、角色、完整源图、Schema/升级与物化检查 | 不换成当前分支的其他 Event，不执行业务处理器 |
| 泛型读取和应用投影 | 检查恢复结果可赋给 T；核对业务事件种类、操作关联与选择项 | 泛型参数不是发布时事实的证明 |

目标使用自己的准确历史 Schema view；当前 State 的 Schema 不能替它解释数据。沿用普通升级/模型登记规则；缺 binding、源图损坏、升级或类型失败均为明确读取失败。普通 State 加载成功，只证明 State 自身完整，不证明所有引用目标可用。Agent 在启动外部模型/工具之前必须完成该操作依赖的解析与校验。

不新增“每次 CommitState 检查所有引用目标”的隐藏遍历。需要全面健康检查时是显式应用操作。事件图的现有坏源验证也不能被热回执或类型转换绕过。

### 3.2 库独立物化，应用缓存操作视图

首片 `LoadEvent(ref)` 保持独立物化语义，不默默改为同一 mutable T 的全库缓存。`EventRef.Value`、自动打开仓库、Hydrate 内回调解析和全局绑定均不进入首片。

AgentSession 持有一个仓库寿命内的按需投影缓存：引用 → 设置／用户消息／模型动作／工具结果。缓存可以位于 Session，或作为应用受控的 Transient；没有 ReadPair 后，两者都可行。推荐 Session 所有，以便统一 fault/Dispose 和纯内存模式。缓存项不可把可变集合直接交给任意调用者修改；可持有私有不可变正文和解析结果，给请求分配必要包装或独立副本。

推荐每个 EventRef 缓存该事实的一个完整、固定的不可变投影，再从中选择调用或结果；若缓存局部投影，键必须另含投影种类和选择项。不能以同一个 EventRef 先缓存调用 0，再让调用 1 命中错误对象。命中仍需完成与未命中相同的业务种类、关联和范围检查，不能因曾用宽类型读过就跳过后续窄类型检查。

缓存必须可清理并有应用指定的驻留预算；Reset/上下文选择变化时释放失去用途的项，超过预算时逐出可重建投影。命中次数保证只针对仍驻留的项，不能同时承诺有限内存和所有历史永久只解析一次。正在构造的请求持有自己的有效快照，逐出不破坏它；缓存预算不是整个进程峰值内存上限。具体预算和估算方式由 P6 的真实载荷测量确定，不在库中预置全局缓存策略。

通用库不能把 CommitEvent 的原参数直接作为已保存事实缓存：保存不冻结该对象。Agent 热路径可以使用与刚提交内容一致的、由本同步调用链拥有的不可变投影，例如同一 JSON string 和复制后的只读文本块；这是应用证据，不升级成任意 T 的库保证。第一轮实验同时比较热种入与冷读取的内容。若无法证明投影独立性，先读回再缓存。

构造器不执行意味着缓存初始为空，恢复后由宿主建立上下文。Repo 操作仍串行，禁止在 Capture/Hydrate/升级回调内触发解析。Session fault 或关闭时丢弃缓存与解析器；已有脱离 owner 的不可变消息快照可继续作为值使用，未加载引用要重新绑定到打开的正确仓库。首片不为已有同步 API造 async lazy 或新 prepared-DTO cache。

所有仍绑定 Session 的 view/读取入口必须在查缓存前统一检查 owner 可用性及应用重入守卫；命中不能绕过关闭、fault 或正在更新的限制。已外泄的旧 view 不自动绑定新 Repository。独立物化隔离以 SG 或遵守 allocation 契约的手工绑定为前提；当前 [GraphReadContext](../../../src/DurableGraph.Persistence/GraphReadContext.cs) 只守卫一次操作，不能据此承诺阻止任意手工 singleton 工厂跨独立读取复用对象。本片不增加全局分配登记。

这能让按需使用的第二次读取复用应用投影。若下一次请求仍发送完整历史，冷启动第一次构造 prompt 仍需读取全部所选 Event；lazy 不减少模型 token，也不自行完成上下文压缩。

## 4. console-agent 的完整落点

依据 [AgentState](../../../../atelia-console-agent/src/ConsoleAgent/AgentState.cs)、[SavedMessages](../../../../atelia-console-agent/src/ConsoleAgent/SavedMessages.cs) 和 [状态机](../../../../atelia-console-agent/src/ConsoleAgent/AgentStateMachine.cs)。以下是业务布局草图，具体 C# 命名不冻结。

| 内容 | 唯一正文归属 | State 的持久表示／读取方式 |
|---|---|---|
| Settings，特别是 Instructions | SessionStartedEvent | SettingsRef；Session 冷开按需读取，Reset 保留引用。配置也可能很长，不能默认可以双存 |
| 用户输入 | UserInputReceivedEvent | 输入消息条目持有 EventRef |
| 模型 JSON、文本、reasoning、调用参数 | ModelResponseReceivedEvent | 响应消息条目持有 EventRef；只有 Completed 响应进入对话历史 |
| 待执行 Calls | 上述模型响应 | PendingResponseRef + 调用进度；读取响应中的调用，不再保存 RawInput |
| 已收到工具结果，含未完成批次 | 各 ToolResultReceivedEvent | 结果引用列表；完成后将同一批描述转交 History |
| 未执行／结果未知的合成提示 | 应用提示规则，无外部大载荷 | 原响应引用 + 调用序号 + 明确提示种类；不把真实结果再复制进中断 Event |
| Phase、Version、操作序号、计数、结果状态、进程高水位 | 当前 State | 直接保存必要的小标量；不要求每个字段都必须引用 Event |

调用选择使用**该准确响应中的顺序号**，其顺序语义由 Agent 的 Event 模型负责；不是 DG 子对象地址。不能把任意路径或未验证唯一的 CallId 当通用定位。解析后仍验证调用关联，索引越界拒绝。未来 Event 升级必须保持这一顺序语义或同步设计显式转换；不声称任何列表下标跨任意升级都稳定。

中断提示第一版保存小型描述并由明确模板渲染，不能承诺未来程序改词后历史逐字不变。若产品要求逐字冻结，最小变化是在 TurnInterruptedEvent 中只保存**新生成的**提示正文，State 引用它；不重复包含此前收到的长结果。这项产品取舍不阻塞 EventRef 值机制。

StateMachine 继续无 I/O。Session 在 Prepare 前解析本次操作所需的设置／活动响应，并把准备好的输入交给纯转换；构造 prompt、查待执行调用的完整视图归 Session/driver。不要为维持现有 `State.History` 无参数属性而隐式注入读盘副作用。

实际迁移包含 Program 的启动设置读取、Dispatch 外的 NextOperation，以及模型请求构造，而非只改状态机内部。Uninitialized 可以没有 SettingsRef；ReadyForModel/ReadyForTool 所需引用必须绑定并在外部调用前验证。开始调用时取得的设置、活动调用与必要关联投影，要在该操作上下文中保留到结果提交，避免工具已执行后因缓存逐出又重读旧引用而丢失已知结果。

### 4.1 发布与应用顺序

```text
取得并校验本次转换所需依赖
Prepare(state, event)                 // 不修改 State、不发布
receipt = CommitEvent(event)          // 正常返回后才有可用的新位置
reference = FromCommit(receipt)       // 纯位置转换
prepared.Apply(reference)            // 装入引用、更新进度；Version 最后写
CommitState()                        // 只捕获状态与引用
```

已解析投影可以随 Prepared 保留到 apply，缓存装入不能改变持久化语义。绝不在 Prepare 阶段预猜位置，也不为拿 ID 提前发布一个尚未校验的 Event。

恢复唯一尾 Event 时，用历史已有 CommitId 及 LoadEvent 结果运行同一 Prepare/Apply；不新造 Event，不为引用另提交一次事实。Event 提交结果不明时不使用候选 ID 继续 apply；沿用现有停止、关闭、根据真实 head 重开规则。

恢复保持现有顺序：先固定 State source/tip 并最多读取两个尾 Event 描述，超过一个立即拒绝；只有尾形状合法，才加载该事件及本次转换实际需要的外部依赖。不能先预热全历史而丢失这一准入边界。没有尾事件时，冷开可以只交付引用与进度；进入某项操作前再确保依赖完整。

依赖读取由 Session 的受控入口包住；错误位置、角色、类型、坏载荷、缺模型或投影关联失败使该 Agent Session 停用，并以持久化／依赖失败类别向外传递。不得作为普通执行异常落入现有 [AgentLoop](../../../../atelia-console-agent/src/ConsoleAgent/AgentLoop.cs) 的 catch 后追加 TurnInterruptedEvent，也不得继续下一轮。DG 自身的只读校验错误仍保原 fault 合同，Agent 的停用不等于强制把 Repository 标成写入故障。用户无效输入导致的纯 Prepare 拒绝继续保持健康；网络／工具执行错误的原有中断处理不因本片改变。只读历史检查仍独立从 Event 历史读取，不触发 State 恢复写入。

| 中断位置 | 重开后的处理 |
|---|---|
| Event 未发布 | 原 State 继续；外部副作用仍按原协议判定 |
| Event 已发布，State 未发布 | 原 State + 原尾 Event，生成指向原位置的引用并保存一个 State |
| State 已发布但交付失败 | 加载新 State 中已保存的引用，不重复应用 |
| apply 部分修改、保存异常 | 弃用该图与缓存，以真实保存 head 恢复 |
| 所需引用解析失败 | 不启动对应模型/工具；报告准确依赖失败，不跳过消息或替换事实 |

纯内存 Session 仍使用本地 Event 序列与投影缓存，不伪造 CommitId。可以用应用级事件来源封装区分内存序号与持久 EventRef；持久 Session 只允许已绑定的后者进入所需持久条目。Replay 是测试/分析入口，不能把临时来源静默序列化成可恢复的持久引用。具体最小代码形状由 Agent 见证收敛，不为两种模式建通用存储插件体系。

### 4.2 Reset、fork 与编辑

Reset 清除当前历史和活动批次，保留设置引用与进程高水位；历史 Event 不删除。同库 fork 继承引用值，解析器使用同库，不要求目标的 CreatedOnBranchId 等于当前分支，不重写祖先引用。引用可以指同库其他分支的已提交 Event，祖先限制属于应用规则而非库自动遍历。

修改旧消息或创建摘要时应产生新的事实或明确的 State 自有派生内容，再改变所选引用／投影；缓存里修改旧 Event 对象不能冒充历史编辑。本片不实现编辑、摘要、分页 prompt 或去重外部工具副作用。

## 5. 保留、导出与“单次保存”的准确范围

正常路径中，一个接收事实只提交一次 Event；多个 State／消息位置引用它。之后 State Base 也不重新保存它的正文。这里不保证不同 Event 中相同字节全库只有一份，不去重同一 Event 内各种业务表示，也不承诺底层永远不搬迁或复制字节。

EventRef 是业务内容依赖，但首片 DG 没有外部边 GC/反向索引/自动 pin。当前完整帧只追加，库不能因某条 Event 不在当前 History 中便删除它。备份保留整个 Repository；单独导出 State 不再自足。未来裁剪、子集导出或 compaction 必须识别已知 EventRef schema 的值，保留目标和 Schema、处理传递依赖及地址映射；否则不能对含此引用的存档启用这些操作。不把未来工具列为本片前置。

同库以实际打开的 Repository 为解释上下文；现有 CommitId 跨库误用检查仅为概率诊断。保留这一限制，不增加每引用全局 scope，也不宣称任意异库混用必被拒绝。独立复制后再次交换引用不是同库使用，需另行设计。

## 6. 最小机制见证与进入实施的条件

本轮只完成设计。下一轮应依次验证以下问题，失败时收缩方案；不用全产品实施来代替一个未通过的机制见证。

| 见证 | 最小内容与判据 |
|---|---|
| P1：普通公开 inline 值能否承载 | 独立消费程序集引用包内 EventRef，字段、nullable/明确缺省、List/数组、跨程序集定义登记与冷开读取成立；生成捕获只见标量。先不把 Dictionary key、泛型 Ref、导出转换器并入门禁 |
| P2：载荷确实不进 State | 分别用 64 KiB/1 MiB 的输入、JSON、工具参数、结果、Instructions；检查真实 State 保存对象行/正文类型，不只搜索文件字符串。热缓存有无均成立；强制对象 Base 与容器 Base 场景要证明确实触发，不能仅修改预算参数就声称已覆盖 |
| P3：恢复与引用位置 | EventSaved、StateSaved、真实部分 apply 故障及重开，引用仍指原 Event；第二次冷开不再追加恢复 State。两尾＋坏引用必须先报尾形状错误且正文加载数为零。操作依赖失败后无外部调用、无新增中断/后续 Event、Version 不再推进 |
| P4：类型、历史与隔离 | default 在读取时拒绝；应用操作所需引用未绑定时拒绝。错误角色、错误结果类型、缺 binding、坏目标图拒绝；准确历史 Schema 与一条普通 Event 升级。合规 SG/allocator 下修改原 Event 或一次读取结果不污染另一独立读取与已保存正文；缓存命中同样校验种类、选择、owner 和重入 |
| P5：Agent 完整纵切 | 多调用／分批结果、长参数、失败/Incomplete、取消/未知结果、Reset、原生 process ID 高水位、纯内存模式；比较旧语义 prompt/待执行操作/冷恢复结果。不是逐字比较所有内部对象形状 |
| P6：成本与分支 | 纯 State 恢复／元数据读取不 LoadEvent；Agent 实际恢复尾事件／启动读取设置／构造 prompt 的依赖读取另计。读取一条只解析目标；仍驻留的投影再次读取命中。DG fork 夹具保留原引用，并使两分支相同业务 Sequence、不同 CommitId 不串缓存。分别记录追加字节、加载/解析次数、捕获耗时、冷 prompt 延迟与缓存驻留 |

性能结果必须拆开“第一次 Event 发布”和“随后 State 保存”，并把完整 prompt 读成本与纯元数据冷开分开。随机标记或散列检查正文只能辅助，不证明没有压缩／转义副本；主要判据是 State 捕获／保存图排除了正文。独立普通包见证须使用 DB-098 后同批 Runtime/Generator/Build，而非沿用 Agent 旧包回执。

先对相同工作负载列出整串重复、JSON 内转义子串、包装／数组／对象行三类成本，并保留当前双存模型作基线；这些结果也决定候选 C 是否更合适。不能仅凭 State 没有正文就推导总延迟和内存全面获益。P6 的连续命中测量须让被测条目留在预算内，另测逐出后的重新加载。

P2/P5 必含三个交叉场景：大量引号／反斜杠的长 RawInput；未完成工具批次后强制 Base；长 Instructions 经 Reset、强制 Base、冷开仍指原 Event 且内容一致。这里排除 State 捕获正文是 EventRef 路线的专属门禁；与 C 比较的共同目标是实际持久正文副本、恢复一致性和总成本，不能用这一专属门禁先验否决物理去重。

推荐推进顺序为 P1 → P2/P3 小型原型 → P5/P6 实际消费者 → 决定正式 API 与产品实施范围。若 P1 需要大幅修改类型系统，先保留应用 inline 原型并重新评估；若冷读开销、历史 Event 模型依赖或应用改造成本不合算，可直接重访 C，不要求先为主路线追加批读／缓存优化。不得因当前草稿入库就把候选新 API 当成已实现合同。

## 7. 暂不引入与明确未决

- `EventRef<T>`、`CommitRef<T>`、任意 ObjectId／字段路径：首个消费者没有必须使用它们的故事；以后确有类型错误频发或 State 历史根引用需求，再增加针对性封装。
- 透明 `.Value`、自动 host 注入、通用 mutable 对象缓存和 prepared-DTO cache：与存储单次保存解耦，等待易用性或性能证据。
- CAS、分块、字符串正文共享：保留 [候选 C](candidates/content-addressing.md) 的胜例及反例，不恢复为本片施工依赖。
- 外部依赖自动验真、强跨库身份、GC/部分导出：当前不是承诺；未来工具必须遵守第 5 节。

真正仍需实验裁决的是：公开 inline 类型的包登记成本、准确冷读与投影缓存的总成本、Agent 的最小纯内存来源表示。概念方向、内容所有权和失败顺序已有推荐；这些实验尚未运行，不写成性能收益或可实施性验收。
