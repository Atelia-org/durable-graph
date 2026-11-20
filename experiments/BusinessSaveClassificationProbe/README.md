# DB-106 业务保存分类对照

这是实验分支上的一次性对照。落盘程序按业务入口保存八代，对照已发布的对象记录和冷开值。热路径程序复用已冻结的行，单独计量 `ObjectWritePlanner.ClassifyCaptured`。两者以测试友元身份查看内部结果。[实验结果与主线建议](../../docs/design-branches/0106-container-adapters/experiment.md)

## 复现

在 Windows 上使用 PowerShell 7 和仓库固定的 .NET SDK。保留这份实验脚本所在的工作树，再从 DurableGraph 仓库建立两个独立构建目录。输出目录可放在任意有写权限的临时位置。

```powershell
$area = Join-Path $env:TEMP ('db106-' + [guid]::NewGuid())
$null = New-Item -ItemType Directory -Path $area
$baseline = Join-Path $area 'baseline'
$candidate = Join-Path $area 'candidate'
git worktree add --detach $baseline e1a4e42d
git worktree add --detach $candidate c19a8d2a
```

按[存储源码模式](../../docs/storage-dependency.md)配置 `StorageSourceRoot` 为 pin 对应的绝对路径，再分别构建两个版本。默认 Release 测试目录用来核对实际加载的产品 DLL。

```powershell
foreach ($tree in $baseline, $candidate) {
    Push-Location $tree
    try {
        dotnet build DurableGraph.slnx -c Release -m:1 -nr:false -p:UseStorageSources=true "-p:StorageSourceRoot=$env:StorageSourceRoot" -p:ShouldUnsetParentConfigurationAndPlatform=false
        if ($LASTEXITCODE) { throw "Build failed in $tree" }
    } finally { Pop-Location }
}
$probes = 'experiments/BusinessSaveClassificationProbe'
pwsh -NoProfile -File "$probes/records/Run.ps1" -BaselineRoot $baseline -CandidateRoot $candidate -RunDirectory (Join-Path $area 'records')
$timing = Join-Path $area 'timing'
pwsh -NoProfile -File "$probes/classification/Run-Comparison.ps1" -Baseline $baseline -Candidate $candidate -CandidateRevision c19a8d2a -Output $timing
```

热路径采样默认绑定十六进制 CPU 掩码 `10`，可用 `-CpuMask` 选择本机可用的逻辑处理器。每轮四对进程交替运行 AB、BA。重跑互补顺序时传 `-FirstArm B`、另一个输出目录和 `-ReuseHarnessFrom (Join-Path $timing 'harness')`。正常 JIT 设置的对照另传 `-JitMode default`。如要重现第一版差异，将候选构建提交和 `-CandidateRevision` 一起改为 `97d6189d`，复用同一个消费程序。计时应在构建和全量测试结束后进行。

磁盘实验的逐记录回执在 `records/{baseline,candidate}/records.json`，计时的逐样本数据在 `timing/samples.csv`。完整回归从候选工作树按同样的源码模式运行 `dotnet test DurableGraph.slnx -c Release --no-build --no-restore`。
