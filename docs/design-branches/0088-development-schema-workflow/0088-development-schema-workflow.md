# DB-088：固定用户基线上的 Schema 开发与定稿

> 状态：2026-10-13，实施与限定验收完成：生产特例退出、测试迁移、源码回归及三条新 G 普通包故事均通过。承接 DB-087 完成后的用户需求校准；后续易用性与版本发布机制仍延期。

## 1. 问题与用户决定

问题：能否用现有分支、private 保存与 Upgrade，从同一份已发布 V1 用户快照建立多个内容不同的 V2 草稿，保持原始存活对象的身份，最后只接纳定稿 V2？

以下来自本轮用户明确决定：

- 首选开发流程是固定 shared baseline → 每次模型调整后建立新的 dev fork → baseline 版本加一的当前草稿。各 fork 的草稿可同版本异形。
- 原用户图仍存活对象的 ObjectId、共享与循环需要保持；开发新增数据通常可以重建或重放。
- 如果要继承上一草稿的数据继续改模型，应递增 V3、V4；同一已接纳目录及其后续增量中的同 SchemaKey 定义不能改形。
- 对外只交付一个定稿新版本；开发用主／次版本号只是备选，不预先加入身份或版本平台。
- 先验证完整开发故事，再按证据删减 DB-087 的同 key 新根替换特例。

当前实现将不可改形约束作用于已接纳目录：两个 fork 从尚无该 key 的共同 baseline 独立新增时可接纳不同形状，完整合并仍须检查冲突；尚未接纳的 dirty 候选可在 Discard 后更正。这些实现边界来自 `P0CatalogWorkspace.CheckAccepted`／`CollectToSharedOwned` 和对应回归，不是增加新的全仓库唯一形状政策。

DB-087 的 append-only、共同 writer、准确 origin／selected／execution S、完整 source／prior／OwnedBatch、refs 原子发布、single writer 与故障寿命合同全部保持。无旧数据／API 兼容负担。

## 2. 最小路线与验收

使用 `CreateBranch(name, baselineCheckpoint)` 与 `CheckoutPrivate(name)`；已有 `Fork` 返回的普通 checkout 亦可关闭后重新明确选择 private。不通过 `CreatePrivateBranch(initialState, overrides)` 实现身份连续。

1. 真 SG V1 程序保存具有共享、环与 List 的用户图，冻结 baseline checkpoint、对象身份与 shared ref。
2. 仅带已发布 V1 compiler history，分别编译 V2 草稿 A 与草稿 B。两者从同一 baseline 建立各自 private fork，执行 V1→当前 V2 Upgrade。
3. 检查原用户身份、实际业务 Parent／内容来源、升级 Base、retained heads 与 private ref2 不变；全部关闭后各用匹配代码冷开续写。
4. 错误草稿代码不能重新解释另一 fork 已接受的 V2。只读失败不写盘，不用 latest shared 补解释。
5. winner 从干净 baseline 起最终 fork，接纳其 compiler V2 history，再执行完整 ToShared。Shared 只新增所选定稿 V2，完整保留 baseline 声明，不导入落选草稿；冷开后继续增量保存。
6. 实际 MSBuild 验 `Off` 仍加载 V1 且不写候选、最终 `Publish`／`Verify` 只形成 V1＋定稿 V2，旧文件字节不变。

编译 history 接纳与运行时 shared catalog 接纳是两次不同动作。实验必须分别观察，不把 Roslyn 直接输入或 source build 当成实际 MSBuild／普通包资格。

## 3. 开发 history 与发布边界

当前默认 `Publish` 会在成功 CoreCompile 后冻结候选。现有 `DurableGraphSchemaHistoryMode=Off` 仅关闭候选写入／Verify，旧 `.dgschema` 仍作为 AdditionalFiles 输入、继续校验；不能用 Off 忽略已存在的同版本异形记录。

最小开发路线保已发布 history，未定稿候选不进入正式 history；不同试验的 build 输出与临时 history 隔离。需要升 V3 时，可以在该 dev 自己的 history 接纳其 V2，再保留相邻 reader／Upgrade；不修改正式 V1 或已接受的持久 bytes。

完整 ToShared 导入 Head.S 的全部声明，包括不可达定义及私有 V3/V4。它没有“只发布最后一个版本”的过滤语义。现机制下，只交付最终 V2 应从原 V1 建立干净 final fork，并提供 V1→final V2 转换与必要重放。

若未来要求直接交付开发 V4、但不携带 V2/V3 模板与 Upgrade，须另设计 release 编号与内部迁移边界；当前相邻 history／Upgrade 机制不能证明该能力。主／次版本号、自动压缩迁移链、选择性目录推广均延期，不改变此次实验。

## 4. 复杂性裁决与删减候选

三方独立评审及交叉质询已收敛于先验故事、迁移语义覆盖后删特例。主线按实际恢复／发布与 Build 接线复核；以下记录已保留的核心和已退出的特例：

| 项目 | 目标裁决 | 理由与验收要求 |
|---|---|---|
| 准确 baseline 恢复、身份导入与 private 增量 | 保留 | 真实用户复现场景所需，不能用空来源的新根替代 |
| fixed 员工引导、meta ObjectId、完整布局与来源校验 | 保留 | 各 fork 可有不同 V2 形状，旧 bytes 仍须准确解释 |
| 完整 ToShared 与两 ref CU | 保留 | 发布一致性合同；最终干净 fork 控制导入范围 |
| 同 key Schema overrides／完整 container replacements | 已删除，源码及新 G 已验 | 必要目录、故障与冷恢复见证已迁移；正常 baseline／Upgrade 替代 |
| 私有新根 bootstrap lookup／延迟空来源建根／声明替换 | 已随入口删除 | 普通 baseline 恢复承担首选流程；保普通 Shared 新图及 Event-only 合法空 State 工作区 |
| 主／次版本、draft token、自动 merge／过滤 | 延期 | 没有此次实验的必要消费，不预建框架 |

删除不能连带丢失完整闭包、实际来源、Event／Discard、发布失败及旧帧保持的回归。现有消费者是复杂性成本的证据，不构成兼容约束。施工顺序为先形成新正控故事，再迁移非特例语义见证，最后删除 API 与专用状态；不为保存旧测试重新建设替代入口。

`CreateBranch(checkpoint)` 是独立 ref 发布，随后 `CheckoutPrivate` 失败不会回滚这个已创建名字；该分支仍准确指向 baseline，可在正确环境中重新签出。不能把旧复合新根入口的“恢复失败不留名字”合同套到两个公开操作上。身份验收必须使用区别于空会话分配的源 IDs，并核实际 source，而非只比数字偶合。

已完成的替换边界：

| 范围 | 替换边界 |
|---|---|
| `SplitRepositoryConsumer`／`PrivateContainerConsumer` | 改为 baseline checkpoint→private 签出；前者保完整目录／ToShared，后者将 Point 的同版异形改为正常下一版及显式 inline／List 元素 Upgrade，不再消费容器 replacements |
| `PrivateSchemaViewGeneratorTests`／`PrivateSchemaPromotionGeneratorTests`／`PrivateContainerSchemaReplacementGeneratorTests` | 保身份／闭包／冷续／完整推广规律，改为不同版本；删除只属于覆盖／合流／removal-only 特例的见证，不人为增加新框架 |
| `PrivateSchemaRepositoryTests`／`PrivateSchemaPromotionTests`／`PrivateContainerReplacementTests` | 将仍必要的来源、Event／Discard、归属与发布失败断言放到普通 private 恢复／保存；两步建分支不承担旧复合建根的回滚名字合同 |
| `Repository.PrivateSchemas`／`P0Session`／`P0CatalogWorkspace` | 保 `CheckoutPrivate`／Head.S 来源及完整 ToShared，退出同 key 新根公开入口、bootstrap config／lookup 和声明替换分支；ABI、共同 writer／planner／cache 不随之重写 |

## 5. 实验结果与下一步

源码故事已执行并独立静审通过：新增 [DevelopmentSchemaWorkflowGeneratorTests](../../../tests/DurableGraph.Tests/DevelopmentSchemaWorkflowGeneratorTests.cs) 的单 Fact 使用真实 SG V1／V2A／V2B，先消费旧 ID，再替换图与 child 升根形成高位 baseline。逐 ID 核值／引用边与实际来源，覆盖 private Discard／Event 保 rewrite、两升级 Base、retained List、双方全关冷 Delta／prior／实耗 H、错误环境 readonly 零写、干净 winner 的完整 ToShared、旧帧与旧 history 保持。

准确 source mode（Storage `974fd47abb73792f9d350af87ab91a53848952eb`）整库构建零 warning／error，新故事 1 项和相关 Upgrade／ToShared／同版 history 3 项均通过；本轮未重跑 2752 项全套。日志与 TRX 在忽略目录 `.artifacts/db088-development-schema-20261013/`。首次编译的测试助手实参错误已修；不属于运行时失败或产品反例。

实际 MSBuild 与普通包故事也已通过：[DevelopmentSchemaConsumer](../../../experiments/PackageConsumerProbe/DevelopmentSchemaConsumer/README.md) 复用准确现存十包，六次成功 Rebuild、一次预期 DG0015 拒绝、五个全新进程。Off 保 V1 输入，定稿 B 才 Publish／Verify，正式 history 只有 V1＋V2B；private 两 refs、原业务历史、全部资源关闭后的读／续写和 readonly 均按其 README 的限定范围验证。完整来源／live IDs／H 与实际包／MSBuild 分层，不互相替代；当前生产源码未修改，没有新 wire／API／版本策略或新 pack。

以上是删除前实验，生产源码当时未修改，资格保其准确来源。它支持删减方向；两轮实施结果与删除后新 G 资格见 §7，不把旧包成功当作删除后资格。

仍未验证或定型：V3/V4 数据继承到单一定稿版本的直接交付／迁移压缩、主／次版本、开发新增数据重放工具及更自动的 draft／release 管理。当前已有整数版本与显式 Off 足够完成首选流程，默认 Publish 尚未修改；不以实验通过宣称这些易用性机制已实现。

<a id="6-下一轮实施合同与停点"></a>
## 6. 实施合同与验收边界

以下产品形状和源码／包见证已按两轮实施完成。此节保留完整产品／验收合同，不作为重复施工清单；最终资格见 §7。§5 的旧实验资格不代替删除后的验收。

**最终产品形状：**

- 删除整个公开 `CreatePrivateBranch(name, initialState, schemaOverrides, ..., containerReplacements)` 入口；不保空 overrides 重载、obsolete／compat adapter、internal 替代建根入口或通用 Fork 策略。首选路线为 ref-only `CreateBranch(checkpoint)`→`CheckoutPrivate`。`Fork` 则先恢复、后发布 ref，交付普通 checkout；关闭后可另行签出 private。两者保持各自失败语义，无需先造便利 API。
- `P0Session` 删除 overrides 专用构造器、`_bootstrapConfiguration`／`_bootstrapOverrides`／`_bootstrapContainerReplacements`／`_bootstrapEditingGuard` 及首次 Prepare 创建空业务 Workspace 的分支。保普通 Shared 新建图、Event-only 的合法空 State 工作区和所有真实来源恢复构造路径，不按“空来源”关键词整删。
- `P0CatalogWorkspace` 删除 `CreatePrivateForkOwned`／`LookupBootstrapSchema`、`_privateBootstrap` 和其逻辑替换表；Builder 去掉 bootstrap 参数、同 key 声明替换、`ReplaceContainers`／`_replacedDeclaration` 及一般 replacement Delta 分支。正常新增版本／容器声明、同形复用、无变化复用和 append Delta 保持；旧 Schema／完整容器布局的声明保留，新版追加共存，已接纳定义不可改形。
- 保 `UsesPrivateSchemas`／private 保存目的地选择、私有 meta 来源 Head.S、业务基线 State.S、冻结 execution view、准确 origin S、完整 ToShared 与共同 writer／owner。目录不可达声明和依赖不能因删特例而被裁剪。
- 删除的是**私有新根专用 bootstrap**，不删除 **meta 自举固定引导**：员工 DTO／fixed bindings／builtin codec、Schema 工厂、descriptor meta ObjectId、存活 Dictionary 与对象版本核心全部保留。`MetaBootstrapP0` 内部名称不构成另一次改名或整束删除任务。

**依赖顺序与交付：**

1. 先对 §4 的六组测试与两活动消费者做断言去向表，按产品规律分配见证，不要求文件、用例、观察字段或 PID 数量一一对应。区别必须保、已有回归可直接承担、只属于删除特例的断言；保真实 source／ID／闭包／Event／Discard／fault／旧帧证据，不重建 no-op／合流／removal-only／同版替换端点认证。
2. 必要 source／Event／Discard／fault 测试迁到真实 baseline 的普通 private 路径；仅模型改型／rewrite 故事需要不同版本 history／Upgrade，同形机制用例以 `Catalog.Set`／Event 产生实际变化。保 Event 为 Head、State.S 较旧时的全关冷签出，完整不可达目录和错误真实来源拒绝；Development 的单一纵向故事不替代这些见证。两步 `CreateBranch(checkpoint)` 成功后，签出失败只释放占用并保 branch 指 baseline；`Fork` 恢复失败不发布名字。
3. 删除上述公开入口和内部专用状态、调用点及仅特例用例。允许测试迁移与代码删除一起完成可构建片，不为了逐步编译留下第二产品路径。正常版本升级须带匹配的 Schema／历史 reader／Upgrade；不放松同 view 同 key 冲突或完整 layout 校验。
4. 迁移 Split／PrivateContainer 的公开故事与其有限 observer／runner。Split 将原 overrides 注入的 `PrivateUnused` 改为真实 SG Event，并在 Open 前注册其 definition；普通 private 签出后提交该 Event，使声明进入 Head.S 而 State／身份／rewrite 不推进。全部关闭后从 Event Head 冷签出，最终 ToShared 捕获普通 Widget，核对当前 State 不可达的 Event 声明仍完整导入；不能再次捕获该 Event 根来补声明、掩盖导入缺陷。普通包不借 internal `Catalog.Set`、IVT 或新公开 meta 登记入口。

   PrivateContainer 先保存 Keeper，再保存并冻结含 Keeper 声明的 main baseline；现夹具在 main/tag 后才保存 Keeper，不能原样保其“从 latest shared 继承”断言。Point V1→V2 使用一个现有 child provider、开启 `AllowNullableLifting` 的已注册规则集与 `UseListElementUpgrades`，不另建 Nullable wrapper 或容器转换 API；nominal List 引用未变时不为此强制 Root 升版。保完整旧／新 Point 与 List 声明、原 root／List 身份、升级 Base 及冷 Delta。V2 带 V1 history 应可只读升级 V1；无实际冲突时 ToShared 应成功。退出旧 missing-pair／同名修正、双向 wrong-read 和同 V1 ToShared 必冲突观察；同 V2 异形拒绝由 Development A/B 与必要源码回归承担，不另造竞争包故事或通用 inspector。
5. 删除片用明确 source mode 构建 solution 并运行相关源码回归；最终整合跑全套源码 tests。公开包重新以**新 G**消费准确 S feed，运行 Development 与已迁移的代表故事；pack 使用 package mode／独立版本与准确来源。此次旧 G 的成功不能授删除后的产品资格。冻结旧工件与归档不改写、不清理。
6. 收束活动 README／术语表／target／PROJECT-STATE／DB-087 与索引中的旧入口、替换能力和资格说明；历史证据保原版本与 successor 指针。当前待办只在路线图保留，不累积施工日志。

**迁移时最易退化的证据：**

| 规律 | 最小见证与去向 |
|---|---|
| 错误但真实的 source | 普通 fork 最初可与 main 共用 State。先让 private 接纳一条新声明并保存 State，再经 Event 接纳另一条；显式核 private State≠main State、Head.S≠State.S／shared.S，再保 `PrivateSchemaRepositoryTests.Private_raw_preparation…` 的三种拒绝，避免把正确来源当错来源 |
| 完整不可达目录与 ToShared 冲突 | `PrivateSchemaPromotionTests` 保实际 Head.S 全目录、目标 latest shared meta priors 与 descriptor 数字碰撞 remap。冲突用“baseline 尚无 K→private 接纳 B→shared 后接纳同 key 异形 A→ToShared 在 Capture 前拒绝”；核 refs／帧／baseline 不变、owner 健康且 private 仍可保存，不能提前在 Set 阶段拒绝来替代 |
| 缺 List 元素 Upgrade | 复用 [ListUpgradeTests](../../../tests/DurableGraph.Tests/ListUpgradeTests.cs)、[NullableUpgradeTests](../../../tests/DurableGraph.Tests/NullableUpgradeTests.cs) 和 [MetaBootstrapContainerUpgradeTests](../../../tests/DurableGraph.Persistence.Tests/MetaBootstrapContainerUpgradeTests.cs) 的空／全 absent 预检；迁移源码夹具补公共 `CheckoutPrivate` 缺规则负例：零输出、健康、保已创建 baseline 名字并释放占用，关闭后在匹配环境重开可签出。当前内部预检证据不自动授这项公开资格 |
| 发布 outcome／可见性／旧 owner 寿命 | private 用例保 ref1-only／ref2 不变及完整帧；Unknown 冷开旧／新、Published 后未交付和安装失败由既有 [SplitRepositoryPublicationOutcomeTests](../../../tests/DurableGraph.Persistence.Tests/SplitRepositoryPublicationOutcomeTests.cs) 与 [MetaBootstrapP0PublicationTests](../../../tests/DurableGraph.Persistence.Tests/MetaBootstrapP0PublicationTests.cs) 承担。保 fault 后 cache／borrowed Store 拒用与 fresh reopen 实 refs 裁决，不向各容器夹具复制故障矩阵，也不将 NotPublished 等同健康 |

**完成验收：**生产源码中公开新根入口与专用状态已退出，活动执行代码不再依赖 overrides／container replacements；实验中的异形 V2 草稿可各自从固定 V1 baseline 接纳，已有同分支定义保持不可变，不增加“仅限 V1→V2”的产品限制。原用户存活身份、共享／循环、真实 Base／Delta／source／prior／H、Event／Discard rewrite、完整目录和 ref1／ref2／fault／旧帧规律经对应测试证实，新 G 普通包的冷恢复与发布故事通过，活动文档一致。

此次不修改默认 Publish、不增加主／次版本或草稿迁移压缩、自动重放、选择性 ToShared、typed cache、wire／程序集改造，也不恢复十九个暂停 lanes。只有具体消费者反例、必需 Upgrade 缺口或会改变上述产品语义的选择才重新讨论；一般 fixture／observer／helper 尾修由实施者自行处理。

<a id="7-第一轮结果与第二轮入口"></a>
## 7. 实施结果与限定验收

生产四文件已退出整个新根 API、八个专用 bootstrap 状态及 Builder 声明替换；净减少约 170 行。业务／meta 保存核心、固定员工引导、普通 Shared 新图与 Event-only 空 State 保持；没有替代入口或新 wire。

| 原六组测试 | 必要规律的实际去向 |
|---|---|
| PrivateSchemaRepositoryTests | 普通 baseline fork、高位 IDs、Event Head.S 冷签出、Discard／无变化、三种真实错误来源、单 ref 故障与旧完整帧、Event-only |
| PrivateSchemaPromotionTests | 完整不可达声明、Event 后新声明从 actual Head.S 导入、descriptor 数字碰撞 remap、Capture 前真实冲突、两 ref 故障与旧帧；17 个方法／23 cases 分布于前两文件 |
| PrivateContainerReplacementTests | 删除仅替换功能用例和文件；一般故障／帧规律并入前两组，普通容器升版由下述及既有 Upgrade 回归承担 |
| PrivateSchemaViewGeneratorTests | 真实 V1→V2／身份与来源、完整声明、Event／Discard、冷 List Delta／prior／H；同 V2 异形拒读由 Development A/B 承担 |
| PrivateSchemaPromotionGeneratorTests | 真实 baseline→Holder 保存，保不可达完整目录、碰撞 remap 与 Upgrade／Event rewrite |
| PrivateContainerSchemaReplacementGeneratorTests | 真实 SG Array／Nullable List／Dictionary 升版；公共缺 List 规则两次健康拒绝、保名字并释放占用、匹配环境冷开、高位 IDs、3 Base→3 冷 Delta／准确 prior／H |

主线准确 source mode（Storage `974fd47abb73792f9d350af87ab91a53848952eb`）Release solution build 零 warning／error；全套 **2735 passed／0 failed／0 skipped**（Persistence 893、Serialization 133、Storage 36、Runtime／SG 1673）。最后两 Persistence 文件补强后再次 solution build 和相关 **23／23** 回归通过。两 runner 的 PowerShell AST 零错误；独立源码审查通过。证据在忽略目录 `.artifacts/db088-round1-20261013/`：`build-final.log`、`tests.log`、`private-final.log`／`private-final.trx`。全套共用 TRX 名被后续项目覆盖，因此四项目总数依据完整 `tests.log`，不声称每项目各存一份 TRX。

第二轮受控 package-mode pack 生成 **G `0.0.0-db088-e2e.20261013141051.30576`**，生产来源为提交 `ca5598b5b37b0b6c4f2bb50ac6968f1b12988043`。189 项保守生产输入在 pack 前／后及消费期间保持一致；四 DG 包、准确六 S 包的 nuspec／完整包字节、各阶段 assets、实际加载 DLL 和包内 SG／Build／targets 均核验。没有 source ProjectReference、IVT、手动 analyzer 或远程发布；旧包结果未用来授新资格。

| 普通包故事 | 实际限定结果 |
|---|---|
| [Development](../../../experiments/PackageConsumerProbe/DevelopmentSchemaConsumer/README.md) | 五个独立 PID；六次成功 Rebuild、一次预期 DG0015 拒绝；Off 草稿只读 V1 history，最终 Publish／Verify 恰好 V1＋V2B；两 private fork 的 shared ref 保持、最终才推广、全关冷续写和独立 readonly。精确完整 IDs／source／prior／H 仍由源码故事另证 |
| [Split](../../../experiments/PackageConsumerProbe/SplitRepositoryConsumer/README.md) | 五个独立 PID、四次成功 Rebuild；history 1→3。真实 PrivateUnused Event 接纳目录但不推进 State，全关冷签出后 List Delta，普通 Widget ToShared 完整导入不可达 Event 声明；准确 refs／source／prior／H、旧帧与 readonly 保持 |
| [PrivateContainer](../../../experiments/PackageConsumerProbe/PrivateContainerConsumer/README.md) | 七个独立 PID、四次成功 Rebuild；history 3→4。正常 Point V1→V2／Nullable lifting／List 元素 Upgrade，保三存活 IDs／Root heads 与旧新完整声明；升级 List Base 后冷 Delta，完整 ToShared 成功并发布两 ref，准确业务 Parent／source 与不可达 Keeper 保持 |

三个门禁首次消费新 G 即成功，无产品运行失败或设计阻塞。仅补强 PrivateContainer 的推广 ref1／ref2、Parent／source 断言；最初误用缺 manifest 的旧十包 feed 被 Prepare 在打包前拒绝，换原六包 manifest feed 后核验成功，失败日志原地保留。未修改生产代码或重包同版本。

主线另以隔离配置／cache、准确 S **package mode** restore／build 整个 solution，零 warning／error；相关 Persistence **51**、List／Nullable **43**、真实生成的 private／development 纵向 **4**，合计 **98 passed／0 failed／0 skipped**。第一轮全套 2735 项保原来源，本轮不称再次全套。独立审查核实际 source、包、日志和持久观察通过。

本轮工件根 `W:/TEMP/dg-db088-round2-20261013-141026-16448`，含 `task.json`、`pack-final.log`、不可变 feed／source sidecar、三个故事的 `result.json`／witness／evidence／实际编译日志，以及 `solution-build.log`、三份测试日志／TRX、`main-acceptance.json` 与 `independent-acceptance.json`。主线复核 189 项生产输入、170 份实际 loaded DLL、全部命令／消费输入／evidence hash 与 98 项 TRX；12 份活动 Markdown 的 916 个本地链接／锚点检查无错误。复跑选择新的仓外目录与独立 G；不改写旧包、history 或完整帧。

第三轮仅作收尾复核：实现合同与普通包冻结证据分别独立审查，主线再次核对当前 189 项生产输入、170 份实际 loaded DLL、三个 receipt／17 个独立 PID 及原 98 项 TRX。这是既有证据复核，没有重新执行包故事或测试；记录在忽略目录 `.artifacts/db088-round3-20261013-222637/revalidated-acceptance.json`。已核范围内未见未完成代码／包门禁或产品阻塞。

本案无剩余必经施工。当前 assets／DLL 为准确 S package mode；未来源码联调须显式重新 restore。默认 Publish、V3/V4 直接压缩交付、主／次版本、自动重放与 draft／release 管理继续由[路线图](../../DurableGraph-research-roadmap.md#42-开发-schema-与正式定稿)记录延期，不扩大本次资格。
