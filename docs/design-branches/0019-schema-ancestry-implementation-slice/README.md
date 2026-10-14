# DB-019：祖先 Schema/history 产品分片

> 历史迁移（2026-10-14）：原路径 `docs/design-branches/0019-schema-ancestry-implementation-slice.md`；来源修订 `8ef9fca`，正文保留原时期语义。
> 适用范围：声明段、exact 祖先与版本传播的已验收 metadata 分片；当片不包含继承 serializer 或完整图 codec。 今日事实从 [PROJECT-STATE](../../../src/PROJECT-STATE.md) 续接。

> 状态：Chosen / Implemented — 2026-09-05，按用户“规划下一工作分片并实施”的授权完成；构建、测试及独立审查通过。
>
> 依据：[DB-018](../0018-generated-graph-codec-shape/0018-generated-graph-codec-shape.md) 的祖先 exact 依赖；这是 metadata 分片，不是继承 serializer。

## 目标与边界

闭合 Base/Middle/Leaf 的 Schema 生成、accepted history、发布校验与运行时 exact 祖先链。
同一 SchemaId/Version 的声明字段或直接基类 exact 绑定变化必须拒绝；各层显式升版后接受。
历史 Leaf 必须继续绑定历史 Middle/Base。

新增 `DurableType(..., SchemaOnly = true)`：整个领域继承链均显式选择，同一编译内、顶层、
非泛型、非 record 的 partial class，可 abstract 或非 sealed，最终继承 DurableBase。
生成当前 `Schema` 及 `GetSchema(int version)` 历史查询；不生成 boxed Serializer、typed payload Snapshot 或 Upgrade。
默认路径保持原来的 sealed/direct DurableBase 与 boxed serializer 能力。

FieldId 在声明层内唯一；`Fields` 只描述本层，祖先通过 `BaseSchema` 访问。
本片仍只接受现有 bool/int/long/string kind；string kind 不决定未来图 codec 的 inline/identity 语义。
不实现跨程序集继承、泛型、数组、图 codec、持久 SchemaStore 或 SchemaHash。

## 冻结的协作接缝

- Runtime：保留现有三参 params 构造；新增 `(string schemaId, int version, DurableFieldInfo[] fields, DurableSchema? baseSchema)`，
  避免 `null` 与 params 重载歧义。`BaseSchema` 是 immutable exact schema 对象，天然携带完整祖先闭包。
  禁止祖先重复 SchemaId（即使版本不同）。equality/hash 包含祖先；hash 仍不是持久 hash。
- 内存 Store：先检查整条链与现存 key/shape 是否冲突，全部通过后 base-first 登记；失败不留下部分祖先。
- History：保留原型 v1 header，在 version 行后可选一行 `// base:<canonical UTF-8 Base64 SchemaId>|<positive canonical version>`。
  无 base 时省略。已有无继承输出字节不变；旧解析器会拒绝新 base 行，不会把它误当平面布局。
  不另建兼容迁移层。这个原型扩展不是长期 wire 承诺。
- SG 与 publisher 都将 base 纳入 shape 比较，并验证 exact 祖先闭包：缺失、同 key 冲突、重复祖先身份必须拒绝。
  历史依赖必须由 accepted history 自身闭合，不能拿今天的 candidate 补历史缺口；当前 candidate 可互相引用或引用 accepted history。
  publisher 在创建任何 snapshot 文件前完成整批逻辑校验；不声称文件系统批次崩溃原子性。
- SchemaOnly 历史查询仅接受 `1..currentVersion`，越界抛 ArgumentOutOfRangeException；历史表达从 accepted shape 构造，
  不调用今天基类的 Schema 属性。默认 legacy 分支遇到带 base 的历史必须拒绝。

## 任务与验收映射

| 要求 | 负责范围 | 验收 |
|---|---|---|
| immutable exact chain、原子登记 | runtime agent：DurableSchema/InMemorySchemaStore/attribute 与独立测试 | 三层绑定、字段局部重号、祖先冲突、失败无部分登记 |
| schema-only 生成与历史传播 | generator agent：Generator 与专用测试 | 基类升版逐层报 mismatch、全升版通过、旧链不漂移、默认路径保留 |
| canonical base 记录与发布闭包 | history agent：Build 与专用测试 | 往返/golden、缺失/环/冲突拒绝、失败不写文件、顺序无关 |
| 跨模块集成与最终验收 | 主代理 | SG manifest → publish → history → SG → runtime 三层见证；根 build、DurableGraph.Tests |
| 独立审查 | 只读 reviewer | 无未解决的正确性阻塞项 |

## 完成证据

- Runtime 使用现有 DurableGraph 程序集；Generator 的 ancestry 实现放在同类 partial 文件，Build 没有新增依赖。
- 新 DG0019 表示非法祖先、历史闭包缺失或默认 serializer 误用继承历史；同版本改变继续使用 DG0015。
- 上表全部要求已验证。跨模块测试实际执行 SG manifest → publish → accepted history → SG → runtime；
  Base/Middle/Leaf 逐层升版、历史文件保持不变、旧 CLR 基类从源码移除后仍可查询旧链均通过。
- `dotnet build DurableGraph.slnx --verbosity quiet`：0 warnings / 0 errors。
- `dotnet test tests/DurableGraph.Tests --no-build --verbosity quiet`：224/224（开工基线 147/147），无跳过。
- 独立只读审查无阻塞项；补齐被字段错误/重复 SchemaId 剔除的祖先不能导致后代生成异常的反例。
- `git diff --check` 通过；产品工作集、DB-018、分叉索引、package 说明与笔记已校准。

本片未修改 Storage/Serialization，也未将 SchemaOnly 元数据误记为继承序列化能力。
保留开工前未提交文档；本片与前序设计工作集随后一起提交为 `29a4704`。
