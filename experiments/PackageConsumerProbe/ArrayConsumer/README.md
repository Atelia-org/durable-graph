# Array 模型编译资产

保留四种 rank、jagged sharing、泛型数组槽、inline 元素及自环模型和元素 Upgrade。

旧 raw/typed 存储执行束及 runner 已删除。此目录现用于模型与 Source Generator 编译，不是当前公开 Repository 回归门禁；模型编译不能证明保存、冷恢复、故障或性能行为。原执行故事与验收可从 Git `6dbaf0e:experiments/PackageConsumerProbe/ArrayConsumer` 恢复。

使用[包实验入口](../README.md)所述的准确 DG 版本和完整 feed。项目保持 PackageReference；后续 history 阶段必须沿用该 fixture 的独立 accepted-history 目录，不能直接跳至高版本。

编译顺序：`HistoryVersion=1` → `2`。
