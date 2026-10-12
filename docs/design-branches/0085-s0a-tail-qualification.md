# DB-085 S0-A：真实写序与短尾资格只读实验

> 状态：内部只读实验已完成并通过独立验收，2026-09-29；允许进入 S0-B 只读资格实验，尚无修复资格。范围依据：[联合方案 §6](0085-joint-crash-recovery-design.md#6-垂直实施与验收门)。保留两仓第 0/1、2 片及用户已有 working tree；未提交或交付包。

## 问题与合同

真实 RBF writer 的中断输出，能否由一个从 Header 开始的连续物理前缀检查器识别，并把可观察矛盾、尚未完成检查与物理短尾候选分开？最小成功标准是：真实 Append、Builder、CreateNew 输出的支持前缀得到正确边界；完整坏帧及可验证的已有字节矛盾拒绝；源文件的长度和字节不变。

本片只增加内部只读接缝与实验。物理短尾候选不是舍弃授权；没有独占仓库生命周期、文件身份/角色、保留事实及依赖证明，不能执行任何动作。第 2 片受控来源的随机读取限制不替代这里的连续物理边界证明。公开 Open、writer 和完整 Audit 行为保持。

| 真实路径 | 实际顺序（由源码和见证补全） | 支持中断形态 | 分类结果 | 拒绝反例 |
|---|---|---|---|---|
| `RbfFile.CreateNew` | CreateNew handle → HeaderFence 写入；durable 由调用方屏障提供 | 0–3 字节正确 Header 前缀、完整 Header | Header 初始化候选 / 物理完整；不授权接管根初始化 | 已有 Header 字节错误、未知格式；路径身份未经上层证明 |
| `RbfAppendImpl.Append` | 最终 HeadLen → payload/meta → padding/CRC/Trailer/Fence；小帧合并 Write，大帧顺序多次 Write | 部分 HeadLen、body、CRC、Trailer、缺 Fence；原始 EOF 可不对齐 | 首个物理短尾候选；所有此前完整帧保留 | 非法长度；已有可验证 CRC/Trailer/Fence 前缀矛盾；完整坏 CRC |
| `RbfFileImpl` Builder | HeadLen reservation 阻挡输出，内存完成 tail 与 HeadLen 后 Commit 顺序推送 | Commit 之前无磁盘帧；Commit 中是最终图像前缀 | 同 Append；Abort 不创造已提交事实 | 把内存回填误当磁盘乱序，或把 Dispose/flush 当突然故障证据 |
| 上层 Schema/State/Journal | 沿现有格式/共同 validator 解释完整记录 | 完整物理记录的未知版本、语义矛盾 | 上层拒绝，绝不降成短尾 | 最后一帧语义错误而回退此前 head |
| F2 模型 | 成功 flush 可靠性、后续写不伤旧范围、发布依赖顺序分别建模 | 全长但 payload 未落盘、共享扇区旧字节受损、缺失依赖屏障 | 不属于 F1 自动资格；暴露所需平台/协议前提 | 把完整可读当已 durable；通过删除已确认事实来宣称恢复成功 |

检测从原始只读文件长度开始，允许非对齐 EOF；不用严格 Open 作为前置条件，不反向搜 Fence，不跳过损坏继续搜帧。完整物理帧包括 orphan/tombstone，不解释应用 payload。已有且足够验证的字节必须校验；尚不能判定的部分字段不得伪称已经验证。读取预算、取消及 I/O 失败单独表示检查未完成，无截断建议的授权含义。

## 本片之外与下一片输入

不截断、不补写、不发布、不改变格式、不加入 Clean/持久索引/通用检查等级，不冻结恢复 API。S0-B 接收原始长度、连续完整边界、首个候选/错误位置和检查状态；还须实施固定 EOF 对 ticket/FrameInfo/TailMeta/缓存/预取的约束及保留依赖检查。S0-C 才实施受限恢复与再次中断闭环。

## 真实写序核对

以下为当前源码事实，路径相对兄弟仓 `atelia-storage`；不是 OS crash 的原子性证明。

- `src/Rbf/RbfFile.cs` 的 CreateNew 先创建文件再写 4B HeaderFence，不自行 flush；中断可以留下 0–3B。任意短文件不能据此取得“合法 next segment”的身份。
- `src/Rbf/Internal/RbfAppendImpl.cs` 的 Append 在内存填最终 HeadLen，顺序写 head、未复制的 payload/meta、tail；CRC 在相应 buffer 写出前填好。1–4 次 Write 都按递增连续偏移写出。零 meta 的分支边界是 payload 4068/4069、8164/8165；旧 `GetPayloadEdgeCase` helper 未计 Fence 的 4B，不能作为精确阈值依据。
- `src/Rbf/Internal/RbfFileImpl.cs` 的 BeginAppend 在首位置预留 HeadLen；`src/Data/SinkReservableWriter.cs` 的 `FlushCommittedData` 在首个 pending reservation 停止，所以 EndAppend 前不输出任何帧字节。EndAppend 在内存完成 padding/CRC/tail/HeadLen 后 Commit，`src/Rbf/Internal/RandomAccessByteSink.cs` 逐 chunk 顺序 Write，没有磁盘回填阶段。
- 正常 Builder Abort 只 Reset 内存；当前 Append/Builder 正常路径及 Abort 不写 tombstone。Commit 中 I/O 失败可能已留下物理后缀，Abort 不清除它；F1 合同要求废弃 owner，不能在旧逻辑 TailOffset 上继续复用 writer。Dispose 关闭资源但不是 DurableFlush，也不算模拟突然退出。
- `src/RbfSegmentStore/RbfSegmentStore.cs` 的轮转是 old flush → next CreateNew → next flush → locator Publish → 交付 next writer。`SegmentLocator.cs` 先写/flush temp，再 move 覆盖；异常 finally 可删除 temp，而进程突然退出不执行 finally。单文件 byte-cut 不覆盖目录/locator/temp 组合，也不证明其名字持久性。old locator + next 数据不是该 F1 顺序的产物。

F1 byte-cut 的有效性依赖上述真实顺序以及“执行中的写只留下该序列前缀”的明确模型。测试从真实 writer 的输出取字节，不能把这些测试称为实际写调用故障注入、进程 kill 或平台断电证据。

## 内部接缝与保守停点

`atelia-storage/src/Rbf/Internal/RbfPrefixInspector.cs` 提供原始只读 path 和借用 seekable Stream 两个内部入口；后者用于可重复的短读、失败和限额测试，不作为恢复 reader 交付。
结果记录原始长度、连续完整边界、问题位置、状态及请求/返回字节与调用数。path 使用无 FileStream 用户态预取的读取；要求调用者持有稳定、独占输入的所有权。单文件 path 打开不是仓库级锁，也不认证文件角色。

状态分为物理完整、Header 初始化前缀、末尾不完整候选、矛盾/不支持、尚未判定、预算耗尽、取消、I/O 失败。完整帧无论当前是否可达都继续计入边界。未知 Header 作为 `Invalid` 拒绝；opaque payload 的版本和语义由上层判定，RBF `Complete` 不等于仓库健康。

Trailer 布局是 CRC 在前、descriptor/tag/TailLen 在后。当 Trailer 仅有 1–11B 时，初版仍检查已存在的 PayloadCRC、descriptor 保留位、meta/padding 可行性及已指定 padding，但统一保守返回 `Unresolved`。这是**本检查器尚未证明合法补全**，不是数学上不可判定，也不是这些真实 writer 中断不可能发生。12B 起可用 HeadLen 所确定的 TailLen 校验已有 CRC，再逐字节核对已有 TailLen；不会补写或把补全的校验材料算作已存在事实。后续自动恢复不能把 `Unresolved` 当作可舍弃尾部。

此停点避免在本片引入 CRC 反解/通用补全器，仍允许进入固定 EOF 与依赖实验；它限制最终自动恢复覆盖范围，须在宣布覆盖全部正常中断前解决或明确收窄承诺。

用户补充的反向流式搜索能力已核对：兄弟仓 [RollingCrc.Scanner.cs](../../../atelia-storage/src/Data/Hashing/RollingCrc.Scanner.cs) / [RollingCrc.cs](../../../atelia-storage/src/Data/Hashing/RollingCrc.cs) 提供 `BackwardScanner`；当前 [RbfRecovery](../../../atelia-storage/src/Rbf/RbfRecovery.cs) 已以 `RollingCrc` 策略接入 16B Trailer 搜索，可在无 Fence 时寻找候选。它适合后续显式离线救援的发现阶段；CRC 命中不能独立证明真实物理成员或发布依赖，S0-A 不以它替代连续前缀证明。本片不修改该既有能力。

## 验证与证据

新增源码仅为内部 [RbfPrefixInspector](../../../atelia-storage/src/Rbf/Internal/RbfPrefixInspector.cs)；[storage 见证](../../../atelia-storage/tests/Rbf.Tests/Internal/RbfPrefixInspectorTests.cs)新增 32 个测试实例，[DG 见证](../../tests/DurableGraph.Persistence.Tests/DB085S0AQualificationTests.cs)新增 10 个实例。

| 证据 | 实际覆盖 / 结论 |
|---|---|
| 真实 writer 图像 | 小 Append/Builder 逐字节裁剪；CreateNew 0–4B；Append 1/多次 Write 分支阈值及约 200KB 帧采样；多 chunk Builder 及未提交/Abort 不输出 |
| 已有字节不能忽略 | 非法部分/完整 HeadLen；部分 PayloadCRC；descriptor 的 meta/pad 下界与保留位；Trailer CRC 和部分 TailLen；完整 body 缺 Fence 及坏 Fence 前缀；完整坏 CRC |
| 连续物理成员 | 外层 payload 含完整合法 RBF 图像；只损坏外层 CRC，内嵌图像完全保留，检查仍停在此前真实完整边界；完整 tombstone 不被删掉 |
| 上层不降格 | 完整 Schema 未知版本/重复 ID，及其后另有真实短尾，仍有不可丢弃的上层拒绝；完整 State orphan 的未知版本/无效 Parent 由 Audit 报告。RBF 的物理 Complete 不认证语义；本片未新增 Journal 格式反例，沿用前片共同校验，不宣称已实现联合恢复判定 |
| 未完成不冒充资格 | 请求限额、预取消/分块取消、真实短读重试、捕获长度内提前返回 0、抛出 IOException；只读 path 检查及上层 Audit 前后源长度/字节不变 |
| F2：未确认新帧非前缀落盘 | 已确认旧帧原样，下一帧头尾完整但 payload 为零；旧 ticket 仍可读，新 ticket CRC 错误。F1 安全拒绝不代表 F2 自动恢复完成 |
| F2：失效屏障/旧范围损伤 | 模型分别破坏屏障可靠性和追加不伤旧范围的前提；已确认 ticket 失效，退回 Header 虽能物理打开却丢失已确认事实，不能算成功恢复 |
| F2：再次故障 | 第一次重启可读但未确认的 source，随后持久 publication 保存该 ticket；第二次故障丢 source 而保留 publication。补 source 确认屏障的模型对照保持依赖；这不是实际 Fork/恢复执行路径的再中断测试 |

所有测试夹具为独立临时副本，没有截断/补写被检查的源数据。byte-cut、合成故障模型与实际写故障、进程 kill、真实断电的证据等级分别记录；后三者尚无本片证据。

### 实际检测成本

下表来自尾修后 RBF 全套运行的 `CostWitness` 输出，每格一次观测，非统计基准；Windows、.NET 10、Release，`TEMP/TMP=C:/Users/gdtut/AppData/Local/Temp/db085-s0a-20260929`。夹具刚写出，OS/device cache 未冷。预热只触及 Header 内存路径，不能保证完整 JIT 稳态。

| 帧数 × payload | 文件长度 B | 请求 B | 返回 B | Read 调用 | 耗时 ms | 当前线程托管分配 B |
|---|---:|---:|---:|---:|---:|---:|
| 0 | 4 | 4 | 4 | 1 | 0.217 | 65,936 |
| 256 × 4B | 8,196 | 8,196 | 8,196 | 1,281 | 24.383 | 65,936 |
| 4,096 × 4B | 131,076 | 131,076 | 131,076 | 20,481 | 140.405 | 65,936 |
| 1 × 1MiB | 1,048,608 | 1,048,608 | 1,048,608 | 21 | 9.681 | 65,936 |

实现没有自身预取、帧表或按 HeadLen 分配的大 buffer，scratch 固定 64KiB，加少量固定字段缓冲；正常无短读的完整输入恰好读一遍文件。请求预算计入短读重试的每次请求，因此异常/短读时请求量可以大于返回量。预算不包含文件打开/Length 元数据，也不限制 OS 隐含预读；没有设备实际 I/O 或实测峰值内存结论。托管线程分配不等于进程峰值。

这是一条线性连续前缀资格路径，历史增长时成本会增长。小帧每帧五次 Read 的调用开销明显；本片没有宣称可直接用于全年运行仓库的快速恢复。固定 EOF/依赖校验、最终 Open 及仓库完整入口成本尚待 S0-B/C；之后若合并读取，必须继续约束 EOF、预算和失败分类。

### 执行记录

- 两仓 HEAD 保持 DG `c900edd5b3a921b7bf46df2ac0482dc39597e606`、storage `bf7d68ab3f3fbad18b87549dbf6ce2aae499c24a`，全部 working tree 保留；公开包 pin 未动。
- 两仓 solution Release build 通过；重新编译 RBF 时有 24 个既有 XML 注释警告，没有新增编译警告。DG 全部项目与兄弟仓依赖均为 Release，测试输出 RBF DLL 与上游 Release SHA256 一致。
- storage 全套 **1086** 项通过；独立审查加强内嵌图像见证后，RBF 全套 **444** 项再次通过。
- DG Persistence **1035** 项、Storage **202** 项通过。DG 的其余测试程序集本片仅构建，没有将上片全量结果重复算为本片执行。
- .NET 命令全部串行，使用 `-c Release -m:1 -nr:false`；test 使用匹配配置的 `--no-build --no-restore`。DG build/test 另显式携带 `-p:UseStorageSources=true -p:StorageSourceRoot=E:/repos/Atelia-org/atelia-storage -p:ShouldUnsetParentConfigurationAndPlatform=false`。
- 本地日志与 TRX 在 `.artifacts/db085-s0a/`：`storage-build-final.log`、`storage-tests.log` / `storage-tests/`、`dg-build.log`、`dg-persistence-tests.log` / `dg-tests/`、`dg-storage-tests.log` / `dg-storage-tests/`、`rbf-review-tests.log` / `rbf-review-tests/`。成本表取最后一组 TRX，未使用初次运行的耗时替代最终数据。
- 收口检查：两仓 `git diff --check`、新增文件空白检查通过；本片文档及活动入口的 56 个本地链接/锚点通过。与片开始保存的 tracked diff 对照，既有产品代码差异保持，没有覆盖用户适配或前两片实现。

### 独立验收与后续门

由 Astra max 只读独立审阅需求、源码、测试与最终日志/TRX，未读取旧评审来推导正确性。主线程整合后复核实际产物。审查中前移了最大帧地址检查，防止部分 HeadLen 绕过地址范围；进一步加强了内嵌合法图像保持原样的反例。最终结论：**没有阻塞进入 S0-B 只读固定 EOF/依赖资格实验的问题**。

下一片必须把 `LastCompleteBoundary` 当作待建立受限视图的物理证据，不能当修复许可。至少覆盖 ticket、FrameInfo、TailMeta、缓存命中、预取及间接 State 读取不越候选 EOF，完整 orphan 和上层依赖不得悬空；元数据、文件身份与持续锁仍由上层证明。`Unresolved`、Invalid、I/O、预算/取消状态都不能进入动作阶段。

实际恢复仍被 S0-B 的依赖资格和 S0-C 的真实动作/再次中断门阻塞；Trailer 1–11B 尚未取得自动资格，完整入口成本、真实故障注入及 F2 部署证据也没有完成。可靠旧屏障下新帧非前缀落盘的 F2 资格应单独论证，不能直接改用“坏 CRC 就截尾”。公开 API、持久格式、Normal/writer/Audit 的现有行为均保持。
