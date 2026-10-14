# DB-034：领域引用图与首次保存的连续施工计划

> 文档整理：2026-10-14；来源为 Git `8ef9fca` 中的 `docs/design-branches/0034-durable-reference-graph-batch.md`，正文保留当片合同与历史验收含义。
> 阅读范围：非泛型 class 共享/循环引用、PrepareNew 和不可达图退出的当片证据见 §8；原显式 WorldId/宿主 Append 路径为历史接缝，后续发布与存储合同从 DB-036、DB-087 进入。
> 当前能力与施工焦点见 [PROJECT-STATE](../../../src/PROJECT-STATE.md)；旧稿中的“下一步”不自动成为当前工作单。

状态：**Chosen / Implemented，G0–G6 已验收**；2026-09-07。
实现基线 `e529f9f`，产品基线 `cab7b5c`（DB-033）；实施证据集中在 §8。
当前事实：[PROJECT-STATE](../../../src/PROJECT-STATE.md)；长期约束：[目标设计](../../DurableGraph-target-design-v0.md)。

## 1. 本批要得到什么

把现有“一个 World 加 string 对象列表”扩成普通非泛型领域类的引用图：

```text
用户 new World，构造共享引用、自环或互环
  → 公开 PrepareNew：递归捕获冻结 DTO，生成无 Parent 的保存计划
  → 宿主 Append
  → 关闭重开，显式地址 + WorldId 加载
  → 完整 stored DTO 校验 → 单对象 Upgrade → 完整 current DTO 校验
  → World 可达闭包 → 先统一分配、再填充引用 → 交付领域图
  → 修改子对象或引用关系 → fixed-Parent Prepare → 宿主 Append → 再 Load
```

核心验收：World.Child 身份未变、只有 Child 内容变化时，不产生 World 的业务 Delta；
共享引用和 readonly 循环恢复为同一实例；断开最后一条根路径后，整个不可达子图退出新 Revision，
旧 Revision 仍可读。升级对象继续强制 Base，原有 string/Empty 和失败不推进基线规则保持。

这仍不是 Repository Commit。WorldId 显式交给宿主保存，追加后重新 Load 建立新基线；
不加入持久根目录、Ref、发布屏障或发布不确定结果协议。

## 2. 为什么选这一批

| 候选 | 当前能看清的部分 | 选择 |
|---|---|---|
| 非泛型 durable class 引用图 | DTO 引用槽继续 UInt32，独立对象 Delta 与 Upgrade 已有；缺 nominal Schema、对象登记队列和引用恢复协调 | 推荐主批，直接验证领域世界的核心模型 |
| 新建 World 首次准备 | 现有无 Parent planner 是 internal，缺普通调用方入口；可复用完整 Capture/Schema 注册/策略 | 纳入最小配套，避免新图只能靠手写 Frame 初始化 |
| 自定义 struct | 嵌套布局、exact inline 依赖及 owner 升版方向明确，但当前 Schema/history 不表达这类依赖 | 独立后片；没有必须先于引用图的依赖 |
| 泛型、数组、BCL | SG 开放模板与运行时闭合已有技术证据；仍缺结构化类型表达、实例 shape 和内容重建合同 | 引用图之后分别切片，不据局部见证宣称整类问题已解 |
| 完整 Repository/Commit | 单一权威、固定 Parent、基线安装原则明确；具体根存储、屏障和故障裁决尚未冻结 | 单独设计，不能当作 Append 的薄包装 |

“看清”指核心状态模型、依赖顺序和反例已经能明确写成验收，不是承诺具体工时或没有集成错误。
本批会修改 Schema 字段的持久表达，必须先通过 G0 格式合同，再并行施工。

## 3. 范围与不变量

承接用户已选目标：统一引用身份、冻结 Versioned DTO、stable nominal 引用约束、exact Base 类型、
单对象 Upgrade、单 World、无构造器/readonly Restore，以及 DB-033 的 source/current 和 Parent 边界。

本批支持：

- 现有 13 标量/string；新增指向带 DurableType 的非泛型 class 字段，包括自引用、互引用、null。
- 同编译顶层 partial class 继承链，字段可声明为已标记的抽象或具体基类，实际非空目标可以是已登记派生类。
  根仍要求所请求的 exact CLR 类型；可达实际对象必须 concrete 且有完整生成能力。
- private、基类字段、readonly 引用字段；共享/循环遵循 ReferenceEqualityComparer。
- 引用目标自身升版不传播 owner 版本；改变字段的 nominal 类型约束则是 owner Schema 变化。
- Upgrade 只改本 DTO 值和已有 ID 槽，不查询其他对象、不创造对象/ID；最终引用必须合法。
  null 用 ID 0，Nullable 注解不在本批变成新的持久非空约束。

明确不包含：struct、泛型、数组/BCL、新标量、interface/object/裸 DurableBase 通配引用、
跨程序集模型继承/发现、boxed value、Transient hook、根替换/清空、多根产品 API、ID 回收池、
Artifact/Derived、Commit/Ref/发布恢复、缓存或 pooling。当前底层数组见证和 legacy Snapshot 不扩展为这些能力。

## 4. 初始目标设计

### 4.1 Schema 只多表达真正需要的 nominal 引用

推荐扩展现有 DurableFieldInfo：DurableReference tag 携带一个非空稳定目标 SchemaId；
标量/string 保持原表达。引用字段不持有目标 DurableSchema，不带目标 Version，不访问目标 Schema 静态初始化器。
Node.Next:Node 与 A↔B 因而不会形成 exact Schema 构造环。Schema 注册的 exact 依赖闭包仍只沿 BaseSchema。
null 引用不要求仅为其 nominal 名称登记一个可执行对象 codec；真正出现的对象必须有 exact reader/model。

初始编码建议（G0 应以独立 golden 冻结）：

- 新增 TypeTag.DurableReference = 15；保留已有 1–14 的数值与编码。
- SchemaBatch：fieldId、tag15、nominal SchemaId；字符串使用现有 SchemaKeyWireCodec 的
  WriteString/ReadString 规则，但不写 version。保留 SchemaBatch header/version1，扩充字段词汇；
  旧 reader 对未知 tag 拒绝。标量/string 的 binary bytes 完全不变。
- history/manifest：旧字段保持 `// field:<id>|<tag>`；引用字段为
  `// field:<id>|15|<canonical-base64-utf8-schema-id>`。复用现有 SchemaId 文本规则，保持原 headers。
  旧严格二列 parser 会拒绝新增引用条目，不能忽略第三列；现有合法文本及 content hash 不被改写。
- 新 parser 对 tag15 强制一份 operand，对其他 tag 禁止 operand；拒绝空白 ID、缺失/额外列、
  不规范数字/base64、非法 UTF-8 文本和截断 binary。binary 字符串的合法性沿既有 codec 规则。
- Runtime equality/hash、SG history comparison、Build Publish/Verify、SchemaStore 同 key 冲突
  一并比较 nominal ID。不同目标 family 不能因为同为 UInt32 body 就被判为相同 Schema。

这是显式的 Schema wire/history 语法扩展，不能报告为“未改格式”。不改变 StateRevision v3 或
Base 对象 envelope：已有 exact SchemaKey 足以解释实际 durable 对象；不预建完整 TypeCodec AST。
不新建 v2 双 reader 或迁移工具；若 G0 发现某真实消费者把旧 header 当作完整语法能力协商，
须先给出证据修订此处，而非悄悄更换编码。未发布原型不承担臆想兼容负担，但现有 golden/包历史必须保持有效。

### 4.2 一个对象身份表，一份明确的模型目录

- 模型目录快照同时建立 SchemaId→current model、exact CLR Type→model、SchemaKey→reader。
  两个模型占同一 exact CLR Type 必须明确拒绝；注册失败不留下半份索引。
- Capture 第一次遇到非空 durable 实例时，用 actual CLR Type 在固定目录中找 binding，
  验证声明 nominal 约束，立即取得 ID/登记，再排队 Capture。后续遇到同实例复用 binding/ID，
  仍须确认本次引用槽自己的 nominal 约束后才返回；去重不能跳过逐边类型校验。
- SG 仍强类型访问字段；标量静态调用原语，引用静态写 UInt32。动态目录分派只在对象登记边界，
  不能落到每个 primitive slot。不得按声明基类 codec 截断未知派生状态，也不扫描程序集找实现。
- 根登记与引用成员登记共享内部 intern/队列，但只有根登记增加 root ID；子对象不能借 AddRoot 伪装为根。
  Seal 用可增长的 index-based 队列，每实例只捕获一次，避免长链递归栈和循环不终止。
- 模型单例不 eager 构造其所有引用目标 Model，避免 A/B 注册静态环。整个 Capture 期间领域图须由宿主保持稳定。

### 4.3 引用检查与引用恢复分开

为每版 DTO 生成专用引用槽遍历：只报告 `(ObjectId, string 或 nominal family 约束)`。
它不是读写统一 visitor；字节 body 不改为动态遍历。相同生成遍历供完整引用验证和可达分析使用，
不在 Runtime 另写反射字段知识。委托/接口和现有 StringReadTable 适配的具体形状在 G0 收敛。

加载顺序固定：

1. 沿每对象旧 Base/Delta 完整还原，全 source 的每条引用均按所查 Revision 的 **stored** 目标
   kind/exact Schema ancestry 校验。即使 owner body 来自更早的 Frame，也不能换成该 Frame 的视图。
2. 全 source 逐行 Upgrade 到 current DTO，再按完整 **current** 目标 Schema ancestry 校验。
   保留所有 source membership、stored Schema 摘要、current DTO 及 RequiresRewrite；不在此剪枝。
3. 从显式 WorldId 沿 current DTO 的引用求闭包，使用迭代队列和 visited；string 也是图节点，null 不入队。
4. 对闭包内全部 durable 对象先 Allocate 并登记 ID→实例，再统一 Hydrate。string 使用已解码实例。
   每次分配必须返回非空 exact CLR 类型，不能让两个 durable ID 共用一个分配实例。
   private readonly 字段仍由声明层 UnsafeAccessor 填充；所有验证成功后才交付 World。
   callbacks 不得提前发布实例，框架不承诺撤销任意用户回调的外部副作用。
5. 把恢复的可达 durable 实例身份导入捕获会话。source DTO 及高水位保持完整；source string
   最小 Empty ID 的反向选择仍沿 DB-033，不能预改写原 DTO 槽。下一 Capture 冻结候选成员集合；
   只有它相对 source membership 的差异才决定 Remove。

必须区分的反例：Owner 的槽约束 BaseB，target 旧版 Derived:BaseB 合法；target 升级为 Derived:BaseC。
旧验证不能用 current CLR 拒绝原来合法的记录；current 验证也不能因旧时合法就放行不相容赋值。
owner 需显式升级清空或调整槽，否则加载失败。历史 Schema 判定不要求旧 CLR 祖先仍存在。

**不采用分配全部 source 的简化：** DB-033 已允许孤立 source 行 Normalize 成 abstract current 类型，
其 Allocate 会抛异常，但加载另一个 World 仍可成功。全分配会无端收紧行为。只分配可达集，同时
仍拒绝不可达 source 的坏内容、缺 reader/Upgrade、非法引用。可达 abstract 实际对象则必须失败。

### 4.4 首次保存与后继保存复用现有 planner

新增最窄静态 `PrepareNew<TWorld>(store, schemas, world, models, parameters)`（名称暂定），
用 fresh CaptureSession 得完整闭包，输出现有 PreparedWorldRevision；Parent 固定 null，WorldId 由 Capture 给出。
它不接受任意 DTO/Parent 拼装，不制造空 LoadedWorld、不安装基线；成功/失败都释放临时捕获。
可以注册并持久化 Schema；不追加 State、不执行 State 的持久化屏障或发布。
宿主 Append 后用 plan.WorldId 和返回地址 Load，保留 SchemaStore.RegisterBatch 自身的 flush 语义。

已有 LoadedWorld.Prepare 扩大到可达对象图：将 source membership 的 current DTO 基线与候选成员集合对照；
仍在候选成员集合内的升级行用 BaseOnlyUpdate，其余保持融合 Delta/PrepareBase/固定策略，
source membership − candidate membership 输出 Removes。完整来源预检与失败清理不削弱。
子对象改值不改变 owner 引用 ID；owner 可因既有策略主动重写 Base，但不得产生虚假的字段变化。

fresh 会话内 ID 单调；加载会话从完整 source live max+1 开始，耗尽只阻止新对象登记。
新占用者从 Base 开始，不接退役数字的旧 Delta；不加入全历史高水位扫描或发布后的就地 Accept。

## 5. 连续施工 gates

| Gate | 交付 | 最小直接验收 |
|---|---|---|
| G0 合同与基线 | 重读 PROJECT/源码，根 build/tests；冻结上述字段编码、引用遍历/目录/分配接缝，记录需求归属 | tag15 独立 golden；旧 scalar/string 文本/bytes 不变；body 静态绑定边界和依赖图明确 |
| G1 Schema/history 贯通 | Runtime 元数据、SG 模型/历史输出与比较、Build Publish/Verify、SchemaStore 写读 | self/mutual nominal Schema 无初始化环；引用 target 升版 owner 不变；改 nominal ID 同版本拒绝；旧 history、未知/损坏新字段与冲突 batch 拒绝 |
| G2 SG 引用 DTO 与代码能力 | UInt32 槽、历史 body、refs-only 遍历、current Capture/Hydrate、稳定 binding | private/基类/readonly；相邻 Upgrade 保持 ID；历史 CLR 声明删除仍有完整 DTO/约束；每槽 body 静态直写 |
| G3 图捕获与首次 Prepare | 原子 catalog 快照、增长队列、根/成员登记分离、公开 PrepareNew | 共享、自环/互环/长链只捕获一次；未知实际子类/冲突 catalog 拒绝；new World 真实计划无 Parent，冻结后变图不影响内容 |
| G4 全目录验证与两阶段恢复 | stored/current 各自验证，current 可达闭包，先分配全部闭包再 Hydrate | 双时态 ancestry 反例；readonly 循环保真；未知/错误目标及 late failure 无交付；不可达 abstract current 不分配但坏 orphan 仍失败 |
| G5 加载后增量保存 | 完整恢复身份导入、现有 loaded planner 处理多 durable 对象 | 变 child 不变 owner；替换 child 引用；断开根路径后循环孤岛 Remove；升级删边/强制 Base；Empty alias、耗尽、重入/失败/重复 Prepare 保持 DB-033 规则 |
| G6 文件与包闭环 | 真实新图经公开入口保存/关闭/重开，历史升级续写，两实际包消费者及独立 review | World→Character↔Item、共享目标、已登记派生实例；多 Delta 后 Upgrade；旧 Revision 保留；候选冻结、失败不推进 Parent；完整根回归及包通过 |

G0/G1 涉及同一 Schema/历史模型，先串行整合；冻结接缝后 Runtime 队列、SG typed 生成、StateStore
加载协调及不相交测试可分派 subagent。主代理保留格式裁决、来源语义、集成与最终验收，dotnet 集中串行。
每个 gate 完成记录源码/测试证据；在批准整批之后不为普通命名和文件组织反复请求授权。

最终执行根 build、相关 focused tests、根完整 tests，以及：

```powershell
./experiments/PackageConsumerProbe/Run-Probe.ps1
./experiments/PackageConsumerProbe/Run-StateStoreProbe.ps1
```

包消费者至少一个必须从普通 new World 图经 PrepareNew 进入文件，不能靠 friend-only planner 或手造 DTO
冒充首次保存能力。历史 malformed 内容可继续用独立 raw fixtures；DB-033 835 是旧基线，不是未来执行结果。

## 6. 完成条件与边界变化

完成须同时具备：G0–G6 的产品实现与直接测试、真实新图/历史图冷重开、完整回归与两个包消费者、
独立审查无阻塞，以及 PROJECT/roadmap/本账本同步。不能仅完成 Schema/队列后宣称引用图支持完成。

可自主处理生成 helper/接口名称、容器与委托形状、现有 public prototype API 的必要重构、测试组织、
少量性能 TODO 和 commit 粒度；不新拆程序集，不改固定策略，不把引用遍历扩为通用 visitor 后端。
若证据要求扩大到完整 TypeCodec、发布恢复、跨对象 Upgrade 或另一份基线权威，先记录具体冲突，
暂停依赖该扩张的工作。格式建议若不能保持明确旧输入含义，先修订 G0 合同，不能默默破坏旧 history。

用户已批准以 spec-driven-implementation 连续实施 G0–G6；完成范围仍以上述 gates 为准。

## 7. 最初规划轮的依据（历史记录）

主代理核对 DurableFieldInfo/TypeTag、SchemaBatchWireCodec、SG/Build history、CaptureContext、
StateModelBinding/StateModelRegistry 与 LoadedWorld 的真实限制。三位独立 reviewer 分别审查需求、
最小架构及语义反例。均推荐引用图为主，PrepareNew 为配套；struct 和发布独立后排。
交叉审议撤回“全 source Allocate”以保留 abstract current orphan 的既有行为，改用共享引用遍历求闭包。
格式推荐为新增词汇/条件 operand，理由是旧 parser 已严格拒绝未知 tag/额外列；不臆造双版本迁移需求。
该规划轮没有验证新算法实现，当时新能力仍是 Proposed；其后实施结果见 §8。

草稿经独立复审，无架构阻塞；补清 Schema 注册仍自行持久化、PrepareNew 不负责 State 屏障。
本轮 4 份 Markdown 的 124 个本地链接/锚点、UTF-8/LF 和 Git diff 检查通过；未重跑产品 build/tests。

## 8. 实施合同与验收账本

2026-09-07 开始实施。范围、非目标及成功条件沿 §3–§6，不另建第二份设计。
主代理负责格式/跨模块契约、集成、最终验收；元数据由单一子任务贯通 Runtime/SG/Build/SchemaStore，
其后以冻结字段模型为接缝，分别实现 Runtime 图操作、SG body、StateStore 协调和独立测试。
所有 dotnet 与包脚本由主代理串行执行。

G0 已选接缝：

- `DurableFieldInfo(..., string? targetSchemaId = null)` / `TargetSchemaId`；tag15 必填，其余 tag 禁止。
  Schema/history 编码按 §4.1，不改变旧 1–14 的意义与 bytes。
- `StateReferenceVisitor<TState>(in TState, IStateReferenceVisitor)`；sink 只提供 `VisitString(id)`、
  `VisitDurable(id, nominalSchemaId)`。reader/model 登记统一采用该遍历，替换旧 string-only validator 合同；
  现有测试按相同语义迁移，避免可选遍历漏掉可达边。生成的直接 string 校验 helper 保留。
- `ObjectReadTable(StringReadTable, IReadOnlyDictionary<uint, DurableBase>)` 供 Hydrate；
  `ResolveDurable<T>` 只解析已分配对象。持久引用校验另按 DTO 目录 Schema 进行，不能用 current CLR 代替。
- `CaptureSession.BeginCapture(IEnumerable<StateModelBinding>)` 冻结本次目录；
  `CaptureContext.CaptureDurable(value, nominalSchemaId)` 只在对象登记边界分派。
  无参数 BeginCapture 仍可用于已有根/string 机制；图操作需要显式登记实际模型。
- `LoadedWorld.PrepareNew` 为首次准备入口；Schema 可自行 flush，State 的 Append/屏障/发布仍归宿主。

| Gate / 不变量 | 状态 | 实现归属 | 直接验收证据 |
|---|---|---|---|
| G0 基线及合同 | 已验收 | 主代理 / 本节 | 根 build 零警告/错误；新跑基线 835/835、零跳过；独立 tag15 golden；旧 golden/历史继续通过 |
| G1 nominal 元数据与严格格式 | 已验收 | Runtime、SG history、Build、SchemaBatch | [history tests](../../../tests/DurableGraph.Tests/NominalReferenceSchemaHistoryTests.cs) 14 项、[wire/Store tests](../../../tests/DurableGraph.Persistence.Tests/NominalReferenceSchemaWireTests.cs) 9 项：独立 golden、冲突、损坏/截断、nominal 无 exact 环、目标升版不传播 |
| G2 强类型生成与历史引用槽 | 已验收 | SG BinaryBody / StateModel | [generated body tests](../../../tests/DurableGraph.Tests/GeneratedReferenceBodyTests.cs) 3 项：UInt32 Base/Delta golden、refs-only 遍历、private/readonly 继承循环、历史 CLR 删除；原静态绑定 guards 通过 |
| G3 目录/队列/首次准备 | 已验收 | CaptureSession / CaptureContext / LoadedWorld | [runtime tests](../../../tests/DurableGraph.Tests/RuntimeReferenceGraphTests.cs) 8 项及 [文件 tests](../../../tests/DurableGraph.Tests/PersistedReferenceGraphTests.cs)：12,000 节点队列、共享/循环、逐边约束、目录冻结/冲突、公开 new 图准备与冻结 |
| G4 完整双视图校验/可达分配 | 已验收 | RevisionDecoder / NormalizedRevision / LoadedWorld | [加载 tests](../../../tests/DurableGraph.Persistence.Tests/LoadedReferenceWorldTests.cs) 15 项：双时态 ancestry、坏/abstract orphan、错误目标、exact/unique 分配、late failure；3,000 节点链统一分配后填充 |
| G5 固定 Parent 增量保存 | 已验收 | 原 LoadedRevisionPlanner / 恢复身份导入 | [文件 tests](../../../tests/DurableGraph.Tests/PersistedReferenceGraphTests.cs) 4 项：child-only Delta、引用替换新 Base、循环岛 Remove、两次历史 Delta 后 Upgrade 仅目标强制 Base；原 Empty、耗尽、重入、Append 失败、重复 Prepare 回归通过 |
| G6 冷重开/包/独立审查 | 已验收 | 文件 tests / 包消费者 / 主代理与独立 reviewer | 完整 889/889、两实际包脚本、无阻塞独立审查；实际首次入口不依赖 raw seed |

基线命令：`dotnet build DurableGraph.slnx --verbosity quiet`；
`dotnet test DurableGraph.slnx --no-build --verbosity quiet --logger "trx;LogFilePrefix=db034-baseline"`。
最终命令与执行结果：

- `dotnet build DurableGraph.slnx --verbosity quiet`：零警告、零错误。
- `dotnet test DurableGraph.slnx --no-build --verbosity quiet --logger "trx;LogFilePrefix=db034-verified"`：
  **889/889，零跳过**（Runtime/SG 441、StateStore 190、Storage 155、Serialization 103），比基线新增 54 项。
- `./experiments/PackageConsumerProbe/Run-Probe.ps1`：通过，7 份 history；实际产物
  `experiments/PackageConsumerProbe/obj/run-20260907053621-36380`。
- `./experiments/PackageConsumerProbe/Run-StateStoreProbe.ps1`：通过，history 2 → 3 → 8；实际产物
  `experiments/PackageConsumerProbe/obj/state-store-run-20260907053722-1324-8a91f510`。
  原持久 Schema/历史升级 markers 保留，新增 PrepareNewGraph、SharedDerived、ReadonlyCycles、ChildOnlyDelta、
  UnreachableCycleRemoved、HistoricalGraphPreserved 全为 True。

集成修正与独立审查：

- 根与 Model 生成同一稳定 CaptureDelegate，避免独立 lambda 在严格 binding 检查中不等价。
- 旧测试中占同一 CLR 类型的两个模型族改为两个实际类型，继续验证 late registration 快照和 orphan Upgrade 失败；
  新增三份 registry 索引原子拒绝测试。旧方法清单/reader 登记断言适配 VisitReferences，未知 tag 从 15 改测 16；
  tag15 缺 operand 由新测试直接覆盖。没有放宽静态 body、历史字段或失败边界的检查。
- 独立 reviewer 无未解决阻塞；其建议的两个负例已通过：查询 Revision 删除 target、owner 沿用旧 Frame 时拒绝；
  第三个对象 Capture 失败后 Schema/State 零写，修复后 PrepareNew 成功。晚期 Hydrate 失败不交付对象图。
- Schema wire/history 确实新增 tag15/operand；StateRevision v3、Base envelope、Storage、Serialization 和固定 policy 未改。
  无发布/Commit/持久根、其他类型扩展、ID 回收池或性能框架；未进入下一施工分片。
- 最终集成 diff 检查通过；47 个变更文件均 UTF-8 无 BOM/LF，7 份 Markdown 的 154 个本地链接/锚点有效。
