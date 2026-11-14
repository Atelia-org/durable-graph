# DB-100：独立对象图堆

2026-11-15，产品代码已集成 master，正在最终源码与普通包验收。用户已选择 memo 的[对象图堆方案](../../../../memo/docs/design/graph-heap.md)，授权实现并集成 DurableGraph 主线。原型无兼容性包袱。此记录承载本片实施边界与验收证据；当前产品状态见 [PROJECT-STATE](../../../src/PROJECT-STATE.md)。

## 问题与选定合同

memo 世界根只保存 `EntryId → GraphRef` 目录，条目正文是按需完整加载的独立对象图。图在盘上不可变，打开得到可改 builder，增量保存返回新引用；回溯世界线只选择该世界目录中的图。接受全图捕获/读取与目录开销。

- Repository 提供 CreateGraph、OpenGraph、SaveGraph；公开角色为 GraphBuilder 与普通 inline GraphRef。多个 builder 可交错保存，不管理每图分支。
- 一个保留内部 Head 复用 Schema/data-first/联合发布与故障协议；日志追加前驱不决定内容基线。业务分支/历史枚举不暴露内部日志。
- State 图自身没有永久编辑父。冷读只由图地址确定 Root/完整目录/Schema/OwnedBatch；实际对象与目录 Delta 沿各自 source/prior 重建。Open(G) 的下次保存基线就是 G。
- 写计划仍以真实 builder 的 source 准备，安装仍核完整候选、未写 heads、Schema、重建字节与 rewrite；owner、日志 CAS 和故障检查保持。
- 普通 WorkingCopy 历史与 StateSourceCommitId 语义不变；Meta.PreviousMeta、独立 Event 全 Base 不改义。
- 不保留实验 flag、State expected-source 读取、旧格式适配或双模式。完整帧仍 append-only。

已验证机制与历史反驳见 memo 的[裁决](../../../../memo/docs/research/graph-heap/main-review.md)、[无图级父实验](../../../../memo/docs/research/graph-heap/sourcefree-probe.md)。此前 DB-096 的最近 State 来源权威设计由本片替代；其日志导航与发布语义仍有效。实验分支继续保留，没有把研究测试直接当产品验收。

## 实施分工与契约

基线 `f7f0c28bacaa67993b7b07a436c3d82bc4ebdda8`，Storage 仍使用 eng/StorageDependency.props 的准确候选包。三个隔离分支由主线程独立复核后集成 master：

| 工作包 | 独占范围 |
|---|---|
| core | 唯一 State address-only 读取、snapshot 类型、adapter/安装、prepared 冷热恢复、旧测试迁移 |
| heap | reserved Head 与保存准入、Repository 三操作、GraphBuilder、公开生命周期测试 |
| ref | GraphRef inline 值、真实生成模型与普通包消费者 |
| 主线程/独立审查 | 契约协调、实际代码复核、集成测试、文档及主线提交 |

共享接缝：`ReadBusinessGraph(address)` / `ValidateBusinessMetadata(address)` 不收 expected source；StoredGraphRevision 删除图级 SourceGraph，PreparedGraphWrite/对象批次/目录的 source 保留。GraphRef 内部由准确 CommitId 签发，不提供任意 Commit 的公共导入接口。

## 最小验收

1. 公共生成模型完成 A0/B0/A1(from A0)/A2(from A0)、no-op、循环/共享、换根删除；全部关闭后准确打开并继续增量保存，断言实际 Delta。
2. Schema变化、升级及必须Base重写；错误owner/prior/目录/候选安装/stale日志Head仍拒绝。原普通State/Event、多分支与冷热缓存回归。
3. 内部Head空库/首次写/重开/只读/fault/reentry与业务分支不可见；发布前后故障沿原outcome，不提前安装builder。
4. 世界目录选择A1/A2，用简单目录枚举加字符串匹配验证不串台；GraphRef可作为普通字段与字典值持久保存，不隐式展开目标。
5. 根solution build、相关及集成回归、准确新候选普通PackageReference消费者；保来源与测试范围，不把单元故障注入当新增掉电资格。

## 当前结果

产品已集成：GraphRef `36a3ca8`、State合同 `6574660` 与迁移/新反例 `16f4bd8`、Heap `ae2784a`、日志准入/四Head回归 `2386cac`。包检查工具与新三进程消费者为 `907af22` / `03d2c58`。没有引入新的生成器特殊类型，GraphRef 使用既有 inline 导出。

集成 Release 根构建 0 warnings/errors；独立 reviewer 在 `ae2784a` 重跑 GraphHeap/GraphRef/SourceFree 三类共 **21/21**，TRX `.artifacts/graph-heap/independent-review/independent-graph-heap.trx`。原完整套暴露的旧最近State禁令及三Head断言已迁移，相关 **56/56** 通过；最终完整套与新候选包结果待回填。审查还修正堆失败异常：TargetBranchId 为 null，保留的候选Commit只作诊断，恢复按世界已选GraphRef进行。

主线程已审实际实现、区分性测试及独立审查结果；包消费者将验证普通PackageReference、SG/Build、三进程冷续写与世界目录限定搜索。不触及用户并行的DurableText。稀疏图、GC、跨图可变身份、通用索引、并发合并与旧数据迁移均不属于本片；不新增掉电资格。
