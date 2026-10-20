# DurableGraph 质量体检：恢复、缓存隔离与资源寿命

日期：2026-10-20。源码基线：`1beca7cf2477d369010b3577b7a795c41dbc45f1`。读者：承担后续验证与小片实现的 Coding Agent。

## 结论和后续顺序

本包没有找到需要立即修改格式或公开 API 的恢复正确性问题。完整 source 校验、准确 selected S、ReadPair 的保守共享证明、两阶段物化及正常 fault/Dispose 后禁止读缓存的路径均有源码和现存测试支持；本报告不把它们宣布为形式化证明或新运行验收。

有一项已经通过六组计数见证的性能债：同一 batch 容纳许多对象、而 batch 无法进入物理 LRU 时，资格检查和逐对象链读取会反复完整读取/解码该 batch。工作量由对象数乘以 batch 大小决定。零预算与 1024 字节预算下的小图已复现；默认预算下的大 batch 触发路径仅经源码分析，未做大图测量。它不涉及坏数据，也不应通过削弱 source/head 校验解决，详见 R03-01。

计数反例已确认；建议据此决定是否开一个限定的物理读取去重小片。其余性能方向继续受现有“真实消费者或测量触发”的延期纪律约束。

## 覆盖与证据边界

完整阅读根 AGENTS.md、src/PROJECT-STATE.md，并以当前源码为准核对：

- `GraphReader` / `GraphReadContext` 的准备、可达性、对象分配、Hydrate、ReadPair 和 editable delivery。
- `StoredGraphDecoder` / `DecodedRevision` / `NormalizedGraphState` 的全体 source 成员、exact 布局、来源和升级后引用校验。
- `PreparedStateRestoration` 以及 Repository 的 State 驻留槽接入。
- `GraphRecordStore` / `GraphPhysicalReadCache` / `OwnedFrameReadCache` 的驻留计量、超预算准入、链枚举、fault/close 防线和 owned 数据寿命。
- 现存测试中的关键反例：`P0OwnedReadCacheTests`、`MetaBootstrapP0IntegrityTests`、`SharedGraphReaderTests`、`SharedEventHistoryTests`、`DB079GraphRestorationTests`、`DB080PreparedStateReuseTests`、`SplitPreparedStateReuseTests`、`SplitRepositoryReadCacheTests`。

本包子线程未执行 dotnet 命令；主线程统一运行本包计数见证，6/6 通过，结果如下。未做 OS crash/power-loss 实验、包交付资格实验、CLR 绝对堆占用测量，也未全面审查全部生成器与容器升级规则的实现。没有从无关 revision 的 sibling storage checkout 推断底座行为。

## R03-01：batch 准入失败时，单次图读取重复完整解码同一 batch

**分类：已执行计数反例支持的性能债，建议优先评估限定修复。不是已发现的数据正确性漏洞。** 实际业务延迟、分配量与严重程度仍须测量。

### 事实与触发

1. [GraphRecordStore.ReadGraphCore](../../src/DurableGraph.Persistence/GraphRecordStore.cs#L162) 对完整目录中的每一个 `(id, head)` 执行 `FindRecord(ReadBatch(head), id)`。它并未按 head 地址对本次调用去重；同一函数前面已经取得的 `ownedBatch` 也没有在这里直接复用。
2. [ReadBatch](../../src/DurableGraph.Persistence/GraphRecordStore.cs#L234) 缓存未命中时执行 `ReadPayload` 和 `GraphFrameCodec.DecodeBatch`，随后才尝试准入。
3. [OwnedFrameReadCache.Admit](../../src/DurableGraph.Storage/OwnedFrameReadCache.cs#L63) 遇到超预算条目直接 bypass；[GraphPhysicalReadCache.GetBatchCharge](../../src/DurableGraph.Persistence/GraphPhysicalReadCache.cs#L50) 对整个 batch 的全部 records/bodies 计量。因此超预算 batch 每次调用均重新生产。零预算的 Store 根本不创建 LRU，也会重复生产。
4. [ReadObjectChains / ReadObjectChainCore](../../src/DurableGraph.Persistence/GraphRecordStore.cs#L296) 又逐个对象读取链，每条链在 [315 行](../../src/DurableGraph.Persistence/GraphRecordStore.cs#L315) 重新执行 `ReadBatch(address)`；[StoredGraphDecoder.ReadCore](../../src/DurableGraph.Persistence/StoredGraphBodies.cs#L96) 消费的正是这条枚举路径。
5. [DecodeBatch](../../src/DurableGraph.Persistence/GraphFrameCodec.cs#L40) 每次解析并构造 batch 的全部对象记录，不是只解析所需对象。batch 记录数上限为 1,000,000，单帧批量不是仅适用于个位对象的实现。

令 N 个 live ID 的 head 全指向同一个包含 N 条 Base 的 batch，batch 总大小为 B。若无法驻留，单次完整目录资格检查执行 N+1 次完整 batch 生产，随后枚举全部对象链再生产 N 次。只要每对象 body 大小大致固定，B 随 N 增长，总解析工作量即呈二次增长。若只有一个很大的 body 撑破缓存预算，其余小对象也会使这个大 body 被重复读/复制。

这与“8 MiB 只约束驻留 charge，不保证峰值或总堆”的既有限制不同：这里关注的是同一次逻辑操作中的重复生产次数，而不是为缓存新增总堆上限承诺。默认预算足够时，现存 LRU 会掩盖该问题。

### 可执行计数见证

持久见证源码：[RestorationCacheProbe.cs](probes/RestorationCacheProbe.cs)。这是自足的 `Audit03RestorationCacheProbe` xUnit 类，复用测试程序集的 `SharedReadModel.DeleteFixture` 清理自己的随机 temp fixture。此目录不参与产品项目自动编译；见证不修改产品，也不测试 typed DTO 缓存。

主线程已按仓库依赖指南确认准确 feed，并完成 package mode restore。把该源码临时复制为 `tests/DurableGraph.Persistence.Tests/Audit03RestorationCacheProbe.cs` 后运行：

```powershell
dotnet test tests/DurableGraph.Persistence.Tests/DurableGraph.Persistence.Tests.csproj -c Release --no-restore -p:UseStorageSources=false --filter "FullyQualifiedName~Audit03RestorationCacheProbe" --logger "console;verbosity=detailed"
```

完成后移除临时 tests 副本，保留本目录的 `probes/RestorationCacheProbe.cs` 和本报告。命令依赖已 restore 的准确六包候选；换环境必须先按依赖指南重新准备与 restore，不可回退无关存储版本。

主线程已在确认的 package mode 下运行，6/6 通过；实际计数如下，与源码推导相符。运行日志为 `.artifacts/maintenance-20261020/cache-probe.log`，TRX 位于 `.artifacts/maintenance-20261020/cache-probe-results/`。这是小预算准入反例及默认预算下的小图对照；尚未测量默认 8 MiB 下超大 batch 的实际行为成本：

| 对象数 N | 驻留预算 | ReadBusinessGraph 后 BatchDecodes | 再枚举全部链后的累计 BatchDecodes |
|---|---:|---:|---:|
| 16 | 0 / 1024 | 17 | 33 |
| 32 | 0 / 1024 | 33 | 65 |
| 16 | 默认 8 MiB | 1 | 1 |
| 32 | 默认 8 MiB | 1 | 1 |

补充诊断：1024 字节预算下 N=16/32 的累计 AdmissionBypasses 分别为 33/66；零预算下均为 0，因为 Store 没有创建缓存而非执行准入 bypass。不可将 bypass 计数为零解释为未发生重复生产。

见证断言的是当前实现的实际生产次数，不是应永久保留的成本目标。若修复，须改为目标性质的验收断言，不得为了让计数测试保持通过而保留重复读取。计数表不等价于实际磁盘 I/O 次数、耗时或峰值内存。

### 最小解决思路与验收

优先从最小边界开始：`ReadGraphCore` 已有本地 `ownedBatch`，先复用它，并让本次资格检查中相同 head 的物理 batch 只生产一次。仍对每个目录 ID 查找实际 record，仍验证 owned record、prior、source 和角色。不要用“缓存过”替代这些关系检查。

逐对象链解码是另一个需要明确处理的范围。可以比较按 head 分组消费或单次读取操作内按地址复用 owned batch 的方案；后者容易实现，但不应不加说明地把全部历史大 batch 都保留到操作结束。必须记录额外临时驻留的边界和对象链深度的影响。跨调用 LRU 的预算、超大条目 bypass 行为可保持；不需要引入长期 typed DTO cache、reader 证书缓存或全库启动审计。

最小验收：

- N=16/32，在预算足够与 1024、0 三种模式下图内容和链来源完全一致；单次阶段的 batch 完整生产次数不再随“共享同一 head 的 ID 数”线性增加。
- 无缓存/小缓存的新回归必须保持 [坏 prior 与目录重播失败](../../tests/DurableGraph.Persistence.Tests/P0OwnedReadCacheTests.cs#L363)、[完整来源反例](../../tests/DurableGraph.Persistence.Tests/MetaBootstrapP0IntegrityTests.cs#L21) 的拒绝行为。
- operation-local 去重仍在每次新公共读取开始时检查 owner/backing，并在枚举推进时保留关闭/fault 拒绝；不得让一次早先成功读取赋予后续操作存储权威。
- 至少补测一个跨多个 batch、含 retained Delta 与重复 prior 的场景，并观测分配量，再决定是否需要扩大优化范围。

## R03-02：selected Schema 已加载时，origin Schema 仍会重新加载

**分类：低优先成本线索，尚未证明是热点；不建议直接施工。**

[DecodedRevision.ReadStoredGraph](../../src/DurableGraph.Persistence/DecodedRevision.cs#L62) 先加载 selected Schema 并创建准确绑定环境；随后 [StoredGraphDecoder.ReadCore](../../src/DurableGraph.Persistence/StoredGraphBodies.cs#L94) 从空的 `origins` 开始。第一个 Base 的 `sourceSchema` 即使恰好等于 selected snapshot 的地址，也会执行一次 [SchemaCatalogView.Load](../../src/DurableGraph.Persistence/StoredGraphBodies.cs#L106)。操作内其他相同 origin 会命中字典，但已经加载过的 selected view 没有成为该字典的初始成员。

可测量的最小候选是只在 selected snapshot 的 owner/address 与实际 origin 完全相符时复用同一 view；不允许使用 latest 或仅相同 schema key 的 view 解释旧 Base。先给 `SchemaCatalogView.Load` 的实际工作量计数，分别比较首版全 Base 与跨 S retained Base 场景。若物理缓存使其成本可忽略，保留现状即可；不能以去重名义引入跨操作 Schema 权威。此项服从现有[性能延期约定](../../docs/DurableGraph-research-roadmap.md#4-明确延后及重访条件)。

## 已核查且不建议作为“疏漏”重开的边界

| 边界 | 当前实现与具体证据 | 后续修改必须保留 |
|---|---|---|
| 完整 stored source 验证 | `StoredGraphDecoder.ReadCore` 读全部 chains，然后对全部 rows VisitReferences / ValidateLookupKeys；[131 行](../../src/DurableGraph.Persistence/StoredGraphBodies.cs#L131) 的目录校验发生在 Normalize 之前 | 不得只恢复 reachable rows 后声称完成 source 验证；坏不可达行也须拒绝 |
| 完整 current source 验证 | [NormalizedGraphState.Create](../../src/DurableGraph.Persistence/NormalizedGraphState.cs#L156) 先 Normalize 每行，后对全部 current 行验证引用和 Dictionary lookup；[RequireStoredSource](../../src/DurableGraph.Persistence/NormalizedGraphState.cs#L135) 复核完整成员与准确 head | Upgrade 切断引用的对象仍属 source baseline；不可被 reachability 优化跳过 |
| 准确 selected S | [SchemaViewBindingEnvironment.RequireMatches](../../src/DurableGraph.Persistence/DecodedRevision.cs#L90) 绑定具体 Store、FrozenConfiguration 和 schemaRoot；Pair 只有准确同一 reader 环境才尝试共享 | 不可用兼容的新 S 或相同 schema key 代替准确 pin；测试见 SplitRepositoryReadCacheTests |
| Pair 共享证明 | [FindSharedClosure](../../src/DurableGraph.Persistence/GraphReader.cs#L201) 核实际 head、同模型/current 布局、无 rewrite 和完整状态 proof，然后沿两个视图的引用求闭包；无 proof 保守不共享，比较异常传播 | 保留 container/inline 引用遍历与反向闭包剔除；不能以相同 head 单独授予共享 |
| 两阶段物化 | [MaterializeCore](../../src/DurableGraph.Persistence/GraphReader.cs#L90) 两图均分配并建表后才 Hydrate；Checkpoint 和 Pair 共享同一个操作内 mutable 实例 guard，即使两边使用不同 models context | 第二图准备/分配失败时不得先 Hydrate 第一图；DB079GraphRestorationTests 有对应反例 |
| 手写 singleton allocator | [GraphReadContext](../../src/DurableGraph.Persistence/GraphReadContext.cs#L7) 只记本次操作的实例；跨公共调用不会登记已交付对象。`StateModelBinding` [提供者合同](../../src/DurableGraph/Runtime/Binding/StateModelBinding.cs#L11) 明确禁止修改已交付图 | 这是 provider 信任边界，不是可据此要求新增全库强引用/弱引用表的缺陷。生成分配器按正常路径创建新对象；操作内碰撞已有回归 |
| State 驻留槽资格 | [PreparedStateRestoration.ValidateStoredSource](../../src/DurableGraph.Persistence/PreparedStateRestoration.cs#L26) 核准确 State/root/S、环境、完整 source 和 requirement set；[RestoreCheckout](../../src/DurableGraph.Persistence/Repository.MetaBootstrap.cs#L104) 在首次完整成功后才交付 candidate，无 State 绕槽 | 不得把热 Capture 当 reader 资格证书。历史 S requirement 继续隔离；失败不得替换原槽 |
| 故障与关闭后的缓存 | [ReadBatch 等](../../src/DurableGraph.Persistence/GraphRecordStore.cs#L234) 在查缓存前 EnsureUsable；[EnsureUsable/Dispose](../../src/DurableGraph.Persistence/GraphRecordStore.cs#L419) 清驻留；`RepositoryResources.MarkFaulted` Dispose Store；Repository EndOperation/Dispose 清 State 槽 | 保留枚举 next 处检查。见 P0OwnedReadCacheTests 的五种资源停止及 bulk iterator 反例 |
| owned 值与 backing lease | [ReadPayload](../../src/DurableGraph.Persistence/GraphRecordStore.cs#L402) 在 `using FrameRead` 内复制 bytes，缓存持有 owned 解码值；外部保留的 owned 内容在 Store Dispose 后仍可读 | 允许内容继续存活不等于允许关闭后使用 Store；不要把物理 lease 驻留到读缓存 |
| charge 与总内存 | OwnedFrameReadCache 统一 frame/map LRU，并在准入前检查合并 charge；已有 combined-components、oversize、zero 和 role 测试 | charge 不是绝对堆占用或峰值；不得将已明确的限制重新报告为“违反内存上限” |

## 明确不纳入新工单

- 正常 incomplete-tail 恢复继续交给 FrameStore/HeadStore；本包不建议 strict preflight、预打开全部 ref/tag 或全库 reconfirmation。
- DB-081 热保存材料不具备 reader 资格的否定结论继续有效；DB-082 不可变叶复用候选默认关闭，均不是遗漏接线。
- map 历史回溯、完整 Schema 检查、全量 Base 准备、当前值比较及默认缓存容量均已有基于实测的重访条件。本报告仅给 R03-01 提供一个比“也许很慢”更具体的计数切入，不据此启动通用缓存重构。
- 没有为了 SOLID 拆分 GraphReader、添加恢复抽象层或新增 framework；当前职责边界能从消费者和不变量解释，抽象数量本身不是质量指标。

## 本包改动与验证入口

新增本报告，以及持久计数见证 `maintenance/20261020/probes/RestorationCacheProbe.cs`；`.artifacts/maintenance-20261020/RestorationCacheProbe.cs` 仅为准备阶段的副本。未改产品、持久格式、公开 API、共享状态文档或既有测试。

主线程合并复核时建议运行 Persistence.Tests 中 `Audit03RestorationCacheProbe`、`P0OwnedReadCacheTests`、`MetaBootstrapP0IntegrityTests`、`SharedGraphReaderTests`、`DB079GraphRestorationTests`、`SplitPreparedStateReuseTests`、`SplitRepositoryReadCacheTests`。如果只是接纳体检文档，无需据此宣称重新完成包模式或 crash 恢复资格。
