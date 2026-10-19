# DB-096：应用合同与并行设计收敛稿

> 后续公开概念与命名以 [API 草图](api-sketch.md)为讨论入口：Branch 对应本文世界线，Commit 对应一次真实业务保存，WorkingCopy 是绑定分支的可编辑工作副本。BranchId/CommitId 分别对应底层 HeadId/HeadEntryId；本文的 Head、Entry 和 HS 方法名保留底层含义。本文保留创建、发布及成本分析，不单独维护另一套公开签名。

> 状态：2026-10-17，三个有界调查包经主线程整合的设计稿，未实施。来源故事和已确认需求只维护在[用户故事](user-stories.md)；本文负责候选合同、取舍与最小验证。
> 本轮分析期间上游完成引用初始化并提交 `474dbe4ddfc12898a894c157f72ac5a44a7bcc80`。主线程只读核对新源码与[正式历史/fork 合同](../../../../atelia-storage/docs/HeadStore/05-versionstore-names-and-indexes.md)，临时方案 17 已删除；DG 固定依赖与运行验收尚未更新，不沿用上游资格作为 DG 接入证据。

> 后继交付：两类 codec 与 `ReadEntryHistory` 已交付并通过独立消费者复核；用户确认紧凑 Handle 的开发期误用诊断目标，[补充实验](handle-misuse-detection.md)支持不为每个引用增加完整 scope。[复核记录](headstore-delivery-review.md)维护准确来源，本片无待上游补强身份的阻塞。

> 后继机制调查（2026-10-18）：[源码映射与最小实验](mechanism-feasibility.md)进一步区分登记两层 outcome、最近 State 内容来源、全量 origin 访问范围与目录累计写量；以下候选须连同该证据理解，仍未实施。

> 2026-10-19：按用户决定改为[同库统一 Catalog](shared-schema-only.md)，开发草稿在独立库副本中隔离；Private/ToShared 已在[独立删除切片](private-removal.md)退出源码。本文其余调用随 API 草图同步，仍为待实施设计。

## 1. 本轮边界与结论

用户在本轮明确：首版原世界线只追加；从旧存档继续通过另开世界线实现，不要求原线 rewind。fork 成功后，即使没有新业务保存，也必须能列出、预览、关闭重开和继续。父子线均可独立推进；来源按选中存档的实际归属确定。

据此，**首选继续验证“一世界线一 Head，继承 head-history 表达领域历史”**。前提是该 Head 只接收真实 State/Event 提交，标签等管理信息放在链外；不另存一份重复的逻辑 Parent。显式父链作为需求变化时的竞争方案保留，不宣称已经完成方案或代码验收。

另一个确定的信息边界是：**世界线标签与当前存档摘要必须具有独立归属。** 新 C 可以与 A 共用 a1，但 C 需要自己的标签。创建属性当前推荐登记候选 B，仍待机制验证；为标签制造一次业务 Commit 会重新带回历史过滤问题。

用户补充的现实目标是玩具交互游戏：每库预计不超过 1000 条世界线，先选根再展开，优先近期路线并通常继续上次进度；每线存档可能很多，例如每分钟保存。durable LLM Agent tool-loop 是重要应用，RL Gym 则是远期性能方向，暂不据此设吞吐指标。不注册业务模型浏览不是硬要求，先顺应实现能力，不另建浏览打开协议。

## 2. 应用操作草图

以下沿用 API 草图的职责与伪签名，不是已有或已冻结 API。BranchId 是写入目标，CommitId 是准确存档；根创建示例使用 State，另有 CreateRootBranchFromEvent。同库只使用统一 Catalog，无 SchemaMode 参数。

```text
A = repo.CreateRootBranchFromState("废灵根", initialState, summary0)
using workA = repo.OpenWorkingCopy(A.Id)
a1 = workA.CommitState(state1, summary1)
a2 = workA.CommitState(state2, summary2)

从 A 的历史列表选中 a1
C = repo.ForkBranch(sourceCommitId: a1.Id, displayLabel: "捡到小绿瓶")
repo.ListBranches() → 已包含 C
repo.ReadBranchInfo(C.Id) → C 的标签与准确来源
repo.ReadCurrentCommit(C.Id) → {BranchId=C.Id, Commit=a1}

关闭全部资源后重开
using workC = repo.OpenWorkingCopy(C.Id)
c1 = workC.CommitState(changedState, newSummary)
重新打开 A 的 WorkingCopy 并提交 → 不改变 C 的末端或固定来源
```

身份的持久读回编码另行设计；此处“按 C 的身份”不要求沿用旧 owner 的句柄，也不依赖标签唯一。从没有新保存的 C 当前状态再 fork D，D 仍按 a1 的归属挂在 A 下。

草图把持久 Create/Fork 与 OpenWorkingCopy 恢复分开：前者成功交付正式世界线身份，后者要求相应模型能力并可能失败。“重开可继续”表示资源与模型具备时能够恢复，不保证缺少模型仍能构造业务对象。若最终保留一次调用就返回 WorkingCopy 的便利入口，须单独定义恢复准备与持久创建顺序，不能把创建已成功后的恢复错误说成创建已回滚。

| 操作 | 成功后的可观察结果 | 不隐含什么 |
|---|---|---|
| 创建根 | 独立世界线、标签、首个真实 State 或 Event 存档可发现 | 不要求领域内容已有完整游戏世界；Event-first 合法 |
| 保存 State | 该线新增一个存档，图入口和摘要共同成为新末端 | 无需先提交 Event；同值重复保存不自动去重 |
| 保存 Event | 该线新增一个 Event 存档，独立于 State 保存 | 不执行事件、不恢复新 State、不自动 replay |
| 从旧存档 fork | 新身份和标签已正式可见，当前存档仍是准确来源 | 不生成新的领域存档，不移动父线 |
| 查询历史 | 返回选定历史内准确身份、Kind 和小描述 | 不等于恢复领域对象，不遍历兄弟线的新后缀 |
| 继续 | 为选定世界线恢复可编辑工作副本 | 不能用当前存档的 owner 代替目标世界线写入 |

## 3. 领域历史的最小模型

### 3.1 首选候选的成立条件

业务 Head 的每次已接受 Entry 对应一次 State/Event 提交。它的本地已接受顺序加上固定来源前缀即领域顺序；不包含 Schema 基线、标签更新、登记或书签等管理发布。

在 `a0 → a1 → a2` 中选择 a1 创建 C，C 的第一次保存 c1 自然接续 a1；A 后来新增 a3 不进入 C 的历史。上游引用初始化避免了额外克隆位置。框架仍要检查业务 Entry 描述的格式和种类，不能把任意底层 payload 当合法存档。

采用该候选时，准确历史位置须能定位真实 Entry；图的 FrameAddress 仅定位图内容，不能继续单独承担“由此查回领域前史”的合同。公开 CommitId 推荐采用 HeadEntryId 的薄角色类型，但不为兼容旧 `CheckpointAddress` 形状建立第二套逻辑父链。

| 路线 | 首版收益 | 成本或重新讨论的触发条件 |
|---|---|---|
| 继承 head-history | 单一顺序权威，不额外编码/维护 Parent；准确来源与分支归属一致 | 依赖业务 Head 的用途约束；准确旧起点与有界读取 API 要核对 |
| 显式 ParentEntryId | 逻辑接续可独立于物理发布顺序 | 当前只追加故事下是可推导的重复关系；引入持久 ID、父链读取和校验；允许原线 rewind/管理发布混入时再比较收益 |

未来原线 rewind 并不必然选择显式父链；还可研究换内部 Head，但那会重新引入稳定入口映射和来源解释。此轮不预建这些机制。

### 3.2 State/Event 与重建依赖

保留 `S,S`、`E,E`、Event-first 和实际 State 类型替换的使用能力，不强制交替。`S0,E1,E2,S1` 是四个真实历史位置；从 E2 继续时最近持久 State 是 S0，事件处理进度由应用解释。

至少固定两个恢复见证：`S0,E1,E2` 从 E1 fork 后，逻辑末端仍是 E1、State 恢复 S0；`E0,E1` 从 E1 fork 后，末端仍是 E1、State 为“尚无”。两者都不自动重放 Event。找不到 State 只有在完整走到根且确无 State 时成立；预算耗尽、坏 State 或读取失败不能降格为“没有 State”，也不回退到更旧的健康 State。

删除逻辑 Parent 不意味着删除对象 Delta prior 或内容来源。源码复核确认，`GraphRecordStore.ResolveContentSource` 对 State 和 Event 都沿 Parent 寻找最近 State，Meta 则取前 Meta；此前“同类内容来源”的表述不准确。重写必须交接这些准确来源及其关系校验，不能只删除字段。业务时间线和对象重建依赖分别承担职责；直接 State 锚点与历史推导的取舍及错源反例见[机制调查 §7](mechanism-feasibility.md#7-恢复先纠正文档再验证删除-parent-的闭包)。

Event 摘要的最小规则：调用方显式提供或缺省为空；不能默默把最近 State 的摘要标成 Event 的新状态。应用若要展示最近 State，可返回一个明确标识其来源的单独视图。缺省值与 bytes 所有权已由[API B2 合同](api-sketch.md#b2-的具体值与迁移合同)收窄，不增加业务自动推导。

### 3.3 准确起点的上游能力与 DG 接线

此缺口现已由 HS `ReadEntryHistory(startInclusive, ...)` 填补；原有 `ReadHeadHistory` 仍从 Head 调用时的固定上界开始。DG 可用准确 Entry 起点衔接固定末端与分页，实际资格与预算边界见[交付复核](headstore-delivery-review.md)。

从 current 向前扫描并跳过目标起点之后的部分可得到正确结果，但读取量随后来提交增长；新建临时 fork 来获得旧起点也不适合作为只读查询。[五个调用故事](save-handle-stories.md)已将准确起点、分页边界和两类 ID 持久化共同收敛成[新上游请求](headstore-save-handles-request.md)，DG 对外采用 HistoryStart.AtOrBefore/Before 和 CommitHistoryPage.Continuation；不预建通用物理 cursor，也不为已补齐的上游缺口引入显式父链。

## 4. 三类信息与标签承载

| 信息 | 权威与更新 | 浏览所需 |
|---|---|---|
| 世界线身份、固定来源、标签 | 来源在创建时固定；标签归世界线，首版允许固定 | 枚举成员与小描述，不恢复世界 State |
| 存档图入口、准确 Schema 绑定、摘要 | 每个真实 Entry 的存档描述；保存时共同发布 | 读准确 Entry 并解轻量外层；当前摘要从所选 current 派生 |
| 缩略图 BLOB | 同仓库 FrameStore 中不可变内容，存档描述保存引用 | 选中时读取，未选择的图片不预读 |

具体标签编码、摘要格式和图片格式仍由各自合同决定。框架验证引用和字节关系，不判断“等级58”是否忠实于 State。业务图已经持久携带的 Schema 绑定不必在外层再复制一份权威；外层应能定位准确图及其解释上下文。

### 4.1 两个完整候选

**A：Head 创建时附带小型不可变属性。** 由上游在 root/fork 正式创建时一同发布 DG 类型标识和标签。C 无本地 Entry 也可完整显示；业务保存只更新业务 Head。它与用户允许固定标签的边界相符，但上游本轮引用初始化未增加这项能力，需要独立请求，不能作为已有保证。枚举是否逐文件读 header、属性尺寸如何限制均须定义。

**B：DG 分支登记（当前推荐）。** 由一个独立管理记录集合保存分支成员与 DisplayLabel（底层对应 `HeadId → 标签`），只有已登记 Head 是 DG 正式世界线。先创建底层 Head，再完成登记，最后才报告创建成功。目录无须存重复的 current 或摘要；它是正式成员资格的权威，不是名字唯一索引或可随意丢弃的缓存。

B 的中断结果可能是未登记底层 Head。DG 不展示它，不自动删除、不把它重新认领为另一次创建，也不通过扫描全部物理 Head 猜测正式成员。已登记者必须指向此前完成可靠创建的 Head。登记发布失败与底层创建失败需要分别映射，不把“没有用户可见线”说成磁盘完全未变。删除/GC 不纳入首片。

DG fork 只接受正式世界线上的已接受业务存档作为来源，不能把未登记 Head 或内部管理 Entry 提升为世界线来源。已登记子线的来源世界线也应正式存在；查询遇到缺失或尚未读到的父节点，不能自动把子线当作新根。这是成员与选点资格，不要求开库先审计全部历史。

B 可先用小型直接编码，避免为标签依赖完整领域模型；但若采用单 payload 整表，其容量和每次写入成本都有边界。是否需要增量目录取决于规模。另建每线描述 Head 再配目录会增加一层协议，当前先不展开。

初轮保留 A/B 未选；后续[API 草图](api-sketch.md)推荐继续证明 B，理由是登记还可承担新根与 shared Schema 的共同可见点，尚未冻结实施。A 需要上游新增能力；B 可在既有原语上独立设计。两者都须证明 root 创建及历史 fork 的立即可见合同，不能仅展示成功路径。

## 5. 发布职责与失败结果

### 5.1 可替换的布局与需要重新证明的能力

当前实现把完整分支字典发布到 Head 1，shared Schema 有更新时与 Head 2 联合发布；该布局可重写。应保留或由新协议等价保证：准确 Schema 解释、必要数据先持久化、发布后安装内存基线、完成帧不回写、owner/fault 与资源寿命约束。

候选普通提交流程（WorkingCopy.CommitState/CommitEvent）：

```text
准备业务图 / 精确 Schema / 小摘要 / 附件
    → 追加必要内容
    → 发布选定世界线的新 Entry
       （若该操作还推进 shared Schema，则联合发布相应管理 Head）
    → 安装内存保存基线并交付结果
```

每个历史存档必须固定准确 Schema，而不能在读历史时取最新 shared Schema。完整闭包先持久化足以解释历史；“shared 指针必须和业务提交一起推进”则是另一项行为选择。首轮建议沿用 shared 增长与业务保存联合发布的行为，不在重写中顺便改成“业务失败但 shared 仍增长”。不增长时无需为此制造另一次业务 Entry。

所有后续保存向同一 Catalog 接纳定义，业务 fork 不另开 Schema 路线。各历史图仍使用自身准确 Schema，旧对象 Base 按其 batch 的来源解释；同库同 key 定义冲突拒绝，正常新版本可以共存。本轮不扩展多 writer、跨库事务或将不同开发库的原始存储记录合并。

### 5.2 创建与常规提交分开设计

HS 的联合发布只覆盖既有 Heads，不能直接当作新 Head 创建事务。A 候选需上游的创建属性与完整正式发布；B 候选以最终登记为 DG 可见提交点。

根创建除标签外还涉及首个真实存档和可能的 shared Schema 增长；普通 fork 复用准确旧存档。这两个路径不能套用一张“只写 header”图。B 的根创建可探索：先准备内容和业务 Head，再将登记与必要 shared 基线共同发布；A 若仍要求 shared 同步推进，则需要单独证明创建协议，不能声称一次带属性的 CreateHead 已经覆盖 shared Head 更新。这是架构候选的主要比较题之一。

| 失败窗口 | 设计必须交代的结果 |
|---|---|
| 摘要超限、模型/Schema 冲突等准备失败 | 可见末端不变；尽可能在追加前拒绝 |
| 内容追加后、发布前失败 | 老末端仍可见；允许完整但未发布引用的帧，不回滚它们 |
| 业务发布或 shared 联合发布中断 | 重开按底座发布资格判断；不出现新摘要配旧业务入口 |
| 已发布、内存安装或结果交付失败 | 不宣称回滚；不继续使用已 fault 的旧 owner；重开检查准确持久结果 |
| B 的 Head 已建、登记未成立 | 不出现残缺 DG 世界线；允许底层未登记产物 |
| 空 C 重开再保存 | 初始为源 a1，第一次新保存写入 C；A 不被误推进 |
| 图片读取失败 | 精确报告所选附件错误；不伪造别的存档图片；文字浏览是否可继续单独定义 |

这里的“老末端可见”是持久结果描述，不等于发生 I/O 故障后旧 owner 仍可继续操作。准确 Outcome、错误类型与是否允许重试留给实施合同；不默认透明重试。

普通提交与多分支 CommitBranches 应共用发布内核；已选 CommitRequest 在调用执行时统一捕获，不能把这里的“准备”理解成公开冻结的 PreparedCommit。联合提交的消费与失败合同见[联合提交设计](multi-branch-commits.md)。

## 6. 查询层次与成本边界

| 查询 | 最小读取范围 | 待定的规模合同 |
|---|---|---|
| ListBranches / ListRootBranches / ListBranchSubtree | 正式 BranchInfo、ForkedFromCommitId、DisplayLabel | 子树包含起点；可先完整扫描小目录建内存索引，无需逐线加载 State |
| ReadCurrentCommit / ReadCommitInfo | 当前或准确 CommitInfo 和 Summary | 当前查询保留 BranchId；Commit 的实际归属由 CreatedOnBranchId 表达 |
| ReadHistoryPage | 固定 CommitId 起点，按继承顺序从新到旧的 CommitInfo | 上游准确起点已交付；DG 分页预算与失败合同待细化 |
| 缩略图 | 明确选中的 BLOB | 大小上限、缺图与读取错误显示策略 |
| LoadState / LoadEvent / LoadLatestSavedState；OpenWorkingCopy | 准确 Schema、业务对象和必要重建依赖 | 加载与获取编辑能力分开；都需相应模型，Load 不授予分支提交能力 |

现有 DG 已允许空/部分 registry 做检查；`ReadFrames` 也不物化领域对象，但它全链读取并分配结果。新设计不能把“无业务反序列化”等同“固定小开销”。当前 Open 还会读取固定控制 Heads 和 shared Meta 元帧；若将来要求无关 Schema 损坏不影响目录浏览，是另一个容错合同，不能仅靠去掉 models 参数得到。

查询必须区分完整结果、主动停止和错误。截断的来源集合不证明根集合完整；历史尚未遍完不证明没有旧 State。DG 历史页已选 Continuation 边界值、页满成功及预算耗尽整次失败，见[API 草图](api-sketch.md#5-历史方向和成本边界)；不能用普通空集合隐藏预算失败，目录查询不因此自动获得分页。

成本轴为世界线数 N、每线存档数 H、分叉深度 D、小描述字节数 M、State 大小 S、图片大小 B 与冷/热缓存。N≤1000 的现实目标允许先评估一次成员/来源/标签扫描与内存树索引；不因此立即设计持久增量索引。若 B 采用单 payload 整表，还需按标签字节上限计算容量，不能仅凭“1000条”断言一定装得下。

长历史 H 才是本片必须正面处理的增长轴：目录浏览和当前预览不遍历全部存档；保存与从已选准确存档 fork 的历史管理部分不扫描 H；最近 K 项应可提前停止，不先计算历史总数。对象图恢复、Delta 链、Schema 与底层 CU 资格检查的成本单独记录，不能将上述目标说成整个操作 O(1)。

建议性能见证为：冷开 1000 条线目录、选定根的首屏摘要、固定旧起点的最近 20 项，以及每分钟保存的长期空间/缓存增长。历史长度可用 100、10000、100000 项作实验梯度，20 项仅为观察窗口，不是产品常量。固定起点读取不应随该点之后的新提交增加额外前缀扫描；底层真实工作预算与交付条数分开计。LLM tool-loop 可另加更高频保存样本，RL 暂不设验收门槛。

“最近更新”与“继续上次进度”分开：前者可按最新真实存档时间作展示参考（不作为严格发布排序证明）；后者是应用记录的上次选中/继续身份。空 fork C 的 current 是旧 a1，a1 的时间不是 C 的创建时间。创建后 UI 可直接聚焦返回的 C；若另需创建时间排序，应使用独立创建事实，不为点击、恢复焦点或排序制造领域 Entry。

## 7. 可独立推进、待上游与待用户

| 类别 | 下一项可验收产物 |
|---|---|
| DG 可独立推进 | 按[B2 施工计划](branch-cutover-plan.md)落地真实 Branch/Commit 恢复与保存；不再重做标签 A/B 选型，当前顺序只查[工作集](PROJECT-STATE.md) |
| 接入与待协商接口 | 引用初始化、同库 codec 与准确历史已完成 B1 源码接入；新布局尚待验证，正式描述查询仍需[定向 origin](headstore-origin-request.md)，不要求上游新增标签存储 |
| 已有量级、待测成本 | 世界线≤1000、长历史与每分钟保存；优先选根/近期/继续；无模型浏览不设硬门槛。目录字节容量已有[候选见证](../../../experiments/BranchWireFormatProbe/README.md)，延迟、实际读写与长期空间仍待测 |
| 后续产品取舍 | 附件缺失策略；Event 摘要缺省为空已选。若无明确增强要求，先不做自动补图或旧摘要修订 |

原 R1 已被[新请求](headstore-save-handles-request.md)接替：底层 HeadId 对应继续分支的 BranchId，HeadEntryId 对应固定提交的 CommitId，准确历史起点支持分页和恢复。两个能力可独立实施，不再只按显式 Parent 的字段存取来判断价值。

首片不实施 rewind、merge、唯一显示名查询、任意字段索引、GC/删除、跨库地址交换、并发 writer 或可改历史摘要。可改标签是用户偏好而非必需；冻结标签首版可接受。RL Gym 不转化为首片批量分叉、并发仿真或吞吐优化工单。

## 8. 证据与本轮验收

三个只读调查包分别覆盖应用历史合同、元数据与查询、发布职责；主线程复核以下关键源码，并整合跨包分歧。独立反向审阅只检查设计证据和遗漏，不替代运行验收。

- [Repository](../../../src/DurableGraph.Persistence/Repository.cs)：空 registry 检查、`ReadFrames` 全链与 `EnumerateEvents(endInclusive)` 固定末端。
- [BranchCheckout](../../../src/DurableGraph.Persistence/BranchCheckout.cs)：State/Event 独立提交与故障边界。
- [RepositoryResources.MetaBootstrap](../../../src/DurableGraph.Persistence/RepositoryResources.MetaBootstrap.cs)：当前控制目录、共享发布与安装顺序。
- [GraphRecordStore](../../../src/DurableGraph.Persistence/GraphRecordStore.cs)、[BranchSaveSession](../../../src/DurableGraph.Persistence/BranchSaveSession.cs)、[DecodedRevision](../../../src/DurableGraph.Persistence/DecodedRevision.cs)：内容来源、Schema 准备和历史解释。
- [上游历史 API](../../../../atelia-storage/src/HeadStore/HeadStore.History.cs)、[正式历史/fork 合同](../../../../atelia-storage/docs/HeadStore/05-versionstore-names-and-indexes.md)、[新增验收记录](../../../../atelia-storage/docs/HeadStore/12-versionstore-final-acceptance.md)：上游完成事实与 DG 尚未接入分开看。

本轮只交付分析和设计文档；未修改运行时代码、未执行性能实验，未把候选标为已可实施。
