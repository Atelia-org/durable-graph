# DurableGraph 后续工作与未决问题

> 本文只维护尚未完成的能力、待裁决问题及延后条件，不维护完成历史或充当实施授权。
> 当前能力、焦点和证据入口：[src/PROJECT-STATE.md](../src/PROJECT-STATE.md)。
> 已选约束及完整产品目标：[目标设计](DurableGraph-target-design-v0.md)。
> 旧阶段安排与 R1–R7 详情：[2026-09-06 归档](archive/2026-09-06/DurableGraph-research-roadmap.md)。

## 1. 下一个分片如何选择

当前能力与已完成分片的验收从 PROJECT-STATE/其账本进入；这里仅保留后续增量。

2026-09-11 的真实消费者 [DramaBoard Event/State 草稿](../../drama-board/archive/firstboard-llm/docs/research/event-journal-state-store-draft.md)
触发独立历史图、外部发布和分支需求。[DB-063](design-branches/0063-event-history-journal-slice.md) 已完成 EventHistory 外观。
操作内解码复用与只读闭包共享的能力/验收从 [DB-064](design-branches/0064-shared-revision-decoding-design.md) 和 PROJECT-STATE 进入，不再列为待施工项。
首轮 [API 反馈](../../drama-board/docs/feedback/durablegraph/001-eventhistory-api.md) 是接口评审，尚非真实接入故障或长轨迹测量；
其近期默认调用、文档交付与恢复示例改进已由 [DB-065](design-branches/0065-event-history-consumer-contract-slice.md) 完成。
第二轮 ReadPair 比较与 Transient 合同由 [DB-066](design-branches/0066-readpair-comparison-and-transient-contract-slice.md)
承接，验收从该分片与 PROJECT-STATE 进入。后继仍与下游真实接入互相校准：

第三轮真实模型反馈触发的 record class 与接口迁移已由 [DB-068](design-branches/0068-record-class-model-slice.md)
完成；[DB-072](design-branches/0072-generator-immutable-leaf-classification-slice.md) 分类的最终类型证明与 Family 覆盖缺口
已由 [DB-075](design-branches/0075-immutable-leaf-proof-and-family-refactor.md) 修复并验收。
既有能力与证据从 PROJECT-STATE/分片进入，不重开已完成工作。

| 分片 | 要解决的增量 |
|---|---|
| [DB-083 Repository / Checkpoint 用户故事](design-branches/0083-repository-checkpoint-api-user-stories.md) | A 公共基础与 B 独立 Checkpoint/固定查询的实际状态见 PROJECT-STATE；余项是 C 多分支与一步 Fork，tag 独立 |
| [DB-084 EventJournal 不可变 tag](design-branches/0084-eventjournal-immutable-tags-slice.md) | 目标已纳入、工程独立：上游已交付本地包 `0.1.2-dev.20260927.1`；DG 待更新 pin 并验收创建/解析、严格 Open/持久确认；DG 接入依赖 078-A，不依赖优化片。首片外部序列化地址不提供 |
| [DB-076 高效 fork 技术路径](design-branches/0076-efficient-graph-fork-technical-path.md#10-分片施工导航) | 后续顺序为 078-C 多分支/Fork → 079 共用恢复 → 080 单 State 准备槽 → 081 热 State 准入；082 叶实例实验只硬依赖080。078 全部阶段完成才形成 named Fork 产品停点；C 与优化片尚未实施 |
| [DB-077 固定模型环境](design-branches/0077-repository-model-environment-slice.md) | 已实施，验收见[记录](design-branches/0077-model-environment-implementation.md)；固定 Open 模型与 durable current 恒等；公共基础迁移由 [078-A](design-branches/0078-a-repository-free-history-implementation.md) 承接。[旧专项评审](design-branches/0077-0078-api-dialectical-review.md)保留历史，不作为新公开合同 |
| [DB-073 实例缓存](design-branches/0073-repository-scoped-weak-reference-cache.md) / [DB-074 方向清单](design-branches/0074-efficient-fork-deferred-directions.md) | Draft：均尚待进一步修订；不作为 DB-077–082 的并行工单或前置，缓存/更深优化取舍待分片实测后再处理 |
| DramaBoard 后续实测 | 继续玩法和较长轨迹，使用真实业务字段做两代升级见证。第三轮单次未预热 Debug 数据不证明缓存瓶颈或可变模型更优；纯 fold 的新实例 Base 与 map Remove 仍属增量保存，不触发跨实例内容配对 |
| 读取优化的后继 | 已交付读缓存的机制与预算证据从 [DB-067](design-branches/0067-owned-revision-read-cache-design.md) 进入；默认容量调整、构建器替换、Normalize 复用和进一步共享比较优化均由真实工作负载触发，见 §4。不承诺跨图实例复用，不改变可写 Checkout 隔离 |

ArtifactStore 在该路线中先由 EventHistory 覆盖事件/快照职责，不另建内容 Store；
大附件/chunk、联合 Schema 视图与 Derived 仍未随之解决。以上方案不要求先实现 ValueTuple 或重排全部程序集。

[DB-046 统一闭合 Schema 目录](design-branches/0046-unified-schema-catalog-slice.md) 已合并持久记录、批次和 exact 依赖引用。
[DB-047 List<T> 内容对象](design-branches/0047-list-content-object-slice.md) 已完成；功能与验收以该分片为入口。
[DB-051](design-branches/0051-bounded-list-delta-competition.md) 已完成默认 Adaptive，§3.2 只保留实测触发的后续优化。
[DB-052 可组合 Nullable 值槽](design-branches/0052-nullable-value-slot-slice.md) 已贯通字段、泛型、数组/List 的历史表示与显式升级。
[DB-053 显式 enum 内联状态](design-branches/0053-enum-inline-state-slice.md) 已贯通单整数布局、组合与历史显式升级；
常量表不入 Schema 的已选合同维护在目标设计，验收以该分片为入口。
[DB-054 Dictionary 内容对象](design-branches/0054-dictionary-content-object-slice.md) 已完成白名单范围的保存、键寻址 Delta 与历史双槽升级。
有限复合值 Key 与当前 comparer 见 [DB-055](design-branches/0055-composite-dictionary-key-design.md) 的实现及验收记录；
领域比较采用当前业务代码，DTO 按全部持久字段独立配对。
[DB-056 record struct](design-branches/0056-record-struct-state-slice.md) 已补齐 positional/readonly/generic 外观、
backing storage 显式分类及现有 InlineValue/历史链的组合。
ValueTuple、同型多 Application 角色及引用内容比较仍各自独立，不预建通用容器平台。
开放模板方案的评估结论与重访条件见 §3.1。
命名分支、历史 fork/Move 和同实例 State 提交已由 DB-063 闭合；联合 Store 视图及更强恢复保证仍独立排期。
MVP 库内加载顺序为 exact 重建 → 单对象 Upgrade → 分配实例 → 填充/连接引用 → 完整交付 World；
Transient 由用户在交付后处理，约束维护在[目标设计](DurableGraph-target-design-v0.md#恢复transient-与宿主边界)。
数组形状、升级、单根、Transient hook、boxed value，以及无需无参构造器/readonly 字段的支持选择见
[MVP 功能边界](DurableGraph-target-design-v0.md#mvp-功能边界)，不再作为开放范围反复讨论。
EventHistory 的正常同实例 State 提交与严格重开从 PROJECT-STATE/DB-063 查证；不再列为未完成能力。
当前公开宿主仍限定单活动 `BranchCheckout`、每图单个非空 durable 根；A 已迁移非泛型入口、自由历史与跨类型根。B/C 尚未实施；发布故障范围仍为正常关闭/进程中止和明确的 I/O 异常。
DB-038 的泛型 Schema/history、开放生成、保存恢复与通用/闭合 owner Upgrade 从 PROJECT-STATE/施工记录查证，不再列为未实现机制。
可组合值 Upgrade 的验收与实际范围见 [DB-039](design-branches/0039-composable-value-upgrade-design.md#8-产品施工合同与验收映射)。
[DB-043 可组合数组与统一引用对象路径](design-branches/0043-vector-array-object-slice.md) 已通过整体验收；
完成证据集中维护在该分片，不再将数组组合列为待施工架构。
完整表示的整数寻址不再作为新待办；其内部描述模型、协变和加载内存预算仍按各自边界推进。

## 2. 已采纳方向中的未完成能力

此表只列仍需工作的增量。方向已选不代表每项 API、顺序和细节已经批准。
B/D/H 分别指本轮精确 Base payload、Delta payload 上界、已有对象重建链的实际 payload 字节；
均排除 ObjectId、ObjectHeadMap 目录与共享 Revision Frame 结构。

跨程序集 nominal/动态组合、固定 inline 和基类状态投影已贯通，
能力及验收从 PROJECT-STATE/[DB-061](design-branches/0061-cross-assembly-inheritance-slice.md) 进入。
剩余的自动业务规则发现仍独立延后，不再将直接外部值字段或基类列为待实现。
[DB-058 DateOnly / TimeOnly / DateTimeOffset](design-branches/0058-temporal-scalar-value-slice.md) 已贯通现有值/历史管线；
能力与验收从 PROJECT-STATE/分片记录进入。剩余 DateTime 的保存合同、ValueTuple 和跨程序集各自独立。
[DB-057](design-branches/0057-bcl-scalar-value-slice.md) 的 Guid/decimal/TimeSpan 主体与 history v8 已实现，
能力与验收从 PROJECT-STATE/分片记录进入，不再列为待办。

ValueTuple 并非不可实现，但 record 已提供复合值建模路径，Tuple 还需一次多 child exact 布局扩展；
关键反例与后继调查保留在 [DB-057 §8](design-branches/0057-bcl-scalar-value-slice.md#8-valuetuple-后继保留的问题)。

List 高效 Diff/Patch 已完成；额外性能工作按 [§3.2](#32-list-差分算法选型与设计)的实测条件重访。

## 3. 尚待裁决的机制

| 问题 | 现有依据与裁决边界 |
|---|---|
| 引用 ID 的泛型目标品牌 | 已选非泛型包装见 [DB-041](design-branches/0041-object-id-state-representation.md)。泛型化暂缓；确有目标级静态检查需求时重访 [DB-040](design-branches/0040-typed-object-id-representation-research.md) 的版本含义、phantom nominal 与历史表示依赖，以及 typed 物理字段的 CLR 加载边界 |
| 对象版本解释与保存来源 | Base 表示 ID 可解析 exact 布局与已登记历史 reader；完整 ObjectHeadMap 中 external object heads 的来源、候选对象身份连续性仍需产品 Save/Load 合同，不能由 Revision Parent 声明一致推导全局身份认证 |
| 保存相等性与真实估算 | 同版 DTO 的浮点按位、引用槽按 ID、inline 值递归融合 Delta 已采纳；DB-043 数组复用元素操作，BCL 容器另定。已准备 body 计量见 [DB-029](design-branches/0029-prepared-object-revision-planning-slice.md)，ID 头见 [DB-045](design-branches/0045-persisted-representation-id-slice.md)；新增容器继续按实际对象 payload 计量 |
| Schema 规范表示和持久引用 | 开放模板/参数与绑定模型的剩余问题见 §3.1；不再将已统一的闭合目录作为待办。未来 SchemaHash 与一般类型家族约束随消费者裁决，不用 GetHashCode 作持久身份 |
| 跨程序集与一般类型形状 | 基类/固定 inline 不再是组合阻碍；跨程序集业务规则扫描、完全无当前声明空库的历史能力生成与独立闭合历史账本分别按具体需求裁决，不与只读模板导入混同。DateTime 的 Local/DST 合同、native int 等各自按需求选择；boxed value identity 已排除 MVP |
| 多态与运行时注册扩展 | 已标记 class 基类到登记派生实例按 DB-034 合同；DB-043 统一框架 object 参数不授予 object/interface 通配字段。数组协变还需空数组的历史元素 ancestry 证据，和跨程序集发现分别后继；不能自动回退成声明基类的 codec |
| 任意外部基类适配 | DB-068 已消除框架基类要求，用户可自建逐层参与 Durable Schema 的继承体系。未标记第三方基类仍拒绝；无当前需求，不推断可支持。真实消费者确需时须先证明祖先状态、构造/恢复不变量及版本演进可建模，再裁决是否扩展 |
| 捕获 BCL 内容的所有权 | 数组使用 owned frozen 元素 buffer，inline struct 递归捕获成标量/ID；后续容器同样不能以浅复制代替冻结，须按其内容模型验证 |
| 根与持久目录扩展 | 根替换、独立读取与 branch/Move 已有实现；A 扩至跨类型根，见其实施记录。每份持久图仍一个非空根；Event-first 的无 State 不等于持久 null 根，不扩为命名根目录或清空 State API |

设计证据：[DB-006](design-branches/0006-flat-graph-delta-prototype.md)、
[旧路线图 R3/R4](archive/2026-09-06/DurableGraph-research-roadmap.md)、
[运行时绑定见证](design-branches/0018-runtime-binding-witness.md)。
这些实验选择不自动成为新 DTO 产品路径的 API 或强制前置项目。
DB-009/010 的旧 no-reuse 前提不能沿用；借用 Base 共享 prior 等结论时也需重新检查 ID 新占用者边界。

### 3.1 版本化表示类型头的统一寻址

完整闭合表示的持久整数 ID 边界与验收由 [DB-045](design-branches/0045-persisted-representation-id-slice.md) 维护；
统一闭合目录、Schema 记录直接作为 class 表示、base/inline 整数依赖由
[DB-046](design-branches/0046-unified-schema-catalog-slice.md) 完成。
长期合同归入[目标设计](DurableGraph-target-design-v0.md#长期-schema-可读与显式演化)。
前序替代方案及反例保留在 [DB-044](design-branches/0044-type-header-blind-review/README.md)，不再把是否登记完整表示 ID 当作未决问题。

后继问题是：是否持久化版本化开放模板 + 必要 exact 实参，并让绑定直接消费闭合描述，
替代现有“展开完整 Schema，再与模板反向匹配”的往返。仅增加模板编码、读后仍展开并反向匹配，
会增加维护面；应先明确能删除哪些机制，并保留手工 Schema/reader 的自然入口。
闭合 Schema 不能反推出字段原来声明为 `T` 还是固定 `int`，需要 SG/history 提供模板材料。

2026-09-09 的[下一片评估](design-branches/0047-list-content-object-slice.md#1-为什么选择这一片)确认：
当前目录冷读直接恢复闭合 Schema，Match 负责对 retained code history 的验证及 DTO 操作数推导；
构造后匹配的往返主要在 current binding 与 Upgrade 端点推导。未找到能整体删除一条机制且不增加等价验证路径的小切片，
故暂不推进模板持久化，推荐先做 List。重访应明确净删除/替换的路径，不能仅以新编码更紧密作为绑定重构理由。

名义参数与 exact 值参数不能一律合并：`ArrayHolder<T>` 在数组引用边截断 exact 依赖，
`Phantom<int>` 与 `Phantom<string>` 即使 DTO 相同也保持不同身份；纯 nominal 参数不得被强制要求拥有 exact Schema/provider。
`Box<Point>` 若允许同模板版本绑定不同 Point exact 版本，会改变现有同 key 唯一布局政策及 Upgrade 路由，
须独立裁决，不能借新号绕过冲突。前缀/后缀表达式编码不决定这些语义。

重访触发为 DB-046 稳定后发现模板展开/反向匹配的具体重复或限制；最小见证须覆盖泛型/数组、
固定及参数 inline 依赖、内建类型员工通道、phantom 身份与旧领域 CLR 删除后的历史 reader。
数组协变仍需独立验证空数组元素 ancestry。
所有组合只锁定本对象需要的 exact 解释，不锁定引用目标版本；元数据与可执行历史能力继续分别保留，不以 latest 补缺。

### 3.2 List 差分算法选型与设计

[DB-049](design-branches/0049-list-range-delta-and-matcher-trial-slice.md) 已完成静态比较、统一 codec 与三种 writer，
独立 Repository 的同领域历史重放及 [DB-050 回退研究](../experiments/ListDeltaReplayProbe/FALLBACK.md)也已完成。
[DB-051](design-branches/0051-bounded-list-delta-competition.md) 已验收默认 Adaptive：完整 Local 基准、独立有界 Myers、
严格限长竞争。用户接受触发路径的额外时间与短期分配，不要求共享一份比较预算或只编码一次。
当前能力与验收入口从 PROJECT-STATE 查阅；该片不保证每次触发都有收益，也不消除无停滞的重复值坏匹配。
[实测](../experiments/ListDeltaReplayProbe/ADAPTIVE.md)保留完整基准的准备成本；若这成为真实消费者瓶颈，再按下面触发条件展开。

DB-051 之外的性能工作以实际轨迹或测量问题触发，不自动扩大该施工片：

- 实际列表尺寸、编辑分布或保存频率明显不同于合成样本时，用现有 Probe 调整规模/种子/预算，
  分别比较完整 Commit、隔离 Diff、分配和策略后的实际写入，不能拿 candidate body 当实际文件节省。
- 候选配对的字节代价验收由 DB-051 的完整基准与限长竞争承担；后续若仍有开销问题，再研究
  元素级编码复用、池化、更细的子 codec 中断及已编码区间复用。保留 Marker 反例、重复值、边界控制和普通轨迹，不能只提高预算或按单例判优。
- 大块搬移、全部元素微改同时插入等场景出现实际写入问题后，再比较哈希匹配、偏移配对或局部 New/Patch 竞价；
  新匹配策略只要输出同 grammar，就不增加格式版本。暂不引入 key/comparer、tracking 容器或全局最优脚本。
- 用户已明确冷读优化最低优先，完整恢复仍为硬条件；链变长只作观察，不为这轮匹配能力增加链长新策略、cache 或 accumulator。

改变 grammar 才需要新的 codec 解释；保持精确状态语义、准确 NoChange 与有界搜索回退。

## 4. 明确延后及重访条件

| 延后项 | 何时重访 / 届时要回答的问题 |
|---|---|
| 跨重开的历史记录定位（反馈 001-D） | 出现独立于可移动 branch 的调试书签、RL 样本或叙事引用需求后，设计“持久 locator → 本次有效 GraphFrame”。须明确仓库作用域/错误仓库、记录保留和 orphan 可见性、branch Move 后定位、无效/不可读错误；诊断 RevisionAddress 不能直接转正。现有命名分支只在应用保持它不移动时固定历史点，不是不可变书签 |
| 局部倒序历史浏览（反馈 001-E） | 真实浏览流程需要最近 N 条或连续分页时，先冻结所浏览 head，再选择起点/方向/限制与续页表示，验证顺序、无重漏及无需恢复无关 State。分别测量严格 Open、frame 枚举、对象恢复；不由 IEnumerable/Reverse/Take 推导惰性读取，不为分页削弱全历史校验。现有整链物化合同与只读入口见 README/DB-065 |
| DateTime 的完整保存合同 | 真实模型需要 DateTime 时，选择是否限制 UTC/Unspecified、接受公开状态规范化，或保留更完整的 Local ambiguous-DST 信息；需跨时区/DST 见证。三者公开行为不同，不随 DB-058 开放。平台依据及候选见 [DB-058 §3.3](design-branches/0058-temporal-scalar-value-slice.md#33-为什么-datetime-留在另一个问题中) |
| ObjectId 数字回收 | 单调分配配合其他机制开发后，再定义候选隔离、retire/reuse 时机与恢复；可评估 StateJournal SlabBitmap/SlotPool，不能复用旧对象 Delta 链 |
| 字典比较的进一步能力 | 同闭合类型多 Application 角色需要实例选择信息，引用内容比较需要确定恢复阶段，保留历史业务规则需要独立能力合同。均按真实需求重访；根 Nullable Key、ValueTuple 外观和标准模式迁移不随 DB-055/056 开放。比较合同仍沿 [DB-055 §10](design-branches/0055-composite-dictionary-key-design.md#10-审阅结论与后续裁决) |
| 后续映射与 BCL 集合 | ValueTuple 先解决多 child exact 槽、参数来源、Item/Rest 组合及完整历史能力，调查入口见 [DB-057 §8](design-branches/0057-bcl-scalar-value-slice.md#8-valuetuple-后继保留的问题)，再组合现有字典；Nullable 根 key 另排。SortedDictionary 另定排序比较，OrderedDictionary 另定顺序状态，Set 等逐类型排期；自建外观仅在明确 API 痛点下重访 |
| SchemaStore 后续能力 | MVP 单调注册已实现；联合 Commit/Ref 及复用 StateStore 的演进候选见下节，Dictionary 与内建类型 codec 完整后重访。多 writer、压缩/GC 另待真实需求 |
| Schema/表示日志自动修复与分段 | 遇到真实坏尾恢复或容量需求时；无额外确认水位不能自动区分未完成尾部和已确认末帧损坏，当前严格拒绝。重访时先冻结故障模型，不绕过完整注册一致性 |
| 发布恢复保证扩展 | DB-063 已闭合交错提交、expected head、数据/Journal/ref 屏障及严格重开；遇到真实可用性要求时再设计坏尾自动修复、OS crash/power loss 与目录持久性，不能默默回退旧 head |
| ArtifactStore | 事件/快照消费者已选 DB-063 EventHistory，暂不另建 ArtifactStore；大附件/chunk/外部地址尚无本轮证据，按实际需要重访，不强迫 State 常驻完整历史 |
| DerivedStore | 真实昂贵派生消费者出现；定义 exact 输入围栏、recipe/builder/model 身份、stale/missing 及可删重建 |
| 框架 Transient hook | MVP 明确不做，用户在完整图交付后自行重建；MVP 后若多个宿主确有重复的重建协调需求，再比较 hook/依赖调度及失败边界 |
| 多根产品 API | DB-062/063 以各自单根 Revision 满足 Event/State 独立读写；同 Revision 任意根列表、命名根与按根过滤解码仍延后 |
| boxed value 持久身份 | MVP 拒绝领域图中的装箱值对象；实际模型需要通过引用槽保留装箱值身份时，再增加局部 codec/身份支持；不影响框架内部 DTO 装箱 |
| 物理 GC、compaction、历史保留 | 出现真实空间或 recovery-closure 问题后；与 CLR 映射清理和数字 ID 回收分开裁决 |
| TwoLeg / incremental cleaner | 多历史 Segment 无法满足实际有界 dependency file count、在线退休、backup/rescue 或 compaction SLO 时重访，见其 [技术储备（归档）](../experiments/ARCHIVE.md#two-leg "原路径：experiments/TwoLegRotationProbe/PROJECT-STATE.md") |
| 性能优化 | frame/map 缓存与热保存 head/H 基线从 [DB-067](design-branches/0067-owned-revision-read-cache-design.md)、[DB-069](design-branches/0069-incremental-save-baseline.md) 查证。DB-069 同场景测量已分离出剩余 map 历史回溯；真实业务长轨迹中它成为主要保存成本时，比较祖先 map 复用或增量 head map，并独立证明完整 membership/prior 验证。默认缓存 8 MiB 与 [DB-070](design-branches/0070-read-amplification-default.md) 选择的保存参数均非已测最优值；出现真实长轨迹的历史空间或冷恢复耗时目标时，比较实际累计追加 payload、冷恢复耗时/链项数，并结合单对象 d/B 与有效冷读频率重新校准。全量 Base 准备、缓冲复制、typed buckets 或指纹由具体测量触发；DB-061 的逐层基类委托/DTO 前缀复制在深继承实际成为热点后再优化，不恢复两套投影路径。DB-042 的 Upgrade requirement set 仍逐次复核，批量历史对象测出热点后可评估 SchemaStore catalog generation |
| 双图读取的后继优化 | 操作内 DTO/string 缓存、保守引用闭包和完整状态比较的现状从 PROJECT-STATE/DB-064/066 进入。比较、Normalize 或内存驻留成为实际热点后，再研究明确 Upgrade 纯度/调用合同后的复用或受限跨操作缓存；多视图分区、深不可变白名单和内容 intern 需各自新需求。Frame/map 缓存另见性能项，不能把 body 解码计数下降写成物理 I/O 或峰值内存收益 |
| 加载内存预算 | 大数组/容器或不可信输入的资源控制成为实际需求时，设计独立的总分配/元素数预算；DB-043 先要求合法 shape、checked 计算及适用时的 payload 下界预检。零字节元素可产生大内存对象，单帧 256MB 不等于 CLR 内存上限 |
| 并发、分支与跨 Repository | 命名 branch/fork/Move 与串行单活动写工作副本从 PROJECT-STATE 查证；DB-078 拟允许不同 branch 各一个工作副本，但尚未实施，且不改变串行仓库操作。concurrent Capture、多 writer、merge、跨仓库身份仍延期。操作内解码或只读实例共享不改变这些并发和来源边界 |
| 跨对象升级与外部副作用 | MVP 仅单对象字段转换；读取其他对象、拆分/合并及创建持久新对象均延后。MVP 后有真实迁移案例时，再讨论图访问、新 ID 与失败隔离；不借普通升级默认授权 |
| 无 CLR 迁移壳的 current Normalize | Family 路径可以保留 state-only exact reader，但 editable Load 仍需要 source 对象族的 current/migration CLR 模型。应用需要删除这层模型而继续加载旧 Revision 时，再设计独立 Normalize/退休协议；不能借 World 删除引用跳过 source 行 |
| 历史工具/升级调用优化 | 有 package/history 或升级调用的真实限制后，再重访 DB-003 的 Try/result/ABI 和 DB-004 的多 writer/多 TFM 与批次原子性，不顺带做兼容框架 |
| UpgradeContext 的动态工具与生命周期扩展 | 当前查询已声明、已绑定的值工具；具体迁移确需执行中解析时，再裁决首个业务回调前失败保证。池化/异步或合法跨回调工具复用有消费者后，再设计生命周期；不提前添加服务定位器、租约或通用工具平台 |
| 值规则的跨程序集与更广生成外观 | 本地 marker/provider 已可显式使用只读导入的外部 inline 历史端点，仍不扫描依赖库的业务方法或自动选择规则。实际需要跨库发现/复用规则或更广属性入口时，再扩展显式登记/诊断；Runtime 的 builtin/引用/闭合模式能力不自动扩张 SG 外观 |
| 泛型闭合历史账本/独立版本 | 用户已选 MVP 仓库内严格一致；要求共享 build history 对所有空库也锁住闭合布局、跨程序集独立演化或跨库交换时，再比较闭合目录与布局身份，见 [DB-038 §3.3](design-branches/0038-generic-schema-state-and-binding-design.md#33-必须明确的保证作用域两个空仓库) |

### 4.1 SchemaStore 复用 StateStore 与联合版本视图

2026-09-07 用户提出的未来技术路线：StateStore 作为基础，在 Dictionary 等 BCL 内容 codec
可用后，评估用一个不含用户自定义类型的 StateStore 实例保存 Schema 定义集合，复用增量保存、
历史视图、回滚与分叉。该路线合理且值得保留，尚未选择具体数据表示或替换当前 MVP 实现。
联合 Commit/Ref 的长期约束见[目标设计](DurableGraph-target-design-v0.md#单一发布权威与明确故障结果)。

重访触发：受支持的 Dictionary 内容保存/重建、所需内建类型组合编码，以及 StateStore 的
Revision/加载能力足以表示元数据集合；建设联合 Commit/Ref 时也应重新检查本条。

届时需闭合：

- 自举：2026-09-07 用户补充“员工通道”思路，推荐由框架显式内建的类型码及 Schema/codec
  解释基元、受支持 BCL 构造和 DurableGraph 元数据类型，仅用户定义类型通过 SchemaStore
  取得定义；不能以程序集/namespace 归属自动豁免所有类型。内建复合类型不必退化为字典树，
  可以用内建 SchemaDefinition/FieldDefinition 等强类型数据表示（名称为示意，尚未实现）。
  若 Dictionary 的 key/value 和实际内容均属于内建类型闭包，即可在没有已加载 SchemaStore
  的情况下读回目录；Dictionary<string, UserClass> 则仍需解析 UserClass 的 Schema。
  目录中的用户 Schema 引用首先是元数据值，不能在解码这些描述数据时递归启动用户对象加载。
- 内建合同：豁免的是 SchemaStore 查询，不是持久布局的版本管理。内建类型身份及其 codec
  版本仍须由明确格式规则确定，不能直接依赖当前 CLR fields 布局；版本可由外层格式约定，
  不预先要求每个对象增加版本字段。引用类型继续共用对象身份、Capture 和 Base/Delta 流程。
- 逻辑职责：复用 StateStore 保存集合，不取消 Schema 的规范定义、exact 依赖与同 key 冲突检查；
  不让一般字典写入绕过 SchemaStore 语义。
- 联合视图：Commit/Ref 绑定各 Store 的 exact roots/Revision，加载与整体回滚使用同一组引用。
  Schema 记录物理保留不等于在所有 Commit 中可见；不要将 MVP 的全局注册索引直接当作未来视图。
- 冲突作用域：回滚/分叉后，同 `(SchemaId, Version)` 的一致性是仓库全局约束还是视图约束，
  须单独裁决。不能隐式允许同 key 不同定义而仍以无视图的 key 查找；任何选择都要保证历史 State
  永远解析到原 exact 定义。本条不授权 MVP 放宽冲突检查。
- 迁移与恢复：从 MVP 注册日志转入版本化目录时，定义旧 Schema 引用的解析、联合发布顺序和
  故障恢复；届时再实施，不现在预建迁移或双写框架。

## 5. 后续切片验收素材

每轮只挑与问题有关的见证，不重复搬运全部历史闸门：

- 保存状态律：unchanged 沿用旧记录，changed/new 追加，不可达退出新视图；
  成功后再次保存无伪变化，失败不能替换已提交基线。
- 类型/图读取：exact Schema 预检、typed decode/upgrade、引用目标合法性、共享/循环，
  late failure 不暴露半成品；升级删边与 source membership 的区别必须可观察。
- 格式：独立 golden bytes、canonical 编码、截断/未知标签/溢长/尾随数据与错误 prior 拒绝。
- 持久发布：在明确故障阶段注入错误并 reopen，证明 Revision Parent 或 exact candidate 的可裁决结果，
  不能仅用 Append 正常返回或内存 Accept 宣称 durability。

历史 logical graph、TwoLeg 和 MultiSegment 的材料由
[设计索引](design-branches/README.md)与[实验笔记入口](DurableGraph-lab-notebook.md)按需访问。
它们保留可复用状态律和反例；产品继续开发不默认重跑全部实验，也不照搬 fixture API。

## 6. 维护方式

- 新问题只在本表或当前分片设计中选一个详细维护位置，另一处放链接。
- 获得裁决后，将长期约束归入目标设计；本表保留尚未实现的增量与验收问题。
- 完成后删除对应待办或收窄剩余部分，产品工作集更新能力入口，完整结果进入历史证据。
- 延后项应有重访触发；不以日期顺序或旧阶段编号代替依赖关系。
- 新证据否定旧假设时替换原条目；历史理由由 Git 和归档保存，不在活跃正文不断叠加校准段落。
