# DB-017：对象 codec 设计要点与逐项决策草稿

> 历史迁移（2026-10-14）：原路径 `docs/design-branches/0017-object-codec-design-points.md`；来源修订 `8ef9fca`，正文保留原时期语义。
> 适用范围：早期草稿已由 DB-018 接续；string 字段 inline 方案被取代，Robird 证据保原语义。 今日事实从 [PROJECT-STATE](../../../src/PROJECT-STATE.md) 续接。

> 状态：Superseded — 整体图模型与生成器形状转入 DB-018；本文保留早期 scalar 要点及 Robird 证据。
> 用户后续明确 string 也保留引用身份，容器统一编号；不再采用本文最初的 string 字段 inline 方案。
>
> 创建日期：2026-09-05；代码基线：`c8a98dc`。
>
> 承接 [DB-016](../0016-next-product-object-content-slice/README.md)。本文是讨论清单，不是全功能施工授权。

当前阅读入口：[DB-018](../0018-generated-graph-codec-shape/0018-generated-graph-codec-shape.md)。K4 的槽位独立 codec 与第 12 节旧实现证据继续有效；
后续 string 语义、TypeCodec 对象头、base-first 继承和统一引用编号以新稿为准。

## 1. 已确定方向和讨论顺序

| 项目 | 状态 |
|---|---|
| 下一层先做单对象 Base 二进制编解码，从支持的 CLR 基础类型开始 | 用户已选择；具体类型清单待定 |
| string 保存 inline 内容，不做 intern symbol | 旧字段值方案已取代：成员保存 ID，string 对象记录保存内容，不作内容去重 |
| 后续允许 Durable 类型互相引用，包括循环 | 用户已提出后续目标，非首片内容 |
| 后续支持 .NET VectorArray 与 MultiDimArray；SG 生成遍历循环，逐元素按 ref 调用 accessor | 用户已提出后续目标；具体 accessor 和数组形状合同待定 |
| ref 的核心用途是让 struct codec 不区分字段与数组元素等存放位置，配合 Writer/Reader 原地访问 | 用户已澄清；不等于要求读写共用一个 mode visitor |
| 后续 BCL 容器保存并重建内容，不序列化内部实现 | 用户已选择原则；identity、comparer、顺序等仍需定义 |
| 首片仍为 current exact Schema、Base-only；不接完整 Save | 沿用已接受的下一片方向 |

优先讨论 K1–K4，再敲定 K5–K6，即可形成 primitive 首片的实施规格。
K7–K9 先列出将影响后续兼容的语义分支；不用为了它们现在实现容器或通用类型系统。

| 编号 | 需要决定什么 | 草稿推荐 |
|---|---|---|
| K1 | 基础类型支持范围 | 显式类型清单；固定宽度整数类型、bool、char、float/double、string |
| K2 | 同值与同字节、null/string 语义 | 复用现有原语；浮点保留位，string 保留 UTF-16 code units 与 null |
| K3 | 一个 Base payload 如何解释 | exact Schema 外部绑定，按 FieldId 顺序写字段值，不逐字段重复类型/ID |
| K4 | SG 与 accessor 如何分工 | 外层取得 ref 槽位；typed codec 分别读/写该值，不关心存放位置 |
| K5 | 计数、冻结视图、读失败 | 同一视图计数/写出；底层可原地读入，外层控制暂存和发布 |
| K6 | Schema 与程序集接线 | 扩展现有 Schema/生成器/历史解析，开放最小原语边界 |
| K7 | Durable、数组和容器的 identity | 用户已明确统一编号，string 也参与；见 DB-018 |
| K8 | 数组形状与逐元素 ref | 分清 SZArray/MDArray/jagged；保留维长，lower bounds 支持范围待定 |
| K9 | BCL 容器的内容等价 | 容器类型逐个定义，明确顺序、comparer、键稳定性，不泛化内部布局 |

## 2. K1：基础类型范围

不要直接以 `Type.IsPrimitive` 或“所有 struct”作为产品支持合同；使用一份可以测试的清单。

| 建议首片类型 | 字节层现状 | 待定点 |
|---|---|---|
| bool | 已有严格 0/1 编码 | 推荐直接复用 |
| byte/sbyte | 已有单字节编码 | 推荐直接复用 |
| short/ushort、int/uint、long/ulong | 已有 canonical VarInt；signed 使用 ZigZag | 固定的是 CLR 数值宽度，不是固定长度 wire |
| char | 可按 ushort 值复用原语，目前没有专用 schema kind | 推荐保留单个 UTF-16 code unit，允许代理项；不解释为 Unicode scalar |
| float/double | 已有 little-endian 位保持读写 | 见 K2 |
| string | 已有 nullable/非 nullable 的 inline codec | 统一选择见 K2 |

Half 的原语已有，但是否列入首片请明确；decimal、Int128/UInt128、enum、Nullable<T>、nint/nuint 也单列讨论，
不借“primitive”一词自动包含。推荐这些先后续：decimal 有 scale/位模式，enum 有 underlying type，
Nullable<T> 有 presence，native int 有平台宽度，需要各自说清。Guid/时间类型和一般值类型再按消费者增加。

**影响范围**：目前 [TypeTag](../../../src/DurableGraph/Schema/TypeTag.cs)仅 Boolean/Int32/Int64/String 四项。
支持更多标量不只是添加 reader/writer，还要同步 current/historical Snapshot 类型映射、boxed 旧路径、
Schema 校验与 history publisher/parser。不能让新增类型能编译但其历史描述无法发布或读取。

## 3. K2：值语义、浮点与 inline string

推荐保留以下已有原语语义：

- 整数不溢出截断；使用已有 canonical 编码，拒绝 overlong/非法值。
- float/double 保留原始位，包括负零、NaN payload。它们数值比较可能相等而字节不同；
  此时“确定性”指同一字段位模式产生相同 bytes，不声称所有数值相等值有相同 bytes。
- 后续 Delta 的 unchanged 判断要与这个合同一致：若要求位保持，就不能仅用普通浮点数值相等跳过修改。
  这是后续约束，不要求现在实现 Delta。
- string 内容仍按 UTF-16 code units 保留；用户后续明确将图内 ReferenceEquals 纳入恢复保证。
  相同源实例必须恢复共享，不同源实例不能因内容相等被合并；字符串对象 body 才 inline 写内容。

inline 并不等于必须选 UTF-8。现有 [StringPayloadCodec](../../../src/DurableGraph.Serialization/StringPayloadCodec.cs)
在 strict UTF-8 与 UTF-16LE 中选较短者，平局选 UTF-16LE，并保留孤立代理项。
推荐首片沿用，避免第二套字符串语义。

**已取代的方案**：不再把 nullable string 内容直接写在每个成员处。
新稿在引用 ID 层区分 null，非 null string 记录写内容；nullable annotation 的验证规则仍可独立讨论。

## 4. K3：Base 布局与 exact Schema

建议首片采用：

`exact current Schema + 有边界的 payload span -> 对应 generated codec`。

字段按 FieldId 升序连续编码；Base 不重复每个 FieldId、TypeTag 或字段名。
因此解码前必须 exact 比较 SchemaId、Version 和全部字段 shape，并在结尾拒绝 trailing bytes。
字段声明顺序改变而 FieldId 与类型不变时，不改变 payload；同 key 改 shape 不能继续解释旧 bytes。

Schema 初期由调用方显式提供，不为首片建立 SchemaHash、动态 registry 或持久 SchemaStore。
bytes 不是自描述文件格式；将来 ObjectVersion 的 envelope 才承担其边界和 codec/schema 绑定。
payload encoding revision 与领域 SchemaVersion 是两个概念，不能因为字段 shape 未变就任意改变已有字节解释。
实现前需决定首个 provisional encoding revision 的标识归属；首片可由固定 codec 入口隐含选择，
未来持久 envelope 再显式记录，不能把这个简化当成永久格式保证。

备选是逐字段 TLV，优点是更容易跳过未知字段，代价是额外 bytes 和未知字段策略。
目前使用 exact Schema，没有当前需求要静默忽略未知字段，因此推荐顺序布局。

## 5. K4：面向 ref 值槽位的 typed codec

用户澄清的核心是：**某个 struct 的 codec 只需要该值的地址，不需要知道它来自字段还是数组元素。**
旧稿把主要价值放在读/写/计数共用遍历上，现作修正；是否共享生成代码仍是可选实现细节。

建议分工：

| 层 | 负责什么 | 不必知道什么 |
|---|---|---|
| 字段/数组的外层生成代码 | 按 Schema 遍历字段或生成索引循环，取得真实 typed 槽位 | 不重复展开每个值类型的内部编码 |
| 某个值类型的 codec | 给定 ref 值及 Writer/Reader，处理该值的受支持成员 | 不关心槽位属于哪个对象字段、数组元素或临时值 |
| BinaryPayload Writer/Reader | 标量与字符串的字节语义、缓冲区/读取游标 | 不理解 FieldId、ObjectId 或图遍历 |

概念调用如下；`Point` 只是未来复合 struct 的示意，不表示本轮扩大了支持范围：

```csharp
PointCodec.Write(ref writer, ref owner.Position);
PointCodec.Write(ref writer, ref points[i, j]);

PointCodec.Read(ref reader, ref staging.Position);
PointCodec.Read(ref reader, ref unpublishedPoints[i, j]);
```

同一个 Point codec 能处理上述位置，外层无需先装箱或复制出整个元素再回填。
primitive codec 内部仍可直接使用 `writer.WriteInt32(value)` / `reader.ReadInt32()`，
不要求每个底层标量方法也改成 ref 签名。复合 struct 包含引用时，应把引用处理交给图层合同，
不能由“struct 可按 ref 访问”推导出可以原样复制 CLR 内存、padding 或对象引用。

**修订后的推荐是独立 Write/Read 入口，共享同一份字段类型模型。**
旧实现也是 `Serialize(ref T)` 与 `Deserialize(out T)` 分离，而非统一 mode visitor。
完整值读入采用 `ref T` 还是 `out T` 仍可在具体接口设计时选择；未来 partial Delta 的原地修改是另一合同。
写出与计数即使拿到 ref 也不应修改值；对 readonly 存放位置是否改用 in 或临时值，按实际支持范围处理，
不承诺任意 property/readonly 字段都能直接作为可写 ref。

旧 accessor 的接口类别、DynamicMethod delegate、Init/cached context 不直接移植。
推荐新代码显式向局部调用传入当前 BinaryPayload Reader/Writer，保留同一 reader 游标，
避免把旧的“引用类型 Reader 保存在 accessor 实例里”机械套到当前 ref struct Reader 上。
是否还需要 accessor 对象、泛型约束或共享循环，等真实生成代码裁决；不先创建一组类型工厂。

## 6. K5：冻结视图、大小计算与 materialization

早期建议是捕获一次 typed Snapshot，让 Measure/Write 消费同一份字段值；
新稿 string 字段捕获的是引用身份，完整图 discover/write 一致性见 DB-018。
这不自动提供多线程一致性：捕获期间调用方仍须保证对象不被并发修改。
未来数组/容器是可变引用，浅拷贝 Snapshot 并不能冻结它们，届时单独确定 mutation/保存视图合同。

大小计算推荐与实际字节数精确一致，作为首片很容易检验的标准；
共享 VarInt/string 的长度选择逻辑，不复制容易漂移的公式；建议避免为了计数提前构造完整 payload，
但这项效率选择尚不是用户已确定的硬约束。
policy 仍允许近似 B/D，本设计不反向提高其输入要求。

**底层原地读入与外层失败隔离分开定义。**
底层 `Read(..., ref T slot)` 允许逐字段填充所给槽位；失败时不默认承诺恢复该槽位或 Reader 游标。
外层把槽位放在本次私有 Snapshot、新对象或尚未发布的数组里，失败则丢弃本次暂存结果。
需要“不修改已有可见对象”的入口，必须先用外层暂存再安装；不能直接传入已有对象字段后声称原子更新。

对 primitive 首片，仍推荐先填充临时 typed Snapshot，验证完整 payload 后才创建当前 durable 实例。
这个失败隔离单位不要求每个嵌套 struct codec 都再复制一份完整值；字段和数组元素可在同一未发布边界内原地填充。
推荐暂时沿用当前生成器的构造约定：不执行领域构造函数，填 durable 字段，
Transient 初始为默认值；不承诺领域 invariant 或 RebuildTransient 已经执行。
是否引入用户 materialization hook 是可单独讨论的产品行为，不混入底层 accessor。

失败不得返回半成品对象；这不等于任意 IBufferWriter 写失败都能回滚。
首片 byte buffer 的所有权/返回方式要明确，持久 append 和发布失败由后续 Save/Storage 边界处理。

## 7. K6：Schema 与产品接线

- 复用现有 DurableSchema 和统一生成器字段模型。新增 kind 的 ID、历史描述接受规则需一致，
  不为 binary codec 私建一份与 Schema 无法对照的类型表。
- 旧 TypeTag 1–4 的语义保持明确；新增 tag 是否需要 snapshot protocol revision 是实施前待定项，
  不引入迁移框架或默认“必须兼容所有旧工具”的要求。
- 历史类型的生成/描述必须能认识新增基础类型；这与“本轮提供 historical binary decode/upgrade”
  不同，后者仍不实施。
- generated code 编译在下游程序集，现有 internal 原语不可直接访问。
  推荐开放既有 Serialization leaf 的最小 scalar reader/writer 边界，并验证下游引用传递；
  不复制 primitive 算法到生成模板，不通过给任意消费者添加 friend 绕过产品接线。
- 保持 Storage 不理解 CLR 字段；新 codec 不依赖 StateRevision、FrameAddress 或策略参数。

## 8. K7：后续引用与 identity

用户后续已明确采用**所有受支持引用类型的统一 identity**，包括数组、BCL 容器和 string。
两个字段指向同一个 List，加载后仍共享该 List；“按内容编码”不消除引用身份。具体形状见 DB-018。

Durable 引用则按 ObjectId 保存，局部对象 codec 不递归嵌入目标对象。
同一实例用同一 ID，null 单独编码；发现对象、分配/保持 ID、决定具体类型都属于图编排。
identity 表须显式按引用身份比较：两个重写 Equals 后“值相等”的不同实例仍应获得不同 ID。
旧源码的默认 IndexedSet<object> 使用一般相等比较，不能直接搬成 Durable identity 表。
静态字段类型与实际目标类型必须相容；是否支持基类/接口多态另定，不能从“互相引用”自动推导。

循环引用的建议恢复步骤是先建立需要的对象壳及 ID 映射，再填充字段引用，最后验证并对外发布。
首片标量的“验证后才 materialize”在图阶段应改为“未完成图不对外发布”，
不能要求所有被引用对象必须先完全构造好，否则无法恢复循环。

对象 ID 的分配是否跨遍历顺序确定，与“固定 ID 下 payload bytes 确定”是不同保证；
不把 canonical graph labeling 作为循环引用支持的隐含前置。

## 9. K8：数组支持范围

VectorArray 在此暂按 CLR SZArray（常见 `T[]`）理解；MultiDimArray 按多维数组理解。
还需要明确是否覆盖非零 lower bound，以及 rank 为 1 的非 SZArray；不能只保存 Length 就说支持所有 CLR 数组。

推荐依次处理：

1. SZArray：保存 length，按索引递增逐元素 ref；区分 null 与空数组。
2. MDArray：保存每一维的长度；若支持非零 lower bound，还保存各维下界。
   SG 为已知 rank 生成 typed 多层循环，末维变化最快，逐元素按 ref。
3. jagged array：它是数组引用数组；依赖 identity/引用语义，保留共享内层数组，不当作矩形数据块摊平。

元素 codec 复用 K4 的 typed ref 槽位边界；数组外层负责 length/bounds/循环和实例登记。
旧实现已有分别取字段地址、向量 ref/out 元素和二维数组 ref/out 元素的调用，见第 12 节。
这提供结构参考，但不自动采纳其 rank 上限、非零下界 wire 或 rank-1 的手写 IL。

完整形状的验收包括空维度、不同 rank/各维长、边界值、共享与循环目标。
推荐后续第一版数组限定已支持的精确 runtime array type；引用数组协变与 writable ref 的相容性需要
专门验证，不能因字段声明为某个 `T[]` 就假定实际数组能安全按该类型取可写 ref。
对非 SZ rank-1 的生成方式及非零下界，先给出编译/运行见证，再宣称支持；当前不引入 unsafe 扁平内存遍历。

独立审查已用本机 C# 见证确认 `ref int[,]` 元素调用可用，以及协变数组即使 accessor 不写也可能
在取得 writable ref 时抛 ArrayTypeMismatchException；这只验证语言机制，没有实现数组 codec。
变量引用与协变规则见 [C# 数组规范](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/language-specification/arrays)，
SZArray 与非 SZ 的分类见 [Type.IsSZArray](https://learn.microsoft.com/en-us/dotnet/api/system.type.isszarray?view=net-10.0)。

## 10. K9：BCL 容器保存“内容”的精确定义

推荐逐个 container shape 支持，不以“能 IEnumerable”就视为可持久化容器。
读出元素可落到临时变量再 ref access，恢复后通过容器的内容 API 插入；
容器没有通用可写元素 ref，这不要求序列化其内部 fields。

| 容器例子 | 要保存/恢复的语义 | 不能遗漏的问题 |
|---|---|---|
| List<T> | 有序元素、重复项、null、共享目标 | Capacity 是实现状态，通常不保存 |
| Queue/Stack | 将来出队/出栈顺序 | 枚举顺序与重建插入顺序未必相同 |
| Dictionary/HashSet | 键值/元素与相等语义 | comparer、重复/冲突、稳定键、输出顺序 |
| Sorted 容器 | 内容及排序语义 | comparer 和恢复依赖，比普通列表更多 |

推荐先 List，再支持明确 comparer 集合的 Dictionary/HashSet；不自动持久化任意 comparer 对象/闭包。
无序容器是否要求“内容等价就字节一致”需要另定：稳定顺序通常需要可定义的排序键，
不能悄悄把 CLR 当前枚举顺序当作跨进程 canonical order。

哈希/排序容器不能简单套用“所有壳创建后立即逐个 populate”。
若 key 的 hash/比较依赖尚未恢复的字段，过早插入后再修改 key 会破坏容器语义；
推荐最先限定稳定 key 类型，或在依赖字段恢复完成后再构建索引。任意带循环的可变 key 是后续专门问题。
comparer、影响 hash 的 key 修改及枚举顺序限制见
[Dictionary<TKey,TValue> 官方说明](https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.dictionary-2?view=net-10.0)。

## 11. 首片验收与非目标

primitive 首片须覆盖：类型边界、独立 golden bytes、字段声明顺序扰动、大小一致、
null/空字符串/UTF-16 内容、浮点位模式、错误 Schema/损坏/尾随输入、Transient 与 materialization、
真正下游程序集使用生成代码。扩大 TypeTag 时还要验证 history 工具能 round-trip 新描述。

当复合 struct/数组进入实施范围时，应增加同一个 codec 处理 local、字段、SZ/MD 元素的见证：
读入修改的是原槽位，嵌套 struct 没有丢失回写，失败不安装未完成结果，写出不改变源值。
这些是后续验收方向，不是本轮已运行的测试。

引用/数组/容器仅记录未来见证，不为了本草稿现在写空接口或未使用的实现。
ObjectVersion 持久内容、Delta、H 恢复及 Save 接入仍按真实消费者推进；
讨论这条能力路线不等于取消 DB-016 中“尽快接入真实内容存储”的目标。

当前事实依据：[primitive writer](../../../src/DurableGraph.Serialization/BinaryPayloadWriter.cs)、
[primitive tests](../../../tests/DurableGraph.Serialization.Tests/Serialization/BinaryPayloadPrimitiveTests.cs)、
[string tests](../../../tests/DurableGraph.Serialization.Tests/Serialization/StringPayloadCodecTests.cs)、
[生成器](../../../src/DurableGraph.Generator/DurableSchemaGenerator.cs)、
[历史解析器](../../../src/DurableGraph.Build/SchemaHistoryTool.cs)。

## 12. Robird 旧实现：本轮吸纳的经验

只读来源：`E:\ElementBackup181125\Source\Workspaces\Robird\Core\Serialization`。
这是用户提供的部分旧实现；本轮没有构建/运行或修改该备份，没有引入源码依赖。
两个独立审查分别核对 struct body 与数组/图边界，主审复查下列具体调用。

| 旧源码证据 | 吸纳到本草稿的经验 |
|---|---|
| [SeriAccessor.cs](E:/ElementBackup181125/Source/Workspaces/Robird/Core/Serialization/SeriAccessor.cs) 8–40；[DeseriAccessor.cs](E:/ElementBackup181125/Source/Workspaces/Robird/Core/Serialization/DeseriAccessor.cs) 9–12、236–248 | ref/out typed 值槽位是共用边界；读写职责分离 |
| [Emitter.cs](E:/ElementBackup181125/Source/Workspaces/Robird/Core/Serialization/Emitter.cs) 27–34、50–56、716–725、852–861 | struct body 接收 byref，字段外层取地址再调用；SG 可以生成等价 C# 结构 |
| [SerializeImpl.cs](E:/ElementBackup181125/Source/Workspaces/Robird/Core/Serialization/SerializeImpl.cs) 493–519、554–583；[DeserializeImpl.cs](E:/ElementBackup181125/Source/Workspaces/Robird/Core/Serialization/DeserializeImpl.cs) 540–568、615–642 | 同一 body/accessor 服务 struct 向量与多维元素；数组 shape/循环属于外层 |
| DeserializeImpl.cs 的数组先登记 Instances 再填元素 | 原地填充可以发生在尚未发布、但已可解析内部循环引用的对象壳里 |
| [SerializationContext.cs](E:/ElementBackup181125/Source/Workspaces/Robird/Core/Serialization/SerializationContext.cs) 9–26；[IndexedSet.cs](E:/ElementBackup181125/Source/Workspaces/Robird/Core/Collections/IndexedSet.cs) 222–235 | 分开引用上下文与值 codec；新 identity 表必须改用引用比较，不能照搬默认 equality |

吸纳的是职责划分与调用机制，不把整个备份视为已验证算法。
`ISerializationAccessor.cs`、`FieldAccessor.cs`、`TypeCodec.cs` 和 FastSerializer 的多处内容是注释草稿，
`DeserializeImpl.Create` 仍直接抛 NotImplementedException；数组分派也有需要重新验证的静态疑点。
因此旧 DynamicMethod/IL、反射字段次序、运行时类型编码、字符串引用压缩和版本转换策略均不直接移植。
string 的旧引用压缩/wire 不直接移植；新稿重新确定统一 ObjectId 与内容 body，
FieldId/exact Schema 和 SG 边界按本项目方向处理。
本次审查未找到足以据此声称 BCL 容器“按内容重建”已实现的证据。
