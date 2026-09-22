# DurableGraph 设计决策与历史证据索引

本目录保存设计讨论、已实施工作单与技术储备。**不按编号顺序通读，也不把旧文档中的“当前”当作今日事实。**
产品续接从 [PROJECT-STATE](../../src/PROJECT-STATE.md) 开始；长期方向见[目标设计](../DurableGraph-target-design-v0.md)，
未完成事项及重访触发条件统一在[路线图](../DurableGraph-research-roadmap.md)维护。下面仅按任务定位依据，不另维护一份 backlog。

截至 2026-09-06，DB-001–026 保留原路径作为历史材料。混合文档中的有效决策与未完成问题已提取到上述活跃文档，
正文不再随每片进度回填；需要重开问题时，在路线图登记并按需写新的分片，链接旧依据。
这是阅读与维护职责的归档，不等于将未决方案否决或将建议批准。

## 如何读状态

- **Open / Deferred**：原讨论尚未全部裁决或等待触发；不意味着其中没有已采用、已实现的部分。
- **Chosen**：在文档所述范围内选择了方案；不自动代表完整产品已实现、长期 API 已冻结或所有附带建议已批准。
- **Proposed**：推荐的下一分片，等待采纳；不是已选持久格式或实施授权。
- **Rejected / Superseded**：保留否决原因或后继链接，不继续作为施工输入。
- 本索引的“实施/适用范围”与原状态分开。源码和可执行证据决定实现事实，历史测试数字只属于当时基线。

## 当前规划

DB-034–039 已实现；DB-041 已实现非泛型 ObjectId，DB-042 已收敛 Upgrade Schema 复核，DB-040 的泛型目标品牌暂缓。
DB-045 持久表示 ID、DB-046 统一闭合目录、DB-047 List 基础能力和 DB-049 区间差分均已完成；DB-048 保留调研依据：

| 文档 | 状态 | 实施范围 |
|---|---|---|
| [DB-075 ImmutableLeaf 完整证明与 Family 重构](0075-immutable-leaf-proof-and-family-refactor.md) | Proposed / 已辩证评审 | 未实施；共同候选规则与生成的最终结构核对，修复其他生成器误判及 Family 非泛型覆盖；缓存片的前置修复 |
| [DB-074 高效 fork 暂缓方向](0074-efficient-fork-deferred-directions.md) | Deferred | 汇总 DeepImmutable、泛型、string、Transient、deep clone、公开 fork API、opened repo session、性能与内存管理的后续触发条件 |
| [DB-073 Repository 作用域的 ImmutableLeaf WeakReference 缓存](0073-repository-scoped-weak-reference-cache.md) | Chosen / Not implemented | 以 opened repository 为 scope，用 `(ObjectId, head, exact binding)` 缓存 ImmutableLeaf hydrated 实例；跨 repo 拒绝，Dispose 清空 |
| [DB-072 Generator 侧 ImmutableLeaf 分类与生成侧见证](0072-generator-immutable-leaf-classification-slice.md) | Implemented / 待修复 | 已有公共 bool 通道与二进制路径分类；审阅发现其他生成器新增状态误判，修复方案见 DB-075；Family 仍 false，无运行时复用机制 |
| [DB-071 程序集与命名空间组织](0071-assembly-namespace-organization-review.md) | Implemented | 保留七项目依赖图，落实 Serialization/Storage/Persistence 与根/Schema/Runtime 分组；[工单](0071-assembly-namespace-implementation-work-order.md) 的签名、产品测试、真实包、旧包续写及隔离下游证据见[验收记录](0071-assembly-namespace-validation.md) |
| [DB-070 读取放大阈值的默认选择](0070-read-amplification-default.md) | Implemented / 已验收 | EventHistory 默认 `{5,5}`；稳态摊销依据、`3/5/11` 调优示例与交互函数图；测试基准独立保持 `{3,5}`，源码回归与 README 原文真包验收通过 |
| [DB-069 热保存基线的增量重建计量](0069-incremental-save-baseline.md) | Implemented / 已验收 | 已验证 head/H 随 DTO 基线增量推进，消除受控保存的旧对象链重复读取；精确 payload、发布与冷读验证保持；独立审阅、前后测量、源码回归及真实包通过 |
| [DB-068 record class 领域模型与统一引用资格](0068-record-class-model-slice.md) | Implemented / G0–G3 | 移除 DurableBase、统一 marker、record backing/继承及完整历史；真包三代、真实旧包迁移和源码回归；不扩张任意外部基类、深不可变或格式 |
| [DB-067 append-only Store 的修订读缓存](0067-owned-revision-read-cache-design.md) | Implemented / 已验收 | 普通 Store 统一有界 frame/map LRU、双数组二分字典与 local-record 二分；自身 Dispose 与直接消费者迁移；预算/失败/离线救援、新表示测量、产品回归与真实包通过 |
| [DB-066 ReadPair 比较能力与 Transient 使用合同](0066-readpair-comparison-and-transient-contract-slice.md) | Implemented / G0–G3 已验收 | 可选完整比较替换共享判断的 Base 编码、缺 proof 保守退出；普通/Family 自动接线与三容器适配，Transient 外部 view 与包内合同；完整测试和真实包通过 |
| [DB-065 EventHistory 消费者上手与恢复合同](0065-event-history-consumer-contract-slice.md) | 已实施 / G0–G3 | 默认调用、包内 XML、引用事件快照、同源恢复故障测试和 README 原文/真实包验收；产品行为和格式保持，locator/分页按需求后继 |
| [DB-062 独立图读取与可由外层发布的工作区](0062-independent-graph-workspace-slice.md) | 已实施 | 内部独立资源、快照、根替换与顺序双图读取；E/S 共享前 S baseline；公开 Journal 外观后继 DB-063 |
| [DB-063 EventJournal 驱动的 EventHistory](0063-event-history-journal-slice.md) | 已实施 | Journal 唯一 ref、E/S 交错提交、同实例 State 根替换、历史分支/Move/Resume、独立浏览与实验性 ReadPair；旧发布器删除，示例迁入新外观；验收见分片 |
| [DB-064 两份 Revision 共享读取](0064-shared-revision-decoding-design.md) | 已实施 | 操作内 exact DTO/string 解码复用、只读引用闭包共享与真实包验证；保持逐视图验证、冷 Resume 可变隔离及无跨图实例身份承诺，验收见分片 |
| [DB-061 跨程序集继承与基类状态投影](0061-cross-assembly-inheritance-slice.md) | 已实施 / G0–G3 | typed base projection、Family own + immediate base、class 只读历史与严格 helper 合同；隐藏泛型/Nullable、参数合流/重排、旧 CLR 删除及 leaf-only Upgrade 经测试和两代真实包验证；展开 DTO/body 与 State 格式不变 |
| [DB-060 跨程序集固定 inline 值与只读历史依赖](0060-cross-assembly-inline-history-slice.md) | 已实施 / G0–G3 | 直接外部值字段、按需只读模板导出、独立 history/生成归属；真实 ref/lib 三库两代包验证旧 CLR 删除、显式 Upgrade 与增量续写；history/State 格式不变，外部 base 另片 |
| [DB-059 跨程序集模型目录与类型组合](0059-cross-assembly-model-composition-slice.md) | 已实施 / G0–G3 | 外部 nominal/动态参数、显式 Family、普通 Model/reader 名义证据；独立模型包升级时 App/Host DLL 和 World history 不变，旧 inline CLR 删除后仍可恢复续写；固定外部 inline/base 模板另排 |
| [DB-058 DateOnly / TimeOnly / DateTimeOffset 内建值槽](0058-temporal-scalar-value-slice.md) | 已实施 / G0–G3 | 三值完整公开表示、offset 持久相等性、静态 body/组合/history v9、字典双比较与显式升级；两代真实包保留旧 inline 历史并升级续写；DateTime Local/DST 独立后继 |
| [DB-057 Guid / decimal / TimeSpan 内建值槽](0057-bcl-scalar-value-slice.md) | 已实施 / G0–G3 | 三种完整表示、静态 body、组合/history v8、字典双语义及显式升级；真实包保留旧 inline 历史并升级续写，Tuple 多 child 后继保留于 §8 |
| [DB-056 record struct 持久值](0056-record-struct-state-slice.md) | 已实施 / G0–G3 验收通过 | positional/readonly/generic、显式 backing storage 分类与强类型字段投影；复用 Family/InlineValue/DB-055，完整图/历史/两代删除旧 CLR 的真实包验证；无新增持久格式 |
| [DB-055 复合 Key 的持久状态与当前比较](0055-composite-dictionary-key-design.md) | 已实施 / G0–G3 验收通过 | Default 零配置、Application typed/惰性泛型 resolver；4/5 回捕稳定、历史数据/当前 TryAdd 分层；完整 tests 和两代删除旧 CLR 的真实包见证；旧固定字段 comparer 推荐已归档 |
| [DB-054 Dictionary 内容对象](0054-dictionary-content-object-slice.md) | Implemented / G0–G4 | 白名单双槽冻结、键寻址 Delta、完整 source/current 校验与历史升级；BCL 外观保持实验性，复合值 Key 为明确后继；全量及九条真实包验收 |
| [DB-053 显式 enum 内联状态](0053-enum-inline-state-slice.md) | Implemented / G0–G3 | 有 nominal 身份的单整数 InlineValue、外置静态投影与完整组合/历史升级；常量表不入 Schema、无新增格式，八整数和跨版本真实包验证 |
| [DB-052 可组合 Nullable 值槽](0052-nullable-value-slot-slice.md) | Implemented / G0–G4 已验收 | exact child、unmanaged 状态包装、字段/泛型/数组/List 与显式升级提升；history v6、SCB1 v2、完整tests/真实包/独立审查通过；不含 enum 或 Dictionary |
| [DB-051 完整基准与限长竞争的默认 List Delta](0051-bounded-list-delta-competition.md) | Implemented / G0–G5 已验收 | 流式 patch、完整 Local 基准、独立有界 Myers 与严格长度竞争；默认 Adaptive、根 tests/真实包/白盒落盘/双 seed 矩阵及独立审查通过 |
| [DB-050 List 匹配回退研究](0050-list-fallback-research.md) | Research / 实验已验证，后继 DB-051 | 共享有界 kernel、双向单次救援及反证；保留原共享/半预算实验条件，字节代价验收转入完整基准与限长竞争计划，未增加公开策略或改变默认 |
| [DB-049 List 区间 Delta 与匹配算法对照](0049-list-range-delta-and-matcher-trial-slice.md) | Implemented / G0–G4 已验收，默认后继 DB-051 | 静态 StateEquals、统一 codec 2、三种 writer 及快照配置；根 tests/五组相关真实包/独立审查和同领域历史正式重放通过；原 LocalResync 默认的选择证据保留，当前默认见 DB-051 |
| [DB-048 List 差分算法与编辑原语调研](0048-list-delta-algorithm-research.md) | Research / 后继 DB-049 | 文献与源码证据、区间复用及稀疏 Patch 候选；用户后续选择静态比较并降低冷读优先级，具体施工转入 DB-049 |
| [DB-047 List<T> 内容对象](0047-list-content-object-slice.md) | Implemented / G0–G3 已验收 | 完整槽组合、简单位置 Delta、List owner Upgrade、统一目录和 history v5；各测试项目与七组真实包通过；高效插入 Diff/Patch 后继选型 |
| [DB-046 统一闭合 Schema 目录](0046-unified-schema-catalog-slice.md) | Implemented / G0–G3 已通过验收 | 合并 Schema/表示记录及单批次登记；exact base/inline 依赖按 ID 寻址；保留当前闭合与版本规则；完整 tests 与四组真实包通过 |
| [DB-045 持久表示 ID](0045-persisted-representation-id-slice.md) | Implemented / G0–G3 已通过验收 | 复用 ObjectLayout，SchemaStore 同日志持久整数登记、Base v4 ID 头与历史 reader 解析；后续清理移除旧 Base 头读取，不改模板/版本模型；初次完整测试及 Array/Generic/ValueUpgrade 真实包证据保留 |
| [DB-044 类型头与 Schema 寻址盲评](0044-type-header-blind-review/README.md) | Research / 未裁决 | 隔离上下文、相反阅读顺序与交叉质询；比较版本化开放模板 token、闭合登记别名及表示版本政策；未修改产品格式 |
| [DB-043 可组合数组与统一引用对象路径](0043-vector-array-object-slice.md) | Implemented / 已通过整体验收 | 统一 object 分派 string/class/array；SZ/rank 2–4、开放泛型/jagged/inline 元素、融合 Delta、数组 owner Upgrade；history/schema v4、Base envelope v3；完整测试和真实包证据见分片 |
| [DB-042 Upgrade exact Schema 依赖证书](0042-upgrade-schema-requirement-set.md) | Chosen / Implemented | plan 级展平 base/inline requirement set、同键异形与晚登记冲突路径诊断、callback 前统一复核；不含 catalog generation |
| [DB-041 非泛型 ObjectId](0041-object-id-state-representation.md) | Chosen / Implemented | DTO/Runtime/StateStore 语义 ID 隔离；Schema/wire 不变，完整测试和六项真实包回归通过 |
| [DB-040 typed ObjectId 调研](0040-typed-object-id-representation-research.md) | Research / Deferred | DTO 品牌的版本/nominal 含义、CLR 加载边界、uint backing + typed 属性及 nominal marker 对照；未修改产品 |
| [DB-039 可组合值 Upgrade](0039-composable-value-upgrade-design.md) | Chosen / Implemented | G0–G3：Context 预绑定 typed 值工具、规则集/exact 槽依赖/provider 子作用域；整链拒绝规则、共享 DAG 与三代真实包闭环已验证，见 §8 |
| [DB-038 泛型 Schema、状态与绑定](0038-generic-schema-state-and-binding-design.md) | Chosen / Implemented | G0–G4：闭合 TypeExpr/history v3、Family DTO/静态 body、按需绑定、通用/闭合 Upgrade 与最小 Context；完整回归与三代真实包通过，值工具后继见 DB-039 |
| [DB-037 inline struct 状态](0037-inline-struct-state-slice.md) | Chosen / Implemented | G0–G4：显式非泛型 struct、exact Schema DAG/history v2、静态嵌套 DTO/融合 Delta、ref 恢复、owner Upgrade/连续保存与删除领域值声明后的历史包恢复；不含泛型或数组对象 |
| [DB-036 工作会话与历史恢复能力](0036-working-session-and-history-capabilities.md) | Chosen / Implemented | 单 head Repository/同实例 GraphSession Commit、原 lease 屏障/严格发布日志、迁移壳跨版本真实包见证；不含自动坏尾恢复或联合 Store 视图 |
| [DB-035 公开合同与持久格式术语迁移](0035-public-contract-terminology-migration.md) | Chosen / Implemented | 删除 legacy、唯一 State model、`.dgschema`、生成/runtime/Storage 术语；State wire v3 不变 |
| [DB-034 领域引用图与首次保存](0034-durable-reference-graph-batch.md) | Chosen / Implemented | G0–G6：nominal Schema/history、非泛型 class 共享/循环、队列 Capture、两阶段 Restore、PrepareNew 与独立增量续写；不含 Commit/发布 |

## 按任务查阅的混合决策

这些文档跨越已实施、已接受未实施和未决问题；仅在路线图指向相关主题时阅读对应章节。

| 文档 | 原状态 | 适用范围与阅读提示 |
|---|---|---|
| [DB-001 Schema authority 与运行时表示](0001-schema-authority-and-runtime-representation.md) | Deferred | canonical authority、typed projection 的候选依据；未裁决持久 Schema 格式 |
| [DB-014 多历史 Segment 与 BackwardFileDistance](0014-multi-segment-backward-file-distance.md) | Chosen | 文件地址/轮转方向已采用；文中未来发布、恢复和 GC 保证不是 Storage 完成声明 |
| [DB-018 统一引用身份、TypeCodec 与 Serializer 形状](0018-generated-graph-codec-shape.md) | Open，混合 | 统一身份与 Schema 依赖原则；直接领域 body 示例已由 DTO 路线取代，泛型 binding/Restore 等仍含候选 |
| [DB-024 引用 Capture 与可复用 ObjectId](0024-reference-capture-and-reusable-object-ids.md) | 首片实现，回收/恢复延期 | §2 身份作用域、§4–6 回收素材、§8 struct 设计；不把推荐的复用时机当作已选算法 |

## 已实现分片与被取代的施工证据

查接口缘由、诊断或验收证据时使用。表中“实现”限于所列分片；后续工作由路线图管理。

| 文档 | 原状态 | 实施/历史范围 |
|---|---|---|
| [DB-002 读取阶段版本升级](0002-read-time-version-upgrade-pipeline.md) | Chosen | legacy boxed 相邻升级 coordinator；不是新 DTO 图 Restore |
| [DB-003 Snapshot 值形状与 Upgrade 签名](0003-snapshot-value-shape-and-upgrade-signature.md) | Open | legacy mutable struct / in-out 已采用；性能、长期 ABI 与新 DTO 升级仍需另议 |
| [DB-004 Snapshot History 创作与发布](0004-snapshot-history-authoring-and-publishing.md) | Open | 单项目 local Publish / CI Verify 已落地；不是持久 SchemaStore 或多 writer 事务 |
| [DB-005 Durable 继承展平](0005-durable-inheritance-flattening.md) | Chosen | 早期分段取舍；落地证据接 DB-019/022/023 |
| [DB-015 对象表示策略](0015-statestore-object-representation-policy.md) | Chosen | 整数参数、估算 DTO → 稀疏 Base/Delta plan；未执行 Save |
| [DB-016 codec-first 纵切选择](0016-next-product-object-content-slice.md) | Chosen | 历史排期理由；不再作为下一工作单 |
| [DB-017 对象 codec 初始草稿](0017-object-codec-design-points.md) | Superseded | Robird 经验与早期选项；string 字段 inline 已被取代 |
| [DB-019 祖先 Schema/history](0019-schema-ancestry-implementation-slice.md) | Chosen / Implemented | 声明段、exact 祖先与版本传播 |
| [DB-020 typed 槽位与数组元素](0020-typed-slot-array-binding-slice.md) | Chosen / Implemented | SZ/rank-2 ref 元素循环；不是完整数组对象 codec |
| [DB-021 primitive class body](0021-generated-primitive-body-slice.md) | Superseded | 曾直接读写领域对象，后改用 DB-022 DTO |
| [DB-022 Versioned DTO 与 Capture](0022-versioned-state-dto-capture.md) | Chosen / Implemented | readonly Vn、current Capture、DTO body；跨 Schema Delta 仍是后续问题 |
| [DB-023 13 种标量贯通](0023-scalar-schema-dto-slice.md) | Chosen / Implemented | Schema/history/DTO 编码的标量扩充 |
| [DB-025 string 解码与引用校验](0025-string-object-decoding-slice.md) | Chosen / Implemented | string 表、各版 DTO 引用校验、Empty 规范化；未完成领域 Restore |
| [DB-026 raw Base 内容存取](0026-raw-base-object-content-slice.md) | Chosen / Implemented | 同 Frame local Base、wire v2、指定 Revision 内容重开读取；不是完整 Save/发布 |
| [DB-027 同版 DTO 比较与字段 Delta body](0027-generated-same-schema-delta-body-slice.md) | Chosen / Implemented | SG 融合 PrepareDelta、owned payload 与独立 Apply；不含持久 prior 链或 Save |
| [DB-028 持久对象 Delta、exact prior 与重建链](0028-persisted-object-delta-chain-slice.md) | Chosen / Implemented | raw Base/Delta、wire v3、Parent/prior 校验、object-first 链与 H、真实 SG 冷重开；不含持久类型目录或 Save/发布 |
| [DB-029 已准备对象内容到可追加 Revision](0029-prepared-object-revision-planning-slice.md) | Chosen / Implemented | SG/string PrepareBase、prepared rows、B/D/H、固定 policy、raw Revision；typed 适配仍为集成见证，不含完整 Save/发布 |
| [DB-030 异构 Capture 图统一准备内容](0030-captured-object-preparation-slice.md) | Chosen / Implemented | SG 对象级 binding、Runtime 完整 prepared 内容；作为未来工作会话内部组件，不含 exact Parent baseline、持久类型或 Save/发布 |
| [DB-031 持久 Schema 注册与 Base 类型引用](0031-persisted-object-type-envelope-slice.md) | Chosen / Implemented | 单调持久注册、严格恢复、Base exact 引用、同版 typed 冷读及保存桥接；不含自动修复、Upgrade/Restore 或 Commit |
| [DB-032 完整 exact-version 对象目录冷读](0032-exact-revision-decoding-slice.md) | Chosen / Implemented | SG 历史 reader 登记、局部目录与完整 Revision 自动读取、目标视图 string 引用验证；不含 Upgrade/roots/Restore/Commit |
| [DB-033 升级、领域恢复与增量续写](0033-upgrade-restore-resave-batch.md) | Chosen / Implemented | G0–G6：标量/string 单对象 DTO Upgrade、无构造器/readonly Restore、受控 LoadedWorld、冻结 Prepare 与 Append 后重新 Load；不含一般类型扩展、持久 roots 或 Commit 发布 |

## 隔离研究与技术储备

四个早期 Probe 已退出活动工作树；原源码、局部文档及运行方式从[Git 归档索引](../../experiments/ARCHIVE.md)
按需恢复。下表保留设计结论的历史入口，不要求读取或恢复对应 Probe 才能继续产品开发。

研究结论的适用边界不能随 Chosen 标签跨到产品。先读[实验目录入口](../../experiments/README.md)，按真实问题再取素材。

| 文档 | 原状态 | 使用限制 |
|---|---|---|
| [DB-006 Flat Graph Delta](0006-flat-graph-delta-prototype.md) | Chosen，R1–R3b 完成 | test-only normalization/materialization；RequiresRewrite 与 normalized baseline 不自动成为新 DTO Save 合同 |
| [DB-007 自适应双腿轮转](0007-adaptive-two-leg-rotation-policy.md) | Deferred | 冻结 TwoLeg 控制与评价研究 |
| [DB-008 contextual self address](0008-revision-contextual-self-address.md) | Chosen | 字段局部 self 思想可复用；旧 same/previous wire 属于 TwoLeg |
| [DB-009 Base lineage parent locator](0009-base-lineage-parent-locator.md) | Chosen | relay-free lineage 证据；旧不复用 ID 假设不适用产品 |
| [DB-010 Base shared prior anchor](0010-base-lineage-anchor-scope.md) | Chosen | shared anchor 研究；产品复用 ID 时须重新验证新占用者边界 |
| [DB-011 两阶段规划与容量](0011-two-phase-save-planning-and-capacity.md) | Superseded | 旧 TwoLeg planning/admission；不加入正常产品 Save |
| [DB-012 多策略 Benchmark Arena](0012-two-leg-strategy-benchmark-arena.md) | Deferred | 仅 TwoLeg 多策略研究，不是 MVP 插件机制 |
| [DB-013 Hot/Cold 双层 segments](0013-tiered-state-segments.md) | Deferred | 旧 two-file placement 储备 |

## 机制见证附件

- [DB-018 运行时泛型 binding 见证](0018-runtime-binding-witness.md)：typed ref + 闭合泛型的独立机制，不能证明通用 codec registry 已实现。
- [DB-018 泛型 DTO 后续技术备忘](0018-generic-dto-binding-followup.md)：2026-09-07 已认可方向；静态缓存、运行时闭合及领域/冻结表示参数分离，未实施 SG 泛型 Schema/DTO，不是下一施工分片。
- [DB-025 独立空字符串分配见证](0025-empty-string-allocation-witness.md)：未采用机制的历史证据；产品明确统一为 string.Empty。

## 新增与结束文档

新的设计文档继续使用稳定 DB 编号，写清问题、当前证据、决定或候选、范围及验收边界。
完成后把能力变化写入 PROJECT，长期决定放目标设计，剩余问题放路线图；本文只增加分类和证据链接。
不靠批量重标 Chosen 消除歧义，也不向历史文档追加每轮测试账本。历史矛盾在入口说明适用时点与后继，保留原始证据。
