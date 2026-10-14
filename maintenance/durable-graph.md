# DurableGraph 循环维护

## 目标与执行约束

- 目标：逐轮消除有证据的技术债或实施有真实收益的安全重构，直至已知线索均有妥善处理的结论。范围覆盖当前产品源码、测试、构建与活动实验；不把路线图中的条件延期当作自动实施授权。
- 用户指定本文件为任务状态入口；使用 `convergent-maintenance`，复杂实施按需 `bounded-delegation`，设计问题优先 `dialectical-simplification`。
- Goal 持续执行，无固定轮数上限；2026-10-15 启动，已完成 **3 轮**。只有完成复核、验收和收尾的维护结果才计一轮。
- 草稿窗口：不承担旧数据或 API 兼容；仍保完整帧 append-only、正常 FS/VS 恢复、准确来源、发布 outcome、fault/reentry/lifetime 检查。保留他人工作，不推送或发布。
- 产品事实与导航：[PROJECT-STATE](../src/PROJECT-STATE.md)；长期约束和条件延期分别归目标设计与路线图，不在此复制。

## 当前工作

初始核对：工作树干净，HEAD `0ac20ba`；此前 DB-092 有限命名重构已收尾。存储 pin 为 `0.1.3-dev.20261014.g3de8fb0`，实际构建须使用其完整本地 feed 或显式源码模式。

首轮模块调查和三轮实施均已验收，结果与证据分别查下表及验收段。第 3 轮 M010 在单次 decoder 中立即完整读图、私有惰性读取各链；32 行见证证明所选图解码／目录物化各一次，保完整 rows/head/实际 H。独立三方设计质疑与源码／测试复核通过，根 build 零警告错误、Persistence 912 与 Runtime／Generator 1632 项通过。

当前无未验收代码；下一轮从已确认候选继续，先收拢 Generator 的重复 nominal 规则（M004）。基线与已完成轮的验证均使用准确存储 pin 的 package-mode assets，未依赖 sibling HEAD。

## 线索与裁决

| 线索 | 证据与当前判断 | 状态／下一步 |
|---|---|---|
| M001：PrivateContainer gate 重复写死存储 pin | `Run-PrivateContainerProbe.ps1:376` 另写版本／修订；`Prepare-Storage.ps1` 已按 props 验证六包与 provenance；最近 pin 升级须额外同步 runner | 待核对：先确定有限 gate 是否有独立锁定来源的需要，再决定删除重复判断或保留。不先扩通用 pin 框架 |
| M002：Build kind／arity 重复验证 | `SchemaHistoryTool.ValidateClosure` 首遍已覆盖两段重复规则；删除 packed family 表及每节点全 records arity 扫描 | 已处理／第 1 轮：根 build 零警告错误，history／Build 353 项通过（含新增 4 种 direct/nested 与声明顺序反例，Publish 零输出、Verify 保原 history bytes）。源码独立复核通过 |
| M003：ToShared 操作内 catalog 重载 | 子调查发现 `BranchSaveSession.Prepare` 的 Validate／Prepare 均 CollectToShared，重复读取 Head.S 与 latest shared | 待复核及设计质疑：Capture 前预检必须保留；不以旧记录或缓存命中代替准确 view 核验 |
| M004：SG kind／arity 同形重复 | `Ancestry.ValidateHistoryClosure`／`TemplateHistory.ValidatePatternReferences` 在 ValidateTemplateNominalShapes 后再建 family 表、逐 named 全表扫描；主线程已读对应源码。DG0019 当前无数量／精确位置合同 | 下轮裁决／实施：去两段重复，但不能首检失败就跳过独立 exact错误；考虑 nominal首检累计冲突，保 kind／arity Error拒绝、参数与 missing/cycle/depth诊断。不建诊断去重框架；补独立冲突正反例 |
| M005：Build manifest 读两次 | ParseManifest 解析候选后丢弃 reference hash，LoadAvailable 再读取；主线程已核全部生产／测试调用。可同次解析 out hash，移除二次读取 | 已确认待实施：保 owned BOM／CRLF与refs canonicalLF原hash规则，补组合正控；未证明实际并发错误或实测加速，不加锁或快照框架 |
| M006：仅测试消费的旧 codec 留在产品程序集 | Serialization 的 internal ValueSlotCodec／ArrayElementCodec 消费者仅 Serialization.Tests；Runtime/SG 使用静态 IStateOps | 待复核消费者闭包：可移测试 helper、保机制见证并删除无用 IVT；不恢复旧 runtime 路由 |
| M007：FrozenState 可收窄 owned buffer 出口 | 原三个 backing-array getter 已删；Array/List 和活动 ListDeltaReplayProbe 两处 friend 读取使用已有 span | 已处理／第 2 轮：根与活动实验 build 零警告错误，根四个测试项目 2,694 项全部通过（含隔离、畸形 Delta、浮点、Upgrade）。源码无旧 getter；仅普通 C# 边界收窄，不声称抵御 Unsafe |
| M008：默认保存重复 checkout 来源核验 | Repository 的 RequireSchemaWriter 已 RequireCheckoutSource，save/discard try 内又重复，无中间 callback | 待核串行／reentry 与 ToShared 分支区别，保每入口完整 lineage 拒绝 |
| M009：FrozenState ownership bool 的 false 分支 | 三个 internal constructor 的 16 个调用均显式 true；public constructor 先复制，其他 producer 交付新建数组 | 待核转移后无写入：与 M007 分开，不能新增会改变 `new(array)` 复制语义的构造器重载 |
| M010：逐对象链读取再次完整 ReadGraph | 原 decoder 每行再次完整 ReadGraph；现改 Store eager wrapper＋private lazy core，由实际完整 snapshot驱动 | 已处理／第 3 轮：根 build零警告错误，相关 2,544 项通过（新增19）。counter见证及bulk prior/role/cycle/H/Basecutoff、资源停止、真实reader callback反例通过；不将手工 snapshot 当验证证书 |

已有 TODO 多为等待实测的缓冲/继承/字典性能项；不凭 TODO 本身重开优化。已返回的候选按表逐项复核，不继续积累新线索而搁置当前可处理项。

## 验收与下一步

代码变更后执行根 solution build 和相关测试；包边界变化另走准确新包消费者。构建来源、检查覆盖、未覆盖项和结果在本文件或链接证据中记录。

第 1 轮证据：`.artifacts/maintenance/20261015/round1-build-final.log`、`round1-tests-final.log`、`round1-history-build-final.trx`。检查的是源码 Build 工作流和历史正反例，没有制作新 G 或授新的普通包资格。
本地提交：`eedd9f9`（含初始化本任务状态）。

第 2 轮证据：`.artifacts/maintenance/20261015/round2-build.log`、`round2-probe-build.log`、`round2-tests.log`、`round2-test-results/`（四份 TRX 已逐份核 total＝passed、failed＝0）。活动实验此次编译，未实跑 replay，不授性能／新包资格。
本地提交：`c3b85b0`。

第 3 轮证据：`.artifacts/maintenance/20261015/round3-build.log`、`round3-persistence.trx`、`round3-runtime-generator.trx`（912＋1632，failed／skip＝0）、对应 tests日志。源码及测试独立复核、主线程完整diff与19个新用例的实际TRX结果核验通过。未制作新 G、不授性能／普通包资格。

下一步：第 4 轮处理 M004，再选择 M005／M006／M001 的局部规则收拢；M003 在 M010 后保首遍完整冲突拒绝再审复用方案，M008／M009 仍独立待核。七条未处理线索均有可推进动作，Goal 保持 active；不把此三轮停点作为总体收敛。

### M003 设计审阅边界

目标是减少单次 ToShared 的重复完整 catalog 物化，不承诺完整保存加速。来源：用户允许安全重构；当前 `BranchSaveSession`／`SchemaCatalogWorkspace` 和 existing ToShared tests 是行为证据，DB-087 只是合同导航。

必须保留：owner producer 窗口内从实际 Head.S 与 latest shared 各自加载；完整源目录（含不可达声明）同 key 异形在业务 Capture 前拒绝；Capture 新布局仍合并／验证；meta priors 与 ID 域只取 latest shared；Discard／失败不安装 accepted/dirty；同库串行，callback 重入拒绝、fault/lifetime 保持。没有长期 cache 或新的公开 API／wire 需求。

候选 A：保现状两遍重载；候选 B：本操作首次加载并预检后，仅复用这些精确 views，capture 后再核完整新增布局；候选 C：额外 promotion plan 缓存闭包。仍排队，先处理更底层的 M010；尚无最终设计裁决。

### M010 设计审阅边界

目标：一次完整 decoder 不为每个 live 对象重做整图物理关系／membership 检查和 map 复制。当前唯一生产链调用者为 StoredGraphDecoder，其他地址入口用于测试／机制实验；没有公共 API、wire、跨操作缓存或新 graph-wide certificate 要求。

必须保留（当前源码／回归与 AGENTS）：开始时完整 ReadGraph，错误 external head 即使不可达也拒绝；每条链仍检查 owner/fault/lifetime、membership、准确 prior、State/meta source role、cycle 与实际 H；每个 typed body 保准确 origin S／selected layout、全部 refs 与 dictionary lookup 检查；不跳过 reader／Upgrade、不交付部分图。操作串行且 completed frames append-only；不承诺离线 rescue 后复用旧视图。

备选：A 保逐行重读；B 私有链 core 仅由 Store 自己创建的本次 ReadGraph 驱动，提供窄批量遍历供 decoder；C 内部接受任意带 SourceOwner 的 StoredGraphRevision。重点质疑 C：constructor 是 internal，owner 引用本身不能证明任意手工快照已验证；也不为 B 引入长期 cache／证书／生命周期框架。

裁决（独立三角色第一轮＋批量形状交叉质询，主线程按源代码裁决）：采用 B 的 eager wrapper `ReadObjectChains(address, out snapshot)`，立即完整 ReadGraph，再由 private lazy iterator／private chain core 逐条读取。保地址式单链入口；core 开头 EnsureUsable 后保持原链检查。需求怀疑者由 callback 方案修订为支持 iterator，无残余产品取舍。不提前收集所有 chains、不新增读租约或长期状态。

最强否决证据：C 可手工提供同 owner 但缺坏 external head 或替换 selected batch 的 map，因而绕过实际所选图；B 只用 Store 自建私有局部 snapshot 避免该输入。首遍完整图不能删，external Delta 的浅声明不取代逐链 prior/role/cycle/H。row1 reader 关闭／fault owner 后，row2链须拒；最后 reader 后交付纯 owned结果的最终存活保证不属于原实现，本片不扩这项合同。

最小验收：实际 Decoder 的多对象 Base-only graph，在 cache off 下 selected graph decode／directory materialization 各一次、warm 下复用单次完整检查；全 rows/head/H 保持。新批量路径覆盖不可达坏 head、坏 prior/role/cycle/实际冗余H，以及迭代两项间关闭／fault资源。既有 typed 准确 origin/selected、全refs/dictionary与晚期拒绝回归继续通过。不能以旧地址入口测试替代新路径，也不把计数改善声称为总体保存／内存收益。

### 已排除或保留的线索

- `RepositoryResources.Dispose` 的 `_committing` hook 反例：未发现正常公共保存入口的触发；公开 Repository 的 busy guard 阻止 Dispose、独立公开 resources 未公开保存。当前保留，只有未来公开外部 owner／commit callback 才重访，不将内部 hook 当已证产品 bug。
- CapturedGraph／PreparedCapturedGraph 的 FrozenList 小重复：跨程序集统一会扩大形状，当前不处理；出现更多消费者或规则漂移时再核。上述保留结论属于初次调查裁决，不额外计一轮。
