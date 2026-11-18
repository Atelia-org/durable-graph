# DB-101 可增量保存的 DurableText

本片把持续编辑的正文作为图中的可变引用对象接入。研究阶段选定 A-Adaptive Piece Table，产品分支只提取这一种实现。验收围绕一条完整保存链：首次 Base、两次小改 Delta、关闭仓库、打开历史图并续写。对每次写入核对实际前驱、内容和图内引用身份。

**产品证据**。实现在 `fb2bef0ad2acc9c146c72c4edf4a2cd55ce35714`。针对性源码测试覆盖共享 CLR 实例的独立 builder、历史分叉、未变/净零/维护写、账实字节、准备失败、发布边界、Event 及捕获后编辑。[集成测试](../../../tests/DurableGraph.Persistence.Tests/DurableTextIntegrationTests.cs) · [W SSD 保存、渲染与搜索对照数据](mainline-performance.md)

下一片已选择[分段 Base 重构方案](segmented-base-refactoring-plan.md)，拟用全文 UTF-16 单元数前缀、独立 Bare string 和 Binary span 读写入口。直接解码到最终 string 已通过机制验证。当前产品仍用固定 UTF-16LE。备选项、原文尺寸与压缩测量见[编码研究](text-encoding-research.md)。

2026-11-18，固定 Storage 源码模式 Release 根构建 0 警告、0 错误。根 solution 全量测试 3,271 项通过（Serialization 133、Storage 36、Persistence 1,247、Runtime/Generator 1,855），无失败或跳过。性能测试另显式启用并在三个独立进程运行。

**普通包**。新 G 为 `0.0.0-durabletext.20261118.gfb2bef0`，六包 S 为 `0.1.3-dev.20261101.g9ad39fc`。隔离消费者以包内 Generator/Build 执行 Publish/Verify，三个进程分别保存、重开续写和只读复核，检查实际加载程序集。新 reader 读回本次旧库正控；旧 reader `0.0.0-graphheap.20261115.g7a98c7d` 能打开含新类型的仓库、枚举分支及查询 tag，随后在 LoadState 解析所选 Schema 时以 InvalidDataException 明确拒绝。两次交叉读取均未改变数据库字节。本次组合及阶段仅限所测 fixture。[普通包复现](../../../experiments/PackageConsumerProbe/DurableTextConsumer/README.md)

[主线接入与消费合同](mainline-integration-plan.md)涵盖文本操作、来源和保存边界。完整实验、候选实现与接入初稿保存在本地研究分支 `research/durable-text-20261117` 的提交 `0ad8b9836ca7ff179b837cb87656f794067d8643`，可用以下命令读取冻结稿。

```powershell
git show 0ad8b983:docs/design-branches/0101-durable-text/mainline-integration-plan.md
git show 0ad8b983:docs/design-branches/0101-durable-text/finalists-comparison-results.md
```
