# atelia-storage 依赖工作流

五个存储包来自 [atelia-storage](https://github.com/Atelia-org/atelia-storage)。
[eng/StorageDependency.props](../eng/StorageDependency.props) 是本仓选择各包版本、来源 commit 和仓地址的唯一位置。
存储包可选择性发布，不要求五包同版本；它们与 DurableGraph 自身的包版本 G 独立。
日常构建通过 PackageReference 从 nuget.org 自动 restore，不需要 Prepare 或存储源码检出。

| 包 | 版本属性 | 来源属性 |
|---|---|---|
| Primitives、Data、Rbf | `StoragePackageVersion` | `StorageSourceRevision` |
| RbfSegmentStore | `StorageRbfSegmentStorePackageVersion` | `StorageRbfSegmentStoreSourceRevision` |
| EventJournal | `StorageEventJournalPackageVersion` | `StorageEventJournalSourceRevision` |

具体公开版本与完整 commit 以 props 为准；一个包的 source revision 不能代表整个五包集合。

## 日常构建

在仓根执行：

```powershell
dotnet build DurableGraph.slnx -c Release -t:Rebuild
dotnet test DurableGraph.slnx -c Release --no-build
```

升级时同时更新该包的版本与对应完整来源属性，再验证实际公开包的 restore、来源和消费行为。
包或源码构建通过不能替代公开下载验证；本次发布状态见 [PROJECT-STATE](../src/PROJECT-STATE.md)。

## 独立包 probes 的 feed

普通 build/test 不调用 `eng/Prepare-Storage.ps1`。独立包 probes 需要完整 feed 时，由
[PackageProbeSupport.ps1](../experiments/PackageConsumerProbe/PackageProbeSupport.ps1) 调用这个入口：
它从 nuget.org 下载 pin 指定的五个公开 nupkg，返回 feed 信息供 probe 使用，不 clone 源码、不重新构建存储包。
默认下载目录为忽略的 `.artifacts/storage-feed/base-<B>-segments-<S>-journal-<J>`；
各版本组合独立保留，已有旧 feed 不覆盖。这只是 probe 的输入目录，日常 restore 仍直接使用 nuget.org。
完成凭据 `manifest.published.json` 使用 schema 3，每个 package 记录自己的 version、sourceRevision、下载 URL 和 SHA256；
复用时逐包核对 nuspec、仓地址、签名条目存在性及记录的字节 hash。签名条目检查不等于完整信任链验证。
helper 返回的 `Version` / `Revision` 只代表基础三包；`Packages` 是五包来源明细，另返回两个独立包版本。

probe 将下载的五个存储包与本仓现打的四个 G 包组成自己的完整 feed，G 须不同于每个存储版本。
各 runner 的 `-Version` 只表示 G；显式提供 `-PackageSource <feed> -Version <G>` 时消费已有完整 feed，不改写它。
实际命令见[包实验导航](../experiments/PackageConsumerProbe/README.md)与[根 README 的打包段](../README.md#从源码打包)。

## 显式源码联调

```powershell
$storage = 'E:/repos/Atelia-org/atelia-storage'
dotnet restore DurableGraph.slnx -p:UseStorageSources=true "-p:StorageSourceRoot=$storage"
dotnet build DurableGraph.slnx -c Release --no-restore -p:UseStorageSources=true "-p:StorageSourceRoot=$storage"
dotnet test DurableGraph.slnx -c Release --no-build -p:UseStorageSources=true "-p:StorageSourceRoot=$storage"
```

`StorageSourceRoot` 必须是完整新仓的绝对路径；路径无效时清晰失败，不回退到包，也不会因为发现兄弟仓而自动切换。
切回包模式先执行 `dotnet restore DurableGraph.slnx -p:UseStorageSources=false`，再普通 build/test。
两种模式分别检查 restore assets 与实际程序集来源，不能把另一模式的旧 assets 当作验证。
`dotnet pack -p:UseStorageSources=true` 会在生成包前拒绝执行。

## 使用本地开发包

需要联调实验包时，先在存储仓按 `eng/Pack.ps1` 规则，从明确的干净 commit 生成唯一 S_dev：

```powershell
$storage = 'E:/repos/Atelia-org/atelia-storage'
$storageDevVersion = "0.1.1-dev.$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss')).g$([Guid]::NewGuid().ToString('N').Substring(0,8))"
& "$storage/eng/Pack.ps1" -Version $storageDevVersion -OutputDirectory "$storage/artifacts/dev-feed"
```

在本消费仓忽略的 `.artifacts/storage-dev/NuGet.Config` 保存以下配置，并按实际位置替换绝对 feed 路径。
五个精确包名只映射到开发 feed，其余依赖映射到 nuget.org；保留根配置用于正常公开包消费。

```xml
<configuration>
  <packageSources>
    <clear />
    <add key="storage-dev" value="E:/repos/Atelia-org/atelia-storage/artifacts/dev-feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <clear />
    <packageSource key="storage-dev">
      <package pattern="Atelia.Primitives" />
      <package pattern="Atelia.Data" />
      <package pattern="Atelia.Rbf" />
      <package pattern="Atelia.RbfSegmentStore" />
      <package pattern="Atelia.EventJournal" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
```

在同一 PowerShell 会话、消费仓根目录中运行：

显式传入 `StoragePackageVersion` 仍统一覆盖五包版本，支持已有同版本开发 feed。
只升级个别开发包时，可分别传 `StorageRbfSegmentStorePackageVersion` / `StorageEventJournalPackageVersion`；
具体包 override 优先于统一 override。公开来源属性不随开发版本自动变化，开发包必须另保留其 manifest 来源证据。

```powershell
dotnet restore DurableGraph.slnx --configfile .artifacts/storage-dev/NuGet.Config -p:UseStorageSources=false "-p:StoragePackageVersion=$storageDevVersion"
dotnet build DurableGraph.slnx -c Release --no-restore -p:UseStorageSources=false "-p:StoragePackageVersion=$storageDevVersion"
dotnet test DurableGraph.slnx -c Release --no-build --no-restore -p:UseStorageSources=false "-p:StoragePackageVersion=$storageDevVersion"
```

需要产出 DG 实验包时，同样保持 `UseStorageSources=false`，先按上述配置完成 restore，再以 `--no-restore`、
`StoragePackageVersion=S_dev` 和独立的 `PackageVersion=G` 打包。不能给五库存储依赖套用 G。
不同内容始终使用新版本，不覆盖公开版本或已有开发包，不清空用户全局缓存。
结束实验后用根配置重新 `dotnet restore DurableGraph.slnx`，再普通 build/test，恢复 props 中的公开版本。

## Agent 查阅与验证边界

先按 pin 中该包对应的来源属性定位上游 commit：EventJournal 看 `StorageEventJournalSourceRevision`，
RbfSegmentStore 看 `StorageRbfSegmentStoreSourceRevision`，基础三包看 `StorageSourceRevision`。
阅读准确版本的 `README.md`、`src/EventJournal/README.md`、`src/RbfSegmentStore/README.md` 和 `docs/Rbf/`，
不要用兄弟仓当前 HEAD 或另一个包的 commit 解释固定包。
README/XML 文档和 Source Link 帮助查阅，但不会自动把指南加入 Agent 上下文。

包交付变化先运行 [EventHistory](../experiments/PackageConsumerProbe/Run-EventHistoryProbe.ps1) 与
[recovery](../experiments/PackageConsumerProbe/Run-EventHistoryRecoveryProbe.ps1) 包回归，
用独立缓存核对 nuspec、restore assets 和实际加载 DLL；源码测试不替代包消费证据。
probe 的任务 NuGet 配置只使用显式完整 feed，不更改用户配置。

开发包排障以那次上游 Pack 的 manifest.sourceRevision 为源码身份；日常公开版本仍以本仓 pin 为准。

## 历史例外

- `Run-StorageExtractionProbe.ps1` / `StorageExtractionConsumer` 是冻结旧 DG、只更换五个同版本存储包的历史迁移见证，
  其显式 `StorageVersion` 保持原来的统一版本语义，不用于验收当前选择性发布的混合 pin。
- `Run-OrganizationMigrationProbe.ps1` 的旧 lane 针对 DB-071 的 StateStore → Persistence 命名迁移，
  `Run-DurableBaseMigrationProbe.ps1` 针对旧接口迁移。它们继续使用调用者提供的旧包。
- `docs/research/read-cache-probe/Run-Probe.ps1` 依赖旧 `DurableGraph.StateStore.Storage/Serialization` 源码形状，
  属于历史实验；复现应取对应历史 revision，不恢复旧源码或把它当活动回归。
- 历史设计中的旧 `../atelia` 路径保留当时含义。当前生产项目与公开包下载入口不再依赖那些路径。

原有 append-only、严格打开、显式离线救援、lease 和资源生命周期规则保持，见 [AGENTS.md](../AGENTS.md)。
