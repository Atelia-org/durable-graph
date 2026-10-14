# DB-089：checkout 执行环境寿命与 factory 查询入口收窄

> 状态：2026-10-14，**Implemented / A／B／C 有限范围完成**。protected 查询、准确核心解析与内部隔离通过源码和新 G 普通包门禁；合同与长期文档已收束。具体证据与未覆盖范围见 §11，没有剩余必经分片。
> 编制基线：`c42be6d`，当时生产源码为 DB-088 的 `ca5598b`；A／B 施工起点与结果见 §11。后续施工先核实际 HEAD／工作树，并区分源码接口事实与普通包资格。
> 前置：DB-087 有限自举停点与 DB-088 开发 Schema 删减均已完成。本案保持其保存／恢复／发布合同，只收窄默认 factory 能力并说明生命周期。

## 1. 问题、选择与最小验收

问题：能否保留长期 checkout 的稳定执行环境，同时避免普通 binding factory 把“无关 Schema 是否已登记”用作持久业务值的隐式开关？

选择：`StateBindingContext.TryGetSchema` 从 `public abstract` 改为 `protected abstract`；准确目录查询继续由核心执行。保留既有 factory context、外部派生 context 的低层实现能力、按需闭合及每 S 独立 memo，不增加新 context 接口或 wrapper。

最小可观察结果：

1. 普通非 friend 包消费者通过 `StateBindingContext` 参数直接调用 raw lookup，编译明确拒绝；外部派生 context 仍可合法 override 并在自身方法中调用。
2. 完整 Schema 查验、准确中间版本解析及冲突在业务 callback 前拒绝保持；环境隔离与 DB-081 反例仍可执行。
3. 真实 SG Widget／List 在 Event 增加无关声明后，原 checkout 连续保存、独立读回、重新签出与全部关闭后的冷开续写得到预期持久值、身份、共享与环；实际 Base／Delta 与来源保持。
4. README／公开 XML 清楚区分“重新 Checkout”和“重新 Open”，不让用户以为每次提交后都要刷新工作副本。

这是 API 防误用和行为合同收敛。它不实现 callback 沙箱、不证明任意自定义 reader 的完整可恢复性，也不改变持久 wire 或给旧 DLL 提供兼容转发。

## 2. 已核实事实与证据

| 当前事实 | 源码／已有见证 | 对本案的约束 |
|---|---|---|
| Open 复制注册配置，保留委托／comparer 引用；不急切闭合未使用类型 | [StateModelRegistry](../../../src/DurableGraph.Persistence/StateModelRegistry.cs)、[Repository 接线](../../../src/DurableGraph.Persistence/Repository.MetaBootstrap.cs)、[晚注册回归](../../../tests/DurableGraph.Persistence.Tests/SplitRepositoryEnvironmentTests.cs) | 重新 Checkout 不能更新代码注册；冻结不是复制用户闭包的外部状态 |
| 业务 reader 从实际 Store／S 构造环境；严格 reuse 要同 Store、同冻结配置实例、同 S | [DecodedRevision](../../../src/DurableGraph.Persistence/DecodedRevision.cs) | 不用 latest shared、外部 view 或相等地址替代准确环境 |
| State 保存可推进 baseline 的 SelectedSchemaView，SourceModels 仍是原执行 snapshot | [NormalizedRevision](../../../src/DurableGraph.Persistence/NormalizedGraphState.cs)、[checkout 环境回归](../../../tests/DurableGraph.Persistence.Tests/SplitRepositoryEnvironmentTests.cs) | 持久 selected S 和 execution S 不要求相等；不能为修饰符迁移换掉恢复／比较来源 |
| 相同 stored 值 7，仅改变 S 中无关 Extra 声明，手写 Normalize 可恢复为 7／107 | [SplitModelEnvironmentTests](../../../tests/DurableGraph.Persistence.Tests/SplitModelEnvironmentTests.cs) | 分别覆盖工厂捕获查询与转换时延迟查询；跨 S 复用旧 binding 会误释 |
| 相同 current DTO，仅 Hydrate 闭包不同也能得到 7／107 | [SplitRepositoryPairTests](../../../tests/DurableGraph.Persistence.Tests/SplitRepositoryPairTests.cs)、[History 回归](../../../tests/DurableGraph.Persistence.Tests/SplitRepositoryHistoryTests.cs) | 不是只有 Upgrade 会敏感；Pair 不能因 DTO 相同就跨环境共享 |
| 生产 raw lookup 的调用目前只有完整依赖查验与多跳 Upgrade 中间布局选择两处 | [StateBindingContext](../../../src/DurableGraph/Runtime/Binding/StateBindingContext.cs)、[Upgrade planner](../../../src/DurableGraph/Runtime/Binding/StateBindingContext.Upgrade.cs) | 两处都保留，不将公开入口收窄变成解析能力删减 |
| 普通 SG 调用标准 Normalize；业务 UpgradeContext 无任意目录查询入口 | [GenericProjection](../../../src/DurableGraph.Generator/DurableGraphGenerator.GenericProjection.cs)、[UpgradeContext](../../../src/DurableGraph/UpgradeContext.cs) | 尚未发现正常 SG 因无关目录增长改变领域值的反例；不据此否认准确目录解析依赖 |
| Capture／Commit 成功仍不证明未执行的 reader body 依赖检查 | [DB081HotAdmissionProofTests](../../../tests/DurableGraph.Persistence.Tests/DB081HotAdmissionProofTests.cs) | 不宣称本片解决 Commit 成功后冷恢复可能拒绝的既有自定义 reader 边界 |

前一审阅轮用已有 Release 输出运行了环境、Pair／History 相关 13 项测试，全部通过；这不是本案实施后的验收。本方案编制没有重新构建、运行这些测试或普通包故事。

## 3. 使用者合同与生命周期

### 3.1 两层冻结与持续保存

Repository 的代码配置在 Open 固定；每次实际读取按所选图的准确 S 闭合执行环境。checkout 保持起始执行环境，闭合 memo 可以按需增长；“固定”不表示预先闭合所有类型。

V2 程序签出 V1 数据时，已执行 V1→V2 Upgrade 并得到当前模型的内存对象。起始 S 缺少 V2 持久声明不妨碍由编译定义、保留模板及显式 Upgrade 推导准确目标布局。随后保存 V2、连续增量保存是正常用法，不需要每笔提交后重新签出。

| 操作／历史 | 执行环境与业务基线 | 用户应如何理解 |
|---|---|---|
| Shared／Private 有最近 State | execution 从该 State.S 恢复，业务基线也来自该 State | 新 Head 为 Event 时仍不借 Event.S 改模型或重放业务 |
| Shared 无 State | execution 从创建／恢复时 latest shared 取得；空库可无 S | 是合法空 State 工作区，不借驻留 State 槽补模型 |
| Private 无 State | execution 从实际 Head.S 取得 | 与有 State 时选 State.S 的规则区分；不借 latest shared |
| CommitState | 推进 Head、State 基线及持久 selected S；保原 CLR 实例／新根与 execution | 持续编辑；不自动换已取得的模型或 preparation |
| CommitEvent | 推进 Head、接受 meta；不安装 State 或 Event 身份，不消费 State rewrite | Event 的目录推进不等于 State 执行环境刷新 |
| Discard | 不推进 Head／State／meta 接纳；成功闭合 memo 可以保留 | 放弃候选不要求撤销已完成的代码闭合；领域修改不自动回滚 |
| 独立 ReadState／ReadEvent | 使用被读图自己的准确 S；不用活动 checkout 的环境 | 同 owner、甚至物理缓存已暖，也可能是另一个环境 |
| Dispose 后重新 Checkout | 以当前 Head 的最近 State 重新恢复；可能复用同 State 的合法准备材料 | 重新取得领域实例；不是“追到最新 shared”或重载代码配置 |
| 关闭 Repository 再 Open | 使用下一次注册配置并重新签发地址／资源 | 注册变化可生效，旧句柄和 CLR 引用不会跟随新签出 |

私有 meta 后续写来源仍是实际 Head.S；Shared／ToShared 的目标 meta 写来源仍是 latest shared，完整 ToShared 待导入声明来自 Head.S。这些职责保持 [DB-087 的四种视图合同](../0087-meta-bootstrap-refactoring-route/0087-meta-bootstrap-refactoring-route.md#view-contract)，不统一成一个“当前 Schema”。

### 3.2 晚注册的操作配方

新模型、reader、Upgrade 或策略登记应在 Open 前完成。运行中修改 builder 只影响未来 Open，单独重新 Checkout 不够。

有未提交领域修改时，应用先明确保存当前配置可支持的模型，或自行保留／重建修改；Dispose 不保存、不撤销。随后关闭全部 checkout 和 Repository，用更新后的 registry 重新 Open，重新解析 tag／head 并签出，取得新的领域实例。Transient 初始化放在完整恢复交付之后。

这不是动态插件热切换协议；不增加 Refresh、按操作选模型或自动重签出。

## 4. factory 与 callback 的目标行为合同

在相同持久输入 DTO（及 ObjectId 等明确调用输入）、同一代码配置、明确源／目标完整布局、相邻转换及显式依赖成立的前提下，成功的持久字段转换／恢复结果应稳定，不因无关目录条目的登记、memo 命中或调用次数而变化。

- factory 使用定义、布局、静态类型与既有解析服务闭合能力；不把无关 catalog key 的存在当领域开关。
- Capture 提供 canonical current DTO；同一布局的 Capture／恢复正确往返持久字段及引用关系。历史升级的预期字段变化由明确 Upgrade 定义，不要求与旧领域值恒等。
- reader、引用遍历、Normalize、状态比较与 comparer 保持各自稳定性义务；不修改输入 DTO，不把比较／闭合次数纳入业务结果。
- Allocate／Hydrate 只创建、填充本次恢复图；独立 mutable 实例、共享与环、ID 导入仍由既有核心验证。应用初始化／副作用和视图专属 Transient 放在交付之后。
- 委托或 comparer 捕获的外部可变状态没有被复制／冻结；调用者仍负责语义稳定。

不能仅写“callback 是纯函数”：查询固定目录并按存在性加 100 也可以是纯函数，却违反本合同。该要求收窄了此前允许“由任意固定环境决定结果”的业务写法，不将旧环境敏感反例解释成推荐能力。

准确目录仍可参与依赖解析或使操作拒绝：已有同 key 定义必须完整相等；多跳 Upgrade 可以用 selected S 中准确的中间布局解歧义。目录缺失／冲突、缺少能力及 reader 的必要验证失败不被“结果稳定”合同抹去。

## 5. 产品接口改动与保留边界

当前源码签名（B 已实施；C 已取得新 G 普通包资格）：

```csharp
// Runtime / StateBindingContext
protected abstract bool TryGetSchema(TypeExpr type, int version,
    out DurableSchema? schema);

// Persistence / internal sealed StateModelSnapshot
protected override bool TryGetSchema(TypeExpr type, int version,
    out DurableSchema? schema);
```

只调整访问范围。保 `StateModelSnapshot` 的 sealed、schemaLookup 绑定、现有 closure／Upgrade memo 与签发规则。嵌套 `ExactSchemaRequirementSet.Validate` 可以继续访问 enclosing type 的 protected 成员，跨 Runtime／Persistence 的 protected override 不需要 `protected internal`。

保留两处核心调用及其顺序：

1. 完整 Base／inline requirement set 查验：已存定义不同则拒绝；缺失仍可由代码／模板提供。缓存命中也继续复核，不只核两端。
2. 多步 Upgrade 中间版本：保 closed owner 明确选择、最终 current 端点、准确目录中间定义、缺失时 DTO／prior 推导的现有顺序。通用／closed provider 的优先级、KeepExact、值工具预绑定与错完整槽拒绝不变。

普通 factory 的 base-typed 参数不再能直接 raw lookup；仍可调用 BindSchema／ResolveReader 等必要解析服务。外部派生 context 可以 override，并在自身方法中调用 protected lookup，也可刻意另行暴露查询。这是减少默认能力与误用，不是安全隔离或禁止一切旁路。

不加入 obsolete public 转发、public alias、internal abstract 限制、查询追踪、epoch／token、策略平台或第二套 factory context。无旧 API 兼容负担，但实际普通包编译资格必须重新取得。

准确保存来源、append-only 完整帧、两种 Parent、共同 writer／owner、refs 原子发布、故障寿命、旧帧保持均不改变。State selected S 推进不能换掉 execution SourceModels；Event／Discard 不推进 State 或消费 rewrite。ReadPair 仍需相同 BindingEnvironment 才能进入共享证明，不因新合同跨 S 合并。DB-080 槽保持冷准备资格，DB-081 热准入保持未接线，DB-082 默认关闭。

## 6. 测试迁移与必要增量

### 6.1 迁移查询与保全反例

| 范围 | 最小迁移 | 必须保留的证据 |
|---|---|---|
| Runtime 的 11 个 TestContext override | 将对应 override 改 protected；实施时重新搜索全部调用，不锁死历史计数 | Array／List／Dictionary／Nullable／泛型／值规则／base projection／requirements 的实际解析、拒绝和 callback 顺序 |
| SplitModelEnvironmentTests 的普通 presence／完整值断言 | 查 source.SelectedSchemaView.Schemas | 实际读取签发的 S；目标尚未登记仍能由代码派生 |
| SplitRepositoryEnvironmentTests 的普通 presence 断言 | 查 checkout.BindingEnvironment.SelectedSchemaView.Schemas | execution 起始 S 跨 State／Event／Discard 保持；不能改查 Catalog.Accepted 或 latest shared |
| 四组故意敏感 callback：ModelEnvironment、RepositoryEnvironment、History、Pair | 在 Persistence.Tests 共用一个窄非公开查询探针，调用实际 context 的 protected lookup | 原 Store／S 归属、工厂捕获／延迟查询、7／107 值反例、错误 reuse 拒绝、Pair／History 隔离 |

最后一组优先使用仅 test 程序集内的窄 Reflection helper：按确切方法签名找到 base 声明的 protected lookup，在真实 snapshot 上虚派发；方法选择与调用集中一次。受 protected 访问限制时使用集中方法名字符串，不要求普通测试类的 nameof 表达式可访问；传播失败时保原底层异常，不以 TargetInvocationException 改写既有拒绝断言。标注为具有额外访问权的反例探针，只检验内部隔离，不授普通公开 factory 查询权限。

`StateModelSnapshot` 已 sealed，不能通过新建测试派生类假装收到的实际 snapshot 是该派生类。不新增生产 test-only query helper，不取消 sealed，不用 wrapper／synthetic view 替换真实环境；不能把 API 改成 protected internal 来保旧测试。一般断言直接用已有 view，反射只用于刻意反例，不发展诊断框架。

原 7／107 反例继续说明支持低层自定义 context／异常 callback 时为什么需要隔离，不作为新业务合同的合规用例。保 DB-081 成功保存／首次读回拒绝和 historical-only 依赖退出的回归，不删除“看似不符合新合同”的防错见证。

### 6.2 普通 SG 的正向故事

复用 [SplitRepositoryReadCacheTests](../../../tests/DurableGraph.Persistence.Tests/SplitRepositoryReadCacheTests.cs) 的 Widget、共享 List 和环：Event 接纳无关 Extra，原 checkout 保存新 State 并继续增量，冷开恢复／续写已具 cache off／on 证据。A 已在原场景补齐同一 owner 的热 State→独立 ReadState→Dispose 后再签出比较，不另复制完整场景；运行结果见 §11。

验收持久字段、root／List 的原 ObjectId、共享／环、独立可变实例、实际内容 source／prior／H、升级 Base 与后续 Delta；不要求跨恢复 CLR 实例相同、Transient 值相同或 factory 调用次数固定。V1→V2 正向沿用已有真实 SG Upgrade 故事，不让计数型手写 NextRoot 代替普通业务模型。

### 6.3 中间布局与公开能力

[ValueUpgradeBindingTests](../../../tests/DurableGraph.Tests/ValueUpgradeBindingTests.cs) 已有 registered-middle 一般正控；[GenericUpgradeBindingTests](../../../tests/DurableGraph.Tests/GenericUpgradeBindingTests.cs) 已有 phantom 缺失／closed owner 解歧义／同 key 冲突拒绝。A 补入 phantom 首边仅靠匹配的已登记中间布局闭合的正控，第二边保原 closed provider；运行结果见 §11。

复用既有 Phantom 模板／provider，在两个独立 context 下比较：无 V2、无 closed 首边时先于任何 callback 拒绝；另一 context 只新增 Phantom<Point> V2 的 Point V1 槽，执行相同 V1→V3，预期 Point2(0)、两次 callback、第一 Target／第二 Source 等于准确登记 V2、ObjectId 保持。此为 Runtime 解析见证，不授所有 SG／持久组合资格。

公开编译边界须在非 friend 普通包消费者下验证：base-typed 参数直接 raw lookup 明确出现该调用位置的 CS0122；外部派生 context 的 protected override／自身调用和正常 SG factory 编译成功。不能只反射检查方法修饰符，不能把任何非零 exit 都视为正确拒绝。

## 7. 施工顺序、分工与停点

| 分片 | 可并行边界 | 主线验收／停点 |
|---|---|---|
| A：合同与正控 | 文档／公开 XML；既有 SG 正向回归和 phantom 缺项各有文件所有权 | 已完成源码门禁；运行验收及资格边界见 §11 |
| B：接口删减与测试迁移 | 主线持生产两处签名；Runtime fixture 与 Persistence 探针／反例分包 | 已完成接口删减、引用迁移和完整源码门禁；无 public 替代查询或来源换线，限定结果见 §11 |
| C：新 G 普通包与文档收束 | 复用现有包 helper／runner；独立审阅具体 receipt、实际程序集和源码来源 | 已完成新 G 编译能力门禁、Split 故事与文档收束；限定结果见 §11 |

A／B／C 已完成。复验时按 §8.1／8.2 显式切换并 restore 对应模式；pack 使用 package mode，不能复用 source 输出。主线已独立检查 diff、接口元数据／编译诊断、关键反例与 receipt；subagent 成功报告不代替验收。后续仅由真实消费者反馈触发[路线图 §4.3](../../DurableGraph-research-roadmap.md#43-checkout-执行环境与-factory-查询入口)的条件延期，不预设更多施工平台。

若迁移必须更换执行 S、放开公开查询、删掉中间解析或新增保存读回步骤，已超出本片合同，应停止该扩张并重新审议。测试文件／脚本的具体落点可自主调整，无需为常规施工选择重新请示。

## 8. 构建与普通包验收

### 8.1 源码门禁

按 [依赖指南](../../storage-dependency.md) 显式 restore source mode，核 StorageSourceRoot 为 pin 的完整 revision；不复用上次 package-mode assets 作为 source 证据。

```powershell
dotnet restore DurableGraph.slnx --disable-parallel -p:UseStorageSources=true "-p:StorageSourceRoot=$storage"
dotnet build DurableGraph.slnx -c Release --no-restore -m:1 -nr:false -p:UseStorageSources=true "-p:StorageSourceRoot=$storage"
dotnet test DurableGraph.slnx -c Release --no-build --no-restore -m:1 -nr:false -p:UseStorageSources=true "-p:StorageSourceRoot=$storage"
```

`$storage` 须先按指南取得并核验。整合至少执行完整 solution build／tests；排错可先跑相关 Runtime／Persistence 类。记录实际 counts／诊断，不预填沿用 DB-088 的数字。扫描 src／tests／活动消费者中的直接 query 与 override；历史归档、暂停 lanes 不作为恢复兼容路径的理由。

### 8.2 新 G 门禁

以准确 S 六包 feed、独立 G 版本，复用 [PackageProbeSupport](../../../experiments/PackageConsumerProbe/PackageProbeSupport.ps1) 与现有 pack provenance。pack 必须 package mode；四个 G＋六个 S 的完整闭合、生产输入来源、实际 package／loaded DLL 及隔离配置按现有流程核验。旧 DB-088 receipt 保历史含义，不回填新 API 资格，不要求 storage 重新发布。

最低执行 [Split 普通包故事](../../../experiments/PackageConsumerProbe/SplitRepositoryConsumer/README.md)，在其既有 continue 阶段的 PrivateUnused Event→List 202 State 成功提交之后、资源关闭之前补：

1. 热 State 仍是原实例，值／root ID／共享／环正确；独立 ReadState 值相同且 mutable 实例独立。
2. 显式 Dispose 当前 checkout，在同 Repository 再 CheckoutPrivate 同一 branch，核同 State 地址、root ID、202 与图关系；不额外提交，保 runner 的 wire／prior／H 与阶段预期。
3. 复用后续全关 Open、ToShared／Shared 续写与独立只读 verify，证明冷 owner 和真实增量能力。

source 层承担完整 ID／source／H、缓存 off／on；普通包承担真实 SG／MSBuild、公开接线、各阶段全关恢复及领域值观察。不为本片新增通用 wire inspector，也不把有限故事推广为所有容器组合。

在同一个新 G feed 下，复用现有隔离普通 PackageReference 项目加入临时编译片段：

```csharp
static bool Probe(StateBindingContext context, TypeExpr type) =>
    context.TryGetSchema(type, 1, out _);
```

确认源文件实际进入 Compile、失败含指向该调用的 CS0122。移除／排除片段后正常 Rebuild 成功；外部派生 context 的 protected override／自身调用在同等非 friend 编译环境中通过。不运行失败产物，不改变现有 Development 的 DG0015 专属预期；记录明确的 ExpectedRejection 与成功阶段。

临时编译片段在忽略的隔离任务目录，不污染模型 history 或正式消费者源文件。可在现有 runner 增加窄步骤，复用 receipt／输入来源／超时／退出记录，不建设通用负例框架。禁止 ProjectReference、手工 Analyzer／Import 或测试 friend 身份代替普通包接入。

Development 可作为额外防回归故事复用，但不是再建一份生命周期故事的前置；19 类已退出执行的 lanes 不因此恢复。没有新证据要求额外广测时，完成上述门禁即收束。

## 9. 保留的限制与条件延期

- protected 不阻止外部派生实现另行公开查询、反射或闭包读取全局状态；不承诺自动检测所有违反行为合同的 callback。
- 准确 S 的中间布局解析能力明确保留；是否未来强制所有中间布局由代码／closed provider 预先给全是另一项能力选择，不能在本片隐式实施。
- DB-081 表明 Commit 并非任意 reader 的完整执行证明。无新的消费者／证据，不增加每次保存读回、通用依赖声明或热准备证书平台。
- [DB081HistoricalCertificateTests](../../../tests/DurableGraph.Persistence.Tests/DB081HistoricalCertificateTests.cs) 的历史依赖退出保持：写成 current State 后，不永久挂旧 Normalize／Upgrade 的历史证书。
- 不加入动态插件热切换、按操作选模型、跨 S 自动共享／刷新、回调沙箱、全局深冻结，也不扩大 crash／power loss／性能或全类型资格。

## 10. 完成标准与文档交接

完成须同时具备：接口实际收窄且无产品提供的 public raw-query 替代入口；内部来源／解析／隔离／DB-081 回归保全；普通 SG 正向及 phantom 缺项通过；solution 构建／测试和新 G 编译／运行门禁通过；公开使用合同同步。

实施更新 README、BranchCheckout／Repository 与 StateBindingContext／StateDefinitionBinding 的 XML，以及 glossary 的环境入口。长期约束在完成后归入目标设计；PROJECT-STATE 维护实施焦点／能力，路线图只保未完成项与触发条件，设计索引准确标注本案范围。

本文是本片唯一详细施工合同；结果证据在完成时用一节紧凑记录链接工件，不追加逐轮过程。已有 DB-087／088 保其完成含义，若当前契约引用需要校准则只补后继链接／准确说明，不改写冻结归档或旧包结果。

当前无需要用户再选的实质产品语义问题：protected、准确核心查询与稳定 checkout 已通过 B 源码和 C 普通包门禁。C 没有否定最小路线或要求产品扩张；后续保持 §7 的范围边界和 §9 的条件延期。

## 11. 分片结果与续工入口

### A：合同与正控

A 施工起点 `4def447`，完成合同与正控。README、术语表和四处公开 XML 明确稳定持久结果、准确解析仍可拒绝、持续保存与重新 Checkout／Open 的区别；有 State 和无 State 的视图选择分别说明。A 停点的四个生产文件只有 XML 注释变化，当时 raw lookup 仍为 public，B／C 未执行。

- Runtime 新增 `RegisteredPhantomMiddleClosesGenericFirstEdgeWithoutAnExplicitClosedProvider`：两个独立 context 只有准确 V2 登记不同，覆盖缺失在 callback 前拒绝与登记后 Point2(0)、两次 callback、准确相邻布局和 ObjectId。仅证明 Runtime 解析能力。
- Persistence 复用原 cache off／on Widget／List 场景，补热 State 独立读回和同 owner Dispose／再签出，核 mutable 隔离、root／List ID、State 地址、执行 models／SourceModels 保持、持久 S 推进、真实 source／prior／H，保提交序列及全关冷开续写。空库创建的原 execution environment 为 null，Same 断言证明保持该初始形状；不把它扩大为非空 S0 冻结资格。
- V1→V2 的升级 Base、完整身份和后续 List Delta 复用 `GeneratedDevelopmentForksKeepUserIdentitiesAndPublishOnlyCleanWinningVersion` 与 `GeneratedPrivateUpgradeForksCompleteBaselineViewAndColdEventStateContinuesWithoutLatestFallback`；不在同版 Widget 场景另造升级模型。DB-081／环境隔离／Pair 的既有反例保持。

主线核准确上游 `974fd47abb73792f9d350af87ab91a53848952eb`、工作树干净后显式 restore source mode；DG Release solution build 零警告／错误，完整四项目测试 **2736 通过、0 失败、0 跳过**（Persistence 893、Serialization 133、Storage 36、Runtime／SG 1674）。TRX 核新增 phantom、两种 cache 模式、上述真实 SG 升版以及既有 DB-081／环境／History／Pair 见证全部通过；四处生产文件去除 XML 注释后与起点完全一致。独立审查修正了无条件“重签出取最近 State”的措辞，没有暴露设计困难。最终注释校正后又通过 solution build，运行代码／测试没有再变化。

工件位于 `.artifacts/db089-a-20261014-114131/`：restore／build／build-final／test logs、四份 TRX 与 `acceptance.json`；后者记录 TRX hash、正控实际名称、反例覆盖及仅 XML 变化核验。本轮未 pack，不授新 API 的普通包资格。

A 的 source 证据不替代 B 的接口元数据／迁移验收或 C 的普通包资格；B 结果见下节。

### B：接口删减与测试迁移

B 施工起点 `97d2a83`。生产仅两处 `TryGetSchema` 声明从 public 收为 protected，另校准一条 Runtime XML；`StateModelSnapshot` 仍 internal sealed，lookup 来源、memo、完整依赖查验与 Upgrade 中间布局选择顺序均未改变。源码扫描没有新增公开替代入口或活动消费者直接查询。

- Runtime 的 11 个 fixture 仅迁移 override 修饰符；既有正控、完整布局冲突与 callback 顺序保持。
- Persistence 普通断言改查原 reader／checkout 签发的准确 Schema view；四组敏感 callback 共用仅测试程序集内的 [SchemaLookupCounterexampleProbe](../../../tests/DurableGraph.Persistence.Tests/SchemaLookupCounterexampleProbe.cs)。探针按准确 base 方法签名反射，对真实 snapshot 虚派发，以 `ExceptionDispatchInfo` 保留底层异常；工厂捕获／延迟查询、7／107、错误 reuse 和 History／Pair 隔离断言没有削弱。

主线再次核准确上游 `974fd47abb73792f9d350af87ab91a53848952eb`、工作树干净并显式 restore source mode。DG Release solution build **零警告／错误**；完整四项目测试 **2736 通过、0 失败、0 跳过**（Persistence 893、Serialization 133、Storage 36、Runtime／SG 1674）。实际 TRX 核 phantom 已登记中间布局、真实 SG Widget／List 的两种 cache 模式、V1→V2 Base／后续 Delta，以及 DB-081／环境／History／Pair 见证均通过。

编译产物元数据实核 Runtime 为 Family／Abstract／Virtual、Persistence 为 Family／Virtual 且类型 Sealed；与起点比对，生产执行代码只改两处访问范围，11 个 Runtime 测试只改 override。独立审查没有发现需要扩张或更改 C 路线的问题。工件位于 `.artifacts/db089-b-20261014-120027/`：restore／build／test logs、四份 TRX、query scan、`metadata.json` 和含文件 hash／逐组覆盖的 `acceptance.json`。

B 停点时 root assets／DLL 仍是 source mode，尚未 pack；元数据和源码回归不代替普通包门禁。后继 C 已按 §8.2 取得这些资格并收束长期文档，结果如下。

### C：新 G 普通包与文档收束

C 施工起点 `dee9008`，生产源码没有再变。先用准确六包 S 的任务配置重新 restore package mode；DG Release solution build **零警告／错误**，完整四项目测试 **2736 通过、0 失败、0 跳过**（Persistence 893、Serialization 133、Storage 36、Runtime／SG 1674）。实际 TRX 保全 phantom、中间布局、cache off／on、真实 SG 升版、DB-081 及环境／History／Pair 见证。

复用现有 helper 受控制作新 G `0.0.0-public-history-e2e.20261014041822.37600`，准确 S 为 `0.1.3-dev.20261011.g974fd47`／`974fd47abb73792f9d350af87ab91a53848952eb`。189 项生产输入与 `dee9008ee3b857fdd7860e5d9bb698e3beee69b4` 完全一致；消费／文档修改另记为工作树状态，未冒充生产修改。四新 G＋六 S 的包字节／nuspec、隔离 cache／assets、SG／Build inputs 与五进程 **50 份实际加载 DLL** 联核通过；未远程发布。

- 同一非 friend PackageReference 项目显式 Compile 临时片段，真实 csc 在 `RawSchemaQueryRejection.cs(6,17)` 仅报告 **CS0122**；该步明确记录 ExpectedRejection。外部派生 context 的 protected override／自身调用成功，随后精确恢复原项目并 Rebuild 成功再启动消费者。共六次成功 Rebuild、一次精确预期拒绝，history 1→3 且原文件保持；失败产物没有执行。
- Split 的 seed／upgrade／rewrite／continue／verify 五个独立 PID 全部成功。在既有 List202 State 后核原实例、root ID／共享 List／环，独立 ReadState 与同 owner Dispose／重新 CheckoutPrivate 得到同值和独立 mutable 图、相同 State 地址；没有增加提交。原真实 Base／Delta、完整 IDs、source／prior／H、refs、准确 Schema、完成帧及全关冷开续写保持，两只读阶段文件集合／SHA 不变。
- 实际包 DLL 的 PE 元数据再次核 Runtime Family／Abstract／Virtual，Persistence Family／Virtual 且类型 Sealed。源码、编译诊断、包资格分别验收；protected 仍允许低层外部派生，不构成 callback 沙箱或任意 reader 可恢复证明。

普通包工件位于 `W:/TEMP/dg-db089-c-20261014-041310-11152/`，含完整 feed、source provenance、`result.json`、query 源／项目快照、编译日志、history、witness 和逐进程 evidence；receipt SHA256 为 `99C02824FE24E1200FE165BE2CD177882D3981A57C0D5E9167B1BC5940812F6E`。主线工件 `.artifacts/db089-c-20261014-041310/` 保 restore／build／test logs、四份 TRX、`metadata.json` 及含实际文件 hash／见证核验的 `acceptance.json`。

主线及独立反审没有发现设计困难或后续接线缺口。长期合同归入[目标设计](../../DurableGraph-target-design-v0.md#模型执行环境与-factory-行为)，公开 README／XML、术语表、PROJECT-STATE、路线图及索引已对齐。全案在 §9 的有限边界内完成，不自动进入 Refresh、保存读回、热准入、跨 S 共享或额外包 lanes；当前 root assets／DLL 为 package mode，下一次 source 联调须重新显式 restore。
