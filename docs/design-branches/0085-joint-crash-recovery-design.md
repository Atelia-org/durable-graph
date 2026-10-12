# DB-085：atelia-storage / DurableGraph 联合崩溃恢复方案

> 状态：三路径方向已由用户确认，2026-09-29；第 0/1、2 片与 S0-A/B/C/D 受限内部实现已验收，证据见 §9/§10。公开只读默认未切换，完整恢复产品及平台门未完成。用户随后提出[统一系统根候选](0085-unified-root-comparison.md)，其[固定双值机制实验](0085-unified-root-experiment.md)及[目录/单次轮转对照](0085-unified-root-metadata-experiment.md)已验收；目录复制/全量资格成本和真实 DG 接缝仍待裁决，产品格式与公开路径未切换。
> 用户随后允许定制硬件/FS/OS，提出 ZFS + 4 SSD 候选及冷打开分级；§5 据此收缩验证范围，原全量检查不再是每次打开/恢复的无条件要求。
> 主文档只维护于 DurableGraph；上游改动在本文按组件分工，不复制另一份权威方案。
> [本次独立审阅](0085-joint-crash-recovery-preimplementation-review.md)记录新增反例、阻塞项和最小实验；[首轮记录](0085-joint-crash-recovery-review.md)保留历史裁决，当前建议以本文为准。

## 1. 最小目标与需求账本

目标：无人值守宿主启动时，由库处理协议允许的中断，恢复一致的已发布状态并交付恢复报告；证据不足时停止业务写入。应用不自行拼接各 RBF 的救援操作。本文件原路线的最小模型是：**持续持锁，连续前缀检查，限长只读验证保留依赖，按原发布顺序补全，最后全新严格打开。** 一个显式入口承载这条路径，既有发布记录继续作权威。此处保留现实现依据；统一系统根将改变发布权威，适用性及最小实验另见[候选对照](0085-unified-root-comparison.md)，不以原路线选择否定该候选。

| 编号 | 要求或选择 | 来源与状态 |
|---|---|---|
| U1 | 按 §10/§10.1 完成第 0/1 片 Audit 与第 2 片内部只读成本实验；保持公开 Open/可写及已有 working tree | 用户明确授权，两片已验收；恢复分片未随之实施 |
| U2 | 两仓无外部用户，唯一内部旧数据已升级；无需为了历史版本构建兼容框架 | 用户明确事实；公开包存在不等于存在外部消费者 |
| U3 | 关注全年无休 session 状态机，普通崩溃后尽可能自动恢复并告知异常 | 用户场景；自动恢复的最终默认策略尚待采纳 |
| U4 | 可以限定、定制硬件/FS/OS；候选如 ZFS + 4 SSD；希望正常关闭后的冷打开更轻 | 用户新增约束。具体 OS、OpenZFS 版本、拓扑及 Clean 协议尚未选定 |
| U5 | 确认 Normal、崩溃恢复、完整 Audit 三条路径；按需校验与正面的 Clean 证据方向成立，marker 是否采用先测量 | 用户明确同意；不是批准具体 API/格式 |
| U6 | 完整 Audit 实现在主库并提供 API；建立独立 CLI 工具集，提供离线维护/rescue 入口 | 用户明确要求；核心规则不能由 CLI 独占 |
| U7 | 在易用性、持久性、性能、复杂度之间求无明显短板的可行方案；高级别数据安全只承诺给已验证的强存储栈；困难时可提出目标边界调整 | 用户明确的取舍原则；由具体反例、成本和替代方案支撑调整 |
| C1 | 单 writer / 串行操作；DG 独占 repository.lock；原 Store/cache 关闭后才允许离线截断 | 当前 AGENTS、HistoryJournal、Store 实现 |
| C2 | 本片保留完整事实帧及地址，不把已发布依赖损坏解释为可回退；保留 tags、branches、历史及 outcome 语义 | 源码与持久化合同支持保全；“所有 orphan 永久不可清理”不是用户已选定律。补 Archive 是新增行为 |
| C3 | storage 日常 Strict Open 有界；显式恢复的额外成本独立计量 | 上游 bounded-online-io 实现与本轮性能动机 |
| C4 | DG 当前 Open 仍扫描全部物理 Journal 并验证图依赖；不能宣称 storage 有界就等于 DG 有界 | HistoryJournal.Open / ConfirmDurable、Repository |
| C5 | 当前保证不包含 OS crash/power loss 和目录元数据持久性 | PROJECT-STATE 当前能力边界 |
| P1 | 建议默认严格 Open，新增显式恢复入口；无人值守宿主显式选用，只读入口永不修复 | 本方案建议，待用户采纳 |
| P2 | 先做 F1 机制实验，同时提前检验 F2 核心假设；完整断电能力须通过现行操作覆盖及选定平台门 | 本方案建议；S0 不是完整恢复 API 或断电资格规则的冻结门 |

实际消费者为 DG 与内部应用；session agent 是目标使用场景，不能由此引入通用工作流引擎、outbox 或 exactly-once 系统。

## 2. 当前证据与不一致

核对基线：storage `bf7d68ab3f3fbad18b87549dbf6ce2aae499c24a`（含用户所指 `d0854be`）；DG `c900edd5b3a921b7bf46df2ac0482dc39597e606` 加现有适配 working tree。日常公开 pin 与本地候选试用不同，不从兄弟仓 HEAD 推断已发布包行为。

- DG 适配前已显式 `RecoverActiveTailOnOpen = false`，适配只是移除废弃成员。本提案是新增恢复契约。
- storage `RbfSegmentStore.OpenLocated/ValidateActiveTail` 拒绝 NextSegmentPresent / InvalidTail；正常 Open 不修复。
- `RbfRecovery` 已有只读候选扫描及 `TruncateToSuggestedTail`；默认 FrameBoundary 不是 FullFrame。现有 truncate 只复核 header/fence/长度，不证明文件来源、跨文件依赖或持久化完成，不能直接包装成“安全自动恢复”。
- EventJournal `CreateBranch/ForkBranch` 为 allocation → Init → BindName；`ArchiveRef` 涉及 Close 与 Archive；tag 在 ref-op-log 发布；ref move 位于各 ref 的日志。不存在一个统一全仓 commit 序号。
- `active.segment` 是 locator，不保存业务 head；`catalog.snapshot` 是带精确边界的派生 catalog，不保存 ref heads。不能把它们误称为统一提交水位。
- 日常 storage 打开是局部校验，toolkit audit 才是全历史审计；DG 自身仍 eager 校验。两者不能混为一谈。
- DG `ValidateHistory` 枚举物理 Event 和 active branches，不枚举 tag targets；ref 日常读取只检查首末端点，未覆盖历史 move。State orphan 在 `GraphResources` 只做物理检查。这些现有入口不能直接证明本方案的全部保留依赖闭包。
- `JournalFormat.Publish` 使用 temp flush / rename，现有合同明确未证明目录断电持久性；正常崩溃中的 leftover next、孤儿 allocation 与半个 Archive 也不等于坏尾。
- `CatalogCheckpointTests.AllocationWithoutBindAndCloseWithoutArchiveRemainDistinct` 已展示：Close 落盘后崩溃，EventJournal 可严格重开，但 GetHead 失败。只在 Open 抛异常时才检查恢复会漏掉它。
- `StateRevisionStore` 使用 Builder；其 HeadLen reservation 在内存阻塞下推，填好后由 `SinkReservableWriter.Commit` 顺序推送。不能因“回填”一词推断它在磁盘先写零 HeadLen、再随机覆写。

## 3. 故障模型与不可约语义

### 3.1 两档证据，不设两套恢复语义

F1 首个机制实验范围：既有完整初始化仓库的单进程 writer 中止，或符合下述形态的 I/O 失败后废弃资源再启动。没有并发外部修改；已成功完成的持久化屏障所保护的前缀保持；执行中的顺序写只留下真实 writer 字节序列的前缀。为 Append、Builder、CreateNew 分别建立可执行的写序/中断形态表。这个前缀模型是明确假设，不是对任意 IOException 的推断。权限、设备不可用不转成损坏，更不触发截断。

F2 最终目标：OS crash / power loss。逐平台说明文件长度、创建、rename/replace、截断及目录持久性，并考虑非前缀落盘。OS 和文件系统仍可启动不是上述保证；kill-process 也不是断电证明。多扇区写并非天然原子，完整长度、Trailer/Fence 已落盘而部分 payload 未落盘是一类必须检验的模型反例。[Windows WriteFile 合同](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-writefile)

还须区分两个前提：成功 flush 可靠持久，以及后续追加不损伤写入范围外的旧字节。RBF 的 4B 对齐不证明第二项；它对应独立的 powersafe-overwrite 假设。若不成立，共享扇区中的已确认旧帧也可能受损，增加完成 marker 本身不能恢复旧字节。[SQLite 对该假设的定义与保护措施](https://www.sqlite.org/psow.html)是反例依据，不是当前部署平台的能力证据。

因此 **S0 就做小型 F2 模型检验，不能等 S1 完工再首次调查核心假设**。在可靠 flush、旧前缀受保护、串行依赖写序成立的模型下，末次坏 CRC 是否已经足以判为未完成追加，应先论证；不能预先断言新水位必需或永远不需要。必要时再比较平台约束、写入布局保护或最小完成证据。当前 F1 分类仍拒绝全长坏 CRC；这只证明该路径安全停止，不代表最终断电目标已满足。

用户现已允许把受控存储栈作为产品前提。§5.1 将该前提具体化；它允许用已经证明的 writer 因果顺序代替重复的全历史读取，但不将“ZFS”这个名字直接当作应用帧原子性或跨文件事务证明。

不覆盖任意 bit rot、外部截断/删除、错误程序覆写、设备谎报成功、旧备份覆盖或多 writer。遇到可检测异常停止；本地文件无法保证检测整段完整历史被外部删除。

首次 CreateNew 根初始化未完成、缺 journal.format 的目录不自动接管，更不能返回一个新空仓库来冒充恢复。这个边界不排除**已有仓库**的分支/分段创建中断。

### 3.2 恢复选择

- 原路线从既有发布记录重放已完成语义，不在其外叠加另一套“最后好状态”权威或全仓 commit watermark；多 branch/tag 继续各自发布。此为现路线选择，**不是禁止统一根的产品定律**：用户提出的候选以唯一系统根替代分散权威，不要求新增跨分支业务批量事务；是否采用由对照证据决定。
- 已成功返回的提交在支持故障模型下必须存活。未收到成功答复的操作可能已落盘，按现存完整发布记录决定，不自动重试业务。
- 本片保留所有完整事实帧，包括 orphan；它们可能承载已交付地址、RefId 或历史。EJ 的 CAS 失败会返回 `OrphanEventAddress`，不得把“当前 head 不可达”当作删除许可。这不是对未来 GC 的永久决定，本片不做 GC。
- F1 只舍弃获得资格的物理末尾不完整后缀。已有可验证字节与真实 writer 前缀相矛盾、未知格式、中部损坏、语义校验失败均拒绝，不能搜索一个旧 head 掩盖。形态符合模型不等于查明了故障原因，F1 分类不能直接充当 F2 最终舍弃规则。
- 每个保留记录的持久地址和 Schema 依赖必须继续成立；涵盖非当前 branch、tag、历史 ref moves、orphan Journal 及完整 State orphan。可由 §5.1 的受控协议证明或显式完整检查建立保全资格，不要求每次重新读取全部记录。发现完整发布而 target 缺失仍拒绝，不先裁掉发布记录；也不新增全部历史业务对象经当前模型物化的审计合同。
- 仓库恢复不自动重放对外工具调用。session 自己保存执行阶段与操作标识；副作用不确定由应用处理。

## 4. 组件分工与启动路径

### 4.1 RBF：物理证据，不拥有提交策略

复用 `RbfForwardEnumerator / RbfReadImpl` 与 checked read，补一个窄的“连续前缀检查”能力：从文件头或已有且适用的可信边界沿物理帧顺序检查到 EOF；沿途完整帧须校验。自动路径不调用反向 scanner；后者继续服务人工分析。payload 内嵌合法帧不影响正向帧边界。snapshot 的边界只有在原 checkpoint 写序成立时才可作为该 ref-op-log 的检查点；一个 anchor 的 CRC 不证明其他文件或 ref 生命周期。

检查结果只表达物理事实：完整至 EOF、首个不完整候选、损坏/未知格式、工作限额未完成；这些不是最终修复授权。记录原长度、最后完整边界和错误位置，支持 header-only 边界及实际 EOF 非 4B 对齐的输入。正常 `RbfFile.OpenExisting` 会拒绝非对齐文件，因此检测器须有窄的原始只读入口，不能先严格 Open 再假装已覆盖短写。

分类器须检查**已经存在且足够验证的字节**，不能把 `ReadFrameInfoAt` 的 beyond-EOF 错误直接映射为 IncompleteSuffix。部分 HeadLen 须能补全为合法长度；完整 HeadLen 本身不是未完成写入的证明。FrameBytes 已齐而 Fence 缺 1–4B 时，先校验已有 Trailer、PayloadCRC 和 Fence 前缀；任何矛盾均拒绝。其他部分写入形态由真实 Append/Builder 输出的前缀表限定。

满足上述条件的缺 Fence append 可舍弃**整条**，退到前一个完整边界，不补 Fence 来升级事实；还必须通过上层依赖检查。F1 全长坏 Fence/CRC、未知格式和不能解释的 header 均拒绝。

候选 EOF 必须约束所有读取，包括 ticket、`RbfFrameInfo`、TailMeta、缓存命中和预取，不能只改 `TailOffset/ScanForward`。当前随机读直接经 `RandomAccessReader` 访问真实文件，是必须补的接缝。所指帧须属于已验证连续前缀，资格包含正确 Fence 和 `ticket.End + FenceSize <= candidateEnd`；ticket 本身不含 Fence。范围计算检查溢出，超界在大 buffer 租用前拒绝。S0 可以关闭候选缓存，始终不得借用旧 Store/cache。

检查接受取消及一个共享读取限额，覆盖范围按 §5 明确；不先设计多维公共预算、断点续扫或策略框架。MaxHits 不是读取字节上界。超限报告“检查未完成/需要更多预算”，不报告损坏；全部预检未通过前不修改源文件。

执行截断必须在持续独占所有权下重新确认预期路径、长度和边界，截断后执行持久化屏障。内存动作只在本次锁作用域使用，不持久化成可外部重放的计划，不用全库 hash 清单替代生命周期约束。跨包必要能力以最小维护接缝验证；S0 后再确定公开签名及适用范围，不发布通用 RecoveryProvider 框架。

### 4.2 SegmentStore / EventJournal：拥有各自协议恢复规则

SegmentStore 负责识别 active、next 和轮转阶段，EventJournal 负责 catalog、ref 生命周期、tag 与事件依赖。低层只产出证据和候选动作，不在 DG 完成跨层检查前私自落盘。

| 磁盘形态 | 建议处理 | 必要证明 |
|---|---|---|
| active 连续完整前缀 + 不完整尾部 | 候选截断至前缀末尾 | 所有保留引用均不越界 |
| locator 指旧段，next 是本协议创建的空段 | 唯一选择：重新确认持久性后完成 next locator 发布 | old 完整，next 严格 header-only，无冲突后继；旧帧和编号不变 |
| locator 指旧段，next 仅有 0–3B 合法 HeaderFence 前缀 | 补齐空段 header → durable → 发布 next locator | 父级 locator 与真实 CreateNew 写序证明其唯一身份；不能把任意短文件当空段 |
| locator 指旧段，next 有数据 | 拒绝自动选择 | 违反“locator 发布后才向 next 写”的假设，或 F2 元数据屏障缺失 |
| allocation 完整，Init/BindName 未完成 | 保留 allocation、完整 Init；合格的半 Init 可截尾；更早的空容器残片策略由 S1 小实验选择 | 证明 never-bound 与真实创建阶段；不合成 Init/BindName，不删除完整帧或复用 RefId |
| Close 完整，Archive 未完成 | 重新确认 Close 的持久性，再从当前 name→RefId 补写既有 Archive | 同一 active binding；checkpoint 使用实际追加前 catalog；已 Archive / 重绑后不再补 |
| BindName / tag 完整，但依赖缺失 | 拒绝 | 不回退名字或 tag |
| catalog/locator 未知版本或损坏 | 拒绝日常恢复，另行离线审计 | 禁止借机自动全量重建索引 |

多文件恢复动作应只影响已证明不属于保留事实的尾部和现有发布协议允许的 metadata。每个动作完成后或中断后都能重新检查再执行。若某窗口无法证明该性质，必须先修写入协议，不能用一份通用恢复 WAL 掩盖。

**Archive 的具体重入规则：**从 snapshot 精确边界 + 实际完整 suffix 重放 catalog，绝不截至 snapshot boundary 之前。仅对仍绑定 `name→refId` 且最后完整 move 为 Close 的 pair，从 Close 的 sequence/timestamp/reason 重建 Archive。完整 Archive 已存在则不补；名字重绑新 RefId 则不补；半 Archive 先按已验证动作截尾并 flush。追加前重新确认 Close 所在 ref 文件，不能把这个屏障留给最后重开：完整可读的 Close 可能尚未完成 flush。

窄 helper 复用容量预检/checkpoint/append/flush，不能调用拒绝 Closed 的 `ArchiveRef`。需要 checkpoint 时，编码的是**实际追加前 catalog**及其准确 boundary；不得先从候选字典解绑再 checkpoint，否则 snapshot 会提前包含尚未追加的 Archive 结果。Archive durable 后才安装解绑。预期结果投影没有持久化权威，不必编码成虚拟 Archive 帧。

**未绑定孤儿规则：**allocation 已占用 RefId，后续不回收。无对象目录则不创建；完整 Init 保留，不凭 allocation 合成 Init/BindName。有效 locator 后的半 Init 可依尾部规则丢弃。证明 never-bound 不能只看当前 catalog：已归档 ref 也无名字。当前 v2 要求 BindName 紧邻 allocation，可 checked-read 该精确后继来证明是否绑定；这一局部证明须服从现有 replay 不变量，不能泛化成任意格式的捷径。

对更早的 **locator 缺失、无完整事实帧** 的规范初始化残片，S1 比较两种内部候选后只选一种：补齐 header/locator，或清理合格 stub 回到 allocation-only。后者须连同严格识别的 locator temp 与空目录建立重入闭包，不可递归删未知内容；前者增加 header/locator 写入窗口。当前 toolkit 的 `legalEmpty` 同时支持对象不存在和单段 header-only，不能仅因工具方便就认定某候选最小。

这个例外不含损坏的现存 locator、未知内容或已绑定对象。F1 下缺 locator 却有完整或部分 Init 也不是正常创建前缀：CreateNew 先发布 locator 才返回，之后才写 Init。首轮选定的“一律补齐已开始的容器”降为待实验候选，不扩大为两个公开恢复策略。

### 4.3 DG：唯一跨层协调者

建议提供一个高层显式入口，概念示例 `Repository.OpenWithCrashRecovery(...) -> (Repository, RecoveryReport)`，名称与签名在实施片冻结。Strict Open 与只读 API 保持明确；不新增每层互相独立的自动恢复 bool。

内部顺序：

1. 获取仓库独占锁；没有旧 Store/缓存存活。把现有锁获取与 HistoryJournal 正常打开拆开，以便在坏尾时先持锁；检查、动作及新开之间不能释放锁。
2. 主动检查承诺范围内的物理尾部及生命周期窗口，不能只是 `try StrictOpen; catch => recover`。DG 复用已有 active refs 检查，发现 Closed+bound 也进入协调。
3. 有候选动作才建立固定路径、固定可见长度的只读 reader，复用 parser、协议规则和共同校验函数。先确定 §5 的信任资格，再执行相应范围的检查；没有受控协议证明时保留全量检查。S0 不模拟目录、rename 或待写控制记录；候选资源绝不作为可写 Store 或领域对象交付。
4. 全部预检通过后关闭检查用资源，在同一锁内执行确定动作。修好相关物理尾部并 flush；每个新发布动作之前，按原 writer 的顺序重新确认其依赖，特别是 Close → Archive、segment → locator、实际 op-log → snapshot。交付 writer 前还须确认将被续写复用的不确定事实，不能只确认本次修过的文件。每步完成或失败都从盘上重新推导，不能只证明函数重复调用幂等。
5. 有修改则全部重新严格打开，新建资源与缓存；成功后才交付可写 Repository。无动作路径把恢复入口的必要增量检查并入原打开过程，避免再增加一次独立全历史预检；这不意味着现有 DG Open 本身只有一遍读取。
6. 返回恢复报告：实际文件动作、原/新长度、观测原因、恢复后可用 heads、补全的协议操作。失败报告区分拒绝、检查未完成和执行中 I/O 失败。损坏前可能没有合法 head，不计算虚构的“前后业务状态差异”。

“离线”指资源生命周期，并非必须人工。宿主在启动时显式调用即可自动完成。当前 AGENTS 禁止普通 Open 自动截断，实施时应明确新增**显式离线恢复入口**的授权与资源纪律；本提案没有修改现行规则。成功报告由宿主写日志，不断言断电原因；报告交付前再次崩溃可能丢失本次诊断，但不能影响恢复幂等性。耐久回执延后，直到确有跨第二次故障审计需求。

当前 EventJournal/RefMoveStore 持有具体 SegmentStore，reader lease 也绑定具体 owner；只包装一个 `IRbfSegmentStore` 还不够。S0 要证明最小只读构造接缝与共同校验函数确实可用，不因此建设影子文件系统。以下是持久依赖的责任表：完整 Audit/保守恢复须覆盖这些边；可信路径须以协议证明覆盖同样责任，不能只省略遍历。它不是“当前 Open 已全部做到”的声明：

| 保留记录 | 不得因本次修改悬空的依赖 / 复用入口 |
|---|---|
| Schema | 完整目录批次及先前节点；沿用 `SchemaStore` 全量重放 |
| 全部物理 Journal，含 orphan | Parent、Revision、根及现有图结构检查；提取 `ValidateHistory/ValidateGraph` 共用 |
| 全部完整 State，含无 Journal 指向的 orphan | Parent、external heads、local priors、Base 的 RepresentationId；复用 Storage reader 和类型头 codec，补齐当前物理 sweep 没有枚举的持久边 |
| tag、allocation/Fork、所有 ref moves（含历史/archived） | tag target、起点/来源、old/expected/new targets、来源 move 身份；抽取现有 codec/toolkit 的必要共同规则 |
| catalog / locator | snapshot boundary 与实际日志相符，name→RefId 和段定位可由既有协议解释；不借候选投影提前发布 |

修订第二次审阅的无条件扫描要求：没有反向索引、也没有可信发布因果证明时，局部读几个 heads 不能穷尽入边；保守路径仍须检查所有可能引用候选范围的记录。§5.1 的前提成立时可以用因果证明替代该搜索。遇到未知格式、来源失去信任或观测到协议矛盾，停止快速修复，转只读 Audit/维护；Audit 也不自动获得回退已发布数据的权限。共用解析和规则，不搬入 toolkit 外壳。

保留原字节、验证持久依赖、审计全部业务语义是三个不同要求。本片不新增 State orphan 的应用 body/factory/Upgrade 或全图物化审计。当前 DG 的结构规则保持；可信 Normal 路径把其检查时机移至访问/发布前，Audit 以全枚举驱动同一规则。模型缺失或应用升级失败不触发回退。

standalone EventJournal 可共享同一个维护核心，但不能因此获得 DG 的图依赖保证。用户现已要求库内 Audit API 与独立维护 CLI，撤回“不做 CLI 新入口”的旧范围限制：由 DG 的持久化层协调仓库级审计，上游生产库提供必要的 RBF/SegmentStore/EventJournal 规则；CLI 只调用库，不成为恢复策略权威。

### 4.4 库内 Audit 与维护 CLI（已确认分工）

Audit 核心放在现有产品库的适当层，DG 仓库级入口由 `DurableGraph.Persistence` 承担；不把需要 Storage/EventJournal 的逻辑反向放进领域 Runtime，也不为看起来完整而新增多个维护程序集。首片入口为 `RepositoryMaintenance.Audit(path, cancellationToken)`，具体报告及验收见[施工记录](0085-audit-implementation.md)。

Audit 必须能够在正常 Repository 无法打开时报告问题，不能要求调用者先成功 Normal Open。它以独占协调或稳定离线副本保证输入不变，返回结构化的完成状态、发现与位置，支持取消；未知格式、I/O/取消造成检查未完成，与已确认损坏分开。正常 Audit 只读，不调用用户 factory/Upgrade，也不隐式应用修复。

首片将原 [JournalToolkit](../../../atelia-storage/tools/EventJournal.Toolkit/JournalToolkit.cs) 的 Audit 引擎移入上游生产库 [EventJournalAudit](../../../atelia-storage/src/EventJournal/EventJournalAudit.cs)，分段库存由 [SegmentStoreAudit](../../../atelia-storage/src/RbfSegmentStore/SegmentStoreAudit.cs) 共用。旧 toolkit、DG Audit 使用同一核心，后续恢复据需要复用；产品库不反向依赖 CLI exe。索引重建、旧版迁移和命令输出外壳仍在工具中。

新维护工具集提供 `audit` 和显式 `rescue` 等入口：CLI 负责参数、可读/机器可读报告及退出状态；检查、修改资格、锁、资源寿命和修复动作均由库拥有。首片先接通只读 audit，rescue 随 S0/S1 逐步接入同一恢复核心，不能让宿主启动恢复和手动维护各有一套算法。检查与应用分开不意味着先建设可持久化、跨进程重放的通用修复计划。

首片使用原有 [DurableGraph.Cli](../../src/DurableGraph.Cli/Program.cs) 空入口承载新工具程序，`audit` 直接调用库 API；不新增维护程序集或另一套检查语义。

## 5. 性能与范围

- 健康 storage **Strict Open** 保留现有有界检查。没有可信 Clean 证据时，要发现任意 Closed+bound 至少检查所有 active refs；异常路径还可能扫描相关历史，不能宣传它与 Strict Open 同成本。
- 异常恢复可比健康打开贵，但须有明确预算/维护降级；预算不足不意味着数据坏，更不能降低校验强度。
- 用户新增的性能目标允许显式研究 Normal/Audit 分离；它改变错误发现时机，须通过 §5.2 的接线验收，不能直接删掉已有校验调用。
- S0 不先建设每文件 durable watermark、全仓事务日志、双份数据库、自动历史回滚、通用救援插件或 v1/v2 兼容层。Clean/Dirty 因新增的正常关闭需求进入 §5.3 的有限候选，不再无条件排除；是否需要由可写启动合同与测量选择。
- 持久格式只有在现有协议确实无法以小改动恢复时才调整；唯一现存数据的备份/升级是实施时独立步骤，不以当前已升级推断未来格式无须迁移。

当前 State/通用 SegmentStore 和 EJ events 默认阈值为 **64 GiB**，EJ ref segments 为 **64 MiB**；阈值只在借 writer 前检查，不是硬上限。单帧最大约 256 MiB，现有 `ReadPooledFrame` 按整帧租 buffer；Schema/ref-op-log 还可持续增长。

不能把成本写成只有 O(active refs)：DG 打开已有物理扫描、地址扫描、逐历史图/live object 的依赖遍历，`ConfirmDurable` 又检查 Journal 文件。候选预检和最终重开都参与总成本。S0 分别记录各阶段的读取请求/实际读取字节、耗时、峰值内存与最大帧；缓存命中不消除 CPU 遍历，读取上限也不是堆上限。目前只有源码成本证据，**尚无新恢复入口的性能测量**。

一个共享读取限额和取消须贯穿检测、候选依赖验证及最后重开，预取也计入；尚未接入的阶段必须在实验报告中明列，不得声称已有端到端上界。预检超限不改盘；执行已经开始后的 I/O、取消或重开超限可能留下已完成的合法修复动作，须报告阶段并允许重新检查，不能承诺回滚或字节未变。支持规模与恢复时长经测量后选择；反复以同一过小 cap 启动不会使合法大仓库自行恢复。

### 5.1 受控部署：用具体保证收缩故障模型

首个候选为用户提出的 **OpenZFS + 4 SSD，可定制 OS**；还不是已验收配置。资格取决于以下合同，不取决于阵列名字或盘数：

1. 成功的同步屏障保护数据、长度和该发布协议需要的名字；设备/控制器不能丢弃已确认写入。
2. 后续写入不破坏已确认的旧范围；读出的旧内容正确，或报告 I/O/完整性错误。
3. 所有 writer、恢复器和维护工具遵守同一独占、格式和发布协议；引用者的第一字节写出前，依赖已完成语义验证及持久确认。不把普通非 durable `Append` 的任意调用者纳入保证。
4. 导入、未知 writer 版本、绕过协议的修改或配置降级不沿用快速路径资格。可信基线由受控创建，或一次适用的完整验证与持久确认建立。

OpenZFS 的 CoW 和校验树为旧内容保护提供基础；`checksum=on` 默认校验用户数据，`sync=standard` 履行同步请求，而 `sync=disabled` 不符合上述合同。[CoW 说明](https://openzfs.github.io/openzfs-docs/Basic%20Concepts/Copy-on-write.html)、[checksum / sync 属性](https://openzfs.github.io/openzfs-docs/man/master/7/zfsprops.7.html)。仍须对选定版本、设备和 .NET 文件/目录操作验证合同；文件系统事务组原子性不能直接推成一次 RBF Append 或 DG Commit 原子性。

上述前提及恢复后的重新确认成立时，可以证明：**完整引用记录 A 已写出 ⇒ target B 的屏障此前已成功 ⇒ B 在支持故障模型下仍完整 ⇒ B 不属于可丢弃的进行中尾部。** 这对协议产生的记录整体成立，无需每次扫描全部旧 tag/ref/orphan 来搜索反向引用。语义保全不变，证明方式改变。全库 Audit 继续用于来源不明、维护和显式健康检查；快速路径不承诺发现所有未经读取的伪造历史。

### 5.2 检查范围：Normal、恢复与 Audit

用户已确认按目的分三条路径；API 名称待定，不设计可关闭必要守卫的 Low/Medium/High 组合。Strict 指不隐式改盘，不等于全历史 Audit。

| 路径 | 检查与行为 | 成功意味着什么 |
|---|---|---|
| Normal | 必要 format/locator/catalog/active tail；实际访问的帧仍 checked-read，按需检查其结构与依赖；不隐式修复 | 仓库可按该合同使用，未访问历史未被全库认证 |
| 显式恢复并打开 | 对受控协议可能中断的尾部、next、catalog suffix、ref 生命周期做检查；依原规则修复，重新确认不确定依赖后才交付 writer | 已闭合承诺范围内的中断；不把一次 crash 自动等同全部历史损坏 |
| Audit | 只读枚举全部事实及持久依赖；按需要用于导入、维护、怀疑损坏或人工强制检查 | 完成所声明范围的全量审计；不自动授予删数据权限 |

轻量路径首先省去未被使用历史的读取和重复遍历，保留实际读取时的 RBF CRC、wire/地址/Schema/图关系检查。FS 会忠实保存软件写错的字节，不能替代这些规则。按需检查把某些历史错误的发现从 Open 推迟到首次访问，须在 API 合同和测试中明确。

第 2 片实施时用户明确选择了受控来源边界：随机 checked read 不独立证明地址是文件的真实物理帧成员，合法 opaque payload 内嵌合法帧的反例继续保留。未知来源先经适用的完整 Audit 建立结构基线；受控来源也不能豁免访问时可观察的依赖矛盾。候选的实际结果与公开接入限制见[成本实验](0085-normal-readonly-experiment.md)。

当前源码可减少的工作包括 `HistoryJournal.Open` 的三段全历史读取及 `_frames` 常驻、`GraphResources` 的全部 State sweep，以及 `ValidateHistory` 的逐历史图遍历。`_frames` 的产品消费者只有 ValidateHistory，历史查询已经按地址导航，故无需先新增持久历史索引。但 `SchemaStore` 仍要重放目录以建立运行索引，catalog 和首次查询也有成本；须同时测 Open、首次 Checkout/查询及驻留量，不能把工作移到第一次使用后宣称全部消失。

**实施接线门：**`CheckFrame` 目前只查 owner/kind；HeadCore/ResolveTag 签发句柄、历史 CreateBranch/CreateTag/Move 都受益于原先 Open 的验证。移除 eager 校验前，须提取共同的按地址结构检查，在访问/签发相应句柄/再次发布前检查所需祖先、最近 State、Revision Parent、根与现有持久依赖。GraphReader 物化成功不能代替 Journal 与 Revision 的 Parent 对应检查。只读性能原型通过也不自动授予快速可写资格。

**可写确认门：**可信 Clean 或替代的活动前沿检查/确认合同未成立前，Dirty/Unknown 可写启动保留保守验证和确认。具体反例是 source ref move 完整但未 flush，重启后 Fork 将其 sequence 持久写入新 allocation，再断电丢掉 source move。当前 ForkBranch 只显式确认 source Event 段；DG 现有全文件 ConfirmDurable 覆盖了 ref 文件，不能直接删除这层保护。这是优化必须防止的窗口，未声称当前 DG 已观察到该丢失。

### 5.3 Clean 是可选的持久证据，不是缺少 crash 通知

外部 crash 信号可以强制升级检查；缺少信号不能证明正常关闭。若选择 Clean 优化，只增加一个仓库级会话状态，不保存第二套 head、每文件水位或全库 hash：

```text
持锁读取旧状态 → Dirty 持久化完成 → 允许可写打开/恢复/业务修改
→ 停止新操作且健康、到达允许停点 → 完成必要数据/metadata 屏障
→ 成功关闭 Store/cache，仍持锁 → Clean 持久化 → 释放锁
```

Dirty 持久化失败就不得开始任何仓库修改。普通 Dispose/finally、没有异常退出码或仅 IsFaulted=false 都不足以签发 Clean；当前 Dispose 只是释放资源。恢复过程及交付后的整个可写 session 保持 Dirty，不能在“恢复完成”后留下 Clean 再继续写。Clean 发布自身中止时，业务依赖已经完成收尾，磁盘为 Dirty 或新 Clean 都安全；损坏/缺失/不支持的记录按 Unknown 处理，绝不误走快路径。所有维护工具也必须失效旧 Clean。

Clean 只证明受控会话已完成收尾，不证明全部历史通过当前模型的全语义审计，也不能替代按需结构校验。它不是省去历史 Audit 的必要条件；若每次可写打开都能低成本检查并确认必要活动前沿，可以不增加 marker。比较两种内部实现的剩余 I/O、flush 和状态数后选择，不先形成多个公开持久化策略。

### 5.4 最小性能实验与新增否定见证

先做只读 Normal 与 Audit 对照，保留共同结构检查：固定 Schema/catalog、当前访问图及其祖先，只增加无关历史，Normal 不应随之全扫；另增加 Schema 和所需祖先链，报告仍存在的成本。先不新增 Schema checkpoint、UncheckedReader 或长寿命验证缓存。

随后再选可信 Clean 或活动前沿确认来取得快速 writer 资格；必须过三组反例：旧 Clean 取消/新 Clean 发布的每个中断、CRC 正确但 Parent/Revision 错误的 target 在新发布前被拒绝、完整未 flush source move → 重启 Fork → 第二次故障不产生悬空来源。可信局部恢复还要覆盖所有承诺的生命周期窗口；布尔 Dirty 不定位改过哪些文件，不能据它宣称恢复耗时有固定上界。

## 6. 垂直实施与验收门

### S0：先证明一个最小闭环

**当前判定：受限内部 S0 已完成验收；不能据此冻结完整恢复 API。** 第 0/1 片 Audit 及 [S0-A 写序/短尾只读实验](0085-s0a-tail-qualification.md)已完成，后者保留部分 Trailer 的 Unresolved 停点及 F2 反例；[S0-B 固定 EOF/依赖资格](0085-s0b-fixed-eof-qualification.md)也已通过独立验收，保留单 branch/首段、最多一个短尾与全历史成本限制。[S0-C 实际恢复/再次中断闭环](0085-s0c-recovery-implementation.md)现已通过独立验收，证据包含真实短写和外部终止、重试续写/冷开及完整入口成本；[S0-D 薄 CLI rescue/显式维护接线](0085-s0d-cli-rescue-implementation.md)也已通过独立验收，明确 check/apply、库内关闭与纯报告，输出中断后可直接严格续写；扩大生命周期范围和平台保证仍未交付。下表为各片的共同验收责任：

| 最小实验 | 必须得到的观察 / 停点 |
|---|---|
| 写序与短尾分类 | 记录真实 Append/Builder/CreateNew 写范围和 flush；逐前缀截取。缺 Fence 但本体完整须先验 CRC；坏 Trailer/CRC/Fence 前缀、非法部分 HeadLen 拒绝且源字节不变 |
| 固定 EOF 与依赖闭包 | 在物理拟删范围放完整可读帧，经 ticket、FrameInfo、TailMeta、StateRevision 间接读均不能越界；超界在大 buffer 租用前拒绝。原有共同 validator 可工作，完整 State orphan 也不能留下指入拟删范围的持久边 |
| 单提交恢复及再中断 | 固定已有仓库、一个已绑定 branch，无 tag/轮转/checkpoint/分支生命周期变化；分别走 State/Event 提交，覆盖 Schema → State → Journal → ref 的真实写、flush 与回包边界；恢复动作中再次中止后，重试 → 再提交 → 再冷开 |
| F2 核心假设提前检验 | 从真实写范围构造全长坏 CRC、头尾先落盘的合成图像；分别建模“旧范围受保护”与“共享扇区可伤旧范围”。识别当前协议能否保留所有已确认事实，列出最小候选前提/改动；不能把安全拒绝或回滚已确认帧算最终目标通过 |
| 完整入口成本 | 分开计量检测、候选校验和最终打开；改变历史量、live 对象/链规模、最大帧及 orphan 数量，报告读取量、峰值内存、耗时和限额覆盖缺口 |
| 可信部署的只读 Normal 对照 | 按 §5.4 迁移必要的按需结构检查，对照 Audit 测 Open + 首次实际读取；证明消除无关历史工作，暂不改变可写确认或授予 Clean |

storage 交付物理候选检查及窄只读/动作接缝，DG 提取共同校验并贯通闭环。不先建设完整平台 API，不发布只覆盖固定单 branch 的宽泛恢复产品。上述反例先可用合成夹具检查逻辑，随后以子进程中止验证真实调用路径；两者证据分开。

验收：只可能恢复协议允许的旧提交或新提交；已确认提交不丢；无混合图；完整事实/地址不变；恢复中再次中止可重试；在声明检查范围内发现已发布依赖损坏则明确拒绝；失败预检不改变源字节。可信 Normal 的按需检查范围与 Audit 的全量范围分别验收，不再用一个 Open 成功条件混淆两者。

### S1：闭合已有操作的全部正常中断

覆盖 header-only/0–3B 创建前缀、Create/Fork 的 allocation/Init/BindName、Archive 的 Close/Archive、tag、Move、State/Event/ref segment rotation、catalog checkpoint、完整 orphan。每个窗口测试恢复本身再次中止，至少包含“Strict Open 成功但业务不可用”。§4.3 的每类依赖均须有检查或可信写序证明；用 snapshot 前已 Archive 的旧 ref、历史 target 及 State orphan 验证相应责任，不要求可信路径每次全读。保守/Audit 路径及实际访问发现的完整无效依赖不能因当前 head 健康而放行。

空初始化残片先做一次补齐/清理对比：枚举目录、0–4B header、部分/完整 locator temp，连同每个恢复动作后的中断；比较资格分支和可重新识别的状态，再只选一种。Archive 必测补写前触发 checkpoint，禁止把投影后的 catalog 提前持久化。DG 当前没有 Archive 入口，可据此安排施工先后，但联合 S1 门仍包含上游该操作，不能由 reviewer 擅自缩小交付范围。

S1 是现有仓库 F1 恢复能力的联合门。S0 的 F2 模型若要求协议调整，先修订该依赖再展开 S1，不为保留一次性原型接口而拖到最终平台验收。

### S2：断电平台门与交付

对选定 OS/文件系统及所依赖的存储保证建立实际证据，不预设只需几个 platform helper。覆盖数据落盘但名字未持久、rename、next/locator 不同步、truncate 未持久、非前缀落盘，以及“第一次进程中止，恢复期间再断电”。按用户新增约束优先考察 OpenZFS + 4 SSD 的定制部署，OS、版本、拓扑和设备合同仍须选择；不再默认把当前 Windows 开发机当首个生产支持平台。

S2 兑现 S0 提前提出的可行性假设：确认 flush、旧前缀保护及目录发布前提；若需写入布局或完成证据，单独评审最小改动。恢复 Close → Archive 的重新确认屏障也须在此验证。若合法断电仍需人工，或只能牺牲已确认提交才能恢复，明确为最终目标阻塞项，不能以“安全停机”记作自动恢复完成。

storage Release build/test 与 isolated PackageReference smoke；DG solution build、相关 persistence/storage tests、真实候选包冷启动/恢复/续写 probe。两仓版本独立，包内容变更使用新版本，不能覆盖试用 feed。发布与真实数据迁移另按授权执行。

### 共同反例集

1. 末帧 payload 中含有效形状 Fence/Trailer，拒绝误定位内嵌帧。
2. F1 下完整坏 CRC、未知版本、完整语义非法帧不能降为 IncompleteSuffix；F2 的非前缀落盘资格另按模型验证，不能直接沿用此分类。
3. 保守/Audit 路径发现 tag 或非当前 branch 指入拟舍弃范围，整个计划拒绝；可信路径须证明这种形态不由受控 writer/故障模型产生，任何已观测到的矛盾仍拒绝。
4. 回包前进程中止，新提交完整存在；不得按“调用者没看到成功”回退。
5. 恢复截断完成但新开/日志输出前中止，再启动保持一致。
6. 第二次恢复与第一次恢复后再写均不复用仍被保留事实引用的身份。
7. 正常 Open 的 I/O 不随无关历史增长；DG 已有扫描单独计量，不掩盖。
8. allocation 有效但对象首段 0B/缺 locator/Init 半帧，按真实阶段处理，恢复后仍未绑定，新建同名获得不同 RefId；不存在完整事实损失。
9. Archive 完整、半帧、尚未开始、名字后来重绑各自重试，不重复归档或关闭新 ref。
10. snapshot boundary 不被裁掉；恢复补 Archive 触发 checkpoint 的每个中断仍可恢复。
11. 缺 Fence 时已有 CRC/Trailer 或 Fence 前缀错误，拒绝而非截尾；候选 EOF 外的完整本体也不能被随机读或 FrameInfo 读穿。
12. Close 完整但尚未 flush；恢复必须先确认它，再发布 Archive。第二次故障不能制造反向依赖持久顺序。

以上 byte-cut 合成见证、写调用中断注入、真实进程 kill、平台断电模型是不同证据。测试须记录来源，不能用关闭资源时的 Dispose/Flush 代替突然退出。进程故障测试采用子进程与外部终止；真实存档先备份，在独立副本验证。

### 阻塞项归属

| 尚未取得的证据 | 阻塞什么 | 不阻塞什么 |
|---|---|---|
| 短尾分类、固定 EOF、S0 持久边覆盖的可执行见证 | S0 自动改盘及验收 | S0 只读检测和接缝实验 |
| 全部持久边、生命周期和恢复再中断闭包；stub 策略选择 | S1 联合验收 | 受限 S0 |
| 按需结构检查、可信 Clean 或替代的活动前沿确认；二次故障 Fork 见证 | 快速可写 Normal 及可信局部恢复 | 只读 Normal/Audit 成本实验；原保守可写路径 |
| 非前缀落盘、旧前缀保护、目录发布和恢复自身屏障 | F2 保证及相关 API/格式冻结；S1 前需先完成核心模型判断 | 不宣称断电支持的 F1 实验 |
| 端到端成本与所选规模/恢复时长 | 无人值守可用性承诺、默认预算 | 小规模机制正确性实验 |

上述待证项已有具体实验，不再靠增加评审轮次“证明”实现正确。

## 7. 待评审与产品选择

已确认：三条检查路径、库内 Audit API 与维护 CLI、受控存储栈承载高级别数据安全、按需校验及可靠 Clean 证据的方向。以下只保留尚未选定的产品问题，不重复请求用户批准上述方向。

- 默认 Strict + 显式恢复，还是完成验收后让可写 Open 默认恢复？建议前者，使权限/语义清晰，宿主仍只调用一次。
- 首次对外称“自动恢复”是否允许明确限定 F1，还是待 S2 后一并开放？建议先内部完成 S0/S1，最终目标以选定平台 S2 为门。
- 对外保证前须选择平台、支持规模与可接受恢复时长；S0 可先记录实验环境并测量，不把实验机器默认为支持矩阵。这些产品选择不能由 reviewer 票数决定。
- OpenZFS + 4 SSD 是用户给出的候选，具体 OS/版本/拓扑及支持保证待实证；Clean/活动前沿两种内部资格方案按 §5 实验后收口，暂未选择存储格式或检查 API。

当前技术建议：现格式 roll-forward Archive 与 locator，恢复遵守原发布依赖顺序；不合成孤儿 Init/BindName。空容器残片策略由 S1 小实验选择。耐久诊断回执、通用恢复 WAL 与兼容层继续延后；用于快路径资格的 Clean 是不同问题，已因真实需求重新纳入有限候选。不能把它当提交水位，或沿用首轮收敛作为无需新证据的依据。

## 8. 辩证评审记录

第二次审阅使用三位新的 reviewer，首轮不读旧评审或旧对话结论；独立源码核对后交叉质询。两轮后，剩余问题需要实验，未为形成一致意见再开第三轮。主线程核验了关键符号、既有测试及平台原始资料，未运行新恢复测试；详细证据与改判见[本次记录](0085-joint-crash-recovery-preimplementation-review.md)。

用户随后提出受控部署与检查分级，三角色作了两轮限定续审：共同修订“可信模型内仍必须每次全扫入边”的主张，保留按需结构检查和 dirty 恢复后的依赖确认。快速 writer 的 Clean/活动前沿方案尚需对照实验，没有按票数冻结。

| 裁决 | 结果 |
|---|---|
| delete | S0 的通用 metadata/control overlay；“normal validator 已覆盖全部依赖”的隐含前提 |
| defer | 耐久回执、通用恢复框架、兼容层；空 stub 两候选待 S1 实验，完成证据待提前的 F2 模型判断 |
| merge | 共用 parser 与校验规则；持久依赖责任由可信协议证明或显式遍历落实，不复制 toolkit 外壳 |
| simplify | 固定 EOF reader、实际 catalog 与预期投影分工、利用紧邻 Bind 判 never-bound；不合成 Init/BindName |
| keep | 已有可验证字节检查、完整事实保全、持续独占、发布前依赖屏障、新资源冷开、端到端计量和恢复再中断验收 |

## 9. 实施定位与证据入口

| 组件 | 现有入口 / 预期最小改动 |
|---|---|
| RBF | [ForwardEnumerator](../../../atelia-storage/src/Rbf/RbfForwardEnumerator.cs)、[ReadFrameInfo](../../../atelia-storage/src/Rbf/Internal/RbfReadImpl.ReadFrameInfo.cs)、[RbfRecovery](../../../atelia-storage/src/Rbf/RbfRecovery.cs)：复用正向解析，支持 raw EOF 检查、候选限长读及离线动作；人工 scanner 不承载自动策略 |
| 写序 | [AppendImpl](../../../atelia-storage/src/Rbf/Internal/RbfAppendImpl.cs)、[FileImpl](../../../atelia-storage/src/Rbf/Internal/RbfFileImpl.cs)、[SinkReservableWriter](../../../atelia-storage/src/Data/SinkReservableWriter.cs)：建立真实中断轨迹，不凭 API 名猜落盘顺序 |
| SegmentStore | [RbfSegmentStore](../../../atelia-storage/src/RbfSegmentStore/RbfSegmentStore.cs)、[SegmentLocator](../../../atelia-storage/src/RbfSegmentStore/SegmentLocator.cs)：识别 next 创建/轮转，保持普通 Strict Open 合同 |
| EventJournal | [Refs](../../../atelia-storage/src/EventJournal/EventJournal.Refs.cs)、[RefMoveStore](../../../atelia-storage/src/EventJournal/RefMoveStore.cs)、[JournalFormat](../../../atelia-storage/src/EventJournal/JournalFormat.cs)：候选 catalog/ref 解释、窄 CompleteArchive、复用 checkpoint 屏障 |
| DG | [HistoryJournal](../../src/DurableGraph.Persistence/HistoryJournal.cs)、[GraphResources](../../src/DurableGraph.Persistence/GraphResources.cs)、[Repository](../../src/DurableGraph.Persistence/Repository.cs)：锁寿命拆开，联合检查/执行，保留正常验证语义 |
| 现成反例 | [CatalogCheckpointTests](../../../atelia-storage/tests/EventJournal.Tests/CatalogCheckpointTests.cs)、[SegmentLocatorTests](../../../atelia-storage/tests/RbfSegmentStore.Tests/SegmentLocatorTests.cs)、[PublicationSubstrateTests](../../tests/DurableGraph.Persistence.Tests/PublicationSubstrateTests.cs)、[ConsumerRecoveryTests](../../tests/DurableGraph.Persistence.Tests/EventHistoryConsumerRecoveryTests.cs) |
| 孤儿审计 | [EventJournalAudit](../../../atelia-storage/src/EventJournal/EventJournalAudit.cs)：`InventoryStores/AddStore/ValidateRefs` 的库存、locator 与 legalEmpty 规则；原 toolkit 委托该核心 |
| 现行边界 | [storage 合同](../../../atelia-storage/docs/EventJournal/bounded-online-io-contracts.md)、[DG 活动状态](../../src/PROJECT-STATE.md)、[DG 目标](../DurableGraph-target-design-v0.md)：当前实现不因本提案自动升级保证 |

## 10. 压缩后的推进顺序与调度停点

**最新续工建议：先读[统一系统根对照](0085-unified-root-comparison.md)，验证提交权威是否应调整，再决定原 S1 轮转扩展的路线。** 以下表格保留整体能力门及原路线顺序，不是要求越过这项对照直接实施下一项。S0 已验收实现继续作为基线；候选未变成新格式授权。

续工读取顺序：`src/PROJECT-STATE.md` → 本文 §1、§4.4、§5–§7 与本节 → 本片所需源码。旧评审仅按具体争议追溯，不再从头开展全方案辩论。工作树中的 storage breaking 适配是用户已有工作，须保留；两仓实际 revision、包模式和当前差异在实施开头复核。

后续以四条验收线共同选方案：应用一次启动调用即可获得可用仓库或明确报告；支持模型内已确认事实不丢且不静默回退；Normal 的成本不随无关历史整体增长并计入首次使用；维护与启动共用核心、持久状态只为实测需要增加。目标不是覆盖任意损坏或所有平台。高级别数据安全的承诺绑定经验证的部署合同。

| 次序 | 最小交付 | 观察标准 / 停点 |
|---|---|---|
| 0. 收口实验合同 | 从 §5.1 列出首个受控存储候选的必要保证、writer 范围、明确排除项；整理现有写序与检查调用点 | 只冻结首片范围，不冻结未来全部 API/格式。实际 ZFS 环境未就绪不阻塞库内只读实验，但不能签发平台保证 |
| 1. 共同校验 + Audit API + CLI audit | 将必要规则放进生产库，DG 组合仓库级 API，新工具程序调用该 API；先保持现有 Normal 行为 | 正常 Open 失败的坏尾/坏依赖输入仍能得到审计报告；API 与 CLI 结果一致；取消/I/O 未完成不误报 Healthy；源数据不变 |
| 2. 只读 Normal 成本原型 | 同一组结构规则改为按需驱动；移出候选只读路径的无关历史 sweep 和 `_frames` 常驻，Audit 继续全枚举 | 同时测 Open、首次只读查询/图读取、内存与字节数；只增加无关历史不增加全库工作；错误关系在访问时拒绝。Checkout 属可写路径，不用于冒充只读测量；具体续工边界见 §10.1 |
| 3. S0 最小恢复闭环 | 真实写序分类、固定 EOF reader、单 branch 恢复→续写→冷开；CLI rescue 与启动入口调用同一核心 | 通过 §6 S0 的资格门和再次中断见证；仅处理该片支持的中断，不先发布完整恢复承诺 |
| 4. 快速 writer 与 S1 | 按测量选择 Clean 或活动前沿确认，补齐轮转、创建/归档、tag、checkpoint 等已有操作 | 持久确认与按需关系检查都接通；二次故障 Fork、假 Clean、恢复中断及全部生命周期窗口通过。无替代证明前保守 writer 保留 |
| 5. S2 与交付 | 在选定 OS/OpenZFS/SSD 配置验证屏障、元数据和断电假设，完成独立包消费与端到端使用证据 | 只为已验证组合与规模声明高级别保证；模型注入、进程 kill 与平台断电证据分别记录；发布和真实数据迁移另按会话授权 |

首个实现片的具体问题是：**能否把当前分散的结构检查变成一套可由库 API 和 CLI 调用、且不依赖成功 Normal Open 的只读 Audit 核心？** 此片不同时开放快速 writer、Clean 文件、Schema 索引或通用修复计划。新公开 API 的签名、报告最小字段与上游共用接缝在这片开始时先核对再实现；涉及两仓的 .NET 构建/测试串行，包边界按依赖指南验证。

调度建议：主线程负责共同合同与整体验收；明确设计后派发有写入边界的实现任务，另由只读 reviewer 对照源码和反例验收。平台资料调查可并行，但共享库的语义改动与 CLI 接线按依赖先后整合。一个片的验收证据决定下一个片，不以子代理完成报告代替独立验收。

用户只需裁决产品边界，不必逐个选择底层 API。若出现“正常打开必须全库扫描”“简单恢复需要第二套事务权威”“所选部署无法保住已确认前缀”等具体冲突，应提交：最小反例、卡住的承诺、两种可行调整及各自的易用性/持久性/性能/复杂度代价、推荐项。可以建议收窄支持平台、首发操作范围、规模/恢复时长或推迟某功能；不能静默降低已确认数据的保全承诺。

第 0/1 片与第 2 片内部实验已完成；实现与证据分别维护于[首片记录](0085-audit-implementation.md)和[只读成本记录](0085-normal-readonly-experiment.md)。[S0-A 写序/短尾实验](0085-s0a-tail-qualification.md)也已通过独立验收；[S0-B 固定 EOF/依赖资格](0085-s0b-fixed-eof-qualification.md)已完成并独立验收；[S0-C](0085-s0c-recovery-implementation.md)受限内部真实恢复与再中断闭环也已验收，[S0-D 薄 CLI rescue/显式维护调用](0085-s0d-cli-rescue-implementation.md)亦已验收，沿用同一受限核心，库关闭资源后才返回报告；仍不冻结宽泛恢复 API，下一步建议先做统一系统根对照，之后据证据推进 S1 与平台门。公开只读切换还需处理已测出的长祖先重复验证成本。三路径及库内 Audit/CLI 分工无需重新确认。

### 10.1 第 2 片的建议实施边界与验收

问题：**保持目标图、所需祖先及 Schema/catalog 不变，仅增加无关历史时，能否让只读打开及首次实际读取不再承担全库工作，同时保持被访问依赖的结构检查？** 内部候选及实测已完成，结果见[第 2 片记录](0085-normal-readonly-experiment.md)：无关历史工作已消除，但长祖先与 Schema 成本仍在，暂不切换公开默认。以下保留该实验的范围与验收合同；现有公开 Open、可写确认/发布路径及 Audit 行为保持，不顺带开放快速 writer。

先收口一张“入口 → 需要检查的依赖 → 拒绝时机 → 反例”表，再改代码。第 0/1 片抽取的 `RepositoryGraphValidator.FindBaseline` 仍依赖预建的最近 State 字典；共同函数存在不等于按需祖先闭包已实现。需要覆盖 Head/tag 句柄签发、历史导航/枚举、State/Event/Checkpoint/Pair 实际读取，以及间接进入这些路径的调用；只读候选不能复用可写操作来取得资格。按需重复检查的成本也计入实验，不先加长寿命验证缓存。

第一波保留现行打开作基线，加入最小计量与反例；随后在内部只读入口接通按需验证，避开全历史 physical sweep、逐历史图检查及 `_frames` 常驻。Schema 全量目录重建和必要 catalog 检查仍保留。若实际框架需要调用这些共同函数的额外入口，按源码补入表中，不能把上述列表当成穷尽证明。

实验分开记录 Open、首次 `GetHead`/`ResolveTag`、首次 `ReadState`/`ReadEvent`/`ReadCheckpoint` 及合计成本。当前 `Checkout` 调用 `RequireWriter`，如测量它，只能列为未优化的可写对照。至少分开三组变量：无关历史、所需祖先/对象链、Schema/catalog 规模。记录读取请求、底层实际返回字节、预取、耗时、分配及驻留/峰值；指标未接通就注明缺口。已有 RBF `ReadObserver` 的 raw 回调记录的是请求长度，不能直接改名为实际返回字节；底层返回字节也不等于设备物理读取量。新进程只消除了进程内缓存，不证明 OS 页缓存已冷。

反例至少包括：访问 CRC 正确但 Journal/Revision Parent 错配的记录、缺失依赖、空 branch head、tag 与历史导航入口、跨打开地址、枚举期间 Dispose，以及前片已暴露的异常分类/未完成状态。无关历史异常由完整 Audit 检出；若候选推迟发现，须按入口合同解释。Audit 用作独立全量对照，不能在候选 Open 内先跑一遍 Audit。共享函数复用时仍要保证错误来源可定位，且实际访问的错误不会被“来源可信”掩盖。

本片不建设 Clean、截断/恢复、持久索引、新的持久格式或通用检查等级。主线程负责合同与整合，可把计量/明确实现分派给有边界的子任务；校验迁移完成后进行一次独立反例验收。若依赖闭包无法局部建立或测量发现总成本未改善，先报告具体反例/数据及替代方案，不以降低检查强度或把全部工作挪到首次使用通过验收。
