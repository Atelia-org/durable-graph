# DB-078-C：每分支工作副本与 named Fork 实施记录

> 状态：**已实施并独立验收**；2026-09-28。源码基线 `13c11b3`，前置 DB-078-A/B 已验收。
> 合同：[DB-078 §2–6](0078-editable-checkpoint-fork-slice.md#2-078-cfork-api-与恢复行为)、[DB-083](0083-repository-checkpoint-api-user-stories.md)。

## 问题与停点

交付每个分支至多一个活动工作副本，以及 `Fork(string branchName, CheckpointAddress source)`。
最小成功标准：源工作副本存活时，从同一已提交位置扇出独立的可编辑图，串行续写并冷重开；
占用、恢复与发布失败不误释放其他工作副本，也不交付半成品。

不同分支可同时编辑，仓库操作仍串行。Fork 保持精确历史 Head，只恢复最近 State；
纯 Event 前缀交付空 State，不物化 Event。全部恢复与交付准备在发布新 ref 之前完成，Fork 本身不追加历史图。
不改变格式、Storage pin、严格打开或 append-only，不实施 tag、DB-079 恢复核心抽取或 080–082 缓存优化。

## 接缝与分工

| 要求 | 实现责任 | 验收证据 |
|---|---|---|
| ordinal 分支名占用；取得/交付/失败清理；幂等 Dispose | core：Repository / BranchCheckout | 重复签出、恢复失败重试、旧 Dispose、busy/fault/repo Dispose |
| Commit 所有权、存活、exact head/baseline 与最终 CAS | core；tests 独立编写 | 多分支交错、前置无写入、追加后 CAS 失败 |
| Checkout/Fork 最小共用恢复接缝；先准备再发布 | core | S/E/E-only、模型缺失、恢复失败、无新历史帧、精确 Head |
| 整个 Fork guard；ref mutation 核保留 outcome/fault | core；tests | Allocate/Hydrate/注入回调重入、三种 outcome、其他副本受 fault 约束 |
| 图隔离、完整保存来源及异型替根 | tests | alias/cycle/ID、child-only Delta、循环岛 Remove、独立首 State/替根与冷开 |
| 真实公开用法与包边界 | consumers：README / PACKAGE / PackageConsumerProbe | 强制 Family 的扇出、E/E/S/S、纯 E、异型替根和 README 原文执行 |
| 集成、独立审查、证据与活动文档 | 主线程与只读 reviewer | 根构建、相关/完整回归、真包、实际 diff 与本地链接 |

活动名集合只表达使用规则，不持有工作副本对象，不引入 token、epoch 或 GC 自动释放。
只有本次成功取得且未交付的占位由 finally 清理；已发布后失败不删除 ref，依 outcome/fault 重开检查。
Move 只拒绝仍被占用的目标分支；ref-only CreateBranch 不恢复图或占用编辑名。

## 验收记录

基线在原 Release 产物执行 Persistence 全项目，**819 项通过**。最终根构建
`dotnet build DurableGraph.slnx -c Release --no-restore` 零警告、零错误。
完整源码回归 `dotnet test DurableGraph.slnx -c Release --no-build --no-restore`：Serialization 163、Storage 202、
Persistence 855、Runtime/Generator 1625，共 **2845 项通过**，无失败/跳过。本片净增 **36 项**：三个新测试文件 35 项，
既有升级恢复增加 Fork 路径 1 项。日志与 TRX 位于忽略目录 `.artifacts/db078c/`，最终结果见 `final-build.log`、`final-tests.log`。

核心仅修改 [Repository](../../src/DurableGraph.Persistence/Repository.cs) 与
[BranchCheckout](../../src/DurableGraph.Persistence/BranchCheckout.cs)：活动名集合、最小共用恢复接缝及 guarded ref mutation 核。
原 GraphReader、RevisionReadSession、WorldWorkspace 与 Publish/CAS 管线保持；没有提前引入 DB-079 的统一恢复抽取。
工作副本的 owner/存活检查替代原仓库单对象引用判断，分支集合不取代 exact head、最近 State 基线或最终 CAS。

新增行为由 [Fork 测试](../../tests/DurableGraph.Persistence.Tests/DB078ForkTests.cs)、
[占用测试](../../tests/DurableGraph.Persistence.Tests/DB078BranchOccupancyTests.cs)、
[模型边界测试](../../tests/DurableGraph.Persistence.Tests/DB078ForkModelTests.cs) 覆盖；
既有 [升级恢复](../../tests/DurableGraph.Persistence.Tests/EventHistoryRepositoryTests.cs) 同时执行 Checkout/Fork 路径，
[最终 CAS 注入](../../tests/DurableGraph.Persistence.Tests/EventHistoryPublicationFailureTests.cs) 验证仓库 fault 同时阻止兄弟工作副本提交。
模型边界包含只有 State 模型的 E2 Fork、空 registry 的纯 Event 双 Fork、缺 State 模型不降级 null、
两分支独立建立首 State 并跨类型替换；身份验证包含 child-only Delta、循环岛退出 membership 和冷热续写。

首次整合测试暴露测试夹具在 Windows 独占写句柄存活时另行读取文件的共享冲突，未改变产品打开纪律。
修正后 live 检查明确只核对文件集合、长度与修改时间，排除协调锁；原闭仓字节比较保留。
另加 State/Event/Event-only 三种成功 Fork 的冷前后 Schema、State、Journal 文件完整字节比较，
以及 Decode/Normalize/Allocate/Hydrate 四种恢复失败的闭仓完整文件比较，保证没有用排除 Schema 或跳过测试绕开问题。
主线程另发现 exact-current 会绕过 Normalize 委托，已将该失败见证改为实际 V1→V2，验证真实升级错误。

三个受影响的真实 PackageReference runner 全部通过：EventHistory、EventHistoryRecovery、ReadmeQuickStart。
DG 本地验收版本为 `0.0.0-db078c.20260928.1`；Storage 使用默认独立公开版本 `0.1.1-preview.2`，
来源 `976aa345f923da09e2a5cf1dc25ba592b3818b63`。默认 pin 未变，没有重打上游包或远程发布。
各 runner 使用完整九包 feed 和隔离缓存；主线程另核对五个 Storage assets 版本、九包身份及实际加载的九个运行时 DLL
与包内条目的 SHA256 相同，证据为 `.artifacts/db078c/package-evidence.json`。

强制 Family 消费者实际保持源工作副本存活，从同一 Event 扇出两个孩子、独立 E/E/S/S 并冷开验证；
另覆盖纯 Event 双 Fork、首 State、World→Alice 异型替根与无参提交。模型/history 数量保持原 9→11。
README 新 Fork 代码块由 runner 原文提取并运行，冷重开值为 source=96、left=95、right=94；
恢复 runner 验证实际包内 Fork 的 XML 文档。所有调用仍串行，应用自行解释消息处理进度。

主线程逐段检查核心、测试和消费者 diff，独立 reviewer 另行复核。
初审发现无追加快照漏选实际 `schemas.rbf`，已补入并断言存在；最终夹具修复以运行证据为准。
独立 reviewer 已复核最终源码、测试、消费者及实际 TRX/包日志，无未解决阻断项。
16 份受影响 Markdown 的 842 个本地链接检查通过，既有标题和显式锚点保持，`git diff --check` 通过。
活动上下文、目标设计、路线图与设计索引同步；079–081 页首的旧泛型 API 状态也已校准，未修改其优化合同。
DB-078-A/B/C 至此全部完成；后继为 DB-079，tag 接入与 080–082 优化仍独立未实施。
