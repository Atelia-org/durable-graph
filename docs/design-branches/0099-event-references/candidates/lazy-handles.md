# 候选 B：原生类型引用，显式解析，应用视图延迟缓存

> 独立候选，尚未选择或实施。核对基线：DurableGraph `66f2cf3`（DB-098 产品提交 `a8f6ed1`），console-agent `437a586`。本文只提出设计，不声称已有 EventRef、延迟读取或新缓存。

推荐 `EventRef<T>.Resolve(repository)`，配合 Agent 自有的延迟视图缓存。DG 负责引用编码、生成器支持、准确恢复和类型检查；应用把第一次读到的输入、JSON、结果转换成不可变投影，再反复使用。首片直接复用现有 `LoadEvent`，不要求实现新的 DTO 缓存，也不让每个引用暗中绑定 Repository。

这条路线在调用处多一个上下文参数，但普通业务使用的是 `session.History`、待执行工具等视图，参数可以集中在 Session。它不承诺任意 `T` 的 `.Value` 每次返回同一领域对象；若这一点是硬需求，本候选需要让位给具有明确不可变约束的方案。

## 已有事实与新增边界

- [DB-098](../../0098-readpair-retirement/README.md)已经退役 ReadPair，独立读取允许宿主初始化 Transient，但仍保留构造器不执行、完整验证、两阶段物化和故障守卫。
- [GraphReader](../../../../src/DurableGraph.Persistence/GraphReader.cs)先准备完整源图并检查当前根类型，再分配、建立引用表、Hydrate；独立读取交付不同可变实例。[ObjectReadTable](../../../../src/DurableGraph/Runtime/Capture/ObjectReadTable.cs)只解析本图已分配对象，没有宿主依赖注入点。
- [SG Capture](../../../../src/DurableGraph.Generator/DurableGraphGenerator.GeneratedState.cs)读取存储字段；[Allocate/Hydrate](../../../../src/DurableGraph.Generator/DurableGraphGenerator.StateModel.cs)通过 `GetUninitializedObject` 和字段写入恢复对象。自动属性、字段初始化器、构造器创建的 `Lazy<T>` 都不能承担恢复协议。
- 当前 [TypeTag](../../../../src/DurableGraph/Schema/TypeTag.cs)没有外部事件引用；[CommitId](../../../../src/DurableGraph.Persistence/CommitId.cs)是同仓库使用的 12 字节位置，解码不证明存在性或角色，跨库误用检测也不是严格保证。

**拟议首片只引用同一 Repository 内已发布 Event 的根。** State 可以保存此引用；Event 中拒绝 EventRef，维持独立完整载荷，不引入递归加载、路径选择器、子对象 ObjectId 或跨库引用。引用可指向本库另一分支上的 Event，不额外承诺祖先关系；Agent 自己的交替协议保证其引用来源。

## 最小 API 与持久形状

```csharp
// 示意 API，当前不存在。
EventRef<UserInputReceivedEvent> reference =
    repository.CreateEventReference<UserInputReceivedEvent>(eventCommit.Id);
UserInputReceivedEvent accepted = reference.Resolve(repository);
```

`EventRef<T>` 是仅含位置的 readonly struct，约束 `T : class, IDurableObject`。相等和 hash 只看引用位置；当前 CLR 对象身份、缓存命中、Transient 都不参与持久语义。工厂检查位置存在且角色为 Event；解析时才完整检查源图、Schema/升级及结果可赋给 `T`，因此工厂返回不等于载荷已经可恢复。

建议新增 `EventReference<T>` 的内建字段形状：Schema 持有目标的完整 nominal TypeExpr，实例只编码 CommitId 的 12 字节。它不是指向本图目录的 ObjectReference，不应加入本图 alias/cycle 遍历。Runtime 中引用只保存无依赖的定长位置；CommitId 转换、工厂及 `Resolve` 扩展放在 Persistence，避免 Runtime 反向依赖 Persistence/HeadStore。具体 tag 编号和定长布局须在实施片定稿，属于新格式能力，不能宣称只是增加一个便利类。

SG 专门识别这个泛型定义和 nullable 包装，Capture 只复制位置，Hydrate 只重建纯值，不调用 Resolve、属性 getter 或宿主回调。保存先完成捕获，再在 Repository 现有操作内按唯一位置检查目标存在且为 Event，写入前拒绝无效引用；这一步可以读元数据，但不读目标载荷。State 读取只校验引用编码与声明形状，载荷损坏、缺少模型或升级失败在首次 Resolve 才暴露。这是延迟加载明确增加的失败边界，不能把“State 已 Load”解释成所有历史消息都可用。

`default(EventRef<T>)` 无效，捕获时拒绝；缺省值使用 `EventRef<T>?`，不把无效位置当业务 null。拒绝 `EventRef<T?>`，Resolve 成功返回非 null。封闭泛型目标可沿用现有 nominal 类型系统；首片不接受未绑定目标、任意接口或泛型方差转换。旧 Event 按自身准确 Schema 环境读出并升级到当前 `T`，不把 `T` 固定为历史 DTO 版本；State 字段改成不兼容目标类型时需要显式升级，不能仅改泛型参数后悄悄换解释。

## 延迟与复用放在哪一层

首片的 `Resolve` 等价于受类型检查的 `LoadEvent`，每次独立物化。常用读取通过 Agent 视图的 cache miss 进入它：

```csharp
// 应用视图示意；检查入口、缓存和 Repository 都由 Session 持有。
public string Input() {
    EnsureSessionUsable();
    return _cachedInput ??= InputEvent.Resolve(_repository).Input;
}
```

缓存 string 输入、JSON string，或 Agent 自己实现的不可变结果视图。结果视图私有保存复制后的标量/文本集合，向外返回不可变集合或新建可变结果；不要因 DTO 是 record、属性只有 getter 就认定整个对象图不可变。解析后的 ActionMessage 若可变，不能直接作为共享缓存对外暴露；可缓存 JSON 或私有解析结果，再交付独立对象。投影键必须含引用位置及投影种类，不能用业务 Equals 合并事件。

恢复时缓存字段自然为 null；首次访问重建。也可以由 `AgentSession` 创建一个非持久 HistoryView，集中缓存相同引用的投影，避免修改每个领域节点。两者择一即可，不同时建立两套缓存。无需遍历刚恢复的对象图注入 resolver，也无需构造器补执行。领域视图需要 Transient 初始化时，由宿主在独立恢复完成后显式执行。

引用值相等不保证 `ReferenceEquals(Resolve(...), Resolve(...))`；应用缓存命中保证的是同一投影可复用。修改一次 Resolve 返回的领域对象，不影响下一次 Resolve 或别的视图。DG 不缓存调用方传入 `CommitEvent(e)` 的 `e`：它是活对象，不能充当 accepted Event 的事实缓存。首次视图读取取持久 Event；宿主若要用当前 e 预热，只能把它稳定到 Dispatch 完成并复制成不可变投影，这属于需单独验证的优化，首片无需它。

因此**不强制所有 Event 模型不可变，不做隐式注入，不做全局领域对象缓存**。当前 [immutable-leaf 候选](../../../../src/DurableGraph.Generator/DurableGraphGenerator.ImmutableLeaf.cs)甚至排除 string、引用容器和隐式 backing，不能拿来证明 Agent 事件图可安全共享。若以后热读成本有证据，再研究 reader 所有的可信准备数据缓存；它必须保留准确 Schema/来源证明并每次独立物化，可参考 [PreparedStateRestoration](../../../../src/DurableGraph.Persistence/PreparedStateRestoration.cs)，但不能直接把 State 准备槽改成通用缓存当作首片前提。

## Agent 纵切与崩溃恢复

[现行 Dispatch/Recovery](../../../../../atelia-console-agent/src/ConsoleAgent/AgentSession.cs)是 Prepare 后 `CommitEvent → Apply → CommitState`；恢复最多接受一个待处理 Event。[消息模型](../../../../../atelia-console-agent/src/ConsoleAgent/SavedMessages.cs)和[状态机](../../../../../atelia-console-agent/src/ConsoleAgent/AgentStateMachine.cs)目前把输入、Action JSON 和工具结果再次放进 State。拟议改为：

1. 保留提交前纯 Prepare 验证；Event 成功发布后，把其引用交给 apply，State 安装引用及计数/游标。应用须保持输入图从 Prepare 到 Dispatch 完成稳定；若做不到，就从提交引用重新 Resolve 后重新 Prepare，代价是额外一次恢复。
2. Observation 保存 `EventRef<UserInputReceivedEvent>`；Action 保存响应 Event 引用；工具历史和未完成结果保存 `EventRef<ToolResultReceivedEvent>`。工具调用以“响应引用＋调用序号”表示，State 只重复调度所需的小标量，不复制 RawInput/JSON/正文。
3. Session 在纯状态机外按需解析活动响应，传入工具调用/结果投影；纯 Prepare 和 NextOperation 不因访问引用产生 I/O。HistoryView 首次读取某项时 Resolve，随后复用不可变投影。这样 State 仅引用大载荷，内存仍能缓存常用内容。
4. 中断合成的工具结果也要有来源：当前 TurnInterruptedEvent 只有 Reason。可以由该事件引用及调用位置确定性重建；若合成文本/结果不能稳定重建，必须先放进中断 Event 再提交，不能偷偷在 State 留一份无事件来源的大载荷。

进程在 Event 发布前退出，没有可应用的新引用；发布后、Apply 或 State 发布前退出，重开后从待处理 Event 的 CommitId 构造同一引用，再纯 Apply 和保存 State。State 已发布则没有待处理尾事件。缓存随进程丢弃，不参与“是否已应用”的判断；恢复不重新调用模型/工具，未知外部操作结果仍按现有 awaiting 协议处理。

`Resolve` 每次经过原 Repository 的关闭、fault、重入与单操作守卫；缓存命中也先检查 owner/session 可用，避免关闭后有时成功有时失败。已经交付的纯 string/不可变结果可以脱离 owner 存活。Session 串行使用，首片不添加后台加载、同步等待异步任务或 Lazy 的线程模式。Hydrate/升级/捕获回调中 Resolve 一律被现有重入守卫拒绝，不因命中缓存绕过。引用可在同库重开后由新 owner 解析，但不会自动重新绑定；跨库使用继续承担 CommitId 当前已声明的范围限制。

## 最小验收与最大弱点

最小可观察验收应覆盖：输入、响应 JSON、工具正文只出现在 Event 载荷；State 有准确引用且同值保存不变；未访问历史不加载 Event，首次读取一次、重复视图读取零次；构造器及初始化器抛异常仍能冷恢复；SG 捕获未解析引用且 getter/Resolve 计数为零；修改一次独立 Resolve 的可变返回值不污染后续热/冷结果；nullable/default/泛型目标、错角色、错类型、坏载荷及升级反例；EventSaved/Apply 中途/StateSaved 的杀进程恢复与再次恢复幂等；关闭、fault、重入和缓存命中遵循同一边界。这里是拟议门禁，本候选未运行实现测试。

最大弱点是便利程度止于应用视图：DG 原始 Resolve 不复用领域实例，Agent 仍需编写少量投影缓存；全历史首次访问仍逐事件恢复，解析大型 JSON 也可能分配很多对象。若要求统一 `.Value`、自动绑定上下文和任意模型共享实例，就必须付出新的不可变证明或可变对象使用纪律，不能靠 Lazy 名字消除这些成本。

可融合的核心是纯值引用、根级目标、SG 捕获不解析、准确 LoadEvent 语义和缓存从 accepted 数据派生。后续可在不改变引用格式的情况下增加显式 view builder 或准备数据缓存；不要把实体跟踪、修改回写、关系修复、代理子类和通用投影查询一起引入，避免把 Event Ref 扩展成 ORM。
