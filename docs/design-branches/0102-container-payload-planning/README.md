# DB-102 容器 payload 准备与对象重整策略

业务 State 保存沿用现行图级软预算。规划器对有准确前驱的业务 string 和支持精确计量的 List 先取得规范 Base 大小，选中 Base 后才编码正文。List 的固定宽度 byte、double 从 Count 计长，Int32 和对象 ID 按实际变长编码计长。自定义元素绑定使用自己的编码器。已有 DurableText 沿用分段估算。[表示策略](../0015-statestore-object-representation-policy/README.md) · [业务保存准备](../../../src/DurableGraph.Persistence/StoredGraphBodies.cs)

List 的 CopyAndPatch 对已验证的内建标量直接写出子补丁。整链读取在同位置、全长、单区间的更新中复用本次读取独占的数组，重排、重复来源和变长区间按各自语义生成结果。准确前驱、区间边界、补丁索引和正文完整性仍逐层校验。[List 编解码](../../../src/DurableGraph/Runtime/Containers/ListStateReader.cs)

[开题报告](research-protocol.md)与完整实验在独立分支 `research/db102-study` 的结题提交 `bc7e70f5` 对应。研究中，05-care 的 230 次 State 保存累计少分配约 638 MB。65,536 项 double 的 128 层单点修改从约 72.32 MB 物化分配降到 5.19 MB。密集编辑后换新大 int 列表有 0.37–1.61 ms 的单步退让。各工作负载的配对结果、异常、原始证据和复现命令可从以下入口查阅。

```powershell
git show bc7e70f5:docs/design-branches/0102-container-payload-planning/results.md
git show bc7e70f5:docs/design-branches/0102-container-payload-planning/reproduce.md
```

## 主线验收

主线通过普通业务 State 保存和完整读链入口验证三项优化。使用项目固定的 Storage 源版本构建根 solution，零警告、零错误。四个测试程序集共 3,315 项通过，零失败、零跳过。公开 Capture 继续立即生成 Base。独立 Event 和新对象写完整 Base，已发布 head 和重建字节 H 根据实写记录安装。[保存计量与冷开](../../../tests/DurableGraph.Persistence.Tests/BusinessPlanningAccountingTests.cs) · [选择与强制重写](../../../tests/DurableGraph.Persistence.Tests/ObjectRevisionPlannerTests.cs)

| 产品源码与基线 `b9eef489` 的对照 | 观测 |
|---|---|
| 原始 05-care，230 次 State 保存 | 逐步业务指纹、实写内容、前驱、head/H 与文件增长相同，也与研究推荐臂一致。独立进程打开 State 和完整应用，第 231 步续写后重新打开。全量测试退出后串行复测，AB/BA 两对的 State 线程分配各少 638.25 MB 和 638.23 MB（约 10.67%）。累计耗时一对上升 0.17%，另一对下降 5.43%，未观察到稳定的延迟改善。 |
| 内存中编码一张 65,536 项 double 表的密集 Delta | 四次编码的正文相同，单次线程分配从约 27.48 MB 降至 2.84 MB。内建标量固定字节、浮点位模式、变长值、自定义操作与竞争计划的独占上限由[编码回归](../../../tests/DurableGraph.Tests/ListBoundedEncoderTests.cs)覆盖。 |
| 在内存中从 Base 恢复 128 层单点 Delta | 每次恢复都逐元素核对，四次线程分配从约 67.65–67.68 MB 降至 0.525 MB。重复来源、变长范围和无效补丁的失败隔离见[读链回归](../../../tests/DurableGraph.Tests/ListRangeDeltaTests.cs)。 |

完整候选分析及开题时的设计草案位于上述研究提交。本地验收原件为 `W:/DB102/product-accept/acceptance.json`、`W:/DB102/product-accept/quiet1.json` 和 `E:/db102/mainline-list-accept/comparison-product.json`。
