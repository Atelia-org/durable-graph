# DB-076：领域对象图的高效 fork 技术路径

> 状态：**Proposed / 已按 DB-083 交叉校准的目标路线，未实施**。2026-09-27；旧机制证据基线 `343bfa6`，本轮另核对当前源码。
> 公共合同以 [DB-083 用户故事与 Checkpoint API](0083-repository-checkpoint-api-user-stories.md) 为准；本文及 DB-077–082 已按非泛型工作副本、自由 E/S 提交和按用途恢复修订，不是施工授权。
> 来源：用户要求领域图 fork 功能等效于创建盘上 fork ref 后 Load 成独立内存对象图；先正确且好用，再降低执行开销，内部尽量简单一致。
> 用户补充：项目尚未投入实用，**没有任何兼容性包袱**。可直接重设计 API、回调合同与内部结构，不保留旧接口双轨。
> 本文推荐技术路径，不冻结具体公开签名，也不是实施授权。[DB-073](0073-repository-scoped-weak-reference-cache.md) 和 [DB-074](0074-efficient-fork-deferred-directions.md) 保持待进一步修订的草稿，暂不按其旧缓存方案施工。
> DB-077–082 的顺序、硬依赖与共同验收见 [§10](#10-分片施工导航)；DB-078 分为 A/B/C 三个可分别实施验收的阶段，不将一份文档等同于一个主会话的工作量。
> [旧专项评审](0077-0078-api-dialectical-review.md) 保留当时推理与证据；current 恒等快路、每 branch 单工作副本继续采用，旧泛型/交替/PendingEvent 及迁移顺序已由本次校准替代。
> 术语遵循[项目术语表](../DurableGraph-glossary.md#branch-checkout)：拟议公开 API 使用 `BranchCheckout` / `Checkout`；当前源码 `EventHistorySession` / `Resume` 仅在事实说明中保留。

## 1. 收敛后的最小模型

**一个 opened repository 固定一份模型执行环境；每个分支工作副本拥有自己的可编辑图和保存状态；共享已经准备好的不可变恢复材料。**

```text
模型 builder ──打开时冻结──> repository 的模型环境（按需闭合 binding）

选中的已提交 Head（State 或 Event）
  → 验证逻辑历史，确定最近 State 检查点（可无）
  → State 恢复准备（Prepare）：完整读取、归一化、验证，保留保存来源
  → 已准备 State 材料（槽只按此 State 寻址，不存请求 Head）
  → Materialize：每份结果独立分配/连接可变实例、导入身份与基线
  → 非泛型 BranchCheckout：独立 State + 本次请求的精确 Head

合法历史尚无 State → 绕过图恢复和材料槽 → State/基线均无的 BranchCheckout + 精确 Head

各工作副本的提交 → 同一个串行 Repository 发布入口 → expected head / CAS
```

先用完整恢复交付 named durable fork，不要求命中任何缓存。
随后复用单份 State 准备材料；成功提交的冻结 State baseline 也是热路径候选。Event 提交不构造恢复缓存、不改变该槽。
最后按测量添加 ImmutableLeaf 实例共享。无需先完成 repository 弱缓存、DeepImmutable 或生成 deep clone。

第一版不预建公共 ForkSource/ModelContext/Workspace 类型，不引入 branch lease、强持工作副本对象的登记表或通用共享策略框架。
仅用活动分支名集合落实每 branch 一个编辑工作副本，不让同 branch 竞争提交成为额外产品模式。
当前源码的 `EventHistorySession`、WorldWorkspace、NormalizedRevision 已提供大部分职责，优先调整它们的边界。

## 2. 需求账本与证据等级

| 要求 | 来源与强度 |
|---|---|
| 功能以持久 fork ref + 独立 Load 为参照；子图可独立修改和续写 | 用户明确需求 |
| 正确、好用的 API 优先；再逐步廉价；内部尽量一致，考虑 ReadPair 统一 | 用户明确优先级 |
| 非泛型基础入口，自由 E/E/S/S，工作副本不自动恢复/执行 Event；Checkpoint 与事件查询按用途读取 | DB-083 当前目标；用户已选择非泛型入口并要求本组设计与其相容 |
| Event-first、跨类型 State、nearest 严格祖先 PreviousEvent；同打开地址与独立上游 tag | 用户已明确选择；公共合同与验收见 DB-083，tag 接入见 DB-084 |
| LLM tool-loop 树形分解/分支探索、RL rollout、反事实实验 | 用户明确用例；尚不是已有性能测量 |
| 本轮写技术路线，使用辩证评审；DB-073/074 后续再修订 | 用户本轮授权，仅文档 |
| 无兼容性包袱，可改变旧 API、callback 次数及 snapshot 寿命 | 用户最新约束，优先于历史保持项 |
| append-only、串行资源访问、同 opened repo 所有权、发布与 fault 纪律 | AGENTS 与持久化正确性，不因接口可改而取消 |
| 图内 alias/cycle、分支 mutable 隔离、ObjectId/Parent/升级保存来源正确 | fork 产品语义与当前实现/测试证据 |
| 多个可继续保存的子图同时存活；保存串行即可 | 对树形探索的推荐能力，不推导多线程或多 writer |

DB-073/074 是同源提案，不作为独立需求证据。当前源码/测试证明机制和修改面，
不把已有接口或测试中的一次政策选择提升为不可改变的法律。语义改变应直接更新合同与测试，不建兼容适配层。

## 3. fork 的可观察语义

参照输入是本次打开的 repo 签发的 **CheckpointAddress**，不隐式包含源工作副本尚未提交的修改。
当前源码的朴素调用为 `CreateBranch(newName, frame)` 后 `Resume<TState>(newName, models)`；这是旧机制证据。
DB-078 的目标为 `Fork(newName, address)` 返回非泛型 BranchCheckout；只保留成功后的 ref 与独立恢复效果，不继承旧 PendingEvent 合同。
成功子图须保持：

- 所选历史的持久状态、exact 类型、图内共享引用和循环；分支之间 mutable 实例独立。
- 原 branch 不推进，子 branch 从所选逻辑历史续写；不按文件中物理最新位置猜 Parent。
- 每孩子有正确身份映射和保存基线，后续仍可增量保存；不是只复制 Root 后当作新图重分配所有 ID。
- State 起点恢复该 State；Event 起点沿逻辑祖先找最近 State，仅恢复它；无 State 时交付 State=null 的工作副本。不附带 Event 图，也不要求不相关 Event 的 current 模型能力。
- 子工作副本 Head 精确等于选中地址，不因 State 来自较早位置而退回。每次都可提交 Event 或 State；业务自行判断哪些消息需要处理。
- 独立 Checkpoint 读取按 DB-083 交付至多两张可变隔离图；这与工作副本只恢复 State 是不同用途，不引入 PendingEvent。

“独立”不要求所有 CLR 引用不同。真正不可变对象可以共享，但不提供跨 fork ReferenceEquals 保证。
Transient 按恢复合同分别重建，不默认复制源图运行时缓存；线程栈、外部工具调用、网络连接不属于领域状态 fork。
需要可复现随机数或外部观察时，由应用把相关状态/观察显式放入模型，不在本片建立执行器或外部副作用回滚。

**第一片推荐 named durable fork：正常返回时已有持久 ref 和可编辑分支工作副本。**
这是为获得具体可验收 API 所作的范围选择，不把用户的“功能等效”解释成所有未来内存分叉都必须立即写 ref。
无 ref 的短命探索、未提交 live graph 的分叉、分支合并均另作明确产品选择。

## 4. 先简化两项基础合同

### 4.1 每个 branch 一个编辑工作副本，串行发布

将当前源码 `_activeSession` 的全 repo 排他范围缩小到 branch：**不同 branch 可以并存，每 branch 至多一个活动编辑工作副本**。
需要多份可保存候选时 Fork 到新名字；只浏览/比较用独立 Read 或 ReadPair。
源工作副本保持打开时可继续 Fork；同 branch 第二次 `Checkout` 在恢复前拒绝，Move 活动目标前先 Dispose 该 branch 的工作副本。
其他 branch 不受该限制影响，不提供正常的同 branch 竞争编辑、stale 恢复或旧工作副本的 ABA 复活模式。

内部用一份 ordinal 活动分支名集合，不持有工作副本对象；登记在 `_busy` 内并在可能发布前完成分配，
未交付失败只清理本次取得的标记，Dispose 幂等释放。完整生命周期见 [DB-078](0078-editable-checkpoint-fork-slice.md)。
该集合只是使用规则，不是第二个 head 权威，也不是持久租约。
当前源码 [EventHistorySession](../../src/DurableGraph.Persistence/EventHistorySession.cs) 的 owner/head/workspace/disposed 状态继续使用；
[Commit](../../src/DurableGraph.Persistence/EventHistoryRepository.cs) 的提交前 exact head、State baseline、合法角色、
资源与防重入检查以及最终 CAS 保留；**交替约束必须移除**，最近 State 与冷开校验按自由历史重写。
每次 Journal Parent 取当前精确 Head，首次创建才为空；图保存 Parent 取最近已提交 State，没有 State 时为空。
因此 Event-only 前缀与首 State 的图 Parent 都为空，而首 State 的 Journal Parent 仍可为前一个 Event；完整规则见 DB-078-A。
发生资源 fault 时所有工作副本都停止 Store 操作，repo Dispose 关闭资源并清集合。

初评曾因省掉登记表而推荐同 branch 乐观多 session；这是被否决意见的原词，用户随后质疑其用途。
没有找到不可由 named fork/只读浏览满足的消费者，故改用小型内部集合，减少公开竞争状态和配套异常。
这不影响不同 branch 对同一检查点材料的复用；将来有真正的竞争编辑用例再重新设计。

### 4.2 每个 opened repo 固定一份模型执行环境

推荐打开/创建 repository 时消费并冻结 model builder；Read、`Checkout`、Fork、Commit 使用这份环境，
不再默认按调用传入不同 registry。公开签名待具体分片设计，不保留两种目录寿命的兼容双轨。
冻结的是配置和语义；泛型/历史 binding 仍按需闭合，不在 Open 时穷举所有 CLR 构造类型。
由此普通/Family binding 都能在环境内稳定复用，identity 不再随每次读取变化。

新 callback 合同必须明确，不能只复制 delegate 引用后声称外部状态已被冻结：

| 操作 | 推荐合同 |
|---|---|
| Normalize/Upgrade | 结果由完整显式输入与固定环境决定，不依赖时间、随机数或可变全局状态；不修改输入 DTO |
| current 归一化 | **exact current DTO 的 Normalize 恒等**；历史 Upgrade 转为 canonical current DTO，Capture 产出 canonical current DTO |
| reader、VisitReferences、比较和 binding factory | 固定输入对应稳定语义；成功闭合可 memoize；不通过调用次数或环境开关改变字段/边/布局 |
| Dictionary comparer/resolver | 在所属环境内固定相等性/hash 语义；同一对象引用并不自动保证此条件；解析策略固定、成功结果可复用 |
| Allocate | 为需要独立恢复的 mutable 对象提供新的 exact 实例 |
| Hydrate | 只填充本次目标；不修改共享 DTO、其他已交付图，不泄露未完成图；其状态来自 DTO/已选引用表/固定投影 |

不承诺 callback 次数；诊断计数可以测量执行，但不应决定业务结果。
业务动作、随机探索、工具调用和视图专属初始化放在物化之后。错误仍传播，不引入通用运行时纯度检测器；
自定义 binding/comparer 的稳定性由提供者承担，生成路径须自然满足合同。

**纯函数还不够。** `Normalize(current x) = x + 1` 完全确定，却会让热保存基线为 5、冷 Load 为 6。
current 恒等消除这项差异，避免永久维护“保存 current”和“读取 current”两种语义。
恒等指完整持久表示与对象身份不变，不要求 DTO 包装对象的 CLR 引用相同。
这是推荐的新合同，不是声称当前代码已经机械保证。
[DB-077](0077-repository-model-environment-slice.md) 的专项复核进一步将 durable exact-current 分支收束为库内快路，
历史委托不再处理 current；保留标准 context/live Schema 验证，容器路径不随之广域短路。

当前确有同 repo 切换精简/完整目录的[真包见证](../../experiments/PackageConsumerProbe/EventHistoryConsumer/Program.cs)，
但未发现同一类型需要两套相冲解释的真实消费者。可改用固定完整目录验证事件局部恢复，
另用精简目录打开验证 Event 读取不要求 State 能力；目录完整不等于 eagerly 恢复所有类型。
同 repo 多模型解释/热替换待真实用例出现再设计显式 context。

## 5. 一个恢复核：恢复准备（Prepare）与物化（Materialize）

这两个名字表示内部职责，不预先要求新增同名公共类型。

**恢复准备（Prepare）** 从固定已提交检查点和模型环境取得完整、owned、已验证的 current 恢复材料。
利用 [NormalizedRevision](../../src/DurableGraph.Persistence/NormalizedRevision.cs) 和 GraphReader 现有 selection（单图准备材料），
不要再造一份只有 reachable DTO、缺保存来源的 fork snapshot。

| 可共享的准备材料 | 每份子图独有的状态 |
|---|---|
| 完整 source membership、current DTO、SourceLayout、RequiresRewrite | mutable 领域实例及本图 ObjectReadTable |
| exact Revision、每对象 head/H、同 Store/SchemaStore 来源 | 实例→ObjectId 映射、CaptureSession、分配游标 |
| 所选根/角色、可达顺序、固定 binding | 当前根、branch expected head、pending save/候选 |
| 准备时依赖的完整 exact Schema requirement 集合 | 后续保存安装的自身新 baseline |

Upgrade 切断最后引用后，不可达旧行仍属于完整 source；丢掉它会破坏首次保存 Remove、源校验或 ID 游标。
各孩子从完整源 membership 的 max ID + 1 初始化独立游标；兄弟分支可以各自分配相同数值的新 ObjectId，
其含义由各自 revision/history 限定。不引入 repository 全局 ObjectId 分配器；跨版本复用不能只按 ID 寻址。

每次请求先独立验证所选 Head 并解析最近 State。Fork/Checkout 只准备该 State；
`S0 → E1 → E2` 的三种选点可复用同一 S0 材料，但各自的 Head 与分支发布位置不能从缓存推断。
只有确实请求 Event 内容或上下文时，ReadEvent / ReadCheckpoint 才准备对应图；它们不消费 DB-080 的驻留槽。
尚无 State 时建空保存工作区，不造空图材料，不查用、验证、替换或清除其他分支的槽与叶表。
已有 State 的损坏/模型缺失仍失败，不能退回更旧 State 或转换成 null。首 State 成功提交才建立保存基线与可能的热材料。

**Materialize** 为每份结果建立身份表，先完成全部必要 Allocate/登记，再 Hydrate；
按用途交付独立历史图、显式只读 Pair，或导入可保存的工作副本。共享对象也必须进入本图身份检查/映射，绝不再次 Hydrate。
保留 exact type、图内别名/循环、singleton allocator 拒绝和 string/Empty 身份规则。

### 5.1 一次验证的有效范围

同 Store lifetime、固定 revision/root、owned immutable DTO 与合规固定模型环境下，
恢复准备的解码、Normalize、完整 membership/reference 验证和可达分析可复用，不必每个孩子重扫相同内容。
逐对象版本命中不能替代另一个 revision 的完整验证；更换所恢复的 State revision/root 必须取得相应完整准备结果。
请求 Head 改变但最近 State 相同时可以复用同一份材料，仍逐次验证历史导航，不从缓存选择 Head。

每次使用准备材料仍须检查资源可用性、repo/source 身份、实际根及模型资格，以及 **live SchemaStore 新增权威定义**。
复用现有 [exact requirements 检查](../../src/DurableGraph/Runtime/Binding/StateBindingContext.cs)，
材料必须保留实际依赖，包括 [Upgrade](../../src/DurableGraph/Runtime/Binding/StateBindingContext.Upgrade.cs) 的中间版本、
base/inline/Nullable、容器与声明的 value-upgrade 依赖，不能只看 stored/current 两端。

反例：v1→v2→v3 中只有 v2 用到 `Intermediate v5`；恢复准备后该 key 被注册为冲突布局。
跳过 Normalize 后若也丢掉中间依赖复核，就绕过了原本应报告的 Schema 冲突。
无需新增 schema epoch、全局失效广播或第二套权威目录；初版直接复核有限依赖集合。
保存仍保持 [LoadedRevisionPlanner](../../src/DurableGraph.Persistence/LoadedRevisionPlanner.cs) 的 exact Parent、完整 head/来源检查。

### 5.2 热提交材料也可进入同一接缝

current 恒等合同成立后，成功发布并安装的冻结 State baseline 可作为后续已提交 checkpoint 的准备材料，
省去热点路径“刚写入又重新读取”。只能取**已提交的 DTO**，不能拿 live State 的未提交修改当作该 checkpoint。
所需根/可达信息和 Schema 依赖仍须齐全；安装 baseline 不自动等于所有准备步骤已完成。

Event 提交不触碰 State 材料槽，也不生成供 Fork 使用的 Event DTO。若最近 State 仍在槽中，从后继 Event 分叉即可命中；
槽已被其他分支替换时允许冷恢复，不为“每次 Event 后都热命中”新增第二份材料或永久证书。
每个孩子成功保存后安装自己的后继 baseline，不修改其他孩子共同引用的旧材料。
准备证书用于复核被省去的恢复步骤，随槽保留；工作副本只导入保存所需的完整来源，不为未来恢复永久携带旧 Upgrade 的依赖。
新 State 的热材料须形成对应新 revision 的证书，不把旧历史升级路径或整个 Commit 的依赖无条件并入。
缺少热路径证明时不入槽、以后冷恢复；实际验证错误仍传播。完整反例和准入门见 DB-081。

## 6. named fork 的交付与失败边界

推荐顺序：检查 repo/名字/源检查点及最近 State → 私下完整恢复所需 State（无 State 时准备空保存工作区）→ 准备工作副本与交付所需内存
→ 创建持久 ref → 执行无用户 callback 的安装/交付。
它与朴素 ref+Load 的成功结果一致，并把可预见的恢复失败移到发布之前。

这不是持久化和内存返回的原子事务。沿用 [GraphCommitOutcome](../../src/DurableGraph.Persistence/GraphCommitException.cs)
对 NotPublished/Unknown/Published 与 IsFaulted 的区分：

- 恢复/校验失败：没有本次新 ref，不交付半图；不回滚用户回调的外部副作用。
- ref 发布失败或结果未知：不删已发布数据，不自动重试创建；遵循资源 fault 与重新打开查分支的流程。
- ref 已发布但返回前失败：分支可能已存在，不能把异常解释为“什么都没发生”。

保留原 ref-only mutation 的持久纪律，不另建 fork 日志或跨内存/磁盘二阶段事务。
仅完整成功的恢复准备材料可驻留；未完成的领域实例不得作为后续共享结果泄露。

## 7. ReadPair 统一到哪里

统一准备材料、身份登记、两阶段物化与交付检查；共享资格继续由用途决定：

| 使用方式 | 领域实例共享依据 |
|---|---|
| 独立读取 / ReadCheckpoint / 首版可编辑 fork | mutable 实例独立；Checkpoint 的当前图与 PreviousX 也独立，string 身份规则保持 |
| 只读 ReadPair | 可复用经 current/版本及引用闭包证明的节点，包括类型本身 mutable 的对象 |
| 优化后的可编辑 fork | 只复用已证明 immutable 的实例/闭包；内容相同不授予 mutable 共享资格 |

物化器只接受内部可信规划产生的复用映射，仍核对目标身份/类型；不提供公共任意共享策略接口。
ReadPair 两份图完整成功才交付；ReadCheckpoint 使用独立规划，不能借 Pair 的共享结果实现默认便利读取。
第一段可保留当前 `FindSharedClosure/HasSameCurrentState`，先合并重复的 Allocate/Hydrate 接缝。

在新纯合同下，同环境、同 id/head 的 current 值一致性有更直接的来源证明；
以后可单独验证能否移除重复值比较或 `RequiresRewrite` 的保守排除，不与首个 fork API 捆绑。
**引用闭包仍不可省**：owner 的 ObjectId 槽不变，另一个 revision 中该 ID 的目标 head 仍可能变化。

## 8. 优化顺序与停止条件

| 阶段 | 交付 | 完成判据 |
|---|---|---|
| A 正确且可用 | repo 固定模型；非泛型 API 与自由历史；按用途读取；不同 branch 各一个工作副本；named fork | E/E/S/S、State/Event 起点、固定历史查询、A/B 分支独立续写、冷重开及失败行为正确；零缓存也通过 |
| B 重用恢复材料 | 内部恢复准备/物化与完整来源统一；按需求驻留准备结果/热提交材料 | 命中免除已证明可省的解码/归一化/结构扫描，cold/warm 功能一致，live Schema 冲突不被绕过 |
| C 选择性实例共享 | 先 ImmutableLeaf，普通与 Family 在同环境下受益 | 少 Allocate/Hydrate；身份导入正确；不污染可编辑兄弟；总成本确有收益 |

这是纵向演进，不要求先进行一次覆盖所有读取代码的大重构。
不另设“先交付跨操作 exact DTO 缓存”的必经阶段；已有 Store/RevisionReadSession 缓存保留，是否进一步扩展由测量决定。
prepared image 最初可完全不驻留；引入驻留时必须有明确寿命/容量，不以 unbounded 字典冒充免费复用。
长期驻留会强持 DTO、bindings 与依赖信息，不只占几个弱引用槽。

ImmutableLeaf 可先附着准备材料按寿命保留，未必需要 repository-scoped WeakReference。
弱/强引用、容量、跨镜像复用和公开 pin 句柄属于实测后选择，不预定实现；共享叶也未必消除逐图身份表的构造成本。

```text
fork 成本 = ref 发布 + 读取/解码 + Normalize/验证 + 分配/填充 + 身份/保存基线准备
```

这个式子用于归因，不承诺加速比例。普通可编辑 C# 图需要独有 mutable 实例，立即完整交付仍有分配/连接成本。
模型当前允许直接字段访问，没有库级统一写入拦截点；透明软件 COW 需要句柄、代理、访问器或显式编辑协议等改变，暂不预设。

| 后继 | 重访条件 |
|---|---|
| DeepImmutable | 真实引用型不可变子图占主要成本；一次证明能被多次 fork 摊销，收益超过闭包验证/维护 |
| 生成 deep clone | 复用 DTO 与 Hydrate 后仍有具体瓶颈，且可以避免复制第二套持久/Transient 语义 |
| Lazy/COW、持久化集合 | eager mutable 图物化成为主要瓶颈，应用愿意改变访问模型 |
| 公共 ForkSource / pin | 调用者确实需要控制昂贵镜像的保留/释放，内部有界策略不足 |
| 多模型 context | 同 opened repo 必须用不同规则解释同一状态的真实用例 |
| 临时无 ref fork / 未提交 live fork | 分别明确发布时机，或冻结时点、ID/Parent 与后续保存语义；不能作为隐藏优化 |

基准包括普通/Family、全 mutable/叶占优/容器与循环、同点扇出与深链、热提交后立刻分叉、不同子图存活数。
分别报告发布屏障、解码、Normalize、Allocate/Hydrate、身份准备、分配量及峰值/驻留内存。
同时验证功能和总耗时，不用 binary 微基准替代 Family 接入，不用 hit 数代替实际收益。

## 9. 辩证裁决、证据与剩余选择

三个独立强模型分别审查需求、最小架构与不可删语义；主线程核对源码并组织两轮质询，未实施产品代码。
初评以 head/CAS 足以拒绝 stale 为由建议不建 branch 登记；这只证明同 branch 多 session 可实现，不能证明其产品价值。
后续用户质疑触发专项复核，最终改为每 branch 单工作副本的活动名字集合；裁决依据见上述专项评审。
两位评审分别发现纯 Normalize 的 `x+1` 反例，促成 current 恒等合同；无兼容约束使这一简化可以直接作为推荐。

| 候选 | 裁决 | 原因/删除失败 |
|---|---|---|
| 先做 WeakReference 缓存再设计 fork | delete 作为前置 | 缓存通过不能证明可编辑 fork 可用，且遗漏多工作副本生命周期 |
| 全局工作副本排他 | simplify | 缩到每 branch 一个编辑工作副本；只登记名字，不建工作副本对象表/lease；持久 head/CAS 防线保持 |
| 每操作 snapshot、兼容任意 Normalize 外部变化 | simplify | 选择 repo 固定模型与稳定/恒等合同，消除语义身份碎片；不保留旧 callback 节奏 |
| exact DTO 缓存独立迁移阶段 | defer | 无删除后的功能失败；可直接建立 current 准备材料接缝 |
| 新公共 ForkSource/ModelContext/Workspace | defer | 固定环境归 repo，状态材料先内部复用；当前无额外句柄需求 |
| 仅 reachable current DTO 的新快照 | merge | 复用 NormalizedRevision 完整来源；否则丢不可达 source、Remove、cursor 或升级重写义务 |
| 每孩子重跑固定图完整验证 | simplify | 恢复准备成功可复用；仍复核资源和 live Schema 的完整依赖 |
| 全局 ObjectId 分配器、branch epoch | delete 作为预设 | revision lineage 与 exact head 已给出身份/前沿；没有额外消费者 |
| ReadPair/fork 统一共享资格 | reject | ReadPair 的只读 mutable 共享不能用于独立编辑；统一物化机制即可 |
| Checkpoint 独立双图、发布 outcome、完整保存来源 | keep | 分别防历史事件输入污染、误判分支是否已发布、错误 Parent/Remove/增量续写；工作副本只恢复 State |

相对初稿，去掉 exact-cache 必经阶段，不新增三种公共句柄；仅增加活动分支名集合来限制编辑占用，
模型环境寿命、current 数据与保存来源各有一个维护位置。没有以兼容双轨补偿旧语义。

主要事实入口：[Repository](../../src/DurableGraph.Persistence/EventHistoryRepository.cs)、
[WorldWorkspace](../../src/DurableGraph.Persistence/WorldWorkspace.cs)、[GraphReader](../../src/DurableGraph.Persistence/GraphReader.cs)、
[StateModelSnapshot](../../src/DurableGraph.Persistence/StateModelSnapshot.cs)、
[CaptureSession](../../src/DurableGraph/Runtime/Capture/CaptureSession.cs)。
现有边界由 [EventHistoryRepositoryTests](../../tests/DurableGraph.Persistence.Tests/EventHistoryRepositoryTests.cs)、
[WorldWorkspaceStorageBaselineTests](../../tests/DurableGraph.Persistence.Tests/WorldWorkspaceStorageBaselineTests.cs)、
[GenericBindingCatalogTests](../../tests/DurableGraph.Persistence.Tests/GenericBindingCatalogTests.cs) 支持。

2026-09-23 的旧轮次针对历史 fork/Move、PendingEvent 隔离、缓存 DTO 保存来源、移除行来源拒绝、迟注册 Schema 冲突、升级重写义务，
以 `dotnet test` 的 `--no-build --no-restore` 焦点过滤重跑现有 6 个测试方法，**6/6 通过**。
方法为 `HistoricalForkAndMoveSelectLogicalParentRatherThanLatestPhysicalState`、
`PendingEventColdResumeIsolatedFromMutableStateAndPairPreservesEachView`、`CachedDecodedRowsCarryStorageIntoAnEditableLoad`、
`LoadedPlanningRejectsMissingOrForeignStorageIncludingRowsBeingRemoved`、
`CacheHitsRecheckAnAuthoritativeSchemaRegisteredAfterInitialClosure`、`LoadedUpgradeRewriteIsNotClearedByEventSaveAndLaterStatesUseDelta`。
这是既有机制的回归证据，不是新 API、多个工作副本、新纯度合同、热镜像复用或性能收益已经实现。
上述 `x+1` 与升级中间依赖情景为源码支持的设计反例；具体分片须补可执行见证。

2026-09-27 三位独立 reviewer 复核公开分片、恢复材料与关键不变量，经交叉质询得到如下校准：

| 旧方案冲突 | 裁决与决定性反例 |
|---|---|
| 077 先迁移泛型名称，后续仍无新读取/自由历史负责人 | simplify：077 只固定环境，078-A/B/C 分别交付基础、读取、多分支 Fork，避免两次无意义公开迁移 |
| Event 起点必恢复 PendingEvent | delete：只有 State 模型的工具应能从 E2 Fork，保留 E2 Head 而恢复 S0；Event 内容由显式读接口取得 |
| 完整 Head 缓存组带 State + Event | simplify：单槽只存精确 State 材料；S0/E1/E2 可同槽，Head 每请求重新选定与验证 |
| 热 Event 制造恢复材料并让工作副本长持历史证书 | delete：Event 不安装 State，也无 Event 物化消费者；槽淘汰后冷恢复即可 |
| 新 State 继承旧 Upgrade 全证书 | reject：旧 S 经含 X 的历史路径转为新 current S；X 后来冲突时，新 S 冷读不再依赖 X，热读不应凭旧证书失败 |
| 缓存/叶共享替代全 source、live Schema 与发布验证 | keep 验证：不可达高 ID、Remove、迟注册冲突及发布后失败反例仍成立 |

用户已确认 Event-first、允许跨类型 State、PreviousEvent 为最近严格祖先 Event，以及首片同打开地址、不可变 tag 上游独立交付。
078-A 负责无 State 生命周期/首 State 与实际根模型，078-B 落实 PreviousX；优化不将 PreviousEvent 用作保存基线或缓存键。
异型替换保留仍可达实例身份，已有 child 升根不分配新 ID；新 State 的恢复证书和叶表按其新 revision 准备。
tag 持久权威归上游 EventJournal，独立设计与 DG 接入条件见 [DB-084](0084-eventjournal-immutable-tags-slice.md)。不新增运行时策略模式。
无 ref/live fork 是否近期需要尚未确定，不阻止先完成有明确语义的持久检查点 fork。
DB-073/074 在这条路线被采纳后再重写，不把其保留正文当成并行施工计划。

## 10. 分片施工导航

本节按依赖顺序列出可由主会话配合有界 subagent 实施并验收的单元。
**DB-078 的 A/B/C 分别是独立施工与验收停点，不要求一次会话做完该文档**；各单元结束时产品均须可运行。
本轮仅修订设计，不承诺固定工时或 token 数，也不授予施工权限。
每片实施前读取其前置的实际完成结果和当前源码；若前片结论改变，先修订后继，不照旧计划继续累加机制。

| 推荐顺序 | 设计片 | 单片完成后得到什么 | 硬前置 |
|---|---|---|---|
| 1 | [DB-077 固定模型环境](0077-repository-model-environment-slice.md) | Open 冻结配置，移除逐操作 models 参数；current 快路/固定解释；阶段性保留现有公开名称与历史政策 | 当前 DB-075 基线 |
| 2A | [DB-078-A 公共基础与自由历史](0078-editable-checkpoint-fork-slice.md) | 非泛型入口、同打开地址、Event-first/自由历史、跨类型 State、最近 State 或无 State 的恢复、基础读/ref 操作 | DB-077 |
| 2B | [DB-078-B Checkpoint 与事件查询](0078-editable-checkpoint-fork-slice.md) | 独立稳定 Checkpoint、nearest PreviousX、Event 小读、固定逻辑范围的事件枚举 | 078-A |
| 2C | [DB-078-C 多分支与 Fork](0078-editable-checkpoint-fork-slice.md) | 每 branch 一工作副本；精确 Head 的 named Fork，所需工作副本准备成功后发布 ref | 078-A；默认排在 B 后，B 非机制前提 |
| 3 | [DB-079 共用图恢复核心](0079-shared-graph-restoration-core-slice.md) | 恢复准备/分配登记/填充共用；ReadPair 保留独立共享证明 | DB-078 |
| 4 | [DB-080 State 准备材料复用](0080-prepared-checkpoint-reuse-slice.md) | 只为 Fork/Checkout 驻留一份精确 State 准备材料；独立验证请求 Head，命中复核完整证书 | DB-079 |
| 5 | [DB-081 热 State 提交材料](0081-hot-commit-restoration-material-slice.md) | 可证明冷热等价的新 State 提交产物进入同槽；Event 不改槽，缺证明不热准入 | DB-080 可用接缝与实验结论 |
| 6 | [DB-082 ImmutableLeaf 实例复用](0082-prepared-immutable-leaf-reuse-slice.md) | 在同一单图准备材料范围内复用已完整物化的叶，按实测决定是否启用 | DB-080 已交付驻留；DB-081 非硬前置 |
| 独立 | [DB-084 EventJournal 不可变 tag](0084-eventjournal-immutable-tags-slice.md) | 上游发布能力后，DG 按新 pin 接入创建/解析 tag 与严格恢复验证 | 上游独立交付；DG 接入依赖 078-A，不依赖优化片 |

推荐按表串行推进；最后一片可在 DB-080 后独立实验，不必被热路径工程阻塞。
DB-077 与 078-A/B/C 合起来是阶段 A，**到 078-C（默认顺序下 B 也已完成）得到 DB-083 核心 API，可停下收集反馈**；
此时不可声称独立 tag 目标已经交付，DB-084 另行验收。
DB-079–081 是阶段 B，DB-082 是阶段 C；优化片允许以可靠的否定实验结论收束，不能把未启用的机制记为已交付加速。
若 DB-080 没有保留驻留接线，DB-081/082 先根据其证据修订，不视为前置已满足。

这些边界刻意分开不同风险：模型解释；公共入口与自由历史的语义迁移；读取查询；工作副本/发布语义；恢复循环重组；
完整 Schema 依赖证明；热候选 admission；跨图 CLR 实例共享。每片都在自己的终点保持可运行产品，
不能留一个“后片再补”的不安全中间默认值或要求后片才能编译。
API/合同改变同步迁移活动消费者；历史文档保留其历史，不做全仓措辞翻新。

### 共通施工与验收

- 主会话负责合同/关键不变量和最终集成；subagent 承担指定核心模块、测试或消费者迁移。
  同一个高耦合文件由一个实现者修改，交叉审查可以并行；使用子任务完成报告不能代替主会话验收。
- 各片先固化自己的最小失败/成功见证，再完成垂直交付。旧政策测试按明确的新合同更新，
  持久化/来源/防重入等不变量保留；不把所有旧测试都视作不可修改合同，也不为了绿测删掉语义覆盖。
- 每片代码完成运行 `dotnet build DurableGraph.slnx`；开发中运行相关测试，最终运行
  `dotnet test DurableGraph.slnx --no-build`。若变更生成器，构建改用 `dotnet build DurableGraph.slnx -t:Rebuild`。
  Windows 下最终 build/test 串行执行；已通过的检查不无故重复。
- DB-077 与 078-A/B/C 每个停点的公开 API/示例变更还须编译受影响的独立包消费者，并执行 EventHistory、Recovery 与 README 原文见证。
  [PackageConsumerProbe README](../../experiments/PackageConsumerProbe/README.md) 记录 feed/version 与运行方法；
  已核对入口为 `Run-EventHistoryProbe.ps1`、`Run-EventHistoryRecoveryProbe.ps1`、`Run-ReadmeQuickStartProbe.ps1`，
  最后一个要求匹配的 `-PackageSource` 和 `-Version`。不得用 ProjectReference 测试替代真实包验证。
- 后续片复用同一个 fork 消费者，加所需普通/Family 或热/冷见证，不每片发明一套消费者框架。
  性能实验用匹配的 Release 构建、内部计数及可复现数据，报告总成本和分配/驻留，功能测试不依赖计时阈值。
- 完成后记录实际证据、风险与实验裁决到所属分片，更新 PROJECT-STATE/索引/路线图；
  原有脏改动保留，`git diff --check` 与受影响链接通过。提交/push 不因设计中的施工单元而自动获得授权。

本轮只核对源码、测试入口和文档一致性，没有运行新合同的产品测试，也没有执行上述施工命令。
此前 §9 的 6 个现有测试结果仅证明其所述旧机制，不能替代六份新片未来各自的验收。
2026-09-23 的六片审阅及 129 个链接检查只描述旧版本交付，不作为本轮目标一致性的证明。
2026-09-27 前一轮对 14 份变更文档复核完整差异与活动入口，检查 756 个本地链接及 18 个其他文档的入站锚点引用，均有效；
逐文件空白检查包含未跟踪文档，`git diff --check` 通过。三方最终交叉复核发现的措辞冲突已回填，未发现残余跨片语义阻塞。
这是前一轮设计相容性复核，没有执行产品构建/测试或性能实验。本轮用户消除了上述四项产品待决，
三角色再次独立核对源码与反例、交叉质询后回填各片；无 State 不碰其他分支缓存、异型 child 升根保持 ID、
tag 的上游归属与 DG 严格确认顺序均已纳入目标。仍须由各实施阶段的实际测试证明实现正确。
本轮最终复核覆盖 17 份文档的集成差异、774 个本地链接与 20 个入站锚点引用，均通过；
空白检查覆盖全部未跟踪设计稿，`git diff --check` 通过。交叉验收发现的可空 State 示例、旧 null 根待决措辞、
tag 验收误依赖 078-C 已修正；核心分片未发现剩余设计阻塞。DB-084 只固定跨仓消费与接入合同，上游格式/接口设计仍须独立完成。
