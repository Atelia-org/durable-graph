# DB-077 / DB-078：公开 API 与优化基础的专项辩证评审

> 状态：**历史评审记录 / 2026-09-23 结论快照，产品未实施**；源码基线 `343bfa6`。
> 后继：2026-09-27 已按 [DB-083](0083-repository-checkpoint-api-user-stories.md) 重写 [DB-077](0077-repository-model-environment-slice.md) / [DB-078](0078-editable-checkpoint-fork-slice.md)，总裁决见 [DB-076 §9–10](0076-efficient-graph-fork-technical-path.md#9-辩证裁决证据与剩余选择)。下文“最终”“本片”“可施工”等措辞只描述当时范围，不是当前施工输入。
> 仍有效：固定模型/current 恒等、每 branch 单工作副本与发布纪律。已替代：泛型公开入口、PendingEvent/交替恢复、077 承担公共改名、每文档一个实施单元。正文保留旧论证与测试证据，不追改为新方案的历史证明。
> 对象：[DB-077](0077-repository-model-environment-slice.md)、[DB-078](0078-editable-checkpoint-fork-slice.md)。
> 用户要求提高 API 与后续优化基础的成熟度，随后质疑同 branch 多 session 的实际价值，指出创建新持久分支已足够清晰。
> 本轮仅审阅和完善设计，不是施工或 API 发布授权。
> 术语收束遵循[项目术语表](../DurableGraph-glossary.md#branch-checkout)：最终推荐使用分支工作副本、`BranchCheckout` 与 `Checkout`；
> 下文保留评审当时的 session/Resume 旧称以忠实记录被否决观点，当前源码事实则明确标注旧名。

## 1. 最终成熟度与最小模型

**两片经有限修订后，可作为推荐施工输入。** DB-077 固定 repo 模型，并由库保证 durable exact-current 恒等；
DB-077 先完成公开 `EventHistorySession` / `Resume` 到 `BranchCheckout` / `Checkout` 的无兼容迁移，仍保持每 repo 一个活动工作副本；
DB-078 再将该限制改为**不同 branch 可并存、每 branch 至多一个活动编辑工作副本**。
需要多个可保存候选时显式 Fork 到新 branch，普通浏览用 ReadState/Event/ReadPair。
两片不增加公共 ModelContext、专用 head 冲突异常、TryCommit、工作副本对象登记表或租约框架。

当前 maturity 的限度是明确的：公开签名、所有权、失败边界和验收已收敛；新行为尚未实现，
还不能称为经过真实消费者验证的 API。实际编译与保存/重开、故障验证由两片施工完成。

## 2. 需求账本与复核过程

| 要求或选择 | 来源与地位 |
|---|---|
| fork 等效于持久 ref + 独立 Load，正确且好用优先，后续逐步优化 | 用户明确目标 |
| 可改变旧 API、callback 政策，无兼容包袱 | 用户明确放宽 |
| 同 branch 多 session 的价值不足以仅靠“省登记表”证明；named fork 已足够清晰 | 用户本轮质疑与偏好，触发重新裁决 |
| append-only、串行资源、防重入、publication/fault | AGENTS 及现有持久语义 |
| identity/cycle、mutable 分支隔离、ID/Parent/增量保存 | 用户目标与源码/测试证据 |
| 固定 repo 环境、current 恒等、首版 named fork、每 branch 单工作副本 | 当前推荐选择，不是凭文档自我批准 |
| LLM/RL 高扇出、短命探索 | 用户用例方向；尚无 ref 时延或多竞争编辑者的量化需求 |

三个继承强模型的独立评审分别担当需求质疑者、最小架构师、语义守卫，先独立论证，再交叉质询两轮。
用户指出 same-branch 价值问题后，针对这一选择追加一次有界复核；三位评审均重新检查需求和生命周期。
主会话独立查源码/实际依赖及消费者，按证据裁决，评审期间不让 reviewer 修改目标。
DB-076 与后继 DB 都是同源提案，不互相充当独立证明。

## 3. 最终裁决

| 候选 | 裁决 | 最小保留机制或暂缓触发 |
|---|---|---|
| 一个 repo 一份现有 snapshot | keep | 一个固定解释 owner，普通/Family 惰性闭合；不保留 per-call override |
| 任意 current Normalize 委托 | simplify | durable exact-current 由库取 typed DTO、附上当前状态内容操作绑定（`CapturedStatePreparation`）；历史转换仍由 provider 负责 |
| 同 branch 多活动编辑 session | defer | 未找到现用例；待出现不能由 branch-per-attempt 满足的竞争编辑需求及成本证据 |
| 全 repo 单 session | simplify | 改为一个活动 branch 名集合；不同 branch 不互相阻塞 |
| 专用 GraphHeadConflictException / stale 恢复流程 | delete 本轮提案 | 它是 same-branch 竞争政策引出的配套；保留已有 Move/CAS 失败语义 |
| 活动 branch 的 Move、旧 session ABA 复活 | delete 本轮提案 | 先 Dispose 该 branch 再 Move；不让正常操作制造 stale 编辑会话 |
| Fork 与 ref-only CreateBranch | keep 两种语义 | 一个交付完整编辑工作副本，一个无需 current 物化能力地创建 ref |
| 完整恢复后发布 ref、全程 guard、outcome/fault | keep | 防半图/重入，解释 durable-ref 后未返回的窗口；不造 2PC |
| lease、全局 token/epoch、WeakReference 自动释放占用 | defer / 不引入 | 内存中的确定 Dispose 生命周期足够，不授外部一次执行权 |

### 3.1 为什么撤回 same-branch 乐观编辑推荐

它有潜在用途：两个编辑者基于同 head 产生候选，谁先完成谁提交，并省掉各自创建分支。
但当前 README、EventHistory/Recovery 包消费者和集合/enum 包消费者均保持一份活动编辑工作副本；
历史 fork 测试使用不同 branch。没有找到必须竞争推进同一个 ref 的现消费者。
用户的探索树正需要区分尝试和各自历史，named fork 可以直接表达；只看两份状态则已有只读入口。

之前“保留 head/CAS 即可，不用维护登记表”的论证只证明该功能可实现，没证明它值得开放。
它还产生合法 stale session、冲突恢复 API、Move 使活动视图失效及 ABA 复活等公开状态。
用一个内部活动名字集合换掉这些状态，**代码里多一个集合，产品语义反而更少**。
因此撤回第一阶段复核提出的专用 head 冲突异常；不把一个自选功能的配套需求再当作保留该功能的理由。

这不妨碍 DB-079–082：不同 branch 仍可指向同一 GraphFrame，共享完整恢复准备材料/热 baseline/immutable 叶的证明不依赖同 branch 多工作副本。
如果将来大量短命探索主要受 ref 发布约束，优先重新明确无 ref fork 的产品需求，不能用竞争写同一 branch 偷换独立分叉。

### 3.2 一个名字集合的真实边界

最小机制为 `HashSet<string>(StringComparer.Ordinal)`，只存 branch 名，不持有工作副本。
本片不新增名字规格。依据 [StorageDependency.props](../../eng/StorageDependency.props) 和已安装 nupkg 的 repository commit，
核对 `atelia-storage@976aa345f923da09e2a5cf1dc25ba592b3818b63` 的 `src/EventJournal/EventJournal.Refs.cs` / `EventJournal.cs`：
分支索引及重放使用 ordinal，名字校验由 Journal 提供。这里读取的是 pin 对应 Git 对象，不以兄弟 checkout HEAD 推断实际依赖。

占位在 `_busy` 内、可能发布前取得；本次未交付退出只撤销本次成功取得的标记。
具体失败轨迹是 A 已占 main，B 的 Add 失败：若 B 的 finally 无条件 Remove，会解除 A 的占用。
另一个轨迹是 A.Dispose→B.Checkout→A 再 Dispose：必须由 A 自身幂等 guard 拦截，不能删 B 的标记。
正常工作副本构造、只读 owner/BranchName、Dispose 幂等与单线程 guard 已足够，无需 token 或弱引用登记。
repo Dispose 清其集合，旧工作副本的后续清理不访问 Store、不影响重新打开的 owner；忘记 Dispose 不由 GC 自动补偿。

占用仅规定使用方式，**不能删除 exact head、State baseline、发布前检查或最终 CAS**。
现有故障注入仍可制造追加后 CAS 失败，必须按原 GraphCommitException/outcome/fault 处理。

同一 pin 的 CreateBranch 不复制 State/Event 图历史，但包含 Create、ref Init、BindName 以及 durable flush 和新 ref 存储创建。
本轮没有时延测量，不能宣称它近似零成本。该事实不构成保留 same-branch 竞争的需求证据；
当前首先交付用户明确认为足够的持久分叉，后续以真实测量决定优化目标。

### 3.3 current 恒等从委托约定收束到局部快路

[StateModelBinding.Normalize](../../src/DurableGraph/Runtime/Binding/StateModelBinding.cs) 当前无条件调用 `_normalize`。
纯函数 `x→x+1` 也会让热 capture DTO=5、冷读=6。最小修订是保留 RequireSource，
在完整 source layout 等于 current 时读取 exact TState、保留 Id、附当前 preparation；历史输入才调用委托。
不能直接返回可能无 preparation 的 reader row，不做任意委托纯度检测。

最强反例是缓存 Family binding 后迟注册 Schema 冲突，再脱离 context 直接 Normalize。
RequireSource 不能替代 live 权威；但当前唯一产品调用点 [NormalizedRevision.Create](../../src/DurableGraph.Persistence/NormalizedRevision.cs)
每行先 ResolveModel，经 StateModelSnapshot 的 BindSchema/CheckRegistered 后才调用内部 Normalize。
内部 bare binding 不单独承担 repository 权威，binary 静态 binding 本来也不持有这样的 context。
公共 [StateBindingContext.Normalize](../../src/DurableGraph/Runtime/Binding/StateBindingContext.Upgrade.cs) 的零步 requirements 检查保持，
历史转换与容器同布局 NormalizeState 也保持。未来优化不得绕过标准解析或完整材料证书复核。

架构评审撤回“必须机械化才算成熟”的强说法，指出选择取决于该责任边界。
主会话依据可见性、实际调用点及 binary 现状，明确这个内部边界并推荐局部快路；
这没有被伪装成纯参数迁移，也不扩张成 DB-080 的证书工程。

### 3.4 外部副作用的边界仍然存在

每 branch 单工作副本不代表外部 exactly-once：外部动作完成后、State 发布前中止可能导致重做；
从同一 Event Fork 两个不同 branch，也会合法得到两份 PendingEvent。
[恢复消费者](../../experiments/PackageConsumerProbe/EventHistoryRecoveryConsumer/Program.cs) 明确采用纯内存 Apply，
本片继续由应用决定业务调度/外部效果，库不建任务领取或效果回滚框架。

## 4. 验收与仍待施工的证据

DB-077 补当前不执行历史委托、typed DTO/当前 preparation 绑定与长期闭合后迟注册冲突见证，并完成公开改名、XML、仓内调用和真实包消费者迁移；
替换动态 current Normalize 的旧产品保证，保留合法历史/不同 head 的隔离回归。
DB-078 以不同 branch 的 A/B fork、同 branch 重复 `Checkout` 拒绝、Move 活动目标拒绝、登记失败清理及 Dispose 幂等为核心；
消费者实际编译 Open→Fork A/B→各自 E/S Commit→冷重开。两片仍分别实施验收，不合并六片。

本轮以 `--no-build --no-restore` 重跑既有 8 个测试方法，8/8 通过：
`CacheHitsRecheckAnAuthoritativeSchemaRegisteredAfterInitialClosure`、
`FailedClosureDoesNotPublishAndLaterCatalogCanSupplyMissingCapability`、
`HistoricalForkAndMoveSelectLogicalParentRatherThanLatestPhysicalState`、
`CaptureFailureAndCaptureReentryPreserveInstalledStateAndCanRetry`、
`RefCasMismatchAfterJournalAppendDoesNotPublishOrInstallStateCandidate`、
`PendingEventColdResumeIsolatedFromMutableStateAndPairPreservesEachView`、
`CachedZeroStepPlanRechecksLateRegisteredInlineConflict`、
`GeneratedStateModelUpgradesAdjacentDtosOnceAndKeepsCurrentFastPathAndInput`。
这些结果支撑现有机制，不能充当固定环境/current 快路/branch 占用的新实现验收。本轮没有改动产品代码。
最终文档复核检查了 146 个本地链接（含 12 个标题锚点）、新文件空白和 `git diff --check`，全部通过；
DB-077 的局部快路与 DB-078 的占用/Dispose 生命周期经独立回读，未发现残余阻塞。
