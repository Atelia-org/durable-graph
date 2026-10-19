# atelia-storage 依赖工作流

[eng/StorageDependency.props](../eng/StorageDependency.props) 是存储版本、准确来源 commit 和仓地址的唯一位置。
RBF3 栈使用同版本的六包：`Atelia.Primitives`、`Atelia.Data`、`Atelia.Rbf`、`Atelia.Binary`、
`Atelia.FrameStore`、`Atelia.HeadStore`。它们的版本 S 与 DurableGraph 四包的版本 G 独立。
活动依赖中不再包含 RbfSegmentStore 或 EventJournal；不支持旧存储数据的读取或迁移。

当前 pin 为 `0.1.3-dev.20261019.gf8868b5`，来源为 `f8868b57e8f012745413bb38a83d67f1dba29f26`。
这是为准确来源保留的唯一候选版本，已由准确干净源码生成本地六包 feed，尚未公开发布。新 pin 的源码与活动普通包资格见 [DB-096 验收](design-branches/0096-history-and-head-model/package-cutover-validation.md)，后续接线状态见[工作集](design-branches/0096-history-and-head-model/PROJECT-STATE.md)；独立上游 Probe 不替代 DG 产品验收。
2026-10-19 的 [Private 删除切片](design-branches/0096-history-and-head-model/private-removal.md)使用旧 `731eef7` / `0.1.3-dev.20261017.g731eef7` feed；其普通包结果保留原版本意义，不证明新 pin 的包资格。
本机可通过环境变量 `UseStorageSources=true` 与 `StorageSourceRoot` 联调；也可显式使用下方命令。
默认仍为 package mode，不探测兄弟仓、不回退旧包、不在配置中保存本机路径。

本轮依赖包含非兼容 API 与格式变化：上游统一 HeadStore 格式 4 拒绝格式 1/2/3；`HeadId.LocalId` 改为 owner 上的 `GetLocalHeadId`，持久 handle 编码为 4B/12B。Fork 只写固定来源 header，未产生本地 Entry 时承接该固定 Entry 的值，不随父 Head 后续发布推进。
DG 的 DB-096 隔离分支已使用 `P0R3` 的 D 目录入口、`P0T2` 的 T 书签入口及 `P0G2` 图封套；业务来源由 HS 历史选择，业务 Parent 已删除，公开面采用 Branch/Commit/WorkingCopy。S 仍为 `P0S1`；新旧布局不兼容，不自动迁移。真实接线与剩余合入门槛见 DB-096 工作集。
DG 书签定位准确 CommitId，由独立 Dictionary 管理图目录发布；旧三入口接入的历史范围见 [DB-096 接入切片](design-branches/0096-history-and-head-model/headstore-adaptation.md)。
旧 `3de8fb0` 的包验收结果保留历史身份，不能作为本轮证据。

## 显式源码联调

在 DurableGraph 仓根执行；Windows 可将 `$storage` 换成 `E:/repos/Atelia-org/atelia-storage`：

```powershell
$storage = '/repos/Atelia-org/atelia-storage'
dotnet restore DurableGraph.slnx --disable-parallel -p:UseStorageSources=true "-p:StorageSourceRoot=$storage"
dotnet build DurableGraph.slnx -c Release --no-restore -m:1 -nr:false -p:UseStorageSources=true "-p:StorageSourceRoot=$storage"
dotnet test DurableGraph.slnx -c Release --no-build --no-restore -m:1 -nr:false -p:UseStorageSources=true "-p:StorageSourceRoot=$storage"
```

`StorageSourceRoot` 必须是绝对路径，包含 Binary/FrameStore/HeadStore 三个项目，且 Git HEAD 等于 pin 的完整来源。
同一 HEAD 上的源码编辑可用于联调；它们不构成可发布包的来源凭据。无效路径、缺项目或不符 revision 都清晰失败，不回退到包。
`UseStorageSources=true` 仅用于 build/test；`dotnet pack -p:UseStorageSources=true` 在生成包前拒绝执行。

切换到 package mode 必须重新 restore；不要以 source mode 的旧 assets 或 DLL 当作包消费证据。
Generator 仍是 `netstandard2.0` Roslyn 组件，它生成对 Bare reader/writer 的直接调用；Runtime 通过 Serialization 取得 Binary 依赖。
Storage 依赖 FrameStore，Persistence 依赖 HeadStore；必要的 Data/Primitives/Rbf 位于同一个新栈闭包。

## 准备准确来源的六包

在干净、已提交且 HEAD 与 pin 一致的存储仓，使用其已有入口：

```powershell
$storage = '/repos/Atelia-org/atelia-storage'
$storageVersion = '0.1.3-dev.20261019.gf8868b5'
$storageFeed = Join-Path $storage "artifacts/feeds/$storageVersion"
& "$storage/eng/Pack.ps1" -Version $storageVersion -OutputDirectory $storageFeed
```

Pack 使用存储仓固定 SDK，产出六个 nupkg、六个 snupkg 和 `manifest.<S>.json`，记录准确来源与字节 SHA256。
相同版本只允许复用已核验的完整产物；不同内容使用新版本，不覆盖公开包或已有候选，不清空全局缓存。
当前 pin 的本机 feed 位于 `.artifacts/db096-package-cutover/storage-feed`，含六包、六份 symbols 与 `manifest.0.1.3-dev.20261019.gf8868b5.json`；包文件不随 Git 分发，其他机器按上述入口重建。`.artifacts/db096-private-removal/storage-feed` 只含旧 `731eef7` 候选，不能用于当前 pin。

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
当前最短示例已改为纯 State，另有可选 Event 路径；范围与新候选结果见[纯 State 验收记录](DurableGraph-lab-notebook.md#state-only-quickstart)。
下述数字与目录保留首次交付的历史含义。
2026-10-15，以上入口已从干净提交 `3e1f41164d319fd1607ebdd2d2b572cc0089ba57` 生成并验收
G `0.0.0-candidate.20261015.g3e1f411`；S 为当时的 `0.1.3-dev.20261014.g3de8fb0`，不是本文当前 pin。Windows / SDK 10.0.201 的结果为：

- 根 Release Rebuild：0 warnings / 0 errors；本轮未修改产品 C#、公共 API 或持久格式。
- 已有输出目录不被覆盖；缺失 Storage feed 失败后恢复调用者缓存环境且不产生消费者目录；G/S 同版提前拒绝。
- 搬迁到仓外带空格目录，祖先源故意不可用：旧命令两项直接引用准确报 NU1100；新命令直接运行，HP99→98。
- 新 cache 内实际解析十一包、没有 ProjectReference，仅搬迁后的本地包源；示例、配置和版本与 README 同源。
- V2 升级得到 HP97 / Day1；只读浏览保持数据库字节；策略后 HP96，named Fork 冷重开为 96/95/94；四份 history 通过 Clean/Verify，原三份 V1 内容不变。
- 独立只读复核确认十七个交付输入不变，十一份缓存原包、十一份运行时输出 DLL 和 Runtime 包中的 SG/Build/props/targets 与候选一致；六个 S 包与上游原 manifest 摘要一致。

当时的交付目录为 `.artifacts/candidates/0.0.0-candidate.20261015.g3e1f411/`，
未在其中运行示例或写入 bin/obj/history/数据库。历史复现须使用上述来源提交的 README 和脚本；
当前 runner 要求候选源码与当前 README 一致，应按前节重新导出新候选。

原始工件保留在 `.artifacts/candidate-delivery/20261015/`：`prepare.log`、`root-build.log`、
`guard-results.json`、`probe-result.json`、`probe-logs/` 和 `summary.json`。
它们是本地忽略工件；长期回归由脚本和 README 原文重跑。包准备内部工件另在
`.artifacts/candidate-preparation/`，不是对外交付目录。
本次仅验收附带示例与上述既有 API 故事，没有进行公开发布、OS 禁网、任意 RID 或完整签名信任链验证。

## 用任务 NuGet.Config 消费候选包

在本仓忽略目录 `.artifacts/storage-dev/NuGet.Config` 创建如下配置，按实际位置修改绝对 feed 路径。
六个精确包名仅映射到该开发 feed，其余依赖映射到 nuget.org；不修改根配置或用户配置。

```xml
<configuration>
  <packageSources>
    <clear />
    <add key="storage-dev" value="/repos/Atelia-org/atelia-storage/artifacts/feeds/0.1.3-dev.20261019.gf8868b5" />
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
      <package pattern="Atelia.HeadStore" />
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
先读上游 `src/Binary/README.md`、`src/FrameStore/README.md`、`src/HeadStore/README.md` 与 RBF3 文档。
裸地址/帧 CRC、共享数据确认与Head/HeadSetTag 发布由新底座提供；DG 历史书签目录通过 Head 发布；DG 保留必要内容、因果、publication/fault 和缓存寿命检查。
正常 backing-store 尾恢复、只读与显式离线救援的边界见 [AGENTS.md](../AGENTS.md#persistent-data-discipline)；
不重新建立全库重开确认、启动审计或严格 writable 预检。
