# DB-085 第二次独立实施前审阅

> 2026-09-29；结论：建议进入受限 S0 机制实验，不批准完整恢复 API、最终舍弃规则或持久格式。
> 当前方案与实验门只维护于[联合方案](0085-joint-crash-recovery-design.md)；本文保存证据、反例及改判。
> 本轮仅修改文档，未实施产品代码、运行新恢复实验、操作真实存档、提交或发布。
> 后续用户允许定制存储栈，提出 OpenZFS + 4 SSD 与检查分级。以下 N1–N6 保留本次独立审阅的证据；“每次必须全扫入边”的结论已按[方案 §5](0085-joint-crash-recovery-design.md#5-性能与范围)收窄，见文末续审。

## 方法与证据边界

使用 `dialectical-simplification`：三位新的 gpt-6-astra / max reviewer 分别担任需求怀疑者、最小架构师、语义捍卫者。
为满足独立性要求，使用不继承旧对话的相同需求包；三位均确认首轮未读[旧评审](0085-joint-crash-recovery-review.md)。
先独立阅读 PROJECT-STATE、方案与两仓源码，再交叉质询。主线程独立核对关键源码，不按票数裁决；收到三份首轮结论后才读取旧记录以维护历史。
方案里的首轮结论仅作为被审主张，不作为证据。两轮后剩余差异需要可执行实验，没有为制造一致意见追加第三轮。

源码基线未变：DG `c900edd5b3a921b7bf46df2ac0482dc39597e606` 加原有适配 working tree；storage `bf7d68ab3f3fbad18b87549dbf6ce2aae499c24a`，干净。
这些结论针对候选源码，不能用来描述 `eng/StorageDependency.props` 所固定的旧公开包。当前任务、真实消费者和未决产品选择见方案需求账本。

证据分级：源码和测试内容已核对；本文提到的故障轨迹是反例推导，未声称已经实测。构造损坏夹具用于检查拒绝边界，不冒称它们都能由 F1 正常 writer 产生。

## N1：短尾分类与候选读取都尚有真实接缝

- **位置 / 主张：**方案 §4.1；“复用正向 parser + 限长”尚不足以直接实施。
- **来源 / 消费者：**真实 Append/Builder 写序、F1 前缀假设与用户的数据保全目标；消费者是 DG 恢复协调器。
- **证据：**[ReadFrameInfoAt](../../../atelia-storage/src/Rbf/Internal/RbfReadImpl.ReadFrameInfo.cs) 在 Fence end 超 EOF 时就返回，尚未验证已存在的 Trailer/CRC；[RbfFileImpl](../../../atelia-storage/src/Rbf/Internal/RbfFileImpl.cs) 的随机读直接调用 reader。[ReadFrameCore](../../../atelia-storage/src/Rbf/Internal/RbfReadImpl.ReadFrame.cs) 只读 ticket bytes；[RandomAccessReader](../../../atelia-storage/src/Rbf/ReadCache/RandomAccessReader.cs) 没有候选 EOF；FrameInfo 也自带该 reader。
- **反例一：**FrameBytes 全部存在，只缺最后 Fence；CRC 已坏或剩余 Fence 字节错误。若把 beyond-EOF 一律当短写，会吞掉 F1 中已可识别的矛盾。
- **反例二：**候选扫描排除了缺 Fence 的末次 append，随机读却仍能读到物理本体并通过 CRC；预检放行，随后截断删掉该 target。只改 TailOffset/ScanForward 无法阻止它。
- **最小替代 / 成本：**补窄的写序分类表，检查已有可验证内容；固定 EOF 落到共同读取边界，约束 FrameInfo、缓存及预取，超界在大 buffer 租用前拒绝。帧资格包含正确 Fence 与连续物理边界，不能仅凭任意 ticket 的 CRC。S0 可先关闭候选缓存，不建通用文件系统 overlay。
- **裁决 / 置信度：**`simplify / keep`，源码证据高；阻塞 S0 自动改盘的资格验收，不阻塞先研究写序与只读接缝。新增机制仍待实验。

## N2：保留引用闭包不能由 normal Open 代证

- **位置 / 主张：**方案 §3.2、§4.3；原文高估了已有 validator 的覆盖。
- **来源 / 消费者：**当前 tags、refs、历史地址与 DG 对 orphan Journal 的合同；EJ 的 `CommitToRef` CAS 失败确实返回 `OrphanEventAddress`，所以 orphan 并非都不可观察。
- **证据：**[Repository.ValidateHistory/ValidateGraph](../../src/DurableGraph.Persistence/Repository.cs) 验所有物理 Journal 和 active heads；[GraphResources](../../src/DurableGraph.Persistence/GraphResources.cs) 对未被 Journal 引用的 State 只有物理检查。[tag replay / LoadRefState](../../../atelia-storage/src/EventJournal/EventJournal.Refs.cs) 不遍历所有 tag targets 或历史 moves；[tag 测试](../../tests/DurableGraph.Persistence.Tests/RepositoryTagStorageTests.cs) 明确记录惰性 target 检查。[toolkit](../../../atelia-storage/tools/EventJournal.Toolkit/JournalToolkit.cs) 已有更完整的事实规则。
- **反例：**完整 tag、archived ref 的历史 target，或完整 State orphan 指入拟丢弃范围；当前 head 和其图仍健康。仅复用 normal Open 可漏查，不能据此授权截断。
- **最小替代 / 成本：**按方案覆盖表枚举所有可能产生持久入边的记录，复用 codec、Storage reader、Schema replay 和必要 toolkit 规则。无反向索引时不能承诺只读几个 heads；可以去掉工具外壳，不能去掉闭包。
- **边界：**本片保留完整事实及其持久依赖，不新增孤儿应用 body/factory/Upgrade 或全图物化审计，也不宣布未来永远不得 GC。无法解析足够的依赖证据时拒绝修改。
- **改判：**架构师撤回“局部检查能省去审计规模读取”的暗示；怀疑者保留 orphan 分类，但撤回任何据此漏查 orphan 入边的空间；捍卫者将“全部孤儿语义有效”收窄为所需持久依赖。
- **裁决 / 置信度：**`merge / keep`，高。S0 单提交自然会留下完整 State orphan，须覆盖；tag 和生命周期全范围是 S1 门。

## N3：恢复也必须遵守发布因果顺序

- **位置 / 主张：**方案 §4.2 Archive、§4.3 执行阶段；“各动作幂等”和最终冷开不足以证明再中断安全。
- **来源 / 消费者：**现有 Close → Archive、op-log → snapshot 协议；消费者是恢复后的正常 EventJournal/DG。
- **证据：**[RefMoveStore.AppendPreparedMove](../../../atelia-storage/src/EventJournal/RefMoveStore.cs) 的 Append 与 flush 分开；[ArchiveRef / CheckpointCatalog](../../../atelia-storage/src/EventJournal/EventJournal.Refs.cs) 原本先确认 Close，checkpoint 的 boundary 来自实际日志，而内容直接取当前 `_branches/_tags`。[HistoryJournal.ConfirmDurable](../../src/DurableGraph.Persistence/HistoryJournal.cs) 也明确按依赖顺序确认。
- **反例一：**进程中止留下完整、尚未 flush 的 Close；恢复直接 append+flush Archive；最终重开前再断电，可能保留 Archive 而丢 Close。这是 F1 后再遇 F2 的窗口，不是纯重复 kill 已证明会发生的现象。
- **反例二：**候选投影先解绑，再借这份 catalog 做 checkpoint；snapshot 已不含名字，boundary 却在 Archive 之前。后续完整 Archive replay 会因缺原 binding 拒绝。若提前崩溃，snapshot 本身也已领先事实。
- **最小替代 / 成本：**先修相应尾部并 flush，重新确认 Close，再用实际追加前 catalog 做容量预检/必要 checkpoint，随后 Archive append+flush，最后安装解绑。候选结果只用于预检，不编码虚拟控制帧。增加必要局部屏障，无需 recovery WAL。
- **裁决 / 置信度：**`keep / simplify`，F1 catalog 反例高；F2 残留形态仍需平台证据。S0 无 Archive，不阻塞进入；S1 必须具有正确执行顺序并测试强制 checkpoint，S2 检验再次断电。

## N4：F2 可行性前移，但不预选新的持久水位

- **位置 / 主张：**方案 §3.1、S0/S2；首轮“新完成证据以后再说”不能被当成 F1 架构已可直接延伸的证明。
- **来源 / 消费者：**用户的普通断电无人值守目标；session 宿主。拒绝危险修改只是安全性，还未兑现自动恢复可用性。
- **证据：**[AppendImpl](../../../atelia-storage/src/Rbf/Internal/RbfAppendImpl.cs) 多次普通写，[Builder](../../../atelia-storage/src/Rbf/Internal/RbfFileImpl.cs) 也将写出与 durable flush 分开；[SegmentLocator](../../../atelia-storage/src/RbfSegmentStore/SegmentLocator.cs) / [JournalFormat](../../../atelia-storage/src/EventJournal/JournalFormat.cs) 没有已证明的目录持久化合同。微软说明[多扇区 WriteFile 不保证原子性](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-writefile)。
- **反例 / 删除失败：**新帧长度及尾部落盘而 payload 部分未落盘，留下全长坏 CRC；或者 next 数据与发布已落盘、locator rename 丢失。当前 F1 路径拒绝它们，不构成完整断电恢复。
- **最小替代 / 成本：**S0 即从真实写范围构造非前缀落盘模型。分别检查可靠 flush、旧字节范围保护和目录发布；它们不是一个保证。[SQLite PSOW](https://www.sqlite.org/psow.html) 解释了追加为何可能需要保护共享扇区中的旧数据，但不证明当前平台的能力。
- **改判：**三位接受“新 watermark 不是必然交付物”。可靠 flush、旧前缀保护及串行发布因果成立时，可先研究现有格式如何区分未完成追加；若旧字节已受损，单加完成标记不能恢复它们。再按反例选择部署前提、布局保护或完成证据，不先造全仓水位。
- **裁决 / 置信度：**`keep / defer`。明确的模型缺口，高；实际平台形态和最小解法未定。阻塞 F2 承诺及相关冻结，不阻塞标明 F1 的机制实验。

## N5：空初始化残片仍须实验，不能由工具偏好选定

- **位置 / 主张：**方案 §4.2、S1；首轮“一律补齐已开始的容器”降为候选。
- **来源 / 消费者：**allocation 占用 RefId 与完整事实保全有真实语义；把无业务帧的残片补成 Store 主要服务物理 sweep/toolkit，未发现业务消费者需要该半成品。
- **证据：**[CreateBranch/ForkBranch](../../../atelia-storage/src/EventJournal/EventJournal.Refs.cs) 在 allocation 后创建 Store，再写 Init/Bind；Bind 必须紧邻 allocation。[SegmentLocator.Publish](../../../atelia-storage/src/RbfSegmentStore/SegmentLocator.cs) 可留下随机名字 temp；[toolkit legalEmpty](../../../atelia-storage/tools/EventJournal.Toolkit/JournalToolkit.cs) 接受对象不存在或单段 header-only。
- **最小候选：**补齐合法 header/locator；或在 never-bound、locator 确实缺失、无完整事实帧且内容严格属于初始化前缀时，清理精确 stub/已验证 temp/空目录，退回 allocation-only。两个候选均保留 allocation 与完整 Init，不合成 Init/BindName。
- **反例 / 成本：**仅删 stub 后再中止会留下 temp 和不完整库存；补齐则增加 header/locator 再写窗口。删除不能以“无有效 locator”替代“locator 不存在”，不能递归处理未知文件，也不能仅凭 `.tmp` 后缀授予删除资格。
- **改判：**架构师撤回“cleanup 已证明更小”；捍卫者未发现严格限定 cleanup 的事实损失反例，但也撤回“必须补齐”的倾向。never-bound 可利用 allocation 的精确后继检查，不能仅凭 snapshot 中没名字。
- **裁决 / 置信度：**`defer / simplify`，保留两种内部候选到 S1 的有界中断实验，届时只选一种，不增加两个公开策略。无需第三轮争论；不阻塞 S0。

## N6：预算须覆盖入口，成本不能只写 O(active refs)

- **位置 / 主张：**方案 §4.1、§5、S0；只有物理 scanner 的 cap 不等于恢复入口有上界。
- **来源 / 消费者：**用户的长期运行可用性与上游性能动机；消费者是持续增长的 DG 仓库。
- **证据：**[HistoryJournal.Open/ConfirmDurable](../../src/DurableGraph.Persistence/HistoryJournal.cs) 多次物理读取，[Repository.ValidateGraph](../../src/DurableGraph.Persistence/Repository.cs) 对历史图遍历 live objects/对象链。State/events 默认 64 GiB、refs 默认 64 MiB，见 [SegmentStoreOptions](../../../atelia-storage/src/RbfSegmentStore/RbfSegmentStoreOptions.cs) 与 [EventJournalOptions](../../../atelia-storage/src/EventJournal/EventJournalOptions.cs)；[ReadPooledFrame](../../../atelia-storage/src/Rbf/Internal/RbfReadImpl.ReadFrame.cs) 仍按整帧租 buffer。
- **反例：**合法大文件只多出几字节短尾，cap 低于前缀验证成本，每次启动都会失败；仅提高 scanner cap，又不能限制候选图验证与最终重开的成本。
- **最小替代 / 成本：**一个共享读取限额及取消贯穿各阶段，计量读取请求/实际读取、CPU 耗时和峰值内存，明列未受预算约束的路径。保留固定 EOF 的轻量能力，不先引入持久优化水位或多维公共预算框架。
- **阶段区别：**预检失败可保证源字节不变；执行开始后取消/I/O/重开超限可能已有合法动作落盘，必须可再检查，不能承诺回滚。读字节上限也不等于堆或时长上限。
- **裁决 / 置信度：**`keep / simplify`，源码成本证据高，真实耗时与支持规模未知。没有运行新恢复 benchmark，不报告虚构收益。

## 收敛与实施边界

本轮保留原有发布权威、单 writer、离线生命周期、全部完整事实保全和新资源冷开；未找到否定受限 S0 实验价值的反例。
删掉 S0 对通用 metadata/control overlay 的需求，把它缩成固定 EOF reader 与共同校验；区分事实 catalog 和结果投影，不增设恢复 WAL。
“所有 State orphan 永久通过当前业务语义审计”和“新水位必需/永远不需”均未成为产品定律。

怀疑者撤回默认先交付 DG、延后 Archive 的建议：用户请求的是联合方案。DG 当前没有 Archive 调用只影响施工排序，S1 联合门仍覆盖该操作。
未决 stub 策略、F2 前提与实际成本均已有决定性实验；继续开会不能替代证据。

最终判断与阻塞矩阵见[方案 §6](0085-joint-crash-recovery-design.md#6-垂直实施与验收门)：**可以进入修订后的 S0；先过只读资格门，再做临时夹具上的自动改盘闭环，暂不冻结或发布广义恢复 API。**
平台、支持规模、可接受恢复时长与默认入口策略仍是[产品选择](0085-joint-crash-recovery-design.md#7-待评审与产品选择)。本轮授权止于审阅及文档修订。

该次文档验证：集成差异与 `git diff --check` 通过；当时三份 DB-085 文档及导航涉及的 75 个本地链接/锚点通过，三份文档无尾随空白。未运行 build/test，不以文档验证替代恢复测试。

## 后续受控部署约束的限定续审

用户允许定制硬件/FS/OS，并给出 ZFS + 4 SSD 候选。三位原 reviewer 基于新前提提出独立意见，再交叉核对；这是需求变化后的续审，不以之前的收敛代替证明。当前建议集中在方案 §5，未实施代码。

- **修订全入边扫描：**若旧范围受保护、屏障可靠，且所有 writer 在引用者开写前完成依赖语义验证与持久确认，可由因果顺序证明可舍弃尾部没有合法保留引用。全库遍历降为 Audit/失去可信资格后的保守检查。依赖保全责任未删除，也未声称硬件能验证业务语义。
- **Normal 的真实接缝：**`HistoryJournal._frames` 的唯一产品消费者为 `ValidateHistory`；按地址导航不依赖它，因此可以实验移除全历史驻留与 sweep。SchemaStore 的目录重建仍需重放，不先增加索引。
- **主线程补充、三方核实：**`Repository.CheckFrame` 只查 owner/kind，历史 CreateBranch/CreateTag/Move 依赖旧的启动验证；轻开须提取并接回所需 Journal/Revision Parent、根和持久依赖检查，不能只跳过 ValidateHistory。
- **恢复后的二次故障：**完整未 flush 的 source ref move 被冷读后 Fork，`ForkBranch` 只显式确认 Event 段，新 allocation 却引用 source move sequence。当前 DG 全文件 ConfirmDurable 提供保护；缩减它之前须证明替代确认合同。这是优化约束，未观察到当前 DG 丢失数据。
- **Clean 分歧的收口：**全体接受其可选且须 durable Dirty-before-write / Clean-after-successful-close。怀疑者倾向先比较可信 Clean，架构师倾向先证明无 marker 的活动前沿确认；均同意只读成本原型先行，Dirty/Unknown writer 在替代合同通过前保留保守路径。这个选择由测量及中断实验决定，不增加一套通用策略框架。

平台依据采用 OpenZFS 官方 CoW 与 checksum/sync 属性文档；它们支持候选存储合同，不构成已完成的部署验证。实际配置、Clean 格式与快速 writer API 均未冻结。
