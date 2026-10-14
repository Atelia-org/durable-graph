> 历史快照：2026-09-06，来源提交 `54a33df`。正文按原稿保存，仅重定位 Markdown 相对链接。
> 文中的当前、下一步和状态均属于当时语境，不作为当前规格或排期。
> 续工从 [产品工作集](../../../src/PROJECT-STATE.md) 开始；本文件冻结，仅修复引用或史实错误。

# DurableGraph 后续研究与实现路线

> 状态：Living Roadmap  
> 更新日期：2026-09-06
> 用途：记录当前证据支持的研究顺序、每个切片的问题和可执行闸门。  
> 边界：本文不是当前实现事实、冻结 API 或持久格式规格；源码、测试和可复现输出优先，已完成实验的事实记录在 `DurableGraph-lab-notebook.md`。

## 1. 当前出发点

历史验证仍成立，但部分已升级为当前产品切片：

- EXP-011/012/013/014 仍作为历史 fixture-only 证据，持续支撑后续状态律与重建思路。
- DB-022：保存输入切到版本化 DTO（readonly Vn + current Capture + typed Write/ReadVn），并移除旧 direct read/write 字段体路径。
- DB-023：将 bool、byte、sbyte、short、ushort、int、uint、long、ulong、char、Half、float、double
  共 13 种 CLR primitive 贯通 DTO 与历史通道；string 则按引用 ID 进入同一布局。
- DB-024：引用 capture 进入闭合候选图，`CaptureSession`/`CaptureContext`/`CapturedGraph` 已成形；
  root 通过 typed 适配器登记，string 字段写 `uint` 引用 ID，string 本体作为独立内存对象条目捕获，候选可 `Accept`/`Discard`。
- DB-025/026：string 解码/引用校验，以及同 Revision Frame raw Base 内容、wire v2 与 exact-head 文件重开读取已通过验收。

当前仍未实现：

- 对象 Delta/prior 链、TypeCodec/Schema 的持久绑定与 commit/publication/recovery 闭环；
- 通用自定义 durable 引用的递归 Capture、共享/循环/reachability，以及完整领域 Restore；
- reopen 后的 ObjectId 重新绑定、复杂值类型、集合与回收策略。

因此，后续路线不能把 target design 中的完整系统描述成已经存在，也不应让尚无消费者的格式、缓存或兼容层先塑造核心语义。

## 2. 当前研究方向

### 2.1 当前产品进展：DB-026 raw Base 内容已接入

DB-024 的 string 引用 capture 与内存候选已封闭，下一步保持切片独立：

- [DB-025](../../design-branches/0025-string-object-decoding-slice/0025-string-object-decoding-slice.md) 已实现并通过验收：
  保留 ID DTO，以 string 解码表 + SG 各版引用校验形成 typed 字节见证，领域 Restore 另片；
- 用户已选择空串两端统一 string.Empty，非空保留身份；不再要求独立空串分配，不顺带实现通用循环图；
- [DB-026](../../design-branches/0026-raw-base-object-content-slice/README.md) 已实现并通过验收：
  同 Revision Frame 保存完整 local Base records，再由 exact Revision/ObjectId 跨文件重开读取。
  membership-only 模型已迁至完整内容与 wire v2，保留 ObjectHeadMap Base/Delta，
  只有 ID 的 ObjectVersion Delta 占位已移除，后续真实 Delta/prior 链以代码 TODO 保留。
- 之后由真实内容消费者收敛对象 Delta/prior 链、比较估算与保存视图，再连接策略；
  current 领域 Restore、Durable 互引和 struct 保持独立候选，不提前冻结全部先后次序。

MultiSegment probe 已完成其文件级 address/rollover 风险验证，产品 Storage 已吸收相应机制；它继续作为证据来源，
不再充当当前产品路线图。

[`DB-014`](../../design-branches/0014-multi-segment-backward-file-distance/README.md) 与
[`MultiSegmentStateStoreProbe/PROJECT-STATE.md`（归档）](../../../experiments/ARCHIVE.md#multi-segment "原路径：experiments/MultiSegmentStateStoreProbe/PROJECT-STATE.md") 记录文件层证据边界；
`state-store-base-design.md`、`state-store-base-derived.md`、`state-store-addressing-design.md`
与 DB-007/011 仅保留 TwoLeg 技术储备和可复用局部结论，不作为当前 API 形状先行事实。

### 2.2 保留的 logical graph 研究线

R1/R2 已回答 current graph capture/delta，R3a/R3b 已闭合 historical records → normalized baseline → current CLR root。
R4 的 logical StateMap、record reuse 与 repeated delta apply 仍是未完成的历史研究项；它没有被否定，也不是当前 string 小片的前置条件。

产品已有 primitive、DTO body、string 引用校验及 raw Base Revision framing；
持久对象类型头、一般引用目标解析、canonical graph order 和类型相关的 malformed payload 合同仍待后续收敛。

### 2.3 历史版本在读取边界归一化

磁盘或逻辑 Store 中的 historical payload 必须先经过：

```text
exact Schema lookup and shape validation
    -> exact-version typed decode
    -> unique adjacent typed upgrade chain
    -> current-version typed Snapshot
```

进入内存 baseline 后，每个节点只暴露该 durable type 的 current Snapshot。Graph Delta comparer 不再知道 historical Snapshot 类型或版本分派。

若节点由历史版本升级而来，其 baseline entry 带 `RequiresRewrite`。只要它在下一次 Save 中仍可达，就必须写入完整 current Snapshot；若已不可达，则只从新图中移除，不为升级义务而保活。

### 2.4 逻辑图展平，引用只保存 DurableId

baseline 的逻辑形状是：

```text
RootId + Map<DurableId, BaselineEntry>
```

Snapshot 中的 durable reference slot 保存目标 `DurableId`，不嵌套另一份 Snapshot。这样共享引用只出现一次，循环图有限可表示，引用 equality 也只比较 ID。

Map 使用哈希表、排序表还是其他索引属于实现与测量问题；确定性测试、诊断和未来编码在输出边界显式按 ID 排序，不依赖容器枚举顺序。

### 2.5 Authority 与派生 baseline 分离

`NormalizedBaselineGraph` 是由已发布状态产生的、detached、可丢弃重建的比较投影，不是 authority。

未来持久集成的边界应是：

```text
Exact published head
    + authoritative StateMap / object records
    + matching NormalizedBaselineGraph
```

unchanged object 的旧 record address 由 authoritative StateMap 提供；不要复制进每个 baseline entry。当前尚无持久 head，首个探针不伪造 revision、record address 或 source token。

## 3. 主线依赖顺序

```text
R1 Graph Delta semantic probe (Concluded)
    -> R2 generated graph operations (Concluded)
    -> R3a normalized flat-graph Load (Concluded)
    -> R3b two-pass CLR hydrate (Concluded)

Current persistence research track:
    M1 BackwardFileDistance and canonical filename (initial slice concluded)
        -> M2 in-memory append-only Segment/Frame store and soft rollover
        -> M3 cross-file OVD reconstruction and reopen/fail-close
        -> M4 recovery-closure inspection before any compaction/GC

Parked technical reserve:
    S1 TwoLegRotationProbe (paused; executable subsolution and research tag retained)

Product vertical sequence retained:
    R4 in-memory StateMap and repeated logical delta apply
        -> R5 binary codec for the proven logical IR
        -> R6 persistent publication and recovery
        -> R7 measurement-driven optimizations
```

每一步只提升已经由前一步证明的概念。阶段编号表示依赖顺序，不是发布日期承诺。

## 4. 分阶段实验

### R1：手写单类型 Graph Delta 语义探针

状态：Concluded（EXP-011）。实现与 21 个聚焦测试位于 `tests/DurableGraph.Tests/GraphDeltaProbe*.cs`；它们只构成 fixture evidence，不是 runtime product API。

问题：最小的 identity-aware、cycle-safe、reachability-based diff 是否成立？

最小切片：

- 一个 sealed、自引用的 fixture-only `ProbeNode`；
- 一个临时正整数 `ProbeId`，不冻结正式 `DurableId`；
- scalar field、两个 nullable durable references 和一个 transient field；
- flat current-Snapshot baseline 与 `RequiresRewrite`；
- iterative traversal、whole-object Upserts、Unreachable 和 resulting RootId；
- test-only `AcceptForAssertion`。

可执行闸门：

```text
AcceptForAssertion(B, Diff(B, current))
    == CaptureCleanForAssertion(current)
```

并覆盖 no-op、leaf locality、共享引用、循环、duplicate ID、root replacement、升级后强制重写、不可达 upgraded node、失败重试和成功后再次 Save 为空。

本轮不回答：正式 ID、异构图、Generator、Store、bytes、commit、并发机制或性能。

### R2：Generator 产生图操作

状态：Concluded（EXP-012）。实现是 Generator 程序集内默认不可发现的 internal probe generator；它只由测试显式运行，不是 package/runtime capability。

问题：Generator 能否复刻手写 oracle，而不把 weak payload 或 wire format 泄露给正常路径？

已验证的 provisional generated seams：

```text
CaptureCurrent(value, Func<T, TIdentity>, out Snapshot, out CapturedReferences)
DurableEquals(in Snapshot, in Snapshot)
VisitReferences(in CapturedReferences, Action<T>)
```

`TIdentity : struct` 由调用方提供；Generator 不识别 identity field、不定义 allocator。Snapshot 的 self-reference slot 保存 `TIdentity?`，CapturedReferences 保存具体 CLR child。Capture 先按 FieldId 把每个 durable field 读入一次 local，再用同一 reference local 同时形成 ID slot 与 child slot。

可执行结果：

- 10 个新增动态编译 cases 覆盖 shared two-node cycle、重复 alias visitation、四种 scalar、transient、same-ID/different-child、child locality、child replacement、unsupported reference shape、reserved member 和 FieldId-order output；
- generated-driven delta 对 no-op、transient-only、child scalar change 与 same-valued child replacement 同时满足 literal expected results 和 EXP-011 oracle；
- Roslyn symbol/syntax/semantic checks证明 generated Snapshot、captured refs、signature、local 与 conversion 不进入 `object` / `dynamic` registry；
- default `DurableSchemaGenerator` 仍以 DG0007 拒绝 self-reference，probe class 没有 `[Generator]`，因此 package analyzer discovery 与严格 1...4 Snapshot History 未改变。

本轮只证明单个 self-referential durable type 的代码生成 seam。`TIdentity`、`Func`/`Action`、private generated names 和 visit order 都是 provisional；正式 `DurableId`、Reference TypeTag、产品 Generator 接入、异构 dispatch、polymorphism 与跨程序集引用继续暂缓。

### R3：读取归一化与两阶段对象图物化

状态：R3a Concluded（EXP-013）；R3b Concluded（EXP-014）。

问题：能否把 exact historical records 全量归一化成 current Snapshot table，并恢复共享引用和循环 CLR graph？

为保持失败定位清楚，本阶段包含两个依赖明确的小切片。

#### R3a：StoredGraphImage → normalized baseline

R3 首轮仍可沿用 test-only logical IDs/Snapshots 研究 Load 状态律；这不要求先把 EXP-012 probe 接入产品 Generator，也不授权定义 Reference TypeTag 或 wire bytes。

实现采用 immutable `StoredGraphImage`，其 record-table keys 是唯一 `SourceRecordIds` authority。V1/V2 payload 是封闭强类型 variants；test-only exact Schema descriptor 使用 `Int32`/`Reference` logical kind，不修改产品 TypeTag。

输入是 test-only immutable `StoredGraphImage`：显式 RootId、完整 `SourceRecordIds` 和 exact logical records；它不是 authoritative StateMap 或 persistent head。

已验证流程：

1. 从 StoredGraphImage 取得 root 和完整 `SourceRecordIds`；
2. 先按 ID 对全表执行 SchemaId、known version、exact shape 与 payload variant preflight，期间不调用 Decode；
3. 每个 record typed decode；V1 通过 `void(in ProbeSnapshotV1, out ProbeSnapshot)` 升级到 current，V2 直接得到 current Snapshot；
4. historical record 无论值是否改变都标记 `RequiresRewrite`，current record 不标记；
5. current/upgraded Snapshot 的每个 non-null reference 必须属于完整 `SourceRecordIds`，包括 disconnected source entries；
6. 全部成功后才一次性构造并返回 `NormalizedBaselineGraph`。

baseline 的 ID set 保留 `SourceRecordIds`。升级可能删除引用，使其中部分 source nodes 相对 current root 已不可达；这些节点留待下一次 Save 进入 `Unreachable`，不能在归一化阶段静默丢失。升级创建新 durable node 或重新接入 source table 之外的 ID 继续暂缓。

可执行结果：9 个聚焦 tests 覆盖 mixed/reversed records、value-changing/value-preserving upgrades、全表 schema-before-decode、unknown version、payload variant mismatch、missing handler、decode/upgrade late failure 与同 image retry、current/upgraded/default external reference、结构/defensive-copy gate，以及升级删边后保留 source entry 并交给 R1 Save 判为 Unreachable。

本切片只保证 loader 不修改输入、不返回 partial baseline；用户 decode/upgrade hook 自身的外部副作用不具备回滚语义。该 current-Snapshot baseline 随后成为 R3b 输入；StoredGraphImage 与 probe Schema 仍未提升为产品 API。

#### R3b：normalized baseline → current CLR graph

已验证流程：

1. 从 normalized current references 计算 `CurrentReachableIds`；
2. allocate-all reachable CLR placeholders；
3. hydrate-all scalar fields and references；
4. 验证共享引用和循环；
5. 全部成功后只返回 root，不暴露 placeholder map。

可执行结果：7 个聚焦 tests 覆盖 R3a→R3b→R1 组合路径、allocate-all-before-hydrate-all、shared alias、self-cycle、two-node cycle、disconnected source skip、constructor/initializer bypass、transient zero、one-time identity bind、重复物化不缓存，以及 late allocator/hydration failure 的 no-root 与 retry。invalid/dangling baseline 由 `NormalizedBaselineGraph` 在进入 materializer 前拒绝。

materialized root 是可丢弃 working graph，不是 baseline、StateMap 或第二 authority；`RequiresRewrite` 不进入 CLR object，只影响后续 Save。当前 phase hook 与 allocator 都是 test-only fault-injection seam，且不暴露 placeholder。未来若加入 `RebuildTransient` 或 graph invariant validation，root exposure boundary 必须顺延到这些阶段全部成功之后。

### S1：内存自适应双腿轮转策略模拟

状态：Paused Technical Reserve。该路线已形成独立、可执行的 Arena/Baselines/Tests subsolution，覆盖
deterministic workloads、runtime OVD、Base/Delta reconstruction、provisional RBF v0.40 sizing、
Stay-B/Rotate-C、preparatory migration、continuous rotation、typed admission/no-fallback、finite completion
certificate 与 W/P/F/R/L evaluator。它没有实现正式 wire、publication/reopen/crash、GC、完备 solver 或
完整 Adaptive 控制器。

产品候选转向 DB-014，因此 A/B/C、1-bit RelativeFrameTicket、A-debt evacuation 与
`CanPrepareAndRotate` 不进入正常 Save。完整冻结边界、未闭合项、可复用资产与恢复条件见
[`TwoLegRotationProbe/PROJECT-STATE.md`（归档）](../../../experiments/ARCHIVE.md#two-leg "原路径：experiments/TwoLegRotationProbe/PROJECT-STATE.md")；实现证据见其
README/tests，annotated recovery tag 为 `research/two-leg-rotation-probe-tech-reserve-20260902`。

只有 DB-014 无法满足真实的有界 dependency file count、在线磁盘退休、backup/rescue 或 compaction SLO
时，才重启 TwoLeg 或从中抽取 incremental cleaner。历史实验细节保留在本实验簿与 DB-007/011/012，
不再由 live roadmap 逐条维护。

### R4：内存 StateMap 与重复逻辑 delta apply

问题：在没有 bytes、head 和 crash model 的情况下，root、record reuse 与多次顺序 delta apply 的语义是否闭合？

最小切片：

- current logical StateMap，保存 ID 到 logical object-record 的绑定；
- 与该 StateMap 同源的 normalized projection；
- 初始 source StateMap 允许包含因 read-time upgrade 改边而相对 current root disconnected 的 rows，以及对应 rewrite obligations；
- unchanged ID 继承旧 record，Upsert 产生新 record，Unreachable 不进入新 StateMap；
- delta 按测试给定的顺序依次 apply；
- candidate failure 不替换旧 StateMap/baseline；成功 apply 后安装 exact result-root closure 与 clean baseline。

可执行闸门：

```text
SequentialApply(base, delta1, ..., deltaN)
    == expected logical StateMap N

LoadNormalized(logical StateMap N)
    == expected current Snapshot table

Materialize(LoadNormalized(logical StateMap N))
    == expected current CLR root closure
```

R4 的首个组合场景应从“disconnected source rows + reachable rewrite obligations”开始，证明 apply 后 StateMap 恰好成为 result-root closure。该阶段只验证逻辑 state transition，不让 delta 脱离当前顺序独立应用，也不承诺 revision、文件布局、原子 publish 或 durability。

### R5：为已证明的逻辑 IR 增加 binary codec

问题：能否为 Schema、object record、StateMap 和 logical delta 定义 deterministic、bounded、fail-closed 的 bytes，而不改变 R1-R4 的语义？

开始本阶段时重访 DB-001，裁决 Schema integrity/reference binding 是否需要 hash、完整 descriptor 或其他表示，并明确字段顺序、数字和字符串编码、null、length、reference ID、limits、unknown tag/version 与 trailing data。

可执行闸门：

- logical value round-trip；
- golden bytes；
- independent reader/writer agreement；
- truncation、oversized length、unknown tag、Schema mismatch 和 dangling reference fail closed；
- decode 后运行与 R4 相同的 materialization/delta laws。

### R6：持久 Store、publication 与故障恢复

问题：candidate records、StateMap 与 head 在明确故障模型下何时成为 authority？

本阶段才比较并裁决：

- object records 是否 append-only；
- exact head / expected-parent publication；
- 失败 candidate 如何分类、保留或回收；
- publication outcome 不明确时是否以及如何 reopen/reconcile；
- cache 与 exact head、current schema-set identity 的绑定；
- SchemaStore / StateStore 的真实 commit 边界。

可执行闸门必须来自 fault injection 和 reopen，而不是只看正常返回。publication 明确成功后才能安装 clean baseline；若 head 已成功但内存 cache 更新失败，丢弃 cache 并从 authority 重建。

### R7：有测量依据的优化

只有真实性能或容量数据出现后，才分别实验：

- 不物化完整 baseline、直接读取 delta chain；
- baseline cache、typed buckets 或更紧凑索引；
- per-object fingerprint / graph fingerprint；
- field-level sparse patch；
- frozen subgraph 快路径；
- hash table、sorted table、paged index 的替换。

优化不得改变 exact authority、reachable live set、reference-by-ID equality 或失败时 baseline 不变的语义。

## 5. 保持独立的研究分支

以下能力不横向塞进主线阶段；出现真实 consumer 后各自建立小实验：

- durable value struct 与递归 value codec；
- collection identity、ordering、comparer 和共享实例；
- durable inheritance flattening（DB-005）；
- heterogeneous graph、polymorphism 和同 ID Schema identity gate；
- multi-root、跨 root 共享和 ownership；
- `RebuildTransient` 与 graph-level invariant validation；
- upgrade handler 创建新 DurableId / 新 durable node；
- concurrent mutation、async Save 与 snapshot isolation；
- branch/fork、multi-writer 和跨 Repository identity。

## 6. 路线图维护规则

- 每一阶段开始前写清问题、最小成功/失败判据和明确非目标。
- executable evidence 成立后，把结论写入实验簿；路线图只保留尚未完成的依赖关系，不演化成完成历史。
- 若实验推翻当前模型，优先修改或删除后续阶段，而不是增加兼容层保存偶然原型形状。
- 任何会冻结 durable format、public identity 或 publication semantics 的选择，都应先记录竞争方案和重访触发条件。

## 7. 相关材料

- `docs/design-branches/0006-flat-graph-delta-prototype.md`
- `docs/design-branches/0001-schema-authority-and-runtime-representation.md`
- `docs/design-branches/0002-read-time-version-upgrade-pipeline.md`
- `docs/design-branches/0005-durable-inheritance-flattening.md`
- `docs/DurableGraph-lab-notebook.md`
- `docs/DurableGraph-target-design-v0.md`
