# 独立移除 Private / ToShared

> 2026-10-19；已实施并完成限定验收。实现提交 `efdf61b8976dde653e43514e285d78547c1b719b`，起点 `a347a2f`；合同见[统一 Catalog §4](shared-schema-only.md#4-private-实现删除合同)。本页是分支会话交回设计主线的结果入口。

## 范围与验收

只保留统一 Shared 接纳路线，删除公开 Private/ToShared 入口及内部私有发布、推广和专用 admission。保留现有业务历史布局、Shared 命名、准确历史 Schema、冻结执行环境、State 基线及 owner/fault/source 检查。不在本片实施新 Branch API、组提交或操作内 Catalog 重构。

最小成功标准：产品无 Private/ToShared 执行路径；旧格式明确拒绝；普通升级、Event/Discard 保 State/rewrite、冲突写前拒绝、冷开 Delta 与完整帧保持有回归证据；源码根构建与相关完整 suite 通过；迁移的普通包消费者用本次新包验证。

## 已选实现边界

- 必读 Head 1 分支目录的 magic `P0R1` 升为 `P0R2`。旧布局在根解码时拒绝，不扫描全历史或导入旧 Private 内容。Shared/书签编码、图 ABI 不随此片更改。
- 保留内部每 checkout Catalog 工作区作为既有保存机制；仓库 accepted Catalog＋操作内共同候选属于后续组提交切片。
- 开发草稿使用完整关闭库的独立副本。容器消费者转普通升级；Split 保 Event 接纳新定义而不推进 State 的故事。
- 历史测试只退出已取消政策；中性夹具和准确历史、升级、失败边界断言迁入统一路线。

## 实施结果

运行时、Persistence 回归、Generator 回归和普通包消费者分四个有界工作包交付，文档独立维护；主线程集成并运行验收，另做独立正确性及消费者审查。本片无剩余实施项。

`CheckoutPrivate`、两个 `CommitStateToShared`、`CommitEventToShared`、`UsesPrivateSchemas`、private/toShared 路由及完整目录推广代码已删除，没有兼容别名。`RequireCheckoutWriter` 保 writer/source 保护；保存以 owner 当前 Shared Root 为唯一 meta 来源，Catalog 增长时才共同发布业务与 Schema Heads。闭包/布局冲突、准确历史 Schema、完整帧和生命周期检查保持。

首轮回归暴露四个 Capture 回调使 Store/owner/backing 失效的用例：普通 Shared Event-only 路径先准备了 body 才拒绝。旧 promotion helper 的 Capture 后检查属于应保留保护；现已在统一 `BranchSaveSession` 的 Capture 后、Catalog/body 准备前补 `Store.EnsureUsable()`。独立审阅认可该位置；四个断言未放宽，修复后全部通过。另修复一个遗留 Generator 测试与 CrashProbe 的 P0R1 预期为 P0R2。

验证继续使用 DG 固定 Storage `731eef7cd3158572532fbeea7ba95a79213a7328` 的独立 worktree `.artifacts/db096-shared-schema/storage-731eef7`；不切换上游主工作树或升级依赖。命令基线见[前轮验证](shared-schema-only.md#5-本轮基线验证与限度)。

## 回归覆盖与最终证据

- Debug/Release 根构建零警告/错误。最终完整 Release suite **2809/2809**：Generator/runtime 1721、Persistence 919、Serialization 133、Storage 36；零失败/跳过。初轮五个失败均已修复，不把重点复验重复计入总数。
- CrashProbe 构建通过；真实 controller Kill 初始化 3/3、保存 6/6 通过。只授 ProcessCrashOnly 有限窗口资格，不授断电或 OS crash 资格。
- 真实完整 P0R1 根由裸 backing stores 在 DG owner 全关后追加发布；公开只读和可写打开均准确拒绝，健康完整文件不变。没有改写旧完成帧或模拟完整历史审计。
- 代码与迁移覆盖经独立审阅；全仓已删除文件入链及本次文档链接/锚点检查通过。消费者审查发现的 Split receipt 旧推广措辞已修正并用同包重跑成功。

| 当前回归 | 从旧测试保留的有效语义 |
|---|---|
| [SharedSchemaRepositoryTests](../../../tests/DurableGraph.Persistence.Tests/SharedSchemaRepositoryTests.cs) | 高位 ID/环/共享 List、冷 Delta、Event 与 State 视图分离、Discard、准确来源、readonly/占用及 Event-only 首 State |
| [SharedSchemaSaveSafetyTests](../../../tests/DurableGraph.Persistence.Tests/SharedSchemaSaveSafetyTests.cs) | 布局增长联合发布、dirty/capture 冲突、rewrite、重入、四类资源失效、准确 raw 来源、发布失败窗口及完整帧 |
| [DevelopmentSchemaWorkflowGeneratorTests](../../../tests/DurableGraph.Tests/DevelopmentSchemaWorkflowGeneratorTests.cs) | 真 SG Upgrade→Discard→Event→冷开→State Base rewrite→冷 Delta，旧模型准确读 V1 |
| [SharedContainerSchemaUpgradeGeneratorTests](../../../tests/DurableGraph.Tests/SharedContainerSchemaUpgradeGeneratorTests.cs) | 缺 List 规则两次健康拒绝、Nullable/Array/List/Dictionary 升级、身份与 Base/Delta/prior/H |
| [SharedSchemaCopyWorkflowTests](../../../tests/DurableGraph.Tests/SharedSchemaCopyWorkflowTests.cs) | 完整副本 A/B 的同 key 异形独立开发、干净 release 定稿、旧历史及完成帧保持 |

中性帧/文件/资源助手集中到 [RepositoryEvidenceHelpers](../../../tests/DurableGraph.Tests/RepositoryEvidenceHelpers.cs)。Private 不推进 Shared、冻结私有目录、推广后仍 Private、完整私有导入/remap 等取消政策的断言退出；不另建公开 Schema 编辑 API 来保旧测试。

源码日志/TRX、crash receipts、文档检查、包 receipt 路径与摘要索引位于 `.artifacts/db096-private-removal/`。最终全量记录为 `release-suite.log` / `release-suite/*.trx`，构建为 `build-final.log`，crash 为 `crash-init/receipt.json` / `crash-save/receipt.json`。Crash 运行在实现提交前的相同工作树，receipt 的 HEAD 为起点，另含实际输入/DLL hash；不把该 HEAD 误称运行源码版本。测试源码、消费者和控制脚本随 Git 管理；日志为本地忽略工件。

## 新包资格

受控普通包准备来自干净提交 `efdf61b`，G 为 `0.0.0-public-history-e2e.20261019041359.30628`，四个 DG 包；S 保持 `0.1.3-dev.20261017.g731eef7`，六个 Storage 包。上游 manifest、包 hash、DG 生产输入和实际编译/加载证据由 runner 验证；没有远程发布或升级 pin。

| 门禁 | 结果与范围 | 本机完整 receipt |
|---|---|---|
| Development | 5 个独立消费进程通过；V1 全关复制 A/B/release、独立编译 history、Off 异形试验、B Publish/Verify、A 冲突拒绝、干净 release Shared 升级 | `W:/TEMP/dg-db096-development-20261019T041516/result.json` |
| Split | 5 个进程通过；准确历史/Upgrade/Pair/Fork、Event 接纳新 Catalog 不安装 State、冷增量、只读和生命周期；已用修正 Scope 的 runner 重跑 | `W:/TEMP/dg-db096-split-final-20261019T041621/result.json` |
| 容器升级 | 7 个进程通过；Nullable/List 升级、精确缺 List 规则拒绝、冷 Delta/prior/H 和完整帧；沿用历史 `PrivateContainerConsumer` 文件路径 | `W:/TEMP/dg-db096-container-20261019T041516/result.json` |
| Repository API | 11 项精确编译负例（含删除的四项调用）、恢复合法源码并重新 Verify build、冷保存通过 | `W:/TEMP/dg-db096-api-20261019T041516/result.json` |

完整十包 feed 和 `dg-source-provenance.<G>.json` 位于 `W:/TEMP/dg-db096-private-removal-split-20261019T041356/feed`；受控准备的 `ProductionInputsMatchHead`、`WholeWorktreeClean` 均为 true。本轮结束只补文档和 runner Scope 描述；容器 runner 在成功后去掉 Scope 中的 pending 措辞，不改变断言，原 receipt 保其实际输入 hash。上述资格不覆盖新 HeadStore 接入、新 Branch/Commit API、组提交、性能、断电或 OS crash。

重跑时先按[依赖工作流](../../storage-dependency.md)准备精确 S feed。`Run-SplitRepositoryProbe.ps1 -StoragePackageSource <S-feed> -WorkDirectory <fresh-outside-repo>` 会制作新 G；其余三门禁传 `-PackageSource <generated-feed> -Version <G>`（容器额外传相同 S feed）。不覆盖同版包或旧 receipt。

## 返回设计主线

1. Private/ToShared 已彻底退出；以后设计不再考虑 schema 模式组合或推广。旧库 P0R1 不受支持，所有打开方式拒绝，不提供导入/迁移器。
2. 当前仍是三控制 Head、名称到业务图地址的目录、`GraphEnvelope.Parent` 和现有 Shared API。没有借本片实现一 Branch 一 Head、新 CommitId、登记协议或组提交。
3. **下一设计题是仓库 accepted Catalog＋操作内共同候选。** 每 checkout 内部 Catalog 还在；业务对象身份、State 基线与冻结执行环境必须继续独立。然后再接共同发布及全体安装。
4. Capture 可执行用户代码。后续重组 prepare 流程时必须保留 Capture 后、body 准备前的 owner/Store 可用性检查，不能靠有 State 的 baseline 检查间接覆盖 Event-only。
5. Storage pin 未变；已恢复 root solution 的显式 pinned source-mode assets。上游最新 HeadStore 的产品接入仍是单独工作，不借本次新包结果取得资格。
