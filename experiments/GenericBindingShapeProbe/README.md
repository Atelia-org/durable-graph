# GenericBindingShapeProbe

2026-09-08 的独立机制见证：比较并验证 DurableGraph 泛型 DTO 候选中的
**领域类型参数 / 状态表示参数 / 静态操作类型参数**、运行期闭合及历史升级可书写性。
这是手写 generated-like C# 的编译运行实验，不是产品 Source Generator 已支持泛型的声明。
后续用户已选择统一 UpgradeContext 外观，见 [DB-039](../../docs/design-branches/0039-composable-value-upgrade-design/README.md)。
本 Probe 的值转换仍直接传委托，35 项历史结果不覆盖 Context 用户签名、依赖 key、子作用域或按对象调用信息。

## Typed ObjectId 独立补充见证

```powershell
pwsh -NoProfile -File experiments/GenericBindingShapeProbe/Run-TypedObjectIdProbe.ps1
```

2026-09-08 的补充脚本逐项生成隔离的 .NET 10 小项目，检查 `ObjectId<TDTO>` 的 unmanaged/尺寸、
强类型赋值、静态操作桥接、自引用、互引用和泛型扩张，并比较 class DTO、nominal marker、uint backing + typed 属性。
每项单独编译并在有超时的独立进程中加载；这是编译/加载形状见证，不运行产品 SG 或 StateStore。
脚本还明确检查已观测的 TypeLoadException；后续 CLR 若接受这些类型，应复核证据而非视为产品回归。
原始结果进入本目录忽略的 `obj/typed-object-id-*`；结论与适用边界见
[DB-040](../../docs/design-branches/0040-typed-object-id-representation-research/README.md)。原 35 项 Probe 的合同保持独立。

## 运行

```powershell
pwsh -NoProfile -File experiments/GenericBindingShapeProbe/Run-Probe.ps1
```

或直接执行：

```powershell
dotnet run --project experiments/GenericBindingShapeProbe/GenericBindingShapeProbe.csproj --configuration Release
```

依赖 .NET 10 与 Roslyn 5.3.0。项目不加入主 solution，不修改产品程序集、格式或接口。
`Program.cs` 用 Roslyn 编译 `Fixtures/*.cs.txt`，运行正例并检查四类编译负例；失败抛出异常且返回非零。

## 已观测的结果

2026-09-08，Windows、.NET SDK **10.0.201**、Release，以上命令 exit 0：

```text
ColdMakeGenericMethodCreateDelegateCachedFacade:True
SameUInt32RepresentationDistinctReferenceSemantics:True
PrimitiveUInt32ThirdBindingWithoutReferenceVisit:True
NestedStaticProjectionBodyAndReferenceVisit:True
FrozenUnmanagedDtoWithoutDomainReferences:True
GenericPrivateReadonlyClassFieldRefHydration:True
TrailingUnusedAccessorTypeParameterAlsoWorks:True
ReorderedAccessorParameters:MissingFieldException
GenericReadonlyStructDefaultRefHydration:True
OpenSameRepresentationAndClosedOwnerUpgrade:True
StaticHelperConstrainedCallWithoutCallvirt:True
HistoricalBodyAndUpgradeWithoutOldDomain:True
NamedFamilyDtoAliasesAndOwnerUpgrade:True
PhantomFirstEdgeHasMultipleStateClosures:True
ExplicitHistoricalMiddleThreeVersionUpgrade:True
StaticAndDelegateValueUpgradeReuseInBoxAndPair:True
StaticAndDelegateNestedValueUpgradeComposition:True
OwnerSelectsDifferentRulesForSameDtoFields:True
ValueUpgradeDtoRemainsUnmanaged:True
BothValueUpgradeFacadesColdClosedWithoutDynamicInvoke:True
ExplicitSelectionUsesFullSlotNotStateClrType:True
EqualSchemaCanExplicitlySelectBusinessRuleInsteadOfIdentity:True
IdentityRequiresCompleteSlotEquivalence:True
ExplicitSemanticConversionBetweenEqualClrStateTypes:True
AllKnownChainDependenciesPreboundBeforeBusiness:True
MissingConflictingOrWrongExactDependencyFailsWithZeroCallbacks:True
DependencyCycleFailsWithZeroCallbacks:True
KeepExactFallbackOnlyWhenExplicitRuleIsAbsent:True
MalformedExplicitRuleCannotFallBackToIdentity:True
BusinessFailureDoesNotTryAnotherRule:True
HistoricalAssemblyWithoutOldDomainTypes:True
MissingBusinessConversionRejected:CS1503
MissingDomainConstraintRejected:CS0452
PhantomFirstEdgeCannotInferStateParameter:CS0411
CurrentStateCannotReplaceHistoricalMiddle:CS1503
GenericBindingShapeProbe:PASS
```

| 问题 | 见证与可支持的结论 |
| --- | --- |
| SG 所需的开放模板/运行期闭合形状在 C# 中可行吗？ | `FacadeFactory` 解析领域实参，递归闭合 `Pair<Pair<Point>>` 的状态和 helper，以 `MakeGenericMethod/CreateDelegate` 构建外观。当前单线程缓存按领域实参区分绑定；调用成员时无逐成员 Type 查找。 |
| `uint` 表示能否保留不同槽位语义？ | `Box<uint>` / `Box<string>` / `Box<Node>` 三者同为 `BoxStateV1<uint>`，绑定不同 projection 与 state ops。primitive uint 不登记对象、不访问引用表；string 状态交给 Node 引用遍历，以及数值 999 被误当作不存在的引用，均抛 `InvalidDataException`。这不意味着仅凭相同 CLR DTO/bytes 能判定选错了 binding；绑定身份必须由外层保留。 |
| 冻结状态是否需要领域引用？ | 嵌套 Pair/Point DTO 满足 `unmanaged`，`IsReferenceOrContainsReferences` 为 false；Capture 后领域改动不改变已捕获值。实际写入、读回及递归引用遍历全部通过。 |
| generic private/readonly 字段可以恢复吗？ | `ReferenceDomainAccess<T>` 返回 private readonly 字段的 ref；`ReferenceHydrate<TDomain,TState,TProjection>` 用静态 helper 写回无构造器分配的 class。`ValueDomainAccess<T>` 配合 default readonly struct/ref 恢复值，Transient 保持 0。class/unmanaged 约束均实际执行。 |
| accessor 与执行 helper 为何分开？ | 把领域参数维持在原 ordinal 的形状可用。额外尾随且未使用的 `TState` **也可用**，所以分离不是“参数数目必须完全相同”的要求；但调换成 `Access<TState,TDomain>` 后本例抛 `MissingFieldException`。领域参数优先且隔离表示参数可减少错位面。不能外推未试验的继承、嵌套类型或一般约束组合。 |
| static abstract 是否避免实例接口调用？ | Roslyn 生成的 `BoxProjection<,,>.Capture` IL 含 `constrained.` 和 `call`，没有 `callvirt`。此结论仅为该方法的 IL 形状；外层异构外观仍使用接口/装箱。 |
| 升级如何书写？ | 同状态表示可用一个开放 `Upgrade<TState>` 透传。Point v1→v2 的业务变化通过显式 closed owner edge 转换（X 乘 1000，引用 ID 保留）。只有不同 `TOld/TNew` 参数、却不提供转换的代码报 CS1503。 |
| 历史状态是否依赖旧领域 struct？ | 第二个独立编译不包含领域 Point/Box 定义，不引用第一个程序集；读取第一个程序集产生的 bytes，并执行历史 body/引用校验及 owner Upgrade。 |
| 用户能否自然命名泛型历史 DTO？ | `NamedHistory.cs.txt` 使用非泛型 `Generated.Family_Box` 宿主及嵌套 `V1<TState>/V2<TState>`；用户可 `using BoxStates = Generated.Family_Box`，书写 `in BoxStates.V1<PointStates.V1>` 及开放 generic Upgrade。仅证明 C# 表达与调用成立。 |
| 中间版本的状态参数能否自动推导？ | `Family_Phantom.V1` 不含 T，V2 才新增 T 字段。`AddField<TState>(in V1)` 未显式传 TState 时编译报 CS0411；即使赋值目标已声明为 `V2<Point.V1>`，也不能从返回类型推导。分别显式闭合 `uint` 与 `Point.V1` 均可编译运行，表明 C# 模板本身没有选出唯一中间表示。 |
| 能否拿 current 表示填补历史中间版本？ | 显式第一条 owner edge 选择 `V2<Point.V1>`，第二条把 Point.V1 转换为 Point.V2，形成 `Phantom.V1 → V2<Point.V1> → V3<Point.V2>` 完整执行链。把 `V2<Point.V2>` 传给第二条 edge 编译报 CS1503；不能省略历史中间表示或业务转换。 |

## 值 Upgrade 组合与显式选择见证

`Fixtures/ValueUpgrade.cs.txt` 共 342 行，追加两种可执行形状，均保留强类型 `in` 参数：

| 候选 | 本例的表示及已观察成本 |
| --- | --- |
| A：static interface + helper 参数 | `IValueUpgrade<TOld,TNew>` 的 `TUpgrade.Convert(...)`；owner 再带一个 helper 类型参数，组合 Pair 时闭合 `PairStaticUpgrade<TOld,TNew,TUpgrade>`。没有操作实例字段；每一种操作组合会增加一个闭合 helper 类型。 |
| B：typed delegate 注入普通 generic owner | `ValueUpgrade<TOld,TNew>` delegate 作为 owner 参数；复用同一 Point 函数，组合 Pair 时在绑定阶段建立一个捕获子 delegate 的闭包。热路径是 typed delegate 调用，不使用 `DynamicInvoke`。 |

两者实际将相同 Point v1→v2 业务规则复用到 Box、Pair 和嵌套 Pair，输出相等。
同一 Pair 的两个 Point 字段还可显式分别选择乘 1000 与加 100；输入/输出 DTO 类型相同不唯一决定业务规则。
所有输出仍为 unmanaged DTO；fixture 在无旧领域 Point/Box 编译依赖的第二个程序集运行。
两种方案都通过 `MakeGenericMethod/CreateDelegate` 冷闭合为简单 facade，之后反复执行 typed 代码。
本例不做 benchmark，不能据此认定 A 或 B 更快；外层 facade 仍有异构边界的接口调用与装箱。

同文件的有限 `ExperimentSelection` 使用 **实验 record** `ExperimentSlot`：
`StateType + Kind + Family + Version + LayoutToken`。它不是产品 `DurableSchema`，不生成或保存 Schema；
尤其 `LayoutToken` 只是显式模拟“同 family/version，但完整布局定义不同”这一冲突。
为隔离选择问题，该 harness 的实际操作统一为 `ValueUpgrade<uint,uint>`；另校验 StateType 必须匹配它。

已运行的选择边界：

- 调用者提供完整有限链、rule ID、exact 槽端点和明确依赖。Prepare 全部绑定后才交出可执行链，
  不发现迁移路径、不搜索最优路径、不推导中间状态、不登记全局 latest。
- Identity 必须显式选择且完整槽相等；相同完整槽仍可显式选业务规则，已有 Identity 不会吞掉它。
  相同 uint CLR 表示的 number/string ID/durable ID 不可互换绑定，但显式规则可进行已声明的语义转换。
- 调用者还可显式启用有限 `KeepExact` fallback：仅 requested rule ID 根本不存在且完整槽相等时透传。
  ID 存在即先验证其完整端点；即使请求两端自身相等，同名同版但 layout 错误的显式规则仍报错，
  不会被当成“无匹配”滑入 identity。正常无候选的相同槽透传、不同槽拒绝、未启用 fallback 拒绝，
  以及相同 Schema 的显式 normalize 仍执行，均已实际运行。
- 先有合法步骤、后有缺失依赖/端点不匹配时，全部 business callback 仍为 0；
  重复冲突 rule ID、同名同版不同 layout token、错误 CLR 状态类型与循环依赖也在业务执行前拒绝。
- 业务规则执行抛异常后原样失败，不尝试另一个候选规则；成功路径使用已绑定 typed 依赖，不逐值查找。

这里的绑定工厂是本实验控制的纯组装代码。callback=0 只约束被测业务函数，
不声称任意用户自定义工厂都无副作用，也不证明任意领域修改可以回滚。
此 harness 不等于完整 Schema 选择器、真实 SG 签名检查或产品 Upgrade API。
新增 fallback 只按调用者明确给出的 rule ID 判断候选是否存在；
**未验证根据 ruleset 与 nominal 端点模式自动定位候选**，也不定义其语法或歧义选择算法。

## 证据边界与后续闸门

- 这里使用 `BinaryWriter/Reader` 写少量定长字段，**不是 DurableGraph wire**；没有 Base/Delta 策略、
  SchemaStore、对象 envelope、Revision、GraphSession、真实源生成器或 NuGet 消费验收。
- helper/DTO 家族名、工厂实参映射与 Upgrade 都是手写；不证明 SG 可从真实 history 自动生成命名、
  找到 Upgrade，或完成泛型持久身份、exact 版本替换与引用约束匹配。
  三版本 phantom 见证只证明表达式无法推导、多个闭合可编译、显式历史链可运行；
  **未实现或验证完整 Schema 边规划器**，也不证明它能自动发现/消歧所有中间状态闭合。
- 工厂在单线程下缓存一个完整绑定，未验证并发初始化、失败重试、递归模型目录、发布半成品防护。
  `Pair` 是值组合见证，Node 仅作身份投影，不递归闭合引用目标 body。
- 第二个程序集证明编译依赖闭包中没有旧领域类型；两个程序集在同一进程运行，未模拟独立发布包。
  真正实现仍须跨版本真实 PackageReference 回归。
- IL 不是 JIT 机器码；没有性能 benchmark，也不保证内联、零间接调用或 NativeAOT。
- 尚未证明泛型继承、任意约束、任意参数表达式、数组/BCL 组合、自动业务升级。
  后续产品分片须先设计这些支持边界，再由真实 SG 与持久化测试验收。

本 Probe 保留为可复跑的机制证据。产品泛型分片完成后，再按其仍能捕获的独立风险决定是否保留回归。
