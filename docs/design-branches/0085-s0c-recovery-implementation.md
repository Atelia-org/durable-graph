# DB-085 S0-C：内部真实恢复与再次中断闭环

> 状态：内部受限闭环已完成并通过独立验收，2026-09-29。依据 [联合方案 §4.3、§6、§10](0085-joint-crash-recovery-design.md)、[S0-A](0085-s0a-tail-qualification.md) 与 [S0-B](0085-s0b-fixed-eof-qualification.md)。保留两仓已有 working tree；不提交、发布或操作真实存档。

## 问题、范围与验收

在同一次仓库独占所有权内，能否把 S0-B 的只读资格转为最小真实动作，并在提交或恢复再次中断后，从盘上重新推导、恢复、续写及冷开？只在新建独立测试/实验夹具改盘。完整事实及地址（含 orphan）不得删除或回退；未收到成功回包不构成删除理由。

范围沿用 S0-B：已初始化、一个已绑定 branch、各 store 只有首段、初始空 catalog snapshot、Create/BindName 与 Init/Advance、State/Event 提交、最多一个支持的短尾。Trailer 1–11B 的 Unresolved 继续拒绝。不做 tag、轮转、checkpoint、分支生命周期、Clean、新格式、持久索引、快速 writer 或通用恢复 API；薄 CLI rescue 后续调用同一核心。

## 共同合同

| 真实提交/恢复步骤 | 已成立的持久事实 | 中断后磁盘形态 | 允许动作 | 再次启动结果 | 拒绝反例 |
|---|---|---|---|---|---|
| Schema append → flush → 交付表示 | 成功屏障之前只有此前确认事实；之后目录批次可供 State 引用 | 无新增、一个短尾或完整额外 Schema | 全量保留依赖资格通过后仅去掉支持短尾；完整批次保留并重确认 | 原已发布 head 可读，表示 ID 不回收 | 完整未知版本/坏 CRC/语义矛盾不能删除 |
| State append → 原 lease flush | Schema 已确认；State flush 成功后 State 完整 | State 短尾或完整 orphan | 仅资格短尾可截；完整 State 含 orphan 保留 | 原 head 或后续完整发布事实；续写不能覆盖 orphan | 完整 State 的 Parent/external/prior/表示悬空 |
| Journal Event append → flush | 被引用 Schema/State 已确认 | Event 短尾或完整 orphan Event | 保留全部完整 Event，校验其全部持久依赖 | 以完整 ref 选择 head，不按调用者回包推断 | 完整 Event 引用不存在 Revision/错误图 Parent |
| ref Advance append → flush → 回包 | 图依赖已确认；成功回包后新提交已确认 | ref 短尾、完整未确认或已确认 Advance | 短尾通过全部历史 ref 资格才可去掉；完整 Advance 必须保留 | 旧提交或完整新提交，无混合图；已回包提交不丢 | 当前 head 健康但历史 target/ExpectedOldTarget 矛盾 |
| 持锁 → 连续检测 → 固定 EOF/全部依赖检查 | 本次锁内证据，尚无动作许可外传 | 源字节保持 | 仅 Qualified 可继续；候选全部关闭，锁继续持有 | 失败不交付 writer，不改变源文件 | Invalid/Unsupported/Unresolved/I/O/取消/额度不足不能通过 |
| 关闭候选/guard → 复核路径/原长度/边界 → SetLength → Flush(true) | 此前完整前缀必须保留 | 原支持短尾或精确完整边界；屏障前持久性尚不确定 | 仅截断本次锁内取得资格的一个后缀；记录动作已尝试/长度变化/屏障状态 | 再启动重新检测；完整边界进入无截断路径 | 使用旧报告、锁间隙、路径/长度变化、任意较早 EOF |
| 全新严格打开并保守重确认 Schema → State → Journal → ref | 成功的依赖屏障逐步建立；读到不等于已持久 | 文件内容保持，可能仅部分屏障完成 | 复用现有保守打开/确认；失败关闭全新资源，继续持锁清理 | 重试重新确认全部所需依赖，不只 flush 被修文件 | 跳过未修文件的确认、复用旧 Store/cache、失败后透明重试提交 |
| 最终验证 → writer/报告交付 | 全部检查与保守确认成功 | 完整新资源；回包前再次 kill 可能丢诊断 | 仅成功后将同一锁所有权交给新 Repository | 重新推导无需上次内存报告；续写→冷开一致 | 动作后取消声称源未改变、提前交付 writer |

锁接缝采用最小内部 borrowed/adopt ownership：普通打开继续自行取得并拥有锁；恢复打开借用持续持有的锁，全部成功后才转移所有权。`Qualify` 保留原只读语义，抽出锁内共享实现；其普通报告不成为可重放动作计划。受控合作 writer/文件系统边界保持，不声明抵御恶意目录替换。

## 证据与计量计划

真实调用点故障注入与子进程外部终止分开记录；图像裁剪只用于拒绝反例/特定短尾，不称 kill。覆盖原提交各层写/flush/回包、恢复动作前后、截断后屏障前、依赖重确认、严格重开和最终交付前。子进程被终止不得执行 Dispose/正常关闭；每个支持窗口须重试→续写→新进程冷开，并对照已确认 head、完整帧字节/地址和图内容。

物理检测、候选构造/依赖检查、动作/重确认、严格打开及入口合计分别计量。保守严格打开与确认本就交错，计量须说明阶段归属并避免重叠相加。保留 S0-B 请求字节预算仅覆盖资格 RBF raw/header、线程分配非峰值、OS/device cache 未冷等限制；恢复后的打开/flush不冒充受该预算约束。保留无动作路径额外资格扫描成本，不在本片做性能专项优化。

F1 进程故障证据与 F2 模型分列；可靠 flush、旧范围保护及目录持久性仍为平台门，不把当前 Windows kill 实验升级为断电保证。

## 实施与验证结果

内部入口 [RepositoryRecovery.Recover](../../src/DurableGraph.Persistence/RepositoryRecovery.cs) 接收路径、模型 registry、资格请求预算和取消信号。报告仅在成功时携带全新 Repository；`QualificationRefused / PrecheckRefused / ActionFailed / StrictOpenFailed / DeliveryFailed` 区分失败阶段，另记录 `ActionAttempted / LengthChanged / BarrierCompleted / SourceMayHaveChanged / Cancelled`。`NoAction` 表示成功且未截断，不表示未执行保守确认。失败不返回部分 writer。

[资格核心](../../src/DurableGraph.Persistence/RepositoryRecoveryQualification.cs)抽出内部借锁调用；普通 `Qualify` 继续自行持锁且只读。HistoryJournal 的普通打开仍拥有自己的锁，恢复分支借用同一锁，最终检查后接管。动作前关闭全部候选、guard 和缓存，检查完整目录清单/长度，并在独占读写句柄上重跑选中文件物理检测，再调用 `SetLength` 和 `Flush(true)`。最终新开之后再次核对清单与预期长度。源目录与路径的稳定性仍依赖合作 writer/受控文件系统，不是对恶意进程的安全隔离。

保守重确认复用 SchemaStore、GraphResources 和 HistoryJournal：Schema → State → Event → ref 对象 → ref-op-log。新增内部 callback 仅用于精确观察屏障与计量，普通空 callback 路径沿用原动作；保留 HistoryJournal 无参方法以兼容既有内部方法组调用。`BeforeJournalConfirmation` 位于 Schema/State 确认之后，不能误解成所有确认之前。

上游 [RbfWriteInstrumentation](../../../atelia-storage/src/Rbf/Internal/RbfWriteInstrumentation.cs) 以 AsyncLocal 隔离内部实验 hooks，覆盖普通 Append、Builder、Header 实际写和 DurableFlush；真实短写回调返回后必抛 IOException。路径由每个活句柄的一条弱关联提供，不保活句柄；未启用 hook 时转发原 I/O，仍有弱路径登记的分配成本。本片成本包含该实现，不声明生产零开销。

[RecoveryCrashProbe](../../experiments/RecoveryCrashProbe/README.md) 是内部可执行宿主，runner 只新建独立 C: 夹具；通过 PID/检查点握手外部终止真实 writer。两种 Schema、循环、别名、文本和值构成图见证；第一次 kill 后、恢复前记录全部连续完整前缀的 hash、长度和地址，包含刚完成的 orphan/未回包事实。完整新 ref 的写后、flush 后及发布回包边界须恢复新 head；随后续写，再由独立进程严格只读冷开。

起点 diff/untracked SHA256 保存在 `.artifacts/db085-s0c/`。全部 .NET 串行、Release、任务专用 `C:/Users/gdtut/AppData/Local/Temp/db085-s0c-20260929` TEMP/TMP、显式 storage 源码与 `ShouldUnsetParentConfigurationAndPlatform=false`。

### 正确性证据

| 证据 | 已验证内容 |
|---|---|
| [RBF instrumentation 测试](../../../atelia-storage/tests/Rbf.Tests/Internal/RbfWriteInstrumentationTests.cs)，8 实例 | 普通 Append/Builder 的真实短写、写序、部分写必失败、无效长度首写前拒绝、屏障前后异常、AsyncLocal 隔离/reset |
| [恢复测试](../../tests/DurableGraph.Persistence.Tests/DB085S0CRecoveryTests.cs)与[成本测试](../../tests/DurableGraph.Persistence.Tests/DB085S0CCostTests.cs)，共 45 实例 | 四类短尾；部分 payload/Fence 0/1/3B；无动作完整 orphan/未回包事实；资格拒绝；锁连续和候选全部关闭；动作前源变化；动作/打开/交付异常及取消；逐文件 flush 失败后的重新确认和续写/冷开 |
| [外部进程宿主](../../experiments/RecoveryCrashProbe/README.md)，138 案例 | 42 个真实 State/Event 提交中断，84 个有短尾恢复再中断，12 个无动作路径再中断；共 138 次首次 kill、96 次恢复 kill，全部重试→续写→新进程严格冷开 |
| 独立验收的工件复核 | 对照全部 READY 的实际 PID/检查点；92 次部分写均为实写 2B 且原请求更大；重算 690 个完整 RBF 前缀 hash/地址无差异；全部成功恢复均按 Schema→State→Event→ref→ref-op-log 重确认 |

新测试不会把动作后取消报告成源字节未变；资格阶段的 Invalid、Unsupported、Unresolved、I/O、预算和取消均无截断。初轮夹具失败来自路径规范化/对独占资源的外部字节读取；修正夹具的生命周期后复跑，未放宽产品资格或打开检查。实际 `SetLength` 系统调用抛错尚无独立设备故障注入；源码在调用前置 `ActionAttempted`，保守表示效果可能已发生，不能用返回失败推断源未改变。

外部矩阵来源 `.artifacts/db085-s0c/host-evidence.json` / `host-matrix.log`；原始 `child-*.jsonl` 保留于任务临时目录 `durable-s0c-kill-534aeeaef5c54bcfb7e2ec2e7b7741be`。第一次 kill 后记录的新完整 orphan 和完整 ref 亦参与逐字节前缀对照；没有以调用者是否收到回包选择删除事实。进程冷开未清除 OS/device page cache。

### 完整成本

最终隔离运行 [成本测试](../../tests/DurableGraph.Persistence.Tests/DB085S0CCostTests.cs)：七格，每格新建四份夹具，第一次预热，后三次观测，各字段独立取中位数。Audit 在计量外作独立对照；成功报告后的 writer Dispose/字节核对/第二次 Audit 不算入口成本。资格 cache Off/State cache 0；最终严格打开沿普通默认缓存行为。夹具刚写入，OS/device cache 未冷；当前线程托管分配不是峰值或驻留内存。

| 夹具 | 物理检测请求 B | 构造请求 B | 依赖请求 B | 动作复检请求 B | 严格打开请求 B | 合计 raw 请求 / 返回 B | raw 调用 | 预取多读返回 B | 线程分配 B |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 基线 | 664 | 264 | 1,440 | 0 | 65,612 | 67,980 / 4,752 | 146 | 2,148 | 2,109,168 |
| 基线 + 2B 短尾 | 666 | 264 | 1,440 | 50 | 65,612 | 68,032 / 4,804 | 154 | 2,148 | 2,174,176 |
| +8 次提交 | 2,872 | 872 | 14,624 | 0 | 65,612 | 83,980 / 29,648 | 874 | 11,044 | 2,413,416 |
| +32 次提交 | 9,496 | 2,696 | 127,904 | 0 | 65,612 | 205,708 / 178,064 | 6,130 | 37,732 | 5,160,728 |
| 32 live 节点 | 976 | 576 | 48,080 | 0 | 65,612 | 115,244 / 52,640 | 394 | 2,772 | 3,041,488 |
| 32 完整 orphan | 1,944 | 2,440 | 6,560 | 0 | 65,612 | 76,556 / 15,888 | 754 | 4,708 | 2,308,176 |
| 约 1MiB opaque orphan | 1,049,280 | 1,048,908 | 4,195,904 | 0 | 2,166,812 | 8,460,904 / 8,397,756 | 185 | 10,376 | 7,359,112 |

| 夹具 | 物理 ms | 构造 ms | 依赖 ms | 动作/复检 ms | 严格打开 ms | 其中确认屏障 ms | 入口合计 ms |
|---|---:|---:|---:|---:|---:|---:|---:|
| 基线 | 0.858 | 0.372 | 9.424 | 2.559 | 11.119 | 1.127 | 36.892 |
| 2B 短尾 | 0.900 | 0.396 | 10.472 | 3.825 | 10.613 | 1.084 | 38.270 |
| +8 次提交 | 1.464 | 0.665 | 14.582 | 2.858 | 9.852 | 1.009 | 41.782 |
| +32 次提交 | 2.985 | 1.430 | 51.495 | 2.778 | 11.355 | 1.146 | 82.802 |
| 32 live 节点 | 0.840 | 0.387 | 14.849 | 2.828 | 10.092 | 1.142 | 40.684 |
| 32 orphan | 1.538 | 1.275 | 11.233 | 2.614 | 8.979 | 0.942 | 37.652 |
| 约 1MiB orphan | 1.437 | 0.887 | 12.102 | 2.890 | 10.818 | 0.989 | 41.254 |

确认屏障是 StrictOpen 内嵌子集，不能再次相加；这批样本确认阶段没有 RBF 读取。动作阶段包括无动作路径的清单/长度检查，所以无截断也有非零耗时。入口合计还包含取得锁、两次清单采集、资格资源关闭、最后交付检查等工作；独立中位数不必相加等于总中位数。

资格三个阶段的请求量与 S0-B 七格逐一相同；新增动作复检和最终严格打开没有隐藏在首次使用中。基线从 S0-B 的 2,368B 请求/返回增加到 67,980B 请求、4,752B 返回；较大的请求数主要体现普通 reader 的预取请求在 EOF 得到短返回，不能当作设备流量。1MiB orphan 从约 6.29MB 请求增至约 8.46MB；历史 32 的依赖重复检查依然占主导。S0-B 基线约 18.37ms，本轮完整入口约 36.89ms；两轮时序不同，只作观测对照，不作稳定倍率保证。

`cost-tests.log` / `cost-tests/*.trx` 保存 21 个实际样本，`cost-samples.json` / `cost-medians.csv` 为派生数据，另有全部 logical 请求/返回及阶段分配。资格请求额度不覆盖动作复检、最后 Open、目录/locator/format/snapshot metadata、CPU、解压或分配；没有设备 I/O、精确峰值内存或总资源有界保证。无动作仍额外完整资格扫描；当前实现证明保守闭环，尚未优化大规模恢复成本。

### 验证执行

- 两仓 HEAD 保持 DG `c900edd5b3a921b7bf46df2ac0482dc39597e606`、storage `bf7d68ab3f3fbad18b87549dbf6ce2aae499c24a`。起点已有 untracked 文件全部保留；其中既有实现只改动本片提取共享核心的 RepositoryRecoveryQualification，联合方案文档更新当前状态；既有产品适配保留。
- storage Release solution build 与 **1120/1120** 测试通过；DG Release solution build 与 **3129/3129** 测试通过（Persistence 1112、Serialization 163、Storage 202、主测试 1652）。本片新增 RBF 8 项、DG 45 项。最终 DG/宿主 build 0 warning/0 error；重编 RBF 保持 24 个既有 XML 注释警告。
- 构建 `-c Release -m:1 -nr:false`；DG/宿主显式 `-p:UseStorageSources=true -p:StorageSourceRoot=E:/repos/Atelia-org/atelia-storage -p:ShouldUnsetParentConfigurationAndPlatform=false`。test 使用匹配构建的 `--no-build --no-restore`。上游、DG 测试和宿主 RBF DLL 的 SHA256 同为 `4CC53E0070CD6EC831A4370085124554CBB4BADA1158E46206B8ECCF874AFD28`。
- 最终证据：`storage-build.log`、`storage-tests.log` / `storage-tests/`，`dg-build-verified.log`、`dg-all-tests.log` / `dg-all-tests/`，`host-build-fixed.log`、`host-matrix.log` / `host-evidence.json`，`cost-tests.log` / `cost-tests/`。保留初轮失败日志，最终 TRX 才是验收数据；未制作新包。
- Astra max 独立只读验收从当前合同、源码、全部终止矩阵原始记录和最终 TRX 判断；核对源码与反例、重算全部外部矩阵完整前缀和成本账本，结论为 **S0-C 通过，无阻塞进入薄 CLI rescue/显式维护接线小片的问题**。这不替代完整 S1/S2 验收。
- 两仓 `git diff --check` 通过；主线程检查 50 个受影响本地链接，独立审阅核对七份文档的 451 个本地链接均存在。本轮文档写入导致的 CRLF 差异已恢复为原 LF，没有以整文件换行改动覆盖已有工作。

### 仍未取得的保证与后续门

这是受限内部 S0-C 闭环，尚不是一般仓库恢复产品。普通公开 Open、writer 和 Audit 行为保持；原定 S0 范围外仍拒绝，Trailer 1–11B 仍 Unresolved。完整对象 body/factory/Upgrade 的业务可恢复性沿用 Audit 的既有边界。

薄 CLI rescue/启动维护调用可随后接本核心，但应保留明确范围、结构化失败报告及离线资源纪律；不从本片推出 S1 生命周期已闭合。快速 writer 与 Normal 验证去重另做，不能用本片的保守全历史资格成本作为全年无人值守恢复时长承诺。

F2 延用 [S0-A 模型见证](0085-s0a-tail-qualification.md)：非前缀落盘、失效 flush、共享扇区旧前缀损伤、读到但未重确认的依赖仍分别是所需前提。本片实际调用既有保守确认并验证其重试路径，未取得 OS/OpenZFS/SSD 断电、目录持久性或设备缓存保证，也没有迁移/操作真实存档。
