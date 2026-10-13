# DB-088：固定用户基线上的 Schema 开发与定稿

> 状态：2026-10-13，最小纵向实验已通过，实施前语义校核已收束；特例删减尚未实施。承接 DB-087 完成后的用户需求校准；不是重新打开自举重构排期。

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

三方独立评审及交叉质询已收敛于先验故事、迁移语义覆盖后删特例。主线按实际恢复／发布与 Build 接线复核；以下区分已选核心与尚未删除的特例：

| 项目 | 目标裁决 | 理由与验收要求 |
|---|---|---|
| 准确 baseline 恢复、身份导入与 private 增量 | 保留 | 真实用户复现场景所需，不能用空来源的新根替代 |
| fixed 员工引导、meta ObjectId、完整布局与来源校验 | 保留 | 各 fork 可有不同 V2 形状，旧 bytes 仍须准确解释 |
| 完整 ToShared 与两 ref CU | 保留 | 发布一致性合同；最终干净 fork 控制导入范围 |
| 同 key Schema overrides／完整 container replacements | 删除目标，未实施 | 超出当前用户需求；先替换仍有价值的目录、故障与冷恢复见证 |
| 私有新根 bootstrap lookup／延迟空来源建根／声明替换 | 随入口删除，未实施 | 普通 baseline 恢复承担首选流程；保普通 Shared 新图及 Event-only 合法空 State 工作区 |
| 主／次版本、draft token、自动 merge／过滤 | 延期 | 没有此次实验的必要消费，不预建框架 |

删除不能连带丢失完整闭包、实际来源、Event／Discard、发布失败及旧帧保持的回归。现有消费者是复杂性成本的证据，不构成兼容约束。施工顺序为先形成新正控故事，再迁移非特例语义见证，最后删除 API 与专用状态；不为保存旧测试重新建设替代入口。

`CreateBranch(checkpoint)` 是独立 ref 发布，随后 `CheckoutPrivate` 失败不会回滚这个已创建名字；该分支仍准确指向 baseline，可在正确环境中重新签出。不能把旧复合新根入口的“恢复失败不留名字”合同套到两个公开操作上。身份验收必须使用区别于空会话分配的源 IDs，并核实际 source，而非只比数字偶合。

后续施工落点：

| 范围 | 替换边界 |
|---|---|
| `SplitRepositoryConsumer`／`PrivateContainerConsumer` | 改为 baseline checkpoint→private 签出；前者保完整目录／ToShared，后者将 Point 的同版异形改为正常下一版及显式 inline／List 元素 Upgrade，不再消费容器 replacements |
| `PrivateSchemaViewGeneratorTests`／`PrivateSchemaPromotionGeneratorTests`／`PrivateContainerSchemaReplacementGeneratorTests` | 保身份／闭包／冷续／完整推广规律，改为不同版本；删除只属于覆盖／合流／removal-only 特例的见证，不人为增加新框架 |
| `PrivateSchemaRepositoryTests`／`PrivateSchemaPromotionTests`／`PrivateContainerReplacementTests` | 将仍必要的来源、Event／Discard、归属与发布失败断言放到普通 private 恢复／保存；两步建分支不承担旧复合建根的回滚名字合同 |
| `Repository.PrivateSchemas`／`P0Session`／`P0CatalogWorkspace` | 保 `CheckoutPrivate`／Head.S 来源及完整 ToShared，退出同 key 新根公开入口、bootstrap config／lookup 和声明替换分支；ABI、共同 writer／planner／cache 不随之重写 |

## 5. 实验结果与下一步

源码故事已执行并独立静审通过：新增 [DevelopmentSchemaWorkflowGeneratorTests](../../tests/DurableGraph.Tests/DevelopmentSchemaWorkflowGeneratorTests.cs) 的单 Fact 使用真实 SG V1／V2A／V2B，先消费旧 ID，再替换图与 child 升根形成高位 baseline。逐 ID 核值／引用边与实际来源，覆盖 private Discard／Event 保 rewrite、两升级 Base、retained List、双方全关冷 Delta／prior／实耗 H、错误环境 readonly 零写、干净 winner 的完整 ToShared、旧帧与旧 history 保持。

准确 source mode（Storage `974fd47abb73792f9d350af87ab91a53848952eb`）整库构建零 warning／error，新故事 1 项和相关 Upgrade／ToShared／同版 history 3 项均通过；本轮未重跑 2752 项全套。日志与 TRX 在忽略目录 `.artifacts/db088-development-schema-20261013/`。首次编译的测试助手实参错误已修；不属于运行时失败或产品反例。

实际 MSBuild 与普通包故事也已通过：[DevelopmentSchemaConsumer](../../experiments/PackageConsumerProbe/DevelopmentSchemaConsumer/README.md) 复用准确现存十包，六次成功 Rebuild、一次预期 DG0015 拒绝、五个全新进程。Off 保 V1 输入，定稿 B 才 Publish／Verify，正式 history 只有 V1＋V2B；private 两 refs、原业务历史、全部资源关闭后的读／续写和 readonly 均按其 README 的限定范围验证。完整来源／live IDs／H 与实际包／MSBuild 分层，不互相替代；当前生产源码未修改，没有新 wire／API／版本策略或新 pack。

实验支持删减方向，尚未删除 DB-087 特例。下一施工片按 §4 保留真实目录／source／Event／fault 规律，迁移两旧消费者与六组测试中的必要见证，再删除 `CreatePrivateBranch` 的同 key overrides／container replacements、P0Session bootstrap 状态和 Builder 声明替换。no-op／合流／dynamic→fixed 等只属于拟删除特例，不要求重建等价框架。删除片须重新 build／相关 tests；公开 API 退出影响的活动包故事须使用新 G 重新验收，旧包证据保其来源，不把此次复用包当成删除后资格。

仍未验证或定型：V3/V4 数据继承到单一定稿版本的直接交付／迁移压缩、主／次版本、开发新增数据重放工具及更自动的 draft／release 管理。当前已有整数版本与显式 Off 足够完成首选流程，默认 Publish 尚未修改；不以实验通过宣称这些易用性机制已实现。

## 6. 下一轮实施合同与停点

目标已选，无待决产品政策阻塞：退出整套同 key 私有新根替换能力，保留从真实 baseline 恢复、保身份和正常升版的 private 路径。§5 的实验通过是删除前置证据，不是删除完成证据。

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
| 缺 List 元素 Upgrade | 复用 [ListUpgradeTests](../../tests/DurableGraph.Tests/ListUpgradeTests.cs)、[NullableUpgradeTests](../../tests/DurableGraph.Tests/NullableUpgradeTests.cs) 和 [MetaBootstrapContainerUpgradeTests](../../tests/DurableGraph.Persistence.Tests/MetaBootstrapContainerUpgradeTests.cs) 的空／全 absent 预检；迁移源码夹具补公共 `CheckoutPrivate` 缺规则负例：零输出、健康、保已创建 baseline 名字并释放占用，关闭后在匹配环境重开可签出。当前内部预检证据不自动授这项公开资格 |
| 发布 outcome／可见性／旧 owner 寿命 | private 用例保 ref1-only／ref2 不变及完整帧；Unknown 冷开旧／新、Published 后未交付和安装失败由既有 [SplitRepositoryPublicationOutcomeTests](../../tests/DurableGraph.Persistence.Tests/SplitRepositoryPublicationOutcomeTests.cs) 与 [MetaBootstrapP0PublicationTests](../../tests/DurableGraph.Persistence.Tests/MetaBootstrapP0PublicationTests.cs) 承担。保 fault 后 cache／borrowed Store 拒用与 fresh reopen 实 refs 裁决，不向各容器夹具复制故障矩阵，也不将 NotPublished 等同健康 |

**完成验收：**生产源码中公开新根入口与专用状态已退出，活动执行代码不再依赖 overrides／container replacements；实验中的异形 V2 草稿可各自从固定 V1 baseline 接纳，已有同分支定义保持不可变，不增加“仅限 V1→V2”的产品限制。原用户存活身份、共享／循环、真实 Base／Delta／source／prior／H、Event／Discard rewrite、完整目录和 ref1／ref2／fault／旧帧规律经对应测试证实，新 G 普通包的冷恢复与发布故事通过，活动文档一致。

此次不修改默认 Publish、不增加主／次版本或草稿迁移压缩、自动重放、选择性 ToShared、typed cache、wire／程序集改造，也不恢复十九个暂停 lanes。只有具体消费者反例、必需 Upgrade 缺口或会改变上述产品语义的选择才重新讨论；一般 fixture／observer／helper 尾修由实施者自行处理。实施前校核仅静读源码与交叉质询，未新增运行时资格；以上迁移、公共缺规则负例与删除后新 G 均待实施验收。
