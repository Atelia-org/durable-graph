# DB-079 施工与验收：共用图恢复核心

> 状态：**已实施并独立验收**；基线 `1ff5571`；2026-09-28。合同见 [DB-079](0079-shared-graph-restoration-core-slice.md)，公开语义见 [DB-083](0083-repository-checkpoint-api-user-stories.md)。

## 范围与成功标准

统一单图准备材料及 Allocate/Register → Hydrate 机制，使独立读取、Checkpoint、Checkout/Fork 与显式 ReadPair 使用同一核心。
历史选择、公开 API、持久格式、发布和故障协议保持；不引入跨操作缓存、Schema 证书或 immutable 叶复用，也不宣称加速。
只读交付不构造保存身份材料；可编辑恢复仍导入完整 source membership、来源/head/H、重写义务、字符串身份与 ID 游标。
ReadPair 的共享闭包与完整值比较保持；独立图不规划 mutable 复用，操作内 singleton allocator 不能绕过冲突检查。

## 实施与证据账本

| 合同 | 实施归属 | 验收入口 |
|---|---|---|
| 完整单图准备材料，不额外保留 decoded 目录 | 核心 agent：GraphReader 与内部材料 | 完整来源、不可达行、head/H 与后继保存测试 |
| 共用两阶段物化，可信共享与一般 allocator 冲突分开 | 核心 agent：GraphReader | SharedGraphReaderTests 与新增 DB079 行为见证 |
| 只读交付和 editable 身份导入分开 | 核心 agent：读取接线 | 独立 Checkpoint、ReadEvent、工作区保存回归 |
| Event-only、精确 Head、跨类型 State、Fork 发布保持 | 不改历史/发布机制 | DB078 A/B/C 回归与完整 Persistence 测试 |
| 验收矩阵补缺 | 测试 agent：DB079 测试文件 | 关键失败顺序、隔离与保存后果 |
| 源码裁决、集成、文档与本地提交 | 主线程；另设独立只读审查 | 根构建、相关回归、实际 diff 与本地链接检查 |

## 验收结果

产品改动限于 [GraphReader](../../src/DurableGraph.Persistence/GraphReader.cs)、
[PreparedGraphSelection](../../src/DurableGraph.Persistence/PreparedGraphSelection.cs)、
[Repository](../../src/DurableGraph.Persistence/Repository.cs) 的只读接线与
[RevisionReadSession](../../src/DurableGraph.Persistence/RevisionReadSession.cs) 的说明。
WorldWorkspace/LoadedWorld 沿原 editable 入口进入共用核心；未改历史导航、Fork 发布、存储依赖或公开签名。

- `Prepare` 为每张实际选择的图完整解码、归一化、验证并求可达顺序，材料保留完整 normalized 源；不再额外持有 decoded 目录。
- `Materialize` 统一分配与填充。独立两图使用各图非空 string 冲突检查和操作内 mutable 冲突检查；Pair 仅可信闭包可跳过重复分配/填充。
- `ReadRoot` / `ReadIndependent` 只交付根；`Read<T>` 经 `DeliverEditable` 单独构造保存身份，导入全源 strings 和 max-ID 游标。
- Pair 的 head 证明改读 normalized 行的 `Storage.Head`；`RevisionDecoder.ReadCore` 已逐行验证它等于 exact head map。
  未删除闭包、完整值比较、RequiresRewrite 排除或错误传播；缺 storage provenance 不获得共享资格。

有意加强的失败顺序：Checkpoint 两图先完成全部 Prepare，然后全部 Allocate 和引用表构造，再 Hydrate。
第二图准备失败时第一图尚未分配；第二图分配失败时两图均未填充。全部成功后才交付，仍不回滚应用回调自身的副作用。
审查发现 `ObjectReadTable` 构造会复制并验证目录，已将两张表都移到任何 Hydrate 之前；不能只以实例 Dictionary 已存在作为完成建表的证据。

### 合同到可执行证据

| 合同/风险 | 本次通过的测试入口 |
|---|---|
| Pair 两表先分配、晚失败零 Hydrate；无 equality proof 的 singleton 不得共享 | 新增 [DB079GraphRestorationTests](../../tests/DurableGraph.Persistence.Tests/DB079GraphRestorationTests.cs)：`PairCompletesBothAllocationTablesBeforeAnyHydration`、`EqualExactHeadWithoutEqualityProofDoesNotAuthorizeSingletonReuse` |
| Checkpoint 两图预分配、singleton 拒绝、第二图准备失败零 Allocate、可重试 | 同文件：`CheckpointCompletesBothAllocationTablesBeforeAnyHydration`、`CheckpointSingletonCollisionRejectsBeforeHydratingEitherGraph`、`CheckpointPreviousGraphPreparationFailsBeforeEitherGraphAllocates`；最后一项使用真实 v1→v2 Upgrade，不误用会绕过转换委托的 exact-current 路径 |
| 稳定环共享、changed-child 分裂、比较/第二图 Hydrate 错误、字符串版本身份 | [SharedGraphReaderTests](../../tests/DurableGraph.Persistence.Tests/SharedGraphReaderTests.cs)：`StableCycleDecodesAllocatesAndHydratesOnlyOneCopyAcrossDifferentRevisionViews`、`RewrittenChildSplitsItsEntireCycleEvenWhenItsNewBytesAreEqual`、`ComparisonFailurePropagatesWithoutEncodingAllocationOrRetry`、`SecondHydrationFailureDoesNotDeliverHalfAPairOrWriteEitherStore`、字符串共享/Empty 两组 |
| 独立可变图、稳定 getter、只读一份 Event、至多两图与 reentry | [DB078CheckpointTests](../../tests/DurableGraph.Persistence.Tests/DB078CheckpointTests.cs)：`CheckpointGraphsKeepCyclesAliasesAndListsButIsolateAllMutableViews`、`ReadEventAllocatesAndHydratesOnlyItsReachableGraph`、`NearestOppositeRoleUsesStrictAncestorsAndRestoresAtMostTwoGraphs`、`LaterGraphFailureOrReentryDeliversNoCheckpointAndReleasesBusy` |
| 不可达 source 高 ID、完整 strings、Empty 最小 ID、Remove/首次 Base/后继 Delta | [WorldWorkspaceTests](../../tests/DurableGraph.Persistence.Tests/WorldWorkspaceTests.cs)：`UpgradeRewriteAndCompleteSourceMembershipSurviveDiscardThenClearOnInstall`、`EmptyAliasesCollapseOnlyAtSuccessfulInstallAndThenRemainStable` |
| exact Store 来源/head/H、含待删除行的完整来源校验 | [WorldWorkspaceStorageBaselineTests](../../tests/DurableGraph.Persistence.Tests/WorldWorkspaceStorageBaselineTests.cs)：`CachedDecodedRowsCarryStorageIntoAnEditableLoad`、`LoadedPlanningRejectsMissingOrForeignStorageIncludingRowsBeingRemoved`；[LoadedReferenceWorldTests](../../tests/DurableGraph.Persistence.Tests/LoadedReferenceWorldTests.cs) 的长链、实际类型与晚期失败 |
| Event-first、异型根及 child 升根/旧 root 降为 child 的身份 | [DB078HistoryTests](../../tests/DurableGraph.Persistence.Tests/DB078HistoryTests.cs)：`EventOnlyColdCheckoutNeedsNoModelsAndFirstStateCanReplaceTypeAndSaveAgain`、`ChildPromotionAndOldRootDemotionPreserveIdentityAndUnreachableObjectsAreRemoved` |
| 连续 Event 中间 Fork 仅需 State 模型、空目录 Event-only Fork、升级后 Event 不消除 State rewrite | [DB078ForkModelTests](../../tests/DurableGraph.Persistence.Tests/DB078ForkModelTests.cs)；[EventHistoryRepositoryTests](../../tests/DurableGraph.Persistence.Tests/EventHistoryRepositoryTests.cs)：`LoadedUpgradeRewriteIsNotClearedByEventSaveAndLaterStatesUseDelta` 的 Checkout/Fork 两行 |

新增 8 个案例，不修改或删除旧测试。独立 reviewer 已检查实际产品与测试 diff，无遗留阻塞项；主线程另核对 exact-head 来源链、完整身份导入与两表构造时序。

运行证据集中于忽略目录 `.artifacts/db079/`：基线 Persistence **855/855**；根 Release 构建 **0 warning / 0 error**；
完整 solution **2853/2853**，零失败、零跳过（Serialization 163、Storage 202、Persistence 863、Runtime/Generator 1625）。新增 8 项已逐项核对 TRX。
命令为 `dotnet build DurableGraph.slnx -c Release --no-restore`，随后 `dotnet test DurableGraph.slnx -c Release --no-build --no-restore`，保留日志和 TRX。
集成 diff 与 `git diff --check` 通过；9 份变更 Markdown 的 724 个本地链接无断链，既有标题与显式 anchor ID 保持。
本片未改公开 API、包接线、Generator 或存储 pin，不重打包；不能将此前 078-C 的真包验证计作本片重新执行的证据。

## 后续入口

下一片 [DB-080](0080-prepared-checkpoint-reuse-slice.md) 才赋予最近 State 准备材料跨操作复用资格；关键是完整 Schema 依赖证书与成功后的入槽门。
随后 [DB-081](0081-hot-commit-restoration-material-slice.md) 单独证明热提交材料与冷恢复等价；
[DB-082](0082-prepared-immutable-leaf-reuse-slice.md) 是依赖 DB-080 的独立叶复用实验。
当前产品接续以 [PROJECT-STATE](../../src/PROJECT-STATE.md) 为入口，不将本片重构解释为这些优化已经安全或有效。
