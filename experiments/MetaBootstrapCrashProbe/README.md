# Split DG ProcessCrashOnly 见证

本 Probe 验证 DB-087 内部 split Repository：真实生成的 Widget、共享 List、Schema meta 图与业务图经两 ref 提交后，外部终止 writer 的六个具体窗口能否由独立 verifier 冷恢复并继续真实 Delta。

这是源码模式的有限进程中止见证。默认公开工厂仍走旧路线；不授 PackageReference、OS crash、断电、设备故障或完整 P1d 门禁资格。正常关闭重开、异常抛出与 writer 的 Dispose 都不能代替这里的外部 Kill。活动状态与后续门禁见 [PROJECT-STATE](../../src/PROJECT-STATE.md) 和 [DB-087](../../docs/design-branches/0087-meta-bootstrap-refactoring-route.md)。

先由统一构建者显式构建一次，controller 默认且始终不构建：

```powershell
dotnet build experiments/MetaBootstrapCrashProbe/MetaBootstrapCrashProbe.csproj -c Release -p:UseStorageSources=true -p:StorageSourceRoot=E:/repos/Atelia-org/atelia-storage --verbosity minimal -m:1 -nr:false
pwsh -File experiments/MetaBootstrapCrashProbe/Run-Probe.ps1 -AssemblyPath experiments/MetaBootstrapCrashProbe/bin/Release/net10.0/Atelia.DurableGraph.Persistence.Tests.dll
```

项目不加入根 solution。host 使用 `Atelia.DurableGraph.Persistence.Tests` 的既有内部访问身份，因此输出文件名为 `Atelia.DurableGraph.Persistence.Tests.dll`；它是独立 console host。controller 接受 `-AssemblyPath`，但要求本项目 assets 中六个 storage 依赖全部为实际 ProjectReference，且 storage HEAD 与 [StorageDependency.props](../../eng/StorageDependency.props) 的 pin 一致、工作树干净。该检查记录源码来源，不代替统一构建者核对 DLL 与冻结源码的关系。

固定矩阵每项使用一个全新的 repository：

| writer 停点 | fresh verifier 选择 |
|---|---|
| `AfterMetaAppend` | 两个旧根 |
| `AfterBusinessAppend` | 两个旧根 |
| `AfterFirstCuEnd` | 两个旧根；只有第一个参与 ref 的 End 已追加 |
| `AfterAllCuEnd` | 两个新根；两个 End 已完整追加，尚未代称 Confirmed |
| `AfterPublicationConfirmed` | 两个新根；发布确认后、DG 安装前 |
| `BeforeInstall` | 两个新根；DG 安装停点 |

writer 写完原子 `ready.json`，输出并 flush 单行 `READY`，随后保持存活等待外部终止。controller 同时核对 READY 行、文件中的窗口和真实 PID，调用 `Process.Kill(entireProcessTree: true)`，等待退出后才启动 fresh verifier。进程以 `UseShellExecute=false`／`CreateNoWindow=true` 隐藏启动，输出和错误均捕获；READY 默认限时 60 秒，seed／verify 默认 120 秒，终止默认 30 秒。参数仅调这些有限等待，不扩大固定矩阵。

三个 host 命令使用 named 参数：

```text
dotnet <host.dll> seed --repo <fixture> --result <seed.json>
dotnet <host.dll> writer --repo <fixture> --window <window> --ready <ready.json>
dotnet <host.dll> verify --repo <fixture> --window <window> --ready <ready.json> --result <verify.json>
```

READY 保存旧 State／Schema、已追加的候选地址、对象 IDs／heads／H、全部旧完成帧和新候选完成帧摘要、实际 publication stage 及 CU End／flush-hook 次数。controller 先核对旧根、Root／List IDs 与 seed 一致。verifier 负责确认所选两根、业务图和 meta 完整解释、完整帧保留及旧历史；继续一次保存必须是真实单条 Delta，保持准确 prior、IDs 与实际 H。controller 核验 `Passed`、`ObservedOldOrNew`、`SingleDelta`、`ExactPrior`、`IdsPreserved`、`HVerified`、`CompletedFramesPreserved`、`ReadOnlyFilesUnchanged`、`NoNonNoneRecovery` 以及 writer／verifier PID 和窗口，禁止以单个进程内成功报告代替 fresh 恢复。

默认证据目录是 `.artifacts/MetaBootstrapCrashProbe/<UTC>-<guid>/`，也可指定一个尚不存在的 `-RunRoot`。每个窗口保留 `repo/`、seed／READY／verify JSON 和三进程 stdout／stderr；顶层 `receipt.json` 记录 PID、退出码、时间、窗口预期与实际结果、源码 revision、source assets、输入及 DLL SHA256 和整体成功状态。失败时也保存已有证据。controller 不删除夹具、不改完整 data 帧、不自动重试或恢复旧 writer；仅 fresh owner 执行 backing store 的正常尾恢复。

六个停点均在相应追加调用完成后收集证据。所有 READY 记录的旧帧和候选帧都必须在 fresh owner 中保持地址、role、payload 与 tail-meta 摘要；前三窗口中的完整候选只是 orphan，不能被当成已发布根。verifier 先以只读 owner 核对所有文件字节不变，再以可写 owner 续写；本矩阵要求 backing recovery 没有非 None 动作，不以丢失已完成候选为成功。controller 的 Kill 成功本身不证明发布成功。此 Probe 不测 timing、总堆、总 O(k)，也不穷举所有异常与初始化窗口。
