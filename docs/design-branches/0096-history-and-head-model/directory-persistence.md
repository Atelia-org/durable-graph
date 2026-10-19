# DB-096：管理目录复用 Dictionary 持久化

> 2026-10-19，B2 的目录接线合同。用户已接受复用现有增量序列化，替代 D/T 专用整表 codec；本文件中的新目录产品接线尚未实施。它收窄[整体切换计划](branch-cutover-plan.md)的管理载荷，不改变 Branch/Commit、继承历史或创建登记语义。

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

解码 ID 不等于校验它在当前库已发布；D 正式成员的具体描述查询、T 的 ResolveTag 仍走既定 HeadStore/业务 Entry 资格入口。不能把冷开目录变成逐个打开全部业务 Head 的审计。

每个管理图的 `PreviousMeta` 只指向**该目录自身已接受的前版**，D、S、T 互不充当增量来源。D/T 的 accepted baseline 持有真实 snapshot、对象身份、当前行和准确 prior/H；冷恢复后沿用这些身份准备下一次变化，不能每次重建所有 string/array 对象导致退化为全 Base。业务 Graph 删除 Parent 的决定不影响这些管理图的物理来源。

## 4. 准备、追加、发布与安装

1. 在仓库既有串行操作 guard 内验证参数、重复名、准确 Commit 和当前基线。目录候选与已接受视图分开；不可先改 accepted Dictionary 再尝试持久化。
2. 复用容器准备与既有 Base/Delta 选择器生成候选：只有新增标签/名称/定位值需要新对象；未变项目和字典对象身份保留。选择 Base 或 Delta 由现有大小/H 策略决定，不承诺每次新增都选 Delta。
3. 追加管理图，准备其安装材料；保留已有所有完成帧。图地址写入对应 Head 的小入口。候选追加成功还不等于已登记。
4. 发布所需 Head 后才安装全部 accepted baseline、snapshot 与 HeadEntryId；D/S 联合发布不能先安装其中之一。Unknown、发布后安装/交付失败依既有合同停用整个 owner，不能继续使用半安装缓存。

创建协议保持：fork 先 CreateHead，再追加 D 候选图并发布 D；新根先准备真实业务图与必要 S 候选、CreateHead 得首 Entry，再发布 D 或 D/S。创建成功可列出且空 fork 可立即预览。普通业务保存只发布 B 或 B/S，不写 D；CreateTag 只准备/发布 T。未登记 Head 和未发布候选图不自动清理、认领或修复。

这一步不引入通用 Meta workspace 框架。可在 RepositoryResources 内维护两个窄目录状态，抽取确实相同的编码/准备小函数；复用底层容器机制，不为了统一业务保存、Catalog 和管理目录强造新的公开层级。早期验证失败、追加后失败、NotAttempted/Unknown/Confirmed 和安装失败应沿用当前 owner/outcome 边界，不能因“只是目录”放松。

## 5. 容量、成本与最小验收

1 MiB 限制仍约束每条 HeadEntry payload，但 D/T 现在只是5或17字节入口，**不再用全部目录文本的总大小与1 MiB比较**。完整图仍受单帧、batch、对象大小及内存预算约束，不能声称无限容量。保留单标签长度上限作为输入合同；1000条线是当前故事规模，不引入条目数硬上限。

增量持久化不代表增量捕获或磁盘单点查询。首次读取允许完整恢复目录到内存；准备更新可能扫描完整字典。现有策略可能周期性重写字典 Base。先记录读取量、捕获量和实际落盘变化，再由证据决定分片或索引；不重新写容器来兑现未经验证的 O(1) 承诺。

下一片最小可观察判据：

- D/T 用真实 Meta 图完成 Base→新增项→实际 Delta，保准确 source/prior/H；关闭全部资源后恢复，再新增并关闭重开。
- D 有重复中文标签但不同合法 HeadId；T 保 Ordinal、不可重绑与12B准确值。证明不查询应用 Schema，S 默认 reader 仍拒绝目录布局，D/T 拒绝错误根、比较器、闭包和畸形值；历史 Base 带动态 Schema 时，必须在读取该 Schema 图之前失败。
- D 候选与真实增长的 S 候选在一次 HeadStore CU 中共同发布，冷重开同时看到二者；未发布候选不进入目录，正式产品故障分类另依 B2 验收。
- 空库无图入口和首项 Base、缺必需 Head 拒绝保持；大于旧1 MiB整表容量的目录若通过现有帧限制，应由实际图读取见证，不能仅从地址变小推出。

原 [BranchWireFormatProbe](../../../experiments/BranchWireFormatProbe/README.md)的 D/T 整表编码与容量结论保留为已替代方案的对照，其 Entry 小描述/ID/摘要隔离部分仍适用；它不是本方案的运行资格。新[Dictionary 机制见证](../../../experiments/DirectoryDictionaryMechanismProbe/README.md)验证了 D 的真实 Meta/规划器 Delta/冷开续写和 D/S 共同发布，以及 T 的 Base 冷读；产品窄管理 reader、完整目录 workspace/安装、空入口与故障仍未实施。不能把这些原语组合或 source build 冒充新 Repository 已完成切换。
