# Event Ref 方案征集与评审约束

> 2026-10-25；设计任务，未授权本文中的候选自动进入产品实施。最终草稿与评选结果从 [README](README.md) 进入。

## 问题与事实基线

真实消费者 console-agent 把用户输入、完整模型响应和工具结果先保存为 Event，再放进 State 保存。用户接受内存中为了操作效率保留不同表示，但希望不可变大载荷不再随 State 重复持久化。用户提出以独立 Event 为载荷本体，State 保存引用、按需恢复内存表示；征集允许挑战这个方向，不要求所有候选为既定答案背书。没有兼容性包袱。

- DG 源码基线 `66f2cf3`，产品变更 `a8f6ed1`：[DB-098](../0098-readpair-retirement/README.md) 已退役 ReadPair，独立恢复、Checkpoint 守卫与 State 增量保留。
- Agent 源码基线 `437a586`：[Dispatch](../../../../atelia-console-agent/src/ConsoleAgent/AgentSession.cs) 执行 Prepare → CommitEvent → Apply → CommitState。Agent 仍固定 DB-098 之前的包；本轮没有切包。
- [CommitId](../../../src/DurableGraph.Persistence/CommitId.cs) 提供同库跨重开定位；[LoadEvent](../../../src/DurableGraph.Persistence/Repository.cs) 精确加载，不执行业务处理器。
- 旧文档关于 ReadPair 导致 Transient 不可变的限制不作为本轮前提。历史事件的 CLR 图仍不天然深度不可变。

## 五条独立路线

| 候选 | 独立问题 | 交付 |
|---|---|---|
| A | 最小显式引用及强类型读取，尽量复用现有 schema/保存机制 | [explicit-refs](candidates/explicit-refs.md) |
| B | 内建 lazy handle、绑定与缓存如何提高易用性 | [lazy-handles](candidates/lazy-handles.md) |
| C | 独立内容存储、CAS 或存储层去重是否优于 Event 本体 | [content-addressing](candidates/content-addressing.md) |
| D | 通用历史图／对象引用是否比专用 Event 根引用更合适 | [snapshot-references](candidates/snapshot-references.md) |
| E | 真实消费者的最小应用方案与反例，检验库抽象是否必要 | [consumer-minimum](candidates/consumer-minimum.md) |

首轮各自只读相关源码、独立写一个文件，不读其他候选。主线程汇总后再指定交叉审查，不通过共同编辑一份文档掩盖分歧。各候选保留提案身份，最终草稿是推荐决策的单一入口。

## 评审顺序

先判断正确性和真实需求覆盖，再比较复杂度与便利性，不使用无测量依据的数字评分。

1. **存储所有权**：State Base、容器重写、冷热缓存都不能重新捕获正文；不能只减少内存复制或压缩字节。
2. **恢复协议**：已发布 Event 与其引用的建立时序明确；失败后用准确历史恢复，不能靠原进程缓存、重放整段历史或预猜地址。
3. **消费者完整性**：覆盖 Messages、待执行 Calls、部分 Results、中断合成消息、Reset、纯内存运行；不能只替换一个 History 属性。
4. **来源与类型**：同库范围、实际 Event 角色、目标 Schema/升级、类型错误、fork 后来源均有解释。校验不能隐含全历史正文重读。
5. **内存语义**：明确缓存所有者、可变对象污染、构造器不执行、关闭/fault/reentry；捕获不能意外触发 lazy I/O。
6. **变更成本**：优先已有 inline/value 与恢复机制；新 TypeTag、通用外部依赖图、CAS、自动代理各需具体收益理由。
7. **可证伪**：提出最小可执行实验及失败时的收缩路线；区分源码依据、设计判断、未验证假设。

## 本轮交付边界

交付五份候选、主线程选择／融合草稿、交叉审查与未决问题。文档可在已有授权内本地提交；不改产品代码、不运行发布、不切换 Agent 包、不迁移或删除用户存档。文档链接检查只证明可导航，不证明机制可行；后续机制见证通过后才进入实施计划。
