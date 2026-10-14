# DB-018 技术附件：泛型模板、运行时绑定与 DTO 表示参数

2026-09-07。用户已认可技术方向并要求留存；本文件不是下一施工分片或完整泛型支持已实现的声明。
接续 [DB-018 运行时绑定见证](0018-runtime-binding-witness.md)与
[DB-020](../0020-typed-slot-array-binding-slice/README.md)，补充采用冻结 Versioned DTO 后必须处理的表示映射。
不回写旧实验的历史含义；排期与剩余工作见[路线图](../../DurableGraph-research-roadmap.md)。
完整方案比较及最新审阅收敛见 [DB-038](../0038-generic-schema-state-and-binding-design/README.md)；本附件保留技术素材来由。

## 1. 已验证的技术基础

用户提出 `SerializerRegisterer<T>`：为每个闭合 T 缓存 codec，开放泛型 body 的 T 字段通过它转发。
这个思路与旧见证中的 `GSlots<T>.Read` 一致。SG 可以生成一次开放泛型模板，无需枚举全部闭合组合；
首次使用时以 MakeGenericType / MakeGenericMethod / CreateDelegate 闭合预编译代码并缓存，
后续保持 typed 调用，不要求 DynamicMethod 发出新 IL。

每个闭合泛型类型拥有独立静态成员，这是缓存成立的依据；不要求每种 T 都有独立机器码，
CLR 可以共享部分泛型执行代码。参见[静态成员规则](https://learn.microsoft.com/en-us/dotnet/csharp/programming-guide/classes-and-structs/static-classes-and-static-class-members)
与[运行时泛型机制](https://learn.microsoft.com/en-us/dotnet/csharp/programming-guide/generics/generics-in-the-run-time)。

证据分两层：

- DB-018 的完整可复跑见证：静态 `GSlots<T>` + 运行时闭合，读入 `GCell<int>[]` 得到 `[17,29]`。
- [RuntimeGenericSlotBindingTests](../../../tests/DurableGraph.Serialization.Tests/Serialization/RuntimeGenericSlotBindingTests.cs)：
  使用真实 BinaryPayloadReader/Writer，组合 `Cell<Cell<int>>` 与 `Cell<double>`，同一 ref body
  操作局部变量、字段、SZ 和 rank-2 元素。这里由工厂显式传入 member codec，不是全局 static-T 注册表。
  部分读取失败的测试仅证明已访问槽位的行为，不承诺整个图恢复的事务性。

2026-09-07 讨论时主代理执行：

```powershell
dotnet test tests/DurableGraph.StateStore.Serialization.Tests/DurableGraph.StateStore.Serialization.Tests.csproj --no-build --filter FullyQualifiedName~RuntimeGenericSlotBindingTests --verbosity quiet
```

结果 3/3，通过，无跳过。以上 body 是手写的生成代码等价物；当前 SG 仍拒绝自定义泛型领域类型，
未生成泛型 Schema/DTO/history。旧测试含非零下界矩阵，其机制覆盖不改变新选的 MVP 数组排除范围。

## 2. DTO 路线增加的表示映射

对领域 `Box<T> { T Value; }`，捕获后的字段类型不能总是继续写成 T：

| 领域 T | 冻结后的字段表示 |
|---|---|
| int | int |
| string 或受支持领域 class | UInt32 ObjectId |
| 含引用的自定义 struct | 该 struct 的对应版本 DTO，引用槽变为 ID |

静态注册槽解决“调用哪个函数”，并不能让 C# 字段声明自动变成“由 T 决定的另一种类型”。
推荐生成器区分领域参数与表示参数，示意为：

```text
BoxStateV1<TValueState>
BoxCodec<TDomainValue, TValueState>
```

首次工厂绑定确定两种参数的对应关系，然后闭合模板；额外表示参数由生成代码和工厂管理，
不要求领域作者修改 `Box<T>` 的定义。闭合后 Capture、比较、Base/Delta 与 Hydrate 保持 typed 调用，
装箱只保留在必要的异构对象目录边界。具体名称、方法签名、约束和 Schema 表示尚未冻结。

更复杂定义中的 T、T[]、Pair<T,U> 等字段，可能需要多个表示参数，不能假定一个 TValueState
足以让 C# 推导全部组合；按实际字段需要生成，不提前建设通用关联类型框架。

只给泛型槽使用装箱的不可变状态，是可比较的简化方案，但会增加字段级转换/分配并削弱静态约束；
当前更倾向于显式表示参数。类似 SnapshotSlot<T> 的包装若内部仍擦除类型，并未消除这个取舍。
若以后采用装箱表示，仍不能把可变领域实例直接塞进 DTO 来冒充冻结状态。

## 3. 缓存、版本和引用边界

- 已知具体类型的普通字段继续直接调用字节原语或静态 body；泛型槽可以使用缓存的 typed delegate/
  codec，不为所有字段引入 Type 查表。不预先承诺 JIT 必然内联或消除间接调用。
- 当前 Capture 的纯代码能力可以按闭合 CLR Type 缓存；历史读取必须保留 exact stored type/Schema
  绑定，不能以今天的 SerializerRegisterer<T> 覆盖历史布局。缓存不持有 Repository、Session 或对象实例。
  登记、初始化失败及递归初始化的具体协议在真正的消费者分片收敛。
- `Box<Node>` 和 `Box<string>` 都可能使用 `BoxStateV1<uint>`，但其领域类型约束不同。
  binding 必须保留泛型定义、领域实参 TypeExpr 和 exact Schema；不能根据 DTO CLR 类型反推持久身份。
  泛型定义/history 与闭合实参的规范表示还需要 Schema/TypeCodec 设计，不以 CLR 名称字符串替代。
- 引用槽只捕获/保存 ObjectId，不在 codec 绑定期间递归展开引用目标 body；实际对象进入遍历队列时
  再按实际类型处理，避免 `Node<T> → Node<Node<T>>` 等无穷静态展开。
- 泛型参数的值布局仍遵守 exact inline Schema 依赖；引用约束仍是 nominal，不能因引用目标升版
  就自动传播整个引用图的 owner 版本。历史泛型 DTO、Upgrade 和依赖传播尚未由现有见证验证。

## 4. 何时重访与最小验证

自定义泛型保留为支持方向，不因为采用 SG 而排除，也不因本备忘而承诺任意 CLR 类型组合已可用。
实施触发：准备扩充 Schema 类型表达或开始自定义泛型 Capture/DTO 时，先读本文件再冻结分片。

最小验证建议让真实 SG 生成 Box<T>，先对 Box<int>、Box<string> 验证领域值到冻结值/ID 的不同投影，
再验证同一泛型定义的历史版本读取不会命中 current body；复合 struct 可在其 Schema/DTO 能力到位后加入。
数组组合遵守已选 rank/零下界范围。测试应涵盖 Capture 后领域变动不影响 DTO、不同领域实参即使
共享同一种 DTO CLR 表示也不会混淆绑定。该建议不是当前实施授权，亦不重排正在讨论的 Upgrade/Restore。

## 5. DB-037 提供的产品接缝

2026-09-07，非泛型 inline struct 已由 [DB-037](../0037-inline-struct-state-slice/README.md) 接通产品。
[生成值 helper](../../../src/DurableGraph.Generator/DurableSchemaGenerator.InlineState.cs) 将 exact 版本 DTO/body
与当前领域 struct 的 Capture/ref Hydrate 分开，已验证删除领域值声明后仍生成历史 DTO 并保留 owner 升级链。
SchemaKind 与 InlineSchema 只表达当前非泛型 exact 布局，不是一般 TypeExpr。
后续泛型可复用静态值操作的组合方式，但仍须显式建立领域参数与状态表示参数的对应关系，
并为闭合实参及历史版本设计身份/缓存；当前按 SchemaId/version 的 helper 名称不能代替这些工作。

## 6. StateJournal 的静态 helper 与工厂组装素材

2026-09-07 用户提供以下相邻仓库素材，主代理与独立只读 subagent 已核查源码。
本节是下一泛型分片的推荐细化，尚未在 DurableGraph 中实施或验证性能，不替代完整分片设计。

- [DurObjDictImpl](../../../../atelia/src/StateJournal/Internal/DurObjDictImpl.cs)：对外为
  `DurableDict<TKey, TDurObj>`，内部为 `DictChangeTracker<TKey, LocalId>`；读写外观转换对象/ID，
  持久内容操作静态选择 `LocalIdAsRefHelper`。这是引用投影的具体证据，尚非任意领域类型到 DTO 的映射框架。
- [ITypeHelper](../../../../atelia/src/StateJournal/Internal/ITypeHelper.cs)：静态抽象操作经
  `where THelper : unmanaged, ITypeHelper<T>` 和 `THelper.Write(...)` 等形式调用，不需要 helper 接口实例。
- [HelperRegistry](../../../../atelia/src/StateJournal/Internal/HelperRegistry.cs)：解析并验证类型，
  返回 helper Type 与 TypeCode；复合类型递归闭合预制 helper，解析结果可缓存。
- [DurableFactory](../../../../atelia/src/StateJournal/Internal/DurableFactory.cs)：MakeGenericType 选择闭合实现，
  Expression 编译构造/外观适配委托，后续不再逐字段反射。泛型静态工厂按闭合实参初始化，
  外层 Type 缓存不意味着并发 GetOrAdd 的 value factory 严格只执行一次。

据此，将 §2 的默认候选进一步细化为 **领域参数 + 状态参数 + 操作类型参数**。
优先比较静态 helper 方案，不再预设每个泛型字段都需要持有 codec 接口实例。示意：

```csharp
interface IValueProjection<TDomain, TState> where TState : unmanaged {
    static abstract TState Capture(in TDomain value, CaptureContext context);
    static abstract void Hydrate(ref TDomain value, in TState state, ObjectReadTable objects);
}

// 生成模板以 TProjection.Capture(...) 调用，TProjection 没有运行期实例。
// BoxCapture<TDomain, TState, TProjection>
//     where TState : unmanaged
//     where TProjection : unmanaged, IValueProjection<TDomain, TState>
```

DTO 本身仍只含状态表示参数，例如 `BoxStateV1<TState>`；操作类型参数属于执行模板，
不会作为 DTO 字段持久化。工厂解析出 StateType/ProjectionType 后闭合模板，用户仍只写 `Box<T>`。
这不取消领域/状态映射：static interface 约束检查已经选出的类型配对，不能自动推导关联的 StateType。

历史状态操作与当前领域投影继续分开：可采用 `IStateOps<TState>` 及带 TStateOps 参数的 body 模板，
覆盖 Base、融合 Delta、Apply 与引用遍历。其绑定必须带 exact 历史布局/引用约束，不能仅按 TState 缓存；
`uint` 可以是普通数值、string ID 或 durable ID。历史 helper 的类型参数也不能无意引用已经删除的领域 struct，
否则会破坏 DB-037 的历史能力。引用约束等元数据可由生成的身份 helper 或显式参数承载，具体形状待验证。

Expression 适合首次构造与外观适配；若 SG 已生成可调用的泛型工厂，则 MakeGenericMethod/CreateDelegate
也可完成组装。两者都是冷路径选择，不用 Expression 重写成员遍历，也不把 Expression.Compile 称为完全无动态代码。
静态接口通过受约束的类型参数调用，见 [C# 官方说明](https://learn.microsoft.com/en-us/dotnet/csharp/advanced-topics/interface-implementation/static-virtual-interface-members)。
它消除了 helper 实例和相应的实例接口调用需求，但 JIT 是否内联、泛型共享下最终调用形状及性能须实测，
不承诺所有情形零间接调用或必然最优。

复用的是组装机制：不搬入 StateJournal 的 setter/ChangeTracker 权威、懒加载外观、string/symbol 或独立估算语义。
DurableGraph 仍先捕获冻结 DTO，融合准备实际 body，按 exact 版本读取，并由现有 Session 发布原候选。
首次构建失败不发布半成品 binding；模型目录初始化及循环引用仍需在下一分片验证，
不直接复制静态构造中所有异常/缓存策略。优先验证同一模板在 int、string、durable 引用、含引用 struct 下的闭合，
再验证历史 body 脱离旧领域 struct、共享状态 CLR 类型但引用约束不同，以及泛型 owner Upgrade 的可书写性。
