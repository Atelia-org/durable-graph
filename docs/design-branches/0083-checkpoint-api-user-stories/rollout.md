# DB-083 用例：模拟 rollout 与反事实实验

> 状态：目标 API 的应用检验，未实施；2026-09-27 按已选语义校准。
> 本用例是潜在应用夹具，优先级低于用户已明确需要的 DramaBoard 与 LLM tool-loop。
> 公共目标合同以[DB-083 主文](../0083-repository-checkpoint-api-user-stories.md)为准；下列 C# 是示意代码。

## 1. 本用例检验什么

模拟应用希望保存一个可继续推进的领域对象图，从同一已提交位置探索不同动作，再读取事件样本分析结果。
本轮只用它检验三个通用性质：不记录 Event 也能连续保存 State；同点不同分支彼此独立；单读 Event 不额外物化关联 State。
它不要求 DG 提供 Gym 接口、实验调度器、回放器、分支合并、并行执行或结果汇总框架。
分支入口与 BranchCheckout 非泛型；模拟业务自行转换 State 类型，模型注册仍是持久化前提。库允许无 State 前缀及跨实际类型 State 替换；本例选择从已建立 SimState 的种子开始，所有转换均为模拟应用自己的类型约束。
下文 `Require<SimState>` 是应用辅助函数，以模式匹配拒绝 null 或错误根类型，不是库的泛型工作副本。

`SimState` 是应用定义的 durable 根，包含模拟时钟、对象图、所需随机数生成状态及环境配置。
是否还须保存规则版本、外部输入或模拟器内部状态，由应用确定；DG 不克隆系统 RNG、进程、GPU 或外部服务。
保存了对象图，不等于已经证明模拟可重复；本例也不声称 fork 已达到某个实测速度。

## 2. 故事一：没有 Event 的连续 State 保存

作为模拟应用，我只想定期保存完整的逻辑状态，并在重开后继续运行，不希望构造无业务意义的 Event。

```csharp
using var repo = Repository.OpenExisting(path, models);
using var seed = repo.CreateBranch("seed", initialState); // 已发布 S0
var state = Require<SimState>(seed.State);

Advance(state);
var s1 = seed.CommitState();

state.TimeScale = 0.5;
var s2 = seed.CommitState();

SimState next = Reduce(state, nextInput); // 应用也可采用返回新根的写法
var s3 = seed.CommitState(next);
// seed.State 现为 next；state 仍指旧根，下一步应重新取 seed.State。
```

历史为 `S0 → S1 → S2 → S3`。每次成功的 State 提交推进分支 head，并安装此次保存基线。
`CommitState(next)` 的根资格仍按编辑工作副本合同核对；库不把应用的 fold 变成自动处理协议。
`CreateBranch` 保留调用者传入的初始领域实例，应用自己造成的外部别名不因此消失。

冷重开时从持久分支重新取得工作副本：

```csharp
// 前面的工作副本和 repo 已关闭；这是另一次打开。
using var reopened = Repository.OpenExisting(path, models);
using var resumed = reopened.Checkout("seed");
Advance(Require<SimState>(resumed.State));
resumed.CommitState();
```

验收：上述过程不产生 dummy Event；冷恢复值与保存值相符，后续保存成功。
这一条历史上各 State 的 `PreviousEvent` 均为 null，不能由库虚构一个“引发状态变化的事件”。

## 3. 故事二：同一检查点的多个 rollout

作为探索程序，我希望同时保留若干候选工作副本，串行推进它们，并各自保存结果。
源分支的工作副本仍可存活；同一分支仍至多有一个活动工作副本。

```csharp
var start = repo.GetHead("seed"); // 固定结尾点；后来 seed 的 head 移动不改变 start
using var left = repo.Fork("rollout-left", start);
using var right = repo.Fork("rollout-right", start);
var leftState = Require<SimState>(left.State);
var rightState = Require<SimState>(right.State);

leftState.LineageId = "left";   // 领域谱系属于应用
left.CommitState();
rightState.LineageId = "right";
right.CommitState();

Step(leftState, actionA);
left.CommitState();
Step(rightState, actionB);
right.CommitState();
```

Fork 正常成功的效果是新持久 ref 指向 start，并交付独立恢复的可保存工作副本；不读取源工作副本的未提交字段。
实现先完成所需恢复与工作副本准备再发布 ref，普通恢复失败不留下新分支；发布后交付失败则须按 outcome/fault 检查实际结果。
上例修改 LineageId 后直接提交，是连续 State 保存的真实用途；DG 不自动分配或重写领域谱系。
分支名、实验编号及候选比较由应用管理。这里的多个工作副本不表示仓库允许操作并发。

验收：修改 left 的可变图不改变 seed 或 right；两边保存后分别冷重开，得到各自结果。
若应用保存了完整的确定性模拟状态，同一动作可用于检验重复性；这项证明属于模拟应用。
`start` 是仅在本次打开的 repo 中有效的 `CheckpointAddress`，重开后须从 ref 重新 `GetHead`。
不可变 tag 已纳入独立目标，见 [DB-084](../0084-eventjournal-immutable-tags-slice.md)；本例不假定产品已经有 tag，也不把底层 Revision 地址当作可跨重开的检查点书签。首片不提供可序列化的外部地址。

## 4. 故事三：只读事件样本，再选择性展开上下文

作为分析程序，我通常只需要一个小事件中的动作与奖励，不希望为了它恢复整个模拟世界。
本例的应用约定：每次模拟步从已保存 State 出发，记录结果 Event 后再保存下一 State。
这只是本应用的安排，不是仓库强制协议。

```csharp
TransitionSample result = SimulateOneStep(Require<SimState>(left.State), observedAction);
var sampleAt = left.CommitEvent(result);
left.CommitState();

var sample = (TransitionSample)repo.ReadEvent(sampleAt);
Display(sample.Action, sample.Reward);
```

`ReadEvent` 只恢复事件图，不为导航便利额外 Allocate/Hydrate 前置 State。
这不保证完全不读 StateStore 字节；事件自己的引用闭包与存储依赖仍须读取和验证。
若应用把整个世界作为 Event 的持久引用，读取该事件自然可能很大；本例的样本只保留分析所需内容。

只有需要上下文时，才使用非泛型的便利入口，并显式 cast 领域根：

```csharp
var observed = (EventCheckpoint)repo.ReadCheckpoint(sampleAt);
if (observed.PreviousState is not SimState before ||
    observed.PreviousStateAddress is not { } beforeAddress) {
    throw new InvalidOperationException("模拟样本必须来自已保存的 SimState。");
}
Inspect(before, (TransitionSample)observed.Event);

using var alternative = repo.Fork("counterfactual", beforeAddress);
Step(Require<SimState>(alternative.State), alternativeAction);
alternative.CommitState();
```

`ReadCheckpoint` 的两个领域图默认可变隔离；相同 ObjectId/head 不得导致可变实例跨图共享。
反复访问同一根属性取得稳定的同一图；它们也独立于活动工作副本。用户可初始化 Transient 或计算候选状态，
修改这些恢复结果不会自动保存、推进 ref 或安装工作副本基线。`PreviousX` 是领域根及对应地址，不递归装载历史。
上例通过 Fork 取得带保存语义的工作副本；专用 `ReadPair` 仍保留其独立的只读共享合同。

跨 State 的样本浏览可以用固定结尾点枚举，不因遇到 State 就终止：

```csharp
var end = repo.GetHead("rollout-left");
foreach (var at in repo.EnumerateEvents(end, order: HistoryOrder.NewestFirst)) {
    var row = (TransitionSample)repo.ReadEvent(at);
    Display(row.Action, row.Reward);
}
```

枚举返回轻量 `CheckpointAddress`；反例测试应使枚举本身不物化任何领域 State/Event。
正序可选 `HistoryOrder.OldestFirst`；范围下界用 `afterExclusive`，不从相邻 State 隐式推导。

## 5. 两个必须保留的反例

`PreviousX` 已选定为严格逻辑祖先链中最近的对应种类记录。
在 `S0 → E1 → S1 → S2` 中，`S2.PreviousEvent` 因而仍是 E1：它表示截至 S2 最近记录的 Event，
不证明 E1 导致 S2，也不证明 S2 又处理了一次 E1。查询 S1 之后的事件区间则为空，二者不能混称。

在 `S0 → E1 → E2` 中，从 E2 Fork 恢复的 State 仍来自 S0，head 则定位 E2。
库不自动应用两个 Event，也不能把“PreviousState 加当前 Event”当作所有应用的完整恢复配方。
若本例改为批量 Event，单个样本的业务 before 可能不同于其最近保存 State；应用须保存或计算所需进度。

本用例最终保留最小要求：自由保存、分支隔离、固定历史读取和事件小读；其余模拟平台能力不由本轮引入。
