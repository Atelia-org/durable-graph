# DB-078-B：独立 Checkpoint 与固定历史查询实施记录

> 状态：**已实施并独立验收**；源码基线 `14094ec`，前置 DB-078-A 已验收。
> 合同：[DB-078 §1.2](0078-editable-checkpoint-fork-slice.md#12-078-b独立-checkpoint-与固定历史查询)、[DB-083 §3–4](0083-repository-checkpoint-api-user-stories.md#3-api-草图与读取形状)。

## 问题与停点

在指定历史位置读取最多两张彼此独立的领域图，并按固定历史结尾查询事件地址。
最小成功标准是 PreviousX 的严格祖先/null 规则、可变隔离与稳定 getter、按需逆序及固定范围、失败和资源寿命边界，
以及真实 PackageReference 消费者。查询不依赖当前分支 head，不递归恢复祖先领域图。

本片保留每仓库至多一个活动工作副本；078-C 的每 branch 占用/Fork、tag 接入与 079–082 优化不在范围内。
不改持久格式、Storage pin、发布与严格打开协议；删除旧全量 `ReadEvents`，保留全链元数据 `ReadFrames`。

## 接缝与分工

| 要求 | 实现责任 | 验收证据 |
|---|---|---|
| 非泛型 Checkpoint / EventCheckpoint / StateCheckpoint；eager 至多两图 | core：Repository 与新公开类型 | 精确选点、最近严格祖先、null、数量、alias/cycle、稳定 getter |
| 共享一次读取会话，独立分配领域图 | core：复用 GraphReader / RevisionReadSession | 双图与其他读取/工作副本隔离、singleton、后段恢复失败、防重入 |
| EnumerateEvents 固定 end、两种方向、可选祖先下界 | core：Repository / HistoryOrder | 跨 State、物理交错、首项前拒绝外链、同点空、提前停止 |
| 每次推进检查可用性，交付项时释放 guard/lease | core；tests 独立编写 | foreach ReadEvent、间隔提交/Move、Dispose/fault |
| ReadEvent 只物化实际请求图；移除 ReadEvents | tests / consumers 分别迁移 | 模型回调计数、已有回归、真包编译与执行 |
| 公共用法、包内 XML、固定查询示例 | consumers：experiments 与根 README | 强制 Family 真包与恢复/README runner |
| 集成与独立审阅、证据与活动文档 | 主线程与只读 reviewer | 串行构建/回归/真包、实际 diff 与本地链接 |

`Checkpoint` 只由库构造，根属性交付后稳定；PreviousX 是领域根加地址，不是递归 Checkpoint。
一次 ReadCheckpoint 使用同一内部读取会话分别恢复当前图与至多一张 PreviousX 图，保留操作内 singleton 拒绝。
不使用具有显式只读共享合同的 ReadPair，也不增加跨操作实例登记或缓存。

`HistoryOrder.NewestFirst` 为默认值，`OldestFirst` 可缓冲所需范围的地址；两者都只遍历元数据。
可选下界在首项前验证为 end 的祖先或同点。无下界逆序按需回溯，提前停止不要求先读完整逻辑链。
Open 的全物理校验、下界验证与正序缓冲成本分别保留，不据此宣称常数时间或零 StateStore I/O。

## 验收记录

基线使用既有 Release 产物执行 Persistence 全项目，784 项通过；日志为 `.artifacts/db078b/baseline.log`。
整合根构建 `dotnet build DurableGraph.slnx -c Release --no-restore` 零警告/错误。
新增行为分别由 [Checkpoint 测试](../../tests/DurableGraph.Persistence.Tests/DB078CheckpointTests.cs)、
[查询测试](../../tests/DurableGraph.Persistence.Tests/DB078EventQueryTests.cs) 与
[自由历史测试](../../tests/DurableGraph.Persistence.Tests/DB078HistoryTests.cs) 的异型根/缺前驱模型/物理交错见证覆盖。

核心仅改 [Repository](../../src/DurableGraph.Persistence/Repository.cs)，新增
[Checkpoint](../../src/DurableGraph.Persistence/Checkpoint.cs) 与 [HistoryOrder](../../src/DurableGraph.Persistence/HistoryOrder.cs)。
既有 GraphReader/RevisionReadSession 已有独立恢复与跨图分配唯一性接缝，无需提前抽取 DB-079。
默认逆序无下界的按需性质由主线程与独立 reviewer 直接检查控制流；不使用易波动的耗时阈值测试。
枚举器外层检查每次 MoveNext，避免编译器生成的 iterator 在耗尽后跳过可用性检查。

独立审阅未发现代码阻断项。它补充了“当前 State 可读、PreviousEvent 缺模型”的失败见证，
避免空 registry 在当前图就失败而掩盖前驱能力错误。真实包 XML guard 所需的 HistoryOrder remarks 已补齐。
审阅另发现包内 PACKAGE.md 的既有 Family 注册示例仍使用 A 之前的 API，已随本轮包用法同步修正；
冻结旧包迁移输入保持原样，只改变 current lane。

完整源码回归使用默认公开 Storage `0.1.1-preview.2`，执行
`dotnet test DurableGraph.slnx -c Release --no-build --no-restore`：
Serialization 163、Storage 202、Persistence 819、Runtime/Generator 1625，共 **2809 项通过**，无失败/跳过。
本片新增 35 项；最后补充物理交错的 Checkpoint 断言后再次根构建并运行 DB078 筛选，**44 项通过**。
两次整合根构建均零警告/错误。日志和 TRX 集中在忽略目录 `.artifacts/db078b/`，其中
`integrated-tests.log` 保存完整运行，`final-db078.log` 保存最后补充断言的验证。

五个受影响的真实 PackageReference runner 全部通过：EventHistory、EventHistoryRecovery、RecordClass、
ReadmeQuickStart、DurableBaseMigration。它们使用各自独立缓存，从完整 feed 恢复；
DG 版本为 `0.0.0-db078b.20260927.1`，Storage 为独立的 `0.1.1-preview.2`，来源
`976aa345f923da09e2a5cf1dc25ba592b3818b63`，默认 pin 未变。
主线程另核对五个 Storage restore assets 版本、九个包身份及实际加载的九个运行时 DLL 与包条目的 SHA256 一致，
证据为 `.artifacts/db078b/package-evidence.json`。本轮包仅供本地验收，没有远程发布。

强制 Family 消费者实际运行 Event-first、两种 Checkpoint 的 PreviousX/null、图内 alias/cycle 与图间 mutable 隔离、
稳定 getter、跨 State 正逆序查询、foreach ReadEvent、推进/Move 后固定 end 及非祖先下界拒绝。
Recovery 验证包内新增类型/属性/方法 XML 文档并拒绝旧 ReadEvents；README 原文代码实际编译执行，
本地修改 PreviousState 后重新读取仍为持久值。RecordClass 保持旧到新查询、历史升版及冷重开。
DurableBaseMigration 继续使用真实旧包 `0.0.0-dramaboard.20260912.f68388f.1`，
验证旧数据读取、新 API 续写、Delta 与历史字节不变；没有为旧输入新增兼容壳。

源码、测试、消费者及包用法经主线程复核与独立审阅，没有未解决的阻断项。
差异检查与受影响 Markdown 的本地链接/锚点检查通过，既有标题和显式锚点保持。
后继为 078-C；整个 DB-078、一步 Fork、DG tag 接入及 079–082 优化尚未完成。
