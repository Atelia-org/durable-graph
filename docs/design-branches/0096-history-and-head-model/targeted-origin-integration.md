# DB-096：定向来源查询接入

2026-10-19。问题是：DG 描述指定 Branch 时，能否避免全量读取未登记 Head，同时继续拒绝坏目标、坏来源与登记集合中的环？本片实现[定向请求](headstore-origin-request.md)，不新增持久来源副本、索引、树 API 或来源认证类型。

## 实现与边界

上游基线 `f8868b5`，独立分支 `dg-targeted-origin` 的源码提交为 `70b406cd8d1117733eee81a33e7f978399aa98b2`。新增 `AteliaResult<HeadOrigin> ReadHeadOrigin(HeadId)`，小只读结果只含 `HeadEntryId? SourceEntryId`。Result 不允许 null 成功值，因此用此包装表达 root，而不改全局 Result 或用 default ID 充当哨兵。返回值仅表示 checked header 的声明，不证明来源存在或发布成立。

实现复用所选目标解析、checked header、owned/history 借用、临时句柄与既有 cleanup；不调用 List/Visit、ReadHead 或 ReadEntry，不把陌生目标放进长期缓存。保持正常 RW 惰性尾恢复、RO 不修复、最后检查与清理失败不可成功交付。格式4、4B/12B Handle codec、原全集查询及 CU 协议均不变。

DG 的 [ReadOrigin](../../../src/DurableGraph.Persistence/RepositoryResources.MetaBootstrap.cs)直接调用新接口，仍检查目标 current 的业务角色，再验证准确 source Entry 的正式成员资格与角色；这次没有借优化暗改这些拒绝边界。`ReadBranchInfo` 只读指定目标及直接相关来源，不为单点查询扫描祖先全集。

原全集 adapter 附带的环检不能直接消失。[ListBranches](../../../src/DurableGraph.Persistence/Repository.cs)取得所有正式成员描述后，对内存的一父边集合做迭代三色检查，正常完成才返回数组；不增加磁盘读取或持久权威。使用标记 Dictionary 与 List 路径，避免反复 Clear 大 HashSet 容量的隐藏成本；这不构成完整 I/O 或吞吐承诺。后续根/子树可复用此验证边界，仍须独立实现。

## 源码验收

实现、上游测试、DG 反例与独立审阅采用有界工作包，主线程核对实际源码、运行构建并读取 TRX。

- 上游 Release solution build：0警告/0错误；完整 tests **3608通过、10平台跳过、0失败**。新增29项全部通过，覆盖 root/空fork/首存/冷读，header 声明与 source 资格分离，0 List/Visit 及实际读日志、临时/借用资源、cleanup错误，以及目标正常尾恢复。没有新的 Linux、进程中止或掉电资格。
- DG 固定新 revision 的 source 根 build：0警告/0错误；[新增8项](../../../tests/DurableGraph.Persistence.Tests/BranchOriginQueryTests.cs)、完整 Persistence **994/994**、相关 Runtime 三类 **16/16** 均通过且0跳过。新反例包括未登记坏文件隔离、坏/缺目标、子已有正常本地 Entry 仍不能隐藏坏父/准确旧来源，以及两个都有健康本地 Entry 的真实来源环。
- 首轮 DG 992/994，两处 fixture 错误已独立定位：RBF HeaderFence 损坏应为 InvalidDataException；header 内部来源槽使用 raw local Head 编号，不等于公开 HeadEntryId codec 的编码 HeadId。修后保两次单点读取成功，再断言列表环拒绝，没有用坏 header 代替真正环检。原失败证据保留。

上游工件在 `.artifacts/db096-targeted-origin/` 的 `storage-build.log`、`storage-tests.log`、`storage-tests/` 和 `storage-test-summary.json`；DG 为 `dg-build-2.log`、`dg-targeted.log`、`dg-persistence-2.log`、`dg-runtime.log` 及 `dg-tests/`。首轮 `dg-persistence.log` / `persistence.trx` 不作为最终通过证据。

## 依赖与交付

新 S 为 `0.1.3-dev.20261019.g70b406c`，来源 `70b406cd8d1117733eee81a33e7f978399aa98b2`，准确依赖由 [StorageDependency.props](../../../eng/StorageDependency.props)维护。旧固定 worktree/feed 保持；本片新 worktree 为 `.artifacts/db096-targeted-origin/storage`，新六包和六份 symbols 位于同目录的 `storage-feed`。manifest 与 DG Prepare 校验通过，未公开发布。

主线程已完成以下独立包验收：

| 验收 | 来源与实际结果 | 限度 |
|---|---|---|
| Storage 普通包 | 干净 `70b406c` 打包；上游四个 public PackageReference smoke 全部通过；HeadStore 实际调用新 API，验证 root、空 fork、首次本地发布及两层冷重开 | metadata/PDB/静态 SourceLink 及170份本地源码 checksum 通过；未做远端 SourceLink 下载 |
| DG 包模式根构建 | 完整新 S feed、独立 NuGet.Config/cache，Release Rebuild 0警告/0错误 | 不是源码模式的重复输出 |
| DG Split | G `0.0.0-public-history-e2e.20261019084143.30708`，来源干净 `c9ad9351ce761b13f789e5aff9d42bdc051bf908`；五个独立进程场景通过，receipt `Passed=true` | 覆盖升级、rewrite、续写及验证，不代表所有故障窗口 |
| DG Tag | 使用同一 G/feed；seed、metadata、continue、verify 四个进程均通过，runner exit 0 | 覆盖冷书签、Event-first、State fork、持久 ID 和只追加，不代表全套消费者重新资格化 |

工件定位只保一处：`.artifacts/db096-targeted-origin/package-paths.json` 指向本次外部 `W:/TEMP/db096-origin-eff0825f2f624ab9b79a4349747de818/`。其中 `storage-smoke` 保各 smoke 日志和 `verified-packages.json`；`dg-split/result.json` 及其 `feed/dg-source-provenance.*.json` 保 G 来源和结果。DG 日志为本片目录的 `package-root-build.log`、`dg-package-split.log`、`dg-package-tag.log`；Tag 独立工件为 `experiments/PackageConsumerProbe/obj/tags-20261019084418-36156-7ffc9c41`。这些本机产物不随 Git 分发。

上游主工作树 `main` 已快进至 `70b406c`，与 DG pin 一致；DG 实现提交为 `c9ad935`，仍在 `db096/branch-repository-cutover`，尚未正式合入。前片的11个普通包门禁见[原包迁移验收](package-cutover-validation.md)，仅证明其原 G/S；本片新 G/S 的资格严格限于上表与源码验收，未重跑完整 Runtime、Linux 或进程中止/掉电实验。

本片不代表组提交、根/子树或分页已交付。完整目标、正式合入门槛及下一步只查 [PROJECT-STATE](PROJECT-STATE.md)。
