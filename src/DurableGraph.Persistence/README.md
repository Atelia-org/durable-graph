# Persistence 源码导航

本程序集承载公开 Repository／BranchCheckout／Checkpoint 入口和默认持久化集成。公开入口在 `Atelia.DurableGraph`；
共同内核、固定员工、绑定表与模型执行环境在 `Atelia.DurableGraph.Persistence`。
[DB-092 E3](../../docs/design-branches/0092-type-namespace-refactoring/README.md#e3-status)将旧阶段目录并入本目录，源文件按现行职责命名，多类型文件保持共置。
固定员工的持久 SchemaId 与 Family 宿主保持，C 的生成与存档资格见[实施记录](../../docs/design-branches/0092-type-namespace-refactoring/implementation-c.md)。
业务图 wire 标识保持；上游 HeadStore 格式与三入口书签布局变更见 [DB-096](../../docs/design-branches/0096-history-and-head-model/headstore-adaptation.md)。原 meta 内核合同与限定资格维护在
[DB-087](../../docs/design-branches/0087-meta-bootstrap-refactoring-route/0087-meta-bootstrap-refactoring-route.md)。
开发 Schema 与查询后继见 DB-088/089，不从旧逐片“下一步”恢复排期。

两种变化来源共用一套对象版本保存和一个发布 owner：业务 Capture/比较与受控 meta 编辑。
catalog 用固定员工 DTO 保存 Schema/TypeExpr，再经原工厂投影；存活目录使用 Dictionary body。
动态 Base 按其 batch origin S 的 descriptor 解释，selected view 核完整 retained 布局。
追加后准备安装材料，FS/HS 确认发布后才接受；Event 接受 meta/Head，保最近 State 基线。

| 任务 | 源码入口 |
|---|---|
| 历史 checkpoint 书签目录 | [RepositoryResources.Bookmarks](RepositoryResources.Bookmarks.cs) |
| 公开仓库、持续编辑与历史读取 | [Repository](Repository.cs)、[BranchCheckout](BranchCheckout.cs)、[Checkpoint](Checkpoint.cs)、[CheckpointAddress](CheckpointAddress.cs)／[CheckpointKind](CheckpointKind.cs) |
| graph／branch-ref／tag 发布失败合同 | [RepositoryPublicationException／Outcome](RepositoryPublicationException.cs)；内部 [RepositoryOperationStage](RepositoryOperationStage.cs) 仅为故障注入 hook |
| 准确执行环境与共同恢复／比较 | [ModelExecutionEnvironment](ModelExecutionEnvironment.cs)、[GraphReader](GraphReader.cs) |
| meta 查询、意图与 Schema 工厂 | [SchemaCatalogView](SchemaCatalogView.cs)、[SchemaCatalogWorkspace](SchemaCatalogWorkspace.cs) |
| 固定员工、登记与有限绑定 | [BootstrapRecords](BootstrapRecords.cs)、[BootstrapBindings](BootstrapBindings.cs) |
| 对象 batch、目录、图与准确来源 | [GraphRecordStore](GraphRecordStore.cs)、[GraphFrameCodec](GraphFrameCodec.cs)、[StoredGraphDecoder／TypedBaseBodyCodec](StoredGraphBodies.cs) |
| 存储图、对象链与共同保存材料 | [GraphPersistenceContracts](GraphPersistenceContracts.cs)、[PendingRepositorySave](PendingRepositorySave.cs) |
| 业务准备、真实 B/D/H 与安装 | [BusinessPersistenceAdapter](BusinessPersistenceAdapter.cs)、[BranchSaveSession](BranchSaveSession.cs) |
| 两根与发布/fault/资源 owner | [RepositoryResources](RepositoryResources.MetaBootstrap.cs) |
| owned 物理缓存 | [GraphPhysicalReadCache](GraphPhysicalReadCache.cs)、[缓存合同](../../docs/design-branches/0067-owned-revision-read-cache-design/README.md) |

准入、private 与 ToShared 规则从 DB-087 当前合同进入；实际普通包门禁见
[PackageConsumerProbe](../../experiments/PackageConsumerProbe/README.md)。
旧 raw 消费者只保模型编译资产，不授原运行故事资格。进程 Kill 见证使用
[MetaBootstrapCrashProbe](../../experiments/MetaBootstrapCrashProbe/README.md)，不授断电保证。
逐片历史稿与旧格式从 `6dbaf0e` 查阅；当前产品不保第二保存/恢复路径。
