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

热路径分配值不计已经从池取得并可复用的容量。多片全文访问每次仍要复制整篇，首次租借、嵌套访问和不同正文集合的缓存局部性可改变成本。
