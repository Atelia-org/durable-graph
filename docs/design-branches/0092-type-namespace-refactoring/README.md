# DB-092：类型名与命名空间重构提案

> 2026-10-14；提案源码基线 `246b888`。状态：A／B／C／D1／D2／E1／E2／E3／D3／D4／D5 已限定验收；有限文件组织完成。D6 正在验证公开发布类型迁移；另两组候选保名，见[有限收敛](#d6-status)。
> 问题：哪些现行名称已经不能准确表达职责，如何先整理它们，为以后判断程序集和文件归属提供清楚的概念边界？
> 本文区分原提案与实际分片结果，不自行授权全部候选。当前名称、行为和包身份仍以源码为准；结果见 [A](implementation-a.md)／[B](implementation-b.md)／[C](implementation-c.md)／[D1](implementation-d1.md)／[D2](implementation-d2.md)／[E1](#e1-status)／[E2](#e2-status)／[E3](#e3-status)／[D3](#d3-status)／[D4](#d4-status)／[D5](#d5-status)，当片见 [D6](#d6-status)。

已处理默认持久化内核的历史阶段名、模型执行环境的误称，以及 Generator 的 Family 分析材料，并完成有限文件组织。
公共接入入口和已有 `Schema`／`Runtime` 分工保持；其余公开名称与程序集归属分别按现行职责收益判断，后续入口见[路线图](../../DurableGraph-research-roadmap.md)。

## 1. 范围与判断依据

调查覆盖七个产品项目、`src/Shared`，并追查对应测试、生成文本和活动消费者。五个只读工作包分别调查 Runtime/Schema/Serialization、
Persistence 外观、meta/Storage、Generator/Build/CLI，以及独立提出最小 namespace 方案；主线程核对跨包职责和关键源码后形成以下取舍。

只把以下现象作为改名理由：名称承诺了错误阶段或职责、同一词掩盖不同概念、已经没有比较对象的旧路径标签、
或者名称妨碍辨别当前协作边界。长度、文件位置、项目名不等于 namespace，以及同词出现多次，都不足以单独证明问题。

最小验收标准：推荐表中的当前符号可定位，职责有源码或调用证据；目标名不合并不同 owner、来源、角色或能力；
每片明确传播范围与可观察验收。没有证明外部所有调用者覆盖，也没有用这次静态调查声明二进制或存档兼容。

[术语表](../../DurableGraph-glossary.md)维护概念；本文只维护本次候选映射与迁移范围。
[DB-071](../0071-assembly-namespace/README.md)的当片组织迁移已完成，但其旧 raw Storage／SchemaStore 说明不能替代当前源码。

## 2. 命名空间：先复用现有边界

| 命名空间 | 本次建议 | 当前依据 |
|---|---|---|
| `Atelia.DurableGraph` | 保留公共仓库／工作副本／检查点和模型作者入口 | [Repository](../../../src/DurableGraph.Persistence/Repository.cs)、[BranchCheckout](../../../src/DurableGraph.Persistence/BranchCheckout.cs)、[登记接口](../../../src/DurableGraph/IStateModelRegistration.cs)刻意共用接入空间，所在程序集可以不同 |
| `.Schema` | 保留不可变描述与 exact layout | [DurableSchema](../../../src/DurableGraph/Schema/DurableSchema.cs)、[ObjectLayout](../../../src/DurableGraph/Schema/ObjectLayout.cs)不因名称含 State 就成为业务 State 图 |
| `.Runtime` | 保留执行绑定、捕获、静态状态操作和容器能力 | [ObjectBinding](../../../src/DurableGraph/Runtime/Binding/ObjectBinding.cs)、[StateDefinitionBinding](../../../src/DurableGraph/Runtime/Binding/StateDefinitionBinding.cs)含实际 CLR 工厂与执行能力；本轮不按子目录再分公开 namespace |
| `.Persistence` | 保留持久化集成；接纳去阶段名后的默认内核 | [RepositoryResources](../../../src/DurableGraph.Persistence/RepositoryResources.MetaBootstrap.cs)是资源与共同发布 owner；[DecodedRevision](../../../src/DurableGraph.Persistence/DecodedRevision.cs)和工作区连接准确存储视图与 Runtime |
| `.Persistence.MetaBootstrapP0` | B／C 已退出该 CLR namespace；[E3](#e3-status)将 16 份源码并入 Persistence 根 | B 迁移 14 份，C 收尾员工与固定绑定两份；当前 [StoredGraphKind](../../../src/DurableGraph.Persistence/GraphPersistenceContracts.cs)、[BranchSaveSession](../../../src/DurableGraph.Persistence/BranchSaveSession.cs)承载 Event、State、Meta 与分支保存；P0 不再作为产品分层或源码目录 |
| `.Serialization`／`.Storage` | 保留；类型按下表逐项判断 | [PreparedBaseBody](../../../src/DurableGraph.Serialization/PreparedBaseBody.cs)、[ObjectVersionPayloadCodec](../../../src/DurableGraph.Storage/ObjectVersionPayloadCodec.cs)仍是共同字节契约 |
| `.Generator`／`.Build`／`.SchemaHistory` | 保留工具阶段和 source-linked 文本协议 | [Generator 项目](../../../src/DurableGraph.Generator/DurableGraph.Generator.csproj)、[Build 项目](../../../src/DurableGraph.Build/DurableGraph.Build.csproj)的目标框架与运行方式不同；[Shared 协议](../../../src/Shared/SchemaHistoryReferenceProtocol.cs)不是新运行时包 |

不把整个旧 P0 包改成 `.Meta`：其中业务读写和物理目录不只服务 meta。
暂不增加 `.Engine`／`.Reading`／`.Saving` 等子空间：当前收益主要来自准确的类型名，新增层级没有得到独立协作边界的证据。
后续若有真实独立消费或替换需求，再比较 namespace、文件归属和程序集依赖；这三种边界分别判断。

## 3. 默认持久化内核：推荐映射

本节左侧是提案基线 `Atelia.DurableGraph.Persistence.MetaBootstrapP0` 中的旧名，均为 `internal`；右侧普通内核由 B、固定员工／登记／固定表由 C 采用，进入现有 `.Persistence`。
C 的生成与存档资格另见其记录。表保旧名便于迁移检索，不能做全局 `P0` 文本替换。

### 3.1 保存、来源和物理图

| 当前名 → 推荐名 | 职责与证据 | 必须保留的区别 |
|---|---|---|
| `P0Repository` → `RepositoryStorageFacade` | [声明](../../../src/DurableGraph.Persistence/RepositoryStorageFacade.cs)转发 Store、head、prepare/commit；默认借用，Create/Open 工厂也可拥有 RepositoryResources | façade 不是第二个发布 owner；不命名为 Borrowed 或 RepositoryCore，不顺便删壳 |
| `P0Session` → `BranchSaveSession`；`P0SessionSave` → `PreparedBranchSave` | [声明](../../../src/DurableGraph.Persistence/BranchSaveSession.cs)保存分支 State 工作区与 catalog 意图，准备一次共同保存 | 前者有寿命内状态，后者有一次准备／提交／释放寿命；不替代公开 BranchCheckout |
| `P0BusinessWorkspace` → `BusinessPersistenceAdapter`；`P0PreparedBusiness` → `PreparedBusinessWrite` | [声明](../../../src/DurableGraph.Persistence/BusinessPersistenceAdapter.cs)包装 BusinessWorkspace，补 Store/source/S、typed Base、规划与实际安装 | 与共同 [BusinessWorkspace](../../../src/DurableGraph.Persistence/BusinessWorkspace.cs)的 Capture/身份职责不同，不能直接去前缀造成同名 |
| `P0PendingSave` → `PendingRepositorySave`；`P0SaveResult` → `RepositorySaveResult` | [pending](../../../src/DurableGraph.Persistence/PendingRepositorySave.cs)、[结果](../../../src/DurableGraph.Persistence/GraphPersistenceContracts.cs)承载业务／可选 meta／共同发布材料 | Prepare、Append、发布结果、Confirmed 后安装仍分开；名字不授发布资格 |
| `P0GraphStore` → `GraphRecordStore` | [声明](../../../src/DurableGraph.Persistence/GraphRecordStore.cs)借用 FS，追加／读取 graph、batch、head directory，检查物理与来源关系 | 不负责 VS 发布、Upgrade 或领域实例物化；不能改成上游 FrameStore |
| `P0GraphKind` → `StoredGraphKind`；`P0GraphMetadata` → `GraphEnvelope`；`P0GraphSnapshot` → `StoredGraphRevision` | [声明](../../../src/DurableGraph.Persistence/GraphPersistenceContracts.cs)分别为三种存储角色、封套、地址＋完整 heads＋来源 owner | Revision 不含已物化 CLR 图，也不一定已发布；内部 Meta 与公开业务 Event/State enum 不合并 |
| `P0PreparedGraphWrite` → `PreparedGraphWrite`；`P0GraphWire` → `GraphFrameCodec` | [写材料](../../../src/DurableGraph.Persistence/GraphPersistenceContracts.cs)、[wire](../../../src/DurableGraph.Persistence/GraphFrameCodec.cs)分别为 owned 对象记录／Remove 和 graph/batch/directory 三种帧的编码 | 不是完整交易或领域序列化器；保留全部已有关系校验、帧角色和编码 |
| `P0Batch` → `ObjectVersionBatch`；`P0ChainEntry` → `ObjectVersionChainEntry`；`P0ObjectChain` → `ObjectVersionChain` | [声明](../../../src/DurableGraph.Persistence/GraphPersistenceContracts.cs)持对象版本、准确 prior 和 batch 来源 | heads 指 batch 地址；Base→Delta 链与业务 ancestry 不混称 |
| `P0DirectoryCodec` → `ObjectHeadDirectoryCodec`；`P0DirectoryVersion` → `ObjectHeadDirectoryVersion`；`P0DirectoryResult` → `MaterializedObjectHeadDirectory` | [codec](../../../src/DurableGraph.Persistence/ObjectHeadDirectoryCodec.cs)、[版本](../../../src/DurableGraph.Persistence/GraphPersistenceContracts.cs)、[完整目录](../../../src/DurableGraph.Persistence/GraphPhysicalReadCache.cs) | 这里是 ObjectId→head，不是文件目录或 Schema catalog；完整结果保实际重建 payload 成本 |
| `P0TypedBodyCodec` → `TypedBaseBodyCodec`；`P0TypedReader` → `StoredGraphDecoder`；`P0DecodedRows` → `DecodedGraphRows` | [声明](../../../src/DurableGraph.Persistence/StoredGraphBodies.cs)编码／解释 Base 类型头，读取全部 live 行并核 origin/selected 来源与引用 | Delta 没有同类 Base 头；Decoder 不执行 Upgrade、Allocate 或 Hydrate，不与后续 GraphReader 合并 |
| `P0GraphReadCache` → `GraphPhysicalReadCache`；`P0GraphReadCacheStatistics` → `GraphPhysicalReadStatistics` | [声明](../../../src/DurableGraph.Persistence/GraphPhysicalReadCache.cs)复用完成封套、batch、directory version 和完整 heads | 不缓存 typed DTO／模型执行结果／领域实例；命中仍不能替代图语义和准确 S 验证 |
| `P0SaveCheckpoint` → `RepositorySaveStage` | [声明](../../../src/DurableGraph.Persistence/GraphPersistenceContracts.cs)是共同保存 hook 的阶段；[Kill 见证](../../../experiments/MetaBootstrapCrashProbe/Program.cs)据此选择窗口 | 与公共 Checkpoint 历史位置不同；只改符号，保 hook 位置、枚举值和实验窗口含义 |

基线 `P0GraphSnapshot.Objects/Heads` 实际是同一 head map，B 已统一为 `StoredGraphRevision.ObjectHeads` 并适配调用者。
B 基线 GraphResources（现 RepositoryResources）上指向默认路径的内部成员已按实际职责收回 backend 标签，例如 `MetaBootstrapStore` → `GraphStore`。
自举本身仍是有效机制；说明固定解释依赖时可以继续使用 bootstrap，不能把所有出现该词的描述都当历史垃圾。

### 3.2 Schema catalog 与固定员工

| 当前名 → 推荐名 | 依据与限定 |
|---|---|
| `P0CatalogView` → `SchemaCatalogView`；`P0CatalogWorkspace` → `SchemaCatalogWorkspace`；`P0PreparedCatalog` → `PreparedSchemaCatalogWrite`；`P0CatalogSemantics` → `SchemaCatalogSemantics` | [view](../../../src/DurableGraph.Persistence/SchemaCatalogView.cs)、[workspace](../../../src/DurableGraph.Persistence/SchemaCatalogWorkspace.cs)、[规则](../../../src/DurableGraph.Persistence/SchemaCatalogSemantics.cs)含完整用户 Schema 和容器 descriptor；不是独立发布的旧 SchemaStore |
| `P0CatalogRecord` → `SchemaCatalogRecord`；`P0DescriptorRecord` → `LayoutDescriptorRecord`；`P0FieldRecord` → `LayoutSlotRecord`；`P0TypeExprRecord` → `TypeExprRecord`；`P0HeadLocator` → `ObjectHeadLocator` | [员工声明](../../../src/DurableGraph.Persistence/BootstrapRecords.cs)；descriptor 也描述容器，slot 也服务元素/key/value/Nullable child；head locator 是固定地址状态，不能替代 FrameAddress |
| `P0EmployeeDefinitions` → `BootstrapDefinitions`；`P0FixedBindings` → `BootstrapBindings`；`P0BuiltinLayouts` → `BuiltinObjectLayouts` | [员工登记](../../../src/DurableGraph.Persistence/BootstrapRecords.cs)、[固定表](../../../src/DurableGraph.Persistence/BootstrapBindings.cs)、[builtin 布局](../../../src/DurableGraph.Persistence/BuiltinObjectLayouts.cs)保有限员工/string 与 builtin-only structural 路由；不把 bootstrap 扩成任意定义发现服务 |

员工带 `DurableType`，C 单列 CLR 名与 namespace 收尾，并验证生成结果和当前新包从空库保存／冷开／续写。
不要为让字符串整齐改持久 SchemaId：`P0C/P0D/P0F/P0T/P0H`、V1、FieldId、字段类型保持；
相应 `Family_503043/503044/503046/503054/503048` 保持，因为 [FamilyName](../../../src/DurableGraph.Generator/DurableGraphGenerator.GenericState.cs)取 SchemaId 的 UTF8 hex。

## 4. Persistence 的其他内部名称

本节左侧为 B 基线，右侧已实施，均在 `Atelia.DurableGraph.Persistence`；nested 类型保 nested，结构未抽取；[E2](#e2-status)对应执行环境、归一化材料与写规划器的文件名。

| 当前名 → 推荐名 | 事实、传播范围与边界 |
|---|---|
| `StateModelSnapshot` → `ModelExecutionEnvironment` | [实现](../../../src/DurableGraph.Persistence/ModelExecutionEnvironment.cs)含准确 lookup、按需闭合工厂和 memo；[FrozenConfiguration](../../../src/DurableGraph.Persistence/StateModelRegistry.cs)才区分冻结的配置。同步改返回该环境的 `Snapshot/CreateSnapshot` 为 `CreateEnvironment`；真正复制 reader 索引的 Snapshot 保留。不是搬入 Runtime 的理由 |
| `DecodedRevision.SplitBindingEnvironment` → `DecodedRevision.SchemaViewBindingEnvironment` | [实现](../../../src/DurableGraph.Persistence/DecodedRevision.cs)是携带实际 Store＋冻结配置＋准确 S 来源的绑定封套，内部持有上行 ModelExecutionEnvironment；复用仍核完整来源，两层不能合并。`ReadSplit` → `ReadStoredGraph`；只改默认路径标签，不扩大跨 Store／跨 S 共享 |
| `NormalizedRevision` → `NormalizedGraphState` | [实现](../../../src/DurableGraph.Persistence/NormalizedGraphState.cs)既含归一化来源，也含未追加 candidate，后续才安装实际 heads/H；不用 PublishedRevision 或只服务保存的 SaveBaseline。`FromSplitCandidate/WithSplitGraph/RequireSplitSource` 改为表达真实来源／安装职责的名称，保原校验 |
| `MaterializedGraph<T>` → `EditableGraphRestoration<T>` | [声明与交付](../../../src/DurableGraph.Persistence/GraphReader.cs)除领域根还含 normalized baseline、ID 游标和反向身份，只由 editable delivery 产生；不把纯只读根包装成此材料 |
| `ObjectRevisionPlanner` → `ObjectWritePlanner` | [实现](../../../src/DurableGraph.Persistence/ObjectWritePlanner.cs)只分类／规划对象写入，source membership 和 Removes 在持久 adapter。同步纠正 [ObjectRepresentationPlan](../../../src/DurableGraph.Persistence/ObjectRepresentationPlan.cs)中仍声称 planner 供应 membership difference 的旧注释 |
| `CommitCheckpoint` → `RepositoryOperationStage` | [声明](../../../src/DurableGraph.Persistence/RepositoryOperationStage.cs)为外观层阶段 hook，不是历史读取；与 RepositorySaveStage 各处于不同层。改 hook 名但不合并枚举、移注入点或顺手删未使用成员 |

`Split` 并不总是历史词：测试／包故事可能借它描述 shared/private 或准确视图场景。
这里只收回曾表示并行 backend 的内部标签，不批量改实验名、fixture 类型或历史记录。

## 5. Generator／Build 的分析材料

本节是 A 片旧名→新名的迁移表，当前已采用右侧名称；保 `.Generator`、`.Build` 与生成用户 API。配套私有名称与实际验证只维护在[实施记录](implementation-a.md)。

| 当前名 → 推荐名 | 证据与取舍 |
|---|---|
| Generator 私有 `GenericLayout/GenericField` → `FamilyLayout/FamilyField` | [声明](../../../src/DurableGraph.Generator/DurableGraphGenerator.GenericState.cs)、[路径选择](../../../src/DurableGraph.Generator/DurableGraphGenerator.TemplateHistory.cs)、[Family 汇聚](../../../src/DurableGraph.Generator/DurableGraphGenerator.GenericFactories.cs)：非泛型 enum/record/Nullable、显式 Upgrade、外部模板和 forced definitions 也走此路径。表示 Family 路径的配套方法同步命名；真正描述 CLR 泛型或类型参数列表的 Generic 词保留 |
| Generator 私有 `DurableTypeModel/DurableFieldModel` → `DurableTypeAnalysis/DurableFieldAnalysis` | [声明](../../../src/DurableGraph.Generator/DurableGraphGenerator.cs)是 Roslyn 符号与字段分析，含 enum 的无 CLR 字段合成项；不用只含实际声明意味的 FieldDeclaration，也不混称 Runtime 的执行 Model |
| Generator 私有 `SchemaHistoryModel/SchemaHistoryFieldModel` → `SchemaTemplate/SchemaTemplateField` | [声明](../../../src/DurableGraph.Generator/DurableGraphGenerator.cs)、[current＋accepted＋referenced](../../../src/DurableGraph.Generator/DurableGraphGenerator.GenericFactories.cs)均使用同一模板形状，history 只是来源之一；不合并 Runtime 的 StateSchemaTemplate |
| Build 内部 `SchemaHistoryDocument` → `SchemaHistoryTextCodec` | [声明](../../../src/DurableGraph.Build/SchemaHistoryTool.cs)是读取、解析、canonical render 和 SHA 文件命名的静态类，没有 Document 实例；发布／校验与闭包仍由 SchemaHistoryTool 承担 |

`BinaryVersionModel/BinaryFieldModel` 只用于普通直接生成路径，Family 也生成 binary body，名字有区分力不足的线索。
暂不定名：先确认对普通 `__DurableState` 路径的统一称呼，再考虑 `DirectStateVersionPlan/DirectStateFieldPlan`；不是第一片必改项。

[D2](implementation-d2.md)已将入口 `Atelia.DurableGraph.Generator.DurableSchemaGenerator` 改为 `DurableGraphGenerator`：实际职责覆盖 DTO/body、Capture/Hydrate、reader/model/definition、Upgrade 和 exports。
17 份 partial 声明与 3 处测试构造点同步，保唯一 `[Generator]`、hints、生成宿主和算法；D2 未移动文件，后继 [E1](#e1-status)对应现行文件名。
原始生成文本／hints／history 等价，diagnostics 仅三条隐藏记录的 generated Location.Path 随入口 FQN 改变。
准确新包的普通／Family 消费实测：旧／新输出目录并存触发[targets](../../../src/DurableGraph/build/Atelia.DurableGraph.targets)现有的“必须恰好一份”拒绝，`dotnet clean` 仍保留生成文件。
确认属于当前消费者后显式清理派生生成输出，再强制重编译，Publish／Verify／run 均恢复，history 保持。
两条错误只更新清理／重编译提示，扫描和数量约束保持；没有旧入口壳、双 Generator 或常驻清扫 target。

CLI 目前只有顶层 `return 0`，没有值得改名的产品类型。`SchemaHistoryTool`、Shared 的 `TypePattern` 名称与构建期职责相符，保留。

## 6. 公开候选与尚不宜机械处理的问题

这些线索已证实，但不纳入内部命名第一片；D1 只采用下表前两项，内部接口随改，具体映射与验收见[当片合同](implementation-d1.md)。D3 采用仓库资源 owner 名称；D4 将两项纯槽转换归回既有 Schema 类型；D5 采用根空间的检查点角色名。其余候选保独立判断，不因没有兼容负担而自动全改。

| 提案基线完整类型名 | 采用结果与限制 |
|---|---|
| `Atelia.DurableGraph.Runtime.CapturedStatePreparation<T>` | D1 已采用 `StatePreparationBinding<T>`，内部接口为 `IStatePreparationBinding`。[声明](../../../src/DurableGraph/Runtime/Capture/StatePreparationBinding.cs)是稳定 Schema/DTO/body/equality 能力绑定，Normalize 后和 [Pair 比较](../../../src/DurableGraph.Persistence/GraphReader.cs)也使用；[SG](../../../src/DurableGraph.Generator/DurableGraphGenerator.StateModel.cs)直接发出新名称。不是一次准备结果，equality 与精确绑定检查保持 |
| `Atelia.DurableGraph.Serialization.StringPayloadCodec` | D1 已采用 `StringBodyPreparer`；[唯一操作](../../../src/DurableGraph.Serialization/StringBodyPreparer.cs)返回 raw PreparedBaseBody，不含 typed 头或 ObjectVersion payload。不为 Codec 的读写对称性增加 reader |
| `Atelia.DurableGraph.Persistence.GraphResources` | [D3](#d3-status)采用 `Atelia.DurableGraph.Persistence.RepositoryResources`；实际拥有仓库级 FS/VS、branch/shared 基线、唯一 pending 和 fault。namespace 与公开成员保持，直接迁移并重编译，不与薄 facade 合并 |
| `Atelia.DurableGraph.Persistence.GraphFrameKind` | [D5](#d5-status)采用根空间 `Atelia.DurableGraph.CheckpointKind`，与 CheckpointAddress 的业务角色对应；[enum](../../../src/DurableGraph.Persistence/CheckpointKind.cs)仅 Event/State，仍在 Persistence 程序集。不与内部 StoredGraphKind 合并或加 Meta |
| `Atelia.DurableGraph.Persistence.GraphCommitException/GraphCommitOutcome` | [D6](#d6-status)采用根空间 `RepositoryPublicationException/RepositoryPublicationOutcome`；[现合同](../../../src/DurableGraph.Persistence/RepositoryPublicationException.cs)覆盖 graph、branch-ref 和 tag。仍在 Persistence 程序集，保 outcome/fault/候选地址；内部阶段 hook 保原 namespace |
| `.Runtime.StringValueProjection/DurableValueProjection<T>/ObjectValueProjection<T>` | [D6](#d6-status)保名并关闭本轮候选。[实现](../../../src/DurableGraph/Runtime/State/BuiltinStateValues.cs)在引用与 ObjectId 间转换，属于 IValueProjection 值槽投影家族，Value 不限定 CLR 值类型。保通用接口／支持范围和 public helper |
| `Atelia.DurableGraph.Schema.ObjectStateKind` | [D6](#d6-status)保名并关闭本轮候选。[ObjectLayout](../../../src/DurableGraph/Schema/ObjectLayout.cs)和 ObjectStateRecord 共享冻结内容分类；不只描述 layout，不对应业务 State 或 wire kind |

`Atelia.DurableGraph.Schema.DurableFieldInfo` 描述带正编号的完整持久值槽：普通字段用声明内 FieldId，Array/List/Nullable child 规范为 1，Dictionary key/value 规范为 1/2；它不是容器元素的索引。
[D4](#d4-status)采用局部归位，保现名和 record 相等。`ValueSlot`／`ValueSlotLayout` 纯改名仍需解释编号，并不能代替完整布局；不拆多套公开槽类型、不移除 FieldId、不改编号。

四种 [Schema layout](../../../src/DurableGraph/Schema/DictionaryLayout.cs)原为调用
`StateBindingContext.WithFieldId/NominalType` 而依赖 Runtime；D4 将两项同签名 public static 纯转换归回
[DurableFieldInfo](../../../src/DurableGraph/Schema/DurableFieldInfo.cs)，直接迁移调用方，无旧入口壳。
整个执行 context 保 Runtime 归属，不据此拆程序集；名义 TypeExpr 不包含 exact inline 版本，不能作为完整槽相等的证明。

## 7. 明确保留的名称与合同

- `Repository`／`BranchCheckout`／`Checkpoint`／`CheckpointAddress` 已区分资源宿主、持续编辑、读取视图、本次打开签发定位句柄，保留。
- `DecodedRevision` 是固定 stored-exact DTO/string 视图，资源关闭后仍可用；与含未写候选的 NormalizedGraphState 不同，保留。
- `BusinessWorkspace` 同时准备 State 和独立 Event，改 StateWorkspace 会收窄语义；`GraphReadContext` 是操作级 guard，不能统一成模型 Environment。
- `ObjectStateRecord`／`IStateOps`／`Frozen*State` 的 State 表示冻结持久内容，不限定业务 State 角色，不全局替换为 Graph 或 DTO。
- `StateDefinitionBinding`／`StateSchemaTemplate` 含执行类型／工厂信息，保 Runtime 归属；`StateModelRegistry` 冻结模型配置供准确视图闭合，`StateReaderRegistry` 登记 exact reader，可复制索引或构造带所选 lookup 的执行环境，不按“注册器”搬到 Runtime。
- Storage 的 ObjectVersionRecord/Kind/PayloadCodec/PayloadSize、FrameAddressWireCodec/Validator、FrozenSortedDictionary、OwnedFrameReadCache 名称与职责相符；不因小型通用类而新增 Utilities/Collections 程序集或 namespace。
- `Generated.Family_<UTF8HexId>.Vn`、BodyVn、Definition、`__DurableState`、`__DurableProjection`、`DurableDefinitions.Register` 维持现行生成合同；这次整理内部词汇不重命名用户 Upgrade 所引用的宿主。

## 8. 后续施工顺序与验收

先冻结当片旧全名→新全名与配套成员表，再实施；下面是建议次序，不是全部候选自动获批的队列。
保持程序集／PackageId／ProjectReference／IVT／目标框架和访问级别；A–D4 保文件位置，D5 提取一个公开 enum，D6 分开公开发布合同与内部阶段 hook；E1／E2 只改同目录 basename，E3 按有限表并入既有目录且保多类型文件。不得用 rename 掩盖合并 owner、删校验或改行为。

| 片 | 有限范围 | 最小可观察验收 |
|---|---|---|
| A：Generator 分析材料 | §5 私有分析／Family／模板名与 Build 内部 codec，暂保 generator 入口 | 新旧 generated texts/hints、diagnostics 和 history 完整字节相同；覆盖普通路径、非泛型 Family、真实泛型、record、enum、Nullable、Upgrade、外部 fixed base/inline、forced definitions；根 build 与相关 SG/Build tests |
| B：默认内核与执行语义 | §3.1、catalog 普通内核及 §4；固定员工 CLR 名暂留，允许一片内显式引用旧员工 namespace，不建兼容类型 | 根 build、相关 Persistence/Runtime tests；核完整来源、head/H、Remove、准确 S、唯一 pending、Event/State 安装与 fault/lifetime；源码只有批准映射差异，当前活动包故事与 crash 见证按直接受影响入口适配 |
| C：固定员工收尾 | §3.2 员工 CLR 名／namespace／登记，收完 MetaBootstrapP0 | 对比 V1 layout、固定 binding 编号、Family DTO/body 与 history；当前新 G 从空库保存、冷开／续写／再冷开，shared/private/meta 与目录均覆盖；根 build 与相关 tests。不设旧包存档兼容门槛 |
| D：公开有限迁移 | [D1](implementation-d1.md)采用状态内容操作绑定与 string body 准备两项；[D2](implementation-d2.md)完成 Generator 入口；[D3](#d3-status)采用仓库资源 owner 名称；[D4](#d4-status)归位两个纯槽 static 方法；[D5](#d5-status)采用根空间 CheckpointKind | 同步 emitted FQN、手写绑定、外部模型库与消费者；准确新 runtime/SG PackageReference 编译与运行，变化有迁移表，调用方直接迁移并重编译，不建兼容壳；入口另核 dirty 生成输出清理恢复 |
| E：组织判断 | [E1](#e1-status)对应 Generator／公开内容操作文件名，[E2](#e2-status)对应 Persistence 的 6 个文件名，[E3](#e3-status)将旧阶段目录的 16 份源码并入 Persistence 根并迁移导航；多类型文件共置 | 文件移动核 Git 原始字节、显式路径、根 build／相关 tests 和 incoming links；只有实证需要才增加项目、包或 namespace，不从名称表推导必须拆分 |

2026-10-14 用户明确：当前没有需要兼容的旧数据，实验存档可 reset 后重生成。
命名分片采用当前包的新建库故事验收，不为旧包新增兼容壳或常驻迁移门禁。
当前包内部的 Schema 升级、准确历史读取和打开 Store 生命周期内的完整帧纪律仍按现行合同验证。

每次代码变更按 [仓库规则](../../../AGENTS.md#evidence-and-validation)运行根 `dotnet build DurableGraph.slnx` 和相关 tests；
依赖模式按[存储指南](../../storage-dependency.md)显式选择，包交付用准确 package mode 与独立 DG 版本。
只有相关包／生成合同发生变化时扩大到对应真实消费者；用现有有效门禁，不恢复 DB-091 已删除的 runner。

实施时额外核对以下容易逃过 IDE Rename 的接缝：

- [SG metadata identity](../../../src/DurableGraph.Generator/DurableGraphGenerator.CrossAssembly.cs)与 [export 验证](../../../src/DurableGraph.Generator/DurableGraphGenerator.SchemaExports.cs)：`global::`、GetTypeByMetadataName、准确 Generated helper 和 marker 所属程序集都是真实合同；不能只改拼接文本，或放宽为仅名称匹配。
- [持久 wire](../../../src/DurableGraph.Persistence/GraphFrameCodec.cs)：PBB7/PBD7/PBG7、P0B1/P0D1/P0G1，发布根 P0R1/P0S1，typed ABI5、graph kind 值、完整 FrameAddress 编码保持。P0 字符串仍可作为协议历史标识存续。
- 生成 hints、export 合同 1/2、history 模板 v9、accepted `.dgschema` 文件名和字节保持；员工与用户 SchemaId／FieldId 不变。
- source rename 不等于可见性调整；namespace 迁移不改变 IVT 的程序集身份，也不改变 owner/lifetime、正常 backing 恢复或离线救援纪律。

## 9. 当前实施停点与限度

原始提案基于 `246b888`；A 采用 §5 内部映射，B 采用普通内核与 §4 映射，C 采用 §3.2 员工／登记／固定表映射，D1 采用 §6 前两项公开操作名称和内部接口，D2 完成 §5 Generator 入口，E1／E2 对应部分现行类型的文件名，E3 退出旧阶段源码目录，D3 采用唯一仓库资源 owner 的公开职责名，D4 将两项纯槽转换归回 Schema，D5 采用根空间 CheckpointKind。本轮有限组织与 D3／D4／D5 范围完成，余项另判。
[A](implementation-a.md)维护新旧 Generator/Build 等价；[B](implementation-b.md)维护 namespace、owner/source、根测试、公开 API 和包门禁各自资格，结果不互相代授。
原有 117 处历史引用缺口未扩入本案。B／[C](implementation-c.md)／[D1](implementation-d1.md)／[D2](implementation-d2.md)／[D3](#d3-status)／[D4](#d4-status)／[D5](#d5-status)已完成各自准确包的限定消费验收；当前无旧包兼容门槛，调用方按迁移表重编译。[E1](#e1-status)／[E2](#e2-status)／[E3](#e3-status)取得源码组织、构建与相关回归的限定资格；后续公开候选与程序集问题只维护在[路线图](../../DurableGraph-research-roadmap.md)。

<a id="e1-status"></a>
## 10. E1：文件名对应现行类型

2026-10-14；基线 `de8a0eb`。问题：Coding Agent 和 IDE 能否直接按现行类型名定位文件，同时保持源码和编译接线？
本片只移动同目录中的 19 个源文件和 1 个测试文件，C# 原始字节、类型／成员、namespace、程序集／包、生成入口与协议保持。
最小验收是 20 对文件与 Git 基线完整字节相同，编译配置／脚本没有失效路径，根构建与相关测试通过，incoming links 没有新增缺口。

| 所在目录 | 旧 basename → 新 basename |
|---|---|
| `src/DurableGraph.Generator/` | `DurableSchemaGenerator*.cs` → `DurableGraphGenerator*.cs`，共 17 份，suffix 保持 |
| `src/DurableGraph/Runtime/Capture/` | `CapturedStatePreparation.cs` → `StatePreparationBinding.cs` |
| `src/DurableGraph.Serialization/` | `StringPayloadCodec.cs` → `StringBodyPreparer.cs` |
| `tests/DurableGraph.Serialization.Tests/Serialization/` | `StringPayloadCodecTests.cs` → `StringBodyPreparerTests.cs` |

Generator 的有限 suffix 为主文件，以及 Ancestry、CrossAssembly、Enums、GeneratedState、GenericFactories、GenericProjection、GenericState、GenericUpgrades、ImmutableLeaf、InlineState、Records、SchemaExportHelpers、SchemaExports、StateModel、TemplateHistory、ValueUpgrades。
现有 `DurableSchemaGeneratorTests` partial 测试宿主保持；Persistence 目录和多类型文件不在本片。

实施者、独立复核者和主线程分别核 20 对 Git 原始字节相同；主线程另核 587 个其他非文档跟踪文件原字节保持。相关项目采用 SDK 默认编译收集，没有发现旧 basename 的显式 Compile／脚本硬编码或 CallerFilePath 依赖。
更新 30 份文档中的 129 个当前源码链接；历史裸路径、Git 来源和冻结 receipt 保其历史意义。当前焦点、索引和路线图另按实施结果同步，未改历史结论。

使用现有准确 S pin 的 package-mode assets，主线程运行：

```powershell
dotnet build DurableGraph.slnx -c Release --no-restore -t:Rebuild -m:1 -nr:false -p:UseStorageSources=false
dotnet test tests/DurableGraph.Tests/DurableGraph.Tests.csproj -c Release --no-build --no-restore -m:1 -nr:false -p:UseStorageSources=false
dotnet test tests/DurableGraph.Serialization.Tests/DurableGraph.Serialization.Tests.csproj -c Release --no-build --no-restore -m:1 -nr:false -p:UseStorageSources=false
```

根 Rebuild 零警告／错误；Runtime／Generator／Build 1628 项及 Serialization 133 项全部通过，失败／跳过为零。集成 diff 和本地链接检查通过，没有新增历史引用缺口。
完整路径表与移动 receipt 位于 `.artifacts/db092-e1/source/`，`result.json` SHA256 `F9B056030A9422E24D5936F866BC9575987A6EC9B9461FBA0CD5EA09AD6121A9`；主线程字节验收为 `.artifacts/db092-e1/root-source-acceptance.json`，构建／测试日志在同一切片工件目录。
本片没有制作新 G 或重跑包消费者／存档故事，不把 D2 原 G 的资格回填为 E1 新包证据；文件路径会改变调试来源，不声明 DLL／PDB 字节相等。后续范围只维护在[路线图](../../DurableGraph-research-roadmap.md)。

<a id="e2-status"></a>
## 11. E2：Persistence 文件名对应现行类型

2026-10-14；基线 `eb2638be99722a862c5ba6042c5bf3736a641595`。问题：B 已改名的执行环境、归一化材料与对象写规划器，能否直接按类型名定位文件？
本片只移动 `src/DurableGraph.Persistence/` 同目录中的 6 个文件，C# 原始字节保持；`NormalizedObject` 留在原文件内容中。
最小验收是 6 对文件与 Git 基线完整字节相同，没有失效的编译／脚本路径，根构建与 Persistence 回归通过，incoming links 没有新增缺口。

| 旧 basename | 新 basename |
|---|---|
| `StateModelSnapshot.cs` | `ModelExecutionEnvironment.cs` |
| `StateModelSnapshot.Arrays.cs` | `ModelExecutionEnvironment.Arrays.cs` |
| `StateModelSnapshot.Lists.cs` | `ModelExecutionEnvironment.Lists.cs` |
| `StateModelSnapshot.Dictionaries.cs` | `ModelExecutionEnvironment.Dictionaries.cs` |
| `NormalizedRevision.cs` | `NormalizedGraphState.cs` |
| `ObjectRevisionPlanner.cs` | `ObjectWritePlanner.cs` |

实施者、独立复核者与主线程核对移动前后原始字节；主线程另核 601 个其他非文档跟踪文件保持。Persistence 使用 SDK 默认编译收集，未发现旧 basename 的 Compile／脚本硬编码或 CallerFilePath 依赖。
更新 29 份文档中的 49 个当前源码链接；历史符号文字、Git 来源与冻结证据保其原义。目录、namespace、类型／成员、程序集／包、生成入口与持久协议保持。

使用现有准确 S pin 的 package-mode assets，主线程运行：

```powershell
dotnet build DurableGraph.slnx -c Release --no-restore -t:Rebuild -m:1 -nr:false -p:UseStorageSources=false
dotnet test tests/DurableGraph.Persistence.Tests/DurableGraph.Persistence.Tests.csproj -c Release --no-build --no-restore -m:1 -nr:false -p:UseStorageSources=false
```

根 Rebuild 零警告／错误；Persistence 893 项全部通过，失败／跳过为零。集成 diff 和本地链接检查通过，没有新增历史引用缺口。
完整路径表和移动 receipt 位于 `.artifacts/db092-e2/source/`，`result.json` SHA256 `7209632448E8E279F82904C5ABD2C87A8252111C88123F59E741C3CD0E0A1026`；主线程字节验收为 `.artifacts/db092-e2/root-source-acceptance.json`，构建／测试日志在同一切片工件目录。
本片没有制作新 G 或重跑包消费者／存档故事，不声明 DLL／PDB 字节等价。旧 P0 源码目录未动，后续范围只维护在[路线图](../../DurableGraph-research-roadmap.md)。

<a id="e3-status"></a>
## 12. E3：退出旧阶段源码目录

2026-10-14；基线 `e60cfbcc8788fdff7019341757a67f30bb8b4817`。问题：已进入同一 `.Persistence` namespace 的默认内核，能否按现行职责定位，不再经历史阶段路径查找？
本片将旧 `src/DurableGraph.Persistence/MetaBootstrapP0/` 中恰好 16 份 C# 并入既有 Persistence 根目录，原始字节保持；有效 README 迁为[程序集源码导航](../../../src/DurableGraph.Persistence/README.md)。
最小验收是 16 对文件与 Git 基线完整字节相同、其他源码／配置保持、无失效编译／脚本路径，根构建和 Persistence 回归通过，迁移后的入链／出链没有新增缺口。

下表旧 basename 均位于旧目录，新 basename 均位于 `src/DurableGraph.Persistence/`：

| 旧 basename | 新 basename |
|---|---|
| `P0BuiltinLayouts.cs` | `BuiltinObjectLayouts.cs` |
| `P0BusinessWorkspace.cs` | `BusinessPersistenceAdapter.cs` |
| `P0CatalogSemantics.cs` | `SchemaCatalogSemantics.cs` |
| `P0CatalogView.cs` | `SchemaCatalogView.cs` |
| `P0CatalogWorkspace.cs` | `SchemaCatalogWorkspace.cs` |
| `P0DirectoryCodec.cs` | `ObjectHeadDirectoryCodec.cs` |
| `P0EmployeeModels.cs` | `BootstrapRecords.cs` |
| `P0FixedBindings.cs` | `BootstrapBindings.cs` |
| `P0GraphContracts.cs` | `GraphPersistenceContracts.cs` |
| `P0GraphReadCache.cs` | `GraphPhysicalReadCache.cs` |
| `P0GraphStore.cs` | `GraphRecordStore.cs` |
| `P0GraphWire.cs` | `GraphFrameCodec.cs` |
| `P0PendingSave.cs` | `PendingRepositorySave.cs` |
| `P0Repository.cs` | `RepositoryStorageFacade.cs` |
| `P0Session.cs` | `BranchSaveSession.cs` |
| `P0TypedBodies.cs` | `StoredGraphBodies.cs` |

`BootstrapRecords` 共置固定员工记录、ObjectHeadLocator 与登记；`GraphPersistenceContracts` 共置图、batch、目录、chain 与共同保存合同；`StoredGraphBodies` 同时含 typed Base 编码、DTO 行与完整图解码，不以单一 decoder 命名。
其他文件中的准备结果与缓存统计等 companion 同样保持共置。namespace、成员、访问级别、程序集／包、生成合同、员工 IDs／Family 与 wire 保持；既有 P0 测试宿主和历史协议标识不随目录机械更名。

实施者、独立复核者与主线程核对 16 对 Git 原始字节；主线程另核 591 个其他非文档跟踪文件保持。Persistence 采用 SDK 默认编译收集，未发现旧路径的 Compile／脚本／打包硬编码或 CallerFilePath 依赖。
修复 10 份文档中 86 个当前源码链接，另重算迁移 README 的 7 个未移动目标出链；导航补充公开入口、执行环境与共同合同，入链从当前工作集进入。历史包 scope 裸路径、Git 来源与冻结 receipt 保其原义。

使用现有准确 S pin 的 package-mode assets，主线程运行：

```powershell
dotnet build DurableGraph.slnx -c Release --no-restore -t:Rebuild -m:1 -nr:false -p:UseStorageSources=false
dotnet test tests/DurableGraph.Persistence.Tests/DurableGraph.Persistence.Tests.csproj -c Release --no-build --no-restore -m:1 -nr:false -p:UseStorageSources=false
```

根 Rebuild 零警告／错误；Persistence 893 项全部通过，失败／跳过为零。集成 diff 和本地链接检查通过，没有新增历史引用缺口。
完整路径表与移动 receipt 位于 `.artifacts/db092-e3/source/`，`result.json` SHA256 `029601F73F2A4823EFFCB4E4C231FFCFC7E04D19D4ABCD157EB184A90B1D2118`；主线程字节验收为 `.artifacts/db092-e3/root-source-acceptance.json`，构建／测试日志在同一切片工件目录。
本片没有制作新 G 或重跑包消费者／存档故事，不声明 DLL／PDB 字节等价，也不重授 C／D1／D2 的准确包资格。有限文件组织已完成；余下名称与程序集选择从[路线图](../../DurableGraph-research-roadmap.md)进入，不把本片升级为全项目目录整理。

<a id="d3-status"></a>
## 13. D3：仓库资源 owner 命名

2026-10-14；基线 `54445915476797bce2bf77897e77904d3ffcda2b`。问题：具体资源 owner 的名称能否明确其仓库级发布与寿命职责？
源码中的 owner 创建匹配的 FS/VS、持有 branch/shared 发布基线与唯一 pending，并统一 fault/dispose；RepositoryStorageFacade 只是可借用或拥有该资源的薄外观。
现有 XML 已能解释职责，但 GraphResources 容易被理解为单图的读取资源。采用 RepositoryResources，保现有 `.Persistence` 归属和唯一 owner，不增加另一层。

| 传播项 | 本片有限映射 |
|---|---|
| 公开完整类型名 | `Atelia.DurableGraph.Persistence.GraphResources` → `Atelia.DurableGraph.Persistence.RepositoryResources` |
| 两份文件名 | `GraphResources.cs`／`GraphResources.MetaBootstrap.cs` → `RepositoryResources.cs`／`RepositoryResources.MetaBootstrap.cs` |
| 源码接缝 | 类型、构造器、返回／参数、nested owner 类型引用；配套注释、owner XML summary 与两条资源健康／只读消息采用仓库职责名 |
| 文档 | 当前 owner 用语与两份源码链接同步；历史结论、包 receipt 和旧测试宿主名称保其原义 |

成员、访问级别、namespace、程序集／PackageId、项目与构建配置保持；不连带 GraphFrameKind、publication companion、helper 或 wire／Schema／history。
调用方直接迁移并重编译，不保兼容壳。公开资源仅提供三种 Create/Open 工厂、借用 Data、健康／只读状态与 Dispose；不能从外部注入 Repository 或直接使用 internal facade，不为验收扩 API。

最小验收：当前 C# 仅含有限标识符／注释及两条批准消息差异；公开 API 在唯一类型名反向映射后相同；根构建及 Persistence／Runtime／Generator 相关回归通过。
准确新 G 的非 friend PackageReference 消费者直接编译使用新 owner，验证 Data 的确认、读取、只读拒写及 owner 关闭后借用资源失效；全关后再以公开 Repository 在同库保存、跨进程冷开续写与只读历史读取。
internal 薄外观借用／pending／fault 寿命沿用现存回归，不以包消费者反射绕过边界。只验新建存储库，不设旧包或旧数据兼容门槛。

源码实施与主线程验证已通过：58 份 C# 的 317 个标识符、4 处名称注释、1 处 owner XML summary、2 条批准消息与两份 basename 同步；其他 549 个非文档跟踪文件保持原字节。
Roslyn token／literal 核验无失败或新解析错误；完整源码字节对照只含批准映射。公开／protected API 清单 1359 行在唯一类型名反向映射后相同，不授二进制兼容或全部 attribute／nullable annotation 等价资格。
当前 owner 用语已同步，修复 8 份文档的 19 个源码目标链接；历史正文与旧测试宿主名保持。集成 diff 和本地链接检查通过，原有 117 处历史引用缺口没有新增。

主线程以准确 S pin 的 package-mode assets 串行运行：

```powershell
dotnet build DurableGraph.slnx -c Release --no-restore -t:Rebuild -m:1 -nr:false -p:UseStorageSources=false
dotnet test tests/DurableGraph.Persistence.Tests/DurableGraph.Persistence.Tests.csproj -c Release --no-build --no-restore -m:1 -nr:false -p:UseStorageSources=false
dotnet test tests/DurableGraph.Tests/DurableGraph.Tests.csproj -c Release --no-build --no-restore -m:1 -nr:false -p:UseStorageSources=false
```

根 Rebuild 零警告／错误；Persistence 893 项、Runtime／Generator／Build 1628 项全部通过，失败／跳过为零。
根 solution 外的 MetaBootstrapCrashProbe 使用准确 S 源码，以独立输出编译通过，零警告／错误；没有重跑 Kill 矩阵或授本次源码进程中止资格：

```powershell
dotnet build experiments/MetaBootstrapCrashProbe/MetaBootstrapCrashProbe.csproj -c Release -p:UseStorageSources=true -p:StorageSourceRoot=E:/repos/Atelia-org/atelia-storage -p:UseArtifactsOutput=true -p:ArtifactsPath=E:/repos/Atelia-org/durable-graph/.artifacts/db092-d3/crash-host -m:1 -nr:false
```

源码 receipt 为 `.artifacts/db092-d3/source/result.json`，SHA256 `26B54356253A13EA558015D4DD5BE6B994C84FC370B0C00931715DD99D8AE412`；主线程字节／API 凭据与实际构建／测试日志位于 `.artifacts/db092-d3/`。
准确新包由源码／合同提交 `6c9735e7794bbe1d508ba462341e76728d4d1fb3` 生产，单次 Prepare；188 个生产输入在打包前后与当前源码一致。六个 S 原包字节及四个 DG 包的 nuspec、实际 SG／Build／props／targets均核对：

| 包证据 | 准确来源 |
|---|---|
| DG G | `0.0.0-db092-d3-e2e.20261014114453.18688` |
| Storage S／来源 | `0.1.3-dev.20261014.g758cebf`／`758cebf8ba82788ba91435d34fd484dff32095fd` |
| 完整十包 feed | `.artifacts/db092-d3/package/feed` |
| 独立消费者／cache／新库 | `W:/TEMP/dg-db092-d3-owner-20261014T114803-40020` |

非 friend 消费者使用两个 PackageReference、一个 forced Family TagState V1 模型及强类型 `RepositoryResources`，没有 ProjectReference 或访问内部 facade。
实际 packaged SG 编译与 Build Publish／CI Verify 通过；两种 history manifest 各恰好一份，唯一已接受 history 文件名／SHA 保持。四个独立进程按下表验证同一个新库：

| 进程 | 实际观察 |
|---|---|
| create | 直接 CreateNew，Data.Append／ConfirmDurable／ReadFrame；owner 关闭后借出的 Data.EnsureUsable／ReadFrame 报 ObjectDisposedException，独立拥有的 FrameRead buffer 仍可用 |
| seed | 直接 OpenExisting，原完整 frame／StoreId 保持，再追加确认；全关后公开 Repository 打开同库，创建初始 State=10/self-cycle 与 tag，保存为 11 |
| continue | 新进程冷签出准确 11、保持根 ID/self-cycle，续写为 12，产生新 head 并读回 |
| verify | 直接 OpenReadOnlyExisting，两个 raw frame 保持；Data.Append 报 InvalidOperationException且 owner 健康；全关后 Repository 只读验证当前 12／初始 tag 的 10，整个只读阶段文件 SHA 保持 |

主线程核对实际 assets／独立 cache／输出与所选十包，四个 PID 的 39 次实际 Atelia 程序集加载均匹配包 DLL 字节；包内公开 API 1359 行亦通过唯一类型名逆映射。
独立复核从实际源码、日志、consumer 和 receipt 验收，未发现阻断项。包凭据为 `.artifacts/db092-d3/package/consumer-result.json`，SHA256 `CA0190BF8DC2CB15B30432C45CBC20E88085C8D7D6FC6188A666115DEB4228FD`；主线程接受凭据为同片根目录的 `root-pack-acceptance.json`／`root-consumer-acceptance.json`。

当前停点：D3 的唯一公开 owner 名称迁移与上述源码／准确新 G 消费范围完成。调用方按表迁移并重编译；不将 D1／D2 原包资格或旧 DB 的更广业务故事回填为本 G 证据。
没有旧包／旧数据兼容门槛；本片不授 Kill／断电、性能或完整其他包故事资格。下一候选与重访条件只维护在[路线图](../../DurableGraph-research-roadmap.md)，不恢复全表改名或程序集整理工单。

<a id="d4-status"></a>
## 14. D4：完整值槽的纯转换归位

2026-10-14；基线 `1d7006e30f193e917c3576cc3d1f6ac7f8c0f477`。问题：不可变 Schema layout 能否仅依赖槽描述进行编号规范化和名义类型投影？
调查与独立审查均确认两项不读取执行环境。采用静态同签名归位，保 `DurableFieldInfo` 现名并补 XML：它同时表达字段和容器中的完整持久值槽，FieldId 是角色编号，参与相等。
相比全局类型改名或改为 instance API，本片只纠正真实依赖和能力归属，保调用参数顺序与求值。

| 旧入口 | 新入口 |
|---|---|
| `Atelia.DurableGraph.Runtime.StateBindingContext.WithFieldId(DurableFieldInfo slot, int fieldId)` | `Atelia.DurableGraph.Schema.DurableFieldInfo.WithFieldId(DurableFieldInfo slot, int fieldId)` |
| `Atelia.DurableGraph.Runtime.StateBindingContext.NominalType(DurableFieldInfo slot)` | `Atelia.DurableGraph.Schema.DurableFieldInfo.NominalType(DurableFieldInfo slot)` |

有限范围为两段 switch 原文迁移、已查明的显式及继承／未限定调用、四种 Schema layout 的 Runtime using，以及槽／编号／方法 XML。
旧入口直接删除，调用方按表迁移重编译；`StateValueBinding.WithFieldId(int)` 保持，其内部转用新 owner。SG 发出的该实例调用、Generator/Build、文件名、namespace、程序集／PackageId、构造校验与消息、编号、相等、wire／Schema／history 均保持。
名义类型投影忽略槽编号与 inline 版本；同 nominal 的两个 exact 槽仍可能不等，不放宽任何完整布局检查。

最小验收：源码仅含上述有限变化，Schema 不再引用 DG Runtime；public/protected API 只有两个方法 owner 改变；根构建和 Runtime／Generator／Persistence 回归通过。
准确新 G 的非 friend PackageReference 消费者直接使用新入口，核编号／容器规范化／精确 inline／nominal 边界，并从新库保存、跨进程冷开续写、再冷开只读。
不设旧包／旧数据兼容门槛；不扩 API、恢复历史 runner 或授 crash／性能资格。后续候选只维护在[路线图](../../DurableGraph-research-roadmap.md)。

源码与根验证已完成：27 份 C# 更新 50 个调用（37 个显式 owner、13 个原 context 内／继承的未限定调用）；两方法原始字节相同，包含 Nullable 递归。独立复核从 Git 原 blob 核验，其他 581 个非 Markdown 跟踪文件原字节保持；Generator/Build、实验源码和所有构造消息保持。
公开／protected API 清单 1359 行只有上述两个 METHOD 的声明 owner 改变；该清单不穷尽 custom attribute／nullable metadata，也不授二进制或 DLL／PDB 等价资格。

主线程使用准确 S pin 的既有 package-mode assets 串行运行：

```powershell
dotnet build DurableGraph.slnx -c Release --no-restore -t:Rebuild -m:1 -nr:false -p:UseStorageSources=false
dotnet test tests/DurableGraph.Persistence.Tests/DurableGraph.Persistence.Tests.csproj -c Release --no-build --no-restore -m:1 -nr:false -p:UseStorageSources=false
dotnet test tests/DurableGraph.Tests/DurableGraph.Tests.csproj -c Release --no-build --no-restore -m:1 -nr:false -p:UseStorageSources=false
```

根 Rebuild 零警告／错误；Persistence 893 项、Runtime／Generator／Build 1628 项全部通过，失败／跳过为零。集成 diff 与本地链接核验没有新增引用缺口；117 处历史问题未扩入本片。
源码 receipt 为 `.artifacts/db092-d4/source/result.json`，SHA256 `E673875572712D4A32AD673BA9E4EB928087870606AE6D0E597EDEE617D612DB`；独立源码凭据在 `review/source-review.json`，主线程 API／根验证日志在同一切片目录。
准确新包由源码／合同提交 `6a5f4eed7bf64cc6a3361ffe3664b0db611bd7cd` 单次 Prepare 生产；188 个生产输入在前后及当前 Git 对照中保持，六个 S 原包与四个 DG 包的身份／字节、实际 SG／Build／props／targets均核验。

| 包证据 | 准确来源 |
|---|---|
| DG G | `0.0.0-db092-d4-e2e.20261014122747.5400` |
| Storage S／来源 | `0.1.3-dev.20261014.g758cebf`／`758cebf8ba82788ba91435d34fd484dff32095fd` |
| 完整十包 feed | `.artifacts/db092-d4/package/feed` |
| 独立消费者／cache／新库 | `W:/TEMP/dg-db092-d4-slots-20261014T122850-39940` |

两份非 friend PackageReference compilation 实际消费同一新 G，没有 ProjectReference、手写 analyzer/import 或内部反射：

| 消费者 | 实际观察 |
|---|---|
| ordinary | 现行 Consumer 的 ProbeVersion=3，实际 `__DurableState` 路径及既有完整断言通过；packaged SG 与 Build Publish／CI Verify 通过，7 份 accepted history 保持 |
| Family | Point V1＋State V1，字段编号 3/7/11/17/23，含 Point?、Array、List、Dictionary 与 self-cycle；直接调用新 owner 两方法，核 scalar/reference/inline/Nullable、规范编号、输入保持及同 nominal／不同 exact inline 版本，2 份 accepted history 保持 |

Family 的 seed／continue／verify 为三个独立 PID：新库保存真实初始内容和 tag，全关后冷签出相同根 ID/head，编辑全部容器及 Nullable 后提交新 head；再次冷开只读验证 current 与 initial tag，整个只读阶段文件 SHA 保持。
四个运行 PID 均 exit 0，34 次实际加载映像逐一匹配所选包或普通消费者 DLL 字节；两份 assets、独立 cache、输出、实际编译输入与两种生成 manifest 的唯一性均核验。包内公开／protected API 1359 行也只改变两个方法 owner。

任务脚本首轮 Family 编译／只读运行误用同名日志，原编译日志被覆盖；保留原 controller／receipt 和缺口说明，仅补一次同 G、同输入的 CI Verify 到独立日志。运行 DLL、源输入、generated、history 与首轮已捕获 SHA 保持；不把未执行的修正版 controller 当成首轮来源。首轮 PDB 未捕获，不授其字节等价。
最终包凭据为 `.artifacts/db092-d4/package/qualification-result.json`，SHA256 `A16BFE2788C86E4CF7C3B44228496106E520EB5C601EB5D11A312F9094D60C3E`；原 consumer receipt 与 `log-recovery-result.json` 分别保运行与补日志来源。主线程接受凭据为同片的 `root-pack-acceptance.json`、`root-consumer-acceptance.json`、API 清单与实际根验证日志。

当前停点：D4 的公开方法归属、源码、根回归与上述准确新 G 有限消费完成，类型名保留。正常续写证明新 head／内容保持，没有检查物理 Base/Delta kind；手工 V2 槽反例不授持久 Upgrade 资格，也不授旧数据兼容、dirty SG、dual-view、Crash/Kill 或性能资格。后续入口只维护在[路线图](../../DurableGraph-research-roadmap.md)。

<a id="d5-status"></a>
## 15. D5：检查点角色名称与公开归属

2026-10-14；基线 `839d85981f2bc5d95da83b99b3b3a6fe77f9ca8c`。问题：公开 Kind 能否直接表达 Checkpoint 的业务角色，消除物理帧分类的暗示？
调查与独立质疑均支持 `Atelia.DurableGraph.Persistence.GraphFrameKind` → `Atelia.DurableGraph.CheckpointKind`，与根空间的 `CheckpointAddress.Kind` 对齐。
保现名虽可工作，却继续混淆公开历史角色与物理存储分类。此处仅调整名称与 namespace；程序集仍为 Persistence。

有限范围：将公开 enum 从 `HistoryGraphRecord.cs` 提取为同项目目录 `CheckpointKind.cs`，迁移现行源码／测试／实验的标识符引用，补 enum 与 Kind 的 XML 并更新当前导航。
`HistoryGraphRecord` 留在原 namespace；公开 enum 的 `uint`、Event=1／State=2 保持。内部 `StoredGraphKind`（byte，含 Meta）及其唯一业务角色投影的分支、校验、错误消息保持。
不改 codec／wire、Schema／history、公开成员与访问级别、生成器、项目／包配置、地址／owner／fault／寿命规则；旧入口直接退出，消费者迁移后重编译。

最小验收：只有以上有限源码变化；公开／protected API 按唯一完整类型名逆映射后相同；根 Rebuild 与 Persistence、Runtime／Generator／Build 回归通过。
单份准确新 G 的非 friend PackageReference 消费者强类型使用新 enum，消费真实 SG／Build Publish 与 CI Verify；从空库保存 Event-first 与 State/Event，跨进程冷签出续写，再冷开只读核历史、tag、Checkpoint 与内容。
没有旧包或旧数据兼容门槛，不扩大为其他包故事、Crash/Kill 或性能资格。后续候选只维护在[路线图](../../DurableGraph-research-roadmap.md)。

源码与根验证完成：31 份既有 C# 迁移 73 个标识符，新增一份 enum 文件；独立复核从 Git 原 blob 核完整有限变化，577 份其他非 Markdown 文件原字节保持。
公开／protected API 清单 1359 行仅六行含此类型，按唯一完整 FQN 逆映射后全等；不穷尽 attribute／nullable metadata，不授二进制兼容或 DLL／PDB 等价。
主线程按 D4 同一准确 S pin 的 package-mode 命令串行 Rebuild、两个测试项目：零警告／错误，Persistence 893 项、Runtime／Generator／Build 1628 项通过，失败／跳过为零。
根 solution 外的 ListDeltaReplay 另以专用配置显式 restore package mode，并用 `--artifacts-path .artifacts/db092-d5/replay-build` 隔离所有项目输出，compile 零警告／错误；未运行性能实验。
源码 receipt 为 `.artifacts/db092-d5/source/result.json`，SHA256 `CFF96BD975BCB82D7A26EF02565819535618370392140A36D2534F9461653685`；独立源码凭据为 `review/source-review.json`，根日志及 API 清单保存在同片目录。
准确新包由源码／合同提交 `2f99e1fbb54b2edf82d6b7e42e11f96f877f8ef0` 单次 Prepare 生产；189 个生产输入在前后及当前 Git 对照中保持，六个 S 原包及四个 DG 包的身份、来源、实际 DLL 字节均核验。

| 包证据 | 准确来源 |
|---|---|
| DG G | `0.0.0-db092-d5-e2e.20261014144649.36716` |
| Storage S／来源 | `0.1.3-dev.20261014.g758cebf`／`758cebf8ba82788ba91435d34fd484dff32095fd` |
| 完整十包 feed | `.artifacts/db092-d5/package/feed` |
| 独立消费者／cache／新库 | `W:/TEMP/dg-db092-d5-checkpoints-20261014T144805-39696` |

一份非 friend 普通 PackageReference compilation，两个包引用，单个标量 Sample 类型同时充当 Event／State，直接强类型使用新 enum；实际新 G 的 SG 普通 `__DurableState` 与 Build Publish／CI Verify 通过，唯一 accepted history 文件保持。
三个运行进程均 exit 0；Kind 的新 FQN、Persistence 程序集与 1／2 角色值直接观察，uint 底层类型由源码／API 清单核验。

| 独立 PID | 实际观察 |
|---|---|
| seed 7260 | 空库保存 Event7 → State10 → Event8；Event-first 没有 State，Event8 保持原 State10；三个位置分别建 tag |
| continue 39228 | 全关后冷签出 Event8 的准确 head，恢复 State10；保存 State11 → Event9 → State12，Event9 保 State11，再建三个 tag |
| readonly 10292 | 冷开只读核六项准确角色／内容、tag、ReadCheckpoint 最近相反角色、GetPreviousState 与 EnumerateEvents；全部库文件前后 SHA 相同 |

实际 assets、独立 cache、输出、编译输入及 generated／history 均核验；33 次加载映像（30 次包 DLL＋3 次消费者）逐一匹配所选字节。包内公开／protected API 1359 行亦只有同一六行类型 FQN 变化。
包凭据 `.artifacts/db092-d5/package/consumer-result.json`，SHA256 `D040BB4EE2E8F4399897E0D3659D2A7B75B0B0936C368EBE7500ADB587108D8F`；主线程接受凭据为同片 `root-acceptance.json`，独立复核为 `review/final-review.json`。集成 diff 与链接检查无新增缺口，117 处历史问题保持原范围。
当前停点：D5 的名称／namespace、源码、根回归、Replay 编译及上述准确新 G 普通消费完成。没有 Family、物理 Base/Delta kind、Upgrade、private promotion、Fork/Pair、旧包／旧数据兼容、Crash/Kill 或性能资格；前片结果不代授。后续候选与重访条件只维护在[路线图](../../DurableGraph-research-roadmap.md)。

<a id="d6-status"></a>
## 16. D6：发布合同命名与剩余候选收敛

2026-10-14；基线 `e33373a`。本轮 Goal 仅收敛 §6 剩余的发布类型、引用投影 helper 和对象内容分类三组，不继续扩充候选。
调查、独立质疑与主线程源码核对后，采用以下决定：

| 候选 | 决定与依据 |
|---|---|
| `GraphCommitException`／`GraphCommitOutcome` | 实施根 namespace 的 `RepositoryPublicationException`／`RepositoryPublicationOutcome`。Repository 的 ref-only 与 tag 发布也使用同一失败合同，无新图且候选地址为空；旧 GraphCommit 名称收窄了实际职责。它们是公开 Repository 操作的 catch 合同，归入接入 namespace；程序集仍为 Persistence |
| `StringValueProjection`／`DurableValueProjection<T>`／`ObjectValueProjection<T>` | 保名。它们是 `IValueProjection<TDomain,TState>` 值槽投影家族中的引用实现，当前 XML 已明确引用与 ObjectId 的转换；Value 不承诺 CLR 值类型。保通用接口与三个 public helper，不因局部未调用就删除 |
| `ObjectStateKind` | 保名。它区分 captured／decoded 对象内容，`ObjectLayout.Kind` 与 `ObjectStateRecord.Kind` 是同一分类轴，不只属于 layout；当前 XML 明确它不是 wire type code。单改 LayoutKind 收窄了内容分类含义 |

有限代码范围：两个公开类型共置于 `RepositoryPublicationException.cs`；内部阶段 hook 单列 `RepositoryOperationStage.cs`，仍在 `.Persistence`。
同步现行 C# 类型引用、相关注释和包 XML 的精确 member FQN 断言；不合并其他 owner、改程序集、成员或访问级别，不保兼容壳。
outcome 的默认 int／0、1、2、候选地址与 InnerException、消息原文、全部发布映射／hook／fault／安装与寿命检查保持。

| 旧完整类型名 | 新完整类型名 |
|---|---|
| `Atelia.DurableGraph.Persistence.GraphCommitException` | `Atelia.DurableGraph.RepositoryPublicationException` |
| `Atelia.DurableGraph.Persistence.GraphCommitOutcome` | `Atelia.DurableGraph.RepositoryPublicationOutcome` |

最小验收：源码仅有批准的标识符、namespace、文件拆出和 XML member FQN 差异；根构建与 Persistence／Runtime／Generator／Build 相关测试通过；公开 API 只有两类型的批准 FQN 变化。
准确新 G 普通非 friend 消费者编译新 catch／属性合同，并验证新库保存、ref-only 分支与 tag、冷开只读；内部确定性失败回归与普通包证据分别记录，不把 catch 编译声称为三个 outcome 的包故障运行。
源码已完成：27 份既有 C# 只作批准替换，删除旧混合文件并提取两份新文件；共迁移 142 个标识符、7 处 XML／注释引用及一处 PS 的 XML member FQN。
独立原 Git/blob 核验 609 份基线非 Markdown，580 份其他文件原字节保持；两个保名候选组、SG／Build 和协议代码均保持。
公开／protected API 共 1359 行，仅十行受两类型完整 FQN 映射影响；不声明属性／nullable 元数据穷举或二进制兼容。

```powershell
dotnet build DurableGraph.slnx -c Release --no-restore -t:Rebuild -m:1 -nr:false -p:UseStorageSources=false
dotnet test tests/DurableGraph.Persistence.Tests/DurableGraph.Persistence.Tests.csproj -c Release --no-build --no-restore -m:1 -nr:false -p:UseStorageSources=false
dotnet test tests/DurableGraph.Tests/DurableGraph.Tests.csproj -c Release --no-build --no-restore -m:1 -nr:false -p:UseStorageSources=false
```

根 Rebuild 零警告／错误；Persistence 893 项与 Runtime／Generator／Build 1628 项全部通过，失败／跳过为零。
既有确定性回归覆盖图／ref-only／tag 失败、候选地址、三个 outcome、NotPublished 可健康或 faulted、故障后停止服务与重开检查；没有改变注入面或增加公开故障 hook。
源码凭据 `.artifacts/db092-d6/source/result.json` SHA256 为 `7B31E7D5AD1996660E41DC7DCD2A27D2F18933F4E0DD4CD590983DC567C37A66`；独立源码复核 `.artifacts/db092-d6/review/source-review.json` SHA256 为 `23F6C6AE259B06CA5469AF79C6AF9A8E14163A82E04B4AF67D962D40244ED6C7`。当片根日志与 API 清单位于同一切片工件目录。

当前状态：源码、根回归与 API 映射已验收；准确新 G 有限消费待验收。其余两组已作保名结论；重访只由真实消费歧义或新的独立分类需求触发，后续条件只维护在[路线图](../../DurableGraph-research-roadmap.md)。
