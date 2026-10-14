# DB-069：热保存基线的增量重建计量

> 整理说明：2026-10-14 从 `docs/design-branches/0069-incremental-save-baseline.md` 搬入问题入口；正文来源 Git `8ef9fca`。
> 适用性：受控保存基线 head/H 增量推进的历史验收与测量；精确 payload、发布及冷读保持于当片范围，不声明整个 Commit 与历史长度无关。
> 本文原过程与测试数字保留当时基线；当前能力与焦点见 [PROJECT-STATE](../../../src/PROJECT-STATE.md)。

> 状态：已实施并验收。2026-09-12。用户已授权具体设计、分工实施与验证；本片不调整策略默认值。
> 前置：[DB-067](../0067-owned-revision-read-cache-design/README.md) 的 append-only Store、owned frame/map 缓存及测量设施。

## 1. 问题与验收边界

连续细粒度 Event/State 保存通常远多于冷恢复。当前 LoadedRevisionPlanner 为全部 source 对象重读链验证来源，
ObjectRevisionPlanner 又为 Update/NoChange 重读链求 H；raw 缓存命中仍会逐项遍历 prior。
本片将已经验证的对象 head 与累计重建 payload 随已提交 DTO 基线保留，冷恢复时取得、热提交时增量推进。

最小判据：建立可编辑基线后，普通 State 与 Event snapshot 的准备不再调用旧对象链读取；
增量 head/H 与独立持久读链一致；Base/Delta 选择、冷恢复、升级、删除、Event/State 基线和失败发布语义保持。
分别记录对象链调用/项数与 map 回溯帧数，不将 raw cache hit 当成没有遍历，不承诺整个 Commit 已与历史长度无关。

## 2. 选择与范围

- 每个已提交对象仅保留实际 head 和 H；复用 NormalizedObject 已有 SourceLayout/RequiresRewrite/Model，
  不保留完整历史记录或 body 链。额外基线元数据规模与当前 source 对象数有关。
- Decoder 完整验证并解码时带出 H；RevisionReadSession 的 (ObjectId, head) 缓存同时保留它。
  每视图 membership、引用、Dictionary lookup 与 current Normalize 仍独立执行。
- 受控 Loaded planner 使用基线计量；独立 raw ObjectRevisionPlanner/Captured planner 仍按原路径认证输入。
  exact Parent、完整 source membership、每个 source head、模型和升级义务的检查保留。
- 用户允许启发式计量简化；当前选择继续保持精确 H，因为实际文件范围下的 Delta payload 只需现有 varint 长度算术。
  在现有 ObjectVersionPayloadSize 中增加实际 FileScope 计量 helper，使用真实 writer/reader 测试对照。
  规划 D 仍为既有上界；累计 H 不反复累加上界。ObjectId、共享 Frame/map 等开销仍不计入 B/D/H。
- 不改 wire、Append/prior 验证、发布屏障、默认阈值、缓存预算或 map 物化算法；不优化全量 Base 准备。

## 3. 基线生命周期

冷 Load/Resume 从完整 decoded view 取得持久 head/H；synthetic DTO 测试视图可继续纯 Normalize，
但缺少存储来源不能进入受控热保存路径。
已验证来源只在同一 Store 生命周期、精确 Parent 及受控工作区中复用；沿用 append-only、fault/dispose/reentry 合同。
SchemaStore 的活目录复核不由历史来源缓存代替。

| 本次实际写入 | 下一版 head/H |
|---|---|
| Base（包括升级及 NoChange 的可选 Base） | 新地址；本次 Base 精确 payload |
| Delta | 新地址；旧 H + 实际文件范围下本次 Delta payload |
| NoChange 且无本地写入 | 原 head/H |
| Remove | 不进入下一版基线 |

Stage 只构造私有候选；Append 返回后、发布之前完成地址依赖计量和可失败的安装准备。
Install 仅在确认发布后接受候选与其元数据，无用户 callback 或新增分配。
丢弃、预发布失败和发布结果不确定均不提前更新活动基线，故障后依既有合同重开。
Event snapshot 使用前 State 的来源及 H，但不安装到 State；E1 与 S1 均以 S0 为父基线。
升级切断可达性的 source 行仍参与完整来源检查，下一次 State 安装才按候选 membership 收缩。

## 4. 分工与验证映射

| 要求 | 实现责任 | 验收 |
|---|---|---|
| 实际 scope payload 与独立遍历统计 | Storage 基础分片 | 同/跨文件及 varint 边界对照真实 wire；chain/map 计数分离 |
| 冷读带出与缓存复用 head/H | Decoder/ReadSession/DecodedRevision | 冷恢复与双视图缓存命中，纯 synthetic 视图不授予存储来源 |
| 受控 planner 与增量安装 | NormalizedRevision/Workspace/PreparedWorldSave/planners | 热 Stage 零旧链读取；独立重读 H/head 一致；布局、Parent、失败与 Event 隔离 |
| 测量与回归 | 复用 DB-067 测量风格、现有模型及真包脚本 | 修改前/后相同轨迹，时间/线程分配/chain/map 分开；cold checksum 与实际 bytes |
| 集成与独立审阅 | 主线程及独立 reviewer | solution build、Storage/StateStore/Runtime-Generator 回归、真实包消费者、diff/文档链接 |

先加入不改变行为的计数并运行修改前测量，再实施受控基线路径，最后重跑同一测量。
正式结果、具体测试与审阅结论见 §6；DB-067 的历史结果不回填改写。

## 5. 替代方案与后续

- 完整 ObjectVersionChain 常驻：保存规划只需要累计量和已验证来源，保留历史 body 增加不必要的内存与维护成本。
- 每次读回新 Frame 取得 H：可行，但增加 I/O/解码/复制及安装准备失败面；实际 scope 长度算术更小。
- body-only 或固定 overhead 启发式 H：用户允许，但当前精确公式足够简单，优先保持既有边界行为。
- ancestor map 拼接、增量 map seed、known-head 通用入口：本片先独立计量；如仍成为主要热保存成本，再以单独验证合同推进。

## 6. 实施与测量证据

### 6.1 实施落点

- [ObjectStorageInfo](../../../src/DurableGraph.Persistence/ObjectStorageInfo.cs) 是每对象的 head/H 值；
  [DecodedRevision](../../../src/DurableGraph.Persistence/DecodedRevision.cs) 和
  [NormalizedRevision](../../../src/DurableGraph.Persistence/NormalizedRevision.cs) 绑定来源 Store/SchemaStore，
  [ReadSession](../../../src/DurableGraph.Persistence/RevisionReadSession.cs) 的 owned DTO 缓存同时携带计量。
- [Loaded planner](../../../src/DurableGraph.Persistence/LoadedRevisionPlanner.cs) 完整核对 source head，
  调用 [Object planner](../../../src/DurableGraph.Persistence/ObjectRevisionPlanner.cs) 的受控基线入口；独立入口继续读链。
- [PrepareInstall](../../../src/DurableGraph.Persistence/PreparedWorldSave.cs) 在发布前复制候选行目录，
  仅替换本次写入行的不可变 metadata；旧基线和原候选均不修改。实际文件范围下的
  [Delta 计量](../../../src/DurableGraph.Storage/ObjectVersionPayloadSize.cs) 与 wire 对照。
- [遍历统计](../../../src/DurableGraph.Storage/StateRevisionTraversalStatistics.cs) 为内部累积快照：
  ObjectChainReads 计有效参数发起的调用，ObjectChainEntries 计实际加入的链项，MapReplayFrames 计 materializer 每次访问历史帧，
  包括 raw cache hit。完整 map hit 不计 replay，失败前已经进行的工作仍计数；不改变缓存预算与异常行为。

### 6.2 相同工作负载的前后测量

完整 24 个样本和源码 SHA256 见 [incremental-save-baseline-results.json](../../research/incremental-save-baseline-results.json)。
Baseline 是 `3f889ef84dea8c3bb3341d11d716f033671acb87` 的原保存路径加无行为改变的遍历计数；
先取得 12 个样本才修改核心实现。前后 [HotSaveMeasurementTests](../../../tests/DurableGraph.Persistence.Tests/HotSaveMeasurementTests.cs)
的源码 hash 相同，所有对应样本的 checksum、链长度、Base/累计 Delta/末次 Delta 的实际 payload 一致。

Windows，SDK 10.0.201/runtime 10.0.5，Release，关闭 tiered compilation。每格丢弃一轮，再保留三轮；
每轮有 4 次热保存。fixture 为根、4096 元素 int[]、两个稳定 Node 和共享 string，共 5 个对象；
先积累 8 或 64 次单元素修改，固定策略 `{1000000,5}` 确保长 Delta 链。
使用真实 WorldWorkspace → Stage → Storage.Append → PrepareInstall → Install；
**不含 DurableFlush 或 Journal 发布，不代表完整 Commit 的端到端加速。** OS 页缓存没有清空。
计时回合中不插入冷读，结束后使用新 Store 完整恢复并独立核对数组全部值、共享 string 和真实重建链。

下表为每格首个热保存，时间取三个样本中位数；分配及遍历数在同格三个样本中一致。
“总分配”涵盖上述四个保存阶段，未包含计时外的反射统计、断言及独立恢复。
表中对象链项数及 map 回溯帧数也为这四阶段合计，并非仅 Stage。

| 历史 / 缓存预算 | Stage ms 前→后 | Stage 分配 B 前→后 | 总分配 B 前→后 | 对象链项 前→后 | map 回溯帧 前→后 |
|---|---:|---:|---:|---:|---:|
| 8 / 8 MiB | 0.1757 → 0.1585 | 233,496 → 221,264 | 234,416 → 222,704 | 26 → 0 | 9 → 9 |
| 64 / 8 MiB | 0.2438 → 0.1424 | 248,048 → 225,816 | 248,968 → 227,256 | 138 → 0 | 65 → 65 |
| 8 / 0 | 0.7804 → 0.2031 | 823,800 → 258,512 | 845,800 → 281,032 | 26 → 0 | 189 → 27 |
| 64 / 0 | 11.3838 → 0.4252 | 7,616,152 → 388,576 | 7,703,184 → 476,128 | 138 → 0 | 5,005 → 195 |

修改前每次热保存读 10 条对象链，修改后记录的 48 次热保存全部为 0 次/0 项。
默认缓存的长历史样本总分配减少约 8.7%，收益小于零缓存对照；不能把零缓存的边界结果外推到日常频率。
该 5 对象 fixture 的 PrepareInstall 分配从 56B 增至 576B，Install 仍为 0B；这是明确支付的候选目录复制成本。
不把线程累计分配当作常驻内存或峰值内存测量。新增常驻信息只随 source 对象数增长，不保留完整链。
微秒级时间样本仅作参考，不设性能断言，也不据三轮样本宣称普遍耗时倍率。

默认预算下 map 回溯仍随 Revision 历史增长；零预算下免除对象链读取也减少其间接 map 重放，
但剩余 Loaded/Object planner 和 Append 的 Parent map 读取仍在。这为后继 map 优化提供独立证据，不把它混入本片完成声明。
全量 Base 准备同样保持现状。

重跑当前实现测量：

```powershell
$env:DOTNET_TieredCompilation = '0'
dotnet test tests/DurableGraph.StateStore.Tests -c Release --filter FullyQualifiedName~HotSaveMeasurementTests --logger 'console;verbosity=detailed'
```

### 6.3 回归与独立审阅

- [Storage 计量与统计测试](../../../tests/DurableGraph.Storage.Tests/Db069StorageAccountingTests.cs)：
  15 项通过，包括实际 wire 的文件距离/ticket/body varint 边界、禁缓存及 raw/map 命中计数。
- [保存基线测试](../../../tests/DurableGraph.Persistence.Tests/WorldWorkspaceStorageBaselineTests.cs)：
  新增 5 项，覆盖热 State/NoChange/Remove、每次安装 head/H 与独立 wire 重读一致、Delta/升级 Base 候选丢弃、
  Event 可选 Base 不推进 State、DTO cache hit 的可编辑加载、synthetic/foreign/缺失或错误的待删除行来源拒绝。
- 首轮相关集成 51 项通过；现有 publication/recovery checkpoint 测试复用，不新增故障框架。
- 独立审阅核对 cold/cache、完整 source/owner、Schema 活权威、私有安装、Event、raw 路径与持久格式；无剩余阻塞意见。
- 根 `dotnet build DurableGraph.slnx` 通过，0 warning/error；随后串行完整回归：Storage 202/202、
  StateStore 732/732、Runtime/Generator 1,571/1,571、Serialization 163/163，合计 2,668 项，无跳过。
  前后 Release 独立测量各 4 项通过；最终源码 hash 与 After 证据保持一致。
- 真实 PackageReference 验收：[Run-StateStoreProbe.ps1](../../../experiments/PackageConsumerProbe/Run-StateStoreProbe.ps1)、
  [Run-EventHistoryRecoveryProbe.ps1](../../../experiments/PackageConsumerProbe/Run-EventHistoryRecoveryProbe.ps1)、
  [Run-EventHistoryProbe.ps1](../../../experiments/PackageConsumerProbe/Run-EventHistoryProbe.ps1) 均通过。
  后两条复用首条生成的同版九包 feed，覆盖冷读/升级/连续提交、Pending 恢复与重复恢复不重放、ReadPair、
  根替换、只读浏览不写入和历史图保留。未修改上游、持久格式、默认参数或发布协议。

## 7. 协作落款与回访入口

落款日期：2026-09-12。

| 项目 | 记录 |
|---|---|
| 协作来源 | Codex 会话 `01a093d8-8f37-74b0-89dc-8ef3f822db42`（主协调线程；当次环境的 thread ID 与 session ID 相同） |
| 参与范围 | DB-069 的方案收敛、分工实施协调、集成验收与文档维护；实现、测量、测试及独立审阅由本次协作中的多个 subagent 共同完成 |
| 对应实现 | Git 提交 `5fa7b21f80594ec0acf5ea9cb4df7d78d416e1f4` |
| 讨论回顾 | [用户提供的 Codex 会话分享快照](https://chatgpt.com/s/cx_6aa4ee32aad08191a33c26c3b3f916fd) |
| 本地记录定位 | `~/.codex/sessions/2026/09/12/rollout-2026-09-12T12-20-38-01a093d8-8f37-74b0-89dc-8ef3f822db42.jsonl`（落款时已确认本机存在；原始记录未纳入本仓库） |

可回访的问题包括：为什么只保存 head/H、为什么保留精确 payload、安装与 Event 隔离如何证明，以及为什么把 map 回溯留作独立后继。
历史判断以对应提交及本文证据定位；讨论后续修改时，先对照当前源码与 [PROJECT-STATE](../../../src/PROJECT-STATE.md)。

分享快照是辅助回顾入口。按落款时核对的 [OpenAI 官方说明](https://learn.chatgpt.com/docs/use-chatgpt#share-a-read-only-snapshot-of-a-codex-thread)，
它仅捕获分享时支持的内容，不随后续消息更新，不含原始工具调用、Shell 命令及工具输入输出，也不能直接 fork 原线程。
长期保留期限未确认；落款提供回访线索，不将分享链接视为完整会话归档或可恢复性保证。
