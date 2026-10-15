# 显式 storage 分类机制 Probe

问题：同一个 `field:` 标注在 Roslyn source symbol、实际 PE metadata 和 DurableGraph Generator 中分别表现为什么？
本 Probe 保留 H01 调查的 14 个独立 fixture，观察真实 backing、计算属性、event、主构造捕获、static、普通 struct 与 record 对照。
它不执行业务保存，不进入根 solution，也不为普通 backing 增加产品支持。

当前工作集见 [PROJECT-STATE](PROJECT-STATE.md)；实施计划见 [DB-093](../../docs/design-branches/0093-explicit-storage-classification/README.md)，
未来支持研究见 [DB-094](../../docs/design-branches/0094-backing-field-support/README.md)。

## 运行

在仓库根目录运行，需要 .NET 10。项目正常引用当前 Generator（作为普通库）、Runtime 与 Roslyn 5.3.0，
从运行 host 的 TPA 和 `typeof(...).Assembly.Location` 取得 metadata references；不搜索旧 DLL、包缓存或本机固定目录。

存储依赖仍遵守[依赖指南](../../docs/storage-dependency.md)：默认 package mode 使用完整准确本地 feed 与任务 NuGet.Config；
显式 source mode 必须指向 pin 对应 checkout，二者不互相回退。

```powershell
# Package mode：$probeNuGetConfig 指向按依赖指南准备的完整 feed 配置。
$probeProject = 'experiments/ExplicitStorageClassificationProbe/ExplicitStorageClassificationProbe.csproj'
dotnet restore $probeProject --configfile $probeNuGetConfig -p:UseStorageSources=false
dotnet run --project $probeProject --no-restore -p:UseStorageSources=false -- --output experiments/ExplicitStorageClassificationProbe/obj/observations.json

# Source mode：$probeStorageRoot 为准确 pin 的 atelia-storage 绝对目录。
dotnet restore $probeProject -p:UseStorageSources=true -p:StorageSourceRoot=$probeStorageRoot
dotnet run --project $probeProject --no-restore -p:UseStorageSources=true -p:StorageSourceRoot=$probeStorageRoot -- --output experiments/ExplicitStorageClassificationProbe/obj/observations.json
```

选择一种模式执行即可。切换模式必须重新 restore；不要复用另一模式的 assets。
不传 `--output` 时，JSON 写入当前调用目录的 `obj/ExplicitStorageClassificationProbe/observations.json`；`--help` 查看参数。
动态 JSON 不提交，包含源代码、诊断和 metadata 观察，不写本机绝对引用路径、PID或时间流水。

## 观察与退出码

- `SemanticFields`／`SemanticProperties`／`SemanticEvents`：生成前 Roslyn 符号及 Attribute 归属。
- `InputCSharpDiagnostics`／`SourceEmission`：独立原始 C# 编译、Emit 及 PE 字段/属性；event 的 attributed backing 可以只在 PE 中出现。
- `Generator.HasErrors`／`Generator.Diagnostics`／`Generator.Exceptions`：Generator 层结果；没有 error 不证明该持久字段已经生成。
- `GeneratedCSharpDiagnostics`／`GeneratedEmission`：生成后的 C# 编译与独立 Emit；Emit 成功也不证明 Generator 接纳或拥有完整模型能力。
- `GeneratedFiles`／`CandidateManifest`：实际输出提示名与 Schema 候选，便于核对显式持久槽是否出现。

默认是纯观察模式，附带 Roslyn 形状与 record 支持正控。退出 0 表示这些正控成立；退出 1 表示正控失败，退出 2 表示参数/host不满足运行条件。
不以缺陷 case 的“SG 无诊断”作为成功条件。DB-093 修复后可以继续观察诊断变化，不能要求重现旧错误才能退出 0。
正式产品回归应进入相应 tests，并按 DB-093 的合同断言。

## 冻结的调查来源

来源为产品源码 `b209a777c3c103108f95e0277241a127fb1cc7b4`，2026-10-15 的临时 RoslynProbe/observations.json。
本目录将其源码改为正常 ProjectReference/PackageReference，并补充分层观察；下表是原始调查的压缩结果，
不是迁移后程序已运行的收据，也不是未来产品准入规范。原始 14 项均无 SG error、均能 Emit；差别如下。

| Fixture | Roslyn / 实际 PE 字段 | 原始 C# 警告 |
|---|---|---|
| ordinary_durable_auto | 隐式 property backing；DurableField | 无 |
| ordinary_transient_auto | 隐式 property backing；Transient | 无 |
| ordinary_unmarked_auto | 隐式 property backing；未分类 | 无 |
| ordinary_field_backed | C# 14 field storage；DurableField | 无 |
| ordinary_get_only | readonly backing；DurableField | 无 |
| ordinary_computed_durable | 无 backing，标注被编译器忽略 | CS0657 |
| ordinary_computed_transient | 无 backing，标注被编译器忽略 | CS0657 |
| ordinary_event_durable | source 无 IFieldSymbol；PE Changed 字段含 DurableField | CS0067 |
| ordinary_event_transient | source 无 IFieldSymbol；PE Changed 字段含 Transient | CS0067 |
| ordinary_primary_capture | 隐式 `<seed>P` 存在但无 DurableField；参数标注被忽略 | CS0657 |
| ordinary_static_durable | static backing；DurableField | 无 |
| ordinary_static_transient | static backing；Transient | 无 |
| struct_durable_auto | 普通 struct 隐式 backing；DurableField | 无 |
| record_durable_auto | record 隐式 backing；DurableField；Family 生成对照 | 无 |

原始 ordinary/struct 的候选未含相应 backing slot；record 为支持正控。
保存与全关冷读的 H01 证据另见 [DB-093 的历史包见证](../../docs/design-branches/0093-explicit-storage-classification/README.md#h01-evidence)，
本 Probe 不能替代该故事或证明持久恢复、history 发布、错误回滚与包交付。

## 2026-10-15 迁移验收

产品源码保持上述 `b209a77` 基线；本轮只改文档和机制 Probe。使用 SDK 10.0.201、C# 14/Roslyn 5.3.0、准确存储 pin 的 package mode，并显式使用任务 NuGet.Config：

| 检查 | 结果与范围 |
|---|---|
| 根 `DurableGraph.slnx` Release Rebuild | 0 warnings / 0 errors |
| 本 Probe Release build/run | 0 warnings / 0 errors；14 项观察与形状/record 正控通过 |
| 与原临时 JSON 逐项核对 | 14 份 fixture 源码、Roslyn 字段及其属性、生成后 PE 字段/属性一致；原基线 SG/Emit 阶段一致 |
| 相关产品回归 | 135 项通过，0 failed/skipped；筛选 RecordClass、RecordStruct、ImmutableLeaf 与 marker/类型资格反例 |

相关测试的实际筛选为：

```powershell
dotnet test tests/DurableGraph.Tests/DurableGraph.Tests.csproj -c Release --no-build --no-restore -m:1 -nr:false -p:UseStorageSources=false --filter 'FullyQualifiedName~RecordClass|FullyQualifiedName~RecordStruct|FullyQualifiedName~ImmutableLeaf|FullyQualifiedName~SourceMarkerLookalikeCannotGrantDurableQualification|FullyQualifiedName~UnqualifiedSourceClassCannotEnterNominalOrGenericSlots'
```

原始长日志和比较收据位于忽略的 `.artifacts/doc-plan-0093-0094/20261015/`；这里保留可复现方法和限定结果。该次归档验证没有执行 DB-093 修复，也没有实验性接入 DB-094；归档当时 H01 尚未修复。

## DB-093 修复后的观察

2026-10-15 用 `5ae1f18` 的修复源码、相同 SDK/Roslyn 和准确 S package mode 重新运行本工具，14 项形状及 record 正控通过：8 项不支持或无目标的显式 Durable/Transient 分类报 DG0024，2 项 static backing 报 DG0009，其余 4 项真实 Transient、未标普通属性与 record 正控保持合法。

缺陷输入的 source/生成后 CLR Emit 仍可成功；这说明观察层必须区分 Generator Error、current 模型资格与 CLR Emit。本工具仍不冻结“错误必须 Emit 失败”，也不把这些错误输入的旧“SG 无诊断”当作回归要求。

当前动态输出位于 `.artifacts/db093-implementation/20261015/portable-probe.json`。事件边界的追加调查已转成正式产品测试；源码回归、普通包负例及修正后冷恢复的完成证据集中见 [DB-093](../../docs/design-branches/0093-explicit-storage-classification/README.md#5-2026-10-15-验收证据与复跑)，不由本工具授予产品或包资格。

## 限制

14 项有意分开编译，避免 record 触发整个编译进入 Family 后掩盖普通类的默认路径。
未加载生成程序集执行领域代码；PE metadata 也不证明 Capture/Hydrate 行为。
不含 accepted history 输入、MSBuild Publish/Verify、别名/嵌套声明完整诊断矩阵、普通 backing 支持实验或性能测量。
有后续需求时扩展对应工作包，保留这些证据边界。
