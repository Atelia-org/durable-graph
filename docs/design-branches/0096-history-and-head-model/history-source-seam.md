# DB-096：历史来源材料的最小接缝

> 2026-10-19，源码调查与下一片建议，未实施。DG 起点 `35ae718`；产品仍固定 Storage `731eef7`。上游主工作树仍为 `f8868b57e8f012745413bb38a83d67f1dba29f26`，干净；本轮未发现 `ReadHeadOrigin` 的新交付。本文细化[Branch 纵切](branch-vertical-design.md)，不替代其需求、查询范围和登记协议。

## 1. 结论：复用已有 snapshot，不另建证明体系

推荐由 Repository 取得准确历史关系，**把已有 `StoredGraphRevision` 保留到解码、归一化和热安装之后**。它已经携带准确图地址、`SourceOwner`、`Metadata`、`SourceGraph` 与完整 heads，足以成为内部来源材料；不必再给同一路径添加一套 proof/token/历史解析器接口。

`StoredGraphRevision` 只说明被选中图的内容资格。Branch、Commit、当前写入目标和 HS 发布资格仍由 Repository 持有；不必让物理图层引用 HeadStore，也不需要将 CommitId 放进每层 DTO。空 fork 的写入 Branch 与继承 Commit 的真实 owner 继续分开。

这不是“缓存命中即可跳过检查”：进入保存、重用 prepared State、准备实际安装时仍按当前约定重读实际图及 source/Schema/membership；只复用“该图在准确领域历史中的前源 State 是谁”这一不可变关系。资源关闭/fault 后，读取内容 DTO 仍可用，重新取得保存资格则必须失败。

保留 snapshot 会延长其中完整 heads 的驻留；`DecodedRevision` 当前已经复制 `ObjectHeads`，不能因此宣称总内存下降。先复用实际 snapshot 引用，不再生成第三份 heads 副本；是否去除已有重复 map 在测量及调用方核对后决定，不把来源重构扩成缓存优化。

## 2. 当前实际调用链及遗漏风险

| 入口或阶段 | 当前调用链 | 必须改动的接点 |
|---|---|---|
| 冷 State/Event 读取 | `DecodedRevision.ReadStoredGraph(address)` → `store.ReadGraph(address)`；再 `StoredGraphDecoder.Read(address)` → `ReadObjectChains(address)` → 第二次 `ReadGraph` | 首次产生 checked snapshot；decoder 接收同一 snapshot，避免只因参数退化为地址而重新解释历史 |
| 冷可编辑恢复 | `Repository.RestoreCheckout` → 最近 State → decoded → `NormalizedGraphState.Create` → `BusinessPersistenceAdapter.FromLoaded` → `RequireStoredSource` | decoded 与 normalized 都保留同一来源 snapshot；最后一次复核不能重新猜前源 |
| 热 State 槽命中 | `PreparedStateRestoration.ValidateStoredSource` → `Selection.Normalized.RequireStoredSource` | 保 Schema 证书、根与实际图复核，沿用 normalized 内来源材料；槽命中不授予当前 Branch 写权限 |
| 准备保存 | `BusinessPersistenceAdapter.PrepareOwned` → `baseline.RequireStoredSource`；State 候选又经 `FromCapturedCandidate` → `RequireStoredSource` | 已接受 baseline 的来源材料保持；是否消除第二次物理复核另行用证据决定，不在本片顺带优化 |
| 写后、发布前安装 | `PreparedBusinessWrite.PrepareInstall` → `NormalizedGraphState.PrepareGraphInstallation` → `store.ReadGraph(graph.Address)` | `AppendBusiness` 返回的真实 snapshot 带确定 expected source；安装仍核实际 bytes、heads、records、Schema 和源 baseline |
| 发布后继续保存 | `CompleteInstallation` 创建新的 normalized；`BusinessPersistenceAdapter.Install` 更新 Source | 新 normalized 必须保留这次实际 snapshot，不能只留下 address/heads 而在下一次保存丢失来源资格 |
| 独立底层入口 | `BusinessPersistenceAdapter.LoadState/ReadRoot`、`ReadObjectChain` | 也须使用 checked snapshot；不能留下业务 address-only 旁路 |
| 保存准入/导航 | `RequireCheckoutSource` → `GetStateHead`；`PrepareSaveCore` → `ResolveContentSource`；`BranchSaveSession` 历史构造；`Repository` 的 Parent 游走 | 改为同 guard 下检查准确 expected tip，并使用历史解析结果或已合格 live baseline |

对应源码：[图读取](../../../src/DurableGraph.Persistence/GraphRecordStore.cs)、[decoded](../../../src/DurableGraph.Persistence/DecodedRevision.cs)、[decoder](../../../src/DurableGraph.Persistence/StoredGraphBodies.cs)、[normalized](../../../src/DurableGraph.Persistence/NormalizedGraphState.cs)、[adapter](../../../src/DurableGraph.Persistence/BusinessPersistenceAdapter.cs)、[prepared State](../../../src/DurableGraph.Persistence/PreparedStateRestoration.cs)、[资源保存](../../../src/DurableGraph.Persistence/RepositoryResources.MetaBootstrap.cs)、[恢复入口](../../../src/DurableGraph.Persistence/Repository.MetaBootstrap.cs)。

当前 `DecodedGraphRows.ToDecodedRevision` 和 synthetic DTO 构造刻意不赋予保存来源；改造后仍不由这些入口自动填入 `SourceSnapshot`。测试能构造 internal 值不构成产品公开 capability；关键是产品调用图中，业务 expected source 的生产者只有受约束的 Repository 操作。

## 3. 建议阶段签名与资料流

以下是**阶段 B 切换历史权威时**的内部形状说明，不冻结最终名称。先行阶段 A 只调整 snapshot 的传递与保留，仍由现行 `ReadGraph/Append` 取得资格，不新增 expected-source 入口。B 的 `ReadBusinessGraph` 第二参不是应用可填值，也不能由 decoder 从 batch 自报 source 推导。

```csharp
// GraphRecordStore：业务历史先由 Repository 资格化。
StoredGraphRevision ReadBusinessGraph(FrameAddress graph, FrameAddress? expectedStateSource);
void ValidateBusinessMetadata(FrameAddress graph, FrameAddress? expectedStateSource);
StoredGraphRevision ReadMetaGraph(FrameAddress graph);
StoredGraphRevision Revalidate(StoredGraphRevision snapshot);

// 从已有实际 snapshot 进入解码，不再重新以裸地址选历史。
DecodedRevision ReadStoredGraph(GraphRecordStore store, StoredGraphRevision source,
    FrozenConfiguration configuration, SchemaViewBindingEnvironment? environment = null);
DecodedGraphRows Read(GraphRecordStore store, StoredGraphRevision source, /* models/view */);
IEnumerable<ObjectVersionChain> ReadObjectChains(StoredGraphRevision source);

// 此值保留在 DecodedRevision 和 NormalizedGraphState 内部。
StoredGraphRevision? SourceSnapshot { get; }

// expected source 取自受 guard 保护的已接受 tip/baseline，不以 write 自证。
StoredGraphRevision AppendBusiness(StoredGraphKind kind, FrameAddress? expectedStateSource,
    FrameAddress schemaRoot, PreparedGraphWrite write);
StoredGraphRevision AppendMeta(FrameAddress? previousMeta, PreparedGraphWrite write);
```

初次 `ReadBusinessGraph` 检查业务 Kind、源为 State/非自身，以及现有完整图关系；Repository 保证源确为严格祖先中的最近 State，null 必须来自到根证明。`ValidateBusinessMetadata` 仅承接当前 tip Event 的轻量 source/batch/directory 关系校验，不隐含完整目录/对象读取。

`Revalidate` 首先检查同 owner 和 owner 可用性，用 snapshot 的准确地址与 expected source 重新读取，核 metadata/source/heads 一致；不通过最新 Catalog 改写历史 Schema。下层物理缓存仍遵守现有 append-only 合同，本片不承诺每次检查强制从磁盘 bypass cache。

`AppendBusiness` 在追加前核 `write.SourceGraph == expectedStateSource`，追加后沿同一 expected source 验证真实图；尚未发布没有 CommitId。`PrepareGraphInstallation` 仍比较 actual source 与候选 baseline 地址、实际 records/heads/Schema，安装后的 normalized 保留 actual snapshot。发布失败不安装；Confirmed 后失败沿[组内核](group-kernel-design.md)处理。

冷恢复在 HS visitor 中只收集准确 Commit→图描述和 Kind，结束 visitor 后完成小图/对象内容验证与物化。目标 State 的严格前源也要取得；tip Event 另用最近 State 做轻量校验。必须保留现有[纵切 §4](branch-vertical-design.md#4-删除业务-parent-后如何保留恢复证明)的三个选择边界与无 State 到根判据。

## 4. 为什么不注入历史 source 解析器

| 形状 | 优点 | 当前代价与缺口 |
|---|---|---|
| 显式 snapshot，Repository 先决定来源 | 可沿既有 SourceOwner/SourceGraph 结构接线；冷/热/未发布都能表达；物理层无需知道 Commit；同一操作只导航一次 | 少量内部参数和两个来源保留字段需要调整；必须补齐所有 bare-address 旁路 |
| 给 GraphRecordStore 注入 `ResolveSource(graphAddress)` | 表面上保留大多数 address-only 签名 | Head 历史按 Commit 定位，不按图地址定位；需要反查、临时注册表或额外参数。未发布图还没有 Entry，必须另加候选分支。重新读取 baseline/decoder 会重复导航，callback 内易触发 HS 禁止递归历史的边界 |
| 注入 `ResolveSource(CommitId)` | 准确历史起点可得 | CommitId 仍须贯穿调用，未发布图仍需另分路；相较显式 snapshot 没减少资料流，却将 HS 依赖扩散到图层 |

因此不引入 owner 全局 Graph→Commit 索引、`AsyncLocal` 查询上下文或 callback 中的隐式当前 Commit。只有发现真实调用方必须从图地址恢复业务历史、且不能持有 Entry 起点时，才重新审查反查需求；当前 API 没有这个要求。

## 5. 物理 Delta 与 Meta 的边界

源码核对发现无需为递归物理 source 构造更多历史证明：

- `GraphRecordStore.ReadObjectChainCore` 沿 `record.PriorAddress` 读取 batch；Delta 用 `batch.SourceGraph` 的 **metadata + directory** 核准确 prior，不调用该 source 的 `ReadGraph`，也不沿它的业务 Parent。
- `ReadDirectoryChain` 沿 directory prior，核 source Kind 为 State/Meta 且 prior 恰等于 source.DirectoryRoot。到 Base 即停止，不证明被 Base 丢弃的更旧链。
- 因此替换的是所选图的来源权威，不是将每个物理 Delta source 递归审计成领域历史。保持上述局部关系检查，避免引入扫描整条领域历史的新成本。
- `SchemaCatalogView.Load` → `StoredGraphDecoder.Read` 是 Meta 解码路径；它仍从 Meta graph 自带的前 Meta 字段取得来源。业务字段删除时应区分 Meta 编码，不能把 Meta 当“没有前源”。
- 业务图的 selected Schema 与对象 Base 所属 batch 的 origin Schema 是两项不同事实。decoder 按每条链的 Base.SchemaRoot 加载相应 Meta，并以 selected view 检查完整布局；任何 snapshot 简化都不能把 origin 换成 latest/selected。

相关源码：[数据结构](../../../src/DurableGraph.Persistence/GraphPersistenceContracts.cs)、[编码](../../../src/DurableGraph.Persistence/GraphFrameCodec.cs)、[Meta 入口](../../../src/DurableGraph.Persistence/SchemaCatalogView.cs)。

## 6. 可执行顺序：先接缝，再一次切换历史权威

**A：同格式接缝整理，建议作为下一轮第一个真实实施步。** 在现行产品路径中保留 snapshot，改 decoder/normalized/安装参数；当前 `ReadGraph/Append` 与 Parent 仍是唯一来源生产者，不新增 expected-source 读取入口。其规模比直接切换 Branch/Commit 小，能独立检验最易漏掉的热安装/再保存传递。它不提供新 Branch API、不增加第二格式或第二 Parent，也不声称已验证 HS 来源。

A 的合入门槛：

- 冷读取→同 owner prepared State 命中→State/Event→再次 State 保存，来源与准确 Schema 保持；保真实 Delta 与 rewrite 断言。
- 错 owner、synthetic DTO fixture、关闭/fault 后的来源再用均拒绝保存；完整公开 DTO 内容仍可在关闭 Store 后读取。
- 用窄计数证明同一次 `ReadStoredGraph` 进入 decoder 时不再重复完整 `ReadGraph`；不能只靠冷 cache 的 graph decode 次数，因为缓存会隐藏重复校验。
- 温读/保存入口仍执行实际图、source/Schema/membership 验证，现有错 source/prior/Schema 反例保持。根构建及相关回归通过后可单独提交。

**B：单业务成员真实纵切，整体切换历史权威。** 首先资格化新 Storage 依赖：精确选择 revision、适配 `LocalId` 等不兼容调用并运行根构建/相关回归。源模式和独立普通包资格分开记录；既有独立 HS Probe 不替代此步骤，同一测试进程不混两套 Storage 二进制。

随后在隔离实施分支闭合 D 登记 HeadId→label、真实业务 Head Entry→graph、准确 Commit 入口、Repository 签发 expected source、业务 Parent 移除和必要根/图格式拒旧。旧 name→graph 目录及旧 Parent 导航不作为备用产品路径。先通过内部测试宿主驱动同一实际生产内核，覆盖创建、追加、旧点 fork、冷恢复和再次保存；全部 API 的命名/便利重载、分页组合、BLOB 和联合提交外观可后置。不能复制 GraphRecordStore/decoder，或用包装将旧 Parent 图当作已删除 Parent 的实现。

若 B 的一次切换规模仍过大，可在隔离实现分支保留工作提交，直到最小纵切与必要现存调用迁移一起通过再合入；不在主线公开两种 Repository 布局。A 的通过仅证明资料贯穿，没有替代 B 的正确性验收。

B 的最小判据：真实 `S0,E1,S1,E2`，父线再推进 E3 后从准确旧点 **E2** fork，关闭所有 resources/caches 重开；恢复 S1 时证明前源 S0；子线 Event 后 State 使用正确 baseline，State 再次变更确实写 Delta，父线继续不改变子线历史。补一个只有 S0 的根证明和 Event-only 到根证明。反例至少包括“E2 图/batch/directory 一致自报 S0、HS 前源实际 S1”、错误 prior、错误 selected/origin Schema、snapshot 跨 owner/关闭后重用、已安装 State 下次保存丢失来源材料。登记中断与真实多成员 CU 在该闭环之后接入。

本调查未改生产源码、依赖或运行新历史测试；没有将签名草图当成实现事实。独立复核确认 A/B 边界、冷热来源接点及物理 Delta 检查范围，未发现阻断；同轮另行运行的混组原语资格见[组内核 §8](group-kernel-design.md#8-真实混组发布与冷续写见证)，不作为本方案已实施的证据。下一步优先 A，之后 B；定向 origin 请求仍可独立推动，不阻断来源材料接线。
