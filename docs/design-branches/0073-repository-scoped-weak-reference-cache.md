# DB-073：Repository 作用域的 ImmutableLeaf 实例缓存

> 状态：**Draft / 尚待进一步修订，未实施**。2026-09-23，源码核对基线 `343bfa6`。
> 现行术语见[项目术语表](../DurableGraph-glossary.md)：公共分支工作副本拟为 `BranchCheckout`，签出拟为 `Checkout`；本文旧正文的 session/fork 用语保留其草稿时期含义，不据此说明现有 API。
> 用户已明确先研究领域对象图的高效 fork 技术路径，再回头修订本片。正文保留上轮分析，不是当前选定的分片或施工输入；新讨论见 [DB-076](0076-efficient-graph-fork-technical-path.md)。
> 当前逐步施工入口为 [DB-077–082 导航](0076-efficient-graph-fork-technical-path.md#10-分片施工导航)；实例共享先由 [DB-082](0082-prepared-immutable-leaf-reuse-slice.md) 提议按单图准备材料寿命实验，本片不作为并行 weak cache 工单。
> 本文替代 2026-09-21 的 Chosen 草案；保留仓库内弱引用复用方向，修正正确性前提并收窄首片。
> 前置：[DB-075](0075-immutable-leaf-proof-and-family-refactor.md) 已实施；旧 DB-072 模型程序集仍须重编译，单独升级 runtime 不修复其中内嵌的 true 常量。
> 问题：独立历史读取能否复用仍存活的不可变叶实例，同时保持每次读取的值、验证与失败行为？
> 推荐：先做 `ReadState/ReadEvent` 的有界实例缓存实验；保留完整读取与 current DTO 比较，只省 Allocate/Hydrate。
> 收益边界：稳定 concrete binding 可跨调用命中；普通 Family definitions 接入每次创建 binding，本片不解决其跨调用命中，也不宣称已实现高效 fork。

## 1. 最小模型与需求来源

```text
owner = 现有 EventHistoryRepository 实例
key   = (ObjectId, actual object head, exact StateModelBinding identity)
entry = (WeakReference<object>, 一个 owned current ObjectStateRecord)

完整 Decode → Normalize/Upgrade → 验证 → 求可达集合
→ 对叶候选查缓存并证明 current DTO 相等
→ 命中复用，未命中正常 Allocate/Hydrate
→ 整图成功后才发布新缓存项
```

这是物化实例的可选优化，不是解码、归一化或整个图的缓存。原“entry 不保存 DTO”的约束撤销：
**实例不可变不等于每次 Normalize 的输出相同**，保存单叶 current DTO 是复用既有相等证明的最小材料。

| 要求 | 来源与边界 |
|---|---|
| 审阅完善设计，可重写；本轮不实施产品 | 用户本轮明确请求 |
| 原帧 append-only；Repository 串行、拒绝重入，关闭或 fault 后不再读取 | 仓库纪律；`EventHistoryRepository.RequireAvailable` 与资源检查 |
| 每次读取完整 source/current 校验，包含不可达 source 行；错误不能变成 cache miss | `GraphReader.Prepare`、`NormalizedRevision.Create` 与现有失败回归 |
| 每图身份正确；ReadPair 原共享规则、Resume 可变隔离保留 | 当前实现与 DB-066 回归；不等于所有入口须同片接入缓存 |
| 只复用 exact 类型的 ImmutableLeaf，不扩展引用闭包、Transient 或泛型分类 | DB-075 已实施 capability；手写 true 仍由调用者承担断言责任 |
| 不改公共 API、持久格式、模型目录生命周期 | 本次推荐的分片边界，不把优化扩大成新产品合同 |
| 不承诺跨调用 ReferenceEquals，也不预先承诺业务加速 | 弱引用/淘汰允许 miss；尚无本片工作负载测量 |

运行库为 .NET 10。使用普通同步字典与弱引用，不增加并发缓存协议。
DB-072/073/074 是同一方向的设计材料，不能互相充当独立收益证据。

## 2. 原方案不能直接施工的原因

### 2.1 同一 key 不能代替 current 状态证明

[GraphReader](../../src/DurableGraph.Persistence/GraphReader.cs) 每视图执行 Normalize，
[SharedGraphReaderTests](../../tests/DurableGraph.Persistence.Tests/SharedGraphReaderTests.cs) 的
`SameLayoutNormalizationMustCompareCompleteCurrentValuesAndReferences` 已覆盖相同 head/binding/layout 下 Normalize 改值或改边。
这个现有夹具不是 ImmutableLeaf；但它证明 Normalize 没有原稿所假定的全局确定性合同。
同样的 Normalize 可以给 readonly 标量叶先后产出 20 和 21，结构资格不会因此消失。
若第二次直接复用第一次的实例，就返回错误值。

[DB-066](0066-readpair-comparison-and-transient-contract-slice.md) 与
[CapturedStatePreparation](../../src/DurableGraph/Runtime/Capture/CapturedStatePreparation.cs)
约束完整相等比较等 preparation 回调，不能据此宣称所有 Normalize/Upgrade 都可跳过。
即使保留 Normalize，只凭原 key 复用仍然错误。因此保留完整 Prepare，并比较旧 entry 与本次 current DTO。

### 2.2 Family capability 为 true，不等于 binding 跨读取稳定

[StateModelRegistry.Snapshot](../../src/DurableGraph.Persistence/StateModelRegistry.cs) 每次复制目录；
[StateModelSnapshot.TryGetCurrentModel](../../src/DurableGraph.Persistence/StateModelSnapshot.cs) 只在本 snapshot 内缓存工厂产物。
[Family 工厂](../../src/DurableGraph.Generator/DurableSchemaGenerator.GenericProjection.cs) 每次创建新的 preparation/binding，
Normalize 与 source reader 委托还引用本次 context。

因此，README 推荐的 definitions/强制 Family 注册，两次独立读取得到不同 binding，exact key 将 miss。
DB-075 修复了资格和最终类型证明，**没有改变 binding 的寿命**。普通二进制生成的静态 binding，
以及调用者显式复用的合格 concrete binding，才是首片的跨调用命中见证。
同一个 registry、相同 CLR Type/Schema/definition 名称均不能替代 exact binding identity。

也不在本片复用整个 repository 长寿命 snapshot。反例是 Application Dictionary comparer：
[registry 合同](../../src/DurableGraph.Persistence/StateModelRegistry.cs) 明确成功解析结果按 snapshot 缓存，
不冻结 resolver 捕获的外部状态；[实现](../../src/DurableGraph.Persistence/StateModelSnapshot.Dictionaries.cs) 与
[测试](../../tests/DurableGraph.Persistence.Tests/CompositeDictionaryRepositoryTests.cs)
`ApplicationRecipeCanReturnStandardComparerWithoutModeDriftAcrossReloadAndDelta` 均维持新 snapshot 独立解析。
即使 registry 配置 token 未变，沿用旧 snapshot 也会延长 comparer/factory 结果的有效期。
为让叶命中而改变整个图的目录行为，不是本片的局部优化。

### 2.3 已有 owner 足够；弱实例不等于有界元数据

[EventHistoryRepository](../../src/DurableGraph.Persistence/Repository.cs) 已用 `_identity`、
`GraphFrame.Owner` 与 `CheckFrame` 拒绝跨 repo frame，并统一管理资源、重入和 Dispose。
无需新增 `RepositoryScope`、第二个 Identity，或把独立的 `RevisionReadSession` 改造成 repo handle。

弱引用只不保活目标；字典仍强持 key、binding 与 DTO。Family binding 还可能保留 context/目录。
扫描不断变化的 head 时，dead target 不会自动删除 entry，因此简单条目上限属于首片。

## 3. 首片范围

| 入口 | 本片行为 |
|---|---|
| Repository `ReadState<TState>/ReadEvent<TEvent>` | 使用 repository 私有实例缓存；只读打开同样适用 |
| Repository `ReadPair` | 沿用原操作内 DTO/string 与引用闭包共享；不读写本缓存 |
| Repository `Resume` | 沿用原恢复及可编辑身份导入；State/PendingEvent 不读写本缓存 |
| `CreateBranch(initialState)`、热提交、Capture | 不把调用者持有的领域对象 seed 进缓存 |
| 独立 GraphReader/GraphResources/低层测试入口 | 默认无缓存；不要求构造 repository scope |
| `CreateBranch(name, frame)` / Move | 保持已有 ref 操作；不引入 fork API 或新缓存动作 |

首片只增加一个私有缓存概念及一个独立读取接缝，不重构公共 model registry。
`GraphReader` 可接收内部可选 cache 参数，但仅 Repository 的独立读取路径传入；
不能借共享的底层资源字段让 ReadPair/Resume 被动启用它。
mutable/Transient 节点仍独立分配，独立读取后应用侧 Transient 初始化合同保持。

## 4. 复用与交付规则

### 4.1 Key 和 entry

- ObjectId 与 head 使用本次实际 decoded live map 的值；head 是对象版本所在 frame 的完整地址，不是所选 Revision 地址、branch 名或最近发布位置。
- model 以引用身份比较，不能依赖其未来可能重写的值相等。
- entry 仅持有该叶 current row 和弱实例；不保留整个 NormalizedRevision、MaterializedGraph、根或引用闭包。
- `WeakReference<object>.TryGetTarget` 成功后，在操作局部变量/实例表中强持目标直到操作结束。

同 head 可以包含多个 ObjectId，同 ObjectId 可在不同分支指向不同 head，两者都不能从 key 删除。
缓存自身归 repo 所有，不另在 key 放 repo id。换 repo/reopen 即独立缓存，原 GraphFrame 跨 owner 检查保持。

### 4.2 Hit 是充分证明，不是地址相同

完整 Prepare 成功后，仅对可达且 `StateModelBinding.IsImmutableLeaf == true` 的 durable 行查询。
必须同时满足：

1. exact key 命中、entry 尚未淘汰、weak target 存活；目标为 model 的 exact DomainType。
2. 旧 row 与本次 row 的完整 current layout 一致，preparation 存在且为同一实例。
3. 现有 `ICapturedStatePreparation.ProvesSameState(old, current)` 证明完整 current DTO 相等。

无 entry、目标死亡、布局/preparation 不符或比较返回 false 均为 miss，走正常物化。
比较缺失也返回 false；真正的校验/比较异常继续传播，不 catch 后降级重试，不回退编码或重新 Capture 旧实例。
不调用领域 Equals。缓存不是来源认证器，不能补救错误的手写 true、错误 equality 或主动返回已发布对象的 allocator。

升级后的叶也可按以上规则命中；仍逐次执行 Upgrade，保留本次 source/current 验证。
无需额外复制 ReadPair 的 `RequiresRewrite` 排除规则；本片也不修改 ReadPair 原算法。

### 4.3 命中不能再次 Hydrate

保持“全部实例分配/登记完成后才开始 Hydrate”的顺序：

- hit 和新 Allocate 的实例都进入本图的 reference-equality 身份检查与 ObjectReadTable；保留 exact type 和不同 ID 不共用实例的检查。
- 单独记录命中 ID，Hydrate 循环跳过它们；mutable/noncandidate 与其他 miss 照旧恢复。
- 必须在任何 Hydrate 前拒绝“hit A 得到 X，而 miss B 的错误 allocator 也返回 X”，防止 B 改写已交付的 A。
- 不增加全 repo 的反向强引用实例表，不试图用缓存修复跨操作返回旧实例的违规 allocator。

所有新 entry 在**整个独立读取成功**后才发布；后半图 Allocate/Hydrate 失败，不留下本次新建或替换的 entry。
之前成功请求已有的项无需清空；允许删除已死亡项等无语义影响的清理。
不建立持久 pending 状态或回滚用户回调副作用。缓存更新不写任何 Store。

### 4.4 有界生命周期

首片使用内部有限正整数条目上限 C；初值是实验参数，随首轮测量记录，不增加公共配置。
最小淘汰策略为：替换已有 key 不增加数量；插入新 key 前若已达 C，先清空再插入；查询到 dead target 时删除该项。
逐项发布也必须保持 `Count <= C`，不能因为图很大而先把整批装入长寿命缓存再检查上限。

这只限制驻留条目数，不承诺总堆字节或操作峰值上限。触顶清空可能造成大图扫描反复 miss，测量须如实报告。
暂不引入 LRU、分支分区、GC 通知、后台扫描、强实例保活或精确字节预算。
Repository.Dispose 清空缓存；可用性/故障检查先于任何缓存读取。已交付对象不因清空而失效。

## 5. 最小施工与验收

先验证覆盖，再决定是否值得扩大投入。以下是未来施工验收，不是本轮已完成结果。

| 阶段 | 最小交付与判据 |
|---|---|
| G0 覆盖与基线 | 同 repo 重复独立读取：分别记录静态生成 binding 与 README 式 Family binding 的身份/资格；选定有限 C，保留首读结果以排除 GC 干扰。确认目标负载是否有可受益的稳定 leaf binding |
| G1 垂直实现 | 私有有界缓存 + 仅独立读接线 + DTO proof + 身份登记 + 延迟发布；不修改 registry/snapshot、ReadPair 或 Resume |
| G2 集成验收 | 下面的矩阵、根 solution build 与相关测试；生成正例/真包验证；记录 cold/warm 计数与分配/耗时，再决定是否扩覆盖 |

如果实际目标只有 Family definitions，G0 应得出“本片没有该路径的跨调用命中”，
随后先研究 binding/投影寿命或暂停优化；不能用 binary 微基准通过来宣称目标负载获益。
不要仅为使测试命中而更改 README 推荐注册方式或要求消费者长期持有内部 snapshot。

| 场景 | 必须观察到的结果 |
|---|---|
| 稳定 binding，同 id/head，current proof 相等，目标存活且未淘汰 | 第二次独立读复用叶；该叶新增 Allocate/Hydrate 为零；Decode/Normalize/验证仍执行 |
| 不同 id / head / exact binding | 均 miss；即使字段值相等也不跨 key intern |
| 同 key 同布局，Normalize 改 readonly 标量 | 返回本次新值，不重写原结果；归一化调用次数保持 |
| 缺 equality / equality 抛错 / Base/Delta preparer 抛错 | 正常 miss / 原错误传播 / 共享判断不调用 preparer，读取仍可成功 |
| 升级后 current 相等或不等 | Upgrade 每次执行，相等可复用，不等新建；真实错误不被掩盖 |
| 缓存已热，但另一 source 行或不可达 source 行无效 | 完整验证仍失败，不返回缓存根 |
| hit/miss allocator 别名、后半 Hydrate 失败 | 前者在 Hydrate 前拒绝；后者不发布本次新项，重试不命中失败结果 |
| mutable/Transient/string/容器 | 不进入本实例缓存；既有 string/ReadPair 行为独立保持 |
| weak target 死亡 / 容量溢出 | 正常 miss 并恢复；条目数不超过 C，不靠 GC 一定发生才能验证容量 |
| different repo / reopen / disposed / faulted | 不共享；外 repo frame 仍拒绝；无效生命周期不能经 hit 绕过检查 |
| ReadPair、Resume、独立读取的 Transient 初始化 | 原共享/隔离/续写基线回归通过；前两者不读写本缓存，独立读仍允许可变/Transient 节点初始化 |
| 强制 Family 非泛型叶 | 实际 capability 可为 true；独立调用 binding 不同，明确验证 miss |

优先复用 [SharedGraphReaderTests](../../tests/DurableGraph.Persistence.Tests/SharedGraphReaderTests.cs)、
[SharedEventHistoryTests](../../tests/DurableGraph.Persistence.Tests/SharedEventHistoryTests.cs) 与
[ImmutableLeaf 真包消费者](../../experiments/PackageConsumerProbe/ImmutableLeafConsumer/README.md)。
新增正例须证明真实生成静态 binding 命中，不能只用手写 true。
真包分别验证可命中的二进制路径和推荐 Family 路径的边界；不以 ProjectReference 代替。

计数分开记录 decode、Normalize、Allocate、Hydrate、实例 cache hit，不与现有 DTO cache hit 混淆。
报告冷/暖耗时、分配量及缓存驻留条目，说明目标是否被调用者持有、容量与模型路径。
命中减少物化不自动证明总读取更快；无实测前不设加速百分比或 fork 性能结论。
GC 测试须避免 JIT 局部根导致假失败；确定性的 dead-target 路径测试与实际弱持有性测试分开。

## 6. 辩证裁决与后继

三个独立评审分别承担需求怀疑、最小架构、语义守卫；主线程核对源码并进行两轮交叉质询。
争议在第 2 轮收敛，不以评审票数代替证据。

| 原机制/替代 | 裁决 | 理由 |
|---|---|---|
| 新 RepositoryScope、额外 Identity、全入口 owner 迁移 | delete | 现有 owner 和检查已足够；保留其职责 |
| ObjectId、实际 head、exact binding key | keep | 防止同 frame 多对象、不同版本、不同物化模型混用 |
| hit 跳 Normalize/Upgrade/StateEquals | delete | 与逐视图 Normalize 语义及失败回归冲突 |
| 禁止 entry 保存任何 DTO | simplify | 仅保留单叶 current DTO，复用既有比较 |
| 所有 repo read/fork 入口一次接入 | simplify | 首片仅独立读；ReadPair/Resume 集成暂缓 |
| 只靠 WeakReference，元数据管理全部延期 | simplify | 有限条目上限和简单淘汰；复杂策略暂缓 |
| registry token + repository 长寿命 snapshot | defer | 改动 factory/comparer 生命周期，须独立裁决 |
| Type/Schema/definition token 替换 exact binding | reject | 尚无跨 binding 物化语义等价证明 |
| 泛型/deep immutable/string/Transient、强 LRU、公开 fork | defer | 不属于当前最小机制，见 DB-074/路线图 |

与原稿相比，去掉一个 owner 类型及其迁移，接入范围从笼统 read/fork 全入口收窄到独立读；
增加不可删的单叶状态证据与条目上限。没有增加公共 API、目录 epoch 或新持久身份。
剩余的是收益选择：是否为真实 Family 负载设计稳定投影/binding，由 G0 覆盖和工作负载决定。
本片不再规定“缓存落地后必然设计公开 fork API”；后继从 [DB-074](0074-efficient-fork-deferred-directions.md) 的触发条件重访。

本轮只修改设计与导航。主线程重新构建并执行了上述两个现有见证方法的全部参数用例：
`dotnet test tests/DurableGraph.Persistence.Tests/DurableGraph.Persistence.Tests.csproj --no-restore --filter "FullyQualifiedName~SameLayoutNormalizationMustCompareCompleteCurrentValuesAndReferences|FullyQualifiedName~ApplicationRecipeCanReturnStandardComparerWithoutModeDriftAcrossReloadAndDelta"`，**5/5 通过**。
它们验证保留的 Normalize/snapshot 边界，不是尚未实施缓存的验收，也不是性能测量。
