# DB-096：一 Branch 一 Head 的最小纵切

> 2026-10-19，设计合同；B2 隔离首段已接真实 Repository 并运行，准确范围见[施工记录 §8](branch-cutover-plan.md#8-b2-隔离分支首段纵切)，原回归与正式合入尚未完成。承接 [API 草图](api-sketch.md)、[应用合同](application-design.md)及[首批机制调查](mechanism-feasibility.md)。最初调查基线为 DG `c077d1c` / Storage `731eef7`；后续[B1 已接入 f8868b5](storage-handle-adaptation.md)。具体调用迁移及单布局合入门槛见[整体切换计划](branch-cutover-plan.md)，不把依赖资格当成新历史布局资格。

## 1. 问题和当前推荐

问题：用继承 Head 历史替换业务 `GraphEnvelope.Parent` 后，能否同时保留空 fork 的可发现性、准确历史恢复和增量再次保存，而不新增一份逻辑父链或 Graph→Commit 反查索引？

推荐将第一次可运行纵切限为：**登记小目录＋业务 Head 小描述＋从继承历史证明最近 State 来源＋同 owner 内传递已验证读取材料**。保持现有对象/batch/directory Delta 与准确 Schema 机制，不同时重写整个序列化层。普通保存与组提交共用准备/安装原则，但本纵切先以一个业务成员验证读写接缝。

可观察闭环：创建真实 State/Event 根 → 保存 → 从准确旧 Event 创建子线 → 关闭全部资源重开 → 列出/预览子线 → 恢复最近 State 或证明尚无 State → 子线首次保存 → 父线继续追加且不改变子线的继承前缀。

这是一份接线与实验合同，不冻结新 wire 编号、字段宽度、全部异常类型或公共 C# 签名。业务布局改动应在正式接入切片统一升级格式并拒绝旧布局；不维护双读协议。

## 2. 最小持久信息：各事实只有一个权威

| 位置 | 纵切需要的事实 | 首片不存的重复信息 |
|---|---|---|
| DG 登记 Head D | 小入口指向增量 Dictionary 管理图，表达正式成员 `HeadId → DisplayLabel` | current、summary、来源副本；普通保存不重写 D |
| Catalog Head S | 当前接受的 Schema 图入口 | 各 Branch 私有写目录 |
| 业务 Head B 的 header | HS 固定来源；根无来源，fork 指向准确来源 Entry | DG 再存一份父 Branch/父 Commit |
| B 的每个真实 Entry | 小描述格式识别、准确业务图入口、该次 summary | 逻辑 Parent、CreatedOnBranchId、重复 SchemaRoot |
| 业务图及对象内容 | Kind、根对象/目录、准确 Schema、batch/directory 的 source 与 Delta prior | 业务逻辑 Parent |

`CreatedOnBranchId` 从真实 Entry owner 派生。空 C 的 current=a1 不写新小描述，不产生 c0，预览沿 a1 读取同一份摘要。首个 c1 才属于 C。

**Kind 暂保留在图 envelope，由小描述读取再访问一个小图帧取得。**这样不用立即冻结 Entry/图双 Kind 一致性。若测得该额外小帧 I/O 是瓶颈，再比较内联 Kind；不能只为少一次 API 调用增设第二权威。摘要在 Entry，图内准确 Schema 保持原位置；缩略图及通用附件 API 不作为本纵切的前置。

原 envelope 的 Parent 在 Meta 上还承担前 Meta 内容来源，不能同业务 Parent 一并删除。实现可为 Meta 保留对应字段、为业务编码移除它，或做同等明确的内部区分；不为字段整齐引入多个公共图接口。D/T 各自的 Dictionary 管理图沿自己的前 Meta 延续，不借用 S 的来源；具体自举与窄读取合同见[目录持久化](directory-persistence.md)。

## 3. 入口资格与内容资格分开验证

所有入口先检查 owner 可用性、DG 正式成员与 HS 发布资格；内部 Head 和未登记产物不能作为用户 Branch 或 Commit。ReadCurrentCommit 还保留调用者指定 BranchId；不能从返回 Entry owner 反推写入目标。

推荐明确两层职责：

1. **描述层**：`ReadCommitInfo`、历史页、fork 来源检查读取准确 Entry，检查 DG 描述格式和被指向图的业务 Kind/基本 envelope 角色；不物化领域对象，不逐项推导最近 State。来源查询检查已登记父成员及森林关系。描述成功不声明全部对象、历史 Schema 和附件健康。
2. **内容层**：`LoadState/Event`、`LoadLatestSavedState`、`OpenWorkingCopy` 与保存恢复，进一步取得所选图的准确内容来源，保留现有 source/prior、Schema、对象关系、模型/Upgrade 与资源寿命检查。

这是有意划分的校验范围，不将坏内容静默当成健康内容。调用内容 API 时仍精确报错；fork 创建成功后，模型或内容加载失败不能改称创建回滚。若以后要求 Fork 预先证明完整可恢复性，应另行命名/界定成本，不暗中让所有 fork 扫描历史和恢复整图。

这是新 ForkBranch / ReadCommitInfo 的独立合同，不是现有 ForkShared 的等价改名。现有 [ForkShared](../../../src/DurableGraph.Persistence/Repository.cs)先 RestoreCheckout；新 ForkBranch 只创建持久入口，编辑恢复由 OpenWorkingCopy 单独承担。

该区分避免两种反向复杂性：历史页每看到一个 Event 都再扫描前史，导致重复工作；从准确旧点 fork 因附带恢复校验而与累计历史 H 绑定。HS 自身的来源和 CU peer 资格检查仍正常进行，不承诺物理 I/O 为常数。

## 4. 删除业务 Parent 后，如何保留恢复证明

### 4.1 三种边界不能混用

| 操作 | 应寻找的 State |
|---|---|
| 资格检查已发布图 X 的内容 source | `Before(X)` 的最近 State，严格祖先 |
| `LoadLatestSavedState(atOrBefore: X)` 选择待加载 State | `AtOrBefore(X)` 的最近 State，可以就是 X |
| 从已接受末端 X 准备新保存 Y | `AtOrBefore(X)` 的最近 State，作为 Y 的 source |

例如 `S0,E1,S1,E2`：从 E2 找到待加载 S1 后，**检查 S1 本身的内容来源还要找到 S0**。不能用 S1 作为自己的 source，也不能只验证 E2→S1 就认为 S1 的内容关系已经验证。首次冷开扫描可以在同一轮历史 visitor 中记下最近两个 State，结束 visitor 后再物化所选 State；不要在 HS 历史回调中递归启动另一轮历史读取。

找到待恢复的最近 State 及其严格前源 State 即可停止；不足两个时必须到根，才能证明唯一 State 没有前源。不是找到第一个 State 就提前成功。

Event-only 只有完整到根才能证明 source=null。扫描预算耗尽、坏 envelope、读取错误必须失败，不能产生空 State 或跳过坏 State。若只加载准确 Event，则验证其 source 关系但不顺便物化 State，不执行 Event。

`OpenWorkingCopy` 还应保留现有 `RestoreCheckout` 对所选 tip Event 的轻量内容关系检查：用找到的最近 State 校验该 Event 的 batch/directory source，但不物化它的业务对象。不能只恢复 State 就遗漏当前 Event 的错误来源；沿途其他 Event 不因此升级为逐个完整图审计。

### 4.2 传递材料，不在每层重查历史

可用最小内部材料是：**同 owner、准确所选图、经过相应边界历史查询取得的 expected State source（或完整到根的无源结论）**。已发布材料还保留 CommitId；尚未发布候选从当前操作的已验证 tip/baseline 派生材料，没有虚构 CommitId。

Repository 是该材料的生产者，物理层继续用它比对图、owned batch、directory creator/source 和准确 prior。材料随 `StoredGraphRevision → DecodedRevision → NormalizedGraphState` 保留；重读验证沿用同 owner 的来源材料，不让任意 FrameAddress 获得保存权限。[接缝调查](history-source-seam.md)进一步推荐直接保留现有 `StoredGraphRevision`，无需另建 proof/token；CommitId 留在 Repository 层，不要求所有物理 DTO 携带。它是内部接线，不是可序列化证明、公开 capability API 或一份全库历史缓存。资源关闭/fault 后保存资格失效，已解码 DTO 内容仍遵循关闭后可读合同。

发布前候选必须同样经过实际追加图、实际 batch/heads、安装成员、Schema 与 candidate source 校验。发布成功前只准备安装，成功后安装 State 基线与准确 CommitId；Event 只推进逻辑末端。发布后安装失败按 owner 整体 fault 处理，不能继续半安装的 WorkingCopy。

写入窗口中 expected tip 未变、baseline 曾按准确历史恢复，之后只由成功 State 安装推进，这些事实足以复用最近 State 结论；无需每次 Event 保存重新扫描到最早 State。未参与或已陈旧工作副本仍必须在新操作入口检查其 expected tip，不能用缓存事实绕过冲突检查。

### 4.3 必须保留的反例与边界

历史为 `S0 → S1 → E2`，而 E2 的图、batch、directory 一致指向 S0：物理字段彼此一致仍然错误。必须以 HS 顺序得出的最近 State=S1 拒绝，不能把“直接锚点可读”当成正确性证明。

现有对象 Delta 与目录 Delta 链检查局部 source 角色和准确 prior，不递归证明全部 source 的领域历史。这次不扩大为全历史审计，也不以保留现有局部检查为由建立 Graph→Commit 反查索引。当前源码接缝是选中图的多次重新读取，详见 §7。

**性能边界**：从准确 Commit 冷恢复需要扫描最近 State 及其前源之间的 Event 段；同 owner、同有效基线的重复保存可复用证明。首先记录该成本，再决定是否需要持久直接 State 锚点。添加锚点仍须说明最近关系的权威/验证，不能因为扫描慢就删去验证。

## 5. 创建与登记：只保留一个 DG 可见点

所有路径先准备标签/描述大小、模型与 Schema 冲突等可提前拒绝条件。D 是正式成员唯一权威；只有完成最终登记才交付成功。下表的具体耐久顺序仍需运行见证。

| 路径 | 内容和物理 Head 准备 | 最终 DG 发布 |
|---|---|---|
| `ForkBranch(a1,label)` | 验证正式来源；HS `ForkHead(a1)` 得到空 B | `PublishHead(D)` |
| 新根，无 Catalog 增长 | 编码初始真实 State/Event；`CreateHead` 创建 B 的首 Entry | `PublishHead(D)` |
| 新根，有 Catalog 增长 | 追加 Meta/业务图，业务准确 pin 新 Meta；创建 B 的首 Entry | `PublishHeads(D,S)` |

新 B 不放入最后的既有 Head CU；否则会多造一个业务 Entry。`CreateHead` 返回的真实初始 Entry 已足够，最终登记负责把它纳入 DG。需要的内容应在登记前完成准备并由底座确认耐久，采用上游 ProcessCrashOnly 语义，不外推到掉电。

| 失败点 | DG 结果 | 后续可做什么 |
|---|---|---|
| 未尝试登记，包括物理创建 Unknown | 正式 Branch 未创建；可能留下未知物理产物 | owner 是否可用按底座/输出故障裁决；不自动回收/认领 |
| 登记未成立 | 正式 Branch 未创建，旧 D/S 保持 | 完整帧保留；不宣称磁盘未变 |
| 登记 Unknown | Branch 是否创建未知；增长时 D/S 应共同成立或共同不成立 | 异常携带已知候选 BranchId，关闭后重开按 D 查询；不用重复标签定位 |
| 登记 Confirmed 后安装/交付失败 | Branch 已创建 | 保留候选 BranchId 与 Published 结果；fault owner，重开后查询；不透明重试 |

创建的候选 BranchId 在物理创建成功后已经得到，故最终登记错误的定位合同可以很小。物理创建本身 Unknown 且未返回 ID 时，不要求伪造候选 ID；DG 已知其尚未登记。这与组提交 Unknown 时尚无新 CommitId 的恢复问题不同，不必强造一种统一结果定位结构。

## 6. 查询和实验顺序

**[来源 snapshot 接缝 A 已实施验收](history-source-seam.md#7-a-实施与验收)**：保留和传递已有实际图材料，仍以当前 Parent 取得来源资格，保格式/公开 API 与热复核；消除同次 decoder 的重复完整图校验。新依赖的[B1 接入](storage-handle-adaptation.md)已完成，下一片按[整体切换计划](branch-cutover-plan.md)切换业务历史权威及相关调用。下列真实纵切和登记判据保持，不以 A 替代新布局验收。

成员目录复用既有 Dictionary 增量持久化，标签先固定；来源仍以 HS header 为准。`ListHeadOrigins` 在当前上游扫描物理全集，包含管理 Head/未登记产物，并验证整个来源图；它能做健康数据的功能见证，**不能证明只访问登记集合或忽略无关损坏的查询边界**。

较小上游增强已整理为[定向 origin 请求](headstore-origin-request.md)：检查指定 header，返回 root 或准确固定来源，不承诺源 Entry 内容健康；DG 检查父成员、循环及子树。无需先加可变 metadata、保留区角色或上游子树索引。若尚未交付，纵切可以明确使用全量 origin adapter 并记录其限制，不把临时 adapter 当生产最终资格。

建议按以下两个见证推进，不一次覆盖全部 API：

1. **恢复接缝见证**：真实 HS Entry/继承历史＋DG 小描述/图；覆盖 `S0,E1,S1,E2`，父线推进 E3 后从准确旧点 E2 fork；冷恢复 S1 时证明其严格前源 S0，子线先 Event 后 State，再次 State 确实写 Delta。另覆盖唯一 S0 与 `E0,E1` Event-only 到根。保留图/batch/directory 一致自报 S0 而 HS 实际最近 State 为 S1 的错源反例，以及错误 prior、selected/origin Schema 和 owner 失效反例。完整判据只查[来源接缝 §6](history-source-seam.md#6-可执行顺序先接缝再一次切换历史权威)，先完成 Parent 替换闭环。
2. **登记见证**：在上述真实读写链上覆盖三条创建路径与各阶段中断；正式成员精确、空 fork 可预览、D/S 共同变化、未登记产物不展示，登记后交付异常能靠候选 BranchId 核对。补多根/子树、从 B 界面选择 A/a1 创建 C 挂 A 的断言。

两者可以共用测试宿主，但不要公开两套 Repository 形状。实验若需要临时编码，标清版本和仅限见证；不得直接把它登记为产品 wire 已冻结。真实 cold reopen 必须关闭全部资源并重新取得 stores/caches；跨进程、故障进程、包消费者资格分别记录。

成本记录至少分开：目录 N 与未登记 U、固定旧起点之后的无关后缀、所选点之前的连续 Event 数、对象 Delta 链。最近 State 查找可随前方 Event 段增长；描述/当前摘要/fork 不应因 DG 附加验证扫描累计历史。额外小图帧读取与打开文件保留数量也要记录，避免以“轻量描述”掩盖 I/O。

## 7. 切换前源码证据与未验证项

以下源码对照描述 B2 之前的基线；本轮已改变的入口和新的限定运行证据以[施工记录 §8](branch-cutover-plan.md#8-b2-隔离分支首段纵切)替代，不能把下述“尚未运行”当成当前停点。

- [GraphRecordStore](../../../src/DurableGraph.Persistence/GraphRecordStore.cs)：`Append`、`ReadGraph`、`ValidateMetadataRelations`、`ResolveContentSource`、`ReadObjectChainCore`、`ReadDirectoryChain`。业务内容来源确为最近 State；batch/directory 的局部 source/prior 检查与逻辑 Parent 不可一起删除。
- [DecodedRevision](../../../src/DurableGraph.Persistence/DecodedRevision.cs) 与 [StoredGraphDecoder](../../../src/DurableGraph.Persistence/StoredGraphBodies.cs) 已经 A 片接入 snapshot，地址入口读取一次选中图并传递；新增内部入口拒绝错误 owner/失效寿命。它仍是 Parent 来源，尚未成为 HS 签发材料。
- [NormalizedGraphState](../../../src/DurableGraph.Persistence/NormalizedGraphState.cs)：`RequireStoredSource`、`PrepareGraphInstallation` 再读实际图并检查 owner、环境、Schema、成员、prior、实际写入；未发布候选不能依赖已有 CommitId。
- [RepositoryResources](../../../src/DurableGraph.Persistence/RepositoryResources.MetaBootstrap.cs)：`PrepareSaveCore` 检查实际最近 State；`CommitSave` 当前在发布前准备安装、发布后安装并按阶段 fault。旧 name→graph 目录不是本稿一 Branch 一 Head 的实现。
- [Repository 恢复入口](../../../src/DurableGraph.Persistence/Repository.MetaBootstrap.cs)：`RestoreCheckout` 先对选中 Event 执行 `ValidateMetadataRelations`，再找最近 State 并物化；上述纵切保留该检查范围。
- 上游 [ForkHead](../../../../atelia-storage/src/HeadStore/HeadStore.Fork.cs)准确重验来源后只写 header；[ReadEntryHistory](../../../../atelia-storage/src/HeadStore/HeadStore.History.cs)起点 inclusive，禁止活动 visitor 递归历史，正常停止不证明未访问历史健康；[ListHeadOrigins](../../../../atelia-storage/src/HeadStore/HeadStore.ForkQuery.cs)扫描完整物理集合。

本稿对应的新 DG 布局未构建/运行；产品仅完成[同格式 snapshot 接缝 A](history-source-seam.md#7-a-实施与验收)，新依赖资格单列于[B1 记录](storage-handle-adaptation.md)，上游源码未改。[独立 HeadStore Probe](../../../experiments/BranchHeadMechanismProbe/README.md)已用示意 payload 验证三条登记成功路径、空 fork、跨 fork 分页及旧起点后缀预算；它不引用 DG，不能替代本稿的准确图恢复与登记故障资格。仍未验证：HS 来源签发及业务 Parent 删除后的真实恢复、三路径登记中断、长 Event 段成本、定向 origin 接入、整目录实际容量/累计写量。以上见证通过后再收敛产品字段、异常交付和实施顺序；现阶段无需新增需求边界询问。
