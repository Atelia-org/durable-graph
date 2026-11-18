# 目录前驱复用之后的主线重构计划

2026-11-18 制定，起点为主线 `7f9121fa`。这一版已能从同一 Store 中最近的已验证前驱目录重放后缀。后续要回答的问题是：能否同时降低应用打开、历史分叉签出、独立图恢复及日常保存的成本，并保持每份可编辑对象图独立、损坏数据照常被拒绝？研究已完成，以下五项等待主线实施。

## 实施顺序与续接位置

| 顺序 | 拟进入主线的改动 | 最小可观察验收 | 状态 |
|---|---|---|---|
| 1 | Repository 内复用最近一次成功恢复 Event 的 Schema 绑定环境 | 同一准确 Schema 可复用环境，每个 Event 仍独立校验和物化；固定 05-care 顺序为 231 次建立降到 4 次 | 待实施 |
| 2 | 对象目录的 Delta 在本次读取独占的映射上批量应用 | 逐目录 heads 与 H 相同；非空 Delta 后缀只在末尾冻结一次；单独交付 fork 和第二份图的对照 | 待实施 |
| 3 | 每次 Dictionary 键索引扫描复用一个编码缓冲 | 32K 单点编辑的持久字节及全部键值相同，保存和深链恢复分配均下降 | 待实施 |
| 4 | 同次 State 保存复用 typed Base 头，选中 Base 再拼接正文 | 同布局重复编码消失，230 步写计划、实写正文、安装结果均相同 | 待实施 |
| 5 | ResolveTag 移除一次重复的提交描述读取 | 成功命中只完整验证一次，损坏目标仍拒绝，公开入口分配及耗时下降 | 待实施 |

先分别验收前两项，再测完整应用的组合收益。第三项用业务字典衡量第 2 项落地后的增益，后两项分别用保存与标签负载验收。下一轮从表内首个待办项续接。每项裁决后把产品提交、验收数据或淘汰反例写入该片结果记录，在此移除对应待办及施工细节，保留结果链接。

## 1. 复用 Event 的 Schema 绑定环境

**线索。** 从已知 CommitId 顺序读 231 个 Event，每次都调用 `SchemaViewBindingEnvironment.Load`。同一 Repository 内复用最近一次合格环境后，建环境 4 次、命中 227 次，完整业务图资格检查仍是 231 次。逐 Event 的线程分配由 93.02 降至 21.51 MB，进程读取次数和字节数相同。入口是 `ReadGraphRootCore`，解码层已经提供可选环境和 `RequireMatches`。[历史恢复入口](../../../src/DurableGraph.Persistence/Repository.MetaBootstrap.History.cs) · [环境资格](../../../src/DurableGraph.Persistence/DecodedRevision.cs)

**原理与改法。** 在 `ReadGraphRootCore` 先为本次业务图取得合格快照。处理 Event 时，用快照的准确 Schema 根匹配 Repository 中的上一个绑定环境；地址可由环境自身提供。未命中走正常加载，命中仍由 `RequireMatches` 核对 Store、冻结配置、Schema 根与所选快照的归属。每次都独立解码、物化可变领域对象，成功后才替换槽。沿用 Repository 观察到故障及 Dispose 时释放驻留状态的路径。

**验收。** 同 Schema 连读、跨 Schema 跳读，并在 A 成功、B 解码或物化失败后再读 A。在两个同 Schema 的 Event 上验证成功建立的 factory/comparer 可以复用，而根和可变字典各自独立。沿用现有跨配置、换仓与普通 `LoadState` 的独立环境回归。`LoadEvent<T>` 在根物化成功后才检查结果类型，类型不匹配仍抛错，已经合格的环境可以留存。重跑 `LoadEvent` 和 `GameSession.Open`，核对建环境次数、操作 ID 和业务根，并记录单槽的驻留代价。[环境复用与隔离](../../../tests/DurableGraph.Persistence.Tests/SplitModelEnvironmentTests.cs)

## 2. 批量应用对象目录 Delta，验证 fork 和第二份图

**线索。** `GraphRecordStore.ReadDirectory` 已先查准确地址、再按已验证前驱选后缀。剩余热点在循环体：`ObjectHeadDirectoryCodec.ApplyDelta` 每层都把整张旧表交给通用 Dictionary 编解码器、重建键索引、投影出完整目录。即使这一层仅改一个 head，也要为全表付费。在 231 步真实存档上，仅改这个机制，新进程首次加载 State 的调用线程分配由 460.87 降至 16.42 MB，四组配对耗时降幅中位为 19.56%。[目录读取与缓存](../../../src/DurableGraph.Persistence/GraphRecordStore.cs) · [目录 codec](../../../src/DurableGraph.Persistence/ObjectHeadDirectoryCodec.cs) · [缓存契约](../0067-owned-revision-read-cache-design/README.md)

**原理与改法。** 直接命中完整目录就返回不可变结果；遇到 Base 就截止追链。纯 Base 直接返回解码结果。还有 Delta 时，从已验证的 Base 或缓存前驱复制一次 heads，在本次读取独占的有序映射上按物理顺序处理 Remove、Patch、Add。每层独立记录本层已操作的键，层末须完整消费输入；全部成功后冻结一次，按原规则仅准入最终目录。H 是已验证前缀的累计 payload 字节加每个实读后缀的实际长度，使用受检加法。仅查询 H 的入口仍可单独计长。采用专用路径的理由是此处的键固定为 uint 对象 ID、值为 locator，可以直接确定键身份；业务 Dictionary 继续执行自身的比较与编码规则。

**正确性见证。** 沿用 `ReadDirectoryChain` 对来源图和前驱边的检查；正文复用 count、尾部长度与 locator 的现有规则。保留成员资格、结果规模、有效 ID、Patch 确实改变值与完整消费检查。同一层的 Remove/Patch/Add 共享去重集合，下一层重新开始，同一 ID 可以合法连续更新。畸形后缀应丢弃本次私有状态，已缓存前驱仍可重读。通用 codec 作为测试对照，逐个比较有序 ID/head 和 H；研究已覆盖 469 个目录、49,154 项 head。复用现有纯 Base、短后缀、错误来源与驱逐回归，覆盖稀疏及密集修改。[主线前驱与失败回归](../../../tests/DurableGraph.Persistence.Tests/P0OwnedReadCacheTests.cs)

**应用验收。** 按公开操作采样墙钟、分配与已有目录命中、重放帧数的增量。`DirectoryMaterializations` 只计成功完成的目标目录。验证消除逐层整表构造时，在研究轨分别记 Base 解码、Delta 后缀内的完整投影或冻结；新旧两臂可以重放同样多帧。

| 场景 | 对照方式与判据 |
|---|---|
| 当前工作副本在用，从当前或较老提交 fork | 分段测 `ForkBranch` 和 `OpenWorkingCopy`。登记会发布及读回分支管理 Meta 图；签出才恢复所选 State。签出同时记准确 State、已有 `PreparedStateHits/Misses` 和物理目录计数。准备槽命中仍复验持久来源，物理目录可能命中或重放。用同 State 热签出与确有后缀重放的历史签出作对照，保留父工作副本的未提交修改，验证子图独立和两边续写。 |
| 第一份堆图保持打开，按同一 GraphRef 重读 | 同一 Repository 连开两次 `OpenGraph`，改第一份再核对第二份的根、子对象和值。此入口直接从持久图恢复。历史的 1.42 ms 样本是释放首份工作副本后的同仓签出，实录两次物理目录命中、零重放；它只能作为热签出的参照。 |
| 第一份图保持打开，再读历史快照 | 分别走 `OpenGraph(GraphRef)` 与 `LoadState(CommitId)`。保留一例只命中前驱、实际重放短后缀的公开读取，和同一位置新开 Repository 的冷读作对照。驱逐回退复用机制测试。短链或热路径退化时，再展开链深、密度与预算。 |
| 完整应用重开 | 同一存档记录目录改动的增量及与第 1 项的组合收益。核对终态、操作 ID、关闭后文件指纹和续写后再开。 |

分支和堆图各有自己的公开入口。35/115/231 步分叉点的 138/804/1,609 帧来自已登记分支的冷签出，适合作为选点和归因参照。[签出](../../../src/DurableGraph.Persistence/Repository.MetaBootstrap.cs) · [按 GraphRef 打开](../../../src/DurableGraph.Persistence/Repository.GraphHeap.cs) · [堆图隔离用例](../../../tests/DurableGraph.Persistence.Tests/GraphHeapTests.cs)

## 3. 在一次业务 Dictionary 索引扫描内复用键编码缓冲

**线索。** 生成式 `Dictionary<int,int>` 有 32,768 项时，一次单点保存只新增 23 B 的 Delta，却做五遍全表索引、分配 81.36 MB。每个键都新建一个 `ArrayBufferWriter<byte>`。只改变缓冲寿命，两组保存从 134.76/136.28 降至 54.99/54.01 ms，分配降至 30.25 MB。冷读 Base 加 16 层单点 Delta 会累计索引 1,179,648 个键，耗时从约 992 降至 323 ms。[DictionaryStateBody.Index](../../../src/DurableGraph/Runtime/Containers/DictionaryStateReader.cs)

**原理与改法。** 在非空的每次 `Index` 调用内创建一个 writer 缓冲。一个键写完，先复制其准确持久字节供索引持有，再复位写入位置处理下一个键。各键的独立字节、哈希与原有持久键/语义重复校验保持有效。空表直接完成；长键只会令缓冲容量保留到本次扫描结束。先采用全闭合类型的局部实现，跨行或跨保存共享状态需要另外的证据。

**验收。** 保留 32K 单点保存和 Base+16 层冷读两端的公开入口对照，核对实际链深、完整 body、全部键值与分配。机制回归覆盖宽键后接短键、空表、重复持久键、业务比较器下重复的键及复合键。研究版全类型实现通过 298 项定向用例；完整规模矩阵已有脚本，可在小表退化或标度异常时展开。此前此改动单独用于 05-care 减少约 215 MB 分配，两组应用耗时方向不同。

## 4. 业务 State 按布局复用 typed Base 头，选中后拼接

**线索。** `BusinessPersistenceAdapter.PrepareOwned` 在每个候选对象上调用 `TypedBaseBodyCodec.Prepare`。05-care 的 230 次 State 保存编码 47,800 次头，最终只选中 775 个 Base。试验将编码减到 2,026 次，总分配从 375.48 降到 321.38 MB，两组总耗时分别下降 14.61% 和 9.32%。[业务准备](../../../src/DurableGraph.Persistence/BusinessPersistenceAdapter.cs) · [typed Base 头](../../../src/DurableGraph.Persistence/StoredGraphBodies.cs) · [冻结候选](../../../src/DurableGraph.Persistence/PreparedTypedBase.cs)

**原理与改法。** 在一次 State 保存的固定 catalog 视图内，按完整不可变 ObjectLayout 复用既有头编码与路由校验的结果。eager 正文按已冻结实长检查头加正文的可分配上限。deferred 延续各类型现有规划输入：可精确计长的 string/List 沿用原计量，DurableText 保留分段软估算。用软估算做容量拒绝会误判，用它安装 H 会记错成本。规划器选中 Base 后才拼接，在追加前按实际编码长度检查，按实写记录安装 H。保留捕获、eager 回调时机和软预算。[DurableText 估算与实写回归](../../../tests/DurableGraph.Persistence.Tests/DurableTextIntegrationTests.cs)

**验收。** 逐步比较 230 个写计划的移除 ID、对象 ID、Base/Delta、prior、正文长度和完整字节，再比较安装 head/H、文件增长和冷开续写。合成同布局及混合布局图，覆盖无改、单叶更新、强制 Base，以及已有自定义绑定、超限和失败回归。4,096 叶图的研究结果是每次头编码 12,293 → 6；单叶更新的整次耗时一对变慢、一对变快，验收分别报告机制计数、分配和墙钟。

## 5. ResolveTag 只验证一次目标提交描述

**线索与原理。** `RepositoryResources.ResolveStoredTag` 已验证找到的提交及引用的图元数据。公开 `Repository.ResolveTag` 在同一次受保护的操作内，对返回的同一 ID 再读一次 descriptor 并丢弃结果。16/64/128 个名称、每规模连续命中 4,096 次的两对实验中，均值从约 64–76 µs 降至 31–34 µs，每次分配从 576 降至 288 B。[公开入口](../../../src/DurableGraph.Persistence/Repository.cs) · [内部解析与验证](../../../src/DurableGraph.Persistence/RepositoryResources.Bookmarks.cs)

**改法与验收。** 删除公开包装层多余的 `ReadDescriptor`，保留内部校验及现有 owner、只读和故障规则。沿用既有标签测试覆盖缺失、损坏目标、非法图指针、关闭与只读重开。用已有公开命中脚本复核一次完整验证与每次分配、耗时；规模差异异常时展开全档。

## 共同验收与研究入口

- **对照。** 每项比较其直接父版本与产品候选；第 1、2 项组合及最终版本再与 `7f9121fa` 对照。固定 Storage 源码 pin、应用模型、输入及实际加载程序集指纹；正反顺序串行计时，诊断计数另跑。发现异常保留原始组并重测。首次进程打开、预热运行时下的新 Repository、同一 Store 再读分别报告。MB 为十进制调用线程托管分配，墙钟只比较同一入口。
- **参照。** 05-care 原始轨迹是 230 组 Event/State；第 231 组是正常续写。第 231 步存档在安静窗口的四组实验中，两项组合的 `GameSession.Open` 从 448.1 降至 92.9 ms，分配 544.46 → 28.47 MB。首次调用含 JIT，从约 1,423 降到 979 ms，OS 文件页可热。保留这三个口径，按新测的主线结果下结论。
- **交付。** 每片用仓库固定 Storage 源构建 `DurableGraph.slnx`，跑相关测试；完成整组后全量回归并从普通消费方复核。性能开关和临时计数留在研究工具中。主线程核对受测程序集、原始样本和集成差异，链接每项的裁决与验收结果。[Storage 构建入口](../../storage-dependency.md)

以下均为本仓本地研究提交。即使实验 worktree 搬迁，也可以用 `git show <提交>:<路径>` 取回报告，按报告内的输入说明复跑。真实私有存档留在原实验环境。例：

```powershell
git show 0c4f6f19:docs/research/post-prefix-performance/README.md
git show 0c4f6f19:docs/research/post-prefix-performance/series/decision.json
```

| 用途 | 研究提交与文件 |
|---|---|
| Event、历史分叉签出、同仓重读 | `daf34e7a:experiments/HistoryReadAmplificationProbe/README.md` |
| 批量目录回放的机制 / 差分与四对实测 | 实现 `fb1c1f91`；报告 `2b5fa58d:experiments/DirectoryDictionaryMechanismProbe/cold-map.md` |
| 低复杂度目录备选 | `d042dcbc:experiments/Db102Consumer/ColdIntermediates.md` |
| 大业务字典 / 全类型产品候选 | `92d01e0d:experiments/BusinessDictionaryCostProbe/README.md`、`42cf45fa:experiments/DirectoryDictionaryMechanismProbe/IndexScratch.md` |
| typed Base 准备 | 实现 `52583558`；报告 `50944e69:experiments/Db102Consumer/SAVE-AUDIT.md` |
| 标签解析及创建的分段测量 | `e0503285:experiments/TagDirectoryAudit/README.md` |

## 保留的备选与重访条件

- **缓存已验证的中间目录。** 这是第 2 项的备选。它继续用通用 codec，在默认 8 MiB 下多占约 0.76 MB 缓存计费，与 Event 单槽组合可达 134.6 ms、88.49 MB。如果批量路径在主线上出现难以消除的语义分歧或维护成本，拿它重新比较，并补测紧预算驱逐。叠加两者的净收益尚待独立测量。
- **进一步消除业务字典的整表扫描、并行 Prepare。** 第 3 项仍保留五轮扫描，第 4 项仍做全图准备。等这两项落地，在代表性写负载上重新看剩余 CPU 时间；若索引构造或可独立执行的准备工作仍占主导，再单独开题。
- **历史分页与 CreateTag。** 在 463 个描述的样本中，分页的额外访问符合续页边界复验。创建第 65–128 个标签时，重编旧叶只占 5.77% 分配、6.58% 时间。出现高频应用需求时，分别分析枚举附加读取或创建时的追加、安装前读回、目录重建，再选机制。
