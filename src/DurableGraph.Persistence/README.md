# Persistence 源码导航

本程序集承载公开 Repository／WorkingCopy／Checkpoint 入口和默认持久化集成。公开入口在 `Atelia.DurableGraph`；
共同内核、固定员工、绑定表与模型执行环境在 `Atelia.DurableGraph.Persistence`。
[DB-092 E3](../../docs/design-branches/0092-type-namespace-refactoring/README.md#e3-status)将旧阶段目录并入本目录，源文件按现行职责命名，多类型文件保持共置。
固定员工的持久 SchemaId 与 Family 宿主保持，C 的生成与存档资格见[实施记录](../../docs/design-branches/0092-type-namespace-refactoring/implementation-c.md)。
B2 隔离分支采用 P0G2 业务无 Parent 与新 D/T 管理图入口，原回归资格及剩余合入/包边界见[施工记录 §9](../../docs/design-branches/0096-history-and-head-model/branch-cutover-plan.md#9-b2-调用闭包与回归迁移)；上游 HeadStore 格式与三入口书签布局变更见 [DB-096](../../docs/design-branches/0096-history-and-head-model/headstore-adaptation.md)。原 meta 内核合同与限定资格维护在
[DB-087](../../docs/design-branches/0087-meta-bootstrap-refactoring-route/0087-meta-bootstrap-refactoring-route.md)。
统一 Catalog 与 Private/ToShared 删除见 [DB-096 删除切片](../../docs/design-branches/0096-history-and-head-model/private-removal.md)；开发隔离改用完整关闭库副本。DB-088 保历史故事，查询后继见 DB-089，不从旧逐片“下一步”恢复排期。

两种变化来源共用一套对象版本保存和一个发布 owner：业务 Capture/比较与受控 meta 编辑。
catalog 用固定员工 DTO 保存 Schema/TypeExpr，再经原工厂投影；存活目录使用 Dictionary body。
动态 Base 按其 batch origin S 的 descriptor 解释，selected view 核完整 retained 布局。
追加后准备安装材料，FS/HS 确认发布后才接受；Event 接受 meta/Head，保最近 State 基线。

| 任务 | 源码入口 |
|---|---|
| 历史 checkpoint 书签目录 | [RepositoryResources.Bookmarks](RepositoryResources.Bookmarks.cs) |
| 公开仓库、持续编辑与历史读取 | [Repository](Repository.cs)、[WorkingCopy](WorkingCopy.cs)、[Checkpoint](Checkpoint.cs)、[BranchId](BranchId.cs)／[CommitId](CommitId.cs)／[CommitKind](CommitKind.cs) |
| graph／branch-ref／tag 发布失败合同 | [RepositoryPublicationException／Outcome](RepositoryPublicationException.cs)；内部 [RepositoryOperationStage](RepositoryOperationStage.cs) 仅为故障注入 hook |
| 准确执行环境与共同恢复／比较 | [ModelExecutionEnvironment](ModelExecutionEnvironment.cs)、[GraphReader](GraphReader.cs) |
| meta 查询、意图与 Schema 工厂 | [SchemaCatalogView](SchemaCatalogView.cs)、[SchemaCatalogWorkspace](SchemaCatalogWorkspace.cs) |
| 固定员工、登记与有限绑定 | [BootstrapRecords](BootstrapRecords.cs)、[BootstrapBindings](BootstrapBindings.cs) |
| 对象 batch、目录、图与准确来源 | [GraphRecordStore](GraphRecordStore.cs)、[GraphFrameCodec](GraphFrameCodec.cs)、[StoredGraphDecoder／TypedBaseBodyCodec](StoredGraphBodies.cs) |
| 存储图、对象链与共同保存材料 | [GraphPersistenceContracts](GraphPersistenceContracts.cs)、[PendingRepositorySave](PendingRepositorySave.cs) |
| 业务准备、真实 B/D/H 与安装 | [BusinessPersistenceAdapter](BusinessPersistenceAdapter.cs)、[BranchSaveSession](BranchSaveSession.cs)、[多分支协调](Repository.GroupCommit.cs) |
| D/S/T、业务 Head 与发布/fault/资源 owner | [RepositoryResources](RepositoryResources.MetaBootstrap.cs)、[共同发布内核](RepositoryResources.GroupSave.cs) |
| owned 物理缓存 | [GraphPhysicalReadCache](GraphPhysicalReadCache.cs)、[缓存合同](../../docs/design-branches/0067-owned-revision-read-cache-design/README.md) |

单提交与 `CommitBranches` 复用共同发布内核；请求绑定活 WorkingCopy 和准确 Base，执行时捕获。当前保存统一向资源 owner 的 accepted Catalog 准备，准确历史 Schema 与 source/prior 验证保留；旧 Private/ToShared 合同不再适用。实际普通包门禁见
[PackageConsumerProbe](../../experiments/PackageConsumerProbe/README.md)。
旧 raw 消费者只保模型编译资产，不授原运行故事资格。进程 Kill 见证使用
[MetaBootstrapCrashProbe](../../experiments/MetaBootstrapCrashProbe/README.md)的旧版本记录；该 host 尚待迁移新 Branch API，不能替本次组内核授进程 Kill 或断电资格。
逐片历史稿与旧格式从 `6dbaf0e` 查阅；当前产品不保第二保存/恢复路径。
