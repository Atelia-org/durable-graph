# 外部可变叶的普通调用与有盘对照

运行 [Run-Consumer.ps1](../Run-Consumer.ps1) 会构建本工作树的 Release 项目，创建新的真实 Repository，写出八个子进程的 JSON、[时间汇总](../results/consumer/summary.csv)和[诊断汇总](../results/consumer/diagnostics.csv)。从研究树根目录执行，输出目录须为新路径。

```powershell
./experiments/RegisteredLeafProbe/Run-Consumer.ps1 -StorageSourceRoot E:/repos/Atelia-org/atelia-storage -OutputDirectory (Join-Path ([IO.Path]::GetTempPath()) ("RegisteredLeaf-" + [guid]::NewGuid().ToString("N")))
```

需要 .NET 10 SDK、同 pin 的 Storage 源码以及 Python 3 用于汇总。各项目使用 ProjectReference。[本轮原始记录](../results/consumer/)含实际加载的 .NET 程序集 SHA-256、每笔编辑、保存以及写入记录的 body SHA-256。单跑某个 stage 的语法见 [Program.cs](Program.cs)。[读帧诊断](Records.cs)仅用于计时后的检查，通过反射取得仓库的内部读取器。正常消费代码、两个外部叶和 SG 模型使用公开 API。用于实际落盘检查的 Base 长度包含类型封套，Delta 长度只计正文。

## 两种不同的外部类型

[位图库](../Bitmap/VariableBitmap.cs)是独立程序集，项目只引用 DG Serialization。域对象可以改变位数，管理普通 byte 数组，冻结时复制到私有托管状态。普通等长更新的 Delta 记录有序的字节下标与新字节，改长可以写完整值。文本叶来自独立 Atelia.Text。业务 [SG 宿主](Models.cs)在直接字段、别名和 List 中引用两个叶。框架 Runtime、Persistence、Generator 与 Serialization 源码均无 Atelia.Text.TextEditor 或 Probe.VariableBitmapV1 名称分支。

- seed 保存初稿，再将同一文本中的三个字符替换成四个字符，同时将位图第 1 位置位。读回两条磁盘 Delta，指向对应对象的旧 Base；文本正文 20 B，位图正文为 `00010003`。
- resume 在新的进程中读回，对同一 GraphRef 的两次打开能保住图内别名且彼此隔离；继续局改文本、更改位数并设置新尾位后保存。verify 再次全关冷开，回读三个世代的完整文本、每一位、List 别名和 null。
- missing 分别省略一个注册入口，真实读历史时拒绝，在异常中指明缺少的 Probe.VariableBitmapV1 或 Atelia.Text.TextEditor。

## 热编辑、保存、冷开

每个独立 A/B 进程内都运行两种 SG 根，各仅有一个文本字段，分别是研究树中的内建 DurableText（已同步缓存优化 230efe36）与注册的 TextEditor。两者从相同的全 a 正文起步，随后 16 个周期，每周期在同一组分散位置串行执行 8 次 Replace，再 SaveGraph。两种实现均先单独预热，每个测量使用新的磁盘仓，两路按进程和正文长度轮换顺序，关闭分层 JIT。采样后完整比对字符串，按真实 ObjectId 重读各行。每次 Delta 精确指向上一次的叶所在帧。对应两个实现的 16 个 Delta 的长度和 SHA-256 逐一相同。

| UTF-16 单元数 | 路径 | 8 笔编辑 µs / B | 热 Save µs / B | 完整周期 µs / B | 关所有资源再开、读全文 ms / B |
|---|---|---:|---:|---:|---:|
| 4096 | 内建 | 2.55 / 2,944 | 764.5 / 56,960 | 768.8 / 59,904 | 42.70 / 1,072,304 |
| 4096 | 注册 | 5.50 / 11,360 | 749.85 / 50,824 | 755.5 / 62,184 | 40.90 / 1,082,960 |
| 65536 | 内建 | 6.10 / 7,552 | 754.95 / 49,880 | 760.15 / 57,252 | 41.02 / 1,503,784 |
| 65536 | 注册 | 8.25 / 9,984 | 740.15 / 50,892 | 750.05 / 62,492 | 40.27 / 1,519,368 |

每项先取进程内中位数，再取三个进程的中位数。完整周期逐样本相加后才求中位数，能计到在下一次 Replace 时消费上一轮接受回执的成本。第一个周期可能消费基线的回执；最后一次保存后直接释放图。冷开计时包括创建新模型注册、OpenReadOnlyExisting、OpenGraph、将最新正文物化为 string 及资源关闭。此时上一仓的资源已关闭，同进程和 OS 页缓存仍可复用。时间包括 Windows 本地文件 I/O。两条路径的各进程中位耗时范围重叠，不能据此排出微小延迟差异。分配为当前调用线程的托管分配，未计 OS 或其他线程。

4096 输入的每个热 Delta 为 72–74 B，16 项共 1,175 B；65536 的每项为 80–89 B，共 1,382 B。写了相同的增量正文，注册路径完整周期在这两个规模上分别多分配 2,280 B 和 5,240 B。本对照只覆盖单文本叶的串行调用和这组更改密度，两个 profile 都使用支持注册的同一研究版框架；多稳定叶、只改一条的行为由另一路对照评估。

## 编码、文件增长与分配归因

单独的 diagnose 进程为每个周期增加“先 `VisitSpan` 查两次，再连续改八处，最后保存”的对照。第一次查询可能首次展开当前版本，第二次查询读到热缓存。外部使用包裹真实 codec 的计数器，内建使用现有的 Base 编码观察器；这些观测器只安装在诊断进程。[诊断摘要](../results/consumer/diagnostics.csv)

| UTF-16 单元数 | 内建 / 注册的首次仓文件总长 | 内建 / 注册的后续 16 次总增长 | 每份首次原始 / 带类型 Base | 双读＋八改＋保存分配，内建 / 注册 |
|---|---:|---:|---:|---:|
| 4096 | 5,352 / 5,428 B | 5,756 / 5,756 B | 4,100 / 4,103 B | 59,904 / 62,184 B |
| 65536 | 66,792 / 66,868 B | 5,980 / 5,976 B | 65,542 / 65,545 B | 188,348 / 190,240 B |

两类型每个测量仓首笔都实际编码一次 Base，随后的 16 笔各真的写入一个文本 Delta，热路径 Base 编码次数为 0。文件总长是仓库内所有文件的 FileInfo.Length 之和，包含图、Schema、目录和控制文件等；数值会受分帧和地址编码影响。文件逻辑长度和 Delta 正文长度都不衡量 OS 实际写放大或断电耐久性。

为归因另在同一进程构造准确前驱、16 个分散替换（33 片）并绕过 Graph 层，预热后测两份真实 codec。4K / 64K 的 PrepareDelta 托管分配分别同为 696 / 712 B，编码出同 SHA 的 150 / 163 B Delta；PrepareBase 对新正文同为 20,560 / 193,576 B；ApplyDelta 在两个长度均为 3,648 B，并校验回放全文。

[早期两次诊断](../results/consumer/before-codec-alignment.zip)保留代码对齐前的对照。早期外部文本还物化来源映射列表，33 片 PrepareDelta 比内建多分配 4,200 B。单独替换 Text DLL 后，这项差额消失，两规模读、改、存的完整周期均少分配 2,128 B；随后 schema 准备去重后，完整周期又各减少 1,656 B。其余周期差额包含编辑和图层分配，当前对照未进一步拆分。
