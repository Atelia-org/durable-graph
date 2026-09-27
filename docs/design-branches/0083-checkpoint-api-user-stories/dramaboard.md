# DramaBoard：Occurrence 提交、检查点与分支探索

> 状态：Draft / API 用户故事，未实施。与 [DB-083 主文](../0083-repository-checkpoint-api-user-stories.md)共同评审；用语见[术语表](../../DurableGraph-glossary.md)。
> 下列 C# 是拟议调用示意，领域辅助函数不属于 DurableGraph API。当前源码符号及发布合同不因本文自动改变。

## 1. 已核实的应用事实

- 活跃 [IOccurrenceHistory](../../../../drama-board/src/Kernel/Journal/IOccurrenceHistory.cs) 自己规定 E/S 交替、最多一个 PendingEvent、发布失败后重开。
- [SimulationKernel](../../../../drama-board/src/Kernel/Simulation/SimulationKernel.cs) 先 Plan、纯 fold 和验证得到 scratch，再分别提交 Event 与 State；State 成功才安装世界。恢复 pending 不重新 Forecast、Plan 或调用 Player。
- [KernelCursor](../../../../drama-board/src/Kernel/Simulation/KernelCursor.cs) 保存有限的已完成业务边界；[SimulationFork](../../../../drama-board/src/Kernel/Simulation/SimulationFork.cs) 的现行内存实验通过复制、重放前缀，并显式接收新 LineageId。
- 真实 DG 持久化 adapter 已归档，活跃 Server 使用内存历史，见[应用当前状态](../../../../drama-board/PROJECT-STATE.md)。这里推导新的接入方式，不声称它已经落地。

用户需要游戏自然交替；这不要求 Repository 强制所有应用交替。Occurrence、完成进度和 Pending 属于游戏 adapter 的协议。通用仓库只记录提交、恢复领域图和维护分支。

## 2. 本文采用的候选边界

Repository 固定模型环境；同分支最多一个活动 BranchCheckout，不同分支可以各有一个；所有仓库调用串行。`CommitState(nextRoot)` 必须保留，以支持当前的纯 fold / 替换根模型。
分支入口与 BranchCheckout 非泛型；示例中的 `Require<T>` 是应用类型检查，不是库的 typed wrapper。
成功替换根后，之前取出的 CLR 引用不会自动跟随，须使用新根或重新取得 `checkout.State`。

`ReadCheckpoint(address)` 返回非泛型 EventCheckpoint 或 StateCheckpoint。PreviousState / PreviousEvent 直接提供领域根及对应地址，不递归拥有完整 Checkpoint。同一视图重复访问属性返回同一图；加载时机暂不承诺 eager 或 lazy。

领域图默认 mutable 隔离：两图之间、与工作副本之间、不同读取之间不暗中共享 mutable 实例；图内 alias/cycle 保持。应用可以修改读取结果，但修改不会自动保存。ReadPair 是另外的显式只读共享入口；共同恢复机制不要求共同实例共享规则。

PreviousX 已选定为逻辑历史中最近的严格祖先对应项。`S0 → E1 → S1 → S2` 时 S2.PreviousEvent 仍为 E1；它不证明因果或已处理进度。下面故事按游戏自己的完成边界恢复，不根据 PreviousEvent 是否存在判断待办。

首片地址是当前 Repository 签发的 opaque `CheckpointAddress`，仅同一打开实例有效；重开从持久分支 ref 重新取地址，首片不提供可序列化的外部地址。不可变 tag 已纳入目标，由上游 EventJournal 独立分片实现后接入，见 [DB-084](../0084-eventjournal-immutable-tags-slice.md)，不表示当前产品已有 tag API。

库允许无 State 的 Event 前缀，也允许 State 根更换实际类型。本文普通 tick、游戏恢复与 Occurrence 浏览以游戏已经建立 SimulationState 为前提；`Require<SimulationState>` 拒绝 null 或其他类型是应用协议。初始化故事见 §9，不把这个应用前提提升为仓库约束。

## 3. 故事一：正常推进一次 Occurrence

需求来自真实 Kernel；Repository 放开交替，游戏 adapter 继续约束自己的运行流程。

```csharp
using var repo = Repository.OpenExisting(path, models);
using var main = repo.Checkout("main");
var state = Require<SimulationState>(main.State);

OccurrenceEvent occurrence = await PlanNext(state, cancellationToken);
SimulationState scratch = FoldAndValidate(state, occurrence);

// 最后一次普通取消检查在 Event 提交前；之后完成游戏自己的持久待办。
cancellationToken.ThrowIfCancellationRequested();
main.CommitEvent(occurrence);
main.CommitState(scratch);
kernel.Install(Require<SimulationState>(main.State));
presentation.PublishCompleted(occurrence);
```

新分支可由 `repo.CreateBranch("main", initialState)` 建立。State 提交正常返回才呈现一次完成的游戏变化；E/S 是两个发布动作。删除替换根入口会迫使应用建立稳定可变外壳或复制 scratch 回旧图，这是 API 限制造成的额外工作。

## 4. 故事二：以历史 Event 地址为结尾查看一次变化

用户从本次打开所得的历史地址中选择一个，读取结果固定于它，不随 branch 后续推进改变。

```csharp
Checkpoint checkpoint = repo.ReadCheckpoint(selectedAddress);
switch (checkpoint) {
    case EventCheckpoint e:
        var before = Require<SimulationState>(e.PreviousState);
        var occurrence = Require<OccurrenceEvent>(e.Event);
        var after = FoldAndValidate(before, occurrence);
        ShowTransition(before, occurrence, after);
        break;
    case StateCheckpoint s:
        ShowWorld(Require<SimulationState>(s.State));
        if (s.PreviousEvent is { } previous) {
            ShowEarlierOccurrence(Require<OccurrenceEvent>(previous));
        }
        break;
}
```

`Require<T>` 是应用类型检查。只有游戏自己的交替合同允许用前 State 和一条 Event 算出下一状态；一般的 `S0 → E1 → E2` 必须另行考虑 E1。`PreviousStateAddress` / `PreviousEventAddress` 用于定位便利根来自哪一项；它们不是业务因果证明。

mutable reducer 也可以修改读取出的 PreviousState，再显式 `checkout.CommitState(nextRoot)`。若 ReadCheckpoint 默认让 Event 的历史快照子图与 State 共享 mutable 实例，修改 State 可能改掉后续处理仍要读取的事件输入，因此默认隔离。

## 5. 故事三：从 State 分叉并初始化应用 lineage

需求来自廉价 fork；LineageId 是现有应用概念。例中 selectedStateAddress 明确选中一个 State。

```csharp
using var alternative =
    repo.Fork("alternate-choice", selectedStateAddress);

// DG 先恢复相同领域值；由应用决定是否替换 lineage、配置或种子。
SimulationState forkState = Require<SimulationState>(alternative.State).WithLineage(newLineageId);
alternative.CommitState(forkState); // S → S，不必编造一个领域 Event。

OccurrenceEvent alternate = await PlanNext(forkState, cancellationToken);
SimulationState scratch = FoldAndValidate(forkState, alternate);
alternative.CommitEvent(alternate);
alternative.CommitState(scratch);
```

这个 S→S 用例说明：即使游戏 tick 自然交替，通用仓库仍应允许独立保存。Fork 不自动重写领域 LineageId 或随机种子。main 可继续存活，两分支的可变世界独立。

从 E 点 Fork 则继承该已记录 Event，游戏应先完成它；如果想让 Player 重新选择事件，应从其前 S 分叉。Fork 不隐式丢弃 E，也不运行业务处理器。

## 6. 故事四：E 已发布、S 提交出现不确定结果

任一次提交异常后，游戏采取停止 Kernel、关闭并重开的保守策略；不能从旧 CLR 图或异常位置推断最终 head。重新打开后的示意：

```csharp
using var reopened = Repository.OpenExisting(path, models);
using var resumed = reopened.Checkout("main");
var state = Require<SimulationState>(resumed.State);
Checkpoint actual = reopened.ReadCheckpoint(resumed.Head);

switch (actual) {
    case StateCheckpoint:
        Validate(state);
        StartKernel(state); // 不重放，不重新询问 Player。
        break;
    case EventCheckpoint e:
        ValidateGameHistoryContract(e); // 游戏约束，不由 DG 猜测处理进度。
        var occurrence = Require<OccurrenceEvent>(e.Event);
        var recovered = FoldAndValidate(state, occurrence);
        resumed.CommitState(recovered);
        StartKernel(Require<SimulationState>(resumed.State));
        break;
}
```

此处 Checkpoint 与 checkout 的 State 独立，简单实现可能重复恢复前图；先保证语义，后续优化准备材料。失败后保留 append-only 与发布/fault 纪律；此例不提供外部副作用恰好一次。

现有 [SimulationKernelTests](../../../../drama-board/tests/Kernel.Tests/Simulation/SimulationKernelTests.cs) 的 `PublicationFailureStopsKernelAndResumeUsesActualBoundary` 覆盖前 E、后 E、前 S、后 S 窗口及恢复时不重新 Plan。本轮只核对源码，未运行这些测试。

## 7. 故事五：事件浏览跨过 State，处理范围则显式限定

浏览游戏轨迹时 State 不应截断 Event 历史。固定 end 后，只枚举地址，读取选中的 Event 不额外物化领域 State：

```csharp
CheckpointAddress end = repo.GetHead("main");
foreach (CheckpointAddress address in repo.EnumerateEvents(end)) {
    ShowRecordedOccurrence(Require<OccurrenceEvent>(repo.ReadEvent(address)));
    if (EnoughForCurrentPage()) { break; }
}
```

默认 NewestFirst。若应用要处理“最近 State 之后到某 E”为止的一段连续 Event，应明确上、下边界与顺序：

```csharp
var e = (EventCheckpoint)repo.ReadCheckpoint(selectedEventAddress);
var nextState = Require<SimulationState>(e.PreviousState);
foreach (CheckpointAddress address in repo.EnumerateEvents(
    e.Address, order: HistoryOrder.OldestFirst, afterExclusive: e.PreviousStateAddress)) {
    ApplyInPlace(nextState, Require<OccurrenceEvent>(repo.ReadEvent(address)));
}
```

第二段用于支持连续 Event 的应用；nextState 先由 e.PreviousState 准备，当前游戏 adapter 仍可拒绝非交替输入。枚举沿固定 end 的逻辑 parent 历史，不沿物理文件混合不同分支；区间约束不代表事件已处理。

## 8. 最小验收与延期

| 观察点 | 最小通过条件 |
|---|---|
| 普通 tick | 替换根成功；只有 S 正常返回后游戏安装、通知。 |
| S/E 独立提交 | S→S 与 E→E 可保存；游戏 adapter 自己保留严格交替。 |
| 历史读取 | 非尾地址固定选择；PreviousX 不递归恢复整条历史，不暗示因果。 |
| 图隔离 | 修改 PreviousState 不改变 Event、另一读取或活跃 checkout；图内 alias/cycle 保持。 |
| Fork | 原分支继续存在；领域初值相同、可变对象独立、lineage 不被库改写。 |
| 失败恢复 | 按重开后的实际 S/E 边界恢复；不重新询问 Player，不盲重放已完成 E。 |
| 浏览 | 跨 State 的 Event 查询与显式 suffix 查询分别正确；ReadEvent 不恢复无关 State。 |

业务已处理水位、通用自动 replay、外部调用恢复和恰好一次不进入仓库首片。不可变 tag 另见 DB-084，首片不提供跨重开外部地址；本文不增设 GraphSession、业务调度器或第二套持久化 authority。

## 9. 初始化请求先于世界 State

用户提出的 `InitWorldSetup` 是新的初始化接入目标，并非现有 Kernel 已实现的流程。初始化输入可以先持久保存；失败后保留输入供应用检查、重试或分叉，无需伪造空世界。

```csharp
using var setup = repo.CreateBranchFromEvent("new-world", initWorldSetup);
// setup.State == null；Head 已定位首个 Event，没有空 Head 分支。
setup.CommitEvent(additionalSetupInput);
SimulationState initial = BuildAndValidateWorld(initWorldSetup, additionalSetupInput);
setup.CommitState(initial); // 第一个非空 State；成功后保留传入原实例。
```

`CreateBranchFromEvent` 追加首个 Event；已有历史地址的分叉仍用 `Fork`。如果初始化在保存 State 前中断，重开或从该 Event 点 Fork 得到 `State == null`，库不自动重新运行初始化。应用按固定结尾显式读取初始化事件，决定下一步；无参 `CommitState()` 在无 State 时拒绝。

应用也可以先保存真实的 `SetupState`，再用 `CommitState(simulationState)` 切换到 `SimulationState`；库按各自实际类型的已注册模型保存。它是领域阶段转换，不是 Schema Upgrade。游戏 adapter 可以从进入 Kernel 起只接受 SimulationState；随后 `CommitState()` 保存已成功安装的新根。

验收须区分合法无 State 与损坏：Event-only 分支的 PreviousState/PreviousStateAddress 同时为 null，损坏图或未知模型按实际读取需求报错；首 State 前的 Event 不代表初始化已成功。
