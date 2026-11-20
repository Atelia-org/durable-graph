# 外部节点包的历史、集合和引用身份

第三方文本示例的 Book 是宿主的生成模型。它的 Page 定义由两个独立 NuGet 包依次提供。V1 与 V2 声明相同的持久节点名，各自提供当前版的私有评分、单个 Page 引用、内嵌的重复引用，以及指向内建 List 和 Dictionary 的引用。V1 还保存末尾的字符串引用。V2 只登记当前槽及可接纳的版本 1，由历史 Schema 提供旧布局。

运行器从新目录构建并打包两个模型，再以普通 PackageReference 编译两个宿主和验证生成的 Book Schema history。两个宿主使用各自的新缓存和模型包源。Reader 的依赖清单只接受 V2 模型包。每阶段的回执核对实际加载的领域库和 DG 程序集与消费包内 DLL 的 SHA-256。

| 独立进程 | 观察结果 |
|---|---|
| Writer | 写入两代 V1，评分从 10 变为 21，旧标签和 Page 的出边均被改写。 |
| Reader upgrade | 从旧仓恢复字段、环及集合别名，验证两次打开得到独立的列表、字典、可变节点和引用数组，再修改出边、List 项、Dictionary 项和评分，提交 V2。 |
| Reader verify | 全部资源关闭后按持久标签读取 10、21、34 三代，再次验证对象身份、图结构和版本隔离。 |
| Writer verify-old | V1 独立读回两代旧图，核对 V2 已退役的历史字符串引用。 |

在干净的 DG 生产 checkout 中准备 G 包。选用已经准备好的候选 feed 时可以只执行最后一条命令。各次运行使用新的输出目录。

~~~powershell
$g = "0.0.0-nodes.$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))"
$feed = Join-Path $env:TEMP "registered-nodes-feed-$g"
$run = Join-Path $env:TEMP "registered-nodes-run-$g"
. ./experiments/PackageConsumerProbe/PackageProbeSupport.ps1
Prepare-DurableGraphProbeFeed -RepositoryRoot (Get-Location).Path -OutputDirectory $feed -Version $g -StoragePackageSource $env:DG_STORAGE_PACKAGE_SOURCE
./experiments/PackageConsumerProbe/RegisteredNodes/Run.ps1 -PackageSource $feed -Version $g -OutputDirectory $run
~~~

`$PackageSource` 提供准确版本的四个 DG 包与六个固定 Storage 包。若它含原始 K4os 依赖，消费只用本地源；否则 NuGet 可以从 nuget.org 取得该依赖。最终 `verification.json` 汇总 SDK、G、各版本的实际消费包与 DLL 指纹和阶段回执。存储包的准备说明见[依赖指南](../../../docs/storage-dependency.md)，历史坏目录与实际出边准入见 [DB-105](../../../docs/design-branches/0105-registered-nodes/README.md)。
