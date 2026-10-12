# DB-085 S0-D：薄 CLI rescue 与显式维护接线

> 状态：内部受限维护接线已完成并通过独立验收，2026-09-29。依据[联合方案 §4.3、§4.4、§6、§10](0085-joint-crash-recovery-design.md)与 [S0-C](0085-s0c-recovery-implementation.md)。保留两仓已有改动；实际改盘仅限新建独立夹具。

## 问题、范围与验收

把已验收的内部恢复核心接到薄 CLI，由库完成资格、动作和资源关闭，返回不含活 Repository/Store 的维护报告。无需业务模型登记；只证明既有结构规则，不能承诺业务 body、factory 或 Upgrade 可恢复。

沿用 S0-C 的已初始化、单个已绑定 branch、各 store 首段、State/Event 提交、最多一个支持短尾；完整事实和地址含 orphan 全部保留。Trailer 1–11B 仍 Unresolved。普通 Open、writer 和完整 Audit 保持。不扩展生命周期、格式、Clean、索引、快速 writer 或平台断电保证，不冻结宽泛公开恢复 API。

## 共同合同

| 命令 / 库内入口 | 是否改盘 | 持锁及资源寿命 | 返回报告 / 退出状态 | 中断与拒绝反例 |
|---|---|---|---|---|
| `audit <path> [--json]` / 既有 Audit | 只读 | 既有 Audit 独占与关闭纪律 | 既有 0/1/2/3 保持 | 损坏仓库无需先成功 Open；不隐式救援 |
| `rescue <path> --check [--json] [--read-budget <bytes>]` / 内部资格维护入口 | 只读 | 复用 Qualify；返回前关闭资源及释放锁 | Qualified 可返回 0，仅表示检查当时具备受限资格；拒绝 2，未完成 3 | 报告不能作为稍后执行的许可；Invalid/Unsupported/Unresolved 与 I/O/取消/预算分开 |
| `rescue <path> --apply [--json] [--read-budget <bytes>]` / 内部执行维护入口 | 显式允许受限截断及保守确认 | 调用同一 Recover，以空 registry；检测至交付同次锁；库关闭交付资源后才返回纯数据报告 | 成功 0（区分截断/无动作）；资格拒绝 2；预检未完成 3；动作失败 4；严格打开、交付或关闭失败 5 | 不接收检查报告；动作已尝试/可能改盘/长度变化/屏障/取消信息不丢失；动作后取消不声称源未变 |
| 参数验证 | 不接触仓库 | 解析全部参数后才调用库 | 缺少或同时指定模式、未知/重复参数、非法预算等均 1 | `--apply --unknown` 不能先恢复再报用法错误 |
| 文本/JSON 输出 | 库维护已结束 | 输出时无活资源；CLI 只呈现及映射退出状态 | 两格式表达相同报告事实；输出失败为非成功（6） | 输出失败或输出前进程终止不能回滚已经完成的恢复；再运行重新从磁盘推导 |

报告保留核心状态、资格原因、选中文件与原/候选长度、实际动作标志、各阶段计量和预算范围；不能把拟执行长度冒充已观察到的最终长度。关闭失败报告不冒充成功；执行后任何失败都保留已知动作信息。新增维护总计覆盖资源关闭，核心计量单列；输出计量独立，不重复相加 Confirmation 子集。

最小可观察验收：库与 CLI 状态一致；检查不改任何字节；空 registry 可完成真实模型仓库的结构维护；成功/失败后可重新独占访问；真实短写外部 kill → CLI rescue → 再执行 → 续写 → 新进程冷开保持完整事实。覆盖动作后失败、取消、输出失败和输出前再次外部终止。

## 验证安排

明确实现有界委派 Sol medium，主线程整合，最终 Astra max 只读独立验收。全部 .NET 串行 Release，专用 `C:/Users/gdtut/AppData/Local/Temp/db085-s0d-20260929` TEMP/TMP，显式 `UseStorageSources=true`、`StorageSourceRoot=E:/repos/Atelia-org/atelia-storage`、`ShouldUnsetParentConfigurationAndPlatform=false`。

新增小规模 CLI 进程闭环，不机械重跑 S0-C 的全部 138 案例；若核心变更或出现新风险，按实际影响补回归。成本保留请求/返回/预取/调用、耗时与线程分配；预算仍仅覆盖资格 RBF 请求，未覆盖元数据、动作复检、严格打开、CPU、总内存和设备 I/O。进程冷启动不等于 OS 页缓存冷。

起点 diff 和 untracked SHA256 保存在 `.artifacts/db085-s0d/`。不发布或迁移真实数据；用户已允许按需本地提交，当前先保留 working tree 供整合验收。

## 实施结果

[RepositoryRescueMaintenance](../../src/DurableGraph.Persistence/RepositoryRescueMaintenance.cs) 是内部库接缝。`Check` 直接复用 Qualify；`Apply` 以新建空 `StateModelRegistry` 调用同一 Recover，接管并关闭其成功返回的 Repository，才返回纯数据 `RepositoryRescueReport`。CLI 获得单个程序集 friend 接线，未新增公开恢复签名或第二套恢复规则。报告对象及其所有嵌套数据不携带 Repository、Store 或文件句柄。

[MaintenanceCli](../../src/DurableGraph.Cli/MaintenanceCli.cs) 完整验证模式、重复/未知参数和非负 Int64 预算后才调用库；`--check` 成功称 Qualified，不称仓库已修复。文本逐项呈现与 JSON 相同的报告，包括预算、计量和 nullable 字段。`SelectedFile.OriginalLength / VisibleEof` 是原长度与候选边界；须结合 `ActionAttempted / LengthChanged / BarrierCompleted` 判断已发生的动作，不能把候选长度无条件解释为修改后的长度。`NoAction` 只表示成功且未截断，仍执行原有保守依赖确认。

`ResourcesClosed=true` 仅用于有完成证据的资格返回或已交付资源关闭；失败路径无法确认全部嵌套清理时为 null，而非宣称一定泄露或一定关闭。取消/observer 异常不能跳过实际 Dispose。Recover 的窄尾修捕获清理异常并继续尝试另一资源的清理；`CleanupPreviousStatus / CleanupDetail` 保留原失败阶段，动作及取消标志仍在。真实 Dispose 后的 observer 注入验证此报告路径，**不是操作系统 Dispose 系统调用故障证据**；普通 Open/Dispose 及资格策略未改。

输出在库关闭资源之后进行；rescue 输出失败返回 6，并尽力提示执行可能已经改变文件，即使 stderr 也失败仍不返回成功。输出之前再 kill 可以丢失诊断，不能改变已有恢复结果。报告未引入持久回执，重试不依赖前次内存报告。

## 正确性与进程证据

- Release solution 与实验宿主构建均 0 warning / 0 error。Persistence **1152/1152** 通过，包含 S0-D 新增 **40** 个实例（39 个维护/CLI、1 个成本），以及 S0-C **45** 个回归；既有 Audit/CLI 和持久化测试一同通过。
- [维护测试](../../tests/DurableGraph.Persistence.Tests/DB085S0DMaintenanceTests.cs)覆盖真实 typed 仓库的空 registry 维护、check 字节不变、apply/no-action、旧检查后源变坏再执行拒绝、完整 CRC 错误、范围外输入、Trailer Unresolved、锁竞争/文件 I/O/预算/取消、动作后与交付失败、关闭 observer 异常、文本/JSON 一致、未知参数在改盘前拒绝和输出失败重跑。资源释放通过独占打开夹具全部文件验证。
- [CLI 进程实验](../../experiments/RecoveryCrashProbe/README.md#s0-d-薄-cli-rescue-进程闭环) **7/7**：State 提交的 Schema/State/Event/ref 四种实写 2B 短尾、Event 提交的 Event 短尾、报告输出前外部 kill、报告输出异常。每例均从真实 writer 的 READY/PID 握手外部终止，不执行正常关闭来冒充崩溃。
- 每例先证明实际 apphost `--check` 不改文件集合/长度/hash；首次 apply 或输出中断后，先直接普通 `OpenExisting` 验证并续写，再执行重复 apply（无动作且已续写字节不变），最后独立只读进程冷开。中间没有再次 Recover 来掩盖首次结果。全部完整前缀、orphan、已确认 Event 地址/revision 和 State/Event 图关系保留；主线程重算 **35** 个完整 RBF 前缀 SHA256 与帧 offset 一致。
- 报告边界两例由实验宿主调用真实 `MaintenanceCli.Run` 配合 TextWriter：首写时独占打开全部仓库文件，再外部 kill 或抛 IOException。其余路径及重试调用实际 CLI apphost。分别标记两种执行方式，不声称给生产 CLI 加了故障环境开关。

证据在 `.artifacts/db085-s0d/`：`dg-build-fixed.log`、`host-build.log`、`persistence-tests.log` / `persistence-tests/*.trx`、`host-matrix-final.log`、`host-evidence.json`。原始进程日志与夹具保留于 `C:/Users/gdtut/AppData/Local/Temp/db085-s0d-20260929/cli-kill-86cd6c9fddae421cb10138a1c1c6ecf0`。初轮 build 的缺少 namespace 导入及 runner 函数名命中 PowerShell `cli` alias 均已修正；初轮日志保留，不作为通过证据。

核心、CLI、Persistence tests、实验宿主加载的 Persistence DLL SHA256 均为 `3CDEB1D439AA0E59F08E339B00E94F7B9D24D4877011D58A9CB6E25267645A2E`。上游本片未改，tracked 起止 diff hash 相同，全部既有 untracked 保留；未重跑无变化的 storage suite，也未机械重跑 S0-C 全部 138 例。既有完整矩阵证据仍由 S0-C 记录维护。

## 成本增量与完整入口

[成本测试](../../tests/DurableGraph.Persistence.Tests/DB085S0DCostTests.cs)隔离执行：三格 × 库维护/文本 CLI/JSON CLI，每组新建一份预热与三份观测夹具，27 个记录样本；Audit、完整字节核对在计量外。以下为各字段独立中位数，时间不能跨行相减，独立中位数之和也不必等于总中位数。

三种入口的 RBF 读取量逐格相同；Close 与 CLI 接线未增加 RBF 读取。资格、动作及严格打开保持 S0-C 的重复扫描，没有优化或转移到首次读取：

| 夹具 | logical 请求 / 返回 B | raw 请求 / 返回 B | raw 调用 | 预取多读返回 B | 库完整调用 ms | 库完整调用线程分配 B |
|---|---:|---:|---:|---:|---:|---:|
| 基线 | 6,920 / 6,920 | 67,980 / 4,752 | 146 | 2,148 | 36.565 | 2,109,912 |
| 2B 短尾 | 6,972 / 6,972 | 68,032 / 4,804 | 154 | 2,148 | 37.441 | 2,178,640 |
| +32 次提交 | 171,404 / 171,404 | 205,708 / 178,064 | 6,130 | 37,732 | 86.580 | 5,164,056 |

| 夹具 / 入口 | 外层完整调用 ms | 同样本外层调用 − CoreTotal ms | 同样本外层调用 − CoreTotal 分配 B | 输出 UTF-8 B |
|---|---:|---:|---:|---:|
| 基线 / 库 | 36.565 | 0.110 | 1,992 | 0 |
| 基线 / 文本 CLI | 38.736 | 0.529 | 85,672 | 7,153 |
| 基线 / JSON CLI | 36.302 | 0.249 | 43,704 | 6,555 |
| 短尾 / 库 | 37.441 | 0.104 | 1,992 | 0 |
| 短尾 / 文本 CLI | 37.672 | 0.466 | 88,872 | 7,324 |
| 短尾 / JSON CLI | 37.027 | 0.240 | 44,712 | 6,723 |
| 历史 32 / 库 | 86.580 | 0.115 | 1,992 | 0 |
| 历史 32 / 文本 CLI | 85.763 | 0.431 | 86,096 | 7,224 |
| 历史 32 / JSON CLI | 76.417 | 0.226 | 44,120 | 6,626 |

差值从每个样本自身的内外计量得出，包含内层计量基点之外的对象、空 registry、关闭、入口/解析/呈现/返回与计量开销，不等同纯序列化或纯 wrapper 净成本。报告 `Total − CoreTotal` 的分配为 1,352B；另有约 640B 在内部 Total 的分配基点之外，所以不能把前者称完整增量。Close 单独约 0.095–0.111ms / 128B，包含于 Total。完整调用计量使用测试宿主的外层 Stopwatch/线程分配；参数数组和 StringWriter 创建在调用外，进程启动不在这张性能表内。

全部阶段及八个读取字段保存在 `cost-tests/*.trx` / `cost-samples.json`，派生中位数在 `cost-medians.json`；Confirmation 仍是 StrictOpen 子集，不能二次相加。输出写入 StringWriter，UTF-8 字节数在计量外计算，不代表终端/管道耗时或设备 I/O；文本通过同一 JSON 数据投影，故分配高于 JSON。原始外层值完整保留，没有只报告内部差值。

这些是刚写入夹具的受控小规模观测，OS/device cache 未冷，线程托管分配不是峰值或驻留内存。字节预算仅限资格 RBF 请求；元数据、动作复检、严格打开、CPU/解压、输出和总内存未受其约束。全历史与重复依赖验证仍随规模增长，本片不提供全年规模或恢复时长保证。

## 剩余门槛

Astra max 只读独立验收判定 **S0-D 通过，无剩余阻塞项或必补最小测试**。审阅发现的进程证据次序缺口已修正为首次维护后先直接严格续写，再 repeat apply；计量文字已明确内部基点外开销。审阅者独立核对 7 次首次真实短写 kill、1 次报告前 kill 与输出失败 exit 6，重算 35 个完整前缀 hash/offset、27 个样本的全部八字段读取账本及所有中位数，均一致；四份 Persistence DLL 一致。首次继续日志没有恢复记录，进程日志时间序也确认直接续写先于重复维护。没有把 S0-C 的旧验收结论替代本片证据。

S0-D 只接通受限结构维护入口；空 registry 通过不等于业务图可以物化。普通公开 Open、writer、Audit 的语义保持，恢复核心和维护调用仍内部。公开只读候选的操作内去重、S1 生命周期/轮转/tag/checkpoint、Trailer 合法补全与快速可写路径仍独立待办。受控 OS/OpenZFS/SSD 平台断电证据、独立包消费及交付均未取得，本片没有发布或迁移真实数据。
