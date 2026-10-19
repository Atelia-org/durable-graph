# DB-096：真实 Branch 历史的一次切换计划

> 2026-10-19，施工收窄稿，**布局与公开入口已在隔离分支完成首段运行；根 solution 调用闭包已迁移，正式合入尚未完成，见 §9**。以 DG `2d118da` 的来源 snapshot 接缝为起点；新 Storage 的独立接入资格以[工作集](PROJECT-STATE.md)当期记录为准。本文负责 B 片的接线顺序、调用面迁移和合入边界；需求及来源正确性仍由[Branch 纵切](branch-vertical-design.md)与[来源接缝](history-source-seam.md)定义。

## 1. 收窄结果

**最小可合入结果不是一个新的内部读取函数，而是一个只支持新布局的 Repository：每条 Branch 对应真实 Head，准确 Commit 对应 Entry，业务内容资格化与恢复先按 Entry 的继承历史选择 source。** 描述查询与 fork 不因此扫描前史。创建、打开、保存、旧点分叉、关闭重开和现存历史读取必须同时可用。内部测试宿主可以先打通接缝，但不能作为保留旧公开 address-only 入口的理由。

推荐分两次可独立验收的变更：

1. **B1：精确新 Storage 依赖与不兼容调用适配。**保持旧 DG 布局，根构建和相关现有回归通过；不据此声称新 Branch 已实现。
2. **B2：在隔离实现分支完成新布局与必要调用迁移，再整体合入。**其中可保留可审阅的工作提交；主线不加入运行时布局开关、旧格式读取或第二套 Repository/GraphRecordStore。先写真实纵切测试，随后接齐现存调用；最后按 §7 验收。

隔离分支只是容纳暂时不能通过全体调用的工作，并不能减少协议依赖。B2 不顺带实现多成员 CommitRequest 协调器、BLOB 管理、持久索引或性能优化；一成员保存仍须使用真实业务 Head，不能继续更新旧 name→graph 目录。

不改变公开面和现行布局的前置内部接缝可独立验收；[D/T 读取及候选原语](directory-persistence.md#7-目录候选与小入口切片)已属此类交付。其合入不代表 B2 的新 Repository 合入门槛已经满足；下一片直接将这些原语接入隔离分支的真实 owner/业务布局，不继续扩展独立目录 workspace 或发布协调器。

## 2. 最少协议选择

以下是建议落实的具体形状。**D/T 改为复用现有 Dictionary 增量序列化，撤回专用整表 codec 作为施工选择。** 管理图的具体形状、角色验证、初始化与发布接线只在[目录持久化合同](directory-persistence.md)维护。原[独立 codec 见证](../../../experiments/BranchWireFormatProbe/README.md)的 D/T 容量结果仅作旧方案对照，Entry 小描述与 ID/摘要结论保留；它们均不证明新 Repository 已运行。

| 载体 | 推荐内容与变更 | 原因和限度 |
|---|---|---|
| D，保留控制 Head 1 | `P0R3`＋可选 Meta 图入口；图根为 `Dictionary<uint,string>`，key 取 HeadId 稳定编码 | 正式成员唯一、标签可重复；复用容器 Base/Delta，不保存 current/summary/origin。删除旧 `branch/` 字符串到图地址映射 |
| S，保留控制 Head 2 | 继续保存当前 SchemaCatalog Meta 图入口 | `P0S1` 可保持；历史业务图仍各自 pin Schema，S 有限员工 reader 不因目录而放宽 |
| T，保留控制 Head 3 | `P0T2`＋可选 Meta 图入口；图根为 `Dictionary<string,byte[]>`，值用上游 codec 的12B CommitId | 保 Ordinal 不可变书签；不再用 `NamedFrameMapCodec` 存整表；不为内部值扩大公共 ID durable 字段支持 |
| 业务 Head Entry | `P0E1`、准确 graph FrameAddress、长度前缀的 opaque summary bytes | Kind 仍取图 envelope；不重复 Schema、owner 或 Parent；创建与每次提交时复制摘要，空摘要合法 |
| Graph envelope | `P0G2`；Kind 后**仅 Meta** 编码 `PreviousMeta`，业务图没有该字段；其他根/目录/Schema/batch 字段保持 | 一个内部 envelope 和 codec 的 Kind 分支足够；D/S/T 各以前一版本的自身管理图为来源，不能串用 |
| batch / object directory | 保现有格式、SourceGraph、准确 prior 及内容校验 | 物理增量来源不是待删除的逻辑 Parent；不能为字段改名一并去掉 |

D/T 的 Head payload 只含4B标记、1B存在标记及有值时12B地址，恰好5或17B；空入口只表示初始空目录，缺失/损坏必需 Head 仍失败。首次新增项写真实 Dictionary Base；读取必要控制入口后才建立图 Store/cache，沿用现有初始化边界。目录读写走窄管理入口和固定 builtin environment，不需要应用 Catalog，也不是新业务 Branch 或第二 Schema 策略。

标签为1..1024个 UTF-16 code unit 的有效 Unicode，首版创建后固定，文本可重复；不用现有小写路径式 `ValidateBranchName`，不 trim/normalize。书签名称继续保持 Ordinal 与不可重绑。目录键值、根布局、比较器和所有行闭包的验证见[专题 §3](directory-persistence.md#3-管理图读取与来源)，不以“能解成一个字典”代替角色校验。

summary 第一片用自有 bytes，不指定 JSON、字符串或另建摘要 Schema。输入可取 `ReadOnlyMemory<byte>`，内部取得自有副本；读取返回值不能暴露会影响缓存的可变数组。唯一保留的小描述字节约定为：

```text
Entry = "P0E1" + FrameAddress[12] + summaryByteLength[UInt32LE] + summaryBytes
```

Entry 固定开销20B，整个 payload 不超过当前 HS `MaxHeadEntryPayloadLength`，故摘要最多1,048,556B；拒绝截断与尾随字节。标签、摘要与可提前发现的编码错误在追加/物理创建前拒绝。缩略图地址可由应用写入摘要，附件协议后置；字符串便利重载若以后加入，只是明确编码的外观。

容量裁决改为：**1 MiB 不再约束 D/T 的全部目录文本**，但单帧、batch、对象及内存预算限制继续适用。1000条线可以先完整恢复到内存；增量写盘不承诺增量捕获、无全表扫描或磁盘单点查询。按现有容器策略选择 Base/Delta，不新写分片目录或索引；旧 codec 的200 KB/776 KB计算不再是产品容量上限。

旧 D/图/T 由新 codec 明确拒绝，不迁移、不自动重写完整帧。只有实际改变的格式升级；无需为了版本对齐修改 batch、directory 或 S。Open 先完成正常 FS/HS 恢复并读取必需控制根，再建立图 Store/cache；不为了新版本做全库图或全部 Head 预打开审计。

## 3. 来源接线：Repository 决定历史，图层校验内容

### 3.1 Repository 的两个读取步骤

第一步 `ReadCommitDescriptor`（内部名称可调整）：由 CommitId 取得 checked HS Entry；确认其 owner 是 D 正式成员，再解 Entry 小描述与图 envelope 的 State/Event 角色。当前值读取还须同时保留被查询的 BranchId：空 C 继承 A/a1，不意味着编辑目标变成 A。历史 visitor 交付的每项也执行成员/描述检查，不容许管理 Head 或未登记来源混入业务历史。

第二步按操作边界读取历史：准确图 X 的 source 是 `Before(X)` 最近 State；加载最近 State 从 `AtOrBefore(X)` 选点；为新保存 Y 准备 source 则从已接受 tip X 的 `AtOrBefore(X)` 取得。冷 OpenWorkingCopy 需要最近 State 及其严格前源，不足两个必须查到根。保持[纵切 §4](branch-vertical-design.md#4-删除业务-parent-后如何保留恢复证明)中的 tip Event 轻量检查、Event-only 到根及预算失败规则。

HS visitor 只收集准确 Entry/图地址、Kind 和足够的最近 State 信息；退出 visitor 后再验证或物化内容，禁止递归进入 HS history。一次内部历史选择函数供上述入口复用即可，不要引入 resolver 接口、Graph→Commit 表或全库索引。预算不足仍失败；不可用有限前缀证明 null。沿途普通 Event 只读取小描述和 envelope，不变成全历史内容审计。

**停止条件由调用目的决定，不能统一成“找到两个 State”。** `source(X)` 表示 X 严格之前的最近 State（只有到根才可证明为空）；它是图层资格化的输入，不要求恢复 source 的领域对象。

| 入口 | 必须取得的材料与停止条件 |
|---|---|
| `ReadCommitInfo(X)` / 描述枚举 | X 的正式成员、Entry 小描述与业务 envelope；不寻找 `source(X)` |
| `LoadState(X)` / `LoadEvent(X)` | 检查 X 的准确 Kind，再从 `Before(X)` 找 `source(X)` 或到根；只物化 X |
| `LoadLatestSavedState(X)` / `OpenWorkingCopy(B)` | 从选点找最近 State S，再找 `source(S)`；不足两个 State 到根。Open 另保当前 tip Event 的轻量关系检查；Event-only 完整到根才交付无 State |
| 新提交 Y，已接受 tip X | `source(Y)` 为 X 及其之前最近 State；不能把已恢复 S 自己的 source 当成 Y 的 source |
| `ReadCheckpoint(X)` | 先找最近严格相反角色 O 或到根；分别取得 `source(X)` 与 `source(O)`，然后才恢复两个独立对象图。O 不存在仍须资格化 X |
| `ReadPair(X,Y)` | X、Y 分别取得自己的 source；允许同一 Commit、兄弟分支、不同根，无祖先关系要求；保输入顺序与只读共享合同 |
| `GetPreviousState(E)` / Event 枚举 | 仅选择描述；前者核 E 的角色并找严格前 State，后者保固定上下界、方向与下界祖先资格，不为返回位置恢复内容 |

反例 `S0 → E1 → S1 → S2`：`ReadCheckpoint(S2)` 需要 S2/S1 与 E1/S0 两组材料；在得到 S2/S1 后停止会漏 E1，找到 E1 后马上停止又无法证明其来源 S0。首版允许顺序执行几次窄扫描，共用准确 source 选择函数；不要为了合成一次扫描建立另一套历史材料框架。每次 HS visitor 返回后才做下一次扫描/解码；受限扫描必须失败，不能把预算耗尽当作缺少相反角色。

既有 [ReadCheckpoint / ReadPair](../../../src/DurableGraph.Persistence/Repository.MetaBootstrap.History.cs)分别使用独立物化与只读共享，不能为复用材料统一其对象别名合同。[ReadFrames / EnumerateEvents](../../../src/DurableGraph.Persistence/Repository.cs)还分别承诺完整旧到新列表、无下界的新到旧增量交付；迁移后不得跨 `yield` 持有 HS visitor/lease，也不能将后者偷偷改成预读全史。

### 3.2 图层的唯一资格入口

落实[来源接缝 §3](history-source-seam.md#3-建议阶段签名与资料流)的业务/Meta 分流：

- `ReadBusinessGraph(address, expectedStateSource)`、`ValidateBusinessMetadata(address, expectedStateSource)` 接受 Repository 选择的 source；检查其为 State、非自身，以及现有准确 source/batch/directory/Schema 关系。
- `ReadMetaGraph(address)` 只接受 Meta，从 `PreviousMeta` 得来源；`AppendMeta` 保前 Meta 关系。Meta 的 selected Schema 仍为空，业务 selected 与各 Base 的 origin Schema 保各自含义。
- `AppendBusiness(kind, expectedStateSource, schemaRoot, write)` 在写前比较 `write.SourceGraph`，写后按同一个 expected source 取得实际 snapshot。未发布图无需伪造 CommitId。
- `Revalidate(snapshot)` 先查同 owner/lifetime，业务图使用 `snapshot.SourceGraph` 重读，Meta 走 Meta 入口；继续比较实际 metadata/source/heads。**不保留能够仅凭裸业务地址签发 snapshot 的 `ReadGraph(address)` 旁路。**
- `ReadObjectChain(s)`、`DecodedRevision.ReadStoredGraph`、`BusinessPersistenceAdapter.LoadState/ReadRoot` 的业务路径改接已有 snapshot；`SchemaCatalogView.Load` 等合法 Meta 裸地址入口显式走 Meta。内部底层 fixture 可明确提供 expected source，不能由 candidate 自报 source 充当产品历史证明。

保持一个容易漏掉的合法路径：若新图复用 expected source 的同一 DirectoryRoot，则必须没有 OwnedBatch；该旧 directory 的 creator/source 可能更早，**不能**强制 `directory.SourceGraph == expectedStateSource`。只有新建 directory 时才核其 creator。无变化仍追加新的业务图和新 Entry，领域保存身份不会被去重。

物理 `ReadObjectChainCore` / `ReadDirectoryChain` 仍核局部准确 prior 和 source 角色；它们不需要知道 Commit，也不递归扫描所有物理来源的领域历史。snapshot 接缝 A 已打通传递与热安装，本片只换资格生产者，不重做缓存体系。

## 4. 资源 owner、创建与保存接线

| 当前文件 / 符号 | B2 的具体改动 |
|---|---|
| [RepositoryResources.MetaBootstrap](../../../src/DurableGraph.Persistence/RepositoryResources.MetaBootstrap.cs)：`PublicationBaseline`、`GetBranchHead`、`ReadHistory`、`PrepareSaveCore`、`CommitSave` | baseline 持 D 管理图及成员与 S，业务 current 从 HS 查询；pending 保 BranchId、expected Entry、准确 State source。目录候选复用容器准备，发布成功才安装；普通保存发布 B 或 B/S，不触碰 D |
| [RepositoryResources.Bookmarks](../../../src/DurableGraph.Persistence/RepositoryResources.Bookmarks.cs) | T 改窄管理 Dictionary 图与小入口；值解码成 CommitId 后重新检查成员/Entry/业务描述，保不可变名及现有 outcome/fault 行为 |
| [RepositoryStorageFacade](../../../src/DurableGraph.Persistence/RepositoryStorageFacade.cs)、[PendingRepositorySave](../../../src/DurableGraph.Persistence/PendingRepositorySave.cs) | 只转发新角色值和既有 pending 生命周期，不留以字符串定位分支或裸业务图定位历史的重载 |
| [Repository.MetaBootstrap](../../../src/DurableGraph.Persistence/Repository.MetaBootstrap.cs)：`RestoreCheckout`、`RequireCheckoutSource`、`PublishSave` | 先从准确 Entry 取得历史材料；同 BranchId 编辑占用；保存前核当前 Entry 等于 BaseCommitId。成功后安装 State source（仅 State）与所有提交的新 Base；Event 不接受 State 身份/rewrite |
| [BranchSaveSession](../../../src/DurableGraph.Persistence/BranchSaveSession.cs) | 去掉自行沿 Parent 恢复的构造；由 Repository 传入已资格化 baseline / 无 State 结论和固定模型环境。捕获后立即复查 Store，保 staging/ready 的 AbortPreparation 清理 |
| [Repository.MetaBootstrap.History](../../../src/DurableGraph.Persistence/Repository.MetaBootstrap.History.cs) | 全部业务恢复、Pair 与相反角色查询从 Commit 解析，复用内容接缝；不保留以 FrameAddress 开始的 Parent 游走 |
| [GraphPersistenceContracts](../../../src/DurableGraph.Persistence/GraphPersistenceContracts.cs)、[GraphFrameCodec](../../../src/DurableGraph.Persistence/GraphFrameCodec.cs)、[GraphRecordStore](../../../src/DurableGraph.Persistence/GraphRecordStore.cs) | GraphEnvelope 的业务 Parent 退出；分流 MetaPrevious 和业务 expected source；保物理关系和 A 的 snapshot 贯穿 |

**B2 不以合并所有 checkout Catalog 为前置。**当前 `SchemaCatalogWorkspace` 是内部实现；既有 latest Catalog rebase 可以暂保在单成员路径，每条 Branch 并无独立持久 Schema 权威。仓库 accepted Catalog 和多成员共同准备在后续组内核统一，避免同时改两条关键机制。现有模型环境、prepared State 命中复核、对象可变隔离和 fault/close 清理不得因新 ID 而退出。

创建按[纵切 §5](branch-vertical-design.md#5-创建与登记只保留一个-dg-可见点)三条路径：fork 创建引用 Head 后追加 D 候选图并发布 D；新根先追加真实业务图并 CreateHead 得首 Entry，再追加/发布 D，Catalog 增长时最终发布 D/S。D/S 管理候选均仅在共同发布成功后安装。不能把新 B 又加入最后 CU 制造第二次业务保存。普通 commit 则发布已有 B 或 B/S。共享准备/安装代码可以抽窄函数，不为这两种不同发布点强造通用事务框架。

错误仍分 publication outcome 与 owner health。创建登记 Unknown/Published 交付失败要保已知候选 BranchId；普通保存先保 BranchId 与 ExpectedBase，新 CommitId 只能在 HS 实际返回后取得，Confirmed 异常也可能没有该 ID。既有 `CandidateRevisionAddress` 若保留，只是诊断图地址，不能充当存档 handle。未登记产物不自动清理/认领；旧 owner fault 后不可继续编辑，关闭后按 D 或准确 BranchId 查结果。

## 5. 公开面与调用迁移：必须闭合，不能按草图遗漏删能力

`CheckpointAddress` 当前绑定本次 owner 与图 FrameAddress；不是可原样改名的 CommitId。采用薄 BranchId/CommitId 值类型及上游4B/12B codec，跨重开可用，使用时重新验证所选库。`BranchCheckout → WorkingCopy` 可做声明级重命名，但其 BranchName/Head/StateRevisionAddress 必须分别改为 BranchId/BaseCommitId/StateSourceCommitId，内部图地址继续由持久层持有。新值不默认成为 Generator 支持的 durable 字段；该适配另片处理。具体 TryRead/TryWrite、摘要隔离、返回类型与发布异常定位字段以[API 草图的 B2 合同](api-sketch.md#b2-的具体值与迁移合同)为准，不再保为无候选的待定项。

| 旧公开入口 / 能力 | 本次切换后的去向 |
|---|---|
| `CreateSharedBranch` / `CreateSharedBranchFromEvent` | `CreateRootBranchFromState/Event`，返回 BranchInfo；调用者另 OpenWorkingCopy；创建不隐含永久编辑占用 |
| `CheckoutShared` | `OpenWorkingCopy(BranchId)`；一个 Branch 最多一个活副本，仓库仍串行操作 |
| `ForkShared` 与 ref-only `CreateBranch` | 统一 `ForkBranch(CommitId,label)`，只创建持久入口；需要对象时另打开，不把加载失败当创建回滚 |
| `GetHead`、`ListBranches` | `ReadCurrentCommit(BranchId)`、BranchInfo 列表；补 `ReadBranchInfo` / `ReadCommitInfo`，标签不再用于定位 |
| State/Event 立即保存 | WorkingCopy 的 `CommitState/Event` 返回 CommitInfo，含真实 CommitId；现有无参保存当前 State 的便利能力可保留；新参数是该次自有 summary |
| `ReadState` / `ReadEvent` | `LoadState/LoadEvent(CommitId)`；严格 Kind，不隐式寻找 State；分别恢复独立 mutable 图 |
| `ReadCheckpoint` / `ReadPair` | 保既有“所选图＋最近严格相反角色”及 Pair 只读共享能力，输入改 CommitId，返回位置字段也改 CommitId/CommitInfo；不因主草图省略而删除。名称可暂保，后续统一不是 B2 的前置 |
| `GetPreviousState` / `EnumerateEvents` / `ReadFrames` | 保原有严格祖先、固定上界/下界和查询方向；统一 CommitId，底层复用 HS 历史与小描述。需要枚举时仍在每次推进检查寿命，不能跨用户代码持活动 HS visitor。分页外观可后置，不能暂恢复 Parent 导航 |
| `CreateTag` / `ResolveTag` | 改绑定/返回 CommitId，持久定位不再依赖重新签发 owner-bound 地址；保现有不可变名称能力，不改成 HS HeadSetTag |
| `MoveBranch` | 删除：该能力允许任意重定位，和已确认的首版只追加合同冲突；旧点续写改用 ForkBranch。不通过新名字或兼容壳保留 rewind |

最小公开入口须在 B2 合入时齐备：新建 State/Event 根、fork、读成员/当前/准确描述、准确 Load、最近 State 加载、OpenWorkingCopy、State/Event 保存，以及上表现存额外能力的统一 Commit 迁移。`ListRootBranches/ListBranchSubtree`、分页便利、CommitRequest/组协调器可在同一新内核上后接；已接受功能不是被取消。公开 ID codec 需要最小 round-trip，Generator 字段支持、文本显示格式和泛化外部序列化不扩大本片。

现存消费范围包括 Persistence 测试、真实生成模型测试、CLI、README QuickStart、PackageConsumerProbe 的源码/脚本模板；不能只让 solution build 看不到的包示例保持旧调用。2026-10-19 对 `src/tests/experiments` 的旧入口族宽搜索命中113个 C#文件，仅用于说明迁移面，不是113项独立改造或准确工作量。测试按意图迁移：跨重开旧句柄必拒变为同库 codec round-trip 可定位；重复标签从错误变为可列出的不同 Branch；Move 的旧正例改成只追加/旧点 fork 的验收，而内容、Schema、故障与身份反例保留。

## 6. 真正依赖与可后置项

**定向 origin 影响正式描述查询，不阻断图来源接缝。**上游尚缺[ReadHeadOrigin 窄接口](headstore-origin-request.md)时，可在隔离分支用既有全量 ListHeadOrigins 跑健康数据见证；不能以它为生产 `ReadBranchInfo` 的最终资格，因为无关孤儿损坏会扩散到本次查询。B2 合入前应取得窄接口并固定依赖，或由主线程显式重新裁决并记录较弱边界；不得暗中在 D 重存 origin、从最早 Entry 猜 origin，或将每次 Open 变成全库审计。

新图/Entry 标记、标签上限、summary 所有权与薄 ID codec 继续按 §2 和 API 草图施工；D/T 按[目录合同](directory-persistence.md)复用现有 Dictionary。内部读取、候选/小入口、自然 Delta/冷续写和健康 D/S 发布已有 §7 证据；下一片验收真实 Repository 接线及失败边界，不重复原语实验。附件、树索引和联合提交不是前置。若实际接线揭示必须偏离这些持久形状，再用证据更新本合同及对应反例。

没有发现需要回到用户故事层的新需求冲突。实际施工量仍需用首个纵切 diff 校准，尤其历史便利入口与包模板迁移；不能把“隔离分支”当成可以无限扩展范围的理由。

## 7. 合入验收与后续顺序

先用新生产内核跑最小判别闭环，再迁移全部调用并运行回归。通过条件必须观察持久结果，不能只断言方法返回或内存 State：

1. **真实来源闭环**：`S0,E1,S1,E2` 后父线追加 E3，再从旧 E2 fork；关全部资源/caches，重开恢复 S1 并证明前源 S0；子线 Event→State→再次变更 State，准确 prior/source/H 与真实 Delta 成立；父线继续不改变子线继承前缀。另有唯一 S0 和 Event-only 完整到根。
2. **没有伪造历史/丢物理语义**：空 fork 没有 c0；连续两次相同内容保存有不同 CommitId，冷恢复后再变化保存，覆盖合法 reused directory / 无 OwnedBatch。以实际第二个 Meta 对前 Meta 的 Delta 及 SchemaClosure 验证 MetaPrevious，没有把整个 Meta 演进误当无源根。
3. **真正错误仍失败**：E2 的图/batch/directory 一致自报 S0 而 HS 前源 S1；错误 prior、selected/origin Schema；wrong owner、fault/closed snapshot；失效 ExpectedBase。预算失败不交付 null/部分内容，正常到根才证明无 State。
4. **正式成员与创建边界**：三条登记成功路径、至少两条相同中文标签的 Branch、未登记/管理 Head 被拒、空 fork 重开后即列出/预览/继续；新根有 Catalog 增长时 D/S 共同可见。定向 origin 的坏目标失败、无关未登记坏 Head 不影响目标描述，缺失父成员不当新根。中断注入可在后续登记专项扩充；B2 至少保原有 Unknown/Confirmed/fault 分类，不能先交付会误报未创建的异常路径。
5. **调用闭包**：准确 Load、相反角色、Pair、固定历史枚举、Tag 都通过新 Commit 资格入口；冷读、prepared State 重用、Event 保 baseline、安装后再次保存与关闭后已交付对象可读均保持。源码不再有业务 Parent 游走、旧 name→graph 发布、裸业务 address-only 资格入口；不以删除内容反例换通过率。
6. **管理目录复用**：D/T 的真实 Meta Base→Delta→冷重开续写，准确 source/prior/H、空入口首项 Base、S 白名单不被放宽以及根/比较器/值闭包反例。D/S 共同发布后才共同安装；普通业务保存不追加 D，具体最小判据见[目录合同 §5](directory-persistence.md#5-容量成本与最小验收)。
7. **按操作停止**：`S0,E1,S1,S2` 的 ReadCheckpoint 恢复 S2 与 E1，分别以 S1/S0 资格化；同一点及跨两个根/兄弟分支的 ReadPair 保输入顺序和各自 source，不能由一条历史代签另一条。无相反角色须完整到根，预算失败不交付缺项。Event-only、State-only 与已有方向/下界/枚举寿命反例一并保留。

B2 至少运行根 `dotnet build DurableGraph.slnx`、Persistence 全套和受影响生成模型/Runtime 回归；按当期精确 Storage pin 运行。README/包消费者模板同步编译或在独立任务包路径资格化，普通包、跨进程/kill 与掉电证据分别记录，源码通过不冒充包交付。测试 fixture 的 explicit expected source 只证明物理关系；来源最近性必须由上述真实 HS 集成反例证明。

完成后继续依赖顺序：**真实多业务 Head 组内核 → 全组 Base/清理/outcome 与登记中断 → 根/子树及分页外观补齐、附件 BLOB 和 ID 字段适配 → 成本测量驱动的优化。**基本 summary 已随 B2。若定向 origin 先到，可提前补根/子树；不必等待组提交。后续工作状态仍只在[PROJECT-STATE](PROJECT-STATE.md)滚动维护，本文不累积逐轮日志。

## 8. B2 隔离分支首段纵切

2026-10-19，从 `866ce89` 创建 `db096/branch-repository-cutover`。本段直接改真实 Repository、资源 owner 和图层；没有第二套 Repository 或运行时布局开关。master 仍保前一停点；本分支尚不能作为完整 solution/普通包交付。

### 已接线与实测

- D/S/T 必需控制入口，新 D/T Dictionary 候选准备/回读/发布后接受；业务 Head 的 Entry 使用 P0E1。P0G2 业务没有 Parent，Meta 保 PreviousMeta；batch/directory 格式不变。旧格式不兼容。
- BranchId/CommitId、WorkingCopy、根创建/旧点 fork/保存/准确加载/最近 State、Tag、Pair、Checkpoint 与事件枚举已接真实 Head。没有 MoveBranch 或旧 address-only 入口。源码层面的改动并不自动证明原测试全部仍满足。
- [真实纵切 Probe](../../../experiments/RepositoryBranchVerticalProbe/README.md)使用生成业务模型和正式 Repository API。44 个正向断言覆盖父子独立续写、空 fork 精确继承、重复中文标签、Event-only、关闭全部资源再打开、摘要隔离、Tag、跨根/兄弟/同点 Pair、Checkpoint 停止反例及事件枚举不持 HS lease 跨用户代码。
- 54 个完整性断言覆盖严格 Entry 编码、真实对象/对象目录 Delta、历史最近性错源拒绝（物理关系自洽仍不够）、tip Event 轻量关系检查、发布后抛错的 Published/owner fault/冷开可见；以及有新 Catalog 的根创建在 D/S 登记前中断，NotPublished 与已知物理身份并存、D/S 保旧、冷开隐藏未登记成员、完整候选帧仍可读。普通 State 提交不追加 D。
- Probe 故意通过原始 HS 制造坏历史时先关闭 public Repository，再关闭低层 owner 后全新打开，不复用陈旧缓存。它没有替代正常恢复或实施数据修复。

主线程实际运行（Storage 显式使用独立 `f8868b5` worktree）：

```powershell
$b2Storage = (Resolve-Path .artifacts/db096-head-mechanism/storage-f8868b5).Path
dotnet build DurableGraph.slnx -p:UseStorageSources=true "-p:StorageSourceRoot=$b2Storage"
dotnet run --project experiments/RepositoryBranchVerticalProbe/RepositoryBranchVerticalProbe.csproj `
  -p:UseStorageSources=true "-p:StorageSourceRoot=$b2Storage"
```

Persistence 构建成功；修复 XML 注释后根构建没有警告，CLI 空入口也通过。**根 solution 失败于两个旧测试项目及其链接消费者：当前首轮报告35条旧类型/内部接口编译错误，测试没有执行。**该数字只是首轮编译诊断，不是剩余迁移量；先修声明后还会暴露方法与语义差异。新 Probe 最终返回0，44+54断言通过且无警告。本机日志 `.artifacts/db096-branch-vertical/{persistence-build,solution-build,probe}.log`，生成库留在同目录随机子目录。无普通包、进程 kill、掉电、规模或吞吐资格。

### 独立复核与仍需收窄的地方

两份前置只读调查确认：初始化/登记、HS来源和公开身份不能在旧名字目录下拆成有产品意义的小切换。随后分为图层、资源 owner、公开面三个写入包；第四个包写真实调用故事并独立检查 source/Checkpoint/Pair/交付边界。主线程独立检查源码、运行构建与 Probe，并追加错源/故障/登记反例。

当前最小已知限制：

1. `ReadOrigin` 仍调用全量 `ListHeadOrigins`，会触及无关未登记 Head；它仅用于隔离健康纵切，正式 ReadBranchInfo/根/子树必须等[定向请求](headstore-origin-request.md)落地或明确重裁。此次只读核对上游仍为 `f8868b5`，未替用户发送请求。
2. 打开/保存重复查最近 State，Member 检查线性枚举 D；列表临时逐项扫 origins。这些是具体可削减成本，尚无性能承诺。优先在原回归恢复后利用 session 的已接受 baseline 与 expected Entry 去重，不新增 proof 类型、业务 Parent 或反向索引。
3. 根或 fork 的物理创建已成功、最终登记被拒绝时，目前可能保守 fault owner；NotPublished 仍表示未登记，不能从结果推健康。健康拒绝重试、Unknown、全部安装/清理窗口仍须专门验收。
4. 旧 suite 中的热材料、immutable leaf、升级/准确历史 Schema、独立可变图、无关损坏隔离等尚未重新资格化。本段保留其代码路径，不宣称迁移完整。

### 当时的下一片

下述调用迁移由 §9 接续；此段保首段交付时的范围。先在本隔离分支恢复调用闭包：物理测试显式给 expectedSource、Meta 走 Meta 入口；Repository 测试改真实 BranchId/CommitId，不让 candidate 自报来源代替 HS 历史。同步迁移生成模型 fixture 和链接 PendingRecovery；保测试原意，区分创建后另打开与原实例保留的差异。随后运行完整 Persistence/受影响 Runtime 回归，迁移 README/包消费者以及尚未对齐的源码术语导航。CLI 当前无业务入口，无额外迁移工单。

上述通过后，再做重复历史扫描的局部收窄、定向 origin 接入及其隔离反例，按 §7 决定是否整体合入。之后才推进组内核、根/子树和分页；不要下一轮重新搭一个健康目录或 Branch 实验协调器。


## 9. B2 调用闭包与回归迁移

2026-10-19，以 `f6075d3` 为起点继续同一隔离分支。**根 solution 的两个测试项目及链接 PendingRecovery 已迁移，新布局下的完整原回归重新执行；产品 API 签名和持久格式本轮没有再改变。**本段接替 §8 的“旧测试尚不能编译”停点，仍不构成正式合入或普通包交付。

### 迁移与设计裁决

工作按物理/Meta、公开历史及材料复用、Split/Tag、生成模型调用和其余 Repository 行为分为五个主包，并在包内有限再委派。主线程协调共享夹具、迁移初始化/恢复/根格式夹具、独立检查跨包语义并执行集中构建和测试。

- **公开调用按新职责验证。**`CreateRootBranchFromState/Event` 发布后另 `OpenWorkingCopy`，恢复对象不保创建输入实例；后续 State 提交继续保工作副本当前实例。Fork 先登记，模型恢复在 Open，恢复失败保留已登记 Branch 并释放编辑占用。重复标签合法、同库 CommitId 跨重开有效；取消的 Move 场景改为旧点另 fork 并证明父线不动。
- **机制夹具不改变产品合同。**原 codec/生成模型试验需要保持 capture/install 的同一实例，使用测试专用 `BranchSaveSessionFixture` 组合真实内部准备、提交和实际 Entry 推进；root 安装后仅在测试内取得实际 business adapter，绑定真实 Branch/Entry 后续写。没有向产品补旧入口。已发布物理图的 snapshot 从真实 HS 历史选择严格前 State；未发布/损坏 fixture 明确提供预期 source，不从图 payload 自报来源。
- **保住原来的判别力。**Meta 夹具按 P0G2 补齐仅 Meta 存在的 PreviousMeta；业务 Parent 断言迁为 HS 前项或准确内容来源。Schema 故障测试同时检查真实业务 Entry，避免“D 未变”掩盖 B 已变。Fork/Tag 可以追加管理图，仍逐字节验证既有完整文件前缀或完整 Frame。事件枚举直接检查原 Enumerator，避免 LINQ 在耗尽后遮住寿命检查。目录 Delta 见证调整有限数据量，继续明确要求 Base/Delta，不将断言放宽为任意编码选择。
- **目录限制按实际存储重定。**T 的350个长中文名称超过旧整表 Head payload 容量，控制入口仍17B，冷开首末绑定正确。根目录旧整表容量拒绝测试改验实际标签/summary 输入限制和写前健康拒绝；固定指针与 Dictionary 的局部预算各自保持。
- **没有为了统一异常外观增加机制。**Fork 操作 hook 在任何物理创建前失败，仍允许原 IOException；真实登记阶段失败继续报告 publication outcome。NotPublished 与 owner health 分开。`CommitState(nextState: null!, ...)` 明确选择根参数重载，避免测试的裸 null 被绑定到 summary 重载。

README 的纯 State、旧点 fork/冷续写、ID codec、可选 Event 和策略片段已对齐新 API；原文 runner 的提取标记和隐藏断言同步更新。普通包消费者目录的其他项目、旧资格记录和包模式执行尚未整体迁移，不能据此宣称候选包可交付。

### 验证与限度

集中构建使用同一固定 Storage `f8868b5` 独立 worktree，未改 pin、上游或 feed：

```powershell
$b2Storage = (Resolve-Path .artifacts/db096-head-mechanism/storage-f8868b5).Path
dotnet build DurableGraph.slnx -p:UseStorageSources=true "-p:StorageSourceRoot=$b2Storage"
dotnet test tests/DurableGraph.Persistence.Tests/DurableGraph.Persistence.Tests.csproj --no-build --no-restore `
  -p:UseStorageSources=true "-p:StorageSourceRoot=$b2Storage"
dotnet test tests/DurableGraph.Tests/DurableGraph.Tests.csproj --no-build --no-restore `
  -p:UseStorageSources=true "-p:StorageSourceRoot=$b2Storage"
```

根 build 已通过，0警告/0错误。Persistence 首次974条中11条迁移夹具失败，经明确原因修复后完整重跑 **974/974，通过且0跳过**。Runtime/生成器在最后尾修后完整重跑 **1724/1724，通过且0跳过**。没有禁用测试或用放宽不变量断言来换通过；已退出的 Move、唯一标签、owner-bound 旧句柄和目录整表容量假设按上面的新合同替换；Persistence 数量相对切换前970增加4，来自根格式及输入边界的理论用例。

测试覆盖原内容来源/prior/H、准确历史 Schema 与升级、冷热恢复材料、独立可变图/Pair、immutable leaf 正误例、reentry、清理、正常尾恢复与原有 publication outcome 注入。恢复这些用例不等于性能基准、全部登记故障窗口或掉电资格；部分测量型测试的默认路径不启动其 opt-in 测量。上一段98条独立 Probe 证据保其原运行，不重复报作本轮新结果。

.NET 结果由主线程检查实际日志及 TRX；变更 Markdown 的605个本地链接/锚点检查无错误，`git diff --check`通过。

本机证据：`.artifacts/db096-branch-vertical/migration-build-5.log`、`{persistence,runtime}-migration-2.{log,trx}`；初轮故障与修复前记录保 `*-migration-1`。README source 项目位于同目录 `readme-source/`，使用显式 ProjectReference/Generator 验证，不等于普通包/Build history/候选搬迁资格。从当前 README 原文提取的 source 验证已在新库运行：两个独立进程得到 HP99/98，浏览100/99/98，旧点 fork 并冷续写后 main98/alternate88，4B ID codec 往返成功，可选 Event 读回 Amount1/PreviousState HP98。日志为 `readme-verified-{initial,reopen,browse,fork,events-program}.log`；每步重写 Program 并核对预期输出，防止增量构建误跑旧样例。未执行普通包 runner 或 Schema history/升级流程的重新资格化。

### 下一片：先限制读取范围，再补交付闭包

1. **根测试迁移与 D 重复投影均已完成。**后继[目录查询切片](directory-persistence.md#已接受目录的查询投影)按 accepted D 实例复用只读查询表；普通 B/S/T 不重建，成员/fault 检查保留。不要重做该优化或由此推断整体列表性能。最近 State 重复扫描保留为独立、须有计数反例的后续候选，不阻挡先收拢包调用闭包。
2. **定向 origin 仍为正式合入门槛。**D 查询片再次只读核对上游仍为 `f8868b5`，窄请求仍未交付，也未代用户发送。交付后另接 pin 并验证坏目标/无关未登记坏 Head 的隔离，以及已登记子 Head 的父成员缺失反例；当前全量 adapter 不能作为最终 ReadBranchInfo 产品资格。
3. **下一片优先补齐仓库外层交付。**剩余 PackageConsumerProbe 项目、打包脚本/普通包原文流程以及术语导航按新 API 迁移，包来源独立资格化；不要把 source build 或新测试数字借给旧包。静态清单：EventHistoryRecovery、DevelopmentSchema、ExplicitStorageClassification、PrivateContainer、SplitRepository、Tag、ImmutableLeaf/Family 的 Program；MetaBootstrapSchema 的内部 session/source 调用；Run-RepositoryApiProbe 与 Run-EventHistoryRecoveryProbe 的生成代码。Tag 的 Move/跨 owner 拒绝改成旧点 fork/同库持久 ID 合同，Run-TagProbe 及 Run-SplitRepositoryProbe 的结果断言同步检查。README runner 已迁移但尚未执行新包流程；不把保留模型编译资产扩成全部旧故事重新实施。
4. 此后按 §7 重核是否可整体合入，再接 CommitRequest 组内核、根/子树和分页。它们没有因本轮恢复原回归而变成已实施；不再重新搭健康 Branch/目录实验协调器。
