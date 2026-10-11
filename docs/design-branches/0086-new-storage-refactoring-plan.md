# DB-086：新存储栈重构方案

> 日期：2026-10-11。状态：**三角色、三轮简化评审完成；已选方案，实施与验收见[施工记录](0086-new-storage-implementation.md)**。
> 目标：以 Binary / FrameStore / VersionStore 替换旧 SegmentStore / EventJournal，使最终产品只保留一套存储与发布实现。
> 本文记录方案合同；后续产品施工已获用户授权。公开发包和旧存档操作不在本片范围。
> 入口：[PROJECT-STATE](../../src/PROJECT-STATE.md)；前置事实：[DB-085](0085-rbf3-framestore-versionstore-migration-feasibility.md)及[上游 API 复核](0085-upstream-api-refinement-review.md)。

## 1. 最终模型与衡量标准

一个资源 owner 拥有一个 FrameStore 和一个借入它的 VersionStore。Schema 完整快照与图帧共用 data；
图帧的 Payload 是 StateRevision，TailMeta 是 Event/State、业务 Parent 和 RootId，一个地址同时定位历史记录与 Revision。
一个预留 VS ref 发布仓库当前根，包括 Schema 快照和 branchName→图帧。tag 使用 VS 的不可变 tag。
DG 保留对象图语义；文件发现、锁、分配、RBF 完整性、尾恢复与发布耐久顺序交给上游。

```text
Schema 登记：准备完整候选目录 → append SchemaSnapshot → 发布新 Schema 根 → 安装目录
图提交：准备/编码 Revision 与业务头 → append 一个图帧 → 发布新 branch head → 安装候选
图读取：取得所选图帧 → 检查两种 Parent 对应 → 解码 Revision/Schema/对象 → 完整成功后交付
重开：上游打开 → 读仓库根 → 重建 Schema 目录；不审计全部 State/commit，不重新 flush 全库
```

简单性按最终源代码判断：一套地址表示、一个仓库根权威、一个业务父链、一个正常打开策略；
没有旧栈运行时、全库重开确认、启动全图审计、物理文件穿透、双格式分派、第二套 binary cursor 或默认关闭的审计框架。
施工可使用隔离分支、临时实验和多次提交，但每个桥接必须在切换完成时删除。
不为完成感重分程序集/命名空间。Model、Capture、Upgrade、恢复算法保持；SG/静态 StateOps 的二进制签名要整体换成 Bare 类型，
这是有意扩大施工面以删除最终适配壳，不是另设计一套生成器。

## 2. 需求账本与证据边界

| 编号 | 当前要求与来源 | 本轮含义 |
|---|---|---|
| U1 | 用户 2026-10-11：以最终项目代码简单性为准，过程可以复杂 | 不把保留旧壳、通用 backend 或长期双模式作为简化 |
| U2 | 用户赞同移除全库重开确认/启动审计；健康诊断在主要功能稳定后再做 | 撤出原反馈第 3 项，不新增 VS ConfirmDurable，不建立 RO preflight→WO 桥接 |
| U3 | 用户明确：旧数据无需保留，只验收新建库 | 新运行时不读旧 DG 格式，不交付迁移器；旧根明确拒绝，不删除、不原位转写 |
| U4 | 用户：正确内容、报错或按底层合同丢弃不完整结果 | 上游负责物理恢复；完整但坏的必要内容报错，不静默回退 |
| P1 | [Repository](../../src/DurableGraph.Persistence/Repository.cs)、[BranchCheckout](../../src/DurableGraph.Persistence/BranchCheckout.cs)与 Persistence 测试 | 自由 E/S、Event-first、最近 State、Move 后续写、named Fork、tag、历史选点保持 |
| P2 | [GraphReader](../../src/DurableGraph.Persistence/GraphReader.cs)、[RevisionDecoder](../../src/DurableGraph.Persistence/RevisionDecoder.cs)、[WorldWorkspace](../../src/DurableGraph.Persistence/WorldWorkspace.cs)与模型/恢复测试 | exact Schema、身份、Upgrade、完整图准备、mutable 隔离、ReadPair 共享合同保持 |
| P3 | [GraphCommitException](../../src/DurableGraph.Persistence/GraphCommitException.cs)、保存/故障测试 | NotPublished/Unknown/Published、fault、发布后安装、不透明重试保持 |
| P4 | [SchemaStore](../../src/DurableGraph.Persistence/SchemaStore.cs)、[SchemaCatalogReplayTests](../../tests/DurableGraph.Persistence.Tests/SchemaCatalogReplayTests.cs) | 仓库全局、单调目录；已接受登记不随 branch 回退；same exact key 冲突仍报错 |
| P5 | [StateRevisionStore](../../src/DurableGraph.Storage/StateRevisionStore.cs)、链/cache 测试 | exact Parent/prior 对应、链终止、真实 H/D、借入 owner 生命周期保持 |
| C1 | 根 README、[包消费者](../../experiments/PackageConsumerProbe/README.md)为本地可检查的调用方；List/Enum/Generic 等 consumer 直接组合 SchemaStore/StateRevisionStore/RevisionDecoder | 保留独立 exact 解码见证，调整具体资源获得方式；高层行为保持，低层签名修改并重编译，不建立兼容壳 |
| F1 | 当前 Repository 单 writer、串行、每 branch 一个活动 checkout；新 FS/VS 当前源码 | 保留必要 guard；不增加 CAS 平台、多 writer、OS crash/power loss 或 exactly-once 保证 |

DG 旧栈调研基线为 `c900edd5b3a921b7bf46df2ac0482dc39597e606`；当时的旧包 pin 以该 revision 为准。
当前来源见 [StorageDependency.props](../../eng/StorageDependency.props)。本方案的上游依据为 WSL
`/repos/Atelia-org/atelia-storage`，`974fd47abb73792f9d350af87ab91a53848952eb`。
旧包行为由 DG 源码、测试和准确 pin 解释；新源码能力由上述新栈 revision 解释，不混作一个来源。
DB-085、本方案和路线图不是相互独立的需求证据。没有找到当前 checkout 中可执行的 DramaBoard 源码，不声称已验证它。

本方案选择的新合同：打开不再证明全库健康；新开 writable 底座允许 RBF3 尾恢复；Schema 接受以发布根为准，
不再收养所有物理可发现的完整 Schema orphan；binary reader 接受上游合法有界冗余编码；目录总量受单帧容量限制。
这些是方案裁决，不冒充当前产品实现。值语义与已接受 Schema 的单调性仍保持；证据和代价见[评审记录](0086-new-storage-simplification-review.md)。

## 3. 权责与资源寿命

| 层 | 唯一职责 | DG 不再重复的工作 |
|---|---|---|
| Binary | 基元/字符串基本读写、尺寸 | 第二套 Base128/ZigZag/浮点底层算法 |
| FrameStore | 帧分配、opaque 地址、读取 CRC、文件生命周期、正常恢复、必要输出确认 | segment locator/rotation、lease 池、raw RBF open/flush、escaping/物理 overhead |
| VersionStore | 根发布、必要记录资格、不可变 tag、publication outcome | ref 对象/名字日志、逐文件耐久顺序、恢复报告台账 |
| DG | Schema 与对象格式、两种 Parent、分支名字、图准备/安装、访问所需语义校验 | 不重做上游物理/发布证明，不审计未访问图 |

整合现有 GraphResources 与 HistoryJournal 的 owned resources，由一个具体 owner 保存 FS、VS、当前根和 Schema/State facade；
名称可沿用 GraphResources 或另取局部暂名，不能同时保留两份根快照/两个发布协调者。Repository 是唯一业务发布 driver，
现有公开低层 exact decoder/Store 调用方仍通过具体资源/facade 组合工作，不被假作不存在。
StateRevisionStore 借 FS；SchemaStore 使用同 owner 的 Schema frontier 发布入口。无需 IStorageBackend、provider registry 或 public lease。
Dispose 先失效 DG 句柄与缓存，再关闭 VS，最后关闭 FS；清理继续释放所有资源，保持主错误证据。
读/缓存命中前检查实际 backing owner 与 DG fault；只读不写不修；所有操作串行，禁止 callback 重入。
删除重复 repository.lock，固定 FS→VS 顺序取得两底座实际 owner 锁；任何一步失败释放已取得资源，不交付 partial owner。
DG 根初始化/发布只在完整 owner 内进行；创建不接管已有根或初始化残留，不另造初始化事务/格式门文件。

正常新开先打开 FS、VS 并读取必要仓库根，再构建 DG facade/cache；VS 对 tag 等控制文件的首次惰性打开仍按上游合同恢复，
不枚举所有控制文件来完成全库恢复。已有完整帧在 owner 生命周期内保持内容/地址稳定；
DG 不在 live owner 下救援、不继续使用故障 owner、不把不完整结果签发为合法业务句柄。
原“所有正常打开都禁止修尾”约束由此收窄；未访问内容不宣称健康。

## 4. 一个仓库根，两个不同的历史含义

### 4.1 当前根

首选一个 VS 预留 ref（建议 localId=1，建库时 `reservedRefIdUpperBound=1`），不按 branch 分配 ref。
root payload 为 `DG magic/version + RootMap codeword`，初始 body 可为空 map；不存在必要根时 Open 报错，不自动补建。
不加第三个 format 文件。旧 DG 布局缺新 data/versions 要求时，在调用 writable 工厂前拒绝；新底座布局里的未知应用 root，
必须打开上游后才能检查 magic/version，检查前可能已发生正常恢复，不假称对所有未知库都无物理动作。

首选直接复用 [RootMapCodec](../../../atelia-storage/src/VersionStore/RootMapCodec.cs)：
内部 key `schema` 表示最新 SchemaSnapshot，`branch/`+原 branch 名表示图帧；两个域无歧义，Schema 初始项缺失表示内建目录。
按现有 branch 名称合同检查输入，Ordinal 比较，不新增 trim/normalize；现有名字是受限 ASCII，自然满足 RootMap 有效 Unicode 条件。
只在 RootMap 外加上述短格式头，不增加第二个 body codec、双 codec 回退或 registry。

每次发布都从唯一最新根构造：Schema 登记保持全部 branch；branch 更新保持最新 Schema frontier 与全部其他 branch。
特别禁止在 Stage 前捕获旧根、Schema 登记推进根后仍用旧副本覆盖，否则会丢掉已接受登记。
单 writer guard 内检查 expected branch commit 后发布，不把 VS RefRevision 当业务 CAS/业务父链。
整个 root payload（包括格式头）受 VS 的 1MiB 限制；复用 RootMap Encode 并预检加头后的总长度，超限正常拒绝，不丢 branch、不分块补救。
验收一项超限且旧根不变的见证；不以规模 benchmark 为施工前置。大目录的真实消费者出现才重选每 branch ref。

### 4.2 一个图帧，两种 Parent

不再额外 append 一个 commit envelope。图帧 Payload 是 Revision，TailMeta 是
`DG metadata magic/version / Event或State / BusinessParent? / RootId`；不存一条指向自身 Payload 的 Revision locator。
Revision 中的 Parent 指最近 State，BusinessParent 指上一条 E/S，两者信息不能合并。VS 分支和 tag 都指向同一个图帧地址。
CheckpointAddress 仍由本次 Repository 打开签发；其历史位置与诊断 RevisionAddress 是同一个 FrameAddress，owner 不是永久库身份。

Storage 与 Persistence 的最小接缝如下；签名在 R1 固定，不引入 generic callback、可切换 backend 或元数据 registry：

```csharp
// 借入 FS，只解释 Revision Payload；TailMeta 是实际调用方 Persistence 的 opaque bytes。
FrameAddress Append(StateRevision revision, ReadOnlySpan<byte> tailMeta = default);
StateRevision Read(FrameAddress address); // 解 frame.Payload，不要求 TailMeta 为空。
```

低层独立 Revision 可有空 meta，但 Repository 不能把它签发为 graph checkpoint；这两个层的职责不同，不是双存储格式。
Repository 导航检查 frame 角色/墓碑/业务头版本与完整消费，只解 TailMeta，不解全部 Revision 对象或运行模型。
FS 目前只有完整 CRC 的 ReadFrame；读取 TailMeta 仍读整个 Payload。大 Revision 的历史导航会增加字节 I/O，
同帧 Payload CRC 坏时导航也报错；导航后准备图可能再次读同帧。本轮接受这个明确代价，不新增缓存或要求上游 metadata-only API。
实际大快照导航成为瓶颈时再评估；现有空 model、无 Allocate/Upgrade 的元数据合同仍保持。

`c1→c2`，Move 到 c1 再写 c3，要求 `c3.Parent=c1`。VS 根发布序列可能仍有 c2，不能用其上一项推导最近 State。
named Fork 只向根 map 加一个名字→所选图帧，不追加图帧，也不调用 VS ForkRef 来复制另一份仓库根。
仍先恢复/构造独立 editable 图再发布新名字；恢复失败不留分支。Event 不推进 State 基线或接受 Event 的 live 身份。
tag 使用 VS CreateTag/ResolveTag，payload 为单个图帧地址；不新增 tag 目录、tag 索引或关联 revision 依赖。

## 5. Schema 与地址：删除物理顺序，保留业务信息

### 5.1 完整 SchemaSnapshot

共享 data 上写完整已接受目录的 SchemaSnapshot；root 的 schema 项只指最新快照，不保存 PreviousBatch。
复用 SchemaCatalogWireCodec，将完整 Nodes 按 RepresentationId 顺序从内建/空 registered 集合编码、读取；依赖必须先于使用者，ID 从2连续。
现有 Prepare 已准备完整候选字典，保留这个 prepare/install 边界。开库只读取根所选的一个快照，完整校验后一次安装。
不建立 Schema 链 visited、逆序批次列表、增量重放、单独 next-ID 账本或全帧 Inventory；next ID 从目录推导。
内建-only 目录用缺失 schema 项表达，不强行编码当前 codec 拒绝的空批次。无新增节点不写快照/发布新根。

总目录必须能放入一个 FS 帧，变化时重新编码完整目录。使用上游实际合法 payload 上限与既有容量预检；
超限在 I/O 前正常拒绝，不添加压缩、分块或 linked fallback。真实累计目录接近上限或登记写放大成为瓶颈时才重访增量存储。
这是新格式的容量取舍，不能宣称目录无界，也不能把 Schema bytes 计入对象 B/D/H。

登记成功的边界：准备候选目录/返回值 → append 完整快照 → 发布仅更新 schema 的新根 → 安装目录并返回。
后续图提交失败也不撤销已确认 Schema；Move/Fork 不回滚 Schema。Schema 未引用的孤立 data 帧不是目录权威。
如果 schema 根发布 Unknown，owner fault，重开读取实际 schema frontier；不补发、不盲目认领物理 orphan。
GraphCommitOutcome 只描述本次 branch/tag 的业务发布。Schema 发布 Unknown 时图的根若尚未尝试，图仍未发布；
普通 Stage/输入失败可以保留原异常，不强制全部包装 GraphCommitException。保留原始 VS 证据与 fault，不能机械把 Schema Unknown 映射成 graph Unknown。

### 5.2 一个地址类型

最终直接使用上游 FrameAddress，删除 DG 的 `(FileNumber,SizedPtr)`、FileScope 与 backward-file-distance codec。
新 DG Revision/业务头 links 与 tag 地址使用上游固定12B编码：无需知道所在文件，尺寸固定，避免另写变长 consumed/Measure 协议。
RootMap 继续消费上游既有变长 codec；这是两个用途的编码选择，不增加第二种运行时地址。
State wire 整体升级版本，一次定义 null/required 地址边界；不能保留旧版本 reader或从12B猜回上游字段。

### 5.3 因果与终止

写入时 parent/prior/external head 必须已在当前 FS 中完整存在且角色正确；DG 不暴露提前 graph/Revision 地址。
这利用串行 append 的事实保证新帧不能成为自己的已完成父；不新增全局 durable sequence。
map replay、object prior 和业务 Parent 的实际遍历各自防环；检查须覆盖间接递归，不能仅顶层放一个未传递的 visited。
Delta prior 必须等于 exact Parent map 为该 ObjectId 选择的 head；完整 map 的 external head 保持 shallow 声明，
允许重新选择更老的同 ObjectId head，实际读取对象时要求目标含该 ObjectId 的 local record，不升级成跨链实体来源认证。
遇环、缺失、错角色或 exact prior 不符明确失败；不按 FileId/offset 排序，也不启动全库 DAG audit。
对象图自身允许循环，不能把 Revision/业务 Parent 链的防环规则套到 ObjectId 引用图。
只有这些局部机制不足以保留具体产品规律时才提出更强机制与反例，不能以旧 EnsureStrictlyEarlier 的存在证明需新时钟。

### 5.4 成本与缓存

H/D 仍由实际 DG object-record bytes 定义；变更地址宽度后同步 writer、size estimator 与读回 consumed 计量。
不使用 RBF3 overhead 代替 H、不把预计最短长度当实际消费。热基线 H 必须与冷读 H 一致。
保留已使用 owned Revision/head-map 缓存、操作内读复用及一槽 State 准备材料；适配地址/寿命，不扩大缓存或新增 provenance registry。
默认关闭的 DB-082 immutable-leaf 实验不进入必保内核，不为它新增接口/资格框架；影响本轮接线的死支线删除，证据保留在原记录。

## 6. Binary：直接采用上游 cursor，只保留实际组合语义

最终删除 BinaryPayloadReader/Writer、CanonicalVarInt，以及无生产/生成调用的 nullable-string 方法与 NullablePayloadHeader。
SG、Runtime 静态 StateOps、body delegates、Schema/Revision codec 与消费者直接使用 BareValueReader/Writer。
整数、bool、浮点、bytes、非 null string 直接调上游；char 选定上游 CharLE 一种编码；Guid/decimal/Temporal 用少数 DG 组合 helper。
不得用另起一个同功能 Cursor 类型、别名兼容壳或 permissive/strict 开关恢复删除的层。

新 body 合同是“writer 输出上游默认最短表示；reader 接受其合法有界冗余表示”。`80 00` 读为0、合法非最短 string 表示可以接受；
溢出、截短、非法 bool/UTF8、结构/ID/count/角色、decimal flags、日期范围及完整消费仍检查，组合读取失败保持既定 cursor 边界。
保留 NaN/负零/decimal scale与符号零/DateTimeOffset完整offset、字符串code-unit和引用身份，不把原 golden bytes 全不变作为兼容要求。

DictionaryStateReader 的 ReadKey 与 Index 按解码后的语义 key 重新 EncodeKey，去重/Delta寻址不依赖输入的冗余原始bytes。
因此保留规范 writer 与语义 key 重编码，但删除全局 primitive 最短输入检查，不能用并不存在的错寻址反例保留 facade。
nullable 值槽和 ObjectId=null 保持；删除的是无消费者的 nullable-string 内容头，不是 null 引用。

同时修改 Generator 的 SchemaExportHelpers 中 hardcoded metadata 类型、全部生成模板/静态签名与反射 factory 检查；
引用旧生成 body 协议的模型程序集明确要求重编译，不自动降级或生成签名适配器。
模型 `.dgschema`/manifest 的版本历史和 exact 升级语义继续保留；旧存储数据不保留不等于删除新库内 V1→V2 模型升级。

## 7. 正常操作与失败结果

打开只检查本次必要格式/绑定/根与 Schema 快照；不扫描全部 data、graph、branch/tag 图依赖。
必要检查分成三个层次：FS 的整帧 CRC；DG 元数据导航的角色/业务头/路径终止；准备所选图时的 Parent 对应与完整图语义。
准备每个实际选中图（ReadState/Event/Pair/Checkpoint、Checkout/Fork）前，沿 BusinessParent 找该帧之前最近 State，
比较所选 Revision.Parent 与其地址；即使完整 Base 也不能豁免。元数据浏览不做 typed 解码或逐对象链验证。
这是原 Repository.ValidateGraph 中独有检查的具体新落点，不是把全 ValidateHistory 改名搬到每次跳读。

DG 不主动扫描未引用 orphan 的 body/图语义来增加阻断；上游必要 header/EOF/恢复发现的错误仍传播。
不承诺所有坏 orphan 都能忽略，例如 active EOF 结构损坏可能使 FS Open 失败；实际访问坏完整内容不回退。

正常图保存先准备模型候选、Schema 登记与业务 meta；append 一个图帧后准备与地址相关的安装材料，
由本次 VS 根发布执行 data-first 确认，再输出/确认 ref。Repository 不重复执行“State全库flush→commit全库flush→ref全库flush”。
删除无保留调用需求的独立 AppendDurably；确实只需 data-only 确认的低层宿主直接调用借入 FS.ConfirmDurable，不再造另一屏障壳。
完整完成输出可以留下 orphan，DG 不清理完整帧；未完成输出由上游恢复规则处理。

| 失败位置 | 本次业务结果与后续 |
|---|---|
| 输入/模型/名字/容量正常拒绝，未开始业务根输出 | NotPublished；是否 fault 按实际 backing/owned 操作，不凭 outcome 猜健康 |
| 图帧输出/确认或安装材料准备失败，但未开始业务根输出 | NotPublished；必要时 fault，close/reopen；不撤销已有 Schema/帧 |
| VS 业务根/tag 输出已尝试，确认未完成 | Unknown；fault；新 owner 查实际 branch/tag，不透明重试 |
| VS 业务根/tag 已 Confirmed，安装/交付失败 | Published；保留证据，不能降成 Unknown/NotPublished或回滚发布 |

单一 root 更新不需要 CU，多 ref 原子更新、CAS、跨库事务、后台恢复和审计报告不进入本轮。
不保证重开能证明某个旧调用返回过 Confirmed，恢复只接受当下必要 checked 事实。

## 8. 施工顺序：一个纵切先成立，随后搬入其余语义

每片都有可执行终点，不先造一圈空接口。生产切换在隔离工作分支进行；任何混合旧/新同名 Rbf 闭包的实验用不同进程/项目。
临时 consumer/桥接只为验证具体问题，不计作最终能力，也不进入最终生产依赖。

| 分片 | 范围、具体落点 | 最小验收与删除出口 |
|---|---|---|
| R0 合同与纵切输入 | 本文 §3–7；固定root头/keys、图meta、地址wire、SchemaSnapshot和Bare接受域；新source gate清单 | 字段/角色/容量/outcome边界可检查；明确新源码身份；不实现strict preflight/迁移器 |
| R1 可执行最小纵切 | 改最终产品source：Bare必要签名/SG、共享FS、SchemaSnapshot、合一图帧、VS root，一个最小生成模型 | Create→登记→branch→Save→Dispose→冷Open→Read→再Save；真DG图，不使用旧Segment/Journal；独立host只负责调用，不能复制最小DG |
| R2 地址与真实图恢复 | StateRevisionStore/wire/size/cache、Bare全部组合/导出/反射、Schema所有登记与冲突 | Base/Delta、exact prior、浅external head、间接环拒绝、H热冷一致、19标量/容器、新库V1→V2；删FileScope/旧地址/cursor/nullable-string/专用schemafilewhole-tail |
| R3 产品历史与分支 | Repository/BranchCheckout/Checkpoint、业务meta导航/选图检查、root-map更新与VS tag | E-first/E-S、Move再写、Fork mutable隔离、Read/Pair/Checkpoint/EnumerateEvents、空model metadata、tag冷重开；删HistoryJournal/EventAddress/独立commit/名字日志 |
| R4 故障与调用方收束 | 新失败窗口、owner寿命、fixtures/动态编译/低层exact consumer | 图帧/root/tag各窗口；Schema与graph接受分离/stale root/Unknown；热cache在backing失效后拒绝；RO不写、尾恢复/必要错误；删旧路径/全库确认/启动审计测试 |
| R5 清场与交付 | 项目/脚本/README/XML/probes/依赖文档与新pin | 活动依赖无旧栈；无临时backend/双模式/第三把锁；source-mode全回归，新候选包纵切及实际改动的SG/跨程序集probes；删旧storage迁移lanes |

R0/R1 中的几个局部 helper 不意味着最终要保留同名类型。Binary、State、Schema 可由有界工作包施工，但共享 wire/root/owner 合同由主线程一次统筹。
R1 的临时 host/编译清单可以与旧 solution 并存，产品源码只能有一份新实现；R5 删除临时项目。
产品切换整体关闭旧闭包，不能用同一运行时替换一半。Binary→SG→consumer 的签名变更需同批集成，不以两个public cursor长期共存来通过旧tests。
不要求每片恢复全部历史测试；每片 build 与相关语义测试通过，合并切换与R5完成整体回归。

### 每片共同验证

代码变更执行 `dotnet build DurableGraph.slnx` 与相关测试；本机重 .NET 操作串行，建议 Release、`-m:1 -nr:false`。
文档-only片只核对集成diff、相对链接/anchors与状态口径。必要的跨进程关闭/重开覆盖单测不能替代的事实。
现有 2976 项通过是 DB-085 旧基线证据，不是新栈验收数；上游 FS/VS 结果亦不能替代 DG 回归。
不以全部旧测试原样保留为完成条件：删除已撤销合同的测试；保留产品语义测试，替换物理layout fixtures。
新 package pin/来源与 DG 自己版本独立；source-mode仅build/test，pack必须package-mode。真实包资格使用唯一候选版本与独立feed/config。
先改源码联调图为Binary/FS/VS等新依赖；当前旧pin不能消费新API。新storage包可用准确新来源的唯一候选包，不能沿用旧六包候选或静默还原旧package assets。
最终包验收至少一条新库README/升级/branch/tag冷进程纵切，并按实际生成/导出资产改动运行相关跨程序集probes；公开发包不属于本轮隐含授权。
最终正常依赖不残留旧 SegmentStore/EventJournal；需要 transitive Data/Primitives/Rbf 不等于允许 DG 穿透物理文件。

## 9. 删除、延后与不可删内核

| 裁决 | 项目 | 具体理由或删除失败 |
|---|---|---|
| delete | 全库重开flush/confirmed准入、启动State/Journal/全部图审计、strict writable桥接 | 用户撤销其作为日常保证；无需对上游重新证明所有未访问内容 |
| delete | 旧格式reader/converter、旧地址/options壳、旧Journal owner/第三把锁、BinaryPayload两个cursor/CanonicalVarInt/nullable-string死路径 | 无旧数据保留；不为旧接受域维护第二套cursor和算法 |
| merge | Schema与branch发布权威；GraphResources/HistoryJournal资源；Revision与commit物理帧 | 一个最新根避免stale覆盖；一个图地址删除locator/append/中间失败窗，仍保留两种Parent |
| simplify | Schema批次链→完整快照；物理先后→opaque地址/已完成来源/按需防环；Binary→直接Bare+组合helpers | 删除Schema前驱/逆序重放、FileScope和命名翻译；保留实际格式/值/因果规律 |
| defer | 健康诊断、全库Audit host、增量Schema、metadata-only读、完整CU、每branch ref、跨库地址交换、持久序号平台 | 实际容量/成本/消费者出现再设计；不预留运行时两种模式 |
| keep | 正常发布顺序/outcome/fault、exact语义/Parent-prior、必要格式校验、owner/cache寿命、mutable隔离 | 删除会交付坏图、选错State、使用失效cache或把已发布结果误判为未发布 |

不可删内核的具体反例：Move后以VS上一条发布为Parent会让c3错误继承c2；Schema登记后用旧根发布branch会丢目录；
缺prior精确检查会把对象Delta接到另一对象/另一Parent；删除必要路径防环会不终止；删除读取语义验证会把CRC合格但非法DG内容交付；
根已Confirmed后降档会诱发重复提交；把mutable Fork做成ReadPair共享会串改两个工作副本。

## 10. 质询与后续维护

三角色完成独立首论、交叉质询和两项剩余分歧的最终裁决，本文已按证据修订；过程结论与代价见
[简化评审记录](0086-new-storage-simplification-review.md)，不按票数决定，也不记录私有推理或完整交谈。
当前没有需要保留双架构的未决产品选择；具体magic/版本值、新低层资源获得方式和严格字段长度在R0/R1一次固定并执行见证。
本文是后续工单的输入，方案不等于已实施能力。实现某片后从活动队列移除完成项并链接验收，不能把本文积累成施工流水账。
持久字段/API冻结在对应实现片形成可检查合同；没有反例的未来机制留到触发条件成立后。
