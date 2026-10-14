# DB-036 工作会话与历史恢复能力重构草案

> 文档整理：2026-10-14；来源为 Git `8ef9fca` 中的 `docs/design-branches/0036-working-session-and-history-capabilities.md`，正文保留当片合同与历史验收含义。
> 阅读范围：同实例会话、单 head 发布和迁移壳包见证保当片资格，结果见 §7；旧 lease/严格日志及坏尾拒绝不能覆盖 DB-086 正常恢复与 DB-087 当前发布合同。
> 当前能力与施工焦点见 [PROJECT-STATE](../../../src/PROJECT-STATE.md)；旧稿中的“下一步”不自动成为当前工作单。

> 状态：Chosen / Implemented，2026-09-07；源码调研基线 `6bcadbb`。
> §1–6 保留原调研与施工合同，其“当前/待验证”指实施前；最终选择、实现与证据见 §7。
> 当前事实见 [PROJECT-STATE](../../../src/PROJECT-STATE.md)，术语见[术语表](../../DurableGraph-glossary.md)。

## 1. 结论与需求依据

可以形成重构方案，但三项问题不应再作为三个并列的大改造：

| 原问题 | 当前判断 | 推荐处理 |
|---|---|---|
| 连续保存保持同一套领域实例 | 仍未闭合，是主要产品缺口 | 最小 Repository 单发布 head + 单活动工作会话，内部复用现有 Capture/规划/恢复 |
| Schema 历史与未来程序恢复能力 | Schema 元数据、读取代码、升级/恢复代码的保留责任尚需明确 | 先明确支持合同并做迁移壳真实包见证；不立即建设退休类型框架 |
| 阶段区别依赖调用约定 | DB-035 已修复原先的 raw/encoded Base 混用及 carrier 命名 | 保留现有边界；保存来源约束在会话主线解决，不另造阶段类型体系 |

需求来自用户已采纳的方向及[目标设计](../../DurableGraph-target-design-v0.md#单一发布权威与明确故障结果)：
一个非空 World、单 writer、捕获期间领域图静止；版本化 DTO 冻结后再比较和写入；成功发布后才能安装
同一候选为基线；保留对象身份、完整 source 目录、单对象 Upgrade、升级存活对象强制 Base。
新 API 名称、publication carrier、故障域尚未采纳；不将本文的建议反写成已选目标。

初次调研仅阅读源码、测试与文档；后续用户批准的实施验证记录在 §7。

## 2. 当前代码提供了什么

- [LoadedWorld](../../../src/DurableGraph.Persistence/LoadedWorld.cs)：`_baseline` 固定，`Prepare` 在 finally
  Discard 候选；`PrepareNew` 也丢弃捕获会话。计划可独立 Append，但没有下一基线及新对象绑定安装。
  因此重新 Load 会分配另一套实例，继续使用原 LoadedWorld 则仍从原 Parent 分支准备。
- [CaptureSession](../../../src/DurableGraph/Runtime/Capture/CaptureSession.cs)：已有 candidate 身份检查、Accept/Discard、
  live bindings 转移和单调 cursor；这是可复用的内核，Accept 本身不是持久 Commit。
- [NormalizedRevision](../../../src/DurableGraph.Persistence/NormalizedRevision.cs) 与
  [LoadedRevisionPlanner](../../../src/DurableGraph.Persistence/LoadedRevisionPlanner.cs)：保存完整 source membership、
  current DTO、SourceSchema/RequiresRewrite，已经拥有加载后首次升级续写所需信息。
- [StateRevisionStore.Append](../../../src/DurableGraph.Storage/StateRevisionStore.cs) 只在 writer lease
  内 EndAppend 并返回地址，没有 State durability barrier 或发布。底层
  [OpenActiveWriter](../../../../atelia/src/RbfSegmentStore/RbfSegmentStore.cs) 可以在取得 lease 时轮转；
  Append 后重新 OpenActiveWriter 再 flush，不能证明 flush 的是刚追加帧所属文件。
- [BaseObjectBodyCodec](../../../src/DurableGraph.Persistence/BaseObjectBodyCodec.cs) 已是 internal，签名为
  `PreparedBaseBody → EncodedBaseObjectBody`；内部 PreparedObject/规划器只接受对应表示。
  [ObjectStateRecord](../../../src/DurableGraph/Runtime/Capture/ObjectStateRecord.cs) 是中性单行，来源仍由外层视图约束。
  不需要再增加三个不同阶段的单行 DTO 或泛型 phase 框架。

## 3. 主线：最小受控工作会话

### 3.1 对外形状与范围

推荐暂称 `GraphSession<TWorld>`，由一个 Repository 创建或加载。名称为示意：

```csharp
var session = repository.Create(world, models); // 保留用户传入的 World
session.World.Character.ChangeEquipment();
var revision = session.Commit(parameters);
session.World.Character.Move();
var nextRevision = session.Commit(parameters); // 相对于刚发布的 revision
// 从仓库重新打开时，repository.Load<TWorld>(models) 从持久 head 取得 WorldId。
```

Repository 拥有匹配的 State/Schema 资源、发布 head 和活动会话；普通调用者不分别设置 Parent、DTO
或实例绑定。首片只实现单 head、单活动会话、固定非空 World；第二次创建活动会话明确拒绝。
Create 仅允许尚无发布 head 的仓库；已有 head 必须 Load，关闭旧会话不赋予覆盖旧 head 的权限。
不加入命名 branch、Reset、根替换/清空、多 writer 或联合 Artifact/Schema 版本视图。
SchemaStore 保持单调持久注册；新发布记录只需选择 State Revision 和 WorldId，及校验前驱所需信息。
这不是把单调 Schema 可见性冻结为长期合同。

低层显式地址 Load/Prepare/Append 可保留作测试和开发工具入口，其语义保持原样。
受控 Repository 不暴露可并行修改其 head/Store 的普通产品入口；独占资源假设需写入 API 合同。
不同时维护两套捕获、恢复和策略算法，新会话与 LoadedWorld 共用内部实现。

### 3.2 唯一比较基线与候选

内部建议从 `NormalizedRevision` 提取适用于加载和提交安装的比较基线职责；是否保留类名随实现决定。
只维护一份 current DTO 目录及其 source provenance，不同时让另一份 `CaptureSession.Current`
成为可独立推进的第二权威。后者如保留，只能由同一安装流程同步维护或作为共享行的投影。

工作会话拥有：固定 World、模型目录快照、可空的 Parent、比较基线、实例-ID 关联、原单调 cursor。
一次私有 pending save 拥有：exact candidate、对应 bindings、已准备 bytes/Revision、待安装基线及追加地址。
不要向普通调用方暴露可任意组合的 `Accept(address)`、host receipt 或可重复消费的提交凭据。

成功路径为：

1. 检查会话/资源可用、head 仍为预期 Parent，拒绝重入。
2. Capture/Seal；从此 bytes 和待安装 DTO 都取自这个候选，不再读取领域字段。
3. 准备内容和 Revision；Schema 注册按现有合同先完成。预构造下一份完整目录和 bindings。
4. 追加 State，并在**同一个 writer lease** 内执行目标文件的持久化屏障；具体内部方法由 G0 选择。
5. 地址已知后，在发布前完成剩余基线包装与分配；发布 `(RevisionAddress, WorldId)`。
6. 仅在发布已确认后安装候选、Parent 和 live bindings。正常安装不再 Capture、Hydrate、调用用户代码或分配集合。

[CaptureContext.DetachBindings](../../../src/DurableGraph/Runtime/Capture/CaptureContext.cs) 当前会 new 一个空 Dictionary，
因此不能直接把现有 Accept 放到发布后就宣称安装无分配；应移动准备动作或简化转移状态。
这不承诺所有运行时异常都可避免；发布已成功而会话安装失败时，持久结果仍成功，会话失效并从权威 head 恢复。

下一基线必须满足：

- current DTO 是原 candidate 中的行；领域随后修改应在下一次 Commit 被比较出来。
- 完整 source membership 变为成功发布候选的完整 ID 集合；发布前仍保留旧 source 全集，不能提前丢掉 Remove 来源。
- durable 的 SourceSchema 变为该 candidate 的 current Schema，RequiresRewrite 清除；仍按原策略处理后续可选 Base。
- 未变存活对象不要求写新记录；引用槽仍按 ID，child-only 修改不制造 owner 字段变化。
- 保留同一个 World 及存活子对象实例；移除对象的绑定退出，原 CLR 实例以后重新接入视为新占用者，从新 ID/Base 开始。
- 保留原 ulong cursor，包括失败烧掉的数字；不能从当前候选 max ID 重新计算而回退。
- 新会话首存也走此路径。加载时的 orphan/Empty 多 ID 仍按现有规则处理，成功保存才转入候选 membership。

### 3.3 失败与发布：尚需 G0 的实证裁决

| 失败位置 | 允许的结论与动作 |
|---|---|
| Capture/内容准备失败，未触发不确定 I/O | 原基线不变，释放临时候选，cursor 不回退；可修正领域状态后重试 |
| Schema/State 写入或屏障失败，尚未调用发布 | 本次没有发布；原 head 不变，但不能据此认定 Store 仍可写。按资源 fault 状态重开，不自动重复追加 |
| State 已确认写入，发布前基线包装等内存操作失败 | head 和原基线不变，候选不安装；资源仍健康时可放弃孤立追加并重试，不因此将 Store 判为 I/O 不确定 |
| 发布操作开始后异常 | 结果可能不确定；禁止当作确定失败丢弃身份并透明重试。会话停止新 Commit，按权威发布记录重新裁决 |
| 发布成功、内存安装失败 | 不报告为未提交；使会话失效，按已发布 head 重建。正常成功路径才承诺原实例连续使用 |

首片建议以**明确的单进程 writer、正常关闭与进程中止模型**建立实证，不预先声称任意断电/磁盘损坏保证。
G0 必须具体裁决：publication 的持久表示、CRC/截断/坏尾行为、写入成功但 flush 抛错后的重开规则、
首次空仓创建、State rollover、新文件及目录元数据边界。OS crash/power loss 的支持不能由方法名 DurableFlush 推导。

publication carrier 候选为单独的短 RBF 发布日志，或小 head 文件原子替换。前者复用现有帧与地址工具，
但必须处理尾部不确定；后者记录少，却需验证替换与目录持久性。优先验证 RBF 候选，**当前不冻结 wire**。
不能自动采用“最后一个能读的记录”并忽略损坏：它可能掩盖已确认的提交。
如果无法从现有证据安全恢复，允许明确拒绝打开；不能把拒绝打开伪装成已判定旧 head。
需要可用性更强的坏尾自动恢复时，应作为明确追加的产品选择，而不是在 G0 中偷偷建设通用修复框架。

## 4. 并行小线：明确历史恢复能力合同

历史能力至少分两层，不能仅看目录里是否有 `.dgschema`：

| 承诺 | 必须提供的能力 |
|---|---|
| exact 解码历史 Revision 的 DTO 全目录 | 对每个 source-live durable 族/版保留 exact Schema、DTO reader、同版 Delta applier、引用遍历 |
| 将历史 Revision 恢复为当前可编辑 World | 上述能力 + 全部 source 行到当前 DTO 的 Normalize/Upgrade；全部 current 引用合法；可达对象可 Allocate/Hydrate |

[StateReaderBinding](../../../src/DurableGraph/Runtime/Binding/StateReaderBinding.cs) 不依赖领域 CLR Type，
但[生成入口](../../../src/DurableGraph.Generator/DurableSchemaGenerator.GeneratedState.cs)依附当前被标记类。
删除整个模型族的类，仅留 `.dgschema` 不会自动生成该族 reader；
[StateModelRegistry](../../../src/DurableGraph.Persistence/StateModelRegistry.cs) 与 NormalizedRevision
也仍要求每个 source-live durable 族有 current model。
旧版 DTO 可以由历史形状再生成，不等于被删除的整个模型族会自动恢复可执行能力。

推荐先证明现有 ABI 的**迁移壳**用法，而不拆 StateModelBinding：

1. V1 World 引用 Legacy；V2 World 的 Upgrade 清除这条边。
2. Legacy 在 V2 程序保留仅用于迁移的 attributed partial class、完整 history 和需要的 Upgrade。
   它可以没有业务方法；如果变为无字段/改变 exact base，仍须正常升版和显式转换，不能同版偷改 Schema。
3. 可以选 abstract 的末版壳：其 source 行仍完整解码、归一化、验证，变得不可达后不 Allocate；
   意外仍可达则明确加载失败。壳自己的引用也不能绕过 current 验证，必要时升级清槽或调整合法约束。
4. 新保存的 Revision Remove 这些对象后，读这个新 Revision 可以不再登记它们；
   但若仍宣称支持更老的 Revision，就仍要保留那些 Revision 所需的能力。

迁移壳不是要求永久保留所有旧业务实现：历史 exact 祖先布局可以从 history 再生。
它也不是自动删除的退休标记；若旧 Revision 中有某祖先族的独立对象行，该族自己的能力仍须提供。
支持哪些历史 Revision/应用版本由应用发布策略明确，库不悄悄缩短窗口、不猜测业务 Upgrade。

需用两/三版实际 PackageReference 消费工程验证：旧图落盘 → 新 World 删边 + 壳归一化 → 不分配壳 →
保存 Remove → 再删壳程序能读新 Revision、读旧 Revision 明确缺能力；另验证坏 orphan 仍失败。
已有 [abstract orphan 测试](../../../tests/DurableGraph.Persistence.Tests/LoadedReferenceWorldTests.cs)
和[旧 nominal 槽测试](../../../tests/DurableGraph.Tests/GeneratedReferenceBodyTests.cs)分别提供局部证据，不能代替该完整见证。

后续只有在真实应用需要**完全删除 CLR 壳却继续加载旧图**时，再评估独立的 family 状态能力、
显式退休声明、history-only 生成与注册；这需要解决 Normalize/current 引用合同，单独自动生成 reader 不够。

## 5. 被比较但未推荐的方案

| 方案 | 优点 | 不作为当前推荐的原因 |
|---|---|---|
| Append + Flush 后直接推进显式地址游标 | 很小，无新 head 格式，立即保持领域实例 | 可以作为 detached 修订功能，但不满足已选的发布后安装；需明确改变产品范围，不能冒称 Commit |
| 宿主回调发布，再返回成功/receipt | 宿主可接已有发布系统 | 当前未找到现成宿主协议；把关键一致性约束转成回调约定，并新增公开错误面 |
| 一次实现完整联合 CommitManifest/Ref | 一步覆盖长期 Store 视图 | Artifact/版本化 Schema 尚无消费者，远超连续保存所需 |
| 当前就拆 reader/Normalize/领域 model 三套目录 | 可承载无领域类的退休族 | 存在未来价值，但现有迁移壳可验证需求，暂不引入新的 current/retired 行语义 |
| 再增加阶段品牌/认证层 | 表面上更严格 | 原包装洞已修；真正待闭合的是一次受控保存的生命周期，不是单行再换类型 |

三位独立 reviewer 分别从最小架构、语义保全和需求裁剪出发，并交叉质询以上方案。
共同结论是：不重做 DB-035，保留完整 source 验证，主线闭合发布后安装；publication 尚不能仅凭阅读视为已验证。

## 6. 推荐施工顺序与可观察闸门

| 步骤 | 产物 | 最小验收 / 停点 |
|---|---|---|
| G0 发布与屏障见证 | 明确故障域、选定 carrier/格式、原 lease 屏障接缝 | 正常重开、轮转、进程中止/故障注入；可区分确认成功、确认未发布、不确定/无法打开。未闭合则停止该主线，回到设计裁决 |
| G1 私有候选与基线推进 | 共用保存准备、预构造安装、单调身份连续性 | 同一 World/child 连续三次状态变化；candidate 冻结；Remove/重接入；升级重写义务只在成功后清除；不新增公开 Accept |
| G2 最小 Repository/GraphSession | 单 head Create/Load/Commit；绑定 WorldId、屏障和发布 | 无外传地址/DTO即可 reopen；第二次 Commit Parent 正确；外部持有 child 引用不失效；重入/故障后禁止不合法推进 |
| H1 历史能力规则与迁移壳见证，可与 G0/G1 独立 | 发布支持说明、真实生成/package 回归；必要时仅修现有接缝 | 覆盖 §4 完整跨版路径及坏 orphan；若现有 ABI 不足，先返回证据，不自动引入退休框架 |
| G3 集成与文档收口 | 用新入口演示长期连续保存；更新术语与能力状态 | 根 solution build、相关及集成 tests、真实包消费；新格式独立 golden/非法输入；成功/失败轨迹与声明故障范围一致 |

G1 的内存设计和 H1 已足够明确；G2 的大方向明确，完整施工合同以 G0 证据为前置。
因此可以批准一个**带 G0 停点的多步骤工作批次**，不能把本文当作 publication 格式已经选定的盲执行工作单。
下一轮若采纳此路线，先补齐 G0 的具体实验输入和故障范围，再启动实施；不扩展类型、策略、wire v3
对象格式、GC、性能缓存或新程序集。长期已选约束不变。

## 7. 本轮施工与验证记录

实施基线 `9cbb5c1`，工作区干净；根 build 0 warning/error，现有四项目测试 840/840 通过。
授权边界是本篇 G0–G3/H1；不实现跨对象 Upgrade、无 CLR 壳退休框架、其他类型、联合 Store 视图或自动修复。
依赖保持 StateStore → Runtime/Storage；不修改 Atelia 上游，主线程串行运行 build/test/package。

| 要求 | 负责人/落点 | 状态与证据 |
|---|---|---|
| G0 严格发布日志、原 lease 屏障、process 中止 | PublicationSubstrateTests、PublicationCrashProbe、StateRevisionStore.AppendDurably | verified：13 个 substrate cases + 5 个进程 Kill 场景；选择专用 RBF 严格日志 |
| G1 exact 候选/基线/绑定安装 | WorldWorkspace、PreparedWorldSave、CaptureContext | verified：7 个 WorldWorkspace cases；原 fixed-Parent/加载 tests 回归通过 |
| G2 单 head Repository/GraphSession | GraphRepository、GraphSession、PublicationLog | verified：21 个 Repository cases、35 个 publication cases；无任意 Accept，State wire v3 不变 |
| H1 迁移壳完整消费 | HistoryCapabilityConsumer、Run-HistoryCapabilityProbe.ps1 | verified：V1、V2 readers-only、V2 migration、V3 删除壳四次真实包编译运行 |
| G3 集成与独立审查 | 根 solution、真实包 consumer、独立只读 review | 根 build 0 warning/error；916/916 tests；StateStore 包消费含 GraphSessionContinuousCommit；最终包/文档检查见下 |

### 冻结的最小产品合同

- `GraphRepository.CreateNew(path)` 新建且拒绝已有路径；`OpenExisting(path)` 只打开完整仓库。
  布局为 publication.rbf、schemas.rbf、state/；先独占 publication 文件，再取得其他资源。
  创建中途失败不删除半成品，重开缺文件明确拒绝；Create(World, models) 只允许无发布 head。
- 一个活动 `GraphSession<TWorld>`，Create 保留用户实例；Load 从日志取得 WorldId/Revision，无地址 sidecar。
  Dispose 会话不保存；故障或 Dispose 不回滚领域修改。WorldId/Parent 在首次成功 Commit 前为空。
- State 屏障由公开低层 `AppendDurably` 在原 lease 内完成；原 `Append` 语义不变。
  重开强制 RecoverActiveTailOnOpen=false，Schema 确认后严格检查全部物理 State 文件 CRC/终止并 flush，
  再重放 publication、验证每条记录的 State Parent/World membership、完整对象链及 Schema 引用，最后确认 publication。
  领域 DTO 和引用合法性仍由 Load 的完整 exact/Normalize 验证承担，不声称能认证任意错误手写 codec。
- publication 独占 RBF 文件，tag 数值 `0x44475048`，body v1：byte version、canonical Boolean hasParent、
  optional parent address、new address、canonical UInt32 WorldId；address 是 canonical UInt32 FileNumber +
  canonical UInt64 `SizedPtr.Serialize()`。全消费；帧 CRC/类型/尾元数据/断链/WorldId 改变均拒绝。
  独立 golden：首条 `010001860207`；下一条 `010101860201A20207`。
- 日志每条前驱必须等于上条 Revision，新地址严格更晚，WorldId 固定。发布 Append/flush 结果不确定后
  repository faulted；GraphCommitException 提供 Unknown 和已知 candidate 地址，禁止旧会话续写。
  重开按可见完整日志及新屏障确认旧/新 head；坏尾明确拒绝，不自动回退。
  发布已确认而安装失败报告 Published；确定未发布但 State 写入路径异常报告 NotPublished 并要求重开。
  AfterStateDurable 后的普通内存失败可保留健康资源重试，孤立追加不成为 head。
- 只验证正常 OS 下的进程中止/确定性故障注入；不承诺 OS crash、power loss、目录元数据、介质丢失。
  单日志无法区分合法旧文件和被外部精确删除了完整帧后缀的文件；该外部破坏不在本片模型中。
  不确定结果的重开可能拒绝打开，此处没有自动修复或不中断保留原实例的恢复承诺。

### 集成证据与实际限制

- 根 build 0 warnings/errors；根四项目 **916/916**，0 skip，比基线增加 76。
  Runtime/SG 391、StateStore 267、Storage 155、Serialization 103；TRX 前缀 `db036-final`。
  初轮发现并修正了测试清理前缀、测试访问上游私有路径、InvalidDataException 继承关系的错误断言；
  最终测试没有通过跳过、放宽格式或修改上游来规避失败。
- PublicationCrashProbe 五场景由父进程 Kill 子进程，不执行子进程 Dispose；独立验证进程检查结果。
  产物 `experiments/PublicationCrashProbe/obj/run-20260907132120-c6c6cb6c420a4906bdae0c92cf4d4976`。
  substrate/probe 使用小 ordinal 格式，产品日志格式另由 PublicationLogTests 的 golden/非法输入覆盖。
  Repository 的 BeforeStateAppend 故障是内部检查点注入，不冒称真实设备 flush 失败；
  publication 的 Append/flush 前后失败还由 IRbfFile wrapper 覆盖。
- H1 产物 `experiments/PackageConsumerProbe/obj/history-capability-20260907132553-28960-19f04a71`，
  history 数量 2→4→4→4，旧文件 hash 保持。V2 壳为 abstract 且保留一个可注入非法 ID 的引用槽，
  用来证明坏 orphan 仍拒绝；未要求所有迁移壳都是零字段，也未新增退休类型 ABI。
- StateStore 实际包消费保留旧 markers，并新增 `GraphSessionContinuousCommit:True`：SG 领域图连续三次
  Commit 保留实例/readonly 环/transient，目录重开后再 Commit 并第二次重开验证。
  产物 `experiments/PackageConsumerProbe/obj/state-store-run-20260907133347-38280-45b4d5a8`。
- 原 Runtime package consumer 回归通过，7 份 Schema history；产物
  `experiments/PackageConsumerProbe/obj/run-20260907133616-27792`。
  三条包回归与进程见证均由主线程串行执行，未改动上游源码。
- 文档/编码检查：34 个变更文件 UTF-8 无 BOM/LF，9 份 Markdown 的 278 个本地链接/锚点通过，
  `git diff --check` 通过；构建产物及测试结果留在 ignored 目录。
- 独立只读评审覆盖候选/完整 source/身份安装与 publication/重开/异常结果，无未解决阻塞。
  根据评审调整重开屏障次序、增加 CRC 正确但 State Parent 错误/World 缺失的真实跨 Store 拒绝测试。
- 不实现的部分继续是原先的明确非目标：命名 branch/Reset、根替换/清空、多 writer、联合 Store 视图、
  自动坏尾修复、无 CLR 迁移壳历史族、类型扩展及性能优化。它们不是本轮未完成的实施闸门。
