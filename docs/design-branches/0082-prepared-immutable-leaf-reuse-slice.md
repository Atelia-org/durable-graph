<a id="db-082准备图范围的-immutableleaf-实例复用实验"></a>
# DB-082：单图准备材料范围的 ImmutableLeaf 实例复用实验

> 状态：**Proposed / 按 DB-083 校准的条件实验，未实施**。2026-09-27；此前源码事实基线 `343bfa6`。
> 目标合同：[DB-083 用户故事与 Checkpoint API](0083-repository-checkpoint-api-user-stories.md)。本片只优化非泛型 Fork/Checkout 已存在的最近 State 恢复，不恢复 Event，不改变 Event-first、自由 E/S、精确 Head 或默认 Checkpoint 的独立图语义。
> 推荐顺序：第 6 片；硬前置是 [DB-080](0080-prepared-checkpoint-reuse-slice.md) 已交付可用材料驻留，
> [DB-081](0081-hot-commit-restoration-material-slice.md) 不是正确性前置，但先完成它可一起测量热 fork。
> 消费已实施 DB-075 的 capability；不把 [DB-073](0073-repository-scoped-weak-reference-cache.md) 的旧 weak cache 正文当施工依据。
> 术语：采用[术语表](../DurableGraph-glossary.md#restoration-preparation)；恢复准备（Prepare）与保存侧内容准备区分。非泛型 `BranchCheckout` 与 named Fork 已由 [078-C](0078-c-branch-checkout-fork-implementation.md) 交付；材料与证书接缝见 [080 记录](0080-prepared-checkpoint-reuse-implementation.md)，本片叶实例复用尚未实施。

## 1. 要回答的问题

单图准备材料已复用后，对叶占优的 fork，少做 immutable 叶的 Allocate/Hydrate 是否足以抵消复用表、身份登记和驻留成本？
**本片是有明确停点的产品内实验：证明安全并得到总成本收益才启用；无收益可撤下接线并以结论完成。**
不扩展 DeepImmutable、不做跨单图准备材料配对、不实现 repository WeakReference 索引。

## 2. 最小机制

同一份 State 单图准备材料（prepared selection）附一个可选的 `ObjectId → instance` 只读叶表。
DB-080 的单槽按最近 State 检查点及其 exact revision/root/来源选择；每次请求仍先验证选中 Head 的逻辑导航，
工作副本 Head 来自本次请求，不存进槽。`S0→E1→E2` 的三个起点可以复用同一 S0 材料与叶表，但保持各自精确 Head。
该材料已固定完整 DTO/模型环境与来源，因此表内按 ObjectId 即可，不另建全局版本 key。
Event-only 前缀无 State 时没有单图准备材料：不查、不填、不复核叶表，也不清除另一分支已有的材料/叶表。
首次 State 提交即使获得热材料资格，也要到其完整成功物化后才可能建立本片叶表。
只收 `StateModelBinding.IsImmutableLeaf == true` 的可达 durable 对象；string 仍走既有规则，inline 值不单独入表。

root/reachable 固定，第一次完整成功物化时可一次构造全部合格叶的候选表；安装后只读，不逐次 merge 或触碰淘汰链。
没有合格叶时避免长期分配空字典。叶表跟单图准备材料的单槽驻留寿命释放；淘汰不会破坏已交付图的正常引用。
活动分支工作副本可以继续持有自身领域叶，缓存自身不额外无限保活过去 checkpoint。
分支工作副本/工作区只导入正常 normalized 保存基线、完整来源/head/H/SourceLayout/rewrite、身份和独立保存状态，
不因保存导入而持有恢复准备证书、叶表或整个单图准备材料/entry。保存仍执行原有来源与 Schema 校验。
叶表的缓存引用由单槽管理；物理上放在槽拥有的内部 wrapper 也可以，不增加公共类型。

初版只有 Fork/Checkout 消费和填充此表。ReadCheckpoint、ReadState/Event、ReadPair 不读取也不回填它；
它们仍共用 DB-079 物化核心。默认 Checkpoint 的两图独立物化，ReadPair 仍使用自己的只读引用闭包规划。
Event 提交按 DB-081 不改槽；新的 State 材料即使包含同 ID/head 或同值对象，也不继承旧材料的叶表。
跨实际类型根替换、旧根成为新根子对象或已有子对象升根也遵守此规则：候选保存可以保留正常实例 ID，
但新 revision 的恢复准备证书与叶表不因此获得跨材料继承资格。根的实际 binding 来自新选择。

## 3. 命中仍要参与完整身份协议

1. 先完成 DB-080 的来源/root/live Schema 复核，再取得内部可信复用映射。
2. 先登记所有复用实例，纳入本图 ObjectReadTable、实例→ID 映射及操作内冲突记录；然后分配其余对象。
3. 只有映射明确提供的实例免 Allocate/Hydrate；所有必要分配/登记完成后才 Hydrate 未复用的对象。
4. 每个分支工作副本仍建立独立 `CaptureSession`、cursor、工作区/保存基线安装状态；保存沿用完整源信息。

登记顺序覆盖本次完整 State：全部已有叶命中先进入操作内冲突记录，再执行需要 Allocate/Hydrate 的恢复步骤。
不增加全 repo 活对象表，也不承诺检测任意历史操作中的违规 singleton。

两个不同 ObjectId 不因值相等合并。普通 Allocate 即使声称分配 immutable 类型，也不能返回某个已登记实例绕过 singleton 检查。
**命中对象绝不再次 Hydrate**：生成器可以用低层 accessor 填 readonly 字段，再次填充会污染别的已交付图。
全 immutable 根可以被复用，两个 fork 的 State 可能 ReferenceEquals；独立性要求可编辑状态隔离，不要求所有根引用不同。

手写 true 仍是调用者断言，承担 DB-075 的结构不变量及 DB-077 的固定恢复合同；
结果不能依赖未声明的别的对象内容、调用次数或 mutable 全局状态。不新增手写反射复检或生成器身份认证。
源分支工作副本的 live State 或提交时传入的领域实例均不直接充种，即使其 binding 为 true；
只能从已提交 State DTO 的成功物化结果入表。热 State 入槽本身不会带入叶实例。

## 4. 候选表何时安装

叶 Hydrate 成功不等于整个图恢复成功。私下形成候选表，待本次整个 State 物化与工作副本交付准备成功后才具备安装资格。
Checkout 在交付门安装；Fork 在 ref 发布前构造好表，确认发布与交付准备成功后只引用赋值，
不在发布后进行字典扩容、callback 或新的复杂工作。
恢复失败、发布失败或未知结果不安装本次新表；已有成功表不因另一对象的普通恢复失败而被重填或破坏。
沿用既有 publication/fault 纪律，不为实例表新增事务、补偿或自动重试。
若第一次叶 Allocate/Hydrate 通过标准 context 检查了额外 Schema，后续复用会省略这些回调：
用 DB-080 的内部收集机制将相关依赖并入候选表的不可变证书，一起安装、每次命中先复核。
尤其热材料尚未执行物化，不能假定其最初恢复准备证书已经包含这些检查；不因叶缓存跳过原本应报告的权威冲突。
这些额外依赖随叶表和所在槽存活，不能因已交付工作副本持有叶引用而让仓库另行永久保留它们。
新 State revision 按 DB-081 建立自己的准备证书，首次物化时再收集其叶依赖；不得机械继承旧 State 的历史 Upgrade
中间依赖或旧叶表证书。旧依赖若仍被新布局或其实际恢复回调使用，应由新材料的证明与标准检查重新纳入。

## 5. 施工与验收

G0 加内部 A/B 开关与计数见证；G1 在共同物化接缝接入可信叶映射与冲突登记；
G2 完成成功交付门、生命周期/失败测试及普通/Family 真生成模型；G3 独立审查保存后果并执行成本实验。
核心实现者负责共享对象生命周期；subagent 分别承担身份/失败审查和可重复测量。

| 场景 | 最小可观察结果 |
|---|---|
| mutable owner + 多个 true 叶，同点连续 fork | 第二次叶 Allocate/Hydrate 为零，owner 独立且可各自续写 |
| S0/E1/E2 起点共用最近 S0 | 叶可以命中同一表，三个工作副本仍保留各自选中的精确 Head；不准备/物化 Event |
| 另一分支叶表已存在，本次 Fork 的 Event 前缀尚无 State | State=null、零叶查找/物化；不复核未请求图的证书，不清除旧表；该图独有 Schema 冲突不影响无 State 请求 |
| 首 State 或异型 State，包含旧 revision 的同 ID/head 叶 | 热提交本身不携带实例；新材料首次成功物化才建立新表，不继承旧表/证书；后续同材料请求才命中 |
| binary / 强制 Family | 实际 binding 为 true 且均命中；至少一个 false 可变模型始终不共享 |
| 同值不同 ID、alias、循环、mutable 集合 | 不合并不同身份；图内别名/环保持；集合独立 |
| 全 immutable root、空 immutable 类 | 可以复用根，仍正确导入身份、独立分支工作副本、Parent 与增量保存 |
| 另一单图准备材料 / 后继 revision / 新开 repo | 不误用旧表，不跨寿命复用实例 |
| 叶成功后 mutable Hydrate 失败；工作副本准备或 Fork 发布失败 | 无新叶表安装、无半图；下一次仍重新物化未成功入表的叶，发布结果按 outcome/fault 处理 |
| 已有叶表后的失败 / allocator 返回表内实例给另一 ID | 不再次 Hydrate 已交付叶；冲突在污染发生前拒绝 |
| ReadCheckpoint/Event/Pair 与 fork 交替 | 读取不填叶表；Checkpoint 可变隔离与 Pair 自身共享合同保持 |
| 叶首次物化新增标准 Schema 依赖，之后注册冲突 | 复用叶前仍拒绝，不因免 Allocate/Hydrate 丢掉依赖复核 |
| 升级后的新 State 消除旧中间依赖 | 新材料不继承旧叶表/证书；迟注册的旧依赖冲突不使热路径额外失败，实际新依赖冲突仍拒绝 |
| 命中后保存、Upgrade 首存、Remove、冷重开 | 完整来源/ID/cursor/rewrite 与关闭实例复用时相同 |
| 淘汰、Dispose/fault | 缓存引用按材料寿命释放；不宣称对所有已交付用户图实行内存上限 |

基础证据：[GraphReader](../../src/DurableGraph.Persistence/GraphReader.cs)、
[RevisionReadSession](../../src/DurableGraph.Persistence/RevisionReadSession.cs)、
[StateModelBinding](../../src/DurableGraph/Runtime/Binding/StateModelBinding.cs)、
[DB-075 验收](0075-immutable-leaf-implementation-work-order.md)。不重复重写整套生成分类器测试；保留其回归即可。

## 6. 启用或停止的判据

A/B 两边都启用 DB-080 的 State 准备材料复用，只切换本片实例复用，避免把上片收益计到本片。
覆盖叶占优同点扇出、全 mutable、容器/循环、纯叶根、深链低复用；DB-081 已实施时加热提交后 fork；普通/Family 都须出现。
报告完整 named Fork 总耗时、物化耗时、Allocate/Hydrate 次数、托管分配、峰值与缓存驻留规模。
发布屏障可能盖过物化收益；不得用命中率或计数下降替代总成本结论，也不预定百分比加速承诺。

功能全部通过，目标工作负载出现可重复的总成本收益，且全 mutable/低复用路径没有不可接受的代价，才建议保留默认接线；
记录原始比较和取舍。若证据噪声过大或无净收益，以“实验完成、暂不启用/撤回”结案。
内部 A/B 开关不自动成为公共 options；不得为凑收益扩成 weak cache、跨图版本关联或 DeepImmutable。
按 [共通验收](0076-efficient-graph-fork-technical-path.md#10-分片施工导航) 完成回归与代表性真实包。
DB-073/074 的后续取舍由实际瓶颈再决定，本片不以更新编号为由重复实现缓存体系。
