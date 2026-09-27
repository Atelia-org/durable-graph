# LLM tool-loop：从使用场景推导 Checkpoint API

> 状态：目标 API 的应用检验，**未实施**；2026-09-27 按已选语义校准。
> 统一目标合同见[主文](../0083-repository-checkpoint-api-user-stories.md)。
> 以下为 C# 伪代码；`AgentLogic`、消息和运行状态类型均由假想应用定义，不代表任何厂商 API。

## 1. 应用需要保存什么

应用有一条消息历史：system、user、assistant、tool、assistant，以及配套运行状态。
用户已明确：**完整运行状态保存在 State；Event 只记录消息或操作。**
State 持久保存有效 system prompt、模型选择、推理强度、执行阶段、必要的待完成工具调用和当前上下文信息。
Event 带应用定义的 MessageId / ToolCallId，以便业务定位消息、关联结果；这些不是库内处理进度协议。
它们是应用持久保存的领域 ID，不是跨检查点唯一的 ObjectId，也不是把仅在当前打开 Repository 内有效的 CheckpointAddress 存进 State。

```csharp
// 领域概念草图，省略 Durable 声明和 Schema。
AgentMessage {
    string MessageId;
    MessageRole Role;
    MessageContent Content;
    string? ToolCallId;
}

AgentState {
    EffectiveSettings Settings;
    AgentPhase Phase;
    PendingToolCall? PendingTool;
    MessageProgress Progress;
    ContextWindow Context;
    string ExplorationId;
}
```

`Progress` / `Context` 的具体字段由应用选择，例如已处理输入的 MessageId、窗口起点或摘要。
冷开直接恢复最近保存的完整 State，不需 replay 整条历史来重建配置和状态机。
若 State 后还有 Event，它们是否仍待处理由业务协议判断；读取消息、处理少量未完成输入，不等同于从历史重建 State。
这里的“无需 replay”也不表示无需读取持久图、内部 Delta 或下一次模型请求需要的消息。

## 2. 本案例采用的读取与保存合同

- `ReadCheckpoint(address)` 返回非泛型 `EventCheckpoint` 或 `StateCheckpoint`。
- `EventCheckpoint` 提供 `Event`、可空的 `PreviousState` 和 `PreviousStateAddress`；尚无 State 时后两者同时为 null。
- `StateCheckpoint` 提供 `State`、可空的 `PreviousEvent` 和 `PreviousEventAddress`。
- PreviousX 是领域根，配套地址定位其历史位置；不递归返回另一个 Checkpoint 对象。
- 同一 getter 稳定返回同一份已交付图；eager/lazy 是内部选择，所指历史位置始终由传入 address 决定。
- 一次 Checkpoint 内两图的可变对象隔离；各图内部的别名和循环保持。
- 普通读出图允许应用修改，修改不自动保存；保存通过工作副本的 `CommitState(nextRoot)` 显式完成。
- 首片地址只在签发它的打开 Repository 内有效，不提供可序列化的外部地址。重开后通过持久分支 ref 的 `GetHead(name)` 重新取地址；不可变 tag 已选定为独立目标，见 [DB-084](../0084-eventjournal-immutable-tags-slice.md)，本例不假定已有 tag API。

工作副本初次签出的 `State` 来自最近 StateCheckpoint；若尚无 State 则为 null。Event 提交不会隐式安装或替换 State。
下面运行故事以应用已经建立 AgentState 为前提，应用转换时应拒绝 null 或不支持的阶段类型。初始化输入也可由 `CreateBranchFromEvent(name, initialEvent)` 先保存，随后由应用显式创建完整 AgentState 并调用 `CommitState(state)`；库不自动执行初始化消息。
应用需要时可将真实的 SetupState 替换为 AgentState；库允许跨实际类型，后继无参提交保存新根，领域类型约束由应用负责。
BranchCheckout 与分支入口非泛型；应用在业务入口将 `State` 检查/转换为 AgentState，模型配置仍须提供。
下文 `Require<T>(IDurableObject?)` 是应用辅助函数：以模式匹配检查实际类型，null 或不匹配时抛出应用使用错误；不是库级 typed wrapper。
成功提交替换根后，先前取得的局部引用不自动更新；下一处理边界重新读取 `work.State`。
任意两个工作副本之间不得由应用自行接入共享的可变领域对象，否则独立演化的前提不成立。

## 3. 故事一：直接保存配置，冷开无需寻找旧配置消息

用户修改模型、推理强度或 system prompt，应用直接更新 State 并保存。纯配置保存不需要制造配套 Event。

```csharp
using (var repo = Repository.OpenExisting(path, models)) {
    using var work = repo.Checkout("main");
    var state = Require<AgentState>(work.State);
    state.Settings = newSettings;
    work.CommitState();
}

// 后续进程重新打开；不沿用上一次 Repository 签发的地址。
using var reopened = Repository.OpenExisting(path, models);
var checkpoint = reopened.ReadCheckpoint(reopened.GetHead("main"));
var restored = checkpoint switch {
    EventCheckpoint { PreviousState: AgentState state } => state,
    StateCheckpoint { State: AgentState state } => state,
    _ => throw new InvalidOperationException("不支持的领域检查点内容。")
};
```

`restored.Settings` 是已保存的完整有效配置，不需要倒序查找最后一条“更换模型”或“修改 prompt”消息。
即使之后记录了 Event，读出的 State 仍是最近已保存版本；未保存的配置修改不会因为 Event 提交而得到保存。
应用如需审计配置操作，可以另外记录 Event；两个独立提交不构成联合事务。

## 4. 故事二：连续记录消息，再保存 State

连续消息不必人为插入 State。应用可以记录两个输入，再批量处理并保存完整运行状态。

```csharp
using var repo = Repository.OpenExisting(path, models);
using var work = repo.Checkout("main");
var state = Require<AgentState>(work.State);

var first = AgentMessage.User(firstMessageId, "调查 A");
var firstAddress = work.CommitEvent(first);
var second = AgentMessage.User(secondMessageId, "补充约束 B");
var secondAddress = work.CommitEvent(second);

// 应用同时更新运行状态及自身的 MessageId 处理进度。
var nextState = AgentLogic.HandleInputs(state, first, second);
work.CommitState(nextState);
```

`HandleInputs` 是应用逻辑；本例假设它构造完整替换根，并记录这两个输入的处理进度。
两次 Event 提交分别推进 Head，但不安装 State 保存基线。最后一次提交保存应用交来的替换根。
若进程在保存 State 前停止，冷开只取得旧 State；应用依据 Progress 与 MessageId 识别仍需处理的消息。
这段待完成工作必须显式执行，库不会自动处理 Event，也不能把“读取最后一条 Event”说成恢复了新的 State。
库不提供隐含的 `PendingEvents` 或 `AppliedThrough`，也不从 State 的写入位置推导“前面都处理完了”。

## 5. 故事三：工具调用中断后识别继续位置

```csharp
// 在外部调用前保存恢复所需的调用 ID、参数及执行阶段。
var toolState = Require<AgentState>(work.State);
toolState.PendingTool = new PendingToolCall(callId, toolName, arguments);
toolState.Phase = AgentPhase.AwaitingTool;
work.CommitState();

var result = await InvokeTool(callId, arguments);

var resultMessage = AgentMessage.ToolResult(resultMessageId, callId, result);
work.CommitEvent(resultMessage);
var completed = AgentLogic.AcceptToolResult(toolState, resultMessage);
work.CommitState(completed);
```

`AcceptToolResult` 更新运行阶段、待完成调用及应用处理进度；Event 和完成后的 State 是两个独立提交。
若结果 Event 已保存、State 尚未保存，冷开得到 AwaitingTool State，应用凭 ToolCallId 和阶段识别已记录结果，完成这一步后再保存 State。
若完成 State 已保存，则从新的阶段继续，不因 PreviousEvent 仍指向该结果就再处理一次。
外部工具已产生副作用、结果尚未保存时，库不能由历史推知实际执行情况；调用 ID、查询或重试由应用安排。
无需为此在 DurableGraph 加入事件执行器或分布式 exactly-once 协议。

可变隔离在这里具有具体价值：State.PendingTool.Arguments 与 Event.ToolRequested.Arguments 可能在持久视图中同 ID/head。
若用 ReadPair 的可变共享来交付 Checkpoint，应用修改 PreviousState 中的重试参数，会连带修改读出的原请求事件。
默认独立物化应保证两者互不影响；将来只共享已经证明不可变的实例，不改变这个使用合同。

## 6. 故事四：历史 fork 后修改配置继续探索

```csharp
using var experiment = repo.Fork("reasoning-high", selectedAddress);
var selected = repo.ReadCheckpoint(selectedAddress);

var next = AgentLogic.CreateExplorationState(
    Require<AgentState>(experiment.State), newExplorationId, alternativeSettings);
experiment.CommitState(next);
```

`CreateExplorationState` 是应用构造独立分支内容的操作，不是库级 callback；不能把另一个活动工作副本的可变对象接到新分支。
若选中 Event，Fork 初始 State 是该点最近 StateCheckpoint；源工作副本在 Event 后尚未保存的业务修改不被克隆。
当 `selected` 匹配为 EventCheckpoint 时，其 Event 可供业务识别继续处理的消息。
应用仍需按自身进度决定是否处理它，以及此前是否还有其他未完成消息；选中某条 Event 不证明只有这一条需要处理。
保存探索身份/配置时保留原 MessageProgress，不自动确认这些消息；尚未处理的消息可能位于这个新 State 之前，后续查询不能一律在最近 State 截断。
若选中 State，探索身份或配置变化可直接再提交 State；连续 State 是正常用法。
库不会自动更新领域 ExplorationId，也不会 replay 后续事件来猜测应用期待的状态。

## 7. 故事五：按需组装跨 State 的消息上下文

```csharp
var messages = new List<ModelMessage>();
foreach (var address in repo.EnumerateEvents(
    selectedAddress, order: HistoryOrder.NewestFirst, afterExclusive: null)) {
    var domainEvent = repo.ReadEvent(address);
    if (domainEvent is AgentMessage message) {
        messages.Add(ToModelMessage(message));
    }
    if (EnoughContext(messages)) {
        break;
    }
}
messages.Reverse();
```

循环元素就是地址；`ReadEvent(address)` 只读该 Event 的领域图，不附带物化 PreviousState。
固定的 `selectedAddress` 决定逻辑历史结尾，不受随后分支 head 推进影响。
在 `S0 → user → assistant → S1 → tool → assistant` 中，S1 不意味着早期对话消失；上下文查询必须能跨 State。
消息摘要、压缩边界和窗口起点由应用决定。`afterExclusive` 可表达已知历史边界，但不能直接按地址数值比较来判断分支祖先。
“回到前一个 State 就停止”的事件区间便利 API 可以后续再议，不替代这个跨 State 的查询需求。

## 8. 最低验收与应用边界

1. 连续 Event、连续 State、替换 State 根均可保存；Event 提交不安装 State 基线。
2. 已建立 AgentState 后，无论 head 为 State 还是 Event，冷开都可取得最近已保存 State 的完整配置、phase 和工具调用位置，无须历史 replay 重建它们；无 State 的初始化前缀则明确返回 null，不虚构运行状态。
3. 修改 Checkpoint.PreviousState 的可变子图不改变同一 Checkpoint.Event；重复 getter 不产生新图。
4. 普通读图修改不会推进任何分支；显式提交替换根后，重开取得新 State。
5. 从历史 Event/State fork 均保持源分支不变；应用可为新分支改变领域身份并连续保存 State。
6. 消息枚举遵循固定结尾的分支逻辑历史，可跨 State、可停止；ReadEvent 不额外物化 PreviousState，仍须恢复 Event 自身实际可达的图。
7. 结果 Event 与完成 State 之间中断时，可依据 State 中的 ToolCallId/Phase 与结果消息完成待办；已完成 State 不因 PreviousEvent 存在而被重复处理。

PreviousEvent 已选定为最近的严格祖先 Event；`S0 → E1 → S1 → S2` 中 S2 仍指 E1，`E0 → E1 → S0` 中首 State 指 E1。UI 若只展示两次 State 之间新增的消息，应使用显式范围查询，不能改写通用 PreviousEvent 的语义。PreviousX 只表达历史关系，不能用于判断业务处理完成或证明 State 的因果来源。
