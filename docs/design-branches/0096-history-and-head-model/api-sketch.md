# DB-096：Branch、Commit 与 WorkingCopy 的 API 草图

> 2026-10-19 机制收敛版；讨论稿，未实施，未冻结 C# 签名或持久格式。同库统一 Catalog、请求无独立消费状态；描述查询和内容恢复分别承担明确的校验范围。依据见[统一 Catalog](shared-schema-only.md)、[组内核](group-kernel-design.md)及[Branch 纵切](branch-vertical-design.md)。调用故事见[五个调用故事](save-handle-stories.md)；词汇审阅与旧名映射见[命名审阅](api-naming-review.md)。当前源码术语仍见[项目术语表](../../DurableGraph-glossary.md)，不能把本稿当成已完成的实现。

## 1. 三个核心概念

**Repository 持有 Branch；每次业务保存产生 Commit；打开 Branch 的 WorkingCopy 后，可以显式提交新的 State 或 Event。**

| 概念 | 一句话定义 | 必须保留的区别 |
|---|---|---|
| **Branch／分支** | 有独立身份、显示标签和当前提交位置，可从旧提交分出并继续追加的持久入口 | DramaBoard 的世界线是应用层称呼；标签可重复，不承担身份；不是完整历史数组或内存对象图 |
| **Commit／提交记录** | 一次已接受的业务 State 或 Event 保存所产生的准确历史位置 | 不等于完整 State 快照、未完成的提交尝试或底层多 Head 事务；fork 和管理更新不产生业务 Commit |
| **WorkingCopy／工作副本** | 绑定一个 Branch、持有可编辑 State 和保存基线的内存工作对象 | 每次 Commit 独立发布；Dispose 只释放编辑占用，不提交、不撤销已经发布的记录 |

BranchId 对应紧凑 HeadId，CommitId 对应紧凑 HeadEntryId；推荐薄角色类型，持久编码仍分别为4B/12B，不额外附16B库身份。使用时由所选 Repository 校验成员和目标，解码本身不证明存在、健康或跨库全局唯一。codec 与 DG 标量适配尚待具体设计。

一条分支只有 State Commit 完全合法，只有 Event Commit 也合法。CommitState/CommitEvent 是动词，Commit 是它们产生的业务记录；一次保存伴随 Schema 更新，仍只产生一个业务 Commit。同值再次提交保留不同 CommitId。

## 2. 最小调用面

以下是 C# 风格伪签名。`Read/List` 返回描述，`Load` 物化业务对象，`OpenWorkingCopy` 获取编辑能力，`Create/Fork/Commit` 持久发布。此处约定的是 DG 调用职责，不把 Read/Load 宣称为整个 .NET 的统一标准。

```text
repo.ListBranches()                         → BranchInfo[]
repo.ListRootBranches()                     → BranchInfo[]
repo.ListBranchSubtree(branchId)             → BranchInfo[]
repo.ReadBranchInfo(branchId)                → BranchInfo
repo.ReadCurrentCommit(branchId)             → { BranchId, Commit: CommitInfo }
repo.ReadCommitInfo(commitId)                → CommitInfo
repo.ReadHistoryPage(start, limits)          → CommitHistoryPage

repo.CreateRootBranchFromState(displayLabel, initialState, summary)
                                             → BranchInfo
repo.CreateRootBranchFromEvent(displayLabel, initialEvent, summary)
                                             → BranchInfo
repo.ForkBranch(sourceCommitId, displayLabel) → BranchInfo

repo.LoadState(stateCommitId)                → State 对象（准确点必须是 State）
repo.LoadEvent(eventCommitId)                → Event 对象（准确点必须是 Event）
repo.LoadLatestSavedState(atOrBefore: commitId)
                                             → LoadedState
repo.OpenWorkingCopy(branchId)              → WorkingCopy
work.CommitState(state, summary)             → CommitInfo
work.CommitEvent(domainEvent, summary)       → CommitInfo

work.CreateStateCommitRequest(state, summary)       → CommitRequest
work.CreateEventCommitRequest(domainEvent, summary) → CommitRequest
repo.CommitBranches(requests)                      → BranchId 到 CommitInfo 的结果表
```

同一 Repository 只有统一的 Schema Catalog；创建、编辑和请求都不再选择 SchemaMode，也没有 ToShared 推广操作。Catalog 持续接纳不同类型和正常版本，同一个 SchemaKey 的已接受完整定义不可改形。历史图和 batch 仍固定准确解释来源；统一写入目录不等于用最新 Schema 读取全部历史。资源读写模式、模型/Upgrade 准入、fault 与重入检查继续独立成立。

两个创建入口分别说明首个真实记录是什么，避免 `initialRecord` 或相同 CLR 根类型的重载隐藏 State/Event 区别。普通 Fork 固定继承旧提交，之后 OpenWorkingCopy 取得写入该 Branch 的能力；从旧点继续也沿仓库当前统一 Catalog 准备新增定义，不重置 Schema 演进路线。

异步/取消/预算参数、实际泛型/根类型签名尚未定稿。本文是主要故事的设计面，不是现有所有 API 的迁移清单；Private/Schema promotion 已决定从新 API 删除，比较读取等其他未列能力不因省略而被决定删除。

## 3. 定位值、描述与加载对象分开

| 返回角色 | 推荐字段／职责 | 解释 |
|---|---|---|
| BranchInfo | Id、DisplayLabel、ForkedFromCommitId? | 一次读取的成员描述；根没有来源；来源是准确提交，不是任意指定的“父分支” |
| CommitInfo | Id、CreatedOnBranchId、Kind、Summary | 轻量描述；CreatedOnBranchId 是实际发布该提交的分支，可由位置归属派生，不另存第二份来源权威 |
| ReadCurrentCommit 的结果 | BranchId、Commit | 同时保留选中分支与准确当前提交；不是全库“当前分支” |
| LoadedState | SelectedCommitId、StateCommitId?、State? | 加载不晚于选点的最近已保存 State；两个可空字段同时有值或同时无值 |
| WorkingCopy | BranchId、BaseCommitId、StateSourceCommitId?、State? | BaseCommitId 是最后接受的逻辑末端；StateSourceCommitId 是当前 State 保存基线对应的提交，成功 CommitState 后更新，CommitEvent 与未提交内存编辑不改变它；Event-only 时可以尚无 State |
| CommitHistoryPage | Items、Continuation、StopReason | 物化的小描述页；Continuation 是继续查询边界，不保证下一页非空 |

从 A/a1 分出尚无新保存的 C 后，`ReadCurrentCommit(C)` 返回 `{BranchId=C, Commit.Id=a1, Commit.CreatedOnBranchId=A}`。打开 C 得到的 WorkingCopy 始终绑定 C。不能从提交实际归属反推出调用者应继续写哪条线。

`DisplayLabel` 不叫 Name，避免暗示唯一键；`ForkedFromCommitId` 不叫 ParentId，避免丢掉准确分叉点。目录按来源提交的 CreatedOnBranchId 建父子关系；相同来源可复用描述读取。由 B 的历史选中 A/a1 创建 C，C 挂在 A 下。

描述查询检查正式成员、HS 发布资格、小描述及图 envelope 的业务角色；不顺便完整恢复对象或逐项寻找最近 State。描述成功不保证全部内容/模型/附件可用。内容加载才执行相应准确 Schema、source/prior、对象和模型检查。首个纵切候选让 Kind 保留在图 envelope，Entry 仅存图入口和 summary；付出一次小帧读取，先避免重复 Kind 权威，成本待测。

### 对象加载不等于回退，也不等于重放

`LoadState` / `LoadEvent` 严格读取所选记录的对应内容；Kind 不匹配失败，不自动向前找。只加载 Event 不要求顺便恢复最近 State。`LoadLatestSavedState` 才沿选中提交的继承历史向前找最近 State；**不执行 Event**。

例如 `S0,E1,E2`，选择 E1 得到 `{SelectedCommitId=E1, StateCommitId=S0, State=S0的对象}`；选择 S0 则两个 Id 相同。只有完整查到根且确实没有 State 时才返回无 State；预算不足、损坏或解释失败不能变成 null，也不能跳过坏 State 找更旧的健康值。这个查询可能涉及长历史和内容重建，不承诺固定成本。

这里不再用“只读视图”描述加载结果：它们**没有绑定分支的提交能力**，但这不证明领域对象深度不可变。当前独立读取允许修改内存对象、初始化 Transient；新实现是否复用该合同、如何处理共享和资源寿命须明确，不能靠名字改掉。上述调用本身不发布新业务记录、不移动分支；持久化须通过显式 WorkingCopy 提交。

OpenWorkingCopy 恢复分支当前内容和编辑基线，不切换 Repository 的全局当前分支。若预览后分支又推进，随后打开得到当时的新基线；查看固定旧点用 Load，继续旧点用 ForkBranch。不要增加一个默默 rewind 的 `OpenWorkingCopy(branchId, oldCommitId)` 重载。CommitEvent 不隐含保存工作副本里的 State 编辑。

## 4. 五个调用故事

这些片段分别展示一个操作；`Encode/Decode` 代表定位值 codec，接口形式待定。summary 由应用显式提供或明确缺省，不自动继承另一条记录的摘要。

```text
// 继续上次分支：应用另外保存仓库位置，标签不参与定位。
branchId = DecodeBranchId(preferences.LastBranch)
current = repo.ReadCurrentCommit(branchId)
using work = repo.OpenWorkingCopy(branchId)
Advance(work.State)
committed = work.CommitState(work.State, summary)

// 固定书签：准确位置不随后续保存移动。
bookmark = EncodeCommitId(committed.Id)
info = repo.ReadCommitInfo(DecodeCommitId(bookmark))
loaded = repo.LoadLatestSavedState(atOrBefore: info.Id)
// 需要该点 Event 时显式 LoadEvent(info.Id)，不自动执行它。

// 历史选点：固定末端，后续页不重读今日 current。
tip = repo.ReadCurrentCommit(branchId).Commit.Id
page = repo.ReadHistoryPage(HistoryStart.AtOrBefore(tip), limits)
if page.Continuation is present:
    nextPage = repo.ReadHistoryPage(page.Continuation, limits)

// 旧点另开：成功即有持久身份和标签，不制造额外 Commit。
child = repo.ForkBranch(sourceCommitId: selectedCommitId,
                        displayLabel: "尝试另一种选择")
preferences.LastBranch = EncodeBranchId(child.Id)
// 即使此处关闭重开，child 仍可发现，当前提交仍是 selectedCommitId。
using childWork = repo.OpenWorkingCopy(child.Id)
childWork.CommitState(newState, newSummary)

// LLM：按应用保存的 phase 恢复流程，不替应用重放工具。
using run = repo.OpenWorkingCopy(runBranchId)
ResumeAccordingToApplicationPhase(run.State)
```

ForkBranch 与 OpenWorkingCopy 分开：创建成功、随后模型加载失败，不应描述成创建回滚。LLM 工具的幂等键、结果查询及“外部成功但结果尚未保存”的处理仍属应用，不因 Commit 这个名字得到跨系统事务。

## 5. 历史方向和成本边界

历史只按选定提交的**继承顺序从新到旧**读取，不按时间戳排序，不跳向兄弟分支。`HistoryStart.AtOrBefore(id)` 包含该点，`HistoryStart.Before(id)` 排除该点并向祖先继续；不再使用方向含混的 After。这是 `{CommitId, 包含/排除}` 的小边界值，不是跨进程物理扫描状态。

首版返回所有 State/Event 小描述，应用筛选展示；正常取得扫描进度后，Continuation 为 `HistoryStart.Before(lastProcessedCommitId)`，使用最后扫描项而非最后显示的 State。Before 在底层重读 inclusive anchor 并跳过首项，预算包含该工作。条数限制与工作量限制分别约束，ReadHistoryPage 不先算全历史总数。

**首版选择页满正常返回、工作预算耗尽整次失败。** `StopReason` 只区分抵达根和页满停止，不新增预算截断页；已在内部收集的前缀不交付。取消、损坏和清理错误照常失败。Continuation 不证明还有一项：满页末端恰是根时，下次可以返回空且完整。零进度失败不能伪造推进位置，也不保证相同预算重试能前进。[独立 HeadStore 见证](../../../experiments/BranchHeadMechanismProbe/README.md)验证了该组合与跨 fork 分页；DG 小描述物化及故障资格仍待接入。

### 根与子树查询

用户已同意纳入 `ListRootBranches()` 与 `ListBranchSubtree(branchId)`。前者返回没有 ForkedFromCommitId 的正式 Branch；后者包含指定 Branch 自身及沿固定分叉来源连接的全部后代，返回平面 BranchInfo 集合，不返回 Commit 历史或物化 State。空 fork 正常参与子树。无效/未登记的起点应失败，不能伪装成空树；缺失父节点或坏来源不能被展示成新根。

这些查询都以 DG 正式成员为范围。在登记候选 B 中，不登记 Shared Schema 等管理 Head，也不展示创建中断遗留的未登记 Head。HeadStore 保留区只约束编号分配，不能替代成员资格；仅过滤低编号仍可能暴露自动区的未登记产物。应用自己的角色/知识 Branch 仍是业务成员，是否出现在“选择世界”的界面由应用决定。

首版可完整读取小目录并建立内存来源索引，子树查询不保证只访问该子树的磁盘文件。列表不承诺时间排序；应用按需排序和展开。展开某组分支再读该组 current 摘要，选中附件才读 BLOB。不先造持久子树索引，也不以新增查询为由强制增强 HeadStore。来源查询能否限定已登记集合及其成本，留到接入时验证。

## 6. 创建协议与后续工作

仍推荐继续证明[DG 独立登记候选 B](application-design.md)：只有登记成员是正式 Branch；先准备内容/物理 Head，再发布登记，最后交付 BranchInfo。普通 fork 复用准确旧 Commit；新根创建首个真实 Commit，必要时将登记与 shared Schema 更新联合发布。登记只保存成员/标签等创建属性，普通高频提交无需重写目录。

该建议尚需证明 fork、新根无 Schema 增长、新根有 shared 增长三条路径的耐久顺序和失败结果。登记前可留下未登记物理 Head；登记后结果交付失败不能声称未创建或透明重试。1000条分支的目录容量须按实际 payload/标签上限核算；不先假定整表方案合格，不新增 GC。

旧目录上的真实 State/Event 混组发布、安装及冷续写已获[限定见证](group-kernel-design.md#8-真实混组发布与冷续写见证)，不再为旧布局扩展协调器。[来源 snapshot 接缝 A 已实施验收](history-source-seam.md#7-a-实施与验收)，尚未提供本稿新外观。下一步资格化新 Storage 并以[Branch 纵切](branch-vertical-design.md)证明 Parent 替换恢复/再保存，之后接入真正多业务 Head 组内核和登记故障验收。summary/BLOB、加载对象寿命、ID 字段支持与具体异常随后收敛。Private 创建、Schema 模式混组政策与请求消费状态已退出未决项；没有新增 rewind、merge、内容寻址或唯一名字需求。

## 7. 多 Branch 联合提交：已选 CommitRequest 路线

用户已认可轻量 `CommitRequest` 路线：WorkingCopy 构造请求，Repository 执行时统一捕获与共同提交。冻结内容的 PreparedCommit 不进入当前设计；具体职责、失败矩阵和对照理由见[联合提交设计](multi-branch-commits.md)。调用为：

```text
repo.CommitBranches([
    worldWork.CreateStateCommitRequest(worldState, worldSummary),
    playerWork.CreateStateCommitRequest(playerState, playerSummary)
]) → BranchId 到 CommitInfo 的结果表
```

另有 `CreateEventCommitRequest(domainEvent, summary)`。创建 request 不发布、不捕获业务图快照；在 CommitBranches 期间统一捕获、校验和准备。同一次调用只提交同一 Repository 实例中不同 Branch 的有效工作副本，每分支一个 State 或 Event。既有 CommitState/CommitEvent 仍表示立即提交，不能放入上述集合充当延迟请求。普通提交与联合提交应复用同一内核；此处没有公开新的跨库事务或任意 Head 写入入口。

请求不占资源、不实现独立消费状态。它绑定原活 WorkingCopy 实例及 expected Base；健康失败且基线未变时可再次执行并重新捕获当前图，摘要仍是创建请求时自有的值。成功（包括 Event）使旧请求过期；Unknown、发布后异常或 owner fault 阻止复用，关闭重开的新副本也不能接收旧请求。框架不自动重试；NotPublished 不等于资源健康。正常返回完整结果表，失败不保证全部新 CommitId 已可取得。
