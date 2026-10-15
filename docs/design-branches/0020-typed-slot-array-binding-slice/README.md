# DB-020：typed 值槽位与数组元素 binding 分片

> 历史迁移（2026-10-14）：原路径 `docs/design-branches/0020-typed-slot-array-binding-slice.md`；来源修订 `8ef9fca`，正文保留原时期语义。
> 当前适用范围（2026-10-15）：仅保留 SZ/rank-2 ref 元素循环的测试机制见证；[ValueSlotCodec](../../../tests/DurableGraph.Serialization.Tests/TestHelpers/ValueSlotCodec.cs)、[ArrayElementCodec](../../../tests/DurableGraph.Serialization.Tests/TestHelpers/ArrayElementCodec.cs) 已随 primitive helper 移入测试，Serialization 不再提供这套 internal 路由。迁移验收见[维护状态 M006](../../../maintenance/2026-10-14.md)，当前产品的静态 SG body 与数组对象能力从 [PROJECT-STATE](../../../src/PROJECT-STATE.md) 续接。以下正文保留原时期实现与证据。

> 状态：Chosen / Implemented — 2026-09-05，根构建、全部产品测试与独立审查通过。
>
> 基线：`29a4704`；承接 [DB-018](../0018-generated-graph-codec-shape/0018-generated-graph-codec-shape.md)。属于 P1 的执行机制子片。

后续校准：用户指出已知成员类型应由 SG 直接静态绑定。PrimitiveSlotCodecs 已移入
[测试共享工具](../../../tests/DurableGraph.Serialization.Tests/TestHelpers/PrimitiveSlotCodecs.cs)，
保留原调用与测试；它不是产品 SG 的必经接口。ValueSlotCodec/ArrayElementCodec 本次保留。

## 问题与选择

同一强类型值 body 能否配合现有 BinaryPayload Reader/Writer，直接操作局部变量、字段、
SZArray 和二维数组元素，并在只知道运行时闭合类型时取得可复用的数组 binding？

选择先在现有 Serialization leaf 落地内部值槽位/数组元素机制。直接生成 class serializer 会同时要求
string 引用表、历史 decoder 和 SchemaOnly 能力边界，本片先不承担这些依赖。
不新增程序集、Schema authority、注册框架或长期 wire/API 承诺。

## 接缝与范围

- 全部新增 API 为 internal，使用现有 namespace；不公开 Reader/Writer，不增加 friend 或项目依赖。
- `ValueSlotCodec<T> where T : struct` 持有 `ReadSlot<T>` / `WriteSlot<T>` 委托：
  `void (ref BinaryPayloadReader reader, ref T value)` 与对应 Writer 签名。
  委托决定内容合同；复合值需要调用方显式提供 body，不反射发现成员或按 CLR 内存复制。
  body 是受信任代码：写出应保持源值，ref 签名不强制其只读；带引用的 struct 仍需未来的图上下文。
- 非泛型 `ValueSlotCodec` 提供 ValueType、BindVector、BindArray2；typed 实现直接创建含 T 的数组模板。
  已闭合 slot 不再反射一次。每次返回独立 binding，调用方复用；没有 Type-only 全局 body cache。
- 测试工具 `PrimitiveSlotCodecs.Get(Type)` / `Get<T>()` 仅查固定清单：bool、byte/sbyte、short/ushort、int/uint、
  long/ulong、char、Half、float、double。复用既有原语，char 按 UInt16 code unit，允许代理项。
  未知类型抛 NotSupportedException；null Type 抛 ArgumentNullException。
  不因此扩大 DurableSchema/历史生成器当前四种 kind 的支持范围。
- `ArrayElementCodec` 提供 ArrayType、WriteElements(ref writer, Array)、ReadElements(ref reader, Array)。
  精确验证实际数组类型后只做一次强转；循环逐元素传 ref，无 GetValue/SetValue、DynamicInvoke 或元素装箱。
- SZArray 索引递增；固定 rank 2 末维最快，支持 CLR 已分配数组的非零下界；循环用 length/offset 避免上界加一溢出。
  空维度不调用元素 body。其他 rank、非 SZ rank 1 不提供 binding。
- 本片只编码元素序列，不编码长度/下界/ID/类型头，不分配或发布数组。
  每次 Read 只读给定目标的元素数，完整 payload 边界由外层 EnsureFullyConsumed 检查。
  失败可以留下已读元素/游标，写失败也不回滚；调用方应使用未发布目标及稳定保存视图。
- string/其他引用不进入 primitive slot；不增加引用上下文或声称支持带引用的 struct。
  测试手写 Cell<T> body 只作未来 SG 输出形状见证，不是一般 struct/泛型 Schema 支持。

## 任务与验收

| 要求 | 所有者 | 验收 |
|---|---|---|
| typed slot、primitive lookup、数组元素 binding | runtime agent：Serialization 新增文件 | 所有入口实现，既有原语字节不变，无新依赖 |
| primitive 边界与数组失败语义 | tests agent：独立测试文件 | golden、浮点位、char、精确类型、空维度/下界、部分失败、不同 codec 不串用 |
| runtime 闭合的复合值见证与集成 | 主代理 | Cell<T> 开放工厂运行时闭合，同一 body 用于字段/local/SZ/MD，无元素装箱或反射 |
| 独立审查 | 只读 reviewer | 对照本文及源码，无未解决正确性阻塞项 |

完成门槛：根 solution build、Serialization 与核心相关测试通过、diff 检查、更新产品工作集/笔记及设计索引、提交本片。
本片不实现 SG body、完整数组对象 codec、TypeCodec、图恢复、历史 binary decoder、BCL 容器或 Storage/Save 接入。

## 完成证据

- 最初实现位于 Serialization 项目的三个文件；PrimitiveSlotCodecs 随后移至测试项目，runtime 保留
  ValueSlotCodec/ArrayElementCodec。未修改原语算法或新增程序集/项目依赖；上表要求均已验证。
- 独立 golden 覆盖 13 种 primitive、char 代理项与浮点负零/NaN 位；同 CLR Type 的两个 body 不会混用。
  数组循环验证非零下界、空维度、int.MinValue 下界/int.MaxValue 上界、精确类型预检与部分失败。
- RuntimeGenericSlotBindingTests 使用真实 Reader/Writer、运行时 MakeGenericMethod/CreateDelegate 工厂，
  闭合 Cell<Cell<int>> 和 Cell<double>；同一嵌套 codec 修改 local/字段/SZ/MD 真正槽位。
  这是手写开放 body 的组合证据，不是 SourceGenerator 已支持泛型成员。
- 主代理最终 `dotnet build DurableGraph.slnx --verbosity quiet`：0 warnings / 0 errors。
- 主代理最终 `dotnet test DurableGraph.slnx --no-build --verbosity quiet`：431/431，全部无跳过：
  Serialization 94（开工 65）、Storage 73、StateStore 40、DurableGraph 224。
- 独立只读审查无阻塞；建议的极端数组边界测试已补齐并通过。diff 检查与相关文档链接检查通过。
- 产品工作集、目标设计、DB-018、索引与实验簿已校准；后续按实际生成代码消费者开放所需边界。
