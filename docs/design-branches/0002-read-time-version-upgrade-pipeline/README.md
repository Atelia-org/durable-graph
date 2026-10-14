# DB-002：读取阶段的版本升级管线

> 历史迁移（2026-10-14）：原路径 `docs/design-branches/0002-read-time-version-upgrade-pipeline.md`；来源修订 `8ef9fca`，正文保留原时期语义。
> 适用范围：legacy boxed 相邻升级 coordinator 的当片裁决；不是后续 DTO 图 Restore 的实现合同。 今日事实从 [PROJECT-STATE](../../../src/PROJECT-STATE.md) 续接。

> 状态：Chosen for current prototype
>
> 创建日期：2026-08-27
>
> 当前方向：读取旧版本时尝试升级为请求的当前版本；Load 不隐式写 Store，后续显式 Save 才保存升级后的对象。

> 实现状态：EXP-009 已验证唯一相邻 read-time upgrade 语义；EXP-010 进一步选择“唯一一次 version switch + case decode + goto Snapshot-ready labels + 单份顺序相邻链”。本选择不冻结长期公共 API，也不开放一般版本图。

## EXP-009/010 裁决与实现结果

当前 version-aware serializer seam 是：

```csharp
DurableSchema Schema { get; } // current/write schema

T Deserialize(
    DurableSchema storedSchema,
    IReadOnlyDictionary<int, object?> fields);
```

`InMemoryStateStore` 从 State key 取得 authoritative exact Schema，只预先拒绝 SchemaId 不同；同 identity 的 version/shape 由 generated serializer 与其 historical IR 校验。Load 不登记 current Schema、不写回 State。

Generator 生成一个线性 coordinator：

```text
single stored-version switch
    -> each case validates and decodes its exact starting Snapshot
    -> goto SnapshotVnReady
    -> one shared ordered sequence of UpgradeVnToVn+1 labels/calls
    -> allocate and hydrate current domain object
```

缺相邻 handler 与漏填 out field 分别由 CS8795/CS0177 在编译期拒绝；unknown version、identity mismatch、shape conflict 和 handler exception 在运行时使用 typed failures。两个 current CLR types 复用同一 SchemaId 由 DG0017 拒绝。

初次独立挑战比较了每入口 O(N²) 后缀复制与 O(N) suffix helpers；后者会引入多层 helper frames。随后 EXP-010 验证了第三种形状：用 goto labels 在同一方法内让多个 case 进入共享后缀。它既不复制 edge call-sites，也不增加 helper frames，因此成为当前选择。

当前生成规模为 O(versions + fields + edges)，但运行时性能尚未据此宣称改善：单方法包含全部 Snapshot locals，`in/out` address-taking 下的 stack-slot reuse 与大方法 JIT 成本需要真实长链测量。

## 阶段目标

在继续使用 `InMemorySchemaStore`、`InMemoryStateStore` 和 boxed state 的前提下，跑通：

```text
stored State (Schema V1)
    -> exact historical Schema validation
    -> exact V1 Snapshot decode
    -> static adjacent upgrade path
    -> current V2 CLR object
    -> caller-visible result

later explicit Save(V2 object)
    -> register Schema V2
    -> write State as V2
```

本阶段不处理增量序列化、统一 commit、多 Store durability、对象身份或 wire format。

## EXP-006 后的方向修订

`experiments/SourceGeneratorHistoryProbe/` 已观察到：

- Source Generator 的 `AddSource` 输出不会自行成为下一次 compilation 的 `AdditionalFiles`；即使旧 candidate 仍物理存在，未显式登记时也不是输入。
- opt-in post-compile publisher 可以在成功 build 后把 current shape 写到外部 Snapshot History；它只能在下一次独立 project evaluation 中被 Generator 读取。
- 只保留当前 V2 领域源码和显式 V1 history 时，fresh compilation 能生成 `SnapshotV1`/`SnapshotV2`，编译并执行强类型 `V1 → V2` 方法。

同时已经决定首轮只支持唯一相邻版本链。由此当前倾向收窄为闭世界 generated pipeline：

```text
explicit Snapshot History + current domain source
    -> generated exact historical Snapshots/materializers
    -> generated stored-version switch
    -> direct typed V1 -> V2 -> V3 calls
    -> current domain materialization
```

runtime historical binding registry、upgrade registry 和 object-erased edge adapter 暂缓。只有 runtime plugin、独立 migration package、多个程序集共同贡献版本或热安装修复成为真实消费者时再重访。

EXP-006 只证明 build hook/自制工具能够承担历史发布 side effect；它没有选定正式 history format、publisher owner，也没有批准普通 build 修改受版本控制的工作树。

后续 Snapshot signature probe 又收窄了当前原型形状：historical Snapshot 使用 generated ordinary struct；相邻 handler 使用版本限定的 required partial `void UpgradeV1ToV2(in oldValue, out newValue)`。`out` 提供字段级 definite-assignment tripwire，但 `default` 仍可绕过，不能把它描述成领域有效性保证。详见 DB-003。

针对快速原型的 history publishing，当前直觉选择是成功的本地非 design-time compilation 自动 append current Snapshot；CI/design-time 只读验证，单 writer 串行发布。显式 Accept target 保留为竞争分支。详见 DB-004。

## 必须保留的边界

1. State record 的 `(SchemaId, Version)` 决定唯一 historical Schema，不使用 latest fallback。
2. historical payload 只能交给与该 exact Schema shape 绑定的 serializer/materializer。
3. 同 SchemaId 才允许版本升级；不同 SchemaId 是类型不匹配。
4. 缺失 historical shape、缺失相邻升级实现、链不完整、handler 抛错或后续 validation 拒绝 out Snapshot 时 fail closed；可在生成期/编译期拒绝的情况不推迟到运行时。
5. Load 不修改 StateStore 或 SchemaStore authority；升级成功只返回新的内存对象。
6. 只有调用方随后显式 Save，升级后的当前版本才进入 Store。
7. 失败升级不得覆盖旧 State；旧 Schema 和旧 boxed fields 保持可再次读取/诊断。

## 为什么需要 historical serializer binding

EXP-009 之前，`InMemoryStateStore.Load<T>` 只把 fields 交给 current `IDurableSerializer<T>`，V2 serializer 不能安全解释 V1 fields。现在 Store 同时传入 authoritative stored Schema，generated serializer 用一次 version switch 绑定 exact historical Snapshot。

升级前必须先找到一个与 stored exact Schema 绑定的 historical binding：

```text
ExactSchemaKey + exact Schema shape
    -> historical CLR type
    -> historical serializer/materializer
```

原始候选是假设加载协调器需要在运行时查找未知 historical CLR 泛型类型。EXP-009 已验证更小的闭世界方案：Generator 为每个 current durable type 生成完整 historical switch 和直接强类型调用；Store 只面对一个 version-aware current binding，不知道 Snapshot CLR 类型。

## 原始候选最小组件（runtime registry 已暂缓）

### Historical serializer registry

职责：

- 按 exact `(SchemaId, Version)` 查找 binding。
- 验证 binding.Schema 与 SchemaStore 中的 stored Schema 结构相等。
- 从 boxed fields 物化 exact historical CLR object。
- duplicate exact key 且 shape/type/serializer 不一致时拒绝注册。

不负责：

- 选择升级路径；
- 自动扫描所有程序集；
- wire-format decode；
- 持久化 registry。

### Upgrade registry

每条 edge 至少表达：

```text
SchemaId
FromVersion + source CLR type
ToVersion + target CLR type
Upgrade handler
```

handler 应是显式、同步、确定的对象转换；本阶段不允许网络、时间、LLM 或 Store side effect。

### Load orchestrator

建议流程：

1. 从 State record 取得 stored exact Schema key。
2. 从 SchemaStore 取得并验证 stored Schema。
3. 从 serializer registry 取得 exact historical binding，并再次核对完整 shape。
4. materialize historical object。
5. 若 stored version 已是 requested version，验证 CLR type 后直接返回。
6. 否则查找完整 upgrade path。
7. 逐 edge 执行 handler；每一步验证输入/输出 CLR type、SchemaId 和 Version。
8. 返回 requested current CLR object，不修改 Store。

## 路径规则候选

### 候选 A：唯一相邻版本链

只允许 `V1 -> V2 -> V3`，每个 `(SchemaId, FromVersion)` 最多一条 edge，且 `ToVersion = FromVersion + 1`。

优点：确定、易诊断、无需图算法，符合当前小实验。

代价：无法直接跳过没有 CLR 定义的中间版本，也不能表达多条迁移策略。

### 候选 B：一般有向图

允许 `V1 -> V3`、多条路径或分支，再定义 deterministic path selection。

优点：灵活。

代价：需要处理环、歧义、优先级和路径稳定性；当前没有消费者证明需要。

当前倾向：先用候选 A。出现真实跳版需求前不引入一般图。

## 首个演示场景

建议由 history + current source 生成两个 ordinary struct Snapshot，共用 SchemaId：

```text
CharacterSnapshotV1, Schema version 1
    Field 1: String display name

CharacterSnapshotV2, Schema version 2
    Field 1: String display name
    Field 2: Boolean is active
```

演示：

1. 用 V1 current-domain generated serializer 保存 boxed State。
2. V2 compilation 从 checked-in V1 history 生成 SnapshotV1/V2、exact materializer、required V1 → V2 partial declaration 和 direct call。
3. 请求 Load V2，先 materialize SnapshotV1，再通过 `void UpgradeV1ToV2(in V1, out V2)` 得到 SnapshotV2，最后 hydrate 当前领域对象。
4. 证明 StateStore 仍是 V1，Load 没有隐式改写。
5. 显式 Save V2 后，证明 slot 改为 V2，后续可直接 Load V2。

## 必测失败路径

- State 指向未知 Schema version。
- Snapshot History 缺少 stored version，或 history version chain 有缺口。
- generated historical expected Schema 与 SchemaStore exact Schema shape 不一致。
- 缺少 required V1 → V2 implementation 或漏赋目标 Snapshot field 时 compilation fail。
- handler 抛异常。
- SchemaId 不同却尝试升级。
- 任一 runtime 失败后旧 State 保持不变，修复 history/handler、重新构建后仍可重试。

## 对象自洽仍未解决

`RuntimeHelpers.GetUninitializedObject` 绕过构造器。即使字段类型与 Schema 匹配，物化对象仍可能违反领域 cross-field invariant，transient field 也保持零值。

本升级切片暂时只验证：

- exact Schema/type binding；
- handler 输入输出类型；
- upgrade path 完整性；
- Store 无副作用。

`RebuildTransient`、对象图 invariant validation 和 malformed payload 的“先验证全部字段再分配”仍是后续独立阶段。

## EXP-009 后仍未裁决的问题

1. boxed payload 的最终 malformed/extra-field/nullability 错误模型。
2. handler 领域 validation、RebuildTransient 与外部 side-effect policy。
3. 何时需要共享 suffix coordinators、一般版本图或 runtime registry。
4. persistent wire format 与 Schema/State commit/recovery boundary。

## 相关材料

- `docs/DurableGraph-lab-notebook.md` 的“当前自洽边界”、EXP-005、EXP-006 和 EXP-007。
- `docs/design-branches/0001-schema-authority-and-runtime-representation.md`。
- `docs/design-branches/0003-snapshot-value-shape-and-upgrade-signature.md`
- `docs/design-branches/0004-snapshot-history-authoring-and-publishing.md`
- `experiments/SourceGeneratorHistoryProbe/README.md`
- `experiments/SnapshotUpgradeShapeProbe/README.md`
- `src/DurableGraph/InMemoryStateStore.cs`
- `src/DurableGraph/IDurableSerializer.cs`
- `src/DurableGraph.Generator/DurableSchemaGenerator.cs`
