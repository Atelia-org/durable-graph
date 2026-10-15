# DB-093：核对所有显式存储分类是否有效

> 状态：**方案已选，待实施**。2026-10-15 用户确认方案，并经独立审查、交叉质询与补充 Probe 完善本计划；尚未进入产品实施。
> 源码调研基线：`b209a777c3c103108f95e0277241a127fb1cc7b4`。
> 本案约束 Generator 的显式分类准入，不新增普通属性持久化能力。后继能力只作 [DB-094 前期调研](../0094-backing-field-support/README.md)。

## 1. 问题、范围与最小成功标准

普通 Durable class 的 `[field: DurableField]` 自动属性能编译、提交成功，但 backing field 在字段发现阶段被过滤，实际 Schema 不含该槽，冷恢复得到默认值。这个问题也涉及普通 struct、static backing，以及被 C# 忽略的错误 `field:` target。

本案选择的合同是：**在 Generator 管理的 Durable 声明内，每一处 DG 显式存储分类都必须有明确解释；能够处理就处理，不能处理就报错，不生成遗漏该标注的 current 持久模型。**

“所有显式分类”不等于“所有实例存储必须分类”：普通 class/struct 未标注的自动属性、事件和主构造捕获保持当前准入；显式字段的原分类要求保持。record 保持既有完整分类及事件拒绝策略。

最小实施模型是：**现有字段分析保持为唯一持久字段准入；新增局部补漏检查；错误接入既有 `hasErrors → null → 不进入 validTypes` 流程。** 不新建分类模型或 current 资格登记表。

最小可观察成功标准：H01 在两个独立编译的默认与 Family 组都报预期 Error，且没有自己的 current 候选、Capture/Hydrate/binding；各组独立合法普通字段模型仍按预期路线生成。record 正控另编译，避免它把默认组整体切入 Family。普通包消费者须复现目标诊断，修正声明后可正常编译、保存和冷重开。

## 2. 依据与证据边界

<a id="h01-evidence"></a>
### 2.1 已观察的失败

```csharp
[DurableType("HealthCheck.Ordinary", 1)]
public partial class OrdinaryModel : IDurableObject {
    [field: DurableField(1)] public int Value { get; set; }
    [DurableField(2)] public int Control;
}
```

2026-10-15 的独立普通包见证使用 DG `G=0.0.0-health.20261015.b209a77`，ordinary/record 对照均编译 0 warnings/errors，写入和另一进程只读重开均正常返回：

| 模型 | 写入 Value / Control | 冷读取 Value / Control | accepted history |
|---|---|---|---|
| 普通 class | 42 / 7 | **0 / 7** | 只有 Field2 |
| 相同字段声明的 record class | 42 / 7 | 42 / 7 | Field1、Field2 |

这份一次性包见证证明模型生成阶段发生遗漏，不是旧字段字节损坏。它不是已实施的诊断回归，也不是本轮重新完成的包发布资格。

### 2.2 当前源码原因

- [Generator](../../../src/DurableGraph.Generator/DurableGraphGenerator.cs) 的 `GetDirectFields` 过滤普通类型的 `IsImplicitlyDeclared` 字段，`AnalyzeDurableType` 只给 record 调用 storage audit。
- [record 检查](../../../src/DurableGraph.Generator/DurableGraphGenerator.Records.cs) 已有语义属性识别和未消费标注核对，但还包括 record 专属的完整存储限制，不能整段套给普通类型。
- [Attribute 定义](../../../src/DurableGraph/DurableFieldAttribute.cs) 允许 Field target；C# 并不能仅靠 AttributeUsage 区分显式字段与 backing field。

### 2.3 共用机制 Probe

[ExplicitStorageClassificationProbe](../../../experiments/ExplicitStorageClassificationProbe/README.md) 从 `.artifacts/h01-options/20261015/RoslynProbe` 整理而来，保留 14 个关键形状。原始基线全部可 Emit，且没有 DG 诊断；其中部分有 C# warning。

关键证据是：自动/C# 14 field-backed/get-only 属性有带 DG 标注的 `IFieldSymbol`；field-like event 的 backing 不作为普通字段 symbol 暴露，但最终 metadata 确实带标注；computed property 和普通主构造参数上的错误 `field:` 仅获 CS0657，不能据捕获字段存在就认定标注已附着。

Probe 分列输入/生成后 C# 诊断、Generator 诊断、Emit 和 PE metadata。它是机制观察工具；其成功退出不代表 Generator 已正确消费标注，不把“SG 无诊断”冻结为永久产品回归。原始基线与迁移后的运行说明只维护在该入口。

### 2.4 本次补充核实：field-like 语法不保证有 event backing

2026-10-15 在上述 Probe 的隔离副本中追加 9 个 fixture，使用 C# 14 / Roslyn 5.3.0 与当前产品源码观察。原 14 项加新 9 项均可 source Emit，当前 SG 均无 Error；这仍是缺陷调查，未取得修复资格。

| 新增形状 | C# / PE 观察 | 对本计划的约束 |
|---|---|---|
| `abstract event`、`extern event`，分别标 `field: DurableField` / `field: Transient` | CS0657；无 backing；extern 另有 CS0626 | 没有真实 target，不能接受为有效 Transient |
| `partial event` 定义部标 DurableField / Transient，或实现部标 Transient；实现使用 custom add/remove | CS0657；无 backing | 定义部虽为 field-like，仍不能按语法接受 |
| 普通 `virtual event` 标 `field: Transient` | PE backing 带 Transient | virtual 本身不是拒绝理由 |
| `event Action? First, Second;` 标 `field: Transient` | 两个 PE backing 都带 Transient | 共享 AttributeSyntax 合法，不是重复分类错误 |

partial 反例的最小成员为：

```csharp
[field: Transient] public partial event System.Action? Changed;
public partial event System.Action? Changed { add { } remove { } }
```

因此，事件核对需要语义 DG 标注、`field:` target 和真实 storage 的声明条件；既不能只看 `IFieldSymbol`，也不能把 `EventFieldDeclarationSyntax` 或 `!IsAbstract && !IsExtern` 当作充分条件。实现不需 Emit/反射或调用 C# 全量诊断来探测 backing。新增 fixture 源码与原始观察位于忽略的 `.artifacts/db093-dialectic/`；上述反例须在实施时进入正式测试。

## 3. 已选行为合同

### 3.1 类型与行为边界

适用源声明是 Generator 已管理的 Durable class/struct，含其当前支持的泛型与 partial 形状。既有类型资格、nested/file-local/ref-like 拒绝保持；不新增对整个仓库所有未登记类型的扫描。

普通类型新增核对，record 保现有准入。可复用语义识别和位置处理，不能把两种 policy 合成一个更严格的统一拒绝规则。

| 普通 class/struct 中的形状 | 0093 要求 |
|---|---|
| 当前受支持的显式 Durable 字段 | 沿现有类型、FieldId、Schema 与生成规则处理 |
| 当前受支持的显式 Transient 实例字段 | 排除，不新增槽 |
| 真实 property backing 上的 DurableField | Error：该 storage 当前尚不受支持；提示显式字段写法 |
| 真实 property backing 上的 Transient | 接受排除语义；不新增持久槽或该 storage 的投影，不调用 accessor |
| 有真实 backing 的 instance field-like event 上有效的 `field: Transient` | 接受排除语义；包括 virtual 和一个声明中的多个 event 变量 |
| instance field-like event 上的 DurableField | Error；delegate/event 不因标注获得持久能力 |
| classified static 字段、backing 或 field-like event | 沿 static 分类规则拒绝 |
| 同一真实实例 storage 同时 DurableField/Transient | 沿冲突分类规则拒绝 |
| computed property、custom add/remove、abstract/extern/上述 partial event 等没有目标 storage 的分类 | Error，不执行属性或事件方法来补目标 |
| 普通主构造参数上被 C# 忽略的 `field:` 分类 | Error；不能把未附着的标注移给 captured field 或 initializer 字段 |
| 未标记的自动属性、event、主构造捕获 | 保当前准入，不顺带要求补 Transient |
| 访问显式字段的普通包装属性 | 不产生第二份存储；只处理被实际分类的显式字段 |

真实 Transient target 仍须核实。仅凭语法出现 `field:` 不足以接受；反之，仅因 `IFieldSymbol.GetAttributes()` 未消费也不足以拒绝，event 是具体反例。

### 3.2 诊断与失败输出

本计划为普通类型的无效显式分类选择新增 **DG0024 / Error**，建议标题 `Invalid explicit storage classification`。这是待实施合同，当前 Generator 尚无此规则。

原计划拟用的 DG0022 已由 [InvalidDefinitionsOption](../../../src/DurableGraph.Generator/DurableGraphGenerator.cs) 占用，且 [现有测试](../../../tests/DurableGraph.Tests/CrossAssemblyGeneratorTests.cs)精确锁定；[SchemaExports](../../../src/DurableGraph.Generator/DurableGraphGenerator.SchemaExports.cs) 另用 DG0023。新编号已核对当前源码，实施时再核是否新增占用，不能仅按尚未完整登记的发布跟踪表取号。DG0021/0022/0023 保原职责，普通类型不借用 record 文案。

对能定位到真实 storage 的普通类型，主诊断顺序为：

1. static 且存在 DG 分类：DG0009，包括 static 双分类；
2. instance 双分类：DG0004；
3. 不支持的 instance Durable implicit storage（含 event backing）：DG0024；
4. 真实 instance Transient：排除；
5. 当前受支持的显式 Durable 字段：继续既有 FieldId/类型/重复 ID 规则。

没有真实 target 的显式分类使用 DG0024。只要仍能语义识别为 DG 分类，新增检查就报告自身错误并标记分析失败；允许与 C# Error 并存，不实现编译器诊断查询、归并或去重。C# warning/ignored target 不能代替拒绝；对于无法解析的任意非法 C#，不额外承诺逐项 DG 诊断。record 的既有诊断顺序不在本片重排。

诊断指向 attribute 或属性/event/参数的源位置，支持别名、后缀与全限定名，不依赖短名称文本匹配。继承层各管自己的声明，扫描全部 partial，并排除 nested 声明所有的标注。诊断中不要求用户理解 `<Value>k__BackingField`。

**错误判定须独立于消息最终是否被抑制。** 沿用模型分析的失败返回语义，不为错误 current 声明生成 Schema candidate、Capture/Hydrate 或 current binding；不能因 NoWarn/pragma 而重新生成少字段的成功模型。

该合同不要求所有 generated files 为空：同编译中无依赖关系的合法类型继续生成；现有路线允许的 retained history DTO/body 不应被新增全局停机分支一并删除。诊断被抑制且用户不引用生成能力时，普通 CLR 类型甚至仍可 Emit。验收检查错误类型自己的 candidate/current 投影和登记，不以所有文件为空、出现类型名或 `Emit.Success == false` 代替。

### 3.3 持久模型与包影响

本片不纳入新的持久槽，不修改公共 Attribute 签名、Schema/history/wire 编码、runtime 保存恢复、Family 路由或 ImmutableLeaf 资格。原来静默通过的错误声明将被拒绝，属于明确的构建行为收严。

交付需要新 G 中的 Generator，并重新编译模型项目；六个存储包 S 不因此升版。当前依赖准备继续使用[准确 pin 工作流](../../storage-dependency.md)。

应用若把 H01 改为显式字段而开始保存 Field1，仍是相对旧 accepted V1 的真实新增槽：须正常升版和显式 Upgrade，或在原型授权的实验库上重建。过去未保存的 42 无法由本修复恢复；不重写既有 history 或完成帧。

## 4. 最小重构形状

保留 `GetDirectFields` 和现有字段循环。普通类型只增加一个局部补漏检查，返回错误布尔值并接入既有失败出口；不向 emitter 提供新的分类 IR。复用 record 的语义属性解析、源位置与 partial/nested 归属工具，record 的完整存储 policy 仍单独应用。

补漏检查按以下三项闭合，不另做一遍完整字段分析：

1. 显式字段上的 DG 标注交给原循环，登记为已解释；原循环接受或拒绝都算已解释，避免再报 DG0024。FieldId、字段类型、重复 ID 仍只有原循环负责。
2. 对原过滤排除的 implicit 字段及 source 不暴露为字段的 event，核实实际分类和 target，按 §3.2 的 static/双分类/不支持 Durable/真实 Transient 顺序处理。事件只确认已知具有 backing 的普通 field-like 声明，排除 abstract、extern 与 partial/custom 等无 backing 情况；不实现事件配对框架。多变量声明的共享标注按整条声明解释，不能用 AttributeSyntax 充当 storage 的唯一身份。
3. 扫描本类型各 partial 声明剩余的 DG 标注；不能被真实 target 或既有处理解释的，报 DG0024。主构造参数不能靠 captured field 名称猜配，nested 的标注不能归给外层。

局部 syntax/span 集合如有需要只用于避免遗漏和重复报告，不成为新的模型或跨类型登记。普通 Transient backing 不进入 `durableFields`，不触发 Family；unsupported Durable 也不能先纳入再退回 BaseOnly 或属性访问。检查只服务编译期准入，不增加公开接口、运行时反射或持久状态。

| 位置 | 最小责任 |
|---|---|
| `DurableGraphGenerator.cs` 的 AnalyzeDurableType/GetDirectFields 周边 | 接入显式分类检查，保持错误声明的失败返回；不放开持久字段过滤 |
| `DurableGraphGenerator.Records.cs` 周边 | 复用必要语义工具；保 record 全分类/事件策略，按需抽取小 helper |
| Generator 的诊断定义及发布跟踪 | 登记 DG0024；保既有诊断编号及 record 规则 |
| `tests/DurableGraph.Tests` | 按可观察行为补反例、正控、生成输出与 suppression 断言 |
| `experiments/PackageConsumerProbe` | 增加有界普通包负/正故事；复用既有 feed/config，不新增通用 runner 框架 |
| README/Attribute XML/产品工作集 | 更新实际准入说明；不把 DB-094 写成已支持 |

## 5. 后续实施顺序与验收

### 5.1 一个源码修复片

先为 H01 建立失败测试，随即接入 DG0024、局部检查与既有失败返回，再补齐以下独立分支。它们是同一修复片的验收，不是先建框架再接行为的横向阶段。

| 证据组 | 最小见证与断言 |
|---|---|
| 两条真实生成路线 | 相同源包含 H01 错误类型和一个独立合法、非泛型显式字段 class。默认组不带 record、泛型、导入、Upgrade 或会触发 Family 的 history；另一组显式 `DurableGraphGenerateDefinitions=true`。分别确认合法控制生成传统 `DurableStates.g.cs` / Family `DurableGenericStates.g.cs`，且错误类型均无自己的 current 输出。仅切 option 不够：错误类型被过滤后若 `validTypes=0`，两条 emitter 都可能没执行 |
| unsupported 与无 target | ordinary class/struct 的 auto/get-only/C# 14 field-backed Durable；computed property、主构造参数、custom/abstract/extern/partial event 的误标。命中 DG0024 和源位置，不以无关 C# 错误或 CS8785 充数 |
| 既有诊断优先级 | 显式字段、backing、event 各选必要 static/双分类见证；真实 static 双分类仍为 DG0009，真实 instance 双分类为 DG0004。已由原循环解释的标注不附加 DG0024 |
| 合法排除与普通现状 | 真实 Transient backing、普通/virtual/多变量 event 不入槽；未标 ordinary storage 保准入；包装属性不重复存储。用一个真实 backing 的运行见证确认 Capture/Hydrate 不执行 accessor，且 Transient 不改变 Schema 或触发 Family |
| 局部归属与语义识别 | partial、泛型、继承声明层、nested 各选必要见证；别名/全名识别 DG 属性，指向无关 Attribute 的别名不误报。不做形状的笛卡尔积 |
| suppression 与 retained history | 一个混合见证：错误 current 被抑制诊断仍无候选/投影/登记，独立合法模型仍生成；按现有 Family 条件允许的 historical DTO/body 保留。不要求 CLR Emit 必败或所有输出为空 |
| 现有合同回归 | 复用 record、字段 Schema/body/history、Readonly 恢复、ImmutableLeaf 与 Definition option 的现有测试。constructor/initializer 绕过机制未改，不为每个新形状再建完整运行矩阵 |

Transient 的保证针对生成的 Capture/恢复：不访问被排除 storage，不新增持久槽；构造器/初始化器继续由现有分配机制绕过。它不是“整个应用不执行用户代码”的新承诺，不包括用户构造输入实例或显式 Upgrade。

### 5.2 一次普通包交付验证

使用新 G、准确 S、完整 feed、任务 NuGet.Config 和独立 consumer/cache：

1. 普通包的默认与显式 Family 负例沿用上述隔离 fixture，精确命中 DG0024/目标位置，并由合法控制输出确认实际路线。普通失败 build 前后核 accepted history 未新增或改写；使用既有 Publish/Verify 边界，不增加审计或回滚协议。static/冲突/别名矩阵由源码测试负责，不在包层重复。
2. 将错误模型改为受支持的显式字段，并保留一个真实 Transient 正控；强制重新构建消费者，再验证新建和另一进程冷重开。正控使用新建实验库，不能把旧 H01 的 V1 少字段 history 当作同版合法输入，也不能用负例前留下的 DLL 代替重编译。
3. 检查无 consumer ProjectReference、手工 analyzer/import 或 friend 身份；核新包和实际消费输出身份。两条包路线负责验证新 SG/Build 分发与编译选项传递，不扩成所有形状的交付矩阵。新 G 来源只代表本次有限故事。

代码变更后运行 `dotnet build DurableGraph.slnx` 和相关 Generator/record/history/ImmutableLeaf 测试；涉及包入口时完成上述普通包故事。包与源码资格分列，结果保在本案一次验收记录，不复制进全部活动文档。

结束时压缩本计划为当前合同及完成证据，更新 PROJECT-STATE/路线图的待办状态。DB-094 仍是储备，0093 完成不自动授权其实施。

## 6. 本次收敛与明确不做

本次由需求质疑、最小架构、语义防守三份独立审查，经交叉质询和事件 Probe 取证收敛；没有新增待用户决定的产品语义。

| 裁决 | 计划中的落点 |
|---|---|
| merge / simplify | 显式字段准入只由原循环负责；补漏只返回错误，复用既有 `null/validTypes` 门禁；四个 A–D 阶段收为一个源码片和一次包验收 |
| delete | 删除 C# 诊断去重例外与包层重复 static/冲突矩阵；不派生新分类 IR 或资格登记表 |
| keep | 真实 event target、被忽略语法、诊断抑制下失去 current 资格、合法模型与历史输出隔离；两条路线必须有实际输出见证 |
| defer | 普通 backing 持久化仍属 DB-094；不因本次补漏检查推进 |

不支持普通 property backing 的持久化，不执行 getter/setter，不统一普通/record 的全部存储纪律，不改变生成 API 路由，不扩大 ImmutableLeaf，不新增 runtime 反射 preflight，不修复历史缺值，不设计旧包兼容壳或持久格式迁移器。

前次[共用 Probe](../../../experiments/ExplicitStorageClassificationProbe/README.md)归档构建、14 形状复现及相关回归仍只保原有资格。本次只修订计划并补做 §2.4 的符号/PE 调查；§5 的产品修复、正式回归及新 G 包交付仍全部待实施。
