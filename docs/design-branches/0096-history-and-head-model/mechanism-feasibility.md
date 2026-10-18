# DB-096：API 到机制的首批可行性调查

> 2026-10-18，首批源码调查与集成稿。公开行为以 [API 草图](api-sketch.md)为基准，活动状态由 [PROJECT-STATE](PROJECT-STATE.md)维护。本批只读源码与文档，不把推演、已有测试代码或历史验收当成新模型的运行资格。

## 1. 问题、范围与验收

问题：已认可的 Branch / Commit / WorkingCopy API 能否由现有底座和较小的 DG 内部调整承接，哪些不确定性必须先用实验消除？

范围：API 到原语的映射；共同 Schema 准备与发布；分支登记创建；准确 Schema、State/Event 与 Delta 恢复；目录与历史查询成本。保持只追加、旧点另开、空 fork 立即可用、执行时捕获及紧凑身份合同；不增加 rewind、GC、多 writer 或跨库事务。

最小验收：每组 API 有源码支持的可用原语、缺口、读写范围和失败点；区分事实、推断、推荐与未验证；主线程复核跨包约束后，选出有明确观测判据的下一批最小实验。无需在本批完成产品接入或性能资格。

## 2. 本次源码基线

| 项目 | 2026-10-18 本机观察 |
|---|---|
| DurableGraph | `c93e7f5a5c10aaf6bccf55f2c929794e08136e7d`；调查开始时工作树干净 |
| atelia-storage | `f8868b57e8f012745413bb38a83d67f1dba29f26`；调查开始时工作树干净 |
| DG 固定 Storage | `731eef7cd3158572532fbeea7ba95a79213a7328`，候选包 `0.1.3-dev.20261017.g731eef7` |
| 本机环境 | `UseStorageSources=true`；`StorageSourceRoot=E:\repos\Atelia-org\atelia-storage` |

固定依赖见 [StorageDependency.props](../../../eng/StorageDependency.props)。[ValidateStorageSourceMode](../../../Directory.Build.targets)要求 sibling HEAD 等于 pin；当前环境直接构建将触发版本不一致门禁，这是源码条件推断，本批未执行构建。后续实验必须显式选择和记录依赖基线，不绕过门禁后沿用旧产品资格。

## 3. 有界工作包

| 工作包 | 边界 | 交付 |
|---|---|---|
| 发布与创建 | 只读 DG capture/prepare/install、HS 创建与联合发布 | Shared 协调缺口、三种登记路径、失败窗口、最小见证 |
| 描述与恢复 | 只读图封套、最近 State 来源、准确 Schema 与恢复管线 | Parent 职责拆分、最小描述候选、Event-only/空 fork 恢复路径、定位值接入缺口 |
| 查询与成本 | 只读 HS 目录/来源/准确历史、DG 当前查询 | 每组查询访问范围、容量估算、分页合同及成本实验 |
| 主线程集成 | 核对共同基线及关键证据，不重复全部搜索 | API 映射、交叉约束裁决、下一批顺序和文档验收 |

三个调查包不修改文件、不构建。主线程统一维护设计文档；发现需要改变用户可见能力或持久协议的选择，先记录候选与理由，不借调查直接实施。

## 4. API 到机制：哪些能组合，哪些要改

下表是本批源码调查后的推断，不是已实现能力。后续实验优先验证跨行共享的发布和恢复内核，而非逐个方法造样板实现。

| 公开 API 组 | 可用原语与当前 DG 基础 | 真正缺口、访问范围与失败边界 |
|---|---|---|
| BranchId / CommitId 编解码 | HS `HeadId.TryRead/TryWrite` 为4B，`HeadEntryId` 为12B；解码不做 I/O | DG 薄角色类型和持久字段支持未接入；使用时仍检查正式成员与准确记录。不能直接 dump CLR 内存布局 |
| ListBranches / ListRootBranches / ListBranchSubtree / ReadBranchInfo | DG 有小目录编码与发布；HS 有 `ListHeadOrigins` | 目录须从 name→current graph 改为 HeadId→标签等登记属性；当前来源查询扫描物理全集。完整 BranchInfo 包含来源，不能称仅读登记就足够。子树可由内存邻接表得到，无需恢复 State |
| ReadCurrentCommit / ReadCommitInfo | HS `ReadHead` / `ReadEntry` 提供已接受 Entry、实际身份和 payload | DG 新小描述尚无格式；需业务成员/Kind/引用检查。空 C 的 current=a1，返回值保留 C；底层 CU 可能访问必要 peer，不能按一次 API 调用推断一次文件读取 |
| ReadHistoryPage | HS `ReadEntryHistory` 从准确点继续继承历史，能 visitor 提前停止 | DG 解码小描述并 materialize 一页；Before 重读并跳过 anchor。现 `ReadFrames` 全链缓存，不能仅包一层 Take 就达到目标。预算失败和页满需明确区分 |
| LoadState / LoadEvent | 现 `ReadState/ReadEvent`、`DecodedRevision`、`GraphReader` 已有准确 Schema 及独立物化管线 | 从 Commit 定位图并保留准确内容依赖；Kind 错误拒绝。只读描述与恢复对象应继续分开；对象读取可能涉及目录/Delta/历史 Schema |
| LoadLatestSavedState / OpenWorkingCopy | 现有最近 State 查找、State-only/Event-only 与可编辑基线导入 | 查找须改用继承历史或经证明的直接锚点；Open 还要保存目标 Branch 与逻辑 tip、State 基线两种位置。Event-only 的 null 必须有完整查询证据 |
| ForkBranch / 两个 CreateRootBranch 入口 | HS 引用 fork、root 创建，DG 已有准确内容与 Schema 保存基础 | 最终登记决定 DG 可见性；新建 Head 不属于既有 Head 的 CU。三条路径见 §6；模型加载和持久创建继续分开 |
| CommitState/Event、Create…CommitRequest、CommitBranches | DG capture/prepare/install、HS `PublishHeads` | 请求执行时捕获；一个组级准备窗口、Shared 候选协调、每副本安装。当前单 pending 不能直接扩成若干独立 Prepared 的数组，见 §5 |
| summary / 附件（上述保存和预览的参数） | HS opaque payload 与 FrameStore 可承载小描述及按需 BLOB | DG 尚无本稿对应的摘要封套/附件使用合同。大小限制、字节所有权、引用闭包、错误传播仍需选择；不能把底层存在地址当成已交付附件 API |

直接事实入口：[Repository](../../../src/DurableGraph.Persistence/Repository.cs) L143–174、L273–329；[资源与发布](../../../src/DurableGraph.Persistence/RepositoryResources.MetaBootstrap.cs) L12–36、L265–275；[准确读取](../../../src/DurableGraph.Persistence/DecodedRevision.cs) L54–96；[HeadId](../../../../atelia-storage/src/HeadStore/HeadId.cs)、[HeadEntryId](../../../../atelia-storage/src/HeadStore/HeadEntryId.cs)、[Entry 读取](../../../../atelia-storage/src/HeadStore/HeadStore.Entry.cs)。行号对应 §2 基线，链接指向工作树文件。

## 5. 联合提交：保留一个操作窗口，合并 Shared 准备

### 5.1 已核对的障碍

[RepositoryResources](../../../src/DurableGraph.Persistence/RepositoryResources.MetaBootstrap.cs) L168–171、L188–198、L234–236 明确只有一个准备/待提交窗口；[BranchSaveSession.Prepare](../../../src/DurableGraph.Persistence/BranchSaveSession.cs) L88–120 为每份业务独立 rebase、capture、准备 Catalog 和编码。[SchemaCatalogWorkspace.Builder](../../../src/DurableGraph.Persistence/SchemaCatalogWorkspace.cs) L173–177 从 accepted rows 的最大 ObjectId+1 分配；[BusinessPersistenceAdapter](../../../src/DurableGraph.Persistence/BusinessPersistenceAdapter.cs) L37–63 立即用该 Catalog 映射编码业务 Base。

因此取消单 pending 检查并不够：两份独立候选可能将不同定义分配到同一描述符号码。保留最后一份 shared 根会丢定义；将已编码图改 pin 到另一份 Catalog 也不能保证解释正确。

### 5.2 首选实验候选

保持 owner 串行/重入保护，将其内部单位变成**一个组级准备窗口**：

1. 检查全部请求的 owner、目标、基线和策略，捕获各业务图。
2. 收集 Shared 成员的显式 dirty Schema/容器和捕获布局，在同一个最新 shared 基线上检查完整闭包与冲突。
3. 一个 Builder 统一分配描述符，所有 Shared 成员用同一最终 view 编码；追加一份共同 Meta 和各业务内容。
4. 发布前准备全部安装材料；一次 PublishHeads 更新各业务 Head 与必要 shared Head。
5. 确认后分别安装参与副本，消耗各自 dirty；普通提交采用单成员路径。

复用 [SchemaCatalogSemantics.Collect / ValidateDeclarations](../../../src/DurableGraph.Persistence/SchemaCatalogSemantics.cs) 的完整冲突规则；**不能循环 Set 到临时 workspace 就当完成合并**：[SchemaCatalogWorkspace.Set](../../../src/DurableGraph.Persistence/SchemaCatalogWorkspace.cs) L29–35 对尚未 accepted 的同 key dirty 会覆盖，需要先检出组间不一致。

对照路线是逐成员生成累计 Meta 链，最后共同发布；它多出中间 Schema 图、不同 pin 与安装状态。一次汇总更值得先验证，但本批没有性能比较结果。

未参与副本不必同时改写 Catalog；它下次保存会 rebase 到最新 shared，并检查自己的 dirty 是否冲突。参与副本不能只安装临时合并 workspace，必须各自消费其请求。State 成员推进 State 基线，Event 成员只推进逻辑 tip；Event 中尚未保存的 State 编辑不随组发布。Shared/Private 混组和 promotion 没有因该候选被默认纳入首片。

当前发布在追加 Meta/业务内容后、正式发布前准备安装/返回材料，确认后安装；失败保留 NotPublished/Unknown/Published 并按阶段 fault owner（资源与发布 L247–318）。新组内核应继承这个顺序，不能返回仍可继续使用的半安装副本，也不能将 Published 后的异常包装成回滚。

## 6. 创建：登记 B 有组合路径，但有两层 outcome

HS [CreateHead / PublishNewHead](../../../../atelia-storage/src/HeadStore/HeadStore.cs) L138–243 创建 root header+首个 Entry；[ForkHead](../../../../atelia-storage/src/HeadStore/HeadStore.Fork.cs) L37–56 重验准确来源后仅创建 header。共同顺序为 Data ConfirmDurable、必要来源依赖确认、私有新文件写入/flush/close、正式 rename。`PublishHeads` 只操作既有 Heads（[Batch](../../../../atelia-storage/src/HeadStore/HeadStore.Batch.cs) L65–111）。

设 D 为 DG 登记 Head，S 为 shared Schema Head，B 为新业务 Head：

| 创建路径 | 先准备 | 最终 DG 可见点 |
|---|---|---|
| 从旧 Commit fork | 校验来源正式成员及业务描述；ForkHead 得到 B | PublishHead(D)，登记 B/标签；不产生业务 Commit，不推进 S |
| root 无 shared 增长 | 准备首个真实图及准确 Schema pin；CreateHead 得到 B 和初始 Entry | PublishHead(D) |
| root 有 shared 增长 | 准备新 Meta、首个业务图并固定准确 pin；CreateHead 得到 B | PublishHeads({D,S})，共同发布登记与 shared 增长 |

这是无需新增底层创建事务的候选；不要求把先建好的 B 再加入最终 CU。它的成立条件是所有 DG 查询/操作以 D 的正式成员资格为入口，且必需内容在最终登记前完成可靠准备。普通保存无需再更新 D。

**底层 Head 创建结果与 DG Branch 创建结果不能直接等同：**

- 最终登记尚未尝试，即使底层创建 Unknown，也可以确定本次未创建正式 DG Branch；可能有隐藏 Head，owner 也可能已经 fault。
- 登记 NotAttempted：B 可已存在但不展示；增长路径 S 也未推进。
- 登记 Unknown：重开按 D 判断；D/S 共同成立或共同不成立。登记开始前已有候选 BranchId，后续失败合同应评估携带该 ID 供核对，重复标签不能当恢复键。
- 登记 Confirmed 后交付异常：Branch 已存在，不能声称未创建或透明重试。

推演仅采用上游 [ProcessCrashOnly 合同](../../../../atelia-storage/docs/HeadStore/05-versionstore-names-and-indexes.md)；源码中的 flush/rename 不构成设备断电资格。本批没有新故障实验。

## 7. 恢复：先纠正文档，再验证删除 Parent 的闭包

### 7.1 Parent 现在不只是领域前驱

[GraphEnvelope](../../../src/DurableGraph.Persistence/GraphPersistenceContracts.cs) L48–49 保存 Kind、Parent、RootId、DirectoryRoot、SchemaRoot、OwnedBatch。[GraphRecordStore.ResolveContentSource](../../../src/DurableGraph.Persistence/GraphRecordStore.cs) L283–303 对 **State 和 Event 都沿 Parent 找最近 State**；Meta 才取前一 Meta。并非“找最近同 Kind”；不应据旧文档引入 Event→Event 增量基线。

当前业务读写还核对：图选择的 source、OwnedBatch.SourceGraph、目录 creator/source 与 Delta prior 相符；对象 Delta 必须引用其准确 source graph 中该 ObjectId 的实际 head（同文件 L147–173、L241–279、L306–340）。这些关系验证不能随逻辑 Parent 一起删掉。

准确 Schema 已有图的 `SchemaRoot`；历史对象 batch 还保留其自身解释所需的 `SchemaRoot`。两者作用域不同，跨 Schema 的 Delta 链不能一概改用选中 Commit 或当前 shared 的 Schema。外层 Commit 描述没有必要再复制同一图的 Schema 为第二份权威。

### 7.2 候选与反例

较小的实验起点：Entry 只定位业务图和小摘要；继承 HS history 给出业务顺序，并查到准确最近 State；内容层保留目录/对象的 source、prior 和准确 Schema。先验证能否将历史推导的 expected State source 交给恢复层，再考虑是否持久存直接 State 锚点来减少扫描。

这里必须区分两个边界：**所选图的内容 source 是 Before(selectedCommit) 的最近 State 严格祖先**；`LoadLatestSavedState(atOrBefore: selectedCommit)` 在选中 State 时则返回该 State 自己。准备未发布候选时，以当前已接受 tip 的 AtOrBefore 查找作为候选 source，不能让已发布 State 图以自己为 source。

现有对象/目录 Delta 链只校验局部 source 与准确 prior，不递归证明每个 source 的业务历史；暂未发现必须建立 Graph→Commit 反查索引。真正接缝是多个只接收 FrameAddress 的 ReadGraph 调用：`DecodedRevision.ReadStoredGraph`、`StoredGraphDecoder.Read`、`NormalizedGraphState.RequireStoredSource` 都要取得或保留选定图的来源资格；`GraphRecordStore.Append` 和 `NormalizedGraphState.PrepareGraphInstallation` 处理的候选尚无 CommitId，须使用本次准备的来源材料。实验必须覆盖冷读、baseline 再保存和发布前安装准备，不只在 Repository 入口传一次参数。相关入口见 [NormalizedGraphState](../../../src/DurableGraph.Persistence/NormalizedGraphState.cs) L69、L137 和 [StoredGraphDecoder](../../../src/DurableGraph.Persistence/StoredGraphBodies.cs) L68–98。

反例：HS 历史为 `S0 → S1 → E2`，但 E2 的图、batch、directory 彼此一致地将 S0 当 source。仅检查这些物理字段互相相等、source.Kind=State，仍可能通过，却遗漏实际最近的 S1。**直接锚点的单点读取成功，不证明“最近逻辑 State”正确。** 后续必须明确祖先/最近关系的证明方式，并覆盖 source 依赖闭包；不能用新字段默默削弱现有验证。

沿 HS 历史查询的路线无需立即冻结新持久锚点，但连续 Event 很多时会扫描前缀；直接锚点路线可能降低此成本，却需要明确它的权威、关系校验和额外持久信息。两者只比较这项真实取舍，不重开独立逻辑 Parent 或通用历史索引海选。Meta 的前 Meta 是内容依赖，不属于本次业务历史替换，不能盲删。

必须走通的恢复例子：

| 历史与操作 | 应保留的结果 |
|---|---|
| S0,E1,E2，从 E1 fork C | C 的 BaseCommitId=E1；State 来源为 S0，不 replay E1/E2；首次保存写 C |
| E0,E1，从 E1 fork C | C 有 current；State 暂无；只有完整查到根才能给 null |
| 连续 State、State 类型替换 | 每次真实保存保留身份；准确图/Schema 解释和当前模型适配分别处理 |
| 连续 Event，期间编辑 State | Event 不安装新的 State 基线；下一次 State 仍基于最后接受的 State |
| Schema 升级后 Delta 读回 | 选中图与旧 Base/batch 的准确解释均保留；不能把 latest shared 当恢复环境 |

Private 写入 Catalog 来自所选 tip 的准确 Schema，恢复 State 则用最近 State 自己的 Schema，两者可能不同。Event-only 仍有 Event 的 Schema 可供 Private 后续演进；不应因没有 State 就丢掉该 Schema。Kind 可先从图 envelope 取得；是否在 Entry 再内联一份，应由轻量描述读取成本决定，并明确一致性，暂不冻结冗余字段。

定位值还有两层工程问题：外部4B/12B codec 可薄包装 HS；将 BranchId/CommitId 作为 DG 业务字段则涉及 SG/Runtime 的值类型支持和程序集依赖，不能只加 TryRead。可先比较现成生成 inline struct 路线与窄标量支持；外部 codec 的固定大小不自动约束 DG 图内 inline 编码。未要求新增通用自定义 codec 框架。

## 8. 查询：小目录可试，来源范围和预算须说清

### 8.1 来源查询是当前最小的上游接口候选

[ListHeadOrigins](../../../../atelia-storage/src/HeadStore/HeadStore.ForkQuery.cs) L13–64 扫描全部正式物理 Head，逐个读 header；L106–151 校验完整来源图，返回有来源的边，root 不在边集合内。当前没有按单 Head 或指定成员集合读取来源的公开入口。尚未打开的文件会临时打开；可写 owner 可能执行正常 lazy recovery，见同文件 L68–80。

因此 B 方案若用该接口再过滤，**完整 BranchInfo 查询仍将内部 Head、未登记的遗留 Head 纳入工作量和 header 校验失败范围**。全量扫描后过滤在健康数据上可实现功能，但不是成员限定访问，也不是必须在 Repository.Open 执行的预审计。

建议下一步评估很窄的上游请求：按 HeadId 读已检查 header 的 origin，root 明确返回无来源，缺失/损坏不得当 root；结果携带可直接使用的准确来源定位值更方便。读取 origin 本身不保证来源 Entry 健康；DG 负责正式父成员与森林关系检查，用户真正读/fork 选点时仍核准确 Entry。无需同时请求可变 Head metadata、持久子树索引或保留号类别体系。本批未修改上游或将该请求设为所有实验的前置。

[HeadOriginInfo](../../../../atelia-storage/src/HeadStore/HeadOriginInfo.cs) 已含 SourceHeadId 与 SourceEntryTicket。树父边可直接用声明的 SourceHeadId 并检查 DG 成员，**不必为了取得父 BranchId 逐边 ReadCommitInfo**。这不等于已证明来源 Entry 的发布资格。定向 origin 也不能消除真正必要的 CU peer、FrameStore 和共享 Schema 依赖。

### 8.2 整表登记容量：明确假设的估算

假设一份8B表头，每项4B HeadId、4B UTF-8长度和 L 字节标签，没有重复 current、summary 或来源副本：`size = 8 + Σ(8 + Lᵢ)`。这是估算用布局，不是 wire 决策；标签上限必须按字节定义。

| 1000项、每项标签字节 L | 最终 payload | 从1项逐次增加到1000项、每次整表发布的累计 payload |
|---|---:|---:|
| 64 | 72,008 B | 36,044,000 B |
| 256 | 264,008 B | 132,140,000 B |
| 1024 | 1,032,008 B | 516,524,000 B |

HS 上限为1,048,576B（[HeadStore.MaxHeadEntryPayloadLength](../../../../atelia-storage/src/HeadStore/HeadStore.cs) L14）。故1000项小标签有可行容量空间；但不限定标签长度就不能承诺任意1000项都装得下。表中不含 frame/CU 开销；整表登记的累计写入随创建数平方增长。普通高频保存不写目录，先测实际创建成本再决定是否引入增量目录。

### 8.3 分页的较小候选与成本范围

[ReadEntryHistory](../../../../atelia-storage/src/HeadStore/HeadStore.History.cs) L89–117 直接定位准确 ticket 的扫描边界，未先扫 newer suffix；L165–185 在接受每项后调用 visitor，可立即停止。DG 返回 K 项时，AtOrBefore 接收 K 项即停止；Before 先接受并跳过重复 anchor，再接受 K 项，底层 entry allowance 至少覆盖额外这一项。只有跳过 anchor 而无新项不算向祖先取得进度。

建议先验证**页满正常返回，工作预算耗尽则整次失败**的较小合同：只在底层成功退出并完成清理后交付物化页；预算失败不把内部收集的前缀当正常返回。这样无需首片再设计预算截断页的状态机。此为待实验的推荐，API 草图的部分结果取舍尚未在本批冻结。若以后保留预算截断页，只能明确识别预算错误，不能把取消、损坏、清理错误都截成前缀成功。

Continuation=Before(lastProcessedCommitId)；页满恰遇根时，允许下一页空且 Complete。预算内无进度不可伪造新位置，也不承诺原预算重复尝试会推进。

准确 anchor 避免的是该 Head 新后缀的重新定位扫描，**不是整个操作恒定 I/O**：来源跨 Head、完整 payload CRC、CU peer 资格、首次打开/恢复都可能有额外成本。[CU 读取](../../../../atelia-storage/src/HeadStore/HeadStore.ConditionalRead.cs) 的结构性否定见证还有 peer tail 逆扫路径；历史预算也不包办文件工厂/恢复全部 I/O。性能实验须分 SingleEntry/CU、冷/热、正常接受/未成立旧 CU，不以一条健康热路径推广全部情况。

另外三项成本须单列：可写 HS Open 为自动编号水位枚举所有 Head 路径，不能只算后续登记解码；ReadHead/ReadEntry 的目标文件进入 owner 的 `_ownedHeads` 缓存，逐条预览所有 current 可能累计千个句柄；来源/历史临时文件则使用完关闭。底层 Head 默认 `RbfCacheMode.Off`，热 owner 不等于跳过 payload 校验。证据见 [HeadStoreFactory](../../../../atelia-storage/src/HeadStore/Internal/Storage/HeadStoreFactory.cs) L57–81、[GetOrOpen](../../../../atelia-storage/src/HeadStore/HeadStore.cs) L359 起及 [FactoryOperations](../../../../atelia-storage/src/HeadStore/Internal/Storage/HeadStoreFactoryOperations.cs) L35–39。新进程只能称冷 owner，不能假定 OS 文件缓存也冷。

## 9. 下一批最小实验与依赖顺序

| 顺序 | 实验题与边界 | 可观察通过标准 |
|---|---|---|
| 0 | 固定新的实验 Storage 基线；选择隔离 worktree/实验项目或明确限定的接入片，不改动正在使用的 sibling | 依赖可追溯且满足门禁；列出实际兼容修改。当前测试/CrashProbe 仍有 `HeadId.LocalId` 调用，不能只改 pin 就宣称接入完成 |
| 1 | 两 Shared 图共同准备；先验证 Catalog/编码组合，不先铺开新 Repository API | 两份不同新定义进入一个最终 Schema view，分别编码/冷读正确；同 key 不同定义在发布前拒绝，业务与 shared 不推进。参与 dirty 安装和未参与副本后续 rebase 均有正负控；Event 不推进 State 基线 |
| 2，可与1独立 | 用 HS 公开原语、小示意 payload 见证 §6 三条登记路径 | 重开只展示登记成员；登记前隐藏产物不冒充 Branch；空 fork current 固定且首次写入写子线；增长登记与 shared 共同成立。仅证明组合，不冒充 DG 图恢复 |
| 3 | 最小恢复接缝：以继承历史计算 expected State source，明确图内依赖闭包；再比较直接锚点 | §7 恢复故事及冷加载→修改→Event→State再保存通过，发布前候选安装不依赖尚不存在的 CommitId；错误旧 source、错误 prior、错误 Schema 拒绝。记录长 Event 前缀成本，不以只有正常读回作为删 Parent 资格 |
| 4 | 小目录与固定旧点读取的针对性成本见证 | 测1000项的目录、未登记Head增量、来源扫描与累计写量；首屏只选20条current，记录句柄；固定旧点取20项，增加后缀0/100/10000，比较结果和最小成功work预算。单列 CU 否定见证/冷开成本，无实际计数接缝时不声称测得 I/O 次数 |
| 后续 | 组内核、格式和失败合同稳定后再做产品故障注入与完整资格 | CU 部分追加、确认后安装/交付失败均有准确 outcome；旧 owner 停用；产品 build/回归、独立包资格分开记录 |

首批只选择实验，不建立全能 Probe。Shared 组合实验若暂用现有目录完成多图发布，应注明它只证明 Schema 协调，不证明“一 Branch 一 Head”布局；恢复实验同样不能替代完整产品构建、恢复或包资格。按实验收益再定最终签名、payload 格式及实施切片。

## 10. 本批裁决与验收边界

- 继续验证登记候选 B 和单业务 Head 历史；当前没有发现必须新增 HS 创建事务的证据。
- 保留一个 owner 操作窗口；优先试一份 Shared 合并候选，不新增公开冻结 PreparedCommit。
- 修正“同类来源”为业务最近 State 来源；Parent 删除需要关系验证和依赖闭包证据，尚未获准直接删字段。
- 目录/树可以先小表与内存索引；值得评估定向 origin 读取，暂不引入持久索引或第二份来源权威。
- 页满成功、预算耗尽整次失败是较小的待验证分页候选；精确 API 与 wire 仍未冻结。

本批证据来自三个只读调查包及主线程对相关关键源码的复核；另一次独立集成审阅未发现阻断，提出的严格祖先边界、追加后发布前准备安装两处澄清已纳入本文。已有测试仅作为后续复用线索：`MetaBootstrapP0PublicationTests`、`SplitRepositoryPublicationOutcomeTests`、`EventHistoryPublicationFailureTests`、`MetaBootstrapP0IntegrityTests`、`DB078HistoryTests`、`SelectedGraphIntegrityTests`，均位于 [Persistence tests](../../../tests/DurableGraph.Persistence.Tests)；[GeneratedSchemaPersistenceTests](../../../tests/DurableGraph.Tests/GeneratedSchemaPersistenceTests.cs)提供 Schema 冲突线索。本批未运行 build/test、性能或进程中断实验，不沿用上游报告为 DG 新模型背书。

文档验收：集成差异已审阅；6份受影响文档的219个本地链接（含7个锚点）检查通过；无指向已替换工作集标题的残留引用；Git差异空白检查通过。这些检查只证明文档集成有效。
