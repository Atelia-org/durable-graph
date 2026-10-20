# DB-096：Branch、Commit 与 WorkingCopy 的历史模型

> 状态：Branch/Commit/WorkingCopy、统一Catalog、多分支原子提交、根/子树和历史分页已完成实现、消费者迁移、最终源码与普通包验收，并合入本地master。准确来源、2992项根回归、12个活动包runner及资格限度见[最终交付验收](final-delivery-acceptance.md)。文档对齐：2026-10-19。
> 当前实现以源码和本片验收为准；术语导航见[项目术语表](../../DurableGraph-glossary.md)。
> 来源：workshop [反馈 4](../../../../durable-graph-workshop-a/docs/durable-graph-feedback.md)，以及 head-log / domain-history、原子名称映射和独立 metadata 需求。

**交付后反馈（2026-10-20）：**[Echo Garden研究](echo-garden-feedback.md)已落实为导航ID、分叉切换与版本化摘要三个[可执行接入配方](../../../experiments/PackageConsumerProbe/ApplicationRecipesConsumer/README.md)。根构建、定向源码测试及独立普通包运行已通过，证据与范围见[实施验收](application-recipes.md)；产品API/格式保持，无性能优化结论。

**2026-10-19 当前收敛：**用户已决定[同库统一 Schema Catalog](shared-schema-only.md)，开发草稿改用独立完整库副本。API 草图已删除 SchemaMode/Private/promotion；[产品 Private 删除切片](private-removal.md)已实施并通过源码、新包与有限进程中止验收。组发布已接入，限定资格见[实施验收](group-commit-integration.md)；新历史模型已合入本地master。

**跨轮续接先读 [PROJECT-STATE](PROJECT-STATE.md)**：已确认边界、最新身份裁决与未决顺序集中于此；其余材料按问题查阅。当前讨论入口是[API 草图](api-sketch.md)：推荐 Branch / Commit / WorkingCopy 词汇，已串起五个调用故事；[命名审阅](api-naming-review.md)记录依据与旧名映射。DG登记已完成实现与验收。以下早期现状对照保切换前基线；实际交付范围与限制以最终验收为准。

**本期收敛与交付已完成。** 后续由真实消费者反馈选择增量；重复扫描、索引与缓存优化须有成本证据，保持已接受功能和易用性，明确延期项不自动成为新任务。

**首批机制调查已完成：**[API 到源码机制的映射](mechanism-feasibility.md)记录 Shared 共同准备、登记两层 outcome、最近 State 内容依赖、来源查询范围及目录容量估算；给出下一批最小实验。该调查保其当时的源码证据；后继实现与运行边界由施工记录限定。

**后继收窄与局部实施：**[组内核](group-kernel-design.md)明确一个 accepted Catalog、一次共同候选及阶段清理/安装；旧目录真实 State/Event 混组已有发布和冷 Delta 续写见证。现有格式下[来源 snapshot 接缝 A 已接通并验收](history-source-seam.md#7-a-实施与验收)，无需新 proof/token，保存仍复核实际图。[B1 新依赖接入](storage-handle-adaptation.md)之后，按[整体切换计划](branch-cutover-plan.md)实施[Branch 纵切](branch-vertical-design.md)、删除业务 Parent；书签与既有历史读取同时迁移到 CommitId。旧布局不再复制正式组协调器。请求不设 Consumed，分页预算耗尽整次失败；[独立 HS Probe](../../../experiments/BranchHeadMechanismProbe/README.md)保其限定范围；[定向 origin 请求](headstore-origin-request.md)已由[上游交付与 DG 接入](targeted-origin-integration.md)闭合，未把单点查询扩大为全库审计。

需求依据见[用户故事与能力提炼](user-stories.md)，以 DramaBoard 多世界线明确对外行为，再用本文的分层模型比较实现。[已收敛的上游命名](storage-naming-candidates.md)与[DG 接入切片](headstore-adaptation.md)记录已完成边界。早期 NamedValue / HeadSnapshot 提案已被实际 `HeadSetTag` 取代，不再作为待选 API。

[应用合同与并行设计稿](application-design.md)整合操作、标签/摘要、发布和查询分析：在首版无 rewind、业务 Head 仅真实提交的条件下，优先验证继承 head-history，显式父链降为对照；标签承载与 DG 历史窗口 API 仍待收敛，上游准确历史起点已交付。

[存档定位值的五个调用故事](save-handle-stories.md)进一步串起继续、书签、分页、fork 和 LLM 恢复；[新的 HeadStore 请求](headstore-save-handles-request.md)收敛两类 ID 编解码、准确 Entry 历史起点和使用时资格。它取代[原请求](headstore-requests.md)中 R1 的具体范围；原文继续保留已实施引用初始化的比较依据。

[最新交付复核与 API 方向](headstore-delivery-review.md)：上游同库 codec/准确历史已可用，独立消费者通过。用户明确跨库检查只需开发期诊断，[概率与穷举实验](handle-misuse-detection.md)支持默认保留紧凑 Handle，撤回每个引用附完整 scope 的建议；DG 的新 pin 源码接入见[B1 记录](storage-handle-adaptation.md)，新历史模型和普通包资格仍分开验证。

用户已明确 DG 当前 publish 设计与实现是可重写草稿；本文列举的 GraphEnvelope.Parent、目录布局与发布 API 是现状证据，不是新模型的兼容性约束。

[根/子树查询](branch-tree-queries.md)已实施并限定验收；用户提供的密码门和知识神器故事见[用户故事 C4/C5](user-stories.md)。[多 Branch 联合提交设计](multi-branch-commits.md)已选 CommitRequest 与执行时统一捕获，已落实组内 Schema 和发布边界，当前资格见[实施验收](group-commit-integration.md)。

## 1. 本轮问题与边界

当前首批调查的最小验收标准：各 API 组有现有原语、缺口、访问范围与失败点；跨包结论经源码复核，下一批实验有可观察判据。范围与结果集中在[机制调查](mechanism-feasibility.md)，本批不代表产品改名、接入或运行验收。

此前文档对齐已完成：活动设计使用 API 草图的概念和调用职责；底层 API、旧源码符号与历史证据保持可辨认；无法直接对应的旧方案注明适用范围。

### 当前词汇与阅读边界

| 当前设计概念 | 与底层或旧源码的关系 | 不能混同的角色 |
|---|---|---|
| Branch / BranchId | DG 正式业务成员；BranchId 推荐为 HeadId 的薄角色类型 | DisplayLabel 可重复，不是旧 branchName 唯一键；管理 Head、未登记 Head 不是 Branch |
| Commit / CommitId | 一次已接受的 State 或 Event 保存；CommitId 推荐对应 HeadEntryId | 不等于图帧地址、完整快照、任意 HS 发布或多 Head 事务；fork 不制造 Commit |
| WorkingCopy | 绑定 Branch 的编辑对象；旧源码近似职责为 BranchCheckout | BaseCommitId 与 StateSourceCommitId 分开；CommitEvent 不隐含保存 State 编辑 |
| CommitInfo / LoadedState | 前者是描述，后者包含选点与最近已保存 State 的来源 | 旧 Checkpoint 是恢复出的对象视图，不能直接改名成 CommitInfo；加载不执行 Event |
| ForkedFromCommitId / CreatedOnBranchId | 前者固定准确分叉点；后者是 Commit 实际发布分支 | 选中 Branch 与 Commit 的实际归属可能不同；不按操作 UI 来源建树 |

`Read/List` 读取描述，`Load` 物化对象，`OpenWorkingCopy` 获取编辑能力，`Create/Fork/Commit` 发布。调用职责以 [API 草图](api-sketch.md)为基准，未决协议由其链接的专题继续细化。下文源码对照中的 Checkpoint、branch name、checkout 以及旧 rewind 比较保留原意，不能覆盖这套新外观。

### 初始研究边界（历史）

我们要能分别回答：一个发布入口曾经指向什么、应用当前承认哪条逻辑历史、用户通过什么名字访问它、以及这些事实由什么标识符关联。

初始研究的最小验收标准：用同一组术语解释保存、rewind 后续写、从继承祖先分叉、重开定位和名称绑定；区分现有事实与候选设计，并能指出两种 domain-history 路线必须共同回答的问题。只核对源码、测试内容和下游用法，不宣称运行实验或性能资格。

本轮形成首选验证候选，不冻结 metadata 编码和附件协议。用户此前提出的“一世界线一 Head、Entry payload 存父 HeadEntryId”保留为[父链对照](user-stories.md#26-候选细化一世界线一-headentry-payload-显式父链)。既有有限源码适配只增加独立书签目录并迁移上游 API，不代表完成新历史模型。

DG 既有接入基线为 Storage `731eef7cd3158572532fbeea7ba95a79213a7328`，见[固定依赖](../../../eng/StorageDependency.props)与[接入切片](headstore-adaptation.md)。既有交付复核 HEAD 为 `3e1dc46041ce731768f18552639c18fbf73e111e`、包来源为 `c6fb64a97ebc066e10c480e4439f20a2067683a2`；本批另查 `f8868b5` 源码，完整基线见[机制调查](mechanism-feasibility.md#2-本次源码基线)。以下 HS 现行术语已按紧凑 Handle 校准，不表示 DG pin、代码或测试资格已更新。

## 2. 先区分两种历史

**head-log（头发布历史）**回答“某个 head 接受过哪些发布值”；**domain-history（领域逻辑历史）**回答“从选定 Commit 回看，哪些 Commit 构成这条继承历史”。旧源码以 checkpoint 与 Parent 链表达逻辑历史，见 §3 的源码对照。

head-log 记录已接受的发布，不等于全部编辑动作、失败尝试或事务审计日志。domain-history 是应用通过 DG 表达的逻辑接续，不等于领域事件自动重放；State 与 Event 都可以构成节点。

以下是早期区分两种历史的反例，**不是当前 API 需求**；首版只追加，旧点继续须 ForkBranch。假设用户保存 S0、S1、S2，然后 rewind 到 S0，再保存 S3：

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

## 3. 底层与旧源码的概念—术语对照

本节用于解释底层和既有源码；不是与 API 草图竞争的命名建议。当前符号保持原拼写，旧 DG 术语的对应关系见 §1。概念分开不意味着必须新增同名类型或独立文件。

### 3.1 存储与发布

| 讨论用语 | 含义与不应混入的含义 | 当前标识符 / 状态 |
|---|---|---|
| **head（发布头）** | 有身份、可接受新发布值的持久入口；讨论其当前值时明确写 head value。不是某个固定 revision。 | `HeadStore` / `HeadId`，已实施；Head 身份与 current Entry 分开。 |
| **head value（发布头当前值）** | 当前已接受 Entry 的不透明 HeadEntryPayload。可能编码图地址、目录根或描述记录；不是天然的业务 State。 | `ReadHead` 返回的 `HeadEntry.Payload`；精确旧位置使用 `ReadEntry`。 |
| **HeadEntry（头修订）** | 某个 head 上一次已接受发布的准确位置与对应值。同值的两次发布仍可有不同 Entry。 | HS `HeadEntryId`、`HeadEntry`。 |
| **本地 head-log** | 同一 head 自己的已接受发布序列。其顺序有发布意义，不自动有业务祖先意义。 | 同一 Head 文件中经 HS 校验后交付的发布记录；不能直接把全部物理帧当作有效发布。 |
| **继承 head-history（含来源的头历史）** | 本地 log 逆序遍历后，沿创建来源接续源 log 的准确前缀。用于明确区别单个文件的本地序列。 | `ReadHeadHistory` 当前提供此遍历；不是新增另一种存储。 |
| **head fork origin（头创建来源）** | 新 head 引用哪个实际 source HeadEntry 作为固定初值。 | `HeadOriginInfo.SourceHeadId / SourceEntryTicket` 的来源声明；`ForkHead` 写入。 |
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
| **逻辑历史入口** | 应用用于选择、推进或重定位一条逻辑历史的入口。它可在 rewind 后继续存在，而底层 head 是否改变由方案决定。 | 当前最接近 DG branch；新设计采用 BranchId；原线重定位不进入首版。 |
| **branch（DG 分支）** | 当前是持久的名称到 domain tip 的绑定。新设计已分开 BranchId、DisplayLabel 与底层 Head，见 API 草图；此行仅描述旧实现。 | `ListBranches`、`GetHead`、`CreateBranch`、`ForkShared`；不能等同 HS Head。 |
| **name binding（名称绑定）** | 字符串到目标身份或位置的关联；名称比较、唯一性、改绑规则属于目录语义。 | 当前 `branchName → FrameAddress`；新设计以 BranchId 定位，不以 DisplayLabel 查找；旧名称目录不是新设计的必需兼容层。 |
| **checkout（工作副本）** | 可编辑的内存对象、保存基线及编辑占用，不是持久入口或历史节点。 | `BranchCheckout`；`CheckoutShared`。 |
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
| `HeadId` | 4B 紧凑库上下文 Handle；通过公开 codec 读回；局部编号由 owner 的 `GetLocalHeadId` 解释。 | 不含完整库身份，跨库相等/误命中可能发生；不证明名称、DG 分支资格、存在或健康。 |
| `HeadEntryId` | 紧凑 HeadId + 完整 ticket；12B 持久表示，可脱离 owner 编解码/比较，使用时校验目标。 | 不证明完整库身份、业务 checkpoint 资格或永久健康；不含选中世界线 UI 上下文。按已确认目标用于对应仓库上下文，不默认增加完整 scope。 |
| `FrameAddress` | 指定 FrameStore 上下文中的物理帧位置。 | 仓库全局唯一身份、领域对象身份或发布成功。 |
| `CheckpointAddress` | 当前打开的 DG owner + 图帧位置；按这两者判等。 | 可序列化的跨重开书签。`RevisionAddress` 只是诊断投影。 |
| `ObjectId` | 图解释上下文中的领域对象身份。 | 某个 checkpoint、head、存储位置，或跨独立仓库的全局对象身份。 |
| branch / tag 字符串 | 当前仓库各自命名空间中的名称。 | 天生不变的逻辑身份；同拼写跨仓库不是同一对象。 |

新设计已选 BranchId / CommitId 薄角色类型，分别对应紧凑 HeadId / HeadEntryId（4B / 12B）。这来自继续分支与固定书签的不同职责，不依赖改名或切换底层 Head；不另增 HistoryId 或完整库 scope。最小 codec 见[API B2 合同](api-sketch.md#b2-的具体值与迁移合同)，DG durable 字段适配后置。上表中的 CheckpointAddress 和字符串仍是旧源码事实。

## 5. 四种关系不能统称 parent

1. **逻辑前驱**：某个 checkpoint 在 domain-history 中接续谁。当前为 `GraphEnvelope.Parent`。
2. **存储重建依赖**：Delta 依赖哪个 prior / 内容来源，目的是还原值。可能跨过 Event，不能代替逻辑前驱。
3. **head 创建来源**：HS child head 固定引用哪个准确来源 HeadEntry，记录在 header；初始克隆属于旧实现。
4. **操作来源上下文**：用户在何条逻辑历史或哪个分支视图中选择位置并发起分叉。不能仅由位置的物理归属反推。

**共同祖先**是对逻辑历史求交得到的结构关系，也不能单独确定第 4 项。

存储重建依赖内部也要注明对象：在 `S0 → E1 → E2 → S1` 中，S1 的逻辑 Parent 是 E2，图内容 source 是 S0；具体对象的 Delta prior 则指向该对象先前的存储记录。三者都不能含糊地叫“上一版本”。

例：A 发布 `a0 → a1 → a2`，B 从 a2 创建。用户在 B 的继承历史中选择 a1 创建 C。

- 内容位置 a1 实际属于 A 的发布记录。
- 用户选择的操作来源上下文是 B。
- 直接 `ForkHead(a1Revision)` 的 header 来源是 A。
- 旧 clone 实现曾给 B 生成初始 Entry；新引用初始化不生成它，B 未续写时读取当前值直接得到源 a2，不能冒充 a1。

这不是底层来源不准确，而是两种来源描述不同事实。workshop 的 `parentNumber` 保存第 4 项。**DramaBoard 当前故事已明确选择 C 挂在 A 下**，按准确存档归属表达来源，不要求保留“从 B 的视图创建”；因此该故事可省去操作来源上下文。这个选择与 workshop 原有展示语义不同，不能称为从现有历史恢复了同一信息，也不据此修改 workshop。

在一世界线一 Head 候选中，历史列表保留 a1 的原始身份，再调用 `ForkHead(a1.Id)`，即可使底层 origin 与上述业务来源一致。父线继续发布不改变子线固定的分叉位置。旧实现若选到 fork 初始克隆，曾需解释它代表的源存档；该历史对照见用户故事 §2.6，当前引用初始化不再产生这种克隆项。

## 6. 操作用语与候选实现的分界

| 操作 | 本文讨论的可见效果 | 当前落点 / 边界 |
|---|---|---|
| publish | 让某个 head 接受新 payload。 | HS 层，不自动新增业务节点。 |
| commit | 提交一个领域 State 或 Event 并推进逻辑末端。 | DG `CommitState` / `CommitEvent`；各自独立发布。 |
| head fork | 新建底层 head，固定引用准确来源 Entry。 | 新 HS `ForkHead` 返回新 HeadId，不生成克隆 Entry，也不复制对象图。 |
| domain fork | 从逻辑位置创建新的应用历史入口。 | 当前 `CreateBranch` / `ForkShared`；后者另交付工作副本。新外观为 ForkBranch(sourceCommitId, displayLabel)，推荐登记候选 B；成功交付 BranchInfo，另行 OpenWorkingCopy。 |
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
| DramaBoard 多世界线故事 | 多开局森林；父子线均可继续；可枚举历史并分叉，按选中存档归属挂接；重复且相对固定的显示标签；每次提交一同发布摘要。详见[用户故事](user-stories.md)。 | 不要求 Git 式唯一分支名或操作视图来源；一世界线一 Head 与 payload 父链仍是候选，rewind 未列为必需。 |
| 本轮分析提出的探问 | 入口改名后身份、回退后旧路线发现、分页游标、归档/删除与保留策略。 | 都是待确认的使用场景，不是承诺全部交付的需求。 |

“易于查询”还应拆为：按名定位、列出入口、按固定末端遍历逻辑历史、浏览发布轨迹、查询来源关系、读取摘要。历史节点数、分支数和单份 State 大小是不同成本维度。当前 `ReadFrames` 不恢复 State，但仍全链读取与分配；“无需反序列化 State”不等于“有分页或有界开销”。

## 8. 两种 domain-history 路线的共同讨论题

本节保留早期以 rewind 为比较题的两条路线。首版现已明确不需要原线 rewind，因此不必在两者之间预选一整套回退机制；继承历史直接表达只追加领域历史的较小候选见[应用合同设计稿](application-design.md)。下表供需求变化时复查。

| 比较轴 | A：显式逻辑父链 | B：rewind 时 fork head-log 并切换入口 |
|---|---|---|
| 接续关系载体 | checkpoint 记录逻辑前驱；入口选 tip。当前 DG 接近此路线。 | 利用发布序列和 fork 来源组成逻辑路径；应用入口可换到底层新 head。尚非当前 DG 实现。 |
| rewind 后继续 | 选旧 tip，新节点指向该点；底层发布仍可追加到原 head。 | 从准确选点创建新 log，入口切换后追加；旧 log 保留。 |
| 节点身份 | 明确 checkpoint 与物理图位置是否绑定。 | 明确 checkpoint 与 HeadEntry 是否绑定；fork 初始克隆不应未经定义就算额外业务节点。 |
| 非业务发布 | 目录 / 属性发布不在逻辑父链中。 | 必须定义哪些 Entry 属于业务节点；不能把元数据更新自动算成领域进展。 |
| 来源表达 | 逻辑前驱不能独自表达操作来源上下文；DramaBoard 已选择不以该上下文建树。 | HS origin 表达准确来源 Entry；当前引用初始化直接使用源 Commit 对应的位置，符合 DramaBoard 的归属选择。 |
| 原子性问题 | 名称、tip、其他必需根如何共同发布。 | 新 head 创建、入口改绑及其他根的发布如何形成可恢复的完整操作。 |
| 管理与查询 | 遍历父链、旧路线的发现入口、目录更新范围。 | log 数量与生命周期、继承遍历、入口切换轨迹、旧 log 的发现入口。 |

旧 HS `ReadHeadHistory` 会交付 child 初始克隆和 source Entry，两项 payload 相等但 EntryId 不同；新上游引用初始化已取消该重复。用户认可 fork 不增加领域存档，真实的同值重复保存仍保留，不按 payload 去重。[请求 R2](headstore-requests.md)保留前后比较与接入场景；DG 仍需验证新上游和所选领域历史模型。

HS 当前联合更新针对多个**既有** Head 的 payload，不把 `CreateHead` / `ForkHead` 自动纳入同一次调用。因此 B 中“fork 后改绑名称”不能仅凭有 `PublishHeads` 就宣称整体已经原子；需要后续协议证明。相同问题也约束任何采用动态 head 创建的目录方案。

### 供后续方案共同解释的场景

1. 保存 S0、S1、S2，rewind S0 后保存 S3：当前逻辑路径是 S0、S3；发布轨迹能否查询另行定义。
2. A、B 共享旧前缀，从 B 的继承祖先创建 C：DramaBoard 要求 C 挂在实际存档所属的 A 下；A、B、C 各自可继续。核对准确分叉位置与初始克隆的解释。
3. 关闭重开后，按名称或固定书签取回位置：不得复用旧 DG owner 的句柄。
4. 更新一个名称绑定或一个分支末端：说明必须一起可见的事实，以及实际需要重写的范围。
5. 创建新 head 后入口发布失败：说明哪些结果可见、如何重新发现或管理准备产物；不能承诺透明重试。
6. 仅浏览历史结构或摘要时，候选需求是不恢复业务 State；读取历史 State 本身可涉及领域对象构造与 Upgrade。不注册模型并非硬要求；新设计已选固定 CommitId 起点的 ReadHistoryPage，AtOrBefore/Before 与 Continuation 表达边界，页满成功、工作预算耗尽整次失败，实际资格见[分页验收](history-pagination.md)。

这些是需求澄清与方案检查题，不是本轮已通过的实验。

## 9. 后续验证顺序

调用职责与概念以 [API 草图](api-sketch.md) 为基准；功能、创建故障与逐片源码/包资格已完成，已完成[最终交付验收与合入](final-delivery-acceptance.md)，不再从头海选命名。最终产品版本的完整门禁、合入与当前停点集中在 [PROJECT-STATE](PROJECT-STATE.md)。

[应用合同稿](application-design.md)保留登记候选 B 与上游属性候选 A 的机制比较；[联合提交设计](multi-branch-commits.md)采用 CommitRequest 与执行时捕获。§8 的 rewind 比较仅作需求变化时的历史对照，不是当前实施路线。恢复材料、创建可见性与失败边界已有源码见证；其范围见各分片记录，最终版本的整体资格以交付清单为准。

产品实际能力仍由[产品工作集](../../../src/PROJECT-STATE.md)承载，全局未决入口由[路线图](../../DurableGraph-research-roadmap.md)维护。用户要求跨上下文续接后，本设计问题使用本地 [PROJECT-STATE](PROJECT-STATE.md)维护简短研究焦点与已确认边界，不复制产品能力或完整设计账本。

## 10. 证据与阅读入口

| 要核对的事实 | 当前来源 |
|---|---|
| HS Head 身份、准确 Entry | [HeadId](../../../../atelia-storage/src/HeadStore/HeadId.cs)、[HeadEntryId](../../../../atelia-storage/src/HeadStore/HeadEntryId.cs) |
| HS 创建来源、来源查询、继承历史 | [Fork](../../../../atelia-storage/src/HeadStore/HeadStore.Fork.cs)、[ForkQuery](../../../../atelia-storage/src/HeadStore/HeadStore.ForkQuery.cs)、[History](../../../../atelia-storage/src/HeadStore/HeadStore.History.cs) |
| 联合发布范围、克隆重复项、地址与时间语义 | [Batch](../../../../atelia-storage/src/HeadStore/HeadStore.Batch.cs)、[现存克隆历史测试](../../../../atelia-storage/tests/HeadStore.Tests/ConditionalUpdateIntegration/ConditionalHistoryForkAuditTests.cs)、[FrameAddress](../../../../atelia-storage/src/FrameStore/FrameAddress.cs)、[HeadEntry](../../../../atelia-storage/src/HeadStore/HeadEntry.cs) |
| DG 图封套与内容来源 | [GraphPersistenceContracts](../../../src/DurableGraph.Persistence/GraphPersistenceContracts.cs)、[GraphRecordStore](../../../src/DurableGraph.Persistence/GraphRecordStore.cs) |
| DG 当前三入口布局、完整分支目录更新 | [RepositoryResources.MetaBootstrap](../../../src/DurableGraph.Persistence/RepositoryResources.MetaBootstrap.cs) |
| DG Fork / Move / ReadFrames 与地址作用域 | [Repository](../../../src/DurableGraph.Persistence/Repository.cs)、[CheckpointAddress](https://github.com/Atelia-org/durable-graph/blob/866ce89705888af5fea56a5532c7d1986a21ca65/src/DurableGraph.Persistence/CheckpointAddress.cs "Pre-B2 source: Git 866ce89") |
| 下游来源语义和实际查询形态 | [PersistentGarden](../../../../durable-graph-workshop-a/Game/PersistentGarden.cs)：`Fork`、`GetGameCore`、`Inventory` |

初始概念研究的并行调查分别覆盖底层发布模型、DG 历史与标识符、下游需求与反例；主线程整合并复核关键源码。初始文档校验只证明链接与文本集成有效，不证明候选路线已可实施。

上游 `ReadTag` 只验证完整成员表，`ReadEntry` 再验证准确内容与 CU 资格；ID 不作永久健康证明。HeadSetTag 的名称空间不等于 DG branch/bookmark 目录。
