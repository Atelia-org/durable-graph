# DurableGraph 循环维护

## 目标与执行约束

- 目标：逐轮消除有证据的技术债或实施有真实收益的安全重构，直至已知线索均有妥善处理的结论。范围覆盖当前产品源码、测试、构建与活动实验；不把路线图中的条件延期当作自动实施授权。
- 用户指定本文件为任务状态入口；使用 `convergent-maintenance`，复杂实施按需 `bounded-delegation`，设计问题优先 `dialectical-simplification`。
- 本次循环已收敛；2026-10-15 启动，已完成 **9 轮**。只有完成复核、验收和收尾的维护结果才计一轮；十条已知线索均有实施或保留裁决。
- 草稿窗口：不承担旧数据或 API 兼容；仍保完整帧 append-only、正常 FS/VS 恢复、准确来源、发布 outcome、fault/reentry/lifetime 检查。保留他人工作，不推送或发布。
- 产品事实与导航：[PROJECT-STATE](../src/PROJECT-STATE.md)；长期约束和条件延期分别归目标设计与路线图，不在此复制。

## 当前工作

初始核对：工作树干净，HEAD `0ac20ba`；此前 DB-092 有限命名重构已收尾。存储 pin 为 `0.1.3-dev.20261014.g3de8fb0`，实际构建须使用其完整本地 feed 或显式源码模式。

九项实施均已验收；M009 在逐一核对 16 个 ownership 调用后裁决保留，其他调查保留项见末段。产品变化只收窄重复验证／读取和无产品消费者的旧机制，保完整来源、声明／布局、逐链 H／typed refs、Capture 前后合并、寿命／故障和发布边界。当前产品工作集已同步，详细结果与证据各自只查下表及验收段。

最终源码集成回归为四项目 2,741 项全通过，根 build 零警告错误。产品提交 `831a279` 的新 G 与准确 S pin 在仓库外独立 feed/cache 中通过四次真实 Publish／Verify Rebuild 与七个冷消费者；主线程另核 source sidecars、包／缓存／加载 DLL hash、history 和只读文件快照。包验收后已重新 restore 正常任务配置并完成根构建；包资格只覆盖固定 List／Nullable inline、private 升版、冷增量和完整 ToShared 故事，不外推故障矩阵／全容器或性能。

## 线索与裁决

| 线索 | 证据与当前判断 | 状态／下一步 |
|---|---|---|
| M001：PrivateContainer gate 重复写死存储 pin | Prepare-Storage 已按唯一 props 验证准确六包与 provenance，runner 的固定三行无独立权威；活动用户故事随选定 pin 实跑 | 已处理／第 9 轮：重复 pin 已删，脚本 AST／准确 feed Prepare／新 G 普通包门禁均通过；四次真实 Rebuild、七个独立进程 exit0，包与加载 DLL／source／history／readonly 字节独立核验通过，最终根 build 零警告错误 |
| M002：Build kind／arity 重复验证 | `SchemaHistoryTool.ValidateClosure` 首遍已覆盖两段重复规则；删除 packed family 表及每节点全 records arity 扫描 | 已处理／第 1 轮：根 build 零警告错误，history／Build 353 项通过（含新增 4 种 direct/nested 与声明顺序反例，Publish 零输出、Verify 保原 history bytes）。源码独立复核通过 |
| M003：ToShared 操作内 catalog 重载 | 单一同步 wrapper 实际 Load Head.S 与 latest shared 各一次；保完整 precheck／postcheck 和 target Builder | 已处理／第 5 轮：根 build 零警告错误，921＋11 项通过。counter、新 root layout 的 late 冲突、Event-only 四资源停止与 owner 重入均通过，失败不安装／不消费 dirty；不承诺完整保存加速 |
| M004：SG kind／arity 同形重复 | packed表和逐named全表扫描已退出；单一 nominal 索引保首值、累计record/field冲突，参数／exact独立 | 已处理／第4轮：根build零警告错误，942项通过。14个实际RunGenerator用例含排序前后direct/nested正反例、单phase单诊断、独立family/field/cycle；不建诊断去重框架 |
| M005：Build manifest 读两次 | ParseManifest 同次 out reference hash，LoadAvailable 无 owned 路径，二读入口已删除 | 已处理／第 6 轮：根 build 零警告错误，351 项通过；owned BOM×CRLF 四组合及 reference BOM 拒绝均已核。保各层完整 closure 与 accepted bytes；不授实测加速或并发错误资格 |
| M006：仅测试消费的旧 codec 留在产品程序集 | 全部实际消费者均在 Serialization.Tests；两个 codec 已迁测试 helper，产品保静态 body 路由 | 已处理／第 7 轮：根 build 零警告错误，133＋71 项通过；精确逻辑比对、消费者闭包、产品 DLL metadata 和链接复核通过，三个无用 IVT 已删。DB-020 原时期正文／证据保持 |
| M007：FrozenState 可收窄 owned buffer 出口 | 原三个 backing-array getter 已删；Array/List 和活动 ListDeltaReplayProbe 两处 friend 读取使用已有 span | 已处理／第 2 轮：根与活动实验 build 零警告错误，根四个测试项目 2,694 项全部通过（含隔离、畸形 Delta、浮点、Upgrade）。源码无旧 getter；仅普通 C# 边界收窄，不声称抵御 Unsafe |
| M008：默认保存重复 checkout 来源核验 | 默认 save/discard 复用 RequireSchemaWriter 的准确 Head／最近 State／RootId 检查；ToShared 保 try 内检查 | 已处理／第 8 轮：根 build 零警告错误，四项目 2,741 项通过；独立复核确认串行／无 callback、模式／寿命／错误顺序与 owner publication 边界保持，不改手工 editing 的 owner admission 顺序 |
| M009：FrozenState ownership bool 的 false 分支 | 16个true调用已逐一核：3个public先复制、13个内部producer完成写入才转移；Dictionary后续Index仅span读取并复制key编码。未找到可写泄漏／转移后写入 | 已裁决保留：当前仅3个未用分支，M007已收窄数组出口；为删bool增加工厂并迁移16处没有足够当前收益。出现ownership误用／新创建来源／构造合同分叉时重访，不计额外实施轮 |
| M010：逐对象链读取再次完整 ReadGraph | 原 decoder 每行再次完整 ReadGraph；现改 Store eager wrapper＋private lazy core，由实际完整 snapshot驱动 | 已处理／第 3 轮：根 build零警告错误，相关 2,544 项通过（新增19）。counter见证及bulk prior/role/cycle/H/Basecutoff、资源停止、真实reader callback反例通过；不将手工 snapshot 当验证证书 |

已有 TODO 多为等待实测的缓冲/继承/字典性能项；不凭 TODO 本身重开优化。已返回的候选按表逐项复核，不继续积累新线索而搁置当前可处理项。

## 验收与下一步

代码变更后执行根 solution build 和相关测试；包边界变化另走准确新包消费者。构建来源、检查覆盖、未覆盖项和结果在本文件或链接证据中记录。

第 1 轮证据：`.artifacts/maintenance/20261015/round1-build-final.log`、`round1-tests-final.log`、`round1-history-build-final.trx`。检查的是源码 Build 工作流和历史正反例，没有制作新 G 或授新的普通包资格。
本地提交：`eedd9f9`（含初始化本任务状态）。

第 2 轮证据：`.artifacts/maintenance/20261015/round2-build.log`、`round2-probe-build.log`、`round2-tests.log`、`round2-test-results/`（四份 TRX 已逐份核 total＝passed、failed＝0）。活动实验此次编译，未实跑 replay，不授性能／新包资格。
本地提交：`c3b85b0`。

第 3 轮证据：`.artifacts/maintenance/20261015/round3-build.log`、`round3-persistence.trx`、`round3-runtime-generator.trx`（912＋1632，failed／skip＝0）、对应 tests日志。源码及测试独立复核、主线程完整diff与19个新用例的实际TRX结果核验通过。未制作新 G、不授性能／普通包资格。
本地提交：`d522626`。

第 4 轮证据：`.artifacts/maintenance/20261015/round4-build.log`、`round4-tests.log`、`round4-generator-history.trx`；942项全通过，新增14项已按实际TRX逐项核通过。没有计时／普通包资格。

第 5 轮证据：`.artifacts/maintenance/20261015/round5-build.log`、`round5-persistence.trx`、`round5-integration-actual.trx` 及相应 tests 日志；921＋11 项全通过，9 个新增 case 已核实际 TRX。最初按文件名构造的 integration filter 未匹配 partial class，不计验收，已改按实际方法名运行。源码与测试独立复核通过；未授完整保存性能或新包资格。

第 6 轮证据：`.artifacts/maintenance/20261015/round6-build.log`、`round6-tests.log`、`round6-history-build.trx`；351 项全部通过，新增 5 个 case 已按 TRX 核执行／通过。单次读取由实际调用链证明，没有运行时 I/O instrumentation 或性能／普通包资格。

第 7 轮证据：`.artifacts/maintenance/20261015/round7-build.log`、`round7-serialization.trx`、`round7-static-body.trx` 及 tests 日志；133＋71 项全通过。`round7-product-metadata.json` 记录实际产品 DLL SHA 与六个 type definitions，旧 codec／委托定义及 IVT 类型引用均不存在。未授 DLL 大小／速度或新包资格。

第 8 轮证据：`.artifacts/maintenance/20261015/round8-build.log`、四份 `round8-*-tests.log` 和 `round8-test-results/` 四份 TRX；Persistence 921、Runtime／Generator 1651、Serialization 133、Storage 36，逐份核 total＝passed、failed／skip＝0。这是当前已整合产品源码回归，尚未授新包资格或保存性能。

第 9 轮证据：`.artifacts/maintenance/20261015/round9-package-path.txt` 导航到 `W:\TEMP\dg-maintenance-private-container-20261015-d4d09629`；新 G 为 `0.0.0-private-container-e2e.20261014173624.9436`，产品来源 `831a279fc019fcb9419b4ec1def77d4b82a2bd88`，S／来源与本文件 pin 一致。外部 `result.json`、`run.log`、source sidecars、四次 Rebuild 日志及七份 evidence 保原始结果。主线程 `round9-accept-package.ps1` 与 `round9-independent-acceptance.json` 另核实际工件；result SHA256 `AB72F8763A82FD2F4711B9E7605DD0917F9BDCE60BACE61BC770658AD3941D12`，witness SHA256 `AA42566D2C786584E3F8974BF0749173B1E892ED509EC876F17E6D5426AE168B`。新包 Serialization metadata 也无旧 codec／IVT。根正常配置恢复与构建见 `round9-root-restore.log`、`round9-root-build.log`，0 warnings／errors；产品源码未再改变，不重复第 8 轮全套测试。未推送或公开发布包。

完成审计：M001–M010 均已实施验收或有具体保留理由，调查排除项无剩余阻断；未把路线图条件延期、无当前消费者的性能优化或旧包故事当作本次必经工作。必要源码／包／链接检查全部通过，任务状态及产品工作集已收尾；本次循环没有下一轮必经施工。后续只在表列重访条件或新的实际消费者证据出现时另选增量。

### M003 设计审阅边界

目标是减少单次 ToShared 的重复完整 catalog 物化，不承诺完整保存加速。来源：用户允许安全重构；当前 `BranchSaveSession`／`SchemaCatalogWorkspace` 和 existing ToShared tests 是行为证据，DB-087 只是合同导航。

必须保留：owner producer 窗口内从实际 Head.S 与 latest shared 各自加载；完整源目录（含不可达声明）同 key 异形在业务 Capture 前拒绝；Capture 新布局仍合并／验证；meta priors 与 ID 域只取 latest shared；Discard／失败不安装 accepted/dirty；同库串行，callback 重入拒绝、fault/lifetime 保持。没有长期 cache 或新的公开 API／wire 需求。

已裁决（独立三角色＋形状交叉质询，主线程源证据裁决）：采用操作内复用。单一同步 `PrepareToSharedOwned(store, headRoot, sharedRoot, Func<IEnumerable<ObjectLayout>> captureLayouts)` 内实际Load两view→完整preCollect→caller Capture→完整postCollect→Builder(target)；view helper保持private，不接受任意view，不返回延迟delegate。业务root、Capture、abort、业务Prepare仍由typeddriver拥有。

保原 Capture 后的精确可用性边界：直接调用Store现有 `EnsureUsable`（只将其改internal），覆盖Store自身Dispose及owner/backing故障，不新增guard类型。仅取owner的Store getter会漏Store自身已Dispose的提前拒绝；不以最终零append检查替代这项原边界。

最小验收：独立Base-only真实meta catalogs、cache0下同步方法两次GraphDecode/DirectoryMaterialization（两根各一次），callback恰一次；既有完整Head导入、不可达/dirty冲突零Capture、Discard、Event与State来源、ID重映射、两ref失败保持。新增Capture才引入新合法layout／同key异形冲突；callback重入及Store/owner/backing停止须在业务bodyPrepare或pending/append前拒绝。不承诺整体保存加速。

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
- `EnsureEditingAllowed(checkout)` 的来源检查跨 owner admission，M008 只去掉普通 save/discard 在无 callback 区间的重复。手工 editing 当前保原拒绝顺序；没有已证故障，不作为剩余必经施工，只在其 admission 合同改变时重访。
