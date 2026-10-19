# DG Branch/Commit 进程中止见证

2026-10-19，0096 新布局迁移及6保存＋3初始化窗口验收完成；协议为 `DG.MetaBootstrapCrashProbe.v2`，运行资格集中于[迁移与验收](../../docs/design-branches/0096-history-and-head-model/process-crash-migration.md)。产品当前能力和后续顺序见[0096工作集](../../docs/design-branches/0096-history-and-head-model/PROJECT-STATE.md)。本 host 不加入根 solution，使用现有 friend assembly 身份；不新增公开 crash API。

本 Probe 由 controller 外部终止 writer，再启动独立 verifier。保存模式验证**一个业务 B 与必要 Schema S**的共同发布，D/T 保持；初始化模式验证工厂依次创建控制 Heads 1/2/3。正常关闭、异常抛出和同进程重开不能代替这里的 external Kill。

## 构建与运行

统一构建者先构建一次，controller 始终不构建。上游 checkout 必须干净且与[Storage pin](../../eng/StorageDependency.props)一致，assets 中六个 Storage 依赖须全部为实际 ProjectReference。

```powershell
dotnet build experiments/MetaBootstrapCrashProbe/MetaBootstrapCrashProbe.csproj -c Release -p:UseStorageSources=true -p:StorageSourceRoot=E:/repos/Atelia-org/atelia-storage --verbosity minimal -m:1 -nr:false
pwsh -File experiments/MetaBootstrapCrashProbe/Run-Probe.ps1 -AssemblyPath experiments/MetaBootstrapCrashProbe/bin/Release/net10.0/Atelia.DurableGraph.Persistence.Tests.dll
pwsh -File experiments/MetaBootstrapCrashProbe/Run-Probe.ps1 -InitializationOnly -AssemblyPath experiments/MetaBootstrapCrashProbe/bin/Release/net10.0/Atelia.DurableGraph.Persistence.Tests.dll
```

输出名复用已有 `Atelia.DurableGraph.Persistence.Tests` friend 身份。两种模式各用全新 RunRoot 和逐窗独立库；controller 默认工件在 `.artifacts/MetaBootstrapCrashProbe/<UTC>-<guid>/`，也可传尚不存在的 `-RunRoot`，不覆盖或删除旧证据。

## 保存矩阵

生成的 Widget 环与共享 List 先保存，再改变一个 List 槽并带入新 Schema 声明，强制真实 B/S 两成员 CU。每窗均从 seed 开始：

| writer 停点 | 独立 verifier 的选择 |
|---|---|
| AfterMetaAppend | 旧 B/S；完整 Meta 候选保留 |
| AfterBusinessAppend | 旧 B/S；完整 Meta/业务候选保留 |
| AfterFirstCuEnd | 旧 B/S；只有第一个参与 Head 的 End 已追加 |
| AfterAllCuEnd | 新 B/S；两个 End 完整，不等同于已经 Confirmed |
| AfterPublicationConfirmed | 新 B/S；发布确认后、DG 安装前 |
| BeforeInstall | 新 B/S；DG 安装前 |

不假设首个 CU 成员一定是业务 Head。seed、READY、verify 绑定相同 TargetBranchId、ExpectedBaseCommitId、旧 D/T EntryId；ID 采用公开 codec 的 hex。verifier 从真实业务 Head 取得 SelectedCommitId，旧窗口等于原 Base、新窗口不同；同时验证准确图地址和 Schema 共同选择。

已发布图通过真实 HeadStore 历史选择严格前序 State；已知完整未发布候选只按 READY 中的旧 State 验证物理 source，不伪造 CommitId。verifier 保留旧 State 历史、原对象 IDs、实际 heads/H、候选完整帧及 Meta 描述符；正常冷开后继续保存须为真实单条 List Delta、准确 prior、ID 和 H。先 RO 检查文件不变，再独占 RW 续写；完成帧不因恢复或失败而被删除/修复。

## 初始化矩阵

无 seed。工厂每次成功 CreateHead 返回后停住，不产生业务或 Meta 用户帧：

| 停点 | 独立 verifier 的结果 |
|---|---|
| AfterFirstCreateHead | 缺 Head 2，RO/RW 均拒绝，不补建；关闭后能独占取得 backing 锁 |
| AfterSecondCreateHead | 缺 Head 3，拒绝边界同上 |
| AfterThirdCreateHead | D/S/T 为空且可 RO 打开，之后 RW 保存真实 Widget，再冷开续写 Delta |

空 D/T 按管理 pointer（P0R3/P0T2）解码，S 为 P0S1。READY 保存成功创建的 Head 集合与正式文件 SHA256；verifier 自行清点零用户帧，核对文件不变、缺入口未被补全、锁已释放。只有第三窗要求 SingleDelta/ExactPrior/IdsPreserved/HVerified；前两窗这些值为 false 表示没有可用库，不是假通过。

## 进程与证据纪律

writer 原子写 ready.json，输出并 flush `READY` 后保持存活；controller 检查窗口、协议和真实 PID，再 `Kill(entireProcessTree: true)` 并等待退出，随后才启动 verifier。进程隐藏运行，stdout/stderr保留；默认 READY 60秒、普通进程120秒、终止30秒，参数仅调有限等待，不扩大矩阵。writer exit 0 不算 Kill 成功。

每窗保存 seed（保存模式）、READY、verify、进程日志；顶层 receipt 记录协议/模式、来源 revision、source assets、输入/DLL SHA256、PID、预期与实际结果。失败同样保已有工件，不自动重试。控制器验证具体布尔证据与身份，不只相信一个 Passed。

本见证仅限当前机器的 ProcessCrashOnly。它不证明 OS crash、断电、设备故障、PackageReference 交付、总体性能或所有多业务 Branch 组合；多 Branch 故障语义另见[组提交验收](../../docs/design-branches/0096-history-and-head-model/group-commit-integration.md)。不增加启动全库审计；这里的全帧扫描是独立 Probe 诊断。

旧协议与旧 D/S 名字目录的运行记录保原版本含义：[最初接入](../../docs/design-branches/0096-history-and-head-model/headstore-adaptation.md)、[Private 删除](../../docs/design-branches/0096-history-and-head-model/private-removal.md)、[B1接入](../../docs/design-branches/0096-history-and-head-model/storage-handle-adaptation.md)。它们不授本轮新布局资格。
