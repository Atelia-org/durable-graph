# DB-049：List 区间 Delta 与匹配算法对照施工方案

> 文档整理：2026-10-14；来源为 Git `8ef9fca` 中的 `docs/design-branches/0049-list-range-delta-and-matcher-trial-slice.md`，正文保留当片合同与历史验收含义。
> 阅读范围：统一 codec 2、三 writer 与独立历史重放证据见 §8；LocalResync 默认保当时选择理由，当前默认 Adaptive 由 DB-051 承接。
> 当前能力与施工焦点见 [PROJECT-STATE](../../../src/PROJECT-STATE.md)；旧稿中的“下一步”不自动成为当前工作单。

> 状态：**Implemented / G0–G4 已通过验收**，2026-09-09。实现与验证证据见 §8。
> 前置：[DB-048 调研](../0048-list-delta-algorithm-research/README.md)；实现基线为 `fac4981` 的 List codec 1。
> 用户本轮明确：优先保存开销与 Delta 尺寸；冷读性能是最低优先级；允许并存少量算法，重放相同领域编辑历史到独立 Repository 比较。

## 1. 本片要回答的问题与范围

为完整受支持的 `List<T>` 元素闭包提供紧凑插删 Delta，在同一持久语法下比较两种匹配器：
公共前后缀＋有界局部重同步，以及公共前后缀＋有界 Myers。位置匹配保留为实验对照。
不要求用户提供 key/comparer，不换 tracking 容器，不实现哈希匹配、业务相似度或全局最小字节脚本。

最小验收：

- LocalResync/BoundedMyers 对其余元素未变的单个头插/中插/删除，只描述新值和旧区间，不重写未变长后缀；
  Position 作为位置基线允许后缀改写。三种 writer 对 struct 少量字段变化均保留子 Delta。
- 三个 writer 共用一套 reader；换算法不改变 Schema/RepresentationId，不影响旧 writer 产生的同版数据读取。
- 任意候选与预算回退均精确恢复、准确 NoChange，prior/candidate 不变。
- 相同领域编辑历史在独立数据目录重放，交付生成耗时/分配、实际写入与候选质量报告；不以缺少唯一性能赢家视为功能失败。

本片形成一个可继续选型的产品实现和轻量实验，不必在编码前押注最终算法。
默认先选 LocalResync 便于使用，不将临时默认宣传为性能结论；实验完成后可据证据调整。

## 2. 元素比较：无分配的静态持久状态相等

### 2.1 .NET 能提供什么

`MemoryMarshal.AsBytes`＋byte span `SequenceEqual` 可以不分配比较一段值类型存储。
它比较的是原始内存，包含布局细节，不会自动忽略 padding；因此不是任意 struct 的持久字段相等函数。
[AsBytes 官方文档](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.memorymarshal.asbytes?view=net-10.0)
说明该操作是原始二进制投影；[.NET 10 ValueType 实现](https://github.com/dotnet/runtime/blob/v10.0.0/src/coreclr/System.Private.CoreLib/src/System/ValueType.cs)
也只在满足紧密布局等条件时选择按字节比较，而非对任意 struct 使用该快路。

例如含 `byte` 和 `int` 的 struct 可能有 padding；字段相同不能据此推断所有 padding 字节相同。
`EqualityComparer<T>.Default`/领域 Equals 也不能替代：默认浮点相等不保留本项目需要区分的 ±0 和 NaN bit pattern。
把 DTO 强制 Pack=1 会引入布局、对齐和泛型复合保证的额外问题，本片不为省比较代码而改变 DTO 内存布局。

我们拥有 SG 和完整字段语义，故直接采用静态逐字段比较，无需先把元素编码成临时 Base bytes。
这里“无分配”指正常匹配路径不装箱、不创建缓冲/委托；比较仍随访问的字段数消耗 CPU，不能称零成本。

### 2.2 接口与生成代码

在现有 Runtime `IStateOps<TState>` 增加一个能力（命名以此为施工基准）：

```csharp
static abstract bool StateEquals(
    in TState left, in TState right, DurableFieldInfo slot);
```

规则：

- 整数、bool、char 比较值；Half/Single/Double 转为相应整数 bit pattern 后比较。
- 引用槽已经在 Capture 阶段变为 ObjectId，直接比较 ID；不读取领域引用或调用 string 内容相等。
  这延续引用身份语义及既有 Empty 归一化，List 的 diff 工作于 DTO 而非可变领域列表。
- inline/generic DTO 逐持久字段比较并可短路；已知叶子静态绑定，未知值槽经 `TOps.StateEquals` 静态约束调用。
- 空持久 struct 恒相等；忽略 padding、Transient 和领域 Equals。所有比较均在已确认的同 exact 槽下进行。
- generic 与传统 inline 生成路径共用各自已有的标量比较生成规则；避免重新维护一张独立的类型语义表。

正常匹配不调用 `PrepareDelta`。匹配完成后，对真正需要子 Patch 的 source/target pair 调用它，取得 HasChanges 和 owned bytes，
继续复用结果写入最终 List Delta。不新增一次 size estimation，也不在策略选中后重新编码。
被判断不同的 pair 若 PrepareDelta 返回无变化，视为能力实现不一致并拒绝；测试同时检查另一方向。

需验证 `StateEquals(a,b) == !PrepareDelta(a,b).HasChanges`；覆盖各标量、ObjectId、NaN/±0、嵌套/generic DTO、空 struct、
具有 padding 的 fixture，以及 Encode→Read 后的值。Base bytes 可作为独立测试参照，不进入生产匹配的预处理管线。
不调用整段 StateEquals 后又让原有 owner PrepareDelta 无条件重扫所有 owner；本片新增能力主要服务 List 匹配。

## 3. 一种持久语法，多种写入算法

匹配器决定“找到哪些可复用区间”，codec 决定“这些区间如何解码”。三种匹配器输出相同语法，
因此 **algorithm 不进入 payload、ListLayout 或 SchemaStore**。实验报告记录 writer 的算法和参数即可。

延续当前版本寻址：ListLayout.CodecVersion 通过 Base 的 RepresentationId 查得；Delta 沿终止 Base 继承。
不重复在每个 List body 写算法标签。若未来真的比较两种不兼容语法，才分别分配解释版本，而不是让 reader 猜 writer。

本片将 List codec 从 1 升到 **2**，只写/读 2；当前无部署数据，旧 1 明确拒绝，不建立兼容 reader 或迁移。
Base body 仍是 `count + element Base...`；变化在 Delta 语法。
history 的 `l(element)`、Schema 模板和 Base v4 envelope 不变；同片 fresh fixture 统一使用新 codec。

### 3.1 codec 2 Delta 语法

所有整数使用现有 canonical UInt32 编码；opcode 用单字节，先不合并 opcode/长度位。

```text
newCount
按输出顺序重复指令，直到输出恰好达到 newCount：
    1 Copy        oldStart, count
    2 New         count, count 个 element Base body
    3 CopyAndPatch oldStart, count,
                   (localIndexPlusOne, element Delta body)*, 0
```

语义为 immutable prior 上的 source 区间＋顺序输出：

- Copy 将旧区间复制到当前输出位置；New 按序读入新元素。
- CopyAndPatch 先复制旧区间，再对本输出区间的 local index 应用子 Delta。
  每个子 Delta 相对于该区间所选 source 元素；索引严格递增、范围内，每项必须实际改变。
- CopyAndPatch 至少包含一项 patch；没有 patch 使用 Copy。索引编码为局部零基索引＋1，0 终止。
- 每个指令 count > 0；oldStart/count/output 的 checked 范围成立；所有输出指令恰好填满 newCount。
- source 允许重复、重叠或倒序；它始终只读，不存在 target-copy 或跨另一 ObjectVersion 的 source。
- newCount=0 时无指令；输出填满后仍有数据则拒绝。非法 opcode、截断、非 canonical、溢出、越界和重复 patch 拒绝。
  最后一条 CopyAndPatch 仍须完整读取其子 patch 列表与终止 0 后才能结束；opcode 0 不是外层终止码。
- NewRange 的最小元素 Base 字节预检只针对 New 的元素；**不能用 newCount 对整个 Delta 做该预检**，Copy 可用很少字节重建长区间。
  零字节元素沿用已有 Count/CLR 上限规则，总分配预算不在本片另建。
- 输出完整 DTO 后沿用现有引用校验；decoder 不证明指令最优或等于某个 writer 的输出。

稀疏更新可以由一个 CopyAndPatch 覆盖整个共同长度，减少逐个修改拆指令的头部开销。
删除是不输出旧片段；搬移/重复复制可由区间复用表达，不需要单独的 Delete/Move/Run 指令。

## 4. 匹配器与共同编码阶段

Runtime 内的简单 enum：`ListDeltaAlgorithm { Position, LocalResync, BoundedMyers }`。
不引入 plugin/provider 注册协议。匹配器输出有序的旧/新坐标匹配区间及未解决 gap，随后进入共同编码阶段。

### 4.1 共同规则

- LocalResync/Myers 先剥离公共前后缀；前后缀不得重叠。空区间、纯增删直接处理。
- 同 Count 且所有对应元素相等时精确 NoChange；匹配预算耗尽不允许直接标 changed。
- 未解决 gap 按相对位置配对 `min(oldLength,newLength)`，新侧余量写 New，旧侧余量省略。
- 相邻配对/精确匹配区间若 source 和 target 均连续，先合并为复用段；不能跨 New、source 跳跃/重叠/倒序机械合并。
- 复用段内只对不相等 pair 准备子 Delta，形成稀疏 patch；没有变化发 Copy，否则发 CopyAndPatch。
  可携带已证明的匹配信息避免重复比较，但不建设全体 pair 的缓存表。
- 首版不为每个复用段再生成一份完整 New payload 与之竞价；记录实际 List Delta 长度，由原策略进行整体 Base/Delta 选择。
  这是为控制施工与额外编码成本作出的范围选择；不承诺逐段最小字节。局部选码优化待结果显示必要再做。
- Position 直接按共同位置配对、尾部 New；它使用同一 codec 2，是公平 matcher 对照。它不是旧 codec 1 reader。

### 4.2 LocalResync

失配后在有限 old/new lookahead 内找相等锚点；两个偏移分别在 `[0,32]`（含端点）内且不得越过未解决区间，排除同时为 0。
按 `(oldOffset + newOffset, oldOffset, newOffset)` 升序选择第一个相等锚点；
记录 gap 后延伸连续相等段，再继续。未找到锚点时按位置推进；预算耗尽则整个未解决 gap 回退。
任一循环都必须推进坐标或结束；重复值可以影响压缩质量，不能影响正确性。

### 4.3 BoundedMyers

在已剥离的中间区间执行有界 Myers，固定 insert/delete 平局选择；恢复 trace 得到单调匹配区间。
采用标准 furthest-x frontier：边界以外，仅当 `V[k-1] < V[k+1]` 时选 insertion，否则选 deletion；traceback 使用同一规则。
空侧和明显超过编辑深度范围的情况直接走 gap 回退；比较、深度或 trace 空间耗尽后可丢弃该区域搜索结果并按位置回退。
不要求恢复部分最优解，不加入线性空间高级变体；额外空间和回退控制优先清楚可验证。

### 4.4 初始实验预算

施工先使用内部常量：Local lookahead 32；Myers 最大编辑深度 128、trace 工作区上限 1 MiB；
每对象额外匹配比较预算 `min(1_000_000, 4096 + 8 * (oldCount + newCount))`，运算用 long。
这些是可调整的初始实验设置，不是性能承诺或长期公共 API；报告中必须记录实际值。
比较预算计入 local 每次锚点验证和 Myers 每次 snake 相等查询，不只计命中或 frontier 层数。
trace 只保存各层活跃对角线，不按输入总长度复制每层数组；1 MiB 是 trace 的保底上限，不冒充整个方法的总 scratch 上限。

必需的前后缀扫描、精确 NoChange、gap 回退与最终编码是基础成本，额外匹配预算在其上生效。
StateEquals 可能访问多个字段，因此比较次数不是整个 Diff 的字节工作量或 wall-clock 上限；大 DTO 单独测量。
不以机器负载相关 timeout 决定正常产品输出。

## 5. 实验配置的最小接线

建议采用实例配置，而非全局静态可变开关；现有 Registry→Snapshot 已有天然传递路径：

```csharp
models.UseListDeltaAlgorithm(ListDeltaAlgorithm.LocalResync);
// 登记模型，随后用该 registry 创建/加载 GraphSession。
```

- enum 位于 Runtime；StateModelRegistry 持有选择，Snapshot 复制只读值。
- `StateModelSnapshot.Lists` 创建 ListObjectBinding 时传入算法；binding 内只读，直接低层工厂可用 optional 参数选择，默认一致。
- 已创建/加载的 WorldWorkspace 保存自己的 snapshot，现有 session 不因 registry 再配置而改变。
  不替换同一会话已有 binding，避免破坏 previous/current preparation 的引用一致性检查。
- 后续新建/重新加载使用新选择；reader 无需传算法。切换 writer 后仍可读取前一算法的 codec 2 链。
- 非法 enum 立即拒绝；不增加算法到 TypeExpr、SchemaKey、ListLayout、RepresentationId 或 Upgrade 路由。
- probe 的命令行参数映射上述 enum；无需把环境变量读取或 CLI 逻辑放入产品 Runtime。

该配置只选择写法，不授予并发 Capture、热替换 session 或跨 Repository 身份能力。

## 6. 可重复的领域编辑历史重放

新增轻量 `experiments/ListDeltaReplayProbe`，使用当前产品代码，在本仓库中维护；不必另建 Git 仓库或复制产品实现。
每个 workload/算法写独立的 DurableGraph 数据目录。流程为：

1. 在计时外生成固定种子的编辑脚本，以及相同初始图/模型登记顺序。
   脚本以领域操作和稳定的测试编号描述，不能以某次 Repository 分配出的 ObjectId 充当跨运行编辑地址。
2. 为每个算法独立创建领域图和 Repository，在同一 GraphSession 上逐条应用相同脚本并 Commit。
3. 算法枚举、内部预算、运行环境、规模/种子、X/Y、基线版本写入报告；禁止复用前一算法留下的数据目录。
4. 计时外验证各步持久结果，冷重开和指定历史读取验证内容、引用身份、顺序及 Count；读入时不提供原 writer 算法。
   另用直接 body roundtrip 验证被策略放弃的 Delta，不能因实际选 Base 而漏测该 payload。
5. 输出 CSV/JSON 和简短结论，产物放 ignored 目录；可重跑命令和解释放 README，研究状态放旁边 PROJECT-STATE。

必测素材：头/中插删、多处分散插删、尾部增删、稀疏替换/struct 字段变化、重复 ID/null、交替值、rotate/reverse、
全部 struct 微改再插入、改后撤销与 child-only 更新；小/中/大列表及不同 inline 宽度。
大规模组合按总状态大小约束，不把所有最重维度做笛卡尔积。

评价顺序：正确性与可控回退为硬条件；保存端耗时/分配与实际字节为主要结果；实现复杂度参与选型；
**冷读速度和分配最低优先级，只记录观察，不以优化冷读作为本片通过条件。**
SSD 不消除逐层物化 CPU 成本，但用户已接受该取舍，本片不新增链长策略、Frame cache 或 accumulator。

主报告包括 Commit 时间/分配、实际 Base/Delta 次数和对象字节、State/Schema/publication 文件大小，保持 X/Y 相同。
再对相同冻结状态做局部 PrepareDelta 计时以解释差异；可用测试友元访问内部实现，勿为观测引入公开事件框架。
全量 Capture/PrepareBase、策略读链、append/flush 的成本不得从整体 Commit 报告中删掉。
发布 flush 波动、JIT/首次绑定单列；Release、预热、多次重复、交错算法执行顺序，至少报告中位数与范围。
初次结果若显示瓶颈被全量阶段掩盖，保留该结果，不据此篡改计时范围或宣称 matcher 没差异。

## 7. 施工依赖与分工

| 步骤 | 修改边界 | 最小验证 |
|---|---|---|
| G0 静态相等 | Runtime IStateOps/builtin、两条 SG 值 body 路径、对应手工测试 ops | StateEquals 与 PrepareDelta 一致；浮点/ID/嵌套/历史 DTO；正常比较无 per-call 分配 |
| G1 区间格式 | ListStateBody/Reader、codec 2、共同 lowerer，先接 Position | 独立 golden、畸形 patch、准确 NoChange、输入独立；更换 codec 拒绝旧格式 |
| G2 两种 matcher | Runtime 局部算法、确定预算与回退 | 随机小序列独立 roundtrip；简单插删只写新值与区间；重复、截断、预算边界 |
| G3 配置与存储接线 | Registry/Snapshot/List binding、fresh catalog/集成 fixture | 不同配置共用 reader/表示；现有 session 配置冻结；换算法重开续写正常 |
| G4 重放与验收 | ListDeltaReplayProbe、真实包与状态文档 | 同领域历史三算法报告、cold correctness、SG 包交付；事实与性能假设分开 |

G0 与格式/算法草实现可以按文件归属并行，接口确定后集成；源文件有交叉时只设一个 owner。
主线程保留格式/接口决策、综合 diff 审查与验收。算法 review 应独立检查前后缀重叠、Myers traceback、坐标推进、
source/target 范围和预算回退，不以 benchmark 输出成功代替正确性证明。

代码落地后按仓库要求运行根 solution build 和受影响测试；更新旧测试以验证新合同，不削弱原引用/冻结/历史语义。
原位置基线“每共同位置调用一次 PrepareDelta”的断言应替换为“相等探测不准备 payload，仅实际子 Patch 准备并复用”，
并继续验证子 Delta 无重复编码。真实 List/Generic/InlineStruct 包验证新增静态能力可随历史 DTO 正确交付；其它包按改动影响运行。

完成时记录实际环境、验证与跑分证据；维护 PROJECT-STATE/roadmap/设计索引。根据结果选择临时默认，
若两算法各有优势可继续保留两者和简单配置，不强行宣布唯一赢家。

## 8. 施工验收记录

基线 `55045b4` 工作区干净，根构建通过（0 warnings/errors）。本片保持 §1 非目标；
主线程负责区间 codec、交叉接口、综合审查和串行构建/测试/跑分，子任务按文件独占写入。

| 要求 | 实施责任 | 状态 / 证据 |
|---|---|---|
| G0 静态比较、SG/history | [IStateOps](../../../src/DurableGraph/Runtime/Binding/StateValueBinding.cs)、SG 两条 body 路径 | [静态比较](../../../tests/DurableGraph.Tests/StateEqualityTests.cs)、[历史/泛型/padding](../../../tests/DurableGraph.Tests/GeneratedStateEqualityTests.cs) 通过 |
| G1 区间 codec / ListLayout | [reader/body](../../../src/DurableGraph/Runtime/Containers/ListStateReader.cs)、[ListLayout](../../../src/DurableGraph/Schema/ListLayout.cs) | [golden/坏输入](../../../tests/DurableGraph.Tests/ListBodyTests.cs)、[区间与源独立](../../../tests/DurableGraph.Tests/ListRangeDeltaTests.cs) 通过 |
| G2 Position/Local/Myers | [matcher](../../../src/DurableGraph/Runtime/Containers/ListDeltaMatcher.cs) | [独立 DP/随机/预算](../../../tests/DurableGraph.Tests/ListDeltaMatcherTests.cs) 通过 |
| G3 snapshot 配置、目录与集成 | [Registry](../../../src/DurableGraph.Persistence/StateModelRegistry.cs)、List binding | [配置冻结/跨算法续链](../../../tests/DurableGraph.Persistence.Tests/ListRepositoryTests.cs)、目录 tests 通过 |
| G4 同历史重放与报告 | [ReplayProbe](../../../experiments/ListDeltaReplayProbe/README.md) | Release smoke 及三规模三次重复矩阵通过，[结果](../../../experiments/ListDeltaReplayProbe/RESULTS.md) |
| 独立审查、完整构建/tests/真实包、文档 | 主线程与独立 reviewer | 核心及 replay 审查无剩余阻断；根 tests 1,529、五组相关真实包通过 |

根构建 0 warnings/errors；Runtime 772、StateStore 499、Serialization 103、Storage 155，均 0 failures/skips。
日志 `experiments/PackageConsumerProbe/obj/db049-tests.log`。首轮编译发现新增测试误用跨程序集 internal helper，
已改用 snapshot 的既有绑定入口，没有扩大测试程序集权限。
smoke 产物 `experiments/ListDeltaReplayProbe/obj/run-20260909082004-42552-0fccb2f7`：15 个独立测量库、225 个历史 Revision 全验证。
probe 审查另指出无内容变化仍可被策略选 Base，已修正检查以允许并正常计量；正式矩阵使用修正后的代码。

真实包均使用本片生产代码，五组为 List、Generic、InlineStruct、Array、ValueUpgrade；
包含旧领域 CLR 删除后的历史 DTO、静态泛型操作、Upgrade、同引用身份、区间 Delta 及冷重开。
八包共享 feed 为 `experiments/PackageConsumerProbe/obj/list-20260909082333-372-d4a22df9/feed`，
版本 `0.0.0-list-e2e.20260909082333.372`。对应日志为该 Probe 的 `obj/db049-package-{list,Generic,InlineStruct,Array,ValueUpgrade}.log`。

正式重放 `experiments/ListDeltaReplayProbe/obj/run-20260909082715-47256-4f0b463a`：
126 个独立测量库、1,890 个历史 Revision、另 60 次 warmup；Count 32/512/4096（wide 最大512），
每组合重复3次，每 pair 的隔离 Diff 重复5次，seed49001、X8/Y5。所有历史图和候选 roundtrip 通过。
完整重现参数、指标边界和样本表集中在 ReplayProbe 的 RESULTS，不在多个工作集重复堆积数字。

保留默认 LocalResync 与显式 Myers：14 个 workload/规模组合中11组实际对象写入相同，
Myers 在另3组分别少12/13/31字节；LocalResync 的隔离 Diff 总分配在14组均更低，Myers 在部分大列表更快。
独立结果复核支持“保留选择”而非宣布普适赢家；本轮不继续调参、加哈希或区间 New/Patch 竞价。
