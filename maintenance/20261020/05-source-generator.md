# Source Generator 质量体检

审阅日期：2026-10-20。源码基线：`1beca7cf2477d369010b3577b7a795c41dbc45f1`。读者为接续调查和修复的 Coding Agent。

## 结论与边界

本包发现两个值得优先处理的生成器缺陷，以及一个较低优先级的诊断合同缺口。都发生在编译/生成阶段；本次没有发现可以据此断言的持久数据损坏或恢复合同破坏。产品代码、公开 API、history/wire 格式均未修改。

| 编号 | 优先级 | 分类 | 触发与影响 |
|---|---|---|---|
| SG-01 | P2 | 生成代码正确性缺陷 | 领域泛型参数与固定生成参数同名，合法模型产生 C# 约束错误 |
| SG-02 | P2 | 无效输入处理缺陷 | Family 缺 history 时按声明版本逐一报错，无快速退出和循环取消检查；大版本号可耗尽构建资源 |
| SG-03 | P3 | 诊断合同缺口 | 普通 class/struct 的 Family helper 碰撞未在生成前检查，最终由生成文件中的 C# 错误拒绝 |

范围：`src/DurableGraph.Generator` 的入口、符号分类、显式 storage 分类、Family/传统路由、继承与 inline 的布局生成、跨程序集导入导出、命名及对应 Roslyn 测试。阅读根 AGENTS 和完整 `src/PROJECT-STATE.md` 后，按代码接缝查看 runtime 定义合同。

未覆盖：未穷举 body codec 的每种标量/容器组合；未重新做普通 NuGet 包资格或 Build 工具发布审核；未做 IDE 性能基准、进程强杀或掉电实验。跨程序集业务规则自动发现、新 CLR 类型支持、持久格式扩张均不在建议范围。没有因为 partial 文件较多或单文件较大，就推导其违反 SOLID。

## SG-01：生成泛型参数遮蔽领域类型参数

**触发输入**（当前支持的顶层 partial 泛型 class）：

```csharp
using Atelia.DurableGraph;
[DurableType("audit.shadow", 1)]
public partial class Box<TState0> : IDurableObject where TState0 : class {
    [DurableField(1)] public TState0 Value = default!;
}
```

**事实链**：

- [GenericFactories.cs](../../src/DurableGraph.Generator/DurableGraphGenerator.GenericFactories.cs#L170) `DomainParameters` 保留源参数的原名；没有把 `TState0`、`TOps0`、`TProjection0` 纳入拒绝条件。
- [GenericProjection.cs](../../src/DurableGraph.Generator/DurableGraphGenerator.GenericProjection.cs#L39) `CurrentExecutionParameters` 固定产生 `TState0, TOps0, TProjection0` 等名字；`AppendCurrentExecutionConstraints` 为状态参数增加 `unmanaged` 约束。
- 同文件 [L17](../../src/DurableGraph.Generator/DurableGraphGenerator.GenericProjection.cs#L17) 的领域槽类型和 owner 使用 Roslyn 原始符号显示名；[L239](../../src/DurableGraph.Generator/DurableGraphGenerator.GenericProjection.cs#L239) 的 `__DurableCreateTyped` 又在方法作用域声明这些参数。

因此生成方法内的 `Box<TState0>` 指向方法的状态参数，而非外层领域参数。源模型要求它为 `class`，生成方法却要求它为 `unmanaged`，造成约束冲突。主线程执行最小反例，实际观察到 `DurableGenericStates.g.cs` 第 106–125 行共 **10 条 CS0452**；生成器没有 DG error。即使去掉 `class` 约束，领域字段 accessor 与方法里的领域实参也可能绑定到不同参数；后一点仍是待扩展验证的推导，不要只消除 CS0693 遮蔽警告就认为修好了。

**建议的最小方案**：仅在领域 partial 内，为生成状态/ops/projection 参数建立避开 `type.Symbol.TypeParameters` 的确定性名字映射，并让该段输出的所有参数引用共用映射。Family DTO/body 宿主本身没有领域参数作用域，可保留现有对外 DTO 名字与 arity。不要重命名用户参数，也不要新增“不能叫 TState0”的模型限制。

备选是给生成参数加更长前缀；固定前缀仍存在被合法用户名字撞到的可能，应继续做实际避让。完整语法树后处理/重建整个生成器不是本问题所需。

**修复验收**：按 `TState0`、`TOps0`、`TProjection0` 三种领域参数名字，测试 class、inline struct、受约束参数及真实 Capture→冷恢复。源端 `where TState0 : class` 与状态端 `unmanaged` 必须仍各指向自己的参数。生成参数改名不能改变 history、DTO 槽布局、静态 body 字节或 Family arity。

## SG-02：Family 缺版诊断数被版本号控制

**触发输入**：`[DurableType("audit.history", 32)] public partial class Box<T> : IDurableObject { }`，不提供任何 `.dgschema`。

**事实链**：

- [TemplateHistory.cs](../../src/DurableGraph.Generator/DurableGraphGenerator.TemplateHistory.cs#L213) `ValidateFamilyTemplateHistory` 从 1 遍历到 `type.Version - 1`，每个缺版创建一个 DG0014；设置 `valid=false` 后仍继续，没有循环内取消检查。
- [Ancestry.cs](../../src/DurableGraph.Generator/DurableGraphGenerator.Ancestry.cs#L266) 的传统路线遇到首个缺版就 `break`，两条路线的错误成本不一致。
- 当前元数据准入只要求版本为正数，没有一个足以约束该循环成本的版本上限。泛型声明自动选择 Family，空泛型模型也能触发。

Version=32 是有限、安全的观测点，主线程实测产生 **31 个 DG0014**，无 CS8785 生成器异常。由循环可以推导，`int.MaxValue` 在没有 history 的情况下会试图生成约 21 亿条诊断；**本次不执行此极端输入**，不声称测得其耗时或峰值内存。用户误把年份/大整数放入版本也会无收益地放大 IDE/构建开销。

**建议的最小方案**：首个缺版报告 DG0014 后退出该类型的缺版检查，与传统路线对齐；循环或主要工作边界检查 `context.CancellationToken`。如要同时列出所有缺口，至少先以真实 history 集合计算、限制输出数量；不必为此增加任意公开版本上限。

**修复验收**：Version=32/10000、空 history 应以有界诊断数拒绝，不生成可用 current 模型；有完整历史的正常链保持；取消请求可在工作边界退出。避免用计时型断言证明算法复杂度，先断言诊断数和退出结果，再做独立工具响应性验证。

## SG-03：Family helper 碰撞检查被 record 条件误限

**触发输入**：

```csharp
using Atelia.DurableGraph;
[DurableType("audit.helper", 1)]
public partial class Model<T> : IDurableObject {
    public static int __DurableCapture => 0;
}
```

**事实链**：

- [DurableGraphGenerator.cs](../../src/DurableGraph.Generator/DurableGraphGenerator.cs#L798) 仅在 `type.IsRecord` 时调用 `ReportRecordHelperCollisions`。
- [Records.cs](../../src/DurableGraph.Generator/DurableGraphGenerator.Records.cs#L87) 的检查本身覆盖 `__DurableCapture`、`__DurableHydrate`、`__DurableCreateCurrent`、`__DurableCreateTyped`、`__DurableProjection` 和逐字段 accessor。
- [GenericFactories.cs](../../src/DurableGraph.Generator/DurableGraphGenerator.GenericFactories.cs#L62) 对所有有效类型调用 `AppendFamilyDomainProjection`；[GenericProjection.cs](../../src/DurableGraph.Generator/DurableGraphGenerator.GenericProjection.cs#L89) 给普通 class 也发出 `__DurableCapture`。
- 现有 [RecordStructGeneratorTests.cs](../../tests/DurableGraph.Tests/RecordStructGeneratorTests.cs#L17) 测了 record accessor 碰撞，[同文件 L62](../../tests/DurableGraph.Tests/RecordStructGeneratorTests.cs#L62) 测了 record 类型名碰撞；[SchemaOnlyGeneratorTests.cs](../../tests/DurableGraph.Tests/SchemaOnlyGeneratorTests.cs#L119) 测的是另一组传统 Schema 名字。它们不能证明普通 Family 模型具备同样检查。

结果是用户源代码单独合法、生成器也不报 DG 错误，却生成同名属性/方法。主线程实测在 `DurableGenericStates.g.cs` 第 86 行出现 **1 条 CS0102**。这里仍会编译失败，**没有证据表明会悄悄交付错误模型**；优先级低于 SG-01。它也反映一处职责放置偏差：检查属于实际生成路线，而不是“是否为 record”。

**建议的最小方案**：确定 Family 路线后，对所有会生成领域 projection 的非 enum 类型执行共用 helper 名字检查，生成资格由检查结果决定。保留现有 record 对应诊断，传统路线只检查它实际使用的名字；不要把 Family 全部名字强加给仍用传统路线的普通模型。

修复时同步考虑 `__DurableCheckImmutableLeafShape` 等按候选条件发出的 helper，不要仅复制一份列表。建议让实际输出与保留名清单共用小型局部描述/常量，避免为了 SOLID 再制造通用命名框架。

**修复验收**：普通 class/struct、record class/struct、泛型自动 Family、强制 Family、类型名撞 helper、逐字段 read/write/readonly accessor；要求精确 DG0020 且拒绝 current 输出。保留无碰撞正控及传统路线“未使用的 Family 名字仍可用”的正控。

## 已有的有效边界与有据保留

- **显式分类没有仅靠诊断严重性维护资格。** [AnalyzeDurableType](../../src/DurableGraph.Generator/DurableGraphGenerator.cs#L694) 汇总 storage 错误，并在 [L800](../../src/DurableGraph.Generator/DurableGraphGenerator.cs#L800) 返回 null；[抑制回归](../../tests/DurableGraph.Tests/ExplicitStorageClassificationGeneratorTests.cs#L207) 实际验证抑制 DG0024 后仍不获得 current 模型，同时保留历史 reader。这是应保留的分界，不能用“抑制诊断即同意生成”简化。
- **跨程序集 nominal 与 exact history 有独立准入。** [CrossAssembly.cs](../../src/DurableGraph.Generator/DurableGraphGenerator.CrossAssembly.cs#L27) 检查 DurableType 的实际符号及运行时程序集；[SchemaExports.cs](../../src/DurableGraph.Generator/DurableGraphGenerator.SchemaExports.cs#L97) 拒绝 foreign 依赖借 owned 同 ID 补齐，[L109](../../src/DurableGraph.Generator/DurableGraphGenerator.SchemaExports.cs#L109) 拒绝多 owner，[L183](../../src/DurableGraph.Generator/DurableGraphGenerator.SchemaExports.cs#L183) 核对执行 helper。未把“有 attribute”误当成执行能力。
- **跨库 inline/base 资格已有针对性反例。** [inline 导入测试](../../tests/DurableGraph.Tests/CrossAssemblyInlineGeneratorTests.cs#L101) 覆盖错误合同/缺 helper，[继承测试](../../tests/DurableGraph.Tests/CrossAssemblyInheritanceGeneratorTests.cs#L212) 覆盖更强约束/ref-like helper；[同库与拆库同布局测试](../../tests/DurableGraph.Tests/CrossAssemblyInheritanceGeneratorTests.cs#L95) 覆盖 history/DTO/body 一致性。静态抽样未出现必须推倒重做的证据。
- **保留两条输出路线有实际消费者合同。** [路由入口](../../src/DurableGraph.Generator/DurableGraphGenerator.cs#L263) 与 [UsesFamilyTemplates](../../src/DurableGraph.Generator/DurableGraphGenerator.TemplateHistory.cs#L182) 保持传统非泛型 API 与 Family 的条件选择。SG-02/03 显示的是局部验证漂移，不足以授权立即删除传统路线、整体迁移消费者或新增程序集。

维护上的直接收益点是把“共用验证合同”放在它实际服务的生成路径附近；先修上述可复现分歧，再以真实编译成本决定是否做更大整理。泛型、enum、record、历史与跨库是正交组合，后续新增边界测试应优先覆盖它们和名字/错误输入的交叉点，而不是只按特性文件各补一个正控。

## 验证、交接与改动

本审阅包不运行 dotnet，以免与主线程及其他审阅包争用共享 bin/obj。主线程统一运行根构建和回归。

已保留独立观测源码：[GeneratorProbe.cs](probes/GeneratorProbe.cs)。它是 `DurableSchemaGeneratorTests` 的 partial，复用已有 Roslyn `RunGenerator`；主线程临时复制进测试工程后运行，结束后移除测试目录中的副本。以下命令使用本次主线程已经 restore 的 Release package 模式。新环境必须先按当前依赖指南准备准确完整 feed 与对应 NuGet.Config，并以相同模式 restore，不能直接沿用不明来源 assets：

```powershell
Copy-Item maintenance/20261020/probes/GeneratorProbe.cs tests/DurableGraph.Tests/Maintenance20261020GeneratorProbe.cs
dotnet test tests/DurableGraph.Tests/DurableGraph.Tests.csproj -c Release --no-restore -p:UseStorageSources=false --filter "FullyQualifiedName~Maintenance20261020" --logger "console;verbosity=detailed"
Remove-Item -LiteralPath tests/DurableGraph.Tests/Maintenance20261020GeneratorProbe.cs
```

这三个测试**断言审阅基线的问题行为**：SG-01 预期 CS0452；SG-02 预期 31 条 DG0014；SG-03 预期没有 DG 错误但有 CS0102。它们不是可永久提交的修复后回归；修复时必须反转为目标合同，并加入上文的必要正控。

执行状态：静态源码与既有测试交叉核查完成，主线程以 Release package 模式实测三项观测测试，**3/3 通过**。这里的“通过”表示测试成功观察到基线缺陷，**不表示 SG-01/SG-03 的领域模型生成成功**：它们实际分别遭遇 CS0452/CS0102；SG-02 实际被 DG0014 拒绝。主线程结果回传后，本报告作者也读取了本地日志，核对了上述错误条数及诊断内容。

本轮原始记录为 [generator-probe.log](../../.artifacts/maintenance-20261020/generator-probe.log) 和 [TRX](../../.artifacts/maintenance-20261020/generator-probe-results/gdtutu_TUTU17_2026-10-20_13_36_05.trx)。这些 `.artifacts` 文件是 ignored 本机记录，未来 checkout 未必存在；持久复现源码与关键观测结果已经保留在本目录和本文。未执行极端版本输入、存档读写或修复后正向资格，不将此结果扩展为持久格式/存档漏洞或完整包交付验收。

本包写入路径为本报告、`maintenance/20261020/probes/GeneratorProbe.cs` 及其 ignored `.artifacts/maintenance-20261020/GeneratorProbe.cs` 副本。没有直接实施上述修复：生成名字/诊断资格均触及生成合同，不属于可以顺手修改的文档格式或代码风格问题。
