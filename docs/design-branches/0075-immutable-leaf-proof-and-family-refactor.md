# DB-075：ImmutableLeaf 完整证明与 Family 路径重构

> 状态：**Implemented**（2026-09-23 实施并完成集成验收）。2026-09-22 辩证评审定稿并批准[施工工单](0075-immutable-leaf-implementation-work-order.md)，2026-09-23 主线程完成核心实现、两委派包集成与全量验证。
> 来源：DB-072 实施后的审阅；用户授权按施工工单（含六项施工约束 C1–C6）实施。
> 验证：`dotnet build DurableGraph.slnx -t:Rebuild` 0 警告 0 错误；DurableGraph.Tests 全量 1617/1617（含 ImmutableLeaf 焦点 43、sibling 反例与 System 影子编译阶段见证）；[真包消费者探针](../../experiments/PackageConsumerProbe/ImmutableLeafConsumer/README.md)双 marker 通过（Family flag true + 保存/重开；sibling flag false）。
> 施工约束执行情况、阶段记录与发现（System 全名影子为编译阶段失败、Roslyn 5.3.0 API 替代等）见施工工单 §6/§7。
> 2026-09-23 复核修正：主构造参数仅在物化为隐式实例字段时排除（仅用于显式字段初始化器或未使用时不产生实例状态，不再按主构造语法一律拒绝）；单元测试 sibling 夹具改用 IIncrementalGenerator（删除 RS1042 豁免）；System 影子编译阶段见证钉住预期错误 ID。见施工工单 §7。
> 问题：其他生成器补入的状态可能逃过分类；Family 路径使合法非泛型叶模型一律失去 capability。
> 最小验收：双生成器补入可变状态不能交付 true capability；README 的 Family 接入中 readonly 标量叶可得到 true；两者共用同一分类合同。
> 不改持久格式，不实现 DB-073 缓存，不扩张泛型 closed-instantiation、DeepImmutable、string 或 Transient 共享。

## 1. 需求账本与证据边界

| 编号 | 要求 | 来源与强度 |
|---|---|---|
| R1 | 只完成方案文档，再用独立强模型评审简化；本轮不实现产品 | 当前用户明确要求 |
| R2 | 保留公共可选 `isImmutableLeaf` 构造参数；省略默认 false | 先前用户裁定，DB-072 §3.2，当前 StateModelBinding 源码 |
| R3 | true 必须证明 exact current model 全部实例状态满足叶规则，包含祖先及递归 inline 值；不能把生成器输入当作最终类型 | DB-072 现有合同 + 2026-09-22 可执行反例 |
| R4 | 普通 readonly 叶在强制 Family、含无关容器/record 等编译中仍能分类；真正泛型依然 false | 当前用户要求解决审阅 P2；README 推荐 Family 接入；不等于要求一般泛型推断 |
| R5 | 不改持久格式、append-only、exact-type 恢复校验、ReadPair 行为；不实现缓存 | 仓库纪律、DB-072 范围、当前源码 |
| R6 | 手写默认 false 不是生成器来源认证；公共 true 参数必须有准确的责任合同 | 当前公共 API 事实；不凭文档宣称手写无法置 true |
| R7 | 验证必须覆盖真实包接入，不只 GeneratorDriver 文本断言 | 当前包携带 Generator DLL 与 MSBuild targets；README 依赖该交付路径 |

现有运行模型：Generator 为 netstandard2.0，运行库为 .NET 10；生成代码在消费者程序集。
实例恢复先 Allocate 后 Hydrate，只填充未交付对象；IsImmutableLeaf 当前尚无运行时消费者，DB-073 是拟议消费者。
没有并发缓存或失败恢复机制需要在本片创建。

事实入口：

- [分类器](../../src/DurableGraph.Generator/DurableSchemaGenerator.ImmutableLeaf.cs)、[二进制发射](../../src/DurableGraph.Generator/DurableSchemaGenerator.StateModel.cs)、[Family 工厂](../../src/DurableGraph.Generator/DurableSchemaGenerator.GenericProjection.cs)。
- [编译级分流](../../src/DurableGraph.Generator/DurableSchemaGenerator.cs)、[模板路由](../../src/DurableGraph.Generator/DurableSchemaGenerator.TemplateHistory.cs)。
- [binding 通道](../../src/DurableGraph/Runtime/Binding/StateModelBinding.cs)、[NuGet 打包](../../src/DurableGraph/DurableGraph.csproj)、[构建 targets](../../src/DurableGraph/build/Atelia.DurableGraph.targets)。
- [现有分类测试](../../tests/DurableGraph.Tests/ImmutableLeafClassificationTests.cs)、[通道测试](../../tests/DurableGraph.Tests/ImmutableLeafBindingTests.cs)。

DB-072/073 是同一方向的设计材料，不作为彼此正确性的独立证明。

## 2. 已复现的问题

### 2.1 输入符号不等于最终对象

用户源文件：

```csharp
[DurableType("review.sibling", 1)]
public partial class Leaf : IDurableObject {
    [DurableField(1)] public readonly int Value;
}
```

第二个 incremental generator 普通输出：

```csharp
public partial class Leaf {
    public int Counter { get; set; }
}
```

两生成器同轮运行：生成代码仍传 `isImmutableLeaf: true`，最终 Emit 成功；
对未运行构造器的实例也可写入/读回 Counter。未来缓存会把该可变状态共享给另一读取者。
原始复现与日志留在本地 artifacts/reviews/db072-20260922；上面的失败路径应转为正式回归，不能依赖该忽略目录续工。

### 2.2 编译路由压过模型资格

`DurableGraphGenerateDefinitions=true`，或同编译出现容器、enum、record、Nullable、相关历史/升级/外部依赖，
会切到 GenericProjection。现有工厂没有传 flag，纯标量非泛型类也 false。
“Family 生成路径”和“泛型领域模型”必须分开：前者不应成为后者排除规则的替身。

## 3. 收敛方案与关键设计理念

**生成器证明候选，生成的代码在 binding 创建时确认最终结构没有改变；两条路径共用这套机制。**

```text
编译期候选不成立 → 发射 false
候选成立 → 发射闭包结构核对 → 一致才传 true，否则传 false
```

1. **允许形状只有一份规则。** 不在 Runtime 再解释一遍 TypeTag、Schema 和持久属性。
2. **输入不是成品。** 允许其他生成器正常补代码；它们改变候选实例结构时，只失去本优化资格。
3. **Family 是生成路径，不是类型资格。** 非泛型模型按自身形状决定，不能被无关 List 模型整体否决。
4. **布尔值仍经已批准的公共参数传输。** 新增的是传参前的最终结构核对，不是反射读取标记、私有命名协议或另一个 capability API。
5. **不把优化资格失败升级为业务错误。** 结构不一致返回 false；不新增编译硬门禁，不承诺修复其他生成器引起的持久管线错误。

这是对原 DB-072 “不经过运行时反射”的方案修订：增加 binding 构造时的具体 Type 字段查询，
不在读取、比较、Allocate 或 Hydrate 热路径逐次查询。当前反射 API 可枚举全部声明字段，
但私有祖先字段必须逐声明层处理，不能依赖 FlattenHierarchy；见 [Type.GetFields 官方说明](https://learn.microsoft.com/en-us/dotnet/api/system.type.getfields?view=net-10.0)。

## 4. 共同候选规则

把现有 `IsImmutableLeafModel` 的二值返回改为内部的“非候选，或已审计声明闭包”；无需公共 proof 类型。
规则仍由 Generator 决定，闭包是发射材料，不是运行时注册表或持久格式。

- root 是当前编译已建模的非泛型 class / record class；不要求 sealed，仍按 exact runtime type 使用。
- 自身、领域基类、递归 inline struct 必须都是当前编译已建模的非泛型声明。
  泛型基类/inline 的已闭合实例仍不进入本片，不能通过 OriginalDefinition 查找绕过这个限制。
- 全部实例字段必须是**显式声明**、readonly、已进入有效 durable 字段管线且没有 Transient。
  static 不参与；field-like event、隐式 backing、物化为隐式实例字段的主构造捕获都排除；
  仅用于显式字段初始化器或未使用的主构造参数不产生实例状态，不因此失去资格。
- 允许 DB-072 的 19 种真实内建值、同编译 durable enum、其 Nullable 和递归合格 inline 值。
  enum 是不可被另一 partial 扩展的终端；Nullable 只展开 child，不扫描 BCL 私有实现。
- string、对象引用、容器与其他引用形状继续 false。
- 空对象可进入候选；最终核对仍必须检查零字段和 object 基类，不能直接写常量 true。

**record 的范围修正：**含显式 readonly 字段的 record class / record struct 可参与；所有隐式 backing 暂为 false，
包括 positional、get-only、init-backed property。源码已确认 positional backing 的 `IsReadOnly` 可以为 true，
故旧文档“按 IsReadOnly 即可，同时 init 留待后继”的表述并不精确。
这不取消 record 持久化能力，只限制共享优化。当前产品所有 record 原本都因 Family 路径而 false，没有已交付正例回退。

若某个声明在闭包内被重复引用，可去重发射；不新增任意深度限额、跨编译缓存或通用图框架。

## 5. 最终结构一致性检查

### 5.1 检查材料与算法

对闭包内每个自定义 class/base/inline 声明，生成具体类型的 private helper 代码，核对：

| 事实 | 最终检查 | 缺失时的失败 |
|---|---|---|
| 直接基类 | 每个 class 的 BaseType 等于分类时的 exact base，包括 object 终点 | sibling 给原 object 根增加有状态基类，自身字段数不变 |
| 完整实例字段集合 | `Instance | Public | NonPublic | DeclaredOnly` 数量等于已审计字段数 | 新 auto-property/event、readonly DurableField 或隐藏存储逃出原管线 |
| 字段身份与类型 | 每个原 MetadataName 必须存在，FieldType 等于原符号对应的受信 Type | sibling 引入同名类型，原显式字段重新绑定，数量不变 |
| 只读性 | 每个字段 IsInitOnly 为 true | 最终结构与 readonly 前提不一致 |

数量、名字与逐字段签名一起使用；不能依赖反射返回顺序，也不能把数量相等当作字段集合相等。
检查覆盖原闭包的每个声明层，因此派生类和 inline owner 不会替祖先/子值漏过验证。
新增 readonly 字段同样返回 false：即使它本身不引入可变性，也没有进入本轮已生成的捕获管线。

以下为标量例子的发射示意，精确 helper 名称复用现有生成名碰撞纪律：

```csharp
private static bool __DurableCheckImmutableLeafShape() {
    const System.Reflection.BindingFlags flags =
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly;
    var type = typeof(Leaf);
    if (type.BaseType != typeof(object) || type.GetFields(flags).Length != 1) return false;
    var field = type.GetField("Value", flags);
    return field is not null && field.IsInitOnly && field.FieldType == typeof(int);
}

// 沿用已有公共参数，不修改构造器语义。
new StateModelBinding<Leaf, State>(..., isImmutableLeaf: __DurableCheckImmutableLeafShape());
```

不重新读取 DurableField/Transient 属性、不按 Schema 重新分类：编译期已审计的显式字段声明和其属性不能被其他生成器改写；
最终检查负责验证字段集合、类型绑定和祖先前提仍成立。字段名字只存在于本编译产物，不进入 `.dgschema` 或存档。

### 5.2 受信的 Type 身份

全限定名字不总能固定原符号。另一生成器若补入 `System.Guid`，最终 `typeof(global::System.Guid)` 也会被源类型遮蔽，
通常只有 CS0436 警告。不能用这个表达式重新推断原先证明的是哪个 BCL 类型。

- 语言内建类型使用 `typeof(int)`、`typeof(decimal)` 等关键字表达式。
- Half、Guid、TimeSpan、DateOnly、TimeOnly、DateTimeOffset 使用实际 corelib 身份，例如
  `typeof(object).Assembly.GetType("System.Guid", throwOnError: false)`；找不到所需 Type 时核对返回 false。
- Nullable 的泛型定义同样锚定实际 corelib 的 ``System.Nullable`1``；检查实际泛型定义和唯一实参，
  child 沿用上述受信 Type 规则。无需 MakeGenericType，也不增加一般类型名 resolver。
- 同编译已存在的非泛型领域/inline/enum 声明使用完整限定名；同全名只能合法合并为同一个 partial 类型，
  其可变结构由闭包核对处理，不能悄悄建立另一个同全名类型。

生成器仍须根据真实 corelib 符号判定内建候选，不能把同名用户类型当作 builtin。
固定允许集的 Type 表达式映射属于发射器，不是另一份运行时资格规则。

### 5.3 生命周期、失败与支持范围

检查在**每个 binding 创建时**进行；二进制静态 binding 因而一次，Family 按其现有工厂/快照节奏进行。
不宣称 Family binding 在全进程只有一个实例，也不添加全局 Type 缓存、额外静态状态或 Lazy。

正常不匹配和缺少预期成员返回 false；不吞掉任意异常。无法加载类型、损坏元数据等实际运行错误仍传播。
如果结构核对失败，既有生成序列化代码是否还能支持该模型，由原有合同决定；本方案只防止错误共享资格。

证明面是当前普通 .NET 10、具有完整字段元数据的编译产物；不扩展到字段重写、恶意篡改生成代码或 Unsafe 修改 readonly。
`PACKAGE.md` 已明确没有 NativeAOT 保证；本次也没有找到既有 trimming 支持承诺，不能反写成“项目早已禁止 trimming”。
具体 typeof + GetFields 对元数据保留有要求，不能把“枚举不到”解释为已证明没有字段。
若将来要求裁剪发布，须先验证字段/类型元数据保留，再承诺 true；本片不建立 linker descriptor 或通用 AOT 适配层。

手写 binding 省略参数仍 false；显式 true 继续是调用者断言，与可信 Capture/Hydrate 回调同类。
修改参数/属性 XML 说明，区分“生成绑定执行上述核对”和“手写调用者自行承担相同不变量”；
不借本片改变已有手写 true 通道测试，不承诺认证任意委托或阻止主动绕过。

## 6. 代码接缝与最小施工顺序

| 接缝 | 计划变更 |
|---|---|
| `DurableSchemaGenerator.ImmutableLeaf.cs` | 共同候选规则；拒绝全部隐式实例字段、外部/泛型闭包；返回可发射的声明与字段材料 |
| `GeneratedState.cs` / `StateModel.cs` | 二进制路径从常量 true 改为共同 helper 的结果；非候选仍常量 false |
| `GenericState.cs` / `GenericProjection.cs` | 沿现有工厂传递候选材料，非泛型当前 reference model 使用同一 helper；inline 不建立对象 binding |
| 生成器内部发射 helper | 发射具体声明层/字段/受信 Type 核对；不新增公开类型或独立程序集 |
| `StateModelBinding.cs` | 仅澄清 XML 责任合同；保留 bool 参数、默认值和 runtime 存储语义 |
| Generator tests / 包消费者 probe | 实际 Emit、构造 exact binding、断言内部 flag；补双生成器与 Family 普通接入 |

一个垂直分片完成，不先建设平台：

1. 固化双生成器反例和 §8 中否决简化方案的机制见证。
2. 接好共同候选/最终 helper，并同时修复二进制与 Family 工厂；阶段内不得将尚未核对的候选 true 暴露给 DB-073。
3. 通过 §7，更新 DB-072 能力结论、产品工作集与路线图；之后才进入缓存片。

实施时如果最小 helper 不能满足反例，应回到本设计补证，不自动增设 analyzer + runtime + postbuild 三道门。

## 7. 验收与非目标

| 场景 | 可观察结果 |
|---|---|
| 纯 readonly 标量，二进制/强制 Family；同编译加入无关 List 模型 | 叶的实际 binding 均 true；容器 owner false |
| enum、Nullable、递归 inline、显式 readonly 字段 record，分别进入 Family | 符合候选者 true；不能用一个组合测试替代各形状正例 |
| positional/get-only/init backing、主构造捕获、event、Transient、mutable/string/ref | false；不影响其既有持久化支持情况 |
| mutable base/inline、外部或泛型 root/base/inline | 对应 exact binding false；immutable base + mutable derived 分别验收 |
| sibling 在自身/祖先/inline 增可变或 readonly 实例字段 | Emit 合法时，实际 binding 必须 false；无新增状态的静态/计算成员不必失去资格 |
| sibling 新增基类、类型名重绑定、全名 BCL 影子 | 最终结构核对拒绝；不能仅断言生成文本中存在 helper |
| 空类 | 原形 true；sibling 加状态后 false |
| 手写 bool 通道与旧生成代码 | 省略参数 false，手写 true 原合同保持；旧代码不会被此修复自动重写 |
| 直接 GeneratorDriver → Emit，不运行 analyzer/MSBuild | 最终 guard 仍生效；无需建立 analyzer/history 配置笛卡尔矩阵 |
| 新打包的普通 PackageReference 消费者，README 强制 Family | 非泛型叶 true，并完成保存/重开；同包加入 sibling generator 后不安全候选 false |
| 原产品回归 | solution Rebuild、相关 Generator/ReadPair 测试通过；包/构建变更若有须按仓库交付规范验证 |

**重编译边界：**存档无需迁移，但已编译的 DB-072 消费者包含常量 true，只升级 runtime 不会修复它。
在 DB-073 首次使用 capability 前，应重编译参与测试和实际接入的模型程序集；DG 尚未固定发布，
本片不增加旧 binding 来源认证或二进制兼容层。若以后需要支持任意未重编译旧模型 DLL，必须另开版本/证明合同。

不实现运行时缓存、Normalize 复用、引用闭包分析、泛型 closed classification、跨程序集深证明、string 特例、
record backing 放宽、reflection 扫描程序集注册模型、缓存性能框架或持久 proof。

## 8. 辩证评审裁定与机制证据

三位继承主线程强模型的评审独立担任需求质疑、最小架构、语义防守；先独立立论，再交叉质询，
第三轮仅处理类型重绑定反例。主线程核对源码并执行独立 Roslyn 机制 probe，不按票数选结论。

| 对象 | 裁定 | 保留/删除的具体理由 |
|---|---|---|
| 完整最终结构核对 | keep | 双生成器补 Counter 后仍 true 已实际复现 |
| 两条 emitter 各自分类 | merge | 路由不改变非泛型模型资格，规则只维护一次 |
| 初稿 analyzer 硬门 | delete | 直接 Driver→Emit 是当前真实测试入口，不经过 analyzer；没有必要再造强制运行协议 |
| Build 工具新增 PE 审核 | defer | 现工具处理文本 history；会引入另一个交付/元数据协议，当前 guard 不依赖它 |
| 全 Runtime Type+Schema 再分类 | simplify | 改为原证明前提的结构对照；避免第二份允许集、属性和 Schema 解释器 |
| 仅 count + BaseType | reject | 原显式字段可重新绑定到新增类型，数量/基类不变 |
| record 隐式 backing 正分类 | defer | 编译器合成存储可被另一 partial 的属性替代；原 init-only 延后描述也不精确 |
| 全局缓存、AOT 平台、配置矩阵 | defer | 无当前独立失败类别或消费者要求；只做必要的真包/直接编译见证 |

相对于初稿，删除新 DiagnosticAnalyzer 及其候选定位/强制配置机制，不扩展 Build 工具；
相对于完整 runtime 备选，删除第二套 Schema/属性分类过程。保留一份候选规则、一种生成结构核对、两个发射接缝；
公开 API、程序集、持久格式均不增加。未测量性能，不宣称固定百分比收益。

2026-09-22 主线程用仓库同版 Roslyn 5.3.0、.NET 10 在独立临时项目中实测：

1. `partial record R(int X)` 的输入 backing 为 implicit + readonly；追加 `partial record R { public int X { get; set; } }` 后
   Emit 成功，字段数仍为 1，最终 IsInitOnly 从 true 变 false。这否定“所有候选字段只能增加”。
2. 原 `using System; namespace Local { partial class Leaf { readonly Guid Value; ... } }` 追加本地 `Guid` struct，
   该 struct 带数组字段和到 System.Guid 的隐式转换。模拟 Capture 的转换仍成功 Emit；字段数为 1、基类仍 object，
   实际字段为 Local.Guid。count + base 返回 true，exact FieldType 对照返回 false。
   这是编译机制见证，不冒充已运行完整 DurableGraph Hydrate。
3. 追加 `namespace System { struct Guid { ... } }` 后，`typeof(global::System.Guid)` 不等于真实 corelib Guid；
   `typeof(object).Assembly.GetType("System.Guid")` 则仍取得真实类型。两者均在成功 Emit 的程序集内实测。
4. readonly 单字段 struct 追加真正 InlineArrayAttribute，Emit 失败 CS9180；不增加无消费者的布局审计框架。
   该结果也符合[官方 inline array 诊断说明](https://github.com/dotnet/docs/blob/main/docs/csharp/language-reference/compiler-messages/inline-array-errors.md)。

本轮只建立设计和上述机制证据，未实现 helper，未声称 §7 已通过。
方案没有剩余架构分歧；反射成本、生成文本细节和发布形态验证由施工验收裁定，不能提前写成实现事实。
