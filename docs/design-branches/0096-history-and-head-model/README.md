# DB-096：版本、历史与命名入口的概念模型

> 状态：上游术语已落地；DG 接入已施工，domain-history 新模型仍待设计。校准：2026-10-17。
> 当前实现以源码和本片验收为准；术语导航见[项目术语表](../../DurableGraph-glossary.md)。
> 来源：workshop [反馈 4](../../../../durable-graph-workshop-a/docs/durable-graph-feedback.md)，以及 head-log / domain-history、原子名称映射和独立 metadata 需求。

先读本文的分层模型，再读[已收敛的上游命名](storage-naming-candidates.md)与[DG 接入切片](headstore-adaptation.md)。早期 NamedValue / HeadSnapshot 提案已被实际 `HeadSetTag` 取代，不再作为待选 API。

## 1. 本轮问题与边界

我们要能分别回答：一个发布入口曾经指向什么、应用当前承认哪条逻辑历史、用户通过什么名字访问它、以及这些事实由什么标识符关联。

本轮最小验收标准：用同一组术语解释保存、rewind 后续写、从继承祖先分叉、重开定位和名称绑定；区分现有事实与候选设计，并能指出两种 domain-history 路线必须共同回答的问题。只核对源码、测试内容和下游用法，不宣称运行实验或性能资格。

本轮不选择父链或 head-log 路线，不展开 app metadata 的编码和附件协议。有限源码适配只增加独立书签目录，并迁移上游 API；不代表完成增量名称索引或通用 domain-history 设计。

上游基线：Storage `731eef7cd3158572532fbeea7ba95a79213a7328`，见[固定依赖](../../../eng/StorageDependency.props)。DG 本轮适配和验证边界只记在[接入切片](headstore-adaptation.md)，旧包资格不沿用。

## 2. 先区分两种历史

**head-log（头发布历史）**回答“某个 head 接受过哪些发布值”；**domain-history（领域逻辑历史）**回答“从选定逻辑末端回看，哪些 checkpoint 构成这条历史”。

head-log 记录已接受的发布，不等于全部编辑动作、失败尝试或事务审计日志。domain-history 是应用通过 DG 表达的逻辑接续，不等于领域事件自动重放；State 与 Event 都可以构成节点。

例如，用户保存 S0、S1、S2，然后 rewind 到 S0，再保存 S3：

```text
逻辑结构：       S1 → S2
              ↗
          S0
              ↘
                S3       当前逻辑末端

当前 domain-history：S0 → S3
```

若同一底层 head 承担每次指针更新，其本地 head-log 可以是 `指向 S0、S1、S2、S0、S3`。若 rewind 创建新的底层 head-log，则这组操作分布在多个 log 与入口切换中。两者可以呈现同样的当前 domain-history。

`S1/S2` 不再位于当前逻辑路径，不代表物理记录已被删除，也不自动保证应用还能通过公开书签找回它们。**保留、可达、可发现、可恢复**需要分别描述。

## 3. 概念—术语—标识符

表中“讨论用语”是本文的建议词义；当前符号保持原拼写。概念分开不意味着必须新增同名类型或独立文件。

### 3.1 存储与发布

| 讨论用语 | 含义与不应混入的含义 | 当前标识符 / 状态 |
|---|---|---|
| **head（发布头）** | 有身份、可接受新发布值的持久入口；讨论其当前值时明确写 head value。不是某个固定 revision。 | `HeadStore` / `HeadId`，已实施；Head 身份与 current Entry 分开。 |
| **head value（发布头当前值）** | 当前已接受 Entry 的不透明 HeadEntryPayload。可能编码图地址、目录根或描述记录；不是天然的业务 State。 | `ReadHead` 返回的 `HeadEntry.Payload`；精确旧位置使用 `ReadEntry`。 |
| **HeadEntry（头修订）** | 某个 head 上一次已接受发布的准确位置与对应值。同值的两次发布仍可有不同 Entry。 | HS `HeadEntryId`、`HeadEntry`。 |
| **本地 head-log** | 同一 head 自己的已接受发布序列。其顺序有发布意义，不自动有业务祖先意义。 | 同一 Head 文件中经 HS 校验后交付的发布记录；不能直接把全部物理帧当作有效发布。 |
| **继承 head-history（含来源的头历史）** | 本地 log 逆序遍历后，沿创建来源接续源 log 的准确前缀。用于明确区别单个文件的本地序列。 | `ReadHeadHistory` 当前提供此遍历；不是新增另一种存储。 |
| **head fork origin（头创建来源）** | 新 head 从哪个实际 source HeadEntry 克隆初值。 | `HeadOriginInfo.SourceHeadId / SourceEntryTicket` 的来源声明；`ForkHead` 写入。 |
| **HeadEntryPayload（发布根载荷）** | HS 原样存取的字节，含义由上层定义。root 是发布入口角色，不意味着必须内联完整对象图。 | `PublishHead` / `PublishHeads` 等的 payload。 |
| **联合发布（multi-head publication）** | 多个既有 head 的新 payload 作为一次共同更新接受。 | `PublishHeads`；创建 head、命名绑定、读快照隔离不因这个名称而自动纳入。 |
| **HeadSetTag（具名 Head 位置表）** | 固定所选 Head 当前已接受 Entry 的非空表，不随后续发布移动；不代表业务历史书签或一次业务事务。 | `CreateTag(name, heads, out outcome)` / `ReadTag`；输入 `ReadOnlySpan<HeadId>`。 |
| **frame / frame address（物理帧 / 帧地址）** | FrameStore 中的物理存储记录及其位置；可承载图、对象数据或未来附件。 | `FrameAddress`；不是跨仓库全局身份，也不是已发布资格的证明。 |

### 3.2 领域历史与应用入口

| 讨论用语 | 含义与边界 | 当前标识符 / 状态 |
|---|---|---|
| **checkpoint（检查点）** | 已提交的一个领域历史位置，可承担 State 或 Event 角色。内容相同不自动代表同一历史位置。 | DG 的 `CheckpointAddress` 指代其位置；`Checkpoint` 类型是恢复出的读取视图，不是此概念的纯元数据记录。 |
| **graph revision（图内容版本）** | 一份准确的根、对象目录、Schema 环境与对象版本所表达的持久图。与 HS 的 HeadEntry 分层解释。 | `StoredGraphRevision` / `GraphEnvelope`；同一图可被多个命名入口引用。 |
| **Schema version（结构定义版本）** | 描述某个类型的持久布局演变；不是存档序号或发布次数。 | `SchemaId` 及定义版本；不替代 checkpoint / HeadEntry 身份。 |
| **domain-history（领域逻辑历史）** | 从选定逻辑末端可追溯的 checkpoint 接续关系及其路径。先研究当前单前驱场景，不预设 merge。 | 当前 `GraphEnvelope.Parent` + `ReadFrames` 实现一条选定历史的遍历。 |
| **domain tip（逻辑末端）** | 当前选择的最后一个 checkpoint；与承载该选择的底层 head 身份区分。 | 当前 branch head 的 `CheckpointAddress`。 |
| **逻辑历史入口** | 应用用于选择、推进或重定位一条逻辑历史的入口。它可在 rewind 后继续存在，而底层 head 是否改变由方案决定。 | 当前最接近 DG branch；未来是否引入独立身份尚未决定。 |
| **branch（DG 分支）** | 当前是持久的名称到 domain tip 的绑定。后续是否将名称、入口身份与存储 head 拆开，需要设计。 | `ListBranches`、`GetHead`、`CreateBranch`、`ForkShared`；不能等同 HS Head。 |
| **name binding（名称绑定）** | 字符串到目标身份或位置的关联；名称比较、唯一性、改绑规则属于目录语义。 | 当前 `branchName → FrameAddress`；未来映射到 `HeadId` 或逻辑入口身份均为候选。 |
| **checkout（工作副本）** | 可编辑的内存对象、保存基线及编辑占用，不是持久入口或历史节点。 | `BranchCheckout`；`CheckoutShared` / `CheckoutPrivate`。 |
| **tag / bookmark（标签 / 书签）** | 跨重开重新取得固定历史位置的命名手段。 | DG `CreateTag/ResolveTag`；独立书签目录 Head 3 保存名称到历史 FrameAddress。与 HS `HeadSetTag` 捕获当前 Head 集合不同。 |

### 3.3 管理、查询与业务补充信息

| 讨论用语 | 含义与边界 |
|---|---|
| **权威目录（authoritative directory）** | 决定哪些名称绑定正式存在的持久事实；丢失后不能任意从展示缓存猜回。 |
| **派生索引（derived index）** | 为某个查询维护、可由指定权威事实重建的数据。必须说明重建来源；不能仅因形状是字典就称其为缓存。 |
| **记录时间 / 领域时间** | 发布记录的时钟值与游戏日、回合等业务坐标分开。HS `RecordedAtUnixMs` 不保证单调，不能代替发布顺序或事务身份。 |
| **结构元数据（structural metadata）** | 解释身份、逻辑接续、创建来源、名称绑定所需的信息；由框架理解。 |
| **checkpoint metadata（检查点业务附注 / 摘要）** | app 对一个 checkpoint 的业务描述，如游戏日、地域、等级；内容对 HS 可不透明。归属、版本和更新语义待后续专题确定。 |
| **branch metadata（分支属性）** | 描述入口本身的属性，如显示名、颜色；不应与某次存档的摘要混称。暂未选定支持范围。 |
| **attachment / blob（附件 / 大块字节）** | 可按需读取的内容，如缩略图。用户提出以 FrameAddress 引用的候选；地址所属 Store、发布依赖、保留与错误语义仍须定义。 |
| **管理操作** | 创建、定位、重定位及可能的重命名、归档、删除等。这里只是能力分类，不表示这些 API 都存在或都要实施。 |

将 checkpoint metadata 编入 HS HeadEntryPayload，是一种**物理承载候选**，不能据此把它的概念归属改称“head 属性”。同理，内容放在独立 frame 中，不自动意味着它是可丢弃的派生缓存。

“可索引”不等于第一版支持任意字段检索；不透明 payload 本身也不提供业务字段索引。查找名称与展示名称是否相同，属于后续命名需求。

## 4. 标识符必须回答身份与作用域

| 当前标识符 | 标识什么 | 不能据此推断什么 |
|---|---|---|
| `HeadId` | 带 HeadStore 持久身份的 Head 身份；`LocalId` 只是库内编号。 | 名称、DG 分支身份、目标存在或内容健康。 |
| `HeadEntryId` | 实际 Head 身份 + 完整 ticket 的定位值；从发布、checked read 或 checked HeadSetTag 表取得，值可脱离 owner 比较。 | 业务 checkpoint 身份、读到的内容永远健康，或调用方选择来源的 UI 上下文。不是 DG 的 open-scoped 地址。 |
| `FrameAddress` | 指定 FrameStore 上下文中的物理帧位置。 | 仓库全局唯一身份、领域对象身份或发布成功。 |
| `CheckpointAddress` | 当前打开的 DG owner + 图帧位置；按这两者判等。 | 可序列化的跨重开书签。`RevisionAddress` 只是诊断投影。 |
| `ObjectId` | 图解释上下文中的领域对象身份。 | 某个 checkpoint、head、存储位置，或跨独立仓库的全局对象身份。 |
| branch / tag 字符串 | 当前仓库各自命名空间中的名称。 | 天生不变的逻辑身份；同拼写跨仓库不是同一对象。 |

后续设计若需要 `HistoryId` / `BranchId`，必须先证明“入口在改名或切换底层 head 后仍需保持身份”的需求。本文不预先分配新 ID，也不把现有名称与潜在稳定身份锁定为一一对应。

## 5. 四种关系不能统称 parent

1. **逻辑前驱**：某个 checkpoint 在 domain-history 中接续谁。当前为 `GraphEnvelope.Parent`。
2. **存储重建依赖**：Delta 依赖哪个 prior / 内容来源，目的是还原值。可能跨过 Event，不能代替逻辑前驱。
3. **head 创建来源**：HS child head 克隆了哪个实际 HeadEntry，记录在 header。
4. **操作来源上下文**：用户在何条逻辑历史或哪个分支视图中选择位置并发起分叉。不能仅由位置的物理归属反推。

**共同祖先**是对逻辑历史求交得到的结构关系，也不能单独确定第 4 项。

存储重建依赖内部也要注明对象：在 `S0 → E1 → E2 → S1` 中，S1 的逻辑 Parent 是 E2，图内容 source 是 S0；具体对象的 Delta prior 则指向该对象先前的存储记录。三者都不能含糊地叫“上一版本”。

例：A 发布 `a0 → a1 → a2`，B 从 a2 创建。用户在 B 的继承历史中选择 a1 创建 C。

- 内容位置 a1 实际属于 A 的发布记录。
- 用户选择的操作来源上下文是 B。
- 直接 `ForkHead(a1Revision)` 的 header 来源是 A。
- 从 B 自己的初始克隆 Entry 分叉时，来源可以是 B，但那份值对应 a2，不能冒充 a1。

这不是底层来源不准确，而是两种来源描述不同事实。workshop 的 `parentNumber` 保存第 4 项；若后续决定 UI 只展示内容祖先，应明确这是产品语义选择，不能称为从现有历史恢复了同一信息。

## 6. 操作用语与候选实现的分界

| 操作 | 本文讨论的可见效果 | 当前落点 / 边界 |
|---|---|---|
| publish | 让某个 head 接受新 payload。 | HS 层，不自动新增业务节点。 |
| commit | 提交一个领域 State 或 Event 并推进逻辑末端。 | DG `CommitState` / `CommitEvent`；各自独立发布。 |
| head fork | 新建底层 head，克隆准确来源 revision。 | HS `ForkHead`；复制 HeadEntryPayload 不意味着复制整个对象图。 |
| domain fork | 从逻辑位置创建新的应用历史入口。 | 当前 `CreateBranch` / `ForkShared`；后者另交付工作副本。是否对应 head fork 待选。 |
| rewind | 将既有逻辑入口选点退到本历史的旧位置，使以后提交从该位置接续。 | 讨论用语；当前可通过 `MoveBranch` 表达，但 `MoveBranch` 允许更广的跨线重定位。 |
| move / retarget | 将入口指向另一个合法位置，不限定祖先关系。 | 当前 `MoveBranch(name, expectedHead, target)` 检查预期 Head，要求关闭目标分支 checkout。 |
| undo | 产品所称的撤销意图，可能对应撤销内存编辑、rewind 或追加补偿。 | 必须由具体使用场景定义，未选择新的 API。 |
| compensation | 追加新的业务动作以补偿旧动作。 | 与 rewind 不同；重定位不会撤销已发生的外部副作用。 |

`MoveBranch` 当前只改分支目录并发布，不追加业务图、不删除旧图、不移动 tag；后续 checkout 从选定位置恢复最近 State。承载整个 DG 分支目录的 HS Head，其 head-log 留下目录发布轨迹；当前 domain-history 则沿选点的 Parent 链遍历。这里描述现有能力，不要求未来 API 原样保留。

## 7. 需求来自哪里

| 来源 | 已知需求或关注 | 尚不能推出的要求 |
|---|---|---|
| workshop 实际消费者 | 按游戏日浏览历史、从选定分支旧日分叉、显示父分支和 fork day、关闭后续写。当前以名字编码编号/父编号/日期。 | 没有大图或长期分支增长的性能数据；不证明必须引入通用 metadata 框架。 |
| 用户本轮明确方向 | 区分 head-log 与 domain-history；研究易用的 DG 历史层；名称映射需保持原子性并缩小写入范围、方便查询。 | 没有选定每分支一个 HS Head、父链、树型目录或具体复杂度指标。 |
| 用户另一个设计分支 | app 摘要可不透明，较大 BLOB 可按地址按需读取。 | 本片不确定编码、更新协议、格式版本或附件存储 API。 |
| 本轮分析提出的探问 | 入口改名后身份、回退后旧路线发现、分页游标、归档/删除与保留策略。 | 都是待确认的使用场景，不是承诺全部交付的需求。 |

“易于查询”还应拆为：按名定位、列出入口、按固定末端遍历逻辑历史、浏览发布轨迹、查询来源关系、读取摘要。历史节点数、分支数和单份 State 大小是不同成本维度。当前 `ReadFrames` 不恢复 State，但仍全链读取与分配；“无需反序列化 State”不等于“有分页或有界开销”。

## 8. 两种 domain-history 路线的共同讨论题

本节保留用户提出的两个候选，列出比较轴，不作优劣裁决。

| 比较轴 | A：显式逻辑父链 | B：rewind 时 fork head-log 并切换入口 |
|---|---|---|
| 接续关系载体 | checkpoint 记录逻辑前驱；入口选 tip。当前 DG 接近此路线。 | 利用发布序列和 fork 来源组成逻辑路径；应用入口可换到底层新 head。尚非当前 DG 实现。 |
| rewind 后继续 | 选旧 tip，新节点指向该点；底层发布仍可追加到原 head。 | 从准确选点创建新 log，入口切换后追加；旧 log 保留。 |
| 节点身份 | 明确 checkpoint 与物理图位置是否绑定。 | 明确 checkpoint 与 HeadEntry 是否绑定；fork 初始克隆不应未经定义就算额外业务节点。 |
| 非业务发布 | 目录 / 属性发布不在逻辑父链中。 | 必须定义哪些 Entry 属于业务节点；不能把元数据更新自动算成领域进展。 |
| 来源表达 | 逻辑前驱不能独自表达操作来源上下文。 | HS origin 表达实际克隆来源；继承祖先场景仍需裁决操作上下文语义。 |
| 原子性问题 | 名称、tip、其他必需根如何共同发布。 | 新 head 创建、入口改绑及其他根的发布如何形成可恢复的完整操作。 |
| 管理与查询 | 遍历父链、旧路线的发现入口、目录更新范围。 | log 数量与生命周期、继承遍历、入口切换轨迹、旧 log 的发现入口。 |

HS 当前 `ReadHeadHistory` 会交付 child 初始克隆和 source Entry；两项 payload 相等，但 EntryId 不同。B 必须定义这种存储级重复如何投影为业务历史；按 payload 字节相等去重也不能普遍成立，因为应用可能合法地连续保存相同状态。

HS 当前联合更新针对多个**既有** Head 的 payload，不把 `CreateHead` / `ForkHead` 自动纳入同一次调用。因此 B 中“fork 后改绑名称”不能仅凭有 `PublishHeads` 就宣称整体已经原子；需要后续协议证明。相同问题也约束任何采用动态 head 创建的目录方案。

### 供后续方案共同解释的场景

1. 保存 S0、S1、S2，rewind S0 后保存 S3：当前逻辑路径是 S0、S3；发布轨迹能否查询另行定义。
2. A、B 共享旧前缀，从 B 的继承祖先创建 C：明确 UI 所谓来源是实际内容归属还是操作来源上下文。
3. 关闭重开后，按名称或固定书签取回位置：不得复用旧 DG owner 的句柄。
4. 更新一个名称绑定或一个分支末端：说明必须一起可见的事实，以及实际需要重写的范围。
5. 创建新 head 后入口发布失败：说明哪些结果可见、如何重新发现或管理准备产物；不能承诺透明重试。
6. 仅浏览历史结构或摘要时，候选需求是不恢复业务 State；读取历史 State 本身可涉及领域对象构造与 Upgrade。是否完全不注册模型、是否分页、怎样固定遍历上界，留待查询合同裁决。

这些是需求澄清与方案检查题，不是本轮已通过的实验。

## 9. 后续讨论顺序

先裁决 domain-history 的可见语义：rewind 是否保留入口身份、旧路线需怎样被发现、UI 来源具体表达什么、重复保存和 fork 克隆是否形成节点。再用相同场景比较 A/B，之后讨论名称目录的原子性与更新成本。checkpoint metadata 独立展开，并与选定的 checkpoint 身份和发布边界衔接。

上游改名和 DG 接入不再待选；下一步是 domain-history 的需求和设计。持续状态由[产品工作集](../../../src/PROJECT-STATE.md)承载，未决研究入口由[路线图](../../DurableGraph-research-roadmap.md)维护；本目录暂不建立第二份状态账本。

## 10. 证据与阅读入口

| 要核对的事实 | 当前来源 |
|---|---|
| HS Head 身份、准确 Entry | [HeadId](../../../../atelia-storage/src/HeadStore/HeadId.cs)、[HeadEntryId](../../../../atelia-storage/src/HeadStore/HeadEntryId.cs) |
| HS 创建来源、来源查询、继承历史 | [Fork](../../../../atelia-storage/src/HeadStore/HeadStore.Fork.cs)、[ForkQuery](../../../../atelia-storage/src/HeadStore/HeadStore.ForkQuery.cs)、[History](../../../../atelia-storage/src/HeadStore/HeadStore.History.cs) |
| 联合发布范围、克隆重复项、地址与时间语义 | [Batch](../../../../atelia-storage/src/HeadStore/HeadStore.Batch.cs)、[现存克隆历史测试](../../../../atelia-storage/tests/HeadStore.Tests/ConditionalUpdateIntegration/ConditionalHistoryForkAuditTests.cs)、[FrameAddress](../../../../atelia-storage/src/FrameStore/FrameAddress.cs)、[HeadEntry](../../../../atelia-storage/src/HeadStore/HeadEntry.cs) |
| DG 图封套与内容来源 | [GraphPersistenceContracts](../../../src/DurableGraph.Persistence/GraphPersistenceContracts.cs)、[GraphRecordStore](../../../src/DurableGraph.Persistence/GraphRecordStore.cs) |
| DG 当前三入口布局、完整分支目录更新 | [RepositoryResources.MetaBootstrap](../../../src/DurableGraph.Persistence/RepositoryResources.MetaBootstrap.cs) |
| DG Fork / Move / ReadFrames 与地址作用域 | [Repository](../../../src/DurableGraph.Persistence/Repository.cs)、[CheckpointAddress](../../../src/DurableGraph.Persistence/CheckpointAddress.cs) |
| 下游来源语义和实际查询形态 | [PersistentGarden](../../../../durable-graph-workshop-a/Game/PersistentGarden.cs)：`Fork`、`GetGameCore`、`Inventory` |

初始概念研究的并行调查分别覆盖底层发布模型、DG 历史与标识符、下游需求与反例；主线程整合并复核关键源码。初始文档校验只证明链接与文本集成有效，不证明候选路线已可实施。

上游 `ReadTag` 只验证完整成员表，`ReadEntry` 再验证准确内容与 CU 资格；ID 不作永久健康证明。HeadSetTag 的名称空间不等于 DG branch/bookmark 目录。
