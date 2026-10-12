# DB-085 只读 Normal 成本实验

本入口服务[联合方案 §10.1](../../docs/design-branches/0085-joint-crash-recovery-design.md#101-第-2-片的建议实施边界与验收)。
基线为公开 `Repository.OpenReadOnlyExisting`，对照为内部 `Repository.OpenReadOnlyCandidate`。
候选只读入口和 RBF metrics 的依赖必须先由对应施工片接通；这里不创建产品 CLI 或新的公开 Open API。

2026-09-29 的修复后观察见 [CSV](observations-20260929.csv) 与[施工记录](../../docs/design-branches/0085-normal-readonly-experiment.md#最终成本结果)。CSV 的 90 行分别为 15 个夹具单元 × 两模式 × 三入口，各指标独立取三个记录样本的中位数；阶段中位数之和不必等于合计中位数。原始记录为施工记录所列 JSONL，时间不是回归阈值。
本实验显式使用兄弟 `atelia-storage` checkout 的源码模式，不是公开包交付验收；当前公开 package pin 不含本片接缝。
普通产品构建继续遵循[存储依赖指南](../../docs/storage-dependency.md)。
命令显式设置 `ShouldUnsetParentConfigurationAndPlatform=false`：solution 构建的默认行为可能清除外部
storage ProjectReference 的父级 Configuration，使 `-c Release` 的 DG 搭配 Debug storage；
该实验属性保持整条源码依赖链的 Release 配置，不修改项目常规配置。

## 运行

从仓库根目录运行常规确定性见证：

```powershell
$storageSourceRoot = (Resolve-Path ../atelia-storage).Path
$fixtureTemp = Join-Path $env:LOCALAPPDATA 'Temp/db085-normal-20260929'
New-Item -ItemType Directory -Path $fixtureTemp -Force | Out-Null
$previousTemp, $previousTmp = $env:TEMP, $env:TMP
try {
    $env:TEMP = $env:TMP = $fixtureTemp
    dotnet build DurableGraph.slnx -c Release -m:1 -nr:false -p:ShouldUnsetParentConfigurationAndPlatform=false -p:UseStorageSources=true "-p:StorageSourceRoot=$storageSourceRoot"
    dotnet test tests/DurableGraph.Persistence.Tests/DurableGraph.Persistence.Tests.csproj -c Release -m:1 -nr:false -p:ShouldUnsetParentConfigurationAndPlatform=false -p:UseStorageSources=true "-p:StorageSourceRoot=$storageSourceRoot" --no-build --no-restore --filter FullyQualifiedName~DB085ReadOnlyCostMeasurements
}
finally { $env:TEMP, $env:TMP = $previousTemp, $previousTmp }
```

重矩阵 opt-in。默认 runner 验证兄弟源码路径，用 Release、串行 MSBuild、禁用 node reuse 构建一次，
然后每个轴启动一个独立 test host；每个 host 内有一次弃用 warmup、三次记录样本，并交替基线/候选顺序：

```powershell
./experiments/ReadOnlyNormalCostProbe/Run-Probe.ps1
```

已完成构建时可运行一轴或一个规模：

```powershell
./experiments/ReadOnlyNormalCostProbe/Run-Probe.ps1 -NoBuild -Axis history -Scale 1024
./experiments/ReadOnlyNormalCostProbe/Run-Probe.ps1 -NoBuild -Axis ancestors
```

也可直接启动测试进程，输出必须是绝对路径：

```powershell
$storageSourceRoot = (Resolve-Path ../atelia-storage).Path
$fixtureTemp = Join-Path $env:LOCALAPPDATA 'Temp/db085-normal-20260929'
New-Item -ItemType Directory -Path $fixtureTemp -Force | Out-Null
$previousTemp, $previousTmp = $env:TEMP, $env:TMP
try {
    $env:TEMP = $env:TMP = $fixtureTemp
    $env:DURABLEGRAPH_DB085_MEASURE = '1'
    $env:DURABLEGRAPH_DB085_MEASURE_OUTPUT = Join-Path $fixtureTemp 'db085.jsonl'
    $env:DURABLEGRAPH_DB085_MEASURE_AXIS = 'history'
    $env:DURABLEGRAPH_DB085_MEASURE_SCALE = '256'
    dotnet test tests/DurableGraph.Persistence.Tests/DurableGraph.Persistence.Tests.csproj -c Release -m:1 -nr:false -p:ShouldUnsetParentConfigurationAndPlatform=false -p:UseStorageSources=true "-p:StorageSourceRoot=$storageSourceRoot" --no-build --no-restore --filter FullyQualifiedName=Atelia.DurableGraph.Persistence.Tests.DB085ReadOnlyCostMeasurements.Measure_read_only_open_query_and_first_graph_read
}
finally {
    $env:TEMP, $env:TMP = $previousTemp, $previousTmp
    Remove-Item Env:DURABLEGRAPH_DB085_MEASURE, Env:DURABLEGRAPH_DB085_MEASURE_OUTPUT, Env:DURABLEGRAPH_DB085_MEASURE_AXIS, Env:DURABLEGRAPH_DB085_MEASURE_SCALE -ErrorAction SilentlyContinue
}
```

runner 默认使用 C: 用户 LocalAppData 下 `Temp/db085-normal-20260929` 作为 TEMP/TMP，
可用 `-FixtureTempDirectory` 覆盖；用 `-StorageSourceRoot` 指定其他源码 checkout。退出时恢复其修改的环境变量。
输出默认位于本实验被忽略的 `obj/`，是每轴独立的 JSONL，保留配置、fixture 固定项和每次样本；指定的同名文件会覆盖。
脚本不会清理实验输出。临时数据库只由受路径约束的测试 fixture 删除器清理。

## 独立变量与固定图

模型复用 `SharedReadModel`，State 与 Event 都是三个节点的环：`Next == Alias`，无 Text/Links。
State 最终值为 11/12/13，Event 为 21/22/23。每个读取验证完整值、环、别名；Checkpoint 同时验证两个独立图。

| 轴 | 默认规模 | 唯一增加的工作 |
|---|---|---|
| 无关历史 `history` | 0/256/1024；小型常规见证另含 16 | 不发布 ref 的合法独立根 State/Journal orphan；目标分别在它们之前和之后 |
| 所需祖先 `ancestors` | 0/4/32 | 合法 Journal State 链、Revision map Delta 和三个对象 Delta 链；最终目标逻辑图固定 |
| Schema `schema` | 0/16/256 | `schemas.rbf` 中目标不引用的 Schema/表示注册，State/Journal 图不变 |
| catalog `catalog` | 0/16/256 | 指向同一最终 State 的额外不可变 tag；Schema/State/Journal 图不变 |

所有轴只在写完图后发布一个 `main` branch 和两个固定目标 tag；无关历史与祖先不增加 ref-op 数量。
增加历史会移动目标地址，catalog 中地址字节可能变化。验证固定 Schema 哈希、catalog 条目/文件数量与字节长度，
不要求移动前后的 catalog 内容相等。fixture 输出记录目标地址、实际 frame 数和对象链深度。
每个 fixture 先经独立完整 Audit Healthy 校验，并验证 Audit/所有只读样本前后文件名称及 SHA-256 不变。
Audit 不在测量 interval 内，也不进入候选 Open。

## 计量边界

每个操作从 fresh resources 开始，分别测：

- `Open`。
- `Query`：Event/Checkpoint 使用 `GetHead("main")`，State 使用 `ResolveTag("target-state")`。
- `FirstRead`：`ReadEvent`、`ReadState` 或 `ReadCheckpoint`。
- `Total`：上述 API 阶段成本之和；额外 `ChainWallMilliseconds` 包含测量快照与阶段 heap 采样间隙。

核心链中没有强制 GC，断言、JSON、Dispose 均不在阶段计时内。每阶段记录 Stopwatch 时间、
`GC.GetAllocatedBytesForCurrentThread` 分配及 RBF metrics 差分：逻辑读取 calls/requested/returned、
raw calls/requested/returned、`ReadAheadReturnedBytes`、header calls。线程分配不含其他线程/native memory。
RBF header 字节包含在读取量中；raw 返回区间落在逻辑请求区间外的字节记作 read-ahead。
这些是软件层请求/返回字节，不是设备物理读取量。

FileStream metadata（包括 segment/catalog locator）读取字节尚未接通，输出 `MetadataFileStreamBytes=null` 和说明。
其 API 时间/分配仍包含在测量中。设备物理读取量与精确操作峰值也为 `null`。

强制 GC 后的 Open 驻留、图结果保留和结果释放后驻留使用**另一次 fresh open 的诊断**，
不会把 GC 插进核心计时链。它们是 managed heap 差值，含模型/runtime 共享驻留，不能当成缓存独占字节。
核心阶段末尾 `GC.GetTotalMemory(false)` 是 occupied heap 抽样，可能错过瞬时峰值；
`Process.PeakWorkingSet64` 是整个 test host 生命周期累计峰值，含 JIT 与前面场景，不是本次 API 峰值。

每个操作 fresh open 只清掉其资源/Store 缓存。同一 test host 的 process/JIT 已预热；
独立进程只消除进程内缓存。生成 fixture 和预先 Audit 也会触碰 OS 缓存，OS page cache 未控制。
Checkout 不属于这个只读实验。

常规见证核验确定性工作量：256→1024 无关帧增长允许至多 8 次额外逻辑请求与 4096 请求字节，
覆盖有限入口/地址编码差异；raw 返回量不要求相等，因为对齐及预取会改变返回量。
这些固定界限是内部原型的局部回归条件，应结合每阶段数据复核；不设耗时阈值。
