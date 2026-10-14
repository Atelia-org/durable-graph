# DB-029：已准备对象内容到可追加 Revision

> 历史迁移（2026-10-14）：原路径 `docs/design-branches/0029-prepared-object-revision-planning-slice.md`；来源修订 `8ef9fca`，正文保留原时期语义。
> 适用范围：SG/string PrepareBase、prepared rows、B/D/H 与 raw Revision 的已验收分片；typed 适配仍为见证，当片不含完整 Save/发布。 今日事实从 [PROJECT-STATE](../../../src/PROJECT-STATE.md) 续接。

> 状态：Chosen / Implemented — 用户已采纳 PrepareBase/PrepareDelta，实施与验证见 §9。
> 日期：2026-09-06；核对源码基线 `b2d0e09`，规划起点工作区干净。
> 实施授权来自本轮用户请求；本片不包含完整 Save 或发布。

## 1. 问题与最小成功判据

已有 frozen DTO、融合 PreparedDelta、持久原始对象链与 H，以及独立表示策略。
下一片回答：给定调用方冻结的完整 post-live 对象集合及已经准备好的内容，能否依据 exact Parent，
自动产生真实 Base/Delta records 和 membership Removes，交给现有 Append 落盘？

最小见证：真实 SG 捕获并准备内容，产品规划器产生 Revision，通过现有 Storage.Append 保存；
再次修改并保存，冷重开后按 DB-028 链重建得到独立预期。未改对象沿用旧 head，变化对象
使用策略选出的表示；未进入候选成员集合的 Parent 成员在新 Revision 中被 Remove，旧 Revision 仍可读取。

本片落点是 **已准备内容 → 估算 → policy → 可追加 Revision**。
Capture 到 prepared rows 的强类型适配在集成测试中显式编写，不宣称产品通用对象列表比较器已完成。

## 2. 选片依据与取舍

| 候选 | 收益 / 额外边界 | 推荐 |
|---|---|---|
| 从 prepared 内容接策略和 Revision | 首次让现有策略决定真实对象记录，消费 DB-027/028；无需新类型权威 | 本片 |
| 一次做 Capture 到完整 Save | 还需 heterogeneous DTO 分派、baseline 生命周期、根目录和发布结果 | 拆开 |
| 先持久 TypeCodec / Schema 目录 | 补足冷读解释元数据，但同时要裁决 Schema canonical 格式与持久引用 | 保留紧邻候选，不作为本片前置 |
| struct / 一般引用 / Restore | 扩充类型或恢复能力，但不接通现有策略消费者 | 可独立穿插，不改既有 TODO |

规划起点 `b2d0e09` 的源码事实（完成能力见 §9）：

- [CapturedObject](../../../src/DurableGraph/CapturedObject.cs) 用 GetState<TState> 取得 exact DTO；
  尚无通用 Write/PrepareDelta 分派。StateStore 目前只引用 Storage，不引用 DurableGraph runtime。
- [PreparedDelta](../../../src/DurableGraph.StateStore.Serialization/Serialization/PreparedDelta.cs)
  拥有 payload 和 HasChanges，不认证 Schema、prior 或两份 DTO 的对应关系。
- [策略](../../../src/DurableGraph.Persistence/ReadAmplificationBaseBudgetPolicy.cs)消费完整 post-live 估算，
  但 [ObjectSaveEstimate](../../../src/DurableGraph.Persistence/ObjectSaveEstimate.cs) 的 Update 当前强制要求 D/H。
- [Storage](../../../src/DurableGraph.Storage/StateRevisionStore.cs) 已能取得 exact Parent map、
  按对象读取完整链和 H，并预检 Delta 的直接 prior；Append 不发布 head。

主代理与设计 subagent 一致推荐较小边界；独立事实调查另核对了 rollover 和 wire 计量接缝。
不新增程序集、通用 codec registry 或 CLR 反射遍历。

## 3. 已采纳的数据流与职责

```text
测试中的显式 typed 适配
  frozen current/prior DTO → SG PrepareBase + PrepareDelta → 完整 prepared rows
                                                       ↓
StateStore: exact Parent 校验 → H / B / D → 固定 policy → StateRevision
                                                       ↓
调用方: 现有 Storage.Append(revision) → candidate address
```

规划器消费 `Storage + exact Parent? + complete prepared rows + 两个整数策略参数`，
输出不可变的待追加 Revision，并保留最少量的估算/选择结果供验收。
名称可暂用 `PrepareRevision` / `PreparedObjectRevision`，施工时随实际消费者收敛。
优先保持 StateStore 当前内部 API 范围；真实 SG 集成测试可增加项目引用及定向测试可见性，
不为测试方便把整个策略表面公开。

产品规划器不调用 CaptureSession.Accept/Discard、不持有领域对象、不选择或发布最新 head。
输出通过既有 `store.Append(result.Revision)` 执行，无需额外 Append coordinator。

### 3.1 内容输入及所有权

每行至少有非零 ObjectId、拥有 current Base bytes 的 PreparedBase；existing 行还声明 exact prior FrameAddress。
推荐工厂表达以下逻辑形状，避免零长度 payload 被误判为无变化：

| 输入形状 | 内容与含义 |
|---|---|
| New | 无 prior；完整 Base body；Parent 中必须没有该 ID |
| Unchanged | 有 prior；完整 current Base body；producer 明确声明 durable 状态无变化 |
| Compared | 有 prior；完整 Base body 和同一 prior/current 对的 PreparedDelta；变化分类由 HasChanges 导出 |
| BaseOnlyUpdate | 有 prior；完整 Base body；producer 明确要求重写，但没有合法 Delta |

Compared 的 HasChanges=false 归入 NoChange，不执行其零位图 payload；true 才形成可 Delta Update。
不增加独立 StateEquals 或 EstimateDelta，也不让调用方再提供一份可矛盾的变化布尔值。
Unchanged 工厂供 immutable string 等无需生成 Delta 的来源使用，不能用 payload.Length==0 推断。

本片新增公开 sealed PreparedBase，构造时复制输入，暴露只读 Payload；不含 HasChanges。
SG 对每个已支持 current/history Vn 生成 PrepareBase(in Vn)，创建缓冲并调用已有 Write，
不生成第二份字段遍历/EstimateBase。string 内容 codec 提供预制 PrepareBase。

对所有 live 对象各准备一次 Base body，保证可选 Base 随时能执行；即使最后只写 Delta，
也不重做 DTO 比较或 Delta 编码。先接受全量 Base 编码及临时内存成本，性能优化留到 MVP 后。
单 Frame 约 256 MB 的限制不约束全部候选 Base/Delta、DTO 与缓冲副本的内存总量。
输入 bytes 和集合需真正冻结；读取视图不能泄漏可写 backing array。
仅在缓冲复制、全量 Base 准备或 H 读取等实际位置留少量性能 TODO；不实现池/租约/缓存。

Base/Delta **body** 的长度都来自准备结果。§4 只计量 Storage envelope，
Delta 地址尚未确定的 distance 仍采用最多高估 4 字节的既定上界，不另加业务字段估算器。

string 仍是同一列表中的对象。持续存活的同一 string ID 为 Unchanged；内容相等的不同非空实例
仍是不同 ID，新 string 写 Base。零长度 string 遵守既有 Empty 规则。本片不新增 string Delta。
一般 BaseOnlyUpdate 可以表达同 Schema 暂无 Delta 的 codec；跨 Schema 也不得调用同版 Delta，
但这只是 raw 消费能力，不等于 DTO 升级、类型目录或跨版本 Save 已实现。

### 3.2 Parent、身份与 H

规划器从指定 Parent 读取完整 live map，检查全部输入后再产生结果：

- Parent=null 时所有行必须为 New；否则 New 必须不在 Parent 中。
- 每个 existing 行必须在 Parent 中，claimed prior 必须等于 exact Parent Revision 选定的
  该 ObjectId 对象 head 完整 FrameAddress。
- 拒绝重复/零 ID、无效地址、错误形状和错误 prior；不按最大文件号或最近 Revision 猜 Parent。
- NoChange 和可 Delta Update 使用 `ReadObjectVersionChain(parent, id)` 的实际 ReconstructionBytes。
  不接收外部任意填写的 H。暂按对象逐条读取，沿用 DB-028 的缓存 TODO。
- BaseOnlyUpdate 无需读取旧内容链或 H，因为新 Base 截断它；仍必须通过 membership/prior 校验。
  保留 DB-028 允许新 Base 截断坏旧 Delta 的能力，不把本片变成全历史审计。

raw 层不能证明 prepared bytes 真是基于所声明 prior 产生的，也不能判断 Unchanged 的内容是否相等。
这一责任仍属于 typed producer：它须使用 exact Parent 对应的 frozen baseline、检查 exact Schema，
再调用正确版本生成函数。测试显式构造这一对应，不伪造产品级 baseline witness 或持久类型认证。

完整 rows 是调用方给定的完整 post-live 对象集合，即候选成员集合；StateStore 可由 Parent membership
减去这些 IDs 得到 Removes。
这不是 Storage 推断 reachability：Storage 仍只保存调用方最终提供的移除集合。
ID 相同不自动证明跨会话/跨 Revision 的实体连续性；本片仍使用同一 CaptureSession 的单调 ID，
不导入任意 reopen 实例映射，不实现数字回收。

## 4. B/D/H 与目标 Segment 的分歧

### 4.1 已采纳：scope-independent 的小幅保守 D

当前 v3 对象 payload 排除 ObjectId key，包含 kind、Delta prior、body 长度和 body。
令 V32/V64 为现有 canonical unsigned varint 编码长度，n 为对应 body 字节数：

```text
B      = 1 + V32(baseBody.Length) + baseBody.Length
D_est  = 1 + 5 + V64(prior.FrameTicket.Serialize())
           + V32(deltaBody.Length) + deltaBody.Length
H      = DB-028 从旧记录原 Frame 实编码汇总的 ReconstructionBytes
```

Delta prior 的 BackwardFileDistance 是 UInt32，编码长度为 1..5；唯一预先未知的这一项按 5 计。
对同一 v3 record 的任何合法目标 scope，`0 <= D_est - D_actual <= 4`。
B 是精确对象 Base payload 成本，H 是盘上实际对象链成本；D 是明确标记的保守估算。
尺寸运算先提升到 long 再 checked 汇总，策略继续使用现有 Int128 比较交叉乘积；排除共享 Frame/membership/对齐。
未来增加类型头必须同步更新 B/D/H，不能继续套用本片的 v3 公式。

这保持用户要求的“大致估算”接缝，且不低估新增 Delta 的对象 payload。
代价：在 B<=D 或放大阈值附近可能更早选择 Base；不同对象的候选排序也可能变化。
上界误差小不代表所有选择都与 exact D 相同，尤其小对象；最终仍不是总写入硬峰值保证。
可选 Base 按完整 B 收费及预算语义不变，强制写入仍不消耗可选预算。

计量逻辑应放在 Storage 的小型对象 payload 计量接缝中，由 StateStore 消费，避免上层复制 wire 知识。
不通过写出整个临时 Frame 才计量；独立 wire golden 测试证明公式与实际字节的关系。

### 4.2 为什么暂不冻结 writer scope

[StateRevisionStore.Append](../../../src/DurableGraph.Storage/StateRevisionStore.cs) 内部取得 writer lease
后才确定实际 Segment。底层 [OpenActiveWriter](../../../../atelia/src/RbfSegmentStore/RbfSegmentStore.cs)
会先按旧 tail 判断 rollover；调用前的 ActiveSegmentNumber 不是本次目标 scope 的承诺。
持有 active writer lease 时，底层也不允许再打开同 active Segment 的 reader。

| 方案 | 取舍 |
|---|---|
| 固定 distance 5 bytes 上界 | 推荐；计划不依赖目标 scope，正常 Append 即可，误差有清晰上界 |
| lease 后 callback 内精确规划 | 可行但需先完成 Parent/H 读取，并设计 callback 失败、lease 与 rollover 的边界；本片不引入 |
| 预留 scope / 预测 rollover / 两遍重新规划 | 多一套位置承诺或调度状态；没有当前收益支撑 |
| 直接把 Delta body.Length 当 D | 漏算 envelope；拒绝 |

此处沿用已采纳的 envelope 计量取舍，不把近似值伪称“真实编码 D”。若后续实测门槛误差重要，
再改为读取预检完成后、同 writer scope 内精确计量和规划；不修改持久 wire 来消除估算问题。

## 5. 最小策略扩展与 Revision 构造

为 policy 输入增加显式 BaseOnlyUpdate，B 有值，D/H 均不适用。
其语义是 required Base，计入完整 graphBaseBytes，但不消耗可选 Base budget；不参加放大候选排序。
不得伪装为 Insert，或伪造 D=B / D=MaxValue。原 Insert/Update/NoChange 的规则全部保持。

转换 policy 稀疏选择为 owned ObjectVersionRecord：Base 取已准备 Base bytes；Delta 复用 PreparedDelta
payload，prior 使用已校验地址。未选写入的 NoChange 沿用旧 head，不能把稀疏 writes 当完整 membership。

Parent=null 生成 map Base；有 Parent 固定生成 map Delta，附完整集合差分所得 Removes。
本片不引入 map checkpoint policy。所有内容都未写入时也允许空 local 的 Revision；
是否省略整个提交留给未来同时处理 roots/发布的上层。

Prepare 阶段只读文件，不追加、不安装 baseline。失败不返回半成品，也不调用 Accept。
Append 之后仍只得到 candidate address；调用方仍负责发布和之后的基线安装。
测试可显式选择其下一轮 baseline，但不能用测试中的 Capture.Accept 冒充持久提交协议。
准备结果绑定显式 Parent；期间即使另有 Revision 被追加，它仍可作为该 Parent 的分支追加。
这不是 latest-head/expected-parent 发布围栏，也不提供并发提交检查。

## 6. 验收与子任务划分

| 验收 | 最小见证 |
|---|---|
| 计量符合 v3 | 手写 Base/Delta 字节期望；body 127/128、distance 127/128 及更高 varint 边界、不同 ticket 长度；B 精确、D 上界误差 0..4、H 取原 scope；构造 D_actual < B <= D_est，验证保守估算确实可能选 required Base |
| policy 扩展不漂移 | BaseOnlyUpdate 强制 Base 且不占可选预算；原 strict ratio、首候选超额、最长前缀无回填、并列 ID、溢出及输入检查回归 |
| 完整集合到 records | Insert、Delta Update、B<=D required Base、可选 Base、未选 NoChange、Remove；两种参数确实影响输出，移除行不进入预算总量；空 post-live 表达 Remove all，全部 NoChange 不丢 membership |
| prior / 错误输入 | stale/另一分支/full ticket、重复 ID、New 已存在、existing 不存在、坏旧链应按需拒绝；BaseOnly 可以截断坏旧链 |
| 所有权与失败 | 改动原数组/集合不影响结果；HasChanges=false 非空位图仍无业务变化；prepare 失败不写 Frame、不触发 rollover、不换 baseline |
| 真实集成 | SG frozen DTO/string rows → 产品 Prepare → 正常 Append；连续改动中同时保留/替换/移除对象，跨 Segment 冷重开后 Read/Apply 和完整引用校验 |
| 选择效果 | 变化对象写 Delta 后 H 增长；后续改参/放大动机可选择 Base 并重置 H；未改高放大对象也能被策略主动 Base |
| 范围声明 | typed kind/exact Schema/roots 仍是显式 fixture 元数据；不宣称自描述图、Restore、发布、reopen 身份接续已完成 |

施工建议：

1. 主代理先冻结输入形状、BaseOnlyUpdate 和计量合同；不先扩通用保存接口。
2. 子任务 A：Storage 计量接缝与独立 wire 测试。
3. 子任务 B：policy 最小扩展及 StateStore 规划器、模型与文件测试；依赖 A 的计量合同。
4. 子任务 C：真实 SG/Capture 集成见证；使用产品规划器，不能在 fixture 重写 policy。
5. 主代理整合，独立 reviewer 检查成本口径、Parent 责任、只读集合与失败边界；完成根 build 和相关/全套测试。

若实现改变 Generator/runtime 包交付边界，按 PackageConsumerProbe 验证；仅测试项目接 StateStore
不能当作已经验证新的公开 StateStore 包消费者。实际验证见 §9；DB-028 的 607 项仅是此前证据。

## 7. 停止条件与后续接缝

上述产品规划器、真实 Append 冷重开和拒绝案例通过即结束，不连做完整 Save。
下一候选再在“类型/Schema 持久解释”与“Capture 到 prepared rows 的产品强类型适配及 exact baseline”
之间选择。roots 持久化、DTO upgrade/Restore、一般引用、struct、BCL、ID 回收、Frame cache、
publication/reconcile 均保持各自路线，不为本片预制接口。

## 8. 规划阶段核对

当前源码与 wire/Segment lease 路径已核对；独立设计讨论与另一位 reviewer 审查无阻塞意见。
审查补入清空 live 集合、全量 NoChange，以及估算偏差改变 required Base 选择的明确验收。
规划提交 `6b1c82e` 只改四份规划/导航文档，检查本地文件链接与 Git diff；当时未修改产品代码、未重跑 build/tests。
此前 DB-028 的测试结果不视为本片实现或验证结果。

## 9. 实施合同与账本

实施起点 `6b1c82e`，worktree 干净。保留 v3 wire 和现有 Append；不修改上游 RBF。
本片固定以下接缝，内部细节可随证据收敛：

- Serialization: public PreparedBase(ReadOnlySpan<byte>) / Payload；SG PrepareBase 与 StringPayloadCodec.PrepareBase(string) 返回它。
- Storage: public ObjectVersionPayloadSize.GetBaseBytes(int bodyLength)、EstimateDeltaBytes(int bodyLength, FrameAddress prior)。
- StateStore: internal PreparedObject.New/Unchanged/Compared/BaseOnlyUpdate，现有对象参数顺序 id、prior、PreparedBase、可选 PreparedDelta。
- internal ObjectRevisionPlanner.PrepareRevision(store, parentRevisionAddress, objects, parameters)
  返回 PreparedObjectRevision，含 Revision、Estimates、RepresentationPlan；输出集合真正只读。
- 只向集成测试开放 StateStore friend access；不增加 DurableGraph runtime 依赖或通用 typed registry。

| 要求 | 负责人 / 路径 | 验证 | 状态 |
|---|---|---|---|
| PreparedBase、SG 各版 helper、string helper | 子任务 A / Serialization + Generator | owned bytes、真实生成各版 DTO、包消费者 | 已验证 |
| v3 payload 计量 | 子任务 B / Storage | 独立 wire 和 varint 边界 | 已验证 |
| BaseOnly policy、prepared rows、Revision planner | 子任务 C / StateStore | 输入/Parent/H/预算/membership/失败/冻结 | 已验证 |
| 真实 SG 内容到策略再到冷重开 | 集成子任务 / DurableGraph.Tests | frozen DTO、字符串、正常 Append、Read/Apply | 已验证 |
| 整合、独立审查、文档、最终验证 | 主代理 + reviewer | 根 build/tests、PackageConsumerProbe、diff | 已验证 |

实施前基线验证：`dotnet build DurableGraph.slnx --verbosity quiet` 为 0 警告/错误；
`dotnet test DurableGraph.slnx --no-build --verbosity quiet` 为 607/607（341+96+130+40），无跳过。
此项是基线，完成后的整合结果另记。

最终实现入口：

- [PreparedBase](../../../src/DurableGraph.StateStore.Serialization/Serialization/PreparedBase.cs) 与
  [StringPayloadCodec.PrepareBase](../../../src/DurableGraph.Serialization/StringPayloadCodec.cs)；
  [SG helper](../../../src/DurableGraph.Generator/DurableSchemaGenerator.BinaryBody.cs) 对每个 Vn 复用 Write。
- [ObjectVersionPayloadSize](../../../src/DurableGraph.Storage/ObjectVersionPayloadSize.cs) 集中 v3 envelope 计量；
  [独立 wire 测试](../../../tests/DurableGraph.Storage.Tests/ObjectVersionPayloadSizeTests.cs) 验证 B 精确、D 超额 0..4。
- [PreparedObject](../../../src/DurableGraph.Persistence/PreparedObject.cs)、
  [ObjectRevisionPlanner](../../../src/DurableGraph.Persistence/ObjectRevisionPlanner.cs)、
  [结果](../../../src/DurableGraph.Persistence/PreparedObjectRevision.cs) 连接固定 policy 与可追加 Revision。
  Estimates 与 RepresentationPlan 使用真正只读集合，同时修复旧计划经 ICollection.SyncRoot 泄漏数组的问题。
- [规划器文件测试](../../../tests/DurableGraph.Persistence.Tests/ObjectRevisionPlannerTests.cs) 覆盖完整集合、
  两参数、实际 H、prior/失败、BaseOnly 截断坏链、保守 D 改变选择与已准备分支。
- [真实 SG 集成](../../../tests/DurableGraph.Tests/PreparedRevisionGeneratorTests.cs) 用五轮保存验证 Base → Delta →
  Delta → 主动 Base → NoChange；包含 string 退出、共享/相等但不同实例/Empty、冻结后 mutation、
  历史可读与全 DTO 引用校验。typed 解释元数据仍由 fixture 提供。
- [生成 Base 测试](../../../tests/DurableGraph.Tests/PreparedBaseBodyTests.cs) 包含全部当前标量、string ID、
  空布局与旧 CLR 祖先已移除的历史 overload。原两处生成方法完整 whitelist 增加 PrepareBase，仍保持严格断言。

2026-09-06 最终集中验证：

- `dotnet build DurableGraph.slnx --verbosity quiet`：0 警告、0 错误。
- `dotnet test DurableGraph.slnx --no-build --verbosity quiet`：657/657，无跳过；
  DurableGraph 346、Serialization 103、Storage 155、StateStore 53，比基线增加 50 项。
- `./experiments/PackageConsumerProbe/Run-Probe.ps1`：通过；单一 Runtime PackageReference 的实际输出
  通过脚本严格检查 `BinaryBody:012154:True:ReferenceCapture:True:StringDecoding:True:PreparedDelta:True:PreparedBase:True`。
  产物在 `experiments/PackageConsumerProbe/obj/run-20260906141521-39608`；不将该包见证解释为 StateStore 发布能力。
- 独立 reviewer 检查完整产品与测试，无未解决阻塞项。审查发现的两处 whitelist 已修复并纳入全套验证。
- 7 份修改文档的 145 个本地文件链接和新增 §9 锚点有效；修改文件 UTF-8/LF、Git diff 检查通过。
- 并行构建一度遇到 DLL 占用；集中串行构建消除争用。新 fixture 的内部 Writer 调用及 offset 断言
  在最终集中验证前修正，不增加 Serialization friend 或放松验收。

保留两处 DB-029 性能 TODO：SG 临时缓冲/owned-copy，planner 重复对象链读取。
未实现池、租约、缓存或基准测试；wire v3、Storage.Append、上游 RBF 及领域 Capture 协议保持既有职责。
完整 Save、类型目录、通用 typed baseline/分派和 publication/reconcile 均未纳入本片。
