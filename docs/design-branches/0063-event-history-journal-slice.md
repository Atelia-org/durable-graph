# DB-063：EventJournal 驱动的 EventHistory 外观

> 状态：已实施，2026-09-11；依赖 [DB-062](0062-independent-graph-workspace-slice.md)。完成证据见 §8。
> 消费者合同：[DramaBoard 草稿](../../../drama-board/archive/firstboard-llm/docs/research/event-journal-state-store-draft.md)。
> 实验性合并读取 API 随本片首版交付；[DB-064](0064-shared-revision-decoding-design.md) 的实际去重/共享算法低优先级后置。

## 1. 目标与范围

把“保存强类型事件/状态图、浏览事件、从 branch 恢复续写”收敛到一个易用外观。
StateStore 继续负责图、Schema/Upgrade 和 Base/Delta；EventJournal 负责逻辑链、命名 branch 和唯一发布 ref。
正常恢复读取已经保存的处理结果，不回放历史业务 reducer，也不调用 Player/LLM。

本片包含单 writer、单活动分支会话、E/S 交替、独立只读、冷 Resume、State 根替换、fork/显式移动 ref、
明确发布失败结果。并发 merge、多 checkout、多 writer、自动 retry、断电保证、自动坏尾修复、GC、
完整 DramaBoard 的 Kernel/Player 状态建模与迁移不在本片。

先在 StateStore 程序集中新增这组外观并直接引用 EventJournal，复用 DB-062 私有核心；不让下游管理两套存储资源。
这样暂时使该包携带 EventJournal 依赖，但避免为分程序集新增公开内部阶段 API 或友元边界。
未来程序集审视可以移动外观，不影响下面的持久和会话语义；不在本片批量整理 namespace。

用户已明确旧 API 没有下游兼容负担。本片重新收敛公开入口，不保留 GraphRepository/GraphSession 的旧调用形状、
旧 publication.rbf 的读写/迁移路径或第二种发布器。保留已有可验证的持久语义，测试和包示例改接新外观。
本轮收敛范围是仓库/会话/读写外观；模型声明、SG 生成 DTO、Schema history 和 Upgrade 能力继续支撑长期演化，
不因封闭外观顺带隐藏跨程序集模型组合实际需要的公开能力。

## 2. 可直接复用的 EventJournal 能力

| 当前源码 | 可以复用什么 | 不应误称什么 |
|---|---|---|
| [AppendEventFrame](../../../atelia/src/EventJournal/EventJournal.cs) | opaque kind + arbitrary payload、Parent 校验、追加后 DurableFlush | 底层只有一种 EJF1 EventFrame；尚无一等 StateFrame |
| [Refs API](../../../atelia/src/EventJournal/EventJournal.Refs.cs) | Create/Fork branch、CAS AdvanceRef/MoveRef、GetHead、reflog | 不是跨 Store 事务；Append 后 CAS 失败可留下 orphan |
| [EventJournal options](../../../atelia/src/EventJournal/EventJournalOptions.cs) | event/ref segment 与 ref-op 的恢复选项 | 默认可写打开会恢复尾部，不能原样套入 DG 严格重开合同 |
| [只读打开](../../../atelia/src/EventJournal/EventJournal.cs) | OpenReadOnlyExisting，不修复尾部、不写 forward cache | 不证明仅操作某一个 Event 就是常数时间打开 |
| [正向遍历](../../../atelia/src/EventJournal/EventJournal.ForwardPlan.cs) | 按逻辑 Parent 链枚举正序 | 不能按物理追加顺序配对 E/S；orphan 不算分支进展 |

初版不修改 EventJournal 的底层 tag/header，不为领域事件另写 JSON/body codec。
新 wrapper 的少量二进制数据只是图地址信封，领域内容始终走已有 SG/SchemaStore。

## 3. 帧与两种 Parent

```text
Journal Parent： S0 → E1 → S1 → E2 → S2
Revision Parent：E1 → S0，S1 → S0；E2 → S1，S2 → S1
```

opaque kind 固定 Event=1、State=2；共同 payload：ASCII `DGH1`、格式 byte=1、
canonical UInt32 FileNumber、UInt64 SizedPtr 序列化值、UInt32 非零 RootId；golden test 冻结。
kind 只由头部表达一次；未知 kind/version、非规范整数、错误 ticket 或尾随数据拒绝。
不重复保存领域类型、Schema/DTO、业务 EventKind、logical time 或 branch head；业务元数据可存在事件根中。

EventJournal 与 StateStore 都有叫 FrameAddress 的概念，但分段号和 Ticket 的类型/语义不能强转。
编写显式 envelope codec 并用类型别名消歧；引用的两个 Store 固定属于同一打开仓库。
对外 frame handle 由仓库签发并携带运行期来源身份，避免把另一个仓库的裸整数直接传入 Read/Move。
本片不增加跨库传输格式或全局地址 UUID。

每个 frame 的根及其完整 live membership 应有效，根为已支持 durable 引用对象。
写入时通过 registered actual model 校验；读取请求的基类/实际子类按 DB-062 绑定。
图内 nominal 引用仍在自身 Revision 中解析，不读取 Journal 前帧来补缺失对象。

协议校验：

- 空 branch 只能发布初始 S0，Journal Parent 与 Revision Parent 均为空。
- E 的 Journal Parent 必须是 S；其 Revision Parent 必须等于该 S 信封指向的 Revision。
- 后继 S 的 Journal Parent 必须是 E；其 Revision Parent 必须等于 E 的直接前 S 所引用 Revision。
- 不以“仓库最新 State”替代同一逻辑链上的精确前 S；S/E/E、S/S、错误 root/Parent 拒绝。
- fork/MoveRef 可以选择有效 E 或 S，恢复按所选 head 执行。DramaBoard 可另限制可玩 fork 只选完整 S。

仓库严格打开时可验证文件、信封、根存在与原始重建/Schema 地址，不执行所有帧的 typed Decode/Upgrade。
读取某个 E 才绑定其可达成员 reader；因此只浏览 Event 不要求提供所有历史 World 的 current Allocate 能力。
共享 Schema 日志仍单调积累，不在本片伪装成按分支回滚的 SchemaStore 视图。

## 4. 候选外观与易用性

当前公开使用形状如下；完整可运行包示例见[根 README](../../README.md)：

```csharp
using (var repository = EventHistoryRepository.OpenExisting(path)) {
    using var session = repository.Resume<World>("main", models);
    if (session.PendingEvent is null) {
        var nextEvent = GenerateSnapshotEvent(session.State);
        session.CommitDomainEvent(nextEvent);
    } else {
        DomainEvent pending = session.GetPendingEvent<DomainEvent>();
        var nextState = Handle(session.State, pending);
        session.CommitDomainState(nextState);
    }
}

// 上一个 writer 已关闭；独立历史浏览不建立可写 State 会话。
using var history = EventHistoryRepository.OpenReadOnlyExisting(path);
foreach (var frame in history.ReadEvents("main")) {
    DomainEvent e = history.ReadEvent<DomainEvent>(frame, models);
}
```

新库另提供 CreateNew/CreateBranch(initialState) 路径：初始 S0 也经过一次完整发布，见下节初始化顺序。
`CreateBranch<TState>(name, initialState, models, policy)` 返回已经发布 S0 的 `EventHistorySession<TState>`，
保留传入的 CLR 实例；`session.Head` 取得初始 `GraphFrame`。这避免初次写入后再 Resume 丢弃调用方实例。
策略参数可省略，本片记录时默认 `(3, 5)`；后继默认值选择见 [DB-070](0070-read-amplification-default.md)。
CommitDomainState 的无根参数形式使用 session.State。
不要求调用方传 roots list、Parent、ObjectId 或 DTO baseline；需要明确查看世界时调用 ReadState，
需要处理基态时从 EventFrame 查询其直接前 S。读取可以返回请求基类的异构事件子类。
注册方式沿 README/现有模型 facade；不引入事件专用 Schema 注册体系。

PendingEvent 的静态边界为 nullable DurableBase，typed getter 在没有待处理事件或请求类型不匹配时明确拒绝；
也可由调用方显式模式匹配。不会为了异构事件让框架扫描并生成领域业务 dispatch。

Resume(S) 只加载 S；Resume(E) 加载该 E 和其直接前 S。
初版两次实例化互相独立，各图内部保留共享/循环。State 的可写 baseline 只来自 S；
升级后的 E 不写回历史，也不成为 State 的比较基线。
CommitDomainState() 可保存原 State 实例；带 nextState 的形式支持 immutable replacement 根，发布后才切换 session.State。

首版另交付实验性 `ReadPair<TFirst,TSecond>(firstFrame, secondFrame, models)`（命名示意）：
返回按输入对应的 First/Second 只读快照，复用 DB-062 的双图读取核心，初始实现就是顺序独立还原两次。
每个 handle 明确仓库、Revision 和根；调用不改变 ref、不创建编辑会话，不强迫独立 ReadEvent 先加载 State。
API 不承诺跨图一定复用或一定不复用 CLR 实例；引用共享不是程序判断业务身份或版本的依据。
将来仅在不改变快照内容和既有身份约束的情况下逐步共享，算法不作为首版公开选项。
两份只读图的合同与内部可写 Resume 分开，后者继续保证 State 修改不会通过框架创建的可变别名改变 Event。

Branch 是持久 ref，会话是内存编辑所有者。只允许一个活动写会话；fork/Move/切换要求先关闭旧会话，
并从目标创建新会话。不存在沿旧 DTO baseline 继续写新 head 的操作。
只读图默认由调用方作为快照使用，不允许把它的内部实例表直接安装为编辑基线。

### Snapshot 使用合同

Event 表示记录时的观察/领域快照；后续处理读取它并更新 State。
其可达内容由用户保持只读，尤其不要回指活动世界容器或完整历史。
保存会冻结 DTO，却不会冻结原 CLR 对象；readonly 外壳不自动使 List/child 不可变。
热路径如果把 Event 和活动 State 的可变对象互相别名，仍由领域建模隔离未来会修改的部分；
持久的 E 保持旧值，不能据此保证调用方手中被直接修改的 event 变量也不变。

默认冷 Resume 的独立可变实例避免框架自行制造这种别名。DB-064 后续可机会性共享 DTO/string；
普通引用对象的共享只进入明确的只读双图操作，不默认用于可写 Resume。

## 5. 唯一发布与失败判定

新仓库布局拥有 `schemas.rbf`、`state/` 与一个 EventJournal 目录；**不创建或推进 publication.rbf**。
本片接管后删除旧 GraphRepository/PublicationLog 发布路径；不提供同时维护 publication.rbf 与 Journal 的配置或双写模式。
需要仓库级独占资源锁，覆盖写会话与 ref 修改；EventJournal 内部的单 lease 合同不能自动替代跨文件所有权。

提交顺序：

1. 检查会话 head/角色，Stage 原候选，完成 Schema/表示登记屏障。
2. StateRevision AppendDurably，准备所有可预先构造的发布/安装材料。
3. 追加以 expected Journal head 为 Parent 的图引用帧，完成 Journal frame 屏障。
4. AdvanceRef CAS(expectedHead, newFrame)，完成 ref 屏障。
5. S 安装原候选；E 释放临时 candidate 并保留 State workspace，仅更新 session 的 Journal head/pending event。

库可以用现有 AppendEventFrame + AdvanceRef 明确区分阶段；无需把 EventJournal.CommitToRef 的所有错误归成同一结局。
CAS 本来就是发布防误用边界，不能用仓库单 writer 假设省略 expected head。

初始分支使用另一条同样明确的发布路径：先持久 S0 图与 null-parent Journal 引用帧，
再 `EventJournal.CreateBranch(name, s0Frame)`。其 Create/Init/BindName 顺序中，名称绑定是可见发布前沿；
绑定前失败只留未发布记录，绑定尝试后结果不明则停止并重开查询名称和 head。
不先创建可见空 branch 再要求用户自行补救初始化。对已存在名字先拒绝，失败不自动换名或覆盖。
任意历史点 fork 可用已校验 frame 调用 CreateBranch(newName, selectedFrame)；
底层 ForkBranch 要求所给点就是 source 当前 head，不通过临时 Move 原分支来迁就该限制。

| 中断 | 严格重开时文件完整可读的结局 |
|---|---|
| Schema/Revision 已写，Journal ref 未变 | 原 head；新增记录是 orphan，不安装基线 |
| Journal frame 已写，AdvanceRef 尚未成功 | 原 head，或若 AdvanceRef 曾尝试则查询实际 head；不按 append 地址推测发布 |
| E ref 已发布，后继 S 尚未发布 | 返回 PendingEvent + 同分支前 S，恢复待处理位置 |
| S ref 已发布，返回前失败 | S 为已完成状态，禁止仅因上次报错重复处理 E |
| 半帧、损坏尾或无法验证的依赖 | 明确重开失败；不回退旧 head 或自动截断 |

保留 NotPublished/Unknown/Published 的含义和 faulted 后禁止续写；返回的图地址或 orphan 地址仅用于诊断。
I/O 失败不推断“肯定没写入”；尝试 ref 写后无法裁决的结果为 Unknown。发布后内存安装失败为 Published。
EventJournal 明确返回的 `RefCasMismatch` 保留 NotPublished；若已经写入 State/Journal，仓库仍停止续写。
上游 `CreateBranch` 是一个整体调用，不暴露内部 Create/Init/BindName 的失败位置，因此其无法裁决的失败保守报告 Unknown，
即使重开最终证明名称尚未绑定。只在测试层对真实 ref-op I/O 注入中断，不在产品中复制上游发布协议。
错误重开不会撤销任意领域修改、外部副作用或重新调用业务处理器。

显式关闭 EventSegmentStoreOptions、RefSegmentStoreOptions 和 RefOpLogOptions 的 RecoverActiveTailOnOpen；
关闭修复不等于已经完整验证，仍须审查相应打开/重放路径并注入坏尾测试。
本片保证范围沿 DG 的正常关闭、进程中止及受测 I/O 阶段；不扩写 OS crash/power loss 或目录元数据保证。

## 6. G0–G4 施工与验收

| 阶段 | 内容 | 验收 |
|---|---|---|
| G0 | envelope、严格资源打开、typed frame handle、元数据遍历 | golden/截断/未知 kind/错地址；readonly 零写入；同仓库原始闭包可核对，无 World typed callbacks |
| G1 | S0→E1→S1 热路径及唯一 ref | E/S 两种 Parent 正确；无 publication.rbf；same-State 连续 Commit 保留实例，nextState 替换仅发布后生效 |
| G2 | ReadEvent/ReadState/Resume 与实验性 ReadPair | 从打开到 E 浏览，World/Bob typed callbacks 为 0；单独 ReadState 不加载前 E；异构事件、图内共享/循环、旧值；pair 首版两次还原，错误来源/类型/第二图失败拒绝；跨图是否共享不作为断言 |
| G3 | fork/Move 与故障阶段 | 从旧 S 与 E 分支分别续写；旧会话不得继续；仅按所选逻辑 Parent 配对；三处发布中断、CAS 失败、发布后安装失败、初始 Create/Init/BindName 失败、坏尾不修复 |
| G4 | 真实 PackageReference 消费者与旧外观退出 | S0/Event/State 跨进程、ReadPair 与可写 Resume；两代 history+Upgrade、强制 Base；E-only reader；迁移 README/活动包 probe，清除旧发布器及过时专用测试；记录读取量与保存字节 |

用 `World(Alice, Bob) + Event(AliceSnapshot)`，快照不回指 World/Bob；加入列表/只读定义共享和根替换。
不能用“只查看已经打开且提前解码过 World 的仓库”通过独立事件浏览测试。
先完成根 build/相关 tests，再串行真实包 lane；新包依赖必须在隔离 feed 可解析。

施工分工：G0 为 HistoryJournal/GraphEnvelopeCodec；G1–G3 为 EventHistoryRepository/Session 与内部工作区；
G4 为真实包消费者及 README。旧生成器机制测试通过测试程序集内的 Fixture bridge 继续验证原有 body/history 语义，
它不属于产品兼容外观，也不替代直接使用 EventHistory 的外观与 PackageReference 验收。
LoadedWorld/PreparedWorldRevision 仅保留为内部机制入口；旧 GraphRepository/GraphSession/PublicationLog 已删除。
严格可写打开先持有仓库锁、只读校验 Journal，再确认 Schema/State 和原始图依赖，最后按 Event→ref objects→ref-op-log 顺序确认 Journal。

DramaBoard 接入是后续消费者工作：使用真实 Kernel 提交边界验证 Game+Spatial+Kernel 完整恢复，
不是只替换 IJournalSink 或验证 WorldSnapshot。CandidateKey、逻辑时刻等继续由领域决定。

## 7. ArtifactStore 与后继

建议先用 EventHistory 承担事件、消息、模型快照的历史定位与独立读取，暂不建设另一个 ArtifactStore。
这是以真实消费者覆盖一部分目标职责，不是将任何大二进制附件、chunk、外部内容地址或 Derived 缓存需求都证明为多余。
将来确需这些能力时，再按消费者选择；不让 State 持有全部历史对象链。

本片仍用仓库内单调 SchemaStore；联合 Schema 分支视图、自举和完整 CommitManifest 没有随之完成。
EventFrame/StateFrame 在本文是外观角色；落盘均复用 EventJournal 的现有 EventFrame，不混淆物理类型名。

## 8. 实现与验收记录（2026-09-11）

| 合同 | 实现与证据 |
|---|---|
| G0 严格打开、信封与来源 | [HistoryJournal](../../src/DurableGraph.Persistence/HistoryJournal.cs)、[GraphEnvelopeCodec](../../src/DurableGraph.Persistence/GraphEnvelopeCodec.cs)、[GraphFrame](../../src/DurableGraph.Persistence/CheckpointAddress.cs)；[信封 tests](../../tests/DurableGraph.Persistence.Tests/GraphEnvelopeCodecTests.cs)、[资源与屏障 tests](../../tests/DurableGraph.Persistence.Tests/HistoryJournalTests.cs) |
| G1–G3 交错提交、独立读取、Resume、分支与中断 | [Repository](../../src/DurableGraph.Persistence/Repository.cs)、[Session](../../src/DurableGraph.Persistence/BranchCheckout.cs)；[外观集成](../../tests/DurableGraph.Persistence.Tests/EventHistoryRepositoryTests.cs)、[真实 ref-op 故障注入](../../tests/DurableGraph.Persistence.Tests/EventHistoryPublicationFailureTests.cs) |
| G4 独立包消费及历史能力 | [EventHistoryConsumer](../../experiments/PackageConsumerProbe/EventHistoryConsumer/README.md)、[Run-EventHistoryProbe](../../experiments/PackageConsumerProbe/Run-EventHistoryProbe.ps1)；[全部包实验入口](../../experiments/PackageConsumerProbe/README.md)、[应用快速上手](../../README.md) |

- 根 `dotnet build DurableGraph.slnx --no-restore -v:q` 通过，零警告/错误。
- `DurableGraph.StateStore.Tests` 672 项、`DurableGraph.Tests` 1519 项通过；合计 2191，零失败/跳过。
  存储格式与二进制 body 未改；本片未另行重跑 Storage/Serialization 独立套件。
- 新 EventHistory lane 与迁移后的 17 条活动包 lane 均通过：StateStore、HistoryCapability、Array、List、Dictionary、
  CompositeDictionary、Nullable、Enum、Record、BclScalar、TemporalScalar、Generic、ValueUpgrade、InlineStruct、
  CrossAssembly、InlineLibrary、InheritanceLibrary。隔离 feed 包含 EventJournal 在内的九个依赖包。
- 新 lane 从 V1 的 E head 在第二进程/第二代程序读取：E-only 目录不含 World/Bob；ReadPair 保持各自版本值；
  Resume 的 World Upgrade 调用 1 次、Alice 调用 2 次（两份独立图）；World 与 Alice 强制 Base 后，
  后续 State 分别为零对象写入与一个 Delta 写入；根替换、共享/循环及只读文件零改变均通过。
  本次 fixture 最终 RBF 文件共 3676 字节，指标写入其 `metrics.txt`；这不是物理读取 I/O 或一般性能结论。
- README 的项目、Models.cs、Program.cs 原样提取，用真实 PackageReference 构建，两个独立进程依次输出 Hp=99、Hp=98。
- [ListDeltaReplayProbe](../../experiments/ListDeltaReplayProbe/README.md) 的 `Counts=32, Repeats=1, Rounds=1, DiffRepeats=1`
  smoke 通过：20 个独立测量仓库、300 份已验证 State Revision。报告 v2 明确计时包含 marker E 与后继 S，
  State 对象 payload 指标仍单独计量；不与旧单次发布报告直接比较。
- 独立代码审阅无未解决阻断项。修正了非法分支名预检、已知 CAS 拒绝分类及重开屏障顺序。
  包迁移时给两个极小对象夹具增加稳定持久字段，保留真实 Delta 链断言；按实际字节数选择更小 Base 的产品策略未改。

旧 public GraphRepository/GraphSession、PublicationLog 与旧 publication.rbf 路径已退出产品；没有兼容或迁移层。
原有低层生成器/容器测试的 Fixture bridge 只在测试程序集，真实包消费者直接使用新外观。
独立 ordinal RBF 的 PublicationCrashProbe 仍是底层进程中止见证，不被冒充为本片 EventHistory wire 验收。
本片完成后优先安排 DramaBoard 实际接入与 API 反馈；DB-064 的实际共享、联合 Schema 分支视图及更强故障保证继续后置。
