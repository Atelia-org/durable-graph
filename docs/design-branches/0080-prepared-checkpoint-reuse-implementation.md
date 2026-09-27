# DB-080 施工与验收记录

> 状态：**已实施并独立验收，单槽默认启用**；2026-09-28。基线 `70a5d1c`。合同见 [DB-080](0080-prepared-checkpoint-reuse-slice.md)，公共语义见 [DB-083](0083-repository-checkpoint-api-user-stories.md)。

## 问题与范围

在真实 Repository.Checkout/Fork 路径保留最近使用的一份 State 准备材料：同一 State 再次恢复免 Decode、Normalize 和已证明的全图扫描，仍逐次复核完整 live Schema 依赖并独立物化、导入保存身份。
无 State 的请求完全绕过槽；请求 Head 与最近 State 分别处理。持久格式、公开 API、存储包 pin 不变；热提交准入、不可变叶实例复用和只读入口跨操作缓存不属本片。

最小成功标准是启用/禁用槽的产品结果和保存来源一致，重复请求的准备计数降为零，中间 Schema 迟冲突在 Allocate 前阻止命中交付，任何失败不发布半成品槽或分支。

## 分工与验收账本

| 要求 | 实现 / 验收责任 | 当前状态 |
|---|---|---|
| G0：词法收集完整 exact requirements，预闭合计划与工具依赖 | Runtime 实现者；历史测试与主线程复核 | 已验证 |
| G1：完整来源、根、模型和证书，独立物化 | Persistence 实现者；冷/命中产品测试 | 已验证 |
| G2：单槽、无 State 绕过、发布前候选与成功后安装、fault/Dispose 释放 | Persistence 实现者；失败与隔离测试 | 已验证 |
| G3：普通/Family、历史升级、容器、身份与保存等价、真实 Fork 测量 | 分立测试与测量实现者；主线程串行运行 | 已验证，测量限制见下文 |
| 集成、独立审查、文档与最终证据 | 主线程与只读审查者 | 完整回归及独立终审通过 |

先冻结 Runtime 内部证书接缝，再接 Persistence；测试文件与产品写入互不重叠。主线程检查实际 diff 与执行结果，不以代理报告替代验收。

## 实际接缝与审查裁决

- [Runtime requirements](../../src/DurableGraph/Runtime/Binding/StateBindingContext.Requirements.cs) 提供同步词法收集，成功子范围合并、异常释放、完整 ObjectLayout 的 Schema/容器槽检查；沿用原 `ExactSchemaRequirementSet` 去重、冲突诊断和 live 权威复核。
- [StateModelSnapshot](../../src/DurableGraph.Persistence/StateModelSnapshot.cs) 的 model、reader、value、Nullable 与容器闭合缓存，以及 owner/容器/value UpgradePlan，保存各自闭合时的标准检查证据；缓存命中复核并传递这些证据。
- [Repository](../../src/DurableGraph.Persistence/Repository.cs) 仅在 Checkout/Fork 选择最近 State 后查一个槽。冷范围跨完整 Prepare、全部 source/current layouts、物化和工作副本构造；完成后形成候选，Fork 发布成功后只安装引用。无 State 早返不碰槽；fault/Dispose 清槽。
- [PreparedStateRestoration](../../src/DurableGraph.Persistence/PreparedStateRestoration.cs) 将精确 State 选择、[单图材料](../../src/DurableGraph.Persistence/PreparedGraphSelection.cs)与证书组合；命中复核模型、同 Store 来源、根实际 binding 和全部 requirements。[GraphReader](../../src/DurableGraph.Persistence/GraphReader.cs)继续承担独立物化与保存身份导入。活动工作副本保留 normalized 保存来源，不持有 State 证书或槽。

独立审查提出并解决了一个实际完整性问题：只在本次恢复收集，会漏掉更早已闭合工厂经同 context 的标准 Resolve 检查过的额外依赖。修复同时覆盖 binding 缓存和计划缓存，不只保留 source/current 两端或首次 factory 执行结果。历史测试分别构造 model、reader、value factory 的额外依赖；Runtime 测试另外证明预闭合 UpgradePlan 必须传递其 reader factory 证据。

Owner 计划也显式合并声明工具的 requirements，与容器机制一致。工具选择受 exact endpoint 的字段约束，不能仅凭原代码缺少一次显式合并就宣称已有可复现的工具依赖遗漏；本片可执行反例的核心是预闭合标准检查证据丢失。

槽容量只限制 State entry 数量。固定模型环境本身还保留成功 binding/plan 的依赖证书，活动工作副本有各自保存来源；不把“单槽”写成总堆或字节上限。

## 验收覆盖

| 证据入口 | 对应风险 |
|---|---|
| [Runtime 证书测试](../../tests/DurableGraph.Tests/DB080SchemaRequirementTests.cs) | 嵌套/异常作用域、稳定诊断与冲突、base/Nullable/各容器布局、预闭合中间版本和 reader factory 依赖 |
| [Repository 单槽测试](../../tests/DurableGraph.Persistence.Tests/DB080PreparedStateReuseTests.cs) | cold/hit、环/别名/List 隔离、独立续写、精确 S/E/E Head、Event-first、同盘重开、根替换、占用、失败发布、淘汰、fault/Dispose、非法新 revision |
| [历史与来源测试](../../tests/DurableGraph.Persistence.Tests/DB080PreparedStateHistoryTests.cs) | v1→v2→v3 的中间独有依赖；工厂额外依赖；迟冲突不影响 Event-only；全部 source/head/H、高 ID、不可达行、Empty、Event 保留 rewrite 与继续保存 |
| [首次物化依赖测试](../../tests/DurableGraph.Persistence.Tests/DB080MaterializationRequirementTests.cs) | 首次 Allocate/Hydrate 中的标准检查也纳入证书，下一次迟冲突在 Allocate 前拒绝 |
| [真实 Fork 测量](../../tests/DurableGraph.Persistence.Tests/DB080PreparedStateMeasurements.cs) | 同路径禁槽/启槽，普通/Family、环/List、连续同 State/连续 Event/低命中/Event-only |

保存来源测试按实际成本策略允许后继写入选择 Base 或 Delta，验证源身份、rewrite 清除、内容和读回；不以强迫 tiny DTO 选择 Delta 代替保存正确性。原有 Delta、Dictionary、数组、生成器、Schema/升级与只读恢复回归仍须通过。

## 测量与停点

Windows x64、.NET 10.0.5、同一 Release 构建；64 节点 mutable 环，第二种图多一个含全部节点的 List。
普通 closed model 与手写 `StateDefinitionBinding` 的 Family 闭合路径各测一遍；这不是新增的生成器或 PackageReference 消费证据。
4 种选点序列 × 2 种图 × 2 种 binding × 禁槽/启槽，共 32 格；每格丢弃 1 次预热、记录 3 次，每次 12 个新命名分支 Fork，合计执行 1536 次 Fork。
每个样本使用新建、相同种子历史的仓库，启用/禁用执行先后交替；没有关闭 Store/OS 缓存。

时钟及当前线程托管分配只包围完整 `Repository.Fork`，包含持久 ref 发布，排除 Dispose 与断言。下表每格聚合 12 个记录样本，数值对应 **12 次 Fork**，MB 为十进制。

| 场景 | 禁槽耗时 median [min,max] ms | 启槽耗时 median [min,max] ms | 禁槽 / 启槽分配 median MB |
|---|---:|---:|---:|
| 同一 State 扇出 | 1088.3 [967.2,2233.0] | 1084.0 [941.8,1425.2] | 4.801 / 1.705 |
| 连续 Event 共享 State | 1085.9 [981.9,2598.6] | 1078.0 [999.1,1224.2] | 4.819 / 1.722 |
| 两个 State 交替 | 1094.8 [924.8,1264.8] | 1120.2 [916.2,1499.3] | 4.793 / 4.794 |
| Event-only | 945.4 [915.4,1156.5] | 1031.8 [949.8,1291.3] | 0.985 / 0.985 |

所有普通/Family 和图形变体的计数一致：前两场景启槽为 11 hit / 1 miss、1 次 Prepare，禁槽为 12 次 Prepare。
Decode、Normalize、current 引用验证、可达遍历分别从 768/780 次降为 64/65 次；Allocate/Hydrate 都保持 768/780 次。
交替 State 双方均 12 miss；Event-only 所有图计数与槽规模均为零。测量图没有 Dictionary，相关调用计数为零，不据此声称量到了 Dictionary 收益。

有 State 且启槽时实际保留 1 个 entry、64/65 source 行、64/65 reachable ID、1 个 exact Schema requirement。
输出的 1024/1284 bytes 仅为 DTO 字段与 reachable ID 的逻辑 payload 估计，排除了对象头、字典、来源、布局、证书路径和共享环境等，**不是 retained heap 字节量**。

**结论：保留默认启用单槽。** 高命中场景有可归因的准备工作消除及约 64.5% / 64.3% 的托管分配中位数下降；低命中不增加第二套恢复路径。
此实验没有证明完整 Fork 普遍加速、低命中没有耗时退化或大图驻留成本可忽略。持久发布使总耗时波动显著；Event-only 完全绕槽仍有时间差，不能把所有 wall-clock 差归因于缓存。
不据此扩充 LRU、多槽、公共容量选项或热提交准入；大图/多模型实际驻留和业务长轨迹反馈仍是后续优化前提。

测量入口只供内部实验：`PreparedStateReuseEnabled` 禁用时仍走相同冷准备/证书/物化路径；`RestorationStatistics` 可跨操作累计。
Hit 计选择匹配的尝试，随后证书失败也计入；Miss 包括禁槽的冷路径；Event-only 均为零。
Normalize 计实际调用（含 current 恒等路径），另计 current 引用验证、Dictionary key 验证和 reachability；
没有单独计 stored 引用扫描、Schema 比较次数或物理 I/O。Allocate 计回调（含 string），不等于 CLR 新对象个数。

## 执行证据与后续

基线 Persistence **863/863** 通过；最终根 Release build **0 warning / 0 error**；完整 solution **2899/2899** 通过、0 failed、0 skipped：
Serialization 163、Storage 202、Persistence 897、Runtime/Generator 1637。新增 45 个语义验收用例和 1 个 opt-in 测量入口；未删除或改写旧测试。
历史专项修正后 **7/7** 通过，另行开启测量入口执行 **1536 次 Fork** 通过。最终完整回归运行后未再改动产品或测试源码。
独立源码审查无遗留阻断；主线程复核源码、测试与实际计数。新测试夹具曾缺 Family 的 source reader resolver、未真正调用 value factory，并错误强求微小 DTO 写 Delta，均已按真实机制修正，没有降低产品校验。

本地证据目录为 `.artifacts/db080/`：`baseline.log`、`build.log`、`tests.log`、`history-final.log`、`measurement.log`、`measurements.jsonl` 与 `test-results/*.trx`；原始 JSONL 含逐 Fork 时间/分配及每个 binding/shape 样本。
构建与测试串行执行：

```powershell
dotnet build DurableGraph.slnx -c Release --no-restore
dotnet test DurableGraph.slnx -c Release --no-build --no-restore --logger "trx;LogFilePrefix=final" --results-directory .artifacts/db080/test-results
$env:DURABLEGRAPH_DB080_MEASURE = '1'
$env:DURABLEGRAPH_DB080_MEASURE_OUTPUT = "$PWD/.artifacts/db080/measurements.jsonl"
dotnet test tests/DurableGraph.Persistence.Tests/DurableGraph.Persistence.Tests.csproj -c Release --no-build --no-restore --filter FullyQualifiedName~DB080PreparedStateMeasurements --logger "trx;LogFileName=measurement.trx" --results-directory .artifacts/db080/test-results
Remove-Item Env:DURABLEGRAPH_DB080_MEASURE
Remove-Item Env:DURABLEGRAPH_DB080_MEASURE_OUTPUT
```

普通 CI 中测量入口不执行实验、也不设速度断言；本轮另以环境变量实际执行并通过 1536 次 Fork。
本轮无公开 API、格式、Generator 或包接线改变，Storage pin 不变；未重新执行 PackageReference probes，不把历史包证据算成本片新验证。
文档集成核对包括 `git diff --check`、10 个修改/新增 Markdown 的本地链接检查，以及既有标题/显式 anchor 保持；验证产物留在本地 `.artifacts`，不作为产品文件提交。

后继候选：[DB-081 热 State 准入](0081-hot-commit-restoration-material-slice.md)；[DB-082 不可变叶实验](0082-prepared-immutable-leaf-reuse-slice.md)仅硬依赖本片，不随本片实施。

后继先读 [PROJECT-STATE](../../src/PROJECT-STATE.md)，再读本记录与 DB-081。DB-081 的关键门是新 State 的完整冷恢复等价证明：
Capture 的依赖不能代替冷 reader 的依赖，也不能继承已经无关的历史 Upgrade 证书；候选所有可失败工作须在 ref 发布前完成，
State 正常 Install 后仅作引用安装。无法证明的候选继续走本片冷路径，Event 不碰槽。
DB-082 应另测有证明的 immutable 叶复用，不能解除 mutable 隔离或省略被跳过回调的额外依赖。
