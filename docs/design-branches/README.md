# DurableGraph 设计问题与证据导航

查当前产品从 [PROJECT-STATE](../../src/PROJECT-STATE.md) 开始；长期选择见[目标设计](../DurableGraph-target-design-v0.md)，
未完成事项与重访条件见[路线图](../DurableGraph-research-roadmap.md)。这里按问题找到当片合同、论证与验收，不另维护能力或施工队列。

每个 DB 有一个稳定问题目录及 README 入口。简单单文直接作为 README；多材料在入口给阅读顺序。
文件名、目录位置和历史 Chosen 标签不能证明今日实现；否定实验、候选默认关闭和部分后继以入口限定范围解释。
退出材料的来源统一见 [Git 历史](0091-retired-material-cleanup/README.md#git-history)。已退出 DB 的编号不复用。

## 按问题找材料

不知道编号时先在下面搜索主题；确定问题后，只在其目录内查所需证据：

```powershell
rg -n 'FrameStore|Schema|Fork' docs/design-branches/README.md
rg --files docs/design-branches/0086-new-storage-refactoring-plan
rg -n '恢复|验收' docs/design-branches/0086-new-storage-refactoring-plan
```

下表只提供主题、阅读入口及必要的历史提示。行的先后不表示排期，状态细节与测试数字在原记录查。

| DB | 问题入口与检索词 |
|---|---|
| 092 | [类型名、命名空间与后续组织重构](0092-type-namespace-refactoring/README.md)；A／B／C／D1／D2 已限定验收，公开调用方直接迁移重编译；下一片可从 E1 文件名对应开始，程序集组织另判 |
| 091 | [淘汰材料删除、继任与验证](0091-retired-material-cleanup/README.md) |
| 090 | [文档治理、问题目录与随任务维护](0090-document-governance/README.md) |
| 089 | [checkout 环境寿命与 factory 查询](0089-checkout-environment-and-factory-query-refactoring/README.md) |
| 088 | [开发 Schema 草稿 fork 与定稿](0088-development-schema-workflow/README.md) |
| 087 | [meta 自举与共享私有 Schema 视图](0087-meta-bootstrap-refactoring-route/README.md)；P2 原文另有 Git 来源 |
| 086 | [正常 backing 恢复与资源寿命](0086-new-storage-refactoring-plan/README.md) |
| 084 | [不可变 tag 与跨重开定位](0084-eventjournal-immutable-tags-slice/README.md) |
| 083 | [Repository 与 Checkpoint 用户故事](0083-checkpoint-api-user-stories/README.md) |
| 082 | [单图准备材料 ImmutableLeaf 复用](0082-prepared-immutable-leaf-reuse-slice/README.md)；候选已实现，默认关闭 |
| 081 | [热 State 提交恢复资格实验](0081-hot-commit-restoration-material-slice/README.md)；否定实验，热准入未接线 |
| 080 | [最近 State 准备材料复用](0080-prepared-checkpoint-reuse-slice/README.md) |
| 079 | [共用图恢复准备与物化核心](0079-shared-graph-restoration-core-slice/README.md) |
| 078 | [分支工作副本与 Checkpoint Fork](0078-editable-checkpoint-fork-slice/README.md) |
| 077 | [Repository 固定模型环境](0077-repository-model-environment-slice/README.md) |
| 076 | [高效领域图 fork 技术路径](0076-efficient-graph-fork-technical-path/README.md) |
| 075 | [ImmutableLeaf 完整证明与 Family](0075-immutable-leaf-proof-and-family-refactor/README.md) |
| 074 | [高效 fork 暂缓方向](0074-efficient-fork-deferred-directions/README.md)；Draft，非排期 |
| 073 | [Repository WeakReference 叶实例缓存](0073-repository-scoped-weak-reference-cache/README.md)；Draft，未实施 |
| 072 | [Generator ImmutableLeaf 分类](0072-generator-immutable-leaf-classification-slice/README.md) |
| 071 | [程序集、命名空间与下游适配](0071-assembly-namespace/README.md) |
| 070 | [读取放大默认阈值](0070-read-amplification-default/README.md) |
| 069 | [热保存基线与增量 head/H](0069-incremental-save-baseline/README.md) |
| 068 | [record class 与统一引用资格](0068-record-class-model-slice/README.md) |
| 067 | [append-only Store owned 读缓存](0067-owned-revision-read-cache-design/README.md) |
| 066 | [ReadPair 比较与 Transient 合同](0066-readpair-comparison-and-transient-contract-slice/README.md) |
| 065 | [消费者上手与恢复合同](0065-event-history-consumer-contract-slice/README.md) |
| 064 | [双 Revision 解码与只读安全共享](0064-shared-revision-decoding-design/README.md) |
| 063 | [EventJournal 与 EventHistory 外观](0063-event-history-journal-slice/README.md) |
| 062 | [独立图读取与外层发布工作区](0062-independent-graph-workspace-slice/README.md) |
| 061 | [跨程序集继承与基类状态投影](0061-cross-assembly-inheritance-slice/README.md) |
| 060 | [跨程序集固定 inline 值与只读 history 模板导入](0060-cross-assembly-inline-history-slice/README.md) |
| 059 | [跨程序集 nominal 模型目录与类型组合](0059-cross-assembly-model-composition-slice/README.md) |
| 058 | [DateOnly、TimeOnly、DateTimeOffset 内建值槽](0058-temporal-scalar-value-slice/README.md) |
| 057 | [Guid、decimal、TimeSpan 内建标量值槽](0057-bcl-scalar-value-slice/README.md) |
| 056 | [record struct positional/generic 持久值与复合 Key](0056-record-struct-state-slice/README.md) |
| 055 | [复合 Dictionary Key 持久状态与当前领域 comparer](0055-composite-dictionary-key-design/README.md) |
| 054 | [Dictionary 内容对象、comparer 与键寻址 Delta](0054-dictionary-content-object-slice/README.md) |
| 053 | [显式 enum nominal 身份与内联整数状态](0053-enum-inline-state-slice/README.md) |
| 052 | [可组合 Nullable 值槽、历史依赖与显式 Upgrade](0052-nullable-value-slot-slice/README.md) |
| 051 | [完整 Local 基准、限长竞争与默认 Adaptive List Delta](0051-bounded-list-delta-competition/README.md) |
| 050 | [有界 List matcher 回退、共享预算与反例研究](0050-list-fallback-research/README.md) |
| 049 | [List 区间 Delta、StateEquals 与 matcher 对照](0049-list-range-delta-and-matcher-trial-slice/README.md) |
| 048 | [List 差分算法、区间复用与编辑原语调研](0048-list-delta-algorithm-research/README.md) |
| 047 | [List&lt;T&gt; 内容对象、共享身份与历史 Upgrade](0047-list-content-object-slice/README.md) |
| 045 | [持久 RepresentationId 与 Base ID 类型头](0045-persisted-representation-id-slice/README.md) |
| 044 | [类型头与 Schema 寻址的匿名方案盲评](0044-type-header-blind-review/README.md)；盲评历史；已选后继 DB-045 |
| 043 | [可组合数组、rank 与统一引用对象路径](0043-vector-array-object-slice/README.md) |
| 042 | [UpgradePlan exact Schema 依赖证书与晚登记冲突](0042-upgrade-schema-requirement-set/README.md) |
| 041 | [非泛型 ObjectId 的 DTO 数值隔离](0041-object-id-state-representation/README.md) |
| 040 | [typed ObjectId DTO 品牌与 CLR 加载边界研究](0040-typed-object-id-representation-research/README.md) |
| 039 | [owner 显式调用的可组合值 UpgradeContext](0039-composable-value-upgrade-design/README.md) |
| 038 | [泛型 Schema、Family DTO 与运行时闭合绑定](0038-generic-schema-state-and-binding-design/README.md) |
| 037 | [inline struct exact Schema、嵌套 DTO 与融合 Delta](0037-inline-struct-state-slice/README.md) |
| 036 | [同实例 GraphSession Commit 与历史恢复能力](0036-working-session-and-history-capabilities/README.md) |
| 035 | [唯一 State model、Schema history 与公开术语迁移](0035-public-contract-terminology-migration/README.md) |
| 034 | [领域引用图 Capture、循环 Restore 与首次保存](0034-durable-reference-graph-batch/README.md) |
| 033 | [DTO Upgrade、领域 Restore 与增量续写闭环](0033-upgrade-restore-resave-batch/README.md) |
| 032 | [完整 exact-version 对象目录冷读与引用验证](0032-exact-revision-decoding-slice/README.md) |
| 030 | [异构 Capture 图统一准备对象内容](0030-captured-object-preparation-slice/README.md) |
| 029 | [prepared 内容到可追加 Revision](0029-prepared-object-revision-planning-slice/README.md) |
| 027 | [同 exact Schema DTO 比较与字段 Delta body](0027-generated-same-schema-delta-body-slice/README.md) |
| 025 | [string 解码、引用槽校验与 Empty 规范化](0025-string-object-decoding-slice/README.md) |
| 024 | [引用 Capture、revision 身份与 ObjectId 复用](0024-reference-capture-and-reusable-object-ids/README.md) |
| 023 | [13 种标量 Schema/history/DTO 贯通](0023-scalar-schema-dto-slice/README.md) |
| 022 | [Versioned DTO、Capture 与 binary body](0022-versioned-state-dto-capture/README.md) |
| 020 | [typed 值槽位与数组元素 binding](0020-typed-slot-array-binding-slice/README.md) |
| 019 | [祖先 Schema/history 与 exact 依赖](0019-schema-ancestry-implementation-slice/README.md) |
| 018 | [统一引用身份、TypeCodec 与 Serializer 形状](0018-generated-graph-codec-shape/README.md)；混合历史草稿；见 DTO／泛型后继 |
| 017 | [对象 codec 设计要点与 Robird 证据](0017-object-codec-design-points/README.md) |
| 015 | [对象表示估算 DTO 与 Base/Delta plan](0015-statestore-object-representation-policy/README.md) |
| 005 | [Durable 继承与 FieldId 分段](0005-durable-inheritance-flattening/README.md) |
| 004 | [Snapshot History 创作与发布](0004-snapshot-history-authoring-and-publishing/README.md) |
| 003 | [Snapshot 值形状与 Upgrade 签名](0003-snapshot-value-shape-and-upgrade-signature/README.md) |
| 002 | [读取阶段版本升级管线](0002-read-time-version-upgrade-pipeline/README.md) |
| 001 | [Schema authority 与运行时表示](0001-schema-authority-and-runtime-representation/README.md) |

## 新增与收尾

执行[局部维护规则](AGENTS.md)：开题或重开时查已有问题，改结论时限定旧依据的适用性，
完成时保一次最终证据并移交对应知识 home，移动或压缩时核入链、出链与实际 anchor 去向。
治理依据与本次验收见 [DB-090](0090-document-governance/README.md)。不要把历史正文的“下一步”直接当新工单。
