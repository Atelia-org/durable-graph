# DB-085 辩证简化评审记录

> 首轮历史记录；后续[第二次独立实施前审阅](0085-joint-crash-recovery-preimplementation-review.md)已修订短尾资格、验证覆盖、恢复屏障与 S0 验收门，并将空初始化残片的一律补齐降为实验候选。下文保留当时裁决，不作为当前施工合同。
> 2026-09-29；对象为[联合恢复方案](0085-joint-crash-recovery-design.md)。本文保留证据和改判，不维护第二份规范或 backlog。
> 授权范围：撰写与修订方案；未实现代码、未操作真实存档、未提交或发布。

## 方法与证据边界

使用 `dialectical-simplification` 技能的三角色审阅。
需求怀疑者、最小架构师、语义捍卫者独立阅读同一草案和需求账本，再作交叉质询；第三轮只裁决 orphan 初始化的剩余分歧。
主线程独立核验源符号及测试内容，不以票数表决。

基线：storage `bf7d68ab3f3fbad18b87549dbf6ce2aae499c24a`，DG `c900edd5b3a921b7bf46df2ac0482dc39597e606` 加已有适配 working tree。
用户明确无外部消费者且唯一旧数据已升级；自动恢复默认值、具体 API 和持久格式仍未批准。
以下“已有测试”仅指当前测试源码中的见证，本轮未运行测试，不证明新恢复能力已经实现。

## R1：连续正向检查取代自动反向搜帧

- **位置 / 主张：**方案 §4.1；草案把 reverse scanner 候选与连续性证明组合，最终只采用正向检查。
- **需求来源 / 当前消费者：**[RbfForwardEnumerator](../../../atelia-storage/src/Rbf/RbfForwardEnumerator.cs) 的真实 HeadLen 连续布局；DG 自动恢复需完整前缀，人工 salvage 才需任意位置搜索。
- **复杂度成本 / 最小替代：**移除自动路径的候选搜索、二次筛选；复用正向 parser 与 checked read，反向 scanner 保留给人工工具。
- **删除失败：**若删掉连续性校验，payload 内嵌合法帧 → 外层 append 中止 → reverse FullFrame 命中内嵌帧 → 截尾后前向解析失败。删除反向搜索则没有这个问题。
- **裁决 / 置信度：**`simplify / keep`，高；可信起点与候选限长接缝仍须 S0 证明。

## R2：缺 EOF Fence 可是正常中断

- **位置 / 主张：**方案 §4.1；撤回草案“缺 Fence 首片一律拒绝”，以实际 writer-prefix 形态表决定。
- **需求来源 / 当前消费者：**[AppendImpl](../../../atelia-storage/src/Rbf/Internal/RbfAppendImpl.cs) 多次 Write、[FileImpl](../../../atelia-storage/src/Rbf/Internal/RbfFileImpl.cs) Builder；DG [StateRevisionStore](../../src/DurableGraph.Storage/StateRevisionStore.cs) 实际使用 Builder。
- **复杂度成本 / 最小替代：**不用补 Fence 来救半帧；从完整旧边界舍弃整次未完成 append，仍校验所有保留依赖。
- **删除失败：**新帧最后 Fence 短写、无发布引用，若一律拒绝，最普通的写入中止仍永久需要人工。反过来全长坏 CRC 不能凭其在末尾就删除。
- **裁决 / 置信度：**`simplify`，F1 条件下中高；形态不是故障原因证明，也不覆盖所有 F2 残留。
- **修订事实：**主线程核验 [SinkReservableWriter.FlushCommittedData/Commit](../../../atelia-storage/src/Data/SinkReservableWriter.cs)：HeadLen reservation 在内存阻挡下推，填好后才顺序 push。撤销“Builder 回填意味着磁盘先留空洞再随机覆写”的推测；OS 页回写仍另证。

## R3：轮转唯一向前完成，覆盖创建半个 header

- **位置 / 主张：**方案 §4.2；草案有发布 next / 保留旧段处理 next 两种策略，且只覆盖有效空段。
- **需求来源 / 当前消费者：**[RotateActiveSegment](../../../atelia-storage/src/RbfSegmentStore/RbfSegmentStore.cs) 的 old flush → next create/flush → locator → next append；持续写入最终必然轮转。
- **复杂度成本 / 最小替代：**只向前完成 locator；0–3B 合法 HeaderFence 前缀按创建协议补齐并 flush，不提供删除 next 的另一套策略。
- **删除失败：**old 已 durable → CreateNew 建立 0B 文件 → header 写前崩溃；Strict Open 拒绝 NextSegmentPresent，普通 RBF scanner 连 header 都无法打开。
- **裁决 / 置信度：**`simplify / keep`，高；实际短写字节集合须验证。未知内容、冲突后继、old locator 下有数据的 next 不套用此规则。
- **改判：**最小架构师撤回“只处理完整 header-only 足以覆盖轮转”的隐含前提。

## R4：Close 与 Archive 保留语义，以现记录补全

- **位置 / 主张：**方案 §4.2；不立即重写为单一 Archive 权威。
- **需求来源 / 当前消费者：**[ArchiveRef/LoadRefState](../../../atelia-storage/src/EventJournal/EventJournal.Refs.cs)；现有按 RefId 操作与按名字访问需要分别关闭旧身份、解绑名字。
- **复杂度成本 / 最小替代：**只对当前 catalog 中仍绑定、末 move 为 Close 的 pair 补 Archive；复用容量/checkpoint/flush，不新增 WAL 或全仓水位。
- **删除失败：**只删 Close，旧 RefId 仍可能 Move；只截尾，Close durable → Archive 前崩溃，名字占用且 GetHead 失败。后者已有 [AllocationWithoutBindAndCloseWithoutArchiveRemainDistinct](../../../atelia-storage/tests/EventJournal.Tests/CatalogCheckpointTests.cs) 见证。
- **裁决 / 置信度：**`keep / simplify`，高；完整 Archive 已解绑不补、名字重绑不匹配旧 ref、半 Archive 截尾再补。补全 helper 不能调用本就拒绝 Closed 的 ArchiveRef。
- **改判：**无需立即承担 sole-Archive 的 API/reflog 重构；该反例用既有记录可闭合。

## R5：孤儿只规范化容器，不合成 Init/BindName

- **位置 / 主张：**方案 §4.2；“保留 orphan”不足以处理其初始化残片，最终采用 neverBound + 真实创建前缀资格。
- **需求来源 / 当前消费者：**[CreateBranch/ForkBranch](../../../atelia-storage/src/EventJournal/EventJournal.Refs.cs)、[RbfFile.CreateNew](../../../atelia-storage/src/Rbf/RbfFile.cs)；DG [ValidatePhysicalFiles](../../src/DurableGraph.Persistence/HistoryJournal.cs) 扫所有 RBF，toolkit [InventoryStores/AddStore/ValidateRefs](../../../atelia-storage/tools/EventJournal.Toolkit/JournalToolkit.cs) 检查库存与 legalEmpty。
- **复杂度成本 / 最小替代：**没有对象目录则不创建；已开始的合法初始化补到唯一首段 header/locator，截去合格的未完成 Init、保留完整 Init；不合成业务 Init/Bind，不回收 allocation/RefId。
- **删除失败：**allocation durable → orphan 首段 0B → 崩溃，“忽略孤儿”仍被 DG 全文件扫描拒绝；Bind→Archive 后当前无名字，若误当 neverBound 则会破坏旧身份。F1 缺 locator 但已有 Init 也不是合法创建前缀。
- **裁决 / 置信度：**`simplify / keep`，高；必须从保留事实证明 neverBound，不由 catalog 缺名字推导。
- **第三轮改判：**架构师撤回合成 Init：toolkit legalEmpty 接受单段 4B 的空未发布对象。捍卫者撤回一直不补 locator：现有 audit 会留下 MetadataMissing。缺 locator 单独可重建，不是永久不能 rebuild；缺 segments/零段的库存才会阻止候选。补初始化 metadata 有真实工具依据。

## R6：共用校验，承认主动恢复检查成本

- **位置 / 主张：**方案 §4.3；候选视图不能另造 root-only 有效性，也不能仅在 Strict Open 抛异常时检查。
- **需求来源 / 当前消费者：**DG [ValidateHistory/ValidateGraph](../../src/DurableGraph.Persistence/Repository.cs) 包含所有完整 orphan Journal；storage Strict Open 对 ref 惰性加载，DG 当前已遍历所有 active branches。
- **复杂度成本 / 最小替代：**窄限长/metadata 视图复用原 parser/validator；只有有动作才增加预检。显式恢复主动检查 active refs，公开 O(active refs)；Strict Open 保留有界合同。
- **删除失败：**完整 orphan Journal 引用候选丢弃的 State，root-only 预检允许先改盘，DG 正常重开却失败；Close-without-Archive 也能底层打开成功、业务失败。
- **裁决 / 置信度：**`merge / keep`，高；接缝须 S0 具体实现证明，不运行模型 factory/Upgrade 来决定删数据。

## R7：回执延后，预算按真实规模决定

- **位置 / 主张：**方案 §4.1/§5；多维预算与 durable receipt 不应扩成平台。
- **需求来源 / 当前消费者：**用户要日志，但未要求跨第二次崩溃审计；[SegmentStoreOptions](../../../atelia-storage/src/RbfSegmentStore/RbfSegmentStoreOptions.cs) 当前阈值 64 GiB，单帧可越过阈值。消费者为恢复宿主。
- **复杂度成本 / 最小替代：**一个读取限额加取消；返回实际动作报告，不计算损坏前虚构 head；耐久回执延后到有明确审计消费者。
- **删除失败：**没有工作控制可扫描极久；过低 cap 让合法运行达到规模后确定性停机。修复后报告前再次崩溃会丢诊断，但业务恢复正确性不能依赖日志。
- **裁决 / 置信度：**`simplify / defer`，高；限额和恢复时长仍待测量。

## R8：小实验不是无人值守断电能力交付

- **位置 / 主张：**S0 保留为内部实验，S1/S2 是保证门；不先发布宽泛名字的局部恢复产品。
- **需求来源 / 当前消费者：**用户的长期运行和断电场景；[storage 合同](../../../atelia-storage/docs/EventJournal/bounded-online-io-contracts.md) 与 DG 当前保证均未证明目录断电持久性。
- **复杂度成本 / 最小替代：**同一恢复语义分证据门推进，不建两套恢复 API；平台真实反例若要求新持久证据，再选择最小写协议改动。
- **删除失败：**S0 通过但 next create/Close/rename 中止仍停机；或者把 kill 测试误当断电证明，在依赖丢失时仍宣传安全自愈。
- **裁决 / 置信度：**`keep / user-decision`，高；首次公开保证范围、支持平台与恢复时长是产品选择。

## 删减量与结果边界

可观察的删减：自动边界搜索由“反向候选 + 连续验证”收为一种正向路径；rotation 从两个候选动作收为一种；删除 orphan 合成 Init 的业务写入；byte/frame 多轴预算收为一个读取限额；耐久回执退出近期范围。未将这些概念数伪装成代码行数或性能收益。

保留：完整事实及依赖、单 writer 生命周期、恢复再中断验证、F1/F2 区分。不增加全仓 commit 序号、通用恢复 WAL 或旧格式兼容层。
产品选择只维护于[方案 §7](0085-joint-crash-recovery-design.md#7-待评审与产品选择)。实施从 S0 的单次真实提交闭环开始；本轮文档不改变 runtime 严格拒绝语义与 AGENTS。
