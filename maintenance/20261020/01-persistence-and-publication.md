# 持久化、保存规划与发布正确性体检

日期：2026-10-20。检查基线：`1beca7cf2477d369010b3577b7a795c41dbc45f1`。受众：后续 Coding Agent。

## 结论与使用方式

本工作包没有确认必须立即修复的持久化正确性缺陷。当前实现已经把 complete-frame append-only、准确 source、Base origin S / selected S、实际 H 计量和发布后安装分别落实到具体边界；不能因重构规模大或文件较长便判定违反 SOLID。

建议保留两个有源码依据的改进线索：**P2：确定性 batch 容量错误发现偏晚**；**P3：单次保存反复完整核验同一来源并复制目录**。另记录一个 P3 的发布安装协议维护风险。它们均不是已经观察到的数据丢失，第二、三项也不构成立即重构授权。先用下面的最小验收补证，再选择一片实施。

本轮仅新增本文，没有产品代码、测试、持久格式或 API 改动，没有执行 dotnet、进程故障注入或大规模性能实验。测试名称和已有断言来自源码检查，不宣称本工作包重新运行通过；根 build/test 由主线程统一执行。

## 范围和判断依据

完整阅读根 `AGENTS.md`、`src/PROJECT-STATE.md`、DB-096 工作集及 DB-086 当前恢复合同。重点检查：

- `GraphRecordStore` 的 append、source/prior、目录和对象链、读缓存寿命。
- `StoredGraphBodies` 的 ABI5 路由、准确 origin S 和 selected S、完整引用与 Dictionary lookup 验证。
- `ObjectWritePlanner`、`ObjectVersionPayloadCodec`、`ObjectVersionPayloadSize`、`ObjectVersionRecord` 和地址 codec。
- `BusinessPersistenceAdapter`、`NormalizedGraphState`、`PreparedGraphWrite` 的保存规划和安装证据。
- `RepositoryResources.GroupSave`、`MetaBootstrap`、`Bookmarks`、`ManagementDirectoryWriter` 的联合发布、创建登记及异常路径。
- 定向阅读实际计量、P0 integrity/publication、组提交 integrity、目录发布和故障测试。

任务包提到的 `ObjectVersionSaver` 在当前 `src` 中已经没有同名符号；实际保存链由 producer、`ObjectWritePlanner` 和 `GraphRecordStore` 组成。不要据旧符号重建适配层。

未覆盖：Source Generator 的 body 正确性、全部容器算法、完整 SchemaCatalog 闭包实现、公开 Repository 的全部 reentry/枚举行为、上游 HeadStore/FrameStore 内部实现、真实断电、全平台及大图性能资格。这些不能由本文替代。存储包身份只以 `eng/StorageDependency.props` 为准，本轮未从不相干 sibling HEAD 推断底座行为。

## F1 / P2：确定性 batch 容量错误在整体保存已开始输出后才被拒绝

**类别：已确认的校验时序设计债；尚未通过运行重现其具体公开 API 表现。**

证据：

1. [`PreparedGraphWrite`](../../src/DurableGraph.Persistence/GraphPersistenceContracts.cs#L13) 检查非空 root、记录唯一性、Remove 唯一性和二者不相交，但不检查 batch record 数量上限。
2. [`GraphFrameCodec.EncodeBatch`](../../src/DurableGraph.Persistence/GraphFrameCodec.cs#L19) 才拒绝超过 `MaxCollectionCount = 1_000_000` 的 records；reader 的对应限额也在 [`DecodeBatch`](../../src/DurableGraph.Persistence/GraphFrameCodec.cs#L41)。这是已有 wire/实现上限，不是建议新增限制。
3. [`CommitSaveGroup`](../../src/DurableGraph.Persistence/RepositoryResources.GroupSave.cs#L113) 先 append Catalog，再逐成员 `AppendBusiness`。因此新 Catalog + 超限业务 batch，或前成员合法而后成员 batch 超限时，已知不合法输入可能在已有完整候选帧输出之后才暴露。
4. [`GraphRecordStore.AppendCore`](../../src/DurableGraph.Persistence/GraphRecordStore.cs#L92) 在实际 append batch 之前调用 EncodeBatch；该成员不会写出半个 batch，但之前 Catalog/成员的完整帧已经存在。组发布尚未开始，catch 按 [`AppendedFrames`](../../src/DurableGraph.Persistence/RepositoryResources.GroupSave.cs#L199) 的变化 fault owner。

触发与影响：业务对象/写记录达到上限且同次操作前面已有输出时，调用会得到 NotPublished 并失去当前 owner 的继续工作能力，须关闭重开。完整帧仍保留，旧 heads 保持，不是 append-only 或原子发布缺陷。用户输入本可提前拒绝，却被升级为一次有输出的保存故障。首次大图 Capture 自身的时间/内存也可能成为更早的限制，尚未实测。

最小方案：在 `PreparedGraphWrite` 构造或统一的 pre-append 候选检查中校验非空 batch 的既有 record 数量限额，允许零 records（复用旧目录的无变化保存合法）。EncodeBatch 保留自己的防线。不要为解决这一点引入全库 writable preflight、扫描历史、修复旧帧或事务回滚。

验收建议：

1. 内核级构造 `MaxCollectionCount + 1` 个唯一 ID 的轻量记录，确认在进入任何 append 之前拒绝。该规模测试可能较重，宜单独标记，或把纯计数 validator 作为边界测试接缝；不要为了测试建立通用限额框架。
2. 合法 pending 之后仍可保存；`AppendedFrames`、heads、State 基线不变，owner 健康。
3. 若专门保留旧路径见证，放到第二个组成员，核对 NotPublished、owner fault、冷开 heads 全旧及已完成候选帧未被删除。
4. 上限整值、零 records 的 unchanged State、普通组提交仍能按现有语义运行。

此项不主张一次性预编码全部 batch：那会增加峰值内存。先前置零额外 body 复制的已知计数检查；单帧字节容量另需准确底座合同和具体反例，本文没有确认其边界。

## F2 / P3：单次保存存在重复的完整来源核验和目录分配

**类别：有据性能风险；不是语义缺陷，尚无当前 workload 的性能反例。**

证据链：

- [`BusinessPersistenceAdapter.PrepareOwned`](../../src/DurableGraph.Persistence/BusinessPersistenceAdapter.cs#L45) 先调用 `baseline.RequireStoredSource`；后面非 Event 候选进入 [`NormalizedGraphState.FromCapturedCandidate`](../../src/DurableGraph.Persistence/NormalizedGraphState.cs#L41)，再次调用同一 baseline 的 `RequireStoredSource`。
- [`RequireStoredSource`](../../src/DurableGraph.Persistence/NormalizedGraphState.cs#L135) 每次 `store.Revalidate(source)`，随后完整遍历所有 source rows 并检查 source layout。
- [`ReadGraphCore`](../../src/DurableGraph.Persistence/GraphRecordStore.cs#L141) 即使 physical cache 全命中，也逐一检查所有 live head 的本地记录。然后 [`StoredGraphRevision`](../../src/DurableGraph.Persistence/GraphPersistenceContracts.cs#L51) 再复制并冻结整张 head map；`Revalidate` 还进行完整 membership 序列比较。
- 新图 append 的 readback、[`PrepareGraphInstallation`](../../src/DurableGraph.Persistence/NormalizedGraphState.cs#L64) 的 revalidation 继续保证安装来源，因此一个逻辑保存中会出现多个完整资格/复制阶段。另有 `AppendCore` 的 finalHeads、`ReadDirectory` 的 frozen map 复制。

触发与影响：大 live 图、极少变动、频繁 State 保存或很多组成员时，物理解码缓存不能消除这些 CPU/分配成本。源码足以证明重复工作存在；不能据此声称吞吐已不达标、总复杂度退化到平方级或删除来源校验。

最小下一步：在已有 `BusinessGraphQualificationCount`、read-cache 统计和保存阶段上做一次 N=1k/10k/100k、固定少量变化的计数与分配对照，分别看 Prepare、append/readback、安装准备。把现有次数先记录为 baseline。只有热点成立，才考虑让同一串行操作内、同一个 owner/source 的已验证结果在 `PrepareOwned → FromCapturedCandidate` 之间直接传递；不可把跨操作 snapshot、physical hit 或旧 State 证书变成免验证凭证。也不建议引入长期 DTO cache。

验收建议：新切片必须保留跨 owner、错误 source、完整 membership、升级后不可达行、错误 prior、fault/Dispose 后暖缓存拒绝，以及 Event 不接受 State H/rewrite 的反例。收益判据是完整操作的耗时/分配及核验次数，不只是 ReadBatch miss 减少。

## F3 / P3：发布安装协议由多组可空 Action 和调用位置共同表达

**类别：维护风险；当前调用和失败回归未显示实际错误。**

[`CommitSaveGroup`](../../src/DurableGraph.Persistence/RepositoryResources.GroupSave.cs#L78) 接收 `installMeta`、`prepareMeta`、`prepareState`、`installState`、`prepareDelivery`、`deliver`、`cleanup` 等 callback。正确性依赖 producer 同时理解：先准备所有安装材料；真正 PublishHeads 成功后校验准确返回 head 集；随后才 install；Event 跳过 State 安装；cleanup 无论哪条路径只尝试一次。

当前这些步骤有明确的代码顺序和 guard，并且 [`BranchGroupIntegrityTests`](../../tests/DurableGraph.Persistence.Tests/BranchGroupIntegrityTests.cs#L10) 覆盖 Confirmed 结果缺失/错 head/额外 head 时不安装、cold heads 实际全新，以及 cleanup 失败与主异常保留。因此不能把“Action 参数多”直接判为 SRP/DIP 违例，更不能为了内存全组回滚破坏现行 Published+fault 合同。

风险在后续增加 producer 或修改单个 callback 时：编译器不能防止漏传准备或把 install 放到不匹配的 producer；跨参数关系隐藏在调用者中。

最小建议：先保持当前实现。如果真实修改反复需要同时调整这些参数，可收敛一个**具体的内部 prepared-installation 值**，显式携带准备结果及其对应安装动作，由现有 owner 调度；不要建立公共交易接口、通用两阶段框架或新的发布 owner。更小的当前改进是给本入口补一段协议注释，并让新 producer 必须复用现有故障矩阵。

验收：保留 State/Event 混合、第二成员安装失败、Published 后 cleanup 失败、结果集篡改、Unknown 前后两端和新根登记失败见证；公开 outcome、Members 身份和 fault/冷开观察均不能改变。

## 已核对且应保留的边界

| 边界 | 当前证据与判断 |
| --- | --- |
| 完整帧 append-only | GraphRecordStore 的唯一输出为 AppendFrame→FrameStore.Append；本范围没有截断/覆写/修复完成帧路径。回收不在本期范围。 |
| 每个 Delta 的准确 prior/source | ReadObjectChainCore 逐 batch 查 source 的 head，再 RequirePrior；目录 Delta 同样查其 source directory；循环有 visited 集拒绝。 |
| 新 Base 截断重建依赖 | Base 停止旧链读取，不应为“完整审计”重新读已被替代的坏链；已有 integrity 用例验证该边界。 |
| Base origin S 与 selected S | StoredGraphDecoder 从首个 Base 的 batch.SchemaRoot 加载 origin、解码 descriptor；selected.DescriptorId 核完整布局。没有使用 latest 补解释。 |
| 完整图校验 | 所有 source rows 完成 reader 解码后，全部 VisitReferences 与 Dictionary lookup 才结束；不可达行不会免检。 |
| 实际 B/D/H | 写入策略用完整 typed Base 与固定 prior 封套成本；读取 PayloadCodec 用 ConsumedCount 记录实际字节，包括合法冗余长度；安装从实际 readback records 推进 H。 |
| State/Event 区别 | prepareState/installState 只对 State；Event 的共同 Catalog 与 head 可推进，State 内容基线与 rewrite 不被接受。 |
| Confirmed 与交付 | PublishHeads 返回集先核准确 head 集，之后才调用安装；发布后安装/cleanup 失败为 Published+fault，不虚构回滚。 |
| 创建与登记 | 新物理 head 和 D 登记分开；NotPublished 可伴随已创建的物理产物和 fault，不代表可在原 owner 重试。 |
| 寿命与恢复 | fault 会 Dispose GraphRecordStore；EnsureUsable 清缓存后传播 backing/owner 失败。正常 backing 尾恢复与按需控制根打开保持，不建议恢复全库启动审计。 |

## 复验入口

以下为本轮阅读过、适合主线程统一运行的测试过滤器候选，非本工作包运行成绩：

```text
FullyQualifiedName~P0BusinessPlanningAccountingTests
FullyQualifiedName~MetaBootstrapP0IntegrityTests
FullyQualifiedName~MetaBootstrapP0PublicationTests
FullyQualifiedName~BranchGroupFailureTests
FullyQualifiedName~BranchGroupIntegrityTests
FullyQualifiedName~ManagementDirectoryPublicationTests
FullyQualifiedName~NewPublicationFailureTests
```

后续 Agent 若选择 F1，先做边界用例并确认是普通参数拒绝还是已有输出故障；若选择 F2，先取得成本反例；若选择 F3，仅在新 producer/修改成本显现时收束。不要把本次审查的风险项升级为既成 bug，也不要以维护名义同时改 wire、API 和恢复协议。
