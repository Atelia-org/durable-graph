# DB-096 设计续接工作集

> 更新：2026-10-18。**下一轮先完整读本文，再按问题读链接；无需重读整段对话或全部历史草案。**
> 本文是本设计问题的活动工作集；产品实现能力仍由 [src/PROJECT-STATE](../../../src/PROJECT-STATE.md)维护。当前用户决定、源码和实际证据优先于旧草案。

## 目标与当前停点

为 DG 重新设计 Branch、Commit 与 WorkingCopy 的 API；世界线是应用称呼，准确存档位置在新外观中称 Commit。先以实际下游故事选择对外能力，再与存储实现取舍。用户明确 DG 现有 publish 设计/实现和 GraphEnvelope.Parent 是可重写草稿，无兼容包袱；不要为保留旧形状建立双 Parent 或过渡兼容层。

上游已交付引用初始化 fork、紧凑 ID codec 和准确 Entry 起点历史；同库操作经独立消费者验证。已接受其开发期跨库误用检出目标，撤回每引用附加16B scope的建议。**用户认可当前 API 的清晰性与功能覆盖，下一阶段转向可行性验证、效率与简化，收窄设计；尚未开始新模型实施。**

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

1. **写入目标与准确提交位置分开。** BranchId（对应 HeadId）决定继续哪个 Branch；CommitId（对应 HeadEntryId）决定查看、加载或从哪点 fork。空 C 的 current 可以是 A/a1，但首次保存仍写 C。上次世界线存前者，固定书签存后者。
2. **首选继承 head-history。** 在“业务 Head 只发布真实 State/Event、原线只追加、管理属性在链外”的条件下，不额外存逻辑 Parent。仍是首选设计候选，未冻结 wire/API。
3. **不为 fork 制造存档。** 新 C 只有固定来源引用，首次新保存才产生 c1；同值真实保存仍有不同 Entry 身份。
4. **紧凑 ID 足够当前目标。** HeadId编码4B、HeadEntryId编码12B；跨库误用是开发期概率诊断。随机独立key、目标有N个Head时，误命中已有Head约N/2^32（保零有极小修正）。1000个Head约四百二十九万分之一；固定库对内相关，不是每次独立抽奖。不要重新默认附16B强scope。
5. **仓库由调用方选择。** 值可无owner编码/解码，相等不代表全局身份。必要的仓库选择信息可放在应用配置/集合外层，不默认重复到每个引用。
6. **位置校验与业务解释分层。** HS检查记录和发布资格；DG检查正式业务成员、Kind、准确图与Schema。历史Schema不能取当前shared替代，CommitId 也不必携带全部 meta Heads 快照。
7. **分页按准确 Commit 起点。** ReadHistoryPage 使用 AtOrBefore/Before；底层 inclusive 重读上页最后处理项并跳过该项。筛选 State 时 Continuation 仍取最后扫描的 Commit。完成、停止、预算不足和错误分开；没有新项的预算失败不保证原样重试能推进。

## 当前焦点与下一步

**首批机制调查已集成（2026-10-18）。** 三个只读工作包与主线程完成 API 到源码原语的映射；[机制可行性调查](mechanism-feasibility.md)集中维护事实、缺口、候选与下一批实验。未修改产品代码、未运行新模型实验或冻结持久格式。

**压缩后恢复顺序：完整读本文 → [API 草图](api-sketch.md) → 按验证题读一份详细设计。先不继续扩展故事或海选名字，也不直接铺开产品重构。**

当前讨论基线（本目录活动设计以 [API 草图](api-sketch.md) 对齐；历史请求、源码与实验保持原名并注明映射）：

- **Branch / Commit / WorkingCopy** 已形成清晰概念族；Read/List 读描述，Load 加载对象，OpenWorkingCopy 显式选 SchemaMode，Create/Fork/Commit 发布。准确 LoadState/LoadEvent 与 LoadLatestSavedState 分开；加载对象无分支提交能力，不等于深度不可变。当前源码 Checkpoint/BranchCheckout 尚未改名。
- 根/子树查询已加入设计；历史以 AtOrBefore/Before 和 Continuation 表达准确边界，返回 State/Event 小描述。五个核心调用故事保持可走通，不用减少方法数代替使用便利。
- [联合提交设计](multi-branch-commits.md)已选 CommitRequest：保存身份、基线、根引用，CommitBranches 执行时统一捕获和协调Schema；原 CommitState/Event 立即执行。公开冻结 PreparedCommit 暂不采用，具体消费/失败合同仍待定。
- [创建方案](application-design.md)推荐 B：DG 登记决定正式成员；先准备 Head，再登记，允许不展示的未登记产物；普通保存不更新目录。上游不可变创建属性 A 留作对照，不作为前置依赖。新根不能假定 CreateHead 参加 PublishHeads。
- [用户故事](user-stories.md)中 C4 密码门、C5 知识神器是验证参考，不要求全部产品化。应用幂等不保证组合不放大；稳定知识身份、独立向前保存与并集建议只约束同一批事实，不改变 DG 提交/重试合同。

### 首批结果与后续顺序

- **发布：**一个组级准备窗口、一份汇总的 Shared Catalog 是首选实验候选。不能拼接分别分配描述符的 Prepared；汇总还须包含显式 dirty 并检出组间冲突。未发现必须增强 HS 事务的证据。
- **创建：**登记 B 有三条明确路径，root 的登记/Shared 可共同发布。底层创建 Unknown 不自动等于 DG 创建 Unknown；以是否已尝试最终登记区分，详见调查 §6。
- **恢复：**源码 State/Event 都以最近 State 为内容来源，Meta 才取前 Meta；已纠正“同类来源”误述。沿 HS 历史推导 expected source 是较小实验起点；直接 State 锚点不能仅凭可读就证明最近关系。
- **查询：**完整 BranchInfo 需要来源；现 ListHeadOrigins 扫物理全集，未登记产物会扩大访问/故障范围。建议评估定向 origin 读取，不复制来源为第二权威。千条小标签容量可行性有估算，整表创建累计写量与 current 文件句柄仍需测。
- **分页：**优先试页满返回、预算耗尽整次失败，暂不增加预算截断页；这是候选，未冻结公开合同。

下一轮：先固定实验依赖基线 → 两 Shared 不同新 Schema 的共同准备正负控 → 可并行的小 payload 三路径登记见证 → 恢复依赖闭包/错 source 反例 → 查询成本。各包最小可观察标准只维护在[调查 §9](mechanism-feasibility.md#9-下一批最小实验与依赖顺序)。随后以原调用故事复核简化，形成实施切片；不预建全能 Probe，不将局部实验当成产品资格。

允许略调签名、返回形状和内部组织，以减少重复状态、读取量或资源占用；调整必须仍满足已接受功能和调用体验。有新证据才能重访已排除方案，不以性能猜测牺牲正确性，不把成本转移成应用重复样板代码。不增加合并、原线rewind、GC、跨库事务或通用分布式框架。

## 已知未决项与明确延期

未决：登记 B 的运行见证及创建未知结果如何保留候选 BranchId、Shared 合并候选的实际编码/安装、直接 State 锚点与历史校验取舍、定向 origin 的窄请求、分页预算合同；BranchId/CommitId 外部 codec 与 DG 字段表示、Event 摘要缺省、附件闭包/错误；请求消费/失败后复用及历史组合恢复。执行时捕获已选，不重开冻结快照方案；不能删除业务 Parent 后遗失准确最近 State、Meta 前源或 Delta 依赖。

延期：rewind、merge、删除/GC、唯一显示名查询、任意摘要字段索引、多writer、跨库交换协议、异步补图、修订旧摘要、RL批量/吞吐专项。1000条线允许先测完整小目录和内存树索引；长历史操作应避免扫描累计H，具体性能指标尚未定。

## 本批完成边界

2026-10-18：完成首批机制调查、源码交叉复核、恢复来源误述修正及下一批最小实验选择；详见[调查](mechanism-feasibility.md)。API 草图基准、产品代码、固定依赖均未修改；构建、运行与性能资格不在本批结果内。此前词汇对齐继续有效，底层/旧源码符号保留其实际含义。

## 实现与证据边界

- 本批已核对 DG `c93e7f5`、Storage `f8868b5`，开始时两工作树均干净；DG pin 仍为旧 `731eef7`，环境 source mode 指向该 sibling，当前直接构建会被版本门禁拒绝（源码推断，未构建）。完整版本记录见调查 §2；后续实验需显式固定新基线，不沿用旧 build 结论。
- 既有交付复核的上游调查HEAD `3e1dc46`；包源码 `c6fb64a`，包 `0.8.0-persistenthandles-local.20261017105233`。该基线为格式4、HeadId.LocalId已改为owner.GetLocalHeadId，ReadEntryHistory已存在；不是对下一阶段实际HEAD的锁定。
- [交付复核](headstore-delivery-review.md)：独立PackageReference消费者，2个进程、38个断言通过；不是DG产品suite或性能/断电验收。
- [误用检出实验](handle-misuse-detection.md)：实际置换函数穷举相关16,777,216个key，确认存在性检查的额外收益。**最新身份裁决以此为准，旧完整scope建议已退出。**
- 两份实验源码/receipt在本机`.artifacts`，不随Git克隆分发；详细路径见上述记录。上游既有未提交文件不属于本工作，不覆盖、不清理。

## 按需阅读

| 要讨论什么 | 唯一详细入口 |
|---|---|
| 用户明确要求、规模、非目标 | [用户故事](user-stories.md) |
| 当前机制缺口、源码裁决及下一批最小实验 | [机制可行性调查](mechanism-feasibility.md) |
| 五种调用流程与定位值职责 | [存档调用故事](save-handle-stories.md) |
| 最小调用面与当前创建协议建议 | [API草图](api-sketch.md) |
| 命名原则、竞争方案、旧名映射与审阅限度 | [命名审阅](api-naming-review.md) |
| 跨分支共同保存、已选请求路线与实验题 | [联合提交设计](multi-branch-commits.md) |
| 标签候选、发布失败、Schema、新根与查询成本 | [应用合同稿](application-design.md) |
| 上游实际交付、API和验收限度 | [交付复核](headstore-delivery-review.md) |
| 原request与其已澄清身份要求 | [持久Handle请求](headstore-save-handles-request.md) |

后续更新本文时替换当前焦点、压缩已完成事项；不要追加轮次流水账或复制完整设计。README继续是问题入口，本文是短续接工作集。
