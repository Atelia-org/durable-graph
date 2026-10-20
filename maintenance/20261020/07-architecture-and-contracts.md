# DurableGraph 质量体检：跨层架构与交付合同

日期：2026-10-20。审阅基线：`1beca7cf2477d369010b3577b7a795c41dbc45f1`。读者：后续 Coding Agent。

## 范围与结论

本包检查项目引用方向、Repository/WorkingCopy/Resources 入口职责、CLI 现状、活动导航与外发说明、公共 API 编译见证和 README 原文验收的对应关系。先读了根 AGENTS、完整 `src/PROJECT-STATE.md` 和 DB-096 工作集，未把历史设计稿当成当前实现。

发现一个直接影响外部接入的文档错误，及两个活动导航漂移；其中两处已做文档小修。没有从本次有限审阅中取得需要拆程序集、引入 Repository 接口或通用事务框架的证据。没有证明运行时数据损坏或 SOLID 层面的必改缺陷。

本包不替代其他工作包对发布内核、内容校验、Generator、容器、Schema 历史和包闭包的深入审计。不将源码读过等同于运行通过；未执行构建、测试或 pack，统一由主线程协调。

优先级：P1 表示应阻止继续交付的正确性缺陷，P2 表示具体消费者会踩到的接入/维护问题，P3 表示低风险导航债。本包未确认 P1。

## A07-01 / P2 / 已确认并修复：NuGet 内嵌 README 仍指导调用已删除 API

### 事实与影响故事

基线的 [PACKAGE.md](../../src/DurableGraph/PACKAGE.md) 第29–34行仍写：`CreateSharedBranch`、`CreateSharedBranchFromEvent`、`CheckoutShared` 和 `ForkShared` 获取编辑副本，并称目录格式为 P0R2。它还使用 named branches 的说法，容易延续按标签寻址的旧认识。

这不是未使用的旧草稿：[DurableGraph.csproj](../../src/DurableGraph/DurableGraph.csproj) 第7、32–34行将它作为包内 `README.md`。从包管理器阅读说明的消费者会直接得到无法编译的入口；新 API 的创建/打开两阶段职责也会被混淆。

当前事实：

- [Repository.CreateRootBranchFromState / FromEvent / OpenWorkingCopy](../../src/DurableGraph.Persistence/Repository.cs) 第79、89、99行：创建返回 `BranchInfo`，编辑能力另由 `OpenWorkingCopy(BranchId)` 取得。
- [Repository.ForkBranch](../../src/DurableGraph.Persistence/Repository.cs) 第385行：登记 fork 后返回 `BranchInfo`；本身不恢复 WorkingCopy。
- [ManagementDirectoryPointer.Magic](../../src/DurableGraph.Persistence/ManagementDirectoryPointer.cs) 第38–40行：Branches 为 P0R3，Bookmarks 为 P0T2。[GraphFrameCodec](../../src/DurableGraph.Persistence/GraphFrameCodec.cs) 第17行：业务图为 P0G2。
- [普通包 API probe](../../experiments/PackageConsumerProbe/Run-RepositoryApiProbe.ps1) 第164行明确要求旧 `CreateSharedBranch` 得到 CS1061；源码 [RepositoryPublicApiTests](../../tests/DurableGraph.Tests/RepositoryPublicApiTests.cs) 第52–53行也拒绝 CheckoutShared/ForkShared。

### 已做的最小修复

只修改 `src/DurableGraph/PACKAGE.md`：替换为三个返回 `BranchInfo` 的真实创建/fork 方法、独立 `OpenWorkingCopy(BranchId)`、可重复 display label，并将格式说明改为 P0R3 分支目录指针及 P0G2 业务图。无公开签名、包版本或持久格式变更；未来构建出的包会携带修正后的说明，已有包字节不会追溯改变。

### 原因与后续建议

这是有证据的重复维护风险：根 README、包 README 和固定编译片段独立维护。现有 [README probe](../../experiments/PackageConsumerProbe/Run-ReadmeQuickStartProbe.ps1) 第110–114行比较的是根 README 与候选导出 README；[候选导出脚本](../../eng/Prepare-DurableGraphCandidate.ps1) 第61、206行从根示例生成候选说明。它们没有把 PACKAGE.md 这段入口说明作为编译输入。因此“旧 API 已被负例拒绝”和“外发说明仍推荐旧 API”可以同时成立。

后续最小方案：API 切换时把 `PACKAGE.md` 纳入具体变更清单；若再次漂移，优先收缩包 README 中重复的签名/格式细节，链接到现行根接入指南。不要为这次文案修正新建全仓 Markdown 编译框架，也不要补镜像字符串单测。

### 可执行验收

1. 检查 `git diff -- src/DurableGraph/PACKAGE.md`，确认旧四个方法及 P0R2 已退出当前指导。
2. 普通下一次 package-mode pack 时，用 ZIP reader 打开 `Atelia.DurableGraph.*.nupkg`，读取 `README.md`，确认与修正后的 PACKAGE.md 一致；不需为此发布包。
3. 若借此继续更改 API，再运行既有 `Run-RepositoryApiProbe.ps1` 和 `Run-ReadmeQuickStartProbe.ps1` 的当前 package-mode 流程。本次没有运行这些 probe，也没有改变其已有资格。

## A07-02 / P3 / 已确认并修复：当前源码导航仍停在隔离分支阶段

[Persistence README](../../src/DurableGraph.Persistence/README.md) 基线第7行仍以“B2 隔离分支”“剩余合入/包边界”引导读者；同一文档第36行又指向当期工作集。DB-096 [工作集](../../docs/design-branches/0096-history-and-head-model/PROJECT-STATE.md) 第7–11行和 [最终交付验收](../../docs/design-branches/0096-history-and-head-model/final-delivery-acceptance.md) 已明确完整验收并合入本地 master。Coding Agent 从源码 README 恢复任务时可能误以为还需执行合入或重新迁移消费者。

已将第7行改为当前 P0G2/D/T 产品布局和最终验收入口。保留历史施工记录本身的历史意义，没有回写历史 DB，也没有增加新的状态权威。

验收：审查此单行 diff；新链接 `../../docs/design-branches/0096-history-and-head-model/final-delivery-acceptance.md` 已核存在。无新 anchor，未改代码和测试。

## A07-03 / P3 / 已确认、交主线程处理：产品工作集中的旧类型名和依赖图

本包未修改主线程独占的 [src/PROJECT-STATE.md](../../src/PROJECT-STATE.md)。以下按基线行号定位，若主线程本轮已修正，视为已关闭，不重新立工单。

- 第48行把当前公开历史角色写成 `CheckpointKind`。实际公开枚举为 [CommitKind](../../src/DurableGraph.Persistence/CommitKind.cs) 第4行；DB-096 不保旧 API 兼容壳。这会干扰 Agent 对当前声明位置和改名完成范围的判断。
- 第372行写“Persistence → Runtime + Storage，二者分别复用 Serialization；Storage 另用 RBF substrate”。实际 [Persistence.csproj](../../src/DurableGraph.Persistence/DurableGraph.Persistence.csproj) 第13–15行直接引用 Runtime、Storage 和构建期 Generator；[Runtime.csproj](../../src/DurableGraph/DurableGraph.csproj) 第15–22行引用 Serialization、构建期 Generator/Build；[Storage.csproj](../../src/DurableGraph.Storage/DurableGraph.Storage.csproj) 第17–29行只引用上游 Binary/Data/Primitives/Rbf/FrameStore，没有 DG.Serialization 引用。应按实际直接边改写，不能据旧文给 Storage 补回不需要的依赖。

最小修复：主线程在工作集把当前枚举写成 CommitKind，压缩依赖句为“Persistence 依赖 Runtime 与 Storage；Runtime 复用 Serialization；Storage 使用独立存储底座”，构建期依赖由 csproj 链接承载。无需新架构文档或依赖守卫测试。

验收：查上述声明与 `ProjectReference`/`PackageReference`；执行 `rg -n 'CheckpointKind|产品依赖' src/PROJECT-STATE.md`，确认当前说明不再用旧类型，历史 DB 不做批量替换。

## 保留判断：当前架构中哪些形状不应仅凭观感调整

### facade、保存内核与薄角色 ID 的现有分工可解释

[WorkingCopy](../../src/DurableGraph.Persistence/WorkingCopy.cs) 第23、93行后的提交入口把分支编辑能力和对象基线绑定到实例；[Repository.GroupCommit](../../src/DurableGraph.Persistence/Repository.GroupCommit.cs) 第25行后统一请求校验、捕获、共同 Catalog 候选、发布和安装；[RepositoryStorageFacade](../../src/DurableGraph.Persistence/RepositoryStorageFacade.cs) 第8行说明 borrowed facade，第40–55行转发到 Resources 保存生命周期。这些转发不是存在两份发布权威的证据。

`Repository` 同时承载业务入口和历史导航，并不自动违反单一职责。要提取新层，至少需要具体反例，例如新增查询必须越过写入能力边界，或双路径同一错误产生不同 outcome；本包没有证明这类反例。不建议按 partial 数量或类长度拆接口/程序集。

[BranchId](../../src/DurableGraph.Persistence/BranchId.cs) 第7–10行和 [CommitId](../../src/DurableGraph.Persistence/CommitId.cs) 第7–10行使用上游紧凑值但不公开底层构造器。存储位置不是全局身份已写进 XML 文档；跨库强 scope、merge、删除等延期项不自动算未实现技术债。

### CLI 是空壳，当前不构成分层泄漏

[CLI Program](../../src/DurableGraph.Cli/Program.cs) 只有两行，明确 intentionally left empty，并返回0；[CLI.csproj](../../src/DurableGraph.Cli/DurableGraph.Cli.csproj) 第11行只引用 Runtime。没有发现 CLI 偷藏持久化权威，根接入故事也不依赖它。当前 README/包 probes 已提供真实入口，不为“完整性”把 probes 搬进 CLI。

若未来真的交付 inspect/rescue 命令，再单独给 CLI 指定输入/退出码/失败合同和用户故事；本次不把命令开发或自动启动审计列为整改项。

## 本包验证与修改清单

已做：逐项核对入口声明、格式 magic 和项目引用；审查两个文档 diff；对两个修改文档全部本地 Markdown 链接执行存在性检查，无缺失；`git diff --check -- src/DurableGraph/PACKAGE.md src/DurableGraph.Persistence/README.md` 通过。现有 links 的 anchor 未做全量标题解析；本次新链接不带 anchor。

未做：`dotnet build`、`dotnet test`、新包导出与 nupkg 回读、跨平台运行或性能测量。文中测试名称和脚本是源码证据与后续验收入口，不是本轮已执行结果。根构建/回归结果以主线程报告为准。

本包全部改动：

- `src/DurableGraph/PACKAGE.md`：修正当前外发 API/目录格式说明。
- `src/DurableGraph.Persistence/README.md`：替换过时隔离分支/合入导航。
- `maintenance/20261020/07-architecture-and-contracts.md`：本报告。

未改 src/PROJECT-STATE、其他报告或共享索引；未提交、推送或发布包。
