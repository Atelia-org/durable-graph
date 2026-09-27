# DB-083：从用户故事推导 Repository 与 Checkpoint API

> 状态：**目标语义已选定；A/B/C 已实施并验收；DG tag 接入未实施；不是执行工单**。2026-09-28 校准交付范围；用户确认的 Event-first、跨类型 State、nearest PreviousEvent 与上游不可变 tag 保持。
> 本文重新检验 DB-076–082 所依赖的公共使用模型；不是继续按旧 Event/State 交替合同施工的授权。
> 当前产品事实仍以源码为准。公共基础见 [078-A 记录](0078-a-repository-free-history-implementation.md)；独立 Checkpoint 与固定查询见 [078-B 记录](0078-b-checkpoint-history-query-implementation.md)；多分支工作副本与 Fork 见 [078-C 记录](0078-c-branch-checkout-fork-implementation.md)。
> 内部恢复机制的统一见 [DB-079 实施记录](0079-shared-graph-restoration-core-implementation.md)，Checkout/Fork 单 State 材料复用见 [DB-080 实施记录](0080-prepared-checkpoint-reuse-implementation.md)；均不改变本文选图、隔离、历史或发布语义，不包含热提交准入或领域实例共享。
> [DB-082 叶复用实验](0082-prepared-immutable-leaf-reuse-implementation.md) 已实现内部候选，未获得稳定总成本收益而保持默认关闭；不增加公开 options，也不改变本稿默认读取行为。

## 1. 本轮需求账本

| 要求或问题 | 来源与地位 |
|---|---|
| 下游从目录打开统一 Repository，在实例上创建/签出分支 | 用户明确认可的产品入口方向；内部 Store 层次不应成为日常接入前提 |
| 非泛型 BranchCheckout 作为基础入口 | 用户本轮明确选择；非特定应用的基础工具也应能创建、签出和分叉，不必在调用处指定领域 State 类型 |
| Event-first；允许跨实际类型替换 State | 用户已明确选择；首个 State 前可以记录 Event，领域类型限制由应用承担 |
| PreviousEvent 为最近的严格祖先 Event | 用户已明确选择；不含自身，不限直接 Parent，不代表业务完成度 |
| 不可变 tag 纳入目标，由上游 EventJournal 独立分片实现 | 用户已明确选择；首片地址仅限本次打开，不提供可序列化外部地址 |
| Event 与 State 可以各自提交，也可连续提交同一种 | 用户明确要求；替代旧强制 E/S 交替，不意味着移除发布、来源和故障检查 |
| 以指定历史地址为结尾读取 Checkpoint；不是始终读取最新 head | 用户候选；用途包括从 ref/tag 指定位置浏览和恢复 |
| Checkpoint 非泛型，按 EventCheckpoint / StateCheckpoint 区分 | 用户首选；没有以 Coding Agent 追踪类型为由增加泛型的需求 |
| EventCheckpoint 提供 PreviousState；StateCheckpoint 提供 PreviousEvent | 用户要求的读取便利；具体返回图还是递归检查点、缺失情况和顺序含义由故事检验 |
| API 不保证 eager / lazy，首版选简单实现 | 用户明确放宽；资源寿命、对象可变性与可观察失败边界仍须定义 |
| DramaBoard：规则的 State→Event→State | 用户当前用例；应用的实际 adapter/历史接入事实另以代码核对 |
| LLM tool-loop：完整运行状态保存在 State；Event 只记录消息或操作 | 用户本轮明确澄清；冷开不靠 replay 消息历史重建配置/执行状态，后续消息处理仍由应用编排 |
| 模拟 rollout / 反事实探索 | 用户此前提及的应用方向；用于压力检验，不据此引入 Gym 框架或业务 API |
| fork 等效于持久分支 ref + 独立恢复；当前无兼容性包袱 | 用户已明确的目标；优先正确与易用，后续按证据优化 |
| 串行仓库操作、不同分支各一个工作副本、append-only、明确 publication/fault | 已接受项目边界；不同分支不等于多线程 writer，文档不授予外部副作用回滚 |

相关前案：[技术路径](0076-efficient-graph-fork-technical-path.md)、[固定模型环境](0077-repository-model-environment-slice.md)、
[可编辑 fork](0078-editable-checkpoint-fork-slice.md)、[术语校准稿](../DurableGraph-glossary.md)。
它们是本轮被检验的同源设计，不作为互相独立的需求证据。

## 2. 推荐的最小使用模型

**一个 Repository 管理持久分支；非泛型 BranchCheckout 持有可选的 State 与保存基线；每次独立提交 Event 或 State；
非泛型 Checkpoint 在指定历史位置提供当前图与必要的前置上下文。业务自己决定怎样处理事件、何时保存 State。**

分支可以从非空 State 或非空 Event 开始，随后允许任意 E/S 顺序：

```text
S0 → E1 → E2 → S1 → S2 → E3
E0 → E1 → S0 → E2 → S1 → S2
```

`InitWorldSetup → 首个 State` 已纳入目标。没有 State 是合法历史状态，不是一个 null-root State 检查点，
也不表示初始化成功或失败。库不为它制造占位 State，不自动执行初始化 Event。分支首次发布仍必须指向真实 E/S，
不暴露空 Head 或未发布的初始化工作副本；保存与验证规则见 §6。

三个层次分别负责：

| 层次 | 提供什么 | 不从它推断什么 |
|---|---|---|
| 分支 Head | 已发布的最后历史位置 | 不是最近 State 的别名，也不是已处理事件位置 |
| 工作副本的 State 基线 | 最近已提交 State 的完整保存来源与当前编辑图；尚无 State 时两者均无 | 不自动归约后续 Event，不承诺是任意应用在 Head 处的完整运行状态 |
| 业务恢复状态 | 由应用持久保存的配置、阶段、游标、必要输入 | 库不凭 PreviousEvent 或提交次序推断业务执行成功 |

DramaBoard 可继续在自己的 adapter 中执行 E/S 交替；LLM 可以连续记录消息；模拟实验可以只提交 State。
三者共用同一仓库与发布机制，不需要不同的 Repository 模式。

## 3. API 草图与读取形状

以下公共形状由 DB-078-A/B/C 交付；构造器、保存策略等非本轮差异省略，实际验收范围见各片记录。
公开门面建议使用根命名空间 `Atelia.DurableGraph`，实现可以继续位于现有 Persistence 程序集。
不新增一个转发门面与旧公开类型并行维护，也不为命名反转程序集依赖。

```csharp
// Repository factory consumes the model configuration once.
Repository.CreateNew(path, models);
Repository.OpenExisting(path, models);
Repository.OpenReadOnlyExisting(path, models);

repo.CreateBranch(name, initialState);       // IDurableObject root; publishes S0; BranchCheckout
repo.CreateBranchFromEvent(name, initialEvent); // IDurableObject root; publishes E0; State == null
repo.Checkout(name);                         // BranchCheckout; no new durable ref
repo.Fork(newName, checkpointAddress);       // BranchCheckout; new ref + restore State if present
repo.GetHead(name);                          // CheckpointAddress

checkout.State;                              // IDurableObject?; null only before the first State
checkout.Head;                               // CheckpointAddress, may identify E or S
checkout.CommitEvent(domainEvent);           // IDurableObject root; returns CheckpointAddress
checkout.CommitState();                      // requires an existing State; returns CheckpointAddress
checkout.CommitState(nextState);             // non-null IDurableObject; first State or any registered root type

repo.ReadCheckpoint(address);                // Checkpoint, non-generic
repo.ReadState(address);                     // IDurableObject, specified State graph only
repo.ReadEvent(address);                     // IDurableObject, Event graph only
repo.EnumerateEvents(endInclusive, order, afterExclusive);
// IEnumerable<CheckpointAddress>; order defaults to HistoryOrder.NewestFirst;
// afterExclusive defaults to null.
```

ref-only `CreateBranch(name, address)`、`MoveBranch(name, expectedHead, target)`、单图 ReadState 与非泛型 ReadPair 保留各自用途，
位置参数/返回值统一为 CheckpointAddress；ReadPair 继续显式只读共享，不替代默认 Checkpoint。全链元数据 ReadFrames 与旧 ReadEvents 的迁移
见 [DB-078 分阶段设计](0078-editable-checkpoint-fork-slice.md)；本草图不是全部 API 清单，也不保留旧 GraphFrame 地址的兼容双轨。
打开参数中的 `RbfSegmentStoreOptions` 等底层类型也应在公共接入审查中处理，但本轮不提前新增 options 框架或重组所有包。

```csharp
public abstract class Checkpoint {
    public CheckpointAddress Address { get; }
}

public sealed class EventCheckpoint : Checkpoint {
    public IDurableObject Event { get; }
    public IDurableObject? PreviousState { get; }
    public CheckpointAddress? PreviousStateAddress { get; }
}

public sealed class StateCheckpoint : Checkpoint {
    public IDurableObject State { get; }
    public IDurableObject? PreviousEvent { get; }
    public CheckpointAddress? PreviousEventAddress { get; }
}
```

PreviousX 直接提供领域根，另给对应地址；**不递归返回另一份带 PreviousX 的完整 Checkpoint**。
否则“最简单的 eager 实现”会递归恢复整条历史。相应种类的严格祖先不存在时，PreviousX 的根和地址同时为 null；
首个 State 若前有 Event，仍有 PreviousEvent。EventCheckpoint.Event 与 StateCheckpoint.State 始终非空。
应用按自己的模型检查或转换根类型；
Checkpoint 与 BranchCheckout 均不要求调用处先指定领域类型。实际模型资格与替换根合同见 §3.4。

### 3.1 PreviousX 的选定规则

用户已选定统一规则：**沿选中位置的严格逻辑祖先链，取最近的相应种类；不包含当前位置，不按物理相邻记录查找。**

| 历史位置 | PreviousState | PreviousEvent |
|---|---|---|
| S0 | — | null |
| S0→E1 | S0 | — |
| S0→E1→E2 | S0 | — |
| S0→E1→E2→S1 | — | E2 |
| S0→E1→E2→S1→S2 | — | E2 |
| E0 | null | — |
| E0→E1 | null | — |
| E0→E1→S0 | — | E1 |

PreviousEvent 不表示唯一原因、已经应用或上一次 State 之后新增的事件；PreviousState 不保证只应用当前这一个 Event 就够。
例如 E2 前还有 E1，通用应用不能仅 `Apply(E2.PreviousState, E2.Event)` 就宣称得到完整后继状态。

不采用“仅直接 Parent 为 Event 才返回”的备选，不提供运行时策略参数。
需要“上次 State 之后新增的事件”时使用带 afterExclusive 的 EnumerateEvents；三份案例均不依赖 PreviousEvent 推断业务完成度。

### 3.2 图的独立性与加载时机

ReadCheckpoint 交付的是**可用于内存计算的独立历史恢复结果**；修改它不写盘，不推进分支，也不自动更新工作副本。
需要保存计算结果时，应用显式调用工作副本的 CommitState(nextState)。这条路径允许新根失去原工作副本实例映射的复用机会，
不为优化它提前增加 adopt/import 或自动身份转移。

- 每张图内部的引用别名、环和真实类型保持；当前图与 PreviousX 图之间、与活动工作副本及其他独立读取之间，不暗中共享可变领域实例。
- 同一 Checkpoint 的一个根属性首次成功交付后，重复访问得到同一份图；不能重新读取并丢掉调用者的本地修改。
- 不承诺所有引用都不同；string 规则保持，未来有证明的不可变实例复用可以优化。跨图可变隔离不能被优化撤销。
- 不保证 eager / lazy、读取次数或缓存方式。首版建议 eager，使用一个内部读取会话分别物化至多两份领域图，全部成功后返回。
- 属性在本次打开的 Repository 可用期间使用；访问可能 I/O、执行模型回调或失败。已取出的领域根可作为普通内存对象保留，
  不保证关闭/故障后的 Checkpoint 仍能继续加载或导航。Lazy 实现不得重新选取当前 branch head，也不得交付半幅图。
- 专用 ReadPair 保留明确的只读共享合同。它可能共享可变类型，不能直接用来实现默认 Checkpoint 的独立图语义。

决定性反例：State.PendingTool.Arguments 与 Event.ToolRequested.Arguments 指向内容相同的历史子图。
用户修改 PreviousState 准备重试时，Event 中记录的原参数必须保持；默认使用 ReadPair 的共享结果会破坏这个正常用法。

### 3.3 地址、ref 与打开寿命

`CheckpointAddress` 已由 A 选为库签发的不可变 class；它不是旧 `GraphFrame.RevisionAddress` 的另一种拼写，诊断修订地址只定位底层图修订，
不包含完整 Journal 历史角色与来源。示意代码中的地址来自仓库的 GetHead、成功提交和历史查询。

首个垂直分片选定同一打开实例签发的 opaque 地址，重开后从持久 ref 重新解析；跨仓库、重开旧句柄与无效/default 输入拒绝。
地址相等按本次打开的 owner 与逻辑位置判断，不按包装对象的引用判断；无公共裸坐标构造或反序列化入口。
这个范围足以表达“固定历史结尾”。若未来需要把地址直接保存到外部、跨进程/跨重开使用，
须另定仓库身份、复制仓库和地址验证合同，不能靠“同坐标恰好是合法帧”认证来源。

不可变 tag 已纳入目标，持久机制由兄弟仓库 `atelia-storage/src/EventJournal` 拥有，
独立交付顺序与接入验收见 [DB-084](0084-eventjournal-immutable-tags-slice.md)。当前 pin **没有独立 tag API**，不能当作现成功能调用。
目标只需创建与按名解析固定点；重开解析得到本次打开的新地址，不移动分支或恢复领域图。
tag 不阻塞 DB-078-A/B/C，也不授权在 DurableGraph 建第二套 tag 日志。可序列化外部地址留到有单独身份合同后再设计。

### 3.4 非泛型工作副本：角色、模型与替换根

`CreateBranch(string name, IDurableObject initialState)`、`CreateBranchFromEvent(string name, IDurableObject initialEvent)`、`Checkout(string name)` 与
`Fork(string newName, CheckpointAddress from)` 均返回同一个非泛型 `BranchCheckout`；工作副本实现 `IDisposable`，
绑定一个分支并持有可选的 State 保存基线。Dispose 释放编辑占用，不自动保存、撤销领域修改或删除持久分支。
不新增公共 `BranchCheckout<TState>`、`<TState, TEvent>`、`As<T>` adapter 或 `StateType` 属性。

- **持久角色由入口决定。** `CommitEvent(IDurableObject domainEvent)` 发布 Event；
  `CommitState(IDurableObject nextState)` 发布 State。Event 与 State 无需共同领域基类，也无需实现额外的 IEvent/IState。
  同一 CLR 类型可以用于不同角色；库不根据类型名推断业务意图或完成进度。
- **模型由实际类型和持久 Schema 决定。** 新根按实际 CLR 类型选择已注册模型；恢复按持久 Schema 与打开时固定的模型环境
  取得当前实际类型。`IDurableObject` 仅表示准入，不自带序列化能力。非泛型不等于无需模型、reader 或 Upgrade，
  也不承诺通读未知 Schema。通用工具可以由宿主提供模型配置而不在分支调用处引用具体领域类型；只操作 ref 的工具仍走 ref-only 路径。
- **根替换是一次明确提交。** `CreateBranch` 正常返回保留传入的初始实例；`CommitState(nextState)` 正常返回后，
  `State` 指向传入的新根并安装此次保存基线，后继无参数 `CommitState()` 保存这个根。提交期间图须稳定，发布前不安装候选根。
  应用之前取出的根仍是普通 CLR 引用，不会自动跟随替换；调用者自己建立的可变别名也不被库消除。
  提交失败不承诺回滚；仍按 publication/fault 合同恢复，不能凭旧 getter 推断磁盘结果。
- **跨类型替换允许。** `SetupState → RunningState → FinishedState` 是业务根切换，不是历史 Schema Upgrade。
  每次按实际根选择模型；发布成功后，根、RootId、模型解释、冻结基线与实例身份映射一致推进，后继无参提交使用新根的模型。
  原 State 中仍被新图引用的对象保持身份；已有 child 升为根也保留 ID，只有新实例分配新 ID，仅不再可达的旧成员被移除。
  不重置整个工作区，也不把旧根 ID 强给新实例。应用可以自行要求某一领域类型，库不设置分支持久类型标记。
- **无 State 是同一工作副本的合法状态。** `CreateBranchFromEvent` 追加首个 Event 并发布新分支，返回 `State == null`；
  它接受新领域事件，已有历史地址分叉使用 Fork/ref-only CreateBranch。连续 CommitEvent 不建立 State。
  `CommitState(nonNullRoot)` 建立首个 State；无参 CommitState 在没有 State 时、任何 Capture/追加前拒绝。
  已知 null/未注册直接根在追加/发布前拒绝，Head/State/已提交基线不变；深层模型或 Schema 错误仍按实际准备/发布阶段处理，
  不承诺所有失败都零写入。缺模型、损坏或恢复失败不能被转换成 `State == null`。

领域代码在业务入口 cast 或模式匹配即可；成功替换后使用新根或重新读取 State：

```csharp
using var work = repo.Checkout("main");
var state = work.State as SimulationState
    ?? throw new InvalidOperationException("This application requires an initialized SimulationState.");
SimulationState next = FoldAndValidate(state, occurrence);
work.CommitEvent(occurrence);
work.CommitState(next);
kernel.Install((SimulationState)work.State); // state 仍指向旧根
```

强类型便利层留待真实接入证明需要，不为省一次 cast 增加第二种工作副本及其寿命/缓存合同。
`Fork` 的成功效果是“精确位置的新持久 ref + 独立 State 工作副本”，不是要求先发布 ref 再恢复：
先完成所需恢复与工作副本准备，再发布新 ref；普通恢复失败不创建 ref、不交付半图。
发布后交付失败时，新分支可能已经存在，仍按 outcome/fault 重开检查，不能自动删除或盲目重试。
Fork 本身不追加新的 E/S 检查点，不复制源工作副本尚未提交的字段，也不把选中的 Event head 降为其前 State。
Checkout/Fork 有最近 State 时只恢复它，不恢复选中或沿途 Event 的领域图；没有 State 时交付 State/基线均无的工作副本，
不物化任何领域图、不需要 Event 模型能力。读取事件内容由应用显式调用 ReadEvent/ReadCheckpoint。
所需模型能力取决于实际请求图的完整恢复来源；Open 的全历史数据验证仍保留，不能把坏历史降级为空 State。

## 4. 事件查询：便利配对不应强迫加载 State

ReadCheckpoint 服务“当前图 + 邻近上下文”的高频读取；ReadEvent 则只恢复指定 Event 的可达图，
不因为存在 PreviousState 而额外 Allocate/Hydrate 那份 State。两者有不同的可观察内容范围，值得同时保留。
如果 Event 自身引用巨大领域图，或其 Delta 重建需要旧记录，仍须读取这些实际依赖；不承诺零 StateStore I/O。

事件枚举建议进入本轮最小 API，而非先增加专用“遇 State 停止”的唯一入口：

- 以 endInclusive 固定逻辑历史，后来分支推进/移动不改变本次查询。
- 只返回轻量事件地址；经过 State 记录不物化其领域图。
- 可选 afterExclusive 界定祖先范围；同一位置得空范围，不在该祖先链的起点拒绝，不能拼接别的分支。
  提供下界时，在首项交付前验证其祖先关系；这可能需要完整范围的元数据回溯，不伪称该校验没有准备成本。
- 默认逆序，允许用户提前停止；不要先准备全链再包装成 IEnumerable 冒充按需路径。
- 正序可先准备所需范围的轻量地址再输出。首版不承诺正序首项 O(1)、有界地址缓冲或每返回一项只读取一帧。
- 库操作仍串行。枚举器不跨 yield 保持 `_busy` 或底层借用 lease，调用者才能在循环内调用 ReadEvent；
  每次推进/读取分别检查仓库可用性，固定 end 保证其间其他串行提交不改变所选历史。
- 仓库打开时的完整校验成本单独计量；事件小读不等于已经优化 Open。

LLM 的 `S0→user→assistant→S1→tool→assistant→S2` 需要跨 S1 找到先前消息，State 保存不意味着对话历史截断。
“从 E 倒查到最近 State”只是上述查询限定起点后的一个区间：可用 PreviousStateAddress 作为 afterExclusive，暂不必另造枚举器。
区间成员不自动称为 PendingEvents；消息窗口、压缩和摘要位置由应用 State 表达。
例如从 Event 点 fork 后先保存新配置，未处理消息可能已位于最新 State 之前；查待办须遵守应用 Progress，不能总以最近 State 截断。

## 5. 三类应用的故事与证据

| 案例 | 独立稿 | 推导出的共同能力 |
|---|---|---|
| DramaBoard | [游戏推进、恢复与分叉](0083-checkpoint-api-user-stories/dramaboard.md) | 应用保留交替，库解除交替；纯 fold 的替换根；历史 E 上下文；fork 后改 lineage 可立即提交 S |
| LLM tool-loop | [消息、运行状态与历史探索](0083-checkpoint-api-user-stories/llm-tool-loop.md) | 连续 E；配置/阶段直接恢复；按固定结尾跨 State 读消息；从历史位置恢复完整 State，并按应用协议处理消息 |
| 模拟与 rollout | [纯 State、扇出与样本小读](0083-checkpoint-api-user-stories/rollout.md) | 无 dummy Event 的连续 S；不同命名分支独立推进；只读出小 Event 的内容范围 |

DramaBoard 的实际 Kernel 接缝仍规定最多一个 PendingEvent，并先纯 fold/validate scratch，再 CommitEvent、CommitState，
最后安装世界。当前活跃持久 adapter 已归档，不能把新示意写成已接入产品；真实证据见案例中的源码链接。

用户已明确澄清 LLM 数据归属：**完整运行状态保存在 State，Event 只记录消息或操作。**
AgentState 直接保存有效 prompt/model/reasoning、执行阶段、必要工具调用与上下文状态；冷开直接恢复它，
不回放旧消息或配置变更操作来拼出运行状态。消息内容仍可从历史查询，以构造模型输入，这不是重放状态机。

**从 Event 点 Fork 精确继承该历史 Head，并恢复存在的最近 State 保存基线。** 尚无 State 时由应用决定初始化；若已有 State 后还有消息，
它们是否待处理由应用的阶段、消息身份/进度等协议判断，不能从 PreviousX 或 Head.Kind 自动推出。
工作副本不会自动应用这些消息，也不包含尚未提交的运行状态变化；应用可以继续处理已记录消息后显式 CommitState。
连续 E 的批量处理、工具结果已记 E 但后继 S 尚未发布，必须出现在应用恢复案例里；不能以“无需 replay”省略这些故障窗口。
分支 LineageId、RNG 状态、工具调用 ID 等均属领域模型，DG 不在 Fork 时偷偷重写。

进一步适合探索的用途是可分支的规则调试、规划器候选比较、参数反事实实验：共同条件是主要运行状态确实由可持久对象图表达。
这些是潜在应用，不构成当前交付需求；不由此增加合并、分布式调度、GPU/进程克隆或外部副作用恰好一次机制。

### 5.1 通用工具：无需知道 State 类型的分叉

本轮用户明确提出非特定应用的基础工具。其分支操作只依赖仓库接口与历史地址，领域模型能力由宿主配置：

```csharp
using var repo = Repository.OpenExisting(path, models);
var selected = repo.GetHead(sourceBranch);
using var candidate = repo.Fork(candidateBranch, selected);
ShowBranch(candidateBranch, candidate.Head); // 无领域根类型参数或反射分派
```

成功后新分支位于 selected，源分支不变；释放 candidate 后持久分支仍存在，应用可再 Checkout 后检查 State 类型并继续。
这里的 Fork 在存在 State 时会恢复它，仍有模型能力与物化成本；完全不需要编辑图的工具可使用 ref-only 操作，
其命名与保留入口沿 §3 的施工核对处理，不能用“非泛型”冒充“只改 ref”或“无需模型”。

## 6. 与现有存储机制的对应

已有 [HistoryJournal](../../src/DurableGraph.Persistence/HistoryJournal.cs) 用 OpaqueEventKind 区分 E/S，
每条记录的小 envelope 定位对应图；不要求仅为两种公开 Checkpoint 派生类就新增两种 RBF 物理格式。
A 曾保留的全量 ReadEvents 由 B 移除；当前 [EnumerateEvents](../../src/DurableGraph.Persistence/Repository.cs) 默认逆序按需，
只返回所选历史范围内的事件地址，不物化 State 领域图；ReadFrames 保留全链元数据检查用途。
实际 storage pin 以 [StorageDependency.props](../../eng/StorageDependency.props) 为准；本轮查的是该 commit 的头部读取、Parent、refs 与 forward-plan 源码。

放开交替后仍区分 Journal Parent 与图保存 Parent。统一规则：提交前 Head 为 P（首次创建时无），
B 为 P 及其祖先中的最近 State（可以无）。所有 E/S 的 Journal Parent=P，图保存 Parent=B.Revision 或 null。
冷开验证某记录时，则从该记录的严格祖先链寻找 B；不能把被验证的 State 自身当成其保存 Parent。

| 发布记录 | Journal Parent | StateRevision Parent |
|---|---|---|
| S0 | null | null |
| E1 | S0 | S0 |
| E2 | E1 | S0 |
| S1 | E2 | S0 |
| S2 | S1 | S1 |

Event-first 的另一条合法链：

| 发布记录 | Journal Parent | StateRevision Parent |
|---|---|---|
| E0 | null | null |
| E1 | E0 | null |
| S0 | E1 | null |
| E2 | S0 | S0 |
| S1 | E2 | S0 |
| S2 | S1 | S1 |

无 State 基线时，Event 与首个 State 都走无 Parent 的完整 Base 规划，不能借前一个 Event 作比较来源。
Event 始终丢弃捕获候选、不 Accept live 身份、不安装保存基线；只更新发布 Head。首个 State 成功后才安装完整保存状态。
每次提交检查工作副本基线与 B 同时存在且 exact 匹配，或同时不存在；冷开检查所有物理记录，包括未被分支引用的 orphan。
合法无 State 由完整历史导航证明，不能通过捕获读取异常来判定；已有 State 的缺失模型或损坏不能回退到更旧 State。

最近 State 与 PreviousEvent 可先从已验证 Parent/Kind 推导，或在现有记录索引上记派生导航；不是新的持久权威。
不要按物理前一帧推导祖先，也不必先修改格式加入多套前驱指针。
[WorldWorkspace.StageSnapshot](../../src/DurableGraph.Persistence/WorldWorkspace.cs) 已能基于 State 保存独立 Event 候选且不安装 State，
这项底层能力可以保留；需要重做的是提交检查、最近 State 查找、重开验证及单个 PendingEvent 的公共含义。

非泛型工作副本也不是把现有类型直接替换为 `WorldWorkspace<IDurableObject>`：
A 实施前的 WorldWorkspace 按 `typeof(TWorld)` 做 exact 根检查并持有根模型；该限制已在[当前工作区](../../src/DurableGraph.Persistence/WorldWorkspace.cs)移除。
新的工作副本应按实际根模型保存/恢复，同时保持完整保存来源。现有
[替换根测试](../../tests/DurableGraph.Persistence.Tests/EventHistoryRepositoryTests.cs)中的
`RootReplacementInstallsOriginalCandidateOnlyAfterPublication` 是原实例安装与后继保存的回归依据，不能因公共类型擦除而丢失。

Event 捕获在热路径消耗 ID 游标但丢弃其 live 绑定；冷恢复按 State 的完整源成员最大 ID + 1 开始，
无 State 的冷 Checkout/Fork 使用空捕获会话，游标从正常初值开始；可能与过去 Event-only 对象的数字重合。
无 State 的热路径也只消耗游标、不接受 Event 身份；首 State 不导入先前 Event 图的身份。ID 以具体 Revision 解释，不能增加“跨检查点永久唯一”的新假设，
也不必为维持这个未承诺的性质恢复整段 Event 图。

## 7. 辩证裁决记录

三位应用审阅者独立提出故事，随后互相检验；一位存储语义审阅者核对当前产品和 pin 的实际能力。
主线程复查 DramaBoard pure-fold/lineage、当前恢复/发布路径，裁决仅依据需求与反例；没有运行新 API 或性能实验。
2026-09-27 针对非泛型工作副本另由需求质疑、最小架构、语义防守三个角色独立审阅并交叉质询；
主线程核对源码、既有测试与 Fork 失败边界。该轮是文档修订，没有执行新 API，也没有重新运行产品测试。

| 候选 | 裁决 | 原因与最小替代 |
|---|---|---|
| EventHistory 专用公开仓库名 | simplify | 统一 Repository；内部 Journal/资源组织可以保留 |
| 库强制 E/S 交替、单个 PendingEvent | delete 新合同中的强制 | 编排归应用；连续消息与 fork 后连续 S 已给出实际用法 |
| 非泛型 Checkpoint 两种派生类 | keep | 用户直接需求；避免 kind 加一组任意可空图的无效组合 |
| 泛型分支入口与 BranchCheckout | simplify | 采用非泛型基础入口；通用工具无需指定领域根类型，模型验证仍保留 |
| 公共 typed wrapper / 新角色接口 | defer / delete | cast 足以支撑当前故事；角色由提交入口表达，不另造领域继承要求 |
| 同 exact State 根类型限制 | delete | 用户已选择允许跨类型；保留实际模型、完整来源与发布后安装，领域限制归应用 |
| Event-first 初始化 | keep | 用户已选择；同一工作副本允许无 State，首 E/首 S 与冷开基线规则一起交付 |
| Fork 先发布 ref 再恢复的字面顺序 | simplify | 成功效果等价不代表失败行为等价；恢复失败不得遗留新 ref，发布后失败仍查实际结果 |
| PreviousX 递归 Checkpoint 链 | simplify | 直接领域根加位置；限制一次便利读取的内容范围 |
| 默认 Checkpoint 使用 ReadPair mutable 共享 | delete | 会在正常编辑 PreviousState 时改掉 Event；改用独立物化，保留显式 ReadPair |
| 统一恢复/物化机制 | keep | 共用机制不意味着共享相同实例；不用 Clone 或另一个持久引擎 |
| nearest PreviousEvent | keep | 用户已选最近严格祖先；区间新增事件用 EnumerateEvents，不加查询策略模式 |
| 库级 AppliedThrough / 自动重放 | defer | 未找到需提升为通用库规则的消费者；处理位置与完整运行状态先在领域 State 中表达 |
| 只到最近 State 的唯一事件枚举 | simplify | 固定历史结尾与可选起点，覆盖局部区间和跨 State 消息 |
| 不可变 tag / 外部可序列化地址 | keep 独立 tag 分片 / defer 外部地址 | tag 持久权威归上游 EventJournal，DG 依包接入；首片仅同打开地址，不引入全局仓库身份 |
| 为公开类型调整而整体重组程序集/包 | defer | 命名空间与程序集可分开；先验证完整消费代码 |

明确的立场修订：所有审阅者最终支持默认 Checkpoint 可变隔离；曾主张只读共享的 reviewer 撤回优化优先意见。
PreviousEvent 的初评分歧曾归为产品偏好，现由用户选定 nearest，已结束该项待决。
非泛型复核中，需求审阅者撤回了“旧 exact 检查证明必须永久禁止异构 State”的论据：它只证明不能机械删检查。
模型准入、原实例安装与发布失败合同有代码/测试依据；跨类型 State 与 Event-first 由用户独立确认。
本轮三角色复核还修正了“跨类型新根必分配新 ID”的过度概括：已有 child 升为根应保留 ID。
无 State 请求也不能查用、验证或清除另一个分支的 State 缓存槽；否则会误造 State，或被未请求图的 Schema 冲突阻塞。

## 8. 最小垂直实施验证与旧路线处理

以上公共语义已确定；[DB-078](0078-editable-checkpoint-fork-slice.md) 提供 A/B/C 独立实施合同。
以下是跨片验收目标，不代表已执行测试或自动开始产品施工。

1. 一个真实 PackageReference 消费者，能以统一 Repository 和非泛型 BranchCheckout 分别从 S0 与 E0 创建分支、独立 E/E/S/S 提交、重开并读取指定历史 Checkpoint。
   分支工具调用端不引用具体领域类型，模型由宿主配置；验证无共同领域基类的 E/S，以及同一已注册 CLR 类型可分别充当两种角色。
2. PreviousX 的选点与 null 合同、字段实际类型检查、双图 mutable 隔离与稳定 getter；不得递归加载祖先领域图。
3. 同一点的 Event 小读与逆序提前停止；跨 State 的 LLM 消息案例；物理交错多分支仍按选中逻辑祖先读取。
   验证非祖先下界在首项前拒绝、foreach 内 ReadEvent、两次 MoveNext 间推进分支仍固定结尾、Dispose 后 MoveNext 拒绝。
4. State-only、有前置 State 的 Event、尚无 State 的 Event 三种 named Fork，源工作副本仍存活；应用明确完成 lineage/执行阶段与消息处理选择，子图可独立保存。
   保持精确历史 Head；所需模型缺失或恢复失败不留新 ref，发布后交付失败则按实际持久结果恢复。
5. 改后的 Parent/baseline、Event-only ID 重合、失败后的实际发布点、fault/reentry/Dispose 与只读打开回归；不删除完整数据验证。
6. 无 State 时无参提交在 Capture/追加前拒绝；空模型环境可从合法 Event-only 历史 Checkout/Fork，零领域物化。
   `E0→E1→首 S→异型 S→无参 S` 冷热续写正确；首 S 前后发布失败仍按真实 outcome/fault 重开，不自动执行初始化。
7. 同型与异型替根均安装传入原实例，旧已取根不自动跟随；已有 child 升根、旧根变 child、共享子对象保持 ID，只有不可达成员移除。
   后继无参提交、冷读类型与值正确。null/未注册直接根前置拒绝；深层错误按其实际阶段验证，不抹除可能的 Schema 写入或 fault。
8. tag 在 DB-084 独立验收跨重开固定位置、同名冲突、branch Move 后不变、只读/坏尾与持久确认顺序；不作为 078 核心交付前置。

首片代码规模应在实际拆施工文档时再评估，不能以“只是改名和删除 if”估算。
本轮已将 [DB-076–082](0076-efficient-graph-fork-technical-path.md#10-分片施工导航) 校准为同一候选目标：
DB-077 只固定模型与 current 恒等；DB-078-A/B/C 分别交付公共基础/自由历史、Checkpoint/查询、多分支 Fork，分别实施验收。
DB-079 统一按用途准备/物化；DB-080 仅驻留最近 State 的一份材料，逐请求独立验证 Head；
DB-081 只将具备冷热等价证据的 State 提交产物放入同槽，Event 不动槽；DB-082 仅在该 State 材料范围实验 immutable 叶复用。
优化不影响本稿可观察语义，也不强迫工作副本装载事件。Event-first、异型 State 和同打开地址由 078-A 交付，
nearest PreviousEvent 由 078-B 交付；tag 的上游交付及 DG 接入单独跟踪。
地址 CLR 表示已由 A 选定；剩余内部取舍是恢复证书的可证明覆盖范围和缓存实测收益；均有明确合同/回退或停点，不再作为未定产品语义。
