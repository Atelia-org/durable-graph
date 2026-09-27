# DB-064：两份 Revision 合并读取与安全共享边界

> 状态：阶段 A/B 已实施，2026-09-11；阶段 0 内部顺序读取随 DB-062 实现，公开 API 随 DB-063 交付。
> 阶段 0 的内部入口纳入 [DB-062](0062-independent-graph-workspace-slice.md)，公开 API 纳入 [DB-063](0063-event-history-journal-slice.md) 首版。
> 用户本轮已调度阶段 A/B；保持现有 ReadPair 只读合同与可写 Resume 隔离，不改持久格式。

## 本轮施工与验收边界

问题是同一读取操作能否减少 exact body 解码与只读实例分配，同时保持每份 Revision 的引用解释和完整验证。
阶段 A 使用封闭资源/模型 snapshot 的内部 `RevisionReadSession`，按 `(ObjectId, head)` 缓存 owned stored row 与 reader；
每份 `DecodedRevision` 内部保留实际 head map。阶段 B 在 `GraphReader.ReadPair` 中核对 current body 后计算最大安全引用闭合集，
与可写 `MaterializedGraph` 导入路径分离。`Resume` 只使用阶段 A，State/Event 各自分配可变对象。

| 工作包 | 代码接缝 | 最小可观测验收 |
|---|---|---|
| exact 解码缓存 | RevisionDecoder / RevisionReadSession / DecodedRevision | 同 key 一次 body 解码；cache hit 仍拒绝另一视图的坏引用/lookup |
| 只读引用闭包共享 | GraphReader | 稳定环只分配/填充一次，changed-child 和同布局 Normalize 改值使依赖者分裂 |
| 可写恢复接线 | WorldWorkspace / EventHistoryRepository | Pending Event 与 State 可变图隔离；同版 string 不导致续写新 ID 或额外 Base |
| 集成与交付 | StateStore tests / EventHistory PackageConsumer | 真实 E/S heads、容器引用、冷重开、零写入、第二边失败不交付 |

缓存仅活于一次操作；没有公共 cache/matcher 配置，不引入 Frame cache、跨操作缓存、多视图分区、内容 intern 或 wire 变更。
验收使用内部 decoded/Allocate/Hydrate/shared 计数和真实包行为；不同于物理 I/O、实际 CLR 堆分配或峰值内存。

## 阶段 0：已交付的实验性双图 API

用户已选择 API 先行、算法后置。目标是与 DramaBoard 早期磨合调用方式，首版允许两次独立还原，
不以缓存命中率、共享实例数或加速比例作为交付条件。

候选语义：`ReadPair<TFirst,TSecond>(firstSelection, secondSelection, models)` 返回有明确 First/Second 的结果。
DB-062 内部 selection 是同资源所有者下的 `(RevisionAddress, RootId)`；
DB-063 公开 selection 使用该仓库的 frame handle，应用不自己维护 DTO、ID map 或发布状态。
两个输入均显式指定，不自动解析为“最新”；保持一次操作的同一冻结模型目录、资源范围和稳定读取条件，
但初版两条 Decode/Normalize/Allocate/Hydrate 路径各自独立执行。

后续 API 打磨提供 `ReadPair(first, second, models)` 非泛型重载，返回 `(DurableBase First, DurableBase Second)`。
实际根类型完整保留，位置对应输入，不按 State/Event 角色重排，也不要求两个选择相邻或异类。
调用方以 frame.Kind 判断角色、以模式匹配使用领域类型；泛型重载保留为已知类型时的校验入口。
该重载直接调用既有共享读取管线，不改变提交拓扑或持久格式。
真实包覆盖 State/Event、Event/State、Event/Event 和 State/State，既有公开读取回归覆盖同帧与错误来源 handle。
此重载增补经 32 个 EventHistory 回归、真实包双进程 Publish/Verify 和根构建验证通过；构建 0 警告/错误。

从首版起固定以下行为：

- 每份图自身的值、actual 类型、共享/循环与 Revision 解释正确；不同版本不能为了省内存混为一个，Empty 沿既有例外。
- 两份输出均按只读快照使用；库不冻结 CLR 对象，使用者不得通过其中一份修改可能被另一份共享的内容。
- 不承诺跨图 ReferenceEquals 为 true，也不承诺它为 false；跨图引用相等性不能作为业务身份、版本或程序控制条件。
- 同一输入传两次仍返回两个有明确位置的结果，不规定是否为两个实例；两边读取均成功后才交付 pair。
  第二边失败不交付半个结果，但不承诺回滚已经执行的用户回调副作用。
- 不发布、不更新 ref、不安装可写基线；独立 ReadEvent/ReadState 保持独立入口。
- 可写 Resume 可以复用双图编排，但使用隔离可变对象的内部恢复路径；将来的只读共享结果不能直接安装为 State 工作区。
- 实例共享只是允许的优化，首版不开放具体 matcher/cache/共享算法枚举，不引入通用多视图平台。

阶段 0 验收：输入与返回位置对应、异构根类型校验、不同版本保留各自值、图内共享/环、
第二图损坏不交付 pair、只读操作零写入、相同输入两次合法、错误仓库 handle 拒绝。
消费者测试不断言跨图 ReferenceEquals；阶段 A/B 后续可用内部观测单独证明优化生效，不能反向把它变成 API 保证。
实际 public 签名可随示范应用反馈调整，无旧 API 兼容负担；这里先明确行为约束。

## 1. 要修正的判据

用户提出：一次读取两份 Revision，尽可能共用相同版本的对象，并保持不同版本的领域快照独立。
目标合理，但现有 ObjectVersion 仅确定自身 DTO，不包含所有引用目标的版本：

```text
R1: A(head=a1).Next = ObjectId(2)     B(id=2, head=b1).Value = 10
R2: A(head=a1).Next = ObjectId(2)     B(id=2, head=b2).Value = 20
```

A 自己的字段没变，两个 Revision 复用 a1 完全正确；但已 Hydrate 的一个 A 只有一个 Next，
不能同时指向 B 的两个版本。即使 A 的 Next 字段是 readonly，问题仍存在。
引用版本是 Revision 的解释结果；不应为简化加载而给每个祖先重写 ObjectVersion，破坏现有增量语义。

这不是新假设：[EventHistoryRepositoryTests](../../tests/DurableGraph.Persistence.Tests/EventHistoryRepositoryTests.cs)
的 `ThreeCommitsRetainInstancesAndAdvanceExactParentThenReopenFromPublishedWorldId` 已写出只 child 变化、root 不写且有循环的见证；
[LoadedReferenceWorldTests](../../tests/DurableGraph.Persistence.Tests/LoadedReferenceWorldTests.cs)
检验未变 owner 的引用仍按当前 Revision 验证。

后续算法必须遵守的安全边界为：

- 同 exact ObjectVersion 可以共用 stored DTO 解码结果。
- 不同 ObjectVersion 不因内容相等而合并实例。
- 同 ObjectVersion 的普通 CLR 实例只有在完整引用闭包也可共享时才合并。
- 一侧可编辑时，不因初始值相同就共享会被修改的对象；后续最多机会性共享 DTO 和 string，不承诺命中。
- Empty 继续沿全库既有 string.Empty 例外；不同 ID 的等值非空 string 不做值 intern。

## 2. 阶段 A：操作内共享 stored DTO 与 string

读取一次两份明确 `(RevisionAddress, RootId)` 的图，固定同一资源所有者与模型/reader snapshot。
复用 key 是 `(repository scope, ObjectId, head FrameAddress)`：
Frame 是多个对象的共同容器，只有 FrameAddress 不够；只有 ObjectId 也不够。
同一读取操作中 repository scope 可由所有者封闭，不必持久化一个新全局 ID。

为每份视图分别物化 ObjectHeadMap；按 exact head 查缓存。
首次命中前完成原始链读取、Base 表示/reader 匹配及 body 全消费，缓存拥有独立内存的 ObjectStateRecord。
不在缓存中保存需要文件 lease 继续存活的 span/buffer，不跨仓库或不同 reader snapshot 搬用绑定结果。

先只缓存 stored DTO，不缓存 NormalizedRevision、业务 Upgrade 的结果、领域实例表或 Capture 绑定。
每份视图仍独立执行：

1. stored 引用、名义类型与 Dictionary lookup 的完整验证；
2. 单对象 Upgrade 及现有实时 Schema dependency closure 复核；
3. current DTO 的引用/lookup 验证；
4. 按根求可达集合及两阶段 Allocate/Hydrate。

缓存命中不携带“已在另一个 Revision 验证通过”的通行证。
同 DTO 在一边可合法、另一边可悬空或目标类型不匹配，后者仍须拒绝。
升级可能删边，source live 验证与 current reachable 仍保持区别；不因为共享而绕过不可达历史行的原校验。

string DTO 自身即不可变 string；保留同 key 首次解码实例即可获得安全共享，
两份 graph 的可变 class/array/List/Dictionary 默认分开 Allocate。
可写 Resume 只导入 State 那份 baseline/ID 表，Event 从不变成它的可变别名。

缓存只活到本次读取结束，输出图持有其所需实例；无全局 weak cache、LRU、池或对象永久驻留。
这是“同版本解码一次”的直接收益，不承诺两份图所有相等内容都去重，也不承诺每个 RBF Frame 只读一次。

## 3. 阶段 B：显式只读双图的闭包共享

这一阶段有清晰可行的线性图算法，不需要解决一般图同构；使用阶段 0 已建立的公开只读合同。
不自动用于会继续修改的 State，也不由 readonly 字段、record 或 IReadOnlyList 推断深不可变。

在同一实验性 ReadPair 入口内部逐步加入闭包共享，调用方继续将两图全部视为只读。
框架不自动冻结普通 CLR 对象；修改已共享对象会在另一视图可见，不能再将其声明为隔离编辑图。
该调用结果不暴露导入 EventHistorySession/WorldWorkspace 可写基线的入口；未来深不可变类型白名单若有需求另定。

### 共享集合

分别按阶段 A 得到 validated current DTO 与可达集，先形成候选 C：

- 两边都可达，ObjectId 与真实 head 相同；
- 同一冻结代码目录，current model/完整 layout 匹配；
- 两边完整 current DTO 状态相同，包括容器 comparer metadata；初版可复用各自 preparation 生成 current Base body，
  逐字节相同作为充分条件，只比较这一对临时结果而不写盘。不同字节可以保守不共享，不做 hash-only 判断。
  这会多一次编码/缓冲成本；待实际测量再改为已有完整 StateEquals 的内部适配，不为首片新增公开接口；
- 首版保守要求未执行布局升级（两边 RequiresRewrite 都为 false）。升级后即便偶然相等也分别分配，
  避免为共享增加第二套跨版本状态等价判定或业务回调执行次数约定。

`RequiresRewrite` 仅由 source/current layout 差异决定，不证明 Normalize 没改值。
现有手工 StateModelBinding 可提供自己的 Normalize；不得仅凭该标志跳过上面的完整 current 状态核对。
string 单独按阶段 A 的同 key/同实例条件处理；不能取得完整状态比较能力的 binding 保守不共享。

对每个候选访问 current DTO 引用边；null 不产生边。任一目标不在 C，就将该候选标为不能共享。
构建反向引用表，把“不能共享”向所有引用者传播，直到队列为空。
剩余是这些保守候选中的最大引用闭合集：稳定循环可整体保留；一个节点分裂会传播到所有依赖它的祖先/环。
图剔除本身的时间与临时索引空间为 O(V+E)，V/E 取两份已加载图中的节点/引用数；
前述 current 状态比较另计编码字节量与临时 body 成本，不将总加载工作虚报为纯图算法成本。

然后先分配所有共享与各视图独立实例，再分别建立 ObjectId→实例表；共享节点只 Hydrate 一次。
其全部非空目标均已证明共享，任取一份对应表填充即可；独立 owner 可引用共享 child。
失败不交付任何部分图。数组元素、List/Dictionary 内容和嵌套 struct/Nullable 中的引用一律通过现有 VisitReferences 纳入。

暂不对一对多视图做一般分区细化，不进行内容 hash 合并，不为提高命中率追加任意自定义“immutable”回调。
对于 source/current 相同但布局刚升级的对象，允许漏掉共享机会，不能牺牲正确性。

## 4. 阶段 A/B 的优化验收

下表用于特定算法的内部测试，不是阶段 0 的交付门槛，也不构成下游可依赖的实例复用保证。

| 场景 | 必须结果 |
|---|---|
| 两份图复用相同 `(ID,head)` | 阶段 A exact body 解码一次；每视图的引用校验依然执行 |
| 同一个 Frame 的两个对象 | 两份缓存行，不得因 Frame 相同互相覆盖 |
| 同 ID、不同 head，包括同值的重写 Base | 不合并；不能用 layout/内容相等代替版本身份 |
| 相同非空 string 的同版本 | 两图 ReferenceEquals；另一个 ID 的等值非空串保持不同，Empty 沿现有规则 |
| owner head 未变、child head 改变 | owner/child 在两图中各自指向正确版本，不能共享 owner |
| 稳定环与含变化节点的环 | 阶段 B 稳定环共享；变化沿反向边传播后正确拆开，无无限递归 |
| DTO 命中但第二视图引用悬空/错 nominal | 第二视图失败，不暴露第一视图作为成功 pair |
| Upgrade 改边、current 删除旧 child | 各视图保持完整 source 验证；首版升级节点不进入 CLR 共享集合 |
| Resume 后修改 State child/List | 已加载 Event 旧值不变；默认模式不得共用可变 child/List |
| paired Resume 后 State 未改立即保存 | 不因 string 缓存引入新 ID、引用槽变化或额外 string Base；DTO.StringContent、领域实例与绑定表使用同一个字符串 |
| 同布局手工 Normalize 产生不同 current 值 | RequiresRewrite=false 也不能共享 CLR 实例；完整 current 状态比较排除此候选并向引用者传播 |
| 字典 key/value、数组元素、Nullable/struct 引用 | 访问所有实际引用；不能只沿 durable class 的直接字段判断闭包 |

阶段 A/B 各自记录 decoded row 数、Allocate 次数与 retained instance 数；wall time、分配与峰值只在实际样例中报告。
独立 Event 读取的验收仍由 DB-063 保持；不为利用缓存先加载无关 State。
真实包用从 DB-062 快照复用的真实 heads 验证 E/S，另用相邻 StateRevision 验证 changed-child 反例。

## 5. 实现选择与后继

DB-062/063 先交付 API；本轮按用户后续调度实现阶段 A/B，无须修改 public 签名。
读取时仍分别物化两份完整 live map、Normalize 与 current 可达视图，不缓存升级结果。
闭包候选会临时编码两份 current Base body；只读字段、record、领域 Equals 都不代替完整状态核对。
Dictionary 的 Application comparer 不额外排除：本次模型 snapshot 已按闭合类型缓存成功 resolver 结果，
业务比较稳定且不依赖未恢复内容/Transient 的既有约束仍适用；comparer 标签参与 Base body 比较。

普通分配仍须提供 exact 类型的新实例。只读 pair 在任何 Hydrate 前完成两表分配及跨表唯一性检查；
共享节点走显式复用路径，Empty 保留既有例外。可写 Resume 的两条独立恢复路径也共用操作级可变实例防重索引：
若手工 allocator 意外返回前一图实例，在第二次 Hydrate 前拒绝，不交付会话。已执行回调的副作用不回滚。
防重索引和 DTO 缓存都随本次操作结束释放，不作为跨操作领域实例缓存。

后续让 DramaBoard 试用 ReadPair 与 Resume，反馈实际读取与内存压力。
Frame/map 缓存、减少 current body 临时编码、升级后实例共享和多视图分区仍待独立需求与测量，
不改变只读合同或可写会话隔离。内部优化测试会证明本实现的特定命中，消费者仍不能依赖跨图实例身份。

DB-062/063 的已选保存拓扑仍可复用磁盘中真实未变的 ObjectVersion，独立于内存共享优化是否已经实现。

本片不要求更换引用 wire、生成每个对象的传递版本、历史新 ID 分配器或通用冻结框架。
从 `(ObjectId,head)` 到“完整引用环境”的区别属于现有语义本身；不能通过改名 ObjectVersion 消除。

## 6. 实施与验收记录（2026-09-11）

实现入口为 [RevisionReadSession](../../src/DurableGraph.Persistence/RevisionReadSession.cs)、
[RevisionDecoder](../../src/DurableGraph.Persistence/RevisionDecoder.cs)、
[GraphReader](../../src/DurableGraph.Persistence/GraphReader.cs)；
[WorldWorkspace](../../src/DurableGraph.Persistence/WorldWorkspace.cs) 与
[EventHistoryRepository](../../src/DurableGraph.Persistence/Repository.cs) 接通仅阶段 A 的冷 Resume。
缓存按完整 head 的重建结果复用；不同 head 的链即使共享 Base 前缀，仍分别重建。

| 证据 | 结果与边界 |
|---|---|
| [RevisionReadSessionTests](../../tests/DurableGraph.Persistence.Tests/RevisionReadSessionTests.cs) | 10 cases：实际 ID/head、同 Frame 多 ID、Delta 链、等值新 Base、owned 数据闭库后可用、cache hit 仍验证引用/nominal/Dictionary lookup、坏 head/未全消费拒绝、不跨操作缓存 |
| [SharedGraphReaderTests](../../tests/DurableGraph.Persistence.Tests/SharedGraphReaderTests.cs) | 17 cases：双向选择、稳定/变化环、同值新版本、独立 owner 共享 leaf、Normalize 改值/边、升级与 source 验证、晚期失败、singleton、非空 string 与 Empty 例外 |
| [SharedEventHistoryTests](../../tests/DurableGraph.Persistence.Tests/SharedEventHistoryTests.cs) | 3 cases：Pending 与 State 的 child/List 可变隔离；同版 string 复用后 ID 不漂移，未改立即提交零 local object writes；错误 allocator 拒绝且仓库仍可重试读取 |
| StateStore suite | 聚焦 74、全量 702 通过，0 失败/跳过；包含既有独立 Event、错误 handle、发布与严格只读回归 |
| Runtime/Generator suite | 全量 1519 通过，0 失败/跳过；两套全量合计 2221 个测试 |
| 根构建与审阅 | `DurableGraph.slnx` build：0 警告/错误；独立实现/测试审阅无阻碍项，受影响 Markdown 本地链接与锚点校验通过 |
| [真实 PackageReference 消费者](../../experiments/PackageConsumerProbe/EventHistoryConsumer/README.md) | V1/V2 独立进程、9/11 份 immutable history Publish/Verify 通过；真实 E/S 复用 heads、changed-child、稳定环、array/List/Dictionary key/value、独立 generic holder 中 inline/Nullable 引用、readonly 文件不变、Resume 编辑隔离 |

执行入口：

```powershell
dotnet test tests/DurableGraph.StateStore.Tests/DurableGraph.StateStore.Tests.csproj --no-restore -v:q
dotnet test tests/DurableGraph.Tests/DurableGraph.Tests.csproj --no-restore -v:q
./experiments/PackageConsumerProbe/Run-EventHistoryProbe.ps1
dotnet build DurableGraph.slnx --no-restore -v:q
```

内部稳定二节点环见证：两份图总共 decoded=2、cache hits=2、Allocate=2、Hydrate=2、shared=2；
变化环 decoded=3、cache hits=1、Allocate=4、Hydrate=4、shared=0。
这些是调用与对象行计数，不能等同于 CLR 堆分配或 RBF Frame 读取次数。

真实包单次样例观测（`shared-read-metrics.txt`，run `event-history-20260911131936-53716-baee7149`）：

| 选择 | 分别读取保留实例 | ReadPair 保留实例 | 分别读取托管分配 bytes | ReadPair 托管分配 bytes | 分别读取 / ReadPair 毫秒 |
|---|---:|---:|---:|---:|---:|
| S0/E1，整图 heads 复用 | 41 | 21 | 953184 | 559176 | 98.298 / 8.976 |
| S0/S1，仅 child head 改变 | 40 | 28 | 893320 | 542296 | 4.624 / 7.537 |

保留实例是本 fixture 的可达引用实例数，含 class、容器、string，不含 inline 值。
S0/S1 的 root 不共享，稳定环/array/List/Dictionary 共享；这些是实现观测，不是 API 身份保证。
每组只测一次同步调用，包含绑定/JIT 顺序影响；第二组也展示了额外比较成本可能使读取更慢。
未测峰值内存、物理 I/O，不据此推导普遍速度或内存比例；性能后继按路线图的实际需求触发。
