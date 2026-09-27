# DB-084：DurableGraph 不可变 tag 接入记录

> 2026-09-28；已实施并验收。消费合同见 [DB-084](0084-eventjournal-immutable-tags-slice.md)，公共模型见 [DB-083](0083-repository-checkpoint-api-user-stories.md)。

## 问题与施工边界

将上游已有的不可变名字绑定接入 Repository，使调用者能够保存 tag 名，在关闭重开后重新取得固定历史位置。
最小验收是 State 与 Event-first 均完成 create → close → reopen → resolve → read/fork，分支后续提交或移动不改变绑定。

公开接缝为 `void CreateTag(string name, CheckpointAddress address)` 与 `CheckpointAddress ResolveTag(string name)`。
地址仍属于一次打开；不增加裸坐标、序列化地址、全局仓库身份、tag 移动/删除或第二套持久索引。
操作串行且受现有 busy/lifetime guard 保护，不物化领域图、不推进工作副本基线、不追加 E/S。

| 要求 | 实现责任与验收入口 |
|---|---|
| 名字绑定、当前打开地址、元数据解析 | Repository；RepositoryTagTests |
| 普通校验无发布、故障 outcome 与仓库停止服务 | Repository、GraphCommitException；RepositoryTagFailureTests |
| 严格打开、坏目标/格式、依赖先于 tag 确认 | 既有 HistoryJournal/GraphResources 与上游 replay；RepositoryTagStorageTests |
| 精确公开包及来源 | StorageDependency.props、Prepare-Storage、PackageProbeSupport；公开包下载与隔离消费 |
| 真实生成器与跨进程重开 | TagConsumer、Run-TagProbe.ps1；现有 EventHistory/recovery 包回归 |

上游公开发布采用混合版本：EventJournal/RbfSegmentStore 为 `0.1.2-preview.1`，来源
`883f995847f9bc9b92801f4f5f1f0997f54ff7d7`；Primitives/Data/Rbf 保持 `0.1.1-preview.2`，来源
`976aa345f923da09e2a5cf1dc25ba592b3818b63`。逐包 pin 与公开下载验收代替五包同版本假设。
不修改上游源码，不使用兄弟 HEAD 推断公开包，也不发布 DG 包到外部源。

## 失败语义与持久边界

普通上游 Result 失败保留明确错误，不因重名或无效名称将 Repository 标为故障。
固定基础包的 `Unwrap()` 抛出 InvalidOperationException，消息包含 `EventJournal.TagNotFound`、
`TagAlreadyExists` 或 `TagNameInvalid`；本片沿用该错误形状，不另造通用 tag Result/异常体系。
上游 `TagPublicationException` 一律停止仓库服务；`NotAttempted / Unknown / Confirmed` 分别映射
`GraphCommitOutcome.NotPublished / Unknown / Published`。没有新图修订，异常的 CandidateRevisionAddress 为 null。
未知抛出保守保留 Unknown；故障后关闭重开、按原 tag 名解析实际结果，严格打开拒绝损坏尾，不自动重试或修复。

CreateTag 所用的可写 Repository 地址，其 Schema/State 依赖在签发之前已确认：新提交沿现有保存路径；
可写重开先完成资源确认和全历史验证。只读重开验证数据但不确认耐久，签发的地址也不能带到另一打开实例创建 tag。
上游创建 tag 再确认目标 Event segment，随后追加并确认 ref-op-log。DG 不增加冗余图写入或第二次领域 Capture。
tag 不消费/替换 DB-080 准备槽；fault 仍释放槽。DB-081 否定结论及 DB-082 默认关闭保持。

## 验收证据

施工基线为 `41c613a`，工作树干净；旧 pin 下 HistoryJournalTests 的 22 项通过。

新增三个测试文件的 34 项分别覆盖：

| 测试 | 关键证据 |
|---|---|
| RepositoryTagTests（11） | State/Event-first 重开与续写；分支推进/Move 后绑定不变；两个名称空间；旧/foreign/null 地址；空模型/只读；活动基线与 DB-080 槽不变；仅既有 ref-op log 增长 |
| RepositoryTagFailureTests（11） | 上游四个真实注入阶段、原始 typed inner/outcome；真实 RBF pre-I/O 拒绝；DG 前后发布注入及非 typed 异常；fault 停止所有操作并清槽；重入、Dispose、严格重开实际结果 |
| RepositoryTagStorageTests（12） | 合法 RBF 包装下的重复/未知格式/坏目标；tombstone、CRC、坏尾；上游可读但 DG envelope/State/Schema 非法的 tag-only 目标；两种打开拒绝且字节不变；tag 确认顺序和中断 |

故障测试借反射安装固定版上游内部 probe，不替换发布器或伪造 TagPublicationException。
AfterAppend 注入产生完整记录，关闭后重开可见只证明这一确定性场景，不额外承诺断电、部分写入或磁盘损坏恢复。
未知普通异常的见证先关闭实际 journal，再调用 CreateTag，保留 Unknown 并 fault，不把没有 typed 证据的异常推断为未发布。

首轮专项测试暴露了测试夹具假设：Unwrap 异常类型误判、live writer 独占文件不能另开读取，
以及 ReadFrames 可创建 rebuildable forward-plan 文件。修正为固定包的准确异常断言、关闭后全字节比较，
并把额外历史查询移到 tag 字节验收之后；没有放宽产品合同或过滤派生文件来掩盖副作用。

公开包交付由独立审查逐一核对：五个包的 nuspec 版本、完整来源 commit、仓地址、签名条目和 SHA256。
签名条目存在性检查不是完整信任链验证。Prepare 重复复用不改包/receipt 的字节或时间戳，
错来源、无签名同版本包及无 receipt 的已有包均拒绝；默认、统一开发覆盖、具体包优先覆盖三个 MSBuild 求值均正确。

真实 DG 包版本 `0.0.0-tags-e2e.20260927212208.10352`，五个公开依赖加四个 DG 包构成恰好九包的独立 feed。
consumer assets 只有该 feed 与私有缓存；输出的九个运行时 DLL 与私有缓存、nupkg 内 DLL 的 SHA256 三方一致。
以下包回归通过，后两项复用同一批包而在各自私有缓存还原：

- `Run-TagProbe.ps1`：四个独立进程覆盖 seed、空模型 metadata、两种 tag 的 ref-only/Checkout 与 named Fork 续写、冷重开值和自环。
- `Run-EventHistoryProbe.ps1`：原有历史升级、Event-first、Checkpoint、固定查询、Fork 与每分支占用。
- `Run-EventHistoryRecoveryProbe.ps1`：恢复流程及 XML 公共合同；新增两个 tag 方法的打包文档检查。

Tag probe 的运行入口及标记见 [TagConsumer](../../experiments/PackageConsumerProbe/TagConsumer/README.md)。
本地详细日志与 TRX 保留在忽略的 `.artifacts/db084/`；真实 feed/消费者保留在 PackageConsumerProbe 的 `obj/tags-20260927212208-10352-aea6bf4a/`。
Release 根构建零警告、零错误；修正夹具后的 34 项专项测试全部通过。
solution 外调整依赖引用的 PublicationCrashProbe 也完成 Release 构建，零警告、零错误。
完整 solution 测试 **2,976/2,976** 通过，零失败、零跳过：Persistence 959、Runtime/Generator 1,652、Storage 202、Serialization 163。
主线程核对 TRX，全部 34 项新增测试在完整运行中通过；核心、故障证据及包交付的独立复审无未解决阻塞项。
受影响的 11 份 Markdown、532 个本地文件链接与既有标题/锚点检查通过，`git diff --check` 通过。

复跑命令（逐条串行执行）：

```powershell
dotnet build DurableGraph.slnx -c Release -m:1 -nodeReuse:false
dotnet test tests/DurableGraph.Persistence.Tests/DurableGraph.Persistence.Tests.csproj -c Release --no-build --no-restore --filter FullyQualifiedName~RepositoryTag
dotnet test DurableGraph.slnx -c Release --no-build --no-restore
./experiments/PackageConsumerProbe/Run-TagProbe.ps1
```

后两份包 runner 可用 Tag probe 输出的 `-PackageSource <feed> -Version <G>` 复用已生成包。
增加 tag 后，旧的 `0.1.1-preview.2` EventJournal reader 会拒绝新 tag frame；不宣称新记录可由旧 reader 忽略或读取。

## 后续入口

完成后以 [PROJECT-STATE](../../src/PROJECT-STATE.md) 和 [路线图](../DurableGraph-research-roadmap.md) 选择工作。
tag 的基础跨重开定位不意味着外部地址序列化或业务事件 exactly-once；后续扩展应由真实消费者反馈触发。

建议先在 DramaBoard 或 LLM tool-loop 的实际接入中使用「固定 tag → 冷重开 → 历史浏览/Fork 续写」，
确认应用自行保存处理阶段、恢复游标和外部效果去重信息。相关输入为 DB-083 用户故事、本文、
[存储依赖指南](../storage-dependency.md) 及 TagConsumer；本片没有替下游修改 adapter 或发布正式 DG 包。
DB-081 仍需新证明才能重启；DB-082 只有真实物化瓶颈或并存图内存压力时再测，默认保持关闭。
