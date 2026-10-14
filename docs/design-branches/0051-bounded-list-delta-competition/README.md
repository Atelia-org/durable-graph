# DB-051：完整基准与限长竞争的默认 List Delta

> 文档整理：2026-10-14；来源为 Git `8ef9fca` 中的 `docs/design-branches/0051-bounded-list-delta-competition.md`，正文保留当片合同与历史验收含义。
> 阅读范围：默认 Adaptive、严格更短竞争与两 seed 数据的有限证据见 §10/ADAPTIVE；完整基准成本仍存在，不据 body 不增大推导时间或内存不退化。
> 当前能力与施工焦点见 [PROJECT-STATE](../../../src/PROJECT-STATE.md)；旧稿中的“下一步”不自动成为当前工作单。

状态：**Implemented / G0–G5 已验收**。2026-09-09。

用户已选定主干：保留一个完整候选，第二候选边编码边检查长度，只采用严格更短者；
本轮按该合同实现默认 Adaptive；验收记录在文末，后续优化不随本片展开。

实测摘要：两 seed 各 220 frozen pairs 均为 4 更短、216 等长、0 更大；完整结果、成本和制品见
[ADAPTIVE.md](../../../experiments/ListDeltaReplayProbe/ADAPTIVE.md)。下文保留本片合同，§10 记录对应实现与验收。

## 1. 要解决的问题与验收保证

[DB-050](../0050-list-fallback-research/README.md)证明 Local/Myers 可以互补，也证明搜索完成、相等配对数增加
不能保证 Delta 更短。34 个宽 struct 的反例中，成功救援令 207 B 变成 1455 B。
用户接受退化路径多一次有界搜索和编码，以简化实现及获得精确字节选择。

新增默认 writer `ListDeltaAlgorithm.Adaptive`，使用完整 Local 结果作为基准，按停滞信号触发 Myers 竞争。
对相同的合法 frozen prior/current、同一 exact ListLayout：

```text
Adaptive.HasChanges == LocalResync.HasChanges
Adaptive.Body.Length <= LocalResync.Body.Length
Apply(Adaptive.Body, prior) == current
```

只有严格更短的完整候选可以胜出；相等保留 Local。保证针对 **raw List Delta body**，
不承诺全局最优配对、每个输入都尝试 Myers、整个 Revision/Repository 写入量或时间/峰值内存不增。
后续 Base/Delta 策略继续按原 envelope/B/D/H 规则决策。

## 2. 施工起点

- [ListDeltaMatcher](../../../src/DurableGraph/Runtime/Containers/ListDeltaMatcher.cs)已有可暂停 Local、明确 WindowMiss/BudgetExhausted、
  失败不改结果的 Myers kernel，以及独立 old/new offset。
- [ListStateBody](../../../src/DurableGraph/Runtime/Containers/ListStateReader.cs)负责 NoChange、按计划写 codec 2；
  施工前 CopyAndPatch 先写整段临时 buffer，末尾才复制到外层。
- [PreparedDeltaBody](../../../src/DurableGraph.Serialization/PreparedDeltaBody.cs)
  复制传入 bytes，拥有私有数组。原图冻结和候选生命周期无需改变。
- DB-050 的两个组合策略在 Probe，采用一份共享预算和半预算救援；**本片不直接提升这些实验协调器**。
- 施工前默认是 LocalResync；下面保留本片的 Adaptive、限长编码和默认切换合同。

## 3. 默认协调流程

### 3.1 完整 Local 基准

1. 在相同 frozen pair 上完成原有 NoChange 判定。未变化时走原 NoChange 编码，不调用 matcher 或竞争者。
2. 按显式 LocalResync 的原预算、比较顺序、合并规则，生成**完整** Local 计划。
   同时只观察停滞，不消费额外比较、不改变计划或分配第二份搜索额度。
3. 使用公共无界 encoder 完整编码 Local，得到 owned `PreparedDeltaBody incumbent`。
   真实编码错误正常传播，不能用另一个算法掩盖基准失败。

必须有可执行等价测试：Local 观察入口与显式 Local 的计划、比较次数及 bytes 相同。
可用现有 TryLocal 的暂停/消费已查配对/恢复机制；不得因记录信号而重复搜索一个窗口。
无需为“观察”引入公开接口、统计注册表或进程级开关。

### 3.2 唯一的竞争触发规则

记录完整 Local 执行期间是否发生以下任一情况：

- **完整窗口无锚点**，且该时刻至少一侧剩余长度 >33。
- **搜索预算耗尽**，并且中间区域仍有两侧元素尚未配对。

仅发生两侧都 ≤33 的完整窗口失败，不单独触发竞争：剩余笛卡尔积已经全部检查过，没有可找的锚点。
这是避免无益触发的规则，不是证明此前所有 Local 决策全局最优；重复值导致的无停滞坏匹配仍可能不触发。
为保持原比较次数，Local 仍继续原路径；若之后预算耗尽，第二条规则仍可触发一次无收益竞争。
首片接受这种保守误报，不移植 DB-050 的短尾提前结束来改变完整基准的搜索工作量。
本片不增加采样相等率、D/B 比率、literal 数量阈值或领域 key。

### 3.3 独立有界 Myers 与竞争编码

触发后才进行：

1. 从**同一完整 frozen pair** 生成另一个 Myers 计划，允许其原有公共前后缀裁剪。
   本片不固定任意 Local 前段、不抽取失败 Myers 的片段，也不把剩余区域拼成压缩数组。
2. Myers 使用自己完整的原预算 B，深度/trace 上界不变。未完成则直接交付 incumbent，
   不再调用另一遍 Local，不把失败搜索的位置回退当第三个候选。
3. 成功计划与 Local 计划逐项相同时，跳过第二次编码，交付 incumbent。
4. 否则用公共 encoder 编码 Myers，严格上限为 `incumbent.Body.Length`。
   达到或超过上限就终止并舍弃；完整结束且严格小于上限，才构造并交付新的 PreparedDeltaBody。

两个 matcher 的额外搜索各自最多
`B = min(1000000, 4096 + 8*(oldCount+newCount))`，触发路径合计最多 2B；各自首尾基础扫描另计。
**不再共享/平分 B**，避免救援消耗改变 Local 基准。此界限不包含编码期间的字段比较或子 codec 工作量。
没有无限重试、无界 Myers、计时决定结果；最多两个完整计划、一个完整基准和一个可能提前终止的竞争编码。

### 3.4 为什么第一片比较完整 body

完整 body 比较涵盖 newCount、区间头、局部 patch index 和终止符，避免单独比较某个尾段却遗漏
边界合并造成的尺寸变化。公共前后缀仍由 matcher 正常处理，但暂不缓存/拼接已编码区间 bytes。
后续只有出现证据时才复用区域编码；不能以这个优化替换本片的完整基准保证。

## 4. 先简化 writer，再实现精确截断

### 4.1 移除 CopyAndPatch 的整段临时 buffer

计划中 source 起点、长度已经固定，可以直接流式输出，保持原字节序列：

```text
rangeStarted = false
逐个比较范围内元素：
    相等：继续
    首次不同：写 CopyAndPatch(sourceStart, rangeCount)，rangeStarted = true
    写 localIndex+1
    调用原 child PrepareDelta，校验 HasChanges，再写 child.Body
范围结束：
    rangeStarted：写 0
    否则：写 Copy(sourceStart, rangeCount)
```

New 继续写区间头与元素 Base。去掉 `patches` ArrayBufferWriter 及最后一次整段复制，
不引入 backpatch、额外索引映射或另一份 codec。保留源范围重复/倒序等现有 writer 支持，
即使当前两个 matcher 只输出单调范围，也不缩减 codec 已有能力。

先通过手工 golden、已有区间 tests 和差分验证证明三个显式算法的 body 逐字节不变，再改变默认。

### 4.2 普通 Try 控制流

公共内部 encoder 同时服务无限制基准与有限制竞争，形状可采用：

```csharp
// 示意；参数/类型名可按所在文件收敛，不新增公共 API。
bool TryEncodePlan(..., int? exclusiveByteLimit, out PreparedDeltaBody? result);
```

- `null` 表示不设竞争上限。
- 有上限时，实际 `buffer.WrittenCount >= limit` 即返回 false，result 必须为空。
- 只有完整写完、严格更短，才构造 owned PreparedDeltaBody 并返回 true。
- false 只表示不能胜过基准；普通异常、错误 HasChanges、codec 不变量失败必须传播。
  不捕获一般异常并伪装为一次正常回退。
- 不输出、不缓存、不应用被截断的 body；已完成基准始终可交付。

检查实际已写长度：newCount 后、范围头后、每个 New 元素后、每个 patch index 后及 child body 后、
patch 终止符后、最终 Copy/完整结束时。前一个检查点失败后不得再访问下一个元素或调用其 codec。
短路发生后无需验证未执行的候选分支；这不是一个独立的全输入校验流程。

### 4.3 上限含义与明确不做的事

直接流式 patch 后，外层 WrittenCount 是最终 body 长度的可靠单调下界。
检查是**元素调用边界**，不是每一个嵌套字段的抢占式中断：

- 单次 child WriteBase/PrepareDelta 仍可先完成并分配较大 body，外层随后判负；
  一次子调用及 ArrayBufferWriter 扩容可以超过剩余预算。
- 不把输出长度上限说成严格的临时分配/峰值内存上限。
- 不按 `IBufferWriter.GetSpan(sizeHint)` 截断；CanonicalVarInt 会请求最大空间（如 UInt32 的 5 bytes），
  却只 Advance 实际的 1 byte。sizeHint 不是输出长度。
- 本片不创建抛异常的 bounded writer，不修改 BinaryPayloadWriter/SG/IStateOps ABI，不深入中断 struct body。

## 5. 所有权与两种表示的关系

incumbent 是原有 owned PreparedDeltaBody。竞争者仅使用局部 scratch，判负时不再 ToArray 成最终结果；
胜出时才复制到私有数组。失败 scratch 与输掉的 incumbent 可成为垃圾，胜出结果须保留到下游完成消费。
不承诺编码一个 List 返回时立即完成 GC，也不对现有 PreparedDeltaBody 增加 Dispose/池租赁协议。

允许多一份候选的短期分配；不新增元素编码缓存、池化、共享子 Delta、计数型 SG serializer。
这几项以后按 profile 再评估。PrepareBase 仍按原流程准备一次，Base/Delta 策略、SchemaStore、Storage、
对象身份和 publication 协议完全沿用；“Delta 内部竞争”不代替外层表示选择。

## 6. 默认入口与实验边界

- `ListDeltaAlgorithm` 末尾追加 `Adaptive`，保留 Position=0、LocalResync=1、BoundedMyers=2。
  三个旧名字继续表示原算法，不悄悄变成组合策略。
- 同步改变四处默认：ListStateBody.PrepareDelta、ListObjectBinding.Create、
  StateModelRegistry 字段、StateModelSnapshot 构造器。相关 test helper 的默认意图也要逐一检查。
- Adaptive 是需要真实 bytes 的 **writer 协调策略**，不是只返回坐标的 matcher。
  `ListDeltaMatcher.Plan/PlanWithBudget` 必须显式拒绝 Adaptive，不能因 Enum.IsDefined 成功而误落入 Myers 分支。
- snapshot 冻结、懒绑定、重开可换 writer 的原机制沿用；无需新配置对象、静态开关或环境变量。
- 原 internal planFactory 是实验的显式计划覆盖。有覆盖时继续一次性按该计划编码，
  不自动再包 Adaptive 竞争；默认参数变化不能悄悄改变 DB-050 的研究候选。
  可在内部拆成更明确的入口，但保留复用共同 encoder 的能力，不复制产品 codec。
- 不改 List codec 2、RepresentationId、Schema/history 版本、解码分派；reader 不需要知道谁胜出。

`ListDeltaReplayProbe` 必须区别“产品 writer 列表”和“独立 matcher 列表”：普通落盘重放及 body 验证纳入 Adaptive；
原白盒 matcher 计数仍显式限定三种独立算法，Adaptive 另报告完整 Diff/实际写入及必要的内部竞争诊断，
不能硬调用 Plan(Adaptive) 或虚构一个 matcher 数字。旧 DB-049/050、WHITEBOX/FALLBACK 的数据保持历史意义。

## 7. 必须覆盖的反证与回归

### 格式、编码与短路

- 三个显式旧算法的 golden 字节不变；首个 patch 很晚、同范围多 patch、Copy/New/CopyAndPatch 混合、
  源范围重复/倒序、127/128 varint 边界、最终终止符、零字节空 struct 都覆盖。
- 同一竞争计划无界长度为 M：上限 M+1 成功且 bytes 相同；上限 M 或 M-1 判负且无结果。
- GetSpan 请求空间大于实际 Advance 的小 varint 不被误杀。
- 测试 codec 在截断点之后被调用就抛错，证明不再访问后续元素；截断前真实异常必须传播。
- 容许一个 child 调用的超额工作，不写成“每个元素还没编码就已知其成本”的伪保证。
- NoChange 不运行两种 matcher，不制造第二份结果，HasChanges 不以竞争是否完成推导。

### 协调、默认与表示

- 用显式 Local 作基准，随机/生成 DTO 状态满足 Adaptive body≤Local，完整恢复且输入不变。
- 无触发、Myers 失败、相同计划、严格更小、相同长度、较大、第二候选提前停止等路径分别覆盖。
- 完整 Local 观察入口与旧 Local 计划/比较次数/字节相同；Myers 领取独立 B，而非 Local 剩余额度。
- 少量重构前已知的计划/次数须固定为独立预期，覆盖普通锚点、窗口失败、预算截断和短尾，
  避免两个入口共用一个改坏的 kernel 后互相证明。
- 区分触发边界：33×33 完整失败本身不触发，一侧为 34 时可以触发；窗口未查完耗尽预算走
  BudgetExhausted，不冒充无锚点证明；短尾失败后继续耗尽预算的保守误报按 §3.2 验收。
- 头插 33+改末项能选较短 Delta；分散插删各 65 保留 Local；Marker 34/48/65 一律不比 Local 差，
  尤其 207 B 不再变成 1455 B；重复值假阴性仍允许选 Local，不用改写测试来宣称全局最优。
- registry/snapshot/direct binding 默认均为 Adaptive；显式选择三种旧 writer 有效；
  已有会话不受 registry 修改影响；重开后切换不改同一布局的 RepresentationId 或 Schema 日志。
- 真实 GraphSession 连续提交、重开、共享 List、引用循环、child-only、历史 inline/泛型元素 Upgrade 继续通过。

## 8. 施工分片与子任务所有权

| 顺序 | 改动与文件入口 | 退出条件 |
|---|---|---|
| G0 | [ListStateReader](../../../src/DurableGraph/Runtime/Containers/ListStateReader.cs) 流式 patch +公共 encoder；ListBody/ListRangeDelta tests | 三种显式旧算法字节等价，无默认变化 |
| G1 | [ListDeltaMatcher](../../../src/DurableGraph/Runtime/Containers/ListDeltaMatcher.cs) 完整 Local 观察、明确 Myers 完成状态；kernel/协调 tests | 基准完全等价，停滞信号不扰动搜索 |
| G2 | 公共 encoder 的严格长度上限；新增有界编码 tests | false/null、相等截断、子调用边界和异常合同通过 |
| G3 | Runtime Adaptive 协调；enum、body/binding 默认与分派 | 不大于 Local 的性质与全部坏例子通过 |
| G4 | [StateModelRegistry](../../../src/DurableGraph.Persistence/StateModelRegistry.cs)、[StateModelSnapshot](../../../src/DurableGraph.Persistence/ModelExecutionEnvironment.cs) 默认；StateStore tests、Probe 及真实包 | 默认/冻结/同格式落盘闭环 |
| G5 | 根集成、独立审查、文档收口与提交 | 必需验证通过，无未处理缺陷或把计划写成实现 |

G1 与 G0/G2 可独立委派，但 ListStateReader 只由一个 Runtime owner 编辑；G3 在两边合同稳定后集成。
StateStore/真实包/Probe 的窄任务可随后并行编辑；根线程统一审查共享 diff，并在 Windows 串行运行 dotnet 命令。
不预先创建多程序集、通用算法插件体系或跨对象缓存。

验证至少包含：

```powershell
dotnet build DurableGraph.slnx
dotnet test DurableGraph.slnx --no-build
./experiments/PackageConsumerProbe/Run-ListProbe.ps1
./experiments/ListDeltaReplayProbe/Run-Probe.ps1
# 完成 writer/matcher 枚举适配后，按 Probe README 跑白盒及两 seed 代表性矩阵。
```

保留 DB-050 反证与生成 DTO fixtures，将 Adaptive 的实测结果另记，不覆盖旧结果。
报告完整 Diff、比较/子编码次数、累计分配、截断位置以及原有真实落盘指标。
不以时间胜出作为正确性门槛，不声称最多两倍时间/内存；本片的硬保证是内容正确与候选 bytes 不劣于基准。
只在出现新缺陷或测量疑点时扩展测试，不机械重跑全部无关 package lanes。

完工后更新 src/PROJECT-STATE、目标设计、路线图、术语表中受影响的默认/算法说明及 Probe 当前状态；
保留历史证据与后继链接。提交按用户本轮授权进行，不推送。

## 9. 审阅记录

已完成源码核对、集成入口调查及两位子代理的完整设计复核，结论为**可按 G0–G5 施工，无阻塞设计分支**。
审阅纳入：流式 patch 删除 pending buffer；实际 WrittenCount 与 GetSpan 区别；Try 失败及异常界限；
独立搜索预算与完整基准；Adaptive/pure matcher/实验覆盖的分派区别；默认入口和枚举全覆盖；
触发边界及短尾预算误报；固定重构前预期防止测试自证。后续代码仍必须通过 §7–8 的验收。

## 10. 实施与验收记录

| 关卡 | 实现与证据 |
|---|---|
| G0 / G2 | ListStateBody 的共享 TryEncodePlan 流式输出，按实际 WrittenCount 进行严格上限判断；[encoder tests](../../../tests/DurableGraph.Tests/ListBoundedEncoderTests.cs)覆盖独立 golden、全部截止点、相等/超额、异常、反序/重复源、varint 与空 struct |
| G1 | PlanLocal 暂停/消费已检查 pair/恢复，TryPlanMyers 区分完成与位置回退；[observation tests](../../../tests/DurableGraph.Tests/ListDeltaObservationTests.cs)固定旧坐标、比较顺序/次数和 33/34、预算边界，并比较随机路径 |
| G3 | [ListDeltaCompetition](../../../src/DurableGraph/Runtime/Containers/ListDeltaCompetition.cs)提供完整基准、独立预算及真实长度竞争；[协调 tests](../../../tests/DurableGraph.Tests/ListAdaptiveDeltaTests.cs)覆盖 NoChange、override、失败、同计划、严格更短、不同计划等长及随机/ID/浮点；[真实 SG Marker](../../../tests/DurableGraph.Tests/ListAdaptiveGeneratedTests.cs)覆盖嵌套 struct 反例 |
| G4 | 四处默认均为 Adaptive，枚举旧值保留；[binding tests](../../../tests/DurableGraph.Persistence.Tests/ListBindingCatalogTests.cs)与[Repository tests](../../../tests/DurableGraph.Persistence.Tests/ListRepositoryTests.cs)验证冻结、真实救援、切换旧 writer、相同表示、循环及冷重开；Probe 区分四种 writer 与三种纯 matcher |
| G5 | 根 build 零警告/错误；全量 **1612 tests**（Runtime 854、StateStore 500、Serialization 103、Storage 155）通过；独立核心/集成审查无未决问题 |

- 基线为 1545 tests；新增和扩展测试已通过。实施中发现新生成测试夹具错误调用 internal Snapshot，已把
  snapshot/Capture 操作移回测试宿主，没有放宽产品 API 或编译校验。
- 真实 `Run-ListProbe.ps1` 通过，包括 history、共享 owner Upgrade、强制 Base 后 Delta、删除历史领域 struct 和冷重开；
  制品为 `experiments/PackageConsumerProbe/obj/list-20260909110514-47428-28cec0ba`。
- 普通落盘 20 个实测仓库 / 300 Revision；白盒 120 个实测仓库 / 240 Revision；两个 seed 各 220 pairs / 2640 observations；
  原 DB-050 planFactory smoke 84 pairs / 420 observations 均通过。具体 warmup、计量边界、SHA-256 与路径见 ADAPTIVE。
- 独立审阅补入不同计划恰好 1161 B 的平局回归；Marker 34 保留 207 B，竞争者在 220 B 截止，没有交付 1455 B 候选。
- 未修改 Schema/history、表示 ID、List codec 2、reader 或外层保存/发布协议；没有池化、逐字段抢占或区域编码复用。
  完整基准的准备成本仍存在；普通轨迹测得隔离 Diff 约增加 11–14%、累计分配约增加 3.6%，不作时间/内存不退化承诺。
