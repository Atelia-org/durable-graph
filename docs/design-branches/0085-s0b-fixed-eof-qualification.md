# DB-085 S0-B：固定 EOF 与保留依赖只读资格实验

> 状态：内部只读实验已完成并通过独立验收，2026-09-29；可进入 S0-C，尚无实际恢复闭环。依据 [联合方案 §4、§6、§10](0085-joint-crash-recovery-design.md) 与 [S0-A](0085-s0a-tail-qualification.md)。保留两仓全部已有 working tree；本片不提交、不发布、不迁移数据。

## 问题、范围与成功标准

在原文件不变且仍存在 EOF 外可读字节时，能否建立全新、固定可见 EOF 的只读视图，并用现有 parser / 共同 validator 证明全部保留事实的持久依赖完整？成功须同时有越界拒绝、连续物理成员证明、完整 orphan 校验以及源数据不变的可执行证据；固定 EOF 本身不授予舍弃资格。

限定已经初始化、一个已绑定 branch、每个 store 只有第 1 段，catalog 只有初始 snapshot 与 Create/BindName，ref moves 为 Init/Advance。Schema、State、Event、ref Advance 追加可有 S0-A 支持的短尾；单串行 writer 范围内最多一个短尾，ref-op-log 初始化必须完整。tag、Fork/Archive/Close、轮转、checkpoint、额外目录/未知内容明确拒绝为范围外或无效，不能静默遗漏。完整格式/语义错误不通过选择更早 EOF 隐藏。

## 共同合同

| 读取入口 | 候选边界 / 物理成员证据 | 所需依赖 | 拒绝时机 | 最小反例 |
|---|---|---|---|---|
| 原始物理检测 | S0-A 从 Header 连续扫描，只有 Complete / IncompleteSuffix 可继续；固定边界为全部完整帧之后 | 路径角色、初始化与单段定位 | 构造候选之前；Unresolved/Invalid/I/O/取消/超限均不通过 | 完整坏 CRC 后再有短尾；Trailer 1–11B |
| ticket / pooled frame | `offset >= 4`、完整 frame + Fence 在固定 EOF 内；大 buffer 租用前检查 | CRC / Trailer / Fence；成员资格另由连续枚举给出 | 分配和读取前 | 实际文件 EOF 外有完整帧，ticket 仍可被普通 reader 读取 |
| FrameInfo / TailMeta | info 所属全新 reader 的同一不可变 EOF；包括零 meta 快路径 | ticket 与完整 Fence | 每个公开读取通道进入时 | 从较宽旧 reader 带入 info 不能成为候选的成员证据 |
| 扫描 / 缓存 / 预取 | scan 终点固定；logical read 在缓存前裁剪，raw read 在预取前裁剪 | 独立全新缓存、稳定文件 | 缓存命中亦受界限；底层 raw 请求不跨界 | 尾页预取包含 EOF 外合法帧 |
| Schema 重放 | 固定文件连续枚举，全部完整批次保留 | 既有目录 codec 与 SchemaStore 关系规则 | 候选构造阶段 | 最后完整批次未知版本或重复 ID，其后短尾不掩盖它 |
| 全部 State（含 orphan） | 连续扫描生成的实际 ticket 集合；范围 + CRC 不代替成员资格 | Parent、external heads、local priors、Base RepresentationId；共同 validator | 每条边先验证成员及先后，再允许间接读取 | opaque payload 内嵌合法 State；orphan 指向不存在/被排除帧 |
| 全部 Journal 图（含 orphan） | 连续枚举真实 Event ticket，复用 header/payload parser | Journal Parent、Revision、最近 State、根及图结构 | ref 可用前，全枚举检查 | 当前 head 健康但 orphan graph 有坏 Revision |
| 全部 ref/catalog 事实 | 固定 ref/log EOF，复用 EventJournal AuditEngine 规则 | Create/Bind/Init 身份；所有历史 old/expected/new targets；snapshot 对应实际日志 | 所有保留事实检查结束后才报告 Qualified | 完整 Advance 指向被排除 Event；更早 ref move 指向缺失 target |
| 生命周期 / 预算 / 结果 | 独占 repository.lock；源文件 guard 阻止替换/写入；全新资源在锁内关闭 | 共享 RBF 请求字节额度、取消；不同失败分类 | 任一未完成不得升级为 Qualified | 二次读取超限、文件 I/O 失败、检查中取消 |

## 最小构造接缝与边界

- RBF 增内部 fixed EOF factory，复用现有 reader/cache/codec；范围约束同时放在 logical/cache 和 raw/prefetch 边界。仅候选拒绝写入，普通行为保持。此 factory 是受限字节视图，不独立认证物理成员。
- State 使用现有 concrete SegmentStore 的单段只读内部构造接缝，以保持真实 lease / owner 语义；关闭 State 缓存，不能借旧 Store。
- Journal 复用 AuditEngine 的扫描及全部 ref 校验，通过窄内部 RBF opener 接收固定视图；候选在外部持续锁和文件 guard 下运行，不引入 VFS，不重复全文件 hash。公开 Audit 原路径保持。
- DG 协调连续检测、候选构造和依赖验证；复用 SchemaStore、HistoryJournal 解码与 RepositoryGraphValidator。不交付 Repository、可写 Store 或可外部重放的计划。
- 预算限定实际发出的 RBF Header/raw 请求字节（含重复读取/预取），物理检测亦扣同一额度。目录枚举、locator/format/snapshot 元数据读取、分配/CPU/设备实际 I/O 不在该字节预算中，须报告此限制；不称全资源上限。
- 分阶段报告请求/返回字节、调用数、预取、耗时、当前线程托管分配及合计；不把进程冷启动称 OS 页缓存冷，不将线程分配称峰值内存。性能数据以最终执行结果补入。

## 不实施

不截断、补写、发布或恢复；不解决 Trailer 1–11B 合法补全；不优化 S0-A 扫描器、不加 Clean/持久索引/新格式/通用检查等级。S0-C 仍须真实动作、恢复再中断、持久性重确认、续写及冷开证据；本片 Qualified 仅表示本次锁内只读资格通过。

## 实施、验证与独立验收

内部协调器 [RepositoryRecoveryQualification](../../src/DurableGraph.Persistence/RepositoryRecoveryQualification.cs) 的 `Qualify(path, readBudget, token)` 返回 `Qualified / Invalid / Unsupported / Unresolved / BudgetExceeded / Cancelled / IoFailure`、每文件原始长度/固定 EOF 和阶段指标。它不接受调用者指定更早 EOF；成功报告在资源全部关闭后交付，不可当作锁外可执行计划。

RBF [内部 factory](../../../atelia-storage/src/Rbf/RbfFile.cs)复用 [RandomAccessReader](../../../atelia-storage/src/Rbf/ReadCache/RandomAccessReader.cs)，在 logical/cache 与 raw/prefetch 两层裁剪；ticket + Fence 在 frame/TailMeta 租用前检查。构造校验 Header、候选末 Trailer/Fence，沿途 CRC 与连续成员由 S0-A 和上层完整枚举承担。仅候选拒绝 Append/Builder/Flush/Truncate。State 使用 [SegmentStore 单段只读接缝](../../../atelia-storage/src/RbfSegmentStore/RbfSegmentStore.cs)保留真实 lease/owner；Journal [AuditCandidate](../../../atelia-storage/src/EventJournal/EventJournalAudit.cs)沿用已有 AuditEngine，候选路径才省掉 Capture/hash。Event 解码与公开 ReadEvent 共用小核心，候选底层 I/O 异常直接传播，公开错误映射保持。

持续持有既有 `repository.lock`，已知源文件以 `FileShare.Read` guard 防写入/替换；前后核对目录清单并拒绝 reparse。此证据针对遵守协议的受控 writer，不能宣称阻止恶意进程替换父目录或忽略平台锁。先关闭 facade、再 owner/reader/guard，最后释放锁；没有旧 Store/cache 参与。

| 可执行证据 | 结论 |
|---|---|
| [RbfCandidateTests](../../../atelia-storage/tests/Rbf.Tests/Internal/RbfCandidateTests.cs)，19 实例 | 7 个 cache mode；候选外完整帧；ticket/FrameInfo/TailMeta/零 meta/扫描；预取与暖缓存；巨大地址和长度在 `BufferRentObserver` 前拒绝；缺 Fence；取消/预算/I/O；只读及失败句柄释放 |
| [上游 Journal 候选测试](../../../atelia-storage/tests/EventJournal.Tests/CandidateJournalAuditTests.cs)与 [SegmentStore](../../../atelia-storage/tests/RbfSegmentStore.Tests/RbfSegmentStoreTests.cs)，7 新实例 | 真实 lease/只读 owner；同 Audit 协议检查；tag/checkpoint 范围拒绝；Identity/Brotli/Zlib 与普通 Event 读取等价 |
| [DG 资格测试](../../tests/DurableGraph.Persistence.Tests/DB085S0BQualificationTests.cs) | 4 类真实 Append 图像的部分 HeadLen；完整合法 orphan；Parent/external/local prior/RepresentationId；完整 Schema/CRC/Journal 错误；历史 ref 与被排除 Event 的完整 Advance；内嵌合法 State 不能成为成员；Unresolved/多短尾/范围拒绝；共享预算跨阶段、取消、独占 guard、I/O 分类 |
| [State reader 边界](../../tests/DurableGraph.Persistence.Tests/DB085S0BReaderBoundaryTests.cs) | 同一实际文件中候选 EOF 外帧可由普通 reader 读取，fresh State 直接/map/object-chain 读取均拒绝；旧缓存已关闭，新缓存命中亦不读取后缀；仅有完整 body 不足以省掉 Fence |
| [成本与独立 Audit 对照](../../tests/DurableGraph.Persistence.Tests/DB085S0BCostTests.cs) | 原完整夹具 Audit 健康；另造短尾 Audit 拒绝但候选资格通过；全程源文件集合、长度、字节不变；Audit 在计量区间之外 |

本片 DG 新增 **32** 个测试实例（含成本测试）；所有故障都是独立临时夹具的真实 writer 图像裁剪、CRC 正确的反例或明确的异常注入，不是 OS crash/power loss。对象 body/factory/Upgrade 的业务可恢复性沿用 Audit 边界，不在本片新增认证。

### 实际成本

Windows、.NET 10、Release、固定 EOF cache Off / State cache 0；每格新 fixture，预热 1 次、观测 3 次，以下为各项中位数。最终只运行成本测试，避免同程序集其他测试并发影响时间；来源 `.artifacts/db085-s0b/cost-tests/*.trx`，派生 `cost-samples-isolated.json` / `cost-medians-isolated.csv`。夹具刚写入，OS/device cache 未冷；进程启动和单次预热不证明完整 JIT 稳态。

| 夹具 | 物理检测请求 B | 候选构造请求 B | 依赖校验请求 B | 合计请求/返回 B | raw 调用 | 当前线程分配 B |
|---|---:|---:|---:|---:|---:|---:|
| 单图基线 | 664 | 264 | 1,440 | 2,368 / 2,368 | 111 | 514,392 |
| 基线 + 2B 短尾 | 666 | 264 | 1,440 | 2,370 / 2,370 | 112 | 514,392 |
| +8 次历史 State 提交 | 2,872 | 872 | 14,624 | 18,368 / 18,368 | 839 | 788,968 |
| +32 次历史 State 提交 | 9,496 | 2,696 | 127,904 | 140,096 / 140,096 | 6,095 | 3,415,136 |
| 单图 32 个 live 节点 | 976 | 576 | 48,080 | 49,632 / 49,632 | 359 | 1,429,608 |
| +32 个完整 State orphan | 1,944 | 2,440 | 6,560 | 10,944 / 10,944 | 719 | 713,888 |
| +1 个约 1MiB opaque body orphan | 1,049,280 | 1,048,908 | 4,195,904 | 6,294,092 / 6,294,092 | 146 | 5,765,264 |

| 夹具 | 物理检测 ms | 候选构造 ms | 依赖校验 ms | 入口合计 ms |
|---|---:|---:|---:|---:|
| 单图基线 | 1.015 | 0.363 | 9.740 | 18.372 |
| 基线 + 2B 短尾 | 0.926 | 0.384 | 10.672 | 19.781 |
| +8 次提交 | 1.389 | 0.631 | 14.613 | 24.272 |
| +32 次提交 | 3.132 | 1.500 | 56.600 | 68.139 |
| 32 live 节点 | 0.925 | 0.390 | 16.008 | 24.331 |
| 32 orphan | 1.629 | 1.520 | 13.039 | 23.194 |
| 约 1MiB orphan | 1.811 | 0.932 | 14.398 | 23.872 |

每格 RBF 预取返回量为 **0**；上表请求/返回是 raw API 字节，所有 Header 亦计入共享额度，不是设备实际 I/O。logical 指标另留在 JSON。`HeaderReadCalls` 是既有 factory 专项计数与物理检测标记，fixed factory 经 reader 计入 raw，不能把该子项当全体 Header 调用数。

物理阶段是 S0-A 连续检测；构造阶段含固定 reader、全 Schema 重放及 State 物理成员集合；依赖阶段含完整 Journal/ref Audit 及 DG State/graph 规则。入口总耗时/分配还包含持锁、目录/metadata 库存与最终 Dispose，所以大于三阶段之和；独立中位数也不应相加解释。读取预算不覆盖 metadata/目录、解压/对象分配或 CPU。当前线程分配不是峰值、驻留或 native 内存；未取得设备 I/O 和精确峰值内存指标。

检测与物理成员集合随历史增长，后者及 Journal/ref 字典占 O(帧数) 存储；关闭 State cache 后重复 map/object-chain 校验具有显著放大，长链并非线性成本承诺。约 1MiB orphan 被多次读取，共约 6.3MB RBF 请求；32 次提交明显高于 8 次。它证明保守资格可执行，不证明全年仓库快速恢复。最终严格 Open、实际修复及再确认成本仍须 S0-C 单列。

### 执行记录与验收门

- 两仓 HEAD 保持 DG `c900edd5b3a921b7bf46df2ac0482dc39597e606`、storage `bf7d68ab3f3fbad18b87549dbf6ce2aae499c24a`。起点 tracked diff 与旧 untracked SHA256 保存在 `.artifacts/db085-s0b/`；旧产品适配保留。本片仅对旧 AuditEngine 增候选分支，共同普通路径行为由回归核对。
- storage solution Release build、全套 **1112** 项通过；DG solution Release build、Persistence **1067** 项及 Storage **202** 项通过。DG 其他测试程序集本片仅构建。最终 DG build 0 warning/0 error；重编 RBF 的 24 个既有 XML 警告保持，本片新增警告已修。
- 所有 .NET 命令串行，`TEMP/TMP=C:/Users/gdtut/AppData/Local/Temp/db085-s0b-20260929`，`-c Release -m:1 -nr:false`，test 在匹配构建后 `--no-build --no-restore`。DG 另带 `-p:UseStorageSources=true -p:StorageSourceRoot=E:/repos/Atelia-org/atelia-storage -p:ShouldUnsetParentConfigurationAndPlatform=false`。两侧测试 RBF DLL SHA256 均为 `797D2CD3F25AE3DD834682DA1C914375DE8788630F04C2D93BD2619A1841047B`。
- 最终日志：`storage-build.log`、`storage-tests.log` / `storage-tests/`、`dg-build-final.log`、`dg-persistence-final.log` / `dg-persistence-final/`、`dg-storage-tests.log` / `dg-storage-tests/`、`cost-tests.log` / `cost-tests/`。全套运行中的成本样本也保留，但本表取隔离成本运行。
- 收口检查：两仓 `git diff --check`、新增文件空白检查及 42 个受影响本地链接/锚点通过；直接比较起点差异确认既有 tracked 产品改动保留，原 untracked 实现中仅本片接线的 EventJournalAudit 改动。源码联调，无新包或平台断电验证。
- Astra max 独立只读核对需求、源码、反例、起点差异、最终日志/TRX 与 DLL，未读旧评审作为正确性依据；未修改文件或运行 .NET。主线程直接核对并整合，结论为 **S0-B 通过，无阻塞进入 S0-C 的问题**。主线程整合中修复了候选 Event I/O 传播和新增编译警告，并补齐 local prior、历史 ref、独占生命周期及完整成本见证。

S0-C 仍须在持续锁内重新推导资格、关闭所有候选资源、验证路径/长度并执行持久屏障；不得持旧报告在锁外截断，也不得把本片只读通过当作二次故障闭环已经通过。首先沿既有单 branch 的真实 State/Event 提交路径做动作 → 再次中断 → 重试 → 续写 → 冷开；所有完整事实继续保留，未知或 Unresolved 输入停下。S0-B 的全部历史成本与阶段指标作为该片基线，实际动作、依赖重确认和最终严格 Open 成本另计；不同时开启生命周期/S1 或快速 writer 设计。
