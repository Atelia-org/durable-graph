# DB-015：StateStore 对象表示策略的估算 DTO 与保存计划

> 历史迁移（2026-10-14）：原路径 `docs/design-branches/0015-statestore-object-representation-policy.md`；来源修订 `8ef9fca`，正文保留原时期语义。
> 适用范围：整数参数、估算 DTO → 稀疏 Base/Delta plan 的首片合同；本片没有执行 Save，真实内容接线见后继分片。 今日事实从 [PROJECT-STATE](../../../src/PROJECT-STATE.md) 续接。

> 状态：Chosen — 用户已批准 DTO → plan 首切片，并将两个参数简化为整数倍数和整数百分比。
>
> 创建日期：2026-09-05
>
> 范围：既有 `DurableGraph.StateStore` 中的固定 `ReadAmplificationBaseBudgetPolicy`。
> 本文记录本轮实现契约；不冻结 public API、序列化接口或持久格式。

## 1. 需求与证据

| 来源 | 当前约束 |
|---|---|
| 用户：MVP 只绑定一个策略 | 不新增策略程序集、`IPolicy`、注册或替换机制 |
| 用户：序列化尚未开始，只需大致估算 | 先设计数值输入与表示选择；真实 payload、估算算法和 Write 接口不是前置条件 |
| 用户接受的上一轮建议 | 输入覆盖保存后全部 live 对象；G 从其 Base 估算求和；输出稀疏写决策；Removes 属于调用方 |
| 用户接受的两参数职责 | 读放大阈值产生 Base 动机，图大小比例控制可选 Base 写入；预算为软限制 |
| 用户本轮修订 | 两个参数只接受整数“X 倍、Y%”；删除 decimal 参数和相应精度处理 |
| 设计前源码 `19167d1` | StateStore 只有项目骨架；Storage 只有 membership 记录、wire、append/read 和 live-head map，没有对象 payload 或策略 |
| Probe 源码和测试 | 严格阈值、完整 B 计费、放大率排序、前缀选择、首候选超预算已有可执行参照 |
| 根 AGENTS.md | 原型 API 未冻结；优先小纵切；当前源码和测试优先于相邻设计文档 |

产品事实见 [StateStore 项目](../../../src/DurableGraph.Persistence/DurableGraph.Persistence.csproj)、
[StateRevision](../../../src/DurableGraph.Storage/StateRevision.cs)、
[StateRevisionStore](../../../src/DurableGraph.Storage/StateRevisionStore.cs)。
策略参照为 [MultiSegment 实现（归档）](../../../experiments/ARCHIVE.md#multi-segment "原路径：experiments/MultiSegmentStateStoreProbe/Policies/ReadAmplificationBaseBudgetPolicy.cs")
及[测试（归档）](../../../experiments/ARCHIVE.md#multi-segment "原路径：experiments/MultiSegmentStateStoreProbe/Tests/ReadAmplificationBaseBudgetPolicyTests.cs")。
TwoLeg 只提供设计储备，不建立项目依赖，不继承 Stay/Rotate、A-debt、evacuation 或两文件约束。

可观察目标：同一组有效估算和参数，无论输入排列如何，都产生同一组按 ObjectId 排序的写决策；
每个 Insert/Update 恰好写一次；只为有动机的 NoChange 增加 Base；规划不产生 I/O 或推进任何状态。

## 2. 最小边界

```text
同一次冻结的保存视图
    -> 全部 post-live 对象的估算值
    -> 固定 Plan 函数
    -> Writes: ObjectId + Base/Delta
    -> 后续 Save 编排取得 payload，形成 Revision 并写入
```

前四步可以用人工估算值独立验证。最后一步留给真实消费者；plan 不是可直接 Append 的 StateRevision。
产品首切片中的类型均为 `internal`，放在既有 `src/DurableGraph.StateStore`；不新增程序集。
后续真实跨程序集消费者出现时再开放所需类型。以下为声明摘要，省略方法体及 plan 的内部构造器：

```csharp
internal enum ObjectSaveChangeKind {
    Insert,
    Update,
    NoChange,
}

internal readonly record struct ObjectSaveEstimate(
    uint ObjectId,
    ObjectSaveChangeKind ChangeKind,
    long BasePayloadBytes,
    long? DeltaPayloadBytesUpperBound,
    long? ReconstructionPayloadBytes);

internal readonly record struct ReadAmplificationBaseBudgetParameters(
    int ReadAmplificationThreshold,
    int BaseBudgetPercent);

internal enum ObjectRepresentationMode {
    Base,
    Delta,
}

internal readonly record struct ObjectWriteDecision(
    uint ObjectId,
    ObjectRepresentationMode Mode);

internal sealed class ObjectRepresentationPlan {
    internal IReadOnlyList<ObjectWriteDecision> Writes { get; }
}

internal static class ReadAmplificationBaseBudgetPolicy {
    internal static ObjectRepresentationPlan Plan(
        IReadOnlyList<ObjectSaveEstimate> objects,
        ReadAmplificationBaseBudgetParameters parameters);
}
```

不增加输入包装 DTO、分类派生类型、命名工厂家族、Projection/Adapter、策略实例状态、理由枚举或诊断包。
`Plan` 是统一验证入口；包括 positional value DTO 的 default 值在内，都不能绕过入口验证。

## 3. 输入契约

### 3.1 字段适用性

| ChangeKind | B：BasePayloadBytes | D：DeltaPayloadBytesUpperBound | H：ReconstructionPayloadBytes |
|---|---|---|---|
| Insert | 必须提供 | 必须 null | 必须 null |
| Update | 必须提供 | 必须提供 | 必须提供 |
| NoChange | 必须提供 | 必须 null | 必须提供 |

- B 是本次保存后状态的完整 Base payload 精确大小，不是历史链起点的旧 Base 大小。
- H 是 exact parent 中该对象现有重建链的累计对象 payload 实际字节数。
- D 是从该 exact parent 对象状态得到本次目标状态的可用 Delta payload 上界；实际 prior 的向后文件距离未定时，
  该上界可能多计 0–4 bytes。
- B/D/H 都是已提供的非负整数；`null` 只表示该字段不适用，不表示未知。
- `D=0` 的 Update 仍是 Update，仍须写入；`H=0` 也不表示没有前驱。不要求 `H>=B`。
- 本轮前提是每个 Update 均有合法 Delta 方案；无法提供 Delta 的 schema/codec 场景出现时再扩展输入，
  不用 null、极大 D 或伪造 ChangeKind 表示“只能 Base”。

统一口径为对象自身的编码 payload 字节，B/H 不计共享 Revision/Frame、membership、对齐和文件开销；
D 是同层 payload 的保守上界。H 按同一口径累计，因此 `H+D` 可以作为继续 Delta 后的重建成本代理。
零值不代表真实记录零字节。不要求本轮实现编码器；D 的上界只来自实际地址编码范围，
不承诺其他物理开销的估算误差上限。
真实物理冷读涉及完整 Frame，此处比率不承诺磁盘读放大上限。

### 3.2 完整性与身份归属

集合必须恰好覆盖调用方冻结的完整 post-live 对象集合，Insert/Update/NoChange 由调用方从同一保存视图产生。
Remove 不进入此集合；策略只从集合计算 `G=ΣB`，不另外接收一个总量 authority。

策略验证非零且唯一的 ObjectId、合法枚举、字段适用性和数值范围；它不读取 Storage 来证明集合完整，
也不证明 Insert/Update 分类与 parent membership 一致。遗漏冷对象会低估 G；这是调用方契约错误，
不是 policy 能靠自身输入识别的错误。

DTO/plan 不携带 FrameAddress、SnapshotId、PlanId 或对象实例。同步调用期间调用方不能修改输入集合；
输入行是不可变值，policy 不修改也不保留调用方集合。plan 的有效范围是该次冻结的保存视图。
调用方必须保持真实 parent 和待保存状态不变，后续执行使用同一视图；任一改变便重新估算和规划。
仅复制 parent 地址不足以检测“head 未变、内存对象已变”的情形。

### 3.3 数值和失败

- B/D/H 使用非负 `long`；这不承诺能够存储该大小，也不引入 RBF admission 检查。
- `ReadAmplificationThreshold` 为 int 且至少 1；`BaseBudgetPercent` 为 int 且在 1–100 之间（含端点）。
  例如 `(3, 5)` 表示 3 倍、5%；本轮不设产品默认值。
- `G=ΣB`、每个 Update 的 `N=H+D` 必须可表示为非负 long，使用 checked；溢出拒绝，不截断或饱和。
  所有行先验证，包括稍后会直接选择 Base 的 Update。
- null 集合抛 `ArgumentNullException`；负数、未知枚举及参数越界抛 `ArgumentOutOfRangeException`；
  零/重复 ID 和 nullable 形状冲突抛 `ArgumentException`；累计溢出抛 `OverflowException`。
- 合法空集合返回空 Writes。失败不返回 partial plan；无需结果状态枚举、fallback 或重试机制。

## 4. 输出契约

`Writes` 是唯一决策集合，按 ObjectId 严格升序；结果持有自有数组的只读包装，不能把可写数组直接
当作 IReadOnlyList 暴露，也不保留调用方可变集合引用。

| 输入对象 | 输出义务 |
|---|---|
| Insert | 恰好一条 Base |
| Update | 恰好一条 Base 或 Delta |
| NoChange | 被选中时恰好一条 Base，否则缺席 |

没有额外对象、重复 ID、NoChange Delta 或 Remove 决策。NoChange Base 只改变存储表示，不产生逻辑修改。
预算只约束可选 Base，不能使 Insert/Update 从输出中消失。

Writes 是本次对象内容写集合，不是完整 post-live 对象集合。后续生成 ObjectHeadMap Delta 时，被省略的 NoChange
沿用旧 head；生成 ObjectHeadMap Base 时，调用方还必须把其旧 head 放入 ExternalObjectHeads。
Removes 和 ObjectHeadMap 模式仍由外层处理。不能仅用 Writes 生成一个“完整映射 Base”。
输出不附带 G、Q、预测写入总量、后继 H 或第二份 Base/Delta ID 集合；需要时从输入、参数和 Writes 派生。

## 5. 固定策略规则

令 `α=ReadAmplificationThreshold`、`p=BaseBudgetPercent`、`Q=floor(G*p/100)`。

1. 所有 Insert 选择 Base。所有 `B<=D` 的 Update 选择 Base；这一判断使用估算口径，表示 Base
   预计不比 Delta 更大，不是对真实编码结果的保证。两类 Base 均不消耗 Q。
2. 其余 Update 以 `A=(H+D)/B`、全部 NoChange 以 `A=H/B` 判断读动机；只有 `A>α` 才成为候选。
   等号没有动机。`0/0` 定义为 1，正数除以 0 为正无穷；不存在的 H/D 不参与计算。
3. 候选按 A 降序、ObjectId 升序排序，Update 与 NoChange 共用同一序列。
4. 从该序列选择最长前缀，费用为每个候选的完整 B，累计不得超过 Q，恰好等于 Q 可以选。
   遇到第一项装不下便停止，不跳过它去填充后续小对象。
5. 唯一例外：排序后的第一个候选若独自超过 Q，仍选择它，然后停止可选选择。
   例外取决于序号为零，不是“累计费用还为零”；已选零 B 候选也占据前缀位置。
6. 被选候选写 Base；其余 Update 写 Delta；其余 NoChange 不写内容。

每次调用重新得到一个 Q，没有跨 Save 余额、债务、aging 或公平性状态；同样输入重复调用产生同样结果。
费用固定使用 B，不能悄悄改成 Update 的 `B-D`。Insert 也参与 G，虽然其必须写入的 Base 不扣 Q。
Remove 不参与 G；低放大的 NoChange 参与 G，但没有因此获得写入动机。

这是可选 Base 的软预算：Q=0 仍可能通过首候选例外产生正字节 Base；总写入还包含必需工作和格式开销。
有限预算可能推迟处理，所以 α 不是所有对象的放大率硬上限；持续高优先级输入也可能使低优先级对象
长期等待。本轮不新增硬峰值保证、自动拆对象或无饥饿保证。

### 5.1 整数参数与精确中间乘积

按用户修订，参数只表达整数倍数与整数百分比。无需 decimal 分解、BigInteger 或公开算术抽象：

- 阈值比较：对非零 B 检查 `N > (Int128)B * α`；零分母按第 5 节处理。
- 预算：非负整数除法 `Q = (long)((Int128)G * p / 100)`。
- 比率排序：处理零分母后，比较 `(Int128)Nx * By` 与 `(Int128)Ny * Bx`。

非负 long 的交叉积小于 `2^126`，long 与正 int 的乘积小于 `2^94`，均可由有符号 Int128 精确容纳。
因此 Int128 是唯一需要的中间数值类型；字节 DTO、G、H+D 与预算仍为 long。
G 与 H+D 的 checked 验证保留，不能用更宽的中间类型悄悄扩大其输入合同。

上一轮 decimal 舍入反例促成精确参数算术提案；用户明确不需要小数参数后，该提案被本节取代。
原 Probe 参数与实现保持原样，不承担新产品整数 API 的兼容职责。

## 6. 一组完整例子与验证条件

设 `α=3`、`p=25`：

| ObjectId | Kind | B | D | H | 结果 |
|---|---|---:|---:|---:|---|
| 10 | Insert | 20 | — | — | Base，必须写 |
| 20 | Update | 100 | 10 | 350 | Delta，A=3.6 有动机但预算前缀未选中 |
| 30 | NoChange | 80 | — | 400 | Base，A=5，优先选中 |
| 40 | Update | 30 | 40 | 0 | Base，B<=D |
| 50 | NoChange | 170 | — | 170 | 缺席，A=1 |

G=400，Q=100。候选次序为 30、20；选择 30 后费用 80，20 会使费用变为 180，因此停止。
Writes 为 `[(10,Base),(20,Delta),(30,Base),(40,Base)]`，对象 50 沿用旧 head。
本次对象 payload 预计写入 140，大于 Q，但可选 Base 费用只有 80。

最小 selector 切片用独立预期值验证：

| 见证 | 必须观察到的结果 |
|---|---|
| 上述完整例子及输入排列 | 写集合完全相同，输出按 ID 升序；输入未被修改 |
| 相同 B/D、不同 H | 仅链成本改变即可跨过动机阈值；Update/NoChange 在阈值等号处均不因读动机选 Base |
| B=D；D=0；B=0 | B=D 直接选 Base；零 D Update 仍写；0/0=1、正数/0=∞，不得混同缺失 |
| 预算取整、恰好放入、完整 B 费用 | floor 不四舍五入；恰好 Q 可以选；不用 B-D 计费 |
| 首候选大于 Q；Q=0；首项 B=0 后接超预算对象 | 仅真正排序首项可单独超预算；零费用不重新获得例外 |
| 中间候选装不下、后面有小对象 | 在中间项停止，不进行 backfill |
| G=19、p=89 的 floor 见证 | NoChange(1,B=16,H=64)、NoChange(2,B=1,H=3)、Insert(3,B=2)，α=2；Q=16，Writes 仅含 1 和 3 |
| 整数参数边界 | 倍数 1 和 int.MaxValue、百分比 1 和 100 可用；倍数 0、百分比 0/101 均拒绝 |
| long 级交叉积及相近比率 | 比率排序不因中间乘法溢出或浮点近似而错误；大 G 的百分比预算也不溢出 |
| 形状、重复/零 ID、负数、累计溢出 | 在返回 plan 前拒绝，不改输入；合法空集合返回空 Writes |
| 重复规划与结果所有权 | 不消耗后续调用预算；结果集合不可写，调用后修改输入集合不改变已有 plan |

验收使用人工估算，不需真实 serializer、payload buffer 或 RBF fixture。

## 7. 设计分支裁决与暂缓项

本轮先由需求质疑者、最小架构师、语义守护者独立核对，再交叉检验失败例；主审以源码和具体反例裁决。
审查材料属于同一设计过程，不能当成多份独立产品需求。

| 分支 | 裁决 | 理由与重访条件 |
|---|---|---|
| Estimate/Write 接口 vs 数值 DTO | 选择 DTO；defer 序列化接口 | 用户已选方向；首个真实 serializer/执行调用方出现后再设计方法 |
| 泛型策略框架、新程序集 | delete 当前提案中的扩展层 | MVP 一个策略；已有 StateStore 项目足够 |
| 单写集合 vs 分别保存 Update/Rebase/Base/Delta 集合 | merge 为 Writes | 保存相同决策的多份集合会产生重复事实 |
| 输入 parent/token/plan ID | simplify 为调用方冻结视图 | 地址不能检测同 head 下对象改变；真实跨 Save、异步或导出计划消费者出现时重访 |
| 按种类类型家族与工厂 | simplify 为一个值 DTO，Plan 统一验证 | 三种字段形状足够；不保留多入口的重复验证 |
| int B/D + long H vs 全部 long | 主审选择 long | 审查有不同偏好；int 与现有单 Frame 长度一致，long 使估算层保持统一数值域。精确乘法本已需要，无须新增机制；不代表扩大存储能力 |
| decimal 参数及 BigInteger vs 整数倍数/百分比 | 用户选择整数；删除 decimal 路径 | 实际调参精度只需 X 倍、Y%；Int128 足够容纳全部中间乘积 |
| D>0 vs D>=0 | simplify 为允许已知零估算 | 不把 Probe 的 synthetic payload 约束带入估算 DTO；Update 写入义务保留 |
| B-D 计费、backfill、硬预算、跨调用余额 | defer | 均改变已选择的策略；需新的产品行为要求和 workload 证据 |

真实 payload 获取、SameStateRebase 编码、H 的恢复/提交更新和估算精度校准，等首个 serializer/Save 消费者；
ObjectHeadMap checkpoint 等映射冷读纵切；物理 Frame 成本等真实读测量；不可 Delta 的 Update 等具体 codec
限制；public API 与程序集开放等真实外部调用方。均不作为本轮纯 selector 的前置条件。

## 8. 实施边界与证据入口

本轮在现有 StateStore 项目加入
[估算 DTO](../../../src/DurableGraph.Persistence/ObjectSaveEstimate.cs)、
[整数参数](../../../src/DurableGraph.Persistence/ReadAmplificationBaseBudgetParameters.cs)、
[只读计划](../../../src/DurableGraph.Persistence/ObjectRepresentationPlan.cs)和
[固定纯 selector](../../../src/DurableGraph.Persistence/ReadAmplificationBaseBudgetPolicy.cs)，
只为现有 StateStore.Tests 增加 friend assembly。
[产品验收测试](../../../tests/DurableGraph.Persistence.Tests/ReadAmplificationBaseBudgetPolicyTests.cs)使用独立预期值验证第 6 节。

2026-09-05 集成验证：`dotnet build DurableGraph.slnx --no-restore` 成功（0 警告、0 错误）；
StateStore、Storage、Serialization 三个测试项目分别通过 40、73、65 项，无失败或跳过。
独立只读审查未发现阻断问题；验证范围是纯数值选择及既有底层回归。

后续由真实 Save 消费者提供估算并消费 Writes；那一切片才确定估算生产、payload 写入和状态更新。

当前 Storage wire、StateRevision 及其 API 均不因本设计改变；没有必要为运行纯 selector 先改 Storage。
相邻总览见 [阶段 B 设计（归档）](../../../experiments/ARCHIVE.md#multi-segment "原路径：experiments/MultiSegmentStateStoreProbe/STATESTORE-SUBSYSTEM-DESIGN.md")。
