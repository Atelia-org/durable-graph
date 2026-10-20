# DB-097 实施与验收

> 2026-10-20。源码、性能对照和普通包限定验收均完成，无剩余必需施工。

## 来源与实现

- 目标及基线见证提交：`5521847`；实现及消费者迁移：`7cd96f88527b7f38668d022e653f41a89ff41963`。
- 包候选源码：`bd44520`，在实现后仅修正 State 策略类型的 XML 注释，不改变运行时代码。
- Storage pin 保持 `0.1.3-dev.20261019.g70b406c` / `70b406cd8d1117733eee81a33e7f978399aa98b2`。
- 两轮独立目标审查和交叉质询见 [裁决](review.md)，最终合同见 [README](README.md)。

Event 每次使用独立 CaptureSession，在任何 State 内容基线访问前准备全 Base、空来源和空 Remove。候选生命周期、Catalog、图追加、原子发布及故障 owner 共用；State 的长期身份、next baseline、rewrite 和增量保存保留。

Pending 中重复的 StateSource 字段及业务 append 的重复 source 参数退出；准确历史准入仍由 RepositoryResources 独立核对。Event 完整读取拒绝非空来源、Delta、缺 owned batch、外部 head 和不完整成员对应。metadata-only 查询保浅层边界。

任一包含 Event 的 Pair 使用既有 ReadIndependent，保同操作的 mutable factory guard、先分配再 Hydrate，以及图内 alias/cycle。State–State 的共享闭包保持。Branches 根为 P0R4，旧根统一拒绝；其他布局与底座 pin 不变。

公开 `CommitEvent`、`CreateEventCommitRequest`、`CreateRootBranchFromEvent` 不再接受策略参数。没有兼容 overload、旧式 Event 模式、第二存储后端、scope 类型或业务事件 Apply 框架。

## 源码与回归

Windows / .NET SDK 10.0.201，显式 package mode，准确六包来自已验证本地 feed。使用任务配置 `.artifacts/maintenance-20261020/NuGet.Config` 对应的包 assets，未使用兄弟仓未固定的源码。

```powershell
dotnet build DurableGraph.slnx -c Release --no-restore --no-incremental -m:1 -nr:false -p:UseStorageSources=false
dotnet test DurableGraph.slnx -c Release --no-build --no-restore -m:1 -nr:false -p:UseStorageSources=false --logger trx --results-directory .artifacts/db097/final/test-results
```

强制根重建 **0 warnings / 0 errors**；最终四项目 **3,200/3,200**，失败及跳过均为 0：

| 项目 | 通过 |
|---|---:|
| Persistence | 1,222 |
| Runtime / Generator | 1,809 |
| Serialization | 133 |
| Storage | 36 |

根构建日志 `.artifacts/db097/integration/build-frozen.log`；最终运行 `.artifacts/db097/final-tests.log`，四份 TRX 及汇总/hash 在 `.artifacts/db097/final/`。最终运行前代码和测试已冻结；此前一次编译与测试编辑交错造成的旧 DLL 已由强制重建消除。最后 `bd44520` 仅为 XML 注释修正，普通包从它重新编译。

新增专属回归：

- [捕获](../../../tests/DurableGraph.Persistence.Tests/IndependentEventCaptureTests.cs)：3 项，零 State 内容资格/Delta、后继 State 增量、State ID 游标、失败重入与重试。
- [存储与读取](../../../tests/DurableGraph.Persistence.Tests/IndependentEventStorageTests.cs)：26 项，真实 raw 帧负例、冷暖重复读取、metadata 浅查、历史来源准入、四类 Pair 的局部 ID/字符串/循环/alias、singleton 在 Hydrate 前拒绝、四种旧库打开拒绝。
- [成本见证](../../../tests/DurableGraph.Persistence.Tests/IndependentEventCostTests.cs)：7 场景，候选与基线使用完全相同源码；另有公共 Event 参数与旧 P0R3 根的既有套件增补。

现有测试迁移了被替代的 Event source/head/Delta/Remove/共享游标断言，保留值、State rewrite/H、State 内容身份、后续 Delta prior、真实错误来源及 fault/publication 检查。独立复核确认 Snapshot helper 只按角色选择读取期望来源，没有改写负例磁盘输入；原始错误来源、State 隔离和失败边界未被放宽。

## 收益与代价

同一夹具的两轮原始记录、计量窗口、完整数据与限度只在 [性能结果](performance-results.md) 维护。

固定单对象 Event 的准备分配在 N=0/1,000/10,000 都为 **3,520 bytes**；旧实现 N=10,000 为 **9,072,472 bytes**。小 Event 不再为 State 大小支付内容基线、辅助表及目录来源工作。

同时，N=M=10,000、只改一个值的整图 Event，单次库逻辑文件增长从 **284** 到 **211,600 bytes**。该写入放大是完整独立内容的真实代价；没有用准备分配下降掩盖它。

该夹具包括真实准备、追加、发布与 flush，但不是公开 WorkingCopy 全入口 benchmark。单样本时延不构成稳定倍数，逻辑文件长度不等于物理 I/O；打开 WorkingCopy 的 State 恢复仍然存在。未新增设备掉电、跨平台或长时间吞吐资格。

## 普通包与文档

新 G `0.0.0-public-history-e2e.20261020114023.22264` 来自干净的 `bd445204e7ecbe2b844d84aea7ae84a544f39eba`；Storage S 保持上述 pin。SplitRepository、EventHistoryRecovery、GroupCommit、Tag 四套普通 PackageReference 消费者通过，Publish/Verify 零警告零错误。限定场景、原始回执及独立字节核验见 [普通包验收](package-validation.md)。候选只在本地生成，没有公开发布。

主线程另行重读 Split 原始回执，核对独立来源、十个 nupkg SHA/nuspec commit、两个消费者阶段的 20 个实际输出 DLL 与包内字节，以及 Atelia assets 全部为 package。证据 `.artifacts/db097/final/package-main-review.json`；其余消费者的实际缓存与程序集由包验收工作包独立核验。包消费通过不代替源码中的确定性 fault/坏帧测试。

修改的 Markdown 本地目标已检查，没有新增失效链接；历史 DB-062/064 冻结正文的 10 处旧源码链接本片未改，新增后继导航有效。检查工件 `.artifacts/db097/link-check.json` 和 `closing-link-check.json`。PowerShell runner 的窄改经 AST 解析，`git diff --check` 通过；当前能力、后继提示与路线图已统一收口。
