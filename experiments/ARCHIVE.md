# 已归档 Probe 恢复索引

2026-09-07，为减少默认搜索中的旧实现和过时设计，以下四个 Probe 的源码、测试和局部文档
已退出活动工作树，不在本仓库另放一份源码副本。产品开发从 [PROJECT-STATE](../src/PROJECT-STATE.md)
进入；仍在使用的包回归与进程故障见证见 [实验入口](README.md)。

归档恢复点：annotated tag `research/probes-archive-20260907`，对应提交
`96885b691dc20c2a8861694c47f83aa1a0a65c0b`。本轮只建立本地标签，不宣称已经推送。
此点保存四个 Probe 的全部 300 个版本控制文件（44,297 行），不包含 ignored 构建产物。
归档不等于其所有技术均已采用，也不要求以后持续适配 SDK、产品 API 或术语。

归档验收：300 个文件逐一确认可从标签找回，默认 `rg --files` 不再列出旧 Probe 文件；
保留项目无旧路径依赖，13 处历史链接转接并保留原路径；根 build 零警告/错误，916/916 测试通过。
本地忽略的构建缓存未清理，不属于归档内容，也不进入默认搜索。

## 已完成机制

<a id="source-generator-history"></a>
**SourceGeneratorHistoryProbe**（原目录 `experiments/SourceGeneratorHistoryProbe`，9 文件）

- 问题：AddSource 是否自动跨编译保留；成功编译后发布历史材料，再经 AdditionalFiles 消费。
- 已吸纳：[产品 MSBuild targets](../src/DurableGraph/build/Atelia.DurableGraph.targets)、
  [SchemaHistoryTool](../src/DurableGraph.Build/SchemaHistoryTool.cs)；真实消费由 PackageConsumerProbe 回归。
- 独有证据：编译器反馈负对照、局部协议；仅在编译器/build hook 假设变化时恢复调查。
- 在归档 worktree 的根执行：`pwsh -File experiments/SourceGeneratorHistoryProbe/Run-Probe.ps1`。

<a id="snapshot-upgrade-shape"></a>
**SnapshotUpgradeShapeProbe**（原目录 `experiments/SnapshotUpgradeShapeProbe`，11 文件）

- 问题：struct 的强类型 in/out 升级、确定赋值、重载及相关编译错误。
- 已吸纳：[生成的 State model/Upgrade](../src/DurableGraph.Generator/DurableSchemaGenerator.StateModel.cs)，
  [当前生成测试](../tests/DurableGraph.Tests/GeneratedStateModelTests.cs)。
- 独有证据：语言规则反例；不代表旧 Snapshot API 仍受支持，非性能测量。
- 在归档 worktree 的根执行：`pwsh -File experiments/SnapshotUpgradeShapeProbe/Run-Probe.ps1`。

## 研究储备

<a id="multi-segment"></a>
**MultiSegmentStateStoreProbe**（原目录 `experiments/MultiSegmentStateStoreProbe`，97 文件）

- 已吸纳：多 Segment 地址、soft rollover、对象链/存活目录分离、读放大 Base 预算与提交顺序。
  产品入口：[Storage](../src/DurableGraph.Storage/StateRevisionStore.cs)、
  [策略](../src/DurableGraph.Persistence/ReadAmplificationBaseBudgetPolicy.cs)、
  [Repository](../src/DurableGraph.Persistence/Repository.cs)。
- 未迁入：合成 workload/corpus、评估器、共享 Frame 冷读去重计量及 provisional envelope。
  它们是内存模型，不是产品 wire、性能或恢复保证。需要具体测量/策略研究时才恢复。
- 在归档 worktree 的根执行：`dotnet test experiments/MultiSegmentStateStoreProbe/MultiSegmentStateStoreProbe.slnx`。

<a id="two-leg"></a>
**TwoLegRotationProbe**（原目录 `experiments/TwoLegRotationProbe`，183 文件）

- 双腿拓扑、evacuation、债务和轮转控制未选为产品路线；后继方向是
  [DB-014](../docs/design-branches/0014-multi-segment-backward-file-distance.md) 的多历史 Segment。
- 仅在真实需求要求常数依赖文件数、在线旧文件退休、compaction/backup/rescue SLO 时恢复评估。
  原策略不完备，不把冻结 baseline 当作可直接采用的完整 cleaner。
- 在归档 worktree 的根执行：`dotnet test experiments/TwoLegRotationProbe/TwoLegRotationProbe.slnx`。
- 更早独立冻结标签 `research/two-leg-rotation-probe-tech-reserve-20260902` 保留，不替换。

## 精确取回

先只读查看所需文件，避免重新把整个旧项目放回产品搜索范围：

```powershell
git show research/probes-archive-20260907:experiments/MultiSegmentStateStoreProbe/README.md
git ls-tree -r --name-only research/probes-archive-20260907 -- experiments/MultiSegmentStateStoreProbe
```

确需运行时，在当前仓库**外部的相邻目录**创建独立 worktree，保留整套原相对路径：

```powershell
git worktree add --detach ../durable-graph-probe-archive research/probes-archive-20260907
```

旧文档链接的标题中保留“原路径”；可将该路径接在 `git show <tag>:` 后查看对应文件。
旧方案的状态与命令只在归档快照中解释，不是新施工授权。运行须具备原 .NET/包源环境；涉及相邻
`atelia` 的历史命令还须核对其依赖，不承诺归档代码在未来 SDK 下持续可构建。

## DB-086 旧存储迁移 lanes（2026-10-11）

旧数据无需保留，旧栈格式不进入新的运行时验收。以下专属工件退出活动树；
源码恢复点为 `c900edd5b3a921b7bf46df2ac0482dc39597e606`，按明确历史问题用 Git 读取，
不随新 API 持续维护：

- `PackageConsumerProbe/StorageExtractionConsumer` 与 `Run-StorageExtractionProbe.ps1`：固定 DG、替换旧五 storage 包、保留原帧。
- `PackageConsumerProbe/OrganizationMigrationConsumer`、`OrganizationApiInventory` 与 `Run-OrganizationMigrationProbe.ps1`：旧/新 DG 组织迁移和 API 映射。
- `PackageConsumerProbe/Run-DurableBaseMigrationProbe.ps1`：旧框架基类到 marker 的持久库迁移。

当前新格式的模型升级、容器、跨程序集和冷进程 branch/tag 验收仍由
[PackageConsumerProbe](PackageConsumerProbe/README.md) 的活动消费者承担。
