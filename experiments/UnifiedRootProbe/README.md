# DB-085 统一根机制实验宿主

本宿主另有[目录与轮转对照](../../docs/design-branches/0085-unified-root-metadata-experiment.md)：`metadata-*` 候选使用真实 EventFrame/分段地址及 Create-ticket 身份分配，由不可变完整目录和唯一根发布；`baseline-*` 调用现有 EventJournal。两者只使用 32B 事件内容，不执行 DG 图或 Schema 管线。候选的 RefId 属于实验 `allocation.rbf` 身份域，不是旧 EventJournal 目录的兼容格式。

目录实验沿用下方源码联调构建命令，使用独立任务临时目录与脚本：

```powershell
$env:TEMP = $env:TMP = Join-Path $env:LOCALAPPDATA 'Temp/db085-metadata-20260930'
New-Item -ItemType Directory -Path $env:TEMP -Force | Out-Null
& experiments/UnifiedRootProbe/bin/Release/net10.0/Atelia.UnifiedRootProbe.exe metadata-selftest $env:TEMP
pwsh -NoProfile -File experiments/UnifiedRootProbe/Run-MetadataProbe.ps1 -ProbeExe experiments/UnifiedRootProbe/bin/Release/net10.0/Atelia.UnifiedRootProbe.exe
```

`-Suite candidate|baseline|cost` 分开运行，`-CaseFilter` 选择具体中断例。脚本不构建，所有 .NET 仍须串行。每轮新建 C: 任务 GUID 目录，原始子进程 JSONL、完整前缀/地址、PID Kill 证据和成本报告放在其 `evidence/` 下；失败夹具保留，不自动清理。施工记录给出验收批次及仓库忽略目录中的副本。

候选文件为 `allocation.rbf`、`catalog.rbf`、`roots.rbf`、`repository.lock` 和 `events/segments/00000001.rbf`；第二段仅在创建 B 的实际写入中建立。旧根固定名字、身份、head、段 EOF；完整未发布事件、身份和目录保留。恢复仅处理一个支持短尾，或完成未被完整根引用、匹配标准 Header 的第二段 0–3B 初始化。Trailer 1–11B、完整格式/语义矛盾、未知输入、I/O 和未完成检查不授予动作资格。所有完整目录包括 orphan 都要满足真实发布代数及前代根的分支/身份/EOF 转换约束。

普通随机读取仍依赖受控 writer 来源；伪造 ticket 的物理成员资格由完整恢复资格扫描证明。候选每次 writer 准入及恢复仍全扫，目录每次完整复制，首次 `ReadAll` 还校验各分支祖先；不能把根槽有界定位称为完整入口常数成本。成本分开记录请求/返回、直接读取和 RBF 读取、预取、线程分配、屏障及目录字节。基线的 locator/catalog 临时文件 I/O 没有纳入 RBF 计数，但包含在完整耗时中；双方准入检查责任不同，不报告产品性能倍率。

本机仅 `ProcessCrashOnly`；文件内容 flush 是真实调用，目录项断电屏障没有实现，`PowerLossRequired` 明确拒绝。外部 Kill、故障注入和 F2 图像模型分开报告。公开 Open/writer/Audit/rescue 不受本实验接线影响。

## 固定双值实验

本实验按[DB-085 首片合同](../../docs/design-branches/0085-unified-root-experiment.md)使用新建的三个 RBF 文件：`a.rbf`、`b.rbf`、`roots.rbf`。每个子文件有固定身份帧、固定形状的数据帧；根按代数占固定物理槽，记录两张子票据与可见 EOF。根是唯一业务发布选择，完整未发布子帧保留在物理前沿，下一次提交越过它们追加。这里的固定双值形状不代表 DurableGraph 产品格式或任意分支目录。

## 运行与证据

主线程先以 .NET 10、Release、显式 storage source checkout 串行构建，然后用已构建的 apphost 或 DLL 运行；脚本本身不构建。示例从仓库根目录：

```powershell
$storageSourceRoot = (Resolve-Path ../atelia-storage).Path
$fixtureTemp = Join-Path $env:LOCALAPPDATA 'Temp/db085-root-20260930'
New-Item -ItemType Directory -Path $fixtureTemp -Force | Out-Null
$previousTemp, $previousTmp = $env:TEMP, $env:TMP
try {
    $env:TEMP = $env:TMP = $fixtureTemp
    dotnet build DurableGraph.slnx -c Release -m:1 -nr:false -p:UseStorageSources=true "-p:StorageSourceRoot=$storageSourceRoot" -p:ShouldUnsetParentConfigurationAndPlatform=false
    dotnet build experiments/UnifiedRootProbe/UnifiedRootProbe.csproj -c Release -m:1 -nr:false -p:UseStorageSources=true "-p:StorageSourceRoot=$storageSourceRoot" -p:ShouldUnsetParentConfigurationAndPlatform=false
    pwsh -NoProfile -File experiments/UnifiedRootProbe/Run-Probe.ps1 -ProbeExe experiments/UnifiedRootProbe/bin/Release/net10.0/Atelia.UnifiedRootProbe.exe
}
finally { $env:TEMP, $env:TMP = $previousTemp, $previousTmp }
```

`-Suite commit|recovery|cost` 可单独运行一组，`-CaseFilter 'recovery-partial-A-*'` 限定故障例。所有夹具必须是用户 LocalAppData 下 `Temp/db085-root-20260930/run-<unique>` 中的新路径；脚本写任务 marker，从不接受或删除已有仓库。完整原始子进程 JSONL、`evidence.json` 和含 24 个主矩阵样本及 8 个动作样本的原始 `measurements.json` 写在被 Git 忽略的 `.artifacts/db085-root-20260930/<run-id>/`。`evidence.json` 引用 C: 夹具和每个子进程 PID/日志。脚本若失败也保留中途数据，不自动清理。构建后可调用 `selftest <new-scratch-parent>` 做核心拒绝反例的确定性测试；其具体覆盖以实际宿主输出为准。

脚本在真实写点或检查点收到 `{type:"READY",pid,point}` 后确认 PID 与目标，再从控制进程 `Kill(entireProcessTree:true)`。`partial:A|B|Root` 后还要求 inventory 中对应文件 `length > completeLength`，证明确有短尾。提交组覆盖三文件的部分写、完整写返回、flush 返回和 A/B/root/回包检查点。恢复组用 A、B、root 各一种短尾再次 kill：候选资源关闭、动作前、截断前、截断后屏障前、动作后、全新打开前、三次重确认后及交付前；完整根未回包另走无截断重确认/交付边界。每次重试后先独立进程 inspect 首次交付视图，再正常提交新 pair、再独立进程 inspect。没有用第二次恢复掩盖首次交付错误。

`inventory` 对三个文件给出连续完整前缀长度、SHA-256 和固定帧 offset；控制器自己重算 hash，并在 seed 后、第一次 kill 后、恢复再次 kill 后、成功恢复及继续提交后逐次保留所有先前完整前缀和地址，包括已写全但根未发布的 orphan。普通 inspect 前后还对包括短尾原字节的四个完整文件逐个比较长度与 SHA-256；短尾仅允许在显式恢复中处理。完整根选择新 pair，即使应用尚未收到回包；根之前只能选择旧 pair，不能混合。

## 成本边界

`measure` 为历史 `0/32/256` 和完整未发布残留 `0/32/256` 两个独立轴，每格新建一次预热和三份记录夹具。历史轴预置 N 次正常发布，残留轴让真实 `Commit` 在 `AfterAppendB` 抛出内部预期异常 N 次，再通过无截断恢复确认所有完整帧，准备成本排除在记录区间之外。每份样本记录实际完整帧数、完整提交、全新资源 `Open`、第一次选中 pair 读取、两段逐字段求和，以及之后独立的完整无动作 `Recover` 调用。另有 A/root 两种真实 2B 部分写入实修动作，每种一次预热与三份记录。它们是受控写失败成本夹具，不能冒充外部 kill。恢复报告将检测与资格合在首段，动作含复核，全新资格与重新确认合在末段；完整 Total 还包含交付末尾，阶段之和不等于 Total。每阶段数据包含 Stopwatch、当前线程分配、RBF 逻辑/原始请求及返回、预取、直接 FileStream 读、成功的追加调用层字节、flush 次数与实际 flush API 等待。`OpenAndFirstRead` 是两个不重叠阶段之和，排除间隙与 Dispose，不再与嵌套 scope 相加。

软件请求/返回字节与成功追加的调用层字节不是设备物理 I/O；真实写/flush 调用由代码路径计数，不能用文件大小差代替。OS/device 缓存没有冷却；全新进程和新 reader 只清除进程内 Store 缓存。线程分配不是峰值或驻留内存；flush 返回不证明断电持久性，Windows kill 不证明目录项或硬件屏障。资格 read budget 只覆盖被显式收费的 scanner/RBF 读取，不能扩大解释为总 I/O 上限。未记录样本中位数或跨路线速度倍率之前，必须检查原始样本、测试结果及有效的相同责任边界。

## 与当前产品路线的可比性

| 责任 | 当前产品源码事实 | 本实验承担的较小责任 |
|---|---|---|
| 发布顺序 | [Repository.Publish](../../src/DurableGraph.Persistence/Repository.cs)先 `States.AppendDurably`，再 `HistoryJournal.Append`，最后 `CreateBranch` / `AdvanceRef` 发布 branch；[SchemaStore](../../src/DurableGraph.Persistence/SchemaStore.cs)注册时单独 append + durable flush。 | 已有 A/B 文件上追加两值，各自 flush 后追加并 flush 一个根；没有 Schema 注册、State 图、Journal branch/tag。 |
| 现有目录/位置权威 | storage 的 [SegmentLocator.Publish](../../../atelia-storage/src/RbfSegmentStore/SegmentLocator.cs)发布 `active.segment`，[EventJournal.CheckpointCatalog](../../../atelia-storage/src/EventJournal/EventJournal.Refs.cs)管理 catalog 快照；[统一根对照](../../docs/design-branches/0085-unified-root-comparison.md)已指出旧根不能引用这些元数据的“最新”状态。 | 根固定两张 ticket、文件身份与可见 EOF；不解决真实 ref 集合快照、轮转、新文件名持久化。 |
| 现有 S0 恢复 | [RepositoryRecovery.Recover](../../src/DurableGraph.Persistence/RepositoryRecovery.cs)在独占锁中资格判断、关闭候选、复检/必要截断 flush，再严格重开并确认依赖；[HistoryJournal.ConfirmDurable](../../src/DurableGraph.Persistence/HistoryJournal.cs)按事件/对象/ref-op 顺序确认。 | 同样保留完整前缀和重试纪律，但资格只覆盖固定三文件及固定形状双值记录。 |

[S0-D 旧成本记录](../../docs/design-branches/0085-s0d-cli-rescue-implementation.md#成本增量与完整入口)已经测过产品路线基线/短尾/+32 提交，属于 2026-09-29 的**历史证据**，不是本次同夹具对照。真实 DG 有 Schema、State、Journal、ref、目录、可变图及所有完整事实依赖；实验只有两个固定 long。它们可比较协议责任和相同机制的调用数，不能把耗时相除宣称产品加速。此首片的判断应限定为统一根选择与恢复在固定形状下是否成立，以及将真实多分支/tag/轮转纳入根还缺哪些责任。
