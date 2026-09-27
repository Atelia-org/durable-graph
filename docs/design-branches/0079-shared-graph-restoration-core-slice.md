# DB-079：共用图恢复核心

> 状态：**Proposed / 已按 DB-083 校准的候选设计，未实施；不是实施授权**。2026-09-27；当前实现事实另见源码。
> 产品合同以 [DB-083 用户故事与 Checkpoint API](0083-repository-checkpoint-api-user-stories.md) 为准；本片只统一内部机制，不扩大读取内容范围。
> 顺序：第 3 片；依赖 [DB-078](0078-editable-checkpoint-fork-slice.md)，下一片 [DB-080](0080-prepared-checkpoint-reuse-slice.md)。
> 只重组内部恢复职责，保持 DB-077/078 的公开合同；依据 [DB-076 §5、§7](0076-efficient-graph-fork-technical-path.md)。
> 术语：采用[术语表](../DurableGraph-glossary.md#restoration-preparation)；恢复准备（Prepare）与保存侧内容准备区分。非泛型 `BranchCheckout` / `Checkout` 已由 [078-A](0078-a-repository-free-history-implementation.md) 交付；每分支占用与 named Fork 见 [078-C 记录](0078-c-branch-checkout-fork-implementation.md)。本片内部优化仍未实施。

## 1. 问题与独立交付

[GraphReader](../../src/DurableGraph.Persistence/GraphReader.cs) 已有 `Prepare`/`ReadSelection`，
但独立读取与 ReadPair 各有一套 Allocate/身份登记/Hydrate 循环。
本片让 ReadCheckpoint、ReadEvent、Checkout/Fork、保留的独立单图读取和 ReadPair 共用单图准备材料与两阶段物化机制，
**完成标准是所有用途经过同一核心且语义矩阵保持；不宣称本片已经加速，不引入跨操作驻留。**

ReadPair 的共享证明仍由原 FindSharedClosure/HasSameCurrentState 负责。
不同用途的共享资格不同，不能把 ReadPair 的只读 mutable 共享带入可编辑恢复。

| 用途 | 选图与交付 |
|---|---|
| ReadCheckpoint | 按固定历史地址及 DB-083 的 PreviousX 规则选择至多两图；独立物化，全部成功后返回，getter 保持同一实例 |
| ReadEvent / 独立单图读取 | 只准备、物化请求的单图；ReadEvent 不因 PreviousState 而物化 State |
| Checkout / Fork | 独立解析请求 Head；有最近 State 时只物化该 State 并导入保存基线；尚无 State 时交付 State=null 的工作副本，不准备或物化任何领域图；不自动 replay |
| ReadPair | 两个明确图选择；保留原只读共享闭包规划，不作为默认 Checkpoint 的实现捷径 |

历史导航负责校验请求地址、角色、逻辑祖先与所选最近 State；图核心只处理明确 revision/root。
工作副本 Head 始终来自本次请求位置，不能用恢复的 State 位置代替。Event-first、连续 E/E/S/S 与最近严格祖先 PreviousX 的导航在 DB-078 已建立，本片不重新定义。
尚无 State 是成功的历史选择，不是缺失根或损坏图：宿主建立无保存基线的新 `CaptureSession`，不向图核心传入零 RootId、空 normalized revision 或占位 State。
所需模型由实际选择的图决定；Event-only Checkout/Fork 不因未配置 Event 的领域模型而失败。地址、逻辑祖先、发布占用与 Open 的完整数据验证仍照常执行。

## 2. 内部材料边界

沿用 [NormalizedRevision](../../src/DurableGraph.Persistence/NormalizedRevision.cs)，将现有私有 selection 提升为可复用的内部单图准备材料（prepared selection）；
名字由施工裁定，不为其新增公共 ForkSource、Workspace 或通用策略接口。

| 材料 | 必须保留 |
|---|---|
| 完整 normalized 保存基线 | 全部 source membership/current DTO、SourceLayout/RequiresRewrite、每对象 head/H、exact Revision 与 Store/SchemaStore 来源 |
| 当前根选择 | RootId、实际 root binding、固定环境身份、可达顺序；验证非空 durable 根和实际类型，保留仍有显式类型请求的读取检查 |
| 可编辑导入 | 本次实例→ID 表、全 source string 身份、从完整源 max ID + 1 开始的独立 cursor |

单图准备材料的结构可以共享，身份表/CaptureSession/pending candidate 不能共享。
恢复 cursor 只从所选完整 State 源成员求得，不扫描后继 Event 来维持跨检查点永久唯一 ID；Event-only ID 数值可重合。
无 State 的冷工作副本从新 `CaptureSession` 的初始 cursor 开始；不导入前置 Event 的实例、DTO 或身份表。首 State 的图 Parent=null 保存由 DB-078 负责，Journal Parent 仍是前一历史位置。
只保留 reachable DTO 会丢失 Upgrade 后不可达行的 Remove、来源检查或最大 ID，本片不得这样裁剪。
ReadPair 需要的 head 可直接来自完整 `NormalizedObject.Storage`；不为它永久强持第二份 decoded DTO 目录。
如果去除 ReadSelection.Decoded，必须证明用于比较的 exact head 来源未减弱；缺 provenance 不能假造为可保存基线。
Schema 依赖收集与跨操作复核留给 DB-080，本片对每张实际选择的图仍完整执行恢复准备。

跨实际类型 State 替换也沿用同一来源合同：恢复结果的 root binding 来自该 revision 的实际根；后续提交按候选根实际类型解析模型，
不沿用创建分支时的根类型。已有子对象升为根或旧根成为新根的子对象时，同一实例继续保留原 ID；只有新实例才分配新 ID。
根角色变化不清空整个身份映射，也不跳过完整旧来源校验；只移除候选图已不可达的行。成功安装与失败时序由 DB-078 负责，本片不得在重构时改弱。

## 3. 一份物化机制，两种规划

按以下职责抽取内部函数，不必引入对应的类层级：

1. 恢复准备：完整 Decode/Normalize/current 验证，检查根，求可达闭包。
2. Plan：独立图提供空复用映射；ReadPair 提供原闭包证明生成的可信对应关系。
3. Allocate/Register：生成各图实例表及操作内冲突记录；先建立所有需要的实例/表。
4. Hydrate：使用所属图的完整表；被明确复用的对象只填充一次。
5. Deliver：独立读取返回根；可编辑恢复导入完整保存基线和新的 `CaptureSession`，再由公开宿主交付非泛型分支工作副本；ReadCheckpoint/ReadPair 所需各图全部完成才交付。

“先分配后填充”也覆盖整个 ReadPair：两张表均完成后才进行任何 Hydrate。
ReadCheckpoint 的两张独立图使用同一操作的冲突检查，但不规划 mutable 复用；Checkout/Fork 最多一张 State 图，无 State 时跳过上述图流程。
非泛型入口不把 `IDurableObject` 当成要求的 exact CLR 根类型；Allocate 仍须返回所选 binding 的精确实际类型。
防重入仍由 Repository 包住完整操作；内部函数不自行开放用户可嵌套的恢复事务。

复用资格与一般 allocator 冲突必须分开：只有内部可信映射指定的对象可以重复出现；
用户 Allocate 返回同一 singleton 不能因为类型相同或值相同而获得豁免。
保持每图 exact type/ID 检查、非空 string 的身份规则以及 canonical Empty 的既有特殊处理。
纯只读结果无须无条件创建 CaptureSession 或保留一套不用的保存映射。

## 4. 不混入本片的优化

- 不删除 ReadPair 的引用闭包、完整值比较、RequiresRewrite 保守排除或错误传播。
- 不建立 repo cache、Schema epoch、WeakReference、叶 bank；没有跨操作 callback 次数减少承诺。
- 不把 State 和 Event 合成一个保存基线；不合并 mutable 领域图。
- 不根据“新合同理论上允许”同时改动所有比较/解码算法；相邻片各自提供性能见证。

## 5. 施工与独立验收

G0 将已有独立/Pair/Checkout/Fork 见证映射到共同入口；G1 提取完整单图准备材料和身份导入；
G2 合并分配/填充循环并接入原 Pair 规划；G3 独立检查错误顺序与各图保存后果。
核心 GraphReader 由一个实现者修改；subagent 可负责保存基线见证、Pair 回归审阅，避免同时改同一循环。

| 场景 | 最小完成标准 |
|---|---|
| 上述用途矩阵 | 确认共用核心，无另留只在 fork 使用的复制版恢复算法；ReadEvent 无额外 State 物化 |
| 前向引用、alias、自环/互环、容器环 | 全部表先建好；exact 类型和图内 identity 正确 |
| Pair 稳定环 / changed child | 稳定闭包保持既有共享；目标 head 改变时 owner 分裂 |
| Pair comparison 错误 / 第二图 Hydrate 失败 | 原错误传播，无半对结果，无新增 Store 写入 |
| 独立 State/Event 及 singleton allocator | mutable 不混图；伪共享在填充第二图前拒绝 |
| ReadCheckpoint 两图与稳定 getter | 修改 PreviousState 不改变 Event；重复 getter 不重新物化；任一图失败不交付半个 Checkpoint |
| 从连续 Event 中间位置 Checkout/Fork | 只恢复最近 State，保留请求 Head；无 Event 模型时不因恢复未请求的 Event 失败 |
| E0→E1，尚无 State 的 Checkout/Fork | State=null、保存基线为空，零 Decode/Normalize/Allocate/Hydrate；Head/ref 精确；不要求未读取 Event 的领域模型，不导入 Event 身份 |
| Event-first 的 ReadCheckpoint 与首 State | Event 的 PreviousState 根/地址同时为 null；首 State 的 PreviousEvent 为最近严格祖先 Event；只准备实际存在的图 |
| 异型 State 替换，旧根成为子对象或旧子对象升根 | 冷恢复选择实际根模型；同实例 ID、完整来源、Remove 与后继保存保持；不把新根角色误作全图身份重置 |
| 不可达旧行/高 ID、Empty 与非空同值 string | 完整来源/cursor/身份导入保持，后续 Remove/Delta 正确 |
| Upgrade rewrite + Event + 后继 State | Event 不清除 State 重写义务；首次 Base、后继 Delta 如常 |
| 同内容无 state-equality proof | ReadPair 仍保守不共享；不得退回编码 Base 来判断相等 |

证据入口：[SharedGraphReaderTests](../../tests/DurableGraph.Persistence.Tests/SharedGraphReaderTests.cs)、
[SharedEventHistoryTests](../../tests/DurableGraph.Persistence.Tests/SharedEventHistoryTests.cs)、
[WorldWorkspaceStorageBaselineTests](../../tests/DurableGraph.Persistence.Tests/WorldWorkspaceStorageBaselineTests.cs)、
[LoadedReferenceWorldTests](../../tests/DurableGraph.Persistence.Tests/LoadedReferenceWorldTests.cs)。
DB-077 已宣布违约的动态 Normalize 测试应按其新用途区分，不要求恢复旧的逐调用模型语义。

按 [共通验收](0076-efficient-graph-fork-technical-path.md#10-分片施工导航) 构建/测试；本片结束时冷恢复仍逐操作执行，
下一片才授予恢复准备结果跨操作复用资格。重构完成不等于已经证明任何缓存命中安全。
