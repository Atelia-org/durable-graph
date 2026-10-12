# RecoveryCrashProbe — S0-C 外部进程终止见证

本内部实验调用真实 `Repository` 提交、锁内 `RepositoryRecovery.Recover`、续写与独立进程严格只读冷开。合同见 [DB-085 S0-C](../../docs/design-branches/0085-s0c-recovery-implementation.md)。程序集 `Atelia.RecoveryCrashProbe` 仅通过内部友元访问检查点，不提供通用恢复工具。

主线程串行构建/执行；需源码模式 storage 的内部 instrumentation。示例在仓库根运行：

```powershell
$env:TEMP = 'C:\Temp\dg-s0c-task'
$env:TMP = $env:TEMP
New-Item -ItemType Directory -Path $env:TEMP -Force | Out-Null
dotnet build experiments/RecoveryCrashProbe/RecoveryCrashProbe.csproj -c Release -m:1 -nr:false -p:UseStorageSources=true -p:StorageSourceRoot=E:/repos/Atelia-org/atelia-storage -p:ShouldUnsetParentConfigurationAndPlatform=false
pwsh -NoProfile -File experiments/RecoveryCrashProbe/Run-RecoveryCrashProbe.ps1 -HostDll experiments/RecoveryCrashProbe/bin/Release/net10.0/Atelia.RecoveryCrashProbe.dll
```

脚本强制任务 TEMP 在 C:，只使用新建唯一夹具路径，不接受真实仓库路径，也不删除夹具。可用 `-Suite commit` / `-Suite recovery` 或 `-CaseFilter 'commit-state-partial-*'` 缩小执行。所有子进程使用 `ProcessStartInfo.ArgumentList`、隐藏窗口、不经过 shell。每条 JSON `READY` 必须携带真实子进程 PID 与匹配点；控制器在握手后外部 `Kill`，不让宿主通过正常关闭/Dispose 制造证据。失败也终止仍存活的自己启动的进程。

领域模型是从 SharedReadModel 改写的手工绑定，无 xUnit 或 generator。旧、新类型使用不同 Schema；根和子节点包含 cycle、重复 alias、文本与值。seed 确认 State=10、Event=11；尝试提交新类型 State=20 或 Event=21，触发真实 Schema 追加；恢复成功后确认完整旧图或已发布新图；续写 State=30、Event=31；新的只读进程验证图内容/类型/别名/cycle、旧 Event 与其旧 State。

| 组 | 实际窗口 | 案例数 |
|---|---|---:|
| 提交外部 kill | State/Event 两种提交 × Schema/State/Event/ref 四层 × 实写 2B HeadLen、完整 Write 返回且 Flush 前、Flush 返回 | 24 |
| 提交逻辑边界 | 两种提交 × 八个 `CommitCheckpoint` | 16 |
| 成功调用返回但应用尚未 ack | 两种提交 | 2 |
| 恢复外部 kill：有动作 | 四种真实短尾 × 11 固定点（资格资源关闭、动作前、SetLength 前/后屏障前、动作后、严格开前、History/Graph 资源打开后、Journal 确认前、严格开后、交付前） | 44 |
| 依赖重新确认：有动作 | 四种短尾 × 实际 Schema/State/Journal/ref 等每个文件屏障前后 | `8 × N` |
| 无截断路径再次 kill | 完整未 ack 新提交；严格开后/交付前，以及每个文件屏障前后 | `2 + 2 × N` |

`N` 从新建夹具的成功恢复日志发现，ref 对象的随机路径由角色别名在本次夹具映射，READY 同时记录实际检查点。当前五文件确认布局下总计 **138** 案例；以证据文件的实际案例数为准。每次恢复 kill 后再次从磁盘推导，不复用之前报告、Store 或 cache。

脚本在尝试前记录已确认前缀，在第一次实际 kill 后且恢复前，再记录每个 RBF 的完整连续前缀长度、SHA256 和帧起始 offset。第二次 kill、恢复续写以及冷开后都重新对照这份 **包含新完整 orphan / 未 ack 帧** 的前缀，要求字节与原地址保持。完整 ref、新发布回包前窗口要求新 head；较早窗口允许旧 head 或完整新 head，均禁止混合图。`inventory` 的资格调用仅只读；物理连续扫描通过后，按 HeadLen 枚举原帧 offset，不把 orphan 从前缀移除。

夹具根下 `evidence.json` 是机器可读覆盖矩阵，记录每个点、PID、两次终止、前缀/地址、图与日志路径；`child-*.jsonl` 保存 READY、恢复报告与逐文件屏障。恢复报告显式排除活的 Repository 对象，保留资格/动作/确认/严格打开/总计指标。确认是严格打开的嵌套子集，不二次相加。请求字节预算只涵盖资格 RBF 读，线程分配不是峰值；当前进程/OS/device cache 没有被冷却。

这里的证据是 **F1 外部进程故障**。真正 2B write 已由回调观察，再由父进程 kill；不是先造完整图像再裁剪。F2 断电/设备缓存/目录持久性是另外的平台模型，Windows process kill 不能证明这些性质。可靠 flush 与旧范围保护仍是平台假设。宿主自身不把诊断是否交付当成删除持久事实的理由。

2026-09-29 主线程串行 Release/source-mode 构建通过，完整 **138** 案例执行通过（含 96 次恢复再次 kill），Astra max 独立核对原始证据。日志与实际成本集中在 [S0-C 施工记录](../../docs/design-branches/0085-s0c-recovery-implementation.md)，机器可读结果保留于 `.artifacts/db085-s0c/host-evidence.json`，其引用的原始子进程日志在任务 C: 临时目录。无平台断电或独立包交付声明。

## S0-D 薄 CLI rescue 进程闭环

新增 `Run-CliRescueProbe.ps1` 使用相同 seed/真实部分写入宿主，但调用实际 CLI apphost 的 `rescue --check`、`--apply`、重复 `--apply`。仅创建专用 C: TEMP 下的新夹具。先按上述 Release/source-mode 参数构建宿主（其 ProjectReference 同时构建 CLI），然后运行：

```powershell
$env:TEMP = 'C:/Users/gdtut/AppData/Local/Temp/db085-s0d-20260929'
$env:TMP = $env:TEMP
New-Item -ItemType Directory -Path $env:TEMP -Force | Out-Null
pwsh -NoProfile -File experiments/RecoveryCrashProbe/Run-CliRescueProbe.ps1 -HostDll experiments/RecoveryCrashProbe/bin/Release/net10.0/Atelia.RecoveryCrashProbe.dll -CliExe src/DurableGraph.Cli/bin/Release/net10.0/Atelia.DurableGraph.Cli.exe
```

有界矩阵共七例：State 提交的 Schema/State/Event/ref 四种实写 2B 短尾、Event 提交的 Event 短尾，以及 State 短尾恢复完成后报告首写处外部终止、报告输出抛 IOException。后两例由宿主调用实际 `MaintenanceCli.Run` 配合自定义 TextWriter；它先独占打开仓库的全部文件，证明输出前锁和 Store 已关闭，再 READY 等待外部 kill 或抛异常。它们是 CLI 入口的宿主见证，不冒充 apphost 自身的环境故障钩子。输出失败要求退出码 6；输出终止不交付报告，随后先通过普通 OpenExisting 直接续写，再由实际 CLI apphost 重新从磁盘执行。

每例检查 check 前后完整文件集合、长度及 SHA256 不变；恢复后恰好保留崩溃图像中每个完整前缀（包括 orphan），首次维护或输出中断后，先以普通 OpenExisting 直接续写，不经过第二次维护；再对已续写仓库执行重复 apply，要求 noAction 且完整文件字节不变；最后独立冷开保留 acknowledged State/Event 及完整图关系。`evidence.json` 保留两种 CLI 运行方式、真实 PID/READY、原始 stdout/stderr、完整前缀及图事实；只证明 F1 进程故障，运行结果由 S0-D 施工记录追踪。
