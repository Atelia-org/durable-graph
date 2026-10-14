# DB-039：owner 显式调用的可组合值 Upgrade

> 文档整理：2026-10-14；来源为 Git `8ef9fca` 中的 `docs/design-branches/0039-composable-value-upgrade-design.md`，正文保留当片合同与历史验收含义。
> 阅读范围：可组合值工具、整链预绑定和共享 DAG 的当片证据见 §8；原 StateModelSnapshot 接缝保历史含义，当前 reader/Schema 视图从 DB-087 进入。
> 当前能力与施工焦点见 [PROJECT-STATE](../../../src/PROJECT-STATE.md)；旧稿中的“下一步”不自动成为当前工作单。

> 状态：Chosen / Implemented — 2026-09-08。G0–G3 已完成，施工接缝与实际验收映射见 §8。
> 后继接缝：DB-038 的产品代码，实际验收见其 §12。
> 本文细化并修订 [DB-038 §6](../0038-generic-schema-state-and-binding-design/README.md#6-upgrade通用方法与显式闭合边)；泛型身份、历史及仓库一致性继续由 DB-038 说明。
> 排期：DB-038 独立完成泛型闭环和最小 Context 后，再实施本片。原逐参数注入委托改为从 Context 获取；旧 Probe 仍只证明原机制，未验证新外观。

## 1. 问题和不可改变的边界

DB-038 的闭合 owner Upgrade 可以正确表达 `Box<PointV1State> → Box<PointV2State>`，
但不同 owner 即使采用同一种 Point 业务转换，也要重复书写闭合 DTO 签名和外层组装函数。
普通 helper 已能复用 Point 的业务代码；本次要进一步消除这些重复的**闭合外层函数**。

| 约束 | 来源及本轮含义 |
|---|---|
| 业务转换由用户决定，owner 控制执行 | 用户当前要求；框架可以绑定工具，不能主动遍历 inline 值并执行 Upgrade |
| 单对象、无其他对象读取或新 ObjectId | 用户已裁剪的 MVP；Context 提供当前转换信息及事先绑定的工具，不提供跨对象操作 |
| UpgradeContext 为统一入口，灵活性优先 | 用户新采纳；值能力不再逐个占用用户函数参数，执行期仍只获取已声明的能力 |
| DTO readonly/unmanaged，引用为 ID | DB-037/038；delegate 存在于执行 binding 中，不是 DTO 字段 |
| exact 布局先确定，再绑定整条对象升级链 | DB-038 §6.4；值能力不填补 phantom 参数缺少的历史版本信息 |
| 目录在操作开始冻结，失败不交付部分 World | 当前模型目录及 DB-038 的扩展方向；不建立全局 latest 转换目录 |
| 仓库内同 key 完整 Schema 一致，无闭合历史账本 | 用户已选 DB-038 §3.3；本次不改变版本轴或保证范围 |

最小观察标准：同一 Point 规则被开放 Box/Pair 复用并可嵌套；同 DTO 类型的不同业务规则可以明确区分；
声明的依赖槽错绑、缺依赖和歧义在该对象整条链的业务 callback 之前失败。
本片实施生成声明、值依赖绑定和 Normalize 调用工具；不改变 wire 或 SchemaStore 的持久格式。

## 2. 推荐的执行形状

owner 和注册的值 provider 统一使用三参用户形状：`(in prior, out next, UpgradeContext context)`。
Context 沿用 [DB-038 §6.5](../0038-generic-schema-state-and-binding-design/README.md#65-统一-upgradecontext-与本片最小内容)
的非泛型入口，本片增加 `GetValueUpgrade<TPrior,TNext>(key)`，返回已绑定强类型委托：

```csharp
public delegate TNext ValueUpgrade<TPrior, TNext>(in TPrior prior)
    where TPrior : unmanaged
    where TNext : unmanaged;

static void UpgradeBox<A, B>(
    in BoxStates.V1<A> old,
    out BoxStates.V2<B> next,
    UpgradeContext context)
    where A : unmanaged where B : unmanaged {
    var value = context.GetValueUpgrade<A, B>(BoxUpgradeSlots.Value);
    next = new(value(in old.Value), 0);
}

static void UpgradePoint(
    in PointStates.V1 old, out PointStates.V2 next, UpgradeContext context) {
    next = new(old.X * 1000, old.Y * 1000, old.LabelId);
}

static void UpgradePair<A, B>(
    in PairStates.V1<A> old, out PairStates.V2<B> next, UpgradeContext context)
    where A : unmanaged where B : unmanaged {
    var element = context.GetValueUpgrade<A, B>(PairUpgradeSlots.Element);
    next = new(element(in old.Left), element(in old.Right));
}
```

DTO/family 名字、字段及工具 key 名为示意。`value` 已经绑定到本次确切的源/目标语义和业务规则；
owner 的普通 C# 调用决定何时执行。删字段的 Upgrade 可以完全不声明该值依赖；
已声明但在某条数据分支中未调用的依赖，仍须预先绑定成功，不按运行数据临时查找。

Pair 的转换也是用户显式提供的函数。框架可以按它声明的依赖组装 Point 转换，
但只有 owner 调用 Pair 委托，Pair 函数才调用 Point 委托。这没有增加 struct Normalize 阶段。
若两个 Point 字段分别需要缩放和保持数值，可以让 owner 从两个依赖 key 取得不同规则的委托；
重复类型参数仍须满足 DB-038 的同一状态表示约束，不允许借不同规则构造相互矛盾的布局。

| 形状 | 收益及代价 | 本轮建议 |
|---|---|---|
| 普通 helper＋闭合 owner | 最少框架概念；不能消除每种闭合外层签名 | 继续支持，适合少数特例 |
| 逐参数注入 `ValueUpgrade<A,B>` | typed 调用可组合，先前 Probe 已验证 | 保留为实现材料与历史对照；不作为扩展时不断加参的用户入口 |
| `UpgradeContext` 提供 typed 值委托 | 三参用户入口稳定；工具可逐步扩展，内部允许查表、类型检查和委托调用 | **用户已采纳**；本片只查已绑定能力，不运行时选择业务规则 |
| `IValueUpgrade<A,B>`＋`TTransition` static helper | 可用 helper 类型递归表达所选规则，有潜在内联机会 | 技术可行；更多类型参数和组装类型，保留优化候选 |
| 每种值工具另设 typed 实例接口 | 能携带依赖或状态 | 当前值操作用 delegate 已足够；Context 统一承载工具，不必逐个再设接口 |
| 自动按类型搜索迁移图 | 用户声明少 | 无法决定业务规则或缺失的历史布局，本次不引入 |

这不修订 Capture/Base/Delta 的静态 helper 主干。当前 Upgrade 由历史 Load 的 Normalize 按对象/版本调用，
并非每次保存的数据成员循环。没有性能测量证明此处增加 TTransition 参数值得。
static 方案也不必使用动态 emit 或可变全局状态：纯代码 leaf/组合 helper 可以经闭合类型表达选择，
只是当前 Context/委托更容易携带已选能力和调用信息。Upgrade 的查表/间接调用是明确接受的取舍，
不再以“每个工具调用都零查表/零分配”约束实现。

### 2.1 调用作用域与缓存

`ObjectId`、`SourceObjectSchema`、`TargetObjectSchema` 始终表示当前 owner 相邻边，
例如 V1→V2→V3 的第二边必须是 V2/V3。子值转换继承这些只读信息，
自己的值端点及声明的依赖表属于内部绑定数据；本片不必公开通用 SourceValue/TargetValue 平台。

每个注册 provider 有自己的依赖 key 作用域。Box 的 `Value` 和 Pair 的 `Element` 在各自表中解释；
相同局部名字可以出现在不同 provider 中，不能凭名字或相同 TState 在父/子表中串查；找不到 key 不向父 scope 回退。
`GetValueUpgrade` 返回的简单委托已封装相应子 Context，内部再调用三参值 provider，
用户只传值，不手工把 Box Context 传给 Pair。Context 不是可变的递归游标。

snapshot 缓存保存完整端点、所选 provider 和子计划，**不保存 ObjectId、本次 Context 或捕获它们的调用委托**。
执行当前对象边时创建 Context，工具委托可以捕获本次子 Context；相应小额分配可接受，暂不池化。
这避免缓存中第一个对象的身份/上下文泄漏到后续对象。纯代码或无调用状态的 delegate 仍可安全复用。
工具与 Context 只用于当前同步转换，不增加 Dispose、租约或异步服务协议。
用户把本次工具保存到静态字段、在另一个对象转换中调用属于违反调用合同；当前不提供逃逸检测或失效守卫。
若以后引入池化、异步或合法跨回调复用，再单独设计生命周期，不能默默复用带旧 ObjectId 的工具。

### 2.2 查询与预绑定的区别

`GetValueUpgrade` 只查询当前 Context 已绑定的依赖，核对 key 与请求的 CLR 状态类型后返回工具。
不暴露可变目录，不接受任意新规则/版本请求，不因调用 Get 而新建迁移路径。
预声明依赖缺失、歧义或完整 Schema 错绑仍在该对象整链业务 callback 前失败；
但用户函数里的未知 key、错误泛型实参，只能在执行到 Get 时拒绝，不能承诺预先检查任意 C# 函数体。
该类调用错误也不能动态解析或 fallback。读失败仍不交付 World、不隐式写回；不撤销任意用户外部副作用。

未来若具体迁移案例需要执行中首次解析工具，再单独调整预绑定保证；当前不因加入 Context 而自动开放此能力。

## 3. 显式选择：类型正确还不够

一个值能力在逻辑上绑定：

```text
用户选择的规则集/意图
    + source 完整槽语义 + target 完整槽语义
    + typed 输入/输出 + 所选 provider 与子能力
```

完整槽语义包括基元 kind、string/durable 引用类别及 nominal 约束，或 inline 的完整 exact Schema。
DTO CLR 类型只是交叉检查，不能代替这些信息。数值 uint、string ID、Node ID 都可以是 uint，
却不能因为两个泛型参数相等就共用 Identity 或业务转换。
引用槽的匹配依 nominal 约束；目标对象自己的 Schema 升版不等于该 ID 槽需要值转换，
它也不授权通过值能力调用目标对象的 Upgrade。

owner 定义时一次声明每个能力 key 的**规则选择及 source/target 槽位置**。
规则名属于应用代码选择，不是新的持久 ID；不能扫描程序集后按“唯一看起来可用”替用户决定。
槽位置基于历史 Schema 声明段与 FieldId，继承中必须保留声明段，不能仅用可能重复的数字。
模板按本次 exact Schema 解析后，才得到两端完整槽语义。重复 T、相同 TS 都不抹掉字段的选择位置。

以下保留设计期的用户写法草图，实际落地的属性名称与选择器见 §8：

```csharp
[OwnerUpgrade("Box", 1, 2)]
[UpgradeDependency("value", typeof(Coordinates), sourceField: 1, targetField: 1)]
static void Upgrade<A, B>(
    in BoxStates.V1<A> old, out BoxStates.V2<B> next,
    UpgradeContext context)
    where A : unmanaged where B : unmanaged {
    var value = context.GetValueUpgrade<A, B>(BoxUpgradeSlots.Value);
    next = new(value(in old.Value), 0);
}

[ValueUpgradeProvider(typeof(Coordinates), "Point", 1, 2)]
static void PointToMillimetres(
    in PointStates.V1 old, out PointStates.V2 next, UpgradeContext context) {
    next = new(old.X * 1000, old.Y * 1000, old.LabelId);
}
```

简例只有一个声明段，因此省略段参数。正式实现须用生成的字段选择器或明确的声明段/FieldId 定位；
依赖从原来的参数 attribute 移到方法元数据；不通过扫描函数体的 Get 调用发现依赖。
key 可先采用区分大小写的 provider 局部字符串与生成常量，不建立全局持久 ID 或 public key 类型框架。
例如 `BoxUpgradeSlots.Value` 是本方法依赖名称 `"value"` 的生成常量；定位仍由 Context 的 provider scope 决定。
具体属性、selector 表达及规则集登记外观已在本片真实 SG 首闸门冻结，见 §8；不能把上述示意语法当作实际 API。
新值 provider 采用三参形状；DB-038 保留的旧二参 owner 不允许声明 Context 工具依赖，SG 明确诊断，
需要工具时将该方法改为三参。不会因为无法访问 Context 就悄悄忽略依赖声明。
泛型 Pair 可在该规则集中登记一个开放值 provider，声明一份 element 依赖，由用户函数重复调用。
两个 T 槽的完整语义一致性已由 DB-038 的统一替换检查承担，不再增加“列全委托使用位置”的元数据。
需要不同业务规则时则声明不同能力 key。
声明依赖选择的是转换来源，不能静态证明任意用户 C# 传入了声明的 source 字段、最后把结果放到声明的 target 字段。
例如 Count 与 NodeId 同为 uint，把 Count 误传给 Node 转换，且其数值碰巧是合法 ID，最终引用校验也未必能发现。
框架校验声明与绑定的语义，用户业务函数的数据流仍需自己的测试；不引入方法体数据流分析器。

## 4. 绑定协议，复用同一代码目录

扩展 DB-038 已有 provider 目录及操作 snapshot，不另建全局可变注册器：

1. 应用显式登记 owner provider、值 provider 及它们声明的依赖。SG 发出登记材料与强类型调用 adapter；
   frozen snapshot 内再按实际的 nominal family/两端完整 Schema 闭合。
2. 先按 DB-038 确定对象的完整相邻版本路径。值能力只处理已给定的两个端点，
   不枚举版本、不根据已登记转换猜中间布局、不执行回调探路。
3. 对 owner 声明的每个依赖，先在所选规则集中按两端 nominal 身份/版本、kind 及显式模板适用形状定位候选，
   再严格核对完整 expected 槽语义和状态类型。多个候选直接拒绝，不增加 exact/open 模板优先级搜索。
   候选存在但完整 Schema 不匹配属于错误，不能把它当成“无候选”；不同规则集可拥有相同端点的不同业务实现。
4. 开放值 provider 可以递归请求其明确声明的子能力。这是代码依赖绑定，
   不为未请求的字段自动发明转换，也不对未知类型尝试闭合所有备选模板。
   递归绑定环和超出支持深度明确失败；不发布半成品 binding。
5. 所有相邻边及其依赖绑定成功后，生成/缓存不含调用状态的执行计划，按对象/边构造 Context，再调用用户代码。
   Context 工具表提供 typed 委托；允许局部 key 查询及类型检查，不在用户调用时重选规则或使用 DynamicInvoke。
6. 已选择条目的 expected Schema 不匹配、子依赖缺失或执行抛错时直接失败。
   不尝试另一个业务规则，也不回退为 Identity。缓存以 snapshot 为边界并保持完整 Schema 一致性。

可显式提供 `KeepExact` 透传能力。为了让 Box<int> 在定义统一 bump 时复用通用 owner，
规则集也可由作者**明确启用**“根本无显式候选且完整槽等价时使用 KeepExact”的有限兜底。
显式匹配规则优先；它即使在同 Schema 下执行业务变换也不能被 Identity 吞掉。
多个显式匹配不靠优先级选胜者，匹配条目失败也不进入兜底。仅 A=B=uint 不是完整槽等价。
特别是同 Point@2 的显式规则 expected 布局有误时，不能因完整 Schema 不等而排除它，再悄悄透传实际值。
未启用兜底、槽不等价或缺子依赖时，均在执行前失败。

闭合 owner 专用边仍按 DB-038 优先于通用 owner，已选闭合边失败仍不回退。
本节没有新增值版本最短路、通用 overload 排序、全局默认升级、Initializer 或自动业务转换。
需要 Point v1→v3 的值转换时，用户可以写明确调用 v1→v2、v2→v3 的普通 helper；
所有参与的中间表示仍由用户显式指定，不由运行时搜索路径。
框架能预检的是声明的 owner 链和值能力依赖；普通 C# helper 内的局部 DTO/直接函数调用不是 binder 可审计的图，
不承诺检查任意函数体里的所有临时表示。它们仍属于用户业务代码责任。

## 5. 保留的限制与实际简化

- `Phantom<T>` 旧版没有 T 值，不能凭 `ValueUpgrade<A,B>` 获得不存在的 A 或决定中间 B。
  DB-038 §6.4 的已登记中间 Schema/显式闭合边等依据仍必需；不增加 default(T) 初始化协议来掩盖缺信息。
- same key 完整 Schema 冲突仍拒绝，包括框架推导或绑定链中的临时中间 Schema/DTO；不能凭相同 CLR 状态类型绕过。
- 历史值 provider/组合 helper 只引用纯状态 family DTO，不依赖已删除的领域类型。
  独立引用对象族的 current/migration CLR 能力保留规则仍由 DB-036/038 约束。
- 值规则可以构造新状态、保留或清除引用 ID，但没有其他对象内容/新 ID 上下文。
  整体 Upgrade 后仍按 current 全目录验证引用。匹配正确不能证明用户业务逻辑正确。
- 预绑定失败保证该对象链尚未调用业务转换；不许诺整个 Revision 的其他对象也零 callback。
  当前 Normalize 逐 source 行执行。执行期间用户异常也不能撤销任意外部副作用，纯转换仍是调用合同。
- frozen 目录固定选择和依赖，不会自动深冻结用户 delegate 捕获的可变外部对象；推荐普通纯静态业务函数。
  Context 提供的 owner 信息及只读工具不构成对任意用户函数纯度的静态证明。

当 K 种 owner 包装 M 种值且复用相同规则时，可以从 K×M 个闭合外层函数降为 K 个通用外层函数、
M 个叶子规则，加上各模板一次性的依赖声明。开放 Pair 组合后无需专门编写 Box<Pair<Point>> 的业务外壳。
若每个闭合 owner 都有独特业务语义，仍需要相应规则选择，不能凭组合框架消除这份业务信息。

## 6. 产品接缝与研究验收

本片的施工前提是 DB-038 G0–G4 已完成：泛型持久身份/模板 history/完整 Schema、纯状态 family、
通用与闭合 owner 升级、最小 Context、真实保存恢复闭环均已可用。
本片只增加声明的值依赖/规则集、Context 工具获取、开放值 provider 组合及其验证。
DB-038 的 Context 入参、TypeExpr/wire 和通用/闭合 owner 规则保持；不再借此重开类型格式。

DB-038 的 [SG Upgrade](../../../src/DurableGraph.Generator/DurableGraphGenerator.GenericUpgrades.cs) 已接受
显式三参数方法并适配旧非泛型二参数方法；其 DTO 符号在当前生成轮尚未存在，采用发出强类型调用后由编译器校验的办法。
本片沿用这条原则：先读取显式 family/version/槽选择的元数据与 history，
发出 expected typed adapter，再让最终编译器检查用户方法，不能依赖初轮不完整的 DTO Roslyn 符号推断全部绑定。

| 验证层级 | 要回答的问题 |
|---|---|
| 独立机制 | static/delegate 两种形状均能组合；Box/Pair/嵌套复用、两字段不同策略、unmanaged 状态、冷闭合后 typed 调用 |
| 小范围绑定反例 | 同 TS 异语义、同 key 异布局、无依赖/多匹配、预绑定失败零 callback、执行异常不回退 |
| 本片 G0 用户入口 | 真 SG 识别三参 owner/value 方法及方法级依赖声明，生成 key/adapter；Context 获取能力；未知 key/错 typed 请求在调用时拒绝；错误签名及旧二参附工具依赖编译期拒绝 |
| 本片 G1 绑定 | 同规则集叶子/开放 Pair、继承声明段和重复参数；预声明依赖缺失/歧义/环/错 Schema 零该对象回调；KeepExact 不吞坏候选；不同 snapshot 不串绑定 |
| 本片 G2 执行作用域 | Box→Pair→Point 使用各自依赖表；两个对象和多跳的 ObjectId/owner 端点不串；同 T 不同业务 key；Get 不动态解析；执行异常不重选 |
| 本片 G3 历史闭环 | 真实 PackageReference 跨版泛型 owner 复用值规则；删旧领域值类型后仍升级；继承 DB-038 中间布局/完整 Schema 拒绝规则；Load 强制 Base 与稳定续写 |

独立实验的入口和证据边界见 [GenericBindingShapeProbe](../../../experiments/GenericBindingShapeProbe/README.md)。
手写 generated-like adapter 和实验槽 record 不证明真 SG、产品 Schema 匹配或整套 Normalize 已实现。
实施本片时按 G0→G3 完成后停止，不自动进入数组/BCL，也不新增 wire 版本。

语言事实参考：[C# 泛型方法推导](https://learn.microsoft.com/en-us/dotnet/csharp/programming-guide/generics/generic-methods)
说明不能只从返回类型或约束推导类型参数；[静态接口成员](https://learn.microsoft.com/en-us/dotnet/csharp/advanced-topics/interface-implementation/static-virtual-interface-members)
说明受约束泛型参数的调用形状。具体选用 delegate 是本项目成本判断，不是语言禁止 static 方案。

## 7. 审阅与结论记录

### 原委托形状的研究证据

本轮由最小架构、需求/技术反审及历史语义三位 subagent 独立研究并交叉质询；
历史语义沿用用户指定的 gpt-5.6-sol / max。主代理检查实际 Normalize/SG 接缝，独立实验由另一位 agent 完成。
最小架构审阅者初选 static helper，在比较当前调用路径与委托组合后撤回其必要性；
需求反审也明确撤回“静态方案必然需要 emit/全局可变目录”的过强推断。
推荐的核心是显式依赖及 exact 适用性，委托是当前较小的执行形状；并未以审阅人数代替证据。

最终定向复核没有剩余设计阻塞。审阅促成三项局部加固：定位显式候选后再校验完整 Schema，
防止坏条目被 KeepExact 吞掉；按对象明确预绑定范围；区分依赖声明校验与无法保证的用户函数体数据流。
Pair 的重复 T 一致性复用现有替换检查，不另增委托使用位置清单。

主代理重跑 `pwsh -NoProfile -File experiments/GenericBindingShapeProbe/Run-Probe.ps1`，
**35 项预期结果与 PASS，exit 0**，保留 DB-038 原 20 项并增加 15 项值组合/选择见证。
新增 fixture 为 342 行：一部分比较 static/delegate 的真实 C# 组合与冷闭合；
另一部分以实验槽 record 和有限 `uint→uint` 委托测试明确 rule ID 的预绑定。
它验证了缺失/冲突/循环/错完整布局在业务 callback 前拒绝、显式 KeepExact 及坏候选不得透传、执行异常不重选。
**没有验证按规则集和 nominal 模式自动发现候选、真 SG 依赖属性、完整 Schema 求解或产品 Normalize。**
这些仍属于 §6 的产品首闸门；不把研究结果当作已实现泛型支持。
根 `dotnet build DurableGraph.slnx --verbosity quiet` 通过，零警告、零错误。
本轮没有修改产品源代码或格式，相关执行验证使用上述独立 Probe，没有重复将旧产品测试计数作为本轮验收。

### UpgradeContext 修订与两轮施工复核

2026-09-08 用户采纳统一 Context、灵活性优先、当前先提供预绑定工具，并计划分别调度 DB-038 和 DB-039。
本次文档修订把逐参数注入改为 Context 入口，明确 provider 子作用域、只缓存计划、逐对象/边调用信息，
以及声明错误与 Get 调用错误的不同发生时点；将 DB-038 对本片验收的反向依赖移除。
目标、当前焦点、路线图和术语表同步其职责；无产品源代码或实验变更。
原 35 项结果不覆盖新 Context 用户写法、key scope 或生命周期，新证据由上面的 G0–G3 产生。
三位审阅者确认两片可独立交付：没有要求重新裁决泛型主干的结构性短板。
语义审阅提出的 invocation lease 经反例检查延后：当前没有池化/异步/合法跨回调消费者，
框架只缓存计划、用户不得逃逸本次 Context 的合同已经足够，不为假想扩展增加失效状态。

## 8. 产品施工合同与验收映射

2026-09-08 开工基线为 `888bd74`：根 solution build 零警告/错误，1077 项测试通过；工作树干净。
交付目标是通用 owner 显式调用可组合值工具的真实历史升级闭环。依赖顺序为元数据与冻结目录 →
SG adapter/运行时预绑定 → 调用作用域 → 真实包跨版恢复。不同时统一 DB-038 的两条生成 API 路径。

本次冻结的代码接缝：

- `[ValueUpgradeRuleSet(AllowKeepExact = true)]` 标记应用选择的代码规则集；Type 是本地代码目录的身份，
  不成为持久 ID。`StateValueUpgradeRuleSet` 与定义一起经已有登记入口进入冻结 snapshot。
- `[DurableValueUpgrade(typeof(Rules), "Point", 1, 2)]` 生成同一 inline 定义的值 provider；
  字符串定义 ID 与历史 DTO 允许删去旧领域 struct。开放 Pair 的 nominal 参数由 retained history arity 生成。
  Runtime 元数据还可显式表达 builtin、引用、闭合 nominal 子集与完整 expected 槽；不通过扫描程序集发现规则。
- `[UpgradeDependency("value", typeof(Rules), "Box", 1, "Box", 1)]` 用两端声明定义 ID + FieldId 定位槽。
  沿 exact 祖先链选择声明段后读取其直接字段，避免展平后重号。key 按 provider 局部、区分大小写解释。
- SG 生成 `Generated.UpgradeSlots_<UTF8HostHex>_<UTF8MethodHex>` 常量类；属性 key 必须是合法 C# 标识符，
  关键字会转义。Runtime key 只要求非空。Context 的字符串入口仍可直接使用相同 key。
- 规则集、provider 和依赖元数据不可变；snapshot 只缓存无调用状态的计划。
  每对象/相邻边创建 Context，子值工具绑定独立依赖表并继承同一 owner 信息。
- 完整候选筛选先于 Schema/CLR 签名校验；显式候选失败不透传。KeepExact 须明确开启且零候选、完整槽相等。
  整条 owner 链及全部声明依赖在首个业务 callback 前绑定，并在使用缓存时复核完整 exact 闭包。

施工中确认的简化与边界：

- 值工具两端已经由 exact 字段确定；绑定完整 Schema、取得其状态类型并严格统一所选方法签名即可。
  不再次从实际 DTO 反推同一布局；DB-038 用于确定未知 owner 中间布局的反推机制保留。
- 依赖只能选择当前值的子字段，合法 exact 布局本身是有界无环 DAG，因此有效输入不能另构造值工具绑定环。
  代码保留递归/深度防护；验收以完整 Schema 深度合同和共享依赖 DAG 为据，不宣称实际构造了合法环。
- 同一对象/升级边内对同一完整子计划复用调用工具，避免把共享 DAG 展开成重复 Context 子树；
  该调用级缓存不进入 snapshot，后续对象或边另建 Context。
- SG 属性的规则 marker 必须来自同一编译，避免接受后未生成登记材料；跨程序集生成仍属后续范围。
  显式值规则可仅依赖保留的 history，不要求编译中仍有对应领域声明；这不提供已删除对象族的 current Normalize。

| 要求 | 实施责任/接缝 | 集成验收 | 状态 |
|---|---|---|---|
| G0 用户属性、局部 key、typed adapter、旧二参工具诊断 | [SG](../../../src/DurableGraph.Generator/DurableGraphGenerator.ValueUpgrades.cs) / [metadata](../../../src/DurableGraph/Runtime/Binding/StateValueUpgradeProvider.cs) | [真实 SG 编译与调用](../../../tests/DurableGraph.Tests/GeneratedValueUpgradeTests.cs)、错误签名/孤立依赖/别名/外部 marker；纯历史与纯规则登记 | 已验证 |
| G1 候选与完整槽匹配、递归依赖、KeepExact | [值绑定](../../../src/DurableGraph/Runtime/Binding/StateBindingContext.ValueUpgrade.cs)；StateStore 冻结目录 | [值绑定反例](../../../tests/DurableGraph.Tests/ValueUpgradeBindingTests.cs)、[snapshot 隔离](../../../tests/DurableGraph.Persistence.Tests/ValueUpgradeCatalogTests.cs) | 已验证 |
| G2 子作用域与整链预绑定 | [Context](../../../src/DurableGraph/UpgradeContext.cs) / [owner 计划](../../../src/DurableGraph/Runtime/Binding/StateBindingContext.Upgrade.cs) | 嵌套 Box→Pair→Point、两个对象/多跳、未知 key/类型、抛错不回退、28 层共享依赖 DAG | 已验证 |
| G3 历史包闭环 | [真实包消费者](../../../experiments/PackageConsumerProbe/ValueUpgradeConsumer/README.md) | 三代历史/四次构建、删除旧 inline 领域类型、强制 Base 与稳定续写 | 已验证 |
| 不变合同与独立审阅 | 主代理集成 / 独立审阅代理 | 根 build/full tests、六项真实包回归、格式未变、文档检查；独立审阅无剩余阻断 | 已验证 |

主代理实际执行：

- `dotnet build DurableGraph.slnx --no-restore --verbosity quiet`：零警告、零错误。
- 根 solution tests：**1106/1106**，零失败、零跳过；Runtime/SG 531、StateStore 317、Storage 155、Serialization 103。
  本片新增 29 项；初次接合的测试夹具问题已修复，最终验收是在重建后执行。
- `Run-ValueUpgradeProbe.ps1`：自打包八个依赖包，三个历史版本、四次构建/进程全部通过。
  history 数量 5→10→10→12，先前 SHA256 不变，缺规则的失败 Load 不修改仓库文件集合或 bytes。
  产物目录 `experiments/PackageConsumerProbe/obj/value-upgrade-20260908052950-35340-107b3438`，
  包版本 `0.0.0-value-e2e.20260908052950.35340`；执行日志位于同一 `obj` 下的 `db039-value-package.log`。
- 既有 `Run-GenericProbe.ps1`、`Run-InlineStructProbe.ps1`、`Run-HistoryCapabilityProbe.ps1` 复用上述最终 feed 全部通过；
  `Run-Probe.ps1` 和 `Run-StateStoreProbe.ps1` 分别自打包运行通过。连同新增值工具回归，共六项真实包脚本通过。
  对应日志为 `obj/db039-{Generic,InlineStruct,HistoryCapability,runtime,state}-package.log`。
- 集成文档检查：10 份 UTF8 Markdown、330 个本地链接/锚点通过；`git diff --check` 通过。

独立审阅促成并复核了调用级 DAG 复用、固定值端点免重复逆推、外部 marker 明确拒绝，以及无当前领域声明的登记修复。
本片没有更改 TypeExpr、Schema/history、Base/Delta 或 Storage 持久格式；没有新增程序集或自动值 Normalize 阶段。
