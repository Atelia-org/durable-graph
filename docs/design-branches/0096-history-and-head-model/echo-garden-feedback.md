# Echo Garden：新 API 消费反馈与可吸纳事项

2026-10-20；研究结论，**未实施新的产品 API 或正式示例**。基线为 DG `54a0633`，产品来源 `440bccf`；DB-096 的既有交付仍保持完成状态。本次问题是：真实游戏消费者确认了什么，暴露了哪些接入缺口，最小改进应落在框架还是示例？

结论：反馈支持当前职责划分，三个摩擦主要集中在**应用持久导航、创建后的切换流程、预览协议**。建议先交付一份短小、可执行的消费者配方，并从 README 链入；暂不新增文本 ID 协议、ForkAndOpen、自动摘要 factory 或通用 UI 框架。

## 1. 证据来源与复核范围

下游报告和代码位于本机 `W:/TEMP/echo-garden-api-review-b0d50b4bc5174e5fb478748ec9a07012/` 的 `RESULTS.md`、`Program.cs`、`Review.csproj`。它链接 workshop 原有 GardenState、GardenRules、Views，只替换实验中的仓库调用；不是完整网页应用迁移。

主线程在**全新目录**复制相同三个输入文件（Program、project、NuGet.Config），以空独立 cache 和原本地候选 feed restore/build，再分别启动 `seed`、`verify`。原实验与游戏存档未修改：

- 新目录：`W:/TEMP/dg-echo-feedback-f2924b7ec88a460ca5e94178e926d00f`。
- G：`0.0.0-db096-final-readme.20261019110000.28648`；S：`0.1.3-dev.20261019.g70b406c`。
- Release 构建0警告、0错误；两个进程调用均exit0。
- 复现七日三花通关、第3天分叉、父结局保持、重复标签、实际来源树、ID往返、无业务模型浏览摘要、3页覆盖8个Commit、空分支冷重开续写。
- 复现默认 `System.Text.Json` 对两种ID输出 `{}`；空模型配置下fork成功、随后open失败，分支仍登记且仓库健康。
- 复算11个原包/缓存包哈希、10个Atelia输出DLL与包内字节；复制的实验输入与原文件一致。assets只有package依赖。主线程证据索引：`.artifacts/echo-garden-feedback/reproduction.json`；完整日志在新目录。

三名subagent分别只读审视ID、分叉切换、Summary合同；主线程复核关键源码和测试。没有重跑上游完整2992项回归，没有修改生产代码，也没有新性能、crash或网页端到端资格。临时目录不是长期分发材料；本文保留结论、来源与限制，后续正式示例应独立入库，不依赖该目录。

另一个限度：原Probe的open失败用裸 `catch`，且没有同时持有旧WorkingCopy；它证明该调用序列的结果，不独立证明准确异常类型，也不证明“所有打开失败都不影响旧副本”。

## 2. 值得吸纳的正面信息

| 真实使用观察 | 对设计的支持与限度 |
|---|---|
| 重复“温室”标签仍能准确选择分支 | BranchId与DisplayLabel分开确有价值；不应重新让标签承担身份、查找或幂等键 |
| 保存ID后由另一进程继续 | 字节codec和准确Commit位置已能支撑导航/书签；缺的是常见文本传输接法，不是更宽的身份字段 |
| 空fork的current属于父线，但保存写子线 | 选中Branch与Commit来源分开是必要区分；fork不制造业务Commit的模型成立 |
| 从B历史选择A旧点再分C，C在A下 | 当前树表达存档来源，不表达UI点击来源；真实消费者已验证这一取舍 |
| 空业务模型注册表仍能浏览目录、摘要、历史 | 描述与内容恢复分层已获得实际普通包支持；不等于可以无模型编辑，也不代表不读取框架自身元数据 |
| 纯State完成小局 | Event保持可选、统一Catalog减少初接分歧有效；不需要为使用框架引入事件重放 |
| 七字段、固定8个存档只需单次保存和全历史 | 高级API可以按需求采用；不应将组提交/分页变成所有应用的必经样板 |

这轮带来的性能信息是**工作负载边界**，不是性能结果。固定小局可以保留简单全量历史；长LLM tool-loop仍有分页需求。Probe虽运行了分页，但这只是功能见证，不是游戏必须使用分页或其性能更好的证明。

## 3. ID：先补正式可执行的文本适配示例

### 已有能力与缺口

[BranchId](../../../src/DurableGraph.Persistence/BranchId.cs)与[CommitId](../../../src/DurableGraph.Persistence/CommitId.cs)只公开字节codec、相等与哈希，没有公开实例数据属性、JsonConverter或可往返ToString合同。当前 `{}` 已实测；不能把默认ToString当持久格式，`IncludeFields`也不是暴露internal Value的解法。

README已有byte codec，但尚缺“写到导航JSON→退出→解码→选定原仓库→继续”的完整例子。实验的Hex适配证明路径可行，不过其Navigation DTO没有版本/仓库选择约定，解析只是断言，尚不宜直接作为正式模板。

| 候选 | 价值 | 代价与建议 |
|---|---|---|
| 应用DTO＋Hex适配 | 覆盖JSON、网页字符串和配置；无需改变核心类型 | **首选**；官方示例只选一种编码，避免初接时继续海选 |
| 显式注册JsonConverter | 已有typed DTO可自动往返 | 后续有重复样板需求再加；需覆盖nullable/default、非法token与字典键 |
| 框架文本codec或默认converter | 跨消费者统一 | 需决定规范文本、版本及全局JSON行为；目前一个消费者尚不足以选定 |

推荐示例以公开codec写4B/12B，再编码为8/24字符Hex；不直接解析底层内部数值。外层导航DTO声明应用协议版本、LastUsedBranch和可选固定BookmarkCommit；仓库定位由应用配置提供，或放在外层上下文，不向每个ID塞入新scope。

应展示的失败边界：

- 无值使用null/缺项；default不是可持久位置。非法字符、奇数长度、错误长度、零值及未知协议版本明确失败，不能静默选首项。
- 文本解码只证明表示合法；存在性、正式成员、State/Event角色仍由对应Repository操作检查。
- 调用方必须选择原仓库；当前跨库概率诊断不是安全认证，也不保证拒绝所有错库输入。
- 分支列表无排序保证。**“上次使用”是应用导航状态，“最近保存”是另一种排序。**未续写fork承接旧Commit，不能用其current的新旧推断分支创建/使用时间；不能用列表末项或文本ID大小代替时间。
- 默认不顺带支持 `Dictionary<BranchId,…>` 的JSON。若后续采用converter，字典键还需 `ReadAsPropertyName/WriteAsPropertyName`，不能只测试值的Read/Write。[Microsoft converter说明](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/converters-how-to)、[字典键读取合同](https://learn.microsoft.com/en-us/dotnet/api/system.text.json.serialization.jsonconverter-1.readaspropertyname?view=net-10.0)。

导航记录可作为可重新选择的应用偏好，不默认要求它与DG业务提交原子发布；如果以后它成为不可丢的业务事实，再提出相应一致性需求。

## 4. 分叉切换：表达持久创建与内存打开的两个结果

[ForkBranch](../../../src/DurableGraph.Persistence/Repository.cs)登记新分支，不恢复领域对象；[CheckoutCore](../../../src/DurableGraph.Persistence/Repository.MetaBootstrap.cs)才选择State并恢复模型。这一分工允许无模型浏览/分叉，反馈没有给出合并它们的理由。

最小应用流程应明确：

1. 保留旧工作副本；选择的是已保存Commit，旧副本的未提交编辑不会被fork。
2. Fork正常返回后，立即保留新BranchId，应用显示“已创建，尚未打开”。
3. 单独打开新BranchId。失败时保留这一身份；再次尝试应重试open，而非再次fork。
4. open成功后切换当前副本，再单独释放旧副本。旧副本未提交编辑是否丢弃由应用决定，Dispose不会保存它们。

这应是两阶段结果的短例子，而不是包在一个大catch里的“分叉失败”。[已有恢复失败测试](../../../tests/DurableGraph.Persistence.Tests/SplitRepositoryHistoryTests.cs)中的 `HistoricalForkRestoreFailureKeepsTheCreatedBranchAndReleasesWorkingCopyOccupancy` 已覆盖登记保留、失败释放新占用、解除临时allocator故障后重开同一分支。

还需要把三条条件写清楚：

- **保留旧副本不保证旧副本总能继续保存。**原owner健康、未关闭且旧副本仍活着时才可继续；owner fault后所有副本均停用。模型回调的应用内存副作用也不由框架回滚。
- Repository打开时冻结模型配置。缺模型通常需关闭后用完整registry重开，再打开已保存的新BranchId；不是对旧registry补注册就原地生效。
- Fork自身抛发布异常与“正常返回后open失败”分开处理。Unknown/Published不可盲目重建；按异常可用的TargetBranchId和outcome重开核查。NotPublished也不等于owner健康，重复标签不能替代准确身份核查。

当前Dispose只释放占用、没有存储I/O；仍应放在open的catch范围之外，避免把切换成功后的清理错误误报成未打开。不要为了示例新建“CanOpen”预检：预检重复恢复，仍不能保证后续allocator/资源可用；ForkAndOpen同样无法消除已创建却未打开的中间结果。

## 5. Summary：原子发布字节不等于业务投影一致

当前公开请求方法是 `CreateStateCommitRequest` / `CreateEventCommitRequest`，不是早期讨论中的PrepareCommit命名。[CommitRequest](../../../src/DurableGraph.Persistence/CommitRequest.cs)在创建时复制Summary，保留根引用；业务图在CommitBranches执行时捕获。[直接反例](../../../tests/DurableGraph.Persistence.Tests/BranchGroupCommitTests.cs) `Requests_defer_capture_but_own_summary_bytes_and_become_stale_after_success` 已证明“新State＋旧Summary”是合同允许的结果。

正确的示例约束应比“同锁”更精确：

> 完成业务修改后，从State纯投影摘要；从投影开始直到提交调用返回或抛出，相关业务图不再改变。多线程调用方用统一的应用级同步保护整段；单线程也不得在生成摘要后继续修改对象。该范围保留框架要求的整个提交调用内图稳定合同。

游戏主路径继续使用立即 `CommitState(summary)`。若确实需要组提交，先完成所有业务修改，再投影所有摘要、构造请求并立即共同提交；失败后重新编辑应重新投影/创建请求。仅把同一请求重试也会捕获当时的图，不会自动刷新此前复制的摘要。

建议最小摘要协议采用UTF-8 JSON，如 `{schemaVersion:1, day, flowers, won}`，而不规定框架统一领域字段：

| 读到的内容 | 应用呈现 |
|---|---|
| 空字节 | 无摘要 |
| 支持版本且字段合法 | 显示预览 |
| 未知版本 | 此版本预览暂不支持 |
| 非法JSON、缺字段或非法值 | 摘要不可用，保留Branch/Commit身份 |

摘要版本属于应用预览协议，独立于DurableSchema。解码失败不默认成day0，也不强迫物化State；只有成功取得字节后的应用解析失败可降级，不能吞掉底层读取/校验/owner故障。摘要不能作为恢复游戏的权威状态。

示例可自定4KiB等小预算并检查编码/读取长度；[产品payload预算](../../../src/DurableGraph.Persistence/BusinessEntryPayloadCodec.cs)只是最大准入限制，不是推荐预览大小。截图仍属延期附件问题。

暂不新增summary factory：在捕获附近调用回调仍不自动保证一致，回调重入、失败以及投影如何对应冻结图都需要新合同。当前纯投影后立即提交已足够；更强机制应由实际消费者无法遵守此纪律的证据触发。

## 6. 推荐后续切片与验收

**建议下一片只做消费者配方与可执行验证，保持生产API/持久格式不变。**本轮完成研究和复跑，并未把以下建议宣称为已实施。

| 小片 | 最小验收 |
|---|---|
| 导航DTO/Hex | 两进程往返Branch/Commit；空fork仍写选中子线；重复标签/重排列举不改变LastUsed；拒绝非法/default/未知版本；固定书签不随分支推进 |
| 安全分叉切换 | 旧活副本带未提交编辑；fork成功/new open失败；健康owner下旧副本仍可保存；目录只增一条；解除临时故障后只重试open；另测owner fault与配置重开边界 |
| 版本化Summary | 保存后关闭，无模型重开预览；摘要等于准确Commit的State投影；覆盖空/未知/非法/缺字段/超应用预算；保留延迟请求旧摘要反例，不把产品改成自动纠正 |

一份短指南承载三个独立片段，README主路径只加入口/关键提醒，避免把最短保存示例扩成Web框架教程。优先复用已有普通包消费者验证通道；是否需要新增runner取决于后续示例组织，不预设新项目、新包或通用协议。

性能工作暂不启动；下一次值得收集的是实际网页接入中的导航/错误处理代码，以及更长历史下可复现的耗时或读取计数。DB-096既有交付资格不因此撤销或重开。
