# GraphHeap 普通包见证

本消费者只引用当前候选的 `Atelia.DurableGraph` 与 `Atelia.DurableGraph.Persistence` 包。使用包内 Generator/Build 生成四个应用模型，显式登记 `GraphRef.Definition`，不复制 GraphRef 源码或 Schema history，也不调用内部构造器或读取裸图地址。

三个独立进程覆盖公开接口的完整故事：

- `seed`：两个 builder 交错保存，从准确旧引用分叉，保存 no-op；生成世界目录 `Dictionary<EntryId, GraphRef>` 分别选择两个历史版本。未被目录选择的图保留为孤儿，堆不出现在业务分支列表。
- `resume`：冷开世界目录，通过其保存的旧引用恢复独立图，编辑后再次保存，并把新引用随世界 State 发布。旧世界仍指向原有版本。
- `verify`：只读冷开，先在没有条目/子节点模型的注册环境恢复目录，再明确 Open 每个条目。检查图内共享与循环、图间可变实例隔离、read-only/default 拒绝以及按世界目录枚举的简单字符串搜索不会混入另一世界或孤儿。

这是最小消费者见证，不包含产品全文索引；也不把公开行为测试当作字节级 Delta、Upgrade 或发布故障测试的替代，后者由产品回归覆盖。

```powershell
./experiments/PackageConsumerProbe/Run-GraphHeapProbe.ps1 `
  -PackageSource <本轮完整feed> -Version <G> -ExpectedSourceCommit <DG完整提交SHA>
```

可以传 `-WorkDirectory <不存在的仓外目录>`。runner不打包，拒绝旧工作目录；只读消费指定feed，在新cache中验证四个DG包与六个Storage包、准确DG源码提交及加载DLL哈希。它检查包内Generator和history工具实际执行，Publish后Verify不改四份应用history，GraphRef history来自包导出的reference manifest。

每条命令日志、`result.json`、三个进程的回执及cache/history/database都留在工作目录，失败也保留。验收必须使用包含公共GraphHeap API的本轮集成候选；此目录本身不代表已经通过包验证。
