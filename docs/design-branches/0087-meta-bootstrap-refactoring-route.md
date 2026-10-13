# DB-087：meta 自举与共享／私有 Schema 视图

> 校准：2026-10-13。本文只保留当前契约、源码落点、后续门禁与未决问题。
> 默认公开路径已接 meta 自举，旧保存／typed reader／Schema 权威／raw Storage 束已退出。
> P1d 前九片通过各自限定验收；完整非 friend 公开 accepted-history 包门禁尚未完成，P2 私有视图未实现。
> 本次是文档压缩，不增加源码、包、ProcessCrashOnly 或性能资格；内部 API／wire 未冻结。
> [压缩前全文](../archive/2026-10-13/0087-meta-bootstrap-refactoring-route.md)冻结自 `ecc80683f35e2f42795530d28c8974dfb43d95dd`，保留原分片的来源、过程、运行记录与限制。

<a id="current-status"></a>
## 1. 当前结论与范围

本案承接用户指定的原型 [0005](../../../atelia-storage-2/docs/0005-schema-bootstrap-route.md)／[0006](../../../atelia-storage-2/docs/0006-schema-bootstrap-design.md)／[0007](../../../atelia-storage-2/docs/0007-schema-bootstrap-implementation.md) 和 [0008 共享／私有政策](../../../atelia-storage-2/docs/0008-repository-wide-schema.md)。
原型仅提供机制参照；当前实现、测试和分层验收决定 DG 的实际资格。

**当前模型是两种变化来源、一套对象版本保存核心、一个发布 owner。**
业务通过 Capture／比较准备对象记录，meta 通过受控编辑准备变化；两者共用 body、ObjectVersion payload、目录保存与 ref 发布。完整 DurableSchema 逻辑内容由固定员工 DTO 序列化，再经工厂投影；加载 meta 是读取指定根，而不是先打开 SchemaStore。
存活对象目录也走固定 Dictionary body 与对象版本，原 raw-map 不再是第二权威。

公开 Repository／BranchCheckout／GraphResources 只使用新路径。业务保存／恢复、catalog 和发布 owner 均已收成一份；P0 名称仍是内部实现名称，不代表另一套产品后端。
SchemaStore／SCB1、RepresentationId、旧 Base header、WorldWorkspace／LoadedWorld、旧 typed reader 与 raw Storage 束已退出。共用 body、ObjectVersion records／payload／计量、地址、只读字典与 owned LRU 保留。

用户明确无旧数据／API 兼容负担，简单性按最终产品衡量；不提供旧格式 reader、生产双写或迁移壳。这不授权修改、删除或修复已完成持久帧。完整帧 append-only、必要来源校验、正常底座恢复、串行 owner 与故障关闭仍成立。
自举不预设更快或代码更少，固定引导规则也不必消除自身。

当前缺口是[有限公开 accepted-history 准确包门禁](#public-package-gate)，通过后再接[P2 私有视图](#private-plan)。
十九个有效旧 PackageConsumer lanes 暂停当前 G，源资产与历史证据原地保留；选择性迁移不是该门禁前置，也不自动恢复未迁 lane 的资格。
当前能力只在 [PROJECT-STATE](../../src/PROJECT-STATE.md) 维护，未实施导航见[路线图 §4.1](../DurableGraph-research-roadmap.md#41-schemastore-复用-statestore-与联合版本视图)。

<a id="bootstrap-contract"></a>
## 2. 自举与完整布局合同

### 固定解释与动态解释

当前 typed ABI 为 **5**，员工 Schema／生成 DTO 仍为 **V1**。ABI、员工定义版本与用户 Schema 版本分开。

- route 0：有限 string／员工表；按固定身份与完整布局选 reader。
- route 1：动态 descriptor 的 meta ObjectId；必须带 Base 所在 batch 的准确 SchemaRoot 解释。
- route 2：structural TypeExpr；root 仅限所选 ABI 支持的 List／Array／Dictionary，闭合全树无 Named。

ABI5 明定 List codec2／Array codec1／Dictionary codec1；未知 ABI、旧路由或完整布局不匹配拒绝。不能以 BCL 外壳、`ValueSchema == null`、namespace、marker 或 DTO CLR 类型推断固定资格。
meta 仍只准有限员工闭包；builtin-only 容器的结构头／body 可不依赖 catalog 解释，不因此进入 meta 白名单。整份业务图仍需准确 S 校验与模型能力。
基元值是静态槽，不独立占对象行；用户 inline／enum／Named 槽仍需完整准确描述和执行能力。

### 一份描述图与不可变投影

catalog 根声明完整 descriptor 集合，包含引用 Schema、inline Schema 及容器布局。
字段、TypeExpr、base／inline／Nullable 依赖是普通员工节点；布局索引只在加载后派生，不持久化第二份 ID→layout 映射。
动态身份是 **`(origin SchemaRoot, descriptor ObjectId)`**；数字不是跨图、跨 view 的全局类型身份。
已接受 descriptor 不变；shared 同形复用，已接受同 key 异形拒绝。私有改形属于 P2，不能据此重解释旧 bytes。

全部 live 员工行先完成 fixed typed body、全消费与引用／角色校验。
再对根声明的 descriptor 构造闭包调用 Schema／TypeExpr／字段工厂，核 variant、完整槽、kind／arity、重复定义和依赖，重算派生值。
按 ObjectId memo 保共享 DAG；字段规范化只改工厂副本，不污染冻结 DTO 的位置 Delta。
业务 nominal 环允许，Schema／Field／TypeExpr 构造依赖环拒绝；nominal 名字不是递归加载领域对象的命令。

TypeExpr 保 depth≤64、展开 nodes≤4096、arity≤32；exact 布局 DAG 保 depth≤256。wire collection 保现用 `1_000_000` 数量及剩余数据检查，不增加通用限额平台。
未声明 descriptor 即使 live 也不得进入 SchemaKey／ObjectLayout 索引；Field／TypeExpr 不得充当对象 descriptor，inline 只作值依赖、不得作对象头。
未触及孤儿不获得语义接受承诺；必要闭包验证不是全历史 audit。

### meta 修改材料

长期 dirty 只保手工 Set 的最终逻辑 root 意图，不保存追加操作日志、自动 Capture 布局或派生依赖根。
`PrepareOwned(capturedLayouts)` 临时合并实际布局与手工意图，收集完整闭包。
在同一 owner guard 内取最新 accepted view 后才分配本次 meta IDs；第二个工作副本不能用先前未发布编号覆盖第一笔已接受结果。
Discard 保手工意图和领域对象修改，丢弃临时闭包／编号／bytes；下一次实际 Capture 重新推导。
Prepare 不消费 dirty，Confirmed 才消费对应候选修改；不增加 ID generation、世代 token 或重放日志。

<a id="source-contract"></a>
## 3. 图根、目录与内容来源

ref1 保存业务分支目录，ref2 保存 shared catalog 根；branch 是 ref1 的目录键，不是各自独立 VS ref。每个业务图 pin 准确 S，branch rewind／ref-only 操作不倒退 shared。
双 ref 沿用户 0008／CU 目标选定；单 ref 也可原子发布，不因存在替代方案重开已选布局。

持久图由三个固定入口组成：

- graph 封套：role、业务 Parent、RootId、DirectoryRoot、SchemaRoot、可空 OwnedBatch。
- 对象 batch：sourceGraph、动态 Base 的 SchemaRoot、本次 owned ObjectVersion records。
- 目录版本：创建 sourceGraph、固定 Dictionary 的 Base／Delta payload；Delta prior 是来源目录根。

目录为非零 ObjectId→batch head，固定 ScalarDefault comparer；取 head 还须核 batch 的同 ID local record。
HeadLocator 使用员工 inline DTO 三个 uint 搬运完整 canonical 12B bits，再经 `FrameAddress.TryRead` 恢复。
不解析坐标、不猜地址，不将 SG varuint body 声称为固定12B内容 codec。
目录排除自身、直接根寻址；对象 batch 完成后才能准备 locator，最后写目录，再写 graph。
一图至多一个对象 batch；超容量明确拒绝，不预建分片 manifest。首次空来源只写对象／目录 Base。

### 两种祖先关系与精确 prior

业务 Parent 是提交前准确 Head；内容来源 C 是该 Parent 自身或祖先中最近 State，无 State 为 null。
meta 的 Parent／C 是前版 meta，不能进入业务 ancestry。`S0→E1→E2→S1` 中 E2／S1 的 C 都是 S0。
C 由元数据派生，不重复持久化 content Parent 或 sourceDirectoryRoot。

1. OwnedBatch.sourceGraph 必须等于 C；每个 local ID 必须入最终目录且指该 batch，不能遗漏／删除／改指 external。
2. 新 DirectoryRoot 的 sourceGraph 必须为 C；Delta prior 必须为 C.DirectoryRoot。Base 也核来源关系，但截断旧目录内容链。
3. 复用 C.DirectoryRoot 时不能同时有新 OwnedBatch，也不改旧目录创建 context；根或所 pin 的 S 变化仍可形成正常版本。
4. 对象 Delta 按**该记录原 batch**的 sourceGraph 取目录，核 exact prior，再取 prior 的同 ID local record。

retained 旧链不要求来源等于当前 C；external head 保完成角色／同 ID 与所需链的浅声明边界，不认证全局实体史。
Base 截内容链，不删除必要业务 ancestry／source 元数据，也不无条件回读已截断的坏旧目录／Delta。
完成帧、CRC、角色、循环与全消费检查保留；领域引用图中的环仍合法。
无变化目录可复用根而追加真实 E/S，不强写空 Delta。Remove 只改新目录，不擦除完成帧或回收 ID。

<a id="view-contract"></a>
## 4. 准确视图、执行环境与恢复

### 四种职责不能混用

- **origin S**：终止 Base 自身 batch 的 S，解释 descriptor／bytes；不能从 graph 或 sourceGraph 的旧 S 猜本次 Base 的布局。
- **selected S**：所选业务图 pin 的 S，核全部 retained 布局完整相容；同数字 descriptor 不足以换 view 重解释。
- **execution view／target layout**：冻结代码在准确只读查询下闭合 current 模型／Upgrade；目标布局可尚未持久登记。
- **latest shared**：真实 Save 的准入与 catalog 候选来源；历史读取不借它补 origin／selected S。

目标 key 不存在时可由历史模板、当前定义和显式 Upgrade 提供；已有同 key 定义仍须完整相等。只读 target 不分配 meta ID、不调用 catalog Prepare、不追加或发布；无需先登记 V2 才读取只含 V1 的历史 S。

### 环境归属与完整恢复

Repository Open 冻结代码配置；reader 从实际 Store／S 私有签发环境，严格复用必须同 Store、同配置引用、同 S。
同 S 可显式复用闭合 memo，不同 S 独立；已显式注册的稳定 Model／Reader／Comparer 仍可保原引用。不能用外部 view、相等地址或 synthetic DTO 补保存来源。Synthetic 可作读取机制输入，不授可编辑来源认证。

checkout 执行 S／models／preparation 在其寿命内固定：有 State 取该 State.S，无 State 取创建时 latest shared，空库无 S。
State 提交可推进存储 selected S，不替换旧执行环境；热后首次闭合新类型仍看起始 S，新 cold checkout 另按其最近 State.S 闭合。
这是稳定执行环境的寿命边界，不承诺任意 view-sensitive callback 的跨 S 热冷领域值等价。

完整 stored 表先解码与核引用、Dictionary canonical key／适用 lookup，再 Normalize；包括 current 中不可达的 source 行。
模式4／5的实际 CLR comparer 碰撞按现可达图 Hydrate 边界拒绝，不冒称全部发生在 Normalize 前。
标准 Upgrade／值依赖在该 owner 首次业务 callback 前预绑定；空／absent 仍需能力，不承诺全图所有 callback 前完成一切准备。
current 完整引用／lookup 检查后才求可达闭包；两阶段先建实例-ID 表，再 Hydrate，保共享／环与 mutable allocator 唯一性 guard。

可写恢复保完整 source membership、layout、真实 head/H、实例身份、cursor 和 RequiresRewrite，包括之后将 Remove 的 source 行。
升级后仍 live 的对象在下一次 Confirmed State 保存写新 Base；独立只读不导入 Capture 身份。
新旧根按实际类型模型选择，已有 child 升根／旧 root 降为 child 保 ID，跨实际类型 State 根替换保持现合同。

### history、Pair 与 State 准备槽

GetHead／历史导航只读 graph 封套，不物化目录／typed body；恢复请求 Event 另核窄 source／目录／batch 关系，再选择最近 State。
Fork 从**selected head**派生最近 State，完整恢复并备齐交付材料后才发布新 branch，不借目标 branch 当前 head。
ref-only 不追加业务 graph、不改 shared；tag 保上游不可变绑定，句柄仅属于签发的 open owner。

Pair 各侧完整按准确 S Decode／Normalize，严格同 reader-issued 环境后才求共享闭包。
共享仍须同 ID／实际 head、Model／Preparation、完整 layout／state proof、未 rewrite 和引用闭包；same S 或相等 current DTO 不足够。
string 只在已有 same-ID/head 候选内用 ReferenceEquals 快路或完整 Ordinal 内容 proof，不 intern 不同 ID。
跨环境独立可变实例，两图共同 Materialize 保跨图 allocator guard；Pair 只读，不借 State 槽、不导入保存身份，也不承诺实例复用。

DB-080 单 State 槽保最近 State 的 reader 环境、Selection 与完整标准 Requirements。
命中仍核准确 owner／State／root／S／配置／models／完整来源，再独立物化；请求 head 每次独立导航。
Event-only 绕槽，失败不换槽，Fork 发布成功后才装槽；DB-081 热 seed 禁用，DB-082 leaf 候选默认关闭。
读取证明不能由 Capture／热安装基线代替，也不借物理缓存免检。

<a id="save-contract"></a>
## 5. 共同保存、发布与故障

GraphResources 是唯一 backing／refs／pending／fault owner；P0Repository 只是实验外观。
Repository 操作串行、每 branch 最多一个活动 checkout；生命周期、占用、重入与 borrowed backing writer 不重叠纪律保持。
公开 FS rotation 参数保原义，read-cache budget 单独透传；不把现第三位置的 rotation 偷换为 cache 配置。
同 owner 的唯一 pending、具体基线引用和候选身份承载资格，不造通用参与者／token 平台。

正常 Save 顺序：

1. owner／branch／Head／State 与完整 source 来源前检；guard 覆盖 lazy 模型闭合、Capture 和后续准备窗口。
2. 业务实际 Capture；catalog 根据 latest accepted、手工意图和 captured layouts 准备完整候选，再编码 typed Base／Delta。
3. 生产者各自择 Base，交 owned records／RemovedIds／RootId／准确 source；未列旧成员隐含 Retain。
4. catalog 有变化先完成 meta graph 取得 S，再写引用 S 的业务 batch、write-last 目录和 graph，无向前引用。
5. 按实际 Append snapshot.Objects／read records 完成真实 head/H、rewrite、meta／State 安装及 Head 交付材料，全部在发布前检查。
6. 新增 shared 定义**或容器 descriptor**时 PublishRefs 同次推进 ref1／ref2；否则只推进业务 ref1。Confirmed 后接受原候选并换引用。

不能只看 Schema Count 决定 CU；VS 执行 FS durability barrier，DG 不加第二事务日志。
原数字 ID、任意 map＋地址或伪 view 不能替代来源；候选安装还核同一 actual DTO 材料身份。
Capture 后最新 catalog 冲突在输出前拒绝，不承诺回滚已执行用户 callback 的副作用。

Confirmed E/S 都推进 branch Head 并接受已发布 meta；**只有 State**安装 DTO／身份／head/H 和消除 rewrite。
Event 丢弃业务候选、不建 next State baseline；Discard 亦保 State 与 rewrite，保领域修改和手工 meta 意图。
共同发布尚未成立时，已完成候选只作 orphan，不取得共享绑定或权威；Unknown 是否已生效须冷开裁决。Confirmed 后不重新 Capture、Normalize 或分配安装内容。

显式无变化 CommitState 可追加正常 State 并复用目录，所 pin 的 S 可以变化；Schema Set 本身不提前占共享键或暗造 E/S。
业务 head 完全不动的 Schema-only 更新不在当前合同内，见[条件延期](#open-questions)。

### 底座与结果消费

准确存储 pin 由 [StorageDependency.props](../../eng/StorageDependency.props) 决定，操作模式见[依赖指南](../storage-dependency.md)。
PublishRefs 仅处理同 owner 既有 refs，无 expected-head／CAS，也无跨多次 ReadRef 快照隔离；single driver／writer 独占及 DG guard 承担 stale 防护。
根 payload 受上游1MiB范围与 DG 根容量检查约束，不能把完整 catalog 塞入 ref 或预建分批平台。

两次 CreateRef 不是 CU；Open 必须取得两个正式根再建 DG Store／cache。
缺任一拒绝，不补建、不删完成内容；两个根完整后 fresh Open 可用。FS 不完整尾与 VS pending／必要控制文件 lazy recovery 沿底座协议。
不预开全部 refs／tags，不新增启动 audit／可写预检；readonly 不修尾。离线救援须全关、弃 cache、救援后 fresh 开。

- 确定 pre-I/O 拒绝不安装；健康 owner 可以修正后继续。
- NotPublished 不等于健康，仍须看 fault；未共同接受的候选定义不占 shared。
- Unknown／fault 停用相关 owner／checkout／cache，关闭后按必要正式根冷开裁决，不透明重试。
- Confirmed 后安装或交付失败仍是 Published，已接受定义仍绑定；保完成 orphan，废弃旧会话重开。

故障与关闭要尝试完整清理，保 primary／close error 身份与栈、Store→VS→FS 顺序和聚合；不得经已失效 getter 绕过清理。
ProcessCrashOnly 见证中首 CU End 只有一成员完整时两旧；全部 End 在 flush 前已经完整时必须两新。
进程未交付结果不等于返回 Unknown／Published；这些证据分开，不外推断电或未测试窗口。

## 6. 计量与缓存边界

业务保完整 post-live 全图 Base 预算，可给未变对象维护 Base；强制 rewrite 不占 optional 预算，不以 k 项变化替代全图估计。
meta／live-directory 只在变化容器比较真实 B/D：B≤D 或 H+D 超阈值乘B时选 Base，否则 Delta；不进入业务全图预算。
不可变 descriptor 旧 head 沿用，无 meta 变化不保存 meta，无目录变化复用根。

B/D/H 以 ObjectVersion payload 为单位，含实际类型头、kind／length、Delta prior 12B，排除 ID／membership／batch／graph framing。冷链 H 按 record 实际消费量，合法冗余长度可能高于 canonical writer／估计；H 不是总 I/O。
目录由固定入口确定布局，不补虚拟 ObjectId／类型头；其保存成本另计，不混业务预算。

owned 物理缓存按同 owner 完整地址复用 metadata／batch／directory version 和完整 heads/H；frame／map 两组件共享一份预算／LRU。
成功解析／replay 后 admit，值私有 owned／只读；hit 前核 owner，fault／Dispose／backing 关闭清驻留并拒用。
zero 关闭、negative pre-I/O 拒绝、oversize bypass／驱逐保持；预算 charge 不保证总堆或峰值有界。
不从 prepared/input seed，不缓存 typed／整个 graph 来源证书；上层失败不回滚已经正确的 raw/map，每图关系与准确 S 校验保持。

本案不承诺总 O(k)：业务比较／预算、容器 Base、字典冻结／ApplyDelta／索引、投影、冷验证与基线复制仍可 O(N)。分页、增量索引、持久树和更多复用仅由具体测量触发；旧 body 尺寸或单次 Prepare 时间不推导持久加速倍率。

<a id="implementation-map"></a>
## 7. 当前源码导航

- 自举路由：[员工定义](../../src/DurableGraph.Persistence/MetaBootstrapP0/P0EmployeeModels.cs)、[fixed bindings](../../src/DurableGraph.Persistence/MetaBootstrapP0/P0FixedBindings.cs)、[builtin 完整布局](../../src/DurableGraph.Persistence/MetaBootstrapP0/P0BuiltinLayouts.cs)、[typed body／reader](../../src/DurableGraph.Persistence/MetaBootstrapP0/P0TypedBodies.cs)。
- meta 查询／编辑：[P0CatalogView](../../src/DurableGraph.Persistence/MetaBootstrapP0/P0CatalogView.cs)、[P0CatalogWorkspace](../../src/DurableGraph.Persistence/MetaBootstrapP0/P0CatalogWorkspace.cs)，复用 [Schema 工厂](../../src/DurableGraph/Schema/DurableSchema.cs)。
- 持久核心：[P0GraphStore](../../src/DurableGraph.Persistence/MetaBootstrapP0/P0GraphStore.cs)、[wire](../../src/DurableGraph.Persistence/MetaBootstrapP0/P0GraphWire.cs)、[目录 codec](../../src/DurableGraph.Persistence/MetaBootstrapP0/P0DirectoryCodec.cs)、[共用 payload](../../src/DurableGraph.Storage/ObjectVersionPayloadCodec.cs)。
- 业务 producer／候选：[BusinessWorkspace](../../src/DurableGraph.Persistence/BusinessWorkspace.cs)、[P0BusinessWorkspace](../../src/DurableGraph.Persistence/MetaBootstrapP0/P0BusinessWorkspace.cs)、[planner](../../src/DurableGraph.Persistence/ObjectRevisionPlanner.cs)、[NormalizedRevision](../../src/DurableGraph.Persistence/NormalizedRevision.cs)。
- owner／公开前端：[GraphResources](../../src/DurableGraph.Persistence/GraphResources.cs)／[发布 partial](../../src/DurableGraph.Persistence/GraphResources.MetaBootstrap.cs)、[Repository](../../src/DurableGraph.Persistence/Repository.cs)／[恢复 partial](../../src/DurableGraph.Persistence/Repository.MetaBootstrap.cs)／[history partial](../../src/DurableGraph.Persistence/Repository.MetaBootstrap.History.cs)、[BranchCheckout](../../src/DurableGraph.Persistence/BranchCheckout.cs)。
- 环境／恢复／缓存：[StateModelRegistry](../../src/DurableGraph.Persistence/StateModelRegistry.cs)、[ReadSplit](../../src/DurableGraph.Persistence/DecodedRevision.cs)、[GraphReader](../../src/DurableGraph.Persistence/GraphReader.cs)、[State 槽](../../src/DurableGraph.Persistence/PreparedStateRestoration.cs)、[owned cache](../../src/DurableGraph.Persistence/MetaBootstrapP0/P0GraphReadCache.cs)。

修改具体机制时先核相应 source／tests，再选[历史证据组](#evidence-index)，不按分片编号通读过去施工。
内部命名、tags／位宽、co-location 和局部签名不因已存在而冻结；无新消费者不增加通用后端、codec 或限额平台。

<a id="63-p1b-首片续工入口"></a>
<a id="public-package-gate"></a>
## 8. 下一门禁：有限公开 accepted-history 准确包

目标是以**非 friend、普通 PackageReference** 的真实公开默认 Repository 消费者补齐 P1 包停点。
既有内部 friend 的 history 包、普通非 friend README 包和 source 回归各保原资格，不能互相代替。
本轮压缩不执行该门禁；具体 runner／夹具形状由下一有限施工片决定，不先建平台。

最小代表故事采用真实 SG Widget V1→V2 显式 Upgrade、共享引用／环与 List：

1. V1 进程 seed／State／tag，保 accepted history 与完整持久证据。
2. V2 从准确旧 S readonly Upgrade，目标尚未编号亦成功；全关前后文件集合／bytes 相同，旧 history 文件保持。
3. main 推进新 shared S；从旧 State tag Fork，读取仍按旧 State.S，写准入看 latest shared。
4. 首次 State 对仍 live 的升级对象写 Base，未变 List 可沿用其 origin S/head；Event／Discard 不提前消费 rewrite。
5. 同 reader 环境 Pair 验完整共享闭包，跨 S Pair 独立恢复；same current DTO 不代环境资格，旧 tag 解释保持。
6. fresh owner 继续实际单 List Delta，核当前值、ObjectId、实际 heads／prior 来源及链 record 实耗 H：Base 重置、Delta 累加、retained 保持。再 readonly 冷读旧 tag／新分支，旧完成帧保持。

包源验收须同时具备：

- DG 四包与准确六存储包用独立版本及完整 feed；消费者放在 repo 外隔离 stage，避开仓库 Directory.Build.*，使用 fresh NuGet.Config／cache，核实际 assets 无 ProjectReference。
- 业务程序集使用普通身份，不用 IVT／friend 名称取得内部入口，也不新增 public 诊断 API。实际 heads／prior／H 可由独立的窄 wire inspector 观察，不授格式冻结或通用检查平台。
- nupkg／cache／stage output 与实际加载程序集一致；不能只证明 feed 中存在包。
- 包内 SG／Build inputs、实际工具来源及 accepted-history Publish／Verify 被真实消费，历史1→2且旧文件不变。
- 明确 pack source provenance；含未提交源码的产物不得说成 clean HEAD 包，source build 不能代包运行。

验收采用具体故事、独立关闭／重开和公开行为／实际 backing 证据，不重复所有 family／provider／comparer／rank 矩阵。
十九个有效暂停 lanes 不是前置；包门禁也不授未迁 lanes 当前资格，不新增进程 Kill／断电／性能结论。
唯一公开默认路径、旧权威退出与该有限包门禁共同决定完整 P1 停点；完成前仍不宣告 P1 已完成。

<a id="private-plan"></a>
## 9. P2：已选政策、尚未实现

以下是用户 0008 方向的已选设计，**不是当前可用 private API 或已验能力**。
当前默认 shared 路径仍可正常保存；下面的“无策略写拒”是 P2 配置入口的选定合同。

1. 应用显式选择 shared／private 写入归属；无策略可读、写时拒绝。配置位置／方法签名到施工时决定。
2. private 从冻结 shared fork **完整 view**，不做 private miss→latest shared fallback；旧图始终 pin 原 S。
3. 首个同 key 异形见证在匹配 B 环境建**新私有 branch＋新根＋全部 live 新 Base**；旧 A 分支继续准确读写。
4. private→shared 比较完整定义／布局闭包：同形复用、新项共同加入、异形拒绝；不能按数字 descriptor ID换指针。
5. retained heads 只有完整相容证明才保留，必要时完整重编码；两个匹配环境独立 cold 读／续写，错环境与深层 retained 改形拒绝。

私有首片保图内共享／环，不承诺跨完整重建的数字 ObjectId 连续。
私有改形新建 descriptor ObjectId 并替换目录声明成员，不改写已接受 descriptor；旧 S 保持旧节点与含义。
这是收窄首次验收，非永久禁止原分支显式重建；若有消费者再定义该入口与身份规则，不要求用 B 物化 A 旧图。
不能从分支名、地址相等或上次进程内存猜策略；无配置冷开恢复策略若成为需求，再决定是否持久化配置。
当前 reader／definition／binding 每 key/version 单形状；匹配环境能力与持久目录身份分开，不借 private 篡改 Generator accepted history。
不增加自动 promotion／merge／rebase、overlay 继承链或全历史占键账本。

<a id="open-questions"></a>
## 10. 局部未决与条件延期

当前没有需要重新选择产品政策的阻塞。P1 包消费者的局部形状和 P2 配置签名可在各自有限施工片落实。wire／内部 API 未冻结不等于要先做命名、程序集或后端统一；新格式变化按准确 source／package模式验证。

以下只在真实消费者或测量触发时重访：

- 原分支显式全量重建、单次 Open 多套同键异形模型：先说明调用与身份合同，不简单放宽旧环境 guard。
- 业务 head 完全不动的 Schema-only：当前显式无变化 CommitState 已可 pin 新 S，登记本身不得暗造历史或提前接受。
- 分页／持久树／索引与基线增量：先给具体增长或时间／分配证据，不把 H 或驻留charge冒充总成本。
- DB-082 默认关闭候选的启用与 DB-081 热准入：现有读取证明边界保持，无消费者不升级为自举／包门禁前置。
- 更多 comparer 配方、SG rank3/4、嵌套／constraints／跨库升版：有限证据不外推完整组合；只有实际新接缝反例才追加资格实验。

不引入自动扫描、ArtifactStore、GC／压缩、多 writer、兼容框架或通用 policy/provider 作为本案前置。

<a id="evidence-index"></a>
## 11. 分组证据与冻结历史入口

下列入口保旧链接可定位性，**不把旧“下一片／默认未切／未实施”当当前排期**。
逐次 counts／日志／hash、旧 ABI、夹具校准、施工双后端和退休源码的 `git show` 指令仅在[冻结全文](../archive/2026-10-13/0087-meta-bootstrap-refactoring-route.md)维护。
当前资格必须带 source／内部 friend 包／普通公开包／ProcessCrashOnly 的实际边界，不把历史总数累计为本轮运行。

<a id="10-p0-实施证据与方案修订"></a>
<a id="11-p1a准确视图与共同恢复接线"></a>
<a id="12-p1b-首片schema-员工闭包与固定-list-路由"></a>
<a id="13-p1b-第二片闭合泛型与-enum-执行资格"></a>
<a id="14-p1b-第三片builtin-only-list-结构固定路由"></a>
<a id="15-p1b-第四片array-结构与动态路由"></a>
<a id="16-p1b-第五片dictionary-双槽与-source-lookup"></a>
### 自举闭包与类型路由

[归档 §10–16](../archive/2026-10-13/0087-meta-bootstrap-refactoring-route.md#10-p0-实施证据与方案修订)记录真实 meta／Widget／容器冷读、固定／动态路由及 Schema 工厂／引用／variant／limits拒绝；[Dictionary source lookup](../archive/2026-10-13/0087-meta-bootstrap-refactoring-route.md#16-p1b-第五片dictionary-双槽与-source-lookup)补前置检查。
各 ABI／包只保当片资格，旧基础包不自动证明最新 G 或全 BCL组合。

<a id="17-p1b-第六片真实-history-容器-upgrade"></a>
<a id="18-p1b-第七片多参泛型继承与-provider-history"></a>
<a id="19-p1b-第八片跨程序集继承与有限停点"></a>
<a id="24-p1c-第五片冻结配置与准确-s-绑定"></a>
<a id="25-p1c-第六片repositorycheckout-执行环境"></a>
### Upgrade 与准确环境

[容器／generic provider／跨程序集代表 §17–19](../archive/2026-10-13/0087-meta-bootstrap-refactoring-route.md#17-p1b-第六片真实-history-容器-upgrade)保真实 accepted history及各自范围；[环境 §24–25](../archive/2026-10-13/0087-meta-bootstrap-refactoring-route.md#24-p1c-第五片冻结配置与准确-s-绑定)否定无条件跨 S lazy共享，保 hot执行S寿命和 readonly零写。
能力预绑定／同 key完整冲突与各层 callback边界仍有效，不据代表故事穷举 provider／跨库组合。

<a id="20-p1c-首片共同业务规划内核"></a>
<a id="21-p1c-第二片normalized-保存材料与发布前安装准备"></a>
<a id="22-p1c-第三片共同业务-workspace-与候选寿命"></a>
<a id="23-p1c-第四片内部-split-ownerroot"></a>
<a id="26-p1c-第七片history-元数据与-ref-操作"></a>
### 保存、owner 与历史

[归档 §20–23](../archive/2026-10-13/0087-meta-bootstrap-refactoring-route.md#20-p1c-首片共同业务规划内核)保真实 typed B/D/H、候选身份／重入／安装准备失败与完整来源；[§26](../archive/2026-10-13/0087-meta-bootstrap-refactoring-route.md#26-p1c-第七片history-元数据与-ref-操作)保 selected-head Fork／ref-only／历史选择。
其两 adapter／owner仅属施工阶段，现用路径已收为一份。

<a id="27-p1c-第八片split-readpair"></a>
<a id="28-p1c-第九片split-checkoutfork-准备材料复用"></a>
<a id="29-p1c-第十片owned-物理缓存"></a>
### Pair、State 槽与 owned缓存

[归档 §27–29](../archive/2026-10-13/0087-meta-bootstrap-refactoring-route.md#27-p1c-第八片split-readpair)保准确双侧恢复、字符串 proof、独立物化、完整证书／失败保旧槽及 raw/frame/map预算与生命周期规律。
不授 typed cache／保存来源、默认启用 leaf 或总堆／性能资格。

<a id="30-p1d-首片初始化与正常恢复"></a>
<a id="31-p1d-第二片真实-dg-processcrashonly-保存窗口"></a>
<a id="32-p1d-第三片直接初始化-kill-与实际返回-outcome"></a>
### 初始化、故障与进程窗口

[归档 §30–32](../archive/2026-10-13/0087-meta-bootstrap-refactoring-route.md#30-p1d-首片初始化与正常恢复)分开记录必要 backing恢复、lower CreateRef联验、真实DG保存／直接factory Kill和实际返回outcome；入口为 [MetaBootstrapCrashProbe](../../experiments/MetaBootstrapCrashProbe/README.md)。
有限 source ProcessCrashOnly不外推断电、包或未测试窗口，BeforeInstall hook不冒充Install本体故障。

<a id="33-p1d-第四片准确包与-accepted-history"></a>
<a id="34-p1d-第五片公开默认接线与回归移接"></a>
<a id="35-p1d-第六片旧保存高层退出"></a>
<a id="36-p1d-第七片旧typed恢复束退出"></a>
<a id="37-p1d-第八片旧schema权威与owner模式退出"></a>
<a id="38-p1d-第九片旧raw-storage束退出"></a>
<a id="retired-save-source"></a>
<a id="retired-typed-reader-source"></a>
<a id="retired-schema-authority-source"></a>
<a id="retired-raw-storage-source"></a>
### 包、默认接线与旧权威退出

[内部 friend 包 §33](../archive/2026-10-13/0087-meta-bootstrap-refactoring-route.md#33-p1d-第四片准确包与-accepted-history)和[普通公开包／默认接线 §34](../archive/2026-10-13/0087-meta-bootstrap-refactoring-route.md#34-p1d-第五片公开默认接线与回归移接)分责，不代尚待实施的完整公开accepted-history门禁。
[退旧 §35–38](../archive/2026-10-13/0087-meta-bootstrap-refactoring-route.md#35-p1d-第六片旧保存高层退出)将有效规律迁真实新路径，保实际计量／来源／CRC／no-admit／清理／锁／容量；最新源码回归与活动Replay只保其运行用途，不授新包或性能。
退休源码与本地Git恢复见[保存](../archive/2026-10-13/0087-meta-bootstrap-refactoring-route.md#retired-save-source)／[typed reader](../archive/2026-10-13/0087-meta-bootstrap-refactoring-route.md#retired-typed-reader-source)／[Schema权威](../archive/2026-10-13/0087-meta-bootstrap-refactoring-route.md#retired-schema-authority-source)／[raw Storage](../archive/2026-10-13/0087-meta-bootstrap-refactoring-route.md#retired-raw-storage-source)；不假定本地历史已发布远程。
