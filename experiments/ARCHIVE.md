# 已退出实验的 Git 来源

活动回归见 [实验入口](README.md)；本页只定位已退出源码，不维护旧方案的排期或运行步骤。
本地 Git 保留以下来源，不宣称已推送。

| 退出材料 | 删前来源 |
|---|---|
| <a id="source-generator-history"></a>SourceGeneratorHistoryProbe | `research/probes-archive-20260907` 下 `experiments/SourceGeneratorHistoryProbe/` |
| <a id="snapshot-upgrade-shape"></a>SnapshotUpgradeShapeProbe | 同标签下 `experiments/SnapshotUpgradeShapeProbe/` |
| <a id="multi-segment"></a>MultiSegmentStateStoreProbe | 同标签下 `experiments/MultiSegmentStateStoreProbe/`；地址与轮转已由 FrameStore 接管 |
| <a id="two-leg"></a>TwoLegRotationProbe | 同标签下 `experiments/TwoLegRotationProbe/`；双腿轮转未选为产品路线 |
| 旧存储提取、组织/API 和 DurableBase 迁移 lanes | `c900edd5b3a921b7bf46df2ac0482dc39597e606` 下对应 `experiments/PackageConsumerProbe/` 路径 |
| 旧 raw 消费程序、逻辑图 Probe 与本轮研究工件 | `6dbaf0e`，范围及继任见 [DB-091](../docs/design-branches/0091-retired-material-cleanup/README.md) |

四个早期 Probe 的标签对应 `96885b691dc20c2a8861694c47f83aa1a0a65c0b`。
需要历史证据时先只读查询；旧源码不构成当前 API、格式或性能资格：

```powershell
git ls-tree -r --name-only research/probes-archive-20260907 -- experiments
git show research/probes-archive-20260907:experiments/SourceGeneratorHistoryProbe/README.md
```

保留的模型编译资产见 [PackageConsumerProbe](PackageConsumerProbe/README.md#model-fixtures)。
