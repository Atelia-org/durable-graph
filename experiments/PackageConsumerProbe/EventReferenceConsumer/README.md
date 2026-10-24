# EventReference 普通包见证

本消费者独立编译对 Persistence 包内公开 EventRef 的 ordinary inline 使用，不复制 EventRef 源码、history 或 bootstrap 登记。
显式登记 `EventRef.Definition` 与本消费者定义，验证公开 schema export 的三个 UInt32 字段、直接字段、nullable、List、数组及可保存的 default。

runner 将明确输入复制到仓外新目录，只用本轮同批 Runtime/Generator/Build 包；Publish 后 Verify 不改 accepted history。
两个独立进程先 seed 再只读冷 verify：同库 fork 继承原引用，两分支相同业务 Sequence 的 Event 用不同 CommitId 准确读取，主分支继续前进也不移动旧引用。
同时验证 State 回执、default、错误结果类型的拒绝及合规 SG 下的读取隔离。缓存、依赖 visitor、Schema 升级和故障注入不属于这个夹具。

```powershell
./experiments/PackageConsumerProbe/Run-EventReferenceProbe.ps1 `
  -PackageSource <本轮完整feed> -Version <G> -ExpectedSourceCommit <DG完整提交SHA>
```

可传 `-WorkDirectory <不存在的仓外目录>`；不传则在 TEMP 创建新目录。脚本从不打包。
`result.json`、每个 dotnet 命令的日志、两进程证据、全新 cache/history/database 均留在该目录；失败也保留现场。
该回执只限定这次包和这条消费者故事，不替代 DG 源码回归、Agent 纵切或性能测量。
