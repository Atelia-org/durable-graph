<a id="db-087-p0持久-meta-自举实验"></a>
# DB-087：meta 自举实现与分片证据

本目录的自举读写已进入公开默认 Repository／BranchCheckout／GraphResources；旧保存／typed reader／Schema 权威／owner 模式与 raw Storage 束已退出。[当前状态](../../../docs/design-branches/0087-meta-bootstrap-refactoring-route.md#current-status)与[后继公开 accepted-history 包门禁](../../../docs/design-branches/0087-meta-bootstrap-refactoring-route.md#public-package-gate)维护活动合同；该门禁尚未完成。下文各分片实施／验收直接链接冻结归档，保留当片含义，不用旧“默认未切”说明推定当前状态。

catalog、共同 writer 与发布 owner 已接共用恢复／业务 workspace；公开前端复用这一条路径，不整体丢弃 P0。旧保存与 raw 低层已退出，共同 payload 和受控容器 emitter 保留。
代码去向、依赖顺序与尚未敲定的问题集中在 [DB-087 活动合同](../../../docs/design-branches/0087-meta-bootstrap-refactoring-route.md#implementation-map)，不以 `P0` 目录名推断最终产品边界。

实验只把两种变化来源汇入共同保存：业务 Capture/比较；meta 按逻辑 key 归约修改。
图 writer 接收来源、根、owned ObjectVersion records 与删除集合；取得完成对象 batch 地址后，
用真实 Dictionary body 保存存活目录，再追加固定图入口。目录自身没有领域 ObjectId。
descriptor 使用其 meta ObjectId；每个动态 Base 的解释仍受其 batch 的准确 Schema 根约束。

真实 FS/VS 拥有文件和两个 refs。共同确认后才接受候选：Event 接受 meta、推进业务 Head，
但保留最近 State 的业务保存基线。关闭全部资源后重新打开，必须只从实际发布根恢复。
P0 wire 和员工闭包仍允许调整；不提供旧格式兼容，私有策略后置。旧核心退出与完整公开包门禁是不同证据层次；当前尚未完成后者。

从 [P0Session](P0Session.cs) 看两侧准备与安装；
[P0GraphStore](P0GraphStore.cs) 保存共同 batch／目录／图并校验来源。
第四片已将内部发布状态迁入 [GraphResources.MetaBootstrap](../GraphResources.MetaBootstrap.cs)，
[P0Repository](P0Repository.cs) 为借用实际 owner 的薄内部外观；内部资源 owner 已通过第四片源码验收。
[P0CatalogWorkspace](P0CatalogWorkspace.cs) 保留手工 Set 的最终逻辑意图，
[P0CatalogView](P0CatalogView.cs) 从员工 DTO 调用 Schema 工厂；均不调用 SchemaStore。
员工模型在本程序集内经真实 Source Generator 编译。

P0 首轮见证接受无继承、无泛型的 ReferenceObject Schema，基元字段、简单命名引用及 List<int>。
P1a 已将恢复并回 GraphReader/NormalizedRevision，真实 V1→V2 可在只读路径升级而不发布 meta；
Save 按实际 Capture 布局自动准备 catalog，Event/Discard 保留 State 重写义务。
固定通道只共享不可变布局，绑定缓存归操作／builder。内部类型／history 的代表资格见下述 P1b 记录；
私有模式、公开缓存与完整产品包交付仍待后续资格。
目录冻结、Base 候选、Schema 投影和业务 Capture／预算仍可全量工作；这里只减少受控变更的比较与发射。

P1b 首片当时使用强类型 Descriptor/Field/TypeExpr 员工与 typed／fixed-table ABI v2；
内部员工 DurableType/Schema 与生成 DTO 仍 V1，由外层 ABI 选择固定表，不伪造 history。
当时精确 `List<int>` 固定，用户 Root／`List<Point?>` 按准确 S 的动态 descriptor 解释；该 List 专码已由第三片结构路由替代。
长期 dirty 只留手工 root 意图；自动 Capture 依赖在 Prepare 中临时派生，Discard 后重新推导。
真实消费者范围为 base／inline／Nullable／List，完整类型执行、公开接线和 private 仍待后续；
完整源码回归与隔离包四进程验收统一在 [DB-087 §12（归档）](../../../docs/archive/2026-10-13/0087-meta-bootstrap-refactoring-route.md#12-p1b-首片schema-员工闭包与固定-list-路由)。

P1b 第二片以既有记录验证闭合泛型／enum/Nullable/inline，无生产代码或 ABI 改动。
真实 SG 的两个 Box/Cell 闭合实例可冷读共享／环并续写实际 Delta；独立反例验证有序实参、版本与准确视图，
范围和相关回归见 [DB-087 §13（归档）](../../../docs/archive/2026-10-13/0087-meta-bootstrap-refactoring-route.md#13-p1b-第二片闭合泛型与-enum-执行资格)。
该片不授予泛型历史 Upgrade 或新包资格。

P1b 第三片 builtin-only List 结构固定路由已完成源码与隔离包验收，问题与证据见
[DB-087 §14（归档）](../../../docs/archive/2026-10-13/0087-meta-bootstrap-refactoring-route.md#14-p1b-第三片builtin-only-list-结构固定路由)。
第三片当时选 typed ABI3 的有限 string/employee 表、dynamic descriptor 与 structural TypeExpr 三路；
List root 递归无 Named，codec 2 由 ABI 固定，精确 `List<int>` 专码移除，员工 Schema/DTO 仍 V1。
结构 body 能力不扩大 meta 员工白名单；`List<long?>`／共享 inner 的 nested List 已验冷读、Delta与独立拒绝，真实包四进程正常冷读／续写通过。
此处保留第三片历史范围，不据其授予 Array／Dictionary 资格。

P1b 第四片 Array 路由已完成源码与隔离包验收，问题、选定设计与证据见
[DB-087 §15（归档）](../../../docs/archive/2026-10-13/0087-meta-bootstrap-refactoring-route.md#15-p1b-第四片array-结构与动态路由)。
D variant4 复用完整 Array TypeExpr、Element、显式 codec1，Version0／Fields和Base空；
重建完整 ArrayLayout 后核 Type，并独立核 exact inline 依赖，shape 只在 body。
第四片当时 typed ABI4 固定 List2／Array1、拒旧3，Schema/DTO仍V1且不增字段；meta 有限白名单不扩大。
真实 SG `int?[]`／`Point?[,]` cold与稀疏 Delta、raw 边界、完整源码回归和四进程组合包均已验；NoChange S2只来自源码 SG。
该片保留历史范围，公开 P1c/d 和 P2 不随其切换。

P1b 第五片 Dictionary 双槽与 source lookup 已完成源码与隔离包验收，问题和证据见
[DB-087 §16（归档）](../../../docs/archive/2026-10-13/0087-meta-bootstrap-refactoring-route.md#16-p1b-第五片dictionary-双槽与-source-lookup)。
D5复用 Type＋有序 Key1/Value2 Fields，Version0、Element/Base空、codec1；ABI5明确 Dictionary1/List2/Array1、拒旧4，DTO仍V1。
完整引用检查后、Normalize 前验证所有 stored Dictionary lookup；current lookup 不代 source，Root Nullable key 仍由 body/binding KeyPolicy准入。
真实 SG builtin／inline双槽 Dictionary cold、Remove/Patch/Add Delta、独立阶段反例、完整源码回归和四进程组合包均已验，模型范围分开记录。
该片保留历史范围，P1c/d/P2保持后续边界。

P1b 第六片已完成真实 history 容器值规则的限定源码资格，问题与证据见
[DB-087 §17（归档）](../../../docs/archive/2026-10-13/0087-meta-bootstrap-refactoring-route.md#17-p1b-第六片真实-history-容器-upgrade)。
Root＋Array/List/Dictionary 共四对象，Key/Point/Grade V1→V2，Grade byte→ushort 经 Point 局部依赖；Root同Schema、Self/alias保持。
沿现有 ABI5／DTO V1，readonly零写、Discard/E1/E2保留State rewrite、容器Base→Delta和独立11边界均已通过。
本片仅新增测试、生产/wire/history与SG ABI/交付未变，build＋新12用例记证；未跑完整回归或包，无新增包资格，最后assets为source。
该片保留历史范围，完整P1b/P1c/d/P2未完成。

P1b 第七片已完成真实 SG 多参／泛型继承 current 与有限 provider history 的源码资格，问题与证据见
[DB-087 §18（归档）](../../../docs/archive/2026-10-13/0087-meta-bootstrap-refactoring-route.md#18-p1b-第七片多参泛型继承与-provider-history)。
Root＋两 closed Derived 的 private Base<U>／Pair<T> 和有序实参冷读／Delta，与另一个 Box<int>／Box<Point> accepted-history 端点分开验收。
开放／必要闭合 provider 的输出、缺失／错误 source DTO 三个cold拒绝，与current的warm wronglayout分层记证，不外推所有泛型仲裁。
本片仅新增测试，build零警告错／五新用例通过，生产/ABI/wire/SG/history交付未变，未跑完整回归或新包，assets source。
该片保留历史范围；跨程序集 current 后继见第八片，不外推全部 provider 仲裁或跨库升版。

P1b 第八片的三程序集继承代表已完成源码验收，**有限 P1b 停点已达**；精确模型、三用例计数和未覆盖集中
[DB-087 §19（归档）](../../../docs/archive/2026-10-13/0087-meta-bootstrap-refactoring-route.md#19-p1b-第八片跨程序集继承与有限停点)。
三份真实 SG 模板经 P0 cold meta／业务恢复、整 leaf Delta／准确 prior、NoChange 和 fresh 旧新图；本片生产零改，无新完整回归或包资格，assets source。
[P1c 首片（归档）](../../../docs/archive/2026-10-13/0087-meta-bootstrap-refactoring-route.md#20-p1c-首片共同业务规划内核) 提取共同 `ClassifyCaptured`／`PlanObjectWrites`；两 adapter 保真实 typed 编码和来源前检。当时P0的nextH维护由第二片共享NormalizedRevision的实际heads/H安装取代。
两真实头／length-prefix边界的计量见证及完整源码回归通过；未改wire/SG/交付、无新包资格，assets source，未合workspace/owner或公开切换。
[P1c 第二片（归档）](../../../docs/archive/2026-10-13/0087-meta-bootstrap-refactoring-route.md#21-p1c-第二片normalized-保存材料与发布前安装准备) 已验共享 normalized 保存材料、实际 heads/H 与发布前安装准备；冷读工厂自行加载准确 S 并冻结模型，保完整 source 与 rewrite。typed Commit/Dispose 拒绝重入不释放外层候选；Event 不准备或安装实际 State 存储材料，仍接受 meta。build、定向和完整源码回归通过，无新包／Crash资格。
已完成 [P1c 第三片（归档）](../../../docs/archive/2026-10-13/0087-meta-bootstrap-refactoring-route.md#22-p1c-第三片共同业务-workspace-与候选寿命)：[BusinessWorkspace／BusinessSaveCandidate](../BusinessWorkspace.cs) 合并 models、Capture、完整 baseline、根与候选寿命，保留两薄 backend／owner；Event 不建立 next baseline。真实根替换／cold Delta 和安装材料误接见证及完整源码回归已通过，未新增包或 Crash 资格。
已完成 [P1c 第四片（归档）](../../../docs/archive/2026-10-13/0087-meta-bootstrap-refactoring-route.md#23-p1c-第四片内部-split-ownerroot)：内部 backing、双 refs、发布基线、唯一 pending 和 prepare/commit/fault 收入 GraphResources，内部 owner/root 新读新写停点已达，定向与完整源码回归通过。P0Repository 的工厂外观拥有资源，显式借用外观只释放自身；沿用 P0 root payload／wire。catalog 工作区仍由各 P0Session 持有并按最新 accepted 准备，公开 Repository 外层未合；无新包或 Crash 资格，assets 为 source mode。
已完成 [P1c 第五片（归档）](../../../docs/archive/2026-10-13/0087-meta-bootstrap-refactoring-route.md#24-p1c-第五片冻结配置与准确-s-绑定)：StateModelRegistry.FrozenConfiguration 与 DecodedRevision.SplitBindingEnvironment 已通过定向和完整源码验收。环境从真实 Store／S 私有签发，同 S 显式复用、跨 S 独立 factory/runtime memo；稳定已注册 Model/Reader/Comparer 保原引用。hot SourceModels／P0Session 和公开入口未改，无新包或 Crash 资格，assets 为 source mode。
[P1c 第六片（归档）](../../../docs/archive/2026-10-13/0087-meta-bootstrap-refactoring-route.md#25-p1c-第六片repositorycheckout-执行环境) 已接真实 Repository 的内部 split 工厂与 BranchCheckout，构建、定向与完整源码回归通过。仅接 Create／Checkout／独立 State/Event 读取与保存、readonly 和 owner／占用 guard，默认公开工厂不切。checkout 执行 S 固定：有 State 取 State.S，无 State 取创建时最新 shared／空库 None；State 可推进存储 selected S，不换 hot models／preparation。新增 lazy closure 的构造观察与普通业务 roundtrip 分开记证，不保证任意跨 S 委托的领域值等价。其后继 history 元数据导航＋ref／tag／Fork 已完成，见 §26；Pair 已由第八片接通，见 §27；准备材料已由第九片接通，见 §28；第十片物理缓存已验，见 §29／§6.3／7.1。
[P1c 第七片（归档）](../../../docs/archive/2026-10-13/0087-meta-bootstrap-refactoring-route.md#26-p1c-第七片history-元数据与-ref-操作) 已接 metadata/history、ref-only 分支／tag／Move／Fork 与独立 ReadCheckpoint，构建、定向与完整源码回归通过。Fork 先从 selected head 恢复最近 State，再发布目标 branch，不借当前源／目标分支选 State；无 State 使用创建时 shared 环境。ref-only 无新 graph、不倒退 shared S，meta 仍由真实业务共同保存推进。ReadCheckpoint 两图各准确 S，共同物化保跨图 allocator 错误拒绝，保持 mutable 实例独立；该片历史范围不代本轮 Pair／缓存资格。
[P1c 第八片（归档）](../../../docs/archive/2026-10-13/0087-meta-bootstrap-refactoring-route.md#27-p1c-第八片split-readpair) 已接 split ReadPair，构建、定向与完整源码回归通过。两侧准确 S；同 reader-issued 环境才按原 Model／Preparation、完整状态与引用闭包共享，跨环境独立，仍共同物化保 mutable allocator guard。string 在已有 same-ID/head 候选内增加完整 Ordinal 内容 proof，不 intern 不同 ID、不新建 DTO cache。只读包括 Transient，不导入可编辑保存基线；身份断言只验当前优化。DB-080 最近 State 准备材料已由第九片接通，见 §28；第十片物理缓存已验，见 §29；具体接缝集中 §6.3／7.1。
[P1c 第九片（归档）](../../../docs/archive/2026-10-13/0087-meta-bootstrap-refactoring-route.md#28-p1c-第九片split-checkoutfork-准备材料复用) 已接 split Checkout／Fork 的单 State 准备槽，构建、定向与完整源码回归通过；DB-082 既有候选默认关闭，DB-081 热 seed 禁用。物理缓存第十片已验，见 §29；缓存不替代该 State 的准确 reader 环境、完整 source 或独立可变物化资格。
[P1c 第十片（归档）](../../../docs/archive/2026-10-13/0087-meta-bootstrap-refactoring-route.md#29-p1c-第十片owned-物理缓存) 已验 owned 物理缓存，构建、定向与完整源码回归通过：共用地址／frame+map 两组件／单预算 LRU，旧 adapter 保原计量，split 接实际固定角色帧与完整目录/H。值私有 owned 且只读，成功解析／重建后 admit，hit 前核 owner，每图／typed 仍核原关系与准确 S/model；内部预算透传供 zero／调小，不增公开 API。不从 prepared/input seed，不因上层失败回滚正确 raw/map。限定运行证据集中 §29；机制层 P1c 内部停点可进入 P1d，完整 P1 产品门禁尚未完成；后继 P1d 初始化／失败边界及进程／包门禁，再默认切换退旧，P2 private 后置。
[P1d 首片（归档）](../../../docs/archive/2026-10-13/0087-meta-bootstrap-refactoring-route.md#30-p1d-首片初始化与正常恢复) 已通过初始化／正常恢复的 tests-only 限定源码验收，构建、定向与完整源码回归通过，生产零改；两 CreateRef 不是 CU，缺正式 ref 不补建。lower CreateRef 故障＋fresh DG Open 不等于 DG.CreateNew 内部故障或 ProcessCrashOnly，VS writable 清理创建中 pending 遵原控制协议。结果统一记 §30；本目录不重复运行记录。保存进程的后继证据见下段；该片未验直接 factory 初始化，后继证据见第三片。

[P1d 第二片（归档）](../../../docs/archive/2026-10-13/0087-meta-bootstrap-refactoring-route.md#31-p1d-第二片真实-dg-processcrashonly-保存窗口) 已验 [真实 DG ProcessCrashOnly source 宿主](../../../experiments/MetaBootstrapCrashProbe/README.md) 的六个固定保存窗口。外部 Kill 后 fresh 核实际两根／准确 S、完成 orphan 保留与单 List Delta；首 CU End 两旧，全部 End 在 flush 前完整时两新。仅新增 solution 外宿主，生产／solution／test 源码零改；运行证据只在 §31。直接初始化 factory Kill／实际 Repository 返回边界由第三片补验，见下段；前片生产零改仅描述当片历史范围。

[P1d 第三片（归档）](../../../docs/archive/2026-10-13/0087-meta-bootstrap-refactoring-route.md#32-p1d-第三片直接初始化-kill-与实际返回-outcome) 已完成 actual factory 成功创建 ref1／ref2 后外部 Kill 与实际 typed Repository 返回／fault 的限定源码验收，构建、定向与完整源码回归通过。只有两个生产文件的局部 internal afterCreateRef callback，wire／public API／默认保持；BeforeInstall 是安装前 hook，不是实际 Install 本体故障。该片运行证据保留 §32；准确 Repository／history 包的后继验收见第四片，默认接线退旧与 P2 仍后置。

[P1d 第四片（归档）](../../../docs/archive/2026-10-13/0087-meta-bootstrap-refactoring-route.md#33-p1d-第四片准确包与-accepted-history) 已通过 [内部 friend 的准确包消费者](../../../experiments/PackageConsumerProbe/SplitRepositoryConsumer/README.md)：V1 seed／tag→V2 readonly Upgrade→latest shared 与旧 tag Fork→同／跨 S Pair→首次 rewrite Base→fresh List Delta／readonly。生产零改，包内 Generator／Build targets 与 accepted history、准确来源／实际 heads/H 均有证据；包运行与 source solution 回归分开记录于 §33。默认公开工厂仍 DB-086，内部资格不授 public-default 或完整 P1 产品停点。下一片默认前端先保 FS rotation 参数意义，再按调用清零退旧并迁两个 cache 小依赖，最后验非 friend 公开包；具体施工导航见 §33.4，P2 private 后置。

[P1d 第五片（归档）](../../../docs/archive/2026-10-13/0087-meta-bootstrap-refactoring-route.md#34-p1d-第五片公开默认接线与回归移接) 已把本目录的共同读写接入公开默认 Repository／BranchCheckout／GraphResources，产品回归移接与普通非 friend README 包验收通过。rotation／cache 分别传参，准确 S／Event/State／owner／发布前安装保持；旧 Schema/State getter 仅 internal 施工低层，后继按调用清零退出。普通非 friend README 故事不代完整 accepted-history 公开包，P2 private 后置；最终源码／普通包证据只在 §34，历史各片“默认未切”保当片含义。

[P1d 第六片（归档）](../../../docs/archive/2026-10-13/0087-meta-bootstrap-refactoring-route.md#35-p1d-第六片旧保存高层退出) 已删除旧 LoadedWorld／WorldWorkspace／PreparedWorldSave、旧 planner 与 Store 规划入口；共同 writer、workspace／候选及 WithSplitGraph 真实安装保留，有效安全见证迁移、最终源码回归与 Probe smoke 已验。SCB1／raw Store／旧 reader 另片退出，完整非 friend accepted-history 公开包再后置；不搬旧 StageCore 到新壳，不将历史源码链接当仍存 API。


[P1d 第七片（归档）](../../../docs/archive/2026-10-13/0087-meta-bootstrap-refactoring-route.md#36-p1d-第七片旧typed恢复束退出) 已删除 RevisionDecoder／RevisionReadSession／TypedObjectVersionReader、GraphReader 旧 Store/session 入口及 legacy 保存来源字段。ReadSplit 私有签发／完整成员与 heads/H、共同 Normalize／Pair／两阶段物化／leaf／候选检查保持；旧 typed cache 次数退役，强反例迁真实新 reader，CRC 补例与最终源码回归通过。该片的 SCB1／GraphResources 旧模式／raw Store／header codecs 后继退出已由 §37–38 完成，旧 probe 仍暂停当前 G、按需迁移；不造替代 reader，不改该片原资格或授完整 P1／新包资格。

[P1d 第八片（归档）](../../../docs/archive/2026-10-13/0087-meta-bootstrap-refactoring-route.md#37-p1d-第八片旧schema权威与owner模式退出) 已退出旧 SchemaStore／SCB1 catalog／RepresentationId／Base header 与 GraphResources legacy 模式，最终源码构建／完整回归通过限定验收。Snapshot 仅保所选不可变 view 的 lookup，成功 memo 仍复核标准 exact 依赖；真实清理、锁、根 grammar／cold 容量拒绝迁入唯一 owner。该片后继 raw 束退出见下段，原资格不扩到包、进程或性能。

[P1d 第九片（归档）](../../../docs/archive/2026-10-13/0087-meta-bootstrap-refactoring-route.md#38-p1d-第九片旧raw-storage束退出) 已退出旧 raw Storage 十一类型与旧 TailMeta codec，最终源码构建、定向与完整回归通过限定验收。共用 records／payload／精确计量、地址／只读字典与 owned LRU 保留；真实 P0 承接必要来源、Base 截链、循环、CRC／失败不 admit、完整地址键与组件预算规律，不造替代 Store。下一步直接验有限非 friend 公开 accepted-history 准确包，再接 P2；十九个有效旧包 lane 的资产原地保留并暂停当前 G，选择性迁移不是该门禁前置，也不自动恢复未迁 lane 资格。不新增包、进程或性能证据，运行记录只维护 §38。

验证命令从仓库根执行，准确上游 pin 及 source/package 模式见[依赖指南](../../../docs/storage-dependency.md)：

```powershell
dotnet build DurableGraph.slnx -c Release -m:1 -nr:false -p:UseStorageSources=true -p:StorageSourceRoot=E:/repos/Atelia-org/atelia-storage
dotnet test tests/DurableGraph.Persistence.Tests/DurableGraph.Persistence.Tests.csproj -c Release --no-build --no-restore -p:UseStorageSources=true -p:StorageSourceRoot=E:/repos/Atelia-org/atelia-storage --filter FullyQualifiedName~MetaBootstrap

dotnet test tests/DurableGraph.Tests/DurableGraph.Tests.csproj -c Release --no-build --no-restore -p:UseStorageSources=true -p:StorageSourceRoot=E:/repos/Atelia-org/atelia-storage --filter FullyQualifiedName~MetaBootstrap
dotnet test DurableGraph.slnx -c Release --no-build --no-restore -m:1 -nr:false -p:UseStorageSources=true -p:StorageSourceRoot=E:/repos/Atelia-org/atelia-storage
```

历史运行结果只维护于冻结归档；当前未完成资格统一见 [DB-087 公开包门禁](../../../docs/design-branches/0087-meta-bootstrap-refactoring-route.md#public-package-gate)。本目录不另维护产品能力清单或长期路线图。
