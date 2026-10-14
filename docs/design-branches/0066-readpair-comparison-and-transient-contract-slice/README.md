# DB-066 · ReadPair 比较能力与 Transient 使用合同

> 整理说明：2026-10-14 从 `docs/design-branches/0066-readpair-comparison-and-transient-contract-slice.md` 搬入问题入口；正文来源 Git `8ef9fca`。
> 适用性：完整比较替代共享判定编码、缺 proof 保守退出及图外 Transient view 的历史合同与验收；不据当片结果外推性能或跨操作共享。
> 本文原过程与测试数字保留当时基线；当前能力与焦点见 [PROJECT-STATE](../../../src/PROJECT-STATE.md)。

> 状态：Implemented / G0–G3 已完成，2026-09-11；实现与验收见 §8。
> 核对基线：`79be2c2`，包含 DB-065 的公开 XML 交付和恢复示例。
> 来源：[DramaBoard 002 共享读取反馈](../../../../drama-board/docs/feedback/durablegraph/002-readpair-sharing-contract.md)。
> 初始反馈评估为静态核对；本轮施工与实际运行证据集中见 §8，不新增性能结论。

## 1. 问题、推荐与最小成功标准

ReadPair 为优化实例共享，是否需要额外依赖对象编码成功？消费者能否明确区分“可共享的持久状态”与
“各视图专属的 Transient 状态”，从而正确建立历史查看器？

推荐接纳两项反馈：

- **002-A**：共享候选判定改用完整持久状态比较，去掉其中的 Base/Delta preparation 调用。
  未登记比较能力的手工模型保守不共享；真实错误仍传播。
- **002-B**：ReadPair 的只读约束连接到 Transient 使用说明；视图专属 owner/context/index/cache 放到图外。
  需要任意原位 Transient 重建的浏览场景使用已有独立读取；需要续写时使用 Resume。

最小成功标准：同一持久图使用正常 reader/Normalize/Hydrate、抛错的对象 Base/Delta preparer 时，
单读与 ReadPair 均可正确恢复，后者只通过可选比较能力决定共享，不调用这些 preparer。
另有真实包示例为两份 World 分别建立图外索引，查询始终返回对应视图的世界信息。

## 2. 施工前事实与反馈裁决

| 项目 | 当前事实 | 本片选择 |
|---|---|---|
| 候选比较依赖编码 | [GraphReader.HasSameCurrentState](../../../src/DurableGraph.Persistence/GraphReader.cs) 对两份 current DTO 执行 Validate/PrepareBase，再逐字节比较 | 改用独立比较回调；不否定 DB-064 当时为简化实现而作的选择 |
| 正式的只读 current model | [StateModelBinding](../../../src/DurableGraph/Runtime/Binding/StateModelBinding.cs) 仍要求 preparation，[CapturedStatePreparation](../../../src/DurableGraph/Runtime/Capture/CapturedStatePreparation.cs) 要求非空 Base/Delta 委托 | 不引入新注册体系或允许 writer=null；只明确哪些回调会被读取使用 |
| 最小失败见证 | 抛错 Base-preparer 可造成单读成功而 pair 失败，调用路径明确；下游尚未执行该复现 | 在施工时固化回归，不能把静态推导写成实际下游故障 |
| 同版 Normalize | 同 head/layout 且 RequiresRewrite=false 仍可能改变值或引用 | 完整 current 比较继续必需，不能改成地址/布局快速放行 |
| Transient 重建 | README 分别说明“pair 只读”和“恢复后应用重建 Transient”，缺少两者的连接 | 明确 Transient 写入也可能影响另一视图，提供图外索引示例 |
| 共享收益 | DB-064 已记录解码/保留实例收益与临时编码成本，包含较慢的 paired 样例 | 本片以依赖清晰和可维护性为目标，不设加速比例，不扩张性能工程 |

### 读取仍可能包含必要的规范编码

不能将新合同写成“整个 ReadPair 不执行任何 encoder”。
[DictionaryStateBody](../../../src/DurableGraph/Runtime/Containers/DictionaryStateReader.cs) 的 ReadBase/ApplyDelta 与完整 lookup 验证
已通过 Index → KOps.WriteBase 生成 canonical key bytes，检查持久 key 的唯一性。这条必要验证路径也存在于单读。

本片精确移除的是：**共享候选判定对对象 Base/Delta preparation 的调用，以及为比较而新增的槽编码。**
Dictionary 既有读时 key 校验保持；其异常不能被吞掉。测试分别计量共享比较和必要读取校验，不用一个总 writer 计数混淆两者。

## 3. 范围与保持项

实施涉及 Runtime 对象比较适配、SG 普通/Family 登记接线、StateStore 共享筛选、公开说明和真实包验收。
保留非泛型/泛型 ReadPair 的签名、输入顺序和实际类型语义；独立 ReadEvent/ReadState、可写 Resume 仍是独立入口。

以下不变：同资源/模型 snapshot、同 ID/真实 head、完整 current model/layout、RequiresRewrite 排除、
逐视图 source/current 验证及 Normalize/Upgrade、可达集合与反向引用闭包、所有分配先于 pair Hydrate、
同图身份与 Empty 例外、Resume 可变隔离、第二边失败不交付 pair、无跨图 ReferenceEquals 保证。

不改 Schema/history/catalog/对象 body/State/Journal 格式，不更改提交拓扑，不拆程序集。
不新增 Clone/Freeze、共享开关、自动 Transient hook、通用只读模型平台、全局 cache 或无序字典比较算法。
下游原反馈由下游维护；此片不修改 DramaBoard 的模型或接入代码。

## 4. 比较能力的最小接缝

### 4.1 在现有 preparation 旁增加可选能力

复用现有 ObjectStateRecord.Preparation 的类型擦除边界，避免为本片维护第二份对象操作登记表。
建议在 CapturedStatePreparation 所在文件添加以下形状（名称可在施工中局部打磨）：

```csharp
public delegate bool StateEquality<TState>(in TState left, in TState right)
    where TState : unmanaged;

// CapturedStatePreparation<TState> 构造器末尾追加：
// StateEquality<TState>? stateEquals = null

// 仅内部 ICapturedStatePreparation 增加：
bool ProvesSameState(ObjectStateRecord left, ObjectStateRecord right);
```

`true` 必须证明两个当前 DTO 的全部持久状态相同；`false` 可表示不相同或没有可用证明。
共享决策无需区分这两种 false，不引入三态公共结果或单独 capability 注册。
该方法不证明对象身份、来源或传递引用可共享；这些仍由 GraphReader 外层负责。

- typed 实现先检查两端 exact Schema/DTO，再取 unmanaged 值副本并调用已登记委托；委托缺失时返回 false。
- 委托只读取 DTO，须覆盖全部持久成员，保持确定性，不读领域实例、其他图或外部可变上下文。
  沿用现有表示级比较，不用领域 Equals/EqualityComparer.Default、原始 struct 内存 memcmp、hash-only 或 PrepareDelta 代替。
- 已登记委托抛错即失败；不把 NotSupportedException 等异常解释为能力缺失，不捕获后改走独立恢复。
- 不回退 PrepareBase 比较。存储/Schema/DTO 验证、Upgrade、分配和 Hydrate 的错误仍走现有传播路径。

把委托放入现有 preparation，是复用已经关联的操作容器，不是让比较调用编码器。
另在 ObjectBinding 建方法或 typed model 单独存委托也可实现，但会增加接线位置；本片优先集中改动，
不为名称纯粹性重命名整个 CapturedStatePreparation 或拆分 read/write 目录。

### 4.2 接入现有强类型比较

| 路径 | 接线与比较规则 |
|---|---|
| 普通 SG class | [StateModel.cs](../../../src/DurableGraph.Generator/DurableSchemaGenerator.StateModel.cs) 传入 [GeneratedState.cs](../../../src/DurableGraph.Generator/DurableSchemaGenerator.GeneratedState.cs) 已生成的完整 StateEquals |
| Family/泛型与跨程序集 class | [GenericProjection.cs](../../../src/DurableGraph.Generator/DurableSchemaGenerator.GenericProjection.cs) 将 [GenericState.cs](../../../src/DurableGraph.Generator/DurableSchemaGenerator.GenericState.cs) 的 StateEquals 与 exact schema 闭合为委托；复用已有完整 leaf 字段比较 |
| scalar、inline、enum、Nullable | 继续使用生成 helper/[IStateOps](../../../src/DurableGraph/Runtime/Binding/StateValueBinding.cs)；浮点按位、decimal 全表示、DateTimeOffset exact，Nullable 比 presence 与有效 child，引用槽比 ObjectId |
| [ArrayObjectBinding](../../../src/DurableGraph/Runtime/Containers/ArrayObjectBinding.cs) | 完整 shape 相等，再按 row-major 比较每个元素的 TOps.StateEquals；相同元素总数不能替代 shape |
| [ListObjectBinding](../../../src/DurableGraph/Runtime/Containers/ListObjectBinding.cs) | Count 相同，再按顺序比较每个元素 |
| [DictionaryObjectBinding](../../../src/DurableGraph/Runtime/Containers/DictionaryObjectBinding.cs) | ComparerKind、Count 相同，再按当前 frozen entry 顺序比较每对 key/value 的 KOps/VOps.StateEquals |
| 手工模型 | 可选委托缺失时，该对象不共享；向引用它的 owner/环传播。独立 child 若满足条件仍可共享，不必禁用整次 pair |

Dictionary 采用的是保守的相等证明：条目顺序不同可返回 false，不代表业务映射不同。
旧 Base body 比较同样受 entry 顺序影响，本片不增加无序匹配或 canonical key 重编码来争取这些共享机会。
其查找 comparer 不参与 DTO 比较，完整 metadata 必须参与；不能只比 value、Count 或引用对象的当前内容。

### 4.3 GraphReader 接线与迁移影响

HasSameCurrentState 保留当前外层守卫和 string 特判，非 string 改用同一 current preparation 的 ProvesSameState。
失去 proof 的节点按原算法剔除，其引用者沿反向边退出，稳定的可证明闭包仍整体共享。

SG 当前模型自动登记比较，应用无需额外配置。历史 reader 和 `.dgschema` 不增加新要求。
已有手工模型不提供新委托时会减少 CLR 共享，这是允许的优化变化；DTO/string 解码缓存仍可用。
现有白盒测试若专门证明稳定环/实例共享，应显式登记完整比较委托，不可通过恢复 Base fallback 来维持旧断言。
其他消费者测试继续断言每图值和图内身份，不把跨图命中率升级为 API 合同。

## 5. Transient 与历史视图的使用规则

Transient 表示不进入持久布局，**不表示修改没有可见效果**。
共享 Actor 的持久字段即使完全相同，向其 Transient.OwnerWorld 写入第二个 World，仍可能改变第一视图的查询结果。
闭包算法只分析持久引用，不能证明这种未来的反向引用或环境缓存可以共享。

推荐将 README 的 ReadPair 段、Transient 段、Repository 类型级 remarks、ReadState/ReadEvent 与两种 ReadPair 重载的包内 XML 连接起来：

1. ReadPair 的两份结果及可达对象都按只读使用，包括会影响观察结果的 Transient 写入；
   不分别向可能共享的节点写 owner、查询上下文或视图专属缓存。
2. 视图专属信息由各自的图外 view/index 持有，例如 `WorldView { Snapshot, ActorsById, QueryContext }`。
   两个 WorldView 可引用同一 Actor，但各自拥有世界上下文。不能仅用全局 Actor→context 缓存，因为共享 Actor 仍是同一个 key。
3. 历史浏览若必须原位重建任意 Transient，可分别调用已有 ReadState/ReadEvent 恢复独立领域实例，
   允许各自进行应用侧 Transient 初始化；持久成员仍按历史快照使用。这不需要开启 writer。
   两次读取仍不发布也不安装保存基线，要继续修改并提交时再使用 Resume。

类型级说明不能继续只笼统说所有 read results 都只读，而不连接单图 Transient 初始化与 pair 的更严格约束。
这里第三项只明确独立读取结果的应用侧 Transient 初始化用途，不授予它们作为可写工作区导入的能力。
调用方需遵守已有分配器/比较器稳定性约束；string 和应用主动保留的外部全局对象不因此得到通用深拷贝保证。
本片不增加“可共享延迟缓存原位初始化”的例外或并发承诺；有真实用例再讨论视图无关、可重复且受同步保护的缓存。

真实包示例使用两个不同 World 引用同版本 Actor，分别建立外部索引；查询应得到各自 World 的信息和正确 Actor 值。
消费者不需要判断 Actor 是否 ReferenceEquals；错误示范“给 Actor 写两次 OwnerWorld”只作为原因解释，不作为受支持操作。

## 6. 施工步骤与验收

| 阶段 | 交付 | 验收重点 |
|---|---|---|
| G0 能力与适配 | 可选 typed equality、内部 proof 桥、三容器比较 | 比较不调用 Base/Delta/槽 writer；Shape、Count、ComparerKind、全部槽完整；缺能力与错误不同 |
| G1 生成与共享接线 | 普通/Family 自动登记，GraphReader 替换 body 比较 | 抛错 preparer 的单读/pair 均成功；same-layout Normalize 差异仍排除；已有闭包不变量保留 |
| G2 使用与包交付 | README/PACKAGE、Repository 类型及单图/pair 方法 XML、图外 view 示例 | Transient 合同一致；真实 NuGet 场景查询正确；XML 文案确实随包交付 |
| G3 集成审阅 | focused/full relevant tests、根 build、真实包、文档同步 | 无格式变化、无通用异常降级；活跃文档按实现结果更新，保留旧 DB-064 测量的历史口径 |

必须具备的可执行见证：

- 正常保存后换读取模型：Base/Delta preparer 抛错，完整 equality 可用；单读/pair 成功且 preparer 调用为零。
- equality 缺失：不调用 preparer，独立恢复值正确；缺 proof 对 owner/环的传播正确。
- equality 或 DTO 校验抛错：原异常/现有包装传播，不交付 pair，不暗中再次 Normalize/Hydrate 重试。
- 同 head/layout 的手工 Normalize 改标量或改边：双方仍独立 Normalize，受影响实例不共享。
- 浮点特殊位、decimal 表示、DateTimeOffset offset、Nullable presence、继承/inline/generic 引用槽保持完整比较。
- 数组同 Count 不同 shape、List 顺序/元素差异、Dictionary 标签或 key/value 差异不得通过；
  字典条目重排允许保守 false，不改变其持久映射无序语义。
- 直接比较容器时使用会在 WriteBase/PrepareDelta 抛错的测试槽，证明比较自身不编码；
  另保留真实 Dictionary 读时 canonical key 重复拒绝，不能为了零 encoder 总数而跳过验证。
- 稳定环、changed-child、string 同版本与 Empty、完整 source 验证、Resume 隔离与未改续写的既有回归保持。
- 普通 SG 与 Family 两条路径都证明无需手工登记 equality，且实际共享仍生效；普通路径不能被只测试 Family 掩盖。
- 真实包 external view 示例校验两视图查询结果，不依赖跨图实例身份；包内两种 ReadPair remarks 包含 Transient 限制。

优先复用 [SharedGraphReaderTests](../../../tests/DurableGraph.Persistence.Tests/SharedGraphReaderTests.cs)、
[SharedEventHistoryTests](../../../tests/DurableGraph.Persistence.Tests/SharedEventHistoryTests.cs)、
[EventHistoryConsumer](../../../experiments/PackageConsumerProbe/EventHistoryConsumer/README.md) 和
[DB-065 包文档验收](../../../experiments/PackageConsumerProbe/Run-EventHistoryRecoveryProbe.ps1)。
普通/Family 生成器验收覆盖 current 比较登记；cross-assembly 只复用其既有执行合同，不创建新历史版本。
最终运行根 solution build、相关 Runtime/Generator/StateStore tests 和涉及的真实包 lanes；.NET 验证串行执行。

## 7. 替代方案与延期项

| 方案 | 取舍与结论 |
|---|---|
| 仅公布 pair 需要 Base-preparer 成功 | 改动最少，但持续保留额外的读取依赖；已有 StateEquals 可用，因此推荐本片接线 |
| 捕获编码异常后独立恢复 | 不采纳；能力缺失与坏模型/Schema/资源错误混淆，还可能重复执行用户回调 |
| 无比较能力就只信 ID/head/layout | 不采纳；same-layout Normalize 反例已存在 |
| 将 read/write/comparison 各建正式目录 | 暂缓；目前只需一个可选委托，未出现独立注册平台的消费者 |
| 无序 Dictionary 比较、共享开关或深不可变标记 | 暂缓；本片保守 proof 已满足正确性和现有主要共享场景 |

本片经用户确认进入实施；后续能力仍不随本片扩张。
若未来需要专用只读模型注册、图内 per-view Transient 或更一般多视图缓存，再依据具体模型与可执行复现选片。

## 8. 施工与验收记录

本轮以 `5dd828a` 为干净工作树基线，保留 DB-065 的包文档与恢复示例。
冻结的接缝即 §4 的可选 `StateEquality<TState>` 第四构造参数及内部 `ProvesSameState`；
Runtime、SG、公开合同/真实包示例分工实现，主线程负责 GraphReader、共享失败回归和统一验收。
独立审阅核对完整值比较、双方校验、错误传播、引用闭包、生成接线和使用合同，未发现阻塞项。

| 要求 | 实现与可执行见证 |
|---|---|
| 可选完整 proof，缺能力与错误区分 | [CapturedStatePreparation](../../../src/DurableGraph/Runtime/Capture/CapturedStatePreparation.cs)、[ObjectStateComparisonTests](../../../tests/DurableGraph.Tests/ObjectStateComparisonTests.cs)；双方 exact Schema/DTO 均校验，不回退编码 |
| 容器完整比较且不编码 | 三种 binding；上述 tests 用抛错槽 writer 验证 shape、零长度维度、Count、ComparerKind、key/value 和顺序 |
| SG 自动登记及完整表示 | [GeneratedComparisonRegistrationTests](../../../tests/DurableGraph.Tests/GeneratedComparisonRegistrationTests.cs)；普通/Family 分别 Capture 并调用已登记 proof，冷重开 ReadPair 证明实际共享；覆盖继承、inline/generic 引用、特殊浮点、decimal、offset、Nullable |
| 读取仅依赖比较，错误不重试 | [SharedGraphReaderTests](../../../tests/DurableGraph.Persistence.Tests/SharedGraphReaderTests.cs)；保存后用抛错 preparer 读取，缺 proof 与循环依赖、比较异常计数，以及同版 Normalize 改值/改边 |
| 两种 Transient 用法与包交付 | [SharedReadProbe（Git 历史）](../0091-retired-material-cleanup/README.md#git-history "Git 6dbaf0e:experiments/PackageConsumerProbe/EventHistoryConsumer/SharedReadProbe.cs")；两个 WorldView 查询各自上下文，独立只读打开后原位初始化；[XML gate](../../../experiments/PackageConsumerProbe/Run-EventHistoryRecoveryProbe.ps1) 检查两种 ReadPair remarks |

串行验证（2026-09-11），日志存放于未跟踪的 `artifacts/db066-validation/`：

- 根 `dotnet build DurableGraph.slnx -t:Rebuild`：0 警告/错误。
- Runtime/Generator 完整 `DurableGraph.Tests`：1,534 通过，0 失败/跳过；包括新增 11 项 Runtime、4 项 SG 接线/冷读用例。
- StateStore 完整 `DurableGraph.StateStore.Tests`：711 通过，0 失败/跳过；保留 canonical key 重复拒绝、source 校验和 Resume 隔离回归。
- EventHistory V1/V2 真实包：history 9/11 不变，外部 WorldView 与独立 Transient 初始化、升级/共享/根替换/只读回归通过；工作集 `event-history-20260911152344-55424-2e3ed686`。
- Recovery 真实包：包内与恢复目录 XML 一致、两种 ReadPair 的 Transient remarks 检查通过；热/冷恢复及只读验证通过；工作集 `event-recovery-20260911152519-9296-5904674b`。
- README 原文 runner 复用同批 feed，独立包缓存与项目：HP99/98、升版 HP97/Day1 和历史保留验证通过；工作集 `readme-20260911152544-9296-120b7a64`。
- 变更文档的本地链接/锚点与 `git diff --check` 通过；没有未解决的独立审阅发现。

调试中修正了新测试把根擦除为 DurableBase 后调用 CreateBranch 的夹具错误，改由生成宿主以 exact 根类型保存；
并更新一处旧生成文本断言，以包含第四个 StateEquals 参数。一次修正与编译重叠产生了旧测试二进制，
随后强制 Rebuild、定向 5 项与完整套件重新验证。没有用放宽产品 exact 根校验或回退编码来通过测试。
