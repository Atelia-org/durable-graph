# DB-006：Flat Graph Delta 原型

> 历史迁移（2026-10-14）：原路径 `docs/design-branches/0006-flat-graph-delta-prototype.md`；来源修订 `8ef9fca`，正文保留原时期语义。
> 适用范围：R1–R3b test-only normalization/materialization 的实验结论；RequiresRewrite 与旧 baseline 不自动成为 DTO Save 合同。 今日事实从 [PROJECT-STATE](../../../src/PROJECT-STATE.md) 续接。

> 状态：Chosen for current prototype；R1/R2/R3a/R3b Concluded
> 创建日期：2026-08-27  
> 当前实验选择：latest typed Snapshot baseline、flat ID table、per-entry `RequiresRewrite`、whole-object Upsert、single-root iterative traversal、success-only baseline replacement。  
> 边界：此选择只固定下一实验的问题与不变量；不冻结 public API、正式 DurableId、异构 Snapshot 容器、wire format、persistent Store 或 commit protocol。
> 实现状态：EXP-011 已完成 R1，EXP-012 已完成隔离 R2，EXP-013/014 已完成 test-only normalization/materialization R3a/R3b。production runtime、默认 Generator 与 package 尚未获得 reference graph 能力。

## 1. 问题

在不依赖 ChangeTracker、setter interception 或业务代码 `MarkDirty()` 的前提下，能否比较：

```text
current editable CLR object graph
    vs
last accepted state's normalized typed Snapshot projection
```

并精确产生：

- 新增或变化节点的完整 current Snapshot；
- 仍可达但必须推进到 current Schema 的完整 Snapshot；
- 不再属于新 root closure 的 ID；
- 新图的 RootId；
- 失败时完全不修改旧 baseline。

首个探针追求语义可证伪，而不是持久化、内存最省或类型覆盖最广。

## 2. 当前实现边界

当前仓库已经有 exact Schema、boxed scalar payload、generated current/historical Snapshot、唯一相邻版本升级链和无隐式回写的 Load。

当前没有：

- `DurableId` 或 reference TypeTag；
- 对象图 traversal、共享引用或循环物化；
- Graph Delta、StateMap、object version 或 head；
- persistent Store、binary format、commit 或 cache；
- durable inheritance、collection 或一般 value struct。

因此本设计首先以 fixture-only 单类型逻辑模型验证语义，不立即扩张 runtime 公共 API 或 production Generator。

## 3. 三层边界

### 3.1 Authority layer（未来）

未来的 exact published head、authoritative StateMap、object record address、stored exact Schema 与 payload 属于 authority layer。它决定“哪个 record 已发布”和 unchanged ID 可以复用哪个旧 record。

首个探针没有这一层，不伪造 head、revision、record address 或 source token。

### 3.2 Normalization layer

读取 historical record 时：

```text
stored exact Schema validation
    -> exact historical Snapshot decode
    -> adjacent typed upgrades
    -> latest/current typed Snapshot
    -> BaselineEntry
```

所有 historical Snapshot 都只在 decode/upgrade pipeline 中短暂存在。进入 baseline 后，Graph Delta 只面对 current Snapshot。

EXP-013 已在 fixture 中验证该层：record-table keys 是唯一 `SourceRecordIds`；全表 exact Schema/payload-variant preflight 先于任何 Decode；V1 typed handler 只产出 current `ProbeSnapshot`；decode/upgrade/reference failure 均不返回 partial baseline。该证据不是 persistent authority 或产品 Load API。

### 3.3 Materialization layer

EXP-014 从 normalized baseline 的 RootId 出发，只计算 current reachable closure；先为每个 reachable ID 分配唯一 CLR placeholder，再统一 hydrate scalar 与 references，全部成功后只返回 root。disconnected source entries 不分配，仍留在 baseline 供下一次 Save 分类。

materialized root 是可丢弃 working graph，不是 authority、object table 或 cache。`RequiresRewrite` 不进入领域对象。未来 `RebuildTransient`/graph invariant phase 若加入，root exposure 必须顺延到其成功之后。

### 3.4 Comparison layer

comparison layer 从 current domain root 遍历 CLR graph，与 normalized baseline 按 DurableId 比较，产生 logical `GraphDelta`。它不读磁盘 delta chain，也不解释 historical Schema。

## 4. 术语与最小逻辑模型

以下伪类型只表达语义，不是 proposed public API：

```csharp
readonly record struct ProbeId(long Value);

struct ProbeSnapshot {
    int Value;
    ProbeId? NextId;
    ProbeId? AliasId;
}

readonly record struct BaselineEntry<TSnapshot>(
    TSnapshot Snapshot,
    bool RequiresRewrite);

sealed record NormalizedBaselineGraph<TSnapshot>(
    ProbeId RootId,
    IReadOnlyDictionary<ProbeId, BaselineEntry<TSnapshot>> Entries);

sealed record GraphDelta<TSnapshot>(
    ProbeId ResultRootId,
    IReadOnlyDictionary<ProbeId, TSnapshot> Upserts,
    IReadOnlySet<ProbeId> UnreachableIds);
```

### Current domain graph

由普通 CLR references 组成的可编辑对象图。每个 durable node 暴露稳定 ProbeId；首轮由 fixture 手工赋值，不设计 allocator。

### Normalized baseline

与 current domain objects 完全脱钩的派生比较投影：

- map key 是 durable identity；
- entry Snapshot 是该类型 current write version；
- Snapshot 的 reference fields 只保存 ID；
- `RequiresRewrite` 不进入 Snapshot durable fields；
- RootId 是 graph envelope 的一部分，不能从 flat table 推导。

baseline entry set 对应来源 record table 的 `SourceRecordIds`。若历史升级删除引用，使某些 source nodes 相对 current root 已不可达，它们仍暂留在 baseline；下一次 Save 必须把它们分类为 `UnreachableIds`。升级后的 non-null reference 若指向 `SourceRecordIds` 之外的 ID，首轮视为 unsupported/dangling 并 all-or-nothing fail。

### Graph Delta

- `Upserts` 合并 Added、Changed 与 reachable RewriteRequired；
- 每个 Upsert 都是完整 current Snapshot，不是 field patch；
- `UnreachableIds` 只表示不进入新 root closure / StateMap，不表示物理删除历史 record；
- `ResultRootId` 总是显式携带，避免提前设计 optional root-change encoding。

## 5. `RequiresRewrite` 的语义

读取归一化规则：

```text
exact current payload
    -> current Snapshot
    -> RequiresRewrite = false

exact historical payload
    -> historical Snapshot
    -> adjacent typed upgrades
    -> current Snapshot
    -> RequiresRewrite = true
```

选择 `RequiresRewrite` 而不是 `WasUpgraded`，因为它表达下一次 Save 的义务，而不是持久字段或诊断历史。当前只有 historical upgrade 设置该标志；暂不引入 reason enum。

该标志位于 `BaselineEntry` envelope：

- 不属于 Schema shape；
- 不参与 Snapshot equality；
- 不要求 upgrade handler 复制；
- 不单独成为持久 authority；
- 不与 per-entry stored version、record address 或 source token 并存。

exact stored Schema 和 upgrade path 在 load boundary 验证。未来诊断若需要来源信息，可产生独立 `LoadReport`；unchanged record address 仍从 authoritative StateMap 取得。

## 6. Flat graph 与索引

Flat graph 的规范语义是 `Map<DurableId, Entry>`，不是某个具体容器：

- reference slot 比较目标 ID；
- child 内容变化由 child 自己的 Snapshot 负责；
- shared child 只存在一个 entry；
- cycle 通过 ID edge 表达，不递归嵌套 Snapshot；
- current live set 由 ResultRootId 经 durable references 得到。

首个探针可以用 `Dictionary<ProbeId, ...>` 获得直接 lookup。若希望日志和断言稳定，输出时对 ID 显式排序。未来 canonical bytes 需要自己的排序规则，不能继承 `Dictionary`、`SortedDictionary` 或 CLR comparer 的偶然行为。

## 7. Save planning

首版前置条件：调用方保证整个 capture/diff 期间 current domain graph quiescent，且同一 baseline 不被并发 Save。此处不实现锁、epoch、copy-on-write 或 mutation detection。

概念算法：

```text
PlanSave(baseline, currentRoot):
    stack.Push(currentRoot)
    idToInstance = empty map
    expandedInstances = reference-identity set
    currentReachableIds = empty set
    upserts = empty map

    while stack not empty:
        node = stack.Pop()
        id = node.DurableId

        if idToInstance contains id with a different CLR instance:
            fail closed
        else:
            idToInstance[id] = node

        if node already in expandedInstances:
            continue

        mark node expanded
        currentReachableIds.Add(id)

        capture one ephemeral current Snapshot
        capture actual child references from the same field reads

        if baseline has no id
           or baseline[id].RequiresRewrite
           or generated typed equality says Snapshot differs:
            upserts[id] = complete current Snapshot

        push every non-null durable child reference

    unreachable = baseline.Ids - currentReachableIds

    return GraphDelta(
        currentRoot.DurableId,
        upserts,
        unreachable)
```

关键限制：

1. 必须先检查 `DurableId -> CLR instance` 冲突，再按 visited 跳过；否则两个不同实例冒用同一 ID 会被静默当作共享引用。
2. parent Snapshot 相等也必须继续访问 child；parent unchanged 不能作为 traversal pruning。
3. reference field 最好只读一次，同时得到 Snapshot 中的 ID 和待压栈 CLR child，减少实现自身的二次读取不一致；这仍不替代 quiescence 前置条件。
4. current Snapshot 可作为短寿命 local；只有 Upsert 才长期保留。完整 current SnapshotGraph 可以存在于测试 oracle，不是 production diff 的必要分配。

## 8. Save 成功与失败

规划、candidate write 和 publication 期间，accepted baseline 保持不可变。不能在产生 Upsert 时原地清除 `RequiresRewrite`。

首探针使用纯内存 `AcceptForAssertion` 模拟成功：

```text
next = old baseline
    - UnreachableIds
    + Upserts as clean entries
    + unchanged clean entries
    + ResultRootId
```

它必须断言旧 baseline 的每个 `RequiresRewrite` entry 最终属于：

```text
Upserts union UnreachableIds
```

成功后安装一个全新的 clean baseline；失败则保留旧 baseline 和所有 rewrite obligations，retry 应产生等价 candidate。

未来真实 publication 的顺序是：

```text
plan candidate
    -> write candidate records / StateMap
    -> publish exact new head
    -> install matching clean projection
```

若 publish 失败，旧 bundle 不变；若 publish 已成功但 projection 更新失败，丢弃 projection 并从 authority 重建，不能回滚或猜测 head。

## 9. 未来 persistent binding

一旦存在 persistent Store，normalized projection 不能单独用于 Save：

```text
LoadedBaselineBundle
    ExactHead
    AuthorityStateMap
    NormalizedBaselineGraph
```

必须保证：

- projection RootId 与 StateMap root 同源；
- projection ID set 与 source live StateMap 一致；
- projection 与 StateMap 来自同一 exact head；
- unchanged object address 只从 AuthorityStateMap 取得；
- 若 projection cache 跨进程或代码版本，还要绑定 current schema-set / generator generation；
- head mismatch 时拒绝使用 stale projection。

这些是 persistent integration 的 gate，不是首探针要伪造的 API。

## 10. 不可约不变量

1. exact Schema/shape validation 必须先于 decode 和 upgrade；任一节点失败不产生可消费 partial baseline。
2. baseline Snapshot 全部是各自 durable type 的 current write version，并与 domain objects detached。
3. baseline 是按 ID 展平的 `SourceRecordIds` table，并显式保存 RootId；Snapshot reference 只保存 ID。
4. 一个 materialization/save traversal 中，一个 DurableId 只对应一个 CLR instance。
5. 进入异构图后，同一 DurableId 的 source Schema identity 与 current CLR binding 不同必须 fail closed，不能作为普通 Upsert；单类型首探针暂时不可构造该状态。
6. Load normalization 不回写 authority；`RequiresRewrite` 只记录下一次显式 Save 的义务。
7. reachable `RequiresRewrite` 节点 whole-object Upsert；unreachable 节点不因 flag 被保活。
8. scalar/value 按其 durable equality 比较；reference 按 ID 比较；transient 完全忽略。
9. child 内容变化不传播成 parent 变化；parent unchanged 也不停止 traversal。
10. capture、diff、candidate 或 publication 失败不改变 accepted baseline。
11. 只有 publication 明确成功后才安装 clean baseline；不原地清 flag。
12. `UnreachableIds` 不等于物理删除历史数据。
13. 首版 Save 假设 current graph quiescent；当前没有并发保证。

## 11. 首个探针的可执行闸门

| 场景 | 必须观察到的结果 |
|---|---|
| 完全不变 | 空 Upserts、空 Unreachable，RootId 不变 |
| 只修改 transient field | 空 Upserts、空 Unreachable |
| 修改 child scalar | 只 Upsert child，parent 不变 |
| 同值 child 换新 ID | parent 与新 child Upsert；旧 child Unreachable |
| 两字段共享 child | child 只访问和记录一次 |
| self-cycle / two-node cycle | traversal 终止，flat edges 正确 |
| 两个实例冒用同一 ID | fail closed，baseline 不变 |
| 移除一条共享 edge | child 仍可达 |
| 移除进入 cycle 的最后 edge | 整个 cycle Unreachable |
| root 替换但 node table 不变 | ResultRootId 改变，即使无 Upsert |
| reachable historical-upgraded/equal node | 仍产生完整 current Upsert |
| historical-upgraded node 已不可达 | 不 Upsert，只进入 Unreachable |
| upgraded reference 指向 source table 外 ID | all-or-nothing fail，不扩张 baseline |
| capture/comparer/candidate failure | baseline 与 flag 不变，retry 可重复 |
| successful Accept 后再次 Diff | 第二次为空 |
| deterministic display | Map/Set 以显式 ID 顺序显示 |

核心状态律：

```text
AcceptForAssertion(B, Diff(B, current))
    == CaptureCleanForAssertion(current)
```

失败律：

```text
Capture / Diff / candidate failure
    => AcceptedBaselineAfter == AcceptedBaselineBefore
```

## 12. Generator seam 的隔离验证

EXP-012 没有把 self-reference 半接入仍受 scalar Schema/history/boxed serializer 约束的产品 Generator，而是在 Generator 程序集内增加无 `[Generator]` 的 internal probe。测试显式运行它，并验证：

```text
CaptureCurrent(value, Func<T, TIdentity>, out Snapshot, out CapturedReferences)
DurableEquals(in Snapshot, in Snapshot)
VisitReferences(in CapturedReferences, Action<T>)
```

`TIdentity : struct` 是 provisional caller-provided resolver result。Snapshot reference slot 保存 `TIdentity?`，CapturedReferences 保存具体 self type；Capture 先把字段读入 FieldId-order locals，再由同一 reference local 同时形成 ID 与 CLR child。generated normal path 没有 wire writer、Store、delta-chain reader、`object` registry 或 type-erased Snapshot。

该 seam 只证明 Source Generator 可表达 R1 所需的强类型操作。它不定义正式 identity、Reference TypeTag 或 package API；默认 `DurableSchemaGenerator` 继续以 DG0007 拒绝 self-reference。

## 13. 当前裁决与暂缓项

| 事项 | 当前裁决 |
|---|---|
| latest Snapshot baseline | Keep |
| flat ID table + explicit RootId | Keep |
| `RequiresRewrite` entry envelope | Keep |
| whole-object Upsert | Keep |
| success 后整体替换 baseline | Keep |
| per-entry source Schema/version/address | Defer；留在 authority/load context |
| hash table vs ordered table | Defer；容器无语义 |
| full current SnapshotGraph | Test oracle only |
| field-level sparse patch | Defer until measured need |
| direct disk-chain comparison | Defer until materialized path is correct and measured |
| formal DurableId/allocator | Defer |
| provisional `TIdentity` resolver | Probe only；不提升为产品 API |
| Reference TypeTag/history representation | Defer；默认严格 history 仍只有 1...4 |
| production Generator graph adapter | Defer；EXP-012 默认不可发现 |
| test-only StoredGraphImage normalizer | Keep as R3a evidence；不提升为产品 API |
| root-only two-pass materializer | Keep as R3b evidence；不暴露 placeholder map |
| materialized CLR graph ownership | Disposable working graph；不是 baseline/StateMap authority |
| heterogeneous graph/type-erased Snapshot table | Defer |
| 同 ID 更换 Schema identity | Future heterogeneous gate；预期 fail closed |
| inheritance/value struct/collections | Separate experiments |
| multi-root/shared ownership | Defer |
| BaseRevision/head/CAS/cache | Persistent integration stage |
| BinaryReader/Writer/canonical format | After logical delta-chain experiment |
| physical delete/GC/compaction | Defer |
| concurrent mutation support | Defer; quiescence precondition |

## 14. EXP-011 实验结果

实现位置：

- `tests/DurableGraph.Tests/GraphDeltaProbe.cs`
- `tests/DurableGraph.Tests/GraphDeltaProbeTests.cs`

观察：

- **Observed**：在 `ProbeNode.Id` 不可变的前提下，一个 `Dictionary<ProbeId, ProbeNode>` 足以同时承担 first-visit、shared/cycle termination、current reachability 与 same-ID/different-instance fail-closed；不需要并行维护 reference-visited 和 reachable set。
- **Observed**：baseline entry 只保存 current `ProbeSnapshot` 与 `RequiresRewrite`，就能把 missing、changed 和 reachable historical-upgraded node 合并为 whole-object Upsert；unreachable upgraded node 只进入 `UnreachableIds`。
- **Observed**：reference equality 按 ID 后，child scalar change 只 Upsert child；shared child 只 capture 一次；self-cycle 和 two-node cycle 均终止。
- **Observed**：baseline constructor 可以保留 source table 中暂时 disconnected 的 entries；`PlanSave` 会把它们分类为 unreachable。成功 `AcceptForAssertion` 则必须额外验证结果 table 恰好等于 `ResultRootId` 的可达闭包，不能让漏报 removal 的 clean entry 延迟到下次 Save。
- **Observed**：capture fault 即使发生在前面已经形成局部 Upsert 之后，也不会暴露 partial candidate 或改变 baseline；retry 仍从完整 rewrite obligation 开始。
- **Observed**：baseline、Upserts 和 Unreachable inputs 均 defensive copy；核心 Map/Set 不承诺枚举顺序，测试展示通过显式按 ID 排序获得确定性。
- **Observed**：targeted test review 找到并促成 exact accepted-closure gate；修复后 correctness、test-evidence 与 simplification 三路复审均无 blocker/medium。

验证：

- Graph Delta 聚焦测试：21/21 passed。
- `DurableGraph.slnx`：0 warnings / 0 errors。
- `DurableGraph.Tests`：121/121 passed。
- `dotnet format --verify-no-changes`：passed。

本实验仍未证明：

- historical payload 实际读取和 upgrade-to-baseline integration；
- EXP-011 自身未证明 Generator 产生 `Capture / Equals / VisitReferences`；该 gap 随后由隔离的 EXP-012 probe 回答；
- 两阶段 CLR graph hydrate 后恢复 sharing/cycle；
- 正式 DurableId、异构图、StateMap、bytes、commit 或并发 Save；
- allocation、stack、throughput 或 baseline 内存成本。

结论：R1 semantic probe 成立；DB-006 的最小逻辑模型获得 executable evidence。该结论随后成为 EXP-012 的 oracle，而不是直接把 fixture types 提升为 public API。

## 15. EXP-012 实验结果

实现位置：

- `src/DurableGraph.Generator/DurableGraphOperationsProbeGenerator.cs`
- `tests/DurableGraph.Tests/GeneratedGraphOperationsProbeTests.cs`

观察：

- **Observed**：无 `[Generator]` 的 internal probe generator 可以为四种 scalar 加 direct self-reference 生成 `Snapshot<TIdentity>`、`CapturedReferences`、Capture、typed equality 与 visitor；默认 analyzer discovery 不运行它。
- **Observed**：reference field 的 CLR child 只从 owner 读取一次；同一 local 同时提供 identity resolver 输入和 captured child，visitor 按 FieldId 顺序保留每个 reference slot，包括两个 slot 指向同一 child 的重复项。
- **Observed**：parent equality 比较 reference ID 而非 CLR instance 或 child 内容；same-ID/different-child 与 child scalar mutation 不改变 parent Snapshot，child ID/null change 会改变 parent Snapshot。
- **Observed**：generated-driven delta 的 no-op、transient-only、shared two-node cycle child change 与 same-valued child replacement，均同时匹配 literal results 和 EXP-011 oracle。
- **Observed**：array、base-typed 与 cross-type references 以 DG0007 fail closed；reserved generated type 以 probe-only DG0018 fail closed；任一单类型 validation failure 不留下空 graph hint。
- **Observed**：Roslyn syntax/symbol/semantic gates 覆盖 strong Snapshot/ref/signature/local/conversion，未发现 generated normal path 中的 `object` / `dynamic`、dictionary、TypeTag 或 Serializer。
- **Observed**：独立 correctness 与 test/simplification review 的有效发现已修复；最终无 blocker/medium。

验证：

- R1+R2 `~Probe` 聚焦测试：31/31 passed，其中 EXP-012 新增 10 cases。
- 完整 solution/test/format 结果在实验簿 EXP-012 中记录。

本实验仍未证明：

- production/package Generator 支持 durable reference；
- 正式 DurableId、allocator、Reference TypeTag 或 historical reference Snapshot；
- EXP-012 自身未证明 historical graph Load；该 normalization gap 随后由 test-only EXP-013 回答；two-pass hydrate、StateMap、bytes 或 persistence 仍未证明；
- delegate/struct shape 的 allocation 或性能优势。

结论：R2 code-generation seam 成立，但保持为隔离探针。该结论随后为 EXP-013 提供 current Snapshot 边界，而不是把 probe 自动注册为产品 Generator。

## 16. EXP-013 实验结果

实现位置：

- `tests/DurableGraph.Tests/StoredGraphNormalizationProbe.cs`
- `tests/DurableGraph.Tests/StoredGraphNormalizationProbeTests.cs`

观察：

- **Observed**：immutable StoredGraphImage 从 entry sequence 建立 defensive copied record table；重复/default ID、missing/default root 与 null record 在 Decode 前拒绝，`Records.Keys` 是唯一 `SourceRecordIds` authority。
- **Observed**：test-only Schema 对 `(SchemaId, Version, sorted FieldId/Kind)` 做内容相等；全表按 ID 预检 identity、known version、exact shape 与 V1/V2 payload variant，任一失败时全表 decode/upgrade 计数为零。
- **Observed**：V1 `void(in ProbeSnapshotV1, out ProbeSnapshot)` handler 与 V2 typed Decode 都只产生 current Snapshot；historical entry 无论升级后值是否改变都 `RequiresRewrite=true`，current entry 为 false。
- **Observed**：current、upgrade-produced 与显式 nullable-default reference 均对完整 source table fail closed；坏引用位于 disconnected record 时也会被验证，证明 normalization 不只走 root closure。
- **Observed**：upgrade 删除 edge 后，目标 source entry 仍保留在 baseline；交给 R1 PlanSave 后 historical root whole-object Upsert，断开的 target 进入 Unreachable。
- **Observed**：late decode 与 late upgrade failure 均不返回 partial result；保留原 input record instances 与 inner exception，同一 image 修复后重试会重新 decode 全部相关 records 并返回完整 baseline。
- **Observed**：输入 schema/entry arrays、typed value payload 与输出 baseline 相互 detached；record input order 不改变按 ID 的逻辑结果或处理 trace。
- **Observed**：两路独立 correctness/test-evidence review 的有效缺口已补齐；最终无 blocker/medium。

验证：

- R3a 聚焦测试：9/9 passed。
- 完整 solution/test/format 结果在实验簿 EXP-013 中记录。

本实验仍未证明：

- EXP-013 自身未证明 R3b allocate-all/hydrate-all；该 gap 随后由 EXP-014 回答；transient rebuild 仍未证明；
- product Reference TypeTag、Generator/Store integration 或 historical bytes；
- heterogeneous graph、upgrade-created node、StateMap/head、commit 或 persistence；
- 对 decode/upgrade handler 外部 side effect 的 rollback。

结论：R3a normalization state law 成立。该 baseline 随后成为 EXP-014 materialization 输入；不要把 fixture Schema/StoredGraphImage 当作 durable format。

## 17. EXP-014 实验结果

实现位置：

- `tests/DurableGraph.Tests/NormalizedGraphMaterializationProbe.cs`
- `tests/DurableGraph.Tests/NormalizedGraphMaterializationProbeTests.cs`
- `tests/DurableGraph.Tests/GraphDeltaProbe.cs` 中的 one-time hydration seam。

观察：

- **Observed**：materializer 用显式 stack 计算 current RootId closure；baseline 中 disconnected source ID 不 allocation/hydration，也不从 baseline 删除。
- **Observed**：第一轮为每个 reachable ID 产生唯一 `RuntimeHelpers.GetUninitializedObject` placeholder；第二轮才解析完整 ID table 并 hydrate，phase tests 不依赖 ID 枚举顺序。
- **Observed**：shared Next/Alias 恢复同一 CLR instance；self-cycle 与 two-node cycle 均以 `ReferenceEquals` 恢复；scalar/null refs 使用 literal assertions 验证。
- **Observed**：正常 constructor/field initializer 被绕过，transient/CaptureHook 保持零值；one-time hydration seam 拒绝正常构造节点或已绑定 identity 的节点再次 hydrate。
- **Observed**：R3a historical root materialize 后仍由 baseline `RequiresRewrite` 驱动 R1 whole-object Upsert；disconnected source row 进入 Unreachable，flag 不进入 CLR object。
- **Observed**：late allocator failure 与 late hydration failure 都不返回 root，保存 outer phase/id/inner exception，baseline 不变；同一 baseline retry 重新执行完整两阶段。重复成功 materialization 返回互不共享的 fresh graphs。
- **Observed**：fault-injection phase hook 只接收 `(Phase, Id)`，不暴露 placeholder/map；可插拔 allocator 仅为 internal test seam，默认仍是 `GetUninitializedObject`。
- **Observed**：独立 correctness/test-evidence review 找到并促成真实 allocator-boundary fault injection 与去除 ID sorting；最终无 blocker/medium。

验证：

- R3b 聚焦测试：7/7 passed。
- 完整 solution/test/format/package 结果在实验簿 EXP-014 中记录。

本实验仍未证明：

- `RebuildTransient`、graph invariant validation 或其失败时的 exposure gate；
- product DurableId 的 constructor-bypass one-time binding API；
- product Generator/Reference TypeTag、heterogeneous materialization、StateMap/cache 或 bytes；
- allocation/throughput/stack/memory 性能。

结论：R3b root-only two-pass materialization state law 成立。下一主线仍是 roadmap R4 logical StateMap/delta apply；R5–R7 顺序不变。

## 18. 重访触发条件

- 首个 probe 证明或推翻 flat baseline / whole-object delta laws；
- production Generator 开始支持第一个 durable reference field；
- 正式 identity 需要在 constructor-bypassed CLR instance 上一次性绑定；
- 引入 `RebuildTransient` 或 graph-level invariant validation；
- historical upgrade 需要改变引用或创建新 durable node；
- 出现第二个 durable node type，需要异构 dispatch 和 Schema identity gate；
- 开始设计 logical/persistent StateMap 与 exact head；
- 开始定义 canonical object-record bytes；
- 性能数据表明 materialized baseline 或 O(live graph) traversal 不可接受。

## 19. 相关材料

- `docs/DurableGraph-research-roadmap.md`
- `docs/design-branches/0002-read-time-version-upgrade-pipeline.md`
- `docs/design-branches/0005-durable-inheritance-flattening.md`
- `docs/DurableGraph-lab-notebook.md`
- `docs/DurableGraph-target-design-v0.md`
