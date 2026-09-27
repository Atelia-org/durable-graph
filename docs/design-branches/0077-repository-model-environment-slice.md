# DB-077：Repository 固定模型环境

> 状态：**已实施并验收 / 2026-09-27**。实际分工与验证见[实施记录](0077-model-environment-implementation.md)；原设计源码基线 `343bfa6`。
> 目标以 [DB-083 用户故事与 Checkpoint API](0083-repository-checkpoint-api-user-stories.md) 为准；本片仅交付固定模型环境与 current 快路，公开 API 统一和自由历史由 DB-078 分阶段交付。
> 顺序：DB-076 路线的第 1 片；完成后进入 [DB-078](0078-editable-checkpoint-fork-slice.md)。
> 来源：用户要求按单个主会话配合 subagent 的粒度拆分；无兼容性包袱。本文记录本片合同；本轮施工由用户另行明确授权。
> current 恒等快路的反例与原裁决见 [历史专项评审](0077-0078-api-dialectical-review.md)；本片保留该局部机制，公开迁移范围以本次校准为准，历史转换仍承担稳定性合同。
> 术语遵循[项目术语表](../DurableGraph-glossary.md#branch-checkout)。本片阶段性保留源码的 `EventHistoryRepository` / `EventHistorySession<TState>` / `Resume<TState>`，DB-078-A 一次迁移为最终非泛型入口；不先制造一个过渡的 `BranchCheckout<TState>`。

## 1. 本片的问题与完成边界

让一个 opened repository 的所有操作使用同一份冻结的模型执行环境，为后续准备材料复用提供稳定解释。
**完成标志：公开入口不再逐次接收 registry；普通与 Family 闭合跨操作稳定，新合同、仓内消费者和真实包一起通过。**
本片仍保持每个仓库实例至多一个活动**分支工作副本**，不实现 fork、不缓存恢复结果、不改变持久格式。
本片保留现有交替历史和公开名称，使环境/回调合同有独立可运行停点；这不是最终目标或并存的兼容 API。
DB-078-A 才统一公开入口并落实用户已选的 Event-first、自由 E/S 顺序及跨类型 State 替换，
DB-078-C 再把占用范围放宽为不同分支各一个工作副本。这里保留的交替/exact 根检查只属于本阶段停点，不再是待产品裁定的目标限制。

实施前 [Repository](../../src/DurableGraph.Persistence/Repository.cs) 的 Open 不接收模型；
CreateBranch、`Resume`、ReadState/Event/Pair 各自取得 snapshot。
[StateModelSnapshot](../../src/DurableGraph.Persistence/StateModelSnapshot.cs) 已有成功闭合缓存，
[WorldWorkspace](../../src/DurableGraph.Persistence/WorldWorkspace.cs) 已有接收 snapshot 的加载入口；
因此先调整所有权与参数传递，无须另造公共 ModelContext。

## 2. 本片阶段性公开合同

```csharp
public static EventHistoryRepository CreateNew(string path, StateModelRegistry models,
    RbfSegmentStoreOptions? options = null);
public static EventHistoryRepository OpenExisting(string path, StateModelRegistry models,
    RbfSegmentStoreOptions? options = null);
public static EventHistoryRepository OpenReadOnlyExisting(string path, StateModelRegistry models,
    RbfSegmentStoreOptions? options = null);

public EventHistorySession<TState> CreateBranch<TState>(string branchName, TState initialState,
    ReadAmplificationBaseBudgetParameters? parameters = null) where TState : class, IDurableObject;
public EventHistorySession<TState> Resume<TState>(string branchName) where TState : class, IDurableObject;
public TState ReadState<TState>(GraphFrame frame) where TState : class, IDurableObject;
public TEvent ReadEvent<TEvent>(GraphFrame frame) where TEvent : class, IDurableObject;
public (IDurableObject First, IDurableObject Second) ReadPair(GraphFrame first, GraphFrame second);
public (TFirst First, TSecond Second) ReadPair<TFirst, TSecond>(GraphFrame first, GraphFrame second)
    where TFirst : class, IDurableObject where TSecond : class, IDurableObject;
```

上面是 API 声明清单，省略实现体。保留各入口的根类型规则：Read/ReadPair 接受可赋值的请求类型且保留实际类型；
CreateBranch/Resume 暂保留既有 exact TState 检查；它不意味着 DB-083 非泛型目标需要调用者提供请求类型。保存策略不变。
`CreateBranch(string, GraphFrame)`、MoveBranch 与工作副本的 Commit 无须模型参数，继续保留。
移除旧的逐调用 registry 公开重载；本片没有同 repo 的逐操作覆盖参数，也没有新旧名称兼容双轨。
上述阶段性签名只规定本片终点，DB-078-A 同步迁移全部调用者，删除旧公开名称和 typed 便利重载。
仅检查历史/refs 的消费者可显式传空 registry；
Open 的物理历史验证仍不要求拥有所有 current CLR 模型，不在打开时 eagerly 闭合全部定义。

Repository 在资源建立后创建并持有一份 `StateModelSnapshot`，其使用授权限于本次 Open；打开失败仍清理所有已取得资源。
Dispose 关闭资源并禁止后续 repo 操作，不承诺调用者仍持有的工作副本/模型引用立即被 GC 回收。
`models` 为 null 时在取得文件资源或创建目录前抛出 `ArgumentNullException`，不留下本次创建的仓库。
registry 是 builder：Open 消费其配置快照，不禁止调用者之后修改 builder；修改只影响之后打开的 repo。
WorldWorkspace 的创建与恢复都接收此 snapshot。低层独立 GraphReader/LoadedWorld 测试入口可继续自行取得 snapshot，
它们不是同一个 opened repository 内的兼容双轨，毋须为整齐而改造全部低层 API。

## 3. 固定配置之外的语义合同

完整根据见 [DB-076 §4.2](0076-efficient-graph-fork-technical-path.md#42-每个-opened-repo-固定一份模型执行环境)。本片须把以下要求写入现有公开入口的 XML/接入文档；DB-078-A 改名时保留这些合同：

| 提供者 | 必须承担的合同 |
|---|---|
| 历史 Normalize/Upgrade | 固定显式输入与环境决定结果；不修改输入；历史升级产出 canonical current DTO |
| Capture | 输出 canonical current DTO，保持完整表示及对象身份；current 恒等不要求包装对象 ReferenceEquals |
| reader、引用遍历、比较、binding factory | 语义稳定；成功闭合可复用；调用次数不能决定业务结果 |
| comparer/resolver | 所属环境内 equality/hash 与解析策略稳定；持有同一个可变 comparer 对象不等于满足合同 |
| Allocate/Hydrate | mutable 恢复得到新的 exact 实例；只填充本次目标，不修改共享 DTO/已交付图，不泄露半图 |

**exact-current 恒等由 durable binding 的库内快路保证。**
在 [StateModelBinding<TDomain,TState>.Normalize](../../src/DurableGraph/Runtime/Binding/StateModelBinding.cs) 中保留 RequireSource：
完整 source layout 等于 CurrentLayout 时直接读取 exact `TState`，保留 ObjectId/完整值，
构造带当前 `_preparation` 的 current row；只有历史输入调用原 normalize 委托。
不能只比较 Schema key/version，也不能直接 `return source`，因为 reader 的 row 可能尚无 current preparation。
错误 exact layout/DTO 类型仍拒绝，不借用户回调把它们“修正”为可接受输入。

因此 current 输入不执行手写 `x+1` 或初始化回调；这不是分析/检测委托纯度，而是移除该执行路径。
构造参数可继续名为 `normalize`，XML 须明确它负责历史转换；不为重命名另做一轮公开接口迁移。
历史转换、Capture、Hydrate/comparer 等仍承担上表合同。诊断计数不应决定结果，也不提供一般的回调次数承诺；
“exact current 不调用历史转换委托”则是本片明确保证。业务初始化/副作用在物化交付后执行。

这个快路的权威边界必须同时落实：产品 Normalize 前仍通过 repo 的 `ResolveModel` 取得/复核 binding，
当前唯一产品调用点是 [NormalizedRevision.Create](../../src/DurableGraph.Persistence/NormalizedRevision.cs)，
它经 StateModelSnapshot 检查完整 current Schema 的 live 权威。
内部 bare binding.Normalize 不单独承担 repository 权威验证；binary 静态 binding 原本也没有这种 context。
**公共 `StateBindingContext.Normalize<T>` 的零步 requirement 验证保持不变**；历史转换及其完整依赖检查保持。
不增加公共 context 参数/validation hook，也不在 NormalizedRevision 一次跳过所有 ObjectBinding.Normalize：
Array/List/Dictionary 的同布局 NormalizeState 仍有依赖复核职责，本片保持并回归这些路径。

冻结代码配置不冻结 SchemaStore：后续新增的权威 Schema 仍须在闭合缓存命中时复核。
保留 `StateBindingContext` 的 exact requirement 检查，不以稳定 binding identity 为由跳过它。
Schema 依赖须体现在 exact layouts、声明的 Upgrade 依赖，或经过同一 repo context 的标准 Resolve/Normalize 验证；
不通过私有 context 或隐藏的预计算布局规避权威检查。后续 [DB-080](0080-prepared-checkpoint-reuse-slice.md) 复用这些检查的依赖证据，不分析任意委托闭包。

## 4. 施工单元

| 单元 | 可独立承担的工作 | 汇合条件 |
|---|---|---|
| G0 合同与见证 | 新 API 编译见证、固定环境/current 快路/惰性闭合测试 | 明确旧政策测试怎样更新，先保留失败证据 |
| G1 核心接线 | Repository 持有 snapshot；WorldWorkspace 创建接缝；registry 参数和 XML 迁移；durable current 快路 | 不改变发布、恢复、每仓库一个工作副本政策，不绕过 context 权威 |
| G2 消费者迁移 | src/tests、活动 experiments、根 README/真实包消费者改为 Open 提供 models | 删除逐操作 registry 重载，不批量改写历史 DB/归档 |
| G3 独立验收 | 审阅配置寿命、打开失败清理、live Schema、包输出 | 合并后由主会话运行构建与测试 |

参数迁移可能涉及较多文件，current 快路与权威检查仍须独立语义验收，不能把整片估为机械改名。
不顺带重命名 Snapshot 类型、重写绑定目录，或提前实施 DB-078 的公共产品形状。

## 5. 验收矩阵

| 场景 | 最小可观察结果 |
|---|---|
| 同 repo 连续 Read/Resume，普通与强制 Family | 使用同一环境；成功 factory/comparer 闭合不按操作重做；结果正确 |
| Open 后 builder 追加能力 | 当前 repo 看不到；使用更新 builder 重新打开后可见 |
| 不完整目录 | Event-only 目录单独打开可读相应 Event，不要求 State 的 current 能力；尝试恢复缺能力的 State 正常失败 |
| 手写 current 委托为 x+1 或 throw | exact current 不执行该委托，值/ID 保持且带当前 preparation；错误 layout/typed DTO 不被回调救回 |
| 普通/Family current 与历史 DTO | current 快路一致；相邻历史 Upgrade 正确且不改输入，真实转换错误仍传播 |
| 先闭合后注册冲突 Schema | 同 repo 的 current 读取仍在快路前拒绝；公共 context 的零步冲突检查继续通过回归 |
| Array/List/Dictionary 同布局 | 既有 authority 复核/恒等值保持，不被 durable 快路广域短路 |
| 打开/恢复失败、只读打开 | 资源正确释放；不交付部分图；只读不写盘 |
| 固定环境与真包 | XML 明确模型寿命；仓内调用、README 和 EventHistory 真实包消费者均实际编译 Open 提供 models、后续操作不传 models；至少含强制 Family 接入 |

证据入口：[GenericBindingCatalogTests](../../tests/DurableGraph.Persistence.Tests/GenericBindingCatalogTests.cs)、
[SharedEventHistoryTests](../../tests/DurableGraph.Persistence.Tests/SharedEventHistoryTests.cs)、
[GeneratedStateModelTests](../../tests/DurableGraph.Tests/GeneratedStateModelTests.cs)、
[EventHistoryConsumer](../../experiments/PackageConsumerProbe/EventHistoryConsumer/Program.cs)。
同 repo 切换坏 allocator/好 registry 的测试改为固定环境的故障测试或另开 repo；
真实包的精简/完整目录切换改为分别打开，不能为旧测试保留逐操作覆盖参数。
低层测试若仍在测试独立 snapshot，则保持其自身范围，不误改为 repo 寿命测试。
实施前 `SharedGraphReaderTests.SameLayoutNormalizationMustCompareCompleteCurrentValuesAndReferences` 以调用计数
改写同一 current DTO 的值/引用；其产品保证角色随新合同退休，替换为上述 current 不执行历史委托的见证。
保留不同 head、真实历史 Upgrade 和 changed-child 的合法闭包隔离测试；不要为了旧动态行为保留 current 回调。
这不授权提前删除 ReadPair 的完整值比较、RequiresRewrite 排除或引用闭包，相关算法简化仍属后续独立证明。

## 6. 验证与停点

按 [总路线的共通验收](0076-efficient-graph-fork-technical-path.md#10-分片施工导航) 执行根构建、相关测试、全量回归和受影响真实包消费者。
本片特别核对仓内调用、XML、README 原文和真实包消费者的模型参数迁移完整性。
只读 ReadState/Event/ReadPair、MoveBranch 与 ref-only `CreateBranch(string, GraphFrame)` 的既有协议在本片不变。
完成后更新本片状态/证据和 PROJECT-STATE；此时仍不是 DB-083 产品 API 已完成。
下一阶段是 DB-078-A，先读取本片实际结果再安排独立会话；不自动连做，也不因已有稳定 binding 就提前添加缓存。
