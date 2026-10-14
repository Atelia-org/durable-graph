# DB-092：类型名与命名空间重构提案

> 2026-10-14；源码基线 `246b888`。状态：调查完成、主线程建议，尚未实施或取得重命名后的兼容资格。
> 问题：哪些现行名称已经不能准确表达职责，如何先整理它们，为以后判断程序集和文件归属提供清楚的概念边界？
> 本轮只形成方案。本文不授权改代码；当前名称、行为和包身份仍以源码为准。

建议先处理默认持久化内核的历史阶段名、模型执行环境的误称，以及 Generator 的 Family 分析材料。
保留公共接入入口和已有 `Schema`／`Runtime` 分工；公开名称变化另作有限迁移，程序集和文件组织留到命名稳定之后判断。

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
| `.Persistence` | 保留持久化集成；接纳去阶段名后的默认内核 | [GraphResources](../../../src/DurableGraph.Persistence/GraphResources.MetaBootstrap.cs)是资源与共同发布 owner；[DecodedRevision](../../../src/DurableGraph.Persistence/DecodedRevision.cs)和工作区连接准确存储视图与 Runtime |
| `.Persistence.MetaBootstrapP0` | 最终退出这个 CLR namespace；类型逐片进入现有 `.Persistence` | 16 份已跟踪 C# 文件同时承载 Event、State、Meta、物理图与分支保存，见 [P0GraphKind](../../../src/DurableGraph.Persistence/MetaBootstrapP0/P0GraphContracts.cs)、[P0Session](../../../src/DurableGraph.Persistence/MetaBootstrapP0/P0Session.cs)；P0 是施工阶段，不是当前产品分层 |
| `.Serialization`／`.Storage` | 保留；类型按下表逐项判断 | [PreparedBaseBody](../../../src/DurableGraph.Serialization/PreparedBaseBody.cs)、[ObjectVersionPayloadCodec](../../../src/DurableGraph.Storage/ObjectVersionPayloadCodec.cs)仍是共同字节契约 |
| `.Generator`／`.Build`／`.SchemaHistory` | 保留工具阶段和 source-linked 文本协议 | [Generator 项目](../../../src/DurableGraph.Generator/DurableGraph.Generator.csproj)、[Build 项目](../../../src/DurableGraph.Build/DurableGraph.Build.csproj)的目标框架与运行方式不同；[Shared 协议](../../../src/Shared/SchemaHistoryReferenceProtocol.cs)不是新运行时包 |

不把整个旧 P0 包改成 `.Meta`：其中业务读写和物理目录不只服务 meta。
暂不增加 `.Engine`／`.Reading`／`.Saving` 等子空间：当前收益主要来自准确的类型名，新增层级没有得到独立协作边界的证据。
后续若有真实独立消费或替换需求，再比较 namespace、文件归属和程序集依赖；这三种边界分别判断。

## 3. 默认持久化内核：推荐映射

本节当前名称统一位于 `Atelia.DurableGraph.Persistence.MetaBootstrapP0`，均为 `internal`；目标统一位于已有 `.Persistence`。
这是主线程的推荐拼写，不是多个别名方案。实施时按表逐个处理，不能做全局 `P0` 文本替换。

### 3.1 保存、来源和物理图

| 当前名 → 推荐名 | 职责与证据 | 必须保留的区别 |
|---|---|---|
| `P0Repository` → `RepositoryStorageFacade` | [声明](../../../src/DurableGraph.Persistence/MetaBootstrapP0/P0Repository.cs)转发 Store、head、prepare/commit；默认借用，Create/Open 工厂也可拥有 GraphResources | façade 不是第二个发布 owner；不命名为 Borrowed 或 RepositoryCore，不顺便删壳 |
| `P0Session` → `BranchSaveSession`；`P0SessionSave` → `PreparedBranchSave` | [声明](../../../src/DurableGraph.Persistence/MetaBootstrapP0/P0Session.cs)保存分支 State 工作区与 catalog 意图，准备一次共同保存 | 前者有寿命内状态，后者有一次准备／提交／释放寿命；不替代公开 BranchCheckout |
| `P0BusinessWorkspace` → `BusinessPersistenceAdapter`；`P0PreparedBusiness` → `PreparedBusinessWrite` | [声明](../../../src/DurableGraph.Persistence/MetaBootstrapP0/P0BusinessWorkspace.cs)包装 BusinessWorkspace，补 Store/source/S、typed Base、规划与实际安装 | 与共同 [BusinessWorkspace](../../../src/DurableGraph.Persistence/BusinessWorkspace.cs)的 Capture/身份职责不同，不能直接去前缀造成同名 |
| `P0PendingSave` → `PendingRepositorySave`；`P0SaveResult` → `RepositorySaveResult` | [pending](../../../src/DurableGraph.Persistence/MetaBootstrapP0/P0PendingSave.cs)、[结果](../../../src/DurableGraph.Persistence/MetaBootstrapP0/P0GraphContracts.cs)承载业务／可选 meta／共同发布材料 | Prepare、Append、发布结果、Confirmed 后安装仍分开；名字不授发布资格 |
| `P0GraphStore` → `GraphRecordStore` | [声明](../../../src/DurableGraph.Persistence/MetaBootstrapP0/P0GraphStore.cs)借用 FS，追加／读取 graph、batch、head directory，检查物理与来源关系 | 不负责 VS 发布、Upgrade 或领域实例物化；不能改成上游 FrameStore |
| `P0GraphKind` → `StoredGraphKind`；`P0GraphMetadata` → `GraphEnvelope`；`P0GraphSnapshot` → `StoredGraphRevision` | [声明](../../../src/DurableGraph.Persistence/MetaBootstrapP0/P0GraphContracts.cs)分别为三种存储角色、封套、地址＋完整 heads＋来源 owner | Revision 不含已物化 CLR 图，也不一定已发布；内部 Meta 与公开业务 Event/State enum 不合并 |
| `P0PreparedGraphWrite` → `PreparedGraphWrite`；`P0GraphWire` → `GraphFrameCodec` | [写材料](../../../src/DurableGraph.Persistence/MetaBootstrapP0/P0GraphContracts.cs)、[wire](../../../src/DurableGraph.Persistence/MetaBootstrapP0/P0GraphWire.cs)分别为 owned 对象记录／Remove 和 graph/batch/directory 三种帧的编码 | 不是完整交易或领域序列化器；保留全部已有关系校验、帧角色和编码 |
| `P0Batch` → `ObjectVersionBatch`；`P0ChainEntry` → `ObjectVersionChainEntry`；`P0ObjectChain` → `ObjectVersionChain` | [声明](../../../src/DurableGraph.Persistence/MetaBootstrapP0/P0GraphContracts.cs)持对象版本、准确 prior 和 batch 来源 | heads 指 batch 地址；Base→Delta 链与业务 ancestry 不混称 |
| `P0DirectoryCodec` → `ObjectHeadDirectoryCodec`；`P0DirectoryVersion` → `ObjectHeadDirectoryVersion`；`P0DirectoryResult` → `MaterializedObjectHeadDirectory` | [codec](../../../src/DurableGraph.Persistence/MetaBootstrapP0/P0DirectoryCodec.cs)、[版本](../../../src/DurableGraph.Persistence/MetaBootstrapP0/P0GraphContracts.cs)、[完整目录](../../../src/DurableGraph.Persistence/MetaBootstrapP0/P0GraphReadCache.cs) | 这里是 ObjectId→head，不是文件目录或 Schema catalog；完整结果保实际重建 payload 成本 |
| `P0TypedBodyCodec` → `TypedBaseBodyCodec`；`P0TypedReader` → `StoredGraphDecoder`；`P0DecodedRows` → `DecodedGraphRows` | [声明](../../../src/DurableGraph.Persistence/MetaBootstrapP0/P0TypedBodies.cs)编码／解释 Base 类型头，读取全部 live 行并核 origin/selected 来源与引用 | Delta 没有同类 Base 头；Decoder 不执行 Upgrade、Allocate 或 Hydrate，不与后续 GraphReader 合并 |
| `P0GraphReadCache` → `GraphPhysicalReadCache`；`P0GraphReadCacheStatistics` → `GraphPhysicalReadStatistics` | [声明](../../../src/DurableGraph.Persistence/MetaBootstrapP0/P0GraphReadCache.cs)复用完成封套、batch、directory version 和完整 heads | 不缓存 typed DTO／模型执行结果／领域实例；命中仍不能替代图语义和准确 S 验证 |
| `P0SaveCheckpoint` → `RepositorySaveStage` | [声明](../../../src/DurableGraph.Persistence/MetaBootstrapP0/P0GraphContracts.cs)是共同保存 hook 的阶段；[Kill 见证](../../../experiments/MetaBootstrapCrashProbe/Program.cs)据此选择窗口 | 与公共 Checkpoint 历史位置不同；只改符号，保 hook 位置、枚举值和实验窗口含义 |

`StoredGraphRevision.Objects` 实际是 head map，建议随该类型改为 `ObjectHeads`，收掉重复 `Heads` 入口并更新调用者。
GraphResources 上指向默认路径的内部 `MetaBootstrap*` 成员也按实际职责去掉路径选择含义，例如 `MetaBootstrapStore` → `GraphStore`。
自举本身仍是有效机制；说明固定解释依赖时可以继续使用 bootstrap，不能把所有出现该词的描述都当历史垃圾。

### 3.2 Schema catalog 与固定员工

| 当前名 → 推荐名 | 依据与限定 |
|---|---|
| `P0CatalogView` → `SchemaCatalogView`；`P0CatalogWorkspace` → `SchemaCatalogWorkspace`；`P0PreparedCatalog` → `PreparedSchemaCatalogWrite`；`P0CatalogSemantics` → `SchemaCatalogSemantics` | [view](../../../src/DurableGraph.Persistence/MetaBootstrapP0/P0CatalogView.cs)、[workspace](../../../src/DurableGraph.Persistence/MetaBootstrapP0/P0CatalogWorkspace.cs)、[规则](../../../src/DurableGraph.Persistence/MetaBootstrapP0/P0CatalogSemantics.cs)含完整用户 Schema 和容器 descriptor；不是独立发布的旧 SchemaStore |
| `P0CatalogRecord` → `SchemaCatalogRecord`；`P0DescriptorRecord` → `LayoutDescriptorRecord`；`P0FieldRecord` → `LayoutSlotRecord`；`P0TypeExprRecord` → `TypeExprRecord`；`P0HeadLocator` → `ObjectHeadLocator` | [员工声明](../../../src/DurableGraph.Persistence/MetaBootstrapP0/P0EmployeeModels.cs)；descriptor 也描述容器，slot 也服务元素/key/value/Nullable child；head locator 是固定地址状态，不能替代 FrameAddress |
| `P0EmployeeDefinitions` → `BootstrapDefinitions`；`P0FixedBindings` → `BootstrapBindings`；`P0BuiltinLayouts` → `BuiltinObjectLayouts` | [员工登记](../../../src/DurableGraph.Persistence/MetaBootstrapP0/P0EmployeeModels.cs)、[固定表](../../../src/DurableGraph.Persistence/MetaBootstrapP0/P0FixedBindings.cs)、[builtin 布局](../../../src/DurableGraph.Persistence/MetaBootstrapP0/P0BuiltinLayouts.cs)保有限员工/string 与 builtin-only structural 路由；不把 bootstrap 扩成任意定义发现服务 |

员工带 `DurableType`，单列一片改 CLR 名与 namespace，验证生成结果和冷读，再收完阶段空间。
不要为让字符串整齐改持久 SchemaId：`P0C/P0D/P0F/P0T/P0H`、V1、FieldId、字段类型保持；
相应 `Family_503043/503044/503046/503054/503048` 保持，因为 [FamilyName](../../../src/DurableGraph.Generator/DurableSchemaGenerator.GenericState.cs)取 SchemaId 的 UTF8 hex。

## 4. Persistence 的其他内部名称

本节当前／目标名均在 `Atelia.DurableGraph.Persistence`；nested 类型先保 nested，不借命名工作改文件或抽取结构。

| 当前名 → 推荐名 | 事实、传播范围与边界 |
|---|---|
| `StateModelSnapshot` → `ModelExecutionEnvironment` | [实现](../../../src/DurableGraph.Persistence/StateModelSnapshot.cs)含准确 lookup、按需闭合工厂和 memo；[FrozenConfiguration](../../../src/DurableGraph.Persistence/StateModelRegistry.cs)才区分冻结的配置。同步改返回该环境的 `Snapshot/CreateSnapshot` 为 `CreateEnvironment`；真正复制 reader 索引的 Snapshot 保留。不是搬入 Runtime 的理由 |
| `DecodedRevision.SplitBindingEnvironment` → `DecodedRevision.SchemaViewBindingEnvironment` | [实现](../../../src/DurableGraph.Persistence/DecodedRevision.cs)是携带实际 Store＋冻结配置＋准确 S 来源的绑定封套，内部持有上行 ModelExecutionEnvironment；复用仍核完整来源，两层不能合并。`ReadSplit` → `ReadStoredGraph`；只改默认路径标签，不扩大跨 Store／跨 S 共享 |
| `NormalizedRevision` → `NormalizedGraphState` | [实现](../../../src/DurableGraph.Persistence/NormalizedRevision.cs)既含归一化来源，也含未追加 candidate，后续才安装实际 heads/H；不用 PublishedRevision 或只服务保存的 SaveBaseline。`FromSplitCandidate/WithSplitGraph/RequireSplitSource` 改为表达真实来源／安装职责的名称，保原校验 |
| `MaterializedGraph<T>` → `EditableGraphRestoration<T>` | [声明与交付](../../../src/DurableGraph.Persistence/GraphReader.cs)除领域根还含 normalized baseline、ID 游标和反向身份，只由 editable delivery 产生；不把纯只读根包装成此材料 |
| `ObjectRevisionPlanner` → `ObjectWritePlanner` | [实现](../../../src/DurableGraph.Persistence/ObjectRevisionPlanner.cs)只分类／规划对象写入，source membership 和 Removes 在持久 adapter。同步纠正 [ObjectRepresentationPlan](../../../src/DurableGraph.Persistence/ObjectRepresentationPlan.cs)中仍声称 planner 供应 membership difference 的旧注释 |
| `CommitCheckpoint` → `RepositoryOperationStage` | [声明](../../../src/DurableGraph.Persistence/GraphCommitException.cs)为外观层阶段 hook，不是历史读取；与 RepositorySaveStage 各处于不同层。改 hook 名但不合并枚举、移注入点或顺手删未使用成员 |

`Split` 并不总是历史词：测试／包故事可能借它描述 shared/private 或准确视图场景。
这里只收回曾表示并行 backend 的内部标签，不批量改实验名、fixture 类型或历史记录。

## 5. Generator／Build 的分析材料

本节先改私有／内部材料，保 `.Generator`、`.Build` 与生成用户 API。

| 当前名 → 推荐名 | 证据与取舍 |
|---|---|
| Generator 私有 `GenericLayout/GenericField` → `FamilyLayout/FamilyField` | [声明](../../../src/DurableGraph.Generator/DurableSchemaGenerator.GenericState.cs)、[路径选择](../../../src/DurableGraph.Generator/DurableSchemaGenerator.TemplateHistory.cs)、[Family 汇聚](../../../src/DurableGraph.Generator/DurableSchemaGenerator.GenericFactories.cs)：非泛型 enum/record/Nullable、显式 Upgrade、外部模板和 forced definitions 也走此路径。表示 Family 路径的配套方法同步命名；真正描述 CLR 泛型或类型参数列表的 Generic 词保留 |
| Generator 私有 `DurableTypeModel/DurableFieldModel` → `DurableTypeAnalysis/DurableFieldAnalysis` | [声明](../../../src/DurableGraph.Generator/DurableSchemaGenerator.cs)是 Roslyn 符号与字段分析，含 enum 的无 CLR 字段合成项；不用只含实际声明意味的 FieldDeclaration，也不混称 Runtime 的执行 Model |
| Generator 私有 `SchemaHistoryModel/SchemaHistoryFieldModel` → `SchemaTemplate/SchemaTemplateField` | [声明](../../../src/DurableGraph.Generator/DurableSchemaGenerator.cs)、[current＋accepted＋referenced](../../../src/DurableGraph.Generator/DurableSchemaGenerator.GenericFactories.cs)均使用同一模板形状，history 只是来源之一；不合并 Runtime 的 StateSchemaTemplate |
| Build 内部 `SchemaHistoryDocument` → `SchemaHistoryTextCodec` | [声明](../../../src/DurableGraph.Build/SchemaHistoryTool.cs)是读取、解析、canonical render 和 SHA 文件命名的静态类，没有 Document 实例；发布／校验与闭包仍由 SchemaHistoryTool 承担 |

`BinaryVersionModel/BinaryFieldModel` 只用于普通直接生成路径，Family 也生成 binary body，名字有区分力不足的线索。
暂不定名：先确认对普通 `__DurableState` 路径的统一称呼，再考虑 `DirectStateVersionPlan/DirectStateFieldPlan`；不是第一片必改项。

Generator 入口 `Atelia.DurableGraph.Generator.DurableSchemaGenerator` 实际还生成 DTO/body、Capture/Hydrate、reader/model/definition、Upgrade、exports，
建议以后单片改为 `DurableGraphGenerator`，保唯一 `[Generator]`，不改 hint 或生成宿主。
入口 FQN 变化可能留下不同 compiler-generated 输出目录；[targets](../../../src/DurableGraph/build/Atelia.DurableGraph.targets)通配扫描并要求每种 manifest 恰好一份，
需验证干净构建和已有 obj 的升级情形。若要调整输出清理机制，另定明确范围，不能借私有类型改名隐含扩大修改。

CLI 目前只有顶层 `return 0`，没有值得改名的产品类型。`SchemaHistoryTool`、Shared 的 `TypePattern` 名称与构建期职责相符，保留。

## 6. 公开候选与尚不宜机械处理的问题

这些线索已证实，但不纳入内部命名第一片；具体采用需在公开迁移窗口再冻结映射。

| 当前完整类型名 | 提议与限制 |
|---|---|
| `Atelia.DurableGraph.Runtime.CapturedStatePreparation<T>` | 建议 `StatePreparationBinding<T>`，内部接口配为 `IStatePreparationBinding`。[声明](../../../src/DurableGraph/Runtime/Capture/CapturedStatePreparation.cs)是稳定 Schema/DTO/body/equality 能力绑定，Normalize 后和 [Pair 比较](../../../src/DurableGraph.Persistence/GraphReader.cs)也使用；[SG](../../../src/DurableGraph.Generator/DurableSchemaGenerator.StateModel.cs)直接发出此名称。不是一次准备结果，不因改名移除 equality 或精确绑定检查 |
| `Atelia.DurableGraph.Serialization.StringPayloadCodec` | 建议 `StringBodyPreparer`；[唯一操作](../../../src/DurableGraph.Serialization/StringPayloadCodec.cs)返回 raw PreparedBaseBody，不含 typed 头或 ObjectVersion payload。不为 Codec 的读写对称性增加 reader |
| `Atelia.DurableGraph.Persistence.GraphResources` | `RepositoryResources` 更能表达唯一 FS/VS、branch/shared、pending、fault owner。现 XML 已说明具体 owner，保名也可；收益与 public consumer 迁移成本比较后决定，不强制随 façade 更名 |
| `Atelia.DurableGraph.Persistence.GraphFrameKind` | 候选根空间 `Atelia.DurableGraph.CheckpointKind`，与 CheckpointAddress 的业务角色对应；[现 enum](../../../src/DurableGraph.Persistence/HistoryGraphRecord.cs)仅 Event/State。也可保现名，不与内部 StoredGraphKind 合并或加 Meta |
| `Atelia.DurableGraph.Persistence.GraphCommitException/GraphCommitOutcome` | 候选根空间 `RepositoryPublicationException/RepositoryPublicationOutcome`；[现合同](../../../src/DurableGraph.Persistence/GraphCommitException.cs)已覆盖 graph、branch-ref 和 tag。现行为无缺陷；不为根空间整齐自动搬 public companion，不改 outcome/fault/候选地址含义 |
| `.Runtime.StringValueProjection/DurableValueProjection<T>/ObjectValueProjection<T>` | 可分别明确为 StringReferenceProjection/DurableReferenceProjection/ObjectReferenceProjection；[实现](../../../src/DurableGraph/Runtime/State/BuiltinStateValues.cs)在引用与 ObjectId 间转换。收益中等，先不全改；保通用 IValueProjection，不授 object/interface 通配能力，不因未发现调用就删 public helper |
| `Atelia.DurableGraph.Schema.ObjectStateKind` | 可考虑 ObjectLayoutKind；[实际拥有者](../../../src/DurableGraph/Schema/ObjectLayout.cs)是布局，ObjectStateRecord 代理分类。收益较低，保现名可行，不借此统一所有 kind 数值／协议 |

`Atelia.DurableGraph.Schema.DurableFieldInfo` 有更深的概念张力：除 Schema 字段外，还作为 Array/List/Dictionary/Nullable 的完整值槽，
但结构仍带 FieldId。候选 `ValueSlot`／`ValueSlotLayout` 暂不冻结；纯改名是否仍清楚表达编号语义，需要结合 API 使用核对。
本轮不拆成 field/element/key 多套公开类型，不移除 FieldId、改编号或重写相等。

相关可观察的放置问题：四种 [Schema layout](../../../src/DurableGraph/Schema/DictionaryLayout.cs)为调用
[StateBindingContext.WithFieldId/NominalType](../../../src/DurableGraph/Runtime/Binding/StateBindingContext.cs)而依赖 Runtime。
两项是纯槽转换，适合以后归回槽描述；它们目前是 public static 方法，抽取还涉及调用点和 API。
单列后续职责整理，不把整个执行 context 搬到 Schema，也不据此立即拆程序集。

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
保持程序集／PackageId／ProjectReference／IVT／目标框架、文件位置和访问级别；不得用 rename 掩盖合并 owner、删校验或改行为。

| 片 | 有限范围 | 最小可观察验收 |
|---|---|---|
| A：Generator 分析材料 | §5 私有分析／Family／模板名与 Build 内部 codec，暂保 generator 入口 | 新旧 generated texts/hints、diagnostics 和 history 完整字节相同；覆盖普通路径、非泛型 Family、真实泛型、record、enum、Nullable、Upgrade、外部 fixed base/inline、forced definitions；根 build 与相关 SG/Build tests |
| B：默认内核与执行语义 | §3.1、catalog 普通内核及 §4；固定员工 CLR 名暂留，允许一片内显式引用旧员工 namespace，不建兼容类型 | 根 build、相关 Persistence/Runtime tests；核完整来源、head/H、Remove、准确 S、唯一 pending、Event/State 安装与 fault/lifetime；源码只有批准映射差异，当前活动包故事与 crash 见证按直接受影响入口适配 |
| C：固定员工收尾 | §3.2 员工 CLR 名／namespace／登记，收完 MetaBootstrapP0 | 对比 V1 layout、固定 binding 编号、Family DTO/body 与 history；真旧 G 写入、新 G 冷开／续写／再冷开，完整旧帧地址／内容保持；shared/private/meta 与目录均覆盖。不能用同一 runtime 改 HistoryVersion 代替真旧包 |
| D：公开有限迁移 | 只选择 §6 中有明确收益的名称；Generator 入口也单独处理 | 同步 emitted FQN、手写绑定、外部模型库与消费者；准确新 runtime/SG PackageReference 编译与运行，源兼容变化有迁移表。如测试旧 DLL，明确其是否要求重编译；不自动承诺二进制兼容 |
| E：组织判断 | 命名已稳定，再看真实引用与消费边界 | 分别提出文件归属与程序集方向；只有实证需要才增加项目、包或 namespace，不从这张名称表推导必须拆分 |

每次代码变更按 [仓库规则](../../../AGENTS.md#evidence-and-validation)运行根 `dotnet build DurableGraph.slnx` 和相关 tests；
依赖模式按[存储指南](../../storage-dependency.md)显式选择，包交付用准确 package mode 与独立 DG 版本。
只有相关包／生成合同发生变化时扩大到对应真实消费者；用现有有效门禁，不恢复 DB-091 已删除的 runner。

实施时额外核对以下容易逃过 IDE Rename 的接缝：

- [SG metadata identity](../../../src/DurableGraph.Generator/DurableSchemaGenerator.CrossAssembly.cs)与 [export 验证](../../../src/DurableGraph.Generator/DurableSchemaGenerator.SchemaExports.cs)：`global::`、GetTypeByMetadataName、准确 Generated helper 和 marker 所属程序集都是真实合同；不能只改拼接文本，或放宽为仅名称匹配。
- [持久 wire](../../../src/DurableGraph.Persistence/MetaBootstrapP0/P0GraphWire.cs)：PBB7/PBD7/PBG7、P0B1/P0D1/P0G1，发布根 P0R1/P0S1，typed ABI5、graph kind 值、完整 FrameAddress 编码保持。P0 字符串仍可作为协议历史标识存续。
- 生成 hints、export 合同 1/2、history 模板 v9、accepted `.dgschema` 文件名和字节保持；员工与用户 SchemaId／FieldId 不变。
- source rename 不等于可见性调整；namespace 迁移不改变 IVT 的程序集身份，也不改变 owner/lifetime、正常 backing 恢复或离线救援纪律。

## 9. 本轮结果与限度

已完成源码／调用／生成合同调查、主线程整合、独立方案质疑及实际文档复核，无遗留阻断项。
推荐表当前类型均可在跟踪源码定位，建议类型名未发现现有源码同名；这不是完整 Roslyn 符号依赖证明。
本轮没有改产品代码、namespace、类型名、程序集或 wire。集成 diff／空白检查通过，文档链接相对本任务基线新增失效为零；
原有 117 处历史引用缺口未扩入本案。不以旧测试数字授重命名后资格，未运行产品 build/test/pack 或外部下游验证。
后续首次施工优先 A 与 B，员工与公开变化独立验收；是否执行以及当片映射以用户后续选择为准。
