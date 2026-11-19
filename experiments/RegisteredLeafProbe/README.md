# 普通宿主登记两种可变叶

这个探针在同一个生成模型中使用 `Atelia.Text.TextEditor` 和独立程序集的变长位图。宿主从两个库各调用两次登记帮助方法，模拟应用中的多个模块登记同一种叶。直接字段、别名、List 和 null 都进入图。[宿主模型](Consumer/Models.cs) · [位图实现](Bitmap/VariableBitmap.cs)

位图用 byte 数组保存内容。冻结时复制数组，等长变更的 Delta 记录有序的字节下标与新值，改长时保存新的位数与全部字节。包消费者只需要调用位图库的登记方法，冻结类型和 codec 均为私有。

## 运行

使用 .NET 10 SDK。从仓根运行下列命令，并为每次运行选择新的输出目录。源模式需要仓中固定版本的 Storage 源码，包模式需要匹配的本地 Storage 包源。[依赖准备](../../docs/storage-dependency.md)

```powershell
./experiments/RegisteredLeafProbe/Run-Consumer.ps1 -StorageSourceRoot $env:StorageSourceRoot -OutputDirectory (Join-Path $env:TEMP ("leaf-source-" + [guid]::NewGuid()))
./experiments/RegisteredLeafProbe/Run-PackageConsumer.ps1 -StoragePackageSource $storageFeed -OutputDirectory (Join-Path $env:TEMP ("leaf-packages-" + [guid]::NewGuid()))
```

两个运行器都启动四个独立消费进程，并写出 JSON 回执。[消费程序](Consumer/Program.cs)

| 阶段 | 验证内容 |
|---|---|
| `seed` | 先保存原图，再局改文本和一个位。读回两条指向原对象 Base 的磁盘 Delta，并核对位图编码。|
| `resume` | 重新打开仓库，同一 GraphRef 物化为相互独立的图，分别保留图内别名。继续修改文本、扩展位图、设置新尾位后保存，检查旧文本快照。|
| `verify` | 只读重开并逐一核对三个世代的全文、每一位、图内别名及 null。|
| `missing` | 分别省略一种叶登记，实际读取时拒绝，并在异常中指出缺少的类型标识。|

源模式通过 ProjectReference 构建。包模式另建本地 feed 和独立的 PackageReference 项目，并校验恢复包、生成器和加载的程序集。[包运行器](Run-PackageConsumer.ps1) 两条物理 Delta 的额外检查通过反射读取仓内记录，封装在探针中。[读帧检查](Consumer/Records.cs)

## 重跑共存版本的性能对照

主线提交 `06c57dd4a7f3baf75fd89a7a7d592861fc61ce4f` 同时包含原生文本、登记文本及同工作量的对照驱动。128 条文本的三路对照还需要紧邻的原生主线提交 `375e033eb126424032b2f33513d5479cb9978dc8`。保持这两个检出目录独立。从共存版本运行其 `Run-Consumer.ps1` 和 `Fanout/Run-Fanout.ps1` 即可重测。前者用 Python 3 汇总三个进程的结果并比对逐笔 Delta SHA，后者需要通过 `-MasterRoot` 指向原生版本检出目录。两者均使用相同的 Storage 源码版本、真实文件仓和 Release 构建。[DB-104](../../docs/design-branches/0104-text-editor/README.md)
