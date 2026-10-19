# DB-096：基于 CommitRequest 的多 Branch 联合提交设计

> 2026-10-19 收敛版；采用轻量 CommitRequest，由 Repository 执行时统一捕获与共同提交。请求不另设消费状态，失败合同见 §2.1；产品组内核已实施，源码/包资格及限度只查[实施验收](group-commit-integration.md)。需求事实见[用户故事 C4/C5](user-stories.md)，公开概念沿用 [Branch / Commit / WorkingCopy](api-sketch.md)。本文保留冻结准备的对照理由，不再将两种路线并列为待选。

> 后继源码调查：[共同准备的真实缺口与首选实验](mechanism-feasibility.md#5-联合提交保留一个操作窗口合并-shared-准备)。单 pending 的调整不足以支持联合提交；必须先汇总显式 dirty 和捕获布局、检出组间冲突并统一分配 Shared 描述符。该调查没有改变执行时捕获决定，也不是运行资格。

> 2026-10-19 范围收窄：用户已选[统一 Catalog](shared-schema-only.md)。所有成员只有同一 Schema 接纳目的地；Private、混组、promotion 退出设计，但组内闭包、冲突和描述符分配仍须统一完成。

## 1. 能力与范围

DG 有条件组合现有底层原语，为同一 Repository 中多个既有 Branch 追加提交，并共同发布必要的 Schema。一次组操作每个参与分支产生自己的 CommitId；不把整组重新命名成一个 Commit，不需要先引入持久 GroupId 或全局事务历史。

候选首片限定同一个打开的 Repository 实例、串行调用、不同且有效的 WorkingCopy、每个 Branch 一次 State 或 Event 保存。拒绝空组、重复分支、已关闭或其他 owner 的工作副本，以及过期基线。多个分支有独立图和对象身份；联合保存不自动赋予跨图 CLR 引用或 ObjectId 以共享业务身份。

公开入口由 DG 负责准确 Schema、内容先持久化、统一发布与基线安装，不让应用直接提交 HeadStore payload。新建根/fork、跨库、并发隔离、同一分支一次追加多个 Commit、外部工具事务暂不放入该候选。只读/故障/重入与存储寿命纪律保持。

## 2. 创建请求与执行时捕获

| 路线 | 调用时保存的东西 | 好处 | 额外合同 |
|---|---|---|---|
| **已选 CommitRequest：延迟请求** | WorkingCopy、目标 Branch、预期 BaseCommitId、State/Event 根引用、小摘要参数 | 不建立长寿命 frozen graph 或额外存储资源；Repository 能统筹捕获和 Schema | 创建请求后、执行前的领域对象修改会进入保存；请求不是快照 |
| 未采用 PreparedCommit：内容已冻结 | 上述身份及捕获后的不可变内容、所需布局/准备材料 | 可以保存 Prepare 当时的值，随后修改源对象也不改变候选 | 内存/资源释放、失效、单次消费、准备副作用、Schema最终协调，以及发布后工作副本的活对象与旧快照基线关系 |

已选第一种，设计入口采用 `CreateStateCommitRequest` / `CreateEventCommitRequest`，使“构造请求”和“真正准备保存”不混淆。PrepareCommitState/Event 不进入本次公开草图，避免暗示已经冻结内容。草图中的 CommitState/CommitEvent 保持立即发布；只有出现必须保存请求创建时快照的具体需求时，才重访 PreparedCommit。

```text
requests = [
    worldWork.CreateStateCommitRequest(worldState, worldSummary),
    playerWork.CreateStateCommitRequest(playerState, playerSummary)
]
result = repo.CommitBranches(requests)
// result 按 BranchId 对应各 CommitInfo；正常返回意味着整组已确认。
```

CommitRequest 不做图捕获、不占用新的存储 writer、不改变工作副本基线。它保留 State/Event 根引用与操作角色，携带预期 BaseCommitId；执行前若该副本已另行提交则拒绝，不把旧请求自动套到新基线。工作副本和 request 都没有 SchemaMode：执行时以仓库当前统一 Catalog 汇总准备，历史内容仍按其准确 Schema 解释。请求绑定**同一活 Repository 和同一 WorkingCopy 实例**，不是可序列化或可重放的任务；Dispose 后重新打开相同 Branch，即使基线没变，也不能使旧请求重新有效。

摘要若为可变字节，应在创建请求时复制为自有小数据，或使用明确不可变的值；业务图仍在执行时捕获。期间继续修改业务图时，应用需要同步更新摘要请求，框架不保证二者业务一致。调用方及应用回调在整个 CommitBranches 调用期间保持全部参与图稳定；不预建对象变更追踪或后台冻结机制。

这比暴露完整 PreparedCommit 更小：现有源码的保存正常返回后安装调用方原始 State 实例。若允许先冻结 S0、随后把同一实例改成 S1、最后发布 S0，工作副本安装/dirty基线便必须明确如何解释 S1。该路线可行，但不能只以“返回捕获的参数”略过这些成本。

### 2.1 请求没有独立消费状态

**选定无 `Consumed` 标志的参数对象。** 每次执行重新检查活 owner、原工作副本、占用资格及 expected Base，再创建本次操作的候选。成功保存一定产生新的 CommitId，包括同值保存，因此旧请求自然过期；同一 Branch 的两个预先构造请求也不能先后成功套用同一基线。

| 上次执行结果 / 当前条件 | 再提交这个请求 |
|---|---|
| 尚未执行，原副本和基线有效 | 可执行，捕获此时的业务图 |
| 准入、Capture 或 Schema 校验失败，清理成功且 owner 健康，基线未变 | 可再次执行；仍须所有准入检查，不保留旧候选 |
| NotPublished | **不能只看 outcome 判断可重用**；仅健康 owner、原活副本及未变基线允许再次执行 |
| 已正常成功，或其他提交推进该副本（包括 Event） | 旧 expected Base 不匹配，拒绝 |
| Unknown，或 Published 后安装/交付失败 | owner 停用；旧请求不能转投重开的 owner |
| 副本 Dispose、owner 关闭/fault | 拒绝，即使新打开的 Branch 仍有相同 Base |
| 只有其他 Branch 推进 Catalog，本 Branch 基线未变 | 可执行，用当前 accepted Catalog 重新准备 |

健康失败后的再次执行是**重新捕获当前图**，不是恢复第一次尝试的字节或应用副作用。现有 [CaptureSession.Discard](../../../src/DurableGraph/Runtime/Capture/CaptureSession.cs)丢弃候选绑定但不回收已分配 ObjectId；不承诺失败后编号或内存对象恢复原样，也不自动重试。摘要仍是请求创建时自有的值，需要更新摘要时构造新请求。

所有参与图在整个调用期间应保持稳定，包括 Capture 回调不得修改另一个成员的图。相同 CLR 对象同时作为两份输入不自动获得跨 Branch 持久身份；各自拥有身份表，独立冷加载不承诺跨图 `ReferenceEquals`。首片不为拒绝或合并这种别名做跨图扫描。

该简化要求清理所有未安装候选并保 owner 操作窗口，不能把“参数对象无状态”理解为内部保存候选也无状态。具体阶段与最小反例见[组内核设计](group-kernel-design.md)。这些是选定设计规则，后继运行证据见[实施验收](group-commit-integration.md)。

## 3. Repository 层的共同准备

推荐流程是：

1. 先进入 owner 操作窗口，再固化输入集合并完成 owner、工作副本、重复 Branch、基线及策略准入检查。若接收 IEnumerable，枚举本身也可能执行用户代码，不能放在 guard 外。
2. 捕获所有参与业务图；每次 Capture 后立即重查资源可用性，再进入下一成员或 Catalog/body 准备。收集 Schema、统一检出冲突；任一成员失败清理全部未安装候选。准备失败时不推进任何分支；应用回调造成的副作用不承诺回滚。
3. 构造准确图、Schema、摘要和附件闭包，追加必要内容；准备返回材料和内存安装材料。
4. 将各业务 Head 的新 payload 与必要的共享 Schema 管理 Head **一次共同发布**。
5. 发布确认后安装所有工作副本的相应基线，再交付完整结果表。

多份请求不能分别以同一个旧 Catalog 根准备后，只选择最后一份更新。必须共同解决定义兼容、描述符分配与准确 pin；一个 Catalog 管理 Head 在最终发布组中只有一个新值。所有成员的显式声明和捕获布局先作完整闭包检查，再由一次 Builder 分配描述符。跨成员同 key 异形使整组失败，不能借 Set 覆盖或选择最后一个定义解决。没有 Private 分配域和 promotion/remap 路线。

公开路径自动从捕获图收集布局，没有逐 WorkingCopy 编辑 Catalog 的公开 API。新内核首选仓库 accepted Catalog＋操作内共同候选；显式声明仅作为必要的内部输入，不能为沿用旧 dirty 测试而新增公开编辑协议。两份图共同编码/冷读的限定证据与仍未接通的发布/安装见[统一 Catalog §3](shared-schema-only.md#3-两个可执行机制见证)。

CommitState 更新 BaseCommitId 与 StateSourceCommitId，并安装相应 State 保存基线；CommitEvent 只推进 BaseCommitId，不改变 StateSourceCommitId，也不隐含保存该副本的 State 编辑。统一 Catalog 不合并各业务图的身份、保存基线或冻结执行环境。单分支 CommitState/Event 应复用组内核的单成员路径，避免两套持久化语义。

## 4. 原子性具体保证什么

目标是这组**新业务位置及必要 Schema 发布共同成立或共同不成立**，不是把磁盘所有追加、应用内存编辑或外部副作用全部回滚。失败后允许完整但未发布的帧；不删除或重写完成帧。

正常返回交付完整 BranchId→CommitInfo 表，不返回伪装成功的部分组。发布结果未知时必须保留不确定性，不透明重试。若持久发布已成功但安装或交付失败，不能报告整组未提交；按 owner fault 合同停止旧资源，重开检查持久事实。安装材料和结果槽尽量在发布前准备；实际 CommitId 由 HS 发布后返回，再填入并安装，不能假设发布前已知它。任何半安装异常都停用整个 owner；具体异常类型留到实施合同。

NotPublished 只说明业务发布没有成立，不说明 owner 必然健康。沿用现有严格边界：准备失败并完成清理可继续；追加途中异常等情况可能停用；HS 正常拒绝且 backing 健康则可以留下完整未发布帧并继续。Unknown 不保证能取得候选 CommitId，失败描述应保参与 Branch/原基线和 outcome，不为补齐结果猜造位置。重开后先检查这些 Branch 再做新写入；已有后续写入时不能把最新 current 直接认作本次结果。不新增持久 GroupId 或透明重放协议。

共同写入不自动提供跨调用读快照。另一个调用推进一部分分支后，随后分别调用 ReadCurrentCommit 读各 Branch 当前提交不保证仍属于同一组。要保存历史组合，应用可以记录结果表中的准确 CommitId；该表的持久化、组结果未知后的定位，以及冷重开“当时配对”的需要，都是独立设计题，不假定现有 HeadSetTag 或调用返回值已完整解决。

原子保存只保证应用提交的多份状态共同落地，不验证“剑只有一把”。C4 的物品身份/所有权、C5 的知识去重/不遗忘仍属应用规则。幂等知识有助于应用级重试，但不能替代存储结果判断，也不能使丢失保存自动恢复。

## 5. 源码线索与最小实验题

以下保留现有源码符号名，它们不是已经实施的新 API。当前 [BranchCheckout](https://github.com/Atelia-org/durable-graph/blob/866ce89705888af5fea56a5532c7d1986a21ca65/src/DurableGraph.Persistence/BranchCheckout.cs "Pre-B2 source: Git 866ce89")为每个分支持有独立保存会话，并区分 State/Event 的安装效果；[BranchSaveSession](../../../src/DurableGraph.Persistence/BranchSaveSession.cs)存在内部 capture/prepare/install 分层；[RepositoryResources](../../../src/DurableGraph.Persistence/RepositoryResources.MetaBootstrap.cs)已有业务与 Schema 联合发布及提前准备安装材料。当前只有一个 owner pending 等限制，不能简单把两个旧 prepared 对象塞进数组就称支持多分支。

上游 [PublishHeads](../../../../atelia-storage/src/HeadStore/HeadStore.Batch.cs)提供多个既有 Head payload 的共同发布基础；它不替 DG 组合 Schema 和工作副本。上述是源码线索，本轮没有运行新的 Probe 或确认产品接入资格。

后续首先做两分支各新增不同 Schema 的机制见证：完整保存并冷重开恢复两份准确内容，确认 shared 增长未丢失、描述符无冲突。再覆盖：

- 第二份输入冲突时，两分支及 shared 都不推进；请求创建不导致第一份先发布。
- 请求创建后修改业务图，执行保存的是执行时值；单独提交推进基线后，旧请求被拒绝。
- 注入发布中断：重开只能接受整组或旧组，不能一个得到剑、另一个仍保有剑；区分结果未知与已确认。
- State/Event 混组后，两份 WorkingCopy 的 BaseCommitId 推进，但 Event 成员的 StateSourceCommitId 与 State 保存基线不推进。
- 发布确认后安装故障：报告已发生的发布，旧 owner 不可继续使用，不返回半组成功。

这些是候选验收题，不是已通过的测试，也不意味着本轮已经承诺全部纳入首片。
