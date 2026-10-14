# DB-001：Schema authority 与运行时表示

> 历史迁移（2026-10-14）：原路径 `docs/design-branches/0001-schema-authority-and-runtime-representation.md`；来源修订 `8ef9fca`，正文保留原时期语义。
> 适用范围：早期 canonical authority / typed projection 候选依据；本文未裁决最终持久 Schema 格式。 今日事实从 [PROJECT-STATE](../../../src/PROJECT-STATE.md) 续接。

> 状态：Deferred
>
> 创建日期：2026-08-27
>
> 当前实现：继续使用强类型 `DurableSchema` 作为实验模型，不把它视为最终 durable authority。

## 问题

Schema 的正常运行时表示是否应该长期保持为强类型 `DurableSchema`，还是由 Generator 嵌入 canonical JSON/string/blob，并只在诊断或工具路径按需解释？

这不是单纯的 class 与 string API 风格选择。它同时涉及：

- 哪种表示是持久化 Schema 的唯一 authority；
- 同一 `(SchemaId, Version)` 如何做 exact mismatch 判定；
- generated codec 如何证明只解释与自己完全匹配的历史 Schema；
- 字段遍历、升级、救援和 Coding Agent 阅读是否属于正常路径需求；
- 是否值得为每个 durable type 的一次性静态初始化优化分配。

## 已确认约束

1. Schema key 使用 ordinal、case-sensitive 的 `(SchemaId, Version)`。
2. Field 声明顺序不属于 Schema 语义；FieldId 必须唯一，TypeTag 必须可解释。
3. 同一 key 只能对应一个规范 shape；任何差异都必须 fail closed。
4. 跨进程 identity 必须来自带 format version 的 canonical bytes 和稳定 hash，不能使用 CLR `GetHashCode()`。
5. malformed、noncanonical、unknown-tag 和 unknown-version 必须在 payload decode 前失败。
6. historical payload 只能交给与 exact historical Schema 绑定的 decoder/materializer。
7. 系统只能有一个 Schema authority；typed view、JSON/text 和 diagnostic display 都只能是可验证派生物。

## 当前证据

- 当前 Generator、`InMemorySchemaStore` 和测试直接消费 `DurableSchema`。
- EXP-005 已生成 boxed serializer，并由 `InMemoryStateStore` 在调用它前比较 typed Schema；由于仍无 canonical blob/hash binding，这只证明当前模型能支撑 demo，尚不能裁决长期 runtime representation。
- 强类型模型已经验证字段排序、构造期校验、结构相等和 field-level inspection。
- 目前没有持久化 SchemaStore、canonical format、SchemaHash、generated codec registry 或历史数据。
- 当前 typed Schema 每个 durable type 只进行一次静态初始化；没有启动时间或内存瓶颈测量。
- 编译期 string literal 可能减少对象数量，但每个完整 Schema string 通常仍是唯一字符串；磁盘读取的历史 Schema 也不会天然共享 literal identity。
- raw JSON 的 ordinal equality 只有在严格 canonical profile 已定义时才等价于 Schema equality。

## 候选 A：强类型逻辑模型长期常驻

```text
attributes
    -> Generator
DurableSchema
    -> canonical codec
canonical bytes/hash
    -> persistent SchemaStore
```

优点：

- 正常路径无需解析即可遍历字段和产生精确诊断。
- 构造器与类型系统封闭非法 FieldId/TypeTag。
- 在选择 wire format 前保持可逆。

代价：

- 每个 Schema 存在对象和字段集合分配。
- 若 generated codec 已包含全部字段知识，runtime descriptor 可能成为重复信息。

## 候选 B：Canonical blob + exact generated codec

```text
Generator internal IR
    -> GeneratedSchemaBinding
       - SchemaKey
       - canonical blob
       - exact generated codec

load:
stored blob
    -> exact binding/blob comparison
    -> exact codec
    -> explicit upgrade edge
```

优点：

- 正常路径不需要 Schema parser、DOM 或 `DurableSchema` 分配。
- byte-for-byte comparison 直接贴近最终 mismatch authority。
- canonical JSON 可同时具备嵌入程序集与人工阅读优势。

代价与前提：

- 必须先冻结 format version、编码、属性/字段顺序、数字、转义、Unicode、duplicate-key 和 unknown-field 规则。
- blob 与 codec 必须从同一个 Generator IR 产生；codec 只有在 exact blob match 后才可执行。
- field-level diff、通用救援或动态 Schema consumer 需要额外的严格 inspector。

## 候选 C：Canonical authority + lazy typed projection

```text
canonical bytes/hash       durable authority
    -> strict validation
    -> cached DurableSchema projection when requested
```

它保留 B 的正常路径，也允许 CLI、救援、动态 migration 或差异诊断按需获得结构化视图。若这些消费者长期不存在，projection 可以删除；若它们成为核心需求，则可以提升为正式能力。

## 当前收敛

- 现在不替换 `DurableSchema`，因为 JSON/blob wire format 尚无真实消费者，立即替换只会提前冻结格式。
- 不继续仅凭未来想象扩张 `DurableSchema` 的公共 API；它目前是可被后续实验淘汰的 scaffolding。
- 不让 Generator 分别维护 typed Schema 与 JSON literal 两套独立语义。
- JSON 可作为未来 canonical encoding 的候选，但“任意 JSON string”永远不是合法 authority。
- Coding Agent 可读性应通过 canonical text 或 deterministic inspector 提供，不应单独决定 runtime object model。
- 分配差异等待真实 Schema 数量、启动成本和内存数据，不提前优化。

## 重访触发条件

满足任一条件时重开本分叉：

1. 开始定义首个 persistent SchemaStore 或 canonical SchemaHash。
2. Generator 开始产生与 canonical blob/hash exact 绑定的 serializer/deserializer/materializer。
3. 版本升级流程需要按 FieldId 动态遍历历史 Schema。
4. CLI、救援或 Coding Agent 工具出现真实 field-level diff/inspection consumer。
5. 测量证明 static typed Schema 初始化或常驻内存形成瓶颈。

## 建议裁决实验

在首次 exact codec vertical slice 中尝试候选 B：

1. 从 Generator 的单一内部 IR 产生带 format version 的 canonical blob 和 exact codec binding。
2. 让内存 Store 以 exact blob 判定同 key mismatch。
3. 跑通 unknown version、blob mismatch、missing codec 和 missing upgrade edge 的 fail-closed 流程。
4. 只在异常/CLI 路径加入严格 inspector，观察正常路径是否仍需要 `DurableSchema.Fields`。
5. 若没有真实正常路径消费者，删除或降级 runtime typed descriptor；否则转向候选 C。

## 相关材料

- `docs/DurableGraph-target-design-v0.md` 中的 VersionedSchema、canonicalization 与 SchemaStore 章节。
- `docs/DurableGraph-lab-notebook.md` 中的 EXP-002、EXP-003 与 EXP-004。
- `src/DurableGraph/DurableSchema.cs`
- `src/DurableGraph/InMemorySchemaStore.cs`
- `src/DurableGraph.Generator/DurableSchemaGenerator.cs`
