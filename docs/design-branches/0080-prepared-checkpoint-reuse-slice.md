# DB-080：最近 State 单图准备材料复用

> 状态：**已实施并独立验收，单槽默认启用**。2026-09-28；实际边界、证据与默认启用结论见 [实施记录](0080-prepared-checkpoint-reuse-implementation.md)。
> 产品合同以 [DB-083 用户故事与 Checkpoint API](0083-repository-checkpoint-api-user-stories.md) 为准；Fork/Checkout 有最近 State 才恢复该图，无 State 时不进入缓存路径。
> 顺序：第 4 片；依赖 [DB-079](0079-shared-graph-restoration-core-slice.md)，推荐下一片 [DB-081](0081-hot-commit-restoration-material-slice.md)。
> 前置机制与验收见 [DB-079 实施记录](0079-shared-graph-restoration-core-implementation.md)；本片在内部 `PreparedGraphSelection` 外补充跨操作证书与成功入槽资格。
> 本片将 [DB-076](0076-efficient-graph-fork-technical-path.md) 的材料复用方向收束为单槽实验，不预设 repository 弱引用缓存。
> 术语：采用[术语表](../DurableGraph-glossary.md#restoration-preparation)；恢复准备（Prepare）与保存侧内容准备区分。非泛型 `BranchCheckout` / `Checkout` 已由 [078-A](0078-a-repository-free-history-implementation.md) 交付；每分支占用与 named Fork 见 [078-C 记录](0078-c-branch-checkout-fork-implementation.md)。

## 1. 问题与完成标准

从同一持久 State 基线 fork 多次时，保留一次完整恢复准备的结果，让每个分支工作副本只做必要复核和独立物化。
**首次完整恢复后再次 Fork/Checkout 到同一最近 State，不再对该 State Decode/Normalize/重做已证明的全图结构扫描，
但 mutable Allocate/Hydrate 与每图身份导入仍独立，live Schema 冲突仍能阻止交付。**
本片不复用 CLR 领域实例，不接入热提交候选，不优化 ReadCheckpoint、ReadEvent、保留的独立单图读取或 ReadPair 的跨操作驻留。

## 2. 首版只保留一份最近 State 准备材料

Repository 内一个可选槽保存 DB-079 的完整 State 单图准备材料。key 是本 repo 的最近 State 检查点，
并核对其 exact revision/root；不按可移动 branch name、单独 ObjectId 或请求的 Event Head 寻址。
槽不存请求 Head，也不持有 Event 图材料。环境和资源寿命由所属 repo 隐含，不增公共 context id。

每次操作先独立验证请求历史地址/角色/逻辑祖先，有最近 State 才查槽；工作副本 Head 和 Fork ref 均取本次请求位置。
若 Event-only 前缀尚无 State，按 DB-079 交付无基线工作副本：不读、不复核、不替换也不清空已有槽，不存负缓存条目或占位图。
例如 A 分支的 S_A 已入槽，B 分支是 E0→E1；从 E1 Fork 到新分支必须 State=null，且不能因 S_A 的 Schema 后来冲突而失败，
因为该请求没有选择 S_A。正常地址/历史验证、编辑占用、发布与 fault 检查并未省略；真正 repo fault 仍使操作拒绝。
`S0→E1→E2` 的三个请求可以复用同一份 S0 材料，但得到各自精确 Head；这不意味着三个历史位置相同。
另一个 State revision 首次使用须取得自己的完整恢复准备结果，哪怕全部 object head 相同，也不能拿前一 revision 的验证代替它。
这也覆盖跨实际类型根替换或把已有子对象升为根：新选择使用自己的 root binding、可达闭包与证书，不借旧根的材料认证。
重复 Checkout 同 branch 须先 Dispose 前一分支工作副本；驻留命中不绕过 DB-078 的每 branch 单编辑占用。
根资格、实际 binding 与精确物化类型每次重验；非泛型入口不引入声明 `TState` 或要求根的实际类型等于 `IDurableObject`。

完整 State 恢复准备、物化和工作区/分支工作副本构造成功后才具备换槽资格。
Checkout 在交付门换槽；Fork 的候选槽内容在 ref 发布前备好，成功后只做引用安装。
任何失败不把本次未完成材料发布为可复用结果；原有成功槽可保持。repo Dispose/fault 释放缓存所持引用，且不可再使用。

这是 **entry 数量上限，不是字节上限**：一个大图仍可能很大，活动分支工作副本持有的保存基线也不受该槽约束。
首版不增加 LRU、多 checkpoint 字典、公共容量 options 或 pin 句柄；记录实际驻留量后再决定是否需要它们。
内部测试/测量可禁用槽，用相同产品路径提供 cold 对照，不把测试开关自动扩成公共功能。

## 3. 复用证书与每次仍须检查的事项

一次成功证明成立的条件是 owned 不变 DTO、固定模型环境、完整 source membership、固定 revision/root、同 Store lifetime。
这些允许省去重复解码、Normalize、引用完整性/Dictionary 结构校验与可达扫描。
每次命中仍先检查 repo/resources、请求历史选择与 State 来源/角色、root 类型，再复核该材料的全部 live Schema requirements，最后物化。
后续保存继续走 LoadedRevisionPlanner 的完整来源/head/Parent 检查，不因准备结果命中而放宽。
活动工作副本继续保留完整 normalized 保存来源、SourceLayout/RequiresRewrite 与 head/H，
但不因导入而额外长期持有跨操作恢复准备证书或整个槽。淘汰后再次请求旧 State 时重新冷准备并收集其完整依赖。
证书是省略特定恢复工作的依据，不是历次保存、Upgrade 或其他图全部依赖的累积清单；新 State 证书见 DB-081。

最难的工程接缝是完整依赖证书，而非缓存容器：

1. 复用 [StateBindingContext](../../src/DurableGraph/Runtime/Binding/StateBindingContext.cs) 的 `ExactSchemaRequirementSet`，
   使其可在内部收集/合并并作为不可变检查材料传递给 Persistence；不复制一套布局权威。
2. 用有明确开始/结束、异常后必释放的内部词法收集范围，记录标准 `Validate` 实际核对过的完整依赖。
   覆盖 Decode、Normalize、完整引用验证和 reachability；显式纳入全 source/current ObjectLayout 的 Schema/容器槽依赖。
   如首次物化还产生额外标准依赖检查，入槽前也将其纳入，不以“工厂已闭合”为由漏记。
3. [UpgradePlan](../../src/DurableGraph/Runtime/Binding/StateBindingContext.Upgrade.cs) 的缓存命中也记录已有完整 requirements；
   包括中间 owner 版本、base/inline/Nullable、数组/List/Dictionary 以及声明的 value-upgrade 工具依赖。
   模型、reader、value 与容器 binding 的工厂闭合，也保留其经标准 Resolve/Validate 取得的完整证据；闭合缓存命中重新复核并传递，
   不能只在 State 第一次准备时观察工厂是否执行。计划闭合还须纳入这些 binding 的证据。
4. 证书去重时保留冲突检测和诊断路径；复核仍使用同一个 live SchemaStore。不要增加 Schema epoch、全局失效通知、
   AsyncLocal 状态或面向插件的通用依赖追踪器；单线程内部作用域足够。

**关键反例：** v1→v2→v3 中只有 v2 使用 `Intermediate v5`。初次恢复准备后该 key 才出现冲突权威定义，
下次命中仍必须失败。只存 source/current 两端不够；只记录首次 factory 执行也不够，因为计划可能早已预闭合。

DB-077 的 provider 合同在此具体化：依赖必须体现在 exact layouts、声明的 upgrade dependencies，
或经过同一 repo context 的标准 Resolve/Normalize 验证。不能借另一个私有 context、闭包中的隐藏预计算布局绕过权威验证。
这限制合规 provider 的行为，不承诺分析任意 delegate；若某条库内路径尚不能给出完整证据，先补齐该路径，
不能把缺证据的材料作为命中交付。无需为不合规回调建立兼容 fallback。
提前复核的必需证据覆盖本次被省略的恢复准备/闭合/Normalize 工作。尚未执行的 Hydrate 若首次产生额外标准检查，
仍在该回调内检查，不保证这些检查也发生在 Allocate 前；不为发现它们预执行用户回调。

## 4. 施工单元

| 单元 | 交付与验收 |
|---|---|
| G0 证书机制 | 现有 exact requirements 的内部收集/合并/复核；特别覆盖预闭合计划与中间依赖 |
| G1 材料资格 | 完整单图准备材料附不可变证书；来源/根/角色检查；cold 与重复使用取得同一语义 |
| G2 单槽接线 | Fork/Checkout 的查槽、成功后换槽、Dispose/fault 释放；失败不替换 |
| G3 独立验收与测量 | 冷/命中功能对照、回调/分配归因、真实产品路径和完整回归 |

Runtime 证书接缝和 Persistence 单槽可以分配不同实现者；先约定内部最小返回形状，再顺序集成。
Coding Agent 主会话重点复核证书完整性及入槽门，避免把“缓存测试命中”误当成权威校验已完成。

## 5. 验收矩阵

| 场景 | 最小可观察结果 |
|---|---|
| 同点连续 fork、两 branch 同 head、重复 Checkout | 第一次冷恢复准备，之后 Decode/Normalize 为零；mutable 图仍独立，可各自增量续写 |
| S0 / E1 / E2 指向同一最近 State | State 材料可命中；Head/ref 分别保持精确请求位置；不准备或物化 Event 图 |
| 槽为另一分支 S_A，本次请求 Event-only 前缀 | State=null，零图准备/物化；槽与证书不被访问或替换；即使 S_A 的依赖迟注册冲突，该未请求图也不影响本次成功 |
| Event-first 后首 State，或异型 State / 已有子对象升根 | 新 State 首次冷恢复独立建立完整材料；不误用前置 Event、旧 root binding 或旧 revision 证书；保存身份与 cold 对照一致 |
| 新 State revision、槽替换、同盘重开 | 不误命中；冷路径重新完整验证；旧分支工作副本可继续持有自己的合法保存基线 |
| 普通、强制 Family、历史 Upgrade、容器 | 同一证书机制工作；不只依赖 binary 静态 binding |
| 中间依赖迟注册冲突 | 先预闭合 reader/model/upgrade plans，再入槽；冲突后 hit 在 Allocate 前失败且 Normalize 为零 |
| 根/来源错误，State 物化后段失败 | 不交付半图、不发布 fork ref、不替换旧槽；作用域不会污染下一次恢复准备 |
| Upgrade 后不可达旧行、高 ID、Empty | 完整 source/Remove/rewrite/身份游标与 cold 一致 |
| 冷升级导入后槽被替换，再请求旧 State | 工作副本保存来源保持；恢复走 cold 并重新检查历史中间依赖，不借活动工作副本伪造命中 |
| 不同 revision 缺失引用或非法不可达行 | 即使旧对象版本曾命中，也不得逃过这个 revision 的完整验证 |
| Dispose/fault | 槽引用释放，后续操作拒绝；已交付图不因淘汰被主动破坏 |

证据入口：[GenericBindingCatalogTests](../../tests/DurableGraph.Persistence.Tests/GenericBindingCatalogTests.cs)、
[WorldWorkspaceStorageBaselineTests](../../tests/DurableGraph.Persistence.Tests/WorldWorkspaceStorageBaselineTests.cs)、
[GraphReadStatistics](../../src/DurableGraph.Persistence/GraphReadStatistics.cs)、
[共享读取测试](../../tests/DurableGraph.Persistence.Tests/SharedGraphReaderTests.cs)。
计数明确区分 typed Decode、Normalize、结构扫描、Allocate/Hydrate 与实际文件 I/O，不能把 Store 已缓存误报成全部恢复准备成本消失。

## 6. 测量与停点

同一 Release 构建、同一模型/检查点，对比禁用与启用单槽；覆盖重复同点扇出、连续 E 共享最近 State、每次换 State 的低命中路径、
Event 起点、普通/Family、全 mutable 与容器/循环。报告完整 Fork 总耗时、各阶段计数、托管分配及槽驻留规模。
保留命令、模型规模、预热与重复策略；不把绝对耗时阈值做成普通 CI 断言。

验收按 [共通规则](0076-efficient-graph-fork-technical-path.md#10-分片施工导航)。若复核/驻留成本抵消收益，
记录结果并保持默认关闭/撤回驻留接线，不扩大为多级缓存来凑成果；已验证的恢复核心可以保留。
后继开始前以本片实际结论为前提，不能把否定结果写成“缓存已交付”。
