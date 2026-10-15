# 实验与回归入口

产品当前能力与下一步见 [src/PROJECT-STATE.md](../src/PROJECT-STATE.md)。
按问题选择入口；独立项目不在根 solution 中，运行方法和资格由各自 README 维护。

| 问题 | 活动入口 |
|---|---|
| 实际包交付、Generator 与 history/build | [PackageConsumerProbe](PackageConsumerProbe/README.md) |
| 无业务注册的 meta 冷读与容器 Delta | [MetaBootstrapSchemaConsumer](PackageConsumerProbe/MetaBootstrapSchemaConsumer/README.md) |
| accepted-history、Upgrade、准确 S、tag、Fork、Pair 与冷增量 | [SplitRepositoryConsumer](PackageConsumerProbe/SplitRepositoryConsumer/README.md) |
| baseline private、Nullable/List 升级与完整 ToShared | [PrivateContainerConsumer](PackageConsumerProbe/PrivateContainerConsumer/README.md) |
| 用户 baseline、开发 fork、定稿与 history 模式 | [DevelopmentSchemaConsumer](PackageConsumerProbe/DevelopmentSchemaConsumer/README.md) |
| 泛型 DTO、工厂与 readonly accessor 的独立机制 | [GenericBindingShapeProbe](GenericBindingShapeProbe/README.md) |
| FS/VS 发布可见性与进程中止后的正常尾恢复 | [PublicationCrashProbe](PublicationCrashProbe/README.md) |
| 实际 DG 两根创建与 SG 保存的固定 Kill 窗口 | [MetaBootstrapCrashProbe](MetaBootstrapCrashProbe/README.md) |
| List matcher 的实际落盘成本与冷读正确性 | [ListDeltaReplayProbe](ListDeltaReplayProbe/README.md) |

## 机制调查见证

| 问题 | 入口与资格 |
|---|---|
| 显式分类、真实 backing 的 Roslyn/PE 暴露与当前 SG 输出 | [ExplicitStorageClassificationProbe](ExplicitStorageClassificationProbe/README.md)；14 形状的观察工具，保独立正控；不把当前遗漏固定为产品回归，不证明普通 backing 已获支持 |

旧 raw Store 消费者的执行代码与 runner 已删除；保留的 19 类业务模型是
[生成器与 history 编译资产](PackageConsumerProbe/README.md#model-fixtures)，不再是待恢复的运行门禁。
旧设计、数据与实验源码按需从 [Git 历史](ARCHIVE.md) 查证。
Coding Agent 工作方法实验另见[评测材料](../docs/agent-evaluations/README.md)。
