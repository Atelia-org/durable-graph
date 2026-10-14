# DB-069：State 基线的对象 head／H 增量安装

保留已验证来源与确认发布后推进的实际对象计量；旧 FileScope helper、raw-map 选择器、LoadedRevisionPlanner／WorldWorkspace 保存链及其前后测量不再作为现行接线。

## 当前合同

- 冷恢复先准确解码、完整校验对象链与所选 Schema／models，保存每个 source 对象的实际 head 与重建 payload H。受控热保存复用该 DTO 基线的已验证事实，但仍核同一 Store／owner、模型环境、实际图来源和完整 source 成员，包含将被 Remove 的成员。
- 捕获候选的 unwritten 对象保留旧 head/H；本次实际记录由真实追加结果读回。不能用另一个 owner／候选／Schema view 或人工 map 替代安装材料。
- `NormalizedRevision.WithSplitGraph` 核实际 graph、content source、完整成员、预期 writes 和准确 selected view，然后准备安装结果。Base 的 H 重置为该实际记录消费字节；Delta 的 H 为旧 H 加实际记录字节，其 prior 必须等于旧 head；未写入的 NoChange 保原 H/head，Remove 只退出新目录。
- B/D 是本次规范输出的精确 payload；H 保实际输入的字节长度，合法冗余输入不得重编码后缩小历史 H。对象 payload 与目录／共享帧／物理 I/O 继续分开计量。
- 追加后先准备全部安装材料，再发布；只有确认发布的 State 才接受 DTO／身份／root／head/H 基线。安装准备失败不能推进 refs 或旧基线，已完成 orphan 不回收。
- Event 可发布自身 Head／meta，但不接受 State 基线；Discard 不推进 State，也不消费仍 live 的 Upgrade rewrite 要求。后继 State 仍从最近 State 的实际内容来源规划；需要重写的对象先用 Base，再能继续 Delta。

## 当前实现与验证入口

- [NormalizedRevision](../../../src/DurableGraph.Persistence/NormalizedRevision.cs) 的 `RequireSplitSource`、`WithSplitGraph` 与实际记录安装计量。
- [共同 workspace／候选](../../../src/DurableGraph.Persistence/BusinessWorkspace.cs)、[P0 producer／安装准备](../../../src/DurableGraph.Persistence/MetaBootstrapP0/P0BusinessWorkspace.cs)、[唯一发布 owner](../../../src/DurableGraph.Persistence/GraphResources.MetaBootstrap.cs)。
- [混合新／保留 head/H、完整 source 与冷续写](../../../tests/DurableGraph.Persistence.Tests/P0NormalizedBusinessBaselineTests.cs)、[真实 B/D/H、Event／Discard 与确认 Base](../../../tests/DurableGraph.Persistence.Tests/BusinessPlanningAccountingTests.cs)、[候选安装材料归属](../../../tests/DurableGraph.Persistence.Tests/BusinessSaveCandidateInstallationTests.cs)。

这些接缝免除受控保存中旧对象链的重复计量，不承诺完整 Commit 与历史长度无关、总 O(k) 或完整 Fork 普遍加速。目录回放、完整 live Base 准备与其他成本只按真实工作负载证据再评估，未完成工作维护在[路线图](../../DurableGraph-research-roadmap.md)。

历史正文可用 `git show 6dbaf0e:docs/design-branches/0069-incremental-save-baseline/README.md` 查阅；旧原始样本在同修订 `docs/research/incremental-save-baseline-results.json`。那些时间／分配数据对应已退出的 raw 保存实现，不授当前 FS/VS／共同 saver 性能资格。
