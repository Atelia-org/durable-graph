# DB-082：准备材料范围的不可变叶复用实施记录

> 状态：**候选实现完成，默认关闭**；2026-09-28。A/B 未证明稳定总成本收益，按 §6 保留内部实验开关和重跑证据；最终回归及真实包结果见下文。合同见 [DB-082](0082-prepared-immutable-leaf-reuse-slice.md)，公共语义见 [DB-083](0083-repository-checkpoint-api-user-stories.md)。

## 问题、边界与验收门

本轮只回答：在 DB-080 已复用 State 准备材料的前提下，复用首次成功恢复的 ImmutableLeaf 实例是否安全且有总成本收益。
只允许同一 selection 的可达 true durable 叶进入只读表；mutable 图、保存会话与精确 Head 仍独立。
源工作副本、提交实例、Event 和只读读取不充种。没有 State 的请求完全绕过槽；新的 revision 不继承旧叶表。
不增加公共 API、持久格式、跨 revision 缓存、DeepImmutable、热提交资格或新的 provider 合同。

最小成功见证：同点第二次 named Fork 省去叶 Allocate/Hydrate，mutable owner 独立，保存及冷重开一致；
失败、身份冲突和迟注册 Schema 冲突均保持原有拒绝边界。两边均开启 DB-080，仅切换内部叶复用开关做 A/B，
观察完整 Fork 时间和分配、物化时间、回调次数及驻留规模，再决定默认接线。

## 分工与依赖

| 要求 | 实现/验收归属 | 完成证据 |
|---|---|---|
| 同材料叶表、完整身份预登记、不再 Hydrate | 产品核心代理；主线程审实际 diff | 核心审查与生成/失败回归通过 |
| 全图/交付门成功才安装、失败与证书寿命 | 失败测试代理、独立审查代理 | 14 cases 通过 |
| 普通/Family 真生成模型、历史位置、图隔离及保存 | 生成模型测试代理 | 14 cases 通过 |
| Upgrade、历史依赖淘汰、新材料不继承 | 历史测试代理 | 4 cases 通过 |
| 对称 A/B、完整成本、代表性真实包 | 测量代理准备；主线程串行运行 | 960 次 Fork 完成；真实包通过；默认关闭 |
| 构建、回归、集成裁决、文档与提交 | 主线程 | 根构建及全仓 2,942 项通过；独立审查无阻断 |

产品接缝先冻结：`Repository.ImmutableLeafReuseEnabled` 为内部实验开关；entry 暴露内部叶数量供见证；
统计新增复用叶计数与仅显式观测时启用的物化计时。只读映射不进入工作副本保存基线。
先完成实现和语义验收，再运行测量与真实包，最后独立审查及决定默认行为。

## 实现与所有权

- [PreparedStateRestoration](../../src/DurableGraph.Persistence/PreparedStateRestoration.cs) 持有可选只读叶表及已收集标志。
  无合格叶时不保留空字典；已安装表不再合并或修改。工作副本仍只导入 normalized 保存基线与自己的 CaptureSession。
- [GraphReader](../../src/DurableGraph.Persistence/GraphReader.cs) 在任意 allocator 运行前，将全部复用叶登记到本图实例表、
  反向身份记录及操作内唯一性集合。只对映射明确给出的 ID 跳过 Allocate/Hydrate，其他对象继续完整两阶段恢复。
  表只来源于同一 selection 的已验证 entry，不从领域 live 图、另一 revision 或只读结果取得。
- [Repository](../../src/DurableGraph.Persistence/Repository.cs) 在冷恢复成功后构造候选表；完整身份导入和工作副本构造成功后才形成候选 entry。
  Fork 沿用原发布门，发布后只赋引用。失败保持旧槽，fault/Dispose 清槽；Event-only 不读取槽。
  内部 A/B 开关由关闭改为开启时，对既有无表 entry 完整物化一次，在新收集范围中复核旧证书并加入本次检查，再按同一交付门替换。
- 首次成功物化的额外标准 Schema 检查进入 DB-080 证书，命中先复核后共享。新 revision 从自己的冷准备与物化收集，
  不继承旧历史 Upgrade 或叶回调证书。DB-081 仍未接入热提交。
- [统计](../../src/DurableGraph.Persistence/GraphReadStatistics.cs) 增加复用叶数与物化 Stopwatch ticks。
  仅显式提供统计对象时计时，范围是登记、分配、引用表及 Hydrate，也累计失败尝试；不包括身份导入和叶表收集。
  完整 named Fork 测量另外覆盖这些成本及 ref 发布。

## 可执行证据

| 入口 | 覆盖 |
|---|---|
| [真实生成图](../../tests/DurableGraph.Tests/DB082GeneratedLeafReuseTests.cs) | binary / forced Family 实际 capability、mutable/集合隔离、等值异 ID、alias/环、纯叶与空根、S/E 精确 Head、Event-first、只读入口不碰表、跨 revision/换根、Remove/保存/冷重开 |
| [失败与生命周期](../../tests/DurableGraph.Persistence.Tests/DB082LeafReuseFailureTests.cs) | 全图成功门、开关补填、已有表失败不重 Hydrate、晚遍历缓存叶预登记、额外 Schema 冲突、冲突后的无 State 绕过、发布三种 outcome、叶表释放、活动工作副本不保活旧 entry |
| [历史与保存来源](../../tests/DurableGraph.Persistence.Tests/DB082LeafReuseHistoryTests.cs) | 升级叶首存 Base/rewrite、ID/head/H、后继 NoChange、冷重开、同 ID/head 新 revision 新表、旧中间依赖消除与实际叶依赖保留 |
| [测量入口](../../tests/DurableGraph.Tests/DB082ImmutableLeafMeasurements.cs) | opt-in 真实生成模型 A/B，完整 Fork 与物化分项、托管分配与驻留观测 |
| [真实包消费者](../../experiments/PackageConsumerProbe/ImmutableLeafConsumer/README.md) | Family 叶共享、mutable owner 独立、等值异 ID、别名/环、独立保存与冷重开；同轮 sibling generator 分类拒绝继续回归 |

独立审查发现的两处夹具错误已修正：跨重开不能比较带 Repository owner 的 CheckpointAddress；
完整 DTO 的 Delta body 不保证被规划器选成 Delta，changed owner 验证新行与保存来源，升级叶仍明确要求 Base。
首次编译还修正了测试程序集直接访问 Runtime internal capability 的错误；没有扩大 friend 权限。
`FromLoaded` / `BranchCheckout` 构造先于候选 entry 与发布的顺序经源码审查确认；本轮没有新增专用的工作副本构造故障钩子。
可执行失败见证覆盖叶已完成后的全图失败、补填失败及实际发布前/后/未知结果。

## A/B 成本与默认关闭裁决

环境为 Windows、.NET 10.0.5，Release 产品及 Release 动态生成模型。两边 DB-080 均开启；
每条普通/强制 Family 路径覆盖五种场景，每格丢弃一批预热、记录五批，每批八次 named Fork，
共 960 次 Fork，其中 800 次进入记录。每批使用新仓库、相同种子，A/B 先后交替；八个工作副本同时存活至批尾。
没有 DB-081 热准入，也没有改变发布屏障。普通生成源不含导致自动切换 Family 的容器字段；Family 循环场景另含 List。

[逐批 CSV](0082-prepared-immutable-leaf-reuse-measurements.csv) 忠实导出原始 100 批的 30 个非逐操作字段；
主线程复算配对耗时及分配结果。原始逐操作 JSONL 位于 `.artifacts/db082/measurements.jsonl`，
分析详情与日志在同目录；这些可重生成工作产物不提交。

下表比率均为**同一路径、场景、批次的 on/off 比率中位数**，小于 1 表示开启更低；不是两组中位数之比。
分配差是每批八次 Fork 的配对差中位数。“更快”只计样本方向，不表示统计显著或普遍加速。

| 路径 / 场景 | 完整 Fork 比率 | 开启更快 | 物化比率 | 托管分配差 B |
|---|---:|---:|---:|---:|
| 普通 / 叶占优 | 1.026 | 1/5 | 0.975 | −236,336 |
| Family / 叶占优 | 1.038 | 2/5 | 0.646 | −244,264 |
| 普通 / 全 mutable | 1.046 | 1/5 | 0.968 | +40 |
| Family / 全 mutable | 1.022 | 2/5 | 1.001 | +40 |
| 普通 / 循环 | 1.042 | 2/5 | 1.235 | −237,360 |
| Family / List 与循环 | 1.019 | 2/5 | 0.912 | −243,896 |
| 普通 / 纯叶根 | 0.854 | 5/5 | 0.836 | +520 |
| Family / 纯叶根 | 1.019 | 2/5 | 0.784 | +520 |
| 普通 / 深链低复用 | 0.971 | 3/5 | 1.010 | +2,368 |
| Family / 深链低复用 | 1.148 | 1/5 | 1.012 | +2,368 |

叶占优使用 256 个 mutable owner 和 4,096 片 readonly int 叶；每批 Allocate/Hydrate 从 34,816 降到 6,144，
复用 28,672 次。非交替场景双方准备槽均为 7 hit / 1 miss，未混入 DB-080 准备收益。
目标场景总托管分配的配对中位数只下降约 0.486% / 0.509%。Family 物化五对样本均改善，
但完整 Fork 比率范围为 0.590–1.102；普通为 0.951–1.359，无法确认稳定端到端收益。
纯叶普通路径虽然五对完整 Fork 均更快，物化仅节省约 0.01 ms，完整 Fork 却相差约百毫秒，
不能把这类发布/文件系统波动归因于叶复用。全 mutable 未驻留空叶字典；低复用路径仍需付建表成本。

驻留与峰值观测也须区分：

- 目标场景八图并存时，强制 GC 后的进程托管堆端点配对差约少 452 KB；释放工作副本后反而约多 334,208 B。
  这是近似进程堆差，含表等开销，不是精确缓存字节。槽逻辑驻留为 4,096 叶；4 B/叶仅计算领域标量，不含对象头、表、DTO、证书和模型。
- 纯叶根端点分别约增加 88 / 280 B；深链低复用增加 256 / 280 B。源图、已交付图的持有量不受单槽约束。
- Fork 后采样的 occupied heap 高水位不是严格峰值：目标普通约 45.87→45.71 MB、Family 49.45→48.99 MB，
  普通循环反而约 45.66→48.84 MB，受 GC 时机影响。进程生命周期 peak working set 为约 252.9–266.4 MB，
  包含编译/JIT/先前格，不能用于开关归因。详细字节值保留在 CSV。

**裁决：候选安全机制保留，`ImmutableLeafReuseEnabled` 默认 false；不宣称产品默认交付叶共享加速。**
这不是证明所有叶模型都无收益，而是当前实验未满足原先约定的启用门。内部开关只为回归与重跑，
不加入公共 options，不扩展 weak cache、跨 revision 或 DeepImmutable。

## 验证与重跑

验证使用正常公开 Storage pin `0.1.1-preview.2`，未切换兄弟仓源码或开发包。基线 Persistence 907/907 通过；
最终产品构建为 0 warnings / 0 errors。所有 dotnet 命令由主线程串行运行，测试与测量没有重叠。
新测试的真实执行结果以最终 TRX 及测量独立运行记录为准，验证日志保留在 `.artifacts/db082/`。

最终全仓 **2,942/2,942** 通过、0 failed / 0 skipped：Persistence 925、Runtime/Generator 1,652、Storage 202、Serialization 163。
主线程解析全部 TRX 并确认本片 32 个语义用例及 1 个 opt-in 入口均通过；性能入口另以环境开关启用后独立通过。
根构建日志 `build-final.log`，全仓日志 `tests-final.log`、四份 TRX 位于 `final-test-results/`。

真实 PackageReference 验收通过，DG 使用独立版本 `0.0.0-immutable-leaf-e2e.20260927210413.10000`，Storage 仍为 `0.1.1-preview.2`。
Family 输出 `ImmutableLeafFamily:Flag:True:Roundtrip:True:ForkSharing:True:MutableIsolation:True:ColdSave:True`；
sibling 输出 `ImmutableLeafSibling:Flag:False:True`。日志为 `package.log`，独立 feed、缓存与消费者产物保留在
`experiments/PackageConsumerProbe/obj/immutable-leaf-20260927210413-10000-fb877e11/`。

独立审查覆盖产品 diff、语义/失败/历史测试、测量混杂和文档，未遗留阻断。
主线程与 reviewer 分别复算 CSV；受影响的 8 份 Markdown、474 个本地文件链接和原有标题/显式锚点保持检查通过，`git diff --check` 通过。

```powershell
dotnet build DurableGraph.slnx -c Release --no-restore -m:1 -nodeReuse:false
dotnet test DurableGraph.slnx -c Release --no-build --no-restore --logger trx --results-directory .artifacts/db082/final-test-results

$env:DURABLEGRAPH_DB082_MEASURE = '1'
$env:DURABLEGRAPH_DB082_MEASURE_OUTPUT = Join-Path $PWD '.artifacts/db082/measurements.jsonl'
dotnet test tests/DurableGraph.Tests/DurableGraph.Tests.csproj -c Release --no-build --no-restore --filter FullyQualifiedName~DB082_Measure_generated_immutable_leaf_forks
Remove-Item Env:DURABLEGRAPH_DB082_MEASURE
Remove-Item Env:DURABLEGRAPH_DB082_MEASURE_OUTPUT

./experiments/PackageConsumerProbe/Run-ImmutableLeafProbe.ps1
```

测量开关只影响该 opt-in Fact；普通全仓回归不自动执行 960 次 Fork 的成本实验。
真实包运行显式通过 probe-only 反射开启内部叶候选，验证候选的包交付，并继续验证 sibling generator 的 false 分类；
不据此声称默认产品已启用。没有改生成器或包接线，未扩大成全部历史包消费者矩阵。

## 后续入口

下一产品候选是 [DB-084](0084-eventjournal-immutable-tags-slice.md) 的 DG 接入，解决 [DB-083](0083-repository-checkpoint-api-user-stories.md)
仍未交付的跨重开固定位置。开始时重新核实上游开发包的精确来源、发布异常语义和严格 Open，按
[存储依赖指南](../storage-dependency.md) 安排独立包验收；本轮未改 storage pin 或实施 tag。

恢复优化先等待真实业务叶模型、同时持有图数量和性能剖析。若物化已是明显热点，或多图并存节省的内存足以抵消槽驻留，
复用本轮内部开关与逐批基准重新裁决；不因编号递增自动推进 DB-073/074。
DB-081 的证明缺口与重启条件仍以[其资格实验](0081-hot-commit-restoration-material-experiment.md)为准。
