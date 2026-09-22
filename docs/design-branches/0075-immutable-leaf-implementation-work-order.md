# DB-075 施工工单：ImmutableLeaf 完整证明与 Family 路径重构

> 状态：**Implemented / 复核修正后验收**（2026-09-22 计划审阅通过并开工；2026-09-23 实施验收，同日用户专项复核后修正三处施工瑕疵并复验）。
> 权威设计：[DB-075 主文档](0075-immutable-leaf-proof-and-family-refactor.md)。本工单只分解施工、记录审阅约束与验收；设计论证以主文档为准，不重复。
> 来源：2026-09-22 主线程分解方案 + 用户审阅追加六项施工约束。
> 完成判定：主文档 §7 验收矩阵通过、solution Rebuild 与相关测试通过、真包探针通过，且主线程完成集成审查之后，才允许把主文档状态改为 Implemented。

## 1. 施工约束（用户裁定，2026-09-22）

- **C1 阶段状态与命名**：阶段 1 重构期间旧基线保持通过；阶段 0 的预期红测持续存在。分类返回材料统一叫 **Candidate**（`TryBuildImmutableLeafCandidate` / `ImmutableLeafCandidate`），避免与"最终已验证资格"混淆。`candidate → 常量 true` 只是主线程内部过渡检查点，不得作为交付状态或完成修复的声明。
- **C2 主动替换旧 Family 验收预期**：`FamilyPathModelsStaysConservativeWithoutImmutableLeafClaim` 等"Family 全 false / 不出现 isImmutableLeaf 文本"的断言必须改写为实际 binding 断言（符合候选者 true、不符合者 false）；不得为了保留旧测试维持错误的路由限制。
- **C3 sibling 反例覆盖两条路径并配正向对照**：同一原始模型在二进制路径与强制 Family（`DurableGraphGenerateDefinitions=true`）下都应先得 true，再加入 sibling 生成器输出后得 false。测试基架须同时支持附加生成器与 forceDefinitions；sibling 必须与 DG 同轮运行（同一 GeneratorDriver pass），禁止先把 sibling 输出并入输入再启动 DG。
- **C4 区分编译机制见证与完整产品验收**：record backing 翻转、类型遮蔽等独立见证证明简化方案不成立，但不能替代实际生成 binding 的测试。若某组合被既有生成代码或编译器拒绝，必须准确记录失败阶段；不得把编译失败记成"运行时 guard 返回 false"，也不得为强行跑通探针扩张序列化支持。
- **C5 真包探针观测方式**：`IsImmutableLeaf` 是 internal，独立消费者不能直接访问；探针侧用反射读取实际 binding 属性。不新增公共查询 API 或产品 IVT。保存/重开与 flag 断言都要执行，单有往返成功不能证明分类已接通；复用现有包准备与隔离消费脚本。
- **C6 施工单元不等于委派数量**：主线程先固定候选材料与共同发射接口（独占生成器核心文件与测试基架），再委派边界清楚的独立测试、包探针任务；避免多代理同时修改生成器核心。dotnet 验证串行执行；主线程检查集成 diff 和实际结果后，才能将文档标为 Implemented。

通用红线（重申主文档 §7/§8）：最终结构核对必须包含**字段类型身份与基类核对**，不得退化为数量检查；不新增 Analyzer、Build 工具 PE 审核、运行时第二套分类器、全局缓存或 DB-073 机制。

## 2. 事实基线（2026-09-22 源码核对）

- 二进制发射：`DurableSchemaGenerator.GeneratedState.cs` 在 `GenerateStates` 内调用 `IsImmutableLeafModel(type, types, halfType)`；`AppendBinaryStateModel`（`DurableSchemaGenerator.StateModel.cs`）发射常量 `isImmutableLeaf: true/false`。
- Family 发射：`DurableSchemaGenerator.GenericProjection.cs` 的 `__DurableCreateTyped` 构造 `StateModelBinding` 时不传 `isImmutableLeaf`（默认 false）；`GenerateGenericStates`（`DurableSchemaGenerator.GenericFactories.cs`）持有 `compilation`，可按主分流同一方式计算真实 corelib `halfType`。
- 编译路由：`DurableSchemaGenerator.cs` `GenerateSchemas` 中 `references / forceDefinitions / 升级注册 / UsesGenericTemplates` 触发 Family；路由本身不改。
- 分类规则现状：`AuditInstanceFields` 允许 record 隐式 backing（`field.IsImplicitlyDeclared && !symbol.IsRecord` 才拒绝）；本片收紧为 record 隐式 backing 一律非候选，主构造捕获仅在物化为隐式实例字段时排除（Roslyn 5.3.0 无 `INamedTypeSymbol.PrimaryConstructor` API；初版误用声明语法 ParameterList 一律排除，2026-09-23 复核后删除，见 §7）。
- 测试基架：`RunGenerator` 只挂 DG 单生成器、无 forceDefinitions；`CrossAssemblyGeneratorTestSupport.cs` 已有 `CrossAssemblyOptions` analyzer-config 模式可复用。
- TypeTag 映射（受信 Type 表输入）：1=bool、2=int、3=long、4=string、5=byte、6=sbyte、7=short、8=ushort、9=uint、10=ulong、11=char、12=Half、13=float、14=double、19=Guid、20=decimal、21=TimeSpan、22=DateOnly、23=TimeOnly、24=DateTimeOffset。关键字表达式用于 1/2/3/5–11/13/14/20；12/19/21–24 用 `typeof(object).Assembly.GetType(...)` corelib 锚定；4/15/17 不进候选。

## 3. 主线程核心序列（串行，独占核心文件）

| 任务 | 内容 | 验收 |
|---|---|---|
| M1 | `RunGenerator` 增加附加生成器 + `forceDefinitions` 重载（复用 `CrossAssemblyOptions`） | 现有测试不受影响 |
| M2 | 新文件 `ImmutableLeafSiblingTests.cs` 固化红测：双生成器反例（二进制/强制 Family 各配正向对照）、sibling 在自身/祖先/inline 增字段、新增基类、本地与 `System` 命名空间类型遮蔽、空类加状态、静态成员不失去资格 | 新测试按预期失败（红），失败原因正确 |
| M3 | `TryBuildImmutableLeafCandidate` 返回闭包材料（每个声明层：exact base、审计字段 MetadataName/受信类型/IsInitOnly）；规则收紧（record 隐式 backing false、隐式实例状态经字段审计与最终核对拒绝、闭包声明同编译非泛型、闭合泛型实例不进） | 旧基线保持绿；此状态不交付 |
| M4 | 结构核对 helper 发射器：候选根聚合 `__DurableCheckImmutableLeafShape()`，按声明去重；基类核对 + 完整实例字段数 + 逐字段 MetadataName/FieldType/IsInitOnly；受信 Type 映射（关键字/corelib 锚定/Nullable 定义+唯一实参/同编译全名）；空类也走 helper | 发射文本含类型身份与基类核对 |
| M5 | 二进制接缝：候选改发 `isImmutableLeaf: __DurableCheckImmutableLeafShape()`，非候选仍常量 false | M2 红测中二进制项转绿 |
| M6 | Family 接缝：`GenerateGenericStates` 计算 candidate 并传入 `AppendGenericDomainProjection` → `AppendGenericCurrentFactory`/`__DurableCreateTyped`；helper 发射进域 partial 类；inline 不建对象 binding | M2 全部红测转绿；Family 旧测试按 M8 改写后通过 |
| M7 | `StateModelBinding.cs` XML 责任合同澄清（区分生成绑定核对与手写调用者自担） | 不改签名/默认值/存储；`ImmutableLeafBindingTests` 不变通过 |
| M8 | 既有测试预期替换：`AssertLeafClassification` 改实际 binding 断言；继承链两测试改 Host 双模型断言；`FamilyPathModels...` 改为符合候选者 true | 全部 DurableGraph.Tests 通过 |
| M9 | `dotnet build DurableGraph.slnx` + ImmutableLeaf 焦点 + 全 `DurableGraph.Tests` | 0 警告 0 错误 |

M3–M6 构成一个垂直分片：红测转绿之前不得把候选 true 暴露给任何 DB-073 消费路径。

## 4. 委派包（M9 通过后并行；写集不相交）

### D1 验收矩阵补全

- Owned files：`tests/DurableGraph.Tests/ImmutableLeafClassificationTests.cs`、`ImmutableLeafSiblingTests.cs`（或新增矩阵文件）。只改测试。
- 输入：DB-075 §7 验收表、M1 基架、已落地的候选/helper 行为。
- 内容：Family 正例逐形状独立测试（enum、Nullable builtin/inline/enum、递归 inline、显式 readonly record；不能用一个组合测试替代）；同编译无关 List 模型混合下 leaf true + 容器 owner false；`DurableGraphGenerateDefinitions=true` 标量 true；负例矩阵（positional/get-only/init backing、主构造捕获、event、Transient、mutable/string/ref、mutable base/inline、外部/泛型 root-base-inline、immutable base + mutable derived 分开）；容器/泛型/跨程序集的"无文本"断言升级为实际 binding false。
- 非目标：不改生成器/运行时源码；发现产品缺口报告主线程，不自行扩张。

### D2 真包探针

- Owned files：`experiments/PackageConsumerProbe/ImmutableLeafConsumer/`、`Run-ImmutableLeafProbe.ps1`、该目录 README 段落。
- 输入：`PackageProbeSupport.ps1` 包准备模式、README 接入方式、DB-075 §7 包消费者行。
- 内容：两个真实 PackageReference 消费者构建——A：`DurableGraphGenerateDefinitions=true` 的非泛型 readonly 叶，断言 flag true 并完成保存/重开；B：同一模型加 sibling source generator（本地 analyzer 引用），断言不安全候选 false。flag 经反射读取 internal 属性。
- 非目标：不修改包结构、build targets、运行时公共面；不新增 IVT。

## 5. 集成与完成

1. dotnet 验证全部串行；主线程审查每个委派包的 diff 并在集成树重跑相关检查。
2. 收尾文档：DB-075 主文档状态 → Implemented（附验证证据）；DB-072 §3.1/能力结论更新（Family 非泛型叶可达 true、双生成器缺口已由最终结构核对关闭）；design-branches README 索引行；`src/PROJECT-STATE.md` 当前焦点替换；路线图 DB-073 前置完成；记录重编译边界（旧 DB-072 已编译消费者内嵌常量 true，只升 runtime 不修复，DB-073 首次使用 capability 前须重编译模型程序集）。
3. 任何超出本工单的扩张（新 analyzer、第二套分类器、缓存、格式变更）回到主文档补证，不在施工中自动增设。

## 6. 状态跟踪

| 任务 | 状态 |
|---|---|
| M1 基架扩展 | Done（RunGenerator 附加生成器 + forceDefinitions 重载） |
| M2 红测固化 | Done（ImmutableLeafSiblingTests；红测全部转绿） |
| M3 Candidate 材料 | Done（TryBuildImmutableLeafCandidate；record 隐式 backing 收紧；主构造按实例状态审计，不做语法级排除——见 §7 复核修正） |
| M4 结构核对发射器 | Done（聚合 helper：基类核对 + 完整字段数 + 逐字段 MetadataName/受信 Type/IsInitOnly） |
| M5 二进制接缝 | Done（候选发 helper 调用，非候选仍常量 false） |
| M6 Family 接缝 | Done（GenerateGenericStates 计算 candidate → 域 partial helper → __DurableCreateTyped 传参） |
| M7 XML 合同澄清 | Done（IsImmutableLeaf 备注：生成绑定核对 vs 手写调用者自担） |
| M8 既有测试替换 | Done（AssertLeafClassification/继承链/FamilyPathQualified 改 binding 断言；GeneratedBodyBindingAssertions 允许 shape-check helper 持有 typeof） |
| M9 核心验证 | Done（build 0 警告 0 错误；ImmutableLeaf 焦点 31/31 + SystemTypeShadow 1/1；全量首跑 1604/1605，唯一失败为旧断言假设，已修复并复跑确认） |
| D1 验收矩阵 | Done（ImmutableLeafFamilyMatrixTests：Family 逐形状正例、混合编译、负例矩阵、三处文本断言升级为 binding 断言；全量 1617/1617） |
| D2 真包探针 | Done（ImmutableLeafConsumer + Run-ImmutableLeafProbe.ps1；双 marker 通过，主线程独立复跑确认） |
| 集成审查 + 文档收尾 | Done（2026-09-23：集成树 Rebuild 0 警告 0 错误、全量 1617/1617、探针复跑通过、git diff --check 干净；DB-075/DB-072/README/路线图/PROJECT-STATE 已更新；同日用户专项复核后修正主构造假阴性、删除 RS1042 豁免、加强影子断言并复验） |

## 7. 施工发现记录

- **System 全名影子按 C4 记录为编译阶段失败**：`namespace System { struct Guid }` 会把生成 DTO 中的 `global::System.Guid` 一并重绑定；reader/writer API 返回真实 corelib Guid，模型程序集在编译阶段即被拒绝（CS8377 unmanaged 约束 + CS1503/CS0029/CS0019 类型不匹配），无法构造 binding。在该 fixture 的引用方式下，`global::System.Guid` 本身解析到影子，源代码无法再命名真实 corelib Guid，因此两个同名类型之间的转换不可声明——这是该 fixture 与引用方式的结论，不是一般类型系统定理。产品见证 `SystemTypeShadowFailsAtCompilationBeforeAnyBinding` 断言生成成功、Emit 失败并钉住 CS0436 + CS8377 + 类型不匹配错误（2026-09-23 复核后加强，避免把意外编译错误当作预期失败）；guard 对重绑定内建字段类型的运行时拒绝由保持可 Emit 的 `LocalTypeShadowLosesImmutableLeaf`（本地命名空间影子 + 双向隐式转换）见证。不得为让该组合通过 Emit 而扩张序列化支持。
- **主构造捕获排除的语义修正（2026-09-23 用户复核）**：初版因 Roslyn 5.3.0 无 `INamedTypeSymbol.PrimaryConstructor` 而改用声明语法 `ParameterList` 一律排除，造成假阴性——参数仅用于显式 readonly 字段初始化器、或未使用、或空主构造时并无实例状态，本应合格。已删除该语法级开关；资格由实例字段审计与运行时最终结构核对判定（真捕获会物化为隐式实例字段而被拒绝）。已补 binary/Family 字段初始化正例、空主构造正例与真捕获负例。
- **测试 sibling 夹具改用 IIncrementalGenerator（2026-09-23 用户复核）**：以 `RegisterSourceOutput(CompilationProvider)` + `.AsSourceGenerator()` 接入基架，同轮不可见语义不变，RS1042 豁免已删除；不使用 `RegisterPostInitializationOutput`（会改变输出对其他生成器的可见时机）。
