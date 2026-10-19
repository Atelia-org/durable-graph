# 管理目录复用 Dictionary 的机制见证

问题：DB-096 的 Branch 目录 D、书签目录 T 能否复用现有容器与对象版本机制，避免给整个目录另写 codec？本实验直接复用现有测试项目，不创建独立运行时或第二份容器实现。

源码：[DirectoryDictionaryMechanismTests.cs](../../tests/DurableGraph.Persistence.Tests/DirectoryDictionaryMechanismTests.cs)。设计续接见 [DB-096 工作集](../../docs/design-branches/0096-history-and-head-model/PROJECT-STATE.md)。

## 判据与结果

2026-10-19，DG 起点 `6e1a06e` 加本片测试，Storage source pin `f8868b57e8f012745413bb38a83d67f1dba29f26`；两个 Fact 定向通过。主线程独立运行根构建0警告/0错误、Persistence 全套929项通过（含两项见证），失败/跳过均为0。构建/测试日志与 TRX 在 `.artifacts/db096-directory-dictionary/`；没有全 solution 测试、普通包或进程中止的新资格。

1. **D 的冷恢复、增量和联合发布。**独立 FrameStore/HeadStore 中，以真实 Meta 图保存内建 `Dictionary<uint,string>`；`uint` 是 `HeadId.TryWrite` 的稳定编码，不是本地文件编号。字符串经 ObjectId 与现有 String body 保存，包含两个相同的中文标签。首次 Base 完全关闭后恢复，新增一项，真实 `ObjectWritePlanner.PlanObjectWrites` 使用 `(5,5)` 预算、完整存活对象集合与从读取结果获得的重建字节数，自然选择字典 Delta 与新字符串 Base，旧字符串帧地址保持。再次关闭后继续增量，也得到准确的 source、prior 和三项重建链。
2. **D/S 共用现有发布协议。**S 候选由 `SchemaCatalogWorkspace` 产生，包含真实 Schema 定义；一次 `PublishHeads` 发布 D/S，冷开同时恢复新目录和新 Schema。Head payload 仅用现有 12B `FrameAddress` codec 定位图。之后追加完整 D/S 候选但不发布，冷开依旧选择已发布根；未发布图仍能按地址读回，未登记 Head 仍存在但不进入目录。
3. **T 的紧凑引用可复用内建组合。**`Dictionary<string,byte[]>` 以 ordinal 字符串键、内建 byte 数组保存真实 `HeadEntryId.TryWrite` 的 12B 编码。完整关闭后恢复编码、`TryRead` 并准确 `ReadEntry`，无应用模型或持久 Schema 依赖。此 Fact 只验证初始 Base 与冷读，未验证 T 的增量写入及不可变名称策略。
4. **默认 Meta 白名单不扩大。**D/T 采用现有 `StoredGraphKind.Meta`、`SchemaRoot=null` 与显式内部 builtin execution environment；默认员工 Meta reader 对两个目录都拒绝，显式路径通过。健康样本各 Base 不带 Schema，环境的 Schema 查询回调未执行。这不证明畸形历史也不能触发 Schema 访问：现有 decoder 可直接调用 SchemaCatalogView.Load，产品管理入口必须在此前拒绝非空来源 Schema，见[目录读取合同](../../docs/design-branches/0096-history-and-head-model/directory-persistence.md#3-管理图读取与来源)。

## 重跑

从仓库根运行；`StorageSourceRoot` 应指向与当前 pin 一致的准确源码，不可随意替换为 sibling HEAD：

```powershell
$storageSources = (Resolve-Path .artifacts/db096-head-mechanism/storage-f8868b5).Path
dotnet test tests/DurableGraph.Persistence.Tests/DurableGraph.Persistence.Tests.csproj `
  -p:UseStorageSources=true "-p:StorageSourceRoot=$storageSources" `
  --filter FullyQualifiedName~DirectoryDictionaryMechanismTests
```

本地 worktree 不随 Git 克隆分发；其他环境按[依赖指南](../../docs/storage-dependency.md)准备准确源码后替换路径。

## 证据限度与后续

这是现有底层原语的组合见证，不是新 Repository API 或新管理目录格式的交付。测试没有把候选布局写进旧 Repository 后继续使用其缓存。fixture 的控制 Head payload 只有 FrameAddress，产品仍须外层版本标记与明确 D/T 根角色校验；5/17B空入口、首次新增及缺失必需控制 Head 的初始化合同未在本见证运行。

这里手动构造冻结字典与对象行，复用了 typed codec、planner、Meta graph、Schema Catalog 及 HS 发布；尚未接入管理目录 workspace 的真实 Capture/对象身份安装、定向加载、完整根类型与所有行闭包限制。图的 source 资格仍采用现有 Meta Parent。T 的名称不可变/绑定校验以及 decoded handle 的 DG 正式成员资格仍是产品职责。

未衡量1000条目录的累计写入、Capture 成本或冷加载时间，未证明磁盘单点查询、恒定 I/O 或每次必选 Delta；实际 planner 可按重建预算选择 Base。正常关闭前不发布候选的见证不等于进程中断、掉电或发布故障注入。下一步在 B2 管理目录接线中复用这些机制，补固定内容准入与真实生命周期，不扩大 S 员工白名单，也不另写 Base/Delta 决策策略。
