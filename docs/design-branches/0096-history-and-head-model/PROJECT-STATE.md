# DB-096 设计续接工作集

> 更新：2026-10-18。**下一轮先完整读本文，再按问题读链接；无需重读整段对话或全部历史草案。**
> 本文是本设计问题的活动工作集；产品实现能力仍由 [src/PROJECT-STATE](../../../src/PROJECT-STATE.md)维护。当前用户决定、源码和实际证据优先于旧草案。

## 目标与当前停点

为 DG 重新设计历史、世界线/分支、存档定位及预览 API。先以实际下游故事选择对外能力，再与存储实现取舍。用户明确 DG 现有 publish 设计/实现和 GraphEnvelope.Parent 是可重写草稿，无兼容包袱；不要为保留旧形状建立双 Parent 或过渡兼容层。

上游已交付引用初始化 fork、紧凑 ID codec 和准确 Entry 起点历史；同库操作经独立消费者验证。最后一轮已接受其开发期跨库误用检出目标，撤回每引用附加16B scope的建议。**没有待上游补强身份的阻塞；接下来回到 DG 功能/API 设计，尚未开始新模型实施。**

## 已确认的需求与边界

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

1. **写入目标与准确存档分开。** HeadId 对应继续哪条线；HeadEntryId 对应查看、恢复或从哪点 fork。空 C 的 current 可以是 A/a1，但首次保存仍写 C。上次世界线存前者，固定书签存后者。
2. **首选继承 head-history。** 在“业务 Head 只发布真实 State/Event、原线只追加、管理属性在链外”的条件下，不额外存逻辑 Parent。仍是首选设计候选，未冻结 wire/API。
3. **不为 fork 制造存档。** 新 C 只有固定来源引用，首次新保存才产生 c1；同值真实保存仍有不同 Entry 身份。
4. **紧凑 ID 足够当前目标。** HeadId编码4B、HeadEntryId编码12B；跨库误用是开发期概率诊断。随机独立key、目标有N个Head时，误命中已有Head约N/2^32（保零有极小修正）。1000个Head约四百二十九万分之一；固定库对内相关，不是每次独立抽奖。不要重新默认附16B强scope。
5. **仓库由调用方选择。** 值可无owner编码/解码，相等不代表全局身份。必要的仓库选择信息可放在应用配置/集合外层，不默认重复到每个引用。
6. **位置校验与业务解释分层。** HS检查记录和发布资格；DG检查正式业务成员、Kind、准确图与Schema。历史Schema不能取当前shared替代，存档handle也不必携带全部meta Heads快照。
7. **分页按准确Entry起点。** inclusive重读上页最后处理项并跳过该项即可；筛选State时继续位置仍取最后扫描的Entry。完成、停止、预算不足和错误分开；没有新项的预算失败不保证原样重试能推进。

## 当前焦点与下一步

已形成[最小公开 API 草图](api-sketch.md)，用[五个调用故事](save-handle-stories.md)逐项走通：

1. 继续上次世界线；固定存档书签。
2. 读当前预览；从准确旧点分页选项。
3. fork 后立即可见、关闭重开；父子独立续写。
4. LLM State恢复，以及 Event-first/最近State的边界。

经[命名审阅](api-naming-review.md)，后续草图推荐 **Branch / Commit / WorkingCopy**：持久追加入口、一次业务 State/Event 保存记录、绑定分支的内存工作副本。Line/Save 是旧故事占位；当前源码 Checkpoint/BranchCheckout 不因设计文档更新而已改名。推荐薄角色包装仍不增加持久字节。

调用约定为 Read/List 读描述、Load 加载对象、OpenWorkingCopy 显式选择 SchemaMode、Create/Fork/Commit 持久发布。准确 LoadState/LoadEvent 与 LoadLatestSavedState(atOrBefore) 分开；加载结果没有分支提交能力，不等于领域对象深度只读。最近 State 查询返回选点与实际 State 来源，不重放 Event。历史采用 AtOrBefore/Before 和 Continuation，返回全部 State/Event 小描述；目录先完整读小集合并在内存建树。推荐词汇用于后续讨论，签名、对象寿命/可变性和部分失败合同仍未冻结。

**下一件事：证明世界线标签随成功创建一起可见的协议。**

- A：上游 Head 创建属性，首版不可变；概念直接，但当前上游未承诺这个能力。
- B：DG 独立登记，只有登记成功的 Head 是正式世界线；先建 Head 后登记，失败可留下不展示的未登记产物。不要求GC或自动修复。
- 不把标签放进继承的a1，也不为标签写假业务Entry。普通fork与新root分别推演；新root首次引入Schema时，不能假设CreateHead天然参加PublishHeads事务。

当前推荐 B：其登记与必要 shared Schema 可形成共同的 DG 可见发布点，普通保存无需更新登记目录。尚待证明三条路径（fork、新root无Schema增长、新root有shared增长）的耐久顺序、结果不明时的发现，以及按实际payload/标签上限核算1000条线容量；不先假定整表方案已合格。A保留为对照，未向上游提出新增创建属性要求。

随后细化存档描述/重建依赖、summary/BLOB接口、Shared/Private编辑意图与故障结果，最后安排DG依赖接入和产品实施。分页部分结果、Private新根和精确来源解析仍须收敛，不把伪API当成已有能力。

新增研究分支：[多 Branch 联合提交候选](multi-branch-commits.md)。用户提出 WorkingCopy 准备、Repository 统一发布；现推荐先以轻量 CommitRequest 组合，在 CommitBranches 内统一捕获/协调Schema，不先暴露冻结快照的长寿命 PreparedCommit。命名候选 CreateStateCommitRequest/CreateEventCommitRequest；原 CommitState/Event 仍立即执行。首要机制题是两个Shared成员同时引入Schema，不得各自准备后覆盖其中一份。范围和准确失败合同未冻结，也未批准施工。

[用户故事](user-stories.md)新增 C4 密码门（物品可迁移、世界不回溯）与 C5 知识神器（可选旧路线、只迁移幂等且不遗忘的知识）。均为用户提供的参考故事，尚不承诺全部支持。C5 可先映射旧点另开，不自动引入原线rewind；物品所有权和知识单调性归应用。

## 已知未决项与明确延期

未决：标签A/B、新root与shared Schema发布、是否/怎样包装紧凑ID、DG序列化定位值的标量适配、Event摘要缺省语义、图片错误展示规则；联合提交的捕获时机、组内Schema、基线安装、结果未知与历史组合恢复。最近State/同类内容来源必须有恢复方案，不能删除GraphEnvelope.Parent后遗失重建依赖。

延期：rewind、merge、删除/GC、唯一显示名查询、任意摘要字段索引、多writer、跨库交换协议、异步补图、修订旧摘要、RL批量/吞吐专项。1000条线允许先测完整小目录和内存树索引；长历史操作应避免扫描累计H，具体性能指标尚未定。

## 实现与证据边界

- DG固定依赖仍是旧 `731eef7`，新模型尚未接入；本机 source mode 环境变量可能指向更新的 sibling。**实施前重新核对HEAD、pin、环境和工作树，不沿用旧build结论。**
- 最新只读上游调查HEAD `3e1dc46`；新包源码 `c6fb64a`，包 `0.8.0-persistenthandles-local.20261017105233`。实际格式4、HeadId.LocalId已改为owner.GetLocalHeadId；新ReadEntryHistory已存在。
- [交付复核](headstore-delivery-review.md)：独立PackageReference消费者，2个进程、38个断言通过；不是DG产品suite或性能/断电验收。
- [误用检出实验](handle-misuse-detection.md)：实际置换函数穷举相关16,777,216个key，确认存在性检查的额外收益。**最新身份裁决以此为准，旧完整scope建议已退出。**
- 两份实验源码/receipt在本机`.artifacts`，不随Git克隆分发；详细路径见上述记录。上游既有未提交文件不属于本工作，不覆盖、不清理。

## 按需阅读

| 要讨论什么 | 唯一详细入口 |
|---|---|
| 用户明确要求、规模、非目标 | [用户故事](user-stories.md) |
| 五种调用流程与定位值职责 | [存档调用故事](save-handle-stories.md) |
| 最小调用面与当前创建协议建议 | [API草图](api-sketch.md) |
| 命名原则、竞争方案、旧名映射与审阅限度 | [命名审阅](api-naming-review.md) |
| 跨分支共同保存、请求/冻结候选与实验题 | [联合提交候选](multi-branch-commits.md) |
| 标签候选、发布失败、Schema、新根与查询成本 | [应用合同稿](application-design.md) |
| 上游实际交付、API和验收限度 | [交付复核](headstore-delivery-review.md) |
| 原request与其已澄清身份要求 | [持久Handle请求](headstore-save-handles-request.md) |

后续更新本文时替换当前焦点、压缩已完成事项；不要追加轮次流水账或复制完整设计。README继续是问题入口，本文是短续接工作集。
