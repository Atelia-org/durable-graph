# DB-060：跨程序集固定 inline 值与只读历史依赖

> 文档整理：2026-10-14；来源为 Git `8ef9fca` 中的 `docs/design-branches/0060-cross-assembly-inline-history-slice.md`，正文保留当片合同与历史验收含义。
> 阅读范围：固定外部值字段、只读历史导出与 ref/lib 两代包证据见 §9；不复制依赖库 history，外部 durable base 由 DB-061 承接，NU5131 限于见证模型包。
> 当前能力与施工焦点见 [PROJECT-STATE](../../../src/PROJECT-STATE.md)；旧稿中的“下一步”不自动成为当前工作单。

> 状态：已实施，G0–G3 验收通过；用户于 2026-09-10 授权，结果见 §9。
> 日期：2026-09-10；调查基线：`0068694`（DB-059 已实施）。
> 续工入口：[PROJECT-STATE](../../../src/PROJECT-STATE.md)；目标：[目标设计](../../DurableGraph-target-design-v0.md)。

## 1. 问题与最小成功标准

**让独立库中的持久值可以直接成为领域字段，同时继续由定义库保留它的历史 DTO 和执行能力。**

DB-059 后，`RemotePoint[]`、`List<RemotePoint>`、本地泛型值 `LocalBox<RemotePoint>` 已有组合路径，
直接声明 `RemotePoint Position` 或 `RemotePoint? Position` 仍被拒绝。
这个区别来自 SG 对固定 inline 模板的可见范围，不来自值序列化或对象身份规则。

最小成功标准：值库与模型库分别编译、分别维护 history，模型库直接保存外部值；
外部值升版后，owner 显式升版并转换，仍能读取旧 exact DTO、恢复领域图、强制 Base 后继续正常 Delta。
**消费方不复制或重新生成外部 Family，不把依赖库的 `.dgschema` 发布到自己的 history 目录。**

本轮候选比较：

| 候选 | 推荐理由或暂缓理由 |
|---|---|
| 固定外部 inline | 补齐同一个值能放容器、不能直接放字段的实际建模缺口；本片选择 |
| 外部 durable base | 需要跨声明层访问和历史 base 段展开合同；与一个值槽复用投影不同，单独后继 |
| ValueTuple | record 已提供复合值；多 child exact、参数来源与 Upgrade 仍需独立扩展，见 [DB-057 §8](../0057-bcl-scalar-value-slice/README.md#8-valuetuple-后继保留的问题) |
| 新建消费者外观/API | 现有 GraphRepository/GraphSession 已简短，真实包已覆盖完整流程；缺导航不足以证明需要新包装。自然领域示例并入本片验收 |
| SchemaStore 自举、联合视图 | 尚需元数据引导与视图合同；替换日志后端本身不提供回滚/分叉，不作为本片前置 |

## 2. 实施前事实与真正缺口

以下是源码调查，不是新方案已经运行的证明：

- [TryGetInlineValue](../../../src/DurableGraph.Generator/DurableSchemaGenerator.cs) 仍要求同程序集及 source shape；
  [DB-059 负例](../../../tests/DurableGraph.Tests/CrossAssemblyGeneratorTests.cs) 明确拒绝外部 Point、Nullable Point 和固定外部泛型值。
- [ValidateCurrentDependency](../../../src/DurableGraph.Generator/DurableSchemaGenerator.Ancestry.cs) 在本地 `DurableTypeModel` 中寻找全部 inline/base；
  仅去掉第一道检查仍会失败。历史 DAG 检查也要求找到完整固定依赖。
- [MakeGenericLayout / HasDynamicInlineLayout](../../../src/DurableGraph.Generator/DurableSchemaGenerator.GenericState.cs)
  查询 exact 子模板，决定直接使用 `Family.Vn` 还是动态状态参数。Base/Delta/Visit/StateEquals 共用同一生成算法。
- [Family 工厂](../../../src/DurableGraph.Generator/DurableSchemaGenerator.GenericFactories.cs) 已公开 DTO、body 和 Definition；
  [当前投影](../../../src/DurableGraph.Generator/DurableSchemaGenerator.GenericProjection.cs) 已公开值 `__DurableProjection`，
  enum 使用[外置投影](../../../src/DurableGraph.Generator/DurableSchemaGenerator.Enums.cs)。本片不需要另一套序列化执行接口。
- `GenerateGenericStates` 当前把 `available` 同时用于查询模板和生成全部 Family；导入材料后必须分离这两个集合。
- [SG history](../../../src/DurableGraph.Generator/DurableSchemaGenerator.TemplateHistory.cs) 和
  [Build Publish/Verify](../../../src/DurableGraph.Build/SchemaHistoryTool.cs) 目前都从本项目 history 校验 exact 闭包；
  Build 通过[生成的候选 manifest](../../../src/DurableGraph/build/Atelia.DurableGraph.targets)工作，不执行目标程序集。
- Runtime 已能按已登记外部 Definition 绑定完整值槽、历史 reader 和当前 projection；
  但这不证明消费方构建期已拥有外部模板，也不免除缺历史能力时的拒绝。

因此需要补的是 **只读构建依赖材料及其所有权**，随后让已有生成路径消费完整模板视图。
不以当前领域字段反射结果冒充旧模板，不以运行时最终可能成功为理由删去构建期 history 校验。

## 3. 本片支持范围

`Remote...` 来自引用库；`Local...` 属于当前编译。消费方直接命名的外部类型仍须为 DB-059 接受的真实、public、顶层 Durable 声明。
public 外部值的 private 字段可以使用其库内的 internal inline 类型；传递模板及公开 Family DTO/body 可供查询，
该内部领域类型不必变成 public，当前字段访问仍由提供库自己的投影负责。

| 声明形状 | 本片目标 |
|---|---|
| `RemotePoint` 普通/readonly struct；外部 record struct、enum | 直接值字段 |
| `RemotePoint?`、`RemotePair<LocalPoint>`、`RemotePair<T>` | 复用 Nullable 和已有泛型状态参数，不枚举全部闭合组合 |
| 本地 struct/record/泛型值内部固定含外部值 | 沿 inline 规则递归组合，包括作为 Dictionary 复合 Key |
| 外部值内部含 string、durable class、数组/List/Dictionary 引用 | 保存 ObjectId，目标版本仍由目标 Base 决定；允许图共享和循环 |
| 本地 class 继承本地 base，任一声明层含外部值 | 支持；仍按本地 base-first 布局及 FieldId 分段 |
| App → LibraryA → LibraryB 的传递固定 inline 依赖 | 联合读取各定义库的导出材料；A 不重新导出 B 的材料 |
| 本地 class 继承外部 durable base | 保持拒绝，另片设计 |

保留既有 public 可访问性、泛型约束、depth/node/arity、readonly/ref 恢复与 Dictionary 比较边界。
不开放一般 object/interface、boxed identity、nested/ref struct/record class、数组协变或新的 BCL 类型。
不改变单 World、发布、对象 ID、跨对象 Upgrade 和 SchemaStore 一致性政策。

## 4. 方案选择：导入模板，复用已有生成形状

三条候选经过主线程与两路独立审视：

| 路线 | 得失与结论 |
|---|---|
| A：只读导入 exact 模板，保留已知/动态两种状态形状 | 模板查询统一，本地固定字段与外部固定字段采用相同规则；可直接复用公开 DTO/body/projection。推荐 |
| B：仅外部 fixed inline 改成动态参数 | DTO 泛型 arity 会依赖声明当前位于哪个程序集；若将外部性写入 history，又增加与持久含义无关的状态。不采用 |
| C：全部 Family inline 都统一动态参数 | 可删部分名称选择和 `HasDynamicInlineLayout`，但需迁移全部固定 inline DTO/Upgrade 签名，扩大冷路径反射闭合和参数反推；仍不能删去模板及闭包校验。本片不采用 |

已有静态/动态分流主要决定操作类型名称，不是重复实现两套 codec；
C 的局部净删除不足以抵消本轮迁移面。将来若外部 helper 合同产生实质重复，再单独重访统一状态参数。

### 4.1 三类材料及唯一归属

明确区分：

1. **owned accepted**：本项目已接受的 `.dgschema`。
2. **referenced**：引用程序集提供的只读模板，包括其保留版本；当前字段还须核验实际 metadata 类型。
3. **owned candidate**：本项目当前源声明产生的候选。

先校验 `owned accepted + 所需 referenced` 的完整历史闭包，再校验加入 owned candidate 后的当前闭包及同键一致性。
**当前候选不能补本项目 accepted history 缺失的版本。** 某定义仍由本项目当前声明或 history 拥有时，
不能转而用外部材料补它缺失的版本；本地和外部不能同时认领同一定义。

统一查询视图可读上述材料；Family/DTO/Definition/登记聚合器只为 owned 定义生成。
Publish 只写 owned candidate；Verify 只要求本项目拥有的候选已在自己的目录中，同时验证依赖闭包。
引用记录不计入本项目发布数，不复制为本地 `.dgschema`，不经消费方再次导出。

同一个程序集经依赖图多次到达可去重；不同程序集认领同一个被需要的 DefinitionId 则拒绝，
即使模板内容相同也不任选一个，避免两个同名 Family 的 CLR 身份歧义。
程序集来源只用于构建定位及诊断，不进入 SchemaKey、TypeExpr 或持久布局。

### 4.2 导出材料与读取方式

推荐由 Family 生成路径发出框架专用的 assembly metadata attribute，携带版本化模板材料。
属性由 Runtime 定义；SG 通过 Roslyn 的真实 attribute symbol 读取编译引用，不加载/执行目标程序集或静态初始化器。

材料合同分为两部分：

- **模板材料**：按已有 history 记录语法表达自有 inline 定义的当前及保留版本；当前输出可规范化到 v9。
  旧 `.dgschema` 仍先按其原格式验证，再形成模板；不改写其文件、hash 或旧格式能力门槛。
- **生成执行合同版本**：单独版本化，首版覆盖现有公开 Family/Vn/BodyVn、状态参数推导和当前值 projection 形状。
  history v9 不能代替此版本；未知执行合同、缺少被需要的导出或不匹配的公开 helper 明确拒绝。

首片只需导出 inline 模板，不为未来外部 base 提前导出完整 class 段。
即使某个历史 inline CLR 已删除，只要该库仍生成 retained Family，它的旧模板和 DTO/body 仍可导出。
这不新增“完全删除所有当前声明后自动生成整库恢复能力”的承诺。

普通非 Family 值库若无所需材料，诊断应指出启用已有 `DurableGraphGenerateDefinitions=true` 并重建提供库。
消费方发现固定外部 exact 依赖时自动选择已有 Family 路径；不要求用户添加 dummy 泛型或逐字段登记。
只含历史外部 fixed 依赖的消费方也应保留这一选择。普通路径迁入 Family 的 helper 外观变化沿 DB-059 合同，
不承诺原 `__DurableState` 名称不变；已在 Family 路径的本地固定字段 DTO arity 不因本片改变。

导出由同一次已经验证的模板模型产生；不要新增用户手工维护的第二份 Schema 描述。
精确属性/生成文件名称在 G0 固定，需在正式修改之前以最小 metadata 编译见证确认引用程序集也保留该材料。

### 4.3 按需闭包与传递依赖

从 owned accepted/current 的固定 inline 依赖出发，按 exact ID/version 获取外部模板，递归取得它需要的固定 inline 依赖。
读取所选版本的全部字段事实以保持当前模板验证规则；普通 nominal 引用不因此追入目标的 exact 历史。
泛型动态实参继续使用已有 Runtime 绑定和仓库内严格一致性，不能将此片扩成全闭合历史账本。

按需选择材料，避免所有已引用 DLL 中未使用的定义影响本次编译。
可先从引用图索引导出位置，再解析所需记录；被选定义出现多 owner、缺指定版本、错误 kind/arity、循环/超深都拒绝。
编译引用必须能提供全部所需定义库；不从机器缓存、其他目录或“最新包”偷偷寻找补件。
库 A 的导出若依赖库 B，消费者从 B 自身取材料；A 的导出不成为 B history 的维护副本。

只有当完整模板查询视图建立后，才执行现有 `MakeGenericLayout` 等状态形状推导；
不要捕获 `FindHistory` 失败后猜成静态或动态槽，也不要将未找到的 fixed inline 降级为 nominal。

### 4.4 Build Publish/Verify 的同一事实输入

SG 除现有 owned candidate manifest 外，建议生成独立只读 reference manifest，列出本次选择的外部模板及诊断来源。
Build 接受一个可选 reference-manifest 参数，由现有包 targets 自动传入；没有外部依赖的直接工具用法保持可用。
reference manifest 是 `obj` 下本次编译的派生材料，不是新的版本历史目录，不参加 Publish。
仅有本地规则、没有 owned Durable 声明的编译也可需要外部材料；reference 解析和校验须早于
“零候选且无 history 目录”的快速返回，不能让零发布数跳过依赖检查。

SG 与 Build 都应复用已有 history 字段语法/规范化规则及明确的导出合同解释；
不能一边 SG 放行缺依赖、一边期待 CoreCompile 后再失败，也不能只有 SG 校验而允许直接 Build 工具绕过闭包。
Build 先验证 owned accepted 与 referenced，后合入候选，保留既有重复、规范 bytes/文件名与 create-only 发布纪律。
缺/多份/与本次生成不一致的 reference manifest 不得静默视为空依赖；G0 固定两份材料的关联，
例如由 owned manifest 记录本次 reference 内容摘要。该关联只检查生成集合一致性，不是持久身份或发布凭证。
清理后的干净构建必须自足；不承诺检测人为整体替换成另一套自洽旧材料或删除全部历史。

这是新的**构建材料协议**，不是新 State wire。推荐 `.dgschema` 新写仍为 v9，
SCB1 v2、Base v4、Storage v3、数组/List/Dictionary body 格式全部不变。
不得为了新材料通道重开旧 State 数据格式的兼容分支。

## 5. 当前投影、历史解码与升级

当前固定外部值由真实 metadata 声明确定 nominal/version，再由所需导出核对；
静态槽直接调用定义库的公开 DTO/body/projection，动态槽继续由已登记 Definition 闭合静态泛型操作。
不访问外部私有字段，不从消费方重发外部值的 Capture/Hydrate，不增加已知槽的 Type 查表或虚调用。

历史 owner 只依赖其旧模板和外部 retained Family，不重新找旧领域 CLR 类型。
显式登记仍由各库的公开 facade 完成；构建期导入不能自动给予运行时 reader、current factory 或 Upgrade 能力。
缺实际历史能力在原边界拒绝；缓存命中仍复核完整 exact 依赖，不能仅检查 owner key。

`RemotePoint v1 → v2` 改变固定字段的完整槽，必须递增直接 owner 及受影响的本地 inline/base 链版本。
作者显式编写完整 owner DTO 转换，或在自己的规则集中调用已有强类型值工具；不自动猜业务转换。
历史 DTO 参数与结构依然由 exact 模板决定，不能仅凭外部当前版本补齐中间升级布局。

本地 Upgrade 方法可以显式引用外部公开历史 DTO。若使用本地 `DurableValueUpgrade` 规则，
端点校验可查询所需只读外部 inline history；marker/provider 的发现与登记仍只属于当前编译。
不扫描依赖库里的业务规则，不自动选择其 comparer 或 Upgrade 策略。
本地显式规则所需的外部历史端点也须作为材料请求入口，包括已不再出现在当前字段中的旧值版本。

验证例子应包含外部值自身的引用成员：owner 只保存这些 ID，目标升版不传播；
外部值的值字段变化则传播 owner。这两种传播规则不能因分包混在一起。

本片不保证 inline 库任意升版时消费方 DLL 无需重编译：静态 exact 依赖和 public CLR/helper 兼容是实际条件。
DB-059 的 nominal-only 目标升级、App/Host DLL 不变见证必须继续通过。

## 6. 分阶段施工与验收

以下为本片施工顺序与验收合同；实际结果集中在 §9。

| 阶段 | 工作及最小验收 |
|---|---|
| G0：冻结材料合同 | 最小独立编译库导出当前/历史 inline，消费方从 metadata/ref assembly 读取；确认 Family/DTO/body/projection 可调用。固定执行合同版本、reference manifest 语法和错误路径；无跨库继承 |
| G1：只读依赖与 history | owned/reference/candidate 分离；SG 与 Build 联合闭包，own-only Publish/Verify/生成。旧 history 文件 bytes/hash 不变；当前候选不能补 accepted 缺口 |
| G2：字段与生成 | 外部 struct/record/enum、Nullable、泛型和本地嵌套；自动 Family。相同模型放本地或依赖库时，owner Schema/history 与 Base/Delta 字节相同，已有 Family DTO arity 不变 |
| G3：图、升级、包 | 两代真实包和三层依赖、旧 CLR 删除、exact Read→Upgrade→Base→NoChange→Delta；全部负例、根 solution build/tests、相关旧包回归、独立审阅和文档 |

具体见证至少包括：

- 普通 `World.Position`、Nullable、外部泛型值闭合本地类型、本地 inline 嵌外部值、复合 Dictionary Key；
  private/readonly 字段和 enum/record 不因 metadata 路径失效；public 外部值的 private 字段使用 internal inline 类型也贯通。
- 外部值里存共享 string/class/container 引用，Capture 冻结、循环/别名恢复、子对象修改不制造 owner 伪 Delta；
  值字段修改产生嵌套 Delta，恢复后再次 Commit 为 NoChange。
- 同形本地/外部模板推导具有相同 DTO 参数数量与持久 bytes；nominal/phantom 实参不要求无用 exact 材料。
- App → A → B 的 exact 链；各项目 history 文件集合独立，包消费者无手工 AdditionalFiles、Analyzer、Import 或历史拷贝。
- 第二代同时重编译受影响 owner，外部旧值 CLR 类型改名/删除但保留同身份历史；
  冷读旧 exact DTO、显式升级、仍 live 的升级 owner 强制 Base，随后正常 Delta。空 Nullable/空容器的旧依赖也验证。
- 当前字段已删除但 owned accepted 仍有外部 fixed 依赖：自动选择 Family，保留旧 reader；
  本地规则单独请求已不在当前字段中的外部历史端点，包括零 owned 声明的规则库，也完整导入校验。
- 漏升 owner、缺外部旧模板、缺运行时 Definition/历史 factory、错 kind/arity、错误执行合同、伪标记、重复归属、
  exact 循环/超深均拒绝；相应失败不发布新 head，缺升级工具在受影响 owner 首次业务回调前拒绝。
  此处不承诺整图所有对象的全局零回调：此前已处理的其他 owner 可以已经完成自身 Upgrade。
- 删除仍被其他 owned accepted 记录 exact 引用的依赖版本，current candidate 恰好能补它时，
  SG 和直接 Publish/Verify 都拒绝；当前声明所需的旧版本缺口仍由既有 SG history 规则拒绝。
  不能借外部导入补同一个本地拥有定义的缺失版本。不检测已无剩余证据的整段 history 删除。
- reference manifest 缺失/截断/重复记录/错误来源、重新 Clean 后构建、包中的 reference assembly 元数据传递；
  与缺历史工厂分开观察，导出模板不冒充可执行能力。

根 build/tests 及真实包运行由主线程串行，避免 Windows 文件锁；保留未改变机制的旧负例。
相关旧包至少覆盖 CrossAssembly、Generic、ValueUpgrade、Record、CompositeDictionary；是否扩展其余 lane 由实际改动范围决定。
不用仅 ProjectReference 或同一个 Roslyn compilation 的测试代替真实独立包。

## 7. 分工、交付和边界变化

建议实施分工：

- 主线程先固定 G0 材料形状与 owned/reference 查询边界，负责 Runtime 标记、集成、最终命令与验收。
- history/Build agent：导出解析、两阶段闭包与 Publish/Verify，拥有 Build/共享协议/targets 的约定文件。
- SG agent：metadata 字段识别、模板查询、owned-only 生成和 diagnostics；与前者先约定数据形状再并行修改。
- 独立测试 agent：两代图/历史与实际 package consumer；不同时跑根 build，不通过改产品可见性迁就测试。
- 只读 reviewer：重点检查材料所有权、当前候选补历史、旧 CLR 删除及 nominal/exact 版本传播。

最终更新 PROJECT-STATE 的能力与焦点、术语表/目标中的已落实合同、路线图剩余边界及本索引状态。
在 PACKAGE 和本片真实消费者 README 中用简短自然领域例子说明库作者导出、宿主登记、owner 升级责任；
不为示例建立新持久 API 或把核心语义放进空 CLI。

如果 G0 发现公开 helper 无法跨 reference assembly 稳定调用，或者只读模板通道必然要求广泛改变 DTO 形状，
应报告具体反例并重访 §4，不能静默实施全部 inline 动态化、导入外部 base 或放弃历史校验。
规划时仅有源码与审阅依据；G0 的 metadata 见证及后续执行结果见 §9，不以规划审阅代替运行证据。

## 8. 规划审阅结论

主线程与两路设计审阅收敛到 A；另一路只读事实调查定位了 SG 拒绝、历史展开和公开投影的接缝。
两个初始分歧均保留理由：消费者导航缺口不足以单独推动新 API；统一动态状态参数的净删除不足以抵消全局 DTO 迁移。
审阅要求已纳入：独立执行合同版本、按需传递闭包、唯一归属、own-only 发布生成、候选不能修补 accepted 缺口、
旧 CLR 删除后保留历史 body、外部基类继续拒绝。终审另补齐 public 外部值的 internal 实现依赖、
仅历史/仅规则的材料入口，以及历史删除与旧生成文件检测的可观察边界；不引入额外的完整回滚检测账本。
本节不是运行验收记录。

## 9. 施工合同与验收账本

施工基线 `c26c9d8`，工作区干净；根 solution 基线 build 零警告、零错误。
本轮实施 §3–§7，外部 base、全部 inline 动态化和新 State wire 不进入范围。
主线程负责 Runtime 元数据属性、集成验证和文档；SG、Build/shared protocol、独立图测试、真实包分别拥有独立改动范围。

下一波接口：`DurableSchemaExportAttribute(int contractVersion, string schemaId, int schemaVersion, string manifest)`；
执行合同 1，manifest 为单记录的现有 v9 candidate 文本。SG 发出只含自有 inline 的属性。
只读材料使用 `DurableGraphSchemaHistoryReferences.g.cs`，共享协议负责规范排序、解析与 SHA-256；
存在引用材料时 owned manifest 附摘要，Build `Publish/Verify` 接受第三个可选 reference manifest 路径，
包 targets 自动连接。无引用时保持旧 candidate 文本，旧 history/State 格式不变。

| 要求 | 当前状态 | 负责人/落点 | 验收 |
|---|---|---|---|
| G0 metadata/ref assembly 与公开 helper | 已验证 | [导出标记](../../../src/DurableGraph/DurableSchemaExportAttribute.cs)、[SG 导入导出](../../../src/DurableGraph.Generator/DurableSchemaGenerator.SchemaExports.cs) | [SG tests](../../../tests/DurableGraph.Tests/CrossAssemblyInlineGeneratorTests.cs)：真实 metadata-only/ref assembly、public helper 与 internal 实现依赖 |
| G1 严格只读闭包与 own-only 发布 | 已验证 | [共享协议](../../../src/Shared/SchemaHistoryReferenceProtocol.cs)、[Build 依赖](../../../src/DurableGraph.Build/SchemaHistoryTool.References.cs)、包 targets | [Build tests](../../../tests/DurableGraph.Tests/CrossAssemblyInlineBuildTests.cs)：canonical/摘要/归属、零候选、两阶段 history、禁止引用借本地补缺 |
| G2 固定外部槽、历史/规则入口 | 已验证 | SG 字段/模板/生成 | SG tests：普通/record/enum/Nullable/generic、仅历史/仅规则、旧 DTO arity、placement Schema/history/Base/Delta 精确对比 |
| G3 图与两代包 | 已验证 | [图 tests](../../../tests/DurableGraph.Tests/CrossAssemblyInlineGraphTests.cs)、[历史 tests](../../../tests/DurableGraph.Tests/CrossAssemblyInlineHistoryTests.cs)、[真实包](../../../experiments/PackageConsumerProbe/InlineLibraryConsumer/README.md) | 冻结/共享/循环、删除旧 CLR、空值/空 List 的能力预检、Base→NoChange→Delta、三库各自 history 与 ref/lib 资产；五条相关旧包回归通过 |
| 最终集成 | 已验证 | 主线程与独立 reviewer | 根 build 0 warning/error；2406 tests 全过；最终 diff/链接检查与提交 |

G0 最终使用执行合同 1 的单记录 v9 manifest；reference 协议独立版本 1，规范 UTF-8 无 BOM/LF，
按 owner/ID/version 排序，摘要只关联本次派生材料。包生成文件显式使用无 BOM 编码。
不新增运行时绑定机制，不更改旧 State/Schema wire；既有本地 Family DTO/body 算法保持。

观察与修正：

- 外部模板的固定子依赖曾可命中消费方 owned 同 ID；SG 现显式拒绝，Build 先验证引用自身闭包，
  然后联合 accepted。即使同模板也不能合并两个提供者，已有对应生成与直接工具负例。
- 首轮 SG 回归因新增 current/history 预检次序改变了纯本地错误诊断；无外部材料请求现在直接跳过索引，
  有引用时按 accepted/current 两阶段处理。旧诊断回归已在全套测试中通过。
- 两个测试夹具修正了错误的动态 DLL 内部 API 调用及示例 DTO 泛型参数；未放宽产品可见性或改变 DTO 形状。
- List 缺规则负例原误要求整图零回调；现独立计量该 List 的回调，验证明确错误、零 List 回调、head 不变，
  允许先前 World 已完成自己的合法 Upgrade，不扩张为全图预绑定或副作用回滚。

根验证：`dotnet build DurableGraph.slnx --no-restore` 零警告/错误；
`dotnet test DurableGraph.slnx --no-build --no-restore` 全部 **2406** 通过，新增 **69**：
Runtime/SG 1458、StateStore 630、Serialization 163、Storage 155，无失败或跳过。
日志为忽略目录中的 `obj/db060-build-4.log`、`obj/db060-final-tests.log`；更早的失败日志保留用于区分修正过程。

两代新包成功产物位于 `experiments/PackageConsumerProbe/obj/inline-library-20260910101449-29804-81bb3bc9`，
运行日志 `obj/db060-package-inline-2.log`；隔离 runtime feed 版本
`0.0.0-inline-library-e2e.20260910101449-29804-81bb3bc9`。
LibraryB/A/AppModel history 分别从 2/2/1 到 4/3/2，旧 bytes/hash 原样，消费方没有发布外部记录；
独立 reviewer 已复核全部最终历史与 `independent-history.json`，并抽查编译使用 ref DLL、运行使用 lib DLL。
包 marker `InlineLibrarySeed`、`InlineLibraryUpgrade`、`InlineLibraryPackaging` 全部通过。
测试模型包存在已说明的 NU5131（旧式 nuspec references 组）警告；此见证验证 PackageReference，不验证 packages.config。
它不影响已观察的 ref/lib 资产与执行；根 build 的零警告声明不涵盖该包警告。

同一最终 runtime feed 的 CrossAssembly、Generic、ValueUpgrade、Record、CompositeDictionary 五条旧包 lane 全部通过；
日志为 `obj/db060-package-<Lane>.log`。CrossAssembly 仍验证 nominal-only App/Host DLL 与 World history 不变，
不因新导出材料误升级 owner。独立审阅无未处理阻塞项；本片没有遗留待完成的范围内 TODO。
最终检查 10 份修改 Markdown 的 516 个本地链接、49 个锚点，零错误；完整 diff 空白检查通过。
