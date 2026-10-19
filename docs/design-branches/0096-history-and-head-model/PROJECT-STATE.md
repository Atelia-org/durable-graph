# DB-096 设计续接工作集

> 更新：2026-10-19。**下一轮先完整读本文，再按问题读链接；无需重读整段对话或全部历史草案。**
> 本文是本设计问题的活动工作集；产品实现能力仍由 [src/PROJECT-STATE](../../../src/PROJECT-STATE.md)维护。当前用户决定、源码和实际证据优先于旧草案。

## 目标与当前停点

为 DG 重新设计 Branch、Commit 与 WorkingCopy 的 API；世界线是应用称呼，准确存档位置在新外观中称 Commit。先以实际下游故事选择对外能力，再与存储实现取舍。用户明确 DG 现有 publish 设计/实现和 GraphEnvelope.Parent 是可重写草稿，无兼容包袱；不要为保留旧形状建立双 Parent 或过渡兼容层。

上游已交付引用初始化 fork、紧凑 ID codec 和准确 Entry 起点历史；同库操作经独立消费者验证。已接受其开发期跨库误用检出目标，撤回每引用附加16B scope的建议。**用户认可当前 API 的清晰性与功能覆盖，下一阶段转向可行性验证、效率与简化，收窄设计；公开新模型已在隔离分支切换，正式合入尚未完成。**

## 完整交付目标与逐轮方法

用户已将本设计设为持续目标：在无兼容性负担下，实现已接受的 Branch/Commit/WorkingCopy 功能族，补齐多分支原子提交、根/子树目录查询和历史分页，完成旧 API 替代、活动消费者迁移、源码与普通包验收、文档清理及正式合入。只完成其中一片不能宣称整体完成；BLOB、ID durable 字段等明确后置项不自动扩为本目标必经条件。

每轮从本文恢复，按依赖选择可独立验收的小片，记录问题/范围/最小判据；有界委派实施和独立质疑，主线程检查真实产物与实际验证，再更新当前停点、剩余门槛与下一步并按需提交。当前总验收还缺：正式组提交及故障边界、根/子树和分页、最终文档与合入；源码通过不代替包资格，内部机制见证不代替公开 API。

活动消费者迁移及原11个普通包门禁已完成，见[原片资格](package-cutover-validation.md)。[定向来源查询](targeted-origin-integration.md)已完成上游实施、DG 接入和新包增量验收；两个版本的证据不混用。只完成依赖和包闭包不等于0096整体完成。

## 已确认的需求与边界

- **用户已决定：D/T 管理目录优先复用现有 Dictionary 增量持久化，撤回专用整表 codec 的施工选择。**Head 只保存目录图入口；旧容量 Probe 留作对照，不作为产品目录的容量或选型依据。公开 Branch/Commit 合同不变。
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

**当前停点：定向 origin 已完成。**隔离分支 `db096/branch-repository-cutover` 的实现提交 `c9ad935` 已替换全集 adapter：单 Branch 查询只访问目标及直接相关来源，完整列表在已登记集合中检查环。公开 DG 签名与持久格式不变。Storage pin 已到 `70b406c` / `0.1.3-dev.20261019.g70b406c`，上游 `main` 已快进；准确源码、反例及新 S/G 包验收只查[接入记录](targeted-origin-integration.md)。DG 尚未正式合入。

**下一片：公开 CommitRequest、owner 共同 Catalog 和真正多 Branch 提交共核。**先 Capture 全员，再统一准备 Schema、逐成员准备内容，一次发布 B₁…Bₙ 与必要 S；不更新 D。即时单提交复用单成员路径。成功必须推进全部 Base，State/Event 各自基线规则保持；输入枚举、准备、发布、安装、清理和交付都在同一操作边界内。实现接点与最小反例集中于[组内核 §9](group-kernel-design.md#9-新布局实际内核的施工接点)，不得用旧布局原语 Probe 代替正式接口验收。

**压缩后恢复顺序：完整读本文 → 组内核 §9 与联合提交合同 → 当前 BranchSaveSession / RepositoryResources / WorkingCopy 源码。**用当前 pin 的源码根或完整新 feed；旧 `f8868b5` worktree/feed只保其原资格。不要重迁消费者、重做 D 优化或重新等待 origin 交付。

当前讨论基线（本目录活动设计以 [API 草图](api-sketch.md) 对齐；历史请求、源码与实验保持原名并注明映射）：

- **Branch / Commit / WorkingCopy** 已形成清晰概念族；Read/List 读描述，Load 加载对象，OpenWorkingCopy(branchId) 取得编辑能力，Create/Fork/Commit 发布。统一 Catalog 不再需要归属参数。准确 LoadState/LoadEvent 与 LoadLatestSavedState 分开；加载对象无分支提交能力，不等于深度不可变。隔离分支已采用新身份和 WorkingCopy；Checkpoint 保留复合恢复职责，位置改为 CommitInfo。
- 根/子树查询已加入设计；历史以 AtOrBefore/Before 和 Continuation 表达准确边界，返回 State/Event 小描述。五个核心调用故事保持可走通，不用减少方法数代替使用便利。
- [联合提交设计](multi-branch-commits.md)已选 CommitRequest：绑定原活副本、准确 Base、根引用与自有摘要，不设 Consumed。健康失败且基线不变可重捕；成功含 Event 后旧请求过期；Unknown/发布后失败停用 owner。输入枚举至结果交付均在操作 guard 内，全部图在整个调用内稳定。单成员异常的目标/ExpectedBase/实际 CandidateCommit 字段已接入；全组传播待验收，不再重开请求消费状态或 PreparedCommit。
- [创建方案](application-design.md)推荐 B：DG 登记决定正式成员；先准备 Head，再登记，允许不展示的未登记产物；普通保存不更新目录。上游不可变创建属性 A 留作对照，不作为前置依赖。新根不能假定 CreateHead 参加 PublishHeads。
- [用户故事](user-stories.md)中 C4 密码门、C5 知识神器是验证参考，不要求全部产品化。应用幂等不保证组合不放大；稳定知识身份、独立向前保存与并集建议只约束同一批事实，不改变 DG 提交/重试合同。

### 机制结论与依赖顺序

- **已完成基础：**来源 snapshot 接缝 A、Storage B1、B2 新布局/外观、原回归迁移、D 查询投影、活动消费者与定向 origin。各片只证明其实际版本，历史记录不作为未完成工单。
- **组提交优先：**一个 owner accepted Catalog、操作内共同候选和具体成员数组。staging/ready 逐成员 AbortPreparation，installed 后无操作；确认实际 CommitId 后才填结果。全组 Base、reentry、清理及 outcome 必须在新内核验收。现每 checkout Catalog 的移除属于该片，旧工作副本执行模型环境不能随 Catalog 增长刷新。
- **创建：**新根/空 fork/Tag 已接 D/T Dictionary，成功后接受；普通保存只发布 B 或 B/S。登记前中断已有有限见证，组内核重构须保创建回归，并补剩余登记失败窗口。新根不能假定 CreateHead 参加 PublishHeads。
- **树与历史页：**定向 origin 门槛已闭合；接下来补 ListRootBranches/ListBranchSubtree 和公开分页。历史固定准确 anchor，Before 排除重复首项；页满成功、工作预算耗尽整次失败。既有 Probe 只证明有限健康计数，不是完整 I/O 资格。
- **最终交付：**完成以上功能、故障边界和调用示例，重核源码与普通包门禁、文档/旧 API 清理（包括 src/PROJECT-STATE 仍保留的旧 API 能力细节），再正式合入。新版本必须有自己的资格，不借用前片绿色记录。

允许略调签名、返回形状和内部组织，以减少重复状态、读取量或资源占用；调整必须仍满足已接受功能和调用体验。有新证据才能重访已排除方案，不以性能猜测牺牲正确性，不把成本转移成应用重复样板代码。不增加合并、原线rewind、GC、跨库事务或通用分布式框架。

## 已知未决项与明确延期

待闭合：真实组提交及全组 Base/清理/outcome、根/子树、分页、登记故障矩阵的剩余窗口，以及最终文档/正式合入。定向 origin 和活动消费者迁移已经完成。打开/保存重复最近 State 扫描仍为有计数反例才启动的效率候选；ID durable 字段、附件和专项成本测量明确后置。

延期：rewind、merge、删除/GC、唯一显示名查询、任意摘要字段索引、多writer、跨库交换协议、异步补图、修订旧摘要、RL批量/吞吐专项。1000条线允许先测完整小目录和内存树索引；长历史操作应避免扫描累计H，具体性能指标尚未定。

## 实现与证据边界

- 当前定向 origin 的源码、测试、S/G 来源、包资格及 Git 边界见[接入记录](targeted-origin-integration.md)。新包只验受影响 Split/Tag 与上游四 smoke；没有把前片11门禁外推到新 G。
- 原 B2 调用闭包、完整根回归及11个普通包消费者见[包迁移验收](package-cutover-validation.md)；来源/范围保持其原版本，后续不重复那次迁移。
- [交付复核](headstore-delivery-review.md)是更早同库 Handle/历史能力的独立消费者证据；[误用检出实验](handle-misuse-detection.md)支持紧凑 ID 的开发期诊断边界，旧强 scope 建议已退出。二者不代替当前产品验收。
- 组提交、根/子树和分页尚未作为公开产品能力交付；未公开发布包、未正式合入 DG。底层普通恢复、完整帧 append-only、owner/fault/lifetime 纪律保持。

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
| B2 单布局、旧调用迁移与剩余合入门槛 | [整体切换计划](branch-cutover-plan.md) |
| 活动消费者/脚本、公开 XML、普通包资格 | [包迁移验收](package-cutover-validation.md) |
| D/T Dictionary 复用、固定读取边界和管理候选 | [目录持久化](directory-persistence.md) |
| 定向读取已登记 Head 的创建来源 | [实现与验收](targeted-origin-integration.md)；[原请求](headstore-origin-request.md) |
| 标签候选、发布失败、Schema、新根与查询成本 | [应用合同稿](application-design.md) |
| 上游实际交付、API和验收限度 | [交付复核](headstore-delivery-review.md) |
| 原request与其已澄清身份要求 | [持久Handle请求](headstore-save-handles-request.md) |

后续更新本文时替换当前焦点、压缩已完成事项；不要追加轮次流水账或复制完整设计。README继续是问题入口，本文是短续接工作集。
