# 候选 D：已保存图根引用

> 独立候选，尚未选择或实施。源码基线：DG `66f2cf3`（产品 `a8f6ed1`），console-agent `437a586`。只读调查，无构建或运行实验；未阅读其他候选。以下“建议”均为提案。

## 结论与最小范围

建议竞争方案为 `CommitRef<T>`：引用同一 Repository 中一个**已经发布的 commit 根**，可指 State 或 Event；固定坐标不会随 branch tip 移动。第一片采用普通 inline 值与显式 resolver，Agent 只使用 State→Event 根。暂不提供任意子对象、字段路径、跨库引用、自动递归展开、全库依赖图或领域对象共享缓存。

它比专用 EventRef 多出的有效通用性只有“根的角色由 descriptor 判断”，实现仍可复用现有两类读取。若需要通用类型系统、任意路径、只读代理和引用 GC 才能交付，当前消费者不足以支持这笔成本，应收敛成 EventRef；底层坐标与 resolver 可以保留相同形状。

不推荐 `FrozenRef<T>`：冻结的是持久坐标与已保存值，解析所得 CLR 对象仍可能可变。名字不能替代深度不可变证明。

## 当前源码事实

- [CommitId.cs](../../../../src/DurableGraph.Persistence/CommitId.cs)第 6–20 行：12 字节位置可跨重开复用，解码只检查表示；不证明存在、角色或仓库归属，跨库误用检测只是概率性的。[Repository.cs](../../../../src/DurableGraph.Persistence/Repository.cs)第 339–342、422 行附近分别加载准确 Event/State，并检查角色。
- [ObjectId.cs](../../../../src/DurableGraph/ObjectId.cs)说明身份依赖外层 view，不自带 repository、revision、目标类型或版本。[StateReferenceVisitor.cs](../../../../src/DurableGraph/Runtime/Binding/StateReferenceVisitor.cs)第 51–86 行只在一个完整目录中检查存在性和持久 nominal ancestry。普通引用会进入图内 Capture/可达性，不是历史位置。
- [BusinessWorkspace.cs](../../../../src/DurableGraph.Persistence/BusinessWorkspace.cs)第 54 行为 Event 新建 CaptureSession；[BusinessPersistenceAdapter.cs](../../../../src/DurableGraph.Persistence/BusinessPersistenceAdapter.cs)第 39–50 行写全部 Base 且来源为空。State 继续使用 baseline、Delta、prior 与 H。[DB-098](../../0098-readpair-retirement/README.md)已删除 ReadPair，保留独立物化、准确解码复用与物理读缓存。
- [DecodedRevision.cs](../../../../src/DurableGraph.Persistence/DecodedRevision.cs)第 50–64 行读取完整 stored graph；[GraphReader.cs](../../../../src/DurableGraph.Persistence/GraphReader.cs)第 154–170 行先规范化全图再计算可达性。[NormalizedGraphState.cs](../../../../src/DurableGraph.Persistence/NormalizedGraphState.cs)第 156–180 行逐行 Upgrade、随后校验完整当前目录。内部可换 rootId，不等于已有公开子对象懒读 API。
- Agent 的 [AgentSession.cs](../../../../../atelia-console-agent/src/ConsoleAgent/AgentSession.cs)第 112–133 行先 Prepare，再保存 Event、Apply、保存 State。[AgentStateMachine.cs](../../../../../atelia-console-agent/src/ConsoleAgent/AgentStateMachine.cs)第 109–149 行把 Event 的 Action/SavedResult 放入 State；[SavedMessages.cs](../../../../../atelia-console-agent/src/ConsoleAgent/SavedMessages.cs)保存 JSON、文本数组及工具调用 Input。双图各自捕获会重复保存正文；本报告没有重跑题设的 64K 实验。

## 坐标、边与签发

建议概念形状：`CommitRef<T> { CommitId }`，`T : class, IDurableObject`。最小存储实现可声明 `[DurableType]` 泛型 inline struct，以三个 UInt32 保真承载 `CommitId.TryWrite` 的 12 字节，通过公开 TryRead 还原，不泄露 HeadEntryId 内部结构。闭合 `TypeExpr` 记录 T，不保存 CLR 类型名，也不每值重复写类型。[CrossAssemblyGraphTests.cs](../../../../tests/DurableGraph.Tests/CrossAssemblyGraphTests.cs)第 135 行已有不在字段中使用 T 的 `Phantom<T>`；泛型 inline 与跨程序集机制有源码见证，本精确包装的包级接线仍需实验证明。无需新增 TypeTag；这是有应用语义的普通值，不伪装成 ObjectReference，也不会自动获得 DG 强引用完整性保证。

这种边不参加 owner 的对象 Capture、不导入目标 ObjectId、不推进 State baseline；Load owner 只得到坐标。显式 `Resolve(repository, ref)` 才恢复目标。不能在 Hydrate 中回调 Repository 自动解析，否则触碰现有串行/重入边界。target 是否必须为祖先是业务约束；通用根引用只要求同库且已保存，不能以“最近 State”代替准确目标。

同库是显式调用契约，12 字节值不能确定证明来源。缓存必须按实际 Repository owner 隔离；如果产品要求任意外来值都能确定拒绝跨库，必须另有持久仓库身份或导入协议，不能借当前概率检测冒充该保证。

签发使用刚返回的 CommitInfo，或显式查询已有位置，检查非默认值、存在及所需角色；T 表示期待类型，实际当前 T 在 Resolve 时验证。若需要“签发即证明目标类型”，这是显式读取/校验操作，不能由泛型参数自行推出。签发不冻结调用者仍持有的 Event 对象。保存/读取 owner 仅做现有 inline 值/Schema 校验，不探查外部目标，也不声称拒绝所有伪造坐标；首次 Resolve 检查存在、准确位置、角色、目标自己的 Schema view、升级及当前 T。缺失 binding、升级失败或类型不兼容是解析失败，不可偷偷改指邻近 commit。这样复用当前 CommitId 的“值解码不证明存在”边界，无需新增保存 visitor 或全库审计。若产品要求坏外部边必须在 owner 发布前拒绝，再增加一种外部边枚举与保存校验，而不是假称本片已做到。

`CommitId + ObjectId` 只适合第二片的 `ObjectSnapshotRef<T>`：由 reader 根据准确保存图及实例反向表签发，调用者不能猜数字。Event 每次局部编号，下一条 Event 的相同数字毫无关系；字段名、列表下标也不是稳定地址。inline 值没有独立 ObjectId。即使当前 Normalize 保留对象 ID，任意未来 Upgrade 仍不保证子对象满足 T。须为所需类型与升级故事另立合同。

## 成本、完整性与缓存

指向 State 根不会再保存正文，但第一次解析仍承担其完整目录、对象 prior 链与 Upgrade；N 个历史 State 引用可能反复规范化相似大图。H/物理缓存有帮助，不能据此承诺 O(1) 解析。子对象地址在当前 reader 上也不消除全图解码。Agent 应直接引用拥有正文的 Event，避免“State 引前一 State，再找正文”的应用层长链。

普通 inline 路线允许 Event 中出现此值，无需增设按 owner 角色禁止的跨对象扫描。Event 的值与本地对象仍全部自有、全 Base、`SourceGraph=null`；LoadEvent 不解析外部边，仍能独立还原“含地址的事件记录”。但是其业务含义若依赖另一个 Event，就不再是单条 Event 的语义闭包；独立业务导出与保留必须带依赖。必须明确收窄 DB-097 的“完整业务对象图”表述，不能仅凭物理 Base 宣称语义独立。Agent 第一片无需使用 Event→Event，因此不因此减少正文完整性。受支持签发流程只指已发布目标，可避免正常流程形成 commit 环；普通值仍可被伪造，未来闭包工具必须用 visited 集合/预算，不自动无界展开。

缓存优先存 owned exact DTO/准备材料，键包含 Repository 生命周期、准确 commit、冻结模型配置和所选 Schema 环境；采用有界预算。现有 [GraphPhysicalReadCache.cs](../../../../src/DurableGraph.Persistence/GraphPhysicalReadCache.cs)缓存物理帧与目录，不等于领域对象缓存。每次交付可变 T 应独立物化；同一缓存实例交给多个 State 会让一个调用者的修改污染另一引用。若缓存最终字符串或应用投影，必须单独证明其不可变并说明失效规则；record、readonly 字段和“历史数据”本身均不构成证明。

精确目标的历史 Schema 负责解码，闭合泛型身份与 retained ancestry 负责类型验证；不能只比较 CLR `typeof(T)`、SchemaId 字符串或默认泛型参数。旧引用字段可在 owner Upgrade 时转换，但目标 schema 变化不会自动重写所有 owner。泛型目标和跨版本失配应纳入验收，不能假定子路径天然稳定。

## Agent 纵切、保留与证伪

Dispatch 的 Prepare 继续在 Event 发布前做业务校验；apply 接收保存成功后才产生的引用，不能在 Prepare 时预造 CommitId。恢复尾 Event 时使用已知 commit 签发同一类引用。History/待处理结果保存引用和必要的小元数据，投影读取由 AgentSession 持有 resolver。内存模式可由独立内存 resolver 提供同一业务值，不把 repository 句柄放进持久 State。

替换 Action/SavedResult 还不够：`SavedToolCall.Input` 也可能是从 Action JSON 抽出的巨大重复文本；应保存 Event 根引用加应用定义的调用键，解析后验证键唯一且存在，不能依赖未经约定稳定的列表下标。用户输入同样引用其 Event。模型请求最终消费全量上下文时仍会读取全量正文；lazy 的直接收益是冷开、只看元数据和只选部分历史。

业务上应把引用视为强保留边，首片 DG 尚不追踪这种边。当前 append-only 保住开放 Store 的完整帧，不能代替未来 GC 的合同：保留 owner 应保留被引 commit、准确 Schema、目标 State 的重建来源及传递闭包。未来工具可识别已知 Ref 的稳定 Schema 定义并枚举值、按需遍历，用完即弃；不必预先维护依赖图、反向索引或在每次保存重算闭包。开展物理 GC/部分导出前必须实现这项识别，任意看似 12 字节的普通 struct 不能猜成引用。导出必须带闭包和地址重映射，或显式物化为自有正文；复制 CommitId 字节不构成跨库导出。首片仅同库、无删除/搬迁，记录边界即可。

最小证伪实验：同一离线 Agent 序列分别加入 64K 用户输入、Action JSON、工具文本与工具调用 Input，检查逻辑对象记录确保正文只在对应 Event 保存一次，后续 State 仅含引用；分开记录文件增长、物理读与 Decode/Normalize 次数。冷开 State 应不解码正文，选一条解析只读对应 Event，重复解析有界缓存命中且 mutable 实例互不污染。再注入 EventSaved 后退出、错误角色/类型、缺 binding、泛型历史目标、State 长 Delta 链及重开恢复；跨库样本拒绝只证明该样本，不能证明不存在坐标碰撞。若根引用仍迫使常规 State 加载全历史正文，或唯一存盘必须引入任意子对象 reader，本候选的“小通用根”优势即被证伪。

可融合点：EventRef 可作为相同根坐标的角色约束 facade；反之先交付 EventRef 也不妨碍以后增加 State 根读取。最值得保留的是显式跨图边、准确坐标与解析隔离，而非同时推出两个名字或一套通用快照框架。
