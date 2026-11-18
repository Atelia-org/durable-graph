# 目录前驱复用之后的主线重构计划

2026-11-18 制定，起点为主线 `7f9121fa`。这一版已能从同一 Store 中最近的已验证前驱目录重放后缀。后续要回答的问题是：能否同时降低应用打开、历史分叉签出、独立图恢复及日常保存的成本，并保持每份可编辑对象图独立、损坏数据照常被拒绝？研究已完成，以下五项等待主线实施。

## 实施顺序与续接位置

| 顺序 | 拟进入主线的改动 | 最小可观察验收 | 状态 |
|---|---|---|---|
| 1 | Repository 内复用最近一次成功恢复 Event 的 Schema 绑定环境 | 231 个 Event 全部独立恢复且校验，建环境从 231 次降到 4 次 | 待实施 |
| 2 | 对象目录的 Delta 在本次读取独占的映射上批量应用 | 冷长链逐目录 heads 与 H 相同，每次物化仅在末尾冻结结果；单独交付 fork 和第二份图的对照 | 待实施 |
| 3 | 每次 Dictionary 键索引扫描复用一个编码缓冲 | 32K 单点编辑的持久字节及全部键值相同，保存和深链恢复分配均下降 | 待实施 |
| 4 | 同次 State 保存复用 typed Base 头，选中 Base 再拼接正文 | 同布局重复编码消失，230 步写计划、实写正文、安装结果均相同 | 待实施 |
| 5 | ResolveTag 移除一次重复的提交描述读取 | 成功命中只完整验证一次，损坏目标仍拒绝，公开入口分配及耗时下降 | 待实施 |

先完成前两项，在普通消费方上做一次组合验收。它们可以分开归因，合起来覆盖真实应用打开的两条热点。第三项单独测业务字典，避免把它与第二项对通用 Dictionary 的减负重复计入。后两项分别用保存与标签负载验收。每完成一项，在本表把状态改为**已合入 + 产品提交 + 验收记录**；若新证据推翻方案，写明**退回或淘汰 + 反例 + 替代或重访条件**。下次会话从第一项尚未裁决的条目续接。

## 1. 复用 Event 的 Schema 绑定环境

**线索。** 从已知 CommitId 顺序读 231 个 Event，每次都调用 `SchemaViewBindingEnvironment.Load`。同一 Repository 内复用最近一次合格环境后，建环境 4 次、命中 227 次，完整业务图资格检查仍是 231 次。逐 Event 的线程分配由 93.02 降至 21.51 MB，进程读取次数和字节数相同。入口是 `ReadGraphRootCore`，解码层已经提供可选环境和 `RequireMatches`。[历史恢复入口](../../../src/DurableGraph.Persistence/Repository.MetaBootstrap.History.cs) · [环境资格](../../../src/DurableGraph.Persistence/DecodedRevision.cs)

**原理与改法。** 一个精确 Schema 根及一套冻结模型配置可以服务多个 Event。先为本次选中的业务图执行现有资格检查，取得它实际声明的 Schema 根。在 Repository 内保留一个槽，仅对 Event 按准确地址查找。命中后交给现有解码器核对 Store、配置和 Schema 身份；未命中沿用正常加载路径。成功解码并物化领域根后才替换槽。读的每个 Event 仍有自己的图上下文和可变对象。将槽的生命周期限定在 Repository，检查长期持有一个环境的驻留代价。

**验收。** 在同一仓里按顺序和跳跃顺序读跨 Schema 的 Event，重复读同一 Event 并修改第一份，验证第二份独立。穿插错误角色、损坏目标、物化失败、关闭和换仓，检查失败不会安装新环境。用普通 `LoadEvent` 及完整 `GameSession.Open` 重跑，核对恢复出的操作 ID 集合和业务根。单独看只读 State 作对照。研究实现和负例见文末 `daf34e7a`。

## 2. 批量应用对象目录 Delta，验证 fork 和第二份图

**线索。** `GraphRecordStore.ReadDirectory` 已先查准确地址、再按已验证前驱选后缀。剩余热点在循环体：`ObjectHeadDirectoryCodec.ApplyDelta` 每层都把整张旧表交给通用 Dictionary 编解码器、重建键索引、投影出完整目录。即使这一层仅改一个 head，也要为全表付费。在 231 步真实存档上，仅改这个机制，新进程首次加载 State 的调用线程分配由 460.87 降至 16.42 MB，四组配对耗时降幅中位为 19.56%。[目录读取与缓存](../../../src/DurableGraph.Persistence/GraphRecordStore.cs) · [目录 codec](../../../src/DurableGraph.Persistence/ObjectHeadDirectoryCodec.cs) · [缓存契约](../0067-owned-revision-read-cache-design/README.md)

**原理与改法。** 直接命中完整目录就返回不可变结果；只需读 Base 时保持简短路径。还有 Delta 时，从已验证的 Base 或缓存前驱复制一次 heads，在本次读取独占的有序映射上按物理顺序处理 Remove、Patch、Add。每层独立记录本层已操作的键，层末须完整消费输入；全部成功后冻结一次，按原规则仅准入最终目录。H 是已验证前缀的累计 payload 字节加每个实读后缀的实际长度，使用受检加法。仅查询 H 的入口仍可单独计长。采用专用路径的理由是此处的键固定为 uint 对象 ID、值为 locator，可以直接确定键身份；业务 Dictionary 继续执行自身的比较与编码规则。

**正确性见证。** 复用现有 count、尾部长度检查和 locator 编解码；逐层校验源图、前驱、非零且唯一的操作键、增删改的成员资格、Patch 确实改变值、结果规模及尾随字节。中途失败即丢弃操作私有状态，缓存前驱和已交付的图依旧有效。用通用 codec 在测试中做差分，对每个物理版本比较完整有序的 ID/head 和 H。研究对照覆盖了 469 个目录、49,154 项 head；已有的来源不符、损坏后缀、重试与驱逐回归应继续覆盖。再测纯 Base、单层、长链、稀疏和密集修改，防止只照顾长链。[主线前驱与失败回归](../../../tests/DurableGraph.Persistence.Tests/P0OwnedReadCacheTests.cs)

**应用验收矩阵。** 分配和墙钟之外，同时记目录准确命中、实际重放帧数及本次生成完整目录的次数。批量回放降低每层的成本，即使两臂重放同样多帧也可能有大幅收益。源图仍在内存、Store 有缓存、目标目录仍驻留是三个不同条件。

| 场景 | 对照方式与判据 |
|---|---|
| 当前工作副本在用，从当前或较老提交 fork | 分开计时 `ForkBranch` 登记和子分支 `OpenWorkingCopy` 签出。登记只继承准确提交；签出恢复最近 State。保留父工作副本及其未提交修改，核对分支起点、子图独立编辑和两边续写。冷开 35/115/231 步历史分叉点在主线分别重放 138/804/1,609 帧；用同样的点补齐产品版前后对照。 |
| 第一份堆图保持打开，按同一 GraphRef 重读第二份 | 同一 Repository 连开两次 `OpenGraph`，改第一份再核对第二份的根、子对象和值。所需目录准确命中时无需重放；这里观察热路径是否退化。已有主线测量是**释放第一份工作副本**后同仓再次签出，1.42 ms、0.786 MB、零重放，仅作参照。 |
| 第一份图保持打开，再读较老或另一个快照 | 堆图用 `OpenGraph(GraphRef)`，分支 State 用 `LoadState(CommitId)`，各用对应入口生成的持久位置。分别构造仅前驱驻留和紧预算驱逐，并以新开 Repository 的冷读作对照。核对可达对象和独立修改。较新的缓存目录不能逆推较老版本。 |
| 完整应用重开 | 单独记录仅目录改动，再记录与第 1 项组合。逐项核对终态、操作 ID、关闭后文件指纹、续写并再开。 |

分支和堆图各有自己的公开入口。上述矩阵用于补齐单操作收益，已有完整应用的组合数字不能代替这些测量。[签出](../../../src/DurableGraph.Persistence/Repository.MetaBootstrap.cs) · [按 GraphRef 打开](../../../src/DurableGraph.Persistence/Repository.GraphHeap.cs) · [堆图隔离用例](../../../tests/DurableGraph.Persistence.Tests/GraphHeapTests.cs)

## 3. 在一次业务 Dictionary 索引扫描内复用键编码缓冲

**线索。** 生成式 `Dictionary<int,int>` 有 32,768 项时，一次单点保存只新增 23 B 的 Delta，却做五遍全表索引、分配 81.36 MB。每个键都新建一个 `ArrayBufferWriter<byte>`。只改变缓冲寿命，两组保存从 134.76/136.28 降至 54.99/54.01 ms，分配降至 30.25 MB。冷读 Base 加 16 层单点 Delta 会累计索引 1,179,648 个键，耗时从约 992 降至 323 ms。[DictionaryStateBody.Index](../../../src/DurableGraph/Runtime/Containers/DictionaryStateReader.cs)

**原理与改法。** 在非空的每次 `Index` 调用内创建一个 writer 缓冲。一个键写完，先复制其准确持久字节供索引持有，再复位写入位置处理下一个键。各键的独立字节、哈希与原有持久键/语义重复校验保持有效。空表直接完成；长键只会令缓冲容量保留到本次扫描结束。先采用全闭合类型的局部实现，跨行或跨保存共享状态需要另外的证据。

**验收。** 对 1K/8K/32K 三档做无改、单点、Base 及 Base+1/8/16 层恢复；核对写出的 body、实际链深、全表键值、比较器和错误行为。覆盖宽键后紧跟短键、空表、重复持久键和复合键。研究版全类型实现通过 298 项定向用例。另在第 2 项之后的产品版本上量剩余增益。此前单独推广到 05-care 减少约 215 MB 分配，但两组应用耗时方向不同。

## 4. 业务 State 按布局复用 typed Base 头，选中后拼接

**线索。** `BusinessPersistenceAdapter.PrepareOwned` 在每个候选对象上调用 `TypedBaseBodyCodec.Prepare`。05-care 的 230 次 State 保存编码 47,800 次头，最终只选中 775 个 Base。试验将编码减到 2,026 次，总分配从 375.48 降到 321.38 MB，两组总耗时分别下降 14.61% 和 9.32%。[业务准备](../../../src/DurableGraph.Persistence/BusinessPersistenceAdapter.cs) · [typed Base 头](../../../src/DurableGraph.Persistence/StoredGraphBodies.cs) · [冻结候选](../../../src/DurableGraph.Persistence/PreparedTypedBase.cs)

**原理与改法。** 在一次保存的固定 catalog 视图中，同一完整不可变 ObjectLayout 的已校验头字节可以复用。首遇布局走现有头编码与路由校验，后续候选持有操作内只读的头。eager 正文使用已冻结内容的长度，deferred 正文沿用准确估算；保留捕获、用户回调时机与失败时机要求。规划器选中 Base 后，在追加前拼装完整字节。保留实际布局的选路、旧来源资格、软预算、实写记录与安装 H。检查头加正文的受检长度、可分配数组上限和原有超限拒绝边界。

**验收。** 逐步比较 230 个写计划的移除 ID、对象 ID、Base/Delta、prior、正文长度和完整字节，再比较安装 head/H、文件增长和冷开续写。合成同布局及混合布局图，覆盖无改、单叶更新、强制 Base，以及已有自定义绑定、超限和失败回归。4,096 叶图的研究结果是每次头编码 12,293 → 6；单叶更新的整次耗时一对变慢、一对变快，验收分别报告机制计数、分配和墙钟。

## 5. ResolveTag 只验证一次目标提交描述

**线索与原理。** `RepositoryResources.ResolveStoredTag` 已验证找到的提交及引用的图元数据。公开 `Repository.ResolveTag` 在同一次受保护的操作内，对返回的同一 ID 再读一次 descriptor 并丢弃结果。16/64/128 个名称、每规模连续命中 4,096 次的两对实验中，均值从约 64–76 µs 降至 31–34 µs，每次分配从 576 降至 288 B。[公开入口](../../../src/DurableGraph.Persistence/Repository.cs) · [内部解析与验证](../../../src/DurableGraph.Persistence/RepositoryResources.Bookmarks.cs)

**改法与验收。** 删除公开包装层多余的 `ReadDescriptor` 调用，保留内部校验及现有 owner、只读和故障规则。分别验证成功、找不到名称、损坏元数据、非法图指针、关闭及只读重开，重跑上述规模的公开命中测试。本项只改善名称解析；创建标签另有发布和读回成本。

## 共同验收与研究入口

- **对照。** 每项比较其直接父版本与产品候选；第 1、2 项组合及最终版本再与 `7f9121fa` 对照。固定 Storage 源码 pin、应用模型、输入及实际加载程序集指纹；正反顺序串行计时，诊断计数另跑。发现异常保留原始组并重测。首次进程打开、预热运行时下的新 Repository、同一 Store 再读分别报告。MB 为十进制调用线程托管分配，墙钟只比较同一入口。
- **参照。** 05-care 原始轨迹是 230 组 Event/State；第 231 组是正常续写。第 231 步存档在安静窗口的四组实验中，两项组合的 `GameSession.Open` 从 448.1 降至 92.9 ms，分配 544.46 → 28.47 MB。首次调用含 JIT，从约 1,423 降到 979 ms，OS 文件页可热。保留这三个口径，按新测的主线结果下结论。
- **交付。** 每片用仓库固定 Storage 源构建 `DurableGraph.slnx`，跑相关测试；完成整组后全量回归并从普通消费方复核。产品提交只取已验收机制及回归，性能开关和临时计数留在研究工具中。主线程核对受测程序集、原始样本和集成差异后更新上表。最终记录五项裁决、产品提交、实测收益及仍适用的限制。[Storage 构建入口](../../storage-dependency.md)

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
