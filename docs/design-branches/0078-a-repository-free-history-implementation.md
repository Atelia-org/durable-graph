# DB-078-A：非泛型入口与自由历史实施记录

> 状态：**已实施并独立验收**。前置 DB-077 已验收；本片以 `1ef1d29` 为源码基线。
> 合同：[DB-078 §1.1](0078-editable-checkpoint-fork-slice.md#11-078-a非泛型公共基础与自由历史)、[DB-083](0083-repository-checkpoint-api-user-stories.md)。

## 问题与停点

让通用分支工具不指定领域泛型，即可从 Event 或 State 开始历史、连续提交任一角色，并按实际模型替换 State 根。
成功标准同时包括热提交、严格冷重开、身份保持、故障结果和真实包消费；不以编译通过代替持久语义验收。
本片仍每仓库至多一个活动工作副本。078-B 的便利 Checkpoint/按需查询、078-C 的多分支工作副本/Fork、tag 和 079–082 优化均不在本片。

## 接缝与分工

| 要求 | 实现责任 | 验收证据 |
|---|---|---|
| 根命名空间 Repository、BranchCheckout，统一历史地址和非泛型读取/ref API | repository 子任务 | API 消费、来源/角色/寿命测试 |
| 无 State 与跨类型替根，完整保存基线、live ID 和 Upgrade rewrite | workspace 子任务 | 首 State、child 升根、旧根降为 child/Remove、后继保存 |
| 自由历史 Journal Parent=P，图 Parent=最近 State 或 null | repository 子任务 | 连续 E/S、物理交错、全物理/orphan 冷开验证 |
| 原有回归迁移 | tests-migration 子任务 | 原有持久化和生成器回归；删除旧 Pending/交替假设 |
| 新行为及失败边界 | semantic-tests 子任务 | 新 DB078 测试，三种 publication outcome、fault/reentry/Dispose |
| 真包、README、应用恢复进度 | consumers 子任务 | PackageReference 编译执行与冷重开 |
| 集成、独立审阅、证据与文档 | 主线程 | 串行构建/测试/包验证、独立审阅、diff/link 检查 |

`CheckpointAddress` 选不可变 sealed class：无公共构造入口，null 无效；相等性由打开 owner 与 Journal 位置共同决定。
保留 Kind、RevisionAddress、RootId 诊断元数据；裸图修订不成为外部书签或来源认证。
低层 typed LoadedWorld 测试接缝继续存在；公共分支与读取不保留泛型兼容壳。

保存候选在发布前完成捕获、模型选择和安装准备，发布后不重新调用用户模型。
Event 永不接受为 State 基线；Event-only 冷 Checkout 使用全新空捕获会话，不物化 Event。
任何损坏或恢复能力缺失都不能降级为 State=null。追加、CAS、fault 与严格无修尾打开继续遵循原协议。

## 验收记录

核心落点为 [Repository](../../src/DurableGraph.Persistence/Repository.cs)、[BranchCheckout](../../src/DurableGraph.Persistence/BranchCheckout.cs)、
[CheckpointAddress](../../src/DurableGraph.Persistence/CheckpointAddress.cs)、[WorldWorkspace](../../src/DurableGraph.Persistence/WorldWorkspace.cs) 与
[PreparedWorldSave](../../src/DurableGraph.Persistence/PreparedWorldSave.cs)。旧公开类型及泛型分支/读取重载已移除，程序集及持久 envelope 格式不变。
内部 `LoadedWorld<T>` 继续验证 exact 请求类型，作为低层机制见证；它不是公共兼容层。

[DB078HistoryTests](../../tests/DurableGraph.Persistence.Tests/DB078HistoryTests.cs) 覆盖两种首建、连续 E/S、
冷热保存 Parent、空/部分模型目录、跨实际类型与 child 升根、旧根降为 child 及不可达删除、地址相等/来源、物理交错分支。
[DB078FailureTests](../../tests/DurableGraph.Persistence.Tests/DB078FailureTests.cs) 覆盖首 Event 与首 State 的
NotPublished/Unknown/Published、未安装候选、重入/Dispose/只读、坏图 Parent/缺失祖先/orphan 及失败打开后文件不变。
它们共 31 项。既有共享、Schema Upgrade rewrite、精确来源、保存策略与发布故障回归继续保留。

测试见证作了两类必要校准：文件字节比较只在资源关闭后进行；成员删除验证有效 head map，允许既有规划器选择更小的目录 Base。
旧工作区测试中“无 State 禁止 snapshot”的断言改为完整 Base、候选互斥与不安装基线；不能保留与 Event-first 冲突的旧前置条件。

独立源码/测试/消费者审阅未发现未解决的阻断项。审阅发现 ref-only CreateBranch 的包内 XML remarks 缺失，已补齐；
包内文档随真实消费者一起验收。

源码验收使用默认公开 Storage pin `0.1.1-preview.2`：`dotnet build DurableGraph.slnx -c Release --no-restore`
最终零警告/错误；Serialization 163、Storage 202、Persistence 784、Runtime/Generator 1625，共 **2774 项通过**，无跳过。
完整运行中识别出的旧 snapshot 前置断言修正后，重新构建并全跑 Persistence 784 项；其他三项目此前已全部通过，未重复无关测试。
基线为 2743 项，新增加 31 项。日志与 TRX 位于忽略目录 `.artifacts/db078a/`。

新上游包另以任务专属 `.artifacts/db078a/NuGet.Config` 和 CLI override 验证：
Storage `0.1.2-dev.20260927.1`、来源 `deb55672106c8a8966e4b04a75bedf0b1523be7f`；五个 nupkg SHA256 与上游 manifest 全部一致。
该 lane 根构建零警告/错误，Persistence **784 项通过**；没有更改默认公开 pin、上游源码或用户 NuGet 配置。

DG 真包版本 `0.0.0-db078a.20260927.1`，与上述 Storage 版本独立；四个 DG 包加五个上游包组成完整 feed。
同一完整 feed 的 **24 个真实包 runner 全部通过**：EventHistory、EventHistoryRecovery、ReadmeQuickStart、StateStore、Generic、RecordClass、Array、BclScalar、CompositeDictionary、CrossAssembly、Dictionary、Enum、HistoryCapability、ImmutableLeaf、InheritanceLibrary、InlineLibrary、InlineStruct、List、Nullable、Record、TemporalScalar、ValueUpgrade，以及两条真实旧包迁移。
各 runner 使用独立恢复缓存；主线程另核对了 EventHistory 消费者的 assets 中五个 Storage 版本，以及实际九个运行时 DLL 与 nupkg 条目哈希一致。
强制 Family 的 EventHistory 消费者实跑无领域泛型工具、Event-only 冷空模型签出、首 State、跨类型 child 升根保 ID、后继无参保存及同 CLR 类型的两种角色。
Recovery 使用明确的应用交替协议和显式 ReadEvent；包内 XML、README 原文保存/重开也实际通过。

旧包输入保持冻结：OrganizationMigration 使用 DB-071 的 `0.0.0-event-history-e2e.20260912091137.71028`，
DurableBaseMigration 使用 `0.0.0-dramaboard.20260912.f68388f.1`；分别验证旧数据冷读/续写、历史内容不变及真实 Delta。
没有给旧包套用新 API 源码，也没有为本轮新增产品兼容壳。

验收结束已恢复默认公开 pin 的 root restore assets，根构建零警告/错误，Storage 202 + Persistence 784 再次通过。
ListDeltaReplay 功能 smoke 通过 20 个独立仓库、300 个修订；本轮不据此声明性能收益。
`git diff --check` 通过，既有 Markdown 标题和显式锚点保持。重命名源码的本地链接已修复；
受影响文档的其余 11 个缺失链接在基线中已存在，均为历史兄弟仓路径，没有新增断链。

本片完成后的下一停点是 DB-078-B；整个 DB-078、Fork 与 DG tag 接入尚未完成。
