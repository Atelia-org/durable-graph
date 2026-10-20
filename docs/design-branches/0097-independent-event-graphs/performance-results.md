# DB-097 Event 成本见证

> 2026-10-20。旧实现基线与独立 Event 候选均已完成同夹具对照，各 7/7 通过。合同见 [README](README.md)。产品源码基线 `4d7c4b3`；新增见证不修改生产代码。

## 测量方法和边界

[IndependentEventCostTests](../../../tests/DurableGraph.Persistence.Tests/IndependentEventCostTests.cs) 使用真实 BranchSaveSessionFixture、Catalog、业务图追加和发布流水线。单链节点只有 int Value 与 Next；N 精确等于已保存 State 的 live 节点数。三个形状：small 为独立单节点（M=1）；shared8 为原 State 链尾 8 个节点；whole 为同一 State 根（M=N）。后两者仅 Event 根的 Value 改变，k=1；State 基线保持原值。每场景两样本，后一次仍相对同一 State，不相对前一 Event。

各样本先独立测 Capture 与 PrepareOwned 后丢弃候选；catalog 准备不计入该 PrepareOwned 窗口。随后重新走完整 Prepare/Commit，分别记录准备与含 flush 的 Commit，再记录两者总窗口。这是内部 fixture 的 Prepare+Commit 窗口，不是公开 WorkingCopy.CommitEvent 全入口端到端 benchmark；它包含真实 prepare/catalog/append/publish/flush，但不含公开输入构造及 WorkingCopy 准入。总窗口不含测量日志输出；它包含分阶段读取计时器/分配计数的极小观测开销。分配是当前线程累计 bytes，不是峰值堆或 RSS。第一次 dry preparation 会预热物理 cache/模型，不代表冷保存。时延是现场单样本，不作测试阈值或稳定性能承诺。

`repositoryFileGrowth` 为持久提交前后整个库目录所有文件逻辑长度之差，包含内容、历史及控制文件；不是磁盘块数、压缩前 payload bytes 或系统调用 I/O bytes。`appendedFrames` 是 GraphRecordStore 实际追加帧计数。N=0 第一次提交包含分支/Schema 初始化，应与第二样本分开解读。冷读在完全关闭资源后重新只读打开，计时不包括 Open；不是清空 OS 文件缓存后的物理冷 I/O。

测试断言 Event 值准确、读出 mutable 实例独立、修改读出根不影响输入，并且每次提交不改变 session State baseline/source 实例。结构/来源细节只观测，不硬编码旧 Event 增量语义，因此同一 fixture 可用于候选复测。

## 旧实现结果

包模式，Storage `0.1.3-dev.20261019.g70b406c`；既有 assets 确认为 package 引用，来自 `.artifacts/maintenance-20261020/NuGet.Config`。命令：

```powershell
dotnet test tests/DurableGraph.Persistence.Tests/DurableGraph.Persistence.Tests.csproj --no-restore -c Release -p:UseStorageSources=false --filter FullyQualifiedName~IndependentEventCostTests --logger 'trx;LogFileName=costs.trx' --results-directory .artifacts/db097/baseline
```

7/7 通过，无编译警告。原始输出在 `.artifacts/db097/baseline/test.log`、`costs.trx`、提取的 `measurements.txt`。本次只运行有界定向测试；根构建和完整回归由最终产品验收承担。见证文件 SHA256：`D66241F1F9ECF956582A3B51AE1C5AD8102ACB1E7C01019D0C41125E43E93F46`。

以下统一列第二样本，全部 bytes 为十进制原值：

| N | M / 形状 | Capture 分配 | PrepareOwned 分配 | 完整 Prepare+Commit 分配 | 总 ms | 库文件增长 bytes |
|---:|---|---:|---:|---:|---:|---:|
| 0 | 1 / small | 3,552 | 5,024 | 60,136 | 3.549 | 248 |
| 1,000 | 1 / small | 2,512 | 921,024 | 1,578,488 | 9.920 | 276 |
| 10,000 | 1 / small | 2,512 | 9,072,472 | 15,245,352 | 64.694 | 276 |
| 1,000 | 8 / shared8 | 10,720 | 943,528 | 1,620,720 | 8.759 | 344 |
| 10,000 | 8 / shared8 | 10,720 | 9,094,192 | 15,286,816 | 119.749 | 344 |
| 1,000 | 1,000 / whole | 1,134,848 | 4,038,752 | 8,677,688 | 33.582 | 284 |
| 10,000 | 10,000 / whole | 11,370,144 | 40,191,768 | 85,240,192 | 270.151 | 284 |

N>0 场景 PrepareOwned 完整 source 资格次数均为 1；small/shared8/whole 的 body calls 分别为 1/16/2N，待写对象 records 均为 1，removed 分别为 N/(N-8)/0。全部第二样本追加 3 帧。

固定 M=1 的 N=1,000→10,000 变化直接证明准备与追加存在 N 税，而 Capture 分配保持 2,512 bytes。N=10,000 时完整 prepare 为 9,088,736 bytes，commit 为 6,156,616 bytes；不能仅修改 Capture 就消除这些开销。

冷读分配：small 的 N=0/1k/10k 为 104,088 / 113,424 / 112,912 bytes；shared8 的 N=1k/10k 为 301,688 / 1,410,248 bytes；whole 为 9,364,280 / 91,042,936 bytes。shared8 当前虽然只需要 8 个对象，但引用旧批次可能读取整个 State 批次，因而仍可能随 N 放大。该原因是源码解释，测量本身只证明结果的相关性；未单独统计批次 payload 或控制多层 Delta 深度。

whole 是必要反例：当前 PrepareOwned 对 10k 对象支付约 40MB 分配，但实际只写 1 条记录，库增长仅 284 bytes。独立完整 Event 后即使准备便宜，完整 10k 对象写入也预计显著增大；不得只报道准备收益。

## 独立 Event 候选结果

产品完成强制根重建后，直接使用同一测试二进制定向复测；见证源码 SHA256 与基线完全一致，没有为候选改测量或断言。命令在上述基线命令基础上增加 `--no-build`，results-directory 改为 `.artifacts/db097/candidate`，7/7 通过。原始记录为该目录的 `test.log`、`costs.trx`、`measurements.txt`。产品最终提交身份由本片最终验收记录承载；这些数据来自本片完成实现的候选工作树，并非旧基线源码。

以下同样列第二样本：

| N | M / 形状 | Capture 分配 | PrepareOwned 分配 | 完整 fixture Prepare+Commit 分配 | 总 ms | 库文件增长 bytes |
|---:|---|---:|---:|---:|---:|---:|
| 0 | 1 / small | 3,696 | 3,520 | 57,432 | 4.042 | 248 |
| 1,000 | 1 / small | 2,656 | 3,520 | 33,160 | 0.941 | 244 |
| 10,000 | 1 / small | 2,656 | 3,520 | 33,176 | 1.252 | 248 |
| 1,000 | 8 / shared8 | 10,864 | 23,232 | 76,832 | 1.160 | 368 |
| 10,000 | 8 / shared8 | 10,864 | 23,232 | 76,888 | 1.214 | 384 |
| 1,000 | 1,000 / whole | 1,134,992 | 2,816,640 | 6,199,656 | 17.122 | 20,788 |
| 10,000 | 10,000 / whole | 11,370,288 | 28,160,640 | 61,286,976 | 114.091 | 211,600 |

所有场景 PrepareOwned 的 State source 资格次数都为 0，removed 都为 0，body calls 与待写 records 都为 M；全部第二样本追加 3 帧。该计数是 PrepareOwned 窗口内的 State 内容资格，不能误解为整次提交完全不做图/Schema/owner/发布验证。

最明确的结果是固定 Event 的准备分配已不随 N 增长：M=1 在 N=0/1k/10k 都为 3,520 bytes；共享尾 M=8 在 N=1k/10k 都为 23,232 bytes。以 N=10k/M=1 为例，完整 fixture 分配从 15,245,352 降到 33,176 bytes；Capture 反而从 2,512 小增至 2,656 bytes，符合新增独立 CaptureSession 的固定开销，而非不存在的全 State Capture 复制被消除。

N=10k/M=1 的完整准备从 9,088,736 降至 17,888 bytes，Commit 从 6,156,616 降至 15,288 bytes；收益同时发生于准备和实际追加/发布窗口。现场时延 64.694→1.252 ms 只是一对第二样本，不作为稳定倍数，更不是公开 WorkingCopy API 全入口的性能保证。N=0 现场时延还从 3.549 小升到 4.042 ms，说明固定开销、JIT/GC/调度与 flush 噪声不应被忽略。

共享尾 8 的冷资源重开读取分配在 N=1k/10k 时变成 160,360 / 160,776 bytes；旧值为 301,688 / 1,410,248。独立小 Event 的冷读约 104–105 KB，旧实现约 113 KB，改善较小。whole 的 1k/10k 冷读为 7,940,776 / 77,441,696 bytes，旧值为 9,364,280 / 91,042,936；完整恢复仍需处理 M 对象，不会降为常数。

**必须同时保留的代价：** whole 的 N=10k/k=1 场景，库文件增长从 284 变为 211,600 bytes（约 745 倍），记录数从 1 变为 10,000；N=1k 则从 284 变为 20,788 bytes。小共享尾 8 的文件增长也从 344 变为 368/384 bytes。帧数仍为 3，不能据帧数不变声称写入量不变。上述比值是这两次有明确形状的逻辑文件增长对照，不能外推为所有 Event 的空间放大倍数。

## 结论与限度

同夹具证据支持本片核心目标：小 Event 退出 State 大小相关的资格、差分和目录来源工作；M=1/8 时分配随 N 基本平坦。完整保存仍保留 M 项捕获、编码/排序、业务回调、Schema、发布和 flush 成本。大共享对象图的准备成本有所减少，但全量写入显著增大，应由应用按事实载荷/必要快照建模。

本见证未建立长度 0/4/16 的受控 Delta 链矩阵，未测试多批长时间吞吐、公开 WorkingCopy 全入口的独立 benchmark、真实设备掉电或 OS 冷缓存。两轮各两样本的时延只供定位，不证明端到端倍数。测试覆盖固定小 Event 的 N 税、大共享子图写入放大以及真实冷资源重开读回，源码与结构/故障回归另行承担语义证明。
