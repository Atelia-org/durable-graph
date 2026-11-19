# Registered leaf codec probe

研究分支 `research/db104-registered-leaves` 的实验入口。产品对照为 `c3478351` 的主线 DurableText。文本和变长位图在各自程序集声明独立 CLR 类型，宿主生成 Schema 后将 codec 显式登记到模型环境。两种类型的字段、别名、List、历史读和冷开续写共用同一路接入。

- [单篇文本、位图和诊断](Consumer/README.md)；[多进程运行器](Run-Consumer.ps1)。
- [128 条文本的整图基准](Fanout/Program.cs)和[三路运行器](Fanout/Run-Fanout.ps1)。每条 4096 个 UTF-16 单元，轮换修改其中 6 条，每轮仅改一条的首尾两处。预热 6 个提交周期后，测 24 次编辑加 SaveGraph；每次冷读重新打开 Repository 并物化图，然后逐字验证全部 128 条正文。冷读只计到图返回，校验和资源关闭在计时外。进程内取排序后的中间偏上观测，三进程再取中位数。线程分配含 DG 与同步 I/O 相关的托管分配。主线内建、研究树内建和注册文本依次轮转。进程及 OS 页缓存仍可复用。[逐进程结果](results/fanout.jsonl)
- [内建编码器前后对照](CodecParity/Program.cs)。以固定种子对每个候选 DLL 各回放 8400 次，输出变化标志、长度、Delta 的统一 SHA-256。传入带有 DG/Serialization/Binary 三个 Release DLL 的目录，例如 `dotnet run -c Release --project experiments/RegisteredLeafProbe/CodecParity -p:DgBinaryPath=E:/dg/bin/old`。切换旧/新 DLL 时分开编译目录或先重新生成。此白盒驱动借用既有测试程序集名，独立包消费走公开 API。
- [注册叶生命周期与版本回归](../../tests/DurableGraph.Persistence.Tests/RegisteredLeafLifecycleProbeTests.cs)，检验真实准确前驱、独立 Event、失败、历史版本、未消费字节与强制 Base 重写。
- [普通包实验](Run-PackageConsumer.ps1) 从独立源码副本生成私有候选 NuGet 包，并另建消费项目。无须线上发布。

在 Windows PowerShell 中，从本分支仓根运行。Storage 项目需为仓中 eng/StorageDependency.props 固定的版本。选择新的输出目录以保留各进程的存储、哈希和样本。

~~~powershell
$storage = 'E:/repos/Atelia-org/atelia-storage'
$master = 'E:/repos/Atelia-org/durable-graph'
./experiments/RegisteredLeafProbe/Run-Consumer.ps1 -StorageSourceRoot $storage -OutputDirectory W:/temp/registered-leaf-consumer
./experiments/RegisteredLeafProbe/Fanout/Run-Fanout.ps1 -MasterRoot $master -StorageSourceRoot $storage -OutputDirectory W:/temp/registered-leaf-fanout
~~~

研究范围为无 DG 出边的普通引用对象。实验声明采用带稳定名义 ID 与版本的顶层、公开、非泛型 sealed class。声明者负责冻结内容与格式正确性。新增可变叶应在实际业务模型、Schema 发布和包闭包上复验，再用于生产。
