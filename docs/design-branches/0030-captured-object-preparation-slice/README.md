# DB-030：从异构 Capture 图统一准备对象内容

> 历史迁移（2026-10-14）：原路径 `docs/design-branches/0030-captured-object-preparation-slice.md`；来源修订 `8ef9fca`，正文保留原时期语义。
> 适用范围：SG 对象级 binding 与 Runtime prepared 内容的已验收分片；当片不含 exact Parent baseline、持久类型或 Save/发布。 今日事实从 [PROJECT-STATE](../../../src/PROJECT-STATE.md) 续接。
> 名称后继：[DB-092 D1](../0092-type-namespace-refactoring/implementation-d1.md)将 `CapturedStatePreparation<T>` 改为 `StatePreparationBinding<T>`；本文保留当片名称与证据，不作为当前 API 拼写参考。

> 状态：Chosen / Implemented — 用户已采纳，实施与验证见 §7。
> 日期：2026-09-06；源码核对基线 `0755a56`，规划开始时工作区干净。
> 实施授权来自用户本轮请求；新增工作会话方向只记录目标，不扩大本片为完整 Commit。

## 1. 问题与最小成功判据

DB-029 已接通 prepared rows → B/D/H → policy → 可追加 Revision。
但 [真实 SG 集成](../../../tests/DurableGraph.Tests/PreparedRevisionGeneratorTests.cs) 仍手写领域类型知识：
取 current/prior DTO，调用对应 PrepareBase/PrepareDelta，再转换为 rows。

本片回答：SG 能否在 Capture 登记时绑定这些已知操作，让 Runtime 对异构对象列表统一准备内容，
调用方不再逐个认识 DTO 类型？

最小成功判据：至少两个不同的 generated 根类型（包含继承）及 string，通过一次统一 Prepare
得到完整 post-live 内容；fixture 只按 ID 查 Storage prior 并机械映射到 DB-029，便能完成真实
Base → Delta → 策略主动 Base → NoChange、new/remove 和冷重开验证。
保存适配代码不再出现领域类型 switch、GetState<T> 或手写 PrepareDelta 调用。
读取 fixture 仍可显式选择 codec/exact Schema；不把这一成功判据扩大为通用 Load。

## 2. 为什么选择这片

| 候选 | 收益与代价 | 本轮建议 |
|---|---|---|
| 纯 typed prepared-content projection | 消除现有保存链的人工类型适配，直接消费 frozen DTO 与 SG body；无需新 wire | 推荐 |
| 同时做 exact Parent baseline 管理 | 需要证明 graph 确实对应某次追加，并区分追加、发布、安装与失败 | 与本片拆开 |
| 先持久 TypeCodec / Schema | 冷读可查 exact 类型；但须裁决 canonical Schema、引用方式、对象头及成本变化 | 紧邻候选，非本片前置 |
| 先自定义 struct | 扩充值布局及版本依赖，但不会消除当前列表准备的手写适配 | 保留独立穿插机会 |

两位独立设计 subagent 与主代理收敛到第一项。讨论中特别收窄了“typed 适配 / exact baseline”
这一混合候选：前者可以独立完成，后者不是加一个 `(graph, revisionAddress)` 包装就能认证。

规划起点 `0755a56` 的源码事实（实现后的接缝见 §7）：

- [CaptureContext](../../../src/DurableGraph/Runtime/Capture/CaptureContext.cs) 的 RootCapture 已配对 domain、Schema、
  DTO 和 Capture；[SG AddRoot](../../../src/DurableGraph.Generator/DurableSchemaGenerator.BinaryBody.cs)
  是注入准备操作的自然位置。
- [CapturedObject](../../../src/DurableGraph/CapturedObject.cs) 私有保存 DTO box，公开 GetState<T> 按值返回；
  Seal 后尚未保留通用的准备操作。
- [CaptureSession](../../../src/DurableGraph/Runtime/Capture/CaptureSession.cs) 的 Current 只表示已 Accept 的内存图，
  RequireCandidate 能检查当前 unresolved candidate；没有 Storage 地址或发布含义。
- [ObjectRevisionPlanner](../../../src/DurableGraph.Persistence/ObjectWritePlanner.cs) 校验 ID/prior/H，
  不认证 DTO/Schema。raw wire 没有类型头，拿 current codec 成功解码或 roundtrip 同一段 bytes，
  也不能证明该记录原本属于这个 exact Schema。

## 3. 推荐数据流与层次

```text
SG AddRoot：domain + exact Schema + DTO + Capture + preparation binding
                      ↓
Runtime Seal：完整 frozen CapturedGraph（保留对象级 binding）
                      ↓
CaptureSession.Prepare(candidate)：自动以 Current 为 previous
                      ↓
完整 prepared 内容列表 + exact previous/candidate 内存来源
                      ↓
fixture：显式对应 Parent，按 ID 填 prior address、机械转换 rows
                      ↓
既有 ObjectRevisionPlanner → 调用方 Append
```

推荐入口暂名 `CaptureSession.Prepare(CapturedGraph candidate)`；名称和私有实现可在施工时收敛。
它只接受本会话当前 sealed、unresolved candidate，previous 自动取 Current，不允许另传任意旧图。

新能力放在 DurableGraph runtime 与 Generator；准备结果复用 Serialization 的 PreparedBase/Delta。
不新增程序集，不让 Runtime 依赖 StateStore/Storage，不为这片公开 DB-029 的内部 API。
StateStore 产品代码及 v3 wire 无需变化；其消费见证继续使用测试已有 friend access。

### 3.1 对象级绑定，成员级静态调用

SG 为 current DTO 生成稳定的 preparation binding，包含 exact Schema、DTO 类型及
`PrepareBase(in TState)` / `PrepareDelta(in TState prior, in TState current)` 的强类型入口。
优先以静态字段持有每个生成类型的 binding；泛型 runtime holder 隐藏异构列表中的 TState。
需要跨生成程序集可见的最小 binding/委托表面可公开，但不引入全局注册表或 Type 查找。

每个对象允许少量委托/虚调用完成类型擦除后的分派；进入生成 body 后，字段操作仍直接绑定
原语和静态函数。不新增每字段 ValueSlotCodec、反射遍历、第二份相等性或尺寸估算实现。
SG binding 不捕获 domain 实例；冻结图只保留 DTO、不可变 string 和静态操作。
自定义 binding 须遵守确定性及不保留可变领域状态的合同；Runtime 不检查委托捕获对象，
执行 guard 也不构成对任意回调纯度的证明。

现有 capture-only `AddRoot` 保留。新增显式带 preparation binding 的路径，SG 使用该路径：

- 不带 binding 的旧调用仍可 Seal/Accept；只有请求 Prepare 时才明确拒绝。
- 重复登记同一根，除已有 Schema/DTO/Capture 检查，还要检查 preparation binding 一致；
  推荐稳定 binding 实例身份。混用 capture-only 与带 binding 的登记不能悄悄覆盖。
- 先预检完整 current 集合及需要比较的 prior：缺 binding、kind/Schema/DTO/binding 错配均拒绝，
  之后才开始 body 准备。不因某个对象未变化而跳过其 binding 检查。
- schema 相等使用完整 descriptor（含 exact ancestors），不能只比较 SchemaId/version 或 hash。

### 3.2 完整列表与分类

推荐结果暂名 `PreparedCapturedGraph`，保留只读 Previous（可 null）、Candidate 及按 ID 排序的完整 rows。
每行保留 current 对象来源、可选 previous 来源、PreparedBase、可选 PreparedDelta。
New/Unchanged/Compared 等名称可沿实际消费者收敛；工厂内部建立合法形状，不暴露任意拼装构造。

| 对象关系 | 准备行为 | 转入 DB-029 的机械映射 |
|---|---|---|
| previous 不存在该 ID（含 Current=null） | PrepareBase | New |
| 持续存活的 durable、同 exact Schema/DTO/binding | PrepareBase + 融合 PrepareDelta | Compared，变化由 HasChanges 判定 |
| 持续存活的 string ID | 预制 PrepareBase；immutable unchanged | Unchanged |
| previous 独有的 ID | 不进入 current rows | planner 按完整集合计算 Removes |

对全部 current live 对象准备 Base，不另建按需回调或缓存。HasChanges=false 可以伴随非空零位图，
不能以 payload 长度判断变化。PrepareBase 消费 Candidate 的冻结 DTO，PrepareDelta 消费
Previous/Candidate 中对应的两份冻结 DTO。

本片严格同版：kind、完整 Schema、DTO 或 binding 不匹配则拒绝，不自动退化 BaseOnlyUpdate。
当前没有合法跨版 Capture 消费者；错误绑定不能伪装成正常重写。DB-029 的 BaseOnly 能力保留，
未来由迁移/加载层在定义合法来源及重写义务后使用。

string 同样是一行对象内容，Runtime 预制其准备操作。持续存活的 ID 对应同一 immutable 实例；
相等但不同的非空实例仍按新 ID 处理，空串仍规范化 Empty，null 为 ID 0 且没有对象行。
只改变 string 引用 ID 时，owner 的生成 Delta 正常记录该槽变化。

### 3.3 候选、失败和所有权

Prepare 不分配 ObjectId、不重新 Capture、不调用 Accept/Discard、不访问文件。
先核对 candidate，再预检、准备，完整成功后才返回结果。任何失败都保持 Current 与 pending candidate，
调用方仍可重试、Discard 或 Dispose 原 context。

允许对同一候选重复 Prepare，要求内容等价，不保证结果引用相等，也不自动缓存。
不新增成功后的 Prepared 候选阶段；现有 Seal → Accept/Discard 协议仍成立。
若公开回调可重入会话，须用临时执行 guard 在 mutation 前拒绝递归 Prepare、Accept、Discard 和
pending context 的 Dispose/Resolve；finally 解除 guard。不能只在回调返回后检测已经发生的状态变化。
已解决且脱离 session 的 context.Dispose 保持幂等。回调捕获重入异常后可继续，因 mutation
尚未发生；不另加失败标记。异常传播后解除 guard，候选仍可再次 Prepare/Discard。

结果集合和 bytes 真正只读，不通过 ICollection.SyncRoot 等路径暴露数组。
Previous/Candidate 引用仅说明内存来源，不是持久 receipt；Discard 后结果 bytes 仍有效，
但候选不再可由该会话接受。结果不持有 session/context 或可变 domain。

## 4. exact Parent 接缝明确留到后片

本片不宣称消除所有保存适配：fixture 仍显式保证 Previous 对应传入 DB-029 的 exact Parent，
再由 planner 检查实际 membership/prior。结果中不出现 FrameAddress，不以 Current 命名已提交基线。

用户在规划后已采纳外层 WorkingTree/GraphSession 方向：由 Repository 的受控加载与成功提交
流程同时建立、推进 Parent、冻结 DTO 与实例身份绑定，普通消费者只使用 checkout/create 与 Commit。
其封装本身就能维护正确来源，不需要另造独立 receipt 框架。持久类型解释仍是冷加载的后续依赖。
长期约束见 [目标设计](../../DurableGraph-target-design-v0.md#单一发布权威与明确故障结果)。

因此本片的 CaptureSession.Prepare 明确是未来工作会话的内部准备组件。现有基础设施公开表面
供生成代码与机制消费者使用，不等于最终用户要手工协调 Capture/Prepare/Accept。
本片不实现 WorkingTree、Commit、加载导入或任意 `(graph, address)` 基线安装接口。

## 5. 验收与施工顺序

| 验收 | 可观察见证 |
|---|---|
| 异构列表无需手写类型适配 | 两种 generated 根、继承、共享 string；统一 Prepare 驱动 DB-029，保存桥接无 GetState<T>/领域类型 switch/直接 PrepareDelta |
| 分类与完整集合 | 初次全部 New；新加入根、变化/未变对象、引用替换、移除、空集合、重复/null roots；减少根但仍可达的 string 保留 |
| 策略和持久消费 | Base/Delta/主动 Base/NoChange、H 重置、真实 Segment 冷重开；读取元数据仍显式提供 |
| 冻结与重复准备 | Seal 后修改领域对象不影响两次 Prepare；所有 live Base 均可用，浮点位语义与零位图沿用 DB-027 |
| 严格绑定 | capture-only 缺操作、重复根不同 binding、same key 不同 descriptor、跨版本、DTO/kind 错配均失败；需要的内部反例用定向 tests 构造 |
| 会话与失败 | 错会话/已解决候选拒绝；late callback 抛错不安装状态，仍能 Discard/重试；Prepare 不烧 ID、不产生文件写入；重入在 mutation 前拒绝 |
| 包交付 | 单 Runtime PackageReference 消费 SG AddRoot + 统一 Prepare，检查实际 payload；不依赖友元或手工 analyzer 接线 |

实施建议：

1. 主代理先冻结 runtime binding、结果和失败合同，检查依赖边界。
2. 子任务 A：runtime typed holder、session Prepare 与独立失败/所有权测试。
3. 子任务 B：SG binding/AddRoot、异构生成测试、PackageConsumerProbe；按 A 的接口落地。
4. 子任务 C：真实 Storage 集成，保留无类型知识的机械桥接，不能重写 policy。
5. 独立 reviewer 检查 binding、候选重入、来源声明和范围；主代理集中串行 build/tests，避免共享 obj 争用。

完成时运行根 solution build、相关及整合 tests、PackageConsumerProbe。657/657 是 DB-029 的历史验证，
不是本片测试结果。实现通过上述判据即停止，不继续添加持久类型目录、baseline 安装、Save/发布、
DTO upgrade/Restore、一般引用、struct、数组对象、BCL 或性能设施。

## 6. 本轮规划核对

已核对当前 runtime、SG AddRoot、DB-029 planner 与真实集成源码；两位 subagent 独立评估后共同
推荐把纯 preparation 与 exact Parent baseline 分开。规划阶段只维护本文件、PROJECT、路线和索引，
不更改目标设计中已选约束，不修改产品代码或重跑 build/tests。
独立 reviewer 核对规划无阻塞项，补清自定义回调合同、重入拒绝后的行为及 prior/current DTO 来源。
四份 Markdown 的 102 个本地文件链接及引用锚点检查通过，文件为 UTF-8/LF；Git diff 检查通过。

## 7. 实施合同与账本

实施起点 `e62ec7f`，工作区干净。根 build 基线 0 警告/错误、全套 tests 657/657；完成结果另记如下。
本轮新增 WorkingTree 方向的目标约束，DB-030 范围仍为统一内容准备；没有 Storage wire、程序集
依赖或上游 RBF 变化。主代理集中运行 dotnet 验证，各实施子任务不并行构建共享 obj。

冻结跨模块接缝：

- public `StateBasePreparer<TState>(in TState)` / `StateDeltaPreparer<TState>(in TState prior, in TState current)`，
  TState : unmanaged，分别返回 PreparedBase / PreparedDelta。
- public sealed `CapturedStatePreparation<TState>(schema, prepareBase, prepareDelta)`；Schema 只读，binding 实例身份稳定。
- `CaptureContext.AddRoot(value, schema, capture, preparation)` 新四参数路径；原三参数 capture-only 路径保留。
- `CaptureSession.Prepare(candidate)` 返回 `PreparedCapturedGraph`：Previous、Candidate、只读 Objects。
- `PreparedCapturedObject`：Current、可 null 的 Previous、BaseContent、可 null 的 DeltaContent；构造由内部控制。
  Previous=null 映射 New；existing + DeltaContent=null 映射 string Unchanged；其余映射 Compared。

| 合同 | 负责人 / 路径 | 验证 | 状态 |
|---|---|---|---|
| typed holder、完整预检/准备、候选 guard 与所有权 | runtime 子任务 / DurableGraph | CapturedGraphPreparationTests | 已验证 |
| SG 稳定 binding、静态 body 调用、异构/历史支持 | generator 子任务 / Generator + tests | GeneratedCapturePreparationTests | 已验证 |
| 实际包消费 | generator 子任务 / PackageConsumerProbe | 单 Runtime PackageReference | 已验证 |
| 无 DTO 知识的保存桥接与真实冷重开 | integration 子任务 / PreparedRevisionGeneratorTests | 异构根、new/remove、Base/Delta/H | 已验证 |
| 独立审查、集中验证、文档 | 主代理 + reviewer | 根 build/tests、包、链接/diff | 已验证 |

最终入口与证据：

- [CapturedStatePreparation](../../../src/DurableGraph/Runtime/Capture/StatePreparationBinding.cs) 实现对象级 typed 桥接，
  [CaptureSession.Prepare](../../../src/DurableGraph/Runtime/Capture/CaptureSession.cs) 完整预检后准备内容，
  [PreparedCapturedGraph](../../../src/DurableGraph/Runtime/Capture/PreparedCapturedGraph.cs) 保存来源及只读 rows。
- [CaptureContext](../../../src/DurableGraph/Runtime/Capture/CaptureContext.cs) 保留 capture-only overload，检查重复根 binding，
  并在 Resolve 前检查临时 guard；无效的新 overload 登记仍遵循原 AbortBuild/烧号规则。
- [生成器](../../../src/DurableGraph.Generator/DurableSchemaGenerator.BinaryBody.cs) 仅为 concrete current DTO
  生成 private static readonly Preparation 字段，绑定已有方法组并传给 AddRoot；未增加字段遍历或历史 binding。
- [runtime tests](../../../tests/DurableGraph.Tests/CapturedGraphPreparationTests.cs) 的 26 项覆盖完整分类、
  后置预检失败零回调、Schema/祖先/DTO/binding 错配、Base/Delta late failure、null 返回、
  重试与不烧 ID、caught/propagated 重入和 Dispose 幂等、不可变结果。
- [SG tests](../../../tests/DurableGraph.Tests/GeneratedCapturePreparationTests.cs) 的 2 项覆盖异构继承/string、
  冻结与重复、current V2 绑定及独立 V1 body。原严格方法 whitelist 无需修改。
- [真实集成](../../../tests/DurableGraph.Tests/PreparedRevisionGeneratorTests.cs) 改为统一 Prepare，保存桥接
  不再调用 GetState<T>/PrepareDelta 或分派领域 DTO；五轮真实文件验证 owner Delta、Tag 根加入/移除、
  string 保留/替换、主动 Base、NoChange、H 重置及冷重开。读取仍显式持有类型和 roots 元数据。

2026-09-06 主代理集中验证：

- `dotnet build DurableGraph.slnx --verbosity quiet`：0 警告、0 错误。
- `dotnet test tests/DurableGraph.Tests/DurableGraph.Tests.csproj --no-build --filter "FullyQualifiedName~CapturedGraphPreparation|FullyQualifiedName~GeneratedCapturePreparation" --verbosity quiet`：29/29。
- `dotnet test DurableGraph.slnx --no-build --verbosity quiet`：685/685，无跳过；DurableGraph 374、
  Serialization 103、Storage 155、StateStore 53；比基线净增加 28 项。
- `./experiments/PackageConsumerProbe/Run-Probe.ps1`：通过。实际单 Runtime PackageReference 的
  [统一 Prepare 消费](../../../experiments/PackageConsumerProbe/Consumer/Domain.Preparation.cs) 核对独立 goldens，
  脚本严格验证输出后缀 `PreparedBase:True:CapturePreparation:True`，history count 为 7。
  产物：`experiments/PackageConsumerProbe/obj/run-20260906152511-32412`。
- 独立 reviewer 审查最终产品、测试、包和目标文档，无未解决阻塞项。主代理复查实际 diff 与执行结果。
- 19 个修改文件为 UTF-8/LF；6 份 Markdown 的 132 个本地文件链接和引用锚点有效，Git diff 检查通过。

本片未改 StateStore/Storage 产品代码、wire、项目依赖或上游框架；未实现 WorkingTree、Commit、
持久 baseline、TypeCodec/Schema 目录或加载导入。自定义 callback 的确定性由合同保证，guard 不认证其纯度。
