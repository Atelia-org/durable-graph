# DB-096：统一 Schema Catalog 与开发库隔离

> 2026-10-19，一个 Repository 只维护统一的 Schema 接纳路线。Private / ToShared 已删除并完成限定验收，结果统一见[删除切片](private-removal.md)。本页保留设计依据、前轮机制见证与删除合同。

## 1. 决定及不可混淆的边界

同库所有业务 Branch 共用一个持续演进的 Catalog，同 SchemaKey 的已接受定义不可改形。不同业务类型、正常不同版本可以共存；Branch 分叉业务历史，不再分叉 Schema 接纳路线。历史图的 SchemaRoot 和历史 batch 的解释来源继续准确固定，不用 current Catalog 替代。

开发故事采用完整、关闭后的稳定 Repository 副本，各试验独立保存；模型代码、编译 history 与构建输出也隔离。定稿后在干净正式基线上进行正常升级；没有原始帧合库或自动把试验数据推广回正式库的承诺。文件系统快照可作为环境优化，当前先验证普通完整复制，不在 DG 新建快照平台。

## 2. 对外与内部模型的简化

API 草稿已经去掉 SchemaMode；`OpenWorkingCopy(branchId)`、两个 CreateRootBranch 入口和 CommitRequest 都没有写入归属选择。Private 新根、Shared/Private 混组与 promotion 队列关闭。保资源只读、完整模型/Upgrade 准入与故障处理，不再把打开可写资源与遗漏 Schema 参数绑定。

**下一内核首选一个仓库接受的 Catalog，加一个操作内共同候选。** 收集本组捕获布局及必要的内部显式声明，检查完整闭包和冲突，一次 Builder 分配；所有业务按同一最终 view 编码。候选成功发布前不修改 accepted，无新增定义可复用旧根。单分支是单成员组。

这比“每 WorkingCopy 保存一份可变 Catalog，然后组内合并它们”更小。源码 [BranchCheckout.Catalog](https://github.com/Atelia-org/durable-graph/blob/866ce89705888af5fea56a5532c7d1986a21ca65/src/DurableGraph.Persistence/BranchCheckout.cs "Pre-B2 source: Git 866ce89") 和 [SchemaCatalogWorkspace.Set](../../../src/DurableGraph.Persistence/SchemaCatalogWorkspace.cs) 都是 internal，当前产品调用从 [BranchSaveSession.Prepare](../../../src/DurableGraph.Persistence/BranchSaveSession.cs) 的 capture layouts 收集定义；没有已公开的逐副本 dirty 管理 API 必须保留。显式输入仍可用于内部机制验证，不为旧测试新增公开 Schema 编辑平台。

共享写入 Catalog **不合并**各业务图的身份、State 基线、准确历史 Schema 或冻结执行环境。有 State 时从该 State 的准确视图恢复；无 State 时沿统一写入路线建立当前配置环境。Event 只推进逻辑 tip，不安装 State/rewrite。保留对象/目录 source 与 Delta prior 验证，完整旧 Schema 和容器声明持续存在。

角色与世界可以使用不同类型；正常 V1/V2 也能共存。唯一限制是同库同 SchemaKey 不允许异形。未来只有出现“必须同库长期运行冲突定义并共同提交”的真实应用才重访多目录；当前故事不要求它。

## 3. 两个可执行机制见证

本轮问题与最小验收：开发隔离能否转移到完整库副本而保留身份/历史；统一 Catalog 是否能真实编码并冷读两份不同业务图、在写入前拒绝跨输入冲突。均使用当前已实施 Shared 管线/内部原语，不实现新 API 外观。

### 3.1 独立副本开发与定稿

源码：[SharedSchemaCopyWorkflowTests](../../../tests/DurableGraph.Tests/SharedSchemaCopyWorkflowTests.cs)，单 Fact `SharedSchemaRepositoryCopiesIsolateDraftsAndUpgradeCleanReleaseBaseline`。

- 真 SG V1 保存含环、共享 List 的图；先消耗低位 IDs，再更换图/child 升根，防止“ID相等只是从1分配”的假见证。
- 全部 owner 关闭后将整个 V1 库复制成 A、B 和 release，逐相对路径比较文件集合与 SHA256；模板及尚未定稿的 release 不变。
- A/B 都只用现有 `CheckoutShared`/`CommitState` 接纳同 key 异形 V2；分别保身份、升级 Base、List head；关闭重开续写真实 Delta、准确 prior/source/H，再冷读。
- A/B 错模型交叉读回拒绝，读取后完整文件 hash 不变。
- compiler history 只接受 V1＋定稿 B；release 从干净 V1 用普通 Shared 升级并冷续写，无 ToShared，无试验帧导入。当前 Catalog 已含 V2 时，旧 V1 模型仍能按书签准确读 V1。

编译 history 使用真实 SG 与 SchemaHistoryTool；不是打包 MSBuild Off/Publish targets 或普通包消费者验收。复制/重开在同一测试进程串行完成，没测 OS 快照、进程终止或设备断电。既有 DB-088 的 Discard/Event 保 rewrite 断言未在这个新 Fact 迁移，删除实现时按 §4 转移，不称全面替代原 suite。

完整复制会复制底层身份与已有坐标；每份副本必须按自己的仓库路径使用。它们以后可独立分化，不能靠复制前相同的身份/坐标判断后来数据相同，也不借此建立跨副本合并或误用强检出合同。

### 3.2 一份共同 Catalog 编码两份图

源码：[SharedCatalogGroupPreparationTests](../../../tests/DurableGraph.Persistence.Tests/SharedCatalogGroupPreparationTests.cs)，3个方法／4个用例。

- 两份真实 generated Widget/Envelope 分别 Capture，汇总捕获布局与显式不可达声明；已有 Catalog 描述符保持，一次共同候选编码两个业务根。
- 真实追加 Meta/State 帧并 ConfirmDurable，全部关闭后重新打开读取准确 Catalog 与两份对象；检验环、List、根类型及准确 Schema pin。
- dirty-dirty 同 key 异形、显式声明与容器 inline 依赖异形都在 append 前拒绝；对照表明直接连续 Set 会覆盖未接纳同 key，不能当合并算法。
- 两份独立 Catalog 可给不同类型分配相同 descriptor 数字；将一份已编码 body 改 pin 到另一目录，会被解释成另一类型，证明“选最后目录”不可行。

该测试是**内容准备/编码/冷读**见证，直接通过内部 Store 追加，没有发布业务 Heads。它不覆盖真实 WorkingCopy 候选收集/消费、组原子发布、State/Event 基线安装、CommitRequest 生命周期或性能。显式声明数组是 test-only 输入，不是假装已读出各工作副本的 dirty，也不要求未来保留 per-copy Catalog。

## 4. Private 实现删除合同

用户已授权后续移除，不设兼容入口或自动导入旧 Private 数据。源码删除宜作为独立小片，先保持当前业务历史布局，不与整个一 Branch 一 Head 重构同时进行。

| 范围 | 删除 / 简化 | 必须保留或迁移 |
|---|---|---|
| 公开入口 | `CheckoutPrivate`、两项 `CommitStateToShared`、`CommitEventToShared`；新外观无 SchemaMode | 普通创建/编辑/提交、只读资源和显式持久化；旧 Shared 命名随新 API 切片调整，不为过渡增加 alias |
| 私有路由 | `UsesPrivateSchemas`、`_privateCatalog`、`privateCatalog/toShared` 参数、`PrivateCatalogRoot`、`PreparePrivateFrom/PreparePrivateSaveFrom`、`PendingRepositorySave.PrivateCatalog` | owner 单操作窗口、准确业务 source、Catalog 实际来源、expected tip/State、寿命及重入保护 |
| 推广 | `PrepareToSharedOwned`、`CollectToShared`、完整私有目录导入/remap | `PrepareOwned`、闭包/声明验证、Builder、准确 Catalog pin；操作内候选代替 per-copy 接纳状态后仍需检查当前 Catalog 冲突 |
| Shared 特殊准入 | `RequireSchemaWriter` 中 selected view 全表是否包含于 shared 的 Private admission 扫描 | 其 `RequireWriter/RequireCheckoutSource` 必须保留；普通闭包、Schema/layout 冲突及准确图/batch/source/prior 校验不删 |
| 测试 | Private 不推进 Shared、永不跟随 Shared、推广后仍 Private、跨目录 remap、未推广目录拒绝等退出语义 | 普通升级/缺 Upgrade、完整帧保持、readonly/fault、reentry、Event-only、Event/Discard 保 State/rewrite、错 source 及冷 Delta 证据转入统一路线 |
| 包消费者 | Development 改独立副本；PrivateContainer 改普通容器升级；Split 退出 Private/promotion 调用 | Development 的真实 MSBuild、容器缺规则、Event 接纳 Schema 但 State 不推进、准确历史与冷开故事，用新包重新验证 |

删除全表 admission 的成立前提是：**受支持的新库中所有写入均来自统一单调 Catalog**，同 key 不变，没有旁路导入旧 Private 分支。旧格式可能隐藏 private 历史，不能仅因当前根看起来 Shared 就放行；删除片以 P0R2 分支目录标记拒绝 P0R1 旧布局，不做启动全历史审计或迁移器，完整帧 append-only 纪律不因此放松。

关键源码入口：[Repository.RequireCheckoutWriter](../../../src/DurableGraph.Persistence/Repository.cs)、[保存会话](../../../src/DurableGraph.Persistence/BranchSaveSession.cs)、[Catalog](../../../src/DurableGraph.Persistence/SchemaCatalogWorkspace.cs)、[发布 owner](../../../src/DurableGraph.Persistence/RepositoryResources.MetaBootstrap.cs)。已删除的 Private 入口与旧回归可在 Git `a347a2f` 查阅；内部 Catalog/workspace 不按关键词整束删除。

测试迁移保留中性材料：[RepositoryEvidenceHelpers](../../../tests/DurableGraph.Tests/RepositoryEvidenceHelpers.cs)从旧 Private view 文件提取帧/文件/资源检查；新复制见证继续复用 Development 模型/身份检查。Event/Discard 的 rewrite 保留转为普通 Shared 见证；“合并 Catalog 正确”不替代这项验收。具体覆盖映射见删除切片。

## 5. 本轮基线、验证与限度

DG 起点 `19bd6ae6131f0573223b6083a8470995ba46ee2c`。Storage 使用当前 DG pin `731eef7cd3158572532fbeea7ba95a79213a7328`，通过独立 detached worktree `.artifacts/db096-shared-schema/storage-731eef7` 构建；上游主工作树仍为 `f8868b5`，未修改或切换。没有修改 DG pin、包版本或环境变量，也没有将旧 pin 验证称作最新 HeadStore API 接入。

验证命令（先准备上述精确提交的 worktree，以下从 DG 根执行）：

```powershell
$storageSources = (Resolve-Path .artifacts/db096-shared-schema/storage-731eef7).Path
dotnet restore DurableGraph.slnx -p:UseStorageSources=true "-p:StorageSourceRoot=$storageSources"
dotnet build DurableGraph.slnx --no-restore -p:UseStorageSources=true "-p:StorageSourceRoot=$storageSources"
dotnet test tests/DurableGraph.Tests/DurableGraph.Tests.csproj --no-build --no-restore -p:UseStorageSources=true "-p:StorageSourceRoot=$storageSources" --filter FullyQualifiedName~SharedSchemaRepositoryCopiesIsolateDraftsAndUpgradeCleanReleaseBaseline
dotnet test tests/DurableGraph.Persistence.Tests/DurableGraph.Persistence.Tests.csproj --no-build --no-restore -p:UseStorageSources=true "-p:StorageSourceRoot=$storageSources" --filter FullyQualifiedName~SharedCatalogGroupPreparationTests
```

最终根 solution build 零 warning/error；两组共5个不同用例通过（复制1、共同Catalog4），无失败/跳过。独立审阅提出的“已演进 release 用旧模型读 V1”已补入并重新通过复制用例；另明确保留尚未迁移的 Event/Discard 断言。运行证据为忽略目录 `.artifacts/db096-shared-schema/build.log`、`copy-workflow-final.log`、`catalog-group.log` 及 `results/` 下对应 TRX；见证源码随 Git 管理。

本轮没有修改产品 `src` 下的 C#、固定依赖或持久格式；没有运行全套回归、普通包消费者、最新 HeadStore 接入或产品组事务资格。初次5例成功及补强后的重复1例不累计成6个不同用例。

文档集成校验：16份受影响 Markdown 的741个本地链接（含100个锚点）通过；已复核集成差异和空白检查。DB-087/088/095 与早期机制/命名审阅保留历史含义，并加后继指引；活动 API、目标与路线图已消除 Private 续工要求。

## 6. 当前停点与后续顺序

已选：同库统一 Catalog；新 API 无归属参数；开发试验复制完整关闭库。首选内部模型进一步收窄为仓库 accepted Catalog＋操作内共同候选，保各业务冻结执行环境与基线。两份机制见证支持该方向；产品删除进度与资格见[独立切片](private-removal.md)。

删除片已完成；后继[组内核](group-kernel-design.md)的候选清理与真实混组发布/冷续写已有原语见证，旧目录不再增加正式组协调器。[现有来源 snapshot 已贯穿并验收](history-source-seam.md#7-a-实施与验收)，新依赖[B1](storage-handle-adaptation.md)之后，下一片按[整体切换计划](branch-cutover-plan.md)验证 Parent 替换后的准确来源及旧调用迁移；最终在新布局接共同 Catalog/组内核及故障验收。示意 Probe 与旧布局见证不代替新 DG 模型资格。
