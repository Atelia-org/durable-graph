# DB-043：可组合数组与统一引用对象路径

> 文档整理：2026-10-14；来源为 Git `8ef9fca` 中的 `docs/design-branches/0043-vector-array-object-slice.md`，正文保留当片合同与历史验收含义。
> 阅读范围：SZ/rank 2–4、jagged/泛型元素和数组 owner Upgrade 的当片证据见 §10；原 history/schema/Base 版本为历史格式，List 等容器另由后继扩展。
> 当前能力与施工焦点见 [PROJECT-STATE](../../../src/PROJECT-STATE.md)；旧稿中的“下一步”不自动成为当前工作单。

> 状态：Implemented — 2026-09-08，G0–G5 已完成并通过根构建、完整测试及真实包验收。
> 本文保留施工合同；实现映射、验证和审查修复见 §10。
> 当前基线：[PROJECT-STATE](../../../src/PROJECT-STATE.md)；已选 [MVP 边界](../../DurableGraph-target-design-v0.md#mvp-功能边界)。

## 1. 问题、证据与成功标准

问题：能否让受支持的槽类型在数组/泛型构造下组合，并让 string、用户 class、数组共享引用对象的生命周期？
成功标准：真实 World 包消费者完成 Capture → Commit → reopen → Load → 原实例再次 Commit，包含
泛型 struct 元素、交错数组、有限多维数组、共享/循环及一次历史元素 Upgrade；Base/Delta 与旧 Revision 均可读。

初稿 `a1bfb30` 仅允许声明处闭合的 scalar/string/class Vector。2026-09-08 对 Robird 旧模式和当前代码的
独立复核撤回该缩界：它是施工容量取舍，不能作为 SG 无法组合或数组元素能力必须弱于字段的结论。

现有可复用基础：

- [StateValueBinding](../../../src/DurableGraph/Runtime/Binding/StateValueBinding.cs)：IValueProjection 负责 Capture/Hydrate，
  IStateOps 负责 Base/Delta/引用遍历。字段与元素共享这些能力。
- [SG 泛型工厂](../../../src/DurableGraph.Generator/DurableGraphGenerator.GenericProjection.cs) 已用
  ResolveCurrentValue 和泛型方法冷闭合，把 state/ops/projection 交给静态循环；无需穷举组合。
- [StateModelSnapshot](../../../src/DurableGraph.Persistence/ModelExecutionEnvironment.cs) 已区分 current projection 和
  stored exact value reader，且引用槽不递归闭合目标 body。数组应遵守同一条边界。
- Robird 的类型构造操作码、泛型数组遍历器与 ref accessor 是模式素材；旧 TypeCodec.cs 整体注释，
  反序列化有未完成入口，未进行完整旧项目 round-trip 验证，不直接移植其格式/工厂。

## 2. 范围

本片支持零下界 SZ `T[]` 及 rank 2、3、4 数组，rank 上界推荐取 4。元素复用目前支持的完整槽集合：
13 种标量、string、durable class、inline struct，含受支持的泛型 class/struct，并递归允许数组元素。

必须验证的组合包括 `Holder<T>.Items : T[]`、`Holder<T>.Value : T` 闭合为数组、`Point[]`、
`Pair<int,string>[]`、`Box<int[]>`、`int[][]`、`Point[][,]`，以及 struct 中再含数组引用。
语法开放不等于运行时开放：SG 保存 Parameter 模式，实际 Capture/持久类型必须闭合，并满足既有泛型约束、
定义登记、TypeExpr depth/node/arity 上限。引用槽绑定不展开目标 body，合法引用递归不会变成无限布局展开。

继续拒绝非零下界、非 SZ rank-1 `T[*]`、rank > 4、boxed value、未支持的元素族、object/interface 通配字段。
数组引用的声明/实际数组类型本片要求 exact 相等；普通 class 元素仍可指向符合 nominal 约束的派生实例。
协变是独立后继问题，见 §9，不影响一般数组组合。World 根仍为一个固定的非空 durable class。

不新增 BCL 容器实现、程序集、通用插件注册平台、Reflection.Emit 后端、chunking 或压缩；
当前类型集合之外的 enum/nullable/decimal 等不因数组自动获得支持。

## 3. 统一 object 路径

### 3.1 实际类型分派与槽约束

引用操作的示意入口：

```csharp
ObjectId CaptureObject(object? value, TypeExpr declaredType);
T? ResolveObject<T>(ObjectId id) where T : class;
void VisitObject(ObjectId id, TypeExpr declaredType);
```

object 是框架接受所有受支持引用实例的内部表示，不授予任意 CLR 类型序列化能力。具体流程：

1. 校验声明约束；null 返回 ObjectId(0)，不分配、不取得实例类型。
2. 空 string 规范化为 string.Empty；其余实例不按值合并。
3. 根据实际 GetType() 解析本操作 snapshot 中的 string / 用户 durable / array binding；未知类型拒绝。
4. 每条入边先校验声明约束与实际类型，再按引用相等查询统一 identity map。
5. 首次出现先分配 ID、登记 binding 和对象，再入队捕获；重复引用只返回既有 ID。

当前 string 确有专用分支，但尚未与 class 共享全部对象操作。新路径将两者与数组一起接入；
string 可继续立即冻结不可变内容，调度优化不拥有第二套 ID 权威。现有 CaptureString/CaptureDurable
如继续保留，应为薄适配器；所有路径最终遵守同一身份登记和类型验证合同。

### 3.2 对象操作与静态槽操作

抽取实际由三类对象消费的 object binding，职责为 Capture、Prepare、exact Decode、Normalize、
VisitReferences、Allocate(currentRecord)、Hydrate(object, currentRecord, objects)。名称/拆类由实施选择。
其中 current CLR binding 与 historical state reader 必须可独立取得，不能为了 Decode 急切要求 current CLR 模型。

分配需要 current record：class 使用其分配器，array 使用 shape，string 使用已解码的不可变实例；
string Hydrate 无操作。对象层允许一次类型擦除/分派，字段和元素热循环继续静态操作。
现有 StateModelBinding<TDomain,TState> 可作为用户 class 的强类型适配器，不必为了保护偶然的内部签名
继续让共用目录受 DurableBase 限制，也不把 unmanaged 限制扩散到数组状态容器。

ObjectReadTable 内部统一为 ObjectId→object，类型化访问仍验证实际对象。统一分配的实例唯一性规则：
不同 ID 的 class、array、非空 string 不得合并；历史多个空串 ID 可对应同一 string.Empty。
保留现有 Empty 反向 identity map 选择最小 source ID 的规则，以及下一次 Capture 形成真实 Delta/Remove 的行为。
ObjectStateRecord 的 layout 使用受控判别值：String / exact DurableSchema / exact ArrayLayout，
不能由互不约束的 nullable 属性拼出非法组合；Parent provenance 必须保留 exact source layout。

### 3.3 引用槽与类型码

wire 字段 tag 15 保持数值，源码收敛为 TypeTag.ObjectReference，允许 closed Named 或 Array TargetType。
引用 ID 的数值读写共用静态操作，visitor/类型校验使用完整槽约束；不为每类 array/BCL 新增 Reference tag。
现有 string 字段 tag 4 继续作为唯一 canonical string 槽编码，语义上映射到 Builtin(String) 约束；
不另接受 tag15 + Builtin(String) 造成两个等价 Schema 表示。string 对象依然进入统一 object 生命周期。

## 4. 类型表达与暂定持久布局

### 4.1 递归组合

TypeExpr、SG TypePattern 及所有替换/匹配/排序/历史检查共享同一构造语法：

```text
1 Builtin(tag)
2 Named(definitionId, ordered arguments...)
3 Parameter(ordinal)               // 仅开放模板；持久闭合表达拒绝
4 VectorArray(element)
5 Rank2Array(element)
6 Rank3Array(element)
7 Rank4Array(element)
```

公开构造器可采用 VectorArray(element) 与校验 rank 的 MultiDimArray(element, rank)；
不伪装成 Named("$Array", ...)。开放数组允许 Parameter 子表达式。跨构造的 depth/node 限制统一计数，
不能只限制 Named 子树；替换与所有访问者不能把未知 kind 默认解释成 Named。
上表数值用于 Runtime TypeExpr wire；.dgschema 的 TypePattern 继续使用 canonical 文本，新增数组文本拼写
及字段顺序由 G0 golden 固定。旧 v1–v3 reader 只接受各自原语法，不因兼读而接受 v4 数组构造。

### 4.2 exact ArrayLayout

表示类型头最终是否统一以 VersionedSchema ID 寻址，已按用户要求记入
[路线图待办](../../DurableGraph-research-roadmap.md#31-版本化表示类型头的统一寻址)。本片不裁决最终 TypeCode API；
采用以下局部格式继续推进，保证 exact 读取的元数据来自持久内容与 SchemaStore，而非当前 CLR 布局。

```text
Array Base envelope v3, object kind 3:
    ArrayCodecVersion : canonical UInt32 (= 1)
    ArrayConstructor  : byte (4..7)
    ElementSlot       : tag + parameters

ElementSlot:
    scalar / string   : 原槽 tag
    ObjectReference   : tag 15 + closed TypeExpr
    InlineValue       : tag 16 + SchemaKey

Array raw Base body:
    Length[rank]      : canonical UInt32
    ElementBaseBody[checked element count]  // row-major，末维最快
```

SchemaKey 沿用逻辑闭合 TypeExpr + 版本，inline slot 从 SchemaStore 解析完整 immutable Schema/依赖闭包；
数组本体走内建通道，不制造用户 DurableSchema。element slot 可含数组引用，组合从首版支持，
不以将来换 codec version 为由排除已有槽组合。
ArrayLayout 在内存中保存完整 exact element slot；数组 nominal TypeExpr 从 constructor + slot 派生，
不再持久写一份重复来源。引用 element 不携带目标对象 body；其实际类型/布局在目标自己的 Base 头中。
所有 exact element Schema 依赖在 State Append 前按现有 SchemaStore 规则注册/验证。

推荐新写 SchemaBatch/history v4、Base envelope v3，保留原有严格旧格式 reader；StateRevision wire 不变。
SchemaBatch/history v4 接受上述完整开放/闭合语法的对应子集，不加 closed-array 白名单。
数组 codec v1 的 Delta 不携带类型头，沿终止 Base 的 ArrayLayout 解码。后续布局升级必须从新 Base 开始。
上述值由 G0 的 golden 固定；未来表示类型头统一时，可替换 metadata 编码而复用 element ops、图生命周期与 Upgrade。

## 5. 冻结状态、遍历与对象图

### 5.1 有限数组模板与历史 reader

```text
FrozenArrayState<TStateElement> where TStateElement : unmanaged
    immutable Shape
    private owned TStateElement[] Elements

Current array binding:
    TDomainElement, TStateElement, TProjection, TOps
Stored array reader:
    TStateElement, TOps, exact ArrayLayout
```

四种有限 shape 使用预制强类型泛型模板；SZ/rank2–4 的实际字段访问分别使用 ref a[i]、ref a[i,j] 等。
Capture/Hydrate 复用 IValueProjection；Base/Delta/refs 复用 IStateOps。不要求 SG 为全部闭合组合生成代码，
也不使用 Array.GetValue/SetValue 逐元素装箱。冷闭合可用现有泛型工厂模式。

元素是 struct 时，冻结容器保存其生成 DTO；是引用时保存 ObjectId。交错数组的外层只保存内层 ID。
owned buffer 不公开可变别名；任何后续领域元素修改不得污染 Candidate/Previous。历史 reader 依据 exact slot
取得 retained DTO/ops，即使旧领域 struct 已删也可 exact decode；可编辑 Load 的 current/Upgrade 能力要求另行校验。

### 5.2 两阶段恢复与身份延续

保留完整 source 解码/Normalize/验证后、按 current World 可达闭包分配的流程：先给全部可达 class/array
分配实例并登记 string，再 Hydrate 所有字段和元素。共享、重复引用、循环和 struct 中的引用同样受 refs-only visitor 验证。
实际值遇到引用时只登记 ID，不把引用对象 body 内联到数组中。

同一 CLR 数组实例的实际类型与 shape 不变。会话内元素修改保持 ID；新建替换数组获得新 ID/Base；
替换为已绑定实例复用已有 ID。旧数组只有退出可达闭包才 Remove，旧 Revision 不受影响。
Normalize 的合法 element layout 升级可以保持同一 ID 并强制 Base；普通 Capture/Prepare 不得把意外的
shape/layout/binding 不匹配静默当成合法升级。成功发布安装原候选及所有引用实例的同一 identity map。

### 5.3 分配与输入验证

读 shape 后校验 rank、零下界合同、每维合法长度、总元素数和 CLR 可分配形状；算术使用 checked。
总元素数还须符合本片单个 frozen TStateElement[] backing 的长度范围；超出时明确拒绝，不截断索引。
任一维零长度的合法数组保留完整 shape，不能因简单相乘的溢出顺序误判合法空数组。
元素最小编码长度为正时，结合剩余 payload 在分配前拒绝明显不可能的长度；零字段 struct 可以有零字节 body，
此检查不能被当成普遍的内存上界。实际内存不足允许以失败结束读取，不能交付部分 World。
通用加载内存预算另见 roadmap；单 Frame 256MB 不等于 CLR backing memory 上限。

## 6. 融合 Delta

所有 rank 使用同一种 row-major 线性索引稀疏流：

```text
(linearIndex + 1), ElementDeltaBody
...
0  // terminator
```

索引是 canonical UInt32。Prepare 每元素恰调用一次 TOps.PrepareDelta，有变化才追加其索引与已准备 bytes；
同一结果回答 HasChanges、D 和 payload。scalar/引用/struct 全部复用现有元素差异语义。
无变化 body 为单个终止符，HasChanges=false；密集 Delta 由原 Base/Delta policy 比较实际准备尺寸，无新数组策略。

Apply 克隆 prior elements，再应用变化；索引须严格递增且在范围内。缺终止符、重复/逆序/越界、
非 canonical 编码、子 Delta 无变化、截断/尾随 bytes 均拒绝，不污染 prior。各 element codec 自定界消费。
same-schema Delta 要求 exact ArrayLayout 和完整 Shape 相等；不在 Delta 中改变类型或尺寸。

## 7. 数组独立 owner Upgrade

### 7.1 最小规则入口

在已有模型目录上显式指定一个数组元素规则集，示意：

```csharp
models.UseArrayElementUpgrades(typeof(ArrayElementRules));
```

该入口引用已经显式登记的 DB-039 StateValueUpgradeRuleSet，不是程序集扫描或新 provider 平台。
同一 snapshot 只指定一套数组入口规则集；不同指定拒绝，同值登记幂等。集合内已有 provider 匹配
元素 nominal 模式与 source/target exact 版本，因而可覆盖不同 struct family、泛型实参和所有 rank。
同 nominal 端点多候选、选中 provider 的签名/完整槽/子依赖不匹配仍拒绝，不回退。

完整 ArrayLayout 相同直接沿用 frozen state，不调用业务 Upgrade。element exact slot 改变时，以 source exact element slot
和 current exact element slot 为端点，复用 PrepareValueUpgrade；首层直接提供两槽，不伪造用户声明/FieldId。
provider 内部的字段子依赖继续使用现有 selector。缺规则集、缺匹配 provider、端点歧义均在该数组首次业务调用前拒绝。

本片不从 v1→v2、v2→v3 自动搜索 v1→v3 路径；需要 v1→current 的显式 provider，作者可在函数中明确组合转换。
这沿用 DB-039 的直接两端值工具规则；数组无用户定义的相邻 owner 版本，不捏造一个自动版本链。
codec-only 格式变化的重写义务与业务值转换分开处理，不因 codec version 改变调用元素 Upgrade。
Exact endpoint/历史 reader 的缺失不能用 current/latest 填补。空数组仍需完成 layout 和升级能力验证，
即使没有元素回调，布局改变后仍标记 RequiresRewrite。

### 7.2 Context 与执行

UpgradeContext 的 owner 信息扩展为 source/target exact object layout 判别值（durable/string/array），
保留 ObjectId 和已有局部预绑定工具。用户 class owner plan 仍校验同族相邻版本；array owner plan 从
source/current state 校验同 nominal 数组类型和 shape，
并持有两端 exact element layout。原 Schema 专用访问器如保留，只在 durable owner 有效；array 上明确报错，
不能返回伪 Schema/null 让用户误判。值规则若读取 owner facts，应通过新的 layout 判别接口处理数组。
数组 shape 来自已解码 source frozen record，作为不可变 owner 调用信息单独传入 Context；target 保持同一 shape。
它不属于 ArrayLayout，不从尚未分配的 current CLR 实例推导。

为每个数组预绑定完整值计划及声明依赖，从两端 element InlineSchema 及声明依赖收集 DB-042 exact requirements，
在调用元素转换前统一核对；缓存命中也复核，不能因数组没有用户 DurableSchema 而跳过依赖证书。
对数组各元素调用一次顶层值工具，生成新的 owned buffer；ObjectId、rank、各维长度保持不变。
Context 与其工具限于本次同步数组升级，可在其元素循环内复用；snapshot 计划不保存 ObjectId、Context 或领域实例。
元素回调可用 source/target element slot 及数组 owner 信息；本片不引入 per-index Context、图读取或 ID 分配。

同一共享数组按自己的 ID Normalize 一次，所有引用方观察同一结果。Point[][] 的升级发生于各内层数组，
外层 ID 槽不级联变更。元素引用转换后的合法性由完整 current 目录统一验证。
任何回调异常不交付部分 World、不修改 source state、不自动尝试另一规则；不承诺撤销用户回调的外部副作用。
每对象首个 callback 前预绑定的保证不扩大为“整批所有对象 callback 前零失败可能”。
仍 live 的已升级数组，下一次 Commit 必须 Base，成功后清除重写义务。

## 8. 施工波次与验收

按依赖逐步合入；一个波次未闭合时，不把中间基础设施称为已实现数组产品能力。

| 波次 | 工作和最小证据 |
|---|---|
| G0 类型/格式 | 完整数组 TypeExpr/TypePattern 替换匹配、history/schema/Base grammar；新旧 golden，Parameter 只在开放模板；精确 inline SchemaKey 解析 |
| G1 统一引用对象 | string/class/array object binding、ObjectReadTable、Capture/refs 路由；先用现有 string/class 回归证明身份/Empty/冻结/恢复不退化 |
| G2 数组核心 | 独立 current binding 与 stored reader；四种 shape、owned 状态、全部现有槽组合、静态 ref 循环与融合 Delta |
| G3 SG/持久图 | 开放 T[]、Parameter→array、jagged/generic struct；Schema 依赖、完整 Revision、两阶段恢复、same-instance Prepare/Commit |
| G4 元素 Upgrade | 显式单一规则集、array owner Context、exact 证书/子依赖、共享数组一次转换与强制 Base |
| G5 产品闭环 | 真实 NuGet consumer 两代历史保存/冷重开/升级/再次提交，旧 Revision exact 读取；完整 build/tests 和独立 review |

G0 的格式证据与 G1 的共用接口固定后，可按明确文件所有权并行 Runtime、Generator、StateStore 施工；
Windows 最终 build/test 串行执行。若实现暴露会改变用户业务语义的新分支，应说明具体反例和推荐后再裁决，
不因实施工作量重新悄悄禁掉组合，也不在没有证据时自动扩大 API。

验收至少包括：

- 新旧 history/schema/Base golden，完整类型节点的深度/数量/arity 上限；未知构造/codec、错 kind、缺 exact
  Schema、同 key 异形、截断/尾随数据拒绝。空数组也验证 element declaration/布局。
- 四种 rank、每维空/非空 shape；所有支持元素类别，开放泛型数组、数组泛型实参、jagged、mixed-rank、
  struct→array→struct 引用递归。非零下界、T[*]、超 rank、协变与未支持元素明确拒绝。
- Seal 后修改领域数组、nested struct 或引用槽不污染 candidate/prior；不同内容相等的数组及不同空数组保持独立 ID。
  string 进入共用路径后，非空引用身份、Empty canonicalization 与历史 Empty 最小 ID 映射不退化。
- sharing/cycle、同实例第二次 Commit 保持 ID、无变化不产生伪更新；新/已绑定替换数组、条件 Remove、旧 Revision 保留旧内容。
- 稀疏 Delta 的首/中/末/多索引、标量位语义、引用 ID、inline 子 Delta；各 rank Apply(Base,Delta)=current。
  无变化、坏索引/终止符/子 body、shape/layout 错配及失败后 prior 未改变。
- 空数组与零字段 struct 的分配验证；正最小编码尺寸下巨型长度+极小 body 提前拒绝，溢长输入不交付部分图。
- 真实策略分别选择 Base 和 Delta，两个结果均冷读；新增 Array envelope/shape 字节正确进入原 B/D/H 计量。
- 两个 owner 共享历史 struct 数组，只转换一次并强制 Base；generic struct 工具复用；相同布局零回调，
  空数组升级零元素回调但仍要求规则并 Base；缺 provider/多候选/未使用的坏子依赖/迟登记 exact 冲突在该数组 callback 前拒绝。
- v1→v3 显式直接规则成功，只有两条相邻规则而无直接规则时明确失败；缺 current 能力不妨碍可用的历史 exact reader。
- 真实 PackageReference history publish/verify 和 cold reopen，根 `dotnet build DurableGraph.slnx`、完整 tests、
  文档链接、格式及独立 correctness review。手写 binding 不能代替 SG/package 交付证据。

## 9. 独立后继问题

- [版本化表示类型头统一寻址](../../DurableGraph-research-roadmap.md#31-版本化表示类型头的统一寻址)：按用户要求暂缓，
  不把本片 ArrayLayout 编码宣称为最终 TypeCodec 架构。
- 数组协变：按实际数组类型闭合 ref 循环在执行上可行；但空 Derived[] 经 Base[] 槽引用时，stored 校验还需
  当时元素族的 ancestry witness，不能用 current CLR ancestry 替代。与历史类型头一起重访；当前 exact 数组约束明确保留。
- 统一加载内存预算、dense/range Delta、pool/chunk/cache 等性能与资源治理，依据实际需求独立推进。
- BCL 容器复用已验证的 object 入口，逐类型确定内容、顺序、comparer 和重建时机；不保存其内部实现字段。

## 10. 本次设计复核记录

2026-09-08：主线程和独立子代理对照旧 Robird 与当前源码，确认字段/元素静态能力可组合；修订
closed-only/无 inline/无 jagged 的初稿限制。用户要求统一 object 实例分派、同意数组独立 owner Upgrade，
并将版本化表示类型头归入待办。该次设计复核只修订文档；后续施工映射如下。

### 10.1 实施追踪

以下为本轮新增或改动的代码与验收入口；各波次均已通过集成验证，命令与审查结论见 §10.2。

| 波次 | 实现入口 | 验收入口与当前状态 |
|---|---|---|
| G0 类型/格式 | [TypeExpr](../../../src/DurableGraph/Schema/TypeExpr.cs)、[共享 TypePattern](../../../src/Shared/SchemaHistoryTypePattern.cs)、[SchemaBatchWireCodec（其后由 DB-046 替换）（Git 历史）](../0091-retired-material-cleanup/README.md#git-history "Git 6dbaf0e:docs/design-branches/0046-unified-schema-catalog-slice/README.md")、[BaseObjectBodyCodec](../../../src/DurableGraph.Persistence/BaseObjectBodyCodec.cs) | [ArrayTypeExprTests](../../../tests/DurableGraph.Tests/ArrayTypeExprTests.cs)、[ArrayTemplateHistoryTests](../../../tests/DurableGraph.Tests/ArrayTemplateHistoryTests.cs)、[ArrayWireFormatTests](../../../tests/DurableGraph.Persistence.Tests/ArrayWireFormatTests.cs)；已通过 |
| G1 统一引用对象 | [ObjectBinding](../../../src/DurableGraph/Runtime/Binding/ObjectBinding.cs)、[ObjectLayout](../../../src/DurableGraph/Schema/ObjectLayout.cs)、[CaptureContext](../../../src/DurableGraph/Runtime/Capture/CaptureContext.cs)、[ObjectReadTable](../../../src/DurableGraph/Runtime/Capture/ObjectReadTable.cs) | [ArrayGraphTests](../../../tests/DurableGraph.Tests/ArrayGraphTests.cs) 与现有 string/class 回归；已通过 |
| G2 数组核心 | [FrozenArrayState](../../../src/DurableGraph/Runtime/Containers/FrozenArrayState.cs)、[ArrayObjectBinding](../../../src/DurableGraph/Runtime/Containers/ArrayObjectBinding.cs)、[ArrayStateReader](../../../src/DurableGraph/Runtime/Containers/ArrayStateReader.cs) | [ArrayBodyTests](../../../tests/DurableGraph.Tests/ArrayBodyTests.cs)；已通过 |
| G3 SG/持久图 | [SG generic projection](../../../src/DurableGraph.Generator/DurableGraphGenerator.GenericProjection.cs)、[StateModelSnapshot.Arrays](../../../src/DurableGraph.Persistence/ModelExecutionEnvironment.Arrays.cs)、[RevisionDecoder](../../../src/DurableGraph.Persistence/RevisionDecoder.cs)、[WorldWorkspace](../../../src/DurableGraph.Persistence/WorldWorkspace.cs) | [ArrayBindingCatalogTests](../../../tests/DurableGraph.Persistence.Tests/ArrayBindingCatalogTests.cs) 及 G5 包消费者；已通过 |
| G4 元素 Upgrade | [StateBindingContext.ArrayUpgrade](../../../src/DurableGraph/Runtime/Binding/StateBindingContext.ArrayUpgrade.cs)、[UpgradeContext](../../../src/DurableGraph/UpgradeContext.cs)、[StateModelRegistry](../../../src/DurableGraph.Persistence/StateModelRegistry.cs) | [ArrayUpgradeTests](../../../tests/DurableGraph.Tests/ArrayUpgradeTests.cs)；已通过 |
| G5 产品闭环 | [ArrayConsumer](../../../experiments/PackageConsumerProbe/ArrayConsumer/ArrayConsumer.csproj) 的历史模型及独立进程程序 | 真实 PackageReference 两代执行、根 build/完整 tests、文档链接及独立 review；已通过 |

本次格式演进的新写版本为 SchemaBatch/history v4、Base envelope v3；兼读旧版本保持原语法，
旧 Schema/history 内容不重写。包回归脚本针对新发布文件检查 v4，并继续验证既有 history 的文件存在性与 SHA256。

### 10.2 验收与审查结果

2026-09-08：根 `dotnet build DurableGraph.slnx --no-restore` 通过，零警告、零错误；
`dotnet test DurableGraph.slnx --no-restore --no-build` 全部通过：Runtime/Generator 636、StateStore 353、
Serialization 103、Storage 155，共 1247 项，零跳过。

- [Run-ArrayProbe（Git 历史）](../0091-retired-material-cleanup/README.md#git-history "Git 6dbaf0e:experiments/PackageConsumerProbe/Run-ArrayProbe.ps1") 从当前源码打包，两代真实 NuGet
  消费者分别 Publish/Verify，history 数量 4→5，旧文件哈希不变。覆盖四 rank、泛型/交错/共享/循环、
  一次数组 owner Upgrade、强制 Base 后恢复 Delta、旧 Revision exact 读取和冷重开。
- 原 [Generic（Git 历史）](../0091-retired-material-cleanup/README.md#git-history "Git 6dbaf0e:experiments/PackageConsumerProbe/Run-GenericProbe.ps1") 与
  [ValueUpgrade（Git 历史）](../0091-retired-material-cleanup/README.md#git-history "Git 6dbaf0e:experiments/PackageConsumerProbe/Run-ValueUpgradeProbe.ps1") 包回归通过，保留删除旧 inline
  领域类型后的历史能力与相邻 owner Context。
- [基础包消费回归](../../../experiments/PackageConsumerProbe/Run-Probe.ps1) 从当前源码重新打包并通过，
  包含普通非泛型生成、运行时 API 和 history 发布/校验的既有交付边界。
- 生成图回归验证同一现有数组由原策略先选择稀疏 Delta、再选择密集 Base；Base 截断内容链，
  数组类型头计入完整 payload/H；rank-4 引用元素 Delta 冷读保持身份。
- 独立审查发现并修复空数组没有元素回调时的声明校验缺口：reader 绑定前递归验证名义类型 kind/arity，
  包括嵌套类型，仍不要求 current CLR 或目标 body；传统显式 model/reader 可提供声明元数据。
  后续复核无剩余阻塞。另补齐迟登记嵌套 Schema 冲突、未使用的坏子工具在首回调前拒绝的回归。

实现保持 §9 的独立后继边界：未加入 BCL 容器、数组协变、统一表示头或通用加载内存预算。
收尾检查：10 份变动 Markdown 的 369 个本地链接/锚点全部有效，`git diff --check` 通过。
