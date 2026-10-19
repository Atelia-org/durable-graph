# DB-096：管理目录复用 Dictionary 持久化

> 2026-10-19，B2 的目录接线合同。用户已接受复用现有增量序列化，替代 D/T 专用整表 codec；窄管理读取切片见 §6，Repository 的新目录写入与发布接线尚未实施。它收窄[整体切换计划](branch-cutover-plan.md)的管理载荷，不改变 Branch/Commit、继承历史或创建登记语义。

## 1. 选择与边界

D 仍决定正式业务成员，T 仍提供不可变名称书签，S 仍保存唯一 Schema Catalog。D/T 的内容进入 FrameStore 中各自的管理 Meta 图，保留 Head 只发布该图入口。管理目录不是业务 Branch，也不是另一套可演进 Schema 策略。

| 角色 | 内存逻辑形状 | 持久解释 |
|---|---|---|
| D | `Dictionary<uint, string>`，ScalarDefault | key 为上游 HeadId codec 的4字节按 UInt32LE 装载得到的稳定编码值，绝非本地文件号；value 为 DisplayLabel。重复标签合法，不含 current/summary/origin |
| T | `Dictionary<string, byte[]>`，StringComparer.Ordinal | value 为上游 HeadEntryId codec 的准确12字节；名称不可重绑。写入取得独立数组，内部数组不外泄；读出转换为 CommitId |
| S | 原 SchemaCatalog 图 | 不把目录模型注册进应用 Catalog，不扩大原有 Schema 员工闭包 |

选择 T 的 `byte[]` 是为了复用已存在的纯内建 Array 与 Dictionary。每个书签增加一个小数组对象是已知成本；换成固定元组或新增员工值对象需要额外模型/绑定和支持证明，暂不值得。此表示只属于内部目录，不抢先扩展公共 BranchId/CommitId 的 durable 字段支持。数值装载只在 D 的内部边界发生，调用 HeadStore 时仍经过其 ID codec，不重新实现 ID 编码。

复用现有 Dictionary 的布局、比较器、Base/Delta、字符串与数组对象、引用验证及物理增量选择。**不新增目录容器协议、目录专属 Delta 算法或一套按名字排序的整表格式。** 仍需少量角色验证与 owner 内准备/发布接线；“复用序列化”不等于直接调用当前要求业务 `IDurableObject` 根与 Schema 的 `BusinessPersistenceAdapter`。当前 CaptureContext/BusinessWorkspace 也没有普通 Dictionary 根的直接入口；首选仿照 SchemaCatalogWorkspace 的窄管理构造方式准备冻结 rows，复用 Dictionary binding/body 与 ObjectWritePlanner，保留既有对象身份及真实 H，不在本片引入泛化 Capture 根 API。

## 2. Head 小入口与初始化

```text
D = "P0R3" + hasGraph[1] + (GraphAddress[12] if hasGraph == 1)
T = "P0T2" + hasGraph[1] + (GraphAddress[12] if hasGraph == 1)
```

`hasGraph` 只能是0或1，长度必须恰好5或17字节；地址复用既有 FrameAddress 字节原语，拒绝非法地址、截断与尾随。这里保留的是固定入口，不是新的容器 codec。S 的入口语义不变。

`hasGraph=0` 精确定义为初始空目录。创建库仍先创建 D/S/T 三个必需控制 Head，再读齐其 Entry 和入口，随后建立 GraphRecordStore/cache。缺失控制 Head 是初始化未完成，损坏入口是错误，均不能当作空目录或自动续建。首次新增项才写真实 Dictionary Base 图；首版不支持删除，不需要“非空后重新回到 null”的路径。

此选择避免为一张尚无项目的目录预写空图，或引入“控制 Head 初始化后还必须再发布一次才能完成创建”的协议。正常打开先完成 backing store 正常恢复和必要控制根读取，再恢复目录图；不预开所有业务 Head，也不执行全库审计。旧 D/T 格式由新标记拒绝，不兼容读取、不重写完成帧。

## 3. 管理图读取与来源

当前 [TypedBaseBodyCodec / StoredGraphDecoder](../../../src/DurableGraph.Persistence/StoredGraphBodies.cs)已经理解纯内建 Dictionary/Array 布局，但默认 Meta reader 的 `models == null` 路线只接受 [BootstrapBindings](../../../src/DurableGraph.Persistence/BootstrapBindings.cs)的有限员工集合。已有纯内建 Dictionary 测试使用业务 State 图加无 Schema 环境，不能直接证明默认 Meta reader 能读目录。

产品接线采用 **D/T 窄管理入口＋固定内部 builtin environment**，共用现有 decoder 的逐对象链循环；不把 D/T 类型追加进共享员工表，也不放宽 S 的默认 reader。目录环境没有应用模型、升级委托或 comparer resolver。仅给现有 Read 传一个非 null environment 还不够：它会在处理历史 Base.SchemaRoot 时直接调用 SchemaCatalogView.Load，绕过 environment 的 schemaLookup。窄入口须在该动作之前拒绝管理链的动态 Schema，并在调用 reader 前验证有限布局；不预扫后重复解码，也不新增通用读取策略框架。读取至少检查：

- 当前图为 Meta，selected Schema 为空；物理 source、准确 prior 与来源图角色继续由图层验证。
- 每条已取得对象链的 SchemaRoot 均为空，至少首 Base 必须在任何 SchemaCatalogView.Load 前被检查；以 catalog=null 解 typed Base，再核固定布局集合。当前图 SchemaRoot=null 不能替代历史 Base 检查；无 Schema 查询委托也不能单独证明此隔离。
- 根精确为对应 Dictionary 布局，D 比较器为 ScalarDefault，T 为 StringOrdinal；不能只验证“某种 Dictionary”。
- 所有行，包括不可达行，属于对应有限闭包：D 只允许该 Dictionary 与 string；T 只允许该 Dictionary、string 与 `byte[]`。继续验证引用、重复查询键与实际布局；不接受额外业务类型或 Schema 员工对象混入。
- D 的 key 经上游 codec 可解成合法 HeadId，标签为1..1024个 UTF-16 code unit 的有效 Unicode，不 trim/normalize，文本可重复。T 名称沿用有效 Unicode、Ordinal 与不可重绑规则，值必须准确12字节并可解成 HeadEntryId。

角色值约束适用于每个对应 Dictionary 的条目，包括不可达 Dictionary；独立且未被条目引用的 string/byte[] 只接受既有内建布局与内容校验，不凭布局推断它一定是标签或书签值。后者不构成公开目录项目，也不额外获得业务资格。

解码 ID 不等于校验它在当前库已发布；D 正式成员的具体描述查询、T 的 ResolveTag 仍走既定 HeadStore/业务 Entry 资格入口。不能把冷开目录变成逐个打开全部业务 Head 的审计。

每个管理图的 `PreviousMeta` 只指向**该目录自身已接受的前版**，D、S、T 互不充当增量来源。D/T 的 accepted baseline 持有真实 snapshot、对象身份、当前行和准确 prior/H；冷恢复后沿用这些身份准备下一次变化，不能每次重建所有 string/array 对象导致退化为全 Base。业务 Graph 删除 Parent 的决定不影响这些管理图的物理来源。

## 4. 准备、追加、发布与安装

1. 在仓库既有串行操作 guard 内验证参数、重复名、准确 Commit 和当前基线。目录候选与已接受视图分开；不可先改 accepted Dictionary 再尝试持久化。
2. 复用容器准备与既有 Base/Delta 选择器生成候选：只有新增标签/名称/定位值需要新对象；未变项目和字典对象身份保留。选择 Base 或 Delta 由现有大小/H 策略决定，不承诺每次新增都选 Delta。
3. 追加管理图，准备其安装材料；保留已有所有完成帧。图地址写入对应 Head 的小入口。候选追加成功还不等于已登记。
4. 发布所需 Head 后才安装全部 accepted baseline、snapshot 与 HeadEntryId；D/S 联合发布不能先安装其中之一。Unknown、发布后安装/交付失败依既有合同停用整个 owner，不能继续使用半安装缓存。

创建协议保持：fork 先 CreateHead，再追加 D 候选图并发布 D；新根先准备真实业务图与必要 S 候选、CreateHead 得首 Entry，再发布 D 或 D/S。创建成功可列出且空 fork 可立即预览。普通业务保存只发布 B 或 B/S，不写 D；CreateTag 只准备/发布 T。未登记 Head 和未发布候选图不自动清理、认领或修复。

这一步不引入通用 Meta workspace 框架。可在 RepositoryResources 内维护两个窄目录状态，抽取确实相同的编码/准备小函数；复用底层容器机制，不为了统一业务保存、Catalog 和管理目录强造新的公开层级。早期验证失败、追加后失败、NotAttempted/Unknown/Confirmed 和安装失败应沿用当前 owner/outcome 边界，不能因“只是目录”放松。

### 4.1 下一片的最小状态与安装轮廓（尚未实施）

经源码复核，首片可进一步减少状态：D/T 分别持有一个可空的 `DecodedGraphRows` accepted baseline，初始无图时为 null。它已经包含实际 snapshot、冻结 rows、对象 head 与重建成本 H；不再并行维护另一份可编辑的 CLR Dictionary 作为持久化权威。查询索引可由这些 rows 派生，不能独立推进。实际控制 HeadEntryId 仍由 RepositoryResources 持有，目录候选不新增 publication owner。

候选只需保留角色、预期 accepted baseline、`PreparedGraphWrite` 与可空的已准备安装结果。D/T 使用两个角色专用增项函数，公共部分只复用窄候选载体和已有 planner；不设计通用 Meta workspace。由完整 accepted rows 分配新 ObjectId（用宽游标检查 uint 耗尽），保留所有旧对象身份和不可达但仍 live 的行；替换根 Dictionary 的冻结状态并增加所需字符串/数组行。T 从输入复制定位字节，不持有调用方数组。字符串去重是可选实现细节，不改变标签可重复的合同。

准备阶段先用 `GraphRecordStore.Revalidate` 复核长期持有的实际 snapshot，再复用当前 Dictionary 的完整前后状态 `PrepareDelta` 与 `ObjectWritePlanner`，提供完整 post-live 集和真实 prior/H。现有操作列表 Delta emitter 可留作测量后的优化，不作为本片前置，也不新增 add-only 容器格式。

**首片采用实际回读准备安装。**追加后、发布前，核对同一 owner、准确 source/root、完整成员集合，以及实际 OwnedBatch 的 ObjectId/Kind/prior/body 与候选 records 一致；再经窄管理 reader 读回该实际图，取得安装用 rows/snapshot/H。这样无需移植业务 `NormalizedGraphState` 的热安装成本推导或另建一套证明。回读成功仍不修改 accepted；全部 D/S 安装材料就绪后才能共同发布，成功后在 guard 内替换基线与实际 EntryId，交付失败按既有合同停用 owner。旧候选不能安装到已推进的基线上；未发布候选直接丢弃内存材料，完成帧保留。

安装反例须覆盖错误 owner/source/root/实际 records 与成员遗漏，不能只核数量。首版增项的预期成员是 accepted 全体成员与新记录 ID 的并集；未写对象保留准确旧 head。T 新增了 graph append 阶段，失败分类须采用现有管理追加与发布边界，不能照抄当前只发布小 payload 的 bookmark catch 条件。

代价是每次目录变化会完整解码目录，物理缓存可能减少 I/O，但不保证 O(新增项)。D 仅创建时变化，普通保存不承担此成本；T 的规模不受“1000条世界线”估计约束，不能据此宣称书签吞吐足够。只有实测表明回读成为瓶颈后，再考虑沿实际 records 增量计算安装材料。此轮尚未实施候选或验证该成本。

## 5. 容量、成本与最小验收

1 MiB 限制仍约束每条 HeadEntry payload，但 D/T 现在只是5或17字节入口，**不再用全部目录文本的总大小与1 MiB比较**。完整图仍受单帧、batch、对象大小及内存预算约束，不能声称无限容量。保留单标签长度上限作为输入合同；1000条线是当前故事规模，不引入条目数硬上限。

增量持久化不代表增量捕获或磁盘单点查询。首次读取允许完整恢复目录到内存；准备更新可能扫描完整字典。现有策略可能周期性重写字典 Base。先记录读取量、捕获量和实际落盘变化，再由证据决定分片或索引；不重新写容器来兑现未经验证的 O(1) 承诺。

下一片最小可观察判据：

- D/T 用真实 Meta 图完成 Base→新增项→实际 Delta，保准确 source/prior/H；关闭全部资源后恢复，再新增并关闭重开。
- D 有重复中文标签但不同合法 HeadId；T 保 Ordinal、不可重绑与12B准确值。证明不查询应用 Schema，S 默认 reader 仍拒绝目录布局，D/T 拒绝错误根、比较器、闭包和畸形值；历史 Base 带动态 Schema 时，必须在读取该 Schema 图之前失败。
- D 候选与真实增长的 S 候选在一次 HeadStore CU 中共同发布，冷重开同时看到二者；未发布候选不进入目录，正式产品故障分类另依 B2 验收。
- 空库无图入口和首项 Base、缺必需 Head 拒绝保持；大于旧1 MiB整表容量的目录若通过现有帧限制，应由实际图读取见证，不能仅从地址变小推出。

原 [BranchWireFormatProbe](../../../experiments/BranchWireFormatProbe/README.md)的 D/T 整表编码与容量结论保留为已替代方案的对照，其 Entry 小描述/ID/摘要隔离部分仍适用；它不是本方案的运行资格。新[Dictionary 机制见证](../../../experiments/DirectoryDictionaryMechanismProbe/README.md)验证了 D 的真实 Meta/规划器 Delta/冷开续写和 D/S 共同发布，以及 T 的 Base 冷读；后继窄读取见 §6，完整目录候选/安装、空入口与故障仍未实施。不能把这些原语组合或 source build 冒充新 Repository 已完成切换。

## 6. 窄管理读取切片

本片问题：在不扩大 S 员工 reader、不调用应用模型或 Schema 的前提下，D/T 能否复用共同解码循环，拒绝错误角色和历史 Schema，并保留真实 snapshot/对象身份/H？最小判据是实际 Meta 图的冷读正例，以及历史 Base/Delta、根、闭包、比较器和条目反例；不接新 Repository 布局或候选发布。

[ManagementDirectoryReader](../../../src/DurableGraph.Persistence/ManagementDirectoryReader.cs)提供两个内部角色入口，复用 [StoredGraphDecoder](../../../src/DurableGraph.Persistence/StoredGraphBodies.cs)的同一链循环。每条链在加载 origin Schema 前检查所有 SchemaRoot；typed Base 解析后、对象 reader 调用前检查精确布局；既有完整引用/lookup 验证后再检查根和所有 Dictionary 条目。返回原 `DecodedGraphRows`，不转换为要求业务 State/Schema 的 DecodedRevision。

[回归测试](../../../tests/DurableGraph.Persistence.Tests/ManagementDirectoryReaderTests.cs)使用实际 FrameStore/GraphRecordStore。D/T 的 Base→Delta 在全部关闭后通过新入口冷读；默认 S reader 仍拒绝它们。历史坏 Schema 反例以管理专属拒绝诊断及不增加 GraphMiss 证明没有读取其内容；原通用 builtin 入口作正控，确实尝试读取 Base 的坏 Schema。未登记的 codec-valid ID 可以读取，表明本层没有偷做 HeadStore 全库审计；成员与实际 Entry 资格仍属后续操作。

**2026-10-19 验收：**DG 起点 `fa4a858`，Storage 使用 pin `f8868b5` 的独立源码 worktree。实施者定向28项通过；独立 subagent 复核源码/反例，主线程检查集成 diff 并运行根 `dotnet build DurableGraph.slnx`（0警告/0错误）及 Persistence 全套（957/957，0失败/跳过）。两条命令均显式 `UseStorageSources=true`、`StorageSourceRoot=.artifacts/db096-head-mechanism/storage-f8868b5`；实际命令传入绝对路径。构建、测试日志与 TRX 位于本机 `.artifacts/db096-management-reader/`。文档本地链接与锚点检查、`git diff --check` 通过。没有运行全 solution tests、包或进程中止验收。

边界：此处 Delta 由既有 body encoder 明确构造，用于验证读取，不证明规划器会为这个小样本选择 Delta。T 的冷 Delta 读取不等于冷续写候选/安装或名称不可重绑已接通。5/17B 空入口、首项创建、D/S 安装与故障、超过旧1 MiB目录、公开 Branch API 和普通包资格均不在本片。
