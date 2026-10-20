# DB-096 设计续接工作集

> 更新：2026-10-20。完整目标已验收并合入本地master；后继Echo Garden接入配方已实施验收，产品合同保持。先读本文，再按问题读取链接；当前用户决定、适用AGENTS、源码和可执行证据优先于旧草案。产品能力入口为[src/PROJECT-STATE](../../../src/PROJECT-STATE.md)。

## 目标与当前停点

在无兼容性负担下，Branch/Commit/WorkingCopy已替代旧公开API；多分支原子提交、根/子树目录、历史分页、活动消费者迁移、源码与普通包资格、文档收尾及正式合入均完成。[最终交付验收](final-delivery-acceptance.md)保存完整要求映射、准确版本和证据限度。

产品/测试来源为`440bccf`，本地master已从`866ce89`快进至该提交，后续仅文档收尾。最终根回归2992项全通过；12个活动普通包runner及候选导出通过。没有推送或公开发布包。旧分片的G/S记录保原范围，不与最终版本混用。

## 完整交付目标与逐轮方法

本期完整目标已闭合，不再把已交付能力列为下一片施工。后续增量仍从工作集恢复，依据实际消费者或计数反例选择一个可独立验收的小片，明确问题、边界与成功判据，有界委派调查/实施/质疑，主线程核对真实产物与测试，更新状态和剩余事项并按需提交。没有新证据，不重开已排除机制或扩大明确后置需求。

## 已确认的需求与边界

- 一个Repository只有统一Schema Catalog；Private/ToShared已删除。开发异形草稿使用完整关闭库的独立副本，历史图/batch仍保准确Schema和正常升级。
- 多根世界线森林；父子线均可继续，原线只追加，旧存档另开新线。首版无rewind、merge或内容寻址。
- DisplayLabel可重复、首版创建后固定；不充当身份、唯一名称或打开凭证。Summary归每次准确Commit，随业务提交共同发布。
- Fork成功即正式可列出、预览、冷重开继续，不制造业务Commit。从B历史选实际属于A的a1创建C，C挂A下；空C承接A/a1，首次保存写C。
- 纯State或纯Event都合法，不强制交替、不自动重放工具、不承诺外部副作用exactly-once。
- 现实世界线预计不超过1000、单线历史可能很长；先选根、聚焦近期路线。RL Gym为远期设想，尚无吞吐/规模资格。
- D/T管理目录复用现有Dictionary增量序列化，Head保存小入口；旧整表codec容量Probe只作历史对照。

## 当前设计认识

1. **写入目标与准确位置分开。**BranchId为4B，CommitId为12B薄角色类型；OpenWorkingCopy取得写入能力，Load只恢复内容。调用方选择repo；紧凑ID的跨库误用检查是开发期概率诊断，不附16B强scope，也不宣称全局身份。
2. **继承head-history是当前实现。**业务Head仅追加真实State/Event，管理属性链外；DG不另存逻辑Parent。内容恢复通过历史选择准确source，描述读取不因此恢复业务模型。
3. **登记是正式成员的权威。**先准备物理Head再发布D；未登记产物不展示。普通提交只发布B或必要B/S，不更新D；新根不假定CreateHead能参加PublishHeads。
4. **多分支共用一次Catalog候选和发布。**CommitRequest绑定原活副本、准确Base、根引用和自有摘要，无Consumed状态；guard覆盖输入枚举与整个操作。所有图调用内稳定，成功推进全组Base；Unknown或发布后失败停用owner。
5. **树查询检查正式森林。**ListRootBranches与包含起点的ListBranchSubtree只列D登记成员；真实来源来自定向Head origin，不增加第二份持久父关系或索引。
6. **分页以准确位置为anchor。**AtOrBefore/Before，返回所有State/Event描述；应用筛选后仍使用扫描Continuation。Before验证并跳过anchor，页满可成功后再遇空末页；预算、取消、清理失败不交付前缀。HS工作预算不是总I/O或内存预算。
7. **既有持久纪律保持。**正常backing尾恢复、完整帧append-only、owner/fault/lifetime检查均保持；不用全库启动审计替代按需恢复。

## 当前焦点与下一步

**本期无剩余必经施工或验收门槛。**API草图保设计合同和历史推演意义，真实签名查源码；不从早期“下一批实验”重启设计。

2026-10-20的[Echo Garden反馈研究](echo-garden-feedback.md)已形成[消费者接入配方](application-recipes.md)并完成源码与普通包验收：导航DTO文本适配、创建成功后打开失败的应用切换流程、版本化Summary。入口为[短指南](../../../experiments/PackageConsumerProbe/ApplicationRecipesConsumer/README.md)；三项helper与两进程故事、真实故障源码测试均已交付。不新增核心文本codec、ForkAndOpen或summary factory；本片无剩余必经施工，下一增量等真实消费反馈。

性能优化仍须有真实成本反例。七字段、8存档的游戏没有提供性能基准；继续保留单次CommitState与全历史的简单用法，长历史应用再使用分页，不建立新缓存/索引框架。

### 机制结论与依赖顺序

来源snapshot接缝、Storage接入、B2布局与调用迁移、D查询投影、定向origin、真实组内核、树查询、历史分页和创建失败验收均已闭合。进程Kill host已迁移并取得6＋3窗口的限定见证。完整最终资格统一查[交付记录](final-delivery-acceptance.md)，各机制记录继续解释其当时版本。

## 已知未决项与明确延期

附件BLOB及其就绪/读取合同、ID durable字段、可编辑标签、创建分支入组、rewind、merge、删除/GC、唯一显示名查询、任意摘要字段索引、多writer、跨库交换/事务、异步补图、修订旧摘要、RL吞吐专项均不属本期交付。后续需求及重访条件集中在[路线图](../../DurableGraph-research-roadmap.md)，不将这些项自动转成新任务。

应用幂等本身不保证跨Branch组合不放大；知识神器故事中的稳定事实身份、独立向前保存与并集合并建议见[用户故事](user-stories.md)，不改变DG提交或重试合同。

## 实现与证据边界

最终根回归是DG源码＋准确Storage包S；普通DG PackageReference是另一组独立验收。主G与README G都来自440bccf，但各有自己的包字节和资格。Meta限friend内核见证；6＋3Kill限实际Windows/进程窗口，不外推掉电或所有多Branch组合；分页预算和目录实现不构成性能保证。

## 按需阅读

| 问题 | 入口 |
|---|---|
| 最终能力、源码/包版本、合入证据 | [最终交付验收](final-delivery-acceptance.md) |
| 实际用户需求与调用职责 | [用户故事](user-stories.md)、[五个调用故事](save-handle-stories.md)、[API草图](api-sketch.md) |
| 命名与历史候选 | [命名审阅](api-naming-review.md)、[应用合同稿](application-design.md) |
| 统一Catalog与Private移除 | [统一Catalog](shared-schema-only.md)、[删除切片](private-removal.md) |
| 布局与来源选择 | [Branch纵切](branch-vertical-design.md)、[整体切换计划](branch-cutover-plan.md) |
| 目录持久化与树查询 | [D/T目录](directory-persistence.md)、[树查询验收](branch-tree-queries.md) |
| 联合提交 | [请求合同](multi-branch-commits.md)、[组内核](group-kernel-design.md)、[验收](group-commit-integration.md) |
| 分页、创建失败与进程恢复 | [分页](history-pagination.md)、[创建故障](creation-failure-acceptance.md)、[Kill见证](process-crash-migration.md) |
| 包消费者与上游接入的历史来源 | [原包迁移](package-cutover-validation.md)、[Storage接入](storage-handle-adaptation.md)、[定向origin](targeted-origin-integration.md) |
| ID身份与上游请求 | [交付复核](headstore-delivery-review.md)、[误用诊断](handle-misuse-detection.md)、[Handle请求](headstore-save-handles-request.md)、[Origin请求](headstore-origin-request.md) |

后续更新应替换当前焦点，保留证据指针，不追加逐轮流水账；README继续作为问题入口。
