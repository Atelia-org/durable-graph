# 默认 meta 自举持久路径

本目录是当前产品核心；历史文件名暂保，普通内核类型已按 [DB-092 B](../../../docs/design-branches/0092-type-namespace-refactoring/README.md)
进入 `Atelia.DurableGraph.Persistence`，固定员工与固定绑定表的旧 CLR 名／namespace 留待 C 片。
内部 wire 标识保持。完整合同与限定资格只维护在
[DB-087](../../../docs/design-branches/0087-meta-bootstrap-refactoring-route/0087-meta-bootstrap-refactoring-route.md)。
开发 Schema 与查询后继见 DB-088/089，不从旧逐片“下一步”恢复排期。

两种变化来源共用一套对象版本保存和一个发布 owner：业务 Capture/比较与受控 meta 编辑。
catalog 用固定员工 DTO 保存 Schema/TypeExpr，再经原工厂投影；存活目录使用 Dictionary body。
动态 Base 按其 batch origin S 的 descriptor 解释，selected view 核完整 retained 布局。
追加后准备安装材料，FS/VS 确认发布后才接受；Event 接受 meta/Head，保最近 State 基线。

| 任务 | 源码入口 |
|---|---|
| meta 查询、意图与 Schema 工厂 | [SchemaCatalogView](P0CatalogView.cs)、[SchemaCatalogWorkspace](P0CatalogWorkspace.cs) |
| 对象 batch、目录、图与准确来源 | [GraphRecordStore](P0GraphStore.cs)、[GraphFrameCodec](P0GraphWire.cs)、[StoredGraphDecoder／TypedBaseBodyCodec](P0TypedBodies.cs) |
| 业务准备、真实 B/D/H 与安装 | [BusinessPersistenceAdapter](P0BusinessWorkspace.cs)、[BranchSaveSession](P0Session.cs) |
| 两根与发布/fault/资源 owner | [GraphResources](../GraphResources.MetaBootstrap.cs) |
| owned 物理缓存 | [GraphPhysicalReadCache](P0GraphReadCache.cs)、[缓存合同](../../../docs/design-branches/0067-owned-revision-read-cache-design/README.md) |

准入、private 与 ToShared 规则从 DB-087 当前合同进入；实际普通包门禁见
[PackageConsumerProbe](../../../experiments/PackageConsumerProbe/README.md)。
旧 raw 消费者只保模型编译资产，不授原运行故事资格。进程 Kill 见证使用
[MetaBootstrapCrashProbe](../../../experiments/MetaBootstrapCrashProbe/README.md)，不授断电保证。
逐片历史稿与旧格式从 `6dbaf0e` 查阅；当前产品不保第二保存/恢复路径。
