# DB-096：真实多分支提交实施与验收

2026-10-19。以 `285a910` 为起点，在新 Branch 布局接通已接受的 CommitRequest 合同。问题是：两条已有 Branch 各引入新 Schema 时，能否统一准备并一次发布，正确安装 State/Event 基线，在失败后保留整组 outcome 和 owner 状态？范围包括即时单提交共核、原回归与消费者迁移；不引入批量创建、持久 GroupId、跨库事务或新 wire 格式。

## 实现

- [CommitRequest](../../../src/DurableGraph.Persistence/CommitRequest.cs)绑定原 WorkingCopy、Branch 和准确 Base，保根引用与自有 summary；不捕获、不设 Consumed。`CreateStateCommitRequest` 支持当前 State 或显式根，`CreateEventCommitRequest` 保存独立 Event。旧 Base 或已释放实例的请求拒绝；健康失败的同一请求可重新捕获当前图。
- [Repository 协调](../../../src/DurableGraph.Persistence/Repository.GroupCommit.cs)先设 busy，并在资源 prepare guard 内枚举全部输入和内部声明，再完成全员准入。每个枚举步骤、Capture、body 后复核 owner/backing/GraphStore；所有 Capture 立即由固定槽接管。先汇总完整布局，再统一分配 Schema 描述符，逐图编码。
- [资源内核](../../../src/DurableGraph.Persistence/RepositoryResources.GroupSave.cs)拥有唯一 accepted Catalog、一个多成员 pending 和一次发布生命周期。普通组仅发布 B₁…Bₙ＋必要 S，D/T 不变。新根继续先创建真实 Head、再 D(/S)登记；即时 State/Event 走同一内核的单成员路径。
- 发布前准备安装材料；确认后核对完整返回目标集合和 ID 归属，再接受 Catalog、安装 State，最后推进全部 WorkingCopy Base 并交付只读结果表。Event 不安装 State、不消费其 rewrite，也不刷新工作副本的冻结执行环境。
- 全部捕获的准备失败清理由协调器统一执行 `AbortPreparation`，取消 adapter 的重复清理权威。输入枚举主异常和 Dispose/资源检查异常均保留；已发布后的安装、交付、清理错误停用整个 owner。
- `RepositoryPublicationException.Members` 提供各成员的目标、原 Base、已知图地址和实际获得的 CommitId。单成员便利字段保留；多成员时为 null。未获得的 CommitId 不推算，结果未知不透明重试。

WC/Session 的可变 Catalog 已移除。旧内部测试声明迁为同次操作输入；独立 SchemaCatalogWorkspace 原语测试仍可测试其自身能力。真实准备后丢弃、准确历史 Schema、冲突和冻结环境反例保留，不能用“仅创建请求”替代候选准备证据。

## 验收状态

主线程完成根 `dotnet build DurableGraph.slnx -p:UseStorageSources=true -p:StorageSourceRoot=<准确70b406c上游路径>`，最终0警告/0错误；随后使用该构建的 `--no-build --no-restore` 结果：

| 验收 | 结果与实际范围 |
|---|---|
| 完整 Persistence | **1049/1049通过，0跳过**，包含下列55项新增组反例 |
| 新增公开组调用 | 18项：共同Schema/准确冷热Delta、延迟捕获/自有summary、过期/重复/foreign/disposed准入、操作内Schema冲突、策略预检、冻结模型环境 |
| 新增发布与生命周期 | 32项：19个有无S增长的真实CU与DG窗口，第二Capture/body失败重捕，输入枚举各步骤/Dispose及模型回调重入/资源失效 |
| 新增结果与清理 | 5项：确认后返回map缺项/错Head/额外键；成功发布后及BeforeAppend失败后的cleanup错误、一次清理、主异常与outcome保留 |
| 完整 Runtime | **1724/1724通过，0跳过**，包含生成模型、升级与原内部调用迁移 |

首轮50项组用例通过后，完整 Persistence 仍发现2个既有递归 Dispose 回归：拒绝 active pending 的 Dispose 时，finally 错误释放外层 Capture。已改为 Pending.Dispose 成功后才清理；原反例未弱化，最终全套通过。初次编译的缺少 Schema using、后续一处 nullable 警告也已消除。独立审阅核过最终源码及TRX，但不代替主线程验收。

证据目录 `.artifacts/db096-group-commit/`，初轮 `build-1.log`、`build-2.log`、`group-1.log`、`persistence-1.log` 保失败/修复前范围；最终资格为 `build-3.log`、`persistence-2.log`、`runtime.log` 及 `tests/persistence-2.trx`、`tests/runtime.trx`；不得从初轮绿色子集外推。新增测试为 [公开组调用](../../../tests/DurableGraph.Persistence.Tests/BranchGroupCommitTests.cs)、[真实发布及重入](../../../tests/DurableGraph.Persistence.Tests/BranchGroupFailureTests.cs)、[确认结果与清理](../../../tests/DurableGraph.Persistence.Tests/BranchGroupIntegrityTests.cs)。最后一组含内部返回 map 变换 seam，不是观察到 HeadStore 正常返回坏数据。

## 普通包交付

产品提交 `9aa72ffbefe3d10933a4dcc144ffa7485a1e17f4`，新 G 为 `0.0.0-public-history-e2e.20261019091754.31088`；S 保持 `0.1.3-dev.20261019.g70b406c` / `70b406cd8d1117733eee81a33e7f978399aa98b2`。独立 package-mode restore/Release 根 Rebuild 为0警告/0错误；Split 控制打包的 provenance 证明生产输入匹配 HEAD、打包时工作树干净。未公开发布包。

| 门禁 | 本片结果 |
|---|---|
| Split 公开消费者 | 五个独立进程、升级/准确历史/改写/冷续写及编译保护门禁通过，receipt Passed=true |
| GroupCommit 公开消费者 | 三个独立进程通过；双新 State Schema、延迟捕获、请求自有 summary 与冷读、State/Event 混组、准确过期诊断、冷开 Event 保留原保存 State；history Publish/Verify与最终构建0警告/0错误 |
| Meta 内部友元消费者 | 迁移 session Catalog/构造参数后，四个独立进程 seed/read/continue/verify 通过；真实容器Delta与共同Catalog复用保持；不冒充非友元公开API证明 |

Group 首跑成功但有两处 nullable 警告，审阅又发现过期检查过宽、相对 feed 解释依赖 cwd。仅修消费者及 runner，保同一 G：改精确诊断、增加历史 summary 读回、收窄输出标记，并在目录切换前解析显式 feed。最终从仓库外以相对 `split/feed` 再跑通过；没有为消费者尾修重打产品包。已核 Group assets 的4G＋6S全为package，十个实际输出DLL的SHA256均与所选nupkg一致。

本机 `.artifacts/db096-group-commit/package-paths.json` 指向 `W:/TEMP/db096-group-de87f10a158e4b03a9dcbe658f6300b1/` 下 `split/result.json`、`split/feed/dg-source-provenance.*.json` 和 `meta/result.json`。Group最终工件为 `experiments/PackageConsumerProbe/obj/group-20261019092025-35944-b8e5b7ae`；来源与DLL校验摘要为本片 `group-package-verification.json`。根日志为 `package-{restore,build,split,meta}.log`、`package-group-2.log`，首轮 `package-group.log` 不作为最终零警告资格。工件不随Git分发。

本片新G仅资格化上述三个门禁，前片其他门禁仍保原 G/S 意义；最终整体交付再对最终版本验收完整活动集合。

## 后续边界

- 本片源码和上述普通包验收已完成；后继[Crash迁移](process-crash-migration.md)已恢复原6＋3窗口。组 source 测试自身不授进程 Kill、Linux、OS crash/power-loss 或总体性能保证。
- Crash host 已迁移真实 B/S 与 D/T不变、Commit历史来源及管理pointer；一个业务B加S的进程矩阵不代表多业务组的全部Kill组合。具体运行范围只查后继验收。
- 最终交付仍需目录树、历史分页、登记故障剩余窗口、文档/消费者清理及正式合入。状态与顺序只维护在 [PROJECT-STATE](PROJECT-STATE.md)。
