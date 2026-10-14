# DB-005：Durable 继承展平

> 历史迁移（2026-10-14）：原路径 `docs/design-branches/0005-durable-inheritance-flattening.md`；来源修订 `8ef9fca`，正文保留原时期语义。
> 适用范围：早期继承取舍与 metadata 分片；后继实施证据见 DB-019、DB-022、DB-023，不以本文旧缺口描述今日能力。 今日事实从 [PROJECT-STATE](../../../src/PROJECT-STATE.md) 续接。

> 状态：Chosen（metadata 分片）— DB-019 选择声明层 FieldId 分段与 exact 祖先；继承 codec/升级仍待后续分片。
>
> 创建日期：2026-08-27
>
> 默认 serializer 仍要求 sealed/direct DurableBase；显式 SchemaOnly 已支持同编译领域继承的元数据。

## 问题

如果未来允许 durable domain class 继承其他 durable class，Snapshot 和 exact Schema 是否应展平完整继承链？这会同时改变 FieldId scope、private field access、base evolution、leaf versioning 和 handler visibility。

## 当前事实

- 默认 Generator serializer 只接受 sealed class 直接继承 `DurableBase`；SchemaOnly 生成声明层 Schema 与历史查询。
- 当前 Schema 的 Fields 是本声明层，BaseSchema 携带 immutable exact 祖先链；本层 FieldId 可与祖先重复。
- Source Generator 能读取 base private field symbol，但生成在 derived partial class 中的代码不能访问 base private storage。
- 当前 consumer 是 Schema/history；继承字段内容的 base-first codec 尚未实现。

## 候选 A：每个 leaf Schema 完全展平

所有 base/derived durable slots 进入 leaf 的一个 flat Snapshot。

建议约束：

- FieldId 在整个 flattened `(SchemaId, Version)` 内唯一；base/derived 相同 FieldId 是编译错误。
- Snapshot member 继续使用 `Field{FieldId}`，CLR field 重名不构成碰撞。
- 每个声明 durable fields 的 source class 必须是 partial，并由 Generator 在其自身 scope 内生成 capture/hydrate adapter；derived binding 组合这些 adapters，而不是直接访问 base private fields。

尚未解决：base shape 改变是否强制所有 leaf Schema 同步 bump，以及 referenced-assembly base adapter 的可见性/兼容契约。

## 候选 B：FieldId 按 declaring type 分段

持久 identity 变成 `(declaring schema/type segment, FieldId)`，允许 base/derived 重复 FieldId。

优点：base 局部编号独立。

代价：payload Snapshot/State fields 和 codec 必须表达分段。DB-019 的 metadata 用声明 SchemaId 表示段，
没有引入独立 segment ID，也没有把继承硬接到旧平面 boxed State consumer。

## 候选 C：不支持继承，改用 durable composition

领域通过包含 durable value/reference component 复用状态，而不是继承存储布局。

优点：Schema ownership 和 private state 边界更清楚。

代价：领域模型可能需要额外转写；当前 durable references/value components 也尚未设计。

## 必须回答的问题

1. FieldId 的唯一性 scope 是 leaf Schema 还是 declaring segment？
2. base field 新增/删除/改类型时，哪些 leaf Version 必须 bump？
3. source base 与 referenced-assembly base 的 private fields 由谁 capture/hydrate？
4. upgrade handler 是否应看到 base private durable bits，还是只能通过生成的 projection？
5. 同一 base 被多个不同 SchemaId leaf 复用时，history 由谁拥有？
6. 构造/反序列化时 base 与 derived invariant 的恢复顺序是什么？

## 历史裁决与本轮重访

早期因没有具体消费者而保持继承关闭，Snapshot struct 实验只处理当前类型直接声明的 fields。
用户现已提出 base-first，并同意祖先 exact Schema 变化要求派生版本递增；
[DB-018](../0018-generated-graph-codec-shape/0018-generated-graph-codec-shape.md)提出候选 B 的声明 Schema 分段，每层访问自己的 private fields。
[DB-019](../0019-schema-ancestry-implementation-slice/README.md)现已选择并实现 metadata 范围的候选 B，
覆盖 runtime、生成器、accepted history 与 publisher；继承 codec、升级和跨程序集 helper 仍待后续消费者收敛。

## 重访触发条件

- 出现首个无法合理改写为 composition 的 durable inheritance 领域模型。
- durable value/reference composition 已有具体设计，可以做真实对比。
- 需要继承 referenced assembly 中已发布的 durable base。

## 相关材料

- `src/DurableGraph.Generator/DurableSchemaGenerator.cs`
- `docs/design-branches/0003-snapshot-value-shape-and-upgrade-signature.md`
