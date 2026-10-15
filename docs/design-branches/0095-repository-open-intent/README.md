# DB-095：Repository 打开意图与业务保存准入

> 状态：**E 方向已获用户同意；实施计划已细化，可进入下一轮实施，产品尚未修改**。
> 日期：2026-10-15；源码调查基线：`79bab5d27bf50c6e66a54da0d4cc729a9181b245`。
> 阅读顺序：本节目标 → §7 API 合同 → §8 接线 → §9 迁移 → §10 验收 → §11 实施顺序。§3–6 保留竞争评审依据，不是并行实施路线。
> 本轮仅完善计划；下一轮才实施。计划就绪不等于代码、回归或普通包已经验收。

## 1. 问题与研究边界

下游「回声温室」反馈第 2 项指出：默认可写 Open 不意味着普通业务对象可以保存，调用者还须显式传入 `schemaWriteMode: SchemaWriteMode.Shared`。该消费者一开始就按 README/XML 正确配置，**没有实际触发保存拒绝**；这是有依据的可发现性问题，不是已发生的数据丢失或持久化失败。

原始反馈：`E:\repos\Atelia-org\durable-graph-workshop-a\docs\durable-graph-feedback.md`，来源 DG `b209a777c3c103108f95e0277241a127fb1cc7b4`。此路径是外部工作区来源，不是仓库内交付依赖。

研究问题：能否让打开仓库、取得可编辑对象和选择保存归属形成一致的使用流程，同时保住 Shared/Private 的准确 Schema 来源、完整冲突校验、只读和失败边界？初始三态 `{SharedSchema, PrivateSchema, ReadOnly}` 经竞争评审后退出主线；用户已同意以 **E：在 checkout/创建时选择** 继续细化。

最小可观察标准：所有返回可编辑 `BranchCheckout` 的公开入口都在名称中表达 Shared/Private；得到会话后，不再因遗漏 Open 配置而在首次保存时拒绝。完整 Schema 冲突、模型能力不足、只读及故障仍按原合同拒绝。该目标比下游原句的文档建议更强，现已纳入用户认可的 E 方向；不附带 private 首次创建或新的 Fork 能力。

本案是 DB-087 打开/保存准入外观的候选后继，不重开其完整持久化重构。现行合同仍见 [DB-087](../0087-meta-bootstrap-refactoring-route/README.md) 与 [产品工作集](../../../src/PROJECT-STATE.md)。本轮不改变格式、升级规则、构建期 history 协议、并发模型或底层恢复纪律。

## 2. 已核实现状

| 事实 | 实现或测试入口 |
|---|---|
| `CreateNew` / `OpenExisting` 默认 `SchemaWriteMode? = null`；`IsReadOnly` 独立取自底层资源 | [Repository](../../../src/DurableGraph.Persistence/Repository.cs) |
| `SchemaWriteMode` 当前只有 `Shared = 1`；null 不是只读，而是缺少普通业务写入归属 | [枚举](../../../src/DurableGraph.Persistence/SchemaWriteMode.cs)、[准入测试](../../../tests/DurableGraph.Persistence.Tests/SchemaWriteAdmissionTests.cs) |
| 未指定时仍能恢复 checkout、Fork、修改 ref/tag；普通业务保存即使无变化也在 Capture 前拒绝 | `RequireSchemaWriter`、`MissingModeRejectsBeforeAnyCaptureCallbackIncludingUnchangedSave` |
| Private 目前由 `CheckoutPrivate` 选择，只恢复已有分支；要求实际 Head 的准确 Schema pin | [Private 入口](../../../src/DurableGraph.Persistence/Repository.PrivateSchemas.cs)、[BranchSaveSession](../../../src/DurableGraph.Persistence/BranchSaveSession.cs) 的 `PrivateCatalogRoot` |
| 普通 Shared 保存检查选定的完整 Schema 视图已被 shared 接纳；不会自动把 private 视图合入共享目录 | `Repository.RequireSchemaWriter` |
| `Commit…ToShared` 是显式单次完整目录合入；不改变 checkout 默认归属或固定执行环境 | [BranchCheckout](../../../src/DurableGraph.Persistence/BranchCheckout.cs)、[promotion 测试](../../../tests/DurableGraph.Persistence.Tests/PrivateSchemaPromotionTests.cs) |
| 私有续写取实际 Head.S，业务恢复取最近 State.S；两者不能合并为一个“当前 Schema” | [Repository 接线](../../../src/DurableGraph.Persistence/Repository.MetaBootstrap.cs)、[保存 owner](../../../src/DurableGraph.Persistence/RepositoryResources.MetaBootstrap.cs) |

这里核对的是源码和测试定义；本轮尚未运行新 Probe，不把现有测试名字等同于本轮已通过的结果。

两条会改变方案取舍的补充事实：

- [PrivateSchemaRepositoryTests](../../../tests/DurableGraph.Persistence.Tests/PrivateSchemaRepositoryTests.cs) 的 `Unchanged_private_save_reuses_meta_and_directory_and_never_rebases_to_latest_shared` 在同一 owner 内交错 Shared main 与 Private dev 保存：main 推进 shared 后，dev 仍保持自身视图，并可在各自目录新增同 key 不同布局。promotion 回归还以这种混用制造 descriptor 编号冲突。它证明现有能力与机制验证价值；尚不能据此声称外部业务应用必须混用。
- [DB-088 §4/§6](../0088-development-schema-workflow/0088-development-schema-workflow.md) 刚完成 private 新根 bootstrap、空来源建根及 overrides/replacements 特例的删除，首选固定 baseline→private 恢复。新 private 初次创建不必等同于重建全部旧特例，但必须独立证明需要，不能作为入口对称性的免费附赠。

## 3. 初始候选 A：必选 Repository 三态（未采用）

拟议形状：

```csharp
Repository.OpenExisting(path, models, RepositoryOpenMode.SharedSchema);
Repository.OpenExisting(path, models, RepositoryOpenMode.PrivateSchema);
Repository.OpenExisting(path, models, RepositoryOpenMode.ReadOnly);
```

| 模式 | 普通业务保存 | Schema 归属 | ref/tag 写入 |
|---|---|---|---|
| SharedSchema | 允许，保模型/完整 Schema 校验 | 共享目录 | 允许 |
| PrivateSchema | 允许，保准确来源与校验 | 当前分支独立演进 | 允许 |
| ReadOnly | 禁止 | 读取已保存准确视图 | 禁止 |

初始推荐的配套语义（仅保留评审来源，不施工）：

1. 参数必填，没有 null/Unspecified/默认 Shared；CreateNew 的 ReadOnly 在资源获取前拒绝。已有只读命名入口可作为薄包装保留。
2. 普通 CreateBranch、Checkout、Fork 继承打开模式；移除重复的 CheckoutPrivate，避免 Open 和 checkout 两层默认选择。没有兼容性保留要求。
3. 保留显式 Commit…ToShared，Private 下只改变本次提交目的地，后续普通提交仍为 Private；ReadOnly 下不能借此写入。
4. 不保留“默认拒绝普通保存、允许 ref/tag 写入”的单独模式；ref-only 工具选择 Shared/Private，但其引用操作不得因此新增 Schema。
5. Open 模式不持久化，不从分支名字或上次打开推断，不自动登记全部模型。

**Private 初次创建是能力扩展，而非把 null 换成 enum。** 初始候选规则为：有历史的分支/Fork 从实际 Head.S 开始；无历史的新分支从创建时 shared 视图开始，shared 为空则从空目录开始；后续不追随最新 shared，普通 private 保存不发布共享根。现有 `PrivateCatalogRoot` 会拒绝无 Head，因此此项须专门接线与验证。

初始论据：调用处强制表态，普通业务操作统一；去掉一个容易误读的中间状态。潜在代价：Repository 级选择可能过粗、丢失同 Open 混用 checkout 归属的能力；新分支为何继承整个 shared 目录仍需论证；ToShared 使三态不等同于完整权限模型。

## 4. 竞争方案空间

| 候选 | 最小变化 | 主要收益 | 主要疑问 |
|---|---|---|---|
| A：必选三态 | 统一 Open 意图，普通入口继承，补 private 首次创建 | 常用路径一致，消除未选择状态 | 是否为了一个参数摩擦扩大能力和收窄混用场景？ |
| B：既有模式必填 | 取消默认 null；保只读与 CheckoutPrivate 入口 | 首次遗漏可在编译期发现，改动较小 | 仅一个 Shared 枚举如何表达 private-only 使用意图，是否迫使调用者作虚假选择？ |
| C：命名打开入口 | 如明确 Shared / Private / ReadOnly 的工厂方法 | 易发现，不依赖 enum/非法组合 | 与 A 是同一语义的不同外观，还是可以真正减少实现范围？ |
| D：默认 Shared | 普通可写打开默认可保存，Private 仍显式 | 最短普通用户路径 | 是否隐式选择了应由应用明确决定的目录归属？ |
| E：在 checkout/创建时选择 | Open 管读写，写目标在实际编辑会话选择 | 私有/共享可同 Open 并存，作用域贴近保存 | 是否重复配置、让创建/Fork/checkout 入口膨胀？ |
| F：文档/诊断优化 | 保现有语义，加强样例与报错 | 代价最小，保所有现行能力 | 仍保留延迟到保存才暴露的遗漏 |

此表不是固定候选名单。独立评审可提出组合、更小分片或新方案；不得仅凭票数决定。

## 5. 关键问题的边界裁决

| 问题 | E 的确定处理 |
|---|---|
| Shared/Private 是什么？ | 编辑会话固定的普通保存目的地，不是分支持久属性，也不是权限沙箱；ToShared 仍是显式单次完整合入 |
| Open 后是否就能保存？ | Open 只提供资源；必须从明确命名的编辑入口取得会话，省略目标不再成为合法调用 |
| 是否保同 owner 混用？ | 保持，同一冻结模型配置下可有不同分支的 Shared/Private 会话；不授予同一模型身份的任意异形 CLR 模型并存 |
| 是否提前拒绝 Shared checkout 指向 private 历史？ | 不提前改变准入时机：可恢复时允许交付，普通保存仍核完整目录；显式 ToShared 可执行合法 promotion |
| Private 首次创建与 ForkPrivate？ | 均不新增；沿用 checkpoint 建 ref 后 CheckoutPrivate 的两步语义 |
| 未配置 Open 的专用权限是否保留？ | 删除该状态。ref-only 工具无需 Schema 配置；若主动取得 Shared 会话则可以普通保存，不新增第四态或权限框架 |
| 只读、实际来源、失败边界？ | 保真实底层只读、Head.S/State.S 分责、完整目录校验、fault/lifetime/reentry 与各入口发布顺序 |

这些裁决消除了上一版的主要开放问题。持久化实现与新外观的可执行资格仍由 §10 验收，不能由静态分析代替。

## 6. 独立评审与主线程裁决

前一轮先写初稿，再将同一草案交给三个只读 subagent，各自查源码/测试并提出竞争路线；没有授权它们修改产品或运行构建。主线程核对关键引用并补充反例质询。以下是设计审查意见，不是新代码验收结果。本轮在用户认可 E 后由主线程进一步核对接线、测试与包探针，形成 §7–11 的实施合同。

| 视角 | 发现与建议 | 主线程裁决 |
|---|---|---|
| 需求与作用域 `db095_demand_review` | 同 owner 混用有真实机制回归，外部应用必要性未证；A 同时扩 private 新建、缩混用作用域。建议编辑会话显式选归属 | 接受其事实与范围区分；不因旧测试存在就冻结 API，也不把能力删除称为无损 |
| 竞争 API `db095_api_alternatives` | 首先推荐默认 Shared + 显式 Private，以最小改动统一普通使用；经草稿误接纳反例质询后，改荐命名编辑入口 | 保留这次修正的原因：默认 Shared 的成本不只是文档变化，见下文反例 |
| 语义与实施 `db095_semantic_review` | A 可以实现，但 private 新建有来源规则；混用移除不必要。建议保现有 baseline private 路线、将选择下放到编辑入口 | 接受有限 E；private 初次创建从反馈修复拆出，不以旧特例已删除为由宣称它技术上不可行 |

### 6.1 原推荐需要纠正的地方

**三态不是问题的唯一自然模型。** ReadOnly 是资源写权限；Shared/Private 是某次编辑会话的默认 Schema 目的地。现有实现已经允许同一可写 owner 承载不同会话，单次 ToShared 又可显式改变一次提交目的地。强行用一个 Repository 枚举概括这些层次，必须选择删能力或再加覆盖层；前者有代价，后者削弱原本的简化收益。

**“没有看到必须混用的外部需求”不等于“现在没有混用能力”。** §2 的回归是直接反证。A 若获选应明确记录这种产品收窄，而非机械迁移全部测试并宣称语义保持。

**新建 private 的两种初始来源并不等价。** 当前 owner 的 `PrepareSaveCore` 在 private 无 parent 时仍取 `_baseline.SharedRoot`，并核对 `catalog.SourceGraph`。从创建时 shared 开始（shared 空才从空开始）有现成来源规则可复用，但还需接通 session/公开入口。shared 非空时强行从空目录开始，则须改变 owner 的权威规则，不能只让 `PrivateCatalogRoot` 返回 null。A 的有限新建不必恢复 DB-088 删除的 overrides/replacements；两者不能混为一谈。

**Private 是保存目的地，不是“永远不碰 shared”的权限沙箱。** 显式 ToShared 成功后会接纳合并后的完整目录。后续普通 Private 仍只推进分支发布，但目录基线是新的 Head.S，不能回到推广前的私有目录。此细节来自 `SchemaCatalogWorkspace.PrepareToSharedOwned`、`RepositoryResources.CommitSave` 与 `PrivateSchemaPromotionTests`。

### 6.2 默认 Shared 的强反例

开发程序带 V2 模型，从已发布 V1 baseline 建立 dev 分支，但误用了普通 Checkout，而非 CheckoutPrivate。此时 Head.S 只有 V1，完全包含于 shared；`RequireSchemaWriter` 的完整视图检查会通过。后续 Capture 捕获新 V2，普通 Shared 保存可将它接纳进共享目录。已有完整目录校验不能推断“这个新 V2 只是草稿”。

在当前未配置 Open 下，这个普通提交会由 null guard 拒绝。改为默认 Shared 会移除这一防误操作边界。因此 D 仍是可行产品选择，但须明确接受“省略选择就视为同意共享接纳”，不能说它只是无语义影响的默认值修复。该反例是源码路径推导，**本轮没有执行对应新 Probe**。

### 6.3 竞争方案的最终位置

- **已选择 E 的有限版**：显式编辑入口选归属，Open 管资源读写；详见 §7。它同时消除延迟遗漏和两层模式，保已有混用，不扩 private 新建。
- **A 保留为有效竞争者**：若用户更重视一次打开只表达一种普通保存意图，愿意接受混用收窄并需要 private 首次创建，三态仍合理。当前证据没有证明它更适合本反馈。
- **C 命名 Open**：`OpenSharedExisting` 等名字可降低枚举组合成本；但如果保 checkout override，仍有两层选择；若不保，继承 A 的收窄。命名本身不解决作用域取舍。
- **B 只让现有 Shared 参数必填**：使 private-only 应用先假选 Shared 再 CheckoutPrivate，或改为显式传 null；前者意图不真，后者保留未配置状态，不作为最终外观。
- **D 默认 Shared**：代码可能最少、普通路径最短，但接受 §6.2 的行为变化。本轮不推荐。
- **F 文档与诊断**：若决定不承担公开 API 迁移，这是符合原反馈的保守停点；它不满足本案提出的更强可发现性目标。

这是证据与范围裁决，不以“三票一致”作为正确性证明。当前按 E 施工准备，不再等待 A–F 的重复选型；未来只有新消费者约束改变这些取舍时再重访。

## 7. 已选 API 合同：明确取得哪一种编辑会话

以下为 **E 下一轮实施的最小形状**，不是当前 API。本片采用明确命名入口，不再把命名/枚举作为施工前待决项。保资源入口名字，删除 Open-level `SchemaWriteMode?` 与对应未配置业务准入状态；只读仍由实际底层资源保证。每个可编辑会话一经取得，就已有确定的普通保存目的地。

| 操作 | 目标入口 | 语义 |
|---|---|---|
| 创建/可写打开资源 | `CreateNew(path, models)` / `OpenExisting(path, models)` | 冻结配置、取得 RW 资源；自身不接纳业务 Schema |
| 只读打开 | `OpenReadOnlyExisting(path, models)` | 底层只读；编辑及 ref/tag 写入拒绝 |
| 从业务对象首次建 State 分支 | `CreateSharedBranch(name, initialState)` | 显式 Shared 首次保存；不新增 private 无历史建根 |
| 从 Event 首次建分支 | `CreateSharedBranchFromEvent(name, initialEvent)` | 同上；Event-only 无 State 语义保持 |
| 恢复 Shared 编辑会话 | `CheckoutShared(name)` | 普通保存走 shared，保完整视图准入 |
| 恢复 Private 编辑会话 | `CheckoutPrivate(name)` | 取实际 Head.S 续写；已有能力 |
| 恢复后发布新 ref 并交付 Shared 会话 | `ForkShared(name, checkpoint)` | 现 Fork 的明确命名；仍先恢复后发布 |
| 只建立分支引用 | `CreateBranch(name, checkpoint)` | 仍仅发布引用，不作 Schema 选择或业务保存 |
| 建立 private fork | `CreateBranch(name, checkpoint)` 后 `CheckoutPrivate(name)` | 保当前两步合同，不顺带新增复合 ForkPrivate |
| 普通保存 / 单次合入共享 | `CommitState` / `CommitEvent` / `Commit…ToShared` | 会话默认与单次完整合入分责；无每次 Commit 重复选择 |

示意：

```csharp
using var repository = Repository.CreateNew(path, models);
using var main = repository.CreateSharedBranch("main", initialState);
main.CommitState();

repository.CreateBranch("dev", main.Head);
using var dev = repository.CheckoutPrivate("dev");
dev.CommitState();             // 不推进共享根
dev.CommitStateToShared();     // 显式验证并合入完整目录
dev.CommitState();             // 仍为 Private，基线已是推广后的 Head.S
```

这里移除的是无目标 `Checkout(name)`、从业务对象创建的普通 `CreateBranch` / `CreateBranchFromEvent` 以及无目标 `Fork`，不保这些名称到 Shared 的静默兼容转发。调用者无需读完说明才知道另有一个默认 null；迁移后必须在取得会话的语法中表达目标。`CreateBranch(name, checkpoint)` 作为 ref-only 操作保留，参数类型承担清楚的区分。

本片不增加公开 `RepositoryOpenMode` / `SchemaWriteTarget` 或会话 mode 属性；现有内部 `UsesPrivateSchemas` 足以承担不可变选择。命名入口准确表达当前能力的不对称，避免枚举暴露尚不支持的 private 全新建根。ForkPrivate 若后续确有一次调用完成恢复和 ref 发布的需求，可作为独立小增量，不为表面对称预建。

签名落实：上述改名方法保留现有参数名称、顺序、返回类型与可选保存策略；`CreateSharedBranch` / `CreateSharedBranchFromEvent` 仍返回 `BranchCheckout`，ref-only `CreateBranch(string branchName, CheckpointAddress selectedFrame)` 仍返回 `CheckpointAddress`，`ForkShared` 保 `source` 参数。三个公开资源工厂保已有 rotation/cache 参数及默认值，仅删除 writable 工厂末尾的 `schemaWriteMode`。同时删除公开 `Repository.SchemaWriteMode` 和枚举文件，不保 obsolete、别名或静默默认 Shared 重载。

保留的限制：显式 Shared 会话仍可能因已存在的 private 完整目录未被 shared 接纳而拒绝普通保存；这是准确目录约束，不是遗漏模式。只读不能取得可编辑 checkout，模型不全不能恢复图。显式选择防止遗漏，不能防止调用者主动选错目标。

## 8. 核心接线与不变量

本节是下一轮代码改动的边界；先做最小接线，不重写保存内核。

| 文件/符号 | 必改内容 | 保留边界 |
|---|---|---|
| `Repository.cs` 公开工厂与会话入口 | 按 §7 改名；删除模式属性及 writable 工厂参数；Shared 入口显式传 `privateCatalog: false` | rotation/cache 参数、资源获取/模型冻结时机、真实只读、每分支占用 |
| `Repository.MetaBootstrap.cs` 构造器、内部 CreateStorageNew/OpenStorageExisting/OpenStorage | 删除模式参数、赋值及非法 mode 验证；内部探针工厂与公开工厂一致 | null models、负缓存预算等前置验证与资源清理；不新增测试专用 mode 旁路 |
| `CreateBranchCore` | 首部无 checkout 的 `RequireSchemaWriter()` 改用 `RequireWriter()`；新建 Shared 会话 | 初始保存前尚无 Head，不调用 `RequireCheckoutSource`；原 occupancy、发布与交付异常合同 |
| `RequireSchemaWriter` | 去掉 Open mode/null 分支；改为必需 `BranchCheckout` 参数 | `RequireWriter`、准确 source、Shared 全目录包含性及原冲突异常；不为改名另起 guard 框架 |
| `CheckoutCore` / `RestoreCheckout` / `NewCheckout` | 确认每个公开入口固定传入正确目的地，所有热/冷恢复分支都传递相同值 | 原 bool 内部表示可保留；恢复缓存不缓存目的地，不另设可变目标状态 |
| `Repository.PrivateSchemas.cs` | 保 `CheckoutPrivate`，更新 XML 中“Open 另选模式”的说法 | 必须真实 Head.S，不新增无历史分支初始化 |
| `BranchCheckout.cs` 与当前说明 | 更新会话选择及 ToShared 文档 | Commit 三种普通形态及三种 ToShared 形态不变，事件/State、对象身份和固定执行环境不变 |

三条操作路径不得混合：

1. **新 Shared 分支**：writer guard → 占用新名称 → 无业务来源的 Shared session → 现有 Capture/prepare/publish。只在这一条没有已有 checkout source。
2. **普通会话操作**：writer/owner/source guard → Shared 才核完整 Head.S 已被 latest shared 同形接纳 → Capture/prepare。Private 跳过 Shared 包含性，但不跳过准确来源校验。`EnsureEditingAllowed`、普通 Commit 与内部 PrepareAndDiscard 均保持这条准入。
3. **显式 ToShared**：writer/source guard → 已有完整 Head.S 与 latest shared 的冲突预检 → Capture → 连同本次捕获布局再次完整校验/prepare。已有目录冲突须在 Capture 前拒绝；新捕获布局的冲突在 Capture 后、append 前拒绝，不能混成“所有冲突都在 Capture 前”。不能套用普通 Shared 包含性而挡住合法 promotion，也不能把单次目标写回会话。

正常代码无需改 `BranchSaveSession`、`SchemaCatalogWorkspace`、`RepositoryResources` 的持久机制。若接线迫使这些文件修改来源选择、目录合并或 refs 发布语义，先停下核对范围，不能以外观重构掩盖协议变化；必要注释/符号引用修复不属于这种扩大。

### 8.1 恢复与后续保存的准确含义

- 有 State 的 checkout 始终按最近 State.S 恢复业务，即使 Head 是使用较新 Schema 视图的 Event。Private meta 从实际 Head.S 续写；无 State 的 Private Event-only 使用 Head.S 执行环境，第一次 State 没有业务内容来源。
- Shared meta 保存以 latest shared 为目的地，保已有 rebase/包含性规则；不要因为名字改为 CheckoutShared 就改成从 shared 恢复业务。
- ToShared 确认成功后，当前 Head.S 已是合并结果。Private 后续从这个新 pin 保存，后来的独立 shared 更新仍不会被自动吸收；固定 execution view 不变。
- 冷开时重新由入口选择目的地，不从分支名或上次保存推断。同一分支每次只允许一个活跃 checkout；不同分支可同 owner 串行交错操作，不开放并行访问。
- `ForkShared` 继续从传入准确 checkpoint 恢复、成功后发布 ref；不是先建 ref 再恢复。两步 private fork 中第二步失败保留第一步已发布的 ref；不添加补偿删除或自动重试。

### 8.2 错误阶段

旧入口移除通过编译诊断暴露；真实只读、已关闭/故障、占用或来源不符仍由运行时原 guard 拒绝。Shared checkout 指向尚未 shared 接纳的 private 视图时，不在签出时新增阻断：保普通保存前拒绝与显式 promotion 的合法路径。所有“保存可用”说明仅排除缺模式这一种拒绝，不承诺任意模型/历史都能保存。

## 9. 迁移清单与旧验证的处置

### 9.1 调用迁移

| 旧用法 | 目标用法 |
|---|---|
| Open/Create 的 `schemaWriteMode: Shared`、显式 null、条件参数 | 删除该参数；按后续编辑入口表达归属，不增加 Open 分支 |
| `repository.CreateBranch(name, IDurableObject)` | `CreateSharedBranch`；传入 checkpoint 的同名 ref-only 调用保留 |
| `CreateBranchFromEvent` / `Checkout` / `Fork` | `CreateSharedBranchFromEvent` / `CheckoutShared` / `ForkShared` |
| `CheckoutPrivate` / `Commit…ToShared` | 保留，修正说明与旧模式断言 |
| `repository.SchemaWriteMode` / 公开 enum | 删除模式断言；测试应观察实际 Schema 根/发布结果，不新增替代公开属性 |

检索范围为 `src`、`tests`、`experiments`、`eng` 下源码与脚本内生成的 C#，以及活动 README/XML/使用说明。调查基线有 74 个源码/脚本文件命中旧模式名称，说明迁移面不小；数量不是验收门槛，不做全仓文本替换。特别保 `CreateBranch(checkpoint)`、其他类型的 Checkout/Fork 和冻结历史引用。

[Readme runner](../../../experiments/PackageConsumerProbe/Run-ReadmeQuickStartProbe.ps1) 的 V2 C# 拼接也使用旧参数/Checkout，须与根 README 一起迁移；不能只改编译到的 `.cs`。CLI 当前 Program 为空，只需核实没有漏项，不人为增加 CLI 命令。

### 9.2 必须按语义改写的验证

| 现有资产 | 处置 |
|---|---|
| `SchemaWriteAdmissionTests` 的 unconfigured creation / cold checkout / MissingMode 拒绝 | 移除过时 null 权限断言，改为新 Open + 明确 Shared 创建、冷签出与首次普通保存正例；缺选择改由 §10 编译负例负责 |
| 同文件的 ref-only、只读、null models/资源边界 | 保留独立行为断言，迁移入口；删除纯非法 SchemaWriteMode 数值用例，不连带删除其他参数验证 |
| `PrivateSchemaPromotionTests` 的 ordinary null RW promotion | 改为 CheckoutShared 上执行 ToShared；推广前仍因未接纳完整目录拒绝，推广成功后普通 Shared 保存应允许。旧“推广后仍因 null 拒绝”必须替换，并同步后续 head/计量基线 |
| `PrivateSchemaRepositoryTests`、promotion、真实 SG 开发/私有测试 | 保混用、完整不可达声明冲突、准确 Head.S/State.S、Event-only、故障与旧帧断言；不把能编译等同于通过 |
| [Split consumer](../../../experiments/PackageConsumerProbe/SplitRepositoryConsumer/Program.cs) 的 `Continue` / `UnconfiguredSaveRejected` | 删除过时 block/回执字段及 [runner](../../../experiments/PackageConsumerProbe/Run-SplitRepositoryProbe.ps1) 中对应强制断言；保后续真实冷 Delta、身份/prior/H 和旧帧验证 |
| 同 consumer 的 `RejectOrdinaryPrivateSaves(configuredShared)` | 去掉 true/false 双分支，保 CheckoutShared 对完整未匹配 private 目录的精确拒绝，不能因另一分支过时而删整个反例 |
| [PrivateContainer consumer](../../../experiments/PackageConsumerProbe/PrivateContainerConsumer/Program.cs) 条件 Open 模式 | 无条件 RW Open；原 `Wide` 决定的 Shared/Private 会话选择与实际读写故事保持 |
| [Development consumer](../../../experiments/PackageConsumerProbe/DevelopmentSchemaConsumer/Program.cs) | Shared seed、baseline→private draft、显式定稿、Shared 冷续写分别迁移；保 V1 历史/升级/落选草稿隔离 |

当前文档、测试名、回执字段应准确反映新语义；已完成旧 DB/历史日志不批量重写或回填新 G 资格。删除枚举等源文件后检查受影响链接；本案 §2 的旧实现证据改为明确的基线 Git 来源，不能留下指向已删文件的活动链接。实施结束在 DB-087 入口加本案后继说明，明确仅 Open/checkout 外观被接续，来源/保存内核合同继续有效。

## 10. 验收矩阵与证据层次

### 10.1 源码与公开调用

| 验收项 | 最小可观察结果 | 优先复用 |
|---|---|---|
| 新公开表面 | 新工厂、两种 checkout、ForkShared、ref-only、只读可编译；旧模式与旧编辑入口不可编译 | `DurableGraph.Tests` 已有 Roslyn 引用，增加小型独立调用编译用例；不在 Persistence 测试项目另引入编译器依赖 |
| Shared 首次保存与冷续写 | 无 Open 模式，State/Event 创建、重开/CheckoutShared/Commit 成功，身份/Delta 保持 | admission、EventHistory/DB078 与现有 Generator 集成 |
| Private baseline 与混用 | 同 owner main/dev 交错，private 不追随 latest shared，双方完整定义按各自目录解释 | PrivateSchemaRepository/Promotion、DevelopmentSchemaWorkflowGeneratorTests |
| 普通 Shared 的完整准入 | Head 含业务不可达 private 声明时，Capture 前拒绝；不追加帧、不推进 head/ref、不消费原 baseline | 现有 Shared admission 和 promotion 冲突用例 |
| ToShared 后续（重点新增） | 推广得到 pin P；另一个 Shared 会话发布 Q；原 Private 普通保存仍承接 P、不吸收 Q 新增声明，不改 shared ref；全关后 CheckoutPrivate 再续写保持 | 在现有 promotion 模型/测试中补一条具体故事，检查 `CatalogSource`/Head.S 与 ref bytes，不只比业务值 |
| Event Head / 无 State | Private 从 Head.S 续写，业务仍按 State.S 恢复；Event-only 首次 State 无业务 source | 已有 Event-only、Split 与准确 source 反例 |
| 只读/ref-only | 只读编辑/创建/ref/tag 均拒绝且文件不变；RW ref-only 不新增 Schema/对象帧，不触发无关 Capture | admission 与 branch/tag 回归 |
| Fork/失败/寿命 | ForkShared 恢复失败无新 ref；两步 private 签出失败保 baseline ref；保 occupancy/fault/publication outcome/source 检查 | DB078Fork/Failure、private 及 split 回归 |

编译负例每个片段只触发一个移除项，使用真实新程序集的公开可见面：旧 Checkout/Fork/CreateBranchFromEvent/SchemaWriteMode 属性期望 `CS1061`；旧 `schemaWriteMode: default` 参数期望 `CS1739`；充分限定的旧 enum 类型期望 `CS0234`；静态类型为 `IDurableObject` 的旧 CreateBranch 实参期望 `CS1503`（剩余重载接受 CheckpointAddress）。诊断位置和目标符号必须匹配；不能仅断言“编译失败”。常量 `null` 仍可能绑定 ref-only 重载，按正常空参数拒绝，不声称所有旧文本都必然编译失败。

不要求为了证明已放弃的默认 Shared 路线再造产品开关。§6.2 反例保为源码推导；本片通过明确入口的编译边界和现有 private 实际发布见证验证选择生效。

### 10.2 构建、测试与普通包

下一轮先核准确存储 pin 与 restore 模式，按 [依赖指南](../../storage-dependency.md) 配置 source 或完整本地 package feed；不要混用 assets。最小源码门禁为根 solution build，加 Persistence 与 DurableGraph 两个测试项目的完整套件，因为二者均广泛迁移调用。配置应一致，例如 Release build 后用 `--no-build --no-restore -c Release` 跑两个项目；不沿用过去的通过计数作为新结果。

普通包使用干净来源的新 G，S 继续取 `eng/StorageDependency.props`，本片不要求更改上游。打包必须 package mode，保来源提交、nupkg 哈希、全新 cache、正常 PackageReference/SG/Build；测试不要借 friend、ProjectReference、手工 analyzer 或旧 G。

最低包门禁：

1. 候选导出与 `Run-ReadmeQuickStartProbe.ps1 -CandidateDirectory ...`：直接执行修改后的 README 同源新建、冷开续写、V2/browse/policy/Fork/Verify 故事，保其原有配置/还原负例。
2. 修改后的 `Run-SplitRepositoryProbe.ps1`：新入口下准确来源、完整私有目录、普通 Shared 拒绝、ToShared/冷续写。回执与 runner 一起迁移，删除字段不是测试成功。
3. `Run-DevelopmentSchemaProbe.ps1` 与 `Run-PrivateContainerProbe.ps1`：现有 private 开发及容器升级故事仍由新包正常消费；不因参数迁移退出这些能力。
4. 在隔离的新包消费者上做小型公开调用编译正反例，覆盖 §10.1 的移除项。可作为已有 Probe 的专用步骤，不新增通用测试平台。负例完成后恢复合法源码并重新 build，再运行正常故事，避免使用前一次成功 DLL。

其它受影响活动消费者完成源码迁移；根 solution 不包含的项目至少用新 G 验证合法编译。带多版模型/history 的资产按其既有阶段说明编译，不把仅保留的历史模型 Library 当作已运行故事。已有场景的行为由上述包门禁和根测试承担，不要求每个旧领域故事全量重跑。

## 11. 实施顺序、停点与就绪判断

本轮只准备计划；下一轮收到实施指令后按以下顺序推进，不需要重新投票选型：

1. **核心与可构建迁移**：主线程固定 §7 签名；同步修改公开/内部工厂、guard、根源码测试与 XML。使用明确文件分工迁移独立消费者，不能让多人同时改 Repository。删除旧入口，不为保持中间提交可构建引入兼容外壳。
2. **语义验收**：按 §9 处理过时断言，增加公开编译见证与 ToShared 后续来源故事；根 build 和两个相关套件通过。独立复核重点是 guard 保留、实际 ref/Schema source 和负例准确诊断，不只看批量改名 diff。
3. **文档与新 G**：根 README/包 README/XML/活动 Probe 导航对齐；在干净提交上打新 G，执行 §10.2。失败分清包配置、调用编译与运行期语义，不把旧回执替代新证据；产品补丁后重打唯一新 G。
4. **收口**：DB-095 记录准确源码、G/S、测试/包结果与未测范围；更新当前状态、目标中必要的长期边界、路线与 DB-087 后继提示。可按“实现与迁移”“验证记录”提交，不 push/公开发包作为本案必经步骤。

实施的成功停点：新入口可从普通包完成创建、读取、Shared/Private 保存、完整 promotion 和冷续写；无未配置 Open 保存状态；现有准确 Schema/source、append-only、只读和故障语义通过回归。公开签名变化需要下游更新 DG 包并重新编译；本片不改变持久格式、不要求重建存档，不承诺旧二进制继续链接。

仅以下发现需要重开设计：显式入口仍无法覆盖真实已支持的调用；必须改变 owner 来源/合并协议才能完成有限 E；现有来源或失败回归揭示合同矛盾；普通包编译/运行证据否定公开形状。个别调用迁移、测试助手或诊断预期修正属于实施，不自动扩成新 API 设计。

**当前就绪判断：可以实施。** 已固定入口、删除项、保留 guard、过时回归处置、包验收和停止边界；未发现必须先做机制 Probe 才能选路的新阻断。主要成本是调用与验收资产迁移，而非新的持久化机制。尚未编译新 API、执行新测试或出新包，因此这里声明的是计划成熟度，不是产品完成度。

前轮三方评审及独立复核的意见见 §6；本轮重新静态核对生产接线与 Split/PrivateContainer/README runner，补齐迁移中的行为变化。本轮文档检查通过：计划全文与三处新导航合计 22 个本地链接目标可达，集成叙述与差异空白已检查；无产品代码、配置或包修改，未运行 build/test。
