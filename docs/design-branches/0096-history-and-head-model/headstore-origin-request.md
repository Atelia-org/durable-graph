# HeadStore 请求：按 HeadId 读取固定创建来源

> 2026-10-19；DG 设计收敛提出的窄请求，尚未向上游提交或实施。源码依据为 Storage `f8868b57e8f012745413bb38a83d67f1dba29f26`。本请求不改变已交付的引用初始化、紧凑身份和准确历史合同。

## 需求与最小能力

DG 推荐以独立登记目录限定正式 Branch；内部 Schema Head 和创建中断留下的未登记 Head 不属于业务目录。`ReadBranchInfo`、`ListRootBranches`、`ListBranchSubtree` 需要读取这些**指定成员**的固定创建来源，用来源提交的实际 Head 建立森林；不需要遍历其 Commit 历史或物化 State。

目前 [ListHeadOrigins](../../../../atelia-storage/src/HeadStore/HeadStore.ForkQuery.cs)检查全部正式物理 Head 的 header，再检查整图。先查全集再过滤虽然可供健康数据实验使用，却会将无关 Head 的访问量、预算和 header 错误带入 DG 目录查询。DG 不希望复制一份来源作为第二权威，也不要求新增可变 Head metadata 或持久子树索引。

请求增加一个单 Head 查询，以下只是语义签名，具体命名/结果包装由上游决定：

```text
store.ReadHeadOrigin(headId) → 已检查 header 声明的 HeadEntryId? 来源
```

- root：成功且没有来源；fork：成功并返回其固定来源的完整定位值。首个本地发布前后来源相同。
- 无效 ID、缺文件、坏 header、owner 不可用均按现有错误合同失败，不能返回“无来源”。
- 检查所选 Head header 的自身身份、格式和来源字段结构；**不读取来源 Entry，不证明来源发布资格或整条历史健康**，不扫描无关 Head 集合。
- 查询返回自有值，可在调用结束后保留。沿用现有串行/lifetime/fault、只读不修复与可写正常 lazy recovery 纪律；不要求预打开全部 Head。
- 不因查询把所有新访问文件永久保留在 owner 中；可以复用已打开目标，其余用临时打开/关闭。性能合同是限定访问对象，不宣称整次调用恒定 I/O，正常打开/恢复仍有成本。

已有 [HeadOriginInfo](../../../../atelia-storage/src/HeadStore/HeadOriginInfo.cs)的 `SourceHeadId` 注释承诺源在本次完整 header 集合中存在；直接复用该类型时须区分新查询较弱的资格，不能原封不动沿用这条承诺。最小返回可为空的准确 `HeadEntryId`，无须另增一套图查询对象。

## DG 自己承担什么

DG 在登记成员集合内检查父成员存在、来源自环和环；`ReadBranchInfo` 至少检查直接父成员，完整列表/子树建立森林时检查整个登记集合。固定 fork 来源必须指向实际发布分支，而不是发起操作的 UI 分支。准确读取或从旧点 fork 时再经 `ReadEntry` / `ForkHead` 检查来源 Entry 发布资格；物化业务图时继续 DG 的准确 Schema、内容来源和 prior 验证。

单 Head 查询不保证父 Head 仍在磁盘上，也不替代上述 DG 检查；若返回结构仍有来源 Head 字段，它只表示所选 header 的声明。DG 不把坏来源降格成新根。

## 建议验收

1. root 返回无来源；引用初始化空 fork 返回固定准确来源，父线推进与子线首次发布不改变该结果。
2. 对指定健康 Head 查询不枚举/打开无关 Head；无关文件错误不成为本次 header 查询的失败。可用上游既有文件替身观测，不要求 DG 修改完成帧。
3. 缺失目标和非法目标失败；“源 Entry 不可读”不会被新 API 误报为已验证健康，也不要求为读 header 访问源 Entry。
4. 只读/可写、取消（若提供）、临时资源清理与故障边界遵循现有查询纪律。

这是产品目录访问范围的改进，不是 [Branch 机制 Probe](../../../experiments/BranchHeadMechanismProbe/README.md)健康数据组合验证的前置条件。上游交付后，DG 另做实际依赖接入与限定回归，不能用本请求或已有独立消费者结果替代产品资格。
