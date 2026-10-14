# DurableGraph 循环维护

## 目标与执行约束

- 目标：逐轮消除有证据的技术债或实施有真实收益的安全重构，直至已知线索均有妥善处理的结论。范围覆盖当前产品源码、测试、构建与活动实验；不把路线图中的条件延期当作自动实施授权。
- 用户指定本文件为任务状态入口；使用 `convergent-maintenance`，复杂实施按需 `bounded-delegation`，设计问题优先 `dialectical-simplification`。
- Goal 持续执行，无固定轮数上限；2026-10-15 启动，已完成 **1 轮**。只有完成复核、验收和收尾的维护结果才计一轮。
- 草稿窗口：不承担旧数据或 API 兼容；仍保完整帧 append-only、正常 FS/VS 恢复、准确来源、发布 outcome、fault/reentry/lifetime 检查。保留他人工作，不推送或发布。
- 产品事实与导航：[PROJECT-STATE](../src/PROJECT-STATE.md)；长期约束和条件延期分别归目标设计与路线图，不在此复制。

## 当前工作

初始核对：工作树干净，HEAD `0ac20ba`；此前 DB-092 有限命名重构已收尾。存储 pin 为 `0.1.3-dev.20261014.g3de8fb0`，实际构建须使用其完整本地 feed 或显式源码模式。

第 1 轮已验收 M002：删去 Build history 闭包验证中已被首遍索引完整覆盖的两段重复规则；保格式、API、参数作用域、exact 依赖、深度、祖先和导入／accepted／current 三次闭包检查。独立源码复核确认删除的拒绝条件均被首遍包含，未把其他依赖算法声称为线性。

基线包模式根 solution build 已通过，零警告／错误（`.artifacts/maintenance/20261015/baseline-build.log`）。持久化和运行时只读调查继续，返回候选经主线程复核后排队。

## 线索与裁决

| 线索 | 证据与当前判断 | 状态／下一步 |
|---|---|---|
| M001：PrivateContainer gate 重复写死存储 pin | `Run-PrivateContainerProbe.ps1:376` 另写版本／修订；`Prepare-Storage.ps1` 已按 props 验证六包与 provenance；最近 pin 升级须额外同步 runner | 待核对：先确定有限 gate 是否有独立锁定来源的需要，再决定删除重复判断或保留。不先扩通用 pin 框架 |
| M002：Build kind／arity 重复验证 | `SchemaHistoryTool.ValidateClosure` 首遍已覆盖两段重复规则；删除 packed family 表及每节点全 records arity 扫描 | 已处理／第 1 轮：根 build 零警告错误，history／Build 353 项通过（含新增 4 种 direct/nested 与声明顺序反例，Publish 零输出、Verify 保原 history bytes）。源码独立复核通过 |
| M003：ToShared 操作内 catalog 重载 | 子调查发现 `BranchSaveSession.Prepare` 的 Validate／Prepare 均 CollectToShared，重复读取 Head.S 与 latest shared | 待复核及设计质疑：Capture 前预检必须保留；不以旧记录或缓存命中代替准确 view 核验 |
| M004：SG kind／arity 同形重复 | `DurableGraphGenerator.Ancestry`／`TemplateHistory.ValidatePatternReferences` 在 ValidateTemplateNominalShapes 后再建 family 表、逐 named 全表扫描 | 待核：错误拒绝保持，但 DG0019 数量／定位可能变化，独立确认诊断约束后再处理 |
| M005：Build manifest 读两次 | ParseManifest 解析候选后丢弃 reference hash，LoadAvailable 再 ReadManifestText 取 hash | 待核：考虑一次解析保 candidates＋hash；目前未证明实际并发错误，不加锁或快照框架 |
| M006：仅测试消费的旧 codec 留在产品程序集 | Serialization 的 internal ValueSlotCodec／ArrayElementCodec 消费者仅 Serialization.Tests；Runtime/SG 使用静态 IStateOps | 待复核消费者闭包：可移测试 helper、保机制见证并删除无用 IVT；不恢复旧 runtime 路由 |
| M007：FrozenState 可收窄 owned buffer 出口 | runtime 调查发现 OwnedEntries 零消费者，其他 owned 数组消费者可能可用既有 ReadOnlySpan | 待核真实 alias／mutation 需求与测试；不凭 getter 名字判断可删 |
| M008：默认保存重复 checkout 来源核验 | Repository 的 RequireSchemaWriter 已 RequireCheckoutSource，save/discard try 内又重复，无中间 callback | 待核串行／reentry 与 ToShared 分支区别，保每入口完整 lineage 拒绝 |
| M009：FrozenState ownership bool 的 false 分支 | 三个 internal constructor 的 16 个调用均显式 true；public constructor 先复制，其他 producer 交付新建数组 | 待核转移后无写入：与 M007 分开，不能新增会改变 `new(array)` 复制语义的构造器重载 |
| M010：逐对象链读取再次完整 ReadGraph | `StoredGraphDecoder.Read` 已 ReadGraph，随后每 live 行调用地址式 ReadObjectChain 再 ReadGraph；后者遍历全部 heads 且复制 map | 优先设计质疑：减少本次 decoder 的重复全图核验，保首遍完整内容、逐链 exact prior/source/cycle/H 及 owner/fault/lifetime；任意手工 internal snapshot 不能作为验证证书 |

已有 TODO 多为等待实测的缓冲/继承/字典性能项；不凭 TODO 本身重开优化。其余调查候选尚待返回和复核。

## 验收与下一步

代码变更后执行根 solution build 和相关测试；包边界变化另走准确新包消费者。构建来源、检查覆盖、未覆盖项和结果在本文件或链接证据中记录。

第 1 轮证据：`.artifacts/maintenance/20261015/round1-build-final.log`、`round1-tests-final.log`、`round1-history-build-final.trx`。检查的是源码 Build 工作流和历史正反例，没有制作新 G 或授新的普通包资格。

下一步：第 2 轮处理 M007 的冻结 buffer 出口，保复制与稀疏 Delta 原先隔离；先设计质疑 M010，再处理其保存／恢复共用热点。M003 的收益可能受 M010 影响，仍排队，不先引入 promotion plan。

### M003 设计审阅边界

目标是减少单次 ToShared 的重复完整 catalog 物化，不承诺完整保存加速。来源：用户允许安全重构；当前 `BranchSaveSession`／`SchemaCatalogWorkspace` 和 existing ToShared tests 是行为证据，DB-087 只是合同导航。

必须保留：owner producer 窗口内从实际 Head.S 与 latest shared 各自加载；完整源目录（含不可达声明）同 key 异形在业务 Capture 前拒绝；Capture 新布局仍合并／验证；meta priors 与 ID 域只取 latest shared；Discard／失败不安装 accepted/dirty；同库串行，callback 重入拒绝、fault/lifetime 保持。没有长期 cache 或新的公开 API／wire 需求。

候选 A：保现状两遍重载；候选 B：本操作首次加载并预检后，仅复用这些精确 views，capture 后再核完整新增布局；候选 C：额外 promotion plan 缓存闭包。仍排队，先处理更底层的 M010；尚无最终设计裁决。

### M010 设计审阅边界

目标：一次完整 decoder 不为每个 live 对象重做整图物理关系／membership 检查和 map 复制。当前唯一生产链调用者为 StoredGraphDecoder，其他地址入口用于测试／机制实验；没有公共 API、wire、跨操作缓存或新 graph-wide certificate 要求。

必须保留（当前源码／回归与 AGENTS）：开始时完整 ReadGraph，错误 external head 即使不可达也拒绝；每条链仍检查 owner/fault/lifetime、membership、准确 prior、State/meta source role、cycle 与实际 H；每个 typed body 保准确 origin S／selected layout、全部 refs 与 dictionary lookup 检查；不跳过 reader／Upgrade、不交付部分图。操作串行且 completed frames append-only；不承诺离线 rescue 后复用旧视图。

备选：A 保逐行重读；B 私有链 core 仅由 Store 自己创建的本次 ReadGraph 驱动，提供窄批量遍历供 decoder；C 内部接受任意带 SourceOwner 的 StoredGraphRevision。重点质疑 C：constructor 是 internal，owner 引用本身不能证明任意手工快照已验证；也不为 B 引入长期 cache／证书／生命周期框架。
