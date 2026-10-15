# atelia-storage 依赖工作流

[eng/StorageDependency.props](../eng/StorageDependency.props) 是存储版本、准确来源 commit 和仓地址的唯一位置。
RBF3 栈使用同版本的六包：`Atelia.Primitives`、`Atelia.Data`、`Atelia.Rbf`、`Atelia.Binary`、
`Atelia.FrameStore`、`Atelia.VersionStore`。它们的版本 S 与 DurableGraph 四包的版本 G 独立。
活动依赖中不再包含 RbfSegmentStore 或 EventJournal；不支持旧存储数据的读取或迁移。

当前 pin 为 `0.1.3-dev.20261014.g3de8fb0`，来源为
`3de8fb050d81d35ee1c36d1b978024cbc42cb455`（上游维护更新，API 与 wire format 不变）。**这是未公开发布的本地候选**，不能假定 nuget.org 已有该版本。
当前源码联调采用下面的显式 source mode；包验收使用准确来源的完整本地 feed 和任务 NuGet.Config。
默认仍为 package mode，不探测兄弟仓、不回退旧包、不在仓库配置中保存本机路径。

本次 pin 已通过上游四个隔离包消费者，以及 DG 独立缓存下的包模式 Release Rebuild（0 warnings/errors）和根 solution 的 2,690 项测试。
六包缓存与当前 solution 的 19 份输出 DLL 均与候选 feed 字节一致；验证工件保留在 `.artifacts/storage-pin-upgrade/20261014-3de8fb0/`。

## 显式源码联调

在 DurableGraph 仓根执行；Windows 可将 `$storage` 换成 `E:/repos/Atelia-org/atelia-storage`：

```powershell
$storage = '/repos/Atelia-org/atelia-storage'
dotnet restore DurableGraph.slnx --disable-parallel -p:UseStorageSources=true "-p:StorageSourceRoot=$storage"
dotnet build DurableGraph.slnx -c Release --no-restore -m:1 -nr:false -p:UseStorageSources=true "-p:StorageSourceRoot=$storage"
dotnet test DurableGraph.slnx -c Release --no-build --no-restore -m:1 -nr:false -p:UseStorageSources=true "-p:StorageSourceRoot=$storage"
```

`StorageSourceRoot` 必须是绝对路径，包含 Binary/FrameStore/VersionStore 三个项目，且 Git HEAD 等于 pin 的完整来源。
同一 HEAD 上的源码编辑可用于联调；它们不构成可发布包的来源凭据。无效路径、缺项目或不符 revision 都清晰失败，不回退到包。
`UseStorageSources=true` 仅用于 build/test；`dotnet pack -p:UseStorageSources=true` 在生成包前拒绝执行。

切换到 package mode 必须重新 restore；不要以 source mode 的旧 assets 或 DLL 当作包消费证据。
Generator 仍是 `netstandard2.0` Roslyn 组件，它生成对 Bare reader/writer 的直接调用；Runtime 通过 Serialization 取得 Binary 依赖。
Storage 依赖 FrameStore，Persistence 依赖 VersionStore；必要的 Data/Primitives/Rbf 位于同一个新栈闭包。

## 准备准确来源的六包

在干净、已提交且 HEAD 与 pin 一致的存储仓，使用其已有入口：

```powershell
$storage = '/repos/Atelia-org/atelia-storage'
$storageVersion = '0.1.3-dev.20261014.g3de8fb0'
$storageFeed = Join-Path $storage "artifacts/feeds/$storageVersion"
& "$storage/eng/Pack.ps1" -Version $storageVersion -OutputDirectory $storageFeed
```

Pack 使用存储仓固定 SDK，产出六个 nupkg、六个 snupkg 和 `manifest.<S>.json`，记录准确来源与字节 SHA256。
相同版本只允许复用已核验的完整产物；不同内容使用新版本，不覆盖公开包或已有候选，不清空全局缓存。
上述候选已经在该来源上生成；它是本地验收输入，未进行远程发布。换 feed 路径不会改变包内容或来源。

[eng/Prepare-Storage.ps1](../eng/Prepare-Storage.ps1) 可以只读验证这个完整 feed：

```powershell
$selected = ./eng/Prepare-Storage.ps1 -StoragePackageSource $storageFeed
```

它核对 pin、上游 manifest、六个 nuspec 的身份/仓地址/commit 和六个字节 hash，再返回
`PackageSource`、`Version`、`Revision`、`Packages`；不 clone、构建、重新打包或修改源 feed。
没有 `-StoragePackageSource` 时，它从 nuget.org 下载六个 pin 包，完成后写入 `manifest.published.json`。
当前候选尚未公开时下载会明确失败，并提示源码联调或本地 feed 入口。
公开下载仍核对签名条目存在性；该检查不等于完整签名信任链验证。

<a id="candidate-delivery"></a>
## 对外交付一个候选目录

应用接入优先使用 [Prepare-DurableGraphCandidate.ps1](../eng/Prepare-DurableGraphCandidate.ps1) 导出的目录。
它复用下文十包准备流程，附上当前示例还需要的原始 `K4os.Compression.LZ4 1.3.8`，
将包、完整消费配置、固定版本的 QuickStart 和运行步骤放在一起；消费者不需要寻找生产者的临时配置。
以下命令中的 `$storageFeed` 是上一节核验过的准确六包 feed：

```powershell
$version = "0.0.0-local.$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))"
$candidate = Join-Path (Get-Location) ".artifacts/candidates/$version"
./eng/Prepare-DurableGraphCandidate.ps1 -Version $version -OutputDirectory $candidate -StoragePackageSource $storageFeed
```

输出目录必须是新目录。准备过程可以联网，S 的来源仍由 `StorageDependency.props` 和已有存储校验负责；
G 只输入一次并写入生成项目的一个版本属性，由 Runtime 与 Persistence 两个顶层 PackageReference 共用。
S 通过包的传递依赖取得，不增加一份消费者手写 pin，也不修改上游版本或自动回退到公开旧版。

交付目录包含 `README.md`、`NuGet.Config`、`feed/`、`QuickStart/` 和 `packages.sha256`。
配置仅使用相对 `feed`，清除继承的 sources、mapping 与 fallback folders；整个目录可以一起搬走。
QuickStart 的项目、源码、配置和最短命令来自[根 README](../README.md#最短接入路径)，
交付 README 另记录本批 G/S、来源与先决条件。消费步骤以该 README 为准，不依赖源仓路径。
`packages.sha256` 用于核对本批包复制完整性，不负责依赖选择，也不替代来源或签名信任验证。

本地包闭包的范围是：已安装 **.NET SDK 10.0.201** 后，附带的 `net10.0`、framework-dependent、
没有额外 RID 的 QuickStart 可从交付本地源取得全部 nupkg。当前为四个 G 包、六个 S 包和一个 K4os 包。
这不承诺 SDK 安装、任意应用依赖、额外 RID/self-contained publish 或根 solution 的离线构建；
也不声称已对所有网络行为执行禁网验收。将来依赖变化时按实际示例重新验证，不能把十一包当通用闭包公式。

Atelia nuspec 中的版本是最低范围，不强制任意应用只能解析到该版本。
本次交付 feed 每个 ID 只含一个选定版本，验收再检查 assets 和实际程序集；
消费者另引更新依赖的组合需要自己的验收。公开 preview 可作为后续分发渠道，不是本地候选接入的前置条件。

<a id="candidate-delivery-validation"></a>
### 候选交付验收

[原文接入 runner](../experiments/PackageConsumerProbe/Run-ReadmeQuickStartProbe.ps1) 需要 PowerShell 7.3 或更新版本，接收 `-CandidateDirectory`，
把完整目录搬到仓外新路径，在错误的祖先 NuGet 配置和全新缓存下直接执行 README 的 dotnet 命令。
它同时保留旧 `--source` 命令的准确 `NU1100` 负例、HP99→98 冷进程正控，以及已有 Upgrade/Fork/Verify 回归。
正式验收结果将在本轮脚本运行完成后补入此处；当前这段只规定门禁，不宣称尚未运行的结果。

## 用任务 NuGet.Config 消费候选包

在本仓忽略目录 `.artifacts/storage-dev/NuGet.Config` 创建如下配置，按实际位置修改绝对 feed 路径。
六个精确包名仅映射到该开发 feed，其余依赖映射到 nuget.org；不修改根配置或用户配置。

```xml
<configuration>
  <packageSources>
    <clear />
    <add key="storage-dev" value="/repos/Atelia-org/atelia-storage/artifacts/feeds/0.1.3-dev.20261014.g3de8fb0" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <clear />
    <packageSource key="storage-dev">
      <package pattern="Atelia.Primitives" />
      <package pattern="Atelia.Data" />
      <package pattern="Atelia.Rbf" />
      <package pattern="Atelia.Binary" />
      <package pattern="Atelia.FrameStore" />
      <package pattern="Atelia.VersionStore" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
```

在 DurableGraph 仓根运行：

```powershell
dotnet restore DurableGraph.slnx --disable-parallel --configfile .artifacts/storage-dev/NuGet.Config -p:UseStorageSources=false
dotnet build DurableGraph.slnx -c Release --no-restore -t:Rebuild -m:1 -nr:false -p:UseStorageSources=false
dotnet test DurableGraph.slnx -c Release --no-build --no-restore -m:1 -nr:false -p:UseStorageSources=false
```

覆盖 `StoragePackageVersion` 会统一选择六包；不再有旧 SegmentStore/Journal 的独立覆盖属性。
开发版本必须保存对应 Pack manifest；覆盖版本不会自动改变源码身份。
包 restore 要使用完整任务配置：仅传 `RestoreSources` 不能绕过继承的 package source mapping。
严谨包验收另指定任务独立的 `RestorePackagesPath`，核对 restore assets、nuspec 和实际加载程序集；源码测试不能替代这一步。
刷新 pin 后首次包构建使用 `Rebuild`：本地包归一化时间戳后，新旧 DLL 可能长度和时间戳相同，
增量复制会保留旧输出。仅检查 assets 的版本不足以确认实际消费，验收还须核对输出 DLL 与所选包的字节。

## DurableGraph 十包 feed 与独立 probes

四个 DG 包是 Runtime、Persistence、Storage、Serialization；包模式下分别 pack，G 必须与 S 不同。
在上述 package restore 后使用 `--no-restore -p:UseStorageSources=false -p:PackageVersion=<G>`，
不要把 G 套到六个存储依赖，不要借 source ProjectReference 生成正式依赖闭包。

[PackageProbeSupport.ps1](../experiments/PackageConsumerProbe/PackageProbeSupport.ps1) 可验证并复制六个存储包，
再生成四个 DG 包，构成完整的十包 feed。当前候选显式传入源 feed：

```powershell
$ErrorActionPreference = 'Stop'
$version = "0.0.0-local.$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))"
$feed = Join-Path (Get-Location) ".artifacts/nuget/$version"
. ./experiments/PackageConsumerProbe/PackageProbeSupport.ps1
Prepare-DurableGraphProbeFeed -RepositoryRoot (Get-Location).Path -OutputDirectory $feed -Version $version -StoragePackageSource $storageFeed
```

runner 也可从任务环境变量 `DG_STORAGE_PACKAGE_SOURCE` 读取路径后显式传给 Prepare；路径不进入持久 pin。
已有完整十包 feed 的 runner 使用 `-PackageSource <feed> -Version <G>`，不改写输入。
这是可联网的领域 Probe 输入；其完整任务配置允许取得第三方依赖。
它与上面的便携候选目录用途不同：十包 feed 不自带 QuickStart 的本地依赖闭包，
内部 `pack.nuget.config` 也不作为对外交付的消费配置。
活动验收只使用新建库，保留在新格式库内的模型 V1→V2 升级、branch/tag 与冷进程重开行为。
旧存储提取、旧组织/API 包兼容 lanes 不再作为当前回归；历史证据仍可在对应 Git revision 中复现。
相关命令见[包实验导航](../experiments/PackageConsumerProbe/README.md)和[根 README](../README.md#从源码打包)。

## 查阅边界

以 `StorageSourceRevision` 对应的准确源码、README/XML 与本次候选 manifest 为依据，不以无关兄弟仓 HEAD 解释 pin。
先读上游 `src/Binary/README.md`、`src/FrameStore/README.md`、`src/VersionStore/README.md` 与 RBF3 文档。
裸地址/帧 CRC、共享数据确认与根/tag 发布由新底座提供；DG 保留必要内容、因果、publication/fault 和缓存寿命检查。
正常 backing-store 尾恢复、只读与显式离线救援的边界见 [AGENTS.md](../AGENTS.md#persistent-data-discipline)；
不重新建立全库重开确认、启动审计或严格 writable 预检。
