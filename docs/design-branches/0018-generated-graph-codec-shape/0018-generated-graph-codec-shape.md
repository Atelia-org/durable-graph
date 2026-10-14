# DB-018：统一引用身份、TypeCodec 与生成式 Serializer 形状

> 历史混合草稿（2026-09-06 治理）：本文保留讨论当时的已接受原则、候选与代码示意，正文不再同步产品进度。
> 延续的原则已提取到[目标设计](../../DurableGraph-target-design-v0.md)，未完成问题统一在[路线图](../../DurableGraph-research-roadmap.md)维护。
> 下文“当前阅读入口”仅属历史措辞；现在从[PROJECT](../../../src/PROJECT-STATE.md)续接。直接领域 Read/Write 示例不是现行 DTO 接口，推荐的 binding/Restore 形状也不等于已批准实现。

> 状态：Open — 用户已说明整体语义；本文保留历史形状草稿，签名、格式和实施切片未全部冻结。
> 其中 DB-024 已落地首片 reference/context 实现（session、root Capture/Seal 与 string 引用条目）。
> Restore、stored Schema 到 generated codec 的运行时绑定与一般 durable 循环仍未实现。
>
> 日期：2026-09-05；初始产品代码基线：`c8a98dc`。
>
> 当前阅读入口。取代 DB-017 中“string 字段 inline 值”和“容器身份尚未选择”的提案；
> 历史上图 codec 为设计草图；祖先 Schema/history 的元数据分片已进入产品实现，边界见 [DB-019](../0019-schema-ancestry-implementation-slice/README.md)。
> 随后 [DB-020](../0020-typed-slot-array-binding-slice/README.md)落地 internal 值槽位和 SZ/rank-2 元素循环；
> [DB-021（Git 历史）](../0091-retired-material-cleanup/README.md#git-history "Git 6dbaf0e:docs/design-branches/0021-generated-primitive-body-slice/README.md")落地实际 SG bool/int/long class body 与继承分段；本文的 struct/泛型 body 仍为草图；
> 字符串引用 context 与 root-capture 详见 [DB-024](../0024-reference-capture-and-reusable-object-ids/README.md)。

2026-09-05 后续方向：用户已选择领域图 → Versioned DTO 捕获 → 比较/估算/编码。
[DB-022](../0022-versioned-state-dto-capture/README.md)已把 GenerateBinaryBody 改为 readonly Vn + current Capture + DTO body，
含历史 exact 声明链；下文直接领域 Read/Write 的示例保留为早期机制草图，不能作为当前接口。
引用发现与 Capture 可合并；Capture 期间需要稳定视图，完成后后续步骤应只消费捕获状态。
未来引用 DTO 槽位使用 ObjectId，不能保留可变领域引用；非空 string 对象内容可复用不可变数据而不合并身份。
DB-025 的后续用户裁决明确：空字符串在 Capture/读取两端统一为 string.Empty，不保留其不同实例身份。

本轮重点已收窄：祖先 Schema 不变性/版本传播、nominal 引用声明与 exact 对象类型、
开放泛型/数组 codec 的运行时组合。用户已同意 nominal/exact 的区分，并要求基类变化时派生版本递增；
BCL 集合内容支持明确暂缓，下面相关类型表达只保留为后续设计位置。

后续标量范围见 [DB-023](../0023-scalar-schema-dto-slice/README.md)：13 种标量贯通 Schema/history/DTO，
对应 Reader/Writer 标量操作已公开。下文未实施的 restore/runtime binding 与一般图 codec 仍为设计草图；
字符串/引用根 capture 已在 DB-024 首片实装。

## 1. 本轮收敛与仍需讨论的点

采用用户描述的整体模型：

- 所有**受支持、参与持久内容**的引用对象统一按引用相等登记，包含 string、数组和 BCL 容器。
- 遍历从 roots 出发，经 class 字段、嵌套 struct 中的引用、数组元素、容器内容得到引用对象列表。
- 普通值成员嵌套编码；引用成员仅保存统一 ObjectId；每个引用对象记录的头部带实际类型的 TypeCodec。
- SG 产生强类型成员访问与值 codec；字段和数组元素可以传同一个 struct codec 的 ref 槽位。
- class 每层先调用基类 body，再处理自己声明的字段；BCL 容器保存内容而非实现字段。

这里的 intern 屏障是 **reference-identity interning**；已选择的唯一内容合并例外是零长度 string。
Transient、BCL 内部 bucket/backing array、框架服务引用等不因“所有引用”而自动进入图；
参加遍历的是该受支持类型的持久成员/内容合同。

以下推荐尚待讨论：

| 分支 | 推荐起点 | 需要说明的代价 |
|---|---|---|
| 继承字段身份 | DB-019 选择声明 Schema 分段、祖先 exact 依赖及派生版本递增 | DB-022/023 已接当前与历史 DTO body；DTO 升级/领域恢复尚未实现 |
| 类型复合与执行 | 推荐 SG 开放泛型 body + 运行时按需闭合；必要处局部 DynamicMethod | 已知定义与任意 CLR 类型支持分开，运行时后端不能另建 Schema authority |
| 保存一致性 | Capture 期间由调用方保证视图稳定；Seal 后只消费冻结候选 | 当前 DTO/string 候选是内容快照；通用递归图尚未覆盖 |
| 空 string/boxed 值 | 空串按 DB-025 显式统一 Empty；boxed 支持仍需验证 | 空串不保留实例区别；不能把 boxed 值当 inline 值 |

## 2. 引用身份与保存阶段

历史草图（未落地）：最小机制可以用：

```csharp
Dictionary<object, uint> ids = new(ReferenceEqualityComparer.Instance);
List<object> objects = new();
```

ID 0 表示 null；非 null 对象先登记 ID，再入待访问队列。遍历队列时调用实际类型的
VisitReferences；同一实例再次出现只取已有 ID。这样不需要用 CLR 递归栈展开循环图。
[ReferenceEqualityComparer](https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.referenceequalitycomparer?view=net-10.0)
正是引用比较，不依赖领域 Equals/GetHashCode。

保存的三个阶段（历史草图）：

1. **Discover**：构建引用闭包。嵌套 struct 的 VisitReferences 递归访问值成员，但对引用只登记目标。
2. **Close**：不再接受新对象；确定本次条目集合与 roots。确定所需 runtime 类型/Schema/codec 绑定。
3. **Write**：引用编码只查询 ID，不在这里新增对象、重排或重新分配 ID；每个对象写自己的 body。

写出遇到未发现引用应失败；它仅能发现一部分视图变化，不能检测标量改变、已登记引用之间的替换等。
首版推荐调用方提供同步、排他的 Capture 视图。DB-024 首片实现收敛为 root 注册 + Capture 回调 + Seal 冻结候选；
它已覆盖 root DTO 与 string 引用条目，尚未覆盖自定义 durable 对象之间的通用递归闭包。

### 单次列表序号与增量 ObjectId

全量单次 snapshot 可以按首次发现次序分配 `1..N`，正如用户所述。
增量保存则要复用加载/会话中已知的 `object -> ObjectId` 映射，只给新实例分配新 ID，
不能每次遍历重新从 1 编号。列表位置只是枚举位置；显式 ID 在相邻保存的持续存活区间内保持；跨回收边界不代表同一对象。

统一身份不要求所有对象都继承可写入 ID 的基类：string、数组、BCL 容器可由外层 side table 管理。
2026-09-06 校准：用户明确 ObjectId 经过 StateRevision 解释，允许回收复用；继续使用现有非零 uint 域。
分配/回收/候选生命周期与 string Capture 的当前讨论见 [DB-024](../0024-reference-capture-and-reusable-object-ids/README.md)。
用户随后选择首片仅 session 内单调递增，回收延期；先 Capture/封闭候选，恢复另片推进。
引用解析使用目标 revision，即使 owner payload 来自更早的 record；不按 payload 出生 revision 解析引用。
同一字符串实例不会原地改变内容；替换为内容相同的新实例仍是不同对象引用。

保真范围是保存图内部两两 ReferenceEquals 关系，不包括与新进程已有对象/intern pool 的关系。

## 3. 记录与 TypeCodec

概念上的 full snapshot 包含 roots 和带 ID 的对象条目（设计草图）：

```text
Snapshot
    FormatRevision
    RootObjectIds
    Entries:
        ObjectId
        TypeCodeLength + TypeCodeBytes     // 实际闭合类型，位于对象记录头
        BodyLength + BaseBody
```

长度编码、目录布局、操作码数值及其与 RBF Revision 的嵌入关系尚未冻结；
这不是现有 Storage membership v1 wire 的描述。

TypeCodec 只处理可组合的类型表达，不同时加载对象或生成执行代码：

```text
RuntimeTypeExpr =
    Primitive(kind)
  | String
  | VectorArray(elementType)
  | MultiDimArray(rank, elementType)
  | SupportedBcl(constructor, argumentTypes...)
  | Custom(ExactSchemaRef, argumentTypes...)
```

对象头必须是闭合类型。泛型声明 Schema 中可以出现 TypeParameter(index)，对象头不能保留未绑定参数。
引用字段的 declared type 与对象头的 actual type 分开；加载时验证它们相容。
基元类型在 TypeExpr 中还用于数组/泛型参数和值 Schema，不能据此认定 primitive 本身一定占对象条目。

**Null 是值标记，不是 CLR 类型。** 推荐引用 ID 0 足够表达 null，null 不建对象记录。
若保留用户提出的 Null 短码，它属于 root/dynamic-value envelope；不允许 VectorArray(Null) 这种类型表达。
SZArray 与 rank-1 非 SZArray 必须区分，维长和 lower bounds 属于对象 body 的形状前缀，不属于 CLR 类型 identity。

### 借鉴 StateJournal TypeCodec 的范围

已读 [StateJournal TypeCodec](E:/repos/Atelia-org/atelia/src/StateJournal/Internal/TypeCodec.cs)：
现有实现是长度外置的 postfix 栈解码，支持有限 primitive、预置 Durable 容器/ValueTuple。
它还没有这里所需的 Null/SZArray/MDArray/自定义 SchemaRef；MakeGenericType 也不生成 codec。

推荐保留 postfix 的组合思想；**参数按声明顺序编码，构造时反向填回参数槽**，不要直接连续 Pop 作为参数列表。
例如：

```text
PushInt32 PushString MakeDictionary
    => Dictionary<int, string>

PushSchema(Point@V1) MakeVectorArray MakeList
    => List<Point[]>

PushSchema(Node@V2) MakeMultiDimArray(rank=2)
    => Node[,]

PushInt32 PushString MakeCustom(PairSchema@V1)
    => Pair<int, string>
```

这些是符号操作码，不是最终 byte 分配。BCL 构造符有已知 arity；MakeCustom 从 exact 泛型 Schema
声明取得/校验 arity，按同样规则反向填参数槽；PushSchema 是 arity=0 的简写，不把开放泛型定义压栈当成闭合类型。
解析检查操作数、最终单一结果、闭合参数、rank、合法 SchemaRef 和有界输入；
不把任意 Type.GetType/程序集名解析当作数据绑定。

### Schema 的两类类型依赖

引用成员依赖稳定 nominal 类型/family 合同；实际目标版本由它自己的对象头确定。
值成员则在 owner 内嵌布局，需要 exact value schema；派生类的 base body 也需要 exact base schema。
这样 A 引用 B、B 引用 A 不会要求两个 canonical SchemaHash 互相递归展开。
Schema 图先登记声明，再连接 nominal 引用；不递归展开所有引用目标的静态属性。

ExactSchemaRef 暂是 opaque 设计名，表示 SchemaStore 的精确 schema 绑定；
地址/hash 编码与 canonical schema 格式仍待设计。不能用当前 DurableSchema.GetHashCode 代替持久 hash。

## 4. 生成代码：值类型、继承与引用

下面是**拟生成的 C# 形状**，不是已有产品 API。省略 attributes、Schema 声明和注册样板；
示例假设同程序集，helper 放在声明类型自己的 partial 中，以合法访问 private 字段。
示例里的 FieldId 按声明 Schema 分段，已由 DB-019 的 metadata 分片选择；本节 body 仍是草图。

```csharp
partial struct Position {
    private int _x;          // FieldId 1
    private string? _label;  // FieldId 2：引用！

    internal static class __Body {
        internal static void VisitReferences(ref Position value, ReferenceCollector refs) {
            refs.Add(value._label);
        }
        internal static void Write(ref BinaryPayloadWriter writer, ref Position value, ObjectWriteContext refs) {
            writer.WriteInt32(value._x);
            refs.WriteReference(ref writer, value._label);
        }
        internal static void Read(ref BinaryPayloadReader reader, ref Position value, ObjectReadContext refs) {
            value._x = reader.ReadInt32();
            value._label = refs.ReadReference<string>(ref reader);
        }
    }
}

partial class Entity : DurableBase {
    private long _number; // Entity 段的 FieldId 1

    internal static class __EntityBody {
        internal static void VisitReferences(Entity value, ReferenceCollector refs) { }
        internal static void Write(ref BinaryPayloadWriter writer, Entity value, ObjectWriteContext refs) {
            writer.WriteInt64(value._number);
        }
        internal static void Read(ref BinaryPayloadReader reader, Entity value, ObjectReadContext refs) {
            value._number = reader.ReadInt64();
        }
    }
}

partial class Node : Entity {
    private Position _position; // Node 段的 FieldId 1
    private Node? _next;        // FieldId 2
    private Position[]? _path;  // FieldId 3

    internal static class __NodeBody {
        internal static void VisitReferences(Node value, ReferenceCollector refs) {
            Entity.__EntityBody.VisitReferences(value, refs);
            Position.__Body.VisitReferences(ref value._position, refs);
            refs.Add(value._next);
            refs.Add(value._path);
        }
        internal static void Write(ref BinaryPayloadWriter writer, Node value, ObjectWriteContext refs) {
            Entity.__EntityBody.Write(ref writer, value, refs);
            Position.__Body.Write(ref writer, ref value._position, refs);
            refs.WriteReference(ref writer, value._next);
            refs.WriteReference(ref writer, value._path);
        }
        internal static void Read(ref BinaryPayloadReader reader, Node value, ObjectReadContext refs) {
            Entity.__EntityBody.Read(ref reader, value, refs);
            Position.__Body.Read(ref reader, ref value._position, refs);
            value._next = refs.ReadReference<Node>(ref reader);
            value._path = refs.ReadReference<Position[]>(ref reader);
        }
    }
}
```

关键点：

- Position 内的 string 也参加引用发现；不能只扫 class 的直接引用字段。
- Node 的数组字段只写数组 ID，元素归数组自己的对象记录。
- class 的基类调用是明确的静态 body 调用，不使用再次分派到 Derived 的 virtual 入口。
- 值 body 对字段、数组元素、临时槽位使用同一个 ref codec。它可以部分填充私有目标，不自行负责全图发布。
- 读/写/引用发现可共享生成器字段模型，不要求统一 mode visitor。计数以后作为同一布局的独立操作增加。

### 继承的 exact Schema

推荐每段 FieldId 局部唯一，完整字段身份为 `(declaring Schema segment, FieldId)`；
Base payload 顺序为最上层 base 段到最下层 derived 段，各段内部按 FieldId 排序。
Schema/history/未来 Delta 要保留这个分段事实，而不是把所有字段全局按一个 int 排序。

Derived 的 exact Schema 包含 exact BaseSchemaRef。base 绑定改变要求 derived Version 递增，
从而保持“同 SchemaId/Version 只有一种 shape”。DB-019 采用局部分段；runtime 以 immutable BaseSchema 对象表达闭包。

### DurableSchema 增强的依据与闭环

在 `c8a98dc` 基线上，DurableSchema 仅含 SchemaId/Version/Fields，相等与 hash 都只比较这些值；
Generator 的历史比较也只检查本层 fields。当时禁止领域继承，因此本片是新增受支持范围。
DB-019 已据此实施 BaseSchema、SchemaOnly 与 history 闭包；默认 boxed serializer 的领域继承限制保留。

最小逻辑形状建议为：

```text
DurableSchema
    SchemaId
    Version
    ExactBaseSchema?     // 直接 base 的精确身份，递归闭合祖先；不是当前 CLR BaseType
    DeclaredFields[]    // 本声明层字段；不另存重复的全部祖先布局
```

直接 base 自己再绑定它的 exact base，就能确定整条链；无需另存一份独立祖先列表作为第二份事实。
但这依赖 exact Schema closure 验证：每个祖先存在、同 key shape 唯一、无非法依赖环。
只记 Base 的 key 然后跳过祖先验证，仍不能证明完整布局不变。

ExactBaseSchema 必须进入 runtime equality/hash、accepted history、candidate manifest、publisher 的 shape 比较。
要求版本递增的检查在**有历史证据**的地方完成：SG 对照已接受历史、publisher 对照已存记录；
单次 new DurableSchema 不能凭空知道上一版。版本由开发者显式递增，诊断未递增，不自动改源码版本数字。

`BaseV1 -> MiddleV1 -> LeafV1` 的例子：

- Base 变为 V2 后，Middle 若仍用 V1 绑定新的 Base，出现同版本 shape mismatch，应拒绝。
- Middle 递增版本后，Leaf 的 base 绑定也改变；Leaf 仍 V1 同样拒绝。
- 各层新版本登记/校验通过后接受新链；版本数字不要求相同。
- 历史 LeafV1 永远指向当年的 MiddleV1/BaseV1，不能在生成历史 Snapshot 时引用今天的 Base.Schema。
- 普通方法/Transient 改动不改变 durable Schema；nominal 引用目标升级不触发 owner 的内嵌布局传播。

后续内嵌值类型也有 exact 布局依赖，沿同一原则处理。泛型定义中的类型参数在闭合类型表达中绑定，
其 effective layout 与定义版本要分清，不能给“所有可能闭合组合”逐一自动生成源码版本号。

现有 boxed 字段字典是平面 `Dictionary<int, object?>`；允许 base/derived 重复 FieldId 后，
Snapshot/body/历史读取必须能表达声明段。可以先让 schema/history 见证闭合，旧 boxed consumer 暂留原平面范围；
不能仅修改 DurableSchema 并放开类型诊断就宣称继承序列化已实现。

跨程序集继承/struct 引用需要可调用的生成式 body 边界：当前示例 internal 只证明同程序集形状。
SG 不能向已编译基类注入 partial；是公开 generated helper 还是首片限定同程序集闭包，需实施前选定。
每个 base 必须处理自己的 private storage，不能靠 derived helper 绕过可见性。

## 5. 数组生成形状与暂缓的 BCL 内容

数组 codec 的外层处理 shape，元素循环调用同一个 typed value body：

```csharp
// Position[] 对象的内容，不是 Node._path 字段的 inline 内容。
static void VisitElements(Position[] values, ReferenceCollector refs) {
    for (int i = 0; i < values.Length; i++)
        Position.__Body.VisitReferences(ref values[i], refs);
}
static void WriteElements(ref BinaryPayloadWriter writer, Position[] values, ObjectWriteContext refs) {
    for (int i = 0; i < values.Length; i++)
        Position.__Body.Write(ref writer, ref values[i], refs);
}
static void ReadElements(ref BinaryPayloadReader reader, Position[] values, ObjectReadContext refs) {
    for (int i = 0; i < values.Length; i++)
        Position.__Body.Read(ref reader, ref values[i], refs);
}
```

MultiDimArray 生成已知 rank 的 typed 多层循环，末维最快，传 `ref values[i,j]`。
shape 前缀必须在分配数组之前可读；是否覆盖非零 lower bounds 和非 SZ rank-1 仍待定。
实际 runtime 数组类型决定 codec；不能仅按协变字段声明类型生成 writable ref。
jagged array 是引用数组节点组成的图，保留内层数组的 null、长度、共享与循环，不摊成矩形块。

用户已将 List/Dictionary 等 BCL 集合支持暂缓。统一引用身份与“按内容重建”原则保留，
但 comparer、索引建立、key 依赖和容器适配器均不进入当前施工候选。
前述 List/Dictionary 的 TypeCodec 例子仅说明可组合表达，不表示本轮会实现它们的 codec。

## 6. 静态基础设施形状

以下仅为职责声明，省略方法体和底层存储。可以合并实际类，不能据此提前建立插件框架：

```csharp
sealed class ReferenceCollector {
    internal uint Add(object? value); // null -> 0；新对象按引用身份登记并入队
    internal void Drain();           // 调已登记对象 codec 的 VisitReferences
}
sealed class ObjectWriteContext {
    internal void WriteReference(ref BinaryPayloadWriter writer, object? value); // 仅查封闭表
}
sealed class ObjectReadContext {
    internal T? ReadReference<T>(ref BinaryPayloadReader reader) where T : class; // ID + 类型相容性
}
static class TypeCodec {
    internal static void Write(ref BinaryPayloadWriter writer, RuntimeTypeExpr type);
    internal static RuntimeTypeExpr Read(ref BinaryPayloadReader reader);
}
sealed class GeneratedCodecCatalog {
    internal ReferenceObjectCodec ForRuntimeType(Type actualType);
    internal ReferenceObjectCodec ForStoredType(RuntimeTypeExpr exactType, SchemaResolver schemas);
}
abstract class ReferenceObjectCodec {
    internal abstract void VisitReferences(object source, ReferenceCollector refs);
    internal abstract void WriteBody(ref BinaryPayloadWriter writer, object source, ObjectWriteContext refs);
    internal abstract object Allocate(ref BinaryPayloadReader reader); // 只读 allocation 前缀；string 可读完整 body
    internal abstract void ReadBody(ref BinaryPayloadReader reader, object target, ObjectReadContext refs);
}
```

ReferenceObjectCodec 是**对象记录边界**的异构分派；每次只把 object 转成实际 T，再调用上面的 typed body。
不通过它逐字段装箱。SchemaResolver 只解释已知 schema/binding，不拥有对象图或文件发布 authority。
primitive 字节方法继续放在现有 Serialization leaf；图与 Schema 设施放上层既有项目，不反向耦合 Storage。
DB-021 已公开 Reader/Writer 类型、构造、bool/int/long 操作及 reader 边界检查，并验证 runtime 包引用传递。
其余原语继续 internal，随实际消费者开放。
本节 internal 仅示意同程序集协作，不解决任意下游程序集的可见性。

可组合 TypeExpr 与 executable codec 是两件事，但不要求 SG 枚举所有闭合类型。
下面是本轮新增的推荐路线；完整运行时反射/IL 后端仍是可比较的替代方案，尚未实施。

### SG 开放泛型 body + 运行时按需闭合

```csharp
delegate void ReadSlot<T>(
    ref BinaryPayloadReader reader, ref T value, ObjectReadContext refs);

// 对受支持的 Cell<T> 定义生成一次，不需要枚举 Cell<int>、Cell<Node[]>……
static void ReadCell<T>(
    ref BinaryPayloadReader reader, ref Cell<T> value, ObjectReadContext refs) {
    SlotCodec<T>.Read(ref reader, ref value.Item, refs);
}

// 通用 SZArray 模板，运行时闭合 T；body 仍是 typed ref 委托。
static void ReadVector<T>(
    ref BinaryPayloadReader reader, T[] values, ObjectReadContext refs) {
    var readElement = SlotCodec<T>.Read;
    for (int i = 0; i < values.Length; i++) {
        readElement(ref reader, ref values[i], refs);
    }
}
```

SlotCodec<T> 是此处的值槽位 binding：primitive 调字节原语，struct 调生成 body，
reference 只操作 ObjectId，不在绑定期间递归构造引用目标的对象 body。
对象 body 在实际对象进入遍历队列时按实际 Type 取得，避免 Node<T> -> Node<Node<T>> 的无穷静态展开。
Write/VisitReferences 可采用相同 typed 签名原则；不因此强制统一 mode visitor。

首次遇到受支持的闭合 Type 时，factory 可用 MakeGenericType/MakeGenericMethod/CreateDelegate
闭合已编译的泛型 body 并缓存，JIT 负责生成/复用执行代码。反射只负责首次 binding，
元素循环不使用反射 GetValue/SetValue，也不逐元素装箱。这种组合不需要 DynamicMethod。
Registry 登记的是有限的**受支持类型定义/构造符**，不是预先登记所有闭合组合。

DB-020 已选择更小的底层接缝：ValueSlotCodec<T> 显式持有 typed Read/Write，直接创建绑定它的
SZ/rank-2 元素模板。已有闭合 T 的地方不再反射；没有全局按 CLR Type 缓存可变 body，调用方复用 binding。
primitive lookup 为测试共享工具中的固定 13 类型，已移出产品程序集；已知成员类型的 SG body 直接静态调用，
不要求经过该查表或 slot 委托。开放 Cell<T> factory 的按需闭合是使用真实叶子原语的手写测试见证，
尚无产品通用定义 registry、SG 泛型 body 或引用槽位。上面的含上下文 SlotCodec<T> 仍是后续形状。

当前类型的 Write/Visit binding 可以按 CLR Type 缓存；存储历史读取则必须按 exact stored type/schema
查对应 decoder，不能只按今天的 CLR Type 命中当前 codec。运行时 binding 不自动创造历史字段定义或升级器。
注册集合先固定，成功构造后才发布 cache 项；不增加热注册、可变 placeholder 或失败后修补框架。
初始化不要穿透引用目标；闭合值成员的有限依赖仍需正常验证。

### DynamicMethod 最有价值的位置

| 路径 | 推荐实现起点 |
|---|---|
| 自定义 class/struct 与开放泛型成员 | SG 生成受 Schema 约束的 typed body，runtime factory 按需绑定 |
| SZArray、jagged 的元素访问 | 通用 VectorCodec<T> 模板 |
| 已支持的固定 MD rank | Array2Codec<T> 等 typed 模板即可，不必 emit |
| 任意 runtime rank、非 SZ rank-1 的 typed 地址/循环 | 局部 DynamicMethod 是合理候选，复用已绑定的元素 codec |
| 无法参与 SG 的第三方 class/struct | 以后有明确消费者时再评估完整反射/IL 成员后端 |

旧代码值得延续的经验是首次访问构造 typed handler、按类型注册缓存、字段/元素槽位复用。
完整反射枚举成员再发 IL 的路线也可行；当前代价是再次实现字段筛选、FieldId、继承段、
exact 历史绑定等。即使未来采用它，也应消费同一 Schema/成员模型并验证等价 bytes。
本轮不重新建立第二套布局 authority，也没有决定整体移植 Robird/StateJournal 的运行时后端。
若选 DynamicMethod，首个见证按当前 .NET JIT 环境验证；当前没有提出 AOT 产品目标。

### 本轮执行见证

主审在 .NET 10.0.9 的内存编译中复跑：runtime 构造 Cell<int>[] 类型，
闭合 VectorReader<T> 和 typed ReadSlot<T>，Reader 是持有 ReadOnlySpan<byte> 的 ref struct，
两元素通过 ref 读入得到 `[17,29]`，消费 8 bytes。该路径没有使用 Reflection.Emit。
这验证泛型模板组合机制，不是产品 codec 或完整 factory 的实现。
完整可复跑代码保存在 [运行时泛型 binding 见证附件](0018-runtime-binding-witness.md)。
子代理另行验证了 DynamicMethod 接受 byref Reader 并通过数组 Address 修改非零下界二维元素；
该局部见证不证明任意 rank 的完整循环/格式已经实现。

## 7. 分配、读入与发布

读取建议分三轮：

1. 验证条目 ID、TypeCodec、exact Schema/codec、body 边界；为每条记录分配对象并登记 ID。
   class 用尚未发布的壳，数组读取 shape 后分配，string 直接读取成最终不可变实例。
   分配出的实际 CLR 类型必须匹配对象头绑定的 actual type；引用槽位上的 assignability 不能替代这一检查。
2. 填充 mutable 对象的字段/元素引用；必要时延后建立 hash/sorted 容器索引。
3. 验证完整消费、引用相容性及选定的重建规则，成功后才返回 roots。

Allocate 消耗的 prefix 长度可以作为本次读取的游标偏移保留；ReadBody 从剩余 body 开始。
不把 ref struct Reader 存入 heap 状态。string 在 Allocate 阶段消费完整 body，后续 ReadBody 为空。
对象映射在内容填充前可用，因而可以解析循环；未完成图不对应用暴露。
底层 ref 读入失败不默认回滚值或 Reader；失败丢弃本次私有加载结果。
历史升级若替换 CLR 实例，需要另行设计全图引用重绑定，不能直接把早期 boxed 升级拼上来后宣称闭合。

### string 的精确引用保真

`Node.Label` 等成员写的是 ObjectId；string 自己的记录以现有字符串原语写内容。
非 null string 条目无需再用 nullable header；null 在引用 ID 层表达。
同一源实例的多处引用恢复为同一实例，内容相同的不同非空实例恢复为不同实例；
解码不调用 String.Intern，也不使用按内容去重的实例缓存。

建议加载端检查不同非空 string ID 不得绑定同一 CLR 实例，防止无意合并节点。
**空字符串已由 DB-025 用户裁决为规范化例外**：Capture 登记前统一 Empty，解码也返回 Empty；
多个输入空串 ID 可解析到该单例。无需独立空串分配机制。此规则不自动推广到空数组或其他类型。
string 内容可以复用现有 UTF-8/UTF-16LE 规则，但内容 codec 的值正确不等于节点 identity 已正确。

### boxed 值

object/interface 槽位里的 boxed primitive/struct 具有引用身份，不能仅因实际 Type.IsValueType 就改成 inline。
如果纳入支持，应有 box 的对象记录与 typed box body；普通 struct 字段仍 inline。
是否首片支持 boxed 值尚待确认，不把该边界藏在“支持所有引用”的宣传里。

## 8. 验收与建议实施顺序

整体形状的关键验收：

- Restore/runtime binding、循环解析和通用对象闭包的条目仍是验收草图；已实现的 root-seal capture 与 string slot identity 见 DB-024。

- 两个 Equals 相等的不同 class 实例仍是两个 ID；同一个实例的多个路径仍指向一个 ID。
- 两个内容相同的不同非空 string、一个被多处引用的 string、null 与空串分别验证引用关系与内容；
  不同空实例按已选规则规范化为 string.Empty。
- class -> struct -> string、struct[] 中引用、class 自环/互环、共享/jagged 数组都能发现完整闭包。
- 同一个 value codec 处理字段和 SZ/MD 元素；原地读入修改正确槽位。
- base/derived 可重复局部 FieldId、private 字段、base-first 及 exact base 版本绑定得到验证。
- TypeCodec 的参数顺序、嵌套数组/泛型、SchemaRef、未知 binding、非法表达式和精确边界得到验证。
- 写出不新增身份；失配引用/损坏 payload 不返回半成品图；保存视图约束明确。

Base/Middle/Leaf 三层 Schema/history 版本传播由 DB-019 分片实施，
DB-020 又验证显式提供的泛型值 body + runtime binding + SZ/rank-2 ref 元素路径；
DB-021 已由实际 SG 生成当前 bool/int/long class body，直接 primitive 调用与继承段组合得到验证。
后续扩充复合 Schema kinds；含 string 引用的小对象图继续用于恢复/runtime binding、对象重建与同一 ID 重定位，不是本篇的既定事实。
这样首个 string 消费者就不会走已被取代的字段 inline 路径。
与 ObjectVersion 存取、增量策略、SchemaStore 的产品接入顺序仍按下一份明确施工边界裁决，
不在本次讨论中实现完整 framework 或长期 wire。

## 9. 当前事实与材料位置

当前产品保留 primitive byte leaf、membership Storage、估算策略和 scalar boxed schema/history 路径，
并新增 DB-019 的 SchemaOnly 继承元数据：声明层字段、精确祖先、历史查询与发布闭包校验。
默认 serializer 路径仍限制 sealed/direct DurableBase；DB-022 的额外 GenerateBinaryBody 已支持当前 Capture 与各版 scalar DTO body；
DB-024 补齐首片 string 引用槽位（`string` 对应 `uint` slot）与 root-level Capture/Seal；
本篇 struct、开放泛型及非 string durable 引用的 SG body 尚未实现。
DB-020 的内部值槽位和数组元素循环不处理对象头、shape、分配或图身份，也没有扩大 Schema kind。

用户先后授权的实现范围见 DB-019–024。完整领域 Deserialize、图身份恢复、stored-Schema runtime binding、
一般 durable 循环与对象重建、开放泛型生成器与旧 IL 后端翻新仍待后续工作；BCL 集合继续暂缓。

材料：[产品工作集](../../../src/PROJECT-STATE.md)、
[目标设计](../../DurableGraph-target-design-v0.md)、
[DB-017 早期要点与旧实现证据](../0017-object-codec-design-points/README.md)、
[DB-005 继承分支](../0005-durable-inheritance-flattening/README.md)、
[DB-001 Schema authority](../0001-schema-authority-and-runtime-representation/README.md)。
