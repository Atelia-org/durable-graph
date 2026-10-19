# DB-096：真实 Branch 历史的一次切换计划

> 2026-10-19，施工收窄稿，**本文所述布局与公开入口尚未实施**。以 DG `2d118da` 的来源 snapshot 接缝为起点；新 Storage 的独立接入资格以[工作集](PROJECT-STATE.md)当期记录为准。本文负责 B 片的接线顺序、调用面迁移和合入边界；需求及来源正确性仍由[Branch 纵切](branch-vertical-design.md)与[来源接缝](history-source-seam.md)定义。

## 1. 收窄结果

**最小可合入结果不是一个新的内部读取函数，而是一个只支持新布局的 Repository：每条 Branch 对应真实 Head，准确 Commit 对应 Entry，业务内容资格化与恢复先按 Entry 的继承历史选择 source。** 描述查询与 fork 不因此扫描前史。创建、打开、保存、旧点分叉、关闭重开和现存历史读取必须同时可用。内部测试宿主可以先打通接缝，但不能作为保留旧公开 address-only 入口的理由。

推荐分两次可独立验收的变更：

1. **B1：精确新 Storage 依赖与不兼容调用适配。**保持旧 DG 布局，根构建和相关现有回归通过；不据此声称新 Branch 已实现。
2. **B2：在隔离实现分支完成新布局与必要调用迁移，再整体合入。**其中可保留可审阅的工作提交；主线不加入运行时布局开关、旧格式读取或第二套 Repository/GraphRecordStore。先写真实纵切测试，随后接齐现存调用；最后按 §7 验收。

隔离分支只是容纳暂时不能通过全体调用的工作，并不能减少协议依赖。B2 不顺带实现多成员 CommitRequest 协调器、BLOB 管理、持久索引或性能优化；一成员保存仍须使用真实业务 Head，不能继续更新旧 name→graph 目录。

## 2. 最少协议选择

以下是建议落实的具体形状。版本标记须在 B2 的 codec 测试中确定；下表版本值是建议，不是现已支持的格式。

| 载体 | 推荐内容与变更 | 原因和限度 |
|---|---|---|
| D，保留控制 Head 1 | 新根标记（例如 `P0R3`）；按 HeadId 的稳定编码排序的 `HeadId → DisplayLabel` 完整表 | 成员唯一、标签可重复；不保存 current/summary/origin。删除旧 `branch/` 字符串到图地址映射 |
| S，保留控制 Head 2 | 继续保存当前 Meta 图入口 | `P0S1` 可保持，因为字段语义未变；历史图仍各自 pin Schema |
| T，保留控制 Head 3 | 新标记（例如 `P0T2`）；不可变名称 → 12B CommitId | 既有书签能力保留；旧 graph address 在删除 Parent 后不再足以定位历史，不能继续使用 `NamedFrameMapCodec` 存 T |
| 业务 Head Entry | 新小描述标记、准确 graph FrameAddress、长度前缀的 opaque summary bytes | Kind 仍取图 envelope；不重复 Schema、owner 或 Parent；创建与每次提交时复制摘要，空摘要合法 |
| Graph envelope | 新图标记（例如 `P0G2`）；Kind 后**仅 Meta** 编码 `PreviousMeta`，业务图没有该字段；其他根/目录/Schema/batch 字段保持 | 用一个内部 envelope 和 codec 的 Kind 分支足够，不需要三个公开图类；禁止业务图以 null Parent 占位来保留旧历史模型 |
| batch / object directory | 保现有格式、SourceGraph、准确 prior 及内容校验 | 物理增量来源不是待删除的逻辑 Parent；不能为字段改名一并去掉 |

summary 推荐第一片用自有 bytes，不指定 JSON、字符串或另建摘要 Schema。公开输入可取 `ReadOnlyMemory<byte>`，内部取得自有副本；读取返回值也不能暴露会影响内部缓存的可变数组。限长按实际 Entry 编码后不超过 HS `MaxHeadEntryPayloadLength` 检查，不能只检查 summary 的独立长度。缩略图地址可由应用写在这些 bytes 中，附件读取协议后置。以后若选择字符串便利重载，应是明确编码的外观，不是第二种持久权威。

标签采用有效 Unicode 文本，首版创建后固定；不能继续调用现有只接受小写路径式名称的 `ValidateBranchName`。建议沿用书签已有的有效 UTF-16 检查思路，取一个显式标签上限（候选：1024 个 UTF-16 code unit），同时按编码后的整个 D 做 HS payload 限长；数量1000并不是该最大标签长度下的容量保证。D 的顺序以编码值定义，不以进程哈希或本地文件号定义。拒绝重复 HeadId、非法编码、计数超界、截断与尾随字节；不拒绝重复文本。

旧 D/图/T 由新 codec 明确拒绝，不迁移、不自动重写完整帧。只有实际改变的格式升级；无需为了版本对齐修改 batch、directory 或 S。Open 先完成正常 FS/HS 恢复并读取必需控制根，再建立图 Store/cache；不为了新版本做全库图或全部 Head 预打开审计。

## 3. 来源接线：Repository 决定历史，图层校验内容

### 3.1 Repository 的两个读取步骤

第一步 `ReadCommitDescriptor`（内部名称可调整）：由 CommitId 取得 checked HS Entry；确认其 owner 是 D 正式成员，再解 Entry 小描述与图 envelope 的 State/Event 角色。当前值读取还须同时保留被查询的 BranchId：空 C 继承 A/a1，不意味着编辑目标变成 A。历史 visitor 交付的每项也执行成员/描述检查，不容许管理 Head 或未登记来源混入业务历史。

第二步按操作边界读取历史：准确图 X 的 source 是 `Before(X)` 最近 State；加载最近 State 从 `AtOrBefore(X)` 选点；为新保存 Y 准备 source 则从已接受 tip X 的 `AtOrBefore(X)` 取得。冷 OpenWorkingCopy 需要最近 State 及其严格前源，不足两个必须查到根。保持[纵切 §4](branch-vertical-design.md#4-删除业务-parent-后如何保留恢复证明)中的 tip Event 轻量检查、Event-only 到根及预算失败规则。

HS visitor 只收集准确 Entry/图地址、Kind 和足够的最近 State 信息；退出 visitor 后再验证或物化内容，禁止递归进入 HS history。一次内部历史选择函数供上述入口复用即可，不要引入 resolver 接口、Graph→Commit 表或全库索引。预算不足仍失败；不可用有限前缀证明 null。沿途普通 Event 只读取小描述和 envelope，不变成全历史内容审计。

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
| [RepositoryResources.MetaBootstrap](../../../src/DurableGraph.Persistence/RepositoryResources.MetaBootstrap.cs)：`PublicationBaseline`、`GetBranchHead`、`ReadHistory`、`PrepareSaveCore`、`CommitSave` | baseline 持 D 成员与 S，业务 current 从 HS 查询；pending 保 BranchId、expected Entry、准确 State source。普通保存发布 B，有 Catalog 增长时发布 B/S；不再为每次保存编码 D |
| [RepositoryResources.Bookmarks](../../../src/DurableGraph.Persistence/RepositoryResources.Bookmarks.cs) | T codec 改 CommitId；解析后重新检查成员/Entry/业务描述，保不可变名及现有 outcome/fault 行为 |
| [RepositoryStorageFacade](../../../src/DurableGraph.Persistence/RepositoryStorageFacade.cs)、[PendingRepositorySave](../../../src/DurableGraph.Persistence/PendingRepositorySave.cs) | 只转发新角色值和既有 pending 生命周期，不留以字符串定位分支或裸业务图定位历史的重载 |
| [Repository.MetaBootstrap](../../../src/DurableGraph.Persistence/Repository.MetaBootstrap.cs)：`RestoreCheckout`、`RequireCheckoutSource`、`PublishSave` | 先从准确 Entry 取得历史材料；同 BranchId 编辑占用；保存前核当前 Entry 等于 BaseCommitId。成功后安装 State source（仅 State）与所有提交的新 Base；Event 不接受 State 身份/rewrite |
| [BranchSaveSession](../../../src/DurableGraph.Persistence/BranchSaveSession.cs) | 去掉自行沿 Parent 恢复的构造；由 Repository 传入已资格化 baseline / 无 State 结论和固定模型环境。捕获后立即复查 Store，保 staging/ready 的 AbortPreparation 清理 |
| [Repository.MetaBootstrap.History](../../../src/DurableGraph.Persistence/Repository.MetaBootstrap.History.cs) | 全部业务恢复、Pair 与相反角色查询从 Commit 解析，复用内容接缝；不保留以 FrameAddress 开始的 Parent 游走 |
| [GraphPersistenceContracts](../../../src/DurableGraph.Persistence/GraphPersistenceContracts.cs)、[GraphFrameCodec](../../../src/DurableGraph.Persistence/GraphFrameCodec.cs)、[GraphRecordStore](../../../src/DurableGraph.Persistence/GraphRecordStore.cs) | GraphEnvelope 的业务 Parent 退出；分流 MetaPrevious 和业务 expected source；保物理关系和 A 的 snapshot 贯穿 |

**B2 不以合并所有 checkout Catalog 为前置。**当前 `SchemaCatalogWorkspace` 是内部实现；既有 latest Catalog rebase 可以暂保在单成员路径，每条 Branch 并无独立持久 Schema 权威。仓库 accepted Catalog 和多成员共同准备在后续组内核统一，避免同时改两条关键机制。现有模型环境、prepared State 命中复核、对象可变隔离和 fault/close 清理不得因新 ID 而退出。

创建按[纵切 §5](branch-vertical-design.md#5-创建与登记只保留一个-dg-可见点)三条路径：fork 创建引用 Head 后发布 D；新根先追加真实图并 CreateHead 得首 Entry，再发布 D，Catalog 增长时最终发布 D/S。不能把新 B 又加入最后 CU 制造第二次业务保存。普通 commit 则发布已有 B 或 B/S。共享准备/安装代码可以抽窄函数，不为这两种不同发布点强造通用事务框架。

错误仍分 publication outcome 与 owner health。创建登记 Unknown/Published 交付失败要保已知候选 BranchId；普通保存先保 BranchId 与 ExpectedBase，新 CommitId 只能在 HS 实际返回后取得，Confirmed 异常也可能没有该 ID。既有 `CandidateRevisionAddress` 若保留，只是诊断图地址，不能充当存档 handle。未登记产物不自动清理/认领；旧 owner fault 后不可继续编辑，关闭后按 D 或准确 BranchId 查结果。

## 5. 公开面与调用迁移：必须闭合，不能按草图遗漏删能力

`CheckpointAddress` 当前绑定本次 owner 与图 FrameAddress；不是可原样改名的 CommitId。采用薄 BranchId/CommitId 值类型及上游4B/12B codec，跨重开可用，使用时重新验证所选库。`BranchCheckout → WorkingCopy` 可做声明级重命名，但其 BranchName/Head/StateRevisionAddress 必须分别改为 BranchId/BaseCommitId/StateSourceCommitId，内部图地址继续由持久层持有。新值不默认成为 Generator 支持的 durable 字段；该适配另片处理。

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

必须在开始 B2 写 codec 时确定的局部值只有：新 D/图/T/Entry 格式号、标签编码/上限、summary 的 bytes 输入与所有权、Entry/D 总长度检查、薄 ID 的公开 codec 形状。以上有明确小候选，不需要先设计附件、树索引或联合提交。若选择与 §2 不同的持久形状，应先更新本合同和拒旧/畸形反例，再施工。

没有发现需要回到用户故事层的新需求冲突。实际施工量仍需用首个纵切 diff 校准，尤其历史便利入口与包模板迁移；不能把“隔离分支”当成可以无限扩展范围的理由。

## 7. 合入验收与后续顺序

先用新生产内核跑最小判别闭环，再迁移全部调用并运行回归。通过条件必须观察持久结果，不能只断言方法返回或内存 State：

1. **真实来源闭环**：`S0,E1,S1,E2` 后父线追加 E3，再从旧 E2 fork；关全部资源/caches，重开恢复 S1 并证明前源 S0；子线 Event→State→再次变更 State，准确 prior/source/H 与真实 Delta 成立；父线继续不改变子线继承前缀。另有唯一 S0 和 Event-only 完整到根。
2. **没有伪造历史/丢物理语义**：空 fork 没有 c0；相同内容保存有新 CommitId；覆盖合法 reused directory / 无 OwnedBatch。以实际第二个 Meta 对前 Meta 的 Delta 及 SchemaClosure 验证 MetaPrevious，没有把整个 Meta 演进误当无源根。
3. **真正错误仍失败**：E2 的图/batch/directory 一致自报 S0 而 HS 前源 S1；错误 prior、selected/origin Schema；wrong owner、fault/closed snapshot；失效 ExpectedBase。预算失败不交付 null/部分内容，正常到根才证明无 State。
4. **正式成员与创建边界**：三条登记成功路径、至少两条相同中文标签的 Branch、未登记/管理 Head 被拒、空 fork 重开后即列出/预览/继续；新根有 Catalog 增长时 D/S 共同可见。定向 origin 的坏目标失败、无关未登记坏 Head 不影响目标描述，缺失父成员不当新根。中断注入可在后续登记专项扩充；B2 至少保原有 Unknown/Confirmed/fault 分类，不能先交付会误报未创建的异常路径。
5. **调用闭包**：准确 Load、相反角色、Pair、固定历史枚举、Tag 都通过新 Commit 资格入口；冷读、prepared State 重用、Event 保 baseline、安装后再次保存与关闭后已交付对象可读均保持。源码不再有业务 Parent 游走、旧 name→graph 发布、裸业务 address-only 资格入口；不以删除内容反例换通过率。

B2 至少运行根 `dotnet build DurableGraph.slnx`、Persistence 全套和受影响生成模型/Runtime 回归；按当期精确 Storage pin 运行。README/包消费者模板同步编译或在独立任务包路径资格化，普通包、跨进程/kill 与掉电证据分别记录，源码通过不冒充包交付。测试 fixture 的 explicit expected source 只证明物理关系；来源最近性必须由上述真实 HS 集成反例证明。

完成后继续依赖顺序：**真实多业务 Head 组内核 → 全组 Base/清理/outcome 与登记中断 → 根/子树及分页外观补齐、summary/BLOB 和 ID 字段适配 → 成本测量驱动的优化。**若定向 origin 先到，可提前补根/子树；不必等待组提交。后续工作状态仍只在[PROJECT-STATE](PROJECT-STATE.md)滚动维护，本文不累积逐轮日志。
