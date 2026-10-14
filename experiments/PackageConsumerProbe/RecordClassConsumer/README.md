# RecordClass 模型编译资产

保留 BaseLibrary → FactsLibrary → Host 的普通 class/record 继承、只读字段和记录模型；Host 现为模型 Library。

旧 raw/typed 存储执行束及 runner 已删除。此目录现用于模型与 Source Generator 编译，不是当前公开 Repository 回归门禁；模型编译不能证明保存、冷恢复、故障或性能行为。原执行故事与验收可从 Git `6dbaf0e:experiments/PackageConsumerProbe/RecordClassConsumer` 恢复。

使用[包实验入口](../README.md)所述的准确 DG 版本和完整 feed。项目保持 PackageReference；后续 history 阶段必须沿用该 fixture 的独立 accepted-history 目录，不能直接跳至高版本。

编译顺序：每代按 BaseLibrary → FactsLibrary → Host 顺序构建；前两库 pack 后供下一级 PackageReference 消费，各项目保独立 history；`HistoryVersion=1` → `2` → `3`，每代 `ModelPackageVersion` 使用新版本。
