# 实验与回归入口

产品当前能力与下一步见 [src/PROJECT-STATE.md](../src/PROJECT-STATE.md)。
按正在调查的问题选择入口；无需顺序阅读所有 Probe。独立项目通常不在根 solution 中；部分 runner 调用现有测试项目。
运行命令、证据范围与限制由各自 README 维护。

| 何时使用 | 入口 |
|---|---|
| 验证统一根视图、目录与真实段轮转及恢复再次中断 | [UnifiedRootProbe](UnifiedRootProbe/README.md)：固定双值及目录/单次轮转对照均已验收；保留 orphan、完整目录与原 EventJournal 成本对照，不是 DG 产品格式或平台断电保证 |
| 比较只读 Normal 候选与公开打开的 Open、首次查询/图读取、字节与内存成本 | [ReadOnlyNormalCostProbe](ReadOnlyNormalCostProbe/README.md)：DB-085 内部实验，复用 Persistence.Tests；受控来源，公开默认未切换 |
| 验证真实提交和恢复再次中断后的续写/冷开 | [RecoveryCrashProbe](RecoveryCrashProbe/README.md)：DB-085 S0-C 内部宿主与 S0-D CLI 闭环；新建夹具、真实短写/外部终止、输出中断后直接严格续写；不提供平台断电保证 |
| 修改 Generator 输出、runtime 公共 API、history/build 或 package 接线，需要验证真实单包消费者 | [PackageConsumerProbe](PackageConsumerProbe/README.md)：活动产品回归 |
| 验证实验性 Dictionary 的 comparer、键寻址 Delta、共享引用与双槽历史升级 | [DictionaryConsumer](PackageConsumerProbe/DictionaryConsumer/README.md)：[Run-DictionaryProbe.ps1](PackageConsumerProbe/Run-DictionaryProbe.ps1)，DB-054 产品回归 |
| 验证显式 enum 的整数表示、完整类型组合与删除旧 CLR enum 后的历史升级续存 | [EnumConsumer](PackageConsumerProbe/EnumConsumer/README.md)：[Run-EnumProbe.ps1](PackageConsumerProbe/Run-EnumProbe.ps1)，DB-053 产品回归 |
| 验证 Nullable 字段/泛型/数组/List 的冻结、历史精确读取与显式值升级提升 | [NullableConsumer](PackageConsumerProbe/NullableConsumer/README.md)：[Run-NullableProbe.ps1](PackageConsumerProbe/Run-NullableProbe.ps1)，DB-052 产品回归 |
| 验证泛型 class/struct、定义历史、通用/闭合 Upgrade 与多代冷重开的真实包交付 | [GenericConsumer](PackageConsumerProbe/GenericConsumer/README.md)：由 [Run-GenericProbe.ps1](PackageConsumerProbe/Run-GenericProbe.ps1) 执行的产品回归；施工记录见 [DB-038 §12](../docs/design-branches/0038-generic-schema-state-and-binding-design.md#12-产品施工跟踪) |
| 验证通用 owner 显式复用值规则、嵌套工具作用域与删除旧 inline 领域声明后的历史升级 | [ValueUpgradeConsumer](PackageConsumerProbe/ValueUpgradeConsumer/README.md)：由 [Run-ValueUpgradeProbe.ps1](PackageConsumerProbe/Run-ValueUpgradeProbe.ps1) 执行；施工与验收见 [DB-039 §8](../docs/design-branches/0039-composable-value-upgrade-design.md#8-产品施工合同与验收映射) |
| 重访泛型 DTO/静态 helper、工厂闭合、readonly accessor 与历史 Upgrade 代码形状的选择依据 | [GenericBindingShapeProbe](GenericBindingShapeProbe/README.md)：独立机制见证；实际产品验证使用上述 GenericConsumer 与 src/tests |
| 验证单 writer 进程中止、RBF 发布可见性与严格坏尾拒绝 | [PublicationCrashProbe](PublicationCrashProbe/README.md)：DB-036 底层故障见证，非断电保证 |
| 比较 List 区间匹配器在相同领域编辑历史上的完整 Commit、候选 Delta、实际落盘字节及冷读正确性 | [ListDeltaReplayProbe](ListDeltaReplayProbe/README.md)：DB-049 轻量实验，独立 Repository、交错算法、报告中位数与范围；不以性能胜负为验收门槛 |

四个已完成或未采用路线的旧 Probe 已退出活动工作树，完整源码与证据保存在 Git 恢复点；
只在明确问题需要时查 [归档索引](ARCHIVE.md)。默认搜索与回归使用上表入口及 src/tests，
不为跟随产品演进而维护或恢复旧 Probe。

Coding Agent 模型/思考强度的工作方法实验另见[评测材料索引](../docs/agent-evaluations/README.md)，不计入产品机制或回归能力。
