# DB-096：多 Branch 联合提交候选

> 2026-10-18，用户提出组合 WorkingCopy 准备操作、由 Repository 统一提交。本文为设计调研，未实施、未冻结首片范围。需求事实见[用户故事 C4/C5](user-stories.md)，公开概念沿用 [Branch / Commit / WorkingCopy](api-sketch.md)。本轮最小产物是界定捕获时机、共同发布范围和可执行验收题。

## 1. 能力与范围

DG 有条件组合现有底层原语，为同一 Repository 中多个既有 Branch 追加提交，并共同发布必要的 Schema。一次组操作每个参与分支产生自己的 CommitId；不把整组重新命名成一个 Commit，不需要先引入持久 GroupId 或全局事务历史。

候选首片限定同一个打开的 Repository 实例、串行调用、不同且有效的 WorkingCopy、每个 Branch 一次 State 或 Event 保存。拒绝空组、重复分支、已关闭或其他 owner 的工作副本，以及过期基线。多个分支有独立图和对象身份；联合保存不自动赋予跨图 CLR 引用或 ObjectId 以共享业务身份。

公开入口由 DG 负责准确 Schema、内容先持久化、统一发布与基线安装，不让应用直接提交 HeadStore payload。新建根/fork、跨库、并发隔离、同一分支一次追加多个 Commit、外部工具事务暂不放入该候选。只读/故障/重入与存储寿命纪律保持。

## 2. Prepare 到底准备什么

| 候选 | 调用时保存的东西 | 好处 | 额外合同 |
|---|---|---|---|
| **CommitRequest：延迟请求** | WorkingCopy、目标 Branch、预期 BaseCommitId、State/Event 根引用、小摘要参数 | 不建立长寿命 frozen graph 或额外存储资源；Repository 能统筹捕获和 Schema | 创建请求后、执行前的领域对象修改会进入保存；请求不是快照 |
| PreparedCommit：内容已冻结 | 上述身份及捕获后的不可变内容、所需布局/准备材料 | 可以保存 Prepare 当时的值，随后修改源对象也不改变候选 | 内存/资源释放、失效、单次消费、准备副作用、Schema最终协调，以及发布后工作副本的活对象与旧快照基线关系 |

当前推荐第一种，名字用 `CreateStateCommitRequest` / `CreateEventCommitRequest`，使“构造请求”和“真正准备保存”不混淆。用户提出的 PrepareCommitState/Event 可作为第二种命名候选；若仍用于仅保存参数的请求，必须显式说明没有捕获快照。两种方案都不应把已有立即发布的 CommitState/Event 偷改成延迟行为。

```text
requests = [
    worldWork.CreateStateCommitRequest(worldState, worldSummary),
    playerWork.CreateStateCommitRequest(playerState, playerSummary)
]
result = repo.CommitBranches(requests)
// result 按 BranchId 对应各 CommitInfo；正常返回意味着整组已确认。
```

推荐请求不做图捕获、不占用新的存储 writer、不改变工作副本基线。携带预期 BaseCommitId；执行前若该副本已另行提交则拒绝，不把旧请求自动套到新基线。请求绑定活 owner/副本，不是可序列化或可重放的任务。首版用后丢弃，不承诺自动重试或幂等执行；准确消费状态与失败后复用规则在实现合同中定稿。

摘要若为可变字节，应在创建请求时复制为自有小数据，或使用明确不可变的值；业务图仍在执行时捕获。期间继续修改业务图时，应用需要同步更新摘要请求，框架不保证二者业务一致。调用方必须在 CommitBranches 全部捕获期间保持所有参与图稳定；不预建对象变更追踪或后台冻结机制。

这比暴露完整 PreparedCommit 更小：当前保存正常返回后安装调用方原始 State 实例。若允许先冻结 S0、随后把同一实例改成 S1、最后发布 S0，工作副本安装/dirty基线便必须明确如何解释 S1。该路线可行，但不能只以“返回捕获的参数”略过这些成本。

## 3. Repository 层的共同准备

推荐流程是：

1. 固化输入集合并完成 owner、工作副本、重复 Branch、基线及策略准入检查。
2. 捕获所有参与业务图，收集所需 Schema，协调组内 Schema 候选与冲突。准备失败时不推进任何分支；应用回调造成的副作用不承诺回滚。
3. 构造准确图、Schema、摘要和附件闭包，追加必要内容；准备返回材料和内存安装材料。
4. 将各业务 Head 的新 payload 与必要的共享 Schema 管理 Head **一次共同发布**。
5. 发布确认后安装所有工作副本的相应基线，再交付完整结果表。

多份 Shared 请求不能分别以同一个旧 shared 根准备后，只选择最后一份 Schema 更新。必须共同解决定义兼容、描述符分配与准确 pin；一个共享管理 Head 在最终发布组中只有一个新值。Private 各按自己的准确视图准备，不因入组而自动合入 shared。Shared/Private 混组是否在首片交付、既有 promotion 如何加入，须在机制验证后裁决，不为接口形状对称偷偷扩张。

State 提交安装相应 State 保存基线；Event 提交只推进逻辑末端，不隐含保存该副本的 State 编辑。每个副本保留各自的 Schema 编辑意图与执行环境。单分支 CommitState/Event 应复用组内核的单成员路径，避免两套持久化语义。

## 4. 原子性具体保证什么

目标是这组**新业务位置及必要 Schema 发布共同成立或共同不成立**，不是把磁盘所有追加、应用内存编辑或外部副作用全部回滚。失败后允许完整但未发布的帧；不删除或重写完成帧。

正常返回交付完整 BranchId→CommitInfo 表，不返回伪装成功的部分组。发布结果未知时必须保留不确定性，不透明重试。若持久发布已成功但安装或交付失败，不能报告整组未提交；按 owner fault 合同停止旧资源，重开检查持久事实。要让正常失败不会留下可继续使用的半安装工作副本，安装材料须提前准备，安装异常路径须统一停用；具体异常/Outcome 类型留到实施合同。

共同写入不自动提供跨调用读快照。另一个调用推进一部分分支后，随后分别读各分支 current 不保证仍属于同一组。要保存历史组合，应用可以记录结果表中的准确 CommitId；该表的持久化、组结果未知后的定位，以及冷重开“当时配对”的需要，都是独立设计题，不假定现有 HeadSetTag 或调用返回值已完整解决。

原子保存只保证应用提交的多份状态共同落地，不验证“剑只有一把”。C4 的物品身份/所有权、C5 的知识去重/不遗忘仍属应用规则。幂等知识有助于应用级重试，但不能替代存储结果判断，也不能使丢失保存自动恢复。

## 5. 源码线索与最小实验题

当前 [BranchCheckout](../../../src/DurableGraph.Persistence/BranchCheckout.cs)为每个分支持有独立保存会话，并区分 State/Event 的安装效果；[BranchSaveSession](../../../src/DurableGraph.Persistence/BranchSaveSession.cs)存在内部 capture/prepare/install 分层；[RepositoryResources](../../../src/DurableGraph.Persistence/RepositoryResources.MetaBootstrap.cs)已有业务与 Schema 联合发布及提前准备安装材料。当前只有一个 owner pending 等限制，不能简单把两个旧 prepared 对象塞进数组就称支持多分支。

上游 [PublishHeads](../../../../atelia-storage/src/HeadStore/HeadStore.Batch.cs)提供多个既有 Head payload 的共同发布基础；它不替 DG 组合 Schema 和工作副本。上述是源码线索，本轮没有运行新的 Probe 或确认产品接入资格。

后续首先做两分支各新增不同 Schema 的机制见证：完整保存并冷重开恢复两份准确内容，确认 shared 增长未丢失、描述符无冲突。再覆盖：

- 第二份输入冲突时，两分支及 shared 都不推进；请求创建不导致第一份先发布。
- 请求创建后修改业务图，执行保存的是执行时值；单独提交推进基线后，旧请求被拒绝。
- 注入发布中断：重开只能接受整组或旧组，不能一个得到剑、另一个仍保有剑；区分结果未知与已确认。
- State/Event 混组后，两逻辑末端推进，但 Event 成员的 State 基线不推进。
- 发布确认后安装故障：报告已发生的发布，旧 owner 不可继续使用，不返回半组成功。

这些是候选验收题，不是已通过的测试，也不意味着本轮已经承诺全部纳入首片。
