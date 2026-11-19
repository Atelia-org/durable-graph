# 重跑 DB-103 字面查找对照

在本目录用 .NET 10 的 Release 模式构建。默认引用仓库的 Release 产品 DLL，也可以显式指定本地已验明的普通包 DLL。程序通过公开 API 执行查询。每份 CSV 记录实际加载的 DLL 的 SHA-256 和 .NET 运行时版本。

```powershell
dotnet build IndexOfProbe.csproj -c Release
# 用普通包 DLL 重建时加 "-p:ProductDll=<Atelia.DurableGraph.dll 的绝对路径>"
$probe = './bin/Release/net10.0/Atelia.IndexOfProbe.dll'
dotnet $probe smoke
foreach ($rep in 0..2) {
    dotnet $probe matrix "./matrix-$rep.csv" $rep
    if ($LASTEXITCODE -ne 0) { throw "matrix-$rep" }
    dotnet $probe corpus "./corpus-$rep.csv" $rep
    if ($LASTEXITCODE -ne 0) { throw "corpus-$rep" }
}
foreach ($rep in 0..2) {
    dotnet $probe short "./short-$rep.csv" $rep
    if ($LASTEXITCODE -ne 0) { throw "short-$rep" }
}
python summarize.py . ./summary
```

每次使用新的输出路径。九个测量进程顺序运行，构建和其他性能测量均在计时外。项目关闭分层 JIT，以便不同样本使用同一编译模式。也可运行 `python summarize.py ../indexof-results/batches.zip ./summary` 从已保存的原值重新汇总。

- **matrix** 对比已取得的快照 `IndexOf`、一次完整 `VisitSpan` 的 Ordinal 查找，以及用快照 `CopyTo` 重叠分窗的诊断臂。每次只取从指定起点开始的首项，所有调用均检查绝对下标。用 `string.IndexOf(needle, start, StringComparison.Ordinal)` 与植入索引独立验证答案。
- 布局包括整串单片、非零偏移单片、以四处或六十四处等距的单单元替换产生的 9 或 129 片。同一个编辑器捕获快照后保持正文稳定。构造期读取实际 chunk 长度序列并与预计边界逐项比对，还核对单切片底串偏移。
- 普通四字针未命中时，背景没有首字“北”。`prefix-late` 与 `periodic-*-miss` 单列重复前缀。后者使用交替 ab 的正文，将同周期针倒数第三个单元改为 X，保留大量通过首末字符检查的假候选。另有长稀有针、单字符、跨片和非零 start 跨窗样例。
- 每进程打乱案例顺序，两条产品路径和诊断臂都预热。每臂按约 8 ms 校准迭代数，最多 131072 次，随后三批各换一次槽位。记录原始计数器 ticks、频率、迭代数和当前线程分配，另列每调用 ns、B。时间覆盖搜索、结果核对与共同的循环/委托开销。`WindowControl` 在多片上从非零偏移 `CopyTo` 时会使用延迟片尾索引，该场景预热已建好索引。
- **short** 是看完主要矩阵后追加的 40 个工况。对同一个交替 ab 正文、同长度的模式，比较首单元或倒数第三单元发生失配，并补两个 256 单元短正文的普通查询。计时与主要矩阵相同。
- **corpus** 扫描 1000 篇常驻正文，比较复用快照、临时取快照、每篇访问一次和每次查找重新访问，以完整扫描为计量单位。检查整库结果的加权校验和。详情见 [Corpus.cs](Corpus.cs)。

原池化版本的热路径分配值不计池中可复用的容量，该版本多片访问每次都复制整篇。切换到按版本缓存的产品 DLL 时，三个热探针的预热步骤会先填充被访问正文的缓存。

## 按版本测首次访问、第二次访问和编辑后再访

`cold` 让两条路线各自拥有一套内容相同的新编辑器。快照路线持有每篇同版快照，多项续查复用该快照。Visit 路线对每篇调用一次，回调内完成 Ordinal 查询。同一套编辑器依次扫三次。第一次尚未访问，第二次紧接第一次，第三次在指定正文发生实际等长改写后运行。准备语料、改写、构造新版快照和计算独立字符串 oracle 均在计时外。为减少准备阶段垃圾的干扰，在第一和第三次计时前强制全代 GC。软件已在其他语料上预热，“首次”指该版正文首次查询。

- 6 个工况与旧整库语料相同。1000 篇中 20% 或 100% 为 9 片，测试稀疏命中、每篇早中和每篇取八项。编辑阶段只改原有碎片正文（200 或 1000 篇）。另有全碎片取八项组仅编辑 1 篇，第三扫经过 1 篇改写正文和 999 篇同版正文。
- 两个大正文工况各含 16 个独立的 1 Mi UTF-16 单元、9 片的编辑器。首项在偏移 8–15，或从末前 64 单元起搜，在末前 25–32 单元命中。两组均改写 16 篇。所有工况的首篇把第一次命中右移 1 单元，其他编辑保持结果。各次都校验加权绝对位置，编辑前后总和必须恰差 1。
- 每个案例每进程用两批全新正文，两条路线各占一次首位。原值记录每次整扫的 ticks、频率、当前线程分配和各代 GC 次数。`edit_target_documents` 标识该工况第三扫前的编辑计划，`edited_documents` 标识该样本实际已改写的篇数。按版本缓存实现的驻留正文字符负载可按已访问的多片篇数 × 每篇长度 × 2 字节估算。CSV 的分配量是实测值，排除对象头的字符负载是推算值。

```powershell
dotnet $probe cold-smoke
foreach ($rep in 0..2) {
    dotnet $probe cold "./cold-$rep.csv" $rep
    if ($LASTEXITCODE -ne 0) { throw "cold-$rep" }
}
python summarize.py . ./summary cold
```

三个独立进程共输出 324 个样本。脚本对每个工况、阶段和路线先取进程内两批中位数，再取三进程中位数，生成 54 行。整次查询以 ms/次整扫、B/次整扫表示。1 Mi 的两组按 16 篇计，可从时间列乘 1000 再除 16 得到 µs/篇。
