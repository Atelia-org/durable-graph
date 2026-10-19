# DB-096 设计续接工作集

> 更新：2026-10-19。**下一轮先完整读本文，再按问题读链接；无需重读整段对话或全部历史草案。**
> 本文是本设计问题的活动工作集；产品实现能力仍由 [src/PROJECT-STATE](../../../src/PROJECT-STATE.md)维护。当前用户决定、源码和实际证据优先于旧草案。

## 目标与当前停点

为 DG 重新设计 Branch、Commit 与 WorkingCopy 的 API；世界线是应用称呼，准确存档位置在新外观中称 Commit。先以实际下游故事选择对外能力，再与存储实现取舍。用户明确 DG 现有 publish 设计/实现和 GraphEnvelope.Parent 是可重写草稿，无兼容包袱；不要为保留旧形状建立双 Parent 或过渡兼容层。

上游已交付引用初始化 fork、紧凑 ID codec 和准确 Entry 起点历史；同库操作经独立消费者验证。已接受其开发期跨库误用检出目标，撤回每引用附加16B scope的建议。**用户认可当前 API 的清晰性与功能覆盖，下一阶段转向可行性验证、效率与简化，收窄设计；尚未开始新模型实施。**

## 已确认的需求与边界

- **2026-10-19 用户已决定：一个 Repository 只有统一的 Schema Catalog（原 Shared 路线），移除 Private 演进和 ToShared 推广；开发草稿在独立库副本中试验。**历史图/batch 的准确 Schema、正常版本升级和完整关系校验继续保留。删除实现的资格见[独立切片](private-removal.md)。
- DramaBoard 多根、多层世界线森林；应用主动在关键点分叉；不要求 merge 或内容寻址。
- 父子线都能继续。首版原线只追加，旧存档另开新线，不做原线 rewind/任意重定位。
- 标签可重复，相对固定；可改更好，但首版固定可接受。不要求 Git 式唯一名字或按标签打开。
- fork 成功即有独立身份/标签，可列出、预览、冷重开继续；无需先产生新业务存档。
- 从 B 的历史中选中实际属于 A 的 a1 创建 C，C 挂 A 下；不保留“从 B 界面发起”作为树父关系。
- 标签归世界线；摘要归准确存档、每次保存共同发布；缩略图按需读，地址不等于完整内容已经可用。
- 现实世界线规模预计不超过1000；先选根、看近期路线、通常继续上次进度。单线历史很长，可每分钟保存。
- durable LLM Agent tool-loop 是重要故事，RL Gym 是远期设想。纯 State 连续保存可用，Event 可选；不强制交替、不自动重放工具、不提供外部副作用 exactly-once。
- 不注册业务模型浏览不是硬要求，先顺应实现。避免为此预建另一套打开协议。
- 已同意在设计加入 ListRootBranches 与 ListBranchSubtree（包含起点），只列 DG 正式业务成员。多根组织和多分支共同提交是不同能力。

## 当前设计认识

1. **写入目标与准确提交位置分开。** BranchId（对应 HeadId）决定继续哪个 Branch；CommitId（对应 HeadEntryId）决定查看、加载或从哪点 fork。空 C 的 current 可以是 A/a1，但首次保存仍写 C。上次世界线存前者，固定书签存后者。
2. **首选继承 head-history。** 在“业务 Head 只发布真实 State/Event、原线只追加、管理属性在链外”的条件下，不额外存逻辑 Parent。仍是首选设计候选，未冻结 wire/API。
3. **不为 fork 制造存档。** 新 C 只有固定来源引用，首次新保存才产生 c1；同值真实保存仍有不同 Entry 身份。
4. **紧凑 ID 足够当前目标。** HeadId编码4B、HeadEntryId编码12B；跨库误用是开发期概率诊断。随机独立key、目标有N个Head时，误命中已有Head约N/2^32（保零有极小修正）。1000个Head约四百二十九万分之一；固定库对内相关，不是每次独立抽奖。不要重新默认附16B强scope。
5. **仓库由调用方选择。** 值可无owner编码/解码，相等不代表全局身份。必要的仓库选择信息可放在应用配置/集合外层，不默认重复到每个引用。
6. **位置校验与业务解释分层。** HS检查记录和发布资格；DG检查正式业务成员、Kind、准确图与Schema。历史Schema不能取当前shared替代，CommitId 也不必携带全部 meta Heads 快照。
7. **分页按准确 Commit 起点。** ReadHistoryPage 使用 AtOrBefore/Before；底层 inclusive 重读上页最后处理项并跳过该项。筛选 State 时 Continuation 仍取最后扫描的 Commit。完成、停止、预算不足和错误分开；没有新项的预算失败不保证原样重试能推进。

## 当前焦点与下一步

**当前停点：B1 新 Storage 源码接入已验收，B2 整体切换计划已完成独立复核（2026-10-19）。**本轮起点 `2d118da`；依赖已接到 `f8868b5`，根构建、2817项测试、初始化3窗/保存6窗通过，准确源/包及故障边界只查[B1 记录](storage-handle-adaptation.md)。[B2 施工计划](branch-cutover-plan.md)补齐书签 CommitId、现有历史调用迁移、Unicode 重复标签、MetaPrevious 和合法目录复用；不新增第二布局或反查索引。产品仍是旧业务目录/Parent、每 checkout 内部 Catalog，尚未实施新 Branch API 或组内核。A/Private 删除资格保各自原版本记录。

此前[首批机制调查](mechanism-feasibility.md)保留源码原语、创建/恢复/查询缺口与最小实验；其中 Private/Shared Schema 混组候选已退出，其余证据仍适用。

**压缩后恢复顺序：完整读本文 → [B2 整体切换计划](branch-cutover-plan.md) → 按问题读[Branch 纵切](branch-vertical-design.md)的来源合同/源码。A 接缝与 B1 依赖接入分别有独立记录，不重做；不重开命名/故事、不再扩展旧目录协调器测试平台。**

当前讨论基线（本目录活动设计以 [API 草图](api-sketch.md) 对齐；历史请求、源码与实验保持原名并注明映射）：

- **Branch / Commit / WorkingCopy** 已形成清晰概念族；Read/List 读描述，Load 加载对象，OpenWorkingCopy(branchId) 取得编辑能力，Create/Fork/Commit 发布。统一 Catalog 不再需要归属参数。准确 LoadState/LoadEvent 与 LoadLatestSavedState 分开；加载对象无分支提交能力，不等于深度不可变。当前源码 Checkpoint/BranchCheckout 尚未改名。
- 根/子树查询已加入设计；历史以 AtOrBefore/Before 和 Continuation 表达准确边界，返回 State/Event 小描述。五个核心调用故事保持可走通，不用减少方法数代替使用便利。
- [联合提交设计](multi-branch-commits.md)已选 CommitRequest：绑定原活副本、准确 Base、根引用与自有摘要，不设 Consumed。健康失败且基线不变可重捕；成功含 Event 后旧请求过期；Unknown/发布后失败停用 owner。输入枚举至结果交付均在操作 guard 内，全部图在整个调用内稳定。具体异常类型/已知结果字段待接入，不再重开请求消费状态或 PreparedCommit。
- [创建方案](application-design.md)推荐 B：DG 登记决定正式成员；先准备 Head，再登记，允许不展示的未登记产物；普通保存不更新目录。上游不可变创建属性 A 留作对照，不作为前置依赖。新根不能假定 CreateHead 参加 PublishHeads。
- [用户故事](user-stories.md)中 C4 密码门、C5 知识神器是验证参考，不要求全部产品化。应用幂等不保证组合不放大；稳定知识身份、独立向前保存与并集建议只约束同一批事实，不改变 DG 提交/重试合同。

### 机制结论与下一片顺序

- **发布：**一个 accepted Catalog＋操作内共同候选。staging/ready 逐成员 AbortPreparation，installed 后无操作。真实混组见证已验证共同编码、一次旧 D/S 发布、State 安装/Event 丢弃、冷续写；直接 HS 发布后必须关旧 owner，不能继续使用其陈旧目录缓存。全组 Base/guard/清理故障仍须真实内核验收。实际 CommitId 确认后才取得，Published 失败也可能拿不到 ID。
- **创建：**三路径登记的公开 HS 成功组合已通过示意 Probe；未登记 Head 冷开隐藏、空 fork 立即可读且不产生 c0。root 的 D/S 可共同发布；实际 DG 图/Schema 与中断资格仍未验证。登记 Unknown/Published 错误保已知候选 BranchId；物理创建 Unknown、尚未尝试登记时 DG 确知未登记。
- **恢复：**已有 StoredGraphRevision 已保留到 decoder/normalized/实际安装，仍由当前 Parent 签发并在保存时复核；不新增 proof/token 或 Graph→Commit 反查。下一片改 HS 签发，CommitId 留在 Repository 层。新布局冷恢复需最近 State 及其严格前源（不足两个到根），保 tip Event 轻量检查。物理 Delta 只核 source 角色与准确 prior，不扩大为递归历史审计。尚无真实新布局运行资格。
- **查询：**描述/fork 只做入口角色检查，内容恢复再验 source/prior/Schema；新 ForkBranch 不是会先恢复对象的旧 ForkShared 改名。Kind 暂从小图 envelope 取得，避免重复存储；成本待测。已写[定向 origin 请求](headstore-origin-request.md)，尚未交付/提交上游。它不阻挡隔离分支的健康纵切，但正式 ReadBranchInfo 合入须满足定向访问边界；不能把全量 adapter 偷换成产品合同，不复制来源权威。
- **分页：**首版选页满成功、工作预算耗尽整次失败，不交付已收集前缀。示意 Probe 已覆盖跨 fork/Before/根后空页/真实部分预算失败；固定旧 anchor 的20项页在较新后缀0/100/10000下冷/热最小预算都45，仅为健康 SingleEntry 计数，不是恒定 I/O 资格。

依赖顺序中 A、B1 分别验收，下一片推进 B2：

1. **A 已完成：来源 snapshot 内部贯穿。**当前 Parent 仍是唯一来源权威；冷读一次完整资格、warm/安装再验证、fixture/owner/lifetime、历史 rewrite 和真实 Delta 均有[回归证据](history-source-seam.md#7-a-实施与验收)。不继续扩成 typed 缓存或免校验证书。
2. **B1：新 Storage 源码接入。**已固定 `f8868b5`，DG 仍用旧历史布局；源码、进程窗口与包资格分开，见[接入记录](storage-handle-adaptation.md)。下一片不重复接依赖；若 origin 新交付改变 revision，再验收相应增量。
3. **B2：单布局整体切换。**按[施工计划](branch-cutover-plan.md)在隔离分支改一 Branch 一 Head、DG 登记、HS 来源签发与业务 Parent 删除；T 书签同步存 CommitId，保 Pair/Checkpoint/事件枚举等已有能力并迁移调用，MoveBranch 因只追加合同退出。先真实纵切，再闭合现有调用与回归后合入；不保两套产品布局。中文重复标签、同值新 Entry/目录复用、Meta Delta 和来源反例纳入门槛。协议号、标签/摘要编码和最小 ID codec 在该片开始时按推荐候选落实。
4. **合流：**在新内核接仓库 accepted Catalog 与组提交，验真正多业务 Head CU、全部 Base 安装/guard/清理及 outcome 故障，再补登记中断、根/子树与分页外观。内部每 checkout Catalog 可留到此时合并，不让它扩大 B2。保留[组内核 §6](group-kernel-design.md#6-下一片的最小判别实验)未验收判据，但不在旧目录复制协调器。summary 的基本 opaque bytes 随 B2，BLOB、ID durable 字段与成本测量后置，不先加索引。

允许略调签名、返回形状和内部组织，以减少重复状态、读取量或资源占用；调整必须仍满足已接受功能和调用体验。有新证据才能重访已排除方案，不以性能猜测牺牲正确性，不把成本转移成应用重复样板代码。不增加合并、原线rewind、GC、跨库事务或通用分布式框架。

## 已知未决项与明确延期

未决：组协调器的真实准备/安装接线及失败验收；用 HS 历史替换 Parent 来签发已有 snapshot；DG 登记中断与异常交付字段；定向 origin 上游接入；BranchId/CommitId 外部 codec 与 DG 字段表示、Event 摘要缺省、附件闭包/错误、加载寿命及历史组合恢复。直接 State 锚点仅在长 Event 成本证据后比较。请求无消费状态与分页预算整次失败已选；Private 新根/混组/promotion 已关闭，不重开冻结快照方案。

延期：rewind、merge、删除/GC、唯一显示名查询、任意摘要字段索引、多writer、跨库交换协议、异步补图、修订旧摘要、RL批量/吞吐专项。1000条线允许先测完整小目录和内存树索引；长历史操作应避免扫描累计H，具体性能指标尚未定。

## 本批完成边界

本轮 B1 的源码全量回归与有限进程窗口资格集中于[接入记录](storage-handle-adaptation.md)；B2 只有源码设计/独立复核，尚无新布局执行资格。此前 A、旧布局混组和独立 HS Probe 保其原资格。不把新 Storage 下旧布局测试视为新 Branch、组内核或普通包交付；本轮没有新 feed/包发布。

## 实现与证据边界

- 本轮 DG 起点 `2d118da`；Storage主工作树仍 `f8868b5`，起点均干净且本轮未改上游。当前 pin 与其一致，根构建/测试仍显式使用干净独立 `f8868b5` worktree。旧 A / Private 删除的 `731eef7` 资格不重写；当前六包候选 `0.1.3-dev.20261019.gf8868b5` 尚未制作 feed，后续包交付必须重新资格化。
- 既有交付复核的上游调查HEAD `3e1dc46`；包源码 `c6fb64a`，包 `0.8.0-persistenthandles-local.20261017105233`。该基线为格式4、HeadId.LocalId已改为owner.GetLocalHeadId，ReadEntryHistory已存在；不是对下一阶段实际HEAD的锁定。
- [交付复核](headstore-delivery-review.md)：独立PackageReference消费者，2个进程、38个断言通过；不是DG产品suite或性能/断电验收。
- [误用检出实验](handle-misuse-detection.md)：实际置换函数穷举相关16,777,216个key，确认存在性检查的额外收益。**最新身份裁决以此为准，旧完整scope建议已退出。**
- 两份实验源码/receipt在本机`.artifacts`，不随Git克隆分发；详细路径见上述记录。上游既有未提交文件不属于本工作，不覆盖、不清理。

## 按需阅读

| 要讨论什么 | 唯一详细入口 |
|---|---|
| 用户明确要求、规模、非目标 | [用户故事](user-stories.md) |
| 统一 Catalog、开发副本证据与 Private 删除片 | [统一 Catalog 设计](shared-schema-only.md) |
| 当前机制缺口、源码裁决及下一批最小实验 | [机制可行性调查](mechanism-feasibility.md) |
| 五种调用流程与定位值职责 | [存档调用故事](save-handle-stories.md) |
| 最小调用面与当前创建协议建议 | [API草图](api-sketch.md) |
| 命名原则、竞争方案、旧名映射与审阅限度 | [命名审阅](api-naming-review.md) |
| 跨分支共同保存、已选请求路线与实验题 | [联合提交设计](multi-branch-commits.md) |
| accepted Catalog、候选生命周期、组安装及下一片判据 | [组内核](group-kernel-design.md) |
| 继承历史来源资格、最小布局与真实恢复纵切 | [Branch 纵切](branch-vertical-design.md) |
| 已完成 A 的 snapshot 接缝、历史权威切换边界 | [来源接缝](history-source-seam.md) |
| B1 新 Storage pin 的源码/运行资格 | [依赖接入](storage-handle-adaptation.md) |
| 下一实施片 B2：单布局、旧调用迁移、合入门槛 | [整体切换计划](branch-cutover-plan.md) |
| 定向读取已登记 Head 的创建来源 | [上游窄请求](headstore-origin-request.md) |
| 标签候选、发布失败、Schema、新根与查询成本 | [应用合同稿](application-design.md) |
| 上游实际交付、API和验收限度 | [交付复核](headstore-delivery-review.md) |
| 原request与其已澄清身份要求 | [持久Handle请求](headstore-save-handles-request.md) |

后续更新本文时替换当前焦点、压缩已完成事项；不要追加轮次流水账或复制完整设计。README继续是问题入口，本文是短续接工作集。
