# DB-096：共同 Catalog 与组提交内核

> 2026-10-19；源码基线 `c077d1c`（Private 删除实现 `efdf61b`）。这是下一实施片的内部设计及判别实验，不是新 API、组发布或故障恢复已经通过的运行资格。公开调用合同见[联合提交](multi-branch-commits.md)，历史布局变化另由 Branch 纵切验证。

> 当前施工入口为 [§9](#9-新布局实际内核的施工接点)，按产品 `c9ad935` 复核；§7–8 保留当时旧布局的证据范围，不再作为续工顺序。

## 1. 收窄结论

推荐一个仓库 accepted Catalog，一次调用内的一份 Catalog 候选，以及各 WorkingCopy 独立的业务候选。单分支保存走同一内核的单成员路径。保留唯一仓库操作窗口；不把多个旧 `PendingRepositorySave` 同时塞入数组，也不为旧布局和新布局制造通用 publisher 框架。

公开 `CommitRequest` 只是活工作副本、预期 Base、根引用、角色和自有摘要的参数对象；内部 prepared 候选仍是一次性的。请求不必增加 `Consumed` 状态：成功使 Base 改变，Unknown/Published 错误使 owner 停用；健康且 Base 未变的失败可以再次提交同一请求，但会重新捕获当前图。具体公开规则以联合提交文档为准。

## 2. 已有原语与真正缺口

| 当前事实 | 能复用的部分 | 必须改变的部分 |
|---|---|---|
| [BranchSaveSession.Prepare](../../../src/DurableGraph.Persistence/BranchSaveSession.cs)在 Capture 后准备 Catalog/body，并重新检查 Store | 捕获与 body 准备分开；Capture 后立即检查寿命 | 移除每 checkout 的写 Catalog 和 Rebase；组内先捕获所有成员，再准备一个 Catalog |
| [SchemaCatalogWorkspace.PrepareOwned](../../../src/DurableGraph.Persistence/SchemaCatalogWorkspace.cs)收集闭包、查 accepted 冲突、一次 Builder | 共同 descriptor 分配和准确 view；无新增时重用旧根 | accepted 归仓库；内部显式声明是操作输入，不保可变 per-copy dirty 平台 |
| [BusinessWorkspace](../../../src/DurableGraph.Persistence/BusinessWorkspace.cs)每实例最多一个候选，区分 staging/ready/installed | 独立身份、冻结模型、State 基线；候选丢弃不安装 | 组协调器必须保存每个已取得候选并负责跨成员失败清理 |
| [BusinessPersistenceAdapter.PrepareOwned](../../../src/DurableGraph.Persistence/BusinessPersistenceAdapter.cs)接收准确 Catalog view | 每份图使用同一最终 view 编码，仍与自己的最近 State 比较 | 不能分别准备独立 Catalog 后改 pin；不能拿另一个候选的安装材料代替 |
| [RepositoryResources.CommitSave](../../../src/DurableGraph.Persistence/RepositoryResources.MetaBootstrap.cs)先追加、准备安装、再发布 | append-only、实际 source 校验、Outcome/fault、发布前准备材料 | 单成员 pending/result 改组；一次共同发布后安装全部成员 |
| [Repository.PublishSave](../../../src/DurableGraph.Persistence/Repository.MetaBootstrap.cs)发布返回后推进 checkout.Head | 一个仓库 busy 窗口覆盖完整操作 | 所有成员的 Base 更新也必须纳入组级失败处理，不能留在已结束的内部 catch 之外 |

共同 Catalog 编码的[已有见证](../../../tests/DurableGraph.Persistence.Tests/SharedCatalogGroupPreparationTests.cs)只验证内容和冷读。它没有验证本页生命周期和真正的组原子发布。

仓库可惰性载入 accepted Catalog；以当前 SharedRoot 对应的准确 view 为唯一缓存值。准备不改变该值，发布确认后才替换；无新增定义不发布 Schema Head。无需在 Open 时为此增加全历史扫描或新审计。工作副本保自己的恢复视图、对象身份、模型执行环境、State source，不能因为写 Catalog 共享就原地刷新执行环境。

## 3. 一次操作的具体阶段

1. **固定输入并准入。** 先取得 Repository 操作 guard，再物化输入集合，防止自定义枚举器重入。检查非空、同一活 owner、同一活 WorkingCopy 实例、每 Branch 至多一个请求、每个预期 Base 与实际 tip、State source/根身份、角色和策略。全部完成后才进入第一个 Capture。不要仅比较 StateSource：连续 Event 也使旧请求过期。候选、结果槽和清理名单尽量提前分配。
2. **取得操作基线。** 固定仓库 accepted Catalog、参与 Branch 的当前位置及每成员业务基线；整个操作持有资源 owner 的准备/提交 guard，直接借入 facade 的重入也不能绕过。此时没有持久发布。
3. **顺序捕获全部图。** 每次 `CaptureOwned` 成功立即把候选纳入操作所有权；随后立即执行 owner/Store 可用性检查，再捕获下一成员。即使全部都是 Event 且没有 State baseline，也要检查。候选处于 staging 是正常状态，不能因等待别的成员而提前安装或释放身份。
4. **一次 Catalog 准备，再逐图编码。** 汇总所有捕获布局及必要内部声明，先用完整 closure 检查同 key 异形及 accepted 约束，再一次分配 descriptor。每成员按同一 view 调用 `PrepareOwned`，成功后变为 ready。保留准确历史 Schema、source membership、Delta prior 和 rewrite 检查。body/preparation 也可能调用自定义代码，完成后仍检查资源；不能把 Capture 当作唯一用户代码窗口。
5. **追加并预备安装。** 追加至多一个新 Meta，再追加所有业务图；无 Catalog 增长时选旧 Meta。所有 State 准备其实际图的安装材料；Event 不调用 State PrepareInstall。准备摘要 payload、完整发布参数、结果容器与发布后要用的非业务代码安装材料。检查所有资源仍有效。失败不移除完成帧。
6. **一次发布。** 新布局使用所有参与业务 Heads，加上必要的 Schema Head，共同发布；无 Schema 增长也须把多业务成员作为一个组。单成员且无 Schema 增长可直接发布一个 Head，但仍走相同状态机。不要更新 Branch 登记目录，也不包括新建/fork Branch。
7. **确认后统一安装和交付。** 填实际 CommitId，更新 accepted Catalog、各 State 基线及所有成员 Base；正常返回完整结果。全过程仍在组级异常/fault 范围内，不运行新的业务捕获、模型工厂或 Upgrade。最后释放所有未安装候选和 owner 操作窗口。

实际 `HeadEntryId` 直到 HeadStore 发布返回才可取得。因此“发布前准备结果”仅指预分配容器、成员关联和其他可准备材料，不能伪造预先确定的 CommitId。确认后校验返回键集完整且匹配目标，填 ID；该步骤或结果交付失败仍属于 Published，整个 owner fault。发布后的少量赋值不是事务回滚点。

所有参与图必须在整次执行期间保持稳定，不能只保持轮到自身 Capture 时稳定；后一成员回调修改前一成员的根，会使安装的原实例与已捕获 DTO 不一致。框架仍不增加通用变更追踪，不回滚应用回调副作用。

## 4. 清理和 State/Event 的关键区别

最容易引入的错误是给候选列表统一 `Dispose()`。当前 `BusinessSaveCandidate.Dispose` 经 `RequireReady`，对 staging 候选会抛。若第二成员 Capture 抛出或共同 Catalog 冲突，第一成员还没 CompletePreparation，正处于 staging。

较小做法是让组拥有所有成功取得的 `BusinessSaveCandidate`，在退出时逐一调用已有 `AbortPreparation()`：它只检查候选归属，可清理 staging 或 ready；已成功 Install 的候选 `_owner` 已清空，再调用是无操作。`PrepareOwned` 自己失败也会 Abort，重复调用安全。第二成员若在返回候选前失败，`BusinessWorkspace.Capture` 已清掉其 context/staging；组仍负责先前成员。不要在 finally 中依赖 Store 再次健康才能清掉纯内存候选。

清理须尽力覆盖全部成员，不能因一项清理异常漏掉后面成员；保留原始失败和已知 publication outcome。清理异常意味着内存生命周期不再可信，owner 停用。尚未成功取得/记录的候选不能遗失，宜预分配固定成员数组再逐槽填写，而非依赖捕获后的集合扩容。

成功返回前须完成全部清理；清理错误也进入统一 outcome 分类。不能在已经结束异常分类的 `finally` 中直接抛清理异常，使 Published 标记丢失；可先聚合清理错误，再在统一收尾处分派结果，不必新增通用事务框架。

| 成员 | 发布确认后的效果 | 清理效果 |
|---|---|---|
| State | 安装该实际候选的 DTO、原始业务实例、身份映射、准确图/Schema、head/H 和 rewrite 结果；推进 Base 与 StateSource | Install 后候选已解决；不能用别人的安装材料 |
| Event | 只推进 Base；保 State 根、身份、StateSource、head/H、待 rewrite | 丢弃 Event 捕获候选，不 Accept 其身份；Catalog 若增长仍全组接受 |
| 任意成员失败且尚未发布 | 不推进 accepted Catalog 或任何工作副本基线 | 丢弃所有候选；调用方业务修改保留，已分配 ObjectId 可以烧号 |

准确候选身份已有反例：[DifferentRealCandidateInstallationIsRejectedWithTheSameStoreModelsAndNumericIds](../../../tests/DurableGraph.Persistence.Tests/BusinessSaveCandidateInstallationTests.cs)。[CaptureSession.Discard](../../../src/DurableGraph/Runtime/Capture/CaptureSession.cs)明确保留已消耗数字；因此重试不承诺 ID 或编码逐字节相同。稳定性是 accepted 状态和来源不被未发布候选改变。

## 5. 失败矩阵和 owner 健康

继续将发布结果与资源健康分开，不用 `NotPublished` 代替 `!IsFaulted`。以下是建议继承当前保守规则，不扩大健康失败恢复范围。

| 失败位置/事实 | 持久结果 | owner / 请求 |
|---|---|---|
| 输入拒绝、Capture/闭包/body 准备失败，未 append 且资源健康，清理成功 | NotPublished；准备期可原样传播异常 | 可继续；同一活 WC/Base 的请求可重新执行并重捕 |
| Capture 后发现资源已停用 | 未调用发布；不声称整个 owner 健康 | 按实际 fault/lifetime 拒绝使用；清理候选不能调用业务代码 |
| 已追加后、发布前的异常 | NotPublished，允许完整孤儿帧 | 当前 CommitSave 保守 fault；本片保持，不借组化放宽 |
| HeadStore 明确正常拒绝，outcome=NotAttempted，backing 健康 | NotPublished，可能已有孤儿帧 | 当前实现允许健康；再次执行仍需同一活 WC/Base |
| HeadStore Unknown 或无法证明正常拒绝的发布异常 | Unknown 或底层明确 outcome | fault；重开检查，禁止透明重试 |
| 确认发布后，结果缺项/ID填充/任一安装或交付失败 | Published，全组持久提交已成立 | fault 所有 WorkingCopy；不返回部分成功，不回滚已安装成员 |

Published 后前几个 State 可能已经安装，这不造成可继续使用的半组：整个 owner 停用是必要保证。安装故障后的对象引用仍可能在应用手中，因此文档不能承诺它们被还原；恢复必须从新 owner 重新取得。

Unknown 的准确组位置发现仍是独立恢复合同，不由本页新增幂等键、组日志或自动重试。需要保存参与 Branch 与 expected Base 以供调用者定位；不能把未取得的实际 CommitId 写入异常假装已知。

## 6. 下一片的最小判别实验

以下是新组协调器的验收要求，尚未整体运行。§7 验证候选清理；§8 进一步验证真实混组发布、安装和冷续写原语。后续将这些判据迁入新布局，不再为旧目录实现正式组协调器或过渡公共 `CommitBranches`。

1. **共同 Catalog＋成功混组。** 两个真实工作副本各引入不同 Schema，一个 State、一个 Event；一次发布，冷重开读取两份准确内容与共同 Meta；热端 State 身份/baseline 推进，Event 的 State/rewrite 不推进。随后分别继续保存，验证正确 Delta/source/prior/H。
2. **第二成员失败与 staging 清理。** 第一成员完成 Capture，第二成员 Capture 抛出；另一用例所有 Capture 成功后跨成员布局冲突。所有 tips、Catalog、基线不变、零 append、owner 健康，两个 WC 都能再次正常保存。包含 Event-only 成员。
3. **部分 body 准备后的失败。** 第一成员 ready、第二成员 preparation 抛出；验证 staging/ready 混合清理及相同活请求重捕当前值。成功后重用旧请求、单独 Event 推进后重用旧 State 请求、Dispose 后同 Branch/Base 新 WC 都拒绝。
4. **生命周期窗口。** Capture 回调尝试 Repository/资源/其他参与 WC 重入与 Dispose；四类 Store/owner/backing 失效反例迁入组，断言下一 Capture 或 body 尚未执行便拒绝。body 回调失效也不得进入 append/publication。
5. **真正组发布故障。** 有/无 Catalog 增长均覆盖旧组或整组新值；明确 NotPublished、Unknown、Published 与 fault。注入第二成员安装故障、确认后返回映射缺项，必须 Published＋全 owner 停用，不返回半组。

其中 1–4 原可在旧目录布局做内部生命周期见证；§7/§8 已取得部分原语证据，剩余项与 5 一并在新实际内核验收，不再扩张旧宿主。两条线共享上述状态机与候选材料约定，不造适配器维持两套长期产品布局。单成员回归继续验证已有 Capture 后检查、准确来源和 Event/Discard；根构建和相关 tests 是实施片门禁，普通包/进程中止另行记录资格。

## 7. 已运行的候选生命周期见证

新增 [GroupCandidateLifetimeTests](../../../tests/DurableGraph.Persistence.Tests/GroupCandidateLifetimeTests.cs)，复用实际 `SplitBusinessWorkspaceTests` 夹具，3个 Fact 验证：第二 Capture 回调失败后，第一 staging 候选不能直接 Dispose、可以 Abort 并重捕；ready Event/staging State 混合 Abort 不安装 Event 身份或改变 State 基线；已安装候选重复 Abort 不撤销 accepted 身份和基线，后续同值准备无多余对象记录。覆盖零追加断言及同 CLR Event 根重捕得到新 ID，明确失败不是编号回滚。

主线程核对测试及其真实原语；显式使用 DG 固定 Storage `731eef7` 的独立 worktree，根 `dotnet build DurableGraph.slnx` 零警告/错误。过滤 `SplitBusinessWorkspaceTests|SharedCatalogGroupPreparationTests|BusinessSaveCandidateInstallationTests` **15/15** 通过，含本轮新增3例，零失败/跳过。日志为 `.artifacts/db096-convergence/root-build.log`、`mechanism-tests.log`，TRX 为 `results/mechanism-tests.trx`。命令沿用[固定依赖验证方式](shared-schema-only.md#5-本轮基线验证与限度)。

这仅为实际候选清理/安装原语及既有共同编码的限定资格；第三例直接追加并安装内部图，没有声称已经发布。尚未实现仓库组协调器、公开 request、完整混组安装或新业务 Head 原子发布，未跑全套、包或进程中断门禁。产品 C# 与固定依赖本轮保持。

独立复核核对恢复边界、请求资格、HS 确认后返回失败路径及本轮测试/Probe 的断言范围，未发现阻断；补齐整次调用图稳定、单 State 到根、旧 ForkShared 与新 ForkBranch 的校验区别，以及清理异常不得绕过 outcome 分类。集成的14份 Markdown 共601个本地链接、55个锚点检查通过，`git diff --check` 通过；这些是文档集成检查，不扩大运行资格。

## 8. 真实混组发布与冷续写见证

2026-10-19，从 DG `35ae718` 新增 [GroupPublicationMechanismTests](../../../tests/DurableGraph.Persistence.Tests/GroupPublicationMechanismTests.cs)。两个已有 State 基线分别准备 State List Delta 与新类型 Event；共同 Catalog 接纳显式声明及 Event 的新类型。只调用一次真实 `PublishHeads`，发布旧业务目录 D 与 Schema S，然后安装 Catalog/State、丢弃 Event 候选。断言 Event 的 State 实例、source、baseline、对象 head/H 不变。

完全关闭 owner 后重开，实际解码目录、共同 Schema 和 Event；两条线分别通过现行 `BranchSaveSession` 再保存，检查真实 Delta、准确 prior/source/H，最后再次全关冷读。Event 所在线的后续 State 以 Event 为逻辑 Parent、以原 State 为内容 source，验证二者确实分开。

直接 HS 发布没有安装旧 `RepositoryResources` 的目录缓存，因此该 owner 随即关闭，不能再通过它保存。本测试证明可组合原语，不是 Repository 组协调器、公开 request、全部 WorkingCopy Base 安装或一 Branch 一 Head 的运行资格。State 侧此例使用已有根布局并加入显式声明，不能称为两成员都捕获了新根类型。

确认后安装故障复用现有 [MetaBootstrapP0PublicationTests](../../../tests/DurableGraph.Persistence.Tests/MetaBootstrapP0PublicationTests.cs) 的 `Confirmed_install_failure_keeps_both_roots_and_invalidates_old_owner`：真实第二安装回调抛错，Published、owner fault，冷开双根已更新。没有另造测试状态机，也不据此宣称新组故障资格通过。

主线程显式使用固定 `731eef7` worktree，根 build 零警告/错误；筛选 `GroupPublicationMechanismTests|SplitBusinessWorkspaceTests|SharedCatalogGroupPreparationTests|MetaBootstrapP0PublicationTests|P0NormalizedBusinessBaselineTests` **27/27** 通过、零跳过。日志 `.artifacts/db096-next/root-build.log`、`mechanism-tests.log`，TRX `results/mechanism-tests.trx`。其中仅混组 Fact 为本轮新增；其余为实际重跑的既有回归。独立复核核对完整资源关闭、source/Parent 区别及断言范围。

**旧布局原语见证到此收束。**后续[来源 snapshot 接缝 A 已实施验收](history-source-seam.md#7-a-实施与验收)，下一片转入新历史布局，再接入真实组内核。§6 的重入、清理错误、组 Base 安装、Unknown/Published 和多业务 Head CU 判据仍保留，随真实内核验收；不为提前满足清单而复制旧协调器。本节混组见证当轮未改产品 C#、依赖或格式，未跑全套、包、进程中止或新 HS 产品资格。

本轮集成复核：10份受影响 Markdown 的557个本地链接、58个锚点检查通过，`git diff --check` 通过。续工入口、路线图和设计索引已改为 A→B→新布局组内核，不重复开启本轮已完成的原语见证。

## 9. 新布局实际内核的施工接点

源码调查基线 `c9ad935`；[定向 origin 接入](targeted-origin-integration.md)已提供下一片所需的查询基础。这里记录施工建议，不构成组 API 或故障资格。下一片直接接通 `CreateStateCommitRequest` / `CreateEventCommitRequest` → `CommitBranches`，正常返回 BranchId→CommitInfo；公开合同仍以[联合提交设计](multi-branch-commits.md)为准，不再增加旧布局 Probe。

| 当前源码事实 | 最小改动方向 |
|---|---|
| `BranchSaveSession` 各自加载 Catalog，`Prepare` 先 Rebase，再 Capture→Catalog→body | accepted Catalog 归 `RepositoryResources`，薄 facade 取同一 view；按 SharedRoot 惰性载入，发布后才接受。各 session 只保自己的业务身份、来源和模型环境；新根创建也使用共同 Catalog |
| `SchemaCatalogWorkspace.PrepareOwned` 已做完整闭包、accepted 冲突检查和一次 Builder | 先 Capture 全员，再将所有布局一次交给现有准备原语，逐业务图使用同一 view 编码；不重写描述符分配器，不刷新旧 WC 的读 Schema/执行环境 |
| `PendingRepositorySave` 只有一个业务成员；资源 owner 已有唯一 pending/prepare/commit 窗口 | 改为一个具体 pending，包含共同 baseline/Catalog 与成员数组；公开组仅收既有 Branch。单成员即时提交复用该内核，内部新根保留原有 Head 创建及 D/S 登记分支，不扩成批量创建事务 |
| `CommitSave` 已把发布确认、安装和 deliver 包在 outcome catch 内；公开 deliver 推进 session/Base | 一次发布所有 B 和必要 S，普通保存不更新 D；先备齐安装材料/结果槽，确认实际返回键集和 ID，再安装全部 State/Base。Event 仅推进 Base，保留 State/source/rewrite |

入口先设 Repository busy，再枚举输入；资源准备 guard 同样覆盖输入枚举至内容准备，防止用户枚举器或回调经借入 facade 重入。现 `RequireCheckoutWriter` 会经 `RequireAvailable` 拒绝 busy，不能在已进入组操作后逐项直接调用；应复用资源写权限及 `RequireCheckoutSource` 核心检查。每次枚举、Capture、body 后检查资源，失效后不得继续下一成员。

固定成员槽立即接管每个成功 Capture，退出时逐一 `AbortPreparation`，覆盖 staging/ready，已安装者无操作。清理须尽力完成并纳入统一 outcome 分类，不能在 catch 外的 finally 覆盖 Published/Unknown。现 `RepositoryPublicationException` 只有单成员位置；实施时需交付全组 Branch、expected Base 和已知实际 ID 的失败信息，不能只留最后一项或猜造 ID。具体成员信息形状和故障注入接点仍待实施时确定；不新增 Consumed、持久 GroupId、兼容层或恢复权威。

测试迁移注意：历史 Schema/rewrite 测试通过内部 `WorkingCopy.Catalog.Set` 注入显式声明，例如 `DB080MaterializationRequirementTests`、`DB081HistoricalCertificateTests`。保留这些反例的含义，将必要声明改为操作内内部输入/fixture；不要为少改测试保留 per-WC dirty 平台。独立 `SchemaCatalogWorkspace` 原语测试仍可直接使用该类型。

验收以 §6 为准，落到真实 API 时至少包括：两成员各引入新 Schema 的 State/Event 混组及冷续写；第二 Capture、跨成员 Schema 冲突、第二 body 失败后的零追加和重捕；空组/重复/外国 owner/旧 Base/已 Dispose 在首次 Capture 前拒绝；输入枚举及回调的重入/资源失效；有无 Catalog 增长的组发布失败、第二成员安装失败和确认后缺结果。单 State/Event 与新根登记继续跑原回归，确保共核没有改变原先的 source、目录与故障边界。源码检查证明接点存在，不代替这些尚未运行的全组反例。
