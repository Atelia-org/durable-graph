# DB-097：独立 Event 对象图

> 状态：目标方案经独立质询收敛，实施中。2026-10-20。
> 基线：`4d7c4b3`。本片改变 Event 的公开保存参数及业务内容来源合同；不改变 State 的增量语义或 Storage pin。

## 问题与目标

Event 是一次提交捕获的完整业务对象图。其内容不依赖其他业务图的对象版本，不推进 State 保存基线；历史顺序、Schema Catalog 和原子发布仍由 Repository 管理。

目标不是让重构步骤少，而是让重构后的 Event 无需参与 State 身份、差分、重建预算、rewrite、删除集合和内容来源证明。复用已有捕获、完整编码、图恢复和发布内核，不建立第二套持久化后端。

当前实现虽然称 Event 为 independent snapshot，仍相对最近 State 捕获身份和准备差分，并验证完整 State、复制 prior H、计算旧成员减去 Event 成员。底层追加又复制来源目录并准备目录 Delta。对于固定小 Event，成本会随 State 规模增长。

源码入口：[工作区](../../../src/DurableGraph.Persistence/BusinessWorkspace.cs)、[准备适配](../../../src/DurableGraph.Persistence/BusinessPersistenceAdapter.cs)、[保存准入](../../../src/DurableGraph.Persistence/RepositoryResources.GroupSave.cs)、[图存储](../../../src/DurableGraph.Persistence/GraphRecordStore.cs)、[读取](../../../src/DurableGraph.Persistence/Repository.MetaBootstrap.History.cs)。

## 需求账本与证据边界

| 要求 | 来源 | 本片处理 |
|---|---|---|
| Event 独立完整保存，不相对旧 State diff | 用户本轮明确同意讨论结论并授权设计与实施 | 唯一 Event 保存语义 |
| 优化最终模型复杂性；经 dialectical-simplification 后 bounded-delegation 实施 | 用户本轮指令 | 三方独立审查、交叉质询、主线程裁决 |
| 保留值、图内 alias/cycle、准确 Schema 与升级 | 当前 Capture/GraphReader、共享图和恢复测试 | 保留完整图校验；不靠放宽验证换性能 |
| 输入可以引用 State 对象，捕获其当前值；提交不冻结调用者对象 | WorkingCopy 契约、SharedEventHistoryTests | 输入引用转为本 Event 的独立图内容 |
| Event 不安装 State、不消费 rewrite、不保存无关的 State 编辑 | WorkingCopy、发布/恢复/升级测试 | State 根、基线、执行环境保持 |
| Event-first、连续 Event、State/Event 自由排列、fork、tag、多 Branch 原子提交 | 当前公开 API、DB-096 回归 | 历史能力保持；历史关联与内容来源分离 |
| 串行操作、重入拒绝、完整帧 append-only、故障后关闭重开、正常底座尾恢复 | 根 AGENTS、现有生产代码与故障测试 | 不扩大故障模型，不删除 owner/lifetime/publication 守卫 |
| 小型事实载荷有实际消费者 | EventHistoryRecoveryConsumer 的 ActorSnapshot/Amount；GroupCommit/Tag 标量 Event | 作为正向验收样本，不推导所有业务零代价 |
| 巨大共享子图可被保存 | SharedEventHistory/整图 Event 机制测试 | 表达保持，但承认完整写入放大 |
| 原型 API/格式未冻结，不维护旧路线 | 根 AGENTS、现行目标的原型迭代约束 | 直接迁移仓内消费者；旧实验库重建，无旧式增量 Event 模式 |

相邻设计文档是历史说明，不是彼此独立的需求证据。现有测试中的 `SourceGraph == State`、Event 复用旧 head 等物理断言属于被替代设计，不作为必须保留的产品法律。

不要求 CLR 深度 immutable、Event 专属小尺寸限制、业务 Apply/diff 接口、业务 ID 框架、大对象 Blob 层或跨图按值去重。现有通用容量限制保留。

## 重构后的最小模型

### 捕获和内容

每次 Event 使用新的 CaptureSession，ID 只在该 Event 图内解释；图内仍按引用身份保留 alias/cycle。复用注册模型及 Capture 生命周期守卫。

Event 只准备完整 Base。准备结果为：

```text
SourceGraph = null
RootId = 本次 Event 根
Records = 本次全部可达对象的 Base
RemovedIds = 空
```

独立 Event 不读取 State 的 normalized rows、旧对象 head、H 或 rewrite；也不消耗 State CaptureSession 的 ID 游标。State 继续保留长期捕获身份和增量保存协议。

一个共用候选持有本次实际 CaptureSession：State 选择长期 session，Event 选择新 session。共用 workspace 的 staging/pending/abort 守卫；不增加 EventWorkspace 或另一套候选协议。

事件候选在失败或发布完成后释放；只有 State 有 next baseline 和安装步骤。不得通过伪造空 State 基线或临时改写工作区状态来复用方法。

### 内容来源、历史和发布

State 的内容来源仍是严格前序最近 State。Event 的内容来源恒为空，即使历史中存在 State。Meta 的来源规则不变。

分支 expected Commit、Branch 占用和 StateSourceCommitId 仍表达编辑/历史准入；不把它们与 Event 内容来源混为同一个值。每次 Event 的内容资格不需要历史 State 的对象目录。

PreparedGraphWrite.SourceGraph 是准备内容来源的唯一表示。删除 PendingBusinessSave.StateSource 及单项 pending 转发；准入现场根据 Kind 和准确 ExpectedEntry 独立推导期望来源并核对 write。AppendBusiness 从已准入 write 取来源，不再接收重复参数。读取入口的外部 expected source 继续保留，核对磁盘内容与历史选择。

沿唯一生产链贯通 Capture、Prepare、group admission、Append、metadata qualification、LoadEvent；不能只在 adapter 内改 source。共用 Catalog 和多 Branch 发布 owner；完成帧不可重写。

存储层拒绝 Event 携带非空 source、Delta、Remove 或外部对象 head。完整读取资格要求非空 OwnedBatch、batch 全 Base、目录 Base/source 空、所有 live head 指向 owned batch，并沿既有检查保持 owned records 与 live membership 对应。仅 source=null 不能证明自包含。metadata-only 准入仍保浅层职责，不为了签出执行完整 Event 恢复。

Event 的业务对象和完整目录必须由本次图拥有；准确 Schema 仍可由 Repository 共享，独立不等于自带 Schema 或脱离 Repository 可读。

### 读取和身份

LoadEvent 独立还原 Event 内容，不追溯 State 对象版本链。其后仍有 Schema 解码、升级、引用验证及分配/填充。

任何包含 Event 的配对（包括同一 Event 两次）统一使用既有 ReadIndependent；数字 ObjectId 相同不代表对象相同，非空字符串也不得因数字重合而误绑定。不新增同 Event 共享例外或持久 ID scope。

独立物化使用各图分配表，但保留同一次读取的 mutable factory 实例守卫。不能用两次无共同守卫的 ReadRoot 替代，否则 singleton 工厂会让第二图覆盖第一图；不能仅传空 shared set，它仍保跨图字符串身份约束。

State–State 的既有 ReadPair 优化及只读约束保持。ReadCheckpoint 的两图独立可变实例和历史关联合同保持。共用物化内核；不复制一套 Event reader。

### API 和版本边界

Event 不再接受读取放大/重建预算参数：迁移 `CommitEvent`、`CreateEventCommitRequest`、`CreateRootBranchFromEvent` 的公开签名及仓内消费者；State 参数保持。共用内部 request 可保留 State 所需参数，但 Event 不验证或使用该策略。

Branches 根格式标识由 P0R3 升为 P0R4，所有 Repository 打开方式明确拒绝旧库，避免同一库内混合旧增量 Event 与独立 Event。已有 batch/Base/Schema/graph 编码不因此另造版本。旧测试数据和开发库按原型约定重新生成，不添加迁移工具或兼容 reader。

## 收益、代价和不可删除的机制

设 State live 数为 N，Event 可达数为 M。目标是消去 Event 内容路径中的 N 项资格/临时表/删除/目录来源工作，而非承诺整次提交严格 O(M) 或固定加速倍数。

Capture 原本只遍历 Event，不复制全 State 身份表；不能把不存在的复制算作收益。现实现已为全部 Event 对象准备 Base，独立化主要额外付出的可能是实际写出这些完整 bytes。

小型自有 Event 预计收益最大。Event 引用巨大不变 State 子图时，原来少量或零对象写入变为全量；必须记录空间与写入代价。Event-first 已是无来源路径，性能提升预计有限。

State/meta 仍需 Delta、prior、H、rewrite、目录增量和精确来源验证；State–State 仍需共享闭包；签出和历史导航仍需最近 State memo。这些不是本片的可删除成果。收益主要是 Event 退出组合协议，不承诺 LOC 下降。

## 最小可观察验收

1. 固定小 Event，State 为 0/1,000/10,000 对象：准备和业务追加不再访问 State rows/构造 N 项辅助表；报告分配、阶段耗时、总提交和实际追加 bytes，不用时间硬阈值作语义测试。
2. 同一对象作为 State 和 Event、共享 child/list/string、cycle、同数值局部 ID：Event 独立完整，图内关系保持；随后 State 保存仍相对旧 State 正确增量。
3. 未保存修改、升级 rewrite、Event-first/连续 Event、跨分支 group、fork/tag、冷开及 exact Schema：保现有业务行为。
4. 来源/Delta/Remove/外部 head 负例、回调重入、失败释放、发布 fault/原子性：失败不安装 State、不交付半图，不改变原故障合同。
5. 大共享子图、k=1 对照：报告写入放大，区分准备收益和总成本。已有约 40 MB/10k 测量是 M=N，不能充作小 Event 结果。
6. 根 solution Release build、完整 tests；新版本普通包验证 Event 恢复、group、tag、State 增量及 Pair。包 G 与 Storage S 独立，候选本地保存，不公开发布。

## 审查与实施状态

三方独立审查与交叉质询见 [目标裁决](review.md)。尚未宣称目标已实现；最终测试和测量证据将在本目录保留。当前能力以 [PROJECT-STATE](../../../src/PROJECT-STATE.md) 为准。
