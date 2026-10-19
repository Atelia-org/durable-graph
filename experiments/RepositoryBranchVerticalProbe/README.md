# 真实 Repository Branch 纵切

本 Probe 直接调用 DB-096 的新 Repository / WorkingCopy API，使用 Source Generator 生成的业务模型；不另写目录发布协调器。问题是：一个业务 Head 一条分支、HeadStore 继承历史成为来源权威以后，旧点分叉能否在彻底关闭资源后恢复并继续保存？

最小通过标准为程序返回 0 并打印 `PASS`。调用故事涵盖：State/Event 混合历史、父继续后的准确旧 Event 分叉、重复中文标签、空 fork 冷开无 clone Commit、纯 Event 根、不可变书签、不同根／兄弟／同点 Pair、`S0,E1,S1,S2` 的 Checkpoint 停止反例、摘要复制与缺省空值，以及同值保存后再次冷恢复和续写。

```powershell
$storageSources = (Resolve-Path .artifacts/db096-head-mechanism/storage-f8868b5).Path
dotnet run --project experiments/RepositoryBranchVerticalProbe/RepositoryBranchVerticalProbe.csproj `
  -p:UseStorageSources=true "-p:StorageSourceRoot=$storageSources"
```

默认在 `.artifacts/db096-branch-vertical/<随机目录>` 保留生成库，也可在 `--` 后传入一个尚不存在的目录。读取身份通过公开 4B/12B codec 往返；各次 open 之间释放所有 Repository/WorkingCopy，但仍处于同一进程。

2026-10-19 主线程最终集成运行通过 44 个正向断言和 54 个完整性断言（无警告）；记录位于 `.artifacts/db096-branch-vertical/probe.log`。`IntegrityChecks.cs` 通过内部友元读取实际物理记录，确认正常保存产生对象 Delta 与对象目录 Delta；另外覆盖 Entry 小描述损坏、物理自洽但历史来源错误的拒绝，以及发布成功后抛错、owner 停用与冷重开可见性。另覆盖新根 D/S 登记前中断、已知物理 ID 保留、冷开隐藏未登记 Head、旧 D/S 不变及完整候选保留；普通提交不追加 D。

完整性 fixture 仅在关闭 public Repository 后借低层资源制造反例，随后关闭低层资源并全新打开；不复用被旁路修改的活 owner/cache。这不是普通 NuGet 消费、进程中断或性能资格，有限确定性故障点不代表完整发布故障矩阵。目录缺失／破损、定向 origin 访问范围等仍须专门回归。此 Probe 不覆盖尚未接入的多 Branch 联合提交、分页或树查询。

设计及本轮资格以 [DB-096 活动工作集](../../docs/design-branches/0096-history-and-head-model/PROJECT-STATE.md)和[整体切换计划](../../docs/design-branches/0096-history-and-head-model/branch-cutover-plan.md)为准；源码存在不等于已经通过运行。
