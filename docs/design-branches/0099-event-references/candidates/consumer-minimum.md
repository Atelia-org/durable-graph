# 消费者最小候选：应用持久化 Event 根地址，投影与缓存留在 Agent

> 独立需求审查，未阅读其他候选。依据：DG `66f2cf3`（DB-098 产品 `a8f6ed1`）、Agent `437a586`；Agent 仍固定旧包。本稿是待验证候选，不宣称已经实现或测得收益。

## 需求与当前证据

目标是长载荷只在 Event 中持久化，State 保存当前选择、执行进度和地址；允许内存冗余。并不要求 Event 与 State 共用 CLR 实例。当前 [AgentState](../../../../../atelia-console-agent/src/ConsoleAgent/AgentState.cs) 持久化 `Messages/Calls/Results/Settings`；[SavedMessages](../../../../../atelia-console-agent/src/ConsoleAgent/SavedMessages.cs) 把完整 Action 存为 JSON，并另存工具 Input。故只替换 History，会留下 Calls 的长输入和 Settings.Instructions。

[DB-098](../../0098-readpair-retirement/README.md)已退役 ReadPair；现行 [LoadEvent](../../../../src/DurableGraph.Persistence/Repository.cs) 独立恢复 Event 根。[CommitId](../../../../src/DurableGraph.Persistence/CommitId.cs)支持同库重开后的 12 字节编码，但解码不证明存在、角色或来源库。这已足以做应用方案，无需恢复跨图身份共享。

## 最小模型与完整所有权

应用定义 durable `EventLocator(CommitHex, ExpectedSequence)`，`CommitHex` 用 `TryWrite/TryRead` 对称转成固定 24 位十六进制；通过现有 SG 保存普通字段。History/Call/Result 用应用自己的具体条目表达投影，不引入任意字段路径解释器。缓存保存 Event 根及已解析的 Action，State 中缓存字段显式 `[Transient]`，或放在非 durable 的 AgentSession 中；它们均不进入 Capture。首版由 Session 显式补齐全部当前有效引用，再交付可运行 State，`State.History` 不暗中读盘。[Transient](../../../../src/DurableGraph/TransientAttribute.cs)恢复后为默认值，不能依赖构造器初始化缓存。

| 载荷/进度 | 唯一持久化所有者与 State 内容 |
|---|---|
| 用户输入、History observation | `UserInputReceivedEvent.Input`；History 存该 Event 地址 |
| 模型正文、native reasoning、工具调用原文 | `ModelResponseReceivedEvent.Action.Json`；成功 History 存根地址；未完成响应仍只留 Event，不进入成功历史 |
| Pending Calls | 指向上述 Action 的地址 + 工具调用序号；从缓存解析 Name/CallId/Input/协议，不再持久化 `SavedToolCall.Input` |
| 实际工具输出、Pending Results、完成批次 | `ToolResultReceivedEvent.SavedResult`；进行中列表和历史批次都存结果引用，批次结束只转移引用 |
| 中断产生的缺失结果 | 当前 [PrepareInterrupt](../../../../../atelia-console-agent/src/ConsoleAgent/AgentStateMachine.cs)由调用和阶段合成，不是 ToolResultReceivedEvent。首版 State 存 CallRef + “执行中未知/未开始”模板代码，固定短文由应用 Schema 版本确定；真实已知结果仍引用原 Event。若要求合成全文也归 Event，就把补齐结果加入 TurnInterruptedEvent，由应用生成，仍不需要 DG 投影功能 |
| Settings，尤其可能很长的 Instructions | `SessionStartedEvent.Settings`；State 持有该 Event 地址。MaxRounds 等少量控制值可投影复制；不能保留另一份 Instructions |
| reset、retry、started、完成状态 | 小 Event 自有原因/序号；State 自有 Version、Phase、计数、Outcome、ActiveOperationId 和进程高水位。reset 清当前条目及其缓存，保留设置引用和高水位，历史 Event 不删除 |
| streaming delta、客户端、凭据、进程句柄 | 不持久化；完整已知响应归响应 Event，流片段只展示；外部副作用不由地址机制重做 |

这些所有权来自 [AgentEvent](../../../../../atelia-console-agent/src/ConsoleAgent/AgentEvent.cs)、[状态转移](../../../../../atelia-console-agent/src/ConsoleAgent/AgentStateMachine.cs)与 [AgentLoop](../../../../../atelia-console-agent/src/ConsoleAgent/AgentLoop.cs)。同一内容被用户再次提交为新 Event，不自动内容去重；承诺是消除同一已接受载荷的 Event/State 双存。

## 接受、恢复与缓存边界

现有 [Dispatch](../../../../../atelia-console-agent/src/ConsoleAgent/AgentSession.cs)顺序是 `Prepare → CommitEvent → apply → CommitState`。`Prepare` 时尚无新 CommitId；[CommitRequest](../../../../src/DurableGraph.Persistence/CommitRequest.cs)也不预分配地址。应用把返回闭包改为 `apply(acceptedOrigin)`：Prepare 用输入 Event 校验并准备投影，保持可达 State 不变；CommitEvent 正常返回后才编码地址、绑定本次来源并 apply，最后保存只有地址的 State。不得把候选 FrameAddress 或异常中的候选 ID 当成已接受引用。

冷开先取得 WorkingCopy 的最近 State，用固定历史边界的元数据查询 Take(2) 检查尾长度；多于一个 pending Event 时，在解析任何引用或加载尾 Event 前拒绝。随后补齐有效引用，用唯一尾 Event 描述的真实 CommitId 绑定并 Prepare/apply，补一个 State。第二次重开不得重放。引用解码后用 LoadEvent 验角色，再校验预期事件类型、Sequence 和调用序号；任何缺失或不符都拒绝交付可运行 State，不拿缓存掩盖错误。DG 不替应用判断 Event 已处理，参见 [WorkingCopy](../../../../src/DurableGraph.Persistence/WorkingCopy.cs)。

纯内存 Dispatch/Replay 用仅属于本次会话的来源令牌和已给定 Event 缓存，不伪造 CommitId；持久会话只接受成功提交后绑定的 locator，并在保存前拒绝未绑定令牌。纯 Replay 不开 Repository。现有[每前缀重放/Prepare 无修改检查](../../../../../atelia-console-agent/tests/StateMachineChecks.cs)应保留，并比较业务指纹，排除缓存和物理地址。

缓存按当前 Repository owner + 完整 CommitId 分组；Sequence 只能用于内存会话，不能作跨分支缓存键。热路径可使用刚成功提交且应用保证不再修改的 Event，不必强制读回。任一提交/应用失败就停用 Session 并丢弃其缓存；重开按实际 Head 重建，不能借暖缓存继续执行。现有[部分修改后冷恢复](../../../../../atelia-console-agent/tests/MutablePersistenceChecks.cs)和[真实保存边界检查](../../../../../atelia-console-agent/tests/PersistenceChecks.cs)是必须保住的行为；本次仅阅读，未运行。

## 编辑、摘要、分支的消费者反例

用户编辑产生新 Event 和新地址，替换当前选择；禁止原地改缓存对象冒充编辑。摘要同样是新 Event 自有的新载荷，State 替换选入上下文的引用；旧明细是否仍被当前 State 引用，由应用产品语义决定。首版只补齐有效引用，不扫描 reset/摘要淘汰的全部旧正文。

同库 Fork 可以继承祖先引用；两个分支的新 Event 即便 Sequence 相同也必须解析到各自 CommitId，缓存不能串线。[ForkBranch](../../../../src/DurableGraph.Persistence/Repository.cs)只登记历史，Agent 当前只支持一个分支，实际分支 UI/编辑/摘要均尚未实现。跨库导出必须复制所引用 Event 并重写地址，或明确拒绝；12 字节地址不是通用文档 ID，也没有垃圾回收/跨库搬运合同。

## 各候选必须回答的验收问题

1. 给一次响应放入 1 MiB 工具输入，再返回 1 MiB 结果：State 的 Action、Calls、Results、完成批次和 Instructions 中是否还有原文？不能只证明 History 变小。
2. Prepare 校验失败时是否仍零领域修改、零尾 Event？成功后才有地址，能否不改变这个顺序？
3. 在 Event 保存后、apply 部分修改后、State 已发布但回调抛错处分别终止：每次冷开是否只处理实际待办一次，且失败缓存永不执行下一工具？
4. 不打开磁盘的逐前缀 Replay 是否与持久恢复得到同样业务内容，包括 native reasoning、text/JSON 协议、失败响应、中断的未知/未开始区别？
5. 空缓存下给 State 角色、错类型、错 Sequence、越界调用序号，是否在工具运行前稳定拒绝？受控异库用例能否展示现有误用诊断，暖缓存是否按正确 owner 隔离？跨库检测只是概率诊断，不承诺所有异库引用必被拒绝。
6. reset 后设置与高水位是否保留？摘要只留下少量引用后，冷开是否仍物化所有旧长消息？历史只读模式能否在坏尾 Event 下继续检查，不触发恢复写入？
7. 同一祖先 Fork 两线，各接收相同 Sequence、不同正文；编辑一线、清缓存并冷开，另一线的内容和旧历史是否完全不变？
8. 若库只新增 `EventRef<TEvent>` 根引用，消费者仍需多少投影代码？若再支持字段级引用、自动装配/懒加载，哪项真实验收是根引用加应用缓存无法满足的？

## 可测成本与何时值得内建

首版冷开成本是有效引用涉及的 U 个不同 Event 的整图恢复与 Action 解析；缓存后每个源最多加载/解析一次，但内存仍可同时保 JSON、解析对象和历史投影。State 有 R 个条目，保存仍需捕获/比较地址图，不能声称 O(1)。hex 的 24 字符、引用对象头、Schema 和批次成本都应实测，不能把 12 字节编码大小当成实际磁盘开销。当前[历史测试](../../../../../atelia-console-agent/tests/HistoryChecks.cs)已经区分选中 Event 的加载数与全部历史，不能因新功能退回无界正文扫描。

用固定事件数、固定条目数，将单载荷从 1 KiB 放大到 1 MiB，分别计 Event/State 写入字节、源加载次数、JSON 解析次数、冷开时间、热保存时间、分配和驻留内存；另用 100/1000/10000 条轨迹、reset/摘要后的小活动集测试。预期 State 增长不再跟长载荷线性增长，整个库仍要保存 Event 本体；没有实测前不报节省比例，也不误称当前每次 State 都重写全部历史。

建议以此作为增强 DG 的对照基线。只有实际消费者反复需要统一的 durable 根引用、角色/来源验证、生命周期或批量解析，或测量证明应用逐根 LoadEvent 成本主导时，才有足够理由内建相应窄能力。库内建仍应先回答上述问题；自动字段投影、跨库引用、通用 Blob 和恢复旧共享机制均不能从本需求直接推出。
