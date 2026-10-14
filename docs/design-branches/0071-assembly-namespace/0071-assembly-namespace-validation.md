# DB-071 实施验收记录

> 2026-09-12；**G0–G4 全部通过**。目标与范围见[工单](0071-assembly-namespace-implementation-work-order.md)。
> 初次授权完成 G0–G4 并停在本地待提交；下游试用通过后，用户另行批准 Git 提交，见文末收尾记录。兄弟工作树正式 pin 由下游处理，不推送远程。

## 起点与证据归属

- DurableGraph 源码基线：`c7ee495621808de72a040dbef1b611ae9cd5ce02`。开工 Git 状态、原有 diff 保存在 `obj/db071-baseline/`。
- 原有改动：路线图、设计索引、产品 PROJECT-STATE；未跟踪的 DB-071 审计、工单与 Goal 文本。全部保留，在本轮范围内收口。
- 实际 Atelia HEAD：`539088414686f51a210798b35ee56d02de8c845c`，工作树有既有修改，不能称作干净提交构建。来源 diff 与状态单独保存，新旧包使用同一输入。
- 实际 DramaBoard HEAD：`30cafe0ed075281bf424e19d71ae00d845cfeac9`，开工工作树干净。下游验收仅使用隔离副本。
- 本轮不改变成员语义、访问级别、依赖方向、算法、默认值或持久合同。类型映射以工单 §2 为准，编译清单与跨真实包运行共同验收。

## 门状态

| 门 | 状态 | 验收位置 |
|---|---|---|
| G0 | 已通过 | 下述旧 build/test、包来源、签名、history 与存量帧 |
| G1 | 已通过 | 新 build、签名对照与 2,668 项产品回归通过 |
| G2 | 已通过 | 新包资产、README 与八组指定真实包 probes |
| G3 | 已通过 | 两套 Runtime 包的旧写新读续写、隔离 DramaBoard |
| G4 | 已通过 | 独立审阅、最终文档、引用图、链接与 diff 复核 |

下文集中记录实际命令、结果及材料路径；完成结论以实际执行结果为依据。

## G0：真实旧组织基线

从根执行 `dotnet build DurableGraph.slnx -v:q`、`dotnet test DurableGraph.slnx --no-build -v:q`：
build 0 warning/error；测试 2,668/2,668（163 Serialization、202 Storage、732 StateStore、1,571 核心）。
日志：`obj/db071-baseline/build.log`、`tests.log`。

原组织 `Run-EventHistoryProbe.ps1` 自包含运行通过，九包版本
`0.0.0-event-history-e2e.20260912091137.71028`，feed 位于
`experiments/PackageConsumerProbe/obj/event-history-20260912091137-71028-f23f502f/feed`。
原生成器运行两代模型，通过 Publish/Verify、事件恢复和 accepted history 保持检查。
包列表/SHA-256/资产与 nuspec 分别保存在 `obj/db071-baseline/old-package-hashes.json`、
`old-package-assets.json`；完整日志 `old-pack-eventhistory.log`。

[编译清单工具](../../../experiments/PackageConsumerProbe/OrganizationApiInventory/README.md) 对显式旧 DLL 闭包捕获
349 个含编译器类型的类型、4,112 条成员/签名记录；核心源类型确认为 146 = 21 根 + 13 Schema + 112 Runtime。
`obj/db071-baseline/api/old/` 保留真实类型名、规范映射及 DLL 来源/hash，`assemblies/` 保留旧 Debug 闭包。

[跨包 runner](../../../experiments/PackageConsumerProbe/Run-OrganizationMigrationProbe.ps1) 的 `-Stage Seed`
使用上述真实旧包/旧生成器，`-WorkRoot obj/db071-organization`，成功建立完整 S 分支与 pending E 分支。
其 `legacy/` 有独立还原缓存、包及 generator provenance、两份 accepted history；
`legacy-frames.json` 保存 18 个完整 Schema/State/Journal 帧的文件、原始 offset/length/tag/hash。
RBF 扫描检查终止错误，未将残缺扫描当作完整清单。产品源码移动在 Seed 成功之后。

## G1：组织与生成接线

三个产品项目和对应测试项目同步改名，Serialization 源码展平；七项目引用图与主包内嵌资产接线保持。
核心 72 个源码文件分为根 20、Schema 10、Runtime 42；属性/登记接口与 ObjectStateKind 按工单提取。
生成器显式分开 RootName/SchemaName/RuntimeName，保留 Generated/Family/DTO 和真实 marker 程序集检查。

新 solution build 0 warning/error；新 Debug 闭包的 `api/new/` 与旧清单按唯一批准映射比较：
`removed=0, added=0, oldLines=4112, newLines=4112`，见 `api/compare.log`。
检查包括内部成员、访问级别、约束、常量、参数默认值、custom modifiers/attributes。

全套 `dotnet test DurableGraph.slnx --no-build -v:q` 2,668/2,668，无跳过，四程序集计数与旧基线一致。
日志 `obj/db071-baseline/g1-build.log`、`g1-tests.log`。独立审阅按旧→新映射复核实际产品/测试正文，
没有发现算法、控制流、默认值变化或断言削弱。

## G2：新包闭包与真实生成消费者

九包统一版本 `0.0.0-db071.20260912.1`，feed 为 `obj/db071-new/feed`。
`package-hashes.json`、`package-assets.json` 记录 SHA-256、zip 条目与依赖；`source-input-hashes.json`
记录打包源码输入。Atelia 工作树 diff 与 G0 完全相同。
四个 DG 包是主包、Serialization、Storage、Persistence，另有 Data/Primitives/Rbf/RbfSegmentStore/EventJournal。
主包仅普通依赖 Serialization，analyzer/build/tools 三组资产完整；Persistence 依赖主包、Storage 与 EventJournal，
包含 facade XML。新包条目和 nuspec 没有旧 StateStore 包或 DLL。

以下 runner 均成功，完整日志集中在 `obj/db071-new/<runner>.log`；各日志末行给出独立 artifact 目录：

| Runner | 观察结果 |
|---|---|
| Run-Probe | 单一主包引用、普通生成/历史发布与 Verify、空候选和负例通过 |
| Run-ReadmeQuickStartProbe | README 原文保存/重开/升级与策略覆盖通过 |
| Run-EventHistoryProbe | 两代模型、事件/状态读取、pending 恢复、共享、Base/Delta 与 readonly 通过 |
| Run-EventHistoryRecoveryProbe | 独立事件快照、pending-only 完成、不重复处理已发布 S、包内及还原 XML 通过 |
| Run-CrossAssemblyProbe | nominal/动态组合、历史精确读取、目标升版、NoChange/Delta、独立 history 通过 |
| Run-InlineLibraryProbe | 固定跨库 inline、ref/lib 接线、owner 升版、外部 history 不重发布通过 |
| Run-InheritanceLibraryProbe | 跨库继承、隐藏/readonly/泛型基类状态、历史转换与冷重开通过 |
| Run-RecordClassProbe | 三次真实包构建、同版 class→record history 不变、升级 Base/Delta 与另进程冷读通过 |

基础 runner 自建独立两包；其余共享上述九包 feed，没有手工 Analyzer/Import/AdditionalFiles/产品 ProjectReference 接线。
首次基础运行发现旧脚本仍要求空 manifest v7，而 G0 源码与实际生成器均已是 v9；只将脚本期望更新为 v9 后完整重跑通过。
未修改产品 history header、版本或编码。跨库临时模型包仍有既有 NuGet README/NU5131 提示，runner 的资产与运行断言通过；
产品 solution build 无 warning/error。

## G3：跨 Runtime 包与真实业务副本

组织见证使用 G0 的旧 feed/version 和 G2 的新 feed/version，调用：

```powershell
pwsh -NoProfile -File experiments/PackageConsumerProbe/Run-OrganizationMigrationProbe.ps1 `
  -LegacyPackageSource experiments/PackageConsumerProbe/obj/event-history-20260912091137-71028-f23f502f/feed `
  -LegacyVersion 0.0.0-event-history-e2e.20260912091137.71028 `
  -PackageSource obj/db071-new/feed -Version 0.0.0-db071.20260912.1 `
  -Stage Complete -WorkRoot obj/db071-organization
```

实际输出：`TwoRealPackages:HistoryUnchanged:OldFramesUnchanged:18:ReadOnlyUnchanged:Delta:NoChange:Passed`。
旧 Seed 与新 read/continue/check 均为独立进程，新读分别验证全部旧 E/S、值、循环及容器别名；
Resume 验证旧 preceding State/PendingEvent，完成 S 并继续 E/S，实读 local rows 证明 Delta 与 NoChange。
两个 lane 的 Model.cs 字节相同；accepted `.dgschema` 文件名/字节不变；只读前后文件集、长度、mtime/hash 相同。
18 个旧完整帧包括 Schema 1、State 4、Journal events 4、Journal refs 9，续写后原 offset/length 区间 hash 全相同。
追加后的文件长度可以增长，没有将整文件 hash 不变误作正常续写要求。

新旧四个 DG DLL 的实际运行路径和 hash 在 `legacy-seed.log`、`current-{read,continue,check}.log`，
并与各自真实 PackageReference 缓存核对。新旧 generator provenance 分别记录在两个 lane 中，SHA-256 为：
旧 `197CC99BEDB30AD673057881192233D9732AF75F42E9B2546012BC44B8B488E6`，
新 `0B49A6E8164E7D2C4CB1E7D10E82073AF9EE460EF0F8CF808C9E026EA014644D`。
两套真实 Runtime/Generator 不是同一 Runtime 的模型 HistoryVersion 变化。

真实下游副本在 `obj/db071-downstream/{old,new}`，来源清单 `source-manifest.json`、最小差异 `migration.diff`。
340 个 tracked 文件复制自上述 DramaBoard HEAD；新副本只有五个业务源码/项目文件做 PackageReference/using 迁移。
Kernel/Spatial/FirstBoard 的 64 份 accepted history 与领域逻辑保持；两副本使用相同的隔离构建配置。
旧包采用真实既有 pin `0.0.0-dramaboard.20260912.1c6083c.1`（来源 DG `1c6083c`），不是 G0 的 c7ee495 包；
这条下游基线与上面的纯组织跨包见证分开解释。未运行固定拉取旧提交的 Prepare-DurableGraph 脚本。

新旧副本 solution build 均为 0 warning/error，持久化 tests 各 28/28（含 10 个 cold-process 案例），
每侧另有两次原样 cold-process create/open。64 份 accepted history、九包版本与九个输出 DLL hash 均通过核验。
两侧通过 `Run-Lane.ps1` 串行 build/test，再由 `Trace-Lane.py`、`Verify-Lane.py` 核验 history、九包版本与 DLL hash。
host trace 的 TPA 证明宿主解析路径，结合成功消费者持久化执行支撑真实包来源，不把它单独称作九程序集全部实例化加载证明。
完整输出在 `obj/db071-baseline/downstream-{old,new}.log` 和副本的 `{old,new}-logs/`。

## G4：独立审阅与收口

独立只读审阅分两轮检查实际旧/新源码、未跟踪的移动目标、编译清单、SG metadata、包闭包与新见证，
补充复核 G2/G3 运行证据和隔离下游差异。没有遗留阻碍项。主线程复核了关键 diff、签名清单和实际日志，
没有用子代理总结代替最终 build/test/package/downstream 验收。

最终核对：

- 编译清单 4,112 条零差异；七个产品项目的 ProjectReference/IVT 经名称映射后与基线逐项相同，
  solution 的七产品/四测试路径全部存在；`project-graph-check.log` 保存结果。
- 源码中的 2,049 处显式 DurableType ID/版本与 DurableField ID 保持；`domain-id-check.log` 保存结果。
  所有最终产品编译输入 hash 与 G2 已验证包的 `source-input-hashes.json` 相同。
- README/PACKAGE、活动 Probe、目标/术语源码落点、设计索引与 PROJECT-STATE 已同步；路线图移除已完成 DB-071 项。
  旧审计统计仍明确是 c7ee495 基线，历史理由与冻结结果不改写；历史文档只修本次移动影响的有效源码链接。
- 检查 1,475 处本地链接/锚点，新增失效为 0；22 处未改变的历史失效引用与 HEAD 对照后单独记录，
  没有以路径替换伪造其有效性。详情 `obj/db071-baseline/link-check.json`。
- 当前源码/测试/项目/runner 的旧完整框架名称仅存在于显式旧包 lane、旧→新映射及其反向核查，
  见 `legacy-name-audit.txt`。原 `StateStoreConsumer` 等 Probe 自身身份保持，不代表仍引用旧产品包。
- `git diff --check` 通过；新 PowerShell runner 语法通过。`final-git-status.txt` 保留收口状态。
  DurableGraph HEAD 仍为开工 c7ee495；Atelia 既有 diff 与 G0 相同，DramaBoard 正式工作树仍干净且 HEAD 不变。

源码移动、名称/接线更新、新验收工具、陈旧 manifest 断言修正与上述文档变化构成本片全部改动。
未新增产品程序集或兼容壳，未修改算法、默认值、持久格式或发布/故障/重入/append-only 合同。
G4 完成时成果停在本地待提交：保留原有改动；当时没有 commit/push、远程包发布、正式下游 pin 变更或业务存档操作。

## 下游试用与提交收尾

2026-09-12，DramaBoard 提供[正式工作树试用反馈](../../../../drama-board/docs/feedback/durablegraph/004-db071-namespace-adaptation.md)，
报告同一试用包的 solution 构建零警告/错误，11 个测试程序集共 527 项通过、零失败/跳过。
64 份 accepted history 保持；九个输出 DLL 与 feed 包资产 SHA-256 一致。
它还独立验证旧程序创建的 Continue/Reverse、S1/pending E2 存档由新程序冷开、续写、仅 fold pending，
完整 World/Cursor/NextRequest/Pending 和事件内容与旧程序连续执行一致；再次冷开无 replay，纯 S-head 读取不改存档。
这些是下游反馈提供的 Windows 本地证据，不宣称远端 CI 或 Linux 验证。

用户确认试用满意、无问题后批准本库 Git 提交。提交前重新记录工作树，核对产品编译输入 hash 与 G2 验收包完全一致；
补充反馈与交接状态，仅做文档收口，沿用本片已经完成的 build/test/package 证据。
本次提交收录 DB-071 设计、迁移、验收工具与文档；不替下游修改正式 pin，不发布远程包或 push。
