# DB-086：新存储栈实施与验收

> 施工：2026-10-11；验收：2026-10-12。状态：**R0–R5 已实施并验收**。
> 合同：[重构方案](0086-new-storage-refactoring-plan.md)；取舍：[简化评审](0086-new-storage-simplification-review.md)。

本片的问题是：删除旧存储栈、DG binary cursor 与全库打开屏障后，真实生成模型能否保留保存、冷重开、模型升级、历史选点和故障结果。
验收边界只包括新建库；不读取、迁移或原位改写旧 DG 存档。公开发布不属于本片。

## 实施合同

- `GraphResources` 是公开的具体资源 owner，按 FS→VS 顺序打开 `data/` 与 `versions/`，由两底座取得锁；Schema 与 State facade 借入同一资源。
- VS 创建时预留编号 1，唯一仓库 ref 为 1。根头为 `DGR1` 与版本字节 1，body 使用上游 RootMap；`schema` 与 `branch/名称` 是两个键域。
- Schema 使用完整目录快照，SCB1 v2 的目录语法从空登记集合读取，表示 ID 从 2 连续分配。只认仓库根选中的快照，不认领完整 orphan。
- StateRevision 使用 wire v4，所有 DG links 固定为上游地址的 12B 编码。图帧的 Payload 是 Revision，TailMeta 为 `DGH2`、版本 1、角色、可选业务 Parent、RootId。
- 业务 Parent 与 Revision 的最近 State Parent 保留为两个关系；所选图准备时检查其对应。VS 发布顺序不决定业务祖先。
- tag 的 payload 恰为一个 12B 图帧地址，创建和解析由 VS 承担。Checkpoint 仍绑定本次打开 owner。
- 写入直接使用 Bare 的最短编码；读取接受上游合法有界冗余表示。char 用 CharLE；Guid、decimal 与 temporal 的少量组合保留在 BclValueCodec。
- 对象 B/D/H 按实际对象记录计量，固定地址消除了旧文件距离上界。Schema、图元数据与物理 RBF 开销不计入 H。
- 打开读取必要根和所选 Schema，保留上游正常恢复及控制文件的惰性首次打开；不执行启动全图审计或全库重新确认。

根含格式头在内最多 1MiB；Schema 与 Revision 使用上游实际单帧容量。历史元数据导航仍执行整帧 CRC 读取。
旧生成 body ABI 不保留适配器，模型程序集需要重编译；新库中的模型 V1→V2 历史升级保持。

## 验收记录

最终源码模式 Release 整库构建零警告、零错误；四个测试程序集共 2798 项全部通过、零跳过：Serialization 133、Storage 132、Persistence 886、模型/Generator 1647。此前的旧栈基线不作为本片验收数。

其中 LifecycleCleanup 5 项验证关闭 VS/FS 都失败时，仍尝试全部资源，并保留打开主错误与清理证据。Repository.Open 的模型快照主错与清理错组合仅作源码复核，没有新增生产注入接口。

真实包消费共 24 条 runner 通过。23 条领域消费者使用独立 NuGet.Config、缓存和新建库，消费同一候选
`G=0.0.0-tags-e2e.20261011161544.131286`；四个 DG nuspec 均指向产品提交
`091064c70ee607481775c814ba36a45c297e8357`。基础打包/history 工具链另用唯一候选
`0.0.0-e2e.20261011163848.145254`，验证资产、诊断、历史发布与冲突拒绝；两套包均使用下面同一 S，未公开发布。

| 真实包路径 | 通过的 runner 与核心见证 |
|---|---|
| 低层与 tag | StateStore、Tag：raw exact 解码、真实 Delta、模型升级、对象身份/环、Event-first、tag 跨进程定位 |
| 生成代码与模型历史 | CrossAssembly、InlineLibrary、InheritanceLibrary、Generic、InlineStruct、Record、RecordClass、ValueUpgrade、HistoryCapability、ImmutableLeaf：跨库 ABI、accepted history、explicit Upgrade、读能力与真实 Base/Delta |
| 容器 | Array、List、Dictionary、CompositeDictionary、Nullable、Enum：真实稀疏 Delta、语义 key 寻址、身份与历史元素升级 |
| 标量 | BclScalar、TemporalScalar：Guid/decimal/TimeSpan 与日期、时刻、完整 offset |
| 业务调用 | EventHistory、EventHistoryRecovery、ReadmeQuickStart：自由 E/S、Move/Fork、固定查询、mutable 隔离、冷重开及 README 原文 |
| 打包工具链 | Run-Probe：十包闭包、analyzer/build assets、缺历史/冲突/遗留 history 输入拒绝 |

固定 12B prior 改变了小对象的成本选择。Generic/ValueUpgrade 的 V1/V2、EventHistory 的 Alice 等实际选择 Base，
探针继续检查真实目标 ObjectId、Parent、未改 heads、membership 与 exact 历史；没有强迫产品产生不经济 Delta。
专门 Delta 见证保留于 StateStore、较大 V3 对象、容器和 inline/record fixture；既有 V1 history 不改写。

存储候选包由上游干净提交 `974fd47abb73792f9d350af87ab91a53848952eb` 的 `eng/Pack.ps1` 生成，
版本 `0.1.3-dev.20261011.g974fd47`；六包的 manifest、nuspec、来源与 hash 已核对，未公开发布。
本地依赖和源码联调入口见[依赖指南](../storage-dependency.md)，本机日志保存在忽略的 `.artifacts/db086/`。

六个门禁负例也按预期失败：非法 source mode、缺源码根、相对源码根、错误 HEAD、source-mode pack、G 与 S 相同。
活动 `src/tests/eng` 与包消费者没有旧存储栈或 DG cursor 的接线；必要 Rbf 容量常量仍使用底座的真实上限。

底座 PublicationCrashProbe 五个进程停止阶段通过：空应用根、数据追加未确认、数据确认未发布、发布已确认，以及控制文件不完整尾。
使用真实 FS/VS 和小轮转阈值，独立进程 kill 后冷开；VS 惰性 ReadRef 恢复不完整尾并保留最后完整选择。
这是底座见证，DG 发布内部窗口由产品故障回归承担，不扩大为 OS crash/power-loss 保证。

### 重跑入口

- 源码 restore/build/test 按[依赖指南](../storage-dependency.md#显式源码联调)执行，指定准确 `StorageSourceRoot`。
- 包 runner 见[包实验导航](../../experiments/PackageConsumerProbe/README.md)，已有十包 feed 时传 `-PackageSource <feed> -Version <G>`。
  本机领域候选 feed 在 `experiments/PackageConsumerProbe/obj/tags-20261011161544-131286-7771ff12/feed`；重新准备时另取唯一 G。
- 基础 `Run-Probe.ps1` 自行产生唯一 G，通过 `DG_STORAGE_PACKAGE_SOURCE` 指定准确六包 feed。
- [PublicationCrashProbe](../../experiments/PublicationCrashProbe/README.md) 是底座进程停止见证：先以同一 source mode 构建其项目，再运行 runner 的 `-NoBuild`。

源码最终日志为 `source-{restore,build,test}-release-final.log`；领域验收为 `package-final-*`、`package-coverage-*` 与
失败后重跑的 `package-recheck-*`，低层/tag 为 `package-state-store-qualified.log`、`package-tag-final.log`，
基础工具为 `package-tooling-final.log`。应以各路径最终通过日志为准，早期失败日志保留排错事实。
底座进程日志为 `publication-process-final.log`；集成 diff 与仓内相对链接/anchor 检查通过。

## 已发现并修正的集成问题

独立复核发现，DG owner fault 而 FS 仍健康时，借出的 State facade 可能继续命中缓存。
现在组合 owner fault 会立即失效其 State facade；实际 backing 资格检查也触发同样的失效，已交付 owned 值仍可读。

普通根容量预检以及 VS 的确定 pre-I/O Result 拒绝与未知发布分开处理。
完整图或 Schema orphan 可以保留；普通拒绝不因此污染健康 owner。Unknown、Confirmed 后交付失败与原始证据仍各自保留。

## 边界

不承诺全库健康、旧调用曾返回 Confirmed、OS crash/power loss 或外部副作用 exactly-once。
没有进行 256MiB Schema 极限压力测试或性能基准；增量目录、metadata-only I/O 和大 branch 目录的替代结构仍按真实瓶颈重访。
