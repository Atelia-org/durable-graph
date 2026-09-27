# DurableGraph 项目术语表

> 用语校准：2026-09-27；公共基础见 [078-A 记录](design-branches/0078-a-repository-free-history-implementation.md)，Checkpoint/固定查询见 [078-B 记录](design-branches/0078-b-checkpoint-history-query-implementation.md)。[DB-083](design-branches/0083-repository-checkpoint-api-user-stories.md) 的后续目标/候选词义仍不作为已实施合同。
> 本文是项目首选术语的维护入口；定义概念、区分歧义并映射代码，不代替各分片合同。
> 公共入口为根命名空间 `Atelia.DurableGraph` 下的 `Repository`、非泛型 `BranchCheckout` 与 `CheckpointAddress`；签出操作为 `Checkout`。旧名只作历史映射，固定模型环境由本次 Open 持有。
> 当前能力查 [PROJECT-STATE](../src/PROJECT-STATE.md)，长期约束查[目标设计](DurableGraph-target-design-v0.md)，未完成工作查[路线图](DurableGraph-research-roadmap.md)。

## 使用与维护

- 表中粗体为讨论和说明文字的首选术语；代码符号标明**当前落点**，不意味着所有符号已经采用首选命名。
  同一行并列的词由释义说明其关系，只有明确标为同义才可互换。
- 已实现词条以当前源码为依据；遇到词义与实现不一致，应记录差异并修订，不能据词条推定代码已有某种行为。
  标为“目标”的词只链接设计，不虚构代码落点。本文不维护实现进度或测试计数。
- 解释 `Current`、`Base/Delta`、`head`、`body/payload` 等词时保留所属对象或阶段；代码引用保留符号原拼写。
  本文内的链接锚点按概念命名，后续改名尽量保留原锚点供旧链接检索。
- 概念演变时同步更新首选词、边界和旧称映射。确有等价改名证据才写“旧称 → 新称”；
  拆分、合并或职责改变需说明对应关系。历史文档保留当时用语及上下文，迁移范围由独立变更确定。
- 生成符号标注“SG 生成”，链接生成器发出位置或代表性测试；不依赖临时 `obj` 路径。
  每项取最能解释含义的代码入口，避免扩展为所有方法的 API 索引。
- 新设计及活动说明采用本表首选词；当前可执行示例、源文件路径、测试名和代码符号保留实际拼写，并在入口说明映射。
  已完成的历史记录和 archive 保留当时称谓；只在仍作为入口的旧草稿补充现行术语链接，不逐篇追改历史。

## 先区分这几个公共概念

**仓库持有分支；签出分支得到工作副本；修改领域状态并显式提交到它绑定的分支。**
历史允许 Event-first、连续 Event 和连续 State；事件处理顺序由应用决定。
Checkout 不切换 Repository 的全局“当前分支”。Commit 后工作副本继续存活；Dispose 释放编辑占用，不自动保存、撤销领域修改或删除分支。

| 首选词 | 含义与边界 | 代码与设计状态 |
|---|---|---|
| **仓库（Repository）** | 持久资源与命名分支的宿主。讨论寿命时写“本次打开的仓库实例”，同一路径重开是新实例。 | 当前 [Atelia.DurableGraph.Repository](../src/DurableGraph.Persistence/Repository.cs)。 |
| <a id="branch"></a>**分支（Branch）** | 指向已发布历史位置的持久命名引用（ref）；不等于加载的 CLR 对象图，也不因关闭工作副本而消失。 | 当前已有 CreateBranch / MoveBranch；[发布 head](#publication-head)。 |
| <a id="branch-checkout"></a>**分支工作副本（Branch checkout）**，上下文明确时简称**工作副本** | 绑定一个分支的可选 State、已提交 State 保存基线、身份分配状态与当前 head 的组合；不能简化为根对象本身。 | 非泛型 [BranchCheckout](../src/DurableGraph.Persistence/BranchCheckout.cs)，State 为 `IDurableObject?`，null 仅表示尚无 State；允许实际根跨类型替换，无 PendingEvent。 |
| <a id="checkout-operation"></a>**签出（Checkout）** | 从已有分支当前 head 恢复并取得一个工作副本，不新建分支，也不自动执行事件处理器。 | `Checkout(branchName)` 保持精确 Head，只恢复最近 State；纯 Event 历史不物化 Event，交付 State=null；已有 State 恢复失败直接传播。 |
| <a id="state-event"></a>**领域状态 / 领域事件（State / Event）** | 逻辑历史中的两种角色，不由 CLR 类型名决定。允许 Event-first、连续 E 或 S，同一 CLR 类型也可承担两种角色；应用自己规定处理顺序，每份持久图仍选非空根。 | 当前 `GraphFrameKind` 与 `CommitEvent` / `CommitState`。 |
| <a id="pending-event"></a>**待完成事件（PendingEvent，历史交替协议用语）** | 旧协议中已记录、等待后继 State 提交的 Event；不是尚未提交的事件。当前仓库不判定业务处理进度，应用可自行使用此概念；不保证外部副作用 exactly-once。 | 旧 `EventHistorySession.PendingEvent` 已移除；当前 Checkout 不恢复 Event。 |
| <a id="checkpoint"></a>**已提交检查点（committed checkpoint）** | 可用来恢复或分叉的已发布 State/Event 历史位置。其角色与图内容由该位置确定；不因分支继续推进而改变。读取视图与可保存工作副本分别定义，不能从 Event 位置自动推断业务已完成。 | 在本次仓库实例中用 [CheckpointAddress](#checkpoint-address) 指代；仅 RevisionAddress 不足以表达其完整含义。 |
| <a id="checkpoint-view"></a>**检查点读取视图（Checkpoint）** | 非泛型 EventCheckpoint / StateCheckpoint，直接提供本项与最近严格祖先异种角色的领域根及地址；PreviousX 缺失时两者均空，不递归构造视图。首版 eager 至多两图，各根 getter 稳定；修改不自动保存，两图可变隔离。 | [Checkpoint](../src/DurableGraph.Persistence/Checkpoint.cs)、[078-B 记录](design-branches/0078-b-checkpoint-history-query-implementation.md)；当前项根始终非空。 |
| <a id="checkpoint-address"></a>**检查点地址（CheckpointAddress）** | 定位 Journal 逻辑历史位置的 opaque 地址；不是裸 StateRevision 地址。限同次打开，以 owner 和逻辑位置判等；重开从持久 branch ref 取得新地址，null 与外实例地址拒绝，无外部序列化入口。 | 当前 sealed class [CheckpointAddress](../src/DurableGraph.Persistence/CheckpointAddress.cs)，只由库签发；公开 Kind、RootId 与诊断用 RevisionAddress 不构成来源认证。 |
| <a id="immutable-tag"></a>**不可变标签（tag，目标）** | 仓库内持久名字绑定固定历史位置；创建后不移动、覆盖或删除，按名解析签发本次打开的新地址。与可移动 branch 分名空间，不占工作副本。 | [DB-084](design-branches/0084-eventjournal-immutable-tags-slice.md)，持久权威归上游 EventJournal；上游本地包已交付，当前 DG pin 与公开 API 尚未接入。 |
| <a id="fork"></a>**分叉（Fork）** | 从已提交检查点创建新命名分支并交付工作副本，Head 保持精确选点；有 State 则独立恢复，无 State 则 State=null；准备完成后发布 ref。源工作副本未提交的修改不参与；发布后失败仍须检查实际结果。 | `Fork(name, address)`；与分步 `CreateBranch(name, address)` + `Checkout(name)` 的成功结果等效，但普通恢复失败不先留下新 ref；见 [DB-078-C](design-branches/0078-c-branch-checkout-fork-implementation.md)。 |
| <a id="commit"></a>**提交（Commit）** | 捕获并持久发布到工作副本绑定的分支，成功后推进 head；State 提交另安装保存基线。不是单纯 Append 或 CaptureSession.Accept。 | `CommitEvent` / `CommitState` 各自独立提交；无参 State 提交要求已有 State，显式非空根可建立首 State 或跨类型替换，均非隐式事件处理器。 |
| <a id="revision"></a>**状态修订（Revision）** | 内容固定的一次存储层修订，包含对象版本记录及存活目录信息；可承载领域 State 图或 Event 图。不是持续编辑的工作副本。 | 当前 [StateRevision](#state-revision)；尚未追加的实例也可表示待写计划，不能据类型名推定已经发布。 |

签出数量与线程安全是不同问题：**每分支至多一个活动工作副本**，不同分支可各有一个，同分支第二次签出拒绝。
显式 Dispose 释放占用，不由 GC 自动解锁。仓库操作仍串行；“多个工作副本”不表示多线程或多 writer。
`CreateBranch(name, initialState)` 创建初始 S0，`CreateBranchFromEvent(name, initialEvent)` 创建初始 E0，均交付工作副本；
`CreateBranch(name, address)` 仅创建 ref；它们都不应简称成签出。

### 命名选择与迁移边界

采用 `BranchCheckout` 是为了同时表达“绑定分支”和“加载后持续编辑”的寿命，避免占用下游业务常用的 Session。
`Revision` 留给固定修订；`Workspace` / `WorldWorkspace` 留给内部 State 保存工作区；`WorkingTree` 不作为对象图的首选称谓。
公共基础已按 DB-078-A 迁移，不保留旧名、泛型工作副本或泛型历史读取兼容重载；后续能力仍按阶段交付。

| 上下文 | 使用方式 |
|---|---|
| DB-076–082 的拟议公开 API | 使用 `BranchCheckout` / `Checkout`，按具体分片区分当前能力与未实施目标。 |
| 当前 README 可运行示例、源码证据 | 使用 `Repository` / `BranchCheckout` / `Checkout` / `CheckpointAddress`；历史记录和见证名称可保留旧名。 |
| 内部 `CaptureSession` / `RevisionReadSession` | 仍称捕获会话 / 操作内解码会话；不因公共命名调整而改名。 |
| Coding Agent 主会话、LLM tool-loop session、兄弟项目类型 | 按其自身语义保留 Session / 会话，不套用分支工作副本。 |

## 恢复与复用路线的用语

以下只确定词义；尚未实施的机制以各设计状态为准，不把术语变成新公共类型。

| 首选词 | 含义与边界 | 依据 |
|---|---|---|
| <a id="model-environment"></a>**模型执行环境（model environment）** | 冻结的模型/定义/provider/策略配置及按需闭合的执行能力。配置冻结不冻结委托捕获的外部状态，也不冻结 SchemaStore 的实时权威。 | 内部 `StateModelSnapshot` 由本次打开的仓库持有；独立低层读取入口仍可自行取得快照。见 [DB-077 实施](design-branches/0077-model-environment-implementation.md)。 |
| <a id="restoration-preparation"></a>**恢复准备（restoration preparation / Prepare）** | 为选定根准备完整 source/current DTO、类型检查、可达信息及来源，尚不交付领域实例；与写入侧内容准备区分。 | `GraphReader.Prepare` 共用流程；[DB-079 实施记录](design-branches/0079-shared-graph-restoration-core-implementation.md)。 |
| <a id="materialization"></a>**物化（Materialize）** | 依据准备材料建立对象身份表，完成 Allocate/登记及 Hydrate，再交付领域图；策略允许的已有实例可复用。可编辑恢复还须导入独立保存状态。 | [实例分配与填充](#allocate-hydrate)、DB-079；不把解码 DTO 简称为领域图物化。 |
| <a id="prepared-selection"></a>**单图准备材料（prepared selection）** | 一个所选根的 normalized baseline、根类型/可达信息及来源；完整基线含不可达 source 行，不能仅留下可达 DTO。 | [PreparedGraphSelection](../src/DurableGraph.Persistence/PreparedGraphSelection.cs)；不是 live 领域图、跨操作复用证书或新公开 ForkSource。 |
| <a id="prepared-checkpoint"></a>**State 检查点准备材料（prepared State）** | 最近 State 的单图准备材料及复用证书；不含请求 Head 或 Event 图。每次先独立验证 Head→最近 State，再按精确 State 查槽；无 State 时绕过槽/证书/叶表，不影响其他分支槽。Head 始终取本次请求。 | [PreparedStateRestoration](../src/DurableGraph.Persistence/PreparedStateRestoration.cs)、[DB-080 记录](design-branches/0080-prepared-checkpoint-reuse-implementation.md)；同一 State 后不同 Event 选点可复用，数量有界不等于字节或总堆有界。 |
| <a id="schema-dependency-certificate"></a>**精确 Schema 依赖证书（exact Schema dependency certificate）** | 被复用步骤所依赖的完整精确 Schema 要求集合；每次复用仍对实时 SchemaStore 复核。这里的证书是内部验证材料，不是签名文件或可跨仓库移植的授权。 | [内部词法收集](../src/DurableGraph/Runtime/Binding/StateBindingContext.Requirements.cs)、DB-080；包含历史中间依赖及预闭合工厂的标准检查证据，不能仅核对两端。 |
| <a id="immutable-leaf"></a>**不可变叶对象（Immutable leaf）** | `IsImmutableLeaf` 表达的 durable 领域对象资格：恢复后无可变实例状态或引用边。生成绑定执行最终结构核对，手写 true 是调用者断言。它不是“DTO 已冻结”的同义词，也不是任意 readonly 字段类型。 | [DB-075](design-branches/0075-immutable-leaf-proof-and-family-refactor.md) 已实现资格与最终结构核对；[DB-082](design-branches/0082-prepared-immutable-leaf-reuse-slice.md) 的实例复用仍是条件实验。 |
| <a id="readonly-immutable"></a>**只读使用 / 不可变状态 / 不可变实例** | ReadPair 的只读是调用者使用合同（含 Transient），可共享原本可变的类型；冻结 DTO 是恢复材料不受 live 修改影响；不可变叶资格才涉及领域实例可否在可编辑图间共享。 | 三者分别证明不同事情；ReadPair 的共享闭包不等于深不可变闭包。 |

使用“快照（snapshot）”须限定为模型配置快照、冻结 DTO 快照或只读历史图；其中只读历史图不承诺 CLR 深不可变。
使用“基线（baseline）”须指出已提交 State 保存基线、待安装候选基线或性能测量基线。
“恢复”指从历史材料还原图和必要保存状态；“重放业务事件”由应用负责，两者不可混称。

## 一张流程图定位阶段

```mermaid
flowchart TD
    R["CheckpointAddress：角色、Revision 地址与 RootId"] --> D["stored DTO 全目录：解码并验证"]
    D --> N["current DTO 全目录：Upgrade 后再验证"]
    N --> C["根可达闭包"]
    C --> A["建立全部实例表，再 Hydrate"]
    A --> W["领域 State 或 Event 图"]
    New["普通 new 领域图"] --> W
    W --> F["Capture / Seal：冻结候选图"]
    F --> P["准备内容、选择表示、生成计划"]
    N -. "前 State 比较基线" .-> P
    P --> S["Schema/State 屏障 → Journal 帧 → branch ref"]
    S --> I["State：安装同一候选基线与 live 身份"]
    S --> E["Event：释放候选，保持 State 基线"]
    I -. "保留领域实例继续编辑" .-> W
```

分支工作副本 `BranchCheckout` 受控完成发布与安装；低层 Append 自身仍不发布。
Journal Parent 是提交前精确 Head；Revision Parent 是该 Head 或其祖先中最近 State，无 State 时为 null。
例如 S0→E1→E2→S1 的后三项均以 S0 为图保存来源，Event-first 的 E0→E1→S0 则全部无图 Parent。
内容层 `CaptureSession.Prepare` 只消费已经 Seal 的候选。各阶段推进什么状态见下列词条。

## Schema、类型与生成代码

统一目录的设计与实现证据见 [DB-046](design-branches/0046-unified-schema-catalog-slice.md)。

| 项目内术语／代码符号 | 释义与示意 | 关键代码 |
|---|---|---|
| <a id="schema-family"></a>**定义身份与闭合 Schema 族（definition identity / closed Schema family）** | DefinitionId 标识声明，例如 `Box<T>` 的 `Box`；闭合族还包括有序类型实参，`Box<int>` 与 `Box<Point>` 是不同族。`DurableSchema.SchemaId` 保留定义 ID 便捷入口，不能独自区分泛型闭合族；两者都不指定版本。 | [TypeExpr](../src/DurableGraph/Schema/TypeExpr.cs)、[DurableSchema](../src/DurableGraph/Schema/DurableSchema.cs) |
| <a id="type-expression"></a>**类型表达与定义模式（TypeExpr / type pattern）** | `Builtin(tag)`、`Named(DefinitionId, args)`、SZ/rank 2–4 数组及 `List(element)`、`Nullable(child)`、`Dictionary(key,value)` 构造递归表达支持范围内的名义类型；定义模板还可含声明内的 `Parameter(ordinal)`，如 `List<T[]>`。List/Nullable 是内建一元构造，Dictionary 是内建双实参构造，不使用 Named 冒充用户定义。闭合 Schema/key 不允许 Parameter；类型形参名字、CLR Type/helper 类型不是持久身份。 | [TypeExpr](../src/DurableGraph/Schema/TypeExpr.cs)、[类型表达 wire](../src/DurableGraph.Persistence/TypeExprWireCodec.cs) |
| <a id="schema-key"></a>**Schema 键（SchemaKey）** | `(闭合 TypeExpr, DefinitionVersion)`，由完整用户 Schema 派生的查询、冲突索引；非泛型是 `Named(id, [])`。统一目录不单独编码 SchemaKey，exact 依赖使用记录 ID。相同键仍须核对完整定义，不承诺两个独立空仓库的同键布局相同；key 不能代替完整布局或用于跨库互换。 | [SchemaKey](../src/DurableGraph.Persistence/SchemaKey.cs)、[SchemaStore](../src/DurableGraph.Persistence/SchemaStore.cs) |
| <a id="representation-id"></a>**持久表示 ID（RepresentationId）** | 仓库内对象表示的持久整数入口，使用统一目录编号空间；0 无效，1 固定 string，动态节点从 2 单调登记，不回收、不跨仓库解释。class 的 Schema 记录直接作为其表示；inline 节点虽占用编号，但不是对象表示，GetRepresentation/Base 必须拒绝。相同完整表示复用，不同名义身份不能因 DTO CLR 类型相同而合并；数组 shape、容器 Count、Dictionary comparer 不参与。ID 不标识对象实例或 Revision，也不锁定引用目标版本。 | [RepresentationId](../src/DurableGraph.Persistence/RepresentationId.cs)、[SchemaStore](../src/DurableGraph.Persistence/SchemaStore.cs)、[统一目录记录](../src/DurableGraph.Persistence/SchemaCatalogEntry.cs) |
| <a id="schema-catalog-entry"></a>**闭合目录记录（SchemaCatalogEntry）** | 一条有持久编号的 reference Schema、inline Schema、数组、List 或 Dictionary 描述；string 为隐式内建项。List/Dictionary 分别使用目录 kind 4/5，不产生用户 Schema。base/inline 边指向已登记 exact 记录，普通引用槽保存无版本 nominal 约束。用户 Schema 记录包含完整名义身份、定义版本和本层字段；读取时重建 DurableSchema/ObjectLayout 视图，不另存 class 表示到 Schema 的转接记录。 | [SchemaCatalogEntry](../src/DurableGraph.Persistence/SchemaCatalogEntry.cs)、[SchemaCatalogWireCodec](../src/DurableGraph.Persistence/SchemaCatalogWireCodec.cs) |
| <a id="exact-schema"></a>**完整精确 Schema（exact Schema）**／`DurableSchema` | 一版不可变闭合定义：完整名义类型、定义版本、SchemaKind、本层字段及完整 base/inline 精确依赖闭包。`DurableSchema.Equals` 检查这些内容；“同 exact Schema”比“同 Schema 键”更强。本文不加限定的 Schema 指完整定义。 | [DurableSchema.Equals](../src/DurableGraph/Schema/DurableSchema.cs) |
| <a id="definition-template"></a>**定义模板（definition template）**／`StateSchemaTemplate` | 一版开放声明的 kind、arity、字段模式及固定 base/inline 版本。它与完整 stored Schema 结构匹配后才能得到该次历史执行布局；模板本身不替代闭合 exact Schema。DTO 参数与 CLR 工厂信息属于派生执行材料，不额外形成持久身份。 | [StateSchemaTemplate](../src/DurableGraph/Runtime/Binding/StateDefinitionBinding.cs)、[StateBindingContext.BindSchema](../src/DurableGraph/Runtime/Binding/StateBindingContext.cs) |
| <a id="base-schema"></a>**精确基 Schema（exact BaseSchema）** | 派生定义绑定某一完整基类定义，例如 `Hero V3 → Character V2`。基类定义改变会改变派生定义，派生版本也须递增；这里的 Base 是继承关系，不是对象内容 Base。 | [DurableSchema.BaseSchema](../src/DurableGraph/Schema/DurableSchema.cs)、[祖先历史验证](../tests/DurableGraph.Tests/SchemaAncestryHistoryTests.cs) |
| <a id="schema-kind"></a>**Schema 种类（SchemaKind）** | ReferenceObject 表示具有对象身份的用户引用类型，InlineValue 表示嵌套值布局；同 DefinitionId 跨版本不得切换 kind 或 arity。不同于 ObjectStateKind 的 durable/string/array/list 内容分类，也不同于目录行的编码 kind。 | [SchemaKind](../src/DurableGraph/Schema/SchemaKind.cs)、[DurableSchema](../src/DurableGraph/Schema/DurableSchema.cs) |
| <a id="inline-value"></a>**内联复合值（inline value）／精确值 Schema** | 用户直接标记 DurableType 的 struct（含 record struct），拥有 Schema/history；字段的 InlineSchema 绑定 exact 布局，变化沿 inline/base 传播。record 属性以 field-target 标注实际 backing storage，不持久化属性方法或合成比较。DTO/body 嵌套而非 CLR 声明嵌套；无独立对象行、Model 或自动 Upgrade。 | [DurableFieldInfo.InlineSchema](../src/DurableGraph/Schema/DurableFieldInfo.cs)、[共享值 DTO/body 发出逻辑](../src/DurableGraph.Generator/DurableSchemaGenerator.InlineState.cs)、[record 字段合同](design-branches/0056-record-struct-state-slice.md) |
| <a id="durable-enum"></a>**持久枚举（Durable enum）** | 显式 DurableType 的用户 enum；复用 InlineValue Schema，以独立 nominal/版本及 FieldId=1 的底层整数槽描述。生成 Family DTO/body，外置投影静态转换领域 enum；不持久化常量表/Flags，不拒绝未命名数值。底层布局变化须升版，数值业务重解释由显式 Upgrade 负责。无独立对象行或新格式；不同 enum 不因整数宽度相同合并身份。 | [enum 生成](../src/DurableGraph.Generator/DurableSchemaGenerator.Enums.cs)、[当前模板校验](../src/DurableGraph/Runtime/Binding/StateDefinitionBinding.cs)、[DB-053](design-branches/0053-enum-inline-state-slice.md) |
| <a id="builtin-scalar"></a>**内建标量值（builtin scalar value）** | 无递归子槽的框架内建值，不等于 CLR Primitive 或固定字节宽度。包括原 13 种标量及 Guid/decimal/TimeSpan、DateOnly/TimeOnly/DateTimeOffset；DTO 直接保留值，不创建用户 Schema 或对象行。Guid 完整 128 bit、decimal 四字含 scale/符号、TimeSpan 有符号 Ticks；日期时间值保留 DayNumber、日内 Ticks 或 clock Ticks + offset，同瞬间不同 offset 也不同状态。string 仍是引用对象。 | [TypeTag](../src/DurableGraph/Schema/TypeTag.cs)、[BCL 静态操作](../src/DurableGraph/Runtime/State/BclScalarStateValues.cs)、[日期时间静态操作](../src/DurableGraph/Runtime/State/TemporalScalarStateValues.cs)、[完整 decimal 比较](../src/DurableGraph.Serialization/ScalarStateEquality.cs) |
| <a id="nullable-value"></a>**可空值槽（Nullable value slot）**／`NullableValueLayout`、`NullableState<TState>` | 领域使用 `T?`，冻结状态保存有值/无值标记及 unmanaged child；完整槽保留 child exact 布局，ValueSchema 暴露内部 inline 依赖。absent 不访问 child 或形成引用边；包装无独立 Schema 版本、ObjectId 或目录行。显式 `AllowNullableLifting` 可提升已绑定 child 升级工具，但空内容不免除能力预检。 | [NullableValueLayout](../src/DurableGraph/Schema/NullableValueLayout.cs)、[状态与静态操作](../src/DurableGraph/Runtime/State/NullableStateValues.cs)、[DB-052](design-branches/0052-nullable-value-slot-slice.md) |
| <a id="nominal-reference"></a>**名义引用约束（nominal reference constraint）**／`TargetType` | 引用槽约束目标的完整闭合族，不绑定目标版本。实际目标 Schema 自身或其祖先须属于该族；泛型实参不自动协变。目标升版不使引用方升版，改变槽约束则改变引用方 Schema；旧 `TargetSchemaId` 只是定义 ID 便捷入口。 | [DurableFieldInfo](../src/DurableGraph/Schema/DurableFieldInfo.cs)、[StateReferenceValidator](../src/DurableGraph/Runtime/Binding/StateReferenceVisitor.cs) |
| <a id="field-id"></a>**持久字段编号（FieldId）与声明段** | FieldId 在声明它的 Schema 内唯一，继承链各段独立编号；基类和派生类都可有字段 1。Schema 字段类型由 TypeTag 描述；ObjectReference tag 15 携带 closed Named/Array/List/Dictionary 约束，string 槽仍用 tag 4。引用 DTO 槽为 ObjectId，wire ID 编码为 UInt32。 | [DurableFieldInfo](../src/DurableGraph/Schema/DurableFieldInfo.cs)、[TypeTag](../src/DurableGraph/Schema/TypeTag.cs) |
| <a id="state-dto"></a>**版本化状态 DTO（Versioned state DTO）** | 与 exact Schema 配对的 readonly/unmanaged 状态值：标量存值，引用存非泛型 `ObjectId`，inline struct 嵌套 exact DTO，包含祖先字段。泛型 DTO 只带状态表示参数，不带领域/helper 参数；相同 DTO CLR 类型仍不证明槽语义或 exact Schema 相同。 | [Family DTO/body 发出逻辑](../src/DurableGraph.Generator/DurableSchemaGenerator.GenericState.cs)、[原非泛型 DTO 发出逻辑](../src/DurableGraph.Generator/DurableSchemaGenerator.GeneratedState.cs) |
| <a id="state-family-host"></a>**状态族宿主（generated Family host）**／`Generated.Family_<UTF8HexId>.Vn<TState…>` | 泛型、组合值（含 Nullable）、当前 enum/record struct、显式 owner/value Upgrade、有当前声明且保留孤立 inline history 或需要外部 exact base/inline 模板的编译使用的独立生成宿主；历史 DTO/body 不嵌入当前领域类型，可由普通 using 别名命名。`DurableGraphGenerateDefinitions=true` 可显式启用；其余使用普通路径的编译保留 `__DurableState.Vn` 及共享 inline helper。这是生成 API 路径差异，不是两套持久身份。 | [Family 发出逻辑](../src/DurableGraph.Generator/DurableSchemaGenerator.GenericFactories.cs)、[真实消费者 Upgrade](../experiments/PackageConsumerProbe/GenericConsumer/Upgrades.cs) |
| <a id="schema-history"></a>**Schema 历史材料（Schema history）**／`.dgschema` | 构建期积累的定义模板记录，含声明 arity、参数模式与固定依赖版本，供 SG 重建历史状态代码；不是某次对象状态，也不枚举全部闭合布局。当前新写 v9：v5/v6/v7 分别引入 List/Nullable/Dictionary，v8 引入 Guid/decimal/TimeSpan，v9 引入 DateOnly/TimeOnly/DateTimeOffset；已有 v1–v8 文件原样保留，各版递归拒绝不属于该版的构造或叶子。manifest 描述本次候选；已接受历史可使用只读外部模板闭合，当前候选不能修补旧缺口，外部记录不发布为本地 history。 | [SchemaHistoryTool](../src/DurableGraph.Build/SchemaHistoryTool.cs)、[模板解析](../src/DurableGraph.Generator/DurableSchemaGenerator.TemplateHistory.cs) |
| <a id="schema-export"></a>**只读声明模板导出（Schema export）** | 定义库通过 `DurableSchemaExportAttribute` 提供的当前/保留声明模板及生成执行合同版本：InlineValue 为 1，ReferenceObject 为 2。消费方按需经编译元数据读取 exact base/inline 闭包并复用公开状态能力；reference manifest 为本次 Build 的派生输入，不是新持久目录或运行时能力登记。引用闭包与本地归属严格分开，内部领域实现类型无需变为 public。 | [导出标记](../src/DurableGraph/DurableSchemaExportAttribute.cs)、[SG 导入导出](../src/DurableGraph.Generator/DurableSchemaGenerator.SchemaExports.cs)、[DB-061](design-branches/0061-cross-assembly-inheritance-slice.md) |
| <a id="schema-store"></a>**持久 Schema 与表示目录（SchemaStore）** | 在 `schemas.rbf` 追加保存统一闭合目录；同一批次包含全部缺失节点及其 base/inline exact 依赖，一次追加与持久屏障后安装。校验同键完整定义和 kind/arity，nominal 引用不形成 exact 依赖。登记独立于 State 发布；Count 只统计用户 Schema，含 inline。只持久元数据；ResolveReader 按本次冻结代码目录取得历史读取绑定，不全局缓存另一 snapshot 的 reader。 | [SchemaStore](../src/DurableGraph.Persistence/SchemaStore.cs)、[统一目录格式](../src/DurableGraph.Persistence/SchemaCatalogWireCodec.cs) |
| <a id="definition-binding"></a>**定义能力绑定（StateDefinitionBinding）** | 某声明的保留模板、当前/历史工厂及 owner Upgrade providers；程序集内的 `Generated.DurableDefinitions.Register` 显式登记；跨程序集经各库公开 facade 聚合，不枚举全部闭合组合。模型配置快照按实际 CLR 类型或 stored exact Schema 按需闭合为 value/reader/model binding；nominal/动态参数、固定 inline 和基类可以跨库，构建期导入不代替此登记。 | [StateDefinitionBinding](../src/DurableGraph/Runtime/Binding/StateDefinitionBinding.cs)、[模型配置快照](../src/DurableGraph.Persistence/StateModelSnapshot.cs) |
| <a id="value-binding"></a>**值槽绑定与静态操作（StateValueBinding / IStateOps / IValueProjection）** | 值槽绑定把完整槽语义与派生的状态/操作 CLR 类型配对；历史 IStateOps 只处理冻结 DTO，当前 IValueProjection 在领域值与状态间转换。SG 已知字段直接调用，未定泛型值经闭合静态 helper 调用；这些 CLR 类型不写入持久身份。 | [静态值合同](../src/DurableGraph/Runtime/Binding/StateValueBinding.cs)、[绑定与结构匹配](../src/DurableGraph/Runtime/Binding/StateBindingContext.cs) |
| <a id="state-equals"></a>**持久状态相等（StateEquals）** | 同一 exact 值槽下的静态比较能力，正常调用不编码、不装箱、无堆分配。按持久字段比较，浮点按 bit pattern、decimal 按 GetBits 完整表示、DateTimeOffset 按 EqualsExact、引用按 ObjectId，忽略 padding/Transient/领域 Equals；与 `!PrepareDelta(a,b).HasChanges` 一致。List 匹配用它试探，真正需要子 patch 的元素对才准备并复用 Delta bytes；不要求普通 owner 额外完整预扫。 | [IStateOps](../src/DurableGraph/Runtime/Binding/StateValueBinding.cs)、[内建比较](../src/DurableGraph/Runtime/State/BuiltinStateValues.cs)、[泛型生成](../src/DurableGraph.Generator/DurableSchemaGenerator.GenericState.cs)、[传统 DTO 生成](../src/DurableGraph.Generator/DurableSchemaGenerator.GeneratedState.cs) |
| <a id="dictionary-key-equality"></a>**字典查找相等与持久键相等** | 查找相等由领域 Dictionary 的 comparer 决定；持久相等以同 exact key 槽的完整 canonical Base bytes 对应 frozen 条目。业务可忽略需要保存的 Timestamp；内容相同但 ObjectId 不同的 string 键也可查找等价而持久状态不同，后者应 Remove+Add。CurrentDefault 使用当前 CLR Default，Application 使用 snapshot 的闭合类型选择；历史 DTO 不执行这两种模式的当前业务 lookup，实际 TryAdd 才拒绝当前碰撞。 | [Dictionary body](../src/DurableGraph/Runtime/Containers/DictionaryStateReader.cs)、[key policy](../src/DurableGraph/Runtime/Containers/DictionaryKeyPolicy.cs)、[复合 Key 设计](design-branches/0055-composite-dictionary-key-design.md) |
| <a id="reader-binding"></a>**精确版本读取绑定（ObjectReaderBinding / StateReaderBinding）** | 一份 exact ObjectLayout 与 Base reader、同布局 Delta applier、引用遍历的绑定；独立于当前 CLR 实例及 Upgrade 能力。StateReaderBinding 是用户 class Schema/DTO 的适配器，ArrayStateReader / ListStateReader / DictionaryStateReader 依据 exact 元素或 key/value 槽 绑定历史元素操作，不要求旧领域 struct CLR 类型。 | [对象读取合同](../src/DurableGraph/Runtime/Binding/ObjectBinding.cs)、[StateReaderBinding](../src/DurableGraph/Runtime/Binding/StateReaderBinding.cs)、[ArrayStateReader](../src/DurableGraph/Runtime/Containers/ArrayStateReader.cs)、[ListStateReader](../src/DurableGraph/Runtime/Containers/ListStateReader.cs) |
| <a id="object-binding"></a>**当前对象绑定（ObjectBinding）** | 一个受支持 CLR 引用类型的 current layout、Capture、Normalize、引用遍历、Allocate(record)/Hydrate 能力；统一承接 class、string、array、List、Dictionary。对象边界允许类型擦除，字段/元素热循环仍静态绑定；历史 reader 可独立取得。 | [ObjectBinding](../src/DurableGraph/Runtime/Binding/ObjectBinding.cs)、[ArrayObjectBinding](../src/DurableGraph/Runtime/Containers/ArrayObjectBinding.cs)、[ListObjectBinding](../src/DurableGraph/Runtime/Containers/ListObjectBinding.cs) |
| <a id="model-binding"></a>**当前模型绑定（StateModelBinding）** | ObjectBinding 的用户 durable class 适配器，负责一个闭合族的当前实现及其保留 reader。Repository 打开时冻结模型、定义及 Upgrade provider；SchemaStore 仍是 Repository 内单调积累的实时权威，缓存 UpgradePlan 以 exact Schema requirement set 在 callback 前复核后来登记的定义。 | [StateModelBinding](../src/DurableGraph/Runtime/Binding/StateModelBinding.cs)、[StateModelRegistry](../src/DurableGraph.Persistence/StateModelRegistry.cs)、[Schema requirement set](design-branches/0042-upgrade-schema-requirement-set.md) |
| <a id="base-state-projection"></a>**基类状态投影（StateBaseProjection）** | current model 显式授予、按完整 Schema/DTO 冷绑定的强类型 Capture/Hydrate 委托。Family class 处理本声明字段并通过 immediate base 投影访问祖先状态，消费方无需知道基类 private 字段的 CLR 类型。只作用于同一领域实例，不独立分配、登记或升级基类；普通整对象 binding 仍要求 exact CLR 类型，完整 leaf DTO/body 仍展开。 | [StateBaseProjection](../src/DurableGraph/Runtime/Binding/StateBaseProjection.cs)、[投影生成](../src/DurableGraph.Generator/DurableSchemaGenerator.GenericProjection.cs)、[DB-061](design-branches/0061-cross-assembly-inheritance-slice.md) |

## 对象图、身份与状态视图

| 项目内术语／代码符号 | 释义与示意 | 关键代码 |
|---|---|---|
| <a id="domain-graph"></a>**领域对象图与单根（domain graph / root）** | 用户领域实例及其持久引用关系，例如 `World → A ↔ B`。每个持久 State/Event 选一个非空 durable 根；模型按实际 CLR 类型选择，工作副本可跨类型替根，公共读取返回实际根。World 是根的历史角色名，不要求类型名为 World。 | [BranchCheckout](../src/DurableGraph.Persistence/BranchCheckout.cs)、[IDurableObject](../src/DurableGraph/IDurableObject.cs) |
| **领域引用对象资格**／`IDurableObject` | 普通 class 与 record class 直接或经自建领域祖先实现的无成员接口，替代已移除的 DurableBase。它不提供 codec，不进入 Schema，不开放接口持久槽或 boxed struct；实际对象仍需已登记的 exact binding。各领域祖先均须参与 Schema/history。 | [IDurableObject](../src/DurableGraph/IDurableObject.cs)、[DB-068](design-branches/0068-record-class-model-slice.md) |
| <a id="object-identity"></a>**CLR 引用身份与对象 ID（reference identity / ObjectId）** | 引用身份区分 CLR 实例；`ObjectId(uint Value)` 是无隐式数值转换的语义包装，在指定 Revision 视图内解释，引用槽中的 0 表示 null，实际对象行 ID 必须非零。Capture 用引用相等关联实例与 ID。值相同的不同非空 string 不合并；空串统一 `string.Empty` 是明确例外。ID 不是 CLR 地址或全仓库永久唯一号。 | [ObjectId](../src/DurableGraph/ObjectId.cs)、[CaptureContext](../src/DurableGraph/Runtime/Capture/CaptureContext.cs)、[CaptureSession](../src/DurableGraph/Runtime/Capture/CaptureSession.cs) |
| <a id="object-state-record"></a>**对象状态记录**／`ObjectStateRecord` | 不可变单对象内存记录：`ID + exact ObjectLayout + frozen 内容`；内容为用户 DTO、string、FrozenArrayState、FrozenListState 或 FrozenDictionaryState。同一代码类型承载捕获、解码与归一化结果；仅有该行不证明它是已接受候选。 | [ObjectStateRecord](../src/DurableGraph/Runtime/Capture/ObjectStateRecord.cs) |
| <a id="object-layout"></a>**精确对象布局（ObjectLayout / ArrayLayout / ListLayout / DictionaryLayout）** | ObjectLayout 是 String / 用户 reference exact DurableSchema / exact ArrayLayout / exact ListLayout / exact DictionaryLayout 的受控判别值，可由合法对象表示 ID 解析得到；inline 目录节点不对应 ObjectLayout。ArrayLayout 保存 codec 版本、数组构造和完整元素槽；ListLayout 保存 codec 版本与完整元素槽，当前仅接受 List codec 2；DictionaryLayout 保存两个 exact 槽与 codec 1。元素 inline Schema 精确绑定，引用元素只保存目标名义约束；数组 shape、容器 Count、Dictionary comparer 和匹配算法不属于类型布局。 | [ObjectLayout / ArrayLayout](../src/DurableGraph/Schema/ObjectLayout.cs)、[ListLayout](../src/DurableGraph/Schema/ListLayout.cs)、[统一目录格式](../src/DurableGraph.Persistence/SchemaCatalogWireCodec.cs) |
| <a id="frozen-array-state"></a>**冻结数组状态（FrozenArrayState / ArrayShape）** | 一个数组对象的不可变 shape 和 owned row-major 元素状态 buffer；元素为标量、ObjectId 或生成 struct DTO。jagged 外层保存内层 ID，不内联子数组 body。领域数组后续修改不得污染该状态。 | [FrozenArrayState / ArrayShape](../src/DurableGraph/Runtime/Containers/FrozenArrayState.cs)、[数组投影](../src/DurableGraph/Runtime/Containers/ArrayObjectBinding.cs) |
| <a id="list-content-object"></a>**列表内容对象（List content object）** | exact BCL `List<T>` 的独立引用对象，只保存 Count、元素顺序与逻辑内容；Capacity、内部数组和修改计数不持久化。元素使用受支持槽闭包，可递归组合 List、数组和用户泛型；同实例增删保留 ObjectId，同内容的新实例仍是新对象。不包括 List 子类或集合接口字段。 | [ListObjectBinding](../src/DurableGraph/Runtime/Containers/ListObjectBinding.cs)、[列表绑定快照](../src/DurableGraph.Persistence/StateModelSnapshot.Lists.cs) |
| <a id="frozen-list-state"></a>**冻结列表状态（FrozenListState）** | 一个列表对象的 owned 有序元素状态 buffer；Count 为该 buffer 的长度，元素为标量、ObjectId 或生成 struct DTO。后续领域修改不能污染它；嵌套列表保存子列表 ID。没有 ArrayShape，不保存领域 List 的 Capacity。 | [FrozenListState](../src/DurableGraph/Runtime/Containers/FrozenListState.cs)、[ListObjectBinding](../src/DurableGraph/Runtime/Containers/ListObjectBinding.cs) |
| <a id="frozen-dictionary-state"></a>**冻结映射状态（FrozenDictionaryState）** | 实验性 BCL Dictionary 的 owned key/value DTO 条目及 ComparerKind，语义无序。Count 为条目数；两侧引用均为 ObjectId。内部排列、容量、hash/bucket 不构成持久内容；Delta 按 canonical key body 寻址 Remove/Add/PatchValue。完整 source/current DTO 均验证 canonical key 唯一和引用；模式 4/5 的当前业务查找碰撞只在可达图恢复的 TryAdd 拒绝。 | [FrozenDictionaryState](../src/DurableGraph/Runtime/Containers/FrozenDictionaryState.cs)、[DictionaryObjectBinding](../src/DurableGraph/Runtime/Containers/DictionaryObjectBinding.cs)、[DictionaryStateReader](../src/DurableGraph/Runtime/Containers/DictionaryStateReader.cs) |
| <a id="candidate-view"></a>**捕获候选图／候选 DTO 视图（candidate view）**／`CapturedGraph` | 本次 Seal 冻结的根 ID 列表及完整可达对象状态集合；后续领域修改不影响它。durable 行使用捕获模型的当前 DTO。Capture 内核可登记多根，每份产品 State/Event 只选一个根。 | [CapturedGraph](../src/DurableGraph/Runtime/Capture/CapturedGraph.cs)、[CaptureContext](../src/DurableGraph/Runtime/Capture/CaptureContext.cs) |
| <a id="capture-session"></a>**捕获会话与候选协议**／`CaptureSession`、Capture–Seal–Accept–Discard | `BeginCapture → AddRoot → Seal → Accept 或 Discard`。Seal 遍历并复制状态；Accept 安装候选及 live 实例-ID 绑定；Discard 放弃候选，已分配数字仍消耗。`CaptureSession.Current` 是已 Accept 的内存图，接受不等于保存或发布。 | [CaptureSession](../src/DurableGraph/Runtime/Capture/CaptureSession.cs)、[CaptureContext](../src/DurableGraph/Runtime/Capture/CaptureContext.cs) |
| <a id="stored-view"></a>**落盘版本 DTO 视图（stored DTO view）**／`DecodedRevision` | 选定 Revision 的完整 live 冻结状态目录，逐对象保持落盘 exact ObjectLayout，如同目录包含 `A/V1`、`B/V3` 与旧版 Point 元素数组。已解码并验证引用，尚未 Upgrade；无领域实例或可编辑基线。内部保留该视图实际 object heads，同一 cached DTO 在不同视图仍分别验证。 | [DecodedRevision](../src/DurableGraph.Persistence/DecodedRevision.cs)、[RevisionDecoder](../src/DurableGraph.Persistence/RevisionDecoder.cs) |
| <a id="current-view"></a>**当前模型 DTO 视图与基线材料（current DTO view）**／`NormalizedRevision` | 从存储加载时包含全部 source 行归一化后的冻结目录，保留 ID、source ObjectLayout、`RequiresRewrite` 及存储来源/head/H；不裁剪归一化后不可达的 source 行。该类型也承载 FromCandidate 的私有保存候选，WithAddress 再补地址，因此类型本身不证明已经发布。current 指模型/表示版本，不是用户刚修改的值或 `CaptureSession.Current`。 | [NormalizedRevision](../src/DurableGraph.Persistence/NormalizedRevision.cs)、[ObjectBinding](../src/DurableGraph/Runtime/Binding/ObjectBinding.cs) |
| <a id="membership-reachability"></a>**源成员集合与根可达闭包（source membership / reachable closure）** | source 是所读 Revision 的全部 live IDs；闭包是 current DTO 图从所选根可达的 IDs。Upgrade 删边后，孤立 A/B 仍须解码、升级、验证，但不再 Allocate；后继候选不含它们时才产生 Remove。使用 live 时说明是哪一个集合。 | [NormalizedRevision](../src/DurableGraph.Persistence/NormalizedRevision.cs)、[LoadedWorld](../src/DurableGraph.Persistence/LoadedWorld.cs) |
| <a id="loaded-baseline"></a>**固定 Parent 比较基线／内部 LoadedWorld** | 领域根配有固定 Parent、完整 source membership、current DTO 和实例身份关联。LoadedWorld 仅为内部低层准备/测试桥接，Prepare 后由宿主 Append/重新 Load；公开工作副本连续保存由 WorldWorkspace 安装原候选推进。 | [LoadedWorld](../src/DurableGraph.Persistence/LoadedWorld.cs)、[WorldWorkspace](../src/DurableGraph.Persistence/WorldWorkspace.cs) |
| <a id="upgrade-normalize"></a>**DTO 升级与归一化（Upgrade / Normalize）** | 用户 class Normalize 按相邻版本执行完整 leaf DTO 转换，不重复升级祖先。数组与 List 各自作为独立内建 owner，以分别选定的 source→current 元素规则转换 owned buffer；不自动搜索相邻值规则路径，空内容也验证能力。均不查询其他对象或分配新 ID；数组保持 shape，List 保持 Count/位置，升级行仍 live 时下次强制 Base。 | [用户 model/Upgrade](../src/DurableGraph.Generator/DurableSchemaGenerator.StateModel.cs)、[数组 owner Upgrade](../src/DurableGraph/Runtime/Binding/StateBindingContext.ArrayUpgrade.cs)、[List owner Upgrade](../src/DurableGraph/Runtime/Binding/StateBindingContext.ListUpgrade.cs)、[NormalizedRevision](../src/DurableGraph.Persistence/NormalizedRevision.cs) |
| <a id="owner-upgrade-provider"></a>**owner 升级提供者（StateUpgradeProvider）** | 明确声明某定义相邻边的通用方法，或某闭合族的专用方法；闭合 provider 优先，选中后失败不回退。框架在该对象首次业务 callback 前绑定完整相邻链及其声明的值依赖；中间 exact 布局必须有依据，不能用 latest 补猜。 | [DurableUpgrade / StateUpgradeProvider](../src/DurableGraph/Runtime/Binding/StateUpgradeProvider.cs)、[Upgrade 绑定](../src/DurableGraph/Runtime/Binding/StateBindingContext.Upgrade.cs) |
| <a id="upgrade-context"></a>**升级上下文（UpgradeContext）** | 一次同步转换的 ObjectId、Source/TargetObjectLayout 与当前 provider 已绑定工具表；array owner 另带 ArrayShape，List/Dictionary owner 分别使用独立 nullable ListCount/DictionaryCount（其他 owner 为 null），Schema 访问器只适用于 durable owner。子值 Context 继承 owner 事实并拥有独立依赖作用域。`GetValueUpgrade<A,B>(key)` 只查本表并核对状态类型，不解析新规则或回查父表。不含图访问/ID 分配，不跨调用保留。 | [UpgradeContext / ValueUpgrade](../src/DurableGraph/UpgradeContext.cs)、[值工具绑定](../src/DurableGraph/Runtime/Binding/StateBindingContext.ValueUpgrade.cs) |
| <a id="value-upgrade-capability"></a>**值升级能力（value upgrade capability）**／`ValueUpgrade<TPrior,TNext>` | 对完整两端槽语义和所选业务规则预绑定的强类型委托，由 owner 显式调用；组合值 provider 可继续调用自己声明的子能力。存在于执行 binding，不进入 DTO，不是 struct Normalize 或自动业务迁移。 | [ValueUpgrade](../src/DurableGraph/UpgradeContext.cs)、[StateValueUpgradeProvider](../src/DurableGraph/Runtime/Binding/StateValueUpgradeProvider.cs)、[值 provider 生成](../src/DurableGraph.Generator/DurableSchemaGenerator.ValueUpgrades.cs) |
| <a id="value-upgrade-rule-set"></a>**值升级规则集（StateValueUpgradeRuleSet）**／`ValueUpgradeRuleSetAttribute` | 应用显式选择的一组值 provider，以本地 CLR marker Type 标识并冻结到操作目录，不成为持久 ID。多个匹配拒绝；仅作者开启 AllowKeepExact、没有显式候选且完整槽相等时可透传。已选候选错误不进入透传。 | [规则集与 provider](../src/DurableGraph/Runtime/Binding/StateValueUpgradeProvider.cs)、[候选绑定](../src/DurableGraph/Runtime/Binding/StateBindingContext.ValueUpgrade.cs) |
| <a id="upgrade-dependency"></a>**升级依赖与局部工具 key（StateUpgradeDependency）** | provider 方法显式声明 `key + 规则集 + source/target 槽位置`；槽由声明定义 ID 与 FieldId 定位，保留继承段。key 区分大小写且只在该 provider 的 Context 中解释；相同状态 CLR 类型或相同 key 不会合并父子作用域。SG 可生成局部字符串常量。 | [UpgradeDependency / StateUpgradeSlot](../src/DurableGraph/Runtime/Binding/StateValueUpgradeProvider.cs)、[常量与声明生成](../src/DurableGraph.Generator/DurableSchemaGenerator.ValueUpgrades.cs) |
| <a id="allocate-hydrate"></a>**领域实例分配与字段填充（Allocate / Hydrate）** | 先为全部可达的受支持引用对象建立实例-ID 表（分配 exact 实例或登记策略允许的复用实例），再填充字段/元素及连接引用；单图不同 ID 不共用实例（历史 string.Empty ID 的规范实例例外）；跨图复用须由明确规划授权。ReadPair 须两边的表均完成才开始 Hydrate。SG class Allocate 使用 RuntimeHelpers.GetUninitializedObject，不执行用户构造器或字段初始化；array 按冻结 shape 分配，List 先构造空列表，全部实例登记后逐元素 Hydrate 并按序 Add。Transient 由用户交付后重建。 | [WorldWorkspace](../src/DurableGraph.Persistence/WorldWorkspace.cs)、[SG Allocate/Hydrate](../src/DurableGraph.Generator/DurableSchemaGenerator.StateModel.cs)、[数组适配器](../src/DurableGraph/Runtime/Containers/ArrayObjectBinding.cs)、[List 适配器](../src/DurableGraph/Runtime/Containers/ListObjectBinding.cs) |
| <a id="reference-operations"></a>**引用槽遍历与引用解析表（VisitReferences / ObjectReadTable）** | 遍历报告 ID 与声明类型约束；class 使用 stored nominal ancestry，数组与 List 要求 exact nominal 类型，string 使用内建约束。校验、可达分析与双图共享闭包复用遍历；每视图 ObjectId→object 表在 Allocate 后提供 Hydrate 解析。单图不同 ID 不共用实例，历史 Empty ID 是例外；跨视图同版本另须满足共享闭包。字段/元素 body 继续静态绑定。 | [StateReferenceVisitor / Validator](../src/DurableGraph/Runtime/Binding/StateReferenceVisitor.cs)、[ObjectReadTable](../src/DurableGraph/Runtime/Capture/ObjectReadTable.cs) |

## 版本、存活目录与地址

| 项目内术语／代码符号 | 释义与示意 | 关键代码 |
|---|---|---|
| <a id="state-revision"></a>**状态修订（StateRevision）** | 一次修订的冻结描述：Parent、本帧对象记录及 head 目录的表示。Append 后由一个 Revision Frame 承载；尚未追加的 StateRevision 也可作为计划。仅本地记录不能代表完整存活状态。 | [StateRevision](../src/DurableGraph.Storage/StateRevision.cs) |
| <a id="frame-address"></a>**帧地址（FrameAddress）** | `FileNumber + FrameTicket` 定位一个 RBF Frame。对象 head 指含该对象记录的 Revision Frame，取对象还须 ObjectId。运行时使用绝对文件编号，向后文件距离仅为 wire 表达。 | [FrameAddress](../src/DurableGraph.Storage/FrameAddress.cs) |
| <a id="object-version-head"></a>**对象版本与对象 head（ObjectVersion / object head）** | 对象版本是一次对象 Base/Delta 记录；对象 head 是某 Revision 对该 ObjectId 指定的当前记录地址。多个 Revision 可以沿用同一个 head；`ObjectVersionRecord` 是内存中的记录描述。 | [ObjectVersionRecord](../src/DurableGraph.Storage/ObjectVersionRecord.cs)、[StateRevisionStore](../src/DurableGraph.Storage/StateRevisionStore.cs) |
| <a id="parent-prior"></a>**修订 Parent 与对象 prior** | `ParentRevisionAddress` 指前驱修订；对象 Delta 的 `PriorAddress` 指该 Parent 对此 ID 指定的 head。例如 R3 的 Parent=R2，X 在 R2 未写，X 的 prior 可以是 R1。内存 `Previous` 另指比较输入，本身不认证磁盘 Parent。 | [StateRevisionStore](../src/DurableGraph.Storage/StateRevisionStore.cs)、[PreparedCapturedGraph.Previous](../src/DurableGraph/Runtime/Capture/PreparedCapturedGraph.cs) |
| <a id="object-base-delta"></a>**对象内容 Base／Delta** | Base 是单个对象完整内容并截断重建链；Delta 保存相对 exact prior 的变化。class/array/List 按 Base → Delta… 重建，同链沿用 Base 的 exact ObjectLayout；跨布局升级续写从新 Base 开始。数组 Delta 是 row-major 稀疏元素索引及子 Delta；List 使用区间差分；string 只保存 Base。 | [ObjectVersionRecord](../src/DurableGraph.Storage/ObjectVersionRecord.cs)、[TypedObjectVersionReader](../src/DurableGraph.Persistence/TypedObjectVersionReader.cs)、[数组 body](../src/DurableGraph/Runtime/Containers/ArrayStateReader.cs)、[List body](../src/DurableGraph/Runtime/Containers/ListStateReader.cs) |
| <a id="list-position-delta"></a><a id="list-range-delta"></a>**列表区间差分（List range Delta）** | List codec 2 的内容差分：新 Count 后顺序输出 Copy、New 或 CopyAndPatch，复用 immutable prior 的 source 区间、写新元素 Base 或应用局部稀疏子 Delta。source 可重叠、重复、倒序；不修改 prior，不从正在构造的 target 复制。NoChange 由两份完整序列决定，不依赖 matcher 搜索结果；旧 codec 1 不读取。位置匹配仍作为同语法 writer 基线，旧字节解释保留在 DB-047 的历史记录中。 | [ListStateBody / reader](../src/DurableGraph/Runtime/Containers/ListStateReader.cs)、[区间合同](design-branches/0049-list-range-delta-and-matcher-trial-slice.md)、[历史位置语法](design-branches/0047-list-content-object-slice.md#4-base--delta-body) |
| <a id="list-delta-matcher"></a>**列表区间匹配器与 writer 选择（ListDeltaMatcher / ListDeltaAlgorithm）** | Position、LocalResync、BoundedMyers 仅用 StateEquals 找复用区间及待配对 gap，共用 codec 2。默认 Adaptive 是 writer 层协调：先完整编码 Local，停滞时尝试独立有界 Myers，实际输出达到基准长度即停止，只接受严格更短的完整 body；纯 matcher 拒绝 Adaptive。`UseListDeltaAlgorithm` 随 Registry→Snapshot 冻结，不影响已取得的模型配置快照。选择不写入 payload 或表示身份；保证相同 frozen pair 的 raw body 不大于 Local，不承诺全局最优或耗时/分配不增。 | [ListDeltaMatcher](../src/DurableGraph/Runtime/Containers/ListDeltaMatcher.cs)、[字节竞争](../src/DurableGraph/Runtime/Containers/ListDeltaCompetition.cs)、[算法枚举](../src/DurableGraph/ListDeltaAlgorithm.cs)、[配置入口](../src/DurableGraph.Persistence/StateModelRegistry.cs)、[List binding 快照](../src/DurableGraph.Persistence/StateModelSnapshot.Lists.cs) |
| <a id="head-map"></a>**存活对象 head 目录（ObjectHeadMap）与目录 Base／Delta** | 将该 Revision 全部 live IDs 映射到 head。目录 Base 完整声明成员与 head，目录 Delta 在 Parent 上替换本地 head 并 Remove。与对象内容 Base/Delta 独立，例如目录 Delta 可以写入对象 Base。 | [StateRevision](../src/DurableGraph.Storage/StateRevision.cs)、[LiveObjectHeadMapMaterializer](../src/DurableGraph.Storage/LiveObjectHeadMapMaterializer.cs) |
| <a id="frozen-sorted-dictionary"></a>**紧凑只读地址字典**／Storage 内部 `FrozenSortedDictionary` | owned keys/values 双数组，按默认 key 顺序二分查询、升序枚举；用于 external heads 和物化后的完整 head map。操作内仍用排序树构建，冻结结果不保留树。区别于 BCL FrozenDictionary，也区别于领域 Dictionary 的 FrozenDictionaryState。 | [双数组实现](../src/DurableGraph.Storage/FrozenSortedDictionary.cs) |
| <a id="revision-read-cache"></a>**修订读缓存（revision read cache）** | 每个 StateRevisionStore 按完整 FrameAddress 复用 owned raw Revision 和完整 head map，共用有界 LRU。命中不证明 local record、typed body 或引用图有效；依赖 append-only 及 Store 寿命，Dispose 释放驻留，离线救援后必须新开。与操作内 DTO/string 解码会话分开。 | [StateRevisionStore](../src/DurableGraph.Storage/StateRevisionStore.cs)、[DB-067](design-branches/0067-owned-revision-read-cache-design.md) |
| <a id="local-external-remove"></a>**本地记录、外部 head 与成员移除（local / external / Remove）** | local 位于当前 Frame；external 是目录 Base 引用的较早 head；Remove 从目录 Delta 的存活集合删除成员。移除不擦除旧帧、不改变旧 Revision，也不等于 ID 数字回收。 | [StateRevision](../src/DurableGraph.Storage/StateRevision.cs) |

## 内容准备、计量与保存动作

编码范围示意：

```text
SG / 内建对象 codec      → 原始对象 body
BaseObjectBodyCodec     → [v4 + RepresentationId | 原始 Base body]（StateStore 内部品牌）
Storage ObjectVersion   → [kind | 可选 prior | body 长度 | body]
Revision Frame          → 本帧对象记录 + head 目录相关信息 + 共享帧结构
```

Base 记录的 `body` 已含类型头；Delta 记录的 `body` 没有该头。最后一行仅示意包含关系，不规定 wire 排列顺序。

| 项目内术语／代码符号 | 释义与示意 | 关键代码 |
|---|---|---|
| <a id="state-preparation-binding"></a>**状态内容操作绑定**／`CapturedStatePreparation<TState>` | 将 exact Schema/DTO 与 Base/Delta 内容准备及可选状态相等操作配对的执行能力。`StateModelBinding._preparation` 指此绑定；它不是已准备的 body，也不是单图恢复准备材料。 | [CapturedStatePreparation](../src/DurableGraph/Runtime/Capture/CapturedStatePreparation.cs)、[模型绑定](../src/DurableGraph/Runtime/Binding/StateModelBinding.cs) |
| <a id="prepared-content"></a>**已准备 raw body（PreparedBaseBody / PreparedDeltaBody）** | 拥有独立 bytes、决策后可复用的公开容器；Delta 还保存 `HasChanges`，无变化仍可含非空零位图。`Body` 不含 StateStore Base 类型头或 Storage envelope，也不认证 Schema/prior。 | [PreparedBaseBody](../src/DurableGraph.Serialization/PreparedBaseBody.cs)、[PreparedDeltaBody](../src/DurableGraph.Serialization/PreparedDeltaBody.cs) |
| <a id="body-payload"></a>**原始对象 body、带类型头的 Base body、ObjectVersion payload** | body 是 DTO/string/数组/List 的内容编码；StateStore 内部 `EncodedBaseObjectBody` 品牌表示 `[Base 类型头｜raw Base body]`。仅支持 v4 头，包含格式版本与 RepresentationId；ObjectVersion payload 再含 kind、可选 prior、长度及 body。 | [BaseObjectBodyCodec](../src/DurableGraph.Persistence/BaseObjectBodyCodec.cs)、[ObjectVersionPayloadSize](../src/DurableGraph.Storage/ObjectVersionPayloadSize.cs) |
| <a id="bdh"></a>**对象写入成本 B／D 与重建成本 H** | B 是本轮 Base payload 精确大小，包括表示 ID 的实际宽度；D 是本轮 Delta payload 上界，未定 prior 文件距离造成 0–4 bytes 超额；H 是已有重建链实际 payload 字节之和。均排除 ObjectId、目录、共享 Frame、Schema/表示登记日志；H 不是总冷读 I/O。 | [ObjectVersionPayloadSize](../src/DurableGraph.Storage/ObjectVersionPayloadSize.cs)、[ObjectVersionChain](../src/DurableGraph.Storage/ObjectVersionChain.cs) |
| <a id="base-budget"></a>**读放大动机与可选 Base 预算** | 未变对象考察 H/B、变化对象考察 (H+D)/B，严格超过整数倍率阈值才有可选 Base 动机。预算取全部 post-live 对象 B 总和的整数百分比；强制 Base 不占可选预算，首个候选允许超预算，因此不是总写入硬上限。 | [ReadAmplificationBaseBudgetPolicy](../src/DurableGraph.Persistence/ReadAmplificationBaseBudgetPolicy.cs) |
| <a id="content-preparation-planning"></a>**内容准备与修订规划（content preparation / revision planning）** | `CaptureSession.Prepare` 消费已经 Seal 的候选，产生可复用内容及比较结果，不进行 Capture 或生成 StateRevision。修订规划另核对 Parent、应用表示策略并构建 StateRevision；规划结果不是已追加的 Frame。 | [CaptureSession.Prepare](../src/DurableGraph/Runtime/Capture/CaptureSession.cs)、[ObjectRevisionPlanner](../src/DurableGraph.Persistence/ObjectRevisionPlanner.cs) |
| <a id="prepare-append-commit"></a>**图保存准备、追加与提交发布（Prepare / Append / Commit）** | 工作区编排 Capture/Seal、内容准备和修订规划，可持久注册 Schema。AppendDurably 只确认 State 文件屏障；Repository 继续追加 Journal 帧并更新 branch ref。State 发布后安装原候选，Event 发布后释放候选、不推进 State 基线。 | [WorldWorkspace](../src/DurableGraph.Persistence/WorldWorkspace.cs)、[StateRevisionStore](../src/DurableGraph.Storage/StateRevisionStore.cs)、[Repository](../src/DurableGraph.Persistence/Repository.cs) |

规划输入是**完整 post-live 集合**，策略 `Writes` 只列实际选写对象：未变对象可能不写，也可能因读放大而写 Base。
推进 State 时，逻辑移除来自 Parent 完整成员集合减去候选成员集合；目录 Delta 显式编码 Remove，目录 Base 按缺席表达移除。
普通 State 仅在目录 Base 的保守字节上界严格更小时选 Base；独立快照始终使用 map Base，仅列候选的 local records 与 exact Parent external heads；[ObjectRevisionPlanner](../src/DurableGraph.Persistence/ObjectRevisionPlanner.cs)
负责生成这份存储计划，Storage 消费明确结果，不自行从领域图推导可达性。

<a id="repository-与工作会话"></a>

## Repository 与分支工作副本

| 项目内术语／代码符号 | 释义与示意 | 关键代码 |
|---|---|---|
| <a id="graph-repository"></a>**仓库**／`Repository` | 拥有匹配的 Schema/State/Journal 资源和命名分支；可写打开独占 repository.lock，每分支最多一个活动工作副本，操作串行。Journal ref 是唯一发布前沿，Schema 仍为单调注册表、尚非联合版本视图；只读打开不写入或修尾。 | [Repository](../src/DurableGraph.Persistence/Repository.cs)、[HistoryJournal](../src/DurableGraph.Persistence/HistoryJournal.cs) |
| **图资源**／`GraphResources` | 匹配 Schema/State 文件的内部所有者；严格只读/可写打开、释放和故障状态，不选择业务 head、不运行模型回调。 | [GraphResources](../src/DurableGraph.Persistence/GraphResources.cs) |
| **独立图读取**／`GraphReader` | 显式 Revision + RootId 的 exact 解码、升级与两阶段恢复；可请求实际 durable 根的基类。独立读取产生可控导入的 MaterializedGraph；ReadPair 的只读共享路径仅返回领域根，不导入可编辑基线。 | [GraphReader](../src/DurableGraph.Persistence/GraphReader.cs) |
| <a id="revision-read-session"></a>**操作内解码会话**／`RevisionReadSession` | 封闭一次操作的 State/Schema 资源与模型配置快照，按 `(ObjectId, 实际 head)` 缓存 owned stored DTO/string 及 exact reader。缓存只省重复 body 解码，不缓存每视图验证、Normalize/Upgrade 结果或工作区基线；不是持久分支或公共分支工作副本，也不是 Frame cache。 | [RevisionReadSession](../src/DurableGraph.Persistence/RevisionReadSession.cs)、[RevisionDecoder](../src/DurableGraph.Persistence/RevisionDecoder.cs) |
| <a id="shared-reference-closure"></a>**安全引用闭包共享（shared reference closure）** | 只读双图在同 ID/head、current binding/完整布局一致、未触发布局重写且完整 current 状态具有相等 proof 的候选中，沿反向引用排除所有依赖非候选的对象；string 直接复用同 key 的解码实例。剩余闭包共享 Allocate/Hydrate，可整体保留稳定环；同 owner head、不同 child head 会使 owner 分裂。缺 proof 保守不共享，业务 record 相等不替代该证明；不是跨图身份保证。 | [GraphReader](../src/DurableGraph.Persistence/GraphReader.cs)、[DB-066](design-branches/0066-readpair-comparison-and-transient-contract-slice.md) |
| **State 工作区与独立快照**／`WorldWorkspace` | 拥有可选 State 的冻结 DTO、根、实例-ID 绑定和串行 cursor。推进候选按实际模型捕获，可跨类型替根，发布后安装同一候选；独立快照借用最近 State 基线，无 State 则准备完整 Base，完成后释放候选，不接受 Event 身份或安装为 State。 | [WorldWorkspace](../src/DurableGraph.Persistence/WorldWorkspace.cs)、[PreparedWorldSave](../src/DurableGraph.Persistence/PreparedWorldSave.cs) |
| <a id="graph-session"></a>**分支工作副本的当前实现**／`BranchCheckout` | 非泛型，拥有分支精确 Head 与可选 State 工作区。首建先发布非空 E 或 S 再交付；State 提交安装原实例并允许跨类型替根，Event 不推进 State。Checkout 只恢复最近 State，无 PendingEvent；Dispose 不自动保存。 | [BranchCheckout](../src/DurableGraph.Persistence/BranchCheckout.cs)、[WorldWorkspace](../src/DurableGraph.Persistence/WorldWorkspace.cs) |
| <a id="publication-head"></a>**发布 head／Journal ref** | 命名分支 ref 指向权威 Journal 帧，该帧引用 RevisionAddress + RootId。Schema/State 先确认，再追加 Journal 帧，最后发布 ref；严格重开不从坏尾猜测旧 head。首个 E 或 S 完成后才绑定分支名字，不交付空 head 分支。 | [HistoryJournal](../src/DurableGraph.Persistence/HistoryJournal.cs)、[Repository](../src/DurableGraph.Persistence/Repository.cs) |
| <a id="graph-frame"></a>**图定位句柄（旧称 graph frame handle）** | 旧公开 `GraphFrame` 已由 CheckpointAddress 取代；仍含角色、RootId 与诊断用 RevisionAddress，但不能通过裸地址伪造来源；不是原始 RBF 帧、DTO 或可跨重开的持久书签。 | [CheckpointAddress](../src/DurableGraph.Persistence/CheckpointAddress.cs)、[检查点地址](#checkpoint-address) |
| **EventHistory、EventFrame / StateFrame** | 独立事件快照图与状态图构成自由 E/S 逻辑历史；两种角色共用 EventJournal 的 EventFrame，用 kind 与 payload 引用图，不是新增两种底层 RBF 帧。每条记录的图 Parent 为其严格祖先中最近 State，无 State 时为 null。 | [GraphEnvelopeCodec](../src/DurableGraph.Persistence/GraphEnvelopeCodec.cs)、[DB-078-A](design-branches/0078-a-repository-free-history-implementation.md) |
| **分支与工作副本的操作边界** | Branch 是持久命名 ref，工作副本是内存编辑宿主。源工作副本存活时可 Fork 到新分支；Move 只要求目标分支无活动工作副本。ref-only 创建不物化领域图，也不取得编辑占用。 | [Repository](../src/DurableGraph.Persistence/Repository.cs) |
| **实验性双图读取**／`ReadPair` | 同一次调用按输入顺序完整还原两个只读快照；第二边失败不交付 pair，不撤销用户回调副作用。可复用 exact DTO/string 与安全引用闭包，但不承诺跨图实例复用或分离，ReferenceEquals 不能承担业务身份/版本判断或控制分支。只读约束也禁止可观察的 Transient 写入；每个视图的 owner/context/cache 放在图外，需原位初始化时使用独立读取。Checkout 采用独立可变图恢复路径。 | [Repository](../src/DurableGraph.Persistence/Repository.cs)、[GraphReader](../src/DurableGraph.Persistence/GraphReader.cs) |
| **对象状态相等证明**／`ProvesSameState` | 共享候选比较两份完整当前状态，不准备 Base/Delta。SG 经可选 StateEquality 委托接入既有 StateEquals，容器比较完整 shape/count/comparer 与有序槽；缺 proof 返回 false，真实错误传播。true 只证明持久状态，身份/head 与引用闭包仍由 GraphReader 判断；字典重排可保守返回 false，不改其无序映射语义。 | [比较能力](../src/DurableGraph/Runtime/Capture/CapturedStatePreparation.cs)、[DB-066](design-branches/0066-readpair-comparison-and-transient-contract-slice.md) |
| <a id="commit-outcome"></a>**提交结果**／`GraphCommitOutcome` | NotPublished、Unknown、Published 描述持久发布结果，不描述领域修改是否撤销；Unknown 或发布后安装失败须使工作副本停止续写，dispose/reopen 裁决。NotPublished 也需检查资源是否 faulted。 | [GraphCommitException](../src/DurableGraph.Persistence/GraphCommitException.cs) |
| <a id="migration-shell"></a>**迁移壳** | 已退出业务模型、仍承载历史 reader/Upgrade 的声明角色，不是新 attribute 或独立框架类型；不可达壳仍完成 source 验证，支持旧 Revision 的程序仍须保留对应能力。 | [真实包见证](../experiments/PackageConsumerProbe/HistoryCapabilityConsumer/Legacy.V2.cs)、[能力合同](design-branches/0036-working-session-and-history-capabilities.md#4-并行小线明确历史恢复能力合同) |

## 多义词、历史叫法与代码命名差异

下列映射供检索及后续一致化定位，不是对旧文本执行全局替换的规则。

| 遇到的用语 | 当前应区分或采用的表达 | 依据 |
|---|---|---|
| `GraphRepository` / `GraphSession` / `PublicationLog` | DB-063 已删除的早期单 head 外观/发布器；当前使用 Repository、BranchCheckout 与 Journal ref，不提供旧仓库兼容。LoadedWorld 只保留为内部机制桥接。 | [仓库](#graph-repository)、[工作副本](#graph-session) |
| `EventHistoryRepository` / `EventHistorySession<TState>` / `Resume<TState>` / `GraphFrame` | DB-078-A 公共迁移前的名称，分别对应 Repository / 非泛型 BranchCheckout / Checkout / CheckpointAddress；同时取消交替、固定根类型与 PendingEvent 合同，不能仅按改名解释旧行为。 | [DB-078-A 实施记录](design-branches/0078-a-repository-free-history-implementation.md) |
| `VersionedSchema`、Schema descriptor | 描述版本化类型定义时落到[完整精确 Schema](#exact-schema)；索引键另称 SchemaKey。这是概念到实现的对应，不声称曾存在一次正式类重命名。 | [DurableSchema](../src/DurableGraph/Schema/DurableSchema.cs) |
| `Snapshot` / 快照 | 合法概念须加限定：模型配置快照、冻结 DTO 快照、只读历史图；不等于分支工作副本或 CLR 深不可变实例。`.dgsnapshot` 是已拒绝的旧构建历史格式；`__DurableSnapshotVn` / InMemoryStateStore snapshot 路径是已删除的历史符号，不能借此否定当前 `StateModelSnapshot`。 | [模型执行环境](#model-environment)、[Schema 历史材料](#schema-history)、[版本化 DTO](#state-dto) |
| `SchemaOnly`、`GenerateBinaryBody` | DB-035 Wave 1 已删除的增量开发开关。裸 `[DurableType]` 现在是唯一生成模式，产生 exact Schema/history、版本化 DTO/body；class 还生成 State model，struct 使用静态值桥接；不支持的形状直接诊断。 | [唯一生成入口](../src/DurableGraph.Generator/DurableSchemaGenerator.cs) |
| `Current`、当前状态、DTO 列表 | 分别写“当前模型版本 DTO”“工作副本已提交的 State 基线”“本次候选 DTO”或“当前领域值”；对象列表注明 stored/current/candidate。`PreparedCapturedObject.Current` 是本次候选行。 | [current 视图](#current-view)、[捕获会话](#capture-session)、[PreparedCapturedGraph](../src/DurableGraph/Runtime/Capture/PreparedCapturedGraph.cs) |
| Base Revision、`StateRevision.CreateObjectHeadMapBase/CreateObjectHeadMapDelta` | 这两个工厂选择[目录 Base/Delta](#head-map)，不保证本地对象都是相同表示。`BaseSchema` 另属继承关系。 | [StateRevision](../src/DurableGraph.Storage/StateRevision.cs) |
| `CapturedObject`、`PreparedBase.Payload`、早期 `EstimatedBaseWriteBytes` | 前两项已在 DB-035 Wave 3 分别迁移为阶段中性的 `ObjectStateRecord` 与 raw `PreparedBaseBody.Body`；带类型头的 Base body 只以 StateStore 内部品牌流转。策略输入使用 `BasePayloadBytes`、`DeltaPayloadBytesUpperBound`、`ReconstructionPayloadBytes`，明确 B 是精确值、D 是上界、H 是已有重建链 payload。 | [对象状态记录](#object-state-record)、[已准备内容](#prepared-content)、[ObjectSaveEstimate](../src/DurableGraph.Persistence/ObjectSaveEstimate.cs) |
| head、Parent、prior、previous | head 指明“对象 head”或“分支发布 head”；Parent 指明 Revision Parent 或 Journal Parent。Journal Parent 为提交前精确 Head，图 Parent 为其自身或祖先中最近 State，无 State 时为空；对象 prior 是 Revision Parent 为此 ID 指定的对象 head。内存 Previous 不能自动当作已认证 Parent。`revisionAddress` 指定 Revision 地址，不证明该地址已发布。 | [Parent/prior](#parent-prior)、[提交发布](#prepare-append-commit) |
| intern、GC、Save、反序列化 | intern 在此为引用身份登记；GC 区分成员 Remove、CLR 回收、ID 数字回收与物理回收。Save/反序列化可作整体口语，讨论实现时展开为 Capture/Prepare/Append 或 Decode/Upgrade/Allocate/Hydrate。 | [对象身份](#object-identity)、[成员移除](#local-external-remove)、[实例恢复](#allocate-hydrate) |
| `DurableObject`、`DurableId`、`[Durable]`、`[DurableMember]`、`RebuildTransient` | 早期工作名列表中的提案，不能直接声明为当前 API 的旧名。当前代码使用 IDurableObject、ObjectId、DurableType/DurableField 等各自角色；MVP 的 Transient 重建由用户完成，无库内自动 hook。 | [早期工作名提案](archive/2026-09-06/DurableGraph-target-design-v0.md#23-工作名与术语建议)、[恢复目标与宿主边界](DurableGraph-target-design-v0.md#恢复transient-与宿主边界) |

## 仅在目标层使用的术语

| 目标术语 | 含义与使用边界 | 设计落点（尚非完整产品实现） |
|---|---|---|
| **CommitManifest** | 联合 State/Schema/Artifact exact 视图的长期表达；当前 Journal ref 不选择 Schema 的历史视图，不是完整联合 manifest。 | [单一发布权威](DurableGraph-target-design-v0.md#单一发布权威与明确故障结果) |
| **通用 TypeCodec** | 受支持类型组合的完整编码/解码能力；TypeExpr/wire 表达标量/string/用户泛型、SZ/rank 2–4 递归数组与内建 List；其他 BCL 内容 codec 仍须逐类型扩展。完整表示以目录 ID 寻址；持久开放模板与必要 exact 实参能否简化绑定另行研究。能表达类型与拥有内容 codec 仍是两项能力。 | [类型表达](#type-expression)、[表示 ID](#representation-id)、[后继问题](DurableGraph-research-roadmap.md#31-版本化表示类型头的统一寻址) |
| **ArtifactStore / DerivedStore** | 分别承担独立历史内容与可重建派生数据的目标职责；不凭现有 StateStore 类推已实现其存储合同。 | [四类 Store 的逻辑职责](DurableGraph-target-design-v0.md#四类-store-的逻辑职责) |
