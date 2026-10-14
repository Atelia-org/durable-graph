# 实验与回归入口

产品当前能力与下一步见 [src/PROJECT-STATE.md](../src/PROJECT-STATE.md)。
按正在调查的问题选择入口；无需顺序阅读所有 Probe。下列项目均不在根 solution 中，
运行命令、证据范围与限制由各自 README 维护。


当前旧 reader/raw Store 包 lanes 暂停。直接旧 raw 消费的 23 个 C# 文件/19 目录及须保留的 SG 业务资产，只维护在 [PackageConsumerProbe 静态清单](PackageConsumerProbe/README.md)；源码/tests 清零不表示全仓已迁。历史验收保留，本片不整体停用 Probe。

旧 typed 恢复与 SCB1/owner 退出保留在 [DB-087 §36](../docs/design-branches/0087-meta-bootstrap-refactoring-route/0087-meta-bootstrap-refactoring-route.md#36-p1d-第七片旧typed恢复束退出)／[§37](../docs/design-branches/0087-meta-bootstrap-refactoring-route/0087-meta-bootstrap-refactoring-route.md#37-p1d-第八片旧schema权威与owner模式退出)；十一 raw 类型及旧 GraphMetadata/TailMeta 路径退出见 [§38](../docs/design-branches/0087-meta-bootstrap-refactoring-route/0087-meta-bootstrap-refactoring-route.md#38-p1d-第九片旧raw-storage束退出)。公开 accepted-history 与旧双异形 private 代表包保 DB-087 原有限 P1／P2 资格；DB-088 删特例后的三条活动故事已以新 G 通过，当前来源见 [DB-088 §7](../docs/design-branches/0088-development-schema-workflow/0088-development-schema-workflow.md#7-实施结果与限定验收)；ListDeltaReplay 与其他 source／包各保范围，不授未迁19 lanes当前 G资格。

| 何时使用 | 入口 |
|---|---|
| 修改 Generator 输出、runtime 公共 API、history/build 或 package 接线，需要验证真实单包消费者 | [PackageConsumerProbe](PackageConsumerProbe/README.md)：活动产品回归 |
| 验证 DB-087 内部员工 Schema 闭包、无业务注册的 meta 冷读与跨正常进程 Delta 续写 | [MetaBootstrapSchemaConsumer](PackageConsumerProbe/MetaBootstrapSchemaConsumer/README.md)：内部包验收，不代替公开切换或进程故障门禁 |
| 验证 DB-087 公开默认 Repository 的 accepted-history／Upgrade／准确 S／tag／历史 Fork／Event／ReadPair 与冷开 Base→Delta | [SplitRepositoryConsumer](PackageConsumerProbe/SplitRepositoryConsumer/README.md)：[runner](PackageConsumerProbe/Run-SplitRepositoryProbe.ps1)，原已验有限普通包及 P2 同形新根／完整 ToShared；DB-088 活动 baseline／Event 全目录故事已以新 G 通过，不授故障或性能 |
| 验证 baseline Point V1→V2／Nullable lifting／List 元素 Upgrade，保身份及完整目录／冷 Delta | [PrivateContainerConsumer](PackageConsumerProbe/PrivateContainerConsumer/README.md)：[runner](PackageConsumerProbe/Run-PrivateContainerProbe.ps1)，DB-088 迁移后新 G 七 PID 已通过；旧 A/B 异形／六 PID 仅保删除前资格 |
| 验证固定 V1 用户快照→独立 V2 草稿 fork→仅定稿 V2，实际 MSBuild Off／Publish／Verify | [DevelopmentSchemaConsumer](PackageConsumerProbe/DevelopmentSchemaConsumer/README.md)：[runner](PackageConsumerProbe/Run-DevelopmentSchemaProbe.ps1)，DB-088 有限开发流程已在删特例后的新 G 通过；精确 live IDs／prior／H 另由源码故事授资格 |
| 验证实验性 Dictionary 的 comparer、键寻址 Delta、共享引用与双槽历史升级 | [DictionaryConsumer](PackageConsumerProbe/DictionaryConsumer/README.md)（旧 reader/diagnostic lane 当前暂停）：[Run-DictionaryProbe.ps1](PackageConsumerProbe/Run-DictionaryProbe.ps1)，DB-054 产品回归 |
| 验证显式 enum 的整数表示、完整类型组合与删除旧 CLR enum 后的历史升级续存 | [EnumConsumer](PackageConsumerProbe/EnumConsumer/README.md)（旧 reader/diagnostic lane 当前暂停）：[Run-EnumProbe.ps1](PackageConsumerProbe/Run-EnumProbe.ps1)，DB-053 产品回归 |
| 验证 Nullable 字段/泛型/数组/List 的冻结、历史精确读取与显式值升级提升 | [NullableConsumer](PackageConsumerProbe/NullableConsumer/README.md)（旧 reader/diagnostic lane 当前暂停）：[Run-NullableProbe.ps1](PackageConsumerProbe/Run-NullableProbe.ps1)，DB-052 产品回归 |
| 验证泛型 class/struct、定义历史、通用/闭合 Upgrade 与多代冷重开的真实包交付 | [GenericConsumer](PackageConsumerProbe/GenericConsumer/README.md)（旧 reader/diagnostic lane 当前暂停）：由 [Run-GenericProbe.ps1](PackageConsumerProbe/Run-GenericProbe.ps1) 执行的产品回归；施工记录见 [DB-038 §12](../docs/design-branches/0038-generic-schema-state-and-binding-design/README.md#12-产品施工跟踪) |
| 验证通用 owner 显式复用值规则、嵌套工具作用域与删除旧 inline 领域声明后的历史升级 | [ValueUpgradeConsumer](PackageConsumerProbe/ValueUpgradeConsumer/README.md)（旧 reader/diagnostic lane 当前暂停）：由 [Run-ValueUpgradeProbe.ps1](PackageConsumerProbe/Run-ValueUpgradeProbe.ps1) 执行；施工与验收见 [DB-039 §8](../docs/design-branches/0039-composable-value-upgrade-design/README.md#8-产品施工合同与验收映射) |
| 重访泛型 DTO/静态 helper、工厂闭合、readonly accessor 与历史 Upgrade 代码形状的选择依据 | [GenericBindingShapeProbe](GenericBindingShapeProbe/README.md)：独立机制见证；实际产品验证使用上述 GenericConsumer 与 src/tests |
| 验证单 writer 进程中止、FS/VS 发布可见性与不完整控制尾正常恢复 | [PublicationCrashProbe](PublicationCrashProbe/README.md)：DB-086 底层故障见证，非断电保证 |
| 验证 DB-087 实际 split Repository 的两次成功创建后／SG Widget 两 ref 保存，外部 Kill 后准确根 cold／Delta | [MetaBootstrapCrashProbe](MetaBootstrapCrashProbe/README.md)：source-only 的两个固定初始化窗口＋六个保存窗口；保存证据见 §31，[本片初始化与返回边界见 §32](../docs/design-branches/0087-meta-bootstrap-refactoring-route/0087-meta-bootstrap-refactoring-route.md#32-p1d-第三片直接初始化-kill-与实际返回-outcome)，构建、矩阵与完整源码回归通过，不代包或完整 P1d 门禁 |
| 比较 List 区间匹配器在相同领域编辑历史上的完整 Commit、候选 Delta、实际落盘字节及冷读正确性 | [ListDeltaReplayProbe](ListDeltaReplayProbe/README.md)：DB-049 轻量实验，独立 Repository、交错算法、报告中位数与范围；不以性能胜负为验收门槛 |

四个已完成或未采用路线的旧 Probe 已退出活动工作树，完整源码与证据保存在 Git 恢复点；
只在明确问题需要时查 [归档索引](ARCHIVE.md)。默认搜索与回归使用上表入口及 src/tests，
不为跟随产品演进而维护或恢复旧 Probe。

Coding Agent 模型/思考强度的工作方法实验另见[评测材料索引](../docs/agent-evaluations/README.md)，不计入产品机制或回归能力。
