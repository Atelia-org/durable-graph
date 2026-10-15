# DB-093：核对所有显式存储分类是否有效

> 状态：**已实施并完成限定验收**；2026-10-15。本文保留当前合同、最小实现与证据；实施前计划见 Git `668f540`。
> 实现与候选包来源：`5ae1f180014d955f5de66eb276a07bb9e9296fc1`；原始调查基线为 `b209a777c3c103108f95e0277241a127fb1cc7b4`。
> 本案约束 Generator 的显式分类准入，不新增普通属性持久化能力。后继能力只作 [DB-094 前期调研](../0094-backing-field-support/README.md)。

## 1. 问题与完成边界

原来的普通 Durable class `[field: DurableField]` 自动属性能编译、提交成功，但 backing field 在字段发现阶段被过滤，实际 Schema 不含该槽，冷恢复得到默认值。这个问题也涉及普通 struct、static backing，以及被 C# 忽略的错误 `field:` target；现在这些显式分类须被有效处理或编译拒绝。

本案选择的合同是：**在 Generator 管理的 Durable 声明内，每一处 DG 显式存储分类都必须有明确解释；能够处理就处理，不能处理就报错，不生成遗漏该标注的 current 持久模型。**

“所有显式分类”不等于“所有实例存储必须分类”：普通 class/struct 未标注的自动属性、事件和主构造捕获保持当前准入；显式字段的原分类要求保持。record 保持既有完整分类及事件拒绝策略。

最小实施模型是：**现有字段分析保持为唯一持久字段准入；新增局部补漏检查；错误接入既有 `hasErrors → null → 不进入 validTypes` 流程。** 不新建分类模型或 current 资格登记表。

已达到的最小成功标准：H01 在两个独立编译的默认与 Family 组都报 DG0024，且没有自己的 current 候选、Capture/Hydrate/binding；各组独立合法普通字段模型仍按预期路线生成。record 正控另编译，避免它把默认组整体切入 Family。新 G 普通包得到相同诊断，修正为显式字段后可正常编译、保存和冷重开，结果见 §5。

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

### 2.2 原始缺口与修复入口

- 原始 [Generator](../../../src/DurableGraph.Generator/DurableGraphGenerator.cs) 的 `GetDirectFields` 过滤普通类型的 `IsImplicitlyDeclared` 字段，`AnalyzeDurableType` 只给 record 调用 storage audit。修复保留该过滤，给普通类型接入[局部显式分类核对](../../../src/DurableGraph.Generator/DurableGraphGenerator.StorageClassification.cs)，不扩大持久字段集合。
- [record 检查](../../../src/DurableGraph.Generator/DurableGraphGenerator.Records.cs) 已有语义属性识别和未消费标注核对，但还包括 record 专属的完整存储限制，不能整段套给普通类型。
- [Attribute 定义](../../../src/DurableGraph/DurableFieldAttribute.cs) 允许 Field target；C# 并不能仅靠 AttributeUsage 区分显式字段与 backing field。

### 2.3 共用机制 Probe

[ExplicitStorageClassificationProbe](../../../experiments/ExplicitStorageClassificationProbe/README.md) 从 `.artifacts/h01-options/20261015/RoslynProbe` 整理而来，保留 14 个关键形状。原始基线全部可 Emit，且没有 DG 诊断；其中部分有 C# warning。

关键证据是：自动/C# 14 field-backed/get-only 属性有带 DG 标注的 `IFieldSymbol`；field-like event 的 backing 不作为普通字段 symbol 暴露，但最终 metadata 确实带标注；computed property 和普通主构造参数上的错误 `field:` 仅获 CS0657，不能据捕获字段存在就认定标注已附着。

Probe 分列输入/生成后 C# 诊断、Generator 诊断、Emit 和 PE metadata。它是机制观察工具；其成功退出不代表 Generator 已正确消费标注，不把“SG 无诊断”冻结为永久产品回归。原始基线与迁移后的运行说明只维护在该入口。

### 2.4 本次补充核实：field-like 语法不保证有 event backing

2026-10-15 在修复前的上述 Probe 隔离副本中追加 9 个 fixture，使用 C# 14 / Roslyn 5.3.0 观察。原 14 项加新 9 项均可 source Emit，原 SG 均无 Error；这份调查本身不代表产品准入正确。

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

因此，事件核对需要语义 DG 标注、`field:` target 和真实 storage 的声明条件；既不能只看 `IFieldSymbol`，也不能把 `EventFieldDeclarationSyntax` 或 `!IsAbstract && !IsExtern` 当作充分条件。实现不需 Emit/反射或调用 C# 全量诊断来探测 backing。原始观察位于忽略的 `.artifacts/db093-dialectic/`；这些反例与真实 backing 正控已进入[正式回归](../../../tests/DurableGraph.Tests/ExplicitStorageClassificationGeneratorTests.cs)。

## 3. 已选行为合同

### 3.1 类型与行为边界

适用源声明是 Generator 已管理的 Durable class/struct，含其当前支持的泛型与 partial 形状。既有类型资格、nested/file-local/ref-like 拒绝保持；不新增对整个仓库所有未登记类型的扫描。

普通类型新增核对，record 保现有准入。可复用语义识别和位置处理，不能把两种 policy 合成一个更严格的统一拒绝规则。

| 普通 class/struct 中的形状 | 当前行为 |
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

普通类型的无效显式分类使用 **DG0024 / Error**，标题为 `Invalid explicit storage classification`。DG0021 保留 record 检查；[DG0022](../../../src/DurableGraph.Generator/DurableGraphGenerator.cs) 保留 Definition option 检查，[DG0023](../../../src/DurableGraph.Generator/DurableGraphGenerator.SchemaExports.cs) 保留跨程序集 Schema 材料检查，不重用已有编号。

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

消费者须采用含本修复的新 G 并重新编译模型项目；六个存储包 S 不因此升版。当前依赖准备继续使用[准确 pin 工作流](../../storage-dependency.md)。

应用若把 H01 改为显式字段而开始保存 Field1，仍是相对旧 accepted V1 的真实新增槽：须正常升版和显式 Upgrade，或在原型授权的实验库上重建。过去未保存的 42 无法由本修复恢复；不重写既有 history 或完成帧。

## 4. 已实施的最小结构

保留 `GetDirectFields` 和现有字段循环。[ReportExplicitStorageClassificationErrors](../../../src/DurableGraph.Generator/DurableGraphGenerator.StorageClassification.cs) 返回错误布尔值并接入既有失败出口；不向 emitter 提供新的分类 IR。复用 record 的语义属性解析、源位置与 partial/nested 归属工具，record 的完整存储 policy 仍单独应用。

补漏检查按以下三项闭合，不另做一遍完整字段分析：

1. 显式字段上的 DG 标注交给原循环，登记为已解释；原循环接受或拒绝都算已解释，避免再报 DG0024。FieldId、字段类型、重复 ID 仍只有原循环负责。
2. 对原过滤排除的 implicit 字段及 source 不暴露为字段的 event，核实实际分类和 target，按 §3.2 的 static/双分类/不支持 Durable/真实 Transient 顺序处理。事件只确认已知具有 backing 的普通 field-like 声明，排除 abstract、extern 与 partial/custom 等无 backing 情况；不实现事件配对框架。多变量声明的共享标注按整条声明解释，不能用 AttributeSyntax 充当 storage 的唯一身份。
3. 扫描本类型各 partial 声明剩余的 DG 标注；不能被真实 target 或既有处理解释的，报 DG0024。主构造参数不能靠 captured field 名称猜配，nested 的标注不能归给外层。

`classified` 局部集合只记录已有解释的 syntax/span，避免遗漏和重复报告，不成为新的模型或跨类型登记。普通 Transient backing 不进入 `durableFields`，不触发 Family；unsupported Durable 也不能先纳入再退回 BaseOnly 或属性访问。检查只服务编译期准入，不增加公开接口、运行时反射或持久状态。

| 位置 | 实现责任 |
|---|---|
| `DurableGraphGenerator.cs` / `StorageClassification.cs` | 接入局部检查和失败返回；不放开字段过滤或改变路由 |
| `DurableGraphGenerator.Records.cs` | 只抽取属性符号类型解析 helper，保 record policy |
| Generator 诊断与发布跟踪 | 定义及登记 DG0024，保已有编号 |
| [源码测试](../../../tests/DurableGraph.Tests/ExplicitStorageClassificationGeneratorTests.cs) | 精确诊断、归属、输出、抑制与真实 Transient 运行反例；harness 仅加可选诊断配置 |
| [普通包 consumer / runner](../../../experiments/PackageConsumerProbe/ExplicitStorageClassificationConsumer/README.md) | 两条路线的负例与修正后冷恢复；复用既有 feed/config |
| README / Attribute XML / 产品工作集 | 说明实际准入，不把 DB-094 写成已支持 |

## 5. 2026-10-15 验收证据与复跑

### 5.1 源码与机制

使用 SDK 10.0.201、准确六包 S 的 package mode、完整任务 NuGet.Config；最终验证如下：

| 检查 | 结果与范围 |
|---|---|
| 根 solution Release Rebuild | **0 warnings / 0 errors** |
| 根 solution 全量测试 | **2,792 passed，0 failed/skipped**；Persistence 921、Serialization 133、Storage 36、Runtime/Generator/Build 1,702 |
| 本片新增回归 | 上述全套包含 **51 项通过**：两种真实 emitter、class/struct backing、无 target、static/冲突、合法排除、alias/partial/generic/继承/nested、suppression/history 与 Capture/Delta/Hydrate |
| 诊断抑制边界 | 错误 CLR 类型仍可 Emit，但无 current 候选/投影；保留历史 Family 的 domain/current factory 为 null，historical factory 存在，合法独立模型正常 |
| 真实 Transient | 不新增槽、不触发 Family、不执行 accessor；只改 Transient 不产生 Delta；构造器/初始化器合同由既有回归保持 |
| 存储输入 | 19 份实际输出 DLL 与准确六包 feed 的对应 DLL SHA256 一致 |
| 共用机制 Probe | 原 14 形状与 record 正控通过；修复后的观察方法与结果只维护在 [Probe 入口](../../../experiments/ExplicitStorageClassificationProbe/README.md)；另复跑 23 项调查，15 项 DG0024、2 项 DG0009、6 项保持合法 |

无关 alias 正控使用 `@DurableField` 消除 C# 后缀歧义；未转义的歧义另有 **CS1614 + DG0024** 负例，不通过放宽语义识别获得“通过”。Transient 的保证只针对生成 Capture/恢复，不包括用户构造输入实例或显式 Upgrade。

复跑先按[依赖指南](../../storage-dependency.md)准备 package mode，再执行根 build/test。只查本片可对 `tests/DurableGraph.Tests/DurableGraph.Tests.csproj` 使用 `--filter FullyQualifiedName~ExplicitStorageClassification`。首次输入中发现的两个 fixture 错误已修正，完成证据取最终 Rebuild 与全套 TRX。

### 5.2 普通包限定资格

从干净的实现提交制作本地候选 **G=`0.0.0-db093.20261015.g5ae1f18`**；六包 **S=`0.1.3-dev.20261014.g3de8fb0`**，来源 `3de8fb050d81d35ee1c36d1b978024cbc42cb455`。四包 G 与六包 S 构成十包 feed，未远程发布。

[普通包 runner](../../../experiments/PackageConsumerProbe/Run-ExplicitStorageClassificationProbe.ps1)在仓外新目录、独立 cache、两个 project/history/database lane 中实跑通过：

| 路线 | 负例 | 修正后与冷进程 |
|---|---|---|
| 默认 | 唯一 Error DG0024，`Model.Invalid.cs:7:13`；合法控制生成 `DurableStates.g.cs`，错误类型无 current 输出/history | 显式字段加真实 Transient；Publish 两份 V1 history，Verify 保字节；保存 42/7，另一进程只读恢复 42/7、Transient=0 |
| 显式 Family | 同一精确诊断与位置；合法控制生成 `DurableGenericStates.g.cs`，错误类型无 current 输出/history | 同上；没有旧 DLL 代替修正后的强制重编译 |

两个 lane 各为负例 exit 1、修正 Publish/Verify exit 0；每个 lane 的写/读使用两个不同进程。Capture 和 Hydrate 不调用 Transient accessor；读进程自己验证 Scratch 才调用一次 getter。只读前后数据库及 accepted history 文件集、字节保持。

consumer 无 ProjectReference、friend 或手工 analyzer/import。已核十包版本/commit、实际 compiler 的 packaged Generator 与 Build CLI、缓存 SG/tool/props/targets、输出及四个进程实际加载的十份 DLL 字节。包阶段不重复全部源码形状矩阵。

复跑参数与方法见 [consumer README](../../../experiments/PackageConsumerProbe/ExplicitStorageClassificationConsumer/README.md)。忽略目录 `.artifacts/db093-implementation/20261015/` 保存最终 build/TRX、源码汇总、机制 JSON、干净打包来源、十包 feed 及包结果副本；仓外原始收据保留六次 build、负例冻结输出与四个进程证据。不将 source 测试或本候选的有限故事外推为旧库迁移、故障或性能资格。

## 6. 本次收敛与明确不做

本次由需求质疑、最小架构、语义防守三份独立审查，经交叉质询和事件 Probe 取证收敛；没有新增待用户决定的产品语义。

| 裁决 | 计划中的落点 |
|---|---|
| merge / simplify | 显式字段准入只由原循环负责；补漏只返回错误，复用既有 `null/validTypes` 门禁；四个 A–D 阶段收为一个源码片和一次包验收 |
| delete | 删除 C# 诊断去重例外与包层重复 static/冲突矩阵；不派生新分类 IR 或资格登记表 |
| keep | 真实 event target、被忽略语法、诊断抑制下失去 current 资格、合法模型与历史输出隔离；两条路线必须有实际输出见证 |
| defer | 普通 backing 持久化仍属 DB-094；不因本次补漏检查推进 |

不支持普通 property backing 的持久化，不执行 getter/setter，不统一普通/record 的全部存储纪律，不改变生成 API 路由，不扩大 ImmutableLeaf，不新增 runtime 反射 preflight，不修复历史缺值，不设计旧包兼容壳或持久格式迁移器。

本片没有剩余必经实施步骤。后续状态从[产品工作集](../../../src/PROJECT-STATE.md)进入；DB-094 仍只按[路线图](../../DurableGraph-research-roadmap.md)的真实消费者触发条件重访，不由本片完成自动开启。
