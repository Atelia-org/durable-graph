<a id="db-087-p0持久-meta-自举实验"></a>
# DB-087：meta 自举内部原型

本目录是产品代码内的自举原型，承担纵向实验与后续接线；公开 Repository 尚未切换。问题与分片边界见
[DB-087 §6](../../../docs/design-branches/0087-meta-bootstrap-refactoring-route.md#6-建议的重构任务顺序)。

P1 会继续演进这里的 catalog、共同 writer 和发布协议，恢复逻辑并回既有核心；临时 driver／owner
在公开接线时合并，不整体丢弃 P0。已经提取的共同 payload 和受控容器 emitter 继续保留。
代码去向、依赖顺序与尚未敲定的问题集中在 DB-087，不以 `P0` 目录名推断最终产品边界。

实验只把两种变化来源汇入共同保存：业务 Capture/比较；meta 按逻辑 key 归约修改。
图 writer 接收来源、根、owned ObjectVersion records 与删除集合；取得完成对象 batch 地址后，
用真实 Dictionary body 保存存活目录，再追加固定图入口。目录自身没有领域 ObjectId。
descriptor 使用其 meta ObjectId；每个动态 Base 的解释仍受其 batch 的准确 Schema 根约束。

真实 FS/VS 拥有文件和两个 refs。共同确认后才接受候选：Event 接受 meta、推进业务 Head，
但保留最近 State 的业务保存基线。关闭全部资源后重新打开，必须只从实际发布根恢复。
P0 wire 和固定员工闭包允许调整；不提供旧格式兼容、私有分支策略或公开入口切换。

从 [P0Session](P0Session.cs) 看两侧准备与安装；
[P0GraphStore](P0GraphStore.cs) 保存共同 batch／目录／图并校验来源。
第四片已将内部发布状态迁入 [GraphResources.MetaBootstrap](../GraphResources.MetaBootstrap.cs)，
[P0Repository](P0Repository.cs) 仅作实验外观；内部资源 owner 已通过第四片源码验收。
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
完整源码回归与隔离包四进程验收统一在 [DB-087 §12](../../../docs/design-branches/0087-meta-bootstrap-refactoring-route.md#12-p1b-首片schema-员工闭包与固定-list-路由)。

P1b 第二片以既有记录验证闭合泛型／enum/Nullable/inline，无生产代码或 ABI 改动。
真实 SG 的两个 Box/Cell 闭合实例可冷读共享／环并续写实际 Delta；独立反例验证有序实参、版本与准确视图，
范围和相关回归见 [DB-087 §13](../../../docs/design-branches/0087-meta-bootstrap-refactoring-route.md#13-p1b-第二片闭合泛型与-enum-执行资格)。
该片不授予泛型历史 Upgrade 或新包资格。

P1b 第三片 builtin-only List 结构固定路由已完成源码与隔离包验收，问题与证据见
[DB-087 §14](../../../docs/design-branches/0087-meta-bootstrap-refactoring-route.md#14-p1b-第三片builtin-only-list-结构固定路由)。
第三片当时选 typed ABI3 的有限 string/employee 表、dynamic descriptor 与 structural TypeExpr 三路；
List root 递归无 Named，codec 2 由 ABI 固定，精确 `List<int>` 专码移除，员工 Schema/DTO 仍 V1。
结构 body 能力不扩大 meta 员工白名单；`List<long?>`／共享 inner 的 nested List 已验冷读、Delta与独立拒绝，真实包四进程正常冷读／续写通过。
此处保留第三片历史范围，不据其授予 Array／Dictionary 资格。

P1b 第四片 Array 路由已完成源码与隔离包验收，问题、选定设计与证据见
[DB-087 §15](../../../docs/design-branches/0087-meta-bootstrap-refactoring-route.md#15-p1b-第四片array-结构与动态路由)。
D variant4 复用完整 Array TypeExpr、Element、显式 codec1，Version0／Fields和Base空；
重建完整 ArrayLayout 后核 Type，并独立核 exact inline 依赖，shape 只在 body。
第四片当时 typed ABI4 固定 List2／Array1、拒旧3，Schema/DTO仍V1且不增字段；meta 有限白名单不扩大。
真实 SG `int?[]`／`Point?[,]` cold与稀疏 Delta、raw 边界、完整源码回归和四进程组合包均已验；NoChange S2只来自源码 SG。
该片保留历史范围，公开 P1c/d 和 P2 不随其切换。

P1b 第五片 Dictionary 双槽与 source lookup 已完成源码与隔离包验收，问题和证据见
[DB-087 §16](../../../docs/design-branches/0087-meta-bootstrap-refactoring-route.md#16-p1b-第五片dictionary-双槽与-source-lookup)。
D5复用 Type＋有序 Key1/Value2 Fields，Version0、Element/Base空、codec1；ABI5明确 Dictionary1/List2/Array1、拒旧4，DTO仍V1。
完整引用检查后、Normalize 前验证所有 stored Dictionary lookup；current lookup 不代 source，Root Nullable key 仍由 body/binding KeyPolicy准入。
真实 SG builtin／inline双槽 Dictionary cold、Remove/Patch/Add Delta、独立阶段反例、完整源码回归和四进程组合包均已验，模型范围分开记录。
该片保留历史范围，P1c/d/P2保持后续边界。

P1b 第六片已完成真实 history 容器值规则的限定源码资格，问题与证据见
[DB-087 §17](../../../docs/design-branches/0087-meta-bootstrap-refactoring-route.md#17-p1b-第六片真实-history-容器-upgrade)。
Root＋Array/List/Dictionary 共四对象，Key/Point/Grade V1→V2，Grade byte→ushort 经 Point 局部依赖；Root同Schema、Self/alias保持。
沿现有 ABI5／DTO V1，readonly零写、Discard/E1/E2保留State rewrite、容器Base→Delta和独立11边界均已通过。
本片仅新增测试、生产/wire/history与SG ABI/交付未变，build＋新12用例记证；未跑完整回归或包，无新增包资格，最后assets为source。
该片保留历史范围，完整P1b/P1c/d/P2未完成。

P1b 第七片已完成真实 SG 多参／泛型继承 current 与有限 provider history 的源码资格，问题与证据见
[DB-087 §18](../../../docs/design-branches/0087-meta-bootstrap-refactoring-route.md#18-p1b-第七片多参泛型继承与-provider-history)。
Root＋两 closed Derived 的 private Base<U>／Pair<T> 和有序实参冷读／Delta，与另一个 Box<int>／Box<Point> accepted-history 端点分开验收。
开放／必要闭合 provider 的输出、缺失／错误 source DTO 三个cold拒绝，与current的warm wronglayout分层记证，不外推所有泛型仲裁。
本片仅新增测试，build零警告错／五新用例通过，生产/ABI/wire/SG/history交付未变，未跑完整回归或新包，assets source。
该片保留历史范围；跨程序集 current 后继见第八片，不外推全部 provider 仲裁或跨库升版。

P1b 第八片的三程序集继承代表已完成源码验收，**有限 P1b 停点已达**；精确模型、三用例计数和未覆盖集中
[DB-087 §19](../../../docs/design-branches/0087-meta-bootstrap-refactoring-route.md#19-p1b-第八片跨程序集继承与有限停点)。
三份真实 SG 模板经 P0 cold meta／业务恢复、整 leaf Delta／准确 prior、NoChange 和 fresh 旧新图；本片生产零改，无新完整回归或包资格，assets source。
[P1c 首片](../../../docs/design-branches/0087-meta-bootstrap-refactoring-route.md#20-p1c-首片共同业务规划内核) 提取共同 `ClassifyCaptured`／`PlanObjectWrites`；两 adapter 保真实 typed 编码和来源前检。当时P0的nextH维护由第二片共享NormalizedRevision的实际heads/H安装取代。
两真实头／length-prefix边界的计量见证及完整源码回归通过；未改wire/SG/交付、无新包资格，assets source，未合workspace/owner或公开切换。
[P1c 第二片](../../../docs/design-branches/0087-meta-bootstrap-refactoring-route.md#21-p1c-第二片normalized-保存材料与发布前安装准备) 已验共享 normalized 保存材料、实际 heads/H 与发布前安装准备；冷读工厂自行加载准确 S 并冻结模型，保完整 source 与 rewrite。typed Commit/Dispose 拒绝重入不释放外层候选；Event 不准备或安装实际 State 存储材料，仍接受 meta。build、定向和完整源码回归通过，无新包／Crash资格。
已完成 [P1c 第三片](../../../docs/design-branches/0087-meta-bootstrap-refactoring-route.md#22-p1c-第三片共同业务-workspace-与候选寿命)：[BusinessWorkspace／BusinessSaveCandidate](../BusinessWorkspace.cs) 合并 models、Capture、完整 baseline、根与候选寿命，保留两薄 backend／owner；Event 不建立 next baseline。真实根替换／cold Delta 和安装材料误接见证及完整源码回归已通过，未新增包或 Crash 资格。
已完成 [P1c 第四片](../../../docs/design-branches/0087-meta-bootstrap-refactoring-route.md#23-p1c-第四片内部-split-ownerroot)：内部 backing、双 refs、发布基线、唯一 pending 和 prepare/commit/fault 收入 GraphResources，内部 owner/root 新读新写停点已达，定向与完整源码回归通过。P0Repository 的工厂外观拥有资源，显式借用外观只释放自身；沿用 P0 root payload／wire。catalog 工作区仍由各 P0Session 持有并按最新 accepted 准备，公开 Repository 外层未合；无新包或 Crash 资格，assets 为 source mode。
下一片先做冻结代码→准确 S 的最小探针与绑定裁决，再移 Repository/history 和 Pair／缓存，接 [§6.3](../../../docs/design-branches/0087-meta-bootstrap-refactoring-route.md#63-p1b-首片续工入口)。
现有公共路径保持，P1d 门禁后才默认切换／删旧路径，P2 另接 private；无具体反例不扩未穷举组合。

验证命令从仓库根执行，准确上游 pin 及 source/package 模式见[依赖指南](../../../docs/storage-dependency.md)：

```powershell
dotnet build DurableGraph.slnx -c Release -m:1 -nr:false -p:UseStorageSources=true -p:StorageSourceRoot=E:/repos/Atelia-org/atelia-storage
dotnet test tests/DurableGraph.Persistence.Tests/DurableGraph.Persistence.Tests.csproj -c Release --no-build --no-restore -p:UseStorageSources=true -p:StorageSourceRoot=E:/repos/Atelia-org/atelia-storage --filter FullyQualifiedName~MetaBootstrap

dotnet test tests/DurableGraph.Tests/DurableGraph.Tests.csproj -c Release --no-build --no-restore -p:UseStorageSources=true -p:StorageSourceRoot=E:/repos/Atelia-org/atelia-storage --filter FullyQualifiedName~MetaBootstrap
dotnet test DurableGraph.slnx -c Release --no-build --no-restore -m:1 -nr:false -p:UseStorageSources=true -p:StorageSourceRoot=E:/repos/Atelia-org/atelia-storage
```

本次结果和未验证项统一记录于 DB-087；本目录不另维护产品能力清单或长期路线图。
