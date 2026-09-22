# DB-072：Generator 侧 ImmutableLeaf 分类与生成侧见证

> 状态：**Implemented**（2026-09-22）。2026-09-21 立项；2026-09-22 裁定 capability 通道、放宽 API 边界并完成实施。验证：`dotnet build DurableGraph.slnx -t:Rebuild` 0 警告 0 错误；ImmutableLeaf 焦点测试 21/21；全解决方案测试 2690/2690（Serialization 163、Storage 202、Persistence 732、DurableGraph.Tests 1593）。
> 问题：为后续高效 fork 与跨操作实例复用，先在编译期识别一类可安全共享的 hydrated 领域对象。
> 最小验收：生成器对一组代表性模型给出保守、可测试的 `ImmutableLeaf` 分类，并经构造参数登记到 binding；生成侧测试见证分类矩阵；现有 `ReadPair` 共享路径对 `ImmutableLeaf` 输入不回归。
> 边界：不改变现有公共 API 行为（仅允许追加 `isImmutableLeaf` 公共可选构造参数）；不改持久格式，不引入 deep clone，不引入 deep-immutable 闭包，**不实现任何运行时复用机制**。
> 2026-09-22 实施后审阅：已复现其他生成器补入可变状态仍被判 true；Family 推荐接入也无法获得正分类。
> 2026-09-23 修复已由 [DB-075](0075-immutable-leaf-proof-and-family-refactor.md)（Implemented）实施并验收：候选须通过生成的最终结构核对才传 true，Family 中的非泛型合格叶可达 true。下文保留原实施合同与记录；§3.1 的 Family 路由限制描述已被 DB-075 取代。

## 1. 为什么先做这一片

DB-064/066 已证明“同 `(ObjectId, head)` + 完整 current 状态 + 引用闭包”可以在一次 `ReadPair` 内安全共享实例；
DB-067 已把 owned revision/map 的重复解码降低到 Store 读缓存层。
但这两者都没有给出一个可跨操作复用的**领域实例形状证明**。跨操作缓存如果只依赖运行时状态比较，
每次命中前仍要付出解码、Normalize、比较或引用闭包传播成本。

本片先补上最小的静态证明：

```text
ImmutableLeaf = 自身持久字段全部 readonly
             + 所有持久成员都是不可变值叶子
             + 没有任何引用类型持久成员
             + 没有 Transient 字段
             + 没有逃过字段管线的隐式实例字段
```

这个定义 intentionally conservative。它不尝试证明 `readonly List<T>`、`readonly DurableObject` 或
`readonly string` 的深不可变性；这些情况留给 [DB-074](0074-efficient-fork-deferred-directions.md) 的后续方向。

## 2. 分类定义

### 2.1 适用对象

v1 只分类**非泛型 reference-object 模型**：

- 普通 `class`；
- `record class`；
- 继承链上的具体叶类型。

以下类型不参与 v1 分类：

- inline struct / record struct：它们没有独立 ObjectId，不是本缓存的对象单位；
- 泛型 family：先由 DB-074 处理 closed instantiation 的分类问题；
- 手写 `StateModelBinding`：默认不可共享，不做显式 opt-in；
- 数组、List、Dictionary：当前都是可变容器对象，不进入 ImmutableLeaf。

空对象（没有任何实例字段，含隐式声明）可以分类为 `ImmutableLeaf`。

### 2.2 实例字段条件

对 exact current model 的**全部实例字段**逐项检查。关键事实是：**持久字段集不等于对象全部状态**。
`GetDirectFields` 对非 record 类排除一切 `IsImplicitlyDeclared` 字段——auto-property backing、
field-like event backing 既不持久化、不进诊断，也**运行时可变**。若只按持久字段分类，
一个带 `public long ViewCount { get; set; }` 的类会被误判为 `ImmutableLeaf`；
共享后第二个消费者会读到第一个消费者写入的 `ViewCount`（DB-066 §5 已记载同类泄漏模式）。

机械规则：

1. **零未分类实例字段**：模型的全部实例字段（含隐式声明）都必须落在 durable 字段管线内。
   非 record 类只要存在任何隐式 backing 字段（auto-property、field-like event），就不是 `ImmutableLeaf`；
   record 的隐式 backing 已进入字段管线并被现有诊断强制分类，按同一字段规则检查。
2. 字段必须 `readonly`（机械口径 `field.Symbol.IsReadOnly`；Generator 经 `UnsafeAccessor`
   在恢复阶段的一次性写入不使对象失去共享资格。init-only 属性的放宽留给 DB-074）。
3. 字段形状必须属于以下之一：
   - 内建不可变值叶子：`bool`、`sbyte`、`byte`、`short`、`ushort`、`int`、`uint`、`long`、`ulong`、
     `char`、`Half`、`float`、`double`、`Guid`、`decimal`、`TimeSpan`、`DateOnly`、`TimeOnly`、`DateTimeOffset`；
   - durable enum（现有 durable enum 管线的成员）；
   - `Nullable<T>`，且 `T` 是允许的值叶子；
   - inline struct，且其全部字段递归满足本定义。
4. 以下形状一律排除：
   - `ObjectReference`；
   - `string`（它是引用槽；v1 按“无引用类型成员”的简化规则排除，例外留给 DB-074）；
   - 数组、List、Dictionary；
   - 任何其他引用类型；
   - 任何非 readonly 字段；
   - 任何 `[Transient]` 字段（hydration 不写 transient ≠ 用户不写；跨操作缓存会把
     DB-066 §5 的 pair 内泄漏窗口扩大到整个 opened repo session，故整体排除）。

实现注意：允许集对应 TypeTag `{1,2,3,5–14,19–24}`。**不得复用 `IsBuiltinTag` 判定“内建叶子”**——
tag 4（string）也在 builtin 范围内。`DateTime`、`nint`、`nuint` 生成器本就不支持。

### 2.3 同编译证据边界

分类只信任当前编译可见的符号证据：

- inline struct 成员来自外部程序集时，v1 一律不分类：外部 nominal shape 当前只查 `DurableType`
  属性参数，字段级证据不足；readonly struct 字段的防御性拷贝会共享堆数组，误分类即静默数据损坏；
- 继承链基类字段的 readonly 证据无法从符号证明时，fail-closed 判非 `ImmutableLeaf`；
- 递归 inline struct 不需要环/深度防线：CLR 布局禁止值类型环（CS0523），深度受源文件物理约束。
  实施中“补”环检测或深度上限属投机复杂度，应拒绝。

### 2.4 继承与 sealed

分类按 exact current model 计算，不按声明基类型泛化：

- 基类满足条件、派生类新增字段也满足条件时，派生 exact model 可独立成为 `ImmutableLeaf`；
- 基类满足条件、派生类新增可变字段时，只有基类 exact model 是 `ImmutableLeaf`，派生不是；
- 不要求 `ImmutableLeaf` 自身 `sealed`，因为该分类只描述 exact model 的自身形状。

`sealed` 的必要性出现在未来的 `DeepImmutable`：
如果一个字段声明为基类型，而运行时实例可能是可变派生类型，则需要 sealed 目标或 exact runtime type 证明。
本片不做该层推断。

## 3. 实现接缝

### 3.1 Generator 侧

Generator 已能看见：

- effective durable field symbol（含继承字段）；
- `field.Symbol.IsReadOnly`；
- 字段的 `TypeTag` / inline Schema；
- 继承链和 record backing storage 分类。

分类在 Generator 内计算，结果直接经生成的 binding 构造调用传入 `isImmutableLeaf` 参数（见 §3.2），
不经过任何运行时反射或跨程序集命名约定。

实现事实（2026-09-22 实施时核实）：`UsesGenericTemplates`（`DurableSchemaGenerator.TemplateHistory.cs`）
在编译内存在任一 durable enum、record、或带类型实参的字段形状（数组/List/Dictionary/Nullable）时返回 true；
跨程序集 durable 引用与升级注册同样触发整编译切换（`DurableSchemaGenerator.cs` 主分流）。
这些编译全部走 GenericProjection 路径，不发射 `isImmutableLeaf`，binding 默认 false。
因此 §2.2 中 enum / Nullable / record 形状的正分类在二进制路径上**当前不可达**：
分类代码按规格保留为纵深防御，测试以 family 路径保守 false 见证。
正分类的实际可达面：无 enum/record/Nullable/容器/跨程序集引用/升级注册的编译中，
全部实例字段为 readonly 标量或 readonly inline struct（递归）的模型。

### 3.2 Capability 通道（已裁定）

2026-09-22 用户裁定：采用**公共可选构造参数**方案，并放宽原“不改公开 API”边界。
背景事实：生成代码运行在用户程序集里，只能访问 DurableGraph 的公共成员，`internal` 属性没有
生成侧可写通道；而现有唯一先例 `supportsBaseProjection: true`（`DurableSchemaGenerator.GenericProjection.cs`）
已经证明“生成器算出的布尔值经公共可选构造参数进 binding”是本代码库的既有模式：

```csharp
public StateModelBinding(..., bool supportsBaseProjection = false,
    bool isImmutableLeaf = false)   // 追加可选参数
```

- 生成代码构造 binding 时传入分类结果；手写模型不传参数，默认 `false`，
  “手写默认不可共享”由参数默认值结构化保证；
- `StateModelBinding` 基类暴露 internal 只读 `IsImmutableLeaf`，DB-073 缓存 hit 条件直接读取；
- 版本偏斜失败模式：旧生成代码 + 新 runtime → 默认 `false`，仅失去优化；
  新生成代码 + 旧 runtime → 编译期错误，响亮且发生在最安全的位置；
- 不为已编译旧二进制提供构造函数兼容层：DG 自身包尚未固定发布，直接扩展签名。

已评估并否决的替代通道（完整分析见 2026-09-21 辩证评审记录）：

- 运行时反射读生成 State 标记：零公共 API，但引入跨程序集命名约定契约；
  trim/AOT 下可能静默失去优化，保活注解本身也是公共 API 修饰；
- `UnsafeAccessor` 跨程序集写 binding 私有字段：把私有字段名变成隐式契约，版本错配时运行时抛异常；
- 派生类 / 标记接口 / 领域类属性：泛型 binding 是 sealed，Generator 不能修改用户手写类，
  且每个接缝最终仍是公共面变更。

### 3.3 生成侧见证（零新增运行时复用机制）

1. Generator 分类测试覆盖 §4 矩阵：该是 `ImmutableLeaf` 的是，不该是的不是；
2. binding 登记断言：生成模型的 `IsImmutableLeaf` 与分类一致；手写模型省略参数默认 `false`
   （`Atelia.DurableGraph.Tests` 经既有 IVT 读取 internal 属性）；
3. `ReadPair` 回归：`ImmutableLeaf` 输入在现有 DB-064/066 共享路径不回归。

同 key 复用、不同 head/model/binding 不复用的**运行时**见证归 DB-073——
它们需要复用机制才可测，而复用机制正是 DB-073 的交付物；本片不做 key-based 见证。

## 4. 最小验收

| 验收点 | 结果 |
|---|---|
| 生成器分类 | 二进制路径可达面：空对象、19 种内建标量 readonly、递归 inline struct、继承链（正/负双模型）；enum / Nullable / record 因编译级路由经 family 路径保守 false（见 §3.1） |
| 保守排除 | mutable 字段、string、object ref、array、List、Dictionary、Transient、非 record 类的隐式 backing 字段（auto-property / field-like event）、可变 inline 成员、泛型 family、跨程序集成员、手写模型均不分类为 ImmutableLeaf；非 durable 基类在分类之前已被 DG0020 拒绝 |
| 通道 | StateModelBinding 追加公共可选构造参数 isImmutableLeaf；基类 internal IsImmutableLeaf；手写模型默认 false |
| 生成侧见证 | 分类矩阵与 binding 登记断言（生成模型 true/false、手写默认 false）通过；现有 ReadPair 共享路径对 ImmutableLeaf 输入不回归 |
| 合同 | 不改现有公共 API 行为（仅追加 isImmutableLeaf 可选参数）、持久格式、append-only 生命周期；无运行时复用机制 |
| 构建 | `dotnet build DurableGraph.slnx -t:Rebuild` 通过（0 警告 0 错误）；ImmutableLeaf 焦点测试与全量 `DurableGraph.Tests` 通过（2026-09-22） |

## 5. 非目标

- 不实现 deep immutable；
- 不实现泛型 closed instantiation 分类；
- 不允许 string 字段；
- 不允许 Transient 字段；
- 不做手写模型 opt-in；
- 不实现任何运行时缓存/复用机制（归 DB-073）；
- 不做 ReadPair 引用闭包短路：`ImmutableLeaf` 无引用成员、闭包可短路是事实，但评估触发条件是
  DB-073 落地后 ReadPair 传播成为热点；
- 不改 `ReadPair` 公开行为；跨操作共享会改变 `ReferenceEquals`/`lock` 的可观察行为，属内部优化非合同；
- probe 生成器 DG0011 对 readonly durable 字段的禁令与本片前提方向相反；该生成器未注册进产品管线，
  冲突的裁决触发权挂在 probe 产品化时。

## 6. 后继

本片完成后进入 [DB-073](0073-repository-scoped-weak-reference-cache.md)：
在同一个 opened repository 作用域内，用 WeakReference cache 复用 `ImmutableLeaf` hydrated 实例。
capability 传输通道已裁定（见 §3.2），DB-073 的 hit 条件直接读 binding 的 `IsImmutableLeaf`。
