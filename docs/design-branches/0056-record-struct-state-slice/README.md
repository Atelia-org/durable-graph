# DB-056：record struct 的持久值支持

> 文档整理：2026-10-14；来源为 Git `8ef9fca` 中的 `docs/design-branches/0056-record-struct-state-slice.md`，正文保留当片合同与历史验收含义。
> 阅读范围：positional/readonly/generic 与 backing storage 分类的当片证据见 §8；复用既有 inline/Family，无新增持久格式，ValueTuple 未随片实现。
> 当前能力与施工焦点见 [PROJECT-STATE](../../../src/PROJECT-STATE.md)；旧稿中的“下一步”不自动成为当前工作单。

> 状态：**已实施 / G0–G3 验收通过**。
> 日期：2026-09-10。调研基线：`d4c9e47`（DB-055）。
> 先完成源码调查与局部机制见证，再按用户授权实施；产品验收与边界见 §8。
> 当前能力从 [PROJECT-STATE](../../../src/PROJECT-STATE.md) 查证；本轮施工与验收记录见 §8。

## 1. 要回答的问题与选择理由

能否让用户用惯常的 `partial record struct`，尤其是 readonly、positional 和 generic 形式，
表达有版本的领域值与复合 Dictionary Key，同时直接复用普通 struct 的 Schema、DTO、Delta 与显式 Upgrade？

DB-055 已允许普通/generic Durable struct 作为 Key，并分开了当前领域查找与完整持久状态比较。
下一片无需重做字典算法。record 的主要缺口在编译器合成的字段如何进入 SG，而不是缺少新的对象或值 codec。

| 候选 | 当前收益 | 本轮取舍 |
|---|---|---|
| record struct | 减少值对象/复合 Key 的样板代码，复用现有 InlineValue 和 comparer | **推荐**；必须包含 positional，不能只放开手写字段形式 |
| ValueTuple | 匿名组合 Key 很方便 | 后继；还需设计内建 Item/Rest 组合、nominal/exact 表达与历史值升级入口，不能借 record 顺带实现 |
| 跨程序集模型 | 多项目领域模型的消费价值很高 | 有明确多程序集消费者时排；外部 history、helper 可见性、登记与规则保留需要另一份完整合同 |
| SchemaStore 复用 StateStore | Dictionary 已提供部分集合基础 | 保留路线图触发；自举元数据、联合视图和冲突作用域尚待裁决，改存字典本身不能消除这些问题 |

最强反对意见是：普通 struct 已能实现相同持久能力。如果只支持非 positional、全部手写字段的 record，
收益不足。因此常用 positional 外观和真实历史恢复共同构成本片下限。
不为本片新增程序集、持久 TypeTag、对象种类或通用 property 序列化平台。

## 2. 用户形状与明确边界

支持以下用法；包含 record 的编译使用 Family 登记入口，见 §3：

```csharp
[DurableType("OrderLineKey", 1)]
public readonly partial record struct OrderLineKey(
    [field: DurableField(1)] int OrderId,
    [field: DurableField(2)] int LineNumber);

[DurableType("ScopedKey", 1)]
public readonly partial record struct ScopedKey<T>(
    [field: DurableField(1)] T Scope,
    [field: DurableField(2)] int Number);

[DurableType("World", 1)]
public partial class World : DurableBase {
    [DurableField(1)] public Dictionary<OrderLineKey, string> Lines = new();
}
```

`field:` 将现有 Attribute 放到编译器生成的 backing field，而非构造参数或属性；
不扩大 `DurableFieldAttribute` / `TransientAttribute` 的 AttributeTargets。
C# 的 positional 属性和字段定向 Attribute 规则见 [Microsoft 文档](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/record#positional-syntax-for-property-definition)。

本片合同：

- 同编译、顶层、显式 `[DurableType]`、partial record struct；支持 readonly / mutable、generic / 非 generic、
  positional / 显式 body，以及分散在多个 partial 声明中的成员。泛型约束沿用现有支持边界。
- 显式字段沿原 `[DurableField]` / `[Transient]` 规则；record 内实际 property backing storage
  使用 `[field: DurableField]` / `[field: Transient]`，包括 positional、body 内自动属性，
  以及 C# 14 `field` 支撑的属性。读取的是字段，不是属性的返回值。
- 每个受支持实例存储必须明确分类；不能只收集带 DurableField 的隐式字段而静默丢弃未标注的 positional 成员。
  重复 FieldId、持久/Transient 冲突、非法 ID、标注 static 存储继续报错。
  对不能识别的隐式实例存储明确诊断，不猜测其布局。
- 无存储的计算属性不参与 Capture，也不调用 getter；其依赖的显式字段仍需分类。
  持久标签若因目标错误或替代 positional 成员而没有绑定到实际字段，必须明确拒绝，
  不能只留下 C# 的 ignored-attribute warning 后生成缺字段的 Schema。
- 字段类型复用现有完整槽闭包：标量、enum、Nullable、Durable inline/generic 值、string 和受支持引用类型。
  record 可作为字段、泛型实参、Nullable child、数组/List 元素和 Dictionary key/value。
  Dictionary 根 Nullable Key 的拒绝边界不变。
- 不支持 record class、ref struct、CLR nested/file-local 类型、未标记 record 或跨程序集定义。
  普通 class/struct 的一般属性支持、ValueTuple 和新容器不随片开放；不修改它们当前字段发现政策。

record 是领域 C# 外观，不新增持久 SchemaKind。它仍是 InlineValue，无独立 ObjectId、对象行或 Normalize。

## 3. 实施前源码证据与生成接缝

| 实施前基线事实 | 源码入口 | 本片局部变更 |
|---|---|---|
| `HasDurableTypeShape` 对全部 `IsRecord` 拒绝 | [Ancestry](../../../src/DurableGraph.Generator/DurableSchemaGenerator.Ancestry.cs) | 仅接纳满足其他条件的 record struct，record class 仍拒绝；同步 DG0001 文案 |
| `GetDirectFields` 过滤全部 `IsImplicitlyDeclared` | [Generator](../../../src/DurableGraph.Generator/DurableSchemaGenerator.cs) | 对 record 纳入并校验真实 backing storage，保留字段 symbol / 所属属性 / 源位置 |
| 当前字段模型已保存 IFieldSymbol；history 仅保存 FieldId 与槽 | 同上 `DurableFieldModel`、`ToSchemaHistoryFields`、`HaveSameFields` | 不引入第二套属性 Schema；成员来源仅供当前投影与诊断使用 |
| Family 捕获按 `value.field` 生成，readonly 写入使用 UnsafeAccessor | [GenericProjection](../../../src/DurableGraph.Generator/DurableSchemaGenerator.GenericProjection.cs) | backing field 的读写统一走强类型字段 accessor；显式字段继续原直接读/普通或 readonly 写 |
| Family 入口已有 enum 等当前形状触发 | [TemplateHistory](../../../src/DurableGraph.Generator/DurableSchemaGenerator.TemplateHistory.cs) | 有当前 record 时选现有 Family；生成壳发出 `partial record struct` |
| 普通/generic struct 已有历史 reader、值升级和 Dictionary 能力 | [GenericFactories](../../../src/DurableGraph.Generator/DurableSchemaGenerator.GenericFactories.cs)、[DB-055](../0055-composite-dictionary-key-design/README.md) | 组合验收；不新增 record 专用 Runtime reader / comparer / Upgrade 路由 |

包含当前 record 的编译走已有 Family 路径，避免向传统 InlineState/Schema/StateModel 生成分支
各复制一套 backing-field 处理。代价是同编译其他纯非泛型模型也会使用 Family 生成入口；
依赖旧 `__DurableState` 等生成名字的源代码需迁至 Family alias/登记。此变化必须写进消费者说明，
不能用“wire 不变”掩盖生成 API 的变化，也不为此新增兼容别名层。
没有 record 的编译继续按原触发规则选择路径，不借本片全面移除传统生成分支。

字段模型保留实际 Roslyn symbol；生成 accessor 的 `Name` 使用当前字段的 metadata 名字，
不从属性名手写拼接 backing-field 名，也不把它存入 Schema/history。
helper 名使用既有 family/FieldId 规则，并验证与用户成员冲突；诊断优先指向用户属性/参数或标签位置。

## 4. Capture 与 Hydrate

backing storage 不能直接生成 `value.<Name>k__BackingField`，也不应改成调用属性 getter/setter。
复用已有 UnsafeAccessor，在当前 record 的泛型声明宿主上生成字段访问器。示意：

```csharp
partial record struct ScopedKey<T> {
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<Scope>k__BackingField")]
    private static extern ref T Storage(ref ScopedKey<T> value);

    internal static ref readonly T Read(in ScopedKey<T> value)
        => ref Storage(ref Unsafe.AsRef(in value));
}
```

示意中的名字仅说明形状，产品代码从 symbol 取实际 metadata 名；生成实现使用全限定类型。
Capture 的只读入口只返回/消费 `ref readonly`，不得写源字段或对外暴露可写 ref。
Hydrate 从 `default` 局部 record 起步，恢复各持久槽后赋回目标；不调用主构造器、无参构造器、
字段/属性初始化器、init/setter、`with`、Deconstruct 或业务转换。
Transient 为默认值，沿原宿主重建合同。
因此 lazy getter 不会替 Capture 补值，setter 的规范化/验证也不会在恢复时运行；
持久的是 backing storage，不是 getter 的可观察返回值。这延续字段恢复合同，不承诺维护任意属性方法的不变量。

这复用普通 struct 的“先在局部恢复全部值，再赋回字段/元素”方式。
record 中的引用槽仍先捕获 ObjectId、恢复时从完整实例表解析；不把领域引用留在 DTO，
也不增加跨对象恢复阶段。访问器的泛型参数必须匹配声明宿主，不做闭合类型特例的反射热路径；
相关 Runtime 规则见 [UnsafeAccessor 泛型合同](https://learn.microsoft.com/en-us/dotnet/core/compatibility/core-libraries/9.0/unsafeaccessor-generics)。

### 4.1 设计阶段的技术见证

2026-09-10，在 Roslyn **5.3.0.0**、.NET **10.0.5** 运行局部编译/调用实验：

- readonly generic positional record 的 Part/Code、自动属性 Extra、C# 14 field-backed 属性 Checked，
  均得到隐式 IFieldSymbol，AssociatedSymbol 指向对应属性，field-target Attribute 实际位于该字段。
  mutable positional record 的 backing field 则为非 readonly。
- 同声明泛型宿主上的手写 UnsafeAccessor，分别以 `Key<int>` / `Key<string>` 闭合，
  通过 `ref` 写入 default 值、经 `Unsafe.AsRef(in value)` 读取成功；string 引用保持同一实例。
- mutable backing field 写入成功；显式抛异常的无参构造器未调用，初始化器计数为零，
  带计数的 getter/init 未执行。相同持久槽以外的 record 存储差异仍影响编译器合成的 Equals。
- 最终实验编译没有 warning/error，输出 `GenericReadonlyBackingAccess:True`。

实验在忽略目录 `obj/db056-record-shape-probe`，未修改 src/tests 或产品二进制。
它只证明字段发现和访问器机制可行；SG 生成、完整图、history 及包交付必须由下面的验收接替，
不要求后续会话依赖该临时目录。

## 5. Schema、历史与业务相等性

持久 Schema 仍由 definition ID、版本、kind、arity、FieldId 和完整槽依赖构成。
record 关键字、位置参数顺序、属性名、backing-field 名、with/Deconstruct/Equals 方法不进入持久描述。
同 FieldId / 完整槽下重命名、调换声明顺序，或普通 struct 与 record struct 互换，不因外观变化自动升版；
字段增加/删除/类型及 inline exact 依赖变化仍须遵守现有显式版本传播。
这种外观互换不会冻结旧业务 Equals 的含义。

历史 reader/DTO 继续从 retained `.dgschema` 生成，旧 record CLR 声明可删除。
显式值规则、owner Upgrade、数组/List/Dictionary 的规则选择与升级后强制 Base 继续沿用，
不自动逐个升级 record，不以当前 record 类型补缺失历史能力。
历史不需要记录“当时是 record”：只要保留持久布局就足以读回 DTO。

Dictionary 的 CurrentDefault 会使用当前 record 的合成或自定义比较；Application 仍用当前已登记规则。
DTO 的 StateEquals/Delta/key pairing 不调用它们，继续按全部持久字段、浮点 bits、引用 ID 比较。

**重要用法边界**：record 合成 Equals 会比较实例数据字段，包括某些被 `[Transient]` 标记的存储；
DurableGraph 的 Attribute 不会改写 C# 生成的 Equals。作为 Key 时，应用必须避免让不可恢复状态决定查找，
必要时自行写 Equals/comparer。record 内含引用类型时，合成比较也可能调用该引用的 Equals，
不能据“它是 record”推导引用内容在 Hydrate 时已就绪。
DB-055 的 canonical 重复拒绝、历史 DTO 与当前 TryAdd 分层、模式 4/5 回捕稳定均继续成立；不加自动 comparer 修补。

未改变持久格式：history v7、SCB1 v2、Base v4、Storage wire v3、Dictionary codec 1 与 List codec 2。
后续若发现必须新增格式或历史 record 标志，应先给出不能由现有 InlineValue 表达的反例，再回到设计。

## 6. 施工顺序与最小验收

本片按下面四步实施。互斥文件的测试/消费者并行准备，核心字段模型和最终集成由主线程统筹。

| 步骤 | 范围 | 可观察验收 |
|---|---|---|
| G0：成员合同 | record 资格、字段发现/分类、位置与误标诊断、Family 路由 | positional/readonly/generic 正例；未分类、重复/冲突、错误 target、替代成员、未知存储等负例；record class/ref/nested 等继续拒绝 |
| G1：当前投影 | record partial 壳、强类型 backing accessors；复用 DTO/body | Capture/Hydrate 不调用构造器/初始化器/属性；完整 Base/Delta 与 StateEquals；源实例与已冻结 DTO 隔离 |
| G2：图与历史 | 完整容器组合、DB-055 比较分层、Schema/history/显式 Upgrade | 同实例多 Commit、冷重开、共享/循环；删除旧 record CLR 后 reader/Upgrade；版本漏升拒绝 |
| G3：真实包及收口 | PackageReference 消费、Publish/Verify、全量验证、独立审阅与文档 | 无 ProjectReference/手工 Analyzer 的两代消费者；旧 history filename/hash 不变；升级 Base 后续 Delta/NoChange |

验收素材按独立风险组织，不为每种形状做笛卡尔积：

1. 一组 SG 正负矩阵覆盖 mutable/readonly、非泛型/generic、positional/body、partial 拆分、
   属性与显式字段混合、private 字段、关键字标识符和泛型约束；**未标注 positional 成员必须报错**。
   用户替代了 positional 合成属性时，只接受实际字段上的有效分类，不把参数 Attribute 猜移到别处。
   误标负例包括无 backing 的计算属性、同名显式字段/属性替代，以及 Attribute 的 alias/全限定写法，
   不以 Attribute 短名文本匹配代替符号判断。
2. 通过生成器真实编译执行 Capture/StateEquals/PrepareBase/PrepareDelta/Apply/Hydrate。
   setter/getter/初始化器计数证明字段路径；引用保持 ID，Transient 不入 DTO，源值不被读取器修改。
   生成 helper 不得新增领域实例状态或干扰 record 的合成 Equals/with/Deconstruct。
3. 一个小 World 覆盖 record 字段、Nullable、泛型嵌套、数组/List、Dictionary 两槽；
   Key 中 Nullable/string/identity 成分与已有闭包组合；共享和环至少有一个见证。
4. Dictionary value 修改产生 PatchValue；替换 Key 的持久 Timestamp/字符串实例产生 Remove+Add，
   即使当前业务查找等价。额外非 Key record 的 Transient 修改无变化；Key 依赖 Transient 造成 canonical 重复明确拒绝。
   验证普通 record Default 的模式 4 与一个 Application 模式 5，冷重开后无模式漂移。
5. 持久等价的普通 struct/record、参数重排/重命名：独立编译的 manifest/Schema/body 一致；
   生成 API 可迁移而持久布局不需要伪升版。持久槽或 inline child 改变而漏升版仍失败。
6. 两代真实包：V1 generic readonly positional Key + 嵌套值；V2 删除旧 CLR 名称，
   保留 history 并显式升级旧 DTO。完整依赖预检、空容器能力检查、Upgrade 后仍 live 的容器强制 Base，
   再次保存无伪变化，真实修改恢复 Delta；不能以只读历史 DTO 代替 editable Load/Commit 验收。

推荐分工：主线程持有成员发现/分类模型与集成；一名 subagent 在约定字段接缝后负责投影生成；
一名负责 SG/Repository/history 测试；一名负责真实包消费者；独立审阅覆盖持久布局稳定、误标不丢字段和 comparer 边界。
按文件明确排他所有权，避免共同修改 Generator 主文件；Windows dotnet 命令由主线程串行运行。

验收命令为根 `dotnet build DurableGraph.slnx`、`dotnet test DurableGraph.slnx`，
新增 record 真实包入口，以及同一包 feed 的 Generic / CompositeDictionary 相关回归。
不修改包依赖边界时不自动重跑全部独立 Probe；失败或实际改动涉及其他路径时再扩大。

## 7. 设计收口与停止条件

源码调查和独立方向审阅均推荐本片；已讨论“不做 positional 收益不足”与“为两套生成路径重复加逻辑”的代价，
据此选择完整常用 record struct 外观 + 现有 Family。局部 Runtime 见证排除了 readonly/generic backing-field 访问的明显技术阻塞。

G0 以 DG0021 报告未知 storage / 未绑定到字段的分类标签，沿用 IFieldSymbol 及所属属性的源位置；
生成 helper 碰撞沿用 DG0020。这些不需要新增业务规则。若后续出现必须执行领域 getter/constructor、无法确定实际字段而可能静默漏数据、
必须改变同 key 布局政策等问题，先停止相关路径并回到设计，不以特殊反射序列化后端绕过。

本片完成后，ValueTuple、跨程序集和 SchemaStore 自举仍按各自消费者及路线图触发；
不因 record 验收自动进入下一轮施工。

设计阶段文档验收：独立完整复审未发现阻塞；补入 field-backed 属性的 storage/行为区别及 alias、
错误 target、positional 替代成员的负例。5 份 Markdown 的 398 个本地链接、35 个锚点和
`git diff --check` 通过。产品代码未改动，未重跑产品 build/tests；§4.1 的局部机制实验单独执行。

## 8. 实施与验收账本

本轮只实现 §2–6：record struct 的字段适配、现有 Family 投影及完整组合/历史交付。
持久格式、Runtime 槽模型和 Dictionary 比较合同不变；不引入 property 调用、record class、ValueTuple 或跨程序集支持。
成员仍以 IFieldSymbol 为接缝；当前 backing field 从 AssociatedSymbol 识别、MetadataName 发出访问器，历史只保留原 FieldId/槽。

| 要求 | 负责路径 | 验收 | 状态 |
|---|---|---|---|
| G0 分类与诊断 | [Records helper](../../../src/DurableGraph.Generator/DurableSchemaGenerator.Records.cs)、Generator/Ancestry/TemplateHistory | [形状与误标 tests](../../../tests/DurableGraph.Tests/RecordStructGeneratorTests.cs) | 已验证 |
| G1 当前投影 | [GenericProjection](../../../src/DurableGraph.Generator/DurableSchemaGenerator.GenericProjection.cs) | [静态 body/副作用隔离 tests](../../../tests/DurableGraph.Tests/RecordStateProjectionTests.cs) | 已验证 |
| G2 图与历史 | 复用原 Runtime/StateStore，未新增 record 专用 reader 或 Upgrade | [图与 keyed Delta tests](../../../tests/DurableGraph.Tests/RecordStructGraphTests.cs)、[history tests](../../../tests/DurableGraph.Tests/RecordStructHistoryTests.cs) | 已验证 |
| G3 包与收口 | [真实包消费者](../../../experiments/PackageConsumerProbe/RecordConsumer/README.md)、独立审阅 | 两代删除旧 CLR、显式双槽升级、Base/Delta 续写 | 已验证 |

实现保持局部：仅 SG 增加 record 资格、字段检查和 current 投影；Runtime 仅补 Attribute 的用法说明。
未扩大普通 class/struct 的 property 发现范围，未改变 Schema/history 模型或任何 body codec。
record 的 private backing accessor 复用原 readonly helper 名；Capture 只交付 ref readonly，Hydrate 仍从 default 局部值恢复。

误标检查直接使用 Roslyn 对 AttributeSyntax 的 Symbol/CandidateSymbols，不维护第二套 alias/后缀查找算法。
独立审阅推动了这项简化：局部实测证明即使 field target 被忽略，编译器仍给出符号或歧义候选。
普通 `[Transient]` 在同名别名与 DurableGraph 特性同时可见时可能有歧义；明确的 `[@Transient]` 别名则按真实类型判断。
两类情况以及普通非 Attribute 类型同名的后缀选择均有回归，避免静默忽略 Durable 标签或误拒无关 Attribute。

验收记录（2026-09-10，Windows dotnet 由主线程串行运行）：

- 实施前根 build：0 warnings / 0 errors；基线 **2091** 项测试通过。
- 最终 `dotnet build DurableGraph.slnx --no-restore`：**0 warnings / 0 errors**。
- `dotnet test DurableGraph.slnx --no-restore`：**2146 passed，0 failed，0 skipped**；Runtime/SG 1290、StateStore 598、Serialization 103、Storage 155。
  新增 56 项 record 验收，移除一条已过期的 record 拒绝用例；ref struct 的拒绝回归保留。
- 相关 `FullyQualifiedName~Record` 筛选：81 项通过；完整集再次包含相同用例。
- `Run-RecordProbe.ps1` 两代 Publish/Verify 通过；history **4 → 7**，旧 filename/hash 不变。
  V2 删除旧 generic Key、Part、Value CLR 声明；192 次显式 Key/Part/Value 回调，两字典 Base 后恢复 NoChange/Delta，冷重开不重复升级。
- 独立最终审阅未留下阻塞；源字段副作用隔离、完整 DTO key 差异、身份环、空容器升级能力与 helper 碰撞均通过实际生成执行测试。
- 复用同一包 feed 的 `Run-GenericProbe.ps1` 与 `Run-CompositeDictionaryProbe.ps1` 均通过，
  包括三代泛型、删除旧 inline CLR、当前 comparer 与同 Schema 行为变化的既有回归。
- 集成差异检查通过；9 份 Markdown（含诊断发布表）的 466 个本地链接、43 个锚点均有效。

真实包产物位于忽略目录 `experiments/PackageConsumerProbe/obj/record-20260910045526-19264-60b589aa`；
产品构建/测试日志分别在 `obj/db056-final-build.log`、`obj/db056-final-tests.log`，不加入版本控制。
