# Split DG ProcessCrashOnly 见证

2026-10-17：已适配 HeadStore 和三入口布局；本轮重新运行初始化三个及保存六个真实 Kill 窗口均通过，来源和证据见 [DB-096 接入](../../docs/design-branches/0096-history-and-head-model/headstore-adaptation.md)。

本 Probe 验证 DB-087 内部 split Repository 的有限进程中止窗口：默认模式检查真实生成的 Widget、共享 List、新增 Schema 的六个保存停点；`-InitializationOnly` 检查直接工厂三次成功 CreateHead 后的停点。两模式都由 controller 外部终止 writer，再由独立 verifier 冷验证。

这是源码模式的有限进程中止见证，默认公开工厂使用同一 meta 自举内核；不授 PackageReference、OS crash、断电、设备故障或完整 P1d 门禁资格。正常关闭重开、异常抛出与 writer 的 Dispose 都不能代替这里的外部 Kill。活动状态与后续门禁见 [PROJECT-STATE](../../src/PROJECT-STATE.md) 和 [DB-087](../../docs/design-branches/0087-meta-bootstrap-refactoring-route/0087-meta-bootstrap-refactoring-route.md)。

[DB-092 B](../../docs/design-branches/0092-type-namespace-refactoring/implementation-b.md)只适配本 host 的内部类型／成员名称并验证编译，协议及窗口保持；没有重跑 Kill 矩阵，不给旧结果追加本次源码的进程中止资格。

[DB-092 D3](../../docs/design-branches/0092-type-namespace-refactoring/README.md#d3-status)将具体资源 owner 改为 `RepositoryResources`，本 host 同步类型引用；本片只补编译验证，Kill 矩阵结果仍保其原来源与范围。

先由统一构建者显式构建一次，controller 默认且始终不构建：

```powershell
dotnet build experiments/MetaBootstrapCrashProbe/MetaBootstrapCrashProbe.csproj -c Release -p:UseStorageSources=true -p:StorageSourceRoot=E:/repos/Atelia-org/atelia-storage --verbosity minimal -m:1 -nr:false
pwsh -File experiments/MetaBootstrapCrashProbe/Run-Probe.ps1 -AssemblyPath experiments/MetaBootstrapCrashProbe/bin/Release/net10.0/Atelia.DurableGraph.Persistence.Tests.dll
# 初始化三窗口使用另一组全新夹具，不运行 seed 或六个保存窗口。
pwsh -File experiments/MetaBootstrapCrashProbe/Run-Probe.ps1 -InitializationOnly -AssemblyPath experiments/MetaBootstrapCrashProbe/bin/Release/net10.0/Atelia.DurableGraph.Persistence.Tests.dll
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

初始化模式只有以下三个固定窗口，无 seed。`init-writer` 直接调用真实 DG 内部 factory；停点在相应 `CreateHead(...).Unwrap()` 成功返回后、factory 交付 Repository 前。它不写业务或 meta 用户帧，也不制造占位 State。

| 初始化停点 | fresh verifier 结果 |
|---|---|
| `AfterFirstCreateHead` | `MissingSecondHead`：仅 Head 1 完整；fresh RO／RW 均拒绝，不补建、不改文件；随后裸 backing store 独占打开，证明失败 Open 已释放锁 |
| `AfterSecondCreateHead` | `MissingThirdHead`：Head 1/2 完整但书签目录 Head 3 缺失；RO/RW 拒绝且不补建 |
| `AfterThirdCreateHead` | `CompleteEmptyRoots`：三个正式空目录可 fresh RO 打开且文件不变；全关后 RW 保存真实 Widget 环／共享 List，再全关冷开续写准确单条 Delta |

writer 写完原子 `ready.json`，输出并 flush 单行 `READY`，随后保持存活等待外部终止。controller 同时核对 READY 行、文件中的窗口和真实 PID，调用 `Process.Kill(entireProcessTree: true)`，等待退出后才启动 fresh verifier。进程以 `UseShellExecute=false`／`CreateNoWindow=true` 隐藏启动，输出和错误均捕获；READY 默认限时 60 秒，seed／verify 默认 120 秒，终止默认 30 秒。参数仅调这些有限等待，不扩大固定矩阵。

host 命令使用 named 参数：

```text
dotnet <host.dll> seed --repo <fixture> --result <seed.json>
dotnet <host.dll> writer --repo <fixture> --window <window> --ready <ready.json>
dotnet <host.dll> verify --repo <fixture> --window <window> --ready <ready.json> --result <verify.json>
dotnet <host.dll> init-writer --repo <fixture> --window <window> --ready <ready.json>
dotnet <host.dll> init-verify --repo <fixture> --window <window> --ready <ready.json> --result <verify.json>
```

READY 保存旧 State／Schema、已追加的候选地址、对象 IDs／heads／H、全部旧完成帧和新候选完成帧摘要、实际 publication stage 及 CU End／flush-hook 次数。controller 先核对旧根、Root／List IDs 与 seed 一致。verifier 负责确认所选两根、业务图和 meta 完整解释、完整帧保留及旧历史；继续一次保存必须是真实单条 Delta，保持准确 prior、IDs 与实际 H。controller 核验 `Passed`、`ObservedOldOrNew`、`SingleDelta`、`ExactPrior`、`IdsPreserved`、`HVerified`、`CompletedFramesPreserved`、`ReadOnlyFilesUnchanged`、`NoNonNoneRecovery` 以及 writer／verifier PID 和窗口，禁止以单个进程内成功报告代替 fresh 恢复。

初始化 READY 采用同一 `DG.MetaBootstrapCrashProbe.v1` 协议，保存 `CompletedRefId`、有序 `CompletedRefs`、已完成正式 ref 文件的 `FormalRefImages`（路径／SHA256）及 `DeclaredUserDataFrames=0`。fresh verifier 自己清点 `InitialUserDataFrames=0`，不依赖 writer 声明；两窗都要求 `FileBytesPreserved`、`ReadOnlyFilesUnchanged`、`LocksReleased` 为 true。首窗 `SingleDelta`／`ExactPrior`／`IdsPreserved`／`HVerified` 明确为 false，表示没有可用 Repository 或业务续写；次窗才要求四项为 true，并保留实际 `SeedState`／`Schema`／`ContinuationState`／`Objects`。这里没有被 Kill 的 factory 返回结果，READY 不是初始化 outcome。

默认证据目录是 `.artifacts/MetaBootstrapCrashProbe/<UTC>-<guid>/`，也可指定一个尚不存在的 `-RunRoot`。每个窗口保留 `repo/`、READY／verify JSON 和进程 stdout／stderr；保存模式另有 seed JSON／进程，初始化模式没有 seed。顶层 `receipt.json` 记录 `Mode=Save|Initialization`、PID、退出码、时间、窗口预期与实际结果、源码 revision、source assets、输入及 DLL SHA256 和整体成功状态。失败时也保存已有证据。controller 不删除夹具、不改完整 data 帧、不自动重试或恢复旧 writer；仅 fresh owner 执行 backing store 的正常尾恢复。

六个保存停点均在相应追加调用完成后收集证据。所有 READY 记录的旧帧和候选帧都必须在 fresh owner 中保持地址、role、payload 与 tail-meta 摘要；前三窗口中的完整候选只是 orphan，不能被当成已发布根。verifier 先以只读 owner 核对所有文件字节不变，再以可写 owner 续写；保存矩阵要求 backing recovery 没有非 None 动作，不以丢失已完成候选为成功。初始化停点尚无已完成业务帧，正式 ref 文件保留与缺根拒绝是其直接证据，后写阶段才验完成业务帧。controller 的 Kill 成功本身不证明发布成功。此 Probe 不测 timing、总堆、总 O(k)，也不穷举所有异常与初始化窗口。
