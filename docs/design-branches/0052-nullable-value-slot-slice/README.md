# DB-052：可组合 Nullable 值槽

> 文档整理：2026-10-14；来源为 Git `8ef9fca` 中的 `docs/design-branches/0052-nullable-value-slot-slice.md`，正文保留当片合同与历史验收含义。
> 阅读范围：Nullable 字段/泛型/数组/List、精确 child 与两代包证据见 §9–10；history v6/SCB1 v2 仅保当片版本，enum/Dictionary 另由后继扩展。
> 当前能力与施工焦点见 [PROJECT-STATE](../../../src/PROJECT-STATE.md)；旧稿中的“下一步”不自动成为当前工作单。

> 状态：Implemented / G0–G4 已验收，2026-09-09。
> 当前能力见 [PROJECT-STATE](../../../src/PROJECT-STATE.md)；以下合同的完成情况以末尾验收账本为准。

## 1. 问题与选择依据

问题：普通领域模型如何直接保存 `int?`、`Point?`、`Box<T>` 中的 `T? where T : struct`，
并让这些值自然出现在数组/List 中，继续满足历史精确读取、显式 Upgrade 和增量续存？

DB-051 已完成默认 List Delta 竞争。下一步推荐补一项常见值形状，而不是继续扩展匹配算法。
这是一片跨 Schema/history/生成器/runtime 的工作，不是只给 BinaryPayload 增加一个方法。

| 候选 | 本次判断 |
|---|---|
| 单独 Nullable | 推荐。补齐可选值表达，复用静态值操作、既有引用图与 owner Upgrade，没有新的对象身份或 comparer 合同 |
| enum 与 Nullable 合片 | 分开。enum 的稳定 nominal 身份、成员/底层类型演化及历史 DTO 表示仍需独立选择 |
| Dictionary/Set | 保留消费者触发条件；key、comparer、Upgrade 后碰撞和索引重建不能直接照搬 List |
| 再做综合应用 Probe | 已有真实包、历史迁移、发布恢复见证；没有具体宿主需求时，另造合成 World 的新增证据有限 |
| 开放模板持久化重构 | 尚无新证据满足[路线图 §3.1](../../DurableGraph-research-roadmap.md#31-版本化表示类型头的统一寻址)的净简化门槛 |

最小成功标准：同一公开 GraphSession 保存含 `Point?` 与共享 `List<Point?>` 的 World，
冷重开恢复状态与引用；换成保留历史的新程序后精确读出旧 DTO、显式升级、强制 Base 续存，
再次无修改保存不出现伪变化。旧 Point CLR 声明可删除，历史代码仍从 history 生成。

## 2. 当前代码证据

- [TypeExpr](../../../src/DurableGraph/Schema/TypeExpr.cs) 仅有 builtin/named/parameter/array/List 构造。
- [DurableFieldInfo](../../../src/DurableGraph/Schema/DurableFieldInfo.cs) 的完整槽只用 TypeTag、TargetType、InlineSchema 表达；尚无可空内建值包装。
- [StateValueBinding](../../../src/DurableGraph/Runtime/Binding/StateValueBinding.cs) 的 IStateOps/IValueProjection 已提供静态组合能力，状态参数要求 unmanaged。
  CLR `Nullable<TState>` 不符合这里的泛型约束，不能直接充当生成 DTO 的状态类型。
- [StateDefinitionBinding](../../../src/DurableGraph/Runtime/Binding/StateDefinitionBinding.cs) 的 StateFieldTemplate 固定版本目前只接受顶层 named inline。
  [StateBindingContext](../../../src/DurableGraph/Runtime/Binding/StateBindingContext.cs) 的 Match、NominalType、WithFieldId 与 exact 依赖证书需要识别包装内的值依赖。
- [StateModelSnapshot](../../../src/DurableGraph.Persistence/StateModelSnapshot.cs) 的 current/stored 值绑定与 CLR/nominal 双向映射需要一起扩展。
- [现有拒绝回归](../../../tests/DurableGraph.Tests/ScalarStateDtoTests.cs) 的 `ScalarTypeRecognitionKeepsUnsupportedValueKindsOutsideTheSlice`
  同时列出 enum 与 int?；实施时只迁出 Nullable 断言，保留其余负例，不整体删除该测试。
- [值 Upgrade](../../../src/DurableGraph/Runtime/Binding/StateBindingContext.ValueUpgrade.cs) 的 provider 匹配、子依赖选择与 requirement 收集直接使用 InlineSchema；
  [List Upgrade](../../../src/DurableGraph/Runtime/Binding/StateBindingContext.ListUpgrade.cs) 的元素预检也有该假设。仅增加 codec 会遗漏历史能力。
- [Schema 目录 codec](../../../src/DurableGraph.Persistence/SchemaCatalogWireCodec.cs) 当前遍历字段/元素的直接 InlineSchema 依赖。
  新包装不能成为完整闭包检查的盲点。

独立设计审视同意单独 Nullable 的优先级，并要求把历史布局、显式升级提升和空内容预检纳入同片。
源码事实调查用于确定修改入口；本规划不把尚未运行的测试当成可行性证据。

## 3. 建议范围与状态语义

支持 CLR `Nullable<T>`，其中 T 是当前受支持的非可空值类型：13 种标量或 Durable inline struct，
包括泛型 struct。完整组合须包括字段、嵌套 struct、已支持用户泛型实参、SZ/rank 2–4 数组和 List 元素。
`T? where T : struct` 在闭合时仍须校验 T 的实际支持能力；单有 CLR struct 约束不等于有 codec。

不随本片开放 enum、decimal、其他 CLR 值类型、boxed value、object/interface 槽、Dictionary、跨程序集生成或数组协变。
Nullable 引用注解 `string?`/`Node?` 不变；这里处理的是有值/无值的 CLR 值构造。
直接 `Nullable<Nullable<T>>` 非合法 CLR 闭合，拒绝；`Wrapper<Point?>?` 等合法递归组合仍支持。

建议预制 `NullableState<TState> where TState : unmanaged`（名称施工时可调整），
包含 HasValue 与嵌套状态值。默认值表示 absent；受控构造保证 absent 的内部值规范化为 default。
它不是带 ObjectId 的对象、不独立登记为 live 行，也不是新的用户 Durable Schema family。

- absent 不调用 child Capture/Hydrate/StateEquals/VisitReferences/编码或业务转换；隐藏字段不形成引用边。
- present 使用 child 的同一套投影、相等与 body；含引用的领域 struct 转成嵌套 ObjectId 状态，不能保留领域引用。
- 两个 absent 相等，present 与 absent 不等；两个 present 使用 child StateEquals，浮点仍按位。
- 对外恢复为普通 `T?`；不执行用户 struct 构造器或初始化器，沿用已有 Hydrate 语义。
- 清空最后一条引用路径后仍由完整可达性差集产生 Remove；不单独为 nullable 建立 GC 机制。

## 4. nominal、exact 与历史表示

建议新增内建 Nullable nominal 构造，同时给完整值槽增加“Nullable + exact child 槽”。
例如 nominal 为 `Nullable<Point>`，exact child 则绑定 Point V1 的完整 Schema。
包装不拥有独立业务版本；child exact 变化改变包含它的 owner 布局，既有 inline 升版传播规则不能被包装截断。
引用边依然截断 exact 传播：Point 内引用的 Node 升版，不因多了一层 Nullable 就传播到 Point/owner。

实现采用最小不可变 child 布局持有者，避免在现有 record struct 内形成无限大小的递归值字段。
外层 FieldId 是成员位置；child 槽使用统一规范位置，不创造额外用户 FieldId。
不要为 Nullable 伪造用户 DefinitionId、生成员工 Schema，或顺势重构为通用类型描述平台。

必须贯通以下路径，尽量共用专门的 child/exact 依赖枚举，不能只修一处 validator：

1. 完整槽 equality/hash、类型打印、nominal 映射、WithFieldId、参数替换和递归深度限制。
2. Schema 版本传播与 history：固定 `Point?` 要记住 Point 的历史 exact 版本；
   `T?` 要从完整 child 槽提取 T 的状态操作数。不能用当前版本或 CLR DTO 类型反猜布局。
3. SG DTO 参数表达与历史端点反推：保留 Nullable 包装层和内部参数来源；
   区分 `T` 恰好闭合为 Nullable 与声明本身为 `T?`，保留 phantom nominal 身份规则。
4. SchemaStore 目录登记/重放的 child inline 依赖、同 key 冲突、单批次容量预检及原子安装。
5. 普通 binding 缓存、Upgrade 中间端点与数组/List 元素的完整 exact requirement 闭包。

G0 选择扩展现有模板的固定 child 版本语义，未新增平行递归模板模型，具体接口见 §9。
编译/绑定见证覆盖固定字段、开放参数及泛型组合，未将 `InlineVersion` 忽略或只实现标量特例。

新 tag/TypeExpr 构造码及 history 版本已在 §9 统一分配，避开 history-only tag 17。
保留已接受构建 history 的文件/hash与读取合同；盘上原型数据没有兼容需求，不新增旧格式读写分支。
需要改目录版本时同步明确拒绝旧格式。StateStore Base 的单 RepresentationId、对象 Delta prior 和 Storage wire 无需因此改形状。

## 5. Base / Delta 建议

Base 使用一个严格的 absent/present 标记；present 紧随 child Base。拒绝未知标记与截断，空值不带 child 内容。

同 exact 槽 Delta 的建议编辑语义：

| prior → current | 编辑内容 |
|---|---|
| absent → absent 或相等 present | HasChanges=false，不生成实际 child patch |
| present → absent | Clear |
| absent → present | Set + child Base |
| 不等 present → present | Patch + 已准备 child Delta |

实际码值见 §9，独立黄金字节见 NullableStateBodyTests；Clear/Set/Patch 对 prior 的前提严格验证。
Patch 不允许“父宣称变化、child 实际未变”，空变化 body 不作为持久 child patch。
各 reader 消费自己的字段边界，尾随数据由相应完整 body 边界检查，不能让嵌套 reader 吞掉后续字段。
Base/Delta 字节计量与上层准备、策略接口继续复用；不新增 Nullable 匹配算法或性能预算配置。

## 6. 显式可组合 Upgrade

不能通过“当前恰好为空”逃过历史能力检查。建议在现有显式 ValueUpgradeRuleSet 增加专用 Nullable 提升选项，默认关闭，
将已绑定 child 工具提升成 wrapper 工具；不建设任意 wrapper adapter 平台。

推荐解析顺序：显式匹配的 wrapper provider → 已允许的完整 KeepExact → 明确开启的 Nullable 提升 → 拒绝。
显式候选歧义、签名错误、expected layout 不符或依赖失败直接拒绝，不进入后续分支。
提升仅处理两端都是 Nullable 的情况，递归使用该规则集选择 child 工具；缺 child 规则不自动用默认值补齐。

wrapper provider 的版本适用性必须来自 child exact 布局：`Nullable<Point V1> → Nullable<Point V2>`
与 V2→V3 不能因为 wrapper 自己没有版本就匹配为同一条边。建议 Runtime provider 的两端 inline 版本选择
在 Nullable 模式下指向其直接 child inline Schema；标量 child 没有 inline 版本。
该选择已落到现有 provider metadata，两个相邻 provider 的无歧义回归见 NullableUpgradeTests。
ExpectedSource/Target 仍只校验已选候选，不用作筛选候选或隐藏错误的条件。
首片只要求 Runtime metadata 可显式表达 wrapper provider；SG 生成提升开关与既有 child 规则即可，
不扩展 `DurableValueUpgrade` 为任意 wrapper 属性入口。显式 wrapper provider 若声明依赖，
选择域为其 child inline Schema，缺该 Schema 时明确拒绝；不得将 Nullable 假扮为新的声明段。

提升保持 HasValue：absent 产生 absent，present 才执行 child 业务转换。
child 规则、全部中间 exact 依赖和类型约束在 owner 首次业务 callback 前绑定/校验，
即使当前字段为空、数组/List 为空也一样；这与执行期跳过 absent child 是不同阶段。

class owner 仍显式声明依赖并调用 `UpgradeContext.GetValueUpgrade` 返回的 wrapper 工具；
数组/List owner 使用显式配置的元素规则集，复用同一提升机制。升级对象仍由 owner 控制，
nullable 没有独立对象 Upgrade，不增加图读取/新 ObjectId 权限。
本片不自动提供 `T → T?`、`T? → T`、空值填充或跨 family 的业务策略；用户 owner 转换可明确决定这些行为。

## 7. 施工顺序与验收闸门

| 阶段 | 实施内容 | 必须观察到的结果 |
|---|---|---|
| G0 合同见证 | 盘点分派/依赖入口，冻结 child 槽/历史模板/DTO 参数形状、码值及提升优先级；先写小编译与绑定见证 | `Point?`、`T?` 与 `Box<Point?>` 三种来源都能表达 exact V1/V2；nullable 状态满足 unmanaged，旧 CLR 删除不阻止端点推导；late child 冲突无法被包装隐藏 |
| G1 元数据与静态值操作 | nominal/exact、目录/history 编解码、NullableState、Base/Delta/Capture/Hydrate/refs、current/stored binding | 独立 golden、无值/有值转移、浮点位相等、引用投影；未知码/坏 child/深度超限拒绝；完整 child 依赖按既有目录规则登记 |
| G2 SG 与 history | 普通/Family 两条生成路径、泛型参数、历史依赖与版本传播 | 普通字段、readonly、泛型 struct、数组/List 全部组合；漏升 owner 版本诊断；删除旧领域 struct 仍生成历史 reader |
| G3 显式值升级 | 专用 nullable 提升、owner依赖、数组/List元素工具与完整 requirement | V1→V2 显式 child 规则；空值/空集合缺能力仍在 callback 前拒绝；错误显式 provider 不回退；晚登记冲突覆盖缓存计划 |
| G4 产品闭环 | 真实包跨程序历史消费、GraphSession、根 build/tests、独立审查与文档 | 历史 exact decode→Upgrade→恢复→强制 Base→同版 Delta/无伪变化；共享/循环/Remove 与失败基线保持原合同 |

建议并行分工：主线程拥有 G0 合同和类型/模板接口；合同冻结后分别委托 runtime body、SG/history、
StateStore 目录/闭环测试；Upgrade 与 exact 闭包审查单独分派。按文件划清所有权，依赖未冻结不同时猜接口。
所有 build/tests/package 验证串行执行；主线程审查集成 diff 与最终证据。

G0 如果发现必须引入通用 Schema 平台、隐式业务迁移或新跨对象权限，应停止扩大范围并重新讨论，
不能以只支持 `int?` 冒充本片完成。局部 API/码值选择可在上述合同内由实施团队确定并记录。

关键回归矩阵除上述闸门外还包括：present struct 内共享/循环引用、清空后 Remove、
捕获后领域变化不污染候选；`List<Point?>` 的 NoChange/区间 Patch；all-null 与空集合仍持有 exact 元素布局；
两个相同 nominal 但 child exact 不同的布局不能误命中 binding/工具缓存。

## 8. 交付与后继边界

实现与验收已同步到 PROJECT-STATE、术语表和目标支持合同，证据集中记在本分片及真实包见证。
冻结的旧目标稿未修改。
enum、Dictionary 和外部真实宿主接入分别按后续需求选择；不自动续做这些分片。

## 9. 实施合同与验收账本

G0 接口裁决：TypeTag.Nullable=18（17 仍为 history 参数），TypeExprKind.Nullable=9；
`DurableFieldInfo.Nullable(fieldId, child)` 持有不可变 `NullableValueLayout.ElementSlot`（child FieldId=1）。
`ValueSchema` 返回直接 inline 或 Nullable child 的 inline Schema，供完整依赖遍历使用；
`InlineSchema` 原本仅属于 tag16 的含义保持。
StateFieldTemplate/StateParameterTemplate 的 InlineVersion 在 Nullable(named) 上解释为直接 child 版本，
不新增平行的递归模板体系；Match 同时记录包装与内部参数来源。

history/manifest 新写 v6，Nullable pattern 为 `q(child)`；字段 `id|18|q(child)`，named child 必须再带正整数版本。
旧构建 history 保留读取与 hash；旧格式不得带 q。持久 Schema 目录 SCB1 新版本为2，只接受2；
Nullable字段编码为tag18+child类型描述，child inline仍引用先前目录ID，不登记独立Nullable对象。
Base标记0/1；Delta Clear=0、Set=1、Patch=2，各自校验prior前提。

含 Nullable 的声明沿现有 UsesGenericTemplate 选择 Family 路径，生成状态使用 `NullableState<TState>`。
普通非泛型领域声明也可使用它；首次加入 Nullable 后，生成 DTO 外观遵循 Family 规则。
不向旧普通 body emitter 复制第二份 Nullable 代码；保留旧普通声明的回归与历史迁入覆盖。

| 要求 | 实施归属 | 验收状态 |
|---|---|---|
| nominal/exact/模板闭包 | Runtime核心元数据与绑定 | 已验收，NullableSchemaTests |
| Nullable状态与静态body | Runtime值操作与独立golden测试 | 已验收，NullableStateBodyTests |
| 生成/history | Generator、Shared、Build | 已验收，NullableGeneratedTests / NullableTemplateHistoryTests |
| 目录与current/stored闭合 | StateStore | 已验收，NullableCatalogTests |
| 显式提升/历史端点/全链校验 | Runtime Upgrade | 已验收，NullableUpgradeTests |
| 真实包、产品闭环、独立审查 | 主线程整合 | 已验收，NullableGraphIntegrationTests / NullableConsumer及独立只读审查 |

## 10. 验收证据

本轮基线：根构建零警告/错误，完整测试1612项通过。
最终 `dotnet build DurableGraph.slnx --no-restore` 零警告/错误；
`dotnet test DurableGraph.slnx --no-build` **1756项通过**：Runtime967、StateStore531、Serialization103、Storage155。
所有构建/tests/package命令由主线程串行执行。

- [元数据与绑定](../../../tests/DurableGraph.Tests/NullableSchemaTests.cs)：完整布局相等、固定版本、T/T?参数来源、泛型基类替换、共享DAG和晚登记冲突。
- [静态body](../../../tests/DurableGraph.Tests/NullableStateBodyTests.cs)：13标量、浮点位语义、独立golden、Clear/Set/Patch、非法prior/截断、absent sentinel、零字节child与真实引用边。
- [生成与历史](../../../tests/DurableGraph.Tests/NullableGeneratedTests.cs)、[history协议](../../../tests/DurableGraph.Tests/NullableTemplateHistoryTests.cs)：普通声明迁入Family、readonly、递归组合、版本传播、删除旧CLR后的真实旧body读取、v6语法及v1–v5保留。
- [目录](../../../tests/DurableGraph.Persistence.Tests/NullableCatalogTests.cs)：tag18和整数依赖golden、SCB1v1拒绝、原子冲突与ID、无领域CLR的historical绑定、空集合reader缓存、kind和深度约束。
- [Upgrade](../../../tests/DurableGraph.Tests/NullableUpgradeTests.cs)：显式provider优先级、相邻child版本选择、缺工具/错误签名/expected/dependency不回退、全空集合能力预检及缓存中间布局晚冲突。
- [冻结与Discard](../../../tests/DurableGraph.Tests/NullableGraphIntegrationTests.cs)：嵌套组合、候选不随领域修改、Discard不退休原引用身份。
- [真实包见证](../../../experiments/PackageConsumerProbe/NullableConsumer/README.md)：独立feed的八个包，history3→5文件并验证hash；
  V2删除旧Point CLR，exact读取不执行Upgrade，35次present转换属于四个owner，共享List只升级一次；
  四owner强制Base→无变化→List Delta，冷重开、历史重读、清空后Remove循环Node和string全部通过。
  执行命令 `./experiments/PackageConsumerProbe/Run-NullableProbe.ps1`；
  忽略的实物目录 `experiments/PackageConsumerProbe/obj/nullable-20260909135835-1860-7c739d18`，日志 `obj/db052-nullable-package.log`。
- 使用同一八包 feed/version 运行 `Run-GenericProbe.ps1 -PackageSource <feed> -Version <version>`，
  原泛型消费者的三代历史、缺闭合升级拒绝、显式闭合业务规则、删除旧inline CLR与稳定续存全部通过。
  实物目录 `experiments/PackageConsumerProbe/obj/generic-20260909140231-18844-a65f4d8e`，日志 `obj/db052-generic-package.log`。

独立审查覆盖Runtime/Upgrade/exact闭包、Build/SG协议对称和真实包断言，无未解决阻断。
集成时修正局部变量/测试类型推导编译错误，以及两处新测试问题（既有诊断ID、Seal后提前Dispose）；
没有改变Capture候选生命周期或扩大已批准范围来消除失败。
文档集成检查：10份Markdown、462个本地链接、48个锚点通过；最终diff无空白错误。
