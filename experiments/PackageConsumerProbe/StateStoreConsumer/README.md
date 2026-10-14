# StateStore 模型编译资产

保留 NamedObject/Character、World V1/V2 与共享派生环模型、readonly/transient 状态和业务 Upgrade；旧 raw Store、Schema 登记和保存/恢复执行代码已删除。

旧 raw/typed 存储执行束及 runner 已删除。此目录现用于模型与 Source Generator 编译，不是当前公开 Repository 回归门禁；模型编译不能证明保存、冷恢复、故障或性能行为。原执行故事与验收可从 Git `6dbaf0e:experiments/PackageConsumerProbe/StateStoreConsumer` 恢复。

使用[包实验入口](../README.md)所述的准确 DG 版本和完整 feed。项目保持 PackageReference；后续 history 阶段必须沿用该 fixture 的独立 accepted-history 目录，不能直接跳至高版本。

编译顺序：默认 `CharacterHistorySeed=true, RestoreVersion=1` 生成 V1；沿同一 history 改 `CharacterHistorySeed=false` 编译 Character V2，再设 `RestoreVersion=2` 编译 World V2 和图模型。
