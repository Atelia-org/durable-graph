# DurableGraph 产品开发工作集

> 校准：2026-09-27；DB-077 已实施并验收；DB-083 目标语义已选定，DB-078-A 已实施并验收；B/C 与 079–082 尚未实施；DB-084 上游已交付本地开发包，DG tag 接入未实施；DB-073/074 仍为待修订草稿。本文只维护当前能力、边界与续工入口。
> 文档不是实现授权；事实以当前源码、测试和工具输出为准。

## 从这里继续

先读本文，再按任务选择一份材料；不需要按 DB 编号通读历史。

- 下游首次接入：先看[根 README](../README.md) 的完整保存/重开示例、包准备与 Schema 升级步骤。
- 理解产品目标与已选约束：[目标设计](../docs/DurableGraph-target-design-v0.md)。
- 查首选用语、概念示意和对应代码：[项目术语表](../docs/DurableGraph-glossary.md)；后续文档、代码命名与注释的一致化以此定位差异。
- 选择下一片、查未完成决策与问题：[后续路线](../docs/DurableGraph-research-roadmap.md)。
- 查某项实现的来由或验收：[设计与施工记录索引](../docs/design-branches/README.md)。
- 查历史实验：[实验簿入口](../docs/DurableGraph-lab-notebook.md)；重跑独立实验：[Probe 导航](../experiments/README.md)。

四个早期 Probe 已归档到 Git，默认搜索优先 src/tests 与活动回归；只有具体历史机制问题才查
[归档恢复索引](../experiments/ARCHIVE.md)，不要把旧项目整体恢复为续工上下文。

## 当前焦点

[DB-078-A 非泛型入口与自由历史](../docs/design-branches/0078-a-repository-free-history-implementation.md) 已完成并验收：
根命名空间的 `Repository` / 非泛型 `BranchCheckout` / `CheckpointAddress` 已迁移，允许 Event-first、连续 E/S 与跨实际类型 State 替换。
模型继续由 DB-077 在每次 Open 固定。Checkout 只恢复最近 State；纯 Event 前缀的 State=null，不恢复或 replay Event。
地址按本次打开实例与 Journal 位置判等；重开必须从持久 ref 重新取得，不提供外部序列化入口。
源码回归、真实包消费者、旧数据续写与独立审阅已通过；实际证据只记录在本片实施记录。

下一停点是 [DB-078-B](../docs/design-branches/0078-editable-checkpoint-fork-slice.md#12-078-b独立-checkpoint-与固定历史查询)：
独立 Checkpoint、nearest 严格祖先 PreviousX、固定历史事件枚举。之后 C 交付每 branch 占用与 named Fork。
本片仍每 repo 至多一个活动工作副本，尚无便利 Checkpoint/EnumerateEvents/Fork；全量 ReadFrames/ReadEvents 保持明确的全链成本。
[DB-083](../docs/design-branches/0083-repository-checkpoint-api-user-stories.md) 是目标合同，不能把已交付的 A 等同于完整目标。
应用持久保存自身执行阶段和处理进度；库没有 PendingEvent，不从 E/S 次序推断业务完成，也不提供外部副作用 exactly-once。

[DB-076–082 路线](../docs/design-branches/0076-efficient-graph-fork-technical-path.md#10-分片施工导航) 的后续顺序保持：
078-B/C 分别验收 → 079 按用途恢复核心 → 080 单 State 准备槽 → 081 热 State 准入；082 immutable 叶实验仅硬依赖 080。
无 State 请求绕过材料槽/叶表，优化关闭仍须正确；这些优化尚未实施。
[DB-084 不可变 tag](../docs/design-branches/0084-eventjournal-immutable-tags-slice.md) 的上游本地包已交付，
DG 公开接入仍独立待办，依赖 A 后按准确 package/revision 验收；不在 DG 自建持久权威。默认公开 Storage pin 保持。
[DB-073](../docs/design-branches/0073-repository-scoped-weak-reference-cache.md) /
[DB-074](../docs/design-branches/0074-efficient-fork-deferred-directions.md) 继续 **Draft / 尚待进一步修订**，不作为并行工单。

[DB-075](../docs/design-branches/0075-immutable-leaf-proof-and-family-refactor.md) 已完成共同候选规则与生成的最终结构核对，
两条路径均可交付合格叶的 true；验证见[施工工单](../docs/design-branches/0075-immutable-leaf-implementation-work-order.md)
及[真包消费者](../experiments/PackageConsumerProbe/ImmutableLeafConsumer/README.md)。后续使用该 capability 的模型程序集须采用修复后的生成结果，
旧 DB-072 已编译消费者内嵌常量 true，只升级 runtime 不修复。
能力现状（经 DB-075 修订）：`StateModelBinding.IsImmutableLeaf` 通道保持公共可选构造参数、手写默认 false；
候选资格由同编译非泛型闭包决定——全部实例字段显式声明 readonly 且进入 durable 管线，允许 19 种内建、
同编译 enum/Nullable/递归 inline 与显式 readonly 字段的 record；record 全部隐式 backing、物化为隐式实例字段的主构造捕获、
闭合泛型、外部成员与 string/引用/Transient 仍 false；最终结构在每次 binding 创建时核对，失败仅失去资格。

存储依赖已改为独立 `atelia-storage` 的版本包与显式源码联调。
版本/来源 pin 统一记录在 [StorageDependency.props](../eng/StorageDependency.props)。
本地固定来源准备、包/源码引用图、934 项存储/持久化测试、EventHistory/recovery 包 probes
及冻结旧存档的冷读→续写→再冷读已通过；完整 2668 项测试通过于此前本地 `0.1.1-dev.20260913.2` 候选。
P5 公开包下载、签名与 Source Link、私有缓存下的完整构建/Storage 202 项、EventHistory/recovery probes 和旧数据冷进程验证已通过。
版本与源码 commit 以 pin 为唯一权威；五个存储包已发布到 nuget.org，DG 自身四包仍由本仓按需生成。
日常 build/test 直接从 nuget.org restore；Prepare 仅为独立 probes 下载公开 nupkg，不再 clone 或重打存储包。
普通构建、显式源码联调及本地开发包的独立 NuGet 配置统一见[存储依赖指南](../docs/storage-dependency.md)；
本片只改交付接线，不改变下述存储格式、恢复和产品能力。

[DB-071：程序集与命名空间组织](../docs/design-branches/0071-assembly-namespace-organization-review.md) 已实施：
七项目依赖保持；底层包为 Serialization/Storage，上层为 Persistence，主程序集内分根/Schema/Runtime。
签名、产品回归、真实包、旧包续写和隔离 DramaBoard 证据集中在[验收记录](../docs/design-branches/0071-assembly-namespace-validation.md)。
[DramaBoard 试用反馈](../../drama-board/docs/feedback/durablegraph/004-db071-namespace-adaptation.md) 未发现适配或持久化问题，
报告 527 项测试及真实旧包存档续写通过；用户已批准本库提交，正式 pin 由下游固定到本次提交。
后续回到真实业务长轨迹与保存成本反馈，不自动开启新能力。

[DB-070：读取放大阈值默认选择](../docs/design-branches/0070-read-amplification-default.md) 将 EventHistory 生效默认改为 `{5,5}`。
选取依据是稳定小 Delta 模型中约 25% 的额外 Base 摊销开销；不是实测最优值或空间/读取上限。
用户的 `{3,5}` 冷读方向与 `{11,5}` 存储方向配置见[根 README](../README.md#调整保存策略)；测试基准独立保持 `{3,5}`。

[DB-069：热保存基线增量计量](../docs/design-branches/0069-incremental-save-baseline.md) 已实施：
已验证的 head/H 随 DTO 基线增量推进，受控热保存免除旧对象链重复读取；精确 payload、Event/State 与失败边界保持。
独立审阅、前后测量、源码回归与真实包验证已通过。
下一步按真实长轨迹反馈评估 map 回溯与全量 Base 准备，重访条件见路线图，测量证据仅保留在本片。
普通 State 已加入[目录 Base 选择](../docs/research/object-head-map-base-feasibility.md)：仅当完整目录尾部的保守字节上界
严格小于 Remove 列表时切换；保留精确 Parent、对象内容决策与基线安装，Event 仍用独立目录 Base。

[DB-068：record class 领域模型](../docs/design-branches/0068-record-class-model-slice.md) 已贯通：
IDurableObject 完全替代并移除 DurableBase，普通 class/record 共用资格和用户自建继承链；
positional/backing storage、泛型、跨库继承及历史升级复用现有对象管线。
真实旧包→新接口续写和同版 class→record 的 history 均保持；证据集中在分片记录。
下一步回到下游真实模型/业务升版和较长轨迹，按需求评估新的摩擦；
下游升级包须迁移接口并重编译，但无需立即把普通 class 改 record。
任意外部基类、深不可变/集合内容相等和新容器不随本片开放。

[读缓存 DB-067](../docs/design-branches/0067-owned-revision-read-cache-design.md) 已实施并验收：
正常 Store 统一有界缓存 owned frame / 完整 map，地址字典采用 keys/values 双数组二分，已有 local records 直接二分定位。
日常 append-only、仅离线救援截断且随后新开 Store 的执行纪律见 [AGENTS](../AGENTS.md#persistent-data-discipline)。
生命周期接线、产品回归、真实包验证及新表示测量已完成；证据集中在 DB-067，后续优化由真实工作负载触发。

[DramaBoard 首轮 API 反馈](../../drama-board/docs/feedback/durablegraph/001-eventhistory-api.md) 的近期改进已由
[DB-065](../docs/design-branches/0065-event-history-consumer-contract-slice.md) 完成：默认调用、包内 XML 文档、
[引用事件快照与恢复示例](../experiments/PackageConsumerProbe/EventHistoryRecoveryConsumer/README.md)，以及 README 原文执行验证。
恢复示例与内部故障测试共用 Pending 判断；已发布 S 不重放，失败后重新取得 State/Event，不复用旧的可变图。
现有提交/格式、DB-064 只读共享与可写隔离合同保持。
第二轮 [ReadPair 反馈](../../drama-board/docs/feedback/durablegraph/002-readpair-sharing-contract.md) 已完成于
[DB-066](../docs/design-branches/0066-readpair-comparison-and-transient-contract-slice.md)：共享判断改用可选完整状态比较，
缺 proof 保守不共享，真实错误传播。普通/Family SG 自动登记，数组/List/Dictionary 复用静态槽比较；
Dictionary 读取的 canonical key 验证保持。ReadPair 的视图专属 Transient 放在图外，独立读取允许各自初始化，续写使用 Checkout。
完整测试、独立审阅和真实包验收已通过，证据集中在分片记录。
下游已按公开入口接入真实模型，可继续玩法扩展并收集具体摩擦；
跨重开书签、局部事件浏览与类型适配扩展按路线图的需求触发，不自动扩展本轮范围。
自动跨库业务规则发现、ValueTuple、DateTime 和 SchemaStore 自举不随该轮扩张。

## 当前能力与实际边界

| 层 | 已验证能力 | 尚未闭合的边界 |
|---|---|---|
| [DurableGraph](DurableGraph/DurableGraph.csproj) | immutable Schema/exact DAG；统一 ObjectBinding、ObjectLayout、Capture/refs/恢复目录；SZ/rank 2–4 数组、List 与 Dictionary owned 状态；静态 StateEquals、数组稀疏/列表区间/字典键寻址 Delta、默认 Adaptive 与三种显式 List writer；独立 historical reader | 其他 BCL、数组协变；持久发布由 Persistence 拥有 |
| [Generator](DurableGraph.Generator/DurableGraph.Generator.csproj) / [Build](DurableGraph.Build/DurableGraph.Build.csproj) | class/struct（均含 record）开放模板、显式 enum、readonly DTO/静态 body、Capture/Hydrate、泛型继承与递归 Nullable/数组/List/Dictionary 组合；跨程序集 nominal/动态参数、固定 base/inline 与只读模板导出；history v9；三参 Upgrade/旧二参适配、值规则/局部依赖 adapter | 其他 CLR 值类型、其他 BCL；跨程序集业务规则发现 |
| [Persistence](DurableGraph.Persistence/DurableGraph.Persistence.csproj) | 统一闭合目录与 Base v4 ID 头；完整 stored/current 引用验证、两阶段恢复；非泛型 Repository、Event-first/自由 E/S、跨类型 State、分支/Move/Checkout 与严格只读浏览；操作内 exact 解码缓存、显式 ReadPair 引用闭包共享、可写恢复隔离；升级 Base/Remove、已提交基线 head/H 增量维护 | 联合 Store 视图、更强恢复保证与有测量依据的读取优化另行排期 |
| [Storage](DurableGraph.Storage/DurableGraph.Storage.csproj) | AppendDurably 原 lease 屏障；local Base/Delta records、wire v3、exact Revision live map、Parent/prior 校验、object-first 原始重建链及实际 payload H；Base 精确/Delta 上界计量；真实 Segment/RBF 冷重开；有界 owned frame/map LRU、双数组只读地址字典与 local-record 二分 | 不解码 typed body；不拥有持久 roots、类型目录或发布 head；缓存预算约束估算驻留量，不约束总堆或操作峰值 |
| [Serialization](DurableGraph.Serialization/DurableGraph.Serialization.csproj) | 字节原语、string 内容 codec、拥有 raw bytes 的 PreparedBaseBody/PreparedDeltaBody、显式 body 的 typed slot、早期元素 ref 循环 | 完整数组/List 对象操作位于 Runtime；其他 BCL 内容 codec 尚无 |

容易混淆的限制：

- SG DTO/body 支持递归 inline struct 与 19 种标量：bool、byte/sbyte、short/ushort、int/uint、long/ulong、char、Half、float、double、Guid、decimal、TimeSpan、DateOnly、TimeOnly、DateTimeOffset；string、受支持 durable class、数组、List 和 Dictionary 引用槽保存非泛型 ObjectId；字节层仍编码 UInt32。
  SG 在声明所属编译中为顶层 partial class/record class 链或显式 partial struct/record struct（包括 readonly）生成代码，支持泛型；
  支持 readonly 持久字段和没有无参构造器的领域类。RuntimeHelpers 分配、SG Hydrate/声明层 UnsafeAccessor
  不执行实例构造器或字段初始化表达式；Transient 由用户交付后重建。
- Guid/decimal/TimeSpan 为内建值叶子，tag 19/20/21；DTO 直接存 CLR 值，无独立 Schema/对象行。
  Guid 固定 16-byte big-endian，decimal 公开四字固定 16-byte little-endian，TimeSpan 复用有符号 Ticks varint。
  decimal 的 scale/符号零保留；普通/Family/Runtime 共用静态完整表示比较。Dictionary 默认 ScalarDefault，
  数值相等但表示不同的真实 key 替换产生 Remove+Add。既有 SCB1 v2、Base/Storage/容器格式保持。
- DateOnly/TimeOnly/DateTimeOffset 为内建叶子，tag 22/23/24；公开 DayNumber、日内 Ticks、clock Ticks + 整分钟 offset
  使用 canonical varint，不读取本地时区或 CLR 私有布局。DateTimeOffset 普通/Family/Runtime 均用 EqualsExact；
  Dictionary 默认仍按瞬间查找，真实换 offset key 为 Remove+Add，indexer 等价 key 不替换原 key。
  新 history v9，旧 v1–v8 保留原能力门槛/文件；DateTime 继续拒绝。详见 DB-058。
- enum 显式标记 DurableType，支持同编译顶层 public/internal 声明及八种整数底层类型，不要求 partial。
  使用有独立 nominal/版本的单整数 InlineValue Schema（合成 FieldId=1），复用 Family DTO/body；外置投影直接 cast。
  不新建 TypeTag、history/catalog 格式或对象行；unknown 数值/Flags bits 原样保留，常量名称、别名、赋值表与 Flags 不参与 history。
  底层类型变化须升版；业务数值重解释由显式版本/Upgrade 负责。Runtime 当前模板核对真实底层类型，历史不受当前宽度限制。
  可组合泛型/Nullable/数组/List，enum 版本依赖及显式 owner/值升级沿用 inline 规则；未标记/nested/file-local enum、常量误标持久属性拒绝。
- Nullable<T> 支持上述标量、Durable inline struct 与 enum，包括泛型组合。DTO 使用 NullableState<TState>，
  exact 槽持有规范 child；absent 不捕获/读取/比较/遍历内部值。Base 0/1、Delta Clear/Set/Patch 校验 prior，
  present 使用 child 静态操作。Nullable 不独立占用对象 ID；含 Nullable 的声明进入现有 Family 生成路径。
  构建 history 的 q(child) 保留固定 inline child 版本；T? 与 T 恰好闭合 Nullable 保留不同参数来源。
  显式规则集 AllowNullableLifting 默认关闭；开启后仅将已绑定 child 工具提升，空值/空集合仍预检完整依赖。
  显式 wrapper provider 优先，按 child inline 版本选择；错误不得回退。框架不自动补值或进行 T↔T? 业务转换。
- SchemaKind 区分 ReferenceObject/InlineValue，同 family 不得跨 kind；inline 字段持有完整 exact Schema，
  版本变化沿 inline/base 传播，nominal 不传播。struct 独立生成 Schema/history，但不登记对象或独立 Upgrade。
  readonly DTO 递归嵌套，引用投影为 ID；子 PrepareDelta 的 HasChanges/bytes 决定父位，置位但子无变化拒绝。
  共享值 DTO/body 按 exact key 生成，不依赖当前领域 struct CLR 宿主；owner Upgrade 通过强类型构造器显式转换，
  删除 struct 后仍可保留完整 owner 升级链。struct Hydrate 从 default 临时值经 ref accessor 填充，
  完成后赋回字段/元素槽，不运行构造器或初始化器，Transient 默认。仍无 ref struct/CLR nested type 支持。
- record struct 支持 positional、body 自动属性及 C# 14 field-backed storage，以 `[field: DurableField]` / `[field: Transient]` 显式分类；
  未分类 backing、无实际字段的误标、未知事件存储和生成 helper 碰撞拒绝。属性方法不参与保存恢复，计算属性不自动入 Schema。
  当前投影按 Roslyn MetadataName 发出强类型 UnsafeAccessor，历史不保存 backing 名或 record 标志；字段重命名/重排不改变持久布局。
  record 的业务 Equals 仍由 C# 或用户决定，可能包含 Transient；作为 Key 沿 DB-055 比较/恢复边界，不自动生成替代 comparer。
- class/record class 以 IDurableObject 统一资格，DurableBase 已移除；用户祖先链止于 object，每个领域祖先仍需 Schema/history。
  record class 复用实际 backing storage 与声明层 base projection；引用 accessor 用对象 receiver，inline 才用 ref。
  positional 复用基类属性不重复存储，支持泛型及跨程序集继承；业务 equality/with 不改变引用身份、DTO 比较或冷 Checkout 隔离。
  普通 class 仍不支持一般属性，marker 不授予接口持久槽、boxed 身份或任意外部基类。迁移与证据见 DB-068。
- `TypeExpr` 区分 builtin、named 定义及有序实参、声明内 parameter、SZ/rank 2–4 数组和内建 List/Nullable/Dictionary 构造；持久 key 为闭合 TypeExpr + 定义版本。
  SchemaId 只表示定义 ID，不能用来区分闭合族。base/inline 显式升版仍沿定义传播；Box<int> 也随 Box 定义升版。
  目标仓库内同 key 完整布局严格一致；两个独立空库仍可能首次登记同 key 异形，不提供闭合历史账本或跨库 key 互换保证。
  TypeExpr depth≤64、展开 nodes≤4096、arity≤32，exact 布局 DAG depth≤256。
- 含泛型/Nullable 当前定义或历史、当前 enum/record struct、显式 DurableUpgrade 或值规则/依赖属性的编译，
  以及有当前声明且保留无当前声明的 inline history 的编译，使用 `Generated.Family_<UTF8HexId>.Vn<TState...>`，
  `Generated.DurableDefinitions.Register` 显式登记生成定义；亦可单独登记 Family.Definition。
  历史 DTO/body 不携带领域泛型参数，phantom 参数不产生状态参数。已知叶子直接调用，未知槽使用 IStateOps/IValueProjection 静态约束调用。
  旧纯非泛型编译保留 Schema/GetSchema/__DurableState；迁入 Family 路径后，内部 DTO 类型引用需改用 Family alias。
- 目录冻结定义/provider，成功闭合在 snapshot 内缓存；current 按 CLR Type，historical 按完整 stored Schema 绑定。
  snapshot 只冻结代码能力，仍观察同一 Repository 内单调积累的 SchemaStore 权威定义。
  同 T 多处 exact 不一致、错误 arity/kind、未知定义及不支持的 CLR 闭合拒绝；nominal 边不递归展开对象 body。
  UpgradePlan 持有展平去重的 exact Schema requirement set；缓存命中在 callback 前统一核对后续已注册 Schema，
  冲突报告 owner endpoint 到具体 base/inline 字段的稳定路径。普通 binding 缓存仍复用同一闭包算法；共享 DAG 不按树重复展开。
- 跨程序集 public 顶层 Durable 类型可进入 nominal 表达及动态表示参数；包括外部 class、容器元素、
  `LocalBox<RemotePoint>` / `RemoteBox<LocalPoint>` 及本地 `InlineBox<T>` 闭合外部值；也支持直接固定外部值字段/Nullable/泛型值。
  `DurableGraphGenerateDefinitions=true` 强制现有 Family 路径；未设置/false 保持自动选择。聚合器 `DurableDefinitions`
  为 internal，各库以公开 Register/ RegisterReaders facade 登记；history 独立，Runtime 不扫描程序集。
  nominal 查询优先 Definition，缺失时允许精确相同 nominal 的已登记普通 Model/reader，仅证明 ReferenceObject，
  不调用目标工厂或补历史模板；普通匹配与 Upgrade 反推共用，实际 exact reader 仍须存在。
  目标单独升版不改变 nominal-only owner；动态 inline 仍要求 owner 升版。程序集拆包不增加 CLR 二进制兼容保证。
- Family 编译以 DurableSchemaExportAttribute 导出自有当前/保留模板（inline 执行合同 1、class 合同 2，模板 v9），SG 按需读取编译引用元数据，
  包括传递 base/inline、internal 实现依赖、仅历史和仅本地值规则端点；固定外部依赖自动选 Family，复用公开状态能力。
  不重生成/重导出外部 Family；Build 通过带候选摘要关联的 canonical reference manifest 校验，只 Publish owned candidate。
  referenced 自闭包、accepted+referenced、最后 current 的顺序保留历史完整性；外部依赖不能借 owned 同 ID 补缺。
  独立库同 ID 即使模板相同也拒绝；导出材料不授予 Runtime factory/reader/Upgrade。固定外部值改变仍要求 owner 升版。
  普通无引用 candidate 与既有 Family DTO arity/Schema/body 保持；自动跨库业务规则发现仍未开放。见 DB-060/061。
- Family class 当前投影统一处理本声明字段，并经 StateBaseProjection 调用 immediate base 的已绑定 Capture/Hydrate，
  同库/跨库共享路径；支持 public 顶层 abstract/generic 外部基类，hidden internal 泛型/Nullable 字段留在定义库处理。
  base DTO 参数按原始参数代表字段映射到 leaf 前缀，state/ops 从 exact 槽取得，本地 projection 参数单独选择；
  完整 leaf DTO/body 仍展开，不分配基类对象、不增加 ID、不自动执行基类 Upgrade。
  该能力由 generated model 显式开启，手工 binding 默认关闭；冷绑定核对完整 Schema/DTO，整对象 exact CLR 检查保留。
  每层委托/DTO 复制的深链成本留待实测，不另建优化后端。历史变化仍要求受影响 leaf 显式升版和 Upgrade。
- 新 `DurableUpgrade` 方法使用非泛型 static host 中可访问的三参方法；运行时优先闭合 owner 特例，否则选择通用边。
  整条相邻链在该对象首次业务调用前绑定；中间 exact 布局来自显式 DTO 表示、已注册 Schema 或唯一历史推导，缺失则拒绝。
  不使用 latest 补缺，不自动升级 struct，失败不尝试另一业务规则。每对象/相邻边独立 UpgradeContext 含 ObjectId 及完整 Source/TargetObjectLayout；Schema 访问器仅适用于 durable owner。
  已有非泛型二参方法通过三参 adapter 调用，声明工具依赖则必须改为三参。Context 不含对象图读取或 ID 分配。
- `UpgradeDependency` 按 provider 局部 key 与两端声明 ID/FieldId 选择 exact 槽；`ValueUpgradeRuleSet` 显式选择业务规则，
  与定义一起进入同一冻结 snapshot。`GetValueUpgrade<A,B>(key)` 只返回该作用域的预绑定工具，未知 key/错 CLR 类型在调用时拒绝。
  全链声明依赖缺失/歧义/错完整槽在该对象首次 callback 前拒绝；显式候选失败不回退，KeepExact 必须启用且零候选、完整槽相等。
  子工具继承当前 owner 信息，按调用复用相同子计划以保留 DAG 共享，snapshot 不缓存带 ObjectId 的 Context/委托。
  SG `DurableValueUpgrade` 通过定义 ID/两端版本声明同一 inline family 的规则，可组合开放 Pair；仅支持同编译规则 marker。
  Runtime metadata 可明确指定 builtin/引用/闭合 nominal 模式及完整 expected 槽。保留 history 的值规则无需旧领域类型，
  也可在无当前 Durable 声明时生成登记材料；这不授予已删除引用对象族的 current Normalize 能力。
- AddRoot 登记根；BeginCapture(models) 冻结 exact CLR Type/model 目录，CaptureObject 逐边校验声明约束，
  先分配 ID/登记再排队；Seal 用增长队列捕获可达对象，子对象不加入根列表。未知实际派生类型明确拒绝。
  Accept/Discard 只是内存候选协议。ID 单调分配、失败可烧号；退役实例映射清理不回收数字。
  空串 Capture/读取两端统一 Empty，非空 string 保留引用身份。
  现有多根 Capture 是内部能力/机制见证；每份产品图仍选一个非空 durable 根。EventHistory 工作副本允许同 exact 类型 State 根替换及已登记的异构 Event 根。
- CaptureSession.Prepare 自动使用 Current，完整预检 exact Schema/DTO/稳定 binding 后编码；全部 live Base 提前生成，
  existing class/array/List/Dictionary 调用融合 Delta、existing string 为 unchanged。结果只标识内存 Previous/Candidate，不带磁盘地址。
  重复准备与失败不安装或放弃候选、不烧号；临时 guard 拒绝会话重入。capture-only 登记仍有效，缺 binding 仅 Prepare 拒绝。
  跨 exact layout/DTO/binding 或数组 shape 不匹配拒绝，不自动降级 BaseOnly；Persistence 的 CapturedRevisionPlanner 统一映射结果，调用方仍负责 exact Parent 对应。
- SG RegisterReaders 显式登记一个模型族的全部可用 Vn；StateReaderRegistry 同 binding 实例幂等，
  同 key 另一实例拒绝，读取开始复制固定索引。Schema 日志不包含可执行 reader，完全移除的模型族仍拒绝。
  Runtime typed 循环完成整链后才装箱，字段 body 保持静态绑定；无程序集扫描或一般 TypeCodec。
- RevisionDecoder.Read 读取指定 Revision 全部 live 行，逐对象匹配完整 ObjectLayout 后解码，由 exact reader
  VisitReferences 验证 string/class/array/List/Dictionary 引用；class nominal 约束按 stored Schema 祖先判断，数组与 List 要求 exact nominal 类型。
  晚期失败不返回部分结果，不要求全批 body 零调用。
  DecodedRevision 保留 stored-exact DTO、查询地址及每 ID 唯一 string 实例，关闭 Store 后仍可使用；
  无 roots/领域实例/Upgrade，不是 CaptureSession.Current，不能直接作为已加载的可编辑基线。
  RevisionReadSession 封闭同资源和冻结目录的一次操作，以 `(ObjectId, head)` 缓存 owned 行/reader；
  每视图仍物化实际 head map 并完整验证引用/Dictionary lookup，不缓存 Normalize、业务 Upgrade 或工作区。
- StateModelRegistry 显式登记稳定 SG Model，Repository 在 Open 时固定 family、exact CLR Type 与 reader 索引；
  创建/恢复/读取共用同一模型环境；独立低层入口仍可自行取得 snapshot。
  同 exact CLR Type 的其他模型原子拒绝。durable exact-current Normalize 保留值/ID 并附加 current preparation，
  不调用历史转换委托；模型缓存仍复核实时 Schema 权威。传统非泛型入口的可选普通静态 UpgradeStateVnToVnPlus1
  按相邻版本转换完整 leaf DTO，不重复升级祖先。已声明边逐一强类型检查；缺边仅阻止需要该边的 current Load。
  GraphReader 为独立读取与 LoadedWorld/工作区恢复共用管线：先完整 exact 解码、再升级全部 source 行，按 current Schema 重新校验全部引用；
  从所选 durable 根迭代求可达闭包，全部可达 class/array/List/Dictionary 实例分配并登记 string 后才 Hydrate。分配必须 exact、非空、彼此不同，Empty 例外。
  内部仅保留 current 冻结状态比较基线及 source ObjectLayout/完整 membership，升级仍 live 必须 Base。
  不可达 source 仍须解码/归一化/验证，但不要求其 current 类型可以 Allocate；历史 ancestry 不能用 current CLR 反推。
- GraphResources 独立拥有 schemas.rbf/state/，严格只读打开不写盘、不修尾，不拥有业务 head。
  GraphReader.ReadPair 各自 Decode/Normalize/验证后，在同 ID/head、current model/完整布局、未重写且有完整状态相等 proof 的候选中，
  沿反向引用剔除依赖非候选者；剩余只读闭包共享 Allocate/Hydrate，稳定环可整体复用，changed-child 会使 owner 分裂。
  string 直接复用同 key 的解码实例；不同 ID 的等值非空串不 intern。只读结果不导入可编辑基线。
  两边完整成功才交付，无跨图实例身份承诺；缺 proof 保守不共享，真实错误传播，比较优化见路线图。
  WorldWorkspace.Stage(nextState) 发布后才安装原候选/新根；StageSnapshot 使用已提交 State 的 DTO/身份和 cursor，完成后 Discard，不清除 State 的升级重写义务。
  快照 map Base 只列候选，保留实际 local Base/Delta 与 exact Parent external heads；后继 State 仍相对前 State；快照不为闭包外 State 成员编码 Removes。
- Repository 独占 repository.lock，拥有 schemas.rbf/state/ 与 journal/；每仓库最多一个活动 BranchCheckout。
  CreateBranch(initialState) / CreateBranchFromEvent(initialEvent) 先发布真实非空 S/E 再交付；不暴露空 head。
  CommitEvent/CommitState 自由排列；Event 不推进 State 基线或接受其 live 身份。
  Journal Parent 指向提交前精确 Head；图 Parent 指向其自身或祖先中最近 State，无 State 时为 null。
  Checkout 保持精确 Head，只恢复最近 State 及完整来源；纯 Event 前缀交付 State=null、全新空 CaptureSession。
  既有 State 损坏或缺模型能力会失败，不回退旧 State 或假装无 State。事件内容由应用显式 ReadEvent。
  State 根按实际模型选择，可跨类型替换，成功后安装原实例；已有 child 升根、旧 root 降为 child 保持 ID，不可达成员退出目录。
  冷 Event-only 历史不导入 Event ID；不同 Revision 的对象数字 ID 可以重合，身份须在具体 Revision 内解释。
  CheckpointAddress 由本次打开签发，相等性为 owner+Journal 位置；跨 repo/重开旧地址拒绝，裸 RevisionAddress 只作诊断。
  ReadEvent/ReadState 独立物化，ReadPair 按显式只读共享合同使用；ref-only CreateBranch 和 Move 须先关闭活动工作副本。
  提交按 Schema/State → Journal EventFrame → branch ref 的持久屏障发布，发布前准备安装，发布后不重新 Capture 或运行模型回调。
  GraphCommitException 区分 NotPublished / Unknown / Published；NotPublished 也须检查 IsFaulted，故障后 dispose/reopen，无透明重试或领域修改回滚。
  严格重开校验全部物理 Journal（含 orphan）、逻辑 Parent 与最近 State 图 Parent、引用 Revision/类型头及根成员；只读不创建、flush 或修尾。
  恢复保证仍限正常关闭、进程中止和明确 I/O 故障；无 OS crash/power loss、目录元数据或完整后缀外部删除检测保证。
- 迁移壳可保留旧族 reader/Upgrade，退出 current World 可达闭包后不分配；仍完整验证 source 行。
  传统生成路径不为完全删除的族生成 reader；Family 路径可按 retained history 生成 state-only exact reader，
  但 editable Load 仍要求每个 source 引用对象族有 current/migration CLR 模型和 Normalize。只承诺读已 Remove 该族的新 Revision 才能删除其恢复能力，
  仍支持旧 Revision 则须保留相应 reader/Normalize。见 DB-036 H1 真实包回归。
- 内部 LoadedWorld 仅保留机制见证/低层测试桥接，不是下游保存 API。PrepareNew 从普通新建图生成无 Parent 的完整冻结计划，返回 WorldId；可持久登记 Schema/表示，
  不追加 State 或执行 State 屏障/发布，不安装基线。单根非空且要求 exact 已登记 CLR 类型。
- LoadedWorld.Prepare 固定 Parent/WorldId，返回 owned StateRevision，成功或失败均释放临时 Capture，
  不推进基线。此内部低层路径由宿主 Append 后重新 Load；产品同实例连续保存使用 BranchCheckout。Schema/表示登记不代表发布。
  Empty 反向映射选择最小 source ID，但基线槽保留旧 ID，首次 Capture 形成真实 Delta/Remove。
  分配从完整 source live max+1 起，只承诺会话内单调；uint 耗尽不阻止已有对象保存。
  恢复的可达 durable 实例身份导入同一捕获会话；child-only 修改不改变 owner ID 槽，
  断开最后根路径后整个循环岛由完整 source − candidate 得到 Remove，旧 Revision 不受影响。
- PrepareDeltaBody 每槽比较一次形成位图，再静态写变化值；结果含 HasChanges 和可复用 raw body，裸 Delta body 大小可直接取长度。
  List 匹配另用静态 StateEquals 试探，仅实际变化的配对调用子 PrepareDelta；浮点按位、decimal 按完整 GetBits、DateTimeOffset 按 EqualsExact、ObjectId 按值、inline 逐持久字段比较，忽略 padding。
  策略 D 还须计入对象 envelope，不能直接以裸 body 大小代替。
  PrepareBaseBody 对每版 DTO 复用 WriteBaseBody；全部 live Base 提前准备，决策后复用 bytes，性能优化留待 MVP 后。
  B 为完整 Base payload 精确值，D 仅对未定文件距离按 5 字节上界计量（超额 0..4）；H 仍是原记录实编码。
  ApplyDeltaBodyVn 只处理同 Vn；不证明 prior 身份，之后仍须对完整 DTO 验证引用。
- SchemaStore 借用独占的专用 IRbfFile，`schemas.rbf` 只接受 SCB1 v2 的统一闭合目录批次，拒绝 v1 与旧 SGB1/RPB1。
  class Schema 记录本身就是对象表示；inline Schema、数组、List 与 Dictionary 共用从 2 起连续单调的 UInt32 编号，0 无效、1 固定 string。
  inline 编号只作为元数据依赖，不能用于对象 Base。编号不回收、不跨仓库解释；shape/对象实例不属于表示身份。
  SchemaKey 为派生查询/冲突索引，不单独编码；Count 只统计用户 Schema，含 inline。完整闭包检查不能因已有 ID 而跳过。
  全部输入、完整 base/inline 闭包、名义 kind/arity、ID/单帧容量预检后，一次追加/flush，最后安装全部索引并交付 ID；幂等登记不追加。
  exact base/inline 使用先前节点 ID，按依赖顺序恢复；字段 tag15 引用仍携带 closed Named/Array/List/Dictionary TargetType，
  不绑定目标版本或形成 exact 注册依赖，nominal 自环/互环允许。Schema DAG depth≤256，数组外壳不额外占深度。
  List 使用目录 kind=4、nominal 构造码=8；数组/List 的 exact inline 元素外壳不增加 Schema DAG 深度。
  `.dgschema` 与 manifest 新写 v9，严格读取原 v1–v8 语法并保留已接受 history 的文件与 hash；List/Nullable/Dictionary 分别需 v5/v6/v7，Guid/decimal/TimeSpan 叶子需 v8，DateOnly/TimeOnly/DateTimeOffset 需 v9，含 nominal 实参也递归检查。
  字段 tag 1–16 不变，history-only 参数 tag=17，Nullable tag=18/nominal 构造码=9；
  Nullable 内部 inline 依赖仍用先前目录 ID，不新增独立 Nullable 目录行。ValueSchema 统一暴露包装内依赖，exact DAG 深度不多算包装层。
  nominal 约束改变属于 owner Schema 改变，目标自身升版则不传播 owner 版本。
  严格重放全部帧/CRC；坏尾、tombstone、未知格式拒绝且不自动截断。写入不确定后 faulted，须重开；
  可写非空重开先 flush 再交付，readonly 不确认新屏障。尚无 Schema 分段、联合版本目录或自动修复。
- Persistence 内部 BaseObjectBodyCodec 为 raw Base body 加 v4 类型头：格式版本 + canonical RepresentationId，返回 `EncodedBaseObjectBody`。
  统一经 SchemaStore 取得完整 ObjectLayout；string 固定 ID 可无目录解析。仅支持 v4，每个解码结果都有 RepresentationId，旧格式明确拒绝。
  State 类型头不再编码 SchemaKey/TypeExpr/数组元素描述，描述语法集中在目录一侧；当前 DTO/SG/body/history/Upgrade 合同不变。
  SchemaStore.ResolveReader 使用本次操作的冻结目录，完整匹配布局；不全局缓存另一个 snapshot 的 CLR reader。
  Delta 沿终止 Base 继承 exact 表示，仍为裸 body。TypedObjectVersionReader 在 callbacks 前匹配持久完整布局，逐 body 全消费；string 拒绝 Delta。
  它保留单对象显式入口，与 RevisionDecoder 共用读取规则；不执行 Upgrade。
  Schema/表示登记帧是共享元数据，不摊入对象 B/D/H；Base 的 ID 实际字节宽度计入 B。
- CapturedRevisionPlanner 先核对 Previous/Parent、完整 prior ID 集合及所有 survivor 的完整 Base 布局，
  包括 NoChange；再批量登记全部 current 表示、包装 Base 并调用原 planner。Schema/表示登记可持久生效，
  但该方法不追加 State/发布/Accept，也不证明 DTO 内容与 Parent 一致；合法迁移由受控 LoadedRevisionPlanner 路径产生 BaseOnlyUpdate。
- ObjectRevisionPlanner 只读 exact Parent，校验完整 post-live rows 的新旧分类/prior；独立入口对 NoChange/Delta Update
  读取链 H，受控 Loaded 入口复用同 Store/SchemaStore 的已验证来源及精确 H，仍核对全部 source head，含待删除行。
  BaseOnlyUpdate 不读取旧内容链。无 Parent 输出 map Base；普通有 Parent 的保存按目录成本选择 Base/Delta，后者显式编码 Removes。
  typed producer 负责内容/Schema/基线对应；planner 不调用 Append/Accept，结果可作为显式 Parent 的分支追加。
  冷解码与 ReadSession 缓存同时携带 head/H；PrepareInstall 在实际地址确定后按本次 payload 完成私有下一基线，
  发布后才安装。Event/Discard 不推进 State，synthetic DTO 视图没有热保存来源资格，详见 DB-069。
- Storage 的 ObjectHeadMap 与对象内容的 Base/Delta 独立组合。ReadObjectVersionChain 逐条核对
  prior 等于该记录 exact Parent Revision 选定的对象 head，要求 direct local record；Base 截断内容链，H 随之重置。
  Append 只预检直接 edge；完整 map 的 external heads 仍是浅声明，不认证全局实体历史。
  ReadObjectBaseBody 仍只接受 Base head，不回退 parent 补内容；wire v3 拒绝 v1/v2。
  H 含 kind/prior/length/body，不含 ObjectId/membership/共享 Frame；不是总冷读 I/O。
  普通 Store 默认以 8 MiB 统一预算复用 owned Revision/完整 map，0 禁用驻留；Append 不 seed 读缓存。
  冷链 H 来自 wire 读回；已提交基线可按实际文件范围精确增量计量，独立 chain/map 遍历统计用于保存成本归因。
  Store.Dispose 释放缓存但不关闭借用底层，已交付值仍可读；使用纪律与验收见 [DB-067](../docs/design-branches/0067-owned-revision-read-cache-design.md)。
- 数组对象支持 SZ/rank 2–4 与完整已有槽闭包：标量、string/class ID、inline/generic struct、递归数组。
  FrozenArrayState 拥有 shape 与元素 buffer；当前投影用静态 ref 循环，historical reader 不要求旧领域 struct CLR 类型。
  同布局稀疏 Delta 使用 row-major 索引与子 PrepareDelta；同实例保持 ID，替换/不可达继续遵循会话身份和 Remove 规则。
  数组独立 owner 显式选择元素 Upgrade 规则集，直接 source→current 端点预绑定，不自动搜索相邻规则路径；
  空数组也验证升级能力，共享数组只归一化一次，保持 ID/shape 并强制 Base。详细合同与证据见 DB-043。
  非零下界、非 SZ rank-1、rank > 4、数组协变与 unsupported 元素拒绝；object 内部入口不授予 object/interface 通配槽。
  已知成员的 SG body 静态绑定字节原语；PrimitiveSlotCodecs 只在测试工具中。
- exact BCL List<T> 复用完整槽闭包，可与数组、泛型 class/struct 递归组合；接口字段、List 子类及其他 BCL 容器仍拒绝。
  ListLayout 只含 exact 元素槽和 codec 版本，Count 属于 owned FrozenListState；Capacity 不持久化。
  同实例 resize 保持 ObjectId/表示 ID；Base=count+元素，codec 2 Delta=newCount+Copy/New/CopyAndPatch 区间，旧 codec 1 拒绝。
  Prepare/Apply 保持 prior/candidate 独立；source 可重复/倒序，只读 prior，区间内使用严格局部稀疏子 patch。
  UseListDeltaAlgorithm 选择 Position/LocalResync/BoundedMyers/Adaptive，默认 Adaptive；配置冻结到会话模型 snapshot，不进入布局/格式/表示 ID。
  三种纯 matcher 不编码且拒绝 Adaptive；Adaptive 在 writer 层保留完整 Local，并按停滞信号竞争独立有界 Myers，只接受严格更短 body。
  限长按实际输出在元素调用边界检查，不能视作峰值内存上限；整个 NoChange 判定独立于搜索预算。性能选择与保留边界见路线图 §3.2。
  UseListElementUpgrades 独立于数组规则选择，空 List 同样预绑定；每个共享 List 归一化一次，保留 ID/count 并强制 Base。
  UpgradeContext.ListCount 随子工具继承，不复用 ArrayShape；历史 reader 不依赖旧领域 struct CLR 类型。
- 实验性 exact BCL Dictionary<TKey,TValue> 采用两个完整槽，codec 1；TypeExpr/共享模式构造码 10，history `d(key,value)`，目录 kind=5。
  TValue 复用完整现有槽闭包；key 支持已有标量/enum、普通/generic Durable struct 和受支持引用类型。
  根 Nullable、未支持类型、子类/接口字段仍拒绝；复合 Key 的 Nullable/string/引用成分复用已有槽投影。
  comparer 是 frozen/Base body 的对象级标签 0–5，不进入 Layout/RepresentationId；0–3 标准选择保持，
  4 CurrentDefault 使用当前 CLR Default/IEquatable，5 Application 使用闭合 Dictionary 的当前应用选择。
  UseDictionaryComparer<K,V> 优先于唯一 UseDictionaryComparerResolver；只实际 Application Capture/Allocate 才解析，空实例亦然，
  缺失/错类型/异常不得回退。snapshot 复制配置、按闭合类型缓存并共享成功 comparer，不能冻结用户捕获的外部状态。
  4/5 用透明转发包装保留 Load→Capture→Commit 的模式；不保证 Comparer 实例身份，同型 Application 只恢复一种当前规则。
  owned 双槽条目语义无序；容量/枚举重排无伪变化，hash/bucket 不持久化。key 和 value 引用均参与共享、循环及可达性。
  Delta 以 canonical key Base bytes 寻址 Remove/PatchValue/Add；键表示不变时每 value 只调用一次融合 PrepareDelta。
  不用领域 comparer 配对、不依赖 entry ordinal；相同查找键但不同持久 ID/bits 使用 Remove+Add，prior 不被修改。
  metadata Validate 不编码 key；临时 key bytes/hash索引只在操作内使用，完整 bytes 决定相等，性能复用留待实测。
  所有模式查语法、根 null、canonical 重复和完整引用，包括不可达行；零宽完整 key 的多条计数在分配前拒绝。
  0–3 保留框架标准 lookup 校验，包括 string 内容与 Empty 身份冲突；4/5 的 exact/Normalize 不执行当前业务比较，
  只在可达图的 TryAdd 拒绝当前碰撞。历史可读不等于当前规则可接纳，失败不交付部分 World。
  key inline/string 和引用身份在填充时可用，引用目标内容可能未就绪；比较不可依赖其内容或未恢复的 Transient。
  UseDictionaryKeyUpgrades/UseDictionaryValueUpgrades 分别选择显式规则；完整双端依赖和全部工具在首次 callback 前预检，空容器亦如此。
  每个共享字典仅 Normalize 一次，保持 ID/count/comparer；升级碰撞拒绝，仍 live 的布局升级强制 Base。
  UpgradeContext.DictionaryCount 只用于 Dictionary owner，子工具继承；历史 enum/struct key/value DTO 不依赖旧领域 CLR。
- Generator 中未注册的 graph operations probe 和 tests 中 logical graph R1–R3b 是机制见证，不能算产品通用图能力。

产品依赖为 Persistence → Runtime + Storage，二者分别复用 Serialization；Storage 另用 RBF substrate。
DurableGraph runtime 也引用 Serialization，单一 runtime PackageReference 可取得传递依赖。

## 按任务定位证据

| 准备修改 | 先查源码/测试，再按需读合同 |
|---|---|
| 热保存基线、head/H 与增量安装 | [DB-069](../docs/design-branches/0069-incremental-save-baseline.md)、[NormalizedRevision](DurableGraph.Persistence/NormalizedRevision.cs)、[基线回归](../tests/DurableGraph.Persistence.Tests/WorldWorkspaceStorageBaselineTests.cs)、[前后测量](../tests/DurableGraph.Persistence.Tests/HotSaveMeasurementTests.cs) |
| IDurableObject、record class 与旧基类迁移 | [DB-068](../docs/design-branches/0068-record-class-model-slice.md)、[准入](DurableGraph/IDurableObject.cs)、[record 图回归](../tests/DurableGraph.Tests/RecordClassGraphTests.cs)、[真包三代/旧包迁移](../experiments/PackageConsumerProbe/RecordClassConsumer/README.md) |
| Revision/map 读缓存、双数组字典与 Store 寿命 | [DB-067](../docs/design-branches/0067-owned-revision-read-cache-design.md)、[缓存](DurableGraph.Storage/StateRevisionReadCache.cs)、[预算与失败测试](../tests/DurableGraph.Storage.Tests/StateRevisionReadCacheTests.cs) |
| 默认保存、事件快照和失败恢复用法 | [DB-065](../docs/design-branches/0065-event-history-consumer-contract-slice.md)、[包示例](../experiments/PackageConsumerProbe/EventHistoryRecoveryConsumer/README.md)、[同源恢复测试](../tests/DurableGraph.Persistence.Tests/EventHistoryConsumerRecoveryTests.cs)、[README 原文验证](../experiments/PackageConsumerProbe/Run-ReadmeQuickStartProbe.ps1) |
| 跨程序集继承、hidden 字段与基类状态投影 | [DB-061](../docs/design-branches/0061-cross-assembly-inheritance-slice.md)、[Runtime 投影](DurableGraph/Runtime/Binding/StateBaseProjection.cs)、[SG 当前投影](DurableGraph.Generator/DurableSchemaGenerator.GenericProjection.cs)、[真实包](../experiments/PackageConsumerProbe/InheritanceLibraryConsumer/README.md) |
| 固定外部 inline、只读模板归属与构建闭包 | [DB-060](../docs/design-branches/0060-cross-assembly-inline-history-slice.md)、[SG 导入导出](DurableGraph.Generator/DurableSchemaGenerator.SchemaExports.cs)、[Build 依赖](DurableGraph.Build/SchemaHistoryTool.References.cs)、[真实包](../experiments/PackageConsumerProbe/InlineLibraryConsumer/README.md) |
| 跨程序集 nominal/动态参数、显式 Family 与稳定消费者 DLL | [DB-059](../docs/design-branches/0059-cross-assembly-model-composition-slice.md)、[metadata 分类](DurableGraph.Generator/DurableSchemaGenerator.CrossAssembly.cs)、[真实包](../experiments/PackageConsumerProbe/CrossAssemblyConsumer/README.md) |
| DateOnly/TimeOnly/DateTimeOffset、完整 offset 与 history v9 | [DB-058](../docs/design-branches/0058-temporal-scalar-value-slice.md)、[静态值操作](DurableGraph/Runtime/State/TemporalScalarStateValues.cs)、[真实包](../experiments/PackageConsumerProbe/TemporalScalarConsumer/README.md) |
| Guid/decimal/TimeSpan、非连续 builtin tag 与 history v8 | [DB-057](../docs/design-branches/0057-bcl-scalar-value-slice.md)、[静态值操作](DurableGraph/Runtime/State/BclScalarStateValues.cs)、[真实包](../experiments/PackageConsumerProbe/BclScalarConsumer/README.md) |
| record struct、backing storage 分类与字段投影 | [DB-056](../docs/design-branches/0056-record-struct-state-slice.md)、[字段分类](DurableGraph.Generator/DurableSchemaGenerator.Records.cs)、[投影](DurableGraph.Generator/DurableSchemaGenerator.GenericProjection.cs)、[真实包](../experiments/PackageConsumerProbe/RecordConsumer/README.md) |
| Dictionary 内容、复合 Key、当前 comparer 与历史双槽升级 | [DB-055](../docs/design-branches/0055-composite-dictionary-key-design.md)、[body](DurableGraph/Runtime/Containers/DictionaryStateReader.cs)、[current binding](DurableGraph/Runtime/Containers/DictionaryObjectBinding.cs)、[升级](DurableGraph/Runtime/Binding/StateBindingContext.DictionaryUpgrade.cs)、[真实包](../experiments/PackageConsumerProbe/CompositeDictionaryConsumer/README.md) |
| enum 表示、外置投影与历史升级 | [DB-053](../docs/design-branches/0053-enum-inline-state-slice.md)、[生成接缝](DurableGraph.Generator/DurableSchemaGenerator.Enums.cs)、[当前模板校验](DurableGraph/Runtime/Binding/StateDefinitionBinding.cs)、[真实包](../experiments/PackageConsumerProbe/EnumConsumer/README.md) |
| Nullable 值槽、history 与显式升级提升 | [DB-052](../docs/design-branches/0052-nullable-value-slot-slice.md)、[静态值操作](DurableGraph/Runtime/State/NullableStateValues.cs)、[完整 child 布局](DurableGraph/Schema/NullableValueLayout.cs)、[真实包](../experiments/PackageConsumerProbe/NullableConsumer/README.md) |
| List 区间 Delta、匹配与配置 | [DB-049](../docs/design-branches/0049-list-range-delta-and-matcher-trial-slice.md)、[matcher](DurableGraph/Runtime/Containers/ListDeltaMatcher.cs)、[reader/body](DurableGraph/Runtime/Containers/ListStateReader.cs)、[重放实验](../experiments/ListDeltaReplayProbe/README.md) |
| List 内容、冻结与历史元素 Upgrade | [DB-047](../docs/design-branches/0047-list-content-object-slice.md)、[当前投影](DurableGraph/Runtime/Containers/ListObjectBinding.cs)、[List 升级](DurableGraph/Runtime/Binding/StateBindingContext.ListUpgrade.cs)、[真实包](../experiments/PackageConsumerProbe/ListConsumer/README.md) |
| 统一闭合目录、持久表示 ID 与 Base 头 | [DB-046](../docs/design-branches/0046-unified-schema-catalog-slice.md)、[目录 codec](DurableGraph.Persistence/SchemaCatalogWireCodec.cs)、[SchemaStore](DurableGraph.Persistence/SchemaStore.cs)、[目录重放测试](../tests/DurableGraph.Persistence.Tests/SchemaCatalogReplayTests.cs)、[表示集成测试](../tests/DurableGraph.Persistence.Tests/RepresentationIntegrationTests.cs) |
| 数组对象、共同引用入口与元素 Upgrade | [DB-043](../docs/design-branches/0043-vector-array-object-slice.md)、[对象布局](DurableGraph/Schema/ObjectLayout.cs)、[数组绑定](DurableGraph/Runtime/Containers/ArrayObjectBinding.cs)、[历史 reader](DurableGraph/Runtime/Containers/ArrayStateReader.cs)、[数组 owner Upgrade](DurableGraph/Runtime/Binding/StateBindingContext.ArrayUpgrade.cs) |
| 引用槽与对象身份包装 | [DB-041](../docs/design-branches/0041-object-id-state-representation.md)、[ObjectId](DurableGraph/ObjectId.cs)、[静态引用操作](DurableGraph/Runtime/State/BuiltinStateValues.cs) |
| 可组合值 Upgrade / 规则集 / Context 子作用域 | [DB-039](../docs/design-branches/0039-composable-value-upgrade-design.md)、[值绑定](DurableGraph/Runtime/Binding/StateBindingContext.ValueUpgrade.cs)、[SG 属性](DurableGraph.Generator/DurableSchemaGenerator.ValueUpgrades.cs)、[历史包](../experiments/PackageConsumerProbe/ValueUpgradeConsumer/README.md) |
| 泛型/历史绑定/UpgradeContext | [DB-038](../docs/design-branches/0038-generic-schema-state-and-binding-design.md)、[绑定上下文](DurableGraph/Runtime/Binding/StateBindingContext.cs)、[生成模板](DurableGraph.Generator/DurableSchemaGenerator.GenericState.cs)、[三代历史包](../experiments/PackageConsumerProbe/GenericConsumer/README.md) |
| inline struct/嵌套 DTO/Schema DAG | [DB-037](../docs/design-branches/0037-inline-struct-state-slice.md)、[生成值 helper](DurableGraph.Generator/DurableSchemaGenerator.InlineState.cs)、[真实生成图](../tests/DurableGraph.Tests/InlineStructGraphTests.cs)、[历史包](../experiments/PackageConsumerProbe/InlineStructConsumer) |
| 独立图工作区、资源与读取 | [DB-062](../docs/design-branches/0062-independent-graph-workspace-slice.md)、[工作区](DurableGraph.Persistence/WorldWorkspace.cs)、[资源](DurableGraph.Persistence/GraphResources.cs)、[读取](DurableGraph.Persistence/GraphReader.cs) |
| EventHistory、分支发布与恢复 | [DB-063](../docs/design-branches/0063-event-history-journal-slice.md)、[Repository](DurableGraph.Persistence/Repository.cs)、[工作副本](DurableGraph.Persistence/BranchCheckout.cs)、[集成测试](../tests/DurableGraph.Persistence.Tests/EventHistoryRepositoryTests.cs) |
| 操作内 exact 解码复用与只读闭包共享 | [DB-064](../docs/design-branches/0064-shared-revision-decoding-design.md)、[读取会话](DurableGraph.Persistence/RevisionReadSession.cs)、[引用闭包](DurableGraph.Persistence/GraphReader.cs)、[共享测试](../tests/DurableGraph.Persistence.Tests/SharedGraphReaderTests.cs)、[Checkout 与续写](../tests/DurableGraph.Persistence.Tests/SharedEventHistoryTests.cs) |
| 领域引用图/首次准备 | [DB-034](../docs/design-branches/0034-durable-reference-graph-batch.md)、[真实生成图冷读](../tests/DurableGraph.Tests/PersistedReferenceGraphTests.cs)、[双视图与失败测试](../tests/DurableGraph.Persistence.Tests/LoadedReferenceWorldTests.cs) |
| Schema、DTO、静态 body | [Generator tests](../tests/DurableGraph.Tests)、[DB-019](../docs/design-branches/0019-schema-ancestry-implementation-slice.md)、[DB-022](../docs/design-branches/0022-versioned-state-dto-capture.md)、[DB-023](../docs/design-branches/0023-scalar-schema-dto-slice.md) |
| 同版 DTO Delta 准备与应用 | [DB-027](../docs/design-branches/0027-generated-same-schema-delta-body-slice.md)、[body tests](../tests/DurableGraph.Tests/FusedDeltaBodyTests.cs)、[history/Capture tests](../tests/DurableGraph.Tests/FusedDeltaHistoryTests.cs) |
| Capture 与 string 读取 | [DB-024](../docs/design-branches/0024-reference-capture-and-reusable-object-ids.md)、[DB-025](../docs/design-branches/0025-string-object-decoding-slice.md) |
| 持久 Schema、Base 引用及 typed 冷读 | [DB-031](../docs/design-branches/0031-persisted-object-type-envelope-slice.md)、[SchemaStore](DurableGraph.Persistence/SchemaStore.cs)、[注册 tests](../tests/DurableGraph.Persistence.Tests/SchemaStoreTests.cs)、[typed reader](DurableGraph.Persistence/TypedObjectVersionReader.cs) |
| current 升级、readonly 恢复与续写 | [DB-033](../docs/design-branches/0033-upgrade-restore-resave-batch.md)、[SG tests](../tests/DurableGraph.Tests/GeneratedStateModelTests.cs)、[加载 tests](../tests/DurableGraph.Persistence.Tests/LoadedWorldTests.cs)、[冷重开集成](../tests/DurableGraph.Tests/LoadedWorldGeneratorTests.cs) |
| 完整历史 DTO 目录与 reader 登记 | [DB-032](../docs/design-branches/0032-exact-revision-decoding-slice.md)、[RevisionDecoder](DurableGraph.Persistence/RevisionDecoder.cs)、[生成冷读 tests](../tests/DurableGraph.Tests/DecodedRevisionGeneratorTests.cs) |
| 异构图统一准备内容 | [DB-030](../docs/design-branches/0030-captured-object-preparation-slice.md)、[runtime tests](../tests/DurableGraph.Tests/CapturedGraphPreparationTests.cs)、[生成 tests](../tests/DurableGraph.Tests/GeneratedCapturePreparationTests.cs) |
| 对象内容、地址与重开读取 | [Storage tests](../tests/DurableGraph.Storage.Tests)、[DB-026](../docs/design-branches/0026-raw-base-object-content-slice.md)、[typed 文件见证](../tests/DurableGraph.Tests/RawBaseStorageGeneratorTests.cs) |
| 持久 Delta、prior 链与 H | [DB-028](../docs/design-branches/0028-persisted-object-delta-chain-slice.md)、[链测试](../tests/DurableGraph.Storage.Tests/ObjectVersionChainStoreTests.cs)、[真实 SG 冷重开](../tests/DurableGraph.Tests/PersistedDeltaChainGeneratorTests.cs) |
| 已准备内容、Base/Delta 策略与 Revision | [DB-029](../docs/design-branches/0029-prepared-object-revision-planning-slice.md)、[规划器](DurableGraph.Persistence/ObjectRevisionPlanner.cs)、[策略实现](DurableGraph.Persistence/ReadAmplificationBaseBudgetPolicy.cs)、[策略 tests](../tests/DurableGraph.Persistence.Tests)、[DB-015](../docs/design-branches/0015-statestore-object-representation-policy.md) |
| 包、生成器消费和 history 发布 | [PackageConsumerProbe](../experiments/PackageConsumerProbe/README.md)；真实 PackageReference 验证不能由 ProjectReference 测试替代 |

代码变更后运行根 solution build 和相关 tests；包交付边界变化时按 PackageConsumer README 验证。
本文件不累积测试计数、命令日志或完成流水账；已有验证结果保留在相应施工记录。
