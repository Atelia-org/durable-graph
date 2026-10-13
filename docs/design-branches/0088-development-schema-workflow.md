# DB-088：固定用户基线上的 Schema 开发与定稿

> 状态：2026-10-13，最小纵向实验已通过；特例删减尚未实施。承接 DB-087 完成后的用户需求校准；不是重新打开自举重构排期。

## 1. 问题与用户决定

问题：能否用现有分支、private 保存与 Upgrade，从同一份已发布 V1 用户快照建立多个内容不同的 V2 草稿，保持原始存活对象的身份，最后只接纳定稿 V2？

以下来自本轮用户明确决定：

- 首选开发流程是固定 shared baseline → 每次模型调整后建立新的 dev fork → baseline 版本加一的当前草稿。各 fork 的草稿可同版本异形。
- 原用户图仍存活对象的 ObjectId、共享与循环需要保持；开发新增数据通常可以重建或重放。
- 如果要继承上一草稿的数据继续改模型，应递增 V3、V4；同一分支已接受的同 SchemaKey 定义不能被另一形状替换。
- 对外只交付一个定稿新版本；开发用主／次版本号只是备选，不预先加入身份或版本平台。
- 先验证完整开发故事，再按证据删减 DB-087 的同 key 新根替换特例。

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

| 项目 | 暂定裁决 | 理由与验收要求 |
|---|---|---|
| 准确 baseline 恢复、身份导入与 private 增量 | 保留 | 真实用户复现场景所需，不能用空来源的新根替代 |
| fixed 员工引导、meta ObjectId、完整布局与来源校验 | 保留 | 各 fork 可有不同 V2 形状，旧 bytes 仍须准确解释 |
| 完整 ToShared 与两 ref CU | 保留 | 发布一致性合同；最终干净 fork 控制导入范围 |
| 同 key Schema overrides／完整 container replacements | 删减候选 | 超出当前用户需求；先替换仍有价值的目录、故障与冷恢复见证 |
| 单独 private bootstrap lookup／空来源 Workspace／声明替换 | 随入口删除的候选 | 普通 baseline 恢复可以承担首选流程；尚未删除或完成等价覆盖迁移 |
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
