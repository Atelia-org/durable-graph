# DB-038：泛型 Schema、状态表示与运行时绑定

> 文档整理：2026-10-14；来源为 Git `8ef9fca` 中的 `docs/design-branches/0038-generic-schema-state-and-binding-design.md`，正文保留当片合同与历史验收含义。
> 阅读范围：G0–G4 泛型/Family 与最小 UpgradeContext 证据见 §12，值工具由 DB-039 承接；原 SchemaStore/Base 类型头只保历史含义，存储后继见 DB-087。
> 当前能力与施工焦点见 [PROJECT-STATE](../../../src/PROJECT-STATE.md)；旧稿中的“下一步”不自动成为当前工作单。

> 状态：Chosen / Implemented — 2026-09-08。本片 G0–G4 已实施并验收，DB-039 另行调度。
> 主干完成交叉审阅；用户已采纳 §3.3 的仓库内严格一致范围及 §6.5 的统一 UpgradeContext 入口。施工跟踪见 §12。
> 设计起点：`5c6545d`；产品施工基线：`2aedb07`。当前能力见 [PROJECT-STATE](../../../src/PROJECT-STATE.md)。
> 本文承接 [DB-018 泛型备忘](../0018-generated-graph-codec-shape/0018-generic-dto-binding-followup.md)；研究结论见 §11，产品验收见 §12。
> 阅读顺序：先看 §2 推荐、§3.2–3.3 版本代价与保证范围、§6 Upgrade；代码接缝与施工顺序在 §8、§10。
> 实施顺序为本片 → [DB-039](../0039-composable-value-upgrade-design/README.md)。本片完成泛型闭环与最小 UpgradeContext；后片通过 Context 提供可组合值转换，两轮边界见 §10。

## 1. 目标、来源与支持范围

使用户继续声明普通 `[DurableType] partial class Box<T>` 或 `partial struct Pair<T>`，
由 SG 生成有限数量的开放代码模板，由 Runtime 按实际类型和 exact 历史布局闭合。
领域代码不声明 DTO/helper 参数，不手写逐字段序列化，不预先枚举所有可能的闭合组合。

| 必须保留的行为 | 来源与现有消费者 |
|---|---|
| Domain → 冻结 readonly/unmanaged DTO；引用保存 UInt32 ID；候选不随之后的领域修改变化 | 用户已选 DTO 路线；DB-037 Capture/GraphSession 回归 |
| class 独立对象身份；struct inline 嵌套；string 共享/等值不同实例保真，Empty 规范化 | 用户决定；当前 CaptureContext、ObjectReadTable |
| exact Schema 同 key 不可变；base/inline 依赖变化要求 owner 显式升版；nominal 不传播目标版本 | 用户决定；DurableSchema、SchemaStore、history 工具 |
| 同版融合 Delta、预备 Base，复用 bytes；引用校验、全 source 升级与验证，成功提交安装原候选 | DB-027/029/032–037 产品证据 |
| Upgrade 是显式单对象转换，不查询其他对象、不分配新 ID、不自动先升级 struct | 用户裁剪；当前 StateModel/NormalizedRevision |
| 历史 inline DTO/body 不依赖已删除的领域 struct；仍支持引用对象族的旧 Revision 必须保留执行能力 | DB-036/037 的不同历史能力边界 |
| 已知字段静态调用；未知泛型槽优先比较 static interface helper 组装 | 用户新素材及性能偏好；StateJournal 源码，不是 DurableGraph 泛型已完成证据 |
| 单 World、单 writer/会话，操作开始冻结代码目录，引用绑定不递归展开目标 body | 当前产品合同；不扩张并发、分支、故障模型 |

本方案推荐首片支持同一编译中的顶层非 record generic class/struct，含 readonly/private 字段，
在当前 13 标量、string、已登记领域 class、inline struct 组成的类型范围内闭合；class 仍以 DurableBase 为根。
同时验证泛型基类和派生类的字段投影，不能把继承当作“以后自然支持”。
非泛型类使用 `Box<Point>` 引用或 `Pair<Point>` 值，也属于实际消费者。

不同时引入数组对象、BCL 容器、nullable/enum 等新类型、boxed identity、interface/object 通配字段、
CLR nested type、跨程序集模型、NativeAOT 保证、独立值升级调度或无 CLR 宿主的退休对象族框架。
这些限制不禁止 CLR 泛型约束；支持的约束须正确复制及由闭合验证，不支持的形状必须明确诊断。

## 2. 方案比较与推荐

| 候选 | 收益 | 代价/反例 | 本轮判断 |
|---|---|---|---|
| `Serializers<T>` 中保存 typed 实例 codec | 组装直接、参数较少、可携带运行时 Schema 信息；DTO 仍可 unmanaged | 未解决 DTO 字段类型；仍需 TState；泛型成员多一层实例调用，但不必逐字段装箱/分配 | 保留为局部保底及对照，不作为默认执行形状 |
| 开放 SG 模板 + TState + 静态操作类型参数 | 保留 typed DTO；可复用现有 body；不需要 helper 实例 | 泛型参数/闭合代码量增加，必须管好历史与目录身份 | **推荐主干** |
| 只生成编译期已知闭合特化 | 用户代码和具体 DTO 直观 | `Box<T>` 工厂以后取得新 T 时没有代码；不能兑现按需组合 | 可作优化，不作完整支持方案 |
| object/冻结字节作为每个泛型状态槽 | 不需要静态关联的 TState 类型 | object 破坏 unmanaged DTO；字节削弱 typed Upgrade/refs 遍历并增加解码/分配 | 当前不采纳 |
| 全面改回 DynamicMethod/Reflection.Emit | 运行时能看到完整 CLR 实参，访问字段方便 | 发方法不能自动产生所需 DTO 历史/业务转换；改变现有 SG 交付路线 | 无当前必要性 |
| 每个闭合实例 ClosedCase + 独立版本账本 | 独立控制 `Box<Point>` 与 `Box<int>` 的升版，历史状态容易命名 | 必须登记闭合集及另一版本轴；削弱任意支持组合的自动闭合 | 当前延后，§3 明确触发条件 |

推荐的最小结构是：**一套类型表达，一套完整 exact Schema，纯状态 DTO，当前领域投影，历史状态操作，冻结目录内的绑定工厂。**
DTO/操作的 CLR 类型只是派生执行材料，不分别建立持久身份体系。

## 3. 类型身份、版本与闭合布局

### 3.1 nominal 类型表达

引入不可变的类型表达（本文称 TypeExpr，名称未冻结）：

```text
闭合类型身份：Builtin(已有标量/string) | Named(DefinitionId, 有序 TypeExpr 实参)
定义中类型模式：上述表达 + Parameter(声明内从 0 开始的 ordinal)
```

类型参数名字、CLR metadata token、程序集版本不作持久身份。类型模式中的 Parameter 是绑定变量，
不能出现在对象 Base 头或闭合 Schema key 中。数组/BCL 构造子在对应分片加入，不预留一个万能 CLR 名称回退。
TypeExpr 表达类型身份；exact inline/base 的版本另由布局依赖表示，不塞进 nominal family。

例如 `Box<Point>` 与 `Box<int>` 是不同闭合族；`Point` v1→v2 不改变 `Box<Point>` 的 nominal 身份。
`Box<Node>` 与 `Box<BaseNode>` 不因 Node 继承 BaseNode 就相同或自动相容；引用校验沿目标 stored/current
Schema 的 exact base 链，比较完整构造族身份。泛型约束不是协变序列化规则。

### 3.2 保留一条显式版本轴

按用户在 §3.3 采纳的 A，推荐 MVP 使用 `SchemaKey = (闭合 TypeExpr, DefinitionVersion)`。
非泛型相当于 Named(SchemaId, [])，可保留旧便捷构造入口；禁止将构造类型偷偷拼成未定义转义规则的 SchemaId 字符串。
DefinitionId、SchemaKind 和泛型 arity 构成声明身份约束；同 ID 不跨 kind，不改变 arity。参数改名不算布局变化。

完整闭合 Schema 保留该 key、declared fields、exact base/inline 依赖和 nominal 引用约束。
DefinitionVersion 不足以单独证明 exactness，仍须结构一致性校验。

关键例子：

```text
Box<T> v1 { T Value; }
已存：Box<Point> v1，Value 的 exact inline Schema 为 Point v1
新代码：Point v2，但 Box 仍 v1
结果：同闭合 SchemaKey 的完整布局不同，必须拒绝注册/保存。
```

作者须把 Box 定义升为 v2，并提供必要的对象 Upgrade；若 Pair<T> 是中间 inline 值，其定义也须升版。
**代价是 Box<int> 等无实际字段布局变化的闭合实例也会进入 v2**，可能需要透传 Upgrade，并在加载后强制 Base。
这是当前同编译 MVP 的保守选择，不声称是开放类库独立演化的最佳规则。
SG 能检查固定依赖和显式已知闭合使用，不能穷尽所有运行时 T；目标 SchemaStore 中已有同 key 时，
完整定义冲突是必要最后闸门。空仓库没有该旧定义，不能宣称此闸门也能检测漏升版，见下节。
它不自动给用户加版本，也不能把新 TState CLR 类型当作偷偷换 key 的理由。

若真实消费者要求跨程序集 generic library 与应用值类型独立升版，或全定义 bump 带来不可接受的改动，
再比较 ClosedCase 显式版本账本与带布局身份的 exact-key 升级图。二者都会增加真实概念，本片不预建。

### 3.3 必须明确的保证作用域：两个空仓库

异构审阅提出以下**当前同编译环境就能发生**的反例，不能用“跨程序集以后再说”排除：

```text
共同的 open history：Box<T> v1，字段为 Parameter(0)
Build A：Point v1，空 Store A 首存 Box<Point> → key=(Box<Point>,1)，inline Point v1
Build B：Point v2，漏升 Box；Box 模板未变，空 Store B 首存 → 同 key，inline Point v2
重开 Store A 写新布局：拒绝。但 Store B 没有旧定义，无法靠 Store 内冲突检查发现。
```

当前 SchemaKey 本就是 SchemaStore 内的定位键，完整 exactness 另做结构核对；不同独立历史/空库的非泛型代码
也可误用相同标识。然而泛型的新增差异是：**即使正常保留同一份 open history，也不能像具体非泛型布局一样，
保证所有漏 bump 都在构建期被发现**。作者升版义务仍在，但不能把义务写成已实现的自动防误用能力。

| 选择 | 保证与代价 | 建议 |
|---|---|---|
| A：目标 Repository 的闭合定义严格一致 | open history 保证模板不可变；完整闭合 Schema 在目标库内裁决；两个空库可能同 key 异形，不能仅按 key 跨库互换 | **2026-09-08 用户已采纳**；保持 Runtime 按需组合且无闭合集账本 |
| B：共享构建历史也锁住每个闭合布局 | 增加持久 ClosedCase/closure catalog，新的组合先纳入账本，再能承诺 fresh Store 也使用同一完整定义 | 当用户要求上述构建期防误用保证时选择；闭合版本/声明 UX 需继续细化 |
| C：exact key 包含布局身份 | 不同布局使用不同 exact key；引入新的身份/升级路径解释，不能继续把一个整数版本当作所有 exact 差异 | 更大重构，当前不推荐 |

若选 A，缓存必须绑定目录 snapshot 并比较完整 Schema，不跨 Repository 仅按 key 共享。
读取完整 Schema 时若同 snapshot 已见同 key 不同定义，明确拒绝；不能复用先前 body 或静默覆盖。
未来导入/合并 Schema 也必须按完整定义校验，当前不提供跨库导入能力。

仅扫描“本次哪几个 inline 类型升版”不能给出 B：Box 可以暂时退出编译、Point 升版后再引入旧 Box。
要用参数宇宙锁解决，就必须持久保留替换环境，且新增类型也牵涉版本管理；当前无理由把它包装成免费检查。
2026-09-08 用户明确选择“保持仓库内严格一致：明确诊断边界，暂不增加闭合历史账本”。
本方案据此收敛；后续产品实施授权与验收另记于 §12。

## 4. 定义历史与 stored Schema 的职责

构建期 `.dgschema` 扩为版本化的**定义模板**：记录 DefinitionId/Version、kind、arity、声明层字段编号、
base/field 值表达式模式可以嵌入 Parameter；其中固定的定义版本被显式记录，
闭合后所有 inline/base 操作数都解析为完整 exact 依赖。Parameter 按声明内 ordinal 表示；不保存 DTO 源码。
旧非泛型 history 可以解释为 arity=0 的定义。已接受材料先独立检查闭包，不能让 current candidate 修补旧缺口。

运行期 SchemaStore 保存**完整闭合 Schema**，包含实际 nominal 实参及每个实际 inline/base 的 exact 布局。
历史 reader 同时使用已保留的定义模板和 stored Schema：按模板逐字段/依赖校验并取得状态表示，
不能用当前 `Point.Schema` 替换旧 Point v1，不能只凭最终 DTO CLR 类型或 body 字节反推实参。
未知定义版本、错 arity/kind、非法替换、同绑定参数的不一致使用、缺失 exact 依赖均在 body callback 前拒绝。

解析得到的参数/值表达式绑定表属于该 exact Schema 的派生结果，供工厂闭合 TState/TOps 使用；
当前推荐不单独持久化参数绑定向量：模板模式、闭合 nominal 实参和完整 exact 布局是结构匹配的输入。
只有以后引入不能从这些输入唯一恢复的非可逆类型构造时，才重访额外 operand。
不双写一份参数绑定表和一份可自由修改的完整字段表。
参数仅用于 phantom identity 或引用目标类型实参时，不因此增加无消费者的 inline 布局依赖。

三个必要对照：`Pair<T>` 的两处 T 不能在同一绑定作用域分别匹配 Point v1/v2；
`Phantom<T>` 可以没有任何状态参数但保留完整闭合族实参；uint 数值/string ID/durable ID 的 DTO CLR 表示相同，
类型表达及字段约束仍须不同，不因字节能 roundtrip 就视为语义正确。

历史状态重建不要求把 nominal TypeExpr 转回旧 CLR 领域类型。
但是当前可编辑对象模型仍须提供其 current CLR 类型/Normalize 能力；
删除 Point 后还能读 `Box<Point>` 的历史 DTO，不自动证明还能建立当前 `Box<Point>` 领域对象模型。
例如非泛型 World 删除 `Pair<Point>` inline 字段，仍可通过保留的纯状态类型完成旧 World Upgrade；
若 source 中有独立 `Box<Point>` 对象行，其当前恢复能力仍适用 DB-036 的保留规则。
具体而言：删除 Point、使 current `Box<Point>` 模型不能闭合时，exact DTO 解码可成功，editable Load 仍须失败，
即使 World Upgrade 随后会删掉该引用。全部 source 行仍须 Normalize/引用验证；不可达只免除领域分配/填充。
要继续 Load，须保留能闭合该对象族的 current/migration CLR 类型与 Normalize 能力。

## 5. 生成代码：领域参数、状态参数、操作参数

### 5.1 参数化范围

只为真正依赖自由类型参数的值表达式引入状态/操作参数。同一表达式在同一绑定作用域重复出现时共享解析结果；
表达式之间的关系由同一解析器确定，不能允许调用方自由组合不匹配的 TState。

| 领域字段 | 状态字段/操作 |
|---|---|
| `int Count` | int，直接静态原语 |
| `T Value` | 待绑定的 TState 与投影/状态操作 |
| `Box<T> Child`（class） | uint，nominal TypeExpr 随 T 替换；不展开 Box body |
| `Pair<T> Value`（struct） | exact Pair DTO；可递归构成，也可作为一个已解析状态槽参数传给外层模板 |
| `Point Value`（已知 struct） | 直接引用已生成的 exact DTO/静态值 body |

DTO 参数只有状态表示，不含 helper 实例、领域引用或 Repository。
执行模板才携带 TProjection/TStateOps；不为每个已知字段机械追加三组泛型参数。
复杂表达式可采用多个表示参数，不假定“一个领域 T 必然只对应一个外层状态参数”。

Parameter ordinal 属于声明作用域，不能跨继承层按数字合并。对 `Derived<T,U> : Base<U>`，
必须先把 Base 的 Parameter(0) 替换为派生视角的 U，再归一化值表达式/去重；
保留 base-first 声明 Schema 段和 FieldId。`T=string,U=Node` 时两槽虽都为 uint，引用约束仍须分别正确。

### 5.2 静态操作合同

以下为职责形状，完整签名应在第一实施闸门与现有 reader/writer/delegate 接缝对齐：

```csharp
interface IValueProjection<TDomain, TState> where TState : unmanaged {
    static abstract TState Capture(in TDomain value, CaptureContext context);
    static abstract void Hydrate(ref TDomain target, in TState state, ObjectReadTable objects);
}

// 历史操作只有 TState，不绑定 TDomain。
// IStateOps<TState>: WriteBase / ReadBase / PrepareDelta / ApplyDelta / VisitReferences
// BoxCapture<TDomain, TState, TProjection>
// BoxBodyVn<TState, TStateOps>
```

生成代码通过受约束参数调用 `TProjection.Capture(...)`，不保存一个该接口的实例。
引用槽写读和数值 uint 可以共享字节原语，但状态操作必须携带 string/durable/primitive 的不同语义与约束。
运行时槽描述可以作为只读方法参数/绑定数据传入，不必把每个 Schema 元数据值都再编码成一种 helper CLR 类型。
融合 Delta 沿用 DB-037：子 HasChanges/bytes 决定父位及输出，Apply 拒绝冗余叶子、空子变化、padding 等。

当前投影按 CLR 类型绑定；历史 body 按 exact Schema 绑定。两者共享静态生成算法，不能共享“latest codec”权威。
helper 参数选用无实例状态的 struct，是给 JIT 的优化机会，不是所有闭合场景必然内联/零间接调用的承诺。

### 5.3 历史宿主与用户可写类型名

历史 `Vn<TState...>` 和 body 必须位于编译级、仅状态的生成宿主；
不能放进 `Box<TDomain>.__DurableState`，因为 CLR nested type 会隐式携带外层 TDomain。
当前领域 partial 类型内只留访问字段及 Capture/Hydrate 等当前桥接。

推荐非泛型的生成 family 宿主包含各版嵌套 DTO，例如：

```csharp
using BoxStates = Atelia.DurableGraph.Generated.Family_Box;   // 本文用短名示意编码后的稳定 family 名
using PointStates = Atelia.DurableGraph.Generated.Family_Point;

// BoxStates.V1<TValueState>
// PointStates.V1、PointStates.V2
```

实际 family 名可沿用 DefinitionId 的确定性 UTF8 编码规则，与领域 CLR 名称无关。
用户用普通 C# using 给 family 宿主起短名，随后可以写嵌套的 generic DTO；无需 C# 不支持的开放泛型 type alias，
也不为每个闭合组合增加持久 ClosedCase 声明。参数构造可继续使用 target-typed new。
从按 exact key 的旧 inline helper 迁入 family 宿主是 SG 内部重排；既有产品回归须验证。

### 5.4 private/readonly 与泛型约束

领域访问器优先放在只含领域泛型参数、保持原 ordinal 与约束的 helper 中；TState/TOps 留在执行模板。
此选择减少 UnsafeAccessor 参数匹配的复杂度。不能把“额外泛型参数都非法”当作 CLR 规则：
本轮见证中尾随未使用参数成功，而调换领域参数 ordinal 后访问 readonly 字段失败。
必须实际执行访问器，单靠 C# 编译通过不足以证明绑定正确。

class 恢复继续 Allocate 后 Hydrate；struct 从 default 临时值开始，经 ref 填充后写回。
闭合 CLR 类型须满足声明约束；历史状态 body 不套用 current 领域类型的约束或构造器。
`new()` 约束不授权加载时执行用户构造器，Transient 仍由用户交付后处理。
interface 仅作为 CLR where 约束，不等于支持 interface 持久字段；序列化能力取决于闭合后的实际类型。
涉及 ref struct 等排除类型的闭合仍拒绝，不能因 CLR 泛型工厂可以接受就视为 durable。

## 6. Upgrade：通用方法与显式闭合边

### 6.1 可表达的通用转换

允许用户为某一相邻定义版本边提供纯状态的泛型方法，典型例子是添加计数但保留 T 槽：

```csharp
static void Upgrade<TState>(
    in BoxStates.V1<TState> old,
    out BoxStates.V2<TState> next,
    UpgradeContext context) where TState : unmanaged {
    next = new(old.Value, 0); // 字段名为示意；不依赖 T 的业务结构。
}
```

只有方法签名的状态参数能与本次旧/新 DTO 唯一匹配、约束满足时才可闭合。
同一个类型形参在多个位置出现时必须绑定为同一类型；不能把旧、新 TState 不同偷偷 cast 成相同。
具体生成登记 API 需支持当前被拒绝的 method arity，保持强类型调用，不用逐字段 reflection/DynamicInvoke。
本片不建设任意 overload 搜索/推断引擎：每个定义版本边最多一个通用 provider，明确登记并严格匹配。

### 6.2 表示变化的闭合 owner 转换

`Box<Point>` 的 Point v1→v2 使旧、新状态类型不同；TOps 不能替用户发明业务转换。
最初方案使用显式闭合 owner 相邻边，参数写为具体历史状态；该能力继续保留给业务特例：

```csharp
static void UpgradePointBox(
    in BoxStates.V1<PointStates.V1> old,
    out BoxStates.V2<PointStates.V2> next,
    UpgradeContext context) {
    next = new(new(old.Value.X, old.Value.Y, 0), 0);
}
```

示例只说明类型位置，实际字段取决于 Schema；不要求 Point CLR v1 仍存在。
该 provider 的登记声明 owner 的闭合 TypeExpr、from/to 定义版本及唯一执行入口；
生成器/工厂根据明确的状态类型及对应 Schema 组成 expected 完整旧/新布局。
这里的推导同时使用 owner nominal TypeExpr、历史定义模板和签名中的显式表示选择；
DTO CLR 类型本身不包含全部引用语义，不能独立反推出 expected Schema。
登记项逻辑上包含闭合 owner、from/to version、两端 expected 完整 Schema、两端 StateType 与执行委托；
expected Schema 是从模板/owner/显式表示派生的能力校验数据，不是第二份持久权威。
它与实际 stored/current 完整 Schema 必须匹配，不能仅验证 Version 或 DTO CLR 类型。
这也是旧参数 exact 布局的显式选择，不根据 current Point 猜 old Point。

同一闭合族、同一相邻版本边最多一个专用 provider；重复登记同能力可幂等，冲突拒绝。
选择顺序为专用边优先，否则尝试通用边；已选专用边 Schema 不匹配或抛错时直接失败，不 fallback。
通用边因状态类型不匹配而不适用、且无专用边时，只阻止 current Load；exact DTO 解码仍可用。
候选数据和选择在调用任何 Upgrade 前固定，保持失败不交付。

不增加自动 struct Normalize。对 inline `Pair<Point>`，转换仍由所属对象的 Upgrade 显式完成。
复用同一值规则的多个 owner 可在下一片 [DB-039](../0039-composable-value-upgrade-design/README.md)
通过 `UpgradeContext.GetValueUpgrade<TPrior,TNext>(key)` 取得已绑定强类型委托，再显式调用值转换。
owner 按定义边声明规则集及源/目标槽位置，开放值 provider 可显式组合子能力；无需每个闭合 owner 重写外壳。
该委托属于执行 binding，不进入 unmanaged DTO；绑定仍比较两端完整槽语义，不能仅凭 CLR 状态类型选规则。
static TTransition 技术可行，当前作为可选优化；值能力不选择 owner 的历史中间版本，不替代本节的闭合特例。
本片先独立实现通用透传和显式闭合 owner 转换，不依赖上述 GetValueUpgrade 或规则集机制。

### 6.3 注册及命名必须先可执行验证

建议复用现有显式模型登记风格，由 SG 发出定义模板、历史 body provider、通用/闭合 Upgrade 的登记入口。
方法可放领域定义或独立应用静态类型中；闭合边的 DTO 参数不得被放回带旧领域类型参数的宿主。
登记语法的属性/函数名字未冻结，第一闸门必须给出可编译用户源码和 actual stored Schema 匹配反例。
已经证明手写形状能编译，不等于已经实现 SG 从方法签名生成这份登记信息。
DB-039 后续在同一冻结代码目录加入值依赖声明及 Context 工具；本片不先实现空规则集/工具查询平台。
两片都不要求首轮 Roslyn 已解析尚未生成的 DTO 类型：依据显式元数据和 history 发出 expected typed adapter，
由最终编译器校验用户方法；真实 SG 及用户源码必须在各片首闸门验证。

### 6.4 多跳升级的中间 exact 布局

绑定完整相邻链发生在调用用户 Upgrade 之前。起点为 source 的完整 stored Schema，终点为 current 完整 Schema。
每一步的类型和 Schema 必须有明确来源：

- 显式闭合 provider 指定其完整输入/输出表示，必须与本步输入匹配。
- 若 SchemaStore 已登记该闭合族/中间版本，使用其 authoritative exact 定义来核对或绑定该步。
- 否则通用 provider 的下一表示须能由已绑定输入、nominal 实参的固定内建语义、历史模板中的固定 exact 依赖
  及明确签名约束唯一确定；推导结果仍与所有已知同 key 定义交叉检查。
- 最后一边有明确的 current 终点，可据此匹配其输出；这不授权把 current 值布局倒灌到未确定的中间历史版本。

例如 `Phantom<T>` v1 无 T 槽，v2 新增 T，v3 又改变布局；读取 `Phantom<Point>` v1 时，
source 无 Point 值布局，v2 模板只有 Parameter，current v3 的 Point v2 不能证明中间 v2 应用 Point v1 还是 v2。
若没有已登记的中间 exact Schema 或其他唯一输入依据，就需要显式闭合的 v1→v2 边选择表示；缺失则拒绝 current Load。
不增加图搜索、current/latest 回退或运行用户代码后再试另一条路径。

即使通用边能构造 `Box<Point>` v2(Point v1) DTO，若仓库已登记同 key 的 v2(Point v2)，也必须拒绝。
“只是临时中间 DTO”不是绕开 Schema 不变性的理由；框架不持久注册这些临时推导定义，也不放宽最终 exact 匹配。
整条绑定路径完整确认后，再逐边执行、检查结果类型并按 current 全目录验证引用；读取不写回。

typed provider 可执行不等于业务语义自动正确。用户显式编写数值/引用转换仍由其代码负责，
框架保留 owner/边/完整 Schema 适用性检查和输出引用验证，不能仅凭两端 TState=uint 宣称语义保持。

### 6.5 统一 UpgradeContext 与本片最小内容

2026-09-08 用户采纳：Upgrade 优先考虑灵活性，统一接收非泛型 `UpgradeContext`，
具体工具由 Context 提供，避免未来给每个历史方法不断追加能力参数。
本片的新用户方法形状为 `static void Upgrade(in prior, out next, UpgradeContext context)`；
Context 是普通非泛型 sealed class，由执行器构造，调用方只能读取本次转换信息。

本片只提供有实际消费者的三个只读信息，名称作为施工起点：

| 信息 | 固定含义 |
|---|---|
| `ObjectId` | 当前被升级的对象 ID；用于诊断，不是分配或读取其他对象的入口 |
| `SourceObjectSchema` | 当前这条 owner 相邻升级边的完整输入 Schema |
| `TargetObjectSchema` | 当前这条 owner 相邻升级边的完整输出 Schema；可能是中间版本，不必是最新版本 |

例如对象 V1→V2→V3 分别收到 V1/V2 和 V2/V3 端点，不能把整链 source/current 填到每一步。
DB-039 的嵌套值 Context 保留同一 owner 信息，自己的值端点另存内部绑定元数据；
这些 object 属性始终指 owner，不将标量/string-ID 槽伪装成对象 Schema。
未来有具体消费者时可添加只读工具，当前不预建 IServiceProvider、服务字典或任意动态解析接口。

生命周期是当前对象、当前相邻边的同步调用。框架缓存不含 ObjectId/本次 Context 的不可变绑定计划，
执行时据此创建 Context，不能把对象 A 的诊断信息或工具绑定复用于对象 B，也不能把 V1→V2 Context 用于下一边。
Context/绑定到它的工具不由用户保存为长期状态；本期不加入 pooling、撤销 token、租约或异步调用协议。
框架不通过 Context 暴露 Repository、对象表、CaptureContext、新 ID 分配或提交能力。

保留当前非泛型二参 Upgrade 的局部迁移办法：SG 发出同一三参 adapter，调用旧方法时忽略 Context。
每个 owner/版本边仍只允许一个已选用户入口；同边同时提供二参/三参重载继续拒绝，不建设重载搜索或第二条 Normalize。
新通用泛型/显式闭合 provider 使用三参；既有二参仅允许零 Context 工具依赖，后续声明工具时须改为三参。
旧调用形状的适配只影响生成调用，不改变历史 DTO、Schema 或磁盘解释。
已有二参历史包也纳入回归，防止把“历史代码还在”误写成“必须旧参数形式才能解码”。

本片不提供 `GetValueUpgrade`、值规则集、依赖 key 或可组合值 provider。
DB-039 在保持本节入口的前提下增加这些能力，不要求再修改已完成的 owner 升级函数签名。

## 7. 目录快照、工厂与缓存

1. 应用显式登记允许的类型定义和 provider。内建叶子由框架预制，自定义模板由 SG 发出；无程序集全扫描。
2. 操作开始冻结模板及升级目录；保留当前 Snapshot 语义。按需闭合是固定目录中的 memoization，
   不能在 Capture 中向用户可变 StateModelRegistry 偷偷 Register。
3. current 路径解析 CLR 类型和当前布局；historical 路径从 TypeExpr/完整 stored Schema 解析状态模板。
4. 解析并核对全部实际需要的 inline/base 布局依赖及投影、状态操作，构造 typed helper/factory，完整成功后才加入缓存；失败不发布半成品。
5. 缓存 key 使用闭合族/exact key，命中仍与完整定义一致；不同目录 snapshot 的执行 provider 不自动共用。

纯代码/无目录依赖的 helper 信息可按 closed CLR Type 静态缓存；依赖应用 provider 的整个 binding 不放
全局 `Serializers<T>`。缺可登记依赖不放进不可重试的类型静态构造器。冻结目录内失败可直接报告；
后续新操作/新目录补齐能力后可重新解析，不要求当前失败 snapshot 自己变成另一份目录。
同 snapshot 顺序重复解析取得稳定 binding，保留当前引用实例幂等语义；不新增多线程 strong-once 合同。

首选 SG 发出统一泛型工厂方法，再 `MakeGenericMethod/CreateDelegate` 闭合。
需要统一异形构造器外观时可用 Expression；二者都只在冷路径使用，不重新生成成员遍历 IL。
对象边界可以保留既有 delegate/erased binding；泛型成员内部使用 TOps 静态调用。

nominal 引用槽仅构造类型约束和 ID 操作，实际对象出队/读取其 Base 后再选模型：

```csharp
class Node<T> { Node<Node<T>>? Next; }
```

即使 Next 为 null，闭合 Node<T> 也不能预先展开 Node<Node<T>> 的 body 并无限继续。
inline/base 才进入 exact 布局递归，检查环/最大路径；TypeExpr 本身也应有长度、深度及 arity 防护。

## 8. 产品代码接缝与持久格式

| 现有落点 | 需要调整的职责 |
|---|---|
| [DurableSchema / DurableFieldInfo](../../../src/DurableGraph/Schema/DurableSchema.cs) | 闭合族 TypeExpr 与完整 exact 布局；原 nominal 字符串扩大为类型表达；区分定义模式/闭合实例 |
| [SchemaHistoryTool](../../../src/DurableGraph.Build/SchemaHistoryTool.cs)、[SG history](../../../src/DurableGraph.Generator/DurableSchemaGenerator.Ancestry.cs) | arity/参数模式、历史闭包、模板版本和当前固定依赖；源代码历史不要求枚举全部 closures |
| [SG body](../../../src/DurableGraph.Generator/DurableSchemaGenerator.GeneratedState.cs)、[inline helper](../../../src/DurableGraph.Generator/DurableSchemaGenerator.InlineState.cs) | 共享 family DTO/开放 body、自由值表达式参数、静态 helper、当前领域桥接 |
| [StateModelRegistry](../../../src/DurableGraph.Persistence/StateModelRegistry.cs)、[StateReaderBinding](../../../src/DurableGraph/Runtime/Binding/StateReaderBinding.cs) | 从逐个完整 model/reader 目录扩为模板目录 + snapshot 内按需闭合，保留非泛型入口 |
| [SchemaKey](../../../src/DurableGraph.Persistence/SchemaKey.cs)、[SchemaStore](../../../src/DurableGraph.Persistence/SchemaStore.cs) | key 的构造类型表达、闭合布局同 key 一致性；不能仍只按 DefinitionId 索引模型 |
| [TypedObjectVersionReader](../../../src/DurableGraph.Persistence/TypedObjectVersionReader.cs)、[RevisionDecoder](../../../src/DurableGraph.Persistence/RevisionDecoder.cs) | 在读取完整 stored Schema 后解析 exact reader，再执行 body；不由 current CLR 类型选择历史表示 |
| [StateModel 生成](../../../src/DurableGraph.Generator/DurableSchemaGenerator.StateModel.cs)、Normalize | 单对象相邻边选择、参数化/闭合 provider、旧新 Schema 匹配、统一 Context adapter；仍 live 升级对象强制 Base |

格式变动应显式版本化：history/manifest 与 SchemaBatch 需新的 arity/类型模式/闭合 key 表达；
Base 类型头需携带新的闭合 SchemaKey。读取保留当前已接受的旧格式，映射为零实参定义；不回写历史材料。
字符串对象继续走内建类型路径；Delta 沿 Base 的 exact Schema，不另写类型参数或版本头。
TypeExpr 写入必须 canonical、严格全消费，拒绝重复的参数声明、非法 ordinal、错 arity、开放参数残留、未知标记及越界；
允许 Parameter 重复使用，并核对所有 occurrence 的绑定一致。
numeric tag、字段排列、旧新格式转换用独立 golden 冻结，不能在尚未验证前宣布现有 v2/v3 wire 已支持泛型。

Storage ObjectVersion/Revision 逻辑、publication log、整数 Base/Delta 策略不改。
新的 Base envelope 长度进入实际 B/H 口径；Schema 共享元数据仍不摊进对象成本。
跨 Schema 的 Delta 禁止；注册 Schema 成功不等于提交发布成功。

## 9. 失败语义与开放边界

不支持的 CLR 实参、违反 generic constraints、未知模板或 history、Schema 不一致、缺升级边、坏引用，
分别在能识别的最早边界明确失败。不能自动降级为对象字典、boxed value、最新 reader 或 Base 来掩盖不相容。
Capture/准备失败保留原基线；SchemaStore 仍整批预检；Decode/Normalize/Hydrate 失败不返回部分 World。
这些沿用原职责，工厂只拥有代码绑定，不能分配对象 ID、读取其他对象来修补升级或发布 head。

以下属于后续裁决，不是假定已经解决：跨程序集闭合版本独立演化、任意删除泛型实参后无 CLR 的 current Normalize、
自动值迁移调度/路径搜索、泛型虚/接口多态、数组/BCL、AOT/裁剪、并发初始化、性能和代码尺寸优化。
owner 显式调用的有限值依赖组合已转入 DB-039 推荐范围，仍尚未产品实施。
本轮应将不同技术取舍及具体失败轨迹保留，不以一句“SG 可生成泛型”覆盖这些边界。

## 10. 建议施工闸门

### 本片独立交付范围

DB-038 负责 TypeExpr/模板 history/完整闭合 Schema、开放 class/struct 的 DTO/body 与当前投影、
exact/current 目录及保存恢复闭环、通用透传和闭合 owner Upgrade、§6.5 最小 Context。
它须独立完成下表，不以“等待 DB-039”代替泛型历史恢复验收。
未使用值组合的业务转换可以直接构造历史 DTO 或调用普通纯状态 helper。

| 闸门 | 最小验收 |
|---|---|
| G0 形状与用户源码 | static TD/TS/TOps 冷闭合；uint 三种语义；readonly generic accessor 真执行；family alias 的三参通用/闭合 Upgrade 与旧二参 adapter 可编译；最小 Context 接缝；三版本 phantom→value 的歧义与显式转换；无旧领域类型的 historical body；缺转换拒绝 |
| G1 类型与历史 | TypeExpr/key、定义模板/闭合布局互校验，Box<Point>漏 bump 冲突，Box<int>保守升版，nominal child 不传播；重复 T 不一致拒绝、phantom 零状态参数、canonical 新格式与旧格式读取 |
| G2 真 SG 闭合 | class/struct、自由值表达式、交换参数位置的泛型继承、nongeneric owner、静态 body、完整 refs、generic constraints；将 G0 等价物替换为真实 SG 输出 |
| G3 保存/恢复 | actual Capture → Prepare → Commit → reopen；generic string 同实例共享/非空等值异实例/Empty 规范化；相同 DTO CLR 类型的不同约束不串绑；闭合目录快照与扩张引用不无限初始化；连续两个对象及多跳收到各自正确 Context 信息 |
| G4 历史包 | 三次真实 PackageReference 构建，通用透传/闭合业务转换，old→middle→current 的布局唯一性/同 key 一致性，旧 inline 领域声明删除后的 owner 升级、缺历史/缺边/错 expected Schema 拒绝、强制 Base 后稳定 NoChange/Delta |

先通过 G0 用户写法与 G1 版本反例，再冻结对外 Upgrade 登记和 wire；不从序列化热循环一路写到最后才发现历史 API 无法使用。
依赖 key/规则集、Context 工具获取、开放值组合及相应历史包验收属于 [DB-039 §6](../0039-composable-value-upgrade-design/README.md#6-产品接缝与研究验收)，
**不是本片 G0/G2/G4 的验收条件**。本片结束后再据实际生成形状校准后片；后片不重开持久类型格式。
独立研究 probe 只证明部分 G0 接缝；后续真实产品闸门验收见 §12。

### 实施入口尚须验证的接缝

新格式的 numeric tag/排列、生成 family/登记名称及支持的 CLR 约束清单，在 G0/G1 用用户源码与 golden 冻结。
这是本轮施工的前置工作，不是“稍后自然成立”的假定；若必须扩大当前类型范围或改变版本语义，返回设计讨论。
真实 SG 的泛型继承、历史 binding 和 PackageReference 尚未实测，独立 Probe 不代替它们。
已接受的全定义 bump 代价、空库保证范围，以及缺中间 exact 布局时拒绝仍保留；不能以消除这些限制为完成本片的条件。

## 11. 比较、验证与审阅记录

2026-09-08 完成三位独立审阅者的初轮论证、交叉质询及对实质修订的定向复核。
其中历史语义审阅采用用户指定的 **gpt-5.6-sol / reasoning effort max**；
另两位沿用主会话模型，分别担任最小架构与需求/技术边界审阅，主代理检查源码并裁决证据。
设计写作与修改由主代理完成，机制 probe 由另一位 agent 编写，主代理检查并实际重跑。

| 被挑战的主张 | 决定性证据/反例 | 最终处理 |
|---|---|---|
| 每个闭合实例都必须有独立版本账本 | 最强初始反例依赖跨程序集独立演化；当前同编译可保守提升定义版本 | 撤回 MUST；保留代价与未来触发 |
| SchemaStore 能兜住所有漏 bump | 同一 open history、两个空 Store 可首存同 key 异形 | 明确诊断作用域；用户已采纳仓库内严格一致，不建闭合账本 |
| 必须持久保存 ParamBinding vector | 完整 TypeExpr＋模板＋closed exact Schema 可逐槽一致化；phantom 参数不需要状态绑定 | 不增加第二持久表；派生绑定只在内存存在 |
| TS/静态 helper 足以自动转换泛型值 | PointV1State 与 PointV2State 没有自动业务转换；负例 CS1503 | 通用 provider 与显式 closed owner edge 分工 |
| 两端 DTO 已知即可描述完整升级 | Phantom 参数在三版本中间首次成为值；存在多个合法闭合，负例 CS0411 | §6.4 先明确整条 exact 路径，缺唯一依据拒绝 |
| 历史 DTO 放在 Box<T> 内也可 | enclosing TDomain 会进入 nested CLR type | 编译级非泛型 family 宿主，使用普通 using alias |
| UnsafeAccessor 多一个泛型参数必然失败 | 尾随未使用参数实测成功；重排领域参数 ordinal 实测 MissingFieldException | 分离访问器以保留 ordinal/约束，避免错误概括 |
| 无 callvirt 证明最优性能 | probe 只检查 IL；实例 codec 也可保留 unmanaged DTO，未做 JIT/性能测量 | 静态 helper 为首选、实例 codec 为保底，不宣称性能已胜 |

三位审阅者对最终修订均未留下设计阻塞；这表示 Proposed 方案足以进入后续产品验证，
不表示已实现 generic SG 或所有参数/继承形状已可执行。用户采纳的 §3.3 选择已同步进入目标设计。

### 首次研究的可复跑证据

[GenericBindingShapeProbe](../../../experiments/GenericBindingShapeProbe/README.md) 为本轮新增的独立实验。
在 Windows / .NET SDK 10.0.201 / Roslyn 5.3.0 上，主代理运行：

```powershell
pwsh -NoProfile -File experiments/GenericBindingShapeProbe/Run-Probe.ps1
dotnet build DurableGraph.slnx --verbosity quiet
```

probe 输出 **20 项预期结果与 PASS，exit 0**，包括 uint 三种语义、递归静态投影、冷工厂、
实际 readonly generic 访问器、移除旧领域程序集依赖、family alias、显式三版本中间状态及四类编译负例。
根 solution build 零警告、零错误。没有修改产品源代码或持久格式，本轮不将此前 994 项产品测试记为新的泛型验收。
后续 DB-039 在同一 Probe 增加可组合值 Upgrade 见证；新增验收以其研究记录和 Probe README 为准。

probe 使用手写 generated-like 模板和少量 BinaryWriter/Reader 定长字段；
不是 DurableGraph wire、融合 Delta、SchemaStore、真实 SG/history 生成、完整升级边规划器或 NuGet 包见证。
它没有验证跨线程缓存、性能、AOT、所有 CLR constraints 或 generic inheritance；这些范围在 §10 分别设闸门。

原始素材：StateJournal 的静态 helper/外观工厂与旧 Robird DynamicMethod 的成员/ref 处理，
来源和作用边界已保存在 [DB-018 §6](../0018-generated-graph-codec-shape/0018-generic-dto-binding-followup.md#6-statejournal-的静态-helper-与工厂组装素材)。
当前产品接缝事实以 §8 所列源码及 DB-037 为准，素材不构成第二份产品规范。

### Context 与两轮施工的文档复核

2026-09-08 按用户新采纳的 Context 方向进行文档修订，并复用三位独立审阅者交叉检查两轮边界。
移除 §10 对 DB-039 的反向验收依赖；新增最小三参 Context、局部旧二参适配、逐对象/相邻边调用信息，
把 Context 工具/规则/组合留给后片。动态工具解析、生命周期平台和跨对象能力不进入本片。
在已接受的版本代价和支持范围内，未发现新的架构阻塞；剩余真实 SG/约束矩阵/格式 golden 必须在 G0/G1 先验证，
不能以设计审阅替代测试。此次仅修改文档，原 Probe/build 数字仍属于上面的历史验证，不是新 Context 验收。

## 12. 产品施工跟踪

2026-09-08 用户授权完整实施 DB-038。基线 `2aedb07` 工作树干净；根构建零警告/错误，994 项测试通过。
本节记录本片的验收映射；DB-039 的工具查询、规则集及组合 provider 不进入施工范围。

| 需求 | 实现归属 | 验收 | 状态 |
|---|---|---|---|
| 闭合身份、exact Schema、版本化类型头和旧格式读取 | Runtime TypeExpr；StateStore SchemaKey/SchemaStore/wire | G1 canonical golden、冲突与完整布局 | 已验证 |
| 开放定义历史与 accepted-history 独立校验 | SG/Build，共享内部 TypePattern parser | G1 模板/约束/缺历史反例 | 已验证 |
| 开放 DTO/静态 body/current 投影、readonly 和继承 | SG Family emitter；Runtime 静态值操作 | G0/G2 真实用户源码及执行 | 已验证 |
| 冻结目录内按需闭合、stored-first reader | Runtime binding；StateStore snapshot/decoder | G3 身份、目录隔离、扩张引用 | 已验证 |
| 三参通用/闭合 owner Upgrade 与最小 Context | SG provider；Runtime 整条相邻链计划 | G0/G4 完整端点、多对象/多跳、失败前置 | 已验证 |
| 包交付和完整保存恢复 | PackageConsumer 与产品集成测试 | G4 三代真实包与回归 | 已验证 |

本轮先冻结的接缝：

- `TypeExpr` 使用 Builtin/Named/Parameter；闭合 Schema 的 `Type` 与 `SchemaKey.Type` 不含 Parameter。
  `SchemaId` 保留为 DefinitionId 便捷访问，不能用它代替闭合族 key。
- TypeExpr 最大深度 64、展开节点 4096、单定义 arity 32；闭合 wire 的 tag 1 为内建、2 为 Named，拒绝开放参数。
  SchemaBatch 新写 v3，Base 类型头新写 v2；旧格式保持严格读取。
- SG 与 Build 共用内部文本 TypePattern parser，不引入新程序集；新 history v3 带 arity 和参数模式。
- 不含显式 DurableUpgrade 的纯非泛型编译保留既有生成入口；包含泛型定义/历史依赖或显式 Upgrade 登记的编译使用统一 Family 状态宿主。
  在后一路径中，用户源码若引用旧内部 `__DurableState.Vn`，改用 Family alias；不生成两份互不相同的状态表示。
  非泛型二参 Upgrade 的调用适配继续保留。
- 新通用/闭合升级方法以 `DurableUpgrade` 属性标明开放/闭合 owner 与相邻边起始版本，终点固定为 +1；
  方法位于顶层非泛型 static host，public/internal 可访问。未知/不支持的 owner 或 host 明确诊断，不能被忽略。
  Family.Definition 可单独登记，也可用 `Generated.DurableDefinitions.Register` 登记整份生成目录。
- CLR 约束复制保留 class/class?、struct、unmanaged、notnull、new()、基类/接口及参数间约束；
  nullable 注解仍遵循 C# 的静态语义。反射可以绕过 C# 的 unmanaged 检查，因此冷绑定另用
  RuntimeHelpers.IsReferenceOrContainsReferences 验证实际实参；含引用字段的 struct 不可冒充 unmanaged。
  此验证不进入字段循环，不约束历史纯状态 reader 的旧领域类型。

独立审阅发现并推动修复：nominal-only 声明的 kind/arity 义务；缓存 owner 根未覆盖后来登记的 exact 子依赖；
以及共享 Schema DAG 的重复展开。最终实现对完整 base/inline 闭包复核，缓存不保存 ObjectId/Context，
匹配按 exact Schema 与声明作用域去重，仍检查较长路径的深度上界。相关拒绝均有针对性回归。

具体源码与证据：

| 闸门 | 产品证据 |
|---|---|
| G0 / G2 | [真实生成模板/静态 body/readonly 继承/属性入口](../../../tests/DurableGraph.Tests/GenericGeneratedStateTests.cs)、[逐对象/相邻边 Context](../../../tests/DurableGraph.Tests/GeneratedUpgradeContextTests.cs)、[CLR 约束补充验证](../../../tests/DurableGraph.Tests/GenericUnmanagedConstraintTests.cs) |
| G1 | [结构化身份](../../../tests/DurableGraph.Tests/GenericSchemaIdentityTests.cs)、[canonical/旧 wire/仓库冲突](../../../tests/DurableGraph.Persistence.Tests/GenericSchemaPersistenceTests.cs)、[v3 history](../../../tests/DurableGraph.Tests/GenericTemplateHistoryTests.cs) |
| G3 | [snapshot/重复参数/缓存/DAG/引用边](../../../tests/DurableGraph.Persistence.Tests/GenericBindingCatalogTests.cs)、[整链/phantom/闭合特例/缺能力](../../../tests/DurableGraph.Tests/GenericUpgradeBindingTests.cs)；实际图保存冷读也由 G0/G2 生成测试覆盖 |
| G4 | [GenericConsumer](../../../experiments/PackageConsumerProbe/GenericConsumer/README.md)：三代真实包、缺闭合转换反例、删除旧 inline CLR、完整 Context、强制 Base 后稳定保存 |

DB-039 从现有 `UpgradeContext`、`StateUpgradeProvider`、`StateBindingContext` 的已绑定计划接入；
值工具查找和嵌套调用作用域尚未实现，不需要再修改 history/SchemaBatch/Base 类型表达格式。

### 完成验收

2026-09-08 主代理在合并工作树验证：

- `dotnet build DurableGraph.slnx --no-restore --verbosity quiet`：零警告、零错误。
- `dotnet test DurableGraph.slnx --no-build --no-restore`：**1077/1077**，零失败、零跳过
  （Runtime/SG 506、StateStore 313、Storage 155、Serialization 103）。
- 随后补强同一泛型图测试的 null/Empty 与 `Box<World>` 回环，重建并定向执行通过；没有修改产品代码。
- 主代理运行全部五个真实包脚本：Run-Probe、Run-StateStoreProbe、Run-InlineStructProbe、
  Run-HistoryCapabilityProbe、Run-GenericProbe，均 exit 0。后三者复用同一最终版本的八个依赖包。
  新泛型脚本跨三代 Schema、四个构建/独立进程，旧 history SHA256 不变，缺闭合转换保持仓库 bytes 不变。
- 独立审阅复核 Schema/history、绑定、整链 Upgrade、缓存 exact 子依赖及 unmanaged 防护，无剩余阻断发现。

回归中的旧格式读取仍保留；新写版本的 golden/空 manifest 预期更新为 v3，新 Base header 引起的实际尺寸变化
同步进入断言。已加载模型随后遭遇 Schema 冲突时，新 resolver 会在 Capture 阶段更早拒绝；没有追加 State 或推进基线。
缺失旧对象族执行能力时，新的目录入口报告缺 declaration factory；该反例仍验证旧 Revision 拒绝、新 Revision 可读。
