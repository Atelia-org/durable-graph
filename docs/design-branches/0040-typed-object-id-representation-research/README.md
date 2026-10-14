# DB-040：以 DTO 或 nominal 标记参数化 ObjectId 的调研

> 文档整理：2026-10-14；来源为 Git `8ef9fca` 中的 `docs/design-branches/0040-typed-object-id-representation-research.md`，正文保留当片合同与历史验收含义。
> 阅读范围：16 项编译/运行见证是研究结果，含预期失败；泛型品牌暂缓，已选非泛型产品分片见 DB-041，不据局部 TypeLoadException 否定全部 typed API。
> 当前能力与施工焦点见 [PROJECT-STATE](../../../src/PROJECT-STATE.md)；旧稿中的“下一步”不自动成为当前工作单。

> 状态：Research / Generic target deferred — 2026-09-08。调研完成；后续已选择先实施[非泛型 ObjectId（DB-041）](../0041-object-id-state-representation/README.md)。下文保留本轮候选与见证。
> 基线：`8515de7`（DB-039 已实施）。用户提议 `ObjectId<TTargetDTO>`，T 使用生成的版本化 DTO 或 string，不使用领域 CLR 类型。
> 本文保存候选含义、源代码证据与独立 CLR 见证；实际能力仍以 [PROJECT-STATE](../../../src/PROJECT-STATE.md) 为准。

## 1. 问题、范围与结论

问题：能否把引用目标的类型信息放进 DTO 的 C# 定义，让 Upgrade 与字段传递具有更严格的编译期检查？
最小观察标准：包装仍能作为 unmanaged 状态；不同目标赋值被编译器拒绝；自环、互环、泛型扩张实际加载；
明确目标独立升版、多态及历史 DTO 身份折叠对类型参数含义的影响。

结论：

- **强类型引用 API 可行且有收益。** 状态表示与静态操作本来已泛型化，异构对象表/Storage 无需随之全部泛型化。
- **版本化 DTO 作为 T，需要先明确它表达什么。** 若承诺实际对象行的 exact DTO，与现有独立对象升级/多态不合；
  若表示声明锚点或本程序期望的 current 视图，则可解释，但增加品牌选择、历史表示闭合与源代码维护成本。
- **物理字段直接采用包装存在已复现的 CLR 边界。** 部分互递归 struct DTO 编译成功后加载失败；
  普通 struct 和 Auto layout 均未消除。保留 `uint` backing field、公开 typed 属性/构造器的对照成功，
  所以不能把观察结果扩大成“所有 typed ID API 都不可行”。

本轮只增加可重跑研究脚本与文档，不修改产品源代码、单元测试、Schema/history/wire 或包接口。

## 2. 真正获得的编译期能力

最小包装：

```csharp
public readonly record struct ObjectId<TTargetDTO>(uint Value);
```

T 不作为实例字段，也不加 `where TTargetDTO : unmanaged`，以允许 `ObjectId<string>`。
本机已验证该闭合类型满足 unmanaged、尺寸 4 字节、无 GC 引用；不同 T 的包装无法隐式赋值。
官方 [unmanaged 类型说明](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/unmanaged-types)
也以实际实例字段是否 unmanaged 为依据，不能仅因泛型实参是 string 就判定包装含托管引用。

收益包括区分数值与引用、区分部分不同目标族、让 typed Upgrade 参数/构造器/属性更难误用。
但它不证明数字对应的对象存在、对象处于哪个 Revision，或实际目标满足声明的 Schema 祖先约束。
所有引用仍共用一个 ID 空间，string 不因此获得另一套编号。

## 3. 实际 CLR 加载见证

运行 [Run-TypedObjectIdProbe.ps1](../../../experiments/GenericBindingShapeProbe/Run-TypedObjectIdProbe.ps1)：

```powershell
pwsh -NoProfile -File experiments/GenericBindingShapeProbe/Run-TypedObjectIdProbe.ps1
```

环境：Windows，SDK 10.0.201，CoreCLR/.NET 10.0.5。每项独立项目/子进程；编译上限 60 秒，运行上限 10 秒。
生成代码、构建/运行输出和 JSON 结果写入该 Probe 忽略的 `obj/typed-object-id-*` 目录。
脚本区分预期编译拒绝、预期 TypeLoadException 与正常运行；其他错误或超时失败。

| 形状 | 编译 | .NET 10.0.5 运行观察 |
|---|---|---|
| `ObjectId<string>` / 普通 `PlainId<string>` | 通过 | 4 字节、无 GC 引用；静态接口/ref 数组槽/原 UInt32 bytes 桥接通过 |
| `NodeDto` 包含 `ObjectId<NodeDto>` | 通过 | record/普通 readonly struct 均通过 |
| `NodeDto<T>` 包含 T 与自身 typed ID | 通过 | 多种闭合实参通过 |
| `ADto → ObjectId<BDto>`，`BDto → ObjectId<ADto>` | 通过 | TypeLoadException；record 与普通 struct 均复现 |
| 泛型字段递归扩大目标 DTO 实参 | 通过 | TypeLoadException；包含将 class 实参映射成 ID 状态的形状 |
| ID 和 DTO 均加 Auto layout | 通过 | 上述互递归/扩张失败仍在 |
| DTO 改为 class，ID 仍为 struct | 通过 | 对照通过；这会改变现有 unmanaged DTO 合同 |
| 用无数据成员的 nominal marker 作为 T | 通过 | 互引用/泛型嵌套对照通过，不展开目标 DTO body |
| DTO 内 uint，typed getter/构造器 | 通过 | 互引用/泛型嵌套对照通过；DTO 仍 unmanaged |
| `ObjectId<ADto>` 赋给 `ObjectId<BDto>` | CS0029 | 正常的编译期防误用收益 |

最小加载失败见证：

```csharp
public readonly record struct ObjectId<T>(uint Value);
public readonly record struct ADto(ObjectId<BDto> Next);
public readonly record struct BDto(ObjectId<ADto> Next);

public static class Program {
    // 编译成功后，实际运行抛 TypeLoadException。
    public static void Main() =>
        System.Console.WriteLine(System.Runtime.CompilerServices.Unsafe.SizeOf<ADto>());
}
```

普通 readonly struct DTO、只含 uint 的普通 ID struct 也复现，不能归因于 record 自动生成的 Equality。
本轮未定位 CLR 源码中的具体拒绝条件，也不声称这是所有 .NET 实现/未来版本的永久限制。
官方 [Type Loader Design](https://github.com/dotnet/runtime/blob/main/docs/design/coreclr/botr/type-loader.md)
解释了为何类型加载错误可能在调用方 JIT、进入方法体之前出现；具体反例以本机执行为证。

### 3.1 保留 typed API 的逃生方案

```csharp
public readonly struct ADto {
    private readonly uint _next;
    public ADto(ObjectId<BDto> next) => _next = next.Value;
    public ObjectId<BDto> Next => new(_next);
}
public readonly struct BDto {
    private readonly uint _next;
    public BDto(ObjectId<ADto> next) => _next = next.Value;
    public ObjectId<ADto> Next => new(_next);
}
```

该形状通过；脚本还验证含 `TState Data` 与
`ObjectId<NodeDto<ObjectId<NodeDto<TState>>>>` 属性的泛型对照。
这只证明局部类型形状，不证明产品 SG 所有闭合组合已实现。

成本：两条 SG 路径要区分物理字段与用户可见的引用属性；普通 property 不能直接作为现有 `in state.Field` 的变量实参，
需要 typed 临时值或直接读 backing field。通用 T 槽和构造器生成仍须一轮真实 SG 适配验证。
本轮没有测量 JIT 机器码、吞吐或内存分配量，不以“4 字节”推出所有热路径零开销。

## 4. 目标 DTO 版本的三种解释

当前 [引用字段](../../../src/DurableGraph/Schema/DurableFieldInfo.cs) 保存 nominal TargetType，没有目标版本。
[引用验证](../../../src/DurableGraph/Runtime/Binding/StateReferenceVisitor.cs) 比较选定 DTO 视图中的 nominal 祖先；
[目标独立升版测试](../../../tests/DurableGraph.Tests/NominalReferenceSchemaHistoryTests.cs) 与
[实际保存/恢复测试](../../../tests/DurableGraph.Tests/PersistedReferenceGraphTests.cs) 保证仅目标升版时 owner 无须变化。

```text
R1：WorldV1 -- ID 7 --> NodeV1
R2：WorldV1 -- ID 7 --> NodeV2
```

R2 可以只写 Node 的新 Base，World 的 Schema、ID 槽与对象 head 保持不变。

| T 的合同 | 是否可解释 | 代价/边界 |
|---|---|---|
| 实际目标行的 exact DTO | 无法作为当前通用静态字段合同 | 同一 WorldV1 在不同 Revision 指向不同 Node 版本；Base 字段还允许实际 Derived 行，null 也无实际目标可取 |
| 固定历史 DTO 声明锚点 | 可以；不必传播 owner Schema 版本 | 版本只作为品牌，不承诺实际内容版；需要确定稳定锚点及其历史保留/泛型状态参数闭包 |
| 本编译期望的 current DTO 品牌 | 可以；历史 helper CLR 类型可随编译变化 | 不证明 stored 目录已归一化；历史 reader/闭合 Upgrade 签名新增对当前目标表示的依赖 |

不能把“历史 helper 的 CLR 签名变化”直接等同于“磁盘 Schema 被改写”。只要 nominal/wire 不变，
第三种解释可以保持旧 bytes 和 owner Schema；本项目没有承诺生成 helper 的跨编译 CLR ABI 稳定。
但是目标升版可能牵动全部历史 owner 的生成 CLR 签名和用户写出的闭合 Upgrade 类型。

对于 `World → Box<Point>`，owner 只有 nominal `Box<Point>`；选择 `Box.V1<Point.Vk>` 仍需另给 k 的来源。
如果 current DTO 可由 history 纯状态生成，不一定需要当前领域 CLR；若要求它恰好是实际 normalized 表示，
其 exact 状态参数仍须与 current model 的选择一致。现有 stored-only 解码原本不需要这些 current 能力。

还可把强 exact DTO 句柄限定为“已完成归一化的目录视图”的 API；那是另一个有用候选，
不能直接据此决定所有持久 DTO 引用字段的形状。多态仍需区分声明视图与目标实际行。

## 5. 包装 DTO 类型仍未使 Schema 映射一一对应

[当前生成测试](../../../tests/DurableGraph.Tests/GenericGeneratedStateTests.cs) 明确验证无持久字段的 `Phantom<T>`
生成非泛型 `PhantomStates.V1`：DTO 参数只保留状态表示真正需要的部分。

```text
Phantom<int>     → PhantomStates.V1
Phantom<string>  → PhantomStates.V1
```

两者 nominal family 不同，但 `ObjectId<PhantomStates.V1>` 相同。
要完整保留目标 nominal 身份，还需让 DTO 或独立标记携带全部 nominal 实参。
因此 [InferSchemaFromState](../../../src/DurableGraph/Runtime/Binding/StateBindingContext.Upgrade.cs) 的 owner nominal/history 输入仍有职责，
DB-039 的完整槽比较及引用目录验证也不能被包装取代。

## 6. 框架接口不必全部泛型化

[IStateOps/IValueProjection](../../../src/DurableGraph/Runtime/Binding/StateValueBinding.cs) 已按 TState 泛型化。
typed 引用可以留在 DTO 用户接口与静态 helper，在异构边界提取 `.Value`：

```csharp
writer.WriteUInt32(id.Value);
visitor.VisitDurable(id.Value, slot.TargetType!);
```

[CaptureContext](../../../src/DurableGraph/Runtime/Capture/CaptureContext.cs)、[ObjectReadTable](../../../src/DurableGraph/Runtime/Capture/ObjectReadTable.cs)、
[引用 visitor](../../../src/DurableGraph/Runtime/Binding/StateReferenceVisitor.cs) 和 Storage 目录仍可用统一 uint/非泛型 ObjectId。
不需要逐字段 Type 查找、DynamicInvoke 或新程序集。typed→raw 的桥接已由独立见证执行。

实际改造面主要在 SG 引用字段/属性/构造器、引用 projection/ops、
[snapshot 状态表示选择](../../../src/DurableGraph.Persistence/ModelExecutionEnvironment.cs)、DTO 参数反推与用户 Upgrade 代码。
选择目标 DTO 不能简单调用 ResolveReader 递归展开引用 body；当前特意截断此依赖，
已有 `Node<T> → Node<Node<T>>` 产品用例依赖按实际有限对象/类型闭合。

## 7. 候选排序与后续裁决

当前调研推荐优先比较：

1. **ObjectId 无泛型**：最小的数值/引用隔离，不额外声称目标族。
2. **ObjectId<TNominalMarker>**：若需要目标级编译检查，更贴合当前引用合同。marker 可生成在同一 Family 下，
   不依赖领域 CLR、不带版本/字段布局，保留全部 nominal 实参；例如示意
   `ObjectId<Family_Box.Nominal<Family_Point.Nominal>>`、`ObjectId<string>`。
3. **ObjectId<TCurrentDTO> 外观 + uint backing**：保留用户直观的 DTO 类型写法与强检查；
   需明确其品牌是声明/期望视图，完成 current 表示选择与历史 reader 能力边界设计。

本轮研究结束时未采纳重构；后续选择非泛型包装见 DB-041。尤其不因局部 loader 失败就把所有 DTO 改为 class；该控制组只证明另一技术路径存在。
后续若选择 typed target，需要明确多态 upcast/受控重标记、phantom nominal 保留和历史表示依赖，
然后验证真实 SG、两阶段读取/Upgrade、目标独立升版、删除旧声明以及原 bytes 不变。

三位子代理分别调查历史语义、接口成本和 CLR 见证。主代理独立重跑原 13 项、补充 marker/typed-property 对照，
并把“DTO 品牌含义”和“物理字段表示”分开复核；不会以审阅人数代替实验证据。

最终保留脚本由主代理执行：**16 项符合预期，零超时**。其中 15 项编译成功；8 项运行成功、7 项为明确的
TypeLoadException；另 1 项是 CS0029 编译负例。产物为
`experiments/GenericBindingShapeProbe/obj/typed-object-id-20260908061601-18924`，含各项完整源代码与 `results.json`。
这不是“16 项产品功能已支持”的计数；失败类型是当前运行时边界的研究证据。
根 solution build 通过，零警告/错误；5 份受影响 Markdown 的 157 个本地链接/锚点及 diff 检查通过。
产品源代码和单元测试未修改，因此未把上一片的产品测试计数作为本轮研究验收。
