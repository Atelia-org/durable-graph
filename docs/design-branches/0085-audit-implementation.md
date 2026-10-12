# DB-085 第 0/1 片：共同校验与只读 Audit

> 状态：已实现并通过独立验收，2026-09-29；代码保留在两仓 working tree，尚未包交付。联合方案的[分片顺序](0085-joint-crash-recovery-design.md#10-压缩后的推进顺序与调度停点)为范围依据；本记录维护首片合同、接缝与验收证据。

## 问题与边界

能否把现有结构检查放进产品库，供库 API 和 CLI 共用，并在正常 Repository 无法打开时返回可定位的只读审计报告？

本片交付完整结构 Audit、共同规则及薄 CLI `audit`。Normal 的扫描时机、可写确认屏障、发布、fault 和异常合同保持；不实现自动恢复、截断、Clean 标记、轻量 Open、Schema 索引或格式迁移。健康报告说明已完成下述持久结构检查，不认证应用业务语义、factory/Upgrade、硬件持久性或任意外部历史删除。

基线为 DG `c900edd5b3a921b7bf46df2ac0482dc39597e606` 加用户已有 breaking 适配及 DB-085 文档；storage `bf7d68ab3f3fbad18b87549dbf6ce2aae499c24a`。源码联调显式使用 `UseStorageSources=true` 和兄弟仓绝对路径；公开 package pin 不改变。既有适配不回退、不重写。

## 第 0 片合同

高级别故障保证以[联合方案 §5.1](0085-joint-crash-recovery-design.md#51-受控部署用具体保证收缩故障模型)为前提：成功屏障保护数据、长度及必要名字，后续写入不伤已确认旧范围；读取返回正确字节或明确错误；所有 writer/维护路径遵守独占及依赖验证、持久确认先于引用记录写出的协议。候选 OpenZFS + 4 SSD 尚未选定 OS、版本和拓扑，本片不签发平台保证。

writer 范围是受控 Repository 与遵守原发布协议的 storage 调用者。任意低层非 durable Append、外部修改/删除、谎报成功的设备、多 writer、未知导入来源不自动取得该资格。只读 Audit 本身不要求先取得可信 writer 资格；它检查可观察结构，不通过一次检查替代持久确认。

| 现有写序 / 检查点 | 源码位置 | 首片处理 |
|---|---|---|
| Schema append → flush；重开完整目录重放 | `SchemaStore` | 复用目录规则；维护检查明确区分不支持版本 |
| State Builder → EndAppend → 原 writer lease 内 DurableFlush | `StateRevisionStore.AppendCore` | 不改写序；Audit 枚举全部 State，包括 orphan |
| State durable → Journal event durable → branch ref publication | `Repository.Publish`、`HistoryJournal.Append` | 不改发布；抽取共同图结构验证 |
| writable Open 先 Schema/State 确认，再 Journal events → ref objects → ref-op-log 确认 | `GraphResources.Open`、`HistoryJournal.ConfirmDurable` | 全部保持；本片没有快速 writer |
| allocation → Init → BindName；Close → Archive；event confirmation → tag | storage `EventJournal.Refs/Tags` | 生产 Audit 复用原 toolkit 的全历史规则及生产 codecs |
| old segment flush → next header/flush → locator；实际 op-log → catalog snapshot | storage `RbfSegmentStore`、`EventJournal.CheckpointCatalog` | Audit 库存、边界与重放一致性；不修复 metadata |

## 库合同与依赖

- `Atelia.DurableGraph.RepositoryMaintenance.Audit(path, cancellationToken)` 位于 **Persistence** 程序集，不需要模型注册、不先调用 `Repository.Open`。
- Audit 以只读方式独占现存 `repository.lock`，不创建锁文件；持锁直至所有临时 Store/reader/cache 关闭。协作使用者须先关闭 Repository；稳定副本也保留锁文件。获取失败就报告检查未完成。
- 报告包含版本、`Completion`、只读 `Findings` 和派生 `IsHealthy`。完成状态为 `Completed`、`Incomplete`、`Cancelled`；发现种类为 `InvalidData`、`UnsupportedFormat`、`IoFailure`、`Warning`，保留稳定 code、相对路径、可用 offset 和说明。不能完成的阶段不出具健康结论；已确认的问题仍保留。
- storage 的 `EventJournalAudit.Audit` 承担整个 Journal 的事实/索引审计，`SegmentStoreAudit.Inspect` 共享目录、连续段和 locator 库存规则。旧 toolkit 调用同一生产核心，JSON/退出码、rebuild 候选写入及 legacy 迁移外壳留在工具侧。
- DG 与正常路径共用图结构/记录解码规则。Audit 额外枚举全部 State 的 Parent、external heads、local priors、实际帧边界及 Base 表示依赖；不执行 orphan 应用 body/factory/Upgrade。
- 损坏或不支持的组件可使后续依赖阶段不能进行。报告保留物理/库存发现并标记未完成；本片不为读出损坏前缀引入虚拟文件系统或恢复 reader。
- 取消在阶段、目录、帧、对象和分块 hash 边界响应；现有单个大帧读取/解码不保证即时中断，也没有总读取/峰值内存上界。完整 Audit 允许历史规模相关成本。

## CLI

现有 `DurableGraph.Cli` 空程序承载新工具集，依赖 Persistence，仅解析参数、调用库和显示报告：

```text
durable-graph audit <repository-path> [--json]
```

JSON 使用库报告的字段及字符串枚举；人类输出包含发现与位置。退出码：`0` 健康、`1` 参数错误、`2` 已完成但发现无效数据、`3` 未完成或取消。尚无 `rescue` 命令。

源码联调执行方式：

```powershell
dotnet build DurableGraph.slnx -c Release -p:UseStorageSources=true -p:StorageSourceRoot=E:/repos/Atelia-org/atelia-storage
dotnet src/DurableGraph.Cli/bin/Release/net10.0/Atelia.DurableGraph.Cli.dll audit <repository-path> --json
```

## 验收账本

| 要求 | 实现责任 | 观察标准 | 状态 |
|---|---|---|---|
| 生产 Audit 核心、旧工具复用 | storage 实现 | 库/CLI 同结果；旧 audit/rebuild/legacy 回归 | 已验证 |
| 正常失败输入仍可审计、库内独占 | DG Persistence | 坏尾/坏依赖可定位，获取锁失败不继续 | 已验证 |
| 全部 State 与 Journal 持久结构 | DG Persistence + storage | CRC 正确的坏引用、完整 orphan、实际帧边界反例 | 已验证 |
| 取消、I/O、不支持格式分开 | 两库 | 未完成不健康；保留已确认问题 | 已验证 |
| 只读与原 Normal/可写行为 | 两库 | 全部文件名称/字节前后相同；既有回归通过 | 已验证 |
| 薄 CLI | 主线程 | API/JSON 对等，真实子进程退出码与报告 | 已验证 |
| 独立验收 | 新只读 reviewer + 主线程 | 对照合同/源码/反例，无未解决阻塞问题 | 已通过 |

已观察基线：显式源码模式 Release solution build 成功（45 个既有 storage XML 文档警告），Persistence 959 项、Storage 202 项通过。测试记录在忽略的 `.artifacts/db085-audit/baseline`；这些是修改前基线，不是本片完成证据。

## 独立审查与执行证据

新的只读审查者先从首片合同、两仓源码与反例形成结论，未读取旧 DB-085 评审记录。主线程核对改动并统一运行测试。审查实际发现并修复以下问题：

| 反例 | 修复与回归 |
|---|---|
| `InvalidDataException` 并不继承 `IOException`，过滤器漏接会使坏尾直接穿透 API | 明确捕获该类型；坏 Schema/State/Journal 仍返回报告 |
| Unsupported finding 仍可能被最后一行覆盖为 `Completed=true` | 上游统一标记检查未完成；metadata/frame 不支持版本的测试，并验证不生成 rebuild 候选 |
| CRC 正确的短 payload 在内存解码时抛 EOF，被误当作设备 I/O | State/Schema/Graph 的短 payload 分别确认 `InvalidData` |
| 第二次 Journal 库存的取消/I/O 报告被忽略，可跳过图检查并误报健康 | 消费所有库存发现与完成状态；真实 marker 文件占用及取消见证，已查坏 State 的发现仍保留 |
| EventJournal 合法的空 branch head 不符合 DG 合同，但 Audit 漏检 | Normal/Audit 共用非空 head 与物理图成员规则；空 branch 创建及 Move→null 均被 DG 拒绝 |

另验证了有效外层 State 的 opaque body 内嵌一个 CRC 正确、随机读取可成功的 State 帧：把 Parent/external 指向该地址仍被实际物理帧集合拒绝。该见证不以损坏外层帧蒙混通过。测试入口为 [RepositoryAuditTests](../../tests/DurableGraph.Persistence.Tests/RepositoryAuditTests.cs)、[MaintenanceCliTests](../../tests/DurableGraph.Persistence.Tests/MaintenanceCliTests.cs) 和上游 [ToolkitIntegrationTests](../../../atelia-storage/tests/EventJournal.Toolkit.Tests/ToolkitIntegrationTests.cs)。

最终观察：两仓 Release 构建均为 0 error / 0 warning（增量）；storage 完整 **1047** 项通过：Primitives 75、Data 212、RBF 405、SegmentStore 48、EventJournal 174、Toolkit 133。DG 完整 **3014** 项通过：Persistence 997、Storage 202、Serialization 163、Runtime/Generator 1652；其中新增 Audit/CLI **38** 项也单独通过。测试运行器均报告 0 failure / 0 skipped；这不把既有按平台直接返回的测试算作另一平台的证据。依赖产物检查未见生产库反向引用 toolkit exe。独立审查收尾复核及主线程整体验收均无未解决阻塞。

验证使用 Windows 与真实文件/Flush。默认 `F:\TEMP` 的同步落盘过慢，核验本轮进程身份后停止自己的未完成测试树；该轮不计整体通过。最终在任务专用 `C:/Users/gdtut/AppData/Local/Temp/db085-audit-20260929` 重跑，未禁用 Flush、未更改系统环境变量；两仓 .NET 操作串行，最终命令带 `-m:1 -nr:false`。

可复跑命令（分别在对应仓根目录；可在当前 shell 设置 `TEMP` / `TMP` 指向本任务临时目录）：

```powershell
# atelia-storage
dotnet build Atelia.Storage.slnx -c Release -m:1 -nr:false
dotnet test Atelia.Storage.slnx -c Release --no-build --no-restore -m:1 -nr:false
# durable-graph
dotnet build DurableGraph.slnx -c Release -m:1 -nr:false -p:UseStorageSources=true -p:StorageSourceRoot=E:/repos/Atelia-org/atelia-storage
dotnet test DurableGraph.slnx -c Release --no-build --no-restore -m:1 -nr:false -p:UseStorageSources=true -p:StorageSourceRoot=E:/repos/Atelia-org/atelia-storage
```

详细日志留在两仓忽略的工件目录：DG `.artifacts/db085-audit/{build-final.log,focused-final.log,tests-full.log,tests/}`；storage `artifacts/db085-audit-build-final.log` 与 `artifacts/db085-audit-tests/{final.log,final/}`。这不是包交付或断电平台证据；首片按显式源码方式验证，新 API 尚不在原开发 feed/公开 pin 中，未制作或发布新包、未迁移真实数据。旧 storage toolkit 的历史退出码仍将不支持格式归入 2；新 DG CLI 用 3 表示该检查未完成，两者不混用。

结论：本片的问题得到肯定回答，可按联合方案 §10 进入第 2 片只读 Normal 成本实验。该片、S0 恢复及平台保证尚未实施，继续由联合方案维护；本记录不重新排列后续分片。
