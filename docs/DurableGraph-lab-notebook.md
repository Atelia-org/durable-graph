# DurableGraph 实验与验证证据入口

本页索引仍有用的证据与没有独立记录的小实验。当前能力见[产品工作集](../src/PROJECT-STATE.md)，
未完成事项见[路线图](DurableGraph-research-roadmap.md)，当片结果留在对应 DB。

| 问题 | 当前证据入口 |
|---|---|
| Schema/history 与真实包交付 | [PackageConsumer](../experiments/PackageConsumerProbe/README.md)、[生成器测试](../tests/DurableGraph.Tests/DurableSchemaGeneratorTests.cs) |
| sharing/cycles、退边与冷恢复 | [真实引用图](../tests/DurableGraph.Tests/PersistedReferenceGraphTests.cs)、[失败与两阶段物化](../tests/DurableGraph.Persistence.Tests/LoadedReferenceWorldTests.cs) |
| DTO Capture、string 身份与读取 | [DB-022](design-branches/0022-versioned-state-dto-capture/README.md)、[DB-024](design-branches/0024-reference-capture-and-reusable-object-ids/README.md)、[DB-025](design-branches/0025-string-object-decoding-slice/README.md) |
| 同版 DTO 融合 Delta | [DB-027](design-branches/0027-generated-same-schema-delta-body-slice/README.md)、[body 回归](../tests/DurableGraph.Tests/FusedDeltaBodyTests.cs) |
| 实际 B/D/H、prior 与确认后基线 | [DB-029](design-branches/0029-prepared-object-revision-planning-slice/README.md)、[DB-069](design-branches/0069-incremental-save-baseline/README.md)、[链反例](../tests/DurableGraph.Persistence.Tests/MetaBootstrapP0IntegrityTests.cs) |
| 进程中止与两根发布 | [MetaBootstrapCrashProbe](../experiments/MetaBootstrapCrashProbe/README.md)；不授断电或全部故障窗口保证 |

<a id="state-only-quickstart"></a>
## 2026-10-15：纯 State 快速入门

问题来自 workshop 反馈 3：状态型应用是否能按文档直接连续保存 State，并使用历史与分叉，而无需先提交 Event？
[根 README](../README.md#最短接入路径)将该路径设为首个示例，保留共享/循环引用、Transient 重建和相同实例续写；
Event 类型与 E/S 保存移到可选章节，提醒两次提交各自发布。运行时、公共 API 和持久格式不变。

最小验收由 [README 原文 runner](../experiments/PackageConsumerProbe/Run-ReadmeQuickStartProbe.ps1)承担：
两进程产生 S0/S1/S2（100/99/98），没有 Event；从 S1 分叉存为 89，全关重开续存为 88；
另一个进程核对共同祖先、旧 State、主分支和零 Event。随后才添加 Event 模型，保留事件读取、策略、Upgrade/Verify 及原交付正反例。
本轮普通包验证结果待补充；原始工件放在 `.artifacts/state-only-quickstart/20261015/`。

## 历史来源

旧 EXP 日志、轮转、raw Store 研究和冻结长稿已退出工作树，来源见
[DB-091 Git 索引](design-branches/0091-retired-material-cleanup/README.md#git-history)。
历史正文中的 Current/Next 只解释原时期，不作为当前续工顺序。

### 2026-09-07：无构造器分配与 readonly 实例字段写入

当时 .NET 10.0.5 临时实验观察到 GetUninitializedObject 不运行构造器/字段初始化，
UnsafeAccessor 可填充 private readonly 与循环引用；完整记录在 `6dbaf0e` 的本文件。
当前产品资格由 [readonly 模型](../tests/DurableGraph.Tests/LoadedWorldGeneratorTests.cs)
与上述引用图/两阶段恢复测试承担，不将旧局部 CLR 实验推广为 AOT 或跨运行时保证。

新小实验只记问题、观察、边界和来源；已有 DB 记录时添加必要链接，避免重复流水账。
