# DB-027：同 exact Schema 的 DTO 比较与字段 Delta body

> 历史迁移（2026-10-14）：原路径 `docs/design-branches/0027-generated-same-schema-delta-body-slice.md`；来源修订 `8ef9fca`，正文保留原时期语义。
> 适用范围：SG 融合 PrepareDelta、owned payload 与 Apply 的已验收分片；当片不含持久 prior 链或 Save。 今日事实从 [PROJECT-STATE](../../../src/PROJECT-STATE.md) 续接。

> 状态：Chosen / Implemented — 用户采纳融合建议；本片已实施并通过独立审查及集中验证。
> 日期：2026-09-06。规划时 HEAD 为 `0e5608d`，产品内容证据基线为 `54a33df`。
> 本文记录本轮实施合同；授权来自用户请求，不冻结最终对象持久格式。

## 1. 本片回答什么

给定同一 exact Schema 的两个已冻结 DTO，能否由 SG 静态生成代码，准确判断持久状态是否变化，
并将 prior DTO 与实际 Delta bytes 重建为 current DTO，保持与完整 Base 编码相同的值和位信息？

最小成功判据：对当前支持的所有槽位类型、继承及历史 Vn，
`ApplyDelta(prior, PrepareDelta(prior, current).Payload)` 的完整 Base bytes 与 `Write(current)` 完全一致；
`!HasChanges` 与完整 Base bytes 的相等一致。错误输入失败，prior 与已封存候选不被修改。

这提供下一轮对象版本链所需的真实 codec 消费者。本片结束仍不能把 Delta 追加到 Storage，
也没有自动比较整张对象列表、生成保存计划或发布 Revision。

## 2. 规划起点的证据与选片理由

| 当前源码事实 | 对排期的影响 |
|---|---|
| [BinaryBody 生成器](../../../src/DurableGraph.Generator/DurableSchemaGenerator.BinaryBody.cs) 的 `BinaryVersionModel.Fields` 已按 base-first、段内 FieldId 顺序给出各版完整布局 | 可在同一布局上生成静态 comparer 与 Delta body，无需反射或新 registry |
| [CapturedObject](../../../src/DurableGraph/CapturedObject.cs) 以 `GetState<TState>()` 返回 readonly DTO 的副本；[CaptureSession](../../../src/DurableGraph/Runtime/Capture/CaptureSession.cs) 的 Current/Accept 是内存候选协议 | 可使用真实 Capture 提供 prior/current，但它不证明持久提交或 exact parent 来源 |
| [Storage](../../../src/DurableGraph.Storage/StateRevisionStore.cs) 已能按 exact Revision 重开读取 local Base body | 后续可以将已验证的 Delta codec 接到真实 prior 链；当前仍缺差异内容本身 |
| [ObjectSaveEstimate](../../../src/DurableGraph.Persistence/ObjectSaveEstimate.cs) 只接收 B/D/H，[策略](../../../src/DurableGraph.Persistence/ReadAmplificationBaseBudgetPolicy.cs) 无 codec 依赖 | 本片提供真实 B/D 的取得方法，不为了演示策略而伪造 D 或 H |
| [标量 DTO tests](../../../tests/DurableGraph.Tests/ScalarStateDtoTests.cs) 验证浮点位保留；新路径没有相等合同 | 位编码事实不能冒充比较决策，需在本片明确提案 |

候选排序经过独立评估与交叉讨论：

| 候选 | 收益与代价 | 本轮推荐 |
|---|---|---|
| SG 同版 DTO comparer + Delta codec | 直接复用已完成的 DTO/body；可独立证明差异内容与重建，尚不解决寻址 | 优先 |
| 先做 opaque Delta records + prior 链 | 能先验证链定位，但仍需另写 Apply 消费者；容易留下只会存字节、不能证明重建的接口 | 下一轮以真实 codec 为消费者再收敛 |
| 同时接 codec、链、估算、策略与 Save | 纵向收益大，但混合格式、来源、基线与发布多个尚未闭合的合同 | 拆开，避免本片同时承担全部问题 |
| roots/type 目录、TypeCodec/SchemaStore | 能减少重开读取对测试元数据的依赖，但要同时确定类型头和 Schema 的持久权威 | 保留独立候选，不作为本片前提 |
| struct、一般引用图或 Restore | 均有独立用户价值；struct 还涉及 exact inline history，Restore 涉及分配与失败可见性 | 保留路线图，下一轮可按消费者需求穿插 |

## 3. 实施合同

### 3.1 范围与入口

继续在已有 `__DurableBinaryBody` 中，为每个已经可生成完整 body 的 `V1..Vcurrent` 增加方法。
不增加新的 opt-in 开关、字段变更树、程序集或通用 codec 接口。
形状如下（每个 Vn 有自己的重载及 Apply 方法）：

```csharp
internal static PreparedDelta PrepareDelta(in V1 prior, in V1 current);
internal static V1 ApplyDeltaV1(
    ref BinaryPayloadReader reader, in V1 prior);
```

按用户的融合建议，撤回独立 `StateEquals/WriteDelta` 入口，不新增 EstimateDelta。
Prepare 在内部先逐槽比较一次形成 byte mask locals，再按 mask 静态写入变化值；比较不重复执行。
这是一份融合操作中的两个阶段，不声称只访问一次 current 字段。不为单遍输出新增回填/pool 基础设施。
mask、HasChanges 和变化值都由这同一次 Prepare 产生；结果供后续选择后直接复用，无需重新比较或编码 Delta。

在现有 Serialization 项目新增一个 public sealed `PreparedDelta`，仅为跨包生成代码提供结果容器：
`PreparedDelta(bool hasChanges, ReadOnlySpan<byte> payload)` 复制输入；暴露只读 `HasChanges` 与
`ReadOnlySpan<byte> Payload`，长度直接取 `Payload.Length`。无可写数组、ReadOnlyMemory 底层数组入口、
DTO/prior/Schema 引用、pool lease 或 Dispose；调用期间输入 span 必须稳定。
构造器只承担字节所有权，不验证 hasChanges 与 raw payload 的语义一致，也不是来源/格式验证凭证。
正确的组合由生成 Prepare 保证及验收；不为了从位图再推导 flag，让结果容器理解 Delta 格式。
调用方不得把不同 prior/current 的结果混用。Prepare 失败只丢弃内部临时结果，不返回 partial payload。

其他 Vn 同形；两个输入必须是同一个生成 DTO 类型。保留现有 `Write/ReadVn`、readonly DTO、
current-only Capture、AddRoot 和引用校验方法；不增加直接领域对象的比较/编码旁路。

本片支持范围就是现有 13 种标量与 UInt32 string 引用槽、同编译继承链和历史 Vn。
历史布局从已接受 history 重建，不能拿 current 祖先布局解释历史版本。
不支持 V1 → V2 Delta；未来跨 Schema 更新如何声明必须 Base，另行处理，不用假 Insert 或极大 D 欺骗策略。

静态 Vn 约束不能识别外部字节对应的 Schema，也不能证明传入的是正确 prior 状态。
调用方必须选对 exact Schema/body 和 prior；同 Vn 的错误 prior 仍可能成功产生错误结果。
来源校验、ObjectId 新占用者隔离和 exact prior locator 属于后续版本链/类型绑定职责。

### 3.2 持久状态相等

以当前支持槽位的完整 Base 编码是否相同为语义判据，生成代码直接逐槽比较，不实际编码后再比较。

- 整数、bool、char：按值比较。
- Half、float、double：分别比较 16/32/64 位表示。相同 NaN 位不产生伪变化；不同 NaN payload、正负零算变化。
- string 引用槽：比较 UInt32 ObjectId，0 仍是 null；不比较文本，不 dereference string 表。
- 不使用整个 CLR struct 的内存比较，避免 padding 成为持久状态；不使用默认 ValueType.Equals。
- transient 不在 DTO 布局中，自然不参与。该比较表达的是保存状态，不替领域类型定义业务 Equals。

浮点按位随本片采纳，保留已经能够无损编码的信息。数值相等会需要另行决定被忽略位变化后的保存内容，
不在本片引入该行为。

### 3.3 Delta body：固定字段位图与新值

对于 exact Vn 中的 N 个槽位，编码为：

```text
ceil(N / 8) bytes 的 changed bitmap
按槽位顺序，为每个置位槽写入 current 的完整新值
```

槽位顺序由已有 Schema 布局唯一推导：base-first，段内 FieldId 升序。
第 i 个槽对应 byte `i / 8` 的低位起第 `i % 8` 位；所有 bitmap bytes 在值之前。
最后一字节超出 N 的位必须为零。槽位值使用现有 `Write*/Read*` 原语，string 槽仍用 UInt32。
字段稀疏编号及不同声明段重复 FieldId 都不会制造位图空洞或冲突。

例如 exact 布局为 `(bool, int, uint-string-id)`，prior `(true, 10, 3)`、current `(true, 11, 4)`：
Delta bytes 为 `06 16 04`（第 1、2 槽变化；Int32 使用现有 ZigZag 原语）。
Base bytes 为 `01 16 04`。这个小对象 Delta 并不更小，不能强迫每个 Update 选择 Delta。
多字段对象只改少数槽时另给出 D < B 的独立见证，实际大小由后续策略选择。

writer 必须只置位真正变化的槽；reader 同时拒绝“置位但新值与 prior 持久相等”的冗余变更，
让固定 prior/current 只有一种合法 Delta 编码。这里复用同一生成比较表达式，不引入 runtime 分派。
这个拒绝规则不证明 prior 的真实性。

相等 DTO 可编码为全零位图；零槽 DTO 的 Delta 为空。它们是 codec 的合法 no-op，
Prepare 返回 `HasChanges=false`，上层据此分类为 NoChange；不以 payload 是否为空判断相等。

相比稀疏 `(segment, FieldId, value)` 表，固定 bitmap 不重复携带 exact Schema 已知的字段定位信息，
无需 runtime 查表与重复/乱序字段处理；代价是字段极多而变化很少时可能不够紧凑。
当前没有该类大小压力，暂不增加双模式、压缩位图或字节 diff。若实际测量出现该问题再比较格式。

这是未接持久 ObjectVersion envelope 的候选 body 合同，不在 body 中复制 Schema/type/prior 头。
不修改 Storage v2、不发布独立 body 版本号；未来接入持久格式时必须明确 codec 格式与 exact Schema 的绑定，
不能据本片默认以后改变编码仍能用旧 decoder 读取。
未来 struct 的嵌套 Delta 粒度也未被这份标量位图方案决定。

### 3.4 Apply、错误输入和引用

生成方法静态读取位图，并从 prior 或 reader 获取各槽值；所有读取及检查通过后构造完整 readonly Vn。
失败不修改 prior，不返回半成品。reader 允许已前进；Prepare 的 writer 是内部临时缓冲，失败结果不交付。
全 body 边界由调用方 `EnsureFullyConsumed()` 检查，和 `ReadVn` 一致。

必须拒绝：截断位图/值、超出布局的置位、冗余置位、底层非法 bool/非 canonical varint/溢长值；
顶层组合读取还要拒绝尾随数据。不要新增未实际读写的校验标签。

Apply 只恢复 ID DTO。引用必须随后针对目标 Revision 的 string 表执行已有 `ValidateStringReferences`；
继承自 prior 的未改槽也要校验，不能只验证 Delta 中出现的引用。
null/Empty/非空引用身份沿用当前合同，不重新研究空串分配。

string 对象自身不可变：同一连续存活实例不会产生内容 Update；换成不同非空实例会改变 owner 的 ID 槽，
同时进入候选对象列表。这里只验证这种 Capture + DTO 效果，不实现对象列表差集/删除或 string 差异压缩器。

### 3.5 B/D 的取得与 H 的边界

本片不新增 `EstimateBase/EstimateDelta` 生成接口或 counting writer。
Prepare 内部用 `ArrayBufferWriter<byte>` 和现有 BinaryPayloadWriter 编码，再复制为结果自有 bytes。
D 直接取结果 Payload.Length，策略选 Delta 后直接消费该 payload。B 仍由现有完整 Write 的实写长度取得。
接受临时缓冲和一次结果复制；本片不引入池化、no-op 缓存或分配性能保证。
未来选择 Base 时已准备的 Delta 会被丢弃；所有待选 Delta 的驻留峰值不能靠 pool 消除。

B 是 current 完整 body 长度，D 是 bitmap 加 changed 值的长度。未来外层加入对象独有 header 时，
应按 DB-015 的对象 payload 口径一致补入 B/D/H；不能把这里的裸 body 长度冒充未来完整对象 payload 成本。
共享 Frame、membership、对齐等仍不属于该策略口径。

本片不生产 H、不调用 policy、不扩展它的 internal 可见性，也不把内存 Accept 当成落盘提交。
H 的真实 prior 链统计和不可 Delta 的 Update 分类，待后续消费者设计。

## 4. 最小验收与实施安排

| 验收组 | 必须观察到的结果 |
|---|---|
| 相等与重建律 | 各支持槽位，equal/single/all change；Apply 的 Base bytes 精确等于 current；!HasChanges 与 Base bytes 相等一致；重复 Prepare 稳定且不改 DTO |
| 融合与所有权 | 不生成 StateEquals/EstimateDelta/WriteDelta；同次比较的 mask 驱动编码；自有 payload 可反复直接 Apply，修改输入/外部复制品或后续 Prepare 不改变旧结果 |
| 浮点与身份 | 相同/不同 NaN 位、正负零；相同 string ID 无差异，等文本不同非空实例产生 ID 槽差异；null 与 Empty 分开 |
| 独立格式见证 | 固定 golden bytes；N=0、1、8、9 的位图边界；稀疏 FieldId 与继承重号；mask 在前、值顺序和 padding 有独立预期 |
| 历史 | current 与旧 Vn 各自同版往返，祖先升版后旧布局仍正确；跨 Vn 不存在可调用的混合 Delta 重载 |
| 错误输入 | 每个截断前缀、非法 padding、冗余置位、底层非 canonical 值、顶层尾随字节；late failure 后 prior 仍可正常使用 |
| Capture 到字节的组合 | 真 SG 捕获两个冻结候选，Seal 后领域变动不影响差异；Apply 后完整引用校验，含继承未改槽的缺失/wrong-kind 目标拒绝 |
| 大小 | 实写长度取得 B/D；分别证明少量改动 D < B 和小对象/全改动 D >= B，不承诺 Delta 必然更小 |
| 包交付 | 单 PackageReference 的既有 consumer 增加 PrepareDelta/Apply 调用，确保结果容器及生成代码只依赖可访问 API |

本轮按依赖组织实施：

1. 主代理固定融合接缝，实现并验证 Serialization 的 owned 结果容器。
2. 生成器子任务：集中扩展 BinaryBody 文件，Prepare 与 Apply 的比较表达式由一个生成 helper 复用；保持静态调用。
3. 独立验收子任务：在新测试文件内写矩阵/历史/恶意字节见证，复用现有 RunGenerator/EmitAndLoad。
4. 在方法形状稳定后，包消费者子任务补跨包见证；主代理整合与独立审查 actual diff。
5. 根 solution build、相关 tests 和 PackageConsumerProbe，通过后更新唯一进度入口并提交。

产品改动限于 Generator 和 Serialization 的结果容器，tests 与包消费者可分工；不扩大 DurableGraph runtime/Storage API。
测试条件应包含实际 generated-code 执行，不能只用生成文本断言证明编码正确。
实施前基线和最终验证记录于下一节，不从历史测试数字推导当前通过。

## 5. 完成后的衔接

下一候选是用此真实 codec 收敛 ObjectVersion Delta record、exact prior、链读取与累计 H，
验证 Base 截断重建依赖、移除/新占用者隔离、wrong prior 与文件重开；届时再选择最小 typed adapter。
然后才把 frozen 对象列表比较、B/D/H、固定 policy 和稀疏记录执行连起来。
这是依赖建议，不强制推迟具有独立消费者的 struct、类型目录或 Restore 工作。

本片明确不含：ObjectVersion Delta 落盘、完整 Save/发布/恢复、自动基线安装、SchemaStore/TypeCodec、
DTO 升级、领域 Restore、一般引用递归、struct/数组/BCL 扩展、ID 回收、池化/回填优化或新增策略。
所有仍未完成的能力继续由[路线图](../../DurableGraph-research-roadmap.md)维护。

## 6. 本轮实施账本

起点 `26760a2`，干净工作区。使用 spec-driven-implementation，主代理保留接缝和整合责任。
实施前 `dotnet build DurableGraph.slnx --verbosity quiet` 成功，0 警告/错误；
`dotnet test DurableGraph.slnx --no-build --verbosity quiet` 通过 559/559，无跳过。

| 要求 | 负责人/路径 | 验证入口 | 状态 |
|---|---|---|---|
| owned PreparedDelta、跨包可用 | 主代理 / [PreparedDelta](../../../src/DurableGraph.StateStore.Serialization/Serialization/PreparedDelta.cs) | [所有权单测](../../../tests/DurableGraph.StateStore.Serialization.Tests/PreparedDeltaTests.cs) + 包消费者 | 已验证 |
| 各 Vn 融合 Prepare 与 Apply、同一比较表达式 | 生成器子任务 / [BinaryBody.cs](../../../src/DurableGraph.Generator/DurableSchemaGenerator.BinaryBody.cs) | 实际生成代码执行 | 已验证 |
| 标量/位图/错误/历史/冻结与引用完整性 | 独立测试子任务 / [body tests](../../../tests/DurableGraph.Tests/FusedDeltaBodyTests.cs)、[history/Capture tests](../../../tests/DurableGraph.Tests/FusedDeltaHistoryTests.cs) | 重建律、golden、负面输入 | 已验证 |
| 真实 PackageReference 交付 | 包消费者子任务 / [Domain.Delta](../../../experiments/PackageConsumerProbe/Consumer/Domain.Delta.cs) | Run-Probe.ps1 | 已验证 |
| 实际 diff 审查、根 build/tests、文档 | 主代理 + 只读审查者 | 下列集中验证；既有 API 白名单同步新增批准的方法 | 已验证 |

2026-09-06 最终验证：

- `dotnet build DurableGraph.slnx --verbosity quiet`：0 警告、0 错误。
- `dotnet test DurableGraph.slnx --no-build --verbosity quiet`：570/570，无跳过；
  DurableGraph 339、Serialization 96、Storage 95、StateStore 40。
- 新 FusedDelta 测试此前单独通过 9/9；结果容器测试通过 2/2，均再次包含在最终全套验证中。
- `./experiments/PackageConsumerProbe/Run-Probe.ps1`：通过。实际单 PackageReference 输出严格满足
  `BinaryBody:012154:True:ReferenceCapture:True:StringDecoding:True:PreparedDelta:True`。
  本地 ignored 产物为 `experiments/PackageConsumerProbe/obj/run-20260906091335-33528`；重跑以新输出为准。
- 独立审查核对实际产品 diff、完整新测试、包接缝及两处既有方法白名单更新，无未解决阻塞项。

验证中修正了包测试对负数 ZigZag 的预期、异常继承关系断言，以及既有测试的生成方法白名单；
没有为迎合这些预期修改产品编码或放宽 immutable DTO 边界。
包验证之后产品代码未再改动，最终两处修改仅为上述旧测试更新。

本片未改 Storage wire/API、policy 或 CaptureSession。Prepare 保留临时编码缓冲及结果复制，
没有性能 benchmark、pool 或峰值内存保证；持久 Delta/prior/H 与 Save 仍按 §5 延后。
