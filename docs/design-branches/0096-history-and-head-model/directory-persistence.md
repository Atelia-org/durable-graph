# DB-096：管理目录复用 Dictionary 持久化

> 后继：目录原语已在 B2 隔离分支接入真实 Repository，见[施工记录 §8](branch-cutover-plan.md#8-b2-隔离分支首段纵切)。本文分片证据保各自基线；其中尚未接线的历史表述不再代表隔离分支当前停点。
> 2026-10-19，B2 的目录接线合同。用户已接受复用现有增量序列化，替代 D/T 专用整表 codec；内部窄读取见 §6，增项候选、回读安装材料与小入口见 §7。Repository 的新布局和发布接线尚未实施。它收窄[整体切换计划](branch-cutover-plan.md)的管理载荷，不改变 Branch/Commit、继承历史或创建登记语义。

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

### 4.1 最小状态与安装合同

采用最小状态：D/T 分别持有一个可空的 `DecodedGraphRows` accepted baseline，初始无图时为 null。它已经包含实际 snapshot、冻结 rows、对象 head 与重建成本 H；不再并行维护另一份可编辑的 CLR Dictionary 作为持久化权威。查询索引可由这些 rows 派生，不能独立推进。实际控制 HeadEntryId 仍由 RepositoryResources 持有，目录候选不新增 publication owner。

候选只需保留角色、预期 accepted baseline、`PreparedGraphWrite` 与可空的已准备安装结果。D/T 使用两个角色专用增项函数，公共部分只复用窄候选载体和已有 planner；不设计通用 Meta workspace。由完整 accepted rows 分配新 ObjectId（用宽游标检查 uint 耗尽），保留所有旧对象身份和不可达但仍 live 的行；替换根 Dictionary 的冻结状态并增加所需字符串/数组行。T 从输入复制定位字节，不持有调用方数组。字符串去重是可选实现细节，不改变标签可重复的合同。

准备阶段先用 `GraphRecordStore.Revalidate` 复核长期持有的实际 snapshot，再复用当前 Dictionary 的完整前后状态 `PrepareDelta` 与 `ObjectWritePlanner`，提供完整 post-live 集和真实 prior/H。现有操作列表 Delta emitter 可留作测量后的优化，不作为本片前置，也不新增 add-only 容器格式。

内部基线必须来自窄 reader 或候选的实际回读；Revalidate 验证 snapshot 并不认证同程序集任意伪造的 rows/H。当前调用边界不引入新 proof 类型，也不为防内部手工构造而在每次 Prepare 前再完整解码一次。角色不匹配仍须拒绝。

**首片采用实际回读准备安装。**追加后、发布前，核对同一 owner、准确 source/root、完整成员集合，以及实际 OwnedBatch 的 ObjectId/Kind/prior/body 与候选 records 一致；再经窄管理 reader 读回该实际图，取得安装用 rows/snapshot/H。这样无需移植业务 `NormalizedGraphState` 的热安装成本推导或另建一套证明。回读成功仍不修改 accepted；全部 D/S 安装材料就绪后才能共同发布，成功后在 guard 内替换基线与实际 EntryId，交付失败按既有合同停用 owner。旧候选不能安装到已推进的基线上；未发布候选直接丢弃内存材料，完成帧保留。

安装反例须覆盖错误 owner/source/root/实际 records 与成员遗漏，不能只核数量。首版增项的预期成员是 accepted 全体成员与新记录 ID 的并集；未写对象保留准确旧 head。T 新增了 graph append 阶段，失败分类须采用现有管理追加与发布边界，不能照抄当前只发布小 payload 的 bookmark catch 条件。

代价是每次目录变化会完整解码目录，物理缓存可能减少 I/O，但不保证 O(新增项)。D 仅创建时变化，普通保存不承担此成本；T 的规模不受“1000条世界线”估计约束，不能据此宣称书签吞吐足够。只有实测表明回读成为瓶颈后，再考虑沿实际 records 增量计算安装材料。候选内部原语见 §7，尚无吞吐测量。

## 5. 容量、成本与最小验收

1 MiB 限制仍约束每条 HeadEntry payload，但 D/T 现在只是5或17字节入口，**不再用全部目录文本的总大小与1 MiB比较**。完整图仍受单帧、batch、对象大小及内存预算约束，不能声称无限容量。保留单标签长度上限作为输入合同；1000条线是当前故事规模，不引入条目数硬上限。

增量持久化不代表增量捕获或磁盘单点查询。首次读取允许完整恢复目录到内存；准备更新可能扫描完整字典。现有策略可能周期性重写字典 Base。先记录读取量、捕获量和实际落盘变化，再由证据决定分片或索引；不重新写容器来兑现未经验证的 O(1) 承诺。

目录接线的最小可观察判据（已有内部证据见 §6/§7，Repository 资格另行验收）：

- D/T 用真实 Meta 图完成 Base→新增项→实际 Delta，保准确 source/prior/H；关闭全部资源后恢复，再新增并关闭重开。
- D 有重复中文标签但不同合法 HeadId；T 保 Ordinal、不可重绑与12B准确值。证明不查询应用 Schema，S 默认 reader 仍拒绝目录布局，D/T 拒绝错误根、比较器、闭包和畸形值；历史 Base 带动态 Schema 时，必须在读取该 Schema 图之前失败。
- D 候选与真实增长的 S 候选在一次 HeadStore CU 中共同发布，冷重开同时看到二者；未发布候选不进入目录，正式产品故障分类另依 B2 验收。
- 空库无图入口和首项 Base、缺必需 Head 拒绝保持；大于旧1 MiB整表容量的目录若通过现有帧限制，应由实际图读取见证，不能仅从地址变小推出。

原 [BranchWireFormatProbe](../../../experiments/BranchWireFormatProbe/README.md)的 D/T 整表编码与容量结论保留为已替代方案的对照，其 Entry 小描述/ID/摘要隔离部分仍适用；它不是本方案的运行资格。新[Dictionary 机制见证](../../../experiments/DirectoryDictionaryMechanismProbe/README.md)验证了 D 的真实 Meta/规划器 Delta/冷开续写和 D/S 共同发布，以及 T 的 Base 冷读；后继窄读取与候选接缝见 §6/§7。不能把这些原语组合或 source build 冒充新 Repository 已完成切换。

### 已接受目录的查询投影

后继窄优化问题：B2 的 `ReadBranchInfo` 每次重建全部 D 成员映射，`ListBranches` 又逐项调用它；业务历史中的每次成员检查也线性扫描 D。普通保存并不改变 D，这些重复工作可以从已有 accepted rows 派生的只读查询视图中消除。

本片选择 owner 内按 **accepted `DecodedGraphRows` 对象身份**懒建 `HeadId → label` 查询字典。不是按每次保存都会替换的 `PublicationBaseline` 缓存；也不能直接在 frozen entries 上二分，因为容器合同不保证条目顺序，Delta 应用可在 prior 顺序后追加新 key。列表携已取得的标签生成描述，单点与历史成员检查复用同一视图。

视图只从已接受 D 构造，完整构造后才替换；候选准备不推进它，公开读取仍先检查 owner/backing 寿命与 fault，实际 Entry、来源成员及图校验不省略。它不存 current、origin 或 summary，不产生持久索引或另一份可编辑目录。初次查询/新 D 后的查询允许 O(N) 投影和额外 O(N) 内存；未变化 D 的单点成员查找采用 Dictionary lookup。定向 origin 缺口独立存在，不能据此称完整 ListBranches 已成为 O(N)。

最小验收：以实际投影条目计数证明重复单点/全列表查询不重新投影；普通 B、增长 S 和 T 保存保持同一视图；新登记后刷新而旧视图不变；冷开恢复、未登记/管理 Head 拒绝、登记失败与暖缓存 fault/关闭检查保持。

**2026-10-19 实施与限定验收：**以 DG `2a58730` 为起点，Storage 仍为独立 `f8868b5` worktree。三个有界包分别负责候选/实现、判别回归、上游核对与独立质疑；主线程复核实际 diff 并统一构建和测试。产品仅改 [RepositoryResources](../../../src/DurableGraph.Persistence/RepositoryResources.MetaBootstrap.cs) 与 [Repository](../../../src/DurableGraph.Persistence/Repository.cs)，公开签名、持久格式和发布协议保持。内部计数 `BranchDirectoryProjectedEntries` 只累计真正构造查询映射时的条目，不作为总 I/O 或时间指标。

[12条新增回归](../../../tests/DurableGraph.Persistence.Tests/BranchDirectoryLookupTests.cs)在3/9条分支下验证：创建逐次接受新 D，累计投影为6/45；冷开首次查询只投影3/9，之后三轮全体单点与列表查询计数不再增加。根和空 fork 的重复标签、保留旧视图、不可变包装、B 保存/新增 Event Schema 的 B/S 保存/T 发布都通过。root/fork × 发布前/后抛错，均在安装前观察旧视图；冷开只认实际已发布 D。未登记物理 Head、三控制 Head、原 default 异常和四种 fault/关闭边界保持。既有父成员检查源码未删除；“已登记子 Head 指向未登记父 Head”的专门反例留给定向 origin 接入。

主线程运行根 `dotnet build DurableGraph.slnx`：0警告/0错误；完整 Persistence **986/986**、Runtime 的 `RepositoryPublicApiTests` / `LoadedWorldGeneratorTests` / `PreparedRevisionGeneratorTests` **16/16**，均0跳过。均显式设置 `UseStorageSources=true` 和 `StorageSourceRoot` 为 `.artifacts/db096-head-mechanism/storage-f8868b5` 的绝对路径；测试使用本次 build 的 `--no-build --no-restore` 输出。实际日志及 TRX 在本机 `.artifacts/db096-directory-lookup/{build.log,persistence.log,persistence.trx,runtime-targeted.log,runtime-targeted.trx}`。未重跑全部 Runtime suite、普通包、进程中止或吞吐实验；没有上游改动或 feed 发布。

本片到此收口。后继包调用闭包与[定向 origin](targeted-origin-integration.md)已完成；完整门禁与master合入已由[最终交付验收](final-delivery-acceptance.md)闭合；后续增量入口只查[工作集](PROJECT-STATE.md)；不把 D 投影缓存扩大为通用查询缓存。

## 6. 窄管理读取切片

本片问题：在不扩大 S 员工 reader、不调用应用模型或 Schema 的前提下，D/T 能否复用共同解码循环，拒绝错误角色和历史 Schema，并保留真实 snapshot/对象身份/H？最小判据是实际 Meta 图的冷读正例，以及历史 Base/Delta、根、闭包、比较器和条目反例；不接新 Repository 布局或候选发布。

[ManagementDirectoryReader](../../../src/DurableGraph.Persistence/ManagementDirectoryReader.cs)提供两个内部角色入口，复用 [StoredGraphDecoder](../../../src/DurableGraph.Persistence/StoredGraphBodies.cs)的同一链循环。每条链在加载 origin Schema 前检查所有 SchemaRoot；typed Base 解析后、对象 reader 调用前检查精确布局；既有完整引用/lookup 验证后再检查根和所有 Dictionary 条目。返回原 `DecodedGraphRows`，不转换为要求业务 State/Schema 的 DecodedRevision。

[回归测试](../../../tests/DurableGraph.Persistence.Tests/ManagementDirectoryReaderTests.cs)使用实际 FrameStore/GraphRecordStore。D/T 的 Base→Delta 在全部关闭后通过新入口冷读；默认 S reader 仍拒绝它们。历史坏 Schema 反例以管理专属拒绝诊断及不增加 GraphMiss 证明没有读取其内容；原通用 builtin 入口作正控，确实尝试读取 Base 的坏 Schema。未登记的 codec-valid ID 可以读取，表明本层没有偷做 HeadStore 全库审计；成员与实际 Entry 资格仍属后续操作。

**2026-10-19 验收：**DG 起点 `fa4a858`，Storage 使用 pin `f8868b5` 的独立源码 worktree。实施者定向28项通过；独立 subagent 复核源码/反例，主线程检查集成 diff 并运行根 `dotnet build DurableGraph.slnx`（0警告/0错误）及 Persistence 全套（957/957，0失败/跳过）。两条命令均显式 `UseStorageSources=true`、`StorageSourceRoot=.artifacts/db096-head-mechanism/storage-f8868b5`；实际命令传入绝对路径。构建、测试日志与 TRX 位于本机 `.artifacts/db096-management-reader/`。文档本地链接与锚点检查、`git diff --check` 通过。没有运行全 solution tests、包或进程中止验收。

边界：此处 Delta 由既有 body encoder 明确构造，用于验证读取，不证明规划器会为这个小样本选择 Delta。T 的冷 Delta 读取不等于冷续写候选/安装或名称不可重绑已接通。5/17B 空入口、首项创建、D/S 安装与故障、超过旧1 MiB目录、公开 Branch API 和普通包资格均不在本片。

## 7. 目录候选与小入口切片

本片问题：既有 Dictionary 的冻结状态和正常 planner 能否直接承担 D/T 增项，并在实际追加、回读后取得准确安装材料，而不提前污染 accepted？最小判据是初始 Base、自然 Delta/后续 Base、冷续写再冷读、错误图不能安装，以及真实 HeadStore 共同发布后的冷可见性。仅新增内部原语，公开 Repository 仍保持原布局。

[ManagementDirectoryWriter](../../../src/DurableGraph.Persistence/ManagementDirectoryWriter.cs)提供两个角色增项方法，返回 `PreparedDirectoryWrite`。它用既有 Dictionary body、typed Base 与 `{5,5}` planner 准备完整 live 集合，保留旧对象身份和不可达 live 行；T 的定位字节由传入的 HeadEntryId 编码为自有数组。重复 D ID、重复 T Ordinal 名称在追加前拒绝；显示标签可重复。`PrepareInstall` 核对实际图与候选后经窄 reader 回读，`GetInstallation(currentBaseline)` 检查 owner 可用和预期基线引用后返回该材料；两者都不修改调用者 accepted，也不拥有发布权威。

`GetInstallation` 不是“证明已经发布”的方法。真实 Repository 必须在发布前核基线仍当前，持 guard 发布，成功后接受全部已准备材料和真实 EntryId；不得把第一次旧基线检查拖到发布之后。将来接 Repository 时，公开重复名称等诊断继续由操作层维护，内部 helper 的参数异常不直接定义公开 API。

[ManagementDirectoryPointer](../../../src/DurableGraph.Persistence/ManagementDirectoryPointer.cs)实现 §2 的5/17B固定入口，复用上游地址 codec；[入口测试](../../../tests/DurableGraph.Persistence.Tests/ManagementDirectoryPointerTests.cs)覆盖角色、旧格式、全截断长度、尾随、存在标记和非法地址。它不把合法地址编码当成实际 Meta 图资格。

[候选测试](../../../tests/DurableGraph.Persistence.Tests/ManagementDirectoryCandidateTests.cs)覆盖 D/T 连续增项的真实 planner Base/Delta、旧对象 head 保留、关闭后续写再重开、实际 H，以及错误 owner/source/root/records/成员不能安装且不污染候选。不可达成员保留及 uint ObjectId 耗尽另有边界测试。[共同发布见证](../../../tests/DurableGraph.Persistence.Tests/ManagementDirectoryPublicationTests.cs)用实际 FS/HS 和新内部部件，从空指针开始，准备 D 与真实 S 的初版及增长版，单次 PublishHeads 后安装并全关冷读；T 的准确定位和未发布 D/S/T 候选不成为 current 也通过实际回读观察。完成但未引用的图仍保留且可读。

**2026-10-19 验收：**DG 起点 `e7282f3`，Storage pin/独立源码 worktree仍为 `f8868b5`。新增候选5项、小入口7项、共同发布1项，共13项；实施者定向验收后，独立 subagent 复核原语和主线程集成 fixture，主线程实际执行根构建（0警告/0错误）与 Persistence 全套（970/970，0失败/跳过）。命令仍显式 `UseStorageSources=true`，`StorageSourceRoot` 指向 `.artifacts/db096-head-mechanism/storage-f8868b5` 的绝对路径；日志/TRX 在本机 `.artifacts/db096-directory-candidate/`。文档链接/锚点与 diff 检查通过。未运行全 solution tests、包或进程中止验收；本轮没有改上游。

后续直接进入 B2 隔离分支的真实 Repository 纵切：三必要 Head 的初始化/打开、D 正式登记、一 Branch 一 Head、HS 签发 source 与删除业务 Parent，再闭合旧调用迁移。不要继续增加独立发布协调器或泛化目录 workspace。当前组合见证没有实施 Repository 的故障分类、登记中断、缺控制 Head 拒绝及 D/S 安装失败后的 owner 停用；这些验收应随真实 owner 接线完成。超过旧1 MiB容量、吞吐、进程中止和普通包资格仍未覆盖。
