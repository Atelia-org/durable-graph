# DB-093：核对所有显式存储分类是否有效

> 状态：**方案已选，待实施**。2026-10-15 用户确认本方案，要求本轮形成实施计划；产品实施属于下一轮。
> 源码调研基线：`b209a777c3c103108f95e0277241a127fb1cc7b4`。
> 本案约束 Generator 的显式分类准入，不新增普通属性持久化能力。后继能力只作 [DB-094 前期调研](../0094-backing-field-support/README.md)。

## 1. 问题、范围与最小成功标准

普通 Durable class 的 `[field: DurableField]` 自动属性能编译、提交成功，但 backing field 在字段发现阶段被过滤，实际 Schema 不含该槽，冷恢复得到默认值。这个问题也涉及普通 struct、static backing，以及被 C# 忽略的错误 `field:` target。

本案选择的合同是：**在 Generator 管理的 Durable 声明内，每一处 DG 显式存储分类都必须有明确解释；能够处理就处理，不能处理就报错，不生成遗漏该标注的 current 持久模型。**

“所有显式分类”不等于“所有实例存储必须分类”：普通 class/struct 未标注的自动属性、事件和主构造捕获保持当前准入；显式字段的原分类要求保持。record 保持既有完整分类及事件拒绝策略。

最小可观察成功标准：H01 精确声明在默认与 Family 路径都产生预期的 Generator Error，不生成该错误 current 声明的候选、Capture/Hydrate/current binding；同编译的合法字段模型与 record 正控保持正常。普通包消费者也得到同一目标诊断，修正声明后可正常编译、保存和冷重开。

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

## 3. 已选行为合同

### 3.1 类型与行为边界

适用源声明是 Generator 已管理的 Durable class/struct，含其当前支持的泛型与 partial 形状。既有类型资格、nested/file-local/ref-like 拒绝保持；不新增对整个仓库所有未登记类型的扫描。

普通类型新增核对，record 保现有准入。可复用语义识别和位置处理，不能把两种 policy 合成一个更严格的统一拒绝规则。

| 普通 class/struct 中的形状 | 0093 要求 |
|---|---|
| 当前受支持的显式 Durable 字段 | 沿现有类型、FieldId、Schema 与生成规则处理 |
| 当前受支持的显式 Transient 实例字段 | 排除，不新增槽 |
| 真实 property backing 上的 DurableField | Error：该 storage 当前尚不受支持；提示显式字段写法 |
| 真实 property backing 上的 Transient | 接受其排除语义；不生成 body，不调用 accessor/initializer |
| instance field-like event 上有效的 `field: Transient` | 接受其排除语义；包括一个声明中的多个 event 变量 |
| instance field-like event 上的 DurableField | Error；delegate/event 不因标注获得持久能力 |
| classified static 字段、backing 或 field-like event | 沿 static 分类规则拒绝 |
| 同一真实实例 storage 同时 DurableField/Transient | 沿冲突分类规则拒绝 |
| computed property、custom add/remove event 等没有目标 storage 的分类 | Error，不执行属性或事件方法来补目标 |
| 普通主构造参数上被 C# 忽略的 `field:` 分类 | Error；不能把未附着的标注移给 captured field 或 initializer 字段 |
| 未标记的自动属性、event、主构造捕获 | 保当前准入，不顺带要求补 Transient |
| 访问显式字段的普通包装属性 | 不产生第二份存储；只处理被实际分类的显式字段 |

真实 Transient target 仍须核实。仅凭语法出现 `field:` 不足以接受；反之，仅因 `IFieldSymbol.GetAttributes()` 未消费也不足以拒绝，event 是具体反例。

### 3.2 诊断与失败输出

本计划为普通类型的无效显式分类选择新增 **DG0022 / Error**，建议标题 `Invalid explicit storage classification`。这是待实施合同，当前 Generator 尚无此规则。DG0021 保留 record 的既有职责，避免给普通 class 输出 “Record …” 文案。

对能定位到真实 storage 的普通类型，主诊断顺序为：

1. static 且存在 DG 分类：DG0009，包括 static 双分类；
2. instance 双分类：DG0004；
3. 不支持的 instance Durable implicit storage：DG0022；
4. 真实 instance Transient：排除；
5. 当前受支持的显式 Durable 字段：继续既有 FieldId/类型/重复 ID 规则。

没有真实 target 的显式分类使用 DG0022。C# 已直接报 target Error 的形状不要求堆叠重复消息，但仍须判定该分类无效，不能让声明继续取得 current 生成资格；只有 warning/ignored target 不能视为足够拒绝。record 的既有诊断顺序不在本片重排。

诊断指向 attribute 或属性/event/参数的源位置，支持别名、后缀与全限定名，不依赖短名称文本匹配。继承层各管自己的声明，扫描全部 partial，并排除 nested 声明所有的标注。诊断中不要求用户理解 `<Value>k__BackingField`。

**错误判定须独立于消息最终是否被抑制。** 沿用模型分析的失败返回语义，不为错误 current 声明生成 Schema candidate、Capture/Hydrate 或 current binding；不能因 NoWarn/pragma 而重新生成少字段的成功模型。

该合同不要求所有 generated files 为空：合法同编译类型继续生成；retained history 可以继续生成 historical DTO/body。诊断被抑制且用户不引用生成能力时，普通 CLR 类型甚至仍可 Emit。验收要检查 current 生成资格，不能以 `Emit.Success == false` 代替。

### 3.3 持久模型与包影响

本片不纳入新的持久槽，不修改公共 Attribute 签名、Schema/history/wire 编码、runtime 保存恢复、Family 路由或 ImmutableLeaf 资格。原来静默通过的错误声明将被拒绝，属于明确的构建行为收严。

交付需要新 G 中的 Generator，并重新编译模型项目；六个存储包 S 不因此升版。当前依赖准备继续使用[准确 pin 工作流](../../storage-dependency.md)。

应用若把 H01 改为显式字段而开始保存 Field1，仍是相对旧 accepted V1 的真实新增槽：须正常升版和显式 Upgrade，或在原型授权的实验库上重建。过去未保存的 42 无法由本修复恢复；不重写既有 history 或完成帧。

## 4. 最小重构形状

在字段发现过滤之前或独立于该过滤，观察普通声明的全部 DG 显式分类；检查只服务编译期准入，不增加公开接口或运行时反射审计。

建议复用当前 record 检查中的语义属性解析、标注位置与 partial/nested 归属逻辑，按普通/record policy 分开应用。每个声明只需局部记录哪些标注已经有有效解释：支持的持久字段、真实排除目标或错误。具体私有 helper/集合形状由实施时选择，不建立通用分类框架。

普通属性 backing 上的 Transient 只被验证后排除，不进入 current durableFields，不触发 Family。unsupported Durable 也不能先纳入再退回 BaseOnly 或普通属性访问。

event 需要独立处理 `IEventSymbol` 与实际声明语法；多变量 event 共用 AttributeSyntax 是合法情况。主构造参数、computed property 等语法标注则需核对它是否真正附着，不能靠字段名猜配。

| 位置 | 最小责任 |
|---|---|
| `DurableGraphGenerator.cs` 的 AnalyzeDurableType/GetDirectFields 周边 | 接入显式分类检查，保持错误声明的失败返回；不放开持久字段过滤 |
| `DurableGraphGenerator.Records.cs` 周边 | 复用必要语义工具；保 record 全分类/事件策略，按需抽取小 helper |
| Generator 的诊断定义及发布跟踪 | 登记 DG0022；不重定义 DG0003/0004/0009 或改变既有 record 规则 |
| `tests/DurableGraph.Tests` | 按可观察行为补反例、正控、生成输出与 suppression 断言 |
| `experiments/PackageConsumerProbe` | 增加有界普通包负/正故事；复用既有 feed/config，不新增通用 runner 框架 |
| README/Attribute XML/产品工作集 | 更新实际准入说明；不把 DB-094 写成已支持 |

## 5. 后续实施顺序与验收

### A. 固定失败边界

先把 H01、普通 struct、static backing、无目标标注转成有目标诊断和源码位置的可执行反例。测试可先失败，但不得把当前“SG 无诊断”改写成正确性要求。

同时保留显式字段、record、真实 Transient auto-property/event、未标普通 storage 的正控。多变量 event 和 partial/nested/alias 只各选必要见证，不做全部排列笛卡尔积。

### B. 实现普通类型显式分类核对

接线 DG0022、上述优先级与失败返回，保原字段发现、生成路线和 record 行为。新增规则不能被无关类型错误或 CS8785 代替。

### C. 验证生成输出与历史边界

- 默认与 `DurableGraphGenerateDefinitions=true` 都覆盖 H01；含泛型的普通声明保准确归属。
- 错误类型无 current candidate/Capture/Hydrate/binding；同编译合法模型仍正常。
- suppression 场景检查生成语义；含 retained history 时允许 historical DTO/body，禁止恢复错误 current 资格。
- 有效模型的 Schema/body/history 保持；正常 Error 构建不新增或改写 accepted history。
- DG 属性的别名/全名能识别；别名指向无关 Attribute 不误报。
- 普通 Transient 的 Capture/恢复过程不调用 getter/setter/constructor/initializer；不新增 Schema 槽。用户自行构造输入实例的执行不属于该保证。

### D. 普通包限定资格与交接

使用新 G、准确 S、完整 feed、任务 NuGet.Config 和独立 consumer/cache：

1. 普通包默认及 Family 负例精确命中 DG0022/目标位置，history 不写；static/冲突反例命中其约定规则。
2. 去除错误形状后重新构建消费者，再验证受支持字段/record/Transient 正控的新建、另一进程冷重开；不能让负例留下的旧输出替代正控。
3. 检查无 consumer ProjectReference、手工 analyzer/import 或 friend 身份；核新包和实际消费输出身份。新 G 来源只代表本次有限故事。

代码变更后运行 `dotnet build DurableGraph.slnx` 和相关 Generator/record/history/ImmutableLeaf 测试；涉及包入口时完成上述普通包故事。包与源码资格分列，结果保在本案一次验收记录，不复制进全部活动文档。

结束时压缩本计划为当前合同及完成证据，更新 PROJECT-STATE/路线图的待办状态。DB-094 仍是储备，0093 完成不自动授权其实施。

## 6. 明确不做

不支持普通 property backing 的持久化，不执行 getter/setter，不统一普通/record 的全部存储纪律，不改变生成 API 路由，不扩大 ImmutableLeaf，不新增 runtime 反射 preflight，不修复历史缺值，不设计旧包兼容壳或持久格式迁移器。

本轮只完成该实施计划和[共用 Probe](../../../experiments/ExplicitStorageClassificationProbe/README.md)的源码归档；归档构建、14 形状复现与相关回归结果集中保在 Probe 入口。上述 A–D 尚未实施或取得产品修复资格。
