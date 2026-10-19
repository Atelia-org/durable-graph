# DurableGraph 产品开发工作集

> 校准：2026-10-19；DB-096 统一 Catalog 删除片见当前焦点；081 资格实验否定、热准入未接线，082 候选已实现但默认关闭；DB-073/074 仍为待修订草稿。本文只维护当前能力、边界与续工入口。
> 文档不是实现授权；事实以当前源码、测试和工具输出为准。

## 从这里继续

先读本文，再按任务选择一份材料；不需要按 DB 编号通读历史。

- 下游首次接入：先看[根 README](../README.md) 的完整保存/重开示例、包准备与 Schema 升级步骤。
- 理解产品目标与已选约束：[目标设计](../docs/DurableGraph-target-design-v0.md)。
- 查首选用语、概念示意和对应代码：[项目术语表](../docs/DurableGraph-glossary.md)；后续文档、代码命名与注释的一致化以此定位差异。
- 选择下一片、查未完成决策与问题：[后续路线](../docs/DurableGraph-research-roadmap.md)。
- 当前循环维护的线索、裁决与续接：[维护状态](../maintenance/2026-10-14.md)。
- 查某项实现的来由或验收：[设计与施工记录索引](../docs/design-branches/README.md)。
- 查历史实验：[实验簿入口](../docs/DurableGraph-lab-notebook.md)；重跑独立实验：[Probe 导航](../experiments/README.md)。

默认搜索 src/tests 与活动回归；已淘汰设计和实验只在明确历史问题时查[Git 来源](../experiments/ARCHIVE.md)。

## 当前焦点

2026-10-19：当前位于隔离实现分支 `db096/branch-repository-cutover`，Storage pin `f8868b5`。真实 Repository 已接一 Branch 一 Head、D/T 增量管理图、HS 历史来源与 Branch/Commit/WorkingCopy，业务 Parent 已删除。根 solution 调用闭包及原 Persistence/Runtime 回归已恢复，README 和链接 PendingRecovery 已迁移；产品 API/格式本轮未再改变。准确源码资格见[B2 施工记录 §9](../docs/design-branches/0096-history-and-head-model/branch-cutover-plan.md#9-b2-调用闭包与回归迁移)，当前队列只维护在[DB-096 工作集](../docs/design-branches/0096-history-and-head-model/PROJECT-STATE.md)。下一片选一个读取成本收窄问题；定向 origin、剩余包消费者/普通包验证和术语导航仍待闭合，未合回 master。下面未逐项改写的旧阶段记录只保其历史基线，不外推旧包资格。

[DB-096：版本、历史与命名入口](../docs/design-branches/0096-history-and-head-model/README.md)既有三控制Head与独立书签目录仍是当前产品布局；最初来源见[接入切片](../docs/design-branches/0096-history-and-head-model/headstore-adaptation.md)，新依赖资格见 B1。用户已确认只追加/旧点另开/fork立即可见，继承历史为首选；紧凑codec的开发期误用诊断足够，不增加每引用16B scope。旧布局在新依赖下已回归，新历史模型另需真实纵切，不把它们混为同一次验收。

workshop 反馈 3 的纯 State 入门已完成：[README](../README.md#最短接入路径)以连续 State 保存、历史读取、旧 State 分叉及冷开续写为主路径，Event 为可选后续示例。源码与新 G 原文普通包验收通过，运行时/API/格式保持；准确 G/S、证据与范围见[验收记录](../docs/DurableGraph-lab-notebook.md#state-only-quickstart)，本片无剩余必经施工。

[DB-095：打开意图与业务保存准入](../docs/design-branches/0095-repository-open-intent/README.md)保留 Open 管资源、创建/签出/Fork 取得编辑能力的边界。其旧 Shared/Private 归属由 DB-096 单 Catalog 取代；旧源码与包资格仅解释 DB-095 当时版本。

Schema-history 发布保护已实施并完成限定验收：Build 的 Publish/Verify 在同目录互斥内重读、校验与发布，
保同形幂等及冲突写前拒绝；SG/targets 输入和历史格式保持。完整构建仍串行，冻结历史后可并行 Verify。
准确新 G、源码回归和普通包正反例见 [完成记录](DurableGraph.Build/README.md#publication-coordination-validation)。
本片无剩余必经施工；仅在真实消费者必须消除多记录重叠编译失败时重访稳定输入名单，不自动开启该方案。

H02 / workshop feed 反馈的[候选交付闭环](../docs/storage-dependency.md#candidate-delivery)已实施：`eng/Prepare-DurableGraphCandidate.ps1` 导出固定 G、完整相对路径配置、README 同源 QuickStart 与当前示例原包闭包；原文 runner 直接执行命令。干净来源的新 G 已通过搬迁、空 cache、本地唯一源和既有 Upgrade/Fork/Verify 限定验收，来源与证据只查[完成记录](../docs/storage-dependency.md#candidate-delivery-validation)。本片无剩余必经施工；公开 preview 保留为分发选择，不改运行时/API/格式。

[DB-093：显式存储分类有效性](../docs/design-branches/0093-explicit-storage-classification/README.md)已实施并完成源码与新 G 普通包的限定验收。H01 等普通 class/struct 的不支持 backing 或无目标分类报 DG0024；真实 Transient 保排除，错误声明即使抑制诊断仍无 current 生成资格。未标隐式 storage、record 既有纪律、生成路线与持久布局保持。具体合同、包来源与结果只查 DB-093；[机制 Probe](../experiments/ExplicitStorageClassificationProbe/README.md)继续只承担符号/PE 观察。本片无剩余必经施工，后续由真实消费者反馈选增量。

[DB-094：有限支持真实 property backing field](../docs/design-branches/0094-backing-field-support/README.md)仅为前期调研储备，范围/规模与生成路线待摸清，不是 DB-093 的并行工单。剩余工作和重访条件只查[路线图](../docs/DurableGraph-research-roadmap.md)。

此前[循环维护](../maintenance/2026-10-14.md)的产品改动已整合，保持完整 history/source/typed 图验证、fault/lifetime 与复制隔离；实际改动、验证和保留裁决只查任务状态，不复开已完成命名分片。

[DB-092](../docs/design-branches/0092-type-namespace-refactoring/README.md)的 A／B／C／D1–D6 及 E1／E2／E3 已限定验收，有限源码组织与本轮候选已收敛；源码入口见[导航](DurableGraph.Persistence/README.md)。当前公开内容操作为 `StatePreparationBinding<T>`／`StringBodyPreparer`，Generator 为 `DurableGraphGenerator`，仓库资源 owner 为 `RepositoryResources`。纯槽转换归 Schema 的 `DurableFieldInfo`，保编号与完整相等。
公开历史角色 `CheckpointKind` 与 [D6 发布合同](../docs/design-branches/0092-type-namespace-refactoring/README.md#d6-status) `RepositoryPublicationException`／`RepositoryPublicationOutcome` 位于根 namespace，仍由 Persistence 程序集提供；内部 StoredGraphKind／阶段 hook 分责保持。三个 ValueProjection helper 与 ObjectStateKind 保名。D6 源码、根回归和 API 映射，以及准确新 G 普通 SG／Build 编译、新库保存／ref-only 分支／tag／冷开只读通过；普通包没有运行故障注入，三个 outcome／fault 语义由根确定性回归验证。各片包来源与有限资格只查当片记录，生成合同及旧输出清理查 D1／D2 和[构建指引](../README.md)。
本轮无剩余必经分片；后续由真实消费／依赖问题触发，重访条件只维护在[路线图](../docs/DurableGraph-research-roadmap.md)。按[原型迭代约束](../docs/DurableGraph-target-design-v0.md#原型迭代与数据兼容)直接迁移并重编译，实验存档可 reset 重生成，不设旧包兼容门槛，不预设程序集拆分。

[DB-091 清理](../docs/design-branches/0091-retired-material-cleanup/README.md)已退出淘汰文档与实验执行代码；当前机制和活动门禁保持，旧模型仅保编译资产，完成证据集中在该记录。

[DB-089 checkout 环境与 factory 查询收窄](../docs/design-branches/0089-checkout-environment-and-factory-query-refactoring/0089-checkout-environment-and-factory-query-refactoring.md)的有限 A／B／C 范围已完成：protected 查询保准确核心解析，内部反例与真实 SG／phantom 正控保全；源码和新 G 普通包通过限定门禁，包括准确 CS0122、外部派生 override／自身调用、Split 热读回／同 owner 重签出与全关冷开增量续写。长期合同已归入[目标设计](../docs/DurableGraph-target-design-v0.md#模型执行环境与-factory-行为)，结果证据集中于 DB-089。当前无剩余必经施工或设计阻塞；下一增量由真实消费者反馈选择，条件延期见[路线图 §4.3](../docs/DurableGraph-research-roadmap.md#43-checkout-执行环境与-factory-查询入口)，不自动开启 refresh、保存读回或跨 S 共享。

[DB-088 实施与限定验收](../docs/design-branches/0088-development-schema-workflow/0088-development-schema-workflow.md#7-实施结果与限定验收)已完成：新根 overrides／container replacements 与专用状态退出，必要源码回归已迁移；新 G 的 Development、Split、PrivateContainer 三条普通包故事通过，第三轮复核未发现缺口。本案无剩余必经施工；默认 Publish、主／次版本、V3/V4 迁移压缩与自动重放仍见[路线图 §4.2](../docs/DurableGraph-research-roadmap.md#42-开发-schema-与正式定稿)。

[DB-087 当前停点](../docs/design-branches/0087-meta-bootstrap-refactoring-route/0087-meta-bootstrap-refactoring-route.md#current-status)：约定的有限 P1／P2 产品范围已完成。默认公开路径使用 meta 自举；DurableSchema 完整逻辑内容经固定员工 DTO／工厂恢复，存活 Dictionary 与 descriptor 共用对象版本保存，只有一份业务保存／恢复和发布 owner。旧保存／typed reader／Schema 权威／raw Storage 束已退出，共用 payload／计量、地址、只读字典与 owned LRU 保留。

当前保存统一跟随 latest Shared Catalog；同 key 异形拒绝，新增定义与业务目录走同一两 Head CU。无新增 Catalog 时只发布业务目录。Private/ToShared 入口和私有来源、推广/remap、完整 selected-view admission 扫描退出；P0R2 根准入隔离旧布局。
Open 只选择资源读写；Shared 命名创建/签出/Fork 取得可保存工作副本。业务恢复／比较取最近 State.S；checkout 执行 S／models 在寿命内固定，State 保存可推进 selected S。历史图与对象 batch 的准确 Schema 继续保留，开发同版本异形试验在完整关闭库的独立副本进行。

准确 origin／selected／execution S、共同 source／pending／fault owner、history／Pair／State 槽及 owned 物理缓存保持。Event／Discard 不推进 State 或消费 rewrite；物理命中不免每图来源／完整布局／refs 检查，不新建 typed DTO cache。ABI5／员工 DTO V1，DB-081 热 seed 禁用、DB-082 默认关闭。

Development、Split、容器升级的当前消费者见 [PackageConsumerProbe](../experiments/PackageConsumerProbe/README.md)，本次资格见删除切片。DB-088/089 的旧包结果保各自版本意义；源码精确身份／source／H 与普通包观察各保边界。

DB-087 原有限停点与 DB-088 后继删减均已完成；其他增量与条件延期只维护在[路线图 §4.1](../docs/DurableGraph-research-roadmap.md#41-schemastore-复用-statestore-与联合版本视图)。旧包执行 lanes 已退出，保留模型仅作编译资产。内部名称／wire 未冻结，共同持久化内核是当前产品路径。

DB-087 保当前契约和有限停点审计，旧逐片过程冻结于[完整稿（Git 历史）](../docs/design-branches/0091-retired-material-cleanup/README.md#git-history "Git 6dbaf0e:docs/archive/2026-10-13/0087-meta-bootstrap-refactoring-route.md")（`ecc8068`）。续工先核实际 HEAD／工作树；DB-089 C 的新 G 包结果保历史身份；当前 DB-096 已重新 restore 为 HeadStore source mode。包交付仍须按[依赖指南](../docs/storage-dependency.md)准备准确 feed 并重新 restore，不混用输出或将 source build 当成 pack 证据。用户已允许按需自主本地 Git 提交，验收／文档同步后提交。

[DB-086](../docs/design-branches/0086-new-storage-refactoring-plan/README.md)只保现行正常 backing 恢复、完整帧稳定、必要读取、fault/lifetime 与离线救援约定；物理地址与轮转由 FS/HS 承担，准确 pin 和操作见[依赖指南](../docs/storage-dependency.md)。

[DB-084 DG tag 接入](../docs/design-branches/0084-eventjournal-immutable-tags-slice/0084-durablegraph-tags-implementation.md) 已实现公开 CreateTag/ResolveTag。
旧上游 bytes tag 接入已由 [DB-096 书签目录](../docs/design-branches/0096-history-and-head-model/headstore-adaptation.md)取代；不可变绑定由 DG 拥有。State 和 Event-first 都可跨重开定位，解析签发当前打开地址，不物化图或推进分支。
普通校验失败保持健康；发布异常映射既有 outcome 并同步 fault，重开按名字检查实际结果，不自动重试。物理尾恢复由底座承担。
准确公开包、故障与真实包证据集中于接入记录。下一步由下游固定书签/历史浏览/分叉续写的真实反馈选择产品增量。

[DB-082 不可变叶复用](../docs/design-branches/0082-prepared-immutable-leaf-reuse-slice/0082-prepared-immutable-leaf-reuse-implementation.md) 已实现内部候选，默认保持关闭。
候选在同一 DB-080 State 材料内复用完整成功恢复的合格叶，保持 mutable 隔离、身份预登记及交付/证书边界。
A/B 显示 Family 物化改善，但完整 Fork 未见稳定收益；不以减少 Allocate/Hydrate 次数替代总成本判据。
重跑方法、逐批数据、正确性与真实包证据集中在实施记录。恢复优化等待真实业务瓶颈。

[DB-081 热提交资格实验](../docs/design-branches/0081-hot-commit-restoration-material-slice/0081-hot-commit-restoration-material-experiment.md) 按原分片停点保留否定结论。
现有 reader body 可执行 Capture/factory 未触及、随数据变化的标准依赖检查；没有充分的无读回资格证明，热提交不接入槽。
反例与历史证书隔离见证进入回归；未改产品 API、发布或 provider 合同。重启条件与备选只维护在实验记录及路线图。

[DB-080：最近 State 准备材料复用](../docs/design-branches/0080-prepared-checkpoint-reuse-slice/0080-prepared-checkpoint-reuse-implementation.md) 已实施并独立验收。
Checkout/Fork 独立选择请求历史位置后复用一个精确 State 槽；默认准确 reader 环境／完整标准依赖接线源于 [DB-087 归档 §28（Git 历史）](../docs/design-branches/0091-retired-material-cleanup/README.md#git-history "Git 6dbaf0e:docs/archive/2026-10-13/0087-meta-bootstrap-refactoring-route.md#28-p1c-第九片split-checkoutfork-准备材料复用")，命中仍独立物化和导入保存身份。旧全局 Schema 后登记不污染已 pin 的历史 S。
证书覆盖预闭合 binding/Upgrade 计划的标准检查及首次物化依赖；无 State 请求完全绕槽，失败不换槽，fault/Dispose 释放。
默认启用的依据是实测高命中路径准备计数和分配下降；没有完整 Fork 普遍加速或总堆有界的承诺，具体证据只保留在实施记录。

[DB-079 共用核心](../docs/design-branches/0079-shared-graph-restoration-core-slice/0079-shared-graph-restoration-core-implementation.md)继续承担单图、Checkpoint 与 ReadPair 的完整准备/两阶段物化；
只读入口不使用 State 驻留槽或导入保存身份。完整 source、head/H、rewrite、字符串身份及 ID 游标保持，mutable 独立性不变。

[078-A](../docs/design-branches/0078-editable-checkpoint-fork-slice/0078-a-repository-free-history-implementation.md) 的非泛型入口、自由历史与跨类型 State，
以及 [078-B](../docs/design-branches/0078-editable-checkpoint-fork-slice/0078-b-checkpoint-history-query-implementation.md) 的独立 Checkpoint/固定查询、
[078-C](../docs/design-branches/0078-editable-checkpoint-fork-slice/0078-c-branch-checkout-fork-implementation.md) 的每分支占用/named Fork 保持。
DB-078 已形成无缓存优化也正确的产品停点；[DB-083](../docs/design-branches/0083-checkpoint-api-user-stories/0083-repository-checkpoint-api-user-stories.md) 的 tag 目标由 DB-084 接入。
应用持久保存自身执行阶段和处理进度；库没有 PendingEvent，不从 E/S 次序推断业务完成，也不提供外部副作用 exactly-once。

[DB-076–082 路线](../docs/design-branches/0076-efficient-graph-fork-technical-path/0076-efficient-graph-fork-technical-path.md#10-分片施工导航) 的当前续工入口：
082 已完成候选实验，默认不启用；仅在真实叶模型/并存图内存压力或物化占比提供新证据时重跑，不自动扩展缓存体系。
081 不再作为必经工单；一般新 State 的 reader 完整证明需要先解决，不能以 Capture 或旧历史证书代替。
无 State 请求绕过材料槽/实验叶表，优化关闭仍须正确；热提交准入未实施，叶实例复用只在内部实验开关下可用。
[DB-084 不可变 tag](../docs/design-branches/0084-eventjournal-immutable-tags-slice/0084-eventjournal-immutable-tags-slice.md) 已按准确公开 package/revision 接入；
旧栈接入证据保留历史含义；当前 Binary/FS/HS 六包 pin 和源码入口见[依赖指南](../docs/storage-dependency.md)。
[DB-073](../docs/design-branches/0073-repository-scoped-weak-reference-cache/README.md) /
[DB-074](../docs/design-branches/0074-efficient-fork-deferred-directions/README.md) 继续 **Draft / 尚待进一步修订**，不作为并行工单。

[DB-075](../docs/design-branches/0075-immutable-leaf-proof-and-family-refactor/0075-immutable-leaf-proof-and-family-refactor.md) 已完成共同候选规则与生成的最终结构核对，
两条路径均可交付合格叶的 true；验证见[施工工单](../docs/design-branches/0075-immutable-leaf-proof-and-family-refactor/0075-immutable-leaf-implementation-work-order.md)
及[真包消费者](../experiments/PackageConsumerProbe/ImmutableLeafConsumer/README.md)。后续使用该 capability 的模型程序集须采用修复后的生成结果，
旧 DB-072 已编译消费者内嵌常量 true，只升级 runtime 不修复。
能力现状（经 DB-075 修订）：`StateModelBinding.IsImmutableLeaf` 通道保持公共可选构造参数、手写默认 false；
候选资格由同编译非泛型闭包决定——全部实例字段显式声明 readonly 且进入 durable 管线，允许 19 种内建、
同编译 enum/Nullable/递归 inline 与显式 readonly 字段的 record；record 全部隐式 backing、物化为隐式实例字段的主构造捕获、
闭合泛型、外部成员与 string/引用/Transient 仍 false；最终结构在每次 binding 创建时核对，失败仅失去资格。

存储依赖采用独立 atelia-storage 的六包候选或显式源码联调，准确版本/来源以
[StorageDependency.props](../eng/StorageDependency.props)为准。当前候选未公开发布，日常源码施工须显式指定 source mode；
包模式使用完整本地 feed 和独立配置，不回退旧包，不推定 sibling HEAD 行为。
源码只用于 build/test，pack 必须消费准确存储包并使用独立 DG 版本；流程集中于[依赖指南](../docs/storage-dependency.md)。

[DB-071：程序集与命名空间组织](../docs/design-branches/0071-assembly-namespace/0071-assembly-namespace-organization-review.md) 已实施：
七项目依赖保持；底层包为 Serialization/Storage，上层为 Persistence，主程序集内分根/Schema/Runtime。
签名、产品回归、真实包、旧包续写和隔离 DramaBoard 证据集中在[验收记录](../docs/design-branches/0071-assembly-namespace/0071-assembly-namespace-validation.md)。
[DramaBoard 试用反馈](../../drama-board/docs/feedback/durablegraph/004-db071-namespace-adaptation.md) 未发现适配或持久化问题，
报告 527 项测试及真实旧包存档续写通过；用户已批准本库提交，正式 pin 由下游固定到本次提交。
后续回到真实业务长轨迹与保存成本反馈，不自动开启新能力。

[DB-070：读取放大阈值默认选择](../docs/design-branches/0070-read-amplification-default/README.md) 将 EventHistory 生效默认改为 `{5,5}`。
选取依据是稳定小 Delta 模型中约 25% 的额外 Base 摊销开销；不是实测最优值或空间/读取上限。
用户的 `{3,5}` 冷读方向与 `{11,5}` 存储方向配置见[根 README](../README.md#调整保存策略)；测试基准独立保持 `{3,5}`。

[DB-069：热保存基线增量计量](../docs/design-branches/0069-incremental-save-baseline/README.md) 已实施：
已验证的 head/H 随 DTO 基线增量推进，受控热保存免除旧对象链重复读取；精确 payload、Event/State 与失败边界保持。
独立审阅、前后测量、源码回归与真实包验证已通过。
下一步按真实长轨迹反馈评估 map 回溯与全量 Base 准备，重访条件见路线图，测量证据仅保留在本片。
当前目录用员工 Dictionary 的实际 Base/Delta payload 与重建成本选择表示，见 [GraphRecordStore](DurableGraph.Persistence/GraphRecordStore.cs)；Event 不接受 State 基线。

[DB-068：record class 领域模型](../docs/design-branches/0068-record-class-model-slice/README.md) 已贯通：
IDurableObject 完全替代并移除 DurableBase，普通 class/record 共用资格和用户自建继承链；
positional/backing storage、泛型、跨库继承及历史升级复用现有对象管线。
真实旧包→新接口续写和同版 class→record 的 history 均保持；证据集中在分片记录。
下一步回到下游真实模型/业务升版和较长轨迹，按需求评估新的摩擦；
下游升级包须迁移接口并重编译，但无需立即把普通 class 改 record。
任意外部基类、深不可变/集合内容相等和新容器不随本片开放。

[读缓存 DB-067](../docs/design-branches/0067-owned-revision-read-cache-design/README.md) 已实施并验收：
正常 Store 统一有界缓存 owned frame / 完整 map，地址字典采用 keys/values 双数组二分，已有 local records 直接二分定位。
日常 append-only、仅离线救援截断且随后新开 Store 的执行纪律见 [AGENTS](../AGENTS.md#persistent-data-discipline)。
生命周期接线、产品回归、真实包验证及新表示测量已完成；证据集中在 DB-067，后续优化由真实工作负载触发。

[DramaBoard 首轮 API 反馈](../../drama-board/docs/feedback/durablegraph/001-eventhistory-api.md) 的近期改进已由
[DB-065](../docs/design-branches/0065-event-history-consumer-contract-slice/README.md) 完成：默认调用、包内 XML 文档、
[引用事件快照与恢复示例](../experiments/PackageConsumerProbe/EventHistoryRecoveryConsumer/README.md)，以及 README 原文执行验证。
恢复示例与内部故障测试采用应用自身的 E/S 协议判断处理进度；已发布 S 不重放，失败后重新取得 State/Event，不复用旧的可变图。
现有提交/格式、DB-064 只读共享与可写隔离合同保持。
第二轮 [ReadPair 反馈](../../drama-board/docs/feedback/durablegraph/002-readpair-sharing-contract.md) 已完成于
[DB-066](../docs/design-branches/0066-readpair-comparison-and-transient-contract-slice/README.md)：共享判断改用可选完整状态比较，
缺 proof 保守不共享，真实错误传播。普通/Family SG 自动登记，数组/List/Dictionary 复用静态槽比较；
Dictionary key 读取后按值重编码规范 key，碰撞与重复 Delta 寻址检查保持；不要求 primitive 输入最短。ReadPair 的视图专属 Transient 放在图外，独立读取允许各自初始化，续写使用 Checkout。
完整测试、独立审阅和真实包验收已通过，证据集中在分片记录。
下游已按公开入口接入真实模型，可继续玩法扩展并收集具体摩擦；
跨重开书签、局部事件浏览与类型适配扩展按路线图的需求触发，不自动扩展本轮范围。
自动跨库业务规则发现、ValueTuple、DateTime 和 SchemaStore 自举不随该轮扩张。

## 当前能力与实际边界

| 层 | 已验证能力 | 尚未闭合的边界 |
|---|---|---|
| [DurableGraph](DurableGraph/DurableGraph.csproj) | immutable Schema/exact DAG；统一 ObjectBinding、ObjectLayout、Capture/refs/恢复目录；SZ/rank 2–4 数组、List 与 Dictionary owned 状态；静态 StateEquals、数组稀疏/列表区间/字典键寻址 Delta、默认 Adaptive 与三种显式 List writer；独立 historical reader | 其他 BCL、数组协变；持久发布由 Persistence 拥有 |
| [Generator](DurableGraph.Generator/DurableGraph.Generator.csproj) / [Build](DurableGraph.Build/DurableGraph.Build.csproj) | class/struct（均含 record）开放模板、显式 enum、readonly DTO/静态 body、Capture/Hydrate、泛型继承与递归 Nullable/数组/List/Dictionary 组合；跨程序集 nominal/动态参数、固定 base/inline 与只读模板导出；history v9；三参 Upgrade/旧二参适配、值规则/局部依赖 adapter | 其他 CLR 值类型、其他 BCL；跨程序集业务规则发现 |
| [Persistence](DurableGraph.Persistence/DurableGraph.Persistence.csproj) | 默认三入口／meta 员工自举与 Dictionary 存活目录，共同 body／对象版本与 joint Save；准确 stored／selected／target views、完整引用／两阶段恢复；自由 E/S、根替换／history／tag／Fork／Pair／State 槽；真实 IDs/head/H 增量安装与 owned 物理缓存；统一 Shared Catalog／正常 baseline 升级与冷续写 | Private 删除及 P0R2 资格见[删除切片](../docs/design-branches/0096-history-and-head-model/private-removal.md)；Branch/Commit/WorkingCopy 已在隔离分支通过原回归；定向 origin/包闭包待完成，组提交和操作内 Catalog 候选尚待实施 |
| [Storage](DurableGraph.Storage/DurableGraph.Storage.csproj) | 共用 ObjectVersion records／payload 与 Base/Delta 精确计量、双数组只读地址字典、有界 owned frame/map LRU；图封套／batch／员工目录与发布 owner 位于 Persistence | 不保旧 raw Store／map／wire／chain／cache adapter；charge 不约束总堆／峰值 |
| [Serialization](DurableGraph.Serialization/DurableGraph.Serialization.csproj) | 直接 Bare 签名、少量 Guid/decimal/temporal 组合、拥有 raw bytes 的 PreparedBaseBody/PreparedDeltaBody | 完整数组/List 对象操作位于 Runtime；旧 typed slot／元素 ref 循环仅为测试 helper，见 DB-020；其他 BCL 内容 codec 尚无 |

容易混淆的限制：

- SG DTO/body 支持递归 inline struct 与 19 种标量：bool、byte/sbyte、short/ushort、int/uint、long/ulong、char、Half、float、double、Guid、decimal、TimeSpan、DateOnly、TimeOnly、DateTimeOffset；string、受支持 durable class、数组、List 和 Dictionary 引用槽保存非泛型 ObjectId；字节层仍编码 UInt32。
  SG 在声明所属编译中为顶层 partial class/record class 链或显式 partial struct/record struct（包括 readonly）生成代码，支持泛型；
  支持 readonly 持久字段和没有无参构造器的领域类。RuntimeHelpers 分配、SG Hydrate/声明层 UnsafeAccessor
  不执行实例构造器或字段初始化表达式；Transient 由用户交付后重建。
- Guid/decimal/TimeSpan 为内建值叶子，tag 19/20/21；DTO 直接存 CLR 值，无独立 Schema/对象行。
  Guid 固定 16-byte big-endian，decimal 公开四字固定 16-byte little-endian，TimeSpan 复用有符号 Ticks varint。
  decimal 的 scale/符号零保留；普通/Family/Runtime 共用静态完整表示比较。Dictionary 默认 ScalarDefault，
  数值相等但表示不同的真实 key 替换产生 Remove+Add。当前默认 meta catalog 使用员工描述与对象版本，完成帧保持；旧 SCB1 catalog 与独立发布权威退出，不提供存档兼容。
- DateOnly/TimeOnly/DateTimeOffset 为内建叶子，tag 22/23/24；公开 DayNumber、日内 Ticks、clock Ticks + 整分钟 offset
  写入使用最短 varint，读取接受 Bare 的有界合法冗余，不读取本地时区或 CLR 私有布局。DateTimeOffset 普通/Family/Runtime 均用 EqualsExact；
  Dictionary 默认仍按瞬间查找，真实换 offset key 为 Remove+Add，indexer 等价 key 不替换原 key。
  新 history v9，旧 v1–v8 保留原能力门槛/文件；DateTime 继续拒绝。详见 DB-058。
- enum 显式标记 DurableType，支持同编译顶层 public/internal 声明及八种整数底层类型，不要求 partial。
  使用有独立 nominal/版本的单整数 InlineValue Schema（合成 FieldId=1），复用 Family DTO/body；外置投影直接 cast。
  不新建 TypeTag、history/catalog 格式或对象行；unknown 数值/Flags bits 原样保留，常量名称、别名、赋值表与 Flags 不参与 history。
  底层类型变化须升版；业务数值重解释由显式版本/Upgrade 负责。Runtime 当前模板核对真实底层类型，历史不受当前宽度限制。
  可组合泛型/Nullable/数组/List，enum 版本依赖及显式 owner/值升级沿用 inline 规则；未标记/nested/file-local enum、常量误标持久属性拒绝。
- Nullable<T> 支持上述标量、Durable inline struct 与 enum，包括泛型组合。DTO 使用 NullableState<TState>，
  exact 槽持有规范 child；absent 不捕获/读取/比较/遍历内部值。Base 0/1、Delta Clear/Set/Patch 校验 prior，
  present 使用 child 静态操作。Nullable 不独立占用对象 ID；含 Nullable 的声明进入现有 Family 生成路径。
  构建 history 的 q(child) 保留固定 inline child 版本；T? 与 T 恰好闭合 Nullable 保留不同参数来源。
  显式规则集 AllowNullableLifting 默认关闭；开启后仅将已绑定 child 工具提升，空值/空集合仍预检完整依赖。
  显式 wrapper provider 优先，按 child inline 版本选择；错误不得回退。框架不自动补值或进行 T↔T? 业务转换。
- SchemaKind 区分 ReferenceObject/InlineValue，同 family 不得跨 kind；inline 字段持有完整 exact Schema，
  版本变化沿 inline/base 传播，nominal 不传播。struct 独立生成 Schema/history，但不登记对象或独立 Upgrade。
  readonly DTO 递归嵌套，引用投影为 ID；子 PrepareDelta 的 HasChanges/bytes 决定父位，置位但子无变化拒绝。
  共享值 DTO/body 按 exact key 生成，不依赖当前领域 struct CLR 宿主；owner Upgrade 通过强类型构造器显式转换，
  删除 struct 后仍可保留完整 owner 升级链。struct Hydrate 从 default 临时值经 ref accessor 填充，
  完成后赋回字段/元素槽，不运行构造器或初始化器，Transient 默认。仍无 ref struct/CLR nested type 支持。
- record struct 支持 positional、body 自动属性及 C# 14 field-backed storage，以 `[field: DurableField]` / `[field: Transient]` 显式分类；
  未分类 backing、无实际字段的误标、未知事件存储和生成 helper 碰撞拒绝。属性方法不参与保存恢复，计算属性不自动入 Schema。
  当前投影按 Roslyn MetadataName 发出强类型 UnsafeAccessor，历史不保存 backing 名或 record 标志；字段重命名/重排不改变持久布局。
  record 的业务 Equals 仍由 C# 或用户决定，可能包含 Transient；作为 Key 沿 DB-055 比较/恢复边界，不自动生成替代 comparer。
- class/record class 以 IDurableObject 统一资格，DurableBase 已移除；用户祖先链止于 object，每个领域祖先仍需 Schema/history。
  record class 复用实际 backing storage 与声明层 base projection；引用 accessor 用对象 receiver，inline 才用 ref。
  positional 复用基类属性不重复存储，支持泛型及跨程序集继承；业务 equality/with 不改变引用身份、DTO 比较或冷 Checkout 隔离。
  普通 class 仍不支持一般属性，marker 不授予接口持久槽、boxed 身份或任意外部基类。迁移与证据见 DB-068。
- `TypeExpr` 区分 builtin、named 定义及有序实参、声明内 parameter、SZ/rank 2–4 数组和内建 List/Nullable/Dictionary 构造；持久 key 为闭合 TypeExpr + 定义版本。
  SchemaId 只表示定义 ID，不能用来区分闭合族。base/inline 显式升版仍沿定义传播；Box<int> 也随 Box 定义升版。
  同库统一 Catalog 的声明索引内同 key 完整布局严格一致；同 key 异形开发试验使用独立完整库副本，Private 路线已移除。历史 view 保准确含义；两个独立库的 key 不授布局互换保证，不提供闭合历史账本。
  TypeExpr depth≤64、展开 nodes≤4096、arity≤32，exact 布局 DAG depth≤256。
- 含泛型/Nullable 当前定义或历史、当前 enum/record struct、显式 DurableUpgrade 或值规则/依赖属性的编译，
  以及有当前声明且保留无当前声明的 inline history 的编译，使用 `Generated.Family_<UTF8HexId>.Vn<TState...>`，
  `Generated.DurableDefinitions.Register` 显式登记生成定义；亦可单独登记 Family.Definition。
  历史 DTO/body 不携带领域泛型参数，phantom 参数不产生状态参数。已知叶子直接调用，未知槽使用 IStateOps/IValueProjection 静态约束调用。
  旧纯非泛型编译保留 Schema/GetSchema/__DurableState；迁入 Family 路径后，内部 DTO 类型引用需改用 Family alias。
- 目录冻结定义/provider，成功闭合在 snapshot 内缓存；current 按 CLR Type，historical 按完整 stored Schema 绑定。
  Open 冻结代码配置；每个准确 S 环境持其不可变 lookup／独立闭合 memo，hot checkout models 保持，新提交不原地换环境。
  同 T 多处 exact 不一致、错误 arity/kind、未知定义及不支持的 CLR 闭合拒绝；nominal 边不递归展开对象 body。
  UpgradePlan 持有展平去重的 exact Schema requirement set；缓存命中在 callback 前按该准确环境复核完整依赖，
  冲突报告 owner endpoint 到具体 base/inline 字段的稳定路径。普通 binding 缓存仍复用同一闭包算法；共享 DAG 不按树重复展开。
- 跨程序集 public 顶层 Durable 类型可进入 nominal 表达及动态表示参数；包括外部 class、容器元素、
  `LocalBox<RemotePoint>` / `RemoteBox<LocalPoint>` 及本地 `InlineBox<T>` 闭合外部值；也支持直接固定外部值字段/Nullable/泛型值。
  `DurableGraphGenerateDefinitions=true` 强制现有 Family 路径；未设置/false 保持自动选择。聚合器 `DurableDefinitions`
  为 internal，各库以公开 Register/ RegisterReaders facade 登记；history 独立，Runtime 不扫描程序集。
  nominal 查询优先 Definition，缺失时允许精确相同 nominal 的已登记普通 Model/reader，仅证明 ReferenceObject，
  不调用目标工厂或补历史模板；普通匹配与 Upgrade 反推共用，实际 exact reader 仍须存在。
  目标单独升版不改变 nominal-only owner；动态 inline 仍要求 owner 升版。程序集拆包不增加 CLR 二进制兼容保证。
- Family 编译以 DurableSchemaExportAttribute 导出自有当前/保留模板（inline 执行合同 1、class 合同 2，模板 v9），SG 按需读取编译引用元数据，
  包括传递 base/inline、internal 实现依赖、仅历史和仅本地值规则端点；固定外部依赖自动选 Family，复用公开状态能力。
  不重生成/重导出外部 Family；Build 通过带候选摘要关联的 canonical reference manifest 校验，只 Publish owned candidate。
  referenced 自闭包、accepted+referenced、最后 current 的顺序保留历史完整性；外部依赖不能借 owned 同 ID 补缺。
  独立库同 ID 即使模板相同也拒绝；导出材料不授予 Runtime factory/reader/Upgrade。固定外部值改变仍要求 owner 升版。
  普通无引用 candidate 与既有 Family DTO arity/Schema/body 保持；自动跨库业务规则发现仍未开放。见 DB-060/061。
- Family class 当前投影统一处理本声明字段，并经 StateBaseProjection 调用 immediate base 的已绑定 Capture/Hydrate，
  同库/跨库共享路径；支持 public 顶层 abstract/generic 外部基类，hidden internal 泛型/Nullable 字段留在定义库处理。
  base DTO 参数按原始参数代表字段映射到 leaf 前缀，state/ops 从 exact 槽取得，本地 projection 参数单独选择；
  完整 leaf DTO/body 仍展开，不分配基类对象、不增加 ID、不自动执行基类 Upgrade。
  该能力由 generated model 显式开启，手工 binding 默认关闭；冷绑定核对完整 Schema/DTO，整对象 exact CLR 检查保留。
  每层委托/DTO 复制的深链成本留待实测，不另建优化后端。历史变化仍要求受影响 leaf 显式升版和 Upgrade。
- 新 `DurableUpgrade` 方法使用非泛型 static host 中可访问的三参方法；运行时优先闭合 owner 特例，否则选择通用边。
  整条相邻链在该对象首次业务调用前绑定；中间 exact 布局来自显式 DTO 表示、已注册 Schema 或唯一历史推导，缺失则拒绝。
  不使用 latest 补缺，不自动升级 struct，失败不尝试另一业务规则。每对象/相邻边独立 UpgradeContext 含 ObjectId 及完整 Source/TargetObjectLayout；Schema 访问器仅适用于 durable owner。
  已有非泛型二参方法通过三参 adapter 调用，声明工具依赖则必须改为三参。Context 不含对象图读取或 ID 分配。
- `UpgradeDependency` 按 provider 局部 key 与两端声明 ID/FieldId 选择 exact 槽；`ValueUpgradeRuleSet` 显式选择业务规则，
  与定义一起进入同一冻结 snapshot。`GetValueUpgrade<A,B>(key)` 只返回该作用域的预绑定工具，未知 key/错 CLR 类型在调用时拒绝。
  全链声明依赖缺失/歧义/错完整槽在该对象首次 callback 前拒绝；显式候选失败不回退，KeepExact 必须启用且零候选、完整槽相等。
  子工具继承当前 owner 信息，按调用复用相同子计划以保留 DAG 共享，snapshot 不缓存带 ObjectId 的 Context/委托。
  SG `DurableValueUpgrade` 通过定义 ID/两端版本声明同一 inline family 的规则，可组合开放 Pair；仅支持同编译规则 marker。
  Runtime metadata 可明确指定 builtin/引用/闭合 nominal 模式及完整 expected 槽。保留 history 的值规则无需旧领域类型，
  也可在无当前 Durable 声明时生成登记材料；这不授予已删除引用对象族的 current Normalize 能力。
- AddRoot 登记根；BeginCapture(models) 冻结 exact CLR Type/model 目录，CaptureObject 逐边校验声明约束，
  先分配 ID/登记再排队；Seal 用增长队列捕获可达对象，子对象不加入根列表。未知实际派生类型明确拒绝。
  Accept/Discard 只是内存候选协议。ID 单调分配、失败可烧号；退役实例映射清理不回收数字。
  空串 Capture/读取两端统一 Empty，非空 string 保留引用身份。
  现有多根 Capture 是内部能力/机制见证；每份产品图仍选一个非空 durable 根。工作副本允许已登记的跨实际类型 State 根替换及异构 Event 根。
- CaptureSession.Prepare 自动使用 Current，完整预检 exact Schema/DTO/稳定 binding 后编码；全部 live Base 提前生成，
  existing class/array/List/Dictionary 调用融合 Delta、existing string 为 unchanged。结果只标识内存 Previous/Candidate，不带磁盘地址。
  重复准备与失败不安装或放弃候选、不烧号；临时 guard 拒绝会话重入。capture-only 登记仍有效，缺 binding 仅 Prepare 拒绝。
  跨 exact layout/DTO/binding 或数组 shape 不匹配拒绝，不自动降级 BaseOnly；Persistence 的 ObjectWritePlanner 分类／规划内核映射结果，真实 producer 仍负责准确内容来源。
- SG RegisterReaders 显式登记一个模型族的全部可用 Vn；StateReaderRegistry 同 binding 实例幂等，
  同 key 另一实例拒绝，读取开始复制固定索引。Schema 日志不包含可执行 reader，完全移除的模型族仍拒绝。
  Runtime typed 循环完成整链后才装箱，字段 body 保持静态绑定；无程序集扫描或一般 TypeCodec。
- 默认 ReadStoredGraph／typed reader 读取指定图全部 live 行，按原 Base 的准确 S／descriptor 与 selected view 匹配完整 ObjectLayout，由 exact reader
  VisitReferences 验证 string/class/array/List/Dictionary 引用；class nominal 约束按 stored Schema 祖先判断，数组与 List 要求 exact nominal 类型。
  晚期失败不返回部分结果，不要求全批 body 零调用。
  DecodedRevision 保留 stored-exact DTO、查询地址及每 ID 唯一 string 实例，关闭 Store 后仍可使用；
  无 roots/领域实例/Upgrade，不是 CaptureSession.Current，不能直接作为已加载的可编辑基线。
  旧 RevisionReadSession 与 typed DTO cache 已由第七片退出；每侧解码保准确执行环境，owned 物理缓存只复用完成帧／完整目录；
  每视图仍物化实际 head map 并完整验证引用/Dictionary lookup，不缓存 Normalize、业务 Upgrade 或工作区。
- StateModelRegistry 显式登记稳定 SG Model，Repository 在 Open 时固定 family、exact CLR Type 与 reader 索引；
  配置在 Open 固定；恢复／读取按实际 S 闭合环境，同 S 可严格复用、跨 S 独立 memo，checkout 在自身寿命内保持模型环境。
  同 exact CLR Type 的其他模型原子拒绝。durable exact-current Normalize 保留值/ID 并附加 current preparation，
  不调用历史转换委托；模型缓存复核所选准确视图的标准依赖。传统非泛型入口的可选普通静态 UpgradeStateVnToVnPlus1
  按相邻版本转换完整 leaf DTO，不重复升级祖先。已声明边逐一强类型检查；缺边仅阻止需要该边的 current Load。
  GraphReader 为独立读取与工作区恢复共用 decoded 输入管线：先完整 exact 解码、再升级全部 source 行，按 current Schema 重新校验全部引用；
  从所选 durable 根迭代求可达闭包，全部可达 class/array/List/Dictionary 实例分配并登记 string 后才 Hydrate。分配必须 exact、非空、彼此不同，Empty 例外。
  单图准备材料保留完整 normalized 来源、实际根、模型环境与可达顺序，不额外持有 decoded 目录。
  Repository Checkout/Fork 可驻留一份成功 State 材料及完整依赖证书；重复选择同 State 省去冷准备，mutable 分配与身份导入仍独立。
  预闭合 factory/binding/Upgrade 证据按实际依赖传递、命中复核该 State 准确环境的完整标准依赖；槽不替代逐请求 Head 导航或保存来源校验。
  独立与 Pair 共用 Allocate/Register、建表和 Hydrate 循环；只有 editable 交付才导入保存身份，纯只读不构造 CaptureSession。
  内部仅保留 current 冻结状态比较基线及 source ObjectLayout/完整 membership，升级仍 live 必须 Base。
  不可达 source 仍须解码/归一化/验证，但不要求其 current 类型可以 Allocate；历史 ancestry 不能用 current CLR 反推。
- RepositoryResources 公开为 concrete owner，拥有共享 data FrameStore 与借入它的 HeadStore，保管分支目录／shared／书签三个入口与唯一 pending；只读不写盘、不修尾。
  GraphReader.ReadPair 各自 Decode/Normalize/验证后，在同 ID/head、current model/完整布局、未重写且有完整状态相等 proof 的候选中，
  沿反向引用剔除依赖非候选者；剩余只读闭包共享 Allocate/Hydrate，稳定环可整体复用，changed-child 会使 owner 分裂。
  共享还要求同 reader-issued 环境；string 在既有 same-ID/head 候选内按引用快路／Ordinal 完整内容证明，不 intern 不同 ID 的等值非空串。只读结果不导入可编辑基线。
  两边完整成功才交付，无跨图实例身份承诺；缺 proof 保守不共享，真实错误传播，比较优化见路线图。
  共同 workspace／候选在 State 确认后才安装原实例／新根；独立 Event 使用最近 State 的 DTO/身份和 cursor，完成后 Discard，不清除 State rewrite。
  Event 完整目录按 source − candidate 产生 Removes，保实际 local Base/Delta 与内容来源 external heads；后继 State 仍相对最近 State。
- Repository 拥有 RepositoryResources 的 FS/HS 底座锁，不再另持 repository.lock；每分支最多一个活动 BranchCheckout，仓库操作串行。
  CreateSharedBranch(initialState) / CreateSharedBranchFromEvent(initialEvent) 先发布真实非空 S/E 再交付；不暴露空 head。
  CommitEvent/CommitState 自由排列；Event 不推进 State 基线或接受其 live 身份。
  业务 Parent 指向提交前精确 Head；业务内容来源取该 Parent 自身或祖先中最近 State，无 State 时为 null，旧称 Revision Parent。
  Checkout 保持精确 Head，只恢复最近 State 及完整来源；纯 Event 前缀交付 State=null、全新空 CaptureSession。
  Fork 在完整 guard 内占用新名字、恢复并构造工作副本后才发布 ref，不追加历史图；源副本可保持存活，未提交修改不参与。
  不同恢复的 mutable 图独立，完整身份与保存来源保持；普通恢复失败不留 ref，发布后失败依 outcome/fault 重开检查。
  ordinal 名称集合只管理编辑占用；显式 Dispose 释放，不强持副本或靠 GC 解锁，未交付只清理本次取得的占位。
  既有 State 损坏或缺模型能力会失败，不回退旧 State 或假装无 State。事件内容由应用显式 ReadEvent。
  State 根按实际模型选择，可跨类型替换，成功后安装原实例；已有 child 升根、旧 root 降为 child 保持 ID，不可达成员退出目录。
  冷 Event-only 历史不导入 Event ID；不同 Revision 的对象数字 ID 可以重合，身份须在具体 Revision 内解释。
  CheckpointAddress 由本次打开签发，相等性为 owner+图帧位置；跨 repo/重开旧地址拒绝，裸 RevisionAddress 只作诊断。
  CreateTag/ResolveTag 使用上游独立于 branch 的不可变名称空间；同名一律拒绝，重开按名字签发新句柄，不要求领域模型。
  tag 不追加图、不改工作副本/准备槽；发布异常即使 NotPublished 也可能 fault，重开 ResolveTag 检查实际结果。
  ReadEvent/ReadState 独立物化，ReadPair 按显式只读共享合同使用；ref-only CreateBranch 不占用编辑名，Move 只拒绝活动目标分支。
  ReadCheckpoint 一次独立恢复当前图与最近严格祖先的相反角色图，最多两图；稳定 getter，PreviousX 缺失时根/地址成对为空。
  EnumerateEvents 固定历史结尾，仅返回地址；下界首项前验证，每次 MoveNext 检查寿命，交付项时不占 guard/lease。
  Schema 只记录受控意图，真实 E/S 的 joint Save 才接受；先追加 meta／业务对象与目录／图，新增 shared 时用 VS 两 ref CU，共同 data-first 确认，发布后不重捕或运行模型回调。
  RepositoryPublicationException 区分 NotPublished / Unknown / Published；NotPublished 也须检查 IsFaulted，故障后 dispose/reopen，无透明重试或领域修改回滚。
  重开只读必要根与所选 Schema；准备所选图时检查两种 Parent、完整内容和图语义。不启动全库/orphan 图审计；只读不创建、flush 或修尾。
  恢复保证仍限正常关闭、进程中止和明确 I/O 故障；无 OS crash/power loss、目录元数据或完整后缀外部删除检测保证。
- 迁移壳可保留旧族 reader/Upgrade，退出 current World 可达闭包后不分配；仍完整验证 source 行。
  传统生成路径不为完全删除的族生成 reader；Family 路径可按 retained history 生成 state-only exact reader，
  但 editable Load 仍要求每个 source 引用对象族有 current/migration CLR 模型和 Normalize。只承诺读已 Remove 该族的新 Revision 才能删除其恢复能力，
  仍支持旧 Revision 则须保留相应 reader/Normalize。见 DB-036 H1 真实包回归。
- 旧 LoadedWorld／WorldWorkspace 保存壳退出，真实准备／恢复与连续保存分别由共同 GraphReader／BusinessWorkspace 和 BranchCheckout／BranchSaveSession 承担；不保替代旧保存适配器。
  Empty 反向映射选择最小 source ID，但基线槽保留旧 ID，首次 Capture 形成真实 Delta/Remove。
  分配从完整 source live max+1 起，只承诺会话内单调；uint 耗尽不阻止已有对象保存。
  恢复的可达 durable 实例身份导入同一捕获会话；child-only 修改不改变 owner ID 槽，
  断开最后根路径后整个循环岛由完整 source − candidate 得到 Remove，旧 Revision 不受影响。
- PrepareDeltaBody 每槽比较一次形成位图，再静态写变化值；结果含 HasChanges 和可复用 raw body，裸 Delta body 大小可直接取长度。
  List 匹配另用静态 StateEquals 试探，仅实际变化的配对调用子 PrepareDelta；浮点按位、decimal 按完整 GetBits、DateTimeOffset 按 EqualsExact、ObjectId 按值、inline 逐持久字段比较，忽略 padding。
  策略 D 还须计入对象 envelope，不能直接以裸 body 大小代替。
  PrepareBaseBody 对每版 DTO 复用 WriteBaseBody；全部 live Base 提前准备，决策后复用 bytes，性能优化留待 MVP 后。
  B/D 均为新对象记录的精确值，prior 固定12B；H 是实际读取记录的消费字节，合法冗余表示也按实际长度计入。
  ApplyDeltaBodyVn 只处理同 Vn；不证明 prior 身份，之后仍须对完整 DTO 验证引用。
- 默认 typed ABI5：有限员工／string、builtin-only 结构容器与 dynamic descriptor 路由分责；dynamic Base 引用其 batch origin S 的 meta ObjectId，selected S 核完整 retained 布局，不借 latest 补解释。
  Schema／TypeExpr 从指定 meta 根冷读员工 DTO 后经工厂投影，构造闭包／nominal kind/arity／limits 保持；目录排除自身、write-last，用固定 Dictionary body 与对象版本，不保 raw-map 第二权威。
  业务与 meta producer 共用窄 saver；实际 append 后准备全部安装材料，Confirmed 才接受 State／meta，Event 只接受 meta／Head。
  保存规划与计量保全部 source membership、实际 records／heads/H、强制 rewrite Base；物理缓存按 owner/address 复用合法 owned 值，每次仍核图关系／准确 S／typed 来源，zero 禁用、关闭／fault 清驻留。
- 默认产品只使用 meta 自举的共同保存/恢复路径；共用 body、payload/计量、planner、workspace与 owned LRU 保留，已退出的生产路径不重建兼容适配器。

- 数组对象支持 SZ/rank 2–4 与完整已有槽闭包：标量、string/class ID、inline/generic struct、递归数组。
  FrozenArrayState 拥有 shape 与元素 buffer；当前投影用静态 ref 循环，historical reader 不要求旧领域 struct CLR 类型。
  同布局稀疏 Delta 使用 row-major 索引与子 PrepareDelta；同实例保持 ID，替换/不可达继续遵循会话身份和 Remove 规则。
  数组独立 owner 显式选择元素 Upgrade 规则集，直接 source→current 端点预绑定，不自动搜索相邻规则路径；
  空数组也验证升级能力，共享数组只归一化一次，保持 ID/shape 并强制 Base。详细合同与证据见 DB-043。
  非零下界、非 SZ rank-1、rank > 4、数组协变与 unsupported 元素拒绝；object 内部入口不授予 object/interface 通配槽。
  已知成员的 SG body 静态绑定字节原语；PrimitiveSlotCodecs 只在测试工具中。
- exact BCL List<T> 复用完整槽闭包，可与数组、泛型 class/struct 递归组合；接口字段、List 子类及其他 BCL 容器仍拒绝。
  ListLayout 只含 exact 元素槽和 codec 版本，Count 属于 owned FrozenListState；Capacity 不持久化。
  同实例 resize 保持 ObjectId/表示 ID；Base=count+元素，codec 2 Delta=newCount+Copy/New/CopyAndPatch 区间，旧 codec 1 拒绝。
  Prepare/Apply 保持 prior/candidate 独立；source 可重复/倒序，只读 prior，区间内使用严格局部稀疏子 patch。
  UseListDeltaAlgorithm 选择 Position/LocalResync/BoundedMyers/Adaptive，默认 Adaptive；配置冻结到会话模型 snapshot，不进入布局/格式/表示 ID。
  三种纯 matcher 不编码且拒绝 Adaptive；Adaptive 在 writer 层保留完整 Local，并按停滞信号竞争独立有界 Myers，只接受严格更短 body。
  限长按实际输出在元素调用边界检查，不能视作峰值内存上限；整个 NoChange 判定独立于搜索预算。性能选择与保留边界见路线图 §3.2。
  UseListElementUpgrades 独立于数组规则选择，空 List 同样预绑定；每个共享 List 归一化一次，保留 ID/count 并强制 Base。
  UpgradeContext.ListCount 随子工具继承，不复用 ArrayShape；历史 reader 不依赖旧领域 struct CLR 类型。
- 实验性 exact BCL Dictionary<TKey,TValue> 采用两个完整槽，codec 1；TypeExpr/共享模式构造码 10，history `d(key,value)`，目录 kind=5。
  TValue 复用完整现有槽闭包；key 支持已有标量/enum、普通/generic Durable struct 和受支持引用类型。
  根 Nullable、未支持类型、子类/接口字段仍拒绝；复合 Key 的 Nullable/string/引用成分复用已有槽投影。
  comparer 是 frozen/Base body 的对象级标签 0–5，不进入 Layout/RepresentationId；0–3 标准选择保持，
  4 CurrentDefault 使用当前 CLR Default/IEquatable，5 Application 使用闭合 Dictionary 的当前应用选择。
  UseDictionaryComparer<K,V> 优先于唯一 UseDictionaryComparerResolver；只实际 Application Capture/Allocate 才解析，空实例亦然，
  缺失/错类型/异常不得回退。snapshot 复制配置、按闭合类型缓存并共享成功 comparer，不能冻结用户捕获的外部状态。
  4/5 用透明转发包装保留 Load→Capture→Commit 的模式；不保证 Comparer 实例身份，同型 Application 只恢复一种当前规则。
  owned 双槽条目语义无序；容量/枚举重排无伪变化，hash/bucket 不持久化。key 和 value 引用均参与共享、循环及可达性。
  Delta 以 canonical key Base bytes 寻址 Remove/PatchValue/Add；键表示不变时每 value 只调用一次融合 PrepareDelta。
  不用领域 comparer 配对、不依赖 entry ordinal；相同查找键但不同持久 ID/bits 使用 Remove+Add，prior 不被修改。
  metadata Validate 不编码 key；临时 key bytes/hash索引只在操作内使用，完整 bytes 决定相等，性能复用留待实测。
  所有模式查语法、根 null、canonical 重复和完整引用，包括不可达行；零宽完整 key 的多条计数在分配前拒绝。
  0–3 保留框架标准 lookup 校验，包括 string 内容与 Empty 身份冲突；4/5 的 exact/Normalize 不执行当前业务比较，
  只在可达图的 TryAdd 拒绝当前碰撞。历史可读不等于当前规则可接纳，失败不交付部分 World。
  key inline/string 和引用身份在填充时可用，引用目标内容可能未就绪；比较不可依赖其内容或未恢复的 Transient。
  UseDictionaryKeyUpgrades/UseDictionaryValueUpgrades 分别选择显式规则；完整双端依赖和全部工具在首次 callback 前预检，空容器亦如此。
  每个共享字典仅 Normalize 一次，保持 ID/count/comparer；升级碰撞拒绝，仍 live 的布局升级强制 Base。
  UpgradeContext.DictionaryCount 只用于 Dictionary owner，子工具继承；历史 enum/struct key/value DTO 不依赖旧领域 CLR。

产品依赖为 Persistence → Runtime + Storage，二者分别复用 Serialization；Storage 另用 RBF substrate。
DurableGraph runtime 也引用 Serialization，单一 runtime PackageReference 可取得传递依赖。

## 按任务定位证据

| 准备修改 | 先查源码/测试，再按需读合同 |
|---|---|
| 热保存基线、head/H 与增量安装 | [DB-069](../docs/design-branches/0069-incremental-save-baseline/README.md)、[NormalizedGraphState](DurableGraph.Persistence/NormalizedGraphState.cs)、[基线回归](../tests/DurableGraph.Persistence.Tests/P0NormalizedBusinessBaselineTests.cs)、[实际计量回归](../tests/DurableGraph.Persistence.Tests/BusinessPlanningAccountingTests.cs) |
| IDurableObject、record class 与旧基类迁移 | [DB-068](../docs/design-branches/0068-record-class-model-slice/README.md)、[准入](DurableGraph/IDurableObject.cs)、[record 图回归](../tests/DurableGraph.Tests/RecordClassGraphTests.cs)、[record 模型资产](../experiments/PackageConsumerProbe/RecordClassConsumer/README.md) |
| owned frame/map 读缓存、双数组字典与 Store 寿命 | [DB-067](../docs/design-branches/0067-owned-revision-read-cache-design/README.md)、[当前缓存](DurableGraph.Persistence/GraphPhysicalReadCache.cs)、[预算与失败测试](../tests/DurableGraph.Persistence.Tests/P0OwnedReadCacheTests.cs) |
| 默认保存、事件快照和失败恢复用法 | [DB-065](../docs/design-branches/0065-event-history-consumer-contract-slice/README.md)、[包示例](../experiments/PackageConsumerProbe/EventHistoryRecoveryConsumer/README.md)、[同源恢复测试](../tests/DurableGraph.Persistence.Tests/EventHistoryConsumerRecoveryTests.cs)、[README 原文验证](../experiments/PackageConsumerProbe/Run-ReadmeQuickStartProbe.ps1) |
| 跨程序集继承、hidden 字段与基类状态投影 | [DB-061](../docs/design-branches/0061-cross-assembly-inheritance-slice/README.md)、[Runtime 投影](DurableGraph/Runtime/Binding/StateBaseProjection.cs)、[SG 当前投影](DurableGraph.Generator/DurableGraphGenerator.GenericProjection.cs)、[模型资产](../experiments/PackageConsumerProbe/InheritanceLibraryConsumer/README.md) |
| 固定外部 inline、只读模板归属与构建闭包 | [DB-060](../docs/design-branches/0060-cross-assembly-inline-history-slice/README.md)、[SG 导入导出](DurableGraph.Generator/DurableGraphGenerator.SchemaExports.cs)、[Build 依赖](DurableGraph.Build/SchemaHistoryTool.References.cs)、[模型资产](../experiments/PackageConsumerProbe/InlineLibraryConsumer/README.md) |
| 跨程序集 nominal/动态参数、显式 Family 与稳定消费者 DLL | [DB-059](../docs/design-branches/0059-cross-assembly-model-composition-slice/README.md)、[metadata 分类](DurableGraph.Generator/DurableGraphGenerator.CrossAssembly.cs)、[模型资产](../experiments/PackageConsumerProbe/CrossAssemblyConsumer/README.md) |
| DateOnly/TimeOnly/DateTimeOffset、完整 offset 与 history v9 | [DB-058](../docs/design-branches/0058-temporal-scalar-value-slice/README.md)、[静态值操作](DurableGraph/Runtime/State/TemporalScalarStateValues.cs)、[模型资产](../experiments/PackageConsumerProbe/TemporalScalarConsumer/README.md) |
| Guid/decimal/TimeSpan、非连续 builtin tag 与 history v8 | [DB-057](../docs/design-branches/0057-bcl-scalar-value-slice/README.md)、[静态值操作](DurableGraph/Runtime/State/BclScalarStateValues.cs)、[模型资产](../experiments/PackageConsumerProbe/BclScalarConsumer/README.md) |
| record struct、backing storage 分类与字段投影 | [DB-056](../docs/design-branches/0056-record-struct-state-slice/README.md)、[字段分类](DurableGraph.Generator/DurableGraphGenerator.Records.cs)、[投影](DurableGraph.Generator/DurableGraphGenerator.GenericProjection.cs)、[模型资产](../experiments/PackageConsumerProbe/RecordConsumer/README.md) |
| Dictionary 内容、复合 Key、当前 comparer 与历史双槽升级 | [DB-055](../docs/design-branches/0055-composite-dictionary-key-design/README.md)、[body](DurableGraph/Runtime/Containers/DictionaryStateReader.cs)、[current binding](DurableGraph/Runtime/Containers/DictionaryObjectBinding.cs)、[升级](DurableGraph/Runtime/Binding/StateBindingContext.DictionaryUpgrade.cs)、[模型资产](../experiments/PackageConsumerProbe/CompositeDictionaryConsumer/README.md) |
| enum 表示、外置投影与历史升级 | [DB-053](../docs/design-branches/0053-enum-inline-state-slice/README.md)、[生成接缝](DurableGraph.Generator/DurableGraphGenerator.Enums.cs)、[当前模板校验](DurableGraph/Runtime/Binding/StateDefinitionBinding.cs)、[模型资产](../experiments/PackageConsumerProbe/EnumConsumer/README.md) |
| Nullable 值槽、history 与显式升级提升 | [DB-052](../docs/design-branches/0052-nullable-value-slot-slice/README.md)、[静态值操作](DurableGraph/Runtime/State/NullableStateValues.cs)、[完整 child 布局](DurableGraph/Schema/NullableValueLayout.cs)、[模型资产](../experiments/PackageConsumerProbe/NullableConsumer/README.md) |
| List 区间 Delta、匹配与配置 | [DB-049](../docs/design-branches/0049-list-range-delta-and-matcher-trial-slice/README.md)、[matcher](DurableGraph/Runtime/Containers/ListDeltaMatcher.cs)、[reader/body](DurableGraph/Runtime/Containers/ListStateReader.cs)、[重放实验](../experiments/ListDeltaReplayProbe/README.md) |
| List 内容、冻结与历史元素 Upgrade | [DB-047](../docs/design-branches/0047-list-content-object-slice/README.md)、[当前投影](DurableGraph/Runtime/Containers/ListObjectBinding.cs)、[List 升级](DurableGraph/Runtime/Binding/StateBindingContext.ListUpgrade.cs)、[模型资产](../experiments/PackageConsumerProbe/ListConsumer/README.md) |
| 闭合目录、descriptor 与 Base 来源 | [当前 catalog view](DurableGraph.Persistence/SchemaCatalogView.cs)、[typed 头](DurableGraph.Persistence/StoredGraphBodies.cs)、[目录闭包反例](../tests/DurableGraph.Persistence.Tests/MetaBootstrapSchemaClosureTests.cs)、[表示集成测试](../tests/DurableGraph.Persistence.Tests/RepresentationIntegrationTests.cs) |
| 数组对象、共同引用入口与元素 Upgrade | [DB-043](../docs/design-branches/0043-vector-array-object-slice/README.md)、[对象布局](DurableGraph/Schema/ObjectLayout.cs)、[数组绑定](DurableGraph/Runtime/Containers/ArrayObjectBinding.cs)、[历史 reader](DurableGraph/Runtime/Containers/ArrayStateReader.cs)、[数组 owner Upgrade](DurableGraph/Runtime/Binding/StateBindingContext.ArrayUpgrade.cs) |
| 引用槽与对象身份包装 | [DB-041](../docs/design-branches/0041-object-id-state-representation/README.md)、[ObjectId](DurableGraph/ObjectId.cs)、[静态引用操作](DurableGraph/Runtime/State/BuiltinStateValues.cs) |
| 可组合值 Upgrade / 规则集 / Context 子作用域 | [DB-039](../docs/design-branches/0039-composable-value-upgrade-design/README.md)、[值绑定](DurableGraph/Runtime/Binding/StateBindingContext.ValueUpgrade.cs)、[SG 属性](DurableGraph.Generator/DurableGraphGenerator.ValueUpgrades.cs)、[模型资产](../experiments/PackageConsumerProbe/ValueUpgradeConsumer/README.md) |
| 泛型/历史绑定/UpgradeContext | [DB-038](../docs/design-branches/0038-generic-schema-state-and-binding-design/README.md)、[绑定上下文](DurableGraph/Runtime/Binding/StateBindingContext.cs)、[生成模板](DurableGraph.Generator/DurableGraphGenerator.GenericState.cs)、[泛型模型资产](../experiments/PackageConsumerProbe/GenericConsumer/README.md) |
| inline struct/嵌套 DTO/Schema DAG | [DB-037](../docs/design-branches/0037-inline-struct-state-slice/README.md)、[生成值 helper](DurableGraph.Generator/DurableGraphGenerator.InlineState.cs)、[真实生成图](../tests/DurableGraph.Tests/InlineStructGraphTests.cs)、[inline 模型资产](../experiments/PackageConsumerProbe/InlineStructConsumer/README.md) |
| 独立图工作区、资源与读取 | [DB-062](../docs/design-branches/0062-independent-graph-workspace-slice/README.md)、[工作区](DurableGraph.Persistence/BusinessWorkspace.cs)、[资源](DurableGraph.Persistence/RepositoryResources.cs)、[读取](DurableGraph.Persistence/GraphReader.cs) |
| EventHistory、分支发布与恢复 | [DB-063](../docs/design-branches/0063-event-history-journal-slice/README.md)、[Repository](DurableGraph.Persistence/Repository.cs)、[工作副本](DurableGraph.Persistence/WorkingCopy.cs)、[集成测试](../tests/DurableGraph.Persistence.Tests/EventHistoryRepositoryTests.cs) |
| 操作内 exact 解码复用与只读闭包共享 | [DB-064](../docs/design-branches/0064-shared-revision-decoding-design/README.md)、[恢复上下文](DurableGraph.Persistence/GraphReadContext.cs)、[引用闭包](DurableGraph.Persistence/GraphReader.cs)、[共享测试](../tests/DurableGraph.Persistence.Tests/SharedGraphReaderTests.cs)、[Checkout 与续写](../tests/DurableGraph.Persistence.Tests/SharedEventHistoryTests.cs) |
| 领域引用图/首次准备 | [DB-034](../docs/design-branches/0034-durable-reference-graph-batch/README.md)、[真实生成图冷读](../tests/DurableGraph.Tests/PersistedReferenceGraphTests.cs)、[双视图与失败测试](../tests/DurableGraph.Persistence.Tests/LoadedReferenceWorldTests.cs) |
| Schema、DTO、静态 body | [Generator tests](../tests/DurableGraph.Tests)、[DB-019](../docs/design-branches/0019-schema-ancestry-implementation-slice/README.md)、[DB-022](../docs/design-branches/0022-versioned-state-dto-capture/README.md)、[DB-023](../docs/design-branches/0023-scalar-schema-dto-slice/README.md) |
| 同版 DTO Delta 准备与应用 | [DB-027](../docs/design-branches/0027-generated-same-schema-delta-body-slice/README.md)、[body tests](../tests/DurableGraph.Tests/FusedDeltaBodyTests.cs)、[history/Capture tests](../tests/DurableGraph.Tests/FusedDeltaHistoryTests.cs) |
| Capture 与 string 读取 | [DB-024](../docs/design-branches/0024-reference-capture-and-reusable-object-ids/README.md)、[DB-025](../docs/design-branches/0025-string-object-decoding-slice/0025-string-object-decoding-slice.md) |
| 持久 Schema、Base 引用及 typed 冷读 | [catalog 工作区](DurableGraph.Persistence/SchemaCatalogWorkspace.cs)、[真实 Schema 闭包 tests](../tests/DurableGraph.Persistence.Tests/MetaBootstrapSchemaClosureTests.cs)、[默认 typed reader](DurableGraph.Persistence/StoredGraphBodies.cs)、[旧权威退出（归档）（Git 历史）](../docs/design-branches/0091-retired-material-cleanup/README.md#git-history "Git 6dbaf0e:docs/archive/2026-10-13/0087-meta-bootstrap-refactoring-route.md#37-p1d-第八片旧schema权威与owner模式退出") |
| current 升级、readonly 恢复与续写 | [DB-033](../docs/design-branches/0033-upgrade-restore-resave-batch/README.md)、[SG tests](../tests/DurableGraph.Tests/GeneratedStateModelTests.cs)、[加载 tests](../tests/DurableGraph.Persistence.Tests/LoadedWorldTests.cs)、[冷重开集成](../tests/DurableGraph.Tests/LoadedWorldGeneratorTests.cs) |
| 完整历史 DTO 目录与 reader 登记 | [DB-032](../docs/design-branches/0032-exact-revision-decoding-slice/README.md)、[ReadStoredGraph](DurableGraph.Persistence/DecodedRevision.cs)、[生成冷读 tests](../tests/DurableGraph.Tests/DecodedRevisionGeneratorTests.cs) |
| 异构图统一准备内容 | [DB-030](../docs/design-branches/0030-captured-object-preparation-slice/README.md)、[runtime tests](../tests/DurableGraph.Tests/CapturedGraphPreparationTests.cs)、[生成 tests](../tests/DurableGraph.Tests/GeneratedCapturePreparationTests.cs) |
| 对象内容、地址与重开读取 | [Storage tests](../tests/DurableGraph.Storage.Tests)、[typed 文件见证](../tests/DurableGraph.Tests/RawBaseStorageGeneratorTests.cs) |
| 持久 Delta、prior 链与 H | [准确来源／链反例](../tests/DurableGraph.Persistence.Tests/MetaBootstrapP0IntegrityTests.cs)、[真实 SG 冷重开](../tests/DurableGraph.Tests/PersistedDeltaChainGeneratorTests.cs) |
| 已准备内容、Base/Delta 策略与 Revision | [DB-029](../docs/design-branches/0029-prepared-object-revision-planning-slice/README.md)、[规划器](DurableGraph.Persistence/ObjectWritePlanner.cs)、[策略实现](DurableGraph.Persistence/ReadAmplificationBaseBudgetPolicy.cs)、[策略 tests](../tests/DurableGraph.Persistence.Tests)、[DB-015](../docs/design-branches/0015-statestore-object-representation-policy/README.md) |
| 包、生成器消费和 history 发布 | [PackageConsumerProbe](../experiments/PackageConsumerProbe/README.md)；真实 PackageReference 验证不能由 ProjectReference 测试替代 |

代码变更后运行根 solution build 和相关 tests；包交付边界变化时按 PackageConsumer README 验证。
本文件不累积测试计数、命令日志或完成流水账；已有验证结果保留在相应施工记录。
