# 共存提交的测量与复现

2026-11-20 在 Windows x64 的本地文件仓对比直接以两种具体文本类型作为字段和 List 元素的生成模型。共存版本、相邻原生主线、最终产品与 Storage 的 Git 来源、Release DLL 的 SHA-256、归档文件哈希和运行环境见[清单](manifest.json)。主线验收另见 [DB-104](../README.md)。

- [单条正文](single.csv)含 4,096 和 65,536 个 UTF-16 单元的首次 Save、八次局改、热 Save、完整周期和关仓重开一次读全文。每个进程先预热两种实现，再按轮换顺序测每种规模的 16 个提交，取三个进程中位数。完整周期逐样本相加，计到成功保存后延迟到下次编辑的开销。冷开包括新建模型注册、仓库、图、全文物化及关闭。
- [编码诊断](codec.csv)记录双读后八改再存、物理文件增长、首笔与热路径实际 Base 编码次数，以及两种 codec 的 33 片准备和回放分配。三个正式进程共 192 条热 Delta 均有准确前驱，两实现每一对 Delta 长度和 SHA-256 完全相同。
- [128 条正文的结果](fanout.csv)保留三个实验。每条 4,096 个单元，轮换六个目标，每次只在一条中替换两处，共预热六次、测 24 次。每次冷开重新创建仓和图，复用相同的进程与模型配置。计时到 OpenGraph 交付为止，之后核对全部 128 条的全文。所有时间均可受 OS 页缓存影响，分配仅计调用线程。
- [逐提交配对](matched-commits.csv)用两个相互独立的消费进程，各持一个仓，固定在同一逻辑核。驱动在每个相同编辑步骤轮换先后顺序，开始前和完成后才通过管道交接控制。每次测量只有一个消费进程在做编辑或保存。六对进程共 144 对实测提交。CPU 列使用 Windows QueryThreadCycleTime 的实际线程周期。完整周期分组的百分比取每对相对差的中位数。

三路轮转 (`rotation`) 的首轮热中位数为 1.359 ms 和 1.705 ms，后续六对完整进程 (`process-pair`) 的差值有正有负。逐提交配对 (`commit-pair`) 的相对差中位数为 +2.67%，登记实现先执行和后执行分别为 −3.94% 和 +7.31%。应结合先后效应解读墙时。登记实现完整提交多分配 11,592 B。实体记录审计在同一批九个轮转仓计时结束后只读进行。每仓初始 130 个 Base，随后每次只写一条指向该叶最近版本的 Delta，30 条合计 626 B，九仓的逐条 SHA 相同。

[最终产品复验](final.json)通过同一份登记文本 Fanout 宿主，对提交 `f829f849` 独立生成的 Release DLL 再跑三个进程。全量测试和打包在同机并行，耗时只作观察。三个进程均逐字核对冷开内容，完整热提交均为 456,072 B，冷开均为 5,422,832 B。三仓共 90 条热 Delta 与共存原生样本逐笔同 SHA、同对象准确前驱。

## 重跑

以下命令在 Windows PowerShell 中从 DurableGraph 仓根执行。`$storage` 指向含清单所列 Git 提交的 Storage 仓。创建新的工作目录，各驱动会拒绝覆盖先前的输出。原始记录和补充驱动收在[证据包](raw-and-drivers.zip)。`measured/` 保存本轮数据。

```powershell
$storage = $env:StorageSourceRoot
$run = Join-Path $env:TEMP ('dg-leaf-coexist-' + [guid]::NewGuid())
Expand-Archive docs/design-branches/0104-text-editor/coexist-results/raw-and-drivers.zip $run
git worktree add --detach "$run/coexist" 06c57dd4a7f3baf75fd89a7a7d592861fc61ce4f
git worktree add --detach "$run/baseline" 375e033eb126424032b2f33513d5479cb9978dc8
git worktree add --detach "$run/final" f829f849fbbf123bcfa83f4c1bd89dd3792d045b
git -C $storage worktree add --detach "$run/storage" 3f780ad95239d7556f917e1adca6839cda144c92
$probe = Join-Path $run 'coexist/experiments/RegisteredLeafProbe'
& "$probe/Run-Consumer.ps1" -StorageSourceRoot "$run/storage" -OutputDirectory "$run/single"
& "$probe/Fanout/Run-Fanout.ps1" -MasterRoot "$run/baseline" -StorageSourceRoot "$run/storage" -OutputDirectory "$run/fanout"
dotnet build "$run/audit/Audit.csproj" -c Release "-p:AssemblyDirectory=$run/fanout/lab-leaf/bin/Release/net10.0"
dotnet "$run/audit/bin/Release/net10.0/Audit.dll" "$run/fanout"
python "$run/paired.py"
python "$run/summarize-paired.py"
python "$run/lockstep.py"
python "$run/summarize-lockstep.py"
python "$run/final-fanout.py"
```

`paired.py` 和 `lockstep.py` 从共存检出构建产品和测量宿主。后者复用前者生成的分相源码并增加逐步同步与绑核，需要至少四个可用的逻辑处理器。`final-fanout.py` 从共存检出复制宿主源码，以最终检出的项目构建和运行，并对先前轮转的原生仓复核 Delta。编码与帧审计会在观测不符时失败。目录内的程序集哈希标识本次实际加载或使用的构建产物，新的检出路径与生成源位置可能影响重建的 DLL 哈希。
