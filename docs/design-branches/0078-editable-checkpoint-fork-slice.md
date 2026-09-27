# DB-078：分支工作副本与可编辑检查点 fork
<a id="db-078多会话与可编辑检查点-fork"></a>

> 状态：**A 已实施并验收；B/C 尚未实施**。2026-09-27 按 DB-083 已选产品语义施工。原源码核对基线 `343bfa6`。
> 目标以 [DB-083 用户故事与 Checkpoint API](0083-repository-checkpoint-api-user-stories.md) 为准；本文拆成 A/B/C 三个独立可验收阶段，不承诺一个主会话完成全文。
> 顺序：第 2 片；依赖 [DB-077](0077-repository-model-environment-slice.md)，下一片 [DB-079](0079-shared-graph-restoration-core-slice.md)。
> A 的分工与验收见[实施记录](0078-a-repository-free-history-implementation.md)；B/C 保留后续合同。需求与约束来源见 [DB-076](0076-efficient-graph-fork-technical-path.md)。
> 每 branch 单工作副本及故障边界的原裁决见 [历史专项评审](0077-0078-api-dialectical-review.md)；本次公开 API 与阶段范围以本文和 DB-083 为准，不开放同 branch 竞争提交或新增专用 head 冲突异常。
> 术语遵循[项目术语表](../DurableGraph-glossary.md#branch-checkout)：A 已采用 `BranchCheckout` / `Checkout`；旧名仅在实施前事实说明中保留。

## 1. 交付范围与阶段停点

**统一非泛型 Repository/BranchCheckout、自由 E/S 历史和独立历史读取，然后交付不同 branch 各一个工作副本及 named Fork。**
三阶段全部完成才覆盖 DB-083 的分支/Checkpoint 核心产品；独立 tag 分片另行交付，后续所有优化关闭也必须正确。
仅串行调用；用一个活动分支名集合落实每 branch 单工作副本，不增加多线程支持、跨进程 writer、强持工作副本对象的表、branch epoch 或锁租约。

A 实施前源码的 [Repository](../../src/DurableGraph.Persistence/Repository.cs) 已有 ref-only CreateBranch、`Resume`、
exact head/State baseline 检查及 CAS；但 Commit、PreviousStateCore 和 ValidateHistory 都依赖交替，
[WorldWorkspace](../../src/DurableGraph.Persistence/WorldWorkspace.cs) 当时还依赖 exact 泛型根；这是本片同时修改保存工作区的原因。

| 阶段 | 独立可运行停点 | 尚未交付 |
|---|---|---|
| 078-A 公共基础与自由历史 | 非泛型入口、地址、Event-first 与 E/E/S/S、可空 State、异型根替换、基础单图与 ref 操作、冷重开和真包消费者 | 仍每 repo 至多一个工作副本；无便利 Checkpoint、按需事件枚举、Fork |
| 078-B 历史读取与查询 | 独立 Checkpoint、稳定 getter、Event 小读、固定历史范围事件枚举 | 多分支工作副本及一步 Fork |
| 078-C 多分支与 Fork | 每 branch 占用、完整 guarded Fork、发布失败处理；源工作副本可保持存活 | 所有准备材料/实例复用优化 |

每阶段是可单独安排主会话配合有界 subagent 的任务，分别完成构建、回归、受影响真实包消费者和证据更新；
阶段间先读取上一阶段实际结果，不因处于同一文档就自动连做。推荐 A→B→C；C 的恢复机制硬依赖 A，
B 只是推荐先交付的产品顺序，不是 Fork 的技术前提。A/B 阶段的限制是明确的未完成范围，不另设运行时模式或兼容双轨。

### 1.1 078-A：非泛型公共基础与自由历史

一次迁移为根命名空间 `Atelia.DurableGraph` 下的 `Repository`、非泛型 `BranchCheckout`；
实现可以留在现有 Persistence 程序集，不新增转发门面或按命名重组程序集。
模型仍由 DB-077 的 Open 固定环境提供。以下省略保存策略/options 等非差异参数：

```csharp
BranchCheckout CreateBranch(string name, IDurableObject initialState);
BranchCheckout CreateBranchFromEvent(string name, IDurableObject initialEvent);
BranchCheckout Checkout(string name);
CheckpointAddress GetHead(string name);
CheckpointAddress CreateBranch(string name, CheckpointAddress from); // ref-only
void MoveBranch(string name, CheckpointAddress expectedHead, CheckpointAddress target);
IDurableObject ReadState(CheckpointAddress address);
IDurableObject ReadEvent(CheckpointAddress address);
(IDurableObject First, IDurableObject Second) ReadPair(CheckpointAddress first, CheckpointAddress second);

// BranchCheckout : IDisposable
IDurableObject? State { get; }
CheckpointAddress Head { get; }
CheckpointAddress CommitEvent(IDurableObject domainEvent);
CheckpointAddress CommitState();
CheckpointAddress CommitState(IDurableObject nextState);
```

`CheckpointAddress` 是本次打开实例签发的 opaque 历史位置，不是 StateRevision 地址的别名；
外 repo/重开旧地址拒绝，重开从持久 ref 取得新地址。A 已选择不可变 sealed class，
不能靠裸坐标恰好合法认证来源。地址由库签发，没有公共裸坐标构造/反序列化入口；默认值、未签发值、
外 repo 或重开旧地址在访问持久记录前拒绝，不把裸坐标存在当作来源证明。
同一打开 owner 与同一逻辑 Journal 坐标的地址相等；重复 GetHead/读取签发不要求 ReferenceEquals，
null（包括该引用类型的 default）无效。地址只表示已提交历史位置，不承诺跨打开相等。
本阶段不提供可序列化外部地址或 tag；不可变 tag 已纳入目标，按 DB-083 的独立上游分片安排，不阻塞 A/B/C。
GetHead、提交返回、工作副本 Head、ref-only、Move、单图/Pair 读取与元数据历史入口必须同时迁移，
不混用新地址和旧 GraphFrame；不保留无当前需求的 typed 读取便利重载、泛型工作副本或旧名称兼容壳。

ReadState/ReadEvent 按角色拒绝错误地址，均返回实际领域根；ReadPair 接受两种角色，保持输入次序与显式只读共享合同。
ref-only CreateBranch 只创建持久 ref、返回所选地址，不恢复领域图或占用编辑名，不要求 State/Event 的 current 模型能力。
ReadFrames 可保留用于元数据全链检查，并将其位置结果统一为新地址；其全量成本须明确。
原 ReadEvents 在 A 仅作为尚未迁移的全量查询入口保留，位置结果也改用新地址；B 同次迁移消费者并以 EnumerateEvents 取代它，
不长期保留两套事件查询。所有这些入口继续使用同一仓库可用性、来源与防重入检查。

两种首建入口直接发布首个检查点：CreateBranch 发布非空 State，正常返回保留传入根；
CreateBranchFromEvent 发布非空 Event，正常返回 Head 为该 Event、State 为 null，不将 Event 安装为 State。
不提供空 head 分支或额外初始化工作副本类型；同一 CLR 类型可由不同入口承担 E/S 角色。

定义 P 为提交前精确 Head（首建时不存在），B 为 P 自身或其逻辑祖先中最近已提交 State（可能不存在）。
每条记录的 Journal Parent=P；Event 图和新 State 图的 StateRevision Parent=B.Revision，B 不存在则为 null。
只有成功 State 提交安装新保存基线。以下是两条独立历史，S0 表示各自的首个 State：

| 发布位置 | Journal Parent | 图保存 Parent |
|---|---|---|
| State-first：S0 | null | null |
| E1 | S0 | S0 |
| E2 | E1 | S0 |
| S1 | E2 | S0 |
| S2 | S1 | S1 |
| Event-first：E0 | null | null |
| E1 | E0 | null |
| S0 | E1 | null |
| E2 | S0 | S0 |

提交前检查、最近 State 导航和冷开 ValidateHistory 必须共同实现该规则：workspace 的已提交基线与 B 同时缺失，
或指向 exact 相同 State 修订及根来源。对每条记录验证图 Parent 时，从其严格祖先查找 B；不能把该记录本身当作自己的基线。
保留全部物理记录、orphan、ref 目标、图根 membership、完整引用/来源验证与 append-only，不能只放宽热提交或只检查 ref 可达链。
最近 State 沿已验证逻辑 Parent 查找，可复用现有记录索引的派生导航，不增第二套持久权威。
Checkout 保持精确 Head：有 B 时仅恢复 B 并导入完整保存来源；无 B 时交付 State=null 的工作副本，
不物化任何 Event、不提供 PendingEvent、不自动 replay。只有有效链上从未存在 State 才返回 null；
已有 State 的损坏、缺模型/reader/Upgrade 等失败必须传播，不回退旧 State 或假装没有 State。
空 registry 可以打开有效历史并 Checkout 纯 Event 前缀；这不豁免 Open 的全物理数据验证，也不赋予它读取 Event 图的能力。

无 State 时 CommitEvent 继续发布 Event；CommitState(nonNullRoot) 建立首个 State；无参 CommitState() 在 Capture/追加前拒绝。
首 State 之前每个 Event 图均按无 Parent 的完整 Base 准备，Event 成功也只丢弃候选，不 Accept 其 DTO/live 身份绑定，
故首 State 仍以空基线保存。热捕获可消耗工作副本 ID 游标；Event-only 冷恢复用新的空 CaptureSession，不扫描或导入历史 Event 身份。
不同 Revision 中数字 ID 可以重合，不能据此要求恢复 Event 图或制造跨检查点永久唯一 ID。
已有 State 时 Event 仍基于最近 State 保存且不推进基线。Head 始终保持所选位置，事件处理进度由应用解释。

根模型按每次实际 CLR 类型与持久 Schema 选择，不能机械套 `WorldWorkspace<IDurableObject>` 或继续固定创建时的 `_model`。
CommitState(nextState) 允许跨类型非空根，领域类型限制由应用承担；正常返回后将传入原实例、root ID、
根 binding、完整保存基线及接受的 live 身份映射一致安装，后继无参提交保存这个根；旧 CLR 引用不自动跟随。
这些候选材料在发布前准备，发布后不重新选择模型或调用用户 callback。跨类型替换不是 Schema Upgrade，
也不重建一个丢失旧身份的 workspace：已有异型 child 升为 root 保持原 ID，新实例才分配新 ID；
旧 root 若仍被新图引用则保留，仅不再可达的对象按既有规则 Remove。同 ID 的 exact 布局/模型与完整来源校验继续保持。
预先可知的 null/未注册直接根在 Capture、Schema 登记和追加前拒绝，Head/State/已提交基线不变。
深层模型、Schema 或回调失败不能笼统声称均无物理写入；仍按实际追加、publication outcome 与 fault 恢复，不承诺回滚。

本阶段最小验收：真包消费者不在分支调用端指定领域类型；无共同领域基类的 E/S，亦允许同 CLR 类型承担两种角色；
S0→E1→E2→S1→S2，以及 E0→E1 冷重开→首 S0→异型 S1→无参 S2 均冷热续写正确。
Event 不推进 State 基线、Upgrade rewrite 义务与完整来源保持；仅 State 模型可从已有 State 的 Event Head Checkout；
空 registry 可从纯 Event 历史 Checkout，但不能读取缺能力的图，已有 State 缺能力不得返回 null。
无 State 的无参保存、null/未注册直接根、无效地址在追加前拒绝；异型 child 升根、旧根降为子对象、旧根不可达 Remove、
原实例安装与后继无参保存均正确。物理交错分支按各自 Parent；坏图 Parent/缺失祖先/orphan 损坏冷开拒绝。
Event-only 对象数字 ID 与冷恢复后的新对象可以重合，但各 Revision 正确；首 Event 创建与首 State 提交的
NotPublished/Unknown/Published、fault/reentry/Dispose/只读均须覆盖，失败不误安装候选，不自动重试或修尾。
先完成这个独立停点，不把冷开或保存正确性留给 B/C。

### 1.2 078-B：独立 Checkpoint 与固定历史查询

交付 DB-083 的非泛型 EventCheckpoint/StateCheckpoint、ReadCheckpoint(address) 与
`EnumerateEvents(endInclusive, order = NewestFirst, afterExclusive = null)`，后者只返回事件地址。
ReadEvent 已在 A 交付，本阶段验证它的 Event 可达图读取范围，不额外物化 PreviousState；实际 Delta 依赖和 Open 校验成本另计。

ReadCheckpoint 首版推荐 eager，内部一次读取会话分别物化至多两图并全部成功后交付；
PreviousX 返回领域根及地址，不递归返回 Checkpoint。各图、各次独立读取、活动工作副本间不共享 mutable 实例；
同一根属性首次成功后重复访问保持同一实例。string 既有规则不变，不能借 ReadPair 的 mutable 共享实现默认 Checkpoint。
保留图内 alias/cycle/exact 类型和操作内 singleton 拒绝；库不保证关闭/fault 后继续导航，已经取得的根可作为普通对象保留。
先用小型内部接缝实现该合同，全面合并恢复循环留给 DB-079。

PreviousX 采用用户已选的最近严格祖先相应角色，不包含自身，也不只检查直接 Parent。
Event-first 前缀的 EventCheckpoint.PreviousState 与 PreviousStateAddress 同时为 null；
首个 State 若前面已有 Event，其 PreviousEvent 非空，只有严格祖先内没有 Event 才与地址同时为 null。
S0→E1→S1→S2 的 S1/S2 均指向 E1；该便利导航不表示因果、完成或本次保存区间的新事件。
StateCheckpoint.State 始终非空；没有 PreviousX 时只物化当前一张图。无 State、损坏与缺模型不能混为一种 null 结果。

枚举始终固定 end，后续推进/Move 不改所选历史；可选下界必须在首项前验证祖先关系，同点得到空范围，外链拒绝。
默认逆序允许提前停止，不先调用旧全链 ReadEvents 再包装 IEnumerable。正序可缓冲所需轻量地址，
不承诺首项 O(1) 或有界缓冲。每次 MoveNext 及 ReadEvent 各自检查可用性；yield 之间不持有 `_busy` 或底层 lease，
循环体因此可以 ReadEvent，两个推进之间也允许其他串行仓库操作。只遍历元数据，不物化途中 State。

本阶段最小验收：选定历史位置及 PreviousX/null；State/Event 互改隔离与稳定 getter；最多两图、不递归恢复；
跨 State 消息、逆序提前停止、正序顺序、物理交错分支、非祖先下界首项前失败、foreach 内 ReadEvent、
两次 MoveNext 间推进或移动 ref 仍固定 end、Dispose/fault 后推进拒绝；更新并实际运行真包历史查询消费者。
同期删除原全量 ReadEvents 公开入口与调用，保留明确不同用途的 ReadFrames 元数据检查能力。

## 2. 078-C：Fork API 与恢复行为

```csharp
public BranchCheckout Fork(string branchName, CheckpointAddress source);
```

方法只选本 repo 签发的已提交检查点，不读取源工作副本的未提交字段。正常返回时，新 ref 已持久化，
工作副本可继续 Commit，源分支未推进；不提供便捷但含混的 `Fork(liveState)` 或隐式提交。
保留 A 的 ref-only `CreateBranch(string, CheckpointAddress)`，二者分别服务 ref 操作与“创建并恢复”操作，不是兼容壳。
ref-only 操作不要求具备物化领域图的模型能力；Fork 有最近 State 时要求其完整恢复能力，纯 Event 前缀无需领域物化，
均不因历史包含 Event 而额外要求 Event 模型；State 自身图/恢复来源对 Event 类型的真实依赖仍须满足。
本片选的是 **named durable fork**，每次成功仍需要名字与持久 ref 发布；后续缓存不会消除该屏障。
无 ref 的短命探索不是本 API 的隐藏模式，也不由这个首版选择永久排除，待实际应用需要时另定发布合同。

| 起点 | 子工作副本 |
|---|---|
| State 地址 | 恢复该 State，Head 保持该位置；下一步可提交 Event 或 State |
| 已有 State 的 Event 地址 | 恢复最近 State，Head 保持所选 Event；下一步可提交 Event 或 State |
| 首 State 之前的 Event 地址 | State=null、Head 保持所选 Event；下一步可提交 Event 或显式非空 State，无参 State 保存拒绝 |

根按实际模型恢复，跨类型替换与无 State 合同沿 A。图内 alias/cycle、实际类型、ObjectId、逻辑 Parent 与完整保存基线保持。
不同 fork 的 mutable 对象独立；Transient 按恢复合同重建。Event 图仅在应用显式调用 ReadEvent/ReadCheckpoint 时恢复。
字符串沿用现有身份规则；不承诺跨 fork 的 ReferenceEquals。初版不使用 ImmutableLeaf 共享。
初始 `CreateBranch(initialState)` 仍保留调用者提供的实例；调用者主动把同一 mutable 实例交给多个工作副本，
其外部别名不由库消除。Allocate 的跨操作 freshness 属于 provider 合同，singleton 拒绝保持既有操作内检查，
不为检测任意违规 provider 增加跨工作副本活对象登记表。

**每 branch 单工作副本不提供外部效果 exactly-once。** 从同一 Event fork 两个 branch，两个孩子合法保留相同历史 Head；
外部动作成功后、State 发布前中止，应用重开后也可能再次处理历史消息。库不判定 PendingEvent，也不回滚 LLM 工具调用等外部效果，
主机负责业务调度与外部效果协议；不为此加入任务领取表或执行框架。

## 3. 将排他范围从 repo 缩小到 branch

不同 branch 可以同时编辑；同 branch 第二次 `Checkout` 在恢复前拒绝，MoveBranch 在其**目标 branch**仍有活动工作副本时拒绝。
源工作副本保持打开时，可以读取历史，也可以从已提交地址 Fork 到新名字；其他 branch 的活动工作副本不阻塞本次操作。
用户需要两个可分别保存的编辑候选时，显式创建两个 branch。只浏览或比较时使用 ReadState/Event/ReadPair。

```text
main 活动：可 Read(main)、Fork(child)，不可再次 Checkout(main) 或 Move(main)。
child 活动：与 main 各自串行提交，互不覆盖 workspace/身份游标。
Dispose(main)：可 Move(main) 或重新 Checkout(main)，child 不受影响。
```

本片不再提供合法的同 branch 竞争编辑、活动工作副本被 Move 变 stale、H→K→H 后旧工作副本复活等状态。
重复 `Checkout` 或 Move 活动目标属于使用错误，沿用 InvalidOperationException；Move 的 expectedHead 不符也保留现有失败语义。
不新增 GraphHeadConflictException、TryCommit、IsStale 或自动 rebase。

**占用集合不取代持久校验。** 每次 Commit 仍在任何 Capture/Schema 登记/Append 前检查 repo/工作副本所有权与存活、
资源可写、该 branch 的编辑占用、expected exact head、合法提交角色与最近 State 的 exact baseline；不重新加入交替检查。最终 Journal CAS、防重入、
publication/fault 全部保留。追加后 CAS 失败仍沿用 GraphCommitException/outcome/fault，不能因为有集合就删除防线。

### 3.1 活动分支名集合的最小生命周期

将当前源码 `_activeSession` 换为 repo 私有的 `HashSet<string>(StringComparer.Ordinal)`，不持有工作副本对象。
名字校验复用 Journal；不自己 casefold/normalize。实际依赖 pin 的分支索引也使用 ordinal，证据见专项评审。

- CreateBranch(initialState)、CreateBranchFromEvent、`Checkout`、Fork 在完整 `_busy` 临界区里取得本分支的私有占位，然后恢复/构造/发布。
  Add 及可能扩容必须在发布前完成；正常交付保留标记，任何未交付退出在 finally 清理**本次成功取得**的标记。
  用局部 acquired/delivered 状态即可，不建立公共 reservation/事务。
- Add 失败表示已占用；该次 finally 绝不能 Remove 别人的标记。普通恢复失败清理占位，健康 repo 可稍后再 `Checkout`。
  已发布后失败也清理未交付占位，但不删除持久 ref；仍按原 outcome/fault 决定重开。
- 工作副本 Dispose 幂等，释放自己的占用后才设置 `_disposed`。旧工作副本重复 Dispose 不得清掉后来新工作副本的标记。
  `_busy` 中拒绝 Dispose 时，不得把工作副本假装为已释放；不开放公共“按名字释放”接口。
- repo.Dispose 清空本 repo 的集合；随后旧工作副本 Dispose 仅作旧 owner 的内存清理，不访问已关闭 Store，也不影响重开实例。
  repo fault 仍阻止所有工作副本保存，Dispose 清理允许继续；集合不负责广播故障。
- 调用者必须显式 Dispose。遗失未 Dispose 的工作副本时，该 branch 在本次打开期间仍占用；不加终结器、WeakReference 或 GC 自动解锁。

活动集合是使用规则，不是持久 ref/head 的第二权威；不存 token/epoch，不追踪所有 CLR 图。

## 4. Fork 的完整临界区与失败行为

执行顺序：校验可写/名字/source → 私下占用新分支名 → 定位最近 State，有则完整恢复、无则准备空基线 → 构造 workspace/工作副本与交付所需内存
→ 创建新 ref → 无用户 callback 的最终交付。恢复逻辑与 `Checkout` 共用小型内部接缝即可，不提前做 DB-079 全部抽取。

**整个过程保持 `_busy`。** 当前 MutateRef 自己开关 `_busy`，不能直接从已经 guarded 的 Fork 套用后提前清掉 guard；
提取供外层 guard 调用的 mutation 核，既有 CreateBranch/MoveBranch 仍各自包住完整公开操作。
Allocate/Hydrate、故障注入回调均不能重入 Fork、`Checkout`、Commit、Move 或 Dispose。

| 失败位置 | 可观察要求 |
|---|---|
| 名字/source/type、Decode/Normalize/Allocate/Hydrate | 无新 ref、不交付半图；无需把普通恢复错误都包装为提交错误 |
| 发布尝试前、发布结果未知、已发布后 | 沿用 GraphCommitOutcome 的 NotPublished / Unknown / Published 与独立 IsFaulted |
| 已发布后未成功返回 | 重开后分支可能存在；不删 ref、截帧或自动重试，不宣称磁盘与返回值原子 |

首次 fork 本身只发布 ref，不追加新的 State/Journal 历史帧，不通过 Save/recapture 制造副本。
应用 callback 的外部副作用不回滚；已有 source 工作副本的未提交图不参与恢复与发布。

## 5. 078-C 施工单元与验收

G0 固化 fork 的朴素参照及每 branch 占用/故障测试；G1 将 repo 排他缩到 branch 并保留提交前置条件；
G2 共用 `Checkout` 恢复接缝、加入 guarded Fork/ref mutation 核；G3 完成使用示例、包消费者和独立语义复核。
G1/G2 由同一核心实现者顺序完成；subagent 可并行承担测试/消费者，主会话最终合并验收。

| 场景 | 完成标准 |
|---|---|
| 同点扇出 A/B，源仍打开 | 修改任一 mutable 图不影响另两者；串行 E/S 续写与冷重开值正确 |
| 历史 State / Event 起点 | 与 ref-only CreateBranch + `Checkout` 成功结果相同；Event 保持精确 Head，仅恢复最近 State，逻辑 Parent 正确 |
| 连续 Event、仅 State 模型 | 从 E2 Fork 不物化 Event；可立即提交 S，提交 E 仍需该 Event 根及实际图模型，冷开/后续保存正确 |
| 纯 Event 前缀、空 registry | Fork 不物化 Event，Head 精确、State=null；新分支持久存在，之后以具备模型的新打开实例提交首 State 正确 |
| 无 State 的两个 Fork，已有 State 的异型替根 | 各自建立首 State 或替换为异型 State 后无参续写，互不推进另一分支基线 |
| 未提交源字段变化 | fork 仍看到所选位置最近 State 的持久值 |
| 同 branch 第二次 `Checkout`、Move 活动目标 | 恢复/写入前拒绝；已有工作副本仍可保存，其他 branch 可正常操作 |
| Dispose 后重开编辑、Move 非活动目标 | 可继续操作；错误 expectedHead 仍在写入前拒绝 |
| 取得占位前失败、占位后恢复失败 | 不误删别人占用、不泄漏本次占用；健康 repo 可再次 `Checkout` |
| Dispose A → Checkout B → 再次 Dispose A | B 的占用保持，第三次 `Checkout` 仍拒绝；busy 中 Dispose 失败不解除占用 |
| 追加后最终 CAS 失败 | 仍是 GraphCommitException/outcome/fault；不因编辑占用省掉末尾防线 |
| alias/cycle、child-only 更新、断开循环岛 | 身份保持；首次/后续保存的 Delta、Remove 与完整来源正确 |
| 外 repo/重开旧地址、错误根、同名分支、只读 repo | 拒绝；已有 ref/文件不变 |
| 恢复后段失败与重入 | 无半工作副本、新 ref 或提前解除的 guard；singleton allocator 仍拒绝 |
| publication 三种 outcome | 按 outcome 与 IsFaulted 恢复；其他工作副本也遵守 repo fault |
| 工作副本/repo Dispose | 各自范围正确，任意过期工作副本不能绕过所属 repo 校验 |

原 [EventHistoryRepositoryTests](../../tests/DurableGraph.Persistence.Tests/EventHistoryRepositoryTests.cs)
中的全 repo 排他断言须改为每 branch 排他，保留合法角色/无写入检查；历史 fork/Move、升级重写、
[共享恢复测试](../../tests/DurableGraph.Persistence.Tests/SharedEventHistoryTests.cs) 均是回归基础。
A 已将旧 PendingEvent 测试中的可变隔离见证改为 Checkout 加显式 Event 读取；B 另验证独立 Checkpoint 的双图合同。
不为通过旧测试保留已删除的产品状态机。

## 6. 交付与停点

根 README 给出固定模型的 Open、保留源工作副本、从地址 fork 两个子图、独立提交/重开的短例；
在 [PackageConsumerProbe](../../experiments/PackageConsumerProbe/README.md) 增加或扩展一个强制 Family 的真实包见证。
该见证须实际编译完整 API：Open/Create（含 CreateBranchFromEvent）→ 从同一地址 Fork A/B → 各自 E/E/S/S Commit → 冷重开验证，
并展示源工作副本保持存活、子 branch 各自编辑，以及 Dispose 后可重新 `Checkout`；
Event 点示例明确库只恢复最近 State（无则 null），消息处理由应用进度决定，不暗示外部效果 exactly-once。
覆盖纯 Event 前缀的 Fork→显式首 State，以及至少一次跨类型根替换→无参保存；不能用同型示例代替异型根绑定验收。
按 [共通验收](0076-efficient-graph-fork-technical-path.md#10-分片施工导航) 完成构建/回归和相应包验证。
每阶段记录自己的实际完成范围和证据；A/B/C 全部完成才把本片标记完成并进入 DB-079。
完成即得到无需缓存的正确 fork；不以“后续缓存片会补上”为理由遗漏当前失败或身份语义。
