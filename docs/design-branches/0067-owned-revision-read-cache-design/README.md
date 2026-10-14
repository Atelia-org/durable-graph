# DB-067：append-only Store 的修订读缓存与紧凑只读索引

> 整理说明：2026-10-14 从 `docs/design-branches/0067-owned-revision-read-cache-design.md` 搬入问题入口；正文来源 Git `8ef9fca`。
> 适用性：owned frame/map LRU、紧凑索引与寿命的当片实现证据；原严格无修尾表述的适用性由 [DB-086](../0086-new-storage-refactoring-plan/README.md) 的底座正常恢复合同收窄，完整帧 append-only 与离线救援缓存纪律继续有效。
> 本文原过程与测试数字保留当时基线；当前能力与焦点见 [PROJECT-STATE](../../../src/PROJECT-STATE.md)。

> 状态：**已实施并验收**。2026-09-12。产品实现、测量与独立审阅证据见 §7。
> 本片实施日常 append-only Store 的统一读缓存、keys/values 双数组二分及生命周期迁移。
> 初始材料：[read-cache.md](../../research/read-cache.md)。早期缓存机制证据见 [Probe](../../research/read-cache-probe/README.md)。
> Probe 源码基线为 f68388f88ba09354e9fa90420dc2cf22b146b6cf，Atelia 为 539088414686f51a210798b35ee56d02de8c845c。
> 沿用用户已完成的 FrozenSortedDictionary 重命名，现提取为 Storage 共享内部双数组实现。
> 后继：[DB-069](../0069-incremental-save-baseline/README.md) 实施已提交保存基线的 head/H 增量维护；本片的历史测量与原范围保持。

## 1. 选择与最小判据

**StateRevisionStore 普通路径使用统一有界 LRU；缓存 owned StateRevision 与完整 head map。只读地址字典采用私有 keys/values 双数组，按 key 二分查找、升序枚举。**

正常 Store 的稳定性由项目明确的 append-only 与生命周期纪律保证，不再为 GraphResources 单设缓存特许入口或逐次反向回调 owner。
保留一个完整 FrameAddress 索引、一条 LRU 链、frame-only / map-only / both 三种非空 entry；超预算可以不驻留。

本片回答：在保持 wire、exact Parent/prior、真实 H、每视图验证及发布语义的前提下，能否减少重复 wire 解码和完整 map 物化，并用紧凑只读表示降低驻留开销？
最小判据是：成功准入且尚未驱逐的组件命中不再生产；冻结后的键值与升序保持；预算与驱逐可测；合法使用下的结果和原持久数据拒绝规则保持。
不承诺每地址每实例只解码一次，也不承诺任意预算下性能非劣。

## 2. 需求账本与使用纪律

| 来源 | 已选约束或事实 |
|---|---|
| 当前用户决定 | 正常读写 append-only；只有专门离线救援才能截断尾部；纪律写入根 AGENTS |
| 当前用户选型 | 预期控制单次 Revision 写入体积，单帧复用通常不超过几百次；首选构建简单、驻留紧凑的双数组二分字典，不把 FrozenDictionary 对比试验设为施工前置 |
| 当前源码 | StateRevision 构造后只读；LocalObjects 和 local IDs 已排序；Frame 的 external heads 及完整 live map 都按 ObjectId 升序 |
| 当前源码与测试 | exact Parent 选定 prior、direct local record、strict descent、Base 截断内容链、原始实编码 H 不变 |
| 当前产品模型 | 宿主串行操作、仓库独占锁、fault/dispose 后停止服务；raw Storage 借用底层寿命 |
| 当前授权边界 | 用户已认可设计并授权实施、验证、相关文档维护及按完成边界提交 |

“几百次”是用户的工作负载预期和选型理由，不是当前 API 的硬上限，也没有被证明适用于每张完整 live map。
单帧记录不多，不代表经过历史重放的 live map 小；图中仍可能保留大量未变化对象。预算按实际驻留表示计费，不据该预期删掉有界机制。
选择二分是当前工程取舍，不把它写成已实测优于 BCL FrozenDictionary。

### 2.1 正常使用与离线救援

执行纪律维护于 [AGENTS.md](../../../AGENTS.md#persistent-data-discipline)，本节解释缓存的依赖：

- 正常打开期间，已有完整持久帧内容稳定；追加、分段轮换和逻辑 branch/Move 不覆写或删除既有 StateRevision。正常路径不得自动修尾。
- 底层必须比借用它的 Store 活得久；宿主串行化操作，底层外借 writer lease 与 Store 操作不重叠；写入不确定或宿主 fault 后按既有合同关闭、重开。
- 截断只在离线救援阶段执行：先关闭相关 Repository/Store/文件资源，丢弃全部关联缓存，再由不使用缓存的专门工具操作。
- 救援结束后重新打开底层和新 Store。不能只把旧缓存临时关闭、救援后再启用；旧完整 FrameAddress 可能已经对应新内容。
- 外层发布引用、锁文件和可重建派生文件仍按各自协议更新；append-only 不禁止合法 ref 发布，也不增加新的发布权威。

底层 IRbfFile 仍可以有 Truncate，不能由这一点推断正常调用方可在 Store 存活期间使用它。
在线原址重写、底层先关闭后继续读 Store、故障后继续调用或 lease 重叠都属于合同外使用；本片不为这些情况建设 generation、失效通知或命中前底层探测。
正常参数校验、坏持久数据拒绝和产品入口既有 fault/dispose/reentry 检查不因此删除。

### 2.2 当前消费者和不同缓存层

[StateRevisionStore](../../../src/DurableGraph.Storage/StateRevisionStore.cs) 是 raw Read、完整 map、对象链及 Append direct-prior preflight 的共同入口。
读取方包括 [RevisionDecoder](../../../src/DurableGraph.Persistence/RevisionDecoder.cs)、Loaded/Captured/ObjectRevisionPlanner 以及 EventHistory 打开时的逐历史图验证。
连续提交经 [WorldWorkspace](../../../src/DurableGraph.Persistence/WorldWorkspace.cs) → [LoadedRevisionPlanner](../../../src/DurableGraph.Persistence/LoadedRevisionPlanner.cs) → [ObjectRevisionPlanner](../../../src/DurableGraph.Persistence/ObjectRevisionPlanner.cs)，因此保存侧同样有当前消费者。

[DB-064](../0064-shared-revision-decoding-design/README.md) 的操作内 (ObjectId, head) DTO/string 缓存继续保持其生命周期，不合并进 raw 缓存。
完整 head map 是从 Revision 链派生的浅声明；它不证明 external locator 真有 local record，更不证明 typed body 或图有效。

## 3. 双数组只读字典与 local-record 二分

### 3.1 一个共享内部表示

将用户已重命名的私有类型提取为 Storage 共享的 [FrozenSortedDictionary](../../../src/DurableGraph.Storage/FrozenSortedDictionary.cs)，供两个当前消费者使用：

1. StateRevision 的 ExternalObjectHeads；
2. LiveObjectHeadMapMaterializer 完成后的完整 head map。

沿用 FrozenSortedDictionary 命名以区别于 System.Collections.Frozen.FrozenDictionary；首片实际键值类型均为 uint / FrameAddress。
保持 IReadOnlyDictionary 查询外观，不新建公共集合库、comparer/provider 平台或运行时策略选择。

唯一常驻数据为两份等长 owned 数组：

~~~text
keys   : TKey[]      // 按默认比较顺序严格递增
values : TValue[]    // values[i] 永远对应 keys[i]

TryGetValue / ContainsKey / indexer：在 keys 上二分，再用同一下标取 values
Keys / Values / pair 枚举：按数组下标递增
~~~

首片使用默认 key 比较；不接受自定义比较规则。uint 的数值顺序就是 ObjectId 规范顺序。
二分中点用不会产生索引加法溢出的写法；空集合、首尾命中、缺失 key 的 indexer/查询行为保持字典惯例。
不附加哈希表、树节点、第二份 pair 数组或热键索引；泛型实现不意味着支持额外产品键类型。

数组只能来自实现内部新建、填充后不再修改的存储。对外只暴露读取接口和 iterator，不返回可变数组或 mutable collection。
普通调用者也须遵守只读合同；不为反射、Unsafe 或刻意越过合同的访问新增防御框架。
这一小类型直接承担数据存储、查找和只读投影，无需在它外面再包 ReadOnlyDictionary 或单独的防 SyncRoot 包装。

### 3.2 构建一次，冻结后复用

首片保留现有 SortedDictionary 作为操作内构建器，继续执行相同的重复 ID、0 ID、地址、集合互斥和 replay 验证。
只提供从本模块现有、默认比较规则的 SortedDictionary 冻结的内部入口：分配精确长度的 keys/values 数组，在同一遍升序枚举中填充并核对严格递增；冻结结果不保留构建器引用。
长度对应由这个单一入口保证；原创建和 wire 入口继续承担原校验，不新增通用输入验证或数组接管后端。
StateRevision 的公开创建入口仍冻结调用方输入；不能为少一次复制而接收调用方随后可修改的数组。

这种安排先改变最终驻留表示，不同时替换 map 重放算法。
从 Dictionary 构建后最后排序、直接接管 wire reader 数组、流式合并多个 Delta，均不是本片前置任务。
后续若构建成本成为实测热点，再在相同结果与拒绝规则下独立比较。

保留 [wire writer](../../../src/DurableGraph.Storage/StateRevisionWireWriter.cs) 的升序遍历及 reader 的严格递增检查；
新表示不改变 wire 或 H。双数组新增加的冻结复制属于构建成本，不能从删除树节点推断总分配也必然下降。

### 3.3 LocalObjects 直接使用已有排序结果

[StateRevision](../../../src/DurableGraph.Storage/StateRevision.cs) 的 LocalObjects 及 local IDs 已排序，无需为 local record 再建字典或复制一组索引。
将 StateRevisionStore.FindLocalRecord 的线性扫描改为对现有排序记录的二分，保持 direct local record 缺失时拒绝，不能回退 parent。
这是帧内定位优化，不是单 ID membership 早退；完整 map、prior 和图校验仍照常运行。

## 4. Store 缓存与生命周期

### 4.1 一个正常构造路径

施工 API 采用普通构造的预算参数；示意为：

~~~text
StateRevisionStore(segments, readCacheBudgetBytes = DefaultReadCacheBudgetBytes)
  Read / ReadLiveObjectHeadMap / 原有读写入口
  Dispose()  // 只结束本 Store 并释放缓存；不 Dispose 借用的 segments

内部缓存
  Dictionary<FrameAddress, Entry> + 一条 LRU 链
  Entry { StateRevision? Revision; FrozenSortedDictionary<uint, FrameAddress>? Heads; Charge; ... }
~~~

默认预算暂取 8 MiB；0 走直读且不建缓存，负数拒绝。预算是单 Store 总预算，构造时确定，不提供在线改预算、清缓存后恢复或可插拔策略。
普通 Store 和 GraphResources 创建的 Store 使用相同实现；不再保留 GraphResources-only 内部构造，不为该接线增加 StateStore IVT，也不存 requireAvailable 委托。

StateRevisionStore 实现 IDisposable；Dispose 幂等，标记自身 disposed 并释放整个缓存对象。
它用于在 Store 结束时及时释放驻留量，即使调用方仍合法保留已结束的 Session/Repository 引用，也不继续保留缓存容量。
预算为 0 仅关闭缓存驻留，不改变双数组表示、校验或生命周期。所有公开读写入口均检查自身 disposed，包括直读路径和不读取 prior 的纯 Base Append/AppendDurably。
这不转移 segments 的所有权。调用方负责先结束 Store，再结束借用的底层；仅关闭底层后继续使用 Store 仍是合同外行为。
GraphResources.Dispose 在关闭 segments 前调用 States.Dispose；产品 fault/dispose/reentry 继续在现有宿主入口判断，缓存不反向回调宿主。
正常打开阶段的内部验证可直接使用 Store；不新增 ambient operation scope 或逐层传播上下文。
宿主 fault 后由原合同停止调用并最终 Dispose，不增加另一套缓存 fault 状态或恢复分支。

已交付的 owned revision/map 不因驱逐或 Dispose 被修改，调用方仍可读取其内容。它们不能跨救援重新充当新 Store 的缓存。

### 4.2 成功读取与准入

~~~text
Read(A):
  检查本 Store 寿命和原参数
  若 entry.Revision 存在：更新 LRU，返回
  完整走原 RBF/tag/TailMeta/wire 解码
  尝试纳入 owned Revision，返回

ReadLiveObjectHeadMap(A):
  检查本 Store 寿命和原参数
  若 entry.Heads 存在：更新 LRU，返回
  完整调用原 Materialize(A, Read)，最终冻结为双数组字典
  完成后按 A 重新查缓存并尝试纳入，返回
~~~

不递归利用祖先 cached map 改写 materializer，不作 membership 早退。
构造期间根 entry 可以被逐出；完成后按地址重新查询，不保存开始时的 LRU node，不需要 pin、epoch 或 pending entry。

准入前计算全部 charge，再做任何驱逐：

- 新组件本身超过预算，或与同地址旧组件合计超过预算：保留旧项，绕过 incoming，不扫空热缓存。
- 可准入：按增量所需空间逐出其他最旧整项，合并或新建 entry，并置为最近使用。
- 对应组件不存在就是该组件 miss；entry 至少一个组件非空，同组件不重复计费。
- 整项驱逐只释放引用，不修改值；允许 frame-only / map-only / both。

保留简单的先到者规则：两组件分别能放下而合计放不下时，留下旧组件。
不加收益预测、预算分区、组件级驱逐或第二种替换策略。append-only 不代表工作集有界，因此仍保留预算和逐出。

### 4.3 计量按新表示重算

保证是驻留项估算 charge 总和不超过预算，不是整个进程、操作峰值或精确 GC 堆上限。
新只读字典的 charge 按两个数组及其实际元素宽度计算，不沿用旧 Probe 的每树节点 64B：

~~~text
AddressMapCharge = 固定对象/两数组头部及对齐估算
                 + Count * (sizeof(uint) + sizeof(FrameAddress))
FrameCharge      = 固定 revision/record/数组估算 + owned body bytes
                 + external-head 双数组成本 + local-ID / remove 数组成本
EntryCharge      = entry/index/LRU 摊销 + 已有两组件成本
~~~

实现常数集中于 [StateRevisionReadCache](../../../src/DurableGraph.Storage/StateRevisionReadCache.cs)：entry/index/LRU 摊销 160B，map 对象 32B，数组头部 24B 并向 8B 对齐，FrameAddress 元素宽度取实际 Unsafe.SizeOf。revision 及三个 list 外观合计 168B，每个 record 估算 64B，另计各数组与 body。这是保守的 64-bit 布局估算，不是实测 GC 对象大小；乘加用 checked long。每项有正 charge，空 body 也计元数据和容器成本，不重复计算共享于同 entry 的开销。
调用方持有值、构建器和冻结时同时存活的数组、索引容量高水位及未 GC 对象不由 resident charge 精确约束。
8 MiB 是施工起点，不是已测最优容量。统计只作 internal：两类命中/缺失/成功生产、驱逐、准入绕过及 resident/peak charge。
不将 raw 缓存统计混入 DB-064 的 typed GraphReadStatistics.CacheHits。

### 4.4 验证与写入保持

raw entry 只代表 framing/tag/TailMeta/wire 解码成功；map entry 只代表完整 exact membership 物化成功。
map 首次物化继续检查完整 parent spine 和完成 Base 的全部 external 地址；命中不授予 local record、prior、Schema、typed body 或引用图有效性。
原 RequireParentHead、strict descent、真实 H、每视图引用和 Dictionary lookup 校验继续运行；新 Base 仍可合法截断旧坏内容链。

不缓存异常或永久失败标记。raw 失败不入 raw 项，map 失败不入 map 项；失败前成功读出的 raw frames 可以留下。
后续 chain/typed/图失败不必驱逐正确 raw/map；成功完整 map 中某 ID 不存在是合法 membership 结果。

Append/AppendDurably 不 seed 输入 StateRevision：CreateBase/CreateDelta 的 EncodedPayloadBytes 为 null，只有读回 wire 才得到实际地址编码的 H。
完整可读但未发布的 candidate 可被正常 Read 缓存；缓存不推断发布 head，也不按 publication 状态分区。

## 5. 已有证据及其适用范围

[早期 Probe](../../research/read-cache-probe/README.md) 在临时目录复制实际 Storage 源码，使用真实 Segment/RBF，注入候选缓存与私有只读树包装。
它复现 Decoder 的 Storage 读循环，没有 Schema/DTO/Upgrade/Hydrate、业务 Commit 或发布屏障。
六组场景、十种配置，每配置三次空内容缓存读取及紧随的重复读取。下面保留早期树表示的计数和十进制 MB：

| 场景 | 直读 wire / map / 分配 MB | 仅帧 8 MiB | 统一帧+map 8 MiB |
|---|---|---|---|
| 单对象 Base | 3 / 2 / 0.0047 | 1 / 2 / 0.0028 | 1 / 1 / 0.0023 |
| 256 对象单 Base、每 body 128B | 513 / 257 / 38.252 | 1 / 257 / 4.491 | 1 / 1 / 0.163 |
| 128 对象、24 次单对象修改 | 3677 / 153 / 14.707 | 25 / 153 / 1.762 | 25 / 25 / 0.354 |
| 64 对象、12 次全体 Delta | 6669 / 833 / 90.781 | 13 / 833 / 4.586 | 13 / 13 / 0.341 |
| 64 对象单 Base、每 body 4096B | 129 / 65 / 35.283 | 1 / 65 / 0.591 | 1 / 1 / 0.296 |
| 128 对象、32 次稀疏修改、空 body | 4945 / 161 / 10.136 | 33 / 161 / 1.871 | 33 / 33 / 0.429 |

这些结果支持 frame 与 map 复用各有独立收益；不证明双数组的构建、查找、驻留或端到端性能，更不是与 BCL FrozenDictionary 的比较。
旧 map charge 为 256 + 64 * Count；其数值和驻留阈值不能套用新数组表示。
“冷”仅指缓存内容为空，OS 页、JIT 和索引容量已预热；完整时间样本见 [results.json](../../research/read-cache-probe/results.json)。

保留三个机制反例作为新实现的验收素材，按新 charge 重设预算再运行：

- 单项都能准入，工作集仍可反复驱逐；旧 dense / 16 KiB 的单帧缓存解码 6,605 次。
- 大 frame 不能准入，小 map 仍有价值；旧大 body / 16 KiB 统一缓存把 map 从 65 次降至 1 次，wire 从 129 次降至 65 次。
- 统一预算不保证非劣；旧 dense / 64 KiB 组合缓存比分别同预算的单帧缓存多约 4.1% 分配。保留反例，不把旧精确倍率当新表示的预期结果。

原 SyncRoot 问题也是已复现事实：ReadOnlyDictionary 包住 SortedDictionary 后，可以从 map/Keys/Values 的 ICollection.SyncRoot 取得可变 TreeSet 并 Clear，见 JSON 的 ownership。
用户已选择项目内合作式只读纪律，不要求为刻意强转新增安全边界；本稿双数组类型从存储上移除了这条可变树路径，取代先前“保留树再加专用外壳”的建议。
用户此前的 FrozenSortedDictionary 重命名只解决名称歧义；本片随后实施的双数组与缓存须由下述新验收证明。

## 6. 收缩与替代方案记录

| 选项 | 当前裁决 |
|---|---|
| GraphResources-only 缓存、内部特许构造、owner callback、为接线增加 IVT | 删除；正常 Store 的 append-only/lifetime 合同已统一，普通构造与 Store 自身释放即可 |
| 活 Store 在线截断并自动发现同址新内容 | 排除正常合同；离线救援关闭旧 Store/缓存后操作，结束后新开 |
| 保留排序树并增加防 SyncRoot 外壳 | 由双数组只读字典替代；普通只读投影保留，不设计恶意访问防御层 |
| BCL FrozenDictionary / 运行时哈希与二分策略切换 | 当前不选；用户优先有限复用下的构建成本、紧凑表示和已有升序合同。只有真实点查瓶颈才重访，不预建双后端 |
| LocalObjects 线性扫描 | 改为现有排序记录的二分，不新增字典或另一套索引 |
| frame/map 两个缓存、或 frame 必须存在才能缓存 map | 继续使用同地址三态 entry、一个预算和 LRU；旧测量已有 map-only 消费者 |
| 操作上下文贯穿 Decoder/planner/Append/history | 延后；统一 Store 已覆盖这些入口，不引入 ambient scope |
| known-head / planner H 摘要 / membership 早退 / 祖先 map 拼接 | 延后；不为本次表示和缓存改动扩张验证及调用协议 |
| 1:3 门槛、每实例每地址最多解码一次 | 删除；分别缺乏鉴别力、与有界逐出矛盾 |

先前仅让 GraphResources 启用缓存，是在未约束 public caller 对底层操作的情况下作出的范围选择；
当前用户已明确收紧正常行为，因此原反例作为离线救援边界保留，不再成为缓存算法的附加机制。
二分选型不以新的模型对比或算法竞赛为前置；正确性、构建分配及产品回归仍须验证。

本次修订经需求、最小架构与语义三角色独立复核及交叉质询后收敛：保留用于及时释放缓存的 Store 自身 Dispose 和单一冻结入口，不恢复 owner callback、缓存 fault 状态或在线失效机制。

## 7. 实施顺序与验收

本片按以下依赖推进；§1 的问题与最小判据为施工合同，不另立重复工单：

1. 实现共享内部双数组 FrozenSortedDictionary；接入 ExternalObjectHeads 和完整 map 冻结，替换 LocalObjects 线性查找。
2. 实现三态 LRU、按新表示计量、两处成功准入和普通 Store 预算构造；增加 Store 自身 Dispose。
3. GraphResources 使用普通构造并先释放 States，再关闭底层；活动测试及直接使用 Store 的消费者同步改为先结束 Store、后结束底层，包括 PackageConsumerProbe 的 Inspect helpers 和 StateStoreConsumer。保留现有外层 guard，验证读取、保存和新开路径。

| 验收组 | 必须观察到的结果 |
|---|---|
| 双数组 | 空/单项/首尾/缺失及稀疏 uint ID 查询正确；key/value 对应和升序稳定；冻结后构建器或调用方输入变化不影响结果；旧重复/0 ID、集合互斥与 wire 严格顺序检查保持 |
| 只读与局部定位 | 不暴露可变数组/构建器，不另建防御框架；驱逐/Dispose 后交付值可读；local-record 二分与直接定位规则一致，不回退 parent |
| 寿命与救援 | 完整地址键含文件、offset、length；不同 Store 不串；Store Dispose 幂等且不关闭借用底层，含零预算/纯 Base Append 在内的后续 Store 操作拒绝；直接消费者先结束 Store 再关闭底层；救援结束后新 Store 读到同址新内容，不承诺旧缓存跨救援有效 |
| 预算 | 0 直读、负数拒绝、checked 正 charge；超大或组合超预算不扫空热项；三态可达；物化中根 entry 被逐出后正确重新准入；可观察边界 resident charge≤budget |
| 失败与格式 | CRC/tag/TailMeta/wire/map 失败按成功层级准入；cache hit 仍拒绝坏 prior、错 branch、缺 local record；Base 可切断坏内容链；原 wire bytes 和真实 H 保持 |
| 产品路径 | 每视图引用、nominal、Dictionary lookup、ReadPair 原子交付、Resume 可变隔离和 string 身份保持；外层 fault/dispose/reentry 检查在前；正常打开不修尾 |
| 性能 | 按新表示重跑 fitting、超大项、thrashing 控制；记录构建与冻结分配、冷热查询/遍历、resident charge；分别测 Open、Read/ReadPair、Resume 与 Prepare/Append/Commit，不宣称未经测量的 BCL 比较或端到端倍率 |

实施后运行根 solution build、Storage/StateStore 相关测试和现有 [真实包 EventHistory probe](../../../experiments/PackageConsumerProbe/EventHistoryRecoveryConsumer/README.md)。
产品能力只在实现验收后更新；早期基线测试与树表示 Probe 不代替本片新证据。

### 7.1 实施与验证映射

| 要求 | 实现落点 | 验证落点 |
|---|---|---|
| 双数组、升序与 owned 冻结 | FrozenSortedDictionary、StateRevision、LiveObjectHeadMapMaterializer | [字典测试](../../../tests/DurableGraph.Storage.Tests/FrozenSortedDictionaryTests.cs)、现有 Revision/materializer/wire 测试 |
| local-record 二分、成功读回准入、真实 H | StateRevisionStore | [缓存验收](../../../tests/DurableGraph.Storage.Tests/StateRevisionReadCacheTests.cs)、ObjectVersionChainStoreTests |
| 三态整项 LRU、单预算、诊断 | StateRevisionReadCache | 缓存验收的命中提升、超大/组合超预算、物化中驱逐场景 |
| Store 自身释放、借用寿命、离线新开 | StateRevisionStore.Dispose、GraphResources.Dispose、活动直接消费者 using | 缓存验收、[GraphResourcesTests](../../../tests/DurableGraph.Persistence.Tests/GraphResourcesTests.cs)、真实 PackageReference Probe |
| typed/引用/Dictionary/ReadPair/Resume 语义 | 原 Decoder、GraphReader 与宿主入口 | StateStore 与 Generator 集成回归 |
| 新表示及实际产品测量 | [Storage 测量](../../../tests/DurableGraph.Storage.Tests/ReadCacheMeasurementTests.cs)、[产品测量](../../../tests/DurableGraph.Persistence.Tests/ReadCacheProductMeasurementTests.cs) | 全 body/H checksum、驻留预算，以及分阶段时间/线程分配样本 |

诊断通过 internal ReadCacheStatistics 值快照读取；0 预算仍计 miss 和成功生产，未分配缓存。Dispose 释放整个缓存对象，之后缓存专属的驻留/驱逐/峰值诊断归零；如需保留最终诊断，先取得快照。诊断不成为公共 API。

### 7.2 新实现测量：2026-09-12

完整的 42 个样本及源码 SHA256 保存在 [read-cache-implementation-results.json](../../research/read-cache-implementation-results.json)。
Windows、SDK 10.0.201、runtime 10.0.5、Release，关闭 tiered compilation；每格先丢弃一轮，再保留三轮。
Atelia 使用当时工作树（包含既有 SegmentStore rotation/options 修改，关键文件哈希已记录），本任务没有修改上游。

Storage 的首次读取从新 Store 的空缓存开始；OS 页及 JIT 已预热。每轮完整 map 后逐 ID 读取内容链，比较全部 body 字节与真实 H 的 checksum。
下表为首次读取；计数和该表分配在三个样本中一致，字节均为十进制。重复读取的统计在 JSON 中为累计值，须减去 FirstStatistics。

| fixture / 预算 | wire 解码 / map 物化 | 线程分配 B | 最终 resident charge B |
|---|---:|---:|---:|
| fitting：32 对象、64B body、2 次全体 Delta / 0 | 291 / 97 | 2,381,792 | 0 |
| 同上 / 8 MiB | 3 / 3 | 44,320 | 19,344 |
| 同上 / 5,728B（仅够一帧） | 256 / 96 | 2,167,256 | 5,728 |
| oversize：16 对象、4,096B body、单 Base / 0 | 33 / 17 | 2,296,344 | 0 |
| 同上 / 8 MiB | 1 / 1 | 75,200 | 68,016 |
| 同上 / 1,120B | 17 / 1 | 1,171,264 | 560 |
| thrashing：16 对象、128B body、6 次全体 Delta / 0 | 567 / 113 | 3,070,696 | 0 |
| 同上 / 8 MiB | 7 / 7 | 62,576 | 31,696 |
| 同上 / 4,128B（仅够一帧） | 544 / 112 | 3,027,928 | 4,128 |

8 MiB 下三种 fixture 的紧随重复读取均为 0 次新解码、0 次 map 物化。oversize 小预算仅驻留 map，重复读取仍需 16 次 raw 解码；thrashing 小预算重复读取仍为 544 / 112。
这证明本实现的复用与边界行为，不证明物理 I/O、峰值存活内存或任意预算下非劣。

构建仍先用 SortedDictionary，冻结分配包含复制与构建器枚举的临时开销：

| 项数 | 构建树分配 B | 随后冻结分配 B | 双数组 map 的 resident charge B |
|---:|---:|---:|---:|
| 1 | 176 | 176 | 104 |
| 64 | 4,208 | 1,512 | 1,360 |
| 256 | 16,496 | 5,384 | 5,200 |

查询包含稀疏 key 命中/缺失与一次完整升序遍历，另测重复 100 轮；时间样本见 JSON。
每轮组合查询/遍历分配 56B（pair iterator），不声称零分配。冻结增加一次构建分配，不能把驻留紧凑误写成单次总分配下降；没有与 BCL FrozenDictionary 做比较。

产品 fixture 使用 17 个 Node、List 和共享 string，三次 State 修改及最终 PendingEvent；前三个 Event 独立，最后 PendingEvent 的共享内容保持只读，关闭重开后由 Resume 验证可变隔离。
下表分别取三个样本的时间和分配中位数；完整 Commit 包含 Prepare 和 StateAppendDurably，不能把这些行相加。

| 实际产品阶段 | 中位 ms | 中位线程分配 B |
|---|---:|---:|
| OpenReadOnly | 22.42 | 846,896 |
| Open 后首次 ReadState | 0.34 | 135,824 |
| 紧随重复 ReadState | 0.21 | 135,576 |
| 已读后的 ReadPair | 0.29 | 212,008 |
| 紧随重复 ReadPair | 0.30 | 212,008 |
| OpenWritable | 821.19 | 1,540,328 |
| Open 后 Resume | 0.83 | 216,464 |
| Prepare（含入口 preflight） | 0.34 | 150,000 |
| StateAppendDurably | 204.33 | 1,120 |
| 完整 State Commit | 576.75 | 230,392 |

Open 本身会验证历史并预热缓存，所以 Open 后首次 Read 不叫冷缓存。产品只测默认预算，没有禁缓存对照；这些阶段成本不支持端到端加速倍率，也不能外推真实冷盘或其他机器的持久屏障耗时。

重跑新实现测量（在独立 PowerShell 会话中，输出含 DB067_MEASUREMENT JSON）：

```powershell
$env:DOTNET_TieredCompilation = '0'
dotnet test tests/DurableGraph.StateStore.Storage.Tests -c Release --filter FullyQualifiedName~ReadCacheMeasurementTests --logger 'console;verbosity=detailed'
dotnet test tests/DurableGraph.StateStore.Tests -c Release --filter FullyQualifiedName~ReadCacheProductMeasurementTests --logger 'console;verbosity=detailed'
```

### 7.3 集成验收

- 实施前基线：根 solution build 0 warning/error；Storage 155/155、StateStore 711/711。
- 集成后：Storage 187/187、StateStore 713/713、Runtime/Generator 1,534/1,534、Serialization 163/163，全无跳过；Release 独立测量 4 + 1 项通过。产品测量的 Event alias 合同问题经独立审阅修正，修订后的 Release 用例通过。
- 两条独立审阅分别核对缓存/持久语义与验收/消费者迁移，最终无剩余阻塞意见；wire reader/writer/format 与 FrameAddress 源码未改，原 golden/拒绝测试保持。
- 真实 PackageReference：Run-StateStoreProbe.ps1、Run-EventHistoryRecoveryProbe.ps1、Run-EventHistoryProbe.ps1 均通过；后两者复用首个 Probe 生成的同版九包 feed。覆盖直接 Store 消费、冷读/升级续写、ReadPair/Resume、包内文档和重复恢复/只读浏览不写入。
- 最终根 solution build 0 warning/error；553 个相关文档本地链接和四个新增/新引用锚点通过，测量源码哈希与最终源码一致，暂存 diff 空白检查通过。历史 Probe JSON 只统一 LF 换行，保留原始数据。
- 本片不改持久格式、发布权威或上游库，不引入 owner callback、StateStore IVT、在线失效、祖先 cached map 拼接或 BCL 字典双后端。后继优化仅在真实工作负载证据触发时重访。
