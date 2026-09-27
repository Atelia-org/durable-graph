# DB-065 · EventHistory 消费者上手与恢复合同

> 状态：已实施 / G0–G3 验收完成；2026-09-11。实际交付与证据见 §9。
> 基线：`297619b`。本片改善已有外观的使用与交付，不改变 E/S 提交状态机或持久格式。
> 来源：[DramaBoard 001 首轮 API 反馈](../../../drama-board/docs/feedback/durablegraph/001-eventhistory-api.md)。
> 反馈基于 `1cace42` 的接口审阅及此前验证；DramaBoard 尚未完成真实接入，不能当作线上故障或性能测量。

## 1. 问题与最小成功标准

新用户能否只依赖公开包、README 和编辑器调用提示，完成默认保存、引用事件快照、失败后的重开续写？

成功标准是同一套示例经真实 PackageReference 运行：默认调用可保存/重开；热处理与冷 Resume 得到同样的
业务快照值；E 已发布而 S 未完成时只从重新加载的 State 处理 PendingEvent，S 已发布但交付失败时不重复处理。
调用处文档必须实际进入 NuGet 包，不能只存在于本仓库源码。

## 2. 反馈裁决与代码事实

| 反馈 | 核对结论 | 本片处理 |
|---|---|---|
| 001-A 默认策略 | 成立。CreateBranch 和三个 Commit 入口均已有可选参数；README 仍在起步路径显式创建并重复传 policy | 采纳，删除最短路径的必填式示范，另段说明调优 |
| 001-B Commit 文档/恢复 | 成立。Session 类型 remarks 有边界，方法本身没有对应 XML；Outcome 与 IsFaulted 是独立维度 | 采纳，补方法文档、实际文档包产物和可执行恢复路径 |
| 001-C 引用事件快照 | 成立，是示例缺口，不是已有持久 DTO 冻结失效 | 采纳，增加含可变来源集合的业务快照示例；不新增通用 Clone/Freeze API |
| 001-D 跨重开定位 | 成立，现有 GraphFrame 是一次打开内的受检 handle，无持久 locator→handle 入口 | 保留候选，具体书签/引用需求出现再设计；本片只说明现有边界 |
| 001-E 最近 N 条 | 成立，ReadEvents 先全链物化再筛选；尚无长历史测量 | 采纳顺序/物化/成本文档；局部浏览 API 与测量另行触发 |

本片实施时 [Repository](../../src/DurableGraph.Persistence/Repository.cs) 的 DefaultPolicy 为 `(3, 5)`；
后继默认值选择见 [DB-070](0070-read-amplification-default.md)。
每次 CreateBranch/Commit 独立使用该次参数或默认值；CreateBranch 的覆盖值不会变成后续 Commit 的会话默认值。
默认数值是当前实现选择，不是业务语义或永久格式保证。

[Publish](../../src/DurableGraph.Persistence/Repository.cs) 先 Stage，再 State/Journal 屏障与 ref 发布，
最后 Install、更新 Head/PendingEvent 并返回。写入前失败可以直接抛原异常；发生追加尝试后才按发布阶段包装
[GraphCommitException](../../src/DurableGraph.Persistence/GraphCommitException.cs)。NotPublished 也可能已经使仓库 faulted。

[ReadFrames/ReadEvents](../../src/DurableGraph.Persistence/Repository.cs) 返回所选 branch head 的逻辑祖先链，
从旧到新，以数组完整物化；不把物理 orphan 或其他分支独有记录混入该链。它们不恢复领域对象。
打开仓库仍检查物理 Journal、引用 Revision/根等完整性，包括 orphan；枚举、冷打开与领域图恢复是不同成本。

与反馈基线相比，DB-064 增加安全共享读取，`297619b` 增加非泛型 ReadPair，均没有解决 A–E 指出的剩余问题。
反馈对 ReadPair 只读、可选用途的定位仍成立。

### 额外发现：XML 文档没有交付

设计核对时对 StateStore 执行 MSBuild 属性查询，得到 `GenerateDocumentationFile=false`、`DocumentationFile` 为空。
现有 `event-history-20260911134050-18232-cc49dac5` feed 的 StateStore nupkg 中只有 `lib/net10.0/*.dll`，没有相邻 XML。
因此仅补 C# 注释不足以满足 B 的编辑器可发现性；这是本片唯一需要调整的构建交付接缝。

## 3. 范围与不变量

本片负责根 README、必要的进阶使用文档、EventHistory 公开方法 XML、StateStore 文档产物、独立包示例和对应恢复验收。
保留 E/S 严格交替、前 State 比较基线、Journal ref 唯一发布点、单 writer/活动 session、现有异常和 handle 校验。

不新增业务处理器框架、自动 retry、自动回滚、Snapshot/Clone、全局事件 ID、locator、分页或一般 options 层。
不改 DramaBoard 模型/持久集成，不重排程序集，不扩大 record class、接口集合或其他类型支持。
下游反馈原文件由下游维护；本片记录上游结论并链接，不把其中的建议当作跨仓库修改授权。

## 4. 具体改进设计

### 4.1 最短调用与显式调优

README 的默认路径使用：

```csharp
using var session = repository.CreateBranch("main", initialState, models);
session.CommitDomainEvent(domainEvent);
session.CommitDomainState();
// 同 exact 类型的替换式 reducer：session.CommitDomainState(nextState)。
```

原完整 QuickStart 继续演示两进程保存/重开与 Upgrade，不把这一片扩为新的应用框架。
Base/Delta 参数说明留到完成第一次保存之后：两个整数的含义、当次覆盖、必要 Base 不受可选预算限制，
以及“省略参数不继承上次覆盖”的行为。不要把 `(3, 5)` 写成长期承诺。

浏览示例对 ReadEvents 的返回值只获取一次；不能用 `Reverse().Take(n)` 或改写为 IEnumerable 暗示底层按需读取。

### 4.2 调用处文档与实际包交付

至少覆盖以下入口；参数与返回值应说明本次操作的可观察效果：

| 入口 | 必须能在调用处发现的合同 |
|---|---|
| CreateBranch(initialState) | 初始 S0 已发布才交付 session；不是空分支；保留传入实例；失败也可能已发布 |
| Resume | 从持久 head 恢复，E head 同时恢复 preceding State/PendingEvent；不执行业务处理器；Transient 由应用重建 |
| CommitDomainEvent | 仅从 S head 发布 E；成功后 PendingEvent 是传入对象，State 基线不推进；保持 Event 内容只读 |
| 两个 CommitDomainState | 仅从 E head 发布 S；正常返回已安装候选并清空 PendingEvent；替换根须同 exact 类型；发布后交付仍可能失败 |
| PendingEvent / IsFaulted / GraphCommitException | PendingEvent 热路径仍是原对象；持久结果与实例健康分别解释；未包装异常也不代表领域修改已回滚 |
| ReadFrames / ReadEvents | 固定本次取得的 head，正序完整物化、逻辑链范围与 orphan 排除；不恢复对象；没有分页收益 |
| GraphFrame | 当前打开的 Repository 所有；RevisionAddress 是诊断地址，不是跨重开书签 |

只在 StateStore 项目启用 XML 文档生成与 SDK 标准打包。现有未覆盖的公共低层成员如触发 CS1591，
允许在该项目局部追加该项 NoWarn，并记录完整 API 文档不在本片范围；不关闭其他文档诊断、不全仓库批量补注释。
验收直接检查 nupkg 的 `lib/net10.0/Atelia.DurableGraph.StateStore.xml` 和上述成员记录，确保恢复后的包 DLL/XML 相邻。
不要只检查源码有 `///`，也无需为此引入自制文档生成系统。

### 4.3 恢复示例：新工作与已有 Pending 分开

恢复入口只做“重开所选 branch，若有 PendingEvent 则处理并提交结果”；它不在 PendingEvent=null 时创建新事件。
正常接受下一条命令是另一入口。如此才能避免把“此前 S 已发布”误当成“还没开始，需要重做”。

在已存在分支、宿主串行处理且不并发 Move/fork 的前提下：

| 重开后的事实 | 恢复动作 |
|---|---|
| E 为 head：State=S0，PendingEvent=E1 | 重建新 State 的 Transient，用新取得的 E1 处理新取得的 S0，再提交 S1 |
| S 为 head：PendingEvent=null | 恢复入口交付当前 State，不再应用旧事件，不创建替代事件 |
| 打开/Resume 本身失败 | 报告并停止；不删除文件、不退到旧 head、不无界重试 |

第二行只表示没有待完成 E，不能独自证明某个外部请求已经完成：如果失败发生在 E 发布之前，重开同样可能看到 S。
是否再次接收/提交那条外部命令由宿主的请求身份与业务协议决定。本片不承诺外部副作用 exactly-once。

示例为简化恢复控制，可在提交或业务处理失败后统一结束这次工作尝试、退出 using 作用域，记录异常/Outcome/IsFaulted，
随后由明确的恢复入口重新 OpenExisting/Resume。此保守示范不表示所有写前错误都会 poison session，也不强迫正常成功提交后重开。
不能只捕获 GraphCommitException 就宣称覆盖参数/Capture/业务回调失败。

新示例使用只修改领域内存、没有外部副作用的原地处理器，例如 `Hp -= pending.Amount`；事件快照已单独隔离，
不要求再复制整个 State。现有同类型根替换用法与回归保留，不把它设为新示例的前提。
无论采用替换式还是原地式处理器，异常后都不能把旧 State/Event 引用传入恢复尝试。
在恢复入口重新调用 Transient 重建；Restore 不执行构造器/字段初始化器。

### 4.4 业务事件快照：隔离将变化的内容

新增小例子包含 Character、World、DamageEvent、ActorSnapshot，以及至少一组观察值集合：

- Character 以业务 ActorId 定位，具有 Hp 和 `List<string>` 观察数据；ActorId 不等于 ObjectId/GraphFrame。
- Event 保存 TargetSnapshot 的 ActorId、当时 Hp、所需观察内容及 Damage 数值；不回指 Character.Partner 或整个 World。
- ActorSnapshot 是应用正常声明的 Durable 领域类，内部使用 readonly 标量/string 和私有 readonly `string[]`；
  只暴露观察数量/单个 string，不泄露可写数组。它与框架生成的 Versioned DTO 是两层不同概念。
- 从来源 List 复制成独立数组，元素 string 不可变，可以共享。说明 readonly 字段不冻结数组内容；
  如果真实模型的元素也是可变对象，还须隔离其可变内容，仅复制容器不够；本片不为此新增多层观察模型。
- 业务处理按 ActorId 找当前实体；事件中的观察数据只读。若应用使用不可变实体替换，旧实体/不可变子图可直接复用，
  不要求所有事件都深复制或建立一套专门 Saved 模型。

示例验收值：E1.TargetSnapshot.Hp 始终为 10；S1 中对应实体 Hp 为 7；来源观察集合增删/替换元素不改变 E1。
这同时适用于热 PendingEvent、进程重开后的 PendingEvent 和后续提交后独立 ReadEvent 的结果。

现有 [EventHistoryConsumer](../../experiments/PackageConsumerProbe/EventHistoryConsumer/README.md)
故意在 Event 引用 Alice 后修改同一 Alice，证明已落盘 DTO 不随热 CLR 变化；保留这个机制回归，明确它并不是只读事件建模的推荐范例。
新消费者可在独立示例/仓库中验证推荐建模，避免把原历史 Upgrade 与共享读取 fixture 一并重写。
event-only 读取用不含 World/Character 的登记目录证明不需要恢复无关领域图；不据此宣称 Open 不读取或验证 State 元数据。

## 5. 施工顺序与分工

| 阶段 | 内容 | 可交付证据 |
|---|---|---|
| G0 调用处合同 | 核对源码后补 XML、局部文档生成设置与包产物检查 | 主要 API 文档可从实际 NuGet 恢复目录取得；无行为/格式变更 |
| G1 推荐模型和运行路径 | 独立 PackageReference 的快照/恢复例子，分开提交新 E 与完成 Pending；省略默认 policy | 热/冷值一致，默认调用、领域引用和 event-only 登记可执行 |
| G2 故障见证 | 用已有内部 checkpoint/I/O 测试接缝验证与示例相同的恢复分支 | S 未发布、S 已发布但未交付、写前异常，均无旧图重用或重复应用 |
| G3 文档与集成 | 从已运行的代码整理 README 的短路径和进阶示例，更新包导航/活动上下文 | 完整示例原样执行、文档链接/差异审查、相关 build/test/package 验证 |

适合并行的有界工作包：A 负责 StateStore XML/csproj；B 负责独立包示例及其 runner；C 负责恢复测试。
主线程先固定示例模型/恢复分支形状，再安排所有权互不重叠的文件，并最终维护 README 与集成。
故障注入留在产品测试，不增加公开测试钩子，不让普通包消费者用反射修改产品私有字段。
验收复用示例中小的 Pending 判断/处理/Commit 控制函数，包例传入真实 SG 模型处理器，内部故障测试传入现有测试模型处理器；
可链接这一份普通 C# 源码。它只属于示例，不进入库 API，也不演进成 retry 框架。
StateStore.Tests 当前没有 SG analyzer，不为共享示例把整份带 DurableType 的模型链接进去并改变全套测试生成管线。
Windows .NET 验证串行执行。

## 6. 验收矩阵

| 编号 | 可观察结果 | 验证层 |
|---|---|---|
| A1 | README 默认参数路径原样保存/重开/继续保存 | 新 feed 的独立 PackageReference；保留原 QuickStart 的两进程与 history Upgrade、既有根替换验证 |
| B1 | Commit 两个 State 重载、Event、Resume、枚举等 XML member 文本进入 nupkg/恢复目录 | 包产物检查；不能由 ProjectReference 替代 |
| B2 | E 已发布、S 写入后 ref 前失败：即使 NotPublished 也可 faulted；重开仍 S0/E1，再次处理从 HP=10 得到 7 | 内部确定性 checkpoint + 与示例一致的恢复分支 |
| B3 | S ref 已发布、Install/交付前失败：重开得到 HP=7、PendingEvent=null，恢复处理器调用次数为 0 | AfterPublication/BeforeInstall；不允许再生成一条同义 E |
| B4 | 写前或业务处理原异常：允许不是 GraphCommitException，旧 CLR 修改不自动回滚；结束尝试后从持久视图恢复 | 写前/业务失败见证，保留既有真实 ref I/O 故障回归 |
| C1 | 同一业务场景热完成与 E 后关闭/重开完成，E.Hp=10、S.Hp=7、集合内容历史相同 | 独立包多进程；来源集合增删/替换元素不影响快照 |
| C2 | 再提交后独立读取旧 E 保持原值；仅登记 Event/快照闭包，不需要 World/Character 模型 | 独立包只读 open；不把元数据验证计作领域恢复 |
| E1 | XML/README 准确说明正序整链物化、逻辑 chain 和 orphan 排除；示例不暗示分页或冷启动优化 | 源码审查与既有逻辑链/只读回归，不预设性能阈值 |

现有恢复证据入口：

- [EventHistoryRepositoryTests](../../tests/DurableGraph.Persistence.Tests/EventHistoryRepositoryTests.cs)：
  KnownPrepublicationFailureDoesNotInstallCandidateAndColdResumeSeesPendingEvent、
  PublishedStateFailureFaultsWriterAndReopenMustNotReplayPendingEvent。
- [EventHistoryPublicationFailureTests](../../tests/DurableGraph.Persistence.Tests/EventHistoryPublicationFailureTests.cs)：
  PublishedEventFailureReopensAsPendingWithoutAdvancingStateBaseline、真实 Ref Create/Init/Bind 故障与 CAS 失败。
- [当前真实包 runner](../../experiments/PackageConsumerProbe/Run-EventHistoryProbe.ps1)：独立 feed、history Publish/Verify、两代模型；
  新推荐示例不替换其 DB-063/064 机制证据。

实施后运行根 `dotnet build DurableGraph.slnx`、相关 StateStore 恢复测试、新示例与既有 EventHistory 真实包路径，
以及变更文档的本地链接/锚点检查。生成代码/history 未变时，不为文档切片扩成全体类型消费者重新验收。
设计阶段只检查了源码、已有产物与文档；实施阶段的实际运行结果集中记录在 §9。

## 7. 未纳入本片的反馈

001-D 和 001-E 的后继问题与重访触发条件统一记录在[路线图](../DurableGraph-research-roadmap.md#4-明确延后及重访条件)。
当前命名 branch 可固定已选历史点，但必须由应用约定不移动它；它不是不可变书签合同。
GraphFrame/诊断 RevisionAddress 也不能代替新 locator 设计。分页即便以后基于固定 head 逐页，也不自动降低严格 Open 的成本。

record class、接口集合等适配工作量继续从真实 DramaBoard 集成采样；现有反馈不足以优先扩大类型范围。
本片没有发现需要改变提交拓扑、持久 DTO 冻结、冷 Resume 隔离或只读 ReadPair 语义的证据。

## 8. 设计复审记录

主线程核对默认调用、README、构建属性/现有包产物；一位 subagent 独立审查 B/C 的提交与快照合同，
另一位独立核对 D/E 的当前入口和历史读取路径。草稿再经 B/C 审查者复审，未发现施工阻碍。
复审收窄了三处：推荐示例采用原地领域处理而非额外复制整个 State；故障测试只共享普通恢复控制源码，
不为示例给 StateStore.Tests 引入 SG；观察数据选择 List<string> 到私有 string[]，不额外引入多层复制模型。
记录的是设计审查结论，不是新实现或新运行结果。

## 9. 施工接缝与验收登记

本轮边界为 A1/B1–B4/C1–C2/E1，不改产品提交逻辑、格式或增加 API。主线程负责集成、README 与验收。
独立恢复例子使用 `EventHistoryRecovery.PendingRecovery.Complete<TState>(session, rebuildTransient, apply)`：
先重建当前 State 的 Transient，仅当已有 PendingEvent 时处理并 CommitDomainState，返回是否处理；异常直接交给外层结束尝试。
此普通源码同时供真实包例子与内部故障测试编译，不包含 SG 模型、不进入产品程序集。

| 验收项 | 实施归属 | 证据 |
|---|---|---|
| B1、E1 调用处文档 | [StateStore 项目](../../src/DurableGraph.Persistence/DurableGraph.Persistence.csproj)、上述公开外观 | 新 nupkg/恢复目录 DLL/XML 相邻、XML 一致，12 个关键 member 的 summary/remarks 校验通过；仅项目局部忽略1591 |
| C1、C2 快照/恢复例子 | [RecoveryConsumer](../../experiments/PackageConsumerProbe/EventHistoryRecoveryConsumer/README.md)、[runner](../../experiments/PackageConsumerProbe/Run-EventHistoryRecoveryProbe.ps1) | 热/冷多进程均 E.Hp=10、S.Hp=7、观察保持；再次恢复无 Pending 不写文件；后续 S.Hp=4 时只登记 Event/Snapshot 仍读回旧 E；只读字节/时间戳不变 |
| B2–B4 恢复分支 | [同源故障测试](../../tests/DurableGraph.Persistence.Tests/EventHistoryConsumerRecoveryTests.cs) 链接 [PendingRecovery](../../experiments/PackageConsumerProbe/EventHistoryRecoveryConsumer/PendingRecovery.cs) | 新增 5 case；包含已发布后未交付的两个 checkpoint、NotPublished/faulted、原业务/写前异常；EventHistoryRepositoryTests 共 37/37 通过 |
| A1、G3 | [README 原文 runner](../../experiments/PackageConsumerProbe/Run-ReadmeQuickStartProbe.ps1) 与根 README | 从正文提取真实项目/Models/Program/Upgrade/浏览代码；独立包两次 V1 得 HP99/98，按文档升版后 HP97/Day1；保留旧 history，Clean/Verify 成功 |

独立审阅检查了调用处合同、共享恢复函数、快照模型、故障断言与两个新 runner，没有未解决的阻碍项。
四个外观 C# 文件去除 XML 注释/空白后的可执行文本与施工前相同；产品提交状态机、持久格式和公共签名未改动。

串行验证（2026-09-11）：

- `dotnet test tests/DurableGraph.StateStore.Tests/DurableGraph.StateStore.Tests.csproj --no-restore -v:q --filter FullyQualifiedName~EventHistoryRepositoryTests`：37 通过，0 失败/跳过。
- 新 Recovery runner 自建九包 feed，history 4 条 Publish/Verify 与 XML 验证通过；工作集 `event-recovery-20260911143309-59492-43efdcaf`。
- README 原文 runner 复用该 feed、独立包缓存与项目，工作集 `readme-20260911143711-20068-aaa00e9d`，验证通过。
- 既有 EventHistory V1/V2 runner 复用同批 feed，history 9/11、升级/共享/根替换等原证据通过；工作集 `event-history-20260911143801-55840-ddacd12e`。
- 根 `dotnet build DurableGraph.slnx -v:q`：0 警告/错误。此片没有修改 SG、Runtime codec 或 Storage 行为，不重跑全部类型消费者。
- 9 份变更 Markdown 的 337 个本地链接、20 个锚点检查通过，`git diff --check` 通过。

调试发现的测试边界：文件快照必须在独占仓库关闭后采集。可写模式的 ReadFrames/ReadEvents 会经 EventJournal
持久化派生 forward-plan 缓存；实际失败诊断新增的唯一文件位于 `journal/cache/forward-plans/v1`。
零写入断言应包围 Resume + 无 Pending 的恢复操作，额外历史枚举放在之后，不排除任何文件来掩盖变化。
只读浏览依然使用 OpenReadOnlyExisting，并保留完整文件字节/时间戳验收；该区别已写入根 README。
