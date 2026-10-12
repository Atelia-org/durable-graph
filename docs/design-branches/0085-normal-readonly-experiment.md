# DB-085 第 2 片：内部只读 Normal 成本实验

> 状态：内部实验已实现并通过独立验收，2026-09-29；公开只读默认暂不切换。范围依据：[联合方案 §10.1](0085-joint-crash-recovery-design.md#101-第-2-片的建议实施边界与验收)。第 0/1 片及已有 working tree 保留；不改变公开 Open、可写确认/发布或完整 Audit。

## 问题与实施边界

固定目标图、所需祖先与 Schema/catalog，仅增加无关历史，候选 Open 加首次实际读取能否消除全库工作，同时保留被访问记录及其依赖的检查？

只增加内部只读候选入口；复用生产解析、图关系检查和物化。Schema 重放、必要 catalog/locator/active tail 检查保持。完整 Audit 是独立对照，不嵌入候选 Open。没有 Clean、恢复/截断、持久索引、新格式、通用检查等级或快速 writer。公开默认切换须另作决定。

两仓起点与[首片](0085-audit-implementation.md)一致；显式源码联调，公开包 pin 不变。操作串行、原资源所有权/生命周期/重入守卫保持。按需验证只允许操作内工作集，不增加跨操作验证缓存。

## 入口合同

下表先于候选实现收口。结构校验不运行应用 factory/Upgrade；实际物化仍沿现有完整读取管线。错误向调用者传播，不回退旧 State，不以部分成功结果冒充完整结果。

| 入口 | 所需依赖 | 拒绝时机 | 最小反例 |
|---|---|---|---|
| 内部候选 Open | 现存仓库锁、format、locator、必要 catalog/suffix、active tail、完整 Schema 目录 | 交付 Repository 前 | 锁占用、未知目录格式、坏 Schema、活动坏尾；不提前认证全部 branch/tag 图 |
| ListBranches | 已重放的名称目录与仓库寿命 | 返回名称前 | Dispose 后访问；名称存在不等于其图已验证 |
| GetHead / ResolveTag | 所选 ref/tag 的生产解析及目标；目标 Journal 祖先链；每条记录的最近 State、Revision Parent、根成员、对象链及 Schema 类型头 | 签发句柄前 | 空 branch head、CRC 正确但 Parent 错配、缺 Revision、错误 root、错误表示 |
| ReadFrames | 固定 head 所选逻辑链，与每个签发句柄相同的检查 | 返回完整列表前 | 所选祖先损坏；无关分支不触发全历史扫描 |
| GetPreviousState | 本次打开的 Event 句柄，沿严格祖先找最近 State；签发目标的结构闭包 | 返回句柄前 | 跨打开句柄、错误角色、祖先的 Journal/Revision Parent 错配 |
| EnumerateEvents | 本次打开的固定结尾/下界；所遍历链、下界确为祖先；每个交付 Event 的结构闭包 | 参数错误在调用时；下界错误在首项前；每次 MoveNext 检查寿命，结果交付前检查结构 | 无关下界、两种顺序、枚举期间或耗尽后 Dispose、循环体内继续读取 |
| ReadState / ReadEvent | owner/kind；所选记录的结构闭包；完整 exact 解码、引用验证、Normalize 与领域物化 | owner/kind 在 I/O 前；结构关系在应用回调前；完整成功才交付根 | 跨打开句柄、缺模型、CRC 正确但关系错误、不支持 wire、坏 body |
| ReadCheckpoint | 所选记录与最近严格祖先相反角色记录；两条所需闭包及原独立物化 | 结构检查先于应用回调；两图成功才交付 Checkpoint | Event-only 前缀、跨类型 State、另一角色依赖损坏 |
| ReadPair | 两个本次打开句柄；两条所需闭包及原共享闭包证明/物化 | 两侧结构检查先于应用回调；完整成功才交付 pair | 第二图错误、相同句柄、共享/独立语义、跨打开地址 |
| Checkout / Fork / CreateBranch / CreateTag / Move / Commit | 原可写要求及全部现行确认/发布检查 | 候选在写操作入口拒绝 | 只读候选不能借写接口取得读取资格；公开 writer 回归 |

祖先闭包以 Journal Parent 为导航权威，从目标回到根，再按正向次序应用共同 ValidateGraph。最近 State 不使用全库字典。每个被访问 State 的 Parent、external head、local prior 另以操作内 visited/栈建立依赖闭包；即便一个对象在 Base 终止，也不能漏查其 containing Revision 自身的依赖。单记录规则 `ValidateRevisionDependencies` 与 Audit 共用，Audit 额外提供真实物理成员检查。所需祖先成本允许增长，必须单列计量，不能归入“固定目标”后隐藏。

### 已选择的受控来源与物理边界

现有随机帧读验证局部形状与 CRC，不独立证明真实物理帧边界。合法 opaque payload 可内嵌完整合法帧；即便再查邻接 Fence，也不能替代从可信物理边界开始的扫描。独立认证任意地址最坏会触及此前的无关历史。

用户已明确选择沿 §5.1 的受控来源合同实施内部候选，并保留上述限制和反例。候选不独立认证每个物理边界；未知来源先用适用的完整 Audit 建立结构基线，且后续所有 writer/维护路径必须继续遵守协议。一次 Audit 不证明硬件持久性。要求随机地址独立认证的另一方案会引入此前物理历史扫描，本片不采用。Audit 保留完整物理成员检查；实际访问发现的 CRC、格式或关系矛盾仍拒绝。

## 计量与验收账本

| 要求 | 责任 | 证据 / 当前状态 |
|---|---|---|
| 入口与依赖合同、范围取舍 | 主线程 | 本表；受控来源边界已由用户裁决 |
| 生产只读候选与按需校验 | Sol 实现接线，主线程共同校验与整合 | 已验证；State Base 早停遗漏及失败地址定位已修 |
| 基线/候选完整成本 | Sol 计量、主线程实跑与核验 | 四轴 270 个记录样本；Open、首次查询、首次图读、合计；独立 Audit 对照 |
| 按需错误、寿命、公开行为回归 | 测试与主线程 | 全量通过；上表反例及源路径/字节不变 |
| 独立验收 | Astra max，只读 | 通过；两项有效发现修复，无未关闭阻塞，核对最终源码/日志/TRX/矩阵 |

计量三条独立轴：无关历史（目标在前与在后）、所需祖先/对象链、Schema/catalog。区分逻辑请求、底层请求/实际返回和预取；底层返回不等于物理盘流量，进程冷不等于 OS 缓存冷。记录耗时、分配、驻留和峰值，无法准确覆盖的指标明确标注。原公开只读路径与候选使用相同夹具/操作，不能靠移到首次查询来通过。

验证使用任务专用 `C:/Users/gdtut/AppData/Local/Temp/db085-normal-20260929`，保留真实 Flush；两仓 .NET 串行、Release、`-m:1 -nr:false`，DG 显式 `-p:UseStorageSources=true -p:StorageSourceRoot=E:/repos/Atelia-org/atelia-storage`。本片工件集中在 `.artifacts/db085-normal/`，不制作包、不提交、不发布、不迁移真实数据。

本片修改前的 Release solution build 为 0 warning / 0 error，Persistence 997 项通过。日志为 `baseline-build.log` 与 `baseline-tests.log`；这是起点证据，不是候选完成证据。

## 实现与独立审查

- `Repository.OpenReadOnlyCandidate` 是内部入口，单独取得只读资源；公共 `OpenReadOnlyExisting` 和 writer 仍使用原来的 eager 验证。候选 `HistoryJournal` 不填充全历史 `_frames`，`GraphResources` 不枚举全部 State 文件；Schema/catalog 与上游严格 active tail 检查保留。
- `RepositoryGraphValidator.ValidateAncestry` 只跟随所选 Journal 祖先及其 State 依赖。`Issue` 在签发句柄前调用；实际读取另行复核，并保持 owner/kind、串行、重入、fault 和 Dispose 纪律。没有跨操作验证缓存，重复验证成本进入测量。
- State 单记录边及 map/prior/表示规则抽到 `ValidateRevisionDependencies`，Audit 以物理库存回调使用同一规则，候选以实际访问闭包驱动。Audit 的报告、枚举、取消和不支持格式语义保持。
- 上游仅增加内部同步线程计量 scope `RbfReadMetrics`，包括 RBF factory 的 header 读。真实 `RandomAccess.Read` 返回值与请求分开，overfetch 为返回区间落在当次逻辑请求区间外的字节。没有更改缓存算法、公共 API 或持久格式。

由独立 Astra max 审查者对照合同、源码和反例验收，主线程核对并运行验证；未把前片结论当作本片正确性证据。两项有效发现均已修复：

| 发现 | 修复与见证 |
|---|---|
| live external head 指向无 Journal 的 orphan O；O.local[root] 是合法 Base，但 O.Parent 或另一个 external head 无效。对象链在 Base 早停，不能发现 O 自身的依赖矛盾 | 按需 State 闭包检查所有被访问记录的直接依赖，再共用 map/prior/类型头检查；缺失、非前序及额外 external 反例在 Head/tag/读取前拒绝，回调数为零 |
| CRC/framing Result 的 `Unwrap` 抛 `InvalidOperationException`，原诊断过滤器漏掉它；间接读取还可能把坏 O 标作 owning R | 保留原异常类型，覆盖该异常并附加地址；先逐帧读取地址闭包，再做间接 map/对象链检查。非末 Journal 祖先、State 根及 external O 自身的 CRC 反例验证延后拒绝和定位 |

另保留一个会**被候选接受、被完整 Audit 拒绝**的资格反例：在合法外层 orphan 的 opaque body 内嵌可正常物化的 State frame，再用绕过 DG writer 的引用指向它。该例证明受控来源前提不可省略，不把局部 CRC 成功误记成物理成员认证。测试检查所有读取/Audit 后源文件名称与字节不变。

### 实验环境与计量缺口

Windows 10.0.22000 / .NET 10.0.5 / x64，测试数据库在专用 C: 临时目录。每个成本轴由 runner 启动独立 test host；单个 host 内 JIT/process 已预热，每个模式/操作重新打开资源，一次弃用样本后记录三次，基线/候选顺序交替。预先造库和 Audit 触碰了 OS 缓存，不能把本实验称为设备冷读。

根 solution 的源码外部 ProjectReference 默认可能取消父 Configuration，导致 DG Release 配上 storage Debug。最终命令显式加入 `-p:ShouldUnsetParentConfigurationAndPlatform=false`；日志确认所有项目输出到 Release，且测试目录的 RBF DLL SHA-256 与上游 Release 一致。初轮配置和首轮修复前性能数据不作为最终计量依据。

所有 API 时间与线程分配包含必要 metadata 工作；RBF 字节口径**不包含** journal.format、catalog.snapshot、segment locator 的 FileStream 读取，输出该缺口而不宣称端到端字节上界。原生内存、设备物理 I/O 与精确瞬时峰值未测。阶段 heap 为抽样，process peak 含 host/JIT/之前单元；驻留是另一次 fresh open 的全 GC heap 差值，含共享模型/runtime 影响，不能解释为独占缓存尺寸。详细口径与可复跑命令见[实验入口](../../experiments/ReadOnlyNormalCostProbe/README.md)。

## 最终成本结果

15 个夹具单元 × 2 个模式 × 3 个首次读取入口 × 3 个记录样本，共 **270** 个记录样本，另有 90 个弃用 warmup。State、Event、Checkpoint 分别 fresh open；固定三节点环及别名，Checkpoint 同时检查两个独立图。逐阶段完整中位数见 [CSV](../../experiments/ReadOnlyNormalCostProbe/observations-20260929.csv)；原始 JSONL 在 `.artifacts/db085-normal/measurements-final/`。下列数字来自修复后的同一份产物，不使用首轮数据。

固定目标位于无关历史之前，`Open → GetHead → ReadCheckpoint` 的中位数：

| 无关历史帧对数 | 路径 | RBF 请求 B | raw 实际返回 B | overfetch B | 总分配 B | Open 后驻留差值 B | 总耗时 ms |
|---:|---|---:|---:|---:|---:|---:|---:|
| 0 | 公开基线 | 3,732 | 2,132 | 1,988 | 865,472 | 386,376 | 6.443 |
| 0 | 内部候选 | 2,368 | 1,020 | 932 | 489,280 | 308,448 | 4.031 |
| 256 | 公开基线 | 110,204 | 104,532 | 103,900 | 1,661,784 | 582,504 | 10.596 |
| 256 | 内部候选 | 2,344 | 14,332 | 14,084 | 489,280 | 308,448 | 2.975 |
| 1024 | 公开基线 | 429,692 | 416,252 | 413,924 | 4,083,712 | 1,177,176 | 14.091 |
| 1024 | 内部候选 | 2,344 | 9,212 | 8,964 | 489,280 | 308,448 | 2.542 |

目标位于无关历史之后的候选 Checkpoint 总请求为 2,368 B，0/256/1024 三档保持一致；State/Event 入口也通过确定性工作量见证。raw 返回量随尾页填充和目标地址对齐变化，不能要求等于请求或随规模单调。这些数据支持**消除了无关历史的全扫及常驻工作**，不是固定延迟或设备 I/O 的承诺。

首次查询与图读取并未隐藏在 Open 外：

| 场景 / 路径 | Open ms | 首次查询 ms | 首次 Checkpoint ms | 完整合计 ms |
|---|---:|---:|---:|---:|
| 无关历史 1024 / 公开基线 | 13.945 | 0.014 | 0.141 | 14.091 |
| 无关历史 1024 / 候选 | 1.940 | 0.459 | 0.143 | 2.542 |
| 所需祖先 32 / 公开基线 | 8.394 | 0.007 | 0.414 | 8.830 |
| 所需祖先 32 / 候选 | 2.109 | 5.800 | 8.224 | 16.126 |

各列独立取三个样本的中位数，阶段中位数之和不必恰等于合计中位数。只有三次记录样本，时间用于描述本机观察；确定性读取/分配与源码成本是范围结论的主要证据。

限制和否定结果同样进入验收：

- **祖先成本尚未收口。** 32 层场景候选 Checkpoint 的 1,667,688 B 分配也高于基线 1,177,000 B。源码仍有签发/读取之间、Checkpoint 导航以及图规则/State 规则之间的重复检查；不能用 Open 快来掩盖合计退步，不能宣称单条长期增长历史已获得稳定低成本启动。
- **Schema 仍全量重建。** 增加 256 个未被目标引用的 Schema 时，候选 Checkpoint 合计约 38.589 ms / 15,322,568 B 分配，基线约 41.308 ms / 15,698,760 B。省去历史 sweep 没有消除目录重建成本。
- **catalog 规模仍可见。** 增加 256 个 tag 时，候选 Checkpoint 的 RBF 总请求为 25,920 B、合计约 4.417 ms；零额外 tag 时为 2,368 B / 3.579 ms。没有把必要 catalog 规模混进固定元数据的无关历史结论。
- 按地址访问的物理成员资格继续依赖受控来源；完整 Audit、恢复、持久确认和硬件资格不能由本实验替代。缺少 metadata 实际字节及精确峰值测量，不能发布端到端字节/内存硬上界。

**本片问题得到肯定回答，但不直接切换公开只读默认。** 可行取舍是保留内部候选，先用操作内依赖/检查复用消除重复工作，再复跑长祖先及导航成本；这保留当前检查合同，代价是一个小的后续优化片。另一选择是以受限规模提前公开候选并接受某些首次读取退步，易用性和默认性能不够稳妥，本轮不推荐。若去重仍不能改善，再用反例讨论验证闭包边界，不能直接少查必需依赖。S0 恢复仍可按原计划独立推进，不依赖本候选成为公开默认。

## 最终验证与交付边界

- DG 最终 Release solution build：0 warning / 0 error；全量 **3042** 项通过（Persistence 1025、Serialization 163、Storage 202、Runtime/Generator 1652）。本片新增 28 项，包含重矩阵 opt-in 入口；重矩阵另经 runner 在四个 test host 实际运行并全部完成，不把常规关闭状态算计量证据。
- storage Release 构建成功；全量 **1054** 项通过（Primitives 75、Data 212、RBF 412、SegmentStore 48、EventJournal 174、Toolkit 133）。新增 7 项计量测试包含请求/实际短读、overfetch、header、失败、scope/thread 寿命；重编译时 24 个既有 XML 文档 warning，不是新增告警。
- 新旧 Audit/CLI、候选错误关系与生命周期、原 writer/public Open 回归均包含在最终测试。只运行 Windows，不把既有按平台提前返回的测试解释成 Linux 或断电证据。
- 独立验收确认：内部实验通过，尚不足以切换公开默认；45 组操作的基线/候选结果校验和一致。两仓差异空白检查及受影响本地链接/锚点检查通过。

完整日志：`.artifacts/db085-normal/build-final.log`、`dg-final-tests.log` 与 `dg-final-tests/*.trx`、`storage-tests.log` 与 `storage-tests/*.trx`、`measurements-final.log` 和 `measurements-final/*.jsonl`。只读源数据不变见证和独立反例均保留为回归测试。

本片保持两仓已有 worktree，不提交、不发布、不制作新包、不迁移真实数据。公开 API、writer 依赖确认/发布、完整 Audit 的行为边界保持；仍无新增恢复或断电保证。
