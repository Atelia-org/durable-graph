# 接入与验收

[使用指南入口](README.md)

## 给 Coding Agent 的最小工作顺序

1. 写出一个真实恢复故事，例如“工具 A 的结果已保存，工具 B 执行中进程退出，重开后继续”。
2. 从故事列出必须保存的业务事实、执行进度和运行期资源，按[应用建模](application-design.md)分类。
3. 确定一个 owner、一个领域根和业务更新边界；先选 State-only，确有需要再加入 Event。
4. 按[根 README](../../README.md)用实际包建立最小模型，保存、关闭、冷开、原位修改、再次保存。
5. 再接入事件/状态机/I/O；明确结果未知时的策略。
6. 根据下表补充与应用承诺相关的验收，最后测真实规模。

不要先建设通用 reducer、事件总线、对象映射框架或第二套缓存。
如果现有领域模型与 DG 类型合同不匹配，先确认能否直接使用受支持的字段/容器；
必要的第三方消息适配应集中处理，并验证完整信息往返。

领域更新可以用普通方法组织。测试从独立初始世界出发，比较持久事实与业务不变量，
无需为了测试方便强制生产代码保留每一个旧内存 State。

## 验收应回答什么

| 验收故事 | 应观察的结果 |
| --- | --- |
| 保存后修改原对象，再读旧版本 | 旧版本的事实不受影响 |
| 图内多个路径引用同一对象，包含循环 | 冷开后保留必要 alias/cycle；业务身份仍正确 |
| 冷开后第一次调用业务方法 | 所需 Transient 已重建，没有依赖未运行的构造器 |
| 原位修改已有 List/子对象后保存 | 冷开结果正确；不要仅以“生成了 Delta”替代业务验收 |
| Event 保存后、State 保存前退出 | 应用恢复已接收但未完成的输入，不重复追加同一个 Event |
| State 已发布后退出或结果交付失败 | 以保存进度识别完成，不重复应用 |
| Apply 改到一半抛异常 | 停用受损工作图；重开后从已保存事实继续或明确停止 |
| 外部 I/O 返回前后退出 | 已知结果与未知结果走各自明确策略；不混称 exactly-once |
| 从历史节点分叉并继续 | 原线不被修改；新线的阶段和外部副作用策略符合预期 |
| 新程序读取旧模型版本 | 必要 reader/Upgrade 和未完成事件语义都可用 |
| 重开后没有任何待处理事项 | 不凭空追加业务事件或重复执行外部操作 |

按实际功能挑选，不要求每个小应用实现全部能力。
失效资源、Unknown 发布结果和应用更新异常是不同的失败阶段，应分别描述。
不要为了得到通过结果而关闭 Schema history 校验或清理旧数据库。

优先加入真正跨进程的关闭/重开；要声称抗进程崩溃，还需在所承诺的断点使用实际强杀。
可捕获异常测试不能替代进程终止测试；两个调用之间的强杀也不能证明任意写入窗口、OS 崩溃或掉电。
测试报告写明平台、实际断点、包版本和未覆盖范围。

## 用测量检验“领域图足够小”

分别改变以下维度：

- 活跃对象数、集合元素数、字符串与二进制载荷大小。
- 每次改动量与形状：标量、小范围列表编辑、大规模重排、对象替换。
- 提交频率、事件载荷，以及业务需要的持久确认边界。
- 历史长度、冷开次数、分叉数量和同时存在的工作世界。

记录端到端提交耗时、分配/GC、实际追加字节、冷开和继续保存耗时。
必要时再拆解 Capture、比较、编码、底层发布的耗时。
Base/Delta 比例、对象计数和局部微基准可以帮助定位，但不能代替业务总成本。
Source Generator 减少机械处理成本，不自动证明任何规模都合适。

长寿命 Agent 要区分历史总量与当前根可达量；
RL 要区分环境 step 成本与持久 checkpoint 成本；
RPG 要区分逻辑世界与纹理、网格、渲染等资源。
若测出问题，先缩小不必要的活跃图、选择提交粒度，再依据证据讨论 DG 内部优化。
降低提交频率会扩大可丢失进度窗口，应作为业务取舍明确记录。

## Schema 和包的最小纪律

按[Schema 演化指南](../../README.md#schema-演化保留-history显式写转换)保留 accepted history，
为受影响的模型显式升版和提供转换。
持久布局升级、领域规则变化、底层文件格式兼容是不同问题，不能互相替代。

在接入记录中写明实际 DG/Storage 包版本及来源。
源码模式构建成功不等于普通包消费者成功；旧候选的验证结果也不自动覆盖新候选。
包准备与当前发布状态只查[依赖指南](../storage-dependency.md)和根 README，避免在应用指导中复制易过期的 pin。

## 可交付的最小结果

一个初次接入应留下：

- 短的模型/所有权说明，以及 State-only 或 Event/State 协议。
- 可重复运行的冷开续写故事。
- 必要的故障断点证据、外部副作用策略和已知限制。
- 与目标规模相符的一组成本数据；尚未测量时明确写“未验证性能”。

不需要把以上内容拆成多份大型设计文档。
先建立可信的小闭环，再随真实需求扩展分支、历史 UI、迁移和工具执行能力。

## 查实现和既有实验

| 问题 | 入口 |
| --- | --- |
| 最短的真实包接入 | [根 README](../../README.md)、[QuickStart 验证脚本](../../experiments/PackageConsumerProbe/Run-ReadmeQuickStartProbe.ps1) |
| 事件快照和业务恢复进度 | [恢复消费者](../../experiments/PackageConsumerProbe/EventHistoryRecoveryConsumer/README.md)、[对应回归](../../tests/DurableGraph.Persistence.Tests/EventHistoryConsumerRecoveryTests.cs) |
| 发布失败、故障资源和重开 | [失败回归](../../tests/DurableGraph.Persistence.Tests/EventHistoryPublicationFailureTests.cs)、[异常合同](../../src/DurableGraph.Persistence/RepositoryPublicationException.cs) |
| Event 独立内容与成本 | [独立捕获测试](../../tests/DurableGraph.Persistence.Tests/IndependentEventCaptureTests.cs)、[成本测试](../../tests/DurableGraph.Persistence.Tests/IndependentEventCostTests.cs) |
| 历史导航、摘要与分叉 | [应用配方](../design-branches/0096-history-and-head-model/application-recipes.md) |

这些入口用来定位既有机制和证据，不能替代下游自己的领域恢复验收。
