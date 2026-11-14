# DB-100：独立对象图堆

2026-11-15，产品已集成 master，完整源码回归与三组普通包消费者验收通过。用户已选择 memo 的[对象图堆方案](../../../../memo/docs/design/graph-heap.md)，授权实现并集成 DurableGraph 主线。原型无兼容性包袱。此记录承载本片实施边界与验收证据；当前产品状态见 [PROJECT-STATE](../../../src/PROJECT-STATE.md)。

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

## 实施结果与验收

产品已集成：GraphRef `36a3ca8`、State合同 `6574660` 与迁移/新反例 `16f4bd8`、Heap `ae2784a`、日志准入/四Head回归 `2386cac`。包检查工具与新三进程消费者为 `907af22` / `03d2c58`。没有引入新的生成器特殊类型，GraphRef 使用既有 inline 导出。

主线程最终 Release 根构建 **0 warnings/errors**；完整 solution **3194/3194，0 skipped**：Persistence 1230、Runtime/Generator 1795、Serialization 133、Storage 36。日志 `.artifacts/graph-heap/build-final.log` / `test-final.log`，四份 TRX 在 `.artifacts/graph-heap/full-tests/`。

```powershell
dotnet build DurableGraph.slnx -c Release --no-restore -m:1 -nr:false -p:UseStorageSources=false
dotnet test DurableGraph.slnx -c Release --no-build --no-restore -m:1 -nr:false -p:UseStorageSources=false --logger trx --results-directory .artifacts/graph-heap/full-tests
```

构建前使用准确 Storage feed 和独立配置 restore，未使用共享 Storage 源码输出。独立 reviewer 在 `ae2784a` 重跑 GraphHeap/GraphRef/SourceFree 三类共 **21/21**，TRX `.artifacts/graph-heap/independent-review/independent-graph-heap.trx`。主线程审读产品差分、关键新测试与旧断言迁移；审查修正了堆失败异常：TargetBranchId 为 null，保留候选Commit只作诊断，恢复按世界已选GraphRef进行。后继代码改动仅为旧回归迁移，已被最终完整套覆盖。

### 普通包与准确来源

两个候选均从干净固定提交 **`7a98c7d224f87e0f7ec455417a0a4a2d13d5fa33`** 构建；后续主线只有验收文档更新。Storage 为 `0.1.3-dev.20261101.g9ad39fc` / `9ad39fc51a420e8302468533b82d8bc3a91a403e`，未修改 pin。

| 消费者 | 候选与观察 | 本机结果回执 |
|---|---|---|
| GraphHeap | G1 `0.0.0-graphheap.20261115.g7a98c7d`；普通PackageReference、包内SG/Build、Publish→Verify历史不变，三个独立进程46项检查；6条命令全部exit0 | `E:/repos/Atelia-org/memo-package-evidence/graphheap-7a98c7d/result.json` |
| SplitRepository | G2 `0.0.0-public-history-e2e.20261114163251.38236`；五个独立运行进程成功；九条构建/还原命令包含一个精确CS0122预期拒绝 | `E:/repos/Atelia-org/memo-package-evidence/split-controlled-7a98c7d/result.json` |
| PrivateContainer | 复用G2及其受控打包来源；七个独立运行进程、六条构建/还原命令全部成功 | `E:/repos/Atelia-org/memo-package-evidence/private-controlled-7a98c7d/result.json` |

三份回执均 Passed=true、Failure=null，分别核验四个DG与六个Storage包及实际消费来源。主线程回读了结果、进程/命令、精确负例与来源字段。GraphHeap覆盖生成EntryId→GraphRef目录、A/B交错、旧引用分叉、no-op、冷续写、共享/循环、只读拒写、缺正文模型仍可恢复目录，以及按世界目录搜索不串世界/孤儿。

G1由 `eng/Prepare-DurableGraphCandidate.ps1` 在隔离工作树导出，路径为 `E:/repos/Atelia-org/memo-implementation/package/.artifacts/candidates/0.0.0-graphheap.20261115.g7a98c7d/`。G2由既有Split runner真实采集打包前后208项生产输入并生成来源证明，Private复用其feed；没有为G1事后伪造旧runner要求的采集记录。G2 feed及 `dg-source-provenance.<G2>.json` 位于 `split-controlled-7a98c7d/feed/`。两版字节分别保留，未覆盖同版本产物或公开发布。

本片必需实现与验收已闭合。没有触及用户并行的DurableText。稀疏图、GC、跨图可变身份、通用索引、并发合并与旧数据迁移不属于本片；没有新增掉电、强杀或性能资格。发布故障与实际Delta由产品测试验证，普通包验收不替代这些故障测试。后续仅按[路线图的真实消费触发条件](../../DurableGraph-research-roadmap.md)继续。
